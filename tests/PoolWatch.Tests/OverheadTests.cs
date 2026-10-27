using PoolWatch.Core;
using PoolWatch.Core.Model;

namespace PoolWatch.Tests;

public class OverheadTests
{
    private static readonly DateTime T0 = new(2026, 10, 26, 5, 29, 56, DateTimeKind.Utc);

    [Fact]
    public void ShortLivedProcessesAreRolledUpLongLivedListed()
    {
        var reg = new ProcessRegistry();
        reg.OnStart(500, 4, T0.AddHours(-1).ToFileTimeUtc(), "bash.exe", null, "bash", null, T0.AddHours(-1));
        reg.DrainEvents();
        var sum = new EventSummarizer(TimeSpan.FromSeconds(60));
        var output = new List<LifecycleEvent>();
        var now = T0;
        // 38 short commands in two seconds, plus one long-running node.
        for (int i = 0; i < 38; i++)
        {
            long ct = now.AddMilliseconds(i * 50).ToFileTimeUtc();
            reg.OnStart(1000 + i, 500, ct, i % 2 == 0 ? "grep.exe" : "sed.exe", null, i % 2 == 0 ? "grep -i x" : "sed -n 1p", null, now);
            reg.OnStop(1000 + i, ct, 0, now.AddMilliseconds(i * 50 + 40));
        }
        reg.OnStart(2000, 500, now.ToFileTimeUtc(), "node.exe", null, "node server.js", null, now);
        output.AddRange(sum.Process(reg.DrainEvents(), now.AddSeconds(2), reg.RecentImage));
        Assert.Empty(output); // nothing notable yet: the node start is held until it has run 30 s
        output.AddRange(sum.Process([], now.AddSeconds(35), reg.RecentImage));
        var nodeStart = Assert.Single(output);
        Assert.Equal(LifecycleKind.Start, nodeStart.Kind);
        Assert.Equal("node.exe", nodeStart.Image);
        output.Clear();
        output.AddRange(sum.Process([], now.AddSeconds(63), reg.RecentImage)); // the window (opened at the first call) closes
        var rollups = output.Where(e => e.Kind == LifecycleKind.Rollup).ToList();
        Assert.Equal(2, rollups.Count);
        var grep = rollups.Single(r => r.Image == "grep.exe");
        Assert.Contains("bash.exe (500) started 19, 19 exited", grep.Detail);
        Assert.Contains("19× grep -i x", grep.Detail);
        Assert.Equal(76, sum.RolledUp);
        Assert.Equal(0, sum.Pending);
    }

    [Fact]
    public void OneRollupForManyParentsOfTheSameImage()
    {
        // Claude Code runs every command in its own bash.exe: 50 bash parents, one grep each, are one row, not 50.
        var sum = new EventSummarizer(TimeSpan.FromSeconds(60));
        var events = new List<LifecycleEvent>();
        for (int i = 0; i < 50; i++)
        {
            long ct = T0.AddMilliseconds(i * 100).ToFileTimeUtc();
            events.Add(new LifecycleEvent { TimeUtc = T0.AddMilliseconds(i * 100), Kind = LifecycleKind.Start, Pid = 3000 + i, ParentPid = 2000 + i, CreateTime = ct, Image = "grep.exe", CommandLine = "grep -i x" });
            events.Add(new LifecycleEvent { TimeUtc = T0.AddMilliseconds(i * 100 + 50), Kind = LifecycleKind.Exit, Pid = 3000 + i, ParentPid = 2000 + i, CreateTime = ct, Image = "grep.exe" });
        }
        sum.Process(events, T0.AddSeconds(5), _ => "bash.exe");
        var rollup = Assert.Single(sum.Flush(T0.AddSeconds(61)));
        Assert.StartsWith("bash.exe ×50 started 50, 50 exited", rollup.Detail);
        Assert.Equal(2000, rollup.ParentPid);
    }

