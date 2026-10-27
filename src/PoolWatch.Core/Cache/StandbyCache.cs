using System.Globalization;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Model;

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

public enum ClearTrigger { Manual, PoolOverLimit, Schedule, Experiment, CommandLine }

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
        ClearTrigger.CommandLine => "command line", _ => "manual",
    };

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
        bool backOff = lastPoolClear is { After: not null, Error: null } lp && nowUtc - lp.TimeUtc < PoolRuleBackOff && lp.Freed < Math.Max(512L << 20, lp.PoolBefore / 10);
        if (s.AutoClearPoolGb > 0 && pool >= (long)(s.AutoClearPoolGb * (1L << 30)) && !backOff) return ClearTrigger.PoolOverLimit;
        if (s.AutoClearEveryHours > 0 && nowUtc - (lastClearUtc ?? sinceUtc) >= TimeSpan.FromHours(s.AutoClearEveryHours)) return ClearTrigger.Schedule;
        return null;
    }

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
