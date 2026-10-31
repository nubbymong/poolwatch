using System.Drawing.Drawing2D;

namespace PoolWatch.Ui;

/// <summary>
/// The Services tab's controls and their layout, as the approved app design has them; <see cref="ServicesTab"/> wires
/// what they do. Top to bottom: the toolbar (Find a service, the Profile combo with Save as profile..., Rename... and
/// Delete, "Show not running" and "Show protected", both off at first, and at its right end "Decide with your AI agent"
/// and "Import answer"), the line saying what the profile does, the status line (only while there is something to
/// say), the services list in its groups, and the bar at the bottom: the green line that anything turned off can be put
/// back, then how many services show, Refresh, "Put back..." (its menu: the selected service, the selected service to
/// its Windows default, or all) and the primary "Turn off N services" at the right. The two AI-agent buttons raise
/// <see cref="AgentPromptRequested"/> and <see cref="ImportAnswerRequested"/>; once the tab hands over the windows they
/// open (<see cref="UseAgent"/>), each event opens its window through <see cref="Guard"/>. The search box and the two
/// "Show" ticks filter the list here, and the count line follows what it shows. Made on its own so the tests lay it out
/// at the window's sizes.
/// </summary>
internal sealed class ServicesTabView
{
    private readonly Control _scale;

    /// <summary>Each column's width at 100% when there is room (Source takes what is left), and the least it shrinks to.</summary>
    internal static readonly int[] IdealWidths = [250, 210, 170, 140, 410], LeastWidths = [170, 130, 120, 100, 150];

    public ServicesTabView(Control scale, ToolTip tips)
    {
        _scale = scale;
        foreach (var (h, w) in ServiceRows.Columns.Zip(IdealWidths)) Services.Columns.Add(h, S(w));
        Services.ClientSizeChanged += (_, _) => FitServiceColumns();

        // The toolbar.
        Search.Width = S(190);
        Search.Margin = Px(0, 3, 12, 3);
        Search.PlaceholderText = "Find a service";
        Search.AccessibleName = "Find a service";
        var profileLabel = new Label { Text = "Profile", AutoSize = true, Margin = Px(0, 7, 4, 0) };
        Profile.Width = S(190);
        Profile.Margin = Px(0, 3, 2, 3);
        SaveProfile = ListTabStyle.Style(Btn("Save as profile…"), ListTabStyle.Kind.Link);
        tips.SetToolTip(SaveProfile, ProfileText.SaveTip);
        RenameProfile = ListTabStyle.Style(Btn("Rename…"), ListTabStyle.Kind.Link);
        DeleteProfile = ListTabStyle.Style(Btn("Delete"), ListTabStyle.Kind.Link);
        DeleteProfile.Margin = Px(0, 3, 12, 3);
        ShowNotRunning.Margin = ShowProtected.Margin = Px(0, 6, 12, 0);
        tips.SetToolTip(ShowNotRunning, "Also list the services that are not running now, those PoolWatch turned off among them. A service you ticked always shows.");
        tips.SetToolTip(ShowProtected, "Also list the services PoolWatch never changes: security, Windows Update, input and audio, Hyper-V and WSL, and Windows' core.");
        Agent = ListTabStyle.Style(Btn("Decide with your AI agent"), ListTabStyle.Kind.Accent);
        Agent.Image = Sparkle(S(15), SystemInformation.HighContrast ? SystemColors.ControlText : ListTabStyle.Accent);
        Agent.ImageAlign = ContentAlignment.MiddleLeft;
        Agent.TextImageRelation = TextImageRelation.ImageBeforeText;
        tips.SetToolTip(Agent, "Make a prompt for your AI agent: it asks how you use this PC, then suggests what to turn off.");
        ImportAnswer = ListTabStyle.Style(Btn("Import answer"), ListTabStyle.Kind.Plain);
        tips.SetToolTip(ImportAnswer, "Read your agent's answer file. PoolWatch shows every suggestion before it changes anything.");
        ImportAnswer.Margin = Px(8, 3, 0, 3);
        Agent.Click += (_, _) => AgentPromptRequested?.Invoke(this, EventArgs.Empty);
        ImportAnswer.Click += (_, _) => ImportAnswerRequested?.Invoke(this, EventArgs.Empty);
        AiButtons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
        AiButtons.Controls.AddRange([Agent, ImportAnswer]);
        Toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = Px(0, 2, 0, 4) };
        Toolbar.Controls.AddRange([Search, profileLabel, Profile, SaveProfile, RenameProfile, DeleteProfile, ShowNotRunning, ShowProtected, AiButtons]);
        ListTabStyle.KeepRight(Toolbar, AiButtons, S(12));

