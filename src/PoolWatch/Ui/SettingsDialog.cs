using PoolWatch.Analysis;
using PoolWatch.Engine;

namespace PoolWatch.Ui;

/// <summary>
/// Settings, opened from the main window's Settings button: a short form in plain words with what most people change
/// (Start at logon, automatic cache clearing, deeper capture, the token-leak guard) and an Advanced button for every
/// setting (the old grid).
/// Start at logon is a scheduled task and changes at once; the rest is written to settings.json by Save and taken at
/// PoolWatch's next start, as before. The form sizes itself to its content, so nothing is cut off at any scaling.
/// Created once and reused (the edited settings last as long as the window).
/// </summary>
internal sealed class SettingsDialog : Form
{
    private readonly MonitorEngine _engine;
    private readonly SettingsEditor _editor;
    private readonly CheckBox _logon = new() { Text = "Start PoolWatch when I sign in", AutoSize = true };
    private readonly CheckBox _autoClear = new() { Text = "Clear the standby cache automatically", AutoSize = true };
    private readonly NumericUpDown _hours = new() { DecimalPlaces = 1, Increment = 0.5m, Minimum = 0, Maximum = SimpleSettings.MaxHours, TextAlign = HorizontalAlignment.Right };
    private readonly NumericUpDown _poolGb = new() { DecimalPlaces = 1, Increment = 1, Minimum = 0, Maximum = SimpleSettings.MaxPoolGb, TextAlign = HorizontalAlignment.Right };
    private readonly CheckBox _deeper = new() { Text = "Deeper automatic captures", AutoSize = true };
    private readonly Label _clearHint = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Label _deeperHint = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly CheckBox _tokenGuard = new() { Text = SimpleSettings.TokenGuardLabel, AutoSize = true };
    private readonly ToolTip _tips = new() { AutoPopDelay = 30_000 };
    private readonly Label _problems = new() { AutoSize = true, ForeColor = Color.Firebrick, Visible = false };
    private readonly List<Control> _clearFields = [];
    private AdvancedSettingsDialog? _advanced;

    public SettingsDialog(MonitorEngine engine, AnalysisPipeline analysis, UiActions actions)
    {
        _engine = engine;
        _editor = new SettingsEditor(engine, analysis, actions);
        Text = "PoolWatch settings";
        Icon = TrayIcons.For(TrayState.Green);
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        int width = S(520), indent = S(22);
        var bold = new Font(Font, FontStyle.Bold);
        var form = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Padding = Px(16, 12, 16, 12), Location = Point.Empty };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
        void Row(Control c, int indentPx = 0, int top = 0, int bottom = 4)
        {
            c.Margin = new Padding(indentPx, S(top), 0, S(bottom));
            if (c is Label { AutoSize: true } l) l.MaximumSize = new Size(width - indentPx, 0);
            form.Controls.Add(c);
        }
        Label Heading(string text) => new() { Text = text, AutoSize = true, Font = bold };
        Label Hint(string text) => new() { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText };
        FlowLayoutPanel Line(params Control[] parts)
        {
            var p = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
            foreach (var c in parts)
            {
                c.Margin = c is Label ? new Padding(0, S(5), S(6), 0) : new Padding(0, S(2), S(6), 0);
                p.Controls.Add(c);
            }
            return p;
        }

        Row(Heading("Start at logon"));
        Row(_logon, indent);
        Row(Hint("A scheduled task with the highest privileges, so there is no UAC prompt at each sign-in. It changes as soon as you " +
                 "tick or untick it, and is created only for a copy installed in Program Files."), indent, bottom: 10);

        Row(Heading("Automatic cache clearing"));
        Row(_autoClear, indent);
        _hours.Width = S(70);
        _poolGb.Width = S(70);
        var every = Line(new Label { Text = "Every", AutoSize = true }, _hours, new Label { Text = "hours (0 = no schedule)", AutoSize = true });
        var over = Line(new Label { Text = "When the kernel pool is over", AutoSize = true }, _poolGb, new Label { Text = "GB (0 = no size rule)", AutoSize = true });
        Row(every, indent * 2);
        Row(over, indent * 2);
        Row(_clearHint, indent * 2, bottom: 10);
        _clearFields.AddRange([every, over]);

        Row(Heading("Deeper capture"));
        Row(_deeper, indent);
        Row(_deeperHint, indent, bottom: 10);

        // The guard starts with the tray app (UiRunner), so like the others it takes effect from PoolWatch's next start.
        Row(Heading("Token-leak guard"));
        Row(_tokenGuard, indent);
        Row(Hint(SimpleSettings.TokenGuardHint), indent, bottom: 10);
        _tips.SetToolTip(_tokenGuard, SimpleSettings.TokenGuardTip);

