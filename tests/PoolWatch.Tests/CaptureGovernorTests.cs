using PoolWatch.Core;
using PoolWatch.Core.Cache;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Lag;
using PoolWatch.Core.Model;

namespace PoolWatch.Tests;

/// <summary>
/// C1 (review): trace rate limiting. On a busy many-core PC, confirmation traces that ran back to back lagged typing. One governor for every trace; the lag probe pauses around PoolWatch's own work; no automatic clear
/// during an experiment; the CPU trace is off by default.
/// </summary>
public class CaptureGovernorTests
{
    private static readonly DateTime T0 = new(2026, 10, 26, 11, 20, 0, DateTimeKind.Utc);

    /// <summary>A quiet PC: nothing running, 24 cores, 5 threads waiting, 30% CPU, last input a minute ago.</summary>
    private static readonly CaptureConditions Quiet = new(false, 24, RunQueue: 5, CpuFraction: 0.3, SinceLastInput: TimeSpan.FromMinutes(1));

    /// <summary>The governor's monotonic clock (Now) and the wall clock the requests carry for display (Wall).</summary>
    private sealed class Clock
    {
        public TimeSpan Now = TimeSpan.FromHours(100);
        public DateTime Wall = T0;
        public void Advance(TimeSpan t) { Now += t; Wall += t; }
    }

    private static (CaptureGovernor g, Clock clock, Settings s) New(Action<Settings>? configure = null)
    {
        var s = new Settings { CaptureOnAlert = true };
        configure?.Invoke(s);
        s.Normalize();
        var clock = new Clock();
        return (new CaptureGovernor(s, () => clock.Now), clock, s);
    }

    private static CaptureRequest Alert(Clock c, string why = "incident A", object? context = null) =>
        new(CaptureSource.PoolAlert, c.Wall, c.Now, why) { Context = context };

    private static CaptureRequest Manual(Clock c) => new(CaptureSource.Manual, c.Wall, c.Now, "Capture now");

    [Fact]
    public void TheWallClockNeverMovesTheCap()
    {
        // C1 d (review round 2): the governor reads only its monotonic clock. A wall clock that jumps (NTP step, DST, the
        // owner setting the time) changes the requests' displayed times, never the hourly cap or the lag trip's 15 minutes.
        var (g, c, _) = New();
        RunCapture(g, c, Alert(c), TimeSpan.FromSeconds(60));
        c.Advance(TimeSpan.FromMinutes(20));
        c.Wall += TimeSpan.FromHours(3); // the wall clock jumps three hours ahead
        Assert.Contains("ended 20 min ago", g.Decide(Alert(c, "incident B"), Quiet).Reason);
        c.Wall -= TimeSpan.FromHours(30); // and then far back
        c.Advance(TimeSpan.FromMinutes(40));
        Assert.True(g.Decide(Alert(c, "incident C"), Quiet).Start); // 60 min of monotonic time: allowed

        var (g2, c2, _) = New(s => { s.CpuTraceEnabled = true; s.DeeperCapture = true; });
        g2.Decide(new CaptureRequest(CaptureSource.LagTrip, c2.Wall, c2.Now, "lag"), Quiet with { CpuFraction = 0.99 }); // held back
        c2.Wall -= TimeSpan.FromHours(2);   // a wall clock step back does not make the trip "younger" ...
        c2.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(CaptureVerdict.Off, g2.TryPending(Quiet)?.Verdict); // ... it is dropped after 15 monotonic minutes
    }

    /// <summary>Runs one capture of <paramref name="length"/> through the governor (start, then end).</summary>
    private static void RunCapture(CaptureGovernor g, Clock c, CaptureRequest r, TimeSpan length)
    {
        g.Started(r);
        c.Advance(length);
        g.Finished();
    }

    [Fact]
    public void DefaultsAreTheLightOnes()
    {
        var s = new Settings();
        Assert.False(s.CpuTraceEnabled);     // was on: a 30 s CPU trace with stacks on every Capture now and lag trip
        Assert.False(s.DeeperCapture);
        Assert.Equal(60, s.AutoCaptureMinMinutes);
        Assert.False(s.CaptureOnAlert);
        var low = new Settings { AutoCaptureMinMinutes = 0 };
        low.Normalize();
        Assert.Equal(1, low.AutoCaptureMinMinutes); // never back to back, whatever the settings say
        var nan = new Settings { AutoCaptureMinMinutes = double.NaN };
        nan.Normalize();
        Assert.Equal(60, nan.AutoCaptureMinMinutes);
    }

