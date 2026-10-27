using PoolWatch.Core.Spawn;

namespace PoolWatch.Core.Model;

/// <summary>
/// Turns the raw lifecycle stream into what is worth keeping: notable events individually (orphans, alerts, findings,
/// service changes, captures, the start and exit of processes that ran at least <see cref="LongLived"/>), and
/// short-lived processes as one rollup row per (owner, parent image, child image) per <see cref="RollupWindow"/>
/// ("bash.exe ×90 started 412 × git, grep, sed ..."). Keyed by the parent's image, not its PID: every Claude Code
/// command is its own bash.exe, so a per-PID key wrote one row per command. A busy PC starts hundreds of processes a minute; writing each one
/// made events.csv the largest file (16 MB/h on the CI soak). Starts are held for <see cref="LongLived"/> to learn
/// whether the process is short-lived. Bounded: at most <see cref="MaxPending"/> starts are held. Sampler thread only.
/// </summary>
public sealed class EventSummarizer
{
    public static readonly TimeSpan LongLived = TimeSpan.FromSeconds(30);
    public const int MaxPending = 20_000;
    private const int MaxParents = 1000;
    private readonly TimeSpan _window;
    private readonly Dictionary<ProcessKey, LifecycleEvent> _pendingStarts = new();
    // Keyed by the owner's identity key (2e), not its display name: two owners can share a name.
    private readonly Dictionary<(string? ownerKey, string parent, string image), Rollup> _rollups = new();
    private readonly Dictionary<int, Identity.Owner?> _ownerByParent = new(); // per window: one owner lookup per parent PID
    private DateTime _windowStart = DateTime.MinValue;
    private Func<int, Identity.Owner?>? _owner;

    private sealed class Rollup
    {
        public string? OwnerName;
        public int Starts, Exits, FirstParentPid = -1;
        public readonly HashSet<int> ParentPids = [];
        public DateTime First = DateTime.MaxValue, Last = DateTime.MinValue;
        public readonly Dictionary<string, int> Patterns = new(StringComparer.Ordinal);
    }

    private readonly Func<string?, bool>? _alwaysNotable;

    /// <param name="alwaysNotable">Command lines that are listed even when short-lived (git worktree removal: the release analysis needs them).</param>
    public EventSummarizer(TimeSpan? rollupWindow = null, Func<string?, bool>? alwaysNotable = null)
    {
        _window = rollupWindow ?? TimeSpan.FromSeconds(60);
        _alwaysNotable = alwaysNotable;
    }

    public TimeSpan RollupWindow => _window;
    public long RolledUp { get; private set; }
    public int Pending => _pendingStarts.Count;

    /// <summary>Feeds this tick's events; returns the events to record (notable ones and closed rollups).</summary>
    /// <param name="parentName">Image name of a parent PID (for rollup labels).</param>
    /// <param name="owner">Owner of a parent PID (item 4's identity; resolved once per rollup, while the parent is usually still alive).</param>
    public List<LifecycleEvent> Process(IReadOnlyList<LifecycleEvent> events, DateTime nowUtc, Func<int, string?> parentName, Func<int, Identity.Owner?>? owner = null)
    {
        _owner = owner;
        var output = new List<LifecycleEvent>();
        if (_windowStart == DateTime.MinValue) _windowStart = nowUtc;
        foreach (var e in events)
        {
            switch (e.Kind)
            {
                case LifecycleKind.Start:
                    if (_alwaysNotable?.Invoke(e.CommandLine) == true) { output.Add(e); break; }
                    if (e.Pid <= 4 || (e.Detail == "seen in snapshot" && nowUtc - e.TimeUtc >= LongLived)) { output.Add(e); break; }
                    if (_pendingStarts.Count >= MaxPending) { Count(e, parentName, start: true); break; }
                    _pendingStarts[e.Key] = e;
                    break;
                case LifecycleKind.Exit:
                    if (_alwaysNotable?.Invoke(e.CommandLine) == true) output.Add(e);
                    else if (_pendingStarts.Remove(e.Key, out var start))
                    {
                        if (e.TimeUtc - start.TimeUtc >= LongLived) { output.Add(start); output.Add(e); } // ran long enough: listed
                        else
                        {
                            // Started and exited within LongLived: counted, not listed.
                            Count(start, parentName, start: true);
                            Count(e, parentName, start: false);
                        }
                    }
                    else if (e.CreateTime > 0 && e.TimeUtc - e.Key.CreateTimeUtc < LongLived) Count(e, parentName, start: false);
                    else output.Add(e);
                    break;
                default:
                    output.Add(e); // orphaned, alerts, findings, services, captures: always notable
                    break;
            }
        }
        // Starts that are still running after LongLived become individual (long-running) start events.
        if (_pendingStarts.Count > 0)
        {
            List<ProcessKey>? due = null;
            foreach (var (k, s) in _pendingStarts) if (nowUtc - s.TimeUtc >= LongLived) (due ??= new()).Add(k);
            if (due is not null)
                foreach (var k in due) { output.Add(_pendingStarts[k]); _pendingStarts.Remove(k); }
        }
        if (nowUtc - _windowStart >= _window) output.AddRange(Flush(nowUtc));
        if (output.Count > 1) output.Sort((a, b) => a.TimeUtc.CompareTo(b.TimeUtc)); // promoted starts and rollups arrive late
        return output;
    }

