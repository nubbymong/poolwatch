using PoolWatch.Core;
using PoolWatch.Core.Model;

using static PoolWatch.Tests.Clocks;

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
        output.AddRange(sum.Process(reg.DrainEvents(), At(now.AddSeconds(2)), reg.RecentImage));
        Assert.Empty(output); // nothing notable yet: the node start is held until it has run 30 s
        output.AddRange(sum.Process([], At(now.AddSeconds(35)), reg.RecentImage));
        var nodeStart = Assert.Single(output);
        Assert.Equal(LifecycleKind.Start, nodeStart.Kind);
        Assert.Equal("node.exe", nodeStart.Image);
        output.Clear();
        output.AddRange(sum.Process([], At(now.AddSeconds(63)), reg.RecentImage)); // the window (opened at the first call) closes
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
        sum.Process(events, At(T0.AddSeconds(5)), _ => "bash.exe");
        var rollup = Assert.Single(sum.Flush(At(T0.AddSeconds(61))));
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
        // Owners are told apart by their identity key; two sessions may even share a display name.
        var a = new PoolWatch.Core.Identity.Owner(PoolWatch.Core.Identity.OwnerKind.ClaudeSession, "claude:a", "Claude Code session x");
        var b = new PoolWatch.Core.Identity.Owner(PoolWatch.Core.Identity.OwnerKind.ClaudeSession, "claude:b", "Claude Code session x");
        sum.Process(events, At(T0.AddSeconds(5)), _ => "bash.exe", ppid => { lookups++; return ppid < 2005 ? a : b; });
        var rollups = sum.Flush(At(T0.AddSeconds(61)));
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
        Assert.Equal([orphan, longExit], sum.Process([orphan, longExit], At(T0), _ => null));
    }

    [Fact]
    public void PendingStartsAreBounded()
    {
        var sum = new EventSummarizer();
        var events = Enumerable.Range(0, EventSummarizer.MaxPending + 50).Select(i => new LifecycleEvent
            { TimeUtc = T0, Kind = LifecycleKind.Start, Pid = 10 + i, CreateTime = T0.ToFileTimeUtc() + i, Image = "x.exe" }).ToList();
        sum.Process(events, At(T0), _ => "p.exe");
        Assert.Equal(EventSummarizer.MaxPending, sum.Pending);
        Assert.Equal(50, sum.RolledUp);
    }

    [Fact]
    public void RegistryForgetsShortLivedButKeepsLongLivedAndParentsOfLive()
    {
        // The registry's clock reads the test's time: each exit as it arrives, and the snapshot's prune half an hour on.
        var t = T0;
        var now = t.AddMinutes(10);
        var reg = new ProcessRegistry(clock: () => At(now));
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
        now = t.AddMinutes(30);
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
        Assert.Empty(sum.Process([start], At(T0.AddSeconds(1)), _ => "pwsh.exe"));
        // No tick between start + 30 s and the exit: the exit arrives first, 33 s after the start.
        var exit = new LifecycleEvent { TimeUtc = T0.AddSeconds(33), Kind = LifecycleKind.Exit, Pid = 9, CreateTime = ct, Image = "cmd.exe", ExitCode = 42 };
        var output = sum.Process([exit], At(T0.AddSeconds(34)), _ => "pwsh.exe");
        Assert.Equal([LifecycleKind.Start, LifecycleKind.Exit], output.Select(e => e.Kind));
        Assert.Equal(42, output[1].ExitCode);
    }

    [Fact]
    public void HowLongAProcessRanAndTheRollupWindowAreTimedOnTheClockThatOnlyGoesForward()
    {
        // The forward clock reads 0 as a start arrives (its time, by the system clock, is the test's start).
        long ct = T0.ToFileTimeUtc();
        LifecycleEvent Start(int pid) => new() { TimeUtc = T0, Kind = LifecycleKind.Start, Pid = pid, CreateTime = ct, Image = "cmd.exe" };
        // The system clock set back an hour: 31 s on, the start is listed as one that ran long, not held for an hour more.
        var back = new EventSummarizer();
        Assert.Empty(back.Process([Start(9)], At(T0, 0), _ => "pwsh.exe"));
        Assert.Equal(9, Assert.Single(back.Process([], At(T0.AddHours(-1).AddSeconds(31), 31_000), _ => "pwsh.exe")).Pid);
        // Set forward a day: 5 s on, the start is still held (it may yet be short-lived), and the rollup window, open 10 s,
        // does not close.
        var ahead = new EventSummarizer();
        Assert.Empty(ahead.Process([Start(9)], At(T0, 0), _ => "pwsh.exe"));
        Assert.Empty(ahead.Process([], At(T0.AddDays(1), 5_000), _ => "pwsh.exe"));
        Assert.Equal(1, ahead.Pending);
        // Its exit arrives 10 s after the start by the forward clock, stamped a day later by the system clock: it was
        // short-lived, so it is counted in a rollup, not listed.
        var exit = Start(9) with { Kind = LifecycleKind.Exit, TimeUtc = T0.AddDays(1).AddSeconds(10) };
        Assert.Empty(ahead.Process([exit], At(T0.AddDays(1).AddSeconds(10), 10_000), _ => "pwsh.exe"));
        Assert.Equal(0, ahead.Pending);
        Assert.Equal(2, ahead.RolledUp);
        // The window closes 60 s after it opened, by the forward clock.
        Assert.Empty(ahead.Process([], At(T0.AddDays(1).AddSeconds(59), 59_000), _ => "pwsh.exe"));
        Assert.Equal(LifecycleKind.Rollup, Assert.Single(ahead.Process([], At(T0.AddDays(1).AddSeconds(60), 60_000), _ => "pwsh.exe")).Kind);
    }

    [Fact]
    public void TheExitOfAStartListedAsLongRunningIsListedWhateverTheSystemClockDidWhileItRan()
    {
        // A process that runs 5 minutes: its start is held, then listed 31 s on. The system clock is set back an hour 2
        // minutes in; its exit arrives stamped an hour before its start by the system clock. It ran 5 minutes on the forward
        // clock, so its exit is listed, not counted as a short command's.
        long ct = T0.ToFileTimeUtc();
        var sum = new EventSummarizer();
        var start = new LifecycleEvent { TimeUtc = T0, Kind = LifecycleKind.Start, Pid = 700, CreateTime = ct, Image = "node.exe" };
        Assert.Empty(sum.Process([start], At(T0, 1_000_000), _ => "bash.exe"));
        Assert.Equal([LifecycleKind.Start], sum.Process([], At(T0.AddSeconds(31), 1_031_000), _ => "bash.exe").Select(e => e.Kind));
        var exit = start with { Kind = LifecycleKind.Exit, TimeUtc = T0.AddMinutes(5) - TimeSpan.FromHours(1), ExitCode = 0 };
        Assert.Equal([exit], sum.Process([exit], At(T0.AddMinutes(5) - TimeSpan.FromHours(1), 1_300_000), _ => "bash.exe"));
        Assert.Equal(0, sum.RolledUp);
    }

    [Fact]
    public void AtMost20000StartsListedAsLongRunningAreKeptTheEarliestLetGoFirst()
    {
        // 20,001 starts are held and listed as long-running: the first alone, then 19,999 a second later, then one more. Past
        // 20,000 the earliest listing is let go. The system clock is then set back 50 s and two exits arrive, 70 s after the
        // first start on the forward clock: the first process's exit is judged by its create time (20 s before the exit's
        // time of day: counted as a short command's), the second's by its listing (listed).
        var sum = new EventSummarizer();
        LifecycleEvent Start(int pid, DateTime created) => new() { TimeUtc = created, Kind = LifecycleKind.Start, Pid = pid, CreateTime = created.ToFileTimeUtc(), Image = "node.exe" };
        var first = Start(10, T0);
        var second = Start(11, T0.AddSeconds(1));
        sum.Process([first], At(T0, 0), _ => "bash.exe");
        sum.Process([second, .. Enumerable.Range(12, EventSummarizer.MaxPending - 2).Select(pid => Start(pid, T0.AddSeconds(1)))], At(T0.AddSeconds(1), 1000), _ => "bash.exe");
        Assert.Equal(EventSummarizer.MaxPending, sum.Pending);
        sum.Process([], At(T0.AddSeconds(31), 31_000), _ => "bash.exe");
        var last = Start(1_000_000, T0.AddSeconds(32));
        sum.Process([last], At(T0.AddSeconds(32), 32_000), _ => "bash.exe");
        Assert.Equal([1_000_000], sum.Process([], At(T0.AddSeconds(62), 62_000), _ => "bash.exe").Select(e => e.Pid));
        Assert.Equal(EventSummarizer.MaxPending, sum.Listed);
        var exitUtc = T0.AddSeconds(20);
        var exits = sum.Process([first with { Kind = LifecycleKind.Exit, TimeUtc = exitUtc }, second with { Kind = LifecycleKind.Exit, TimeUtc = exitUtc }],
            At(exitUtc, 70_000), _ => "bash.exe");
        Assert.Equal([11], exits.Select(e => e.Pid));
        Assert.Equal(1, sum.RolledUp);
    }

    [Fact]
    public void ListingAStartWhileTheListIsFullFindsTheEarliestWithoutAPassOverTheList()
    {
        // 20,000 starts are held and listed as long-running, and their exits never come; then ten rounds of 20,000 more are
        // held and listed, 31 s apart on the forward clock. Each listing past the 20,000 lets the earliest go, found without
        // a look at every listed start, so the ten rounds take well under 10 s: about 0.3 s in a Debug build, which leaves
        // room for a loaded machine, and the pass it guards against, over the 20,000 for each of the 200,000 listings
        // (4 billion looks), took about 36 s when it was measured in a Debug build. The last round's starts are the ones
        // kept: with the system clock set back, the exit of the first start is judged by its create time (20 s: a short
        // command's), and the exit of a start of the last round by its listing (listed). With that one gone, two more starts
        // are listed: the first takes its place, and the second lets one of the last round's go (all held at one time, so
        // each is the earliest). Of the last round's other starts and the two new ones, every exit but that one's is listed.
        var sum = new EventSummarizer();
        int pid = 10;
        long ms = 0;
        List<LifecycleEvent> Round(int count)
        {
            var created = T0.AddMilliseconds(ms);
            var starts = Enumerable.Range(pid, count)
                .Select(p => new LifecycleEvent { TimeUtc = created, Kind = LifecycleKind.Start, Pid = p, CreateTime = created.ToFileTimeUtc(), Image = "node.exe" })
                .ToList();
            pid += count;
            sum.Process(starts, At(created, ms), _ => "bash.exe");
            ms += 31_000;
            Assert.Equal(count, sum.Process([], At(T0.AddMilliseconds(ms), ms), _ => "bash.exe").Count(e => e.Kind == LifecycleKind.Start));
            return starts;
        }
        var first = Round(EventSummarizer.MaxPending)[0];
        var timer = System.Diagnostics.Stopwatch.StartNew();
        List<LifecycleEvent> last = [];
        for (int round = 0; round < 10; round++) last = Round(EventSummarizer.MaxPending);
        timer.Stop();
        Assert.True(timer.ElapsedMilliseconds < 10_000, $"ten rounds of 20,000 listings took {timer.ElapsedMilliseconds} ms");
        Assert.Equal(EventSummarizer.MaxPending, sum.Listed);
        List<LifecycleEvent> Exits(params LifecycleEvent[] starts)
        {
            // Each exit stamped 20 s after its process's create time by the system clock, set back since.
            var exits = starts.Select(s => s with { Kind = LifecycleKind.Exit, TimeUtc = s.TimeUtc.AddSeconds(20) }).ToList();
            return sum.Process(exits, At(exits[^1].TimeUtc, ms), _ => "bash.exe");
        }
        Assert.Equal([last[0].Pid], Exits(first, last[0]).Select(e => e.Pid));
        Assert.Equal(1, sum.RolledUp);
        var added = Round(2);
        Assert.Equal(EventSummarizer.MaxPending, sum.Listed);
        Assert.Equal(EventSummarizer.MaxPending, Exits([.. last.Skip(1), .. added]).Count(e => e.Kind == LifecycleKind.Exit));
        Assert.Equal(2, sum.RolledUp);
        Assert.Equal(0, sum.Listed);
        // The list, empty again, is filled and one more start listed: one of the 20,000 is let go, none that exited before.
        Round(EventSummarizer.MaxPending);
        Round(1);
        Assert.Equal(EventSummarizer.MaxPending, sum.Listed);
    }

    [Fact]
    public void WithTheListFullTheStartHeldEarliestIsLetGoNotTheFirstListed()
    {
        // The starts listed as long-running are let go by the time each was held at (its own time read onto the forward
        // clock as it arrived), the earliest first, not by the order they were listed in. 19,999 starts are held at 100 s
        // and listed; then one is held at 10 s (its event's own time, read as it arrives at 200 s) and listed after them.
        // With the list full, one more listed lets that one go, never the first one listed. Each exit comes 5 s after its
        // process's create time: one whose listing was let go is judged by that (a short command's: counted), one still
        // listed is listed.
        var sum = new EventSummarizer();
        long create = T0.AddSeconds(235).ToFileTimeUtc();
        LifecycleEvent Start(int pid, int second) =>
            new() { TimeUtc = T0.AddSeconds(second), Kind = LifecycleKind.Start, Pid = pid, CreateTime = create, Image = "node.exe" };
        List<LifecycleEvent> Process(int second, params LifecycleEvent[] events) =>
            sum.Process(events, At(T0.AddSeconds(second), second * 1000L), _ => "bash.exe");
        var listedFirst = Enumerable.Range(100, EventSummarizer.MaxPending - 1).Select(p => Start(p, 100)).ToArray();
        Process(100, listedFirst);
        Process(131);
        Assert.Equal(EventSummarizer.MaxPending - 1, sum.Listed);
        var heldEarliest = Start(50, 10);
        Process(200, heldEarliest); // held at 10 s and listed at once: it has run 190 s
        Assert.Equal(EventSummarizer.MaxPending, sum.Listed);
        Process(201, Start(60, 201));
        Process(232); // listed: the list was full, so one was let go
        Assert.Equal(EventSummarizer.MaxPending, sum.Listed);
        var exits = new[] { heldEarliest, listedFirst[0] }.Select(s => s with { Kind = LifecycleKind.Exit, TimeUtc = T0.AddSeconds(240) }).ToArray();
        Assert.Equal([listedFirst[0].Pid], Process(240, exits).Where(e => e.Kind == LifecycleKind.Exit).Select(e => e.Pid));
    }

    [Fact]
    public void WorktreeRemovalIsAlwaysListed()
    {
        var sum = new EventSummarizer(alwaysNotable: PoolWatch.Core.Incidents.IncidentManager.IsWorktreeDelete);
        long ct = T0.ToFileTimeUtc();
        var start = new LifecycleEvent { TimeUtc = T0, Kind = LifecycleKind.Start, Pid = 9, CreateTime = ct, Image = "git.exe", CommandLine = @"git worktree remove D:\wt" };
        var exit = start with { Kind = LifecycleKind.Exit, TimeUtc = T0.AddSeconds(1) };
        Assert.Equal(2, sum.Process([start, exit], At(T0.AddSeconds(2)), _ => "bash.exe").Count);
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
