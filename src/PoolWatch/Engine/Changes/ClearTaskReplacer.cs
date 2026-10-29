using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using PoolWatch.Core.Changes;

namespace PoolWatch.Engine.Changes;

/// <summary>Task Scheduler for <see cref="ClearTaskReplacer"/> (tests pass a fake: no test touches a real task).</summary>
internal interface IClearTaskStore
{
    /// <summary>Every clear task of your own, each with its definition (CacheHelper.FindLegacy).</summary>
    IReadOnlyList<CacheHelper.LegacyTaskInfo> Find();

    /// <summary>Removes it when it is still a clear task of your own; the task as read right before, or null when nothing was removed.</summary>
    CacheHelper.LegacyTaskInfo? Delete(CacheHelper.LegacyTaskInfo task);

    /// <summary>Whether a task of this name is in the Task Scheduler root folder now.</summary>
    bool Exists(string name);

    /// <summary>Registers the definition in the root folder (never over an existing task), always with its account; null, or why not.</summary>
    string? Register(string name, string xml, int logonType, string userId);

    /// <summary>
    /// Where a task's RAMMap may run from without a warning: admin-only by place, and by the access rules of its folders
    /// and of RAMMap itself.
    /// </summary>
    AdminPlaces Places { get; }
}

/// <summary>The real Task Scheduler. Elevated: a clear task of your own usually runs as SYSTEM, visible only then.</summary>
internal sealed class WindowsClearTaskStore : IClearTaskStore
{
    private AdminPlaces? _places;

    public IReadOnlyList<CacheHelper.LegacyTaskInfo> Find() => CacheHelper.IsElevated
        ? CacheHelper.FindLegacy()
        : throw new UnauthorizedAccessException("a clear task usually runs as SYSTEM: finding it needs PoolWatch to run as administrator");

    public CacheHelper.LegacyTaskInfo? Delete(CacheHelper.LegacyTaskInfo task) => CacheHelper.DeleteLegacy(task);
    public bool Exists(string name) => CacheHelper.RootTaskExists(name);
    public string? Register(string name, string xml, int logonType, string userId) => CacheHelper.RegisterOwnTask(name, xml, logonType, userId);
    public AdminPlaces Places => _places ??= AdminPlaces.OfThisPc();
}

/// <summary>
/// A clear task of your own (Sysinternals RAMMap in a scheduled task) and PoolWatch's automatic clear do the same job and
/// must never both run. <see cref="Replace"/> (the setup guide) removes your tasks, only once every definition is saved
/// for Undo (<see cref="RemoveAll"/>), and turns the hourly automatic clear on; <see cref="PutBack"/> (Undo) turns the
/// automatic clear off first and registers the saved definition again, after checking it as untrusted input: only
/// RAMMap clear actions from one folder, no access rules of its own, run as the account it is registered with, the root
/// folder, no task of that name now, never a task that needs a password, and a program you can change (by its folder's
/// rules or its own), or one that is not there or is itself a link, only after your yes (the command line, and so the
/// uninstaller, never gives one).
/// </summary>
internal static class ClearTaskReplacer
{
    /// <summary>TASK_LOGON_* values (taskschd.h).</summary>
    internal const int LogonPassword = 1, LogonS4U = 2, LogonInteractiveToken = 3, LogonServiceAccount = 5, LogonInteractiveTokenOrPassword = 6;

    private static readonly XNamespace TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>
    /// Removes every clear task of your own (every definition saved first), then turns the hourly automatic clear on.
    /// Elevated. The caller then re-reads the clear status (CacheClearer.RefreshStatus): until it does, the automatic
    /// clear stays paused for the tasks the last hourly check found, and the "Next cache clear" line says so.
    /// </summary>
    public static (IReadOnlyList<CacheHelper.LegacyTaskInfo> Removed, string? Error) Replace(ChangeRecords records, LiveSettings live, string via) =>
        Replace(records, live, via, new WindowsClearTaskStore());

