using PoolWatch.Core.Incidents;
using System.Text;
using PoolWatch.Analysis;
using PoolWatch.Core;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Identity;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;
using PoolWatch.Core.Util;

namespace PoolWatch.Ui;

/// <summary>One owner's I/O per sample over the tag view's window (the activity strip).</summary>
internal sealed record ActivityBand(string Owner, double[] Ops);

/// <summary>Everything the selected tag's view shows, copied under the history lock.</summary>
internal sealed record TagViewData(PoolTag Tag, string Title, TagAnswer Answer, IReadOnlyList<OwnerSuspect> Owners, IReadOnlyList<EventRow> Events,
    DateTime FromUtc, DateTime ToUtc, IReadOnlyList<DateTime> Times, IReadOnlyList<long> TagSeries, IReadOnlyList<ActivityBand> Bands, DateTime OnsetUtc)
{
    /// <summary>The answer comes from a confirmation trace; the owners listed below it are correlation only (2a).</summary>
    public bool TraceBacked { get; init; }
}

/// <summary>An events-list row: a single notable event, or a summary of many short commands.</summary>
/// <param name="Key">Stable across refreshes (so the list is updated in place): the event's identity, or minute + owner for a summary.</param>
internal sealed record EventRow(DateTime TimeUtc, string Text, bool Notable, string Key);

internal sealed record OverviewData(string Text, IReadOnlyList<ChartPoint> SinceBoot);

internal static class TagViewBuilder
{
    /// <summary>The selected tag over the last hour (or what the raw history holds), ranked by owner, in plain words.</summary>
    public static TagViewData? Build(AnalysisPipeline a, ProcessRegistry registry, PoolTag tag, bool allEvents, IReadOnlyList<LifecycleEvent> rawEvents)
    {
        lock (a.History.SyncRoot)
        {
            var h = a.History;
            if (h.Count < 3) return null;
            var now = h.Times[^1];
            var from = now.AddMinutes(-60) < h.Times[0] ? h.Times[0] : now.AddMinutes(-60);
            int i0 = h.IndexAtOrAfter(from);
            var series = h.TagSeries(tag, i0);
            var cp = Detector.FindChangePoints(series);
            var onset = h.Times[Math.Min(h.Count - 1, i0 + cp.OnsetIndex)];
            DateTime? end = cp.EndIndex is { } e ? h.Times[Math.Min(h.Count - 1, i0 + e)] : null;
            var window = new CorrelationWindow(from, onset, end, now);
            // Every active process, so an owner made of many small ones (a session's short commands) is grouped before any cut.
            var suspects = Correlator.Rank(h, registry, tag, window, maxSuspects: 2000);
            long growth = Detector.GrowthSinceOnset(series, cp.OnsetIndex); // total: 0 for an empty series, onset clamped
            var owners = OwnerRanking.Rank(h, suspects, tag, window, growth, s => ProcessIdentities.Of(registry.Ancestry(s.Key)));
            var info = a.Knowledge.Lookup(tag);
            var (p, np) = h.TagAt(tag, h.Count - 1);
            string span = (now - from).TotalMinutes >= 55 ? "the last hour" : $"the last {Math.Max(1, (int)(now - from).TotalMinutes)} min";
            var answer = OwnerRanking.Answer(info, tag, p, np, growth, span, owners);
            // 2a: a confirmation trace of this tag's current (or last hour's) incident outranks correlation.
            var traced = a.Incidents.All.LastOrDefault(i => i.Tag == tag && i.TraceOwners is { Count: > 0 } && i.Capture is not null &&
                                                            (i.IsOpen || now - i.ClosedUtc!.Value < TimeSpan.FromMinutes(IncidentManager.ReopenMinutes)));
            bool traceBacked = false;
            if (traced is not null && TraceAttribution.Answer(traced, info, traced.TraceOwners!, traced.Capture!) is { } line)
            {
                answer = answer with { Line = line + " (confirmation trace, " + Fmt.Clock(traced.Capture!.StartUtc) + ")" };
                traceBacked = true;
                // 2a: the ranking follows the trace too (its owners by net bytes first), correlation-only owners below.
                owners = TraceAttribution.Rank(traced.TraceOwners!, owners, tag);
            }
            string title = $"{tag.ToDisplay()} · {info.Description} ({info.Owner}): {Fmt.Bytes(p + np)} now, {Fmt.SignedBytes(growth)} in {span}";

            var times = new List<DateTime>();
            for (int i = i0; i < h.Count; i++) times.Add(h.Times[i]);
            var bands = new List<ActivityBand>();
            foreach (var o in owners.Where(o => o.Meaningful && o.Members.Count > 0).Take(4))
            {
                var ops = new double[times.Count];
                foreach (var m in o.Members)
                {
                    var pts = h.ProcessSeries(m.Key, i0);
                    for (int i = 1; i < ops.Length && i < pts.Length; i++)
                        if (pts[i - 1] is { } x && pts[i] is { } y) ops[i] += Math.Max(0, y.TotalOps - x.TotalOps);
                }
                bands.Add(new ActivityBand(o.Name, ops));
            }
            var events = EventRows(allEvents ? rawEvents.Where(ev => ev.TimeUtc >= from).ToList() : a.Incidents.EventsBetween(from, now), registry, allEvents);
            return new TagViewData(tag, title, answer, owners, events, from, now, times, series, bands, onset) { TraceBacked = traceBacked };
        }
    }

