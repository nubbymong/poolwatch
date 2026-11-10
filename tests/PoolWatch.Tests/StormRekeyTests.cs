using PoolWatch.Core;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Model;
using PoolWatch.Core.Spawn;

using static PoolWatch.Tests.Clocks;

namespace PoolWatch.Tests;

/// <summary>
/// SpawnTracker.FollowRekeys when the exact key a snapshot re-keyed an open storm's estimated key to already has an open
/// storm of its own (the same parent counted under both keys before the re-key): the storm under the estimated key ends
/// there, as a storm ends (its finding says so), and the one under the exact key goes on. Before the fix it was put in the
/// other's place, which then never closed and never wrote its folder's last update.
/// </summary>
public class StormRekeyTests
{
    private static readonly DateTime T0 = new(2026, 11, 9, 23, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AStormReKeyedOntoAnOpenStormsKeyEndsAndTheOtherGoesOn()
    {
        var now = T0;
        // A registry that keeps no exited record (and 300 other processes, over its 280, so it prunes at every snapshot): the order of calls below is one it
        // takes from its callers, and leaves an open storm under the exact key when the estimated one is re-keyed to it.
        var registry = new ProcessRegistry(keepExited: TimeSpan.Zero, maxRecords: 280, clock: () => At(now)) { KeepShortLived = TimeSpan.Zero };
        var settings = new Settings { SpawnStormThreshold = 30, SpawnStormWindowSeconds = 30, ShortLivedSeconds = 10 };
        var tracker = new SpawnTracker(settings, registry, new GitRootResolver(new FakeProbe()), new([@"C:\Users\me"], [], @"C:\Users"), _ => null);
        var findings = new List<Finding>();
        var others = Enumerable.Range(0, 300).Select(i => new ProcessSample { Pid = 10_000 + i, ParentPid = 4, ImageName = "svc.exe", CreateTime = T0.AddHours(-1).ToFileTimeUtc() + i }).ToList();
        long exact = T0.AddSeconds(-5).ToFileTimeUtc();
        var parent = new ProcessSample { Pid = 800, ParentPid = 4, ImageName = "python.exe", CreateTime = exact };
        int pid = 2000;
        long seq = 0;
        void Children(int seconds)
        {
            for (int s = 0; s < seconds; s++, now = now.AddSeconds(1))
            {
                for (int k = 0; k < 2; k++)
                {
                    var r = registry.OnStart(pid++, 800, now.ToFileTimeUtc() + ++seq, "git.exe", null, "git rev-parse HEAD", null, now);
                    registry.OnStop(r.Pid, r.Key.CreateTime, 0, now.AddMilliseconds(40));
                }
                findings.AddRange(tracker.Observe(registry.DrainEvents(), At(now)).findings);
            }
        }

        // The parent seen by a snapshot first, with its exact create time: its children's storm is under the exact key.
        registry.ObserveSnapshot([.. others, parent], now);
        registry.DrainEvents();
        Children(20);
        var exactStorm = Assert.Single(tracker.OpenStorms);
        Assert.Equal(exact, exactStorm.ParentKey.CreateTime);
        // Its record goes (stopped, and kept no longer); then a start of the same PID comes with an estimated create time
        // half a second off, and its children's storm is under that key: two open storms of one parent.
        registry.OnStop(800, exact, null, now);
        now = now.AddSeconds(1);
        registry.ObserveSnapshot(others, now);
        Assert.Null(registry.Get(new ProcessKey(800, exact)));
        registry.OnStart(800, 4, exact + 5_000_000, "python.exe", null, "python run.py", null, now, createTimeEstimated: true);
        registry.DrainEvents();
        Children(20);
        Assert.Equal(2, tracker.OpenStorms.Count);
        var estimatedStorm = tracker.OpenStorms.Single(s => s != exactStorm);
        // A snapshot shows the PID with its exact create time: the estimated key is re-keyed to the exact one.
        registry.ObserveSnapshot([.. others, parent], now);
        Assert.Equal(1, registry.Rekeys);
        int before = findings.Count;
        findings.AddRange(tracker.Observe(registry.DrainEvents(), At(now)).findings);

        Assert.Same(exactStorm, Assert.Single(tracker.OpenStorms)); // the one under the exact key goes on
        Assert.Null(exactStorm.ClosedUtc);
        Assert.Equal(At(now).Ms, estimatedStorm.ClosedMs); // the other ended there, as a storm ends
        var ended = Assert.Single(findings.Skip(before), f => f.Kind == FindingKind.SpawnStorm && f.Severity == FindingSeverity.Info);
        Assert.Equal(800, ended.Pid);
        Assert.Same(estimatedStorm.LastFinding, ended);
    }
}
