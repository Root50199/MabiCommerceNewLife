using MabiCommerceNewLife;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalculatorTests;

[TestClass]
public sealed class LegacyRouteEstimatesTests
{
    [TestMethod]
    public void HandcartTimeRequiresAKnownTransportFactor()
    {
        const decimal handcart = 5.5m;

        Assert.AreEqual(handcart, LegacyRouteEstimates.EstimatedMinutesFromHandcart(handcart, 2));
        Assert.AreEqual(handcart / 1.90m, LegacyRouteEstimates.EstimatedMinutesFromHandcart(handcart, 3));
        Assert.AreEqual(handcart / 1.90m, LegacyRouteEstimates.EstimatedMinutesFromHandcart(handcart, 7));
        Assert.AreEqual(handcart / 1.90m, LegacyRouteEstimates.EstimatedMinutesFromHandcart(handcart, 9));
        Assert.AreEqual(handcart / 1.90m, LegacyRouteEstimates.EstimatedMinutesFromHandcart(handcart, 14));
        Assert.AreEqual(handcart / 1.90m, LegacyRouteEstimates.EstimatedMinutesFromHandcart(handcart, 15));
        Assert.AreEqual(handcart / 1.37m, LegacyRouteEstimates.EstimatedMinutesFromHandcart(handcart, 13));
        Assert.AreEqual(handcart / 0.91m, LegacyRouteEstimates.EstimatedMinutesFromHandcart(handcart, 1));
        Assert.AreEqual(handcart / 1.86m, LegacyRouteEstimates.EstimatedMinutesFromHandcart(handcart, 17));
        Assert.AreEqual(handcart / 1.86m, LegacyRouteEstimates.EstimatedMinutesFromHandcart(handcart, 21));
        Assert.AreEqual(handcart / 2.15m, LegacyRouteEstimates.EstimatedMinutesFromHandcart(handcart, 18));
        Assert.AreEqual(handcart / 2.40m, LegacyRouteEstimates.EstimatedMinutesFromHandcart(handcart, 19));
        Assert.AreEqual(handcart / 2.40m, LegacyRouteEstimates.EstimatedMinutesFromHandcart(handcart, TransportIds.Airship));
        Assert.AreEqual(handcart / 1.86m, LegacyRouteEstimates.EstimatedMinutesFromHandcart(handcart, 27));
        Assert.IsNull(LegacyRouteEstimates.EstimatedMinutesFromHandcart(handcart, 36));
        Assert.IsNull(LegacyRouteEstimates.EstimatedMinutesFromHandcart(null, 2));
    }
    [TestMethod]
    public void CalibratedHandcartMinutesUseTheSelectedTransportFactor()
    {
        Assert.AreEqual(18.8m / 1.90m, LegacyRouteEstimates.EstimatedMinutesFromHandcart(18.8m, 3));
        Assert.AreEqual(19m / 0.91m, LegacyRouteEstimates.EstimatedMinutesFromHandcart(19m, 1));
        Assert.IsNull(LegacyRouteEstimates.EstimatedMinutesFromHandcart(18.8m, 36));
    }

    [TestMethod]
    public void WikiRegionalSpeedBonusesApplyOnlyInTheirRegions()
    {
        Assert.AreEqual(18.6m / 3.09m, LegacyRouteEstimates.EstimatedMinutesFromHandcart(18.6m, 17, 3200));
        Assert.AreEqual(30.7m / 3.07m, LegacyRouteEstimates.EstimatedMinutesFromHandcart(30.7m, 18, 3100));
        Assert.AreEqual(13.3m / 1.33m, LegacyRouteEstimates.EstimatedMinutesFromHandcart(13.3m, 18, 3200));
        Assert.AreEqual(30.7m / 2.15m, LegacyRouteEstimates.EstimatedMinutesFromHandcart(30.7m, 18));
    }

    [TestMethod]
    public void RegionalDistanceEstimatesAreCalibratedAndReversible()
    {
        var routes = RegionalRouteEstimates.LoadHandcartMinutes(Path.Combine(AppContext.BaseDirectory, "regional-route-distances.json"));
        var qillaToKaru = 287579.95m / 1000m * 0.04645m;

        Assert.AreEqual(100, routes.Count);
        Assert.AreEqual(qillaToKaru, routes[(101, 201)]);
        Assert.AreEqual(qillaToKaru, routes[(201, 101)]);
        Assert.AreEqual(44768m / 1000m * 0.04645m, routes[(8, 9)]);
        Assert.AreEqual(routes[(8, 9)], routes[(9, 8)]);
    }

    [TestMethod]
    public void UladhDistancesComeFromTheClientNavigationMeshAnalysis()
    {
        var routes = RegionalRouteEstimates.LoadHandcartMinutes(Path.Combine(AppContext.BaseDirectory, "regional-route-distances.json"));

        Assert.AreEqual(120267m / 1000m * 0.04645m, routes[(1, 2)]);
        Assert.AreEqual(routes[(1, 2)], routes[(2, 1)]);
        Assert.IsTrue(routes.ContainsKey((6, 7)));
    }

    [TestMethod]
    public void CrossRegionDistancesFollowPortalLinkedLegs()
    {
        var routes = RegionalRouteEstimates.LoadHandcartMinutes(Path.Combine(AppContext.BaseDirectory, "regional-route-distances.json"));
        // Vales -> Filia crosses Physis, all of Courcle, then Connous; no Physis-Connous shortcut exists.
        var physisCourcleConnous = (121562.31m + 343496.66m + 130151.54m) / 1000m * 0.04645m;

        Assert.AreEqual(physisCourcleConnous, routes[(104, 102)], 0.01m);
    }

    [TestMethod]
    public void PortalCountsAreReversibleAndBufferedPerPortal()
    {
        var portals = RegionalRouteEstimates.LoadPortalCounts(Path.Combine(AppContext.BaseDirectory, "regional-route-distances.json"));

        Assert.AreEqual(2, portals[(1, 2)]);
        Assert.AreEqual(2, portals[(2, 1)]);
        // Vales -> Filia crosses Physis->Courcle and Courcle->Connous.
        Assert.AreEqual(2, portals[(104, 102)]);
        Assert.AreEqual(0, portals[(203, 204)]);
        Assert.AreEqual(1m / 6m, RegionalRouteEstimates.PortalLoadMinutes(2, 5));
        Assert.AreEqual(0m, RegionalRouteEstimates.PortalLoadMinutes(2, 0));
    }
}