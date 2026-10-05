using MabiCommerceNewLife;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Runtime.Versioning;

namespace CalculatorTests;

// Template for OCR regression tests against your own captures. Captures are not committed;
// see Fixtures/README.md. Copy this class, rename it, and add one DataRow per capture.
[TestClass]
public sealed class OcrCaptureTemplateTests
{
    [DataTestMethod]
    // File name in Fixtures/Ocr, the good's exact catalog name, its source buy price, and the expected
    // "Town=Price" sale values visible in the capture.
    [DataRow("example-trade.png", "Lovely Potion", 161, "Dunbarton=185;Bangor=256")]
    [SupportedOSPlatform("windows6.1")]
    public void TradeCaptureReadsExpectedPrices(string fileName, string productName, int sourcePrice, string expectedPrices)
    {
        var imagePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ocr", fileName);
        if (!File.Exists(imagePath))
            Assert.Inconclusive($"Add your own capture as CalculatorTests/Fixtures/Ocr/{fileName} to run this test.");

        (int, string)[] destinations =
        [
            (1, "Dunbarton"), (2, "Bangor"), (3, "Emain Macha"), (4, "Taillteann"),
            (5, "Tara"), (6, "Cobh"), (7, "Belvast"), (8, "Qilla"), (9, "Filia"), (10, "Cor"), (11, "Vales"),
            (12, "Tir Chonaill"), (-1, "Smuggler")
        ];
        var result = PriceListOcrService.RecognizeAnchoredPriceList(File.ReadAllBytes(imagePath),
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Data", "Ocr", "PriceListAnchor.png")),
            Path.Combine(AppContext.BaseDirectory, "Data", "Ocr", "tessdata_best"), destinations);

        Assert.IsFalse(result.IsSaleOnlyTable, "A trade list must use the sale/profit reader.");
        Assert.IsNotNull(PriceListOcrParser.FindUniqueProduct(result.ProductNameText, [new OcrProductIdentity(1, 2, productName)]),
            $"Good name not recognized: {result.ProductNameText}");
        var source = PriceListOcrParser.ParseSourcePrice(result.ProductMetadataText, result.MeanConfidence);
        Assert.AreEqual(sourcePrice, source?.Price, $"Source price misread: {result.ProductMetadataText}");

        var candidates = PriceListOcrParser.ParsePriceCells(result.TownRows, result.PriceCells, sourcePrice, false);
        foreach (var pair in expectedPrices.Split(';'))
        {
            var parts = pair.Split('=');
            var town = candidates.SingleOrDefault(candidate => candidate.PostName == parts[0]);
            Assert.IsNotNull(town, $"{parts[0]} was not read.");
            Assert.AreEqual(decimal.Parse(parts[1]), town.Price, $"{parts[0]} price.");
        }
    }
}
