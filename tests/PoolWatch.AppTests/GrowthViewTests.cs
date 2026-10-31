using System.ComponentModel;
using System.Globalization;
using PoolWatch.Core;
using PoolWatch.Core.Growth;
using PoolWatch.Core.Model;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// The growth and clear stats on screen: the Simple view's tiles, the Advanced view's stats beside the graph, the graph
/// that follows its 1 h / 6 h / 24 h choice (always over the whole window, the time before PoolWatch started a labelled
/// band, broken where nothing was sampled), and Show RAM. The controls are painted off screen and never shown.
/// </summary>
[Collection(nameof(WholeWindowLayout))] // lays windows out: one class at a time, after the parallel ones
public class GrowthViewTests
{
    private const long MB = 1L << 20, GB = 1L << 30;
    /// <summary>Local 15:20, as the mockups' newest time.</summary>
    private static readonly DateTime End = new DateTime(2026, 10, 29, 15, 20, 0, DateTimeKind.Local).ToUniversalTime();

    private static GrowthWindow Window(double hours, long grew, long cleared, long fell, int clears, double measuredHours) => new()
    {
        Span = TimeSpan.FromHours(hours), NowUtc = End, Measured = TimeSpan.FromHours(measuredHours), Total = new GrowthCounts(grew, cleared, fell),
        Clears = clears, StartedUtc = End.AddDays(-2),
    };

    /// <summary>25 hours a point a minute, with a 6-hour sleep (no point) from 00:45 to 06:45, the paged pool 2.8 GB.</summary>
    private static List<ChartPoint> Day()
    {
        var pts = new List<ChartPoint>();
        for (int m = 25 * 60; m >= 0; m--)
        {
            var t = End.AddMinutes(-m);
            var local = t.ToLocalTime();
            if (local >= End.ToLocalTime().Date.AddMinutes(45) && local < End.ToLocalTime().Date.AddHours(6).AddMinutes(45)) continue;
            pts.Add(new ChartPoint(t, new PoolTotals((ulong)(2.8 * GB) + (ulong)(m % 60) * MB, 618 * (ulong)MB, 0, 0)));
        }
        return pts;
    }

    /// <summary>The last <paramref name="minutes"/> minutes, a point every 10 seconds: PoolWatch started that long ago.</summary>
    private static List<ChartPoint> Since(int minutes) =>
        Enumerable.Range(0, minutes * 6 + 1).Select(i => new ChartPoint(End.AddSeconds(10 * i - minutes * 60), new PoolTotals((ulong)(2.8 * GB) + (ulong)i * MB, 618 * (ulong)MB, 0, 0))).ToList();

