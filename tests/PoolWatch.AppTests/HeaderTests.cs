using System.Globalization;
using System.Text.RegularExpressions;
using PoolWatch.Core.Cache;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Growth;
using PoolWatch.Core.Model;
using PoolWatch.Core.Util;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// The main window's header: one statement per fact (the pool now, its trend, the next clear, the last clear's result, a
/// note only when there is something to watch), every amount rounded one way, and laid out without a text over another
/// at the window's sizes. The control is painted off screen and never shown.
/// </summary>
public class HeaderTests
{
    private const long MB = 1L << 20, GB = 1L << 30;
    private static readonly DateTime Now = new DateTime(2026, 10, 30, 9, 3, 0, DateTimeKind.Local).ToUniversalTime();

    private static TotalsNow Totals(long total, long change10Min, TimeSpan? held = null, DateTime? measuring = null) =>
        new(total, held ?? TimeSpan.FromHours(2), change10Min, null) { MeasuringUtc = measuring };

    private static PoolTotals Split(long paged, long nonPaged) => new((ulong)paged, (ulong)nonPaged, 0, 0);

    private static ClearRecord Clear(DateTime at, long freed, string? error = null) => new()
    {
        TimeUtc = at, Mode = ClearMode.Standby, Trigger = ClearTrigger.Schedule, Before = new PoolTotals((ulong)(10 * GB), 0, 0, 0),
        After = new PoolTotals((ulong)(10 * GB - freed), 0, 0, 0), Error = error,
    };

    private static string Clock(DateTime utc) => utc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    [Fact]
    public void TheHeaderSaysEachFactOnceInTheDesignsWords()
    {
        var last = Now.AddMinutes(-4);
        var f = HeaderText.Of(Totals(8601 * MB, 243 * MB), Split(4710 * MB, 3891 * MB), new NextClear(NextClearKind.Scheduled, Now.AddMinutes(37)),
            [Clear(last, 914 * MB)], everyHours: 1, Now);
        Assert.Equal("8.4 GB", f.PoolNow);
        Assert.Equal("paged 4.6 GB, non-paged 3.8 GB", f.PoolSplit);
        Assert.Equal((TrendKind.Up, HeaderTone.Watch, "243 MB", "up in the last 10 min"), (f.Trend, f.TrendTone, f.TrendAmount, f.TrendText));
        Assert.Equal("Next clear in 37 min", f.NextTitle);
        Assert.Equal($"Last at {Clock(last)}: freed 914 MB", f.NextText);
        Assert.Equal(1 - 37 / 60.0, f.Ring!.Value, 6);
        // One statement per fact: the next clear is said once, never as "next cache clear" twice, and no amount is signed.
        string spoken = f.Spoken;
        Assert.StartsWith("Kernel pool now 8.4 GB (paged 4.6 GB, non-paged 3.8 GB).", spoken);
        Assert.Single(Regex.Matches(spoken, "[Nn]ext clear"));
        Assert.DoesNotMatch(@"[+\-−]\d", spoken);
        Assert.DoesNotContain("next cache clear", spoken, StringComparison.OrdinalIgnoreCase);
        Assert.Equal($"Kernel pool now 8.4 GB (paged 4.6 GB, non-paged 3.8 GB). 243 MB up in the last 10 min. Next clear in 37 min. Last at {Clock(last)}: freed 914 MB.", spoken);
    }

    [Fact]
    public void FreedAndClearedAreRoundedTheSameWay()
    {
        // The header said "freed 914 MB" while the stats row said "Cleared 913.6 MB": one rule now, the header's.
        foreach (long bytes in new[] { (long)(913.6 * MB), 1023L * MB + 700 * 1024, 5 * GB + 123 * MB, 600 * 1024 })
        {
            var f = HeaderText.Of(Totals(8 * GB, 0), Split(4 * GB, 4 * GB), new NextClear(NextClearKind.Off), [Clear(Now.AddMinutes(-4), bytes)], 0, Now);
            var tile = TileText.Cleared(new GrowthWindow
            {
                Span = TimeSpan.FromHours(6), NowUtc = Now, Measured = TimeSpan.FromHours(6), Clears = 1, Total = new GrowthCounts(bytes, bytes, 0),
            });
            Assert.EndsWith("freed " + tile.Value, f.NextText);
            Assert.Equal(PlainWords.Amount(bytes), tile.Value);
        }
        Assert.Equal("914 MB", HeaderText.Freed((long)(913.6 * MB)));
        Assert.Equal("nothing", HeaderText.Freed(100 * 1024));
    }

