using PoolWatch.Core;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// Filling the Pool causes rankings: which tag is ranked next (<see cref="RankPlan"/>, pure) and the background runner
/// (<see cref="TagRankings"/>, with a fake ranking: nothing reads the system).
/// </summary>
public class RankingTests
{
    private static readonly DateTime Now = new(2026, 10, 27, 15, 0, 0, DateTimeKind.Utc);
    private static readonly PoolTag A = PoolTag.Parse("Reff"), B = PoolTag.Parse("File"), C = PoolTag.Parse("MmSt");

    private static PoolTag? Next(PoolTag[] wanted, PoolTag? selected, Dictionary<PoolTag, DateTime> built, TimeSpan sinceLast) =>
        RankPlan.Next(wanted, selected, built, Now - sinceLast, Now);

    [Fact]
    public void TheSelectedRowIsRankedAtOnceWhenItHasNoRanking()
    {
        Assert.Equal(C, Next([A, B], C, [], TimeSpan.Zero)); // even right after another ranking
    }

    [Fact]
    public void EveryListedTagIsRankedOnceSpacedAFewSecondsApart()
    {
        var built = new Dictionary<PoolTag, DateTime> { [A] = Now };
        Assert.Null(Next([A, B, C], null, built, TimeSpan.FromSeconds(2)));      // within the spacing
        Assert.Equal(B, Next([A, B, C], null, built, RankPlan.NewGap));           // the first one without a ranking
        built[B] = Now;
        Assert.Equal(C, Next([A, B, C], null, built, RankPlan.NewGap));
    }

    [Fact]
    public void RankedTagsAreRefreshedOnlyWhenStaleAndNeverTwoWithinTheGap()
    {
        var built = new Dictionary<PoolTag, DateTime> { [A] = Now - TimeSpan.FromMinutes(6), [B] = Now - TimeSpan.FromMinutes(1) };
        Assert.Null(Next([A, B], null, built, TimeSpan.FromSeconds(10)));         // stale, but within the gap
        Assert.Equal(A, Next([A, B], null, built, RankPlan.Gap));                 // the stale one
        built[A] = Now;
        Assert.Null(Next([A, B], null, built, RankPlan.Gap));                     // all fresh: nothing to do
        // The selected row's tag is refreshed sooner (after SelectedFresh), still not within the gap.
        Assert.Equal(B, Next([A, B], B, built, RankPlan.Gap));
        Assert.Null(Next([A, B], B, built, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void TheRunnerRanksOffTheCallingThreadOneAtATime()
    {
        var rankings = new TagRankings(() => Now);
        int caller = Environment.CurrentManagedThreadId, ranker = -1;
        using var release = new ManualResetEventSlim();
        using var done = new ManualResetEventSlim();
        TagViewData? Build(PoolTag t)
        {
            ranker = Environment.CurrentManagedThreadId;
            release.Wait(TimeSpan.FromSeconds(10));
            return null;
        }
        Assert.Equal(A, rankings.Pump(Build, [A, B], null, done.Set));
        Assert.True(rankings.Building(A));
        Assert.Null(rankings.Pump(Build, [A, B], null, () => { })); // one at a time
        release.Set();
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
        Assert.NotEqual(caller, ranker);
        Assert.True(rankings.Tried(A));
        Assert.False(rankings.Building(A));
        Assert.Null(rankings.Get(A)); // a ranking that found nothing: the row stays blank
    }

    [Fact]
    public void AFailedRankingCountsAsTriedAndDoesNotLoop()
    {
        var rankings = new TagRankings(() => Now);
        using var done = new ManualResetEventSlim();
        Assert.Equal(A, rankings.Pump(_ => throw new InvalidOperationException("boom"), [A], null, done.Set));
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(rankings.Tried(A));
        Assert.Null(rankings.Pump(_ => null, [A], null, () => { })); // fresh: not tried again at once
    }
}
