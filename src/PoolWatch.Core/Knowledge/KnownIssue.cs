using System.Text.Json.Serialization;
using PoolWatch.Core.Recommendations;

namespace PoolWatch.Core.Knowledge;

/// <summary>
/// A known issue from a knowledge pack (knowledge/*.json, described in docs/knowledge-packs.md): what it is, the evidence
/// that shows it on a PC, what helps, where to read more and who found it. A pack holds plain text, pool tags, program
/// names, numbers and web links only, never a command or a path; what PoolWatch can do about it is one of the built-in
/// <see cref="KnownActions"/>, named by id.
/// </summary>
public sealed record KnownIssue
{
    /// <summary>Lower-case words joined by hyphens ("refs-standby-metadata"); the pack's file name too.</summary>
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("title")] public required string Title { get; init; }
    /// <summary>What is going on, in plain words.</summary>
    [JsonPropertyName("explanation")] public required string Explanation { get; init; }
    /// <summary>What helps, in plain words; says so plainly when PoolWatch cannot fix it.</summary>
    [JsonPropertyName("workaround")] public required string Workaround { get; init; }
    /// <summary>The ways the issue shows: it applies when any one of them holds.</summary>
    [JsonPropertyName("evidence")] public required IReadOnlyList<EvidenceRule> Evidence { get; init; }
    /// <summary>The built-in actions that work around it (<see cref="KnownActions"/> ids); empty when PoolWatch cannot help.</summary>
    [JsonPropertyName("actions")] public IReadOnlyList<string> Actions { get; init; } = [];
    /// <summary>With an action: the sentence after the evidence on that action's suggestion card.</summary>
    [JsonPropertyName("suggestion")] public string? Suggestion { get; init; }
    [JsonPropertyName("links")] public required IReadOnlyList<KnownIssueLink> Links { get; init; }
    [JsonPropertyName("credits")] public IReadOnlyList<KnownIssueCredit> Credits { get; init; } = [];
}

/// <summary>
/// One way an issue shows: every condition given must hold. Each rule needs a measurement (how fast pool tags grew, or
/// what a cache clear freed); the Windows build, the file systems and the running programs only narrow it down, so an
/// issue is never named on a PC where PoolWatch measured nothing.
/// </summary>
public sealed record EvidenceRule
{
    [JsonPropertyName("tagGrowth")] public TagGrowthRule? TagGrowth { get; init; }
    [JsonPropertyName("clearFreed")] public ClearFreedRule? ClearFreed { get; init; }
    /// <summary>Any one of these programs is running.</summary>
    [JsonPropertyName("running")] public IReadOnlyList<ProcessRule>? Running { get; init; }
    [JsonPropertyName("windowsBuild")] public BuildRange? WindowsBuild { get; init; }
    /// <summary>A fixed drive has any one of these file systems ("ReFS").</summary>
    [JsonPropertyName("fileSystems")] public IReadOnlyList<string>? FileSystems { get; init; }
}

/// <summary>Which pool a growth rule counts.</summary>
public enum PoolKind { Any, Paged, NonPaged }

/// <summary>
/// Pool tags that together grew at least this fast. <see cref="Name"/> says what they hold in plain words, written as in
/// the middle of a sentence ("security tokens"). 1 MB is 1,048,576 bytes, as PoolWatch shows sizes.
/// </summary>
public sealed record TagGrowthRule
{
    [JsonPropertyName("tags")] public required IReadOnlyList<string> Tags { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("pool")] public PoolKind Pool { get; init; } = PoolKind.Any;
    [JsonPropertyName("minMBPerHour")] public required double MinMBPerHour { get; init; }
}

/// <summary>A cache clear in the last day freed at least this much of these pool tags together.</summary>
public sealed record ClearFreedRule
{
    [JsonPropertyName("tags")] public required IReadOnlyList<string> Tags { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("minMB")] public required double MinMB { get; init; }
}

