using PoolWatch.Core.Cache;
using PoolWatch.Core.Growth;
using PoolWatch.Core.Model;
using PoolWatch.Ui;
using Xunit.Abstractions;

namespace PoolWatch.AppTests;

/// <summary>
/// The main window's Advanced view on Pool causes, built from the controls the window uses and laid out by its own layout
/// code (<see cref="MainForm.LayoutHeader(Panel, PoolHeaderView, FlowLayoutPanel, IReadOnlyList{Button}, int, bool, int)"/>,
/// <see cref="MainForm.AdvancedRows"/>, <see cref="MainForm.FitGraphRow(TableLayoutPanel, Control, PoolChart, GrowthPanel, bool)"/>):
/// at the window's smallest size, on the smallest desktop PoolWatch fits and at its own size, the header stays one row
/// high (the smaller one where the full facts do not fit), the graph draws its plot, the stats beside it show every row,
/// and nothing overlaps. With the most the graph can show: the 1 h / 6 h / 24 h choice, Show RAM on (its legend's three)
/// and a clear in the window. Never shown.
/// </summary>
[Collection(nameof(WholeWindowLayout))] // lays windows out: one class at a time, after the parallel ones
public sealed class MainWindowLayoutTests(ITestOutputHelper output)
{
    private const long MB = 1L << 20, GB = 1L << 30;
    private static readonly DateTime Now = new DateTime(2026, 10, 30, 9, 3, 0, DateTimeKind.Local).ToUniversalTime();

    private sealed class Window : IDisposable
    {
        public readonly Form Form = new() { Font = new Font("Segoe UI", 9f), FormBorderStyle = FormBorderStyle.Sizable };
        public readonly Panel Header = new() { Dock = DockStyle.Top };
        public readonly PoolHeaderView Facts = new() { Dock = DockStyle.Fill };
        public readonly FlowLayoutPanel Buttons = new() { Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
        public readonly List<Button> Shown;
        public readonly TableLayoutPanel Advanced = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        public readonly Panel GraphRow = new() { Dock = DockStyle.Fill };
        public readonly PoolChart Chart = new() { Dock = DockStyle.Fill };
        public readonly GrowthPanel Stats = new() { Dock = DockStyle.Right };
        public readonly TabControl Tabs = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };
        /// <summary>The Simple view's place: the window's other fill, hidden while the Advanced view shows.</summary>
        public readonly Panel Simple = new() { Dock = DockStyle.Fill, Visible = false };

        public Window(Size size, string nextTitle)
        {
            int S(int logical) => Form.LogicalToDeviceUnits(logical);
            // The header: the facts, Clear now and Settings (Pool causes).
            Shown = [MainForm.HeaderButtonOf("Clear now", S), MainForm.HeaderButtonOf("Settings", S)];
            Buttons.Controls.AddRange([.. Shown]);
            Header.Controls.Add(Facts); // fill first, then the docked edge
            Header.Controls.Add(Buttons);
            var last = Now.AddMinutes(-4);
            var facts = HeaderText.Of(new TotalsNow(8601 * MB, TimeSpan.FromHours(2), 243 * MB, null), new PoolTotals(4710UL * (ulong)MB, 3891UL * (ulong)MB, 0, 0),
                new NextClear(NextClearKind.Scheduled, Now.AddMinutes(37), 8 * GB), [Clear(last, 914 * MB)], 1, Now);
            Facts.Show(facts with { NextTitle = nextTitle }, "", "");

            // The footer: at least what the window's takes (Active changes with its count, the Advanced view switch).
            var footer = new Panel { Dock = DockStyle.Bottom, Height = S(43) };

            // The Advanced view: the graph with its choice and the stats beside it, over the tabs.
            Advanced.Padding = new Padding(S(16), S(12), S(16), S(4));
            Advanced.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var (graph, tabs) = MainForm.AdvancedRows(servicesTab: false);
            Advanced.RowStyles.Add(graph);
            Advanced.RowStyles.Add(tabs);
            Stats.Width = S(250);
            GraphRow.Margin = new Padding(0, 0, 0, S(8));
            GraphRow.Controls.Add(Chart); // fill first, then the stats at the right
            GraphRow.Controls.Add(Stats);
            Chart.SpanOption = true;
            Chart.RamOption = true;
            Chart.ShowRam = true;
            Chart.Span = TimeSpan.FromHours(6);
            Chart.SetData(Enumerable.Range(0, 361).Select(i => new ChartPoint(Now.AddMinutes(i - 360), new PoolTotals((ulong)(5 * GB), (ulong)(4 * GB), 0, 0))).ToList());
            Chart.SetClears([new ChartClear(Now.AddHours(-2), 900 * MB, Automatic: true, Light: false, Done: true)]);
            Stats.Show(StatsWindow.Default, new GrowthWindow
            {
                Span = TimeSpan.FromHours(6), NowUtc = Now, Measured = TimeSpan.FromHours(6), Clears = 6, StartedUtc = Now.AddDays(-1),
                Total = new GrowthCounts(7600 * MB, 7300 * MB, 0),
            });
            Tabs.TabPages.Add(new TabPage("Pool causes") { Padding = new Padding(S(4)) }); // padded as the window's
            Advanced.Controls.Add(GraphRow, 0, 0);
            Advanced.Controls.Add(Tabs, 0, 1);
            Simple.Controls.Add(new Panel { Dock = DockStyle.Top, Height = S(120) }); // its tiles, over the rest
            Form.Controls.AddRange([Simple, Advanced, footer, Header]); // the fills first, then the docked edges
            _ = Form.Handle; // a native window, never shown
            Form.Size = size;
            Form.PerformLayout();
        }

        /// <summary>What the window does when its size or its header changes: the header first, then the graph's row.</summary>
        public void Lay()
        {
            MainForm.LayoutHeader(Header, Facts, Buttons, Shown, Form.ClientSize.Width, advanced: true, tab: 0);
            Form.PerformLayout();
            MainForm.FitGraphRow(Advanced, GraphRow, Chart, Stats, servicesRows: false);
            Advanced.PerformLayout();
            GraphRow.PerformLayout();
        }

        /// <summary>The view and tab shown (the window's IsAdvanced and _tabs.SelectedIndex); it opens on Pool causes.</summary>
        public bool IsAdvanced { get; private set; } = true;
        public int Tab { get; private set; }

        /// <summary>The header for the view and tab shown, as MainForm.HeaderHeight lays it out (Settings on Pool causes only).</summary>
        public void LayHeader()
        {
            bool settings = MainForm.SettingsShown(IsAdvanced, Tab);
            Shown[1].Visible = settings;
            MainForm.LayoutHeader(Header, Facts, Buttons, settings ? Shown : [Shown[0]], Form.ClientSize.Width, IsAdvanced, Tab);
        }

        /// <summary>The window's switch of view (SetView's change): the view, then the header.</summary>
        public void ToView(bool advanced)
        {
            IsAdvanced = advanced;
            Advanced.Visible = advanced;
            Simple.Visible = !advanced;
            LayHeader();
        }

        /// <summary>The window's switch of tab (its change): the Advanced view's rows (MainForm.LayoutAdvancedRows), then the header.</summary>
        public void ToTab(int tab)
        {
            bool services = tab >= 1, was = Tab >= 1;
            Tab = tab;
            if (services != was)
            {
                var (graph, tabs) = MainForm.AdvancedRows(services);
                Advanced.SuspendLayout();
                Advanced.RowStyles[0] = graph;
                Advanced.RowStyles[1] = tabs;
                GraphRow.Visible = !services;
                Advanced.ResumeLayout(true);
            }
            LayHeader();
        }

        /// <summary>The graph's row, as MainForm.FitGraphRow() fits it (in the Advanced view only).</summary>
        public void Fit()
        {
            if (IsAdvanced) MainForm.FitGraphRow(Advanced, GraphRow, Chart, Stats, servicesRows: Tab >= 1);
        }

