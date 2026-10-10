using MabiCommerceNewLife;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalculatorTests;

[TestClass]
public sealed class PriceListOcrParserTests
{
    [DataTestMethod]
    [DataRow("1.034", "529", 505, 1034)]
    [DataRow("1.234", "1.000", 234, 1234)]
    [DataRow("1.234.567", "234.567", 1000000, 1234567)]
    public void TreatsOcrPeriodsAsThousandsSeparators(string sale, string profit, int buyPrice, int expectedPrice)
    {
        var candidate = PriceListOcrParser.ParsePriceCells(
            [new OcrTownRow(10, "Cor", 100, "Cor")],
            [new OcrPriceCellRead(10, sale, 90, profit, 90, false)], buyPrice, false).Single();

        Assert.AreEqual((decimal)expectedPrice, candidate.Price);
        Assert.IsTrue(candidate.Include);
    }

    [DataTestMethod]
    [DataRow("1,034")]
    [DataRow("1.034")]
    [DataRow("1034")]
    public void ParsesSourceAndLinePricesAsWholeDucats(string priceText)
    {
        Assert.AreEqual(1034m, PriceListOcrParser.ParseSourcePrice($"{priceText} Seasonal Ducats / Weight 25", 90)?.Price);
        Assert.AreEqual(1034m, PriceListOcrParser.Parse($"Cor {priceText} 529", [(10, "Cor")], 90).Single().Price);
    }

    [TestMethod]
    public void ParsesFirstPriceForTownAndIgnoresFollowingProfitNumbers()
    {
        var candidates = PriceListOcrParser.Parse(
            "Dunbarton 1,234 60 12.5\nCobh 987 20 9.1",
            [(2, "Dunbarton"), (7, "Cobh")], 83.5f);

        Assert.AreEqual(2, candidates.Count);
        Assert.AreEqual(1234m, candidates[0].Price);
        Assert.AreEqual(987m, candidates[1].Price);
        Assert.AreEqual("83.5%", candidates[0].ConfidenceText);
    }

    [TestMethod]
    public void DoesNotMatchTownNameInsideAnotherWord()
    {
        var candidates = PriceListOcrParser.Parse(
            "Score 1200 50\nCor 850 30",
            [(3, "Cor")], 90f);

        Assert.AreEqual(1, candidates.Count);
        Assert.AreEqual(850m, candidates[0].Price);
        Assert.AreEqual("Cor", candidates[0].SourceLine.Split(' ')[0]);
    }

    [TestMethod]
    public void FiltersRecognizedRowsToEligibleGroupDestinationsAndExcludesSource()
    {
        var rows = new[]
        {
            new OcrTownRow(8, "Qilla", 100, "Qilla"),
            new OcrTownRow(9, "Filia", 120, "Filia"),
            new OcrTownRow(10, "Cor", 140, "Cor"),
            new OcrTownRow(11, "Vales", 160, "Vales"),
            new OcrTownRow(-1, "Smuggler", 180, "Smuggler"),
            new OcrTownRow(2, "Dunbarton", 200, "Dunbarton")
        };

        var destinations = PriceListOcrParser.FilterTownRowsForDestinations(rows, [8, 9, 10, 11], 11);

        CollectionAssert.AreEqual(new[] {8, 9, 10}, destinations.Select(row => row.PostId).ToArray());
    }

    [TestMethod]
    public void IgnoresUnknownTownsAndNonpositivePrices()
    {
        var candidates = PriceListOcrParser.Parse(
            "Unknown 999\nCobh 0\nTara 1,450",
            [(7, "Cobh"), (6, "Tara")], 72f);

        Assert.AreEqual(1, candidates.Count);
        Assert.AreEqual(6, candidates[0].PostId);
        Assert.AreEqual(1450m, candidates[0].Price);
    }

    [TestMethod]
    public void DerivesSalePriceWhenSaleAndProfitDoNotReconcile()
    {
        var candidates = PriceListOcrParser.ParsePriceCells(
            [new OcrTownRow(7, "Cobh", 100, "Cobh")],
            [new OcrPriceCellRead(7, "51", 90, "5", 90, false)], 100m, false);

        Assert.AreEqual(1, candidates.Count);
        Assert.AreEqual(105m, candidates[0].Price);
        Assert.IsFalse(candidates[0].Include);
    }

