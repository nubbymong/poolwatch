using PoolWatch.Analysis;
using PoolWatch.Core;
using PoolWatch.Core.Cache;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Growth;
using PoolWatch.Core.History;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;
using PoolWatch.Core.Util;

namespace PoolWatch.Ui;

public enum TrayState { Green, Amber, Red }

/// <summary>Immutable copies of what the UI shows, taken under the history lock so painting never holds it.</summary>
internal sealed record TagRow(PoolTag Tag, string Owner, string Description, long Bytes, long D1, long D10, long D60, TagState State, TagRates Rates);

internal sealed record ProcRow(ProcessKey Key, string Name, int ParentPid, long OpsPerSec, int Handles, long Quota, long Private, bool Orphan, string? CommandLine);

/// <summary>An incident (in memory, or an older one read from its report.json).</summary>
/// <param name="Top">The most likely cause: the trace's top owner, else the top suspect owner, else the top process; "" when none.</param>
/// <param name="Confidence">The cause's confidence in words ("strong evidence").</param>
internal sealed record IncidentRow(string Id, string Dir, DateTime OpenedUtc, string Tag, IncidentKind Kind, long Growth, string Top, string Confidence, bool Open)
{
    public Confidence? Level { get; init; }
    /// <summary>When the growth began, and when it stopped (or the incident closed); null when not known.</summary>
    public DateTime? OnsetUtc { get; init; }
    public DateTime? EndUtc { get; init; }
    /// <summary>For a release: how much the tag dropped.</summary>
    public long Dropped { get; init; }
}

internal sealed record ChartPoint(DateTime TimeUtc, PoolTotals Totals);

/// <summary>The committed pool total now and how it moved, for the status line.</summary>
/// <param name="Clear">
/// PoolWatch's latest cache clear in the last hour, if any. The changes are measured on the detector's baseline across
/// PoolWatch's own clears (<see cref="ClearSpans"/>): each clear's drop is added back, so the drop it caused is said as its
/// result (<see cref="ChartClear.Result"/>), never as the pool falling; growth after it counts from the level it left and
/// growth before it still counts, as in the tag rows.
/// </param>
internal sealed record TotalsNow(long? Total, TimeSpan Held, long Change10Min, long? ChangeHour, ChartClear? Clear = null)
{
    /// <summary>
    /// What PoolWatch's own clears of the last hour freed of the pool, as each was measured (those that freed nothing, under
    /// <see cref="ClearScheduleText.NothingBelow"/>, left out), with the one clear's time when one did (null for several):
    /// what <see cref="ChangeHour"/> leaves out. Zero when none did.
    /// </summary>
    public (long Bytes, DateTime? OneAtUtc) ClearedHour { get; init; }

    /// <summary>When the clear still being measured ran, or null: no change across it is known until it is (the changes read 0).</summary>
    public DateTime? MeasuringUtc { get; init; }
}

internal static class UiData
{
    /// <summary>The tray icon's colour now, from the open incidents and the tags' states (the rule is below).</summary>
    public static TrayState State(AnalysisPipeline a)
    {
        lock (a.History.SyncRoot)
            return State(a.Incidents.Open.Select(i => (i.Kind, CauseOf(i)?.level)), a.Detector.Status.Values.Any(s => s.State == TagState.Retained));
    }

    /// <summary>
    /// The tray icon's colour, calm on purpose: red only for a real risk (an open incident of the non-paged pool over its
    /// limit and still rising, or an open growth whose cause a trace confirmed); amber while another incident is open or a
    /// tag keeps memory after its activity stopped; green otherwise, also while a tag grows for a while (watched, not alarming).
    /// </summary>
    /// <param name="open">Each open incident's kind and its likely cause's confidence (null while none is known).</param>
    /// <param name="retained">A tag keeps its memory after the activity that grew it stopped.</param>
    internal static TrayState State(IEnumerable<(IncidentKind Kind, Confidence? Cause)> open, bool retained)
    {
        bool anyOpen = false;
        foreach (var (kind, cause) in open)
        {
            if (kind == IncidentKind.NonPagedTotal || cause == Confidence.ConfirmedByTrace) return TrayState.Red;
            anyOpen = true;
        }
        return anyOpen || retained ? TrayState.Amber : TrayState.Green;
    }

