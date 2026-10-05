using System.Globalization;
using System.IO;
using System.Text.Json;

namespace MabiCommerceNewLife;

public sealed class ShipScheduleConfiguration
{
    public decimal ErinnMinutesPerRealMinute { get; set; } = 40m;
    public int PstUtcOffsetHours { get; set; } = -8;
    public int BoardingLeadErinnMinutes { get; set; } = 120;
    public decimal LandMinutesPerThousandWorldUnits { get; set; }
    public Dictionary<string, List<int>> PostContinents { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ShipSchedulePort> Ports { get; set; } = [];
    public List<ShipScheduleRoute> Routes { get; set; } = [];
    public List<ShipLandAccessDistance> LandAccessDistances { get; set; } = [];
}

public sealed class ShipSchedulePort
{
    public string Name { get; set; } = string.Empty;
    public string Continent { get; set; } = string.Empty;
}

public sealed class ShipScheduleRoute
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public List<string> DepartureTimesErinn { get; set; } = [];
    public int TravelDurationErinnMinutes { get; set; }
    public bool Bidirectional { get; set; }
}

public sealed class ShipLandAccessDistance
{
    public int PostId { get; set; }
    public List<string> PortNames { get; set; } = [];
    public decimal DistanceWorldUnits { get; set; }
    public int? RegionId { get; set; }
    // Non-boat portals (region load screens) crossed between the post and the port.
    public int PortalCount { get; set; }
}

// Ports lists the ship-schedule ports visited in order; consecutive ports on different continents are ferry hops.
public sealed record ShipTravelEstimate(decimal Minutes, string Breakdown, IReadOnlyList<string> Ports);

public sealed class ShipScheduleEstimator
{
    private const decimal MinutesPerErinnDay = 1440m;
    // Arriving this close to a departure counts as missing it; the trip waits for the next ship.
    public const int DefaultMissedDepartureBufferSeconds = 30;
    public const int MaxMissedDepartureBufferSeconds = 300;
    private static readonly TimeSpan ErinnDay = TimeSpan.FromDays(1);
    private readonly ShipScheduleConfiguration _configuration;
    private readonly Dictionary<string, string> _continentByPort;
    private readonly Dictionary<int, string> _continentByPost;
    private readonly Dictionary<(int PostId, string PortName), ShipLandAccessDistance> _landAccessDistances = new();
    private readonly List<ScheduledFerry> _ferries;

