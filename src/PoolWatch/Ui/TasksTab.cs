using PoolWatch.Analysis;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Identity;
using PoolWatch.Core.Model;
using PoolWatch.Core.Services;
using PoolWatch.Engine;

namespace PoolWatch.Ui;

/// <summary>An app that has been heavy in the background, keyed by PID AND create time so "End it now" never ends a reused PID.</summary>
internal sealed record BusyApp(ProcessKey Key, string Image, string Identity, string? CommandLine);

/// <summary>
/// When the Tasks and startup apps tab reads the tasks and startup apps again (pure, so it is tested): never while a read
/// runs; on a refresh once the last try is <see cref="TasksTab.ReadEvery"/> old, a failed try too (so a read that fails is
/// tried again then, not on every refresh); at once on Refresh and after a switch (<see cref="Start"/>).
/// <see cref="Loaded"/> once a read succeeded; <see cref="Error"/> why the last one failed.
/// </summary>
internal sealed class TaskReadClock(TimeSpan every)
{
    private DateTime _triedUtc = DateTime.MinValue;

    public bool Reading { get; private set; }
    public bool Loaded { get; private set; }
    public string? Error { get; private set; }

    /// <summary>A refresh reads again now.</summary>
    public bool Due(DateTime nowUtc) => !Reading && nowUtc - _triedUtc >= every;

    /// <summary>A read starts now; false while one runs (that one's result shows).</summary>
    public bool Start(DateTime nowUtc)
    {
        if (Reading) return false;
        Reading = true;
        _triedUtc = nowUtc;
        return true;
    }

    /// <summary>The read ended: <paramref name="error"/> null when it read them.</summary>
    public void Finished(string? error)
    {
        Reading = false;
        Error = error;
        Loaded |= error is null;
    }

    /// <summary>
    /// Something changed the list elsewhere (an AI agent's answer applied from the Services tab): the next refresh reads it
    /// again, also when a read already ran since (it may have read the list before the change).
    /// </summary>
    public void Stale() => _triedUtc = DateTime.MinValue;
}

/// <summary>
/// The Tasks and startup apps tab at work (moved out of the main window, which only adds its page): it reads the
/// non-Microsoft scheduled tasks and every startup app in the background when the tab shows, again after a switch, on
/// Refresh and every few minutes while it shows (<see cref="TaskReadClock"/>); on each refresh while it shows it works out
/// again from the live processes what each costs in the latest sampling window, adds the apps busy in the background for
/// 30 minutes, and updates the list in place. A search, a "Show" tick, a segment or a header click only filters and
/// sorts those rows again. The detail panel acts on the selected row (<see cref="TaskActions"/>): the switch (recorded for
/// Undo first, the same switch as Task Manager's Startup apps or Task Scheduler's Disable), "End it now" (only for the
/// processes it can be sure of, each ended through a handle checked against its start time, after one question) and
/// "Open file location". While the list is read again, the switch and "End it now" wait.
/// </summary>
internal sealed class TasksTab
{
    /// <summary>How long the tasks and startup apps as read stay current while the tab shows.</summary>
    public static readonly TimeSpan ReadEvery = TimeSpan.FromMinutes(5);

    private readonly Form _owner;
    private readonly SetupContext _context;
    private readonly MonitorEngine _engine;
    private readonly AnalysisPipeline _analysis;
    private readonly UiActions _actions;
    private readonly TasksAndStartup _tasks;
    private readonly ListSync _sync;
    private readonly ColumnSort _sort;
    private readonly TaskReadClock _clock = new(ReadEvery);
    private List<TaskEntry> _entries = [];
    private Dictionary<string, TaskRow> _byKey = new();
    private List<TaskRow> _rows = [];
    private (string Title, string Line)? _empty;
    private bool _sortChanged;

