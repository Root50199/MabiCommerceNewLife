using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using MabiCommerceNewLife;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalculatorTests;

[TestClass]
public sealed class CommunitySyncTests
{
    private static CommunityObservation Observation(long sequence, int product = 101) =>
        new(sequence, 1000 + sequence, "trade", product, 1, 100, 50, [new(2, 120, 27)],
            "normalized-v1", 93, 7);

    [TestMethod]
    public async Task BootstrapThenPagedDeltasRequestOnlyChangesAndMergeByGood()
    {
        using var handler = new QueueHandler(
            ("/v1/latest", new CommunitySnapshot([Observation(1)], 0, 1)),
            ("/v1/sync?cursor=1&revision=0&limit=200", new CommunitySyncPage([Observation(2, 102)], 2, 0, true)),
            ("/v1/sync?cursor=2&revision=0&limit=200", new CommunitySyncPage([Observation(3)], 3, 0, false)),
            ("/v1/sync?cursor=3&revision=0&limit=200", new CommunitySyncPage([], 3, 0, false)));
        using var client = new CommunityClient("http://127.0.0.1:8787", handler);
        Assert.AreEqual(1L, (await client.SyncAsync(default)).NextCursor);
        var merged = await client.SyncAsync(default);
        CollectionAssert.AreEqual(new long[] { 2, 3 }, merged.Observations.Select(o => o.Sequence).ToArray());
        Assert.AreEqual(3L, (await client.SyncAsync(default)).NextCursor);
        Assert.AreEqual(0, handler.Remaining);
    }

    [TestMethod]
    public async Task FailedLaterPageDoesNotCommitEarlierPageOrCursor()
    {
        using var handler = new QueueHandler(
            ("/v1/latest", new CommunitySnapshot([Observation(1)], 0, 1)),
            ("/v1/sync?cursor=1&revision=0&limit=200", new CommunitySyncPage([Observation(2)], 2, 0, true)),
            ("/v1/sync?cursor=2&revision=0&limit=200", new Failure("unavailable", HttpStatusCode.ServiceUnavailable)),
            ("/v1/sync?cursor=1&revision=0&limit=200", new CommunitySyncPage([], 1, 0, false)));
        using var client = new CommunityClient("http://127.0.0.1:8787", handler);
        await client.SyncAsync(default);
        await Assert.ThrowsExceptionAsync<CommunityApiException>(() => client.SyncAsync(default));
        Assert.AreEqual(1L, (await client.SyncAsync(default)).Observations.Single().Sequence);
    }

    [TestMethod]
    public async Task ModerationResetReplacesRatherThanMergesRemovedContributions()
    {
        using var handler = new QueueHandler(
            ("/v1/latest", new CommunitySnapshot([Observation(1)], 0, 1)),
            ("/v1/sync?cursor=1&revision=0&limit=200", new Failure("sync_reset_required", HttpStatusCode.Conflict)),
            ("/v1/latest", new CommunitySnapshot([], 1, 1)));
        using var client = new CommunityClient("http://127.0.0.1:8787", handler);
        await client.SyncAsync(default);
        var reset = await client.SyncAsync(default);
        Assert.AreEqual(1L, reset.Revision);
        Assert.AreEqual(0, reset.Observations.Count);
    }

    [TestMethod]
    public async Task InvalidOrderCursorAndNullRowsFailExplicitly()
    {
        foreach (var page in new[] {
            new CommunitySyncPage([Observation(3), Observation(2)], 3, 0, false),
            new CommunitySyncPage([Observation(1)], 1, 0, false),
            new CommunitySyncPage([], 1, 0, true),
            new CommunitySyncPage([], 2, 9, false),
            new CommunitySyncPage([Observation(2) with { Prices = [null!] }], 2, 0, false) })
        {
            using var handler = new QueueHandler(
                ("/v1/latest", new CommunitySnapshot([Observation(1)], 0, 1)),
                ("/v1/sync?cursor=1&revision=0&limit=200", page));
            using var client = new CommunityClient("http://127.0.0.1:8787", handler);
            await client.SyncAsync(default);
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => client.SyncAsync(default));
        }
    }

    [TestMethod]
    public async Task InvalidNormalizedArithmeticAndSnapshotWatermarkFailBeforeApplication()
    {
        foreach (var snapshot in new[] {
            new CommunitySnapshot([Observation(1)], 0, 0),
            new CommunitySnapshot([Observation(1) with { SourcePrice = 99 }], 0, 1),
            new CommunitySnapshot([Observation(1) with { Prices = [new(2, 120, 20)] }], 0, 1) })
        {
            using var handler = new QueueHandler(("/v1/latest", snapshot));
            using var client = new CommunityClient("http://127.0.0.1:8787", handler);
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => client.SyncAsync(default));
        }
    }

    private sealed record Failure(string Error, HttpStatusCode Status);

    private sealed class QueueHandler(params (string Path, object Response)[] responses) : HttpMessageHandler
    {
        private readonly Queue<(string Path, object Response)> _responses = new(responses);
        public int Remaining => _responses.Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var next = _responses.Dequeue();
            Assert.AreEqual(next.Path, request.RequestUri!.PathAndQuery);
            Assert.AreEqual("normalized-v2", request.Headers.GetValues("X-Community-Format").Single());
            return Task.FromResult(next.Response is Failure failure
                ? new HttpResponseMessage(failure.Status) { Content = JsonContent.Create(new { error = failure.Error }) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(next.Response) });
        }
    }
}
