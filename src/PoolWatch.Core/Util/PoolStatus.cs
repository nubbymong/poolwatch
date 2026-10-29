using System.Globalization;
using System.Text.RegularExpressions;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Tags;

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
/// <param name="What">The tag in plain words (<see cref="PlainWords.Tag"/>: "File system metadata (Reff, refs.sys)").</param>
/// <param name="Growth">How much it grew, in bytes (only a positive growth is named, and its amount only when it rounds to at least 1 MB).</param>
/// <param name="Over">Over how long it grew.</param>
/// <param name="Cause">The top suspect (process, service, driver or session), if any.</param>
/// <param name="Confidence">How strongly the evidence ties <paramref name="Cause"/> to the growth.</param>
public sealed record PoolStatusLead(string What, long Growth, TimeSpan Over, string? Cause, Confidence Confidence);

/// <summary>The status line: its kind (for its colour), its text, and whether it says the PC is saturated.</summary>
public sealed record PoolStatus(PoolStatusKind Kind, string Text)
{
    public bool Saturated { get; init; }
}

/// <summary>
/// The one-line, plain-English status at the top of the main window ("Kernel pool grew 709 MB in the last hour."), built
/// from what the analysis already knows. Pure, so every wording is unit-tested; it never prints a sign (the words say
/// which way the pool went).
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
    /// <summary>A tag's growth is said with its amount from this much: half a megabyte reads "1 MB", less would read "0 MB".</summary>
    private const long LeastShownGrowth = 512L << 10;

    /// <param name="totalBytes">Committed paged + non-paged pool now (what Task Manager shows); null before the first sample.</param>
    /// <param name="held">How far back the samples go.</param>
    /// <param name="change10Min">The total's change over the last 10 minutes (over <paramref name="held"/> when that is shorter).</param>
    /// <param name="changeHour">The total's change over the last hour; null with less than about an hour of samples.</param>
    /// <param name="lead">The growth that matters most (see <see cref="PoolStatusLead"/>), or null.</param>
    /// <param name="saturatedBusiest">
    /// Null while the PC is not saturated; while it is (all cores busy for a minute, the saturation detector), the busiest
    /// process, or "" when none stands out. Said in one short clause at the end.
    /// </param>
    public static PoolStatus Describe(long? totalBytes, TimeSpan held, long change10Min, long? changeHour, PoolStatusLead? lead, string? saturatedBusiest = null)
    {
        var s = Pool(totalBytes, held, change10Min, changeHour, lead);
        if (saturatedBusiest is null) return s;
        string clause = saturatedBusiest.Length > 0 ? $" Your PC is saturated; busiest: {saturatedBusiest}." : " Your PC is saturated: every core is busy.";
        return s with { Text = s.Text + clause, Saturated = true };
    }

    private static PoolStatus Pool(long? totalBytes, TimeSpan held, long change10Min, long? changeHour, PoolStatusLead? lead)
    {
        if (totalBytes is not { } total) return new PoolStatus(PoolStatusKind.Collecting, "Kernel pool: collecting the first sample…");
        if (lead is { Growth: <= 0 }) lead = null; // only a real growth is ever named
        // A tag's growth is said with its amount only when the amount shows (never "grew 0 MB"); while the total grows,
        // only its name is said, so there any growth counts.
        var shown = lead is { Growth: >= LeastShownGrowth } ? lead : null;
        if (held < MinHistory)
        {
            // Too early to judge the total; a growth the detector has already caught is still said.
            if (shown is null)
                return new PoolStatus(PoolStatusKind.Collecting, $"Kernel pool is {PlainWords.Amount(total)}. It takes a few minutes to tell whether it is growing.");
            return new PoolStatus(PoolStatusKind.TagGrowing,
                $"Kernel pool is {PlainWords.Amount(total)}, and {shown.What} grew {PlainWords.Amount(shown.Growth)} in {PlainWords.Span(shown.Over)}." + CauseSentence(shown, growingMost: false));
        }
        string shortSpan = held >= TimeSpan.FromMinutes(9.5) ? "the last 10 minutes" : $"the last {PlainWords.Span(held)}";

        // The last 10 minutes first: a fall just now (a cache clear, an app closed) is the current news even after an hour of growth.
        if (change10Min <= -DropBytes10Min)
            return new PoolStatus(PoolStatusKind.Dropping, $"Kernel pool shrank {PlainWords.Amount(-change10Min)} in {shortSpan}.");
        bool hourGrew = changeHour is { } h && h >= GrowBytesHour;
        if (hourGrew || change10Min >= GrowBytes10Min)
        {
            var (amount, span) = hourGrew ? (changeHour!.Value, "the last hour") : (change10Min, shortSpan);
            return new PoolStatus(PoolStatusKind.Growing, $"Kernel pool grew {PlainWords.Amount(amount)} in {span}." + CauseSentence(lead, growingMost: true));
        }
        if (changeHour is { } fell && fell <= -DropBytesHour)
            return new PoolStatus(PoolStatusKind.Dropping, $"Kernel pool shrank {PlainWords.Amount(-fell)} in the last hour.");
        if (shown is not null)
            return new PoolStatus(PoolStatusKind.TagGrowing,
                $"Kernel pool is steady at {PlainWords.Amount(total)}, but {shown.What} grew {PlainWords.Amount(shown.Growth)} in {PlainWords.Span(shown.Over)}." + CauseSentence(shown, growingMost: false));
        return new PoolStatus(PoolStatusKind.Steady, $"Kernel pool is steady at {PlainWords.Amount(total)}.");
    }

    /// <summary>" Most likely cause: X, with strong evidence." when the evidence is at least "some"; otherwise " No clear cause yet."</summary>
    private static string CauseSentence(PoolStatusLead? lead, bool growingMost)
    {
        if (lead is { Cause: { Length: > 0 } cause } && lead.Confidence >= Confidence.Correlated)
            return $" Most likely cause: {cause}, {PlainWords.ConfidencePhrase(lead.Confidence)}.";
        return growingMost && lead is not null ? $" No clear cause yet; growing most: {lead.What}." : " No clear cause yet.";
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

    /// <summary>The same, to follow a name: "confirmed by a trace", "with strong evidence", "with some evidence".</summary>
    public static string ConfidencePhrase(Confidence c) => c == Correlation.Confidence.ConfirmedByTrace ? Confidence(c) : "with " + Confidence(c);

    /// <summary>A process name with its PID, in one pair of brackets: "Leaker.exe (PID 7716)", "svchost.exe (Schedule, PID 1234)".</summary>
    public static string WithPid(string name, int pid) =>
        name.EndsWith(')') && name.Contains('(') ? $"{name[..^1]}, PID {pid})" : $"{name} (PID {pid})";

    /// <summary>
    /// An amount without a sign (the words around it say which way): whole megabytes below a gigabyte ("709 MB"), then
    /// gigabytes with one decimal ("1.2 GB"). The status line rounds this way so that a move smaller than the shown
    /// precision (under half a megabyte, or under 0.05 GB above a gigabyte) leaves its text as it was.
    /// </summary>
    public static string Amount(long bytes)
    {
        double mb = Math.Abs((double)bytes) / (1L << 20);
        return mb < 1023.5 ? FormattableString.Invariant($"{mb:0} MB") : FormattableString.Invariant($"{mb / 1024:0.0} GB");
    }

    /// <summary>A span in words: "40 seconds", "a minute", "25 minutes", "the last hour", "2 hours 10 minutes".</summary>
    public static string Span(TimeSpan t)
    {
        t = t.Duration();
        if (Math.Abs(t.TotalMinutes - 60) <= 1) return "the last hour";
        if (t < TimeSpan.FromMinutes(1)) return $"{Math.Max(1, (int)t.TotalSeconds)} seconds";
        if (t < TimeSpan.FromHours(1))
        {
            int m = (int)Math.Round(t.TotalMinutes);
            return m == 1 ? "a minute" : $"{m} minutes";
        }
        int h = (int)t.TotalHours, min = t.Minutes;
        return (h == 1 ? "1 hour" : $"{h} hours") + (min > 0 ? $" {min} minutes" : "");
    }

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

    /// <summary>Everyday names for the tags that matter most here (DESIGN.md section 1); the tag code and driver follow.</summary>
    private static readonly Dictionary<PoolTag, string> Friendly = new()
    {
        [PoolTag.Parse("File")] = "Open files",
        [PoolTag.Parse("Reff")] = "File system metadata",
        [PoolTag.Parse("Ntff")] = "File system metadata",
        [PoolTag.Parse("RefF")] = "Folder metadata",
        [PoolTag.Parse("NtfF")] = "Folder metadata",
        [PoolTag.Parse("NtfE")] = "Folder index metadata",
        [PoolTag.Parse("Refx")] = "ReFS working memory",
        [PoolTag.Parse("Ntfx")] = "NTFS working memory",
        [PoolTag.Parse("NtFs")] = "NTFS structures",
        [PoolTag.Parse("NtFC")] = "Open-file context",
        [PoolTag.Parse("MSPi")] = "ReFS metadata store",
        [PoolTag.Parse("MSeb")] = "ReFS metadata store",
        [PoolTag.Parse("MSb+")] = "ReFS metadata store",
        [PoolTag.Parse("MSde")] = "ReFS metadata store",
        [PoolTag.Parse("MSpa")] = "ReFS metadata store",
        [PoolTag.Parse("RLin")] = "File range locks",
        [PoolTag.Parse("FMfn")] = "File-name cache",
        [PoolTag.Parse("FMsl")] = "File filter stream data",
        [PoolTag.Parse("FMfc")] = "File filter file data",
        [PoolTag.Parse("FMwi")] = "File filter work items",
        [PoolTag.Parse("IoNm")] = "File names being opened",
        [PoolTag.Parse("MmSt")] = "Cached file mappings",
        [PoolTag.Parse("MmCa")] = "Mapped file records",
        [PoolTag.Parse("CcVl")] = "File cache tables",
        [PoolTag.Parse("Nbuf")] = "Network buffers",
        [PoolTag.Parse("AfdB")] = "Network socket buffers",
        [PoolTag.Parse("EtwB")] = "Event tracing buffers",
        [PoolTag.Parse("Toke")] = "Security tokens",
        [PoolTag.Parse("SeAt")] = "Security token data",
        [PoolTag.Parse("SeTd")] = "Security token data",
        [PoolTag.Parse("SeDt")] = "Security token data",
        [PoolTag.Parse("SeTl")] = "Logon session locks",
        [PoolTag.Parse("WCsc")] = "Container file isolation",
        [PoolTag.Parse("WCfc")] = "Container file isolation",
        [PoolTag.Parse("WCri")] = "Container file isolation",
        [PoolTag.Parse("WCse")] = "Container file isolation",
        [PoolTag.Parse("WCss")] = "Container file isolation",
        [PoolTag.Parse("Even")] = "Event objects",
        [PoolTag.Parse("Key ")] = "Registry keys",
        [PoolTag.Parse("CM31")] = "Registry data",
        [PoolTag.Parse("Sect")] = "Shared memory sections",
        [PoolTag.Parse("Thre")] = "Threads",
        [PoolTag.Parse("Proc")] = "Processes",
        [PoolTag.Parse("Muta")] = "Mutexes",
        [PoolTag.Parse("ObNm")] = "Object names",
        [PoolTag.Parse("ObHd")] = "Handle table entries",
        [PoolTag.Parse("Obtb")] = "Handle tables",
        [PoolTag.Parse("Vad ")] = "Process memory maps",
    };

    /// <summary>
    /// A tag in plain words, with one pair of brackets: "File system metadata (Reff, refs.sys)", "Open files (File, Windows
    /// kernel)". A tag without a known name uses a short plain description from pooltag.txt when it has one ("NetIO
    /// buffers (Nbxx, netio.sys)"), else "Pool tag Abcd (foo.sys)", or "Pool tag Abcd" when nothing is known.
    /// </summary>
    public static string Tag(PoolTag tag, TagInfo? info)
    {
        string code = tag.ToDisplay();
        string driver = Driver(info?.Owner);
        string? name = Friendly.GetValueOrDefault(tag) ?? Plain(info?.Description);
        if (name is null) return driver.Length > 0 ? $"Pool tag {code} ({driver})" : $"Pool tag {code}";
        return driver.Length > 0 ? $"{name} ({code}, {driver})" : $"{name} ({code})";
    }

    /// <summary>The allocating driver as people know it: "nt" and "nt!mm" are the Windows kernel; unknown owners are left out.</summary>
    public static string Driver(string? owner) =>
        owner is null || owner.Length == 0 || owner.StartsWith('<') ? ""
        : owner == "nt" || owner.StartsWith("nt!", StringComparison.Ordinal) ? "Windows kernel"
        : owner;

    private static readonly Regex Aside = new(@"\s*\([^()]*\)", RegexOptions.CultureInvariant);
    private static readonly Regex CodeWord = new(@"\b[A-Z0-9]+_[A-Z0-9_]+\b", RegexOptions.CultureInvariant);

    /// <summary>A description without its bracketed asides, when that leaves a short phrase in words (not "FCB_DATA"); else null.</summary>
    private static string? Plain(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        string s = Aside.Replace(description, "").Trim().Trim('-', ':', ',', ';').Trim();
        if (s.Length is 0 or > 40 || s.Contains('(') || s.Contains(')') || CodeWord.IsMatch(s)) return null;
        return char.ToUpperInvariant(s[0]) + s[1..];
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

    /// <summary>"0", "2.5 MB", "250 MB", "2 GB", "2.5 GB" (GB when the step is a gigabyte or more, else MB).</summary>
    public static string Label(long bytes, long step) =>
        bytes == 0 ? "0"
        : step >= GiB ? (bytes / (double)GiB).ToString("0.##", CultureInfo.InvariantCulture) + " GB"
        : (bytes / (double)MiB).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
}
