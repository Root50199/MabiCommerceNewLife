using System.Text.Json.Serialization;

namespace MabiCommerceNewLife;

/// <summary>Where a rotation sighting came from. Only machine-read sources are logged at runtime.</summary>
public enum BarterSightingSource
{
    /// <summary>Built-in owner report from before scan logging existed; approximate time.</summary>
    OwnerReport,
    /// <summary>A price-list scan that recognized the offer.</summary>
    Scan
}

/// <summary>A run of sightings of one rotating barter offer at a post, from first to last confirmation.</summary>
public sealed record BarterRotationSighting(int PostId, int OfferId, DateTime FirstSeenUtc, DateTime LastSeenUtc,
    BarterSightingSource Source = BarterSightingSource.Scan)
{
    [JsonIgnore]
    public bool IsApproximate => Source == BarterSightingSource.OwnerReport;
}

/// <summary>
/// Tracks which rotating barter offer each post showed and when, so the rotation can be identified.
/// Order: a fixed cycle once every alternative has rotated in twice in the same order, or not a fixed
/// cycle once any alternative returns before all have been seen. Timing: each change is checked for a
/// weekly reset (Thursday 7 AM Pacific) and a calendar-month start between the last sighting of the old
/// offer and the first sighting of the new one; an offer still showing across either rules that interval out.
/// </summary>
public static class BarterRotationLog
{
    private const BarterSightingSource Owner = BarterSightingSource.OwnerReport;

    // Owner reports made before scan logging existed; these are the only trusted manual entries.
    // The previous rotation was last checked before the month rolled over to October; only Oasis
    // has been confirmed since (2026-10-03). Everything newer must come from a scan.
    public static IReadOnlyList<BarterRotationSighting> KnownSightings { get; } =
    [
        new(PostIds.KaruForest, 21008, Utc(2026, 9, 30, 12), Utc(2026, 9, 30, 12), Owner), // Wooden Piece Puzzle
        new(PostIds.Oasis, 22007, Utc(2026, 9, 30, 12), Utc(2026, 9, 30, 12), Owner), // Intro to Ruin Exploration
        new(PostIds.Calida, 23007, Utc(2026, 9, 30, 12), Utc(2026, 9, 30, 12), Owner), // Large Hammock
        new(PostIds.Pera, 24008, Utc(2026, 9, 30, 12), Utc(2026, 9, 30, 12), Owner), // Fire Crystal Ball
        new(PostIds.Oasis, 22010, Utc(2026, 10, 3, 12), Utc(2026, 10, 3, 12), Owner) // Ancient Mural Fragment
    ];

    private static DateTime Utc(int year, int month, int day, int pacificHour) =>
        TimeZoneInfo.ConvertTimeToUtc(new DateTime(year, month, day, pacificHour, 0, 0, DateTimeKind.Unspecified),
            GroupStockResetSchedule.PacificTimeZone);

    /// <summary>
    /// Records that a scan saw <paramref name="offerId"/> at the post at
    /// <paramref name="seenUtc"/>. Manual selections are not evidence and must not be recorded.
    /// </summary>
    public static void Record(List<BarterRotationSighting> log, int postId, int offerId, DateTime seenUtc, BarterSightingSource source)
    {
        if (source == BarterSightingSource.OwnerReport)
            throw new ArgumentException("Only scan sightings are recorded at runtime.", nameof(source));
        var latest = log.Where(item => item.PostId == postId).MaxBy(item => item.LastSeenUtc);
        if (latest is not null && latest.OfferId == offerId && seenUtc >= latest.LastSeenUtc)
            log[log.IndexOf(latest)] = latest with { LastSeenUtc = seenUtc };
        else
            log.Add(new BarterRotationSighting(postId, offerId, seenUtc, seenUtc, source));
        log.Sort((left, right) => left.FirstSeenUtc.CompareTo(right.FirstSeenUtc));
    }

    /// <summary>Trusted saved sightings merged with the built-in history for one post, oldest first, same-offer runs joined.</summary>
    public static List<BarterRotationSighting> History(IEnumerable<BarterRotationSighting> saved, int postId)
    {
        var merged = new List<BarterRotationSighting>();
        var trusted = saved.Where(item => item.Source == BarterSightingSource.Scan);
        foreach (var item in KnownSightings.Concat(trusted).Where(item => item.PostId == postId)
                     .OrderBy(item => item.FirstSeenUtc).ThenBy(item => item.LastSeenUtc))
        {
            if (merged.Count > 0 && merged[^1].OfferId == item.OfferId)
                merged[^1] = merged[^1] with
                {
                    LastSeenUtc = item.LastSeenUtc > merged[^1].LastSeenUtc ? item.LastSeenUtc : merged[^1].LastSeenUtc
                };
            else
                merged.Add(item);
        }
        return merged;
    }

    /// <summary>True when the latest sighting for the post is after the most recent weekly reset.</summary>
    public static bool IsConfirmedSinceReset(IReadOnlyList<BarterRotationSighting> history, DateTime utcNow) =>
        history.Count > 0 && history[^1].LastSeenUtc >= GroupStockResetSchedule.GetLatestResetUtc(utcNow);

    public static string Analyze(IReadOnlyList<BarterRotationSighting> history, IReadOnlyCollection<int> alternativeIds,
        Func<int, string> nameOf, DateTime utcNow)
    {
        if (history.Count == 0) return "No sightings recorded yet.";
        return AnalyzeOrder(history, alternativeIds, nameOf) + "\n" + AnalyzeTiming(history, utcNow);
    }

