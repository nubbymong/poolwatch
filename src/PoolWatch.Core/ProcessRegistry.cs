using PoolWatch.Core.Model;

namespace PoolWatch.Core;

/// <summary>Everything known about one process instance (live or exited).</summary>
public sealed class ProcessRecord
{
    public required ProcessKey Key { get; init; }
    public int Pid => Key.Pid;
    public int ParentPid { get; set; }
    /// <summary>The parent instance, when it was alive (or seen via ETW) at the time this process started.</summary>
    public ProcessKey? ParentKey { get; set; }
    public string Image { get; set; } = "";
    public string? ImagePath { get; set; }
    public string? CommandLine { get; set; }
    public string? User { get; set; }
    public DateTime StartUtc => Key.CreateTimeUtc;
    public DateTime FirstSeenUtc { get; set; }
    public DateTime? ExitUtc { get; set; }
    public int? ExitCode { get; set; }
    /// <summary>Set when the parent is gone (exited, or its PID now belongs to a newer process).</summary>
    public DateTime? OrphanedUtc { get; set; }
    public bool SeenStartEvent { get; set; }
    /// <summary>Enrichment retries left (a process opened in its ETW start callback may not be queryable yet).</summary>
    internal int EnrichRetries { get; set; } = 2;
    /// <summary>
    /// The create time in <see cref="Key"/> is an estimate (ETW event timestamp; the process could not be opened).
    /// The first snapshot that shows the same PID with an exact create time close to it re-keys the record.
    /// </summary>
    public bool CreateTimeEstimated { get; set; }
    public List<string> Services { get; set; } = [];
    /// <summary>Optional friendly label (Electron role, AICC role).</summary>
    public string? Label { get; set; }

    private Dictionary<string, string>? _context;

    /// <summary>
    /// Values read from the process (its working directory, its session's environment); lock it to use it. Created on
    /// first use: most records, the short commands of a busy PC among them, never need one.
    /// </summary>
    public Dictionary<string, string> Context => LazyInitializer.EnsureInitialized(ref _context);

    /// <summary>A <see cref="Context"/> value, without creating the context to look.</summary>
    public string? ContextValue(string key)
    {
        if (Volatile.Read(ref _context) is not { } context) return null;
        lock (context) return context.GetValueOrDefault(key);
    }

    /// <summary>When the parent exited (the child is an orphan only if it is still running <see cref="ProcessRegistry.OrphanGrace"/> later).</summary>
    public DateTime? ParentExitedUtc { get; set; }

    public bool IsAlive => ExitUtc is null;
    public bool IsOrphan => OrphanedUtc is not null;

    public string DisplayName => (Services.Count > 0 ? $"{Image} ({string.Join(", ", Services)})" : Image) + (Label is { } l ? $" [{l}]" : "");
}

