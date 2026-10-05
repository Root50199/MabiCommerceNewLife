using TesseractOCR;
using TesseractOCR.Enums;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using PixImage = TesseractOCR.Pix.Image;

namespace MabiCommerceNewLife;

[SupportedOSPlatform("windows6.1")]
public static class PriceListOcrService
{
    private const int AnchorTemplateWidth = 34;
    private const int AnchorTemplateHeight = 28;
    private const int NameRegionX = 41;
    private const int NameRegionY = 9;
    private const int NameRegionWidth = 237;
    private const int NameRegionHeight = 19;
    private const int MetadataRegionX = 30;
    private const int MetadataRegionY = 29;
    private const int MetadataRegionWidth = 257;
    private const int MetadataRegionHeight = 20;
    private const int TownRegionX = 17;
    private const int TownRegionY = 95;
    private const int TownRegionWidth = 85;
    private const int TownRegionHeight = 187;
    private const int SaleCellX = 125;
    private const int SaleCellWidth = 42;
    // The barter sale crop starts where the town-name column ends, covering any right-aligned price width.
    private const int BarterSaleExtraLeft = SaleCellX - (TownRegionX + TownRegionWidth);
    private const int ProfitCellX = 239;
    private const int ProfitCellWidth = 42;
    private const int SmugglerPostId = -1;
    private const int SmugglerRowY = 280;
    private const int SmugglerRowHeight = 20;
    private const int SmugglerSaleX = 88;
    private const int SmugglerSaleWidth = 48;

    public static PriceListOcrResult Recognize(byte[] imageBytes, int imageWidth, string tessdataPath)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        using var engine = new Engine(tessdataPath, Language.English, EngineMode.Default);
        using var image = PixImage.LoadFromMemory(imageBytes);
        string fullPageText;
        float meanConfidence;
        var lines = new List<OcrPriceTextLine>();
        using (var page = engine.Process(image))
        {
            fullPageText = page.Text ?? string.Empty;
            meanConfidence = page.MeanConfidence;
            foreach (var block in page.Layout)
            foreach (var paragraph in block.Paragraphs)
            foreach (var textLine in paragraph.TextLines)
            {
                var words = new List<OcrPriceWord>();
                foreach (var word in textLine.Words)
                {
                    if (word.BoundingBox is not { } bounds) continue;
                    words.Add(new OcrPriceWord(word.Text ?? string.Empty, word.Confidence,
                        bounds.X1, bounds.Y1, bounds.X2, bounds.Y2));
                }
                lines.Add(new OcrPriceTextLine(textLine.Text ?? string.Empty, words));
            }
        }

        var productHeaderText = RecognizeProductHeader(engine, imageBytes);
        var text = string.Join(Environment.NewLine, new[] { productHeaderText, fullPageText }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        return new PriceListOcrResult(text, meanConfidence, imageWidth, lines);
    }

    public static IReadOnlyList<OcrPriceCellRead> ReadPriceCells(byte[] imageBytes, int imageWidth,
        string tessdataPath, IReadOnlyList<OcrTownRow> townRows)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        ArgumentNullException.ThrowIfNull(townRows);
        using var sourceStream = new MemoryStream(imageBytes);
        using var source = new Bitmap(sourceStream);
        using var engine = new Engine(tessdataPath, Language.English, EngineMode.Default);
        engine.SetVariable("tessedit_char_whitelist", "0123456789,.−-");
        engine.SetVariable("classify_bln_numeric_mode", 1);

