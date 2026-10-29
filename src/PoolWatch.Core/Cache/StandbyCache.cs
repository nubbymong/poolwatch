using System.Globalization;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Model;
using PoolWatch.Core.Util;

namespace PoolWatch.Core.Cache;

/// <summary>
/// The memory lists (SystemMemoryListInformation): what Task Manager folds into "available" is free + zeroed + standby.
/// Standby is cached file pages; on a PC with lots of RAM it never shrinks, and the per-file ReFS / filter-manager pool
/// records those pages keep alive (Reff, FMfn, RLin, ...) never go away either (addendum 2).
/// </summary>
public readonly record struct MemoryLists(long FreeBytes, long ZeroBytes, long StandbyBytes, long ModifiedBytes, long LowPriorityStandbyBytes)
{
    public long AvailableBytes => FreeBytes + ZeroBytes + StandbyBytes;
    public bool Known => FreeBytes + ZeroBytes + StandbyBytes > 0;

    /// <summary>"free 2.1 GB, standby (file cache) 46 GB, modified 0.3 GB: Task Manager's 'available' 48 GB is mostly cache".</summary>
    public string Text => Known
        ? $"free {Fmt.Bytes(FreeBytes + ZeroBytes)}, standby (file cache) {Fmt.Bytes(StandbyBytes)}, modified {Fmt.Bytes(ModifiedBytes)}" +
          (StandbyBytes > 2 * (FreeBytes + ZeroBytes) ? $": Task Manager's \"available\" {Fmt.Bytes(AvailableBytes)} is mostly cache" : "")
        : "memory lists unknown";
}

public enum ClearMode
{
    /// <summary>MemoryPurgeLowPriorityStandbyList (5): only the low-priority standby pages (less disruptive).</summary>
    LowPriority,
    /// <summary>MemoryPurgeStandbyList (4): the whole standby list (what RAMMap "Empty Standby List" does).</summary>
    Standby,
}

/// <summary>Why a clear ran. <see cref="Escalation"/>: the full purge right after an automatic light one freed little.</summary>
public enum ClearTrigger { Manual, PoolOverLimit, Schedule, Experiment, CommandLine, Escalation }

/// <summary>One clear: the moment, how, why, and the pool / memory lists just before and 2 minutes after.</summary>
public sealed class ClearRecord
{
    public required DateTime TimeUtc { get; init; }
    public required ClearMode Mode { get; init; }
    public required ClearTrigger Trigger { get; init; }
    public required PoolTotals Before { get; init; }
    public MemoryLists ListsBefore { get; init; }
    public IReadOnlyList<(PoolTag Tag, long Bytes)> TagsBefore { get; init; } = [];
    public PoolTotals? After { get; set; }
    public MemoryLists ListsAfter { get; set; }
    /// <summary>The lists right after the purge returned (the cache refills within the 2 minutes).</summary>
    public MemoryLists ListsImmediate { get; set; }
    public IReadOnlyList<(PoolTag Tag, long Bytes)> TagsAfter { get; set; } = [];
    /// <summary>The helper's result (0 = the purge call succeeded), or a message when it could not run.</summary>
    public string? Error { get; set; }
    public bool Done => After is not null || Error is not null;

    public long PoolBefore => (long)(Before.CommittedPaged + Before.CommittedNonPaged);
    public long PoolAfter => After is { } a ? (long)(a.CommittedPaged + a.CommittedNonPaged) : PoolBefore;
    public long Freed => PoolBefore - PoolAfter;

