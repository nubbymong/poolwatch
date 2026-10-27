using PoolWatch.Core.Correlation;
using PoolWatch.Core.Detection;
using PoolWatch.Core.History;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;

namespace PoolWatch.Core.Incidents;

/// <summary>
/// Turns detector alerts into incidents (DESIGN.md section 10): one open incident per tag while its growth rule keeps
/// firing, closed after a quiet period; release and retention attach to the tag's latest incident. Each incident
/// is re-ranked and its folder rewritten while it is open, and once more when it closes.
/// Called on the sampler thread with the history lock held.
/// </summary>
public sealed class IncidentManager
{
    private readonly Settings _settings;
    private readonly TagKnowledge _knowledge;
    private readonly IIncidentHost _host;
    private readonly ProcessRegistry _registry;
    private readonly IncidentWriter _writer;
    private readonly Dictionary<PoolTag, Incident> _open = new();
    private readonly List<Incident> _closed = new();
    private readonly List<LifecycleEvent> _events = new();
    private readonly Dictionary<PoolTag, DateTime> _suppressed = new();

    /// <summary>Ignores growth alerts for a tag until a time (PoolWatch's own trace buffers grow EtwB while it captures).</summary>
    public void Suppress(PoolTag tag, DateTime untilUtc) => _suppressed[tag] = untilUtc;
    private TimeSpan EventKeep => TimeSpan.FromMinutes(_settings.IncidentEventKeepMinutes);
    private static readonly TimeSpan ReleaseAttachWindow = TimeSpan.FromHours(6);
    private const int MaxEvents = 20_000;

