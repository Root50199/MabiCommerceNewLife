using MabiCommerceNewLife;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalculatorTests;

[TestClass]
public sealed class CommerceRewardModelTests
{
    private static RewardModifiers Modifiers(string rank = "1", GuaranteeLetterKind letter = GuaranteeLetterKind.None,
        decimal rate = 1.2m, int supply = -1, decimal letterValue = 0m) => new(rank, letter, rate, supply, letterValue);

    [TestMethod]
    public void MasteryPercentRunsFromRankNToRankOne()
    {
        Assert.AreEqual(0, CommerceRewardModel.MasteryPercent("N"));
        Assert.AreEqual(1, CommerceRewardModel.MasteryPercent("F"));
        Assert.AreEqual(6, CommerceRewardModel.MasteryPercent("A"));
        Assert.AreEqual(7, CommerceRewardModel.MasteryPercent("9"));
        Assert.AreEqual(15, CommerceRewardModel.MasteryPercent("1"));
        Assert.AreEqual(0, CommerceRewardModel.MasteryPercent("bogus"));
    }

    [TestMethod]
    public void RankOneMasteryMatchesObservedGoldReward()
    {
        // Observed in game: 6,912 Ducat profit at Commerce Mastery rank 1 paid 7,948 Gold.
        var reward = CommerceRewardModel.Calculate([new RewardLine(1, 1_000m, 7_912m, 10)], false, Modifiers());

        Assert.AreEqual(6_912m, reward.BaseProfit);
        Assert.AreEqual(1_036m, reward.MasteryBonus);
        Assert.AreEqual(7_948m, reward.RawGold);
        Assert.AreEqual(7_948m, reward.DucatGain);
        Assert.AreEqual(7_948m + 7_948m * 1.2m, reward.TotalGold);
    }

    [TestMethod]
    public void LetterUsesTradeOrBarterPercentAndGrantsUseDucats()
    {
        var trade = CommerceRewardModel.Calculate([new RewardLine(10, 100m, 200m, 5)], false,
            Modifiers("N", GuaranteeLetterKind.Imp));
        Assert.IsTrue(trade.LetterApplied);
        Assert.AreEqual(500m, trade.LetterBonus);
        Assert.AreEqual(1_000m + 500m + 500m, trade.DucatGain);
        Assert.AreEqual(1_500m, trade.RawGold);

        var barter = CommerceRewardModel.Calculate([new RewardLine(10, 100m, 200m, 5)], true,
            Modifiers("N", GuaranteeLetterKind.Imp, rate: 2m));
        Assert.AreEqual(2_000m, barter.BaseProfit);
        Assert.AreEqual(500m, barter.LetterBonus);
        Assert.AreEqual(1_000m, barter.MaterialGold);
        Assert.AreEqual(2_500m + (2_000m + 500m + 500m) * 2m - 1_000m, barter.TotalGold);
    }

    [TestMethod]
    public void BarterGoldTotalIsLinearInSaleDucatsSoAutoCanRankByScaledNetProfit()
    {
        var modifiers = Modifiers("1", GuaranteeLetterKind.Imp, rate: 1.2m);
        decimal Total(decimal sale, decimal materialDucats) =>
            CommerceRewardModel.Calculate([new RewardLine(1, materialDucats, sale, 0)], true, modifiers, perLoadLetterEffects: false).TotalGold;
        decimal Gold(decimal sale)
        {
            var reward = CommerceRewardModel.Calculate([new RewardLine(1, 0m, sale, 0)], true, modifiers, perLoadLetterEffects: false);
            return reward.RawGold + reward.DucatGold;
        }
        var goldPerSaleDucat = (Gold(2_000_000m) - Gold(1_000_000m)) / 1_000_000m;
        var offset = Total(100_000m, 0m) - 100_000m * goldPerSaleDucat;

        foreach (var (sale, material) in new[] { (50_000m, 10_000m), (148_934m, 42_000m), (400_000m, 250_000m) })
        {
            var expected = sale * goldPerSaleDucat - material + offset;
            Assert.AreEqual(expected, Total(sale, material), 2m, $"sale {sale}, material {material}");
        }
    }

