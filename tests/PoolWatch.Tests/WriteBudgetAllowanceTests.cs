using PoolWatch.Core.Incidents;

namespace PoolWatch.Tests;

/// <summary>
/// What the report folders' write budget allows the writes that could wait (<see cref="WriteBudget.CanWaitAllowance"/>),
/// which About, profile.json and the CI's agentic-load check hold <see cref="WriteBudget.SpentCanWait"/> to: the refill,
/// the burst it starts with, and one burst more, as such a write is made with any room left and debited whole.
/// </summary>
public sealed class WriteBudgetAllowanceTests
{
    private const long Burst = 256L << 10;

    /// <summary>
    /// A burst's write with the whole burst there, then, a moment later, another with almost no room left: both made as
    /// the budget allows, 2 bursts written with almost no refill. An allowance of the refill and one burst would call that
    /// over its budget.
    /// </summary>
    [Fact]
    public void TwoBurstsWrittenWithTheRoomThereAreWithinWhatTheBudgetAllows()
    {
        var b = new WriteBudget(bytesPerHour: 1L << 20, burst: Burst);
        Assert.True(b.HasRoom(0));
        b.Spend(Burst, 0);
        Assert.False(b.HasRoom(0));
        Assert.True(b.HasRoom(10)); // 10 ms of refill: about 3 bytes
        b.Spend(Burst, 10);
        Assert.Equal(2 * Burst, b.SpentCanWait);
        Assert.True(b.SpentCanWait <= b.CanWaitAllowance, $"{b.SpentCanWait} > {b.CanWaitAllowance}");
        Assert.InRange(b.CanWaitAllowance, 2 * Burst, 2 * Burst + 10);
    }

    /// <summary>
    /// Ten hours of a load like the owner's: refreshes, rows and storm updates up to a burst each, made whenever the budget has
    /// room, and writes a reader needs at once of any size (ten incidents opening together); the writes that could wait stay
    /// within the allowance at every step, and come close to it (the budget is spent, not left idle).
    /// </summary>
    [Fact]
    public void UnderLoadWhatCouldWaitStaysWithinTheAllowanceAtEveryStep()
    {
        var rng = new Random(20261110);
        var b = new WriteBudget();
        long ms = 0;
        for (int step = 0; step < 36_000; step++, ms += 1000)
        {
            if (rng.Next(3600) == 0)
                for (int i = 0; i < 10; i++) b.Spend(rng.Next(20_000, 60_000), ms, must: true);
            for (int i = 0; i < 3; i++)
            {
                if (!b.HasRoom(ms)) { b.Hold(); break; }
                b.Spend(rng.Next(1, (int)Burst + 1), ms);
            }
            Assert.True(b.SpentCanWait <= b.CanWaitAllowance, $"step {step}: {b.SpentCanWait} > {b.CanWaitAllowance}");
        }
        Assert.True(b.SpentMust > 0);
        Assert.True(b.SpentCanWait > b.CanWaitAllowance - 4 * Burst - b.SpentMust, "the budget was spent as it refilled");
        Assert.True(b.Held > 0);
    }
}
