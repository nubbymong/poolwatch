using PoolWatch.Core;
using PoolWatch.Core.Cache;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Core.Recommendations;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// The Simple view: its graph marks every cache clear, inside the plot, with what it freed on hover; the status leaves out
/// what PoolWatch's own clear did and keeps the tray icon calm; its suggestion cards each have one button, fit at the
/// window's sizes and change only when what they say changed. The controls here are painted off screen and never shown.
/// </summary>
public class SimpleViewTests
{
    private const long GB = 1L << 30;

    private static List<ChartPoint> ThreeHours(DateTime end) =>
        Enumerable.Range(0, 181).Select(i => new ChartPoint(end.AddMinutes(i - 180), new PoolTotals((ulong)(6 * GB), (ulong)(5 * GB), 0, 0))).ToList();

    private static ChartLayout Paint(PoolChart chart)
    {
        using var bitmap = new Bitmap(chart.Width, chart.Height);
        chart.DrawToBitmap(bitmap, new Rectangle(Point.Empty, chart.Size));
        return chart.LastLayout!;
    }

    [Theory]
    [InlineData(900, 280)]
    [InlineData(420, 160)]
    public void ClearMarksSitInsideThePlotAndSayWhatEachClearFreed(int width, int height)
    {
        using var host = new Form();
        var chart = new PoolChart { Size = new Size(width, height) };
        host.Controls.Add(chart);
        var end = DateTime.UtcNow;
        chart.SetData(ThreeHours(end));
        ChartClear[] clears =
        [
            new(end.AddHours(-3), 5 * GB, Automatic: true, Light: false, Done: true),        // at the graph's left edge
            new(end.AddMinutes(-90), 0, Automatic: true, Light: true, Done: true),
            new(end, 0, Automatic: false, Light: false, Done: false),                          // at its right edge, being measured
            new(end.AddHours(-5), 4 * GB, Automatic: true, Light: false, Done: true),         // before the graph: not marked
        ];
        chart.SetClears(clears);
        chart.HelpText = "Committed paged and non-paged pool.";

        var layout = Paint(chart);
        Assert.Equal(3, layout.Markers.Count);
        Assert.Empty(layout.Problems());
        var middle = layout.Markers[1];
        Assert.Equal(clears[1].Text, chart.TipAt(new Point(middle.Left + middle.Width / 2, middle.Bottom + 20))); // on its line, below the mark
        Assert.Equal(clears[0].Text, chart.TipAt(layout.Markers[0].Location));
        Assert.Equal("Committed paged and non-paged pool.", chart.TipAt(new Point(layout.Plot.Left + layout.Plot.Width / 4, layout.Plot.Top + 5)));
    }

    [Fact]
    public void ClearsCloseInTimeShowTogether()
    {
        using var host = new Form();
        var chart = new PoolChart { Size = new Size(400, 200) };
        host.Controls.Add(chart);
        var end = DateTime.UtcNow;
        chart.SetData(ThreeHours(end));
        // A light clear that freed nothing and the full one 2 minutes later: their marks overlap.
        ChartClear light = new(end.AddMinutes(-60), 0, true, true, true), full = new(end.AddMinutes(-58), (long)(5.1 * GB), true, false, true);
        chart.SetClears([light, full]);
        var marks = Paint(chart).Markers;
        var both = Rectangle.Intersect(marks[0], marks[1]);
        Assert.False(both.IsEmpty);
        Assert.Equal(light.Text + Environment.NewLine + full.Text, chart.TipAt(new Point(both.Left + both.Width / 2, both.Top + 1)));
        Assert.EndsWith("(automatic, light): freed nothing", light.Text);
    }

    // ------------------------------------------------------------------ the status: PoolWatch's own clear, and the tray icon

