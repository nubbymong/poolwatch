using PoolWatch.Core;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Identity;
using PoolWatch.Core.Model;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// The Pool causes rankings hold only what the details show: five owners with their first processes and the last ten
/// notable events (not the whole ranking and a row per event of the hour). A released tag keeps its ranking, and the
/// window can drop them all when it hides.
/// </summary>
public class TagViewKeepTests
{
    private static readonly DateTime T0 = new(2026, 10, 27, 15, 0, 0, DateTimeKind.Utc);
    private static readonly PoolTag A = PoolTag.Parse("Reff"), B = PoolTag.Parse("File");

    /// <summary>Three events a second, of every kind, a fifth of them short-command rollups.</summary>
    private static List<LifecycleEvent> Events(int count) => Enumerable.Range(0, count).Select(i =>
    {
        var t = T0.AddSeconds(i / 3); // equal times keep their order
        var kind = (i % 5) switch { 0 => LifecycleKind.Rollup, 1 => LifecycleKind.Start, 2 => LifecycleKind.Exit, 3 => LifecycleKind.Alert, _ => LifecycleKind.Orphaned };
        return new LifecycleEvent
        {
            TimeUtc = t, Kind = kind, Pid = 1000 + i, CreateTime = t.AddSeconds(-5).ToFileTimeUtc(), ParentPid = 4, Image = $"tool{i % 7}.exe",
            ExitCode = kind == LifecycleKind.Exit ? i % 3 : null, Detail = kind == LifecycleKind.Rollup ? $"bash.exe (500) started {i}, git.exe 2" : $"detail {i}",
            Owner = kind == LifecycleKind.Rollup ? "Claude Code session" : null, Count = kind == LifecycleKind.Rollup ? i : 0,
        };
    }).ToList();

    /// <summary>A session owner of <paramref name="members"/> short commands, none with a command line.</summary>
    private static OwnerSuspect Session(int k, int members, bool meaningful)
    {
        var owner = new OwnerSuspect { Owner = new Owner(OwnerKind.ClaudeSession, $"s{k}", $"Claude Code session {k}"), Members = [], Identities = [], Meaningful = meaningful };
        for (int i = 0; i < members; i++)
        {
            owner.Members.Add(new Suspect
            {
                Key = new ProcessKey(k * 1000 + i + 1, 1), Image = "bash.exe",
                Direct = new DirectSignal(null, 0, 0, 0, 0, 0, false), Activity = new ActivitySignal("total", double.NaN, 0, double.NaN, 0, 0, false),
                Lifecycle = new LifecycleSignal(false, false, false, []),
            });
            owner.Identities.Add(new ProcessIdentity("bash.exe", null, owner.Owner, false));
        }
        return owner;
    }

    [Fact]
    public void TheRecentNotableEventsAreTheLastTenNotableRowsOfTheWholeList()
    {
        var registry = new ProcessRegistry();
        var events = Events(400);
        var whole = TagViewBuilder.EventRows(events, registry, all: false).Where(e => e.Notable).TakeLast(TagViewBuilder.ShownEvents).Select(e => e.Text);
        var recent = TagViewBuilder.RecentNotable(events, registry);
        Assert.Equal(whole, recent.Select(e => e.Text));
        Assert.Equal(TagViewBuilder.ShownEvents, recent.Count);
        Assert.All(recent, e => Assert.True(e.Notable));
    }

    [Fact]
    public void ARowKeepsTheOwnersItsDetailsPrintWithTheirFirstProcesses()
    {
        var owners = Enumerable.Range(0, 8).Select(k => Session(k, members: 50, meaningful: k != 1)).ToList();
        var shownNames = owners.Where(o => o.Meaningful).Take(TagViewBuilder.ShownOwners).Select(o => o.Name).ToList();
        var window = new CorrelationWindow(T0, T0.AddMinutes(30), null, T0.AddHours(1));
        var view = TagViewBuilder.View(A, "title", new TagAnswer("grew", owners, 1, 0), owners, Events(400), new ProcessRegistry(), window, traceBacked: false);
        Assert.Equal(shownNames, view.Owners.Select(o => o.Name));
        Assert.Same(view.Owners, view.Answer.Owners); // the answer does not hold the whole ranking either
        Assert.All(view.Owners, o =>
        {
            Assert.InRange(o.Members.Count, 1, OwnerRanking.KeptMembers + 1);
            Assert.Equal(50, o.MemberCount); // what "N more processes" counts
        });
        Assert.Equal(TagViewBuilder.ShownEvents, view.Events.Count);
        Assert.Equal((T0, T0.AddHours(1), T0.AddMinutes(30)), (view.FromUtc, view.ToUtc, view.OnsetUtc));
    }

    private static TagViewData View(PoolTag tag) => new(tag, tag.ToDisplay(), new TagAnswer("grew", [], 1, 0), [Session(1, 3, true)], [], T0, T0, T0);

    [Fact]
    public void AReleasedTagKeepsItsRankingUntilTheWindowHides()
    {
        var now = T0;
        var rankings = new TagRankings(() => now);
        void Rank(PoolTag[] wanted)
        {
            using var done = new ManualResetEventSlim();
            Assert.NotNull(rankings.Pump(View, wanted, null, done.Set));
            Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
            now += RankPlan.Gap;
        }
        Rank([A, B]);
        Rank([A, B]);
        Assert.Equal(2, rankings.Held().rankings);
        now += RankPlan.Fresh;
        Rank([B]); // A stopped growing: its row says "released", and its cause and Copy summary still read the ranking
        Assert.NotNull(rankings.Get(A));
        Assert.Equal((2, 2, 6, 0), rankings.Held());
        rankings.Clear();
        Assert.Equal((0, 0, 0, 0), rankings.Held());
    }

    [Fact]
    public void ClearDropsEveryRankingAndDiscardsOneStillRunning()
    {
        var rankings = new TagRankings(() => T0);
        using var release = new ManualResetEventSlim();
        using var done = new ManualResetEventSlim();
        Assert.Equal(A, rankings.Pump(t => { release.Wait(TimeSpan.FromSeconds(10)); return View(t); }, [A], null, done.Set));
        rankings.Clear(); // the window hides while A is being ranked
        release.Set();
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, rankings.Held().rankings);
        Assert.False(rankings.Tried(A)); // shown again, the row says "Ranking the suspects…" and is ranked afresh
        Assert.Equal(A, rankings.Pump(View, [A], null, () => { }));
    }
}