    [TestMethod]
    public void MissingSaleAndProfitCellDoesNotCreatePriceCandidate()
    {
        Assert.AreEqual(0, PriceListOcrParser.ParsePriceCells(
            [new OcrTownRow(7, "Cobh", 100, "Cobh")],
            [new OcrPriceCellRead(7, string.Empty, 0, string.Empty, 0, false)], 100m, false).Count);
    }

    [TestMethod]
    public void SaleOnlyBarterCellsCrossCheckOnlyWithStrongMajority()
    {
        OcrTownRow[] rows = [new(7, "Cobh", 100, "Cobh"), new(3, "Bangor", 120, "Bangor"), new(2, "Dunbarton", 140, "Dunbarton"),
            new(9, "Qilla", 160, "Qilla"), new(10, "Filia", 180, "Filia")];
        var candidates = PriceListOcrParser.ParseSaleOnlyCells(rows,
        [
            new OcrPriceCellRead(7, "3628", 90, string.Empty, 0, false, "3623×1", 7, 8),
            new OcrPriceCellRead(3, "3387", 90, string.Empty, 0, false, "3381×2", 5, 7),
            new OcrPriceCellRead(2, "3635", 90, string.Empty, 0, false, string.Empty, 3, 3),
            new OcrPriceCellRead(9, "126229", 90, string.Empty, 0, false, "126228×2", 6, 8),
            new OcrPriceCellRead(10, "92155", 90, string.Empty, 0, false, "92158×3", 5, 8)
        ]);
        Assert.AreEqual(5, candidates.Count);
        Assert.IsTrue(candidates.Single(candidate => candidate.PostId == 7).IsCrossChecked);
        Assert.AreEqual(3628m, candidates.Single(candidate => candidate.PostId == 7).Price);
        Assert.IsFalse(candidates.Single(candidate => candidate.PostId == 3).IsCrossChecked);
        Assert.IsFalse(candidates.Single(candidate => candidate.PostId == 2).IsCrossChecked);
        Assert.IsTrue(candidates.Single(candidate => candidate.PostId == 9).IsCrossChecked);
        Assert.IsFalse(candidates.Single(candidate => candidate.PostId == 10).IsCrossChecked);
        Assert.IsFalse(PriceListOcrParser.CanAutoAccept(candidates, rows.Length, false));
        Assert.IsTrue(PriceListOcrParser.CanAutoAccept(candidates.Take(1).ToArray(), 1, false));
    }

    [DataTestMethod]
    [DataRow("", false, false)]
    [DataRow("15", false, true)]
    [DataRow("15", true, false)]
    [DataRow("14", false, false)]
    public void AutoSelectsOnlyExactCrossChecksWithNonestimatedSourcePrice(
        string profitText, bool sourcePriceIsEstimated, bool expectedInclude)
    {
        var candidate = PriceListOcrParser.ParsePriceCells(
            [new OcrTownRow(7, "Cobh", 100, "Cobh")],
            [new OcrPriceCellRead(7, "115", 90, profitText, 90, false)],
            100m, sourcePriceIsEstimated).Single();

        Assert.AreEqual(expectedInclude, candidate.Include);
    }

    [DataTestMethod]
    [DataRow("15", "25", false, 2, true)]
    [DataRow("15", "24", false, 2, false)]
    [DataRow("15", "25", true, 2, false)]
    [DataRow("15", "25", false, 3, false)]
    public void AutoAcceptsOnlyWhenEveryVisibleTownCrossChecks(
        string cobhProfit, string bangorProfit, bool sourcePriceIsEstimated, int visibleTownRows, bool expected)
    {
        var candidates = PriceListOcrParser.ParsePriceCells(
            [new OcrTownRow(7, "Cobh", 100, "Cobh"), new OcrTownRow(3, "Bangor", 120, "Bangor")],
            [new OcrPriceCellRead(7, "115", 90, cobhProfit, 90, false),
             new OcrPriceCellRead(3, "125", 90, bangorProfit, 90, false)],
            100m, sourcePriceIsEstimated);

        Assert.AreEqual(expected, PriceListOcrParser.CanAutoAccept(candidates, visibleTownRows, sourcePriceIsEstimated));
    }

