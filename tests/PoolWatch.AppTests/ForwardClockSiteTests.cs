using PoolWatch.Analysis;
using PoolWatch.Collectors;
using PoolWatch.Core;
using PoolWatch.Core.Cache;
using PoolWatch.Core.Filters;
using PoolWatch.Core.Model;
using PoolWatch.Core.Setup;
using PoolWatch.Core.TokenGuard;
using PoolWatch.Engine;
using PoolWatch.Engine.Setup;
using PoolWatch.Headless;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>The app tests' moments: the forward clock at the same count of milliseconds as the system time.</summary>
internal static class AppClocks
{
    public static Moment At(DateTime utc) => new(utc, utc.Ticks / TimeSpan.TicksPerMillisecond);

    public static long Ms(DateTime utc) => At(utc).Ms;
}

/// <summary>
/// A test's two clocks: time passes on both (<see cref="Pass"/>), and the system clock alone can be set back or forward
/// (<see cref="SetSystemClock"/>), as a person or a time sync does, while the clock that only goes forward goes on.
/// </summary>
internal sealed class TwoClocks
{
    public DateTime Utc { get; private set; } = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
    public long Ms { get; private set; } = 5_000_000;

    public Moment Now() => new(Utc, Ms);

    public void Pass(TimeSpan t)
    {
        Utc += t;
        Ms += (long)t.TotalMilliseconds;
    }

    public void SetSystemClock(TimeSpan by) => Utc += by;
}

