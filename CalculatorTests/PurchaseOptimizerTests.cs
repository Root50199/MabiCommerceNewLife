using System.Text.Json;
using MabiCommerceNewLife;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalculatorTests;

[TestClass]
public sealed class PurchaseOptimizerTests
{
    private static PurchaseGood Good(int id, int stock, decimal buy, int weight, int bundle, decimal sell,
        bool group = false, bool locked = false, int? weekly = null, int source = 2) =>
        new(id, source, stock, buy, weight, bundle, new Dictionary<int, decimal> { [7] = sell }, group, locked, weekly);

    [TestMethod]
    public void BarterCostScaledByGoldPerSaleDucatFillsLargerTransportThanBackpack()
    {
        // Karu Forest + Oasis weekly allowances, material Gold and best sale quotes from a real session (Gold per Ducat 2).
        var data = new (int Id, int Stock, decimal MaterialGold, int Weight, int Bundle, decimal Sale)[]
        {
            (1, 3, 173_000m, 35, 3, 76_341m), (2, 3, 231_500m, 30, 5, 160_692m), (3, 8, 93_500m, 25, 7, 32_059m),
            (4, 10, 36_750m, 20, 7, 15_073m), (5, 15, 27_800m, 15, 7, 7_747m), (6, 25, 25_596m, 15, 10, 4_663m),
            (7, 2, 197_000m, 35, 2, 105_554m), (8, 3, 365_000m, 30, 5, 146_850m), (9, 8, 130_444m, 25, 7, 32_729m),
            (10, 10, 34_520m, 20, 7, 13_655m), (11, 15, 41_400m, 15, 7, 6_602m), (12, 25, 25_000m, 15, 10, 3_990m),
        };
        var modifiers = new RewardModifiers("1", GuaranteeLetterKind.ImpFine, 2m, -1, 0m);
        decimal GoldFor(decimal sale)
        {
            var reward = CommerceRewardModel.Calculate([new RewardLine(1, 0m, sale, 0)], true, modifiers, perLoadLetterEffects: false);
            return reward.RawGold + reward.DucatGold;
        }
        var goldPerSaleDucat = (GoldFor(2_000_000m) - GoldFor(1_000_000m)) / 1_000_000m;
        PurchaseSearchResult Run(decimal saleMultiplier, decimal costDivisor) => PurchaseOptimizer.Search(new PurchaseRequest(2, 1e12m,
            data.Select(item => Good(item.Id, item.Stock, item.MaterialGold / costDivisor, item.Weight, item.Bundle, item.Sale * saleMultiplier,
                weekly: item.Stock)).ToList(),
            [new PurchaseTransport(1, 4, 400), new PurchaseTransport(26, 9, 1400)], [new PurchaseDestination(7)]));

        var ducatModel = Run(1m, 2m);
        Assert.AreEqual(ducatModel.Plans.Single(plan => plan.TransportId == 1).Profit.NetProfit,
            ducatModel.Plans.Single(plan => plan.TransportId == 26).Profit.NetProfit,
            "The old Ducat-only cost left too few profitable goods to fill even a Backpack.");

        // Current model: sale Ducats × (raw Gold + Ducats × Gold per Ducat per sale Ducat) − material Gold.
        var goldModel = Run(goldPerSaleDucat, 1m);
        var backpack = goldModel.Plans.Single(plan => plan.TransportId == 1);
        var skiff = goldModel.Plans.Single(plan => plan.TransportId == 26);
        Assert.IsTrue(skiff.Profit.NetProfit > backpack.Profit.NetProfit);
        Assert.IsTrue(skiff.Lines.Count >= 6, $"Skiff carried {skiff.Lines.Count} goods");
        Assert.AreEqual(4, backpack.UsedSlots);
    }

    [TestMethod]
    public void MixBeatsSingleGoodAndPartialBundleUsesSecondSlot()
    {
        var request = new PurchaseRequest(2, 100m,
            [Good(1, 2, 10m, 2, 2, 18m), Good(2, 2, 10m, 3, 2, 17m)],
            [new PurchaseTransport(3, 2, 9)], [new PurchaseDestination(7)]);

        var result = PurchaseOptimizer.Search(request);

        Assert.IsTrue(result.IsExhaustive);
        Assert.IsNotNull(result.Best);
        Assert.AreEqual(2, result.Best.Lines.Count);
        Assert.AreEqual(2, result.Best.Lines.Single(line => line.GoodId == 1).Quantity);
        Assert.AreEqual(1, result.Best.Lines.Single(line => line.GoodId == 2).Quantity);
        Assert.AreEqual(23m, result.Best.Profit.NetProfit);
        Assert.AreEqual(2, result.Best.UsedSlots);
        Assert.AreEqual(7, result.Best.UsedWeight);
    }

