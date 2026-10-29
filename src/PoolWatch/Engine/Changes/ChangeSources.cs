using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PoolWatch.Core.Changes;
using PoolWatch.Core.Services;
using PoolWatch.Core.Util;

namespace PoolWatch.Engine.Changes;

/// <summary>
/// Switching a scheduled task or a startup entry back (<see cref="TasksAndStartup"/>; tests pass a fake, so no test
/// changes a real task or registry value). The existence checks tell "gone" from "failed" before anything is switched.
/// </summary>
internal interface ITaskToggles
{
    string? SetTaskEnabled(string path, bool enabled);
    string? SetStartupEnabled(string location, string name, bool enabled);
    bool TaskExists(string path);
    bool StartupExists(string location, string name);
}

/// <summary>The "start at sign-in" task: listed while it exists in Task Scheduler, undone by removing it.</summary>
internal sealed class LogonTaskSource(ILogonTasks tasks) : IChangeSource
{
    public IEnumerable<ActiveChange> List() =>
        tasks.Read() is null ? [] :
        [
            new ActiveChange(ChangeIds.LogonTask, ChangeKind.LogonTask, null, ChangeWords.LogonTask, CanUndo: true, Note: null)
            {
                Title = ChangeWords.LogonTaskTitle, Detail = ChangeWords.LogonTaskDetail,
            },
        ];

    public UndoResult Undo(ActiveChange change, bool confirmed) => tasks.Remove() is { } error
        ? new(false, "PoolWatch could not stop starting when you sign in: " + error)
        : new(true, "PoolWatch no longer starts when you sign in.");

    public ActiveChange Unread(string error) =>
        new("unread:" + ChangeIds.LogonTask, ChangeKind.LogonTask, null, ChangeWords.Unread("whether it starts when you sign in"), false, ChangeWords.UnreadNote(error));
}

/// <summary>
/// The automatic clear and the token-leak guard, listed while they are on in the running settings and turned off at once
/// through <see cref="LiveSettings"/>. Turning the guard off leaves the Windows setting it changed until you sign out, and
/// its row and its result say so.
/// </summary>
/// <param name="records">Says whether a clear task of your own was replaced (the automatic clear's row then notes it has a row of its own).</param>
/// <param name="tokenGuardOff">The app's guard stops keeping its workaround on (null on the command line, where no guard runs).</param>
internal sealed class SettingsSource(LiveSettings live, ChangeRecords records, Action? tokenGuardOff) : IChangeSource
{
    public IEnumerable<ActiveChange> List()
    {
        var s = live.Running;
        if (s.AutoClearEnabled)
            yield return new ActiveChange(ChangeIds.AutoClear, ChangeKind.AutoClear, null, ChangeWords.AutoClear(s.AutoClearEveryHours, s.AutoClearPoolGb), true,
                records.Has(RecordKind.RemovedClearTask) ? ChangeWords.AutoClearKeepsTaskNote : null)
            {
                Title = ChangeWords.AutoClearTitle, Detail = ChangeWords.AutoClearWhen(s.AutoClearEveryHours, s.AutoClearPoolGb),
            };
        if (s.TokenGuardEnabled)
            yield return new ActiveChange(ChangeIds.TokenGuard, ChangeKind.TokenGuard, null, ChangeWords.TokenGuard, true, ChangeWords.TokenGuardNote)
            {
                Title = ChangeWords.TokenGuardTitle,
            };
    }

    public UndoResult Undo(ActiveChange change, bool confirmed)
    {
        switch (change.Kind)
        {
            case ChangeKind.AutoClear:
                live.Apply("automatic cache clearing off (Active changes)", s => s.AutoClearEnabled = false);
                return new(true, "Automatic cache clearing is off.");
            case ChangeKind.TokenGuard:
                live.Apply("token-leak guard off (Active changes)", s => s.TokenGuardEnabled = false);
                tokenGuardOff?.Invoke();
                return new(true, ChangeWords.TokenGuardOff);
            default:
                return new(false, "This is not a setting.");
        }
    }

    public ActiveChange Unread(string error) =>
        new("unread:settings", ChangeKind.AutoClear, null, ChangeWords.Unread("its automatic clear and token-leak guard settings"), false, ChangeWords.UnreadNote(error));
}

