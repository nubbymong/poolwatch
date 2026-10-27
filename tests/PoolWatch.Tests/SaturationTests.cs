using PoolWatch.Core.Services;

namespace PoolWatch.Tests;

/// <summary>Addendum 4 A: the "PC is saturated" detector and its answer line.</summary>
public class SaturationTests
{
    private static readonly DateTime T0 = new(2026, 10, 26, 11, 19, 0, DateTimeKind.Utc);
    private static LoadPoint P(double sec, double? runQueue, bool probe = false, double cpu = 1.0, int cores = 24) =>
        new(T0.AddSeconds(sec), cpu, runQueue, cores, probe);

    [Fact]
    public void WithoutPdhTheRunQueueRuleIsOffAndCpuAbove85PercentCounts()
    {
        // C2 (review round 2): no PDH means no run queue. The sum of the processes' cores used to stand in for it and could
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
