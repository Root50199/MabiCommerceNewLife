namespace MabiCommerceNewLife;

public sealed class CommunityRefreshGate(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private long? _lastAttempt;
    private bool _busy;
    public bool IsBusy => _busy;
    public TimeSpan Remaining
    {
        get
        {
            if (_lastAttempt is not { } last) return TimeSpan.Zero;
            var remaining = TimeSpan.FromMinutes(1) - _clock.GetElapsedTime(last);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }
    public bool CanRefresh => !_busy && Remaining == TimeSpan.Zero;

    public bool TryBegin()
    {
        if (!CanRefresh) return false;
        _lastAttempt = _clock.GetTimestamp();
        _busy = true;
        return true;
    }

    public void Complete() => _busy = false;
    public void RecordRetry() => _lastAttempt = _clock.GetTimestamp();
}
