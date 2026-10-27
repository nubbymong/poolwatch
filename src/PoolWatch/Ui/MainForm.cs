using System.Diagnostics;
using PoolWatch.Analysis;
using PoolWatch.Core;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Util;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Engine;

namespace PoolWatch.Ui;

/// <summary>
/// The main window (DESIGN.md section 11). Refreshes on a 10 s timer that runs only while the window is visible and not
/// minimised; nothing is rendered or recomputed while it is hidden.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly MonitorEngine _engine;
    private readonly AnalysisPipeline _analysis;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 10_000 };
    private readonly PoolChart _chart = new() { Dock = DockStyle.Top, Height = 190 };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly ListView _tags = NewList(("Tag", 60), ("Owner", 110), ("Description", 240), ("Size", 80), ("1 min", 70), ("10 min", 70), ("60 min", 70), ("State", 80),
        ("Allocs/min", 80), ("Frees/min", 80), ("Shape (10 min)", 230));
    private readonly ListView _owners = NewList(("Suspect (who it works for)", 300), ("Likelihood", 90), ("Why", 340));
    private readonly ListView _eventsList = NewList(("Event", 900));
    private readonly TextBox _answer = new() { Dock = DockStyle.Top, ReadOnly = true, Multiline = true, Height = 50, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, WordWrap = true, Font = new Font("Segoe UI", 10f, FontStyle.Bold), BackColor = SystemColors.Window };
    private readonly TextBox _details = new() { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, BackColor = SystemColors.Window, Font = new Font("Segoe UI", 9f) };
    private readonly ActivityStrip _strip = new() { Dock = DockStyle.Top, Height = 130 };
    private readonly LinkLabel _showMore = new() { Dock = DockStyle.Bottom, Height = 20, Text = "Show more" };
    private readonly CheckBox _allEvents = new() { Text = "Show all events (not just notable ones and summaries)", AutoSize = true, Dock = DockStyle.Top };
    private readonly GroupBox _suspectsBox = new() { Text = "Suspects", Dock = DockStyle.Fill };
    private readonly TextBox _overview = new() { Dock = DockStyle.Top, ReadOnly = true, Multiline = true, Height = 170, WordWrap = true, BorderStyle = BorderStyle.None, BackColor = SystemColors.Window, Font = new Font("Segoe UI", 10.5f) };
    private readonly LagChart _lagChart = new() { Dock = DockStyle.Bottom, Height = 170 };
    private readonly PoolChart _bootChart = new() { Dock = DockStyle.Fill, Title = "Kernel pool since PoolWatch started (minute averages; up to 25 h)" };
    private bool _showAllOwners;
    private bool _tagSelectPending;
    private bool _forceOrder;
    private TagViewData? _view;
    private DateTime _viewAt = DateTime.MinValue;
    private readonly CheckBox _growingOnly = new() { Text = "Growing / retained / released only", Checked = true, AutoSize = true, Dock = DockStyle.Top };
    private readonly ListView _incidents = NewList(("Opened", 130), ("Tag", 60), ("Kind", 100), ("Growth", 80), ("Top suspect", 240), ("Confidence", 130), ("State", 60), ("Folder", 300));
    private readonly ListView _processes = NewList(("Process", 230), ("PID", 60), ("Parent", 60), ("I/O ops/s", 90), ("Handles", 80), ("Quota pool", 90), ("Private", 90), ("Orphan", 60), ("Command line", 500));
    private readonly ListView _findings = NewList(("Time", 130), ("Kind", 100), ("Severity", 70), ("Finding", 760), ("Folder", 260));
    private readonly ListView _services = new() { View = View.Details, FullRowSelect = true, CheckBoxes = true, Dock = DockStyle.Fill, MultiSelect = true, HideSelection = false };
    private readonly Label _servicesStatus = new() { Dock = DockStyle.Top, AutoSize = false, Height = 40, Padding = new Padding(4) };
    private readonly ListView _tasks = new() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, MultiSelect = false, HideSelection = false };
    private readonly ListView _spawnRates = NewList(("Parent", 220), ("PID", 60), ("Child image", 160), ("Starts", 70), ("Short-lived", 80));
    private readonly TextBox _tokenSummary = new() { Dock = DockStyle.Top, ReadOnly = true, Multiline = true, Height = 36, BorderStyle = BorderStyle.None, BackColor = SystemColors.Window, WordWrap = true };
    private readonly PropertyGrid _settingsGrid = new() { Dock = DockStyle.Fill, ToolbarVisible = false };
    private readonly CheckBox _logon = new() { Text = "Start at logon (scheduled task, highest privileges: no daily UAC prompt)", AutoSize = true };
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _orderHint = new() { ForeColor = Color.DarkSlateBlue };
    private Settings _editSettings;
    private PoolTag? _selectedTag;
    private int _procSortColumn = 3;
    private bool _repopulating;
    private bool? _logonPending;
    private bool _procSortDesc = true;

    public MainForm(MonitorEngine engine, AnalysisPipeline analysis)
    {
        _engine = engine;
        _tasksEngine = new Engine.TasksAndStartup(engine.Log);
        _analysis = analysis;
        _editSettings = Clone(engine.Settings);
        Text = "PoolWatch";
        Icon = TrayIcons.For(TrayState.Green);
        Width = 1200;
        Height = 820;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);

        var tools = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        tools.Items.Add(new ToolStripButton("Capture now", null, (_, _) => Guarded("Capture now", CaptureNow)) { ToolTipText = "Run the confirmation trace now (bounded: pool events + Kernel-File, plus CPU when CpuTraceEnabled; not while another capture runs)" });
        tools.Items.Add(new ToolStripButton("Since boot", null, (_, _) => Guarded("Since boot", SinceBoot)) { ToolTipText = "What has grown since boot, and what drove it, hour by hour" });
        tools.Items.Add(new ToolStripButton("Open incident folder", null, (_, _) => Guarded("Open incident folder", OpenIncidentFolder)));
        tools.Items.Add(new ToolStripButton("Export report", null, (_, _) => Guarded("Export report", Export)) { ToolTipText = "Zip the selected incident, optionally redacted" });
        tools.Items.Add(new ToolStripButton("Export vendor bundle", null, (_, _) => Guarded("Export vendor bundle", ExportBundle))
            { ToolTipText = "One zip for an app vendor: Windows build and app versions, boot baselines, samples, tag snapshots, filters, git / spawn traces, crashes (redacted by default)" });
        tools.Items.Add(new ToolStripButton("Copy summary", null, (_, _) => Guarded("Copy summary", CopySummary)) { ToolTipText = "Copy the selected tag's answer and suspects, or the selected incident's summary, as plain text" });
        tools.Items.Add(new ToolStripSeparator());
        tools.Items.Add(new ToolStripButton("Clear cache now", null, (_, _) => Guarded("Clear cache", ClearCacheNow))
            { ToolTipText = "Empty the Windows standby list (cached file pages); the per-file ReFS / filter-manager pool they keep alive is freed with them. Measured: pool before and 2 min after." });
        // S5 (review round 2): no setup any more (the purge is in-process). This appears only when an older PoolWatch's two
        // SYSTEM tasks are still installed, to remove them.
        tools.Items.Add(new ToolStripButton("Remove old clear-cache tasks", null, (_, _) => Guarded("Remove old clear-cache tasks", RemoveOldClearTasks))
            { ToolTipText = @"An older PoolWatch installed two SYSTEM tasks (\PoolWatch\Clear cache ...) and a helper in Program Files for Clear cache; they are no longer used. Removes them after you say yes.", Visible = false, Name = "removeOldTasks" });
        tools.Items.Add(new ToolStripButton("Remove my clear task", null, (_, _) => Guarded("Remove task", RemoveLegacyTask))
            { ToolTipText = "Shows your own clear task (a scheduled task that runs RAMMap with an -E option) and removes it after you say yes; RAMMap itself stays in place", Visible = false, Name = "removeLegacy" });
        tools.Items.Add(new ToolStripSeparator());
        tools.Items.Add(new ToolStripButton("About", null, (_, _) => About()));
        _tools = tools;

        _tabs.TabPages.Add(OverviewPage());
        _tabs.TabPages.Add(TagsPage());
        _tabs.TabPages.Add(Page("Incidents", _incidents));
        _tabs.TabPages.Add(Page("Processes", _processes));
        _tabs.TabPages.Add(FindingsPage());
        _tabs.TabPages.Add(ServicesPage());
        _tabs.TabPages.Add(SettingsPage());

        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_status);
        statusStrip.Items.Add(_orderHint);
        statusStrip.ContextMenuStrip = new ContextMenuStrip();
        statusStrip.ContextMenuStrip.Items.Add("Copy status", null, (_, _) => Copy(_status.Text ?? ""));
        // Copy: Ctrl+C (tab-separated, with a header row) and a right-click menu on every list.
        EnableCopy(_tags, _ => null);
        EnableCopy(_owners, OwnerCopy);
        EnableCopy(_eventsList, _ => null);
        EnableCopy(_incidents, k => k is string dir ? new CopyInfo(null, null, dir, null, () => IncidentSummary(dir)) : null);
        EnableCopy(_processes, k => k is ProcessKey pk && _engine.Registry.Get(pk) is { } r ? new CopyInfo(r.Pid.ToString(), r.CommandLine, r.ImagePath, null, null) : null);
        EnableCopy(_findings, k => k is Finding f ? new CopyInfo(f.Pid != 0 ? f.Pid.ToString() : null, f.Data.GetValueOrDefault("commandLine") as string, f.ReportDir,
            string.Join("\r\n", new[] { f.Title }.Concat(f.Details)), null) : null);
        EnableCopy(_spawnRates, _ => null);
        foreach (var tb in new[] { _answer, _details, _overview }) EnableTextCopy(tb);
        Controls.Add(_tabs);
        Controls.Add(_chart);
        Controls.Add(tools);
        Controls.Add(statusStrip);

        // Selection changes are debounced (a click deselects, then selects; Ctrl+A or Shift-click fire once per row): the
        // view is rebuilt once, after the burst, and only when the selected tag really changed.
        _tags.SelectedIndexChanged += (_, _) =>
        {
            if (_repopulating || _tagSelectPending) return;
            _tagSelectPending = true;
            BeginInvoke(() =>
            {
                _tagSelectPending = false;
                PoolTag? tag = _tags.SelectedItems.Count > 0 ? (PoolTag)_tags.SelectedItems[0].Tag! : null;
                if (tag == _selectedTag) return;
                _selectedTag = tag;
                _view = null;
                SafeRefresh(RefreshSuspects);
            });
        };
        _owners.SelectedIndexChanged += (_, _) => { if (!_repopulating) SafeRefresh(ShowDetails); };
        _showMore.LinkClicked += (_, _) => { _showAllOwners = !_showAllOwners; SafeRefresh(RefreshSuspects); };
        _allEvents.CheckedChanged += (_, _) => { _view = null; SafeRefresh(RefreshSuspects); };
        _growingOnly.CheckedChanged += (_, _) => { _tagsSamples = -1; SafeRefresh(RefreshTags); };
        _incidents.DoubleClick += (_, _) => OpenIncidentFolder();
        _findings.DoubleClick += (_, _) => { if (_findings.SelectedItems.Count > 0 && _findings.SelectedItems[0].Tag is Finding { ReportDir: { } d } && Directory.Exists(d)) OpenFolder(d); };
        _processes.ColumnClick += (_, e) => { _procSortDesc = e.Column == _procSortColumn ? !_procSortDesc : true; _procSortColumn = e.Column; _forceOrder = true; SafeRefresh(RefreshProcesses); _forceOrder = false; };
        _tabs.SelectedIndexChanged += (_, _) => RefreshNow();
        _timer.Tick += (_, _) => RefreshNow();
        VisibleChanged += (_, _) => UpdateTimer();
        HandleCreated += (_, _) => { if (_logonPending is { } v) { _logonPending = null; BeginInvoke(() => WireLogon(v)); } };
        Resize += (_, _) => UpdateTimer();
    }

    /// <summary>Start-up (JIT, first scans, tag scan) is not steady state: overhead is shown only after this long.</summary>
    private const int SteadySeconds = 300;

    private static Settings Clone(Settings s) => System.Text.Json.JsonSerializer.Deserialize<Settings>(System.Text.Json.JsonSerializer.Serialize(s))!;

    private static ListView NewList(params (string name, int width)[] cols)
    {
        var lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true };
        foreach (var (n, w) in cols) lv.Columns.Add(n, w);
        return lv;
    }

    private static TabPage Page(string title, Control c)
    {
        var p = new TabPage(title);
        p.Controls.Add(c);
        return p;
    }

    private TabPage OverviewPage()
    {
        var page = new TabPage("Overview");
        page.Controls.Add(_bootChart);
        page.Controls.Add(_lagChart);
        page.Controls.Add(_overview);
        return page;
    }

    private TabPage TagsPage()
    {
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 190 };
        split.Panel1.Controls.Add(_tags);
        split.Panel1.Controls.Add(_growingOnly);
        // Lower half: the answer line, the activity strip, the owners (top 3) with a details pane, and the events.
        var lower = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 760 };
        var ownersSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 110 };
        ownersSplit.Panel1.Controls.Add(_owners);
        ownersSplit.Panel1.Controls.Add(_showMore);
        ownersSplit.Panel2.Controls.Add(_details);
        _suspectsBox.Controls.Add(ownersSplit);
        _suspectsBox.Controls.Add(_strip);
        _suspectsBox.Controls.Add(_answer);
        var eventsBox = new GroupBox { Text = "Events in the window", Dock = DockStyle.Fill };
        eventsBox.Controls.Add(_eventsList);
        eventsBox.Controls.Add(_allEvents);
        lower.Panel1.Controls.Add(_suspectsBox);
        lower.Panel2.Controls.Add(eventsBox);
        split.Panel2.Controls.Add(lower);
        var page = new TabPage("Growing tags");
        page.Controls.Add(split);
        return page;
    }

    private TabPage FindingsPage()
    {
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 300 };
        split.Panel1.Controls.Add(_findings);
        var box = new GroupBox { Text = "Process starts per parent and child image (current window; short-lived = exited quickly)", Dock = DockStyle.Fill };
        box.Controls.Add(_spawnRates);
        split.Panel2.Controls.Add(box);
        split.Panel2.Controls.Add(_tokenSummary);
        var page = new TabPage("Findings");
        page.Controls.Add(split);
        return page;
    }

    private readonly ComboBox _preset = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };

    private TabPage ServicesPage()
    {
        foreach (var (h, w) in new[] { ("Service", 220), ("Group", 90), ("Category", 150), ("State", 260), ("Cost", 200), ("You lose / publisher", 380) })
            _services.Columns.Add(h, w);
        _preset.Items.AddRange(["Custom", "Telemetry only", "Dev workstation"]);
        _preset.SelectedIndex = 0;
        _preset.SelectedIndexChanged += (_, _) => Guarded("Preset", ApplyPresetTicks);
        var apply = new Button { Text = "Apply (turn off ticked)", AutoSize = true };
        apply.Click += (_, _) => Guarded("Apply services", ApplyServices);
        var restoreOne = new Button { Text = "Restore selected", AutoSize = true };
        restoreOne.Click += (_, _) => Guarded("Restore service", RestoreSelected);
        var restoreDefault = new Button { Text = "Restore selected to Windows default", AutoSize = true };
        restoreDefault.Click += (_, _) => Guarded("Restore to Windows default", RestoreSelectedToDefault);
        var restore = new Button { Text = "Restore all", AutoSize = true };
        restore.Click += (_, _) => Guarded("Restore services", () => DoRestore(null));
        var refresh = new Button { Text = "Refresh", AutoSize = true };
        refresh.Click += (_, _) => Guarded("Refresh services", () => { _servicesLoaded = false; RefreshServices(); });
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(4) };
        bar.Controls.AddRange([new Label { Text = "Preset:", AutoSize = true, Padding = new Padding(4, 8, 2, 0) }, _preset, apply, restoreOne, restoreDefault, restore, refresh]);
        _servicesStatus.Text = "PoolWatch recommends; you choose; every change is reversible (Restore). Protected services (security, Windows Update, input/audio, core) are greyed out.";
        foreach (var (h, w) in new[] { ("Type", 70), ("Name", 240), ("State", 150), ("Cost", 220), ("Runs / command", 520) }) _tasks.Columns.Add(h, w);
        var taskDisable = new Button { Text = "Disable", AutoSize = true };
        taskDisable.Click += (_, _) => Guarded("Disable", () => ToggleTaskOrStartup(false));
        var taskEnable = new Button { Text = "Enable", AutoSize = true };
        taskEnable.Click += (_, _) => Guarded("Enable", () => ToggleTaskOrStartup(true));
        var appEnd = new Button { Text = "End process", AutoSize = true };
        appEnd.Click += (_, _) => Guarded("End process", EndSelectedApp);
        var appOpen = new Button { Text = "Open file location", AutoSize = true };
        appOpen.Click += (_, _) => Guarded("Open location", OpenSelectedAppLocation);
        var cBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(4) };
        cBar.Controls.AddRange([taskDisable, taskEnable, appEnd, appOpen]);
        var cBox = new GroupBox { Text = "Non-Microsoft scheduled tasks, startup apps, and heavy background apps (same rules: you choose, reversible)", Dock = DockStyle.Fill };
        cBox.Controls.Add(_tasks);
        cBox.Controls.Add(cBar);
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 420 };
        split.Panel1.Controls.Add(_services);
        split.Panel1.Controls.Add(_servicesStatus);
        split.Panel2.Controls.Add(cBox);
        var page = new TabPage("Background services");
        page.Controls.Add(split);
        page.Controls.Add(bar);
        _services.ItemChecked += (_, e) => { if (!_repopulating && e.Item.Tag is Core.Services.ServiceOption o && !o.CanTurnOff && e.Item.Checked) e.Item.Checked = false; };
        return page;
    }

    private bool _servicesLoaded;
    private List<Core.Services.ServiceOption> _serviceOptions = new();

    private void RefreshServices()
    {
        if (_servicesLoaded) { UpdateServiceCosts(); return; }
        _servicesLoaded = true;
        _servicesStatus.Text = "Reading services…";
        Task.Run(() => _analysis.Diagnostics.Services.Options()).ContinueWith(t =>
        {
            if (!IsHandleCreated || IsDisposed || t.IsFaulted) return;
            BeginInvoke(() => ShowServices(t.Result));
        });
        Task.Run(LoadTasksAndApps).ContinueWith(t =>
        {
            if (!IsHandleCreated || IsDisposed || t.IsFaulted) return;
            BeginInvoke(() => ShowTasks(t.Result));
        });
    }

    private List<(string type, string name, string state, string cost, string runs, object tag)> LoadTasksAndApps()
    {
        var rows = new List<(string, string, string, string, string, object)>();
        var d = _analysis.Diagnostics;
        // Cost per task / startup entry (addendum 4 C): the live processes running its program, matched by image.
        var live = _engine.Registry.LiveRecords().Select(r => new Core.Services.LiveImage(r.Key, r.Image, r.ImagePath)).ToList();
        string Cost(string command)
        {
            string? exe = Core.Services.EntryCost.ExeOf(command, Environment.ExpandEnvironmentVariables);
            var r = Core.Services.EntryCost.ViaLauncher(exe) ? null : Core.Services.EntryCost.Match(exe, live, d.CostOfProcess);
            return Core.Services.EntryCost.Text(exe, r);
        }
        foreach (var t in _tasksEngine.Tasks())
            rows.Add(("task", t.Name, t.Enabled ? "enabled" : "disabled", Cost(t.Runs), t.Runs, t));
        foreach (var su in _tasksEngine.Startup().Where(s => !s.Microsoft))
            rows.Add(("startup", su.Name, su.Enabled ? "enabled" : "disabled", Cost(su.Command), $"{su.Location}: {su.Command}", su));
        // Heavy background apps: over 0.5 core averaged over the last 30 minutes, never with a window in front (judged on
        // minute points, not one 10 s window).
        foreach (var h in d.HeavyBackground().Take(10))
        {
            var rec = _engine.Registry.Get(h.Key);
            string identity = rec is not null ? Core.Identity.ProcessIdentities.Of(_engine.Registry.Ancestry(rec.Key)).Display : h.Image;
            var app = new HeavyApp(h.Key, h.Image, identity, rec?.CommandLine, h.Cores);
            rows.Add(("app", identity, "running, no window in front", $"{Core.Services.ServicePlan.Cost(h.Cores, h.PageFaultsPerSec)} (average over {(int)h.Covered.TotalMinutes} min)",
                $"PID {h.Key.Pid}{(h.Key.CreateTime > 0 ? $", started {Fmt.Clock(DateTime.FromFileTimeUtc(h.Key.CreateTime))}" : "")}{(rec?.CommandLine is { } cl ? ": " + cl : "")}", app));
        }
        return rows;
    }

    /// <summary>A heavy background app, keyed by PID AND create time so End process never kills a reused PID (S6).</summary>
    private sealed record HeavyApp(Core.Model.ProcessKey Key, string Image, string Identity, string? CommandLine, double Cores);

    private void ShowTasks(List<(string type, string name, string state, string cost, string runs, object tag)> rows) =>
        KeepPlace(_tasks, it => it.SubItems.Count > 1 ? it.SubItems[0].Text + "\u001f" + it.SubItems[1].Text : null, () =>
        {
            _tasks.BeginUpdate();
            try
            {
                _tasks.Items.Clear();
                foreach (var (type, name, state, cost, runs, tag) in rows)
                    _tasks.Items.Add(new ListViewItem([type, name, state, cost, runs]) { Tag = tag });
            }
            finally { _tasks.EndUpdate(); }
        });

    private readonly Engine.TasksAndStartup _tasksEngine;

    private void ToggleTaskOrStartup(bool enable)
    {
        if (_tasks.SelectedItems.Count == 0) return;
        var tag = _tasks.SelectedItems[0].Tag;
        string? err = tag switch
        {
            Engine.ScheduledTaskView t => _tasksEngine.SetTaskEnabled(t.Path, enable),
            Engine.StartupView su => _tasksEngine.SetStartupEnabled(su.Location, su.Name, enable),
            _ => "select a scheduled task or startup entry",
        };
        if (err is not null) MessageBox.Show(this, err, "PoolWatch");
        else
        {
            // Every change is on the timeline (addendum 4 B / C), like the service changes.
            string what = tag switch
            {
                Engine.ScheduledTaskView t => $"Scheduled task {(enable ? "enabled" : "disabled")} by you: {t.Path}",
                Engine.StartupView su => $"Startup entry {(enable ? "enabled" : "disabled")} by you: {su.Location}\\{su.Name} (Task Manager shows the same switch)",
                _ => "",
            };
            if (what.Length > 0) _analysis.Diagnostics.RecordOwnerChange(what, tag is Engine.StartupView s ? s.Name : (tag as Engine.ScheduledTaskView)?.Name);
        }
        _servicesLoaded = false; RefreshServices();
    }

    private void EndSelectedApp()
    {
        if (_tasks.SelectedItems.Count == 0 || _tasks.SelectedItems[0].Tag is not HeavyApp app) { MessageBox.Show(this, "Select a background app.", "PoolWatch"); return; }
        // Part 3 review: never a core / security process, a service host or a VM's memory process.
        var hosted = _engine.Services.Where(s => s.ProcessId == app.Key.Pid).Select(s => s.Name).ToList();
        if (Core.Services.EndProcessPolicy.WhyNot(app.Key.Pid, app.Image, hosted) is { } why)
        {
            MessageBox.Show(this, $"PoolWatch will not end {app.Identity}: {why}.", "PoolWatch");
            return;
        }
        // S6 (review round 2): the handle is opened first, the create time read from it and compared exactly, the owner asked,
        // and the process ended through that same handle (Engine.ProcessEnder). An unknown create time ends nothing.
        string detail = $"{app.Identity}\nPID {app.Key.Pid}" +
            (app.Key.CreateTime > 0 ? $", started {Fmt.Clock(DateTime.FromFileTimeUtc(app.Key.CreateTime))}" : "") +
            $"\n{app.Image}" + (app.CommandLine is { } cl ? $"\n{cl}" : "");
        var r = Engine.ProcessEnder.End(new Engine.WindowsProcessOpener(), app.Key, app.Identity, () =>
            MessageBox.Show(this, $"End this process?\n\n{detail}\n\nUnsaved work in it is lost. PoolWatch never uninstalls anything.",
                "PoolWatch: end process", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK);
        if (r.Outcome == Engine.ProcessEnder.Outcome.Cancelled) return;
        if (r.Outcome == Engine.ProcessEnder.Outcome.Ended) _analysis.Diagnostics.RecordOwnerChange($"Process ended by you: {app.Identity} (PID {app.Key.Pid})", app.Image);
        else MessageBox.Show(this, r.Message, "PoolWatch");
        _servicesLoaded = false; RefreshServices();
    }

    private void OpenSelectedAppLocation()
    {
        if (_tasks.SelectedItems.Count == 0 || _tasks.SelectedItems[0].Tag is not HeavyApp app) { MessageBox.Show(this, "Select a background app.", "PoolWatch"); return; }
        try
        {
            string? path = System.Diagnostics.Process.GetProcessById(app.Key.Pid).MainModule?.FileName;
            if (path is not null) ShowFile(path);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "PoolWatch"); }
    }

    private void ShowServices(List<Core.Services.ServiceOption> options)
    {
        _serviceOptions = options.OrderBy(o => o.Group).ThenByDescending(o => o.Service.Cores).ThenBy(o => o.Service.Name).ToList();
        _repopulating = true;
        try { KeepPlace(_services, it => (it.Tag as Core.Services.ServiceOption)?.Service.Name, FillServices); }
        finally { _repopulating = false; }
        int off = _analysis.Diagnostics.Services.Changes.Count;
        _servicesStatus.Text = $"{_serviceOptions.Count} services; {_serviceOptions.Count(o => o.CanTurnOff)} you may turn off, {_serviceOptions.Count(o => o.Group == Core.Services.ServiceGroup.Protected)} protected." +
            (off > 0 ? $" {off} changed by PoolWatch (Restore all to undo)." : "") + " Tick, pick a preset or tick by hand, then Apply.";
    }

    /// <summary>
    /// The Cost cell: the live CPU and page faults of the service's process (a shared host says so), or, for a service
    /// PoolWatch turned off, what that saved over the hour after the change (addendum 4 B).
    /// </summary>
    private string CostCell(Core.Services.ServiceOption o)
    {
        if (o.PoolWatchChange is not null && _analysis.Diagnostics.Services.SavingsText(o.Service.Name) is { } saved) return saved;
        var live = o.Service.Running && o.Service.ProcessId > 0 && _analysis.Diagnostics.CostOfPid(o.Service.ProcessId) is { } c
            ? o.Service with { Cores = c.cores, PageFaultsPerSec = c.faultsPerSec } : o.Service;
        return Core.Services.ServicePlan.CostText(live);
    }

    /// <summary>Every refresh while the page shows: the Cost cells only (the list itself is re-read on Refresh / Apply).</summary>
    private void UpdateServiceCosts()
    {
        foreach (ListViewItem it in _services.Items)
            if (it.Tag is Core.Services.ServiceOption o && it.SubItems.Count > 4 && CostCell(o) is var cost && it.SubItems[4].Text != cost)
                it.SubItems[4].Text = cost;
    }

    private void FillServices()
    {
        _services.BeginUpdate();
        try
        {
            _services.Items.Clear();
            foreach (var o in _serviceOptions)
            {
                string group = o.Group switch { Core.Services.ServiceGroup.Optional => "optional", Core.Services.ServiceGroup.Unknown => "unknown", _ => "protected" };
                string state = Core.Services.ServicePlan.StateText(o);
                if (o.CanRestoreDefault) state += $"; Windows default: {o.WindowsDefault}";
                string cost = CostCell(o);
                string detail = o.Catalogue is { } c ? $"lose: {c.Lose}" + (c.Note is { } n ? $" — {n}" : "") : o.Service.Publisher ?? o.ProtectedReason ?? o.Service.Description ?? "";
                var it = new ListViewItem([o.Service.Name, group, o.Catalogue?.Category ?? "", state, cost, detail]) { Tag = o };
                if (o.Group == Core.Services.ServiceGroup.Protected) it.ForeColor = Color.Gray;
                else if (o.Catalogue is { Risk: Core.Services.ServiceRisk.CheckFirst }) it.ForeColor = Color.DarkGoldenrod;
                _services.Items.Add(it);
            }
        }
        finally { _services.EndUpdate(); }
    }

    private void ApplyPresetTicks()
    {
        var preset = _preset.SelectedIndex switch { 1 => Core.Services.Preset.TelemetryOnly, 2 => Core.Services.Preset.DevWorkstation, _ => Core.Services.Preset.Custom };
        if (preset == Core.Services.Preset.Custom) return;
        var ticked = Core.Services.ServicePlan.Tick(preset, _serviceOptions);
        _repopulating = true;
        foreach (ListViewItem it in _services.Items) it.Checked = it.Tag is Core.Services.ServiceOption o && ticked.Contains(o.Service.Name);
        _repopulating = false;
    }

    private void ApplyServices()
    {
        var ticked = _services.Items.Cast<ListViewItem>().Where(i => i.Checked && i.Tag is Core.Services.ServiceOption o && o.CanTurnOff)
            .Select(i => ((Core.Services.ServiceOption)i.Tag!).Service.Name).ToList();
        if (ticked.Count == 0) { MessageBox.Show(this, "Tick the services to turn off first.", "PoolWatch"); return; }
        bool anyUnknown = ticked.Any(n => _serviceOptions.First(o => o.Service.Name == n).Group == Core.Services.ServiceGroup.Unknown);
        string summary = $"{ticked.Count} service(s) will be stopped and set to not start:\n\n  {string.Join("\n  ", ticked)}\n\nThe original is saved first; Restore puts it back. Continue?";
        if (MessageBox.Show(this, summary, "PoolWatch: turn off services", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        if (anyUnknown && MessageBox.Show(this, "Some are third-party services PoolWatch does not recognise (\"you decide\"). Turn them off too?", "PoolWatch", MessageBoxButtons.YesNo) != DialogResult.Yes)
            ticked = ticked.Where(n => _serviceOptions.First(o => o.Service.Name == n).Group != Core.Services.ServiceGroup.Unknown).ToList();
        var preset = _preset.SelectedIndex switch { 1 => "Telemetry only", 2 => "Dev workstation", _ => "manual" };
        RunServiceOp(() => _analysis.Diagnostics.Services.Apply(ticked, preset));
    }

    private void DoRestore(string? service)
    {
        if (_analysis.Diagnostics.Services.Changes.Count == 0) { MessageBox.Show(this, "Nothing to restore: PoolWatch has not changed any service.", "PoolWatch"); return; }
        RunServiceOp(() => _analysis.Diagnostics.Services.Restore(service));
    }

    private Core.Services.ServiceOption? SelectedService() =>
        _services.SelectedItems.Count > 0 ? _services.SelectedItems[0].Tag as Core.Services.ServiceOption : null;

    /// <summary>Per-service Restore: puts back exactly what PoolWatch saved before changing it (and starts it if it was running).</summary>
    private void RestoreSelected()
    {
        if (SelectedService() is not { } o) { MessageBox.Show(this, "Select a service first.", "PoolWatch"); return; }
        if (o.PoolWatchChange is not { } c) { MessageBox.Show(this, $"PoolWatch has not changed {o.Service.Name}: nothing to restore.", "PoolWatch"); return; }
        string again = Core.Services.ServicePlan.StartAfterRestore(c) ? " and start it again (it was running before)" : "";
        if (MessageBox.Show(this, $"Restore {o.Service.Name} to {c.OriginalStart}{again}?", "PoolWatch: restore service", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        RunServiceOp(() => _analysis.Diagnostics.Services.Restore(o.Service.Name));
    }

    /// <summary>"Restore to Windows default" for a service that is off outside PoolWatch (through the same gate; reversible with Restore).</summary>
    private void RestoreSelectedToDefault()
    {
        if (SelectedService() is not { } o) { MessageBox.Show(this, "Select a service first.", "PoolWatch"); return; }
        if (!o.CanRestoreDefault)
        {
            string why = o.Group == Core.Services.ServiceGroup.Protected ? "it is a protected service"
                : o.PoolWatchChange is not null ? "PoolWatch changed it: use Restore"
                : o.OffOutsidePoolWatch != Core.Services.ServicePlan.OffOutside ? "it is not off outside PoolWatch"
                : "PoolWatch does not know its Windows default start type (set it in services.msc)";
            MessageBox.Show(this, $"{o.Service.Name}: not offered: {why}.", "PoolWatch");
            return;
        }
        if (MessageBox.Show(this, $"Set {o.Service.Name} back to its Windows default ({o.WindowsDefault})?\n\nIt starts at the next boot or when something asks for it. " +
                "Your current setting (Disabled) is saved first; Restore puts it back.", "PoolWatch: Windows default", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        RunServiceOp(() => _analysis.Diagnostics.Services.RestoreToDefault(o.Service.Name));
    }

    /// <summary>Runs a service change off the UI thread (it can take seconds), then re-reads the list.</summary>
    private void RunServiceOp(Func<List<(string service, string? error)>> op)
    {
        _servicesStatus.Text = "Applying…";
        Task.Run(op).ContinueWith(t =>
        {
            if (!IsHandleCreated || IsDisposed) return;
            BeginInvoke(() =>
            {
                string msg = t.IsFaulted ? t.Exception!.GetBaseException().Message
                    : string.Join("\n", t.Result.Select(r => r.error is null ? $"{r.service}: done" : $"{r.service}: {r.error}"));
                MessageBox.Show(this, msg, "PoolWatch: services");
                _servicesLoaded = false;
                RefreshServices();
            });
        });
    }

    private TabPage SettingsPage()
    {
        _settingsGrid.SelectedObject = _editSettings;
        var save = new Button { Text = "Save settings", AutoSize = true };
        save.Click += (_, _) => Guarded("Save settings", () =>
        {
            _editSettings.Normalize();
            if (!_editSettings.AutoClearEnabled) _autoClearConfirmed = false;
            bool turningOnAutoClear = _editSettings.AutoClearEnabled && !_engine.Settings.AutoClearEnabled && !_autoClearConfirmed;
            if (turningOnAutoClear)
            {
                // The automatic clear goes on only after the elevated step has dealt with any clear task of your own.
                _editSettings.AutoClearEnabled = false;
                SaveSettings(null);
                ConfirmAutoClear();
                return;
            }
            SaveSettings("Saved. Changes take effect after PoolWatch restarts.");
        });
        // schtasks /query can take seconds: ask in the background, then wire the checkbox.
        _logon.Enabled = false;
        Task.Run(SafeExists).ContinueWith(t =>
        {
            if (!IsHandleCreated || IsDisposed) { _logonPending = t.Result; return; }
            BeginInvoke(() => WireLogon(t.Result));
        });
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(4) };
        bar.Controls.Add(save);
        bar.Controls.Add(_logon);
        var page = new TabPage("Settings");
        page.Controls.Add(_settingsGrid);
        page.Controls.Add(bar);
        return page;
    }

    private void WireLogon(bool exists)
    {
        _logon.Checked = exists;
        _logon.Enabled = true;
        _logon.CheckedChanged += (_, _) => Guarded("Start at logon", () =>
        {
            var (ok, output) = LogonTask.Set(_logon.Checked, Path.Combine(_engine.Paths.DataRoot, "logon-task.xml"), _engine.Paths.Guard.CheckReal);
            if (!ok) MessageBox.Show(this, "Could not change the logon task:\n" + output, "PoolWatch");
        });
    }

    private static bool SafeExists()
    {
        try { return LogonTask.Exists(); } catch { return false; }
    }

    private void UpdateTimer()
    {
        bool active = Visible && WindowState != FormWindowState.Minimized;
        _timer.Enabled = active;
        if (active) RefreshNow();
    }

    private long _chartSamples = -1;

    public void RefreshNow()
    {
        if (!Visible || WindowState == FormWindowState.Minimized) return;
        SafeRefresh(RefreshVisible);
    }

    private void RefreshVisible()
    {
        using var _ui = Core.Util.Profiler.Stage("ui.refresh");
        if (_engine.SampleCount != _chartSamples) // only when there is a new sample
        {
            _chartSamples = _engine.SampleCount;
            _chart.SetData(UiData.Chart(_analysis, TimeSpan.FromHours(6)), UiData.IncidentMarkers(_analysis));
        }
        using (Core.Util.Profiler.Stage("ui.page" + _tabs.SelectedIndex))
            switch (_tabs.SelectedIndex)
            {
                case 0: RefreshOverview(); break;
                case 1: RefreshTags(); break;
                case 2: RefreshIncidents(); break;
                case 3: RefreshProcesses(); break;
                case 4: RefreshFindings(); break;
                case 5: RefreshServices(); break;
            }
        Icon = TrayIcons.For(UiData.State(_analysis));
        _status.Text = $"{_engine.SampleCount} samples · ETW {(_engine.EtwRunning ? _engine.EtwMode : "not running")} · " +
                       $"pooltag.txt {(_analysis.Knowledge.PoolTagTxtLoaded ? "loaded" : "not found")} · {_engine.Meter.OwnCostLine()}";
        _status.ForeColor = _status.Text.Contains("OVER BUDGET", StringComparison.Ordinal) ? Color.DarkRed : SystemColors.ControlText;
    }

    private long _tagsSamples = -1;

    private void RefreshTags()
    {
        // The tag table only changes with a new sample; the suspects below have their own 30 s clock.
        if (_engine.SampleCount == _tagsSamples) { RefreshSuspects(); return; }
        _tagsSamples = _engine.SampleCount;
        var rows = UiData.Tags(_analysis, _growingOnly.Checked);
        Update(_tags, rows.Select(r => Item(r.Tag, r.Tag.ToString(), r.Owner, r.Description, Fmt.Bytes(r.Bytes), Fmt.SignedBytes(r.D1), Fmt.SignedBytes(r.D10),
            Fmt.SignedBytes(r.D60), r.State == TagState.Normal ? "" : r.State.ToString(), r.Rates.AllocsPerMin.ToString("N0"), r.Rates.FreesPerMin.ToString("N0"),
            r.Rates.Shape == AllocShape.Idle ? "" : r.Rates.Text)).ToList(), _selectedTag);
        if (_selectedTag is { } sel && !rows.Any(r => r.Tag == sel)) _selectedTag = null; // it left the list
        RefreshSuspects();
    }

    private long _overviewSamples = -1;

    private void RefreshOverview()
    {
        if (_tools?.Items["removeLegacy"] is { } rl)
            rl.Visible = _analysis.Diagnostics.Cache.HelperStatus?.LegacyTaskPresent == true;
        if (_tools?.Items["removeOldTasks"] is { } ro)
            ro.Visible = _analysis.Diagnostics.Cache.HelperStatus?.OldTasksPresent == true;
        if (_engine.SampleCount == _overviewSamples) return; // nothing new since the last refresh
        _overviewSamples = _engine.SampleCount;
        var o = TagViewBuilder.Overview(_analysis);
        SetText(_overview, o.Text, "overview");
        _bootChart.SetData(o.SinceBoot, UiData.IncidentMarkers(_analysis));
        var lag = _analysis.Diagnostics.Lag;
        _lagChart.AlertRatio = _engine.Settings.LagAlertRatio;
        lock (lag) _lagChart.SetData(lag.Recent.ToList(), lag.Baseline);
    }

    private TagViewData? _renderedView;
    private bool _renderedShowAll;

    private void RefreshSuspects()
    {
        if (_selectedTag is not { } tag)
        {
            _view = null;
            _renderedView = null;
            _suspectsBox.Text = "Suspects: select a tag above";
            SetText(_answer, "", null);
            SetText(_details, "", null);
            _strip.SetData(null);
            Update(_owners, [], null);
            Update(_eventsList, [], null);
            return;
        }
        // The ranking runs the correlator over the last hour: every 30 s for the same tag is enough (a new selection,
        // or the events toggle, clears _view and rebuilds at once).
        if (_view is null || _view.Tag != tag || DateTime.UtcNow - _viewAt >= TimeSpan.FromSeconds(29))
        {
            _view = TagViewBuilder.Build(_analysis, _engine.Registry, tag, _allEvents.Checked, _allEvents.Checked ? _engine.RecentRawEvents() : []);
            _viewAt = DateTime.UtcNow;
        }
        var v = _view;
        if (v is null) return;
        // The same view, shown the same way: nothing to redo (building the owner and up to 300 event rows was the tag
        // page's main cost on every 10 s refresh).
        if (ReferenceEquals(v, _renderedView) && _showAllOwners == _renderedShowAll) return;
        _renderedView = v;
        _renderedShowAll = _showAllOwners;
        string box = "Suspects for " + v.Title + (v.TraceBacked ? " (the confirmation trace's owners first, then correlation only)" : " (correlation, not proof)");
        if (_suspectsBox.Text != box) _suspectsBox.Text = box;
        SetText(_answer, v.Answer.Line, v.Tag);
        _strip.SetData(v);
        var meaningful = v.Owners.Where(o => o.Meaningful).ToList();
        var shown = _showAllOwners ? v.Owners.ToList() : meaningful.Take(3).ToList();
        int hidden = v.Owners.Count - shown.Count;
        _showMore.Text = _showAllOwners ? "Show fewer" : hidden > 0 ? $"Show more ({hidden} more, including {v.Owners.Count - meaningful.Count} with no meaningful activity)" : "";
        if (Update(_owners, shown.Select(o => Item(o.Owner.Key, o.Summary, OwnerRanking.Strength(o.Confidence) + (o.CorrelationOnly ? " (correlation only)" : ""), o.Plain.FirstOrDefault() ?? "")).ToList(), null))
            FitColumns(_owners); // measuring text: only when the rows changed
        Update(_eventsList, v.Events.TakeLast(300).Select(e => { var it = Item(e.Key, e.Text); if (!e.Notable) it.ForeColor = Color.DimGray; return it; }).ToList(), null);
        ShowDetails();
    }

    /// <summary>Key columns sized to their content within the list's width (the last one takes the rest): no horizontal scrolling.</summary>
    private static void FitColumns(ListView lv)
    {
        if (lv.Columns.Count == 0 || lv.Items.Count == 0) return;
        int avail = lv.ClientSize.Width - 4;
        int used = 0;
        for (int c = 0; c < lv.Columns.Count - 1; c++)
        {
            lv.AutoResizeColumn(c, ColumnHeaderAutoResizeStyle.ColumnContent);
            lv.Columns[c].Width = Math.Min(lv.Columns[c].Width, Math.Max(80, avail / 2 - used / 2));
            used += lv.Columns[c].Width;
        }
        lv.Columns[^1].Width = Math.Max(120, avail - used);
    }

    /// <summary>The selected owner (else the top one) in full: plain evidence, members with identity, chain and command line, raw numbers.</summary>
    private void ShowDetails()
    {
        var v = _view;
        object? key = _owners.SelectedItems.Count > 0 ? _owners.SelectedItems[0].Tag : null;
        var o = v?.Owners.FirstOrDefault(x => Equals(x.Owner.Key, key)) ?? v?.Owners.FirstOrDefault(x => x.Meaningful);
        string text = o is null ? "" : OwnerDetails(o, v!);
        SetText(_details, text, (v?.Tag, o?.Owner.Key)); // the item is the tag AND the owner: either changing replaces the text
    }

    internal static string OwnerDetails(OwnerSuspect o, TagViewData v)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{o.Name}: {OwnerRanking.Strength(o.Confidence)}{(o.CorrelationOnly ? " (correlation only: the confirmation trace did not see it allocate this tag)" : "")} ({o.Owner.Kind})");
        foreach (var p in o.Plain) sb.AppendLine("• " + p);
        sb.AppendLine();
        // 2a / 2b: what the trace saw this owner's processes do, each with its identity, chain and command line.
        if (o.Trace is { } t)
        {
            sb.AppendLine($"Traced processes ({t.Processes.Count}, net {Fmt.Bytes(t.Net)}, {t.Allocs:N0} allocations, {t.Frees:N0} frees):");
            foreach (var p in t.Processes.OrderByDescending(p => p.Usage.NetBytes).Take(20))
            {
                sb.AppendLine($"{p.Name}  (PID {p.Usage.Pid}{(p.StartUtc is { } st ? $", started {Fmt.Clock(st)}" : "")}{(p.ExitUtc is { } ex ? $", exited {Fmt.Clock(ex)}" : "")}): {Fmt.SignedBytes(p.Usage.NetBytes)} net");
                if (p.Chain.Count > 1) sb.AppendLine("   chain: " + string.Join(" › ", p.Chain));
                if (p.CommandLine is { } tcl) sb.AppendLine("   command line: " + tcl);
            }
            if (t.Processes.Count > 20) sb.AppendLine($"… and {t.Processes.Count - 20} more traced processes");
            if (o.Members.Count > 0) sb.AppendLine().AppendLine("Seen by correlation:");
        }
        for (int i = 0; i < o.Members.Count && i < 20; i++)
        {
            var m = o.Members[i];
            var id = o.Identities[i];
            sb.AppendLine($"{id.Display}  (PID {m.Key.Pid}, started {Fmt.Clock(m.StartUtc)}{(m.ExitUtc is { } x ? $", exited {Fmt.Clock(x)}" : "")}{(m.IsOrphan ? ", kept running after its parent exited" : "")})");
            if (m.Ancestry.Count > 1) sb.AppendLine("   chain: " + string.Join(" › ", m.Ancestry));
            if (m.CommandLine is { } cl) sb.AppendLine("   command line: " + cl);
            if (m.ImagePath is { } path) sb.AppendLine("   path: " + path);
            foreach (var e in m.Evidence) sb.AppendLine("   raw: " + e);
        }
        if (o.Members.Count > 20) sb.AppendLine($"… and {o.Members.Count - 20} more processes");
        return sb.ToString().TrimEnd(); // AppendLine is \r\n on Windows: a TextBox needs that
    }

    private void RefreshIncidents()
    {
        var rows = UiData.Incidents(_analysis, _engine.Paths.IncidentsDir);
        object? sel = _incidents.SelectedItems.Count > 0 ? _incidents.SelectedItems[0].Tag : null;
        Update(_incidents, rows.Select(r => Item(r.Dir, Fmt.Stamp(r.OpenedUtc), r.Tag, r.Kind.ToString(), Fmt.SignedBytes(r.Growth), r.Top, r.Confidence,
            r.Open ? "open" : "closed", r.Dir)).ToList(), sel);
    }

    private void RefreshFindings()
    {
        var d = _analysis.Diagnostics;
        object? sel = _findings.SelectedItems.Count > 0 ? _findings.SelectedItems[0].Tag : null;
        Update(_findings, d.Findings.Snapshot().AsEnumerable().Reverse().Take(500).Select(f =>
        {
            var it = Item(f, Fmt.Stamp(f.TimeUtc), f.Kind.ToString(), f.Severity.ToString(), f.Title, f.ReportDir ?? "");
            if (f.Severity == FindingSeverity.Alert) it.BackColor = Color.MistyRose;
            else if (f.Severity == FindingSeverity.Warning) it.BackColor = Color.LemonChiffon;
            return it;
        }).ToList(), sel);
        List<(string Parent, int ParentPid, string Image, int Starts, int ShortLived)> rates;
        lock (d.Spawns) rates = d.Spawns.Rates(DateTime.UtcNow, 20);
        string tokens;
        lock (d.Tokens) tokens = "Toke vs process starts: " + (d.Tokens.Latest?.Summary ?? "collecting the first buckets");
        if (_tokenSummary.Text != tokens) _tokenSummary.Text = tokens;
        Update(_spawnRates, rates.Select(r => Item($"{r.ParentPid}/{r.Image}", r.Parent, r.ParentPid.ToString(), r.Image, r.Starts.ToString(), r.ShortLived.ToString())).ToList(), null);
    }

    private void RefreshProcesses()
    {
        var pair = _engine.LatestPair;
        var rows = UiData.Processes(pair?.Current, pair?.Previous, _engine.Registry);
        Func<ProcRow, IComparable> key = _procSortColumn switch
        {
            0 => r => r.Name, 1 => r => r.Key.Pid, 2 => r => r.ParentPid, 4 => r => r.Handles, 5 => r => r.Quota, 6 => r => r.Private, 7 => r => r.Orphan,
            _ => r => r.OpsPerSec,
        };
        var sorted = _procSortDesc ? rows.OrderByDescending(key) : rows.OrderBy(key);
        object? sel = _processes.SelectedItems.Count > 0 ? _processes.SelectedItems[0].Tag : null;
        var items = sorted.Take(400).Select(r =>
        {
            var it = Item(r.Key, r.Name, r.Key.Pid.ToString(), r.ParentPid.ToString(), r.OpsPerSec.ToString("N0"), r.Handles.ToString("N0"), Fmt.Bytes(r.Quota),
                Fmt.Bytes(r.Private), r.Orphan ? "orphan" : "", r.CommandLine ?? "");
            if (r.Orphan) it.BackColor = Color.MistyRose;
            return it;
        }).ToList();
        Update(_processes, items, sel);
    }

    private static ListViewItem Item(object tag, params string[] cols) => new(cols) { Tag = tag };

    /// <summary>The item (tag, owner) each read-only text box shows, so a refresh knows a new item from an update of the same one.</summary>
    private readonly Dictionary<TextBox, object?> _textItems = new();

    /// <summary>
    /// Updates a read-only text box without making it jump (C5). A different <paramref name="item"/> (another tag or owner)
    /// always replaces the text, even while text is selected: skipping then froze the answer and details on the old tag.
    /// For the same item the first visible line and the caret are kept, and a selection is kept when the selected text is
    /// unchanged; an update that would destroy the selection waits only while the box has focus (Core.Util.TextRefresh).
    /// </summary>
    private void SetText(TextBox tb, string text, object? item)
    {
        bool same = _textItems.TryGetValue(tb, out var shown) && Equals(shown, item);
        var action = TextRefresh.Plan(tb.Text, text, same, tb.SelectionStart, tb.SelectionLength, tb.ContainsFocus);
        if (action == TextRefreshAction.Hold) return;
        _textItems[tb] = item;
        if (action == TextRefreshAction.None) return;
        if (action == TextRefreshAction.Replace)
        {
            tb.Text = text; // a new item starts at the top with nothing selected
            tb.Select(0, 0);
            return;
        }
        int selStart = tb.SelectionStart, selLen = tb.SelectionLength;
        int firstLine = tb.IsHandleCreated ? (int)SendMessage(tb.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero) : 0;
        tb.Text = text;
        if (action == TextRefreshAction.UpdateKeepSelection) tb.Select(selStart, selLen);
        else tb.Select(Math.Min(selStart, text.Length), 0);
        // EM_SETSEL does not scroll; put the same line back at the top (setting the text scrolled to line 0).
        if (firstLine > 0 && tb.IsHandleCreated) SendMessage(tb.Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)firstLine);
    }

    private const int EM_GETFIRSTVISIBLELINE = 0x00CE, EM_LINESCROLL = 0x00B6;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// A list rebuilt from scratch (the services and the tasks lists) keeps its top visible row and its selection, matched by
    /// <paramref name="key"/> (C5 item 3).
    /// </summary>
    private static void KeepPlace(ListView lv, Func<ListViewItem, object?> key, Action rebuild)
    {
        int topIndex = 0;
        var fromTop = new List<object>();
        var selected = new List<object>();
        try
        {
            if (lv.IsHandleCreated && lv.Items.Count > 0 && lv.TopItem is { } top)
            {
                topIndex = top.Index;
                for (int i = top.Index; i < lv.Items.Count && fromTop.Count < 64; i++) if (key(lv.Items[i]) is { } k) fromTop.Add(k);
            }
            foreach (ListViewItem it in lv.SelectedItems) if (key(it) is { } k) selected.Add(k);
        }
        catch (Exception) { /* best effort */ }
        rebuild();
        try
        {
            var byKey = new Dictionary<object, ListViewItem>();
            foreach (ListViewItem it in lv.Items) if (key(it) is { } k) byKey.TryAdd(k, it);
            foreach (var k in selected) if (byKey.TryGetValue(k, out var it)) it.Selected = true;
            if (lv.IsHandleCreated && ScrollAnchor.Pick(topIndex, fromTop, byKey.ContainsKey) is { } a && byKey.TryGetValue(a, out var t)) lv.TopItem = t;
        }
        catch (Exception) { }
    }

    private string? _lastRefreshError;

    /// <summary>
    /// C5: a refresh runs from the timer and from selection events, outside <see cref="Guarded"/>; an exception there went
    /// to WinForms' unhandled-exception path. It is logged (once per distinct error) and the window keeps running.
    /// </summary>
    private void SafeRefresh(Action refresh)
    {
        try { refresh(); }
        catch (Exception ex)
        {
            string sig = ex.GetType().Name + ": " + ex.Message;
            if (sig == _lastRefreshError) return;
            _lastRefreshError = sig;
            try { _engine.Log.Info("window refresh failed (the window keeps running): " + ex); } catch { }
        }
    }

    /// <summary>Ctrl+C in a read-only text box goes through the copy redaction setting too.</summary>
    private void EnableTextCopy(TextBox tb) => tb.KeyDown += (_, e) =>
    {
        if (e.Control && e.KeyCode == Keys.C && tb.SelectionLength > 0) { Copy(tb.SelectedText); e.Handled = e.SuppressKeyPress = true; }
    };

    /// <summary>What the right-click menu can copy for a row (null entries are left out of the menu).</summary>
    private sealed record CopyInfo(string? Pid, string? CommandLine, string? Path, string? Evidence, Func<string>? Summary);

    private void EnableCopy(ListView lv, Func<object?, CopyInfo?> info)
    {
        lv.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.C) { Copy(RowsText(lv, selectedOnly: true)); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.A)
            {
                _repopulating = true; // one view rebuild after, not one per row
                try { foreach (ListViewItem it in lv.Items) it.Selected = true; }
                finally { _repopulating = false; }
                e.Handled = true;
            }
        };
        var menu = new ContextMenuStrip();
        menu.Opening += (_, e) =>
        {
            menu.Items.Clear();
            var key = lv.SelectedItems.Count > 0 ? lv.SelectedItems[0].Tag : null;
            if (key is null) { e.Cancel = true; return; }
            menu.Items.Add(lv.SelectedItems.Count > 1 ? $"Copy {lv.SelectedItems.Count} rows" : "Copy row", null, (_, _) => Copy(RowsText(lv, selectedOnly: true)));
            menu.Items.Add("Copy all rows", null, (_, _) => Copy(RowsText(lv, selectedOnly: false)));
            if (info(key) is { } i)
            {
                if (i.CommandLine is { } cl) menu.Items.Add("Copy command line", null, (_, _) => Copy(cl));
                if (i.Path is { } p) menu.Items.Add("Copy path", null, (_, _) => Copy(p));
                if (i.Pid is { } pid) menu.Items.Add("Copy PID", null, (_, _) => Copy(pid));
                if (i.Evidence is { } ev) menu.Items.Add("Copy evidence", null, (_, _) => Copy(ev));
                if (i.Summary is { } sum) menu.Items.Add("Copy summary", null, (_, _) => Guarded("Copy summary", () => Copy(sum())));
            }
        };
        lv.ContextMenuStrip = menu;
    }

    /// <summary>Tab-separated text with a header row: exactly the cells the list shows.</summary>
    internal static string RowsText(ListView lv, bool selectedOnly)
    {
        var headers = lv.Columns.Cast<ColumnHeader>().Select(c => c.Text).ToList();
        var items = selectedOnly ? lv.SelectedItems.Cast<ListViewItem>() : lv.Items.Cast<ListViewItem>();
        return CopyText.Tsv(headers, items.OrderBy(i => i.Index).Select(i => (IReadOnlyList<string>)Enumerable.Range(0, headers.Count)
            .Select(c => c < i.SubItems.Count ? i.SubItems[c].Text : "").ToList()));
    }

    private void Copy(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        text = CopyText.Apply(text, _engine.Settings.RedactCopies ? CurrentRedaction() : null);
        try { Clipboard.SetDataObject(text, copy: true, retryTimes: 5, retryDelay: 100); }
        catch (Exception ex) { _engine.Log.Info("clipboard: " + ex.Message); } // another app holds the clipboard
    }

    private CopyInfo? OwnerCopy(object? key)
    {
        if (_view?.Owners.FirstOrDefault(o => Equals(o.Owner.Key, key)) is not { } o) return null;
        var pids = string.Join(", ", o.Members.Select(m => m.Key.Pid));
        var cls = string.Join("\r\n", o.Members.Select(m => m.CommandLine).Where(c => c is not null).Distinct());
        var paths = string.Join("\r\n", o.Members.Select(m => m.ImagePath).Where(p => p is not null).Distinct());
        return new CopyInfo(pids, cls.Length > 0 ? cls : null, paths.Length > 0 ? paths : null, OwnerDetails(o, _view!), TagSummary);
    }

    /// <summary>The selected tag's answer, window and suspects with their evidence, as plain text.</summary>
    private string TagSummary()
    {
        if (_view is not { } v) return "";
        var sb = new System.Text.StringBuilder();
        sb.Append(v.Answer.Line).Append("\r\n");
        sb.Append($"{v.Title}\r\nWindow: {Fmt.Stamp(v.FromUtc)} to {Fmt.Stamp(v.ToUtc)}, growth from {Fmt.Clock(v.OnsetUtc)}\r\n\r\n");
        int n = 0;
        foreach (var o in v.Owners.Where(o => o.Meaningful).Take(5)) sb.Append($"{++n}. ").Append(OwnerDetails(o, v)).Append("\r\n\r\n");
        if (n == 0) sb.Append("No suspect stood out.\r\n");
        return sb.ToString().TrimEnd() + "\r\n";
    }

    private static string IncidentSummary(string dir) => CopyText.IncidentSummary(File.ReadAllText(Path.Combine(dir, "report.json")));

    /// <summary>Toolbar "Copy summary": the incident selected on the Incidents page, else the selected tag.</summary>
    private void CopySummary()
    {
        if (_tabs.SelectedTab?.Text == "Incidents" && SelectedIncidentDir() is { } dir && File.Exists(Path.Combine(dir, "report.json"))) { Copy(IncidentSummary(dir)); return; }
        if (_view is not null) { Copy(TagSummary()); return; }
        MessageBox.Show(this, "Select a tag (Growing tags) or an incident first.", "PoolWatch");
    }

    private readonly Dictionary<ListView, ListSync> _syncs = new();
    private readonly Dictionary<ListView, string> _listSignature = new();

    /// <summary>
    /// Updates a list in place (no clear-and-rebuild: that made the window flash). Rows are matched by their Tag; the
    /// selection and scroll position stay. <paramref name="select"/> is kept for the callers' intent; the selection is
    /// never moved by a refresh.
    /// </summary>
    /// <returns>False when the rows were unchanged (nothing was touched).</returns>
    private bool Update(ListView lv, List<ListViewItem> items, object? select)
    {
        // Unchanged rows: nothing to do (even an in-place sync walks every row; the UI thread's largest cost on a busy PC).
        var sig = new System.Text.StringBuilder(items.Count * 64);
        foreach (var it in items)
        {
            sig.Append(it.Tag).Append('\u001e');
            foreach (ListViewItem.ListViewSubItem sub in it.SubItems) sig.Append(sub.Text).Append('\u001f');
            sig.Append(it.BackColor.ToArgb()).Append(it.ForeColor.ToArgb()).Append('\n');
        }
        string signature = sig.ToString();
        if (!_syncs.TryGetValue(lv, out var sync)) _syncs[lv] = sync = new ListSync(lv);
        // Unchanged rows, and no held order waiting to be released: nothing to do.
        if (!sync.OrderHeld && !_forceOrder && _listSignature.TryGetValue(lv, out var last) && last == signature)
        {
            UpdateOrderHint();
            return false;
        }
        _listSignature[lv] = signature;
        _repopulating = true;
        try
        {
            sync.Update(items.Select(i => new ListSync.Row(i.Tag!, i.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text).ToArray(),
                i.BackColor == Color.Empty || i.BackColor == SystemColors.Window ? null : i.BackColor,
                Fore: i.ForeColor == Color.Empty || i.ForeColor == SystemColors.WindowText ? null : i.ForeColor)).ToList(), _forceOrder);
        }
        catch (Exception) { /* the handle may not exist yet (hidden tab) */ }
        finally { _repopulating = false; }
        UpdateOrderHint();
        return true;
    }

    /// <summary>The hint reflects the lists on the visible page only.</summary>
    private void UpdateOrderHint()
    {
        bool held = _syncs.Any(kv => kv.Value.OrderHeld && kv.Key.Visible);
        string hint = held ? "List order held while a row is selected or the mouse is over it" : "";
        if (_orderHint.Text != hint) _orderHint.Text = hint;
    }

    private ToolStrip? _tools;

    private void ClearCacheNow()
    {
        var c = _analysis.Diagnostics.Cache;
        // S5 (review round 2): the purge is in-process; PoolWatch runs as administrator, so there is nothing to set up.
        if (!Engine.CacheHelper.IsElevated)
        {
            MessageBox.Show(this, NotElevated, "PoolWatch");
            return;
        }
        MessageBox.Show(this, c.Request(Core.Cache.ClearMode.Standby, Core.Cache.ClearTrigger.Manual)
            ? "Clearing the standby cache. PoolWatch measures the pool again in 2 minutes; the result is on the Overview and in Findings."
            : "A clear is already running (or no sample yet).", "PoolWatch");
    }

    private bool _autoClearConfirmed;

    private void SaveSettings(string? message)
    {
        _editSettings.Save(_engine.Paths.Guard.CheckReal(_engine.Paths.SettingsFile));
        _settingsGrid.Refresh();
        if (message is not null) MessageBox.Show(this, message, "PoolWatch");
    }

    private const string NotElevated = "Clear cache needs PoolWatch to run as administrator (start PoolWatch normally: it asks for that).";

    /// <summary>
    /// Turning the automatic clear on: finds any clear task of your own (RAMMap in a scheduled task), shows it, and removes
    /// it only on a yes; kept, the automatic clear stays off. In-process (S5, review round 2: no UAC step any more;
    /// PoolWatch is elevated).
    /// </summary>
    private void ConfirmAutoClear()
    {
        if (!Engine.CacheHelper.IsElevated) { MessageBox.Show(this, NotElevated, "PoolWatch"); return; }
        if (LegacyInProcess() == 0)
        {
            _autoClearConfirmed = true;
            _editSettings.AutoClearEnabled = true;
            SaveSettings("The automatic clear is on (from the next start of PoolWatch).");
        }
        else SaveSettings("Your own clear task stays, so PoolWatch's automatic clear stays off (the two should not both run).");
        _analysis.Diagnostics.Cache.RefreshStatus();
    }

    private void RemoveLegacyTask()
    {
        if (!Engine.CacheHelper.IsElevated) { MessageBox.Show(this, NotElevated, "PoolWatch"); return; }
        int code = LegacyInProcess();
        _analysis.Diagnostics.Cache.RefreshStatus();
        if (code == 0) MessageBox.Show(this, "Removed (RAMMap itself is left in place).", "PoolWatch");
    }

    /// <summary>The same step as <c>--remove-legacy-clear-task</c>. 0 none/removed, 4 kept.</summary>
    private int LegacyInProcess()
    {
        var (outcome, _) = Engine.CacheHelper.ResolveLegacy(t =>
            MessageBox.Show(this, Engine.CacheHelper.LegacyQuestion(t), "PoolWatch: your own clear task", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes);
        return outcome == Engine.CacheHelper.LegacyOutcome.Kept ? 4 : 0;
    }

    /// <summary>
    /// S5 (review round 2): removes the two \PoolWatch\ SYSTEM tasks and the helper files an older PoolWatch's Clear cache
    /// setup installed (the button shows only while they are there), after a yes. Recorded on the timeline.
    /// </summary>
    private void RemoveOldClearTasks()
    {
        if (!Engine.CacheHelper.IsElevated) { MessageBox.Show(this, NotElevated, "PoolWatch"); return; }
        var old = _analysis.Diagnostics.Cache.HelperStatus?.OldTasks ?? [];
        string list = old.Count > 0 ? string.Join("\n", old.Select(n => $@"{Engine.CacheHelper.Folder}\{n}")) : "(none found just now)";
        if (MessageBox.Show(this, "An older PoolWatch installed these SYSTEM tasks for Clear cache. PoolWatch clears the cache itself now and no longer " +
                $"uses them:\n\n{list}\n\nRemove them, and the helper files in {Engine.CacheHelper.OldHelperDir}? (Your own clear task, if any, is not touched.)",
                "PoolWatch: old clear-cache tasks", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var removed = Engine.CacheHelper.RemoveOldTasks();
        _analysis.Diagnostics.Cache.RefreshStatus();
        if (removed.Count > 0) _analysis.Diagnostics.RecordOwnerChange("Removed the old Clear cache tasks and helper: " + string.Join("; ", removed), null);
        MessageBox.Show(this, removed.Count == 0 ? "Nothing was found to remove." : "Removed:\n" + string.Join("\n", removed), "PoolWatch");
    }

    private void CaptureNow()
    {
        var inc = _analysis.CaptureNow();
        var scope = Core.Incidents.CaptureGovernor.ScopeFor(Core.Incidents.CaptureSource.Manual, _engine.Settings);
        MessageBox.Show(this, inc is null ? "A capture is already running. Try again when it has finished." :
            $"Capturing {scope.Describe()} for {_engine.Settings.CaptureSeconds} s" + (scope.Cpu ? $" (then {_engine.Settings.CpuTraceSeconds} s of CPU)" : "") +
            $" into\n{inc.Dir}\nThe report updates when the trace has been parsed.", "PoolWatch");
    }

    private void SinceBoot()
    {
        // Builds the 25-hour report under the history lock: keep it off the UI thread.
        string dir = _engine.Paths.EnsureDir(Path.Combine(_engine.Paths.DataRoot, "reports"));
        Task.Run(() => _analysis.WriteSinceBoot(dir)).ContinueWith(t =>
        {
            if (!IsHandleCreated || IsDisposed) return;
            BeginInvoke(() =>
            {
                if (t.IsFaulted) MessageBox.Show(this, "Since-boot report failed: " + t.Exception!.GetBaseException().Message, "PoolWatch");
                else ShowFile(t.Result); // t.Result is the report file; show it in its folder, never open it through an association
            });
        });
    }

    private void Guarded(string what, Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            _engine.Log.Info($"{what} failed: {ex}");
            MessageBox.Show(this, $"{what} failed: {ex.Message}", "PoolWatch");
        }
    }

    private string? SelectedIncidentDir() =>
        _incidents.SelectedItems.Count > 0 ? _incidents.SelectedItems[0].Tag as string : UiData.Incidents(_analysis, _engine.Paths.IncidentsDir).FirstOrDefault()?.Dir;

    private void OpenIncidentFolder()
    {
        var dir = SelectedIncidentDir();
        OpenFolder(dir is not null && Directory.Exists(dir) ? dir : _engine.Paths.EnsureDir(_engine.Paths.IncidentsDir));
    }

    private void Export()
    {
        var dir = SelectedIncidentDir();
        if (dir is null || !Directory.Exists(dir)) { MessageBox.Show(this, "No incident to export yet.", "PoolWatch"); return; }
        if (_analysis.Captures.Busy) { MessageBox.Show(this, "A confirmation trace is being recorded. Export when it has finished.", "PoolWatch"); return; }
        // M1: redaction is the default (Yes, or Enter); the incident folder itself is never redacted.
        var answer = MessageBox.Show(this, "Redact the user name, home and TEMP paths, machine name, SID and values that look like secrets " +
            "(passwords, tokens, API keys on a command line)? Recommended before you share it.\n(The .etl trace is left out of a redacted export. " +
            "Redaction is best-effort: read the export before you post it.)",
            "Export report", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
        if (answer == DialogResult.Cancel) return;
        var zip = Exporter.Export(dir, _engine.Paths.ExportsDir, answer == DialogResult.Yes ? CurrentRedaction() : null, _engine.Paths.Guard.CheckReal);
        OpenFolder(Path.GetDirectoryName(zip)!);
    }

    private void ExportBundle()
    {
        // Redaction is the default: Yes (or Enter) redacts; No has to be chosen.
        var answer = MessageBox.Show(this, "Redact the user name, home paths, machine name, SID and values that look like secrets? (Recommended before sending it to anyone.)",
            "Export vendor bundle", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
        if (answer == DialogResult.Cancel) return;
        var zip = BundleBuilder.Export(_engine, _analysis, answer == DialogResult.Yes ? CurrentRedaction() : null);
        OpenFolder(Path.GetDirectoryName(zip)!);
    }

    /// <summary>
    /// The home folder in its long and 8.3 forms (TEMP is often C:\Users\JOHNDO~1\AppData\Local\Temp) and the TEMP
    /// folders, so a short path in a command line cannot carry part of the user name out of a redacted export. C6: TEMP
    /// is redacted as &lt;TEMP&gt; (it was listed as a home path, so ...\Temp\x became &lt;HOME&gt;\x).
    /// </summary>
    internal static RedactionInfo CurrentRedaction()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var homes = new List<string> { home };
        if (ShortPath(home) is { } sh && !sh.Equals(home, StringComparison.OrdinalIgnoreCase)) homes.Add(sh);
        var temps = new List<string>();
        foreach (var v in new[] { "TEMP", "TMP" })
            if (Environment.GetEnvironmentVariable(v) is { Length: > 3 } t)
            {
                temps.Add(t);
                if (ShortPath(t) is { } st) temps.Add(st);
            }
        return new(Environment.UserName, Environment.MachineName, homes.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), Environment.UserDomainName,
            System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value, temps.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static string? ShortPath(string path)
    {
        var sb = new System.Text.StringBuilder(520);
        try { return GetShortPathName(path, sb, sb.Capacity) is > 0 and < 520 ? sb.ToString() : null; } catch { return null; }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint GetShortPathName(string longPath, System.Text.StringBuilder shortPath, int size);

    private void About()
    {
        var o = _engine.Meter.Report();
        MessageBox.Show(this,
            $"PoolWatch {typeof(MainForm).Assembly.GetName().Version}\n\nOverhead (budget in brackets){(o.SteadyStateSeconds >= SteadySeconds ? $", steady state over {Fmt.Duration(TimeSpan.FromSeconds(o.SteadyStateSeconds))}" : " - still warming up, numbers include start-up")}:\n" +
            $"CPU {o.CpuPercentOfOneCore:0.00}% of one core (< {SelfMeter.CpuBudgetPercent.ToString(System.Globalization.CultureInfo.InvariantCulture)}%)\n" +
            $"Peak working set {Fmt.Bytes(o.PeakWorkingSetBytes)} (< {SelfMeter.RamBudgetBytes >> 20} MB)\n" +
            $"Disk writes {Fmt.Bytes(o.DiskWriteBytesPerHour)}/h (< {SelfMeter.DiskBudgetBytesPerHour >> 20} MB/h)\n\nData: {_engine.Paths.DataRoot}\n\n" +
            "PoolWatch watches and reads. It changes the system where you choose to on its pages (Clear cache, background " +
            "services, scheduled tasks, startup apps, End process, start at logon) and, when you turn it on, in the token-leak " +
            "guard's live focus setting at logon; and, on its own, it secures its data folder and removes an older PoolWatch's " +
            "leftover clear-cache tasks. SECURITY.md in the source repository (github.com/nubbymong/poolwatch) lists every such " +
            "action. No network calls, no telemetry.", "About PoolWatch");
    }

    /// <summary>The real explorer.exe, by full path, never a bare name resolved through App Paths or the current directory.</summary>
    private static readonly string ExplorerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// <summary>
    /// Opens a folder in Explorer. S1/S3: the elevated app never shell-executes a path (that would consult the user's
    /// shell associations and App Paths, both user-writable): it starts explorer.exe by full path with the folder as an
    /// argument and UseShellExecute = false.
    /// </summary>
    private static void OpenFolder(string folder)
    {
        try { Process.Start(new ProcessStartInfo(ExplorerPath, $"\"{Path.GetFullPath(folder)}\"") { UseShellExecute = false }); } catch { }
    }

    /// <summary>Shows a file in Explorer with it selected: the folder opens, never the file through its association.</summary>
    private static void ShowFile(string file)
    {
        try { Process.Start(new ProcessStartInfo(ExplorerPath, $"/select,\"{Path.GetFullPath(file)}\"") { UseShellExecute = false }); } catch { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing the window hides it; the tray keeps monitoring. Exit is in the tray menu.
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        base.OnFormClosing(e);
    }

    /// <summary>CI soak: every minute show the next page (tags with the top tag selected, incidents, processes, findings).</summary>
    public void StartSoakCycle()
    {
        var t = new System.Windows.Forms.Timer { Interval = 60_000 };
        int page = 0;
        t.Tick += (_, _) =>
        {
            int[] pages = [0, 1, 2, 3, 4, 5]; // overview, tags, incidents, processes, findings, background services
            _tabs.SelectedIndex = pages[page++ % pages.Length];
            if (_tabs.SelectedIndex == 1 && _tags.Items.Count > 0 && _tags.SelectedItems.Count == 0) _tags.Items[0].Selected = true;
            RefreshNow();
        };
        t.Start();
    }

    /// <summary>CI: what the window actually shows (list rows), and whether hiding it stops the refresh timer.</summary>
    public object SmokeStats()
    {
        var counts = new Dictionary<string, int>();
        long ioRows = 0;
        for (int i = 1; i < _tabs.TabCount - 1; i++)
        {
            _tabs.SelectedIndex = i;
            RefreshNow();
            // Every tab between Overview and Settings, each with its own list (the Background services list loads in the
            // background, so it may still be empty here).
            counts[_tabs.TabPages[i].Text] = i switch { 1 => _tags.Items.Count, 2 => _incidents.Items.Count, 3 => _processes.Items.Count, 4 => _findings.Items.Count, 5 => _services.Items.Count, _ => 0 };
            if (i == 3) foreach (ListViewItem it in _processes.Items) if (it.SubItems[3].Text != "0") ioRows++;
        }
        // No flashing: one refresh keeps the row objects and the selection; Ctrl+C copies exactly what the row shows.
        _tabs.SelectedIndex = 3;
        RefreshNow();
        var before = _processes.Items.Cast<ListViewItem>().ToDictionary(i => i.Tag!, i => i);
        if (_processes.Items.Count > 1) _processes.Items[1].Selected = true;
        var selectedKey = _processes.SelectedItems.Count > 0 ? _processes.SelectedItems[0].Tag : null;
        RefreshNow();
        int kept = _processes.Items.Cast<ListViewItem>().Count(i => before.TryGetValue(i.Tag!, out var old) && ReferenceEquals(old, i));
        double identityKept = _processes.Items.Count == 0 ? 0 : kept / (double)_processes.Items.Count;
        bool selectionKept = selectedKey is not null && _processes.SelectedItems.Count == 1 && Equals(_processes.SelectedItems[0].Tag, selectedKey);
        string copied = RowsText(_processes, selectedOnly: true);
        var lines = copied.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        bool copyMatches = lines.Length == 2 && lines[0] == string.Join('\t', _processes.Columns.Cast<ColumnHeader>().Select(c => c.Text)) &&
                           _processes.SelectedItems.Count == 1 && lines[1] == string.Join('\t', _processes.SelectedItems[0].SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => CopyText.Cell(s.Text)));
        _processes.SelectedItems.Clear();
        _tabs.SelectedIndex = 1;
        if (_tags.Items.Count > 0) { _tags.Items[0].Selected = true; RefreshNow(); }
        int ownerRows = _owners.Items.Count;
        string answer = _answer.Text;
        _tabs.SelectedIndex = 0;
        RefreshNow();
        string overview = _overview.Text;
        bool timerWhileVisible = _timer.Enabled;
        Hide();
        bool timerWhileHidden = _timer.Enabled;
        return new { rows = counts, processRowsWithIo = ioRows, suspectRows = ownerRows, answer, overview, timerWhileVisible, timerWhileHidden,
            identityKept, selectionKept, copyMatches, copiedSample = copied };
    }

    /// <summary>CI/README screenshots: renders each tab to a PNG.</summary>
    public List<string> Screenshots(string dir)
    {
        var files = new List<string>();
        for (int i = 0; i < _tabs.TabCount; i++)
        {
            _tabs.SelectedIndex = i;
            if (i == 1 && _tags.Items.Count > 0) _tags.Items[0].Selected = true;
            RefreshNow();
            Application.DoEvents();
            using var bmp = new Bitmap(Width, Height);
            DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
            string name = Path.Combine(dir, $"{i + 1}-{_tabs.TabPages[i].Text.ToLowerInvariant().Replace(' ', '-')}.png");
            bmp.Save(_engine.Paths.Guard.CheckReal(name), System.Drawing.Imaging.ImageFormat.Png);
            files.Add(name);
        }
        _tabs.SelectedIndex = 0;
        return files;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
