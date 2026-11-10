using MabiCommerceNewLife;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalculatorTests;

[TestClass]
public sealed class BarterRouteOrderTests
{
    [TestMethod]
    public void KaruOasisStartPostChangesWithDestination()
    {
        var departureTime = new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
        decimal? LegMinutes(int from, int to, DateTimeOffset _) => (from, to) switch
        {
            (PostIds.KaruForest, PostIds.Oasis) or (PostIds.Oasis, PostIds.KaruForest) => 10,
            (PostIds.Oasis, PostIds.TirChonaill) => 20,
            (PostIds.KaruForest, PostIds.TirChonaill) => 1,
            (PostIds.Oasis, PostIds.Dunbarton) => 1,
            (PostIds.KaruForest, PostIds.Dunbarton) => 20,
            _ => null
        };

        Assert.AreEqual(PostIds.Oasis, BarterRouteOrder.SelectStartPostId(
            PostIds.KaruForest, PostIds.Oasis, PostIds.TirChonaill, LegMinutes, departureTime));
        Assert.AreEqual(PostIds.KaruForest, BarterRouteOrder.SelectStartPostId(
            PostIds.KaruForest, PostIds.Oasis, PostIds.Dunbarton, LegMinutes, departureTime));
    }

    [TestMethod]
    public void DestinationLegEstimateStartsAfterTheFirstPairLeg()
    {
        var departureTime = new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
        var destinationLegDeparture = DateTimeOffset.MinValue;
        decimal? LegMinutes(int from, int to, DateTimeOffset startedAt)
        {
            if (from == PostIds.KaruForest && to == PostIds.Oasis) return 10;
            if (from == PostIds.Oasis && to == PostIds.KaruForest) return 10;
            if (to == PostIds.TirChonaill)
            {
                destinationLegDeparture = startedAt;
                return from == PostIds.Oasis ? 20 : 5;
            }
            return null;
        }

        Assert.AreEqual(PostIds.Oasis, BarterRouteOrder.SelectStartPostId(
            PostIds.KaruForest, PostIds.Oasis, PostIds.TirChonaill, LegMinutes, departureTime));
        Assert.AreEqual(departureTime.AddMinutes(10), destinationLegDeparture);
    }

    [TestMethod]
    public void CalidaPeraCanBeFixedToStartAtPera()
    {
        var departureTime = DateTimeOffset.UtcNow;
        decimal? LegMinutes(int from, int to, DateTimeOffset _) => from == PostIds.Calida && to == PostIds.Pera ? 1 :
            from == PostIds.Pera && to == PostIds.TirChonaill ? 1 :
            from == PostIds.Pera && to == PostIds.Calida ? 100 :
            from == PostIds.Calida && to == PostIds.TirChonaill ? 100 : null;

        Assert.AreEqual(PostIds.Pera, BarterRouteOrder.SelectStartPostId(
            PostIds.Calida, PostIds.Pera, PostIds.TirChonaill, LegMinutes, departureTime, fixedStartPostId: PostIds.Pera));
    }
}