/// <summary>
/// A running program, by its file name ("ChatGPT.exe") or by the start of its Microsoft Store package's name
/// ("OpenAI.Codex"): exactly one of the two. <see cref="Name"/> is what people call it ("Codex desktop").
/// </summary>
public sealed record ProcessRule
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("image")] public string? Image { get; init; }
    [JsonPropertyName("storePackage")] public string? StorePackage { get; init; }
}

/// <summary>Windows build numbers, both ends included (26100 is Windows 11 24H2); either end may be left open.</summary>
public sealed record BuildRange
{
    [JsonPropertyName("min")] public int? Min { get; init; }
    [JsonPropertyName("max")] public int? Max { get; init; }
}

/// <summary>A page to read more on: a public report, a write-up, Microsoft's documentation.</summary>
public sealed record KnownIssueLink
{
    [JsonPropertyName("title")] public required string Title { get; init; }
    [JsonPropertyName("url")] public required string Url { get; init; }
}

/// <summary>Who found or documented it: <see cref="What"/> reads on from the name ("documented the leak and its cause").</summary>
public sealed record KnownIssueCredit
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("what")] public required string What { get; init; }
    [JsonPropertyName("url")] public string? Url { get; init; }
}

/// <summary>
/// A built-in action a known issue can name as what helps: the suggestion whose button runs it, what it does in plain
/// words (read on from "PoolWatch can"), what the note says once it is in effect (read on from "PoolWatch already"),
/// and a sentence its suggestion always carries, if any. An action with no <see cref="Done"/> words stops its issue.
/// </summary>
public sealed record KnownAction(string Id, RecommendationId Suggestion, string Does, string? Done, string? Notice = null)
{
    /// <summary>With it in effect the issue cannot happen, so growth of the issue's tags has another cause: it is not named.</summary>
    public bool Stops => Done is null;
}

/// <summary>
/// The actions a pack may name, by id. What an action does, what it is called, and whether and when it is offered all
/// stay in PoolWatch's code (the recommendation rules): a pack can explain a suggestion, never cause one.
/// </summary>
public static class KnownActions
{
    public static IReadOnlyList<KnownAction> All { get; } =
    [
        // The hourly clear frees what has built up; it does not stop the build-up, so the issue is still named.
        new("auto-clear", RecommendationId.HourlyCacheClear, "clear the cache every hour and show what each clear freed",
            "clears the cache every hour and shows what each clear freed"),
        // The guard stops the leak. Its trade-off, as the rules' own suggestion words it, goes wherever the guard is offered.
        new("token-guard", RecommendationId.TokenLeakGuard, "turn on its token-leak guard", Done: null,
            Notice: "With it on, any app may bring its window to the front."),
        new("chatgpt-shortcut", RecommendationId.ChatGptShortcut, "create a desktop shortcut that starts the app without its GPU shader disk cache",
            "made a desktop shortcut that starts the app without its GPU shader disk cache; use it instead of the usual one"),
    ];

    public static KnownAction? Find(string id) => All.FirstOrDefault(a => a.Id == id);

    /// <summary>
    /// The ids of the actions already in effect on this PC: the hourly clear is on; the Windows setting that stops the
    /// token leak is on now (a foreground lock timeout of 0), whether the guard or something else set it; the shortcut is
    /// made. The guard's own setting does not count: it can be on while Windows is not protected (Windows refused the
    /// change, or the guard leaves Windows alone on a build that tested clean twice).
    /// </summary>
    /// <param name="liveForegroundLockTimeout">Windows' foreground lock timeout as read now; null when it could not be read.</param>
    public static IReadOnlyList<string> InEffect(RecommendationInputs i, uint? liveForegroundLockTimeout)
    {
        var on = new List<string>();
        if (i.AutoClearOn) on.Add("auto-clear");
        if (liveForegroundLockTimeout == 0) on.Add("token-guard");
        if (i.ChatGptShortcutMade) on.Add("chatgpt-shortcut");
        return on;
    }
}