    public TasksTab(Form owner, ToolTip tips, SetupContext context)
    {
        _owner = owner;
        _context = context;
        _engine = context.Engine;
        _analysis = context.Analysis;
        _actions = context.Actions;
        _tasks = new TasksAndStartup(_engine.Log);
        View = new TasksTabView(owner, tips);
        _sync = new ListSync(View.List);
        View.List.RowOf = key => key is string k ? _byKey.GetValueOrDefault(k) : null;
        // Ranked by what each costs now, most first; a click on a header sorts by it, a second click reverses.
        _sort = new ColumnSort(View.List, TaskRows.CostColumn, ascending: false, () => { _sortChanged = true; Show(); });
        View.FilterChanged += (_, _) => _actions.Guarded(owner, "Filter startup apps and tasks", () => { _sortChanged = true; Show(); });
        View.List.SelectedIndexChanged += (_, _) => { if (!_sync.Updating) ShowDetail(); };
        View.Refresh.Click += (_, _) => _actions.Guarded(owner, "Refresh startup apps and tasks", Read);
        View.Detail.Toggle.Click += (_, _) => _actions.Guarded(owner, "Switch a startup app or task", ToggleSelected);
        View.Detail.End.Click += (_, _) => _actions.Guarded(owner, "End it now", EndSelected);
        View.Detail.Open.Click += (_, _) => _actions.Guarded(owner, "Open file location", OpenSelected);
    }

    public TasksTabView View { get; }
    public TabPage Page => View.Page;

    /// <summary>Every row as worked out at the last refresh, the filter's hidden ones too (CI, the AI agent's prompt).</summary>
    public IReadOnlyList<TaskRow> Rows => _rows;

    /// <summary>The tasks and startup apps have been read once.</summary>
    public bool Loaded => _clock.Loaded;

    /// <summary>A change made elsewhere (an AI agent's answer applied): the list is read again when the tab next refreshes.</summary>
    public void ReadAgain() => _clock.Stale();

    /// <summary>Every refresh while the tab shows: read again when due (in the background), else work the costs out again.</summary>
    public void Refresh()
    {
        if (_clock.Due(DateTime.UtcNow)) Read();
        else Recost();
    }

    /// <summary>Reads the tasks and startup apps in the background (Task Scheduler and the registry take a moment), then shows them.</summary>
    private void Read()
    {
        if (!_clock.Start(DateTime.UtcNow)) return;
        View.Refresh.Enabled = false;
        ShowDetail(); // the switch and "End it now" wait for the list
        Task.Run(() => Entries(_tasks.Tasks(), _tasks.Startup(), Environment.ExpandEnvironmentVariables)).ContinueWith(t =>
        {
            if (!_owner.IsHandleCreated || _owner.IsDisposed) return;
            try
            {
                _owner.BeginInvoke(() =>
                {
                    View.Refresh.Enabled = true;
                    if (t.IsFaulted)
                    {
                        string why = t.Exception!.GetBaseException().Message;
                        _engine.Log.Info("startup apps and tasks not read: " + why);
                        _clock.Finished(why);
                        Show();
                        return;
                    }
                    _entries = t.Result;
                    _clock.Finished(null);
                    Recost();
                });
            }
            catch (InvalidOperationException) { } // the window is closing
        });
    }

    /// <summary>The entries of the scheduled tasks (non-Microsoft, as read) and the startup apps (Microsoft's marked as such).</summary>
    internal static List<TaskEntry> Entries(IEnumerable<ScheduledTaskView> tasks, IEnumerable<StartupView> startup, Func<string, string> expand)
    {
        var list = new List<TaskEntry>();
        foreach (var t in tasks)
        {
            string? exe = EntryCost.ExeOf(t.Runs, expand);
            list.Add(new TaskEntry("task:" + t.Path, TaskKind.ScheduledTask, t.Name, t.Author is { Length: > 0 } a ? a : EntryCost.InstallFolderName(exe),
                t.Starts, t.Runs, t.Enabled, false, t, exe));
        }
        foreach (var su in startup)
        {
            string? exe = EntryCost.ExeOf(su.Command, expand);
            string name = su.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? su.Name[..^4] : su.Name;
            list.Add(new TaskEntry($"startup:{su.Location}\\{su.Name}", TaskKind.StartupApp, name, EntryCost.InstallFolderName(exe),
                "", su.Command, su.Enabled, su.Microsoft, su, exe));
        }
        return list;
    }

