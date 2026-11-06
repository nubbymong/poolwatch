using PoolWatch.Core.Model;
using PoolWatch.Core.Services;

namespace PoolWatch.AppTests;

/// <summary>
/// The processes' costs between samples are timed on the clock that only goes forward (each sample's TickMs), never on
/// the system clock: the load per process (the Services tab's Cost column) and the minute cost points (a change's
/// before-cost, the heavy background apps) carry on as before when the system clock is set back an hour or forward a day
/// between two samples.
/// </summary>
public sealed class ProcessCostClockTests
{
    private const long PerSecond = 10_000_000; // 100 ns units
    private static readonly DateTime Wall0 = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
    private const long Tick0 = 5_000_000;
    private static readonly ProcessKey Busy = new(100, 7);

    /// <summary>The sample <paramref name="i"/> of one every 10 seconds, with a process at 1 core; the system clock moved by <paramref name="wallJump"/>.</summary>
    private static Sample Taken(int i, TimeSpan wallJump) =>
        new(Wall0.AddSeconds(10 * i) + wallJump, new PoolTotals(0, 0, 0, 0), [],
            [new ProcessSample { Pid = Busy.Pid, CreateTime = Busy.CreateTime, ImageName = "esrv_svc.exe", KernelTime = PerSecond * 10 * i }])
        { TickMs = Tick0 + 10_000L * i };

    [Theory]
    [InlineData(-60)]      // the system clock set back an hour after the first minute
    [InlineData(24 * 60)]  // set forward a day
    [InlineData(0)]        // left alone
    public void TheLoadAndTheCostPointsFollowTheClockThatOnlyGoesForward(int wallJumpMinutes)
    {
        var costs = new ProcessCostHistory();
        Sample? before = null;
        var loads = new List<ProcessLoad.Rates?>();
        for (int i = 0; i <= 12 * 6; i++) // twelve minutes
        {
            var s = Taken(i, i >= 6 ? TimeSpan.FromMinutes(wallJumpMinutes) : TimeSpan.Zero);
            loads.Add(PoolWatch.Analysis.Diagnostics.StepLoad(s, before, costs, cores: 8, foregroundPid: 0));
            before = s;
        }

        Assert.Null(loads[0]); // no sample before the first
        // Every later sample gives the process's load over the 10 seconds since the one before, the jump included.
        Assert.All(loads.Skip(1), load => Assert.Equal(1.0, Assert.Single(load!.Processes).Cores, 3));
        // A point a minute, all along: the last ten minutes average 1 core, over the whole ten minutes.
        var avg = costs.Average(Busy, TimeSpan.FromMinutes(10), TimeSpan.FromMilliseconds(Tick0 + 10_000L * 72))!.Value;
        Assert.Equal((1.0, TimeSpan.FromMinutes(10)), (Math.Round(avg.cores, 3), avg.covered));
    }

    /// <summary>The history's own clock is Windows' time since it started, which setting the system clock does not move.</summary>
    [Fact]
    public void TheHistorysClockIsTheTimeSinceWindowsStarted() =>
        Assert.InRange(ProcessCostHistory.ClockNow(), TimeSpan.FromMilliseconds(Environment.TickCount64 - 60_000), TimeSpan.FromMilliseconds(Environment.TickCount64));
}
