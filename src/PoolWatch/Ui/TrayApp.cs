using PoolWatch.Analysis;
using PoolWatch.Core.Detection;
using PoolWatch.Engine;
using PoolWatch.Engine.Changes;
using PoolWatch.Native;
using PoolWatch.Ui.Setup;

namespace PoolWatch.Ui;

/// <summary>
/// The tray app: an icon coloured by state (grey while starting, then green / amber / red) with the paged + non-paged
/// totals in its tooltip, a balloon when an incident opens, and the main window on demand. Until the first setup is done,
/// setup is the way in: it opens by itself at every start, and anything that would show the main window brings setup
/// instead (<see cref="FirstRunGate"/>). Its menu runs setup again and opens Active changes, says when a newer release is
/// out (update notices, information only), and holds the tools that are not on the main window (DESIGN.md section 11):
/// Capture now, Since boot report, Open incident folder, Export vendor bundle, Copy latest incident summary, the
/// clear-task removals (shown only while there is something to remove), Report an issue and About. It is the shell setup
/// and the suggestions call (<see cref="IShell"/>): one of each window at a time, and the token-leak guard's sign-in check
/// with its notices. Once setup was completed, it also looks for the token leak every 30 minutes while the window is
/// hidden (<see cref="WatchTokenLeak"/>: the check-only test, which changes nothing), and says so when it finds it with
/// the guard off; a click shows where the window offers the guard. The installer's exit signal ends it cleanly.
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
    /// <summary>The token-leak watch's slow beat in the tray (<see cref="TokenLeakWatchEvery"/>; <see cref="WatchTimer"/>).</summary>
    private readonly System.Windows.Forms.Timer _watchTimer;
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
    private SetupWizardForm? _guide;
    /// <summary>Where each way into the app leads: setup until it was completed once (never in the CI UI test).</summary>
    private readonly FirstRunGate _gate;
    private ActiveChangesForm? _activeChanges;
    /// <summary>What a click on the balloon showing now opens (Active changes, or the token-leak guard where the window offers it), if anything.</summary>
    private readonly BalloonClick _balloonClick = new();

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
        menu.Items.Add(FirstRunGate.MenuItem, null, (_, _) => OpenGuide());
        menu.Items.Add("Active changes...", null, (_, _) => OpenActiveChanges());
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
        _icon.BalloonTipClicked += (_, _) => _balloonClick.Clicked();
        _icon.BalloonTipClosed += (_, _) => _balloonClick.Closed();
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
        // Until setup was completed once, it opens on its own at every start (started at sign-in too), and the main window
        // waits for it. The update check starts once setup has been completed, so the owner has seen its switch first.
        _gate = new FirstRunGate(completed: () => Setup.Store.GuideCompleted, smoke: smoke, openSetup: OpenGuide, showMain: ShowMain,
            switchToAdvanced: tab => Form.ShowAdvanced(tab), switchToSimple: () => Form.ShowView(advanced: false),
            rememberView: advanced => _view?.Remember(advanced), startUpdates: () => _updates?.Start());
        _gate.Start(showWindow);
        // The token-leak watch also runs while the window is hidden (never in the CI UI test, where it refuses anyway).
        _watchTimer = WatchTimer(smoke, WatchTokenLeak);
    }

    /// <summary>How often the tray's token-leak watch looks in (the main window looks in as well while it shows).</summary>
    internal static readonly TimeSpan TokenLeakWatchEvery = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The tray's token-leak watch beat: every <see cref="TokenLeakWatchEvery"/>, <paramref name="tick"/>; started at once,
    /// except in the CI UI test (<paramref name="smoke"/>), where it is never started. The caller stops and disposes it.
    /// </summary>
    internal static System.Windows.Forms.Timer WatchTimer(bool smoke, Action tick)
    {
        var timer = new System.Windows.Forms.Timer { Interval = (int)TokenLeakWatchEvery.TotalMilliseconds };
        if (smoke) return timer;
        timer.Tick += (_, _) => tick();
        timer.Start();
        return timer;
    }

    /// <summary>
    /// The tray's notice when the watch found the leak and the guard is off: a click opens the window on the guard as its
    /// view offers it (<see cref="MainForm.ShowTokenGuardOffer"/>).
    /// </summary>
    internal const string TokenLeakFoundNotice = "PoolWatch found the Windows token leak on this PC. Click here to see the guard that stops it.";

    /// <summary>Whether the tray's watch looks in now: once setup was completed, and only where the check could run at all.</summary>
    internal static bool WatchFromTray(bool setupCompleted, Core.Setup.TokenLeakStatus? leak) =>
        setupCompleted && leak == Core.Setup.TokenLeakStatus.NeedsTest;

    /// <summary>The notice after a check the watch started: only when it found the leak and the guard is still off.</summary>
    internal static string? WatchNotice(Core.TokenGuard.LeakVerdict? verdict, bool guardOn) =>
        verdict == Core.TokenGuard.LeakVerdict.Leak && !guardOn ? TokenLeakFoundNotice : null;

    /// <summary>
    /// The token-leak watch from the tray (<see cref="RecommendationActions.WatchTokenLeak"/>): a check it starts runs in
    /// the background and changes nothing; when it finds the leak, the tray says so and a click opens the window on the
    /// guard (<see cref="ShowTokenGuard"/>). Anything that goes wrong is logged, never shown.
    /// </summary>
    private async void WatchTokenLeak()
    {
        try
        {
            if (!WatchFromTray(Setup.Store.GuideCompleted, Setup.Probe.Last?.TokenLeak)) return;
            if (Setup.Recommendations.WatchTokenLeak(Setup.Inputs()) is not { } check) return;
            await check;
            if (WatchNotice(Setup.Recommendations.LeakTestNow, _live.Running.TokenGuardEnabled) is { } notice) Balloon(notice, 10_000, ShowTokenGuard);
        }
        catch (Exception ex)
        {
            try { _engine.Log.Info("the token-leak watch did not finish: " + ex.Message); } catch { }
        }
    }

    /// <summary>
    /// The token-leak notice was clicked: the window (setup while it was never completed), then the guard as the window's
    /// view offers it, once the window shows (the Simple view's card, or Settings in the Advanced view). Nothing is turned on.
    /// </summary>
    private void ShowTokenGuard()
    {
        ShowWindow();
        if (_form is { Visible: true, IsDisposed: false } form) form.BeginInvoke(form.ShowTokenGuardOffer);
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

    /// <summary>The main window, or setup while it was never completed (<see cref="FirstRunGate"/>).</summary>
    public void ShowWindow() => _gate.ShowWindow();

    private void ShowMain()
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

    /// <summary>Setup from the menu, a first start or a way into the app before it was done: a failure to open it is shown, never thrown (PoolWatch keeps running).</summary>
    private void OpenGuide() => _actions.Guarded(null, "Set up PoolWatch", ShowGuide);

    /// <summary>
    /// Setup, one at a time, in a window of its own. When it closes, <see cref="FirstRunGate.SetupClosed"/> decides what
    /// follows: finished ("Open PoolWatch", it has marked itself completed), the app takes its choice and opens;
    /// cancelled (only possible once it was completed before), nothing more happens.
    /// </summary>
    private void ShowGuide()
    {
        if (_guide is { IsDisposed: false } open) { Bring(open); return; }
        bool advancedBefore = _view?.Advanced == true;
        var guide = _guide = new SetupWizardForm(Setup, firstRun: !Setup.Store.GuideCompleted, advanced: advancedBefore);
        guide.FormClosed += (_, _) =>
        {
            _guide = null;
            _gate.SetupClosed(guide.Finished, guide.Advanced, advancedBefore);
        };
        Open(guide);
    }

    /// <summary>The setup guide's update switch: the setting itself, applied at once.</summary>
    private sealed class UpdateSwitch(LiveSettings live) : IUpdateCheck
    {
        public bool Enabled
        {
            get => live.Running.CheckForUpdates;
            set => live.Apply($"update notices {(value ? "on" : "off")} (setup)", s => s.CheckForUpdates = value);
        }
    }

    // ---------------------------------------------------------------- IShell

    /// <summary>The main window in Advanced view on a tab; before the first setup is done, setup (the view is not switched behind its back).</summary>
    public void ShowAdvanced(int tab) => _gate.ShowAdvanced(tab);

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

    /// <summary>
    /// A notice in the tray from any thread about changes PoolWatch made by itself (a service profile applied at start): a
    /// click on it opens Active changes, which lists them, each with a way to put it back.
    /// </summary>
    public void NotifyChanges(string text) => _ui.Post(_ => Balloon(text, 10_000, OpenActiveChanges), null);

    /// <param name="onClick">What a click on this balloon opens; null: nothing (a click on it never runs an older balloon's).</param>
    private void Balloon(string text, int ms = 5000, Action? onClick = null)
    {
        _balloonClick.Shown(onClick);
        _icon.BalloonTipTitle = "PoolWatch";
        _icon.BalloonTipText = text.Length > ProfileAtStart.MaxNotice ? text[..ProfileAtStart.MaxNotice] : text;
        _icon.ShowBalloonTip(ms);
    }

    /// <summary>Opens Active changes from the tray (its menu, or a notice about changes): a failure is shown, never thrown.</summary>
    private void OpenActiveChanges() => _actions.Guarded(null, "Active changes", ShowActiveChanges);

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
        _watchTimer.Stop();
        _watchTimer.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _form?.Dispose();
        base.ExitThreadCore();
    }
}