    [Theory]
    [InlineData(0L, "Steady", "in the last 10 min", (int)TrendKind.Steady, (int)HeaderTone.Calm)]
    [InlineData(40L * MB, "40 MB", "up in the last 10 min", (int)TrendKind.Up, (int)HeaderTone.Calm)]       // a small rise: grey
    [InlineData(150L * MB, "150 MB", "up in the last 10 min", (int)TrendKind.Up, (int)HeaderTone.Watch)]     // 100 MB or more: amber
    [InlineData(-30L * MB, "30 MB", "down in the last 10 min", (int)TrendKind.Down, (int)HeaderTone.Calm)]
    [InlineData(-1300L * MB, "1.3 GB", "down in the last 10 min", (int)TrendKind.Down, (int)HeaderTone.Good)]
    public void TheTrendIsAnArrowAnAmountAndItsWindow(long change, string amount, string text, int kind, int tone)
    {
        Assert.Equal(((TrendKind)kind, (HeaderTone)tone, amount, text), HeaderText.Trend(Totals(8 * GB, change)));
    }

    [Fact]
    public void TheTrendWaitsForSamplesAndForAClearBeingMeasured()
    {
        Assert.Equal((TrendKind.Collecting, "", "collecting the first sample"),
            (HeaderText.Trend(new TotalsNow(null, TimeSpan.Zero, 0, null)).Kind, HeaderText.Trend(new TotalsNow(null, TimeSpan.Zero, 0, null)).Amount, HeaderText.Trend(new TotalsNow(null, TimeSpan.Zero, 0, null)).Text));
        Assert.Equal(TrendKind.Collecting, HeaderText.Trend(Totals(8 * GB, 50 * MB, TimeSpan.FromMinutes(2))).Kind);
        Assert.Equal("up in the last 6 min", HeaderText.Trend(Totals(8 * GB, 50 * MB, TimeSpan.FromMinutes(6))).Text);
        var measuring = HeaderText.Trend(Totals(8 * GB, 0, measuring: Now.AddMinutes(-1)));
        Assert.Equal((TrendKind.Measuring, "a cache clear is being measured"), (measuring.Kind, measuring.Text));
        // Before the first sample the pool is not said as a number.
        var none = HeaderText.Of(new TotalsNow(null, TimeSpan.Zero, 0, null), default, new NextClear(NextClearKind.Off), [], 0, Now);
        Assert.Equal(("not known yet", ""), (none.PoolNow, none.PoolSplit));
        Assert.StartsWith("Kernel pool now not known yet. Collecting the first sample.", none.Spoken);
    }

    [Fact]
    public void TheNextClearSaysWhenAndHowCloseForEachRule()
    {
        Assert.Equal(("Next clear in 37 min or at 8 GB", 1 - 37 / 60.0),
            HeaderText.Next(new NextClear(NextClearKind.Scheduled, Now.AddMinutes(37), PoolBytes: 8 * GB), 1, 6 * GB, Now));
        var size = HeaderText.Next(new NextClear(NextClearKind.AtPoolSize, PoolBytes: 8 * GB), 0, 6 * GB, Now);
        Assert.Equal(("Next clear at 8 GB", 0.75), (size.Title, size.Ring!.Value));
        Assert.Equal(("Clearing now", (double?)1), HeaderText.Next(new NextClear(NextClearKind.Running), 1, 6 * GB, Now));
        Assert.Equal(("Automatic clearing is off", (double?)null), HeaderText.Next(new NextClear(NextClearKind.Off), 1, 6 * GB, Now));
        Assert.Equal("Your own clear task clears it", HeaderText.Next(new NextClear(NextClearKind.OwnTask), 1, 6 * GB, Now).Title);
        Assert.Equal("Next clear in under a minute", HeaderText.Next(new NextClear(NextClearKind.Scheduled, Now.AddSeconds(20)), 1, 0, Now).Title);
        Assert.Equal("Next clear in 1 h 20 min", HeaderText.Next(new NextClear(NextClearKind.Scheduled, Now.AddMinutes(80)), 2, 0, Now).Title);
        // The last clear: what it freed, that it did not run, or that there was none yet; nothing while the first one runs.
        Assert.Equal("No clear yet", HeaderText.LastClear([], Now, running: false));
        Assert.Equal("", HeaderText.LastClear([], Now, running: true));
        Assert.Equal($"Last at {Clock(Now.AddMinutes(-5))}: did not run", HeaderText.LastClear([Clear(Now.AddMinutes(-5), 0, "access denied")], Now, false));
        var yesterday = Now.AddDays(-1);
        Assert.Equal($"Last yesterday at {Clock(yesterday)}: freed nothing", HeaderText.LastClear([Clear(yesterday, 0)], Now, false));
    }

