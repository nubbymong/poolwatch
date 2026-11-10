using PoolWatch.Core.Model;
using PoolWatch.Core.Util;

namespace PoolWatch.Engine;

/// <summary>
/// Where PoolWatch writes. On Windows everything lives under %ProgramData%\PoolWatch (admin-only: PoolWatch runs
/// elevated, so its data must be out of reach of an unelevated process), except an explicit headless --out
/// directory. On other platforms (the Linux build/CI) it is %LOCALAPPDATA%\PoolWatch. All writes go through
/// <see cref="Guard"/>.
/// </summary>
internal sealed class AppPaths
{
    public string DataRoot { get; }
    public string? OutDir { get; }
    public PathGuard Guard { get; }
    /// <summary>What securing the data root did (moved a foreign one aside, repaired its permissions): for the log and the timeline.</summary>
    public List<string> StartupNotes { get; } = new();
    /// <summary>
    /// The usual data root, when PoolWatch does not use it this time and <see cref="DataRoot"/> is a folder of PoolWatch's
    /// own next to it instead: it could not be made safe, or the data in a folder of its own could not be moved back to
    /// it (<see cref="DataRootGuard.PrepareFallback"/>), or this process does not hold the one-instance mutex and the data
    /// is in that folder (<see cref="DataRootGuard.UseAsItIs"/>). Null otherwise.
    /// </summary>
    public string? UsualRootNotUsed { get; }

    /// <summary>
    /// Whether the data root's checks ran here (Windows, elevated): its writes then never make the data root itself.
    /// </summary>
    private readonly bool _secured;
    // The log's roll-over runs through this proof's WhileHeld, so only while it is not let go (one process at a time
    // holds the one-instance mutex): the proof given when it holds the mutex on the thread that makes these paths
    // (HeldHere, as for moving data folders), and null otherwise: no proof, one that holds on another thread or was let
    // go, or the tests' stand-in (holdsInstance).
    private readonly ExitSignal.Hold? _rollGate;

    /// <summary>
    /// The data folder for this process. A start given <paramref name="hold"/>, the proof that its thread holds the
    /// one-instance mutex (<see cref="ExitSignal.Hold"/>: only <see cref="ExitSignal.AsTheOneInstance"/>, for the tray app
    /// and a headless run, and <see cref="ExitSignal.StopAndHold(TimeSpan)"/>, for the uninstall cleanup once it has
    /// stopped PoolWatch, make one), used on that thread while it holds, is the one PoolWatch that may move data folders,
    /// make a folder of its own or record one (<see cref="DataRootGuard.Prepare"/>). Every other process (the token-leak
    /// guard's helper, <c>--mark</c>, <c>--changes-dump</c> and the other command-line steps) finds the folder in use as it
    /// stands, and makes the usual one only when nothing is there and no PoolWatch may hold the mutex (<see
    /// cref="DataRootGuard.UseAsItIs"/>).
    /// </summary>
    public AppPaths(string? outDir, ExitSignal.Hold? hold = null) : this(DefaultDataRoot(), outDir, secure: OperatingSystem.IsWindows(), hold: hold) { }

