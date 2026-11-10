using MabiCommerceNewLife;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalculatorTests;

[TestClass]
public sealed class CommunityScanBuilderTests
{
    private static readonly CommunityTicket Ticket = new(Guid.NewGuid().ToString(),
        DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(), Guid.NewGuid());

    private static PriceListOcrCandidate Candidate(int id = 2, decimal price = 120, decimal? profit = 20) =>
        new() { PostId = id, Price = price, Include = true, IsCrossChecked = true, ObservedProfit = profit };

    private static CommunityScan Build(bool automatic = true, OcrStockRead? stock = null,
        IReadOnlyList<PriceListOcrCandidate>? candidates = null, string mode = "trade", OcrSourcePrice? source = null) =>
        CommunityScanBuilder.Build(Ticket, mode, 101, 1, automatic,
            source ?? new OcrSourcePrice(100, 99, "100"), stock ?? new OcrStockRead(0, true, "0"),
            candidates ?? [Candidate()], [2]);

    [TestMethod]
    public void UploadNormalizesOnceButCrossChecksObservedDiscountedSource()
    {
        var scan = CommunityScanBuilder.Build(Ticket, "trade", 101, 1, true,
            new OcrSourcePrice(93, 99, "93"), new OcrStockRead(50, true, "50"),
            [Candidate(price: 120, profit: 27)], [2], 7);
        Assert.AreEqual("normalized-v1", scan.PriceBasis);
        Assert.AreEqual(100, scan.SourcePrice);
        Assert.AreEqual(93, scan.ObservedSourcePrice);
        Assert.AreEqual(7m, scan.PurchaseDiscountPercent);
        Assert.AreEqual(27, scan.Prices.Single().Profit);
        Assert.ThrowsException<InvalidOperationException>(() => CommunityScanBuilder.Build(Ticket, "trade", 101, 1,
            true, new OcrSourcePrice(93, 99, "93"), new OcrStockRead(50, true, "50"),
            [Candidate(price: 120, profit: 20)], [2], 7));
    }

    [TestMethod]
    public void FreshExactCrossCheckedScanIncludesObservedZeroStockAndOptionalSmuggler()
    {
        var scan = Build(candidates: [Candidate(), Candidate(-1, 125, 25)]);
        Assert.AreEqual(0, scan.Stock);
        Assert.AreEqual(100, scan.SourcePrice);
        Assert.AreEqual(0, scan.Prices[0].PostId);
        Assert.AreEqual(25, scan.Prices[0].Profit);
        var loss = Build(candidates: [Candidate(price: 80, profit: -20)]);
        Assert.AreEqual(-20, loss.Prices[0].Profit);
    }

    [TestMethod]
    public void UnreviewedScansAndUncertainStockNeverBecomeAutomaticUploads()
    {
        Assert.ThrowsException<InvalidOperationException>(() => Build(automatic: false));
        Assert.ThrowsException<InvalidOperationException>(() => Build(stock: new OcrStockRead(20, false, "uncertain")));
        Assert.ThrowsException<InvalidOperationException>(() => Build(stock: new OcrStockRead(null, false, "")));
        Assert.ThrowsException<InvalidOperationException>(() => CommunityScanBuilder.Build(Ticket, "trade",
            101, 1, true, null, new OcrStockRead(10, true, ""), [Candidate()], [2]));
    }

    [TestMethod]
    public void ReviewedScanPreservesEditFlagsAndNeverInventsObservedProfit()
    {
        var review = new CommunityReview(false, true, [3]);
        var scan = CommunityScanBuilder.BuildReviewed(Ticket, "trade", 101, 1, 93, 135,
            [Candidate(price: 120, profit: 27), Candidate(3, 150, 20)], [2, 3], 7, review);
        Assert.AreEqual(100, scan.SourcePrice);
        Assert.AreEqual(93, scan.ObservedSourcePrice);
        Assert.AreEqual(27, scan.Prices[0].Profit);
        Assert.IsNull(scan.Prices[1].Profit, "Edited sale must not acquire an invented OCR profit.");
        Assert.AreEqual(review, scan.Review);
        var unchanged = CommunityScanBuilder.BuildReviewed(Ticket, "trade", 101, 1, 100, 0,
            [new PriceListOcrCandidate { Include = true, PostId = 2, Price = 120 }], [2], 0,
            new CommunityReview(false, false, []));
        Assert.IsNull(unchanged.Prices[0].Profit);
        Assert.AreEqual(0, unchanged.Review!.ManualPostIds.Count);
    }