    [DataTestMethod]
    [DataRow("2,194", "8", true)]
    [DataRow("2,194", "81", true)]
    [DataRow("2,194", "18", false)]
    [DataRow("2,184", "8", false)]
    public void CrossChecksSaleWhenProfitReadOnlyDroppedThinOnes(string sale, string profit, bool expectedCrossChecked)
    {
        var candidate = PriceListOcrParser.ParsePriceCells(
            [new OcrTownRow(4, "Emain Macha", 100, "Emain Macha")],
            [new OcrPriceCellRead(4, sale, 90, profit, 90, false)], 1383m, false).Single();

        Assert.AreEqual(expectedCrossChecked, candidate.IsCrossChecked);
        if (expectedCrossChecked) Assert.AreEqual(2194m, candidate.Price);
    }

    [DataTestMethod]
    [DataRow("432", "17", true, true)]
    [DataRow("432", "17", false, false)]
    [DataRow("432", "11", true, false)]
    public void CrossChecksLossWhenReadOnlyDroppedThinOnes(string sale, string profit, bool isLoss, bool expectedCrossChecked)
    {
        var candidate = PriceListOcrParser.ParsePriceCells(
            [new OcrTownRow(8, "Belvast", 100, "Belvast")],
            [new OcrPriceCellRead(8, sale, 90, profit, 90, isLoss)], 549m, false).Single();

        Assert.AreEqual(expectedCrossChecked, candidate.IsCrossChecked);
        if (expectedCrossChecked) Assert.AreEqual(432m, candidate.Price);
    }

    [DataTestMethod]
    [DataRow("19684", "49", true)]
    [DataRow("19684", "47", true)]
    [DataRow("19684", "99", false)]
    [DataRow("19684", "419", false)]
    [DataRow("19684", "4", false)]
    public void CrossChecksProfitWithDroppedOnesAndOneSevenReadAsNine(string sale, string profit, bool expectedCrossChecked)
    {
        var candidate = PriceListOcrParser.ParsePriceCells(
            [new OcrTownRow(11, "Tailteann", 100, "Tailteann")],
            [new OcrPriceCellRead(11, sale, 90, profit, 90, false)], 15513m, false).Single();

        Assert.AreEqual(expectedCrossChecked, candidate.IsCrossChecked);
        if (expectedCrossChecked) Assert.AreEqual(19684m, candidate.Price);
    }

    [TestMethod]
    public void DerivesSalePriceFromProfitAndLeavesItUncheckedForReview()
    {
        var candidate = PriceListOcrParser.ParsePriceCells(
            [new OcrTownRow(7, "Cobh", 100, "Cobh")],
            [new OcrPriceCellRead(7, string.Empty, 0, "15", 92, false)], 100m, true).Single();

        Assert.AreEqual(115m, candidate.Price);
        Assert.IsFalse(candidate.Include);
        StringAssert.Contains(candidate.PriceSourceText, "estimated source price");
    }

    [TestMethod]
    public void FindsUniqueGoodNameAndRefusesAmbiguousMatches()
    {
        var goods = new[]
        {
            new OcrProductIdentity(101, 2, "Baby Potion"),
            new OcrProductIdentity(102, 3, "Wool Boots")
        };

        Assert.AreEqual(101, PriceListOcrParser.FindUniqueProduct("Baby Potion 11 Seasonal Ducats", goods)?.Id);
        Assert.IsNull(PriceListOcrParser.FindUniqueProduct("Potion 11", goods));
    }

    [TestMethod]
    public void ToleratesOneMisreadLetterInTheGoodName()
    {
        var goods = new[]
        {
            new OcrProductIdentity(22003, 202, "Oasis Painting"),
            new OcrProductIdentity(23006, 203, "Lake Calida Painting"),
            new OcrProductIdentity(22001, 202, "Fine Sand")
        };

        Assert.AreEqual(22003, PriceListOcrParser.FindUniqueProduct("Qasis Painting", goods)?.Id);
        Assert.AreEqual(22003, PriceListOcrParser.FindUniqueProduct("Oasis Paintinq\nInventory 28,161", goods)?.Id);
        Assert.AreEqual(22003, PriceListOcrParser.FindUniqueProduct("0asis Painting", goods)?.Id);
        Assert.IsNull(PriceListOcrParser.FindUniqueProduct("Oasis Pa1nt1nq", goods));
        Assert.IsNull(PriceListOcrParser.FindUniqueProduct("Inventory 28,161", goods));
    }

    // Giant Canine Fossil from Oasis: weights from the client InterPostWeight table, range 68,000–96,000.
    private static readonly Dictionary<int, decimal> OasisWeights = new()
    {
        [1] = 1.82m, [2] = 1.7m, [3] = 1.59m, [4] = 1.76m, [5] = 1.89m, [6] = 1.93m,
        [7] = 1.7m, [8] = 1.82m, [101] = 1.63m, [102] = 1.19m, [103] = 1.32m, [104] = 1.61m
    };

