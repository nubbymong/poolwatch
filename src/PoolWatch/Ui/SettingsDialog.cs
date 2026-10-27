using PoolWatch.Analysis;
using PoolWatch.Core;
using PoolWatch.Engine;

namespace PoolWatch.Ui;

/// <summary>
/// Settings, opened from the main window's Settings button: the settings grid, Save, and Start at logon. It was the
/// Settings tab; the behaviour is the same (saved settings take effect after a restart; turning on the automatic clear
/// first deals with any clear task of your own). Created once and reused, so the edited settings and the "automatic clear
/// confirmed" state last as long as the window, as they did on the tab.
/// </summary>
internal sealed class SettingsDialog : Form
{
    private readonly MonitorEngine _engine;
    private readonly AnalysisPipeline _analysis;
    private readonly UiActions _actions;
    private readonly Settings _editSettings;
    private readonly PropertyGrid _grid = new() { Dock = DockStyle.Fill, ToolbarVisible = false };
    private readonly CheckBox _logon = new() { Text = "Start at logon (scheduled task, highest privileges: no daily UAC prompt)", AutoSize = true, Anchor = AnchorStyles.Left };
    private bool _autoClearConfirmed;
    private bool? _logonPending;

    public SettingsDialog(MonitorEngine engine, AnalysisPipeline analysis, UiActions actions)
    {
        _engine = engine;
        _analysis = analysis;
        _actions = actions;
        _editSettings = Clone(engine.Settings);
        Text = "PoolWatch settings";
        Icon = TrayIcons.For(TrayState.Green);
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        Size = LogicalToDeviceUnits(new Size(680, 640));
        MinimumSize = LogicalToDeviceUnits(new Size(480, 360));

        _grid.SelectedObject = _editSettings;
        var save = new Button { Text = "Save", AutoSize = true, Margin = Px(4, 4, 4, 4) };
        save.Click += (_, _) => _actions.Guarded(this, "Save settings", Save);
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel, Margin = Px(4, 4, 4, 4) };
        CancelButton = close;
        var note = new Label { Text = "Changes take effect after PoolWatch restarts.", AutoSize = true, ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.Left, Margin = Px(4, 8, 4, 4) };
        // Start at logon on a row of its own; below it the note on the left, Save and Close on the right.
        var bar = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, RowCount = 2, Padding = Px(8, 6, 8, 8) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _logon.Margin = Px(4, 4, 4, 4);
        bar.Controls.Add(_logon, 0, 0);
        bar.SetColumnSpan(_logon, 3);
        bar.Controls.Add(note, 0, 1);
        bar.Controls.Add(save, 1, 1);
        bar.Controls.Add(close, 2, 1);
        Controls.Add(_grid);
        Controls.Add(bar);

        // schtasks /query can take seconds: ask in the background, then wire the checkbox.
        _logon.Enabled = false;
        Task.Run(SafeExists).ContinueWith(t =>
        {
            if (!IsHandleCreated || IsDisposed) { _logonPending = t.Result; return; }
            BeginInvoke(() => WireLogon(t.Result));
        });
        HandleCreated += (_, _) => { if (_logonPending is { } v) { _logonPending = null; BeginInvoke(() => WireLogon(v)); } };
    }

    private Padding Px(int l, int t, int r, int b) => new(LogicalToDeviceUnits(l), LogicalToDeviceUnits(t), LogicalToDeviceUnits(r), LogicalToDeviceUnits(b));

    private static Settings Clone(Settings s) => System.Text.Json.JsonSerializer.Deserialize<Settings>(System.Text.Json.JsonSerializer.Serialize(s))!;

    private void Save()
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
    }

    private void SaveSettings(string? message)
    {
        _editSettings.Save(_engine.Paths.Guard.CheckReal(_engine.Paths.SettingsFile));
        _grid.Refresh();
        if (message is not null) MessageBox.Show(this, message, "PoolWatch");
    }

    /// <summary>
    /// Turning the automatic clear on: finds any clear task of your own (RAMMap in a scheduled task), shows it, and removes
    /// it only on a yes; kept, the automatic clear stays off. In-process (S5, review round 2: no UAC step any more;
    /// PoolWatch is elevated).
    /// </summary>
    private void ConfirmAutoClear()
    {
        if (!CacheHelper.IsElevated) { MessageBox.Show(this, UiActions.NotElevated, "PoolWatch"); return; }
        if (_actions.LegacyInProcess(this) == 0)
        {
            _autoClearConfirmed = true;
            _editSettings.AutoClearEnabled = true;
            SaveSettings("The automatic clear is on (from the next start of PoolWatch).");
        }
        else SaveSettings("Your own clear task stays, so PoolWatch's automatic clear stays off (the two should not both run).");
        _analysis.Diagnostics.Cache.RefreshStatus();
    }

    private void WireLogon(bool exists)
    {
        _logon.Checked = exists;
        _logon.Enabled = true;
        _logon.CheckedChanged += (_, _) => _actions.Guarded(this, "Start at logon", () =>
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
}
