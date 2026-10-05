using System.Text.Json;
using MabiCommerceNewLife;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalculatorTests;

[TestClass]
public sealed class DefaultItemValuesTests
{
    [TestMethod]
    public void DefaultsContainOwnerValuationsAndOnlyTradableCatalogMaterials()
    {
        var values = DefaultItemValues.Load(Path.Combine(AppContext.BaseDirectory, "default-item-values.json"));
        Assert.AreEqual(83, values.BarterMaterialValuesById.Count);
        Assert.AreEqual(7, values.GuaranteeLetterMarketValue.Count);
        Assert.AreEqual(1_245_000m, values.BarterMaterialValuesById[5100404]);
        Assert.AreEqual(100_000, values.GuaranteeLetterMarketValue["Ogre"]);
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "commerce-catalog.json")));
        var materials = catalog.RootElement.GetProperty("barterMaterials").EnumerateArray()
            .ToDictionary(item => item.GetProperty("id").GetInt32());
        foreach (var id in values.BarterMaterialValuesById.Keys)
        {
            Assert.IsTrue(materials.ContainsKey(id));
            Assert.IsFalse(materials[id].TryGetProperty("isUntradable", out var flag) && flag.GetBoolean());
        }
    }

    [TestMethod]
    public void LoadingDefaultsReturnsIndependentDictionaries()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "default-item-values.json");
        var first = DefaultItemValues.Load(path);
        first.BarterMaterialValuesById.Clear();
        first.GuaranteeLetterMarketValue.Clear();
        var second = DefaultItemValues.Load(path);
        Assert.AreEqual(83, second.BarterMaterialValuesById.Count);
        Assert.AreEqual(7, second.GuaranteeLetterMarketValue.Count);
    }
}