    [TestMethod]
    public void SafelyDominatedGoodIsPrunedWhenBetterGoodHasReplacementStock()
    {
        var better = Good(1, 100, 1m, 1, 10, 10m);
        var dominated = Good(2, 1, 2m, 2, 5, 10m);
        var request = new PurchaseRequest(2, 100m, [better, dominated],
            [new PurchaseTransport(1, 1, 10)], [new PurchaseDestination(7)]);

        var result = PurchaseOptimizer.Search(request);

        Assert.IsTrue(result.IsExhaustive);
        Assert.IsNotNull(result.Best);
        Assert.AreEqual(1, result.Best.Lines.Count);
        Assert.AreEqual(1, result.Best.Lines[0].GoodId);
    }

    [TestMethod]
    public void FundsStockAndWeightConstrainWholeUnits()
    {
        var result = PurchaseOptimizer.Search(new PurchaseRequest(2, 25m,
            [Good(1, 50, 9m, 3, 10, 14m), Good(2, 1, 6m, 2, 10, 10m)],
            [new PurchaseTransport(1, 2, 8)], [new PurchaseDestination(7)]));

        Assert.IsNotNull(result.Best);
        Assert.AreEqual(2, result.Best.Lines.Single(line => line.GoodId == 1).Quantity);
        Assert.AreEqual(1, result.Best.Lines.Single(line => line.GoodId == 2).Quantity);
        Assert.AreEqual(8, result.Best.UsedWeight);
        Assert.AreEqual(24m, result.Best.Profit.PurchaseCost);
    }

    [TestMethod]
    public void WeeklyLimitLockedStatusModeAndFlightEligibilityAreHonored()
    {
        var goods = new[]
        {
            Good(1, 20, 1m, 1, 10, 10m),
            Good(2, 20, 1m, 1, 10, 5m, group: true, weekly: 2),
            Good(3, 20, 1m, 1, 10, 100m, group: true, locked: true)
        };
        var transports = new[] { new PurchaseTransport(1, 1, 1), new PurchaseTransport(2, 3, 10, IsAvailable: false),
            new PurchaseTransport(TransportIds.Airship, 10, 100, IsFlight: true) };

        var trade = PurchaseOptimizer.Search(new PurchaseRequest(2, 100m, goods, transports, [new PurchaseDestination(7)]));
        var group = PurchaseOptimizer.Search(new PurchaseRequest(2, 100m, goods, transports, [new PurchaseDestination(7)], GroupMode: true));

        Assert.AreEqual(1, trade.Plans.Count);
        Assert.AreEqual(1, trade.Best!.TransportId);
        Assert.AreEqual(1, trade.Best.Lines[0].GoodId);
        Assert.AreEqual(2, group.Plans.Count);
        Assert.AreEqual(2, group.Best!.Lines[0].Quantity);
        Assert.AreEqual(TransportIds.Airship, group.Best.TransportId);
    }

    [TestMethod]
    public void MissingQuotesAndNonpositiveRouteValuesAreNotInvented()
    {
        var goods = new[] { Good(1, 5, 10m, 1, 5, 20m) };
        var transports = new[] { new PurchaseTransport(1, 1, 5) };
        var destinations = new[] { new PurchaseDestination(7, Minutes: 2m, MapDistance: 5m),
            new PurchaseDestination(8), new PurchaseDestination(9, Minutes: 0m) };

        var timed = PurchaseOptimizer.Search(new PurchaseRequest(2, 100m, goods, transports, destinations, PurchaseObjective.ProfitPerMinute));
        var mapped = PurchaseOptimizer.Search(new PurchaseRequest(2, 100m, goods, transports, destinations, PurchaseObjective.ProfitPerMapUnit));

        Assert.AreEqual(1, timed.Plans.Count);
        Assert.AreEqual(25m, timed.Best!.Score);
        Assert.AreEqual(10m, mapped.Best!.Score);
    }

    [TestMethod]
    public void EfficiencyObjectivesRankDestinationsByTheirSuppliedRouteValues()
    {
        var good = new PurchaseGood(1, 2, 2, 10m, 1, 2,
            new Dictionary<int, decimal> { [7] = 20m, [8] = 30m });
        var destinations = new[] { new PurchaseDestination(7, Minutes: 1m, MapDistance: 10m),
            new PurchaseDestination(8, Minutes: 5m, MapDistance: 5m) };
        var request = new PurchaseRequest(2, 20m, [good], [new PurchaseTransport(1, 1, 2)], destinations);

        Assert.AreEqual(8, PurchaseOptimizer.Search(request).Best!.DestinationId);
        Assert.AreEqual(7, PurchaseOptimizer.Search(request with { Objective = PurchaseObjective.ProfitPerMinute }).Best!.DestinationId);
        Assert.AreEqual(8, PurchaseOptimizer.Search(request with { Objective = PurchaseObjective.ProfitPerMapUnit }).Best!.DestinationId);
    }