/// <summary>
/// The caches and waits of the app timed on the clock that only goes forward: each is given a test's clocks, the system
/// clock is set forward a day and back again past where it was, and each keeps, makes again and waits by the forward clock
/// alone.
/// </summary>
public sealed class ForwardClockSiteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pw-forward-" + Guid.NewGuid().ToString("N"));

    public ForwardClockSiteTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void TheDeviceMapIsReadAgainEvery10MinutesOnTheForwardClock()
    {
        var c = new TwoClocks();
        int builds = 0;
        var map = DevicePaths.NewMap(c.Now, () =>
        {
            builds++;
            return new(StringComparer.OrdinalIgnoreCase) { [@"\Device\HarddiskVolume3"] = "C:" };
        });
        Assert.Equal(@"C:\x", DevicePaths.ToDosPath(@"\Device\HarddiskVolume3\x", map));
        Assert.Equal(1, builds);
        c.SetSystemClock(TimeSpan.FromDays(1));
        c.Pass(TimeSpan.FromMinutes(9));
        DevicePaths.ToDosPath(@"\Device\HarddiskVolume3\x", map);
        Assert.Equal(1, builds); // a day on the system clock is not 10 minutes
        c.SetSystemClock(-TimeSpan.FromDays(1) - TimeSpan.FromHours(1));
        c.Pass(TimeSpan.FromMinutes(1));
        DevicePaths.ToDosPath(@"\Device\HarddiskVolume3\x", map);
        Assert.Equal(2, builds); // 10 forward minutes, though the system clock is behind where it was
        c.Pass(TimeSpan.FromMinutes(9));
        DevicePaths.ToDosPath(@"\Device\HarddiskVolume3\x", map);
        Assert.Equal(2, builds);
        Assert.Equal(TimeSpan.FromMinutes(10), DevicePaths.MapAge);
    }

    [Fact]
    public void TheObjectTypeNamesAreReadAgainEveryHourOnTheForwardClock()
    {
        var c = new TwoClocks();
        using var q = new SystemQueries(c.Now);
        var first = q.ObjectTypeNames();
        Assert.Contains("File", first.Values);
        c.SetSystemClock(TimeSpan.FromDays(1));
        c.Pass(TimeSpan.FromMinutes(59));
        Assert.Same(first, q.ObjectTypeNames());
        c.SetSystemClock(-TimeSpan.FromDays(2));
        c.Pass(TimeSpan.FromMinutes(1));
        var again = q.ObjectTypeNames();
        Assert.NotSame(first, again);
        c.SetSystemClock(TimeSpan.FromHours(3));
        c.Pass(TimeSpan.FromMinutes(30));
        Assert.Same(again, q.ObjectTypeNames());
    }

    [Fact]
    public void TheEventLogStateIsSavedAtMostEvery10MinutesOnTheForwardClock()
    {
        var c = new TwoClocks();
        string file = Path.Combine(_dir, "eventlog-last.txt");
        var feed = new EventLogFeed(_ => { }, file, null, c.Now);
        LogEvent At(DateTime t) => new(t, "System", "FilterManager", 6, null, []);
        var t1 = new DateTime(2026, 11, 4, 11, 0, 0, DateTimeKind.Utc);
        feed.Enqueue(At(t1));
        feed.Drain();
        Assert.Equal(t1.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), File.ReadAllText(file));
        feed.Enqueue(At(t1.AddMinutes(1)));
        c.SetSystemClock(TimeSpan.FromDays(1));
        c.Pass(TimeSpan.FromMinutes(9));
        feed.Drain();
        Assert.Equal(t1.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), File.ReadAllText(file)); // not yet
        c.SetSystemClock(-TimeSpan.FromDays(1) - TimeSpan.FromHours(1));
        c.Pass(TimeSpan.FromMinutes(1));
        feed.Drain();
        Assert.Equal(t1.AddMinutes(1).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), File.ReadAllText(file));
        Assert.Equal(TimeSpan.FromMinutes(10), EventLogFeed.SaveEvery);
    }

    [Fact]
    public void AKernelProcessEventIsDatedAsItArrivedLessItsCounterWaitElseAStartByTraceEventsTimeAndAnExitAsItArrived()
    {
        // The system time as the event arrives, less how long the performance counter says it waited (not TraceEvent's own
        // time of it, reckoned from the system time the session started at: here the system clock was set forward a day
        // since, so TraceEvent's time is a day behind).
        var arrived = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        var traceEvent = arrived.AddDays(-1).AddSeconds(-40);
        const long PerSecond = 10_000_000;
        foreach (var at in new Func<long, long, long, DateTime>[]
            {
                (eventQpc, nowQpc, perSecond) => ProcessEtwCollector.KernelStartUtc(eventQpc, nowQpc, perSecond, arrived, traceEvent),
                (eventQpc, nowQpc, perSecond) => ProcessEtwCollector.KernelExitUtc(eventQpc, nowQpc, perSecond, arrived),
            })
        {
            Assert.Equal(arrived.AddMilliseconds(-800), at(5 * PerSecond, 5 * PerSecond + 8_000_000, PerSecond));
            Assert.Equal(arrived.AddSeconds(-30), at(0, 30 * PerSecond, PerSecond));
            Assert.Equal(arrived, at(5 * PerSecond, 5 * PerSecond, PerSecond));
        }
        // A stamp after now, a wait over 30 s (a stamp on another counter, or an event delivered that late) or no counter: a
        // start keeps TraceEvent's time for its create-time estimate (a start delivered that late, dated as it arrived, would
        // be more than 5 s off the create time Windows gives), and an exit is dated as it arrives.
        foreach (var (eventQpc, nowQpc, perSecond) in new (long, long, long)[] { (5 * PerSecond, 4 * PerSecond, PerSecond), (0, 31 * PerSecond, PerSecond), (0, 5, 0) })
        {
            Assert.Null(ProcessEtwCollector.DeliveredUtc(eventQpc, nowQpc, perSecond, arrived));
            Assert.Equal(traceEvent, ProcessEtwCollector.KernelStartUtc(eventQpc, nowQpc, perSecond, arrived, traceEvent));
            Assert.Equal(arrived, ProcessEtwCollector.KernelExitUtc(eventQpc, nowQpc, perSecond, arrived));
        }
        Assert.Equal(TimeSpan.FromSeconds(30), ProcessEtwCollector.MaxDeliveryWait);
    }

    [Fact]
    public void AnEventRecordIsReadOntoTheForwardClockAsItArrives()
    {
        // A record stamped 2 s before it arrives; the system clock is then set back an hour before the sampler drains it.
        // Its time on the forward clock is the one read as it arrived.
        var c = new TwoClocks();
        var feed = new EventLogFeed(_ => { }, null, null, c.Now);
        feed.Enqueue(new LogEvent(c.Utc.AddSeconds(-2), "System", "FilterManager", 6, null, []));
        long arrived = c.Ms;
        c.Pass(TimeSpan.FromSeconds(8));
        c.SetSystemClock(-TimeSpan.FromHours(1));
        Assert.Equal(arrived - 2000, Assert.Single(feed.Drain()).Ms);
    }

    [Fact]
    public void ADrivesAnswerIsKept5MinutesOnTheForwardClock()
    {
        var c = new TwoClocks();
        int asked = 0;
        var probe = new PoolWatch.Analysis.Diagnostics.FileProbe(c.Now, _ => { asked++; return 3; });
        Assert.True(probe.IsLocalFixed(@"C:\x"));
        Assert.Equal(1, asked);
        c.SetSystemClock(TimeSpan.FromDays(1));
        c.Pass(TimeSpan.FromMinutes(4));
        Assert.True(probe.IsLocalFixed(@"C:\y"));
        Assert.Equal(1, asked);
        c.SetSystemClock(-TimeSpan.FromDays(1) - TimeSpan.FromHours(1));
        c.Pass(TimeSpan.FromMinutes(1));
        Assert.True(probe.IsLocalFixed(@"c:\z"));
        Assert.Equal(2, asked);
        Assert.Equal(TimeSpan.FromMinutes(5), PoolWatch.Analysis.Diagnostics.FileProbe.DriveAge);
    }

    /// <summary>
    /// A sleep for a wait under test: time passes on both clocks, and the system clock is set forward a day at the 10th
    /// sleep and back two days at the 20th, so a wait timed on it would end at once or never.
    /// </summary>
    private static Action<TimeSpan> Sleep(TwoClocks c, List<TimeSpan> slept) => t =>
    {
        slept.Add(t);
        c.Pass(t);
        if (slept.Count == 10) c.SetSystemClock(TimeSpan.FromDays(1));
        if (slept.Count == 20) c.SetSystemClock(-TimeSpan.FromDays(2));
        if (slept.Count > 100_000) throw new InvalidOperationException("the wait does not end on the forward clock");
    };

    private static TimeSpan Sum(List<TimeSpan> slept) => slept.Aggregate(TimeSpan.Zero, (a, b) => a + b);

    [Fact]
    public void TheEndOfRunWaitForACaptureIs6MinutesOnTheForwardClock()
    {
        var c = new TwoClocks();
        var slept = new List<TimeSpan>();
        Assert.False(AnalysisPipeline.WaitForCaptures(() => true, c.Now, Sleep(c, slept)));
        Assert.Equal(TimeSpan.FromMinutes(6), Sum(slept));
        Assert.All(slept, t => Assert.Equal(TimeSpan.FromMilliseconds(200), t));
        slept.Clear();
        int looks = 0;
        Assert.True(AnalysisPipeline.WaitForCaptures(() => ++looks < 5, c.Now, Sleep(c, slept)));
        Assert.Equal(4, slept.Count);
    }

    [Fact]
    public void TheWaitForTranscriptSearchesIsTimedOnTheForwardClock()
    {
        var c = new TwoClocks();
        var slept = new List<TimeSpan>();
        Assert.False(WindowsIncidentHost.WaitWhilePending(() => 1, TimeSpan.FromSeconds(10), c.Now, Sleep(c, slept)));
        Assert.Equal(TimeSpan.FromSeconds(10), Sum(slept));
        Assert.All(slept, t => Assert.Equal(TimeSpan.FromMilliseconds(100), t));
        slept.Clear();
        Assert.True(WindowsIncidentHost.WaitWhilePending(() => 0, TimeSpan.FromSeconds(10), c.Now, Sleep(c, slept)));
        Assert.Empty(slept);
    }

    [Fact]
    public void AHeadlessRunsDurationIsTimedOnTheForwardClock()
    {
        var c = new TwoClocks();
        var slept = new List<TimeSpan>();
        Assert.False(HeadlessRunner.RunFor(30, () => false, Sleep(c, slept), c.Now));
        Assert.Equal(TimeSpan.FromSeconds(30), Sum(slept));
        Assert.All(slept, t => Assert.Equal(TimeSpan.FromMilliseconds(500), t));
        slept.Clear();
        Assert.True(HeadlessRunner.RunFor(null, () => slept.Count >= 25, Sleep(c, slept), c.Now)); // no duration: until stopped
        Assert.Equal(25, slept.Count);
    }

    [Fact]
    public void TheSelfTestsWaitForItsChildsExitIs15SecondsOnTheForwardClock()
    {
        var c = new TwoClocks();
        var slept = new List<TimeSpan>();
        Assert.False(SelfTest.WaitForExit(() => false, c.Now, Sleep(c, slept)));
        Assert.Equal(TimeSpan.FromSeconds(15), Sum(slept));
        Assert.All(slept, t => Assert.Equal(TimeSpan.FromMilliseconds(250), t));
        slept.Clear();
        Assert.True(SelfTest.WaitForExit(() => slept.Count >= 3, c.Now, Sleep(c, slept)));
        Assert.Equal(3, slept.Count);
    }

    [Fact]
    public void TheTagAndProcessRowsAreWrittenEveryMinuteOnTheForwardClock()
    {
        // A sample every 10 s; the system clock is set back an hour at the 3rd and forward a day at the 9th. The tag and
        // process rows are written every 60 forward seconds: at the 1st, 7th and 13th samples, neither stopped by the hour
        // back nor written at once by the day forward.
        var c = new TwoClocks();
        var paths = new AppPaths(Path.Combine(_dir, "data"), null, secure: false);
        var tag = new PoolTagEntry(PoolTag.Parse("Leak"), 0, 0, 0, 1, 0, 64UL << 20);
        var proc = new ProcessSample { Pid = 77, ParentPid = 4, CreateTime = 1, ImageName = "x.exe" };
        var registry = new ProcessRegistry();
        using (var sink = new CsvSink(paths, new Settings(), verbose: false))
        {
            for (int i = 0; i < 13; i++)
            {
                if (i == 2) c.SetSystemClock(-TimeSpan.FromHours(1));
                if (i == 8) c.SetSystemClock(TimeSpan.FromDays(1));
                var at = c.Now();
                sink.WriteSample(new Sample(at.Utc, default, [tag], [proc]) { TickMs = at.Ms }, registry);
                c.Pass(TimeSpan.FromSeconds(10));
            }
        }
        int Rows(string name) => Directory.EnumerateFiles(Path.Combine(_dir, "data"), name, SearchOption.AllDirectories).Sum(f => File.ReadAllLines(f).Length - 1);
        Assert.Equal(3, Rows("tags.csv"));
        Assert.Equal(3, Rows("processes.csv"));
    }

    [Fact]
    public void TheLagProbesResumeGapAndCompositorRetryAreTimedOnTheForwardClock()
    {
        var c = new TwoClocks();
        int asked = 0;
        using var prober = new LagProber(TimeSpan.FromSeconds(10), Path.Combine(_dir, "probe.tmp"), () => default, clock: c.Now,
            dwmFrames: () => { asked++; return null; });
        // The compositor did not answer: asked again 5 minutes on by the forward clock, whatever the system clock says.
        Assert.Equal(c.Ms, prober.Probe().TickMs);
        Assert.Equal(1, asked);
        c.SetSystemClock(TimeSpan.FromDays(1));
        c.Pass(TimeSpan.FromMinutes(4));
        prober.Probe();
        Assert.Equal(1, asked);
        c.SetSystemClock(-TimeSpan.FromDays(1) - TimeSpan.FromHours(1));
        c.Pass(TimeSpan.FromMinutes(1));
        var probe = prober.Probe();
        Assert.Equal(2, asked);
        Assert.Equal(c.Utc, probe.TimeUtc);
        // A clock set forward a day between two probes is not a sleep; a gap past the interval on the forward clock is.
        Assert.False(prober.Resumed(0));
        c.SetSystemClock(TimeSpan.FromDays(1));
        c.Pass(TimeSpan.FromSeconds(10));
        Assert.False(prober.Resumed(0));
        c.Pass(TimeSpan.FromMinutes(3));
        Assert.True(prober.Resumed(0));
    }
}
