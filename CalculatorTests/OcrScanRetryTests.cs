using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using MabiCommerceNewLife;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalculatorTests;

[TestClass]
public sealed class OcrScanRetryTests
{
    [TestMethod]
    public void MissingNamesOrPricesCountAsMissingEvenWhenOtherRowsAgree()
    {
        OcrTownRow[] rows = [new(1, "First", 100, ""), new(2, "Second", 120, "")];
        PriceListOcrCandidate[] candidates = [new() { PostId = 1, Price = 100, IsCrossChecked = true }];
        CollectionAssert.AreEqual(new[] { 2, 3 },
            PriceListOcrParser.MissingDestinationEntries([1, 2, 3], rows, candidates).ToArray());
        CollectionAssert.AreEqual(new[] { 2 },
            PriceListOcrParser.MissingDestinationEntries([1, 2], rows.Take(1),
                [.. candidates, new() { PostId = 2, Price = 100 }]).ToArray());
    }

    [TestMethod]
    public void CompleteRowsNeedNoRetryEvenWhenPricesNeedReview()
    {
        Assert.AreEqual(0, PriceListOcrParser.MissingDestinationEntries([1],
            [new(1, "First", 100, "")], [new() { PostId = 1, Price = 100, IsCrossChecked = false }]).Count);
        Assert.AreEqual(1, PriceListOcrParser.MissingDestinationEntries([1],
            [new(1, "First", 100, "")], [new() { PostId = 1, Price = 0 }]).Count);
    }

    [TestMethod]
    public async Task CompleteInitialReadNeverCaptures()
    {
        var result = await OcrScanRetry.ReadAsync(true, () => throw new AssertFailedException("Unexpected capture"),
            _ => Task.FromResult(false), value => value, CancellationToken.None);
        Assert.IsTrue(result);
    }

    [TestMethod]
    public async Task StopsAfterFirstCompleteRetry()
    {
        var captures = 0;
        var result = await OcrScanRetry.ReadAsync(false, () => { captures++; return []; },
            _ => Task.FromResult(true), value => value, CancellationToken.None);
        Assert.IsTrue(result);
        Assert.AreEqual(1, captures);
    }

    [TestMethod]
    public async Task FiveCapturesOccurAtOneSecondIntervalsEvenWithSlowOcr()
    {
        var clock = Stopwatch.StartNew();
        var times = new List<double>();
        var reads = 0;
        var result = await OcrScanRetry.ReadAsync(false, () =>
        {
            times.Add(clock.Elapsed.TotalSeconds);
            return [];
        }, async _ => { reads++; await Task.Delay(1300); return false; }, value => value, CancellationToken.None);
        Assert.IsFalse(result);
        Assert.AreEqual(5, times.Count);
        Assert.AreEqual(5, reads);
        for (var i = 0; i < times.Count; i++)
        {
            Assert.IsTrue(times[i] >= i + 0.9, $"Early capture: {times[i]}");
            Assert.IsTrue(times[i] <= i + 1.5, $"Late capture: {times[i]}");
        }
        var finalCount = times.Count;
        await Task.Delay(1100);
        Assert.AreEqual(finalCount, times.Count, "Captures must not continue after exhaustion.");
    }

    [TestMethod]
    public async Task CancellationStopsCapturesAndDoesNotReturnPartialSuccess()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
        var captures = 0;
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => OcrScanRetry.ReadAsync(false,
            () => { captures++; return []; }, _ => Task.FromResult(false), value => value, cancellation.Token));
        Assert.AreEqual(1, captures);
        await Task.Delay(1100);
        Assert.AreEqual(1, captures);
    }

    [TestMethod]
    public async Task CaptureErrorsAreSurfaced()
    {
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => OcrScanRetry.ReadAsync(false,
            () => throw new InvalidOperationException("Capture failed"), _ => Task.FromResult(true),
            value => value, CancellationToken.None));
    }

    [TestMethod]
    public async Task RecognitionErrorsAreSurfacedAndStopFurtherCaptures()
    {
        var captures = 0;
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => OcrScanRetry.ReadAsync(false,
            () => { captures++; return []; }, _ => throw new InvalidOperationException("OCR failed"),
            value => value, CancellationToken.None));
        await Task.Delay(1100);
        Assert.AreEqual(1, captures);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [SupportedOSPlatform("windows6.1")]
    public void ObstructingTownOrBarterPriceMakesExpectedEntryMissing(bool coverPrice)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ocr", "Client_BarterFineSandOasis.png");
        if (!File.Exists(path)) Assert.Inconclusive("Owner's local OCR fixture is unavailable.");
        var anchor = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Data", "Ocr", "PriceListAnchor.png"));
        var tessdata = Path.Combine(AppContext.BaseDirectory, "Data", "Ocr", "tessdata_best");
        var original = PriceListOcrService.RecognizeAnchoredPriceList(File.ReadAllBytes(path),
            anchor, tessdata, OcrStockTests.Towns);
        var expected = OcrStockTests.Towns.Where(town => town.Item1 != -1).Select(town => town.Item1).ToArray();
        Assert.AreEqual(0, PriceListOcrParser.MissingDestinationEntries(expected, original.TownRows,
            PriceListOcrParser.ParseSaleOnlyCells(original.TownRows, original.PriceCells)).Count);
        var tara = original.TownRows.Single(row => row.PostId == 6);
        using var bitmap = new Bitmap(path);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.FillRectangle(Brushes.Black, original.Anchor.X + (coverPrice ? 102 : 17),
                tara.CenterY - 8, coverPrice ? 70 : 85, 16);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        var obstructed = PriceListOcrService.RecognizeAnchoredPriceList(stream.ToArray(),
            anchor, tessdata, OcrStockTests.Towns);
        var candidates = PriceListOcrParser.ParseSaleOnlyCells(obstructed.TownRows, obstructed.PriceCells);
        CollectionAssert.Contains(PriceListOcrParser.MissingDestinationEntries(expected,
            obstructed.TownRows, candidates).ToArray(), 6, "Tara obstruction must not look like a complete scan.");
    }
}