    /// <summary>The Simple view's line about clearing: when the automatic clear runs next, and what the last clear freed.</summary>
    public static string NextClearLine(AnalysisPipeline a)
    {
        var cache = a.Diagnostics.Cache;
        var nowUtc = DateTime.UtcNow;
        return ClearScheduleText.Line(cache.Next(nowUtc), cache.History(), LatestTotals(a), nowUtc);
    }

    /// <summary>What the status line adds to a growth: "The next cache clear is in 12 min."; null when no clear is scheduled.</summary>
    public static string? NextClearContext(AnalysisPipeline a)
    {
        var nowUtc = DateTime.UtcNow;
        return ClearScheduleText.Context(a.Diagnostics.Cache.Next(nowUtc), nowUtc);
    }

    /// <summary>The cache clears of the last <paramref name="span"/> that ran, for the graph's marks.</summary>
    public static List<ChartClear> Clears(AnalysisPipeline a, TimeSpan span)
    {
        var from = DateTime.UtcNow - span;
        return a.Diagnostics.Cache.History().Where(r => r.Error is null && r.TimeUtc >= from).Select(ChartClear.Of).ToList();
    }

    private static PoolTotals LatestTotals(AnalysisPipeline a)
    {
        lock (a.History.SyncRoot) return a.History.Count > 0 ? a.History.Totals[^1] : default;
    }

    public static List<TagRow> Tags(AnalysisPipeline a, bool growingOnly, int max = 200)
    {
        List<TagStatus> status;
        lock (a.History.SyncRoot) status = a.Detector.Status.Values.ToList();
        return status
            .Where(s => !growingOnly || s.State != TagState.Normal)
            .OrderByDescending(s => s.State != TagState.Normal).ThenByDescending(s => s.Delta10m).ThenByDescending(s => s.Bytes)
            .Take(max)
            .Select(s => { var i = a.Knowledge.Lookup(s.Tag); return new TagRow(s.Tag, i.Owner, i.Description, s.Bytes, s.Delta1m, s.Delta10m, s.Delta60m, s.State, s.Rates); })
            .ToList();
    }

    /// <summary>Live suspects for a selected tag: the correlation over the last 10 minutes.</summary>
    public static (List<Suspect> suspects, List<LifecycleEvent> events) Suspects(AnalysisPipeline a, ProcessRegistry registry, PoolTag tag)
    {
        lock (a.History.SyncRoot)
        {
            var h = a.History;
            if (h.Count < 3) return ([], []);
            var now = h.Times[^1];
            var from = now.AddMinutes(-10) < h.Times[0] ? h.Times[0] : now.AddMinutes(-10);
            var series = h.TagSeries(tag, h.IndexAtOrAfter(from));
            var cp = Detector.FindChangePoints(series);
            int i0 = h.IndexAtOrAfter(from);
            var onset = h.Times[Math.Min(h.Count - 1, i0 + cp.OnsetIndex)];
            DateTime? end = cp.EndIndex is { } e ? h.Times[Math.Min(h.Count - 1, i0 + e)] : null;
            var suspects = Correlator.Rank(h, registry, tag, new CorrelationWindow(from, onset, end, now));
            return (suspects, a.Incidents.EventsBetween(from, now));
        }
    }

    /// <summary>A shared runtime without a label yet (labels are set once a minute) gets its identity on demand.</summary>
    private static string DisplayOf(ProcessRecord rec, ProcessRegistry registry) =>
        rec.Label is null && Core.Identity.ProcessIdentities.SharedRuntimes.Contains(rec.Image) && registry.Ancestry(rec.Key) is { Count: > 0 } chain
            ? Core.Identity.ProcessIdentities.Of(chain, useLabel: false).Display
            : rec.DisplayName;

    public static List<ProcRow> Processes(Sample? latest, Sample? previous, ProcessRegistry registry)
    {
        if (latest is null) return [];
        var prev = previous?.Processes.ToDictionary(p => p.Key) ?? new();
        double secs = previous is null ? 1 : Math.Max(1, (latest.TimeUtc - previous.TimeUtc).TotalSeconds);
        return latest.Processes.Select(p =>
        {
            var rec = registry.Get(p.Key);
            long ops = prev.TryGetValue(p.Key, out var q) ? (long)((p.TotalOperations - q.TotalOperations) / secs) : 0;
            return new ProcRow(p.Key, rec is null ? p.ImageName : DisplayOf(rec, registry), p.ParentPid, Math.Max(0, ops), p.HandleCount,
                (long)(p.QuotaPagedPoolUsage + p.QuotaNonPagedPoolUsage), (long)p.PrivateBytes, rec?.IsOrphan ?? false, rec?.CommandLine);
        }).ToList();
    }

