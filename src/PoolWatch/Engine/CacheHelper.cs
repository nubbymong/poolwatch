using System.Security.Principal;
using PoolWatch.Core.Cache;
using PoolWatch.Native;

namespace PoolWatch.Engine;

/// <summary>
/// "Clear cache" (addendum 2). The standby purge needs SeProfileSingleProcessPrivilege (administrators), and PoolWatch
/// always runs elevated (its manifest asks for it), so it purges in-process: Clear cache now, the automatic rule and the
/// "lag before / after a clear" experiment alike.
/// <para>
/// S5 (review round 2, decision reversed): the SYSTEM-task route is removed. It existed for an unelevated PoolWatch, which
/// never runs: a one-time setup wrote a helper DLL into %ProgramFiles%\PoolWatch and registered two on-demand SYSTEM tasks
/// in \PoolWatch\ that a named standard user could start. Both review rounds recommended removing it (less attack surface:
/// a SYSTEM payload startable by a non-administrator, plus the setup that wrote it). Nothing creates those tasks any more;
/// <see cref="RemoveOldTasks"/> removes what a previous build installed: automatically on the first elevated start that finds
/// it (review round 3, <see cref="OldTaskCleanup"/>), else <c>--remove-clear-cache</c> or the toolbar button that appears when
/// they are found.
/// A clear task of your own (a task in the Task Scheduler root that runs Sysinternals RAMMap with one of its -E "empty"
/// options as SYSTEM or with highest privileges, under any name: <see cref="IsOwnClearAction"/>) is shown, and removed only
/// on a yes, so it and the automatic clear never both run. PoolWatch never ships, starts or changes RAMMap itself.
/// </para>
/// </summary>
internal static class CacheHelper
{
    public const string Folder = @"\PoolWatch";
    /// <summary>The two tasks a previous build's setup registered (only ever removed now).</summary>
    public const string StandbyTask = "Clear cache (standby)";
    public const string LowPriorityTask = "Clear cache (low priority)";
    /// <summary>What the old setup wrote into %ProgramFiles%\PoolWatch; only these names are ever removed (never the folder
    /// while anything else is in it: PoolWatch.exe is installed there now).</summary>
    private static readonly string[] OldHelperFiles = ["PoolWatch-CacheHelper.dll", "PoolWatch-CacheHelper.runtimeconfig.json", "PoolWatch-CacheHelper.exe"];

    public static string OldHelperDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PoolWatch");