    public ShipScheduleEstimator(ShipScheduleConfiguration configuration)
    {
        _configuration = configuration;
        if (configuration.ErinnMinutesPerRealMinute <= 0 || configuration.BoardingLeadErinnMinutes < 0 ||
            configuration.LandMinutesPerThousandWorldUnits < 0)
            throw new InvalidDataException("Ship schedule clock settings are invalid.");

        _continentByPort = configuration.Ports.ToDictionary(port => port.Name, port => port.Continent,
            StringComparer.OrdinalIgnoreCase);
        _continentByPost = new Dictionary<int, string>();
        foreach (var continent in configuration.PostContinents)
        foreach (var postId in continent.Value)
            if (!_continentByPost.TryAdd(postId, continent.Key))
                throw new InvalidDataException($"Post {postId} belongs to more than one continent.");

        foreach (var access in configuration.LandAccessDistances)
        {
            if (!_continentByPost.ContainsKey(access.PostId) || access.DistanceWorldUnits < 0 || access.PortalCount < 0 ||
                access.PortNames.Count == 0)
                throw new InvalidDataException($"Land access distance for post {access.PostId} is invalid.");
            foreach (var portName in access.PortNames)
            {
                if (!_continentByPort.ContainsKey(portName) ||
                    !string.Equals(_continentByPost[access.PostId], _continentByPort[portName], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Port '{portName}' is not on post {access.PostId}'s continent.");
                if (!_landAccessDistances.TryAdd((access.PostId, portName), access))
                    throw new InvalidDataException($"Duplicate land access distance for post {access.PostId} to {portName}.");
            }
        }

        _ferries = [];
        foreach (var route in configuration.Routes)
        {
            if (!_continentByPort.ContainsKey(route.From) || !_continentByPort.ContainsKey(route.To) ||
                route.TravelDurationErinnMinutes <= 0 || route.DepartureTimesErinn.Count == 0)
                throw new InvalidDataException($"Ship route '{route.From}' to '{route.To}' is invalid.");

            var departures = route.DepartureTimesErinn.Select(ParseDeparture).Order().ToArray();
            _ferries.Add(new ScheduledFerry(route.From, route.To, departures, route.TravelDurationErinnMinutes));
            if (route.Bidirectional)
                _ferries.Add(new ScheduledFerry(route.To, route.From, departures, route.TravelDurationErinnMinutes));
        }
    }

    public static ShipScheduleEstimator Load(string path)
    {
        if (!File.Exists(path)) return new ShipScheduleEstimator(new ShipScheduleConfiguration());
        var configuration = JsonSerializer.Deserialize<ShipScheduleConfiguration>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("The ship schedule file is empty.");
        return new ShipScheduleEstimator(configuration);
    }

    // Accepts only a plain whole number of seconds from 0 to the maximum; no signs, decimals, separators or units.
    public static bool TryParseBufferSeconds(string? text, out int seconds)
    {
        seconds = 0;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 3 || !trimmed.All(char.IsAsciiDigit)) return false;
        seconds = int.Parse(trimmed, System.Globalization.CultureInfo.InvariantCulture);
        return seconds <= MaxMissedDepartureBufferSeconds;
    }

    public static TimeSpan GetErinnTimeOfDay(DateTimeOffset utcTime, int pstUtcOffsetHours = -8,
        decimal erinnMinutesPerRealMinute = 40m)
    {
        var pstTimeOfDay = utcTime.ToUniversalTime().ToOffset(TimeSpan.FromHours(pstUtcOffsetHours)).TimeOfDay;
        var gameTicks = (long)((decimal)pstTimeOfDay.Ticks * erinnMinutesPerRealMinute) % ErinnDay.Ticks;
        return TimeSpan.FromTicks(gameTicks);
    }

    public ShipTravelEstimate? Estimate(int sourcePostId, int destinationPostId, DateTimeOffset utcNow, int transportId = TransportIds.Handcart,
        Func<int, string>? postName = null, int missedDepartureBufferSeconds = DefaultMissedDepartureBufferSeconds,
        decimal landSpeedPercent = 0m, int portalLoadBufferSeconds = 0)
    {
        if (missedDepartureBufferSeconds is < 0 or > MaxMissedDepartureBufferSeconds)
            throw new ArgumentOutOfRangeException(nameof(missedDepartureBufferSeconds));
        if (portalLoadBufferSeconds is < 0 or > RegionalRouteEstimates.MaxPortalLoadBufferSeconds)
            throw new ArgumentOutOfRangeException(nameof(portalLoadBufferSeconds));
        postName ??= id => $"Post {id}";
        if (!_continentByPost.TryGetValue(sourcePostId, out var sourceContinent) ||
            !_continentByPost.TryGetValue(destinationPostId, out var destinationContinent) ||
            string.Equals(sourceContinent, destinationContinent, StringComparison.OrdinalIgnoreCase))
            return null;

        var bestMinutes = _configuration.Ports.ToDictionary(port => port.Name, _ => decimal.MaxValue,
            StringComparer.OrdinalIgnoreCase);
        var bestBreakdown = _configuration.Ports.ToDictionary(port => port.Name, _ => string.Empty,
            StringComparer.OrdinalIgnoreCase);
        var bestPorts = _configuration.Ports.ToDictionary(port => port.Name, _ => (IReadOnlyList<string>)[],
            StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var port in _configuration.Ports.Where(port =>
                     string.Equals(port.Continent, sourceContinent, StringComparison.OrdinalIgnoreCase)))
        {
            var accessMinutes = GetLandAccessMinutes(sourcePostId, port.Name, transportId, landSpeedPercent, portalLoadBufferSeconds);
            if (accessMinutes is null) continue;
            bestMinutes[port.Name] = accessMinutes.Value;
            bestPorts[port.Name] = [port.Name];
            bestBreakdown[port.Name] = accessMinutes.Value > 0
                ? $"{postName(sourcePostId)} → {port.Name}: {accessMinutes.Value:N1} min{PortalNote(sourcePostId, port.Name, portalLoadBufferSeconds)}"
                : string.Empty;
        }

        while (visited.Count < _configuration.Ports.Count)
        {
            var current = _configuration.Ports
                .Where(port => !visited.Contains(port.Name))
                .OrderBy(port => bestMinutes[port.Name])
                .FirstOrDefault();
            if (current is null || bestMinutes[current.Name] == decimal.MaxValue) break;
            visited.Add(current.Name);

            // Ports on the same continent connect only through a known land leg, never for free.
            foreach (var neighboringPort in _configuration.Ports.Where(port =>
                         !visited.Contains(port.Name) &&
                         string.Equals(port.Continent, current.Continent, StringComparison.OrdinalIgnoreCase)))
            {
                var transferMinutes = GetPortTransferMinutes(current.Name, neighboringPort.Name, transportId, landSpeedPercent, portalLoadBufferSeconds);
                if (transferMinutes is null) continue;
                Relax(current.Name, neighboringPort.Name, transferMinutes.Value,
                    AppendLeg(bestBreakdown[current.Name], $"{current.Name} → {neighboringPort.Name}: {transferMinutes.Value:N1} min"),
                    bestMinutes, bestBreakdown, bestPorts);
            }

            var arrivalAtPort = utcNow.ToUniversalTime().AddMinutes((double)bestMinutes[current.Name]);
            foreach (var ferry in _ferries.Where(route =>
                         string.Equals(route.From, current.Name, StringComparison.OrdinalIgnoreCase) &&
                         !visited.Contains(route.To)))
            {
                var sailing = GetNextSailing(ferry, arrivalAtPort, missedDepartureBufferSeconds);
                var description = AppendLeg(bestBreakdown[current.Name],
                    $"{ferry.From} → {ferry.To}: {sailing.DepartureWaitMinutes:N1} min wait + {sailing.SailingMinutes:N1} min sail");
                Relax(current.Name, ferry.To, sailing.DepartureWaitMinutes + sailing.SailingMinutes,
                    description, bestMinutes, bestBreakdown, bestPorts);
            }
        }

        // The exit leg starts from the port where the trip actually arrives.
        ShipTravelEstimate? best = null;
        foreach (var port in _configuration.Ports.Where(port =>
                     string.Equals(port.Continent, destinationContinent, StringComparison.OrdinalIgnoreCase) &&
                     bestMinutes[port.Name] != decimal.MaxValue))
        {
            var exitMinutes = GetLandAccessMinutes(destinationPostId, port.Name, transportId, landSpeedPercent, portalLoadBufferSeconds);
            if (exitMinutes is null) continue;
            var total = bestMinutes[port.Name] + exitMinutes.Value;
            if (best is not null && total >= best.Minutes) continue;
            var breakdown = bestBreakdown[port.Name];
            if (exitMinutes.Value > 0)
                breakdown = AppendLeg(breakdown, $"{port.Name} → {postName(destinationPostId)}: {exitMinutes.Value:N1} min{PortalNote(destinationPostId, port.Name, portalLoadBufferSeconds)}");
            best = new ShipTravelEstimate(total, breakdown, bestPorts[port.Name]);
        }
        return best;
    }

    // A port-to-port land leg is known when some post sits at one port and has a measured distance to the other.
    private decimal? GetPortTransferMinutes(string fromPort, string toPort, int transportId, decimal landSpeedPercent,
        int portalLoadBufferSeconds)
    {
        decimal? best = null;
        foreach (var (postId, portName) in _landAccessDistances.Keys)
        {
            string otherPort;
            if (string.Equals(portName, fromPort, StringComparison.OrdinalIgnoreCase)) otherPort = toPort;
            else if (string.Equals(portName, toPort, StringComparison.OrdinalIgnoreCase)) otherPort = fromPort;
            else continue;
            if (_landAccessDistances[(postId, portName)].DistanceWorldUnits != 0) continue;
            var minutes = GetLandAccessMinutes(postId, otherPort, transportId, landSpeedPercent, portalLoadBufferSeconds);
            if (minutes is not null && (best is null || minutes < best)) best = minutes;
        }
        return best;
    }

    private string PortalNote(int postId, string portName, int bufferSeconds) =>
        bufferSeconds > 0 && _landAccessDistances.TryGetValue((postId, portName), out var access) && access.PortalCount > 0
            ? $" (incl. {access.PortalCount} portal{(access.PortalCount == 1 ? "" : "s")} × {bufferSeconds} s)"
            : string.Empty;

    private static string AppendLeg(string breakdown, string leg) =>
        string.IsNullOrEmpty(breakdown) ? leg : $"{breakdown}{Environment.NewLine}{leg}";

    private List<(ShipSchedulePort Port, decimal Minutes)> GetLandAccessOptions(int postId, string continent, int transportId,
        decimal landSpeedPercent)
    {
        var options = new List<(ShipSchedulePort Port, decimal Minutes)>();
        foreach (var port in _configuration.Ports.Where(port =>
                     string.Equals(port.Continent, continent, StringComparison.OrdinalIgnoreCase)))
        {
            if (!_landAccessDistances.TryGetValue((postId, port.Name), out var access)) continue;
            if (access.DistanceWorldUnits == 0)
            {
                options.Add((port, 0m));
                continue;
            }
            if (_configuration.LandMinutesPerThousandWorldUnits <= 0) continue;
            var handcartMinutes = access.DistanceWorldUnits / 1000m * _configuration.LandMinutesPerThousandWorldUnits;
            var minutes = LegacyRouteEstimates.EstimatedMinutesFromHandcart(handcartMinutes, transportId, access.RegionId);
            if (minutes is not null) options.Add((port, TransportSpeed.ApplyToMinutes(minutes.Value, landSpeedPercent)));
        }
        return options;
    }

    private decimal? GetLandAccessMinutes(int postId, string portName, int transportId, decimal landSpeedPercent,
        int portalLoadBufferSeconds)
    {
        if (!_landAccessDistances.TryGetValue((postId, portName), out var access)) return null;
        if (access.DistanceWorldUnits == 0) return 0m;
        if (_configuration.LandMinutesPerThousandWorldUnits <= 0) return null;
        var minutes = LegacyRouteEstimates.EstimatedMinutesFromHandcart(
            access.DistanceWorldUnits / 1000m * _configuration.LandMinutesPerThousandWorldUnits, transportId, access.RegionId);
        return minutes is null ? null : TransportSpeed.ApplyToMinutes(minutes.Value, landSpeedPercent) +
            RegionalRouteEstimates.PortalLoadMinutes(access.PortalCount, portalLoadBufferSeconds);
    }

    private SailingEstimate GetNextSailing(ScheduledFerry ferry, DateTimeOffset arrivalUtc, int bufferSeconds)
    {
        var currentGameTime = GetErinnTimeOfDay(arrivalUtc, _configuration.PstUtcOffsetHours,
            _configuration.ErinnMinutesPerRealMinute);
        var currentGameMinutes = (decimal)currentGameTime.Ticks / TimeSpan.TicksPerMinute;
        var nextDepartureWait = decimal.MaxValue;
        var boardWaitAtNextDeparture = 0m;

        var bufferErinnMinutes = bufferSeconds / 60m * _configuration.ErinnMinutesPerRealMinute;
        foreach (var departure in ferry.Departures)
        {
            var wait = (departure - currentGameMinutes + MinutesPerErinnDay) % MinutesPerErinnDay;
            if (wait < bufferErinnMinutes) wait += MinutesPerErinnDay;
            if (wait < nextDepartureWait)
            {
                nextDepartureWait = wait;
                boardWaitAtNextDeparture = Math.Max(0m, wait - _configuration.BoardingLeadErinnMinutes);
            }
        }

        var departureWaitMinutes = nextDepartureWait / _configuration.ErinnMinutesPerRealMinute;
        var boardWaitMinutes = boardWaitAtNextDeparture / _configuration.ErinnMinutesPerRealMinute;
        return new SailingEstimate(
            boardWaitMinutes,
            departureWaitMinutes - boardWaitMinutes,
            departureWaitMinutes,
            ferry.TravelDurationErinnMinutes / _configuration.ErinnMinutesPerRealMinute);
    }

    private static void Relax(string current, string next, decimal addedMinutes, string description,
        Dictionary<string, decimal> bestMinutes, Dictionary<string, string> bestBreakdown,
        Dictionary<string, IReadOnlyList<string>> bestPorts)
    {
        var candidate = bestMinutes[current] + addedMinutes;
        if (candidate >= bestMinutes[next]) return;
        bestMinutes[next] = candidate;
        bestBreakdown[next] = description;
        bestPorts[next] = [.. bestPorts[current], next];
    }

    private static decimal ParseDeparture(string value)
    {
        if (!TimeSpan.TryParseExact(value, @"hh\:mm", CultureInfo.InvariantCulture, out var departure) ||
            departure < TimeSpan.Zero || departure >= ErinnDay)
            throw new InvalidDataException($"Invalid Erinn departure time '{value}'.");
        return (decimal)departure.TotalMinutes;
    }

    private sealed record ScheduledFerry(string From, string To, decimal[] Departures, int TravelDurationErinnMinutes);
    private sealed record SailingEstimate(decimal BoardWaitMinutes, decimal BoardingWindowMinutes,
        decimal DepartureWaitMinutes, decimal SailingMinutes);
}