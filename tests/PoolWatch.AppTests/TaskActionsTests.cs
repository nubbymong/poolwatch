using PoolWatch.Core.Impact;
using PoolWatch.Core.Model;
using PoolWatch.Core.Services;
using PoolWatch.Engine;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// The decisions behind the Tasks and startup apps tab's actions, without a window or a real process: which processes
/// "End it now" may end and when it ends none, the one question, the ending itself through a made-up process opener, the
/// switch's target and direction, and the words for a row PoolWatch has not measured yet or a list with no row.
/// </summary>
public class TaskActionsTests
{
    private const string Exe = @"C:\Program Files\Example Vendor\Deck\deck.exe";
    private static readonly ProcessKey A = new(4100, 133_000_000_000_000_000), B = new(4200, 133_000_000_000_000_001);

    private static TaskEntry Startup(bool enabled = true, bool microsoft = false) =>
        new("startup:HKCU Run\\Deck", TaskKind.StartupApp, "Example Deck", "Example Vendor", "", $"\"{Exe}\" --background", enabled, microsoft,
            new StartupView("Deck", Exe, "HKCU Run", enabled, microsoft), Exe);

    private static TaskEntry Task(bool enabled = true) =>
        new("task:\\Vendor\\Nightly", TaskKind.ScheduledTask, "Nightly", "Example Vendor", "every day", Exe, enabled, false,
            new ScheduledTaskView(@"\Vendor\Nightly", "Nightly", "Example Vendor", enabled, Exe, ""), Exe);

    private static TaskRow Busy() => new(new TaskEntry("busy:7:1", TaskKind.Busy, "Build Server", null, "", @"C:\Tools\build.exe --watch", null, false,
        new BusyApp(new ProcessKey(7, 1), "build.exe", "Build Server", "--watch"), @"C:\Tools\build.exe"), new TaskCost(true, true, 0.9, 0, 1, [new ProcessKey(7, 1)], Average: true));

    private static TaskRow Running(TaskEntry e, params ProcessKey[] keys) => new(e, new TaskCost(true, true, 0.3, 0, Math.Max(1, keys.Length), keys));

    private static readonly Func<ProcessKey, string?> Deck = _ => "deck.exe";
    private static IReadOnlyList<string> NoServices(int pid) => [];

    // ---------------------------------------------------------------- "End it now"

    [Fact]
    public void EndItNowEndsEveryProcessOfARowItCanBeSureOf()
    {
        var plan = TaskActions.End(Running(Startup(), A, B), Deck, NoServices);
        Assert.Null(plan.Refusal);
        Assert.Equal([A, B], plan.Keys);
        var busy = TaskActions.End(Busy(), _ => null, NoServices); // a busy app's own process
        Assert.Null(busy.Refusal);
        Assert.Equal([new ProcessKey(7, 1)], busy.Keys);
    }

    [Fact]
    public void EndItNowEndsNothingOfARowItCannotBeSureOf()
    {
        Assert.Equal(TaskActions.SelectRunning, TaskActions.End(null, Deck, NoServices).Refusal);
        Assert.Equal(TaskActions.SelectRunning, TaskActions.End(Running(Startup(microsoft: true), A), Deck, NoServices).Refusal); // Microsoft's own
        Assert.Equal(TaskActions.SelectRunning, TaskActions.End(Running(Startup()), Deck, NoServices).Refusal);                   // matched by name only
        Assert.Equal(TaskActions.SelectRunning, TaskActions.End(new TaskRow(Startup(), TaskCost.NotRunning), Deck, NoServices).Refusal);
        Assert.Empty(TaskActions.End(Running(Startup(microsoft: true), A), Deck, NoServices).Keys);
    }