    [Fact]
    public void AChangeLeavesOutPoolWatchsOwnClearAndCountsTheGrowthAfterIt()
    {
        // A sample a minute: 8 GB, then a clear 6 minutes ago takes it to 5 GB within its 2 minutes, then it grows 75 MB a minute.
        const long MB = 1L << 20;
        var end = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        var h = new Core.History.History();
        for (int m = -20; m <= 0; m++)
        {
            long pool = m < -5 ? 8 * GB : m < -4 ? 6 * GB : 5 * GB + (m + 4) * 75 * MB;
            h.Add(new Sample(end.AddMinutes(m), new PoolTotals((ulong)pool, 0, 0, 0), [], []));
        }
        var clear = new ClearRecord
        {
            TimeUtc = end.AddMinutes(-6), Mode = ClearMode.Standby, Trigger = ClearTrigger.Schedule, Before = new PoolTotals((ulong)(8 * GB), 0, 0, 0),
            After = new PoolTotals((ulong)(5 * GB), 0, 0, 0),
        };
        var totals = UiData.Totals(h, [clear]);
        Assert.Equal(300 * MB, totals.Change10Min);                                  // not the 2.7 GB drop: that is the clear's result
        Assert.Equal(ChartClear.Of(clear), totals.Clear);
        Assert.Equal(-2.7, UiData.Totals(h, []).Change10Min / (double)GB, 1);         // without it, the drop would read as the pool falling

        var measuring = new ClearRecord { TimeUtc = end.AddMinutes(-1), Mode = ClearMode.Standby, Trigger = ClearTrigger.Manual, Before = new PoolTotals((ulong)(5 * GB), 0, 0, 0) };
        Assert.Equal(0, UiData.Totals(h, [clear, measuring]).Change10Min);           // no change is told while a clear is measured
    }

    [Fact]
    public void TheStatusTotalsAndTheTagRowsMeasureAcrossPoolWatchsOwnClearOnOneBaseline()
    {
        // A sample every 10 seconds for 70 minutes: Reff carries the pool's growth, 18 MB a minute, and 40 minutes in
        // PoolWatch's clear frees 1.5 GB of it. The total's change over the hour is its growth before the clear and after it
        // (the clear's drop added back, its 2 minutes the clear's): the same figure as Reff's row, not only the growth since
        // the clear, so one sentence never says "steady" of the total and "grew" of a tag from two baselines.
        const long MB = 1L << 20;
        const int count = 421, clearAfter = 240;
        var start = new DateTime(2026, 10, 28, 11, 0, 0, DateTimeKind.Utc);
        var reff = PoolTag.Parse("Reff");
        var h = Core.History.History.ForInterval(10);
        var clears = new List<ClearRecord>();
        for (int i = 0; i < count; i++)
        {
            long tag = 2 * GB + i * 3 * MB - (i > clearAfter ? 1536 * MB : 0);
            var t = start.AddSeconds(10 * i);
            h.Add(new Sample(t, new PoolTotals((ulong)(4 * GB + tag), 0, 0, 0), [new PoolTagEntry(reff, 0, 0, 0, 1, 0, (ulong)tag)], []));
            if (i == clearAfter)
                clears.Add(new ClearRecord { TimeUtc = t.AddSeconds(1), Mode = ClearMode.Standby, Trigger = ClearTrigger.Schedule, Before = h.Totals[^1] });
        }
        var detector = new Core.Detection.Detector(new Core.Settings());
        detector.Evaluate(h, new Core.Detection.OwnClears(clears, Analysis.CacheClearer.MeasureAfter));
        var row = detector.Status[reff];
        var totals = UiData.Totals(h, clears);
        Assert.Equal((row.Delta10m, row.Delta60m), (totals.Change10Min, totals.ChangeHour!.Value));
        long Total(int i) => (long)(h.Totals[i].CommittedPaged + h.Totals[i].CommittedNonPaged);
        int measured = h.IndexAtOrAfter(clears[0].TimeUtc + Analysis.CacheClearer.MeasureAfter);
        Assert.Equal(Total(clearAfter) - Total(count - 1 - 360) + Total(count - 1) - Total(measured), totals.ChangeHour);
        Assert.True(totals.ChangeHour > Total(count - 1) - Total(measured) + 500 * MB); // far more than the growth since the clear
        // With no clear, the plain change: the clear's drop would read as the pool falling.
        Assert.Equal(Total(count - 1) - Total(count - 1 - 360), UiData.Totals(h, []).ChangeHour);
    }