    /// <summary>
    /// What an entry's program costs now: the live processes running it, matched by path (by name when a path is unknown),
    /// at <paramref name="cost"/> (the latest sampling window's, <see cref="ProcessLoad.CostNow"/>).
    /// </summary>
    internal static TaskCost CostOf(TaskEntry e, IReadOnlyList<LiveImage> live, Func<ProcessKey, (double cores, long faultsPerSec)?> cost)
    {
        if (e.Exe is null) return TaskCost.NotRunning;
        if (EntryCost.ViaLauncher(e.Exe)) return TaskCost.NotMeasurable;
        return EntryCost.Match(e.Exe, live, cost) is { } r
            ? new TaskCost(true, true, r.Cores, r.PageFaultsPerSec, r.Processes, r.ByFullPath ? r.Keys : [], Measured: r.Measured)
            : TaskCost.NotRunning;
    }

    /// <summary>The entries' rows, each with what its program costs in <paramref name="load"/>, the latest sampling window (none yet: not measured).</summary>
    internal static List<TaskRow> EntryRows(IEnumerable<TaskEntry> entries, IReadOnlyList<LiveImage> live, ProcessLoad.Rates? load)
    {
        var now = ProcessLoad.CostNow(load);
        return entries.Select(e => new TaskRow(e, CostOf(e, live, now))).ToList();
    }

    /// <summary>
    /// The rows as they are now: the entries with what they cost in the latest sampling window ("Cost now" is now, not an
    /// average), then the apps busy in the background (at most ten, their 30-minute average, which the detail panel says).
    /// </summary>
    private List<TaskRow> WorkOut()
    {
        var d = _analysis.Diagnostics;
        var live = _engine.Registry.LiveRecords().Select(r => new LiveImage(r.Key, r.Image, r.ImagePath)).ToList();
        var entries = EntryRows(_entries, live, d.Load);
        var busy = new List<TaskRow>();
        // Heavy background apps: over 0.5 core averaged over the last 30 minutes, never with a window in front (judged on
        // minute points, not one 10 s window).
        foreach (var h in d.HeavyBackground().Take(10))
        {
            var rec = _engine.Registry.Get(h.Key);
            string identity = rec is not null ? ProcessIdentities.Of(_engine.Registry.Ancestry(rec.Key)).Display : h.Image;
            var app = new BusyApp(h.Key, h.Image, identity, rec?.CommandLine);
            string command = rec?.CommandLine ?? rec?.ImagePath ?? h.Image;
            var entry = new TaskEntry($"busy:{h.Key.Pid}:{h.Key.CreateTime}", TaskKind.Busy, identity, EntryCost.InstallFolderName(rec?.ImagePath), "", command,
                null, false, app, rec?.ImagePath);
            busy.Add(new TaskRow(entry, new TaskCost(true, true, h.Cores, h.PageFaultsPerSec, 1, [h.Key], Average: true)));
        }
        // One row per key (the list matches its rows by key): a name read twice is listed once.
        return TaskRows.Merge(entries, busy).DistinctBy(r => r.Key).ToList();
    }

    /// <summary>What each costs, worked out again from the live processes (each refresh, after a read), then the list.</summary>
    private void Recost()
    {
        _rows = WorkOut();
        _byKey = _rows.ToDictionary(r => r.Key);
        Show();
    }

