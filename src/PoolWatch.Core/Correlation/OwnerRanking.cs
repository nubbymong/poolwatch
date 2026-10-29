using System.Globalization;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Identity;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;

namespace PoolWatch.Core.Correlation;

/// <summary>
/// Suspects grouped by who they work for (an app, a service, a Claude Code / AICC session), with the evidence in plain
/// words. The 40 short commands one Claude session ran are one suspect, with the breakdown in <see cref="Members"/>.
/// </summary>
public sealed class OwnerSuspect
{
    public required Owner Owner { get; init; }
    public required List<Suspect> Members { get; init; }
    public required List<ProcessIdentity> Identities { get; init; }
    public double Score { get; set; }
    public Confidence Confidence { get; set; }
    /// <summary>Bytes of the tag's growth this owner's evidence accounts for (direct charge, quota, trace).</summary>
    public long ExplainedBytes { get; set; }
    public double IoShare { get; set; }
    public long Ops { get; set; }
    /// <summary>Spearman of the tag's growth against the owner's summed I/O (NaN when not computable).</summary>
    public double Correlation { get; set; } = double.NaN;
    /// <summary>False when nothing in the evidence is above noise (hidden by default).</summary>
    public bool Meaningful { get; set; }
    public int Rank { get; set; }
    /// <summary>Plain-language evidence lines ("file activity: 3.5 M operations (25% of all)").</summary>
    public List<string> Plain { get; } = [];
    /// <summary>The confirmation trace's figures for this owner, when a trace exists (2a): the ranking comes from them.</summary>
    public Incidents.TraceOwner? Trace { get; set; }
    /// <summary>Ranked below the trace's owners on correlation alone (2a).</summary>
    public bool CorrelationOnly { get; set; }

    public string Name => Owner.Name;

    private int? _memberCount;
    private string? _summary;

    /// <summary>How many processes this owner groups (after <see cref="Trim"/>, <see cref="Members"/> holds only the first of them).</summary>
    public int MemberCount => _memberCount ?? Members.Count;

    /// <summary>
    /// Keeps the first <paramref name="members"/> members and their identities, plus the first member with a command line
    /// when none of those has one (the details show it). <see cref="MemberCount"/> and <see cref="Summary"/> still describe all.
    /// </summary>
    public void Trim(int members)
    {
        _summary = Summary;
        _memberCount = MemberCount;
        if (Members.Count <= members) return;
        int withCommandLine = Members.FindIndex(m => m.CommandLine is not null);
        if (withCommandLine >= members)
        {
            Members[members] = Members[withCommandLine];
            Identities[members] = Identities[withCommandLine];
            members++;
        }
        Members.RemoveRange(members, Members.Count - members);
        Identities.RemoveRange(members, Identities.Count - members);
        Members.TrimExcess();
        Identities.TrimExcess();
    }

    /// <summary>"Codex (2 processes: node.exe · Codex cua_node, codex.exe)"; an owner only the trace saw: "... 412 traced processes (...)".</summary>
    public string Summary
    {
        get
        {
            if (_summary is not null) return _summary;
            if (Members.Count == 0)
            {
                if (Trace is not { } t) return Name;
                var images = t.Processes.GroupBy(p => p.Identity.Image).OrderByDescending(g => g.Count()).Take(3).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key);
                return $"{Name}: {t.Processes.Count} traced process{(t.Processes.Count == 1 ? "" : "es")} ({string.Join(", ", images)})";
            }
            var parts = Identities.GroupBy(i => i.Display).OrderByDescending(g => g.Count()).Take(3).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key).ToList();
            return Members.Count == 1 ? $"{Name}: {parts[0]}" : $"{Name}: {Members.Count} processes ({string.Join(", ", parts)}{(Identities.Select(i => i.Display).Distinct().Count() > 3 ? ", …" : "")})";
        }
    }
}

/// <summary>The one-line answer for a tag plus the owners behind it.</summary>
public sealed record TagAnswer(string Line, IReadOnlyList<OwnerSuspect> Owners, long Growth, long ExplainedBytes);

