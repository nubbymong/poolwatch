using System.Globalization;
using System.Text;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Identity;
using PoolWatch.Core.Model;

namespace PoolWatch.Core.Spawn;

/// <summary>One child process start as seen by the spawn tracker.</summary>
public sealed class SpawnRecord
{
    public required ProcessKey Child { get; init; }
    public ProcessKey? Parent { get; init; }
    public int ParentPid { get; init; }
    public required string Image { get; init; }
    public string? CommandLine { get; init; }
    public DateTime StartUtc { get; init; }
    /// <summary>The start on the clock that only goes forward: the registry's (<see cref="ProcessRecord.StartMs"/>, read as it
    /// arrived), else read onto it at the tick that brought its event (<see cref="Moment.MsOf"/>).</summary>
    public long StartMs { get; init; }
    public DateTime? ExitUtc { get; set; }
    /// <summary>The exit on the clock that only goes forward: the registry's (<see cref="ProcessRecord.ExitMs"/>, read as it
    /// arrived), else read onto it at the tick that brought its event; never before the start; null while it runs.</summary>
    public long? ExitMs { get; set; }
    public int? ExitCode { get; set; }
    /// <summary>For git.exe: the directory it worked in and where that came from ("git -C", "cwd", "parent cwd"; none starts with a
    /// character a spreadsheet reads as a formula, so spawns.csv holds them as they are).</summary>
    public string? WorkDir { get; set; }
    public string? WorkDirSource { get; set; }
    public string? GitRoot { get; set; }
    /// <summary>
    /// The parent this start counts to: the process that started it, or, for a start by a fork copy, the process the copy
    /// was forked from (<see cref="SpawnTracker.IsForkCopy"/>), by its key now (a re-key followed).
    /// </summary>
    public ProcessKey StormParent { get; set; }
    /// <summary>A fork copy of its parent (the parent's own image and exact command line): not a launch, never counted.</summary>
    public bool ForkCopy { get; init; }
    /// <summary>The registry's key for it when that differs from <see cref="Child"/> (the event's key was re-keyed): a fork copy's origin is kept under both.</summary>
    public ProcessKey? RegistryKey { get; init; }

    /// <summary>How long it ran, on the clock that only goes forward; null while it runs.</summary>
    public TimeSpan? Lifetime => ExitMs is { } e ? TimeSpan.FromMilliseconds(Math.Max(0, e - StartMs)) : null;
    public bool ShortLived(TimeSpan limit) => Lifetime is { } l && l <= limit;
}