    private static bool Selectable(Control c) =>
        (bool)typeof(Control).GetMethod("GetStyle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(c, [ControlStyles.Selectable])!;

    private static ChartLayout Paint(Control c)
    {
        using var bitmap = new Bitmap(c.Width, c.Height);
        c.DrawToBitmap(bitmap, new Rectangle(Point.Empty, c.Size));
        return (c as PoolChart)?.LastLayout!;
    }

    private static string Clock(DateTime utc) => utc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ the graph follows the window

    [Theory]
    [InlineData(1, "Kernel pool, last hour")]
    [InlineData(6, "Kernel pool, last 6 hours")]
    [InlineData(24, "Kernel pool, last 24 hours")]
    public void TheGraphsTitleSaysItsSpan(double hours, string title)
    {
        // Whether or not PoolWatch has run that long: the band before it started says when it did.
        Assert.Equal(title, PoolChart.TitleFor(TimeSpan.FromHours(hours), 100));
        Assert.Equal("Kernel pool", PoolChart.TitleFor(TimeSpan.FromHours(hours), 1));
    }

    [Fact]
    public void TheAxisAlwaysSpansTheWholeWindowWithABandBeforePoolWatchStarted()
    {
        // PoolWatch started 30 minutes ago. Before, the axis started at the first sample, so the hour, 6 hours and 24
        // hours drew the same "since" chart; now each spans its own window, and the time before the start is a band.
        var started = End.AddMinutes(-30);
        Assert.Equal(new ChartSpan(End.AddHours(-1), End, started), ChartSpan.Of(End, TimeSpan.FromHours(1), started));
        Assert.Equal(new ChartSpan(End.AddHours(-24), End, started), ChartSpan.Of(End, TimeSpan.FromHours(24), started));
        // A start before the window, or within its first 2 minutes (the edges of the samples' minutes): no band.
        Assert.Null(ChartSpan.Of(End, TimeSpan.FromHours(1), End.AddHours(-3)).BandEndUtc);
        Assert.Null(ChartSpan.Of(End, TimeSpan.FromHours(1), End.AddMinutes(-59)).BandEndUtc);
        Assert.Null(ChartSpan.Of(End, TimeSpan.FromHours(1), null).BandEndUtc);
        // The newest sample at the right edge, the window's start at the left, the start where it falls between.
        var day = ChartSpan.Of(End, TimeSpan.FromHours(24), started);
        Assert.Equal(0f, day.X(End.AddHours(-24), 960));
        Assert.Equal(960f, day.X(End, 960));
        Assert.Equal(940f, day.X(started, 960), 0.01);
        Assert.Equal($"Before PoolWatch started ({Clock(started)})", ChartSpan.BandLabel(started, End));
        // A start before today says its day, as the clear marks do.
        var yesterday = End.AddHours(-20);
        string day2 = yesterday.ToLocalTime().Date < End.ToLocalTime().Date ? "yesterday " : "";
        Assert.Equal($"Before PoolWatch started ({day2}{Clock(yesterday)})", ChartSpan.BandLabel(yesterday, End));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(24)]
    public void EachWindowDrawsItsOwnAxisWhenPoolWatchHasRunForLess(double hours)
    {
        using var host = new Form();
        var chart = new PoolChart { Size = new Size(806, 215), Span = TimeSpan.FromHours(hours), StartedUtc = End.AddMinutes(-30) };
        host.Controls.Add(chart);
        chart.SetData(Since(30));
        var layout = Paint(chart);
        Assert.Empty(layout.Problems());
        Assert.Equal(PoolChart.TitleFor(TimeSpan.FromHours(hours), 100), chart.LastTitle);
        // The band runs from the plot's left edge to the start: a half of the hour, the rest of 6 or 24 hours.
        double before = (hours * 60 - 30) / (hours * 60);
        Assert.Equal(layout.Plot.Left, layout.Band.Left);
        Assert.InRange(layout.Band.Width, layout.Plot.Width * before - 2, layout.Plot.Width * before + 2);
        Assert.False(layout.BandLabel.IsEmpty);
        Assert.StartsWith("Before PoolWatch started", chart.BandWords);
        // The time labels cover the whole window: the first one sits near the plot's left edge, before the start.
        Assert.True(layout.XLabels[0].Left < layout.Band.Right, $"the first time label at {layout.XLabels[0]} is after the band {layout.Band}");
    }

    [Fact]
    public void ThePointsSitWhereTheirTimeIsAndTheClearsMarkInsideTheWindow()
    {
        // A clear 20 minutes ago on the hour's graph: its mark is two thirds of the way across, not at the edge a since-the-
        // start axis put it at.
        using var host = new Form();
        var chart = new PoolChart { Size = new Size(806, 215), Span = TimeSpan.FromHours(1), StartedUtc = End.AddMinutes(-30) };
        host.Controls.Add(chart);
        chart.SetData(Since(30));
        var clear = new ChartClear(End.AddMinutes(-20), GB, Automatic: true, Light: false, Done: true);
        chart.SetClears([clear]);
        var layout = Paint(chart);
        var mark = Assert.Single(layout.Markers);
        float x = mark.Left + mark.Width / 2f;
        Assert.InRange(x, layout.Plot.Left + layout.Plot.Width * 2 / 3f - 3, layout.Plot.Left + layout.Plot.Width * 2 / 3f + 3);
        Assert.Equal(clear.TextOn(End), chart.TipAt(new Point((int)x, mark.Top + 2)));
        // Over the band, the graph's help (nothing was measured there).
        chart.HelpText = "help";
        Assert.Equal("help", chart.TipAt(new Point(layout.Band.Left + 4, layout.Band.Top + 4)));
    }

    [Fact]
    public void ANarrowBandHasShorterWordsOrNone()
    {
        using var host = new Form();
        // Started 10 minutes into the hour on a narrow graph: room for the short words at most.
        var chart = new PoolChart { Size = new Size(466, 180), Span = TimeSpan.FromHours(1), StartedUtc = End.AddMinutes(-50) };
        host.Controls.Add(chart);
        chart.SetData(Since(50));
        var layout = Paint(chart);
        Assert.Empty(layout.Problems());
        Assert.True(chart.BandWords is null or ChartSpan.ShortBandLabel, chart.BandWords);
        Assert.False(layout.Band.IsEmpty);
    }

    [Theory]
    [InlineData(1, 806, 215)]
    [InlineData(24, 806, 215)]
    [InlineData(1, 466, 160)]  // the window at its smallest
    [InlineData(24, 466, 160)]
    public void TheTimeAxisIsReadableAtAnHourAndADay(double hours, int width, int height)
    {
        using var host = new Form();
        var chart = new PoolChart { Size = new Size(width, height), Span = TimeSpan.FromHours(hours), RamOption = true, SpanOption = true };
        host.Controls.Add(chart);
        chart.SetData(Day());
        var layout = Paint(chart);
        Assert.Equal(hours == 1 ? "Kernel pool, last hour" : "Kernel pool, last 24 hours", chart.LastTitle); // the span picked, not the Simple view's 6 hours
        Assert.Empty(layout.Problems()); // no label touches another or leaves the graph
        Assert.InRange(layout.XLabels.Count, 3, 12);
        Assert.True(layout.Band.IsEmpty); // PoolWatch ran all through
        // The labels are evenly spaced clock times.
        var gaps = layout.XLabels.Zip(layout.XLabels.Skip(1), (a, b) => b.Left - a.Left).ToList();
        Assert.InRange(gaps.Max() - gaps.Min(), 0, 2);
    }

    [Fact]
    public void TheSpanChoicePicksAWindowByClickOrKeyAndNotWhenSetFromCode()
    {
        using var host = new Form();
        var chart = new PoolChart { Size = new Size(806, 215), SpanOption = true };
        host.Controls.Add(chart);
        var choice = chart.SpanBox;
        Assert.Equal(["1 h", "6 h", "24 h"], SpanChoice.Labels);
        Assert.Equal("Last 6 hours", choice.Window.Name); // the session starts on 6 hours
        Assert.Equal("Time window", choice.AccessibleName);
        Assert.True(choice.TabStop);
        var picked = new List<string>();
        chart.SpanPicked += w => picked.Add(w.Name);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        void Click(int segment)
        {
            int x = choice.Width * segment / 3 + choice.Width / 6;
            typeof(Control).GetMethod("OnMouseDown", flags)!.Invoke(choice, [new MouseEventArgs(MouseButtons.Left, 1, x, choice.Height / 2, 0)]);
        }
        void Key(Keys k) => typeof(Control).GetMethod("OnKeyDown", flags)!.Invoke(choice, [new KeyEventArgs(k)]);
        Click(2);
        Assert.Equal(["Last 24 hours"], picked);
        Click(2); // the same one again: nothing new
        Key(Keys.Left);
        Key(Keys.Home);
        Key(Keys.Home);
        Assert.Equal(["Last 24 hours", "Last 6 hours", "Last hour"], picked);
        choice.SelectedIndex = 2; // the window sets it (a view switch): no event
        Assert.Equal(3, picked.Count);
        Assert.Equal("Last 24 hours", choice.Window.Name);
        // It sits at the top right, Show RAM left of it, neither over the title.
        chart.RamOption = true;
        var layout = Paint(chart);
        Assert.Empty(layout.Problems());
        Assert.True(layout.SpanBox.Right <= chart.Width && layout.RamBox.Right <= layout.SpanBox.Left, $"Show RAM {layout.RamBox}, the choice {layout.SpanBox}");
        chart.SpanOption = false; // the Simple view
        Assert.True(Paint(chart).SpanBox.IsEmpty);
    }

    [Fact]
    public void TheLinesBreakWherePoolWatchWasNotSampling()
    {
        var pts = Day();
        var runs = PoolChart.Segments(pts);
        Assert.Equal(2, runs.Count); // before and after the sleep
        Assert.Equal(pts.Count, runs.Sum(r => r.Count));
        Assert.Single(PoolChart.Segments(pts.Take(100).ToList()));
        Assert.Equal(TimeSpan.FromMinutes(10), PoolChart.Gap);
    }

    [Fact]
    public void APointAloneBetweenTwoGapsIsDrawnAsADot()
    {
        // Ten minutes of points, a sleep, one sample, another sleep, ten minutes more: the lone sample is a dot on each line.
        var pts = new List<ChartPoint>();
        var t = End.AddHours(-3);
        void Add(DateTime at) => pts.Add(new ChartPoint(at, new PoolTotals((ulong)(2.8 * GB), 618 * (ulong)MB, 0, 0)));
        for (int i = 0; i < 10; i++) Add(t.AddMinutes(i));
        Add(t.AddMinutes(60));
        for (int i = 0; i < 10; i++) Add(t.AddMinutes(120 + i));
        Assert.Equal([10, 1, 10], PoolChart.Segments(pts).Select(r => r.Count));
        using var host = new Form();
        var chart = new PoolChart { Size = new Size(776, 215), Span = TimeSpan.FromHours(6) };
        host.Controls.Add(chart);
        chart.SetData(pts);
        Paint(chart);
        Assert.Equal(2, chart.DotsDrawn); // on the paged and the non-paged line
        chart.SetData(Day());
        Paint(chart);
        Assert.Equal(0, chart.DotsDrawn);
    }

    [Fact]
    public void NewPointsWithTheSameCountAndNewestPointButAnotherFirstAreDrawn()
    {
        // Picking another window can hand the graph as many points as before, ending at the same one: the new ones are
        // drawn (without a start of its own, the first point says when PoolWatch started, so the band follows it).
        using var host = new Form();
        var chart = new PoolChart { Size = new Size(776, 215), Span = TimeSpan.FromHours(6) };
        host.Controls.Add(chart);
        var totals = new PoolTotals((ulong)(2.8 * GB), 618 * (ulong)MB, 0, 0);
        List<ChartPoint> Series(int firstMinutesAgo) =>
            [new(End.AddMinutes(-firstMinutesAgo), totals), .. Enumerable.Range(0, 100).Select(m => new ChartPoint(End.AddMinutes(m - 99), totals))];
        chart.SetData(Series(200));
        var before = Paint(chart).Band;
        chart.SetData(Series(150));
        var after = Paint(chart).Band;
        Assert.Equal($"Before PoolWatch started ({Clock(End.AddMinutes(-150))})", chart.BandWords);
        Assert.True(after.Width > before.Width);
    }

    [Fact]
    public void AClearBeforeTodaySaysItsDayOnTheDaysGraph()
    {
        var yesterday = End.AddHours(-20);
        var c = new ChartClear(yesterday, (long)(1.2 * GB), Automatic: true, Light: false, Done: true);
        Assert.Equal($"Cache clear yesterday at {yesterday.ToLocalTime():HH:mm} (automatic): freed 1.2 GB", c.TextOn(End));
        using var host = new Form();
        var chart = new PoolChart { Size = new Size(776, 215), Span = TimeSpan.FromHours(24) };
        host.Controls.Add(chart);
        chart.SetData(Day());
        chart.SetClears([c]);
        var mark = Assert.Single(Paint(chart).Markers);
        Assert.Equal(c.TextOn(End), chart.TipAt(mark.Location));
    }

    // ------------------------------------------------------------------ Show RAM

    private static List<RamColumn> Quarters()
    {
        var ram = new RamHistory();
        for (int m = 24 * 60; m >= 0; m--) ram.Add(End.AddMinutes(-m), 127 * GB, (long)(14.3 * GB), (long)(71.5 * GB));
        return ram.Columns(End, TimeSpan.FromHours(24), UiData.RamColumnWidth(TimeSpan.FromHours(24)));
    }

    [Fact]
    public void ShowRamDrawsColumnsBehindTheLinesOnAPercentAxisWithGigabytesOnHover()
    {
        using var host = new Form();
        var chart = new PoolChart { Size = new Size(776, 215), Span = TimeSpan.FromHours(24), RamOption = true };
        host.Controls.Add(chart);
        chart.SetData(Day());
        var columns = Quarters();
        chart.SetRam(columns, 127 * GB);

        // Off until the window sets it from the settings: no columns, no right axis, and the plot runs to the edge.
        Assert.False(chart.ShowRam);
        var plain = Paint(chart);
        Assert.Equal(0, chart.RamColumnsDrawn);
        Assert.Empty(plain.RamLabels!);
        Assert.Equal("Show RAM", chart.RamBox.Text);
        Assert.False(plain.RamBox.IsEmpty);
        // On the graph's fixed white, in a fixed dark text colour: high contrast's white text would vanish there.
        Assert.False(chart.RamBox.ForeColor.IsSystemColor);
        Assert.Equal((Color.White, Color.FromArgb(40, 40, 40)), (chart.RamBox.BackColor, chart.RamBox.ForeColor));
        Assert.Empty(plain.Problems());

        var turned = new List<bool>();
        chart.ShowRamChanged += turned.Add;
        chart.RamBox.Checked = true; // the owner ticks Show RAM
        Assert.Equal([true], turned);
        Assert.True(chart.ShowRam);
        var layout = Paint(chart);
        Assert.Empty(layout.Problems());
        Assert.InRange(chart.RamColumnsDrawn, 96 - 30, 97); // a quarter hour each, but the sleep's hidden none (RAM was read all day)
        Assert.Equal(5, layout.RamLabels!.Count);            // 0%, 25%, 50%, 75%, 100% on the right
        Assert.True(layout.RamLabels.All(r => r.Left > layout.Plot.Right));
        Assert.True(layout.Plot.Right < plain.Plot.Right);   // the plot makes room for them
        Assert.True(layout.Legend.Width > plain.Legend.Width); // in use, cache (standby) and free join the legend
        // Hovering a column: its time and GB; over a clear mark or outside the plot, the mark's text or the graph's help.
        chart.HelpText = "help";
        var at = new Point(layout.Plot.Left + layout.Plot.Width * 3 / 4, layout.Plot.Top + layout.Plot.Height / 2);
        string tip = chart.TipAt(at);
        Assert.Matches(@"^\d\d:\d\d: in use 41\.2 GB, cache 71\.5 GB, free 14\.3 GB \(of 127 GB RAM\)$", tip);
        Assert.Equal("help", chart.TipAt(new Point(2, 2)));
        // The pointer over a column outlines it; a new sample moves the columns under the still pointer, and the outline
        // is the column under it then, not where the old one was. Gone with the pointer.
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        void Move(Point p) => typeof(PoolChart).GetMethod("OnMouseMove", flags)!.Invoke(chart, [new MouseEventArgs(MouseButtons.None, 0, p.X, p.Y, 0)]);
        Move(at);
        var before = chart.HoverColumn;
        Assert.True(before.Contains(at));
        chart.SetData(Day().Select(p => p with { TimeUtc = p.TimeUtc.AddMinutes(7) }).ToList());
        Paint(chart);
        Assert.True(chart.HoverColumn.Contains(at));
        Assert.NotEqual(before, chart.HoverColumn);
        // The tooltip follows the outline: once another column is under the still pointer, it says that column's GB.
        string tipBefore = chart.ShownTip!;
        chart.SetData(Day().Select(p => p with { TimeUtc = p.TimeUtc.AddMinutes(37) }).ToList());
        Paint(chart);
        Assert.NotEqual(tipBefore, chart.TipAt(at));
        Assert.Equal(chart.TipAt(at), chart.ShownTip);
        typeof(PoolChart).GetMethod("OnMouseLeave", flags)!.Invoke(chart, [EventArgs.Empty]);
        Paint(chart);
        Assert.True(chart.HoverColumn.IsEmpty);
        // Over a clear mark the mark's text wins, and no column is outlined.
        var clear = new ChartClear(End.AddHours(-2), GB, Automatic: true, Light: false, Done: true);
        chart.SetClears([clear]);
        var mark = Assert.Single(Paint(chart).Markers);
        var onMark = new Point(mark.Left + mark.Width / 2, mark.Top + mark.Height / 2);
        Move(onMark);
        Paint(chart);
        Assert.True(chart.HoverColumn.IsEmpty);
        Assert.Equal(clear.TextOn(End.AddMinutes(7)), chart.TipAt(onMark));
        chart.SetClears([]);
        Move(at);
        // The Simple view's graph has no Show RAM at all.
        chart.RamOption = false;
        Paint(chart);
        Assert.Equal(0, chart.RamColumnsDrawn);
        Assert.Equal("help", chart.TipAt(at));
    }

    [Fact]
    public void TheRamOverlayIsAShareOfThePhysicalRam()
    {
        // The PC's RAM as Windows reports it (GlobalMemoryStatusEx), never less than what the GC may use of it.
        long physical = PoolWatch.Native.MemoryNative.TotalPhysical();
        Assert.True(physical >= (long)GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, $"{physical} bytes");
        Assert.True(physical >= 1L << 30);
    }

    [Fact]
    public void RamIsSaidInTheMockupsWords()
    {
        var column = new RamColumn(new DateTime(2026, 10, 29, 13, 25, 0, DateTimeKind.Local).ToUniversalTime(), End, (long)(41.2 * GB), (long)(71.5 * GB), (long)(14.3 * GB));
        Assert.Equal("13:25: in use 41.2 GB, cache 71.5 GB, free 14.3 GB (of 127 GB RAM)", PoolChart.RamTip(column, 127 * GB, End));
        var lastNight = column with { FromUtc = new DateTime(2026, 10, 28, 23, 15, 0, DateTimeKind.Local).ToUniversalTime() };
        Assert.Equal("Yesterday 23:15: in use 41.2 GB, cache 71.5 GB, free 14.3 GB (of 127 GB RAM)", PoolChart.RamTip(lastNight, 127 * GB, End));
        Assert.Equal("RAM in use, cache (standby) and free, as a share of the PC's 127 GB (right axis).", PoolChart.RamBoxTip(127 * GB));
        Assert.Equal((Color.FromArgb(221, 212, 234), Color.FromArgb(235, 230, 243), Color.FromArgb(246, 244, 250)), (PoolChart.RamInUse, PoolChart.RamCache, PoolChart.RamFree));
        // The columns follow the window: a minute, 5 minutes, a quarter hour.
        Assert.Equal([TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)], StatsWindow.All.Select(w => UiData.RamColumnWidth(w.Span)));
    }

