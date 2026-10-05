using MabiCommerceNewLife;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalculatorTests;

[TestClass]
public sealed class TradeHistoryTests
{
    private static TradeHistoryEntry Entry(decimal rawGold, decimal ducats, decimal totalGold) =>
        new(new DateTime(2026, 10, 4, 12, 0, 0), "Trade", "Tara", [new TradeHistoryGood("Wool", 10)],
            1_000m, rawGold, ducats, totalGold, 50m);

    [TestMethod]
    public void TotalsSumEveryEntry()
    {
        var history = new TradeHistory();
        history.Add(Entry(500m, 600m, 30_500m));
        history.Add(Entry(-20m, -20m, -1_020m));

        var totals = history.Totals;

        Assert.AreEqual(2, totals.Trades);
        Assert.AreEqual(480m, totals.RawGold);
        Assert.AreEqual(580m, totals.Ducats);
        Assert.AreEqual(29_480m, totals.TotalGold);
    }

    [TestMethod]
    public void SaveAndLoadRoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"trade-history-{Guid.NewGuid():N}", "trade-history.json");
        try
        {
            var history = new TradeHistory();
            history.Add(Entry(500m, 600m, 30_500m) with { Letter = "Ogre's Letter of Guarantee" });
            history.Save(path);

            var loaded = TradeHistory.Load(path);

            Assert.AreEqual(1, loaded.Entries.Count);
            var entry = loaded.Entries[0];
            Assert.AreEqual("Tara", entry.Destination);
            Assert.AreEqual("Wool", entry.Goods[0].Name);
            Assert.AreEqual(10, entry.Goods[0].Quantity);
            Assert.AreEqual(30_500m, entry.TotalGold);
            Assert.AreEqual("Ogre's Letter of Guarantee", entry.Letter);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [TestMethod]
    public void MissingOrCorruptFileLoadsEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"trade-history-{Guid.NewGuid():N}.json");
        Assert.AreEqual(0, TradeHistory.Load(path).Entries.Count);
        try
        {
            File.WriteAllText(path, "{ not json");
            Assert.AreEqual(0, TradeHistory.Load(path).Entries.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