/// <summary>Services PoolWatch changed: listed from service-changes.json, put back through the services page's gated Restore.</summary>
internal sealed class ServicesSource(ServicesController services) : IChangeSource
{
    public IEnumerable<ActiveChange> List() => services.Changes.Select(c =>
    {
        bool turnedOff = c.NewStart == ServiceStartType.Disabled;
        return new ActiveChange(ChangeIds.Service(c.Service), ChangeKind.Service, c.ChangedUtc, ChangeWords.Service(c.DisplayName, c.Service, turnedOff),
            CanUndo: true, Note: null)
        {
            Title = ChangeWords.ServiceTitle(c.DisplayName, c.Service),
            Detail = ChangeWords.ServiceDetail(c.OriginalStart, c.OriginalRunning, turnedOff),
            Group = turnedOff ? ChangeGroup.TurnedOff : ChangeGroup.Other,
        };
    });

    public UndoResult Undo(ActiveChange change, bool confirmed)
    {
        if (Find(change.Id) is not { } record) return new(true, "This service is already back to how it was.");
        string? error = services.Restore(record.Service).Select(r => r.error).FirstOrDefault();
        string name = ChangeWords.ServiceName(record.DisplayName, record.Service);
        // Restore removes the record once the start type is back, even when starting the service again failed.
        bool back = Find(change.Id) is null;
        return back
            ? new(true, error is null ? $"{name} is back to how it was." : $"{name}: {error}.")
            : new(false, $"PoolWatch could not put back {name}: {error ?? "it is still changed"}.");
    }

    private ServiceChange? Find(string id) => services.Changes.FirstOrDefault(c => ChangeIds.Service(c.Service) == id);

    public ActiveChange Unread(string error) =>
        new("unread:services", ChangeKind.Service, null, ChangeWords.Unread("the services it changed"), false, ChangeWords.UnreadNote(error));
}

/// <summary>
/// The changes kept in applied-changes.json. Each record is checked again against the live system right before Undo acts
/// on it: a removed clear task by <see cref="ClearTaskReplacer.PutBack"/>; a task or startup entry must still be there
/// (when it is gone the record goes and the message says so); a shortcut must be a .lnk directly in your Desktop or
/// Start-menu Programs folder, opened without following links, a plain file whose bytes still match what PoolWatch wrote.
/// Putting a change back removes only the record it acted on, as it was saved: one saved for that target since is kept.
/// </summary>
internal sealed class RecordedSource : IChangeSource
{
    private readonly ChangeRecords _records;
    private readonly ITaskToggles _toggles;
    private readonly IClearTaskStore _clearTasks;
    private readonly LiveSettings _live;
    private readonly IReadOnlyList<string> _shortcutFolders;