    /// <summary>"Freed 4.7 GB of pool (paged 6.2 -> 3.6 GB, non-paged 6.0 -> 3.9 GB); standby 46 GB -> 73 MB. Reff 722 -> 72 MB, ...".</summary>
    public string Summary()
    {
        if (Error is not null) return $"Clear cache ({ModeText}) failed: {Error}";
        if (After is not { } a) return $"Clear cache ({ModeText}) at {Fmt.Clock(TimeUtc)}: measuring (2 min)";
        string s = $"Clear cache ({ModeText}, {TriggerText}): freed {Fmt.Bytes(Math.Max(0, Freed))} of pool (paged {Fmt.Bytes((long)Before.CommittedPaged)} -> {Fmt.Bytes((long)a.CommittedPaged)}, " +
                   $"non-paged {Fmt.Bytes((long)Before.CommittedNonPaged)} -> {Fmt.Bytes((long)a.CommittedNonPaged)})";
        if (ListsBefore.Known && ListsImmediate.Known)
            s += $"; standby {Fmt.Bytes(ListsBefore.StandbyBytes)} -> {Fmt.Bytes(ListsImmediate.StandbyBytes)}" + (ListsAfter.Known ? $" ({Fmt.Bytes(ListsAfter.StandbyBytes)} 2 min later)" : "");
        else if (ListsBefore.Known && ListsAfter.Known) s += $"; standby {Fmt.Bytes(ListsBefore.StandbyBytes)} -> {Fmt.Bytes(ListsAfter.StandbyBytes)}";
        var after = TagsAfter.ToDictionary(t => t.Tag, t => t.Bytes);
        var tags = TagsBefore.Select(t => (t.Tag, t.Bytes, After: after.GetValueOrDefault(t.Tag))).OrderByDescending(t => t.Bytes - t.After).Take(8)
            .Where(t => t.Bytes - t.After >= 1L << 20).Select(t => $"{t.Tag.ToDisplay().Trim()} {Fmt.Bytes(t.Bytes)} -> {Fmt.Bytes(t.After)}").ToList();
        if (tags.Count > 0) s += ". By tag: " + string.Join(", ", tags);
        return s + ".";
    }

    public string ModeText => Mode == ClearMode.LowPriority ? "low-priority standby" : "standby list";
    public string TriggerText => Trigger switch
    {
        ClearTrigger.PoolOverLimit => "pool over the limit", ClearTrigger.Schedule => "scheduled", ClearTrigger.Experiment => "experiment",
        ClearTrigger.CommandLine => "command line", ClearTrigger.Escalation => "after a light clear freed little", _ => "manual",
    };

    /// <summary>PoolWatch started it itself (its automatic rule, or an experiment's phase), not a button or the command line.</summary>
    public bool Automatic => Trigger is ClearTrigger.Schedule or ClearTrigger.PoolOverLimit or ClearTrigger.Experiment or ClearTrigger.Escalation;

    /// <summary>What this clear freed from <paramref name="tags"/>, from the tags measured before and after it (0 when they were not).</summary>
    public long FreedFrom(IReadOnlySet<PoolTag> tags)
    {
        long freed = 0;
        foreach (var (tag, before) in TagsBefore)
            if (tags.Contains(tag)) freed += Math.Max(0, before - TagsAfter.FirstOrDefault(t => t.Tag == tag).Bytes);
        return freed;
    }

    public Finding ToFinding() => new()
    {
        TimeUtc = TimeUtc, Kind = FindingKind.CacheClear, Severity = Error is null ? FindingSeverity.Info : FindingSeverity.Warning, Title = Summary(),
        Data = new Dictionary<string, object?>
        {
            ["mode"] = Mode.ToString(), ["trigger"] = Trigger.ToString(), ["freedBytes"] = Freed, ["poolBeforeBytes"] = PoolBefore, ["poolAfterBytes"] = PoolAfter,
            ["standbyBeforeBytes"] = ListsBefore.StandbyBytes, ["standbyAfterBytes"] = ListsAfter.StandbyBytes, ["error"] = Error,
            ["tags"] = TagsBefore.Take(20).Select(t => string.Create(CultureInfo.InvariantCulture,
                $"{t.Tag.ToDisplay().Trim()}: {t.Bytes} -> {TagsAfter.FirstOrDefault(x => x.Tag == t.Tag).Bytes}")).ToList(),
        },
    };
}

