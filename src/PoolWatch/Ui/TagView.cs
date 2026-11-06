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

/// <summary>
/// A growing tag's suspects and events (a Pool causes row's details), copied under the history lock. It keeps what the
/// details can show (<see cref="TagViewBuilder.ShownOwners"/> owners, <see cref="TagViewBuilder.ShownEvents"/> events), not
/// the whole ranking: the cache holds one of these per growing tag.
/// </summary>
internal sealed record TagViewData(PoolTag Tag, string Title, TagAnswer Answer, IReadOnlyList<OwnerSuspect> Owners, IReadOnlyList<EventRow> Events,
    DateTime FromUtc, DateTime ToUtc, DateTime OnsetUtc)
{
    /// <summary>The answer comes from a confirmation trace; the owners listed below it are correlation only.</summary>
    public bool TraceBacked { get; init; }
}

/// <summary>An events-list row: a single notable event, or a summary of many short commands.</summary>
/// <param name="Key">Stable across refreshes (so the list is updated in place): the event's identity, or minute + owner for a summary.</param>
internal sealed record EventRow(DateTime TimeUtc, string Text, bool Notable, string Key);

internal static class TagViewBuilder
{
    /// <summary>The most meaningful owners the details print (the copied summary lists five, the details pane three).</summary>
    public const int ShownOwners = 5;
    /// <summary>The recent notable events the details pane lists.</summary>
    public const int ShownEvents = 10;

    /// <summary>A tag over the last hour (or what the raw history holds), ranked by owner, in plain words.</summary>
    public static TagViewData? Build(AnalysisPipeline a, ProcessRegistry registry, PoolTag tag)
    {
        lock (a.History.SyncRoot) return Build(a.History, a.Knowledge, a.Incidents.All, a.Incidents.EventsBetween, registry, tag);
    }

    /// <summary>
    /// <see cref="Build(AnalysisPipeline, ProcessRegistry, PoolTag)"/> over <paramref name="h"/>, the incidents and their
    /// events (<paramref name="eventsBetween"/>, by the forward clock's milliseconds). The hour, the growth in it, how long it
    /// is said to be and whether a traced incident closed within the last hour are timed on the clock that only goes forward.
    /// The caller holds the history lock.
    /// </summary>
    internal static TagViewData? Build(Core.History.History h, Core.Tags.TagKnowledge knowledge, IEnumerable<Incident> incidents,
        Func<long, long, List<LifecycleEvent>> eventsBetween, ProcessRegistry registry, PoolTag tag)
    {
        if (h.Count < 3) return null;
        var recent = CorrelationWindow.Recent(h, tag, TimeSpan.FromMinutes(60));
        var (window, series, cp) = (recent.Window, recent.Series, recent.ChangePoints);
        long nowMs = h.Ms[^1];
        // Every active process, so an owner made of many small ones (a session's short commands) is grouped before any cut.
        var suspects = Correlator.Rank(h, registry, tag, window, maxSuspects: 2000);
        long growth = Detector.GrowthSinceOnset(series, cp.OnsetIndex); // total: 0 for an empty series, onset clamped
        var owners = OwnerRanking.Rank(h, suspects, tag, window, growth, s => ProcessIdentities.Of(registry.Ancestry(s.Key)));
        var info = knowledge.Lookup(tag);
        var (p, np) = h.TagAt(tag, h.Count - 1);
        double minutes = (nowMs - recent.SinceMs) / 60_000.0;
        string span = minutes >= 55 ? "the last hour" : $"the last {Math.Max(1, (int)minutes)} min";
        var answer = OwnerRanking.Answer(info, tag, p, np, growth, span, owners);
        // A confirmation trace of this tag's current (or last hour's) incident outranks correlation.
        var traced = incidents.LastOrDefault(i => i.Tag == tag && i.TraceOwners is { Count: > 0 } && i.Capture is not null &&
                                                  (i.IsOpen || nowMs - i.ClosedMs!.Value < (long)TimeSpan.FromMinutes(IncidentManager.ReopenMinutes).TotalMilliseconds));
        bool traceBacked = false;
        if (traced is not null && TraceAttribution.Answer(traced, info, traced.TraceOwners!, traced.Capture!) is { } line)
        {
            answer = answer with { Line = line + " (confirmation trace, " + Fmt.Clock(traced.Capture!.StartUtc) + ")" };
            traceBacked = true;
            // The ranking follows the trace too (its owners by net bytes first), correlation-only owners below.
            owners = TraceAttribution.Rank(traced.TraceOwners!, owners, tag);
        }
        string title = $"{tag.ToDisplay()} · {info.Description} ({info.Owner}): {Fmt.Bytes(p + np)} now, {Fmt.SignedBytes(growth)} in {span}";
        return View(tag, title, answer, owners, eventsBetween(recent.SinceMs, nowMs), registry, window, traceBacked);
    }

