using PoolWatch.Core.Spawn;

namespace PoolWatch.Core.Model;

/// <summary>
/// Turns the raw lifecycle stream into what is worth keeping: notable events individually (orphans, alerts, findings,
/// service changes, captures, the start and exit of processes that ran at least <see cref="LongLived"/>), and
/// short-lived processes as one rollup row per (owner, parent image, child image) per <see cref="RollupWindow"/>
/// ("bash.exe ×90 started 412 × git, grep, sed ..."). Keyed by the parent's image, not its PID: every Claude Code
/// command is its own bash.exe, so a per-PID key wrote one row per command. A busy PC starts hundreds of processes a minute; writing each one
/// made events.csv the largest file (16 MB/h on the CI soak). Starts are held for <see cref="LongLived"/> to learn
/// whether the process is short-lived. Bounded: at most <see cref="MaxPending"/> starts are held. How long a process
/// ran, how long a start is held and the rollup window are timed on the clock that only goes forward: each event's time,
/// which Windows gives on the system clock, is read onto it as it arrives (<see cref="Moment.MsOf"/>), so setting the
/// system clock back or forward neither holds a start back nor lists it early. An exit is judged by its start's time as
/// that start was held (a start listed as long-running has its exit listed); the exit of a start that was not held, or
/// whose listing was let go (at most <see cref="MaxPending"/> listed starts are kept, the earliest let go first), is judged
/// by Windows' create time, read onto the forward clock as the exit arrives. Sampler thread only.
/// </summary>
public sealed class EventSummarizer
{
    public static readonly TimeSpan LongLived = TimeSpan.FromSeconds(30);
    public const int MaxPending = 20_000;
    private const int MaxParents = 1000;
    private readonly TimeSpan _window;
    /// <summary>The starts held, each with its time on the forward clock (<see cref="Moment.Ms"/>), read as it arrived.</summary>
    private readonly Dictionary<ProcessKey, (LifecycleEvent Start, long Ms)> _pendingStarts = new();
    /// <summary>
    /// The starts held and then listed as long-running, each with its time on the forward clock as it was held, until the
    /// process exits: its exit is listed too, whatever the system clock did while it ran. At most <see cref="MaxPending"/>
    /// (past that the earliest is let go, and its exit is judged by the create time, as an exit whose start was not held).
    /// Each also has a number of its own (<see cref="_listedOrder"/>), so two listed at the same time are two entries of
    /// <see cref="_listedByTime"/>.
    /// </summary>
    private readonly Dictionary<ProcessKey, (long Ms, long Order)> _listedStarts = new();
    /// <summary>
    /// The listed starts by their time (<see cref="_listedStarts"/>'s, then its number), so the earliest is found without a
    /// look at every one: a burst of starts promoted while the list is full costs a step each, not a pass over the 20,000.
    /// </summary>
    private readonly SortedSet<(long Ms, long Order, ProcessKey Key)> _listedByTime =
        new(Comparer<(long Ms, long Order, ProcessKey Key)>.Create((a, b) => a.Ms != b.Ms ? a.Ms.CompareTo(b.Ms) : a.Order.CompareTo(b.Order)));
    /// <summary>How many starts were listed so far: the next one's number.</summary>
    private long _listedOrder;
    // Keyed by the owner's identity key, not its display name: two owners can share a name.
    private readonly Dictionary<(string? ownerKey, string parent, string image), Rollup> _rollups = new();
    private readonly Dictionary<int, Identity.Owner?> _ownerByParent = new(); // per window: one owner lookup per parent PID
    /// <summary>When the rollup window began (<see cref="Moment.Ms"/>); null before the first call.</summary>
    private long? _windowStartMs;
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
    /// <summary>The starts listed as long-running whose exit is still to come (at most <see cref="MaxPending"/>).</summary>
    internal int Listed => _listedStarts.Count;

