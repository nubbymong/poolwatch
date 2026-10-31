using System.Text.RegularExpressions;
using PoolWatch.Analysis;
using PoolWatch.Core;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Cache;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Growth;
using PoolWatch.Core.Identity;
using PoolWatch.Core.Impact;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;
using PoolWatch.Core.Util;
using PoolWatch.Engine;

namespace PoolWatch.Ui;

internal enum CauseKind { Incident, Tag, Process, Finding }

internal enum RowTone { Normal, Warning, Alert }

/// <summary>
/// One row of the Pool causes list: what is growing, what it grew, what PoolWatch's own clears freed of it, its net change
/// and its growth per hour (over the window the graph's 1 h / 6 h / 24 h choice says, for a row about a tag the stats
/// count), the likely cause, the confidence in words, its impact (<see cref="ImpactRules.FromGrowth(long)"/>), and when. It merges what were four tabs (Incidents, Growing tags, Processes,
/// Findings); the payload says where its details come from. <paramref name="HowMuch"/> and <paramref name="HowFast"/> are
/// the row's own figures (an incident's growth and rate), shown in Grew and Per hour while it has no window stats.
/// </summary>
internal sealed record CauseRow(string Key, CauseKind Kind, string What, string HowMuch, string HowFast, string Cause, string Confidence, string When)
{
    /// <summary>The four columns the window picker drives: "+4.9 GB (now 912 MB)", "4.8 GB", "+103 MB", "836.3 MB/h".</summary>
    public string Grew { get; init; } = "";
    public string Cleared { get; init; } = "";
    public string Net { get; init; } = "";
    public string PerHour { get; init; } = "";
    /// <summary>The numbers behind the columns, for sorting (null: the cell says no number).</summary>
    public long? GrewBytes { get; init; }
    public long? ClearedBytes { get; init; }
    public long? NetBytes { get; init; }
    public double? PerHourBytes { get; init; }
    /// <summary>The likely cause's confidence, for sorting.</summary>
    public Confidence? Level { get; init; }
    /// <summary>The four columns are the window's (the tag's stats), not the row's own figures.</summary>
    public bool Windowed { get; init; }
    /// <summary>
    /// How much its growth costs this PC (<see cref="ImpactRules.FromGrowth(long)"/> of its growth per hour, PoolWatch's own
    /// clears added back); null where the row has no rate (a release, a clear, a finding) or the rate is not known yet.
    /// </summary>
    public ImpactLevel? Impact { get; init; }

    /// <summary>Happening now (an open incident, a growing or retained tag, a process piling up): listed first.</summary>
    public bool Active { get; init; }
    public DateTime TimeUtc { get; init; }
    public RowTone Tone { get; init; }
    /// <summary>The incident or finding report folder, when there is one.</summary>
    public string? Folder { get; init; }
    public PoolTag? Tag { get; init; }
    public ProcessKey? Process { get; init; }
    public ProcessCandidate? Candidate { get; init; }
    public Finding? Finding { get; init; }

    public string[] Cells => [What, Grew, Cleared, Net, PerHour, Cause, Confidence, ImpactScale.Text(Impact), When];

    /// <summary>The details' title: "File system metadata (Reff, refs.sys): +4.9 GB (now 912 MB)".</summary>
    public string Title
    {
        get
        {
            string much = Windowed ? Grew : HowMuch;
            return much.Length > 0 ? $"{What}: {much}" : What;
        }
    }
}

/// <summary>A live process whose handles or pool charge rose over the last hour, or that became an orphan while PoolWatch watched and is still busy.</summary>
internal sealed record ProcessCandidate(ProcessKey Key, string Name, int Handles, int HandlesBefore, long Quota, long QuotaBefore, TimeSpan Over,
    bool BecameOrphan, long OpsPerSec, DateTime StartUtc);

/// <summary>
/// Builds the Pool causes rows. The mapping functions are pure (unit-tested in PoolWatch.AppTests); <see cref="Build"/>
/// reads the analysis under its lock and only reads the tag rankings, which <see cref="TagRankings"/> builds off the UI
/// thread (<see cref="RankPlan"/>), so the list costs about what the old tabs did.
/// </summary>
internal static class PoolCauses
{
    public static readonly string[] Columns = ["What is growing", "Grew", "Cleared", "Net", "Per hour", "Likely cause", "Confidence", "Impact", "When"];
    /// <summary>The columns the window choice drives (<see cref="WithWindow"/>), and the others by place.</summary>
    public const int GrewColumn = 1, ClearedColumn = 2, NetColumn = 3, PerHourColumn = 4, CauseColumn = 5, ConfidenceColumn = 6, ImpactColumn = 7, WhenColumn = 8;

    /// <summary>A process row needs one of: this many more handles in the hour ...</summary>
    public const int HandleRise = 5_000;
    /// <summary>... this much more pool charged to it in the hour ...</summary>
    public const long QuotaRise = 32L << 20;
    /// <summary>... or, orphaned while PoolWatch watched, at least this much I/O a second (the orphaned crawl of DESIGN.md section 1).</summary>
    public const long OrphanOpsPerSec = 500;
    public const int MaxProcesses = 8, MaxFindings = 30, MaxIncidents = 50;
    public static readonly TimeSpan FindingWindow = TimeSpan.FromHours(24);
    public static readonly TimeSpan IncidentWindow = TimeSpan.FromDays(7);

