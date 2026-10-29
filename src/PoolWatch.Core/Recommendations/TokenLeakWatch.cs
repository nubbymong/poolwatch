using PoolWatch.Core.Knowledge;
using PoolWatch.Core.Setup;

namespace PoolWatch.Core.Recommendations;

/// <summary>
/// PoolWatch keeps watching for the Windows token leak after setup found none (or could not tell): it checks again by
/// itself when Windows was updated, and it looks for the leak's evidence all the time. Once the leak shows, the Simple view
/// offers the guard (<see cref="RecommendationRules.Current"/>); nothing here ever turns the guard on. Pure.
/// </summary>
public static class TokenLeakWatch
{
    /// <summary>The knowledge pack about the leak (knowledge/windows-token-leak.json).</summary>
    public const string IssueId = "windows-token-leak";

    /// <summary>
    /// Whether PoolWatch runs its check-only test now, by itself: the guard is off, nothing was checked in this session,
    /// the facts say this Windows build may leak and the check can run (<see cref="TokenLeakStatus.NeedsTest"/>), and no
    /// verdict was saved for this build: Windows was updated since the last check, or no check ever gave one. A build
    /// tried already in this session (<paramref name="tried"/>) is not tried again, so a check that could not run is not
    /// repeated every minute. The test changes nothing on the PC: it starts a hidden helper that counts its own token.
    /// </summary>
    public static bool ShouldCheck(RecommendationInputs i, IReadOnlyCollection<string> tried) =>
        !i.TokenGuardOn && i.LeakTestNow is null && i.Facts is { TokenLeak: TokenLeakStatus.NeedsTest } f
        && i.SavedVerdictBuild != f.Build && !tried.Contains(f.Build);

    /// <summary>
    /// The leak's own known issue (<see cref="IssueId"/>) matches the evidence, as Pool causes names it
    /// (<see cref="KnownIssueMatcher.Match"/>, the one evaluator of a pack's rules): every condition of one of its rules holds
    /// (its tags grew in the pool it names, at its rate, over the last hour measured across PoolWatch's own clears, on a
    /// Windows build in its range), and the Windows setting that stops the leak is not on already (then what grows has
    /// another cause, and the guard could not help). False without the pack, the build or the measurement: PoolWatch claims
    /// nothing it did not measure.
    /// </summary>
    public static bool Seen(KnowledgeBase knowledge, KnowledgeEvidence evidence) =>
        KnownIssueMatcher.Match(knowledge, evidence).Any(m => m.Issue.Id == IssueId);
}