    [Fact]
    public void ShowRamIsOnForANewInstallAndKeptInTheSettingsButNotInTheAdvancedGrid()
    {
        Assert.True(new Settings().ShowRam);                              // a new install
        Assert.True(Settings.Parse("{ \"retentionDays\": 5 }").ShowRam); // a settings.json saved before Show RAM existed
        // A settings.json that holds it keeps it: turned off, it stays off.
        Assert.False(Settings.Parse("{ \"showRam\": false }").ShowRam);
        Assert.True(Settings.Parse("{ \"showRam\": true }").ShowRam);
        Assert.Contains("\"showRam\": false", new Settings { ShowRam = false }.ToJson());
        Assert.False(Settings.Parse(new Settings { ShowRam = false }.ToJson()).ShowRam);
        var property = TypeDescriptor.GetProperties(typeof(Settings))[nameof(Settings.ShowRam)]!;
        Assert.False(property.IsBrowsable); // the graph's own switch, not a row of the Advanced grid
    }

    // ------------------------------------------------------------------ the stats beside the graph

    [Fact]
    public void TheStatsBesideTheGraphSayEachWindowAsTheDesignDoes()
    {
        using var host = new Form();
        var panel = new GrowthPanel { Size = new Size(250, 215) };
        host.Controls.Add(panel);
        panel.Show(StatsWindow.Default, Window(6, 7578 * MB, 7270 * MB, 0, 6, 6));
        Assert.Equal(["Last 6 hours", "Grew", "+7.4 GB", "Cleared (6 clears)", "\u22127.1 GB", "Fell on its own", "0", "Net change", "up 308 MB",
            "Without the clears it would grow 1.2 GB per hour."], panel.Texts);
        // What a screen reader reads: each label with its value, as sentences.
        Assert.Equal("Last 6 hours. Grew +7.4 GB. Cleared (6 clears) \u22127.1 GB. Fell on its own 0. Net change up 308 MB. " +
                     "Without the clears it would grow 1.2 GB per hour.", panel.AccessibleName);
        Assert.False(panel.TabStop || Selectable(panel));
        using (var bitmap = new Bitmap(panel.Width, panel.Height)) panel.DrawToBitmap(bitmap, new Rectangle(Point.Empty, panel.Size));
        Assert.Equal(panel.Texts, panel.Painted); // it draws what it says
        Assert.InRange(panel.NeededHeight, 1, panel.Height); // it all fits beside the graph
        Assert.Equal(GrowthText.PerHourTip, panel.TipAt(new Point(40, 60)));

        // PoolWatch started inside the day: since when, and how long it measured.
        var started = End.AddMinutes(-(8 * 60 + 48));
        panel.Show(StatsWindow.Day, Window(24, 11469 * MB, 11059 * MB, 102 * MB, 9, 8.8) with { StartedUtc = started });
        Assert.Equal($"Since {Clock(started)} (8.8 h)", panel.Texts[0]);
        Assert.Equal("\u2212102 MB", panel.Texts[6]);
        Assert.Equal("up 308 MB", panel.Texts[8]);
        // A day with a sleep in it: how much was measured, and why the rest was not.
        panel.Show(StatsWindow.Day, Window(24, 23245 * MB, 21811 * MB, 615 * MB, 18, 18));
        Assert.Equal("Last 24 hours (18 h measured)", panel.Texts[0]);
        Assert.Equal(LedgerText.NotSampled, panel.Texts[^1]);
        using (var bitmap = new Bitmap(panel.Width, panel.Height)) panel.DrawToBitmap(bitmap, new Rectangle(Point.Empty, panel.Size));
        Assert.InRange(panel.NeededHeight, 1, panel.Height);
    }

