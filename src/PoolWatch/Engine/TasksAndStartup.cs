using System.Text.Json;
using Microsoft.Win32;
using PoolWatch.Core.Services;
using PoolWatch.Core.Util;
using PoolWatch.Engine.Changes;

namespace PoolWatch.Engine;

/// <summary>A non-Microsoft scheduled task the owner may disable (addendum 4 C).</summary>
internal sealed record ScheduledTaskView(string Path, string Name, string Author, bool Enabled, string Runs, string NextRun);

/// <summary>A startup entry (Run key or Startup folder), with Task Manager's enabled state.</summary>
internal sealed record StartupView(string Name, string Command, string Location, bool Enabled, bool Microsoft);

/// <summary>
/// Scheduled tasks and startup apps (addendum 4 C): enumerate the non-Microsoft ones, and enable/disable them
/// reversibly — a task through its Enabled property, a startup entry through the same Explorer StartupApproved bytes
/// Task Manager uses, so the owner can flip it back there too. Microsoft's own tasks and entries are left alone.
/// Put back under Active changes flips them back through <see cref="ITaskToggles"/>.
/// </summary>
internal sealed class TasksAndStartup : ITaskToggles
{
    private readonly Log _log;
    public TasksAndStartup(Log log) => _log = log;

    // ---- scheduled tasks (Task Scheduler COM)
    // C6 (review): every COM object a call touches (the service, folders, task and folder collections, each task, its
    // definition, registration info, actions and each action) is tracked by a ComScope and released when the call ends.
    // Left to the finalizer they kept RPC handles and native buffers alive: the leak ComScope exists to fix.
    // Review round 2: the collections are walked with ComScope.Each, which also releases the enumerator behind each
    // foreach (the collection's IEnumVARIANT); a plain foreach (dynamic ...) left those to the finalizer.

    public List<ScheduledTaskView> Tasks()
    {
        var list = new List<ScheduledTaskView>();
        try
        {
            using var com = new ComScope();
            dynamic svc = com.Track(Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!)!;
            svc.Connect();
            Walk(com, com.Track(svc.GetFolder("\\"))!, list);
        }
        catch (Exception ex) { _log.Info("scheduled tasks not read: " + ex.Message); }
        return list;
    }

    private void Walk(ComScope com, dynamic folder, List<ScheduledTaskView> list, int depth = 0)
    {
        string path = folder.Path;
        if (path.StartsWith("\\Microsoft", StringComparison.OrdinalIgnoreCase)) return; // Microsoft's own, left alone
        try
        {
            foreach (dynamic t in com.Each(folder.GetTasks(1 /* TASK_ENUM_HIDDEN */)))
            {
                string author = "";
                string runs = "";
                dynamic? def = null;
                try { def = com.Track(t.Definition); } catch { }
                try { author = com.Track(def!.RegistrationInfo)!.Author ?? ""; } catch { }
                if (author.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    foreach (dynamic a in com.Each(def!.Actions))
                        if ((int)a.Type == 0) { runs = ((string)a.Path + " " + (string)a.Arguments).Trim(); break; }
                }
                catch { }
                list.Add(new ScheduledTaskView((string)t.Path, (string)t.Name, author, (bool)t.Enabled, runs, SafeTime(() => (DateTime)t.NextRunTime)));
            }
        }
        catch { }
        if (depth < 4)
            try
            {
                foreach (dynamic sub in com.Each(folder.GetFolders(0))) Walk(com, sub, list, depth + 1);
            }
            catch { }
    }

    private static string SafeTime(Func<DateTime> f) { try { var t = f(); return t.Year > 1900 ? t.ToString("g") : ""; } catch { return ""; } }

