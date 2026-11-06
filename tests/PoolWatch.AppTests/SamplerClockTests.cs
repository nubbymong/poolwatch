using PoolWatch.Core;
using PoolWatch.Core.Model;
using PoolWatch.Engine;

namespace PoolWatch.AppTests;

/// <summary>
/// The sampler's schedule and clock (<see cref="MonitorEngine.SamplerClock"/>), the one the sampler's tick reads its two
/// times from and asks whether each slower job is due, handing nothing of its own: the tick's sample is stamped with the
/// two times the tick began at, the system clock for the times shown and written and Windows' time since it started
/// (Environment.TickCount64) for every interval. Each slower job (the trace's restart, services, handles, volumes,
/// retention, the hourly profile) runs on its own interval, the settings' for the first three, and keeps its own next due
/// time; the first profile.json is due an hour after the clock is made, the trace's restart a minute after the first
/// tick; and each falls due on Windows' time alone, so setting the system clock back an hour never holds one back, nor
/// does setting it forward run one early. And the load point of a sample, which "PC is saturated" is timed on, carries
/// both of its times.
/// </summary>
public sealed class SamplerClockTests
{
    private static readonly DateTime Wall0 = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
    private const long Tick0 = 9_000_000;
    private static readonly MonitorEngine.TickTime Made = new(Wall0.AddSeconds(-5), Tick0 - 5_000);

    /// <summary>
    /// A clock that reads the times it is given, one at each read, the last one again after that: the first is read as it
    /// is made (<see cref="Made"/> unless given), the next ones as each tick begins.
    /// </summary>
    private static MonitorEngine.SamplerClock Reading(params MonitorEngine.TickTime[] times)
    {
        int i = 0;
        return new MonitorEngine.SamplerClock(() => times[Math.Min(i++, times.Length - 1)]);
    }

    private static MonitorEngine.TickTime At(long ms, DateTime? wall = null) => new(wall ?? Wall0, Tick0 + ms);

    /// <summary>Settings with each of the three intervals they set different: services every minute, handles every 5, volumes every 15.</summary>
    private static Settings Intervals() => new() { ServiceIntervalSeconds = 60, HandleIntervalSeconds = 300, VolumeIntervalMinutes = 15 };

    private static readonly MonitorEngine.SamplerClock.Job[] Jobs = Enum.GetValues<MonitorEngine.SamplerClock.Job>();

    /// <summary>
    /// The jobs due at <paramref name="tick"/>, each asked once, in the order the engine asks them; the trace's restart too,
    /// which the engine asks only while the trace is not running.
    /// </summary>
    private static List<MonitorEngine.SamplerClock.Job> DueAt(MonitorEngine.SamplerClock.ThisTick tick, Settings settings) =>
        Jobs.Where(j => tick.Due(j, settings)).ToList();

    [Fact]
    public void ATickReadsTheSystemClockAndTheTimeSinceWindowsStarted()
    {
        long before = Environment.TickCount64;
        DateTime wallBefore = DateTime.UtcNow;
        var at = MonitorEngine.TickTime.Now();
        Assert.InRange(at.Ms, before, Environment.TickCount64);
        Assert.InRange(at.Utc, wallBefore, DateTime.UtcNow);
        // The sampler's own clock reads these two.
        var clock = new MonitorEngine.SamplerClock();
        before = Environment.TickCount64;
        wallBefore = DateTime.UtcNow;
        var begun = clock.Begin().Time;
        Assert.InRange(begun.Ms, before, Environment.TickCount64);
        Assert.InRange(begun.Utc, wallBefore, DateTime.UtcNow);
        Assert.Equal(begun, clock.At);
    }

