using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using PoolWatch.Native;

namespace PoolWatch.Engine;

/// <summary>
/// Keeps PoolWatch's own footprint small and flat (DESIGN.md section 3: under 100 MB working set, private memory flat).
/// After start-up (30 s, 90 s and 3 min) a compacting full collection of PoolWatch's own heap returns the freed segments,
/// and EmptyWorkingSet sends the start-up pages it no longer touches to the standby list. Then, every 5 minutes, it compacts
/// only when the heap has grown <see cref="GrowthToCompact"/> past what the last full collection left: emptying the
/// working set of a heap in use only made it fault straight back in (491 MB, then 64 MB, then 477 MB within 15 s on a
/// busy PC), at a cost in CPU each time. When the main window hides (<see cref="Request"/>) both run once more: the
/// window's pages and caches really are unused then. The heap each full collection leaves (<see cref="Floors"/>, in
/// profile.json) shows whether memory stays flat. Only ever PoolWatch's own process.
/// </summary>
internal sealed class MemoryTrim
{
    private static readonly TimeSpan[] Early = [TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(90), TimeSpan.FromMinutes(3)];
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(5);
    /// <summary>Growth of the heap past the last full collection that is worth a compacting one.</summary>
    public const long GrowthToCompact = 32L << 20;
    /// <summary>Floors kept for profile.json: a day of 5-minute checks.</summary>
    private const int MaxFloors = 288;
    private readonly Stopwatch _since = Stopwatch.StartNew();
    private readonly Queue<Floor> _floors = new();
    private int _early;
    private TimeSpan _next = Early[0];
    private int _requested;
    private long _lastFullGc;

    /// <summary>
    /// The heap after a full collection: PoolWatch's own compaction, timed when it ran and with private bytes right after
    /// it, or the GC's own, timed when the next 5-minute check saw it (the last of them since the check before).
    /// </summary>
    internal readonly record struct Floor(double Seconds, long HeapBytes, long? PrivateBytes);

    internal enum TrimAction { None, Compact, CompactAndEmpty }

    /// <summary>Compacting collections run (start-up, window hidden, heap grown).</summary>
    public int Compactions { get; private set; }
    public int WorkingSetEmptied { get; private set; }

    /// <summary>The floors seen, oldest first (sampler thread; read after it stops or on it).</summary>
    public IReadOnlyCollection<Floor> Floors => _floors;

    /// <summary>Any thread: the next sample compacts the heap and empties the working set (the main window has hidden).</summary>
    public void Request() => Interlocked.Exchange(ref _requested, 1);

    /// <summary>
    /// After start-up and when asked, both; otherwise a compaction only when the heap the last collection left
    /// (<paramref name="heapBytes"/>) has grown <see cref="GrowthToCompact"/> past the last full one's.
    /// </summary>
    internal static TrimAction Decide(bool release, long heapBytes, long lastFullHeapBytes) =>
        release ? TrimAction.CompactAndEmpty : heapBytes - lastFullHeapBytes >= GrowthToCompact ? TrimAction.Compact : TrimAction.None;

    /// <summary>Sampler thread, after the sample's handlers.</summary>
    public void MaybeRun()
    {
        bool requested = Interlocked.Exchange(ref _requested, 0) == 1;
        var t = _since.Elapsed;
        bool due = t >= _next;
        if (!due && !requested) return;
        bool startUp = due && _early < Early.Length;
        if (due) _next = ++_early < Early.Length ? Early[_early] : t + Every;

        var full = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        if (full.Index > _lastFullGc) Record(t, full.HeapSizeBytes, null); // a full collection the GC ran on its own
        var action = Decide(startUp || requested, GC.GetGCMemoryInfo(GCKind.Any).HeapSizeBytes, full.HeapSizeBytes);
        if (action == TrimAction.None) return;
        using var stage = Core.Util.Profiler.Stage("tick.trim");
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        Compactions++;
        if (action == TrimAction.CompactAndEmpty)
        {
            EmptyWorkingSet(GetCurrentProcess()); // the pseudo-handle: nothing to close
            WorkingSetEmptied++;
        }
        Record(t, GC.GetGCMemoryInfo(GCKind.FullBlocking).HeapSizeBytes, NativeMethods.GetOwnMemory(out _, out long priv) ? priv : null);
    }

    private void Record(TimeSpan t, long heapBytes, long? privateBytes)
    {
        _lastFullGc = GC.GetGCMemoryInfo(GCKind.FullBlocking).Index;
        _floors.Enqueue(new Floor(Math.Round(t.TotalSeconds), heapBytes, privateBytes));
        while (_floors.Count > MaxFloors) _floors.Dequeue();
    }

    [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr process);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
}