/// <summary>
/// When to clear automatically (off by default) and how: pool over <see cref="Settings.AutoClearPoolGb"/> and/or every
/// <see cref="Settings.AutoClearEveryHours"/>, never within <see cref="Settings.AutoClearMinMinutes"/> of the last clear.
/// The mode: the low-priority purge when past clears show it frees the pool about as well (at least
/// <see cref="LowPriorityGoodEnough"/> of what a full standby purge freed), else the full standby purge.
/// </summary>
public static class ClearPolicy
{
    public const double LowPriorityGoodEnough = 0.6;
    public static readonly TimeSpan PoolRuleBackOff = TimeSpan.FromHours(6);

    /// <param name="sinceUtc">The last clear, or PoolWatch's start when there was none (the first scheduled clear waits one period).</param>
    /// <param name="lastPoolClear">The last measured clear the pool rule started: when it freed little (the pool over the
    /// limit is not cache-held), the pool rule waits <see cref="PoolRuleBackOff"/> instead of purging every 30 min for nothing.</param>
    /// <param name="experimentRunning">An A/B experiment is running (C1): no automatic clear, which would decide its result
    /// (the "lag before / after a clear" preset makes its own clear at the phase boundary).</param>
    public static ClearTrigger? Due(Settings s, PoolTotals now, DateTime nowUtc, DateTime? lastClearUtc, DateTime sinceUtc, ClearRecord? lastPoolClear = null,
        bool experimentRunning = false)
    {
        if (!s.AutoClearEnabled || experimentRunning) return null;
        if (lastClearUtc is { } last && nowUtc - last < TimeSpan.FromMinutes(s.AutoClearMinMinutes)) return null;
        long pool = (long)(now.CommittedPaged + now.CommittedNonPaged);
        bool backOff = BackOffEnds(lastPoolClear, nowUtc) is not null;
        if (s.AutoClearPoolGb > 0 && pool >= PoolLimit(s) && !backOff) return ClearTrigger.PoolOverLimit;
        if (s.AutoClearEveryHours > 0 && nowUtc - (lastClearUtc ?? sinceUtc) >= TimeSpan.FromHours(s.AutoClearEveryHours)) return ClearTrigger.Schedule;
        return null;
    }

    /// <summary>
    /// When the next automatic clear will run, from the inputs <see cref="Due"/> takes, so the "Next cache clear" line
    /// can never disagree with it. The automatic rule also waits for the helper's status (<paramref name="canPurge"/>
    /// and <paramref name="ownClearTask"/>, null until it is read; the schedule is described meanwhile) and for a clear
    /// that runs or is being measured (<paramref name="busy"/>).
    /// </summary>
    /// <param name="escalationDue"><see cref="EscalateNow"/>: the full purge runs at the next sample.</param>
    public static NextClear Next(Settings s, PoolTotals now, DateTime nowUtc, DateTime? lastClearUtc, DateTime sinceUtc, ClearRecord? lastPoolClear,
        bool? canPurge, bool? ownClearTask, bool experimentRunning, bool busy, bool escalationDue = false)
    {
        if (busy) return new(NextClearKind.Running);
        if (!s.AutoClearEnabled) return new(NextClearKind.Off);
        if (ownClearTask == true) return new(NextClearKind.OwnTask);
        if (canPurge == false) return new(NextClearKind.NotElevated);
        if (experimentRunning) return new(NextClearKind.Experiment);
        if (escalationDue) return new(NextClearKind.Scheduled, nowUtc);
        var spacingEnds = lastClearUtc is { } last ? last + TimeSpan.FromMinutes(s.AutoClearMinMinutes) : DateTime.MinValue;
        DateTime? scheduled = s.AutoClearEveryHours > 0 ? Later((lastClearUtc ?? sinceUtc) + TimeSpan.FromHours(s.AutoClearEveryHours), spacingEnds) : null;
        if (s.AutoClearPoolGb <= 0) return scheduled is { } at ? new(NextClearKind.Scheduled, at) : new(NextClearKind.Off);
        // The size rule may clear once the spacing and any back-off have passed.
        var sizeFrom = Later(spacingEnds, BackOffEnds(lastPoolClear, nowUtc) ?? DateTime.MinValue);
        if ((long)(now.CommittedPaged + now.CommittedNonPaged) >= PoolLimit(s))
        {
            var bySize = Later(nowUtc, sizeFrom);
            return new(NextClearKind.Scheduled, scheduled is { } first && first < bySize ? first : bySize);
        }
        DateTime? after = sizeFrom > nowUtc ? sizeFrom : null;
        if (scheduled is not { } next) return new(NextClearKind.AtPoolSize, PoolBytes: PoolLimit(s), PoolRuleAfterUtc: after);
        // The size rule can only bring the clear forward when it is allowed before the scheduled time.
        return after >= next ? new(NextClearKind.Scheduled, next) : new(NextClearKind.Scheduled, next, PoolLimit(s), after);
    }