    private static List<PriceListOcrCandidate> FossilCandidates(params (int PostId, decimal Price)[] overrides)
    {
        var prices = new Dictionary<int, decimal>
        {
            [1] = 140706, [2] = 131760, [3] = 123131, [4] = 136354, [5] = 146241, [6] = 147080,
            [7] = 131706, [8] = 140824, [101] = 126229, [102] = 92155, [103] = 102222, [104] = 124785
        };
        foreach (var (postId, price) in overrides) prices[postId] = price;
        return prices.Select(pair => new PriceListOcrCandidate
        {
            Include = true, PostId = pair.Key, PostName = $"Town {pair.Key}", Price = pair.Value,
            PriceSourceText = "Barter sale; 8/8 reads agree", IsCrossChecked = true
        }).ToList();
    }

    [TestMethod]
    public void BarterValueCheckAcceptsObservedPricesIncludingDemandDrops()
    {
        var checkedCandidates = PriceListOcrParser.ApplyBarterValueCheck(FossilCandidates(), OasisWeights, 68000, 96000);

        Assert.IsTrue(checkedCandidates.All(item => item.IsCrossChecked && item.Include));
        Assert.IsTrue(checkedCandidates.All(item => item.PriceSourceText.EndsWith("fits game value", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void BarterValueCheckAcceptsKaruWoodenTableWithLowTara()
    {
        // Native Karu Forest capture: every town fits a ~2,330 base within 0.7% except Tara, 5.8% under its
        // client weight of 2.09 (Tara has read low in every barter capture).
        var weights = new Dictionary<int, decimal>
        {
            [1] = 1.93m, [2] = 1.83m, [3] = 1.92m, [4] = 1.89m, [5] = 2.01m, [6] = 2.09m,
            [7] = 1.8m, [8] = 1.92m, [101] = 1.36m, [102] = 1.5m, [103] = 1.57m, [104] = 1.86m
        };
        var prices = new Dictionary<int, decimal>
        {
            [1] = 4485, [2] = 4279, [3] = 4474, [4] = 4396, [5] = 4663, [6] = 4590,
            [7] = 4199, [8] = 4481, [101] = 3170, [102] = 3507, [103] = 3653, [104] = 4349
        };
        var candidates = prices.Select(pair => new PriceListOcrCandidate
        {
            Include = true, PostId = pair.Key, PostName = $"Town {pair.Key}", Price = pair.Value,
            PriceSourceText = "Barter sale; 8/8 reads agree", IsCrossChecked = true
        }).ToList();

        var checkedCandidates = PriceListOcrParser.ApplyBarterValueCheck(candidates, weights, 2040, 2880);

        Assert.IsTrue(checkedCandidates.All(item => item.IsCrossChecked && item.Include),
            string.Join(" | ", checkedCandidates.Select(item => $"{item.PostId}: {item.PriceSourceText}")));
    }

    [TestMethod]
    public void BarterValueCheckAcceptsAgreeingDemandDropsWithinTheGameRange()
    {
        // Prison Ghost Wings from Oasis (range 3,094–4,368): Taillteann and Tara were 9.3% and 12.5% below the model.
        var prices = new Dictionary<int, decimal>
        {
            [1] = 6363, [2] = 6135, [3] = 5695, [4] = 6315, [5] = 6158, [6] = 6066,
            [7] = 6144, [8] = 6377, [101] = 5857, [102] = 4291, [103] = 4776, [104] = 5809
        };
        List<PriceListOcrCandidate> Candidates(bool tailteannAgrees) => prices.Select(pair => new PriceListOcrCandidate
        {
            Include = true, PostId = pair.Key, PostName = $"Town {pair.Key}", Price = pair.Value,
            PriceSourceText = "Barter sale; 8/8 reads agree", IsCrossChecked = pair.Key != 5 || tailteannAgrees
        }).ToList();

        var checkedCandidates = PriceListOcrParser.ApplyBarterValueCheck(Candidates(true), OasisWeights, 3094, 4368);

        Assert.IsTrue(checkedCandidates.All(item => item.IsCrossChecked && item.Include),
            string.Join(" | ", checkedCandidates.Select(item => $"{item.PostId}: {item.PriceSourceText}")));
        StringAssert.Contains(checkedCandidates.Single(item => item.PostId == 6).PriceSourceText, "demand drop");

        var unconfirmed = PriceListOcrParser.ApplyBarterValueCheck(Candidates(false), OasisWeights, 3094, 4368)
            .Single(item => item.PostId == 5);
        Assert.IsFalse(unconfirmed.Include);
        StringAssert.Contains(unconfirmed.PriceSourceText, "game value model");

        // Demand lowers the sale price itself, so this Tara price is accepted even though 6,066 / 1.93 is below a
        // 3,300 minimum; the 20% cap below still catches wrong leading digits.
        var belowRange = PriceListOcrParser.ApplyBarterValueCheck(Candidates(true), OasisWeights, 3300, 4368)
            .Single(item => item.PostId == 6);
        Assert.IsTrue(belowRange.Include);

        // Wrong leading digits (6,066 -> 4,066 or 8,066) need review even when the range is wide.
        foreach (var misread in new[] { 4066m, 8066m })
        {
            prices[6] = misread;
            var wrongDigits = PriceListOcrParser.ApplyBarterValueCheck(Candidates(true), OasisWeights, 1000, 9000)
                .Single(item => item.PostId == 6);
            Assert.IsFalse(wrongDigits.Include, $"{misread}");
            Assert.IsFalse(wrongDigits.IsCrossChecked, $"{misread}");
            StringAssert.Contains(wrongDigits.PriceSourceText, "game value model");
        }
    }

    [TestMethod]
    public void BarterValueCheckAcceptsWoodenCraftTaraDemandDropBelowTheBaseMinimum()
    {
        // Native Karu Forest capture: Tara 7,012 is 15% below the model and implies 3,355, under the 3,400 minimum.
        var weights = new Dictionary<int, decimal>
        {
            [1] = 1.93m, [2] = 1.83m, [3] = 1.92m, [4] = 1.89m, [5] = 2.01m, [6] = 2.09m,
            [7] = 1.8m, [8] = 1.92m, [101] = 1.36m, [102] = 1.5m, [103] = 1.57m, [104] = 1.86m
        };
        var prices = new Dictionary<int, decimal>
        {
            [1] = 7442, [2] = 7283, [3] = 7415, [4] = 7445, [5] = 7240, [6] = 7012,
            [7] = 7184, [8] = 7463, [101] = 5411, [102] = 6003, [103] = 6258, [104] = 7362
        };
        var candidates = prices.Select(pair => new PriceListOcrCandidate
        {
            Include = true, PostId = pair.Key, PostName = $"Town {pair.Key}", Price = pair.Value,
            PriceSourceText = "Barter sale; 7/7 reads agree", IsCrossChecked = true
        }).ToList();

        var checkedCandidates = PriceListOcrParser.ApplyBarterValueCheck(candidates, weights, 3400, 4800);

        Assert.IsTrue(checkedCandidates.All(item => item.IsCrossChecked && item.Include),
            string.Join(" | ", checkedCandidates.Select(item => $"{item.PostId}: {item.PriceSourceText}")));
    }

    [TestMethod]
    public void BarterValueCheckRejectsMisreadLeadingAndMiddleDigits()
    {
        var checkedCandidates = PriceListOcrParser.ApplyBarterValueCheck(
            FossilCandidates((1, 40706), (2, 181760), (3, 128131)), OasisWeights, 68000, 96000);

        foreach (var postId in new[] { 1, 2, 3 })
        {
            var candidate = checkedCandidates.Single(item => item.PostId == postId);
            Assert.IsFalse(candidate.IsCrossChecked, $"{postId}");
            Assert.IsFalse(candidate.Include, $"{postId}");
            StringAssert.Contains(candidate.PriceSourceText, "game value model");
        }
        Assert.IsTrue(checkedCandidates.Where(item => item.PostId > 3).All(item => item.IsCrossChecked));
    }

    [TestMethod]
    public void BarterValueCheckRejectsAllPricesWhenImpliedBaseIsOutsideTheGameRange()
    {
        var checkedCandidates = PriceListOcrParser.ApplyBarterValueCheck(FossilCandidates(), OasisWeights, 1870, 2640);

        Assert.IsTrue(checkedCandidates.All(item => !item.IsCrossChecked && !item.Include));
        Assert.IsTrue(checkedCandidates.All(item => item.PriceSourceText.Contains("outside game range", StringComparison.Ordinal)));
    }
}