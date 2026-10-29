using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using PoolWatch.Analysis;
using PoolWatch.Core;
using PoolWatch.Core.Changes;

namespace PoolWatch.Engine.Changes;

/// <summary>
/// Every lasting change PoolWatch made on this PC, each of which can be put back: one list over several sources (the
/// Active changes window, the uninstaller). A change that already has a store of its own (services, settings, the logon
/// task) is read from it; only the others are kept in applied-changes.json (<see cref="ChangeRecords"/>), so there are
/// never two truths. List and putting back touch COM, the service control manager and the registry: call them off the
/// UI thread. Put all back (Active changes, the uninstaller) runs in <see cref="UndoOrder"/>, never stops at a failure
/// and returns every result. A source that cannot be read is listed as a row that cannot be put back
/// (<see cref="IChangeSource.Unread"/>), never left out.
/// </summary>
internal sealed class ChangeLedger
{
    private readonly IReadOnlyList<IChangeSource> _sources;
    private readonly Log _log;
    private readonly Action<ActiveChange, UndoResult>? _undone;

    /// <param name="undone">Called after each change is put back or turned off (the app puts a line on the timeline).</param>
    public ChangeLedger(IReadOnlyList<IChangeSource> sources, ChangeRecords records, Log log, Action<ActiveChange, UndoResult>? undone = null)
    {
        _sources = sources;
        Records = records;
        _log = log;
        _undone = undone;
    }

    /// <summary>
    /// The running app's ledger: settings through <paramref name="live"/>; turning the token-leak guard off also stops it
    /// keeping its workaround on; each one put back is a timeline line (a service's own Restore already writes one), and
    /// putting a clear task back re-reads the clear status, so the automatic clear pauses for it.
    /// </summary>
    public static ChangeLedger ForApp(MonitorEngine engine, AnalysisPipeline analysis, LiveSettings live)
    {
        var records = ChangeRecords.Open(engine.Paths, engine.Log);
        var d = analysis.Diagnostics;
        IChangeSource[] sources =
        [
            new LogonTaskSource(new Ui.WindowsLogonTasks(engine.Paths)),
            new SettingsSource(live, records, () => d.Guard.WorkaroundWanted = false),
            new ServicesSource(d.Services),
            new RecordedSource(records, new TasksAndStartup(engine.Log), new WindowsClearTaskStore(), live),
        ];
        return new ChangeLedger(sources, records, engine.Log, (change, result) =>
        {
            if (change.Kind != ChangeKind.Service) d.RecordOwnerChange(TimelineLine(change, result), null);
            if (change.Kind == ChangeKind.RemovedClearTask && result.Done) d.Cache.RefreshStatus();
        });
    }

    /// <summary>
    /// The timeline's line after a row's button ran, in the button's own words: a feature turned off (by its title, "Token-
    /// leak guard"), the shortcut removed, or anything else put back (by its line, "Turned off the startup app Helper").
    /// </summary>
    internal static string TimelineLine(ActiveChange change, UndoResult result) =>
        ChangeWords.Timeline(ActiveChange.ButtonOf(change), result.Done, change.Group == ChangeGroup.Feature ? change.Title : change.What, result.Message);

    /// <summary>
    /// The command line's ledger (<c>--changes-dump</c>, <c>--uninstall-cleanup</c>), with no PoolWatch running: settings
    /// are read from and written to settings.json; services through a services controller of its own. A settings.json
    /// that cannot be read is logged and lists no settings rows, not a row that cannot be undone: those settings act only
    /// while PoolWatch runs, so an uninstall has nothing of theirs to put back.
    /// </summary>
    public static ChangeLedger ForCommandLine(AppPaths paths, Log log)
    {
        var records = ChangeRecords.Open(paths, log);
        Settings saved;
        try { saved = Settings.Load(paths.SettingsFile); }
        catch (Exception ex) { log.Info("settings.json not read: " + ex.Message); saved = new Settings(); }
        var live = new LiveSettings(paths, saved, persist: true, log);
        var services = new ServicesController(paths, log, f => log.Info(f.Title), _ => null, _ => null);
        IChangeSource[] sources =
        [
            new LogonTaskSource(new Ui.WindowsLogonTasks(paths)),
            new SettingsSource(live, records, tokenGuardOff: null),
            new ServicesSource(services),
            new RecordedSource(records, new TasksAndStartup(log), new WindowsClearTaskStore(), live),
        ];
        return new ChangeLedger(sources, records, log);
    }

