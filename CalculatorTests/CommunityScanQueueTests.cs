using MabiCommerceNewLife;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalculatorTests;

[TestClass]
public sealed class CommunityScanQueueTests
{
    private static CommunityScan Scan(int good = 1, int post = 10, string mode = "trade") =>
        new(Guid.NewGuid().ToString(), mode, good, post, 100, 25, [new(20, 120, 20)],
            "normalized-v1", 100, 0);

    [TestMethod]
    public void FirstScanWaitsFullMinuteAndActivityResetsOnlyIdleTimer()
    {
        var clock = new Clock();
        var queue = new CommunityScanQueue(clock);
        queue.Enqueue(Scan(), [1, 2]);
        Assert.AreEqual(TimeSpan.FromMinutes(1), queue.NextUploadIn);
        clock.Advance(59);
        Assert.IsNull(queue.TakeNext());
        queue.ScanStarted(new("trade", 10));
        clock.Advance(59);
        Assert.IsNull(queue.TakeNext());
        clock.Advance(1);
        Assert.AreEqual(1, queue.TakeNext()!.Scans.Count);
    }

    [TestMethod]
    public void FourMinuteMaximumCannotBePostponedByContinuousRescans()
    {
        var clock = new Clock();
        var queue = new CommunityScanQueue(clock);
        queue.Enqueue(Scan(), [1, 2]);
        for (var elapsed = 30; elapsed < 240; elapsed += 30)
        {
            clock.Advance(30);
            queue.ScanStarted(new("trade", 10));
            queue.Enqueue(Scan(), [1, 2]);
            Assert.IsNull(queue.TakeNext());
        }
        clock.Advance(29);
        Assert.IsNull(queue.TakeNext());
        clock.Advance(1);
        Assert.IsNotNull(queue.TakeNext());
    }

    [TestMethod]
    public void CompleteOutpostUploadsImmediatelyAndLatestScanReplacesSameGood()
    {
        var queue = new CommunityScanQueue();
        queue.Enqueue(Scan(), [1, 2]);
        var newest = Scan() with { Stock = 30 };
        queue.Enqueue(newest, [1, 2]);
        Assert.AreEqual(1, queue.Count);
        queue.Enqueue(Scan(2), [1, 2]);
        var batch = queue.TakeNext()!;
        Assert.AreEqual(2, batch.Scans.Count);
        Assert.AreEqual(newest.ScanId, batch.Scans[0].ScanId);
        Assert.AreEqual(30, batch.Scans[0].Stock);
        queue.Complete(batch);
        Assert.AreEqual(0, queue.Count);
        Assert.IsNull(queue.NextUploadIn);
    }

    [TestMethod]
    public void DifferentOutpostsAndModesHaveIndependentTimersAndCompletion()
    {
        var clock = new Clock();
        var queue = new CommunityScanQueue(clock);
        queue.Enqueue(Scan(), [1, 2]);
        clock.Advance(20);
        queue.Enqueue(Scan(post: 11), [1, 2]);
        queue.Enqueue(Scan(mode: "group"), [1, 2]);
        queue.ScanStarted(new("trade", 10));
        clock.Advance(40);
        Assert.IsNull(queue.TakeNext());
        clock.Advance(20);
        var batches = new List<CommunityPendingBatch>();
        while (queue.TakeNext() is { } batch) { batches.Add(batch); queue.Complete(batch); }
        Assert.AreEqual(3, batches.Count);
        Assert.AreEqual(3, batches.Select(batch => batch.Outpost).Distinct().Count());
    }

    [TestMethod]
    public void InFlightBatchCannotOverlapAndAckCannotDeleteNewerRescan()
    {
        var queue = new CommunityScanQueue();
        var old = Scan();
        queue.Enqueue(old, [1]);
        var batch = queue.TakeNext()!;
        Assert.IsNull(queue.TakeNext(force: true));
        var newer = Scan() with { Stock = 99 };
        queue.Enqueue(newer, [1]);
        queue.Complete(batch);
        Assert.AreEqual(1, queue.Count);
        Assert.AreEqual(newer.ScanId, queue.TakeNext()!.Scans[0].ScanId);
    }

    [TestMethod]
    public void FailureRetainsStableIdsUntilExplicitRetryAndOffDiscardsAll()
    {
        var queue = new CommunityScanQueue();
        var scan = Scan();
        queue.Enqueue(scan, [1]);
        var sent = queue.TakeNext()!;
        queue.Fail(sent);
        Assert.IsTrue(queue.HasFailures);
        Assert.IsNull(queue.TakeNext());
        Assert.IsNull(queue.NextUploadIn);
        var retry = queue.TakeNext(force: true)!;
        Assert.AreEqual(scan.ScanId, retry.Scans[0].ScanId);
        queue.Clear();
        queue.Complete(retry);
        Assert.AreEqual(0, queue.Count);
        Assert.IsFalse(queue.HasFailures);
    }

    [TestMethod]
    public void QueueSnapshotsPreserveManualFlagsWithoutRetainingMutableInputLists()
    {
        var manual = new List<int> { 20 };
        var prices = new List<CommunityPrice> { new(20, 125, null) };
        var queue = new CommunityScanQueue();
        queue.Enqueue(Scan() with { Prices = prices, Review = new(false, true, manual) }, [1]);
        manual.Clear();
        prices.Clear();
        var queued = queue.TakeNext()!.Scans[0];
        Assert.AreEqual(1, queued.Prices.Count);
        CollectionAssert.AreEqual(new[] { 20 }, queued.Review!.ManualPostIds.ToArray());
    }

    [TestMethod]
    public void InvalidOrOversizedOutpostsAreExplicitAndNeverQueued()
    {
        var queue = new CommunityScanQueue();
        foreach (var expected in new int[][] { [], [2], [1, 1], Enumerable.Range(1, 17).ToArray() })
            Assert.ThrowsException<InvalidOperationException>(() => queue.Enqueue(Scan(), expected));
        Assert.AreEqual(0, queue.Count);
    }

    private sealed class Clock : TimeProvider
    {
        private long _time;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _time;
        public void Advance(int seconds) => _time += TimeSpan.FromSeconds(seconds).Ticks;
    }
}
