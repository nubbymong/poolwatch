using PoolWatch.Core;
using PoolWatch.Core.Boot;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Identity;
using PoolWatch.Core.Model;
using PoolWatch.Core.Spawn;

namespace PoolWatch.Tests;

/// <summary>Review Part 3: item 12 (slow pile-ups still trip) and item 13 (before-launch / after-exit snapshots outside experiments).</summary>
public class PileUpAndSnapshotTests
{
    private static readonly DateTime T0 = new(2026, 10, 26, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Owner Codex = new(OwnerKind.App, "app:codex", "Codex");

    private static List<ProcessRecord> Cua(int n) =>
        Enumerable.Range(0, n).Select(i => new ProcessRecord { Key = new ProcessKey(1000 + i, 1), Image = "node.exe" }).ToList();

    private static ProcessIdentity? Id(ProcessRecord r) => new("node.exe", "Codex cua_node (server.mjs)", Codex, false);

    private static Settings S() => new() { AccumulationIntervalSeconds = 300, AccumulationWindowPoints = 6, AccumulationMinCount = 5, AccumulationMinRise = 3 };

    [Fact]
    public void ASlowPileUpTripsAgainstTheHourlyBaseline()
    {
        // One more Codex helper every 90 minutes: the 30-minute window never sees it rise at 3 of 4 checks.
        var acc = new ChildAccumulation(S());
        var found = new List<Finding>();
        for (int m = 0; m <= 8 * 60; m += 5)
        {
            int n = 2 + m / 90;
            found.AddRange(acc.Observe(Cua(n), Id, T0.AddMinutes(m)));
        }
        var f = found[0];
        Assert.Equal(T0.AddHours(4.5), f.TimeUtc); // 2 -> 5 over four and a half hours
        Assert.StartsWith("Child processes piling up slowly: 5 × node.exe · Codex cua_node (server.mjs) under Codex (2 -> 5 in 4.5 h", f.Title);
        Assert.Contains("never falling back", f.Title);
        Assert.Equal(true, f.Data["slow"]);
        Assert.StartsWith("Hourly counts: 2, ", f.Details[0]);
        // Afterwards only when it grew again (hourly at most), never for an unchanged count.
        Assert.Equal([5, 6, 7], found.Select(x => (int)x.Data["to"]!));
    }

    [Fact]
    public void HelpersThatDoExitDoNotTripTheSlowRule()
    {
        // Up and down by more than one across the hours: churn, not a pile-up.
        var acc = new ChildAccumulation(S());
        var found = new List<Finding>();
        int[] hourly = [2, 4, 6, 3, 5, 7, 4, 6, 8, 5];
        for (int m = 0; m < hourly.Length * 60; m += 5) found.AddRange(acc.Observe(Cua(hourly[m / 60]), Id, T0.AddMinutes(m)));
        Assert.Empty(found);
    }

    [Fact]
    public void TooFewHoursIsNotASlowPileUp()
    {
        var acc = new ChildAccumulation(S());
        var g = new AccumulationGroup { OwnerName = "Codex", What = "node.exe" };
        foreach (var (h, n) in new[] { (0, 2), (1, 4), (2, 6) }) { g.Hourly.Add(new(T0.AddHours(h), n)); g.Points.Add(new(T0.AddHours(h), n)); }
        Assert.False(acc.GrowingSlowly(g, out _, out _, out _)); // two hours of points: not yet three
        g.Hourly.Add(new(T0.AddHours(3), 7)); g.Points.Add(new(T0.AddHours(3), 7));
        Assert.True(acc.GrowingSlowly(g, out int from, out int to, out var span));
        Assert.Equal((2, 7), (from, to));
        Assert.Equal(TimeSpan.FromHours(3), span);
        g.Points.Add(new(T0.AddHours(3.5), 4)); // the count fell back since the last hourly point
        Assert.False(acc.GrowingSlowly(g, out _, out _, out _));
    }

    private static ProcessSample P(int pid, string image) => new() { Pid = pid, CreateTime = 100 + pid, ImageName = image };

    private static List<PoolTagEntry> Tags(long reffMb) =>
    [
        new(PoolTag.Parse("Reff"), 0, 0, 0, 0, 0, (ulong)(reffMb << 20)),
        new(PoolTag.Parse("Tiny"), 0, 0, 0, 0, 0, 1024), // under 16 KB: left out
    ];

    [Fact]
    public void KeepsTheLastSnapshotBeforeAnAppStartsAndTheFirstAfterItExits()
    {
        var snaps = new AppSnapshots(["ChatGPT", "node", "Claude", "claude"]);
        Assert.Empty(snaps.Observe(T0, [P(4, "System")], Tags(100)));                                       // first sample: nothing yet
        Assert.Empty(snaps.Observe(T0.AddSeconds(10), [P(4, "System")], Tags(110)));
        var started = snaps.Observe(T0.AddSeconds(20), [P(4, "System"), P(50, "ChatGPT.exe"), P(51, "node.exe")], Tags(150));
        var before = Assert.Single(started);                                                                // node is a shared runtime: no snapshot
        Assert.Equal(("ChatGPT", true, T0.AddSeconds(10)), (before.App, before.BeforeLaunch, before.TimeUtc)); // the sample BEFORE it started
        Assert.Equal("before-launch", before.Moment);
        Assert.Equal(110L << 20, Assert.Single(before.Tags).NonPaged);                                     // Reff only (Tiny is under 16 KB)
        Assert.Empty(snaps.Observe(T0.AddSeconds(30), [P(4, "System"), P(50, "ChatGPT.exe"), P(52, "ChatGPT.exe")], Tags(400))); // still running
        var exited = Assert.Single(snaps.Observe(T0.AddSeconds(40), [P(4, "System")], Tags(390)));
        Assert.Equal(("ChatGPT", false, T0.AddSeconds(40)), (exited.App, exited.BeforeLaunch, exited.TimeUtc)); // the first sample after the exit
        Assert.Equal("after-exit", exited.Moment);
        Assert.Equal(2, snaps.Snapshots.Count);

        // A second launch replaces the before-launch snapshot (the newest per app and moment).
        snaps.Observe(T0.AddSeconds(50), [P(4, "System"), P(60, "chatgpt.exe")], Tags(395));
        Assert.Equal(T0.AddSeconds(40), snaps.Snapshots.Single(s => s.BeforeLaunch).TimeUtc);
        Assert.Equal(2, snaps.Snapshots.Count);
    }
}
