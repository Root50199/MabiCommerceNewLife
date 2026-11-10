using System.Globalization;

namespace MabiCommerceNewLife;

public sealed record BarterRecipeNeed(int GoodsQuantity, int ItemId, string Name, int PerGood);

public sealed record BarterShoppingLine(int ItemId, string Name, long Needed, long Have, decimal? UnitCost = null,
    bool IsUnitCostEditable = true)
{
    public long StillNeed => Math.Max(0, Needed - Have);
}

public static class BarterShoppingList
{
    public const int MaxHave = 9_999_999;

    // Totals each material across the whole barter cart; materials are listed alphabetically.
    public static IReadOnlyList<BarterShoppingLine> Build(IEnumerable<BarterRecipeNeed> needs, Func<int, int> have,
        Func<int, decimal?>? unitCost = null, Func<int, bool>? isUnitCostEditable = null) =>
        needs.Where(need => need.GoodsQuantity > 0 && need.PerGood > 0)
            .GroupBy(need => need.ItemId)
            .Select(group => new BarterShoppingLine(group.Key, group.First().Name,
                group.Sum(need => (long)need.GoodsQuantity * need.PerGood), Math.Max(0, have(group.Key)),
                unitCost?.Invoke(group.Key), isUnitCostEditable?.Invoke(group.Key) ?? true))
            .OrderBy(line => line.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static string FormatForSpreadsheet(IEnumerable<BarterShoppingLine> lines)
    {
        var rows = new List<string> { "Material\tNeeded\tGold each\tHave\tStill need" };
        rows.AddRange(lines.Select(line => string.Join("\t",
            line.Name.Replace("\t", " ").Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " "),
            line.Needed.ToString(CultureInfo.InvariantCulture),
            line.UnitCost?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty,
            line.Have.ToString(CultureInfo.InvariantCulture),
            line.StillNeed.ToString(CultureInfo.InvariantCulture))));
        return string.Join(Environment.NewLine, rows);
    }

    public static bool TryParseUnitCost(string? text, out decimal? unitCost)
    {
        unitCost = null;
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0) return true;
        var cleaned = trimmed.Replace(",", string.Empty);
        if (!decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            return false;
        if (parsed < 0) return false;
        if (parsed > 0) unitCost = parsed;
        return true;
    }

    // Blank means none on hand; otherwise only whole numbers (digit-group commas allowed) from 0 to MaxHave.
    public static bool TryParseHave(string? text, out int have)
    {
        have = 0;
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0) return true;
        var digits = trimmed.Replace(",", string.Empty);
        if (digits.Length == 0 || digits.Length > 7 || !digits.All(ch => ch is >= '0' and <= '9')) return false;
        have = int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        return have <= MaxHave;
    }
}
