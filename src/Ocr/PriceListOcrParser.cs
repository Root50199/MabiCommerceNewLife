using System.Globalization;
using System.Text.RegularExpressions;

namespace MabiCommerceNewLife;

public sealed class PriceListOcrCandidate
{
    public bool Include { get; set; }
    public int PostId { get; init; }
    public string PostName { get; init; } = string.Empty;
    public decimal Price { get; set; }
    public float Confidence { get; init; }
    public string SourceLine { get; init; } = string.Empty;
    public string PriceSourceText { get; init; } = "OCR sale price";
    public bool IsCrossChecked { get; init; }
    public string ConfidenceText => $"{Confidence:N1}%";
}

public sealed record PriceListOcrResult(
    string RecognizedText,
    float MeanConfidence,
    int ImageWidth,
    IReadOnlyList<OcrPriceTextLine> Lines);

public sealed record OcrPriceWord(string Text, float Confidence, int Left, int Top, int Right, int Bottom);

public sealed record OcrPriceTextLine(string Text, IReadOnlyList<OcrPriceWord> Words);

public sealed record OcrTownRow(int PostId, string PostName, int CenterY, string SourceLine);

public sealed record OcrPriceCellRead(int PostId, string SalePriceText, float SaleConfidence,
    string ProfitText, float ProfitConfidence, bool ProfitIsLoss, string AlternateSaleText = "",
    int SaleVotes = 0, int SaleReads = 0);

public sealed record OcrProductIdentity(int Id, int SourcePostId, string Name);

public sealed record OcrAnchorMatch(int X, int Y, float MeanPixelError);

public sealed record AnchoredPriceListOcrResult(
    string ProductNameText,
    string ProductMetadataText,
    float MeanConfidence,
    OcrAnchorMatch Anchor,
    IReadOnlyList<OcrTownRow> TownRows,
    IReadOnlyList<OcrPriceCellRead> PriceCells,
    bool IsSaleOnlyTable = false);

public sealed record OcrSourcePrice(decimal Price, float Confidence, string SourceText);

public static class PriceListOcrParser
{
    private static readonly Regex PricePattern = new(@"(?<![A-Za-z])\d[\d,.]*(?![A-Za-z])", RegexOptions.Compiled);

    public static OcrSourcePrice? ParseSourcePrice(string metadataText, float confidence)
    {
        ArgumentNullException.ThrowIfNull(metadataText);
        var match = PricePattern.Match(metadataText);
        if (!match.Success || !decimal.TryParse(match.Value.Replace('.', ','), NumberStyles.Number,
                CultureInfo.InvariantCulture, out var price) || price <= 0)
            return null;
        return new OcrSourcePrice(price, Math.Clamp(confidence, 0, 100), metadataText);
    }

    public static OcrProductIdentity? FindUniqueProduct(string recognizedText,
        IEnumerable<OcrProductIdentity> products, IReadOnlySet<int>? visibleDestinationPostIds = null)
    {
        ArgumentNullException.ThrowIfNull(recognizedText);
        ArgumentNullException.ThrowIfNull(products);
        var normalizedText = Normalize(recognizedText);
        var matches = products
            .Where(product => Normalize(product.Name) is { Length: > 0 } name && normalizedText.Contains(name, StringComparison.Ordinal))
            .DistinctBy(product => product.Id)
            .OrderByDescending(product => Normalize(product.Name).Length)
            .ToList();
        if (matches.Count == 0) return FindClosestProduct(normalizedText, products);
        var longestNameLength = Normalize(matches[0].Name).Length;
        var bestMatches = matches.Where(product => Normalize(product.Name).Length == longestNameLength).ToArray();
        if (bestMatches.Length == 1) return bestMatches[0];
        if (visibleDestinationPostIds is not null)
        {
            var absentSourceMatches = bestMatches.Where(product => !visibleDestinationPostIds.Contains(product.SourcePostId)).ToArray();
            if (absentSourceMatches.Length == 1) return absentSourceMatches[0];
        }
        return null;
    }

