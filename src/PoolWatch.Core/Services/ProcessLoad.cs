using PoolWatch.Core.Model;

namespace PoolWatch.Core.Services;

/// <summary>
/// Per-process CPU and page-fault rates from two consecutive process snapshots (addendum 4 A). CPU time and the page
/// fault count are cumulative in the snapshot PoolWatch already takes; the rate is the delta over the elapsed time.
/// Pure: matched by (pid, create time) so a reused PID is not mistaken for the same process.
/// </summary>
public static class ProcessLoad
{
    /// <summary>One process's rate in the window, plus the total CPU fraction over all cores.</summary>
    public sealed record Rates(IReadOnlyList<ProcessRate> Processes, double CpuFraction, long SystemPageFaultsPerSec);

    public sealed record ProcessRate(ProcessKey Key, string Image, double Cores, long PageFaultsPerSec);

    /// <summary>
    /// What each process costs in the window <paramref name="rates"/> covers, by (pid, create time): its rate, (0, 0) for
    /// a process the window does not list (it was idle there, or new in it); null for every process while there is no
    /// window yet (PoolWatch has not measured anything). The lists that say "now" read this, not an average.
    /// </summary>
    public static Func<ProcessKey, (double cores, long faultsPerSec)?> CostNow(Rates? rates)
    {
        if (rates is null) return _ => null;
        var byKey = new Dictionary<ProcessKey, ProcessRate>(rates.Processes.Count);
        foreach (var r in rates.Processes) byKey.TryAdd(r.Key, r);
        return key => byKey.TryGetValue(key, out var r) ? (r.Cores, r.PageFaultsPerSec) : (0, 0);
    }

    /// <param name="cores">Logical processors, for the CPU fraction.</param>
    public static Rates Between(IReadOnlyList<ProcessSample> prev, IReadOnlyList<ProcessSample> cur, TimeSpan elapsed, int cores)
    {
        double secs = elapsed.TotalSeconds;
        if (secs <= 0 || cores <= 0) return new Rates([], 0, 0);
        var before = new Dictionary<ProcessKey, ProcessSample>(prev.Count);
        foreach (var p in prev) before[p.Key] = p;
        double hundredNsPerSec = 10_000_000.0;
        var rates = new List<ProcessRate>(cur.Count);
        double totalBusy100ns = 0;
        long totalFaults = 0;
        foreach (var p in cur)
        {
            // C2 (review): the System Idle Process (PID 0) "runs" on every idle core, so its CPU delta is the idle
            // time. Counting it made the whole-machine CPU fraction read ~100% at all times. Skip it entirely.
            if (p.Pid == 0) continue;
            if (!before.TryGetValue(p.Key, out var b)) continue; // new this window: no baseline, skip (next window counts it)
            long cpu = (p.KernelTime - b.KernelTime) + (p.UserTime - b.UserTime);
            if (cpu < 0) cpu = 0;
            long faults = unchecked((int)(p.PageFaultCount - b.PageFaultCount));
            if (faults < 0) faults = 0;
            totalBusy100ns += cpu;
            totalFaults += faults;
            double procCores = cpu / (hundredNsPerSec * secs);
            long fps = (long)(faults / secs);
            if (procCores >= 0.01 || fps >= 1000)
                rates.Add(new ProcessRate(p.Key, p.ImageName, procCores, fps));
        }
        double cpuFraction = Math.Min(1.0, totalBusy100ns / (hundredNsPerSec * secs * cores));
        return new Rates(rates.OrderByDescending(r => Math.Max(r.Cores, r.PageFaultsPerSec / 100_000.0)).ToList(),
            cpuFraction, (long)(totalFaults / secs));
    }
}