    /// <summary>Each sample is stamped with the two times its tick began at, also after the system clock was set back.</summary>
    [Fact]
    public void EachSampleIsStampedWithTheTwoTimesItsTickBeganAt()
    {
        var clock = Reading(Made, At(0), At(10_000, Wall0.AddHours(-1)));
        var tick = clock.Begin();
        var sample = tick.NewSample(1_000, 2_000, new SystemCounters(10, 20, 30, 0, 40, 50),
            [new PoolTagEntry(PoolTag.Parse("Reff"), 1, 0, 100, 1, 0, 300), new PoolTagEntry(PoolTag.Parse("Vi54"), 1, 0, 5, 1, 0, 7)],
            [new ProcessSample { Pid = 4, ImageName = "System", WorkingSet = 1 }, new ProcessSample { Pid = 8, ImageName = "Memory Compression", WorkingSet = 4_096 }]);
        Assert.Equal((Wall0, Tick0), (sample.TimeUtc, sample.TickMs));
        Assert.Equal(new PoolTotals(1_000, 2_000, 105, 307), sample.Totals);
        Assert.Equal(new SystemCounters(10, 20, 30, 4_096, 40, 50), sample.System);
        // The next tick, ten seconds later by Windows' time since it started, after the system clock was set back an hour.
        var next = clock.Begin().NewSample(0, 0, new SystemCounters(0, 0, 0, 0, 0, 0), [], []);
        Assert.Equal((Wall0.AddHours(-1), Tick0 + 10_000), (next.TimeUtc, next.TickMs));
        Assert.Equal(At(10_000, Wall0.AddHours(-1)), clock.At);
    }

    /// <summary>The load point of a sample carries both of its times: "PC is saturated" times how long load lasts on the second.</summary>
    [Fact]
    public void TheLoadPointOfASampleCarriesItsTwoTimes()
    {
        var s = new Sample(Wall0, new PoolTotals(0, 0, 0, 0), [], []) { TickMs = Tick0 };
        var p = PoolWatch.Analysis.Diagnostics.PointOf(s, 0.9, 30, 24, probeTripping: true, faults: null);
        Assert.Equal((Wall0, Tick0), (p.TimeUtc, p.TickMs));
        Assert.Equal((0.9, (double?)30, 24, true), (p.CpuFraction, p.RunQueue, p.Cores, p.ProbeTripping));
    }

    /// <summary>
    /// Each job's interval is its own: the services, the handle scan and the volumes the settings' (read at each tick), the
    /// trace's restart a minute, retention six hours, profile.json an hour.
    /// </summary>
    [Fact]
    public void EachJobRunsOnItsOwnInterval()
    {
        var s = Intervals();
        Assert.Equal([TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(6), TimeSpan.FromHours(1)],
            Jobs.Select(j => MonitorEngine.SamplerClock.Every(j, s)));
        Assert.Equal([MonitorEngine.SamplerClock.Job.EtwRetry, MonitorEngine.SamplerClock.Job.Services, MonitorEngine.SamplerClock.Job.Handles,
                      MonitorEngine.SamplerClock.Job.Volumes, MonitorEngine.SamplerClock.Job.Retention, MonitorEngine.SamplerClock.Job.Profile], Jobs);
        // A change to the settings applies from the next tick.
        s.ServiceIntervalSeconds = 30;
        Assert.Equal(TimeSpan.FromSeconds(30), MonitorEngine.SamplerClock.Every(MonitorEngine.SamplerClock.Job.Services, s));
    }