    /// <summary>Finding kinds that explain pool growth or its release; the rest (lag, marks, versions, ...) stay in findings.jsonl and the bundle.</summary>
    private static readonly HashSet<FindingKind> PoolKinds =
        [FindingKind.SpawnStorm, FindingKind.GitRoot, FindingKind.StrayGit, FindingKind.ChildAccumulation, FindingKind.TokenSpawn, FindingKind.FilterEvent];

    // ---------------------------------------------------------------- pure mapping

    /// <summary>A tag in plain words (<see cref="PlainWords.Tag"/>): "File system metadata (Reff, refs.sys)", "Pool tag Abcd (foo.sys)".</summary>
    public static string TagWords(PoolTag tag, TagInfo? info) => PlainWords.Tag(tag, info);

    /// <summary>The same for a tag read back from a report ("Reff"); a tag that does not parse is shown as it is.</summary>
    public static string TagWords(string tag, TagInfo? info) => PoolTag.TryParse(tag, out var t) ? PlainWords.Tag(t, info) : $"Pool tag {tag}";

    public static CauseRow ForIncident(IncidentRow r, string what, DateTime nowUtc)
    {
        var start = r.OnsetUtc ?? r.OpenedUtc;
        string much = "", fast = "", net = "";
        long? grew = null, change = null;
        double? rate = null;
        switch (r.Kind)
        {
            case IncidentKind.Release:
                long freed = r.Dropped > 0 ? r.Dropped : Math.Abs(r.Growth);
                much = freed > 0 ? $"freed {PlainWords.Amount(freed)}" : "";
                if (freed > 0) { net = GrowthText.SignedAmount(-freed); change = -freed; }
                what = $"{what} was released";
                break;
            default:
                if (r.Growth > 0)
                {
                    var over = (r.Open ? nowUtc : r.EndUtc ?? r.OpenedUtc) - start;
                    much = $"+{PlainWords.Amount(r.Growth)}";
                    fast = PlainWords.PerHour(r.Growth, over);
                    grew = r.Growth;
                    rate = r.Growth / Math.Max(1.0 / 60, over.Duration().TotalHours);
                }
                if (r.Kind == IncidentKind.NonPagedTotal) what += ", with non-paged pool over the limit";
                else if (r.Kind == IncidentKind.Capture) what = "Manual capture: " + what;
                break;
        }
        return new CauseRow("inc:" + r.Dir, CauseKind.Incident, what, much, fast, r.Top, r.Confidence,
            r.Open ? $"since {PlainWords.When(start, nowUtc)}" : PlainWords.When(start, nowUtc))
        {
            Active = r.Open, TimeUtc = r.OpenedUtc, Tone = r.Open ? RowTone.Alert : RowTone.Normal, Folder = r.Dir,
            Tag = PoolTag.TryParse(r.Tag, out var tag) ? tag : null, // for the known issues about it
            Grew = grew is null ? "" : much, PerHour = fast, Net = net, GrewBytes = grew, PerHourBytes = rate, NetBytes = change, Level = r.Level,
            Impact = rate is { } perHour ? ImpactRules.FromGrowth((long)perHour) : null,
        };
    }

    /// <summary>A growing, retained or released tag without an open incident; <paramref name="view"/> is its ranking, when there is one.</summary>
    public static CauseRow ForTag(TagStatus s, string what, TagViewData? view, DateTime nowUtc)
    {
        string much, fast = "", when, net = "";
        long? grewBytes = null, change = null;
        double? rate = null;
        switch (s.State)
        {
            case TagState.Released:
                long fell = -Math.Min(Math.Min(s.Delta10m, s.Delta60m), 0);
                what = $"{what} was released";
                much = fell > 0 ? $"freed {PlainWords.Amount(fell)}" : "";
                if (fell > 0) { net = GrowthText.SignedAmount(-fell); change = -fell; }
                when = s.StateSinceUtc is { } r ? PlainWords.When(r, nowUtc) : "just now";
                break;
            case TagState.Retained:
                much = $"{PlainWords.Amount(s.Bytes)} held";
                fast = "not falling";
                when = s.StateSinceUtc is { } since ? $"since {PlainWords.When(since, nowUtc)}" : "now";
                break;
            default:
                long grew = s.Delta60m > 0 ? s.Delta60m : Math.Max(0, s.Delta10m);
                much = grew > 0 ? $"+{PlainWords.Amount(grew)} (now {PlainWords.Amount(s.Bytes)})" : $"now {PlainWords.Amount(s.Bytes)}";
                fast = s.Delta10m > 0 ? PlainWords.PerHour(s.Delta10m, TimeSpan.FromMinutes(10)) : "";
                grewBytes = grew;
                if (s.Delta10m > 0) rate = s.Delta10m * 6.0;
                when = "now";
                break;
        }
        // The cause comes from the tag's ranking (built in the background, TagRankings): blank until there is one. A release
        // has no cause to name here (its incident report lists what exited then).
        string cause = "", conf = "";
        Confidence? level = null;
        if (s.State != TagState.Released && view is not null)
        {
            if (CauseOf(view) is { } c) { cause = c.name; conf = PlainWords.Confidence(c.level); level = c.level; }
            else cause = "no clear cause yet";
        }
        return new CauseRow("tag:" + s.Tag.Raw.ToString("x8"), CauseKind.Tag, what, much, fast, cause, conf, when)
        {
            Active = s.State != TagState.Released, TimeUtc = s.StateSinceUtc ?? nowUtc,
            Tone = s.State == TagState.Released ? RowTone.Normal : RowTone.Warning, Tag = s.Tag,
            Grew = s.State == TagState.Released ? "" : much, PerHour = fast, Net = net, GrewBytes = grewBytes, PerHourBytes = rate, NetBytes = change, Level = level,
            // From a rate measured (the last 10 minutes' rise). A tag held is not growing, which the agreed rule calls None; one
            // still growing but with no rise in them has no level known yet.
            Impact = rate is { } perHour ? ImpactRules.FromGrowth((long)perHour) : s.State == TagState.Retained ? ImpactLevel.None : null,
        };
    }

