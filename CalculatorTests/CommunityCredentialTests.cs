using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using MabiCommerceNewLife;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalculatorTests;

[TestClass]
public sealed class CommunityCredentialTests
{
    private static readonly CommunityPrivacy Policy = new("2026-11-09", 30, "indefinite");
    private const string Endpoint = "http://127.0.0.1:8787/";
    private string _directory = null!;
    private string FilePath => Path.Combine(_directory, CommunityCredentialStore.FileName);
    private readonly string _id = Guid.NewGuid().ToString();
    private const string Token = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), "MabiCredentialsTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    private CommunityCredentialStore.Credentials Credentials(string? endpoint = null, string? token = null) =>
        new() { Endpoint = endpoint ?? Endpoint, ConsentVersion = Policy.ConsentVersion,
            InstallationId = _id, Token = token ?? Token };

    private HttpResponseMessage Registration() =>
        Json(new { installationId = _id, token = Token, consentVersion = Policy.ConsentVersion });

    [TestMethod]
    public async Task FirstOptInRegistersOnceAndReconnectUsesSameSavedIdentity()
    {
        var registrations = 0;
        var tickets = 0;
        using var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/installations")
            {
                registrations++;
                return Registration();
            }
            Assert.AreEqual("/v1/scan-tickets", request.RequestUri.AbsolutePath);
            Assert.AreEqual(Token, request.Headers.Authorization!.Parameter);
            tickets++;
            return Ticket();
        });
        using (var first = new CommunityClient(Endpoint, handler, FilePath))
        {
            Assert.IsFalse(File.Exists(FilePath));
            await first.ConnectAsync(Policy, default);
            Assert.IsTrue(first.IsConnected);
            first.Disconnect();
            Assert.IsTrue(File.Exists(FilePath), "OFF must retain the saved identity.");
            await first.ConnectAsync(Policy, default);
        }
        // A new HTTP handler models a new app launch, not merely an in-memory reconnect.
        using var nextHandler = new Handler(request =>
        {
            Assert.AreEqual("/v1/scan-tickets", request.RequestUri!.AbsolutePath);
            Assert.AreEqual(Token, request.Headers.Authorization!.Parameter);
            tickets++;
            return Ticket();
        });
        using var next = new CommunityClient(Endpoint, nextHandler, FilePath);
        Assert.IsFalse(next.IsConnected, "Saved credentials must not enable sharing.");
        await next.ConnectAsync(Policy, default);
        Assert.IsTrue(next.IsConnected);
        Assert.AreEqual(1, registrations);
        Assert.AreEqual(2, tickets);
        var saved = new CommunityCredentialStore(FilePath).Load(Endpoint, Policy.ConsentVersion)!;
        Assert.AreEqual(_id, saved.InstallationId);
        Assert.AreEqual(Token, saved.Token);
        CollectionAssert.AreEqual(new[] { CommunityCredentialStore.FileName },
            Directory.GetFiles(_directory).Select(Path.GetFileName).ToArray());
    }

    [TestMethod]
    public void StoredIdentityNeverContactsNetworkUntilExplicitConnect()
    {
        new CommunityCredentialStore(FilePath).Save(Credentials());
        using var handler = new Handler(_ => throw new AssertFailedException("Unexpected network request"));
        using var client = new CommunityClient(Endpoint, handler, FilePath);
        Assert.IsFalse(client.IsConnected);
        client.Disconnect();
        Assert.IsTrue(File.Exists(FilePath));
    }

    [TestMethod]
    public async Task ScopeMismatchAndCorruptCredentialsFailBeforeSendingAnything()
    {
        var cases = new[]
        {
            JsonSerializer.Serialize(Credentials("http://127.0.0.1:9999/")),
            JsonSerializer.Serialize(new { Endpoint, ConsentVersion = "old", InstallationId = _id, Token }),
            JsonSerializer.Serialize(Credentials(token: "invalid")),
            "null", "{"
        };
        foreach (var text in cases)
        {
            File.WriteAllText(FilePath, text);
            using var handler = new Handler(_ => throw new AssertFailedException("Must not send mismatched credentials"));
            using var client = new CommunityClient(Endpoint, handler, FilePath);
            try
            {
                await client.ConnectAsync(Policy, default);
                Assert.Fail("Invalid credentials must fail explicitly.");
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException) { }
            Assert.IsFalse(client.IsConnected);
            Assert.AreEqual(text, File.ReadAllText(FilePath));
        }
    }

    [TestMethod]
    public async Task RejectedSavedTokenDoesNotReregisterOrEraseIdentity()
    {
        new CommunityCredentialStore(FilePath).Save(Credentials());
        var before = File.ReadAllText(FilePath);
        var calls = 0;
        using var handler = new Handler(request =>
        {
            calls++;
            Assert.AreEqual("/v1/scan-tickets", request.RequestUri!.AbsolutePath);
            return Json(new { error = "unauthorized" }, HttpStatusCode.Unauthorized);
        });
        using var client = new CommunityClient(Endpoint, handler, FilePath);
        var error = await Assert.ThrowsExceptionAsync<CommunityApiException>(() => client.ConnectAsync(Policy, default));
        Assert.AreEqual(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.AreEqual(1, calls);
        Assert.IsFalse(client.IsConnected);
        Assert.AreEqual(before, File.ReadAllText(FilePath));
    }

    [TestMethod]
    public async Task CancelledSavedCredentialCheckNeverConnectsOrReplacesIdentity()
    {
        new CommunityCredentialStore(FilePath).Save(Credentials());
        using var cancellation = new CancellationTokenSource();
        using var handler = new Handler(_ => { cancellation.Cancel(); return Ticket(); });
        using var client = new CommunityClient(Endpoint, handler, FilePath);
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => client.ConnectAsync(Policy, cancellation.Token));
        Assert.IsFalse(client.IsConnected);
        Assert.AreEqual(_id, new CommunityCredentialStore(FilePath).Load(Endpoint, Policy.ConsentVersion)!.InstallationId);
    }

    [TestMethod]
    public async Task InvalidRegistrationOrCanceledFirstRegistrationNeverSaves()
    {
        using (var handler = new Handler(_ => Json(new { installationId = _id, token = "bad",
            consentVersion = Policy.ConsentVersion })))
        using (var client = new CommunityClient(Endpoint, handler, FilePath))
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => client.ConnectAsync(Policy, default));
        Assert.IsFalse(File.Exists(FilePath));
        using var cancellation = new CancellationTokenSource();
        using var canceledHandler = new Handler(_ => { cancellation.Cancel(); return Registration(); });
        using var canceled = new CommunityClient(Endpoint, canceledHandler, FilePath);
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => canceled.ConnectAsync(Policy, cancellation.Token));
        Assert.IsFalse(File.Exists(FilePath));
    }

    [TestMethod]
    public async Task RateLimitedSavedAuthorizationRetriesWithoutRegistration()
    {
        new CommunityCredentialStore(FilePath).Save(Credentials());
        var calls = 0;
        using var handler = new Handler(request =>
        {
            Assert.AreEqual("/v1/scan-tickets", request.RequestUri!.AbsolutePath);
            Assert.AreEqual(Token, request.Headers.Authorization!.Parameter);
            if (++calls == 1)
            {
                var response = Json(new { error = "rate_limited" }, HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
                return response;
            }
            return Ticket();
        });
        using var client = new CommunityClient(Endpoint, handler, FilePath);
        await CommunityRetry.RunAsync(async () =>
        {
            await client.ConnectAsync(Policy, default);
            return true;
        }, false, false, (_, _) => Assert.IsFalse(client.IsConnected), default,
            (delay, _) => { Assert.AreEqual(TimeSpan.FromSeconds(2), delay); return Task.CompletedTask; });
        Assert.AreEqual(2, calls);
        Assert.IsTrue(client.IsConnected);
        Assert.AreEqual(_id, new CommunityCredentialStore(FilePath).Load(Endpoint, Policy.ConsentVersion)!.InstallationId);
    }

    [TestMethod]
    public async Task MalformedSavedAuthorizationResponseNeverConnectsOrRegisters()
    {
        new CommunityCredentialStore(FilePath).Save(Credentials());
        using var handler = new Handler(request =>
        {
            Assert.AreEqual("/v1/scan-tickets", request.RequestUri!.AbsolutePath);
            return Json(new { scanId = Guid.Empty.ToString(), expiresAt = 0 });
        });
        using var client = new CommunityClient(Endpoint, handler, FilePath);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => client.ConnectAsync(Policy, default));
        Assert.IsFalse(client.IsConnected);
    }

    [TestMethod]
    public async Task SaveFailureIsExplicitAndNeverLeavesConnectedCredentials()
    {
        var impossible = Path.Combine(_directory, "missing-directory", CommunityCredentialStore.FileName);
        using var handler = new Handler(_ => Registration());
        using var client = new CommunityClient(Endpoint, handler, impossible);
        await Assert.ThrowsExceptionAsync<DirectoryNotFoundException>(() => client.ConnectAsync(Policy, default));
        Assert.IsFalse(client.IsConnected);
        Assert.IsFalse(File.Exists(impossible));
    }

    [TestMethod]
    public void ConcurrentSaveCannotOverwriteExistingIdentity()
    {
        var store = new CommunityCredentialStore(FilePath);
        store.Save(Credentials());
        var before = File.ReadAllText(FilePath);
        Assert.ThrowsException<IOException>(() => store.Save(Credentials(token: new string('a', 64))));
        Assert.AreEqual(before, File.ReadAllText(FilePath));
        Assert.AreEqual(1, Directory.GetFiles(_directory).Length, "Temporary credentials must be cleaned.");
    }

    private static HttpResponseMessage Ticket() => Json(new { scanId = Guid.NewGuid().ToString(),
        expiresAt = DateTimeOffset.UtcNow.AddSeconds(60).ToUnixTimeMilliseconds() });
    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