    [Fact]
    public void TheIssuesKernelPoolLineSaysWhatPoolWatchsOwnClearsFreedApartFromTheHoursChange()
    {
        // The issue text's kernel pool line reads the status line's totals (UiData.Totals): its change over the hour leaves out
        // what PoolWatch's own clears of that hour freed (their drop is added back), so the line says that apart, as their
        // result, whichever way the pool moved. A clear that freed nothing adds nothing, several are summed, and while a clear
        // is still being measured no change is told.
        const long MB = 1L << 20;
        var end = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        DateTime At(int k) => end.AddSeconds(10 * (k - 420));
        string Hm(int k) => At(k).AddSeconds(1).ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        // A sample every 10 seconds for 70 minutes; a clear a second after sample k, its record measuring what it freed.
        string Line(Func<int, long> pool, params (int After, long Freed)[] clearsAt)
        {
            var h = Core.History.History.ForInterval(10);
            var clears = new List<ClearRecord>();
            for (int i = 0; i <= 420; i++) h.Add(new Sample(At(i), new PoolTotals((ulong)pool(i), 0, 0, 0), [], []));
            foreach (var (k, freed) in clearsAt)
                clears.Add(new ClearRecord
                {
                    TimeUtc = At(k).AddSeconds(1), Mode = ClearMode.Standby, Trigger = ClearTrigger.Schedule,
                    Before = new PoolTotals((ulong)pool(k), 0, 0, 0), After = new PoolTotals((ulong)(pool(k) - freed), 0, 0, 0),
                });
            return ReportIssueDialog.KernelLine(UiData.Totals(h, clears));
        }
        // A steady 12 GB pool; the clear 20 minutes ago frees 1.3 GB, and the cache refills 150 MB of it after: the clear freed
        // far more than the hour's change, and is said apart from it, not as part of it.
        long Refill(int i) => i <= 300 ? 12 * GB : 12 * GB - 1331 * MB + Math.Clamp(i - 313, 0, 60) * 150 * MB / 60;
        Assert.Equal($"{Core.Detection.Fmt.Bytes(Refill(420))} (paged and non-paged pool), up 150 MB in the last hour; PoolWatch's own cache clear at {Hm(300)} freed 1.3 GB, not counted in that change",
            Line(Refill, (300, 1331 * MB)));
        // Only the hour's own clears: not one an hour and more ago, nor one dated after the newest sample (the clock was set back).
        Assert.EndsWith($"; PoolWatch's own cache clear at {Hm(300)} freed 1.3 GB, not counted in that change", Line(Refill, (300, 1331 * MB), (40, 400 * MB), (430, 500 * MB)));
        // Two clears in the hour: their sum, with no one time.
        long Two(int i) => Refill(i) - (i > 380 ? 300 * MB : 0);
        Assert.EndsWith($", up 150 MB in the last hour; PoolWatch's own cache clears freed {Core.Detection.Fmt.Bytes(1631 * MB)}, not counted in that change", Line(Two, (300, 1331 * MB), (380, 300 * MB)));
        // Down 2 GB on its own, and a clear frees 300 MB more; and a pool that is flat but for the clear's drop.
        long Down(int i) => 12 * GB - (i > 200 ? 2 * GB : 0) - (i > 300 ? 300 * MB : 0);
        Assert.EndsWith($", down 2.0 GB in the last hour; PoolWatch's own cache clear at {Hm(300)} freed 300 MB, not counted in that change", Line(Down, (300, 300 * MB)));
        long Flat(int i) => 12 * GB - (i > 300 ? 300 * MB : 0);
        Assert.EndsWith($", unchanged in the last hour; PoolWatch's own cache clear at {Hm(300)} freed 300 MB, not counted in that change", Line(Flat, (300, 300 * MB)));
        // A clear that freed nothing (the pool grew in its 2 minutes, or it freed under half a megabyte): nothing to say.
        long Grow(int i) => 12 * GB + i * MB;
        Assert.EndsWith(", up 360 MB in the last hour", Line(Grow, (300, -50 * MB)));
        Assert.EndsWith(", up 360 MB in the last hour", Line(Grow, (300, 100 << 10)));
        // A clear still being measured: no change is known across it yet.
        Assert.EndsWith($", change in the last hour not known yet: PoolWatch's own cache clear at {Hm(414)} is being measured", Line(Refill, (300, 1331 * MB), (414, 0)));
        // With no clear in the hour, before an hour is held, and before the first sample, as it always read.
        Assert.Equal("12.0 GB (paged and non-paged pool), up 2.4 GB in the last hour", ReportIssueDialog.KernelLine(new TotalsNow(12 * GB, TimeSpan.FromHours(2), 0, 2 * GB + 410 * MB)));
        Assert.Equal($"12.0 GB (paged and non-paged pool), up 360 MB in the last hour", Line(i => 12 * GB - 420 * MB + i * MB));
        Assert.Equal("12.0 GB (paged and non-paged pool), watched for less than an hour", ReportIssueDialog.KernelLine(new TotalsNow(12 * GB, TimeSpan.FromMinutes(30), 0, null)));
        Assert.Equal("not measured yet", ReportIssueDialog.KernelLine(new TotalsNow(null, TimeSpan.Zero, 0, null)));
    }

