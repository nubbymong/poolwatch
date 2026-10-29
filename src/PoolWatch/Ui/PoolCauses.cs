using PoolWatch.Analysis;
using PoolWatch.Core;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;
using PoolWatch.Core.Util;
using PoolWatch.Engine;

namespace PoolWatch.Ui;

internal enum CauseKind { Incident, Tag, Process, Finding }

internal enum RowTone { Normal, Warning, Alert }

/// <summary>
/// One row of the Pool causes list: what is growing, how much and how fast, the likely cause, the confidence in words,
/// and when. It merges what were four tabs (Incidents, Growing tags, Processes, Findings); the payload says where its
/// details come from.
/// </summary>
internal sealed record CauseRow(string Key, CauseKind Kind, string What, string HowMuch, string HowFast, string Cause, string Confidence, string When)
{
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

    public string[] Cells => [What, HowMuch, HowFast, Cause, Confidence, When];
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
    public static readonly string[] Columns = ["What is growing", "How much", "How fast", "Likely cause", "Confidence", "When"];

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
        string much = "", fast = "";
        switch (r.Kind)
        {
            case IncidentKind.Release:
                long freed = r.Dropped > 0 ? r.Dropped : Math.Abs(r.Growth);
                much = freed > 0 ? $"freed {Fmt.Bytes(freed)}" : "";
                what = $"{what} was released";
                break;
            default:
                if (r.Growth > 0)
                {
                    much = $"+{Fmt.Bytes(r.Growth)}";
                    fast = PlainWords.PerHour(r.Growth, (r.Open ? nowUtc : r.EndUtc ?? r.OpenedUtc) - start);
                }
                if (r.Kind == IncidentKind.NonPagedTotal) what += ", with non-paged pool over the limit";
                else if (r.Kind == IncidentKind.Capture) what = "Manual capture: " + what;
                break;
        }
        return new CauseRow("inc:" + r.Dir, CauseKind.Incident, what, much, fast, r.Top, r.Confidence,
            r.Open ? $"since {PlainWords.When(start, nowUtc)}" : PlainWords.When(start, nowUtc))
        {
            Active = r.Open, TimeUtc = r.OpenedUtc, Tone = r.Open ? RowTone.Alert : RowTone.Normal, Folder = r.Dir,
        };
    }

    /// <summary>A growing, retained or released tag without an open incident; <paramref name="view"/> is its ranking, when there is one.</summary>
    public static CauseRow ForTag(TagStatus s, string what, TagViewData? view, DateTime nowUtc)
    {
        string much, fast = "", when;
        switch (s.State)
        {
            case TagState.Released:
                long fell = -Math.Min(Math.Min(s.Delta10m, s.Delta60m), 0);
                what = $"{what} was released";
                much = fell > 0 ? $"freed {Fmt.Bytes(fell)}" : "";
                when = s.StateSinceUtc is { } r ? PlainWords.When(r, nowUtc) : "just now";
                break;
            case TagState.Retained:
                much = $"{Fmt.Bytes(s.Bytes)} held";
                fast = "not falling";
                when = s.StateSinceUtc is { } since ? $"since {PlainWords.When(since, nowUtc)}" : "now";
                break;
            default:
                long grew = s.Delta60m > 0 ? s.Delta60m : Math.Max(0, s.Delta10m);
                much = grew > 0 ? $"+{Fmt.Bytes(grew)} (now {Fmt.Bytes(s.Bytes)})" : $"now {Fmt.Bytes(s.Bytes)}";
                fast = s.Delta10m > 0 ? PlainWords.PerHour(s.Delta10m, TimeSpan.FromMinutes(10)) : "";
                when = "now";
                break;
        }
        // The cause comes from the tag's ranking (built in the background, TagRankings): blank until there is one. A release
        // has no cause to name here (its incident report lists what exited then).
        string cause = "", conf = "";
        if (s.State != TagState.Released && view is not null)
        {
            if (CauseOf(view) is { } c) { cause = c.name; conf = PlainWords.Confidence(c.level); }
            else cause = "no clear cause yet";
        }
        return new CauseRow("tag:" + s.Tag.Raw.ToString("x8"), CauseKind.Tag, what, much, fast, cause, conf, when)
        {
            Active = s.State != TagState.Released, TimeUtc = s.StateSinceUtc ?? nowUtc,
            Tone = s.State == TagState.Released ? RowTone.Normal : RowTone.Warning, Tag = s.Tag,
        };
    }

    /// <summary>The top owner of a tag's ranking (the trace's first, when the trace backs it); null when nobody stood out.</summary>
    public static (string name, Confidence level)? CauseOf(TagViewData v)
    {
        var top = v.TraceBacked ? v.Owners.FirstOrDefault() : v.Owners.FirstOrDefault(o => o.Meaningful);
        return top is null || v.Answer.Growth <= 0 ? null : (top.Name, v.TraceBacked ? Confidence.ConfirmedByTrace : top.Confidence);
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
            much = $"+{Fmt.Bytes(dq)} (now {Fmt.Bytes(p.Quota)})";
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
        return new CauseRow($"proc:{p.Key.Pid}:{p.Key.CreateTime}", CauseKind.Process, what, much, fast, PlainWords.WithPid(p.Name, p.Key.Pid), conf,
            p.StartUtc > DateTime.MinValue ? $"started {PlainWords.When(p.StartUtc, nowUtc)}" : "")
        {
            Active = true, TimeUtc = nowUtc, Tone = RowTone.Warning, Process = p.Key, Candidate = p,
        };
    }

    /// <summary>A finding that explains pool growth or release (warning or alert), and every measured cache clear, from the last 24 hours.</summary>
    public static bool Shows(Finding f, DateTime nowUtc) =>
        nowUtc - f.TimeUtc <= FindingWindow && (f.Kind == FindingKind.CacheClear || (PoolKinds.Contains(f.Kind) && f.Severity >= FindingSeverity.Warning));

    public static CauseRow ForFinding(Finding f, DateTime nowUtc)
    {
        string what = f.Title, much = "";
        if (f.Kind == FindingKind.CacheClear)
        {
            int colon = f.Title.IndexOf(':');
            what = colon > 0 ? f.Title[..colon] : f.Title; // "Clear cache (standby list, manual)"
            if (f.Data.GetValueOrDefault("error") is null && f.Data.GetValueOrDefault("freedBytes") is long freed)
                much = $"freed {Fmt.Bytes(Math.Max(0, freed))}";
            else if (f.Data.GetValueOrDefault("error") is string) what += " failed";
        }
        string cause = f.Image is { Length: > 0 } image ? (f.Pid > 0 ? PlainWords.WithPid(image, f.Pid) : image) : "";
        return new CauseRow($"find:{f.TimeUtc.Ticks}:{f.Kind}:{f.Title}", CauseKind.Finding, what, much, "", cause, "", PlainWords.When(f.TimeUtc, nowUtc))
        {
            TimeUtc = f.TimeUtc, Folder = f.ReportDir, Finding = f,
            Tone = f.Severity switch { FindingSeverity.Alert => RowTone.Alert, FindingSeverity.Warning => RowTone.Warning, _ => RowTone.Normal },
        };
    }

    /// <summary>What is happening now first (open incidents, then growing tags, then processes), then the rest, newest first.</summary>
    public static List<CauseRow> Order(IEnumerable<CauseRow> rows) =>
        rows.OrderByDescending(r => r.Active)
            .ThenBy(r => r.Active ? r.Kind switch { CauseKind.Incident => 0, CauseKind.Tag => 1, _ => 2 } : 0)
            .ThenByDescending(r => r.TimeUtc)
            .ToList();

    // ---------------------------------------------------------------- reading the analysis

    /// <summary>All rows, ordered. <paramref name="ranked"/> holds the tag rankings (<see cref="TagRankings"/>).</summary>
    public static List<CauseRow> Build(AnalysisPipeline a, MonitorEngine engine, TagRankings ranked)
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
    /// </summary>
    public static PoolStatusLead? Lead(AnalysisPipeline a, TagRankings ranked, TimeSpan held, out PoolTag? rankTag)
    {
        rankTag = null;
        var now = DateTime.UtcNow;
        Incident? inc;
        (string name, Confidence level)? cause;
        lock (a.History.SyncRoot)
        {
            inc = a.Incidents.Open.Where(i => i.Kind is IncidentKind.Growth or IncidentKind.NonPagedTotal && i.Growth > 0).MaxBy(i => i.Growth);
            cause = inc is null ? null : UiData.CauseOf(inc);
        }
        if (inc is not null)
            return new PoolStatusLead(TagWords(inc.Tag, a.Knowledge.Lookup(inc.Tag)), inc.Growth, now - inc.OnsetUtc, cause?.name, cause?.level ?? Confidence.Weak);
        var openTags = new HashSet<string>(StringComparer.Ordinal);
        if (TopUnexplained(a, openTags) is not { } s) return null;
        long growth = s.Delta60m > 0 ? s.Delta60m : s.Delta10m;
        if (growth <= 0) return null;
        var over = s.Delta60m > 0 ? (held < TimeSpan.FromHours(1) ? held : TimeSpan.FromHours(1)) : TimeSpan.FromMinutes(Math.Min(10, Math.Max(1, held.TotalMinutes)));
        rankTag = s.Tag;
        var view = ranked.Get(s.Tag);
        var c = view is null ? null : CauseOf(view);
        return new PoolStatusLead(TagWords(s.Tag, a.Knowledge.Lookup(s.Tag)), growth, over, c?.name, c?.level ?? Confidence.Weak);
    }

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
/// has a cause and a summary to copy, until the oldest are dropped past <see cref="MaxCached"/> or the window hides and
/// drops them all (<see cref="Clear"/>). Thread-safe.
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