public static class OwnerRanking
{
    /// <summary>Groups ranked suspects by owner, recomputes the activity correlation on the owner's summed I/O, ranks owners.</summary>
    /// <param name="identity">The identity (and owner) of a suspect's process; from its ancestry.</param>
    public static List<OwnerSuspect> Rank(History.History h, IReadOnlyList<Suspect> suspects, PoolTag tag, CorrelationWindow w, long growth,
        Func<Suspect, ProcessIdentity> identity)
    {
        int from = h.IndexAtOrAfter(w.FromUtc);
        int to = Math.Min(h.Count - 1, Math.Max(from, h.IndexAtOrAfter(w.ToUtc.AddTicks(1)) - 1));
        var series = to - from >= 2 ? h.TagSeries(tag, from) : [];
        int n = Math.Min(series.Length, to - from + 1);
        var tagDeltas = new double[Math.Max(0, n - 1)];
        for (int i = 0; i < tagDeltas.Length; i++) tagDeltas[i] = series[i + 1] - series[i];

        var groups = new Dictionary<string, OwnerSuspect>();
        foreach (var s in suspects)
        {
            var id = identity(s);
            if (!groups.TryGetValue(id.Owner.Key, out var g))
                groups[id.Owner.Key] = g = new OwnerSuspect { Owner = id.Owner, Members = [], Identities = [] };
            g.Members.Add(s);
            g.Identities.Add(id);
        }
        foreach (var g in groups.Values)
        {
            g.IoShare = g.Members.Sum(m => m.Activity.IoShare);
            g.Ops = g.Members.Sum(m => Math.Max(0, m.Activity.OpsDelta));
            g.ExplainedBytes = g.Members.Sum(m => Explained(m, growth));
            g.Confidence = g.Members.Max(m => m.Confidence);
            g.Score = g.Members.Max(m => m.Score) + 0.5 * g.Members.Skip(1).Sum(m => m.Score) / Math.Max(1, g.Members.Count);
            if (tagDeltas.Length >= 4 && g.Members.Count > 1)
            {
                var ops = new double[tagDeltas.Length];
                foreach (var m in g.Members)
                {
                    var pts = h.Points(m.Key);
                    for (int i = Math.Max(0, pts.First - from); i < ops.Length && from + i + 1 <= pts.Last; i++)
                        ops[i] += Math.Max(0, pts[from + i + 1].TotalOps - pts[from + i].TotalOps);
                }
                g.Correlation = Stats.Spearman(tagDeltas, ops);
                if (g.Confidence < Confidence.Correlated && g.Correlation >= Correlator.MinSpearman && g.IoShare >= Correlator.MinIoShare)
                {
                    g.Confidence = Confidence.Correlated;
                    g.Score += 1;
                }
            }
            else g.Correlation = g.Members.Select(m => m.Activity.Best).Where(x => !double.IsNaN(x)).DefaultIfEmpty(double.NaN).Max();
            g.Meaningful = g.Members.Any(m => Meaningful(m, growth)) || g.IoShare >= 0.02 || g.Confidence >= Confidence.Correlated;
            Describe(g, tag, growth);
        }
        var ranked = groups.Values.OrderByDescending(g => g.Meaningful).ThenByDescending(g => g.Score).ThenByDescending(g => g.IoShare).ToList();
        for (int i = 0; i < ranked.Count; i++) ranked[i].Rank = i + 1;
        return ranked;
    }

    /// <summary>Members kept per owner once a ranking is final: the most any report or page lists (20).</summary>
    public const int KeptMembers = 20;

    /// <summary>
    /// What a finished ranking keeps (it was built from up to 2,000 suspects): its meaningful owners in order, at most
    /// <paramref name="maxOwners"/>, each trimmed to <see cref="KeptMembers"/> members. Every reader looks only at meaningful
    /// owners and the answer line sums all of them, so an incident keeps them all and a Pool causes row the few it shows.
    /// </summary>
    public static List<OwnerSuspect> Keep(IEnumerable<OwnerSuspect> ranked, int maxOwners = int.MaxValue)
    {
        var kept = ranked.Where(o => o.Meaningful).Take(maxOwners).ToList();
        foreach (var o in kept) o.Trim(KeptMembers);
        return kept;
    }

    /// <summary>Bytes a suspect's own evidence accounts for: the larger of its handle-share estimate, its quota rise and its traced net.</summary>
    public static long Explained(Suspect s, long growth)
    {
        // Handles: a share of the growth, but never more than the handles could hold (4 KB each is generous for one object)
        // and only for a real rise (+1 of +2 handles is not half of a gigabyte).
        long handles = growth > 0 && s.Direct.HandleType is not null && s.Direct.HandleDelta >= Correlator.MinHandleDelta
            ? Math.Min((long)(growth * s.Direct.HandleShare), s.Direct.HandleDelta * 4096L) : 0;
        // Quota is charged for all of a process's pool, not one tag: it counts only when large against the rise (the
        // correlator's own direct-quota rule).
        long quota = growth > 0 && s.Direct.QuotaDelta >= Math.Max(1L << 20, growth / 10) && s.Direct.QuotaShare >= 0.5 ? s.Direct.QuotaDelta : 0;
        return Math.Max(Math.Max(handles, quota), Math.Max(0, s.TraceNetBytes));
    }

    private static bool Meaningful(Suspect s, long growth) =>
        s.Direct.Present || s.Activity.Present || s.Lifecycle.Present || s.Activity.IoShare >= 0.01 || s.TraceNetBytes > 0 ||
        Explained(s, growth) >= Math.Max(64 * 1024, growth / 100);

    public static string Strength(Confidence c) => c switch
    {
        Confidence.ConfirmedByTrace => "confirmed by trace",
        Confidence.StronglyCorrelated => "strong",
        Confidence.Correlated => "some",
        _ => "weak",
    };

