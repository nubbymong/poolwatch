using System.Runtime.InteropServices;
using PoolWatch.Core.Services;
using PoolWatch.Ui;
using Xunit.Abstractions;

namespace PoolWatch.AppTests;

/// <summary>
/// The Services and the Tasks and startup apps tabs laid out as the main window lays them out (the header, the rows
/// <see cref="MainForm.AdvancedRows"/> gives the tab picked, the footer), at the window's default size (1100 x 750), at
/// 150% on a 1920 x 1080 screen (the window cut to 672 logical pixels), on the smallest desktop PoolWatch fits (1024 x
/// 600 logical) and at the window's minimum size (760 x 540): every list keeps rows to work with, nothing is cut off,
/// neither list scrolls sideways, the bars keep their right-hand buttons at the right, and a very long command scrolls
/// in its box instead of pushing the list away. The same at 125, 150 and 200% as far as a test can scale it (its text,
/// its sizes). Never shown: the tabs sit in a panel of the window's client size, which no desktop cuts.
/// </summary>
[Collection(nameof(WholeWindowLayout))] // lays windows out: one class at a time, after the parallel ones
public sealed class ServicesTabLayoutTests(ITestOutputHelper output)
{
    private sealed class Window : IDisposable
    {
        public readonly Form Form = new() { FormBorderStyle = FormBorderStyle.Sizable, Size = new Size(400, 300) };
        public readonly Panel Root;
        public readonly ServicesTabView Services;
        public readonly TasksTabView Tasks;
        public readonly TabControl Tabs = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };
        public readonly ToolTip Tips = new();

