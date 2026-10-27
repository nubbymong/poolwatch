using PoolWatch.Analysis;
using PoolWatch.Core.Detection;
using PoolWatch.Engine;

namespace PoolWatch.Ui;

/// <summary>
/// The tray app: an icon coloured by state (green / amber / red) with the paged + non-paged totals in its tooltip,
/// a balloon when an incident opens, and the main window on demand.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly MonitorEngine _engine;
    private readonly AnalysisPipeline _analysis;
    private readonly NotifyIcon _icon;
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
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open PoolWatch", null, (_, _) => ShowWindow());
        menu.Items.Add("Capture now", null, (_, _) =>
        {
            var i = _analysis.CaptureNow();
            string what = Core.Incidents.CaptureGovernor.ScopeFor(Core.Incidents.CaptureSource.Manual, _engine.Settings).Describe();
            Balloon(i is null ? "A capture is already running." : $"Capture started ({what}): {i.Id}");
        });
        menu.Items.Add("Open incident folder", null, (_, _) => OpenFolder(_engine.Paths.EnsureDir(_engine.Paths.IncidentsDir)));
        menu.Items.Add(new ToolStripSeparator());
        _hotkey = new HotkeyWindow(engine.Settings.MarkHotkey, Mark);
        menu.Items.Add(new ToolStripMenuItem("Mark...", null, (_, _) => Mark()) { ShortcutKeyDisplayString = _hotkey.Registered ? _hotkey.Text : null });
        _experimentItem = new ToolStripMenuItem("Experiment...", null, (_, _) => ToggleExperiment());
        menu.Items.Add(_experimentItem);
        var clearLag = new ToolStripMenuItem("Experiment: lag before / after a clear", null, (_, _) => StartClearExperiment());
        menu.Items.Add(clearLag);
        menu.Opening += (_, _) =>
        {
            bool active = _analysis.Diagnostics.AnyExperimentActive;
            _experimentItem.Text = active ? "Stop experiment" : "Experiment...";
            clearLag.Enabled = !active;
        };
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        _icon = new NotifyIcon { ContextMenuStrip = menu, Visible = true, Text = "PoolWatch: starting", Icon = TrayIcons.For(TrayState.Green) };
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

    public MainForm Form => _form ??= new MainForm(_engine, _analysis);

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
        var state = UiData.State(_analysis);
        if (state != _state) { _icon.Icon = TrayIcons.For(state); _state = state; }
        var t = _engine.Latest?.Totals;
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

    /// <summary>
    /// Opens a folder in Explorer. S1/S3: the elevated app never shell-executes a path (that consults the user's shell
    /// associations and App Paths); it starts explorer.exe by full path with the folder as an argument, UseShellExecute = false.
    /// </summary>
    private static void OpenFolder(string folder)
    {
        string explorer = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(explorer, $"\"{System.IO.Path.GetFullPath(folder)}\"") { UseShellExecute = false }); } catch { }
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
        _form?.Dispose();
        base.ExitThreadCore();
    }
}