    /// <summary>
    /// A row about a tag the stats count, with its four columns over the window (<paramref name="w"/>): what it grew counted
    /// across PoolWatch's own clears and its level now, what the clears freed of it, its net change and its growth per hour
    /// measured ("+4.9 GB (now 912 MB)", "4.8 GB", "+103 MB", "836.3 MB/h").
    /// </summary>
    public static CauseRow WithWindow(CauseRow row, GrowthWindow w, GrowthCounts c, long nowBytes) => row with
    {
        Grew = GrowthText.TagGrew(c, nowBytes), Cleared = GrowthText.TagCleared(c), Net = GrowthText.TagNet(c), PerHour = GrowthText.TagPerHour(w, c),
        GrewBytes = c.Grew, ClearedBytes = c.Cleared, NetBytes = c.Net, PerHourBytes = c.Grew > 0 ? w.PerHour(c) : null, Windowed = true,
        Impact = ImpactRules.FromGrowth(w, c),
    };

    /// <summary>The top owner of a tag's ranking (the trace's first, when the trace backs it); null when nobody stood out.</summary>
    public static (string name, Confidence level)? CauseOf(TagViewData v) => TopOwner(v) is { } top ? (top.owner.Name, top.level) : null;

    /// <summary>The owner <see cref="CauseOf(TagViewData)"/> names, with its confidence.</summary>
    private static (Owner owner, Confidence level)? TopOwner(TagViewData v)
    {
        var top = v.TraceBacked ? v.Owners.FirstOrDefault() : v.Owners.FirstOrDefault(o => o.Meaningful);
        return top is null || v.Answer.Growth <= 0 ? null : (top.Owner, v.TraceBacked ? Confidence.ConfirmedByTrace : top.Confidence);
    }

    public static bool Stands(ProcessCandidate p) =>
        p.Quota - p.QuotaBefore >= QuotaRise || p.Handles - p.HandlesBefore >= HandleRise || (p.BecameOrphan && p.OpsPerSec >= OrphanOpsPerSec);

    public static CauseRow ForProcess(ProcessCandidate p, DateTime nowUtc)
    {
        long dq = p.Quota - p.QuotaBefore;
        int dh = p.Handles - p.HandlesBefore;
        string what, much, fast, conf;
        if (dq >= QuotaRise)
        {
            what = $"Pool charged to {p.Name}";
            much = $"+{PlainWords.Amount(dq)} (now {PlainWords.Amount(p.Quota)})";
            fast = PlainWords.PerHour(dq, p.Over);
            conf = "charged to it";
        }
        else if (dh >= HandleRise)
        {
            what = $"Handles held by {p.Name}";
            much = $"+{dh:N0} (now {p.Handles:N0})";
            fast = $"{dh / Math.Max(1.0 / 60, p.Over.TotalHours):N0} handles/h";
            conf = "its own handles";
        }
        else
        {
            what = $"{p.Name} kept running after its parent exited";
            much = $"{p.OpsPerSec:N0} I/O operations/s";
            fast = "";
            conf = "worth a look";
        }
        double? perHour = dq >= QuotaRise ? dq / Math.Max(1.0 / 60, p.Over.Duration().TotalHours) : null;
        return new CauseRow($"proc:{p.Key.Pid}:{p.Key.CreateTime}", CauseKind.Process, what, much, fast, PlainWords.WithPid(p.Name, p.Key.Pid), conf,
            p.StartUtc > DateTime.MinValue ? $"started {PlainWords.When(p.StartUtc, nowUtc)}" : "")
        {
            Active = true, TimeUtc = nowUtc, Tone = RowTone.Warning, Process = p.Key, Candidate = p,
            Grew = much, PerHour = fast, GrewBytes = dq >= QuotaRise ? dq : null, PerHourBytes = perHour,
            Impact = perHour is { } rate ? ImpactRules.FromGrowth((long)rate) : null,
        };
    }

    /// <summary>A finding that explains pool growth or release (warning or alert), and every measured cache clear, from the last 24 hours.</summary>
    public static bool Shows(Finding f, DateTime nowUtc) =>
        nowUtc - f.TimeUtc <= FindingWindow && (f.Kind == FindingKind.CacheClear || (PoolKinds.Contains(f.Kind) && f.Severity >= FindingSeverity.Warning));

