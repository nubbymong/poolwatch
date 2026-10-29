using PoolWatch.Analysis;
using PoolWatch.Core.Detection;
using PoolWatch.Engine;
using PoolWatch.Engine.Changes;
using PoolWatch.Native;

namespace PoolWatch.Ui;

/// <summary>
/// The tray app: an icon coloured by state (grey while starting, then green / amber / red) with the paged + non-paged
/// totals in its tooltip, a balloon when an incident opens, and the main window on demand. Its menu opens the setup guide
/// and Active changes, says when a newer release is out (update notices, information only), and holds the tools that
/// are not on the main window (DESIGN.md section 11): Capture now, Since boot report, Open incident folder, Export vendor
/// bundle, Copy latest incident summary, the clear-task removals (shown only while there is something to remove), Report
/// an issue and About. It is the shell the setup guide and the suggestions call (<see cref="IShell"/>): one of each window
/// at a time, and the token-leak guard's sign-in check with its notices. The installer's exit signal ends it cleanly.
/// </summary>
internal sealed class TrayApp : ApplicationContext, IShell
{
    private readonly MonitorEngine _engine;
    private readonly AnalysisPipeline _analysis;
    private readonly UiActions _actions;
    private readonly ViewState? _view;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 10_000 };
    private readonly SynchronizationContext _ui;
    private MainForm? _form;
    private TrayState _state = (TrayState)(-1);
    private readonly Action<Core.Incidents.Incident, Core.Detection.Alert> _onOpened;
    private readonly Microsoft.Win32.SessionEndingEventHandler _onSessionEnding;
    private readonly Action<Core.Experiments.ExperimentStep> _onExperiment;
    private readonly HotkeyWindow _hotkey;
    private readonly ToolStripMenuItem _experimentItem;
    private readonly IDisposable? _exitSignal;
    private readonly LiveSettings _live;
    /// <summary>Update notices (none in the CI UI test): started once the setup guide has been completed.</summary>
    private readonly UpdateCheck? _updates;
    /// <summary>The CI UI test's sample release (<see cref="ShowSampleUpdate"/>); null otherwise.</summary>
    private UpdateRelease? _sampleUpdate;
    private bool _markOpen;
    private GuardNotices? _guardNotices;
    private SetupGuideForm? _guide;
    private ActiveChangesForm? _activeChanges;
    /// <summary>The balloon showing now offers the setup guide (a click on it opens the guide).</summary>
    private bool _balloonOpensGuide;

    /// <summary>The state the tray icon currently shows.</summary>
    public TrayState IconState => _state;

    /// <param name="showWindow">Open the main window now (not when started at sign-in with --tray).</param>
    /// <param name="smoke">The CI UI test: the window opens in the Simple view and keeps no view choice, the guide does not open by itself.</param>
    public TrayApp(MonitorEngine engine, AnalysisPipeline analysis, LiveSettings live, ChangeLedger ledger, bool showWindow, bool smoke)
    {
        _engine = engine;
        _analysis = analysis;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _actions = new UiActions(engine, analysis, _ui);
        _view = smoke ? null : new ViewState(engine.Paths, engine.Log);
        _live = live;
        Setup = new SetupContext(engine, analysis, _actions, live, ledger, this, smoke, new UpdateSwitch(live));
        if (!smoke)
        {
            // The check reads the setting at each wake, so turning notices off or on needs no call here.
            _updates = new UpdateCheck(engine.Paths, engine.Log, () => live.Running.CheckForUpdates);
            _updates.Found += _ => _ui.Post(_ => _form?.ShowUpdate(UpdateToShow), null);
        }
        var menu = _menu;
        menu.Items.Add("Open PoolWatch", null, (_, _) => ShowWindow());
        menu.Items.Add("Set up PoolWatch...", null, (_, _) => OpenGuide());
        menu.Items.Add("Active changes...", null, (_, _) => _actions.Guarded(null, "Active changes", ShowActiveChanges));
        // "PoolWatch 1.2.0 is available": shown only while a newer release is out; it opens that release's page.
        var update = new ToolStripMenuItem("", null, (_, _) => _actions.Guarded(null, "Open the release page", () =>
        {
            if (UpdateToShow is { } r) UiActions.OpenWebPage(r.PageUrl);
        })) { Visible = false };
        menu.Items.Add(update);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Capture now", null, (_, _) => _actions.Guarded(null, "Capture now", () =>
        {
            var i = _analysis.CaptureNow();
            string what = Core.Incidents.CaptureGovernor.ScopeFor(Core.Incidents.CaptureSource.Manual, _engine.Settings).Describe();
            Balloon(i is null ? "A capture is already running." : $"Capture started ({what}): {i.Id}");
        }));
        menu.Items.Add("Since boot report", null, (_, _) => _actions.Guarded(null, "Since boot", () => _actions.SinceBoot(null)));
        menu.Items.Add("Open incident folder", null, (_, _) => _actions.OpenIncidentFolder());
        menu.Items.Add("Export vendor bundle...", null, (_, _) => _actions.Guarded(null, "Export vendor bundle", () => _actions.ExportVendorBundle(null)));
        menu.Items.Add("Copy latest incident summary", null, (_, _) => _actions.Guarded(null, "Copy summary", () => _actions.CopyLatestIncidentSummary(null)));
        menu.Items.Add(new ToolStripSeparator());
        _hotkey = new HotkeyWindow(engine.Settings.MarkHotkey, Mark);
        menu.Items.Add(new ToolStripMenuItem("Mark...", null, (_, _) => Mark()) { ShortcutKeyDisplayString = _hotkey.Registered ? _hotkey.Text : null });
        _experimentItem = new ToolStripMenuItem("Experiment...", null, (_, _) => ToggleExperiment());
        menu.Items.Add(_experimentItem);
        var clearLag = new ToolStripMenuItem("Experiment: lag before / after a clear", null, (_, _) => StartClearExperiment());
        menu.Items.Add(clearLag);
        menu.Items.Add(new ToolStripSeparator());
        // Shown only while there is something to remove (they were toolbar buttons that appeared the same way).
        var removeLegacy = new ToolStripMenuItem("Remove my clear task...", null, (_, _) => _actions.Guarded(null, "Remove task", () => _actions.RemoveLegacyTask(null)))
            { ToolTipText = "Shows your own clear task (a scheduled task that runs RAMMap with an -E option) and removes it after you say yes; RAMMap itself stays in place" };
        var removeOld = new ToolStripMenuItem("Remove old clear-cache tasks...", null, (_, _) => _actions.Guarded(null, "Remove old clear-cache tasks", () => _actions.RemoveOldClearTasks(null)))
            { ToolTipText = @"An older PoolWatch installed two SYSTEM tasks (\PoolWatch\Clear cache ...) for Clear cache; they are no longer used. Removes them after you say yes." };
        menu.Items.Add(removeLegacy);
        menu.Items.Add(removeOld);
        menu.Items.Add("Report an issue...", null, (_, _) => _actions.Guarded(null, "Report an issue", () => _actions.ReportIssue(null)));
        menu.Items.Add("About PoolWatch", null, (_, _) => _actions.About(null));
        menu.Opening += (_, _) =>
        {
            bool active = _analysis.Diagnostics.AnyExperimentActive;
            _experimentItem.Text = active ? "Stop experiment" : "Experiment...";
            clearLag.Enabled = !active;
            removeLegacy.Visible = _actions.LegacyTaskPresent;
            removeOld.Visible = _actions.OldTasksPresent;
            var newer = UpdateToShow;
            update.Visible = newer is not null;
            update.Text = newer?.Text ?? "";
        };
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        _icon = new NotifyIcon { ContextMenuStrip = menu, Visible = true, Text = "PoolWatch: starting", Icon = TrayIcons.Starting };
        _icon.DoubleClick += (_, _) => ShowWindow();
        _icon.BalloonTipClicked += (_, _) => { if (_balloonOpensGuide) OpenGuide(); };
        _icon.BalloonTipClosed += (_, _) => _balloonOpensGuide = false;
        // Raised on the sampler's thread under the history lock: the notice is worded here, from the incident as it is now,
        // and only the text goes to the UI thread.
        _onOpened = (inc, _) =>
        {
            if (PoolCauses.TrayNotice(inc, _analysis.Knowledge.Lookup, _engine.Services, _engine.Log.Info) is { } text) _ui.Post(_ => Balloon(text), null);
        };
        _analysis.Incidents.Opened += _onOpened;
        _onExperiment = step => _ui.Post(_ => Balloon(step.Prompt, step.Phase == Core.Experiments.ExperimentPhase.Done ? 20_000 : 10_000), null);
        _analysis.Diagnostics.ExperimentStep += _onExperiment;
        if (Core.Experiments.Hotkey.Parse(engine.Settings.MarkHotkey) is not null && !_hotkey.Registered)
            engine.Log.Info($"mark hotkey {engine.Settings.MarkHotkey} is held by another app; use the tray Mark item");
        // Shutdown / logoff, and the installer's exit signal: exit cleanly so open incidents are closed and written.
        _onSessionEnding = (_, _) => ExitThread();
        Microsoft.Win32.SystemEvents.SessionEnding += _onSessionEnding;
        _exitSignal = ExitSignal.Listen(() => _ui.Post(_ => ExitThread(), null), engine.Log.Info);
        // Tray refresh: one cheap state read every 10 s (no painting, no animation).
        _timer.Tick += (_, _) => UpdateIcon();
        _timer.Start();
        UpdateIcon();
        if (showWindow) ShowWindow();
        // The first start (the guide was never completed): the guide over the main window, or, started at sign-in, a
        // balloon. The update check starts once the guide has been completed, so the owner has seen its switch first.
        if (Setup.Store.GuideCompleted) _updates?.Start();
        else if (!smoke)
        {
            if (showWindow) OpenGuide();
            else Balloon("PoolWatch is running. Click here to set it up.", 10_000, opensGuide: true);
        }
    }

    /// <summary>What the setup guide and the suggestions use (made once).</summary>
    public SetupContext Setup { get; }

    public MainForm Form => _form ??= new MainForm(Setup, _view, () => UpdateToShow);

    /// <summary>The newer release to tell the owner about: read each time (a release taken back changes it), and none while notices are off.</summary>
    private UpdateRelease? UpdateToShow => _sampleUpdate ?? ToShow(_live.Running, _updates?.Available);

    /// <summary>A newer release is told about only while update notices are on in <paramref name="running"/>.</summary>
    internal static UpdateRelease? ToShow(Core.Settings running, UpdateRelease? available) => running.CheckForUpdates ? available : null;

    /// <summary>
    /// CI UI test: the window and the tray tell about <paramref name="release"/> in the check's place (the test never asks
    /// GitHub), so a refresh while the screenshot is drawn keeps the notice. The sample skips the update-notices setting
    /// (CI turns notices off), so it shows whatever the setting: <see cref="UpdateToShow"/> must return it first.
    /// </summary>
    internal void ShowSampleUpdate(UpdateRelease release)
    {
        _sampleUpdate = release;
        Form.ShowUpdate(UpdateToShow);
    }

    /// <summary>What the main window's Pool causes rankings hold, for PoolWatch's own cost report (any thread; none before the window was made).</summary>
    public (int rankings, int owners, int ownerMembers, int events) RankingsHeld() => _form?.Rankings.Held() ?? default;

    /// <summary>Every item of the tray menu, by text (CI: what the main window gave up must still be reachable).</summary>
    public List<string> MenuItemTexts() => _menu.Items.OfType<ToolStripMenuItem>().Select(i => i.Text ?? "").ToList();

    public void ShowWindow()
    {
        Form.Show();
        if (Form.WindowState == FormWindowState.Minimized) Form.WindowState = FormWindowState.Normal;
        Form.Activate();
    }

    /// <summary>The main window when it shows (a window opened from it sits on it), else null (the window gets a taskbar button).</summary>
    private Form? MainWindowIfShown => _form is { Visible: true } f ? f : null;

    /// <summary>Opens a window made for this opening (closing disposes it) over the main window when that shows.</summary>
    private void Open(Form form)
    {
        var owner = MainWindowIfShown;
        form.ShowInTaskbar = owner is null;
        if (owner is null) form.Show();
        else form.Show(owner);
    }

    /// <summary>Brings a window that is open already to the front, shown again if it was hidden with the main window.</summary>
    private static void Bring(Form form)
    {
        if (!form.Visible) form.Show();
        if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
        form.Activate();
    }

    /// <summary>The setup guide from the menu, the balloon or a first start: a failure to open it is shown, never thrown (PoolWatch keeps running).</summary>
    private void OpenGuide() => _actions.Guarded(null, "Set up PoolWatch", ShowGuide);

    /// <summary>The setup guide, one at a time. It marks itself completed when it closes; the update check then starts (once).</summary>
    private void ShowGuide()
    {
        _balloonOpensGuide = false;
        if (_guide is { IsDisposed: false } open) { Bring(open); return; }
        _guide = new SetupGuideForm(Setup);
        _guide.FormClosed += (_, _) =>
        {
            _guide = null;
            _updates?.Start();
        };
        Open(_guide);
    }

    /// <summary>The setup guide's update switch: the setting itself, applied at once.</summary>
    private sealed class UpdateSwitch(LiveSettings live) : IUpdateCheck
    {
        public bool Enabled
        {
            get => live.Running.CheckForUpdates;
            set => live.Apply($"update notices {(value ? "on" : "off")} (the setup guide)", s => s.CheckForUpdates = value);
        }
    }

    // ---------------------------------------------------------------- IShell

    public void ShowAdvanced(int tab)
    {
        ShowWindow();
        Form.ShowAdvanced(tab);
    }

    /// <summary>Active changes, one at a time (modeless: it reads its list off the UI thread).</summary>
    public void ShowActiveChanges()
    {
        if (_activeChanges is { IsDisposed: false } open) { Bring(open); return; }
        _activeChanges = new ActiveChangesForm(Setup.Ledger, _actions);
        _activeChanges.FormClosed += (_, _) => _activeChanges = null;
        Open(_activeChanges);
    }

    public void ShowReport(string? incidentDir) => _actions.ReportIssue(MainWindowIfShown, incidentDir);

    /// <summary>
    /// The token-leak guard's sign-in check now, with its notices (one set for the app): true when this sign-in is protected
    /// (Windows' setting is on) or needs no protection, false when it could not be protected yet (its notice says what to do).
    /// </summary>
    public async Task<bool> StartTokenGuard()
    {
        var d = _analysis.Diagnostics;
        _guardNotices ??= new GuardNotices(_ui, () => d.Guard.TurnOnByOwner(_guardNotices!));
        await d.StartTokenGuard(_guardNotices);
        return !d.Guard.WorkaroundWanted || TokenGuardNative.GetForegroundLockTimeout() == 0;
    }

    // ---------------------------------------------------------------- tools

    /// <summary>Tray Mark item and hotkey: a note on the timeline ("lag", "closed ChatGPT").</summary>
    public void Mark()
    {
        if (_markOpen) return;
        _markOpen = true;
        try
        {
            var when = DateTime.UtcNow; // the moment the owner noticed, not when they finished typing
            if (PromptDialog.Ask("PoolWatch mark", "Note for the timeline (e.g. \"lag\", \"closed ChatGPT\"):") is { } text &&
                _analysis.Diagnostics.AddMark(text, "tray", when) is { } f)
                Balloon($"Marked at {Fmt.Clock(when)}: {f.Title[6..]}");
        }
        finally { _markOpen = false; }
    }

    private void ToggleExperiment()
    {
        if (_analysis.Diagnostics.AnyExperimentActive) { _analysis.Diagnostics.CancelExperiment(); Balloon("Experiment cancelled."); return; }
        if (PromptDialog.AskExperiment(_engine.Settings.ExperimentPhaseMinutes) is not { } x) return;
        Balloon(_analysis.Diagnostics.StartExperiment(x.app, TimeSpan.FromMinutes(x.minutes))
            ? $"Experiment with {x.app}: the baseline starts now. Leave {x.app} closed; PoolWatch tells you when to start it." : "An experiment is already running.");
    }

    /// <summary>The lag experiment's preset: lag as it is, then a clear, then lag again (phases of ExperimentPhaseMinutes).</summary>
    private void StartClearExperiment()
    {
        double m = _engine.Settings.ExperimentPhaseMinutes;
        Balloon(_analysis.Diagnostics.StartExperiment(Core.Experiments.ClearLagExperiment.PresetName, TimeSpan.FromMinutes(m))
            ? $"Experiment: {m:0.#} min of lag measurements, then PoolWatch clears the standby cache and measures {m:0.#} min more." : "An experiment is already running.");
    }

    /// <summary>The icon's colour once there is a reading (grey until the first sample), and the totals in its tooltip.</summary>
    internal void UpdateIcon()
    {
        var t = _engine.Latest?.Totals;
        var state = UiData.State(_analysis);
        if (t is not null && state != _state) { _icon.Icon = TrayIcons.For(state); _state = state; }
        string text = t is { } x ? $"PoolWatch: paged {Fmt.Bytes((long)x.CommittedPaged)}, non-paged {Fmt.Bytes((long)x.CommittedNonPaged)}" : "PoolWatch: starting";
        if (_analysis.Diagnostics.AnyExperimentActive) text += "\n" + _analysis.Diagnostics.ExperimentStatus().Replace("Experiment: ", "");
        _icon.Text = text.Length > 127 ? text[..127] : text;
    }

    private void Balloon(string text, int ms = 5000, bool opensGuide = false)
    {
        _balloonOpensGuide = opensGuide;
        _icon.BalloonTipTitle = "PoolWatch";
        _icon.BalloonTipText = text.Length > 250 ? text[..250] : text;
        _icon.ShowBalloonTip(ms);
    }

    protected override void ExitThreadCore()
    {
        _exitSignal?.Dispose(); // first: a second request finds nothing to signal
        _updates?.Dispose();
        _analysis.Incidents.Opened -= _onOpened; // the sampler may still raise it until the engine stops
        _analysis.Diagnostics.ExperimentStep -= _onExperiment;
        _hotkey.Dispose();
        Microsoft.Win32.SystemEvents.SessionEnding -= _onSessionEnding;
        _timer.Stop();
        _timer.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _form?.Dispose();
        base.ExitThreadCore();
    }
}
