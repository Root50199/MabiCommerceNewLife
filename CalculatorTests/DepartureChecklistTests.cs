using MabiCommerceNewLife;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalculatorTests;

[TestClass]
public sealed class DepartureChecklistTests
{
    private static DepartureChecklistContext Context() => new(
        "Cobh to Belvast", "Wagon", GuaranteeLetterKind.None, false, false, false, false, false,
        new Dictionary<string, int>(), new Dictionary<string, string>(), new Dictionary<string, int>());

    [TestMethod]
    public void NoRouteGivesTheGeneralEverythingList()
    {
        var items = DepartureChecklist.Build(null);
        CollectionAssert.AreEqual(new[]
        {
            "Summon Alpaca / partner.",
            "Activate Grandmaster Merchant.",
            "Letter of Guarantee",
            "Gear",
            "Equip the correct commerce title.",
            "Expansion Cover",
            "Check speed potions and Commerce Floating Stone.",
            "Bandit Candy",
            "Check Arcana.",
            "Check destination for Group commerce."
        }, items.Select(item => item.Text).ToArray());
        Assert.AreEqual(items.Count, items.Select(item => item.Key).Distinct().Count());
    }

    [TestMethod]
    public void BanditCandyAppearsOnceInBothListsAsAnIndependentReminder()
    {
        foreach (var items in new[] { DepartureChecklist.Build(null), DepartureChecklist.Build(Context()) })
        {
            var candy = items.Single(item => item.Text == "Bandit Candy");
            Assert.AreEqual("bandit-candy:Bandit Candy", candy.Key);
            Assert.IsNull(candy.ParentKey);
        }
    }

    [TestMethod]
    public void ActiveRouteNamesTransportAndKeepsGearAndTitleReminders()
    {
        var items = DepartureChecklist.Build(Context());
        Assert.IsTrue(items.Any(item => item.Text.Contains("Cobh to Belvast")));
        Assert.IsTrue(items.Any(item => item.Text.Contains("Wagon")));
        Assert.IsTrue(items.Any(item => item.Key.StartsWith("gear:")));
        Assert.IsTrue(items.Any(item => item.Key.StartsWith("title:")));
        Assert.IsFalse(items.Any(item => item.Key.StartsWith("letter:") || item.Key.StartsWith("cover:")));
        Assert.IsFalse(items.Any(item => item.Key.StartsWith("materials:") || item.Key.StartsWith("group:")));
    }

    [TestMethod]
    public void SelectedLetterAndCoverOnlyAppearWhenUsed()
    {
        var items = DepartureChecklist.Build(Context() with { Letter = GuaranteeLetterKind.ImpFine, UsesCover = true });
        Assert.AreEqual("Imp's Fine Letter of Guarantee", items.Single(item => item.Key.StartsWith("letter:")).Text);
        Assert.AreEqual("Expansion Cover", items.Single(item => item.Key.StartsWith("cover:")).Text);
        Assert.IsFalse(items.Any(item => item.Text.Contains("Ogre's")));
    }

    [TestMethod]
    public void ConsumableLabelsStayConciseRegardlessOfInventory()
    {
        var context = Context() with { Letter = GuaranteeLetterKind.Goblin, NeedsLetterPurchase = true, UsesCover = true, NeedsCoverPurchase = true };
        var items = DepartureChecklist.Build(context);
        Assert.AreEqual(GuaranteeLetters.Get(GuaranteeLetterKind.Goblin)!.Name, items.Single(item => item.Key.StartsWith("letter:")).Text);
        Assert.AreEqual("Expansion Cover", items.Single(item => item.Key.StartsWith("cover:")).Text);
        var unused = DepartureChecklist.Build(context with { Letter = GuaranteeLetterKind.None, UsesCover = false });
        Assert.IsFalse(unused.Any(item => item.Key.StartsWith("letter:") || item.Key.StartsWith("cover:")));
    }

    [TestMethod]
    public void SelectedTitlePotionTalentAndPartnerAreNamedButUnselectedBonusesAreOmitted()
    {
        var items = DepartureChecklist.Build(Context() with
        {
            Modifiers = new Dictionary<string, int>
            {
                ["title-event-pass"] = 1, ["partner-william-bonus"] = 2,
                ["speed-walk-40"] = 1, ["talent-mercantile-grandmaster"] = 1,
                ["speed-commerce-reforge"] = 15, ["speed-floating-stone"] = 0
            }
        });
        foreach (var text in new[] { "Event pass title", "Summon William", "Speed Walk Potion 40%", "Grandmaster Merchant", "reforge level 15", "Check Arcana" })
            Assert.IsTrue(items.Any(item => item.Text.Contains(text)), text);
        Assert.IsFalse(items.Any(item => item.Text.Contains("Floating Stone")));
        Assert.AreEqual(1, items.Count(item => item.Key.StartsWith("title:")));
    }

    [TestMethod]
    public void AccessoryNamesSlotsAndRollsMatchThePlanner()
    {
        var items = DepartureChecklist.Build(Context() with
        {
            Enchants = new Dictionary<string, string> { ["1-prefix"] = "haggler", ["1-suffix"] = "road", ["2-prefix"] = "silk" },
            EnchantRolls = new Dictionary<string, int> { ["1-suffix"] = 2 }
        });
        var gear = items.Single(item => item.Text == "Gear");
        Assert.AreEqual("Accessory 1: Haggler + Road (2%)", items.Single(item => item.Key.StartsWith("accessory-1:")).Text);
        Assert.AreEqual("Accessory 2: Silk (1%)", items.Single(item => item.Key.StartsWith("accessory-2:")).Text);
        Assert.IsTrue(items.Where(item => item.Key.StartsWith("accessory-") || item.Key.StartsWith("title:"))
            .All(item => item.ParentKey == gear.Key));
        Assert.IsFalse(items.Any(item => item.Key.StartsWith("chest:")));
    }

