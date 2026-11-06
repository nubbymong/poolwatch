using System.Text.RegularExpressions;
using PoolWatch.Core;
using PoolWatch.Core.Boot;
using PoolWatch.Core.Cache;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Experiments;
using PoolWatch.Core.History;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;

using static PoolWatch.Tests.Clocks;

namespace PoolWatch.Tests;

/// <summary>
/// The intervals, ages and schedules of the core timed on the clock that only goes forward: each part is fed the same
/// samples twice, once with the system clock left alone and once with it set back and forward, and says the same both
/// times; where it gives times of day, they are as far apart as the forward clock says.
/// </summary>
public class ForwardClockTests
{
    private static readonly DateTime T0 = new(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);
    private const long MB = 1L << 20;
    private const long StepMs = 10_000;
    private static readonly PoolTag Leak = PoolTag.Parse("Leak");
    /// <summary>A tag that creeps up about a megabyte in 10 minutes: under the "growing" rate, unless its 10 minutes are mistimed.</summary>
    private static readonly PoolTag Slow = PoolTag.Parse("Slow");

    /// <summary>
    /// How far the system clock is from the forward clock at sample <paramref name="i"/> (10 s apart): back an hour while the
    /// first incident is open, forward 45 minutes in the release and two hours before the re-open, a day forward while the
    /// re-opened one is open,
    /// two hours back between its last I/O and its retention, an hour back after it closed.
    /// </summary>
    private static TimeSpan Shift(int i) =>
        TimeSpan.FromHours(i >= 100 ? -1 : 0) + TimeSpan.FromMinutes(i >= 305 ? 45 : 0) + TimeSpan.FromHours(i >= 365 ? 2 : 0) + TimeSpan.FromDays(i >= 390 ? 1 : 0)
        + TimeSpan.FromHours(i >= 450 ? -2 : 0) + TimeSpan.FromHours(i >= 600 ? -1 : 0);

    /// <summary>
    /// The Leak tag (non-paged) at sample <paramref name="i"/>: flat, a climb, flat (the incident closes), a fall (a release),
    /// flat, a second climb (a re-open), then flat for hours with one of PoolWatch's own clears that frees 50 MB.
    /// </summary>
    private static long Level(int i) => i < 60 ? 100 * MB : i < 150 ? (100 + (i - 59) * 4) * MB : i < 300 ? 460 * MB
        : i < 310 ? (460 - (i - 299) * 35) * MB : i < 330 ? 110 * MB : i < 400 ? (110 + (i - 329) * 4) * MB : i <= 1000 ? 390 * MB : 340 * MB;

    /// <summary>A writer that does I/O while the tag climbs and while it drops, and exits at sample 470: the incidents' suspect, its retention.</summary>
    private static long Io(int i) => (Math.Min(i, 150) - 60 > 0 ? (Math.Min(i, 150) - 60) * 500L + Math.Max(0, Math.Min(i, 400) - 330) * 500L : 0)
        + Math.Clamp(i - 295, 0, 15) * 300L; // busy while the tag drops too (the release's busy processes)

    /// <summary>A detector, incidents and a registry fed one sample at a time, the registry's clock at the sample's moment.</summary>
    private sealed class Rig : IDisposable
    {
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "pw-fwd-" + Guid.NewGuid().ToString("N"));
        public static readonly Settings Settings = new() { NonPagedTotalAlertBytes = 1L << 30 };
        public readonly History History = History.ForInterval(10);
        public readonly Detector Detector = new(Settings);
        public readonly IncidentManager Incidents;
        public readonly ProcessRegistry Registry;
        public readonly List<ClearRecord> Clears = [];
        private Moment _now;
        private readonly Func<int, long> _level;

        /// <param name="level">The Leak tag at each sample (<see cref="Level"/> when none is given).</param>
        public Rig(Func<int, long>? level = null)
        {
            _level = level ?? Level;
            Registry = new(clock: () => _now);
            Incidents = new(Settings, new TagKnowledge(null, null), new FakeHost(Dir), Registry);
        }

