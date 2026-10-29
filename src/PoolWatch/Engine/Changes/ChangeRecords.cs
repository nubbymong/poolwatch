using System.Text;
using System.Text.RegularExpressions;
using PoolWatch.Core.Changes;

namespace PoolWatch.Engine.Changes;

/// <summary>
/// applied-changes.json: the changes PoolWatch made that have no store of their own (removed clear tasks of your own,
/// scheduled-task and startup toggles, shortcuts). Always in the admin-only data root, never under a headless --out
/// folder, since Undo acts on these records as an administrator; every read and write goes through the guard (never
/// through a link), and a write goes to a temporary file, flushed to the disk, that is then moved over the old one (so a
/// power cut cannot leave an empty list in its place). One instance per data root (<see cref="Open"/>), so the tray app,
/// the Settings form and the tray menu all write through one object; thread-safe. The file is read again before every
/// change and every listing, so a record another PoolWatch process wrote (the command line) is kept and shown; only two
/// processes saving at the same moment can lose one of the two changes, as no lock spans processes. A file that is there
/// but cannot be read is never written over, since it may hold the only copy of a removed task's definition.
/// </summary>
internal sealed class ChangeRecords
{
    private const string FileName = "applied-changes.json";
    /// <summary>Where a removed clear task's definition is saved when PoolWatch cannot put it back by itself: for import in Task Scheduler.</summary>
    private const string ImportFolder = "removed-tasks";

    private static readonly Dictionary<string, ChangeRecords> ByRoot = new(StringComparer.OrdinalIgnoreCase);

    private readonly AppPaths _paths;
    private readonly Log _log;
    private readonly object _lock = new();
    /// <summary>The records as last read, for <see cref="Has"/>.</summary>
    private List<ChangeRecord> _records = [];

    /// <summary>Reads the file once for <see cref="Has"/> (tests use this to read it again, as a restart would).</summary>
    internal ChangeRecords(AppPaths paths, Log log)
    {
        _paths = paths;
        _log = log;
        try { lock (_lock) Read(); }
        catch (Exception ex) { log.Info($"{FileName} not read: {ex.Message}"); }
    }

    /// <summary>The one instance for this data root.</summary>
    public static ChangeRecords Open(AppPaths paths, Log log)
    {
        lock (ByRoot)
        {
            if (!ByRoot.TryGetValue(paths.DataRoot, out var records)) ByRoot[paths.DataRoot] = records = new ChangeRecords(paths, log);
            return records;
        }
    }

    private string FilePath => Path.Combine(_paths.DataRoot, FileName);

    /// <summary>The records as the file holds them now. Throws when it is there but cannot be read.</summary>
    public IReadOnlyList<ChangeRecord> All() { lock (_lock) return Read(); }

    /// <summary>
    /// Whether a change of this kind is recorded (a shortcut was made, a clear task of your own was removed), as last read
    /// or written: from memory, since the suggestions ask often.
    /// </summary>
    public bool Has(RecordKind kind) { lock (_lock) return _records.Any(r => r.Kind == kind); }

    /// <summary>
    /// Adds the record, or replaces the one with its id, and saves at once. Throws when the record is invalid, the file
    /// cannot be read or would pass its bounds, or the save fails; nothing changes then, so a caller that records before
    /// it acts (a clear task is removed only once its definition is saved) can rely on it.
    /// </summary>
    public void Put(ChangeRecord record)
    {
        ThrowIfInvalid(record);
        Edit(now => With(now, record));
    }

    /// <summary>
    /// Removes the record once its change is undone (with a removed clear task's definition saved for import, no longer
    /// needed), only while the record there is still <paramref name="undone"/> as it was saved: a record another change
    /// of that target saved since is that change's to keep. A failure is logged: the change was undone either way.
    /// </summary>
    public void Remove(ChangeRecord undone) => RemoveWhere(undone.Id, r => SameSave(r, undone));

    private void RemoveWhere(string id, Func<ChangeRecord, bool> match)
    {
        ChangeRecord? gone = null;
        try { Edit(now => (gone = now.FirstOrDefault(match)) is null ? null : now.Where(r => !match(r)).ToList()); }
        catch (Exception ex) { _log.Info($"{FileName}: {id} is undone but its record was not removed: {ex.Message}"); return; }
        if (gone is { Kind: RecordKind.RemovedClearTask }) DeleteImportFile(gone);
    }

