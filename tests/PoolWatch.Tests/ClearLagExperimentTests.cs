using PoolWatch.Core.Experiments;
using PoolWatch.Core.Lag;
using PoolWatch.Core.Model;

namespace PoolWatch.Tests;

/// <summary>Addendum 2 C with item 10: the A/B preset "lag before vs after a clear".</summary>
public class ClearLagExperimentTests
{
    private static readonly DateTime T0 = new(2026, 10, 26, 9, 0, 0, DateTimeKind.Utc);
    private const long GB = 1L << 30;

    private static PoolTotals Pool(double gb) => new((ulong)(gb / 2 * GB), (ulong)(gb / 2 * GB), 0, 0);
    private static Sample S(double min, double poolGb) => new(T0.AddMinutes(min), Pool(poolGb), [], []);
    private static LagProbe P(double min, double timer, double file, double poolGb) => new(T0.AddMinutes(min), timer, null, file, null, Pool(poolGb));

    [Fact]
    public void ClearsAtThePhaseBoundaryAndComparesTheMedians()
    {
        Assert.True(ClearLagExperiment.IsPreset("Clear Cache"));
        Assert.True(ClearLagExperiment.IsPreset("clearcache"));
        Assert.False(ClearLagExperiment.IsPreset("ChatGPT"));
        var e = new ClearLagExperiment(TimeSpan.FromMinutes(10), T0);
        Assert.StartsWith("Experiment started", e.Observe(S(0, 12.2))!.Value.step.Prompt);
        for (int i = 1; i < 10; i++) { e.AddProbe(P(i, 18 + i % 3, 40, 12.2)); Assert.Null(e.Observe(S(i, 12.2))); }
        var clear = e.Observe(S(10, 12.2))!.Value;
        Assert.True(clear.clearNow);
        Assert.StartsWith("Experiment: clearing the standby cache now", clear.step.Prompt);
        for (int i = 11; i < 20; i++) { e.AddProbe(P(i, 2, 4, 7.5)); Assert.Null(e.Observe(S(i, 7.5))); }
        var done = e.Observe(S(20, 7.5))!.Value;
        Assert.False(done.clearNow);
        Assert.Equal(ExperimentPhase.Done, e.Phase);
        Assert.Equal("Lag was lower after the clear: timer wake-up 19 ms -> 2 ms, file create/delete 40 ms -> 4 ms; pool 12.2 GB -> 7.5 GB (the clear freed about 4.7 GB) (medians over 10 min each).", e.Statement);
        Assert.Equal("Experiment result: " + e.Statement, done.step.Finding.Title);
        Assert.Null(e.Observe(S(21, 7.5)));
    }

    [Fact]
    public void SaysWhenNothingChangedOrTheClearDidNotRun()
    {
        var e = new ClearLagExperiment(TimeSpan.FromMinutes(1), T0);
        e.Observe(S(0, 3));
        for (int i = 0; i < 5; i++) e.AddProbe(P(0.1 + i * 0.1, 15, 3, 3));
        e.Observe(S(1, 3));
        for (int i = 0; i < 5; i++) e.AddProbe(P(1.1 + i * 0.1, 16, 3, 3));
        e.Observe(S(2, 3));
        Assert.StartsWith("Lag did not change with the clear: timer wake-up 15 ms -> 16 ms", e.Statement);

        var f = new ClearLagExperiment(TimeSpan.FromMinutes(1), T0);
        f.Observe(S(0, 3));
        f.Observe(S(1, 3));
        f.ClearError = "Clear cache is not set up";
        f.Observe(S(2, 3));
        Assert.StartsWith("Lag before vs after a clear: the clear did not run: no lag probes", f.Statement);
        Assert.Contains("The clear did not run: Clear cache is not set up", f.Notes);
        Assert.Contains(f.Notes, n => n.StartsWith("Few lag probes (0 before, 0 after)"));
    }
}