    public static bool IsElevated
    {
        get { try { using var id = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); } catch { return false; } }
    }

    // ---------------------------------------------------------------- the purge (in-process)

    /// <summary>The purge, in-process. Null on success, else the reason. Unelevated it is refused (PoolWatch is not meant to run so).</summary>
    public static string? Run(ClearMode mode)
    {
        if (!IsElevated) return "Clear cache needs PoolWatch to run as administrator (it does when started normally)";
        int st = MemoryNative.Purge(mode);
        return st == 0 ? null : $"the purge returned 0x{st:X8}";
    }

    // ---------------------------------------------------------------- status

    /// <param name="CanPurge">PoolWatch runs elevated: the clear works, in-process.</param>
    /// <param name="LegacyTaskPresent">A clear task of your own exists (<see cref="FindLegacy"/>); null when it could not be read.</param>
    /// <param name="LegacyTaskText">What it is (several are joined with "; ").</param>
    /// <param name="OldTasks">The SYSTEM tasks a previous build's setup installed and that are still there (to remove).</param>
    public sealed record Status(bool CanPurge, bool? LegacyTaskPresent, string? LegacyTaskText, IReadOnlyList<string> OldTasks, string? Note)
    {
        public bool OldTasksPresent => OldTasks.Count > 0;
    }

    public static Status Query()
    {
        if (!IsElevated) return new Status(false, null, null, [], "PoolWatch is not running as administrator: Clear cache needs it");
        List<LegacyTaskInfo> own;
        try { own = FindLegacy(); }
        catch (Exception ex) { return new Status(true, null, null, [], "Task Scheduler unavailable: " + ex.Message); }
        List<string> old;
        try { old = FindOldTasks(); } catch { old = []; }
        return new Status(true, own.Count > 0, own.Count > 0 ? string.Join("; ", own.Select(t => t.Text)) : null, old, null);
    }

    /// <summary>
    /// The automatic clear may run: PoolWatch can purge, and no clear task of your own is there (the two must not both
    /// run). Before the first status has been read (null) it waits.
    /// </summary>
    public static bool AutoClearAllowed(Status? s) => s is { CanPurge: true, LegacyTaskPresent: not true };

    // ---------------------------------------------------------------- what a previous build installed (removal only)

    /// <summary>The only task names the removal ever touches (in \PoolWatch\); never a task of your own in the root.</summary>
    public static readonly IReadOnlyList<string> OldTaskNames = [StandbyTask, LowPriorityTask];

    /// <summary>
    /// Review round 3 (item 7): on an elevated start that finds an older build's two tasks, they are removed automatically
    /// (no longer used: the purge is in-process), with a line for the timeline; the toolbar button stays as the fallback.
    /// Null when there was nothing to do. <paramref name="remove"/> is <see cref="RemoveOldTasks"/> (a fake in tests).
    /// </summary>
    public static string? RemoveOldTasksAutomatically(Status s, Func<List<string>> remove)
    {
        if (!s.CanPurge || !s.OldTasksPresent) return null;
        const string fallback = "toolbar: Remove old clear-cache tasks";
        try
        {
            var removed = remove();
            return removed.Count == 0
                ? $"The Clear cache tasks an older PoolWatch installed could not be removed automatically ({string.Join(", ", s.OldTasks)}); {fallback}."
                : $"Removed the Clear cache tasks and helper an older PoolWatch installed (no longer used: PoolWatch clears the cache itself): " +
                  $"{string.Join("; ", removed)}. Your own clear task, if you have one, was not touched.";
        }
        catch (Exception ex) { return $"The Clear cache tasks an older PoolWatch installed could not be removed automatically ({ex.Message}); {fallback}."; }
    }

    /// <summary>
    /// The status query of one PoolWatch start: the first query that finds the old tasks (elevated) removes them, once, and
    /// reads the status again; the line for the timeline waits in <see cref="Notes"/>. Never a clear task of your own.
    /// </summary>
    internal sealed class OldTaskCleanup(Func<Status> query, Func<List<string>> remove)
    {
        private int _tried;
        public System.Collections.Concurrent.ConcurrentQueue<string> Notes { get; } = new();

        public Status Query()
        {
            var s = query();
            if (s.CanPurge && s.OldTasksPresent && Interlocked.Exchange(ref _tried, 1) == 0
                && RemoveOldTasksAutomatically(s, remove) is { } note)
            {
                Notes.Enqueue(note);
                s = query();
            }
            return s;
        }
    }

    /// <summary>The old tasks still in \PoolWatch\ (only those that run the old helper), by name.</summary>
    public static List<string> FindOldTasks()
    {
        var found = new List<string>();
        using var com = new ComScope();
        dynamic service = com.Track(Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!)!;
        service.Connect();
        dynamic? folder;
        try { folder = com.Track(service.GetFolder(Folder)); } catch { return found; }
        foreach (var name in OldTaskNames)
            if (com.Track(TryGet(folder!, name)) is { } t && RunsOldHelper(com, t)) found.Add(name);
        return found;
    }

    /// <summary>Whether a task's action runs the old helper (a task of the same name that does something else is left alone).</summary>
    private static bool RunsOldHelper(ComScope com, dynamic task)
    {
        try
        {
            dynamic def = com.Track(task.Definition)!;
            foreach (dynamic a in com.Each(def.Actions))
                if ((int)a.Type == 0 && ((string)(a.Path ?? "") + " " + (string)(a.Arguments ?? "")).Contains("PoolWatch-CacheHelper", StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Removes what a previous build's Clear cache setup installed: the two \PoolWatch\ tasks (only when they run the old
    /// helper), the \PoolWatch\ folder when that leaves it empty, and the helper files in %ProgramFiles%\PoolWatch (never
    /// that folder while anything else is in it). Never a clear task of your own. Elevated. Returns what was removed.
    /// </summary>
    public static List<string> RemoveOldTasks()
    {
        if (!IsElevated) throw new UnauthorizedAccessException("removing the old Clear cache tasks must run elevated");
        var removed = new List<string>();
        using (var com = new ComScope())
        {
            dynamic service = com.Track(Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!)!;
            service.Connect();
            dynamic? folder = null;
            try { folder = com.Track(service.GetFolder(Folder)); } catch { }
            if (folder is not null)
            {
                foreach (var name in OldTaskNames)
                    if (com.Track(TryGet(folder, name)) is { } t && RunsOldHelper(com, t))
                        try { folder.DeleteTask(name, 0); removed.Add($@"{Folder}\{name}"); } catch { }
                try { com.Track(service.GetFolder(@"\"))!.DeleteFolder(Folder.TrimStart('\\'), 0); removed.Add(Folder + @"\ (the empty task folder)"); }
                catch { } // not empty (another task is in it): it stays
            }
        }
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var f in OldHelperFiles)
        {
            string p = Path.Combine(OldHelperDir, f);
            try
            {
                if (File.Exists(p) && Core.Util.PathGuard.FirstLinkBelow(programFiles, p) is null) { File.Delete(p); removed.Add(p); }
            }
            catch { }
        }
        try { if (Directory.Exists(OldHelperDir) && !Directory.EnumerateFileSystemEntries(OldHelperDir).Any()) Directory.Delete(OldHelperDir); } catch { }
        return removed;
    }

    // ---------------------------------------------------------------- a clear task of your own (elevated)

    /// <summary>Sysinternals RAMMap's file names (x64, x86 and Arm64 builds).</summary>
    private static readonly System.Text.RegularExpressions.Regex RamMapExe =
        new(@"^RAMMap(64a?)?\.exe$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>RAMMap's "empty" options: -Ew working sets, -Es system working set, -Em modified list, -Et standby list, -E0 priority-0 standby.</summary>
    private static readonly System.Text.RegularExpressions.Regex EmptyOption =
        new(@"(?<![\w-])[-/]E[wsmt0](?!\w)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Pure: a task action that clears memory lists the way PoolWatch's own clear does: the program is Sysinternals RAMMap
    /// (RAMMap.exe, RAMMap64.exe or RAMMap64a.exe, in any folder) and the arguments hold one of its -E options. The task's
    /// name does not matter.
    /// </summary>
    public static bool IsOwnClearAction(string? program, string? arguments)
    {
        if (string.IsNullOrWhiteSpace(program)) return false;
        string p = program.Trim().Trim('"');
        int cut = p.LastIndexOfAny(['\\', '/']);
        return RamMapExe.IsMatch(cut >= 0 ? p[(cut + 1)..] : p) && EmptyOption.IsMatch(arguments ?? "");
    }

    /// <summary>A clear task of your own as you would recognise it: where, as whom, what it runs, when.</summary>
    public sealed record LegacyTaskInfo(string Path, string RunsAs, string Runs, string When, bool Enabled)
    {
        public string Text => $"{Path}: runs {Runs} as {RunsAs}, {When}, {(Enabled ? "enabled" : "disabled")}";
    }

    /// <summary>At most this many tasks of your own are reported (the root folder is read once an hour).</summary>
    private const int MaxOwnTasks = 20;

    /// <summary>
    /// Every clear task of your own: a task in the Task Scheduler root folder, run as SYSTEM or with highest privileges
    /// (a standard user's task cannot clear anything), with an action that <see cref="IsOwnClearAction"/>. Elevated: a
    /// SYSTEM task is visible only then.
    /// </summary>
    public static List<LegacyTaskInfo> FindLegacy()
    {
        var found = new List<LegacyTaskInfo>();
        using var com = new ComScope();
        dynamic service = com.Track(Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!)!;
        service.Connect();
        dynamic root = com.Track(service.GetFolder(@"\"))!;
        foreach (dynamic t in com.Each(root.GetTasks(1))) // TASK_ENUM_HIDDEN: hidden tasks too
        {
            if (found.Count >= MaxOwnTasks) break;
            LegacyTaskInfo? info = Describe(com, (object)t);
            if (info is not null) found.Add(info);
        }
        return found;
    }

    /// <summary>The task as a clear task of your own, or null when it is not one (or cannot be read).</summary>
    private static LegacyTaskInfo? Describe(ComScope com, object task)
    {
        dynamic t = task;
        dynamic? def;
        try { def = com.Track(t.Definition); } catch { return null; }
        if (def is null) return null;
        bool clears = false;
        var acts = new List<string>();
        try
        {
            foreach (dynamic a in com.Each(def.Actions))
            {
                if ((int)a.Type != 0) { acts.Add("(not a program)"); continue; }
                string path = (string)(a.Path ?? ""), args = (string)(a.Arguments ?? "");
                clears |= IsOwnClearAction(path, args);
                acts.Add((path + " " + args).Trim());
            }
        }
        catch { return null; }
        if (!clears) return null;
        string runsAs = "?";
        bool highest = false;
        try
        {
            dynamic principal = com.Track(def.Principal)!;
            string u = (string)(principal.UserId ?? "");
            runsAs = u is "S-1-5-18" or "SYSTEM" or "NT AUTHORITY\\SYSTEM" or "LocalSystem" ? "SYSTEM" : u;
            highest = (int)principal.RunLevel == 1; // TASK_RUNLEVEL_HIGHEST
        }
        catch { }
        if (runsAs != "SYSTEM" && !highest) return null; // it could not clear anything
        bool enabled = false;
        try { enabled = t.Enabled; } catch { }
        string when = "no trigger";
        try
        {
            int n = (int)com.Track(def.Triggers)!.Count;
            if (n > 0) when = $"{n} trigger{(n == 1 ? "" : "s")}, next run {t.NextRunTime}";
        }
        catch { }
        string taskPath;
        try { taskPath = (string)t.Path; } catch { return null; }
        return new LegacyTaskInfo(taskPath, runsAs, string.Join("; ", acts), when, enabled);
    }

    /// <summary>
    /// Deletes only the task (not what it runs: RAMMap stays where it is), and only when it is still a clear task of your
    /// own (checked again by name right before the delete). Elevated.
    /// </summary>
    public static void DeleteLegacy(LegacyTaskInfo task)
    {
        if (!IsElevated) throw new UnauthorizedAccessException("removing the task must run elevated");
        string name = task.Path.TrimStart('\\');
        if (name.Length == 0 || name.Contains('\\')) throw new ArgumentException("only a task in the Task Scheduler root folder is removed", nameof(task));
        using var com = new ComScope();
        dynamic service = com.Track(Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!)!;
        service.Connect();
        dynamic root = com.Track(service.GetFolder(@"\"))!;
        object? t = com.Track(TryGet(root, name));
        if (t is null || Describe(com, t) is null) return; // gone, or no longer a clear task
        root.DeleteTask(name, 0);
    }

    public enum LegacyOutcome { None, Removed, Kept }

    /// <summary>
    /// The step behind "turn on the automatic clear" (and "Remove my clear task"): finds every clear task of your own,
    /// shows them, and deletes them only when <paramref name="ask"/> says yes, so they and the built-in rule never both
    /// run. <see cref="LegacyOutcome.Kept"/>: the caller leaves the automatic clear off.
    /// </summary>
    public static (LegacyOutcome Outcome, IReadOnlyList<LegacyTaskInfo> Tasks) ResolveLegacy(Func<IReadOnlyList<LegacyTaskInfo>, bool> ask)
    {
        if (!IsElevated) throw new UnauthorizedAccessException("a clear task usually runs as SYSTEM: finding it must run elevated");
        var tasks = FindLegacy();
        if (tasks.Count == 0) return (LegacyOutcome.None, tasks);
        if (!ask(tasks)) return (LegacyOutcome.Kept, tasks);
        foreach (var t in tasks) DeleteLegacy(t);
        return (LegacyOutcome.Removed, tasks);
    }

    /// <summary>The question the step shows.</summary>
    public static string LegacyQuestion(IReadOnlyList<LegacyTaskInfo> tasks) =>
        $"{(tasks.Count == 1 ? "A clear task of your own is" : "Clear tasks of your own are")} set up:\n\n{string.Join("\n", tasks.Select(t => t.Text))}\n\n" +
        "PoolWatch's automatic clear does the same job; the two should not both run. " +
        $"Remove {(tasks.Count == 1 ? "this task" : "these tasks")} now? (Only the task is removed; the program it runs, RAMMap, stays where it is.)\n\n" +
        $"No: the task{(tasks.Count == 1 ? "" : "s")} stay{(tasks.Count == 1 ? "s" : "")} and PoolWatch's automatic clear stays off.";

    /// <summary>One line for the console or the log: the tasks' paths.</summary>
    public static string LegacyNames(IReadOnlyList<LegacyTaskInfo> tasks) => string.Join(", ", tasks.Select(t => t.Path));

    private static dynamic? TryGet(dynamic folder, string name)
    {
        try { return folder.GetTask(name); } catch { return null; }
    }
}

/// <summary>
/// Releases the Task Scheduler COM objects a call touched as soon as it is done. Left to the finalizer, each status
/// query kept its RPC handles and native buffers alive (the UI soak's own handles and private memory crept up).
/// </summary>
internal sealed class ComScope : IDisposable
{
    private readonly List<object> _objects = new();

    public dynamic? Track(object? o)
    {
        if (o is not null && System.Runtime.InteropServices.Marshal.IsComObject(o)) _objects.Add(o);
        return o;
    }

    /// <summary>
    /// C6 (review round 2): enumerates a COM collection and tracks every item AND the enumerator behind the
    /// <c>foreach</c> (the collection's IEnumVARIANT, wrapped by the runtime), which a plain <c>foreach (dynamic x in c)</c>
    /// left to the finalizer.
    /// </summary>
    public IEnumerable<dynamic> Each(dynamic collection)
    {
        Track((object)collection);
        System.Collections.IEnumerable items = collection;
        var e = items.GetEnumerator();
        try
        {
            while (e.MoveNext())
            {
                object? item = e.Current;
                Track(item);
                if (item is not null) yield return item;
            }
        }
        finally
        {
            // The runtime's adapter holds the IEnumVARIANT; release that, not just the adapter.
            Track(e is System.Runtime.InteropServices.ICustomAdapter adapter ? adapter.GetUnderlyingObject() : e);
            (e as IDisposable)?.Dispose();
        }
    }

    public void Dispose()
    {
        for (int i = _objects.Count - 1; i >= 0; i--)
            try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(_objects[i]); } catch { }
        _objects.Clear();
    }
}
