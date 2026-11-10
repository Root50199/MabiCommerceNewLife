namespace MabiCommerceNewLife;

public readonly record struct CommunityOutpost(string Mode, int PostId);
public sealed record CommunityPendingBatch(CommunityOutpost Outpost, IReadOnlyList<CommunityScan> Scans);

public sealed class CommunityScanQueue(TimeProvider? timeProvider = null)
{
    public const int MaxBatchScans = 16;
    public static readonly TimeSpan IdleDelay = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(4);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<CommunityOutpost, Pending> _pending = [];
    public int Count => _pending.Values.Sum(group => group.Scans.Count);
    public bool HasFailures => _pending.Values.Any(group => group.Failed);

    public void ScanStarted(CommunityOutpost outpost)
    {
        if (_pending.TryGetValue(outpost, out var group)) group.Activity = _clock.GetTimestamp();
    }

    public void Enqueue(CommunityScan scan, IReadOnlyCollection<int> expectedProducts)
    {
        if (scan.Mode is not ("trade" or "group" or "barter") || scan.SourcePostId <= 0 ||
            expectedProducts.Count is < 1 or > MaxBatchScans || expectedProducts.Distinct().Count() != expectedProducts.Count ||
            !expectedProducts.Contains(scan.ProductId) || !Guid.TryParse(scan.ScanId, out var id) || id == Guid.Empty)
            throw new InvalidOperationException("Cannot queue scan: goods do not match a bounded outpost.");
        var outpost = new CommunityOutpost(scan.Mode, scan.SourcePostId);
        var now = _clock.GetTimestamp();
        if (!_pending.TryGetValue(outpost, out var group))
            _pending[outpost] = group = new Pending(now);
        if (!group.Scans.ContainsKey(scan.ProductId) && group.Scans.Count >= MaxBatchScans)
            throw new InvalidOperationException("Cannot queue scan: pending outpost exceeds the batch limit.");
        group.Expected = expectedProducts.ToHashSet();
        group.Activity = now;
        group.Scans[scan.ProductId] = scan with
        {
            Prices = scan.Prices.ToArray(),
            Review = scan.Review is { } review ? review with { ManualPostIds = review.ManualPostIds.ToArray() } : null
        };
    }

    public CommunityPendingBatch? TakeNext(bool force = false)
    {
        foreach (var (outpost, group) in _pending)
        {
            if (group.InFlight || (!force && group.Failed)) continue;
            if (!force && Remaining(group) > TimeSpan.Zero) continue;
            group.InFlight = true;
            return new CommunityPendingBatch(outpost, group.Scans.Values.OrderBy(scan => scan.ProductId).ToArray());
        }
        return null;
    }

    public void Complete(CommunityPendingBatch batch)
    {
        if (!_pending.TryGetValue(batch.Outpost, out var group)) return;
        foreach (var scan in batch.Scans)
            if (group.Scans.TryGetValue(scan.ProductId, out var current) && current.ScanId == scan.ScanId)
                group.Scans.Remove(scan.ProductId);
        group.InFlight = group.Failed = false;
        if (group.Scans.Count == 0) _pending.Remove(batch.Outpost);
    }

    public void Fail(CommunityPendingBatch batch)
    {
        if (!_pending.TryGetValue(batch.Outpost, out var group)) return;
        group.InFlight = false;
        group.Failed = true;
    }

    public TimeSpan? NextUploadIn => _pending.Values.Where(group => !group.InFlight && !group.Failed)
        .Select(group => (TimeSpan?)Remaining(group)).Min();

    public void Clear() => _pending.Clear();

    private TimeSpan Remaining(Pending group)
    {
        if (group.Expected.All(group.Scans.ContainsKey)) return TimeSpan.Zero;
        var delay = IdleDelay - _clock.GetElapsedTime(group.Activity);
        var maximum = MaximumAge - _clock.GetElapsedTime(group.First);
        var remaining = delay < maximum ? delay : maximum;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private sealed class Pending(long now)
    {
        public long First { get; } = now;
        public long Activity { get; set; } = now;
        public HashSet<int> Expected { get; set; } = [];
        public Dictionary<int, CommunityScan> Scans { get; } = [];
        public bool InFlight { get; set; }
        public bool Failed { get; set; }
    }
}
