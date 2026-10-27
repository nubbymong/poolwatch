using PoolWatch.Core;
using PoolWatch.Core.Model;

namespace PoolWatch.Tests;

public class ProcessRegistryTests
{
    private static readonly DateTime T0 = new(2026, 10, 20, 6, 0, 0, DateTimeKind.Utc);
    private static long Ft(int minutes) => T0.AddMinutes(minutes).ToFileTimeUtc();

    private static ProcessSample P(int pid, int ppid, int startMin, string name) =>
        new() { Pid = pid, ParentPid = ppid, CreateTime = Ft(startMin), ImageName = name };

    [Fact]
    public void ResolvesParentsAndAncestryIndependentOfOrder()
    {
        var r = new ProcessRegistry();
        r.ObserveSnapshot([P(30, 20, 3, "bash.exe"), P(10, 4, 1, "AI Code Conductor.exe"), P(20, 10, 2, "claude.exe"), P(4, 0, 0, "System")], T0.AddMinutes(5));
        var chain = r.Ancestry(new ProcessKey(30, Ft(3)));
        Assert.Equal(["AI Code Conductor.exe", "claude.exe", "bash.exe"], chain.Select(c => c.Image));
        Assert.Empty(r.DrainEvents()); // the initial snapshot is not a burst of "starts"
    }

    [Fact]
    public void ParentExitOrphansChildAndKeepsDeadAncestor()
    {
        var r = new ProcessRegistry();
        r.ObserveSnapshot([P(4, 0, 0, "System"), P(10, 4, 1, "claude.exe")], T0);
        r.OnStart(20, 10, Ft(2), "bash.exe", null, "bash -c find", null, T0.AddMinutes(2));
        r.OnStart(30, 20, Ft(3), "find.exe", null, "find / -maxdepth 6 -type d -name x", null, T0.AddMinutes(3));
        r.OnStop(20, Ft(2), 0, T0.AddMinutes(4));
        var find = r.Get(new ProcessKey(30, Ft(3)))!;
        Assert.False(find.IsOrphan); // not yet: a pipeline's last command may outlive its shell by a moment
        r.ObserveSnapshot([P(4, 0, 0, "System"), P(10, 4, 1, "claude.exe"), P(30, 20, 3, "find.exe")], T0.AddMinutes(4).AddSeconds(31));
        var events = r.DrainEvents();
        Assert.Contains(events, e => e.Kind == LifecycleKind.Orphaned && e.Pid == 30);
        Assert.True(find.IsOrphan);
        Assert.Equal(T0.AddMinutes(4), find.OrphanedUtc);
        Assert.Equal(["claude.exe", "bash.exe", "find.exe"], r.Ancestry(find.Key).Select(c => c.Image));
        Assert.False(r.Ancestry(find.Key)[1].IsAlive);
    }

    [Fact]
    public void PipelineEndAndDaemonsAreNotOrphans()
    {
        var r = new ProcessRegistry();
        r.ObserveSnapshot([P(4, 0, 0, "System"), P(10, 4, 1, "claude.exe")], T0);
        r.OnStart(20, 10, Ft(2), "bash.exe", null, "bash -c \"ls | grep x\"", null, T0.AddMinutes(2));
        r.OnStart(21, 20, Ft(2), "grep.exe", null, "grep x", null, T0.AddMinutes(2));
        r.OnStart(22, 20, Ft(2), "gpg-agent.exe", null, "gpg-agent --homedir x --use-standard-socket --daemon", null, T0.AddMinutes(2));
        r.OnStop(20, Ft(2), 0, T0.AddMinutes(2).AddSeconds(1));
        r.OnStop(21, Ft(2), 0, T0.AddMinutes(2).AddSeconds(1.2)); // grep ends 0.2 s after bash
        r.ObserveSnapshot([P(4, 0, 0, "System"), P(10, 4, 1, "claude.exe"), P(22, 20, 2, "gpg-agent.exe")], T0.AddMinutes(5));
        Assert.DoesNotContain(r.DrainEvents(), e => e.Kind == LifecycleKind.Orphaned);
        Assert.False(r.Get(new ProcessKey(21, Ft(2)))!.IsOrphan);
        var agent = r.Get(new ProcessKey(22, Ft(2)))!;
        Assert.False(agent.IsOrphan);
        Assert.Equal("background service started by bash.exe", agent.Label);
    }

