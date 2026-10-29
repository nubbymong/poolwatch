using System.Globalization;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Util;

namespace PoolWatch.Core.Growth;

/// <summary>One of the windows the growth and clear stats (and the Advanced view's graph) are shown for.</summary>
/// <param name="Name">As the window picker says it: "Last 6 hours".</param>
public sealed record StatsWindow(string Name, TimeSpan Span)
{
    /// <summary>Inside a sentence: "last 6 hours".</summary>
    public string InText => Name.ToLowerInvariant();

    /// <summary>The picker's three, in order; <see cref="Default"/> is the one it opens on.</summary>
    public static readonly IReadOnlyList<StatsWindow> All =
    [
        new("Last hour", TimeSpan.FromHours(1)), new("Last 6 hours", TimeSpan.FromHours(6)), new("Last 24 hours", TimeSpan.FromHours(24)),
    ];

    public static StatsWindow Default => All[1];
    /// <summary>The Simple view's stats row: the last day.</summary>
    public static StatsWindow Day => All[2];
}

/// <summary>
/// The growth and clear stats in words (pure): the Simple view's row ("Without the clears it would grow 1.26 GB/h",
/// "Cleared (18 clears) 21.3 GB", "Net change up 1.4 GB"), the Advanced view's "Growth and clears" panel, the Pool causes
/// columns and a tag's sentence. The rate is the pool's change with PoolWatch's own cache clears added back
/// (<see cref="GrowthLedger"/>) per hour measured, so the rate times the hours measured, less what the clears freed, is
/// the net change. Its caption says what happened: "Without the clears it would grow" (or "fall") when the window holds
/// PoolWatch's own clears, "It grows" (or "It falls") when it holds none. Before 30 minutes are measured the rate says
/// when it will be known, and the caption says no direction ("Growth per hour"), as it does for a rate of nothing.
/// </summary>
public static class GrowthText
{
    public const string Title = "Growth and clears";
    /// <summary>The rate's caption over a window that holds PoolWatch's own clears and, with them added back, grew.</summary>
    public const string PerHourCaption = "Without the clears it would grow";
    /// <summary>... and, with them added back, fell.</summary>
    public const string FallCaption = "Without the clears it would fall";
    /// <summary>The rate's caption over a window with no clear of PoolWatch's: the pool grew, or it fell.</summary>
    public const string GrowsCaption = "It grows", FallsCaption = "It falls";
    /// <summary>The rate's caption while it says no direction: before the rate is known, or when it is nothing.</summary>
    public const string RateNotSaidCaption = "Growth per hour";
    public const string PerHourTip = "The kernel pool's change with PoolWatch's own cache clears added back, for each hour PoolWatch measured.";
    public const string GrewLabel = "Grew", FellLabel = "Fell on its own", NetLabel = "Net change";
    public const string PickerName = "Time window";
    public const string NotKnown = "not known yet";
    /// <summary>The true minus sign, as the stats' amounts are signed.</summary>
    public const string Minus = "−";

    /// <summary>"Cleared (6 clears)", "Cleared (1 clear)".</summary>
    public static string ClearedLabel(int clears) => $"Cleared ({clears} {(clears == 1 ? "clear" : "clears")})";

    /// <summary>
    /// The rate's caption: whether the window holds PoolWatch's own clears, and whether the pool grew or fell, as the rate
    /// beside it says it (<see cref="PerHour(GrowthWindow)"/>); no direction before the rate is known or when it is nothing.
    /// </summary>
    public static string RateCaption(GrowthWindow w)
    {
        if (w.PerHour(w.Total) is not { } perHour || perHour == 0) return RateNotSaidCaption;
        bool falls = perHour < 0;
        return w.Clears > 0 ? falls ? FallCaption : PerHourCaption : falls ? FallsCaption : GrowsCaption;
    }

    /// <summary>
    /// The headline: "1.23 GB/h" (a fall is said by <see cref="RateCaption"/>), "nothing" when it is under a byte an hour,
    /// or "known in 22 min" until <see cref="GrowthWindow.RateAfter"/> is measured.
    /// </summary>
    public static string PerHour(GrowthWindow w) =>
        w.PerHour(w.Total) is not { } perHour ? KnownIn(w) : perHour == 0 ? "nothing" : PlainWords.PerHour(w.Total.Change, w.Measured);

    /// <summary>"known in 22 min": how much more PoolWatch must measure before the rate is said (a sleep adds to it).</summary>
    public static string KnownIn(GrowthWindow w) =>
        $"known in {((int)Math.Ceiling((GrowthWindow.RateAfter - w.Measured).TotalMinutes)).ToString(CultureInfo.InvariantCulture)} min";