        Row(_problems, 0, bottom: 6);
        Row(Hint("Start at logon changes at once. Save writes the other settings now, the token-leak guard included; PoolWatch uses them from its next start."), 0, bottom: 8);

        var advanced = new Button { Text = "Advanced...", AutoSize = true, Margin = Padding.Empty };
        advanced.Click += (_, _) => actions.Guarded(this, "Advanced settings", OpenAdvanced);
        var save = new Button { Text = "Save", AutoSize = true, Margin = new Padding(S(6), 0, 0, 0) };
        save.Click += (_, _) => actions.Guarded(this, "Save settings", Save);
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel, Margin = new Padding(S(6), 0, 0, 0) };
        CancelButton = close;
        var buttons = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty, Dock = DockStyle.Fill };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.Controls.Add(advanced, 0, 0);
        buttons.Controls.Add(save, 2, 0);
        buttons.Controls.Add(close, 3, 0);
        form.Controls.Add(buttons);
        Controls.Add(form);

        _autoClear.CheckedChanged += (_, _) => EnableClearFields();
        LoadFrom();

        // schtasks /query can take seconds: ask in the background, then wire the checkbox on this (the UI) thread. The
        // answer is posted to the context captured here, so it arrives whether or not the dialog has a window handle yet.
        _logon.Enabled = false;
        var ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        Task.Run(SafeExists).ContinueWith(t => ui.Post(_ => { if (!IsDisposed) WireLogon(t.Result, actions); }, null), TaskScheduler.Default);
    }

    private int S(int logical) => LogicalToDeviceUnits(logical);
    private Padding Px(int l, int t, int r, int b) => new(S(l), S(t), S(r), S(b));

    /// <summary>The Advanced grid (created on first use; CI renders it for the screenshots).</summary>
    public AdvancedSettingsDialog Advanced => _advanced ??= new AdvancedSettingsDialog(_editor);

    /// <summary>The form's fields from the edited settings (also after Advanced, which edits the same settings).</summary>
    private void LoadFrom()
    {
        var v = SimpleSettings.From(_editor.Edit);
        _autoClear.Checked = v.AutoClear;
        _hours.Value = v.EveryHours;
        _poolGb.Value = v.PoolGb;
        _deeper.Checked = v.DeeperCapture;
        _tokenGuard.Checked = v.TokenGuard;
        _clearHint.Text = SimpleSettings.AutoClearHint(_editor.Edit);
        _deeperHint.Text = SimpleSettings.DeeperCaptureHint(_editor.Edit);
        EnableClearFields();
        ShowProblems([]);
    }

    private SimpleSettings Values() => new(_autoClear.Checked, _hours.Value, _poolGb.Value, _deeper.Checked, _tokenGuard.Checked);

    private void EnableClearFields()
    {
        foreach (var c in _clearFields) c.Enabled = _autoClear.Checked;
    }

    private void ShowProblems(List<string> problems)
    {
        _problems.Text = string.Join("\n", problems);
        _problems.Visible = problems.Count > 0;
    }

    private void Save()
    {
        if (!Values().ApplyTo(_editor.Edit, out var problems)) { ShowProblems(problems); return; }
        ShowProblems([]);
        _editor.Save(this);
        LoadFrom(); // Save may have kept the automatic clear off (your own clear task stays)
    }

    private void OpenAdvanced()
    {
        // The grid shows what this form holds now (it must be valid first), and this form shows what the grid saved.
        if (!Values().ApplyTo(_editor.Edit, out var problems)) { ShowProblems(problems); return; }
        if (!Advanced.Visible) Advanced.ShowDialog(this);
        LoadFrom();
    }

    private void WireLogon(bool exists, UiActions actions)
    {
        _logon.Checked = exists;
        _logon.Enabled = true;
        _logon.CheckedChanged += (_, _) => actions.Guarded(this, "Start at logon", () =>
        {
            var (ok, output) = LogonTask.Set(_logon.Checked, Path.Combine(_engine.Paths.DataRoot, "logon-task.xml"), _engine.Paths.Guard.CheckReal);
            if (!ok) MessageBox.Show(this, "Could not change the logon task:\n" + output, "PoolWatch");
        });
    }

    private static bool SafeExists()
    {
        try { return LogonTask.Exists(); } catch { return false; }
    }

    /// <summary>Closing hides the dialog (it is reused); the window disposes it.</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && !Modal) { e.Cancel = true; Hide(); }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _advanced?.Dispose(); _tips.Dispose(); }
        base.Dispose(disposing);
    }
}
