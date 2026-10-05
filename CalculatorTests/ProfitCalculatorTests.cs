using Microsoft.VisualStudio.TestTools.UnitTesting;
using MabiCommerceNewLife;

namespace CalculatorTests;

[TestClass]
public sealed class ProfitCalculatorTests
{
    [TestMethod]
    public void MixedLoadIncludesEveryPurchaseAndSale()
    {
        var result = ProfitCalculator.Calculate([
            new ProfitLine(3, 45m, 55m),
            new ProfitLine(2, 153m, 120m)
        ]);

        Assert.AreEqual(405m, result.GrossSale);
        Assert.AreEqual(441m, result.PurchaseCost);
        Assert.AreEqual(-36m, result.NetProfit);
    }

    [TestMethod]
    public void EmptyLoadHasNoProfit()
    {
        var result = ProfitCalculator.Calculate([]);

        Assert.AreEqual(0m, result.NetProfit);
    }

    [TestMethod]
    public void OptionalRouteRatesRequirePositiveManualValues()
    {
        var result = ProfitCalculator.Calculate([new ProfitLine(4, 30m, 55m)]);

        Assert.AreEqual(5m, result.PerMinute(20m));
        Assert.AreEqual(2.5m, result.PerDistance(40m));
        Assert.IsNull(result.PerMinute(null));
        Assert.IsNull(result.PerMinute(0m));
        Assert.IsNull(result.PerDistance(-1m));
    }

    [TestMethod]
    public void UnbuffedCommerceRewardsMatchCobhSeaweedExample()
    {
        var profit = ProfitCalculator.Calculate([new ProfitLine(256, 12m, 39m)]);
        var seasonalScore = CommerceRewardCalculator.CalculateUnbuffedSeasonalScore([profit.NetProfit]);

        Assert.AreEqual(6912m, CommerceRewardCalculator.CalculateUnbuffedRawGold(profit));
        Assert.AreEqual(406m, seasonalScore);
    }
}