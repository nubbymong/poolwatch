using PoolWatch.Core.Services;

namespace PoolWatch.Tests;

/// <summary>The "PC is saturated" detector and its answer line.</summary>
public class SaturationTests
{
    private static readonly DateTime T0 = new(2026, 10, 26, 11, 19, 0, DateTimeKind.Utc);
    private const long Tick0 = 7_000_000; // milliseconds since Windows started, at that time

    /// <summary>A moment <paramref name="sec"/> seconds in, on both clocks (the system clock moved by <paramref name="wallJump"/> since).</summary>
    private static LoadPoint P(double sec, double? runQueue, bool probe = false, double cpu = 1.0, int cores = 24, TimeSpan wallJump = default) =>
        new(T0.AddSeconds(sec) + wallJump, Tick0 + (long)(sec * 1000), cpu, runQueue, cores, probe);

    /// <summary>
    /// No load point is made without the time "PC is saturated" times load on: LoadPoint is a record class whose one public
    /// constructor takes the system time and then TickMs, neither with a default, and it has no constructor without
    /// parameters, so an object initializer with the system time alone does not compile (a struct would, with TickMs 0),
    /// nor does Activator.CreateInstance make one with no arguments (what this pins: RuntimeHelpers.GetUninitializedObject,
    /// which runs no constructor at all, can still make one).
    /// </summary>
    [Fact]
    public void ALoadPointIsMadeOnlyWithItsTickTime()
    {
        Assert.False(typeof(LoadPoint).IsValueType);
        var made = Assert.Single(typeof(LoadPoint).GetConstructors());
        var first = made.GetParameters().Take(2).ToList();
        Assert.Equal([("TimeUtc", typeof(DateTime), false), ("TickMs", typeof(long), false)],
            first.Select(p => (p.Name, p.ParameterType, p.HasDefaultValue)));
        Assert.Throws<MissingMethodException>(() => Activator.CreateInstance(typeof(LoadPoint)));
    }

    /// <summary>
    /// How long load has lasted is timed on the clock that only goes forward: setting the system clock back an hour in the
    /// middle of an episode neither holds the answer back, nor does setting it forward a day give it early.
    /// </summary>
    [Fact]
    public void AnEpisodeIsTimedOnTheClockThatOnlyGoesForward()
    {
        var back = new SaturationDetector();
        Assert.Null(back.Observe(P(0, 33), Esrv));
        Assert.Null(back.Observe(P(30, 33, wallJump: TimeSpan.FromHours(-1)), Esrv));
        Assert.NotNull(back.Observe(P(61, 33, wallJump: TimeSpan.FromHours(-1)), Esrv)); // over a minute, by the clock that only goes forward

        var forward = new SaturationDetector();
        Assert.Null(forward.Observe(P(0, 33), Esrv));
        Assert.Null(forward.Observe(P(1, 33, wallJump: TimeSpan.FromDays(1)), Esrv)); // one second in, whatever the system clock says
        Assert.Null(forward.Observe(P(59, 33, wallJump: TimeSpan.FromDays(1)), Esrv));
        Assert.NotNull(forward.Observe(P(61, 33, wallJump: TimeSpan.FromDays(1)), Esrv));
    }

    [Fact]
    public void WithoutPdhTheRunQueueRuleIsOffAndCpuAbove85PercentCounts()
    {
        // No PDH means no run queue. The sum of the processes' cores used to stand in for it and could
        // never exceed the core count, so the rule never tripped; now CPU above 85% takes its place and the answer says so.
        Assert.False(SaturationDetector.UnderLoad(P(0, null, cpu: 0.85)));  // at 85%: not above
        Assert.True(SaturationDetector.UnderLoad(P(0, null, cpu: 0.86)));
        Assert.True(SaturationDetector.UnderLoad(P(0, null, probe: true, cpu: 0.1)));
        Assert.False(SaturationDetector.UnderLoad(P(0, 2, cpu: 0.99)));    // measured run queue below the cores: the CPU rule is not used
        var d = new SaturationDetector();
        Assert.Null(d.Observe(P(0, null, cpu: 0.95), Esrv));
        var alert = d.Observe(P(61, null, cpu: 0.95), Esrv);
        Assert.NotNull(alert);
        Assert.Contains("CPU 95% of 24 cores", alert);
        Assert.Contains("run queue not measured", alert);
        Assert.Contains("CPU above 85%", alert);
        Assert.DoesNotContain("waiting", alert);
        Assert.DoesNotContain("not measured", SaturationDetector.Answer(P(0, 33), Esrv)); // measured: no note
    }

    private static readonly List<ProcessCost> Esrv =
        [new(2000, "esrv_svc.exe", "esrv_svc.exe (Intel System Usage Report service)", 1.7, 887_000, "ESRV_SVC_QUEENCREEK")];

    [Fact]
    public void AlertsOnlyAfterTheRunQueueStaysHighForOverAMinute()
    {
        var d = new SaturationDetector();
        Assert.Null(d.Observe(P(0, 33), Esrv));   // 33 threads waiting on 24 cores
        Assert.Null(d.Observe(P(30, 33), Esrv));
        Assert.Null(d.Observe(P(59, 33), Esrv));  // under 60 s
        var alert = d.Observe(P(61, 33), Esrv);
        Assert.NotNull(alert);
        Assert.Contains("PC is saturated", alert!);
        Assert.Contains("887k page faults/s", alert);
        Assert.Contains("1.7 cores", alert);
        Assert.Contains("Intel System Usage Report service", alert);
        Assert.Null(d.Observe(P(70, 33), Esrv)); // one alert per episode
        Assert.True(d.Saturated);
    }

    [Fact]
    public void TheProbeTrippingAlsoCounts()
    {
        var d = new SaturationDetector();
        Assert.Null(d.Observe(P(0, 2, probe: true, cores: 24), Esrv)); // run queue below cores, but the probe trips
        Assert.NotNull(d.Observe(P(61, 2, probe: true, cores: 24), Esrv));
    }

    [Fact]
    public void RecoveryResetsTheEpisode()
    {
        var d = new SaturationDetector();
        d.Observe(P(0, 33), Esrv);
        Assert.Null(d.Observe(P(40, 1), Esrv)); // load dropped
        Assert.False(d.Saturated);
        Assert.Null(d.Observe(P(80, 33), Esrv)); // a new episode starts the clock again
        Assert.NotNull(d.Observe(P(141, 33), Esrv));
    }

    [Fact]
    public void UnderLoadDefinition()
    {
        Assert.True(SaturationDetector.UnderLoad(P(0, 25, cores: 24)));
        Assert.False(SaturationDetector.UnderLoad(P(0, 24, cores: 24)));
        Assert.True(SaturationDetector.UnderLoad(P(0, 1, probe: true)));
    }
}
