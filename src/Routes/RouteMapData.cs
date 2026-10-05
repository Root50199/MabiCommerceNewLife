using System.IO;
using System.Text.Json;

namespace MabiCommerceNewLife;

public readonly record struct RouteMapPoint(double X, double Y);

public sealed class RouteMapRegion
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed class RouteMapLeg
{
    public int Region { get; set; }
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string FromName { get; set; } = string.Empty;
    public string ToName { get; set; } = string.Empty;
    public double Distance { get; set; }
    // Normalised 0..1 positions on the region's minimap image.
    public List<RouteMapPoint> Points { get; set; } = [];
}

// Where the in-game map window shows a region at its most zoomed-out view, in absolute screen pixels: the visible map
// area (View) and where the region's route image (0..1 coordinates) lands inside it.
public sealed class RouteMapGameLayout
{
    public int Region { get; set; }
    public double ViewWidth { get; set; }
    public double ViewHeight { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

public sealed class RouteMapGameLayoutFile
{
    public List<RouteMapGameLayout> Regions { get; set; } = [];
}

public sealed class RouteMapFile
{
    public List<RouteMapRegion> Regions { get; set; } = [];
    public List<RouteMapLeg> Legs { get; set; } = [];
    public List<List<string>> Links { get; set; } = [];
    public Dictionary<string, string> Posts { get; set; } = [];
    public Dictionary<string, string> Ports { get; set; } = [];
}

// One minimap page of a trade route: the consecutive legs walked in a single region, oriented in travel direction.
public sealed record RouteMapPage(RouteMapRegion Region, IReadOnlyList<IReadOnlyList<RouteMapPoint>> Lines,
    string StartName, string EndName, string? ArrivalNote);

// Title names the whole trip (start → barter second stop → destination) rather than the current page's map.
public sealed record RouteMapPlan(IReadOnlyList<RouteMapPage> Pages, IReadOnlyList<string> Notes, string Title = "")
{
    public static RouteMapPlan Join(IEnumerable<RouteMapPlan> parts, string title)
    {
        var list = parts.ToList();
        return new RouteMapPlan(MergeSameRegion(list.SelectMany(part => part.Pages)), [.. list.SelectMany(part => part.Notes).Distinct()], title);
    }

    // A barter stop inside one map (e.g. both Zardine legs of a mixed barter) reads as a single line set on that map.
    private static List<RouteMapPage> MergeSameRegion(IEnumerable<RouteMapPage> pages)
    {
        var merged = new List<RouteMapPage>();
        foreach (var page in pages)
        {
            if (merged.Count > 0 && merged[^1] is var last && last.Region.Id == page.Region.Id)
            {
                var notes = new[] { last.ArrivalNote, page.ArrivalNote }.Where(note => !string.IsNullOrEmpty(note)).Distinct().ToList();
                merged[^1] = last with
                {
                    Lines = [.. last.Lines, .. page.Lines],
                    EndName = page.EndName,
                    ArrivalNote = notes.Count == 0 ? null : string.Join("  ·  ", notes)
                };
                continue;
            }
            merged.Add(page);
        }
        return merged;
    }
}

public sealed class RouteMapData
{
    private readonly Dictionary<int, RouteMapRegion> _regions;
    private readonly Dictionary<string, List<(string Next, RouteMapLeg? Leg)>> _edges = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _postNodes = [];
    private readonly Dictionary<string, string> _portNodes;

    public RouteMapData(RouteMapFile file)
    {
        _regions = file.Regions.ToDictionary(region => region.Id);
        foreach (var leg in file.Legs)
        {
            if (!_regions.ContainsKey(leg.Region) || leg.Points.Count < 2 || leg.Distance < 0)
                throw new InvalidDataException($"Route map leg {leg.From} to {leg.To} is invalid.");
            AddEdge(leg.From, leg.To, leg);
            AddEdge(leg.To, leg.From, leg);
        }
        foreach (var link in file.Links)
        {
            if (link.Count != 2) throw new InvalidDataException("Route map links must join exactly two nodes.");
            AddEdge(link[0], link[1], null);
            AddEdge(link[1], link[0], null);
        }
        foreach (var (postId, node) in file.Posts)
            _postNodes[int.Parse(postId, System.Globalization.CultureInfo.InvariantCulture)] = node;
        _portNodes = new Dictionary<string, string>(file.Ports, StringComparer.OrdinalIgnoreCase);
        Legs = file.Legs;
    }

    public IReadOnlyList<RouteMapLeg> Legs { get; }

    private Dictionary<int, RouteMapGameLayout> _gameLayouts = [];

    public RouteMapGameLayout? GameLayout(int regionId) => _gameLayouts.GetValueOrDefault(regionId);

    // The optional game-map-layout.json next to the route file supplies in-game map placement for lines mode.
    public static RouteMapData? Load(string path)
    {
        if (!File.Exists(path)) return null;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var file = JsonSerializer.Deserialize<RouteMapFile>(File.ReadAllText(path), options)
            ?? throw new InvalidDataException("The route map file is empty.");
        var data = new RouteMapData(file);
        var layoutPath = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, "game-map-layout.json");
        if (File.Exists(layoutPath))
        {
            var layouts = JsonSerializer.Deserialize<RouteMapGameLayoutFile>(File.ReadAllText(layoutPath), options)?.Regions ?? [];
            if (layouts.Any(layout => layout.ViewWidth <= 0 || layout.ViewHeight <= 0 || layout.Width <= 0 || layout.Height <= 0))
                throw new InvalidDataException("The game map layout file has an empty view or image size.");
            data._gameLayouts = layouts.ToDictionary(layout => layout.Region);
        }
        return data;
    }

    public IEnumerable<RouteMapLeg> LegsInRegion(int regionId) => Legs.Where(leg => leg.Region == regionId);

    // Ports come from ShipTravelEstimate.Ports; null or empty means a same-continent land trip.
    public RouteMapPlan Plan(int sourcePostId, int destinationPostId, IReadOnlyList<string>? ports = null,
        Func<int, string>? postName = null)
    {
        var notes = new List<string>();
        if (!_postNodes.TryGetValue(sourcePostId, out var source) || !_postNodes.TryGetValue(destinationPostId, out var destination))
            return new RouteMapPlan([], ["No mapped route data for this trade post."]);

        // Each stop is a node and, when a ship carries the trip there, the port it sailed from.
        var stops = new List<(string Node, string? ShipFrom)> { (source, null) };
        ports ??= [];
        for (var index = 0; index < ports.Count; index++)
        {
            var port = ports[index];
            var shipFrom = index > 0 && Continent(ports[index - 1]) != Continent(port) ? ports[index - 1] : null;
            var shipTo = index + 1 < ports.Count && Continent(ports[index + 1]) != Continent(port) ? ports[index + 1] : null;
            // A port's dock can depend on which ship is used there (Cobh has separate Belvast and Iria docks).
            var arrival = PortNode(port, shipFrom ?? shipTo);
            stops.Add((arrival, shipFrom));
            if (shipTo is not null && PortNode(port, shipTo) is var departure && departure != arrival)
                stops.Add((departure, null));
        }
        stops.Add((destination, null));

        var pages = new List<RouteMapPage>();
        string? pendingArrival = null;
        for (var index = 1; index < stops.Count; index++)
        {
            var (from, _) = stops[index - 1];
            var (to, shipFrom) = stops[index];
            if (from == to) continue;
            if (shipFrom is not null)
            {
                pendingArrival = $"Arrive by ship from {shipFrom}";
                continue;
            }
            var path = FindPath(from, to);
            if (path is null)
            {
                notes.Add($"No mapped path from {NodeName(from, postName)} to {NodeName(to, postName)}.");
                continue;
            }
            foreach (var (leg, forward) in path)
            {
                var line = forward ? leg.Points : Enumerable.Reverse(leg.Points).ToList();
                var startName = Display(forward ? leg.FromName : leg.ToName, forward ? leg.From : leg.To, postName);
                var endName = Display(forward ? leg.ToName : leg.FromName, forward ? leg.To : leg.From, postName);
                if (pages.Count > 0 && pendingArrival is null && pages[^1].Region.Id == leg.Region)
                {
                    var previous = pages[^1];
                    pages[^1] = previous with { Lines = [.. previous.Lines, line], EndName = endName };
                }
                else
                {
                    pages.Add(new RouteMapPage(_regions[leg.Region], [line], startName, endName, pendingArrival));
                    pendingArrival = null;
                }
            }
        }
        return new RouteMapPlan(pages, notes);
    }

    private string PortNode(string port, string? partner) =>
        partner is not null && _portNodes.TryGetValue($"{port}|{partner}", out var specific) ? specific
        : _portNodes.TryGetValue(port, out var node) ? node
        : throw new InvalidDataException($"Route map has no node for port '{port}'.");

    // Node keys are prefixed by dataset (U:, B:, I:), which is one per continent.
    private char Continent(string port) => PortNode(port, null)[0];

    private string Display(string name, string node, Func<int, string>? postName)
    {
        if (postName is null) return name;
        foreach (var (postId, postNode) in _postNodes)
            if (postNode == node) return postName(postId);
        return name;
    }

    private string NodeName(string node, Func<int, string>? postName)
    {
        foreach (var leg in Legs)
        {
            if (leg.From == node) return Display(leg.FromName, node, postName);
            if (leg.To == node) return Display(leg.ToName, node, postName);
        }
        return Display(node, node, postName);
    }

    private List<(RouteMapLeg Leg, bool Forward)>? FindPath(string from, string to)
    {
        var distance = new Dictionary<string, double>(StringComparer.Ordinal) { [from] = 0 };
        var previous = new Dictionary<string, (string Node, RouteMapLeg? Leg)>(StringComparer.Ordinal);
        var queue = new PriorityQueue<string, double>();
        queue.Enqueue(from, 0);
        while (queue.TryDequeue(out var node, out var cost))
        {
            if (cost > distance[node]) continue;
            if (node == to) break;
            if (!_edges.TryGetValue(node, out var edges)) continue;
            foreach (var (next, leg) in edges)
            {
                var candidate = cost + (leg?.Distance ?? 0);
                if (distance.TryGetValue(next, out var known) && known <= candidate) continue;
                distance[next] = candidate;
                previous[next] = (node, leg);
                queue.Enqueue(next, candidate);
            }
        }
        if (!distance.ContainsKey(to)) return null;

        var path = new List<(RouteMapLeg, bool)>();
        for (var node = to; node != from;)
        {
            var (prior, leg) = previous[node];
            if (leg is not null) path.Add((leg, leg.From == prior));
            node = prior;
        }
        path.Reverse();
        return path;
    }

    private void AddEdge(string from, string to, RouteMapLeg? leg)
    {
        if (!_edges.TryGetValue(from, out var list)) _edges[from] = list = [];
        list.Add((to, leg));
    }
}
