using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using MabiCommerceNewLife;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalculatorTests;

[TestClass]
public sealed class HighlanderOreOcrTests
{
    [TestMethod]
    [SupportedOSPlatform("windows6.1")]
    public void BelvastProfitAndSaleCrossCheckWithoutDecorativeTownBrackets()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ocr", "Client_HighlanderOreBelvast.png");
        if (!File.Exists(path)) Assert.Inconclusive("Owner screenshot is local only.");
        using var source = new Bitmap(path);
        using var crop = source.Clone(new Rectangle(355, 88, 315, 310), PixelFormat.Format32bppArgb);
        using var stream = new MemoryStream();
        crop.Save(stream, ImageFormat.Png);
        var result = PriceListOcrService.RecognizeAnchoredPriceList(stream.ToArray(),
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Data", "Ocr", "PriceListAnchor.png")),
            Path.Combine(AppContext.BaseDirectory, "Data", "Ocr", "tessdata_best"), OcrStockTests.Towns);
        Console.WriteLine($"Anchor: {result.Anchor}; source: {result.ProductMetadataText}");
        foreach (var row in result.TownRows)
        {
            var cell = result.PriceCells.Single(cell => cell.PostId == row.PostId);
            Console.WriteLine($"{row.PostName}: center {row.CenterY}; text '{row.SourceLine}'; sale {cell.SalePriceText}; profit {cell.ProfitText}");
        }
        var candidates = PriceListOcrParser.ParsePriceCells(result.TownRows, result.PriceCells, 775, false);
        var expected = new Dictionary<int, decimal>
        {
            [1] = 975, [2] = 904, [4] = 989, [5] = 982, [6] = 983, [7] = 976,
            [8] = 1116, [101] = 1137, [102] = 1207, [103] = 1339, [104] = 1250, [-1] = 1125
        };
        Assert.AreEqual(expected.Count, candidates.Count);
        foreach (var candidate in candidates)
        {
            Assert.AreEqual(expected[candidate.PostId], candidate.Price, candidate.PostName);
            Assert.IsTrue(candidate.IsCrossChecked, candidate.SourceLine);
        }
        var belvast = candidates.Single(candidate => candidate.PostId == 8);
        Assert.AreEqual(1116m, belvast.Price);
        Assert.IsTrue(belvast.IsCrossChecked, belvast.SourceLine);
        Assert.IsTrue(belvast.Include);
        Assert.AreEqual("341", result.PriceCells.Single(cell => cell.PostId == 8).ProfitText.Trim());
        Assert.AreEqual(1125m, candidates.Single(candidate => candidate.PostId == -1).Price);
        foreach (var row in result.TownRows)
            Assert.AreEqual(row.PostName, row.SourceLine.Trim(), "Town OCR must not include decorative brackets.");
        Assert.AreEqual(7454, result.Stock?.Count);
    }

    [TestMethod]
    public void DroppedProfitFourDoesNotGetInferredFromPriceArithmetic()
    {
        var candidate = PriceListOcrParser.ParsePriceCells([new(8, "Belvast", 100, "Belvast")],
            [new(8, "1,116", 90, "31", 70, false)], 775, false).Single();
        Assert.IsFalse(candidate.IsCrossChecked);
        Assert.IsFalse(candidate.Include);
    }

    [TestMethod]
    public void DecorativeWordsDoNotChangeTownCenterOrDisplayedOcrText()
    {
        OcrPriceTextLine[] lines =
        [
            new(") Belvast", [new(")", 40, 0, 70, 6, 110), new("Belvast", 90, 12, 80, 60, 90)]),
            new("Smuggler {", [new("Smuggler", 90, 12, 120, 72, 130), new("{", 40, 80, 100, 85, 140)]),
            new("Tir Chonaill", [new("Tir", 90, 12, 40, 30, 50), new("Chonaill", 90, 32, 40, 80, 50)])
        ];
        var rows = PriceListOcrParser.FindTownRows(lines, OcrStockTests.Towns);
        Assert.AreEqual(3, rows.Count);
        Assert.AreEqual(85, rows.Single(row => row.PostId == 8).CenterY);
        Assert.AreEqual("Belvast", rows.Single(row => row.PostId == 8).SourceLine);
        Assert.AreEqual(125, rows.Single(row => row.PostId == -1).CenterY);
        Assert.AreEqual("Smuggler", rows.Single(row => row.PostId == -1).SourceLine);
        Assert.AreEqual("Tir Chonaill", rows.Single(row => row.PostId == 1).SourceLine);
    }
}