    [Fact]
    public void RollupsSplitByOwner()
    {
        var sum = new EventSummarizer(TimeSpan.FromSeconds(60));
        var events = new List<LifecycleEvent>();
        for (int i = 0; i < 10; i++)
        {
            long ct = T0.AddMilliseconds(i * 100).ToFileTimeUtc();
            events.Add(new LifecycleEvent { TimeUtc = T0.AddMilliseconds(i * 100), Kind = LifecycleKind.Start, Pid = 3000 + i, ParentPid = 2000 + i, CreateTime = ct, Image = "git.exe", CommandLine = "git status" });
            events.Add(new LifecycleEvent { TimeUtc = T0.AddMilliseconds(i * 100 + 50), Kind = LifecycleKind.Exit, Pid = 3000 + i, ParentPid = 2000 + i, CreateTime = ct, Image = "git.exe" });
        }
        int lookups = 0;
        // 2e: owners are told apart by their identity key; two sessions may even share a display name.
        var a = new PoolWatch.Core.Identity.Owner(PoolWatch.Core.Identity.OwnerKind.ClaudeSession, "claude:a", "Claude Code session x");
        var b = new PoolWatch.Core.Identity.Owner(PoolWatch.Core.Identity.OwnerKind.ClaudeSession, "claude:b", "Claude Code session x");
        sum.Process(events, T0.AddSeconds(5), _ => "bash.exe", ppid => { lookups++; return ppid < 2005 ? a : b; });
        var rollups = sum.Flush(T0.AddSeconds(61));
        Assert.Equal(2, rollups.Count);
        Assert.Equal(["claude:a", "claude:b"], rollups.Select(r => r.OwnerKey).Order());
        Assert.All(rollups, r => Assert.Equal("Claude Code session x", r.Owner));
        Assert.All(rollups, r => Assert.Equal(5, r.Count));
        Assert.Equal(10, lookups); // once per parent PID, not per event
    }

    [Fact]
    public void NotableEventsPassThrough()
    {
        var sum = new EventSummarizer();
        var orphan = new LifecycleEvent { TimeUtc = T0, Kind = LifecycleKind.Orphaned, Pid = 7, Image = "find.exe" };
        var longExit = new LifecycleEvent { TimeUtc = T0, Kind = LifecycleKind.Exit, Pid = 8, CreateTime = T0.AddMinutes(-5).ToFileTimeUtc(), Image = "node.exe" };
        Assert.Equal([orphan, longExit], sum.Process([orphan, longExit], T0, _ => null));
    }

    [Fact]
    public void PendingStartsAreBounded()
    {
        var sum = new EventSummarizer();
        var events = Enumerable.Range(0, EventSummarizer.MaxPending + 50).Select(i => new LifecycleEvent
            { TimeUtc = T0, Kind = LifecycleKind.Start, Pid = 10 + i, CreateTime = T0.ToFileTimeUtc() + i, Image = "x.exe" }).ToList();
        sum.Process(events, T0, _ => "p.exe");
        Assert.Equal(EventSummarizer.MaxPending, sum.Pending);
        Assert.Equal(50, sum.RolledUp);
    }

    [Fact]
    public void RegistryForgetsShortLivedButKeepsLongLivedAndParentsOfLive()
    {
        var reg = new ProcessRegistry();
        var t = T0;
        // A shell that started a long-running child, then exited (the orphan's parent must stay for the chain).
        reg.OnStart(100, 4, t.ToFileTimeUtc(), "bash.exe", null, null, null, t);
        reg.OnStart(101, 100, t.AddSeconds(1).ToFileTimeUtc(), "find.exe", null, null, null, t);
        reg.OnStop(100, t.ToFileTimeUtc(), 0, t.AddSeconds(5));
        for (int i = 0; i < 400; i++)
        {
            long ct = t.AddSeconds(i).ToFileTimeUtc();
            reg.OnStart(1000 + i, 4, ct, "git.exe", null, null, null, t);
            reg.OnStop(1000 + i, ct, 0, t.AddSeconds(i).AddMilliseconds(80));
        }
        reg.OnStart(3000, 4, t.ToFileTimeUtc(), "long.exe", null, null, null, t);
        reg.OnStop(3000, t.ToFileTimeUtc(), 0, t.AddMinutes(10));
        var live = new List<ProcessSample> { new() { Pid = 101, ParentPid = 100, CreateTime = t.AddSeconds(1).ToFileTimeUtc(), ImageName = "find.exe" } };
        reg.ObserveSnapshot(live, t.AddMinutes(30));
        Assert.Null(reg.Get(new ProcessKey(1000, t.ToFileTimeUtc()))); // short-lived, 30 min ago: gone
        Assert.NotNull(reg.Get(new ProcessKey(3000, t.ToFileTimeUtc()))); // ran 10 minutes: kept
        Assert.Equal(["bash.exe", "find.exe"], reg.Ancestry(new ProcessKey(101, t.AddSeconds(1).ToFileTimeUtc())).Select(r => r.Image));
        Assert.Equal("git.exe", reg.RecentImage(1399));
    }

