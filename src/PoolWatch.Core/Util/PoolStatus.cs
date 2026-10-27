using System.Globalization;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Detection;

namespace PoolWatch.Core.Util;

/// <summary>What the main window's status line says about the kernel pool.</summary>
public enum PoolStatusKind
{
    /// <summary>No sample yet, or too few minutes of samples to judge.</summary>
    Collecting,
    Steady,
    Growing,
    Dropping,
    /// <summary>The total holds, but one tag grows (an open incident, or a tag the detector marks as growing).</summary>
    TagGrowing,
}

/// <summary>
/// The growth that matters most right now: an open incident's tag, else the tag the detector sees growing fastest.
/// </summary>
/// <param name="What">The tag in plain words ("File objects (File)").</param>
/// <param name="Growth">How much it grew, in bytes (only a positive growth is ever shown).</param>
/// <param name="Over">Over how long it grew.</param>
/// <param name="Cause">The top suspect (process, service, driver or session), if any.</param>
/// <param name="Confidence">How strongly the evidence ties <paramref name="Cause"/> to the growth.</param>
public sealed record PoolStatusLead(string What, long Growth, TimeSpan Over, string? Cause, Confidence Confidence);

/// <summary>The status line: its kind (for its colour) and its text.</summary>
public sealed record PoolStatus(PoolStatusKind Kind, string Text);

/// <summary>
/// The one-line, plain-English status at the top of the main window ("Kernel pool is steady (9.6 GB)"), built from what
/// the analysis already knows. Pure, so every wording is unit-tested; it never prints a negative rise.
/// </summary>
public static class PoolStatusLine
{
    /// <summary>The total pool counts as growing from this rise in 10 minutes ...</summary>
    public const long GrowBytes10Min = 100L << 20;
    /// <summary>... or this rise in an hour.</summary>
    public const long GrowBytesHour = 200L << 20;
    /// <summary>It counts as dropping from this fall in 10 minutes ...</summary>
    public const long DropBytes10Min = 100L << 20;
    /// <summary>... or this fall in an hour.</summary>
    public const long DropBytesHour = 200L << 20;
    /// <summary>Fewer minutes of samples than this: too early to say whether the pool grows.</summary>
    public static readonly TimeSpan MinHistory = TimeSpan.FromMinutes(3);

    /// <param name="totalBytes">Committed paged + non-paged pool now (what Task Manager shows); null before the first sample.</param>
    /// <param name="held">How far back the samples go.</param>
    /// <param name="change10Min">The total's change over the last 10 minutes (over <paramref name="held"/> when that is shorter).</param>
    /// <param name="changeHour">The total's change over the last hour; null with less than about an hour of samples.</param>
    /// <param name="lead">The growth that matters most (see <see cref="PoolStatusLead"/>), or null.</param>
    public static PoolStatus Describe(long? totalBytes, TimeSpan held, long change10Min, long? changeHour, PoolStatusLead? lead)
    {
        if (totalBytes is not { } total) return new PoolStatus(PoolStatusKind.Collecting, "Kernel pool: collecting the first sample…");
        if (lead is { Growth: <= 0 }) lead = null; // only a real growth is ever named
        if (held < MinHistory)
        {
            // Too early to judge the total; a growth the detector has already caught is still said.
            if (lead is null)
                return new PoolStatus(PoolStatusKind.Collecting,
                    $"Kernel pool is {Fmt.Bytes(total)}. Not enough data yet to tell whether it is growing (PoolWatch needs a few minutes of samples).");
            return new PoolStatus(PoolStatusKind.TagGrowing,
                $"Kernel pool is {Fmt.Bytes(total)}; {lead.What} grew +{Fmt.Bytes(lead.Growth)} in {Span(lead.Over)}." + CauseSentence(lead, growingMost: false));
        }
        string shortSpan = held >= TimeSpan.FromMinutes(9.5) ? "the last 10 minutes" : $"the last {Minutes(held)}";

        // The last 10 minutes first: a fall just now (a cache clear, an app closed) is the current news even after an hour of growth.
        if (change10Min <= -DropBytes10Min)
            return new PoolStatus(PoolStatusKind.Dropping, $"Kernel pool dropped {Fmt.Bytes(-change10Min)} in {shortSpan}.");
        bool hourGrew = changeHour is { } h && h >= GrowBytesHour;
        if (hourGrew || change10Min >= GrowBytes10Min)
        {
            var (amount, span) = hourGrew ? (changeHour!.Value, "the last hour") : (change10Min, shortSpan);
            return new PoolStatus(PoolStatusKind.Growing, $"Kernel pool is growing: +{Fmt.Bytes(amount)} in {span}." + CauseSentence(lead, growingMost: true));
        }
        if (changeHour is { } fell && fell <= -DropBytesHour)
            return new PoolStatus(PoolStatusKind.Dropping, $"Kernel pool dropped {Fmt.Bytes(-fell)} in the last hour.");
        if (lead is not null)
            return new PoolStatus(PoolStatusKind.TagGrowing,
                $"Kernel pool is steady ({Fmt.Bytes(total)}), but {lead.What} grew +{Fmt.Bytes(lead.Growth)} in {Span(lead.Over)}." + CauseSentence(lead, growingMost: false));
        return new PoolStatus(PoolStatusKind.Steady, $"Kernel pool is steady ({Fmt.Bytes(total)}).");
    }