    /// <summary>
    /// What the automatic rule starts at this sample: the full purge right after a light clear that freed little
    /// (<see cref="EscalateNow"/>), else a due clear (<see cref="Due"/>) in the mode the past clears favour
    /// (<see cref="ModeFor"/>; the comparison run is marked <see cref="ClearTrigger.Experiment"/>); null when nothing is due.
    /// The caller has already checked that no clear runs or waits for its measurement and that the helper allows one.
    /// </summary>
    public static (ClearMode Mode, ClearTrigger Trigger)? Automatic(IReadOnlyList<ClearRecord> history, Settings s, PoolTotals now, DateTime nowUtc,
        DateTime sinceUtc, bool experimentRunning)
    {
        if (!s.AutoClearEnabled || experimentRunning) return null;
        if (EscalateNow(history, s, nowUtc)) return (ClearMode.Standby, ClearTrigger.Escalation);
        if (Due(s, now, nowUtc, history.LastOrDefault()?.TimeUtc, sinceUtc, LastPoolClear(history)) is not { } trigger) return null;
        return (ModeFor(history, s), IsExperiment(history, s) ? ClearTrigger.Experiment : trigger);
    }

    /// <summary>The last clear the size rule started that has a result: <see cref="Due"/> and <see cref="Next"/> back off after it.</summary>
    public static ClearRecord? LastPoolClear(IReadOnlyList<ClearRecord> history) =>
        history.LastOrDefault(r => r.Trigger == ClearTrigger.PoolOverLimit && r.Done);

    /// <summary>
    /// The last clear was an automatic light (low-priority) one, measured, that freed little (<see cref="FreedLittle"/>),
    /// its spacing has not passed yet, and no full purge was measured yet or the last one freed real pool: the next
    /// automatic clear is the full purge, now, inside the spacing. Once: the full purge is then the last clear, so it never
    /// runs twice in a row; and where the last full purge freed little too, it does not run at all. The last full purge,
    /// not the average of all of them, so a PC whose light clears used to help gets the full one at the first light clear
    /// that does not. Only in "auto" mode: "standby" and "lowpriority" are the user's own choice. Seen on a PC with a ReFS
    /// Dev Drive: a light clear freed 0 B, the full one an hour later 5.11 GB.
    /// </summary>
    public static bool EscalateNow(IReadOnlyList<ClearRecord> history, Settings s, DateTime nowUtc) =>
        s.AutoClearMode == "auto" && history.Count > 0
        && history[^1] is { Mode: ClearMode.LowPriority, After: not null, Error: null, Automatic: true } light
        && FreedLittle(light) && nowUtc - light.TimeUtc < TimeSpan.FromMinutes(s.AutoClearMinMinutes)
        && (history.LastOrDefault(r => r is { Mode: ClearMode.Standby, After: not null, Error: null }) is not { } full
            || !FreedLittle(full)); // last: the only part that reads back through the history