    /// <summary>
    /// The Simple view's row label: the window's name ("Last 24 hours"), or "Since 08:05" ("Since yesterday at 23:10")
    /// while PoolWatch has run for less than the window, as the graph's title says it.
    /// </summary>
    public static string WindowLabel(StatsWindow window, GrowthWindow w) =>
        w.StartedInside && w.StartedUtc is { } started ? "Since " + Since(started, w.NowUtc) : window.Name;

    // ---------------------------------------------------------------- the Simple view's row

    /// <summary>"21.3 GB"; "0" when the clears freed nothing; "not known yet" before anything is measured.</summary>
    public static string SimpleCleared(GrowthWindow w) => !w.HasData ? NotKnown : w.Total.Cleared > 0 ? Fmt.Bytes(w.Total.Cleared) : "0";

    /// <summary>"up 1.4 GB", "down 300 MB", "unchanged"; "not known yet" before anything is measured.</summary>
    public static string SimpleNet(GrowthWindow w) =>
        !w.HasData ? NotKnown : w.Total.Net > 0 ? "up " + Fmt.Bytes(w.Total.Net) : w.Total.Net < 0 ? "down " + Fmt.Bytes(-w.Total.Net) : "unchanged";

    // ---------------------------------------------------------------- the Advanced view's panel

    /// <summary>"+7.4 GB"; "0" for nothing.</summary>
    public static string Grew(long bytes) => bytes > 0 ? "+" + Fmt.Bytes(bytes) : "0";

    /// <summary>"−7.1 GB" (what the clears freed, or what fell on its own); "0" for nothing.</summary>
    public static string Less(long bytes) => bytes > 0 ? Minus + Fmt.Bytes(bytes) : "0";

    /// <summary>"+308 MB", "−1.2 GB", "0".</summary>
    public static string Signed(long bytes) => bytes > 0 ? "+" + Fmt.Bytes(bytes) : bytes < 0 ? Minus + Fmt.Bytes(-bytes) : "0";

    /// <summary>
    /// How long the window was measured: "Measured over 6 h."; "Measured over 18 h of the 24: the rest was not sampled, for
    /// example while the PC was asleep." (any gap between samples: a sleep, PoolWatch held up, the clock moved);
    /// "Measured over 3 h 10 min of the 24: PoolWatch started at 12:20."; under 30 minutes, "Measured over 12 min so far:
    /// the rate shows after 30 min." (rounded down there, so it never says 30 before the rate shows).
    /// </summary>
    public static string Measured(GrowthWindow w)
    {
        if (!w.HasData) return "Nothing measured yet: the rate shows after 30 min.";
        if (!w.RateKnown) return $"Measured over {Duration(w.Measured, down: true)} so far: the rate shows after 30 min.";
        if (w.Whole) return $"Measured over {Duration(w.Span)}.";
        string of = w.Span == TimeSpan.FromHours(1) ? "of the hour" : FormattableString.Invariant($"of the {w.Span.TotalHours:0}");
        string why = w.StartedInside && w.StartedUtc is { } started ? $"PoolWatch started {When(started, w.NowUtc)}." : "the rest was not sampled, for example while the PC was asleep.";
        return $"Measured over {Duration(w.Measured)} {of}: {why}";
    }

    /// <summary>
    /// The Pool causes legend's note: "Grew, Cleared, Net change and Per hour of tags now: last 6 hours". Only the rows
    /// about a tag growing or held now (and its open incident) follow the window (the main window's tooltip says which).
    /// </summary>
    public static string WindowNote(StatsWindow window) => "Grew, Cleared, Net change and Per hour of tags now: " + window.InText;

    // ---------------------------------------------------------------- a tag: the Pool causes columns and its sentence

    // Every amount here is rounded as the window's stats and header round it (PlainWords.Amount: whole MB below a GB, then
    // one decimal), so a row, its details and the stats beside the graph say the same "21.3 GB".

    /// <summary>"+4.9 GB (now 912 MB)"; "now 912 MB" when it did not grow.</summary>
    public static string TagGrew(GrowthCounts c, long nowBytes) =>
        c.Grew > 0 ? $"+{PlainWords.Amount(c.Grew)} (now {PlainWords.Amount(nowBytes)})" : $"now {PlainWords.Amount(nowBytes)}";

    /// <summary>"4.8 GB"; "nothing" when PoolWatch's own clears freed none of it.</summary>
    public static string TagCleared(GrowthCounts c) => c.Cleared > 0 ? PlainWords.Amount(c.Cleared) : "nothing";