    [Fact]
    public void ASpanThatStartsInsidePoolWatchsOwnClearIsMeasuredFromItsMeasurement()
    {
        // A sample a minute: 8 GB, then a clear 11 minutes ago drops the pool to 5 GB (5.5 GB a minute later, 6 GB when it is
        // measured 2 minutes after it), then it grows 50 MB a minute. The last 10 minutes start inside the clear's interval:
        // measured from its measurement, not from its dip, the change is the 450 MB grown since.
        const long MB = 1L << 20;
        var end = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        var h = new Core.History.History();
        for (int m = -20; m <= 0; m++)
        {
            long pool = m < -11 ? 8 * GB : m == -11 ? 5 * GB : m == -10 ? 5 * GB + 512 * MB : 6 * GB + (m + 9) * 50 * MB;
            h.Add(new Sample(end.AddMinutes(m), new PoolTotals((ulong)pool, 0, 0, 0), [], []));
        }
        var clear = new ClearRecord { TimeUtc = end.AddMinutes(-11), Mode = ClearMode.Standby, Trigger = ClearTrigger.Schedule, Before = new PoolTotals((ulong)(8 * GB), 0, 0, 0) };
        Assert.Equal(450 * MB, UiData.Totals(h, [clear]).Change10Min);
    }

    [Fact]
    public void TheTrayIconTurnsRedOnlyForARealRisk()
    {
        Assert.Equal(TrayState.Red, UiData.State([(IncidentKind.NonPagedTotal, null)], retained: false));            // the non-paged pool over its limit
        Assert.Equal(TrayState.Red, UiData.State([(IncidentKind.Growth, Confidence.ConfirmedByTrace)], retained: false)); // a cause a trace confirmed
        Assert.Equal(TrayState.Amber, UiData.State([(IncidentKind.Growth, Confidence.StronglyCorrelated)], retained: false));
        Assert.Equal(TrayState.Amber, UiData.State([], retained: true));                                             // memory kept after its activity stopped
        Assert.Equal(TrayState.Green, UiData.State([], retained: false));                                            // a tag that only grows: watched, not alarming
    }

    [Fact]
    public void AClearOutsideThePlotIsALayoutProblem()
    {
        var plot = new Rectangle(40, 30, 300, 120);
        var layout = new ChartLayout(new Size(360, 180), new Rectangle(4, 4, 100, 18), Rectangle.Empty, plot, [], [], [new Rectangle(335, 30, 9, 9)]);
        Assert.Contains("clear mark", Assert.Single(layout.Problems()));
    }

    // ------------------------------------------------------------------ the Simple view