    public static CauseRow ForFinding(Finding f, DateTime nowUtc)
    {
        string what = f.Title, much = "", cleared = "";
        long? clearedBytes = null;
        if (f.Kind == FindingKind.CacheClear)
        {
            int colon = f.Title.IndexOf(':');
            what = colon > 0 ? f.Title[..colon] : f.Title; // "Clear cache (standby list, manual)"
            if (f.Data.GetValueOrDefault("error") is null && f.Data.GetValueOrDefault("freedBytes") is long freed)
            {
                much = freed >= ClearScheduleText.NothingBelow ? $"freed {PlainWords.Amount(freed)}" : "freed nothing";
                // The Cleared column: what the clear freed ("1.2 GB"), "nothing" under half a megabyte (as the next-clear line says it).
                cleared = freed >= ClearScheduleText.NothingBelow ? PlainWords.Amount(freed) : "nothing";
                clearedBytes = Math.Max(0, freed);
            }
            else if (f.Data.GetValueOrDefault("error") is string) what += " failed";
        }
        string cause = f.Image is { Length: > 0 } image ? (f.Pid > 0 ? PlainWords.WithPid(image, f.Pid) : image) : "";
        return new CauseRow($"find:{f.TimeUtc.Ticks}:{f.Kind}:{f.Title}", CauseKind.Finding, what, much, "", cause, "", PlainWords.When(f.TimeUtc, nowUtc))
        {
            TimeUtc = f.TimeUtc, Folder = f.ReportDir, Finding = f,
            Tone = f.Severity switch { FindingSeverity.Alert => RowTone.Alert, FindingSeverity.Warning => RowTone.Warning, _ => RowTone.Normal },
            Cleared = cleared, ClearedBytes = clearedBytes,
        };
    }

    /// <summary>
    /// What PoolWatch already does about a row, for the details' line under the title: "Handled: the standby cache is emptied
    /// every hour, or at 8 GB." for the records a cache clear frees, "Handled: the token-leak guard is on." for the token
    /// leak's security tokens; or what would handle it, when PoolWatch can but it is off ("Not handled yet: ..."). Null for
    /// a row PoolWatch has nothing to do with. Pure.
    /// </summary>
    public static (string Text, bool Done)? Handled(CauseRow row, HandledBy by)
    {
        if (row.Kind is not (CauseKind.Tag or CauseKind.Incident) || row.Tag is not { } tag) return null;
        if (tag == Toke)
            return by.GuardWorking
                ? ("Handled: the token-leak guard is on, so Windows does not leak these.", true)
                : ("Not handled yet: the token-leak guard stops this leak. You can turn it on in Settings.", false);
        if (!ClearFrees.Contains(tag) && row.ClearedBytes is not > 0) return null;
        return by.Schedule is { } schedule
            ? ($"Handled: the standby cache is emptied {schedule}.", true)
            : ("Not handled yet: emptying the standby cache frees it. You can turn on automatic clearing in Settings.", false);
    }

    private static readonly PoolTag Toke = PoolTag.Parse("Toke");

    /// <summary>The records a cache clear frees: the file systems' and the cache's own (as the clear's evidence counts them).</summary>
    private static readonly IReadOnlySet<PoolTag> ClearFrees =
        ClearEvidence.ReFsTags.Concat(ClearEvidence.NtfsTags).Concat(ClearEvidence.CacheTags).Concat(Engine.Setup.RecommendationInputsBuilder.FileSystemMetadataTags).ToHashSet();

    /// <summary>What is happening now first (open incidents, then growing tags, then processes), then the rest, newest first.</summary>
    public static List<CauseRow> Order(IEnumerable<CauseRow> rows) =>
        rows.OrderByDescending(r => r.Active)
            .ThenBy(r => r.Active ? r.Kind switch { CauseKind.Incident => 0, CauseKind.Tag => 1, _ => 2 } : 0)
            .ThenByDescending(r => r.TimeUtc)
            .ToList();

    // ---------------------------------------------------------------- reading the analysis

    /// <summary>
    /// All rows, ordered. <paramref name="ranked"/> holds the tag rankings (<see cref="TagRankings"/>); a row about a tag the
    /// stats count takes its four columns from <paramref name="window"/> (<see cref="WithWindow"/>).
    /// </summary>
    public static List<CauseRow> Build(AnalysisPipeline a, MonitorEngine engine, TagRankings ranked, GrowthWindow? window = null)
    {
        var rows = Rows(a, engine, ranked);
        if (window is null) return rows;
        lock (a.History.SyncRoot)
        {
            var h = a.History;
            long Now(PoolTag tag)
            {
                if (a.Detector.Status.TryGetValue(tag, out var s)) return s.Bytes;
                var (p, np) = h.Count > 0 ? h.TagAt(tag, h.Count - 1) : (0, 0);
                return p + np;
            }
            return Windowed(rows, window, Now);
        }
    }