    [Fact]
    public void OneProcessPoolWatchNeverEndsRefusesTheWholeRow()
    {
        // B hosts a Windows service: turning the service off is the reversible way, so nothing of the row is ended.
        var hosts = TaskActions.End(Running(Startup(), A, B), Deck, pid => pid == B.Pid ? ["ExampleSvc"] : []);
        Assert.Empty(hosts.Keys);
        Assert.StartsWith("PoolWatch will not end Example Deck: it runs the Windows service ExampleSvc", hosts.Refusal);
        // A core or security process, by the image PoolWatch knows for it.
        var core = TaskActions.End(Running(Startup(), A, B), k => k == B ? "svchost.exe" : "deck.exe", NoServices);
        Assert.Contains("svchost.exe is a core Windows or security process", core.Refusal);
        // An image PoolWatch does not know is the program's own file name.
        var host = Startup() with { Exe = @"C:\Windows\System32\svchost.exe" };
        Assert.Contains("svchost.exe is a core Windows or security process", TaskActions.End(Running(host, A), _ => null, NoServices).Refusal);
        // A busy app's image is its own, whatever the registry says now.
        var vm = Busy() with { Entry = Busy().Entry with { Source = new BusyApp(new ProcessKey(7, 1), "vmmemWSL", "WSL", null) } };
        Assert.Contains("a virtual machine's memory", TaskActions.End(vm, _ => "build.exe", NoServices).Refusal);
    }

    [Fact]
    public void TheQuestionNamesItsProcessesItsProgramAndThatItCannotBeUndone()
    {
        var keys = Enumerable.Range(1, 7).Select(i => new ProcessKey(100 + i, 0)).ToList();
        string q = TaskActions.EndQuestion(Running(Startup(), [.. keys]), keys);
        Assert.StartsWith("End it now?\n\nExample Deck\nPID 101\n", q);
        Assert.Contains("PID 105\nand 2 more\n" + Exe, q);
        Assert.Contains("this can't be undone", q);
        Assert.Contains("--watch", TaskActions.EndQuestion(Busy(), Busy().Cost.EndKeys)); // a busy app's command line
    }

    /// <summary>Processes by PID with their start times; ends them in memory.</summary>
    private sealed class FakeOpener(Dictionary<int, long> running) : IProcessOpener
    {
        public List<int> Ended { get; } = [];

        public IProcessHandle? Open(int pid, out string? error)
        {
            error = running.ContainsKey(pid) ? null : "not found";
            return running.TryGetValue(pid, out long created) ? new Handle(this, pid, created) : null;
        }

        private sealed class Handle(FakeOpener owner, int pid, long created) : IProcessHandle
        {
            public long? CreateTime() => created;
            public string? Terminate() { owner.Ended.Add(pid); return null; }
            public void Dispose() { }
        }
    }

    [Fact]
    public void EndingAsksOnceAndEndsEachProcessThatIsStillTheSameOne()
    {
        var opener = new FakeOpener(new() { [A.Pid] = A.CreateTime, [B.Pid] = B.CreateTime });
        int asked = 0;
        var ended = new List<ProcessKey>();
        var problems = TaskActions.RunEnd(new EndPlan([A, B], null), "Example Deck", opener, () => { asked++; return true; }, ended.Add);
        Assert.Equal((1, 0), (asked, problems.Count));
        Assert.Equal([A.Pid, B.Pid], opener.Ended);
        Assert.Equal([A, B], ended);
    }

    [Fact]
    public void EndingStopsAtANoAndSaysWhatItCouldNotEnd()
    {
        var opener = new FakeOpener(new() { [A.Pid] = A.CreateTime, [B.Pid] = B.CreateTime + 1 }); // B's PID is another process now
        Assert.Empty(TaskActions.RunEnd(new EndPlan([A, B], null), "Example Deck", opener, () => false, _ => { }));
        Assert.Empty(opener.Ended);                                                                          // "no": nothing ended
        var problems = TaskActions.RunEnd(new EndPlan([A, B, new ProcessKey(9, 5)], null), "Example Deck", opener, () => true, _ => { });
        Assert.Equal([A.Pid], opener.Ended);
        Assert.Equal(2, problems.Count);
        Assert.Contains("a different process", problems[0]);
        Assert.Contains("no longer running", problems[1]);
        Assert.Empty(TaskActions.RunEnd(new EndPlan([], "refused"), "x", opener, () => throw new InvalidOperationException("never asked"), _ => { }));
    }