    /// <summary>
    /// <see cref="Replace"/> over <paramref name="store"/>. When a task is left in place (<see cref="RemoveAll"/>), or the
    /// automatic clear cannot be turned on, it stays off and the error says so; the tasks removed stay recorded for Undo.
    /// </summary>
    internal static (IReadOnlyList<CacheHelper.LegacyTaskInfo> Removed, string? Error) Replace(ChangeRecords records, LiveSettings live, string via, IClearTaskStore store)
    {
        IReadOnlyList<CacheHelper.LegacyTaskInfo> found;
        try { found = store.Find(); }
        catch (Exception ex) { return ([], "PoolWatch could not read your scheduled tasks: " + ex.Message); }
        var (removed, error) = RemoveAll(found, records, via, store);
        if (error is not null) return (removed, error);
        // The schedule first, so the sampler never sees the clear on with the old one.
        try { live.Apply("the hourly automatic cache clear, in place of your own clear task", s => { s.AutoClearEveryHours = 1; s.AutoClearEnabled = true; }); }
        catch (Exception ex)
        {
            string notOn = "PoolWatch's automatic cache clearing could not be turned on: " + ex.Message;
            return (removed, removed.Count switch
            {
                0 => notOn,
                1 => "Your clear task was removed (Active changes can put it back), but " + notOn,
                _ => "Your clear tasks were removed (Active changes can put them back), but " + notOn,
            });
        }
        return (removed, null);
    }

    /// <summary>
    /// Removes clear tasks of your own for <see cref="Replace"/> and for CacheHelper.ResolveLegacy after your yes. Every
    /// task's definition is saved, and read back as saved, before the first delete; when one cannot be, none is removed
    /// and the records just saved are taken back. A task gone, or no longer a clear task, by its turn keeps no record; one
    /// that changed just before its turn is recorded again as it was removed, when that record can be saved (otherwise its
    /// record keeps the definition from before). A delete that fails is taken to have removed nothing and stops there:
    /// that task and the ones after it stay in place, without records, and the error names them. Returns the tasks
    /// removed, each as read right before its delete, and why any task was left in place (null when none was), including
    /// one still there after a delete that removed nothing.
    /// </summary>
    internal static (IReadOnlyList<CacheHelper.LegacyTaskInfo> Removed, string? Error) RemoveAll(
        IReadOnlyList<CacheHelper.LegacyTaskInfo> tasks, ChangeRecords records, string via, IClearTaskStore store)
    {
        if (tasks.Count == 0) return ([], null);
        if (CacheHelper.LegacyKeptReason(tasks) is { } unread) return ([], unread);
        var saved = new List<(ChangeRecord Record, ChangeRecord? Replaced)>();
        if (SaveAll(tasks, records, via, saved) is { } notSaved)
        {
            foreach (var (record, replaced) in saved) records.Unsave(record, replaced);
            return ([], notSaved);
        }
        var removed = new List<CacheHelper.LegacyTaskInfo>();
        var missed = new List<CacheHelper.LegacyTaskInfo>();
        for (int i = 0; i < tasks.Count; i++)
        {
            CacheHelper.LegacyTaskInfo? done;
            try { done = store.Delete(tasks[i]); }
            catch (Exception ex)
            {
                foreach (var (record, replaced) in saved.Skip(i)) records.Unsave(record, replaced);
                string name = Name(tasks[i].Path);
                return (removed, AfterRemoved(removed, i == tasks.Count - 1
                    ? $"your clear task \"{name}\" could not be removed: {ex.Message}"
                    : $"your clear tasks {Names(tasks.Skip(i))} were left in place because \"{name}\" could not be removed: {ex.Message}"));
            }
            if (done is null) { records.Unsave(saved[i].Record, saved[i].Replaced); missed.Add(tasks[i]); continue; } // gone, no longer a clear task, or unreadable
            // Changed between the read and the delete: record what was removed.
            if ((done.Xml, done.LogonType, done.UserId) != (tasks[i].Xml, tasks[i].LogonType, tasks[i].UserId)) records.RecordRemovedClearTasks([done], via);
            removed.Add(done);
        }
        return (removed, missed.Count == 0 ? null : StillThere(missed, removed, store));
    }