    [TestMethod]
    public void GroupDestinationReminderIsOnlyRelevantToGroupRoutes()
    {
        var barter = DepartureChecklist.Build(Context() with { IsBarter = true });
        Assert.IsFalse(barter.Any(item => item.Key.StartsWith("group:")));
        var group = DepartureChecklist.Build(Context() with { IsGroup = true });
        Assert.IsTrue(group.Any(item => item.Key.StartsWith("group:")));
        Assert.IsFalse(group.Any(item => item.Key.StartsWith("materials:")));
    }

    [TestMethod]
    public void GenericListNamesConfiguredGearTitlesAndReforgeWithoutNeedingARoute()
    {
        var context = Context() with
        {
            Enchants = new Dictionary<string, string> { ["1-prefix"] = "haggler", ["2-suffix"] = "road" },
            EnchantRolls = new Dictionary<string, int> { ["2-suffix"] = 2 },
            Modifiers = new Dictionary<string, int> { ["title-event-pass"] = 1, ["speed-commerce-reforge"] = 15 },
            Letter = GuaranteeLetterKind.Ogre
        };
        var items = DepartureChecklist.Build(context, relevant: false);
        foreach (var text in new[] { "Haggler", "Accessory 2: Road (2%)", "Event pass title", "reforge level 15", "Check Arcana" })
            Assert.IsTrue(items.Any(item => item.Text.Contains(text)), text);
        Assert.IsFalse(items.Any(item => item.Text.Contains("Ogre") || item.Text.Contains("Confirm route") || item.Text.Contains("Use Wagon")));
        Assert.IsTrue(items.Any(item => item.Key.StartsWith("cover:")));
        Assert.IsTrue(items.Any(item => item.Key.StartsWith("group:")));
        var gear = items.Single(item => item.Text == "Gear");
        var chest = items.Single(item => item.Key.StartsWith("chest:"));
        Assert.AreEqual("Commerce chest piece (Transport Speed reforge level 15)", chest.Text);
        Assert.AreEqual(gear.Key, chest.ParentKey);
        Assert.IsTrue(items.Where(item => item.ParentKey is not null).All(item => item.ParentKey == gear.Key));
        var changed = DepartureChecklist.Build(context with
        {
            Modifiers = new Dictionary<string, int> { ["title-event-pass"] = 1, ["speed-commerce-reforge"] = 16 }
        }, relevant: false);
        Assert.AreNotEqual(gear.Key, changed.Single(item => item.Text == "Gear").Key);
        Assert.AreEqual(items.Single(item => item.Key.StartsWith("accessory-1:")).Key,
            changed.Single(item => item.Key.StartsWith("accessory-1:")).Key);
    }

    [TestMethod]
    [DataRow(DepartureCompanion.Alpaca, "Alpaca")]
    [DataRow(DepartureCompanion.Partner, "commerce partner")]
    [DataRow(DepartureCompanion.William, "William")]
    public void RoutedSummonReminderNamesTheTransportCompanion(DepartureCompanion companion, string name)
    {
        var items = DepartureChecklist.Build(Context() with { Companion = companion });
        Assert.AreEqual($"Summon {name}.", items.Single(item => item.Key.StartsWith("partner:")).Text);
        Assert.IsFalse(items.Any(item => item.Key.StartsWith("cover:")));
    }

    [TestMethod]
    public void OrdinaryTransportNeedsNoCompanionUnlessWilliamBonusIsSelected()
    {
        Assert.IsFalse(DepartureChecklist.Build(Context()).Any(item => item.Key.StartsWith("partner:")));
        var context = Context() with { Modifiers = new Dictionary<string, int> { ["partner-william-bonus"] = 3 } };
        Assert.AreEqual("Summon William.", DepartureChecklist.Build(context).Single(item => item.Key.StartsWith("partner:")).Text);
        Assert.AreEqual("Summon William.", DepartureChecklist.Build(context with { Companion = DepartureCompanion.William })
            .Single(item => item.Key.StartsWith("partner:")).Text);
        Assert.AreEqual("Summon Alpaca and William.", DepartureChecklist.Build(context with { Companion = DepartureCompanion.Alpaca })
            .Single(item => item.Key.StartsWith("partner:")).Text);
    }

    [TestMethod]
    public void ChangedReminderDetailsHaveNewCheckKeysButUnchangedItemsStayStable()
    {
        var first = DepartureChecklist.Build(Context() with { Letter = GuaranteeLetterKind.Ogre });
        var changed = DepartureChecklist.Build(Context() with { Letter = GuaranteeLetterKind.Imp });
        Assert.AreNotEqual(first.Single(item => item.Key.StartsWith("letter:")).Key, changed.Single(item => item.Key.StartsWith("letter:")).Key);
        Assert.AreEqual(first.Single(item => item.Key.StartsWith("gear:")).Key, changed.Single(item => item.Key.StartsWith("gear:")).Key);
        CollectionAssert.AreEqual(first.ToArray(), DepartureChecklist.Build(Context() with { Letter = GuaranteeLetterKind.Ogre }).ToArray());
    }
}
