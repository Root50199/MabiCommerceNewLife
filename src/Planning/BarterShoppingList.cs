using System.Globalization;

namespace MabiCommerceNewLife;

public sealed record BarterRecipeNeed(int GoodsQuantity, int ItemId, string Name, int PerGood);

public sealed record BarterShoppingLine(int ItemId, string Name, long Needed, long Have)
{
    public long StillNeed => Math.Max(0, Needed - Have);
}

public static class BarterShoppingList
{
    public const int MaxHave = 9_999_999;

    // Totals each material across the whole barter cart; materials are listed alphabetically.
    public static IReadOnlyList<BarterShoppingLine> Build(IEnumerable<BarterRecipeNeed> needs, Func<int, int> have) =>
        needs.Where(need => need.GoodsQuantity > 0 && need.PerGood > 0)
            .GroupBy(need => need.ItemId)
            .Select(group => new BarterShoppingLine(group.Key, group.First().Name,
                group.Sum(need => (long)need.GoodsQuantity * need.PerGood), Math.Max(0, have(group.Key))))
            .OrderBy(line => line.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static string FormatForSpreadsheet(IEnumerable<BarterShoppingLine> lines)
    {
        var rows = new List<string> { "Material\tNeeded\tHave\tStill need" };
        rows.AddRange(lines.Select(line => string.Join("\t",
            line.Name.Replace("\t", " ").Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " "),
            line.Needed.ToString(CultureInfo.InvariantCulture),
            line.Have.ToString(CultureInfo.InvariantCulture),
            line.StillNeed.ToString(CultureInfo.InvariantCulture))));
        return string.Join(Environment.NewLine, rows);
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