    private static readonly Recommendation SignIn = new(RecommendationId.StartAtSignIn, "Start PoolWatch when you sign in",
        "So it watches from the moment you sign in, without asking for permission each time.", "Turn on", RecommendationAction.Apply, 40);
    /// <summary>The guard's offer as the suggestions make it, its reason ending with their one-line trade-off.</summary>
    private static readonly Recommendation Guard = RecommendationRules.Current(new RecommendationInputs { NowUtc = DateTime.UtcNow, TokenSpawnLast24h = true }).Single();
    private static readonly Recommendation Services = new(RecommendationId.ReviewServices, "Review the background services",
        "Windows Search used noticeable processor time in the background over the last half hour.", "Review", RecommendationAction.OpenServices, 60);
    private static readonly Recommendation Report = new(RecommendationId.ReportIncident, "Report what PoolWatch found", "It grew.", "Report",
        RecommendationAction.ReportIssue, 50) { Subject = "20261028-0412-Reff" };

    private static (Form Host, SimpleView View) View(int width, int height, Func<DateTime>? utcNow = null)
    {
        var host = new Form { ClientSize = new Size(width, height) };
        var view = new SimpleView(utcNow) { Dock = DockStyle.Fill };
        host.Controls.Add(view);
        view.SetChart(new PoolChart());
        return (host, view);
    }

    private static IEnumerable<T> All<T>(Control root) where T : Control =>
        root.Controls.Cast<Control>().SelectMany(c => (c is T t ? [t] : Array.Empty<T>()).Concat(All<T>(c)));

    private static void Raise(Control c, string method, EventArgs e) =>
        c.GetType().GetMethod(method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, [e.GetType()])!.Invoke(c, [e]);

    [Theory]
    [InlineData(1000, 640, 3)]
    [InlineData(744, 400, 1)]   // the main window at its smallest, less its status line and footer
    [InlineData(744, 280, 0)]   // smaller still: the graph keeps its height, the suggestions wait for room
    public void UpToThreeCardsThatFitEachWithOneButtonAndNothingClipped(int width, int height, int atLeast)
    {
        var (host, view) = View(width, height);
        using var _ = host;
        view.ShowNextClear("Next cache clear in 23 min (sooner if the kernel pool reaches 8 GB). Last clear at 09:12 freed 4.7 GB, mostly ReFS file records.");
        view.ShowRecommendations([Guard, SignIn, Report, Services]);

        Assert.InRange(view.ShownCards.Count, atLeast, 3);
        Assert.Equal(new[] { Guard, SignIn, Report }[..view.ShownCards.Count], view.ShownCards); // the first ones, by priority
        Assert.Empty(view.LayoutProblems());
        if (atLeast == 3)
        {
            Assert.Equal(["Turn on", "Turn on", "Report"], All<Button>(view).Select(b => b.Text));
            Assert.Equal(3, All<LinkLabel>(view).Count(l => l.Text == "Hide"));
        }
        // The token-leak guard says what you will notice and how to undo it, and says the trade-off once.
        var texts = All<WrapLabel>(view).Select(l => l.Text).ToList();
        Assert.Contains(TokenGuardWords.Details, texts);
        Assert.Equal(1, texts.Sum(t => t.Split("to the front").Length - 1));
        Assert.DoesNotContain(SimpleSettings.ClearStutter, texts); // no card here clears the cache
    }

    [Fact]
    public void TheCardThatTurnsClearingOnSaysOnceThatAClearCanStutter()
    {
        var (host, view) = View(1000, 640);
        using var _ = host;
        var hourly = new Recommendation(RecommendationId.HourlyCacheClear, "Clear the cache every hour", "It frees what the kernel pool keeps of recently used files.",
            "Turn on", RecommendationAction.Apply, 50);
        view.ShowRecommendations([hourly, SignIn]);
        var texts = All<WrapLabel>(view).Select(l => l.Text).ToList();
        Assert.Equal(1, texts.Count(t => t == SimpleSettings.ClearStutter));
        Assert.Empty(view.LayoutProblems());
    }

