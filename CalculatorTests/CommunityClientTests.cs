using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using MabiCommerceNewLife;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalculatorTests;

[TestClass]
public sealed class CommunityClientTests
{
    private const string Token = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly CommunityPrivacy Policy = new("2026-11-09", 30, "indefinite");

    [TestMethod]
    public async Task CancelledRegistrationNeverLeavesConnectedCredentials()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new StubHandler((_, _) =>
        {
            cancellation.Cancel();
            return Registration();
        });
        using var client = new CommunityClient("http://127.0.0.1:8787", handler);
        try
        {
            await client.ConnectAsync(Policy, cancellation.Token);
            Assert.Fail("Cancelled registration must not succeed.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Assert.IsFalse(client.IsConnected);
        }
    }

    [TestMethod]
    public void EndpointAllowsOnlyFixedProductionHttpsAndLoopbackWithoutRedirectTargetsOrPaths()
    {
        Assert.AreEqual("127.0.0.1", CommunityClient.ValidateEndpoint("http://localhost:8787").Host);
        Assert.AreEqual(CommunityClient.ProductionEndpoint,
            CommunityClient.ValidateEndpoint(CommunityClient.ProductionEndpoint).AbsoluteUri);
        Assert.AreEqual(CommunityClient.ProductionEndpoint,
            CommunityClient.ValidateEndpoint(CommunityClient.ProductionEndpoint.TrimEnd('/')).AbsoluteUri);
        foreach (var endpoint in new[] {
            "http://mabicommerce-community.mabi-projects.workers.dev",
            "https://mabicommerce-community.mabi-projects.workers.dev:444",
            "https://mabicommerce-community.mabi-projects.workers.dev.evil.example",
            "https://mabicommerce-community.mabi-projects.workers.dev/path",
            "https://mabicommerce-community.mabi-projects.workers.dev?target=evil",
            "https://mabicommerce-community.mabi-projects.workers.dev#fragment",
            "https://example.com", "http://192.168.1.1:8787", "http://127.0.0.1:8787/path",
            "http://user:pass@127.0.0.1:8787", "http://127.0.0.1:8787?target=evil",
            "http://127.0.0.1:8787#fragment", "not a URL", "file:///C:/test" })
            Assert.ThrowsException<ArgumentException>(() => CommunityClient.ValidateEndpoint(endpoint), endpoint);
    }

    [TestMethod]
    public void BuildDefaultsKeepDevelopmentAndUserBuildsOnSameOnlineBackend()
    {
        Assert.AreEqual(CommunityClient.ProductionEndpoint, CommunityClient.DefaultEndpoint);
    }

    [TestMethod]
    public async Task ReviewedSnapshotsAllowMissingOcrProfitAndPreserveManualLineFlags()
    {
        var review = new CommunityReview(false, true, [2]);
        using var handler = new StubHandler((request, _) =>
        {
            Assert.AreEqual("normalized-v2", request.Headers.GetValues("X-Community-Format").Single());
            return Json(new
            {
                observations = new[] { new CommunityObservation(1, 200, "trade", 101, 1, 100, 0,
                    [new CommunityPrice(2, 135, null)], "normalized-v1", 100, 0, review) },
                revision = 0, nextCursor = 1
            });
        });
        using var client = new CommunityClient(CommunityClient.ProductionEndpoint, handler);
        var snapshot = await client.ReadLatestAsync(default);
        Assert.IsNull(snapshot.Observations[0].Prices[0].Profit);
        CollectionAssert.AreEqual(new[] { 2 }, snapshot.Observations[0].Review!.ManualPostIds.ToArray());
        Assert.IsTrue(snapshot.Observations[0].Review!.StockManual);
    }

