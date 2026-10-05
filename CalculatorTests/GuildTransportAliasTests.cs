using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalculatorTests;

[TestClass]
public sealed class GuildTransportAliasTests
{
    [TestMethod]
    public void AlpacaWagonRequiresWagonButHasItsOwnCapacity()
    {
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "commerce-catalog.json")));
        var transports = catalog.RootElement.GetProperty("transports").EnumerateArray()
            .ToDictionary(transport => transport.GetProperty("id").GetInt32());

        var alpaca = transports[9];
        var wagon = transports[3];
        StringAssert.Contains(alpaca.GetProperty("condition").GetString()!, "pet(/pet_lama/, 3)");
        Assert.IsFalse(alpaca.GetProperty("isSkin").GetBoolean());
        Assert.IsTrue(alpaca.GetProperty("slots").GetInt32() > wagon.GetProperty("slots").GetInt32());
        Assert.IsTrue(alpaca.GetProperty("weight").GetInt32() > wagon.GetProperty("weight").GetInt32());
    }

    [TestMethod]
    public void GuildVariantsMapToEqualCapacityBaseOrPartnerTransport()
    {
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "commerce-catalog.json")));
        var transports = catalog.RootElement.GetProperty("transports").EnumerateArray()
            .ToDictionary(transport => transport.GetProperty("id").GetInt32());
        var expectedBases = new Dictionary<int, int>
        {
            [45] = 17, [46] = 18, [47] = 19,
            [48] = 21, [49] = 22, [50] = 23,
            [51] = 24, [52] = 25, [53] = 26,
            [1003] = 1001
        };

        foreach (var (variantId, baseId) in expectedBases)
        {
            var variant = transports[variantId];
            var counterpart = transports[baseId];
            Assert.IsTrue(variant.GetProperty("isSkin").GetBoolean(), $"Transport {variantId} should be an alias.");
            Assert.AreEqual(baseId, variant.GetProperty("baseTransportId").GetInt32());
            Assert.AreEqual(counterpart.GetProperty("slots").GetInt32(), variant.GetProperty("slots").GetInt32());
            Assert.AreEqual(counterpart.GetProperty("weight").GetInt32(), variant.GetProperty("weight").GetInt32());
        }
    }
}