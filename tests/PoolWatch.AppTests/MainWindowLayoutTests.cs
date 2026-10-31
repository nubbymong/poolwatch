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
            Tabs.TabPages.Add(new TabPage("Pool causes"));
            Advanced.Controls.Add(GraphRow, 0, 0);
            Advanced.Controls.Add(Tabs, 0, 1);
            Form.Controls.AddRange([Advanced, footer, Header]); // the fill first, then the docked edges
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

        public void Dispose() => Form.Dispose();
    }

    private static ClearRecord Clear(DateTime at, long freed) => new()
    {
        TimeUtc = at, Mode = ClearMode.Standby, Trigger = ClearTrigger.Schedule, Before = new PoolTotals((ulong)(10 * GB), 0, 0, 0),
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
