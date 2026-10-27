using PoolWatch.Core;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Tags;

namespace PoolWatch.Tests;

/// <summary>
/// 2f (review round 2): every alert reaches the timeline, also the ones for an incident that has closed and the releases,
/// and an alert whose counted entry was evicted (the timeline keeps 500) starts a new entry instead of vanishing.
/// </summary>
public class TimelineRoundTwoTests
{
    private static readonly DateTime T0 = new(2026, 10, 26, 6, 34, 0, DateTimeKind.Utc);
    private static readonly PoolTag Reff = PoolTag.Parse("Reff");
    private const long MB = 1L << 20;

    private static Alert A(double minutes, long bytes, double window) =>
        new(AlertKind.Growth, Reff, T0.AddMinutes(minutes), $"grew > 200 MB in {window} min", bytes, TimeSpan.FromMinutes(window), T0);

    [Fact]
    public void AnAlertWhoseEntryWasEvictedStartsANewEntry()
    {
        var inc = new Incident { Id = "x", Dir = "x", Tag = Reff, Kind = IncidentKind.Growth, OnsetUtc = T0 };
        Assert.True(IncidentManager.NoteAlert(inc, A(0, 230 * MB, 1), "opened: grew > 200 MB in 1 min (+230 MB)"));
        Assert.True(IncidentManager.NoteAlert(inc, A(0.1, 230 * MB, 10)));                  // the 10-min rule's run: entry 1
        for (int w = 11; w < 509; w++) IncidentManager.NoteAlert(inc, A(0.1, 230 * MB, w)); // 498 other rules: 500 entries
        Assert.Equal(500, inc.Timeline.Count);
        IncidentManager.NoteAlert(inc, A(0.15, 230 * MB, 509));                             // the oldest alert entry (the 10-min run's) goes
        Assert.DoesNotContain(inc.Timeline, t => t.Text.StartsWith("alert: grew > 200 MB in 10 min", StringComparison.Ordinal));

        // The 10-min rule fires again within its run: it used to be counted on the evicted entry, where nobody sees it.
        Assert.True(IncidentManager.NoteAlert(inc, A(0.2, 231 * MB, 10)));
        Assert.Contains(inc.Timeline, t => t.Text == "alert: grew > 200 MB in 10 min (+231 MB)");
        Assert.False(IncidentManager.NoteAlert(inc, A(0.3, 232 * MB, 10)));                 // and is counted there from now on
        Assert.Contains(inc.Timeline, t => t.Text.StartsWith("alert: grew > 200 MB in 10 min (+231 MB); fired 1 more time", StringComparison.Ordinal));
        Assert.Equal(500, inc.Timeline.Count);
    }

    [Fact]
    public void AlertsAfterTheIncidentClosedAndItsReleaseAreOnItsTimeline()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pw-incidents-r2-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new Settings { GrowthRules = [new(1, 3 * Settings.MB)], IncidentQuietMinutes = 0.5, RetentionMinutes = 0.5, ReleaseBytes = 2 * Settings.MB, ReleaseWindowMinutes = 1 };
            var w = new World();
            var mgr = new IncidentManager(settings, new TagKnowledge(null, null), new FakeHost(dir), w.Registry) { RewriteInterval = TimeSpan.Zero };
            var det = new Detector(settings);
            w.AddProcess(4, 0, "System");
            w.AddProcess(100, 4, "leaker.exe");
            long file = 50 * Settings.MB;
            void Step() { w.Sample(("File", file)); mgr.Process(w.History, det, det.Evaluate(w.History), w.Now - w.Interval); }
            for (int i = 0; i < 15; i++) Step();
            for (int i = 0; i < 20; i++) { w.Io(100, other: 2000, handles: 2000); file += 800_000; Step(); }
            Assert.Single(mgr.Open);
            for (int i = 0; i < 25; i++) Step(); // growth stopped: closes after 30 s while the 1-min rule still fires
            var inc = Assert.Single(mgr.Closed);
            int closedAt = inc.Timeline.FindIndex(t => t.Text.StartsWith("closed", StringComparison.Ordinal));
            Assert.True(closedAt > 0);
            Assert.Contains(inc.Timeline.Skip(closedAt + 1), t => t.Text.StartsWith("alert after it closed: grew", StringComparison.Ordinal));

            file -= 16 * Settings.MB; // handles closed: a release
            for (int i = 0; i < 3; i++) Step();
            Assert.NotNull(inc.Release);
            Assert.Contains(inc.Timeline, t => t.Text.StartsWith("alert (release): ", StringComparison.Ordinal) && t.Text.Contains("(-"));
            mgr.Finish(w.History, w.Now);
            Assert.False(inc.CountPending); // written
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