    /// <summary>Closes the current rollup window (also at exit).</summary>
    public List<LifecycleEvent> Flush(DateTime nowUtc)
    {
        _windowStart = nowUtc;
        var output = new List<LifecycleEvent>();
        foreach (var ((ownerKey, parent, image), r) in _rollups.OrderBy(kv => kv.Value.First))
        {
            var patterns = r.Patterns.OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{kv.Value}× {kv.Key}");
            int n = r.ParentPids.Count;
            string who = n <= 1 ? $"{parent} ({r.FirstParentPid})" : $"{parent} ×{n:N0}{(n >= MaxParents ? "+" : "")}";
            output.Add(new LifecycleEvent
            {
                TimeUtc = r.First, Kind = LifecycleKind.Rollup, ParentPid = r.FirstParentPid, Image = image, Owner = r.OwnerName, OwnerKey = ownerKey, Count = r.Starts,
                Detail = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{who} started {r.Starts:N0}, {r.Exits:N0} exited, short-lived, {Detection.Fmt.Clock(r.First)}-{Detection.Fmt.Clock(r.Last)}: ") + string.Join("; ", patterns),
            });
        }
        _rollups.Clear();
        _ownerByParent.Clear();
        return output;
    }

    private void Count(LifecycleEvent e, Func<int, string?> parentName, bool start)
    {
        RolledUp++;
        if (!_ownerByParent.TryGetValue(e.ParentPid, out var owner) && _ownerByParent.Count < 5000)
            _ownerByParent[e.ParentPid] = owner = _owner?.Invoke(e.ParentPid);
        var key = (owner?.Key, parentName(e.ParentPid) ?? "?", e.Image);
        if (!_rollups.TryGetValue(key, out var r))
        {
            if (_rollups.Count >= 500) { key = (null, "(other parents)", "(other)"); owner = null; }
            if (!_rollups.TryGetValue(key, out r)) _rollups[key] = r = new Rollup { OwnerName = owner?.Name };
        }
        if (r.FirstParentPid < 0) r.FirstParentPid = e.ParentPid;
        if (r.ParentPids.Count < MaxParents) r.ParentPids.Add(e.ParentPid); // distinct parents, counted up to MaxParents
        if (start)
        {
            r.Starts++;
            string p = CommandLineTokens.Pattern(e.Image, e.CommandLine, 4);
            if (r.Patterns.ContainsKey(p) || r.Patterns.Count < 20) r.Patterns[p] = r.Patterns.GetValueOrDefault(p) + 1;
        }
        else r.Exits++;
        if (e.TimeUtc < r.First) r.First = e.TimeUtc;
        if (e.TimeUtc > r.Last) r.Last = e.TimeUtc;
    }
}
