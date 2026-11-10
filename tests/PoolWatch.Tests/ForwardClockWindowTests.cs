using PoolWatch.Core;
using PoolWatch.Core.Cache;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Findings;
using PoolWatch.Core.History;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Knowledge;
using PoolWatch.Core.Model;
using PoolWatch.Core.Recommendations;
using PoolWatch.Core.Tags;

using static PoolWatch.Tests.Clocks;

namespace PoolWatch.Tests;

/// <summary>
/// The windows, rates and graces of the core timed on the clock that only goes forward: each is given the same moments
/// twice, once with the system clock left alone and once with it set back or forward, and says the same both times.
/// </summary>
public class ForwardClockWindowTests
{
    private static readonly DateTime T0 = new(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);
    private const long MB = 1L << 20;
    private static readonly PoolTag Reff = PoolTag.Parse("Reff");

    /// <summary>A text with its times of day taken out (they differ by as much as the system clock was set).</summary>
    private static string Said(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\d\d:\d\d(:\d\d)?", "hh:mm");

    private static ProcessSample Proc(int pid, int parent, DateTime created, string image) =>
        new() { Pid = pid, ParentPid = parent, CreateTime = created.ToFileTimeUtc(), ImageName = image };

    [Fact]
    public void AProcessMissingFromTheSnapshotsIsMarkedExited4SecondsLaterOnTheForwardClock()
    {
        var now = new Moment(T0, 1_000_000);
        var reg = new ProcessRegistry(clock: () => now);
        var a = Proc(700, 4, T0.AddDays(-1), "a.exe");
        var b = Proc(701, 4, T0.AddDays(-1), "b.exe");
        void Snapshot(TimeSpan forward, TimeSpan systemClockSet, params ProcessSample[] live)
        {
            now = new Moment(now.Utc + forward + systemClockSet, now.Ms + (long)forward.TotalMilliseconds);
            reg.ObserveSnapshot(live, now.Utc);
        }
        Snapshot(TimeSpan.Zero, TimeSpan.Zero, a, b);
        Snapshot(TimeSpan.FromSeconds(2), TimeSpan.Zero, b);                          // a. goes missing
        Snapshot(TimeSpan.FromSeconds(3), -TimeSpan.FromHours(1), b);                 // set back an hour, 3 s on
        Assert.True(reg.Get(a.Key)!.IsAlive);
        Snapshot(TimeSpan.FromSeconds(1), TimeSpan.Zero, b);                          // 4 s on the forward clock
        Assert.False(reg.Get(a.Key)!.IsAlive);
        Snapshot(TimeSpan.FromSeconds(1), TimeSpan.Zero);                             // b. goes missing
        Snapshot(TimeSpan.FromSeconds(1), TimeSpan.FromDays(1));                      // set forward a day, 1 s on
        Assert.True(reg.Get(b.Key)!.IsAlive);
        Snapshot(TimeSpan.FromSeconds(3), TimeSpan.Zero);
        Assert.False(reg.Get(b.Key)!.IsAlive);
        Assert.Equal(1_000_000 + 7000, reg.Get(b.Key)!.ExitMs);                      // when it went missing, on the forward clock
    }

    [Fact]
    public void AChildIsAnOrphan30SecondsAfterItsParentExitedOnTheForwardClock()
    {
        var now = new Moment(T0, 1_000_000);
        var reg = new ProcessRegistry(clock: () => now);
        var parent = Proc(100, 4, T0.AddHours(-2), "bash.exe");
        var child = Proc(101, 100, T0.AddHours(-1), "find.exe");
        var parent2 = Proc(200, 4, T0.AddHours(-2), "cmd.exe");
        var child2 = Proc(201, 200, T0.AddHours(-1), "worker.exe");
        void Pass(TimeSpan forward, TimeSpan systemClockSet) =>
            now = new Moment(now.Utc + forward + systemClockSet, now.Ms + (long)forward.TotalMilliseconds);
        reg.ObserveSnapshot([parent, child, parent2, child2], now.Utc);
        Pass(TimeSpan.FromSeconds(1), TimeSpan.Zero);
        reg.OnStop(100, parent.CreateTime, 0, now.Utc);
        reg.OnStop(200, parent2.CreateTime, 0, now.Utc);
        // The system clock set forward a minute 10 s after the exits: not orphans yet.
        Pass(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1));
        reg.ObserveSnapshot([child, child2], now.Utc);
        Assert.False(reg.Get(child.Key)!.IsOrphan);
        // Set back an hour: at 30 s on the forward clock they are.
        Pass(TimeSpan.FromSeconds(10), -TimeSpan.FromHours(1));
        reg.ObserveSnapshot([child, child2], now.Utc);
        Assert.False(reg.Get(child.Key)!.IsOrphan);
        Pass(TimeSpan.FromSeconds(10), TimeSpan.Zero);
        reg.ObserveSnapshot([child, child2], now.Utc);
        Assert.True(reg.Get(child.Key)!.IsOrphan);
        Assert.Equal(1_001_000, reg.Get(child2.Key)!.OrphanedMs);
        Assert.Contains(reg.DrainEvents(), e => e.Kind == LifecycleKind.Orphaned && e.Pid == 101);
    }