    /// <summary>" Most likely cause: X (strong evidence)." when the evidence is at least "some"; otherwise " No clear cause yet."</summary>
    private static string CauseSentence(PoolStatusLead? lead, bool growingMost)
    {
        if (lead is { Cause: { Length: > 0 } cause } && lead.Confidence >= Confidence.Correlated)
            return $" Most likely cause: {cause} ({PlainWords.Confidence(lead.Confidence)}).";
        return growingMost && lead is not null ? $" No clear cause yet (growing most: {lead.What})." : " No clear cause yet.";
    }

    private static string Span(TimeSpan t) => Math.Abs(t.TotalMinutes - 60) <= 1 ? "the last hour" : Fmt.Duration(t.Duration());

    private static string Minutes(TimeSpan t)
    {
        int m = Math.Max(1, (int)Math.Round(t.TotalMinutes));
        return m == 1 ? "minute" : $"{m} minutes";
    }
}

/// <summary>Small plain-English formatters shared by the window (pure, unit-tested).</summary>
public static class PlainWords
{
    /// <summary>Confidence in words: "confirmed by a trace", "strong evidence", "some evidence", "weak evidence".</summary>
    public static string Confidence(Confidence c) => c switch
    {
        Correlation.Confidence.ConfirmedByTrace => "confirmed by a trace",
        Correlation.Confidence.StronglyCorrelated => "strong evidence",
        Correlation.Confidence.Correlated => "some evidence",
        _ => "weak evidence",
    };

    /// <summary>When something happened, in local time: "14:02" today, "yesterday 14:02", else "26 Oct 14:02".</summary>
    public static string When(DateTime utc, DateTime nowUtc)
    {
        var t = utc.ToLocalTime();
        var today = nowUtc.ToLocalTime().Date;
        string clock = t.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (t.Date == today) return clock;
        if (t.Date == today.AddDays(-1)) return "yesterday " + clock;
        return t.ToString("d MMM", CultureInfo.InvariantCulture) + " " + clock;
    }

    /// <summary>A rate per hour without a sign ("600 MB/h"); a span under a minute counts as one minute.</summary>
    public static string PerHour(long bytes, TimeSpan over)
    {
        double hours = Math.Max(1.0 / 60, over.Duration().TotalHours);
        return Fmt.Bytes((long)Math.Abs(bytes / hours)) + "/h";
    }
}

/// <summary>
/// Round axis steps for the pool chart: 1, 2, 2.5 or 5 times a power of ten, in MB up to 500 MB and in whole GB above,
/// so the labels read "0, 2.5 GB, 5 GB, 7.5 GB" rather than "0, 2.41 GB, 4.82 GB".
/// </summary>
public static class ChartScale
{
    private const long MiB = 1L << 20, GiB = 1L << 30;
    private static readonly double[] Nice = [1, 2, 2.5, 5];

    /// <summary>The smallest round step with which <paramref name="maxTicks"/> steps reach <paramref name="maxBytes"/>.</summary>
    public static long Step(long maxBytes, int maxTicks = 4)
    {
        if (maxBytes <= 0 || maxTicks < 1) return MiB;
        double raw = maxBytes / (double)maxTicks;
        double unit = raw > 500.0 * MiB ? GiB : MiB;
        double v = raw / unit;
        double mag = Math.Max(1, Math.Pow(10, Math.Floor(Math.Log10(v)))); // never a step below 1 MB or a fraction of a GB
        foreach (var m in Nice)
            if (m * mag >= v - 1e-9) return (long)Math.Round(m * mag * unit);
        return (long)Math.Round(10 * mag * unit);
    }

    /// <summary>The axis top: whole steps that leave a little headroom above <paramref name="maxBytes"/>.</summary>
    public static long Top(long maxBytes, long step) =>
        step <= 0 ? Math.Max(1, maxBytes) : Math.Max(1, (long)Math.Ceiling(Math.Max(0, maxBytes) * 1.04 / step)) * step;

    /// <summary>"0", "250 MB", "2 GB", "2.5 GB" (GB when the step is a gigabyte or more, else MB).</summary>
    public static string Label(long bytes, long step) =>
        bytes == 0 ? "0"
        : step >= GiB ? (bytes / (double)GiB).ToString("0.##", CultureInfo.InvariantCulture) + " GB"
        : (bytes / (double)MiB).ToString("0", CultureInfo.InvariantCulture) + " MB";
}