    /// <summary>
    /// The rows with the window's four columns (<see cref="WithWindow"/>) on each row about a tag the stats count that is
    /// happening now: a tag growing or held, and its open incident. Every other row keeps its own figures: an earlier
    /// incident what it grew then, a release what it freed, a clear what it freed, a process its own rise.
    /// </summary>
    public static List<CauseRow> Windowed(List<CauseRow> rows, GrowthWindow window, Func<PoolTag, long> nowBytes)
    {
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].Active && rows[i].Tag is { } tag && window.Tags.TryGetValue(tag, out var c)) rows[i] = WithWindow(rows[i], window, c, nowBytes(tag));
        return rows;
    }

    private static List<CauseRow> Rows(AnalysisPipeline a, MonitorEngine engine, TagRankings ranked)
    {
        var now = DateTime.UtcNow;
        var rows = new List<CauseRow>();
        var incidents = UiData.Incidents(a, engine.Paths.IncidentsDir);
        foreach (var r in incidents.Where(r => r.Open || now - r.OpenedUtc <= IncidentWindow).Take(MaxIncidents))
            rows.Add(ForIncident(r, TagWords(r.Tag, PoolTag.TryParse(r.Tag, out var t) ? a.Knowledge.Lookup(t) : null), now));

        var open = incidents.Where(r => r.Open).Select(r => r.Tag).ToHashSet(StringComparer.Ordinal);
        foreach (var s in GrowingTags(a).Where(s => !open.Contains(s.Tag.ToDisplay())))
            rows.Add(ForTag(s, TagWords(s.Tag, a.Knowledge.Lookup(s.Tag)), ranked.Get(s.Tag), now));

        foreach (var p in ProcessCandidates(a, engine)) rows.Add(ForProcess(p, now));

        foreach (var f in a.Diagnostics.Findings.Snapshot().AsEnumerable().Reverse().Where(f => Shows(f, now)).Take(MaxFindings))
            rows.Add(ForFinding(f, now));
        return Order(rows);
    }

    /// <summary>Tags the detector marks as growing, retained or released, the fastest first.</summary>
    private static List<TagStatus> GrowingTags(AnalysisPipeline a)
    {
        lock (a.History.SyncRoot)
            return a.Detector.Status.Values.Where(s => s.State != TagState.Normal).OrderByDescending(s => s.Delta10m).ThenByDescending(s => s.Bytes).ToList();
    }

    /// <summary>The tags whose rows need a ranking for their cause (growing or retained), in the list's order.</summary>
    public static List<PoolTag> TagsToRank(IEnumerable<CauseRow> rows) =>
        rows.Where(r => r.Kind == CauseKind.Tag && r.Active && r.Tag is not null).Select(r => r.Tag!.Value).Distinct().ToList();

    /// <summary>The fastest-growing tag that no open incident explains (the status line's lead when no incident is open).</summary>
    public static TagStatus? TopUnexplained(AnalysisPipeline a, IReadOnlySet<string> openTags)
    {
        lock (a.History.SyncRoot)
            return a.Detector.Status.Values.Where(s => s.State == TagState.Growing && !openTags.Contains(s.Tag.ToDisplay()))
                .OrderByDescending(s => s.Delta10m).ThenByDescending(s => s.Delta60m).FirstOrDefault();
    }

    /// <summary>
    /// The status line's lead: the open growth incident that grew most, else the fastest-growing tag (with its ranking
    /// when there is one; <paramref name="rankTag"/> is then that tag, for the background ranking). Null when nothing grows.
    /// Its cause is named as people know it (<see cref="FriendlyName(Owner, IReadOnlyList{ServiceEntry})"/>), from
    /// <paramref name="services"/>, the services Windows runs now.
    /// </summary>
    public static PoolStatusLead? Lead(AnalysisPipeline a, TagRankings ranked, IReadOnlyList<ServiceEntry> services, TimeSpan held, out PoolTag? rankTag)
    {
        rankTag = null;
        var now = DateTime.UtcNow;
        Incident? inc;
        (string? name, Confidence level)? cause;
        lock (a.History.SyncRoot)
        {
            inc = a.Incidents.Open.Where(i => i.Kind is IncidentKind.Growth or IncidentKind.NonPagedTotal && i.Growth > 0).MaxBy(i => i.Growth);
            cause = inc is null ? null : FriendlyCause(inc, services);
        }
        if (inc is not null)
            return new PoolStatusLead(TagWords(inc.Tag, a.Knowledge.Lookup(inc.Tag)), inc.Growth, now - inc.OnsetUtc, cause?.name, cause?.level ?? Confidence.Weak);
        var openTags = new HashSet<string>(StringComparer.Ordinal);
        if (TopUnexplained(a, openTags) is not { } s) return null;
        long growth = s.Delta60m > 0 ? s.Delta60m : s.Delta10m;
        if (growth <= 0) return null;
        var over = s.Delta60m > 0 ? (held < TimeSpan.FromHours(1) ? held : TimeSpan.FromHours(1)) : TimeSpan.FromMinutes(Math.Min(10, Math.Max(1, held.TotalMinutes)));
        rankTag = s.Tag;
        var tagCause = TagCause(ranked.Get(s.Tag), services);
        return new PoolStatusLead(TagWords(s.Tag, a.Knowledge.Lookup(s.Tag)), growth, over, tagCause?.name, tagCause?.level ?? Confidence.Weak);
    }

    /// <summary>
    /// A growing tag's likely cause as <see cref="CauseOf(TagViewData)"/> picks it from its ranking (none before the
    /// ranking), by its friendly name.
    /// </summary>
    internal static (string? name, Confidence level)? TagCause(TagViewData? view, IReadOnlyList<ServiceEntry> services) =>
        view is not null && TopOwner(view) is { } top ? (FriendlyName(top.owner, services), top.level) : null;

    /// <summary>
    /// The incident's likely cause as <see cref="UiData.CauseOf(Incident)"/> picks it (the trace's top owner, else the top
    /// suspect owner, else the top process), by its friendly name. The caller holds the history lock.
    /// </summary>
    internal static (string? name, Confidence level)? FriendlyCause(Incident i, IReadOnlyList<ServiceEntry> services)
    {
        if (i.TraceOwners is { Count: > 0 } traced) return (FriendlyName(traced[0].Owner, services), Confidence.ConfirmedByTrace);
        if (i.Owners?.FirstOrDefault(o => o.Meaningful) is { } owner) return (FriendlyName(owner.Owner, services), owner.Confidence);
        if (i.Suspects.FirstOrDefault() is { } top) return (FriendlyName(top, services), top.Confidence);
        return null;
    }

    /// <summary>
    /// The tray's notice when a growth incident opens, in the status line's words: "Kernel pool: Open files (File) is
    /// growing.", then the likely cause by its friendly name only when the evidence is strong or a trace confirmed it
    /// (<see cref="PoolStatusLine.CauseSentence"/>), else where to look. Never an exe, a PID or the detector's rule.
    /// Null for any other incident: a pool released (often by PoolWatch's own cache clear) needs nothing from you, and
    /// Pool causes lists it. The caller holds the history lock (the incident manager raises its event under it).
    /// </summary>
    internal static string? IncidentNotice(Incident i, TagInfo? info, IReadOnlyList<ServiceEntry> services)
    {
        if (i.Kind is not (IncidentKind.Growth or IncidentKind.NonPagedTotal)) return null;
        string over = i.Kind == IncidentKind.NonPagedTotal ? ", with non-paged pool over the limit" : "";
        string cause = FriendlyCause(i, services) is { } c ? PoolStatusLine.CauseSentence(c.name, c.level) : "";
        return $"Kernel pool: {TagWords(i.Tag, info)} is growing{over}." + (cause.Length > 0 ? cause : " Open PoolWatch for details.");
    }

    /// <summary>
    /// The tray's handler for an incident that opened: <see cref="IncidentNotice"/> with the tag's description from
    /// <paramref name="lookup"/>, or null for no notice. A failure to word it is logged, never thrown into the sampler.
    /// </summary>
    internal static string? TrayNotice(Incident i, Func<PoolTag, TagInfo?> lookup, IReadOnlyList<ServiceEntry> services, Action<string> log)
    {
        try { return IncidentNotice(i, lookup(i.Tag), services); }
        catch (Exception ex) { log("incident notice: " + ex.Message); return null; }
    }

    private static readonly Regex PidSuffix = new(@" \(PID \d+\)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// A cause by the name people know it by, for the status line: an app's own name ("Codex", "Stream Deck"), a service's
    /// display name ("Example Device Service"), a Claude Code session with its folder, else its program without
    /// ".exe" ("Leaker"), never a PID (Pool causes keeps every detail). Null for an owner nobody would know by a name:
    /// the kernel's own threads, a program started through WMI, a process known only by its PID.
    /// </summary>
    /// <param name="services">The services Windows runs now, for their display names.</param>
    public static string? FriendlyName(Owner owner, IReadOnlyList<ServiceEntry> services) => owner.Kind switch
    {
        OwnerKind.App or OwnerKind.Aicc => owner.Name,
        OwnerKind.Service => ServiceNames(ProcessIdentities.ServicesOf(owner), services),
        OwnerKind.ClaudeSession => PidSuffix.Replace(owner.Name, ""),
        OwnerKind.Unknown when PidSuffix.IsMatch(owner.Name) => ProgramName(PidSuffix.Replace(owner.Name, "")),
        _ => null,
    };

    /// <summary>The same for a process (an incident's top suspect before its owners are ranked): its services, else its program.</summary>
    public static string? FriendlyName(Suspect process, IReadOnlyList<ServiceEntry> services) =>
        process.Services.Count > 0 ? ServiceNames(process.Services, services) : ProgramName(process.Image);

    /// <summary>
    /// The first service by the name Windows shows for it (its own name once Windows no longer lists it), and how many
    /// more the same process runs: "Example Device Service", "Task Scheduler and 2 other services".
    /// </summary>
    private static string? ServiceNames(IReadOnlyList<string> names, IReadOnlyList<ServiceEntry> services)
    {
        if (names.Count == 0) return null;
        string first = services.FirstOrDefault(e => e.Name.Equals(names[0], StringComparison.OrdinalIgnoreCase))?.DisplayName is { Length: > 0 } shown ? shown : names[0];
        return names.Count switch { 1 => first, 2 => $"{first} and 1 other service", _ => $"{first} and {names.Count - 1} other services" };
    }

    /// <summary>A program by its file name without ".exe" ("Leaker"); null for anything else ("System", "unknown process").</summary>
    private static string? ProgramName(string image) =>
        image.Length > 4 && image.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? image[..^4] : null;

    /// <summary>Live processes that stand out (<see cref="Stands"/>), the largest rise first.</summary>
    private static List<ProcessCandidate> ProcessCandidates(AnalysisPipeline a, MonitorEngine engine)
    {
        var pair = engine.LatestPair;
        if (pair?.Current is not { } latest) return [];
        var prev = pair.Previous?.Processes.ToDictionary(p => p.Key);
        double secs = pair.Previous is null ? 1 : Math.Max(1, (latest.TimeUtc - pair.Previous.TimeUtc).TotalSeconds);
        var list = new List<ProcessCandidate>();
        lock (a.History.SyncRoot)
        {
            var h = a.History;
            if (h.Count < 2) return [];
            int from = h.IndexAtOrAfter(h.Times[^1] - TimeSpan.FromHours(1));
            foreach (var p in latest.Processes)
            {
                long quota = (long)(p.QuotaPagedPoolUsage + p.QuotaNonPagedPoolUsage);
                long ops = prev is not null && prev.TryGetValue(p.Key, out var q) ? Math.Max(0, (long)((p.TotalOperations - q.TotalOperations) / secs)) : 0;
                // Cheap filter first: a rise of 5,000 handles or 32 MB needs at least that much now.
                if (p.HandleCount < HandleRise && quota < QuotaRise && ops < OrphanOpsPerSec) continue;
                var rec = engine.Registry.Get(p.Key);
                var series = h.ProcessSeries(p.Key, from);
                int first = Array.FindIndex(series, x => x is not null);
                int handlesBefore = first >= 0 ? series[first]!.Value.Handles : p.HandleCount;
                long quotaBefore = first >= 0 ? series[first]!.Value.QuotaPaged + series[first]!.Value.QuotaNonPaged : quota;
                var over = first >= 0 ? h.Times[^1] - h.Times[from + first] : TimeSpan.Zero;
                bool becameOrphan = rec is { OrphanedUtc: { } orphaned } && orphaned - rec.FirstSeenUtc >= ProcessRegistry.OrphanGrace;
                list.Add(new ProcessCandidate(p.Key, rec?.DisplayName ?? p.ImageName, p.HandleCount, handlesBefore, quota, quotaBefore, over, becameOrphan, ops, p.Key.CreateTimeUtc));
            }
        }
        return list.Where(Stands)
            .OrderByDescending(p => Math.Max((p.Quota - p.QuotaBefore) / (double)QuotaRise, (p.Handles - p.HandlesBefore) / (double)HandleRise))
            .Take(MaxProcesses).ToList();
    }
}


