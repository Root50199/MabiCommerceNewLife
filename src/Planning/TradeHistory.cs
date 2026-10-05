using System.IO;
using System.Text.Json;

namespace MabiCommerceNewLife;

public sealed record TradeHistoryGood(string Name, int Quantity);

// Ducats is the sale's Ducat reward and TotalGold the converted total (raw Gold + Ducats × rate − material/letter cost).
public sealed record TradeHistoryEntry(
    DateTime Time,
    string Mode,
    string Destination,
    IReadOnlyList<TradeHistoryGood> Goods,
    decimal SaleProceeds,
    decimal RawGold,
    decimal Ducats,
    decimal TotalGold,
    decimal GoldPerDucat,
    string? Letter = null);

public readonly record struct TradeHistoryTotals(int Trades, decimal RawGold, decimal Ducats, decimal TotalGold);

public sealed class TradeHistory
{
    private readonly List<TradeHistoryEntry> _entries;

    public TradeHistory(IEnumerable<TradeHistoryEntry>? entries = null) => _entries = entries?.ToList() ?? [];

    public IReadOnlyList<TradeHistoryEntry> Entries => _entries;

    public void Add(TradeHistoryEntry entry) => _entries.Add(entry ?? throw new ArgumentNullException(nameof(entry)));

    public void Clear() => _entries.Clear();

    // Removes this exact logged sale; identical-looking sales elsewhere in the log are kept.
    public bool Remove(TradeHistoryEntry entry)
    {
        var index = _entries.FindIndex(item => ReferenceEquals(item, entry));
        if (index < 0) return false;
        _entries.RemoveAt(index);
        return true;
    }

    public TradeHistoryTotals Totals => new(
        _entries.Count,
        _entries.Sum(entry => entry.RawGold),
        _entries.Sum(entry => entry.Ducats),
        _entries.Sum(entry => entry.TotalGold));

    public static TradeHistory Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new TradeHistory();
            var entries = JsonSerializer.Deserialize<List<TradeHistoryEntry>>(File.ReadAllText(path));
            return new TradeHistory(entries?.Where(entry => entry is not null));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new TradeHistory();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true }));
    }
}