/// <summary>
/// Tracks process instances by (PID, create time) from both the periodic SystemProcessInformation snapshot
/// and the real-time Kernel-Process ETW events, and derives starts, exits, orphaning and ancestry.
/// Thread-safe: ETW callbacks and the sampler call in on different threads.
/// </summary>
public sealed class ProcessRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<ProcessKey, ProcessRecord> _all = new();
    private readonly Dictionary<int, ProcessKey> _livePid = new();
    private readonly TimeSpan _keepExited;
    private readonly int _maxRecords;
    private readonly Queue<LifecycleEvent> _pending = new();
    private bool _initialized;
    private readonly Dictionary<ProcessKey, DateTime> _vanished = new();

    /// <summary>
    /// How long a process may be missing from snapshots before it is marked exited without an ETW stop
    /// (real-time ETW delivery lags by about a second; the stop event carries the exit code).
    /// </summary>
    public TimeSpan ExitGrace { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>Exited processes that lived less than <see cref="EventSummarizer.LongLived"/> (shell pipelines, git calls) are
    /// forgotten after this long: a busy PC starts tens of thousands a day and they were most of PoolWatch's memory.</summary>
    public TimeSpan KeepShortLived { get; set; } = TimeSpan.FromMinutes(15);
    private DateTime _nextPrune = DateTime.MinValue;
    private readonly Dictionary<int, string> _lastImage = new();
    private readonly HashSet<ProcessKey> _orphanCandidates = new();

    /// <summary>
    /// A child counts as an orphan only when it outlives its parent by this much: a shell pipeline whose bash exits a
    /// moment before its last grep is normal, not an orphan.
    /// </summary>
    public static readonly TimeSpan OrphanGrace = TimeSpan.FromSeconds(30);

    /// <summary>Agents that a tool starts and leaves running by design (GnuPG, ssh): "background service", never an orphan.</summary>
    public static readonly IReadOnlySet<string> KnownDaemons = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "scdaemon.exe", "gpg-agent.exe", "ssh-agent.exe", "keyboxd.exe", "dirmngr.exe", "pageant.exe", "gpg-connect-agent.exe", "tpm2daemon.exe" };

    public ProcessRegistry(TimeSpan? keepExited = null, int maxRecords = 20_000)
    {
        _keepExited = keepExited ?? TimeSpan.FromHours(24);
        _maxRecords = maxRecords;
    }

    /// <summary>The image of the latest process that had this PID (live or exited), for labels.</summary>
    public string? RecentImage(int pid) { lock (_lock) return _lastImage.GetValueOrDefault(pid); }

    /// <summary>Called when a process record is created, to fill image path / command line / user (outside the lock).</summary>
    public Action<ProcessRecord>? Enricher { get; set; }

    /// <summary>PIDs that are never treated as orphans (Idle, System, and kernel pseudo-processes).</summary>
    private static bool IsRootPid(int pid) => pid is 0 or 4;

    /// <summary>The record of an instance (a compact start record kept for an incident window comes back as a new record).</summary>
    public ProcessRecord? Get(ProcessKey key) { lock (_lock) return Lookup(key); }

    public ProcessRecord? GetLive(int pid)
    {
        lock (_lock) return _livePid.TryGetValue(pid, out var k) ? _all.GetValueOrDefault(k) : null;
    }

    public List<ProcessRecord> Snapshot() { lock (_lock) return _all.Values.ToList(); }

    /// <summary>Records held (live and remembered exited ones).</summary>
    public int Count { get { lock (_lock) return _all.Count; } }

    public List<ProcessRecord> LiveRecords() { lock (_lock) return _livePid.Values.Select(k => _all[k]).ToList(); }

    /// <summary>Events produced since the last call (starts, exits, orphanings).</summary>
    public List<LifecycleEvent> DrainEvents()
    {
        lock (_lock)
        {
            var list = _pending.ToList();
            _pending.Clear();
            return list;
        }
    }

    /// <summary>Pending events without removing them.</summary>
    public List<LifecycleEvent> PeekEvents() { lock (_lock) return _pending.ToList(); }

    /// <summary>True when a pending (undrained) start is for this image; no copy of the queue.</summary>
    public bool HasPendingStart(string image)
    {
        lock (_lock)
            foreach (var e in _pending)
                if (e.Kind == LifecycleKind.Start && e.Image.Equals(image, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Real-time ETW process start.</summary>
    public ProcessRecord OnStart(int pid, int parentPid, long createTime, string image, string? imagePath, string? commandLine, string? user, DateTime nowUtc,
        bool createTimeEstimated = false)
    {
        var key = new ProcessKey(pid, createTime);
        ProcessRecord? rec;
        lock (_lock) rec = _all.GetValueOrDefault(key);
        bool created = rec is null;
        if (created)
        {
            rec = new ProcessRecord
            {
                Key = key, ParentPid = parentPid, Image = image, FirstSeenUtc = nowUtc, ImagePath = imagePath, CommandLine = commandLine, User = user,
                CreateTimeEstimated = createTimeEstimated,
            };
            Enrich(rec); // outside the lock: opens the process
        }
        lock (_lock)
        {
            if (created)
            {
                if (_all.TryGetValue(key, out var raced))
                {
                    // The sampler added it meanwhile (and queued its Start once initialized): do not report it twice.
                    rec = raced;
                    created = false;
                }
                else
                {
                    if (_livePid.TryGetValue(pid, out var stale) && stale != key && stale.CreateTime < key.CreateTime && _all.TryGetValue(stale, out var staleRec) && staleRec.IsAlive)
                        MarkExited(staleRec, nowUtc, null);
                    ResolveParent(rec!);
                    Add(rec!);
                }
            }
            bool alreadyReported = rec!.SeenStartEvent || (!created && _initialized);
            rec.SeenStartEvent = true;
            rec.ImagePath ??= imagePath;
            rec.CommandLine ??= commandLine;
            rec.User ??= user;
            if (!string.IsNullOrEmpty(image)) rec.Image = image;
            Share(rec);
            if (!alreadyReported) _pending.Enqueue(StartEvent(rec, nowUtc));
        }
        return rec;
    }

    /// <summary>Real-time ETW process stop.</summary>
    public void OnStop(int pid, long createTime, int? exitCode, DateTime exitUtc)
    {
        lock (_lock)
        {
            var key = new ProcessKey(pid, createTime);
            if (!_all.TryGetValue(key, out var rec))
            {
                // Create time from the stop event can differ in precision; fall back to the live PID.
                if (_livePid.TryGetValue(pid, out var k) && (createTime == 0 || Math.Abs(k.CreateTime - createTime) < TimeSpan.TicksPerSecond)) rec = _all[k];
                else return;
            }
            _vanished.Remove(rec.Key);
            MarkExited(rec, exitUtc, exitCode);
        }
    }

    /// <summary>
    /// Reconciles a SystemProcessInformation snapshot: adds unseen processes, marks vanished ones as exited.
    /// </summary>
    public void ObserveSnapshot(IReadOnlyList<ProcessSample> processes, DateTime snapshotUtc)
    {
        // Records whose details could not be read when they started (image not yet mapped): retry a couple of times.
        List<ProcessRecord> retry;
        lock (_lock)
        {
            retry = new List<ProcessRecord>();
            foreach (var p in processes)
                if (_all.TryGetValue(p.Key, out var r) && r.ImagePath is null && r.EnrichRetries > 0 && !IsRootPid(r.Pid)) { r.EnrichRetries--; retry.Add(r); }
        }
        foreach (var r in retry) Enrich(r);

        // Enrich unseen processes first, outside the lock (it opens each process).
        List<ProcessRecord> fresh;
        lock (_lock) fresh = processes.Where(p => !_all.ContainsKey(p.Key))
            .Select(p => new ProcessRecord { Key = p.Key, ParentPid = p.ParentPid, Image = p.ImageName, FirstSeenUtc = snapshotUtc }).ToList();
        foreach (var rec in fresh) Enrich(rec);

        long snapshotFt = snapshotUtc.ToFileTimeUtc();
        lock (_lock)
        {
            var present = new HashSet<ProcessKey>(processes.Select(p => p.Key));
            var added = new List<ProcessRecord>();
            var rekeyed = new HashSet<ProcessKey>();
            foreach (var rec in fresh)
            {
                if (_all.ContainsKey(rec.Key)) continue; // an ETW start raced us
                // The same instance seen by ETW with an estimated create time: adopt its data under the exact key.
                if (_livePid.TryGetValue(rec.Pid, out var est) && est != rec.Key && _all.TryGetValue(est, out var estRec) && estRec.CreateTimeEstimated &&
                    estRec.IsAlive && Math.Abs(est.CreateTime - rec.Key.CreateTime) < 2 * TimeSpan.TicksPerSecond)
                {
                    Rekey(estRec, rec);
                    rekeyed.Add(rec.Key);
                    added.Add(rec);
                    continue;
                }
                // Same PID, older instance still marked alive: it has exited and the PID was reused.
                if (_livePid.TryGetValue(rec.Pid, out var stale) && stale != rec.Key && stale.CreateTime < rec.Key.CreateTime &&
                    _all.TryGetValue(stale, out var staleRec) && staleRec.IsAlive)
                    MarkExited(staleRec, snapshotUtc, null);
                if (_livePid.TryGetValue(rec.Pid, out var newer) && newer.CreateTime > rec.Key.CreateTime)
                {
                    _all[rec.Key] = rec; // a newer instance (from ETW) already owns the PID; keep this one as history
                    continue;
                }
                Add(rec);
                added.Add(rec);
            }
            // Resolve parents after all new records are known, so snapshot order does not matter.
            foreach (var rec in added)
            {
                if (rekeyed.Contains(rec.Key)) continue; // parent and Start event came from ETW
                ResolveParent(rec);
                if (_initialized) _pending.Enqueue(StartEvent(rec, snapshotUtc));
            }
            // Anything live that is missing has exited, unless it was created after the snapshot was taken
            // (an ETW start that raced the query). Wait ExitGrace for the ETW stop, which has the exit code.
            foreach (var key in _livePid.Values.ToList())
            {
                if (present.Contains(key)) { _vanished.Remove(key); continue; }
                if (key.CreateTime >= snapshotFt) continue;
                if (!_vanished.TryGetValue(key, out var since)) _vanished[key] = since = snapshotUtc;
                if (snapshotUtc - since >= ExitGrace)
                {
                    _vanished.Remove(key);
                    MarkExited(_all[key], since, null);
                }
            }
            _initialized = true;
            PromoteOrphans(snapshotUtc);
            Prune(snapshotUtc);
        }
    }

    /// <summary>Parent chain from the root down to (and including) the process. Dead ancestors are included.</summary>
    public List<ProcessRecord> Ancestry(ProcessKey key, int maxDepth = 32)
    {
        lock (_lock)
        {
            var chain = new List<ProcessRecord>();
            var seen = new HashSet<ProcessKey>();
            ProcessKey? cur = key;
            while (cur is { } k && seen.Add(k) && chain.Count < maxDepth && Lookup(k) is { } rec)
            {
                if (IsRootPid(rec.Pid) && chain.Count > 0) break; // System / Idle are not useful ancestors
                chain.Add(rec);
                cur = rec.ParentKey;
            }
            chain.Reverse();
            return chain;
        }
    }

    public void SetServices(Dictionary<int, List<string>> servicesByPid)
    {
        lock (_lock)
        {
            foreach (var key in _livePid.Values)
            {
                var rec = _all[key];
                rec.Services = servicesByPid.TryGetValue(key.Pid, out var s) ? s : [];
            }
        }
    }

    private void ResolveParent(ProcessRecord rec)
    {
        if (IsRootPid(rec.Pid)) return;
        if (_livePid.TryGetValue(rec.ParentPid, out var pk) && pk != rec.Key && pk.CreateTime <= rec.Key.CreateTime)
        {
            rec.ParentKey = pk;
            return;
        }
        // An exited parent we still remember (most recent instance that started before the child).
        ProcessKey? best = null;
        foreach (var k in _all.Keys)
        {
            if (k.Pid == rec.ParentPid && k != rec.Key && k.CreateTime <= rec.Key.CreateTime && (best is null || k.CreateTime > best.Value.CreateTime))
                best = k;
        }
        if (best is { } b && _all[b].ExitUtc is { } exit && exit < rec.Key.CreateTimeUtc) best = null; // exited before the child started: PID reuse
        rec.ParentKey = best;
        if (!IsRootPid(rec.ParentPid) && (best is null || !_all[best.Value].IsAlive))
        {
            // The parent was already gone when we first saw the child: no grace period to wait out.
            if (KnownDaemons.Contains(rec.Image)) rec.Label ??= "background service" + (best is { } bk ? $" started by {_all[bk].Image}" : "");
            else
            {
                // Same 30 s grace as a live exit: a grep first seen just after its bash ended is not an orphan.
                rec.ParentExitedUtc = best is { } dk && _all[dk].ExitUtc is { } pe ? pe : rec.FirstSeenUtc;
                _orphanCandidates.Add(rec.Key);
            }
        }
    }

    /// <summary>Replaces an estimated-key record with the exact-key one, keeping what ETW knew.</summary>
    private void Rekey(ProcessRecord estimated, ProcessRecord exact)
    {
        exact.ParentKey = estimated.ParentKey;
        exact.ParentPid = estimated.ParentPid;
        exact.ImagePath ??= estimated.ImagePath;
        exact.CommandLine ??= estimated.CommandLine;
        exact.User ??= estimated.User;
        exact.OrphanedUtc = estimated.OrphanedUtc;
        exact.ParentExitedUtc = estimated.ParentExitedUtc;
        if (_orphanCandidates.Remove(estimated.Key)) _orphanCandidates.Add(exact.Key);
        exact.SeenStartEvent = estimated.SeenStartEvent;
        exact.FirstSeenUtc = estimated.FirstSeenUtc;
        exact.Services = estimated.Services;
        exact.Label ??= estimated.Label;
        if (!string.IsNullOrEmpty(estimated.Image)) exact.Image = estimated.Image;
        _all.Remove(estimated.Key);
        _vanished.Remove(estimated.Key);
        foreach (var r in _all.Values)
            if (r.ParentKey == estimated.Key) r.ParentKey = exact.Key;
        Add(exact);
    }

    private void Add(ProcessRecord rec)
    {
        _all[rec.Key] = rec;
        _livePid[rec.Pid] = rec.Key;
        if (rec.Image.Length > 0) _lastImage[rec.Pid] = rec.Image;
    }

    private void MarkExited(ProcessRecord rec, DateTime exitUtc, int? exitCode)
    {
        if (rec.ExitUtc is not null) { rec.ExitCode ??= exitCode; return; }
        rec.ExitUtc = exitUtc;
        rec.ExitCode = exitCode;
        if (_livePid.TryGetValue(rec.Pid, out var k) && k == rec.Key) _livePid.Remove(rec.Pid);
        _pending.Enqueue(new LifecycleEvent
        {
            TimeUtc = exitUtc, Kind = LifecycleKind.Exit, Pid = rec.Pid, CreateTime = rec.Key.CreateTime,
            ParentPid = rec.ParentPid, Image = rec.Image, ExitCode = exitCode, CommandLine = rec.CommandLine,
        });
        // Children of this process become orphans if they are still running OrphanGrace from now.
        foreach (var childKey in _livePid.Values)
        {
            var child = _all[childKey];
            if (child.ParentKey != rec.Key || child.OrphanedUtc is not null || child.ParentExitedUtc is not null) continue;
            child.ParentExitedUtc = exitUtc;
            if (KnownDaemons.Contains(child.Image)) child.Label ??= $"background service started by {rec.Image}";
            else _orphanCandidates.Add(childKey);
        }
        _orphanCandidates.Remove(rec.Key);
    }

    /// <summary>Children whose parent exited at least <see cref="OrphanGrace"/> ago and that still run are orphans now.</summary>
    private void PromoteOrphans(DateTime nowUtc)
    {
        if (_orphanCandidates.Count == 0) return;
        foreach (var key in _orphanCandidates.ToList())
        {
            if (!_all.TryGetValue(key, out var child) || child.ParentExitedUtc is not { } pe) { _orphanCandidates.Remove(key); continue; }
            bool outlived = child.ExitUtc is { } x ? x - pe >= OrphanGrace : nowUtc - pe >= OrphanGrace;
            if (!outlived)
            {
                if (child.ExitUtc is not null) _orphanCandidates.Remove(key); // ended with its pipeline: not an orphan
                continue;
            }
            _orphanCandidates.Remove(key);
            child.OrphanedUtc = pe;
            var parent = child.ParentKey is { } pk ? _all.GetValueOrDefault(pk) : null;
            _pending.Enqueue(new LifecycleEvent
            {
                TimeUtc = pe + OrphanGrace, Kind = LifecycleKind.Orphaned, Pid = child.Pid, CreateTime = child.Key.CreateTime,
                ParentPid = child.ParentPid, Image = child.Image, CommandLine = child.CommandLine,
                Detail = $"parent {parent?.Image ?? "?"} ({child.ParentPid}) exited at {Detection.Fmt.Clock(pe)}; still running {(int)OrphanGrace.TotalSeconds} s later",
            });
        }
    }

    private static LifecycleEvent StartEvent(ProcessRecord rec, DateTime nowUtc) => new()
    {
        TimeUtc = rec.Key.CreateTime > 0 ? rec.StartUtc : nowUtc, Kind = LifecycleKind.Start, Pid = rec.Pid,
        CreateTime = rec.Key.CreateTime, ParentPid = rec.ParentPid, Image = rec.Image,
        CommandLine = rec.CommandLine, User = rec.User,
        Detail = rec.SeenStartEvent ? null : "seen in snapshot",
    };

    private void Enrich(ProcessRecord rec)
    {
        try { Enricher?.Invoke(rec); } catch { /* best effort: process may have exited or be protected */ }
        Share(rec);
    }

    /// <summary>Repeated strings (image, path, user, command line) point at one shared copy.</summary>
    private static void Share(ProcessRecord rec)
    {
        rec.Image = Util.StringPool.Intern(rec.Image) ?? "";
        rec.ImagePath = Util.StringPool.Intern(rec.ImagePath);
        rec.CommandLine = Util.StringPool.Intern(rec.CommandLine);
        rec.User = Util.StringPool.Intern(rec.User);
    }

    /// <summary>
    /// Records of processes that exited in [<see cref="ProtectSince"/>, <see cref="ProtectUntil"/>] are not pruned by age:
    /// the incident window plus 10 minutes on each side (2b: sized by time, not by a count). Set by the incident manager
    /// from the open and recently closed incidents; null when nothing needs them. Any thread.
    /// </summary>
    public DateTime? ProtectSince { get; set; }
    /// <summary>The end of the protected window; null while an incident is still open (up to now).</summary>
    public DateTime? ProtectUntil { get; set; }

    /// <summary>
    /// The newest this many protected short-lived processes keep their full record; older ones in the window are kept as a
    /// compact start record (identity, parent, image path, times; no command line, which the trace carries itself), so the
    /// whole window stays resolvable while memory stays small (the soak saw +44 MB/h with full records).
    /// </summary>
    public const int MaxProtectedShortLived = 3000;
    /// <summary>A hard ceiling on compact start records (about 130 bytes each), oldest dropped first.</summary>
    public const int MaxCompact = 100_000;

    /// <summary>A short-lived process kept only so an incident's trace can name it (2b).</summary>
    private sealed record CompactRecord(ProcessKey Key, int ParentPid, ProcessKey? ParentKey, string Image, string? ImagePath, DateTime ExitUtc, int? ExitCode)
    {
        public ProcessRecord ToRecord() => new()
        {
            Key = Key, ParentPid = ParentPid, ParentKey = ParentKey, Image = Image, ImagePath = ImagePath, ExitUtc = ExitUtc, ExitCode = ExitCode,
            FirstSeenUtc = Key.CreateTimeUtc, SeenStartEvent = true,
        };
    }

    private readonly Dictionary<ProcessKey, CompactRecord> _compact = new();

    /// <summary>Compact start records held for incident windows.</summary>
    public int CompactCount { get { lock (_lock) return _compact.Count; } }

    private bool Protected(DateTime exitUtc) => ProtectSince is { } ps && exitUtc >= ps && (ProtectUntil is not { } pu || exitUtc <= pu);

    /// <summary>A full record, else a compact one rebuilt as a record (a new object: changing it changes nothing here).</summary>
    private ProcessRecord? Lookup(ProcessKey key) =>
        _all.TryGetValue(key, out var r) ? r : _compact.TryGetValue(key, out var c) ? c.ToRecord() : null;

    /// <summary>
    /// The record of one process instance: by PID and create time when the create time is known (within a second: the
    /// trace's start timestamp and the kernel's create time differ slightly), else the instance of that PID alive at
    /// <paramref name="atUtc"/>. Null when PoolWatch no longer holds it.
    /// </summary>
    public ProcessRecord? FindInstance(int pid, long createTime, DateTime atUtc)
    {
        lock (_lock)
        {
            if (createTime > 0 && Lookup(new ProcessKey(pid, createTime)) is { } exact) return exact;
            return Pick(_all.Values.Where(r => r.Pid == pid).Concat(_compact.Values.Where(c => c.Key.Pid == pid).Select(c => c.ToRecord())).ToList(), createTime, atUtc);
        }
    }

    /// <summary>The records of these PIDs (full and compact), in one pass (for resolving a whole trace).</summary>
    public Dictionary<int, List<ProcessRecord>> InstancesOf(IEnumerable<int> pids)
    {
        var want = pids.Where(p => p > 4).ToHashSet();
        var d = new Dictionary<int, List<ProcessRecord>>();
        lock (_lock)
        {
            foreach (var r in _all.Values)
                if (want.Contains(r.Pid)) (d.TryGetValue(r.Pid, out var l) ? l : d[r.Pid] = new List<ProcessRecord>()).Add(r);
            foreach (var c in _compact.Values)
                if (want.Contains(c.Key.Pid)) (d.TryGetValue(c.Key.Pid, out var l) ? l : d[c.Key.Pid] = new List<ProcessRecord>()).Add(c.ToRecord());
        }
        return d;
    }

    /// <summary>
    /// Among one PID's records: the one with this create time (within 2 s: the trace's start timestamp and the kernel's
    /// create time differ slightly, and estimated create times differ more), else the one alive at <paramref name="atUtc"/>.
    /// </summary>
    public static ProcessRecord? Pick(List<ProcessRecord>? candidates, long createTime, DateTime atUtc)
    {
        if (candidates is null || candidates.Count == 0) return null;
        if (createTime > 0)
            foreach (var r in candidates)
                if (r.Key.CreateTime > 0 && Math.Abs(r.Key.CreateTime - createTime) < 2 * TimeSpan.TicksPerSecond) return r;
        ProcessRecord? best = null;
        foreach (var r in candidates)
        {
            bool alive = r.StartUtc <= atUtc.AddSeconds(1) && (r.ExitUtc is not { } x || x >= atUtc.AddSeconds(-1));
            if (alive && (best is null || r.StartUtc > best.StartUtc)) best = r;
        }
        return best;
    }

    /// <summary>Compact records outside the protected window are no longer needed; a hard ceiling drops the oldest first.</summary>
    private void PruneCompact()
    {
        if (_compact.Count == 0) return;
        foreach (var k in _compact.Values.Where(c => !Protected(c.ExitUtc)).Select(c => c.Key).ToList()) _compact.Remove(k);
        if (_compact.Count > MaxCompact)
            foreach (var k in _compact.Values.OrderBy(c => c.ExitUtc).Take(_compact.Count - MaxCompact).Select(c => c.Key).ToList()) _compact.Remove(k);
    }

    private DateTime _nextCompactPrune = DateTime.MinValue;

    private void Prune(DateTime nowUtc)
    {
        // The compact records go with their window even while the full registry is small.
        if (_compact.Count > 0 && nowUtc >= _nextCompactPrune) { _nextCompactPrune = nowUtc.AddMinutes(1); PruneCompact(); }
        if (_all.Count <= 256) return;
        if (nowUtc < _nextPrune && _all.Count <= _maxRecords) return; // a full pass once a minute, not every sample
        _nextPrune = nowUtc.AddMinutes(1);
        var cutoff = nowUtc - _keepExited;
        var shortCutoff = nowUtc - KeepShortLived;
        // Every dead ancestor of a live process stays: the whole chain up to claude.exe / AICC is the evidence.
        var keep = new HashSet<ProcessKey>();
        foreach (var k in _livePid.Values)
        {
            var cur = _all.TryGetValue(k, out var lr) ? lr.ParentKey : null;
            for (int d = 0; d < 32 && cur is { } pk && keep.Add(pk) && _all.TryGetValue(pk, out var pr); d++) cur = pr.ParentKey;
        }
        var dead = new List<ProcessKey>();
        var shortLivedKept = new List<ProcessRecord>();
        var protectedShort = new List<ProcessRecord>();
        foreach (var r in _all.Values)
        {
            if (r.ExitUtc is not { } e || keep.Contains(r.Key)) continue;
            bool shortLived = r.Key.CreateTime > 0 && e - r.StartUtc < EventSummarizer.LongLived;
            // Processes that ran inside an incident's window (plus 10 min each side) are its evidence: the trace names them
            // by PID and create time. Kept, by time, until the incident no longer needs them (2b).
            if (Protected(e))
            {
                if (shortLived) protectedShort.Add(r);
                continue;
            }
            if (e < cutoff || (shortLived && e < shortCutoff)) dead.Add(r.Key);
            else if (shortLived) shortLivedKept.Add(r);
        }
        // A long incident on a busy PC would hold every short command it saw as a full record: the newest
        // MaxProtectedShortLived stay full, older ones in the window become compact start records (never dropped for a
        // count: the window is sized by time).
        if (protectedShort.Count > MaxProtectedShortLived)
        {
            protectedShort.Sort((a, b) => b.ExitUtc!.Value.CompareTo(a.ExitUtc!.Value));
            foreach (var r in protectedShort.Skip(MaxProtectedShortLived))
            {
                _compact[r.Key] = new CompactRecord(r.Key, r.ParentPid, r.ParentKey, r.Image, r.ImagePath, r.ExitUtc!.Value, r.ExitCode);
                dead.Add(r.Key);
            }
            protectedShort.RemoveRange(MaxProtectedShortLived, protectedShort.Count - MaxProtectedShortLived);
        }
        shortLivedKept.AddRange(protectedShort);
        foreach (var k in dead) _all.Remove(k);
        PruneCompact();
        if (_all.Count > _maxRecords)
        {
            // Over the cap: evict to 80%, so a busy PC (hundreds of starts per sample) is not over again for several samples
            // (each time over is a full pass), short-lived ones first (those outside an incident window before those inside
            // it), oldest exit first.
            int excess = _all.Count - _maxRecords * 4 / 5;
            var victims = shortLivedKept.OrderBy(r => Protected(r.ExitUtc!.Value)).ThenBy(r => r.ExitUtc)
                .Concat(_all.Values.Where(r => !r.IsAlive && !keep.Contains(r.Key) && !(r.Key.CreateTime > 0 && r.ExitUtc - r.StartUtc < EventSummarizer.LongLived)).OrderBy(r => r.ExitUtc))
                .Take(excess).ToList();
            foreach (var r in victims)
            {
                if (Protected(r.ExitUtc!.Value)) _compact[r.Key] = new CompactRecord(r.Key, r.ParentPid, r.ParentKey, r.Image, r.ImagePath, r.ExitUtc!.Value, r.ExitCode);
                _all.Remove(r.Key);
            }
        }
        if (_lastImage.Count > 2 * _maxRecords)
        {
            _lastImage.Clear();
            foreach (var r in _all.Values) if (r.Image.Length > 0) _lastImage[r.Pid] = r.Image;
        }
    }

}
