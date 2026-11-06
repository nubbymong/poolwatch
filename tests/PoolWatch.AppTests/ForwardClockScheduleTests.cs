using System.Text.Json;
using PoolWatch.Analysis;
using PoolWatch.Capture;
using PoolWatch.Core;
using PoolWatch.Core.Cache;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Identity;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Engine;
using PoolWatch.Engine.Setup;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// The app's schedules, windows and rates timed on the clock that only goes forward: each is given the same moments with the
/// system clock left alone and set back or forward, and does the same both times.
/// </summary>
public sealed class ForwardClockScheduleTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);
    private const long MB = 1L << 20, GB = 1L << 30;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pw-fwd-sched-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void TheActiveChangesCountIsReadEvery5MinutesOnTheForwardClock()
    {
        var c = new TwoClocks();
        var s = new ChangeCountSchedule();
        Assert.True(s.Start(c.Now()));
        s.Done();
        c.Pass(TimeSpan.FromMinutes(4));
        c.SetSystemClock(TimeSpan.FromDays(1));                     // set forward a day: not read early
        Assert.False(s.Start(c.Now()));
        c.Pass(TimeSpan.FromMinutes(1));
        c.SetSystemClock(-TimeSpan.FromDays(2));                    // set back: read at its 5 minutes all the same
        Assert.True(s.Start(c.Now()));
        s.Done();
        c.Pass(TimeSpan.FromSeconds(20));
        c.SetSystemClock(TimeSpan.FromHours(1));
        s.Activated(c.Now());                                       // back to the front 20 s after a read: still fresh
        Assert.False(s.Start(c.Now()));
        c.Pass(TimeSpan.FromSeconds(20));
        c.SetSystemClock(-TimeSpan.FromHours(3));
        s.Activated(c.Now());                                       // 40 s after it: read again
        Assert.True(s.Start(c.Now()));
    }

    [Fact]
    public void TheTasksAreReadAgainAfterTheirWaitOnTheForwardClock()
    {
        var c = new TwoClocks();
        var clock = new TaskReadClock(TasksTab.ReadEvery);
        Assert.True(clock.Start(c.Now()));
        clock.Finished(null);
        c.Pass(TasksTab.ReadEvery - TimeSpan.FromSeconds(1));
        c.SetSystemClock(TimeSpan.FromDays(1));
        Assert.False(clock.Due(c.Now()));
        c.Pass(TimeSpan.FromSeconds(1));
        c.SetSystemClock(-TimeSpan.FromDays(3));
        Assert.True(clock.Due(c.Now()));
    }

    [Fact]
    public void TheTagRankingsAreSpacedOnTheForwardClock()
    {
        var c = new TwoClocks();
        var rankings = new TagRankings(c.Now);
        var a = PoolTag.Parse("Reff");
        void Rank()
        {
            using var done = new ManualResetEventSlim();
            Assert.NotNull(rankings.Pump(_ => null, [a], null, done.Set));
            Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
        }
        Rank();
        c.Pass(RankPlan.Fresh - TimeSpan.FromSeconds(1));
        c.SetSystemClock(TimeSpan.FromDays(1));                     // set forward a day: not ranked again early
        Assert.Null(rankings.Pump(_ => null, [a], null, () => { }));
        c.Pass(TimeSpan.FromSeconds(1));
        c.SetSystemClock(-TimeSpan.FromHours(30));                  // set back: ranked again at its 5 minutes
        Rank();
    }

    [Fact]
    public void TheIncidentsListedAreThoseOfTheLastWeekOnTheForwardClock()
    {
        var c = new TwoClocks();
        IncidentRow Row(string id, bool open, long openedMs) =>
            new(id, id, T0, "Reff", IncidentKind.Growth, GB, "", "", open) { OpenedMs = openedMs };
        var rows = new[] { Row("recent", false, c.Ms - (long)TimeSpan.FromDays(6).TotalMilliseconds), Row("old", false, c.Ms - (long)TimeSpan.FromDays(8).TotalMilliseconds),
            Row("open", true, c.Ms - (long)TimeSpan.FromDays(30).TotalMilliseconds) };
        foreach (var set in new[] { TimeSpan.Zero, TimeSpan.FromDays(10), -TimeSpan.FromDays(10) })
        {
            var now = new Moment(c.Utc + set, c.Ms);
            Assert.Equal(["recent", "open"], PoolCauses.Shown(rows, now).Select(r => r.Id));
        }
    }

    /// <summary>A sample of one process with its handles and I/O, 10 s after the one before.</summary>
    private static Sample S(DateTime utc, long ms, int handles, long ops) =>
        new(utc, default, [], [new ProcessSample { Pid = 700, ParentPid = 4, CreateTime = T0.AddDays(-1).ToFileTimeUtc(), ImageName = "leaky.exe", HandleCount = handles, OtherOperationCount = ops }])
        { TickMs = ms };

    [Fact]
    public void AProcesssRiseAndRateAreTimedOnTheForwardClock()
    {
        // A process opens 100 handles and does 1,000 I/O ops every 10 s for 50 minutes (all the raw history holds); the system
        // clock set back an hour 30 minutes in and forward a day at the last sample. Its rise, how long it took and its rate
        // are those of the samples on the forward clock.
        string Candidates(Func<int, TimeSpan> shift)
        {
            var h = Core.History.History.ForInterval(10);
            Sample? previous = null, latest = null;
            for (int i = 0; i < 300; i++)
            {
                previous = latest;
                latest = S(T0.AddSeconds(10 * i) + shift(i), 1_000_000 + i * 10_000L, 100 * i, 1000L * i);
                h.Add(latest);
            }
            var list = PoolCauses.ProcessCandidates(new SamplePair(previous, latest!), h, new ProcessRegistry());
            var rows = UiData.Processes(latest, previous, new ProcessRegistry());
            return string.Join(";", list.Select(p => $"{p.Name} {p.Handles} {p.HandlesBefore} {p.Over} {p.OpsPerSec}")) + " | " +
                   string.Join(";", rows.Select(r => $"{r.Name} {r.OpsPerSec}"));
        }
        string steady = Candidates(_ => TimeSpan.Zero);
        Assert.Equal("leaky.exe 29900 0 00:49:50 100 | leaky.exe 100", steady);
        Assert.Equal(steady, Candidates(i => (i >= 180 ? -TimeSpan.FromHours(1) : TimeSpan.Zero) + (i == 299 ? TimeSpan.FromDays(1) : TimeSpan.Zero)));
    }

    [Fact]
    public void WhetherAProcessBecameAnOrphanWhilePoolWatchWatchedIsTimedOnTheForwardClock()
    {
        // A crawler and its shell are first seen together; the shell exits a minute later on the forward clock, the system
        // clock set back an hour in between; 30 s on the crawler is an orphan. It became one a minute after PoolWatch first
        // saw it (by their times of day, 59 minutes before), so with 1,000 I/O operations a second it stands out.
        var now = new Moment(T0, 1_000_000);
        var registry = new ProcessRegistry(clock: () => now);
        var history = Core.History.History.ForInterval(10);
        var shell = new ProcessSample { Pid = 600, ParentPid = 4, CreateTime = T0.AddHours(-1).ToFileTimeUtc(), ImageName = "bash.exe" };
        ProcessSample Crawler(long ops) => new() { Pid = 601, ParentPid = 600, CreateTime = T0.AddMinutes(-30).ToFileTimeUtc(), ImageName = "find.exe", OtherOperationCount = ops };
        Sample Snapshot(TimeSpan systemClockSet, long ops, bool withShell)
        {
            now = new Moment(now.Utc + TimeSpan.FromSeconds(10) + systemClockSet, now.Ms + 10_000);
            List<ProcessSample> live = withShell ? [shell, Crawler(ops)] : [Crawler(ops)];
            registry.ObserveSnapshot(live, now.Utc);
            var s = new Sample(now.Utc, default, [], live) { TickMs = now.Ms };
            history.Add(s);
            return s;
        }
        for (int i = 0; i < 6; i++) Snapshot(i == 3 ? -TimeSpan.FromHours(1) : TimeSpan.Zero, 0, withShell: true);
        registry.OnStop(600, shell.CreateTime, 0, now.Utc);
        Sample? previous = null, latest = null;
        for (int i = 0; i < 4; i++) (previous, latest) = (latest, Snapshot(TimeSpan.Zero, 10_000L * i, withShell: false));
        Assert.True(registry.Get(Crawler(0).Key)!.IsOrphan);
        var found = Assert.Single(PoolCauses.ProcessCandidates(new SamplePair(previous, latest!), history, registry));
        Assert.Equal(("find.exe", true, 1000L), (found.Name, found.BecameOrphan, found.OpsPerSec));
    }

    [Fact]
    public void AnIncidentsRateInPoolCausesIsTimedOnTheForwardClock()
    {
        // 1 GB in 30 minutes on the forward clock: an open incident 30 minutes after its onset, with the system clock set
        // forward a day since, and a closed one that ended 30 minutes after its onset, with the clock set back two hours in
        // between. Both grew 2 GB an hour.
        IncidentRow Row(bool open) => new("x", "x", T0, "Reff", IncidentKind.Growth, GB, "", "", open)
        {
            OnsetUtc = T0, OnsetMs = 1_000_000,
            EndUtc = open ? null : T0.AddMinutes(30) - TimeSpan.FromHours(2), EndMs = open ? null : 1_000_000 + 30 * 60_000,
        };
        var open = PoolCauses.ForIncident(Row(open: true), "a", T0.AddDays(1).AddMinutes(30), 1_000_000 + 30 * 60_000);
        var closed = PoolCauses.ForIncident(Row(open: false), "a", T0.AddHours(3), 1_000_000 + 5 * 3_600_000);
        Assert.Equal(2.0 * GB, open.PerHourBytes!.Value, 3);
        Assert.Equal(2.0 * GB, closed.PerHourBytes!.Value, 3);
    }

    [Fact]
    public void TheStatusLinesChangesAreTimedOnTheForwardClock()
    {
        // The pool grows 3 MB every 10 s for 2 hours; the system clock is set back 30 minutes 40 minutes before the end.
        TotalsNow Totals(TimeSpan setBack)
        {
            var h = Core.History.History.ForInterval(10);
            for (int i = 0; i < 720; i++)
                h.Add(new Sample(T0.AddSeconds(10 * i) + (i >= 480 ? setBack : TimeSpan.Zero), new PoolTotals((ulong)(4 * GB + i * 3 * MB), 0, 0, 0), [], [])
                { TickMs = 1_000_000 + i * 10_000L });
            return UiData.Totals(h, []);
        }
        var steady = Totals(TimeSpan.Zero);
        Assert.Equal((60 * 3 * MB, 360 * 3 * MB), (steady.Change10Min, steady.ChangeHour!.Value));
        Assert.Equal(steady, Totals(-TimeSpan.FromMinutes(30)));
        Assert.Equal(steady, Totals(TimeSpan.FromHours(2)));
    }

    [Fact]
    public void TheHeaderSaysPoolWatchsOwnClearFor10MinutesOnTheForwardClock()
    {
        var clear = new ChartClear(T0, 2 * GB, false, false, true) { TickMs = 1_000_000 };
        Assert.NotNull(MainForm.RecentOwnClear(clear, new Moment(T0.AddHours(2), 1_000_000 + 9 * 60_000)));   // set forward 2 h
        Assert.Null(MainForm.RecentOwnClear(clear, new Moment(T0.AddHours(-1), 1_000_000 + 11 * 60_000)));     // set back an hour
        Assert.Null(MainForm.RecentOwnClear(null, new Moment(T0, 1_000_000)));
    }

    [Fact]
    public void TheVersionCrashStrayGitAndFilterChecksAreDueOnTheForwardClock()
    {
        // The checks at every 10 s sample for 2 hours 13 minutes, twice: the second time with the system clock set back an
        // hour at 20 minutes, forward a day at 50 minutes (between two version checks and two instance checks: by the system
        // clock both would be nearly a day old there, past their hour) and back two hours at 2 hours 3 minutes (between the
        // instances an incident asked for at sample 400 and their next hour). They fall due at the same samples.
        var settings = new Settings { VersionCheckMinutes = 60, StrayGitCheckMinutes = 30, ServiceIntervalSeconds = 60 };
        List<string> Due(Func<int, TimeSpan> shift)
        {
            var checks = new CheckSchedule();
            var due = new List<string>();
            for (int i = 0; i < 800; i++)
            {
                var s = new Sample(T0.AddSeconds(10 * i) + shift(i), default, [], []) { TickMs = 1_000_000 + i * 10_000L };
                var d = checks.At(s, settings, instancesAsked: i == 400);
                if (d.Crash && i == 30) checks.CrashPageFull(s);                     // a log that filled its page at the second check
                checks.ReadFilters(s, d, timeline: () => { }, services: () => { }, instances: () => { }); // read those due
                if (d.Version || d.Crash || d.StrayGit || d.FilterServices || d.FilterInstances) due.Add($"{i} {d}");
            }
            return due;
        }
        var steady = Due(_ => TimeSpan.Zero);
        bool At(int i, string what) => steady.Any(d => d.StartsWith($"{i} ", StringComparison.Ordinal) && d.Contains($"{what} = True", StringComparison.Ordinal));
        Assert.Contains("0 Due { Version = True, Crash = True, StrayGit = True, FilterServices = True, FilterInstances = True }", steady);
        Assert.True(At(30, "Crash") && At(36, "Crash"));                               // 5 minutes on, and its next page a minute later
        Assert.True(At(360, "Version") && At(720, "Version") && !At(300, "Version"));
        Assert.True(At(360, "FilterInstances") && At(400, "FilterInstances") && At(760, "FilterInstances") && !At(720, "FilterInstances"));
        Assert.Equal(steady, Due(i => (i >= 120 ? -TimeSpan.FromHours(1) : TimeSpan.Zero) + (i >= 300 ? TimeSpan.FromDays(1) : TimeSpan.Zero)
            + (i >= 740 ? -TimeSpan.FromHours(2) : TimeSpan.Zero)));
    }

    /// <summary>Sample <paramref name="i"/> of the filter-check tests, 10 s apart: the system clock set forward a day at sample 1 and back two days at sample 7.</summary>
    private static Sample FilterSample(int i) =>
        new(T0.AddSeconds(10 * i) + (i >= 1 ? TimeSpan.FromDays(1) : TimeSpan.Zero) + (i >= 7 ? -TimeSpan.FromDays(2) : TimeSpan.Zero), default, [], [])
        { TickMs = 1_000_000 + i * 10_000L };

    [Fact]
    public void TheFilterChecksStayDueUntilTheyAreRead()
    {
        // A sample that failed before it read the filter manager's services and instances: both are due at the next sample,
        // whether the instances fell due because an incident asked for them or by their hour, and no longer once they were
        // read there; the services are due again 60 s after, on the forward clock.
        var settings = new Settings { ServiceIntervalSeconds = 60 };
        foreach (bool asked in new[] { true, false })
        {
            var checks = new CheckSchedule();
            var first = checks.At(FilterSample(0), settings, instancesAsked: asked);
            Assert.True(first.FilterServices && first.FilterInstances);
            var next = checks.At(FilterSample(1), settings, instancesAsked: asked);
            Assert.True(next.FilterServices && next.FilterInstances);
            checks.ReadFilters(FilterSample(1), next, timeline: () => { }, services: () => { }, instances: () => { });
            var after = checks.At(FilterSample(2), settings, instancesAsked: false);
            Assert.False(after.FilterServices || after.FilterInstances);
            Assert.False(checks.At(FilterSample(6), settings, instancesAsked: false).FilterServices); // 50 s after they were read
            Assert.True(checks.At(FilterSample(7), settings, instancesAsked: false).FilterServices);  // 60 s after
        }
    }

    [Fact]
    public void TheFilterServicesAndInstancesAreCountedAsReadAfterTheTimelineJustBeforeEachIsRead()
    {
        // The sampler reads the filter timeline first, then the services and the instances due. A timeline that throws
        // leaves both due at the next sample. There an instance query that throws is still counted as read: it waits for its
        // next hour, not the next sample.
        var settings = new Settings { ServiceIntervalSeconds = 60 };
        var checks = new CheckSchedule();
        var read = new List<string>();
        var due = checks.At(FilterSample(0), settings, instancesAsked: false);
        Assert.Throws<InvalidOperationException>(() => checks.ReadFilters(FilterSample(0), due,
            timeline: () => throw new InvalidOperationException("event log"), services: () => read.Add("services"), instances: () => read.Add("instances")));
        Assert.Empty(read);
        due = checks.At(FilterSample(1), settings, instancesAsked: false);
        Assert.True(due.FilterServices && due.FilterInstances);
        Assert.Throws<InvalidOperationException>(() => checks.ReadFilters(FilterSample(1), due,
            timeline: () => read.Add("timeline"), services: () => read.Add("services"),
            instances: () => { read.Add("instances"); throw new InvalidOperationException("instances"); }));
        Assert.Equal(["timeline", "services", "instances"], read);
        var after = checks.At(FilterSample(2), settings, instancesAsked: false);
        Assert.False(after.FilterServices || after.FilterInstances);
    }

    [Fact]
    public void TheSamplersFilterPartReadsTheEventRecordsFirstAndCountsEachCheckAsReadAsItIsRead()
    {
        // The sampler's filter part (Diagnostics.ReadFilters), its sources faked, with an incident asking for the instances.
        // At sample 0 the event-log records fail to read: the services and the instances are not read and stay due. At
        // sample 1 the records are read into the timeline first, then the services, whose query throws and is logged, then
        // the instances, the incident's request taken just before their query. Neither is due at sample 2.
        var settings = new Settings { ServiceIntervalSeconds = 60 };
        var checks = new CheckSchedule();
        var filters = new Core.Filters.FilterTimeline();
        var record = new Core.Filters.LogEvent(FilterSample(1).TimeUtc, "System", Core.Filters.FilterTimeline.FilterManagerProvider, 6,
            "File System Filter 'bindflt' (Version 10.0) has successfully loaded and registered with Filter Manager.", []);
        var read = new List<string>();
        var logged = new List<string>();
        var sources = new Analysis.Diagnostics.FilterSources(
            EventRecords: () =>
            {
                read.Add("records");
                if (read.Count == 1) throw new InvalidOperationException("event log");
                return [record];
            },
            Services: () =>
            {
                read.Add("services");
                throw new InvalidOperationException("no services");
            },
            Instances: () =>
            {
                read.Add("instances");
                return (new List<Core.Filters.FilterInstance>(), null);
            });
        List<Core.Findings.Finding> Read(int i) => Analysis.Diagnostics.ReadFilters(checks, FilterSample(i), checks.At(FilterSample(i), settings, instancesAsked: true),
            filters, [], () => [], sources, instancesTaken: () => read.Add("request taken"), logged.Add);
        Assert.Throws<InvalidOperationException>(() => Read(0));
        Assert.Equal(["records"], read);
        var found = Read(1);
        Assert.Equal(["records", "records", "services", "request taken", "instances"], read);
        Assert.Contains(found, f => f.Kind == Core.Findings.FindingKind.FilterEvent && f.Image == "bindflt");
        Assert.Equal(["service state query failed: no services"], logged);
        var after = checks.At(FilterSample(2), settings, instancesAsked: false);
        Assert.False(after.FilterServices || after.FilterInstances);
    }

    /// <summary>
    /// The system clock at sample <paramref name="i"/> (10 s apart) in the two schedule tests below: back an hour at sample
    /// 87 (between two minutes' reads), forward a day at sample 200 (between two hours') and back two days at sample 503.
    /// </summary>
    private static TimeSpan Stepped(int i) => (i >= 87 ? -TimeSpan.FromHours(1) : TimeSpan.Zero) + (i >= 200 ? TimeSpan.FromDays(1) : TimeSpan.Zero)
        + (i >= 503 ? -TimeSpan.FromDays(2) : TimeSpan.Zero);

    [Fact]
    public void TheClearersListsStatusAndRuleAreTimedOnTheForwardClock()
    {
        // The memory lists every minute and the helper's status every hour, for 2 hours, and the moment the automatic rule is
        // given at each sample: the same with the system clock left alone and stepped.
        List<string> Due(Func<int, TimeSpan> shift)
        {
            var schedule = new ClearerSchedule();
            var due = new List<string>();
            for (int i = 0; i < 720; i++)
            {
                var s = new Sample(T0.AddSeconds(10 * i) + shift(i), default, [], []) { TickMs = 1_000_000 + i * 10_000L };
                var d = schedule.At(s);
                Assert.Equal(Moment.Of(s), d.At);
                if (d.Lists || d.Status) due.Add($"{i} {d.Lists} {d.Status}");
            }
            return due;
        }
        var steady = Due(_ => TimeSpan.Zero);
        Assert.Equal(Enumerable.Range(0, 120).Select(m => $"{6 * m} True {m % 60 == 0}"), steady);
        Assert.Equal(steady, Due(Stepped));
    }

    [Fact]
    public void TheWorkerLabelsReadsAreTimedOnTheForwardClock()
    {
        // A worker doing 50 reads every 10 s: every minute the labels get its reads since the last time, the same with the
        // system clock left alone and stepped.
        List<string> Due(Func<int, TimeSpan> shift)
        {
            var reads = new WorkerReads();
            var due = new List<string>();
            for (int i = 0; i < 720; i++)
            {
                var worker = new ProcessSample { Pid = 700, ParentPid = 4, CreateTime = T0.AddDays(-1).ToFileTimeUtc(), ImageName = "worker.exe", ReadOperationCount = 50L * i };
                var s = new Sample(T0.AddSeconds(10 * i) + shift(i), default, [], [worker]) { TickMs = 1_000_000 + i * 10_000L };
                if (reads.At(s) is { } r) due.Add($"{i} {string.Join(",", r.Values)}");
            }
            return due;
        }
        var steady = Due(_ => TimeSpan.Zero);
        Assert.Equal(["0 0", "6 300", "12 300"], steady.Take(3));
        Assert.Equal(120, steady.Count);
        Assert.Equal(steady, Due(Stepped));
    }

    [Fact]
    public void AClearIsMeasured2MinutesAfterItRanOnTheForwardClock()
    {
        var clear = new ClearRecord { TimeUtc = T0, TickMs = 1_000_000, Mode = ClearMode.Standby, Trigger = ClearTrigger.Manual, Before = default };
        var failed = new ClearRecord { TimeUtc = T0, TickMs = 1_000_000, Mode = ClearMode.Standby, Trigger = ClearTrigger.Manual, Before = default, Error = "denied" };
        var reported = new HashSet<ClearRecord>(ReferenceEqualityComparer.Instance);
        Sample At(DateTime utc, long ms) => new(utc, default, [], []) { TickMs = ms };
        Assert.Equal([failed], CacheClearer.ToMeasure([clear, failed], reported, At(T0.AddHours(3), 1_000_000 + 119_000)));   // set forward 3 h
        Assert.Equal([clear, failed], CacheClearer.ToMeasure([clear, failed], reported, At(T0.AddHours(-1), 1_000_000 + 120_000))); // set back
        reported.Add(failed);
        Assert.Equal([clear], CacheClearer.ToMeasure([clear, failed], reported, At(T0, 1_000_000 + 120_000)));
    }

    [Fact]
    public void ClearsAnOlderBuildSavedKeepTheirSpacingAndBackOffAfterTheyAreLoaded()
    {
        // clears.json as the published beta writes it: times of day, no forward clock. Read 10 minutes after a scheduled
        // clear and 5 hours after a size-rule clear that freed almost nothing. The spacing (30 minutes) and the size rule's
        // back-off (6 hours) go on from them on the forward clock, whatever the system clock does after the load.
        var loaded = new Moment(T0, 50_000_000);
        string json = $$"""
            [
              {"TimeUtc":"{{T0.AddHours(-5):O}}","Mode":"Standby","Trigger":"PoolOverLimit","PoolBefore":{{10 * GB}},"PoolAfter":{{10 * GB - MB}},"PagedBefore":{{5 * GB}},"PagedAfter":{{5 * GB}},"StandbyBefore":0,"StandbyAfter":0,"Error":null},
              {"TimeUtc":"{{T0.AddMinutes(-10):O}}","Mode":"Standby","Trigger":"Schedule","PoolBefore":{{10 * GB}},"PoolAfter":{{9 * GB}},"PagedBefore":{{5 * GB}},"PagedAfter":{{5 * GB}},"StandbyBefore":0,"StandbyAfter":0,"Error":null}
            ]
            """;
        var history = JsonSerializer.Deserialize<List<CacheClearer.Saved>>(json)!.Select(r => CacheClearer.FromSaved(r, loaded)).ToList();
        Assert.Equal([loaded.Ms - 5 * 3_600_000L, loaded.Ms - 10 * 60_000L], history.Select(r => r.TickMs));
        var s = new Settings { AutoClearEnabled = true, AutoClearEveryHours = 0, AutoClearMinMinutes = 30, AutoClearPoolGb = 8, AutoClearMode = "standby" };
        var big = new PoolTotals((ulong)(5 * GB), (ulong)(5 * GB), 0, 0);
        Moment After(TimeSpan forward, TimeSpan systemClockSet) => new(T0 + forward + systemClockSet, loaded.Ms + (long)forward.TotalMilliseconds);
        long started = loaded.Ms;
        Assert.Null(ClearPolicy.Automatic(history, s, big, After(TimeSpan.FromMinutes(19), -TimeSpan.FromHours(2)), started, false));   // in the spacing
        Assert.Null(ClearPolicy.Automatic(history, s, big, After(TimeSpan.FromMinutes(59), TimeSpan.FromDays(1)), started, false));    // backed off
        Assert.Equal(ClearTrigger.PoolOverLimit, ClearPolicy.Automatic(history, s, big, After(TimeSpan.FromMinutes(61), -TimeSpan.FromDays(1)), started, false)?.Trigger);
    }

    [Fact]
    public void ATracesStartAndEndAreReadOnBothClocksAndItsLengthOnTheForwardOne()
    {
        // wpr is a fake that starts no program; the recording's 60 seconds pass while the system clock is set back an hour.
        var paths = new AppPaths(Path.Combine(_root, "data"), null, secure: false);
        var log = new Log(Path.Combine(_root, "data", "test.log"));
        var moments = new Queue<Moment>([new(T0, 1_000_000), new(T0, 1_000_000), new(T0 - TimeSpan.FromHours(1), 1_060_000)]);
        (int, string) Wpr(string args, int timeout)
        {
            if (args.StartsWith("-stop ", StringComparison.Ordinal))
            {
                string etl = args.Split('"')[1];
                File.WriteAllText(etl, "not a trace");
            }
            return (0, "");
        }
        var runner = new CaptureRunner(paths, log, null, null, () => moments.Count > 1 ? moments.Dequeue() : moments.Peek(), Wpr);
        using var stopped = new CancellationTokenSource();
        stopped.Cancel(); // the recording's wait returns at once
        var r = runner.Run(Path.Combine(paths.DataRoot, "incidents", "t"), [PoolTag.Parse("Reff")], new CaptureScope(true, false, false), 60, stopped.Token);
        Assert.StartsWith("parsing failed", r.Error);
        Assert.Equal((T0, T0 - TimeSpan.FromHours(1)), (r.StartUtc, r.EndUtc));
        Assert.Equal(TimeSpan.FromMinutes(1), r.Duration);
    }

    [Fact]
    public void ACpuRecordingsStartAndEndAreReadOnBothClocksAndItsLengthOnTheForwardOne()
    {
        // A CPU-only capture: wpr is a fake that starts no program and the trace's reader a fake that finds CPU samples in it;
        // the recording's 30 seconds pass while the system clock is set back an hour.
        var paths = new AppPaths(Path.Combine(_root, "data"), null, secure: false);
        var log = new Log(Path.Combine(_root, "data", "test.log"));
        var moments = new Queue<Moment>([new(T0, 1_000_000), new(T0, 1_000_000), new(T0 - TimeSpan.FromHours(1), 1_030_000)]);
        using var stopped = new CancellationTokenSource();
        (int, string) Wpr(string args, int timeout)
        {
            if (args.StartsWith("-start ", StringComparison.Ordinal)) stopped.Cancel(); // the recording's wait returns at once
            if (args.StartsWith("-stop ", StringComparison.Ordinal)) File.WriteAllText(args.Split('"')[1], "a trace");
            return (0, "");
        }
        CaptureResult Parse(string etl, IReadOnlyCollection<PoolTag> tags, Moment start, Moment end, string? symbols) =>
            new() { Cpu = new CpuProfile { Samples = 120_000, Processors = 4, TopCpu = [new("refs.sys", 30_000, 0.25)] } };
        var runner = new CaptureRunner(paths, log, null, null, () => moments.Count > 1 ? moments.Dequeue() : moments.Peek(), Wpr, Parse);
        var r = runner.Run(Path.Combine(paths.DataRoot, "incidents", "c"), [], new CaptureScope(false, false, true), 60, stopped.Token, cpuSeconds: 30);
        var cpu = Assert.IsType<CpuProfile>(r.Cpu);
        Assert.Equal((T0, T0 - TimeSpan.FromHours(1), 30.0), (cpu.StartUtc, cpu.EndUtc, cpu.Seconds));
        Assert.Equal(TimeSpan.FromSeconds(30), r.Duration);
        Assert.Contains(" busy over 30 s;", cpu.Summary());
    }

    [Fact]
    public void TheIncidentToReportIsOneClosedInTheLastWeekOnTheForwardClock()
    {
        var owner = new OwnerSuspect
        {
            Owner = new Owner(OwnerKind.App, "app:x", "X"), Members = [], Identities = [], Confidence = Confidence.StronglyCorrelated, Meaningful = true,
        };
        Incident Closed(string id, long closedMs) => new()
        {
            Id = id, Dir = id, Tag = PoolTag.Parse("Reff"), Kind = IncidentKind.Growth, OpenedUtc = T0, OnsetUtc = T0, ClosedUtc = T0,
            ClosedMs = closedMs, StartBytes = GB, PeakBytes = 3 * GB, Owners = [owner],
        };
        long now = 1_000_000_000;
        var incidents = new[] { Closed("old", now - (long)TimeSpan.FromDays(8).TotalMilliseconds), Closed("recent", now - (long)TimeSpan.FromDays(6).TotalMilliseconds),
            Closed("newest", now - (long)TimeSpan.FromDays(5).TotalMilliseconds) };
        // The newest was hidden 29 days ago on the forward clock, with the system clock 40 days behind now since: still hidden.
        var hidden = new Dictionary<string, Moment> { ["ReportIncident:newest"] = new(T0 - TimeSpan.FromDays(40), now - (long)TimeSpan.FromDays(29).TotalMilliseconds) };
        foreach (var set in new[] { TimeSpan.Zero, TimeSpan.FromDays(30), -TimeSpan.FromDays(30) })
        {
            Assert.Equal("recent", RecommendationInputsBuilder.Reportable(incidents.Take(2), new Dictionary<string, Moment>(), new Moment(T0 + set, now))!.Value.Id);
            Assert.Equal("recent", RecommendationInputsBuilder.Reportable(incidents, hidden, new Moment(T0 + set, now))!.Value.Id);
        }
    }

    [Fact]
    public void AMetadataTagsGrowthIsOfTheLastDayOnTheForwardClock()
    {
        Incident Opened(long openedMs) => new() { Id = "x", Dir = "x", Tag = PoolTag.Parse("Reff"), Kind = IncidentKind.Growth, OpenedUtc = T0, OpenedMs = openedMs };
        long now = 1_000_000_000;
        foreach (var set in new[] { TimeSpan.Zero, TimeSpan.FromDays(3), -TimeSpan.FromDays(3) })
        {
            var at = new Moment(T0 + set, now);
            Assert.True(RecommendationInputsBuilder.MetadataGrewLastDay([Opened(now - 23 * 3_600_000L)], at));
            Assert.False(RecommendationInputsBuilder.MetadataGrewLastDay([Opened(now - 25 * 3_600_000L)], at));
        }
    }
}