    /// <summary>
    /// After deletes that removed nothing: why one of those tasks is still a clear task of yours (it could not be read at
    /// its turn, or it was made again meanwhile), so the automatic clear stays off; null when none is.
    /// </summary>
    private static string? StillThere(IReadOnlyList<CacheHelper.LegacyTaskInfo> missed, IReadOnlyList<CacheHelper.LegacyTaskInfo> removed, IClearTaskStore store)
    {
        IReadOnlyList<CacheHelper.LegacyTaskInfo> found;
        try { found = store.Find(); }
        catch (Exception ex)
        {
            return AfterRemoved(removed, missed.Count == 1
                ? $"PoolWatch could not check whether your clear task {Names(missed)} is still there: {ex.Message}"
                : $"PoolWatch could not check whether your clear tasks {Names(missed)} are still there: {ex.Message}");
        }
        if (found.FirstOrDefault(f => missed.Any(m => m.Path.Equals(f.Path, StringComparison.OrdinalIgnoreCase))) is not { } left) return null;
        return AfterRemoved(removed, left.Xml is null
            ? $"your clear task \"{Name(left.Path)}\" could not be read when PoolWatch went to remove it, so it was left in place."
            : $"your clear task \"{Name(left.Path)}\" changed while PoolWatch was removing it, so it was left in place.");
    }

    /// <summary>
    /// Records every task's removal, each with the record of that name it replaced as the save found it (added to
    /// <paramref name="saved"/> as it goes), then reads the list back: null when each record is there as saved, else
    /// why not.
    /// </summary>
    private static string? SaveAll(IReadOnlyList<CacheHelper.LegacyTaskInfo> tasks, ChangeRecords records, string via, List<(ChangeRecord Record, ChangeRecord? Replaced)> saved)
    {
        // A list that cannot be read stops everything before anything is saved, an import file included.
        try { records.All(); }
        catch (Exception ex) { return NotSaved(tasks, null, ex.Message); }
        foreach (var task in tasks)
        {
            try { saved.Add(records.PutRemovedTask(task, via)); }
            catch (Exception ex) { return NotSaved(tasks, task.Path, ex.Message); }
        }
        // Read back: a record another PoolWatch wrote over meanwhile, or a definition the file could not hold as given,
        // would be lost with its task.
        IReadOnlyList<ChangeRecord> after;
        try { after = records.All(); }
        catch (Exception ex) { return NotSaved(tasks, null, ex.Message); }
        foreach (var (record, _) in saved)
            if (!after.Contains(record)) return NotSaved(tasks, record.Name!, "the copy PoolWatch saved did not match the task.");
        return null;
    }