    /// <summary>Feeds this tick's events; returns the events to record (notable ones and closed rollups).</summary>
    /// <param name="parentName">Image name of a parent PID (for rollup labels).</param>
    /// <param name="owner">Owner of a parent PID (its identity; resolved once per rollup, while the parent is usually still alive).</param>
    public List<LifecycleEvent> Process(IReadOnlyList<LifecycleEvent> events, Moment now, Func<int, string?> parentName, Func<int, Identity.Owner?>? owner = null)
    {
        _owner = owner;
        var output = new List<LifecycleEvent>();
        _windowStartMs ??= now.Ms;
        long longLived = (long)LongLived.TotalMilliseconds;
        foreach (var e in events)
        {
            switch (e.Kind)
            {
                case LifecycleKind.Start:
                    if (_alwaysNotable?.Invoke(e.CommandLine) == true) { output.Add(e); break; }
                    if (e.Pid <= 4 || (e.Detail == "seen in snapshot" && now.Ms - now.MsOf(e.TimeUtc) >= longLived)) { output.Add(e); break; }
                    if (_pendingStarts.Count >= MaxPending) { Count(e, parentName, start: true); break; }
                    _pendingStarts[e.Key] = (e, now.MsOf(e.TimeUtc));
                    break;
                case LifecycleKind.Exit:
                    if (_alwaysNotable?.Invoke(e.CommandLine) == true) output.Add(e);
                    else if (_pendingStarts.Remove(e.Key, out var held))
                    {
                        var start = held.Start;
                        if (now.MsOf(e.TimeUtc) - held.Ms >= longLived) { output.Add(start); output.Add(e); } // ran long enough: listed
                        else
                        {
                            // Started and exited within LongLived: counted, not listed.
                            Count(start, parentName, start: true);
                            Count(e, parentName, start: false);
                        }
                    }
                    // Its start was held and listed as long-running (it ran LongLived on the forward clock): listed.
                    else if (Unlist(e.Key)) output.Add(e);
                    // A start not held (PoolWatch started after it, or too many were held): Windows' create time is a system
                    // time, read onto the forward clock now.
                    else if (e.CreateTime > 0 && now.MsOf(e.TimeUtc) - now.MsOf(e.Key.CreateTimeUtc) < longLived) Count(e, parentName, start: false);
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
            foreach (var (k, s) in _pendingStarts) if (now.Ms - s.Ms >= longLived) (due ??= new()).Add(k);
            if (due is not null)
                foreach (var k in due)
                {
                    var (start, ms) = _pendingStarts[k];
                    output.Add(start);
                    _pendingStarts.Remove(k);
                    List(k, ms);
                }
        }
        if (now.Ms - _windowStartMs.Value >= (long)_window.TotalMilliseconds) output.AddRange(Flush(now));
        if (output.Count > 1) output.Sort((a, b) => a.TimeUtc.CompareTo(b.TimeUtc)); // promoted starts and rollups arrive late
        return output;
    }

    /// <summary>
    /// Lists a start held as long-running, held at <paramref name="ms"/> on the forward clock; with
    /// <see cref="MaxPending"/> listed already, the earliest is let go.
    /// </summary>
    private void List(ProcessKey key, long ms)
    {
        Unlist(key);
        if (_listedStarts.Count >= MaxPending)
        {
            var earliest = _listedByTime.Min;
            _listedByTime.Remove(earliest);
            _listedStarts.Remove(earliest.Key);
        }
        long order = _listedOrder++;
        _listedStarts[key] = (ms, order);
        _listedByTime.Add((ms, order, key));
    }

    /// <summary>Takes a listed start off the list (its exit came); false when it is not listed.</summary>
    private bool Unlist(ProcessKey key)
    {
        if (!_listedStarts.Remove(key, out var listed)) return false;
        _listedByTime.Remove((listed.Ms, listed.Order, key));
        return true;
    }

    /// <summary>Closes the current rollup window (also at exit).</summary>
    public List<LifecycleEvent> Flush(Moment now)
    {
        _windowStartMs = now.Ms;
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
