using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PoolWatch.Core.Services;

namespace PoolWatch.Core.Changes;

/// <summary>
/// The changes kept in applied-changes.json: the ones with no store of their own. Services, settings and the logon task
/// are listed from their own stores (service-changes.json, settings.json, Task Scheduler) and never copied here, so there
/// is one truth for each change.
/// </summary>
public enum RecordKind { RemovedClearTask, ScheduledTask, StartupEntry, Shortcut }

/// <summary>
/// One lasting change PoolWatch made, saved so it can be undone after a restart. The file is admin-only, but a record
/// is untrusted when read, like service-changes.json: <see cref="ChangeRecordFile.Invalid"/> and the live checks of its
/// kind run right before anything acts on it.
/// </summary>
public sealed record ChangeRecord
{
    public required string Id { get; init; }
    public required RecordKind Kind { get; init; }
    public required DateTime ChangedUtc { get; init; }
    /// <summary>Where the change was made, in plain words: "the setup guide", "Settings", "the tray menu", "the command line", "the Services tab", "a suggestion".</summary>
    public required string Via { get; init; }
    /// <summary>What the user recognises: the removed task's path, the scheduled task's or startup entry's name, the shortcut's file name.</summary>
    public string? Name { get; init; }
    /// <summary>RemovedClearTask: the task's definition as Task Scheduler gave it (at most <see cref="ChangeRecordFile.MaxTaskXmlChars"/>).</summary>
    public string? TaskXml { get; init; }
    /// <summary>RemovedClearTask: how the task signed in (TASK_LOGON_*).</summary>
    public int? TaskLogonType { get; init; }
    /// <summary>RemovedClearTask: the account it ran as.</summary>
    public string? TaskUserId { get; init; }
    /// <summary>ScheduledTask / StartupEntry: whether it was on before PoolWatch switched it.</summary>
    public bool? EnabledBefore { get; init; }
    /// <summary>StartupEntry: the <see cref="StartupLocation"/> label; ScheduledTask: the task path.</summary>
    public string? Location { get; init; }
    /// <summary>Shortcut: its full path.</summary>
    public string? Path { get; init; }
    /// <summary>Shortcut: the SHA-256 of the bytes PoolWatch wrote, in hex.</summary>
    public string? Sha256 { get; init; }
}

/// <summary>The id of each change in the Active changes list; a record's id is derived from its target, so one target has one record.</summary>
public static class ChangeIds
{
    public const string LogonTask = "logon-task", AutoClear = "auto-clear", TokenGuard = "token-guard";
    /// <summary>The token-leak guard's Windows setting, still changed while the guard is off (Windows did not let PoolWatch put it back).</summary>
    public const string TokenGuardSetting = "token-guard-setting";
    public static string Service(string name) => "service:" + name.ToLowerInvariant();
    public static string RemovedClearTask(string taskPath) => "clear-task:" + taskPath;
    public static string ScheduledTask(string taskPath) => "task:" + taskPath;
    public static string StartupEntry(string location, string name) => $"startup:{location}\\{name}";
    public static string Shortcut(string path) => "shortcut:" + path;
}

/// <summary>
/// applied-changes.json: <c>{ "version": 1, "changes": [ ... ] }</c>, at most <see cref="MaxBytes"/> and
/// <see cref="MaxRecords"/> records. Parsing is bounded and never throws: a file this build cannot read as a whole
/// (malformed, too large, another version, a field written twice in one object) is null, so it is never written over,
/// and an entry of the list that is invalid or of a kind this build does not know is left out on its own, and kept as
/// its text (<see cref="Parse(string, out IReadOnlyList{string})"/>), which a save writes back as it is
/// (<see cref="ToJson(IEnumerable{ChangeRecord}, IEnumerable{string})"/>), so none is lost.
/// </summary>
public static class ChangeRecordFile
{
    public const int MaxBytes = 1 << 20, MaxRecords = 500, MaxTaskXmlChars = 64 * 1024;
    public const int Version = 1;
    /// <summary>Names, paths, accounts and the "via" words are at most this long.</summary>
    private const int MaxTextChars = 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    /// <summary>
    /// The valid records. Null when the file as a whole is not one this build reads, an empty one included: PoolWatch
    /// always writes the version and the list, so an empty file is one a power cut or a crash left, and may have held records.
    /// </summary>
    public static List<ChangeRecord>? Parse(string json) => Parse(json, out IReadOnlyList<string> _);

    /// <summary><see cref="Parse(string)"/>, and how many entries of the file's list were left out (<see cref="Parse(string, out IReadOnlyList{string})"/>).</summary>
    public static List<ChangeRecord>? Parse(string json, out int unread)
    {
        var records = Parse(json, out IReadOnlyList<string> left);
        unread = left.Count;
        return records;
    }

