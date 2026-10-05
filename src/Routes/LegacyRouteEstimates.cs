using System.IO;
using System.Text.Json;

namespace MabiCommerceNewLife;

// Transport speed factors applied to handcart-baseline minutes. The historical MabiCommerce route table was removed;
// route distances now come only from client-map analysis.
public static class LegacyRouteEstimates
{
    private static readonly IReadOnlyDictionary<int, decimal> TransportSpeedFactors = new Dictionary<int, decimal>
    {
        [1] = 0.91m, [5] = 0.91m, [10] = 0.91m,
        [2] = 1m, [6] = 1m, [11] = 1m,
        [3] = 1.90m, [7] = 1.90m, [9] = 1.90m, [12] = 1.90m,
        [4] = 1.37m, [8] = 1.37m, [13] = 1.37m,
        [14] = 1.90m, [15] = 1.90m, [16] = 1.90m, [20] = 1.90m,
        [17] = 1.86m, [21] = 1.86m, [24] = 1.86m, [27] = 1.86m, [30] = 1.86m, [33] = 1.86m,
        [45] = 1.86m, [48] = 1.86m, [51] = 1.86m, [54] = 1.86m, [57] = 1.86m, [60] = 1.86m,
        [18] = 2.15m, [22] = 2.15m, [25] = 2.15m, [28] = 2.15m, [31] = 2.15m, [34] = 2.15m,
        [46] = 2.15m, [49] = 2.15m, [52] = 2.15m, [55] = 2.15m, [58] = 2.15m, [61] = 2.15m,
        [19] = 2.40m, [23] = 2.40m, [26] = 2.40m, [29] = 2.40m, [32] = 2.40m, [35] = 2.40m,
        [47] = 2.40m, [50] = 2.40m, [53] = 2.40m, [56] = 2.40m, [59] = 2.40m, [62] = 2.40m,
        [1001] = 2.40m, [1002] = 2.40m, [1003] = 2.40m, [1004] = 2.40m
    };
    private static readonly IReadOnlySet<int> DogSledTransportIds = new HashSet<int>
    {
        17, 21, 24, 27, 30, 33, 45, 48, 51, 54, 57, 60
    };
    private static readonly IReadOnlySet<int> CamelTransportIds = new HashSet<int>
    {
        18, 22, 25, 28, 31, 34, 46, 49, 52, 55, 58, 61
    };


    public static decimal? EstimatedMinutesFromHandcart(decimal? handcartMinutes, int transportId, int? regionId = null)
    {
        if (handcartMinutes is null or <= 0 || !TryGetSpeedFactor(transportId, regionId, out var factor))
            return null;
        return handcartMinutes.Value / factor;
    }

    public static bool TryGetSpeedFactor(int transportId, int? regionId, out decimal factor)
    {
        if (regionId == RegionIds.ValesSnowfield && DogSledTransportIds.Contains(transportId))
        {
            factor = 3.09m;
            return true;
        }
        if (CamelTransportIds.Contains(transportId) && regionId == RegionIds.FiliaDesert)
        {
            factor = 3.07m;
            return true;
        }
        if (CamelTransportIds.Contains(transportId) && regionId == RegionIds.ValesSnowfield)
        {
            factor = 1.33m;
            return true;
        }
        return TransportSpeedFactors.TryGetValue(transportId, out factor);
    }

}

public sealed class RegionalRouteDistanceCatalog
{
    public decimal MinutesPerThousandWorldUnits { get; set; }
    public List<RegionalRouteDistance> Routes { get; set; } = [];
}

public sealed class RegionalRouteDistance
{
    public int SourcePostId { get; set; }
    public int DestinationPostId { get; set; }
    public decimal DistanceWorldUnits { get; set; }
    // Non-boat portals (region load screens) crossed along the route.
    public int PortalCount { get; set; }
}

public static class RegionalRouteEstimates
{
    public const int DefaultPortalLoadBufferSeconds = 5;
    public const int MaxPortalLoadBufferSeconds = 300;

    public static decimal PortalLoadMinutes(int portalCount, int bufferSeconds) =>
        portalCount <= 0 || bufferSeconds <= 0 ? 0m : portalCount * bufferSeconds / 60m;

    public static IReadOnlyDictionary<(int SourceId, int DestinationId), int> LoadPortalCounts(string path)
    {
        var counts = new Dictionary<(int, int), int>();
        foreach (var route in LoadCatalog(path)?.Routes ?? [])
        {
            counts[(route.SourcePostId, route.DestinationPostId)] = route.PortalCount;
            counts[(route.DestinationPostId, route.SourcePostId)] = route.PortalCount;
        }
        return counts;
    }

    private static RegionalRouteDistanceCatalog? LoadCatalog(string path)
    {
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<RegionalRouteDistanceCatalog>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("The regional route distance file is empty.");
    }

    public static IReadOnlyDictionary<(int SourceId, int DestinationId), decimal> LoadHandcartMinutes(string path)
    {
        var catalog = LoadCatalog(path);
        if (catalog is null) return new Dictionary<(int, int), decimal>();
        if (catalog.MinutesPerThousandWorldUnits <= 0)
            throw new InvalidDataException("The regional route distance calibration must be positive.");

        var estimates = new Dictionary<(int, int), decimal>();
        foreach (var route in catalog.Routes)
        {
            if (route.SourcePostId <= 0 || route.DestinationPostId <= 0 ||
                route.SourcePostId == route.DestinationPostId || route.DistanceWorldUnits <= 0 || route.PortalCount < 0)
                throw new InvalidDataException("A regional route distance entry is invalid.");
            var handcartMinutes = route.DistanceWorldUnits / 1000m * catalog.MinutesPerThousandWorldUnits;
            if (!estimates.TryAdd((route.SourcePostId, route.DestinationPostId), handcartMinutes) ||
                !estimates.TryAdd((route.DestinationPostId, route.SourcePostId), handcartMinutes))
                throw new InvalidDataException("Regional route distance entries contain duplicate pairs.");
        }

        return estimates;
    }
}