/// <summary>
/// The first run, and where each way into the app leads. Until setup was completed once, it is the way in: it opens by
/// itself at every start (also when started at sign-in), the main window stays hidden, and anything that would show it
/// (Open PoolWatch, a double-click on the tray icon, a suggestion's way into the Advanced view) brings setup instead.
/// Setup finished: the app takes its Simple or Advanced choice, the update check starts and the main window opens;
/// cancelled (only a re-run can be), nothing happens. Never in the CI UI test, which opens the main window at once.
/// Without a window: the tray app passes what each of those does, and the tests pass fakes.
/// </summary>
/// <param name="completed">Setup was completed once (read each time: finishing setup marks it).</param>
/// <param name="smoke">The CI UI test.</param>
/// <param name="openSetup">Opens setup, or brings the one open to the front.</param>
/// <param name="showMain">Shows the main window.</param>
/// <param name="switchToAdvanced">Switches the main window to its Advanced view on a tab (0 Pool causes, 1 Services, 2 Tasks and startup apps).</param>
/// <param name="switchToSimple">Switches the main window to its Simple view (setup run again chose it).</param>
/// <param name="rememberView">Keeps the view chosen in setup (true: Advanced) for the next start.</param>
/// <param name="startUpdates">Starts the update check (once; again does nothing).</param>
internal sealed class FirstRunGate(Func<bool> completed, bool smoke, Action openSetup, Action showMain, Action<int> switchToAdvanced, Action switchToSimple,
    Action<bool> rememberView, Action startUpdates)
{
    /// <summary>The tray menu's item that opens setup (again).</summary>
    public const string MenuItem = "Run setup again...";

    /// <summary>Setup comes first: it was never completed, and this is not the CI UI test.</summary>
    public static bool SetupFirst(bool setupCompleted, bool smoke) => !setupCompleted && !smoke;

    private bool SetupFirstNow => SetupFirst(completed(), smoke);

    /// <summary>
    /// The tray app starts: setup while it was never completed, else the main window when it was asked for (not when
    /// started at sign-in). The update check starts only once setup was completed, so its switch was seen first.
    /// </summary>
    public void Start(bool showWindow)
    {
        if (SetupFirstNow) openSetup();
        else if (showWindow) showMain();
        if (completed()) startUpdates();
    }

    /// <summary>A way into the main window: setup instead while it was never completed.</summary>
    public void ShowWindow()
    {
        if (SetupFirstNow) openSetup();
        else showMain();
    }

    /// <summary>A way into the Advanced view: setup instead while it was never completed (the view is not switched behind its back).</summary>
    public void ShowAdvanced(int tab)
    {
        if (SetupFirstNow)
        {
            openSetup();
            return;
        }
        showMain();
        switchToAdvanced(tab);
    }

    /// <summary>
    /// Setup closed. Finished: its Simple or Advanced choice is kept, the update check starts and the main window opens,
    /// switched to the view chosen now when it differs from the one it showed (an open main window shows the view chosen;
    /// one made now opens in it).
    /// Cancelled: nothing.
    /// </summary>
    /// <param name="advancedBefore">The app showed its Advanced view when setup opened.</param>
    public void SetupClosed(bool finished, bool advanced, bool advancedBefore)
    {
        if (!finished) return;
        rememberView(advanced);
        startUpdates();
        ShowWindow();
        if (advanced && !advancedBefore) switchToAdvanced(0);
        else if (!advanced && advancedBefore) switchToSimple();
    }
}

/// <summary>
/// What a click on the tray's balloon does: each balloon sets its own action, or none, so a click never runs an older
/// balloon's; it runs once, and not after the balloon closed.
/// </summary>
internal sealed class BalloonClick
{
    private Action? _action;

    /// <summary>A balloon is shown, with what a click on it opens (null: nothing).</summary>
    public void Shown(Action? onClick) => _action = onClick;

    public void Clicked()
    {
        var action = _action;
        _action = null;
        action?.Invoke();
    }

    public void Closed() => _action = null;
}