    /// <summary>
    /// The schedule a run of ticks gives: at the first tick every job that has never run is due, except the trace's restart
    /// (first due a minute after the first tick: the engine has just started the trace) and profile.json (first due an
    /// hour after the clock was made, with the engine). From then on each job is due on its own interval and its own next
    /// due time, on Windows' time since it started alone: the system clock set forward a day, then back an hour, changes
    /// nothing.
    /// </summary>
    [Fact]
    public void EachJobFallsDueOnItsOwnTimeOnTheClockThatOnlyGoesForward()
    {
        var s = Intervals();
        var clock = Reading(Made,
            At(0),                                   // the first tick, 5 s after the clock was made
            At(59_000, Wall0.AddDays(1)),            // 59 s on, the system clock set forward a day: nothing is due
            At(60_000, Wall0.AddHours(-1)),          // a minute on, set back an hour: the trace's restart and services
            At(300_000, Wall0.AddHours(-1)),         // five minutes: handles too
            At(900_000),                             // fifteen: volumes too
            At(3_595_000),                           // an hour after the clock was made: profile.json too, not retention
            At(21_600_000));                         // six hours: every job
        var retry = MonitorEngine.SamplerClock.Job.EtwRetry;
        Assert.Equal(Tick0 - 5_000 + 3_600_000, clock.NextMs(MonitorEngine.SamplerClock.Job.Profile)); // set as the clock was made
        Assert.Equal(long.MinValue, clock.NextMs(retry));                                               // not before the first tick
        Assert.Equal(["Services", "Handles", "Volumes", "Retention"], DueAt(clock.Begin(), s).Select(j => j.ToString()));
        Assert.Equal(Tick0 + 60_000, clock.NextMs(retry)); // a minute after the first tick
        Assert.Equal([Tick0 + 60_000, Tick0 + 60_000, Tick0 + 300_000, Tick0 + 900_000, Tick0 + 21_600_000, Tick0 - 5_000 + 3_600_000],
            Jobs.Select(clock.NextMs));
        Assert.Empty(DueAt(clock.Begin(), s));
        Assert.Equal(["EtwRetry", "Services"], DueAt(clock.Begin(), s).Select(j => j.ToString()));
        Assert.Equal(["EtwRetry", "Services", "Handles"], DueAt(clock.Begin(), s).Select(j => j.ToString()));
        Assert.Equal(["EtwRetry", "Services", "Handles", "Volumes"], DueAt(clock.Begin(), s).Select(j => j.ToString()));
        Assert.Equal(["EtwRetry", "Services", "Handles", "Volumes", "Profile"], DueAt(clock.Begin(), s).Select(j => j.ToString()));
        Assert.Equal(Jobs.Select(j => j.ToString()), DueAt(clock.Begin(), s).Select(j => j.ToString()));
    }

    /// <summary>
    /// profile.json is first due an hour after the clock is made (with the engine), read then: not at the first tick, and
    /// not an hour after it.
    /// </summary>
    [Fact]
    public void TheFirstProfileIsDueAnHourAfterTheClockIsMade()
    {
        var profile = MonitorEngine.SamplerClock.Job.Profile;
        var s = Intervals();
        var clock = Reading(Made, At(0), At(3_594_999, Wall0.AddHours(-2)), At(3_595_000, Wall0.AddHours(-2)));
        Assert.False(clock.Begin().Due(profile, s));
        Assert.False(clock.Begin().Due(profile, s)); // 1 ms short of an hour after it was made, the system clock set back
        Assert.True(clock.Begin().Due(profile, s));
        Assert.Equal(Tick0 + 3_595_000 + 3_600_000, clock.NextMs(profile));
    }

    /// <summary>
    /// The trace's restart is first due a minute after the first tick, which runs as soon as the engine has started the
    /// trace: a trace that did not start is not tried again at once. Asked only while the trace is not running, it is then
    /// due every minute.
    /// </summary>
    [Fact]
    public void TheTracesRestartIsFirstDueAMinuteAfterTheFirstTick()
    {
        var retry = MonitorEngine.SamplerClock.Job.EtwRetry;
        var s = Intervals();
        // Made long before the first tick: the minute counts from the tick.
        var clock = Reading(new MonitorEngine.TickTime(Wall0.AddHours(-3), Tick0 - 600_000), At(0), At(59_999, Wall0.AddHours(-1)),
            At(60_000, Wall0.AddHours(-1)), At(100_000), At(120_000));
        Assert.False(clock.Begin().Due(retry, s));
        Assert.False(clock.Begin().Due(retry, s));
        Assert.True(clock.Begin().Due(retry, s));
        Assert.False(clock.Begin().Due(retry, s));
        Assert.True(clock.Begin().Due(retry, s));
    }