    /// <summary>
    /// Switches a scheduled task (<paramref name="location"/> is its path, <paramref name="name"/> its name) or a startup
    /// entry (<paramref name="location"/> is the StartupLocation label) with <paramref name="toggle"/> (null, or why it
    /// failed), recorded for Undo first: the first switch records the state before it, switching it back removes the
    /// record once it is switched back (nothing is left to undo), switching it the same way again keeps the first record.
    /// When the record cannot be saved, nothing is switched and that is the error; when the first switch fails or
    /// throws, its record is taken back (only while the record there is still the one this switch saved). So a switch
    /// is never left in place without its record, even when PoolWatch stops in between. Runs one at a time with Undo's
    /// switch back (<see cref="OneSwitchAtATime{T}"/>). Returns null, or why it was not switched.
    /// </summary>
    public string? Toggle(ChangeKind kind, string location, string name, bool enable, string via, Func<string?> toggle)
    {
        lock (_switching) return ToggleNow(kind, location, name, enable, via, toggle);
    }

    /// <summary>
    /// Held across a switch of a scheduled task or startup entry with its record: <see cref="Toggle"/> (the Services tab,
    /// on the UI thread) and Undo's switch back (on a worker, through <see cref="OneSwitchAtATime{T}"/>). So in this
    /// process a switch never runs between another's record and its switch, which could leave a task switched with no
    /// record; no lock spans processes. Nothing done under it waits for the UI thread.
    /// </summary>
    private readonly object _switching = new();

    /// <summary>Runs Undo's switch back of a task or startup entry, its record's removal included, one at a time with <see cref="Toggle"/>.</summary>
    public T OneSwitchAtATime<T>(Func<T> switchBack) { lock (_switching) return switchBack(); }

    private string? ToggleNow(ChangeKind kind, string location, string name, bool enable, string via, Func<string?> toggle)
    {
        var recordKind = kind switch
        {
            ChangeKind.ScheduledTask => RecordKind.ScheduledTask,
            ChangeKind.StartupEntry => RecordKind.StartupEntry,
            _ => throw new ArgumentException("only a scheduled task or a startup entry is switched", nameof(kind)),
        };
        string id = recordKind == RecordKind.ScheduledTask ? ChangeIds.ScheduledTask(location) : ChangeIds.StartupEntry(location, name);
        var record = new ChangeRecord
        {
            Id = id, Kind = recordKind, ChangedUtc = DateTime.UtcNow, Via = via, Name = name, Location = location, EnabledBefore = !enable,
        };
        if (ChangeRecordFile.Invalid(record) is { } invalid) return "not switched, because PoolWatch cannot record it to put it back later: " + invalid;
        ChangeRecord? added = null, back = null;
        try
        {
            Edit(now =>
            {
                added = back = null;
                var there = now.FirstOrDefault(r => SameId(r, id));
                if (there is null) { added = record; return With(now, record); }
                if (there.EnabledBefore == enable) back = there; // back as it was: its record goes once that is done
                return null; // switched back, or the same way again (the first record, with the state before it, stays)
            });
        }
        catch (Exception ex) { return "not switched, because PoolWatch could not save how it was set: " + ex.Message; }
        string? error;
        try { error = toggle(); }
        catch (Exception) { TakeBack(added); throw; }
        if (error is not null) TakeBack(added);
        else if (back is not null) Remove(back);
        return error;
    }

    /// <summary>A first switch that failed: the record it saved goes, unless another save has replaced it since. A failure is logged.</summary>
    private void TakeBack(ChangeRecord? added)
    {
        if (added is null) return;
        try { Edit(now => now.Any(r => SameSave(r, added)) ? now.Where(r => !SameSave(r, added)).ToList() : null); }
        catch (Exception ex) { _log.Info($"{FileName}: {added.Location} was not switched, but its record was not taken back: {ex.Message}"); }
    }