    /// <summary>
    /// <see cref="Parse(string)"/>, and each entry of the file's list that was left out (<paramref name="unread"/>), as
    /// its text in the file: invalid, of a kind this build does not know (a newer PoolWatch's), a second one for a
    /// target, or past <see cref="MaxRecords"/>. Saving only the records read would lose those, perhaps with the only copy
    /// of a removed task's definition, so ChangeRecords writes them back after its records. Empty when the file is not
    /// read at all.
    /// </summary>
    public static List<ChangeRecord>? Parse(string json, out IReadOnlyList<string> unread)
    {
        unread = [];
        var list = new List<ChangeRecord>();
        if (string.IsNullOrWhiteSpace(json)) return null;
        if (Encoding.UTF8.GetByteCount(json) > MaxBytes) return null;
        try
        {
            // A field written twice anywhere makes the whole file unread: JSON allows it and the last one would count, so
            // {"version": 1, "changes": [...], "changes": []} would read as an empty list that the next save writes.
            using var doc = JsonDocument.Parse(json, ReadOptions);
            var root = doc.RootElement;
            // TryGetInt32 throws on a version that is not a number ("1", null), so its kind is checked first.
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number ||
                !v.TryGetInt32(out int version) || version != Version || !root.TryGetProperty("changes", out var changes) ||
                changes.ValueKind != JsonValueKind.Array) return null;
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var left = new List<string>();
            foreach (var e in changes.EnumerateArray())
            {
                ChangeRecord? r = null;
                if (list.Count < MaxRecords)
                {
                    try { r = e.Deserialize<ChangeRecord>(Json); }
                    catch (JsonException) { left.Add(e.GetRawText()); }
                }
                if (r is not null && Invalid(r) is null && ids.Add(r.Id)) list.Add(r);
            }
            unread = left;
        }
        // InvalidOperationException: a field's name that holds half of a surrogate pair written as an escape ("\ud800")
        // throws when it is compared with the name looked for.
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { unread = []; return null; }
        return list;
    }

    private static readonly JsonDocumentOptions ReadOptions = new() { MaxDepth = 8, AllowDuplicateProperties = false };

    public static string ToJson(IEnumerable<ChangeRecord> records) => ToJson(records, []);

    /// <summary>
    /// The file: <paramref name="records"/>, then each entry of <paramref name="unread"/> (as
    /// <see cref="Parse(string, out IReadOnlyList{string})"/> gave it) written back exactly as it was read.
    /// </summary>
    public static string ToJson(IEnumerable<ChangeRecord> records, IEnumerable<string> unread)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", Version);
            w.WriteStartArray("changes");
            foreach (var r in records) JsonSerializer.Serialize(w, r, Json);
            foreach (string entry in unread) w.WriteRawValue(entry); // checked to be one JSON value, then copied as it is
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Why the record is not acted on, or null. The checks that need only the record: its id matches its target, every
    /// text is bounded and has no control character, a task path is not one of Microsoft's, a removed clear task was in
    /// the Task Scheduler root and has its definition, a startup location is one PoolWatch knows, a shortcut is a .lnk
    /// on a local drive with a SHA-256. The live checks are the engine's, right before Undo acts.
    /// </summary>
    public static string? Invalid(ChangeRecord r)
    {
        if (!Plain(r.Id) || !Plain(r.Via)) return "no id, or no words for where it was made";
        switch (r.Kind)
        {
            case RecordKind.RemovedClearTask:
                // No folder in its name, by either separator: Windows reads "/" as one in a path too.
                if (!IsTaskPath(r.Name) || r.Name!.IndexOfAny(['\\', '/'], 1) >= 0) return "not a task in the Task Scheduler root folder";
                if (string.IsNullOrEmpty(r.TaskXml) || r.TaskXml.Length > MaxTaskXmlChars) return "no task definition, or one over 64 KB";
                if (r.TaskLogonType is not (>= 0 and <= 6)) return "no valid logon type";
                if (r.TaskUserId is { } user && !Plain(user)) return "not a valid account";
                return r.Id == ChangeIds.RemovedClearTask(r.Name) ? null : "its id does not match its task";
            case RecordKind.ScheduledTask:
                if (!IsTaskPath(r.Location)) return "not a scheduled task PoolWatch may change";
                if (r.Name is { } name && !Plain(name)) return "not a valid task name";
                if (r.EnabledBefore is null) return "no state to go back to";
                return r.Id == ChangeIds.ScheduledTask(r.Location!) ? null : "its id does not match its task";
            case RecordKind.StartupEntry:
                if (r.Location is null || StartupLocation.Find(r.Location) is null) return "not a startup location PoolWatch knows";
                if (!Plain(r.Name)) return "not a valid startup entry name";
                if (r.EnabledBefore is null) return "no state to go back to";
                return r.Id == ChangeIds.StartupEntry(r.Location, r.Name!) ? null : "its id does not match its entry";
            case RecordKind.Shortcut:
                if (!Settings.IsLocalDrivePath(r.Path) || !r.Path!.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return "not a shortcut on a local drive";
                if (r.Sha256 is not { Length: 64 } hash || !hash.All(char.IsAsciiHexDigit)) return "no SHA-256 of what PoolWatch wrote";
                return r.Id == ChangeIds.Shortcut(r.Path) ? null : "its id does not match its shortcut";
            default:
                return "a kind of change this PoolWatch does not know";
        }
    }

    private static bool Plain(string? s) => s is { Length: > 0 and <= MaxTextChars } && !s.Any(char.IsControl);

    /// <summary>
    /// A task path ("\Name" or "\Folder\Name"), never one of Microsoft's (TasksAndStartup leaves those alone too), and with
    /// no "/" in it: Windows may read one as a separator too, and the Microsoft check reads only "\".
    /// </summary>
    private static bool IsTaskPath(string? p) =>
        Plain(p) && p!.Length > 1 && p[0] == '\\' && !p.Contains('/') && !p.StartsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase) &&
        p[1..].Split('\\').All(part => part.Length > 0 && part != "." && part != "..");
}
