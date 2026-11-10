using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using MabiCommerceNewLife;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalculatorTests;

[TestClass]
public sealed class OcrStockTests
{
    [DataTestMethod]
    [DataRow("35,190", 35190)]
    [DataRow("35.921", 35921)]
    [DataRow("400", 400)]
    [DataRow("0", 0)]
    public void AgreedWholeCountsAreReliable(string text, int count)
    {
        var stock = PriceListOcrParser.ParseStockReads([text, text, text, text]);
        Assert.AreEqual(count, stock.Count);
        Assert.IsTrue(stock.IsReliable);
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("-1")]
    [DataRow("35,19")]
    [DataRow("1234,567")]
    [DataRow("1,234.567")]
    [DataRow("1.234,567")]
    [DataRow("35 190")]
    [DataRow("2147483648")]
    [DataRow("Inventory 400")]
    [DataRow("22 / 22")]
    public void InvalidCountsNeverBecomeReliable(string text)
    {
        var stock = PriceListOcrParser.ParseStockReads([text, text, text, text]);
        Assert.IsNull(stock.Count);
        Assert.IsFalse(stock.IsReliable);
    }

    [TestMethod]
    public void MissingOrDisagreeingReadsRequireReview()
    {
        foreach (var reads in new[]
        {
            new[] { "400", "400", "400", "40" },
            new[] { "400", "400", "400", "" },
            new[] { "400", "400", "400" }
        })
        {
            var stock = PriceListOcrParser.ParseStockReads(reads);
            Assert.AreEqual(400, stock.Count);
            Assert.IsFalse(stock.IsReliable);
        }
    }

    [DataTestMethod]
    [DataRow("Client_StockFineSandOasis.png", 375, 75, 35190, true, true)]
    [DataRow("Client_StockBangorCoal.png", 370, 75, 35921, false, true)]
    [DataRow("Client_StockLead.png", 354, 76, 400, false, false)]
    [SupportedOSPlatform("windows6.1")]
    public void OwnerCapturesReadInventoryNotQuantityLimit(string fileName, int x, int y, int count, bool barter, bool reliable)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ocr", fileName);
        if (!File.Exists(path)) Assert.Inconclusive($"Owner capture missing: {fileName}");
        using var source = new Bitmap(path);
        using var crop = source.Clone(new Rectangle(x, y, 310, 300), PixelFormat.Format32bppArgb);
        using var stream = new MemoryStream();
        crop.Save(stream, ImageFormat.Png);
        var result = PriceListOcrService.RecognizeAnchoredPriceList(stream.ToArray(),
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Data", "Ocr", "PriceListAnchor.png")),
            Path.Combine(AppContext.BaseDirectory, "Data", "Ocr", "tessdata_best"), Towns, saleOnlyTable: barter);
        Console.WriteLine($"{fileName}: anchor {result.Anchor}; {result.Stock}");
        Assert.AreEqual(count, result.Stock?.Count, result.Stock?.SourceText);
        Assert.AreEqual(reliable, result.Stock!.IsReliable, result.Stock.SourceText);
    }

    internal static readonly (int, string)[] Towns =
    [
        (1, "Tir Chonaill"), (2, "Dunbarton"), (3, "Bangor"), (4, "Emain Macha"), (5, "Taillteann"),
        (6, "Tara"), (7, "Cobh"), (8, "Belvast"), (101, "Qilla"), (102, "Filia"), (103, "Cor"), (104, "Vales"), (-1, "Smuggler")
    ];

    [DataTestMethod]
    [DataRow("Client_0bECIvWsxn.png", 47766)]
    [DataRow("Client_4MrNrI7qTy.png", 9591)]
    [DataRow("Client_4YMqhHUB2P.png", 37902)]
    [DataRow("Client_8nkzOJtkRx.png", 39465)]
    [DataRow("Client_agclHiYXFn.png", 28272)]
    [DataRow("Client_Ah1mgfjPUd.png", 9447)]
    [DataRow("Client_BarterFineSandOasis.png", 35732)]
    [DataRow("Client_BarterFineSandOasis2.png", 35734)]
    [DataRow("Client_BarterGiantCanineFossilOasis.png", 20758)]
    [DataRow("Client_BarterSunMarkShieldKaru.png", 28645)]
    [DataRow("Client_BarterWoodenCraftKaru.png", 27837)]
    [DataRow("Client_BarterWoodenTableKaru.png", 35485)]
    [DataRow("Client_c9JDZiUqkp.png", 28643)]
    [DataRow("Client_CTwOUYmKXr.png", 135)]
    [DataRow("Client_f6bZJ44K1h.png", 45782)]
    [DataRow("Client_Flg0AvLRpL.png", 100)]
    [DataRow("Client_FreshPomegranateQilla.png", 50413)]
    [DataRow("Client_GjnTTYeBjh.png", 119733)]
    [DataRow("Client_GynFqdvPlI.png", 400)]
    [DataRow("Client_J0tfFDnwbv.png", 734)]
    [DataRow("Client_JcBHtAFB37.png", 140)]
    [DataRow("Client_jlDJi9YWES.png", 20758)]
    [DataRow("Client_nlV8t7YO0J.png", 129642)]
    [DataRow("Client_O41J7SRhJN.png", 28195)]
    [DataRow("Client_OZ1NZ5Aocy.png", 577)]
    [DataRow("Client_Prpru6gyO9.png", 35090)]
    [DataRow("Client_r6cSN3WoR8.png", 35738)]
    [DataRow("Client_SYetEt7qXj.png", 76551)]
    [DataRow("Client_WaaHzZXdff.png", 9337)]
    [DataRow("Client_XZHub6Eh7g.png", 20758)]
    [DataRow("Client_z610ykxI1S.png", 27986)]
    [DataRow("smugglercrop.png", 18600)]
    [SupportedOSPlatform("windows6.1")]
    public void PreviousCapturesNeverReliablyReportWrongStock(string fileName, int expected)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ocr", fileName);
        if (!File.Exists(path)) Assert.Inconclusive($"Owner capture missing: {fileName}");
        var result = PriceListOcrService.RecognizeAnchoredPriceList(File.ReadAllBytes(path),
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Data", "Ocr", "PriceListAnchor.png")),
            Path.Combine(AppContext.BaseDirectory, "Data", "Ocr", "tessdata_best"), Towns);
        Assert.IsNotNull(result.Stock);
        Console.WriteLine($"{fileName}: expected {expected}; {result.Stock}");
        Assert.AreEqual(expected, result.Stock.Count, result.Stock.SourceText);
    }
}
