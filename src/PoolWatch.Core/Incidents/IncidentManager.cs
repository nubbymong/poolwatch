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
    /// <summary>The events kept, each with its <see cref="LifecycleEvent.Ms"/> read as it was added: how long it is kept and the windows it is picked for are timed on it.</summary>
    private readonly List<LifecycleEvent> _events = new();
    /// <summary>Each tag held, with until when (<see cref="Moment.Ms"/>, the clock that only goes forward).</summary>
    private readonly Dictionary<PoolTag, long> _suppressed = new();
    /// <summary>The moment of the sample being processed (<see cref="Process"/>): its alerts are of it.</summary>
    private long _nowMs;

    /// <summary>
    /// Ignores growth and release alerts for a tag until <paramref name="untilMs"/> on the clock that only goes forward
    /// (milliseconds since Windows started; PoolWatch's own trace buffers grow EtwB while it captures), so setting the
    /// system clock back or forward neither holds the alerts back longer nor lets them through early.
    /// </summary>
    public void Suppress(PoolTag tag, long untilMs) => _suppressed[tag] = untilMs;
    private TimeSpan EventKeep => TimeSpan.FromMinutes(_settings.IncidentEventKeepMinutes);
    private static readonly TimeSpan ReleaseAttachWindow = TimeSpan.FromHours(6);
    private const int MaxEvents = 20_000;
    /// <summary>
    /// The most closed incidents held in memory (for 6 hours each): the maintainer's PC held 16 after 4 hours of agentic load.
    /// Past it the oldest closed leaves memory first; its folder stays on disk and the window lists it from there.
    /// </summary>
    public const int MaxClosed = 20;

    /// <summary>
    /// How often an open incident's episode (its onset, end and growth) is measured again; its folder is rewritten only on a
    /// material change or every <see cref="MaxRewriteInterval"/>, with its suspects ranked just before.
    /// </summary>
    public TimeSpan RewriteInterval { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>
    /// While the write budget has room for a rewrite, an open incident's suspects are ranked again this often, to see whether
    /// its top suspect changed (a new top suspect is rewritten then).
    /// </summary>
    public TimeSpan SuspectsInterval { get; set; } = TimeSpan.FromMinutes(2);
    /// <summary>
    /// An open incident's suspects are ranked again at least this often whatever the write budget, so the cause the window's
    /// list and the tray show from memory is never older than this (its report.json is as old as its last rewrite).
    /// </summary>
    public TimeSpan SuspectsRefreshInterval { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>How many times incidents' suspects were ranked (the costly part of a re-rank), for the cost report.</summary>
    public long SuspectRankings { get; private set; }
    public TimeSpan MaxRewriteInterval { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>A closed incident's alerts counted after it closed ("fired 37 more times") are written at most this often.</summary>
    public TimeSpan ClosedRewriteInterval { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The report folders' write budget (<see cref="WriteBudget"/>), shared with the spawn-storm folders: an open incident's
    /// refresh, a closed one's update and the rows of events.csv wait while it has no room; an incident's opening, re-opening,
    /// closing and a trace attached are always written.
    /// </summary>
    public WriteBudget Budget { get; set; } = new();
    /// <summary>The end of a run: everything left for later is written.</summary>
    private bool _finishing;
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
    /// <summary>Every incident held, in the order they opened (by the clock that only goes forward).</summary>
    public IEnumerable<Incident> All => _closed.Concat(_open.Values).OrderBy(i => i.OpenedMs);

    /// <summary>What the incidents in memory hold, for PoolWatch's own cost report. Under the history lock.</summary>
    public (int incidents, int owners, int ownerMembers, int events) Held()
    {
        var all = _closed.Concat(_open.Values).ToList();
        return (all.Count, all.Sum(i => i.Owners?.Count ?? 0), all.Sum(i => i.Owners?.Sum(o => o.Members.Count) ?? 0), all.Sum(i => i.Events.Count));
    }

    /// <summary>
    /// Incidents created since PoolWatch started, all of them (<see cref="Closed"/> keeps only those closed in the last
    /// 6 hours, so the boot summary undercounted). A re-opened incident is not counted again; a capture incident discarded
    /// before its trace started is not counted.
    /// </summary>
    public int CreatedCount { get; private set; }
    /// <summary>Raised when an incident is created (for the tray / events.csv).</summary>
    public event Action<Incident, Alert>? Opened;

    /// <summary>
    /// Events to keep for the incidents' timelines, each read onto the clock that only goes forward at <paramref name="now"/>.
    /// The open incidents take theirs at once (<see cref="KeepEvents"/>), before the shared buffer lets any go, so what an
    /// incident keeps has every event of its window however long its writes wait for the write budget.
    /// </summary>
    public void AddEvents(IEnumerable<LifecycleEvent> events, Moment now)
    {
        int before = _events.Count;
        foreach (var e in events) _events.Add(e with { Ms = now.MsOf(e.TimeUtc) });
        _eventsAdded += _events.Count - before;
        foreach (var inc in _open.Values) KeepEvents(inc);
        if (_events.Count > MaxEvents) DropEvents(_events.Count - MaxEvents);
    }

    /// <summary>Events put into the shared buffer in all, and let go from its front in all: the buffer's event i is number <c>_eventsDropped + i</c>.</summary>
    private long _eventsAdded, _eventsDropped;

    private void DropEvents(int n)
    {
        _events.RemoveRange(0, n);
        _eventsDropped += n;
    }

    /// <summary>
    /// What an incident has kept of the shared buffer's events (<see cref="Incident.Events"/>): every one from
    /// <see cref="FromMs"/> on, in time order, of those numbered below <see cref="Seq"/>, up to
    /// <see cref="IncidentWriter.MaxEventRows"/>; from <see cref="CutMs"/> on (the first event it left out) none.
    /// </summary>
    private sealed class KeptEvents
    {
        /// <summary>Its window's start when it first kept events (the onset less 2 min then), on the forward clock.</summary>
        public long FromMs;
        /// <summary>The buffer's events numbered below this were looked at.</summary>
        public long Seq;
        public DateTime? CutUtc;
        public long? CutMs;
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Incident, KeptEvents> _kept = new();

    /// <summary>
    /// The incident takes the buffer's events it has not looked at yet: the first time every one from its window's start, then
    /// each new one (a late one, read onto the forward clock earlier, in its place). It stops when it has kept
    /// <see cref="IncidentWriter.MaxEventRows"/>, or when the buffer let events go before it looked at them (a closed
    /// incident re-opened after the buffer moved on): it keeps none after that point, so it never has a gap.
    /// </summary>
    private KeptEvents KeepEvents(Incident inc)
    {
        HashSet<LifecycleEvent>? had = null;
        if (!_kept.TryGetValue(inc, out var k))
        {
            k = new KeptEvents { FromMs = EventWindowStartMs(inc), Seq = _eventsDropped };
            _kept.Add(inc, k);
            // Events it came with (none, but for one made with them) stay, in time order, each once.
            inc.Events = inc.Events.OrderBy(e => e.Ms).ToList();
            if (inc.Events.Count > 0) had = new HashSet<LifecycleEvent>(inc.Events, ReferenceEqualityComparer.Instance);
        }
        if (k.CutMs is not null) return k;
        long start = k.Seq - _eventsDropped;
        if (start < 0)
        {
            // Let go of before this incident looked at them: from the last it looked at on, it keeps none.
            var last = inc.Events.Count > 0 ? inc.Events[^1] : null;
            (k.CutUtc, k.CutMs) = last is null ? (inc.OnsetUtc.AddMinutes(-2), k.FromMs) : (last.TimeUtc, last.Ms + 1);
            return k;
        }
        var kept = inc.Events;
        for (int i = (int)start; i < _events.Count; i++)
        {
            var e = _events[i];
            if (e.Ms < k.FromMs || (had is not null && had.Contains(e))) continue;
            if (kept.Count >= IncidentWriter.MaxEventRows) { (k.CutUtc, k.CutMs) = (e.TimeUtc, e.Ms); break; }
            if (kept.Count == 0 || kept[^1].Ms <= e.Ms) kept.Add(e);
            else
            {
                // A late event: in its place by time, after those of the same moment.
                int lo = 0, hi = kept.Count;
                while (lo < hi) { int mid = (lo + hi) >>> 1; if (kept[mid].Ms <= e.Ms) lo = mid + 1; else hi = mid; }
                kept.Insert(lo, e);
            }
        }
        k.Seq = _eventsAdded;
        return k;
    }

    /// <summary>Lifecycle events in [<paramref name="fromMs"/>, <paramref name="toMs"/>] on the clock that only goes forward, in the order they happened.</summary>
    public List<LifecycleEvent> EventsBetween(long fromMs, long toMs) =>
        _events.Where(e => e.Ms >= fromMs && e.Ms <= toMs).OrderBy(e => e.Ms).ToList();

    public void Process(History.History h, Detector detector, IReadOnlyList<Alert> alerts, Moment now)
    {
        DateTime nowUtc = now.Utc;
        _nowMs = now.Ms;
        if (h.Count == 0) return;
        // Every interval here is timed on the clock that only goes forward (now.Ms); the times of day are shown and written.
        long nowMs = now.Ms;
        int cut = _events.FindIndex(e => nowMs - e.Ms <= Ms(EventKeep));
        if (cut > 0)
        {
            foreach (var inc in _open.Values) KeepEvents(inc); // what they have not taken yet, before it goes
            DropEvents(cut);
        }
        _clears = detector.Clears;
        LowerHighsByClearsJustMeasured(h);

        foreach (var a in alerts)
        {
            if (a.Kind == AlertKind.Release) OnRelease(h, a);
            else OnGrowth(h, a);
        }

        // The one written longest ago first, so the write budget is shared out when it has room for only some; the rows of
        // events.csv left for later after every refresh due.
        foreach (var inc in _open.Values.OrderBy(i => i.LastWriteMs).ToList())
        {
            // While PoolWatch's own clear is being measured the detector holds: its silence is not the growth stopping.
            if (!detector.Holding && nowMs - inc.QuietSinceMs >= Ms(TimeSpan.FromMinutes(_settings.IncidentQuietMinutes)))
                Close(h, inc, nowUtc, nowMs, $"no new rise for {_settings.IncidentQuietMinutes:0.#} min");
            else if (nowMs - (inc.EpisodeStartMs ?? inc.OpenedMs) >= Ms(TimeSpan.FromMinutes(_settings.IncidentMaxMinutes)))
                Close(h, inc, nowUtc, nowMs, $"open for the maximum {_settings.IncidentMaxMinutes:0} min");
            else if (inc.Dirty && nowMs - inc.LastRankMs >= Ms(RewriteInterval))
            {
                // The episode (onset, end, growth: what the window's row and the tray read) is measured again at once.
                var window = Measure(h, inc, nowMs);
                (inc.LastRankUtc, inc.LastRankMs) = (nowUtc, nowMs);
                // Disk budget: rewrite on a new top suspect, 25% more growth, or every few minutes, while the write budget
                // has room (else at a later re-rank). The suspects, the costly part, are ranked again just before every
                // rewrite, so each report holds the ranking of the moment it is written; while the budget has room every
                // SuspectsInterval, to see whether the top suspect changed; and at least every SuspectsRefreshInterval, for the
                // cause shown from memory. Under load the budget lets few rewrites through: a ranking nothing would be
                // written from is not made more often than that.
                bool grew = inc.Growth > inc.WrittenGrowth + inc.WrittenGrowth / 4 + (1 << 20);
                bool due = nowMs - inc.LastWriteMs >= Ms(MaxRewriteInterval);
                bool room = Budget.HasRoom(nowMs);
                long sinceRanked = inc.SuspectsRankMs is { } ranked ? nowMs - ranked : long.MaxValue;
                if (!(room && (grew || due || sinceRanked >= Ms(SuspectsInterval))) && sinceRanked < Ms(SuspectsRefreshInterval))
                {
                    if (grew || due) Budget.Hold(); // a rewrite that was due waits for the budget
                    continue;
                }
                if (window is not null) RankSuspects(h, inc, window, nowMs);
                if (grew || due || inc.Suspects.FirstOrDefault()?.Key != inc.WrittenTop) Write(h, inc, mustWrite: false);
            }
        }
        // Rows come last: only while every open incident's report is up to date (none has waited for its refresh), so they
        // never take the room a refresh needs; then the open incidents', then the closed ones' (below).
        bool reportsCurrent = _open.Values.All(i => nowMs - i.LastWriteMs < Ms(MaxRewriteInterval));
        if (reportsCurrent)
            foreach (var inc in _open.Values.OrderBy(i => i.LastWriteMs).ToList())
                if (_writer.EventsPending(inc) && !AppendEvents(h, inc)) break; // the budget has no room left

        if (!detector.Holding) CheckRetention(h, detector, now);
        foreach (var freed in detector.ClearedRetentions) NoteRetentionCleared(freed, nowUtc);
        // A closed incident is rewritten on a change, and at most every half hour for alerts counted on its timeline after
        // it closed, while the write budget has room; the rows of events.csv a write left for later follow when it has.
        foreach (var inc in _closed.OrderBy(i => i.LastWriteMs).ToList())
        {
            if (inc.Dirty || (inc.CountPending && nowMs - inc.LastWriteMs >= Ms(ClosedRewriteInterval))) Write(h, inc, mustWrite: false);
            else if (reportsCurrent && _writer.EventsPending(inc) && nowMs - inc.LastWriteMs >= Ms(RewriteInterval)) AppendEvents(h, inc);
        }
        // The processes of an incident's window plus 10 minutes on each side (sized by time, not by a count) stay
        // resolvable in the registry while the incident is open or closed in the last 15 min (its trace may still be
        // parsing), so the trace's PIDs resolve to who ran them.
        bool capturing = CaptureRunning?.Invoke() == true;
        var (since, until) = ProtectWindow(_open.Values.Concat(_closed.Where(c => nowMs - c.ClosedMs!.Value < Ms(TimeSpan.FromMinutes(15)))), capturing);
        // A ranking names only processes the raw history holds points for: one that exited before its first sample (less the
        // margin) can be in no ranking, however early an incident open longer than the history began. A trace can name only
        // processes of its own minutes, which are recent.
        if (since is { } s0 && !capturing) since = Math.Max(s0, h.Ms[0] - Ms(ProtectMargin));
        _registry.ProtectSinceMs = since;
        _registry.ProtectUntilMs = until;
        // While a trace records or parses, it may name any process of the window; else a ranking names only those a snapshot
        // listed, and their ancestors (ProcessRegistry.ProtectAll).
        _registry.ProtectAll = capturing;
        int old = _closed.FindIndex(i => nowMs - i.ClosedMs!.Value < Ms(ReleaseAttachWindow));
        if (old < 0) old = _closed.Count;
        // At most MaxClosed closed incidents stay in memory, the oldest closed leaving first as at 6 hours: a PC that opens
        // many in a few hours would otherwise hold each one's events and owners for 6 hours.
        old = Math.Max(old, _closed.Count - MaxClosed);
        // Their folders stay on disk; an events.csv the write budget had not brought up to date says to when it holds its events.
        for (int i = 0; i < old; i++)
            try { Budget.Spend(_writer.NoteUnwritten(_closed[i]), nowMs, must: true); }
            catch (Exception ex) { _host.Log($"incident {_closed[i].Id}: events.csv write failed: {ex.Message}"); }
        if (old > 0) _closed.RemoveRange(0, old);
    }

    /// <summary>PoolWatch's own cache clears as the detector read them last: <see cref="Rank"/> and the release record measure across them as it does.</summary>
    private OwnClears _clears = OwnClears.None;
    /// <summary>The sample at which the high-water marks were last lowered for a clear, so one clear lowers them once.</summary>
    private long? _loweredAtMs;

    /// <summary>
    /// A clear measured at this sample (the newest) that dropped an incident's series: that growth incident's high-water
    /// mark comes down by the drop (<see cref="ClearSpan.Drop"/>). The drop is the clear's result, not the growth ending,
    /// so a rise after it counts from the level the clear left, as the detector measures it, and not only once it passes
    /// the level before the clear. An open incident that was still rising when the clear ran counts its quiet minutes from
    /// the measurement (<see cref="Incident.QuietFromUtc"/>; its last rise stays the last one seen): the interval hid its
    /// rise (the rules skip it), it did not stop. A clear measured while a later one
    /// still is shares that one's interval (<see cref="OwnClears.SpansIn"/>), so both lower the marks once, when it is.
    /// </summary>
    private void LowerHighsByClearsJustMeasured(History.History h)
    {
        int n = h.Count;
        if (h.Ms[n - 1] == _loweredAtMs) return;
        var spans = _clears.SpansIn(h);
        for (int k = 0; k < spans.Count; k++)
        {
            var c = spans.Spans[k];
            if (c.Measured != n - 1 || c.Before < 0) continue;
            _loweredAtMs = h.Ms[n - 1];
            foreach (var inc in _open.Values.Concat(_closed))
            {
                if (inc.Kind is not (IncidentKind.Growth or IncidentKind.NonPagedTotal)) continue;
                // What it freed of the tag, as Measure's stitch adds it back while the history holds the clear: kept, for when it
                // no longer does (an incident open longer than the raw history).
                var (bp, bnp) = h.TagAt(inc.Tag, c.Before);
                var (mp, mnp) = h.TagAt(inc.Tag, c.Measured);
                if (ClearSpan.Drop(bp + bnp, mp + mnp) is > 0 and var tagDrop) inc.ClearDrops.Add((h.Ms[c.Measured], spans.TimeOf(c), tagDrop));
                long drop = ClearSpan.Drop(Level(h, inc, c.Before), Level(h, inc, c.Measured));
                if (drop == 0) continue;
                inc.HighestSeenBytes -= drop;
                if (inc.IsOpen && inc.LastRiseMs >= h.Ms[c.Before] - Ms(AlertRunGap)) (inc.QuietFromUtc, inc.QuietFromMs) = (h.Times[n - 1], h.Ms[n - 1]);
            }
        }
    }

    /// <summary>The series an incident's high-water mark follows: the committed non-paged total for the system incident, else its tag.</summary>
    private static long Level(History.History h, Incident inc, int i)
    {
        if (inc.Kind == IncidentKind.NonPagedTotal) return (long)h.Totals[i].CommittedNonPaged;
        var (p, np) = h.TagAt(inc.Tag, i);
        return p + np;
    }

    /// <summary>
    /// A "Capture now" incident for the given tags (the fastest-growing ones); the capture result is attached when
    /// the trace finishes.
    /// </summary>
    public Incident CreateCapture(History.History h, IReadOnlyList<PoolTag> tags, DateTime nowUtc)
    {
        long nowMs = NewestMs(h);
        var inc = Create(tags.Count > 0 ? tags[0] : PoolTag.Parse("File"), IncidentKind.Capture, nowUtc, nowMs);
        (inc.OnsetUtc, inc.OnsetMs) = (nowUtc, nowMs);
        inc.LastAlertUtc = inc.LastRiseUtc = nowUtc;
        inc.LastRiseMs = inc.LastAlertMs = nowMs;
        (inc.ClosedUtc, inc.ClosedMs) = (nowUtc, nowMs);
        _closed.Add(inc);
        Rank(h, inc, nowMs);
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
    public void ApplyCapture(History.History h, Incident inc, CaptureResult result)
    {
        inc.Capture = result;
        // The CPU picture next to the pool size at that moment.
        if (result.Cpu is { } cpu && h.Count > 0) cpu.PoolBytes = (long)(h.Totals[^1].CommittedPaged + h.Totals[^1].CommittedNonPaged);
        if (inc.Kind == IncidentKind.Capture && result.EndMs > result.StartMs)
        {
            // A manual capture's window is the trace itself, as the runner read it on both clocks.
            (inc.OnsetUtc, inc.OnsetMs) = (result.StartUtc, result.StartMs);
            (inc.ClosedUtc, inc.ClosedMs) = (result.EndUtc, result.EndMs);
        }
        Rank(h, inc, inc.ClosedMs ?? NewestMs(h));
        Write(h, inc, full: true);
        _host.Log($"incident {inc.Id}: capture attached ({(result.PoolEventsParsed ? $"{result.PoolEvents} pool events" : result.Error ?? "no pool events")})");
    }

    /// <summary>A suspect's session details arrived (transcript search): rewrite the incidents that name it.</summary>
    public void Touch(ProcessKey key)
    {
        foreach (var inc in All.Where(i => i.Suspects.Any(s => s.Key == key)))
        {
            inc.Dirty = true;
            if (inc.IsOpen) (inc.WrittenTop, inc.SuspectsRankMs) = (null, null); // forces the next re-rank to rank and write
        }
    }

    /// <summary>Closes and writes every open incident (end of a headless run, app exit).</summary>
    public void Finish(History.History h, DateTime nowUtc)
    {
        _finishing = true;
        foreach (var inc in _open.Values.ToList()) Close(h, inc, nowUtc, NewestMs(h));
        foreach (var inc in _closed.Where(i => i.Dirty || i.CountPending || _writer.EventsPending(i))) Write(h, inc);
    }

    private void OnGrowth(History.History h, Alert a)
    {
        if (_suppressed.TryGetValue(a.Tag, out long until) && _nowMs < until) return;
        bool total = a.Kind == AlertKind.NonPagedTotal;
        var (p, np) = h.TagAt(a.Tag, h.Count - 1);
        // For the system-level rule, "rising" is judged on the committed non-paged total, not on the chosen tag.
        long cur = total ? (long)h.Totals[^1].CommittedNonPaged : p + np;
        var key = total ? TotalKey : a.Tag;
        if (_open.TryGetValue(key, out var inc))
        {
            // Every alert is on the timeline. The rule fires every sample while it holds: a new run of it, or 25% more
            // growth, is its own entry; the repeats in between are counted on that entry.
            NoteAlert(inc, a);
            (inc.LastAlertUtc, inc.LastAlertMs) = (a.TimeUtc, a.Ms);
            // Small wobbles on a plateau must not keep the incident open: count a rise only above the high + 1/64 of the alert.
            if (cur > inc.HighestSeenBytes + a.Bytes / 64) { inc.HighestSeenBytes = cur; (inc.LastRiseUtc, inc.LastRiseMs) = (a.TimeUtc, a.Ms); }
            // One entry per rule (kind + window): the rule text embeds live numbers, so it is not a key.
            var same = inc.Alerts.FindIndex(x => x.Kind == a.Kind && x.Window == a.Window);
            if (same < 0) inc.Alerts.Add(a);
            else if (a.Bytes > inc.Alerts[same].Bytes)
                inc.Alerts[same] = a.OnsetMs <= inc.Alerts[same].OnsetMs ? a : a with { OnsetUtc = inc.Alerts[same].OnsetUtc, OnsetMs = inc.Alerts[same].OnsetMs };
            if (a.OnsetMs < inc.OnsetMs) (inc.OnsetUtc, inc.OnsetMs) = (a.OnsetUtc, a.OnsetMs);
            inc.Dirty = true;
            return;
        }
        // The window rule keeps firing for a while after growth stops; that is the closed episode, not a new one.
        // Those alerts are still on its timeline (counted), not dropped silently.
        var last = _closed.LastOrDefault(i => Key(i) == key && i.Kind is IncidentKind.Growth or IncidentKind.NonPagedTotal);
        if (last is not null && a.OnsetMs <= last.ClosedMs && cur <= last.HighestSeenBytes + a.Bytes / 64)
        {
            NoteClosed(last, a, "alert after it closed");
            return;
        }
        if (total && last is not null && a.Ms - last.ClosedMs!.Value < Ms(TimeSpan.FromMinutes(_settings.IncidentQuietMinutes * 3)) &&
            cur <= last.HighestSeenBytes + _settings.NonPagedTotalRiseBytesPer10Min / 4)
        {
            NoteClosed(last, a, "alert after it closed");
            return;
        }
        // One incident per tag per episode: the same tag growing again within an hour of the last incident closing
        // re-opens that incident (same folder, timeline entry) instead of a new folder each time.
        if (last is not null && a.Ms - last.ClosedMs!.Value < Ms(TimeSpan.FromMinutes(ReopenMinutes)))
        {
            _closed.Remove(last);
            NoteAlert(last, a, $"re-opened: {a.Rule} ({Fmt.SignedBytes(a.Bytes)}), {Fmt.Duration(TimeSpan.FromMilliseconds(a.Ms - last.ClosedMs.Value))} after it closed");
            if (last.Release is not null || last.Retention is not null)
                AddTimeline(last, a.TimeUtc, "earlier episode: " + string.Join("; ", new[]
                {
                    last.Release is { } rel ? $"released {Fmt.Bytes(rel.Dropped)} at {Fmt.Clock(rel.TimeUtc)}" : null,
                    last.Retention is not null ? "retained after its processes went idle" : null,
                }.Where(x => x is not null)));
            last.Release = null;
            last.Retention = null;
            (last.ClosedUtc, last.ClosedMs) = (null, null);
            (last.EndUtc, last.EndMs) = (null, null);
            (last.EpisodeStartUtc, last.EpisodeStartMs) = (a.TimeUtc, a.Ms); // the maximum duration counts from the re-open, not from the first episode
            last.LastAlertUtc = last.LastRiseUtc = a.TimeUtc;
            last.LastRiseMs = last.LastAlertMs = a.Ms;
            (last.QuietFromUtc, last.QuietFromMs) = (null, null); // a clear's quiet start belongs to the episode it ran in
            last.HighestSeenBytes = Math.Max(last.HighestSeenBytes, cur);
            last.Reopened++;
            var same = last.Alerts.FindIndex(x => x.Kind == a.Kind && x.Window == a.Window);
            if (same < 0) last.Alerts.Add(a); else last.Alerts[same] = a;
            _open[key] = last;
            _host.Log($"incident {last.Id} re-opened: {a.Tag.ToDisplay()} {a.Rule} ({Fmt.SignedBytes(a.Bytes)})");
            _host.RequestHandleScan();
            Rank(h, last, a.Ms);
            Write(h, last, full: false); // its report at once; its chart and CSVs at its first refresh
            Opened?.Invoke(last, a); // a new episode gets its own confirmation trace and notification
            return;
        }
        inc = Create(a.Tag, a.Kind == AlertKind.NonPagedTotal ? IncidentKind.NonPagedTotal : IncidentKind.Growth, a.TimeUtc, a.Ms);
        NoteAlert(inc, a, $"opened: {a.Rule} ({Fmt.SignedBytes(a.Bytes)})");
        (inc.OnsetUtc, inc.OnsetMs) = (a.OnsetUtc, a.OnsetMs);
        inc.LastAlertUtc = inc.LastRiseUtc = a.TimeUtc;
        inc.LastRiseMs = inc.LastAlertMs = a.Ms;
        inc.HighestSeenBytes = cur;
        inc.Alerts.Add(a);
        _open[key] = inc;
        _host.Log($"incident {inc.Id}: {a.Tag.ToDisplay()} {a.Rule} ({Fmt.SignedBytes(a.Bytes)})");
        _host.RequestHandleScan();
        if (_knowledge.NeedsDriverScan(a.Tag)) _host.RequestTagScan([a.Tag]);
        Rank(h, inc, a.Ms);
        Write(h, inc, full: false); // its report at once; its chart and CSVs at its first refresh
        Opened?.Invoke(inc, a);
    }

    private void OnRelease(History.History h, Alert a)
    {
        if (_suppressed.TryGetValue(a.Tag, out long until) && _nowMs < until) return;
        var inc = _open.GetValueOrDefault(a.Tag) ?? _closed.LastOrDefault(i => i.Tag == a.Tag && i.Kind == IncidentKind.Growth);
        bool created = inc is null;
        if (inc is null)
        {
            inc = Create(a.Tag, IncidentKind.Release, a.TimeUtc, a.Ms);
            (inc.OnsetUtc, inc.OnsetMs) = (a.OnsetUtc, a.OnsetMs);
            (inc.LastAlertUtc, inc.LastAlertMs) = (a.TimeUtc, a.Ms);
            (inc.ClosedUtc, inc.ClosedMs) = (a.TimeUtc, a.Ms);
            _closed.Add(inc);
        }
        inc.Alerts.Add(a);
        // A release is on the timeline too, counted like any alert.
        NoteAlert(inc, a, label: "alert (release)");
        var release = BuildRelease(h, a);
        if (inc.Release is null || release.Dropped > inc.Release.Dropped) inc.Release = release;
        inc.Dirty = true;
        _host.Log($"incident {inc.Id}: {a.Tag.ToDisplay()} {a.Rule}");
        if (created)
        {
            if (_knowledge.NeedsDriverScan(a.Tag)) _host.RequestTagScan([a.Tag]);
            Rank(h, inc, a.Ms);
            Write(h, inc);
            Opened?.Invoke(inc, a);
        }
    }

    /// <summary>An incident's folder name: when it opened (local time) and its tag (<see cref="PoolTag.ToFileSafe"/>); "-2" and on follow when two shared a second.</summary>
    public static string FolderName(DateTime openedUtc, PoolTag tag) => $"{openedUtc.ToLocalTime():yyyyMMdd-HHmmss}-{tag.ToFileSafe()}";

    private static readonly System.Text.RegularExpressions.Regex FolderNamePattern = new(
        @"^[0-9]{8}-[0-9]{6}-[A-Za-z0-9_]{0,4}-[0-9a-f]{8}(-[0-9]+)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Whether a folder name is an incident's (<see cref="FolderName"/>, with "-2" and on), not a finding report's
    /// (<c>yyyyMMdd-HHmmss-&lt;kind&gt;-&lt;subject&gt;</c>: a spawn storm, a crash, an experiment).
    /// </summary>
    public static bool IsFolderName(string name) => FolderNamePattern.IsMatch(name);

    private Incident Create(PoolTag tag, IncidentKind kind, DateTime nowUtc, long nowMs)
    {
        string id = FolderName(nowUtc, tag);
        string dir = Path.Combine(_host.IncidentsDir, id);
        for (int i = 2; Directory.Exists(dir); i++) dir = Path.Combine(_host.IncidentsDir, $"{id}-{i}");
        CreatedCount++;
        return new Incident { Id = Path.GetFileName(dir), Dir = dir, Tag = tag, Kind = kind, OpenedUtc = nowUtc, OpenedMs = nowMs };
    }

    /// <summary>An incident whose tag grows again within this many minutes of closing is re-opened.</summary>
    public const double ReopenMinutes = 60;

    /// <summary>The margin kept around an incident's window for its start records (At least the window plus 10 min).</summary>
    public static readonly TimeSpan ProtectMargin = TimeSpan.FromMinutes(10);

    /// <summary>Whether a confirmation trace is recording or being parsed now (set by the pipeline): the window stays open-ended then.</summary>
    public Func<bool>? CaptureRunning { get; set; }

    /// <summary>
    /// The registry's protected window for these incidents, on the clock that only goes forward: from the earliest onset
    /// minus 10 min to the latest end of a growth or its trace plus 10 min; open-ended (null) while one is still open or a
    /// trace is running (a trace the governor held back can start after the growth ended: its processes must still be
    /// resolvable). (null, null) when there are none.
    /// </summary>
    public static (long? sinceMs, long? untilMs) ProtectWindow(IEnumerable<Incident> incidents, bool captureRunning = false)
    {
        long? since = null, until = null;
        bool open = false;
        foreach (var i in incidents)
        {
            if (since is null || i.OnsetMs < since) since = i.OnsetMs;
            if (i.IsOpen) { open = true; continue; }
            long end = i.EndMs ?? i.LastRiseMs;
            if (i.Capture is { } cap && cap.EndMs > end) end = cap.EndMs; // a trace taken after the growth ended
            if (until is null || end > until) until = end;
        }
        if (since is null) return (null, null);
        return (since - Ms(ProtectMargin), open || captureRunning ? null : until + Ms(ProtectMargin));
    }

    /// <summary>A gap longer than this between two alerts of one rule starts a new run (a new timeline entry).</summary>
    public static readonly TimeSpan AlertRunGap = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Puts an alert on the incident's timeline. <paramref name="headline"/> (opened / re-opened) is always its own
    /// entry and starts a run. Otherwise a new run of the rule (first alert, or after a gap of over
    /// <see cref="AlertRunGap"/>) or 25% more growth than its entry is a new entry, and a repeat in between is counted on
    /// the run's entry: "alert: grew > 200 MB in 10 min (+230 MB); fired 37 more times to 06:54:10, latest +280 MB".
    /// When the run's entry is no longer on the timeline (it keeps 500, the oldest alert entries go first),
    /// the alert starts a new entry instead of being counted where nobody sees it. <paramref name="label"/> names the kind
    /// of entry ("alert", "alert after it closed", "alert (release)"); a release's bytes are shown as a drop.
    /// </summary>
    /// <returns>True when the alert made a new timeline entry; false when it was counted on an existing one.</returns>
    public static bool NoteAlert(Incident inc, Alert a, string? headline = null, string label = "alert")
    {
        var key = (a.Kind, a.Window);
        long Shown(long bytes) => a.Kind == AlertKind.Release ? -bytes : bytes;
        if (headline is null && inc.AlertRuns.TryGetValue(key, out var run) && a.Ms - run.LastMs <= Ms(AlertRunGap) && a.Bytes < run.EntryBytes + run.EntryBytes / 4)
        {
            int i = inc.Timeline.FindLastIndex(x => x.TimeUtc == run.EntryTimeUtc && x.Text.StartsWith(run.EntryText, StringComparison.Ordinal));
            if (i >= 0)
            {
                run.More++;
                (run.LastUtc, run.LastMs) = (a.TimeUtc, a.Ms);
                run.LatestBytes = a.Bytes;
                inc.Timeline[i] = (run.EntryTimeUtc, $"{run.EntryText}; fired {run.More} more time{(run.More == 1 ? "" : "s")} to {Fmt.Clock(run.LastUtc)}, latest {Fmt.SignedBytes(Shown(run.LatestBytes))}");
                return false;
            }
            // The entry was evicted: fall through to a new one.
        }
        string text = headline ?? $"{label}: {a.Rule} ({Fmt.SignedBytes(Shown(a.Bytes))})";
        AddTimeline(inc, a.TimeUtc, text);
        inc.AlertRuns[key] = new AlertRun { EntryTimeUtc = a.TimeUtc, EntryText = text, LastUtc = a.TimeUtc, LastMs = a.Ms, EntryBytes = a.Bytes, LatestBytes = a.Bytes };
        return true;
    }

    /// <summary>
    /// An alert for a CLOSED incident (the window rule keeps firing after the growth stopped, a
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

    private void Close(History.History h, Incident inc, DateTime nowUtc, long nowMs, string why = "")
    {
        AddTimeline(inc, nowUtc, "closed" + (why.Length > 0 ? ": " + why : ""));
        inc.AlertRuns.Clear(); // alerts after this are their own entries ("alert after it closed"), not counted above "closed"
        (inc.ClosedUtc, inc.ClosedMs) = (nowUtc, nowMs);
        _open.Remove(Key(inc));
        Rank(h, inc, nowMs);
        _closed.Add(inc);
        Write(h, inc);
        _host.Log($"incident {inc.Id} closed: {Fmt.SignedBytes(inc.Growth)} from {Fmt.Clock(inc.OnsetUtc)}");
    }

    /// <summary>
    /// Re-derives the growth episode and the suspect ranking from the history, up to <paramref name="nowUtc"/> (a time of
    /// day: read onto the clock that only goes forward by the newest sample, <see cref="Moment.MsOf"/>).
    /// </summary>
    public void Rank(History.History h, Incident inc, DateTime nowUtc)
    {
        if (h.Count == 0) return;
        Rank(h, inc, new Moment(h.Times[^1], h.Ms[^1]).MsOf(nowUtc));
    }

    /// <summary>
    /// The onset of the alert whose onset is earliest on the clock that only goes forward (<see cref="Alert.OnsetMs"/>), with
    /// that alert's own time of day; <paramref name="orUtc"/> and <paramref name="orMs"/> without alerts. A system clock set
    /// back between two alerts' onsets cannot give one alert's forward onset with another's time of day.
    /// </summary>
    internal static (DateTime Utc, long Ms) EarliestOnset(IReadOnlyList<Alert> alerts, DateTime orUtc, long orMs) =>
        alerts.MinBy(a => a.OnsetMs) is { } first ? (first.OnsetUtc, first.OnsetMs) : (orUtc, orMs);

    /// <summary>
    /// Re-derives the growth episode and the suspect ranking from the history, up to <paramref name="toMs"/>. The window,
    /// its onset and its end are found on the clock that only goes forward (the samples' <see cref="History.History.Ms"/>,
    /// the alerts' <see cref="Alert.OnsetMs"/>), and the ranking reads them so (<see cref="CorrelationWindow.FromMs"/>), so a
    /// system clock set back or forward inside the window moves none of them; the times of day kept are the samples'.
    /// </summary>
    private void Rank(History.History h, Incident inc, long toMs)
    {
        if (Measure(h, inc, toMs) is { } window) RankSuspects(h, inc, window, toMs);
    }

    /// <summary>
    /// The growth episode of <see cref="Rank"/> alone: its window, onset, end and levels (what the window's row, the tray and
    /// a refresh's triggers read), without the suspects; null when the history cannot say (then nothing is ranked).
    /// </summary>
    private CorrelationWindow? Measure(History.History h, Incident inc, long toMs)
    {
        if (h.Count < 3) return null;
        toMs = Math.Min(toMs, h.Ms[^1]);
        // Anchor on the alerts' own onset so repeated re-ranks cannot drift the window earlier and earlier.
        var growthAlerts = inc.Alerts.Where(a => a.Kind != AlertKind.Release).ToList();
        var release = inc.Kind == IncidentKind.Release ? inc.Alerts.Where(a => a.Kind == AlertKind.Release).MinBy(a => a.OnsetMs) : null;
        var growthOnset = EarliestOnset(growthAlerts, inc.OnsetUtc, inc.OnsetMs);
        DateTime onset = inc.Kind == IncidentKind.Release && inc.Release is { } r ? r.FromUtc : growthOnset.Utc;
        long onsetMs = release is not null ? release.OnsetMs : growthOnset.Ms;
        long lead = Math.Max(60_000, (toMs - onsetMs) / 2);
        long fromMs = Math.Max(h.Ms[0], onsetMs - lead);
        int iFrom = h.IndexAtOrAfterMs(fromMs), iTo = Math.Max(iFrom, h.IndexAtOrAfterMs(toMs + 1) - 1);
        if (iFrom >= h.Count) return null;
        var series = h.TagSeries(inc.Tag, iFrom);
        if (series.Length == 0) return null;
        if (iTo - iFrom + 1 < series.Length) series = series[..(iTo - iFrom + 1)];
        // PoolWatch's own clears, on the detector's stitch: their interval is neither the growth's start nor its end (it
        // takes the level just before it), and their drop is not the growth ending (`grown` adds it back). The levels said
        // (start, now) are the tag's own.
        var spans = _clears.SpansIn(h);
        var grown = series;
        var dropped = new List<ClearSpan>();
        if (spans.Count > 0)
        {
            grown = (long[])series.Clone();
            spans.Stitch(series, iFrom, addBack: false, fill: true);
            spans.Stitch(grown, iFrom, fill: true, dropped: dropped);
        }

        DateTime? end = null;
        long? endMs = null;
        if (inc.Kind != IncidentKind.Release && growthOnset.Ms < h.Ms[0] && inc.OnsetMs < h.Ms[0])
        {
            // The raw history no longer reaches back to the onset (an incident open longer than the history keeps): the onset
            // and the start level are as last measured, never moved to the history's first sample, which is not where the
            // growth began (its growth would shrink as the history moves on, and its events.csv would lose its rows as "before
            // the window"). Its end and its highest point are measured on what the history holds, with what PoolWatch's own
            // clears freed of it since the onset added back: those the history holds on the stitch (`dropped`), those that left
            // it as they were measured (Incident.ClearDrops), so a clear that left the history is still not the growth ending.
            var cp = Detector.FindChangePoints(grown);
            (onset, onsetMs) = (inc.OnsetUtc, inc.OnsetMs);
            if (cp.Rise > 0 && cp.EndIndex is { } e && e < iTo - iFrom) (end, endMs) = (h.Times[iFrom + e], h.Ms[iFrom + e]);
            (inc.EndUtc, inc.EndMs) = (end, endMs);
            long gone = 0;
            int goneCount = 0;
            DateTime? goneAt = null;
            foreach (var (ms, at, drop) in inc.ClearDrops)
            {
                if (ms <= inc.OnsetMs || dropped.Exists(s => h.Ms[s.Measured] == ms)) continue;
                gone += drop;
                goneCount++;
                goneAt = at;
            }
            inc.PeakBytes = Math.Max(inc.PeakBytes, series[0] + gone + (grown.Max() - grown[0]));
            inc.ClearedBytes = gone + grown[^1] - series[^1] - (grown[0] - series[0]);
            inc.ClearedAtUtc = goneCount + dropped.Count == 1 ? goneCount == 1 ? goneAt : spans.TimeOf(dropped[0]) : null;
        }
        else if (inc.Kind != IncidentKind.Release)
        {
            // Refine the onset with change-point detection on the series after the baseline start.
            var cp = Detector.FindChangePoints(grown);
            if (cp.Rise > 0)
            {
                long cpOnsetMs = h.Ms[iFrom + cp.OnsetIndex];
                if (cpOnsetMs >= fromMs && cpOnsetMs <= toMs) (onset, onsetMs) = (h.Times[iFrom + cp.OnsetIndex], cpOnsetMs);
                if (cp.EndIndex is { } e && iFrom + e < iTo) (end, endMs) = (h.Times[iFrom + e], h.Ms[iFrom + e]);
            }
            inc.OnsetUtc = onset;
            (inc.EndUtc, inc.EndMs) = (end, endMs);
            int o = Math.Clamp(h.IndexAtOrAfterMs(onsetMs) - iFrom, 0, series.Length - 1);
            inc.OnsetMs = h.Ms[iFrom + o];
            inc.StartBytes = series[o];
            inc.PeakBytes = series[o] + (grown[o..].Max() - grown[o]);
            inc.ClearedBytes = grown[^1] - series[^1] - (grown[o] - series[o]);
            // The clears whose drop that is (measured after the onset), said with its time when there was one.
            var freed = dropped.FindAll(s => s.Measured > iFrom + o);
            inc.ClearedAtUtc = freed.Count == 1 ? spans.TimeOf(freed[0]) : null;
        }
        else
        {
            inc.StartBytes = series[0];
            inc.PeakBytes = series.Max();
        }
        inc.CurrentBytes = series[^1];
        var window = new CorrelationWindow(h.Times[iFrom], onset, end, h.Times[iTo]) { FromMs = fromMs, OnsetMs = onsetMs, EndMs = endMs, ToMs = toMs };
        inc.Window = window;
        inc.Dirty = true;
        return window;
    }

    /// <summary>
    /// The suspects of <see cref="Rank"/> over the episode <see cref="Measure"/> found just now (<paramref name="window"/>): the
    /// processes, the trace's attribution and the owners. <paramref name="nowMs"/> is when, on the forward clock.
    /// </summary>
    private void RankSuspects(History.History h, Incident inc, CorrelationWindow window, long nowMs)
    {
        // Owners are grouped from every active process (a session's many short commands are one owner), then the
        // per-process list keeps its top 10 and the owners what the reports read (OwnerRanking.Keep).
        using (Util.Profiler.Stage("incident.rank.suspects")) inc.Suspects = Correlator.Rank(h, _registry, inc.Tag, window, maxSuspects: 2000);
        // The confirmation trace's attribution survives re-ranks (applied to the full list, before the owners are built).
        if (inc.Capture is { } cap)
        {
            CaptureAttribution.Apply(inc, cap, TagGrowth(h, inc.Tag, cap.StartMs, cap.EndMs));
            // Every traced process instance with its identity, rolled up by owner; the trace outranks correlation.
            try
            {
                // Resolved once per trace (the registry forgets the short commands later); only unresolved rows are retried.
                inc.Traced = inc.Traced is { } prevTraced && inc.TracedFrom == cap ? TraceAttribution.Retry(prevTraced, cap, _registry) : TraceAttribution.Resolve(cap, inc.Tag, _registry);
                inc.TracedFrom = cap;
                inc.TraceOwners = TraceAttribution.Owners(inc.Traced);
                var starts = StartsByOwner(_events, inc.OnsetMs, inc.EndMs ?? inc.LastRiseMs);
                foreach (var o in inc.TraceOwners) o.StartsInWindow = starts.GetValueOrDefault(o.Owner.Key); // By identity, not name text
            }
            catch (Exception ex) { _host.Log("trace attribution failed: " + ex.Message); inc.Traced = null; inc.TraceOwners = null; }
        }
        try
        {
            using var owners = Util.Profiler.Stage("incident.rank.owners");
            inc.Owners = OwnerRanking.KeepForIncident(OwnerRanking.Rank(h, inc.Suspects, inc.Tag, window, Math.Max(0, inc.Growth),
                s => Identity.ProcessIdentities.Of(_registry.Ancestry(s.Key) is { Count: > 0 } c ? c : [new ProcessRecord { Key = s.Key, Image = s.Image }])));
        }
        catch { inc.Owners = null; }
        inc.Suspects = inc.Suspects.Take(10).ToList();
        inc.SuspectsRankMs = nowMs;
        SuspectRankings++;
        inc.Dirty = true;
    }

    /// <summary>
    /// Short-lived process starts per owner in [<paramref name="fromMs"/>, <paramref name="toMs"/>] on the clock that only goes
    /// forward (each event's <see cref="LifecycleEvent.Ms"/>), from the rollups (they carry their owner and count), keyed by the
    /// owner's identity key, the same key the trace owners have: two owners with the same display name ("Claude
    /// Code session (PID ...)" for two sessions in one folder, or an app and a service sharing a name) are never added up.
    /// </summary>
    public static Dictionary<string, int> StartsByOwner(IEnumerable<LifecycleEvent> events, long fromMs, long toMs)
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in events)
        {
            if (e.Ms < fromMs || e.Ms > toMs) continue;
            // Rollups are the short-lived starts (under 30 s) by construction: "N short commands" counts only those.
            if (e.Kind == LifecycleKind.Rollup && e.OwnerKey is { } k) d[k] = d.GetValueOrDefault(k) + e.Count;
        }
        return d;
    }

    /// <summary>
    /// The tag's growth over [<paramref name="fromMs"/>, <paramref name="toMs"/>] on the clock that only goes forward (max minus
    /// the minimum before it), or null when the history does not cover it.
    /// </summary>
    internal static long? TagGrowth(History.History h, PoolTag tag, long fromMs, long toMs)
    {
        if (h.Count == 0 || toMs <= fromMs || h.Ms[0] > fromMs) return null;
        int i0 = h.IndexAtOrAfterMs(fromMs), i1 = h.IndexAtOrAfterMs(toMs + 1) - 1;
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
    private void CheckRetention(History.History h, Detector detector, Moment now)
    {
        var nowUtc = now.Utc;
        long quiet = Ms(TimeSpan.FromMinutes(_settings.RetentionMinutes));
        foreach (var inc in _closed)
        {
            if (inc.Kind == IncidentKind.Release || inc.Retention is not null || inc.Release is not null || inc.Growth <= 0) continue;
            var top = inc.Suspects.FirstOrDefault();
            if (top is null) continue;
            // When it stopped, on the clock that only goes forward: its last I/O sample, or its exit as the registry read it
            // when the exit arrived (a record the registry no longer holds: its exit time read onto the forward clock now).
            int? lastIo = LastActivityIndex(h, top.Key);
            long? exitMs = top.ExitUtc is { } exit ? _registry.Get(top.Key)?.ExitMs ?? now.MsOf(exit) : null;
            long? stoppedMs = lastIo is { } li ? h.Ms[li] : exitMs;
            DateTime? stopped = lastIo is { } i2 ? h.Times[i2] : top.ExitUtc;
            if (exitMs is { } xm && (stoppedMs is null || xm < stoppedMs)) (stoppedMs, stopped) = (xm, top.ExitUtc);
            if (stopped is not { } st || stoppedMs is not { } sm || now.Ms - sm < quiet) continue;
            var (p, np) = h.TagAt(inc.Tag, h.Count - 1);
            long cur = p + np;
            if (cur < inc.StartBytes + inc.Growth / 2) continue;
            inc.CurrentBytes = cur;
            // Its rise counts back what PoolWatch's own clears freed of it: said as theirs, not as given back.
            string note = $"{inc.Tag.ToDisplay()} still holds {Fmt.Bytes(cur - inc.StartBytes)} of its {Fmt.Bytes(inc.Growth)} rise " +
                          $"{Fmt.Duration(TimeSpan.FromMilliseconds(now.Ms - sm))} after {top.Image} (PID {top.Key.Pid}) {(top.ExitUtc is not null ? "exited" : "went quiet")}" +
                          (inc.OwnClearsFreed is { } freed ? $"; {freed} of it" : "");
            inc.Retention = new RetentionRecord(nowUtc, st, cur - inc.StartBytes, note);
            detector.MarkRetained(inc.Tag, st, inc.StartBytes + inc.Growth / 2);
            inc.Dirty = true;
            _host.Log($"incident {inc.Id}: retained: {note}");
        }
    }

    /// <summary>
    /// A retained tag that PoolWatch's own clear took under its floor (<see cref="Detector.ClearedRetentions"/>): it no longer
    /// counts as retained, and its incident says why, as the clear's result. What the clear freed was the cache's.
    /// </summary>
    private void NoteRetentionCleared(ClearedRetention freed, DateTime nowUtc)
    {
        var inc = _closed.LastOrDefault(i => i.Tag == freed.Tag && i.Retention is not null);
        if (inc is null) return;
        string text = $"no longer retained: {Fmt.OwnClearsFreed(freed.FreedBytes, freed.ClearAtUtc)} of it, so that memory was the cache's";
        AddTimeline(inc, nowUtc, text);
        inc.Dirty = true;
        _host.Log($"incident {inc.Id}: {text}");
    }

    /// <summary>Time of the last sample in which the process did any I/O (null if it never did in the raw history).</summary>
    internal static DateTime? LastActivity(History.History h, ProcessKey key) => LastActivityIndex(h, key) is { } i ? h.Times[i] : null;

    /// <summary>The last sample in which the process did any I/O (null if it never did in the raw history).</summary>
    internal static int? LastActivityIndex(History.History h, ProcessKey key)
    {
        var pts = h.Points(key); // read in place: this runs for every closed incident on every sample
        for (int i = pts.Last; i > Math.Max(0, pts.First); i--)
            if (pts[i].TotalOps > pts[i - 1].TotalOps) return i;
        return null;
    }

    private ReleaseRecord BuildRelease(History.History h, Alert a)
    {
        // The release's samples and events are picked on the clock that only goes forward (the alert's onset and its own
        // moment, each event's Ms): a system clock set back or forward inside the drop moves none of them.
        int iFrom = h.IndexAtOrAfterMs(a.OnsetMs), iTo = h.Count - 1;
        var (fp, fnp) = h.TagAt(a.Tag, iFrom);
        var (tp, tnp) = h.TagAt(a.Tag, iTo);
        const long slackMs = 30_000;
        var exits = _events.Where(e => e.Kind == LifecycleKind.Exit && e.Ms >= a.OnsetMs - slackMs && e.Ms <= a.Ms)
            .Select(e => $"{Fmt.Clock(e.TimeUtc)} {e.Image} (PID {e.Pid}) exited{(e.ExitCode is { } c ? $", code {c}" : "")}")
            .Take(20).ToList();
        // Processes busiest while the tag dropped (mass deletes show up as I/O; Kernel-File attribution is milestone 3).
        // A process still running is counted from the sample before the drop began. I/O counters are read only at each
        // sample and an exit does not report them, so a process that does its last burst (the delete itself) and exits
        // between two samples never shows that burst. A process gone by the latest sample, whose last sample is no
        // earlier than the one before the drop began, is therefore counted over the release rule's window up to its last
        // sample, and marked as exited with its start time (its PID can already belong to another process).
        int iLive = Math.Max(0, iFrom - 1);
        int iWindow = Math.Min(iLive, h.IndexAtOrAfterMs(a.Ms - Ms(a.Window)));
        var active = new List<(string text, long ops)>();
        foreach (var key in h.ProcessesSince(iLive))
        {
            if (key.Pid is 0) continue;
            // Read in place: releases come in bursts after a cache clear, each over every process of the window.
            var pts = h.Points(key);
            int lastAt = pts.Last;
            if (lastAt < iWindow) continue;
            bool gone = lastAt < iTo;
            int firstAt = Math.Max(pts.First, gone ? iWindow : iLive); // a process's points are contiguous
            if (firstAt > lastAt) continue;
            long ops = pts[lastAt].TotalOps - pts[firstAt].TotalOps;
            if (ops <= 0) continue;
            var rec = _registry.Get(key);
            string name = rec?.DisplayName ?? "unknown process";
            string text = gone
                ? $"{name} (PID {key.Pid}{(key.CreateTime > 0 ? $", started {Fmt.Clock(key.CreateTimeUtc)}" : "")}): {ops:N0} I/O ops up to its " +
                  $"last sample at {Fmt.Clock(h.Times[lastAt])}, " +
                  (rec?.ExitUtc is { } x ? $"exited {Fmt.Clock(x)}" : $"exited by {Fmt.Clock(h.Times[iTo])}") + " (I/O after its last sample is not seen)"
                : $"{name} (PID {key.Pid}): {ops:N0} I/O ops{(rec?.ExitUtc is { } y ? $", exited {Fmt.Clock(y)}" : "")}";
            active.Add((text, ops));
        }
        var activeText = active.OrderByDescending(x => x.ops).Take(5).Select(x => x.text).ToList();
        // The real levels it fell from and to, and what PoolWatch's own clears took from it meanwhile: their drop is their
        // result, not the release, so what fell on its own is the rest, as the detector measured the drop.
        long cleared = 0;
        int clears = 0;
        DateTime? clearedAt = null;
        var spans = _clears.SpansIn(h);
        foreach (var s in spans.Spans)
            if (s.Before >= iFrom && s.Measured <= iTo)
            {
                var (bp, bnp) = h.TagAt(a.Tag, s.Before);
                var (mp, mnp) = h.TagAt(a.Tag, s.Measured);
                long drop = ClearSpan.Drop(bp + bnp, mp + mnp);
                if (drop == 0) continue; // it freed none of this tag: not one of the clears said
                cleared += drop;
                clearedAt = clears++ == 0 ? spans.TimeOf(s) : null;
            }
        return new ReleaseRecord(a.TimeUtc, a.OnsetUtc, fp + fnp, tp + tnp, exits, activeText, WorktreeNote(h, iFrom, a))
        {
            ClearedBytes = cleared, ClearedAtUtc = clearedAt,
        };
    }

    /// <summary>
    /// Git worktree sprawl as context (DESIGN.md section 9): the drop coincides with git worktree removal / prune, or with
    /// processes working under a <c>.git\worktrees</c> or worktree folder.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex WorktreeDelete = new(
        @"\bworktree\s+(remove|prune)\b|(^|[\s;&|""'])(rm\s+-[a-zA-Z]*r[a-zA-Z]*|rmdir\s+/s|rd\s+/s|Remove-Item\b.*-Recurse)\b[^;&|]*worktree",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Whether the command line deletes a git worktree (<see cref="WorktreeDelete"/>). Asked of every start and exit (the
    /// summarizer lists such a command whatever its length): a line without "tree" in it (by ordinal case, which no character
    /// of it has another form of) cannot match, and is not run through the expression; most of a busy PC's scripts are a
    /// kilobyte and have none.
    /// </summary>
    public static bool IsWorktreeDelete(string? commandLine) =>
        commandLine is not null && commandLine.Contains("tree", StringComparison.OrdinalIgnoreCase) && WorktreeDelete.IsMatch(commandLine);

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
        foreach (var e in _events.Where(e => e.Ms >= a.OnsetMs - 30_000 && e.Ms <= a.Ms && e.CommandLine is { } c && WorktreeDelete.IsMatch(c)))
            hits.Add($"{e.Image} (PID {e.Pid}): {e.CommandLine}");
        return hits.Count == 0 ? null
            : "The drop coincides with git worktree cleanup (context, not a crawl: deleting worktrees frees the per-file metadata that was held): " +
              string.Join("; ", hits.Distinct().Take(5));
    }

    /// <summary>
    /// The incident's events (<see cref="IncidentEventSet"/>): what it kept (<see cref="KeepEvents"/>: the shared buffer drops
    /// events after <see cref="Settings.IncidentEventKeepMinutes"/>, the incident keeps the first
    /// <see cref="IncidentWriter.MaxEventRows"/> of its window, and its events.csv is written from those, the earliest not
    /// written yet first), and, for the reports (<paramref name="forReports"/>), the window's events now: those kept and the
    /// newest still in the buffer.
    /// </summary>
    internal IncidentEventSet IncidentEvents(Incident inc, long nowMs, bool forReports = true)
    {
        var k = KeepEvents(inc);
        long from = EventWindowStartMs(inc), to = EventWindowEndMs(inc, nowMs);
        var window = new List<LifecycleEvent>();
        if (forReports)
        {
            foreach (var e in inc.Events) if (e.Ms >= from && e.Ms <= to) window.Add(e);
            // The buffer's events the incident did not keep: those before it kept from, and those after it stopped keeping
            // (until then it kept every one it looked at, and it has looked at them all).
            for (int i = 0; i < _events.Count; i++)
            {
                var e = _events[i];
                if (e.Ms < from || e.Ms > to) continue;
                bool kept = e.Ms >= k.FromMs && (k.CutMs is null ? _eventsDropped + i < k.Seq : Kept(inc, e));
                if (!kept) window.Add(e);
            }
            window.Sort((x, y) => x.Ms.CompareTo(y.Ms));
        }
        return new IncidentEventSet(window, inc.Events, k.FromMs, to, k.CutUtc, k.CutMs);
    }

    /// <summary>Whether the incident kept this very event (a search by time: they are in time order).</summary>
    private static bool Kept(Incident inc, LifecycleEvent e)
    {
        var kept = inc.Events;
        int lo = 0, hi = kept.Count;
        while (lo < hi) { int mid = (lo + hi) >>> 1; if (kept[mid].Ms < e.Ms) lo = mid + 1; else hi = mid; }
        for (int i = lo; i < kept.Count && kept[i].Ms == e.Ms; i++) if (ReferenceEquals(kept[i], e)) return true;
        return false;
    }

    /// <summary>
    /// The events of the growth itself, on the clock that only goes forward: from the onset minus 2 min to the end of the
    /// growth (or now while it climbs) plus 1 min.
    /// </summary>
    public static long EventWindowStartMs(Incident inc) => inc.OnsetMs - 120_000;

    public static long EventWindowEndMs(Incident inc, long nowMs) => (inc.EndMs ?? (inc.IsOpen ? nowMs : inc.LastRiseMs)) + 60_000;

    /// <summary>
    /// At most this many of an incident's events go into its events.csv at one write (the rest at later writes): ten
    /// incidents opening together each get the start of their window at once, and none takes the write budget alone.
    /// </summary>
    public const int EventRowsPerWrite = 100;

    /// <summary>
    /// While an incident is open its chart, timeline.csv and processes.csv (which the window does not read) are written
    /// with a refresh at most this often, and only while the write budget has room to spare
    /// (<see cref="WriteBudget.HasRoomToSpare"/>); its report.json and report.md, which the window's details and Copy
    /// summary read, at every refresh. A closed one's every time.
    /// </summary>
    public TimeSpan OpenChartInterval { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Writes the incident's folder. A write that must be made now (<paramref name="mustWrite"/>: the incident opened,
    /// re-opened, closed, or a trace was attached; and the end of a run) is made whatever the write budget; one that can wait
    /// is made only while it has room, and otherwise stays due (the incident stays dirty). <paramref name="full"/>: the
    /// chart, the two CSVs and up to <see cref="EventRowsPerWrite"/> rows of events.csv (every row left at the end of a
    /// run) too; by default when the incident is closed, at the end of a run, and while it is open when the budget has
    /// room to spare and they were never written or last written <see cref="OpenChartInterval"/> ago. Otherwise (an open
    /// incident's opening, re-opening and refreshes) report.json and report.md, and the rows follow as the budget allows
    /// (<see cref="AppendEvents"/>), after every refresh due.
    /// </summary>
    /// <param name="rows">How many rows of events.csv the write may add (null: as above).</param>
    /// <returns>Whether the folder was written (false: it waits for the budget, or the write failed and was logged).</returns>
    private bool Write(History.History h, Incident inc, bool mustWrite = true, bool? full = null, int? rows = null)
    {
        long nowMs = NewestMs(h);
        if (!mustWrite && !_finishing && !Budget.HasRoom(nowMs))
        {
            Budget.Hold();
            return false;
        }
        bool all = _finishing || (full ?? (!inc.IsOpen || ((_writer.FullWrittenMs(inc) is not { } fw || nowMs - fw >= Ms(OpenChartInterval)) && Budget.HasRoomToSpare(nowMs))));
        rows ??= _finishing ? IncidentWriter.MaxEventRows : all ? EventRowsPerWrite : 0;
        using var stage = Util.Profiler.Stage("incident.write");
        try
        {
            Budget.Spend(_writer.Write(h, inc, IncidentEvents(inc, nowMs), rows.Value, all, nowMs), nowMs, mustWrite || _finishing);
            inc.LastWriteUtc = h.Times.Count > 0 ? h.Times[^1] : DateTime.UtcNow;
            inc.LastWriteMs = nowMs;
            inc.WrittenTop = inc.Suspects.FirstOrDefault()?.Key;
            inc.WrittenGrowth = inc.Growth;
            inc.Dirty = false;
            inc.CountPending = false;
            inc.WriteFailed = false;
            return true;
        }
        catch (Exception ex)
        {
            inc.Dirty = inc.CountPending = false; // do not retry every sample; the next change rewrites it
            inc.WriteFailed = true; // its folder is behind until then (BehindNow)
            _host.Log($"incident {inc.Id}: write failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// An open incident's summary as its report.json would say it now, built from memory (<see cref="IncidentWriter.SummaryJson"/>):
    /// what the window's details and Copy summary show for it, so they are never behind its folder, which the write budget can
    /// hold back. Writes nothing. The caller holds the history lock.
    /// </summary>
    public string SummaryJson(History.History h, Incident inc) => _writer.SummaryJson(h, inc);

    /// <summary>The open incidents' summaries from memory, each built again only when it can have changed (<see cref="LiveSummaries"/>). Under the history lock.</summary>
    public LiveSummaries Live => _live ??= new LiveSummaries(this);
    private LiveSummaries? _live;

    /// <summary>
    /// Writes the incident's whole folder now, every event it kept included, whatever the write budget (it spends it as a
    /// write a reader needs at once): an export of an open incident holds its state now, not a refresh the budget held
    /// back. False when the write failed (logged). The caller holds the history lock.
    /// </summary>
    public bool WriteNow(History.History h, Incident inc) => Write(h, inc, mustWrite: true, full: true, rows: IncidentWriter.MaxEventRows);

    /// <summary>
    /// What of the open incidents' folders is behind what PoolWatch knows of them now, on the forward clock. Their reports
    /// (<see cref="FoldersBehind.Reports"/>): one whose report changed and was last written <see cref="MaxRewriteInterval"/>
    /// ago or more (its refresh waits for the write budget), whose events.csv rows wait, or whose last write failed
    /// (<see cref="FoldersBehind.Failed"/>, not retried until its next change); with how long ago the oldest of them was
    /// written. Their charts, timeline.csv and processes.csv (<see cref="FoldersBehind.Charts"/>), written only while the
    /// budget has room to spare: those never written, or last written <see cref="OpenChartInterval"/> ago or more, the
    /// oldest since when (its opening, for one never written). What About and profile.json say the budget holds back. The
    /// caller holds the history lock.
    /// </summary>
    public FoldersBehind BehindNow(History.History h)
    {
        long nowMs = NewestMs(h);
        int reports = 0, charts = 0, failed = 0;
        long? oldestReport = null, oldestChart = null;
        foreach (var inc in _open.Values)
        {
            bool stale = inc.Dirty && nowMs - inc.LastWriteMs >= Ms(MaxRewriteInterval);
            if (inc.WriteFailed) failed++;
            if (stale || inc.WriteFailed || _writer.EventsPending(inc))
            {
                reports++;
                if (oldestReport is not { } o || inc.LastWriteMs < o) oldestReport = inc.LastWriteMs;
            }
            long chartMs = _writer.FullWrittenMs(inc) ?? inc.OpenedMs;
            if (_writer.FullWrittenMs(inc) is null || nowMs - chartMs >= Ms(OpenChartInterval))
            {
                charts++;
                if (oldestChart is not { } c || chartMs < c) oldestChart = chartMs;
            }
        }
        TimeSpan? Ago(long? ms) => ms is { } m ? TimeSpan.FromMilliseconds(Math.Max(0, nowMs - m)) : null;
        return new FoldersBehind(reports, Ago(oldestReport), charts, Ago(oldestChart), failed);
    }

    /// <summary>The rows of events.csv a write left for later, while the write budget has room; false when it had none.</summary>
    private bool AppendEvents(History.History h, Incident inc)
    {
        long nowMs = NewestMs(h);
        if (!Budget.HasRoom(nowMs)) { Budget.Hold(); return false; }
        using var stage = Util.Profiler.Stage("incident.write");
        try { Budget.Spend(_writer.AppendEvents(inc, IncidentEvents(inc, nowMs, forReports: false), EventRowsPerWrite), nowMs); }
        catch (Exception ex) { _host.Log($"incident {inc.Id}: events.csv write failed: {ex.Message}"); }
        return true;
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    private static long Ms(TimeSpan t) => (long)t.TotalMilliseconds;

    /// <summary>The newest sample's time on the clock that only goes forward (the sample being processed's, before any).</summary>
    private long NewestMs(History.History h) => h.Count > 0 ? h.Ms[^1] : _nowMs;
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}

/// <summary>
/// What of the open incidents' folders is behind (<see cref="IncidentManager.BehindNow"/>): how many reports (report.json,
/// report.md, events.csv's rows), the oldest last written how long ago, and of them how many because their last write
/// failed; how many charts with their timeline.csv and processes.csv, the oldest written (or, never written, opened) how
/// long ago.
/// </summary>
public readonly record struct FoldersBehind(int Reports, TimeSpan? OldestReport, int Charts, TimeSpan? OldestChart, int Failed)
{
    public static readonly FoldersBehind None = new(0, null, 0, null, 0);
}
