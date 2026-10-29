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
        int historyProcs; long historyPoints; int minutes; long minuteEntries; int events;
        int growthMinutes, growthTags; long growthTagEntries, growthBytes;
        (int incidents, int owners, int ownerMembers, int events) held;
        lock (analysis.History.SyncRoot)
        {
            historyProcs = analysis.History.ProcessTrackCount;
            historyPoints = analysis.History.ProcessPointCount;
            minutes = analysis.History.Minutes.Count;
            minuteEntries = analysis.History.Minutes.Sum(m => (long)m.Processes.Length);
            events = analysis.Incidents.EventCount;
            held = analysis.Incidents.Held();
            growthMinutes = analysis.Growth.MinuteCount;
            growthTags = analysis.Growth.TagCount;
            growthTagEntries = analysis.Growth.TagEntries;
            growthBytes = analysis.Growth.ApproxBytes();
        }
        int stormTraceRows;
        lock (analysis.Diagnostics.Spawns) stormTraceRows = analysis.Diagnostics.Spawns.OpenStorms.Sum(s => s.Trace.Count);
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
            },
            // The heap after each full collection: PoolWatch's own compactions (with private memory right after them)
            // and the GC's, the last day of them.
            memory = new
            {
                compactions = engine.Trim.Compactions,
                workingSetEmptied = engine.Trim.WorkingSetEmptied,
                floors = engine.Trim.Floors.Select(f => new
                {
                    seconds = f.Seconds, heapMB = Math.Round(f.HeapBytes / 1048576.0, 1),
                    privateMB = f.PrivateBytes is { } b ? Math.Round(b / 1048576.0, 1) : (double?)null,
                }),
            },
            held = new
            {
                registryRecords = engine.Registry.Count,
                compactRecords = engine.Registry.CompactCount,
                stringPool = StringPool.Count,
                historyProcessTracks = historyProcs,
                historyProcessPoints = historyPoints,
                historyMinutes = minutes,
                historyMinuteEntries = minuteEntries,
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
            stages = Profiler.StageReport(elapsed),
            writesByFile = Profiler.WrittenByFile.OrderByDescending(kv => kv.Value)
                .Select(kv => new { file = kv.Key, MB = Math.Round(kv.Value / 1048576.0, 3), MBPerHour = elapsed > 0 ? Math.Round(kv.Value / 1048576.0 / elapsed * 3600, 2) : 0 }),
        };
    }

    public static string Json(MonitorEngine engine, AnalysisPipeline analysis, (int rankings, int owners, int ownerMembers, int events)? window = null) =>
        JsonSerializer.Serialize(Build(engine, analysis, window), new JsonSerializerOptions { WriteIndented = true });
}