    /// <summary>A measured clear that freed less than max(512 MB, 10% of the pool before it): the pool it saw was not cache-held.</summary>
    public static bool FreedLittle(ClearRecord r) => r.Freed < Math.Max(512L << 20, r.PoolBefore / 10);

    /// <summary>When the size rule's back-off after a pool-rule clear that freed little ends; null when it is not backed off.</summary>
    private static DateTime? BackOffEnds(ClearRecord? lastPoolClear, DateTime nowUtc) =>
        lastPoolClear is { After: not null, Error: null } lp && nowUtc - lp.TimeUtc < PoolRuleBackOff && FreedLittle(lp) ? lp.TimeUtc + PoolRuleBackOff : null;

    private static long PoolLimit(Settings s) => (long)(s.AutoClearPoolGb * (1L << 30));

    private static DateTime Later(DateTime a, DateTime b) => a > b ? a : b;

    /// <summary>The mode for the next automatic clear, from the clears so far.</summary>
    public static ClearMode ModeFor(IReadOnlyList<ClearRecord> history, Settings s)
    {
        if (s.AutoClearMode == "standby") return ClearMode.Standby;
        if (s.AutoClearMode == "lowpriority") return ClearMode.LowPriority;
        // "auto": try low priority first; prefer it once it is known to free the pool about as well as the full purge.
        var low = history.Where(r => r.Mode == ClearMode.LowPriority && r.After is not null && r.Error is null).Select(r => (double)r.Freed).ToList();
        var full = history.Where(r => r.Mode == ClearMode.Standby && r.After is not null && r.Error is null).Select(r => (double)r.Freed).ToList();
        if (low.Count == 0) return ClearMode.LowPriority;
        if (full.Count == 0) return ClearMode.Standby; // one full purge to compare against (the experiment)
        return low.Average() >= LowPriorityGoodEnough * full.Average() ? ClearMode.LowPriority : ClearMode.Standby;
    }

    /// <summary>The next automatic clear is the comparison run: low priority measured, never a full purge.</summary>
    public static bool IsExperiment(IReadOnlyList<ClearRecord> history, Settings s) =>
        s.AutoClearMode == "auto" && history.Any(r => r.Mode == ClearMode.LowPriority && r.After is not null && r.Error is null)
                                  && !history.Any(r => r.Mode == ClearMode.Standby && r.After is not null && r.Error is null);

    /// <summary>
    /// "Clearable now": the pool above the level the last full clear left behind (what a clear would free again), or null
    /// before any measured clear.
    /// </summary>
    public static long? ClearableNow(IReadOnlyList<ClearRecord> history, PoolTotals now)
    {
        var last = history.LastOrDefault(r => r.After is not null && r.Error is null);
        if (last is null) return null;
        return Math.Max(0, (long)(now.CommittedPaged + now.CommittedNonPaged) - last.PoolAfter);
    }

    /// <summary>The finding for the Overview: does the low-priority purge alone free the pool?</summary>
    public static string? LowPriorityVerdict(IReadOnlyList<ClearRecord> history)
    {
        var low = history.Where(r => r.Mode == ClearMode.LowPriority && r.After is not null && r.Error is null).ToList();
        var full = history.Where(r => r.Mode == ClearMode.Standby && r.After is not null && r.Error is null).ToList();
        if (low.Count == 0 || full.Count == 0) return null;
        double l = low.Average(r => (double)r.Freed), f = full.Average(r => (double)r.Freed);
        return l >= LowPriorityGoodEnough * f
            ? $"The low-priority purge alone frees the pool ({Fmt.Bytes((long)l)} on average vs {Fmt.Bytes((long)f)} for the full standby purge): automatic clears use it."
            : $"The low-priority purge frees little ({Fmt.Bytes((long)l)} on average vs {Fmt.Bytes((long)f)} for the full standby purge): automatic clears use the full purge.";
    }
}

