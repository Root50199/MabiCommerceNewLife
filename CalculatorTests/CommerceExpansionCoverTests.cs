using MabiCommerceNewLife;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalculatorTests;

[TestClass]
public sealed class CommerceExpansionCoverTests
{
    private static PurchaseRequest Request(decimal? coverPrice, int stock = 3, decimal funds = 1000m,
        PurchaseObjective objective = PurchaseObjective.NetProfit) => new(
        2, funds,
        [new PurchaseGood(1, 2, stock, 10m, 100, 1, new Dictionary<int, decimal> { [7] = 110m })],
        [new PurchaseTransport(3, 2, 200)],
        [new PurchaseDestination(7, Minutes: 10m)],
        Objective: objective, ExpansionCoverMarketValueGold: coverPrice, GoldPerProfitUnit: 2m);

    private static CommerceRewardBreakdown Reward(PurchasePlan plan, bool barter = false,
        GuaranteeLetterKind letter = GuaranteeLetterKind.None) => CommerceRewardModel.Calculate(
        plan.Lines.Select(line => new RewardLine(line.Quantity, line.Cost / line.Quantity,
            line.Sale / line.Quantity, line.Weight / line.Quantity)),
        barter, new RewardModifiers("N", letter, 1m, LetterMarketValue: 25m,
            ExpansionCoverCostGold: plan.ExpansionCoverCostGold));

    [TestMethod]
    public void DefaultTwoMillionCoverDoesNotPayForExtraCargo()
    {
        Assert.AreEqual(2_000_000, CommerceExpansionCover.DefaultMarketValueGold);
        var plans = PurchaseOptimizer.Search(Request(CommerceExpansionCover.DefaultMarketValueGold)).Plans;
        var normal = plans.Single(plan => !plan.UsesExpansionCover);
        var expanded = plans.Single(plan => plan.UsesExpansionCover);
        Assert.AreEqual(2, normal.UsedSlots);
        Assert.AreEqual(200, normal.UsedWeight);
        Assert.AreEqual(3, expanded.UsedSlots);
        Assert.AreEqual(300, expanded.UsedWeight);
        Assert.AreEqual(2_000_000m, expanded.ExpansionCoverCostGold);
        Assert.IsFalse(CommerceExpansionCover.IsWorthwhile(Reward(expanded).TotalGold, Reward(normal).TotalGold));
        Assert.AreEqual(normal, plans[0]);
    }

    [TestMethod]
    public void CheapCoverPaysAndExactBreakEvenKeepsNormalLoad()
    {
        foreach (var objective in new[] { PurchaseObjective.NetProfit, PurchaseObjective.ProfitPerMinute })
        {
            var plans = PurchaseOptimizer.Search(Request(199m, objective: objective)).Plans;
            var normal = plans.Single(plan => !plan.UsesExpansionCover);
            var expanded = plans.Single(plan => plan.UsesExpansionCover);
            Assert.IsTrue(CommerceExpansionCover.IsWorthwhile(Reward(expanded).TotalGold, Reward(normal).TotalGold));
            Assert.AreEqual(expanded, plans[0]);
            var tied = PurchaseOptimizer.Search(Request(200m, objective: objective)).Plans;
            Assert.IsFalse(CommerceExpansionCover.IsWorthwhile(
                Reward(tied.Single(plan => plan.UsesExpansionCover)).TotalGold,
                Reward(tied.Single(plan => !plan.UsesExpansionCover)).TotalGold));
        }
    }

    [TestMethod]
    public void NoExtraCargoFromStockOrDucatBudgetMeansNoCoverBenefit()
    {
        foreach (var request in new[] { Request(1m, stock: 2), Request(1m, funds: 20m) })
        {
            var plans = PurchaseOptimizer.Search(request).Plans;
            var normal = plans.Single(plan => !plan.UsesExpansionCover);
            var expanded = plans.Single(plan => plan.UsesExpansionCover);
            Assert.AreEqual(normal.UsedWeight, expanded.UsedWeight);
            Assert.IsFalse(CommerceExpansionCover.IsWorthwhile(Reward(expanded).TotalGold, Reward(normal).TotalGold));
        }
    }