        /// <summary>
        /// The room inside the Pool causes page (its padding taken off), which its split fills: the page takes the tab
        /// control's display area once the window shows, as the tab control gives it.
        /// </summary>
        public Size CausesArea()
        {
            var page = Tabs.TabPages[0];
            page.Bounds = Tabs.DisplayRectangle;
            return page.DisplayRectangle.Size;
        }

        public void Dispose() => Form.Dispose();
    }

    /// <summary>The room inside Pool causes' page in a window of <paramref name="size"/> (logical pixels) at 100%, laid out as the window lays it out.</summary>
    private static Size CausesAreaAt(Size size)
    {
        lock (Areas)
        {
            if (Areas.TryGetValue(size, out var known)) return known;
            using var w = new Window(size, "Next clear in 1 h 20 min or at 7.5 GB"); // the header's longer next clear
            w.Lay();
            return Areas[size] = w.CausesArea();
        }
    }

    private static readonly Dictionary<Size, Size> Areas = [];

    /// <summary>
    /// Pool causes' page as the window builds it (<see cref="MainForm.Legend(Func{int, int}, Control)"/>,
    /// <see cref="MainForm.CauseColumnWidths"/>, <see cref="CausesSplit"/>, <see cref="MainForm.LayDetails"/>,
    /// <see cref="MainForm.DetailActions"/>), at <paramref name="percent"/> as far as a test can scale it (its text and its
    /// sizes), with nine rows in the list and a row selected whose details show all three actions and the green line.
    /// It opens at <paramref name="opened"/> and is resized as the window is. Never shown.
    /// </summary>
    private sealed class CausesPage : IDisposable
    {
        public readonly Form Form;
        public readonly Panel Area = new();
        public readonly CausesSplit Split = new() { Dock = DockStyle.Fill };
        public readonly ListView List = new BufferedListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, OwnerDraw = true };
        public readonly WrapLabel Title, Handled;
        public readonly TextBox Words;
        public readonly Button[] Buttons;
        public readonly FlowLayoutPanel Actions;
        public readonly DetailsPane Details;
        public readonly Func<int, int> P;

