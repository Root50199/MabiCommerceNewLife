using System.Text.Json;
using MabiCommerceNewLife;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalculatorTests;

[TestClass]
public sealed class BarterRotationCatalogTests
{
    [TestMethod]
    public void EveryRotatingAlternativeIsImportedWithOneDefaultPerPost()
    {
        var offers = LoadBarterOffers();
        var rotating = offers.Where(offer => offer.IsSeasonal).GroupBy(offer => offer.PostId).ToDictionary(group => group.Key);
        CollectionAssert.AreEquivalent(new[] { PostIds.KaruForest, PostIds.Oasis, PostIds.Calida, PostIds.Pera }, rotating.Keys.ToArray());
        foreach (var (postId, group) in rotating)
        {
            Assert.AreEqual(5, group.Count(), $"Post {postId} should expose five alternatives.");
            Assert.AreEqual(1, group.Count(offer => offer.IsDefault), $"Post {postId} should have one default.");
            Assert.AreEqual(1, group.Select(offer => (offer.MinPrice, offer.MaxPrice)).Distinct().Count(),
                $"Post {postId} alternatives should share one value range.");
        }
        Assert.IsTrue(rotating[PostIds.Oasis].Single(offer => offer.IsDefault).Name == "Ancient Mural Fragment");
        Assert.IsTrue(offers.Where(offer => !offer.IsSeasonal).All(offer => offer.IsDefault));
    }

    [TestMethod]
    public void ScannedNameIdentifiesEachRotatingAlternativeAmongAllBarterGoods()
    {
        var offers = LoadBarterOffers();
        var identities = offers.Select(offer => new OcrProductIdentity(offer.Id, offer.PostId, offer.Name)).ToList();
        foreach (var offer in offers.Where(offer => offer.IsSeasonal))
        {
            var match = PriceListOcrParser.FindUniqueProduct($"[{offer.Name}]\nGrade 1", identities);
            Assert.AreEqual(offer.Id, match?.Id, $"{offer.Name} was not uniquely identified.");
        }
    }

    private sealed record Offer(int Id, int PostId, string Name, bool IsSeasonal, bool IsDefault, int MinPrice, int MaxPrice);

    private static List<Offer> LoadBarterOffers()
    {
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "commerce-catalog.json")));
        return catalog.RootElement.GetProperty("barterProducts").EnumerateArray().Select(offer => new Offer(
            offer.GetProperty("id").GetInt32(), offer.GetProperty("postId").GetInt32(), offer.GetProperty("name").GetString()!,
            offer.GetProperty("isSeasonal").GetBoolean(), offer.GetProperty("isDefaultRotation").GetBoolean(),
            offer.GetProperty("minPrice").GetInt32(), offer.GetProperty("maxPrice").GetInt32())).ToList();
    }
}