    [Fact]
    public void AParentThatExitedBeforeItsChildStartedIsNotItsParentOnTheForwardClock()
    {
        // The parent exits; the system clock is set back an hour; 5 s later a process starts with the parent's PID as its
        // parent: by the forward clock it started after that parent exited, so that parent is not its parent (a reused PID).
        var now = new Moment(T0, 1_000_000);
        var reg = new ProcessRegistry(clock: () => now);
        var parent = Proc(300, 4, T0.AddHours(-2), "cmd.exe");
        reg.ObserveSnapshot([parent], now.Utc);
        reg.OnStop(300, parent.CreateTime, 0, now.Utc);
        now = new Moment(T0.AddSeconds(5) - TimeSpan.FromHours(1), 1_005_000);
        var child = Proc(301, 300, now.Utc, "git.exe");
        reg.ObserveSnapshot([child], now.Utc);
        Assert.Null(reg.Get(child.Key)!.ParentKey);
    }

    [Fact]
    public void ATagsGrowthDuringATraceIsTimedOnTheForwardClock()
    {
        // The tag climbs 1 MB a sample; a trace from sample 20 to 30, with the system clock set back an hour at sample 25.
        long? Growth(TimeSpan setBack)
        {
            var h = History.ForInterval(10);
            for (int i = 0; i < 40; i++)
                h.Add(new Sample(T0.AddSeconds(10 * i) + (i >= 25 ? setBack : TimeSpan.Zero), default, [new PoolTagEntry(Reff, 0, 0, 0, 1, 0, (ulong)(64 * MB + i * MB))], [])
                { TickMs = 1_000_000 + i * 10_000L });
            return IncidentManager.TagGrowth(h, Reff, 1_000_000 + 200_000, 1_000_000 + 300_000);
        }
        Assert.Equal(10 * MB, Growth(TimeSpan.Zero));
        Assert.Equal(10 * MB, Growth(-TimeSpan.FromHours(1)));
        Assert.Equal(10 * MB, Growth(TimeSpan.FromDays(1)));
    }

    [Fact]
    public void TheRecordsOfAnIncidentsWindowAreKeptByTheirExitsOnTheForwardClock()
    {
        // 300 short commands, one a second; the system clock set back an hour halfway. The window protects those that exited
        // from the 100th to the 200th second on the forward clock; 40 minutes later the rest are let go (15 minutes).
        var now = new Moment(T0, 10_000_000);
        var reg = new ProcessRegistry(clock: () => now) { KeepShortLived = TimeSpan.FromMinutes(15) };
        reg.ObserveSnapshot([Proc(4, 0, T0.AddDays(-1), "System")], now.Utc);
        for (int i = 0; i < 300; i++)
        {
            now = new Moment(T0.AddSeconds(i) + (i >= 150 ? -TimeSpan.FromHours(1) : TimeSpan.Zero), 10_000_000 + i * 1000L);
            var created = now.Utc.ToFileTimeUtc();
            reg.OnStart(10_000 + i, 4, created, "git.exe", null, "git status", null, now.Utc);
            reg.OnStop(10_000 + i, created, 0, now.Utc);
        }
        reg.ProtectSinceMs = 10_000_000 + 100_000;
        reg.ProtectUntilMs = 10_000_000 + 200_000;
        now = new Moment(now.Utc.AddMinutes(40), now.Ms + 40 * 60_000);
        reg.ObserveSnapshot([Proc(4, 0, T0.AddDays(-1), "System")], now.Utc);
        bool Held(int i) => reg.FindInstance(10_000 + i, 0, DateTime.MinValue) is not null || reg.InstancesOf([10_000 + i]).Count > 0;
        Assert.True(Held(120));   // before the clock was set back, in the window
        Assert.True(Held(180));   // after it, in the window
        Assert.False(Held(50));
        Assert.False(Held(250));
    }

    [Fact]
    public void AnIncidentsEventsAndItsShortCommandsArePickedOnTheForwardClock()
    {
        var mgr = new IncidentManager(new Settings(), new TagKnowledge(null, null), new FakeHost(Path.GetTempPath()), new ProcessRegistry());
        // One event a minute for 20 minutes; the system clock set back an hour after the 10th.
        for (int i = 0; i < 20; i++)
        {
            var at = new Moment(T0.AddMinutes(i) + (i >= 10 ? -TimeSpan.FromHours(1) : TimeSpan.Zero), 5_000_000 + i * 60_000L);
            mgr.AddEvents([new LifecycleEvent { TimeUtc = at.Utc, Kind = LifecycleKind.Rollup, Pid = 100 + i, OwnerKey = "claude:4000", Count = 1 }], at);
        }
        var picked = mgr.EventsBetween(5_000_000 + 5 * 60_000, 5_000_000 + 14 * 60_000);
        Assert.Equal(Enumerable.Range(105, 10), picked.Select(e => e.Pid));                       // in the order they happened
        Assert.Equal(10, IncidentManager.StartsByOwner(picked.Concat(mgr.EventsBetween(0, 1)), 5_000_000 + 5 * 60_000, 5_000_000 + 14 * 60_000)["claude:4000"]);
    }