    [Fact]
    public void PidReuseIsNotAParent()
    {
        var r = new ProcessRegistry();
        // PID 50 now belongs to a process that started *after* the child: the real parent is gone.
        r.ObserveSnapshot([P(4, 0, 0, "System"), P(50, 4, 10, "notepad.exe"), P(60, 50, 5, "find.exe")], T0.AddMinutes(11));
        var child = r.Get(new ProcessKey(60, Ft(5)))!;
        Assert.Null(child.ParentKey);
        Assert.False(child.IsOrphan); // the 30 s grace applies to processes first seen without a parent too
        r.ObserveSnapshot([P(4, 0, 0, "System"), P(50, 4, 10, "notepad.exe"), P(60, 50, 5, "find.exe")], T0.AddMinutes(11).AddSeconds(31));
        Assert.True(child.IsOrphan);
    }

    [Fact]
    public void VanishedProcessesExitAndNewOnesStart()
    {
        var r = new ProcessRegistry();
        r.ObserveSnapshot([P(4, 0, 0, "System"), P(10, 4, 1, "a.exe")], T0);
        r.ObserveSnapshot([P(4, 0, 0, "System"), P(11, 4, 2, "b.exe")], T0.AddMinutes(2));
        Assert.True(r.Get(new ProcessKey(10, Ft(1)))!.IsAlive); // within the ETW grace period
        r.ObserveSnapshot([P(4, 0, 0, "System"), P(11, 4, 2, "b.exe")], T0.AddMinutes(2).AddSeconds(10));
        var ev = r.DrainEvents();
        Assert.Contains(ev, e => e.Kind == LifecycleKind.Exit && e.Pid == 10);
        Assert.Contains(ev, e => e.Kind == LifecycleKind.Start && e.Pid == 11 && e.Detail == "seen in snapshot");
        Assert.False(r.Get(new ProcessKey(10, Ft(1)))!.IsAlive);
    }

    [Fact]
    public void LateEtwStopKeepsExitCodeAfterSnapshotMissedTheProcess()
    {
        var r = new ProcessRegistry();
        r.ObserveSnapshot([P(4, 0, 0, "System"), P(10, 4, 1, "cmd.exe")], T0);
        r.ObserveSnapshot([P(4, 0, 0, "System")], T0.AddSeconds(5));
        r.OnStop(10, 0, 42, T0.AddSeconds(4));
        var exit = r.DrainEvents().Single(e => e.Kind == LifecycleKind.Exit);
        Assert.Equal(42, exit.ExitCode);
    }

    [Fact]
    public void ProcessCreatedAfterSnapshotIsNotMarkedExited()
    {
        var r = new ProcessRegistry { ExitGrace = TimeSpan.Zero };
        r.ObserveSnapshot([P(4, 0, 0, "System")], T0);
        // ETW start for a process created after the snapshot below was taken.
        r.OnStart(90, 4, Ft(10), "late.exe", null, null, null, T0.AddMinutes(10));
        r.ObserveSnapshot([P(4, 0, 0, "System")], T0.AddMinutes(9));
        Assert.True(r.Get(new ProcessKey(90, Ft(10)))!.IsAlive);
    }

    [Fact]
    public void EtwStartAfterSnapshotDoesNotDuplicateStartEvent()
    {
        var r = new ProcessRegistry();
        r.ObserveSnapshot([P(4, 0, 0, "System")], T0);
        r.ObserveSnapshot([P(4, 0, 0, "System"), P(12, 4, 1, "x.exe")], T0.AddMinutes(1));
        r.OnStart(12, 4, Ft(1), "x.exe", null, "x", null, T0.AddMinutes(1));
        Assert.Single(r.DrainEvents(), e => e.Kind == LifecycleKind.Start);
    }

