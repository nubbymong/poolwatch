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
    /// How long after a closed incident's window ended every late event of the window can have come: 2 minutes. The
    /// summarizer hands a short command's rollup, timed at its first start, and a long-running start over up to a
    /// minute and a half after that time, at the next sample. Then a closed incident whose events.csv does not hold
    /// the window's events its report lists is written once more, a write that must be made (<see cref="Process"/>).
    /// </summary>
    public TimeSpan LateEventMargin { get; set; } = TimeSpan.FromMinutes(2);
    /// <summary>
    /// How long after a write of a closed incident's folder failed (another program held one of its files) its folder
    /// is written again: at the first sample a minute or more after it, and then a minute after each try that fails,
    /// while PoolWatch holds the incident (<see cref="Process"/>, <see cref="Retry"/>: the whole folder, whatever the
    /// write budget, or, when all that could not be written was its timeline.csv or its processes.csv, the chart
    /// and those two alone; once the history no longer reaches back to the start of its window, its chart and its
    /// two other CSVs only as far as a try can still make them, <see cref="IncidentWriter.ChartFilesLeft"/>); the
    /// end of a run tries it too, the whole folder (<see cref="Finish"/>). (Before, such a write was tried again
    /// only at the incident's next change, or with rows of its events.csv that still waited, and a closed incident
    /// may never change: its report.md could then stay behind its events.csv to the end of the run and after.)
    /// </summary>
    public TimeSpan FailedWriteRetry { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The report folders' write budget (<see cref="WriteBudget"/>), shared with the spawn-storm folders: an open incident's
    /// refresh, a closed one's update and the rows of events.csv wait while it has no room; an incident's opening, re-opening,
    /// closing and a trace attached are always written.
    /// </summary>
    public WriteBudget Budget { get; set; } = new();
    /// <summary>The end of a run: everything left for later is written.</summary>
    private bool _finishing;
    /// <summary>The closed incidents that leave memory at this sample are being written for the last time (<see cref="Process"/>).</summary>
    private bool _leaving;
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

    /// <summary>What the incidents in memory hold, for PoolWatch's own cost report: the events are both ends of each one's window (<see cref="KeptEvents"/>). Under the history lock.</summary>
    public (int incidents, int owners, int ownerMembers, int events) Held()
    {
        var all = _closed.Concat(_open.Values).ToList();
        return (all.Count, all.Sum(i => i.Owners?.Count ?? 0), all.Sum(i => i.Owners?.Sum(o => o.Members.Count) ?? 0),
            all.Sum(i => i.Events.Count + (_kept.TryGetValue(i, out var k) ? k.Tail.Count : 0)));
    }

    /// <summary>
    /// A test hook, never called by PoolWatch itself: lets go of every incident's latest part (<see cref="KeptEvents.Tail"/>),
    /// its list's room included, so what is held then is what PoolWatch would hold keeping the first part alone and a test
    /// can measure what the latest parts hold (<c>BothEndsTests</c>).
    /// </summary>
    internal void ForgetLatestParts()
    {
        foreach (var inc in _closed.Concat(_open.Values))
            if (_kept.TryGetValue(inc, out var k)) { k.Tail.Clear(); k.Tail.TrimExcess(); }
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
    /// incident keeps of its window (both ends, and how many it let go between them) misses none however long its writes
    /// wait for the write budget. A closed incident takes them at once too when one of them is timed at or before its
    /// window's end (a late one, as the summarizer's rollups and long-running starts are, or one of the minute after the
    /// last rise of an incident closed while it still rose): its window took an event in after its last write, and its
    /// folder is written again (<see cref="KeptEvents.Took"/>). The events after its window's end it takes when it is
    /// written, and at the latest before the buffer lets them go (<see cref="DropEvents"/>).
    /// </summary>
    public void AddEvents(IEnumerable<LifecycleEvent> events, Moment now)
    {
        int before = _events.Count;
        long earliest = long.MaxValue;
        foreach (var e in events)
        {
            long ms = now.MsOf(e.TimeUtc);
            if (ms < earliest) earliest = ms;
            _events.Add(e with { Ms = ms });
        }
        _eventsAdded += _events.Count - before;
        foreach (var inc in _open.Values) KeepEvents(inc, now.Ms);
        foreach (var inc in _closed)
            if (earliest <= TailEndMs(inc)) KeepEvents(inc, now.Ms);
        if (_events.Count > MaxEvents) DropEvents(_events.Count - MaxEvents, now.Ms);
    }

    /// <summary>Events put into the shared buffer in all, and let go from its front in all: the buffer's event i is number <c>_eventsDropped + i</c>.</summary>
    private long _eventsAdded, _eventsDropped;
    /// <summary>The latest of the events the buffer let go (on the forward clock, and its time of day); null before it let any go.</summary>
    private (long Ms, DateTime Utc)? _droppedTo;

    /// <summary>
    /// The buffer lets go of its <paramref name="n"/> oldest events. A closed incident that has not looked at one of them
    /// takes what it has not looked at first (the open ones did just before), so every incident the manager holds has
    /// looked at every event before it goes, and keeps or counts each by its own time against its own window. (Before,
    /// a closed incident looked only when it was written: events that went meanwhile were known to it only as a number,
    /// and all of them were counted as left out of its window when the first of them was in it, the events after its
    /// window's end among them: a trace's incident whose window ended a little after its last write later said "300
    /// events left out" of which 10 were its window's.)
    /// </summary>
    private void DropEvents(int n, long nowMs)
    {
        foreach (var inc in _closed)
            if (_kept.TryGetValue(inc, out var k) && k.Seq < _eventsDropped + n) KeepEvents(inc, nowMs);
        for (int i = 0; i < n; i++)
            if (_droppedTo is not { } d || _events[i].Ms > d.Ms) _droppedTo = (_events[i].Ms, _events[i].TimeUtc);
        _events.RemoveRange(0, n);
        _eventsDropped += n;
    }

    /// <summary>
    /// What an incident has kept of the shared buffer's events: both ends of its window. The first part
    /// (<see cref="Incident.Events"/>): the earliest by time from <see cref="FromMs"/> on of those numbered below
    /// <see cref="Seq"/>, up to <see cref="IncidentWriter.MaxEventRows"/>, in time order; from <see cref="CutMs"/> on (the
    /// earliest event it left out of it) none. The latest part (<see cref="Tail"/>): the events after the first part, the
    /// latest <see cref="IncidentWriter.MaxEventRows"/> up to the window's end (<see cref="TailEndMs"/>), the earlier ones
    /// let go and counted (<see cref="LeftOut"/>), so its events.csv can say how many it leaves out between the two and when.
    /// By time on the forward clock the three never interleave: every event of the first part is at or before the first of
    /// those left out, and every one left out at or before the first of the latest part (a late event goes where it
    /// belongs, <see cref="TakeLatest"/>).
    /// </summary>
    private sealed class KeptEvents
    {
        /// <summary>
        /// Where its first part starts, on the forward clock and as a time of day: its window's start
        /// (<see cref="EventWindowStartMs"/>) as it was measured when the first part was last taken, at the incident's
        /// opening and then each time the first part followed the start (<see cref="Follow"/>): the window's start
        /// moves when the onset is measured again, and the first part follows it, so the incident's events.csv shows the
        /// window its report gives. An open incident's can be behind the window's start by up to
        /// <see cref="FollowEvery"/>; events.csv and report.md say where a file's rows are from when that is not where
        /// the window starts now (<see cref="IncidentWriter"/>).
        /// </summary>
        public long FromMs;
        public DateTime FromUtc;
        /// <summary>When its first part was last taken from its window's start, on the forward clock (<see cref="FollowEvery"/>).</summary>
        public long FollowedMs;
        /// <summary>It came with events (made with them, not from the buffer): its first part does not follow its window's start.</summary>
        public bool Came;
        /// <summary>
        /// When PoolWatch no longer held every event from <see cref="FromMs"/> on as the first part was taken (the shared
        /// buffer keeps <see cref="Settings.IncidentEventKeepMinutes"/> and at most <see cref="MaxEvents"/>: a slow growth
        /// that trips the 60-minute rule on a very busy PC opens with its window starting further back than that, and on
        /// such a PC a start measured again can be further back than the buffer reaches too), the time of day of the latest
        /// event it had let go by then, on the forward clock and its time of day; null when it held every event from
        /// there. The first part is then the earliest events PoolWatch still held: those let go before are not in it (how
        /// many is not known: the buffer does not count them by time), and events.csv and report.md say so
        /// (<see cref="IncidentEventSet.LostToUtc"/>).
        /// </summary>
        public (long Ms, DateTime Utc)? LostTo;
        /// <summary>
        /// The latest event timed before <see cref="FromMs"/> that PoolWatch knew of as the first part was taken (one the
        /// buffer had let go, or one in the buffer the first part passed over), on the forward clock and its time of day;
        /// <see cref="long.MinValue"/>: none. When the window's start is then measured earlier and the buffer no longer
        /// holds the events from there, this is up to when PoolWatch has let go of the window's events (<see cref="Follow"/>).
        /// </summary>
        public long BeforeMs = long.MinValue;
        public DateTime BeforeUtc;
        /// <summary>
        /// Events the incident had taken in that a follow took out of its count again (<see cref="Follow"/>: those from
        /// before the new start, and those the buffer no longer held to take again), so that every event it took in is
        /// counted once on one count across follows (<see cref="IncidentEventSet.TakenIn"/>); less than 0 when a start
        /// measured earlier took in events it had passed over.
        /// </summary>
        public long Carried;
        /// <summary>The buffer's events numbered below this were looked at.</summary>
        public long Seq;
        /// <summary>
        /// The window's end (<see cref="TailEndMs"/>) at the incident's last look at its events, and whether its latest
        /// part and its counts were taken again for that end (<see cref="Refit"/>: once for each end, as what the buffer
        /// could not give then it cannot give later).
        /// </summary>
        public long FitEndMs = long.MinValue;
        public bool Refitted;
        public DateTime? CutUtc;
        public long? CutMs;
        /// <summary>
        /// Its events from the cut on that it looked at, in time order (a late one in its place): of those up to the
        /// window's end as it would be if the incident closed now (<see cref="TailEndMs"/>) the latest
        /// <see cref="IncidentWriter.MaxEventRows"/> (and <see cref="LatestSlack"/> more while it is open), the earlier ones
        /// let go into <see cref="LeftOut"/>; and those after it (its quiet minutes, or the time since it closed), at most
        /// <see cref="AfterEndMax"/>, the earliest of more into <see cref="SetAside"/>, only while they can still become its
        /// window's (it is open, closed and can re-open, or made for a trace that has not finished:
        /// <see cref="CanTakeLater"/>). Shared with the 90-minute buffer while the buffer holds them, so
        /// on a busy PC most of it is references only; let go whole once the incident can no longer re-open and its file
        /// holds both ends (<see cref="Released"/>), before the buffer lets them go.
        /// </summary>
        public readonly List<LifecycleEvent> Tail = [];
        /// <summary>Its events from the cut on that it let go: how many, and the first and last of their times.</summary>
        public EventGap LeftOut;
        /// <summary>Events after the window's end it let go past <see cref="AfterEndMax"/>: they are left out if its window comes to take them in.</summary>
        public EventGap SetAside;
        /// <summary>The latest event it looked at, on the forward clock and its time of day.</summary>
        public long SeenMs = long.MinValue;
        public DateTime SeenUtc;
        /// <summary>
        /// It took in an event of its window (one timed at or before its window's end: kept, or counted as left out) since
        /// its folder was last written. A closed incident's folder is written only on a change, and this is one: its
        /// report and its events.csv no longer say what it holds (<see cref="Process"/>, <see cref="Finish"/>). Cleared
        /// by a write of its folder.
        /// </summary>
        public bool Took;
        /// <summary>
        /// A closed incident took in an event of its window since the last write of its folder that had to be made (its
        /// close, a trace attached): once every late event of its window can have come
        /// (<see cref="LateEventMargin"/> after the window's end), one more such write gives its events.csv what its
        /// report lists, when the file does not hold it (<see cref="Process"/>).
        /// </summary>
        public bool Late;
        /// <summary>When that write was last due, on the forward clock (null: never): the next is no sooner than <see cref="LateEventMargin"/> after it.</summary>
        public long? LateWrittenMs;
        /// <summary>The last write of its events.csv's waiting rows failed and the log said so (<see cref="IncidentManager.RowsFailed"/>): not said again until the file has them (<see cref="RowsWritten"/>).</summary>
        public bool RowsFailed;
        /// <summary>
        /// Its latest part was let go (<see cref="ReleaseLatest"/>): it can no longer re-open and its events.csv held both ends
        /// as written, so what that file holds of them is final, those events are counted in <see cref="LeftOut"/>, and so is
        /// any event of its window that comes after.
        /// </summary>
        public bool Released;
        /// <summary>The write (<see cref="Incident.LastWriteMs"/>) at which letting go of its latest part was tried and its file did not hold both ends; null: not tried.</summary>
        public long? ReleaseTriedAt;
    }

    /// <summary>
    /// The most events after an incident's window end it holds for the window to take in (the latest part,
    /// <see cref="KeptEvents.Tail"/>). The end moves later while the incident is open or can still re-open: to about now when
    /// it grows again or re-opens (its latest <see cref="IncidentWriter.MaxEventRows"/> and its <see cref="LatestSlack"/> are
    /// then the newest events), or to a later end of its growth as more samples are measured. 4,000 is about 25 minutes of
    /// the owner's 9,400 events an hour, so an end that lands up to about 3,000 events (19 minutes) before now still finds
    /// its whole latest part and slack. A matter of how far back in time the end may land, not of how many events each end
    /// keeps, so it stayed 4,000 when the ends went from 1,000 to 500; and references only, as the 90-minute buffer holds
    /// those events too while the incident can still take them in (an hour after it closes at most).
    /// </summary>
    internal const int AfterEndMax = 4_000;

    /// <summary>
    /// While an incident is open its latest part holds this many events more up to its window's end, so that when the end
    /// of its growth is measured a little earlier (by up to this many events: at the owner's 9,400 an hour about 3 minutes)
    /// its latest <see cref="IncidentWriter.MaxEventRows"/> are still there; once it closes, and its end is as measured then,
    /// it holds <see cref="IncidentWriter.MaxEventRows"/>. How far the measured end moves is a matter of samples (a few
    /// minutes), not of how many events each end keeps, so it stayed 500 when the ends went from 1,000 to 500; while open,
    /// those events are the newest, which the 90-minute buffer holds too.
    /// </summary>
    internal const int LatestSlack = 500;

    /// <summary>An open incident's latest part lets go of its earliest events once it holds this many more than it keeps, all at once (a move of its list each time).</summary>
    internal const int TrimBatch = 256;

    /// <summary>A test hook, never called by PoolWatch itself: how many events an incident's latest part holds (<see cref="KeptEvents.Tail"/>), for a test of what <see cref="Held"/> counts.</summary>
    internal int LatestHeld(Incident inc) => _kept.TryGetValue(inc, out var k) ? k.Tail.Count : 0;

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Incident, KeptEvents> _kept = new();

    /// <summary>
    /// Where the incident's window would end if it closed now, on the forward clock: a minute after the end of its growth, or
    /// after its last rise (<see cref="EventWindowEndMs"/> of a closed incident). An incident made for a trace of its own
    /// (<see cref="IncidentKind.Capture"/>) has its window set by its trace: it ends where the incident closed, the moment
    /// it was made and then, once its trace finished, exactly the trace's own end (<see cref="ApplyCapture"/>), with no
    /// minute added. Its latest events are counted up to here.
    /// </summary>
    internal static long TailEndMs(Incident inc) =>
        inc.Kind == IncidentKind.Capture && inc.ClosedMs is { } closed ? closed : (inc.EndMs ?? inc.LastRiseMs) + 60_000;

    /// <summary>
    /// Whether events after the incident's window end can still become its window's: it is open, or a growth incident closed
    /// less than <see cref="ReopenMinutes"/> ago (it can re-open), or an incident made for a trace that has not finished
    /// yet: its window ends where it was made until <see cref="ApplyCapture"/> moves the end to the trace's, and the
    /// events of the trace's minutes must still be there then, whenever it looked at them meanwhile.
    /// </summary>
    private static bool CanTakeLater(Incident inc, long nowMs) =>
        inc.IsOpen || (inc.Kind is IncidentKind.Growth or IncidentKind.NonPagedTotal && nowMs - inc.ClosedMs!.Value < Ms(TimeSpan.FromMinutes(ReopenMinutes))) ||
        (inc.Kind == IncidentKind.Capture && inc.Capture is null);

    /// <summary>
    /// The incident takes the buffer's events it has not looked at yet: the first time every one from its window's start, then
    /// each new one (a late one, read onto the forward clock earlier, in its place). Its first part takes them until it has
    /// <see cref="IncidentWriter.MaxEventRows"/>; from then on its latest part does (<see cref="KeptEvents.Tail"/>), but for
    /// a late event earlier than the first part's last (the summarizer's rollups come up to a minute and a half late, at their first
    /// start), which takes that one's place in the first part, the last one moving on: the first part stays the window's
    /// earliest by time. The first part starts at the window's start (<see cref="KeptEvents.FromMs"/>) and follows it:
    /// the onset is measured again as the growth goes on (later as a long, steady growth's rise grows, sometimes
    /// earlier), and the first part is taken again from the start as measured then (<see cref="Follow"/>), so the
    /// incident's events.csv shows the window its report gives. When the buffer had already let go of events from the
    /// start as the first part was taken, the first part is the earliest PoolWatch still held, and the incident notes
    /// up to when it had let events go (<see cref="KeptEvents.LostTo"/>). An incident the manager holds,
    /// open or closed, has looked at every event before the buffer lets it go (<see cref="AddEvents"/>,
    /// <see cref="DropEvents"/>), so each is kept or counted by its own time, and every incident PoolWatch writes is one
    /// the manager holds (none leaves its memory while a trace that may be attached to it records or is read:
    /// <see cref="Process"/>). An incident the manager does not hold (a test's own) can find that the buffer let events
    /// go before it looked at them: its first part then ends where it was, and those events are counted as left out
    /// (how many went, from the latest event it looked at to the latest the buffer let go: their own times are gone
    /// with them), with the latest part's events before them, when they can be its window's.
    /// </summary>
    /// <param name="ends">Both ends of an open incident's window are wanted now (an export): its first part follows its
    /// window's start now, as at a close.</param>
    private KeptEvents KeepEvents(Incident inc, long nowMs, bool ends = false)
    {
        HashSet<LifecycleEvent>? had = null;
        if (!_kept.TryGetValue(inc, out var k))
        {
            k = new KeptEvents { FromMs = EventWindowStartMs(inc), FromUtc = EventWindowStartUtc(inc), FollowedMs = nowMs, Seq = _eventsDropped };
            // The buffer no longer holds every event from there: the first part is the earliest it still holds.
            if (_droppedTo is { } gone)
            {
                if (gone.Ms >= k.FromMs) k.LostTo = gone;
                else (k.BeforeMs, k.BeforeUtc) = gone;
            }
            _kept.Add(inc, k);
            // Events it came with (none, but for one made with them) stay, in time order, each once.
            inc.Events = inc.Events.OrderBy(e => e.Ms).ToList();
            if (inc.Events.Count > 0)
            {
                had = new HashSet<LifecycleEvent>(inc.Events, ReferenceEqualityComparer.Instance);
                (k.SeenMs, k.SeenUtc) = (inc.Events[^1].Ms, inc.Events[^1].TimeUtc);
                k.Came = true;
            }
        }
        long endMs = TailEndMs(inc);
        if (endMs != k.FitEndMs) (k.FitEndMs, k.Refitted) = (endMs, false); // the end was measured somewhere else since its last look
        bool later = CanTakeLater(inc, nowMs) && !k.Released;
        bool again = Follow(inc, k, nowMs, ends);
        // Both ends are written from what it holds now (a closed incident's file, an export): its latest part and its
        // counts are made to fit the window's end as it is measured now (taken again from the start, they do already).
        if (!again && (!inc.IsOpen || ends)) Refit(inc, k, endMs, later);
        long start = k.Seq - _eventsDropped;
        if (start < 0)
        {
            // Let go of before this incident looked at them: its first part ends where it was (from the last it kept on, it
            // keeps none), and they are left out.
            if (k.CutMs is null)
            {
                var last = inc.Events.Count > 0 ? inc.Events[^1] : null;
                (k.CutUtc, k.CutMs) = last is null ? (k.FromUtc, k.FromMs) : (last.TimeUtc, last.Ms + 1);
            }
            if (_droppedTo is { } to && to.Ms >= k.FromMs)
            {
                // From after the latest it looked at, to the latest the buffer let go.
                var (fromMs, fromUtc) = k.SeenMs >= k.FromMs ? (k.SeenMs, k.SeenUtc) : (k.FromMs, k.FromUtc);
                var lost = new EventGap((int)Math.Min(int.MaxValue, -start), fromUtc, to.Utc, fromMs, to.Ms);
                // In its window: left out, and the latest part's events before them with them (the latest part follows what
                // it left out); after its end, set aside while the window can still take them in, so too.
                if (fromMs <= endMs) { LetGoThrough(k, lost.ToMs); k.LeftOut = k.LeftOut.With(lost); }
                else if (later) { SetAsideThrough(k, endMs, lost.ToMs); k.SetAside = k.SetAside.With(lost); }
            }
            start = 0;
        }
        var kept = inc.Events;
        bool tailed = false;
        int taken = 0;
        for (int i = (int)start; i < _events.Count; i++)
        {
            // Taken again from the start (the whole buffer, up to 90 minutes of events): its latest part held to its bounds
            // as it goes, as each intake would have, not grown to all of them first.
            if (again && tailed && ++taken % RetakeTrimEvery == 0) TrimTail(k, endMs, later, inc.IsOpen);
            var e = _events[i];
            if (e.Ms < k.FromMs)
            {
                if (e.Ms > k.BeforeMs) (k.BeforeMs, k.BeforeUtc) = (e.Ms, e.TimeUtc);
                continue;
            }
            if (had is not null && had.Contains(e)) continue;
            if (e.Ms >= k.SeenMs) (k.SeenMs, k.SeenUtc) = (e.Ms, e.TimeUtc);
            // One of its window's, as the window ends now: kept or counted below, and its folder does not say so yet.
            if (e.Ms <= endMs) (k.Took, k.Late) = (true, k.Late || !inc.IsOpen);
            if (k.Released)
            {
                // Its file's latest part is final: an event of its window that comes now is counted as left out.
                if (e.Ms <= endMs) k.LeftOut = k.LeftOut.With(EventGap.One(e));
                continue;
            }
            if (k.CutMs is null && kept.Count < IncidentWriter.MaxEventRows) { InsertByTime(kept, e); continue; }
            // The first part is the window's earliest by time: a late event earlier than its last takes that one's place, and
            // the last one moves on to the latest part.
            if (kept.Count > 0 && e.Ms < kept[^1].Ms)
            {
                InsertByTime(kept, e);
                if (kept.Count <= IncidentWriter.MaxEventRows) continue; // a first part that ended early (events let go before it looked at them)
                e = kept[^1];
                kept.RemoveAt(kept.Count - 1);
            }
            if (k.CutMs is not { } cut || e.Ms < cut) (k.CutUtc, k.CutMs) = (e.TimeUtc, e.Ms);
            // The latest part: those after the window's end only while the window can still take them in.
            if (e.Ms <= endMs || later) { TakeLatest(k, e, endMs); tailed = true; }
        }
        k.Seq = _eventsAdded;
        if (tailed || k.Tail.Count > 0) TrimTail(k, endMs, later, inc.IsOpen);
        if (again)
        {
            // What the follow took out of its count (or, measured earlier, took in): every event counts once across follows.
            k.Carried -= TakenInNow(inc, k);
            if (k.Tail.Capacity > 2 * k.Tail.Count + TrimBatch) k.Tail.TrimExcess();
        }
        return k;
    }

    /// <summary>While an incident takes every event again from its window's start (<see cref="Follow"/>), its latest part is held to its bounds every this many events.</summary>
    private const int RetakeTrimEvery = 512;

    /// <summary>How many events the incident holds or counts now: its first part, those it let go, its latest part and those after its window's end it set aside.</summary>
    private static long TakenInNow(Incident inc, KeptEvents k) => inc.Events.Count + (long)k.LeftOut.Count + k.Tail.Count + k.SetAside.Count;

    /// <summary>
    /// An open incident's first part follows its window's start at most this often (<see cref="Follow"/>; a closed one's and
    /// an export's at once): 10 minutes, within the 18 the buffer's 90 minutes leave past the 72 the start can be back when it
    /// last moves.
    /// </summary>
    internal static readonly TimeSpan FollowEvery = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The incident's first part follows its window's start when that was measured somewhere else since the first part
    /// was taken: on a long, steady growth the onset is measured later as the rise grows (about a twentieth of the time
    /// grown: after an hour, past the whole of a first part of 500 events at the owner's 9,400 an hour, so that a first
    /// part fixed where the incident opened was, on average, 416 of 500 rows from before the growth), and sometimes
    /// earlier. A person reads the report's window and expects events.csv to show that window, so the first part is the
    /// window's first events as its start is measured now. A closed incident's (its close, a trace attached), and an
    /// open one's when both ends are wanted (<paramref name="ends"/>: an export), follow at once; an open one's
    /// otherwise at most every <see cref="FollowEvery"/>, as its onset moves every few minutes on a long growth and each
    /// follow takes the whole buffer again. Not one that let go of its latest part (its file is final), or one made with
    /// its events. Returns whether every event is to be taken again from the buffer (the caller does, in its loop).
    /// <para>While the shared buffer holds every event from the new start (it let none go at or after it:
    /// <see cref="_droppedTo"/>), the first part is taken again from the buffer and is exactly the window's first
    /// events. The onset is measured only within the raw history (<see cref="Settings.RawHistoryMinutes"/>, 70; once the
    /// history no longer reaches it, it stays as last measured), so with the default settings the start is at most 72
    /// minutes back when it last moves, and at most <see cref="FollowEvery"/> more when the first part follows it,
    /// which the buffer's 90 minutes (<see cref="Settings.IncidentEventKeepMinutes"/>) hold, unless its 20,000 events
    /// are fewer than those 82 minutes of the PC's (over about 14,600 events an hour: more than half as many again as
    /// the owner's), or the settings make the raw history the longer.</para>
    /// <para>When the buffer no longer holds every event from the new start, the first part is the first events
    /// PoolWatch still holds from it, and <see cref="KeptEvents.LostTo"/> says up to when it had let events go.
    /// Those are the incident's own when its first part holds an event from the new start on that the buffer has let
    /// go (it is then earlier than anything the buffer could give): the first part keeps its events from the new start
    /// on, and lets go of those before it (they are not the window's). They are exactly the window's first when the
    /// start moved later (past what it had let go before), and when it moved earlier with no event known between the
    /// two starts; else PoolWatch had let go of the window's events up to the latest event before the first part it
    /// knew of (<see cref="KeptEvents.BeforeUtc"/>). Otherwise they are the buffer's: every event is taken again from
    /// it, the first of them after events the buffer had let go.</para>
    /// </summary>
    private bool Follow(Incident inc, KeptEvents k, long nowMs, bool ends)
    {
        long from = EventWindowStartMs(inc);
        if (from == k.FromMs || k.Released || k.Came) return false;
        if (inc.IsOpen && !ends && nowMs - k.FollowedMs < (long)FollowEvery.TotalMilliseconds) return false;
        long before = TakenInNow(inc, k), was = k.FromMs;
        (k.FromMs, k.FromUtc, k.FollowedMs) = (from, EventWindowStartUtc(inc), nowMs);
        var events = inc.Events;
        int first = 0;
        while (first < events.Count && events[first].Ms < from) first++;
        if (_droppedTo is { } gone && gone.Ms >= from && first < events.Count && events[first].Ms <= gone.Ms)
        {
            // Its own first part is the earliest PoolWatch holds from the new start: it stays, less the events before it.
            if (first > 0)
            {
                (k.BeforeMs, k.BeforeUtc) = (events[first - 1].Ms, events[first - 1].TimeUtc);
                events.RemoveRange(0, first);
            }
            // What PoolWatch had let go of from the new start on, before the first part's first event: what it had as the
            // first part was taken, when that is from the new start on; else, measured earlier, the latest event before
            // the first part it knew of, when that is.
            if (k.LostTo is { } lost && lost.Ms < from) k.LostTo = null;
            if (k.LostTo is null && from < was && k.BeforeMs >= from) k.LostTo = (k.BeforeMs, k.BeforeUtc);
            k.Carried += before - TakenInNow(inc, k);
            return false;
        }
        // Every event from the new start that the buffer holds is taken again: all of them, while it let none go from
        // there. The events not looked at yet are taken with them, and count as taken in either way.
        long pending = 0;
        for (int i = (int)Math.Max(0, k.Seq - _eventsDropped); i < _events.Count; i++) if (_events[i].Ms >= was) pending++;
        k.Carried += before + pending;
        k.Seq = _eventsDropped;
        (k.CutUtc, k.CutMs) = (null, null);
        (k.LeftOut, k.SetAside) = (default, default);
        (k.SeenMs, k.SeenUtc) = (long.MinValue, default);
        (k.BeforeMs, k.BeforeUtc) = (long.MinValue, default);
        k.LostTo = null;
        if (_droppedTo is { } went)
        {
            if (went.Ms >= from) k.LostTo = went;
            else (k.BeforeMs, k.BeforeUtc) = went;
        }
        k.Tail.Clear();
        inc.Events = new List<LifecycleEvent>(IncidentWriter.MaxEventRows);
        return true;
    }

    /// <summary>
    /// The incident's latest part and its counts are taken again from the shared buffer when they no longer fit its
    /// window's end. An incident counts each event after its first part by its own time against the window's end as it
    /// is then: the latest <see cref="IncidentWriter.MaxEventRows"/> up to the end stay (<see cref="LatestSlack"/> more
    /// while it is open), the earlier ones are let go and counted (<see cref="KeptEvents.LeftOut"/>), and of those after
    /// the end the earliest past <see cref="AfterEndMax"/> are set aside (<see cref="KeptEvents.SetAside"/>). The end is
    /// measured again as the growth goes on, and can come out earlier than events already let go (the ranking looks
    /// further back as an incident goes on: a level it then finds lower makes an earlier plateau the growth's end), or,
    /// once more than <see cref="AfterEndMax"/> events came after it, later, among those set aside. Its events.csv
    /// would then count events from after the window's end as left out, or hold fewer than its latest
    /// <see cref="IncidentWriter.MaxEventRows"/> up to the end (none, when the end moved far), while PoolWatch still
    /// held the window's events. So when both ends are wanted (a closed incident, an export) and what the incident
    /// holds does not fit the end (<paramref name="endMs"/>: the end has reached events it set aside, or it holds fewer
    /// than a latest part's events up to the end although the buffer still holds events it let go, as it does when the
    /// end is now before events it counted as left out), the events after the first part are taken again, as
    /// <see cref="Follow"/> takes the first part again for a start measured somewhere else: once for each end
    /// (<see cref="KeptEvents.Refitted"/>).
    /// <para>The buffer holds every event timed after the latest one it let go (<see cref="_droppedTo"/>): those are
    /// taken again one by one, each kept, let go or set aside against the end as it is now. What the incident let go
    /// at or before that time stays counted as it is (<see cref="StaysCounted"/>): how many is counted (all it let
    /// go, less those of them the buffer still gives), up to the time of the latest event the buffer let go (an
    /// incident the manager holds had looked at that event before it went). That goes for the events it left out and
    /// for those it set aside alike: of those set aside, the ones the buffer has let go stay counted, as left out now
    /// that the end has reached them, with the latest part's events before them (so the latest part has no hole), and
    /// the ones it still gives are taken again with the rest. (Before, nothing was taken again once the buffer had
    /// let go of any event set aside: with the end measured after every event the buffer had let go, the file got a
    /// latest part of the events after those set aside alone, or none, and a count that held events from after the
    /// window's end, said as events PoolWatch no longer held, while it held every one of them.) When the events it
    /// let go at or before that time run past the window's end now (the buffer no longer holds the events from after
    /// the end: over 20,000 events since, or a buffer kept shorter than the history), their count stays as it was
    /// made, and events.csv and report.md say that it runs past the window's end, and where that is
    /// (<see cref="IncidentEventSet.EndUtc"/>).</para>
    /// </summary>
    private void Refit(Incident inc, KeptEvents k, long endMs, bool later)
    {
        // Not one whose file is final, one made with its events, one whose first part is not full (nothing is let go
        // then), or one that had not looked at events the buffer let go (it counted those as it found them gone).
        if (k.Refitted || k.Released || k.Came || k.CutMs is not { } cut || k.Seq < _eventsDropped) return;
        long gone = _droppedTo?.Ms ?? long.MinValue;
        // The end has reached events it set aside (those would be counted as left out together, the ones after the end
        // too, and the latest part would be only the events after them); or it holds fewer than a latest part's events
        // up to the end, and the buffer still holds events it let go (an end measured earlier: before most of the
        // latest part it kept, or before events it counted as left out, which are then after the window's end).
        bool among = k.SetAside.Count > 0 && k.SetAside.FromMs <= endMs;
        bool few = k.LeftOut.Count > 0 && k.LeftOut.ToMs > gone && UpperBound(k.Tail, endMs) < IncidentWriter.MaxEventRows;
        if (!among && !few) return;
        k.Refitted = true; // tried for this end: what the buffer cannot give now it cannot give later
        int looked = (int)(k.Seq - _eventsDropped);
        // What it let go at or before the latest event the buffer let go stays counted: of those it left out, and of
        // those it set aside. (Every event it left out is at or before the first one it set aside: where one set
        // aside stays counted, every one left out does, and this look takes again because the end has reached those
        // set aside.)
        var stays = StaysCounted(k, k.LeftOut, gone, looked);
        var aside = StaysCounted(k, k.SetAside, gone, looked);
        long before = TakenInNow(inc, k);
        if (aside.Count > 0)
        {
            // Those set aside that stay counted are left out of the window as it ends now (or run past its end, where
            // the buffer let go of events from after it), and the latest part's events before them with them: the
            // latest part follows what is left out.
            int upTo = UpperBound(k.Tail, aside.ToMs);
            stays = stays.With(EventGap.Of(k.Tail, 0, upTo)).With(aside);
            k.Tail.RemoveRange(0, upTo);
        }
        // The latest part's events at or before that time follow those that stay counted and come before every event
        // taken again: they stay. The rest of it, and everything let go after that time, is taken again.
        int low = UpperBound(k.Tail, gone);
        k.Tail.RemoveRange(low, k.Tail.Count - low);
        (k.LeftOut, k.SetAside) = (stays, default);
        int taken = 0;
        for (int i = 0; i < looked; i++)
        {
            var e = _events[i];
            if (e.Ms <= gone || e.Ms < cut || Holds(inc.Events, e)) continue; // (those after the end that it can no longer take in go again as it is held to its bounds)
            TakeLatest(k, e, endMs);
            if (++taken % RetakeTrimEvery == 0) TrimTail(k, endMs, later, inc.IsOpen);
        }
        TrimTail(k, endMs, later, inc.IsOpen);
        // Every event it took in still counts once on its count across this (IncidentEventSet.TakenIn).
        k.Carried += before - TakenInNow(inc, k);
        if (k.Tail.Capacity > 2 * k.Tail.Count + TrimBatch) k.Tail.TrimExcess();
    }

    /// <summary>
    /// Of events an incident let go and counted (<paramref name="let"/>: those it left out, or those it set aside),
    /// the ones that stay counted as they are when the rest is taken again (<see cref="Refit"/>): those at or before
    /// the latest event the buffer let go (<paramref name="gone"/>). All of them when their last is; none when their
    /// first is after it. Else some are timed after it, and the buffer still gives those: every event the incident
    /// looked at (the buffer's first <paramref name="looked"/>) between that time and the last of them is one of
    /// them, but for one the latest part holds at the last one's very moment (two events of one millisecond, one let
    /// go and one kept). No other event is timed there: the first part's are all at or before the first event it let
    /// go, so at or before that time; every event it left out is at or before the first it set aside; and those it
    /// dropped as after its end are after every one it left out. The rest stay counted, up to that time.
    /// </summary>
    private EventGap StaysCounted(KeptEvents k, EventGap let, long gone, int looked)
    {
        if (let.Count == 0 || let.FromMs > gone) return default;
        if (let.ToMs <= gone) return let;
        int still = 0;
        for (int i = 0; i < looked; i++)
        {
            var e = _events[i];
            if (e.Ms > gone && e.Ms <= let.ToMs && !Holds(k.Tail, e)) still++;
        }
        return let.Count > still ? new EventGap(let.Count - still, let.FromUtc, _droppedTo!.Value.Utc, let.FromMs, gone) : default;
    }

    /// <summary>
    /// An event after the first part goes into the latest part in its place by time, but for one that falls among those
    /// left out (a late one), which is counted with them, and one after the window's end among those set aside, likewise:
    /// the latest part always follows what was left out, and its events after the window's end follow what was set aside.
    /// </summary>
    private static void TakeLatest(KeptEvents k, LifecycleEvent e, long endMs)
    {
        if (k.LeftOut.Count > 0 && e.Ms <= k.LeftOut.ToMs) k.LeftOut = k.LeftOut.With(EventGap.One(e));
        else if (k.SetAside.Count > 0 && e.Ms > endMs && e.Ms <= k.SetAside.ToMs) k.SetAside = k.SetAside.With(EventGap.One(e));
        else InsertByTime(k.Tail, e);
    }

    /// <summary>The latest part's events at or before <paramref name="ms"/> are let go and counted as left out (those left out after them come next).</summary>
    private static void LetGoThrough(KeptEvents k, long ms)
    {
        int n = UpperBound(k.Tail, ms);
        if (n == 0) return;
        k.LeftOut = k.LeftOut.With(EventGap.Of(k.Tail, 0, n));
        k.Tail.RemoveRange(0, n);
    }

    /// <summary>The latest part's events after the window's end (<paramref name="endMs"/>) and at or before <paramref name="ms"/> are set aside (those set aside after them come next).</summary>
    private static void SetAsideThrough(KeptEvents k, long endMs, long ms)
    {
        int from = UpperBound(k.Tail, endMs), to = UpperBound(k.Tail, ms);
        if (to <= from) return;
        k.SetAside = k.SetAside.With(EventGap.Of(k.Tail, from, to - from));
        k.Tail.RemoveRange(from, to - from);
    }

    /// <summary>Puts an event in its place in a list in time order, after those of the same moment (at the end, but for a late one).</summary>
    private static void InsertByTime(List<LifecycleEvent> list, LifecycleEvent e)
    {
        if (list.Count == 0 || list[^1].Ms <= e.Ms) { list.Add(e); return; }
        list.Insert(UpperBound(list, e.Ms), e);
    }

    /// <summary>How many events of the list (in time order) are at or before <paramref name="ms"/> (all of them, most often: looked at first).</summary>
    private static int UpperBound(List<LifecycleEvent> list, long ms)
    {
        if (list.Count == 0 || list[^1].Ms <= ms) return list.Count;
        int lo = 0, hi = list.Count;
        while (lo < hi) { int mid = (lo + hi) >>> 1; if (list[mid].Ms <= ms) lo = mid + 1; else hi = mid; }
        return lo;
    }

    /// <summary>
    /// The latest part held to its bounds: of its events up to <paramref name="endMs"/> the latest
    /// <see cref="IncidentWriter.MaxEventRows"/>, and <see cref="LatestSlack"/> more while the incident is
    /// <paramref name="open"/> (the earlier ones let go into <see cref="KeptEvents.LeftOut"/>, an open incident's
    /// <see cref="TrimBatch"/> at a time); of those after it, none when
    /// the window can no longer take them in (<paramref name="later"/> false), else the latest <see cref="AfterEndMax"/> (the
    /// earlier ones set aside). Set-aside events the window's end has reached are left out, and every event of the latest
    /// part before them with them, so the latest part has no hole: it is the events after them.
    /// </summary>
    private static void TrimTail(KeptEvents k, long endMs, bool later, bool open)
    {
        var tail = k.Tail;
        if (k.SetAside.Count > 0 && k.SetAside.FromMs <= endMs)
        {
            LetGoThrough(k, k.SetAside.ToMs);
            (k.LeftOut, k.SetAside) = (k.LeftOut.With(k.SetAside), default);
        }
        int upTo = UpperBound(tail, endMs);
        int after = tail.Count - upTo;
        if (!later)
        {
            if (after > 0) tail.RemoveRange(upTo, after);
            k.SetAside = default;
        }
        else if (after > AfterEndMax)
        {
            // The earliest go, and any before the last of those set aside with them, so those set aside stay before the rest.
            int n = Math.Max(after - AfterEndMax, k.SetAside.Count > 0 ? UpperBound(tail, k.SetAside.ToMs) - upTo : 0);
            k.SetAside = k.SetAside.With(EventGap.Of(tail, upTo, n));
            tail.RemoveRange(upTo, n);
        }
        // An open one lets go of them a batch at a time (each a move of the list), a closed one at once.
        int keep = IncidentWriter.MaxEventRows + (open ? LatestSlack : 0);
        if (upTo > keep + (open ? TrimBatch : 0))
        {
            int n = upTo - keep;
            k.LeftOut = k.LeftOut.With(EventGap.Of(tail, 0, n));
            tail.RemoveRange(0, n);
        }
    }

    /// <summary>
    /// The incident's latest part for a window ending at <paramref name="endMs"/>: its latest
    /// <see cref="IncidentWriter.MaxEventRows"/> events up to there, and what it left out before them (those it let go, and
    /// its earlier ones up to there past the latest <see cref="IncidentWriter.MaxEventRows"/>). When the end reaches events
    /// it set aside, the latest part is only of those after them (fewer, when there are not that many): it has no hole.
    /// </summary>
    private static (IReadOnlyList<LifecycleEvent> Tail, EventGap LeftOut) TailOf(KeptEvents k, long endMs)
    {
        var left = k.LeftOut;
        int from = 0;
        if (k.SetAside.Count > 0 && k.SetAside.FromMs <= endMs)
        {
            from = UpperBound(k.Tail, k.SetAside.ToMs);
            left = left.With(EventGap.Of(k.Tail, 0, from)).With(k.SetAside);
        }
        int upTo = Math.Max(from, UpperBound(k.Tail, endMs));
        int skip = Math.Max(from, upTo - IncidentWriter.MaxEventRows);
        if (skip > from) left = left.With(EventGap.Of(k.Tail, from, skip - from));
        return (new Slice(k.Tail, skip, upTo - skip), left);
    }

    /// <summary>
    /// Lets go of a closed incident's latest part once it can no longer re-open (an hour after it closed) and its events.csv
    /// holds both ends as written (its closing write made it so): those events are counted among those it left out, and what
    /// its file holds of them is final (<see cref="IncidentEventSet.LatestReleased"/>), so a closed incident held for its 6
    /// hours holds its first part only, as before it kept both ends. When its file does not hold them (its closing write
    /// failed, say) it keeps them for the write that will (the end of a run, or as it leaves memory), and is tried again
    /// after its next write.
    /// </summary>
    private void ReleaseLatest(Incident inc, KeptEvents k, long nowMs)
    {
        if (!_writer.HoldsBothEnds(inc, IncidentEvents(inc, nowMs, forReports: false)))
        {
            k.ReleaseTriedAt = inc.LastWriteMs;
            return;
        }
        k.LeftOut = k.LeftOut.With(EventGap.Of(k.Tail, 0, k.Tail.Count));
        k.Tail.Clear();
        k.Tail.TrimExcess();
        k.SetAside = default;
        k.Released = true;
    }

    /// <summary>
    /// Part of a list, read in place (not copied): the latest part an event set carries, read by the write it is made for,
    /// under the history lock, before the list changes.
    /// </summary>
    private sealed class Slice(List<LifecycleEvent> list, int start, int count) : IReadOnlyList<LifecycleEvent>
    {
        public int Count => count;
        public LifecycleEvent this[int index] => (uint)index < (uint)count ? list[start + index] : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<LifecycleEvent> GetEnumerator() { for (int i = 0; i < count; i++) yield return list[start + i]; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
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
            foreach (var inc in _open.Values) KeepEvents(inc, nowMs); // what they have not taken yet, before it goes
            DropEvents(cut, nowMs); // the closed ones that have not looked at them take theirs there
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
        // it closed, while the write budget has room; the rows of events.csv a write left for later follow when it has,
        // with its report (below).
        foreach (var inc in _closed.OrderBy(i => i.LastWriteMs).ToList())
        {
            // Its window took events in after its closing write (late ones, or those of the minute after the last rise of
            // an incident closed while it still rose), and by now every late event of the window can have come: when
            // its events.csv does not hold the window's events its report lists, one more write that must be made
            // gives it them (a file with both ends is replaced whole, another gets every row left), with its report,
            // so the two agree from here on. Once: again only when still more of its window's events come, and then no
            // sooner than the margin after this one.
            if (_kept.TryGetValue(inc, out var late) && late.Late && nowMs >= Math.Max(TailEndMs(inc), late.LateWrittenMs ?? long.MinValue) + Ms(LateEventMargin))
            {
                (late.Late, late.LateWrittenMs) = (false, nowMs);
                if (_writer.LacksEvents(inc, IncidentEvents(inc, nowMs, forReports: false))) Write(h, inc, mustWrite: true, full: false, rows: IncidentWriter.MaxEventRows);
            }
            // A write of its folder failed (another program held one of its files) and its folder has not been written
            // whole since: it is written again a minute after that, and then a minute after each try that fails, the
            // whole folder, whatever the budget, or, when only its timeline.csv or its processes.csv could not be
            // written, the chart and those two alone (once the history no longer reaches back to the start of its
            // window, a try leaves those of its chart and its two other CSVs that it can no longer make, which ends
            // the tries: Retry). A closed incident may never change again, so nothing else would write it (before,
            // its report.md could stay behind its events.csv for the rest of the run).
            if (inc.WriteFailed && nowMs - inc.WriteFailedMs >= Ms(FailedWriteRetry)) Retry(h, inc);
            else if (inc.Dirty || (inc.CountPending && nowMs - inc.LastWriteMs >= Ms(ClosedRewriteInterval))) Write(h, inc, mustWrite: false);
            // Its window took events in since its last write: its report, which lists and counts them, and their rows
            // in events.csv, together and as the budget allows. Its chart and the two other CSVs stay as its last full
            // write made them. A file that holds both ends is replaced whole only by a write that must be made (the
            // one above, the end of a run, or as the incident leaves memory, below); until then the report counts that
            // file's rows as they are.
            else if (Took(inc)) Write(h, inc, mustWrite: false, full: false, rows: EventRowsPerWrite);
            // Rows a write left for later (more late events than one write adds, or a write of them that failed): with
            // the report again, whose line under the window's events counts the file's rows and says what it still
            // gets. (Appended alone, they left a closed incident's report saying "events.csv gets the rest when
            // PoolWatch's write budget has room" of a file that had every row, to the end of the run.) Only when a row
            // does wait: the events from after its window's end that its first part took are none of its file's.
            else if (reportsCurrent && _writer.EventsPending(inc) && nowMs - inc.LastWriteMs >= Ms(RewriteInterval) &&
                     _writer.RowsWait(inc, IncidentEvents(inc, nowMs, forReports: false)))
                Write(h, inc, mustWrite: false, full: false, rows: EventRowsPerWrite, waiting: true);
            if (_kept.TryGetValue(inc, out var k) && k.Tail.Count > 0)
            {
                // Events after its window's end it held in case it re-opened: let go once it no longer can.
                if (k.Tail[^1].Ms > TailEndMs(inc) && !CanTakeLater(inc, nowMs)) TrimTail(k, TailEndMs(inc), later: false, open: false);
                // Its latest part once it can no longer re-open, when its events.csv holds both ends as written (once a write).
                if (!k.Released && k.Tail.Count > 0 && nowMs - inc.ClosedMs!.Value >= Ms(TimeSpan.FromMinutes(ReopenMinutes)) && k.ReleaseTriedAt != inc.LastWriteMs)
                    ReleaseLatest(inc, k, nowMs);
            }
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
        // None leaves while a trace records or is read: the trace is attached to its incidents when it finishes
        // (ApplyCapture), and an incident that had left by then would be written once more without having looked at the
        // events the buffer let go meanwhile (it counted as many as went as left out, those after its window's end among
        // them). They leave at the first sample after the trace, which the runner bounds (its recording by its hard cap).
        if (capturing) old = 0;
        // Their folders stay on disk, each events.csv with both ends of its window (written whole now when it is not yet, so
        // a later run keeps them), or saying how many the write budget never had room for, and between which times. A file
        // replaced whole now (its closing write failed, or its window took events in since; or one with its first part
        // alone that got a late event's row after later rows, written in time order now) has its report written with
        // it, so the report counts the file's rows and its Gap row as they are; the chart and the two other CSVs stay.
        // A write that fails here is not tried again (the incident is let go), and its log line does not say it is.
        _leaving = true;
        for (int i = 0; i < old; i++)
            try
            {
                var ev = IncidentEvents(_closed[i], nowMs, forReports: false);
                if (_writer.BothEndsDue(_closed[i], ev) || _writer.OutOfOrder(_closed[i], ev)) Write(h, _closed[i], mustWrite: true, full: false, rows: IncidentWriter.MaxEventRows);
                else Budget.Spend(_writer.WriteLeaving(_closed[i], ev), nowMs, must: true);
            }
            catch (Exception ex) { _host.Log($"incident {_closed[i].Id}: events.csv write failed: {ex.Message}"); }
        _leaving = false;
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
    /// An incident for a trace of its own: "Capture now" for the given tags (the fastest-growing ones), or a lag trip's CPU
    /// trace (no tags: File). It is made closed, with its start and its window's end at the moment it is made:
    /// <paramref name="nowUtc"/> and the newest sample's forward clock (the pipeline gives the newest sample's time, so
    /// the two are one moment once the history holds a sample). Both stay there until the trace finishes:
    /// <see cref="ApplyCapture"/> then attaches the result and sets the start to the trace's own start and the end to
    /// its own end (<see cref="EventWindowEndMs"/>). <see cref="Measure"/> never moves either. Until then the incident
    /// holds the events that come after it was made, for the window the trace will give it (<see cref="CanTakeLater"/>).
    /// </summary>
    public Incident CreateCapture(History.History h, IReadOnlyList<PoolTag> tags, DateTime nowUtc, CaptureSource source = CaptureSource.Manual)
    {
        long nowMs = NewestMs(h);
        var inc = Create(tags.Count > 0 ? tags[0] : PoolTag.Parse("File"), IncidentKind.Capture, nowUtc, nowMs);
        (inc.OnsetUtc, inc.OnsetMs) = (nowUtc, nowMs);
        inc.TraceFor = source;
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

    /// <summary>
    /// Attaches a finished confirmation trace to its incident, re-ranks and rewrites it. An incident made for the trace
    /// (<see cref="CreateCapture"/>) starts exactly at the trace's own start from here on, on both clocks, and closes at
    /// its own end, where its window ends too (<see cref="EventWindowEndMs"/>): its events.csv and the events its
    /// report lists run to the trace's end, however long the trace was. One whose trace did not record (it could not
    /// start: its start and end are one) keeps the start and the end it was made with.
    /// </summary>
    public void ApplyCapture(History.History h, Incident inc, CaptureResult result)
    {
        inc.Capture = result;
        // The CPU picture next to the pool size at that moment.
        if (result.Cpu is { } cpu && h.Count > 0) cpu.PoolBytes = (long)(h.Totals[^1].CommittedPaged + h.Totals[^1].CommittedNonPaged);
        if (inc.Kind == IncidentKind.Capture && result.EndMs > result.StartMs)
        {
            // An incident made for the trace starts and closes with it, as the runner read both on both clocks: the trace
            // is its window, from that start to that end, exactly (no minutes before the start, none after the end).
            // Measure leaves both where they are (the owner's choice, 2026-11-11), and the write below takes its
            // first part again from the trace's start (Follow).
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

    /// <summary>
    /// Closes and writes every open incident (end of a headless run, app exit, Windows shutting down), each events.csv with
    /// both ends of its window, and writes what any closed one still has to (its events.csv's both ends among it).
    /// </summary>
    public void Finish(History.History h, DateTime nowUtc)
    {
        _finishing = true;
        long nowMs = NewestMs(h);
        // The closed incidents whose folder could not be written and has not been since are tried once more below:
        // which they are is settled before the open ones close.
        var failedBefore = _closed.Where(i => i.WriteFailed).ToHashSet();
        var closing = _open.Values.ToList();
        foreach (var inc in closing) Close(h, inc, nowUtc, nowMs);
        // An incident this call closed whose closing write failed was tried just now, and its failure logged: it is not
        // written again below, whatever of it still waits (nothing changed since that try: before, it was tried twice
        // in this one call, and logged twice).
        var triedNow = closing.Where(i => i.WriteFailed).ToHashSet();
        // An event of a closed incident's window that came since its last write makes one of the last three true (it took
        // the event as it came: a row not in its file yet, a latest part or a count its file does not hold, or a row its
        // file got after later rows, which this write puts in time order).
        foreach (var inc in _closed.Where(i => failedBefore.Contains(i) ||
                                               (!triedNow.Contains(i) && (i.Dirty || i.CountPending || _writer.EventsPending(i) || Unsettled(i, nowMs)))).ToList())
            Write(h, inc);
    }

    /// <summary>
    /// Tries a closed incident's folder again after a write of it failed (<see cref="Incident.WriteFailed"/>), as a
    /// write that must be made, whatever the write budget: until it is made the folder's files do not agree with
    /// each other. The try writes the whole folder (whatever part of it could not be written: its chart and the two
    /// other CSVs, every row its events.csv is to hold, its report), unless all that is still to write is its
    /// timeline.csv or its processes.csv (<see cref="Incident.ChartsAloneFailed"/>: the write that failed wrote the
    /// reports) and no change waits to have the folder written (the incident is not <see cref="Incident.Dirty"/>:
    /// no new line on its timeline, no growth found still held, no suspect's session found since its report was
    /// written): it then writes the chart and those two files alone, and events.csv and the two reports only when
    /// it made a file the report does not show yet (a chart the report has no line for, a timeline.csv or a
    /// processes.csv its draft bug report does not list). What else waits for a report meanwhile (alerts counted
    /// after the close, late events) is written by the writes <see cref="Process"/> makes for it, as without a
    /// failed write. A try that cannot write
    /// timeline.csv or processes.csv still writes whatever else it is to write
    /// (<see cref="IncidentWriter.ChartFailure"/>), and is followed by the next try. Once the history no longer
    /// reaches back to the start of the incident's window, a try makes one of the chart and the two other CSVs that
    /// was never written for the close only when it holds at least as many of the window's samples as that file
    /// holds already, and at least one (a file that stops before the close is replaced by one that reaches it; none
    /// comes out of fewer samples, or of samples from after the incident). A try that leaves such a file as it is
    /// writes whatever else it is to write, the log names the file, and the failure is over: no further try is made
    /// for that file (<see cref="IncidentWriter.ChartFilesLeft"/>). (Before, every try wrote report.json and
    /// report.md again, whole: they give PoolWatch's own cost as it is at each write, so a report is hardly ever
    /// the same as the one before. While another program held a closed incident's timeline.csv that came to a
    /// report a minute, about 5.4 MB an hour at a report of about 90 KB, more than PoolWatch's whole disk budget;
    /// and as a write that must be made spends the report folders' budget too, the open incidents' refreshes waited
    /// for as long as the file was held.)
    /// </summary>
    private bool Retry(History.History h, Incident inc) => Write(h, inc, chartsAlone: inc.ChartsAloneFailed && !inc.Dirty);

    /// <summary>Whether a closed incident's events.csv is still to be written whole: with both ends of its window (<see cref="IncidentWriter.BothEndsDue"/>), or in time order (<see cref="IncidentWriter.OutOfOrder"/>).</summary>
    private bool Unsettled(Incident inc, long nowMs)
    {
        var ev = IncidentEvents(inc, nowMs, forReports: false);
        return _writer.BothEndsDue(inc, ev) || _writer.OutOfOrder(inc, ev);
    }

    /// <summary>Whether the incident's window took an event in since its folder was last written (<see cref="KeptEvents.Took"/>).</summary>
    private bool Took(Incident inc) => _kept.TryGetValue(inc, out var k) && k.Took;

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
            // A drop has no rise: its last rise is the moment of the alert that saw it, as an incident made for a trace
            // has the moment it was made (CreateCapture). Its window, the events its report lists and its events.csv
            // holds, ends a minute after that (EventWindowEndMs), and its processes stay resolvable until 10 minutes
            // after it (ProtectWindow). (Left unset, the window ended a minute after Windows started: the report listed
            // no event, and events.csv held only the alert's row.)
            inc.LastAlertUtc = inc.LastRiseUtc = a.TimeUtc;
            inc.LastRiseMs = inc.LastAlertMs = a.Ms;
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
    /// system clock set back or forward inside the window moves none of them; the times of day kept are the samples' (but
    /// for the start of an incident made for a trace of its own, which is as it was set: <see cref="Measure"/>).
    /// </summary>
    private void Rank(History.History h, Incident inc, long toMs)
    {
        if (Measure(h, inc, toMs) is { } window) RankSuspects(h, inc, window, toMs);
    }

    /// <summary>
    /// The growth episode of <see cref="Rank"/> alone: its window, onset, end and levels (what the window's row, the tray and
    /// a refresh's triggers read), without the suspects; null when the history cannot say (then nothing is ranked). A
    /// growth's onset is measured: its alerts' earliest, then where the rise began. An incident made for a trace of its own
    /// (<see cref="IncidentKind.Capture"/>) keeps the start it was set with (<see cref="CreateCapture"/>,
    /// <see cref="ApplyCapture"/>) on both clocks: only its levels and the end of its rise are measured, from that start on.
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
            // growth began (its growth would shrink as the history moves on, and the window its report reads would start later
            // and later). Its end and its highest point are measured on what the history holds, with what PoolWatch's own
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
            int o;
            if (inc.Kind == IncidentKind.Capture)
            {
                // An incident made for a trace of its own (Capture now, or a lag trip's CPU trace) has no growth whose start
                // is to be found: its start is where it was set, on both clocks, the moment it was made and then, once its
                // trace finished, the trace's own start (ApplyCapture). It is never moved to where the tag's rise began (as far
                // back as the lead above looks, a minute before a 60 s trace, which can be before the incident was made), nor
                // read off the next sample (the owner's choice, 2026-11-11): the window its report lists starts where its
                // texts say. The tag's levels are read from the first sample at or after the start (the last one before it,
                // when the trace ended before the next sample), and the end of its rise is looked for from there on only, so
                // it is never before the start.
                o = Math.Clamp(h.IndexAtOrAfterMs(onsetMs) - iFrom, 0, series.Length - 1);
                var cp = Detector.FindChangePoints(grown[o..]);
                if (cp.Rise > 0 && cp.EndIndex is { } e && iFrom + o + e < iTo) (end, endMs) = (h.Times[iFrom + o + e], h.Ms[iFrom + o + e]);
            }
            else
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
                o = Math.Clamp(h.IndexAtOrAfterMs(onsetMs) - iFrom, 0, series.Length - 1);
                inc.OnsetMs = h.Ms[iFrom + o];
            }
            (inc.EndUtc, inc.EndMs) = (end, endMs);
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
        (inc.CurrentBytes, inc.CurrentAtUtc) = (series[^1], h.Times[iFrom + series.Length - 1]); // the level, and the sample it is of (Incident.LevelSaid)
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
                var starts = StartsByOwner(_events, inc.OnsetMs, inc.SpanEndMs);
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
            (inc.CurrentBytes, inc.CurrentAtUtc) = (cur, h.Times[^1]);
            // Its rise counts back what PoolWatch's own clears freed of it: said as theirs, not as given back. Said of
            // the sample it was found at, with that sample's time: the incident is closed, and its report (which gives
            // this as its "Retained" line) is written again hours later, when "still holds" read as of then.
            string note = $"{inc.Tag.ToDisplay()} still held {Fmt.Bytes(cur - inc.StartBytes)} of its {Fmt.Bytes(inc.Growth)} rise at {Fmt.Clock(h.Times[^1])}, " +
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
    /// events after <see cref="Settings.IncidentEventKeepMinutes"/>, the incident keeps both ends of its window, the first
    /// <see cref="IncidentWriter.MaxEventRows"/> from the window's start as its first part last followed it and the latest
    /// <see cref="IncidentWriter.MaxEventRows"/> up to its end now, with how many it left out between them and when, and how
    /// many it took in in all; its events.csv is written from those), and, for the reports
    /// (<paramref name="forReports"/>), the events of the window as it is measured now: those kept and the others still in
    /// the buffer.
    /// </summary>
    /// <param name="bothEnds">Both ends of an open incident's window are wanted (an export): its first part follows its
    /// window's start now (<see cref="Follow"/>).</param>
    internal IncidentEventSet IncidentEvents(Incident inc, long nowMs, bool forReports = true, bool bothEnds = false)
    {
        var k = KeepEvents(inc, nowMs, bothEnds);
        long from = EventWindowStartMs(inc), to = EventWindowEndMs(inc, nowMs);
        var (tail, left) = TailOf(k, to);
        var window = new List<LifecycleEvent>();
        if (forReports)
        {
            foreach (var e in inc.Events) if (e.Ms >= from && e.Ms <= to) window.Add(e);
            foreach (var e in k.Tail) if (e.Ms >= from && e.Ms <= to) window.Add(e);
            // The buffer's events the incident did not keep: those before it kept from, those it let go of between its two
            // parts, and those after its window's end (until it stopped keeping its first part it kept every one it looked
            // at, and it has looked at them all).
            for (int i = 0; i < _events.Count; i++)
            {
                var e = _events[i];
                if (e.Ms < from || e.Ms > to) continue;
                bool kept = e.Ms >= k.FromMs && (k.CutMs is null ? _eventsDropped + i < k.Seq : Holds(inc.Events, e) || Holds(k.Tail, e));
                if (!kept) window.Add(e);
            }
            window.Sort((x, y) => x.Ms.CompareTo(y.Ms));
        }
        return new IncidentEventSet(window, inc.Events, k.FromMs, to, k.CutUtc, k.CutMs)
        {
            FromUtc = k.FromUtc, LostToUtc = k.LostTo?.Utc, Tail = tail, Gap = left, LatestReleased = k.Released,
            // The window's end as a time of day, where it has one of its own (EventWindowEndMs: not an open incident's
            // that runs to now), for the texts that say a count of those left out runs past it.
            EndUtc = inc.Kind == IncidentKind.Capture && inc.ClosedUtc is { } closedAt ? closedAt : (inc.EndUtc ?? (inc.IsOpen ? null : inc.LastRiseUtc))?.AddMinutes(1),
            // Every event it took in, wherever its window ends: its first part, those it let go, its latest part with the
            // events after the window's end it holds, and those after the end it set aside; and what its follows took
            // out of that count, so the count goes on across them.
            TakenIn = TakenInNow(inc, k) + k.Carried, Carried = k.Carried,
        };
    }

    /// <summary>Whether a list of events in time order holds this very event (a search by time; none outside its first and last time).</summary>
    private static bool Holds(List<LifecycleEvent> kept, LifecycleEvent e)
    {
        if (kept.Count == 0 || e.Ms < kept[0].Ms || e.Ms > kept[^1].Ms) return false;
        int lo = 0, hi = kept.Count;
        while (lo < hi) { int mid = (lo + hi) >>> 1; if (kept[mid].Ms < e.Ms) lo = mid + 1; else hi = mid; }
        for (int i = lo; i < kept.Count && kept[i].Ms == e.Ms; i++) if (ReferenceEquals(kept[i], e)) return true;
        return false;
    }

    /// <summary>
    /// The events of the growth itself, on the clock that only goes forward: from the onset minus 2 min to the end of the
    /// growth (or now while it climbs) plus 1 min; a drop's (<see cref="IncidentKind.Release"/>) from its onset minus
    /// 2 min to a minute after the alert that saw it (its last rise is that moment: <see cref="OnRelease"/>). An
    /// incident made for a trace of its own (<see cref="IncidentKind.Capture"/>) has its trace for a window, exactly
    /// (the owner's choice, 2026-11-11): from its start, the trace's own once the trace finished, to where it closed,
    /// with no minutes before and none added: the moment it was made, then the trace's own start and end
    /// (<see cref="ApplyCapture"/>; a trace that did not record leaves both where they were). Neither is measured or
    /// moved otherwise, whatever its tag did.
    /// </summary>
    public static long EventWindowStartMs(Incident inc) => inc.Kind == IncidentKind.Capture ? inc.OnsetMs : inc.OnsetMs - 120_000;

    /// <summary><see cref="EventWindowStartMs"/> as a time of day: what the reports and events.csv give as the window's start.</summary>
    public static DateTime EventWindowStartUtc(Incident inc) => inc.Kind == IncidentKind.Capture ? inc.OnsetUtc : inc.OnsetUtc.AddMinutes(-2);

    public static long EventWindowEndMs(Incident inc, long nowMs) =>
        inc.Kind == IncidentKind.Capture && inc.ClosedMs is { } closed ? closed : (inc.EndMs ?? (inc.IsOpen ? nowMs : inc.LastRiseMs)) + 60_000;

    /// <summary>
    /// At most this many of an incident's events go into its events.csv at one write that can wait (the rest at later
    /// writes): ten incidents opening together each get the start of their window at once, and none takes the write
    /// budget alone. A closed incident's write that must be made (its close, a trace attached, the write for its
    /// window's late events) gives its file every row it is to hold, at most <see cref="IncidentWriter.MaxEventRows"/>
    /// of each end, so that write leaves no row waiting (<see cref="Write"/>).
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
    /// (<see cref="AppendEvents"/>), after every refresh due. A closed incident's write that must be made replaces its
    /// events.csv whole with both ends of its window when it does not hold them yet (its closing write, once), and gives
    /// a file that holds its first part alone every row left, in time order.
    /// </summary>
    /// <param name="rows">How many rows of events.csv the write may add (null: as above; every row left for a closed
    /// incident's write that must be made: its close, a trace attached, so that write leaves its events.csv with all it
    /// is to hold, a file with its first part alone as one with both ends, and its report counting them. Left at
    /// <see cref="EventRowsPerWrite"/>, a trace of a minute on a busy PC, about 157 events, got 100 rows as its trace
    /// was attached, and the rest only as the budget had room, or never when no late event followed).</param>
    /// <param name="waiting">The write is for rows of events.csv an earlier write left for later (a closed incident's,
    /// written with its report): when the rows' own write fails they are tried again at each sample, and the log says
    /// so once (<see cref="RowsFailed"/>), where another write that fails is not tried again at the next sample: an
    /// open incident's until its next change (logged each time), a closed incident's until
    /// <see cref="FailedWriteRetry"/> after it, and after each try that fails (<see cref="Retry"/>; said once, and
    /// once when its folder is written again, or, of a try that left one of the chart and the two other CSVs as it
    /// is, that the folder is written again but for that file, with why: <see cref="FolderWrittenBut"/>). So is this
    /// one when its rows were written and the report beside them could not be (while rows still wait after it, the
    /// next sample writes more of them with the report again).</param>
    /// <param name="chartsAlone">A try for a closed incident's chart, timeline.csv and processes.csv alone
    /// (<see cref="Retry"/>): the writer makes the three, and goes on to events.csv and the two reports only when
    /// the report does not show the folder as the try left it (<see cref="IncidentWriter.ReportsWritten"/> says
    /// which). A try that wrote no report leaves what the incident's last write of its report left (when it was,
    /// what it wrote, what still waits for a report), and still makes the failed write good, or fails again, as any
    /// try does.</param>
    /// <returns>Whether the whole folder was written (false: it waits for the budget, or the write failed and was
    /// logged, also when it wrote everything but a timeline.csv or a processes.csv another program holds).</returns>
    private bool Write(History.History h, Incident inc, bool mustWrite = true, bool? full = null, int? rows = null, bool waiting = false, bool chartsAlone = false)
    {
        long nowMs = NewestMs(h);
        if (!mustWrite && !_finishing && !Budget.HasRoom(nowMs))
        {
            Budget.Hold();
            return false;
        }
        bool all = _finishing || (full ?? (!inc.IsOpen || ((_writer.FullWrittenMs(inc) is not { } fw || nowMs - fw >= Ms(OpenChartInterval)) && Budget.HasRoomToSpare(nowMs))));
        rows ??= _finishing || (mustWrite && !inc.IsOpen) ? IncidentWriter.MaxEventRows : all ? EventRowsPerWrite : 0;
        using var stage = Util.Profiler.Stage("incident.write");
        try
        {
            Budget.Spend(_writer.Write(h, inc, IncidentEvents(inc, nowMs), rows.Value, all, nowMs, must: mustWrite || _finishing, chartsAlone), nowMs, mustWrite || _finishing);
            // False only for a try for the chart files alone that found the report showing them as they are.
            bool reports = _writer.ReportsWritten(inc);
            if (reports)
            {
                inc.LastWriteUtc = h.Times.Count > 0 ? h.Times[^1] : DateTime.UtcNow;
                inc.LastWriteMs = nowMs;
                inc.WrittenTop = inc.Suspects.FirstOrDefault()?.Key;
                inc.WrittenGrowth = inc.Growth;
                inc.Dirty = false;
                inc.CountPending = false;
            }
            // Another program holds its timeline.csv or its processes.csv: the write wrote everything else it was
            // to write (the other of the two, events.csv, report.json and report.md; of a try for the chart files
            // alone, the other of the two), so what is written above is true, and the file that could not be
            // written is a failure of this write all the same (below).
            var notWritten = _writer.ChartFailure(inc);
            // A write that failed is made good by any write of an open incident's folder, as before; of a closed
            // incident's only by a write of its whole folder that must be made (Retry, or its close, a trace attached,
            // the end of a run), or, when its timeline.csv or its processes.csv is all that is still to write, by
            // Retry's try for the chart files alone. A write of its report and rows that is made meanwhile (a late
            // event's) leaves what else failed still to write: the chart and the two other CSVs of a full write, the
            // whole events.csv of one that must be made. (A closed incident's write that could not write a
            // timeline.csv or a processes.csv is a failure again: set below.)
            if (inc.WriteFailed && (inc.IsOpen || (all && mustWrite))) inc.WriteFailed = false;
            // The log said that a closed incident's folder could not be written: it says that the folder is written
            // again only once it is, whole. An incident that re-opened meanwhile gets its report at once (that write
            // makes its failure good, as any write of an open incident's does), and its chart and the two other CSVs,
            // which may be what could not be written, only at a later write with them (a refresh, its close, or a
            // try after that close): the line waits until one is made, with both CSVs. (Before, the re-opening's
            // write of the report alone logged "its folder is written again" while another program still held its
            // timeline.csv.) And a try made after the history stopped reaching back to the start of the incident's
            // window can leave one of the chart and the two other CSVs as it is though it was never written for
            // the close (IncidentWriter.ChartFilesLeft): the line names that file, and nothing tries it again.
            if (inc.WriteFailSaid && !inc.WriteFailed && all && notWritten is null)
            {
                inc.WriteFailSaid = false;
                _host.Log(!inc.IsOpen && _writer.ChartFilesLeft(inc) is { Count: > 0 } left ? FolderWrittenBut(inc.Id, left) : $"incident {inc.Id}: its folder is written again");
            }
            if (reports)
            {
                if (_kept.TryGetValue(inc, out var written))
                {
                    written.Took = false; // the folder says what its window holds
                    if (mustWrite || _finishing) written.Late = false; // and its events.csv got every row it is to hold (a closed incident's)
                }
                if (!_writer.EventsPending(inc)) RowsWritten(inc);
            }
            if (notWritten is null) return true;
            // An open incident's report is written, and so not behind (BehindNow counts by this the reports that are
            // behind because their last write failed): its chart and its two other CSVs are tried again at its next
            // refresh with them, and count as behind by their age. A closed incident's file is tried again.
            FailedWrite(inc, notWritten, nowMs, behind: !inc.IsOpen, chartsAlone: true);
            return false;
        }
        catch (Exception ex)
        {
            // The rows' own write failed (events.csv, or the folder before it): they still wait, and are tried again
            // at each sample, said once. When the rows were written and it was the report beside them that could not
            // be (another program holds report.md), it failed as any write does: said as that, with no word of
            // events.csv, whose rows are in. (Before, every failure of this write was logged as events.csv's, and
            // "events.csv is written again" followed at the next write of a file that had not failed.)
            if (waiting && !_writer.EventsWritten(inc))
            {
                RowsFailed(inc, ex);
                return false;
            }
            // Not tried again at every sample: an open incident's next change rewrites it, and a closed incident's
            // folder is written again a minute on, and a minute after each try that fails (Process, Retry), and at
            // the end of a run (Finish).
            inc.Dirty = inc.CountPending = false;
            if (_kept.TryGetValue(inc, out var failed)) failed.Took = false;
            FailedWrite(inc, ex, nowMs, behind: true);
            return false;
        }
    }

    /// <summary>
    /// A write of the incident's folder failed: all of it from the file that could not be written on (the folder
    /// itself, events.csv, a report), or, when only its timeline.csv or its processes.csv could not be written, that
    /// file alone (the write went on, and the rest of the folder is written: <see cref="IncidentWriter.ChartFailure"/>).
    /// With <paramref name="behind"/> the incident counts as one whose folder is behind until a write makes it good
    /// (<see cref="Incident.WriteFailed"/>, <see cref="BehindNow"/>): every failure but an open incident's whose
    /// report was written. An open incident's failure is logged at each write that fails, as before (its next change
    /// tries again). A closed incident's is said once, with what PoolWatch does about it, however many tries fail
    /// after it (a folder that could not be written for an hour would log 60 lines), and once more when its folder
    /// is written again, or, when a try left one of its chart and its two other CSVs as it is, that it is written
    /// again but for that file (<see cref="FolderWrittenBut"/>). At the end of a run, and as the incident leaves
    /// memory, nothing is tried after this write: the line then says only that it failed.
    /// </summary>
    /// <param name="chartsAlone">All this write could not write was its timeline.csv or its processes.csv. Those
    /// files are then all that is still to write of the folder (<see cref="Incident.ChartsAloneFailed"/>), unless
    /// an earlier write that failed left more and has not been made good: this write was then one that can wait,
    /// and the whole folder is still to be written by one that must be made.</param>
    private void FailedWrite(Incident inc, Exception ex, long nowMs, bool behind, bool chartsAlone = false)
    {
        if (behind)
        {
            inc.ChartsAloneFailed = chartsAlone && (!inc.WriteFailed || inc.ChartsAloneFailed);
            inc.WriteFailed = true; // its folder is behind until then (BehindNow)
            inc.WriteFailedMs = nowMs;
        }
        if (inc.IsOpen) _host.Log($"incident {inc.Id}: write failed: {ex.Message}");
        else if (!inc.WriteFailSaid)
        {
            inc.WriteFailSaid = !_finishing && !_leaving;
            _host.Log($"incident {inc.Id}: write failed: {ex.Message}" +
                      (inc.WriteFailSaid ? " (while PoolWatch holds the incident it tries again, also as it stops; this is said once until the folder is written)" : ""));
        }
    }

    /// <summary>
    /// What the log says of the try that ends a closed incident's failed write when it left one or more of its chart,
    /// timeline.csv and processes.csv as they are though they were never written for its close
    /// (<see cref="IncidentWriter.ChartFilesLeft"/>, <paramref name="left"/>: their names): "incident X: its folder
    /// is written again, but for its timeline.csv, which stays as it is (PoolWatch no longer holds the samples from
    /// the start of its window to make it from)". Every other file of the folder is as a write for that close makes
    /// it: by this try, or, of the three, by an earlier one (the chart when it could be drawn: one that cannot be
    /// drawn is no failed write, here as in "its folder is written again", and a chart the folder does not have is
    /// not named as left: the line says of each file it names that it stays as it is). Such a file is left only
    /// once the history no longer reaches back to the start of the incident's window, which the line gives as the
    /// reason. A timeline.csv or processes.csv it names that the folder never had (another program kept it from
    /// being made from before the incident's first write of it) stays as it is too: not made.
    /// </summary>
    internal static string FolderWrittenBut(string id, IReadOnlyList<string> left)
    {
        bool one = left.Count == 1;
        string files = one ? left[0] : string.Join(", ", left.Take(left.Count - 1)) + " and " + left[^1];
        return $"incident {id}: its folder is written again, but for its {files}, which {(one ? "stays as it is" : "stay as they are")} " +
               $"(PoolWatch no longer holds the samples from the start of its window to make {(one ? "it" : "them")} from)";
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
    /// Writes the incident's whole folder now, every event of the first part of its window included (of an open one, but
    /// for a file that stays as written until the close: one with its Note row, or a re-opened incident's with both
    /// ends; a closed one's events.csv has both ends), whatever the write budget (it spends it as a write a reader
    /// needs at once):
    /// an export of an open incident holds its state now, not a refresh the budget held back, and takes its events.csv's
    /// both ends from memory, with a report.md that says so (<see cref="ExportFilesNow"/>). False when the write failed
    /// (logged). The caller holds the history lock.
    /// </summary>
    public bool WriteNow(History.History h, Incident inc) => Write(h, inc, mustWrite: true, full: true, rows: IncidentWriter.MaxEventRows);

    /// <summary>
    /// An open incident's events.csv as it would be written were the incident to close now, built from memory (the first
    /// <see cref="IncidentWriter.MaxEventRows"/> events from its window's start as it is measured now, its first part
    /// following it at once, a row saying how many
    /// it left out and between which times when it left any out, then its latest <see cref="IncidentWriter.MaxEventRows"/>, sorted by time, its alerts'
    /// rows among them), and its report.md as a write would make it now beside that file, so its line under the window's
    /// events counts that file's rows and says what it leaves out; by their names. While the incident is open its folder's
    /// events.csv holds the first part only, so Export report puts these two in place of the folder's (the zip agrees with
    /// itself). Null for a closed incident (its file has both ends). Writes nothing. The caller holds the history lock.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ExportFilesNow(History.History h, Incident inc)
    {
        if (!inc.IsOpen) return null;
        var (events, report) = _writer.ExportTexts(h, inc, IncidentEvents(inc, NewestMs(h), bothEnds: true));
        return new Dictionary<string, string> { ["events.csv"] = events, ["report.md"] = report };
    }

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

    /// <summary>
    /// An open incident's rows of events.csv that a write left for later, while the write budget has room; false when it
    /// had none. (A closed incident's are written with its report, whose line under the window's events counts the
    /// file's rows: <see cref="Process"/>.)
    /// </summary>
    private bool AppendEvents(History.History h, Incident inc)
    {
        long nowMs = NewestMs(h);
        if (!Budget.HasRoom(nowMs)) { Budget.Hold(); return false; }
        using var stage = Util.Profiler.Stage("incident.write");
        try
        {
            Budget.Spend(_writer.AppendEvents(inc, IncidentEvents(inc, nowMs, forReports: false), EventRowsPerWrite), nowMs);
            if (!_writer.EventsPending(inc)) RowsWritten(inc);
        }
        catch (Exception ex) { RowsFailed(inc, ex); }
        return true;
    }

    /// <summary>
    /// A write of the incident's waiting rows failed. The rows stay due and are tried again at the next sample: said
    /// once, not at every sample (a folder that cannot be written for an hour logged 360 lines), and once more when the
    /// file has them (<see cref="RowsWritten"/>).
    /// </summary>
    private void RowsFailed(Incident inc, Exception ex)
    {
        bool said = _kept.TryGetValue(inc, out var k) && k.RowsFailed;
        if (k is not null) k.RowsFailed = true;
        if (!said) _host.Log($"incident {inc.Id}: events.csv write failed: {ex.Message} (its rows are tried again at each sample; this is said once until they are written)");
    }

    /// <summary>
    /// The incident's events.csv has the rows that waited for it (a write was made and none waits now): when their
    /// writes had failed before (<see cref="RowsFailed"/>), the log says they are written again. Not after a write that
    /// added no row while they still wait (a report's refresh: said then, it was untrue, and the failure was logged
    /// again at the next sample).
    /// </summary>
    private void RowsWritten(Incident inc)
    {
        if (!_kept.TryGetValue(inc, out var k) || !k.RowsFailed) return;
        k.RowsFailed = false;
        _host.Log($"incident {inc.Id}: events.csv is written again");
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
