using MabiCommerceNewLife;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalculatorTests;

[TestClass]
public sealed class BarterShoppingListTests
{
    [TestMethod]
    public void TotalsSharedMaterialsAcrossTheCartAndSubtractsWhatIsOnHand()
    {
        var needs = new[]
        {
            new BarterRecipeNeed(10, 1, "Wool", 5),
            new BarterRecipeNeed(4, 2, "Leather", 3),
            new BarterRecipeNeed(6, 1, "Wool", 2),
        };
        var have = new Dictionary<int, int> { [1] = 20, [2] = 50 };

        var lines = BarterShoppingList.Build(needs, id => have.GetValueOrDefault(id),
            id => id == 1 ? 12.5m : null);

        Assert.AreEqual(2, lines.Count);
        Assert.AreEqual("Leather", lines[0].Name);
        Assert.AreEqual(12, lines[0].Needed);
        Assert.AreEqual(0, lines[0].StillNeed);
        Assert.AreEqual("Wool", lines[1].Name);
        Assert.AreEqual(62, lines[1].Needed);
        Assert.AreEqual(42, lines[1].StillNeed);
        Assert.AreEqual(12.5m, lines[1].UnitCost);

        var untradable = BarterShoppingList.Build(needs, id => have.GetValueOrDefault(id),
            isUnitCostEditable: id => id != 1);
        Assert.IsFalse(untradable[1].IsUnitCostEditable);
    }

    [TestMethod]
    public void EmptyCartGivesAnEmptyList() =>
        Assert.AreEqual(0, BarterShoppingList.Build([], _ => 0).Count);

    [TestMethod]
    public void SpreadsheetFormatIsTabDelimitedAndUsesUnformattedCounts()
    {
        var lines = new[]
        {
            new BarterShoppingLine(1, "Wool\tBundle\r\nItem", 1_234_567, 25, 10_000.5m)
        };

        var formatted = BarterShoppingList.FormatForSpreadsheet(lines);

        Assert.AreEqual(
            string.Join(Environment.NewLine,
                "Material\tNeeded\tGold each\tHave\tStill need",
                "Wool Bundle Item\t1234567\t10000.5\t25\t1234542"),
            formatted);
    }

    [DataTestMethod]
    [DataRow("", true, null)]
    [DataRow("  ", true, null)]
    [DataRow("0", true, null)]
    [DataRow("1,250.5", true, 1250.5)]
    [DataRow("-1", false, null)]
    [DataRow("1e3", false, null)]
    [DataRow("abc", false, null)]
    public void UnitCostAcceptsPositivePricesOrBlank(string text, bool expectedValid, double? expectedCost)
    {
        Assert.AreEqual(expectedValid, BarterShoppingList.TryParseUnitCost(text, out var cost));
        Assert.AreEqual(expectedCost is null ? null : (decimal?)Convert.ToDecimal(expectedCost), cost);
    }

    [DataTestMethod]
    [DataRow("", 0)]
    [DataRow("  ", 0)]
    [DataRow("0", 0)]
    [DataRow(" 42 ", 42)]
    [DataRow("1,250", 1250)]
    [DataRow("9999999", 9_999_999)]
    public void HaveAcceptsBlankOrWholeNumbers(string text, int expected)
    {
        Assert.IsTrue(BarterShoppingList.TryParseHave(text, out var have));
        Assert.AreEqual(expected, have);
    }

    [DataTestMethod]
    [DataRow("-1")]
    [DataRow("+1")]
    [DataRow("2.5")]
    [DataRow("abc")]
    [DataRow("10x")]
    [DataRow("1e3")]
    [DataRow(",")]
    [DataRow("10000000")]
    [DataRow("３")]
    public void HaveRejectsMalformedEntries(string text) =>
        Assert.IsFalse(BarterShoppingList.TryParseHave(text, out _));
}
