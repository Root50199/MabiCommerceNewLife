using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using System.Text.Json;
using MabiCommerceNewLife;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalculatorTests;

[TestClass]
public sealed class CommunityLocalIntegrationTests
{
    [TestMethod]
    [TestCategory("LocalCommunity")]
    [SupportedOSPlatform("windows6.1")]
    public async Task OwnerFixtureParsesAndRoundTripsThroughRealWorkerWithoutUploadingPixels()
    {
        var endpoint = Environment.GetEnvironmentVariable("MABI_COMMUNITY_TEST_ENDPOINT");
        if (string.IsNullOrEmpty(endpoint))
            Assert.Inconclusive("Opt-in local test: set MABI_COMMUNITY_TEST_ENDPOINT to an isolated local Worker.");
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ocr", "Client_HighlanderOreBelvast.png");
        if (!File.Exists(fixture)) Assert.Inconclusive("Owner's local OCR fixture is unavailable.");
        using var bitmap = new Bitmap(fixture);
        using var crop = bitmap.Clone(new Rectangle(355, 88, 315, 310), PixelFormat.Format32bppArgb);
        using var stream = new MemoryStream();
        crop.Save(stream, ImageFormat.Png);
        var read = PriceListOcrService.RecognizeAnchoredPriceList(stream.ToArray(),
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Data", "Ocr", "PriceListAnchor.png")),
            Path.Combine(AppContext.BaseDirectory, "Data", "Ocr", "tessdata_best"), OcrStockTests.Towns);
        var source = PriceListOcrParser.ParseSourcePrice(read.ProductMetadataText, read.MeanConfidence);
        Assert.AreEqual(775m, source?.Price);
        var candidates = PriceListOcrParser.ParsePriceCells(read.TownRows, read.PriceCells, source!.Price, false);
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "commerce-catalog.json")));
        var product = catalog.RootElement.GetProperty("products").EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == "Highlander Ore");
        var expected = OcrStockTests.Towns.Select(t => t.Item1)
            .Where(id => id != -1 && id != product.GetProperty("postId").GetInt32()).ToArray();
        using var client = new CommunityClient(endpoint);
        await client.ConnectAsync(await client.GetPrivacyAsync(default), default);
        var ticket = await client.BeginScanAsync(default);
        var scan = CommunityScanBuilder.Build(ticket, "trade", product.GetProperty("id").GetInt32(),
            product.GetProperty("postId").GetInt32(),
            PriceListOcrParser.CanAutoAccept(candidates, read.TownRows.Count, false), source, read.Stock, candidates, expected);
        var receipt = await client.UploadAsync(ticket, scan, default);
        Assert.IsFalse(receipt.Duplicate);
        var duplicate = await client.UploadAsync(ticket, scan, default);
        Assert.IsTrue(duplicate.Duplicate);
        Assert.AreEqual(receipt.Sequence, duplicate.Sequence);
        var snapshot = await client.ReadLatestAsync(default);
        var received = snapshot.Observations.Single(o => o.Sequence == receipt.Sequence);
        Assert.AreEqual(7454, received.Stock);
        Assert.AreEqual(775, received.SourcePrice);
        Assert.AreEqual(1116, received.Prices.Single(p => p.PostId == 8).SalePrice);
        Assert.AreEqual(341, received.Prices.Single(p => p.PostId == 8).Profit);
        Assert.AreEqual(12, received.Prices.Count);
        using var recipient = new CommunityClient(endpoint);
        var initial = await recipient.SyncAsync(default);
        Assert.AreEqual(receipt.Sequence, initial.NextCursor);
        var discountedTicket = await client.BeginScanAsync(default);
        var discounted = CommunityScanBuilder.Build(discountedTicket, "trade", scan.ProductId, scan.SourcePostId,
            true, source, read.Stock, candidates, expected, 7);
        Assert.AreEqual(833, discounted.SourcePrice);
        Assert.AreEqual(775, discounted.ObservedSourcePrice);
        var discountedReceipt = await client.UploadAsync(discountedTicket, discounted, default);
        var delta = await recipient.SyncAsync(default);
        var normalized = delta.Observations.Single(o => o.Sequence == discountedReceipt.Sequence);
        var basePrice = normalized.SourcePrice ?? throw new AssertFailedException("Normalized Trade observation is missing its base price.");
        Assert.AreEqual(833, basePrice);
        Assert.AreEqual(775, normalized.ObservedSourcePrice);
        Assert.AreEqual(7m, normalized.PurchaseDiscountPercent);
        Assert.AreEqual(791, CommunityPurchasePrice.ForRecipient(basePrice, 5));
        Assert.AreEqual(341, normalized.Prices.Single(p => p.PostId == 8).Profit);
        Assert.AreEqual(discountedReceipt.Sequence, (await recipient.SyncAsync(default)).NextCursor);
        Console.WriteLine($"Local Worker parsed {received.Prices.Count} prices; receipt {receipt.Sequence}, source775, stock7454, Belvast1116/profit341.");
    }
}