    [TestMethod]
    public void PerMinuteComparesTransportSpecificTimesAndSkipsMissingSpeeds()
    {
        var request = new PurchaseRequest(2, 20m, [Good(1, 2, 10m, 1, 2, 20m)],
            [new PurchaseTransport(1, 2, 2), new PurchaseTransport(2, 2, 2), new PurchaseTransport(17, 2, 2)],
            [new PurchaseDestination(7, Minutes: 1m)], PurchaseObjective.ProfitPerMinute,
            TransportMinutes: new Dictionary<(int, int), decimal> { [(7, 1)] = 4m, [(7, 2)] = 2m });

        var result = PurchaseOptimizer.Search(request);

        Assert.IsTrue(result.IsExhaustive);
        Assert.AreEqual(2, result.Plans.Count);
        Assert.AreEqual(2, result.Best!.TransportId);
        Assert.AreEqual(10m, result.Best.Score);
    }

    [TestMethod]
    public void NodeBudgetReportsBestFoundWithoutClaimingExactness()
    {
        var result = PurchaseOptimizer.Search(new PurchaseRequest(2, 100m,
            [Good(1, 1, 5m, 5, 2, 11m), Good(2, 2, 4m, 4, 2, 8m)],
            [new PurchaseTransport(1, 2, 8)], [new PurchaseDestination(7)], MaxSearchNodesPerPair: 2));

        Assert.IsFalse(result.IsExhaustive);
        Assert.AreEqual(2, result.VisitedNodes);
        Assert.IsNotNull(result.Best);
        Assert.IsTrue(result.Best.Profit.PurchaseCost <= 100m);
    }

    [TestMethod]
    public void SourceAndQuoteProvenanceDoNotLeakIntoOtherDestinations()
    {
        var eligible = new PurchaseGood(1, 2, 4, 3m, 1, 4,
            new Dictionary<int, decimal> { [7] = 6m, [8] = 8m },
            IsBuyPriceEstimated: true, EstimatedSellDestinations: new HashSet<int> { 7 });
        var otherSource = Good(2, 5, 1m, 1, 1, 100m, source: 3);
        var result = PurchaseOptimizer.Search(new PurchaseRequest(2, 30m,
            [eligible, otherSource], [new PurchaseTransport(1, 2, 10)],
            [new PurchaseDestination(2), new PurchaseDestination(7), new PurchaseDestination(8)]));

        Assert.AreEqual(2, result.Plans.Count);
        Assert.IsTrue(result.Plans.All(plan => plan.Lines.All(line => line.GoodId == 1)));
        Assert.IsTrue(result.Plans.All(plan => plan.HasEstimatedPrices));
        Assert.AreEqual(8, result.Best!.DestinationId);
    }

    [TestMethod]
    public void ObservedQuotesAreNotMarkedAsEstimated()
    {
        var result = PurchaseOptimizer.Search(new PurchaseRequest(2, 20m,
            [Good(1, 1, 2m, 1, 1, 4m)], [new PurchaseTransport(1, 1, 10)], [new PurchaseDestination(7)]));
        Assert.IsFalse(result.Best!.HasEstimatedPrices);
    }

    [TestMethod]
    public void GlobalBudgetReportsPartialResultsAcrossTransports()
    {
        var result = PurchaseOptimizer.Search(new PurchaseRequest(2, 10m,
            [Good(1, 1, 5m, 5, 2, 11m), Good(2, 2, 4m, 4, 2, 8m)],
            [new PurchaseTransport(1, 2, 8), new PurchaseTransport(2, 2, 8)],
            [new PurchaseDestination(7)], MaxTotalSearchNodes: 4));

        Assert.AreEqual(4L, result.VisitedNodes);
        Assert.IsFalse(result.IsExhaustive);
        Assert.AreEqual(2, result.Plans.Count);
    }

