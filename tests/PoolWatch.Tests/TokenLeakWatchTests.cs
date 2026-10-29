using System.Text.RegularExpressions;
using PoolWatch.Core;
using PoolWatch.Core.Knowledge;
using PoolWatch.Core.Recommendations;
using PoolWatch.Core.Setup;
using PoolWatch.Core.TokenGuard;

namespace PoolWatch.Tests;

/// <summary>
/// After setup found no token leak (or could not tell), PoolWatch keeps watching: it checks again by itself once Windows
/// was updated, and it reads the token-leak knowledge pack's own evidence; once the leak shows, the guard is offered, never
/// turned on. And the Simple view's feature cards, in the agreed words.
/// </summary>
public class TokenLeakWatchTests
{
    private const long MB = 1L << 20;
    private static readonly DateTime Now = new(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc);
    private static readonly PoolTag Toke = PoolTag.Parse("Toke");

    private static RecommendationInputs Inputs(string build = "26200.7000", string? savedFor = "26100.4000", TokenLeakStatus status = TokenLeakStatus.NeedsTest) => new()
    {
        NowUtc = Now, SavedVerdictBuild = savedFor, SavedVerdict = GuardResult.CleanOnce,
        Facts = new EnvironmentFacts { ScannedUtc = Now, WindowsName = "Windows 11 Pro", Build = build, Architecture = "x64", TokenLeak = status },
    };

    [Fact]
    public void ItChecksAgainOnlyWhenWindowsWasUpdatedSinceTheLastVerdict()
    {
        Assert.True(TokenLeakWatch.ShouldCheck(Inputs(), []));                               // updated since the clean check
        Assert.True(TokenLeakWatch.ShouldCheck(Inputs(savedFor: null), []));                 // no check ever gave a verdict
        Assert.False(TokenLeakWatch.ShouldCheck(Inputs(savedFor: "26200.7000"), []));        // a verdict (even unclear) for this build
        Assert.False(TokenLeakWatch.ShouldCheck(Inputs(), ["26200.7000"]));                  // tried in this session already
        Assert.True(TokenLeakWatch.ShouldCheck(Inputs(), ["26100.4000"]));                   // tried for another build
        Assert.False(TokenLeakWatch.ShouldCheck(Inputs() with { TokenGuardOn = true }, [])); // the guard already works
        Assert.False(TokenLeakWatch.ShouldCheck(Inputs() with { LeakTestNow = LeakVerdict.Clean }, []));
        Assert.False(TokenLeakWatch.ShouldCheck(Inputs() with { Facts = null }, []));
        // The check cannot run (the setting is 0 already, or not read), or this Windows is not one that leaks.
        foreach (var status in new[] { TokenLeakStatus.NotAffected, TokenLeakStatus.WorkedAroundAlready, TokenLeakStatus.Unknown, TokenLeakStatus.Clean, TokenLeakStatus.Leaks })
            Assert.False(TokenLeakWatch.ShouldCheck(Inputs(status: status), []), status.ToString());
    }

    /// <summary>The known issues' evidence as Pool causes gathers it: Toke's rise in each pool over the last <paramref name="minutes"/>, on a build.</summary>
    private static KnowledgeEvidence Evidence(long pagedMb, long nonPagedMb = 0, double minutes = 60, string? build = "26200.7000") => new()
    {
        NowUtc = Now, WindowsBuild = build, GrowthOver = TimeSpan.FromMinutes(minutes),
        Growth = new Dictionary<PoolTag, TagGrowth> { [Toke] = new TagGrowth(pagedMb * MB, nonPagedMb * MB) },
    };