    /// <summary>"+103 MB", "−50 MB", "0".</summary>
    public static string TagNet(GrowthCounts c) => SignedAmount(c.Net);

    /// <summary>A change rounded as the stats round it (<see cref="PlainWords.Amount"/>): "+103 MB", "−4.8 GB", "0".</summary>
    public static string SignedAmount(long bytes) => bytes > 0 ? "+" + PlainWords.Amount(bytes) : bytes < 0 ? Minus + PlainWords.Amount(-bytes) : "0";

    /// <summary>"836.3 MB/h"; empty when it did not grow or the rate is not known yet.</summary>
    public static string TagPerHour(GrowthWindow w, GrowthCounts c) => w.RateKnown && c.Grew > 0 ? PlainWords.PerHour(c.Grew, w.Measured) : "";

    /// <summary>
    /// "Last 6 hours: it grew 4.9 GB, PoolWatch's own cache clears freed 4.8 GB of it, and it is 103 MB higher. Without the
    /// clears it would grow 836.3 MB/h." The window is "Last 24 hours (18 h measured)" when not measured all through; a
    /// fall on its own is said instead of growth, and the rate only once it is known ("It grows 10 MB/h." when the window
    /// holds no clear of PoolWatch's).
    /// </summary>
    public static string TagSentence(StatsWindow window, GrowthWindow w, GrowthCounts c)
    {
        string lead = w.Whole || !w.HasData ? window.Name : $"{window.Name} ({Duration(w.Measured)} measured)";
        var parts = new List<string> { c.Grew > 0 ? $"it grew {PlainWords.Amount(c.Grew)}" : "it did not grow" };
        if (c.Cleared > 0) parts.Add(w.OneClearUtc is { } one
            ? $"PoolWatch's own cache clear at {Clock(one)} freed {PlainWords.Amount(c.Cleared)} of it"
            : $"PoolWatch's own cache clears freed {PlainWords.Amount(c.Cleared)} of it");
        else if (w.Clears > 0) parts.Add(w.Clears == 1 && w.OneClearUtc is { } t
            ? $"PoolWatch's own cache clear at {Clock(t)} freed none of it" : "PoolWatch's own cache clears freed none of it");
        if (c.Fell > 0) parts.Add($"{PlainWords.Amount(c.Fell)} fell on its own");
        string level = c.Net == 0 ? "it is where it was" : $"it is {PlainWords.Amount(c.Net)} {(c.Net > 0 ? "higher" : "lower")}";
        string said = parts.Count == 1 ? $"{parts[0]} and {level}" : $"{string.Join(", ", parts)}, and {level}";
        string rate = w.RateKnown && c.Grew > 0 ? $" {(w.Clears > 0 ? PerHourCaption : GrowsCaption)} {PlainWords.PerHour(c.Grew, w.Measured)}." : "";
        return $"{lead}: {said}.{rate}";
    }

    /// <summary>
    /// "6 h", "18 h", "3 h 10 min", "40 min", "less than a minute": to the nearest minute (the sample interval lost at each
    /// sleep never reads as "17 h 59 min"), or rounded <paramref name="down"/>.
    /// </summary>
    public static string Duration(TimeSpan t, bool down = false)
    {
        int minutes = (int)(down ? Math.Floor(t.TotalMinutes) : Math.Round(t.TotalMinutes, MidpointRounding.AwayFromZero));
        if (minutes < 1) return "less than a minute";
        if (minutes < 60) return $"{minutes} min";
        return minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60} min";
    }

    private static string Clock(DateTime utc) => utc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>A past time after "Since" (the stats row, the graph's title): "08:05" today, "yesterday at 23:10", else "26 Oct at 09:12".</summary>
    public static string Since(DateTime utc, DateTime nowUtc)
    {
        string when = When(utc, nowUtc);
        return when.StartsWith("at ", StringComparison.Ordinal) || when.StartsWith("on ", StringComparison.Ordinal) ? when[3..] : when;
    }

    /// <summary>A past time: "at 12:20" today, "yesterday at 23:10", else "on 26 Oct at 09:12".</summary>
    private static string When(DateTime utc, DateTime nowUtc)
    {
        var day = utc.ToLocalTime().Date;
        var today = nowUtc.ToLocalTime().Date;
        string at = "at " + Clock(utc);
        return day >= today ? at : day == today.AddDays(-1) ? "yesterday " + at : $"on {day.ToString("d MMM", CultureInfo.InvariantCulture)} {at}";
    }
}