    [TestMethod]
    public void BlankAndZeroPricesDisableExpandedSearch()
    {
        foreach (var price in new decimal?[] { null, 0m })
        {
            var plans = PurchaseOptimizer.Search(Request(price)).Plans;
            Assert.AreEqual(1, plans.Count);
            Assert.IsFalse(plans[0].UsesExpansionCover);
        }
    }

    [TestMethod]
    public void CoverCostDeductedOnceAlongsideLettersForTradeAndBarter()
    {
        var plan = PurchaseOptimizer.Search(Request(50m)).Plans.Single(plan => plan.UsesExpansionCover);
        foreach (var barter in new[] { false, true })
        {
            var withCover = Reward(plan, barter, GuaranteeLetterKind.Ogre);
            var withoutCover = Reward(plan with { ExpansionCoverCostGold = 0 }, barter, GuaranteeLetterKind.Ogre);
            Assert.AreEqual(50m, withoutCover.TotalGold - withCover.TotalGold);
            Assert.AreEqual(withoutCover.LetterCostGold, withCover.LetterCostGold);
            Assert.AreEqual(withoutCover.DucatGain, withCover.DucatGain);
            Assert.AreEqual(withoutCover.RawGold, withCover.RawGold);
            StringAssert.Contains(CommerceRewardModel.DetailedTooltip(withCover), "one per trip costs 50");
            StringAssert.Contains(CommerceRewardModel.BasicTooltip(withCover), "Commerce Expansion Cover");
        }
    }

    [TestMethod]
    public void EmptyAndPerUnitCalculationsDoNotChargeCover()
    {
        var modifiers = new RewardModifiers("F", GuaranteeLetterKind.None, 1m, ExpansionCoverCostGold: 50m);
        Assert.AreEqual(0m, CommerceRewardModel.Calculate([], false, modifiers).ExpansionCoverCostGold);
        Assert.AreEqual(0m, CommerceRewardModel.Calculate([new RewardLine(1, 10m, 100m, 1m)],
            false, modifiers, perLoadLetterEffects: false).ExpansionCoverCostGold);
    }

    [TestMethod]
    public void CoverCostRemainsSingleChargeForMultipleGoods()
    {
        var lines = new[] { new RewardLine(3, 10m, 100m, 1m), new RewardLine(5, 20m, 200m, 2m) };
        var modifiers = new RewardModifiers("1", GuaranteeLetterKind.ImpFine, 3m,
            LetterMarketValue: 100m, ExpansionCoverCostGold: 75m);
        foreach (var barter in new[] { false, true })
        {
            var covered = CommerceRewardModel.Calculate(lines, barter, modifiers);
            var normal = CommerceRewardModel.Calculate(lines, barter, modifiers with { ExpansionCoverCostGold = 0m });
            Assert.AreEqual(75m, normal.TotalGold - covered.TotalGold);
        }
    }

    [TestMethod]
    public void CoverStacksWithExistingCapacityAndWeightCanBindWithoutSlots()
    {
        var request = Request(1m, stock: 100) with
        {
            Transports = [new PurchaseTransport(3, 4, 600)]
        };
        var expanded = PurchaseOptimizer.Search(request).Plans.Single(plan => plan.UsesExpansionCover);
        Assert.AreEqual(5, expanded.UsedSlots);
        Assert.AreEqual(500, expanded.UsedWeight);
        request = request with
        {
            Goods = [request.Goods[0] with { UnitsPerSlot = 10 }],
            Transports = [new PurchaseTransport(3, 4, 600)]
        };
        var plans = PurchaseOptimizer.Search(request).Plans;
        Assert.AreEqual(600, plans.Single(plan => !plan.UsesExpansionCover).UsedWeight);
        Assert.AreEqual(700, plans.Single(plan => plan.UsesExpansionCover).UsedWeight);
        Assert.AreEqual(1, plans.Single(plan => plan.UsesExpansionCover).UsedSlots);
    }

    [TestMethod]
    public void InvalidCoverPriceOrConversionIsRejected()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => PurchaseOptimizer.Search(Request(-1m)));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            PurchaseOptimizer.Search(Request(1m) with { GoldPerProfitUnit = 0m }));
    }
}
