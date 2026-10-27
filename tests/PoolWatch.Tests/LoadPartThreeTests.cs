using PoolWatch.Core;
using PoolWatch.Core.Lag;
using PoolWatch.Core.Model;
using PoolWatch.Core.Services;

namespace PoolWatch.Tests;

/// <summary>Review Part 3, addendum 4 A: the page-fault split and the lag probe as a saturation condition.</summary>
public class LoadPartThreeTests
{
    private static readonly DateTime T0 = new(2026, 10, 26, 11, 19, 0, DateTimeKind.Utc);

    [Fact]
    public void FaultSplitInPlainWords()
    {
        var storm = new FaultSplit(880_000, 5_000, 2_000);
        Assert.Equal("880k/s demand-zero (fresh memory), 5k/s transition, 2k/s hard (pages read from disk)", storm.Text());
        Assert.StartsWith("mostly demand-zero", storm.Dominant());
        Assert.StartsWith("mostly hard faults", new FaultSplit(100, 200, 9_000).Dominant());
        Assert.StartsWith("mostly transition", new FaultSplit(100, 9_000, 200).Dominant());
        Assert.Null(new FaultSplit(4_000, 4_000, 4_000).Dominant()); // no part dominates
        Assert.Null(new FaultSplit(10, 10, 10).Dominant());          // too few to say anything
    }

    [Fact]
    public void SaturationAnswerCarriesTheFaultSplit()
    {
        var p = new LoadPoint(T0, 1.0, 33, 24, false, new FaultSplit(880_000, 5_000, 2_000));
        var answer = SaturationDetector.Answer(p, [new(2000, "esrv_svc.exe", "esrv_svc.exe (Intel System Usage Report service)", 1.7, 887_000, "ESRV_SVC_QUEENCREEK")]);
        Assert.Contains("33 threads waiting; page faults 880k/s demand-zero (fresh memory), 5k/s transition, 2k/s hard", answer);
        Assert.Contains("mostly demand-zero", answer);
        Assert.Contains("Top: esrv_svc.exe (Intel System Usage Report service): 887k page faults/s, 1.7 cores.", answer);
        // A quiet fault picture is left out.
        Assert.DoesNotContain("page faults", SaturationDetector.Answer(p with { Faults = new FaultSplit(100, 10, 5) }, []));
    }

    private static LagProbe Probe(int i, double timer) =>
        new(T0.AddSeconds(10 * i), timer, 0.3, 1, 0, new PoolTotals(1_000L << 20, 2_000L << 20, 0, 0));

    [Fact]
    public void TheLagProbeTripsOnlyWhileItIsLaggingAndFresh()
    {
        var m = new LagMonitor(new Settings { LagBaselineSamples = 3, LagAlertRatio = 5 });
        int i = 0;
        for (; i < 3; i++) m.Add(Probe(i, 15.6));
        Assert.False(m.Tripping(T0.AddSeconds(10 * i), TimeSpan.FromSeconds(30)));
        m.Add(Probe(i++, 200));
        m.Add(Probe(i++, 220)); // two in a row: lagging
        var at = T0.AddSeconds(10 * (i - 1));
        Assert.True(m.Tripping(at.AddSeconds(5), TimeSpan.FromSeconds(30)));
        Assert.False(m.Tripping(at.AddSeconds(31), TimeSpan.FromSeconds(30))); // no fresh probe (paused or stopped): not counted
    }

    [Fact]
    public void ATrippingProbeForAMinuteRaisesSaturationWithoutARunQueue()
    {
        // The wiring asked for: the probe alone (run queue below the core count) for over 60 s.
        var m = new LagMonitor(new Settings { LagBaselineSamples = 3, LagAlertRatio = 5 });
        var d = new SaturationDetector();
        string? alert = null;
        int i = 0;
        for (; i < 3; i++) m.Add(Probe(i, 15.6));
        for (; i < 12; i++)
        {
            m.Add(Probe(i, 250));
            var now = T0.AddSeconds(10 * i);
            alert ??= d.Observe(new LoadPoint(now, 0.2, 2, 24, m.Tripping(now, TimeSpan.FromSeconds(30))), []);
        }
        Assert.NotNull(alert);
        Assert.Contains("typing lags", alert);
    }
}
