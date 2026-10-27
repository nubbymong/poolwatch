using System.Collections.Concurrent;
using System.Diagnostics;

namespace PoolWatch.Core.Util;

/// <summary>
/// PoolWatch's own cost, by stage: wall time, thread CPU time and managed allocations per named stage, and bytes
/// written per output file. Cheap enough to stay on: one Stopwatch read, one thread-CPU read and one allocation-counter
/// read per stage entry. Thread-safe (ETW callbacks and the sampler both record).
/// </summary>
public static class Profiler
{
    public sealed class StageStats
    {
        public long Count;
        public long WallTicks;
        public long CpuTicks;
        public long AllocBytes;
        public long MaxWallTicks;
    }

    private static readonly ConcurrentDictionary<string, StageStats> Stages = new();
    private static readonly ConcurrentDictionary<string, long> Writes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Current thread's CPU time in 100 ns ticks (set by the Windows host; 0 elsewhere).</summary>
    public static Func<long> ThreadCpuTicks { get; set; } = () => 0;

    public static bool Enabled { get; set; } = true;

    public readonly struct Scope : IDisposable
    {
        private readonly StageStats? _s;
        private readonly long _wall, _cpu, _alloc;

        internal Scope(StageStats s)
        {
            _s = s;
            _wall = Stopwatch.GetTimestamp();
            _cpu = ThreadCpuTicks();
            _alloc = GC.GetAllocatedBytesForCurrentThread();
        }

        public void Dispose()
        {
            if (_s is null) return;
            long wall = Stopwatch.GetTimestamp() - _wall;
            Interlocked.Increment(ref _s.Count);
            Interlocked.Add(ref _s.WallTicks, wall);
            Interlocked.Add(ref _s.CpuTicks, ThreadCpuTicks() - _cpu);
            Interlocked.Add(ref _s.AllocBytes, GC.GetAllocatedBytesForCurrentThread() - _alloc);
            long max;
            while (wall > (max = Interlocked.Read(ref _s.MaxWallTicks)) && Interlocked.CompareExchange(ref _s.MaxWallTicks, wall, max) != max) { }
        }
    }

    /// <summary><c>using (Profiler.Stage("tick.tags")) { ... }</c></summary>
    public static Scope Stage(string name) => Enabled ? new Scope(Stages.GetOrAdd(name, _ => new StageStats())) : default;

    /// <summary>Counts bytes written to a file, keyed by its file name (all daily folders together).</summary>
    public static void Wrote(string path, long bytes) => WroteName(Path.GetFileName(path), bytes);

    /// <summary>Same, with the file name already known (the CSV writers call this per row: no allocation).</summary>
    public static void WroteName(string name, long bytes)
    {
        if (bytes <= 0) return;
        Writes.AddOrUpdate(name, static (_, b) => b, static (_, v, b) => v + b, bytes);
    }

    public static IReadOnlyDictionary<string, long> WrittenByFile => Writes;

    public static void Reset()
    {
        Stages.Clear();
        Writes.Clear();
    }

    /// <summary>Per-stage totals, most expensive (CPU, else wall) first.</summary>
    public static List<object> StageReport(double elapsedSeconds) =>
        Stages.OrderByDescending(kv => kv.Value.CpuTicks > 0 ? kv.Value.CpuTicks : kv.Value.WallTicks).Select(kv => (object)new
        {
            stage = kv.Key,
            count = kv.Value.Count,
            wallMs = Math.Round(kv.Value.WallTicks * 1000.0 / Stopwatch.Frequency, 1),
            cpuMs = Math.Round(kv.Value.CpuTicks / 10_000.0, 1),
            cpuPercentOfOneCore = elapsedSeconds > 0 ? Math.Round(kv.Value.CpuTicks / 10_000.0 / 1000 / elapsedSeconds * 100, 4) : 0,
            maxWallMs = Math.Round(kv.Value.MaxWallTicks * 1000.0 / Stopwatch.Frequency, 1),
            allocMB = Math.Round(kv.Value.AllocBytes / 1048576.0, 2),
            allocKBPerCall = kv.Value.Count > 0 ? Math.Round(kv.Value.AllocBytes / 1024.0 / kv.Value.Count, 1) : 0,
        }).ToList();
}