    /// <param name="shortcutFolders">Where PoolWatch's shortcuts may be (null: your Desktop and Start-menu Programs folders; tests pass a TEMP folder).</param>
    public RecordedSource(ChangeRecords records, ITaskToggles toggles, IClearTaskStore clearTasks, LiveSettings live, IReadOnlyList<string>? shortcutFolders = null)
    {
        _records = records;
        _toggles = toggles;
        _clearTasks = clearTasks;
        _live = live;
        _shortcutFolders = shortcutFolders ??
            [Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.Programs)];
    }

    public IEnumerable<ActiveChange> List() => _records.All().Select(Row);

    /// <summary>Listed with the shortcuts, which an uninstall that keeps your other changes still undoes, so it reports this too.</summary>
    public ActiveChange Unread(string error) =>
        new("unread:applied-changes", ChangeKind.Shortcut, null, ChangeWords.Unread("the clear tasks, scheduled tasks, startup apps and shortcuts it changed"), false,
            ChangeWords.UnreadNote(error));

    private ActiveChange Row(ChangeRecord r)
    {
        var kind = KindOf(r.Kind);
        // A task or startup app PoolWatch turned on is not "turned off by PoolWatch": its own group, put back all the same.
        var group = (r.Kind is RecordKind.ScheduledTask or RecordKind.StartupEntry) && r.EnabledBefore == false ? ChangeGroup.Other : ActiveChange.GroupOf(kind);
        if (r.Kind != RecordKind.RemovedClearTask)
            return new ActiveChange(r.Id, kind, r.ChangedUtc, ChangeWords.Of(r), CanUndo: true, Note: null)
            {
                Title = ChangeWords.TitleOf(r), Detail = ChangeWords.DetailOf(r), Group = group,
            };
        var (canUndo, note, warning) = ClearTaskReplacer.Row(r, _records, _clearTasks);
        return new ActiveChange(r.Id, kind, r.ChangedUtc, ChangeWords.Of(r), canUndo, note)
        {
            Warning = warning, Title = ChangeWords.TitleOf(r), Detail = ChangeWords.DetailOf(r), Group = group,
        };
    }

    private static ChangeKind KindOf(RecordKind k) => k switch
    {
        RecordKind.RemovedClearTask => ChangeKind.RemovedClearTask,
        RecordKind.ScheduledTask => ChangeKind.ScheduledTask,
        RecordKind.StartupEntry => ChangeKind.StartupEntry,
        _ => ChangeKind.Shortcut,
    };

    public UndoResult Undo(ActiveChange change, bool confirmed)
    {
        if (_records.All().FirstOrDefault(r => r.Id == change.Id) is not { } r) return new(true, "This change is already put back.");
        return r.Kind switch
        {
            // Your yes holds only for the warning you saw (another PoolWatch may have saved a removal of that name since the
            // list was read): PutBack checks it on the record it registers, this one, read again here.
            RecordKind.RemovedClearTask => ClearTaskReplacer.PutBack(r, confirmed, change.Warning, _clearTasks, _records, _live),
            RecordKind.ScheduledTask => SwitchBack(r, $"the scheduled task {r.Location}", () => _toggles.TaskExists(r.Location!),
                () => _toggles.SetTaskEnabled(r.Location!, r.EnabledBefore!.Value)),
            RecordKind.StartupEntry => SwitchBack(r, $"the startup app {r.Name}", () => _toggles.StartupExists(r.Location!, r.Name!),
                () => _toggles.SetStartupEnabled(r.Location!, r.Name!, r.EnabledBefore!.Value)),
            _ => DeleteShortcut(r),
        };
    }

    private UndoResult SwitchBack(ChangeRecord r, string what, Func<bool> exists, Func<string?> set)
    {
        bool there;
        try { there = exists(); }
        catch (Exception ex) { return new(false, $"PoolWatch could not check {what}: {ex.Message}"); }
        if (!there)
        {
            _records.Remove(r);
            return new(true, $"{Capital(what)} is no longer there, so there is nothing to put back.");
        }
        if (set() is { } error) return new(false, $"PoolWatch could not turn {what} back {OnOff(r)}: {error}");
        _records.Remove(r);
        return new(true, $"Turned {what} back {OnOff(r)}.");
    }

    private static string OnOff(ChangeRecord r) => r.EnabledBefore == true ? "on" : "off";
    private static string Capital(string s) => char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Deletes the shortcut only when it is still exactly what PoolWatch wrote; one you changed is yours and stays.</summary>
    private UndoResult DeleteShortcut(ChangeRecord r)
    {
        string path = Path.GetFullPath(r.Path!), name = ChangeWords.ShortcutName(path);
        string? folder = Path.GetDirectoryName(path);
        if (folder is null || !_shortcutFolders.Any(f => NoFollow.SamePath(folder, f)))
            return new(false, $"PoolWatch deletes only a shortcut it made on your desktop or in your Start menu; \"{name}\" is somewhere else, so it is left in place.");
        using var h = NoFollow.Open(path, dir: false, NoFollow.GENERIC_READ | NoFollow.DELETE, NoFollow.FILE_SHARE_READ);
        if (h.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            if (error is not (NoFollow.ERROR_FILE_NOT_FOUND or NoFollow.ERROR_PATH_NOT_FOUND))
                return new(false, $"PoolWatch could not open the shortcut \"{name}\": {new System.ComponentModel.Win32Exception(error).Message}");
            _records.Remove(r);
            return new(true, $"The shortcut \"{name}\" is no longer there.");
        }
        var facts = NoFollow.Query(h);
        if ((facts.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || !NoFollow.SamePath(NoFollow.FinalPath(h), path))
            return new(false, $"\"{name}\" is not a plain file in that folder any more, so PoolWatch left it in place.");
        if (facts.Length > ShellLink.MaxBytes || !Convert.ToHexString(SHA256.HashData(Read(h, (int)facts.Length))).Equals(r.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            _records.Remove(r);
            return new(true, $"The shortcut \"{name}\" was changed after PoolWatch made it, so PoolWatch left it in place.");
        }
        if (!NoFollow.DeleteOnClose(h))
            return new(false, $"PoolWatch could not delete the shortcut \"{name}\": {new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message}");
        _records.Remove(r);
        return new(true, $"Deleted the shortcut \"{name}\".");
    }

    /// <summary>The file's bytes, through the handle that was checked (the file cannot be written meanwhile: it is open without write sharing).</summary>
    private static byte[] Read(Microsoft.Win32.SafeHandles.SafeFileHandle h, int length)
    {
        var bytes = new byte[length];
        int read = 0;
        while (read < length && RandomAccess.Read(h, bytes.AsSpan(read), read) is var n and > 0) read += n;
        return read == length ? bytes : bytes[..read];
    }
}
