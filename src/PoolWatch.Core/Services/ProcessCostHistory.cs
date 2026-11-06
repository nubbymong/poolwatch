using PoolWatch.Core.Model;

namespace PoolWatch.Core.Services;

/// <summary>A process that has been heavy in the background: its average cost over the window it was judged on.</summary>
public sealed record HeavyProcess(ProcessKey Key, string Image, double Cores, long PageFaultsPerSec, TimeSpan Covered);

/// <summary>
/// Minute points of cumulative CPU time and page faults for the processes that are busy, from the
/// process snapshot PoolWatch already takes; nothing extra is collected. It answers two questions:
/// <list type="bullet">
/// <item>what a process cost on average over the last N minutes (a service's cost before PoolWatch turns it off);</item>
/// <item>which processes ran over 0.5 core for more than 30 minutes with no window in front ("heavy background app"),
/// judged over the whole 30 minutes, not one 10 s window.</item>
/// </list>
/// Bounded: only processes busy in the last step are promoted (at most <see cref="MaxTracked"/>), a series idle for
/// <see cref="IdleDrop"/> is dropped, and points older than <see cref="Keep"/> are trimmed. Sampler thread only.
/// Every time it is given is on a clock that only goes forward (<see cref="ClockNow"/>: the time since Windows started,
/// which is what a sample's <see cref="Sample.TickMs"/> holds), never the system clock: setting that back or forward
/// neither stops the minute points nor bends a rate.
/// </summary>
public sealed class ProcessCostHistory
{
    /// <summary>Now on the clock the history is timed on: the time since Windows started (Environment.TickCount64).</summary>
    public static TimeSpan ClockNow() => TimeSpan.FromMilliseconds(Environment.TickCount64);

    public static readonly TimeSpan Step = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan Keep = TimeSpan.FromMinutes(35);
    public static readonly TimeSpan IdleDrop = TimeSpan.FromMinutes(10);
    public const int MaxTracked = 300;
    /// <summary>Busy in a step: at least this many cores or page faults per second.</summary>
    public const double BusyCores = 0.05;
    public const long BusyFaults = 1000;

    private readonly record struct Point(TimeSpan At, long Cpu100ns, uint Faults, bool Foreground);

    private sealed class Series
    {
        public required string Image { get; init; }
        public List<Point> Points { get; } = new();
        public TimeSpan LastBusy { get; set; }
    }

    private readonly Dictionary<ProcessKey, Point> _last = new();
    private readonly Dictionary<ProcessKey, Series> _series = new();
    private readonly HashSet<int> _foregroundThisStep = new();
    private TimeSpan? _nextStep;

    public int Tracked => _series.Count;

    /// <summary>
    /// Every sample: remembers which process had the window in front (<paramref name="foregroundPid"/>, 0 when unknown);
    /// once per <see cref="Step"/> it takes a point for every busy process.
    /// </summary>
    public void Observe(TimeSpan now, IReadOnlyList<ProcessSample> processes, int foregroundPid)
    {
        if (foregroundPid > 0 && _foregroundThisStep.Count < 64) _foregroundThisStep.Add(foregroundPid);
        if (_nextStep is { } due && now < due) return;
        _nextStep = now + Step;
        var seen = new HashSet<ProcessKey>();
        foreach (var p in processes)
        {
            if (p.Pid <= 4) continue; // Idle and System are not apps
            var key = p.Key;
            seen.Add(key);
            var point = new Point(now, p.KernelTime + p.UserTime, p.PageFaultCount, _foregroundThisStep.Contains(p.Pid));
            if (_series.TryGetValue(key, out var s))
            {
                if (Busy(s.Points[^1], point)) s.LastBusy = now;
                s.Points.Add(point);
                // Keep one point at or before the oldest moment still needed, so a full Keep window stays measurable.
                int cut = 0;
                while (cut + 1 < s.Points.Count && s.Points[cut + 1].At <= now - Keep) cut++;
                if (cut > 0) s.Points.RemoveRange(0, cut);
            }
            else if (_last.TryGetValue(key, out var prev) && Busy(prev, point) && _series.Count < MaxTracked)
            {
                var ns = new Series { Image = p.ImageName, LastBusy = now };
                ns.Points.Add(prev);
                ns.Points.Add(point);
                _series[key] = ns;
            }
            _last[key] = point;
        }
        _foregroundThisStep.Clear();
        foreach (var k in _last.Keys.Where(k => !seen.Contains(k)).ToList()) _last.Remove(k); // exited
        foreach (var (k, s) in _series.ToList())
            if (!seen.Contains(k) || now - s.LastBusy >= IdleDrop) _series.Remove(k);
    }

    private static bool Busy(Point a, Point b)
    {
        double secs = (b.At - a.At).TotalSeconds;
        if (secs <= 0) return false;
        return Cores(a, b) >= BusyCores || unchecked(b.Faults - a.Faults) / secs >= BusyFaults;
    }

    private static double Cores(Point a, Point b)
    {
        double secs = (b.At - a.At).TotalSeconds;
        return secs <= 0 ? 0 : Math.Max(0, b.Cpu100ns - a.Cpu100ns) / (10_000_000.0 * secs);
    }

    /// <summary>
    /// The process's average cores and page faults per second over the last <paramref name="span"/> (or as much of it as
    /// is held). Null when it is not tracked (it was not busy) or has fewer than two points.
    /// </summary>
    public (double cores, long faultsPerSec, TimeSpan covered)? Average(ProcessKey key, TimeSpan span, TimeSpan now)
    {
        if (!_series.TryGetValue(key, out var s) || s.Points.Count < 2) return null;
        var last = s.Points[^1];
        var first = s.Points.LastOrDefault(p => p.At <= now - span);
        if (first == default) first = s.Points[0];
        double secs = (last.At - first.At).TotalSeconds;
        if (secs <= 0) return null;
        return (Cores(first, last), (long)(unchecked(last.Faults - first.Faults) / secs), last.At - first.At);
    }

    /// <summary>
    /// Heavy background apps: held for the whole <paramref name="window"/>, averaging at least
    /// <paramref name="minCores"/> over it, and never the foreground process in it. Most expensive first.
    /// </summary>
    public List<HeavyProcess> Heavy(TimeSpan now, TimeSpan window, double minCores)
    {
        var list = new List<HeavyProcess>();
        foreach (var (key, s) in _series)
        {
            // Anchored on the newest point, not on now: between two minute points (the UI asks at any moment) the window is
            // still the last full 30 minutes the points cover (A heavy app flickered out between steps).
            var last = s.Points[^1];
            if (now - last.At > Step + Step) continue; // no recent point: not running any more
            int i0 = s.Points.FindLastIndex(p => p.At <= last.At - window);
            if (i0 < 0) continue; // not held for the whole window yet
            var first = s.Points[i0];
            bool inFront = false;
            for (int i = i0 + 1; i < s.Points.Count; i++) inFront |= s.Points[i].Foreground;
            if (inFront) continue;
            double cores = Cores(first, last);
            if (cores < minCores) continue;
            double secs = (last.At - first.At).TotalSeconds;
            list.Add(new HeavyProcess(key, s.Image, cores, (long)(unchecked(last.Faults - first.Faults) / secs), last.At - first.At));
        }
        return list.OrderByDescending(h => h.Cores).ToList();
    }
}
