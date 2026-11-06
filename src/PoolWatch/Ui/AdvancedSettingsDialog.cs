namespace PoolWatch.Ui;

/// <summary>
/// Settings, Advanced: the settings in the property grid, as the old Settings tab showed it, with the same Save. Three are
/// hidden from the grid (<see cref="Core.Settings"/>): two that other controls set, the graph's Show RAM switch and the
/// Services tab's profiles, and the file's format version, which is no choice. Every setting it shows has a short
/// description, which the box under the grid shows for the selected one, the first included: the box shows two lines,
/// and at the dialog's own size each description fits in them (the user can drag the box taller). It edits the same
/// settings as the plain form (<see cref="SettingsEditor"/>). The growth alerts' list opens in its own editor, which shows
/// the description of each alert's two values the same way (<see cref="GrowthRulesEditor"/>). Created on first use, reused
/// while Settings is open, and disposed with it.
/// </summary>
internal sealed class AdvancedSettingsDialog : Form
{
    private readonly SettingsEditor _editor;
    private readonly PropertyGrid _grid = new() { Dock = DockStyle.Fill, ToolbarVisible = false };

    /// <summary>The growth alerts' list opens in an editor that shows each value's description (<see cref="GrowthRulesEditor"/>).</summary>
    static AdvancedSettingsDialog() => GrowthRulesEditor.Register();

    public AdvancedSettingsDialog(SettingsEditor editor)
    {
        _editor = editor;
        Text = "PoolWatch settings: Advanced";
        Icon = TrayIcons.App;
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        Size = LogicalToDeviceUnits(new Size(680, 640));
        MinimumSize = LogicalToDeviceUnits(new Size(480, 360));

        _grid.SelectedObject = editor.Edit;
        var save = new Button { Text = "Save", AutoSize = true, Margin = Px(4, 4, 4, 4) };
        save.Click += (_, _) => editor.Actions.Guarded(this, "Save settings", () => { editor.Save(this); _grid.Refresh(); });
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel, Margin = Px(4, 4, 4, 4) };
        CancelButton = close;
        var note = new Label
        {
            Text = "Save applies automatic clearing, the token-leak guard and update notices at once; the other settings apply the next time PoolWatch starts.",
            AutoSize = true, MaximumSize = new Size(LogicalToDeviceUnits(420), 0), ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.Left, Margin = Px(4, 8, 4, 4),
        };
        var bar = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, RowCount = 1, Padding = Px(8, 6, 8, 8) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.Controls.Add(note, 0, 0);
        bar.Controls.Add(save, 1, 0);
        bar.Controls.Add(close, 2, 0);
        Controls.Add(_grid);
        Controls.Add(bar);
        VisibleChanged += (_, _) => { if (Visible) _grid.Refresh(); }; // the plain form may have changed the same settings
    }

    private Padding Px(int l, int t, int r, int b) => new(LogicalToDeviceUnits(l), LogicalToDeviceUnits(t), LogicalToDeviceUnits(r), LogicalToDeviceUnits(b));

    /// <summary>Closing hides the dialog (it is reused while Settings is open); the plain form disposes it.</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && !Modal) { e.Cancel = true; Hide(); }
        base.OnFormClosing(e);
    }
}