    [Fact]
    public void AnIncidentsOwnEventsAreMergedIntoItsWindowInTheOrderTheyHappenedOnTheForwardClock()
    {
        // An incident whose onset is at minute 0 kept one event a minute from minute -3 to 9, the system clock set back an
        // hour at minute 5; the shared buffer has let those go and holds five newer ones (minutes 10 to 14). Its events are
        // its own from 2 minutes before the onset on the forward clock and the buffer's, in the order they happened.
        var mgr = new IncidentManager(new Settings(), new TagKnowledge(null, null), new FakeHost(Path.GetTempPath()), new ProcessRegistry());
        Moment At(int minute) => new(T0.AddMinutes(minute) + (minute >= 5 ? -TimeSpan.FromHours(1) : TimeSpan.Zero), 5_000_000 + minute * 60_000L);
        LifecycleEvent Event(int minute) => new() { TimeUtc = At(minute).Utc, Kind = LifecycleKind.Start, Pid = 100 + minute, Image = "x.exe" };
        for (int m = 10; m < 15; m++) mgr.AddEvents([Event(m)], At(m));
        var inc = new Incident
        {
            Id = "x", Dir = "x", Tag = Reff, Kind = IncidentKind.Growth, OnsetUtc = At(0).Utc, OnsetMs = At(0).Ms, EndUtc = At(20).Utc, EndMs = At(20).Ms,
            Events = Enumerable.Range(-3, 13).Select(m => Event(m) with { Ms = At(m).Ms }).ToList(),
        };
        Assert.Equal(Enumerable.Range(98, 17), mgr.IncidentEvents(inc, At(20).Ms).Window.Select(e => e.Pid));
    }

    [Fact]
    public void AnIncidentsOnsetIsItsEarliestAlertsOnTheForwardClockWithThatAlertsTimeOfDay()
    {
        // Two alerts: the first by the forward clock was raised before the system clock was set back an hour, so its onset's
        // time of day is the later one.
        Alert A(DateTime onsetUtc, long onsetMs) => new(AlertKind.Growth, Reff, onsetUtc, "rule", MB, TimeSpan.FromMinutes(10), onsetUtc) { Ms = onsetMs, OnsetMs = onsetMs };
        var first = A(T0, 1_000_000);
        var second = A(T0.AddMinutes(5) - TimeSpan.FromHours(1), 1_300_000);
        Assert.Equal((T0, 1_000_000L), IncidentManager.EarliestOnset([second, first], T0.AddDays(1), 7));
        Assert.Equal((T0.AddDays(1), 7L), IncidentManager.EarliestOnset([], T0.AddDays(1), 7));
    }

    [Fact]
    public void ARankedIncidentsOnsetIsItsEarliestAlertsOnTheForwardClockWithThatAlertsTimeOfDay()
    {
        // 30 samples 10 s apart of a tag that holds at 64 MB (no change point refines the onset), the system clock set back
        // an hour at sample 15. The incident's growth alerts have their onsets at samples 20 and 10. Re-ranked, its onset is
        // sample 10's on both clocks; by the times of day sample 20's onset is the earlier.
        var h = History.ForInterval(10);
        for (int i = 0; i < 30; i++)
            h.Add(new Sample(T0.AddSeconds(10 * i) + (i >= 15 ? -TimeSpan.FromHours(1) : TimeSpan.Zero), default, [new PoolTagEntry(Reff, 0, 0, 0, 0, 0, (ulong)(64 * MB))], [])
            { TickMs = 1_000_000 + i * 10_000L });
        Alert A(int i) => new(AlertKind.Growth, Reff, h.Times[i], "rule", MB, TimeSpan.FromMinutes(10), h.Times[i]) { Ms = h.Ms[i], OnsetMs = h.Ms[i] };
        var inc = new Incident { Id = "x", Dir = "x", Tag = Reff, Kind = IncidentKind.Growth, OnsetUtc = h.Times[20], OnsetMs = h.Ms[20] };
        inc.Alerts.Add(A(20));
        inc.Alerts.Add(A(10));
        var mgr = new IncidentManager(new Settings(), new TagKnowledge(null, null), new FakeHost(Path.GetTempPath()), new ProcessRegistry());
        mgr.Rank(h, inc, h.Times[^1]);
        Assert.Equal((h.Times[10], h.Ms[10]), (inc.OnsetUtc, inc.OnsetMs));
        Assert.Equal(h.Times[10], inc.Window!.OnsetUtc);
    }