        public List<Alert> Step(int i, Moment at)
        {
            _now = at;
            long level = _level(i);
            var procs = i < 470
                ? new List<ProcessSample> { new() { Pid = 700, ParentPid = 4, CreateTime = T0.AddHours(-3).ToFileTimeUtc(), ImageName = "writer.exe", ReadOperationCount = Io(i) } }
                : [];
            // A deleter busy from sample 200 until it exits as the tag drops: the release counts it over its rule's window.
            if (i is >= 200 and <= 301) procs.Add(new() { Pid = 800, ParentPid = 4, CreateTime = T0.AddHours(-3).ToFileTimeUtc(), ImageName = "deleter.exe", OtherOperationCount = (i - 199) * 100L });
            Registry.ObserveSnapshot(procs, at.Utc);
            var s = new Sample(at.Utc, new PoolTotals(1UL << 30, (1UL << 30) + (ulong)level, 0, 0), [new PoolTagEntry(Leak, 0, 0, 0, (uint)(200 * i + 300 * Math.Max(0, Math.Min(i, 150) - 60) + 300 * Math.Max(0, Math.Min(i, 400) - 330)), (uint)(80 * i), (ulong)level), new PoolTagEntry(Slow, 0, 0, 0, 1, 0, (ulong)(300 * MB + i * 16L * 1024))], procs)
            {
                TickMs = at.Ms,
            };
            History.Add(s);
            if (i == 1000) Clears.Add(new ClearRecord { TimeUtc = at.Utc.AddSeconds(1), TickMs = at.Ms + 1000, Mode = ClearMode.Standby, Trigger = ClearTrigger.Manual, Before = default });
            Incidents.AddEvents([new LifecycleEvent { TimeUtc = at.Utc, Kind = LifecycleKind.Start, Pid = 5000 + i, Image = "x.exe" }], at);
            // Workers that exit as the tag drops (the release's exits).
            if (i is >= 296 and <= 305) Incidents.AddEvents([new LifecycleEvent { TimeUtc = at.Utc, Kind = LifecycleKind.Exit, Pid = 9000 + i, Image = "worker.exe" }], at);
            var alerts = Detector.Evaluate(History, new OwnClears(Clears, TimeSpan.FromMinutes(2)));
            Incidents.Process(History, Detector, alerts, at);
            return alerts;
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    private static string Said(string text) => Regex.Replace(text, @"\d\d:\d\d(:\d\d)?", "hh:mm");

    /// <summary>The report.md of each incident a rig holds, its times of day, dates and folder names taken out.</summary>
    private static List<string> Reports(Rig r) => r.Incidents.All.Select(i => Path.Combine(i.Dir, "report.md")).Where(File.Exists)
        .Select(p => Regex.Replace(Said(File.ReadAllText(p)), @"\d{8}-\d{6}(-\d+)?|\d{4}-\d\d-\d\d", "date")).ToList();

    [Theory]
    // The system clock set back 35 minutes before the release rule's window (sample 200) and forward an hour as the tag
    // begins to drop (sample 299).
    [InlineData(200, 299)]
    // Set back 35 minutes as the tag begins to drop (sample 299) and forward an hour inside the drop (sample 303).
    [InlineData(299, 303)]
    public void AReleaseIsMeasuredFromItsOnsetOnTheClockThatOnlyGoesForward(int setBackAt, int setForwardAt)
    {
        // The release's levels, its exits and the processes busy in it are found on the forward clock.
        TimeSpan Steps(int i) => TimeSpan.FromMinutes(i >= setBackAt ? -35 : 0) + TimeSpan.FromHours(i >= setForwardAt ? 1 : 0);
        using var steady = new Rig();
        using var stepped = new Rig();
        var held = Compare(steady, stepped, 340, Steps, holdAt: 339);
        var release = Assert.Single(held, x => x.Release is not null).Release!;
        Assert.True(release.Dropped >= 100 * MB, $"{release.Dropped}");
        // The processes busy in it (one gone before its end), and exits from before and after the clock was set.
        Assert.Contains(release.ActiveProcesses, p => p.Contains("writer.exe", StringComparison.Ordinal));
        Assert.Contains(release.ActiveProcesses, p => p.Contains("deleter.exe", StringComparison.Ordinal));
        Assert.True(release.Exits.Any(e => e.Contains("PID 9297", StringComparison.Ordinal)) && release.Exits.Any(e => e.Contains("PID 9300", StringComparison.Ordinal)),
            string.Join(" / ", release.Exits));
    }

    [Fact]
    public void TheDetectorsWindowsAndTheIncidentsIntervalsAreTimedOnTheClockThatOnlyGoesForward()
    {
        using var steady = new Rig();
        using var stepped = new Rig();
        var held = Compare(steady, stepped, 2900, Shift);
        // The run did what the test is for: a growth that closed, was released, re-opened and was retained, the total's
        // incident beside it, alerts counted after a close, and every incident let go after its 6 hours.
        var growth = Assert.Single(held, x => x.Kind == IncidentKind.Growth);
        Assert.Equal(1, growth.Reopened);
        Assert.NotNull(growth.Retention);
        Assert.Contains(growth.Timeline, t => t.Text.StartsWith("earlier episode: released", StringComparison.Ordinal));
        Assert.Contains(held, x => x.Kind == IncidentKind.NonPagedTotal && x.Timeline.Any(t => t.Text.StartsWith("alert after it closed", StringComparison.Ordinal)));
        Assert.Empty(steady.Incidents.Closed);
    }

    [Fact]
    public void AClosedIncidentsCountedAlertsAreWrittenEvery5MinutesOnTheClockThatOnlyGoesForward()
    {
        // 1.2 GB in 10 minutes, then flat: the incident closes 10 minutes after its last rise while the hour's rule still
        // fires for most of an hour, each repeat counted on one timeline entry and written at most every 5 minutes. The
        // system clock is set forward a day and later back two hours while those repeats are counted.
        static long Burst(int i) => i < 30 ? 100 * MB : i < 90 ? (100 + (i - 29) * 20) * MB : 1300 * MB;
        static TimeSpan Steps(int i) => TimeSpan.FromDays(i >= 170 ? 1 : 0) + TimeSpan.FromHours(i >= 260 ? -2 : 0);
        using var steady = new Rig(Burst);
        using var stepped = new Rig(Burst);
        var held = Compare(steady, stepped, 500, Steps, holdAt: 300);
        var inc = Assert.Single(held, x => x.Kind == IncidentKind.Growth);
        Assert.Contains(inc.Timeline, t => t.Text.StartsWith("alert after it closed: grew > 1.0 GB in 60 min", StringComparison.Ordinal) && t.Text.Contains("more time"));
    }

    [Fact]
    public void AnOwnClearIsPlacedOnTheSamplesByTheClockThatOnlyGoesForward()
    {
        // Samples 10 s apart; the system clock is set back an hour at sample 15 and forward a day at sample 30. A clear runs
        // just after sample 10: its interval is from sample 10 to the first sample 2 forward minutes after it (sample 23),
        // and it is being measured until then.
        static TimeSpan Steps(int i) => TimeSpan.FromHours(i >= 15 ? -1 : 0) + TimeSpan.FromDays(i >= 30 ? 1 : 0);
        var h = History.ForInterval(10);
        for (int i = 0; i < 40; i++) h.Add(new Sample(T0.AddSeconds(10 * i) + Steps(i), default, [], []) { TickMs = i * StepMs });
        var clear = new ClearRecord { TimeUtc = T0.AddSeconds(101) + Steps(10), TickMs = 101_000, Mode = ClearMode.Standby, Trigger = ClearTrigger.Manual, Before = default };
        var own = new OwnClears([clear], TimeSpan.FromMinutes(2));
        Assert.Equal(new ClearSpan(10, 23), Assert.Single(own.SpansIn(h).Spans));
        Assert.Same(clear, own.Measuring(220_000));
        Assert.Null(own.Measuring(221_000));
    }

    /// <summary>
    /// Feeds both rigs the same samples, the second with the system clock at <paramref name="shift"/> from the first, and
    /// holds that each sample's alerts and what the detector and incidents hold are the same; returns the incidents held at
    /// sample <paramref name="holdAt"/>.
    /// </summary>
    private static List<Incident> Compare(Rig steady, Rig stepped, int samples, Func<int, TimeSpan> shift, int holdAt = 1500)
    {
        List<Incident> held = [];
        for (int i = 0; i < samples; i++)
        {
            if (i == holdAt)
            {
                held = steady.Incidents.All.ToList();
                Assert.Equal(Reports(steady), Reports(stepped)); // the durations, rates and windows the reports say
            }
            long ms = 5_000_000 + i * StepMs;
            var a = steady.Step(i, At(T0.AddMilliseconds(i * StepMs), ms));
            var b = stepped.Step(i, At(T0.AddMilliseconds(i * StepMs) + shift(i), ms));
            string Alerts(List<Alert> x) => string.Join("|", x.Select(y => $"{y.Kind} {y.Tag} {y.Rule} {y.Bytes} {y.Window} {y.Ms} {y.OnsetMs}"));
            Assert.True(Alerts(a) == Alerts(b), $"sample {i}: alerts {Alerts(a)} / {Alerts(b)}");
            string State(Rig r) => string.Join(";",
                r.Incidents.Open.Count, r.Incidents.Closed.Count, r.Incidents.EventCount, r.Registry.ProtectSinceMs, r.Registry.ProtectUntilMs,
                r.Detector.Holding, r.History.Minutes.Count, r.History.Minutes.LastOrDefault()?.Ms,
                string.Join(",", r.Detector.Status.Values.OrderBy(t => t.Tag.Raw).Select(t => $"{t.Tag} {t.State} {t.Delta1m} {t.Delta10m} {t.Delta60m}")),
                string.Join(",", r.Incidents.All.Select(o => $"{o.Kind} {o.Timeline.Count} {Said(o.Timeline[^1].Text)}")),
                string.Join(",", r.Incidents.Open.Select(o => $"{o.Growth} {o.LastWriteMs} {o.LastRankMs} {o.QuietSinceMs}")),
                string.Join(",", r.Incidents.Closed.Select(o => $"{o.LastWriteMs} {o.ClosedMs} {o.Retention is null}")));
            Assert.True(State(steady) == State(stepped), $"sample {i}: {State(steady)} / {State(stepped)}");
        }
        string Incident(Incident x) => $"{x.Kind} {x.Reopened} {x.OpenedMs} {x.ClosedMs} {x.LastRiseMs} {x.OnsetMs} {x.Growth} {x.Retention is null} {x.Release is null} " +
                                         $"{x.EndMs} {x.GrowthSpan} {x.Events.Count} {x.Window?.FromMs} {x.Window?.ToMs} " +
                                         (x.Release is { } r ? $"{r.FromBytes} {r.ToBytes} {r.Dropped} {string.Join("/", r.Exits.Select(Said))} {string.Join("/", r.ActiveProcesses.Select(Said))} " : "") +
                                         string.Join(" / ", x.Timeline.Select(t => Said(t.Text)));
        Assert.Equal(steady.Incidents.All.Select(Incident), stepped.Incidents.All.Select(Incident));
        return held;
    }

    [Fact]
    public void TheAutomaticClearsSpacingScheduleAndBackOffAreTimedOnTheClockThatOnlyGoesForward()
    {
        // The last clear ran at the forward clock's 0 (and, once, it was a pool-rule clear that freed little). Whatever the
        // system clock says, a clear is due by the forward clock's 30 minutes, 1 hour and 6 hours, and the next one is said
        // as far ahead of now as the forward clock says.
        var s = new Settings { AutoClearEnabled = true, AutoClearEveryHours = 1, AutoClearMinMinutes = 30, AutoClearPoolGb = 8 };
        var big = new PoolTotals(5UL << 30, 5UL << 30, 0, 0);
        var small = new PoolTotals(1UL << 30, 1UL << 30, 0, 0);
        var weak = new ClearRecord { TimeUtc = T0, TickMs = 0, Mode = ClearMode.Standby, Trigger = ClearTrigger.PoolOverLimit, Before = big, After = big };
        foreach (var shift in new[] { TimeSpan.Zero, TimeSpan.FromDays(1), -TimeSpan.FromHours(3) })
        {
            Moment At(double minutes) => new(T0.AddMinutes(minutes) + shift, (long)(minutes * 60_000));
            Assert.Null(ClearPolicy.Due(s, big, At(29), 0, -60_000));                                          // the spacing
            Assert.Null(ClearPolicy.Due(s, small, At(59), 0, -60_000));                                        // the hour
            Assert.Equal(ClearTrigger.Schedule, ClearPolicy.Due(s, small, At(60), 0, -60_000));
            var sizeOnly = new Settings { AutoClearEnabled = true, AutoClearEveryHours = 0, AutoClearMinMinutes = 30, AutoClearPoolGb = 8 };
            Assert.Null(ClearPolicy.Due(sizeOnly, big, At(5 * 60), null, 0, weak));                                // the back-off
            Assert.Equal(ClearTrigger.PoolOverLimit, ClearPolicy.Due(sizeOnly, big, At(6 * 60 + 1), null, 0, weak));
            var next = ClearPolicy.Next(s, small, At(10), 0, -60_000, null, true, false, false, false);
            Assert.Equal(TimeSpan.FromMinutes(50), next.AtUtc - At(10).Utc);
            Assert.Equal(TimeSpan.FromMinutes(20), next.PoolRuleAfterUtc - At(10).Utc); // the size rule waits out the spacing
            var bySize = ClearPolicy.Next(s, big, At(10), 0, -60_000, null, true, false, false, false);
            Assert.Equal(TimeSpan.FromMinutes(20), bySize.AtUtc - At(10).Utc); // over the size: when the spacing ends
        }
    }

    [Fact]
    public void AnExperimentsPhasesAreTimedOnTheClockThatOnlyGoesForward()
    {
        // Phases of 5 minutes, a sample a minute; the system clock is set back an hour in the first phase and forward a day in
        // the second: the phases change at the forward clock's 5 and 10 minutes, and the minutes left are its own.
        Sample S(int minute, TimeSpan shift) => new(T0.AddMinutes(minute) + shift, default, [], []) { TickMs = minute * 60_000L };
        TimeSpan ShiftAt(int minute) => minute < 2 ? TimeSpan.Zero : minute < 7 ? -TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
        var e = new Experiment("ChatGPT", TimeSpan.FromMinutes(5), T0);
        var lag = new ClearLagExperiment(TimeSpan.FromMinutes(5), T0);
        var phases = new List<string>();
        for (int m = 0; m <= 16; m++)
        {
            if (e.Observe(S(m, ShiftAt(m))) is { } step) phases.Add($"{m}:{step.Phase}");
            if (lag.Observe(S(m, ShiftAt(m))) is { } l) phases.Add($"{m}:lag {l.step.Phase}");
            if (m == 3) Assert.EndsWith("2 min left", e.Status(m * 60_000L));
            if (m == 8) Assert.EndsWith("2 min left", lag.Status(m * 60_000L));
        }
        Assert.Equal(["0:Baseline", "0:lag Baseline", "5:AppRunning", "5:lag AfterClose", "10:AfterClose", "10:lag Done", "15:Done"], phases);
    }

    [Fact]
    public void TheMarkInboxRelistsEveryMinuteOnTheClockThatOnlyGoesForward()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pw-marks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var stamp = new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);
            var inbox = new MarkInbox(dir, p => p, _ => stamp); // the folder never changes: only the safety net lists it
            inbox.Take(0);
            inbox.Take(59_000);
            Assert.Equal(1, inbox.Listings);
            inbox.Take(60_000);
            Assert.Equal(2, inbox.Listings);
            Assert.Equal(TimeSpan.FromSeconds(60), MarkInbox.RelistEvery);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// The boot summary is written every 10 minutes on the clock that only goes forward, whatever the system clock does
    /// (set back an hour, then forward a day). Each write is counted where the tracker writes it, through its write seam
    /// (the step every write of summary.json takes, just before the file is put in place), and a Flush that returns no
    /// error put it there: never by comparing file times, which a file system gives only to a granularity of its own, so
    /// two writes close together could look like one.
    /// </summary>
    [Fact]
    public void TheBootSummaryIsDueEvery10MinutesOnTheClockThatOnlyGoesForward()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pw-boots-" + Guid.NewGuid().ToString("N"));
        try
        {
            int writes = 0;
            var tr = new BootTracker(dir, p =>
            {
                if (Path.GetFileName(p) == "summary.json") writes++;
                return p;
            });
            Sample S(int minute, TimeSpan shift) => new(T0.AddMinutes(minute) + shift, new PoolTotals(1UL << 30, 1UL << 30, 0, 0),
                [new PoolTagEntry(Leak, 0, 0, 0, 1, 0, 1UL << 20)], []) { TickMs = minute * 60_000L };
            var written = new List<int>();
            for (int m = 0; m <= 25; m++)
            {
                var shift = m < 3 ? TimeSpan.Zero : m < 12 ? -TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
                tr.Observe(S(m, shift), TimeSpan.FromHours(5) + TimeSpan.FromMinutes(m), 0);
                int before = writes;
                Assert.Null(tr.Flush(m * 60_000L));
                if (writes > before) written.Add(m);
                Assert.True(writes - before <= 1);
            }
            Assert.Equal([0, 10, 20], written);
            Assert.Single(Directory.EnumerateFiles(dir, "summary.json", SearchOption.AllDirectories));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void TheRegistryKeepsAnExitedRecordByTheClockThatOnlyGoesForward()
    {
        // A short-lived command exits; the registry forgets it 15 minutes on by the forward clock: not at once when the
        // system clock is set forward a day, and not an hour late when it is set back an hour.
        var now = At(T0, 0);
        var reg = new ProcessRegistry(clock: () => now);
        for (int k = 0; k < 300; k++) reg.OnStart(2000 + k, 4, T0.ToFileTimeUtc() + k, "x.exe", null, null, null, T0); // over the 256 a prune needs
        long ct = T0.ToFileTimeUtc() + 10_000;
        reg.OnStart(9000, 4, ct, "git.exe", null, null, null, T0);
        reg.OnStop(9000, ct, 0, T0.AddMilliseconds(80));
        var key = new ProcessKey(9000, ct);
        now = At(T0.AddDays(1), 14 * 60_000);
        reg.ObserveSnapshot([], now.Utc);
        Assert.NotNull(reg.Get(key));
        now = At(T0.AddDays(1).AddHours(-1).AddMinutes(2), 16 * 60_000);
        reg.ObserveSnapshot([], now.Utc);
        Assert.Null(reg.Get(key));
    }

    [Fact]
    public void TheForwardClocksSchedulesCachesAndWaitsIgnoreTheSystemClock()
    {
        var every = new Every(TimeSpan.FromMinutes(10));
        Assert.True(every.Due(At(T0, 0)));
        Assert.False(every.Due(At(T0.AddDays(1), 9 * 60_000)));
        Assert.True(every.Due(At(T0.AddHours(-1), 10 * 60_000)));
        every.NextIn(At(T0, 10 * 60_000), TimeSpan.FromMinutes(1));
        Assert.False(every.IsDue(At(T0.AddDays(2), 10 * 60_000 + 59_000)));
        Assert.True(every.IsDue(At(T0.AddHours(-5), 11 * 60_000)));

        var end = Deadline.After(At(T0, 1000), TimeSpan.FromSeconds(5));
        Assert.False(end.Passed(At(T0.AddDays(1), 5999)));
        Assert.True(end.Passed(At(T0.AddHours(-1), 6000)));

        long ms = 0;
        var utc = T0;
        int made = 0;
        var kept = new Kept<int>(TimeSpan.FromMinutes(5), () => ++made, () => new Moment(utc, ms));
        Assert.Equal(1, kept.Value);
        (utc, ms) = (T0.AddDays(1), 4 * 60_000);
        Assert.Equal(1, kept.Value);
        (utc, ms) = (T0.AddHours(-1), 5 * 60_000);
        Assert.Equal(2, kept.Value);

        int looks = 0;
        Assert.False(Waits.While(() => true, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1), () => new Moment(utc, ms), _ =>
        {
            looks++;
            ms += 1000;
            utc = looks % 2 == 0 ? utc.AddDays(1) : utc.AddHours(-3);
        }));
        Assert.Equal(3, looks);
    }

    [Fact]
    public void ATimeReadOntoTheForwardClockAndBackIsAsFarApartAsTheForwardClockSays()
    {
        var at = new Moment(T0, 100_000);
        Assert.Equal(40_000, at.MsOf(T0.AddSeconds(-60)));
        Assert.Equal(100_000, at.MsOf(T0.AddHours(1))); // a time after the moment (only a clock set back since gives one)
        Assert.Equal(T0.AddSeconds(-30), at.UtcOf(70_000));
        Assert.Equal(T0.AddSeconds(30), at.UtcOf(130_000));
    }
}
