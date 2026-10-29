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
/// its sizes); and the same when the services are read after the tab shows (the bottom bar's buttons grow then) and
/// after the window is resized, with nothing inside a page laid out again by hand, as nothing is in the window. Never
/// shown: the tabs sit in a panel of the window's client size, which no desktop cuts.
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

        private readonly int _percent;

        /// <param name="size">The window's size in logical pixels (its frame and title bar taken off for its client area).</param>
        /// <param name="percent">The display scaling, as far as a test can make it: the text and the sizes.</param>
        /// <param name="detail">The row the detail panel shows (a short command by default).</param>
        /// <param name="read">The services are read before the tab shows (<see cref="Read"/>); else the tab shows first, as
        /// the window shows it while it reads them: nothing counted yet, the buttons waiting.</param>
        /// <param name="note">The bottom bar's green line, when a test gives it a longer one.</param>
        public Window(Size size, int percent = 100, bool servicesTab = true, TaskRow? detail = null, bool read = true, string? note = null)
        {
            _percent = percent;
            Root = new Panel { Size = Scaled(size), Font = new Font("Segoe UI", 9f * percent / 100) };
            Form.Controls.Add(Root);
            Services = new ServicesTabView(Root, Tips);
            Tasks = new TasksTabView(Root, Tips);
            if (note is not null) Services.Note.Text = note;
            int P(int n) => n * percent / 100;
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
            if (read) Read();
            else
            {
                Services.Status.Text = "Reading services…";
                Services.ShowButtons(ServicesPageText.Buttons(true, 0, 0, null, null));
            }
            Tasks.List.RowOf = _ => null;
            for (int i = 0; i < 30; i++) Tasks.List.Items.Add(new ListViewItem(["Startup app " + i, "Startup app", "Low: 0.2 core", "at sign-in"]));
            Tasks.Detail.Display(TaskRows.Detail(detail ?? TasksTabTests.StreamDeck()));
            Show(1);
        }

        /// <summary>The window's client area for <paramref name="size"/> in logical pixels, at the test's scaling.</summary>
        private Size Scaled(Size size) => new((size.Width - 16) * _percent / 100, (size.Height - 39) * _percent / 100);

        /// <summary>
        /// The services read, in the order the Services tab shows them when its read ends: the toolbar's filter is already
        /// in place, the rows arrive (the count line follows them), the status line empties and the buttons are for 12
        /// ticked; then the count line as long as a PC with 337 services makes it. Only what these changes ask for lays
        /// the page out, as in the window.
        /// </summary>
        public void Read()
        {
            Services.Services.Filter = new ServiceFilter(ShowNotRunning: true);
            Services.Services.SetRows(Enumerable.Range(0, 3).SelectMany(_ => SomeServices.Rows()).ToList());
            Services.Status.Text = "";
            Services.ShowButtons(ServicesPageText.Buttons(false, 12, 3, null, null));
            Services.Shown.Text = ServiceRows.ShownText(36, 337);
        }

        /// <summary>
        /// The window resized to <paramref name="size"/> (logical pixels) while <paramref name="tab"/> shows: its client
        /// area takes the new size and the tab control gives its page the new room, and nothing else lays it out by hand.
        /// </summary>
        public void Resize(Size size, int tab)
        {
            Root.Size = Scaled(size);
            Tabs.TabPages[tab].Bounds = Tabs.DisplayRectangle;
        }

        /// <summary>
        /// Picks a tab and lays it out as the tab control gives its page room once the window shows. Nothing inside the
        /// page is laid out again by hand: a bar that needed that to come right would be wrong in the window.
        /// </summary>
        public void Show(int tab)
        {
            Tabs.SelectedIndex = tab;
            var page = Tabs.TabPages[tab];
            page.Bounds = Tabs.DisplayRectangle;
            foreach (Control c in page.Controls) _ = c.Handle;
            Root.PerformLayout();
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

    /// <summary>A green line a good deal longer than the tab's own (as a longer wording would be).</summary>
    private const string LongNote = "Anything you turn off here can be put back later from Active changes, one at a time or all together.";

    public static IEnumerable<object[]> ReadSizes() =>
    [
        [1024, 720, 100, false], // the CI's window
        [1100, 750, 100, false], // the default size
        [1024, 600, 100, false], // the smallest desktop PoolWatch fits
        [760, 540, 100, false],  // the window's minimum size
        [1024, 600, 150, false],
        [760, 540, 150, false],
        [1024, 720, 100, true],
        [760, 540, 100, true],
        [760, 540, 150, true],
    ];

    [Theory]
    [MemberData(nameof(ReadSizes))]
    public void TheBottomBarsButtonsStayWholeWhenTheServicesAreReadAfterTheTabShows(int width, int height, int percent, bool longNote)
    {
        // As the window has it: the tab shows while the services are read, and the read ends later. The count line and
        // Turn off's text grow then, the bar's group of buttons with them; nothing lays the page out again by hand.
        using var w = new Window(new Size(width, height), percent, read: false, note: longNote ? LongNote : null);
        var v = w.Services;
        Assert.Equal("", v.Shown.Text);
        w.Read();
        Assert.Equal((ServiceRows.ShownText(36, 337), "Turn off 12 services"), (v.Shown.Text, v.TurnOff.Text));
        var problems = new List<string>();
        MainForm.ClippedControls(v.Page, problems, WindowFitTests.OwnVisible);
        problems.AddRange(CutOff(v.Page, v.Note, v.Shown, v.Refresh, v.PutBack, v.TurnOff, v.Agent, v.ImportAnswer));
        problems.AddRange(Unfitted(v.ActionBar, v.Toolbar));
        output.WriteLine($"{width} x {height} at {percent}%{(longNote ? ", a long note" : "")}: bar {v.ActionBar.Bounds}, buttons {v.Actions.Bounds} on a {v.Page.ClientSize} page");
        foreach (var problem in problems) output.WriteLine(problem);
        Assert.Empty(problems);
        // And the buttons still sit at the right end of the bar.
        Assert.InRange(v.ActionBar.ClientSize.Width - v.ActionBar.Padding.Right - v.Actions.Right, 0, w.Form.LogicalToDeviceUnits(12));
    }

    public static IEnumerable<object[]> Resizes() =>
    [
        [1100, 750, 760, 540, 100],  // dragged down to the minimum size
        [760, 540, 1100, 750, 100],  // and back up
        [1100, 750, 1024, 720, 100], // to the CI's window
        [1024, 720, 760, 720, 100],  // only narrower
        [1024, 600, 760, 540, 150],
        [760, 540, 1024, 600, 150],
    ];

    [Theory]
    [MemberData(nameof(Resizes))]
    public void BothTabsKeepEverythingWholeWhenTheWindowIsResized(int width, int height, int toWidth, int toHeight, int percent)
    {
        // A window dragged to another size: each tab is given its new room, and nothing lays it out again by hand.
        foreach (int tab in new[] { 1, 2 })
        {
            using var w = new Window(new Size(width, height), percent);
            w.Show(tab);
            w.Resize(new Size(toWidth, toHeight), tab);
            var (s, t) = (w.Services, w.Tasks);
            var problems = new List<string>();
            if (tab == 1)
            {
                MainForm.ClippedControls(s.Page, problems, WindowFitTests.OwnVisible);
                problems.AddRange(CutOff(s.Page, s.Note, s.Shown, s.Refresh, s.PutBack, s.TurnOff, s.Agent, s.ImportAnswer));
                problems.AddRange(Unfitted(s.ActionBar, s.Toolbar));
            }
            else
            {
                MainForm.ClippedControls(t.Page, problems, WindowFitTests.OwnVisible);
                problems.AddRange(CutOff(t.Page, t.Refresh, t.Note, t.Detail.Toggle, t.Detail.End, t.Detail.Open, t.Detail.Command));
                problems.AddRange(Unfitted(t.Toolbar, t.NoteBar));
                if (t.Detail.Height != t.Detail.NeededHeight) problems.Add($"the detail panel needs {t.Detail.NeededHeight} px, has {t.Detail.Height}");
                // Docked from the bottom up: the note's bar, the detail panel, the gap, then the list.
                if (t.Detail.Bottom != t.NoteBar.Top || t.Gap.Bottom != t.Detail.Top)
                    problems.Add($"the detail panel {t.Detail.Bounds} is not between the gap {t.Gap.Bounds} and the note's bar {t.NoteBar.Bounds}");
            }
            output.WriteLine($"tab {tab}: {width} x {height} to {toWidth} x {toHeight} at {percent}%");
            foreach (var problem in problems) output.WriteLine(problem);
            Assert.Empty(problems);
        }
    }

    /// <summary>Resizes that give the detail panel another width: both ways between the default and the minimum size, only narrower, and both ways at 150%.</summary>
    public static IEnumerable<object[]> WidthChanges() =>
    [
        [1100, 750, 760, 540, 100],
        [760, 540, 1100, 750, 100],
        [1024, 720, 760, 720, 100],
        [1024, 600, 760, 540, 150],
        [760, 540, 1024, 600, 150],
    ];

    [Theory]
    [MemberData(nameof(WidthChanges))]
    public void TheDetailPanelHasItsHeightForANewWidthInThePagesFirstLayout(int width, int height, int toWidth, int toHeight, int percent)
    {
        // The page docks the panel at the height GetPreferredSize gives for the width it docks it at, so a new width and
        // the height for it come in one layout of the page. The page places the list last (it fills what the docked edges
        // leave), so when the list moves or takes its new size in that first layout, the panel must already lie between
        // the gap and the note's bar at the height it needs. A panel docked at its old height that then sets its own
        // height keeps its top, and runs under the note's bar or leaves a gap above it. Its AutoSize asks the page for
        // another layout, which puts it right, so the end state of a resize cannot tell the two apart; this checks the
        // first layout itself. The row's name and who it is from fit on one line in the wider panel and take two in the
        // narrower one, so the panel needs another height at its new width.
        var deck = TasksTabTests.StreamDeck();
        var row = deck with { Entry = deck.Entry with { Name = "Example Deck Companion and Profile Switcher", Publisher = "Example Vendor Software Limited" } };
        using var w = new Window(new Size(width, height), percent, detail: row);
        w.Show(2);
        var t = w.Tasks;
        int before = t.Detail.Height;
        int layouts = 0;
        (int Layout, Size Page, Rectangle Panel, int Needs, int GapBottom, int NoteTop)? placed = null;
        void ListPlaced(object? sender, EventArgs e) =>
            placed ??= (layouts, t.Page.ClientSize, t.Detail.Bounds, t.Detail.NeededHeight, t.Gap.Bottom, t.NoteBar.Top);
        t.Page.Layout += (_, _) => layouts++;
        t.List.Move += ListPlaced;
        t.List.Resize += ListPlaced;
        w.Resize(new Size(toWidth, toHeight), 2);
        output.WriteLine($"{width} x {height} to {toWidth} x {toHeight} at {percent}%: panel {before} px high; when the page placed the list {placed}; " +
            $"after {layouts} layout(s) of the page, panel {t.Detail.Bounds}, gap {t.Gap.Bounds}, note's bar {t.NoteBar.Bounds}");
        Assert.True(placed is not null, "the page never placed the list");
        var p = placed.Value;
        Assert.Equal((1, t.Page.ClientSize), (p.Layout, p.Page)); // the page's first layout, at its new size
        Assert.NotEqual(before, p.Needs); // else a height left from the old width would look right
        Assert.True(p.Panel.Height == p.Needs && p.Panel.Bottom == p.NoteTop && p.Panel.Top == p.GapBottom,
            $"in the page's first layout the panel {p.Panel} (it needs {p.Needs} px) is not between the gap (bottom {p.GapBottom}) and the note's bar (top {p.NoteTop})");
    }

    /// <summary>Every resize above: the six with the default row, and the five that give the panel another height with the long name.</summary>
    public static IEnumerable<object[]> EveryResize() =>
        Resizes().Select(r => r.Append(false).ToArray()).Concat(WidthChanges().Select(r => r.Append(true).ToArray()));

    [Theory]
    [MemberData(nameof(EveryResize))]
    public void AfterAResizeNothingOnTheTasksPageMovesAfterThePagesFirstLayout(int width, int height, int toWidth, int toHeight, int percent, bool longName)
    {
        // The strict form of the test above. The page docks the panel at its final height in its first layout at the new
        // size, and laying the panel out again gives the same height (its buttons keep their width), so no later layout
        // moves the panel, the gap, the note's bar or the list: the first layout puts the panel between the gap and the
        // note's bar at the height it needs, and the window shows it there.
        var deck = TasksTabTests.StreamDeck();
        var row = longName ? deck with { Entry = deck.Entry with { Name = "Example Deck Companion and Profile Switcher", Publisher = "Example Vendor Software Limited" } } : deck;
        using var w = new Window(new Size(width, height), percent, detail: row);
        w.Show(2);
        var t = w.Tasks;
        int layouts = 0;
        (int Layout, Rectangle Panel, Rectangle Gap, Rectangle Note, Rectangle List)? placed = null;
        void ListPlaced(object? sender, EventArgs e) => placed ??= (layouts, t.Detail.Bounds, t.Gap.Bounds, t.NoteBar.Bounds, t.List.Bounds);
        t.Page.Layout += (_, _) => layouts++;
        t.List.Move += ListPlaced;
        t.List.Resize += ListPlaced;
        w.Resize(new Size(toWidth, toHeight), 2);
        output.WriteLine($"{width} x {height} to {toWidth} x {toHeight} at {percent}%{(longName ? ", the long name" : "")}: when the page placed the list {placed}; " +
            $"after {layouts} layout(s) of the page, panel {t.Detail.Bounds} (needs {t.Detail.NeededHeight}), gap {t.Gap.Bounds}, note's bar {t.NoteBar.Bounds}, list {t.List.Bounds}");
        Assert.True(placed is not null, "the page never placed the list");
        var p = placed.Value;
        Assert.Equal(1, p.Layout);
        Assert.Equal((p.Panel, p.Gap, p.Note, p.List), (t.Detail.Bounds, t.Gap.Bounds, t.NoteBar.Bounds, t.List.Bounds));
        Assert.True(t.Detail.Height == t.Detail.NeededHeight && t.Detail.Bottom == t.NoteBar.Top && t.Detail.Top == t.Gap.Bottom,
            $"the panel {t.Detail.Bounds} (it needs {t.Detail.NeededHeight} px) is not between the gap {t.Gap.Bounds} and the note's bar {t.NoteBar.Bounds}");
    }

    /// <summary>The sizes the next test clicks through rows at: the default size, the CI's window and the minimum size at 100%; the smallest desktop and the minimum size at 150%.</summary>
    public static IEnumerable<object[]> ClickSizes() =>
    [
        [1100, 750, 100],
        [1024, 720, 100],
        [760, 540, 100],
        [1024, 600, 150],
        [760, 540, 150],
    ];

    [Theory]
    [MemberData(nameof(ClickSizes))]
    public void TheDetailPanelsButtonsAndTheListsRowsHoldOverTwentySelectionsAndReads(int width, int height, int percent)
    {
        // As the tab does it (TasksTab.ShowDetail): a selection change shows the row's details, and a read of the list shows
        // them again while it runs (the switch and End it now wait) and once it ends. Each lays the panel out. Its buttons
        // are as wide as their widest words need, so one row looks the same however often it is shown: the same buttons,
        // the same panel and the same rows of the list.
        var deck = TasksTabTests.StreamDeck();
        var off = deck with { Entry = deck.Entry with { Key = "StartupApp:Example Tray", Name = "Example Tray", Enabled = false } };
        TaskDetail[] details = [TaskRows.Detail(deck), TaskRows.Detail(off)];
        Assert.NotEqual(details[0].Toggle, details[1].Toggle); // two switches: "Don't start at sign-in", "Start at sign-in again"
        using var w = new Window(new Size(width, height), percent, detail: deck);
        w.Show(2);
        var (t, d) = (w.Tasks, w.Tasks.Detail);
        int S(int logical) => d.LogicalToDeviceUnits(logical); // as the panel scales its own sizes
        var first = new Dictionary<int, (int Button, int Rows, Rectangle Panel, string When)>();
        void Shown(int row, string when)
        {
            int button = d.Toggle.Width, rows = Rows(t.List);
            // What the widest words of the three buttons need, measured as the panel's buttons draw them.
            int words = new[] { d.Toggle, d.End, d.Open }.Max(b => TextRenderer.MeasureText(b.Text, b.Font).Width);
            output.WriteLine($"{when}: buttons {d.Toggle.Bounds}, {d.End.Bounds}, {d.Open.Bounds} (words {words} px); panel {d.Bounds}; the list {rows} rows");
            Assert.Equal((button, button), (d.End.Width, d.Open.Width));
            Assert.InRange(button, words + S(16), words + S(16) + S(24)); // the words, the panel's 16 px and the button's own frame
            Assert.True(d.Toggle.Left > d.Explain.Right && d.Toggle.Right <= d.ClientSize.Width, $"{when}: the buttons {d.Toggle.Bounds} are not right of the text {d.Explain.Bounds} in the panel {d.ClientSize}");
            if (first.TryGetValue(row, out var was))
                Assert.True((was.Button, was.Rows, was.Panel) == (button, rows, d.Bounds),
                    $"{when}: buttons {button} px, the list {rows} rows, panel {d.Bounds}; {was.When}: buttons {was.Button} px, the list {was.Rows} rows, panel {was.Panel}");
            else first[row] = (button, rows, d.Bounds, when);
        }
        Shown(0, "the tab shown");
        for (int i = 1; i <= 20; i++)
        {
            int row = i % 2;
            t.List.Items[row].Selected = true; // as a click selects it
            d.Display(details[row]);
            Shown(row, $"selection {i}");
            d.Display(details[row], busy: true); // Refresh, or after a switch: the list is read again
            Shown(row, $"selection {i}, a read running");
            d.Display(details[row]);
            Shown(row, $"selection {i}, the read ended");
        }
    }

    /// <summary>
    /// Each of <paramref name="controls"/> (that shows) cut off: it must lie inside every container it sits in, up to the
    /// page's client area, as a container shows only what is inside its own.
    /// </summary>
    internal static IEnumerable<string> CutOff(Control page, params Control[] controls)
    {
        foreach (var c in controls)
        {
            bool shown = true;
            for (var p = c; p is not null && p != page; p = p.Parent) shown &= WindowFitTests.OwnVisible(p);
            if (!shown) continue;
            var r = c.Bounds;
            for (var p = c.Parent; p is not null; p = p.Parent)
            {
                if (r.Left < 0 || r.Top < 0 || r.Right > p.ClientSize.Width || r.Bottom > p.ClientSize.Height)
                {
                    yield return $"{c.GetType().Name} \"{c.Text}\" is cut off: {r} in {p.GetType().Name} \"{p.Text}\" ({p.ClientSize})";
                    break;
                }
                if (p == page) break;
                r.Offset(p.Location); // the containers here have no border: their client area starts at their corner
            }
        }
    }

    /// <summary>Each wrapping bar not exactly as tall as the lines it laid its controls out on (cut off, or blank below them).</summary>
    internal static IEnumerable<string> Unfitted(params FlowLayoutPanel[] bars)
    {
        foreach (var bar in bars)
        {
            var shown = bar.Controls.Cast<Control>().Where(WindowFitTests.OwnVisible).ToList();
            if (shown.Count == 0) continue;
            int need = shown.Max(c => c.Bottom + c.Margin.Bottom) + bar.Padding.Bottom;
            if (Math.Abs(need - bar.ClientSize.Height) > 1) yield return $"a bar's lines need {need} px, it has {bar.ClientSize.Height} ({string.Join(", ", shown.Select(c => $"{c.GetType().Name} {c.Bounds}"))})";
        }
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
