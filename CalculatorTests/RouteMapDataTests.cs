using MabiCommerceNewLife;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalculatorTests;

[TestClass]
public sealed class RouteMapDataTests
{
    private static readonly DateTimeOffset MidnightErinn = new(2026, 10, 2, 20, 0, 0, TimeSpan.Zero);

    private static RouteMapData LoadMaps() =>
        RouteMapData.Load(Path.Combine(AppContext.BaseDirectory, "route-maps.json"))
        ?? throw new AssertFailedException("route-maps.json was not copied.");

    [TestMethod]
    public void GameLayoutCoversEveryRouteRegionExceptUnworkableMaps()
    {
        var maps = LoadMaps();
        // Dugald, Rano, Connous and Blago (Tara SE) pan even when fully zoomed out; Osna Sail and Sliab Cuilin have no screenshot yet.
        var uncalibrated = new[] { 16, 70, 301, 402, 3001, 3100 };
        var regions = maps.Legs.Select(leg => leg.Region).Distinct().Order().ToList();

        CollectionAssert.AreEqual(uncalibrated, regions.Where(id => maps.GameLayout(id) is null).ToArray());
        var dunbarton = maps.GameLayout(14)!;
        Assert.AreEqual(320, dunbarton.ViewWidth);
        Assert.AreEqual(320, dunbarton.ViewHeight);
    }
    [TestMethod]
    public void BarterStopInsideOneMapJoinsBothLegsIntoOnePage()
    {
        var maps = LoadMaps();
        var plan = RouteMapPlan.Join([maps.Plan(PostIds.Vales, PostIds.Calida), maps.Plan(PostIds.Calida, PostIds.Pera)], "trip");

        CollectionAssert.AreEqual(new[] { 3200, 3400 }, plan.Pages.Select(page => page.Region.Id).ToArray());
        var zardine = plan.Pages[^1];
        Assert.AreEqual(maps.Plan(PostIds.Calida, PostIds.Pera).Pages[0].EndName, zardine.EndName);
        Assert.AreEqual(maps.Plan(PostIds.Vales, PostIds.Calida).Pages[^1].StartName, zardine.StartName);
        for (var index = 1; index < zardine.Lines.Count; index++)
            Assert.AreEqual(zardine.Lines[index - 1][^1], zardine.Lines[index][0], "Joined legs must stay continuous.");
    }

    [TestMethod]
    public void LandRouteListsRegionPagesInTravelOrderWithJoinedLines()
    {
        var plan = LoadMaps().Plan(PostIds.TirChonaill, PostIds.Dunbarton);

        Assert.AreEqual(0, plan.Notes.Count);
        CollectionAssert.AreEqual(new[] { 1, 16, 14 }, plan.Pages.Select(page => page.Region.Id).ToArray());
        foreach (var page in plan.Pages)
            for (var index = 1; index < page.Lines.Count; index++)
                Assert.AreEqual(page.Lines[index - 1][^1], page.Lines[index][0], "Merged lines in one region must be continuous.");
        Assert.IsTrue(plan.Pages.All(page => page.ArrivalNote is null));
    }

    [TestMethod]
    public void ShipTripSkipsTheSailingAndNotesTheArrival()
    {
        var estimate = ShipScheduleEstimator.Load(Path.Combine(AppContext.BaseDirectory, "ship-schedules.json"))
            .Estimate(PostIds.TirChonaill, PostIds.Belvast, MidnightErinn);
        Assert.IsNotNull(estimate);
        CollectionAssert.AreEqual(new[] { "Cobh", "Belvast" }, estimate.Ports.ToArray());

        var plan = LoadMaps().Plan(PostIds.TirChonaill, PostIds.Belvast, estimate.Ports);

        Assert.AreEqual(0, plan.Notes.Count);
        Assert.AreEqual(23, plan.Pages[^2].Region.Id);
        Assert.AreEqual(4005, plan.Pages[^1].Region.Id);
        Assert.AreEqual("Arrive by ship from Cobh", plan.Pages[^1].ArrivalNote);
    }

    [TestMethod]
    public void IriaShipArrivesAtTheCobhQillaDock()
    {
        var maps = LoadMaps();
        var belvastDock = maps.Plan(PostIds.Cobh, PostIds.Belvast, ["Cobh", "Belvast"]).Pages[0].Lines[^1][^1];
        var qillaDock = maps.Plan(PostIds.Cobh, PostIds.Qilla, ["Cobh", "Port Qilla"]).Pages[0].Lines[^1][^1];
        var arrival = maps.Plan(PostIds.Qilla, PostIds.Cobh, ["Port Qilla", "Cobh"]);

        Assert.AreNotEqual(belvastDock, qillaDock);
        var cobhPage = arrival.Pages.Single(page => page.Region.Id == 23);
        Assert.AreEqual(qillaDock, cobhPage.Lines[0][0]);
        Assert.AreEqual("Arrive by ship from Port Qilla", cobhPage.ArrivalNote);
    }

    [TestMethod]
    public void PortConnousArrivalReachesFiliaAndOasis()
    {
        var maps = LoadMaps();
        foreach (var destination in new[] { PostIds.Filia, PostIds.Oasis })
        {
            var plan = maps.Plan(PostIds.TirChonaill, destination, ["Ceann", "Port Connous"]);

            Assert.AreEqual(0, plan.Notes.Count, string.Join(" ", plan.Notes));
            Assert.AreEqual(100, plan.Pages[^2].Region.Id);
            Assert.AreEqual(3100, plan.Pages[^1].Region.Id);
            Assert.AreEqual("Arrive by ship from Ceann", plan.Pages[^1].ArrivalNote);
        }
    }

    [TestMethod]
    public void PostNamesComeFromTheSuppliedLookup()
    {
        var plan = LoadMaps().Plan(PostIds.TirChonaill, PostIds.Dunbarton, postName: id => $"Name {id}");

        Assert.AreEqual("Name 1", plan.Pages[0].StartName);
        Assert.AreEqual("Name 2", plan.Pages[^1].EndName);
    }

    [TestMethod]
    public void UnknownPostReturnsANoteInsteadOfPages()
    {
        var plan = LoadMaps().Plan(999, PostIds.Dunbarton);

        Assert.AreEqual(0, plan.Pages.Count);
        Assert.AreEqual(1, plan.Notes.Count);
    }
}
