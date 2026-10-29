using PoolWatch.Core.Impact;
using PoolWatch.Core.Model;
using PoolWatch.Core.Services;

namespace PoolWatch.Tests;

/// <summary>
/// What a startup app, scheduled task or service costs on the impact scale the list tabs show, which processes "End it
/// now" may end, who an app is from by where it is installed, and a task's triggers in words. Made-up apps only.
/// </summary>
public class EntryCostLevelTests
{
    [Theory]
    [InlineData(0.0, 0L, ImpactLevel.None)]
    [InlineData(0.049, 999L, ImpactLevel.None)]                // under the busy mark on both: idle
    [InlineData(ProcessCostHistory.BusyCores, 0L, ImpactLevel.Low)]
    [InlineData(0.0, ProcessCostHistory.BusyFaults, ImpactLevel.Low)]
    [InlineData(0.49, 99_999L, ImpactLevel.Low)]
    [InlineData(EntryCost.LowBelowCores, 0L, ImpactLevel.Medium)]
    [InlineData(0.1, EntryCost.LowBelowFaults, ImpactLevel.Medium)]
    [InlineData(1.49, 499_999L, ImpactLevel.Medium)]
    [InlineData(EntryCost.MediumBelowCores, 0L, ImpactLevel.High)]
    [InlineData(0.0, EntryCost.MediumBelowFaults, ImpactLevel.High)]
    [InlineData(1.7, 887_000L, ImpactLevel.High)]
    public void TheWorseOfCoresAndPageFaultsDecidesTheLevel(double cores, long faults, ImpactLevel level) =>
        Assert.Equal(level, EntryCost.Level(cores, faults));

    [Fact]
    public void TheLevelsThresholdsRiseInOrder()
    {
        Assert.True(ProcessCostHistory.BusyCores < EntryCost.LowBelowCores && EntryCost.LowBelowCores < EntryCost.MediumBelowCores);
        Assert.True(ProcessCostHistory.BusyFaults < EntryCost.LowBelowFaults && EntryCost.LowBelowFaults < EntryCost.MediumBelowFaults);
        // "idle" in the cost words and None on the scale are the same thing.
        Assert.Equal("idle", ServicePlan.Cost(0.049, 999));
        Assert.Equal(ImpactLevel.None, EntryCost.Level(0.049, 999));
    }

    [Fact]
    public void AMatchSaysWhichProcessesAndWhetherEachWasMatchedByItsFullPath()
    {
        const string exe = @"C:\Program Files\Example Vendor\Deck\deck.exe";
        var a = new ProcessKey(10, 1);
        var b = new ProcessKey(11, 1);
        var live = new List<LiveImage> { new(a, "deck.exe", exe), new(b, "deck.exe", exe), new(new ProcessKey(12, 1), "deck.exe", @"D:\Other\deck.exe") };
        var byPath = EntryCost.Match(exe, live, _ => (0.1, 0))!;
        Assert.Equal([a, b], byPath.Keys);
        Assert.True(byPath.ByFullPath);
        // One process whose path could not be read is matched by its name: then none of them may be ended from the list.
        var mixed = EntryCost.Match(exe, [.. live, new(new ProcessKey(13, 1), "deck.exe", null)], _ => (0.1, 0))!;
        Assert.Equal((3, false), (mixed.Processes, mixed.ByFullPath));
        // A bare file name has no path to compare.
        var bare = EntryCost.Match("deck.exe", live, _ => null)!;
        Assert.Equal((3, false), (bare.Keys.Count, bare.ByFullPath));
        Assert.Null(EntryCost.Match(@"C:\Program Files\Example Vendor\gone.exe", live, _ => null));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Example Vendor\Deck\deck.exe", "Example Vendor")]
    [InlineData(@"C:\Program Files (x86)\Example Games\launcher.exe", "Example Games")]
    [InlineData(@"C:\Users\someone\AppData\Local\Example Sync\sync.exe", "Example Sync")]
    [InlineData(@"C:\Users\someone\AppData\Roaming\Example Chat\app\chat.exe", "Example Chat")]
    [InlineData(@"C:\Users\someone\AppData\Local\Programs\Example Editor\editor.exe", "Example Editor")]
    [InlineData(@"C:\ProgramData\Example Agent\agent.exe", "Example Agent")]
    [InlineData(@"C:\Program Files\Common Files\Example\helper.exe", null)]
    [InlineData(@"C:\Program Files\WindowsApps\Example.App_1.0_x64__abc\app.exe", null)]
    [InlineData(@"C:\Program Files\tool.exe", null)]
    [InlineData(@"C:\Windows\System32\cmd.exe", null)]
    [InlineData("deck.exe", null)]
    [InlineData(null, null)]
    public void WhoAnAppIsFromIsTheFolderItIsInstalledIn(string? exe, string? from) => Assert.Equal(from, EntryCost.InstallFolderName(exe));

    [Fact]
    public void ATasksTriggersAreSaidInWordsEachOnce()
    {
        Assert.Equal("at sign-in", TaskTriggers.Words([9]));
        Assert.Equal("every day and at sign-in", TaskTriggers.Words([2, 9, 9]));
        Assert.Equal("at startup, every week and every month", TaskTriggers.Words([8, 3, 4, 5]));
        Assert.Equal("", TaskTriggers.Words([]));
        Assert.Equal("", TaskTriggers.Words([42]));                             // a kind PoolWatch does not name is left out
        Assert.Equal("when the PC is idle", TaskTriggers.Words([6, 42]));
        // A session-state trigger also fires when someone connects to the PC (at it or remotely), not only on a lock.
        Assert.Equal("when you lock, unlock or connect to the PC", TaskTriggers.Words([11]));
    }

    [Fact]
    public void AMatchIsMeasuredOnlyOnceACostIsKnown()
    {
        const string exe = @"C:\Program Files\Example Vendor\Deck\deck.exe";
        var live = new List<LiveImage> { new(new ProcessKey(10, 1), "deck.exe", exe), new(new ProcessKey(11, 1), "deck.exe", exe) };
        // Before PoolWatch's first sampling window no cost is known: running, but not "idle".
        var before = EntryCost.Match(exe, live, _ => null)!;
        Assert.Equal((2, false, 0.0), (before.Processes, before.Measured, before.Cores));
        Assert.True(EntryCost.Match(exe, live, _ => (0, 0))!.Measured);         // idle, measured
        Assert.True(EntryCost.Match(exe, live, k => k.Pid == 11 ? (0.2, 0) : null)!.Measured);
    }

    [Fact]
    public void TheCostNowIsTheLatestWindowsNotAnAverage()
    {
        var busy = new ProcessKey(10, 1);
        var reused = new ProcessKey(10, 2); // the same PID, another process
        var rates = new ProcessLoad.Rates([new ProcessLoad.ProcessRate(busy, "deck.exe", 0.7, 3000)], 0.1, 3000);
        var now = ProcessLoad.CostNow(rates);
        Assert.Equal((0.7, 3000L), now(busy));
        Assert.Equal((0.0, 0L), now(reused));                                   // not in the window: idle in it
        Assert.Equal((0.0, 0L), now(new ProcessKey(99, 1)));
        Assert.Null(ProcessLoad.CostNow(null)(busy));                          // no window yet: not measured
    }
}