/// <summary>What the next automatic clear waits for (<see cref="ClearPolicy.Next"/>).</summary>
public enum NextClearKind
{
    /// <summary>Automatic clearing is off, or neither of its rules is set.</summary>
    Off,
    /// <summary>A clear task of your own (RAMMap) does the job: the automatic clear pauses while it exists.</summary>
    OwnTask,
    NotElevated,
    /// <summary>An A/B experiment runs: a clear would decide its result.</summary>
    Experiment,
    /// <summary>At <see cref="NextClear.AtUtc"/>; at the next sample when that has passed.</summary>
    Scheduled,
    /// <summary>When the pool reaches <see cref="NextClear.PoolBytes"/>.</summary>
    AtPoolSize,
    /// <summary>A clear runs now or is being measured.</summary>
    Running,
}

/// <param name="AtUtc">Scheduled: when.</param>
/// <param name="PoolBytes">AtPoolSize: the size rule's limit. Scheduled: the limit that may bring the clear forward, when it can.</param>
/// <param name="PoolRuleAfterUtc">The size rule waits until then (the spacing after the last clear, or its back-off).</param>
public sealed record NextClear(NextClearKind Kind, DateTime? AtUtc = null, long? PoolBytes = null, DateTime? PoolRuleAfterUtc = null);

/// <summary>
/// The Simple view's line about clearing, in plain words: what happens next, then what the last clear did ("Next cache
/// clear in 23 min (sooner if the kernel pool reaches 8 GB). Last clear at 09:12 freed 4.7 GB."), and the texts the graph's
/// clear marks and the status line use. Pure; an amount is never negative ("freed nothing").
/// </summary>
public static class ClearScheduleText
{
    /// <summary>A clear that freed less than half a megabyte (or less than 0: the pool grew meanwhile) is said to have freed "nothing".</summary>
    public const long NothingBelow = 512L << 10;

    public static string Line(NextClear next, IReadOnlyList<ClearRecord> history, PoolTotals now, DateTime nowUtc)
    {
        string first = next.Kind switch
        {
            NextClearKind.Off => "Automatic clearing is off.",
            NextClearKind.OwnTask => "Automatic clearing is paused: your own clear task (RAMMap) does this job.",
            NextClearKind.NotElevated => "Automatic clearing needs PoolWatch to run as administrator.",
            NextClearKind.Experiment => "Automatic clearing waits until the experiment ends.",
            NextClearKind.Running => "A cache clear is running now.",
            NextClearKind.AtPoolSize => $"Next cache clear when {PoolName.InText} reaches {Limit(next.PoolBytes ?? 0)} " +
                                        $"(now {PlainWords.Amount(Total(now))}){(next.PoolRuleAfterUtc is { } after ? ", after " + Clock(after, nowUtc) : "")}.",
            _ => $"Next cache clear in {In((next.AtUtc ?? nowUtc) - nowUtc)}" +
                 (next.PoolBytes is { } limit ? $" (sooner if {PoolName.InText} reaches {Limit(limit)}{(next.PoolRuleAfterUtc is { } a ? " after " + Clock(a, nowUtc) : "")})" : "") + ".",
        };
        return LastResult(history, nowUtc, next.Kind == NextClearKind.Running) is { } last ? first + " " + last : first;
    }

    /// <summary>The context the status line adds to a growth, "The next cache clear is in 12 min."; null when none is scheduled.</summary>
    public static string? Context(NextClear next, DateTime nowUtc) =>
        next is { Kind: NextClearKind.Scheduled, AtUtc: { } at } ? $"The next cache clear is in {In(at - nowUtc)}." : null;

    /// <summary>
    /// A clear mark's hover text on the graph, "Cache clear at 09:12 (automatic): freed 4.7 GB"; on a graph that ends on a
    /// later day (<paramref name="nowUtc"/>), "Cache clear yesterday at 23:00 (automatic): freed 1.2 GB".
    /// </summary>
    public static string Mark(DateTime timeUtc, long freedBytes, bool automatic, bool light, bool done, DateTime? nowUtc = null)
    {
        string how = (automatic, light) switch { (true, true) => " (automatic, light)", (true, false) => " (automatic)", (false, true) => " (light)", _ => "" };
        string when = nowUtc is { } now ? When(timeUtc, now) : "at " + HourMinute(timeUtc);
        return $"Cache clear {when}{how}: {(done ? "freed " + Freed(freedBytes) : "being measured")}";
    }

