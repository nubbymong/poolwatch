using System.Globalization;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Recommendations;
using PoolWatch.Core.Setup;

namespace PoolWatch.Core.Knowledge;

/// <summary>
/// A known issue the evidence on this PC matches: what PoolWatch measured and saw, in plain sentences (measurements first),
/// the pool tags those are about, so the note can be shown next to a tag or an incident of one of them, and which of the
/// issue's actions are already in effect.
/// </summary>
public sealed record KnownIssueMatch(KnownIssue Issue, IReadOnlyList<string> Seen, IReadOnlyList<PoolTag> Tags, IReadOnlyList<string> ActionsOn)
{
    /// <summary>
    /// The Pool causes note, one line per part: the issue, what PoolWatch sees, what helps, what PoolWatch already does or
    /// can do about it, where to read more, who found it. <paramref name="suggestions"/> are the suggestions PoolWatch shows
    /// now (<see cref="RecommendationRules.Current"/>): the note points at an action's suggestion only while it is offered,
    /// and says nothing of an action that is neither in effect nor offered.
    /// </summary>
    public string Note(IReadOnlyList<Recommendation> suggestions)
    {
        var lines = new List<string>
        {
            "Known issue: " + Issue.Title,
            "What PoolWatch sees: " + string.Join(" ", Seen),
            "What is going on: " + Issue.Explanation,
            "What helps: " + Issue.Workaround,
        };
        foreach (var action in Issue.Actions.Select(a => KnownActions.Find(a)!))
            if (ActionsOn.Contains(action.Id)) lines.Add($"PoolWatch already {action.Done}.");
            else if (suggestions.FirstOrDefault(s => s.Id == action.Suggestion && s.State == RecommendationState.Offer) is { } offer)
                lines.Add($"PoolWatch can {action.Does}: see the suggestion \"{offer.Title}\" in Simple view.");
        lines.AddRange(Issue.Links.Select(l => $"Read more: {l.Title}: {l.Url}"));
        lines.AddRange(Issue.Credits.Select(c => $"Credit: {c.Name} {c.What}" + (c.Url is { } url ? $" ({url})." : ".")));
        lines.Add("Pool tags: " + string.Join(", ", Tags.Select(t => t.ToDisplay())));
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>Matches the known issues against the evidence, and gives the suggestions they explain their reasons. Pure.</summary>
public static class KnownIssueMatcher
{
    /// <summary>
    /// The issues whose evidence holds on this PC, in the packs' order, each with the first of its rules that holds. An
    /// issue that an action already in effect stops is left out: what grows has another cause.
    /// </summary>
    public static IReadOnlyList<KnownIssueMatch> Match(KnowledgeBase knowledge, KnowledgeEvidence evidence)
    {
        var matches = new List<KnownIssueMatch>();
        foreach (var issue in knowledge.Entries)
        {
            var on = issue.Actions.Where(evidence.ActionsOn.Contains).ToList();
            if (on.Any(a => KnownActions.Find(a)!.Stops)) continue;
            foreach (var rule in issue.Evidence)
                if (Holds(rule, evidence) is { } lines)
                {
                    matches.Add(new KnownIssueMatch(issue, lines, Measured(rule), on));
                    break;
                }
        }
        return matches;
    }

    /// <summary>
    /// The suggestions, where an offer whose button is a matched issue's action gets that issue's reason: what PoolWatch
    /// measured, the issue's sentence, and the action's notice. Which suggestions there are stays with
    /// <see cref="RecommendationRules"/>; a reason too long for a card leaves the rules' own.
    /// </summary>
    public static IReadOnlyList<Recommendation> Explain(IReadOnlyList<Recommendation> suggestions, IReadOnlyList<KnownIssueMatch> matches) =>
        suggestions.Select(r => r.State == RecommendationState.Offer && Reason(r.Id, matches) is { } why ? r with { Why = why } : r).ToList();

    private static string? Reason(RecommendationId suggestion, IReadOnlyList<KnownIssueMatch> matches)
    {
        foreach (var m in matches)
            if (m.Issue.Actions.Select(a => KnownActions.Find(a)!).FirstOrDefault(a => a.Suggestion == suggestion) is { } action)
            {
                // The first sentence is always a measurement (Holds puts them first): what grew, or what a clear freed.
                string why = $"{m.Seen[0]} {m.Issue.Suggestion}" + (action.Notice is { } notice ? " " + notice : "");
                if (why.Length <= RecommendationRules.MaxWhy) return why;
            }
        return null;
    }

    /// <summary>The rule's evidence in plain sentences when every condition in it holds, else null.</summary>
    private static List<string>? Holds(EvidenceRule r, KnowledgeEvidence e)
    {
        var lines = new List<string>();
        if (r.ClearFreed is { } freed)
        {
            // The newest clear that freed enough says how things stand now; the biggest of the day may be long past.
            var tags = freed.Tags.Select(PoolTag.Parse).ToList();
            var clear = e.Clears.Select(c => (c.TimeUtc, Bytes: tags.Sum(t => c.Freed.GetValueOrDefault(t))))
                .Where(c => c.Bytes >= Bytes(freed.MinMB)).OrderByDescending(c => c.TimeUtc).FirstOrDefault();
            if (clear.Bytes == 0) return null; // none did (a rule always asks for more than 0)
            lines.Add($"The cache clear {At(clear.TimeUtc, e.NowUtc)} freed {Fmt.Bytes(clear.Bytes)} of {freed.Name}.");
        }
        if (r.TagGrowth is { } growth)
        {
            if (e.GrowthOver < KnowledgeEvidence.MinGrowthOver) return null;
            long bytes = growth.Tags.Select(PoolTag.Parse).Sum(t => e.Growth.TryGetValue(t, out var g) ? In(g, growth.Pool) : 0);
            if (bytes / e.GrowthOver.TotalHours < Bytes(growth.MinMBPerHour)) return null;
            string span = e.GrowthOver >= TimeSpan.FromMinutes(55) ? "the last hour" : $"the last {e.GrowthOver.TotalMinutes:0} minutes";
            lines.Add($"{Capital(growth.Name)} grew {Fmt.Bytes(bytes)} in {span}.");
        }
        if (r.Running is { } running)
        {
            if (running.FirstOrDefault(p => e.Running.Any(x => Is(p, x))) is not { } seen) return null;
            lines.Add($"{Capital(seen.Name)} is running.");
        }
        if (r.WindowsBuild is { } range)
        {
            if (TokenLeakRule.Major(e.WindowsBuild) is not { } major || major < (range.Min ?? 0) || major > (range.Max ?? int.MaxValue)) return null;
            lines.Add($"This PC runs Windows build {e.WindowsBuild}.");
        }
        if (r.FileSystems is { } fileSystems)
        {
            if (fileSystems.FirstOrDefault(fs => e.FileSystems.Contains(fs, StringComparer.OrdinalIgnoreCase)) is not { } found) return null;
            lines.Add($"This PC has a drive formatted as {found}.");
        }
        return lines;
    }

    private static double Bytes(double mb) => mb * (1 << 20);

    /// <summary>"at 09:12" in local time, or "yesterday at 22:40" for a clear before today (the clears counted are a day old at most).</summary>
    private static string At(DateTime utc, DateTime nowUtc)
    {
        var local = utc.ToLocalTime();
        return (local.Date == nowUtc.ToLocalTime().Date ? "at " : "yesterday at ") + local.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private static long In(TagGrowth g, PoolKind pool) => pool switch
    {
        PoolKind.Paged => g.PagedBytes,
        PoolKind.NonPaged => g.NonPagedBytes,
        _ => g.PagedBytes + g.NonPagedBytes,
    };

    private static bool Is(ProcessRule rule, RunningProcess p) => rule.Image is { } image
        ? p.Image.Equals(image, StringComparison.OrdinalIgnoreCase)
        : p.StorePackage?.StartsWith(rule.StorePackage!, StringComparison.OrdinalIgnoreCase) == true;

    private static IReadOnlyList<PoolTag> Measured(EvidenceRule r) =>
        (r.ClearFreed?.Tags ?? []).Concat(r.TagGrowth?.Tags ?? []).Select(PoolTag.Parse).Distinct().ToList();

    private static string Capital(string s) => char.ToUpperInvariant(s[0]) + s[1..];
}