        var orderedRows = townRows.OrderBy(row => row.CenterY).ToArray();
        var results = new List<OcrPriceCellRead>(orderedRows.Length);
        for (var index = 0; index < orderedRows.Length; index++)
        {
            var row = orderedRows[index];
            var gaps = new[]
            {
                index > 0 ? row.CenterY - orderedRows[index - 1].CenterY : int.MaxValue,
                index + 1 < orderedRows.Length ? orderedRows[index + 1].CenterY - row.CenterY : int.MaxValue
            }.Where(gap => gap > 0).ToArray();
            var rowHeight = gaps.Length == 0 ? 20 : Math.Clamp((int)Math.Round(gaps.Min() * 1.2), 18, 36);
            var top = Math.Clamp(row.CenterY - rowHeight / 2 + 1, 0, Math.Max(0, source.Height - rowHeight));
            var sale = ReadNumericCell(engine, source, 0.45, 0.55, top, rowHeight);
            var profit = ReadNumericCell(engine, source, 0.72, 0.91, top, rowHeight);
            results.Add(new OcrPriceCellRead(row.PostId, sale.Text, sale.Confidence,
                profit.Text, profit.Confidence,
                IsProfitLossColor(source, (int)(imageWidth * 0.76), (int)(imageWidth * 0.97), row.CenterY)));
        }
        return results;
    }

    public static AnchoredPriceListOcrResult RecognizeAnchoredPriceList(byte[] imageBytes,
        byte[] anchorTemplateBytes, string tessdataPath,
        IEnumerable<(int PostId, string PostName)> destinations, bool? saleOnlyTable = null)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        ArgumentNullException.ThrowIfNull(anchorTemplateBytes);
        ArgumentNullException.ThrowIfNull(destinations);

        using var sourceStream = new MemoryStream(imageBytes);
        using var source = new Bitmap(sourceStream);
        using var templateStream = new MemoryStream(anchorTemplateBytes);
        using var template = new Bitmap(templateStream);
        var anchor = FindAnchor(source, template);
        if (anchor.MeanPixelError > 28)
            throw new InvalidDataException("Could not find the Trading Post corner anchor. Select the marked upper-left corner and full price list.");

        using var engine = new Engine(tessdataPath, Language.English, EngineMode.Default);
        using var nameRegion = CropRegion(source, anchor, NameRegionX, NameRegionY, NameRegionWidth, NameRegionHeight);
        using var preparedName = PrepareProductName(nameRegion);
        var nameOcr = RecognizeText(engine, preparedName, 3, PageSegMode.SingleLine);
        using var metadataRegion = CropRegion(source, anchor, MetadataRegionX, MetadataRegionY,
            MetadataRegionWidth, MetadataRegionHeight);
        var metadataOcr = RecognizeText(engine, metadataRegion, 3, PageSegMode.SingleLine);

        var townRegionHeight = Math.Min(TownRegionHeight + 24, source.Height - anchor.Y - TownRegionY);
        using var townRegion = CropRegion(source, anchor, TownRegionX, TownRegionY, TownRegionWidth, townRegionHeight);
        var townOcr = RecognizeText(engine, townRegion, 2, PageSegMode.SingleColumn);
        var townRows = PriceListOcrParser.FindTownRows(townOcr.Lines, destinations)
            .Select(row => row with { CenterY = anchor.Y + TownRegionY + row.CenterY })
            .ToArray();
        var sourcePrice = PriceListOcrParser.ParseSourcePrice(metadataOcr.Text, metadataOcr.Confidence)?.Price;
        // Every barter good shares its name with a trade good, so the table itself decides the layout:
        // trade lists show "N Ducats / Weight N" under the name and profit digits in the right column;
        // barter lists show neither.
        var isSaleOnlyTable = saleOnlyTable ??
            (!LooksLikeTradeMetadata(metadataOcr.Text) && !HasProfitColumnInk(source, anchor, townRows));
        var priceCells = isSaleOnlyTable
            ? ReadSaleOnlyPriceCells(engine, source, anchor, townRows)
            : ReadAnchoredPriceCells(engine, source, anchor, townRows, sourcePrice);
        var confidences = priceCells.Select(cell => cell.SaleConfidence > 0 ? cell.SaleConfidence : cell.ProfitConfidence)
            .Where(confidence => confidence > 0).ToArray();
        var meanConfidence = confidences.Length == 0 ? nameOcr.Confidence : confidences.Average();

        return new AnchoredPriceListOcrResult(nameOcr.Text, metadataOcr.Text, meanConfidence,
            anchor, townRows, priceCells, isSaleOnlyTable);
    }

    private static bool LooksLikeTradeMetadata(string metadataText) =>
        Regex.IsMatch(metadataText, @"(?i)\d.*(ducat|weig)|weig\w*\s*\d");

    private static bool HasProfitColumnInk(Bitmap source, OcrAnchorMatch anchor, IReadOnlyList<OcrTownRow> townRows)
    {
        var height = Math.Min(TownRegionHeight, source.Height - anchor.Y - TownRegionY);
        if (height <= 0 || anchor.X + ProfitCellX + ProfitCellWidth > source.Width) return false;
        using var profitRegion = CropRegion(source, anchor, ProfitCellX, TownRegionY, ProfitCellWidth, height);
        using var prepared = PrepareNumericColumn(profitRegion, true);
        var inkRows = 0;
        for (var pixelY = 0; pixelY < prepared.Height; pixelY++)
            if (Enumerable.Range(0, prepared.Width).Count(pixelX => prepared.GetPixel(pixelX, pixelY).R == 0) >= 2)
                inkRows++;
        // Each visible town row's profit digits are about 8 px tall, so require ink across several rows.
        return inkRows >= Math.Max(8, townRows.Count * 3);
    }

    private static OcrAnchorMatch FindAnchor(Bitmap source, Bitmap template)
    {
        if (template.Width != AnchorTemplateWidth || template.Height != AnchorTemplateHeight)
            throw new InvalidDataException("The Trading Post anchor template has unexpected dimensions.");
        var maxX = Math.Min(source.Width - template.Width, Math.Max(0, source.Width / 4));
        var maxY = Math.Min(source.Height - template.Height, Math.Max(0, source.Height / 4));
        var best = new OcrAnchorMatch(0, 0, float.MaxValue);
        for (var top = 0; top <= maxY; top++)
        for (var left = 0; left <= maxX; left++)
        {
            double error = 0;
            var samples = 0;
            for (var y = 1; y < template.Height - 1; y += 2)
            for (var x = 1; x < template.Width - 1; x += 2)
            {
                var expected = template.GetPixel(x, y);
                var actual = source.GetPixel(left + x, top + y);
                error += (Math.Abs(expected.R - actual.R) + Math.Abs(expected.G - actual.G) + Math.Abs(expected.B - actual.B)) / 3d;
                samples++;
            }
            var meanError = (float)(error / samples);
            if (meanError >= best.MeanPixelError) continue;
            best = new OcrAnchorMatch(left, top, meanError);
            if (meanError <= 1) return best;
        }
        return best;
    }

    private static Bitmap CropRegion(Bitmap source, OcrAnchorMatch anchor,
        int offsetX, int offsetY, int width, int height)
    {
        var left = anchor.X + offsetX;
        var top = anchor.Y + offsetY;
        if (left < 0 || top < 0 || left + width > source.Width || top + height > source.Height)
            throw new InvalidDataException("The capture must include the bracket anchor and all marked price columns.");
        return source.Clone(new Rectangle(left, top, width, height), PixelFormat.Format32bppArgb);
    }

    private static (string Text, float Confidence, IReadOnlyList<OcrPriceTextLine> Lines) RecognizeText(
        Engine engine, Bitmap source, int scale, PageSegMode mode, bool nearestNeighbor = false)
    {
        using var enlarged = new Bitmap(source.Width * scale, source.Height * scale, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(enlarged))
        {
            graphics.InterpolationMode = nearestNeighbor ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = nearestNeighbor ? PixelOffsetMode.Half : PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(0, 0, enlarged.Width, enlarged.Height));
        }
        using var stream = new MemoryStream();
        enlarged.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        using var image = PixImage.LoadFromMemory(stream.ToArray());
        using var page = engine.Process(image, mode);
        var lines = new List<OcrPriceTextLine>();
        foreach (var block in page.Layout)
        foreach (var paragraph in block.Paragraphs)
        foreach (var textLine in paragraph.TextLines)
        {
            var words = new List<OcrPriceWord>();
            foreach (var word in textLine.Words)
            {
                if (word.BoundingBox is not { } bounds) continue;
                words.Add(new OcrPriceWord(word.Text ?? string.Empty, word.Confidence,
                    bounds.X1 / scale, bounds.Y1 / scale, bounds.X2 / scale, bounds.Y2 / scale));
            }
            lines.Add(new OcrPriceTextLine(textLine.Text ?? string.Empty, words));
        }
        return (page.Text ?? string.Empty, page.MeanConfidence, lines);
    }

    private static IReadOnlyList<OcrPriceCellRead> ReadAnchoredPriceCells(Engine engine, Bitmap source,
        OcrAnchorMatch anchor, IReadOnlyList<OcrTownRow> townRows, decimal? sourcePrice)
    {
        engine.SetVariable("tessedit_char_whitelist", "0123456789,.−-");
        engine.SetVariable("classify_bln_numeric_mode", 1);
        using var saleRegion = CropRegion(source, anchor, SaleCellX, TownRegionY,
            SaleCellWidth, TownRegionHeight);
        var profitRegionHeight = townRows.Any(row => row.PostId == SmugglerPostId)
            ? Math.Min(TownRegionHeight + 24, source.Height - anchor.Y - TownRegionY)
            : TownRegionHeight;
        using var profitRegion = CropRegion(source, anchor, ProfitCellX, TownRegionY,
            ProfitCellWidth, profitRegionHeight);
        using var preparedSale = PrepareNumericColumn(saleRegion, false);
        using var preparedProfit = PrepareNumericColumn(profitRegion, true);
        var saleLines = RecognizeText(engine, preparedSale, 4, PageSegMode.SingleBlock).Lines;
        var profitLines = RecognizeText(engine, preparedProfit, 2, PageSegMode.SingleBlock).Lines;
        var saleByTown = MatchNumericLines(saleLines, anchor.Y + TownRegionY - 6, townRows);
        var profitByTown = MatchNumericLines(profitLines, anchor.Y + TownRegionY - 6, townRows);
        if (townRows.Any(row => row.PostId == SmugglerPostId) &&
            anchor.Y + SmugglerRowY + SmugglerRowHeight <= source.Height)
        {
            using var smugglerSale = CropRegion(source, anchor, SmugglerSaleX, SmugglerRowY,
                SmugglerSaleWidth, SmugglerRowHeight);
            using var preparedSmugglerSale = PrepareNumericColumn(smugglerSale, false);
            var smugglerLines = RecognizeText(engine, preparedSmugglerSale, 3, PageSegMode.SingleLine).Lines;
            foreach (var match in MatchNumericLines(smugglerLines, anchor.Y + SmugglerRowY - 6,
                townRows.Where(row => row.PostId == SmugglerPostId).ToArray()))
                saleByTown[match.Key] = match.Value;
        }
        Dictionary<int, NumericLine>? alternateProfitByTown = null;
        Dictionary<int, NumericLine>? alternateSaleByTown = null;
        Dictionary<int, NumericLine>? smallerSaleByTown = null;
        Dictionary<int, NumericLine>? dilatedSaleByTown = null;
        var results = new List<OcrPriceCellRead>(townRows.Count);
        foreach (var town in townRows)
        {
            saleByTown.TryGetValue(town.PostId, out var sale);
            profitByTown.TryGetValue(town.PostId, out var profit);
            NumericLine? recoveredSaleCandidate = null;
            NumericLine? recoveredProfitCandidate = null;
            var isLoss = IsProfitLossColor(source, anchor.X + ProfitCellX,
                anchor.X + ProfitCellX + ProfitCellWidth, profit?.CenterY ?? town.CenterY);
            if (sourcePrice is > 0 && town.PostId == SmugglerPostId &&
                (sale is null || !ProfitMatchesSale(sale, profit, isLoss, sourcePrice.Value)))
            {
                if (anchor.Y + SmugglerRowY + SmugglerRowHeight <= source.Height)
                {
                    using var smugglerSale = CropRegion(source, anchor, SmugglerSaleX, SmugglerRowY,
                        SmugglerSaleWidth, SmugglerRowHeight);
                    using var preparedSmugglerSale = PrepareNumericColumn(smugglerSale, false);
                    var smallerLines = RecognizeText(engine, preparedSmugglerSale, 2, PageSegMode.SingleLine).Lines;
                    var smallerByTown = MatchNumericLines(smallerLines,
                        anchor.Y + SmugglerRowY - 6, [town]);
                    if (smallerByTown.TryGetValue(town.PostId, out var smallerCandidate) &&
                        ProfitMatchesSale(smallerCandidate, profit, isLoss, sourcePrice.Value))
                        sale = smallerCandidate;
                }
                if ((sale is null || !ProfitMatchesSale(sale, profit, isLoss, sourcePrice.Value)) &&
                    anchor.Y + SmugglerRowY + SmugglerRowHeight <= source.Height)
                {
                    using var wrappedSale = CropRegion(source, anchor, SaleCellX, SmugglerRowY,
                        SaleCellWidth, SmugglerRowHeight);
                    using var preparedWrappedSale = PrepareNumericColumn(wrappedSale, false);
                    var wrappedLines = RecognizeText(engine, preparedWrappedSale, 4, PageSegMode.SingleLine).Lines;
                    var wrappedByTown = MatchNumericLines(wrappedLines, anchor.Y + SmugglerRowY - 6, [town]);
                    if (wrappedByTown.TryGetValue(town.PostId, out var wrappedCandidate) &&
                        ProfitMatchesSale(wrappedCandidate, profit, isLoss, sourcePrice.Value))
                        sale = wrappedCandidate;
                }
            }
            if (sourcePrice is > 0 && profit is not null && town.PostId != SmugglerPostId &&
                (sale is null || !ProfitMatchesSale(sale, profit, isLoss, sourcePrice.Value)))
            {
                alternateSaleByTown ??= MatchNumericLines(
                    RecognizeText(engine, preparedSale, 3, PageSegMode.SingleBlock).Lines,
                    anchor.Y + TownRegionY - 6, townRows);
                if (alternateSaleByTown.TryGetValue(town.PostId, out var alternateSale) &&
                    ProfitMatchesSale(alternateSale, profit, isLoss, sourcePrice.Value))
                    sale = alternateSale;
                if (sale is null || !ProfitMatchesSale(sale, profit, isLoss, sourcePrice.Value))
                {
                    smallerSaleByTown ??= MatchNumericLines(
                        RecognizeText(engine, preparedSale, 2, PageSegMode.SingleBlock).Lines,
                        anchor.Y + TownRegionY - 6, townRows);
                    if (smallerSaleByTown.TryGetValue(town.PostId, out var smallerSale) &&
                        ProfitMatchesSale(smallerSale, profit, isLoss, sourcePrice.Value))
                        sale = smallerSale;
                }
                if (sale is null || !ProfitMatchesSale(sale, profit, isLoss, sourcePrice.Value))
                {
                    recoveredSaleCandidate = ReadNumericInkLine(engine, preparedSale,
                        town.CenterY, anchor.Y + TownRegionY - 6);
                    if (recoveredSaleCandidate is not null &&
                        ProfitMatchesSale(recoveredSaleCandidate, profit, isLoss, sourcePrice.Value))
                        sale = recoveredSaleCandidate;
                }
                if (sale is null || !ProfitMatchesSale(sale, profit, isLoss, sourcePrice.Value))
                {
                    // Thin one-pixel client glyphs such as a 9 beside a 0 can read as 8; thicker strokes disambiguate them.
                    if (dilatedSaleByTown is null)
                    {
                        using var dilatedSale = DilateInkHorizontally(preparedSale);
                        dilatedSaleByTown = MatchNumericLines(
                            RecognizeText(engine, dilatedSale, 3, PageSegMode.SingleBlock).Lines,
                            anchor.Y + TownRegionY - 6, townRows);
                    }
                    if (dilatedSaleByTown.TryGetValue(town.PostId, out var dilatedCandidate) &&
                        ProfitMatchesSale(dilatedCandidate, profit, isLoss, sourcePrice.Value))
                        sale = dilatedCandidate;
                }
            }
            if (sourcePrice is > 0 && sale is not null &&
                !ProfitMatchesSale(sale, profit, isLoss, sourcePrice.Value))
            {
                alternateProfitByTown ??= MatchNumericLines(
                    RecognizeText(engine, preparedProfit, 4, PageSegMode.SingleBlock).Lines,
                    anchor.Y + TownRegionY - 6, townRows);
                if (alternateProfitByTown.TryGetValue(town.PostId, out var alternate))
                {
                    var alternateIsLoss = IsProfitLossColor(source, anchor.X + ProfitCellX,
                        anchor.X + ProfitCellX + ProfitCellWidth, alternate.CenterY);
                    if (ProfitMatchesSale(sale, alternate, alternateIsLoss, sourcePrice.Value))
                    {
                        profit = alternate;
                        isLoss = alternateIsLoss;
                    }
                }
                if (!ProfitMatchesSale(sale, profit, isLoss, sourcePrice.Value))
                {
                    recoveredProfitCandidate = ReadNumericInkLine(engine, preparedProfit,
                        town.CenterY, anchor.Y + TownRegionY - 6);
                    if (recoveredProfitCandidate is not null)
                    {
                        var recoveredIsLoss = IsProfitLossColor(source, anchor.X + ProfitCellX,
                            anchor.X + ProfitCellX + ProfitCellWidth, recoveredProfitCandidate.CenterY);
                        if (ProfitMatchesSale(sale, recoveredProfitCandidate, recoveredIsLoss, sourcePrice.Value))
                        {
                            profit = recoveredProfitCandidate;
                            isLoss = recoveredIsLoss;
                        }
                    }
                }
            }
            if (sourcePrice is > 0 && town.PostId != SmugglerPostId &&
                (sale is null || !ProfitMatchesSale(sale, profit, isLoss, sourcePrice.Value)))
            {
                recoveredSaleCandidate ??= ReadNumericInkLine(engine, preparedSale,
                    town.CenterY, anchor.Y + TownRegionY - 6);
                recoveredProfitCandidate ??= ReadNumericInkLine(engine, preparedProfit,
                    town.CenterY, anchor.Y + TownRegionY - 6);
                var saleCandidates = new[] { sale, recoveredSaleCandidate }.OfType<NumericLine>().ToList();
                if (alternateSaleByTown is not null && alternateSaleByTown.TryGetValue(town.PostId, out var alternateSale))
                    saleCandidates.Add(alternateSale);
                if (smallerSaleByTown is not null && smallerSaleByTown.TryGetValue(town.PostId, out var smallerSale))
                    saleCandidates.Add(smallerSale);
                if (dilatedSaleByTown is not null && dilatedSaleByTown.TryGetValue(town.PostId, out var dilatedSale))
                    saleCandidates.Add(dilatedSale);
                var profitCandidates = new[] { profit, recoveredProfitCandidate }.OfType<NumericLine>().ToList();
                if (alternateProfitByTown is not null && alternateProfitByTown.TryGetValue(town.PostId, out var alternateProfit))
                    profitCandidates.Add(alternateProfit);
                var matches = saleCandidates.SelectMany(candidateSale => profitCandidates.Select(candidateProfit =>
                {
                    var candidateIsLoss = IsProfitLossColor(source, anchor.X + ProfitCellX,
                        anchor.X + ProfitCellX + ProfitCellWidth, candidateProfit.CenterY);
                    return (Sale: candidateSale, Profit: candidateProfit, IsLoss: candidateIsLoss);
                })).Where(candidate => ProfitMatchesSale(candidate.Sale, candidate.Profit, candidate.IsLoss, sourcePrice.Value))
                    .GroupBy(candidate => decimal.Parse(candidate.Sale.Text.Replace('.', ','),
                        System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture))
                    .Take(2).ToArray();
                if (matches.Length == 1)
                {
                    var match = matches[0].OrderByDescending(candidate => Math.Min(candidate.Sale.Confidence,
                        candidate.Profit.Confidence)).First();
                    sale = match.Sale;
                    profit = match.Profit;
                    isLoss = match.IsLoss;
                }
            }
            results.Add(new OcrPriceCellRead(town.PostId, sale?.Text ?? string.Empty, sale?.Confidence ?? 0,
                profit?.Text ?? string.Empty, profit?.Confidence ?? 0, isLoss));
        }
        return results;
    }

    // Barter price lists have one sale column, no profit column, and up to twelve towns, so the column
    // extends one row further. With no profit to cross-check against, each row is read by several
    // independent passes (column scales, thickened strokes, per-glyph and single-line crops) and voted.
    private static IReadOnlyList<OcrPriceCellRead> ReadSaleOnlyPriceCells(Engine engine, Bitmap source,
        OcrAnchorMatch anchor, IReadOnlyList<OcrTownRow> townRows)
    {
        engine.SetVariable("tessedit_char_whitelist", "0123456789,.−-");
        engine.SetVariable("classify_bln_numeric_mode", 1);
        var regionHeight = Math.Min(TownRegionHeight + 24, source.Height - anchor.Y - TownRegionY);
        // Barter sale prices reach six digits (e.g. 140,706) and are right-aligned, so the crop extends left.
        using var saleRegion = CropRegion(source, anchor, SaleCellX - BarterSaleExtraLeft, TownRegionY,
            SaleCellWidth + BarterSaleExtraLeft, regionHeight);
        using var preparedSale = PrepareNumericColumn(saleRegion, false);
        ClearDetachedLeftInk(preparedSale, townRows.Select(town => town.CenterY - (anchor.Y + TownRegionY - 6)));
        using var dilatedSale = DilateInkHorizontally(preparedSale);
        var offsetY = anchor.Y + TownRegionY - 6;
        var columnPasses = new[]
        {
            MatchNumericLines(RecognizeText(engine, preparedSale, 4, PageSegMode.SingleBlock).Lines, offsetY, townRows),
            MatchNumericLines(RecognizeText(engine, preparedSale, 3, PageSegMode.SingleBlock).Lines, offsetY, townRows),
            MatchNumericLines(RecognizeText(engine, preparedSale, 2, PageSegMode.SingleBlock).Lines, offsetY, townRows),
            MatchNumericLines(RecognizeText(engine, dilatedSale, 3, PageSegMode.SingleBlock).Lines, offsetY, townRows)
        };
        var results = new List<OcrPriceCellRead>(townRows.Count);
        foreach (var town in townRows)
        {
            var reads = new List<NumericLine?>();
            foreach (var pass in columnPasses)
                reads.Add(pass.TryGetValue(town.PostId, out var line) ? line : null);
            reads.Add(ReadNumericInkLine(engine, preparedSale, town.CenterY, offsetY));
            reads.Add(ReadNumericInkLine(engine, dilatedSale, town.CenterY, offsetY));
            reads.Add(ReadRowLine(engine, preparedSale, town.CenterY - offsetY, 3, false));
            reads.Add(ReadRowLine(engine, preparedSale, town.CenterY - offsetY, 4, true));

            var valid = reads.OfType<NumericLine>()
                .Select(read => (Read: read, Value: NormalizeDigits(read.Text)))
                .Where(read => read.Value.Length > 0)
                .ToList();
            var groups = valid.GroupBy(read => read.Value)
                .OrderByDescending(group => group.Count())
                .ThenByDescending(group => group.Average(read => read.Read.Confidence))
                .ToList();
            if (groups.Count == 0)
            {
                results.Add(new OcrPriceCellRead(town.PostId, string.Empty, 0, string.Empty, 0, false));
                continue;
            }
            var winner = groups[0];
            var winnerKey = winner.Key;
            var winnerVotes = winner.Count();
            var winnerConfidence = winner.Average(read => read.Read.Confidence);
            var shapeNote = string.Empty;
            // Thin 9s are often read as 8s and the game font's 7 as a 1. When the competing reads differ only by
            // such swaps, the glyph shape decides: an 8 closes two loops and a 9 one; a 1 is a narrow stem and a 7
            // a full-width glyph whose top row is a solid bar.
            var swapPositions = groups.Skip(1).Where(group => IsGlyphSwap(group.Key, winnerKey))
                .SelectMany(group => Enumerable.Range(0, winnerKey.Length).Where(index => group.Key[index] != winnerKey[index]))
                .Distinct().ToArray();
            if (swapPositions.Length > 0 &&
                TrySegmentRowGlyphs(preparedSale, town.CenterY - offsetY, out var glyphTop, out var glyphBottom, out var rowGlyphs) &&
                rowGlyphs.Count == winnerKey.Length)
            {
                var resolved = winnerKey.ToCharArray();
                var decided = true;
                var resolvedPairs = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var index in swapPositions)
                {
                    var digit = ClassifySwapGlyph(preparedSale, rowGlyphs[index].Left, rowGlyphs[index].Width,
                        glyphTop, glyphBottom - glyphTop + 1, winnerKey[index]);
                    if (digit is null) { decided = false; break; }
                    resolved[index] = digit.Value;
                    resolvedPairs.Add(winnerKey[index] is '8' or '9' ? "8/9" : "1/7");
                }
                var resolvedKey = new string(resolved);
                var resolvedGroup = groups.FirstOrDefault(group => group.Key == resolvedKey);
                if (decided && resolvedGroup is not null)
                {
                    var compatible = groups.Where(group => group.Key == resolvedKey ||
                        (IsGlyphSwap(group.Key, resolvedKey) && Enumerable.Range(0, resolvedKey.Length)
                            .All(index => group.Key[index] == resolvedKey[index] || swapPositions.Contains(index))))
                        .ToArray();
                    winnerKey = resolvedKey;
                    winnerVotes = compatible.Sum(group => group.Count());
                    winnerConfidence = resolvedGroup.Average(read => read.Read.Confidence);
                    shapeNote = $"{string.Join(", ", resolvedPairs)} confirmed by glyph shape";
                }
            }
            // A read missing glyphs (e.g. 7117 or 117 for 71117, thin 1s merging or vanishing) is a failed read, not a
            // rival value: it is always roughly a power of ten smaller, which the game-value check rejects anyway.
            // Such reads are reported but do not count against the winner's agreement.
            var droppedDigitReads = groups.Where(group => group.Key != winnerKey && IsDroppedDigitRead(group.Key, winnerKey))
                .Sum(group => group.Count());
            var dissent = string.Join(", ", groups.Where(group => group.Key != winnerKey)
                .Select(group => $"{group.Key}×{group.Count()}{(IsDroppedDigitRead(group.Key, winnerKey) ? " (dropped digits)" : string.Empty)}")
                .Append(shapeNote).Where(text => text.Length > 0));
            results.Add(new OcrPriceCellRead(town.PostId, winnerKey, winnerConfidence, string.Empty, 0, false,
                dissent, winnerVotes, valid.Count - droppedDigitReads));
        }
        return results;
    }

    private static bool IsDroppedDigitRead(string candidate, string reference)
    {
        if (candidate.Length >= reference.Length) return false;
        var position = 0;
        foreach (var digit in reference)
            if (position < candidate.Length && candidate[position] == digit) position++;
        return position == candidate.Length;
    }

    private static bool IsGlyphSwap(string candidate, string reference) =>
        candidate.Length == reference.Length && candidate != reference &&
        candidate.Zip(reference).All(pair => pair.First == pair.Second ||
            (pair.First is '8' or '9' && pair.Second is '8' or '9') ||
            (pair.First is '1' or '7' && pair.Second is '1' or '7'));

    private static char? ClassifySwapGlyph(Bitmap prepared, int left, int width, int top, int height, char readAs)
    {
        if (readAs is '8' or '9')
            return CountEnclosedCounters(prepared, left, width, top, height) switch { 2 => '8', 1 => '9', _ => null };
        if (width <= 3) return '1';
        var firstInkRow = Enumerable.Range(top, height).FirstOrDefault(row =>
            Enumerable.Range(left, width).Any(pixelX => prepared.GetPixel(pixelX, row).R == 0), -1);
        if (firstInkRow < 0) return null;
        var topBar = Enumerable.Range(left, width).Count(pixelX => prepared.GetPixel(pixelX, firstInkRow).R == 0);
        return topBar == width ? '7' : null;
    }

    private static string NormalizeDigits(string text) =>
        new string(text.Where(char.IsAsciiDigit).ToArray()).TrimStart('0');

    // Prices are right-aligned, so within each row band any ink separated from the number's cluster by a
    // wide blank gap (stray text-highlight pixels at the left of the widened crop) is removed.
    private static void ClearDetachedLeftInk(Bitmap prepared, IEnumerable<int> rowCenters)
    {
        const int MaxGlyphGap = 5;
        foreach (var center in rowCenters)
        {
            var top = Math.Max(0, center - 6);
            var bottom = Math.Min(prepared.Height, center + 7);
            if (bottom <= top) continue;
            bool ColumnHasInk(int pixelX)
            {
                for (var pixelY = top; pixelY < bottom; pixelY++)
                    if (prepared.GetPixel(pixelX, pixelY).R == 0) return true;
                return false;
            }
            var pixelX = prepared.Width - 1;
            while (pixelX >= 0 && !ColumnHasInk(pixelX)) pixelX--;
            var gap = 0;
            for (; pixelX >= 0; pixelX--)
            {
                gap = ColumnHasInk(pixelX) ? 0 : gap + 1;
                if (gap > MaxGlyphGap) break;
            }
            for (var clearX = 0; clearX <= pixelX; clearX++)
            for (var pixelY = top; pixelY < bottom; pixelY++)
                prepared.SetPixel(clearX, pixelY, Color.White);
        }
    }

    private static NumericLine? ReadRowLine(Engine engine, Bitmap prepared, int localCenter, int scale, bool nearestNeighbor)
    {
        var top = Math.Clamp(localCenter - 10, 0, Math.Max(0, prepared.Height - 20));
        var height = Math.Min(20, prepared.Height - top);
        if (height <= 0) return null;
        using var row = prepared.Clone(new Rectangle(0, top, prepared.Width, height), PixelFormat.Format32bppArgb);
        var read = RecognizeText(engine, row, scale, PageSegMode.SingleLine, nearestNeighbor);
        if (string.IsNullOrWhiteSpace(read.Text)) return null;
        var confidence = read.Lines.SelectMany(line => line.Words)
            .Select(word => word.Confidence).DefaultIfEmpty(read.Confidence).Min();
        return new NumericLine(read.Text.Trim(), confidence, localCenter);
    }

    private sealed record NumericLine(string Text, float Confidence, int CenterY);

    private static NumericLine? ReadNumericInkLine(Engine engine, Bitmap prepared, int centerY, int offsetY)
    {
        if (!TrySegmentRowGlyphs(prepared, centerY - offsetY, out var top, out var bottom, out var glyphs))
            return null;
        using var line = prepared.Clone(new Rectangle(0, top - 3, prepared.Width, bottom - top + 7),
            PixelFormat.Format32bppArgb);
        var read = RecognizeText(engine, line, 2, PageSegMode.SingleLine);
        if (glyphs.Count is >= 2 and <= 6)
        {
            var digits = new List<string>();
            var confidences = new List<float>();
            foreach (var glyphBounds in glyphs)
            {
                using var ink = prepared.Clone(new Rectangle(glyphBounds.Left, top, glyphBounds.Width, bottom - top + 1),
                    PixelFormat.Format32bppArgb);
                using var glyph = new Bitmap(ink.Width + 12, ink.Height + 12, PixelFormat.Format32bppArgb);
                using (var graphics = Graphics.FromImage(glyph))
                {
                    graphics.Clear(Color.White);
                    graphics.DrawImageUnscaled(ink, 6, 6);
                }
                var digitRead = RecognizeText(engine, glyph, 4, PageSegMode.SingleChar);
                var digit = digitRead.Text.Trim();
                if (digit.Length != 1 || digit[0] < '0' || digit[0] > '9') break;
                digits.Add(digit);
                confidences.Add(digitRead.Lines.SelectMany(textLine => textLine.Words)
                    .Select(word => word.Confidence).DefaultIfEmpty(digitRead.Confidence).Min());
            }
            if (digits.Count == glyphs.Count)
                return new NumericLine(string.Concat(digits), confidences.Min(), offsetY + (top + bottom) / 2);
        }
        if (string.IsNullOrWhiteSpace(read.Text)) return null;
        var confidence = read.Lines.SelectMany(textLine => textLine.Words)
            .Select(word => word.Confidence).DefaultIfEmpty(read.Confidence).Min();
        return new NumericLine(read.Text.Trim(), confidence, offsetY + (top + bottom) / 2);
    }

    // Finds the digit band nearest a row center and splits it into glyph columns; short marks such as the
    // thousands comma are dropped because they cover less than half the band height.
    private static bool TrySegmentRowGlyphs(Bitmap prepared, int localCenter, out int top, out int bottom,
        out List<(int Left, int Width)> glyphs)
    {
        top = bottom = 0;
        glyphs = [];
        bool RowHasInk(int row)
        {
            for (var pixelX = 0; pixelX < prepared.Width; pixelX++)
                if (prepared.GetPixel(pixelX, row).R == 0) return true;
            return false;
        }

        var searchTop = Math.Max(6, localCenter - 7);
        var searchBottom = Math.Min(prepared.Height - 6, localCenter + 8);
        if (searchTop >= searchBottom) return false;
        var seed = Enumerable.Range(searchTop, searchBottom - searchTop)
            .OrderBy(row => Math.Abs(row - localCenter)).FirstOrDefault(RowHasInk, -1);
        if (seed < 0) return false;
        top = seed;
        bottom = seed;
        while (top > 6 && RowHasInk(top - 1)) top--;
        while (bottom < prepared.Height - 7 && RowHasInk(bottom + 1)) bottom++;
        if (bottom - top + 1 > 14) return false;
        var bandTop = top;
        var bandHeight = bottom - top + 1;
        var glyphLeft = -1;
        for (var pixelX = 0; pixelX <= prepared.Width; pixelX++)
        {
            var hasInk = pixelX < prepared.Width && Enumerable.Range(bandTop, bandHeight)
                .Any(row => prepared.GetPixel(pixelX, row).R == 0);
            if (hasInk && glyphLeft < 0) glyphLeft = pixelX;
            if (!hasInk && glyphLeft >= 0)
            {
                glyphs.Add((glyphLeft, pixelX - glyphLeft));
                glyphLeft = -1;
            }
        }
        glyphs.RemoveAll(glyphBounds => Enumerable.Range(bandTop, bandHeight)
            .Count(row => Enumerable.Range(glyphBounds.Left, glyphBounds.Width)
                .Any(pixelX => prepared.GetPixel(pixelX, row).R == 0)) < Math.Max(2, (bandHeight + 1) / 2));
        return true;
    }

    // Counts background regions fully enclosed by a glyph's ink. Background is flooded 4-connected from a
    // one-pixel white margin, so diagonal ink steps still close a loop. An 8 encloses two counters, a 9 one.
    private static int CountEnclosedCounters(Bitmap prepared, int left, int width, int top, int height)
    {
        var gridWidth = width + 2;
        var gridHeight = height + 2;
        var ink = new bool[gridWidth, gridHeight];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            ink[x + 1, y + 1] = prepared.GetPixel(left + x, top + y).R == 0;
        var seen = new bool[gridWidth, gridHeight];
        var regions = 0;
        for (var startY = 0; startY < gridHeight; startY++)
        for (var startX = 0; startX < gridWidth; startX++)
        {
            if (ink[startX, startY] || seen[startX, startY]) continue;
            var touchesEdge = false;
            var pending = new Stack<(int X, int Y)>();
            pending.Push((startX, startY));
            seen[startX, startY] = true;
            while (pending.Count > 0)
            {
                var (x, y) = pending.Pop();
                if (x == 0 || y == 0 || x == gridWidth - 1 || y == gridHeight - 1) touchesEdge = true;
                foreach (var (nextX, nextY) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
                {
                    if (nextX < 0 || nextY < 0 || nextX >= gridWidth || nextY >= gridHeight) continue;
                    if (ink[nextX, nextY] || seen[nextX, nextY]) continue;
                    seen[nextX, nextY] = true;
                    pending.Push((nextX, nextY));
                }
            }
            if (!touchesEdge) regions++;
        }
        return regions;
    }

    private static Bitmap PrepareProductName(Bitmap source)
    {
        var prepared = new Bitmap(source.Width + 12, source.Height + 12, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(prepared)) graphics.Clear(Color.White);
        for (var pixelY = 0; pixelY < source.Height; pixelY++)
        for (var pixelX = 0; pixelX < source.Width; pixelX++)
        {
            var pixel = source.GetPixel(pixelX, pixelY);
            var brightestChannel = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B));
            prepared.SetPixel(pixelX + 6, pixelY + 6, brightestChannel <= 100 ? Color.Black : Color.White);
        }
        return prepared;
    }

    private static bool ProfitMatchesSale(NumericLine sale, NumericLine? profit, bool isLoss, decimal sourcePrice)
    {
        if (profit is null || !decimal.TryParse(sale.Text.Replace('.', ','), System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var salePrice) || salePrice <= 0 ||
            !decimal.TryParse(profit.Text.Replace('.', ','), System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var profitValue))
            return false;
        if (isLoss) profitValue = -Math.Abs(profitValue);
        return salePrice - profitValue == sourcePrice;
    }

    private static Bitmap PrepareNumericColumn(Bitmap source, bool coloredDigits)
    {
        var prepared = new Bitmap(source.Width + 12, source.Height + 12, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(prepared)) graphics.Clear(Color.White);
        for (var pixelY = 0; pixelY < source.Height; pixelY++)
        for (var pixelX = 0; pixelX < source.Width; pixelX++)
        {
            var pixel = source.GetPixel(pixelX, pixelY);
            var isColoredDigit = (pixel.R > 100 && pixel.R > pixel.G * 1.3 && pixel.R > pixel.B * 1.3) ||
                (pixel.B > 100 && pixel.B > pixel.R * 1.2 && pixel.B > pixel.G * 1.1);
            var brightestChannel = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B));
            var darkestChannel = Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
            var isWhiteDigit = darkestChannel >= 170 && brightestChannel - darkestChannel <= 8;
            var isBlackDigit = brightestChannel <= 140 && brightestChannel - darkestChannel <= 24;
            prepared.SetPixel(pixelX + 6, pixelY + 6,
                (coloredDigits ? isColoredDigit || isBlackDigit : isWhiteDigit) ? Color.Black : Color.White);
        }
        return prepared;
    }

    private static Bitmap DilateInkHorizontally(Bitmap prepared)
    {
        var dilated = new Bitmap(prepared.Width, prepared.Height, PixelFormat.Format32bppArgb);
        for (var pixelY = 0; pixelY < prepared.Height; pixelY++)
        for (var pixelX = 0; pixelX < prepared.Width; pixelX++)
        {
            var hasInk = prepared.GetPixel(pixelX, pixelY).R == 0 ||
                (pixelX + 1 < prepared.Width && prepared.GetPixel(pixelX + 1, pixelY).R == 0);
            dilated.SetPixel(pixelX, pixelY, hasInk ? Color.Black : Color.White);
        }
        return dilated;
    }

    private static Dictionary<int, NumericLine> MatchNumericLines(
        IReadOnlyList<OcrPriceTextLine> lines, int offsetY, IReadOnlyList<OcrTownRow> townRows)
    {
        var matches = new List<(int PostId, NumericLine Line)>();
        foreach (var line in lines)
        {
            if (line.Words.Count == 0 || string.IsNullOrWhiteSpace(line.Text)) continue;
            var centerY = offsetY + (line.Words.Min(word => word.Top) + line.Words.Max(word => word.Bottom)) / 2;
            var nearestTowns = townRows.OrderBy(town => Math.Abs(town.CenterY - centerY)).Take(2).ToArray();
            if (nearestTowns.Length == 0 || Math.Abs(nearestTowns[0].CenterY - centerY) > 7) continue;
            if (nearestTowns.Length > 1 && Math.Abs(nearestTowns[0].CenterY - centerY) ==
                Math.Abs(nearestTowns[1].CenterY - centerY)) continue;
            matches.Add((nearestTowns[0].PostId,
                new NumericLine(line.Text.Trim(), line.Words.Min(word => word.Confidence), centerY)));
        }
        return matches.GroupBy(match => match.PostId).Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Line);
    }

    private static (string Text, float Confidence) ReadNumericCell(Engine engine, Bitmap source,
        double leftRatio, double rightRatio, int top, int height)
    {
        var left = Math.Clamp((int)Math.Round(source.Width * leftRatio), 0, source.Width - 1);
        var right = Math.Clamp((int)Math.Round(source.Width * rightRatio), left + 1, source.Width);
        var clippedTop = Math.Clamp(top, 0, source.Height - 1);
        var clippedHeight = Math.Clamp(height, 1, source.Height - clippedTop);
        using var crop = source.Clone(new Rectangle(left, clippedTop, right - left, clippedHeight), PixelFormat.Format32bppArgb);
        using var enlarged = new Bitmap(crop.Width * 4, crop.Height * 4, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(enlarged))
        {
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(crop, new Rectangle(0, 0, enlarged.Width, enlarged.Height));
        }

        using var stream = new MemoryStream();
        enlarged.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        using var image = PixImage.LoadFromMemory(stream.ToArray());
        using var page = engine.Process(image, PageSegMode.SingleWord);
        return (page.Text?.Trim() ?? string.Empty, page.MeanConfidence);
    }

    private static bool IsProfitLossColor(Bitmap image, int startX, int endX, int centerY)
    {
        startX = Math.Clamp(startX, 0, image.Width - 1);
        endX = Math.Clamp(endX, startX + 1, image.Width);
        var top = Math.Clamp(centerY - 5, 0, image.Height - 1);
        var bottom = Math.Clamp(centerY + 6, top + 1, image.Height);
        var redPixels = 0;
        var bluePixels = 0;
        for (var y = top; y < bottom; y++)
        for (var x = startX; x < endX; x++)
        {
            var pixel = image.GetPixel(x, y);
            if (pixel.R > 100 && pixel.R > pixel.B * 1.3) redPixels++;
            if (pixel.B > 100 && pixel.B > pixel.R * 1.2) bluePixels++;
        }
        return bluePixels > redPixels;
    }

    [SupportedOSPlatform("windows6.1")]
    private static string RecognizeProductHeader(Engine engine, byte[] imageBytes)
    {
        using var sourceStream = new MemoryStream(imageBytes);
        using var source = new Bitmap(sourceStream);
        var headerHeight = Math.Min(source.Height, Math.Clamp(source.Height / 5, 32, 90));
        var scale = 3;
        using var header = new Bitmap(source.Width * scale, headerHeight * scale, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(header))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(0, 0, header.Width, header.Height),
                new Rectangle(0, 0, source.Width, headerHeight), GraphicsUnit.Pixel);
        }
        using var headerStream = new MemoryStream();
        header.Save(headerStream, System.Drawing.Imaging.ImageFormat.Png);
        using var headerImage = PixImage.LoadFromMemory(headerStream.ToArray());
        using var headerPage = engine.Process(headerImage, PageSegMode.SparseText);
        return headerPage.Text ?? string.Empty;
    }
}