    /// <summary>A clear's own result, said in place of the drop it caused: "The cache clear at 03:56 freed 1.3 GB."</summary>
    public static string Result(DateTime timeUtc, long freedBytes, bool done) =>
        $"The cache clear at {HourMinute(timeUtc)} {(done ? "freed " + Freed(freedBytes) : "is being measured")}.";

    /// <summary>The last clear with a result (a running one is the line's first part); "No cache clear yet." before any.</summary>
    private static string? LastResult(IReadOnlyList<ClearRecord> history, DateTime nowUtc, bool running)
    {
        int i = history.Count - 1;
        while (i >= 0 && !history[i].Done) i--;
        if (i < 0) return running || history.Count > 0 ? null : "No cache clear yet.";
        var last = history[i];
        if (last.Error is { } error) return $"Last clear {When(last.TimeUtc, nowUtc)} did not run: {error.TrimEnd('.')}.";
        // A full clear right after a light one that freed little is said as the pair.
        if (last.Trigger == ClearTrigger.Escalation && i > 0 && history[i - 1] is { Mode: ClearMode.LowPriority, After: not null } light)
            return $"Last clear {When(light.TimeUtc, nowUtc)} (light) freed {(light.Freed < NothingBelow ? "nothing" : "only " + PlainWords.Amount(light.Freed))}, " +
                   $"so PoolWatch ran a full clear at {HourMinute(last.TimeUtc)}: it freed {Freed(last.Freed)}.";
        string mostly = ClearEvidence.MostlyFrom(last) is { } records ? ", mostly " + ClearEvidence.Words(records) : "";
        return $"Last clear {When(last.TimeUtc, nowUtc)} freed {Freed(last.Freed)}{mostly}.";
    }

    /// <summary>"4.7 GB", "709 MB", or "nothing" (also for a pool that grew during the 2 minutes).</summary>
    private static string Freed(long bytes) => bytes < NothingBelow ? "nothing" : PlainWords.Amount(bytes);

    /// <summary>"under a minute", "23 min", "1 h", "1 h 20 min" (minutes rounded up, so never "0 min").</summary>
    private static string In(TimeSpan t)
    {
        if (t < TimeSpan.FromMinutes(1)) return "under a minute";
        int minutes = (int)Math.Ceiling(t.TotalMinutes);
        if (minutes < 60) return $"{minutes} min";
        return minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60} min";
    }

    /// <summary>A size rule's limit as set: "8 GB", "7.5 GB".</summary>
    private static string Limit(long bytes) => (bytes / (double)(1L << 30)).ToString("0.#", CultureInfo.InvariantCulture) + " GB";

    private static long Total(PoolTotals t) => (long)(t.CommittedPaged + t.CommittedNonPaged);

    private static string HourMinute(DateTime utc) => utc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>A coming time: "15:20" today, "03:20 tomorrow".</summary>
    private static string Clock(DateTime utc, DateTime nowUtc) =>
        HourMinute(utc) + (utc.ToLocalTime().Date > nowUtc.ToLocalTime().Date ? " tomorrow" : "");

    /// <summary>A past time: "at 09:12" today, "yesterday at 09:12", else "on 26 Oct at 09:12".</summary>
    private static string When(DateTime utc, DateTime nowUtc)
    {
        var day = utc.ToLocalTime().Date;
        var today = nowUtc.ToLocalTime().Date;
        string at = "at " + HourMinute(utc);
        return day >= today ? at : day == today.AddDays(-1) ? "yesterday " + at : $"on {day.ToString("d MMM", CultureInfo.InvariantCulture)} {at}";
    }
}