/// <summary>
/// Which tag to rank next (pure; unit-tested). A ranking (the correlator over the last hour) costs tens of milliseconds,
/// so they are spaced: the selected row's tag at once when it has none, then every tag in the list once (one every
/// <see cref="NewGap"/>), then each refreshed after <see cref="Fresh"/>, the selected one after <see cref="SelectedFresh"/>,
/// never two within <see cref="Gap"/> (about the old tag page's two rankings a minute).
/// </summary>
internal static class RankPlan
{
    public static readonly TimeSpan Fresh = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan SelectedFresh = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Gap = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan NewGap = TimeSpan.FromSeconds(5);

    /// <param name="wanted">The tags the rows and the status line show, most important first.</param>
    /// <param name="selected">The selected row's tag, if the selected row is a tag row.</param>
    /// <param name="builtUtc">When each tag was last ranked (tried: a ranking that found nothing counts).</param>
    /// <param name="lastBuildUtc">When the last ranking finished.</param>
    public static PoolTag? Next(IReadOnlyList<PoolTag> wanted, PoolTag? selected, IReadOnlyDictionary<PoolTag, DateTime> builtUtc,
        DateTime lastBuildUtc, DateTime nowUtc)
    {
        var since = nowUtc - lastBuildUtc;
        if (selected is { } sel && !builtUtc.ContainsKey(sel)) return sel; // the user is waiting on it
        if (since >= NewGap)
            foreach (var t in wanted)
                if (!builtUtc.ContainsKey(t)) return t;
        if (since < Gap) return null;
        if (selected is { } s && nowUtc - builtUtc[s] >= SelectedFresh) return s;
        PoolTag? stalest = null;
        var oldest = DateTime.MaxValue;
        foreach (var t in wanted)
            if (builtUtc.TryGetValue(t, out var b) && nowUtc - b >= Fresh && b < oldest) { oldest = b; stalest = t; }
        return stalest;
    }
}

