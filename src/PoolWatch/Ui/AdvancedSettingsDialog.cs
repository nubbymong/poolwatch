namespace PoolWatch.Ui;

/// <summary>
/// Settings, Advanced: every setting in the property grid, as the old Settings tab showed it, with the same Save. It edits
/// the same settings as the plain form (<see cref="SettingsEditor"/>). Created once and reused.
/// </summary>
internal sealed class AdvancedSettingsDialog : Form
{
    private readonly SettingsEditor _editor;
    private readonly PropertyGrid _grid = new() { Dock = DockStyle.Fill, ToolbarVisible = false };

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
        var note = new Label { Text = "Changes take effect after PoolWatch restarts.", AutoSize = true, ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.Left, Margin = Px(4, 8, 4, 4) };
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

    /// <summary>Closing hides the dialog (it is reused); the plain form disposes it.</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && !Modal) { e.Cancel = true; Hide(); }
        base.OnFormClosing(e);
    }
}