    /// <summary>Rows read from disk, keyed by folder and report.json's write time (a rewritten report is read again).</summary>
    /// <summary>A null row: a finding report (spawn storm, ...), shown from the finding itself instead.</summary>
    private static readonly Dictionary<string, (DateTime written, IncidentRow? row)> DiskRows = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The most likely cause of an in-memory incident: the trace's top owner, else the top suspect owner, else the top process.</summary>
    public static (string name, Confidence level)? CauseOf(Incident i)
    {
        if (i.TraceOwners is { Count: > 0 } traced) return (traced[0].Owner.Name, Confidence.ConfirmedByTrace);
        if (i.Owners?.FirstOrDefault(o => o.Meaningful) is { } owner) return (owner.Name, owner.Confidence);
        if (i.Suspects.FirstOrDefault() is { } top) return (PlainWords.WithPid(top.DisplayName, top.Key.Pid), top.Confidence);
        return null;
    }

    /// <summary>
    /// The incidents\ folder's rows as last listed, and what they were listed at: the folder is listed again only when an
    /// incident or finding report was added, an incident left memory, the folder changed, or 5 minutes passed.
    /// </summary>
    private static (string Dir, int Created, int InMemory, int Findings, DateTime Utc, List<IncidentRow> Rows)? _listed;
    private static readonly TimeSpan ListAgainAfter = TimeSpan.FromMinutes(5);

    /// <summary>In-memory incidents plus older ones still on disk (the manager forgets them after 6 h or a restart). UI thread.</summary>
    public static List<IncidentRow> Incidents(AnalysisPipeline a, string incidentsDir)
    {
        List<IncidentRow> live;
        int created;
        lock (a.History.SyncRoot)
        {
            live = a.Incidents.All.Select(i =>
            {
                var cause = CauseOf(i);
                return new IncidentRow(i.Id, i.Dir, i.OpenedUtc, i.Tag.ToDisplay(), i.Kind, i.Growth, cause?.name ?? "",
                    cause is { } c ? PlainWords.Confidence(c.level) : "", i.IsOpen)
                {
                    Level = cause?.level, OnsetUtc = i.OnsetUtc, EndUtc = i.EndUtc ?? i.ClosedUtc, Dropped = i.Release?.Dropped ?? 0,
                };
            }).ToList();
            created = a.Incidents.CreatedCount;
        }
        var known = new HashSet<string>(live.Select(r => r.Dir), StringComparer.OrdinalIgnoreCase);
        int inMemory = live.Count, findings = a.Diagnostics.Findings.Count;
        var nowUtc = DateTime.UtcNow;
        if (_listed is not { } listed || listed.Dir != incidentsDir || listed.Created != created || listed.InMemory != inMemory
            || listed.Findings != findings || nowUtc - listed.Utc >= ListAgainAfter)
            _listed = listed = (incidentsDir, created, inMemory, findings, nowUtc, ListDisk(incidentsDir, known));
        live.AddRange(listed.Rows.Where(r => !known.Contains(r.Dir)));
        return live.OrderByDescending(r => r.OpenedUtc).ToList();
    }