    [Fact]
    public void TheNoteSaysOnlyWhatIsWorthWatching()
    {
        // Nothing to watch: no note.
        Assert.Equal((null, HeaderTone.Calm), MainForm.HeaderNote(null, null, PoolStatusKind.Steady, TrayState.Green));
        // A cause PoolWatch is sure of, in soft words; a weak one is not named.
        var lead = new PoolStatusLead("File system metadata (Reff, refs.sys)", 700 * MB, TimeSpan.FromHours(1), "Stream Deck", Confidence.StronglyCorrelated);
        Assert.Equal(("PoolWatch is keeping an eye on Stream Deck.", HeaderTone.Calm), MainForm.HeaderNote(lead, null, PoolStatusKind.Growing, TrayState.Green));
        Assert.Null(MainForm.HeaderNote(lead with { Confidence = Confidence.Weak }, null, PoolStatusKind.Growing, TrayState.Green).Note);
        // A tag that grew while the total held, and a saturated PC (amber).
        Assert.Equal("File system metadata (Reff, refs.sys) grew 700 MB in the last hour.",
            MainForm.HeaderNote(lead with { Cause = null }, null, PoolStatusKind.TagGrowing, TrayState.Green).Note);
        Assert.Equal(("Your PC is saturated; busiest: cl.exe.", HeaderTone.Watch), MainForm.HeaderNote(null, "cl.exe", PoolStatusKind.Steady, TrayState.Green));
        // Amber and red, with nothing else to say: where to look, in the tray icon's colour.
        Assert.Equal(HeaderTone.Risk, MainForm.HeaderNote(null, null, PoolStatusKind.Steady, TrayState.Red).Tone);
        Assert.EndsWith("Pool causes in the Advanced view has the details.", MainForm.HeaderNote(null, null, PoolStatusKind.Steady, TrayState.Amber).Note);
    }

    [Theory]
    [InlineData(1052, false)]  // the window's own size, less the buttons
    [InlineData(600, true)]    // the window at its smallest: the next clear goes under
    public void TheHeaderLaysItsFactsOutWithoutOneOverAnother(int width, bool twoRows)
    {
        using var host = new Form();
        var header = new PoolHeaderView();
        host.Controls.Add(header);
        var last = Now.AddMinutes(-4);
        var f = HeaderText.Of(Totals(8601 * MB, 243 * MB), Split(4710 * MB, 3891 * MB), new NextClear(NextClearKind.Scheduled, Now.AddMinutes(37), 8 * GB),
            [Clear(last, 914 * MB)], 1, Now, "PoolWatch is keeping an eye on Example Device Service.", HeaderTone.Calm);
        header.Show(f, "Kernel pool grew 243 MB in the last 10 minutes.", "Next cache clear in 37 min (sooner if the kernel pool reaches 8 GB).");
        header.Size = new Size(width, header.NeededHeight(width));
        using (var bmp = new Bitmap(header.Width, header.Height)) header.DrawToBitmap(bmp, new Rectangle(Point.Empty, header.Size));
        Assert.Empty(header.Problems());
        Assert.Equal(twoRows, header.NeededHeight(width) > header.NeededHeight(10_000));
        var painted = header.Painted.Select(p => p.Text).ToList();
        foreach (var text in new[] { "Kernel pool now", "8.4 GB", f.PoolSplit, "243 MB", "up in the last 10 min", f.NextTitle, f.NextText })
            Assert.Contains(text, painted);
        // The note shows where it has room, else it is the tooltip; over the trend and the next clear, their fuller words.
        Assert.Equal(!header.NoteHidden, painted.Contains(f.Note!));
        Assert.Equal(f.Spoken, header.AccessibleName);
        var trend = header.Painted.First(p => p.Text == "243 MB").Bounds;
        Assert.Equal("Kernel pool grew 243 MB in the last 10 minutes.", header.TipAt(new Point(trend.Left + 2, trend.Top + 2)));
        var next = header.Painted.First(p => p.Text == f.NextTitle).Bounds;
        Assert.StartsWith("Next cache clear in 37 min", header.TipAt(new Point(next.Left + 2, next.Top + 2)));
    }

