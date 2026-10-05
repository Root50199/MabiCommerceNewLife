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

        var lines = BarterShoppingList.Build(needs, id => have.GetValueOrDefault(id));

        Assert.AreEqual(2, lines.Count);
        Assert.AreEqual("Leather", lines[0].Name);
        Assert.AreEqual(12, lines[0].Needed);
        Assert.AreEqual(0, lines[0].StillNeed);
        Assert.AreEqual("Wool", lines[1].Name);
        Assert.AreEqual(62, lines[1].Needed);
        Assert.AreEqual(42, lines[1].StillNeed);
    }

    [TestMethod]
    public void EmptyCartGivesAnEmptyList() =>
        Assert.AreEqual(0, BarterShoppingList.Build([], _ => 0).Count);

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