    /// <summary>How often an open incident is re-ranked; its folder is rewritten only on a material change or every <see cref="MaxRewriteInterval"/>.</summary>
    public TimeSpan RewriteInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaxRewriteInterval { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>The non-paged total rule is one system-level incident, whichever tag grew fastest at each alert.</summary>
    private static readonly PoolTag TotalKey = new(0);
    private static PoolTag Key(Incident i) => i.Kind == IncidentKind.NonPagedTotal ? TotalKey : i.Tag;

    public IncidentManager(Settings settings, TagKnowledge knowledge, IIncidentHost host, ProcessRegistry registry)
    {
        _settings = settings;
        _knowledge = knowledge;
        _host = host;
        _registry = registry;
        _writer = new IncidentWriter(knowledge, host, registry);
    }

    public IReadOnlyCollection<Incident> Open => _open.Values;
    public int EventCount => _events.Count;
    public IReadOnlyList<Incident> Closed => _closed;
    public IEnumerable<Incident> All => _closed.Concat(_open.Values).OrderBy(i => i.OpenedUtc);
    /// <summary>
    /// Incidents created since PoolWatch started, all of them (C6: <see cref="Closed"/> keeps only those closed in the last
    /// 6 hours, so the boot summary undercounted). A re-opened incident is not counted again; a capture incident discarded
    /// before its trace started is not counted.
    /// </summary>
    public int CreatedCount { get; private set; }
    /// <summary>Raised when an incident is created (for the tray / events.csv).</summary>
    public event Action<Incident, Alert>? Opened;

    public void AddEvents(IEnumerable<LifecycleEvent> events)
    {
        _events.AddRange(events);
        if (_events.Count > MaxEvents) _events.RemoveRange(0, _events.Count - MaxEvents);
    }

    /// <summary>Lifecycle events in [from, to].</summary>
    public List<LifecycleEvent> EventsBetween(DateTime fromUtc, DateTime toUtc) =>
        _events.Where(e => e.TimeUtc >= fromUtc && e.TimeUtc <= toUtc).OrderBy(e => e.TimeUtc).ToList();

    public void Process(History.History h, Detector detector, IReadOnlyList<Alert> alerts, DateTime nowUtc)
    {
        if (h.Count == 0) return;
        int cut = _events.FindIndex(e => e.TimeUtc >= nowUtc - EventKeep);
        if (cut > 0) _events.RemoveRange(0, cut);

        foreach (var a in alerts)
        {
            if (a.Kind == AlertKind.Release) OnRelease(h, a);
            else OnGrowth(h, a);
        }

        foreach (var inc in _open.Values.ToList())
        {
            if (nowUtc - inc.LastRiseUtc >= TimeSpan.FromMinutes(_settings.IncidentQuietMinutes))
                Close(h, inc, nowUtc, $"no new rise for {_settings.IncidentQuietMinutes:0.#} min");
            else if (nowUtc - (inc.EpisodeStartUtc ?? inc.OpenedUtc) >= TimeSpan.FromMinutes(_settings.IncidentMaxMinutes))
                Close(h, inc, nowUtc, $"open for the maximum {_settings.IncidentMaxMinutes:0} min");
            else if (inc.Dirty && nowUtc - inc.LastRankUtc >= RewriteInterval)
            {
                Rank(h, inc, nowUtc);
                inc.LastRankUtc = nowUtc;
                // Disk budget: rewrite on a new top suspect, 25% more growth, or every few minutes.
                var top = inc.Suspects.FirstOrDefault()?.Key;
                if (top != inc.WrittenTop || inc.Growth > inc.WrittenGrowth + inc.WrittenGrowth / 4 + (1 << 20) || nowUtc - inc.LastWriteUtc >= MaxRewriteInterval)
                    Write(h, inc);
            }
        }

        CheckRetention(h, detector, nowUtc);
        // A closed incident is rewritten on a change, and (2f, review round 2) at most every few minutes for alerts counted
        // on its timeline after it closed.
        foreach (var inc in _closed.Where(i => i.Dirty || (i.CountPending && nowUtc - i.LastWriteUtc >= MaxRewriteInterval)).ToList()) Write(h, inc);
        // 2b: the processes of an incident's window plus 10 minutes on each side (sized by time, not by a count) stay
        // resolvable in the registry while the incident is open or closed in the last 15 min (its trace may still be
        // parsing), so the trace's PIDs resolve to who ran them.
        var (since, until) = ProtectWindow(_open.Values.Concat(_closed.Where(c => nowUtc - c.ClosedUtc!.Value < TimeSpan.FromMinutes(15))),
            CaptureRunning?.Invoke() == true);
        _registry.ProtectSince = since;
        _registry.ProtectUntil = until;
        int old = _closed.FindIndex(i => nowUtc - i.ClosedUtc!.Value < ReleaseAttachWindow);
        if (old > 0) _closed.RemoveRange(0, old); // their folders stay on disk
        else if (old < 0) _closed.Clear();
    }

    /// <summary>
    /// A "Capture now" incident for the given tags (the fastest-growing ones); the capture result is attached when
    /// the trace finishes.
    /// </summary>
    public Incident CreateCapture(History.History h, IReadOnlyList<PoolTag> tags, DateTime nowUtc)
    {
        var inc = Create(tags.Count > 0 ? tags[0] : PoolTag.Parse("File"), IncidentKind.Capture, nowUtc);
        inc.OnsetUtc = nowUtc;
        inc.LastAlertUtc = inc.LastRiseUtc = nowUtc;
        inc.ClosedUtc = nowUtc;
        _closed.Add(inc);
        Rank(h, inc, nowUtc);
        Write(h, inc);
        return inc;
    }

    /// <summary>A line on an incident's timeline from outside the manager ("capture skipped: ..."); the folder is rewritten.</summary>
    public void Note(Incident inc, DateTime t, string text)
    {
        AddTimeline(inc, t, text);
        inc.Dirty = true;
    }

    /// <summary>Removes a capture incident whose trace could not start (another capture won the race).</summary>
    public void Discard(Incident inc)
    {
        if (_closed.Remove(inc)) CreatedCount--;
        try { if (Directory.Exists(inc.Dir)) Directory.Delete(_host.CheckWrite(inc.Dir), true); } catch { }
    }

    /// <summary>Attaches a finished confirmation trace to its incident, re-ranks and rewrites it.</summary>
    public void ApplyCapture(History.History h, Incident inc, CaptureResult result, DateTime nowUtc)
    {
        inc.Capture = result;
        // The CPU picture next to the pool size at that moment (addendum 2 C).
        if (result.Cpu is { } cpu && h.Count > 0) cpu.PoolBytes = (long)(h.Totals[^1].CommittedPaged + h.Totals[^1].CommittedNonPaged);
        if (inc.Kind == IncidentKind.Capture && result.EndUtc > result.StartUtc)
        {
            inc.OnsetUtc = result.StartUtc; // a manual capture's window is the trace itself
            inc.ClosedUtc = result.EndUtc;
        }
        Rank(h, inc, inc.ClosedUtc ?? nowUtc);
        Write(h, inc);
        _host.Log($"incident {inc.Id}: capture attached ({(result.PoolEventsParsed ? $"{result.PoolEvents} pool events" : result.Error ?? "no pool events")})");
    }

    /// <summary>A suspect's session details arrived (transcript search): rewrite the incidents that name it.</summary>
    public void Touch(ProcessKey key)
    {
        foreach (var inc in All.Where(i => i.Suspects.Any(s => s.Key == key)))
        {
            inc.Dirty = true;
            if (inc.IsOpen) inc.WrittenTop = null; // forces the next re-rank to write
        }
    }

    /// <summary>Closes and writes every open incident (end of a headless run, app exit).</summary>
    public void Finish(History.History h, DateTime nowUtc)
    {
        foreach (var inc in _open.Values.ToList()) Close(h, inc, nowUtc);
        foreach (var inc in _closed.Where(i => i.Dirty || i.CountPending)) Write(h, inc);
    }

    private void OnGrowth(History.History h, Alert a)
    {
        if (_suppressed.TryGetValue(a.Tag, out var until) && a.TimeUtc < until) return;
        bool total = a.Kind == AlertKind.NonPagedTotal;
        var (p, np) = h.TagAt(a.Tag, h.Count - 1);
        // For the system-level rule, "rising" is judged on the committed non-paged total, not on the chosen tag.
        long cur = total ? (long)h.Totals[^1].CommittedNonPaged : p + np;
        var key = total ? TotalKey : a.Tag;
        if (_open.TryGetValue(key, out var inc))
        {
            // 2f: every alert is on the timeline. The rule fires every sample while it holds: a new run of it, or 25% more
            // growth, is its own entry; the repeats in between are counted on that entry.
            NoteAlert(inc, a);
            inc.LastAlertUtc = a.TimeUtc;
            // Small wobbles on a plateau must not keep the incident open: count a rise only above the high + 1/64 of the alert.
            if (cur > inc.HighestSeenBytes + a.Bytes / 64) { inc.HighestSeenBytes = cur; inc.LastRiseUtc = a.TimeUtc; }
            // One entry per rule (kind + window): the rule text embeds live numbers, so it is not a key.
            var same = inc.Alerts.FindIndex(x => x.Kind == a.Kind && x.Window == a.Window);
            if (same < 0) inc.Alerts.Add(a);
            else if (a.Bytes > inc.Alerts[same].Bytes) inc.Alerts[same] = a with { OnsetUtc = Min(a.OnsetUtc, inc.Alerts[same].OnsetUtc) };
            if (a.OnsetUtc < inc.OnsetUtc) inc.OnsetUtc = a.OnsetUtc;
            inc.Dirty = true;
            return;
        }
        // The window rule keeps firing for a while after growth stops; that is the closed episode, not a new one.
        // 2f (review round 2): those alerts are still on its timeline (counted), not dropped silently.
        var last = _closed.LastOrDefault(i => Key(i) == key && i.Kind is IncidentKind.Growth or IncidentKind.NonPagedTotal);
        if (last is not null && a.OnsetUtc <= last.ClosedUtc && cur <= last.HighestSeenBytes + a.Bytes / 64)
        {
            NoteClosed(last, a, "alert after it closed");
            return;
        }
        if (total && last is not null && a.TimeUtc - last.ClosedUtc!.Value < TimeSpan.FromMinutes(_settings.IncidentQuietMinutes * 3) &&
            cur <= last.HighestSeenBytes + _settings.NonPagedTotalRiseBytesPer10Min / 4)
        {
            NoteClosed(last, a, "alert after it closed");
            return;
        }
        // 2f: one incident per tag per episode: the same tag growing again within an hour of the last incident closing
        // re-opens that incident (same folder, timeline entry) instead of a new folder each time.
        if (last is not null && a.TimeUtc - last.ClosedUtc!.Value < TimeSpan.FromMinutes(ReopenMinutes))
        {
            _closed.Remove(last);
            NoteAlert(last, a, $"re-opened: {a.Rule} ({Fmt.SignedBytes(a.Bytes)}), {Fmt.Duration(a.TimeUtc - last.ClosedUtc.Value)} after it closed");
            if (last.Release is not null || last.Retention is not null)
                AddTimeline(last, a.TimeUtc, "earlier episode: " + string.Join("; ", new[]
                {
                    last.Release is { } rel ? $"released {Fmt.Bytes(rel.Dropped)} at {Fmt.Clock(rel.TimeUtc)}" : null,
                    last.Retention is not null ? "retained after its processes went idle" : null,
                }.Where(x => x is not null)));
            last.Release = null;
            last.Retention = null;
            last.ClosedUtc = null;
            last.EndUtc = null;
            last.EpisodeStartUtc = a.TimeUtc; // the maximum duration counts from the re-open, not from the first episode
            last.LastAlertUtc = last.LastRiseUtc = a.TimeUtc;
            last.HighestSeenBytes = Math.Max(last.HighestSeenBytes, cur);
            last.Reopened++;
            var same = last.Alerts.FindIndex(x => x.Kind == a.Kind && x.Window == a.Window);
            if (same < 0) last.Alerts.Add(a); else last.Alerts[same] = a;
            _open[key] = last;
            _host.Log($"incident {last.Id} re-opened: {a.Tag.ToDisplay()} {a.Rule} ({Fmt.SignedBytes(a.Bytes)})");
            _host.RequestHandleScan();
            Rank(h, last, a.TimeUtc);
            Write(h, last);
            Opened?.Invoke(last, a); // a new episode gets its own confirmation trace and notification
            return;
        }
        inc = Create(a.Tag, a.Kind == AlertKind.NonPagedTotal ? IncidentKind.NonPagedTotal : IncidentKind.Growth, a.TimeUtc);
        NoteAlert(inc, a, $"opened: {a.Rule} ({Fmt.SignedBytes(a.Bytes)})");
        inc.OnsetUtc = a.OnsetUtc;
        inc.LastAlertUtc = inc.LastRiseUtc = a.TimeUtc;
        inc.HighestSeenBytes = cur;
        inc.Alerts.Add(a);
        _open[key] = inc;
        _host.Log($"incident {inc.Id}: {a.Tag.ToDisplay()} {a.Rule} ({Fmt.SignedBytes(a.Bytes)})");
        _host.RequestHandleScan();
        if (_knowledge.NeedsDriverScan(a.Tag)) _host.RequestTagScan([a.Tag]);
        Rank(h, inc, a.TimeUtc);
        Write(h, inc);
        Opened?.Invoke(inc, a);
    }

    private void OnRelease(History.History h, Alert a)
    {
        if (_suppressed.TryGetValue(a.Tag, out var until) && a.TimeUtc < until) return;
        var inc = _open.GetValueOrDefault(a.Tag) ?? _closed.LastOrDefault(i => i.Tag == a.Tag && i.Kind == IncidentKind.Growth);
        bool created = inc is null;
        if (inc is null)
        {
            inc = Create(a.Tag, IncidentKind.Release, a.TimeUtc);
            inc.OnsetUtc = a.OnsetUtc;
            inc.LastAlertUtc = a.TimeUtc;
            inc.ClosedUtc = a.TimeUtc;
            _closed.Add(inc);
        }
        inc.Alerts.Add(a);
        // 2f (review round 2): a release is on the timeline too, counted like any alert.
        NoteAlert(inc, a, label: "alert (release)");
        var release = BuildRelease(h, a);
        if (inc.Release is null || release.Dropped > inc.Release.Dropped) inc.Release = release;
        inc.Dirty = true;
        _host.Log($"incident {inc.Id}: {a.Tag.ToDisplay()} {a.Rule}");
        if (created)
        {
            if (_knowledge.NeedsDriverScan(a.Tag)) _host.RequestTagScan([a.Tag]);
            Rank(h, inc, a.TimeUtc);
            Write(h, inc);
            Opened?.Invoke(inc, a);
        }
    }

    private Incident Create(PoolTag tag, IncidentKind kind, DateTime nowUtc)
    {
        string id = $"{nowUtc.ToLocalTime():yyyyMMdd-HHmmss}-{tag.ToFileSafe()}";
        string dir = Path.Combine(_host.IncidentsDir, id);
        for (int i = 2; Directory.Exists(dir); i++) dir = Path.Combine(_host.IncidentsDir, $"{id}-{i}");
        CreatedCount++;
        return new Incident { Id = Path.GetFileName(dir), Dir = dir, Tag = tag, Kind = kind, OpenedUtc = nowUtc };
    }

    /// <summary>An incident whose tag grows again within this many minutes of closing is re-opened (2f).</summary>
    public const double ReopenMinutes = 60;

    /// <summary>The margin kept around an incident's window for its start records (2b: at least the window plus 10 min).</summary>
    public static readonly TimeSpan ProtectMargin = TimeSpan.FromMinutes(10);

    /// <summary>Whether a confirmation trace is recording or being parsed now (set by the pipeline): the window stays open-ended then.</summary>
    public Func<bool>? CaptureRunning { get; set; }

    /// <summary>
    /// The registry's protected window for these incidents (2b): from the earliest onset minus 10 min to the latest end of
    /// a growth or its trace plus 10 min; open-ended (null) while one is still open or a trace is running (a trace the
    /// governor held back can start after the growth ended: its processes must still be resolvable). (null, null) when
    /// there are none.
    /// </summary>
    public static (DateTime? since, DateTime? until) ProtectWindow(IEnumerable<Incident> incidents, bool captureRunning = false)
    {
        DateTime? since = null, until = null;
        bool open = false;
        foreach (var i in incidents)
        {
            if (since is null || i.OnsetUtc < since) since = i.OnsetUtc;
            if (i.IsOpen) { open = true; continue; }
            var end = i.EndUtc ?? i.LastRiseUtc;
            if (i.Capture is { } cap && cap.EndUtc > end) end = cap.EndUtc; // a trace taken after the growth ended
            if (until is null || end > until) until = end;
        }
        if (since is null) return (null, null);
        return (since - ProtectMargin, open || captureRunning ? null : until + ProtectMargin);
    }

    /// <summary>A gap longer than this between two alerts of one rule starts a new run (a new timeline entry).</summary>
    public static readonly TimeSpan AlertRunGap = TimeSpan.FromMinutes(2);

    /// <summary>
    /// 2f: puts an alert on the incident's timeline. <paramref name="headline"/> (opened / re-opened) is always its own
    /// entry and starts a run. Otherwise a new run of the rule (first alert, or after a gap of over
    /// <see cref="AlertRunGap"/>) or 25% more growth than its entry is a new entry, and a repeat in between is counted on
    /// the run's entry: "alert: grew > 200 MB in 10 min (+230 MB); fired 37 more times to 06:54:10, latest +280 MB".
    /// Review round 2: when the run's entry is no longer on the timeline (it keeps 500, the oldest alert entries go first),
    /// the alert starts a new entry instead of being counted where nobody sees it. <paramref name="label"/> names the kind
    /// of entry ("alert", "alert after it closed", "alert (release)"); a release's bytes are shown as a drop.
    /// </summary>
    /// <returns>True when the alert made a new timeline entry; false when it was counted on an existing one.</returns>
    public static bool NoteAlert(Incident inc, Alert a, string? headline = null, string label = "alert")
    {
        var key = (a.Kind, a.Window);
        long Shown(long bytes) => a.Kind == AlertKind.Release ? -bytes : bytes;
        if (headline is null && inc.AlertRuns.TryGetValue(key, out var run) && a.TimeUtc - run.LastUtc <= AlertRunGap && a.Bytes < run.EntryBytes + run.EntryBytes / 4)
        {
            int i = inc.Timeline.FindLastIndex(x => x.TimeUtc == run.EntryTimeUtc && x.Text.StartsWith(run.EntryText, StringComparison.Ordinal));
            if (i >= 0)
            {
                run.More++;
                run.LastUtc = a.TimeUtc;
                run.LatestBytes = a.Bytes;
                inc.Timeline[i] = (run.EntryTimeUtc, $"{run.EntryText}; fired {run.More} more time{(run.More == 1 ? "" : "s")} to {Fmt.Clock(run.LastUtc)}, latest {Fmt.SignedBytes(Shown(run.LatestBytes))}");
                return false;
            }
            // The entry was evicted: fall through to a new one.
        }
        string text = headline ?? $"{label}: {a.Rule} ({Fmt.SignedBytes(Shown(a.Bytes))})";
        AddTimeline(inc, a.TimeUtc, text);
        inc.AlertRuns[key] = new AlertRun { EntryTimeUtc = a.TimeUtc, EntryText = text, LastUtc = a.TimeUtc, EntryBytes = a.Bytes, LatestBytes = a.Bytes };
        return true;
    }

    /// <summary>
    /// 2f (review round 2): an alert for a CLOSED incident (the window rule keeps firing after the growth stopped, a
    /// release, a repeat of the system-level rule) goes on its timeline too. A new entry rewrites the folder now; a counted
    /// repeat within <see cref="MaxRewriteInterval"/> (a closed incident is not rewritten on every sample).
    /// </summary>
    private static void NoteClosed(Incident inc, Alert a, string label)
    {
        if (NoteAlert(inc, a, label: label)) inc.Dirty = true;
        else inc.CountPending = true;
    }

    private static void AddTimeline(Incident inc, DateTime t, string text)
    {
        if (inc.Timeline.Count >= 500)
        {
            // Evict the oldest alert entry; opened / re-opened / closed entries are what the timeline is for.
            int i = inc.Timeline.FindIndex(x => x.Text.StartsWith("alert", StringComparison.Ordinal)); // "alert:", "alert after it closed:", "alert (release):"
            inc.Timeline.RemoveAt(i >= 0 ? i : 1);
        }
        inc.Timeline.Add((t, text));
    }

    private void Close(History.History h, Incident inc, DateTime nowUtc, string why = "")
    {
        AddTimeline(inc, nowUtc, "closed" + (why.Length > 0 ? ": " + why : ""));
        inc.AlertRuns.Clear(); // alerts after this are their own entries ("alert after it closed"), not counted above "closed"
        inc.ClosedUtc = nowUtc;
        _open.Remove(Key(inc));
        Rank(h, inc, nowUtc);
        _closed.Add(inc);
        Write(h, inc);
        _host.Log($"incident {inc.Id} closed: {Fmt.SignedBytes(inc.Growth)} from {Fmt.Clock(inc.OnsetUtc)}");
    }

    /// <summary>Re-derives the growth episode and the suspect ranking from the history.</summary>
    public void Rank(History.History h, Incident inc, DateTime nowUtc)
    {
        if (h.Count < 3) return;
        DateTime to = Min(nowUtc, h.Times[^1]);
        // Anchor on the alerts' own onset so repeated re-ranks cannot drift the window earlier and earlier.
        var growthAlerts = inc.Alerts.Where(a => a.Kind != AlertKind.Release).ToList();
        DateTime onset = inc.Kind == IncidentKind.Release && inc.Release is { } r ? r.FromUtc
            : growthAlerts.Count > 0 ? growthAlerts.Min(a => a.OnsetUtc) : inc.OnsetUtc;
        var lead = TimeSpan.FromTicks(Math.Max(TimeSpan.FromMinutes(1).Ticks, (to - onset).Ticks / 2));
        DateTime from = Max(h.Times[0], onset - lead);
        int iFrom = h.IndexAtOrAfter(from), iTo = Math.Max(iFrom, h.IndexAtOrAfter(to.AddTicks(1)) - 1);
        var series = h.TagSeries(inc.Tag, iFrom);
        if (series.Length == 0) return;
        if (iTo - iFrom + 1 < series.Length) series = series[..(iTo - iFrom + 1)];

        DateTime? end = null;
        if (inc.Kind != IncidentKind.Release)
        {
            // Refine the onset with change-point detection on the series after the baseline start.
            var cp = Detector.FindChangePoints(series);
            if (cp.Rise > 0)
            {
                var cpOnset = h.Times[iFrom + cp.OnsetIndex];
                if (cpOnset >= from && cpOnset <= to) onset = cpOnset;
                if (cp.EndIndex is { } e && iFrom + e < iTo) end = h.Times[iFrom + e];
            }
            inc.OnsetUtc = onset;
            inc.EndUtc = end;
            int o = Math.Clamp(h.IndexAtOrAfter(onset) - iFrom, 0, series.Length - 1);
            inc.StartBytes = series[o];
            inc.PeakBytes = series[o..].Max();
        }
        else
        {
            inc.StartBytes = series[0];
            inc.PeakBytes = series.Max();
        }
        inc.CurrentBytes = series[^1];
        var window = new CorrelationWindow(from, onset, end, to);
        inc.Window = window;
        // Owners are grouped from every active process (a session's many short commands are one owner), then the
        // per-process list keeps its top 10.
        inc.Suspects = Correlator.Rank(h, _registry, inc.Tag, window, maxSuspects: 2000);
        // The confirmation trace's attribution survives re-ranks (applied to the full list, before the owners are built).
        if (inc.Capture is { } cap)
        {
            CaptureAttribution.Apply(inc, cap, TagGrowth(h, inc.Tag, cap.StartUtc, cap.EndUtc));
            // 2a / 2b: every traced process instance with its identity, rolled up by owner; the trace outranks correlation.
            try
            {
                // Resolved once per trace (the registry forgets the short commands later); only unresolved rows are retried.
                inc.Traced = inc.Traced is { } prevTraced && inc.TracedFrom == cap ? TraceAttribution.Retry(prevTraced, cap, _registry) : TraceAttribution.Resolve(cap, inc.Tag, _registry);
                inc.TracedFrom = cap;
                inc.TraceOwners = TraceAttribution.Owners(inc.Traced);
                var starts = StartsByOwner(_events, inc.OnsetUtc, inc.EndUtc ?? inc.LastRiseUtc);
                foreach (var o in inc.TraceOwners) o.StartsInWindow = starts.GetValueOrDefault(o.Owner.Key); // 2e: by identity, not name text
            }
            catch (Exception ex) { _host.Log("trace attribution failed: " + ex.Message); inc.Traced = null; inc.TraceOwners = null; }
        }
        try
        {
            inc.Owners = OwnerRanking.Rank(h, inc.Suspects, inc.Tag, window, Math.Max(0, inc.Growth),
                s => Identity.ProcessIdentities.Of(_registry.Ancestry(s.Key) is { Count: > 0 } c ? c : [new ProcessRecord { Key = s.Key, Image = s.Image }]));
        }
        catch { inc.Owners = null; }
        inc.Suspects = inc.Suspects.Take(10).ToList();
        inc.Dirty = true;
    }

    /// <summary>
    /// Short-lived process starts per owner in [from, to], from the rollups (they carry their owner and count), keyed by the
    /// owner's identity key (item 4), the same key the trace owners have (2e): two owners with the same display name ("Claude
    /// Code session (PID ...)" for two sessions in one folder, or an app and a service sharing a name) are never added up.
    /// </summary>
    public static Dictionary<string, int> StartsByOwner(IEnumerable<LifecycleEvent> events, DateTime fromUtc, DateTime toUtc)
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in events)
        {
            if (e.TimeUtc < fromUtc || e.TimeUtc > toUtc) continue;
            // Rollups are the short-lived starts (under 30 s) by construction: "N short commands" counts only those.
            if (e.Kind == LifecycleKind.Rollup && e.OwnerKey is { } k) d[k] = d.GetValueOrDefault(k) + e.Count;
        }
        return d;
    }

    /// <summary>The tag's growth over [from, to] (max minus the minimum before it), or null when the history does not cover it.</summary>
    private static long? TagGrowth(History.History h, PoolTag tag, DateTime fromUtc, DateTime toUtc)
    {
        if (h.Count == 0 || toUtc <= fromUtc || h.Times[0] > fromUtc) return null;
        int i0 = h.IndexAtOrAfter(fromUtc), i1 = h.IndexAtOrAfter(toUtc.AddTicks(1)) - 1;
        if (i1 <= i0) return null;
        var s = h.TagSeries(tag, i0);
        long min = long.MaxValue, rise = 0;
        for (int i = 0; i <= i1 - i0 && i < s.Length; i++) { min = Math.Min(min, s[i]); rise = Math.Max(rise, s[i] - min); }
        return rise;
    }

    /// <summary>
    /// Retention: a growth incident whose top suspect went quiet (or exited) <see cref="Settings.RetentionMinutes"/>
    /// ago while the tag has kept at least half of its rise.
    /// </summary>
    private void CheckRetention(History.History h, Detector detector, DateTime nowUtc)
    {
        var quiet = TimeSpan.FromMinutes(_settings.RetentionMinutes);
        foreach (var inc in _closed)
        {
            if (inc.Kind == IncidentKind.Release || inc.Retention is not null || inc.Release is not null || inc.Growth <= 0) continue;
            var top = inc.Suspects.FirstOrDefault();
            if (top is null) continue;
            DateTime? stopped = LastActivity(h, top.Key) ?? top.ExitUtc;
            if (top.ExitUtc is { } x && (stopped is null || x < stopped)) stopped = x;
            if (stopped is not { } st || nowUtc - st < quiet) continue;
            var (p, np) = h.TagAt(inc.Tag, h.Count - 1);
            long cur = p + np;
            if (cur < inc.StartBytes + inc.Growth / 2) continue;
            inc.CurrentBytes = cur;
            string note = $"{inc.Tag.ToDisplay()} still holds {Fmt.Bytes(cur - inc.StartBytes)} of its {Fmt.Bytes(inc.Growth)} rise " +
                          $"{Fmt.Duration(nowUtc - st)} after {top.Image} (PID {top.Key.Pid}) {(top.ExitUtc is not null ? "exited" : "went quiet")}";
            inc.Retention = new RetentionRecord(nowUtc, st, cur - inc.StartBytes, note);
            detector.MarkRetained(inc.Tag, st, inc.StartBytes + inc.Growth / 2);
            inc.Dirty = true;
            _host.Log($"incident {inc.Id}: retained: {note}");
        }
    }

    /// <summary>Time of the last sample in which the process did any I/O (null if it never did in the raw history).</summary>
    private static DateTime? LastActivity(History.History h, ProcessKey key)
    {
        var pts = h.ProcessSeries(key);
        for (int i = pts.Length - 1; i > 0; i--)
            if (pts[i] is { } b && pts[i - 1] is { } a && b.TotalOps > a.TotalOps) return h.Times[i];
        return null;
    }

    private ReleaseRecord BuildRelease(History.History h, Alert a)
    {
        int iFrom = h.IndexAtOrAfter(a.OnsetUtc), iTo = h.Count - 1;
        var (fp, fnp) = h.TagAt(a.Tag, iFrom);
        var (tp, tnp) = h.TagAt(a.Tag, iTo);
        var slack = TimeSpan.FromSeconds(30);
        var exits = _events.Where(e => e.Kind == LifecycleKind.Exit && e.TimeUtc >= a.OnsetUtc - slack && e.TimeUtc <= a.TimeUtc)
            .Select(e => $"{Fmt.Clock(e.TimeUtc)} {e.Image} (PID {e.Pid}) exited{(e.ExitCode is { } c ? $", code {c}" : "")}")
            .Take(20).ToList();
        // Processes busiest while the tag dropped (mass deletes show up as I/O; Kernel-File attribution is milestone 3).
        var active = new List<(string text, long ops)>();
        foreach (var key in h.ProcessesSince(Math.Max(0, iFrom - 1)))
        {
            var pts = h.ProcessSeries(key, Math.Max(0, iFrom - 1));
            ProcPoint? first = pts.FirstOrDefault(p => p is not null), last = pts.LastOrDefault(p => p is not null);
            if (first is not { } f || last is not { } l) continue;
            long ops = l.TotalOps - f.TotalOps;
            if (ops <= 0 || key.Pid is 0) continue;
            var rec = _registry.Get(key);
            active.Add(($"{rec?.DisplayName ?? "unknown process"} (PID {key.Pid}): {ops:N0} I/O ops{(rec?.ExitUtc is { } x ? $", exited {Fmt.Clock(x)}" : "")}", ops));
        }
        var activeText = active.OrderByDescending(x => x.ops).Take(5).Select(x => x.text).ToList();
        return new ReleaseRecord(a.TimeUtc, a.OnsetUtc, fp + fnp, tp + tnp, exits, activeText, WorktreeNote(h, iFrom, a));
    }

    /// <summary>
    /// Git worktree sprawl as context (DESIGN.md section 9): the drop coincides with git worktree removal / prune, or with
    /// processes working under a <c>.git\worktrees</c> or worktree folder.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex WorktreeDelete = new(
        @"\bworktree\s+(remove|prune)\b|(^|[\s;&|""'])(rm\s+-[a-zA-Z]*r[a-zA-Z]*|rmdir\s+/s|rd\s+/s|Remove-Item\b.*-Recurse)\b[^;&|]*worktree",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static bool IsWorktreeDelete(string? commandLine) => commandLine is not null && WorktreeDelete.IsMatch(commandLine);

    private string? WorktreeNote(History.History h, int iFrom, Alert a)
    {
        var hits = new List<string>();
        foreach (var key in h.ProcessesSince(Math.Max(0, iFrom - 1)))
        {
            var rec = _registry.Get(key);
            string cl = rec?.CommandLine ?? "";
            // Deletion evidence only (sessions routinely *work* inside worktrees): git worktree remove/prune, or a
            // recursive delete of a worktree path.
            if (WorktreeDelete.IsMatch(cl))
                hits.Add($"{rec!.Image} (PID {key.Pid}): {(cl.Length > 120 ? cl[..120] + "…" : cl)}");
        }
        foreach (var e in _events.Where(e => e.TimeUtc >= a.OnsetUtc.AddSeconds(-30) && e.TimeUtc <= a.TimeUtc && e.CommandLine is { } c && WorktreeDelete.IsMatch(c)))
            hits.Add($"{e.Image} (PID {e.Pid}): {e.CommandLine}");
        return hits.Count == 0 ? null
            : "The drop coincides with git worktree cleanup (context, not a crawl: deleting worktrees frees the per-file metadata that was held): " +
              string.Join("; ", hits.Distinct().Take(5));
    }

    /// <summary>
    /// The incident's events: the window's events now, merged with those it already had (the shared buffer drops events
    /// after <see cref="Settings.IncidentEventKeepMinutes"/>; a re-opened incident's onset can be older). Capped at 5,000.
    /// </summary>
    private List<LifecycleEvent> IncidentEvents(Incident inc, DateTime nowUtc)
    {
        var from = EventWindowStart(inc);
        var fresh = EventsBetween(from, EventWindowEnd(inc, nowUtc));
        if (inc.Events.Count > 0)
        {
            var seen = new HashSet<(DateTime, LifecycleKind, int, long)>(fresh.Select(e => (e.TimeUtc, e.Kind, e.Pid, e.CreateTime)));
            fresh.AddRange(inc.Events.Where(e => e.TimeUtc >= from && seen.Add((e.TimeUtc, e.Kind, e.Pid, e.CreateTime))));
            fresh.Sort((x, y) => x.TimeUtc.CompareTo(y.TimeUtc));
        }
        if (fresh.Count > 5000) fresh.RemoveRange(0, fresh.Count - 5000);
        inc.Events = fresh;
        return fresh;
    }

    /// <summary>2c: the events of the growth itself: from the onset minus 2 min to the end of the growth (or now while it climbs).</summary>
    public static DateTime EventWindowStart(Incident inc) => inc.OnsetUtc.AddMinutes(-2);

    public static DateTime EventWindowEnd(Incident inc, DateTime nowUtc) => (inc.EndUtc ?? (inc.IsOpen ? nowUtc : inc.LastRiseUtc)).AddMinutes(1);

    private void Write(History.History h, Incident inc)
    {
        using var stage = Util.Profiler.Stage("incident.write");
        try
        {
            _writer.Write(h, inc, IncidentEvents(inc, h.Count > 0 ? h.Times[^1] : DateTime.UtcNow));
            inc.LastWriteUtc = h.Times.Count > 0 ? h.Times[^1] : DateTime.UtcNow;
            inc.WrittenTop = inc.Suspects.FirstOrDefault()?.Key;
            inc.WrittenGrowth = inc.Growth;
            inc.Dirty = false;
            inc.CountPending = false;
        }
        catch (Exception ex)
        {
            inc.Dirty = inc.CountPending = false; // do not retry every sample; the next change rewrites it
            _host.Log($"incident {inc.Id}: write failed: {ex.Message}");
        }
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