    /// <summary>"strongly" / "somewhat" / "not" for a correlation.</summary>
    public static string Moves(double r) => double.IsNaN(r) ? "unknown" : r >= 0.6 ? "strongly" : r >= 0.3 ? "somewhat" : "not";

    private static void Describe(OwnerSuspect g, PoolTag tag, long growth)
    {
        var p = g.Plain;
        var handleMembers = g.Members.Where(m => m.Direct.HandleType is not null && m.Direct.HandleDelta > 0).ToList();
        if (handleMembers.Count > 0)
        {
            int delta = handleMembers.Sum(m => m.Direct.HandleDelta);
            string type = handleMembers[0].Direct.HandleType!;
            double share = handleMembers.Sum(m => m.Direct.HandleShare);
            long est = growth > 0 ? (long)(growth * share) : 0;
            p.Add($"holds {delta:N0} more {type} handles ({share:P0} of the {type}-handle growth{(est > 0 ? $", about {Fmt.Bytes(est)} of the rise" : "")})");
        }
        long quota = g.Members.Sum(m => m.Direct.QuotaDelta);
        if (Math.Abs(quota) >= 16 * 1024) p.Add($"pool memory charged to it: {Fmt.SignedBytes(quota)}");
        long trace = g.Members.Sum(m => Math.Max(0, m.TraceNetBytes));
        if (trace > 0) p.Add($"the confirmation trace saw it allocate {Fmt.Bytes(trace)} of {tag.ToDisplay()}");
        if (g.Ops > 0) p.Add($"file and other I/O: {Count(g.Ops)} operations ({g.IoShare:P0} of all)");
        if (!double.IsNaN(g.Correlation)) p.Add($"rises and falls with the tag: {Moves(g.Correlation)}");
        foreach (var m in g.Members.Where(m => m.Lifecycle.StartedBeforeOnset).Take(1)) p.Add($"{m.Image} started just before the growth began ({Fmt.Clock(m.StartUtc)})");
        foreach (var m in g.Members.Where(m => m.Lifecycle.ExitedAtEnd).Take(1)) p.Add($"{m.Image} exited when the growth stopped");
        foreach (var m in g.Members.Where(m => m.Lifecycle.OrphanActive).Take(1)) p.Add($"{m.Image} kept running and working after its parent exited");
        if (p.Count == 0) p.Add("no meaningful activity in the window");
    }

    /// <summary>"3.5 M", "120 k", "860".</summary>
    public static string Count(long n) => n >= 1_000_000 ? (n / 1e6).ToString("0.#", CultureInfo.InvariantCulture) + " M"
        : n >= 10_000 ? (n / 1e3).ToString("0", CultureInfo.InvariantCulture) + " k" : n.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>
    /// "File objects (non-paged) grew 1.2 GB in the last hour. Most likely cause: Codex (strong). Next: Stream Deck (some)."
    /// or "No clear cause yet: the suspects below account for 0.6 MB of a 300 MB rise."
    /// </summary>
    public static TagAnswer Answer(TagInfo info, PoolTag tag, long pagedBytes, long nonPagedBytes, long growth, string window, IReadOnlyList<OwnerSuspect> owners)
    {
        string kind = nonPagedBytes > pagedBytes ? "non-paged" : "paged";
        string what = info.Description.Length > 0 && info.Description.Length <= 60 ? $"{info.Description} ({tag.ToDisplay()}, {kind})" : $"{tag.ToDisplay()} ({info.Owner}, {kind})";
        long explained = owners.Where(o => o.Meaningful).Sum(o => o.ExplainedBytes);
        string head = growth > 0 ? $"{what} grew {Fmt.Bytes(growth)} in {window}." : growth < 0 ? $"{what} fell {Fmt.Bytes(-growth)} in {window}." : $"{what} is flat over {window}.";
        var top = owners.Where(o => o.Meaningful).ToList();
        if (growth <= 0) return new TagAnswer(head, owners, growth, explained);
        // A cause is named only when the top owner itself is at least "some", or its own evidence covers a tenth of the rise.
        bool named = top.Count > 0 && (top[0].Confidence >= Confidence.Correlated || top[0].ExplainedBytes >= growth / 10);
        bool explains = explained >= growth / 10 || (top.Count > 0 && top[0].Confidence >= Confidence.StronglyCorrelated);
        if (!named)
            return new TagAnswer($"{head} No clear cause yet: the suspects below account for {Fmt.Bytes(explained)} of a {Fmt.Bytes(growth)} rise.", owners, growth, explained);
        string line = $"{head} Most likely cause: {top[0].Name} ({Strength(top[0].Confidence)}).";
        if (top.Count > 1 && top[1].Confidence >= Confidence.Correlated) line += $" Next: {top[1].Name} ({Strength(top[1].Confidence)}).";
        if (!explains) line += $" The evidence accounts for {Fmt.Bytes(explained)} of the {Fmt.Bytes(growth)} rise.";
        return new TagAnswer(line, owners, growth, explained);
    }
}
