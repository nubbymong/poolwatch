using PoolWatch.Engine;
using PoolWatch.Headless;

namespace PoolWatch.AppTests;

/// <summary>
/// S5 (review round 2): Clear cache runs in-process (PoolWatch is always elevated); the SYSTEM-task setup is gone and only
/// its removal is left. Nothing here touches Task Scheduler or purges anything: the rule and the parser are pure.
/// </summary>
public class ClearCacheTests
{
    private static CacheHelper.Status Elevated(bool? legacy = false, params string[] old) => new(true, legacy, legacy == true ? @"\x" : null, old, null);

    [Fact]
    public void TheAutomaticClearNeedsOnlyElevationAndNoHandMadeTask()
    {
        Assert.True(CacheHelper.AutoClearAllowed(Elevated()));                                   // in-process: no setup needed
        Assert.True(CacheHelper.AutoClearAllowed(Elevated(legacy: null)));                       // the task could not be read
        Assert.True(CacheHelper.AutoClearAllowed(Elevated(false, CacheHelper.StandbyTask)));     // old tasks left over: irrelevant
        Assert.False(CacheHelper.AutoClearAllowed(Elevated(legacy: true)));                      // your own clear task: never both
        Assert.False(CacheHelper.AutoClearAllowed(new CacheHelper.Status(false, null, null, [], "not elevated")));
        Assert.False(CacheHelper.AutoClearAllowed(null));                                        // status not read yet
    }

    [Fact]
    public void OldTasksAreReportedForRemoval()
    {
        Assert.False(Elevated().OldTasksPresent);
        Assert.True(Elevated(false, CacheHelper.StandbyTask, CacheHelper.LowPriorityTask).OldTasksPresent);
    }

    [Fact]
    public void TheOldTasksAreRemovedAutomaticallyOnceAndNoted()
    {
        // Review round 3 (item 7): the first elevated status read removes them, once per start, and leaves a timeline line.
        int removes = 0, queries = 0;
        var old = Elevated(false, CacheHelper.StandbyTask, CacheHelper.LowPriorityTask);
        var cleanup = new CacheHelper.OldTaskCleanup(
            () => ++queries == 1 ? old : Elevated(),
            () => { removes++; return [@"\PoolWatch\Clear cache (standby)", @"\PoolWatch\Clear cache (low priority)"]; });
        var after = cleanup.Query();
        Assert.Equal(1, removes);
        Assert.False(after.OldTasksPresent);                                  // the status shown is re-read after the removal
        Assert.True(cleanup.Notes.TryDequeue(out var note));
        Assert.Contains("Removed", note);
        Assert.Contains("not touched", note);                                 // says your own clear task was left alone
        Assert.False(cleanup.Notes.TryDequeue(out _));

        queries = 0;
        cleanup.Query();                                                      // they are back (or the removal failed): not again
        Assert.Equal(1, removes);
        Assert.Empty(cleanup.Notes);
    }

    [Fact]
    public void NoAutomaticRemovalUnelevatedOrWithNothingToRemove()
    {
        int removes = 0;
        List<string> Remove() { removes++; return ["x"]; }
        Assert.Null(CacheHelper.RemoveOldTasksAutomatically(Elevated(), Remove));
        Assert.Null(CacheHelper.RemoveOldTasksAutomatically(new CacheHelper.Status(false, null, null, [CacheHelper.StandbyTask], "not elevated"), Remove));
        var cleanup = new CacheHelper.OldTaskCleanup(() => Elevated(legacy: true), Remove);     // only your own task: never
        cleanup.Query();
        Assert.Equal(0, removes);
        Assert.Empty(cleanup.Notes);
    }

    [Fact]
    public void AFailedAutomaticRemovalPointsAtTheButton()
    {
        var s = Elevated(false, CacheHelper.StandbyTask);
        Assert.Contains("Remove old clear-cache tasks", CacheHelper.RemoveOldTasksAutomatically(s, () => [])!);
        Assert.Contains("denied", CacheHelper.RemoveOldTasksAutomatically(s, () => throw new UnauthorizedAccessException("denied"))!);
        Assert.Equal([CacheHelper.StandbyTask, CacheHelper.LowPriorityTask], CacheHelper.OldTaskNames); // never your own task
    }

    [Theory]
    [InlineData(@"C:\Program Files\Sysinternals\RAMMap64.exe", "-Ew", true)]          // any name, any folder
    [InlineData(@"""D:\Tools\RAMMap.exe""", "-accepteula -Et", true)]                   // quoted, the standby list
    [InlineData(@"C:\Tools\rammap64a.exe", "-E0", true)]                                // Arm64 build, priority-0 standby
    [InlineData(@"%ProgramFiles%\Sysinternals\RAMMap64.exe", "-Em", true)]              // modified list
    [InlineData(@"C:\Program Files\Sysinternals\RAMMap64.exe", "", false)]             // only opens the UI
    [InlineData(@"C:\Program Files\Sysinternals\RAMMap64.exe", "snapshot.rmp", false)]  // opens a saved snapshot
    [InlineData(@"C:\Program Files\Sysinternals\RAMMap64.exe", "-Ewx", false)]          // not one of its options
    [InlineData(@"C:\Tools\NotRAMMap64.exe", "-Ew", false)]                             // another program
    [InlineData(@"C:\Tools\cleaner.exe", "--RAMMap64.exe -Ew", false)]                  // the name in the arguments only
    [InlineData(null, "-Ew", false)]
    public void YourOwnClearTaskIsFoundByWhatItRunsNotByItsName(string? program, string? arguments, bool clears) =>
        Assert.Equal(clears, CacheHelper.IsOwnClearAction(program, arguments));

    [Fact]
    public void TheSetupOptionIsGoneAndOnlyRemovalAndTheOwnClearTaskStepRemain()
    {
        Assert.NotEmpty(CommandLineOptions.Parse(["--setup-clear-cache"]).Errors);
        Assert.Equal("remove", CommandLineOptions.Parse(["--remove-clear-cache"]).CacheSetup);
        var auto = CommandLineOptions.Parse(["--enable-auto-clear", "--yes"]);
        Assert.Empty(auto.Errors);
        Assert.Equal("enable-auto", auto.CacheSetup);
        Assert.True(auto.LegacyAnswer);
        Assert.Equal("remove-legacy", CommandLineOptions.Parse(["--remove-legacy-clear-task", "--no"]).CacheSetup);
        Assert.DoesNotContain("setup-clear-cache", CommandLineOptions.Usage);
    }
}
