using System.Runtime.CompilerServices;
using PoolWatch.Core;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Incidents;

namespace PoolWatch.Analysis;

/// <summary>
/// The pipeline's capture steps that decide something, as their own units so tests hold them to it (review round 3):
/// which incidents share one trace, what the incidents that did not fit are told (after the governor's decision), and what
/// happens when a trace ends. The pipeline calls these; the governor (Core) decides whether a trace may start.
/// </summary>
internal static class CaptureSteps
{
    /// <summary>One tick's pool-alert trace: the incidents it is for, those that did not fit, and the request for the governor.</summary>
    public sealed record Batch(IReadOnlyList<Incident> Chosen, IReadOnlyList<Incident> Left, CaptureRequest Request);

    /// <summary>The incidents a request is for (a pool alert's context is the list of its incidents).</summary>
    public static IReadOnlyList<Incident> IncidentsOf(object? context) => context switch
    {
        IReadOnlyList<Incident> list => list,
        Incident one => [one],
        _ => [],
    };

    public static List<PoolTag> TagsOf(IEnumerable<Incident> incs, int maxTags) => incs.Select(i => i.Tag).Distinct().Take(maxTags).ToList();

    /// <summary>
    /// C1 c: the growth incidents opened in one tick share ONE trace, up to <paramref name="maxTags"/> tags, the largest alerts
    /// first; a held-back pool-alert request's incidents that are still open join it while there is room (an incident of a tag
    /// already in the trace always fits). Null when nothing opened.
    /// </summary>
    public static Batch? Plan(IReadOnlyList<(Incident inc, Alert alert)> opened, CaptureRequest? pending, DateTime nowUtc, TimeSpan now, int maxTags)
    {
        if (opened.Count == 0) return null;
        var sorted = opened.OrderByDescending(x => x.alert.Bytes).ToList();
        IEnumerable<Incident> held = pending is { Source: CaptureSource.PoolAlert } p ? IncidentsOf(p.Context).Where(i => i.IsOpen) : [];
        var chosen = new List<Incident>();
        var tags = new HashSet<PoolTag>();
        foreach (var inc in sorted.Select(x => x.inc).Concat(held))
        {
            if (chosen.Contains(inc) || (!tags.Contains(inc.Tag) && tags.Count >= maxTags)) continue;
            tags.Add(inc.Tag);
            chosen.Add(inc);
        }
        var left = sorted.Select(x => x.inc).Where(i => !chosen.Contains(i)).Distinct().ToList();
        var mine = sorted.Where(x => chosen.Contains(x.inc)).ToList();
        string why = chosen.Count == 1 ? $"incident {chosen[0].Id} ({mine[0].alert.Rule})"
            : $"incidents {string.Join(", ", chosen.Select(i => i.Id))} ({string.Join("; ", mine.Select(x => x.alert.Rule).Distinct())})";
        var at = sorted.Max(x => x.alert.TimeUtc);
        return new Batch(chosen, left, new CaptureRequest(CaptureSource.PoolAlert, at == default ? nowUtc : at, now, why) { Context = chosen });
    }

    /// <summary>
    /// Review round 3: what the incidents that did not fit are told, written AFTER the governor decided (it used to be written
    /// before, as if a trace had started): none when no trace was asked for (the settings), else which trace took their place.
    /// </summary>
    public static string? LeftNote(CaptureDecision d, int maxTags) => d.Verdict switch
    {
        CaptureVerdict.Start => $"no capture: the trace that started records {maxTags} other tags that alerted at the same time (at most {maxTags} tags per trace)",
        CaptureVerdict.Skipped => $"no capture: the held-back trace keeps {maxTags} other tags that alerted at the same time (at most {maxTags} tags per trace)",
        _ => null,
    };

    /// <summary>
    /// C1 a: a trace ended (or failed). The governor hears it FIRST, whatever happens next; every error is on each
    /// incident's timeline ("capture failed: ..."); attaching to one incident may fail without stopping the others.
    /// </summary>
    public static void Complete(CaptureGovernor governor, IReadOnlyList<Incident> incs, CaptureResult result,
        Action<Incident, string> note, Action<Incident> apply, Action<string> log)
    {
        governor.Finished();
        foreach (var inc in incs)
        {
            if (result.Error is { } error) note(inc, error.StartsWith("capture failed", StringComparison.Ordinal) ? error : "capture failed: " + error);
            try { apply(inc); }
            catch (Exception ex) { log($"incident {inc.Id}: the capture could not be attached: {ex.Message}"); }
        }
    }
}

/// <summary>
/// Review round 3: the capture notes ("capture skipped: ...", "no capture: ...", "held-back capture dropped: ...") go on an
/// incident's timeline at most once per reason. The reason is the text before its first bracket, so "... per 60 min (the
/// last capture ended 20 min ago)" and "(... 21 min ago)" are one reason. An incident a trace started for may be told again.
/// </summary>
internal sealed class CaptureNotes
{
    private readonly ConditionalWeakTable<Incident, HashSet<string>> _told = new();

    public static string Reason(string text)
    {
        int i = text.IndexOf('(');
        return (i > 0 ? text[..i] : text).Trim();
    }

    /// <summary>True the first time this incident is told this reason (the caller writes the note then).</summary>
    public bool First(Incident inc, string text)
    {
        var set = _told.GetOrCreateValue(inc);
        lock (set) return set.Add(Reason(text));
    }

    /// <summary>A trace started for the incident: what held it back before may be said again later.</summary>
    public void Reset(Incident inc) => _told.Remove(inc);
}