    [Fact]
    public void TheStatsSayNotKnownYetBeforeAnyDataAndTheRateInEveryDirection()
    {
        using var host = new Form();
        var panel = new GrowthPanel { Size = new Size(250, 215) };
        host.Controls.Add(panel);
        Assert.Equal(["Last 6 hours", "Grew", "not known yet", "Cleared (0 clears)", "not known yet", "Fell on its own", "not known yet", "Net change",
            "not known yet", "Growth per hour: known in 30 min."], panel.Texts);
        Assert.Equal("It grows 100 MB per hour.", LedgerText.Rate(Window(6, 600 * MB, 0, 0, 0, 6)));
        Assert.Equal("It falls 100 MB per hour.", LedgerText.Rate(Window(6, 0, 0, 600 * MB, 0, 6)));
        Assert.Equal("Without the clears it would fall 100 MB per hour.", LedgerText.Rate(Window(6, 0, 3 * GB, 600 * MB, 2, 6)));
        Assert.Equal("It holds steady.", LedgerText.Rate(Window(6, 0, 0, 0, 0, 6)));
        Assert.Equal("Growth per hour: known in 20 min.", LedgerText.Rate(Window(6, 0, 0, 0, 0, 10.0 / 60)));
        Assert.Null(LedgerText.Measured(Window(6, GB, 0, 0, 0, 6)));
        Assert.Equal(["8.8 h", "40 min", "1 h"], new[] { 8.8, 40.0 / 60, 1 }.Select(h => LedgerText.Hours(TimeSpan.FromHours(h))));
    }