        /// <param name="size">The window's size in logical pixels (its frame and title bar taken off for its client area).</param>
        /// <param name="percent">The display scaling, as far as a test can make it: the text and the sizes.</param>
        /// <param name="detail">The row the detail panel shows (a short command by default).</param>
        public Window(Size size, int percent = 100, bool servicesTab = true, TaskRow? detail = null)
        {
            int P(int n) => n * percent / 100;
            Root = new Panel { Size = new Size(P(size.Width - 16), P(size.Height - 39)), Font = new Font("Segoe UI", 9f * percent / 100) };
            Form.Controls.Add(Root);
            Services = new ServicesTabView(Root, Tips);
            Tasks = new TasksTabView(Root, Tips);
            var header = new Panel { Dock = DockStyle.Top, Height = P(49) };   // the status line and buttons
            var footer = new Panel { Dock = DockStyle.Bottom, Height = P(43) }; // Active changes, Advanced view
            var advanced = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(P(12), 0, P(12), P(4)) };
            advanced.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var (graphRow, tabsRow) = MainForm.AdvancedRows(servicesTab);
            advanced.RowStyles.Add(graphRow);
            advanced.RowStyles.Add(tabsRow);
            Tabs.TabPages.Add(new TabPage("Pool causes"));
            Tabs.TabPages.Add(Services.Page);
            Tabs.TabPages.Add(Tasks.Page);
            advanced.Controls.Add(new Panel { Dock = DockStyle.Fill, Visible = !servicesTab }, 0, 0);
            advanced.Controls.Add(Tabs, 0, 1);
            Root.Controls.AddRange([advanced, footer, header]);
            Form.CreateControl();
            _ = Tabs.Handle;
            Services.Profile.Items.Add("Agentic coding PC (built-in)");
            Services.Summary.Text = "Agentic coding PC (built-in, read-only): ticks the 11 services that are not off yet. 12 ticked now.";
            Services.Services.SetRows(Enumerable.Range(0, 3).SelectMany(_ => SomeServices.Rows()).ToList());
            Services.Services.Filter = new ServiceFilter(ShowNotRunning: true);
            Services.Shown.Text = ServiceRows.ShownText(36, 337);
            Services.ShowButtons(ServicesPageText.Buttons(false, 12, 3, null, null));
            Tasks.List.RowOf = _ => null;
            for (int i = 0; i < 30; i++) Tasks.List.Items.Add(new ListViewItem(["Startup app " + i, "Startup app", "Low: 0.2 core", "at sign-in"]));
            Tasks.Detail.Display(TaskRows.Detail(detail ?? TasksTabTests.StreamDeck()));
            Show(1);
        }

        /// <summary>Picks a tab and lays it out as the tab control gives its page room once the window shows.</summary>
        public void Show(int tab)
        {
            Tabs.SelectedIndex = tab;
            var page = Tabs.TabPages[tab];
            page.Bounds = Tabs.DisplayRectangle;
            foreach (Control c in page.Controls) _ = c.Handle;
            Root.PerformLayout();
            page.PerformLayout();
            Services.Toolbar.PerformLayout();
            Tasks.Toolbar.PerformLayout();
            Services.Actions.PerformLayout(); // the bottom bar, as a window that shows lays it out
            Services.ActionBar.PerformLayout();
            page.PerformLayout();
        }

        public void Dispose() { Form.Dispose(); Tips.Dispose(); }
    }

    /// <summary>The whole rows a list shows under its header.</summary>
    private static int Rows(ListView list)
    {
        var header = SendMessage(list.Handle, 0x1000 + 31 /* LVM_GETHEADER */, IntPtr.Zero, IntPtr.Zero);
        GetWindowRect(header, out var r);
        int row = list.Items[0].Bounds.Height;
        return (list.ClientSize.Height - (r.Bottom - r.Top)) / row;
    }

    public static IEnumerable<object[]> Sizes() =>
    [
        [1100, 750, 100, 14, 5], // the default size at 100%
        [1100, 672, 100, 11, 3], // 150% on a 1920 x 1080 screen: the working area cuts the window to 672 logical pixels
        [1024, 600, 100, 8, 2],  // the smallest desktop PoolWatch fits
        [760, 540, 100, 5, 1],   // the window's minimum size
        [1024, 600, 125, 8, 2],
        [1024, 600, 150, 8, 2],
        [1024, 600, 200, 8, 2],
    ];

    [Theory]
    [MemberData(nameof(Sizes))]
    public void BothTabsKeepTheirRowsAndNothingIsCutOff(int width, int height, int percent, int servicesRows, int tasksRows)
    {
        using var w = new Window(new Size(width, height), percent);
        w.Show(1);
        int services = Rows(w.Services.Services);
        var problems = new List<string>();
        MainForm.ClippedControls(w.Services.Page, problems, WindowFitTests.OwnVisible);
        w.Show(2);
        int tasks = Rows(w.Tasks.List);
        MainForm.ClippedControls(w.Tasks.Page, problems, WindowFitTests.OwnVisible);
        output.WriteLine($"{width} x {height} at {percent}%: services {services} rows, tasks {tasks} rows; detail panel {w.Tasks.Detail.Height} px");
        foreach (var problem in problems) output.WriteLine(problem);
        Assert.Empty(problems);
        Assert.True(services >= servicesRows, $"the services list shows {services} rows");
        Assert.True(tasks >= tasksRows, $"the tasks list shows {tasks} rows");
        Assert.InRange(w.Tasks.Detail.NeededHeight, 1, w.Tasks.Detail.Height);
        // Neither list scrolls sideways: their columns fill them and never pass them, down to the window's minimum size.
        w.Show(1);
        NoSidewaysScroll(w.Services.Services);
        w.Show(2);
        NoSidewaysScroll(w.Tasks.List);
    }

    private static void NoSidewaysScroll(ListView list)
    {
        int columns = list.Columns.Cast<ColumnHeader>().Sum(c => c.Width);
        Assert.True(columns <= list.ClientSize.Width, $"{list.AccessibleName}: columns {columns} px in {list.ClientSize.Width} px");
        Assert.True((GetWindowLong(list.Handle, -16 /* GWL_STYLE */) & 0x00100000 /* WS_HSCROLL */) == 0, $"{list.AccessibleName} shows a sideways scroll bar");
    }

    /// <summary>A command of over 2,000 characters, as a browser's, an editor's or a tool's helper often has.</summary>
    internal static readonly string LongCommand = "\"C:\\Program Files\\Example Vendor\\Deck\\deck.exe\" " +
        string.Join(" ", Enumerable.Range(0, 80).Select(i => $"--flag-number-{i}=value{i}"));

    [Theory]
    [MemberData(nameof(Sizes))]
    public void ALongCommandScrollsInItsBoxAndTheListKeepsItsRows(int width, int height, int percent, int servicesRows, int tasksRows)
    {
        _ = servicesRows;
        var deck = TasksTabTests.StreamDeck();
        using var w = new Window(new Size(width, height), percent, detail: deck with { Entry = deck.Entry with { Command = LongCommand } });
        w.Show(2);
        var panel = w.Tasks.Detail;
        int page = w.Tasks.Page.DisplayRectangle.Height;
        int tasks = Rows(w.Tasks.List);
        output.WriteLine($"{width} x {height} at {percent}%: tasks {tasks} rows; detail panel {panel.Height} px of a {page} px page, command {panel.CommandView}");
        Assert.True(LongCommand.Length > 2000);
        Assert.True(tasks >= tasksRows, $"the tasks list shows {tasks} rows");
        Assert.True(panel.Height <= (int)(page * TaskDetailPanel.MaxShare) + 1, $"the detail panel takes {panel.Height} px of a {page} px page");
        Assert.True(panel.CommandView.Scrolls);
        Assert.InRange(panel.CommandView.Lines, 1, TaskDetailPanel.MaxCommandLines);
        Assert.Equal(ScrollBars.Vertical, panel.Command.ScrollBars);
        Assert.Equal((LongCommand, true), (panel.Command.Text, panel.Command.ReadOnly)); // all of it, to read or copy
        Assert.True(panel.Command.Bottom <= panel.Height);
        var problems = new List<string>();
        MainForm.ClippedControls(w.Tasks.Page, problems, WindowFitTests.OwnVisible);
        Assert.Empty(problems);
    }

    [Fact]
    public void TheDetailPanelKeepsItsShareWhenOnlyTheWindowsHeightChanges()
    {
        // A window dragged shorter (or snapped) keeps its width: the panel, docked at the bottom, is only moved then, so it
        // must lay itself out again for its command's box to keep within its share of the shorter page.
        var deck = TasksTabTests.StreamDeck();
        using var w = new Window(new Size(1100, 750), detail: deck with { Entry = deck.Entry with { Command = LongCommand } });
        w.Show(2);
        var panel = w.Tasks.Detail;
        int before = panel.Height;
        w.Root.Height = 540;
        w.Root.PerformLayout();
        w.Tasks.Page.Bounds = w.Tabs.DisplayRectangle;
        w.Tasks.Page.PerformLayout();
        int page = w.Tasks.Page.DisplayRectangle.Height;
        output.WriteLine($"detail panel {before} px, then {panel.Height} px of a {page} px page");
        Assert.True(panel.Height < before, $"the panel stayed {panel.Height} px");
        Assert.True(panel.Height <= (int)(page * TaskDetailPanel.MaxShare) + 1, $"the detail panel takes {panel.Height} px of a {page} px page");
    }

    [Fact]
    public void AShortCommandShowsWholeWithNoScrollBar()
    {
        using var w = new Window(new Size(1100, 750));
        w.Show(2);
        Assert.Equal((1, false), w.Tasks.Detail.CommandView);
        Assert.Equal(ScrollBars.None, w.Tasks.Detail.Command.ScrollBars);
        // As the design has it, who it is from and when it starts sit on the name's line when there is room.
        var d = w.Tasks.Detail;
        Assert.True(d.Sub.Left > d.Title.Left && d.Sub.Top < d.Title.Bottom, $"title {d.Title.Bounds}, beside it {d.Sub.Bounds}");
    }

    [Fact]
    public void AtTwoHundredPercentTheBoldButtonsAndThePanelsFontsScaleToo()
    {
        if (SystemInformation.HighContrast) return; // then the buttons keep Windows' own look and font
        using var w = new Window(new Size(1024, 600), 200);
        w.Show(2);
        float root = w.Root.Font.SizeInPoints; // 18
        foreach (var b in new[] { w.Services.TurnOff, w.Services.Agent, w.Tasks.Detail.Toggle })
            Assert.Equal((true, root), (b.Font.Bold, b.Font.SizeInPoints));
        Assert.Equal(root, w.Services.Refresh.Font.SizeInPoints);
        Assert.InRange(w.Tasks.Detail.Title.Font.SizeInPoints, root * 10 / 9 - 0.01f, root * 10 / 9 + 0.01f);
        Assert.InRange(w.Tasks.Detail.Command.Font.SizeInPoints, root * 8.5f / 9 - 0.01f, root * 8.5f / 9 + 0.01f);
    }

    [Fact]
    public void TheServicesToolbarFiltersTheListAndTheCountLineFollowsIt()
    {
        using var w = new Window(new Size(1100, 750));
        var view = new ServicesTabView(w.Root, w.Tips);
        var guarded = new List<string>();
        view.Guard = (what, a) => { guarded.Add(what); a(); };
        Assert.Equal("", view.Shown.Text); // nothing read yet
        view.Services.SetRows(SomeServices.Rows());
        Assert.Equal("Showing 4 of 12 services", view.Shown.Text);
        view.ShowNotRunning.Checked = true;
        Assert.Equal((new ServiceFilter(ShowNotRunning: true), "Showing 10 of 12 services"), (view.Services.Filter, view.Shown.Text));
        view.Search.Text = "wsearch";
        Assert.Equal("Showing 1 of 12 services", view.Shown.Text);
        view.ShowProtected.Checked = true;
        view.Search.Text = "";
        Assert.Equal("Showing 12 of 12 services", view.Shown.Text);
        Assert.Equal(4, guarded.Count(g => g == "Filter services"));
        view.Services.SetTicks(["SysMain"]);  // a profile picked: the ticked service shows, and the count says so
        view.ShowNotRunning.Checked = false;
        view.ShowProtected.Checked = false;
        Assert.Equal("Showing 5 of 12 services", view.Shown.Text);
    }

    [Fact]
    public void EveryChangeOfTheTasksToolbarRaisesFilterChangedOnce()
    {
        using var w = new Window(new Size(1100, 750));
        var view = new TasksTabView(w.Root, w.Tips);
        var seen = new List<TaskFilter>();
        view.FilterChanged += (_, _) => seen.Add(view.Filter);
        view.Search.Text = "deck";
        view.ShowNotRunning.Checked = true;
        view.ShowMicrosoft.Checked = true;
        ((RadioButton)view.Segments.Controls[1]).Checked = true; // one segment picked, one left: raised once
        Assert.Equal([new TaskFilter(TaskSegment.All, "deck"), new TaskFilter(TaskSegment.All, "deck", true), new TaskFilter(TaskSegment.All, "deck", true, true),
            new TaskFilter(TaskSegment.StartupApps, "deck", true, true)], seen);
    }

    [Theory]
    [InlineData(1100, 750)]
    [InlineData(1024, 600)]
    [InlineData(760, 540)]
    public void TheAgentButtonsAndTurnOffSitAtTheRightOfTheirBars(int width, int height)
    {
        using var w = new Window(new Size(width, height));
        var toolbar = w.Services.Toolbar;
        int edge = toolbar.ClientSize.Width - toolbar.Padding.Right;
        Assert.InRange(edge - w.Services.AiButtons.Right, 0, w.Form.LogicalToDeviceUnits(12));
        Assert.Equal([w.Services.Agent, w.Services.ImportAnswer], w.Services.AiButtons.Controls.Cast<Control>());
        var bar = w.Services.ActionBar;
        Assert.InRange(bar.ClientSize.Width - bar.Padding.Right - w.Services.Actions.Right, 0, w.Form.LogicalToDeviceUnits(12));
        Assert.Same(w.Services.TurnOff, w.Services.Actions.Controls[^1]);   // the primary action is the right-most
        Assert.True(w.Services.PutBack.Right <= w.Services.TurnOff.Left);  // Put back before it, apart from it
        Assert.True(w.Services.Note.Left < w.Services.Actions.Left || w.Services.Note.Top < w.Services.Actions.Top);
        w.Show(2);
        var tools = w.Tasks.Toolbar;
        Assert.InRange(tools.ClientSize.Width - tools.Padding.Right - w.Tasks.Refresh.Right, 0, w.Form.LogicalToDeviceUnits(12));
    }

    [Fact]
    public void TheTabsAreTheDesignsAndTheirBarsTheirWords()
    {
        using var w = new Window(new Size(1100, 750));
        Assert.Equal(["Pool causes", "Services", "Tasks and startup apps"], w.Tabs.TabPages.Cast<TabPage>().Select(p => p.Text));
        Assert.Equal(("Find a service", "Show not running", "Show protected"), (w.Services.Search.PlaceholderText, w.Services.ShowNotRunning.Text, w.Services.ShowProtected.Text));
        Assert.False(w.Services.ShowNotRunning.Checked || w.Services.ShowProtected.Checked); // both off at first
        Assert.Equal(("Decide with your AI agent", "Import answer"), (w.Services.Agent.Text, w.Services.ImportAnswer.Text));
        Assert.Equal("Anything you turn off can be put back from Active changes.", w.Services.Note.Text);
        Assert.Equal(("Put back…", "Turn off 12 services"), (w.Services.PutBack.Text, w.Services.TurnOff.Text));
        Assert.Equal(["Put back the selected service", "Set the selected service to its Windows default", "Put all back"],
            w.Services.PutBackMenu.Items.OfType<ToolStripMenuItem>().Select(i => i.Text));
        Assert.Equal(["All", "Startup apps", "Scheduled tasks", "Busy in the background"], w.Tasks.Segments.Controls.Cast<Control>().Select(c => c.Text));
        Assert.Equal(("Find", "Show not running", "Show Microsoft"), (w.Tasks.Search.PlaceholderText, w.Tasks.ShowNotRunning.Text, w.Tasks.ShowMicrosoft.Text));
        Assert.False(w.Tasks.ShowNotRunning.Checked || w.Tasks.ShowMicrosoft.Checked);
        Assert.Equal(TaskSegment.All, w.Tasks.Segment);
        Assert.Equal("Anything you switch off can be put back from Active changes.", w.Tasks.Note.Text);
        Assert.Equal(["Name", "Kind", "Cost now", "Starts"], w.Tasks.List.Columns.Cast<ColumnHeader>().Select(c => c.Text));
        Assert.Equal(ProfileText.SaveTip, w.Tips.GetToolTip(w.Services.SaveProfile)); // it also changes a profile, applying at start included
        // The keyboard goes as the eye does: the toolbar, the list, then the bar or the panel under it.
        Assert.Equal((0, 1, 2), (w.Services.Toolbar.TabIndex, w.Services.Services.TabIndex, w.Services.ActionBar.TabIndex));
        Assert.Equal((0, 1, 2), (w.Tasks.Toolbar.TabIndex, w.Tasks.List.TabIndex, w.Tasks.Detail.TabIndex));
    }

    [Fact]
    public void TheButtonsAreStyledByWhatTheyDo()
    {
        if (SystemInformation.HighContrast) return; // then they keep Windows' own look, on purpose
        using var w = new Window(new Size(1100, 750));
        Assert.Equal(ListTabStyle.Kind.Primary, ListTabStyle.KindOf(w.Services.TurnOff));
        Assert.Equal(ListTabStyle.Accent, w.Services.TurnOff.BackColor);
        Assert.Equal(Color.White, w.Services.TurnOff.ForeColor);
        Assert.Equal(ListTabStyle.Kind.Accent, ListTabStyle.KindOf(w.Services.Agent));
        Assert.Equal(ListTabStyle.Kind.Plain, ListTabStyle.KindOf(w.Services.PutBack));
        Assert.NotEqual(w.Services.PutBack.BackColor, w.Services.TurnOff.BackColor); // not all flat grey
        Assert.Equal(ListTabStyle.Kind.Primary, ListTabStyle.KindOf(w.Tasks.Detail.Toggle));
        Assert.Equal(ListTabStyle.Kind.Danger, ListTabStyle.KindOf(w.Tasks.Detail.End));
        Assert.Equal(ListTabStyle.Danger, w.Tasks.Detail.End.ForeColor);
        // A disabled primary button is not left blue with grey text.
        w.Services.ShowButtons(ServicesPageText.Buttons(false, 0, 0, null, null));
        Assert.False(w.Services.TurnOff.Enabled);
        Assert.Equal(ListTabStyle.DisabledBack, w.Services.TurnOff.BackColor);
        Assert.Equal("Turn off", w.Services.TurnOff.Text);
    }

    [Fact]
    public void TheAgentButtonsRaiseTheirEventsAndNothingElse()
    {
        using var w = new Window(new Size(1100, 750));
        int prompt = 0, import = 0;
        w.Services.AgentPromptRequested += (_, _) => prompt++;
        w.Services.ImportAnswerRequested += (_, _) => import++;
        // As a click does (PerformClick needs the window on screen).
        void Click(Button b) => typeof(Button).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(b, [EventArgs.Empty]);
        Click(w.Services.Agent);
        Assert.Equal((1, 0), (prompt, import));
        Click(w.Services.ImportAnswer);
        Assert.Equal((1, 1), (prompt, import));
    }

    [Fact]
    public void TheFilterIsWhatTheToolbarSays()
    {
        using var w = new Window(new Size(1100, 750));
        Assert.Equal(ServiceFilter.Default, new ServicesTabView(w.Root, w.Tips).Filter);
        w.Services.Search.Text = "xbox";
        w.Services.ShowProtected.Checked = true;
        Assert.Equal(new ServiceFilter("xbox", false, true), w.Services.Filter);
        ((RadioButton)w.Tasks.Segments.Controls[2]).Checked = true;
        w.Tasks.ShowMicrosoft.Checked = true;
        Assert.Equal(new TaskFilter(TaskSegment.ScheduledTasks, "", false, true), w.Tasks.Filter);
        Assert.False(((RadioButton)w.Tasks.Segments.Controls[0]).Checked); // one segment at a time
    }

    [Fact]
    public void TheStatusLineShowsOnlyWhileThereIsSomethingToSay()
    {
        // The list starts under the profile line, and under the status line only while it has text.
        using var w = new Window(new Size(1100, 750));
        int Top() { w.Services.Page.PerformLayout(); return w.Services.Services.Top; }
        int without = Top();
        Assert.Equal(w.Services.Summary.Bottom, without);
        w.Services.Status.Text = "Reading services…";
        Assert.True(Top() > without);
        w.Services.Status.Text = "";
        Assert.Equal(without, Top());
    }

    [Fact]
    public void TheMainWindowGivesBothListTabsTheGraphsRowAndPoolCausesKeepsTheGraph()
    {
        var (graph, tabs) = MainForm.AdvancedRows(servicesTab: false);
        Assert.Equal((SizeType.Percent, 36f, SizeType.Percent, 64f), (graph.SizeType, graph.Height, tabs.SizeType, tabs.Height));
        (graph, tabs) = MainForm.AdvancedRows(servicesTab: true);
        Assert.Equal((SizeType.Absolute, 0f, SizeType.Percent, 100f), (graph.SizeType, graph.Height, tabs.SizeType, tabs.Height));
    }

    [Fact]
    public void TheProtectedGroupNamesEveryKindOfProtectedService()
    {
        Assert.Equal("Protected (security, Windows Update, input/audio, Hyper-V and WSL, core)", ServiceRows.ProtectedName);
        // As the gate has them: Hyper-V's and WSL's services are never offered.
        foreach (var s in new[] { "vmms", "vmcompute", "HvHost", "WSLService", "vmicheartbeat" })
            Assert.True(ServiceCatalogue.IsProtected(s, insiderEnrolled: false), s);
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
}
