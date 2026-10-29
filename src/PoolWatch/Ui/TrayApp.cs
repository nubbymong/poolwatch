using PoolWatch.Analysis;
using PoolWatch.Core.Detection;
using PoolWatch.Engine;

namespace PoolWatch.Ui;

/// <summary>
/// The tray app: an icon coloured by state (green / amber / red) with the paged + non-paged totals in its tooltip,
/// a balloon when an incident opens, and the main window on demand. Its menu also holds the tools that are not on the
/// simplified main window (DESIGN.md section 11): Capture now, Since boot report, Open incident folder, Export vendor
/// bundle, Copy latest incident summary, the clear-task removals (shown only while there is something to remove) and About.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly MonitorEngine _engine;
    private readonly AnalysisPipeline _analysis;
    private readonly UiActions _actions;
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
    private bool _markOpen;

    /// <summary>The state the tray icon currently shows.</summary>
    public TrayState IconState => _state;

    public TrayApp(MonitorEngine engine, AnalysisPipeline analysis, bool showWindow)
    {
        _engine = engine;
        _analysis = analysis;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _actions = new UiActions(engine, analysis, _ui);
        var menu = _menu;
        menu.Items.Add("Open PoolWatch", null, (_, _) => ShowWindow());
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
        menu.Items.Add("About PoolWatch", null, (_, _) => _actions.About(null));
        menu.Opening += (_, _) =>
        {
            bool active = _analysis.Diagnostics.AnyExperimentActive;
            _experimentItem.Text = active ? "Stop experiment" : "Experiment...";
            clearLag.Enabled = !active;
            removeLegacy.Visible = _actions.LegacyTaskPresent;
            removeOld.Visible = _actions.OldTasksPresent;
        };
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        _icon = new NotifyIcon { ContextMenuStrip = menu, Visible = true, Text = "PoolWatch: starting", Icon = TrayIcons.Starting };
        _icon.DoubleClick += (_, _) => ShowWindow();
        _onOpened = (inc, a) => _ui.Post(_ => Balloon($"{inc.Tag.ToDisplay()}: {a.Rule}. {inc.Suspects.FirstOrDefault()?.DisplayName ?? ""}"), null);
        _analysis.Incidents.Opened += _onOpened;
        _onExperiment = step => _ui.Post(_ => Balloon(step.Prompt, step.Phase == Core.Experiments.ExperimentPhase.Done ? 20_000 : 10_000), null);
        _analysis.Diagnostics.ExperimentStep += _onExperiment;
        if (Core.Experiments.Hotkey.Parse(engine.Settings.MarkHotkey) is not null && !_hotkey.Registered)
            engine.Log.Info($"mark hotkey {engine.Settings.MarkHotkey} is held by another app; use the tray Mark item");
        // Shutdown / logoff: exit cleanly so open incidents are closed and written.
        _onSessionEnding = (_, _) => ExitThread();
        Microsoft.Win32.SystemEvents.SessionEnding += _onSessionEnding;
        // Tray refresh: one cheap state read every 10 s (no painting, no animation).
        _timer.Tick += (_, _) => UpdateIcon();
        _timer.Start();
        UpdateIcon();
        if (showWindow) ShowWindow();
    }

    public MainForm Form => _form ??= new MainForm(_engine, _analysis, _actions);

    /// <summary>Every item of the tray menu, by text (CI: what the main window gave up must still be reachable).</summary>
    public List<string> MenuItemTexts() => _menu.Items.OfType<ToolStripMenuItem>().Select(i => i.Text ?? "").ToList();

    public void ShowWindow()
    {
        Form.Show();
        if (Form.WindowState == FormWindowState.Minimized) Form.WindowState = FormWindowState.Normal;
        Form.Activate();
    }

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

    /// <summary>The A/B preset of addendum 2 C: lag as it is, then a clear, then lag again (phases of ExperimentPhaseMinutes).</summary>
    private void StartClearExperiment()
    {
        double m = _engine.Settings.ExperimentPhaseMinutes;
        Balloon(_analysis.Diagnostics.StartExperiment(Core.Experiments.ClearLagExperiment.PresetName, TimeSpan.FromMinutes(m))
            ? $"Experiment: {m:0.#} min of lag measurements, then PoolWatch clears the standby cache and measures {m:0.#} min more." : "An experiment is already running.");
    }

    private void UpdateIcon()
    {
        var t = _engine.Latest?.Totals;
        var state = UiData.State(_analysis);
        if (t is not null && state != _state) { _icon.Icon = TrayIcons.For(state); _state = state; }
        string text = t is { } x ? $"PoolWatch: paged {Fmt.Bytes((long)x.CommittedPaged)}, non-paged {Fmt.Bytes((long)x.CommittedNonPaged)}" : "PoolWatch: starting";
        if (_analysis.Diagnostics.AnyExperimentActive) text += "\n" + _analysis.Diagnostics.ExperimentStatus().Replace("Experiment: ", "");
        _icon.Text = text.Length > 127 ? text[..127] : text;
    }

    private void Balloon(string text, int ms = 5000)
    {
        _icon.BalloonTipTitle = "PoolWatch";
        _icon.BalloonTipText = text.Length > 250 ? text[..250] : text;
        _icon.ShowBalloonTip(ms);
    }

    protected override void ExitThreadCore()
    {
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