    [Fact]
    public void WhenTheNotesDoNotFitTheyAreThePanelsTooltip()
    {
        // A large display scaling on a small screen leaves the panel lower than its texts: the notes go into the tooltip,
        // so nothing is cut off.
        using var host = new Form();
        var panel = new GrowthPanel { Size = new Size(250, 120) };
        host.Controls.Add(panel);
        panel.Show(StatsWindow.Day, Window(24, 23245 * MB, 21811 * MB, 615 * MB, 18, 18));
        using (var bitmap = new Bitmap(panel.Width, panel.Height)) panel.DrawToBitmap(bitmap, new Rectangle(Point.Empty, panel.Size));
        Assert.InRange(panel.NeededHeight, 1, panel.Height);
        Assert.DoesNotContain(LedgerText.NotSampled, panel.Painted);
        Assert.Contains(LedgerText.NotSampled, panel.TipAt(new Point(40, 110)));
        Assert.Contains(LedgerText.NotSampled, panel.AccessibleName); // a screen reader still reads it
    }

    [Fact]
    public void UnderHighContrastTheStatsPaintInTheSystemsTextColours()
    {
        Assert.Equal((SystemColors.GrayText, SystemColors.WindowText, SystemColors.GrayText), StatColors.For(highContrast: true));
        Assert.Equal((UiColors.Muted, UiColors.Ink, UiColors.Line), StatColors.For(highContrast: false));
    }

