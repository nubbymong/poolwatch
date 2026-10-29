using PoolWatch.Analysis;
using PoolWatch.Core;
using PoolWatch.Core.Cache;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Model;

namespace PoolWatch.AppTests;

/// <summary>
/// The pipeline's detector step (<see cref="AnalysisPipeline.Detect"/>) reads PoolWatch's own cache clears as the clearer
/// holds them, each measured 2 minutes after it ran as the clearer measures it. Pure: a history built in memory, no engine.
/// </summary>
public class AnalysisDetectTests
{
    private static readonly DateTime T0 = new(2026, 10, 20, 6, 0, 0, DateTimeKind.Utc);
    private const long MB = Settings.MB;

    [Fact]
    public void TheDetectorHoldsWhileTheClearersClearIsMeasuredAndItsDropIsNoRelease()
    {
        // Reff holds 600 MB; a clear after sample 20 drops it to 100 MB. With the clearer's clears the detector holds for the
        // clear's 2 minutes, then reads the drop as the clear's (no release); without them the same samples say released.
        var h = Core.History.History.ForInterval(10);
        var clears = new List<ClearRecord>();
        var seen = new Detector(new Settings());
        var blind = new Detector(new Settings());
        var holding = new List<bool>();
        var alerts = new List<Alert>();
        var blindAlerts = new List<Alert>();
        for (int i = 0; i < 60; i++)
        {
            var t = T0.AddSeconds(10 * i);
            long reff = i <= 20 ? 600 * MB : 100 * MB;
            h.Add(new Sample(t, new PoolTotals(1UL << 30, 1UL << 30, 0, 0), [new PoolTagEntry(PoolTag.Parse("Reff"), 0, 0, 0, 1, 0, (ulong)reff)], []));
            alerts.AddRange(AnalysisPipeline.Detect(seen, h, clears));
            holding.Add(seen.Holding);
            blindAlerts.AddRange(AnalysisPipeline.Detect(blind, h, null));
            if (i == 20) clears.Add(new ClearRecord { TimeUtc = t.AddSeconds(1), Mode = ClearMode.Standby, Trigger = ClearTrigger.Manual, Before = default });
        }
        Assert.Equal(Enumerable.Range(0, 60).Select(i => i is > 20 and < 33), holding); // until 2 minutes after the clear
        Assert.DoesNotContain(alerts, a => a.Kind == AlertKind.Release);
        Assert.Contains(blindAlerts, a => a.Kind == AlertKind.Release);
        Assert.Equal(TagState.Normal, seen.Status[PoolTag.Parse("Reff")].State);
    }
}
