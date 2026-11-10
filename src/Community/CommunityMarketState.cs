namespace MabiCommerceNewLife;

public sealed class CommunityMarketValue
{
    public decimal LocalValue { get; private set; }
    public long LocalTime { get; private set; }
    public string LocalOrigin { get; private set; } = "Saved/default";
    public long? RemoteSequence { get; private set; }
    public long RemoteTime { get; private set; }
    public decimal? RemoteValue { get; private set; }
    private long _consumed;
    private long _localConsumption;
    private long _remoteConsumption;
    private long _highestSequence;
    private long _earliestRemoteConsumption;
    private readonly Dictionary<long, long> _firstSeenConsumption = [];
    public decimal Value => RemoteWins ? RemoteValue!.Value - ConsumptionSince(_remoteConsumption) :
        LocalValue - ConsumptionSince(_localConsumption);
    public bool RemoteWins => RemoteValue.HasValue && RemoteTime > LocalTime;
    public string RemoteOrigin { get; private set; } = "Community";
    public string Origin => RemoteWins ? $"{RemoteOrigin} #{RemoteSequence}" : LocalOrigin;
    public long Time => RemoteWins ? RemoteTime : LocalTime;
    public bool TracksStock { get; }

    public CommunityMarketValue(decimal value, bool tracksStock = false, int loadedQuantity = 0)
    {
        LocalValue = value;
        TracksStock = tracksStock;
        _consumed = _localConsumption = Math.Max(0, loadedQuantity);
    }

    public void SetLocal(decimal value, long time, string origin)
    {
        if (time < LocalTime) return;
        LocalValue = value;
        LocalTime = time;
        LocalOrigin = origin;
        _localConsumption = _consumed;
    }

    public void SetRemote(decimal value, long sequence, long time, string origin = "Community")
    {
        if (!_firstSeenConsumption.TryGetValue(sequence, out var consumption))
        {
            if (_highestSequence == 0) _earliestRemoteConsumption = _consumed;
            consumption = sequence > _highestSequence ? _consumed : _earliestRemoteConsumption;
            _firstSeenConsumption[sequence] = consumption;
            _highestSequence = Math.Max(_highestSequence, sequence);
        }
        RemoteValue = value;
        RemoteSequence = sequence;
        RemoteTime = time;
        RemoteOrigin = origin;
        _remoteConsumption = consumption;
    }

    public void ClearRemote()
    {
        RemoteValue = null;
        RemoteSequence = null;
        RemoteTime = 0;
    }

    public void RetainCurrentAsLocal(decimal effectiveValue)
    {
        if (RemoteWins)
            SetLocal(effectiveValue, RemoteTime, RemoteOrigin == "Community" ? "Saved shared data" : $"Saved shared data ({RemoteOrigin})");
        ClearRemote();
    }

    public void Consume(int quantity)
    {
        if (TracksStock) _consumed = Math.Max(0, checked(_consumed + quantity));
    }

    private long ConsumptionSince(long baseline) => TracksStock ? _consumed - baseline : 0;
}