    // ------------------------------------------------------------------ the Simple view's tiles

    [Fact]
    public void TheTilesSayTheGraphsWindowInTheDesignsWords()
    {
        var six = Window(6, 7578 * MB, 7270 * MB, 0, 6, 6);
        var ram = new RamNow(41 * GB, 71 * GB, 127 * GB);
        Assert.Equal(
        [
            new Tile("Growth per hour", "1.2 GB", "without the clears, last 6 h"), new Tile("Cleared", "7.1 GB", "6 clears, last 6 h"),
            new Tile("Net change", "up 308 MB", "last 6 h"), new Tile("Memory in use", "41 GB", "of 127 GB, 71 GB cache"),
        ], TileText.Of(six, ram));
        // Before anything is measured nothing is said to grow, and no figure is said as known.
        var none = new GrowthWindow { Span = TimeSpan.FromHours(6), NowUtc = End };
        Assert.Equal(["known in 30 min", "not known yet", "not known yet", "not known yet"], TileText.Of(none, null).Select(t => t.Value));
        // Since PoolWatch started, when that is inside the window; no clears; a pool that did not grow.
        var started = End.AddHours(-2);
        var quiet = Window(6, 0, 0, 0, 0, 2) with { StartedUtc = started };
        Assert.Equal(new Tile("Growth per hour", "none", $"since {Clock(started)}"), TileText.Rate(quiet));
        Assert.Equal(new Tile("Cleared", "nothing", $"no clears, since {Clock(started)}"), TileText.Cleared(quiet));
        Assert.Equal("unchanged", TileText.Net(quiet).Value);
        Assert.Equal("1 clear, last hour", TileText.Cleared(Window(1, GB, GB, 0, 1, 1)).Sub);
        Assert.Equal("It grows", GrowthText.RateCaption(Window(6, 600 * MB, 0, 0, 0, 6))); // the panel's caption words stay the Core's
        Assert.Equal(new Tile("Growth per hour", "100 MB", "last 6 h"), TileText.Rate(Window(6, 600 * MB, 0, 0, 0, 6))); // no clears: no "without"
    }