    /// <summary>
    /// Notable events one per row; short-lived process rollups (and, with "all events", raw starts / exits) summarised per
    /// owner per minute: "05:29:56-05:29:57 Claude Code session x ran 38 short commands: bash, grep, git, sed".
    /// </summary>
    public static List<EventRow> EventRows(IReadOnlyList<LifecycleEvent> events, ProcessRegistry registry, bool all)
    {
        var rows = new List<EventRow>();
        var groups = new Dictionary<(DateTime minute, string owner), (DateTime first, DateTime last, int count, SortedSet<string> images)>();
        foreach (var e in events)
        {
            bool shortCommand = e.Kind == LifecycleKind.Rollup || (all && ShortLived(e, registry));
            if (!shortCommand)
            {
                rows.Add(new EventRow(e.TimeUtc, Describe(e, registry), true, RowKeys.ForEvent(e)));
                continue;
            }
            if (e.Kind == LifecycleKind.Exit) continue; // counted with its start
            string owner = e.Owner ?? (registry.GetLive(e.ParentPid) is { } parent && parent.StartUtc <= e.TimeUtc
                ? ProcessIdentities.OwnerOf(registry.Ancestry(parent.Key)).Name : registry.RecentImage(e.ParentPid) ?? $"PID {e.ParentPid}");
            int n = e.Kind == LifecycleKind.Rollup ? RollupCount(e.Detail) : 1;
            var minute = new DateTime(e.TimeUtc.Ticks - e.TimeUtc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
            var key = (minute, owner);
            if (!groups.TryGetValue(key, out var g)) g = (e.TimeUtc, e.TimeUtc, 0, new SortedSet<string>(StringComparer.OrdinalIgnoreCase));
            g.images.Add(Bare(e.Image));
            groups[key] = (Min(g.first, e.TimeUtc), Max(g.last, e.TimeUtc), g.count + n, g.images);
        }
        foreach (var ((minute, owner), g) in groups)
            rows.Add(new EventRow(g.first, $"{Fmt.Clock(g.first)}-{Fmt.Clock(g.last)} {owner} ran {g.count:N0} short command{(g.count == 1 ? "" : "s")}: {string.Join(", ", g.images.Take(8))}{(g.images.Count > 8 ? ", …" : "")}", false,
                $"sum:{minute.Ticks}:{owner}"));
        // C5: two events with the same time, kind, PID and image (app events: PID 0) shared a key and one replaced the
        // other in the list. The key now carries the detail, and identical rows are numbered (stable: OrderBy is stable).
        var ordered = rows.OrderBy(r => r.TimeUtc).ToList();
        var keys = RowKeys.Unique(ordered.Select(r => r.Key));
        for (int i = 0; i < ordered.Count; i++) if (!ReferenceEquals(keys[i], ordered[i].Key)) ordered[i] = ordered[i] with { Key = keys[i] };
        return ordered;
    }

    /// <summary>A raw start or exit of a process that ran less than 30 s (known from its record or its exit event).</summary>
    private static bool ShortLived(LifecycleEvent e, ProcessRegistry registry)
    {
        if (e.Kind == LifecycleKind.Exit) return e.CreateTime > 0 && e.TimeUtc - e.Key.CreateTimeUtc < EventSummarizer.LongLived;
        if (e.Kind != LifecycleKind.Start) return false;
        var rec = registry.Get(e.Key);
        return rec?.ExitUtc is { } x && x - e.TimeUtc < EventSummarizer.LongLived;
    }

    private static string Describe(LifecycleEvent e, ProcessRegistry registry)
    {
        var rec = e.Kind == LifecycleKind.Start ? registry.Get(e.Key) : null;
        string what = e.Kind switch
        {
            LifecycleKind.Start => rec?.ExitUtc is { } x ? $"{e.Image} (PID {e.Pid}) started (ran {Fmt.Duration(x - e.TimeUtc)})" : $"{e.Image} (PID {e.Pid}) started and is still running",
            LifecycleKind.Exit => $"{e.Image} (PID {e.Pid}) exited{(e.ExitCode is { } c ? $" with code {c}" : "")}" +
                                  (e.CreateTime > 0 ? $" after {Fmt.Duration(e.TimeUtc - e.Key.CreateTimeUtc)}" : ""),
            LifecycleKind.Orphaned => $"{e.Image} (PID {e.Pid}) kept running after its parent exited",
            LifecycleKind.Alert => $"Alert: {e.Image} {e.Detail}",
            LifecycleKind.Release => $"Release: {e.Image} {e.Detail}",
            LifecycleKind.Finding => e.Detail ?? e.Image,
            _ => $"{e.Kind} {e.Image}{(e.Pid > 0 ? $" (PID {e.Pid})" : "")}{(e.Detail is { } d ? ": " + d : "")}",
        };
        return $"{Fmt.Clock(e.TimeUtc)} {what}";
    }

    /// <summary>The start count from a rollup's detail ("bash.exe (500) started 19, ...").</summary>
    private static int RollupCount(string? detail)
    {
        if (detail is null) return 1;
        int i = detail.IndexOf(" started ", StringComparison.Ordinal);
        if (i < 0) return 1;
        var digits = new StringBuilder();
        foreach (char c in detail[(i + 9)..]) { if (char.IsDigit(c)) digits.Append(c); else break; } // written with the invariant culture, no separators
        return int.TryParse(digits.ToString(), out int n) ? n : 1;
    }

    private static string Bare(string image) => image.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? image[..^4] : image;
    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    /// <summary>The Overview page: pool now vs shortly after boot, how fast it rises, lag, the top growing tags.</summary>
    public static OverviewData Overview(AnalysisPipeline a)
    {
        lock (a.History.SyncRoot)
        {
            var h = a.History;
            var sb = new StringBuilder();
            if (h.Count == 0) return new OverviewData("Collecting the first sample…", []);
            var now = h.Totals[^1];
            long total = (long)(now.CommittedPaged + now.CommittedNonPaged);
            sb.AppendLine($"Kernel pool now: {Fmt.Bytes(total)} (paged {Fmt.Bytes((long)now.CommittedPaged)}, non-paged {Fmt.Bytes((long)now.CommittedNonPaged)}; as Task Manager shows).");
            var boot = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
            var first = h.Minutes.Count > 0 ? h.Minutes[0] : null;
            var firstTotals = first?.Totals ?? h.Totals[0];
            var firstTime = first?.TimeUtc ?? h.Times[0];
            long firstTotal = (long)(firstTotals.CommittedPaged + firstTotals.CommittedNonPaged);
            if (firstTime - boot <= TimeSpan.FromMinutes(30))
                sb.AppendLine($"Shortly after boot ({Fmt.Duration(firstTime - boot)} in) it was {Fmt.Bytes(firstTotal)}: now {Fmt.SignedBytes(total - firstTotal)} above that.");
            else
                sb.AppendLine($"Usual level shortly after boot: not known (PoolWatch started {Fmt.Duration(firstTime - boot)} after boot; start it at logon to measure it). " +
                              $"Since {Fmt.Clock(firstTime)}: {Fmt.SignedBytes(total - firstTotal)}.");
            long Rise(TimeSpan span)
            {
                int i = h.IndexAtOrAfter(h.Times[^1] - span);
                var t = h.Totals[Math.Min(i, h.Count - 1)];
                return total - (long)(t.CommittedPaged + t.CommittedNonPaged);
            }
            double minutesHeld = (h.Times[^1] - h.Times[0]).TotalMinutes;
            sb.AppendLine($"Rising: {Fmt.SignedBytes(Rise(TimeSpan.FromMinutes(10)))} in the last 10 min" +
                          (minutesHeld >= 55 ? $", {Fmt.SignedBytes(Rise(TimeSpan.FromHours(1)))} in the last hour." : "."));
            var sys = h.System[^1];
            if (sys.CommitLimitBytes > 0)
                sb.AppendLine($"Memory: commit {Fmt.Bytes((long)sys.CommitBytes)} of {Fmt.Bytes((long)sys.CommitLimitBytes)} ({100.0 * sys.CommitBytes / sys.CommitLimitBytes:0}%), " +
                              $"{Fmt.Bytes((long)sys.AvailableBytes)} RAM available, compression store {Fmt.Bytes((long)sys.CompressionWsBytes)}; {sys.Handles:N0} handles, {sys.Processes:N0} processes.");
            lock (a.Diagnostics.Lag)
                sb.AppendLine(a.Diagnostics.Lag.Summary() + (a.Diagnostics.LagProbePaused ? " (probe paused while PoolWatch's own trace or cache clear runs, and 30 s after)" : ""));
            if (a.Diagnostics.ExperimentStatus() is { Length: > 0 } xs) sb.AppendLine(xs);
            if (a.Boots.PreviousLine() is { Length: > 0 } pb) sb.AppendLine(pb);
            // Addendum 2: Task Manager's "available" includes the standby (file) cache; the per-file pool records live as long as it does.
            var lists = a.Diagnostics.Cache.Lists;
            if (lists.Known) sb.AppendLine($"Memory lists: {lists.Text}; kernel pool {Fmt.Bytes(total)}.");
            if (a.Diagnostics.Cache.StatusLine(now) is { Length: > 0 } clear) sb.AppendLine("Clear cache: " + clear);
            // Addendum 3: the token-leak guard (workaround on / off, last test, Toke outstanding).
            var (toke, tokeBytes) = a.Diagnostics.TokeNow;
            sb.AppendLine(a.Diagnostics.Guard.StatusLine(toke, tokeBytes));
            // Addendum 4: CPU / run-queue load and the top CPU / page-fault processes.
            if (a.Diagnostics.Load is { } load)
            {
                string rq = a.Diagnostics.RunQueue is { } q ? $", run queue {q:0}" : "";
                sb.AppendLine($"CPU: {load.CpuFraction * 100:0}% of {Environment.ProcessorCount} cores{rq}" + (a.Diagnostics.Saturation.Saturated ? " — SATURATED" : "") + ".");
                if (a.Diagnostics.Faults is { } f)
                    sb.AppendLine($"Page faults: {f.Text()}" + (f.Dominant() is { } d ? $"; {d}" : "") + ".");
                var busy = load.Processes.Where(r => r.Cores >= 0.2 || r.PageFaultsPerSec >= 50_000).Take(3).ToList();
                if (busy.Count > 0)
                    sb.AppendLine("Busiest: " + string.Join("; ", busy.Select(r => $"{r.Image} {r.Cores:0.0} core{(r.PageFaultsPerSec >= 50_000 ? $", {r.PageFaultsPerSec:N0} page faults/s" : "")}")) + ".");
            }
            // (The lag line is the lag monitor's own summary above: "measuring…", the baseline being learned, or the status.
            // A leftover placeholder here printed "not measured yet" under the real numbers: Part 3, item 9.)
            sb.AppendLine();
            var top = a.Detector.Status.Values.Where(s => s.Delta60m > 0).OrderByDescending(s => s.Delta60m).Take(3).ToList();
            if (top.Count == 0) sb.AppendLine("No tag is growing.");
            else
            {
                sb.AppendLine("Growing most:");
                foreach (var s in top)
                {
                    var info = a.Knowledge.Lookup(s.Tag);
                    sb.AppendLine($"  {info.Description} ({s.Tag.ToDisplay()}, {info.Owner}): {Fmt.Bytes(s.Bytes)}, {Fmt.SignedBytes(s.Delta60m)} in the last hour, {Fmt.SignedBytes(s.Delta10m)} in 10 min" +
                                  (s.Rates.Shape is AllocShape.LeakShape or AllocShape.GrowingWithChurn ? $"; {s.Rates.Text}" : ""));
                }
            }
            var chart = h.Minutes.Select(m => new ChartPoint(m.TimeUtc, m.Totals)).ToList();
            chart.Add(new ChartPoint(h.Times[^1], now));
            return new OverviewData(sb.ToString().TrimEnd(), chart);
        }
    }
}
