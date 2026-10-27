using PoolWatch.Core.Model;

namespace PoolWatch.Core.Services;

/// <summary>A process that has been heavy in the background: its average cost over the window it was judged on.</summary>
public sealed record HeavyProcess(ProcessKey Key, string Image, double Cores, long PageFaultsPerSec, TimeSpan Covered);

/// <summary>
/// Minute points of cumulative CPU time and page faults for the processes that are busy (addendum 4 B / C), from the
/// process snapshot PoolWatch already takes; nothing extra is collected. It answers two questions:
/// <list type="bullet">
/// <item>what a process cost on average over the last N minutes (a service's cost before PoolWatch turns it off);</item>
/// <item>which processes ran over 0.5 core for more than 30 minutes with no window in front ("heavy background app"),
/// judged over the whole 30 minutes, not one 10 s window.</item>
/// </list>
/// Bounded: only processes busy in the last step are promoted (at most <see cref="MaxTracked"/>), a series idle for
/// <see cref="IdleDrop"/> is dropped, and points older than <see cref="Keep"/> are trimmed. Sampler thread only.
/// </summary>
public sealed class ProcessCostHistory
{
    public static readonly TimeSpan Step = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan Keep = TimeSpan.FromMinutes(35);
    public static readonly TimeSpan IdleDrop = TimeSpan.FromMinutes(10);
    public const int MaxTracked = 300;
    /// <summary>Busy in a step: at least this many cores or page faults per second.</summary>
    public const double BusyCores = 0.05;
    public const long BusyFaults = 1000;

    private readonly record struct Point(DateTime TimeUtc, long Cpu100ns, uint Faults, bool Foreground);

    private sealed class Series
    {
        public required string Image { get; init; }
        public List<Point> Points { get; } = new();
        public DateTime LastBusyUtc { get; set; }
    }

    private readonly Dictionary<ProcessKey, Point> _last = new();
    private readonly Dictionary<ProcessKey, Series> _series = new();
    private readonly HashSet<int> _foregroundThisStep = new();
    private DateTime _nextStep = DateTime.MinValue;

    public int Tracked => _series.Count;

    /// <summary>
    /// Every sample: remembers which process had the window in front (<paramref name="foregroundPid"/>, 0 when unknown);
    /// once per <see cref="Step"/> it takes a point for every busy process.
    /// </summary>
    public void Observe(DateTime nowUtc, IReadOnlyList<ProcessSample> processes, int foregroundPid)
    {
        if (foregroundPid > 0 && _foregroundThisStep.Count < 64) _foregroundThisStep.Add(foregroundPid);
        if (nowUtc < _nextStep) return;
        _nextStep = nowUtc + Step;
        var seen = new HashSet<ProcessKey>();
        foreach (var p in processes)
        {
            if (p.Pid <= 4) continue; // Idle and System are not apps
            var key = p.Key;
            seen.Add(key);
            var point = new Point(nowUtc, p.KernelTime + p.UserTime, p.PageFaultCount, _foregroundThisStep.Contains(p.Pid));
            if (_series.TryGetValue(key, out var s))
            {
                if (Busy(s.Points[^1], point)) s.LastBusyUtc = nowUtc;
                s.Points.Add(point);
                // Keep one point at or before the oldest moment still needed, so a full Keep window stays measurable.
                int cut = 0;
                while (cut + 1 < s.Points.Count && s.Points[cut + 1].TimeUtc <= nowUtc - Keep) cut++;
                if (cut > 0) s.Points.RemoveRange(0, cut);
            }
            else if (_last.TryGetValue(key, out var prev) && Busy(prev, point) && _series.Count < MaxTracked)
            {
                var ns = new Series { Image = p.ImageName, LastBusyUtc = nowUtc };
                ns.Points.Add(prev);
                ns.Points.Add(point);
                _series[key] = ns;
            }
            _last[key] = point;
        }
        _foregroundThisStep.Clear();
        foreach (var k in _last.Keys.Where(k => !seen.Contains(k)).ToList()) _last.Remove(k); // exited
        foreach (var (k, s) in _series.ToList())
            if (!seen.Contains(k) || nowUtc - s.LastBusyUtc >= IdleDrop) _series.Remove(k);
    }

    private static bool Busy(Point a, Point b)
    {
        double secs = (b.TimeUtc - a.TimeUtc).TotalSeconds;
        if (secs <= 0) return false;
        return Cores(a, b) >= BusyCores || unchecked(b.Faults - a.Faults) / secs >= BusyFaults;
    }

    private static double Cores(Point a, Point b)
    {
        double secs = (b.TimeUtc - a.TimeUtc).TotalSeconds;
        return secs <= 0 ? 0 : Math.Max(0, b.Cpu100ns - a.Cpu100ns) / (10_000_000.0 * secs);
    }

    /// <summary>
    /// The process's average cores and page faults per second over the last <paramref name="span"/> (or as much of it as
    /// is held). Null when it is not tracked (it was not busy) or has fewer than two points.
    /// </summary>
    public (double cores, long faultsPerSec, TimeSpan covered)? Average(ProcessKey key, TimeSpan span, DateTime nowUtc)
    {
        if (!_series.TryGetValue(key, out var s) || s.Points.Count < 2) return null;
        var last = s.Points[^1];
        var first = s.Points.LastOrDefault(p => p.TimeUtc <= nowUtc - span);
        if (first == default) first = s.Points[0];
        double secs = (last.TimeUtc - first.TimeUtc).TotalSeconds;
        if (secs <= 0) return null;
        return (Cores(first, last), (long)(unchecked(last.Faults - first.Faults) / secs), last.TimeUtc - first.TimeUtc);
    }

    /// <summary>
    /// Heavy background apps (addendum 4 C): held for the whole <paramref name="window"/>, averaging at least
    /// <paramref name="minCores"/> over it, and never the foreground process in it. Most expensive first.
    /// </summary>
    public List<HeavyProcess> Heavy(DateTime nowUtc, TimeSpan window, double minCores)
    {
        var list = new List<HeavyProcess>();
        foreach (var (key, s) in _series)
        {
            // Anchored on the newest point, not on now: between two minute points (the UI asks at any moment) the window is
            // still the last full 30 minutes the points cover (Part 3 review: a heavy app flickered out between steps).
            var last = s.Points[^1];
            if (nowUtc - last.TimeUtc > Step + Step) continue; // no recent point: not running any more
            int i0 = s.Points.FindLastIndex(p => p.TimeUtc <= last.TimeUtc - window);
            if (i0 < 0) continue; // not held for the whole window yet
            var first = s.Points[i0];
            bool inFront = false;
            for (int i = i0 + 1; i < s.Points.Count; i++) inFront |= s.Points[i].Foreground;
            if (inFront) continue;
            double cores = Cores(first, last);
            if (cores < minCores) continue;
            double secs = (last.TimeUtc - first.TimeUtc).TotalSeconds;
            list.Add(new HeavyProcess(key, s.Image, cores, (long)(unchecked(last.Faults - first.Faults) / secs), last.TimeUtc - first.TimeUtc));
        }
        return list.OrderByDescending(h => h.Cores).ToList();
    }
}