    /// <summary>
    /// What the row keeps of a finished ranking: the <see cref="ShownOwners"/> meaningful owners its details print, trimmed
    /// (<see cref="OwnerRanking.Keep"/>), and the <see cref="RecentNotable"/> events, not the whole ranking or the hour's events.
    /// </summary>
    internal static TagViewData View(PoolTag tag, string title, TagAnswer answer, IEnumerable<OwnerSuspect> owners, IReadOnlyList<LifecycleEvent> events,
        ProcessRegistry registry, CorrelationWindow window, bool traceBacked)
    {
        var shown = OwnerRanking.Keep(owners, ShownOwners);
        var recent = RecentNotable(events, registry);
        return new TagViewData(tag, title, answer with { Owners = shown }, shown, recent, window.FromUtc, window.ToUtc, window.OnsetUtc) { TraceBacked = traceBacked };
    }

    /// <summary>
    /// The last <see cref="ShownEvents"/> notable rows of <see cref="EventRows"/> (all: false), built from those events only:
    /// a notable row is one non-rollup event, in time order, so the rest of the hour is never described.
    /// </summary>
    public static List<EventRow> RecentNotable(IReadOnlyList<LifecycleEvent> events, ProcessRegistry registry) =>
        EventRows(events.Where(e => e.Kind != LifecycleKind.Rollup).TakeLast(ShownEvents).ToList(), registry, all: false);

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
            // The live process of the parent's PID is the parent when it started before this event, on the forward clock.
            string owner = e.Owner ?? (registry.GetLive(e.ParentPid) is { } parent && parent.StartMs <= e.Ms
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
        // Two events with the same time, kind, PID and image (app events: PID 0) shared a key and one replaced the
        // other in the list. The key now carries the detail, and identical rows are numbered (stable: OrderBy is stable).
        var ordered = rows.OrderBy(r => r.TimeUtc).ToList();
        var keys = RowKeys.Unique(ordered.Select(r => r.Key));
        for (int i = 0; i < ordered.Count; i++) if (!ReferenceEquals(keys[i], ordered[i].Key)) ordered[i] = ordered[i] with { Key = keys[i] };
        return ordered;
    }

    /// <summary>A raw start or exit of a process that ran less than 30 s (<see cref="Ran"/>).</summary>
    private static bool ShortLived(LifecycleEvent e, ProcessRegistry registry) =>
        e.Kind is LifecycleKind.Start or LifecycleKind.Exit && Ran(e, registry) is { } ran && ran < EventSummarizer.LongLived;

    /// <summary>
    /// How long the process of a start or an exit ran, on the clock that only goes forward while the registry holds it: from
    /// its start to its exit as the registry read them (<see cref="ProcessRecord.StartMs"/>, <see cref="ProcessRecord.ExitMs"/>),
    /// or to the exit event's own time (<see cref="LifecycleEvent.Ms"/>) before the registry marked it exited. For an exit the
    /// registry no longer holds, from Windows' create time to the exit's time, both system times (nothing else is left of it).
    /// Null for a start of a process still running, or an exit without a create time.
    /// </summary>
    private static TimeSpan? Ran(LifecycleEvent e, ProcessRegistry registry)
    {
        bool exit = e.Kind == LifecycleKind.Exit;
        if (registry.Get(e.Key) is { } rec && (rec.ExitMs ?? (exit && e.Ms > 0 ? e.Ms : null)) is { } endMs)
            return TimeSpan.FromMilliseconds(Math.Max(0, endMs - rec.StartMs));
        return exit && e.CreateTime > 0 ? e.TimeUtc - e.Key.CreateTimeUtc : null;
    }

    private static string Describe(LifecycleEvent e, ProcessRegistry registry)
    {
        string what = e.Kind switch
        {
            LifecycleKind.Start => Ran(e, registry) is { } ran ? $"{e.Image} (PID {e.Pid}) started (ran {Fmt.Duration(ran)})" : $"{e.Image} (PID {e.Pid}) started and is still running",
            LifecycleKind.Exit => $"{e.Image} (PID {e.Pid}) exited{(e.ExitCode is { } c ? $" with code {c}" : "")}" +
                                  (e.CreateTime > 0 && Ran(e, registry) is { } after ? $" after {Fmt.Duration(after)}" : ""),
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
}