    /// <summary>Why every task was left in place: the one at <paramref name="taskPath"/> could not be saved, or (null) the list itself.</summary>
    private static string NotSaved(IReadOnlyList<CacheHelper.LegacyTaskInfo> tasks, string? taskPath, string why) => tasks.Count == 1
        ? $"Your clear task \"{Name(tasks[0].Path)}\" was left in place because it could not be saved for putting back later: {why}"
        : $"Your clear tasks were left in place because {(taskPath is null ? "they" : $"\"{Name(taskPath)}\"")} could not be saved for putting back later: {why}";

    /// <summary>Why a task was left in place (<paramref name="why"/>, a clause), after the tasks removed before it, which Undo puts back.</summary>
    private static string AfterRemoved(IReadOnlyList<CacheHelper.LegacyTaskInfo> removed, string why) => removed.Count == 0
        ? char.ToUpperInvariant(why[0]) + why[1..]
        : $"Removed {Names(removed)} (Active changes can put {(removed.Count == 1 ? "it" : "them")} back), but {why}";

    /// <summary>The tasks' names, quoted: "a", "b" and "c".</summary>
    private static string Names(IEnumerable<CacheHelper.LegacyTaskInfo> tasks)
    {
        var names = tasks.Select(t => $"\"{Name(t.Path)}\"").ToArray();
        return names.Length == 1 ? names[0] : string.Join(", ", names[..^1]) + " and " + names[^1];
    }

    private static string Name(string taskPath) => taskPath.TrimStart('\\');

    /// <summary>
    /// Undo of a removed clear task: re-checks the record and registers it again, turning the automatic clear off first
    /// (back on if the task could not be registered). A task that cannot be put back by PoolWatch has its definition saved
    /// for import in Task Scheduler, and the message says where. With <paramref name="confirmed"/>, your yes holds only
    /// for <paramref name="warningYouSaw"/> (null: a row with no warning): this very record, the one registered, must give
    /// that same warning, so a yes never covers a record another PoolWatch saved since the list was read.
    /// </summary>
    internal static UndoResult PutBack(ChangeRecord r, bool confirmed, string? warningYouSaw, IClearTaskStore store, ChangeRecords records, LiveSettings live)
    {
        string? invalid = r.Kind == RecordKind.RemovedClearTask ? ChangeRecordFile.Invalid(r) : "it is not a removed clear task";
        if (invalid is not null) return new(false, $"PoolWatch does not put back this task: its saved record is not valid ({invalid}).");
        string name = r.Name!.TrimStart('\\');
        var registerAs = RegisterAs(r.TaskLogonType, r.TaskUserId);
        var (programs, refused) = ReadDefinition(r.TaskXml!, registerAs?.UserId);
        // The folders and the programs are judged once, and your yes against the warning they give (as Row words it) on
        // this very record.
        var open = refused is null && registerAs is not null ? UserWritable(programs, store.Places) : [];
        if (confirmed && Warning(r, open) != warningYouSaw) // nothing is tried: the list shows it as it is now, to answer again
            return new(false, $"Your clear task \"{name}\", or the permissions of what it runs, changed since you said yes, so PoolWatch " +
                              "did not put it back. Look at the list again: it shows the task as it is now.");
        if (refused is not null) return new(false, $"PoolWatch cannot put back \"{name}\": {refused}. {ImportLine(records.SaveForImport(r), password: false)}");
        if (registerAs is not { } account)
            return new(false, $"PoolWatch cannot put back \"{name}\". {AccountNote(r.TaskLogonType, records.SaveForImport(r))}");
        if (open.Count > 0 && !confirmed)
            return new(false, $"PoolWatch did not put back \"{name}\": it runs {Runs(open)}. Its definition is saved in " +
                              $"{records.SaveForImport(r)}; import it in Task Scheduler (Action, Import Task) if you want it back.");
        // The task there may be your own original, made again or never removed: PoolWatch neither replaces it nor advises
        // deleting it, and the record stays.
        if (store.Exists(name))
            return new(false, $"A task named \"{name}\" already exists in Task Scheduler, so PoolWatch did not put back the one it removed. " +
                              $"Its definition is saved in {records.SaveForImport(r)}: compare the two, and import it in Task Scheduler " +
                              "(Action, Import Task) if you want it back.");
        bool autoClearWasOn = live.Running.AutoClearEnabled;
        if (autoClearWasOn) live.Apply("automatic cache clearing off, before your own clear task is put back", s => s.AutoClearEnabled = false);
        if (store.Register(name, r.TaskXml!, account.LogonType, account.UserId) is { } error)
        {
            if (autoClearWasOn) live.Apply("automatic cache clearing back on: your own clear task was not put back", s => s.AutoClearEnabled = true);
            return new(false, $"PoolWatch could not put back \"{name}\": {error}");
        }
        records.Remove(r); // only this record: one another removal of that name saved meanwhile stays
        return new(true, autoClearWasOn
            ? $"Put back your clear task \"{name}\". PoolWatch's automatic cache clearing is off now, so the two do not both run."
            : $"Put back your clear task \"{name}\".");
    }

    /// <summary>The Active changes row of a removed clear task: whether it can be put back, its note, and the warning to accept first.</summary>
    internal static (bool CanUndo, string? Note, string? Warning) Row(ChangeRecord r, ChangeRecords records, IClearTaskStore store)
    {
        var registerAs = RegisterAs(r.TaskLogonType, r.TaskUserId);
        var (programs, refused) = ReadDefinition(r.TaskXml!, registerAs?.UserId);
        if (refused is not null) return (false, $"PoolWatch cannot put this task back: {refused}. {ImportLine(ImportFileFor(r, records), password: false)}", null);
        if (registerAs is null) return (false, AccountNote(r.TaskLogonType, ImportFileFor(r, records)), null);
        if (UserWritable(programs, store.Places) is { Count: > 0 } open)
            return (true, $"It runs {Runs(open)}: PoolWatch asks before putting it back.", Warning(r, open));
        return (true, "Putting it back turns off PoolWatch's automatic cache clearing first, so the two never both run.", null);
    }

    /// <summary>
    /// The warning to accept before a task whose RAMMap a standard user could change (<paramref name="open"/>, every one
    /// of its programs that is) is put back; null with none.
    /// </summary>
    private static string? Warning(ChangeRecord r, IReadOnlyList<OpenPlace> open)
    {
        if (open.Count == 0) return null;
        string asWhom = CacheHelper.IsSystemAccount(r.TaskUserId) ? "as SYSTEM" : "with administrator rights";
        // By the programs, not the clauses: two programs in one folder share its clause but are still two files.
        bool oneFile = open.Select(o => o.Program).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
        return $"This task runs {Runs(open)}, {asWhom}. Anything that replaces {(oneFile ? "that file" : "those files")} would run {asWhom}.";
    }

    /// <summary>
    /// What the task runs and why a standard user could change it, to follow "it runs": each program named, so a yes
    /// covers every one; a folder they share is named once.
    /// </summary>
    private static string Runs(IReadOnlyList<OpenPlace> open) => string.Join(", and ", Clauses(open));

    private static List<string> Clauses(IReadOnlyList<OpenPlace> open) => open.Select(Runs).Distinct(StringComparer.Ordinal).ToList();

    private static string Runs(OpenPlace open) => open.Because switch
    {
        OpenBecause.ProgramRules => $"{open.Program}, a file you can change without administrator rights",
        OpenBecause.Unreadable => $"{open.Program}, whose permissions, or those of a folder above it, PoolWatch could not read (so you may be " +
                                  "able to change it without administrator rights)",
        OpenBecause.NotThere => $"{open.Program}, which is not there now (so PoolWatch cannot check the permissions a file put there would get)",
        OpenBecause.Link => $"{open.Program}, a link to a file elsewhere (so PoolWatch cannot check the folders that file is in)",
        _ => $"RAMMap from {open.Folder}, a folder you can change without administrator rights",
    };

    /// <summary>Whether Undo can put the task back by itself; when not, its definition is saved for import as soon as it is recorded.</summary>
    internal static bool CanPutBack(ChangeRecord r) =>
        RegisterAs(r.TaskLogonType, r.TaskUserId) is { } account && ReadDefinition(r.TaskXml!, account.UserId).Refused is null;

    /// <summary>
    /// The import file of a row Undo cannot put back, written now when it is not there yet: a record saved before a check
    /// that now refuses it has none, since a definition is saved for import when it is recorded only when it already could
    /// not be put back. When it cannot be written, its path all the same.
    /// </summary>
    private static string ImportFileFor(ChangeRecord r, ChangeRecords records)
    {
        try { return records.SaveForImport(r); }
        catch (Exception) { return records.ImportFile(r); }
    }

    private static string ImportLine(string importFile, bool password) =>
        $"To put it back yourself, import {importFile} in Task Scheduler (Action, Import Task){(password ? " and enter your password" : "")}.";

    /// <summary>Why a task that signs in this way is not put back (<see cref="RegisterAs"/> gave null), and how to put it back yourself.</summary>
    private static string AccountNote(int? logonType, string importFile) => logonType is LogonPassword or LogonInteractiveTokenOrPassword
        ? "It ran with your saved password, which PoolWatch never has, so PoolWatch cannot put it back. " + ImportLine(importFile, password: true)
        : "It is set up in a way PoolWatch cannot recreate, so PoolWatch cannot put it back. " + ImportLine(importFile, password: false);

    /// <summary>
    /// How the task is registered again: SYSTEM as the service account; an interactive-token or S4U task as it was saved,
    /// always with its account, so the account it runs as is never left to whoever registers it. The definition's own
    /// principal must name that same account (<see cref="ReadDefinition"/>): Microsoft documents that the account passed
    /// takes priority over the definition's for RegisterTaskDefinition, but not for RegisterTask, which PoolWatch calls,
    /// so the two must agree. Null for a task with no account (or a blank one), one that needs its password (password, or
    /// interactive token or password) or any other logon type (a group, or none).
    /// </summary>
    internal static (int LogonType, string UserId)? RegisterAs(int? logonType, string? userId)
    {
        if (CacheHelper.IsSystemAccount(userId)) return (LogonServiceAccount, "SYSTEM");
        return logonType is LogonS4U or LogonInteractiveToken or LogonServiceAccount && !string.IsNullOrWhiteSpace(userId) ? (logonType.Value, userId) : null;
    }

    /// <summary>
    /// The programs a saved definition runs (full paths, all in one folder), or why it is refused. It must parse with DTDs
    /// prohibited and fit in <see cref="ChangeRecordFile.MaxTaskXmlChars"/>, hold only Task Scheduler's own parts (one
    /// namespace), carry no access rules of its own, and every action must be a program, given by its full path in its
    /// one plain form with no environment variable, that clears memory the way RAMMap's -E options do
    /// (<see cref="CacheHelper.IsOwnClearAction"/>): no COM handler, e-mail or message action. Task Scheduler expands a
    /// variable as the task's account (often SYSTEM), not as the user PoolWatch runs for, so the folder check could not
    /// tell where such a program really is; and a path with a quote or a ".." in it could be read as another file than
    /// the one judged. Each action holds one program and at most one set of arguments, which are only RAMMap's -E options
    /// (and -accepteula): nothing a variable or an event could fill in. It may start only in RAMMap's own folder, and
    /// every action runs RAMMap from the same folder, so the one folder a warning names is every folder it runs from. So
    /// what is checked here is all of the programs, their arguments and their start folder. Each part checked must be
    /// plain text, as Task Scheduler writes it, so the text judged is the text Task Scheduler reads, and no path or option
    /// in it may hold a line break or another control character (a tab may separate the options).
    /// When <paramref name="registeredAs"/> is the account it would be registered with (<see cref="RegisterAs"/>), its own
    /// principal must be that same account: one principal, no group, and a UserId, when it has one, of that account.
    /// </summary>
    internal static (IReadOnlyList<string> Programs, string? Refused) ReadDefinition(string xml, string? registeredAs)
    {
        if (xml.Length > ChangeRecordFile.MaxTaskXmlChars) return ([], "its saved definition is over 64 KB");
        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = ChangeRecordFile.MaxTaskXmlChars };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            doc = XDocument.Load(reader);
        }
        catch (XmlException) { return ([], "its saved definition is not a valid task"); }
        var actions = doc.Root is { } root && root.Name == TaskNs + "Task" ? root.Elements(TaskNs + "Actions").ToList() : [];
        if (actions.Count != 1 || !actions[0].Elements().Any()) return ([], "its saved definition has no actions");
        // Task Scheduler's own exports use its one namespace: a part in another is one this check would not see.
        if (doc.Root!.Descendants().Any(e => e.Name.Namespace != TaskNs)) return ([], "it has parts that are not Task Scheduler's");
        // Task Scheduler applies a security descriptor in the definition when it registers it: one that let anyone change
        // the task would let them change what runs as SYSTEM.
        if (doc.Root!.Descendants().Any(e => e.Name.LocalName == "SecurityDescriptor")) return ([], "it has its own access rules (who may change it)");
        if (registeredAs is not null && OtherPrincipal(doc.Root!, registeredAs) is { } principal) return ([], principal);
        var programs = new List<string>();
        foreach (var action in actions[0].Elements())
        {
            if (action.Name != TaskNs + "Exec") return ([], "it has an action that is not a program");
            var parts = action.Elements().ToList();
            if (parts.Any(p => p.Name != TaskNs + "Command" && p.Name != TaskNs + "Arguments" && p.Name != TaskNs + "WorkingDirectory") ||
                parts.Count != parts.Select(p => p.Name).Distinct().Count())
                return ([], "it has an action PoolWatch cannot read as one program with its arguments");
            // Each part judged here is read as its joined text; a comment, a processing instruction, a CDATA piece or an
            // element inside one would split it into pieces, and how Task Scheduler reads those is not documented.
            if (!parts.All(IsPlainText)) return ([], NotPlainText);
            string command = action.Element(TaskNs + "Command")?.Value ?? "", arguments = action.Element(TaskNs + "Arguments")?.Value ?? "";
            if (!CacheHelper.IsOwnClearAction(command, arguments)) return ([], "it runs something other than RAMMap's memory clear");
            if (!arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).All(ClearOption.IsMatch))
                return ([], "it gives RAMMap more than its memory clear options");
            // No file or folder name holds a line break or another control character, so such a path is not the one judged;
            // nor do RAMMap's options, but a tab between them. (The options are split above on any white space, while RAMMap
            // splits on spaces and tabs only; each piece must be a clear option, so pieces joined by another space can only
            // make an option RAMMap does not know.)
            if (command.Any(char.IsControl) || action.Element(TaskNs + "WorkingDirectory")?.Value.Any(char.IsControl) == true ||
                arguments.Any(c => char.IsControl(c) && c != '\t'))
                return ([], "its program, its options or its start folder has a line break or another control character in it");
            string program = command.Trim(' ').Trim('"');
            if (HasVariable(program)) return ([], "its program's location uses a variable such as %TEMP%, so PoolWatch cannot check which folder it runs from");
            if (!Path.IsPathFullyQualified(program)) return ([], "its program is not given by its full path");
            // A file name never holds a quote, and the path checked must be the one text Task Scheduler is given.
            if (program.Contains('"')) return ([], "its program's path has a quote inside it");
            if (!IsPlainPath(program))
                return ([], "its program's path is not in its one plain form (it has a \"..\" part, say), so PoolWatch cannot be sure which file runs");
            if (action.Element(TaskNs + "WorkingDirectory")?.Value.Trim(' ').Trim('"') is { Length: > 0 } startIn && !StartsInItsOwnFolder(startIn, program))
                return ([], "it starts in a folder other than RAMMap's own");
            programs.Add(program);
        }
        if (programs.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1) return ([], "it runs RAMMap from more than one folder");
        return (programs, null);
    }

    /// <summary>
    /// Why the definition's own principal is not <paramref name="account"/>, or null. Task Scheduler is given both, and
    /// its documentation does not say which RegisterTask takes, so they must name one account: one principal at most,
    /// with each of its parts once, never a group, and a UserId, when it has one, that is the same account.
    /// </summary>
    private static string? OtherPrincipal(XElement task, string account)
    {
        var lists = task.Elements(TaskNs + "Principals").ToList();
        var principals = lists.SelectMany(p => p.Elements()).ToList();
        if (lists.Count > 1 || principals.Count > 1 || principals.Any(p => p.Name != TaskNs + "Principal")) return "it does not name one account to run as";
        if (principals.FirstOrDefault() is not { } principal) return null; // none: the account it is registered with is the one
        var parts = principal.Elements().ToList();
        if (parts.Count != parts.Select(p => p.Name).Distinct().Count()) return "it does not name one account to run as";
        if (principal.Element(TaskNs + "GroupId") is not null) return "it runs for a group of users, not one account";
        if (principal.Element(TaskNs + "UserId") is { } userId && !IsPlainText(userId)) return NotPlainText;
        return principal.Element(TaskNs + "UserId")?.Value.Trim() is { Length: > 0 } user && !SameAccount(user, account)
            ? "the account in its definition is not the one it ran as"
            : null;
    }

    private const string NotPlainText =
        "a part of it that PoolWatch checks is not plain text (it holds a comment or other markup), so PoolWatch cannot be sure what runs";

    /// <summary>
    /// Whether a part of the definition holds only text, as Task Scheduler writes it: no comment, processing instruction,
    /// CDATA piece or element inside.
    /// </summary>
    private static bool IsPlainText(XElement part) => part.Nodes().All(n => n is XText and not XCData);

    /// <summary>
    /// Whether two account names or SIDs are one account: the same text, SYSTEM by any of its names, or the same SID once
    /// looked up. One that cannot be looked up is not the same.
    /// </summary>
    internal static bool SameAccount(string a, string b) =>
        a.Equals(b, StringComparison.OrdinalIgnoreCase) || (Sid(a) is { } x && Sid(b) is { } y && x.Equals(y));

    private static SecurityIdentifier? Sid(string account)
    {
        if (CacheHelper.IsSystemAccount(account)) return new(WellKnownSidType.LocalSystemSid, null);
        try
        {
            return account.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
                ? new(account)
                : (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier));
        }
        catch (SystemException) { return null; } // not a SID, or no such account here
    }

    /// <summary>
    /// Whether a full path is already in the one form Windows makes of it: Path.GetFullPath leaves it as it is (no "." or
    /// ".." part, no doubled or forward separator, no trailing dot or space), and it is not a device path ("\\?\", "\\.\"
    /// or "\??\"), whose parts Windows takes as they are.
    /// </summary>
    private static bool IsPlainPath(string path) =>
        !path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\.\", StringComparison.Ordinal) &&
        !path.StartsWith(@"\??\", StringComparison.Ordinal) && Path.GetFullPath(path).Equals(path, StringComparison.OrdinalIgnoreCase);

    /// <summary>One of RAMMap's "empty" options (-Ew, -Es, -Em, -Et, -E0) or -accepteula, and nothing else.</summary>
    private static readonly Regex ClearOption =
        new($@"^(?:{CacheHelper.EmptyOptionPattern}|[-/]accepteula)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>An environment variable, or a $(...) value Task Scheduler fills in from the task's start or its event.</summary>
    private static bool HasVariable(string s) => s.Contains('%') || s.Contains("$(", StringComparison.Ordinal);

    /// <summary>Whether the folder a task starts in, from its definition, is the folder of its program, with no variable.</summary>
    private static bool StartsInItsOwnFolder(string startIn, string program) =>
        !HasVariable(startIn) && Path.IsPathFullyQualified(startIn) && Path.GetDirectoryName(Path.GetFullPath(program)) is { } folder &&
        Path.GetFullPath(startIn).TrimEnd('\\').Equals(folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Why a program running without administrator rights could replace one of the programs (full paths in their plain
    /// form, all in one folder: <see cref="ReadDefinition"/>): it is not in an admin-only install root, the access rules
    /// of a folder on the way or of the program itself let a standard user change it or could not be read, or the program
    /// is not there or is itself a link (<see cref="AdminPlaces"/>). Every program for which one holds, in order; none
    /// when none does.
    /// </summary>
    internal static IReadOnlyList<OpenPlace> UserWritable(IReadOnlyList<string> programs, AdminPlaces places) =>
        programs.Select(places.UserWritable).OfType<OpenPlace>().ToList();
}
