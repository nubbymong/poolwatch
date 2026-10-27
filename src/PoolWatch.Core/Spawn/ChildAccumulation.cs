using PoolWatch.Core.Findings;
using PoolWatch.Core.Identity;
using PoolWatch.Core.Model;

namespace PoolWatch.Core.Spawn;

/// <summary>One group's live count at a census.</summary>
public sealed record AccumulationPoint(DateTime TimeUtc, int Count);

/// <summary>A group of live processes: who they work for and what they are ("Codex" / "node.exe · Codex cua_node").</summary>
public sealed class AccumulationGroup
{
    public required string OwnerName { get; init; }
    public required string What { get; init; }
    public List<AccumulationPoint> Points { get; } = new();
    /// <summary>One point an hour (the first census of each hour), up to a day: the baseline a slow pile-up is judged against.</summary>
    public List<AccumulationPoint> Hourly { get; } = new();
    public int LastAlertCount { get; set; }
    public DateTime LastAlertUtc { get; set; } = DateTime.MinValue;
    public int Current => Points.Count > 0 ? Points[^1].Count : 0;
    public string Key => OwnerName + "|" + What;
}

/// <summary>
/// Child accumulation (item 12): every <see cref="Settings.AccumulationIntervalSeconds"/>, the live processes are
/// counted per owner and identity (the issue saw 103 node.exe plus 104 node_repl.exe; a busy PC can show half a dozen
/// Codex cua_node processes). A group that grew at nearly every census over the window, by at least
/// <see cref="Settings.AccumulationMinRise"/> to at least <see cref="Settings.AccumulationMinCount"/>, is flagged: helpers
/// that are started and never exit. Re-flagged when it grows by another half, or after an hour. Sampler thread only.
/// </summary>
public sealed class ChildAccumulation
{
    private readonly Settings _settings;
    private readonly Dictionary<string, AccumulationGroup> _groups = new();
    private DateTime _next = DateTime.MinValue;

    public ChildAccumulation(Settings settings) => _settings = settings;

    public IReadOnlyCollection<AccumulationGroup> Groups => _groups.Values;
    public int Censuses { get; private set; }

    /// <summary>The groups with the most processes now (for the Findings tab and the summary).</summary>
    public List<AccumulationGroup> Top(int n) => _groups.Values.Where(g => g.Current > 0).OrderByDescending(g => g.Current).Take(n).ToList();

    /// <summary>A census is due (checked before the live list is built: the list is not free).</summary>
    public bool Due(DateTime nowUtc) => nowUtc >= _next;

    /// <param name="identity">Identity of a live record (from its ancestry); null to leave it out (system processes).</param>
    public List<Finding> Observe(IReadOnlyList<ProcessRecord> live, Func<ProcessRecord, ProcessIdentity?> identity, DateTime nowUtc)
    {
        var found = new List<Finding>();
        if (nowUtc < _next) return found;
        _next = nowUtc.AddSeconds(_settings.AccumulationIntervalSeconds);
        Censuses++;
        var counts = new Dictionary<string, (string owner, string what, int n)>();
        foreach (var r in live)
        {
            if (r.Key.Pid <= 4 || identity(r) is not { } id || id.Owner.Kind == OwnerKind.System) continue;
            string what = id.Role is null ? id.Image : $"{id.Image} · {id.Role}";
            string key = id.Owner.Name + "|" + what;
            counts[key] = counts.TryGetValue(key, out var c) ? (c.owner, c.what, c.n + 1) : (id.Owner.Name, what, 1);
        }
        int keep = Math.Max(2, _settings.AccumulationWindowPoints) * 4;
        foreach (var (key, (owner, what, n)) in counts)
        {
            if (!_groups.TryGetValue(key, out var g))
            {
                if (_groups.Count >= 2000) continue; // bounded: a new group beyond this is not tracked
                _groups[key] = g = new AccumulationGroup { OwnerName = owner, What = what };
            }
            g.Points.Add(new AccumulationPoint(nowUtc, n));
        }
        foreach (var g in _groups.Values.ToList())
        {
            if (!counts.ContainsKey(g.Key)) g.Points.Add(new AccumulationPoint(nowUtc, 0));
            if (g.Points.Count > keep) g.Points.RemoveRange(0, g.Points.Count - keep);
            if (g.Points.All(p => p.Count == 0)) { _groups.Remove(g.Key); continue; }
            // Item 12 (Part 3): an hourly point, kept for a day, so a pile-up too slow for the short window still shows.
            if (g.Hourly.Count == 0 || nowUtc - g.Hourly[^1].TimeUtc >= HourlyStep)
            {
                g.Hourly.Add(g.Points[^1]);
                if (g.Hourly.Count > MaxHourly) g.Hourly.RemoveRange(0, g.Hourly.Count - MaxHourly);
            }
            if (Check(g, nowUtc) is { } f) found.Add(f);
        }
        return found;
    }

