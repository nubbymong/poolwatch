using System.Diagnostics;
using System.Text.Json;
using PoolWatch.Analysis;
using PoolWatch.Core.Util;

namespace PoolWatch.Engine;

/// <summary>Where PoolWatch's own time, memory and disk writes go (profile.json): stages, files, heap and the sizes of what it holds.</summary>
internal static class CostReport
{

    public static object Build(MonitorEngine engine, AnalysisPipeline analysis)
    {
        using var p = Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        double elapsed = (DateTime.Now - p.StartTime).TotalSeconds;
        int historyProcs; long historyPoints; int minutes; int events;
        lock (analysis.History.SyncRoot)
        {
            historyProcs = analysis.History.ProcessTrackCount;
            historyPoints = analysis.History.ProcessPointCount;
            minutes = analysis.History.Minutes.Count;
            events = analysis.Incidents.EventCount;
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
            },
            held = new
            {
                registryRecords = engine.Registry.Count,
                historyProcessTracks = historyProcs,
                historyProcessPoints = historyPoints,
                historyMinutes = minutes,
                incidentEvents = events,
                findings = analysis.Diagnostics.Findings.Count,
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

    public static string Json(MonitorEngine engine, AnalysisPipeline analysis) =>
        JsonSerializer.Serialize(Build(engine, analysis), new JsonSerializerOptions { WriteIndented = true });
}