    [Fact]
    public void SettingsSavedBeforeC1LoseTheOldCpuTraceDefault()
    {
        string f = Path.Combine(Path.GetTempPath(), "pw-settings-c1-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(f, "{ \"settingsVersion\": 2, \"cpuTraceEnabled\": true }");
            var old = Settings.Load(f);
            Assert.False(old.CpuTraceEnabled); // the old default written out by Save, not a choice
            Assert.Equal(Settings.CurrentSettingsVersion, old.SettingsVersion);
            File.WriteAllText(f, "{ \"settingsVersion\": 3, \"cpuTraceEnabled\": true }");
            Assert.True(Settings.Load(f).CpuTraceEnabled); // turned on after the change: kept

            // C1 e (review round 2): an explicit --config file holds only what its author wrote: never migrated, with or
            // without settingsVersion (a CI config asking for the CPU trace keeps it; a 60 s handle census stays 60 s).
            File.WriteAllText(f, "{ \"cpuTraceEnabled\": true, \"handleIntervalSeconds\": 60 }");
            var cfg = Settings.Load(f, explicitConfig: true);
            Assert.True(cfg.CpuTraceEnabled);
            Assert.Equal(60, cfg.HandleIntervalSeconds);
            var dflt = Settings.Load(f); // the same file as the default settings.json: migrated
            Assert.False(dflt.CpuTraceEnabled);
            Assert.Equal(300, dflt.HandleIntervalSeconds);
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void AutomaticCapturesArePoolEventsOnlyByDefault()
    {
        var (g, c, s) = New();
        var d = g.Decide(Alert(c), Quiet);
        Assert.True(d.Start);
        Assert.Equal(new CaptureScope(PoolEvents: true, KernelFile: false, Cpu: false), d.Scope);
        Assert.Equal("pool events only", d.Scope.Describe());
        Assert.Equal("pool events only (automatic, on a pool alert)", d.Summary);

        // The lag trip's CPU trace: nothing unless CpuTraceEnabled, and a deeper capture even then.
        var lag = new CaptureRequest(CaptureSource.LagTrip, c.Wall, c.Now, "lag probe tripped");
        var off = g.Decide(lag, Quiet);
        Assert.Equal(CaptureVerdict.Off, off.Verdict);
        Assert.Null(off.Note); // CPU traces are off: nothing to tell
        s.CpuTraceEnabled = true;
        var needsDeeper = g.Decide(lag, Quiet);
        Assert.Equal(CaptureVerdict.Off, needsDeeper.Verdict);
        Assert.Contains("DeeperCapture", needsDeeper.Note);
        Assert.Null(g.Pending); // a settings answer is not held back
        Assert.Equal(0, g.SkippedCount);

        // DeeperCapture: what the automatic captures recorded before C1.
        s.DeeperCapture = true;
        Assert.Equal(new CaptureScope(true, true, false), CaptureGovernor.ScopeFor(CaptureSource.PoolAlert, s));
        Assert.Equal(new CaptureScope(false, false, true), CaptureGovernor.ScopeFor(CaptureSource.LagTrip, s));
        Assert.Equal("CPU only", CaptureGovernor.ScopeFor(CaptureSource.LagTrip, s).Describe());
        Assert.True(g.Decide(lag, Quiet).Start);

        // Capture now: pool events + Kernel-File, CPU only with CpuTraceEnabled.
        Assert.Equal("pool events + Kernel-File + CPU", CaptureGovernor.ScopeFor(CaptureSource.Manual, s).Describe());
        Assert.Equal("pool events + Kernel-File", CaptureGovernor.ScopeFor(CaptureSource.Manual, new Settings()).Describe());
    }

    [Fact]
    public void CaptureOnAlertOffMeansNoAlertCapture()
    {
        var (g, c, _) = New(s => s.CaptureOnAlert = false);
        var d = g.Decide(Alert(c), Quiet);
        Assert.Equal(CaptureVerdict.Off, d.Verdict);
        Assert.Null(d.Note);
        Assert.Null(g.Pending);
    }

    [Fact]
    public void AtMostOneAutomaticCapturePerHour()
    {
        var (g, c, _) = New();
        var first = Alert(c, "incident A");
        Assert.True(g.Decide(first, Quiet).Start);
        RunCapture(g, c, first, TimeSpan.FromSeconds(70));

        c.Advance(TimeSpan.FromMinutes(20));
        var second = g.Decide(Alert(c, "incident B"), Quiet);
        Assert.Equal(CaptureVerdict.Skipped, second.Verdict);
        Assert.Contains("at most one automatic capture per 60 min", second.Reason);
        Assert.Contains("ended 20 min ago", second.Reason);
        Assert.StartsWith("capture skipped: at most one automatic capture per 60 min", second.Note);
        Assert.EndsWith("kept as the next capture to run when the PC allows", second.Note);

        c.Advance(TimeSpan.FromMinutes(40)); // 60 min after the end
        Assert.True(g.Decide(Alert(c, "incident C"), Quiet).Start);
    }

    [Fact]
    public void NeverBackToBackCountedFromTheEndOfTheLastCaptureOfAnyKind()
    {
        // A long capture: the interval after its START is not enough, it must be the interval after its END.
        var (g, c, _) = New(s => s.AutoCaptureMinMinutes = 10);
        var r = Alert(c);
        RunCapture(g, c, r, TimeSpan.FromMinutes(5));
        c.Advance(TimeSpan.FromMinutes(6)); // 11 min after the start, 6 after the end
        var d = g.Decide(Alert(c, "incident B"), Quiet);
        Assert.False(d.Start);
        Assert.Contains("ended 6 min ago", d.Reason);

        // Capture now counts too: an automatic capture never starts right after the owner's.
        var (g2, c2, _) = New();
        RunCapture(g2, c2, Manual(c2), TimeSpan.FromSeconds(90));
        c2.Advance(TimeSpan.FromSeconds(2));
        Assert.False(g2.Decide(Alert(c2), Quiet).Start);
    }

    [Fact]
    public void NoQueueWhileRunningButTheNewestRequestIsRemembered()
    {
        var (g, c, _) = New();
        var running = Quiet with { CaptureRunning = true };
        var a = g.Decide(Alert(c, "incident A", "A"), running);
        Assert.Equal(CaptureVerdict.Skipped, a.Verdict);
        Assert.Equal("another capture is running", a.Reason);
        Assert.True(a.Kept);
        Assert.Null(a.Replaced);

        c.Advance(TimeSpan.FromSeconds(30));
        var b = g.Decide(Alert(c, "incident B", "B"), running);
        Assert.True(b.Kept);
        Assert.Equal("A", b.Replaced?.Context); // the older one is dropped: newest wins
        Assert.Equal("B", g.Pending?.Context);
        Assert.Equal("another capture is running", g.Pending?.HeldBack);
        Assert.Equal(2, g.SkippedCount);

        // An older request arriving late does not displace the newer pending one.
        var late = g.Decide(new CaptureRequest(CaptureSource.PoolAlert, T0, TimeSpan.FromHours(100), "incident Z") { Context = "Z" }, running);
        Assert.False(late.Kept);
        Assert.Equal("B", g.Pending?.Context);
    }

    [Fact]
    public void TheRememberedRequestWaitsForTheCapThenStarts()
    {
        var (g, c, _) = New();
        var first = Alert(c, "incident A", "A");
        g.Started(first);
        c.Advance(TimeSpan.FromSeconds(10));
        g.Decide(Alert(c, "incident B", "B"), Quiet with { CaptureRunning = true }); // held back
        c.Advance(TimeSpan.FromSeconds(60));
        g.Finished();

        // Right after the capture ended: nothing starts (the old code started the queued trace within ~2 s).
        Assert.Null(g.TryPending(Quiet));
        c.Advance(TimeSpan.FromMinutes(59));
        Assert.Null(g.TryPending(Quiet));
        Assert.Null(g.TryPending(Quiet with { CaptureRunning = true }));
        c.Advance(TimeSpan.FromMinutes(1));
        var d = g.TryPending(Quiet);
        Assert.NotNull(d);
        Assert.True(d!.Start);
        Assert.Equal("B", d.Request.Context);
        Assert.StartsWith("pool events only (automatic, on a pool alert; held back from ", d.Summary);
        Assert.EndsWith(": another capture is running)", d.Summary);
        Assert.Null(g.Started(d.Request)); // started: no longer pending, nothing superseded
        Assert.Null(g.Pending);
    }

    [Fact]
    public void ARememberedRequestThatIsNoLongerWantedIsDropped()
    {
        var (g, c, _) = New();
        g.Decide(Alert(c, "incident A", "A"), Quiet with { CaptureRunning = true });
        var d = g.TryPending(Quiet, r => !Equals(r.Context, "A")); // its incident closed
        Assert.Equal(CaptureVerdict.Off, d?.Verdict);
        Assert.Equal("no longer needed", d?.Reason);
        Assert.Null(g.Pending);

        // A lag trip's CPU trace is about that lag: dropped after 15 min.
        var (g2, c2, _) = New(s => { s.CpuTraceEnabled = true; s.DeeperCapture = true; });
        g2.Decide(new CaptureRequest(CaptureSource.LagTrip, c2.Wall, c2.Now, "lag"), Quiet with { CpuFraction = 0.99 });
        c2.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(CaptureVerdict.Off, g2.TryPending(Quiet)?.Verdict);
        Assert.Null(g2.Pending);
    }

    [Fact]
    public void AnAutomaticStartSupersedesAnOlderRememberedRequest()
    {
        var (g, c, _) = New();
        var a = Alert(c, "incident A", "A");
        g.Decide(a, Quiet with { CpuFraction = 0.95 }); // held back: saturated
        c.Advance(TimeSpan.FromMinutes(1));
        var b = Alert(c, "incident B", "B");
        Assert.True(g.Decide(b, Quiet).Start);
        Assert.Equal("A", g.Started(b)?.Context); // the caller tells A's incident
        Assert.Null(g.Pending);

        // Capture now leaves a remembered automatic request in place (it runs an hour later if still wanted).
        var (g2, c2, _) = New();
        g2.Decide(Alert(c2, "incident A", "A"), Quiet with { RunQueue = 30 });
        Assert.Null(g2.Started(Manual(c2)));
        Assert.Equal("A", g2.Pending?.Context);
    }

    [Fact]
    public void SkipsWhileThePcIsSaturated()
    {
        var (g, c, _) = New();
        var queue = g.Decide(Alert(c), Quiet with { RunQueue = 33 }); // 33 threads waiting on 24 cores
        Assert.Equal(CaptureVerdict.Skipped, queue.Verdict);
        Assert.Equal("the PC is saturated (33 threads waiting for 24 cores)", queue.Reason);
        Assert.True(queue.Kept);
        var cpu = g.Decide(Alert(c), Quiet with { CpuFraction = 0.93 });
        Assert.Equal("the PC is saturated (CPU 93%, over 85%)", cpu.Reason);

        Assert.Null(CaptureGovernor.Saturation(Quiet with { RunQueue = 24 }));     // at the core count: not above
        Assert.Null(CaptureGovernor.Saturation(Quiet with { CpuFraction = 0.85 })); // at 85%: not above
        Assert.Null(CaptureGovernor.Saturation(new CaptureConditions(false, 24)));  // no readings: not saturated
        Assert.True(g.Decide(Alert(c), Quiet).Start);
    }

    [Fact]
    public void SkipsWhileTheOwnerIsTyping()
    {
        var (g, c, _) = New();
        var d = g.Decide(Alert(c), Quiet with { SinceLastInput = TimeSpan.FromSeconds(3) });
        Assert.Equal(CaptureVerdict.Skipped, d.Verdict);
        Assert.Equal("the PC is in use (keyboard / mouse input 3 s ago)", d.Reason);
        Assert.True(d.Kept);
        Assert.True(g.Decide(Alert(c), Quiet with { SinceLastInput = TimeSpan.FromSeconds(10) }).Start);
        Assert.True(g.Decide(Alert(c), Quiet with { SinceLastInput = null }).Start); // no interactive session: unknown, not typing

        // The pending request waits for the owner to stop typing too.
        Assert.Null(g.TryPending(Quiet with { SinceLastInput = TimeSpan.FromSeconds(1) }));
        Assert.True(g.TryPending(Quiet)?.Start);
    }

    [Fact]
    public void CaptureNowBypassesTheCapAndTheLoadButNotARunningCapture()
    {
        var (g, c, _) = New();
        var auto = Alert(c);
        RunCapture(g, c, auto, TimeSpan.FromSeconds(60));
        c.Advance(TimeSpan.FromSeconds(5));
        var busy = new CaptureConditions(false, 24, RunQueue: 40, CpuFraction: 0.99, SinceLastInput: TimeSpan.Zero);
        Assert.NotNull(g.Blocker(busy)); // an automatic capture would wait (cap, load, input) ...
        var now = g.Decide(Manual(c), busy);
        Assert.True(now.Start); // ... the owner's button does not
        Assert.Equal("pool events + Kernel-File (Capture now)", now.Summary);

        var refused = g.Decide(Manual(c), busy with { CaptureRunning = true });
        Assert.Equal(CaptureVerdict.Skipped, refused.Verdict);
        Assert.Equal("another capture is running", refused.Reason);
        Assert.False(refused.Kept); // the owner presses it again; it is never queued
        Assert.Null(g.Pending);
        Assert.Equal("capture skipped: another capture is running", refused.Note);
    }

    [Fact]
    public void TheLagProbePausesAroundPoolWatchsOwnWork()
    {
        var now = TimeSpan.FromHours(5);
        var started = now - TimeSpan.FromMinutes(2);
        Assert.False(ProbePause.Paused(false, null, null, now));                              // nothing ran
        Assert.True(ProbePause.Paused(true, started, null, now));                             // a trace or a clear is running
        Assert.True(ProbePause.Paused(false, null, now - TimeSpan.FromSeconds(10), now));     // ended 10 s ago
        Assert.True(ProbePause.Paused(false, null, now - TimeSpan.FromSeconds(29.9), now));
        Assert.False(ProbePause.Paused(false, null, now - TimeSpan.FromSeconds(30), now));    // 30 s after: probing again
        Assert.False(ProbePause.Paused(false, null, now - TimeSpan.FromMinutes(5), now));
        // The later of the trace's and the clear's ends counts (either may never have run).
        Assert.Equal(now, ProbePause.Later(now - TimeSpan.FromMinutes(1), now));
        Assert.Equal(now, ProbePause.Later(null, now));
        Assert.Equal(now, ProbePause.Later(now, null));
        Assert.Null(ProbePause.Later(null, null));
        Assert.Equal(started, ProbePause.Earlier(started, now));
        Assert.Equal(started, ProbePause.Earlier(null, started));
    }

    [Fact]
    public void AHungCaptureNeverPausesTheProbeForMoreThanTenMinutes()
    {
        // C1 b (review round 2): a capture thread that hangs used to keep the probe silent until PoolWatch restarted.
        var now = TimeSpan.FromHours(5);
        Assert.Equal(ProbePause.State.Paused, ProbePause.StateOf(true, now - TimeSpan.FromMinutes(9.9), null, now));
        Assert.Equal(ProbePause.State.Overdue, ProbePause.StateOf(true, now - TimeSpan.FromMinutes(10), null, now)); // probe again, say so
        Assert.Equal(ProbePause.State.Overdue, ProbePause.StateOf(true, now - TimeSpan.FromHours(3), null, now));
        Assert.False(ProbePause.Paused(true, now - TimeSpan.FromMinutes(11), null, now));
        Assert.Equal(ProbePause.State.Paused, ProbePause.StateOf(true, null, null, now));     // start unknown: no cap
        Assert.Equal(ProbePause.State.Probing, ProbePause.StateOf(false, null, null, now));
    }

    [Fact]
    public void NoAutomaticCacheClearDuringAnExperiment()
    {
        var s = new Settings { AutoClearEnabled = true, AutoClearPoolGb = 8 };
        s.Normalize();
        var pool = new PoolTotals(5UL << 30, 4UL << 30, 0, 0); // 9 GB, over the 8 GB rule
        Assert.Equal(ClearTrigger.PoolOverLimit, ClearPolicy.Due(s, pool, T0, null, T0));
        Assert.Null(ClearPolicy.Due(s, pool, T0, null, T0, experimentRunning: true));
        Assert.Null(ClearPolicy.Due(s, pool, T0.AddHours(2), null, T0, experimentRunning: true)); // nor the schedule
    }
}