    /// <summary>The spacing of a group's hourly points (a little under an hour, so a census every 5 min lands on each hour).</summary>
    public static readonly TimeSpan HourlyStep = TimeSpan.FromMinutes(59);
    /// <summary>A day of hourly points.</summary>
    public const int MaxHourly = 25;
    /// <summary>A slow pile-up is judged over at least this many hourly points (three hours).</summary>
    public const int SlowMinPoints = 4;

    /// <summary>
    /// A pile-up too slow for the short window (item 12, Part 3 review: "slow pile-ups must still trip"): against the group's
    /// own baseline over hours. Over the hourly points since the last time the count fell by more than one (the trend
    /// restarts there: helpers that do exit), at least three hours of them, it rose at half the steps or more, never fell by
    /// more than one, grew by <see cref="Settings.AccumulationMinRise"/> to at least <see cref="Settings.AccumulationMinCount"/>,
    /// and the count now has not fallen back below the last hourly point by more than one.
    /// </summary>
    public bool GrowingSlowly(AccumulationGroup g, out int from, out int to, out TimeSpan span)
    {
        from = to = 0;
        span = TimeSpan.Zero;
        var h = g.Hourly;
        if (h.Count < SlowMinPoints) return false;
        int start = 0;
        for (int i = 1; i < h.Count; i++) if (h[i].Count - h[i - 1].Count < -1) start = i;
        if (h.Count - start < SlowMinPoints) return false;
        int rises = 0, steps = h.Count - 1 - start;
        for (int i = start + 1; i < h.Count; i++) if (h[i].Count > h[i - 1].Count) rises++;
        from = h[start].Count;
        to = g.Current;
        span = g.Points[^1].TimeUtc - h[start].TimeUtc;
        if (to < h[^1].Count - 1) return false;
        return rises * 2 >= steps && to - from >= _settings.AccumulationMinRise && to >= _settings.AccumulationMinCount;
    }

    /// <summary>Steady growth over the last window: rose at 3 of 4 steps or more, never fell by more than one, net rise and size over the thresholds.</summary>
    public bool Growing(AccumulationGroup g, out int from, out int to, out TimeSpan span)
    {
        int w = Math.Max(2, _settings.AccumulationWindowPoints);
        from = to = 0;
        span = TimeSpan.Zero;
        if (g.Points.Count < w + 1) return false;
        var pts = g.Points.Skip(g.Points.Count - w - 1).ToList();
        from = pts[0].Count;
        to = pts[^1].Count;
        span = pts[^1].TimeUtc - pts[0].TimeUtc;
        int rises = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            int d = pts[i].Count - pts[i - 1].Count;
            if (d > 0) rises++;
            if (d < -1) return false;
        }
        return rises * 4 >= w * 3 && to - from >= _settings.AccumulationMinRise && to >= _settings.AccumulationMinCount;
    }

    private Finding? Check(AccumulationGroup g, DateTime now)
    {
        bool slow = false;
        if (!Growing(g, out int from, out int to, out var span))
        {
            if (!GrowingSlowly(g, out from, out to, out span)) return null;
            slow = true;
        }
        // Again when it grew by half, or after an hour (a slow pile-up: after an hour only if it grew since).
        bool again = g.LastAlertCount == 0 || to >= g.LastAlertCount * 3 / 2 || (now - g.LastAlertUtc >= TimeSpan.FromHours(1) && (!slow || to > g.LastAlertCount));
        if (!again) return null;
        g.LastAlertCount = to;
        g.LastAlertUtc = now;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string when = span.TotalHours >= 2 ? span.TotalHours.ToString("0.#", inv) + " h" : span.TotalMinutes >= 1 ? span.TotalMinutes.ToString("0", inv) + " min" : span.TotalSeconds.ToString("0", inv) + " s";
        var counts = slow
            ? $"Hourly counts: {string.Join(", ", g.Hourly.Select(p => p.Count))} (now {g.Current})"
            : $"Counts: {string.Join(", ", g.Points.TakeLast(Math.Max(2, _settings.AccumulationWindowPoints) + 1).Select(p => p.Count))}";
        return new Finding
        {
            TimeUtc = now, Kind = FindingKind.ChildAccumulation, Severity = to >= 3 * _settings.AccumulationMinCount ? FindingSeverity.Alert : FindingSeverity.Warning,
            Title = slow
                ? $"Child processes piling up slowly: {to} × {g.What} under {g.OwnerName} ({from} -> {to} in {when}, never falling back)"
                : $"Child processes piling up: {to} × {g.What} under {g.OwnerName} ({from} -> {to} in {when}, rising at nearly every check)",
            Details = [counts,
                "Helpers that are started and never exit hold their own handles, tokens and pool memory; closing the owner app usually frees them."],
            Image = g.What,
            Data = new Dictionary<string, object?> { ["owner"] = g.OwnerName, ["what"] = g.What, ["from"] = from, ["to"] = to, ["minutes"] = Math.Round(span.TotalMinutes, 1), ["slow"] = slow },
        };
    }
}
