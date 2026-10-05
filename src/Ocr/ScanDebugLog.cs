using System.Globalization;
using System.IO;
using System.Text;

namespace MabiCommerceNewLife;

// Dev-only, opt-in debug record of price-list scans: auto-accept-worthy reads in the workspace's ScanDebug folder and
// failed reads in ScanDebug\Failed, each as the captured region PNG plus the OCR text outputs.
// Not intended for release: outside a source checkout (no MabiCommerceNewLife.csproj above the app) nothing is saved.
public static class ScanDebugLog
{
    private static readonly Lazy<string?> WorkspaceFolder = new(() =>
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "MabiCommerceNewLife.csproj")))
                return Path.Combine(directory.FullName, "ScanDebug");
        return null;
    });

    public static string? FolderPath => WorkspaceFolder.Value;

    public static string Describe(AnchoredPriceListOcrResult result)
    {
        var text = new StringBuilder();
        text.AppendLine($"Layout: {(result.IsSaleOnlyTable ? "barter (sale only)" : "trade (sale + profit)")}");
        text.AppendLine($"Product name OCR: {result.ProductNameText.ReplaceLineEndings(" ").Trim()}");
        text.AppendLine($"Metadata OCR: {result.ProductMetadataText.ReplaceLineEndings(" ").Trim()}");
        text.AppendLine($"Mean confidence: {result.MeanConfidence:N1}%");
        text.AppendLine($"Anchor: x={result.Anchor.X} y={result.Anchor.Y} error={result.Anchor.MeanPixelError:N2}");
        text.AppendLine();
        text.AppendLine("Town rows (post id, name, center y, OCR line):");
        foreach (var row in result.TownRows)
            text.AppendLine($"  {row.PostId}\t{row.PostName}\t{row.CenterY}\t{row.SourceLine}");
        text.AppendLine();
        text.AppendLine("Price cells (post id: sale [votes/reads] {other reads} | profit):");
        foreach (var cell in result.PriceCells)
        {
            var votes = cell.SaleReads > 0 ? $" [{cell.SaleVotes}/{cell.SaleReads}]" : string.Empty;
            var others = cell.AlternateSaleText.Length > 0 ? $" {{{cell.AlternateSaleText}}}" : string.Empty;
            var profit = cell.ProfitText.Length > 0
                ? $" | profit {(cell.ProfitIsLoss ? "-" : "+")}{cell.ProfitText} ({cell.ProfitConfidence:N1}%)"
                : string.Empty;
            text.AppendLine($"  {cell.PostId}: sale '{cell.SalePriceText}' ({cell.SaleConfidence:N1}%){votes}{others}{profit}");
        }
        return text.ToString();
    }

    public static string DescribeCandidates(string productName, string sourcePostName, OcrSourcePrice? sourcePrice,
        IEnumerable<PriceListOcrCandidate> candidates)
    {
        var text = new StringBuilder();
        text.AppendLine($"Identified good: {productName} from {sourcePostName}");
        if (sourcePrice is not null)
            text.AppendLine($"Source price: {sourcePrice.Price.ToString("N0", CultureInfo.InvariantCulture)} ({sourcePrice.SourceText})");
        text.AppendLine("Candidates (town, price, applied, cross-checked, check):");
        foreach (var candidate in candidates)
            text.AppendLine($"  {candidate.PostName}\t{candidate.Price.ToString("N0", CultureInfo.InvariantCulture)}\t" +
                $"{(candidate.Include ? "apply" : "skip")}\t{(candidate.IsCrossChecked ? "checked" : "unchecked")}\t{candidate.PriceSourceText}");
        return text.ToString();
    }

    // Returns the saved file stem, or null when writing failed; a debug write must never break a scan.
    // Failed reads go to a subfolder so the auto-accept set stays a clean verification record.
    public static string? Save(byte[] capturePng, string productName, string details, string? subfolder = null)
    {
        try
        {
            if (FolderPath is not { } root) return null;
            var folder = subfolder is null ? root : Path.Combine(root, subfolder);
            Directory.CreateDirectory(folder);
            var safeName = string.Concat(productName.Select(character =>
                Path.GetInvalidFileNameChars().Contains(character) || character == ' ' ? '_' : character));
            var stem = Path.Combine(folder, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}_{safeName}");
            File.WriteAllBytes(stem + ".png", capturePng);
            File.WriteAllText(stem + ".txt", $"Scanned {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}{Environment.NewLine}{details}");
            return stem;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
