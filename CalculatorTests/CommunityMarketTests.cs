using MabiCommerceNewLife;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalculatorTests;

[TestClass]
public sealed class CommunityMarketTests
{
    [TestMethod]
    public void InactiveSmugglerZeroWinsByTimeReactivatesAndSurvivesRetention()
    {
        var value = new CommunityMarketValue(32);
        value.SetLocal(32, 100, "Manual");
        value.SetRemote(0, 1, 101);
        Assert.AreEqual(0m, value.Value);
        value.SetLocal(45, 102, "Manual");
        Assert.AreEqual(45m, value.Value);
        value.SetRemote(0, 2, 101);
        Assert.AreEqual(45m, value.Value, "Older absent scans cannot erase newer edits.");
        value.SetRemote(0, 3, 103);
        value.RetainCurrentAsLocal(value.Value);
        Assert.AreEqual(0m, value.Value);
        value.SetRemote(60, 4, 104);
        Assert.AreEqual(60m, value.Value, "A later active quote must reactivate.");
        value.SetLocal(0, 105, "Local scan");
        value.ClearRemote();
        Assert.AreEqual(0m, value.Value, "Local scan inactivity survives sharing OFF.");
    }

    [TestMethod]
    public void ManualCommunityProvenanceSurvivesLocalRetentionWithoutChangingConsumption()
    {
        var value = new CommunityMarketValue(100, true);
        value.SetRemote(30, 2, 200, "Community manual");
        value.Consume(5);
        Assert.AreEqual("Community manual #2", value.Origin);
        value.RetainCurrentAsLocal(value.Value);
        Assert.AreEqual(25m, value.Value);
        StringAssert.Contains(value.Origin, "manual");
    }

    [TestMethod]
    public void RetainingSharedStockKeepsConsumptionTimestampAndReplayBaseline()
    {
        var stock = new CommunityMarketValue(100, true, loadedQuantity: 2);
        stock.SetRemote(50, 5, 500);
        stock.Consume(7);
        stock.RetainCurrentAsLocal(stock.Value);
        Assert.AreEqual(43m, stock.Value);
        Assert.AreEqual(500L, stock.LocalTime);
        Assert.AreEqual("Saved shared data", stock.Origin);
        Assert.IsNull(stock.RemoteSequence);
        stock.SetRemote(50, 5, 500);
        Assert.IsFalse(stock.RemoteWins);
        Assert.AreEqual(43m, stock.Value);
        stock.Consume(-9);
        Assert.AreEqual(52m, stock.Value);
        stock.SetRemote(30, 6, 501);
        Assert.AreEqual(30m, stock.Value);
    }

    [TestMethod]
    public void RetainingPricesUsesEffectiveDiscountedValueWithoutStampingNewTime()
    {
        var value = new CommunityMarketValue(10);
        value.SetRemote(100, 1, 200);
        value.RetainCurrentAsLocal(95);
        Assert.AreEqual(95m, value.Value);
        Assert.AreEqual(200L, value.Time);
        value.SetRemote(100, 1, 200);
        Assert.AreEqual(95m, value.Value);
        value.SetLocal(120, 300, "Manual");
        value.RetainCurrentAsLocal(1);
        Assert.AreEqual(120m, value.Value);
        Assert.AreEqual("Manual", value.Origin);
    }

    [TestMethod]
    [DataRow(10, 7, 11)]
    [DataRow(39, 8, 42)]
    [DataRow(250, 7, 269)]
    [DataRow(775, 0, 775)]
    [DataRow(93, 7, 100)]
    public void NormalizeStripsUploaderDiscount(int observed, int discount, int expected)
    {
        Assert.AreEqual(expected, CommunityPurchasePrice.Normalize(observed, discount));
        Assert.AreEqual(observed, CommunityPurchasePrice.ForRecipient(expected, discount));
    }