    [Fact]
    public void ExitAfterThirtySecondsInTheSameTickIsListed()
    {
        var sum = new EventSummarizer();
        long ct = T0.ToFileTimeUtc();
        var start = new LifecycleEvent { TimeUtc = T0, Kind = LifecycleKind.Start, Pid = 9, CreateTime = ct, Image = "cmd.exe" };
        Assert.Empty(sum.Process([start], T0.AddSeconds(1), _ => "pwsh.exe"));
        // No tick between start + 30 s and the exit: the exit arrives first, 33 s after the start.
        var exit = new LifecycleEvent { TimeUtc = T0.AddSeconds(33), Kind = LifecycleKind.Exit, Pid = 9, CreateTime = ct, Image = "cmd.exe", ExitCode = 42 };
        var output = sum.Process([exit], T0.AddSeconds(34), _ => "pwsh.exe");
        Assert.Equal([LifecycleKind.Start, LifecycleKind.Exit], output.Select(e => e.Kind));
        Assert.Equal(42, output[1].ExitCode);
    }

    [Fact]
    public void WorktreeRemovalIsAlwaysListed()
    {
        var sum = new EventSummarizer(alwaysNotable: PoolWatch.Core.Incidents.IncidentManager.IsWorktreeDelete);
        long ct = T0.ToFileTimeUtc();
        var start = new LifecycleEvent { TimeUtc = T0, Kind = LifecycleKind.Start, Pid = 9, CreateTime = ct, Image = "git.exe", CommandLine = @"git worktree remove D:\wt" };
        var exit = start with { Kind = LifecycleKind.Exit, TimeUtc = T0.AddSeconds(1) };
        Assert.Equal(2, sum.Process([start, exit], T0.AddSeconds(2), _ => "bash.exe").Count);
    }

    [Fact]
    public void WholeAncestryOfALiveProcessIsKept()
    {
        var reg = new ProcessRegistry();
        var t = T0;
        reg.ObserveSnapshot([new() { Pid = 4, ImageName = "System" }, new() { Pid = 10, ParentPid = 4, CreateTime = t.AddHours(-1).ToFileTimeUtc(), ImageName = "claude.exe" }], t);
        reg.OnStart(20, 10, t.ToFileTimeUtc(), "bash.exe", null, null, null, t);
        reg.OnStart(21, 20, t.AddSeconds(1).ToFileTimeUtc(), "bash.exe", null, null, null, t);
        reg.OnStart(22, 21, t.AddSeconds(2).ToFileTimeUtc(), "node.exe", null, null, null, t);
        reg.OnStop(21, t.AddSeconds(1).ToFileTimeUtc(), 0, t.AddSeconds(3));
        reg.OnStop(20, t.ToFileTimeUtc(), 0, t.AddSeconds(4));
        for (int i = 0; i < 300; i++) reg.OnStart(1000 + i, 4, t.AddSeconds(i).ToFileTimeUtc(), "x.exe", null, null, null, t);
        reg.ObserveSnapshot([new() { Pid = 4, ImageName = "System" }, new() { Pid = 10, ParentPid = 4, CreateTime = t.AddHours(-1).ToFileTimeUtc(), ImageName = "claude.exe" },
            new() { Pid = 22, ParentPid = 21, CreateTime = t.AddSeconds(2).ToFileTimeUtc(), ImageName = "node.exe" }], t.AddMinutes(40));
        Assert.Equal(["claude.exe", "bash.exe", "bash.exe", "node.exe"], reg.Ancestry(new ProcessKey(22, t.AddSeconds(2).ToFileTimeUtc())).Select(r => r.Image));
    }

    [Fact]
    public void SettingsMigrateTheOldHandleCensusDefault()
    {
        string f = Path.Combine(Path.GetTempPath(), "pw-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(f, "{ \"handleIntervalSeconds\": 60 }");
            Assert.Equal(300, Settings.Load(f).HandleIntervalSeconds);
            File.WriteAllText(f, "{ \"settingsVersion\": 2, \"handleIntervalSeconds\": 60 }");
            Assert.Equal(60, Settings.Load(f).HandleIntervalSeconds); // chosen after the change: kept
        }
        finally { File.Delete(f); }
    }
}
