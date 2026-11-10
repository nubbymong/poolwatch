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
    /// <summary>
    /// The create time on the clock that only goes forward, read onto it when PoolWatch first saw the process
    /// (<see cref="Moment.MsOf"/>): its lifetime and its start beside a growth are timed on it.
    /// </summary>
    public long StartMs { get; set; }
    public DateTime FirstSeenUtc { get; set; }
    /// <summary>When PoolWatch first saw it, on the clock that only goes forward.</summary>
    public long FirstSeenMs { get; set; }
    public DateTime? ExitUtc { get; set; }
    /// <summary>The exit on the clock that only goes forward, its time read onto it as the exit arrived (<see cref="Moment.MsOf"/>): how long the record is kept is timed on it.</summary>
    public long? ExitMs { get; set; }
    public int? ExitCode { get; set; }
    /// <summary>Set when the parent is gone (exited, or its PID now belongs to a newer process).</summary>
    public DateTime? OrphanedUtc { get; set; }
    /// <summary><see cref="OrphanedUtc"/> on the clock that only goes forward.</summary>
    public long? OrphanedMs { get; set; }
    public bool SeenStartEvent { get; set; }
    /// <summary>
    /// A snapshot listed it (it ran across a sample): only such a process has points in the history, so only it can be a
    /// suspect of the correlation. Most commands of a busy PC (fork copies, sub-second tools) never are.
    /// </summary>
    public bool SeenInSnapshot { get; internal set; }
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
    /// <summary><see cref="ParentExitedUtc"/> on the clock that only goes forward: the <see cref="ProcessRegistry.OrphanGrace"/> is timed from it.</summary>
    public long? ParentExitedMs { get; set; }

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
    /// <summary>Each live process missing from the snapshots, with the snapshot it first went missing at (both clocks).</summary>
    private readonly Dictionary<ProcessKey, Moment> _vanished = new();

    /// <summary>
    /// How long a process may be missing from snapshots before it is marked exited without an ETW stop
    /// (real-time ETW delivery lags by about a second; the stop event carries the exit code). Timed on the clock that only
    /// goes forward.
    /// </summary>
    public TimeSpan ExitGrace { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>Exited processes that lived less than <see cref="EventSummarizer.LongLived"/> (shell pipelines, git calls) are
    /// forgotten after this long: a busy PC starts tens of thousands a day and they were most of PoolWatch's memory.</summary>
    public TimeSpan KeepShortLived { get; set; } = TimeSpan.FromMinutes(15);
    /// <summary>
    /// An exited record kept longer than this (a process that ran 30 s or more is kept a day, for the since-boot report's
    /// names) becomes a named record without its long command line: past the raw history (70 minutes) and the margin no
    /// ranking or release reads it. Not an ancestor of a live process or of one a ranking can name, not inside an incident's
    /// window.
    /// </summary>
    public static readonly TimeSpan ThinAfter = TimeSpan.FromMinutes(90);
    /// <summary>When the next full prune is due, on the forward clock.</summary>
    private long _nextPruneMs = long.MinValue;
    private readonly Func<Moment> _clock;
    private Dictionary<int, string> _lastImage = new();
    private readonly HashSet<ProcessKey> _orphanCandidates = new();

    /// <summary>
    /// A child counts as an orphan only when it outlives its parent by this much: a shell pipeline whose bash exits a
    /// moment before its last grep is normal, not an orphan. Timed on the clock that only goes forward.
    /// </summary>
    public static readonly TimeSpan OrphanGrace = TimeSpan.FromSeconds(30);

    /// <summary>Agents that a tool starts and leaves running by design (GnuPG, ssh): "background service", never an orphan.</summary>
    public static readonly IReadOnlySet<string> KnownDaemons = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "scdaemon.exe", "gpg-agent.exe", "ssh-agent.exe", "keyboxd.exe", "dirmngr.exe", "pageant.exe", "gpg-connect-agent.exe", "tpm2daemon.exe" };

    /// <param name="clock">Reads the two clocks (<see cref="Moment.Now"/>; a test gives its own): how long exited records are
    /// kept, the prunes' minute, <see cref="ExitGrace"/>, <see cref="OrphanGrace"/> and the protected window are timed on its
    /// forward clock, and the times Windows gives are read onto it.</param>
    public ProcessRegistry(TimeSpan? keepExited = null, int maxRecords = 20_000, Func<Moment>? clock = null)
    {
        _clock = clock ?? Moment.Now;
        _keepExited = keepExited ?? TimeSpan.FromHours(24);
        _maxRecords = maxRecords;
    }

    /// <summary>The image of the latest process that had this PID (live or exited), for labels.</summary>
    public string? RecentImage(int pid) { lock (_lock) return _lastImage.GetValueOrDefault(pid); }

    /// <summary>
    /// Each estimated key a snapshot re-keyed (<see cref="Rekey"/>) with the exact key it became, the newest
    /// <see cref="MaxRekeyed"/> kept, so a key copied before the re-key (a child's parent in a spawn record, an open spawn
    /// storm's parent) still names the same process (<see cref="CurrentKey"/>).
    /// </summary>
    private readonly Dictionary<ProcessKey, ProcessKey> _rekeyed = new();
    private readonly Queue<ProcessKey> _rekeyOrder = new();
    public const int MaxRekeyed = 4096;

    /// <summary>How many re-keys there have been: a reader that keeps keys looks them up again only when this changed.</summary>
    public long Rekeys { get { lock (_lock) return _rekeys; } }
    private long _rekeys;

    /// <summary>The key the process instance <paramref name="key"/> names has now: the exact key an estimated one was re-keyed to, else itself.</summary>
    public ProcessKey CurrentKey(ProcessKey key)
    {
        lock (_lock)
        {
            for (int i = 0; i < 4 && _rekeyed.TryGetValue(key, out var next); i++) key = next;
            return key;
        }
    }

    /// <summary>
    /// The process with PID <paramref name="pid"/> that can be the parent of a process created at
    /// <paramref name="childCreateTime"/> (FILETIME; 0 when unknown): the live one, else, when it exited already (a short fork
    /// copy, a child seen late), the newest remembered instance with that PID, as <see cref="ResolveParent"/> takes an
    /// exited parent. Either was created no later than the child, within 2 s (an estimated create time may be a little
    /// late), and a remembered one had not exited before the child was created. Null when there is none, or a newer process
    /// has the PID (the parent exited and its PID was reused).
    /// </summary>
    public ProcessKey? ParentKeyOf(int pid, long childCreateTime)
    {
        lock (_lock)
        {
            if (IsRootPid(pid)) return null;
            long slack = 2 * TimeSpan.TicksPerSecond;
            if (_livePid.TryGetValue(pid, out var k)) return childCreateTime > 0 && k.CreateTime > childCreateTime + slack ? null : k;
            if (!_lastKey.TryGetValue(pid, out var last) || (childCreateTime > 0 && last.CreateTime > childCreateTime + slack)) return null;
            // Exited before the child was created: not its parent (the PID's next process may not have been seen yet). Its
            // record may be full, compact or named (a long-lived process exited hours ago is a named one).
            if (childCreateTime > 0 && ExitUtcOf(last) is { } x && x.ToFileTimeUtc() < childCreateTime - slack) return null;
            return last;
        }
    }

    /// <summary>When the instance exited, by whichever record holds it (null: alive, or not held). Under the lock; no record is built.</summary>
    private DateTime? ExitUtcOf(ProcessKey key) =>
        _all.TryGetValue(key, out var r) ? r.ExitUtc : _compact.TryGetValue(key, out var c) ? c.ExitUtc : _named.TryGetValue(key, out var n) ? n.ExitUtc : null;

    /// <summary>The newest instance seen with each PID (live or exited), for <see cref="ParentKeyOf"/>.</summary>
    private Dictionary<int, ProcessKey> _lastKey = new();

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
            var at = _clock();
            rec = new ProcessRecord
            {
                Key = key, ParentPid = parentPid, Image = image, FirstSeenUtc = nowUtc, FirstSeenMs = at.MsOf(nowUtc), StartMs = at.MsOf(key.CreateTimeUtc),
                ImagePath = imagePath, CommandLine = commandLine, User = user, CreateTimeEstimated = createTimeEstimated,
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
            ShareWithParent(rec);
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
            {
                if (!_all.TryGetValue(p.Key, out var r)) continue;
                r.SeenInSnapshot = true;
                if (r.ImagePath is null && r.EnrichRetries > 0 && !IsRootPid(r.Pid)) { r.EnrichRetries--; retry.Add(r); }
            }
        }
        foreach (var r in retry) Enrich(r);

        // Enrich unseen processes first, outside the lock (it opens each process).
        var at = _clock();
        long snapshotMs = at.MsOf(snapshotUtc);
        List<ProcessRecord> fresh;
        lock (_lock) fresh = processes.Where(p => !_all.ContainsKey(p.Key))
            .Select(p => new ProcessRecord
            {
                Key = p.Key, ParentPid = p.ParentPid, Image = p.ImageName, FirstSeenUtc = snapshotUtc, FirstSeenMs = snapshotMs, StartMs = at.MsOf(p.Key.CreateTimeUtc),
                SeenInSnapshot = true,
            }).ToList();
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
                ShareWithParent(rec);
                if (_initialized) _pending.Enqueue(StartEvent(rec, snapshotUtc));
            }
            // Anything live that is missing has exited, unless it was created after the snapshot was taken
            // (an ETW start that raced the query). Wait ExitGrace for the ETW stop, which has the exit code; the wait is
            // timed on the clock that only goes forward.
            foreach (var key in _livePid.Values.ToList())
            {
                if (present.Contains(key)) { _vanished.Remove(key); continue; }
                if (key.CreateTime >= snapshotFt) continue;
                if (!_vanished.TryGetValue(key, out var since)) _vanished[key] = since = new Moment(snapshotUtc, snapshotMs);
                if (snapshotMs - since.Ms >= (long)ExitGrace.TotalMilliseconds)
                {
                    _vanished.Remove(key);
                    MarkExited(_all[key], since.Utc, null, since.Ms);
                }
            }
            _initialized = true;
            PromoteOrphans(snapshotMs);
            Prune();
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
        // Exited before the child started (on the clock that only goes forward): PID reuse.
        if (best is { } b && _all[b].ExitMs is { } exit && exit < rec.StartMs) best = null;
        rec.ParentKey = best;
        if (!IsRootPid(rec.ParentPid) && (best is null || !_all[best.Value].IsAlive))
        {
            // The parent was already gone when we first saw the child: no grace period to wait out.
            if (KnownDaemons.Contains(rec.Image)) rec.Label ??= "background service" + (best is { } bk ? $" started by {_all[bk].Image}" : "");
            else
            {
                // Same 30 s grace as a live exit: a grep first seen just after its bash ended is not an orphan.
                (rec.ParentExitedUtc, rec.ParentExitedMs) = best is { } dk && _all[dk] is { ExitUtc: { } pe, ExitMs: { } peMs }
                    ? (pe, peMs) : (rec.FirstSeenUtc, rec.FirstSeenMs);
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
        (exact.OrphanedUtc, exact.OrphanedMs) = (estimated.OrphanedUtc, estimated.OrphanedMs);
        (exact.ParentExitedUtc, exact.ParentExitedMs) = (estimated.ParentExitedUtc, estimated.ParentExitedMs);
        if (_orphanCandidates.Remove(estimated.Key)) _orphanCandidates.Add(exact.Key);
        exact.SeenStartEvent = estimated.SeenStartEvent;
        (exact.FirstSeenUtc, exact.FirstSeenMs) = (estimated.FirstSeenUtc, estimated.FirstSeenMs);
        exact.Services = estimated.Services;
        exact.Label ??= estimated.Label;
        if (!string.IsNullOrEmpty(estimated.Image)) exact.Image = estimated.Image;
        _all.Remove(estimated.Key);
        _vanished.Remove(estimated.Key);
        foreach (var r in _all.Values)
            if (r.ParentKey == estimated.Key) r.ParentKey = exact.Key;
        Add(exact);
        if (_lastKey.TryGetValue(exact.Pid, out var last) && last == estimated.Key) _lastKey[exact.Pid] = exact.Key;
        // Keys copied before now (a spawn record's parent) still find it.
        _rekeyed[estimated.Key] = exact.Key;
        _rekeyOrder.Enqueue(estimated.Key);
        while (_rekeyOrder.Count > MaxRekeyed) _rekeyed.Remove(_rekeyOrder.Dequeue());
        _rekeys++;
    }

    private void Add(ProcessRecord rec)
    {
        _all[rec.Key] = rec;
        _livePid[rec.Pid] = rec.Key;
        if (rec.Image.Length > 0) _lastImage[rec.Pid] = rec.Image;
        if (!_lastKey.TryGetValue(rec.Pid, out var was) || was.CreateTime <= rec.Key.CreateTime || was == rec.Key) _lastKey[rec.Pid] = rec.Key;
    }

    /// <param name="exitMs">The exit on the clock that only goes forward when it is known (the snapshot it went missing at);
    /// else <paramref name="exitUtc"/> is read onto it now.</param>
    private void MarkExited(ProcessRecord rec, DateTime exitUtc, int? exitCode, long? exitMs = null)
    {
        if (rec.ExitUtc is not null) { rec.ExitCode ??= exitCode; return; }
        rec.ExitUtc = exitUtc;
        rec.ExitMs = exitMs ?? _clock().MsOf(exitUtc);
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
            (child.ParentExitedUtc, child.ParentExitedMs) = (exitUtc, rec.ExitMs);
            if (KnownDaemons.Contains(child.Image)) child.Label ??= $"background service started by {rec.Image}";
            else _orphanCandidates.Add(childKey);
        }
        _orphanCandidates.Remove(rec.Key);
    }

    /// <summary>
    /// Children whose parent exited at least <see cref="OrphanGrace"/> ago and that still run are orphans now, timed on the
    /// clock that only goes forward (<paramref name="nowMs"/>, the parent's and the child's exits as the registry read them).
    /// </summary>
    private void PromoteOrphans(long nowMs)
    {
        if (_orphanCandidates.Count == 0) return;
        long grace = (long)OrphanGrace.TotalMilliseconds;
        foreach (var key in _orphanCandidates.ToList())
        {
            if (!_all.TryGetValue(key, out var child) || child.ParentExitedUtc is not { } pe || child.ParentExitedMs is not { } peMs)
            {
                _orphanCandidates.Remove(key);
                continue;
            }
            bool outlived = child.ExitUtc is not null ? child.ExitMs - peMs >= grace : nowMs - peMs >= grace;
            if (!outlived)
            {
                if (child.ExitUtc is not null) _orphanCandidates.Remove(key); // ended with its pipeline: not an orphan
                continue;
            }
            _orphanCandidates.Remove(key);
            (child.OrphanedUtc, child.OrphanedMs) = (pe, peMs);
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

    /// <summary>
    /// A fork copy (Git Bash runs a subshell or a pipeline stage as a copy of the shell, started with its exact command line)
    /// holds its parent's command line, not one of its own: an agent's shell script is often a kilobyte, too long for the
    /// string pool, and each command makes two or three copies. Under the lock.
    /// </summary>
    private void ShareWithParent(ProcessRecord rec)
    {
        if (rec.CommandLine is { Length: > Util.StringPool.MaxLength } cl && rec.ParentKey is { } pk && _all.TryGetValue(pk, out var parent) &&
            parent.CommandLine is { } pcl && !ReferenceEquals(cl, pcl) && string.Equals(cl, pcl, StringComparison.Ordinal))
            rec.CommandLine = pcl;
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
    /// Records of processes that exited in [<see cref="ProtectSinceMs"/>, <see cref="ProtectUntilMs"/>] on the clock that
    /// only goes forward (each record's <see cref="ProcessRecord.ExitMs"/>) are not pruned by age: the incident window plus
    /// 10 minutes on each side (Sized by time, not by a count). Set by the incident manager from the open and recently
    /// closed incidents; null when nothing needs them. Any thread.
    /// </summary>
    public long? ProtectSinceMs { get; set; }
    /// <summary>The end of the protected window on the forward clock; null while an incident is still open (up to now).</summary>
    public long? ProtectUntilMs { get; set; }

    /// <summary>
    /// The newest this many protected short-lived processes keep their full record; older ones in the window are kept as a
    /// compact start record (identity, parent, image path, times; no command line, which the trace carries itself), so the
    /// whole window stays resolvable while memory stays small (the soak saw +44 MB/h with full records).
    /// </summary>
    public const int MaxProtectedShortLived = 3000;
    /// <summary>A hard ceiling on compact start records (about 130 bytes each), oldest dropped first.</summary>
    public const int MaxCompact = 100_000;

    /// <summary>
    /// A short-lived process kept only so an incident's ranking or trace can name it (<see cref="Of"/>: no command line, the
    /// trace carries it), or an exited one kept for its name past the raw history (<see cref="Named"/>).
    /// </summary>
    private sealed record CompactRecord(ProcessKey Key, int ParentPid, ProcessKey? ParentKey, string Image, string? ImagePath, DateTime ExitUtc, long ExitMs,
        long StartMs, int? ExitCode, bool Seen)
    {
        public string? CommandLine { get; init; }
        public string? User { get; init; }
        /// <summary>Its own copy (the live record's list is replaced, never shared); each record built from it gets a new list.</summary>
        public string[]? Services { get; init; }
        public string? Label { get; init; }
        public DateTime? OrphanedUtc { get; init; }
        public long? OrphanedMs { get; init; }

        public static CompactRecord Of(ProcessRecord r) =>
            new(r.Key, r.ParentPid, r.ParentKey, r.Image, r.ImagePath, r.ExitUtc!.Value, r.ExitMs ?? r.StartMs, r.StartMs, r.ExitCode, r.SeenInSnapshot);

        /// <summary>Everything but a command line too long for the string pool (an agent's script; events.csv has it).</summary>
        public static CompactRecord Named(ProcessRecord r) => Of(r) with
        {
            CommandLine = r.CommandLine is { Length: <= Util.StringPool.MaxLength } cl ? cl : null, User = r.User,
            Services = r.Services.Count > 0 ? [.. r.Services] : null, Label = r.Label, OrphanedUtc = r.OrphanedUtc, OrphanedMs = r.OrphanedMs,
        };

        public ProcessRecord ToRecord() => new()
        {
            Key = Key, ParentPid = ParentPid, ParentKey = ParentKey, Image = Image, ImagePath = ImagePath, ExitUtc = ExitUtc, ExitMs = ExitMs, ExitCode = ExitCode,
            FirstSeenUtc = Key.CreateTimeUtc, FirstSeenMs = StartMs, StartMs = StartMs, SeenStartEvent = true, SeenInSnapshot = Seen,
            CommandLine = CommandLine, User = User, Services = Services is { } s ? [.. s] : [], Label = Label, OrphanedUtc = OrphanedUtc, OrphanedMs = OrphanedMs,
        };
    }

    /// <summary>
    /// Exited processes kept for their names past <see cref="ThinAfter"/> (<see cref="CompactRecord.Named"/>), for the day the
    /// registry keeps exited records, at most <see cref="MaxNamed"/>, in the order they were named (<see cref="_namedOrder"/>):
    /// the first named goes first.
    /// </summary>
    private readonly Dictionary<ProcessKey, CompactRecord> _named = new();
    /// <summary>The named records' keys in the order they were named (a key named again is in it twice; the first is passed over).</summary>
    private readonly Queue<ProcessKey> _namedOrder = new();
    /// <summary>
    /// A ceiling on named records (about 190 bytes each with their place in the dictionary): on the maintainer's PC under
    /// agentic load (about 2,800 processes of 30 s or more ending an hour) the 3 to 4 hours after the 90 minutes the full
    /// records keep, more on a quieter PC. What reads them later than that: the since-boot report's names, which it takes
    /// as each hour's processes are summed (<see cref="History.History.NameOf"/>), 2 to 3 hours after they ran.
    /// </summary>
    public const int MaxNamed = 10_000;
    /// <summary>Named records held (exited past the raw history, kept for their names).</summary>
    public int NamedCount { get { lock (_lock) return _named.Count; } }

    /// <summary>
    /// A confirmation trace is recording or being parsed (set by the incident manager): every short-lived process of the
    /// protected window is kept as a compact record, since the trace may name any of them. Otherwise only those that can be
    /// named by an incident: the processes a snapshot listed (the suspects of the correlation) and their ancestors (who
    /// they worked for); the rest (fork copies, sub-second tools never sampled) are in no ranking and no trace to come.
    /// </summary>
    public bool ProtectAll { get; set; }

    /// <summary>
    /// The ancestors of every record a ranking can name: one a snapshot listed (<see cref="ProcessRecord.SeenInSnapshot"/>)
    /// that is alive or exited within <see cref="ThinAfter"/> (the raw history and its margin), full or compact; the chains
    /// a ranking names their owners by. Under the lock.
    /// </summary>
    private HashSet<ProcessKey> AncestorsOfSeen()
    {
        long sinceMs = _clock().Ms - (long)ThinAfter.TotalMilliseconds;
        var keep = new HashSet<ProcessKey>();
        void Walk(ProcessKey? cur)
        {
            for (int d = 0; d < 32 && cur is { } k && keep.Add(k); d++)
                cur = _all.TryGetValue(k, out var r) ? r.ParentKey : _compact.TryGetValue(k, out var c) ? c.ParentKey : _named.TryGetValue(k, out var n) ? n.ParentKey : null;
        }
        foreach (var r in _all.Values) if (r.SeenInSnapshot && (r.ExitMs is not { } x || x >= sinceMs)) Walk(r.ParentKey);
        foreach (var c in _compact.Values) if (c.Seen && c.ExitMs >= sinceMs) Walk(c.ParentKey);
        return keep;
    }

    /// <summary>Whether a short-lived record about to leave the full registry is kept as a compact one (<see cref="ProtectAll"/>).</summary>
    private bool KeepCompact(ProcessRecord r, ref HashSet<ProcessKey>? ancestors) =>
        ProtectAll || r.SeenInSnapshot || (ancestors ??= AncestorsOfSeen()).Contains(r.Key);

    private readonly Dictionary<ProcessKey, CompactRecord> _compact = new();

    /// <summary>Compact start records held for incident windows.</summary>
    public int CompactCount { get { lock (_lock) return _compact.Count; } }

    private bool Protected(long exitMs) => ProtectSinceMs is { } ps && exitMs >= ps && (ProtectUntilMs is not { } pu || exitMs <= pu);

    /// <summary>A full record, else a compact or named one rebuilt as a record (a new object: changing it changes nothing here).</summary>
    private ProcessRecord? Lookup(ProcessKey key) =>
        _all.TryGetValue(key, out var r) ? r : _compact.TryGetValue(key, out var c) ? c.ToRecord() : _named.TryGetValue(key, out var n) ? n.ToRecord() : null;

    /// <summary>
    /// The record of one process instance: by PID and create time when the create time is known (exactly, else within 2 s:
    /// the trace's start timestamp and the kernel's create time differ slightly), else the instance of that PID alive at
    /// <paramref name="atUtc"/>, read onto the clock that only goes forward now (<see cref="Moment.MsOf"/>, as
    /// <see cref="Pick"/> compares). Null when PoolWatch no longer holds it.
    /// </summary>
    public ProcessRecord? FindInstance(int pid, long createTime, DateTime atUtc)
    {
        lock (_lock)
        {
            if (createTime > 0 && Lookup(new ProcessKey(pid, createTime)) is { } exact) return exact;
            return Pick(_all.Values.Where(r => r.Pid == pid).Concat(_compact.Values.Where(c => c.Key.Pid == pid).Select(c => c.ToRecord())).ToList(), createTime,
                _clock().MsOf(atUtc));
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
    /// create time differ slightly, and estimated create times differ more), else the one alive at <paramref name="atMs"/>,
    /// within a second, on the clock that only goes forward (each record's <see cref="ProcessRecord.StartMs"/> and
    /// <see cref="ProcessRecord.ExitMs"/>), the latest started first: a system clock set back or forward between a process's
    /// start or exit and that moment does not pick another instance of its PID.
    /// </summary>
    public static ProcessRecord? Pick(List<ProcessRecord>? candidates, long createTime, long atMs)
    {
        if (candidates is null || candidates.Count == 0) return null;
        if (createTime > 0)
            foreach (var r in candidates)
                if (r.Key.CreateTime > 0 && Math.Abs(r.Key.CreateTime - createTime) < 2 * TimeSpan.TicksPerSecond) return r;
        ProcessRecord? best = null;
        foreach (var r in candidates)
        {
            bool alive = r.StartMs <= atMs + 1000 && (r.ExitMs is not { } x || x >= atMs - 1000);
            if (alive && (best is null || r.StartMs > best.StartMs)) best = r;
        }
        return best;
    }

    /// <summary>The record leaves the full registry as a named one (<see cref="CompactRecord.Named"/>). Under the lock.</summary>
    private void Name(ProcessRecord r)
    {
        _named[r.Key] = CompactRecord.Named(r);
        _namedOrder.Enqueue(r.Key);
    }

    /// <summary>
    /// Named records go, the first named first, once the day exited records are kept is over for the first in line, and while
    /// there are more than <see cref="MaxNamed"/>: no pass over all of them (once at the ceiling, a sort of 20,000 every minute
    /// was most of a day's allocations here).
    /// </summary>
    private void PruneNamed(long cutoffMs)
    {
        while (_namedOrder.TryPeek(out var k))
        {
            if (!_named.TryGetValue(k, out var n)) { _namedOrder.Dequeue(); continue; } // gone already (a key named twice)
            if (n.ExitMs >= cutoffMs && _named.Count <= MaxNamed) break;
            _namedOrder.Dequeue();
            _named.Remove(k);
        }
        // A key named twice (a late start event for an exited process brought it back) is in line twice and goes at its first
        // place; the line never holds more than twice the records.
        if (_namedOrder.Count > 2 * (_named.Count + 64))
        {
            var order = _namedOrder.Where(_named.ContainsKey).Distinct().ToArray();
            _namedOrder.Clear();
            foreach (var k in order) _namedOrder.Enqueue(k);
        }
    }

    /// <summary>Compact records outside the protected window are no longer needed; a hard ceiling drops the oldest first.</summary>
    private void PruneCompact()
    {
        if (_compact.Count == 0) return;
        foreach (var k in _compact.Values.Where(c => !Protected(c.ExitMs)).Select(c => c.Key).ToList()) _compact.Remove(k);
        if (_compact.Count > MaxCompact)
            foreach (var k in _compact.Values.OrderBy(c => c.ExitMs).Take(_compact.Count - MaxCompact).Select(c => c.Key).ToList()) _compact.Remove(k);
    }

    private long _nextCompactPruneMs = long.MinValue;

    /// <summary>
    /// Drops exited records no longer needed. The minute between full passes and how long an exited record is kept are timed
    /// on the clock that only goes forward (each record's <see cref="ProcessRecord.ExitMs"/>), so a system clock set back
    /// neither keeps records past their time nor, set forward, drops them early.
    /// </summary>
    private void Prune()
    {
        long nowMs = _clock().Ms;
        // The compact records go with their window even while the full registry is small.
        if (_compact.Count > 0 && nowMs >= _nextCompactPruneMs) { _nextCompactPruneMs = nowMs + 60_000; PruneCompact(); }
        if (_all.Count <= 256) return;
        if (nowMs < _nextPruneMs && _all.Count <= _maxRecords) return; // a full pass once a minute, not every sample
        _nextPruneMs = nowMs + 60_000;
        long cutoff = nowMs - (long)_keepExited.TotalMilliseconds;
        long shortCutoff = nowMs - (long)KeepShortLived.TotalMilliseconds;
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
        long thinCutoff = nowMs - (long)ThinAfter.TotalMilliseconds;
        HashSet<ProcessKey>? ancestors = null;
        foreach (var r in _all.Values)
        {
            if (r.ExitUtc is null || keep.Contains(r.Key)) continue;
            long exitMs = r.ExitMs ?? nowMs;
            bool shortLived = r.Key.CreateTime > 0 && exitMs - r.StartMs < (long)EventSummarizer.LongLived.TotalMilliseconds;
            // Processes that ran inside an incident's window (plus 10 min each side) are its evidence: the trace names them
            // by PID and create time. Kept, by time, until the incident no longer needs them.
            if (Protected(exitMs))
            {
                if (shortLived) protectedShort.Add(r);
                continue;
            }
            if (exitMs < cutoff || (shortLived && exitMs < shortCutoff)) dead.Add(r.Key);
            else if (shortLived) shortLivedKept.Add(r);
            // Past the raw history no ranking or release reads the record any more: it is kept as a named record (name,
            // path, services, label, parent and times: the since-boot report's names, an ancestor's place in a chain, its
            // command line only when short), for the rest of its day. Not an ancestor of anything a ranking can name.
            else if (exitMs < thinCutoff && !(ancestors ??= AncestorsOfSeen()).Contains(r.Key))
            {
                Name(r);
                dead.Add(r.Key);
            }
        }
        // A long incident on a busy PC would hold every short command it saw as a full record: the newest
        // MaxProtectedShortLived stay full, older ones in the window become compact start records (never dropped for a
        // count: the window is sized by time), those an incident can name (KeepCompact).
        if (protectedShort.Count > MaxProtectedShortLived)
        {
            protectedShort.Sort((a, b) => (b.ExitMs ?? nowMs).CompareTo(a.ExitMs ?? nowMs));
            foreach (var r in protectedShort.Skip(MaxProtectedShortLived))
            {
                if (KeepCompact(r, ref ancestors)) _compact[r.Key] = CompactRecord.Of(r);
                dead.Add(r.Key);
            }
            protectedShort.RemoveRange(MaxProtectedShortLived, protectedShort.Count - MaxProtectedShortLived);
        }
        shortLivedKept.AddRange(protectedShort);
        foreach (var k in dead) _all.Remove(k);
        PruneCompact();
        PruneNamed(cutoff);
        if (_all.Count > _maxRecords)
        {
            // Over the cap: evict to 80%, so a busy PC (hundreds of starts per sample) is not over again for several samples
            // (each time over is a full pass), short-lived ones first (those outside an incident window before those inside
            // it, those no snapshot listed before those a ranking can name), oldest exit first.
            int excess = _all.Count - _maxRecords * 4 / 5;
            var victims = shortLivedKept.OrderBy(r => Protected(r.ExitMs ?? nowMs)).ThenBy(r => r.SeenInSnapshot).ThenBy(r => r.ExitMs)
                .Concat(_all.Values.Where(r => !r.IsAlive && !keep.Contains(r.Key) && !(r.Key.CreateTime > 0 && (r.ExitMs ?? nowMs) - r.StartMs < (long)EventSummarizer.LongLived.TotalMilliseconds)).OrderBy(r => r.ExitMs))
                .Take(excess).ToList();
            foreach (var r in victims)
            {
                bool shortLived = r.Key.CreateTime > 0 && (r.ExitMs ?? nowMs) - r.StartMs < (long)EventSummarizer.LongLived.TotalMilliseconds;
                if (Protected(r.ExitMs ?? nowMs) && KeepCompact(r, ref ancestors)) _compact[r.Key] = CompactRecord.Of(r);
                else if (!shortLived) Name(r); // its name for the rest of its day
                _all.Remove(r.Key);
            }
        }
        // Each PID's newest instance and image are for the PIDs of the records held: once an eighth more PIDs are listed (each
        // start of a busy PC brings a new PID, 3,600 a minute), they are made again from the records held, into new
        // dictionaries with room for that many (a cleared one keeps its size: before, on a busy PC 40,000 PIDs' room each,
        // about 4.6 MB, for good). A pass over the 30,000 records held, about every minute there.
        int held = _all.Count + _compact.Count + _named.Count, room = held + held / 8 + 1024;
        if (_lastKey.Count > room || _lastImage.Count > room) RebuildLastByPid(room);
    }

    /// <summary>Each PID's newest instance held (full, compact or named; a live one first) and the image of it. Under the lock.</summary>
    private void RebuildLastByPid(int room)
    {
        var lastKey = new Dictionary<int, ProcessKey>(room);
        var lastImage = new Dictionary<int, string>(room);
        void Offer(ProcessKey k, string image)
        {
            if (lastKey.TryGetValue(k.Pid, out var was) && was.CreateTime >= k.CreateTime) return;
            lastKey[k.Pid] = k;
            if (image.Length > 0) lastImage[k.Pid] = image;
            else if (_lastImage.TryGetValue(k.Pid, out var before)) lastImage[k.Pid] = before;
        }
        foreach (var n in _named.Values) Offer(n.Key, n.Image);
        foreach (var c in _compact.Values) Offer(c.Key, c.Image);
        foreach (var r in _all.Values) Offer(r.Key, r.Image);
        foreach (var (pid, k) in _livePid)
        {
            lastKey[pid] = k;
            if (_all.TryGetValue(k, out var live) && live.Image.Length > 0) lastImage[pid] = live.Image;
        }
        (_lastKey, _lastImage) = (lastKey, lastImage);
    }

}