    [TestMethod]
    public void RecipientUsesOwnDiscountAndPositiveHalfPricesRoundUp()
    {
        Assert.AreEqual(95, CommunityPurchasePrice.ForRecipient(100, 5));
        Assert.AreEqual(93, CommunityPurchasePrice.ForRecipient(100, 7));
        Assert.AreEqual(39, CommunityPurchasePrice.ForRecipient(42, 8));
        Assert.AreEqual(2, CommunityPurchasePrice.ForRecipient(3, 50));
        Assert.AreEqual(100, CommunityPurchasePrice.Normalize(99, 1.01m));
        foreach (var invalid in new[] { -1m, 100m, 1.001m })
            Assert.ThrowsException<InvalidOperationException>(() => CommunityPurchasePrice.Normalize(100, invalid));
        Assert.ThrowsException<InvalidOperationException>(() => CommunityPurchasePrice.Normalize(1_000_000_000, 99));
        Assert.ThrowsException<InvalidOperationException>(() => CommunityPurchasePrice.ForRecipient(0, 0));
    }

    [TestMethod]
    public void NewestLocalOrRemoteWinsAndLocalWinsTies()
    {
        var value = new CommunityMarketValue(10);
        value.SetRemote(20, 1, 100);
        Assert.AreEqual(20m, value.Value);
        value.SetLocal(30, 101, "Manual");
        Assert.AreEqual(30m, value.Value);
        value.SetRemote(40, 2, 101);
        Assert.AreEqual(30m, value.Value);
        value.SetRemote(50, 3, 102);
        Assert.AreEqual(50m, value.Value);
        value.SetLocal(25, 99, "Late OCR");
        value.ClearRemote();
        Assert.AreEqual(30m, value.Value);
        Assert.AreEqual("Manual", value.Origin);
    }

    [TestMethod]
    public void ReplayResetAndDisableNeverRefillConsumedStock()
    {
        var stock = new CommunityMarketValue(100, true);
        stock.SetRemote(80, 1, 10);
        stock.Consume(7);
        Assert.AreEqual(73m, stock.Value);
        stock.ClearRemote();
        Assert.AreEqual(93m, stock.Value);
        stock.SetRemote(80, 1, 10);
        Assert.AreEqual(73m, stock.Value);
        stock.SetRemote(90, 2, 11);
        Assert.AreEqual(90m, stock.Value);
        stock.Consume(5);
        stock.ClearRemote();
        stock.SetRemote(80, 1, 10);
        Assert.AreEqual(68m, stock.Value);
        stock.Consume(-5);
        Assert.AreEqual(73m, stock.Value);
        stock.Consume(-100);
        Assert.AreEqual(80m, stock.Value);
    }

    [TestMethod]
    public void RefundingCargoLoadedBeforeNewObservationSurvivesReplay()
    {
        var stock = new CommunityMarketValue(98, true, loadedQuantity: 2);
        stock.SetRemote(50, 5, 500);
        stock.Consume(-2);
        Assert.AreEqual(52m, stock.Value);
        stock.ClearRemote();
        Assert.AreEqual(100m, stock.Value);
        stock.SetRemote(50, 5, 500);
        Assert.AreEqual(52m, stock.Value);
    }

    [TestMethod]
    public void PreviouslyUnseenOlderObservationDoesNotRefillConsumedStock()
    {
        var stock = new CommunityMarketValue(100, true);
        stock.SetRemote(80, 5, 500);
        stock.Consume(7);
        stock.ClearRemote();
        stock.SetRemote(80, 2, 200);
        Assert.AreEqual(73m, stock.Value);
    }

    [TestMethod]
    public void LocalScanEstablishesFreshBaselineWithoutPriceConsumption()
    {
        var stock = new CommunityMarketValue(100, true);
        stock.Consume(20);
        stock.SetLocal(40, 100, "Local scan");
        stock.Consume(3);
        Assert.AreEqual(37m, stock.Value);
        stock.SetRemote(50, 1, 99);
        Assert.AreEqual(37m, stock.Value);
        var price = new CommunityMarketValue(100);
        price.Consume(20);
        Assert.AreEqual(100m, price.Value);
    }
}
