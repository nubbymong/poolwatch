using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace PoolWatch.Engine;

/// <summary>
/// Keeps PoolWatch's own footprint small and flat (item 1 budget: under 100 MB working set, private memory flat): on a
/// schedule (30 s, 90 s and 3 min after start, then every 5 min) a compacting full collection of PoolWatch's own heap
/// (returns freed segments instead of holding them until the GC next decides to) and then EmptyWorkingSet on PoolWatch's
/// own process (start-up and UI pages it no longer touches go to the standby list; anything still used faults back in
/// cheaply). Only ever PoolWatch's own process. Measured cost: tens of ms of CPU per run, about 0.01% of one core.
/// </summary>
internal sealed class MemoryTrim
{
    private static readonly TimeSpan[] Early = [TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(90), TimeSpan.FromMinutes(3)];
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(5);
    private readonly Stopwatch _since = Stopwatch.StartNew();
    private int _early;
    private TimeSpan _next = Early[0];

    public int Runs { get; private set; }

    /// <summary>Sampler thread, after the sample's handlers.</summary>
    public void MaybeRun()
    {
        var t = _since.Elapsed;
        if (t < _next) return;
        _next = ++_early < Early.Length ? Early[_early] : t + Every;
        using var _ = Core.Util.Profiler.Stage("tick.trim");
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        EmptyWorkingSet(GetCurrentProcess()); // the pseudo-handle: nothing to close
        Runs++;
    }

    [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr process);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
}
