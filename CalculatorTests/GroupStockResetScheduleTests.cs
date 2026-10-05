using MabiCommerceNewLife;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalculatorTests;

[TestClass]
public sealed class GroupStockResetScheduleTests
{
    private static readonly TimeZoneInfo Pacific = GroupStockResetSchedule.PacificTimeZone;

    [TestMethod]
    public void DuringDaylightTimeResetIsThursday1400Utc()
    {
        // Thursday 2026-07-09 7 AM PDT = 14:00 UTC.
        var reset = new DateTime(2026, 7, 9, 14, 0, 0, DateTimeKind.Utc);
        Assert.AreEqual(reset, GroupStockResetSchedule.GetLatestResetUtc(reset, Pacific));
        Assert.AreEqual(reset, GroupStockResetSchedule.GetLatestResetUtc(reset.AddDays(6), Pacific));
        Assert.AreEqual(reset.AddDays(-7), GroupStockResetSchedule.GetLatestResetUtc(reset.AddMinutes(-1), Pacific));
    }

    [TestMethod]
    public void DuringStandardTimeResetIsThursday1500Utc()
    {
        // Thursday 2026-01-08 7 AM PST = 15:00 UTC.
        var reset = new DateTime(2026, 1, 8, 15, 0, 0, DateTimeKind.Utc);
        Assert.AreEqual(reset, GroupStockResetSchedule.GetLatestResetUtc(reset.AddHours(30), Pacific));
    }

    [TestMethod]
    public void RefreshIsDueOnlyAfterAResetPassesSinceLastRefresh()
    {
        var lastRefresh = new DateTime(2026, 7, 9, 1, 0, 0, DateTimeKind.Utc);
        Assert.IsFalse(GroupStockResetSchedule.IsRefreshDue(lastRefresh, new DateTime(2026, 7, 9, 13, 59, 0, DateTimeKind.Utc), Pacific));
        Assert.IsTrue(GroupStockResetSchedule.IsRefreshDue(lastRefresh, new DateTime(2026, 7, 9, 14, 0, 0, DateTimeKind.Utc), Pacific));
        Assert.IsFalse(GroupStockResetSchedule.IsRefreshDue(new DateTime(2026, 7, 9, 14, 0, 1, DateTimeKind.Utc), new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc), Pacific));
    }

    [TestMethod]
    public void NextResetIsTheFollowingThursdaySevenAmPacificAcrossDaylightSaving()
    {
        // Thursday 2026-10-29 7 AM PDT is 14:00 UTC; the next reset (2026-11-05, after DST ends) is 7 AM PST = 15:00 UTC.
        var reset = new DateTime(2026, 10, 29, 14, 0, 0, DateTimeKind.Utc);
        Assert.AreEqual(reset, GroupStockResetSchedule.GetNextResetUtc(reset.AddMinutes(-1), Pacific));
        Assert.AreEqual(new DateTime(2026, 11, 5, 15, 0, 0, DateTimeKind.Utc), GroupStockResetSchedule.GetNextResetUtc(reset, Pacific));
        Assert.AreEqual(new DateTime(2026, 11, 5, 15, 0, 0, DateTimeKind.Utc), GroupStockResetSchedule.GetNextResetUtc(reset.AddDays(7), Pacific));
    }
}