    /// <param name="secure">Secure the data root (Windows, the real data root, elevated). Tests pass false and a TEMP folder:
    /// a test never changes an ACL.</param>
    /// <param name="refuseVolume">With <paramref name="secure"/>, the volumes no write may land on (default: <see
    /// cref="Collectors.VolumeIdentity.RefusalFor"/>, ReFS). Tests that secure a root with fake steps in TEMP pass one that
    /// refuses none, since TEMP may be on a Dev Drive.</param>
    /// <param name="holdsInstance">
    /// Null (the public constructor): <paramref name="hold"/> decides. Only tests of the start's checks pass true or
    /// false, in place of a proof.
    /// </param>
    /// <param name="instanceRunning">
    /// For a process that does not hold the mutex: whether a PoolWatch may hold it now (default: <see
    /// cref="ExitSignal.InstanceRunning()"/>). Tests pass their own answer.
    /// </param>
    /// <param name="hold">
    /// The proof that the calling thread holds the one-instance mutex: this start may move data folders only while it
    /// holds on this thread (<see cref="ExitSignal.Hold.HeldHere"/>). Null: it does not hold it.
    /// </param>
    internal AppPaths(string dataRoot, string? outDir, bool secure, IRootOps? rootOps = null, Func<string, string?>? refuseVolume = null,
        bool? holdsInstance = null, Func<bool>? instanceRunning = null, ExitSignal.Hold? hold = null)
    {
        string root = Path.GetFullPath(dataRoot);
        _rollGate = holdsInstance is null && hold is { HeldHere: true } ? hold : null;
        _secured = secure && OperatingSystem.IsWindows() && (rootOps is not null || InstallGuard.IsElevated);
        var ops = _secured ? rootOps ?? new WindowsRootOps() : null;
        if (ops is not null)
        {
            // The root itself is opened without following links, a foreign or user-writable one is moved
            // aside, a new one is created with its permissions at creation, and they are applied again only when a check
            // through a handle finds them changed (Administrators and SYSTEM full control, read for your account).
            // A link there is moved aside like any untrusted root. When the root cannot be made safe (a process holds it
            // open or makes it again after every move), PoolWatch keeps running on a folder of its own next to it, and says
            // so. That is the start of a process given the proof that it holds the one-instance mutex (Prepare,
            // PrepareFallback); any other finds the folder in use as it stands (UseAsItIs), so it never moves a data folder,
            // and makes the usual one only when nothing is there and no PoolWatch may hold the mutex.
            // DataRootRefusedException, with no file written in a data folder, when:
            // - no folder of its own can be made either (Prepare, then PrepareFallback);
            // - the usual folder, or the folder of its own that PoolWatch's data is in, cannot be read now, or cannot be used
            //   as it is while its permissions are not PoolWatch's (NoFallback: no other folder is used);
            // - a process that does not hold the mutex cannot use the folder as it stands, or the usual folder is not there
            //   while a PoolWatch may hold the mutex, or it cannot make it (NoFallback);
            // - which folder the data folder is cannot be read once it is checked (NoFallback).
            var reader = Interop.CurrentUser();
            if (!(holdsInstance ?? hold is { HeldHere: true }))
            {
                var (used, usual, notes) = DataRootGuard.UseAsItIs(root, reader, ops, instanceRunning ?? ExitSignal.InstanceRunning);
                if (!usual) UsualRootNotUsed = root;
                root = used;
                StartupNotes.AddRange(notes);
            }
            else
            {
                try { StartupNotes.AddRange(DataRootGuard.Prepare(root, reader, ops, () => DateTime.UtcNow)); }
                catch (DataRootRefusedException ex) when (!ex.NoFallback)
                {
                    StartupNotes.AddRange(ex.Notes); // what this start did before (a folder moved aside, say)
                    (string Root, List<string> Notes) fallback;
                    try { fallback = DataRootGuard.PrepareFallback(root, ex.Message, reader, ops, () => DateTime.UtcNow); }
                    catch (DataRootRefusedException refused) { throw refused.After(ex.Notes); }
                    UsualRootNotUsed = root;
                    root = fallback.Root;
                    StartupNotes.AddRange(fallback.Notes);
                }
            }
        }
        DataRoot = root;
        // The ReFS rule keeps PoolWatch's own files off the volumes whose pool it measures (the real app, secure: true).
        // Tests (secure: false, or a refuseVolume that refuses none) write wherever TEMP is, which may be a Dev Drive on a
        // developer's PC. Once the start's checks ran, the data root must be there, as the very folder they checked, for
        // any write in it: a folder made at that name later (after it was moved or deleted) is not written to.
        Guard = new PathGuard(DataRoot, LaunchGate.InstallFolder(), refuseVolume: secure ? refuseVolume ?? Collectors.VolumeIdentity.RefusalFor : null,
            isRemote: Collectors.VolumeIdentity.IsRemoteDrive, dataRootMustExist: _secured, dataRootIdentity: ops is null ? null : ops.IdentityOf,
            holdDataRoot: ops is null ? null : ops.Hold);
        if (!Guard.DataRootPinned)
            throw new DataRootRefusedException($"PoolWatch could not read which folder its data folder {DataRoot} is, after checking it. Nothing " +
                                               "was written.", noFallback: true, notes: StartupNotes);
        if (outDir is not null)
        {
            OutDir = Path.GetFullPath(outDir);
            Guard.AllowRoot(OutDir);
        }
        if (!_secured) Directory.CreateDirectory(Guard.CheckReal(DataRoot)); // tests, the Linux build, an unelevated run (it changes nothing elevated)
        if (OutDir is not null) Directory.CreateDirectory(Guard.CheckReal(OutDir));
    }