    /// <summary>The incident folders on disk that are not in memory; rows of folders no longer there are dropped from the cache.</summary>
    private static List<IncidentRow> ListDisk(string incidentsDir, HashSet<string> known)
    {
        var rows = new List<IncidentRow>();
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Directory.Exists(incidentsDir))
                foreach (var dir in Directory.EnumerateDirectories(incidentsDir).OrderByDescending(d => d, StringComparer.Ordinal).Take(500))
                {
                    listed.Add(dir);
                    if (known.Contains(dir)) continue;
                    var written = File.GetLastWriteTimeUtc(Path.Combine(dir, "report.json"));
                    if (DiskRows.TryGetValue(dir, out var cached) && cached.written == written) { if (cached.row is { } c) rows.Add(c); continue; }
                    var (ok, row) = ReadRow(dir);
                    if (ok) DiskRows[dir] = (written, row); // a failed read is retried next time
                    if (row is not null) rows.Add(row);
                }
        }
        catch (IOException) { }
        foreach (var gone in DiskRows.Keys.Where(d => !listed.Contains(d)).ToList()) DiskRows.Remove(gone);
        return rows;
    }

    private static (bool ok, IncidentRow? row) ReadRow(string dir)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "report.json")));
            var r = doc.RootElement;
            if (r.TryGetProperty("severity", out _)) return (true, null); // a finding report
            string top = "";
            Confidence? level = null;
            if (r.TryGetProperty("traceOwners", out var to) && to.ValueKind == System.Text.Json.JsonValueKind.Array && to.GetArrayLength() > 0 &&
                to[0].TryGetProperty("owner", out var owner) && owner.GetString() is { Length: > 0 } name)
            {
                top = name;
                level = Confidence.ConfirmedByTrace;
            }
            else if (r.TryGetProperty("suspects", out var s) && s.GetArrayLength() > 0)
            {
                top = PlainWords.WithPid(s[0].GetProperty("displayName").GetString() ?? "?", s[0].GetProperty("pid").GetInt32());
                if (Enum.TryParse<Confidence>(s[0].GetProperty("confidence").GetString(), out var c)) level = c;
            }
            Enum.TryParse<IncidentKind>(r.GetProperty("kind").GetString(), out var kind);
            DateTime? Time(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String && v.TryGetDateTime(out var t) ? t.ToUniversalTime() : null;
            long dropped = r.TryGetProperty("release", out var rel) && rel.ValueKind == System.Text.Json.JsonValueKind.Object &&
                           rel.TryGetProperty("dropped", out var d) && d.TryGetInt64(out var db) ? db : 0;
            return (true, new IncidentRow(r.GetProperty("id").GetString() ?? Path.GetFileName(dir), dir, r.GetProperty("openedUtc").GetDateTime().ToUniversalTime(),
                (r.GetProperty("tag").GetString() ?? "").TrimEnd(), kind, r.GetProperty("growthBytes").GetInt64(), top, level is { } l ? PlainWords.Confidence(l) : "", false)
            {
                Level = level, OnsetUtc = Time("onsetUtc"), EndUtc = Time("endUtc") ?? Time("closedUtc"), Dropped = dropped,
            });
        }
        catch { return (false, null); } // still being written, or not an incident folder
    }

    /// <summary>The growth and clear stats of the last <paramref name="span"/> (<see cref="GrowthLedger"/>).</summary>
    public static GrowthWindow Growth(AnalysisPipeline a, TimeSpan span)
    {
        lock (a.History.SyncRoot) return a.Growth.Window(span);
    }

    /// <summary>The graph's RAM columns: a minute wide for the last hour, 5 minutes for 6 hours, a quarter hour for 24 hours.</summary>
    public static TimeSpan RamColumnWidth(TimeSpan span) =>
        span <= TimeSpan.FromHours(1) ? TimeSpan.FromMinutes(1) : span <= TimeSpan.FromHours(6) ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(15);

    /// <summary>The graph's RAM columns over <paramref name="span"/> up to the newest sample, and the PC's RAM.</summary>
    public static (List<RamColumn> Columns, long TotalBytes) Ram(AnalysisPipeline a, TimeSpan span)
    {
        DateTime now;
        lock (a.History.SyncRoot) now = a.History.Count > 0 ? a.History.Times[^1] : DateTime.UtcNow;
        return a.Diagnostics.Cache.Ram(now, span, RamColumnWidth(span));
    }

    /// <summary>Memory now, for the Simple view's tile: the newest minute's RAM in use and cache, and the PC's RAM; null before the first reading.</summary>
    public static RamNow? MemoryNow(AnalysisPipeline a)
    {
        var (columns, total) = Ram(a, TimeSpan.FromMinutes(2));
        return columns.Count > 0 && total > 0 ? new RamNow(columns[^1].InUse, columns[^1].Cache, total) : null;
    }

    /// <summary>When PoolWatch started (its first sample): the graph's band before it. Null before the first sample.</summary>
    public static DateTime? StartedUtc(AnalysisPipeline a)
    {
        lock (a.History.SyncRoot) return a.Growth.StartedUtc;
    }

    /// <summary>The newest sample's paged and non-paged pool (the header's split); default before the first sample.</summary>
    public static PoolTotals Latest(AnalysisPipeline a) => LatestTotals(a);

    /// <summary>Pool totals for the chart: minute averages for older data, raw samples for the last hour.</summary>
    public static List<ChartPoint> Chart(AnalysisPipeline a, TimeSpan span)
    {
        lock (a.History.SyncRoot)
        {
            var h = a.History;
            if (h.Count == 0) return [];
            var now = h.Times[^1];
            var cutoff = now - span;
            var rawStart = h.Times[0];
            var pts = h.Minutes.Where(m => m.TimeUtc >= cutoff && m.TimeUtc < rawStart).Select(m => new ChartPoint(m.TimeUtc, m.Totals)).ToList();
            int step = Math.Max(1, h.Count / 600);
            for (int i = h.IndexAtOrAfter(cutoff); i < h.Count; i += step) pts.Add(new ChartPoint(h.Times[i], h.Totals[i]));
            return pts;
        }
    }

    /// <summary>
    /// For the status line: null while the PC is not saturated (the saturation detector: all cores busy for a minute);
    /// while it is, the busiest process's image, or "" when none uses a fifth of a core.
    /// </summary>
    public static string? SaturatedBusiest(AnalysisPipeline a)
    {
        lock (a.History.SyncRoot)
        {
            var d = a.Diagnostics;
            if (!d.Saturation.Saturated) return null;
            return d.Load?.Processes.FirstOrDefault() is { Cores: >= 0.2 } top ? top.Image : "";
        }
    }

    /// <summary>
    /// The committed total now, how far back the raw samples go, and its change over 10 minutes and an hour, on the
    /// detector's baseline across PoolWatch's own clears (<see cref="ClearSpans"/>: each clear's drop added back, a span
    /// that starts inside a clear's interval starting at its measurement; 0 while a clear is being measured), so the status
    /// line's total and its tag rows never measure from two baselines.
    /// </summary>
    public static TotalsNow Totals(AnalysisPipeline a)
    {
        var clears = a.Diagnostics.Cache.History();
        lock (a.History.SyncRoot) return Totals(a.History, clears);
    }

    /// <summary><see cref="Totals(AnalysisPipeline)"/> from a history (the caller holds its lock) and PoolWatch's clears.</summary>
    internal static TotalsNow Totals(History h, IReadOnlyList<ClearRecord> clears)
    {
        if (h.Count == 0) return new TotalsNow(null, TimeSpan.Zero, 0, null);
        static long Sum(PoolTotals t) => (long)(t.CommittedPaged + t.CommittedNonPaged);
        long total = Sum(h.Totals[^1]);
        var now = h.Times[^1];
        var held = now - h.Times[0];
        var own = new OwnClears(clears, CacheClearer.MeasureAfter);
        var measuringClear = own.Measuring(now);
        bool measuring = measuringClear is not null;
        var spans = own.SpansIn(h);
        int from = h.IndexAtOrAfter(now - TimeSpan.FromHours(1));
        var series = new long[h.Count - from];
        for (int i = from; i < h.Count; i++) series[i - from] = Sum(h.Totals[i]);
        spans.Stitch(series, from);
        long Change(TimeSpan span) => measuring ? 0 : series[^1] - series[spans.Start(h.IndexAtOrAfter(now - span)) - from];
        var clear = clears.LastOrDefault(r => r.Error is null && r.TimeUtc > now - TimeSpan.FromHours(1));
        // A clear that failed, or is not measured yet, has no level after it, so its record frees nothing: it is left out with
        // the clears that freed nothing.
        var freed = clears.Where(r => r.Error is null && r.TimeUtc > now - TimeSpan.FromHours(1) && r.TimeUtc <= now && r.Freed >= ClearScheduleText.NothingBelow).ToList();
        return new TotalsNow(total, held, Change(TimeSpan.FromMinutes(10)), held.TotalMinutes >= 55 ? Change(TimeSpan.FromHours(1)) : null,
            clear is null ? null : ChartClear.Of(clear))
        {
            ClearedHour = (freed.Sum(r => r.Freed), freed.Count == 1 ? freed[0].TimeUtc : null),
            MeasuringUtc = measuringClear?.TimeUtc,
        };
    }
}
