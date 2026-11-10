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

    [TestMethod]
    [DataRow(409)]
    [DataRow(429)]
    [DataRow(503)]
    public async Task FailedBatchKeepsQueueAndStableIdsForManualRetry(int status)
    {
        var queue = new CommunityScanQueue();
        var scan = new CommunityScan(Guid.NewGuid().ToString(), "trade", 1, 10, 100, 25, [new(20, 120, 20)]);
        queue.Enqueue(scan, [1]);
        var requests = 0;
        using var handler = new StubHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/installations") return Registration();
            requests++;
            return new HttpResponseMessage((System.Net.HttpStatusCode)status)
            {
                Content = new StringContent("{\"error\":\"test_failure\"}")
            };
        });
        using var client = new CommunityClient("http://127.0.0.1:8787", handler);
        await client.ConnectAsync(Policy, default);
        var batch = queue.TakeNext()!;
        await Assert.ThrowsExceptionAsync<CommunityApiException>(() =>
            client.UploadBatchAsync(batch, client.ConnectedSession!.Value, default));
        queue.Fail(batch);
        Assert.AreEqual(1, requests, "Upload failures must not replay automatically.");
        Assert.AreEqual(1, queue.Count);
        Assert.IsNull(queue.TakeNext());
        Assert.AreEqual(scan.ScanId, queue.TakeNext(force: true)!.Scans.Single().ScanId);
    }

    [TestMethod]
    public async Task BatchReplyCannotCrossAnOffOnSessionBoundary()
    {
        CommunityClient? active = null;
        var scan = new CommunityScan(Guid.NewGuid().ToString(), "trade", 1, 10, 100, 25, [new(20, 120, 20)]);
        using var handler = new StubHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/installations") return Registration();
            active!.Disconnect();
            return Json(new { receipts = new[] { new CommunityBatchReceipt(scan.ScanId, 1, 1000, false) } });
        });
        using var client = new CommunityClient("http://127.0.0.1:8787", handler);
        active = client;
        await client.ConnectAsync(Policy, default);
        var session = client.ConnectedSession!.Value;
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            client.UploadBatchAsync(new(new("trade", 10), [scan]), session, default));
        Assert.IsNull(client.ConnectedSession);
    }

    [TestMethod]
    public async Task NullBatchReceiptIsAValidationFailureNotAnUnhandledException()
    {
        using var handler = new StubHandler((request, _) =>
            request.RequestUri!.AbsolutePath == "/v1/installations"
                ? Registration() : Json(new { receipts = new object?[] { null } }));
        using var client = new CommunityClient("http://127.0.0.1:8787", handler);
        await client.ConnectAsync(Policy, default);
        var scan = new CommunityScan(Guid.NewGuid().ToString(), "trade", 1, 10, 100, 25, [new(20, 120, 20)]);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
            client.UploadBatchAsync(new(new("trade", 10), [scan]), client.ConnectedSession!.Value, default));
    }

    [TestMethod]
    public async Task OutpostBatchUsesOneRequestWithoutScanTicketsAndValidatesAllReceipts()
    {
        var scans = Enumerable.Range(1, 5).Select(id =>
            new CommunityScan(Guid.NewGuid().ToString(), "trade", id, 10, 100, 25, [new(20, 120, 20)])).ToArray();
        var invalidReceipt = false;
        var batches = 0;
        using var handler = new StubHandler((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/installations") return Registration();
            Assert.AreEqual("/v1/scan-batches", request.RequestUri.AbsolutePath);
            Assert.AreEqual(Token, request.Headers.Authorization!.Parameter);
            using var json = JsonDocument.Parse(body!);
            Assert.AreEqual(5, json.RootElement.GetProperty("scans").GetArrayLength());
            batches++;
            return Json(new { receipts = scans.Take(invalidReceipt ? 1 : 5)
                .Select((scan, index) => new CommunityBatchReceipt(scan.ScanId, index + 1, 1000, false)) });
        });
        using var client = new CommunityClient("http://127.0.0.1:8787", handler);
        await client.ConnectAsync(Policy, default);
        var session = client.ConnectedSession!.Value;
        var batch = new CommunityPendingBatch(new("trade", 10), scans);
        Assert.AreEqual(5, (await client.UploadBatchAsync(batch, session, default)).Count);
        Assert.AreEqual(2, handler.Count, "One registration plus one batch; no ticket requests.");
        invalidReceipt = true;
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => client.UploadBatchAsync(batch, session, default));
        client.Disconnect();
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => client.UploadBatchAsync(batch, session, default));
        Assert.AreEqual(2, batches);
    }

    [TestMethod]
    public async Task RetryAfterSecondsDatesAndInvalidHeadersArePreservedSafely()
    {
        var serverTime = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        foreach (var (header, expected) in new (string?, TimeSpan?)[]
        {
            ("25", TimeSpan.FromSeconds(25)), ("0", TimeSpan.Zero),
            (serverTime.AddSeconds(40).ToString("R"), TimeSpan.FromSeconds(40)),
            (serverTime.AddSeconds(-10).ToString("R"), TimeSpan.Zero),
            (null, null), ("garbage", null), ("-5", null), ("999999999999999999999", null),
            ("10, 20", null)
        })
        {
            using var handler = new StubHandler((_, _) =>
            {
                var response = Json(new { error = "rate_limited" }, HttpStatusCode.TooManyRequests);
                response.Headers.Date = serverTime;
                if (header is not null) response.Headers.TryAddWithoutValidation("Retry-After", header);
                return response;
            });
            using var client = new CommunityClient("http://127.0.0.1:8787", handler);
            var error = await Assert.ThrowsExceptionAsync<CommunityApiException>(() => client.ConnectAsync(Policy, default));
            Assert.AreEqual(expected, error.RetryAfter, header ?? "missing");
            Assert.AreEqual("rate_limited", error.Code);
            Assert.IsFalse(client.IsConnected);
        }
    }

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
