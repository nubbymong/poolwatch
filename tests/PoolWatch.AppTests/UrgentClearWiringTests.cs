using System.Reflection;
using System.Text.RegularExpressions;

namespace PoolWatch.AppTests;

/// <summary>
/// The urgent clear needs the running app to reach it, so its wiring is held by the lines that make it: the diagnostics
/// give the clearer the detector's non-paged rise, the clearer's Observe passes that rise to the automatic rule, and its
/// Next passes the urgent rule's answer to the next-clear line. Every urgent-clear test of the rule calls it directly, so
/// with the rise wired as "() => null" the urgent clear would never run and each of them would still pass. The clearer's
/// constructor also takes the rise with no default, so a construction that leaves it out does not build.
/// </summary>
public sealed class UrgentClearWiringTests
{
    private static string Source(string folder, string file) => File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", folder, file));

    [Fact]
    public void TheDetectorsRiseReachesTheAutomaticRuleAndTheNextClearLine()
    {
        string diagnostics = Source("Analysis", "Diagnostics.cs"), clearer = Source("Analysis", "CacheClearer.cs");
        Assert.Contains("Cache = new CacheClearer(engine, Findings, () => AnyExperimentActive, () => analysis.Detector.NonPagedRise10Min);", diagnostics);
        Assert.Equal(2, Regex.Matches(clearer, @"long\? rise = _nonPagedRise\(\);").Count); // read in Observe and in Next
        Assert.Contains("start = ClearPolicy.Automatic(_history, _engine.Settings, s.Totals, at, _poolWatchStartedMs, experimentRunning, rise);", clearer);
        Assert.Contains("ClearPolicy.UrgentNow(_history, _engine.Settings, pool, rise, now));", clearer);
        // The rise is required.
        var rise = typeof(PoolWatch.Analysis.CacheClearer).GetConstructors(BindingFlags.Instance | BindingFlags.Public).Single()
            .GetParameters().Single(p => p.Name == "nonPagedRise");
        Assert.False(rise.HasDefaultValue, "the clearer's non-paged rise has a default");
        Assert.Equal(typeof(Func<long?>), rise.ParameterType);
    }
}