    [TestMethod]
    public void PartialUnconfirmedOrInvalidReviewedScansCannotUpload()
    {
        CommunityScan Reviewed(decimal? source, int? stock, IReadOnlyList<PriceListOcrCandidate> rows,
            CommunityReview? review = null) =>
            CommunityScanBuilder.BuildReviewed(Ticket, "trade", 101, 1, source, stock, rows, [2], 0,
                review ?? new CommunityReview(false, false, []));
        Assert.ThrowsException<InvalidOperationException>(() => Reviewed(null, 10, [Candidate()]));
        Assert.ThrowsException<InvalidOperationException>(() => Reviewed(100, null, [Candidate()]));
        Assert.ThrowsException<InvalidOperationException>(() => Reviewed(100, 10, []));
        Assert.ThrowsException<InvalidOperationException>(() => Reviewed(100, 10, [Candidate(price: 1.5m)]));
        Assert.ThrowsException<InvalidOperationException>(() => Reviewed(100, 10, [Candidate()], new(false, false, [3])));
        var editedSource = Reviewed(110, 10, [Candidate()], new(true, false, []));
        Assert.IsNull(editedSource.Prices[0].Profit);
        Assert.IsTrue(editedSource.Review!.SourceManual);
        var barter = CommunityScanBuilder.BuildReviewed(Ticket, "barter", 24001, 201, null, 0,
            [Candidate()], [2], 0, new(false, false, [2]));
        Assert.IsNull(barter.SourcePrice);
        Assert.IsNull(barter.Prices[0].Profit);
    }

    [TestMethod]
    public void ReconstructedProfitMustNotBePretendedToBeAnObservedProfit()
    {
        Assert.ThrowsException<InvalidOperationException>(() => Build(candidates: [Candidate(profit: 2)]));
        Assert.ThrowsException<InvalidOperationException>(() => Build(candidates: [Candidate(profit: null)]));
        var parsed = PriceListOcrParser.ParsePriceCells(
            [new OcrTownRow(2, "Dunbarton", 10, "")],
            [new OcrPriceCellRead(2, "120", 99, "20", 99, false)], 100, false);
        Assert.AreEqual(20m, parsed[0].ObservedProfit);
    }

    [TestMethod]
    public void BarterKeepsRealSaleOnlyValuesWithoutInventingSourceCostOrProfit()
    {
        var scan = Build(mode: "barter", candidates: [Candidate(profit: null)]);
        Assert.IsNull(scan.SourcePrice);
        Assert.IsNull(scan.Prices[0].Profit);
        Assert.AreEqual(120, scan.Prices[0].SalePrice);
    }

    [TestMethod]
    public void IncompleteDuplicatedUncheckedAndFractionalValuesStayLocal()
    {
        Assert.ThrowsException<InvalidOperationException>(() => Build(candidates: []));
        Assert.ThrowsException<InvalidOperationException>(() => Build(candidates: [Candidate(), Candidate()]));
        Assert.ThrowsException<InvalidOperationException>(() => Build(candidates: [Candidate(3)]));
        Assert.ThrowsException<InvalidOperationException>(() => Build(candidates: [
            new PriceListOcrCandidate { PostId = 2, Price = 120, Include = true, IsCrossChecked = false }]));
        Assert.ThrowsException<InvalidOperationException>(() => Build(candidates: [Candidate(price: 120.5m)]));
        Assert.ThrowsException<InvalidOperationException>(() => Build(source: new OcrSourcePrice(100.5m, 99, "")));
        Assert.ThrowsException<InvalidOperationException>(() => Build(stock: new OcrStockRead(1_000_000_001, true, "")));
        Assert.ThrowsException<InvalidOperationException>(() => Build(candidates: [Candidate(price: -1, profit: -101)]));
        Assert.ThrowsException<InvalidOperationException>(() => Build(mode: "group", candidates: [Candidate(), Candidate(-1)]));
    }
}