    /// <summary>
    /// The admin-only data root on Windows (%ProgramData%\PoolWatch). On the Linux build/CI %ProgramData% maps to
    /// a path nothing can write to, so there we keep %LOCALAPPDATA%\PoolWatch — that build never runs elevated.
    /// </summary>
    internal static string DefaultDataRoot() => Path.Combine(
        Environment.GetFolderPath(OperatingSystem.IsWindows()
            ? Environment.SpecialFolder.CommonApplicationData
            : Environment.SpecialFolder.LocalApplicationData),
        "PoolWatch");

    public string SettingsFile => Path.Combine(DataRoot, "settings.json");

    /// <summary>
    /// <see cref="PathGuard.CheckReal"/> for a path about to be written, renamed or deleted, but which also throws the
    /// refusal a sharing conflict throws when an open of that path would have to wait for another program
    /// at the time of the check. Passed to the report writers as their path check, which they run on each file before
    /// they write, rename or delete it; their callers already treat a failed write as a logged miss.
    /// </summary>
    public string CheckRealNoWait(string path)
    {
        string real = Guard.CheckReal(path);
        NoWaitOpen.FailIfOpenWouldWait(real);
        return real;
    }

    /// <summary>The log, every append and roll-over through the link-resolving write policy. What securing
    /// the data root did is its first lines. Only the process that holds the one-instance mutex rolls it over, while it
    /// holds it.</summary>
    public Log OpenLog()
    {
        var log = new Log(LogFile, Guard.CheckReal, rollGate: _rollGate);
        foreach (var n in StartupNotes) log.Info("data folder: " + n);
        return log;
    }

    /// <summary>CSV output: the --out dir, or a daily folder under data\.</summary>
    public string CsvDir(DateTime utcNow) => OutDir ?? Path.Combine(DataRoot, "data", utcNow.ToLocalTime().ToString("yyyyMMdd"));

    public string IncidentsDir => Path.Combine(OutDir ?? DataRoot, "incidents");
    public string ExportsDir => Path.Combine(DataRoot, "exports");
    public string LogFile => Path.Combine(OutDir ?? DataRoot, "poolwatch.log");
    public string CacheDir => Path.Combine(OutDir ?? DataRoot, "cache");
    /// <summary>Boot baselines and per-boot summaries, one folder per boot (kept across reboots, last 60).</summary>
    public string BootsDir => Path.Combine(OutDir ?? DataRoot, "boots");
    /// <summary>Requests from a second PoolWatch.exe (<c>--mark</c>) to the running one.</summary>
    public string RequestsDir => Path.Combine(OutDir ?? DataRoot, "requests");

    /// <summary>
    /// Creates a directory after checking the write policy. Below a data root the start's checks secured, each folder is
    /// made on its own, inside the one above it, so the data root itself is never made here: when it is gone (renamed or
    /// deleted while PoolWatch runs), this fails rather than make it again with the permissions %ProgramData% hands down.
    /// </summary>
    public string EnsureDir(string dir)
    {
        string real = Guard.CheckReal(dir);
        if (!_secured) { Directory.CreateDirectory(real); return dir; }
        string root = DataRoot.TrimEnd('\\'), full = Path.GetFullPath(real).TrimEnd('\\');
        if (full.Equals(root, StringComparison.OrdinalIgnoreCase)) return dir; // there (CheckReal says so), and never made here
        if (!full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) { Directory.CreateDirectory(real); return dir; } // --out
        MakeBelow(root, full);
        return dir;
    }

    /// <summary>
    /// Makes <paramref name="full"/> and each folder between it and <paramref name="root"/>, one at a time, each inside the
    /// one above it; <paramref name="root"/> itself is never made (when it is not there, the first step fails).
    /// </summary>
    internal static void MakeBelow(string root, string full)
    {
        string at = root;
        foreach (string part in full[(root.Length + 1)..].Split('\\'))
        {
            at = Path.Combine(at, part);
            int err = NoFollow.CreateDirectoryOnly(at);
            if (err != 0 && err != NoFollow.ERROR_ALREADY_EXISTS)
                throw new IOException($"PoolWatch could not create {at} (error {err}).");
        }
    }