/// <summary>Whose records a clear freed most, told apart by pool tag.</summary>
public enum FileRecords { ReFS, NTFS, Cache }

/// <summary>
/// Does clearing the cache free kernel pool on this PC, and whose records? Read from the measured clears, so every
/// clear adds to it (automatic, the setup guide's check, the button). <paramref name="Helps"/> is true once a clear freed
/// real pool (not <see cref="ClearPolicy.FreedLittle"/>), false once a full clear was measured and none did (a light clear
/// that freed little proves nothing), null before that. <paramref name="Cited"/> is the clear that shows it.
/// </summary>
public sealed record ClearEvidence(bool? Helps, ClearRecord? Cited)
{
    private static IReadOnlySet<PoolTag> Tags(params string[] codes) => codes.Select(PoolTag.Parse).ToHashSet();

    /// <summary>refs.sys's per-file and per-folder records, its metadata store, and the range locks kept per file.</summary>
    public static readonly IReadOnlySet<PoolTag> ReFsTags = Tags("Reff", "RefF", "Refx", "RLin", "MSPi", "MSeb", "MSb+", "MSde", "MSpa");
    public static readonly IReadOnlySet<PoolTag> NtfsTags = Tags("Ntff", "NtfF", "NtFs", "Ntfx", "NtfE", "NtFC");
    /// <summary>Shared by every file system: the filter manager's file-name cache and file data, the cached file mappings and views.</summary>
    public static readonly IReadOnlySet<PoolTag> CacheTags = Tags("FMfn", "FMsl", "FMfc", "MmSt", "CcVl");

    public static ClearEvidence From(IReadOnlyList<ClearRecord> history)
    {
        var measured = history.Where(r => r.After is not null && r.Error is null).ToList();
        if (measured.LastOrDefault(r => !ClearPolicy.FreedLittle(r)) is { } helped) return new(true, helped);
        if (measured.LastOrDefault(r => r.Mode == ClearMode.Standby) is { } full) return new(false, full);
        return new(null, null);
    }

    /// <summary>"On this PC, clearing the cache freed 5.1 GB of the kernel pool, mostly ReFS file records."; null before any evidence.</summary>
    public string? Sentence => (Helps, Cited) switch
    {
        (true, { } r) => $"On this PC, clearing the cache freed {PlainWords.Amount(r.Freed)} of {PoolName.InText}" +
                         (MostlyFrom(r) is { } records ? ", mostly " + Words(records) : "") + ".",
        (false, { } r) => "On this PC, clearing the cache freed " + (r.Freed < ClearScheduleText.NothingBelow ? "nothing" : $"only {PlainWords.Amount(r.Freed)} of {PoolName.InText}") +
                          ", so an hourly clear is not needed now. PoolWatch looks again each time the cache is cleared.",
        _ => null,
    };

    /// <summary>The records that make up at least half of what the clear freed, or null (not measured by tag, or no one kind).</summary>
    public static FileRecords? MostlyFrom(ClearRecord r)
    {
        if (r.Freed <= 0) return null;
        var (records, freed) = new[] { (FileRecords.ReFS, r.FreedFrom(ReFsTags)), (FileRecords.NTFS, r.FreedFrom(NtfsTags)), (FileRecords.Cache, r.FreedFrom(CacheTags)) }
            .MaxBy(x => x.Item2);
        return freed * 2 >= r.Freed ? records : null;
    }

    public static string Words(FileRecords records) => records switch
    {
        FileRecords.ReFS => "ReFS file records",
        FileRecords.NTFS => "NTFS file records",
        _ => "cached file records",
    };
}

/// <summary>
/// The kernel pool's name in the next-clear line, the clear evidence, the graph's title and the setup guide's texts, so
/// those say it alike. (The status line and the other windows have their own texts.)
/// </summary>
public static class PoolName
{
    /// <summary>At the start of a title or a sentence.</summary>
    public const string Title = "Kernel pool";
    /// <summary>Inside a sentence.</summary>
    public const string InText = "the kernel pool";
}