        Summary.Padding = Px(2, 0, 2, 4);
        Summary.ForeColor = SystemInformation.HighContrast ? SystemColors.GrayText : ListTabStyle.Muted;
        Status.Padding = Px(2, 2, 2, 4);
        Status.Visible = false;
        Status.TextChanged += (_, _) => Status.Visible = Status.Text.Length > 0;

        // The bar at the bottom.
        Refresh = ListTabStyle.Style(Btn("Refresh"), ListTabStyle.Kind.Plain);
        PutBack = ListTabStyle.Style(Btn("Put back…"), ListTabStyle.Kind.Plain);
        tips.SetToolTip(PutBack, "Put back a service PoolWatch turned off, set one that is off to its Windows default, or put them all back.");
        TurnOff = ListTabStyle.Style(Btn(ServicesPageText.TurnOffText(0)), ListTabStyle.Kind.Primary);
        TurnOff.MinimumSize = new Size(S(150), 0);
        TurnOff.Margin = Px(8, 3, 0, 3);
        Shown.Margin = Px(0, 8, 12, 0);
        Shown.ForeColor = SystemInformation.HighContrast ? SystemColors.GrayText : ListTabStyle.Faint;
        PutBackMenu.Items.AddRange([PutBackSelected, ToWindowsDefault, new ToolStripSeparator(), PutAllBack]);
        PutBack.Click += (_, _) => PutBackMenu.Show(PutBack, new Point(0, PutBack.Height));
        Note.Margin = Px(0, 7, 12, 0);
        Actions = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
        Actions.Controls.AddRange([Shown, Refresh, PutBack, TurnOff]);
        ActionBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = Px(0, 6, 0, 0) };
        ActionBar.Controls.AddRange([Note, Actions]);
        ListTabStyle.KeepRight(ActionBar, Actions, S(12));

        Page = new TabPage("Services") { UseVisualStyleBackColor = true, Padding = Px(8, 6, 8, 6) };
        Page.Controls.Add(Services); // fill first, then the docked edges (the last added is the outermost)
        Page.Controls.Add(Status);
        Page.Controls.Add(Summary);
        Page.Controls.Add(Toolbar);
        Page.Controls.Add(ActionBar);
        // The keyboard goes as the eye does: the toolbar, the list, then the bar at the bottom.
        (Toolbar.TabIndex, Services.TabIndex, ActionBar.TabIndex) = (0, 1, 2);

        // The toolbar's filter reaches the list, and the count line follows what the list shows.
        void ApplyFilter() => Guard("Filter services", () => Services.Filter = Filter);
        Search.TextChanged += (_, _) => ApplyFilter();
        ShowNotRunning.CheckedChanged += (_, _) => ApplyFilter();
        ShowProtected.CheckedChanged += (_, _) => ApplyFilter();
        Services.ListRebuilt += (_, _) => ShowCounts();
        Page.Disposed += (_, _) =>
        {
            PutBackMenu.Dispose();
            Agent.Image?.Dispose();
        };
    }

    private int S(int logical) => _scale.LogicalToDeviceUnits(logical);
    private Padding Px(int l, int t, int r, int b) => new(S(l), S(t), S(r), S(b));

    /// <summary>A button of the tab's bars: close together, so the bars leave the list room.</summary>
    private Button Btn(string text) => new() { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = Px(8, 1, 8, 1), Margin = Px(0, 3, 6, 3) };

    /// <summary>The design's sparkle (a four-pointed star) for the AI agent button, <paramref name="size"/> pixels square.</summary>
    private static Bitmap Sparkle(int size, Color colour)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float k = size / 20f;
        PointF P(float x, float y) => new(x * k, y * k);
        using var pen = new Pen(colour, Math.Max(1f, 1.5f * k)) { LineJoin = LineJoin.Round };
        g.DrawPolygon(pen, [P(10, 2.5f), P(11.7f, 7.1f), P(16.5f, 9), P(11.7f, 10.9f), P(10, 15.5f), P(8.3f, 10.9f), P(3.5f, 9), P(8.3f, 7.1f)]);
        return bmp;
    }

    public TabPage Page { get; }
    public FlowLayoutPanel Toolbar { get; }
    public TextBox Search { get; } = new();
    public ComboBox Profile { get; } = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    public Button SaveProfile { get; }
    public Button RenameProfile { get; }
    public Button DeleteProfile { get; }
    public CheckBox ShowNotRunning { get; } = new() { Text = "Show not running", AutoSize = true };
    public CheckBox ShowProtected { get; } = new() { Text = "Show protected", AutoSize = true };
    public FlowLayoutPanel AiButtons { get; }
    public Button Agent { get; }
    public Button ImportAnswer { get; }
    /// <summary>What the profile picked does (the profile row's line).</summary>
    public WrapLabel Summary { get; } = new() { Dock = DockStyle.Top };
    /// <summary>"Reading services...", "Applying...", a read that failed, a record that cannot be read: hidden while empty.</summary>
    public WrapLabel Status { get; } = new() { Dock = DockStyle.Top };
    public ServiceListView Services { get; } = new() { Dock = DockStyle.Fill };
    public FlowLayoutPanel ActionBar { get; }
    public GreenNote Note { get; } = new("Anything you turn off can be put back from Active changes.");
    public FlowLayoutPanel Actions { get; }
    /// <summary>"Showing 14 of 337 services".</summary>
    public Label Shown { get; } = new() { AutoSize = true };
    public Button Refresh { get; }
    public Button PutBack { get; }
    public Button TurnOff { get; }
    public ContextMenuStrip PutBackMenu { get; } = new();
    public ToolStripMenuItem PutBackSelected { get; } = new("Put back the selected service");
    public ToolStripMenuItem ToWindowsDefault { get; } = new("Set the selected service to its Windows default");
    public ToolStripMenuItem PutAllBack { get; } = new("Put all back");

    /// <summary>"Decide with your AI agent" was pressed (the window it opens is not the tab's).</summary>
    public event EventHandler? AgentPromptRequested;

    /// <summary>"Import answer" was pressed (the window it opens is not the tab's).</summary>
    public event EventHandler? ImportAnswerRequested;

    /// <summary>
    /// Opens the AI-agent windows from the two buttons: "Decide with your AI agent" the prompt window, "Import answer" the
    /// Open dialog and the review of the answer (<paramref name="agent"/>, which the tab makes; <see cref="AgentDecisions"/>
    /// in the app). Each runs through <see cref="Guard"/>, so a failure is reported, not lost. Call it once.
    /// </summary>
    public void UseAgent(IAgentWindows agent)
    {
        AgentPromptRequested += (_, _) => Guard("Decide with your AI agent", agent.OpenPrompt);
        ImportAnswerRequested += (_, _) => Guard("Import answer", agent.ImportAnswer);
    }

    /// <summary>The filter the search box and the two "Show" ticks set now.</summary>
    public ServiceFilter Filter => new(Search.Text, ShowNotRunning.Checked, ShowProtected.Checked);

    /// <summary>Runs what the toolbar does to the list (the tab reports a failure instead of losing it); as it is by default.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<string, Action> Guard { get; set; } = (_, a) => a();

    /// <summary>"Showing 14 of 337 services" as the list shows them now; nothing before the services are read.</summary>
    public void ShowCounts()
    {
        string text = Services.Rows.Count == 0 ? "" : ServiceRows.ShownText(Services.ShownCount, Services.Rows.Count);
        if (Shown.Text != text) Shown.Text = text;
    }

    /// <summary>The buttons and the Put back items as <paramref name="b"/> has them.</summary>
    public void ShowButtons(ServiceButtons b)
    {
        if (TurnOff.Text != b.TurnOffText) TurnOff.Text = b.TurnOffText;
        TurnOff.Enabled = b.TurnOff;
        PutBack.Enabled = b.PutBack;
        PutBackSelected.Enabled = b.PutBackSelected;
        ToWindowsDefault.Enabled = b.ToWindowsDefault;
        PutAllBack.Enabled = b.PutAllBack;
        Refresh.Enabled = b.Refresh;
    }

    /// <summary>
    /// The columns fill the list's width and never pass it (<see cref="ListTabStyle.FitColumns"/>): no sideways scroll bar
    /// down to the window's minimum size. Source takes any room left; when it is too narrow for its line, the row's
    /// tooltip has the whole of it (<see cref="ServiceRows.TipOf"/>).
    /// </summary>
    internal void FitServiceColumns()
    {
        if (Services.Columns.Count < ServiceRows.Columns.Length || Services.ClientSize.Width <= 0) return;
        var widths = ListTabStyle.FitColumns(Services.ClientSize.Width, IdealWidths.Select(S).ToArray(), LeastWidths.Select(S).ToArray(), ServiceRows.SourceColumn);
        for (int i = 0; i < widths.Length; i++) if (Services.Columns[i].Width != widths[i]) Services.Columns[i].Width = widths[i];
    }
}