    // ---------------------------------------------------------------- the switch

    [Fact]
    public void TheSwitchGoesTheOtherWayFromHowItIsNow()
    {
        var on = Startup(enabled: true);
        Assert.Equal(new ToggleTarget(on.Source!, false), TaskActions.Toggle(new TaskRow(on, TaskCost.NotRunning)));
        var off = Startup(enabled: false);
        Assert.Equal(new ToggleTarget(off.Source!, true), TaskActions.Toggle(new TaskRow(off, TaskCost.NotRunning)));
        var task = Task(enabled: true);
        Assert.Equal(new ToggleTarget(task.Source!, false), TaskActions.Toggle(new TaskRow(task, TaskCost.NotRunning)));
        Assert.True(TaskActions.Toggle(new TaskRow(Task(enabled: false), TaskCost.NotRunning))!.Enable);
    }

    [Fact]
    public void ARowWithNoSwitchHasNoTarget()
    {
        Assert.Null(TaskActions.Toggle(null));
        Assert.Null(TaskActions.Toggle(Busy()));
        Assert.Null(TaskActions.Toggle(new TaskRow(Startup(microsoft: true), TaskCost.NotRunning)));
        Assert.Null(TaskActions.Toggle(new TaskRow(Startup() with { Enabled = null }, TaskCost.NotRunning)));
        // The panel's button follows the same rule: no switch, no button.
        Assert.Null(TaskRows.Detail(Busy()).Toggle);
        Assert.Null(TaskRows.Detail(new TaskRow(Startup(microsoft: true), TaskCost.NotRunning)).Toggle);
    }

    [Fact]
    public void ASwitchGoesOnTheTimelineInWords()
    {
        Assert.Equal(@"Startup app set not to start at sign-in by you: HKCU Run\Deck (Task Manager shows the same switch)",
            TaskActions.Timeline(new ToggleTarget(Startup().Source!, false)));
        Assert.Equal(@"Scheduled task enabled by you: \Vendor\Nightly", TaskActions.Timeline(new ToggleTarget(Task().Source!, true)));
    }

    // ---------------------------------------------------------------- not measured yet, nothing to list

    [Fact]
    public void BeforeTheFirstSamplingWindowARunningRowIsMeasuringNotIdle()
    {
        var live = new List<LiveImage> { new(A, "deck.exe", Exe) };
        var cost = TasksTab.CostOf(Startup(), live, ProcessLoad.CostNow(null));
        Assert.Equal((true, false, (ImpactLevel?)null), (cost.Running, cost.Measured, cost.Level));
        var row = new TaskRow(Startup(), cost);
        Assert.Equal("measuring…", TaskRows.CostText(cost));
        Assert.Equal("Running (1 process). PoolWatch is still measuring what it uses.", TaskRows.CostLine(row));
        Assert.True(TaskRows.CanEnd(row));                                                    // its process is known all the same
        // Ranked above what does not run and below what is idle.
        var idle = new TaskRow(Startup() with { Key = "idle", Name = "Idle" }, TasksTab.CostOf(Startup(), live, _ => (0, 0)));
        var gone = new TaskRow(Startup() with { Key = "gone", Name = "Gone" }, TaskCost.NotRunning);
        Assert.Equal(["Idle", "Example Deck", "Gone"], TaskRows.Build([gone, row, idle], new TaskFilter(ShowNotRunning: true), TaskRows.CostColumn, false).Select(r => r.Entry.Name));
    }