    [Fact]
    public void ATracesRowWithoutACreateTimeIsTheInstanceOfItsPidAliveWhenItFirstAllocatedOnTheForwardClock()
    {
        // PID 700 was a.exe (started 2 hours before the test's start, exited 10 s after it) and then b.exe (started 20 s
        // after the test's start, still running), each start and exit read by the registry as it arrived. The system clock
        // is then set back an hour, and a trace begins 30 s after the test's start on the forward clock. Its rows carry no
        // create time: one of PID 700 that first allocated 5 s into the trace, and one of PID 9000, which PoolWatch never
        // saw, started by PID 700. Both are b.exe's, alive then on the forward clock (by the times of day a.exe was, and
        // b.exe had not started). PID 800 was c.exe (started 5 s after the test's start) and then, its exit not seen,
        // d.exe (started after the clock was set back, 25 s after the test's start on the forward clock): the registry
        // holds both as running. The registry's forward clock only goes forward through these reads.
        var now = new Moment(T0, 1_000_000);
        var reg = new ProcessRegistry(clock: () => now);
        long aCreate = T0.AddHours(-2).ToFileTimeUtc();
        reg.OnStart(700, 4, aCreate, "a.exe", @"C:\Tools\a.exe", "a --run", null, T0);
        now = new Moment(T0.AddSeconds(5), 1_005_000);
        reg.OnStart(800, 4, now.Utc.ToFileTimeUtc(), "c.exe", @"C:\Tools\c.exe", "c --run", null, now.Utc);
        now = new Moment(T0.AddSeconds(10), 1_010_000);
        reg.OnStop(700, aCreate, 0, now.Utc);
        now = new Moment(T0.AddSeconds(20), 1_020_000);
        reg.OnStart(700, 4, now.Utc.ToFileTimeUtc(), "b.exe", @"C:\Tools\b.exe", "b --run", null, now.Utc);
        now = new Moment(T0.AddSeconds(25) - TimeSpan.FromHours(1), 1_025_000);
        reg.OnStart(800, 4, now.Utc.ToFileTimeUtc(), "d.exe", @"C:\Tools\d.exe", "d --run", null, now.Utc);
        var began = T0.AddSeconds(30) - TimeSpan.FromHours(1);
        var cap = new CaptureResult
        {
            PoolEventsParsed = true, Tags = [Reff],
            Pool = [new(700, "", Reff, MB, 0, 10, 0, [], FirstAllocUtc: began.AddSeconds(5)), new(9000, "cut.exe", Reff, MB, 0, 10, 0, [], 0, "cut -f2", 700, began.AddSeconds(5))],
        }.Recorded(new Moment(began, 1_030_000), new Moment(began.AddMinutes(1), 1_090_000));
        Assert.Equal(1_035_000, cap.MsOf(began.AddSeconds(5)));
        var traced = TraceAttribution.Resolve(cap, Reff, reg);
        Assert.Equal("b --run", traced.Single(t => t.Usage.Pid == 700).CommandLine);
        Assert.Equal(["b.exe", "cut.exe"], traced.Single(t => t.Usage.Pid == 9000).Chain);
        // A trace that began 8 s after the test's start, while a.exe ran and before the clock was set: a row of PID 700
        // that first allocated 7 s into it lies between a.exe's exit and b.exe's start on the forward clock, so it is
        // neither's; a row of PID 9001, started by PID 700, that first allocated 14 s into it is b.exe's child (when the
        // trace began, PID 700 was a.exe).
        var between = new CaptureResult
        {
            PoolEventsParsed = true, Tags = [Reff],
            Pool = [new(700, "", Reff, MB, 0, 10, 0, [], FirstAllocUtc: T0.AddSeconds(15)), new(9001, "cut.exe", Reff, MB, 0, 10, 0, [], 0, "cut -f3", 700, T0.AddSeconds(22))],
        }.Recorded(new Moment(T0.AddSeconds(8), 1_008_000), new Moment(T0.AddSeconds(68), 1_068_000));
        var inBetween = TraceAttribution.Resolve(between, Reff, reg);
        Assert.False(inBetween.Single(t => t.Usage.Pid == 700).Resolved);
        Assert.Equal(["b.exe", "cut.exe"], inBetween.Single(t => t.Usage.Pid == 9001).Chain);
        // A row of PID 800 that first allocated 5 s into a trace begun as the first one was is d.exe's, the later started
        // of the two on the forward clock (by their times of day c.exe started later).
        var both = new CaptureResult { PoolEventsParsed = true, Tags = [Reff], Pool = [new(800, "", Reff, MB, 0, 10, 0, [], FirstAllocUtc: began.AddSeconds(5))] }
            .Recorded(new Moment(began, 1_030_000), new Moment(began.AddMinutes(1), 1_090_000));
        Assert.Equal("d --run", Assert.Single(TraceAttribution.Resolve(both, Reff, reg)).CommandLine);
    }

    [Fact]
    public void ATracesRowsWithoutACreateTimeGoToTheSuspectsAliveWhenItBeganOnTheForwardClock()
    {
        // A trace began at the forward clock's 1,000,000 (the test's start by the system clock); its rows carry no create
        // time, so each is credited to the suspect of its PID only if that one had not exited when the trace began. PID 100
        // exited 10 s after, with the system clock set back an hour since (by its time of day it exited first); PID 200
        // exited 10 s before, with the clock set forward a day since (by its time of day it exited after).
        Suspect S(int pid, DateTime exitUtc, long exitMs) => new()
        {
            Key = new ProcessKey(pid, 1), Image = $"p{pid}.exe", ExitUtc = exitUtc, ExitMs = exitMs,
            Direct = new DirectSignal(null, 0, 0, 0, 0, 0, false), Activity = new ActivitySignal("total", double.NaN, 0, double.NaN, 0, 0, false),
            Lifecycle = new LifecycleSignal(false, false, false, []),
        };
        var inc = new Incident
        {
            Id = "x", Dir = "x", Tag = Reff, Kind = IncidentKind.Growth,
            Suspects = [S(100, T0.AddSeconds(10) - TimeSpan.FromHours(1), 1_010_000), S(200, T0.AddDays(1).AddSeconds(-10), 990_000)],
        };
        var cap = new CaptureResult { PoolEventsParsed = true, Tags = [Reff], Pool = [new(100, "p100.exe", Reff, 8 * MB, 0, 100, 0, []), new(200, "p200.exe", Reff, 8 * MB, 0, 100, 0, [])] }
            .Recorded(new Moment(T0, 1_000_000), new Moment(T0.AddSeconds(60), 1_060_000));
        CaptureAttribution.Apply(inc, cap);
        Assert.Equal([(100, 8 * MB), (200, 0L)], inc.Suspects.Select(s => (s.Key.Pid, s.TraceNetBytes)).OrderBy(x => x.Pid));
    }

