using PoolWatch.Core;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Spawn;

namespace PoolWatch.Tests;

/// <summary>
/// A spawn storm's finding stays in memory (with up to 1,000 other findings) and is a line of findings.jsonl: it keeps the
/// top 15 of each map. The storm's own report.json is built from the storm and keeps every key.
/// </summary>
public class StormFindingTests
{
    private static readonly DateTime T0 = new(2026, 10, 26, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TheFindingKeepsTheTopFifteenAndTheReportEverything()
    {
        var settings = new Settings { SpawnStormThreshold = 30, SpawnStormWindowSeconds = 30, ShortLivedSeconds = 10 };
        var registry = new ProcessRegistry();
        var tracker = new SpawnTracker(settings, registry, new GitRootResolver(new FakeProbe()), new([@"C:\Users\me"], [], @"C:\Users"), _ => null);
        registry.OnStart(500, 4, T0.AddHours(-1).ToFileTimeUtc(), "agent.exe", null, "agent.exe", null, T0.AddHours(-1));
        registry.DrainEvents();
        var findings = new List<Finding>();
        Dictionary<string, object?>? report = null;
        var now = T0;
        int pid = 1000;
        for (int s = 0; s < 20; s++, now = now.AddSeconds(1))
        {
            // Four short commands a second, each a different tool, plus one more run of tool0 (the busiest).
            foreach (string image in Enumerable.Range(s * 4, 4).Select(i => $"tool{i}.exe").Append("tool0.exe"))
            {
                var create = now.ToFileTimeUtc() + pid;
                registry.OnStart(pid, 500, create, image, null, image + " --run", null, now);
                registry.OnStop(pid++, create, 0, now.AddMilliseconds(50));
            }
            var (f, toWrite) = tracker.Observe(registry.DrainEvents(), now);
            findings.AddRange(f);
            if (report is null && toWrite.Count > 0) report = tracker.StormData(toWrite[0]); // what its report.json gets now
        }
        var finding = Assert.Single(findings, f => f.Kind == FindingKind.SpawnStorm);
        var kept = Assert.IsType<Dictionary<string, object>>(finding.Data["byImage"]);
        var all = Assert.IsType<Dictionary<string, object>>(report!["byImage"]);
        Assert.True(all.Count > SpawnTracker.FindingKeys, $"{all.Count} images");
        Assert.Equal(all.Take(SpawnTracker.FindingKeys), kept); // the busiest, in the same order
        Assert.Equal("tool0.exe", kept.Keys.First());
        Assert.Equal(SpawnTracker.FindingKeys, Assert.IsType<Dictionary<string, object>>(finding.Data["peakByImage"]).Count);
        Assert.Equal(report["totalStarts"], finding.Data["totalStarts"]);
    }
}
