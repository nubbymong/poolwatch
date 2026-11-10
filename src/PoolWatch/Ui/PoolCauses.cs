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

/// <summary>A tray notice: its headline as the title (the toast names the app above it), and what it says under it.</summary>
internal sealed record NoticeText(string Title, string Text);

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

    /// <summary>
    /// Finding kinds that explain pool growth or its release; the rest (lag, marks, versions, ...) stay in the vendor bundle,
    /// and with Detailed recording in findings.jsonl.
    /// </summary>
    private static readonly HashSet<FindingKind> PoolKinds =
        [FindingKind.SpawnStorm, FindingKind.GitRoot, FindingKind.StrayGit, FindingKind.ChildAccumulation, FindingKind.TokenSpawn, FindingKind.FilterEvent];

    // ---------------------------------------------------------------- pure mapping

    /// <summary>A tag in plain words (<see cref="PlainWords.Tag"/>): "File system metadata (Reff, refs.sys)", "Pool tag Abcd (foo.sys)".</summary>
    public static string TagWords(PoolTag tag, TagInfo? info) => PlainWords.Tag(tag, info);

    /// <summary>The same for a tag read back from a report ("Reff"); a tag that does not parse is shown as it is.</summary>
    public static string TagWords(string tag, TagInfo? info) => PoolTag.TryParse(tag, out var t) ? PlainWords.Tag(t, info) : $"Pool tag {tag}";

    /// <param name="nowMs">Now on the clock that only goes forward: an open incident's growth so far is timed on it from its
    /// onset (<see cref="IncidentRow.OnsetMs"/>), and a closed one's from its onset to its end. A row read back from disk has
    /// its times of day only, and is timed by them.</param>
    public static CauseRow ForIncident(IncidentRow r, string what, DateTime nowUtc, long? nowMs = null)
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
                    var over = r.OnsetMs is { } om && (r.Open ? nowMs : r.EndMs) is { } until ? TimeSpan.FromMilliseconds(until - om)
                        : (r.Open ? nowUtc : r.EndUtc ?? r.OpenedUtc) - start;
                    much = $"+{PlainWords.Amount(r.Growth)}";
                    fast = PlainWords.PerHour(r.Growth, over);
                    grew = r.Growth;
                    rate = r.Growth / Math.Max(1.0 / 60, over.Duration().TotalHours);
                }
                if (r.Kind == IncidentKind.NonPagedTotal) what += ", with " + PlainWords.NonPagedOverTheLimit;
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

    /// <summary>
    /// A finding that explains pool growth or release (warning or alert), and every measured cache clear. The list takes them
    /// from the last 24 hours on the clock that only goes forward (<see cref="Core.Findings.FindingLog.Within"/>).
    /// </summary>
    public static bool Shows(Finding f) =>
        f.Kind == FindingKind.CacheClear || (PoolKinds.Contains(f.Kind) && f.Severity >= FindingSeverity.Warning);

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

    /// <summary>
    /// The incidents the list shows: the open ones and those opened in the last <see cref="IncidentWindow"/> before
    /// <paramref name="at"/>, timed on the clock that only goes forward (<see cref="IncidentRow.OpenedMs"/>).
    /// </summary>
    internal static IEnumerable<IncidentRow> Shown(IEnumerable<IncidentRow> incidents, Core.Model.Moment at) =>
        incidents.Where(r => r.Open || at.Ms - r.OpenedMs <= (long)IncidentWindow.TotalMilliseconds);

    private static List<CauseRow> Rows(AnalysisPipeline a, MonitorEngine engine, TagRankings ranked)
    {
        var at = Core.Model.Moment.Now(); // how long ago an incident opened is timed on the clock that only goes forward
        var now = at.Utc;
        var rows = new List<CauseRow>();
        var incidents = UiData.Incidents(a, engine.Paths.IncidentsDir);
        foreach (var r in Shown(incidents, at).Take(MaxIncidents))
            rows.Add(ForIncident(r, TagWords(r.Tag, PoolTag.TryParse(r.Tag, out var t) ? a.Knowledge.Lookup(t) : null), now, at.Ms));

        var open = incidents.Where(r => r.Open).Select(r => r.Tag).ToHashSet(StringComparer.Ordinal);
        foreach (var s in GrowingTags(a).Where(s => !open.Contains(s.Tag.ToDisplay())))
            rows.Add(ForTag(s, TagWords(s.Tag, a.Knowledge.Lookup(s.Tag)), ranked.Get(s.Tag), now));

        foreach (var p in ProcessCandidates(a, engine)) rows.Add(ForProcess(p, now));

        foreach (var f in a.Diagnostics.Findings.Within(FindingWindow, at).AsEnumerable().Reverse().Where(Shows).Take(MaxFindings))
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
        Incident? inc;
        (string? name, Confidence level)? cause;
        lock (a.History.SyncRoot)
        {
            inc = a.Incidents.Open.Where(i => i.Kind is IncidentKind.Growth or IncidentKind.NonPagedTotal && i.Growth > 0).MaxBy(i => i.Growth);
            cause = inc is null ? null : FriendlyCause(inc, services);
        }
        if (inc is not null)
            return new PoolStatusLead(TagWords(inc.Tag, a.Knowledge.Lookup(inc.Tag)), inc.Growth, TimeSpan.FromMilliseconds(Core.Model.Moment.Now().Ms - inc.OnsetMs), cause?.name, cause?.level ?? Confidence.Weak); // on the forward clock
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

    /// <summary>The longest title a tray notice shows whole (a notice's title holds 63 characters).</summary>
    internal const int MaxNoticeTitle = 63;

    /// <summary>The title of a non-paged pool incident's notice, and of a growth's whose tag's words are too long for one.</summary>
    internal const string NonPagedTitle = "Non-paged pool over the limit", GrowingTitle = "Kernel pool growing";

    /// <summary>Where a notice that names no cause sends you: its own click opens the window.</summary>
    internal const string ClickForDetails = "Click here to open PoolWatch for details.";

    /// <summary>
    /// The tray's notice when a growth incident opens, in the status line's words, the headline as its title (the toast
    /// names the app above it): "Kernel pool: Open files (File) is growing", then in its text the likely cause by its
    /// friendly name only when the evidence is strong or a trace confirmed it (<see cref="PoolStatusLine.CauseSentence"/>),
    /// else that a click opens PoolWatch for details. The non-paged pool over its limit is the title of that incident's
    /// notice, and the growth its first sentence. A title too long for a notice (a tag's long description) is
    /// <see cref="GrowingTitle"/>, and the growth goes into the text. Never an exe, a PID or the detector's rule.
    /// Null for any other incident: a pool released (often by PoolWatch's own cache clear) needs nothing from you, and
    /// Pool causes lists it. The caller holds the history lock (the incident manager raises its event under it).
    /// </summary>
    internal static NoticeText? IncidentNotice(Incident i, TagInfo? info, IReadOnlyList<ServiceEntry> services)
    {
        if (i.Kind is not (IncidentKind.Growth or IncidentKind.NonPagedTotal)) return null;
        string growing = $"Kernel pool: {TagWords(i.Tag, info)} is growing";
        string cause = FriendlyCause(i, services) is { } c ? PoolStatusLine.CauseSentence(c.name, c.level).TrimStart() : "";
        string then = cause.Length > 0 ? cause : ClickForDetails;
        if (i.Kind == IncidentKind.NonPagedTotal) return new(NonPagedTitle, $"{growing}. {then}");
        return growing.Length <= MaxNoticeTitle ? new(growing, then) : new(GrowingTitle, $"{growing}. {then}");
    }

    /// <summary>
    /// Whether the tray shows the notice of an incident of <paramref name="kind"/> whose likely cause is known at
    /// <paramref name="cause"/> (null: none yet), as the owner set <see cref="Core.Settings.PoolNotices"/>: every one; only
    /// one the tray icon shows red now, by the icon's own rule (UiData.State: the non-paged pool over its limit and still
    /// rising, or a growth whose cause a trace confirmed; a growth that turns red later is told then, by
    /// <see cref="IncidentNotices.TurnedRed"/>); or none.
    /// </summary>
    internal static bool NoticeWanted(PoolNoticeLevel level, IncidentKind kind, Confidence? cause) => level switch
    {
        PoolNoticeLevel.EveryGrowth => true,
        PoolNoticeLevel.NeedsAttention => UiData.State([(kind, cause)], retained: false) == TrayState.Red,
        _ => false,
    };

    /// <summary>
    /// The tray's handler for an incident that opened: <see cref="IncidentNotice"/> with the tag's description from
    /// <paramref name="lookup"/>, when <paramref name="level"/> (the setting as it is now) wants one (<see
    /// cref="NoticeWanted"/>); null for no notice, and one held back by the setting is logged. A failure to word it is
    /// logged, never thrown into the sampler.
    /// </summary>
    internal static NoticeText? TrayNotice(Incident i, PoolNoticeLevel level, Func<PoolTag, TagInfo?> lookup, IReadOnlyList<ServiceEntry> services, Action<string> log)
    {
        try
        {
            if (IncidentNotice(i, lookup(i.Tag), services) is not { } notice) return null;
            if (NoticeWanted(level, i.Kind, UiData.CauseOf(i)?.level)) return notice;
            log($"notice not shown ({level}): {notice.Title}");
            return null;
        }
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
    private static List<ProcessCandidate> ProcessCandidates(AnalysisPipeline a, MonitorEngine engine) =>
        ProcessCandidates(engine.LatestPair, a.History, engine.Registry);

    /// <summary>
    /// The live processes of <paramref name="pair"/> that stand out over the history's last hour: the rates and the hour are
    /// timed on the clock that only goes forward (the samples' <see cref="Sample.TickMs"/> and <see cref="Core.History.History.Ms"/>), and
    /// so is whether a process became an orphan after it was first seen. Takes the history's lock.
    /// </summary>
    internal static List<ProcessCandidate> ProcessCandidates(SamplePair? pair, Core.History.History h, ProcessRegistry registry)
    {
        if (pair?.Current is not { } latest) return [];
        var prev = pair.Previous?.Processes.ToDictionary(p => p.Key);
        double secs = pair.Previous is null ? 1 : Math.Max(1, (latest.TickMs - pair.Previous.TickMs) / 1000.0);
        var list = new List<ProcessCandidate>();
        lock (h.SyncRoot)
        {
            if (h.Count < 2) return [];
            int from = h.IndexAtOrAfterMs(h.Ms[^1] - 3_600_000);
            foreach (var p in latest.Processes)
            {
                long quota = (long)(p.QuotaPagedPoolUsage + p.QuotaNonPagedPoolUsage);
                long ops = prev is not null && prev.TryGetValue(p.Key, out var q) ? Math.Max(0, (long)((p.TotalOperations - q.TotalOperations) / secs)) : 0;
                // Cheap filter first: a rise of 5,000 handles or 32 MB needs at least that much now.
                if (p.HandleCount < HandleRise && quota < QuotaRise && ops < OrphanOpsPerSec) continue;
                var rec = registry.Get(p.Key);
                var series = h.ProcessSeries(p.Key, from);
                int first = Array.FindIndex(series, x => x is not null);
                int handlesBefore = first >= 0 ? series[first]!.Value.Handles : p.HandleCount;
                long quotaBefore = first >= 0 ? series[first]!.Value.QuotaPaged + series[first]!.Value.QuotaNonPaged : quota;
                var over = first >= 0 ? TimeSpan.FromMilliseconds(h.Ms[^1] - h.Ms[from + first]) : TimeSpan.Zero;
                bool becameOrphan = rec is { OrphanedMs: { } orphaned } && orphaned - rec.FirstSeenMs >= (long)ProcessRegistry.OrphanGrace.TotalMilliseconds;
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
/// never two within <see cref="Gap"/> (about the old tag page's two rankings a minute). Timed on the clock that only goes
/// forward (<see cref="Core.Model.Moment.Ms"/>): setting the system clock back or forward neither holds a ranking back nor
/// brings one early.
/// </summary>
internal static class RankPlan
{
    public static readonly TimeSpan Fresh = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan SelectedFresh = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Gap = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan NewGap = TimeSpan.FromSeconds(5);

    /// <param name="wanted">The tags the rows and the status line show, most important first.</param>
    /// <param name="selected">The selected row's tag, if the selected row is a tag row.</param>
    /// <param name="builtMs">When each tag was last ranked (tried: a ranking that found nothing counts), on the forward clock.</param>
    /// <param name="lastBuildMs">When the last ranking finished, on the forward clock; null before the first.</param>
    /// <param name="nowMs">Now, on the forward clock.</param>
    public static PoolTag? Next(IReadOnlyList<PoolTag> wanted, PoolTag? selected, IReadOnlyDictionary<PoolTag, long> builtMs,
        long? lastBuildMs, long nowMs)
    {
        long since = lastBuildMs is { } last ? nowMs - last : long.MaxValue;
        if (selected is { } sel && !builtMs.ContainsKey(sel)) return sel; // the user is waiting on it
        if (since >= (long)NewGap.TotalMilliseconds)
            foreach (var t in wanted)
                if (!builtMs.ContainsKey(t)) return t;
        if (since < (long)Gap.TotalMilliseconds) return null;
        if (selected is { } s && nowMs - builtMs[s] >= (long)SelectedFresh.TotalMilliseconds) return s;
        PoolTag? stalest = null;
        long oldest = long.MaxValue;
        foreach (var t in wanted)
            if (builtMs.TryGetValue(t, out var b) && nowMs - b >= (long)Fresh.TotalMilliseconds && b < oldest) { oldest = b; stalest = t; }
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
    /// <summary>Each tag's ranking and when it was made, on the clock that only goes forward.</summary>
    private readonly Dictionary<PoolTag, (TagViewData? view, long builtMs)> _views = new();
    private PoolTag? _building;
    private long? _lastBuildMs;
    /// <summary>Bumped by <see cref="Clear"/>: a ranking started before it is not stored.</summary>
    private int _generation;
    private readonly Func<Core.Model.Moment> _clock;

    /// <param name="clock">Both clocks (<see cref="Core.Model.Moment.Now"/>; a test gives its own): the rankings are spaced on the forward one.</param>
    public TagRankings(Func<Core.Model.Moment>? clock = null) => _clock = clock ?? Core.Model.Moment.Now;

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
            _lastBuildMs = null;
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
            lock (_lock) { long now = _clock().Ms; _views[tag] = (view, now); _lastBuildMs = now; }
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
            var built = _views.ToDictionary(kv => kv.Key, kv => kv.Value.builtMs);
            if (RankPlan.Next(wanted, selected, built, _lastBuildMs, _clock().Ms) is not { } next) return null;
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
                        long now = _clock().Ms;
                        _views[tag] = (view, now);
                        _lastBuildMs = now;
                        if (_views.Count > MaxCached)
                            foreach (var old in _views.OrderBy(kv => kv.Value.builtMs).Take(_views.Count - MaxCached).Select(kv => kv.Key).ToList()) _views.Remove(old);
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

/// <summary>
/// The tray's kernel pool notices, at most one for each episode of an incident (a re-opened incident is a new episode,
/// <see cref="Incident.Reopened"/>, and gets its notice again), as Settings' Notifications choice is at that moment
/// (<see cref="Core.Settings.PoolNotices"/>). Every growth: the notice as the incident opens. Only when it needs attention:
/// the notice as the incident opens when the tray icon shows it red then (the non-paged pool over its limit), else once it
/// turns red while it is open (a trace confirmed the growth's cause, which comes after the incident opened: the incident
/// manager raises its event before any trace), at the tray's next refresh of its icon, so the notice comes as the icon
/// turns red. Off: none. A notice shown under any choice is never shown again in that episode, so a change of the choice
/// repeats nothing. Both calls come under the history lock: <see cref="Opened"/> from the incident manager's event, which
/// it raises under it, and <see cref="TurnedRed"/> from the tray's refresh, which takes it. It keeps only the incidents
/// open now whose notice was shown (or failed to be worded, which is logged once).
/// </summary>
/// <param name="lookup">The tag's description (the knowledge base's).</param>
/// <param name="services">The services running now, for a cause's friendly name.</param>
/// <param name="log">The engine log: a notice held back by the choice, a failure to word one.</param>
internal sealed class IncidentNotices(Func<PoolTag, TagInfo?> lookup, Func<IReadOnlyList<ServiceEntry>> services, Action<string> log)
{
    /// <summary>Each open incident whose notice was shown, by its id, with the episode it was shown for (its <see cref="Incident.Reopened"/>).</summary>
    private readonly Dictionary<string, int> _told = new(StringComparer.Ordinal);

    /// <summary>How many incidents it keeps (the tests: only those open whose notice was shown).</summary>
    internal int Held => _told.Count;

    /// <summary>An incident opened or re-opened: its notice as <paramref name="level"/> wants it now (<see cref="PoolCauses.TrayNotice"/>), or null.</summary>
    public NoticeText? Opened(Incident i, PoolNoticeLevel level)
    {
        var notice = PoolCauses.TrayNotice(i, level, lookup, services(), log);
        if (notice is not null) _told[i.Id] = i.Reopened;
        return notice;
    }

    /// <summary>
    /// The tray's refresh, with the incidents <paramref name="open"/> now: under <see cref="PoolNoticeLevel.NeedsAttention"/>,
    /// the notice of each one the tray icon shows red now whose notice this episode was not shown yet; none under the other
    /// choices (every growth was told as it opened). The incidents no longer open are forgotten.
    /// </summary>
    public IReadOnlyList<NoticeText> TurnedRed(IReadOnlyCollection<Incident> open, PoolNoticeLevel level)
    {
        if (_told.Count > 0) Forget(open);
        if (level != PoolNoticeLevel.NeedsAttention) return [];
        List<NoticeText>? notices = null;
        foreach (var i in open)
        {
            if (_told.TryGetValue(i.Id, out int episode) && episode == i.Reopened) continue;
            if (!PoolCauses.NoticeWanted(level, i.Kind, UiData.CauseOf(i)?.level)) continue;
            _told[i.Id] = i.Reopened; // once, even when it cannot be worded: a failure is logged once, not at every refresh
            try
            {
                if (PoolCauses.IncidentNotice(i, lookup(i.Tag), services()) is { } notice) (notices ??= []).Add(notice);
            }
            catch (Exception ex) { log("incident notice: " + ex.Message); }
        }
        return notices ?? (IReadOnlyList<NoticeText>)[];
    }

    /// <summary>Forgets each incident that is not in <paramref name="open"/> any more (closed; a re-open raises its event again).</summary>
    private void Forget(IReadOnlyCollection<Incident> open)
    {
        List<string>? gone = null;
        foreach (string id in _told.Keys)
        {
            bool still = false;
            foreach (var i in open)
                if (i.Id == id) { still = true; break; }
            if (!still) (gone ??= []).Add(id);
        }
        if (gone is not null) foreach (string id in gone) _told.Remove(id);
    }
}
