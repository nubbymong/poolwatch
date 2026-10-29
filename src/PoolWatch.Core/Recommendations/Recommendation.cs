using PoolWatch.Core.Setup;
using PoolWatch.Core.TokenGuard;

namespace PoolWatch.Core.Recommendations;

public enum RecommendationId { StartAtSignIn, HourlyCacheClear, ReplaceClearTask, TokenLeakGuard, CheckTokenLeak, ReviewServices, DefenderDevDrive, ChatGptShortcut, ReportIncident }

/// <summary>What a recommendation's button does (the window that shows it runs it).</summary>
public enum RecommendationAction { Apply, CheckTokenLeak, OpenServices, ShowInstructions, ReportIssue }

/// <summary>
/// Offer: a button to press. Done: already so. Checking: the token-leak check is running. Info: a plain line, no button.
/// The setup guide shows all four; the Simple view shows offers only.
/// </summary>
public enum RecommendationState { Offer, Done, Checking, Info }

/// <summary>
/// One suggestion, in plain words: the title, why (the evidence), and the button. Texts are short: a title of at most 60
/// characters, a reason of at most 240, a button of at most 16, with no setting names in them.
/// </summary>
public sealed record Recommendation(RecommendationId Id, string Title, string Why, string Button, RecommendationAction Action, int Priority)
{
    public RecommendationState State { get; init; } = RecommendationState.Offer;
    /// <summary>The text a ShowInstructions button opens.</summary>
    public string? Instructions { get; init; }
    /// <summary>ReportIncident: the incident's id, which is also its folder name under incidents\.</summary>
    public string? Subject { get; init; }
    /// <summary>What "Hide" dismisses, for <see cref="RecommendationRules.DismissFor"/>: the id, or "id:subject".</summary>
    public string Key => KeyOf(Id, Subject);

    public static string KeyOf(RecommendationId id, string? subject) => subject is null ? id.ToString() : $"{id}:{subject}";
}

/// <summary>
/// Everything the rules look at, gathered by the app from memory and the cached facts (nothing is scanned for it), so the
/// rules stay pure.
/// </summary>
public sealed record RecommendationInputs
{
    public required DateTime NowUtc { get; init; }
    public EnvironmentFacts? Facts { get; init; }
    public bool AutoClearOn { get; init; }
    public bool TokenGuardOn { get; init; }
    /// <summary>This session's token-leak check, when one ran.</summary>
    public LeakVerdict? LeakTestNow { get; init; }
    /// <summary>The build the saved verdict is for, and the verdict.</summary>
    public string? SavedVerdictBuild { get; init; }
    public GuardResult? SavedVerdict { get; init; }
    /// <summary>A token-leak finding of Warning or above in the last 24 hours.</summary>
    public bool TokenSpawnLast24h { get; init; }
    /// <summary>
    /// The token leak's known issue matches now (<see cref="TokenLeakWatch.Seen"/>): security tokens grew in the last hour
    /// at least as fast as the pack's rule asks, on a Windows build it names, while the setting that stops the leak is off.
    /// </summary>
    public bool TokenLeakSeen { get; init; }
    /// <summary>A file-system metadata tag (Reff, MSb+, FMfn, ...) grew in the last 24 hours: an incident, or growing now.</summary>
    public bool FileSystemMetadataGrewLast24h { get; init; }
    /// <summary>Microsoft Defender's own process, in cores, averaged over the last 30 minutes.</summary>
    public double? DefenderCoresAvg30Min { get; init; }
    /// <summary>Display names of optional services whose share of their process averaged at least 0.1 core over 30 minutes.</summary>
    public IReadOnlyList<string> CostlyOptionalServices { get; init; } = [];
    /// <summary>A "PC is saturated" finding in the last 24 hours that offered to turn a background service off.</summary>
    public bool SaturationOfferedTurnOffLast24h { get; init; }
    /// <summary>The newest closed incident with a likely cause (some evidence or better), not dismissed: its id, folder and a plain headline.</summary>
    public (string Id, string Dir, string Headline)? ReportableIncident { get; init; }
    /// <summary>ChatGPT desktop was a suspect or the cause of a kernel pool growth PoolWatch still has in memory.</summary>
    public bool ChatGptNamedInGrowth { get; init; }
    public bool ChatGptLauncherInstalled { get; init; }
    public bool ChatGptShortcutMade { get; init; }
    /// <summary>Dismissed keys and when.</summary>
    public IReadOnlyDictionary<string, DateTime> Dismissed { get; init; } = new Dictionary<string, DateTime>();
}