/// <summary>
/// The rankings of growing tags (the old tag page's suspects). <see cref="Pump"/> builds the next one <see cref="RankPlan"/>
/// picks on a pool thread, one at a time, so the window never waits on the correlator; the rows show what is cached
/// (a blank cause until the first ranking). A tag that stops growing keeps its last ranking, so its released row still
/// has a summary to copy (a released row names no cause), until the oldest are dropped past <see cref="MaxCached"/> or the
/// window hides and drops them all (<see cref="Clear"/>). Thread-safe.
/// </summary>
internal sealed class TagRankings
{
    /// <summary>The most rankings kept, the oldest dropped first. Each holds only what its row shows (a few owners and events).</summary>
    public const int MaxCached = 64;
    private readonly object _lock = new();
    private readonly Dictionary<PoolTag, (TagViewData? view, DateTime builtUtc)> _views = new();
    private PoolTag? _building;
    private DateTime _lastBuildUtc = DateTime.MinValue;
    /// <summary>Bumped by <see cref="Clear"/>: a ranking started before it is not stored.</summary>
    private int _generation;
    private readonly Func<DateTime> _now;

    public TagRankings(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.UtcNow);

    /// <summary>What the rankings hold, for PoolWatch's own cost report: the rankings, their owners, the owners' kept processes and the events.</summary>
    public (int rankings, int owners, int ownerMembers, int events) Held()
    {
        lock (_lock)
        {
            var views = _views.Values.Select(v => v.view).OfType<TagViewData>().ToList();
            return (_views.Count, views.Sum(v => v.Owners.Count), views.Sum(v => v.Owners.Sum(o => o.Members.Count)), views.Sum(v => v.Events.Count));
        }
    }