    [TestMethod]
    public async Task UnreviewedMissingProfitAndUnknownManualDestinationFailClosed()
    {
        foreach (var review in new CommunityReview?[] { null, new(false, false, [999]) })
        {
            using var handler = new StubHandler((_, _) => Json(new
            {
                observations = new[] { new CommunityObservation(1, 200, "trade", 101, 1, 100, 0,
                    [new CommunityPrice(2, 135, null)], "normalized-v1", 100, 0, review) },
                revision = 0, nextCursor = 1
            }));
            using var client = new CommunityClient(CommunityClient.ProductionEndpoint, handler);
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => client.ReadLatestAsync(default));
        }
    }

    [TestMethod]
    public async Task ConstructorAndOffStateNeverContactBackend()
    {
        using var handler = new StubHandler((_, _) => throw new AssertFailedException("Unexpected network request"));
        using var client = new CommunityClient("http://127.0.0.1:8787", handler);
        Assert.IsFalse(client.IsConnected);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => client.BeginScanAsync(default));
        Assert.AreEqual(0, handler.Count);
    }

    [TestMethod]
    public async Task LegacyIndefiniteAssociationPolicyCannotEnableNewSharingConsent()
    {
        using var handler = new StubHandler((_, _) => Json(new
        {
            consentVersion = Policy.ConsentVersion, privateIpRetentionDays = 30, observationRetention = "indefinite"
        }));
        using var client = new CommunityClient("http://127.0.0.1:8787", handler);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => client.GetPrivacyAsync(default));
        Assert.IsFalse(client.IsConnected);
        Assert.AreEqual(1, handler.Count);
    }

    [TestMethod]
    public async Task PrivacyMismatchNeverRegistersOrEnables()
    {
        using var handler = new StubHandler((_, _) => Json(new { consentVersion = "future", privateIpRetentionDays = 30, observationRetention = "indefinite" }));
        using var client = new CommunityClient("http://127.0.0.1:8787", handler);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => client.GetPrivacyAsync(default));
        Assert.IsFalse(client.IsConnected);
        Assert.AreEqual(1, handler.Count);
    }

    [TestMethod]
    public async Task ConsentTicketUploadAndTypedReadRoundTripWithoutImageOrPersonalData()
    {
        var scanId = Guid.NewGuid().ToString();
        using var handler = new StubHandler((request, body) =>
        {
            Assert.AreEqual("127.0.0.1", request.RequestUri!.Host);
            switch (request.RequestUri.AbsolutePath)
            {
                case "/v1/privacy":
                    return Json(new { consentVersion = Policy.ConsentVersion, privateIpRetentionDays = 30, observationRetention = "indefinite",
                        installationAssociationRetentionDays = 30, installationAssociationExpiryScope = "post_migration_observations", collected = new[] { "prices" } });
                case "/v1/installations":
                    Assert.IsNull(request.Headers.Authorization);
                    using (var document = JsonDocument.Parse(body!))
                    {
                        Assert.AreEqual(2, document.RootElement.EnumerateObject().Count());
                        Assert.IsTrue(document.RootElement.GetProperty("acceptPrivacy").GetBoolean());
                    }
                    return Registration();
                case "/v1/scan-tickets":
                    Assert.AreEqual(Token, request.Headers.Authorization?.Parameter);
                    return Json(new { scanId, expiresAt = DateTimeOffset.UtcNow.AddSeconds(60).ToUnixTimeMilliseconds() });
                case "/v1/scans":
                    Assert.AreEqual(Token, request.Headers.Authorization?.Parameter);
                    using (var document = JsonDocument.Parse(body!))
                    {
                        Assert.AreEqual(10, document.RootElement.EnumerateObject().Count());
                        Assert.IsFalse(document.RootElement.TryGetProperty("screenshot", out _));
                        Assert.AreEqual(0, document.RootElement.GetProperty("stock").GetInt32());
                    }
                    return Json(new { sequence = 1, receivedAt = 1234567, duplicate = false });
                case "/v1/latest":
                    Assert.IsNull(request.Headers.Authorization);
                    return Json(new { observations = new[] {
                        new { sequence = 1, receivedAt = 1234567, mode = "trade", productId = 101,
                            sourcePostId = 1, sourcePrice = 100, stock = 0,
                            prices = new[] { new { postId = 2, salePrice = 120, profit = 20 } } }
                    }, revision = 0 });
                default: throw new AssertFailedException("Unexpected endpoint");
            }
        });
        using var client = new CommunityClient("http://127.0.0.1:8787", handler);
        await client.ConnectAsync(await client.GetPrivacyAsync(default), default);
        var ticket = await client.BeginScanAsync(default);
        var receipt = await client.UploadAsync(ticket, new CommunityScan(ticket.ScanId, "trade", 101, 1, 100, 0,
            [new CommunityPrice(2, 120, 20)]), default);
        Assert.AreEqual(1L, receipt.Sequence);
        var latest = await client.ReadLatestAsync(default);
        Assert.AreEqual(20, latest.Observations[0].Prices[0].Profit);
        Assert.AreEqual(0, latest.Observations[0].Stock);
        client.Disconnect();
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => client.UploadAsync(ticket,
            new CommunityScan(ticket.ScanId, "trade", 101, 1, 100, 0, []), default));
    }

    [TestMethod]
    public async Task OldOrExpiredTicketCannotUploadAfterReconnection()
    {
        using var handler = new StubHandler((request, _) => request.RequestUri!.AbsolutePath.EndsWith("installations")
            ? Registration()
            : Json(new { scanId = Guid.NewGuid().ToString(), expiresAt = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds() }));
        using var client = new CommunityClient("http://127.0.0.1:8787", handler);
        await client.ConnectAsync(Policy, default);
        var ticket = await client.BeginScanAsync(default);
        client.Disconnect();
        await client.ConnectAsync(Policy, default);
        var scan = new CommunityScan(ticket.ScanId, "trade", 101, 1, 100, 0, []);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => client.UploadAsync(ticket, scan, default));
        var fresh = await client.BeginScanAsync(default);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => client.UploadAsync(fresh with { ExpiresAt = 1 },
            scan with { ScanId = fresh.ScanId }, default));
        Assert.AreEqual(4, handler.Count);
    }

    [TestMethod]
    public async Task ErrorsMalformedResponsesAndCanceledRequestsAreExplicit()
    {
        using (var handler = new StubHandler((_, _) => Json(new { error = "rate_limited" }, HttpStatusCode.TooManyRequests)))
        using (var client = new CommunityClient("http://127.0.0.1:8787", handler))
        {
            var error = await Assert.ThrowsExceptionAsync<CommunityApiException>(() => client.ConnectAsync(Policy, default));
            Assert.AreEqual(HttpStatusCode.TooManyRequests, error.StatusCode);
            StringAssert.Contains(error.Message, "rate_limited");
            Assert.IsFalse(client.IsConnected);
        }
        using (var handler = new StubHandler((_, _) => Json(new { observations = (object?)null, revision = 0 })))
        using (var client = new CommunityClient("http://127.0.0.1:8787", handler))
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => client.ReadLatestAsync(default));
        using (var handler = new StubHandler((_, _) => Json(new { revision = 0 })))
        using (var client = new CommunityClient("http://127.0.0.1:8787", handler))
            await Assert.ThrowsExceptionAsync<JsonException>(() => client.ReadLatestAsync(default));
        using (var handler = new StubHandler((_, _) => Json(new { observations = Array.Empty<object>(), revision = 0 })))
        using (var client = new CommunityClient("http://127.0.0.1:8787", handler))
        {
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => client.ReadLatestAsync(cancel.Token));
        }
    }

    private static HttpResponseMessage Registration() =>
        Json(new { installationId = Guid.NewGuid().ToString(), token = Token, consentVersion = Policy.ConsentVersion });

    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Count++;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request, body);
        }
    }
}