/// <summary>A parent that launched more short-lived children than the threshold in one window, and what it launched.</summary>
public sealed class SpawnStorm
{
    public const int MaxKeys = 500;
    /// <summary>The parent's key now (moved when a snapshot re-keys an estimated key).</summary>
    public required ProcessKey ParentKey { get; set; }
    public required int ParentPid { get; init; }
    public required string ParentImage { get; init; }
    public string? ParentCommandLine { get; init; }
    public string ParentChain { get; init; } = "";
    /// <summary>The parent's identity (image, role, owner: <see cref="ProcessIdentities.Of"/>): the key its report folder is shared by.</summary>
    public string Identity { get; init; } = "";
    /// <summary>That identity as shown ("bash.exe under Claude Code session x (PID 12)").</summary>
    public string IdentityName { get; init; } = "";
    public DateTime OpenedUtc { get; init; }
    /// <summary>When it opened, on the clock that only goes forward.</summary>
    public long OpenedMs { get; init; }
    /// <summary>When its short-lived starts were last at half the threshold or more (<see cref="Moment.Ms"/>).</summary>
    public long LastAboveMs { get; set; }
    public DateTime? ClosedUtc { get; set; }
    /// <summary>When it closed, on the clock that only goes forward; null while open.</summary>
    public long? ClosedMs { get; set; }
    public int PeakShortLived { get; set; }
    public int PeakStarts { get; set; }
    public int TotalStarts { get; private set; }
    /// <summary>Fork copies of the parent in its window and while it was open: not counted as starts (what they started is).</summary>
    public int ForkCopies { get; set; }
    public Dictionary<string, int> ByImage { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Peak starts per child image within one window.</summary>
    public Dictionary<string, int> PeakByImage { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> Patterns { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, (GitRootInfo Info, int Count)> GitRoots { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The report folder it is written to (shared with its identity's other storms); null: none, past the hourly limit or not wanted.</summary>
    public StormFolder? Folder { get; set; }
    /// <summary>It got no folder because none was wanted as it opened (<see cref="SpawnTracker.FoldersWanted"/>: Reports only).</summary>
    public bool FolderNotWanted { get; set; }
    /// <summary>The most recent finding about this storm (gets the report folder once it is written).</summary>
    public Finding? LastFinding { get; set; }

    public bool IsOpen => ClosedUtc is null;

    internal void Add(SpawnRecord r, GitRootInfo? root)
    {
        TotalStarts++;
        Bump(ByImage, r.Image);
        Bump(Patterns, CommandLineTokens.Pattern(r.Image, r.CommandLine));
        if (root is not null && (GitRoots.ContainsKey(root.Root) || GitRoots.Count < MaxKeys))
            GitRoots[root.Root] = (root, GitRoots.TryGetValue(root.Root, out var g) ? g.Count + 1 : 1);
        Folder?.Took(r); // its starts for spawns.csv are its report folder's

    }

    /// <summary>"48 git.exe, 3 sh.exe": its busiest child images.</summary>
    public string TopImages(int n = 3) => string.Join(", ", ByImage.OrderByDescending(kv => kv.Value).Take(n).Select(kv => $"{kv.Value} {kv.Key}"));

    /// <summary>Counts a key; past <see cref="MaxKeys"/> distinct keys new ones go to "(other)" so a storm that never ends stays bounded.</summary>
    private static void Bump(Dictionary<string, int> d, string key)
    {
        if (!d.ContainsKey(key) && d.Count >= MaxKeys) key = "(other)";
        d[key] = d.GetValueOrDefault(key) + 1;
    }
}

/// <summary>One storm of a report folder, as its folder keeps it once it closed.</summary>
public sealed record StormEpisode(DateTime OpenedUtc, DateTime? ClosedUtc, int ParentPid, string ParentImage, int TotalStarts, int PeakShortLived, int ForkCopies, string Top)
{
    public static StormEpisode Of(SpawnStorm s) => new(s.OpenedUtc, s.ClosedUtc, s.ParentPid, s.ParentImage, s.TotalStarts, s.PeakShortLived, s.ForkCopies, s.TopImages());
}

/// <summary>
/// The report folder one parent identity's spawn storms share, as a growth incident's re-openings share its folder: a storm
/// that opens while another of its identity (<see cref="SpawnStorm.Identity"/>) is open, or within
/// <see cref="SpawnTracker.ReopenWindow"/> of the last one closing, is written into the same folder; after that a new one
/// is made, at most <see cref="SpawnTracker.MaxNewFoldersPerHour"/> an hour. It holds its open storms whole and the closed
/// ones as one line each (<see cref="StormEpisode"/>), and the first <see cref="MaxTrace"/> starts for spawns.csv, each
/// appended once its process exited. Sampler thread only.
/// </summary>
public sealed class StormFolder
{
    public const int MaxEpisodes = 200, MaxTrace = 1000;
    public required string Identity { get; init; }
    public required string Name { get; init; }
    /// <summary>What the folder's name ends with: the first parent's image without its extension.</summary>
    public required string Subject { get; init; }
    /// <summary>The folder, once it was first written (<see cref="StormReports.Write"/>); null before.</summary>
    public string? Dir { get; set; }
    private readonly List<SpawnStorm> _open = new();
    /// <summary>The storms open in it now.</summary>
    public IReadOnlyList<SpawnStorm> Open => _open;
    /// <summary>The closed storms, the newest <see cref="MaxEpisodes"/>, first opened first.</summary>
    public List<StormEpisode> Closed { get; } = new();
    /// <summary>Closed storms no longer listed (past <see cref="MaxEpisodes"/>), and their starts.</summary>
    public int DroppedEpisodes { get; private set; }
    public int StormCount { get; private set; }
    /// <summary>The storm that opened last: the report's detail is its.</summary>
    public SpawnStorm Latest { get; private set; } = null!;
    public DateTime FirstOpenedUtc { get; private set; }
    /// <summary>When a storm in it was last open, on the clock that only goes forward.</summary>
    public long LastActiveMs { get; private set; }
    /// <summary>When it was last written, on the clock that only goes forward; null: never.</summary>
    public long? LastWriteMs { get; set; }
    /// <summary>Its numbers changed since it was last written (a start counted, a storm closed).</summary>
    public bool Changed { get; set; }
    /// <summary>A storm joined it or closed since it was last written.</summary>
    public bool EpisodesChanged { get; set; }
    /// <summary>It leaves memory after this write (quiet for the whole re-open window, or PoolWatch stopping): written whatever the budget.</summary>
    public bool Final { get; set; }
    private long _closedStarts;
    private readonly List<SpawnRecord> _pending = new();
    private int _traceTaken;
    /// <summary>Rows in its spawns.csv.</summary>
    public int TraceRows { get; set; }
    /// <summary>The starts held for spawns.csv and not written yet (those still running among them: they are written once they exit, or on its last write).</summary>
    public IReadOnlyList<SpawnRecord> PendingRows => _pending;
    /// <summary>It holds starts not in its spawns.csv yet: its last write must still be made, whatever else changed.</summary>
    public bool HasPending => _pending.Count > 0;
    public bool IsActive => _open.Count > 0;
    public long TotalStarts => _closedStarts + _open.Sum(s => (long)s.TotalStarts);

    internal void Join(SpawnStorm s, long nowMs)
    {
        if (StormCount == 0) FirstOpenedUtc = s.OpenedUtc;
        _open.Add(s);
        Latest = s;
        StormCount++;
        LastActiveMs = nowMs;
        Changed = EpisodesChanged = true;
    }

    internal void Leave(SpawnStorm s, long nowMs)
    {
        _open.Remove(s);
        _closedStarts += s.TotalStarts;
        Closed.Add(StormEpisode.Of(s));
        if (Closed.Count > MaxEpisodes) { Closed.RemoveAt(0); DroppedEpisodes++; }
        LastActiveMs = nowMs;
        Changed = EpisodesChanged = true;
    }

    internal void Active(long nowMs)
    {
        if (_open.Count > 0) LastActiveMs = nowMs;
    }

    /// <summary>A start counted in one of its storms: kept for spawns.csv while it holds fewer than <see cref="MaxTrace"/>.</summary>
    internal void Took(SpawnRecord r)
    {
        Changed = true;
        if (_traceTaken >= MaxTrace) return;
        _traceTaken++;
        _pending.Add(r);
    }

    /// <summary>The starts to append to spawns.csv now: those whose process exited (all of them on the folder's last write), oldest first.</summary>
    internal List<SpawnRecord> TakeRows(bool all)
    {
        var rows = all ? _pending.ToList() : _pending.Where(r => r.ExitMs is not null).ToList();
        if (rows.Count == _pending.Count) _pending.Clear();
        else _pending.RemoveAll(r => r.ExitMs is not null);
        rows.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        return rows;
    }

    /// <summary>It was written at <paramref name="nowMs"/> (the clock that only goes forward), as it stands now.</summary>
    public void MarkWritten(long nowMs)
    {
        LastWriteMs = nowMs;
        Changed = EpisodesChanged = false;
    }

    /// <summary>Rows that could not be appended: first at the next write.</summary>
    internal void PutBack(List<SpawnRecord> rows) => _pending.InsertRange(0, rows);

    /// <summary>Every storm it has had, in one line each, the first first (the open ones as they stand now).</summary>
    public List<StormEpisode> Episodes() => Closed.Concat(_open.Select(StormEpisode.Of)).OrderBy(e => e.OpenedUtc).ToList();
}

/// <summary>
/// Counts process starts per (parent, child image) over a sliding window, from the lifecycle events the collector
/// already produces (no extra tracing). A parent that launches more than <see cref="Settings.SpawnStormThreshold"/>
/// short-lived children within <see cref="Settings.SpawnStormWindowSeconds"/> opens a spawn storm, which stays
/// open until the rate falls below half the threshold for a minute. A fork copy (a child with its parent's own image and
/// exact command line: how Git Bash, MSYS2 and Cygwin run a subshell or a pipeline stage) is not a launch and is not
/// counted; what a fork copy starts counts to the process it was forked from. A start's parent is the registry's, else
/// the live process with the parent's PID, by its key now (a key a snapshot re-keyed is followed), so one parent is one
/// storm. For git.exe starts the repository root is resolved and flagged when it is a user profile, a OneDrive folder,
/// a volume root or an empty repository. Each storm is written to its parent identity's report folder
/// (<see cref="StormFolder"/>).
/// The window, how long a child ran, when a storm closes and when its report is due again are timed on the clock that
/// only goes forward (<see cref="Moment"/>): an event's system time is read onto it as the event arrives, so setting the
/// system clock back or forward neither stretches nor shrinks the window. Sampler thread only.
/// </summary>
public sealed class SpawnTracker
{
    private readonly Settings _settings;
    private readonly ProcessRegistry _registry;
    private readonly GitRootResolver _git;
    private readonly GitRootContext _ctx;
    private readonly Func<ProcessKey, string?> _cwdOf;
    private readonly Dictionary<ProcessKey, SpawnRecord> _recent = new();
    private readonly Dictionary<ProcessKey, SpawnStorm> _open = new();
    private readonly Dictionary<string, GitRootInfo> _rootInfo = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _rootReported = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Each fork copy seen (by its key), with the process it was forked from: what it starts counts to that one.</summary>
    private readonly Dictionary<ProcessKey, ProcessKey> _forkOrigin = new();
    /// <summary>The report folders that a storm of their identity would join now, by identity.</summary>
    private readonly Dictionary<string, StormFolder> _folders = new(StringComparer.Ordinal);
    /// <summary>Folders let go of in a sample before <see cref="DueFolders"/> saw them (a new storm of the identity in the tick its hour ended): their last write is still due.</summary>
    private readonly List<StormFolder> _retired = new();
    /// <summary>When each folder made in the last hour was made, on the clock that only goes forward.</summary>
    private readonly Queue<long> _newFolderMs = new();
    private long _rekeysSeen;
    private static readonly TimeSpan CloseQuiet = TimeSpan.FromSeconds(60);
    private const int MaxRecent = 20_000;

    /// <summary>A storm of an identity whose last storm closed less than this long ago is written into that storm's folder.</summary>
    public static readonly TimeSpan ReopenWindow = TimeSpan.FromMinutes(60);
    /// <summary>
    /// At most this many new report folders an hour (folders are made only with Detailed recording); a storm past it has its
    /// findings (the log and, with Detailed recording, findings.jsonl) and no folder.
    /// </summary>
    public const int MaxNewFoldersPerHour = 10;
    /// <summary>A folder is written when it is made, then while its numbers change at most this often (30 min) ...</summary>
    public static readonly TimeSpan FolderRefresh = TimeSpan.FromMinutes(30);
    /// <summary>... or, when a storm joined or closed, this often (10 min); and once more when it leaves memory.</summary>
    public static readonly TimeSpan FolderEpisodeRefresh = TimeSpan.FromMinutes(10);

    /// <param name="cwdOf">Working directory of a process instance (PEB read on Windows), or null.</param>
    public SpawnTracker(Settings settings, ProcessRegistry registry, GitRootResolver git, GitRootContext ctx, Func<ProcessKey, string?> cwdOf)
    {
        _settings = settings;
        _registry = registry;
        _git = git;
        _ctx = ctx;
        _cwdOf = cwdOf;
        _rekeysSeen = registry.Rekeys;
    }

    /// <summary>
    /// Whether a storm gets a report folder now (the app: Detailed recording). When it says no, a storm that opens gets
    /// none, as one past <see cref="MaxNewFoldersPerHour"/> does (its findings, without a folder), and none is counted as
    /// withheld; the folders already held stay until they leave memory. Read as each storm opens. Default: yes.
    /// </summary>
    public Func<bool> FoldersWanted { get; set; } = static () => true;

    public IReadOnlyCollection<SpawnStorm> OpenStorms => _open.Values;
    /// <summary>The report folders held (open storms', and those within their re-open window).</summary>
    public IReadOnlyCollection<StormFolder> Folders => _folders.Values;
    public long StartsSeen { get; private set; }
    public long GitStartsSeen { get; private set; }
    /// <summary>Fork copies seen (never counted as starts).</summary>
    public long ForkCopiesSeen { get; private set; }
    /// <summary>Storms that got no report folder (past <see cref="MaxNewFoldersPerHour"/>).</summary>
    public long FoldersWithheld { get; private set; }
    public int RecentCount => _recent.Count;

    private long WindowMs => (long)(_settings.SpawnStormWindowSeconds * 1000);
    private TimeSpan ShortLived => TimeSpan.FromSeconds(_settings.ShortLivedSeconds);

    /// <summary>
    /// A fork copy: a child with its parent's own image and exact command line (Git Bash, MSYS2 and Cygwin run a subshell,
    /// a pipeline stage or a command substitution as a copy of the shell, started with the shell's own command line).
    /// Both command lines must be known.
    /// </summary>
    public static bool IsForkCopy(string childImage, string? childCommandLine, string? parentImage, string? parentCommandLine) =>
        !string.IsNullOrEmpty(childCommandLine) && !string.IsNullOrEmpty(parentCommandLine) && parentImage is not null &&
        childImage.Equals(parentImage, StringComparison.OrdinalIgnoreCase) && string.Equals(childCommandLine, parentCommandLine, StringComparison.Ordinal);

    /// <summary>Feeds this tick's lifecycle events; returns findings and the report folders that should be (re)written.</summary>
    public (List<Finding> findings, List<StormFolder> toWrite) Observe(IReadOnlyList<LifecycleEvent> events, Moment now)
    {
        var findings = new List<Finding>();
        var toWrite = new List<StormFolder>();
        if (_registry.Rekeys != _rekeysSeen) FollowRekeys(now, findings);
        // Starts first, the earliest created first, so a fork copy is known before what it starts (a snapshot lists new
        // processes in any order); then the exits.
        var starts = new List<LifecycleEvent>();
        foreach (var e in events) if (e.Kind == LifecycleKind.Start && e.Pid > 4) starts.Add(e);
        starts.Sort((a, b) => CreatedAt(a).CompareTo(CreatedAt(b)));
        foreach (var e in starts) OnStart(e, now, findings);
        foreach (var e in events)
        {
            if (e.Kind == LifecycleKind.Exit && _recent.TryGetValue(e.Key, out var r))
            {
                r.ExitMs ??= Math.Max(r.StartMs, _registry.Get(e.Key)?.ExitMs ?? now.MsOf(e.TimeUtc));
                r.ExitUtc ??= e.TimeUtc;
                r.ExitCode ??= e.ExitCode;
            }
        }
        Evaluate(now, findings);
        DueFolders(now, toWrite);
        Prune(now);
        return (findings, toWrite);
    }

    private static long CreatedAt(LifecycleEvent e) => e.CreateTime > 0 ? e.CreateTime : e.TimeUtc.ToFileTimeUtc();

    /// <summary>Starts per (parent, child image) in the current window, busiest first (for the UI): fork copies left out, a fork copy's starts its origin's.</summary>
    public List<(string Parent, int ParentPid, string Image, int Starts, int ShortLived)> Rates(Moment now, int top = 10)
    {
        long from = now.Ms - WindowMs;
        return _recent.Values.Where(r => r.StartMs >= from && !r.ForkCopy)
            .GroupBy(r => (r.StormParent, r.Image))
            .Select(g => (ParentName(g.Key.StormParent, g.Key.StormParent.Pid), g.Key.StormParent.Pid, g.Key.Image, g.Count(), g.Count(r => r.ShortLived(ShortLived))))
            .OrderByDescending(x => x.Item4).Take(top).ToList();
    }

    private string ParentName(ProcessKey? key, int pid) => (key is { } k ? _registry.Get(k)?.Image : null) ?? _registry.GetLive(pid)?.Image ?? "?";

    private void OnStart(LifecycleEvent e, Moment now, List<Finding> findings)
    {
        if (_recent.ContainsKey(e.Key)) return;
        StartsSeen++;
        var rec = _registry.Get(e.Key) ?? (_registry.CurrentKey(e.Key) is var ck && ck != e.Key ? _registry.Get(ck) : null);
        // On the forward clock as the registry read them (its record's StartMs and ExitMs, each read as it arrived), else
        // the start read onto it now: a start seen after the process exited is not timed by the system clock's times.
        long startMs = rec?.StartMs ?? now.MsOf(e.TimeUtc);
        // The parent instance by its key now: the registry's, else the process with the parent's PID that can have started it
        // (live, or the newest remembered one when it exited already: a start the registry has no record of, or had none of
        // when the child started).
        ProcessKey? parentKey = (rec?.ParentKey ?? _registry.ParentKeyOf(e.ParentPid, e.CreateTime)) is { } pk ? _registry.CurrentKey(pk) : null;
        string? commandLine = e.CommandLine ?? rec?.CommandLine;
        var parentRec = parentKey is { } p0 ? _registry.Get(p0) : null;
        bool fork = parentRec is not null && IsForkCopy(e.Image, commandLine, parentRec.Image, parentRec.CommandLine);
        var stormParent = parentKey is { } p1 ? OriginOf(p1) : new ProcessKey(e.ParentPid, 0);
        var r = new SpawnRecord
        {
            Child = e.Key, Parent = parentKey, ParentPid = e.ParentPid, Image = e.Image, CommandLine = commandLine,
            StartUtc = e.TimeUtc, StartMs = startMs, ExitUtc = rec?.ExitUtc, ExitMs = rec?.ExitMs is { } x ? Math.Max(startMs, x) : null,
            ExitCode = rec?.ExitCode, ForkCopy = fork, StormParent = stormParent,
            RegistryKey = rec is not null && rec.Key != e.Key ? rec.Key : null,
        };
        _recent[e.Key] = r;
        if (fork)
        {
            // Not a launch: what it starts counts to the process it was forked from (under both its keys; both go with it, Forget).
            ForkCopiesSeen++;
            _forkOrigin[e.Key] = stormParent;
            if (r.RegistryKey is { } rk) _forkOrigin[rk] = stormParent;
            if (_open.TryGetValue(stormParent, out var parentStorm)) { parentStorm.ForkCopies++; if (parentStorm.Folder is { } pf) pf.Changed = true; }
            return;
        }
        GitRootInfo? info = null;
        if (IsGit(e.Image))
        {
            GitStartsSeen++;
            info = ResolveGit(r, rec, now);
            if (info is { Suspicious: true } && (!_rootReported.TryGetValue(info.Root, out long last) || now.Ms - last >= (long)TimeSpan.FromHours(1).TotalMilliseconds))
            {
                _rootReported[info.Root] = now.Ms;
                findings.Add(GitRootFinding(info, r, now.Utc));
            }
        }
        if (_open.TryGetValue(r.StormParent, out var storm)) storm.Add(r, info);
    }

    /// <summary>
    /// The process a start by <paramref name="parent"/> counts to: <paramref name="parent"/>, or when it is a fork copy, the
    /// process it was forked from (a copy of a copy followed up to 8 deep), by its key now.
    /// </summary>
    private ProcessKey OriginOf(ProcessKey parent)
    {
        if (_forkOrigin.TryGetValue(parent, out var known)) return known;
        var key = parent;
        for (int depth = 0; depth < 8; depth++)
        {
            if (_forkOrigin.TryGetValue(key, out var o)) return o;
            var rec = _registry.Get(key);
            if (rec?.ParentKey is not { } up) break;
            var upKey = _registry.CurrentKey(up);
            var upRec = _registry.Get(upKey);
            if (upRec is null || !IsForkCopy(rec.Image, rec.CommandLine, upRec.Image, upRec.CommandLine)) break;
            key = upKey;
        }
        return key;
    }

    /// <summary>
    /// A snapshot re-keyed an estimated key: the starts, the fork copies' origins and the open storms take the key it became.
    /// A storm whose key became one another open storm already has (the same parent counted under both keys before the
    /// re-key) ends there, as a storm ends (its folder lets it go, its finding says so): the one under that key goes on and
    /// counts the parent's starts from now.
    /// </summary>
    private void FollowRekeys(Moment now, List<Finding> findings)
    {
        _rekeysSeen = _registry.Rekeys;
        foreach (var r in _recent.Values) r.StormParent = _registry.CurrentKey(r.StormParent);
        foreach (var (k, v) in _forkOrigin.ToList()) _forkOrigin[k] = _registry.CurrentKey(v);
        foreach (var (k, storm) in _open.ToList())
        {
            var key = _registry.CurrentKey(k);
            if (key == k) continue;
            _open.Remove(k);
            storm.ParentKey = key;
            if (!_open.TryAdd(key, storm)) CloseStorm(storm, now, findings);
        }
    }

    private static bool IsGit(string image) => image.Equals("git.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>The working directory came from git's own -C option (<see cref="SpawnRecord.WorkDirSource"/>).</summary>
    public const string GitDashC = "git -C";

    private GitRootInfo? ResolveGit(SpawnRecord r, ProcessRecord? rec, Moment now)
    {
        // -C / --work-tree first, then the child's own cwd (cwd-only PEB read in the ETW start callback, rate-limited, when
        // the process was still there), then the parent's.
        string? cwd = rec?.ContextValue(Aicc.SessionContext.CwdKey);
        string source = "cwd";
        if (cwd is null && r.Parent is { } pk) { cwd = _cwdOf(pk); source = "parent cwd"; }
        string? dir = GitRootResolver.WorkingDirectory(r.CommandLine, cwd, out bool viaC);
        if (dir is null) return null;
        r.WorkDir = dir;
        r.WorkDirSource = viaC ? GitDashC : source;
        if (_git.FindRoot(dir, now.Ms) is not { } root) return null;
        r.GitRoot = root;
        if (!_rootInfo.TryGetValue(root, out var info) || _rootInfo.Count > 1000)
        {
            if (_rootInfo.Count > 1000) _rootInfo.Clear();
            _rootInfo[root] = info = _git.Classify(root, _ctx);
        }
        return info;
    }

    private void Evaluate(Moment now, List<Finding> findings)
    {
        long from = now.Ms - WindowMs;
        var groups = new Dictionary<ProcessKey, List<SpawnRecord>>();
        var forks = new Dictionary<ProcessKey, int>();
        foreach (var r in _recent.Values)
        {
            if (r.StartMs < from) continue;
            if (r.ForkCopy) { forks[r.StormParent] = forks.GetValueOrDefault(r.StormParent) + 1; continue; }
            if (!groups.TryGetValue(r.StormParent, out var list)) groups[r.StormParent] = list = new List<SpawnRecord>();
            list.Add(r);
        }
        foreach (var (pk, list) in groups)
        {
            int shortLived = 0;
            foreach (var r in list) if (r.ShortLived(ShortLived)) shortLived++;
            _open.TryGetValue(pk, out var storm);
            bool opened = false;
            if (shortLived > _settings.SpawnStormThreshold && storm is null)
            {
                storm = Open(pk, list, forks.GetValueOrDefault(pk), now);
                _open[pk] = storm;
                opened = true;
            }
            if (storm is null) continue;
            storm.PeakShortLived = Math.Max(storm.PeakShortLived, shortLived);
            storm.PeakStarts = Math.Max(storm.PeakStarts, list.Count);
            foreach (var g in list.GroupBy(r => r.Image, StringComparer.OrdinalIgnoreCase))
                if (storm.PeakByImage.ContainsKey(g.Key) || storm.PeakByImage.Count < SpawnStorm.MaxKeys)
                    storm.PeakByImage[g.Key] = Math.Max(storm.PeakByImage.GetValueOrDefault(g.Key), g.Count());
            if (shortLived >= _settings.SpawnStormThreshold / 2) storm.LastAboveMs = now.Ms;
            storm.Folder?.Active(now.Ms);
            if (opened)
            {
                var f = storm.LastFinding = StormFinding(storm, now, opened: true);
                f.ReportDir = storm.Folder?.Dir; // a folder this storm joined is already there
                findings.Add(f);
            }
        }
        foreach (var storm in _open.Values.ToList())
        {
            if (now.Ms - storm.LastAboveMs < (long)CloseQuiet.TotalMilliseconds) continue;
            _open.Remove(storm.ParentKey);
            CloseStorm(storm, now, findings);
        }
    }

    /// <summary>A storm (no longer in the open ones) ends now: its folder lets it go, and its finding says so.</summary>
    private void CloseStorm(SpawnStorm storm, Moment now, List<Finding> findings)
    {
        storm.ClosedUtc = now.Utc;
        storm.ClosedMs = now.Ms;
        storm.Folder?.Leave(storm, now.Ms);
        var f = storm.LastFinding = StormFinding(storm, now, opened: false);
        f.ReportDir = storm.Folder?.Dir;
        findings.Add(f);
    }

    private SpawnStorm Open(ProcessKey pk, List<SpawnRecord> list, int forkCopies, Moment now)
    {
        var parent = _registry.Get(pk) ?? _registry.GetLive(pk.Pid);
        List<ProcessRecord> ancestry = parent is null ? [] : _registry.Ancestry(parent.Key);
        var chain = string.Join(" › ", ancestry.Select(p => p.DisplayName));
        string image = parent?.DisplayName ?? "?";
        // The identity its report folder is shared by: image, role and owner (a Claude Code session's bash, a test runner's
        // script), so one session's storms share a folder; without a record, the process itself.
        var id = ancestry.Count > 0 ? ProcessIdentities.Of(ancestry) : null;
        var storm = new SpawnStorm
        {
            ParentKey = pk, ParentPid = pk.Pid, ParentImage = image, ParentCommandLine = parent?.CommandLine,
            ParentChain = chain, OpenedUtc = now.Utc, OpenedMs = now.Ms, LastAboveMs = now.Ms, ForkCopies = forkCopies,
            Identity = id is null ? $"pid:{pk.Pid}:{pk.CreateTime}" : $"{id.Image}|{id.Role}|{id.Owner.Key}|{(id.ViaWmi ? "wmi" : "")}",
            IdentityName = id?.Display ?? $"{image} (PID {pk.Pid})",
        };
        AssignFolder(storm, image, now.Ms);
        foreach (var r in list.OrderBy(r => r.StartMs))
            storm.Add(r, r.GitRoot is { } g ? _rootInfo.GetValueOrDefault(g) : null);
        return storm;
    }

    /// <summary>
    /// The folder a storm is written to: its identity's, while that one has a storm open or closed one within the re-open
    /// window; else a new one, unless <see cref="MaxNewFoldersPerHour"/> were made in the last hour (then none).
    /// </summary>
    private void AssignFolder(SpawnStorm storm, string image, long nowMs)
    {
        if (!FoldersWanted())
        {
            storm.FolderNotWanted = true; // Reports only: no folder, not even its identity's (it would not be written)
            return;
        }
        if (_folders.TryGetValue(storm.Identity, out var f))
        {
            if (f.IsActive || nowMs - f.LastActiveMs < (long)ReopenWindow.TotalMilliseconds)
            {
                f.Join(storm, nowMs);
                storm.Folder = f;
                return;
            }
            // Its hour ended in this very sample, before DueFolders let it go: it is let go now, with its last write.
            Retire(f);
        }
        while (_newFolderMs.Count > 0 && nowMs - _newFolderMs.Peek() >= 3_600_000) _newFolderMs.Dequeue();
        if (_newFolderMs.Count >= MaxNewFoldersPerHour)
        {
            FoldersWithheld++;
            return;
        }
        _newFolderMs.Enqueue(nowMs);
        f = new StormFolder { Identity = storm.Identity, Name = storm.IdentityName, Subject = Path.GetFileNameWithoutExtension(image.Split(' ')[0]) };
        f.Join(storm, nowMs);
        _folders[storm.Identity] = f;
        storm.Folder = f;
    }

    /// <summary>
    /// The folders to write now: one not written yet; one whose storms joined or closed, 10 minutes after its last write;
    /// one whose numbers changed, 30 minutes after; and one that leaves memory (quiet for the whole re-open window) with
    /// anything not written yet, its starts held for spawns.csv among it, whatever the time (<see cref="StormFolder.Final"/>).
    /// </summary>
    private void DueFolders(Moment now, List<StormFolder> toWrite)
    {
        foreach (var f in _retired) if (Unwritten(f)) toWrite.Add(f);
        _retired.Clear();
        foreach (var f in _folders.Values.ToList())
        {
            bool leaving = !f.IsActive && now.Ms - f.LastActiveMs >= (long)ReopenWindow.TotalMilliseconds;
            bool due = f.LastWriteMs is not { } w
                       || (f.EpisodesChanged && now.Ms - w >= (long)FolderEpisodeRefresh.TotalMilliseconds)
                       || (f.Changed && now.Ms - w >= (long)FolderRefresh.TotalMilliseconds)
                       || (leaving && Unwritten(f));
            if (leaving) { f.Final = true; _folders.Remove(f.Identity); }
            if (due) toWrite.Add(f);
        }
    }

    /// <summary>A folder is let go of before DueFolders saw its hour end: its last write is due at the end of this sample.</summary>
    private void Retire(StormFolder f)
    {
        f.Final = true;
        _folders.Remove(f.Identity);
        _retired.Add(f);
    }

    /// <summary>Anything of the folder not written yet: the folder itself, its numbers, its storms, or starts held for spawns.csv.</summary>
    private static bool Unwritten(StormFolder f) => f.LastWriteMs is null || f.Changed || f.EpisodesChanged || f.HasPending;

    /// <summary>The folders with anything not written yet, for the end of a run (each then written for the last time).</summary>
    public List<StormFolder> FoldersToFinish()
    {
        var list = _retired.Concat(_folders.Values).Where(Unwritten).ToList();
        _retired.Clear();
        foreach (var f in list) f.Final = true;
        return list;
    }

    private void Prune(Moment now)
    {
        long cutoff = now.Ms - WindowMs - (long)ShortLived.TotalMilliseconds - 60_000;
        if (_recent.Count < 64 && _recent.Values.All(r => r.StartMs >= cutoff)) return;
        foreach (var k in _recent.Where(kv => kv.Value.StartMs < cutoff).Select(kv => kv.Key).ToList()) Forget(k);
        if (_recent.Count > MaxRecent)
            foreach (var k in _recent.Values.OrderBy(r => r.StartMs).Take(_recent.Count - MaxRecent).Select(r => r.Child).ToList()) Forget(k);
    }

    /// <summary>A start leaves the window's memory; a fork copy's origin goes with it, under both its keys (the registry still tells it while it runs).</summary>
    private void Forget(ProcessKey k)
    {
        if (!_recent.Remove(k, out var r) || !r.ForkCopy) return;
        _forkOrigin.Remove(k);
        if (r.RegistryKey is { } rk) _forkOrigin.Remove(rk);
    }

    /// <summary>Fork-copy keys whose origin is held: at most two for each fork copy among the starts held.</summary>
    public int ForkOriginsHeld => _forkOrigin.Count;

    // ---- findings and report text ----

    private Finding StormFinding(SpawnStorm s, Moment now, bool opened)
    {
        string title = opened
            ? $"Spawn storm: {s.ParentImage} (PID {s.ParentPid}) started {s.PeakShortLived} short-lived children in {_settings.SpawnStormWindowSeconds:0} s ({s.TopImages()})"
            : $"Spawn storm ended: {s.ParentImage} (PID {s.ParentPid}) started {s.TotalStarts} processes in {Fmt.Duration(TimeSpan.FromMilliseconds((s.ClosedMs ?? now.Ms) - s.OpenedMs))} (peak {s.PeakShortLived} short-lived per {_settings.SpawnStormWindowSeconds:0} s)";
        var details = new List<string>();
        foreach (var (pattern, n) in s.Patterns.OrderByDescending(kv => kv.Value).Take(3)) details.Add($"{n}× {pattern}");
        foreach (var (root, (info, n)) in s.GitRoots.OrderByDescending(kv => kv.Value.Count).Take(3))
            details.Add($"git root {root} ({n} starts){(info.Suspicious ? ": " + string.Join(", ", info.Flags) : "")}");
        if (s.ForkCopies > 0) details.Add(ForkCopiesLine(s.ForkCopies));
        if (s.Folder is { StormCount: > 1 } f && opened)
            details.Add($"Written into the report folder of {f.StormCount - 1:N0} earlier storm{(f.StormCount == 2 ? "" : "s")} of {f.Name} (one folder while they keep coming within {ReopenWindow.TotalMinutes:0} min)");
        if (s.Folder is null)
            details.Add(s.FolderNotWanted
                // Said of the storm, as it began, so it stays true when the choice changes while the storm goes on.
                ? "No report folder: this storm began while Reports only was chosen, and only Detailed recording keeps spawn-storm folders; this storm is in its findings and the log"
                : $"No report folder: PoolWatch makes at most {MaxNewFoldersPerHour} new spawn-storm folders an hour, so this storm is only in this finding (the log and findings.jsonl)");
        return new Finding
        {
            TimeUtc = now.Utc, Kind = FindingKind.SpawnStorm, Severity = opened ? FindingSeverity.Alert : FindingSeverity.Info, Title = title,
            Details = details, Pid = s.ParentPid, Image = s.ParentImage, Data = StormData(s, FindingKeys),
        };
    }

    private static string ForkCopiesLine(int n) =>
        $"{n:N0} fork cop{(n == 1 ? "y" : "ies")} of the parent (its own image and command line: how Git Bash, MSYS2 and Cygwin run a subshell or a " +
        "pipeline) not counted as starts; what they started is counted to the parent";

    /// <summary>
    /// The keys of each map a storm's finding keeps: a finding stays in memory with up to 1,000 others (and is a line of
    /// findings.jsonl); the storm's report.json has every key.
    /// </summary>
    public const int FindingKeys = 15;

    /// <param name="top">The most keys of each map (images, peak images, git roots), the largest first; patterns are always the top 15.</param>
    public Dictionary<string, object?> StormData(SpawnStorm s, int top = int.MaxValue) => new()
    {
        ["parent"] = s.ParentImage, ["parentPid"] = s.ParentPid, ["parentCommandLine"] = s.ParentCommandLine, ["parentChain"] = s.ParentChain,
        ["opened"] = s.OpenedUtc, ["closed"] = s.ClosedUtc, ["windowSeconds"] = _settings.SpawnStormWindowSeconds, ["threshold"] = _settings.SpawnStormThreshold,
        ["peakShortLived"] = s.PeakShortLived, ["peakStarts"] = s.PeakStarts, ["totalStarts"] = s.TotalStarts, ["forkCopies"] = s.ForkCopies,
        ["byImage"] = s.ByImage.OrderByDescending(kv => kv.Value).Take(top).ToDictionary(kv => kv.Key, kv => (object)kv.Value),
        ["peakByImage"] = s.PeakByImage.OrderByDescending(kv => kv.Value).Take(top).ToDictionary(kv => kv.Key, kv => (object)kv.Value),
        ["patterns"] = s.Patterns.OrderByDescending(kv => kv.Value).Take(15).Select(kv => new Dictionary<string, object?> { ["pattern"] = kv.Key, ["count"] = kv.Value }).ToList(),
        ["gitRoots"] = s.GitRoots.OrderByDescending(kv => kv.Value.Count).Take(top).Select(kv => new Dictionary<string, object?>
        {
            ["root"] = kv.Key, ["count"] = kv.Value.Count, ["flags"] = kv.Value.Info.Flags.ToList(), ["trackedFiles"] = kv.Value.Info.TrackedFiles,
        }).ToList(),
    };

    /// <summary>
    /// A report folder's report.json data: its latest storm's (<see cref="StormData(SpawnStorm, int)"/>, every key), the
    /// identity the folder is shared by, and every storm in it in one line each.
    /// </summary>
    public Dictionary<string, object?> StormData(StormFolder f)
    {
        var d = StormData(f.Latest);
        d["identity"] = f.Name;
        d["storms"] = f.StormCount;
        d["folderTotalStarts"] = f.TotalStarts;
        d["episodes"] = f.Episodes().Select(e => new Dictionary<string, object?>
        {
            ["opened"] = e.OpenedUtc, ["closed"] = e.ClosedUtc, ["parentPid"] = e.ParentPid, ["parent"] = e.ParentImage, ["totalStarts"] = e.TotalStarts,
            ["peakShortLived"] = e.PeakShortLived, ["forkCopies"] = e.ForkCopies, ["children"] = e.Top,
        }).ToList();
        if (f.DroppedEpisodes > 0) d["earlierEpisodesNotListed"] = f.DroppedEpisodes;
        return d;
    }

    /// <summary>The finding a report folder's report.md and report.json are headed by (its storms as they stand).</summary>
    public Finding FolderFinding(StormFolder f)
    {
        var s = f.Latest;
        string window = $"{_settings.SpawnStormWindowSeconds:0} s";
        string title = f.StormCount == 1
            ? $"Spawn storm: {s.ParentImage} (PID {s.ParentPid}) started {s.TotalStarts:N0} processes (peak {s.PeakShortLived} short-lived per {window}){(s.IsOpen ? ", still running" : "")}"
            : $"Spawn storms: {f.Name}: {f.StormCount:N0} storms, {f.TotalStarts:N0} processes started (latest PID {s.ParentPid}, peak {s.PeakShortLived} short-lived per {window}){(f.IsActive ? ", still running" : "")}";
        return new Finding
        {
            TimeUtc = f.FirstOpenedUtc, Kind = FindingKind.SpawnStorm, Severity = FindingSeverity.Alert, Title = title,
            Pid = s.ParentPid, Image = s.ParentImage, Data = StormData(f),
        };
    }

    private static Finding GitRootFinding(GitRootInfo info, SpawnRecord r, DateTime nowUtc) => new()
    {
        TimeUtc = nowUtc, Kind = FindingKind.GitRoot, Severity = FindingSeverity.Warning,
        Title = $"git.exe works in {info.Root}, which is {string.Join(", ", info.Flags)}",
        Details =
        [
            $"Started by PID {r.ParentPid}; working directory {r.WorkDir} (from {r.WorkDirSource})",
            "A repository rooted here makes every status / ls-files scan the whole tree (AppData, OneDrive, caches).",
        ],
        Pid = r.Child.Pid, Image = r.Image,
        Data = new Dictionary<string, object?>
        {
            ["root"] = info.Root, ["flags"] = info.Flags.ToList(), ["trackedFiles"] = info.TrackedFiles, ["workDir"] = r.WorkDir,
            ["workDirSource"] = r.WorkDirSource, ["commandLine"] = r.CommandLine, ["parentPid"] = r.ParentPid,
        },
    };

    /// <summary>report.md body for a report folder: its storms in one line each, then its latest storm in full.</summary>
    public string StormMarkdown(StormFolder f)
    {
        var sb = new StringBuilder();
        if (f.StormCount > 1)
        {
            sb.AppendLine($"- {f.StormCount:N0} storms of {FindingReport.Md(f.Name)}, {f.TotalStarts:N0} processes started in all: one report folder while they " +
                          $"keep coming within {ReopenWindow.TotalMinutes:0} minutes of each other");
            sb.AppendLine().AppendLine("## Storms in this folder").AppendLine();
            sb.AppendLine("| Opened | Closed | Parent | Starts | Peak short-lived | Fork copies | Children |").AppendLine("|---|---|---|---:|---:|---:|---|");
            if (f.DroppedEpisodes > 0) sb.AppendLine($"| | | {f.DroppedEpisodes:N0} earlier storms not listed | | | | |");
            foreach (var e in f.Episodes())
                sb.AppendLine($"| {Fmt.Clock(e.OpenedUtc)} | {(e.ClosedUtc is { } c ? Fmt.Clock(c) : "still open")} | {FindingReport.Md(e.ParentImage)} (PID {e.ParentPid}) | " +
                              $"{e.TotalStarts:N0} | {e.PeakShortLived} | {e.ForkCopies:N0} | {FindingReport.Md(e.Top)} |");
            sb.AppendLine().AppendLine("## The latest storm").AppendLine();
        }
        AppendStorm(sb, f.Latest);
        sb.AppendLine().AppendLine($"The first {f.TraceRows:N0} starts of {(f.StormCount > 1 ? "these storms" : "this storm")} are in `spawns.csv`, each once it exited.");
        return sb.ToString();
    }

    private void AppendStorm(StringBuilder sb, SpawnStorm s)
    {
        sb.AppendLine($"- Parent: **{FindingReport.Md(s.ParentImage)}** (PID {s.ParentPid}){(s.ParentCommandLine is { } cl ? $", `{FindingReport.MdCode(cl)}`" : "")}");
        if (s.ParentChain.Length > 0) sb.AppendLine($"- Parent chain: {FindingReport.Md(s.ParentChain)}");
        sb.AppendLine($"- Open {Fmt.Clock(s.OpenedUtc)} to {(s.ClosedUtc is { } c ? Fmt.Clock(c) : "now (still open)")}; {s.TotalStarts:N0} starts; " +
                      $"peak {s.PeakShortLived} short-lived (exited within {_settings.ShortLivedSeconds:0} s) and {s.PeakStarts} starts per {_settings.SpawnStormWindowSeconds:0} s " +
                      $"(threshold {_settings.SpawnStormThreshold})");
        if (s.ForkCopies > 0) sb.AppendLine($"- {FindingReport.Md(ForkCopiesLine(s.ForkCopies))}");
        sb.AppendLine().AppendLine("Every process start creates a token, a process object, a console host and file-system work; a parent that");
        sb.AppendLine("starts hundreds of short-lived processes a minute multiplies all of it. This is a correlation finding, not a proof of a leak.");
        sb.AppendLine().AppendLine("## Children by image").AppendLine().AppendLine("| Image | Starts | Peak per window |").AppendLine("|---|---:|---:|");
        foreach (var (img, n) in s.ByImage.OrderByDescending(kv => kv.Value))
            sb.AppendLine($"| {FindingReport.Md(img)} | {n:N0} | {s.PeakByImage.GetValueOrDefault(img)} |");
        sb.AppendLine().AppendLine("## Top command-line patterns").AppendLine().AppendLine("| Count | Pattern |").AppendLine("|---:|---|");
        foreach (var (p, n) in s.Patterns.OrderByDescending(kv => kv.Value).Take(15))
            sb.AppendLine($"| {n:N0} | `{Util.MdText.CodeCell(p)}` |");
        if (s.GitRoots.Count > 0)
        {
            sb.AppendLine().AppendLine("## Git repository roots").AppendLine().AppendLine("| Root | git starts | Tracked files | Flags |").AppendLine("|---|---:|---:|---|");
            foreach (var (root, (info, n)) in s.GitRoots.OrderByDescending(kv => kv.Value.Count))
                sb.AppendLine($"| `{Util.MdText.CodeCell(root)}` | {n:N0} | {info.TrackedFiles?.ToString("N0", CultureInfo.InvariantCulture) ?? "?"} | {FindingReport.Md(info.Flags.Count > 0 ? string.Join(", ", info.Flags) : "none")} |");
            if (s.GitRoots.Values.Any(g => g.Info.Suspicious))
                sb.AppendLine().AppendLine("A flagged root (user profile, OneDrive, volume root, empty repository) makes each `git status` / `ls-files` walk far more " +
                                           "than a project. Check for a stray `.git` folder there.");
        }
    }

    /// <summary>spawns.csv's header row.</summary>
    public const string TraceHeader = "time,pid,image,lifetime_ms,exit_code,work_dir,work_dir_source,git_root,command_line";

    /// <summary>spawns.csv rows for <paramref name="rows"/>, one line each.</summary>
    public static string TraceRows(IEnumerable<SpawnRecord> rows)
    {
        var sb = new StringBuilder();
        foreach (var r in rows)
            sb.Append(Util.Csv.Line(r.StartUtc, r.Child.Pid, r.Image,
                r.Lifetime is { } l ? (long)l.TotalMilliseconds : null, r.ExitCode, r.WorkDir, r.WorkDirSource, r.GitRoot,
                r.CommandLine is { Length: > 300 } cl ? cl[..300] + "…" : r.CommandLine)).Append('\n');
        return sb.ToString();
    }
}