    [Fact]
    public void StopEventMatchesWithinOneSecondOfCreateTime()
    {
        var r = new ProcessRegistry();
        r.ObserveSnapshot([P(4, 0, 0, "System"), P(10, 4, 1, "a.exe")], T0);
        r.OnStop(10, Ft(1) + 5000, 3, T0.AddMinutes(3));
        var rec = r.Get(new ProcessKey(10, Ft(1)))!;
        Assert.Equal(3, rec.ExitCode);
        Assert.False(rec.IsAlive);
    }

    [Fact]
    public void EnricherRunsBeforeStartEventIsQueued()
    {
        var r = new ProcessRegistry { Enricher = rec => rec.CommandLine = "cmd /c exit 7" };
        r.ObserveSnapshot([P(4, 0, 0, "System")], T0);
        r.OnStart(70, 4, Ft(1), "cmd.exe", null, null, null, T0.AddMinutes(1));
        Assert.Equal("cmd /c exit 7", r.DrainEvents().Single(e => e.Kind == LifecycleKind.Start).CommandLine);
    }

    [Fact]
    public void SnapshotRacingEtwStartReportsOneStart()
    {
        ProcessRegistry r = null!;
        bool inside = false;
        // The snapshot adds the process while OnStart is enriching it outside the lock.
        r = new ProcessRegistry
        {
            Enricher = rec =>
            {
                if (inside || rec.Pid != 12) return;
                inside = true;
                r.ObserveSnapshot([P(4, 0, 0, "System"), P(12, 4, 1, "x.exe")], T0.AddMinutes(1));
            },
        };
        r.ObserveSnapshot([P(4, 0, 0, "System")], T0);
        r.OnStart(12, 4, Ft(1), "x.exe", null, "x", null, T0.AddMinutes(1));
        Assert.Single(r.DrainEvents(), e => e.Kind == LifecycleKind.Start);
    }

    [Fact]
    public void EstimatedCreateTimeIsRekeyedByTheSnapshot()
    {
        var r = new ProcessRegistry { ExitGrace = TimeSpan.Zero };
        r.ObserveSnapshot([P(4, 0, 0, "System"), P(10, 4, 0, "parent.exe")], T0);
        long estimate = Ft(1) + TimeSpan.TicksPerSecond / 2; // ETW timestamp, slightly after the real create time
        r.OnStart(20, 10, estimate, "child.exe", null, "child --x", null, T0.AddMinutes(1), createTimeEstimated: true);
        r.OnStart(30, 20, Ft(2), "grandchild.exe", null, null, null, T0.AddMinutes(2));
        r.DrainEvents();
        r.ObserveSnapshot([P(4, 0, 0, "System"), P(10, 4, 0, "parent.exe"), P(20, 10, 1, "child.exe"), P(30, 20, 2, "grandchild.exe")], T0.AddMinutes(3));
        r.ObserveSnapshot([P(4, 0, 0, "System"), P(10, 4, 0, "parent.exe"), P(20, 10, 1, "child.exe"), P(30, 20, 2, "grandchild.exe")], T0.AddMinutes(4));
        var ev = r.DrainEvents();
        Assert.DoesNotContain(ev, e => e.Kind is LifecycleKind.Exit or LifecycleKind.Orphaned or LifecycleKind.Start);
        var child = r.Get(new ProcessKey(20, Ft(1)))!;
        Assert.True(child.IsAlive);
        Assert.Equal("child --x", child.CommandLine);
        Assert.Null(r.Get(new ProcessKey(20, estimate)));
        Assert.Equal(child.Key, r.Get(new ProcessKey(30, Ft(2)))!.ParentKey);
    }
}