    private static string AnalyzeOrder(IReadOnlyList<BarterRotationSighting> history, IReadOnlyCollection<int> alternativeIds,
        Func<int, string> nameOf)
    {
        var seen = new HashSet<int>();
        foreach (var run in history)
        {
            if (seen.Contains(run.OfferId) && seen.Count < alternativeIds.Count)
                return $"Order: {nameOf(run.OfferId)} returned before all {alternativeIds.Count} were seen, so it is not a fixed cycle (random or uneven).";
            seen.Add(run.OfferId);
        }
        if (seen.Count < alternativeIds.Count)
            return $"Order: seen {seen.Count} of {alternativeIds.Count}, no repeat yet.";
        var firstCycle = history.Take(alternativeIds.Count).Select(run => run.OfferId).ToList();
        if (history.Count < alternativeIds.Count * 2)
            return $"Order: all {alternativeIds.Count} seen once; a second cycle in the same order would confirm a fixed rotation.";
        return history.Select((run, index) => run.OfferId == firstCycle[index % firstCycle.Count]).All(match => match)
            ? $"Order: fixed cycle {string.Join(" → ", firstCycle.Select(nameOf))}."
            : "Order: all seen twice but the order changed, so it is not a fixed cycle.";
    }

    private static string AnalyzeTiming(IReadOnlyList<BarterRotationSighting> history, DateTime utcNow)
    {
        var notes = new List<string>();
        bool weeklyRuledOut = false, monthlyRuledOut = false, sawChange = false;
        foreach (var run in history)
        {
            var reset = WeeklyResetsBetween(run.FirstSeenUtc, run.LastSeenUtc).LastOrDefault();
            if (reset != default)
            {
                weeklyRuledOut = true;
                notes.Add($"Same offer before and after the {Pacific(reset):MM-dd} weekly reset: not weekly (every 2+ weeks or monthly still possible).");
            }
            var month = MonthStartsBetween(run.FirstSeenUtc, run.LastSeenUtc).FirstOrDefault();
            if (month != default)
            {
                monthlyRuledOut = true;
                notes.Add($"Same offer before and after {Pacific(month):MMMM} 1: not monthly.");
            }
        }

        for (var index = 1; index < history.Count; index++)
        {
            var (before, after) = (history[index - 1], history[index]);
            sawChange = true;
            var hasReset = WeeklyResetsBetween(before.LastSeenUtc, after.FirstSeenUtc).Any();
            var hasMonth = MonthStartsBetween(before.LastSeenUtc, after.FirstSeenUtc).Any();
            var window = $"{Pacific(before.LastSeenUtc):MM-dd}{(before.IsApproximate ? "~" : string.Empty)} to {Pacific(after.FirstSeenUtc):MM-dd}{(after.IsApproximate ? "~" : string.Empty)}";
            if (!hasReset) weeklyRuledOut = true;
            if (!hasMonth) monthlyRuledOut = true;
            notes.Add((hasReset, hasMonth) switch
            {
                (true, true) => $"Changed between {window}: that gap has both a weekly reset and a month start, so either fits.",
                (true, false) => $"Changed between {window}: weekly reset but no month start in that gap, so not monthly.",
                (false, true) => $"Changed between {window}: month start but no weekly reset in that gap, so not weekly.",
                _ => $"Changed between {window} with no weekly reset or month start in that gap: another schedule."
            });
        }

        var nextReset = GroupStockResetSchedule.GetLatestResetUtc(utcNow).AddDays(7);
        var nextMonth = NextMonthStartUtc(utcNow);
        var verdict = (weeklyRuledOut, monthlyRuledOut) switch
        {
            (false, false) when !sawChange => "Interval: no change seen yet.",
            (false, false) => $"Interval: weekly and monthly both still possible. Check soon after the {Pacific(nextReset):MM-dd} 7 AM reset: a change means weekly; the same offer means not weekly. Then check after {Pacific(nextMonth):MMMM} 1.",
            (true, false) => $"Interval: not weekly. Check soon after {Pacific(nextMonth):MMMM} 1 to test monthly.",
            (false, true) => $"Interval: not monthly. Check soon after the {Pacific(nextReset):MM-dd} 7 AM reset to confirm weekly.",
            _ => "Interval: neither weekly nor monthly fits; keep recording to find the period."
        };
        notes.Add(verdict);
        return string.Join("\n", notes);
    }

    private static DateTime Pacific(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, GroupStockResetSchedule.PacificTimeZone);

    /// <summary>Weekly resets strictly after <paramref name="fromUtc"/> and at or before <paramref name="toUtc"/>.</summary>
    public static IEnumerable<DateTime> WeeklyResetsBetween(DateTime fromUtc, DateTime toUtc)
    {
        for (var reset = GroupStockResetSchedule.GetLatestResetUtc(toUtc); reset > fromUtc; reset = GroupStockResetSchedule.GetLatestResetUtc(reset.AddMinutes(-1)))
            yield return reset;
    }

    /// <summary>Pacific month starts (midnight on the 1st) strictly after <paramref name="fromUtc"/> and at or before <paramref name="toUtc"/>.</summary>
    public static IEnumerable<DateTime> MonthStartsBetween(DateTime fromUtc, DateTime toUtc)
    {
        for (var start = NextMonthStartUtc(fromUtc); start <= toUtc; start = NextMonthStartUtc(start))
            yield return start;
    }

    private static DateTime NextMonthStartUtc(DateTime afterUtc)
    {
        var local = Pacific(afterUtc);
        var first = new DateTime(local.Year, local.Month, 1).AddMonths(1);
        return TimeZoneInfo.ConvertTimeToUtc(first, GroupStockResetSchedule.PacificTimeZone);
    }
}