    public static IReadOnlyList<OcrTownRow> FindTownRows(IEnumerable<OcrPriceTextLine> lines,
        IEnumerable<(int PostId, string PostName)> destinations)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(destinations);
        var destinationList = destinations.ToArray();
        var rows = new List<OcrTownRow>();
        foreach (var line in lines)
        {
            if (line.Words.Count == 0) continue;
            var normalizedLine = Normalize(line.Text);
            var destination = destinationList
                .Where(item => normalizedLine.StartsWith(Normalize(item.PostName), StringComparison.Ordinal))
                .OrderByDescending(item => Normalize(item.PostName).Length)
                .FirstOrDefault();
            if (destination.PostId == 0) continue;
            var top = line.Words.Min(word => word.Top);
            var bottom = line.Words.Max(word => word.Bottom);
            rows.Add(new OcrTownRow(destination.PostId, destination.PostName, (top + bottom) / 2, line.Text));
        }
        return rows.DistinctBy(row => row.PostId).ToArray();
    }

    public static IReadOnlyList<OcrTownRow> FilterTownRowsForDestinations(
        IEnumerable<OcrTownRow> townRows, IEnumerable<int> destinationPostIds, int sourcePostId)
    {
        ArgumentNullException.ThrowIfNull(townRows);
        ArgumentNullException.ThrowIfNull(destinationPostIds);
        var allowed = destinationPostIds.ToHashSet();
        return townRows.Where(row => row.PostId != sourcePostId && allowed.Contains(row.PostId)).ToArray();
    }

    // Barter price lists show only a sale column, so there is no profit to cross-check against. The OCR
    // service reads each row several independent ways; a row is cross-checked only when a strong majority agrees.
    public const int MinimumSaleVotes = 4;
    public const int StrongMajoritySaleVotes = 6;

    // Agreement needs either a near-unanimous read (at most one dissent) or a strong 6-of-8 style majority
    // with no more than two dissenting reads, which tolerates isolated 9/8 glyph confusions.
    public static bool SaleVotesAgree(OcrPriceCellRead cell)
    {
        var dissent = cell.SaleReads - cell.SaleVotes;
        return (cell.SaleVotes >= MinimumSaleVotes && dissent <= 1) ||
            (cell.SaleVotes >= StrongMajoritySaleVotes && dissent <= 2);
    }

    public static IReadOnlyList<PriceListOcrCandidate> ParseSaleOnlyCells(
        IEnumerable<OcrTownRow> townRows, IEnumerable<OcrPriceCellRead> priceCells)
    {
        ArgumentNullException.ThrowIfNull(townRows);
        ArgumentNullException.ThrowIfNull(priceCells);
        var cellsByPost = priceCells.GroupBy(cell => cell.PostId)
            .ToDictionary(group => group.Key, group => group.First());
        var candidates = new List<PriceListOcrCandidate>();
        foreach (var town in townRows)
        {
            if (!cellsByPost.TryGetValue(town.PostId, out var cell) ||
                !TryParsePrice(cell.SalePriceText, out var salePrice) || salePrice <= 0) continue;
            var agrees = SaleVotesAgree(cell);
            var votes = $"{cell.SaleVotes}/{cell.SaleReads} reads agree";
            candidates.Add(new PriceListOcrCandidate
            {
                Include = agrees,
                PostId = town.PostId,
                PostName = town.PostName,
                Price = salePrice,
                Confidence = cell.SaleConfidence,
                SourceLine = string.IsNullOrEmpty(cell.AlternateSaleText)
                    ? $"{town.SourceLine} sale {cell.SalePriceText}"
                    : $"{town.SourceLine} sale {cell.SalePriceText}; other reads {cell.AlternateSaleText}",
                PriceSourceText = agrees ? $"Barter sale; {votes}" : $"Barter sale; only {votes}; verify",
                IsCrossChecked = agrees
            });
        }
        return candidates;
    }

    // Barter sale price = hidden base value x the client's per-post-pair weight. Owner captures matched this
    // within 0.3% at stable towns, while Tara (every capture so far) and recently sold-into towns read up to ~6% low;
    // a 9-vs-8 hundreds misread is only ~2% either way, so the low side stays loose. Barter cannot be
    // discounted. A misread leading or middle digit breaks the model, so such rows lose their cross-check.
    // Larger drops (owner captures: Tara -12.5% and -15.0%) are accepted up to 20% for agreeing reads. Demand
    // lowers the sale price, not the base value, so a dropped town's own implied base can fall below the game's
    // minimum (Wooden Craft Tara: 3,355 vs 3,400). Wrong leading digits and higher-than-model prices stay strict.
    public const decimal BarterValueLowTolerance = 0.08m;
    public const decimal BarterValueHighTolerance = 0.015m;
    public const decimal BarterRangeTolerance = 0.01m;
    public const decimal BarterDemandDropTolerance = 0.20m;

    public static IReadOnlyList<PriceListOcrCandidate> ApplyBarterValueCheck(
        IReadOnlyList<PriceListOcrCandidate> candidates, IReadOnlyDictionary<int, decimal> destinationWeights,
        int minBaseValue, int maxBaseValue)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(destinationWeights);
        var weighted = candidates.Where(item => item.Price > 0 &&
            destinationWeights.TryGetValue(item.PostId, out var weight) && weight > 0).ToArray();
        if (weighted.Length < 3 || minBaseValue <= 0 || maxBaseValue < minBaseValue) return candidates;
        var trusted = weighted.Where(item => item.IsCrossChecked).ToArray();
        var basis = trusted.Length >= 3 ? trusted : weighted;
        var impliedBases = basis.Select(item => item.Price / destinationWeights[item.PostId]).Order().ToArray();
        var baseValue = impliedBases.Length % 2 == 1
            ? impliedBases[impliedBases.Length / 2]
            : (impliedBases[impliedBases.Length / 2 - 1] + impliedBases[impliedBases.Length / 2]) / 2m;
        var baseInRange = baseValue >= minBaseValue * (1 - BarterRangeTolerance) &&
            baseValue <= maxBaseValue * (1 + BarterRangeTolerance);

        return candidates.Select(item =>
        {
            if (!destinationWeights.TryGetValue(item.PostId, out var weight) || weight <= 0 || item.Price <= 0)
                return item;
            var expected = baseValue * weight;
            var deviation = item.Price / expected - 1m;
            var demandDrop = deviation < -BarterValueLowTolerance && deviation >= -BarterDemandDropTolerance &&
                item.IsCrossChecked;
            string? problem = !baseInRange
                ? $"implied base {baseValue:N0} outside game range {minBaseValue:N0}–{maxBaseValue:N0}"
                : deviation > BarterValueHighTolerance || (deviation < -BarterValueLowTolerance && !demandDrop)
                    ? $"{deviation:+0.0%;-0.0%} from game value model (~{expected:N0})"
                    : null;
            return new PriceListOcrCandidate
            {
                Include = problem is null && item.Include,
                PostId = item.PostId,
                PostName = item.PostName,
                Price = item.Price,
                Confidence = item.Confidence,
                SourceLine = item.SourceLine,
                PriceSourceText = problem is null
                    ? demandDrop
                        ? $"{item.PriceSourceText}; {deviation:+0.0%;-0.0%} demand drop, verified reads"
                        : $"{item.PriceSourceText}; fits game value"
                    : $"{item.PriceSourceText.Replace("; verify", string.Empty)}; {problem}; verify",
                IsCrossChecked = problem is null && item.IsCrossChecked
            };
        }).ToArray();
    }

    public static IReadOnlyList<PriceListOcrCandidate> ParsePriceCells(
        IEnumerable<OcrTownRow> townRows,
        IEnumerable<OcrPriceCellRead> priceCells,
        decimal buyPrice,
        bool buyPriceIsEstimated)
    {
        ArgumentNullException.ThrowIfNull(townRows);
        ArgumentNullException.ThrowIfNull(priceCells);
        if (buyPrice <= 0) return [];
        var cellsByPost = priceCells.GroupBy(cell => cell.PostId)
            .ToDictionary(group => group.Key, group => group.First());
        var candidates = new List<PriceListOcrCandidate>();
        foreach (var town in townRows)
        {
            if (!cellsByPost.TryGetValue(town.PostId, out var cell)) continue;
            var hasSale = TryParsePrice(cell.SalePriceText, out var salePrice);
            var hasProfit = TryParseSignedPrice(cell.ProfitText, out var profit);
            if (hasProfit && cell.ProfitIsLoss) profit = -Math.Abs(profit);

            if (hasSale && hasProfit && salePrice - profit == buyPrice)
            {
                candidates.Add(new PriceListOcrCandidate
                {
                    Include = !buyPriceIsEstimated,
                    PostId = town.PostId,
                    PostName = town.PostName,
                    Price = salePrice,
                    Confidence = Math.Min(cell.SaleConfidence, cell.ProfitConfidence),
                    SourceLine = $"{town.SourceLine} sale {cell.SalePriceText}; profit {cell.ProfitText}",
                    PriceSourceText = "OCR sale price cross-checked with profit",
                    IsCrossChecked = true
                });
                continue;
            }

            // Thin 1s in the profit column can merge or vanish (811 read as 8, a 117 loss as 17). When the read profit
            // has the same sign as sale - buy and is that amount with only 1s removed, the sale read is still confirmed.
            if (hasSale && hasProfit && salePrice != buyPrice && profit != 0 &&
                Math.Sign(profit) == Math.Sign(salePrice - buyPrice) &&
                IsProfitWithDroppedOnes(Math.Abs(profit), Math.Abs(salePrice - buyPrice)))
            {
                candidates.Add(new PriceListOcrCandidate
                {
                    Include = !buyPriceIsEstimated,
                    PostId = town.PostId,
                    PostName = town.PostName,
                    Price = salePrice,
                    Confidence = Math.Min(cell.SaleConfidence, cell.ProfitConfidence),
                    SourceLine = $"{town.SourceLine} sale {cell.SalePriceText}; profit {cell.ProfitText}",
                    PriceSourceText = $"OCR sale price cross-checked with profit ({(salePrice > buyPrice ? "profit" : "loss")} {Math.Abs(salePrice - buyPrice):N0} misread (dropped 1s or a 7 read as 9))",
                    IsCrossChecked = true
                });
                continue;
            }

            if (hasProfit)
            {
                var derivedSalePrice = buyPrice + profit;
                if (derivedSalePrice <= 0) continue;
                candidates.Add(new PriceListOcrCandidate
                {
                    Include = false,
                    PostId = town.PostId,
                    PostName = town.PostName,
                    Price = derivedSalePrice,
                    Confidence = cell.ProfitConfidence,
                    SourceLine = $"{town.SourceLine} sale {cell.SalePriceText}; profit {cell.ProfitText}",
                    PriceSourceText = buyPriceIsEstimated
                        ? "Profit-derived; estimated source price; verify both"
                        : hasSale ? "Profit-derived; sale OCR mismatch; verify" : "Profit-derived; verify"
                });
                continue;
            }

            if (!hasSale) continue;
            candidates.Add(new PriceListOcrCandidate
            {
                PostId = town.PostId,
                PostName = town.PostName,
                Price = salePrice,
                Confidence = cell.SaleConfidence,
                SourceLine = $"{town.SourceLine} sale {cell.SalePriceText}",
                PriceSourceText = "OCR sale-price column; profit unavailable"
            });
        }
        return candidates;
    }

    private static bool IsProfitWithDroppedOnes(decimal readProfit, decimal expectedProfit)
    {
        if (readProfit != decimal.Truncate(readProfit) || expectedProfit != decimal.Truncate(expectedProfit))
            return false;
        var read = readProfit.ToString("0", CultureInfo.InvariantCulture);
        var expected = expectedProfit.ToString("0", CultureInfo.InvariantCulture);
        if (read.Length >= expected.Length) return false;
        var expectedCore = expected.Replace("1", string.Empty);
        var readCore = read.Replace("1", string.Empty);
        if (expectedCore == readCore) return IsSubsequence(read, expected);
        // The same blurry profit text can also turn a 7 into a 9 (4171 read as 49); allow one such swap when another digit survives intact.
        return read.IndexOf('1') < 0 && readCore.Length == expectedCore.Length && readCore.Length >= 2 &&
            HasSingleSevenNineSwap(readCore, expectedCore);
    }

    private static bool HasSingleSevenNineSwap(string read, string expected)
    {
        var swaps = 0;
        for (var index = 0; index < read.Length; index++)
        {
            if (read[index] == expected[index]) continue;
            if (!((read[index] == '9' && expected[index] == '7') || (read[index] == '7' && expected[index] == '9'))) return false;
            swaps++;
        }
        return swaps == 1;
    }

    private static bool IsSubsequence(string candidate, string reference)
    {
        var position = 0;
        foreach (var digit in reference)
            if (position < candidate.Length && candidate[position] == digit) position++;
        return position == candidate.Length;
    }

    public static bool CanAutoAccept(IReadOnlyCollection<PriceListOcrCandidate> candidates,
        int visibleTownRowCount, bool buyPriceIsEstimated)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return !buyPriceIsEstimated && candidates.Count > 0 && candidates.Count == visibleTownRowCount &&
            candidates.All(candidate => candidate.IsCrossChecked && candidate.Price > 0);
    }

    public static IReadOnlyList<PriceListOcrCandidate> Parse(string recognizedText,
        IEnumerable<(int PostId, string PostName)> destinations, float meanConfidence)
    {
        ArgumentNullException.ThrowIfNull(recognizedText);
        ArgumentNullException.ThrowIfNull(destinations);

        var lines = recognizedText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var candidates = new List<PriceListOcrCandidate>();
        foreach (var destination in destinations)
        {
            var normalizedName = Normalize(destination.PostName);
            if (normalizedName.Length == 0) continue;

            foreach (var line in lines)
            {
                if (!Normalize(line).StartsWith(normalizedName, StringComparison.Ordinal)) continue;
                var priceMatch = PricePattern.Match(line);
                if (!priceMatch.Success || !decimal.TryParse(priceMatch.Value.Replace('.', ','), NumberStyles.Number,
                        CultureInfo.InvariantCulture, out var price) || price <= 0)
                    continue;

                candidates.Add(new PriceListOcrCandidate
                {
                    PostId = destination.PostId,
                    PostName = destination.PostName,
                    Price = price,
                    Confidence = Math.Clamp(meanConfidence, 0, 100),
                    SourceLine = line
                });
                break;
            }
        }

        return candidates;
    }

    // A title letter misread by OCR (for example g read as q) should not fail a scan. Only used when no name
    // appears exactly: allows one edit per seven letters and requires a single strictly closest good.
    private static OcrProductIdentity? FindClosestProduct(string normalizedText, IEnumerable<OcrProductIdentity> products)
    {
        var scored = products
            .Select(product => (Product: product, Name: Normalize(product.Name)))
            .Where(item => item.Name.Length >= 5)
            .DistinctBy(item => item.Product.Id)
            .Select(item => (item.Product, Distance: ClosestWindowDistance(normalizedText, item.Name),
                Allowed: Math.Max(1, item.Name.Length / 7)))
            .Where(item => item.Distance <= item.Allowed)
            .OrderBy(item => item.Distance)
            .ToArray();
        if (scored.Length == 0) return null;
        return scored.Length == 1 || scored[1].Distance > scored[0].Distance ? scored[0].Product : null;
    }

    private static int ClosestWindowDistance(string text, string name)
    {
        var best = int.MaxValue;
        for (var length = Math.Max(1, name.Length - 1); length <= name.Length + 1; length++)
        for (var start = 0; start + length <= text.Length; start++)
            best = Math.Min(best, EditDistance(text.AsSpan(start, length), name));
        return best;
    }

    private static int EditDistance(ReadOnlySpan<char> left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++) previous[j] = j;
        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[right.Length];
    }

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static bool TryParsePrice(string value, out decimal price)
    {
        price = 0;
        var match = PricePattern.Match(value);
        return match.Success && decimal.TryParse(match.Value.Replace('.', ','), NumberStyles.Number, CultureInfo.InvariantCulture, out price) && price > 0;
    }

    private static bool TryParseSignedPrice(string value, out decimal price)
    {
        price = 0;
        var match = Regex.Match(value, @"[-−]?\d[\d,.]*");
        return match.Success && decimal.TryParse(match.Value.Replace('−', '-').Replace('.', ','), NumberStyles.Number,
            CultureInfo.InvariantCulture, out price);
    }
}