    /// <summary>
    /// The folders retention looks in, each with the name its things' records start with (<see cref="RetentionClock.Seen"/>):
    /// the daily data folders, the incidents and the exports, all in DataRoot.
    /// </summary>
    private (string Name, string Path)[] RetentionRoots =>
        [("data", Path.Combine(DataRoot, "data")), ("incidents", Path.Combine(DataRoot, "incidents")), ("exports", ExportsDir)];

    /// <summary>
    /// Where the retention clock keeps its state (<see cref="RetentionClock.State"/>): the data folder's own cache, also in a
    /// headless run that writes elsewhere with --out, as the folders it judges are the data folder's.
    /// </summary>
    internal string RetentionStateFile => Path.Combine(DataRoot, "cache", "retention.json");

    /// <summary>
    /// The retention clock for this start of PoolWatch, counting on <paramref name="forwardMs"/> (the PC's working-time
    /// timer, <see cref="Native.WorkingTime.Ms"/>), from the state its last save kept (read bounded, never through a link).
    /// With none that can be read, everything retention judges is seen for the first time at the next check. Its saves go
    /// to <see cref="RetentionStateFile"/> in one step (<see cref="DurableFile"/>).
    /// </summary>
    public RetentionClock OpenRetentionClock(Func<long> forwardMs, Action<string> log)
    {
        RetentionClock.State? saved = null;
        try
        {
            string file = Guard.CheckReal(RetentionStateFile);
            if (File.Exists(file) && new FileInfo(file).Length <= RetentionClock.MaxJsonChars) saved = RetentionClock.State.Parse(NoWaitOpen.ReadAllText(file));
        }
        catch (Exception ex) { log($"retention: could not read {RetentionStateFile}: {ex.Message}"); }
        if (saved is null) log("retention: no saved retention clock: what is there now is kept for the whole retention period from the next check");
        return new RetentionClock(forwardMs, saved, state =>
        {
            try
            {
                EnsureDir(Path.Combine(DataRoot, "cache"));
                DurableFile.Replace(Guard, RetentionStateFile, state.ToJson());
            }
            catch (Exception ex) { log($"retention: could not save {RetentionStateFile}: {ex.Message}"); throw; }
        });
    }