    [Fact]
    public void ALongerLineAboutClearingLeavesTheCardsLessRoom()
    {
        var (host, view) = View(744, 400);
        using var _ = host;
        view.ShowNextClear("Automatic clearing is off.");
        view.ShowRecommendations([Guard, SignIn, Report]);
        int before = view.ShownCards.Count;
        view.ShowNextClear(string.Join(" ", Enumerable.Repeat("Last clear yesterday at 09:12 (light) freed nothing, so PoolWatch ran a full clear at 09:14: it freed 5.1 GB.", 5)));
        Assert.Empty(view.LayoutProblems());
        Assert.True(view.ShownCards.Count < before);
    }

    [Fact]
    public void AnAnswerStaysForAMinuteAfterItsCardHasGone()
    {
        var now = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        var (host, view) = View(1000, 640, () => now);
        using var _ = host;
        view.ShowRecommendations([SignIn, Services]);

        view.ShowOutcome(SignIn, ok: true, "PoolWatch starts when you sign in.");
        Assert.Null(view.ShownAnswer);                                     // its card still shows it
        view.ShowRecommendations([Services]);                              // the refresh after the change takes the card away
        Assert.Equal("PoolWatch starts when you sign in.", view.ShownAnswer);
        Assert.Equal([Services], view.ShownCards);
        Assert.Empty(view.LayoutProblems());

        now = now.AddSeconds(59);
        view.ShowRecommendations([Services]);
        Assert.NotNull(view.ShownAnswer);
        now = now.AddSeconds(1);
        view.ShowRecommendations([Services]);                              // the refresh a minute later
        Assert.Null(view.ShownAnswer);
        Assert.Empty(view.LayoutProblems());

        // An error too, in red: a change that went only part of the way can also take its card away.
        view.ShowOutcome(Services, ok: false, "That did not work: access denied");
        Assert.Null(view.ShownAnswer);                                     // its card still shows it
        view.ShowRecommendations([]);
        Assert.Equal("That did not work: access denied", view.ShownAnswer);
        Assert.Contains(All<WrapLabel>(view), l => l.Text == "That did not work: access denied" && l.ForeColor == Color.Firebrick);
        Assert.Empty(view.LayoutProblems());
    }

    [Fact]
    public void TheCardsAreRebuiltOnlyWhenWhatTheySayChanged()
    {
        var (host, view) = View(1000, 640);
        using var _ = host;
        view.ShowRecommendations([SignIn, Services]);
        var first = All<Button>(view).First();
        view.ShowRecommendations([SignIn with { }, Services with { }]); // equal: the same cards stay
        Assert.Same(first, All<Button>(view).First());
        view.ShowRecommendations([SignIn with { Why = "Something new." }, Services]);
        Assert.NotSame(first, All<Button>(view).First());
        view.ShowRecommendations([]);
        Assert.Empty(All<Button>(view));
    }

    [Fact]
    public void AButtonWaitsForItsAnswerAndHideIsPassedOn()
    {
        var (host, view) = View(1000, 640);
        using var _ = host;
        var pressed = new List<Recommendation>();
        var hidden = new List<Recommendation>();
        view.ActionClicked += pressed.Add;
        view.HideClicked += hidden.Add;
        view.ShowRecommendations([SignIn, Services]);

        var button = All<Button>(view).First();
        Raise(button, "OnClick", EventArgs.Empty);
        Assert.Equal([SignIn], pressed);
        Assert.False(button.Enabled);                                      // until the answer
        view.ShowOutcome(SignIn, ok: false, "PoolWatch could not set itself to start when you sign in.");
        Assert.True(button.Enabled);
        Assert.Contains(All<WrapLabel>(view), l => l.Text == "PoolWatch could not set itself to start when you sign in." && l.ForeColor == Color.Firebrick);
        Assert.Empty(view.LayoutProblems());

        var hide = All<LinkLabel>(view).Last();
        Raise(hide, "OnLinkClicked", new LinkLabelLinkClickedEventArgs(hide.Links[0]));
        Assert.Equal([Services], hidden);
    }
}