    [TestMethod]
    public void IrusanLetterAddsDucatsButNoGold()
    {
        var reward = CommerceRewardModel.Calculate([new RewardLine(1, 0m, 1_000m, 1)], false,
            Modifiers("N", GuaranteeLetterKind.IrusanSpecial), perLoadLetterEffects: false);

        Assert.AreEqual(3_000m, reward.LetterBonus);
        Assert.AreEqual(0m, reward.LetterGoldBonus);
        Assert.AreEqual(1_000m, reward.RawGold);
        Assert.AreEqual(4_000m, reward.DucatGain);
    }

    [TestMethod]
    public void LetterIsSkippedWhenOutOfStockOrUnprofitable()
    {
        RewardLine[] profitable = [new RewardLine(1, 100m, 200m, 1)];
        Assert.IsTrue(CommerceRewardModel.Calculate(profitable, false, Modifiers(letter: GuaranteeLetterKind.Ogre)).LetterApplied);
        Assert.IsFalse(CommerceRewardModel.Calculate(profitable, false, Modifiers(letter: GuaranteeLetterKind.Ogre, supply: 0)).LetterApplied);
        Assert.IsTrue(CommerceRewardModel.Calculate(profitable, false, Modifiers(letter: GuaranteeLetterKind.Ogre, supply: 3)).LetterApplied);

        var loss = CommerceRewardModel.Calculate([new RewardLine(1, 200m, 100m, 1)], false, Modifiers(letter: GuaranteeLetterKind.Ogre));
        Assert.IsFalse(loss.LetterApplied);
        Assert.AreEqual(0m, loss.RawGold);
        Assert.AreEqual(-100m, loss.DucatGain);
    }

    [TestMethod]
    public void LetterAvailabilityForAutomatedSelectionNeedsMarketValue()
    {
        Assert.IsFalse(GuaranteeLetters.IsAvailable(null));
        Assert.IsFalse(GuaranteeLetters.IsAvailable(0));
        Assert.IsTrue(GuaranteeLetters.IsAvailable(1));
        Assert.IsTrue(GuaranteeLetters.IsAvailable(5_000));
    }

    [TestMethod]
    public void LetterMarketValueIsSubtractedOncePerLoad()
    {
        RewardLine[] lines = [new RewardLine(10, 100m, 200m, 5)];
        var withValue = CommerceRewardModel.Calculate(lines, false, Modifiers("N", GuaranteeLetterKind.Imp, rate: 1m, letterValue: 5_000m));
        var free = CommerceRewardModel.Calculate(lines, false, Modifiers("N", GuaranteeLetterKind.Imp, rate: 1m));
        Assert.AreEqual(5_000m, withValue.LetterCostGold);
        Assert.AreEqual(free.TotalGold - 5_000m, withValue.TotalGold);

        var perUnit = CommerceRewardModel.Calculate(lines, false, Modifiers("N", GuaranteeLetterKind.Imp, letterValue: 5_000m), perLoadLetterEffects: false);
        Assert.AreEqual(0m, perUnit.LetterCostGold);
    }

    [TestMethod]
    public void MerchantRatingDiscountFollowsTheCreditLevelTable()
    {
        MerchantRatingLevel[] levels = [.. new[] { 0, 0, 0, 0, 1, 1, 2, 2, 3 }.Select((discount, index) =>
            new MerchantRatingLevel { Level = index + 1, Discount = discount })];
        Assert.AreEqual(0m, MerchantRatingTable.DiscountPercent(0, levels));
        Assert.AreEqual(0m, MerchantRatingTable.DiscountPercent(4, levels));
        Assert.AreEqual(1m, MerchantRatingTable.DiscountPercent(5, levels));
        Assert.AreEqual(2m, MerchantRatingTable.DiscountPercent(8, levels));
        Assert.AreEqual(3m, MerchantRatingTable.DiscountPercent(9, levels));
        Assert.AreEqual(3m, MerchantRatingTable.DiscountPercent(12, levels));
    }