    /// <summary>
    /// What retention said it could not do, by the folder or thing it was about (a root it could not list, a thing it could
    /// not read whole, one it could not remove), so each is logged once while it lasts, and again only after it could be
    /// done once more. Retention runs on one thread (the sampler's).
    /// </summary>
    private readonly HashSet<string> _retentionSaid = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The things retention found due and could not delete whole in this run of PoolWatch, each with what was in it when it
    /// was found due (<see cref="RetentionClock.Entry"/>: every file and folder in it but a link), so the rest of it stays
    /// due while it holds only that (<see cref="RetentionClock.StillDue"/>). After a restart of PoolWatch such a rest is
    /// seen anew, so kept longer, never shorter.
    /// </summary>
    private readonly Dictionary<string, List<RetentionClock.Entry>> _retentionRemoving = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Deletes the daily data folders, incidents and exports (and the files directly in those folders) that retention
    /// has kept as they are for longer than the retention period, only inside DataRoot and never through a link. It
    /// lists them all first (<paramref name="list"/>, Windows' listing by default), each with its stamp
    /// (<see cref="StampOf"/>: for a folder that is not a link, also the name, length and last-write time of everything
    /// in it but a link, so rows added to its files count as a write into it), then asks <paramref name="clock"/>
    /// (<see cref="RetentionClock.Check"/>), which counts on the PC's working-time timer from the check that first saw
    /// each as it is, never on the system clock, so nothing is deleted while it or anything in it was written within
    /// the retention period of that timer, whatever the system clock did. One whose stamp cannot be read
    /// (<paramref name="lastWriteUtc"/>, Windows' by default, for any part of it) is kept, and judged again at the next
    /// check. In a folder that cannot be listed (when Windows refuses the listing, say), each thing the
    /// clock has a record of is still judged by its own path in that folder (<see cref="RetentionClock.NamesIn"/>, a
    /// plain name only, <see cref="RecordedPath"/>), so what is due there still goes, and the record of one no longer at
    /// its path goes (<see cref="RetentionClock.Forget"/>); the records of the rest are kept, and a thing not recorded yet
    /// is first seen once the folder can be listed (kept longer, never shorter). Then it deletes what the clock named. A
    /// folder that could not be deleted whole (a file in it in use, say) stays due: at a later check, while what is left
    /// of it holds only what was in it when it was found due (<see cref="OnlyWhatWasThere"/>), the rest is tried again,
    /// not counted from 0; anything written into it since counts it anew. Each folder that cannot be listed, thing that
    /// cannot be read whole and thing that cannot be removed is logged once until that can be done again. Then it saves.
    /// </summary>
    public void ApplyRetention(int days, Action<string> log, RetentionClock clock, Func<string, bool, DateTime>? lastWriteUtc = null,
        Func<string, bool, IEnumerable<string>>? list = null)
    {
        list ??= (root, dirs) => dirs ? Directory.EnumerateDirectories(root) : Directory.EnumerateFiles(root);
        var removed = new List<string>();
        var seen = new List<RetentionClock.Seen>();
        var where = new Dictionary<string, (string Path, bool Dir)>(StringComparer.OrdinalIgnoreCase);
        var inside = new Dictionary<string, List<RetentionClock.Entry>>(StringComparer.OrdinalIgnoreCase);
        var notListedWhole = new List<string>();
        foreach (var (name, root) in RetentionRoots)
        {
            try
            {
                if (!Directory.Exists(root))
                {
                    notListedWhole.Add(name); // missing, or not seen: what was there keeps its record
                    continue;
                }
                foreach (var dir in list(root, true)) Add(name, dir, dir: true);
                foreach (var file in list(root, false)) Add(name, file, dir: false);
                _retentionSaid.Remove("list " + root);
            }
            catch (Exception ex)
            {
                notListedWhole.Add(name);
                Once("list " + root, $"retention: could not list {root}: {ex.Message}: what retention has a record of in it is judged by its own path, " +
                                     "and the rest once it can be listed (logged once until then)");
                // What the clock already has a record of in it is judged by its own path: what is due there can still go,
                // and the record of one that is not there any more goes.
                foreach (string known in clock.NamesIn(name))
                    if (!where.ContainsKey(known) && RecordedPath(root, name, known) is { } path)
                    {
                        if (Directory.Exists(path)) Add(name, path, dir: true);
                        else if (File.Exists(path)) Add(name, path, dir: false);
                        else clock.Forget(known);
                    }
            }
        }
        // The rest of a thing that could not be deleted whole stays due while it holds only what was in it then.
        var stamps = seen.Where(s => s.Stamp is not null).ToDictionary(s => s.Name, s => s.Stamp!.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, was) in _retentionRemoving.ToList())
        {
            if (stamps.TryGetValue(name, out long stamp) && inside.TryGetValue(name, out var now))
            {
                if (OnlyWhatWasThere(now, was)) clock.StillDue(name, stamp);
                else _retentionRemoving.Remove(name); // written into since: counted anew
            }
            else if (!where.ContainsKey(name) && !notListedWhole.Contains(name[..name.IndexOf('\\')], StringComparer.OrdinalIgnoreCase))
                _retentionRemoving.Remove(name); // not there any more
        }
        foreach (var name in clock.Check(days, seen, notListedWhole))
        {
            var (path, dir) = where[name];
            try
            {
                // CheckReal refuses if this folder (or any folder above it) is a link, so a planted junction
                // can never turn a retention sweep into a recursive delete of something outside the data root.
                Guard.CheckReal(path);
                if (dir) DeleteTree(path);
                else File.Delete(path);
                _retentionRemoving.Remove(name);
                _retentionSaid.Remove("remove " + path);
                removed.Add(name);
            }
            catch (Exception ex)
            {
                // What is left of a folder was all in it when it was found due: it stays due (above, at the next check).
                if (dir && inside.TryGetValue(name, out var was)) _retentionRemoving[name] = was;
                Once("remove " + path, $"retention: could not remove {path}: {ex.Message}: what is left of it is tried again at each check (logged once until it is removed)");
            }
        }
        clock.Save();
        log(RetentionSummary(days, seen.Count, removed));