    [Fact]
    public void AListWithNoRowSaysWhy()
    {
        Assert.Null(TaskRows.Empty(TaskFilter.Default, shown: 3, loaded: true, readError: null));
        Assert.Equal(("Reading the startup apps and scheduled tasks…", ""), TaskRows.Empty(TaskFilter.Default, 0, loaded: false, null));
        Assert.Equal(("Nothing here is running now.", "Tick Show not running to see them all."), TaskRows.Empty(TaskFilter.Default, 0, true, null));
        Assert.Equal(("Nothing here matches \"deck\".", "Tick Show not running to look through them all."), TaskRows.Empty(new TaskFilter(Search: " deck "), 0, true, null));
        Assert.Equal(("Nothing to list here.", ""), TaskRows.Empty(new TaskFilter(ShowNotRunning: true), 0, true, null));
        Assert.Equal(("PoolWatch could not read the startup apps and scheduled tasks.", "Access is denied. Refresh tries again."),
            TaskRows.Empty(TaskFilter.Default, 0, false, "Access is denied."));
        // A read again failed while the rows from before are still listed: a search that matches nothing says so, and the
        // failure after it.
        Assert.Equal(("Nothing here matches \"deck\".", "Tick Show not running to look through them all. The list could not be read again: Access is denied. Refresh tries again."),
            TaskRows.Empty(new TaskFilter(Search: "deck"), 0, true, "Access is denied."));
        Assert.Equal(("Nothing to list here.", "The list could not be read again: Access is denied. Refresh tries again."),
            TaskRows.Empty(new TaskFilter(ShowNotRunning: true), 0, true, "Access is denied."));
    }
}

/// <summary>When the Tasks and startup apps tab reads its list again: pure state, no window.</summary>
public class TaskReadClockTests
{
    private static readonly DateTime T0 = new(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TheTasksAreReadAtFirstThenEveryFewMinutesNeverTwiceAtOnce()
    {
        var clock = new TaskReadClock(TasksTab.ReadEvery);
        Assert.True(clock.Due(T0));
        Assert.True(clock.Start(T0));
        Assert.False(clock.Due(T0 + TimeSpan.FromHours(1)));   // one runs: its result shows
        Assert.False(clock.Start(T0 + TimeSpan.FromSeconds(1)));
        clock.Finished(null);
        Assert.Equal((true, (string?)null), (clock.Loaded, clock.Error));
        Assert.False(clock.Due(T0 + TimeSpan.FromSeconds(10)));  // a refresh only works the costs out again
        Assert.True(clock.Due(T0 + TasksTab.ReadEvery));
    }

    [Fact]
    public void AFailedReadIsTriedAgainAfterTheSameWaitNotOnEveryRefresh()
    {
        var clock = new TaskReadClock(TasksTab.ReadEvery);
        clock.Start(T0);
        clock.Finished("Access is denied.");
        Assert.Equal((false, "Access is denied."), (clock.Loaded, clock.Error));
        Assert.False(clock.Due(T0 + TimeSpan.FromSeconds(10)));
        Assert.False(clock.Due(T0 + TasksTab.ReadEvery - TimeSpan.FromSeconds(1)));
        Assert.True(clock.Due(T0 + TasksTab.ReadEvery));
        Assert.True(clock.Start(T0 + TimeSpan.FromSeconds(20)));  // Refresh tries at once
    }

    [Fact]
    public void AChangeMadeElsewhereHasTheNextRefreshReadAgainEvenRightAfterARead()
    {
        // An AI agent's answer applied from the Services tab switched some startup apps: the tab must not show the old
        // switches until its next five-minute read.
        var clock = new TaskReadClock(TasksTab.ReadEvery);
        clock.Start(T0);
        clock.Finished(null);
        Assert.False(clock.Due(T0 + TimeSpan.FromSeconds(10)));
        clock.Stale();
        Assert.True(clock.Due(T0 + TimeSpan.FromSeconds(10)));
        // Made stale while a read runs (one that may have read the list before the change): read again once it ends.
        Assert.True(clock.Start(T0 + TimeSpan.FromSeconds(20)));
        clock.Stale();
        Assert.False(clock.Due(T0 + TimeSpan.FromSeconds(21)));   // that read's result shows first
        clock.Finished(null);
        Assert.True(clock.Due(T0 + TimeSpan.FromSeconds(22)));
    }
}