    [Fact]
    public void ATracedCommandIsShortByHowLongItRanOnTheForwardClock()
    {
        // The registry reads each start and exit as it arrives: git.exe runs 5 s with the system clock set forward a day in
        // between, node.exe 5 minutes with it set back an hour. The trace's rows for them: git.exe is a short command, node.exe
        // is not.
        var now = new Moment(T0, 1_000_000);
        var reg = new ProcessRegistry(clock: () => now);
        long ct = T0.ToFileTimeUtc();
        reg.OnStart(700, 4, ct, "git.exe", null, "git status", null, T0);
        reg.OnStart(800, 4, ct, "node.exe", null, "node x.js", null, T0);
        now = new Moment(T0.AddDays(1).AddSeconds(5), 1_005_000);
        reg.OnStop(700, ct, 0, now.Utc);
        now = new Moment(T0.AddMinutes(5) - TimeSpan.FromHours(1), 1_300_000);
        reg.OnStop(800, ct, 0, now.Utc);
        var cap = new CaptureResult { PoolEventsParsed = true, Tags = [Reff], Pool = [new(700, "git.exe", Reff, MB, 0, 10, 0, [], ct), new(800, "node.exe", Reff, MB, 0, 10, 0, [], ct)] }
            .Recorded(new Moment(T0, 1_000_000), new Moment(T0, 1_060_000));
        var traced = TraceAttribution.Resolve(cap, Reff, reg);
        Assert.Equal([("git.exe", true, TimeSpan.FromSeconds(5)), ("node.exe", false, TimeSpan.FromMinutes(5))],
            traced.Select(t => (t.Usage.Image, t.Short, t.Ran!.Value)).OrderBy(t => t.Image, StringComparer.Ordinal));
    }

    [Fact]
    public void AReportsDurationsAreTimedOnTheForwardClock()
    {
        // A growth of 26 minutes and a trace of a minute, with the system clock set back an hour inside each.
        var inc = new Incident
        {
            Id = "x", Dir = "x", Tag = Reff, Kind = IncidentKind.Growth, OnsetUtc = T0, LastRiseUtc = T0.AddMinutes(26) - TimeSpan.FromHours(1),
            EndUtc = T0.AddMinutes(26) - TimeSpan.FromHours(1), LastAlertUtc = T0.AddMinutes(26) - TimeSpan.FromHours(1),
            OnsetMs = 1_000_000, LastRiseMs = 1_000_000 + 26 * 60_000, EndMs = 1_000_000 + 26 * 60_000, LastAlertMs = 1_000_000 + 26 * 60_000,
            StartBytes = 95 * MB, PeakBytes = 675 * MB, CurrentBytes = 675 * MB,
        };
        Assert.Equal(TimeSpan.FromMinutes(26), inc.GrowthSpan);
        var writer = new IncidentWriter(new TagKnowledge(null, null), new FakeHost(Path.GetTempPath()), new ProcessRegistry());
        var info = new TagInfo("refs.sys", "FCB_DATA", "test");
        Assert.Contains("grew 580 MB in 26 min", writer.Headline(inc, info));
        var start = T0.AddMinutes(10);
        var reg = new ProcessRegistry();
        reg.OnStart(5000, 4, start.ToFileTimeUtc(), "leaker.exe", @"C:\Tools\leaker.exe", "leaker", null, start);
        var usage = new CapturePoolUsage(5000, "leaker.exe", Reff, 300 * MB, 0, 1000, 0, [], start.ToFileTimeUtc(), "leaker", 4, start);
        var cap = new CaptureResult { PoolEventsParsed = true, Tags = [Reff], Pool = [usage] }
            .Recorded(new Moment(start, 2_000_000), new Moment(start - TimeSpan.FromHours(1), 2_060_000));
        Assert.Equal(TimeSpan.FromMinutes(1), cap.Duration);
        string answer = TraceAttribution.Answer(inc, info, TraceAttribution.Owners(TraceAttribution.Resolve(cap, Reff, reg)), cap)!;
        Assert.StartsWith("Reff (FCB_DATA) grew 580 MB in 26 min", answer);
        Assert.Contains("in 1 min", answer.Split("The trace shows")[1]);
    }