    [Theory]
    [InlineData(1074, 4)]
    [InlineData(744, 2)] // the window at its smallest: two rows of two
    public void TheTilesDrawWhatTheySayAndFitTheSmallestWindow(int width, int columns)
    {
        using var host = new Form { ClientSize = new Size(width, 640) };
        var view = new SimpleView { Dock = DockStyle.Fill };
        host.Controls.Add(view);
        view.SetChart(new PoolChart());
        // The longest it says (PoolWatch started yesterday, every tile known), at the window's smallest.
        var started = DateTime.UtcNow.AddHours(-5);
        view.ShowTiles(TileText.Of(Window(6, 23245 * MB, 21811 * MB, 300 * MB, 18, 5) with { StartedUtc = started, NowUtc = DateTime.UtcNow },
            new RamNow(41 * GB, 71 * GB, 127 * GB)));
        host.PerformLayout();
        Assert.Equal(columns, view.Tiles.Columns);
        Assert.Equal(view.Tiles.NeededHeight, view.Tiles.Height);
        using (var bitmap = new Bitmap(view.Tiles.Width, view.Tiles.Height)) view.Tiles.DrawToBitmap(bitmap, new Rectangle(Point.Empty, view.Tiles.Size));
        Assert.Equal(view.Tiles.Texts.Where(t => t.Length > 0), view.Tiles.Painted); // it draws what it says
        Assert.Empty(view.Tiles.Problems());
        Assert.StartsWith("Growth per hour", view.Tiles.AccessibleName);
        Assert.False(view.Tiles.TabStop || Selectable(view.Tiles));
        Assert.Equal(GrowthText.PerHourTip, view.Tiles.TipAt(new Point(20, 20)));
        Assert.Equal("", view.Tiles.TipAt(new Point(view.Tiles.Width - 20, 20)));
        Assert.Empty(view.LayoutProblems());
    }
}