    [Fact]
    public void TheServicesTabsHaveTheSmallerHeader()
    {
        Assert.False(MainForm.CompactHeader(advanced: false, tab: 1));
        Assert.False(MainForm.CompactHeader(advanced: true, tab: 0));
        Assert.True(MainForm.CompactHeader(advanced: true, tab: 1));
        Assert.True(MainForm.CompactHeader(advanced: true, tab: 2));
        // So has Pool causes on a window too narrow for the full facts in a row; the Simple view takes two rows instead.
        Assert.True(MainForm.CompactHeader(advanced: true, tab: 0, fullFitsOneRow: false));
        Assert.False(MainForm.CompactHeader(advanced: false, tab: 0, fullFitsOneRow: false));
        // Settings is on the header on Pool causes only: the smaller header has Clear now alone, as the Simple view.
        Assert.True(MainForm.SettingsShown(advanced: true, tab: 0));
        Assert.False(MainForm.SettingsShown(advanced: true, tab: 1));
        Assert.False(MainForm.SettingsShown(advanced: true, tab: 2));
        Assert.False(MainForm.SettingsShown(advanced: false, tab: 0));
        // The smaller header's trend words, as the design has them there.
        Assert.Equal("up in 10 min", HeaderText.Short("up in the last 10 min"));
        Assert.Equal("down in 6 min", HeaderText.Short("down in the last 6 min"));
        Assert.Equal("in the last 6 min", HeaderText.Short("in the last 6 min")); // Steady: never read as a time to come
        Assert.Equal("collecting the first sample", HeaderText.Short("collecting the first sample"));
        using var host = new Form();
        var header = new PoolHeaderView();
        host.Controls.Add(header);
        var f = HeaderText.Of(Totals(8601 * MB, 243 * MB), Split(4710 * MB, 3891 * MB), new NextClear(NextClearKind.Scheduled, Now.AddMinutes(37)),
            [Clear(Now.AddMinutes(-4), 914 * MB)], 1, Now);
        header.Show(f, "", "");
        int full = header.NeededHeight(1052);
        header.Compact = true;
        int compact = header.NeededHeight(1052);
        Assert.True(compact < full, $"compact {compact}, full {full}");
        // What the Services tab's layout test models (ServicesTabLayoutTests): the smaller header with its padding, at most 66.
        Assert.InRange(compact + host.LogicalToDeviceUnits(20), 1, host.LogicalToDeviceUnits(66));
        Assert.InRange(full + host.LogicalToDeviceUnits(28), 1, host.LogicalToDeviceUnits(100));
        header.Size = new Size(1052, compact);
        using (var bmp = new Bitmap(header.Width, header.Height)) header.DrawToBitmap(bmp, new Rectangle(Point.Empty, header.Size));
        Assert.Empty(header.Problems());
        var painted = header.Painted.Select(p => p.Text).ToList();
        Assert.Contains("8.4 GB", painted);
        Assert.Contains("Next clear in 37 min", painted);
        Assert.Contains("up in 10 min", painted);
        Assert.DoesNotContain("up in the last 10 min", painted);
        Assert.DoesNotContain(f.PoolSplit, painted); // the split and the last clear wait for Pool causes
        Assert.DoesNotContain(f.NextText, painted);
        Assert.Equal(f.Spoken, header.AccessibleName); // a screen reader still hears every fact
        // Whether the facts fit one row: the smaller header fits where the full one does not.
        header.Compact = false;
        int fullRow = header.OneRowWidth;
        Assert.True(header.FitsOneRow(fullRow, compact: false) && !header.FitsOneRow(fullRow - 1, compact: false));
        Assert.True(header.FitsOneRow(fullRow - 1, compact: true));
    }
}