    [TestMethod]
    public void RejectsDuplicateProductsAndHonorsCancellation()
    {
        var item = Good(1, 2, 1m, 1, 2, 3m);
        var duplicate = new PurchaseRequest(2, 10m, [item, item],
            [new PurchaseTransport(1, 1, 2)], [new PurchaseDestination(7)]);
        Assert.ThrowsException<ArgumentException>(() => PurchaseOptimizer.Search(duplicate));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => PurchaseOptimizer.Search(duplicate with
        {
            Goods = [item], MaxTotalSearchNodes = 0
        }));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsException<OperationCanceledException>(() => PurchaseOptimizer.Search(duplicate with
        {
            Goods = [item]
        }, cancellation.Token));
    }

    [TestMethod]
    public void ExtremeFundsAndTinyUnitPriceRespectPhysicalCapacity()
    {
        var result = PurchaseOptimizer.Search(new PurchaseRequest(2, decimal.MaxValue,
            [Good(1, 3, 0.00000001m, 1, 3, 0.00000002m)],
            [new PurchaseTransport(1, 1, 3)], [new PurchaseDestination(7)]));

        Assert.IsTrue(result.IsExhaustive);
        Assert.AreEqual(3, result.Best!.Lines[0].Quantity);
    }

    [TestMethod]
    public void SmallCasesMatchIndependentBruteForce()
    {
        var random = new Random(12345);
        for (var scenario = 0; scenario < 80; scenario++)
        {
            var goods = Enumerable.Range(1, 3).Select(id =>
                Good(id, random.Next(1, 5), random.Next(1, 8), random.Next(1, 5),
                    random.Next(1, 4), random.Next(3, 13))).ToArray();
            var funds = random.Next(3, 21);
            var weight = random.Next(3, 13);
            var slots = random.Next(1, 4);
            var request = new PurchaseRequest(2, funds, goods, [new PurchaseTransport(1, slots, weight)],
                [new PurchaseDestination(7)]);
            var expected = 0m;
            for (var first = 0; first <= goods[0].Stock; first++)
            for (var second = 0; second <= goods[1].Stock; second++)
            for (var third = 0; third <= goods[2].Stock; third++)
            {
                var quantities = new[] { first, second, third };
                var cost = goods.Select((good, index) => good.BuyPrice * quantities[index]).Sum();
                var usedWeight = goods.Select((good, index) => good.Weight * quantities[index]).Sum();
                var usedSlots = goods.Select((good, index) => quantities[index] == 0 ? 0 :
                    (quantities[index] + good.UnitsPerSlot - 1) / good.UnitsPerSlot).Sum();
                if (cost > funds || usedWeight > weight || usedSlots > slots) continue;
                expected = Math.Max(expected, goods.Select((good, index) =>
                    quantities[index] * (good.SellPrices[7] - good.BuyPrice)).Sum());
            }
            var actual = PurchaseOptimizer.Search(request);
            Assert.IsTrue(actual.IsExhaustive, $"Scenario {scenario} unexpectedly hit the search budget.");
            Assert.AreEqual(expected, actual.Best?.Profit.NetProfit ?? 0m, $"Scenario {scenario} returned a suboptimal load.");
        }
    }

    [TestMethod]
    public void RealCatalogDimensionsProduceAFeasibleBoundedProposal()
    {
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "commerce-catalog.json")));
        var goods = catalog.RootElement.GetProperty("products").EnumerateArray()
            .Where(item => item.GetProperty("postId").GetInt32() == 2 && !item.GetProperty("commerceParty").GetBoolean())
            .Select(item =>
            {
                var buyPrice = (item.GetProperty("minPrice").GetDecimal() + item.GetProperty("maxPrice").GetDecimal()) / 2;
                return new PurchaseGood(item.GetProperty("id").GetInt32(), 2, item.GetProperty("maxStock").GetInt32(),
                    buyPrice, item.GetProperty("weight").GetInt32(), item.GetProperty("maxBundle").GetInt32(),
                    new Dictionary<int, decimal> { [7] = buyPrice + 10 }, IsBuyPriceEstimated: true,
                    EstimatedSellDestinations: new HashSet<int> { 7 });
            }).ToArray();
        var transport = catalog.RootElement.GetProperty("transports").EnumerateArray()
            .First(item => item.GetProperty("id").GetInt32() == 3);
        var slots = transport.GetProperty("slots").GetInt32();
        var weight = transport.GetProperty("weight").GetInt32();
        var result = PurchaseOptimizer.Search(new PurchaseRequest(2, 1000m, goods,
            [new PurchaseTransport(3, slots, weight)], [new PurchaseDestination(7)], MaxTotalSearchNodes: 3000));

        Assert.IsNotNull(result.Best);
        Assert.IsTrue(result.Best.HasEstimatedPrices);
        Assert.IsTrue(result.Best.Profit.PurchaseCost <= 1000m);
        Assert.IsTrue(result.Best.UsedWeight <= weight);
        Assert.IsTrue(result.Best.UsedSlots <= slots);
        Assert.IsTrue(result.VisitedNodes <= 3000);
        Assert.IsTrue(result.Best.Lines.All(line => line.Quantity <= goods.Single(good => good.Id == line.GoodId).Stock));
    }
}