        void Add(string rootName, string path, bool dir)
        {
            string name = rootName + "\\" + Path.GetFileName(path);
            long? stamp;
            var entries = new List<RetentionClock.Entry>();
            try
            {
                stamp = StampOf(path, dir, lastWriteUtc, entries);
                _retentionSaid.Remove("read " + path);
            }
            catch (Exception ex)
            {
                stamp = null; // kept, and judged again at the next check
                Once("read " + path, $"retention: could not read {path} whole: {ex.Message}: it is kept, and judged again at each check (logged once until it can be read)");
            }
            seen.Add(new RetentionClock.Seen(name, stamp));
            where[name] = (path, dir);
            if (dir && stamp is not null) inside[name] = entries;
        }

        void Once(string key, string line)
        {
            if (_retentionSaid.Add(key)) log(line);
        }
    }

    /// <summary>The names retention's one line a check names (the rest are counted).</summary>
    internal const int SummaryNames = 5;

    /// <summary>
    /// A retention check in one log line: how many things it judged and how many it removed, by folder, with the first
    /// <see cref="SummaryNames"/> removed by name ("retention: removed 1,137 of 15,230 things (incidents 1,130, data 7) kept
    /// unchanged for over 7 days of the PC's awake time: incidents\20261102-101010-SpawnStorm-bash, ... and 1,132 more").
    /// </summary>
    internal static string RetentionSummary(int days, int judged, IReadOnlyList<string> removed)
    {
        if (removed.Count == 0) return $"retention: {judged:N0} things judged, none kept unchanged for over {days} days of the PC's awake time";
        var byFolder = removed.GroupBy(n => n[..Math.Max(0, n.IndexOf('\\'))], StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count():N0}");
        string names = string.Join(", ", removed.Take(SummaryNames)) + (removed.Count > SummaryNames ? $" and {removed.Count - SummaryNames:N0} more" : "");
        return $"retention: removed {removed.Count:N0} of {judged:N0} things ({string.Join(", ", byFolder)}) kept unchanged for over {days} days of the PC's awake time: {names}";
    }

    /// <summary>
    /// Deletes the folder <paramref name="folder"/> with everything in it, never through a link: a link in it (a junction,
    /// a symbolic link) is deleted itself, its own entry, never what it leads to, and nothing is listed through it. Each
    /// file and folder in it is deleted even when one before it could not be (the first error is thrown once all that can
    /// go is gone, and the folder itself is then left). Unlike <see cref="Directory.Delete(string, bool)"/> with
    /// recursive, it never looks the folder up in the folder above it, which that does first: so while that folder above
    /// cannot be listed, what is in this folder still goes, and only the folder's own removal may be refused until then.
    /// </summary>
    internal static void DeleteTree(string folder)
    {
        Exception? first = null;
        var all = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, MatchType = MatchType.Win32, RecurseSubdirectories = false };
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", all))
        {
            try
            {
                bool link = entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
                if (entry is DirectoryInfo && !link) DeleteTree(entry.FullName);
                else if (entry is DirectoryInfo) Directory.Delete(entry.FullName, recursive: false); // the link's own entry
                else File.Delete(entry.FullName);
            }
            catch (Exception ex) { first ??= ex; }
        }
        if (first is not null) throw first;
        Directory.Delete(folder, recursive: false);
    }

    /// <summary>
    /// The path in the retention folder <paramref name="root"/> (named <paramref name="rootName"/>) of a thing the clock
    /// recorded as <paramref name="name"/> (<c>data\20261024</c>): only a plain file or folder name below that folder, as a
    /// listing gives one (no other folder, no '.' or '..', no stream or drive, nothing Windows would read as another name);
    /// null for anything else, which is never looked at, so nothing outside the retention folders is judged or deleted.
    /// </summary>
    internal static string? RecordedPath(string root, string rootName, string name)
    {
        if (!name.StartsWith(rootName + "\\", StringComparison.OrdinalIgnoreCase)) return null;
        string leaf = name[(rootName.Length + 1)..];
        bool plain = leaf.Length > 0 && leaf is not ("." or "..") && leaf.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && leaf.IndexOfAny(['\\', '/', ':']) < 0
                     && !leaf.EndsWith('.') && !leaf.EndsWith(' ') && Path.GetFileName(leaf) == leaf;
        return plain ? Path.Combine(root, leaf) : null;
    }

    /// <summary>
    /// Whether what is in a folder now (<paramref name="now"/>) is only what was in it when it was found due
    /// (<paramref name="was"/>): each file with the same name, length and last-write time, and each folder by its name (a
    /// folder's own time changes as what was in it is deleted). Then nothing was written into it since.
    /// </summary>
    internal static bool OnlyWhatWasThere(IReadOnlyList<RetentionClock.Entry> now, IReadOnlyList<RetentionClock.Entry> was)
    {
        var files = was.Where(e => e.Length >= 0).ToHashSet();
        var folders = was.Where(e => e.Length < 0).Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        return now.All(e => e.Length >= 0 ? files.Contains(e) : folders.Contains(e.Name));
    }

    /// <summary>How a folder's contents are listed for its stamp: every file and folder, hidden and system ones included, a link left out (nothing is read through one), and a folder that cannot be listed an error.</summary>
    private static readonly EnumerationOptions Contents = new() { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false, MatchType = MatchType.Win32 };

    /// <summary>
    /// The stamp retention judges a thing by (<see cref="RetentionClock.Stamp"/>): its own last-write time (<paramref
    /// name="lastWriteUtc"/>) and length, and for a folder the name below it, the length (a file's, read from the file
    /// itself, so a file being written shows its size now) and the last-write time of every file and folder in it at any
    /// depth (<see cref="Contents"/>: a link and what is behind it are left out). A folder that is itself a link (a
    /// junction in a daily folder's place, say) is stamped by its own last-write time alone, as Windows gives it for the
    /// link, so nothing is read through it either; retention never deletes one (<see cref="PathGuard.CheckReal"/> refuses
    /// a link). A file that is a link gives its own time and length, as Windows reads them for the link. Null when a
    /// last-write time is Windows' value for a path that is gone (1601-01-01); throws when any of it cannot be read or
    /// listed. For a folder, what it holds is added to <paramref name="inside"/> when one is given.
    /// </summary>
    /// <param name="lastWriteUtc">A path's last-write time (a test gives its own); null: Windows', read with the file's length in one
    /// query of the file itself (one per file, where reading the time and the length apart took two).</param>
    internal static long? StampOf(string path, bool dir, Func<string, bool, DateTime>? lastWriteUtc, List<RetentionClock.Entry>? inside = null)
    {
        if (!dir) return FileStamp(path) is { } f ? RetentionClock.Stamp(f.written, f.length, []) : null;
        if (Written(path, true) is not { } own) return null;
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) return RetentionClock.Stamp(own, -1, []);
        inside ??= new List<RetentionClock.Entry>();
        var folders = new Stack<string>();
        folders.Push(path);
        while (folders.TryPop(out var folder))
        {
            // One listing of the folder for its folders and files (a link left out); each file's time and length are read
            // from the file itself, as the listing may not yet show what the CSV sink added to a file it holds open.
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", Contents))
            {
                if (entry is DirectoryInfo)
                {
                    if (Written(entry.FullName, true) is not { } written) return null;
                    inside.Add(new(Path.GetRelativePath(path, entry.FullName), -1, written));
                    folders.Push(entry.FullName);
                }
                else
                {
                    if (FileStamp(entry.FullName) is not { } f) return null;
                    inside.Add(new(Path.GetRelativePath(path, entry.FullName), f.length, f.written));
                }
            }
        }
        return RetentionClock.Stamp(own, -1, inside);

        // Windows gives 1601-01-01 for a path that is gone: not read.
        long? Written(string at, bool folder)
        {
            var utc = lastWriteUtc is null ? (folder ? Directory.GetLastWriteTimeUtc(at) : File.GetLastWriteTimeUtc(at)) : lastWriteUtc(at, folder);
            return utc == DateTime.FromFileTimeUtc(0) ? null : utc.Ticks;
        }

        // A file's time and length, from one query of the file (FileInfo reads both at once) unless a test gives the time.
        (long written, long length)? FileStamp(string file)
        {
            var info = new FileInfo(file);
            var utc = lastWriteUtc is null ? info.LastWriteTimeUtc : lastWriteUtc(file, false);
            if (utc == DateTime.FromFileTimeUtc(0)) return null;
            return (utc.Ticks, info.Length);
        }
    }
}