    /// <summary>
    /// Drops every ranking, for when the window hides (nothing shows them, and the next show ranks afresh); a ranking
    /// running now is discarded when it finishes.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _views.Clear();
            _lastBuildUtc = DateTime.MinValue;
            _generation++;
        }
    }

    /// <summary>The cached ranking, or null when the tag has none yet.</summary>
    public TagViewData? Get(PoolTag tag)
    {
        lock (_lock) return _views.TryGetValue(tag, out var v) ? v.view : null;
    }

    /// <summary>The tag has been ranked (or tried) at least once.</summary>
    public bool Tried(PoolTag tag)
    {
        lock (_lock) return _views.ContainsKey(tag);
    }

    /// <summary>The tag is being ranked right now.</summary>
    public bool Building(PoolTag tag)
    {
        lock (_lock) return _building == tag;
    }

    /// <summary>CI screenshots only: ranks, on the calling thread and without spacing, every tag that has none yet.</summary>
    public void RankNow(Func<PoolTag, TagViewData?> build, IEnumerable<PoolTag> tags)
    {
        foreach (var tag in tags)
        {
            if (Tried(tag)) continue;
            TagViewData? view = null;
            try { view = build(tag); } catch (Exception) { }
            lock (_lock) { _views[tag] = (view, _now()); _lastBuildUtc = _now(); }
        }
    }

    /// <summary>
    /// Starts the next ranking (<see cref="RankPlan.Next"/>) on a pool thread when none is running; <paramref name="done"/>
    /// runs on that thread when it has finished (the caller posts back to its own). Returns the tag started, or null.
    /// </summary>
    public PoolTag? Pump(Func<PoolTag, TagViewData?> build, IReadOnlyList<PoolTag> wanted, PoolTag? selected, Action done)
    {
        PoolTag tag;
        int generation;
        lock (_lock)
        {
            if (_building is not null) return null;
            var built = _views.ToDictionary(kv => kv.Key, kv => kv.Value.builtUtc);
            if (RankPlan.Next(wanted, selected, built, _lastBuildUtc, _now()) is not { } next) return null;
            tag = next;
            _building = tag;
            generation = _generation;
        }
        Task.Run(() =>
        {
            TagViewData? view = null;
            try
            {
                using (Core.Util.Profiler.Stage("ui.rank")) view = build(tag);
            }
            catch (Exception) { /* a failed ranking counts as tried: the row stays blank, the next try is after Fresh */ }
            finally
            {
                lock (_lock)
                {
                    _building = null;
                    if (generation == _generation)
                    {
                        var now = _now();
                        _views[tag] = (view, now);
                        _lastBuildUtc = now;
                        if (_views.Count > MaxCached)
                            foreach (var old in _views.OrderBy(kv => kv.Value.builtUtc).Take(_views.Count - MaxCached).Select(kv => kv.Key).ToList()) _views.Remove(old);
                    }
                }
                done();
            }
        });
        return tag;
    }
}

/// <summary>What PoolWatch does now that can handle a row (<see cref="PoolCauses.Handled"/>).</summary>
/// <param name="Schedule">The automatic clear in words ("every hour, or at 8 GB"), or null while it is off.</param>
/// <param name="GuardWorking">The token-leak guard is on and Windows' setting that stops the leak is on now.</param>
internal sealed record HandledBy(string? Schedule, bool GuardWorking)
{
    /// <summary>The automatic clear's rules in words: "every hour, or at 8 GB", "every 2 hours", "when the kernel pool reaches 8 GB"; null when it is off or has no rule.</summary>
    public static string? ScheduleOf(bool enabled, double everyHours, double poolGb)
    {
        if (!enabled || (everyHours <= 0 && poolGb <= 0)) return null;
        string every = everyHours == 1 ? "every hour" : FormattableString.Invariant($"every {everyHours:0.#} hours");
        string at = FormattableString.Invariant($"{poolGb:0.#} GB");
        return everyHours > 0 && poolGb > 0 ? $"{every}, or at {at}" : everyHours > 0 ? every : $"when the kernel pool reaches {at}";
    }
}