    /// <summary>
    /// Records clear tasks of your own just removed, each with its definition, so Undo can put them back (used for a task
    /// that changed just before its delete, ClearTaskReplacer). One that cannot be recorded is logged; the others are
    /// still recorded.
    /// </summary>
    public void RecordRemovedClearTasks(IEnumerable<CacheHelper.LegacyTaskInfo> removed, string via)
    {
        foreach (var task in removed)
        {
            try { PutRemovedTask(task, via); }
            catch (Exception ex) { _log.Info($"{FileName}: your clear task {task.Path} was removed but not recorded: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Records a clear task of your own as it is about to be removed (throws like <see cref="Put"/>), and returns the
    /// record with the one of that name it replaced (a task of that name removed before), as the save found it. One that
    /// PoolWatch cannot put back by itself (it ran with a saved password, say) has its definition saved for import first,
    /// so its row in Active changes never points to a file that is not there.
    /// </summary>
    internal (ChangeRecord Record, ChangeRecord? Replaced) PutRemovedTask(CacheHelper.LegacyTaskInfo task, string via)
    {
        var record = new ChangeRecord
        {
            Id = ChangeIds.RemovedClearTask(task.Path), Kind = RecordKind.RemovedClearTask, ChangedUtc = DateTime.UtcNow, Via = via,
            Name = task.Path, TaskXml = task.Xml, TaskLogonType = task.LogonType, TaskUserId = task.UserId,
        };
        ThrowIfInvalid(record);
        if (!ClearTaskReplacer.CanPutBack(record)) SaveForImport(record);
        ChangeRecord? replaced = null;
        Edit(now => { replaced = now.FirstOrDefault(r => SameId(r, record.Id)); return With(now, record); });
        return (record, replaced);
    }

    /// <summary>
    /// Takes back the record of a clear task that stayed in place after all (its removal stopped before it): the record
    /// goes, with its import file, or the earlier one it replaced is put back. Only while the record there is still the
    /// one this removal saved: a record another removal (another PoolWatch, say) saved over it since is that removal's to
    /// keep. A failure is logged; the task itself is still there.
    /// </summary>
    internal void Unsave(ChangeRecord record, ChangeRecord? replaced)
    {
        bool ours = false;
        // Decided before the list's lock is taken: it may look up an account, which can be slow for a domain one.
        bool replacedForImport = replaced is not null && !ClearTaskReplacer.CanPutBack(replaced);
        try
        {
            Edit(now =>
            {
                if (!(ours = now.Any(r => SameSave(r, record)))) return null;
                if (replaced is null) return now.Where(r => !SameId(r, record.Id)).ToList();
                if (replacedForImport) SaveForImport(replaced);
                return With(now, replaced);
            });
        }
        catch (Exception ex) { _log.Info($"{FileName}: the record of your clear task {record.Name} was not taken back: {ex.Message}"); return; }
        if (!ours) _log.Info($"{FileName}: the record of your clear task {record.Name} is left as it is: it is no longer the one this removal saved");
        else if (replaced is null) DeleteImportFile(record);
    }

    /// <summary>Where a removed clear task's definition is saved for import: removed-tasks\&lt;its name&gt;.xml in the data root.</summary>
    internal string ImportFile(ChangeRecord r) => Path.Combine(_paths.DataRoot, ImportFolder, ImportFileName(r.Name!));

    /// <summary>
    /// Writes the task's definition for import (UTF-16, as its XML declaration says, like Task Scheduler's own export),
    /// to a temporary file that is then moved; left as it is when it already holds that definition.
    /// </summary>
    internal string SaveForImport(ChangeRecord r)
    {
        _paths.EnsureDir(Path.Combine(_paths.DataRoot, ImportFolder));
        string file = _paths.Guard.CheckReal(ImportFile(r));
        if (File.Exists(file) && File.ReadAllText(file) == r.TaskXml) return file;
        DurableFile.Replace(_paths.Guard, file, r.TaskXml!, Encoding.Unicode);
        return file;
    }

    /// <summary>Deletes a removed clear task's definition saved for import, once its record is gone. A failure is logged.</summary>
    private void DeleteImportFile(ChangeRecord r)
    {
        try { File.Delete(_paths.Guard.CheckReal(ImportFile(r))); }
        catch (Exception ex) { _log.Info($"{FileName}: the saved definition of {r.Name} was not deleted: {ex.Message}"); }
    }

    /// <summary>A file name for the task: characters a file name cannot hold become "_", and a device name gets a "_" in front.</summary>
    internal static string ImportFileName(string taskPath)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string stem = string.Concat(taskPath.TrimStart('\\').Select(c => invalid.Contains(c) ? '_' : c));
        return (DeviceName.IsMatch(stem) ? "_" + stem : stem) + ".xml";
    }

    private static readonly Regex DeviceName = new(@"^(CON|PRN|AUX|NUL|COM\d|LPT\d)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool SameId(ChangeRecord r, string id) => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="r"/> is the record <paramref name="saved"/> as it was saved: by its id, time and caller,
    /// not by value, since a definition the file could not hold as given reads back changed and is still that record.
    /// </summary>
    private static bool SameSave(ChangeRecord r, ChangeRecord saved) => SameId(r, saved.Id) && r.ChangedUtc == saved.ChangedUtc && r.Via == saved.Via;

    private static void ThrowIfInvalid(ChangeRecord record)
    {
        if (ChangeRecordFile.Invalid(record) is { } why) throw new ArgumentException("the change was not recorded: " + why); // shown as is: no parameter name
    }

    /// <summary>The list with <paramref name="record"/> added, or in place of the one with its id; throws past the bound.</summary>
    private static List<ChangeRecord> With(List<ChangeRecord> now, ChangeRecord record)
    {
        var next = now.Where(r => !SameId(r, record.Id)).Append(record).ToList();
        return next.Count <= ChangeRecordFile.MaxRecords ? next : throw new InvalidOperationException($"PoolWatch keeps at most {ChangeRecordFile.MaxRecords} changes");
    }

    /// <summary>Reads the file as it is now, edits that list and saves it (an edit that returns null changes nothing). Throws like <see cref="Read"/> and the save.</summary>
    private void Edit(Func<List<ChangeRecord>, List<ChangeRecord>?> edit)
    {
        lock (_lock)
        {
            if (edit(Read()) is not { } next) return;
            Save(next);
            _records = next;
        }
    }

    /// <summary>
    /// The text of a plain file below <paramref name="root"/> (the data root), read through one handle opened on the entry
    /// itself (never what a link there points at); null when it holds more than <paramref name="maxBytes"/>. The handle
    /// decides, so what the path check saw cannot be swapped in between: a link, a folder or a file with a second name (a
    /// hard link) there is refused, and so is a handle that landed anywhere but at that place below the root as the root
    /// is now (a folder on the way that became a link after the path was checked). A link above the root (a moved
    /// ProgramData, say) is not refused: only an administrator can make one there, and the root itself is judged at every
    /// start. The bound is on the bytes read, not on an earlier look at the size. Throws FileNotFoundException when there
    /// is no file (or no folder for it).
    /// </summary>
    internal static string? ReadPlainFile(string root, string file, int maxBytes)
    {
        // Backup semantics so that a folder or a junction opens as itself and is refused below, like a file link.
        using var h = NoFollow.Open(file, dir: true, NoFollow.GENERIC_READ, NoFollow.FILE_SHARE_READ | NoFollow.FILE_SHARE_DELETE);
        if (h.IsInvalid)
        {
            int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            if (error is NoFollow.ERROR_FILE_NOT_FOUND or NoFollow.ERROR_PATH_NOT_FOUND) throw new FileNotFoundException("no such file", file);
            throw new IOException($"{file}: {new System.ComponentModel.Win32Exception(error).Message}");
        }
        var facts = NoFollow.Query(h);
        if ((facts.Attributes & FileAttributes.ReparsePoint) != 0) throw NotRead(file, "is a link");
        if ((facts.Attributes & FileAttributes.Directory) != 0) throw NotRead(file, "is a folder, not a file");
        if (facts.Links > 1) throw NotRead(file, "has another name on the disk (a hard link)");
        // Not at its place: a link on the way, or another PoolWatch's save replaced the file between the open and this look.
        if (!AtItsPlace(h, root, file))
            throw LinkOnTheWay(root, file) ? NotRead(file, "is reached through a link") : new IOException($"{file} changed while PoolWatch read it");
        if (facts.Length > maxBytes) return null;
        // One byte more than its size, to tell a file that grew meanwhile (PoolWatch replaces it, never writes it in place).
        var bytes = new byte[facts.Length + 1];
        int read = 0;
        while (read < bytes.Length && RandomAccess.Read(h, bytes.AsSpan(read), read) is var n and > 0) read += n;
        if (read != facts.Length) throw new IOException($"{file} changed while PoolWatch read it");
        using var text = new StreamReader(new MemoryStream(bytes, 0, read), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return text.ReadToEnd();
    }

    /// <summary>
    /// Whether the data root, or a folder between it and <paramref name="file"/>, is a link now (or cannot be looked at,
    /// which counts as one).
    /// </summary>
    private static bool LinkOnTheWay(string root, string file)
    {
        string rootFull = Path.GetFullPath(root).TrimEnd('\\');
        for (string? f = Path.GetDirectoryName(Path.GetFullPath(file)); f is not null && f.Length >= rootFull.Length; f = Path.GetDirectoryName(f))
        {
            try { if ((File.GetAttributes(f) & FileAttributes.ReparsePoint) != 0) return true; }
            catch (Exception) { return true; }
            if (f.TrimEnd('\\').Equals(rootFull, StringComparison.OrdinalIgnoreCase)) break;
        }
        return false;
    }

    private static UnauthorizedAccessException NotRead(string file, string what) =>
        new($"PoolWatch read policy: '{file}' {what}, so PoolWatch does not read it; PoolWatch never reads through links");

    /// <summary>
    /// Whether the handle is the entry at <paramref name="file"/>'s place below <paramref name="root"/>, with the root
    /// where it is now: the root itself must not be a link, and the handle's own path must be the root's own path plus
    /// that place. Both paths are as Windows resolves them, so a link above the root does not count.
    /// </summary>
    private static bool AtItsPlace(Microsoft.Win32.SafeHandles.SafeFileHandle h, string root, string file)
    {
        using var r = NoFollow.Open(root, dir: true, NoFollow.GENERIC_READ, NoFollow.ShareAll);
        if (r.IsInvalid || (NoFollow.Query(r).Attributes & FileAttributes.ReparsePoint) != 0) return false;
        return NoFollow.FinalPath(r) is { } rootNow && NoFollow.SamePath(NoFollow.FinalPath(h), Path.Join(rootNow, Path.GetRelativePath(root, file)));
    }

    /// <summary>
    /// The records in the file now (none when there is no file). Throws when the file is there but cannot be read: a
    /// read error, or one this build does not read (damaged, over its bound, written by a newer PoolWatch). Under the lock.
    /// </summary>
    private List<ChangeRecord> Read()
    {
        string json;
        try
        {
            // Never read through a link either: no folder on the way (the guard), nor the file itself (its own handle).
            json = ReadPlainFile(_paths.DataRoot, _paths.Guard.CheckReal(FilePath), ChangeRecordFile.MaxBytes)
                   ?? throw Unreadable($"it is over {ChangeRecordFile.MaxBytes >> 20} MB");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return _records = []; }
        return _records = ChangeRecordFile.Parse(json) ?? throw (string.IsNullOrWhiteSpace(json)
            ? Unreadable("it is empty, as a power cut can leave a file", "restore it from a backup")
            : Unreadable("it is damaged, or a newer PoolWatch wrote it"));
    }

    /// <param name="advice">What to do first; without it, install the newest PoolWatch (the file may be a newer one's).</param>
    private InvalidDataException Unreadable(string why, string advice = "install the newest PoolWatch to read it") =>
        new($"PoolWatch cannot read its list of changes, {FilePath}: {why}. The file is left as it is: {advice}, or delete it " +
            "to start a new list (the changes in it can then no longer be put back from PoolWatch).");

    private void Save(List<ChangeRecord> records)
    {
        string json = ChangeRecordFile.ToJson(records);
        if (Encoding.UTF8.GetByteCount(json) > ChangeRecordFile.MaxBytes) throw new InvalidOperationException($"{FileName} would be over {ChangeRecordFile.MaxBytes >> 20} MB");
        DurableFile.Replace(_paths.Guard, FilePath, json);
    }
}
