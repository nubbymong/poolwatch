using PoolWatch.Core;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Lag;
using PoolWatch.Core.Model;

namespace PoolWatch.Tests;

public class LagTests
{
    private static readonly DateTime T0 = new(2026, 10, 26, 8, 0, 0, DateTimeKind.Utc);
    private static LagProbe P(int i, double timer, double file = 1, double? clip = 0.3, long nonPagedMb = 2000) =>
        new(T0.AddSeconds(10 * i), timer, clip, file, 0, new PoolTotals(1_000L << 20, (ulong)nonPagedMb << 20, 0, 0));

    [Fact]
    public void RecentHoldsTheLastHourWhateverTheProbeInterval()
    {
        var m = new LagMonitor(new Settings { LagBaselineSamples = 5 });
        for (int i = 0; i < 3600; i++) // one probe every 2 s for two hours
            m.Add(new LagProbe(T0.AddSeconds(2 * i), 15.6, 0.3, 1, 0, default));
        var recent = m.Recent.ToList();
        Assert.InRange((recent[^1].TimeUtc - recent[0].TimeUtc).TotalMinutes, 59.9, 60);
    }

    [Fact]
    public void AProbeThatRanSecondsLateIsLagByItself()
    {
        // A starved PC: the probe thread got no CPU for 38 s and its first wake-up after that is the only probe.
        var m = new LagMonitor(new Settings { LagBaselineSamples = 5, LagAlertRatio = 5 });
        int i = 0;
        for (; i < 5; i++) Assert.Null(m.Add(P(i, 15.6)));
        Assert.Null(m.Add(P(i++, 500))); // 500 ms late: over the ratio, but one probe still needs a second
        for (int k = 0; k < 2; k++) m.Add(P(i++, 15.6));
        var f = m.Add(P(i++, 38_000, nonPagedMb: 9000));
        Assert.NotNull(f);
        Assert.Contains("timer wake-up", f!.Title);
    }

    [Fact]
    public void LagStartsAtAPoolSize()
    {
        var m = new LagMonitor(new Settings { LagBaselineSamples = 5, LagAlertRatio = 5 });
        int i = 0;
        for (; i < 5; i++) Assert.Null(m.Add(P(i, 15.6)));
        Assert.Equal(15.6, m.Baseline!.Value.timer, 1);
        Assert.Null(m.Add(P(i++, 16, file: 30))); // file 30 ms: under the 50 ms floor, not lag
        Assert.Null(m.Add(P(i++, 120, nonPagedMb: 12000))); // one slow probe is not lag
        var f = m.Add(P(i++, 150, nonPagedMb: 12300));
        Assert.NotNull(f);
        Assert.Equal(FindingKind.Lag, f!.Kind);
        Assert.Contains("Typing / window lag starts at about 12.01 GB of non-paged pool", f.Details[0]);
        Assert.True(m.Latest!.Lagging);
        Assert.Contains("LAG: timer wake-up", m.Summary());
        Assert.Null(m.Add(P(i++, 160))); // still lagging: one finding per episode
        for (int k = 0; k < 3; k++) m.Add(P(i++, 15.6));
        Assert.False(m.Latest!.Lagging);
    }

    [Fact]
    public void LearnsTheBaselineFirst()
    {
        var m = new LagMonitor(new Settings { LagBaselineSamples = 30 });
        m.Add(P(0, 500));
        Assert.Null(m.Baseline);
        Assert.StartsWith("Lag: learning the baseline (1 of 30", m.Summary());
    }
}

/// <summary>C3 (review round 2): a suspend / resume gap is seen on either clock, so the compositor's counter is re-based.</summary>
public class ResumeGapTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    [Fact]
    public void ASleepIsSeenOnEitherClock()
    {
        Assert.True(ResumeGap.Detected(TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(3), Interval));  // both clocks saw it
        Assert.True(ResumeGap.Detected(TimeSpan.Zero, TimeSpan.FromMinutes(45), Interval));           // the monotonic clock stopped while asleep
        Assert.True(ResumeGap.Detected(TimeSpan.FromMinutes(3), null, Interval));                     // the first probe: monotonic only
        Assert.False(ResumeGap.Detected(TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(10), Interval)); // a normal probe
        Assert.False(ResumeGap.Detected(TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(50), Interval));      // a starved PC: lag, not sleep
        Assert.False(ResumeGap.Detected(TimeSpan.Zero, Interval + ResumeGap.SuspendGap, Interval));          // at the limit: not above
    }
}
