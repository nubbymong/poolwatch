using PoolWatch.Core.Model;

namespace PoolWatch.Core.Boot;

/// <summary>A tag snapshot kept for an app: the last one before it started, or the first one after it exited.</summary>
public sealed record AppSnapshot(string App, bool BeforeLaunch, DateTime TimeUtc, IReadOnlyList<TagSize> Tags)
{
    /// <summary>"before-launch" / "after-exit".</summary>
    public string Moment => BeforeLaunch ? "before-launch" : "after-exit";
}

/// <summary>
/// Before-launch / after-exit tag snapshots outside experiments (item 13, Part 3 review): for each watched app, the last
/// full tag snapshot before it started (it had no process, then it had one) and the first one after it exited (its last
/// process ended). The vendor bundle carries them. Taken from the samples PoolWatch already has: the previous sample's tag
/// list is held by reference (no copy each sample); a snapshot is built only when an app starts or exits, keeping tags of
/// at least 16 KB (and the always-tracked ones) as the sparse history does. Shared runtimes (node) are left out: their
/// processes come and go all day, and "before node started" means nothing. In memory, the newest per app and moment.
/// Sampler thread only.
/// </summary>
public sealed class AppSnapshots
{
    private readonly Dictionary<string, string> _imageToApp = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string app, bool before), AppSnapshot> _snapshots = new();
    private IReadOnlyList<PoolTagEntry>? _prevTags;
    private DateTime _prevTime;
    private bool _first = true;
    public const ulong MinBytes = 16 * 1024;

    /// <param name="apps">Watched apps, exe names without .exe (Settings.WatchedApps).</param>
    public AppSnapshots(IEnumerable<string> apps)
    {
        foreach (var a in apps)
        {
            string image = a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? a : a + ".exe";
            if (Identity.ProcessIdentities.SharedRuntimes.Contains(image)) continue;
            _imageToApp.TryAdd(image, a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? a[..^4] : a);
        }
    }

    public IReadOnlyCollection<AppSnapshot> Snapshots => _snapshots.Values;

    /// <summary>Feeds one sample. Returns the snapshots taken now (an app started or exited since the last sample).</summary>
    public List<AppSnapshot> Observe(DateTime nowUtc, IReadOnlyList<ProcessSample> processes, IReadOnlyList<PoolTagEntry> tags)
    {
        var taken = new List<AppSnapshot>();
        var now = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in processes)
            if (_imageToApp.TryGetValue(p.ImageName, out var app)) now[app] = now.GetValueOrDefault(app) + 1;
        if (!_first)
            foreach (var app in _imageToApp.Values.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                int before = _running.GetValueOrDefault(app), after = now.GetValueOrDefault(app);
                if (before == 0 && after > 0 && _prevTags is { } prev) taken.Add(Keep(app, true, _prevTime, prev)); // the last snapshot before it started
                else if (before > 0 && after == 0) taken.Add(Keep(app, false, nowUtc, tags));                     // the first after it exited
            }
        _first = false;
        _running.Clear();
        foreach (var (app, n) in now) _running[app] = n;
        _prevTags = tags;
        _prevTime = nowUtc;
        return taken;
    }

    private AppSnapshot Keep(string app, bool before, DateTime timeUtc, IReadOnlyList<PoolTagEntry> tags)
    {
        var list = new List<TagSize>();
        foreach (var t in tags)
            if (t.TotalUsed >= MinBytes || Tags.BuiltinTags.AlwaysTracked.Contains(t.Tag))
                list.Add(new TagSize(t.Tag.ToDisplay(), (long)t.PagedUsed, (long)t.NonPagedUsed));
        var s = new AppSnapshot(app, before, timeUtc, list);
        _snapshots[(app, before)] = s;
        return s;
    }
}