    public ChangeRecords Records { get; }

    /// <summary>Put all back's order: what depends on PoolWatch first, the automatic clear before your own clear task comes back, the logon task last.</summary>
    public static readonly IReadOnlyList<ChangeKind> UndoOrder =
        [ChangeKind.Shortcut, ChangeKind.StartupEntry, ChangeKind.ScheduledTask, ChangeKind.Service, ChangeKind.AutoClear,
         ChangeKind.RemovedClearTask, ChangeKind.TokenGuard, ChangeKind.LogonTask];

    /// <summary>Every change, newest first (the ones that are a state, with no time, before them). Worker thread.</summary>
    public IReadOnlyList<ActiveChange> List() => Rows().Select(r => r.Change).ToList();

    /// <summary>
    /// Puts back every change <paramref name="which"/> selects (null: all) in <see cref="UndoOrder"/>. A change that cannot
    /// be put back, or whose put-back fails, is reported and the next one runs. Worker thread.
    /// </summary>
    public IReadOnlyList<(ActiveChange Change, UndoResult Result)> UndoAll(Func<ActiveChange, bool>? which = null, bool confirmed = false) =>
        Rows().Where(r => which?.Invoke(r.Change) ?? true)
            .OrderBy(r => IndexOf(r.Change.Kind))
            .Select(r => (r.Change, Run(r.Source, r.Change, confirmed)))
            .ToList();

    private static int IndexOf(ChangeKind kind)
    {
        for (int i = 0; i < UndoOrder.Count; i++) if (UndoOrder[i] == kind) return i;
        return UndoOrder.Count;
    }

    private List<(IChangeSource Source, ActiveChange Change)> Rows()
    {
        var rows = new List<(IChangeSource, ActiveChange)>();
        foreach (var source in _sources)
        {
            try { rows.AddRange(source.List().Select(c => (source, c))); }
            catch (Exception ex)
            {
                _log.Info($"active changes: {source.GetType().Name} not read: {ex.Message}");
                rows.Add((source, source.Unread(ex.Message)));
            }
        }
        return rows.OrderByDescending(r => r.Item2.ChangedUtc ?? DateTime.MaxValue).ToList();
    }

    private UndoResult Run(IChangeSource source, ActiveChange change, bool confirmed)
    {
        if (!change.CanUndo) return new(false, change.Note ?? ChangeWords.CannotPutBack);
        UndoResult result;
        try { result = source.Undo(change, confirmed); }
        catch (Exception ex) { result = new(false, "PoolWatch could not put this change back: " + ex.Message); }
        _log.Info($"undo {change.Id}: {(result.Done ? "done" : "not done")}: {result.Message}");
        try { _undone?.Invoke(change, result); }
        catch (Exception ex) { _log.Info("undo: " + ex.Message); }
        return result;
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        // Only what JSON needs is escaped (a quote, a backslash, a control character): the uninstaller reads the names
        // as they are, and the file stays valid JSON for CI and support.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The list as JSON (<c>--changes-dump</c>: CI, support and the uninstaller). Each row is an object of its own, one
    /// property per line, with <see cref="ActiveChange.UninstallLeaves"/> when the uninstaller leaves it as it is and
    /// <see cref="ActiveChange.PutBackCaveat"/> when something stays for a while after it is put back: the uninstaller
    /// reads <c>what</c>, <c>uninstallLeaves</c> and <c>putBackCaveat</c> line by line to say what it puts back
    /// (installer/PoolWatch.iss, <c>ReadChangeList</c>), so keep that shape.
    /// </summary>
    public static string ToJson(IReadOnlyList<ActiveChange> list) => JsonSerializer.Serialize(list, Json);
}