    [Fact]
    public void TheLeakIsSeenWhereItsKnownIssueMatchesAsPoolCausesNamesIt()
    {
        var knowledge = KnowledgeBase.Embedded();
        var rule = knowledge.Entries.Single(e => e.Id == TokenLeakWatch.IssueId).Evidence.Single(r => r.TagGrowth is not null).TagGrowth!;
        // The pack's own numbers: the watch holds none of its own (the matcher reads them, for Pool causes as for the offer).
        Assert.Equal((20, PoolKind.Paged), (rule.MinMBPerHour, rule.Pool));
        Assert.True(TokenLeakWatch.Seen(knowledge, Evidence(25)));
        Assert.True(TokenLeakWatch.Seen(knowledge, Evidence(10, minutes: 20, build: "26100.1"))); // 30 MB an hour over 20 minutes
        Assert.False(TokenLeakWatch.Seen(knowledge, Evidence(15)));                              // too slow
        Assert.False(TokenLeakWatch.Seen(knowledge, Evidence(25, build: "22631.4000")));         // a build the pack does not name
        Assert.False(TokenLeakWatch.Seen(knowledge, Evidence(25, build: null)));
        Assert.False(TokenLeakWatch.Seen(knowledge, Evidence(5, minutes: 5)));                   // too short to tell
        Assert.False(TokenLeakWatch.Seen(knowledge, Evidence(0, nonPagedMb: 25)));               // in the pool the pack does not name
        Assert.False(TokenLeakWatch.Seen(knowledge, new KnowledgeEvidence { NowUtc = Now, WindowsBuild = "26200.7000", GrowthOver = TimeSpan.FromHours(1) }));
        // The setting that stops the leak is on already (the guard's or anyone's): what grows has another cause, and the
        // guard could not help, so it is not offered for it.
        Assert.False(TokenLeakWatch.Seen(knowledge, Evidence(25) with { ActionsOn = ["token-guard"] }));
        Assert.True(TokenLeakWatch.Seen(knowledge, Evidence(25) with { ActionsOn = ["auto-clear"] })); // the hourly clear does not stop it
        // Seen, the guard is offered, and only offered.
        var offer = RecommendationRules.Current(Inputs() with { TokenLeakSeen = true, AutoClearOn = true }).Single();
        Assert.Equal((RecommendationId.TokenLeakGuard, RecommendationAction.Apply, "Turn on"), (offer.Id, offer.Action, offer.Button));
    }

    // ------------------------------------------------------------------ the feature cards

    [Fact]
    public void TheFeatureCardsAreTheDesignsAndHideForGoodOneByOne()
    {
        Assert.Equal(
        [
            ("See what grows the pool", "Pool causes names the driver or app behind each rise.", "Open Pool causes"),
            ("Trim background services", "Decide with your AI agent which ones this PC needs.", "Open Services"),
            ("Mark a slow moment", "When the PC feels slow, Mark in the tray menu saves what was happening.", "Show me"),
        ], Discovery.All.Select(c => (c.Title, c.Text, c.Link)));
        Assert.All(Discovery.All, c => Assert.StartsWith(DiscoveryCard.KeyPrefix, c.Key));
        Assert.Equal(Discovery.All.Count, Discovery.All.Select(c => c.Key).Distinct().Count());
        Assert.Equal(Discovery.All, Discovery.Shown(new HashSet<string>()));
        Assert.Equal([DiscoveryId.PoolCauses, DiscoveryId.Mark], Discovery.Shown(new HashSet<string> { "feature:Services", "Services" }).Select(c => c.Id));
        // No feature card's key is an offer's: hiding one never hides the other.
        Assert.DoesNotContain(Discovery.All, c => Enum.GetNames<RecommendationId>().Contains(c.Key));
    }

    [Fact]
    public void ShowMeSaysWhereMarkIsInPlainWords()
    {
        string withKeys = Discovery.MarkHowTo("Ctrl+Alt+M"), without = Discovery.MarkHowTo(null);
        Assert.StartsWith("When the PC feels slow, right-click PoolWatch's icon next to the clock and choose Mark, or press Ctrl+Alt+M anywhere.", withKeys);
        Assert.DoesNotContain("press", without);
        Assert.DoesNotContain("!", withKeys);
        Assert.DoesNotMatch(new Regex(@"\b[a-z]+[A-Z][A-Za-z]*\b"), withKeys); // no setting names
    }
}