    /// <summary>
    /// The list again from the rows as last worked out: filtered and in the picked order, updated in place (the selection
    /// and place stay); with no row, the list and the panel say why. A cost cell's meter follows its words, which name the
    /// level: a changed cell is drawn again with them.
    /// </summary>
    private void Show()
    {
        var filter = View.Filter;
        var shown = TaskRows.Build(_rows, filter, _sort.Column, _sort.Ascending);
        _sync.Update(shown.Select(r => new ListSync.Row(r.Key, TaskRows.Cells(r), Fore: r.Entry.Microsoft ? Color.Gray : null)).ToList(), forceOrder: _sortChanged);
        _sortChanged = false;
        _empty = TaskRows.Empty(filter, shown.Count, _clock.Loaded, _clock.Error);
        View.List.EmptyText = _empty is { } e ? $"{e.Title} {e.Line}".Trim() : "";
        ShowDetail();
    }

    /// <summary>Selects the list's top row, if any, so the detail panel shows it (CI's screenshot).</summary>
    internal void SelectFirst()
    {
        if (View.List.Items.Count == 0) return;
        View.List.Items[0].Selected = true;
        ShowDetail();
    }

    private TaskRow? Selected() =>
        View.List.SelectedItems.Count > 0 && View.List.SelectedItems[0].Tag is string k ? _byKey.GetValueOrDefault(k) : null;

    private void ShowDetail() => View.Detail.Display(Selected() is { } r ? TaskRows.Detail(r) : null, busy: _clock.Reading, _empty);

    /// <summary>
    /// The switch of the selected startup app or task (<see cref="TaskActions.Toggle"/>): recorded for Undo (Active
    /// changes) before it is switched; a switch that fails takes its record back. Then the list is read again.
    /// </summary>
    private void ToggleSelected()
    {
        if (Selected() is not { } row || TaskActions.Toggle(row) is not { } t) return;
        string? err = ServicesPageText.Switch(t.Source, t.Enable, _context.Ledger.Records, _tasks.SetTaskEnabled, _tasks.SetStartupEnabled);
        if (err is not null) MessageBox.Show(_owner, err, "PoolWatch");
        // Every change is on the timeline, like the service changes, and under Active changes.
        else if (TaskActions.Timeline(t) is { Length: > 0 } what) _analysis.Diagnostics.RecordOwnerChange(what, row.Entry.Name);
        Read();
    }

    /// <summary>
    /// "End it now" (<see cref="TaskActions.End"/>): never a core or security process, a service host or a VM's memory
    /// process (EndProcessPolicy); one question naming what is ended, asked while the first process's handle is held; each
    /// process opened first, its create time read from that handle and compared exactly, and ended through that same
    /// handle (ProcessEnder).
    /// </summary>
    private void EndSelected()
    {
        var row = Selected();
        var plan = TaskActions.End(row, k => _engine.Registry.Get(k)?.Image,
            pid => _engine.Services.Where(s => s.ProcessId == pid).Select(s => s.Name).ToList());
        if (row is null || plan.Refusal is not null) { MessageBox.Show(_owner, plan.Refusal ?? TaskActions.SelectRunning, "PoolWatch"); return; }
        string identity = row.Entry.Name;
        var problems = TaskActions.RunEnd(plan, identity, new WindowsProcessOpener(),
            () => MessageBox.Show(_owner, TaskActions.EndQuestion(row, plan.Keys), "PoolWatch: end it now", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK,
            key => _analysis.Diagnostics.RecordOwnerChange($"Process ended by you: {identity} (PID {key.Pid})", Path.GetFileName(row.Entry.Exe ?? identity)));
        if (problems.Count > 0) MessageBox.Show(_owner, string.Join("\n", problems), "PoolWatch");
        Recost();
    }

    /// <summary>"Open file location": the program's file selected in Explorer, when it is a full path to a file that is there.</summary>
    private void OpenSelected()
    {
        if (Selected()?.Entry.Exe is not { } exe) { MessageBox.Show(_owner, "Select a row first.", "PoolWatch"); return; }
        if (!Path.IsPathFullyQualified(exe) || !File.Exists(exe)) { MessageBox.Show(_owner, $"PoolWatch cannot find {exe}.", "PoolWatch"); return; }
        UiActions.ShowFile(exe);
    }
}
