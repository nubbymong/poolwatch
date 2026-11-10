using System.Diagnostics;
using System.Text.Json;
using PoolWatch.Analysis;
using PoolWatch.Core.Util;

namespace PoolWatch.Engine;

/// <summary>
/// Where PoolWatch's own time, memory and disk writes go (profile.json): stages, files, heap, the floors the heap returns
/// to, and the sizes of what it holds. Built at the end of a CI run, once the sampler has stopped, and every hour on the
/// sampler thread (<see cref="MonitorEngine.Profile"/>): the trim floors are the sampler's own, and the history and the
/// spawn tracker are read under their locks.
/// </summary>
internal static class CostReport
{
    /// <param name="window">What the main window's Pool causes rankings hold (TagRankings.Held), when there is a window.</param>
    public static object Build(MonitorEngine engine, AnalysisPipeline analysis, (int rankings, int owners, int ownerMembers, int events)? window = null)
    {
        using var p = Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        double elapsed = (DateTime.Now - p.StartTime).TotalSeconds;
        int historyProcs; long historyPoints; int minutes; long minuteEntries; int summedHours; int events;
        int growthMinutes, growthTags; long growthTagEntries, growthBytes;
        (int incidents, int owners, int ownerMembers, int events) held;
        Core.Incidents.FoldersBehind behind;
        lock (analysis.History.SyncRoot)
        {
            behind = analysis.Incidents.BehindNow(analysis.History);
            historyProcs = analysis.History.ProcessTrackCount;
            historyPoints = analysis.History.ProcessPointCount;
            minutes = analysis.History.Minutes.Count;
            // Every process entry the minute history holds: the detailed minutes' and the summed hours' (History.cs).
            minuteEntries = analysis.History.MinuteProcessEntries;
            summedHours = analysis.History.SummedHourCount;
            events = analysis.Incidents.EventCount;
            held = analysis.Incidents.Held();
            growthMinutes = analysis.Growth.MinuteCount;
            growthTags = analysis.Growth.TagCount;
            growthTagEntries = analysis.Growth.TagEntries;
            growthBytes = analysis.Growth.ApproxBytes();
        }
        int stormTraceRows;
        object spawns;
        // The starts the spawn-storm report folders hold for their spawns.csv, not written yet; and what the spawn tracker
        // counted: the starts, the fork copies it did not count as launches, the report folders it holds and those it did
        // not make (past 10 an hour). The agentic-load check (ci/agentic-soak.ps1) reads these.
        lock (analysis.Diagnostics.Spawns)
        {
            var t = analysis.Diagnostics.Spawns;
            stormTraceRows = t.Folders.Sum(f => f.PendingRows.Count);
            spawns = new { starts = t.StartsSeen, forkCopies = t.ForkCopiesSeen, stormFoldersHeld = t.Folders.Count, stormFoldersWithheld = t.FoldersWithheld };
        }
        return new
        {
            elapsedSeconds = Math.Round(elapsed, 1),
            process = new
            {
                cpuSeconds = Math.Round(p.TotalProcessorTime.TotalSeconds, 2),
                cpuPercentOfOneCore = elapsed > 0 ? Math.Round(p.TotalProcessorTime.TotalSeconds / elapsed * 100, 3) : 0,
                workingSetMB = Math.Round(p.WorkingSet64 / 1048576.0, 1),
                privateMB = Math.Round(p.PrivateMemorySize64 / 1048576.0, 1),
                handles = p.HandleCount,
                threads = p.Threads.Count,
            },
            gc = new
            {
                heapMB = Math.Round(gc.HeapSizeBytes / 1048576.0, 1),
                committedMB = Math.Round(gc.TotalCommittedBytes / 1048576.0, 1),
                fragmentedMB = Math.Round(gc.FragmentedBytes / 1048576.0, 1),
                totalAllocatedMB = Math.Round(GC.GetTotalAllocatedBytes() / 1048576.0, 1),
                gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2),
                server = System.Runtime.GCSettings.IsServerGC,
                latency = System.Runtime.GCSettings.LatencyMode.ToString(),
                // The gen0 cap the GC runs with: 8 MB from the runtimeconfig (PoolWatch.csproj). The soak prints it.
                gen0MaxBudgetMB = GC.GetConfigurationVariables().TryGetValue("GCGen0MaxBudget", out var cap)
                    ? Math.Round(Convert.ToDouble(cap, System.Globalization.CultureInfo.InvariantCulture) / 1048576.0, 1)
                    : (double?)null,
            },
            // The heap after each full collection: PoolWatch's own compactions (with private memory right after them)
            // and the GC's, the last day of them.
            memory = new
            {
                compactions = engine.Trim.Compactions,
                workingSetEmptied = engine.Trim.WorkingSetEmptied,
                emptiesNotHeld = engine.Trim.EmptiesNotHeld, // of the empties between the checks: back at 75 MB or more 10 s after
                floors = engine.Trim.Floors.Select(f => new
                {
                    seconds = f.Seconds, heapMB = Math.Round(f.HeapBytes / 1048576.0, 1),
                    privateMB = f.PrivateBytes is { } b ? Math.Round(b / 1048576.0, 1) : (double?)null,
                }),
            },
            held = new
            {
                registryRecords = engine.Registry.Count,
                namedRecords = engine.Registry.NamedCount, // exited processes kept by name only (ProcessRegistry)
                compactRecords = engine.Registry.CompactCount,
                stringPool = StringPool.Count,
                historyProcessTracks = historyProcs,
                historyProcessPoints = historyPoints,
                historyMinutes = minutes,
                historyMinuteEntries = minuteEntries,
                historySummedHours = summedHours,
                incidentEvents = events,
                incidents = held.incidents,
                incidentOwners = held.owners,
                incidentOwnerMembers = held.ownerMembers,
                incidentEventsKept = held.events,
                growthMinutes, growthTags, growthTagEntries, growthKB = growthBytes / 1024,
                ramReadings = analysis.Diagnostics.Cache.RamReadings, ramKB = Core.Growth.RamHistory.ApproxBytes / 1024,
                findings = analysis.Diagnostics.Findings.Count,
                stormTraceRows,
                tagRankingsCached = window?.rankings,
                tagRankingOwners = window?.owners,
                tagRankingOwnerMembers = window?.ownerMembers,
                tagRankingEvents = window?.events,
                etwEvents = engine.EtwEvents,
                samples = engine.SampleCount,
                lastHandleScanMs = engine.LatestHandles?.Elapsed.TotalMilliseconds,
                lastHandleTotal = engine.LatestHandles?.Counts.TotalHandles,
            },
            // The report folders' write budget (DESIGN.md section 3): what it let them write (that could wait, against what it
            // allows them, and what a reader needed at once, always written), how often it held a write back for a later
            // sample, and the open incidents whose report or chart is behind now, the oldest last written how long ago. So the
            // owner can see what was deferred.
            writeBudget = WriteBudgetOf(analysis.Incidents.Budget, behind, elapsed),
            spawns,
            // The handle scans whose rows the handle CSV's own thread did not write (each replaced by a later one while it
            // waited behind a write still going on: HandleCsvWriter).
            handleScansNotWritten = engine.HandleScansNotWritten,
            stages = Profiler.StageReport(elapsed),
            writesByFile = Profiler.WrittenByFile.OrderByDescending(kv => kv.Value)
                .Select(kv => new { file = kv.Key, MB = Math.Round(kv.Value / 1048576.0, 3), MBPerHour = elapsed > 0 ? Math.Round(kv.Value / 1048576.0 / elapsed * 3600, 2) : 0 }),
        };
    }

    /// <summary>
    /// profile.json's <c>writeBudget</c>: the report folders' budget, what it spent (what could wait beside what it allows
    /// them, <see cref="Core.Incidents.WriteBudget.CanWaitAllowance"/>), how many times it held a write back (deferrals, not
    /// distinct writes: <c>heldBack</c>), and the open incidents behind now: their reports (<c>incidentsBehindNow</c>, of
    /// which <c>writesFailedNow</c> because their last write failed) and their charts and CSVs (<c>chartsBehindNow</c>).
    /// </summary>
    internal static object WriteBudgetOf(Core.Incidents.WriteBudget b, Core.Incidents.FoldersBehind behind, double elapsedSeconds) => new
    {
        bytesPerHour = b.BytesPerHour,
        burstBytes = b.Burst,
        spentMB = Math.Round(b.Spent / 1048576.0, 3),
        spentMBPerHour = elapsedSeconds > 0 ? Math.Round(b.Spent / 1048576.0 / elapsedSeconds * 3600, 3) : 0,
        canWaitMB = Math.Round(b.SpentCanWait / 1048576.0, 3),
        canWaitAllowanceMB = Math.Round(b.CanWaitAllowance / 1048576.0, 3),
        mustMB = Math.Round(b.SpentMust / 1048576.0, 3),
        heldBack = b.Held,
        incidentsBehindNow = behind.Reports,
        oldestBehindMinutes = behind.OldestReport is { } o ? Math.Round(o.TotalMinutes, 1) : (double?)null,
        writesFailedNow = behind.Failed,
        chartsBehindNow = behind.Charts,
        oldestChartMinutes = behind.OldestChart is { } c ? Math.Round(c.TotalMinutes, 1) : (double?)null,
    };

    public static string Json(MonitorEngine engine, AnalysisPipeline analysis, (int rankings, int owners, int ownerMembers, int events)? window = null) =>
        JsonSerializer.Serialize(Build(engine, analysis, window), new JsonSerializerOptions { WriteIndented = true });
}