    /// <summary>Enable or disable a task (reversible). Microsoft tasks are refused.</summary>
    public string? SetTaskEnabled(string path, bool enabled)
    {
        if (path.StartsWith("\\Microsoft", StringComparison.OrdinalIgnoreCase)) return "a Microsoft task is left alone";
        try
        {
            using var com = new ComScope();
            dynamic svc = com.Track(Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!)!;
            svc.Connect();
            int slash = path.LastIndexOf('\\');
            dynamic folder = com.Track(svc.GetFolder(slash <= 0 ? "\\" : path[..slash]))!;
            dynamic task = com.Track(folder.GetTask(path[(slash + 1)..]))!;
            task.Enabled = enabled;
            _log.Info($"scheduled task {(enabled ? "enabled" : "disabled")}: {path}");
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Whether the task is still there. Throws when Task Scheduler cannot be read (that is not "gone").</summary>
    public bool TaskExists(string path)
    {
        using var com = new ComScope();
        dynamic svc = com.Track(Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!)!;
        svc.Connect();
        int slash = path.LastIndexOf('\\');
        try
        {
            dynamic folder = com.Track(svc.GetFolder(slash <= 0 ? "\\" : path[..slash]))!;
            com.Track(folder.GetTask(path[(slash + 1)..]));
            return true;
        }
        catch (Exception ex) when (ex.HResult is FileNotFound or PathNotFound) { return false; }
    }

    /// <summary>HRESULT_FROM_WIN32 of ERROR_FILE_NOT_FOUND and ERROR_PATH_NOT_FOUND: no such task, no such folder. The COM
    /// binder raises them as a COMException or as the matching FileNotFound / DirectoryNotFound exception.</summary>
    private const int FileNotFound = unchecked((int)0x80070002), PathNotFound = unchecked((int)0x80070003);

    // ---- startup entries (Run keys of both views, the two Startup folders; StartupApproved)

    private static RegistryKey Hive(StartupLocation l) => l.Machine
        ? RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
        : RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);

    private static string StartupFolder(StartupLocation l) =>
        Environment.GetFolderPath(l.Machine ? Environment.SpecialFolder.CommonStartup : Environment.SpecialFolder.Startup);

    /// <summary>
    /// Every startup entry Task Manager lists (addendum 4 C, Part 3 review: not only the HKCU / HKLM Run keys): the Run keys
    /// of both registry views and the per-user and all-users Startup folders, each with its StartupApproved state.
    /// </summary>
    public List<StartupView> Startup()
    {
        var list = new List<StartupView>();
        foreach (var loc in StartupLocation.All)
        {
            try
            {
                using var hive = Hive(loc);
                using var appKey = hive.OpenSubKey(loc.ApprovedKey);
                foreach (var (name, cmd) in Entries(loc, hive))
                {
                    bool enabled = StartupApproved.IsEnabled(appKey?.GetValue(name) as byte[]);
                    bool ms = cmd.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) || cmd.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase);
                    list.Add(new StartupView(name, cmd, loc.Label, enabled, ms));
                }
            }
            catch (Exception ex) { _log.Info($"startup ({loc.Label}) not read: " + ex.Message); }
        }
        return list;
    }

    /// <summary>The entries of one location: Run values (name, command), or Startup-folder files (file name, shortcut target).</summary>
    private static List<(string name, string command)> Entries(StartupLocation loc, RegistryKey hive)
    {
        var list = new List<(string, string)>();
        if (loc.RunKey is { } run)
        {
            using var runKey = hive.OpenSubKey(run);
            if (runKey is null) return list;
            foreach (var name in runKey.GetValueNames())
                if (name.Length > 0) list.Add((name, runKey.GetValue(name)?.ToString() ?? ""));
            return list;
        }
        string folder = StartupFolder(loc);
        if (folder.Length == 0 || !Directory.Exists(folder)) return list;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(file);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add((name, name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutTarget(file) ?? file : file));
        }
        return list;
    }

    /// <summary>
    /// A shortcut's target and arguments, or null when it cannot be read. Read by PoolWatch's own bounded parser
    /// (<see cref="ShellLink"/>), not the shell: the file is user-writable and PoolWatch runs elevated (Part 3 review).
    /// </summary>
    private static string? ShortcutTarget(string lnk)
    {
        try
        {
            var info = new FileInfo(lnk);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > ShellLink.MaxBytes) return null;
            // Read at most MaxBytes + 1 (the file can grow after the size check): a longer one is refused by the parser.
            var buf = new byte[ShellLink.MaxBytes + 1];
            int n;
            using (var fs = new FileStream(lnk, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None))
                n = fs.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
            if (ShellLink.Read(buf.AsSpan(0, n)) is not { } l) return null;
            return (l.Target.Contains(' ') ? $"\"{l.Target}\"" : l.Target) + (l.Arguments is { } a ? " " + a : "");
        }
        catch { return null; }
    }

    /// <summary>
    /// Enable/disable a startup entry with the Task Manager StartupApproved bytes (reversible there too). Only an entry that
    /// exists in that location now is touched, so a stale or crafted request cannot plant values. The all-users and HKLM
    /// locations write HKLM (PoolWatch runs elevated).
    /// </summary>
    public string? SetStartupEnabled(string location, string name, bool enabled)
    {
        if (StartupLocation.Find(location) is not { } loc) return "unknown startup location";
        try
        {
            using var hive = Hive(loc);
            if (!Exists(loc, hive, name)) return $"'{name}' is no longer a startup entry in {location}";
            using var appKey = hive.CreateSubKey(loc.ApprovedKey, writable: true);
            var existing = appKey!.GetValue(name) as byte[];
            appKey.SetValue(name, StartupApproved.Set(existing, enabled, DateTime.UtcNow), RegistryValueKind.Binary);
            _log.Info($"startup {(enabled ? "enabled" : "disabled")}: {location}\\{name}");
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Whether the entry is still in that location. Throws when the location cannot be read (that is not "gone").</summary>
    public bool StartupExists(string location, string name)
    {
        if (StartupLocation.Find(location) is not { } loc) return false;
        using var hive = Hive(loc);
        return Exists(loc, hive, name);
    }

    /// <summary>The entry exists in that location now: by name only, no shortcut is parsed for this.</summary>
    private static bool Exists(StartupLocation loc, RegistryKey hive, string name) => loc.RunKey is { } run
        ? hive.OpenSubKey(run) is { } rk && Using(rk, k => k.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase))
        : name.Length > 0 && name.IndexOfAny(['\\', '/', ':']) < 0 && name != "." && name != ".." && File.Exists(Path.Combine(StartupFolder(loc), name));

    private static bool Using(RegistryKey key, Func<RegistryKey, bool> f) { using (key) return f(key); }

    public void Dump(string file, PathGuard guard)
    {
        var payload = new { tasks = Tasks(), startup = Startup() };
        File.WriteAllText(guard.CheckReal(file), JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }
}