    /// <summary>
    /// The "Allocation shape" line of the report an incident of <paramref name="tag"/> with its onset at sample 100 and its
    /// end at sample 130 gets from 180 samples 10 s apart, the system clock moved by <paramref name="shift"/> at each; the
    /// tag's bytes and cumulative allocation and free counts at each sample are <paramref name="tagAt"/>'s.
    /// </summary>
    private static string ShapeLine(PoolTag tag, Func<int, (long bytes, uint allocs, uint frees)> tagAt, Func<int, TimeSpan> shift)
    {
        var h = History.ForInterval(10);
        for (int i = 0; i < 180; i++)
        {
            var (bytes, allocs, frees) = tagAt(i);
            h.Add(new Sample(T0.AddSeconds(10 * i) + shift(i), default, [new PoolTagEntry(tag, 0, 0, 0, allocs, frees, (ulong)bytes)], []) { TickMs = 1_000_000 + i * 10_000L });
        }
        string dir = Path.Combine(Path.GetTempPath(), "pw-shape-" + Guid.NewGuid().ToString("N"));
        try
        {
            var inc = new Incident
            {
                Id = "shape", Dir = Path.Combine(dir, "shape"), Tag = tag, Kind = IncidentKind.Growth, OpenedUtc = h.Times[100], OpenedMs = h.Ms[100],
                OnsetUtc = h.Times[100], OnsetMs = h.Ms[100], EndUtc = h.Times[130], EndMs = h.Ms[130], LastRiseUtc = h.Times[130], LastRiseMs = h.Ms[130],
                LastAlertUtc = h.Times[130], LastAlertMs = h.Ms[130], StartBytes = tagAt(100).bytes, PeakBytes = tagAt(130).bytes, CurrentBytes = tagAt(179).bytes,
            };
            new IncidentWriter(new TagKnowledge(null, null), new FakeHost(dir), new ProcessRegistry()).Write(h, inc, []);
            return Said(Assert.Single(File.ReadAllLines(Path.Combine(inc.Dir, "report.md")), l => l.StartsWith("- Allocation shape", StringComparison.Ordinal)));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void AReportsAllocationRatesAndTheirBaselineAreFoundOnTheForwardClock()
    {
        // Busy churn for the first 40 samples, light churn up to the onset (sample 100), then a growth to sample 130. The
        // report sets the growth's rates against the same span just before the onset (samples 70 to 100), found on the
        // forward clock: the system clock set back an hour inside the growth (by the times of day it would last less than
        // nothing) or between that span's start and the onset (the span's start would be found among the busy churn) leaves
        // the line as it is.
        static (long, uint, uint) Counts(int i)
        {
            uint allocs = (uint)(1000 * Math.Min(i, 40) + 100 * Math.Clamp(i - 40, 0, 60) + 500 * Math.Clamp(i - 100, 0, 30) + 100 * Math.Max(0, i - 130));
            uint frees = (uint)(1000 * Math.Min(i, 40) + 90 * Math.Clamp(i - 40, 0, 60) + 50 * Math.Clamp(i - 100, 0, 30) + 100 * Math.Max(0, i - 130));
            return (64 * MB + Math.Clamp(i - 100, 0, 30) * MB, allocs, frees);
        }
        string steady = ShapeLine(Reff, Counts, _ => TimeSpan.Zero);
        Assert.Equal("- Allocation shape during the growth: 3,000 allocations/min, 300 frees/min, against 600 and 540 a minute just before it: " +
                     steady.Split(": ")[^1], steady);
        Assert.Equal(steady, ShapeLine(Reff, Counts, i => i >= 115 ? -TimeSpan.FromHours(1) : TimeSpan.Zero));
        Assert.Equal(steady, ShapeLine(Reff, Counts, i => i >= 85 ? -TimeSpan.FromHours(1) : TimeSpan.Zero));
    }

    [Fact]
    public void AReportSaysFromWhenTheTagsCountersCoverItsGrowthOnTheForwardClock()
    {
        // A tag too small to keep its counters (under 16 KB) until 50 s after the growth's onset at sample 100: the report
        // says its rates run from that sample. The system clock set back an hour between the history's start and the onset
        // (the history would no longer seem to hold the onset), or between the onset and that sample (the counters would seem
        // to cover the growth from its start), leaves the line as it is.
        var leak = PoolTag.Parse("Leak");
        static (long, uint, uint) Counts(int i) => i < 105 ? (8 * 1024, 0u, 0u) : (64 * MB + (i - 105) * MB, (uint)(500 * (i - 104)), (uint)(50 * (i - 104)));
        string steady = ShapeLine(leak, Counts, _ => TimeSpan.Zero);
        Assert.StartsWith("- Allocation shape from hh:mm (the tag was too small to keep its counters before): ", steady);
        Assert.Equal(steady, ShapeLine(leak, Counts, i => i >= 50 ? -TimeSpan.FromHours(1) : TimeSpan.Zero));
        Assert.Equal(steady, ShapeLine(leak, Counts, i => i >= 102 ? -TimeSpan.FromHours(1) : TimeSpan.Zero));
    }

    [Fact]
    public void TheRatesAcrossAClearAreTimedOnTheForwardClock()
    {
        // A tag allocating 600 and freeing 300 a minute; PoolWatch's own clear at sample 10 (measured 2 minutes later), and
        // the system clock set back an hour after it. The rates across it are the same with the clock left alone.
        TagRates Rates(TimeSpan setBack)
        {
            var h = History.ForInterval(10);
            for (int i = 0; i < 40; i++)
                h.Add(new Sample(T0.AddSeconds(10 * i) + (i >= 25 ? setBack : TimeSpan.Zero), default,
                    [new PoolTagEntry(Reff, 0, 0, 0, (uint)(100 * i), (uint)(50 * i), (ulong)(64 * MB + i * 50 * 1024))], []) { TickMs = 1_000_000 + i * 10_000L });
            var clear = new ClearRecord
            {
                TimeUtc = T0.AddSeconds(101), TickMs = 1_101_000, Mode = ClearMode.Standby, Trigger = ClearTrigger.Manual, Before = default, After = new PoolTotals(0, 0, 0, 0),
            };
            return new OwnClears([clear], TimeSpan.FromMinutes(2)).SpansIn(h).Rates(h, Reff, 0, 39);
        }
        var steady = Rates(TimeSpan.Zero);
        Assert.Equal(AllocShape.GrowingWithChurn, steady.Shape);
        Assert.Equal(600, steady.AllocsPerMin, 3);
        Assert.Equal(steady, Rates(-TimeSpan.FromHours(1)));
        Assert.Equal(steady, Rates(TimeSpan.FromDays(1)));
    }

    [Fact]
    public void TheCorrelatorsHandleScansAndAProcesssStartAndExitAreTimedOnTheForwardClock()
    {
        // The same world twice: a writer opens handles before the growth and more during it (handle scans before and after),
        // a helper starts just before the onset and exits as the growth ends. In the second the system clock is set back an
        // hour between the first scans, forward two hours just before the onset and an hour more between the exit and the end.
        List<string> Ranked(Func<int, TimeSpan> shift)
        {
            var w = new World { Shift = shift };
            w.AddProcess(500, 4, "writer.exe");
            long v = 10 * MB;
            for (int i = 0; i < 70; i++)
            {
                if (i == 8) w.Io(500, handles: 300);
                if (i == 18) w.AddProcess(600, 4, "helper.exe", startIndex: 18);
                if (i >= 20 && i < 45) { v += 400_000; w.Io(500, other: 2000, handles: 200); w.Io(600, other: 500); }
                if (i == 44) w.Exit(600);
                w.Sample(("File", v));
                if (i is 4 or 15 or 50) w.ScanHandles();
            }
            var h = w.History;
            var window = CorrelationWindow.Of(h, 0, 20, 45, h.Count - 1);
            return Correlator.Rank(h, w.Registry, PoolTag.Parse("File"), window).Select(s => $"{s.Image} {s.Confidence} {s.Score:0.###} " +
                $"{s.Direct.HandlesBefore} {s.Direct.HandlesAfter} {s.Lifecycle.StartedBeforeOnset} {s.Lifecycle.ExitedAtEnd} | " +
                string.Join(" | ", s.Lifecycle.Notes.Concat(s.Evidence).Select(Said))).ToList();
        }
        var steady = Ranked(_ => TimeSpan.Zero);
        Assert.Contains(steady, s => s.StartsWith("helper.exe", StringComparison.Ordinal) && s.Contains(" True True |", StringComparison.Ordinal));
        Assert.Contains(steady, s => s.StartsWith("writer.exe", StringComparison.Ordinal) && s.Contains(" 300 5300 ", StringComparison.Ordinal));
        Assert.Equal(steady, Ranked(i => (i >= 10 ? -TimeSpan.FromHours(1) : TimeSpan.Zero) + (i >= 19 ? TimeSpan.FromHours(2) : TimeSpan.Zero)
            + (i >= 45 ? TimeSpan.FromHours(1) : TimeSpan.Zero)));
        // Set back an hour between the scan before the onset (sample 15) and the onset: by their times of day both scans
        // before the growth lie after its onset, and the scan after it (sample 50) before the scan before it. On the forward
        // clock the scan before is still sample 15's and the scan after sample 50's.
        Assert.Equal(steady, Ranked(i => i >= 17 ? -TimeSpan.FromHours(1) : TimeSpan.Zero));
    }

    [Fact]
    public void TheCorrelatorsOrphanAndItsTolerancesAreTimedOnTheForwardClock()
    {
        // A find.exe started at sample 2 whose bash.exe parent exits at sample 5 crawls through the growth (samples 20 to 45),
        // an orphan 30 s after that exit; an early.exe started 10 minutes before the onset is busy in the growth too. The
        // same world three times: the system clock left alone, set back an hour at sample 30 (between the orphaning and the
        // growth's end) and set forward a day there (by the samples' times of day the interval between samples would be 20
        // minutes, and its tolerances would take early.exe's start as near the onset).
        List<string> Ranked(Func<int, TimeSpan> shift)
        {
            var w = new World { Shift = shift };
            w.AddProcess(700, 4, "bash.exe");
            w.AddProcess(800, 4, "early.exe", startIndex: -280);
            long v = 10 * MB;
            for (int i = 0; i < 70; i++)
            {
                if (i == 2) w.AddProcess(701, 700, "find.exe", startIndex: 2);
                if (i == 5) w.Exit(700);
                if (i >= 20 && i < 45) { v += 400_000; w.Io(701, other: 3000); w.Io(800, other: 1000); }
                w.Sample(("File", v));
            }
            var h = w.History;
            var window = CorrelationWindow.Of(h, 0, 20, 45, h.Count - 1);
            return Correlator.Rank(h, w.Registry, PoolTag.Parse("File"), window).Select(s => $"{s.Image} {s.Confidence} {s.Score:0.###} {s.IsOrphan} " +
                $"{s.Lifecycle.StartedBeforeOnset} {s.Lifecycle.ExitedAtEnd} {s.Lifecycle.OrphanActive} | " +
                string.Join(" | ", s.Lifecycle.Notes.Concat(s.Evidence).Select(Said))).ToList();
        }
        var steady = Ranked(_ => TimeSpan.Zero);
        var find = Assert.Single(steady, s => s.StartsWith("find.exe", StringComparison.Ordinal));
        Assert.Contains(" True True False True | ", find);                                   // started near the onset; an orphan, busy in the growth
        Assert.Contains("(parent PID 700 gone), running 2 min with ", find);                 // its running time on the forward clock
        Assert.Contains(steady, s => s.StartsWith("early.exe", StringComparison.Ordinal) && s.Contains(" False False False False | ", StringComparison.Ordinal));
        Assert.Equal(steady, Ranked(i => i >= 30 ? -TimeSpan.FromHours(1) : TimeSpan.Zero));
        Assert.Equal(steady, Ranked(i => i >= 30 ? TimeSpan.FromDays(1) : TimeSpan.Zero));
    }

    [Fact]
    public void ATagsRecentWindowIsTheLast10MinutesOnTheForwardClock()
    {
        int From(TimeSpan setBack)
        {
            var h = History.ForInterval(10);
            for (int i = 0; i < 120; i++)
                h.Add(new Sample(T0.AddSeconds(10 * i) + (i >= 100 ? setBack : TimeSpan.Zero), default, [new PoolTagEntry(Reff, 0, 0, 0, 1, 0, (ulong)(64 * MB))], [])
                { TickMs = 1_000_000 + i * 10_000L });
            var recent = CorrelationWindow.Recent(h, Reff, TimeSpan.FromMinutes(10));
            Assert.Equal((h.Ms[recent.From], h.Ms[^1]), (recent.Window.FromMs, recent.Window.ToMs));
            Assert.Equal(h.Ms[^1] - 600_000, recent.SinceMs); // the span begins 10 minutes before the newest sample
            return recent.From;
        }
        Assert.Equal(59, From(TimeSpan.Zero));
        Assert.Equal(59, From(-TimeSpan.FromMinutes(30)));
        Assert.Equal(59, From(TimeSpan.FromHours(3)));
    }

    [Fact]
    public void AKnownIssuesClearsAreThoseOfTheLastDayOnTheForwardClock()
    {
        // Two measured clears that freed Reff: one 23 hours before the newest sample on the forward clock (with the system
        // clock set forward two days since), one 25 hours before it (with the clock set back a day since). Only the first
        // is of the last day.
        var h = History.ForInterval(10);
        h.Add(new Sample(T0, default, [new PoolTagEntry(Reff, 0, 0, 0, 1, 0, (ulong)(64 * MB))], []) { TickMs = 100_000_000 });
        ClearRecord Clear(DateTime utc, long ms) => new()
        {
            TimeUtc = utc, TickMs = ms, Mode = ClearMode.Standby, Trigger = ClearTrigger.Manual, Before = default, After = new PoolTotals(0, 0, 0, 0),
            TagsBefore = [(Reff, 900 * MB)], TagsAfter = [(Reff, 100 * MB)],
        };
        var recent = Clear(T0.AddDays(-3), 100_000_000 - 23 * 3_600_000L);
        var old = Clear(T0.AddHours(-1), 100_000_000 - 25 * 3_600_000L);
        var evidence = KnowledgeEvidence.Gather(KnowledgeBase.Embedded(), h, [recent, old], TimeSpan.FromMinutes(2), new RecommendationInputs { NowUtc = T0, NowMs = 100_000_000 }, null, []);
        Assert.Equal([recent.TimeUtc], evidence.Clears.Select(c => c.TimeUtc));
    }

    [Fact]
    public void TheFindingsOfTheLastDayAreTimedOnTheForwardClock()
    {
        var now = new Moment(T0, 1_000_000);
        var log = new FindingLog(() => now);
        log.Add(new Finding { TimeUtc = now.Utc, Kind = FindingKind.CacheClear, Title = "first" });
        now = new Moment(T0.AddDays(2), now.Ms + 23 * 3_600_000L);           // set forward two days, 23 hours on
        log.Add(new Finding { TimeUtc = now.Utc, Kind = FindingKind.CacheClear, Title = "second" });
        Assert.Equal(["first", "second"], log.Within(TimeSpan.FromDays(1), now).Select(f => f.Title));
        now = new Moment(T0.AddDays(-1), now.Ms + 2 * 3_600_000L);           // set back three days, 2 hours on
        Assert.Equal(["second"], log.Within(TimeSpan.FromDays(1), now).Select(f => f.Title));
    }

    [Fact]
    public void AKeyedValueIsKeptForItsAgeOnTheForwardClock()
    {
        var now = new Moment(T0, 0);
        int made = 0;
        var kept = new KeptBy<int, int>(TimeSpan.FromMinutes(1), 512, () => now);
        Assert.Equal(1, kept.Get(7, _ => ++made));
        now = new Moment(T0.AddDays(1), 59_000);                               // set forward a day: still kept
        Assert.Equal(1, kept.Get(7, _ => ++made));
        now = new Moment(T0.AddHours(-1), 60_000);                             // set back: a minute on, made again
        Assert.Equal(2, kept.Get(7, _ => ++made));
        Assert.Equal(TimeSpan.FromMinutes(1), kept.MaxAge);
    }

    [Fact]
    public void SomethingDueAtASampleIsDueOnItsOwnForwardMoment()
    {
        var every = new Every(TimeSpan.FromSeconds(60));
        Sample S(DateTime utc, long ms) => new(utc, default, [], []) { TickMs = ms };
        Assert.True(every.DueAt(S(T0, 0)));
        Assert.False(every.DueAt(S(T0.AddDays(1), 59_000)));                   // set forward a day: not due early
        Assert.True(every.DueAt(S(T0.AddHours(-1), 60_000)));                  // set back: due at its minute
    }
}