    [TestMethod]
    public void ModifierTotalsCapStacksAndSumEveryEffect()
    {
        var counts = new Dictionary<string, int>
        {
            ["title-event-pass"] = 1,
            ["partner-william-bonus"] = 9,
            ["talent-mercantile-grandmaster"] = 1,
            ["speed-transport-potion"] = 1,
            ["speed-floating-stone"] = 1,
            ["enchant-commerce"] = 2,
            ["speed-commerce-reforge"] = 15
        };
        var totals = CommerceModifiers.Totals(counts, groupGoods: false);
        Assert.AreEqual(8m, totals.DucatPercent);
        Assert.AreEqual(0m, totals.ProfitPercent);
        Assert.AreEqual(0m, totals.PurchaseDiscountPercent);
        Assert.AreEqual(1, totals.ExtraSlots);
        Assert.AreEqual(100, totals.ExtraWeight);
        Assert.AreEqual(0m, totals.MerchantRatingPercent);
        Assert.AreEqual(75m, totals.TransportSpeedPercent);
    }

    [TestMethod]
    public void ModifierCatalogDropsExpiredCommerceMasterTitleAndCountedEnchants()
    {
        Assert.IsFalse(CommerceModifiers.All.Any(modifier => modifier.Id == "title-commerce-master"));
        Assert.IsFalse(CommerceModifiers.All.Any(modifier => modifier.Id.StartsWith("enchant-", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AccessoryEnchantsSumPerSlotAndClampRolls()
    {
        var ids = new Dictionary<string, string>
        {
            [AccessoryEnchants.SlotKey(1, EnchantPosition.Prefix)] = "trustworthy",
            [AccessoryEnchants.SlotKey(1, EnchantPosition.Suffix)] = "road",
            [AccessoryEnchants.SlotKey(2, EnchantPosition.Prefix)] = "silk",
            [AccessoryEnchants.SlotKey(2, EnchantPosition.Suffix)] = "trader"
        };
        var rolls = new Dictionary<string, int>
        {
            [AccessoryEnchants.SlotKey(1, EnchantPosition.Suffix)] = 9,
            [AccessoryEnchants.SlotKey(2, EnchantPosition.Prefix)] = 2
        };
        var totals = AccessoryEnchants.Totals(ids, rolls);
        Assert.AreEqual(1m + 3m + 3m, totals.PurchaseDiscountPercent);
        Assert.AreEqual(3m + 2m + 2m, totals.MerchantRatingPercent);
    }

    [TestMethod]
    public void AccessoryEnchantsIgnoreWrongPositionAndDefaultRollToMinimum()
    {
        var ids = new Dictionary<string, string>
        {
            [AccessoryEnchants.SlotKey(1, EnchantPosition.Prefix)] = "road",
            [AccessoryEnchants.SlotKey(2, EnchantPosition.Prefix)] = "silk"
        };
        var totals = AccessoryEnchants.Totals(ids, new Dictionary<string, int>());
        Assert.AreEqual(0m, totals.PurchaseDiscountPercent);
        Assert.AreEqual(1m, totals.MerchantRatingPercent);
    }

    [TestMethod]
    public void AccessoryEnchantRollRangesMatchGame()
    {
        Assert.AreEqual(4, AccessoryEnchants.Find("silk", EnchantPosition.Prefix)!.MaxRoll);
        Assert.AreEqual(3, AccessoryEnchants.Find("road", EnchantPosition.Suffix)!.MaxRoll);
        Assert.IsTrue(AccessoryEnchants.All.Where(enchant => enchant.HasRoll).All(enchant => enchant.MinRoll == 1));
    }

    [TestMethod]
    public void ModifierCatalogHasNoPartnerCapacityOrRandomGroupBuffs()
    {
        var ids = CommerceModifiers.All.Select(modifier => modifier.Id).ToHashSet();
        Assert.IsFalse(ids.Contains("partner-william-owned"));
        Assert.IsFalse(ids.Contains("partner-commerce-owned"));
        Assert.IsFalse(CommerceModifiers.All.Any(modifier => modifier.GroupGoodsOnly));
        Assert.AreEqual(1, CommerceModifiers.All.Count(modifier => modifier.ExtraSlots > 0));
    }

    [DataTestMethod]
    [DataRow(1256, 1381)]
    [DataRow(891, 980)]
    [DataRow(882, 970)]
    public void GroupDestinationBonusMatchesObservedStarredPrices(int basePrice, int boosted) =>
        Assert.AreEqual((decimal)boosted, GroupDestinationBonus.Apply(basePrice));

    [TestMethod]
    public void TransportSpeedDividesTravelTime()
    {
        Assert.AreEqual(10m, TransportSpeed.ApplyToMinutes(10m, 0m));
        Assert.AreEqual(10m, TransportSpeed.ApplyToMinutes(12m, 20m));
    }

    [TestMethod]
    public void ExtraDucatPercentAddsDucatsOnlyAndProfitPercentAddsBoth()
    {
        RewardLine[] lines = [new RewardLine(10, 100m, 200m, 5)];
        var ducatsOnly = CommerceRewardModel.Calculate(lines, false, new RewardModifiers("N", GuaranteeLetterKind.None, 1m, ExtraDucatPercent: 5));
        Assert.AreEqual(50m, ducatsOnly.ExtraDucatBonus);
        Assert.AreEqual(1_050m, ducatsOnly.DucatGain);
        Assert.AreEqual(1_000m, ducatsOnly.RawGold);

        var profit = CommerceRewardModel.Calculate(lines, false, new RewardModifiers("N", GuaranteeLetterKind.None, 1m, ExtraProfitPercent: 4));
        Assert.AreEqual(1_040m, profit.DucatGain);
        Assert.AreEqual(1_040m, profit.RawGold);
    }

    [TestMethod]
    public void OgreLetterExportMatchesTheInGameBabyPotionSale()
    {
        // 2026-10-04 export: 455 Baby Potions bought at 10, sold at Filia for 108, Rank 1 mastery, Ogre letter,
        // two Trader suffixes (+4% rating gain). Game: Gold 64,655, EXP 178,132, Tir Merchant Rating +53,061.
        var reward = CommerceRewardModel.Calculate([new RewardLine(455, 10m, 108m, 1, PurchaseDiscountPercent: 7)], false,
            new RewardModifiers("1", GuaranteeLetterKind.Ogre, 2m, MerchantRatingPercent: 4));

        Assert.AreEqual(44_590m, reward.BaseProfit);
        Assert.AreEqual(64_655m, reward.RawGold);
        Assert.AreEqual(178_132m, reward.Exp);
        Assert.AreEqual(53_061m, reward.MerchantRating);
        // Export Seasonal Ducats 69,205 is the 49,140 sale + 20,065 bonus; the letter's 300 Ducats are paid on use.
        Assert.AreEqual(69_205m - 4_550m + 300m, reward.DucatGain);
        // 7% discount (Tir rating 1% + two Trader 3%): original buy 11, so (108 - 11) × 455 / 17 = 2,596.
        Assert.AreEqual(2_596m, reward.SeasonalScore);
    }

    [TestMethod]
    public void NoLetterExportMatchesTheInGameSpiderGlovesSale()
    {
        // 2026-10-04 export: 154 Spider Gloves bought at 39, sold at Cobh for 45, Rank 1 mastery, no letter, +4% rating gain.
        // Game: Seasonal Ducats 7,068 (6,930 + 138), Gold 1,062, EXP 21,252, Dunbarton Merchant Rating +1,098.
        var reward = CommerceRewardModel.Calculate([new RewardLine(154, 39m, 45m, 4, PurchaseDiscountPercent: 8)], false,
            new RewardModifiers("1", GuaranteeLetterKind.None, 2m, MerchantRatingPercent: 4));

        Assert.AreEqual(1_062m, reward.RawGold);
        Assert.AreEqual(1_062m, reward.DucatGain);
        Assert.AreEqual(21_252m, reward.Exp);
        Assert.AreEqual(1_098m, reward.MerchantRating);
        // 8% discount (Dunbarton rating 2% + 6%): original buy 42, so 3 × 154 / 17 = 27.
        Assert.AreEqual(27m, reward.SeasonalScore);
    }

    [TestMethod]
    public void SeasonalScoreUsesTheUndiscountedBuyPriceForTheSharkFinSale()
    {
        // 2026-10-04 export: 1 Shark Fin bought at Cobh for 250 (7% discount), sold at Bangor for 347. Game: Gold 111,
        // EXP 1,069, Cobh rating +114, S-Commerce Score 4 (original buy 269, not profit 97 / 17 = 5).
        var reward = CommerceRewardModel.Calculate([new RewardLine(1, 250m, 347m, 10, PurchaseDiscountPercent: 7)], false,
            new RewardModifiers("1", GuaranteeLetterKind.None, 2m, MerchantRatingPercent: 4));

        Assert.AreEqual(111m, reward.RawGold);
        Assert.AreEqual(1_069m, reward.Exp);
        Assert.AreEqual(114m, reward.MerchantRating);
        Assert.AreEqual(4m, reward.SeasonalScore);
    }

    [TestMethod]
    public void PerfectSquareExpMatchesTheSingleItemSeaweedSale()
    {
        // 2026-10-04 export: 1 Cobh Seaweed bought at 11, sold at Dunbarton for 13, Rank 1. Game: EXP 34 (30 + 4), Gold 2, rating 2.
        var reward = CommerceRewardModel.Calculate([new RewardLine(1, 11m, 13m, 2)], false,
            new RewardModifiers("1", GuaranteeLetterKind.None, 2m, MerchantRatingPercent: 4));

        Assert.AreEqual(34m, reward.Exp);
        Assert.AreEqual(2m, reward.RawGold);
        Assert.AreEqual(2m, reward.DucatGain);
        Assert.AreEqual(2m, reward.MerchantRating);
        Assert.AreEqual(0m, reward.SeasonalScore);
    }

    [DataTestMethod]
    // 2026-10-04 Emain Macha -> Cobh, one export per good, Rank 1, 7% discount (rating 6 + two Trader), +4% rating gain.
    [DataRow(200, 11, 46, 3, 8_050, 10_250 - 9_200, 69_000, 8_330, 400)]   // Berry Granola
    [DataRow(80, 179, 262, 10, 7_636, 21_956 - 20_960, 77_280, 7_901, 329)] // Smoked Wild Animal
    public void EmainToCobhExportsMatchEveryReward(int quantity, int buy, int sale, int weight,
        int gold, int ducatBonus, int exp, int rating, int score)
    {
        var reward = CommerceRewardModel.Calculate([new RewardLine(quantity, buy, sale, weight, PurchaseDiscountPercent: 7)], false,
            new RewardModifiers("1", GuaranteeLetterKind.None, 2m, MerchantRatingPercent: 4));

        Assert.AreEqual(gold, reward.RawGold);
        Assert.AreEqual(ducatBonus, reward.MasteryBonus);
        Assert.AreEqual(exp, reward.Exp);
        Assert.AreEqual(rating, reward.MerchantRating);
        Assert.AreEqual(score, reward.SeasonalScore);
    }

    [TestMethod]
    public void FormulaShowsGoldDucatsRateAndTotal()
    {
        var reward = CommerceRewardModel.Calculate([new RewardLine(1, 1_000m, 7_912m, 10)], false, Modifiers());
        var text = CommerceRewardModel.FormatFormula(reward);

        StringAssert.Contains(text, "×1.2=");
        StringAssert.EndsWith(text, "k");
    }

    [TestMethod]
    public void DiscountsWidenOnlyTheLowerBuyPriceBound()
    {
        // Observed scan buy prices below the catalog minimum, explained by purchase discounts.
        Assert.IsFalse(PurchasePriceRange.IsPlausibleBuyPrice(794m, 802, 915, 0m));
        Assert.IsTrue(PurchasePriceRange.IsPlausibleBuyPrice(794m, 802, 915, 1m));
        Assert.IsFalse(PurchasePriceRange.IsPlausibleBuyPrice(7_620m, 7_775, 8_830, 1m));
        Assert.IsTrue(PurchasePriceRange.IsPlausibleBuyPrice(7_620m, 7_775, 8_830, 2m));
        Assert.IsFalse(PurchasePriceRange.IsPlausibleBuyPrice(9_000m, 7_775, 8_830, 10m));
        Assert.IsFalse(PurchasePriceRange.IsPlausibleBuyPrice(0m, 7_775, 8_830, 3m));
    }
}
