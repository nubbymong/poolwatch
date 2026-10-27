using PoolWatch.Analysis;
using PoolWatch.Core;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Detection;
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
internal sealed record TotalsNow(long? Total, TimeSpan Held, long Change10Min, long? ChangeHour);

internal static class UiData
{
    public static TrayState State(AnalysisPipeline a)
    {
        lock (a.History.SyncRoot)
        {
            if (a.Incidents.Open.Count > 0) return TrayState.Red;
            return a.Detector.Status.Values.Any(s => s.State is TagState.Growing or TagState.Retained) ? TrayState.Amber : TrayState.Green;
        }
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
        if (i.Suspects.FirstOrDefault() is { } top) return ($"{top.DisplayName} ({top.Key.Pid})", top.Confidence);
        return null;
    }

    /// <summary>In-memory incidents plus older ones still on disk (the manager forgets them after 6 h or a restart).</summary>
    public static List<IncidentRow> Incidents(AnalysisPipeline a, string incidentsDir)
    {
        List<IncidentRow> live;
        lock (a.History.SyncRoot)
            live = a.Incidents.All.Select(i =>
            {
                var cause = CauseOf(i);
                return new IncidentRow(i.Id, i.Dir, i.OpenedUtc, i.Tag.ToDisplay(), i.Kind, i.Growth, cause?.name ?? "",
                    cause is { } c ? PlainWords.Confidence(c.level) : "", i.IsOpen)
                {
                    Level = cause?.level, OnsetUtc = i.OnsetUtc, EndUtc = i.EndUtc ?? i.ClosedUtc, Dropped = i.Release?.Dropped ?? 0,
                };
            }).ToList();
        var known = new HashSet<string>(live.Select(r => r.Dir), StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Directory.Exists(incidentsDir))
                foreach (var dir in Directory.EnumerateDirectories(incidentsDir).OrderByDescending(d => d, StringComparer.Ordinal).Take(500))
                {
                    if (known.Contains(dir)) continue;
                    var written = File.GetLastWriteTimeUtc(Path.Combine(dir, "report.json"));
                    if (DiskRows.TryGetValue(dir, out var cached) && cached.written == written) { if (cached.row is { } c) live.Add(c); continue; }
                    var (ok, row) = ReadRow(dir);
                    if (ok) DiskRows[dir] = (written, row); // a failed read is retried next time
                    if (row is not null) live.Add(row);
                }
        }
        catch (IOException) { }
        return live.OrderByDescending(r => r.OpenedUtc).ToList();
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
                top = $"{s[0].GetProperty("displayName").GetString()} ({s[0].GetProperty("pid").GetInt32()})";
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

    /// <summary>The committed total now, how far back the raw samples go, and its change over 10 minutes and an hour.</summary>
    public static TotalsNow Totals(AnalysisPipeline a)
    {
        lock (a.History.SyncRoot)
        {
            var h = a.History;
            if (h.Count == 0) return new TotalsNow(null, TimeSpan.Zero, 0, null);
            static long Sum(PoolTotals t) => (long)(t.CommittedPaged + t.CommittedNonPaged);
            long total = Sum(h.Totals[^1]);
            var now = h.Times[^1];
            var held = now - h.Times[0];
            long Change(TimeSpan span) => total - Sum(h.Totals[Math.Min(h.IndexAtOrAfter(now - span), h.Count - 1)]);
            return new TotalsNow(total, held, Change(TimeSpan.FromMinutes(10)), held.TotalMinutes >= 55 ? Change(TimeSpan.FromHours(1)) : null);
        }
    }
}