    /// <summary>A job asked for (a handle scan when an alert fires) is due at once, and its next one is timed from that tick.</summary>
    [Fact]
    public void AJobAskedForIsDueAtOnce()
    {
        var handles = MonitorEngine.SamplerClock.Job.Handles;
        var s = Intervals();
        var clock = Reading(Made, At(0), At(10_000), At(20_000));
        Assert.True(clock.Begin().Due(handles, s));
        Assert.False(clock.Begin().Due(handles, s));
        Assert.True(clock.Begin().Due(handles, s, now: true));
        Assert.Equal(Tick0 + 20_000 + 300_000, clock.NextMs(handles));
    }

    /// <summary>
    /// A tick is had only by beginning one: ThisTick is a class (never an empty default, as a struct can be), and its one
    /// constructor takes the clock alone and begins the tick (it reads the two times, which the clock then keeps, and does
    /// the first tick's set-up), so no tick is made with times of its own and none skips that set-up.
    /// </summary>
    [Fact]
    public void ATickIsHadOnlyByBeginningOne()
    {
        var type = typeof(MonitorEngine.SamplerClock.ThisTick);
        Assert.False(type.IsValueType);
        var made = Assert.Single(type.GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic));
        Assert.Equal([typeof(MonitorEngine.SamplerClock)], made.GetParameters().Select(p => p.ParameterType));
        var clock = Reading(Made, At(0), At(1_000));
        var tick = (MonitorEngine.SamplerClock.ThisTick)made.Invoke([clock]);
        Assert.Equal((At(0), At(0)), (tick.Time, clock.At));
        Assert.Equal(Tick0 + 60_000, clock.NextMs(MonitorEngine.SamplerClock.Job.EtwRetry)); // the first tick's set-up
        Assert.Equal(At(1_000), clock.Begin().Time);
    }

    /// <summary>
    /// The engine's tick begins once, stamps its sample with that tick, and puts each slower job's work under that job's
    /// own question: asking the handle scan's for the services' refresh (or any other swap) turns this red, as does a
    /// second question for one job or one left out.
    /// </summary>
    [Fact]
    public void EachSlowerJobOfTheEnginesTickIsUnderItsOwnJobsQuestion()
    {
        string source = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Engine", "MonitorEngine.cs"));
        string tick = UninstallerTests.OneLine(System.Text.RegularExpressions.Regex.Replace(ScriptCode.CSharpMember(source, "private void Tick()").Value, @"//[^\n]*", ""));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(tick, @"\.Begin\("));
        Assert.Contains("var at = _clock.Begin(); var now = at.Time.Utc;", tick);
        Assert.Contains("var sample = at.NewSample(cPaged, cNonPaged, system, tags, procs);", tick);
        Assert.Contains("if (!_etw.Running && at.Due(SamplerClock.Job.EtwRetry, _settings)) { if (SampleCount > 0) _log.Info(\"ETW process session is not running; restarting it\"); _etw.Start(); }", tick);
        Assert.Contains("if (at.Due(SamplerClock.Job.Services, _settings)) { using (Profiler.Stage(\"tick.services\")) RefreshServices(now); }", tick);
        Assert.Contains("if (at.Due(SamplerClock.Job.Handles, _settings, now: requested)) { _handleScanRequest.Reset(); if (_skipNextHandleScan && !requested) " +
                        "_skipNextHandleScan = false; else using (Profiler.Stage(\"tick.handleScan\")) ScanHandles(now, at.Time.Ms, procs); }", tick);
        Assert.Contains("if (at.Due(SamplerClock.Job.Volumes, _settings)) { try { Volumes = VolumeCollector.Query(); _sink.WriteVolumes(Volumes, now); }", tick);
        Assert.Contains("if (at.Due(SamplerClock.Job.Retention, _settings)) _paths.ApplyRetention(_settings.RetentionDays, _log.Info, _retention);", tick);
        Assert.Contains("if (Profile is { } profile && at.Due(SamplerClock.Job.Profile, _settings)) WriteProfile(profile);", tick);
        foreach (var job in Jobs)
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(tick, $@"\.Due\(SamplerClock\.Job\.{job}\b"));
        Assert.Equal(Jobs.Length, System.Text.RegularExpressions.Regex.Matches(tick, @"\.Due\(").Count);
    }
}
