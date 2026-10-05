namespace MabiCommerceNewLife;

/// <summary>Computes the weekly Group goods stock reset at Thursday 7 AM Pacific time (DST-aware).</summary>
public static class GroupStockResetSchedule
{
    private static readonly TimeSpan ResetTimeOfDay = TimeSpan.FromHours(7);

    public static TimeZoneInfo PacificTimeZone { get; } = FindPacificTimeZone();

    /// <summary>Returns the most recent weekly reset at or before <paramref name="utcNow"/>, in UTC.</summary>
    public static DateTime GetLatestResetUtc(DateTime utcNow, TimeZoneInfo? pacific = null)
    {
        pacific ??= PacificTimeZone;
        var utc = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, pacific);
        var daysSinceThursday = ((int)local.DayOfWeek - (int)DayOfWeek.Thursday + 7) % 7;
        var candidate = DateTime.SpecifyKind(local.Date.AddDays(-daysSinceThursday) + ResetTimeOfDay, DateTimeKind.Unspecified);
        var candidateUtc = TimeZoneInfo.ConvertTimeToUtc(candidate, pacific);
        return candidateUtc <= utc ? candidateUtc : TimeZoneInfo.ConvertTimeToUtc(candidate.AddDays(-7), pacific);
    }

    /// <summary>Returns the first weekly reset strictly after <paramref name="utcNow"/>, in UTC (wall-clock Thursday 7 AM Pacific, so DST shifts are honored).</summary>
    public static DateTime GetNextResetUtc(DateTime utcNow, TimeZoneInfo? pacific = null)
    {
        pacific ??= PacificTimeZone;
        var latestLocal = TimeZoneInfo.ConvertTimeFromUtc(GetLatestResetUtc(utcNow, pacific), pacific);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(latestLocal.AddDays(7), DateTimeKind.Unspecified), pacific);
    }

    /// <summary>True when a weekly reset has occurred after <paramref name="lastRefreshUtc"/>.</summary>
    public static bool IsRefreshDue(DateTime lastRefreshUtc, DateTime utcNow, TimeZoneInfo? pacific = null) =>
        GetLatestResetUtc(utcNow, pacific) > DateTime.SpecifyKind(lastRefreshUtc, DateTimeKind.Utc);

    private static TimeZoneInfo FindPacificTimeZone()
    {
        foreach (var id in new[] { "Pacific Standard Time", "America/Los_Angeles" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
            }
        }
        return TimeZoneInfo.CreateCustomTimeZone("Fixed PST", TimeSpan.FromHours(-8), "Fixed PST", "Fixed PST");
    }
}
