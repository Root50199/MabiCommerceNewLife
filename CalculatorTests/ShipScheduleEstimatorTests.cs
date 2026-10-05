using MabiCommerceNewLife;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalculatorTests;

[TestClass]
public sealed class ShipScheduleEstimatorTests
{
    private static readonly DateTimeOffset MidnightErinn = new(2026, 10, 2, 20, 0, 0, TimeSpan.Zero);
    // Handcart walk from the Belvast Trade Post to its dock (8,255 world units).
    private static readonly TimeSpan BelvastDockWalk = TimeSpan.FromMinutes((double)(8255m / 1000m * 0.04645m));

    [TestMethod]
    public void FixedPstClockMatchesTheSuppliedErinnClockTable()
    {
        Assert.AreEqual(TimeSpan.Zero, ShipScheduleEstimator.GetErinnTimeOfDay(MidnightErinn));
        Assert.AreEqual(TimeSpan.FromHours(12), ShipScheduleEstimator.GetErinnTimeOfDay(MidnightErinn.AddMinutes(18)));
        Assert.AreEqual(TimeSpan.Zero, ShipScheduleEstimator.GetErinnTimeOfDay(MidnightErinn.AddMinutes(36)));
    }

    [TestMethod]
    public void UladhToBelvastUsesNextCobhDepartureAndSailingDuration()
    {
        var estimator = LoadEstimator();

        var estimate = estimator.Estimate(PostIds.TirChonaill, PostIds.Belvast, MidnightErinn, postName: id => id switch { PostIds.TirChonaill => "Tir Chonaill", PostIds.Belvast => "Belvast Trade Post", _ => $"Post {id}" });

        Assert.IsNotNull(estimate);
        // Includes the measured 8,255 wu walk from the Belvast dock to its Trade Post.
        Assert.AreEqual(10.63m, decimal.Round(estimate.Minutes, 2));
        StringAssert.Contains(estimate.Breakdown, "Cobh → Belvast");
        StringAssert.Contains(estimate.Breakdown, "Tir Chonaill → Cobh");
        StringAssert.Contains(estimate.Breakdown, "Belvast → Belvast Trade Post");
        Assert.IsFalse(estimate.Breakdown.Contains("Post ", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SelectedTransportSpeedChangesLandAccessBeforeDeparture()
    {
        // Start a minute early so the wagon reaches Cobh with more than the 30-second boarding buffer.
        var start = MidnightErinn.AddMinutes(-1);
        var wagon = LoadEstimator().Estimate(PostIds.TirChonaill, PostIds.Belvast, start, transportId: 3);
        var handcart = LoadEstimator().Estimate(PostIds.TirChonaill, PostIds.Belvast, start, transportId: TransportIds.Handcart);

        Assert.IsNotNull(wagon);
        Assert.IsNotNull(handcart);
        Assert.AreEqual(6.95m, decimal.Round(wagon.Minutes, 2));
        Assert.AreEqual(11.63m, decimal.Round(handcart.Minutes, 2));
        StringAssert.Contains(wagon.Breakdown, "Post 1 → Cobh");
    }

    [TestMethod]
    public void BelvastToIriaUsesTwoSailingsViaUladh()
    {
        var estimator = LoadEstimator();

        var estimate = estimator.Estimate(PostIds.Belvast, PostIds.Qilla, MidnightErinn);

        Assert.IsNotNull(estimate);
        // Ceann's sailing is no longer free from Cobh: the measured Cobh-to-Ceann land leg makes the direct Qilla ship win.
        Assert.AreEqual(15.0m, estimate.Minutes, estimate.Breakdown);
        StringAssert.Contains(estimate.Breakdown, "Belvast → Cobh");
        StringAssert.Contains(estimate.Breakdown, "Cobh → Port Qilla");
        Assert.IsFalse(estimate.Breakdown.Contains("Ceann"), estimate.Breakdown);
    }

    [TestMethod]
    public void ArrivingWithinThirtySecondsOfDepartureWaitsForTheNextShip()
    {
        var estimator = LoadEstimator();
        // Belvast's 02:30 Erinn sailing to Cobh leaves 3.75 real minutes after Erinn midnight.
        var departure = MidnightErinn.AddMinutes(3.75) - BelvastDockWalk;

        var caught = estimator.Estimate(PostIds.Belvast, PostIds.Cobh, departure.AddSeconds(-40));
        var missed = estimator.Estimate(PostIds.Belvast, PostIds.Cobh, departure.AddSeconds(-20));

        Assert.IsNotNull(caught);
        Assert.IsNotNull(missed);
        Assert.IsTrue(caught.Minutes < 3.5m, $"Expected to catch the ship, got {caught.Minutes} min.");
        Assert.IsTrue(missed.Minutes - caught.Minutes > 4m, $"Expected the next ship, got {missed.Minutes} min.");
    }

    [TestMethod]
    public void BufferSettingChangesWhenArrivalCountsAsMissed()
    {
        var departure = MidnightErinn.AddMinutes(3.75) - BelvastDockWalk;
        var arrival = departure.AddSeconds(-20);

        var noBuffer = LoadEstimator().Estimate(PostIds.Belvast, PostIds.Cobh, arrival, missedDepartureBufferSeconds: 0);
        var defaultBuffer = LoadEstimator().Estimate(PostIds.Belvast, PostIds.Cobh, arrival);
        var smallBuffer = LoadEstimator().Estimate(PostIds.Belvast, PostIds.Cobh, arrival, missedDepartureBufferSeconds: 10);

        Assert.IsNotNull(noBuffer);
        Assert.IsNotNull(defaultBuffer);
        Assert.IsNotNull(smallBuffer);
        Assert.IsTrue(noBuffer.Minutes < 3.5m);
        Assert.AreEqual(noBuffer.Minutes, smallBuffer.Minutes);
        Assert.IsTrue(defaultBuffer.Minutes > noBuffer.Minutes + 4m);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            LoadEstimator().Estimate(PostIds.Belvast, PostIds.Cobh, arrival, missedDepartureBufferSeconds: -1));
    }

    [TestMethod]
    [DataRow("0", 0)]
    [DataRow("30", 30)]
    [DataRow(" 45 ", 45)]
    [DataRow("300", 300)]
    public void BufferSecondsAcceptsWholeNumbersInRange(string text, int expected)
    {
        Assert.IsTrue(ShipScheduleEstimator.TryParseBufferSeconds(text, out var seconds));
        Assert.AreEqual(expected, seconds);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("  ")]
    [DataRow(null)]
    [DataRow("301")]
    [DataRow("-5")]
    [DataRow("+5")]
    [DataRow("2.5")]
    [DataRow("1,0")]
    [DataRow("30s")]
    [DataRow("abc")]
    [DataRow("1e2")]
    [DataRow("３０")]
    public void BufferSecondsRejectsMalformedEntries(string? text)
    {
        Assert.IsFalse(ShipScheduleEstimator.TryParseBufferSeconds(text, out _));
    }

    [TestMethod]
    public void SameContinentAndUnknownPostsFallBackToExistingRouteEstimates()
    {
        var estimator = LoadEstimator();

        Assert.IsNull(estimator.Estimate(PostIds.TirChonaill, PostIds.Dunbarton, MidnightErinn));
        Assert.IsNull(estimator.Estimate(PostIds.TirChonaill, 999, MidnightErinn));
    }

    private static ShipScheduleEstimator LoadEstimator() =>
        ShipScheduleEstimator.Load(Path.Combine(AppContext.BaseDirectory, "ship-schedules.json"));

    [TestMethod]
    public void PortalLoadBufferAddsSecondsForEachLandPortalButNotTheShip()
    {
        // Wagon from a minute early reaches Cobh in time for the same departure with or without the buffer.
        var start = MidnightErinn.AddMinutes(-1);
        var plain = LoadEstimator().Estimate(PostIds.TirChonaill, PostIds.Belvast, start, transportId: 3);
        var buffered = LoadEstimator().Estimate(PostIds.TirChonaill, PostIds.Belvast, start, transportId: 3, portalLoadBufferSeconds: 5);

        Assert.IsNotNull(plain);
        Assert.IsNotNull(buffered);
        Assert.AreEqual(plain.Minutes, buffered.Minutes, "The land leg ends before the same departure, so the total is set by the sailing.");
        StringAssert.Contains(buffered.Breakdown, "incl. 3 portals × 5 s");
    }
}