        public CausesPage(int percent, Size opened, string title, string handled)
        {
            P = n => n * percent / 100;
            Form = new Form { Font = new Font("Segoe UI", 9f * percent / 100) };
            Area.Size = opened;
            Form.Controls.Add(Area);
            // The list under its legend, as MainForm.CausesPage builds them.
            List.DrawColumnHeader += (_, e) => e.DrawDefault = true;
            List.DrawSubItem += (_, e) => e.DrawDefault = true;
            var widths = MainForm.CauseColumnWidths(Form.Font, P);
            for (int i = 0; i < PoolCauses.Columns.Length; i++) List.Columns.Add(PoolCauses.Columns[i], widths[i]);
            for (int i = 0; i < 9; i++) List.Items.Add(new ListViewItem(["Pool tag Ntfc (ntfs.sys)", "+1 MB (now 5 MB)", "nothing", "+1 MB", "", "Leaker", "strong evidence", "", "since 07:19"]));
            string noteText = GrowthText.WindowNote(StatsWindow.All.MaxBy(w => GrowthText.WindowNote(w).Length)!); // the longest note
            var note = new PaintedLabel(centerVertically: true) { Text = noteText, Anchor = AnchorStyles.Right };
            note.Size = MainForm.CausesNoteSize(noteText, Form.Font, P);
            Split.Panel1.Controls.Add(List); // fill first, then the legend above it
            Split.Panel1.Controls.Add(MainForm.Legend(P, note));
            // The details: the title, the words, the green line, and the actions on the right.
            Title = new WrapLabel { Font = new Font("Segoe UI Semibold", 10f * percent / 100), Padding = new Padding(P(4), P(8), P(4), P(2)), Text = title };
            Handled = new WrapLabel { Font = new Font("Segoe UI", 9f * percent / 100), Padding = new Padding(P(4), P(6), P(4), P(4)), Text = handled };
            Words = new TextBox
            {
                ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 9.5f * percent / 100), Text = PoolCausesTests.LongWords,
            };
            Buttons = [new() { Text = "Open report folder", AutoSize = true }, new() { Text = "Copy summary", AutoSize = true }, new() { Text = "Export report...", AutoSize = true }];
            Actions = MainForm.DetailActions(P, Buttons);
            Split.Panel2.Padding = new Padding(P(4), 0, P(4), P(4));
            Details = MainForm.LayDetails(Split.Panel2, Title, Words, Handled, Actions);
            Split.SplitterWidth = P(6);
            Split.List = List;
            Split.DetailsLeast = () => Split.Panel2.Padding.Vertical + Details.TitleLine;
            Area.Controls.Add(Split);
            PoolCausesTests.CreateHandles(Form); // native windows, never shown
            Split.Fit(); // as the window's refresh does once the list has its rows
        }

        /// <summary>The page resized (the window resized): one layout of it, as a resize gives it.</summary>
        public void Resize(Size area) => Area.Size = area;

        /// <summary>The rows the list shows whole, as the list itself counts them (LVM_GETCOUNTPERPAGE).</summary>
        public int Rows() => (int)SendMessage(List.Handle, 0x1000 + 40, IntPtr.Zero, IntPtr.Zero);

        /// <summary>The splitter's distance at the list's share alone (as the split puts it after a resize).</summary>
        public int AtShare => (int)(Split.Height * Split.Share);

        public void Dispose() => Form.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Pool causes from the window's smallest size up (760 x 540 and taller, 4 px at a time, at 760, 1024, 1100 and 1280 px
    /// wide), at 100% and 150%, with the longest title and each long green line: the details stay whole and in order and
    /// scroll to all of it, and only where something would be hidden otherwise (<see cref="PoolCausesTests.DetailsProblems"/>:
    /// a scroll bar showed with nothing to hide at 1100 x 672, the window's first size on a 1280 x 720 display), and the
    /// list shows a whole row at every size here: the split at its share wherever that gives the list a row, else moved
    /// down exactly as far as one row needs, the details keeping at least their title's line. A CI ui run at the smallest
    /// size showed the list's header and no row, and the details' actions cut off.
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(150)]
    public void PoolCausesShowsAListRowAndItsWholeDetailsFromTheSmallestWindowUp(int percent)
    {
        int P(int n) => n * percent / 100;
        var opened = CausesAreaAt(new Size(1100, 750)); // the window's own size
        foreach (int width in new[] { 760, 1024, 1100, 1280 })
            foreach (string green in PoolCausesTests.LongGreenLines())
            {
                using var p = new CausesPage(percent, new Size(P(opened.Width), P(opened.Height)), PoolCausesTests.LongTitle, green);
                for (int height = 540; height <= 780; height += 4)
                {
                    var area = CausesAreaAt(new Size(width, height));
                    p.Resize(new Size(P(area.Width), P(area.Height)));
                    var split = p.Split;
                    int rows = p.Rows(), least = split.DetailsLeast!(), content = p.Details.Content.Height, pane = p.Details.ClientSize.Height;
                    string at = $"{width} x {height} at {percent}% (\"{green}\"): split {split.Height}, list {split.SplitterDistance} (share {p.AtShare}), " +
                                $"details {split.Panel2.Height} (least {least}), rows {rows}, content {content} in {pane}";
                    if (height == 540) output.WriteLine(at + $", legend {split.Panel1.Controls[1].Height}, list {p.List.Bounds} client {p.List.ClientSize}, row {p.List.Items[0].Bounds.Height}, scroll range {p.Details.DisplayRectangle.Height}");
                    var problems = PoolCausesTests.DetailsProblems(split.Panel2, p.Title, p.Words, p.Handled, p.Actions, p.Buttons, P);
                    Assert.True(problems.Count == 0, at + Environment.NewLine + string.Join(Environment.NewLine, problems));
                    Assert.True(split.Panel2.Height >= least, at + ": the details lose their title's line");
                    Assert.True(rows >= 1, at + ": the list shows no whole row"); // room for it here at every size, at both scalings
                    if (split.SplitterDistance != p.AtShare)
                        Assert.True(split.SplitterDistance > p.AtShare && rows == 1, at + ": the split left its share further than one row needs");
                }
            }
    }

    /// <summary>
    /// The list's share of the split stays the design's (0.58) as the window is resized: moved down for a row at the
    /// smallest size, it is back at its share at the window's own size. A move by the owner is the new share, kept through
    /// resizes. A SplitContainer takes any distance set in code as its ratio, so after the smallest size it would have kept
    /// the list larger. The rule itself: the share, or more for a row, as far as the details keep their least.
    /// </summary>
    [Fact]
    public void TheListKeepsItsShareOfTheSplitAndTheOwnersMove()
    {
        Assert.Equal(200, CausesSplit.At(from: 200, room: 300, listLeast: 150, detailsLeast: 40)); // the share already gives the row
        Assert.Equal(150, CausesSplit.At(from: 110, room: 300, listLeast: 150, detailsLeast: 40)); // moved down as far as the row needs
        Assert.Equal(260, CausesSplit.At(from: 110, room: 300, listLeast: 260, detailsLeast: 40)); // the details keep exactly their least
        Assert.Equal(110, CausesSplit.At(from: 110, room: 300, listLeast: 261, detailsLeast: 40)); // a px short of it: no row, so no move
        Assert.Equal(110, CausesSplit.At(from: 110, room: 120, listLeast: 150, detailsLeast: 40)); // never less than the share

        var own = CausesAreaAt(new Size(1100, 750));
        using var p = new CausesPage(100, own, PoolCausesTests.LongTitle, PoolCausesTests.LongGreenLines()[0]);
        Assert.Equal(0.58, p.Split.Share);
        Assert.Equal((int)(p.Split.Height * 0.58), p.Split.SplitterDistance);
        int opened = p.Split.SplitterDistance;
        p.Resize(CausesAreaAt(new Size(760, 540)));
        Assert.True(p.Split.SplitterDistance > p.AtShare, $"at the smallest size the list is {p.Split.SplitterDistance} px, its share {p.AtShare}");
        p.Resize(own);
        Assert.Equal(opened, p.Split.SplitterDistance);

        // The owner drags the splitter (SplitterMoving while dragging, then the move): kept through resizes.
        p.Split.OnSplitterMoving(new SplitterCancelEventArgs(0, 0, 0, 0));
        p.Split.SplitterDistance = opened - 60;
        Assert.Equal(opened - 60, p.Split.SplitterDistance);
        p.Resize(CausesAreaAt(new Size(1024, 720)));
        p.Resize(own);
        Assert.Equal(opened - 60, p.Split.SplitterDistance);
        // A distance set in code, with no drag: not the owner's share.
        p.Split.SplitterDistance = opened;
        p.Resize(CausesAreaAt(new Size(1024, 720)));
        p.Resize(own);
        Assert.Equal(opened - 60, p.Split.SplitterDistance);
        // A drag the owner gives up (no move) at the smallest size, then the window's own size: the moves of the resize are
        // the split's, not the owner's (taken as the owner's, the list would keep the share it was moved down to there).
        p.Resize(CausesAreaAt(new Size(760, 540)));
        p.Split.OnSplitterMoving(new SplitterCancelEventArgs(0, 0, 0, 0));
        p.Resize(own);
        Assert.Equal(opened - 60, p.Split.SplitterDistance);
        // At the smallest size the owner drags the list up past its one row: that stays when the window's refresh fits the
        // split again with nothing changed (only a resize, or new needs, move it again).
        p.Resize(CausesAreaAt(new Size(760, 540)));
        int raised = p.Split.SplitterDistance;
        Assert.Equal(1, p.Rows()); // moved down for its row from the owner's smaller share, which left the list no room at all
        p.Split.OnSplitterMoving(new SplitterCancelEventArgs(0, 0, 0, 0));
        p.Split.SplitterDistance = raised - 20;
        p.Split.Fit();
        Assert.Equal(raised - 20, p.Split.SplitterDistance);
    }

    /// <summary>
    /// The split moves down for the list's row as far as the details still show their title's first line whole, as the
    /// title draws it, which is less than the title's whole one-line height (its padding under the line scrolls). At the
    /// page height that leaves the details exactly their least, the list shows its row and every pixel of the title's line
    /// shows in the details; a pixel shorter, the list keeps its share. With visual styles on (as the window draws), at
    /// 940 to 1080 x 540 at 100% the list needs 120 px of a 160 px split: kept to the title's whole one-line height (35 px
    /// with the panel's padding), the details left 34 and the list showed no row.
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(150)]
    public void TheListGetsItsRowWhereTheDetailsStillShowTheirTitlesFirstLine(int percent)
    {
        int P(int n) => n * percent / 100;
        var smallest = CausesAreaAt(new Size(1024, 540));
        using var p = new CausesPage(percent, new Size(P(smallest.Width), P(smallest.Height)), "Pool tag Ntfc (ntfs.sys): +1 MB (now 5 MB)", PoolCausesTests.LongGreenLines()[0]);
        var split = p.Split;
        Assert.True(split.SplitterDistance > p.AtShare && p.Rows() == 1, $"at 1024 x 540 the list is {split.SplitterDistance} px (share {p.AtShare}) with {p.Rows()} rows: not moved for its row");
        int listLeast = split.SplitterDistance, least = split.DetailsLeast!();
        int whole = split.Panel2.Padding.Vertical + p.Details.Padding.Vertical + p.Title.HeightAt(short.MaxValue); // the title's whole one-line height
        p.Resize(new Size(P(smallest.Width), listLeast + split.SplitterWidth + least));
        var (_, ink) = PoolCausesTests.InkRows(p.Title);
        string at = $"at {percent}%: split {split.Height}, list {split.SplitterDistance} (needs {listLeast}, share {p.AtShare}), rows {p.Rows()}, details {split.Panel2.Height} " +
                    $"(least {least}, the title's whole line {whole}), pane {p.Details.ClientSize.Height}, the title's last painted row {p.Details.Content.Top + p.Title.Top + ink}";
        Assert.True((split.SplitterDistance, split.Panel2.Height, p.Rows()) == (listLeast, least, 1), "the list is not moved for its row " + at);
        Assert.True(least < whole, "the details keep the title's whole one-line height " + at);
        Assert.True(p.Details.Content.Top + p.Title.Top + ink < p.Details.ClientSize.Height, "the title's line is cut off " + at);
        var problems = PoolCausesTests.DetailsProblems(split.Panel2, p.Title, p.Words, p.Handled, p.Actions, p.Buttons, P);
        Assert.True(problems.Count == 0, at + Environment.NewLine + string.Join(Environment.NewLine, problems));
        p.Resize(new Size(P(smallest.Width), listLeast + split.SplitterWidth + least - 1));
        Assert.True(split.SplitterDistance == p.AtShare, "a pixel shorter, the list left its share " + at);
    }

    private static ClearRecord Clear(DateTime at, long freed) => new()
    {
        TimeUtc = at, TickMs = AppClocks.Ms(at), Mode = ClearMode.Standby, Trigger = ClearTrigger.Schedule, Before = new PoolTotals((ulong)(10 * GB), 0, 0, 0),
        After = new PoolTotals((ulong)(10 * GB - freed), 0, 0, 0),
    };

    private static void Paint(Control c)
    {
        using var bitmap = new Bitmap(Math.Max(1, c.Width), Math.Max(1, c.Height));
        c.DrawToBitmap(bitmap, new Rectangle(Point.Empty, c.Size));
    }

    [Theory]
    [InlineData(760, 540, "Next clear in 37 min or at 8 GB")]          // the window's smallest size (MainForm's MinimumSize at 100%)
    [InlineData(760, 540, "Next clear in 1 h 20 min or at 7.5 GB")]
    [InlineData(860, 560, "Next clear in 37 min or at 8 GB")]
    [InlineData(1024, 552, "Next clear in 37 min or at 8 GB")]         // the smallest desktop PoolWatch fits (1024 x 600, less the taskbar)
    [InlineData(1024, 552, "Next clear in 1 h 20 min or at 7.5 GB")]
    [InlineData(1100, 750, "Next clear in 37 min or at 8 GB")]         // the window's own size
    public void ThePlotAndEveryStatsRowKeepTheirRoomAtEverySize(int width, int height, string nextTitle)
    {
        using var w = new Window(new Size(width, height), nextTitle);
        w.Lay();
        Paint(w.Chart);
        Paint(w.Stats);
        Paint(w.Facts);
        var layout = w.Chart.LastLayout!;
        output.WriteLine($"{width} x {height}: header {w.Header.Height} (compact {w.Facts.Compact}), graph row {w.Advanced.GetRowHeights()[0]}, " +
                         $"tabs {w.Advanced.GetRowHeights()[1]}, chart {w.Chart.Size} needs {w.Chart.NeededHeight}, plot {layout.Plot}, " +
                         $"stats {w.Stats.Height} needs {w.Stats.NeededHeight} (rows {w.Stats.RowsHeight})");
        Assert.Empty(layout.Problems());
        Assert.True(layout.Plot.Height >= PoolChart.MinPlotLines * w.Chart.LineHeight, $"the plot is {layout.Plot}");
        Assert.InRange(w.Stats.RowsHeight, 1, w.Stats.Height);
        Assert.InRange(w.Stats.NeededHeight, 1, w.Stats.Height + 1);
        Assert.Empty(w.Facts.Problems());
        Assert.True(w.Facts.NeededHeight(w.Facts.Width) <= w.Facts.Height + 1, $"the header's facts need {w.Facts.NeededHeight(w.Facts.Width)} px, have {w.Facts.Height}");
        // The header stays one row high: the smaller one where the full facts do not fit beside the buttons.
        Assert.Equal(!w.Facts.FitsOneRow(w.Facts.Width, compact: false), w.Facts.Compact);
        Assert.True(w.Facts.FitsOneRow(w.Facts.Width, w.Facts.Compact), "the header's facts take two rows");
        // The tabs below keep room for the list and its details.
        Assert.True(w.Advanced.GetRowHeights()[1] >= w.Form.LogicalToDeviceUnits(180), $"the tabs have {w.Advanced.GetRowHeights()[1]} px");
    }

    [Fact]
    public void OnAWindowLargeEnoughTheGraphRowIsItsShareAndTheHeaderTheFullOne()
    {
        using var w = new Window(new Size(1100, 750), "Next clear in 37 min or at 8 GB");
        w.Lay();
        Assert.False(w.Facts.Compact);
        var (share, _) = MainForm.AdvancedRows(servicesTab: false);
        Assert.Equal((share.SizeType, share.Height), (w.Advanced.RowStyles[0].SizeType, w.Advanced.RowStyles[0].Height));
    }

    /// <summary>
    /// The window's switch from Pool causes to a Services tab: the tabs alone (no graph row) and the smaller header, which
    /// moves everything under it. Laid out once (<see cref="MainForm.LayOutOnce"/>), the view ends exactly where laying it
    /// out one change at a time put it, which laid it out twice (each pass moves and repaints the whole view once shown).
    /// </summary>
    [Fact]
    public void ASwitchToTheTabsAloneAndTheSmallerHeaderLaysTheViewOutOnce()
    {
        static void Switch(Window w)
        {
            w.Shown[1].Visible = false; // Settings shows on Pool causes only
            var (graph, tabs) = MainForm.AdvancedRows(servicesTab: true);
            w.Advanced.SuspendLayout(); // as MainForm.LayoutAdvancedRows changes the rows
            w.Advanced.RowStyles[0] = graph;
            w.Advanced.RowStyles[1] = tabs;
            w.GraphRow.Visible = false;
            w.Advanced.ResumeLayout(true);
            MainForm.LayoutHeader(w.Header, w.Facts, w.Buttons, [w.Shown[0]], w.Form.ClientSize.Width, advanced: true, tab: 1);
        }
        static void Fit(Window w) => MainForm.FitGraphRow(w.Advanced, w.GraphRow, w.Chart, w.Stats, servicesRows: true);

        using var once = new Window(new Size(1100, 750), "Next clear in 37 min or at 8 GB");
        using var stepwise = new Window(new Size(1100, 750), "Next clear in 37 min or at 8 GB");
        once.Lay();
        stepwise.Lay();
        Assert.False(once.Facts.Compact); // Pool causes on a window this size: the full header
        int full = once.Header.Height;
        int layoutsOnce = 0, layoutsStepwise = 0;
        once.Advanced.Layout += (_, _) => layoutsOnce++;
        stepwise.Advanced.Layout += (_, _) => layoutsStepwise++;

        MainForm.LayOutOnce(once.Form, once.Advanced, () => Switch(once), () => Fit(once));
        Switch(stepwise); // one change at a time, as the window did
        Fit(stepwise);

        output.WriteLine($"the view laid out {layoutsOnce} time(s) in one pass, {layoutsStepwise} one change at a time; header {full} -> {once.Header.Height} px");
        Assert.True(once.Facts.Compact);
        Assert.True(once.Header.Height < full, $"the smaller header is {once.Header.Height} px, the full one {full}");
        Assert.Equal(1, layoutsOnce);
        Assert.True(layoutsStepwise >= 2, $"one change at a time laid the view out {layoutsStepwise} times");
        Assert.Equal(stepwise.Header.Bounds, once.Header.Bounds);
        Assert.Equal(stepwise.Advanced.Bounds, once.Advanced.Bounds);
        Assert.Equal(stepwise.Advanced.GetRowHeights(), once.Advanced.GetRowHeights());
        Assert.Equal(stepwise.Tabs.Bounds, once.Tabs.Bounds);
        Assert.Equal(once.Header.Bottom, once.Advanced.Top); // under the smaller header, not where the full one ended
    }

    /// <summary>
    /// The window's switch from the Advanced view on a Services tab (the smaller header) to the Simple view (the full one):
    /// the Simple view shows and the header grows in the same pass of the window, so the Simple view lays out once. Laid
    /// out one change at a time, it laid out twice: when it showed, under the smaller header, then again for the full one.
    /// The Advanced view's own suspend does not cover this: only the window's does.
    /// </summary>
    [Fact]
    public void ASwitchToTheSimpleViewAndTheFullHeaderLaysTheViewOutOnce()
    {
        const string next = "Next clear in 37 min or at 8 GB";
        using var once = new Window(new Size(1100, 750), next);
        using var stepwise = new Window(new Size(1100, 750), next);
        foreach (var w in new[] { once, stepwise })
        {
            w.Lay();
            MainForm.LayOutOnce(w.Form, w.Advanced, () => w.ToTab(1), w.Fit); // the walk's last tab before the Simple view
        }
        int compact = once.Header.Height;
        int layoutsOnce = 0, layoutsStepwise = 0;
        once.Simple.Layout += (_, _) => layoutsOnce++;
        stepwise.Simple.Layout += (_, _) => layoutsStepwise++;

        MainForm.LayOutOnce(once.Form, once.Advanced, () => once.ToView(advanced: false), once.Fit);
        stepwise.ToView(advanced: false); // one change at a time, as the window did
        stepwise.Fit();

        output.WriteLine($"the Simple view laid out {layoutsOnce} time(s) in one pass, {layoutsStepwise} one change at a time; header {compact} -> {once.Header.Height} px");
        Assert.True(once.Header.Height > compact, $"the full header is {once.Header.Height} px, the smaller one {compact}");
        Assert.Equal(1, layoutsOnce);
        Assert.True(layoutsStepwise >= 2, $"one change at a time laid the Simple view out {layoutsStepwise} times");
        Assert.Equal(stepwise.Header.Bounds, once.Header.Bounds);
        Assert.Equal(stepwise.Simple.Bounds, once.Simple.Bounds);
        Assert.Equal(once.Header.Bottom, once.Simple.Top); // under the full header, not where the smaller one ended
    }

    /// <summary>
    /// Back on Pool causes after the window was resized while the Services tab (no graph) or the Simple view showed: the
    /// graph's row is fitted to the graph at the width the view gives it, in the view's one layout, as a window opened at
    /// that size has it. Fitted at the width the row kept from before the resize, it stayed wrong until the next sample.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BackOnPoolCausesAfterAResizeTheGraphRowFitsTheNewWidthInTheOneLayout(bool fromSimpleView)
    {
        const string next = "Next clear in 37 min or at 8 GB";
        var small = new Size(760, 540);
        using var w = new Window(new Size(1600, 750), next);
        w.Lay();
        // Away from Pool causes, then the window resized there: its Resize lays the header out, and no graph shows to fit.
        MainForm.LayOutOnce(w.Form, w.Advanced, () => { if (fromSimpleView) w.ToView(advanced: false); else w.ToTab(1); }, w.Fit);
        int widthBefore = w.Chart.Width;
        w.Form.Size = small;
        w.LayHeader();
        w.Fit();

        int layouts = 0, widthAtFit = 0;
        w.Advanced.Layout += (_, _) => layouts++;
        MainForm.LayOutOnce(w.Form, w.Advanced, () => { if (fromSimpleView) w.ToView(advanced: true); else w.ToTab(0); }, () =>
        {
            w.Fit();
            widthAtFit = w.Chart.Width;
        });
        using var opened = new Window(small, next);
        opened.Lay();

        static string Row(RowStyle r) => $"{r.SizeType} {r.Height}";
        output.WriteLine($"graph {widthBefore} px wide before the resize, {widthAtFit} at the fit, {w.Chart.Width} after; row {Row(w.Advanced.RowStyles[0])}, " +
                         $"a window opened at {small}: {Row(opened.Advanced.RowStyles[0])}; the view laid out {layouts} time(s)");
        Assert.Equal(1, layouts);
        Assert.Equal(w.Chart.Width, widthAtFit); // measured at the width the view's layout gave it
        Assert.Equal(opened.Chart.Width, w.Chart.Width);
        Assert.Equal(Row(opened.Advanced.RowStyles[0]), Row(w.Advanced.RowStyles[0]));
        Assert.Equal(opened.Advanced.GetRowHeights(), w.Advanced.GetRowHeights());
        Paint(w.Chart);
        Assert.Empty(w.Chart.LastLayout!.Problems());
        // The next sample fits the graph's row again (UpdateHeader): the row and its width are right, so nothing lays out.
        w.Fit();
        Assert.Equal(1, layouts);
    }

    /// <summary>
    /// The main window needs the whole app to build, so its source is read: its switch of view and of tab each lay the
    /// window out once, through <see cref="MainForm.LayOutOnce"/>, with the header in the same pass and the graph's row
    /// fitted after the window docks. The switch of view (<see cref="ViewSwitch"/>, whose test runs the order) gets
    /// LayOutOnce as its one layout; the view's parts (<c>ArrangeView</c>, run inside it) lay the header out there and
    /// refresh nothing, and the read after it (<c>ReadView</c>, the view's one refresh) lays out nothing again (that was
    /// a second pass for the header's height). ArrangeView's whole body is held here, as ReadView's is, and so is each of
    /// the window's own methods and properties it reaches (<c>LayoutAdvancedRows</c>, <c>HeaderHeight</c>,
    /// <c>ShownButtons</c>, <c>IsAdvanced</c>, <c>Px</c> and <c>S</c>), so any call added to any of them fails this test:
    /// a read of a page (RefreshCauses, a tab's Refresh, ReadSimple) or of the view (RefreshNow, RefreshVisible), and a
    /// call that leads to one or sets the graph's data (PumpRankings, SetData), among them. The window's other methods
    /// they call are static, as the tests that call them on the class need (AdvancedRows, SettingsShown, LayoutHeader and
    /// CompactHeader). Of what it sets, the Advanced view switch's change returns at once when the window sets it to the
    /// view the switch shows (the switch sets its view before it arranges the view, which the switch's test holds), and
    /// the graph's span, span choice and Show RAM raise no change when set from code (GrowthViewTests).
    /// </summary>
    [Fact]
    public void TheWindowLaysOutEachSwitchOfViewOrTabOnce()
    {
        string source = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Ui", "MainForm.cs"));
        static List<string> Lines(string block) => block.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var lines = Lines(source);
        Assert.Contains("_switch = new ViewSwitch(_tabs, change => LayOutOnce(this, _advanced, change, FitGraphRow), ArrangeView, ReadView, tabChanged: () =>", lines);
        Assert.Contains("private void SetView(bool advanced) => _switch.Show(advanced);", lines);
        string arrange = Block(source, "private void ArrangeView(bool advanced)"), read = Block(source, "private void ReadView()");
        Assert.Equal(
        [
            "if (advanced)",
            "{",
            "LayoutAdvancedRows();",
            "_chart.Margin = Px(0, 0, 12, 0);",
            "_graphRow.Controls.Add(_chart);",
            "_chart.BringToFront(); // the fill docks last, after the stats panel",
            "_chart.Span = _window.Span;",
            "_chart.SpanBox.SelectedIndex = StatsWindow.All.ToList().IndexOf(_window);",
            "_chart.SpanOption = true;",
            "_chart.RamOption = true;",
            "_chart.ShowRam = _context.Live.Running.ShowRam;",
            "}",
            "else",
            "{",
            "_simple.SetChart(_chart);",
            "_chart.Span = PoolChart.SimpleSpan;",
            "_chart.SpanOption = false;",
            "_chart.RamOption = false;",
            "}",
            "_advanced.Visible = advanced;",
            "_simple.Visible = !advanced;",
            "_advancedBox.Checked = advanced;",
            "HeaderHeight();",
        ], Lines(arrange));
        // The window's own methods and properties that ArrangeView reaches, each held whole (IsAdvanced below): a call
        // added to one of them fails here, as it does in ArrangeView.
        Assert.Equal(
        [
            "bool services = _tabs.SelectedIndex >= 1;",
            "if (services == _servicesRows) return;",
            "_servicesRows = services;",
            "var (graph, tabs) = AdvancedRows(services);",
            "_advanced.SuspendLayout();",
            "_advanced.RowStyles[0] = graph;",
            "_advanced.RowStyles[1] = tabs;",
            "_graphRow.Visible = !services;",
            "_advanced.ResumeLayout(true);",
        ], Lines(Block(source, "private void LayoutAdvancedRows()")));
        Assert.Equal(
        [
            "if (_buttons.Controls.Count == 0) return;",
            "bool settings = SettingsShown(IsAdvanced, _tabs.SelectedIndex);",
            "foreach (var b in _advancedButtons) if (b.Visible != settings) b.Visible = settings;",
            "LayoutHeader(_header, _headerView, _buttons, ShownButtons.ToList(), ClientSize.Width, IsAdvanced, _tabs.SelectedIndex);",
        ], Lines(Block(source, "private void HeaderHeight()")));
        Assert.Contains("private IEnumerable<Button> ShownButtons => _headerButtons.Where(b => !_advancedButtons.Contains(b) || SettingsShown(IsAdvanced, _tabs.SelectedIndex));", lines);
        Assert.Contains("private int S(int logical) => LogicalToDeviceUnits(logical);", lines);
        Assert.Contains("private Padding Px(int l, int t, int r, int b) => new(S(l), S(t), S(r), S(b));", lines);
        // Set to the view the switch shows, the Advanced view switch returns at once (no second switch, no read).
        Assert.Contains("private bool IsAdvanced => _switch.Advanced;", lines);
        Assert.Equal("if (_advancedBox.Checked == IsAdvanced) return; // set by the window itself", Lines(Block(source, "_advancedBox.CheckedChanged += (_, _) =>"))[0]);
        Assert.Equal(["_simpleRead = new(SimpleEvery);", "RefreshNow(force: true);"], Lines(read));
        string tab = Block(source, "_switch = new ViewSwitch(");
        Assert.Contains("LayOutOnce(this, _advanced, () => { LayoutAdvancedRows(); HeaderHeight(); }, FitGraphRow);", Lines(tab));
        Assert.DoesNotContain("LayoutHeader();", tab);
        Assert.DoesNotContain("_tabs.SelectedIndexChanged", source); // the tab's own change is the switch's (ViewSwitch)
    }

    /// <summary>
    /// A new sample lays the header out again only when a text it is measured from changed (<see cref="PoolHeaderView.Show"/>
    /// says so; HeaderTests pins what it says); otherwise only the graph's row is fitted, which follows the graph's own
    /// needs. A resize still lays the header out for its new width. Read from the window's source.
    /// </summary>
    [Fact]
    public void ANewSampleLaysTheHeaderOutOnlyWhenItsTextsChanged()
    {
        string source = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Ui", "MainForm.cs"));
        string update = Block(source, "private void UpdateHeader(TrayState state)");
        var lines = update.Split('\n').Select(l => l.Trim()).ToList();
        Assert.Contains("if (_headerView.Show(facts, s.Text, UiData.NextClearLine(_analysis))) LayoutHeader();", lines);
        Assert.Contains("else FitGraphRow();", lines);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(update, @"LayoutHeader\("));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(update, @"_headerView\.Show\("));
        Assert.DoesNotContain("HeaderHeight(", update);
        Assert.Contains("Resize += (_, _) => { UpdateTimer(); LayoutHeader(); };", source.Split('\n').Select(l => l.Trim()));
    }

    /// <summary>
    /// A change of display scaling lays the header out again once the window's own scaling (the base handler) is done, so
    /// it is measured with the new fonts: no sample does it unless a text changed. Read from the window's source, as no
    /// test can change a window's display scaling.
    /// </summary>
    [Fact]
    public void AChangeOfDisplayScalingLaysTheHeaderOutAfterTheWindowsOwnScaling()
    {
        string source = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Ui", "MainForm.cs"));
        var lines = Block(source, "protected override void OnDpiChanged(DpiChangedEventArgs e)").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
        Assert.Equal(["base.OnDpiChanged(e);", "LayoutHeader();"], lines);
    }

    /// <summary>
    /// The window's Pool causes split is wired as the tests' page wires it: it measures the window's list and keeps the
    /// details' title line, and the window's refresh fits it again once it has filled the list (the first rows come after
    /// the window is laid out, so without that a small window would show the list's header and no row until a resize).
    /// Read from the window's source, as the window needs the whole app to build.
    /// </summary>
    [Fact]
    public void TheWindowsSplitMeasuresItsListAndFitsWhenTheRowsCome()
    {
        string source = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Ui", "MainForm.cs"));
        var page = Block(source, "private TabPage CausesPage()").Split('\n').Select(l => l.Trim()).ToList();
        Assert.Contains("_causesSplit.List = _causes;", page);
        Assert.Contains("_causesSplit.DetailsLeast = () => _causesSplit.Panel2.Padding.Vertical + details.TitleLine;", page);
        var refresh = Block(source, "private void RefreshCauses(bool force)").Split('\n').Select(l => l.Trim()).ToList();
        int filled = refresh.IndexOf("Update(_causes, items, forceOrder: _sortChanged);");
        int fit = refresh.FindIndex(l => l.StartsWith("_causesSplit.Fit();", StringComparison.Ordinal));
        Assert.True(filled >= 0 && fit > filled, "RefreshCauses fits the split after it fills the list");
    }

    /// <summary>
    /// The window tells the details pane it lays out which row they show, before it fills them, so another row's details
    /// open at their top (<see cref="DetailsPane.ShowRow"/>), and so does each of the two texts without a row: its key
    /// says which one it is (<see cref="DetailsPane.KeyOf"/>), and the texts are filled with the same keys. Read from the
    /// window's source, as the window needs the whole app to build.
    /// </summary>
    [Fact]
    public void TheWindowsDetailsOpenAnotherRowAtTheirTop()
    {
        string source = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Ui", "MainForm.cs"));
        var page = Block(source, "private TabPage CausesPage()").Split('\n').Select(l => l.Trim()).ToList();
        int laid = page.FindIndex(l => l.StartsWith("var details = LayDetails(", StringComparison.Ordinal));
        Assert.True(laid >= 0 && page[laid + 1] == "_details = details;", "the window keeps the details pane it lays out");
        string block = Block(source, "private void ShowCauseDetails()");
        var show = block.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        Assert.Equal("var row = SelectedRow();", show[0]);
        Assert.StartsWith("_details?.ShowRow(DetailsPane.KeyOf(row?.Key, _causeRows.Count));", show[1], StringComparison.Ordinal);
        Assert.Contains("how sure PoolWatch is.\", DetailsPane.NothingListed);", block, StringComparison.Ordinal);
        Assert.Contains("SetText(_detailText, \"\", DetailsPane.NoneSelected);", block, StringComparison.Ordinal);
    }

    /// <summary>
    /// The page moved into a control of its own and made with its handles as showing makes them, under WinForms' hidden
    /// parking window, so nothing is shown (a pane never made does not scroll). Dispose it before the page.
    /// </summary>
    private static Control Made(CausesPage p)
    {
        var host = new Panel { Size = p.Area.Size, Font = p.Form.Font };
        p.Form.Controls.Remove(p.Area);
        host.Controls.Add(p.Area);
        host.CreateControl();
        return host;
    }

    /// <summary>As a key pressed on the pane does (it has no focus while it is not shown).</summary>
    private static void Press(Control c, Keys key) => On(c, "OnKeyDown", new KeyEventArgs(key));

    /// <summary>Raises one of the control's own events (<paramref name="name"/>: its On method), as Windows would.</summary>
    private static void On(Control c, string name, EventArgs e) =>
        c.GetType().GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, [e.GetType()])!.Invoke(c, [e]);

    private static bool IsInputKey(Control c, Keys key) =>
        (bool)typeof(Control).GetMethod("IsInputKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(c, [key])!;

    private static bool Selectable(Control c) =>
        (bool)typeof(Control).GetMethod("GetStyle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(c, [ControlStyles.Selectable])!;

    /// <summary>
    /// The controls Tab stops at, in order, under <paramref name="root"/> (as SelectNextControl walks them once the window
    /// shows: selectable tab stops, each parent before its children, children in their tab order).
    /// </summary>
    private static List<Control> TabStops(Control root)
    {
        var stops = new List<Control>();
        void Walk(Control parent)
        {
            for (var c = parent.GetNextControl(null, forward: true); c is not null && c.Parent == parent; c = parent.GetNextControl(c, forward: true))
            {
                if (c.TabStop && Selectable(c)) stops.Add(c);
                Walk(c);
            }
        }
        Walk(root);
        return stops;
    }

    /// <summary>
    /// In a small window the Pool causes details scroll, and the keyboard reaches all of them: the pane is a tab stop of
    /// its own, before the words and each action, named by its title for a screen reader; the arrow keys, Page Up, Page
    /// Down, Home and End scroll it while Tab still moves on; and Tab or Shift+Tab bringing the focus to it shows its top,
    /// the title, while the focus WinForms gives back when the window is active again keeps the place (it showed the top
    /// on every focus, so each Alt+Tab back lost where the keys had scrolled to). Before, Tab reached the words and the
    /// actions but the pane was no tab stop, so the title of details scrolled down could not be brought back with the
    /// keyboard.
    /// </summary>
    [Fact]
    public void TheDetailsPaneIsATabStopThatShowsItsTitleAndScrollsWithTheKeys()
    {
        var smallest = CausesAreaAt(new Size(760, 540));
        using var p = new CausesPage(100, smallest, PoolCausesTests.LongTitle, PoolCausesTests.LongGreenLines()[0]);
        using var host = Made(p); // made as showing makes it: a pane that is not made does not scroll
        var pane = p.Details;
        int Top() => -pane.AutoScrollPosition.Y;
        int end = pane.Content.Height - pane.ClientSize.Height, line = p.Words.Font.Height, page = pane.ClientSize.Height - line;
        Assert.True(end > line, $"at 760 x 540 the details ({pane.Content.Height} px) do not scroll in {pane.ClientSize.Height} px");

        Assert.True(pane.TabStop && Selectable(pane), "the details pane is no tab stop");
        Assert.Equal([pane, p.Words, .. p.Buttons], TabStops(p.Split.Panel2));
        Assert.Equal(PoolCausesTests.LongTitle, pane.AccessibleName);
        p.Title.Text = "Open files (File): +1 MB (now 5 MB)";
        Assert.Equal(p.Title.Text, pane.AccessibleName);

        Assert.All(new[] { Keys.Up, Keys.Down, Keys.PageUp, Keys.PageDown, Keys.Home, Keys.End }, k => Assert.True(IsInputKey(pane, k), $"{k} is not the pane's"));
        Assert.False(IsInputKey(pane, Keys.Tab)); // Tab still moves on to the words and the actions
        end = pane.Content.Height - pane.ClientSize.Height; // the shorter title
        Press(pane, Keys.Down);
        Assert.Equal(line, Top());
        Press(pane, Keys.Up);
        Assert.Equal(0, Top());
        Press(pane, Keys.Up); // already at the top
        Assert.Equal(0, Top());
        Press(pane, Keys.PageDown);
        Assert.Equal(Math.Min(end, page), Top());
        Press(pane, Keys.End);
        Assert.Equal(end, Top());
        Assert.Equal(pane.ClientSize.Height, pane.Content.Bottom); // the content's end in view
        Press(pane, Keys.Shift | Keys.Up); // not a key of the pane's
        Assert.Equal(end, Top());

        // Scrolled to its end: the focus given back as the window is active again (Alt+Tab back, a restore, a show from the
        // tray) keeps the place. Tab into the details, or Shift+Tab back from the words, shows the title: WinForms selects
        // the pane "directed" then, as here.
        On(pane, "OnGotFocus", EventArgs.Empty);
        Assert.Equal(end, Top());
        Assert.True(p.Split.Panel2.SelectNextControl(null, forward: true, tabStopOnly: true, nested: true, wrap: false)); // Tab
        Assert.Equal(0, Top());
        Assert.Equal(0, pane.Content.Top);
        Press(pane, Keys.End);
        Assert.True(p.Split.Panel2.SelectNextControl(p.Words, forward: false, tabStopOnly: true, nested: true, wrap: false)); // Shift+Tab
        Assert.Equal(0, Top());
        Press(pane, Keys.End);
        Press(pane, Keys.Home);
        Assert.Equal(0, Top());
    }

    /// <summary>
    /// The two texts the details show without a row, "Nothing stands out right now" (nothing is listed) and "Select a row
    /// to see its details" (none is selected), each open at their top like another row's: they have keys of their own,
    /// which no row's key can be (each has a kind and a colon). Both had none, so from one to the other the pane kept where
    /// it was scrolled, the new title out of view in a small window. The same key, refreshed, keeps its place.
    /// </summary>
    [Fact]
    public void TheTwoTextsWithoutARowEachOpenAtTheirTop()
    {
        string nothing = DetailsPane.KeyOf(null, rows: 0), none = DetailsPane.KeyOf(null, rows: 9);
        Assert.NotEqual(nothing, none);
        Assert.Equal("tag:52656666", DetailsPane.KeyOf("tag:52656666", rows: 9));
        Assert.All(new[] { nothing, none }, k => Assert.DoesNotContain(':', k));

        var smallest = CausesAreaAt(new Size(760, 540));
        using var p = new CausesPage(100, smallest, PoolCausesTests.LongTitle, PoolCausesTests.LongGreenLines()[0]);
        using var host = Made(p); // made as showing makes it: a pane that is not made does not scroll
        var pane = p.Details;
        int Top() => -pane.AutoScrollPosition.Y;
        pane.ShowRow(nothing);
        Press(pane, Keys.End);
        int end = Top();
        Assert.True(end > 0, "the details do not scroll at 760 x 540");
        pane.ShowRow(nothing); // the same text, refreshed: it stays where it was scrolled
        Assert.Equal(end, Top());
        pane.ShowRow(none);    // the other text: at its top
        Assert.Equal(0, Top());
        Press(pane, Keys.End);
        pane.ShowRow(nothing);
        Assert.Equal(0, Top());
        Press(pane, Keys.End);
        pane.ShowRow("tag:52656666"); // a row's details
        Assert.Equal(0, Top());
    }

    /// <summary>
    /// The user guide says when each of the details' actions shows, as the window shows them: Export report always, Copy
    /// summary only when the row has a summary or evidence to copy, Open report folder only once the row's report folder
    /// exists. It listed all three as always there. The window's rule (<see cref="MainForm.DetailActions"/>) is run here;
    /// that the window shows its two answers is read from its source (the window needs the whole app to build), and the
    /// guide's words from the guide.
    /// </summary>
    [Fact]
    public void TheGuideSaysWhenOpenReportFolderAndCopySummaryShow()
    {
        const string made = @"C:\ProgramData\PoolWatch\incidents\20261101-File", named = @"C:\ProgramData\PoolWatch\incidents\20261101-Toke";
        bool Exists(string folder) => folder == made;
        Assert.Equal((true, true), MainForm.DetailActions(true, hasSummary: true, null, made, Exists));
        Assert.Equal((true, false), MainForm.DetailActions(true, hasSummary: false, "evidence", named, Exists)); // named, not made yet
        Assert.Equal((false, false), MainForm.DetailActions(true, hasSummary: false, null, null, Exists));      // nothing to copy, no folder
        Assert.Equal((false, false), MainForm.DetailActions(false, hasSummary: true, "evidence", made, Exists)); // no row selected

        string root = TrayIconsTests.RepoRoot();
        string source = File.ReadAllText(Path.Combine(root, "src", "PoolWatch", "Ui", "MainForm.cs"));
        var show = Block(source, "private void ShowCauseDetails()").Split('\n').Select(l => l.Trim()).ToList();
        Assert.Contains("var (summary, folder) = DetailActions(row is not null, copy?.Summary is not null, copy?.Evidence, row?.Folder, Directory.Exists);", show);
        Assert.Contains("if (_copySummary.Visible != summary) _copySummary.Visible = summary;", show);
        Assert.Contains("if (_openFolder.Visible != folder) _openFolder.Visible = folder;", show);
        Assert.Contains("private readonly Button _exportReport = new() { Text = \"Export report...\", AutoSize = true };", source.Split('\n').Select(l => l.Trim()));

        string guide = string.Join(' ', File.ReadAllText(Path.Combine(root, "docs", "USER-GUIDE.md")).Split((char[])[' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("**Export report** is always there", guide, StringComparison.Ordinal);
        Assert.Contains("**Copy summary** shows when the row has a summary or evidence to copy", guide, StringComparison.Ordinal);
        Assert.Contains("**Open report folder** once the row's report folder exists", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("**Open report folder** and **Copy summary** work on it", guide, StringComparison.Ordinal);
    }

    /// <summary>
    /// Opening the Advanced view on a chosen tab (a card's "Open Services", the tray's way in) reads only that tab, once:
    /// from the Simple view the tab is picked inside the switch of view, before the view is arranged, and the switch's one
    /// read after its one layout reads it; the tab's own change, raised by that pick, does nothing. It switched the view
    /// first, which read the tab shown before (every time), and then picked the tab, which read the new one. In the
    /// Advanced view the tab's own change lays it out and reads it, as a click on the tab. The switch (<see cref="ViewSwitch"/>)
    /// is run here with real tabs, made as showing makes them (never shown), so picking one raises its change as on screen,
    /// and with steps that record what each sees; the window gives it its own steps and hands it ShowAdvanced, and the CI
    /// soak's walk opens its tabs through it too.
    /// </summary>
    [Fact]
    public void OpeningTheAdvancedViewOnATabReadsOnlyThatTab()
    {
        using var tabs = new TabControl { Size = new Size(400, 300) };
        foreach (var name in new[] { "Pool causes", "Services", "Tasks and startup apps" }) tabs.TabPages.Add(name);
        _ = tabs.Handle; // made, never shown: picking a tab raises its change, as on screen
        var log = new List<string>();
        bool inLayout = false;
        ViewSwitch? view = null;
        string Shown() => view!.Advanced ? $"tab {tabs.SelectedIndex}" : "the Simple view";
        view = new ViewSwitch(tabs,
            layOutOnce: change =>
            {
                inLayout = true;
                try { change(); }
                finally { inLayout = false; }
                log.Add("laid out");
            },
            arrange: advanced => log.Add($"arranged {(advanced ? "the Advanced view" : "the Simple view")}{(inLayout ? "" : " outside the layout")}, showing {Shown()}"),
            read: () => log.Add($"read {Shown()}"),
            tabChanged: () => log.Add($"the tab's change read {Shown()}"));
        List<string> Run(Action a)
        {
            log.Clear();
            a();
            return [.. log];
        }

        Assert.Equal(["arranged the Simple view, showing the Simple view", "laid out", "read the Simple view"], Run(() => view.Show(false)));
        // From the Simple view: the tab is picked before the view is arranged, and it is the only one read, once.
        Assert.Equal(["arranged the Advanced view, showing tab 1", "laid out", "read tab 1"], Run(() => view.ShowAdvanced(1)));
        // In the Advanced view: the tab's own change reads it, as a click on the tab does.
        Assert.Equal(["the tab's change read tab 2"], Run(() => view.ShowAdvanced(2)));
        Assert.Equal(["the tab's change read tab 0"], Run(() => tabs.SelectedIndex = 0));
        Assert.Empty(Run(() => view.ShowAdvanced(0))); // already there: nothing to read again
        // Back to the Simple view, then the Advanced view on a tab out of range (kept within the tabs) or on the tab it
        // already shows (nothing to pick), or with no tab (the one it showed last).
        Assert.Equal(["arranged the Simple view, showing the Simple view", "laid out", "read the Simple view"], Run(() => view.Show(false)));
        Assert.Equal(["arranged the Advanced view, showing tab 2", "laid out", "read tab 2"], Run(() => view.ShowAdvanced(9)));
        view.Show(false);
        Assert.Equal(["arranged the Advanced view, showing tab 0", "laid out", "read tab 0"], Run(() => view.ShowAdvanced(-3)));
        view.Show(false);
        Assert.Equal(["arranged the Advanced view, showing tab 0", "laid out", "read tab 0"], Run(() => view.Show(true, tab: 0)));
        view.Show(false);
        Assert.Equal(["arranged the Advanced view, showing tab 0", "laid out", "read tab 0"], Run(() => view.Show(true)));

        // The window builds the switch with its own steps and hands it ShowAdvanced, and the CI soak's walk opens its tabs
        // the same way (it switched the view, which read the tab shown before, then picked its page). The window needs
        // the whole app to build, so this is read from its source.
        string source = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Ui", "MainForm.cs"));
        static List<string> Lines(string block) => block.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        Assert.Equal(["_view?.Remember(true);", "_switch.ShowAdvanced(tab);"], Lines(Block(source, "public void ShowAdvanced(int tab)")));
        var walk = Lines(Block(source, "public void StartSoakCycle()"));
        int at = walk.IndexOf("if (page < 0) { SetView(false); return; }");
        Assert.True(at >= 0, "the soak's walk shows the Simple view for a page below 0");
        Assert.StartsWith("_switch.ShowAdvanced(page);", walk[at + 1], StringComparison.Ordinal);
        Assert.DoesNotContain("_tabs.SelectedIndex", string.Join('\n', walk));
    }

    /// <summary>A details pane with the focus or not, and focus cues or not, as the test sets them (no window that is never shown has the focus).</summary>
    private sealed class FocusedPane(WrapLabel title, Control words, WrapLabel handled, Control actions) : DetailsPane(title, words, handled, actions)
    {
        public bool HasFocus;
        public bool Cues;
        public override bool Focused => HasFocus;
        protected override bool ShowFocusCues => Cues;
    }

    /// <summary>
    /// The details' title is framed as the focus only while the pane has the focus and Windows shows focus cues (after the
    /// first Tab or Alt; a mouse user never sees the frame): the focus coming or going, and Windows starting or stopping
    /// the cues, each frame it or take the frame away. The pane's own events are raised as Windows raises them.
    /// </summary>
    [Fact]
    public void TheDetailsTitleIsFramedOnlyWithTheFocusAndFocusCues()
    {
        var title = new WrapLabel { Text = PoolCausesTests.LongTitle };
        using var pane = new FocusedPane(title, new TextBox(), new WrapLabel(), new FlowLayoutPanel());
        var cues = new UICuesEventArgs(UICues.ChangeFocus);

        pane.HasFocus = true;
        On(pane, "OnGotFocus", EventArgs.Empty); // a mouse click, cues hidden
        Assert.False(title.FocusCue, "the title is framed while Windows hides focus cues");
        pane.Cues = true;
        On(pane, "OnChangeUICues", cues);        // the first Tab or Alt
        Assert.True(title.FocusCue, "the title is not framed with the focus and focus cues");
        pane.HasFocus = false;
        On(pane, "OnLostFocus", EventArgs.Empty);
        Assert.False(title.FocusCue, "the title stays framed without the focus");
        pane.HasFocus = true;
        On(pane, "OnGotFocus", EventArgs.Empty);
        Assert.True(title.FocusCue);
        pane.Cues = false;
        On(pane, "OnChangeUICues", cues);        // Windows stops showing focus cues
        Assert.False(title.FocusCue, "the title stays framed once Windows hides focus cues");
    }

    /// <summary>The text between the braces of the block that follows <paramref name="header"/> in <paramref name="source"/> (its only one).</summary>
    internal static string Block(string source, string header)
    {
        int at = source.IndexOf(header, StringComparison.Ordinal);
        Assert.True(at >= 0 && source.IndexOf(header, at + 1, StringComparison.Ordinal) < 0, $"\"{header}\" is not in the source once");
        int open = source.IndexOf('{', at + header.Length), depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[(open + 1)..i];
        }
        throw new InvalidOperationException($"the block after \"{header}\" does not close");
    }

    [Fact]
    public void TheGraphRowIsItsShareUnlessThatIsLessThanTheGraphNeeds()
    {
        var (share, _) = MainForm.AdvancedRows(servicesTab: false);
        Assert.Equal((share.SizeType, share.Height), (MainForm.GraphRow(500, 150).SizeType, MainForm.GraphRow(500, 150).Height)); // 36% of 500 is 180
        Assert.Equal((SizeType.Absolute, 150f), (MainForm.GraphRow(300, 150).SizeType, MainForm.GraphRow(300, 150).Height));     // 108 would cut the plot
        Assert.Equal((SizeType.Absolute, 100f), (MainForm.GraphRow(100, 150).SizeType, MainForm.GraphRow(100, 150).Height));     // never more than there is
    }

    [Fact]
    public void AGraphWithNoRoomForItsPlotIsALayoutProblem()
    {
        using var host = new Form();
        var chart = new PoolChart { Size = new Size(600, 70) };
        host.Controls.Add(chart);
        Paint(chart);
        Assert.Empty(chart.LastLayout!.Problems()); // nothing to draw yet: "Collecting the first samples"
        chart.SetData(Enumerable.Range(0, 61).Select(i => new ChartPoint(Now.AddMinutes(i - 60), new PoolTotals((ulong)(5 * GB), (ulong)(4 * GB), 0, 0))).ToList());
        Paint(chart);
        Assert.Contains(chart.LastLayout!.Problems(), p => p.StartsWith("the graph has no room for its plot", StringComparison.Ordinal));
        Assert.True(chart.NeededHeight > chart.Height);
        chart.Height = chart.NeededHeight;
        Paint(chart);
        Assert.Empty(chart.LastLayout!.Problems());
        Assert.Equal(PoolChart.MinPlotLines * chart.LineHeight, chart.LastLayout.Plot.Height);
    }
}
