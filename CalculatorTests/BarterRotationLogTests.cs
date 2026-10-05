using MabiCommerceNewLife;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalculatorTests;

[TestClass]
public sealed class BarterRotationLogTests
{
    private static readonly int[] Alternatives = [1, 2, 3, 4, 5];
    private const int TestPost = 999;

    // Pacific wall-clock time to UTC.
    private static DateTime At(int month, int day, int hour) =>
        TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, month, day, hour, 0, 0), GroupStockResetSchedule.PacificTimeZone);

    private static string Analyze(params (int Offer, DateTime First, DateTime Last)[] runs) =>
        BarterRotationLog.Analyze(runs.Select(run => new BarterRotationSighting(TestPost, run.Offer, run.First, run.Last)).ToList(),
            Alternatives, id => $"G{id}", At(12, 31, 12));

    private static (int, DateTime, DateTime)[] Weekly(params int[] offers) =>
        offers.Select((offer, index) => (offer, At(10, 8, 12).AddDays(7 * index), At(10, 8, 12).AddDays(7 * index))).ToArray();

    [TestMethod]
    public void RepeatBeforeAllSeenIsNotAFixedCycle() =>
        StringAssert.Contains(Analyze(Weekly(1, 2, 1)), "not a fixed cycle");

    [TestMethod]
    public void TwoMatchingCyclesConfirmAFixedOrder() =>
        StringAssert.Contains(Analyze(Weekly(3, 1, 4, 2, 5, 3, 1, 4, 2, 5)), "fixed cycle G3 → G1 → G4 → G2 → G5");

    [TestMethod]
    public void SecondCycleInADifferentOrderIsNotFixed() =>
        StringAssert.Contains(Analyze(Weekly(1, 2, 3, 4, 5, 2, 1, 3, 4, 5)), "order changed");

    [TestMethod]
    public void ChangeAcrossBothResetAndMonthStartIsInconclusive()
    {
        // 09-30 to 10-03 contains October 1 midnight and the Thursday 10-01 7 AM reset.
        var result = Analyze((1, At(9, 30, 12), At(9, 30, 12)), (2, At(10, 3, 12), At(10, 3, 12)));
        StringAssert.Contains(result, "either fits");
        StringAssert.Contains(result, "weekly and monthly both still possible");
    }

    [TestMethod]
    public void ChangeAtWeeklyResetWithoutMonthStartRulesOutMonthly()
    {
        var result = Analyze((1, At(10, 3, 12), At(10, 8, 6)), (2, At(10, 8, 8), At(10, 8, 8)));
        StringAssert.Contains(result, "so not monthly");
        StringAssert.Contains(result, "Interval: not monthly");
    }

    [TestMethod]
    public void SameOfferAcrossAResetRulesOutWeekly()
    {
        var result = Analyze((1, At(10, 3, 12), At(10, 9, 12)));
        StringAssert.Contains(result, "10-08 weekly reset: not weekly");
        StringAssert.Contains(result, "Interval: not weekly");
    }

    [TestMethod]
    public void MidWeekChangeAtMonthStartRulesOutWeekly()
    {
        // November 1 2026 is a Sunday; no Thursday reset between 10-31 and 11-01.
        var result = Analyze((1, At(10, 30, 12), At(10, 31, 20)), (2, At(11, 1, 9), At(11, 1, 9)));
        StringAssert.Contains(result, "so not weekly");
    }

    [TestMethod]
    public void RecordExtendsRunsAndLogsEveryScannedChange()
    {
        var log = new List<BarterRotationSighting>();
        BarterRotationLog.Record(log, TestPost, 1, At(10, 3, 12), BarterSightingSource.Scan);
        BarterRotationLog.Record(log, TestPost, 1, At(10, 5, 12), BarterSightingSource.Scan);
        Assert.AreEqual(1, log.Count);
        Assert.AreEqual(At(10, 5, 12), log[0].LastSeenUtc);

        BarterRotationLog.Record(log, TestPost, 2, At(10, 9, 12), BarterSightingSource.Scan);
        BarterRotationLog.Record(log, TestPost, 3, At(10, 9, 12).AddMinutes(5), BarterSightingSource.Scan);
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, log.Select(item => item.OfferId).ToArray());
    }

    [TestMethod]
    public void OnlyScanSightingsAreTrusted()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            BarterRotationLog.Record([], TestPost, 1, At(10, 3, 12), BarterSightingSource.OwnerReport));
        var saved = new[] { new BarterRotationSighting(PostIds.Oasis, 22006, At(10, 9, 12), At(10, 9, 12), BarterSightingSource.OwnerReport) };
        CollectionAssert.AreEqual(new[] { 22007, 22010 }, BarterRotationLog.History(saved, PostIds.Oasis).Select(item => item.OfferId).ToArray());
    }

    [TestMethod]
    public void BuiltInHistoryHasThePreviousRotationAndOasisChange()
    {
        var oasis = BarterRotationLog.History([], PostIds.Oasis);
        CollectionAssert.AreEqual(new[] { 22007, 22010 }, oasis.Select(item => item.OfferId).ToArray());
        Assert.AreEqual(21008, BarterRotationLog.History([], PostIds.KaruForest).Single().OfferId);
    }
}
