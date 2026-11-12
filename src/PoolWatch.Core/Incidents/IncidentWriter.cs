using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Identity;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;
using PoolWatch.Core.Util;

namespace PoolWatch.Core.Incidents;

/// <summary>
/// Writes an incident folder: report.md (headline, chart, suspects with evidence, process details, system info,
/// draft bug report), report.json (the same, machine-readable), timeline.csv, processes.csv, events.csv and
/// timeline.png. report.md, report.json, timeline.csv, processes.csv and timeline.png are replaced atomically (temp file +
/// move) so a reader never sees half a report, and only when what they say changed since this run last wrote them (an
/// unchanged file, a closed incident's CSVs and chart among them, is not written again). events.csv holds both ends of the
/// window the report gives: while the incident is open it is appended to, from the first part of what the incident kept
/// of its events (<see cref="IncidentEventSet.Kept"/>: the earliest by time from the window's start as the first part
/// last followed it, in time order, at most <see cref="MaxEventRows"/>): each event once, the earliest not written yet
/// first, so the file holds every event from its first row to its last, however far the write budget held it back; when
/// the incident's first part is full and the file has all of it, a last row says to when it holds them, and no event row
/// follows it. When the incident closes (and at the end of a run, or as it leaves memory, when it has not been yet) it is
/// replaced whole in one step with both ends: the first part, a row between the two parts saying how many events it left
/// out and between which times (when it left any out), and the latest <see cref="MaxEventRows"/> events up to the window's
/// end (<see cref="IncidentEventSet.Tail"/>), all sorted by time, each event once; once the incident let go of its latest
/// part (<see cref="IncidentEventSet.LatestReleased"/>) that file is final. The first part follows the window's start
/// when the onset is measured somewhere else (<c>IncidentManager.Follow</c>: a closed incident's at once, an open one's
/// at most every 10 minutes), so a file written whole starts where the report's window does. An open incident's file
/// that still gets rows starts again from the first part once that has followed (it is written again from the new start,
/// with the rows the first part still holds), and when a late event takes a place among rows it holds (it never holds
/// more than the first part, nor a row after its last row's time); one whose last row says to when it holds its events
/// stays as written until the close. A closed incident's file with its first part alone (no more events than that) is
/// written by each write that must be made too: every row left, and whole, in time order, when a row was added after
/// one of a later time (<see cref="FileState.OutOfOrder"/>). Wherever a file's rows are not from where the window
/// starts now, report.md's line under the window's
/// events says where they are from and where the window starts (<see cref="StartSaid"/>), and every text says so when
/// PoolWatch no longer held every event from the start (<see cref="FirstPartSaid"/>), or when a count of the events
/// left out runs past the window's end (<see cref="PastEnd"/>). It is written whole otherwise
/// only when it is not as PoolWatch last wrote it. Each write returns the bytes it wrote, for the report folders' budget
/// (<see cref="WriteBudget"/>).
/// </summary>
internal sealed class IncidentWriter
{
    /// <summary>
    /// The most events of each end of an incident's window its events.csv holds (the alerts' rows and the row between the
    /// two ends aside), and that an incident keeps in memory for it: the first 500 by time from the window's start (the
    /// onset less 2 minutes, as the onset is measured at that write: the first part follows it), about 3 minutes of a
    /// busy agentic PC's events (9,400 an hour), more on a quieter one, and its latest 500 up to the window's end,
    /// about its last 3 minutes there; with Detailed recording the day's events.csv has every one. 500 at each end is the
    /// owner's choice (2026-11-10) over 1,000: a long incident's closing write replaces its events.csv whole with both ends,
    /// a write that must be made, and at 1,000 each, about 3 such closes an hour (the maintainer's rate) made the report
    /// folders write 3.1 MB an hour, against 2.24 MB at 500 (still about 0.24 MB over their share; <c>BothEndsTests</c>
    /// measures what the closes write, DESIGN.md section 3 compares it). An
    /// incident is kept up to 8 hours (open 2, closed 6) and 26 were held at once on the maintainer's PC: 5,000 each held up
    /// to 130,000 events, most of which the write budget could never write. An open incident shares its events with the
    /// 90-minute buffer only for those 90 minutes; past them, and once closed, what it kept is its own: in AgentLoadTests'
    /// world (10 incidents open all day, 20 closed) 5,000 held 3.2 MB more heap at 6 hours than 1,000 (42.1 against 38.9 MB),
    /// and with all 32 incidents held at the cap it would be about 29 MB more (4,000 more events each, at the 225 bytes an
    /// event the world's closed incidents hold), against a heap already over its target. Its latest 500 are the newest,
    /// which an open incident shares with the buffer, and a closed one lets them go once it can no longer re-open and its
    /// file holds them (an hour after it closed): <c>BothEndsTests</c> measures what they hold.
    /// </summary>
    public const int MaxEventRows = 500;

    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>
    /// An alert's row in events.csv: one per growth rule (kind + window) and onset, when the rule first fired with it (a rule
    /// that keeps firing replaces its alert with the bigger one and keeps its onset, so it stays one row; a re-opening's
    /// alert has its own onset, so a new episode gets its row; the report has the latest numbers), and one per release alert.
    /// </summary>
    private readonly record struct AlertRow(AlertKind Kind, TimeSpan Window, long OnsetMs, Alert? Release)
    {
        public static AlertRow Of(Alert a) => a.Kind == AlertKind.Release ? new(a.Kind, a.Window, 0, a) : new(a.Kind, a.Window, a.OnsetMs, null);
    }

    /// <summary>
    /// One of the three files a full write makes from the history's samples of the incident's window (the chart
    /// timeline.png, timeline.csv, processes.csv), as this run last wrote it: the close it was written for, and the
    /// samples it was written from, the first and the last by their place in the history's own count
    /// (<see cref="History.History.SeqAt"/>: every sample between them was in it). Each of the three is written, or
    /// refused by another program, on its own, so they are not always of one write.
    /// </summary>
    private sealed class ChartFile(string name)
    {
        /// <summary>The file as a log line names it: "chart", "timeline.csv", "processes.csv".</summary>
        public readonly string Name = name;
        /// <summary>The close (<see cref="Incident.ClosedMs"/>) it was last written for; null: never written, or last written while the incident was open.</summary>
        public long? ClosedMs;
        /// <summary>The first and the last sample it was last written from (<see cref="Last"/> under <see cref="First"/>: none, or never written).</summary>
        public long First, Last = -1;

        /// <summary>It was written just now, for <paramref name="closedMs"/> (null: while the incident is open), from the samples <paramref name="first"/> to <paramref name="last"/>.</summary>
        public void Wrote(long? closedMs, long first, long last) => (ClosedMs, First, Last) = (closedMs, first, last);

        /// <summary>How many of the samples it was last written from are among <paramref name="first"/> to <paramref name="last"/>.</summary>
        public long Within(long first, long last) => Math.Max(0, Math.Min(Last, last) - Math.Max(First, first) + 1);
    }

    /// <summary>
    /// What a report.md shows of its incident's folder: whether it has the chart's line
    /// (<paramref name="ChartLine"/>: the write that wrote it had a chart for the folder), and which of the files its
    /// draft bug report can list as attached the folder held when it was written (<see cref="Of"/>). A try for the
    /// chart files alone writes the reports again only when the folder no longer is as the report shows it.
    /// </summary>
    private readonly record struct Shows(bool ChartLine, bool Png, bool Timeline, bool Processes, bool Events)
    {
        /// <summary>The folder as it is now, for a report.md written now.</summary>
        public static Shows Of(Incident inc, bool chartLine)
        {
            bool Has(string name) => File.Exists(Path.Combine(inc.Dir, name));
            return new(chartLine, Has("timeline.png"), Has("timeline.csv"), Has("processes.csv"), Has("events.csv"));
        }
    }

    /// <summary>What this run last wrote into each incident's folder (forgotten with the incident).</summary>
    private sealed class FileState
    {
        /// <summary>Each replaced file's content hash, by file name.</summary>
        public readonly Dictionary<string, UInt128> Hashes = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>The events events.csv holds, by <see cref="RowKey"/> (no event is kept alive for it), and its alerts' rows; null before it was written.</summary>
        public HashSet<long>? Events;
        public HashSet<AlertRow>? Alerts;
        /// <summary>events.csv's length as written, to tell that it is still as written.</summary>
        public long Length;
        public int EventRows;
        /// <summary>
        /// The earliest event it holds, on the clock that only goes forward and as a time of day (null: none): report.md says
        /// so when its rows begin before the window as it is measured now (<see cref="StartSaid"/>).
        /// </summary>
        public (long Ms, DateTime Utc)? First;
        /// <summary>
        /// Where its first part starts (<see cref="IncidentEventSet.FromMs"/> and its time of day as the file was begun, or
        /// written whole): the window's start as the incident's first part had last followed it by then. null: events
        /// given as all there are, which have no start of their own. The texts about this file are from this, not from
        /// where the incident's first part starts now (it follows the window's start; a file can stay as written).
        /// </summary>
        public (long Ms, DateTime Utc)? From;
        /// <summary>When PoolWatch no longer held every event from <see cref="From"/> as the file's first part was taken, the time of the latest event it had let go (<see cref="IncidentEventSet.LostToUtc"/> then); else null.</summary>
        public DateTime? LostToUtc;
        /// <summary>The incident's count of events its follows took out (<see cref="IncidentEventSet.Carried"/>) as the file was begun: the events that came from the file's start on are counted from it.</summary>
        public long CarriedAtStart;
        /// <summary>
        /// The incident's first part followed the window's start since the file was begun, and the file is to start again
        /// from it at its next write of rows (a write without rows left it as it was): what report.md says it gets.
        /// </summary>
        public bool StartsAgain;
        /// <summary>Its last row says the events after that row's time are not in it yet (an open incident's first part is full; its latest come at its close): when, else null.</summary>
        public DateTime? NotedAt;
        /// <summary>A write left rows of the window for later (it may write only so many, or none while the write budget has no room).</summary>
        public bool Pending;
        /// <summary>
        /// The last write of the folder (<c>Write</c>) got as far as events.csv, and wrote it: when that write then
        /// failed, it was at a file written after events.csv (report.json, report.md), not at the rows
        /// (<see cref="EventsWritten"/>).
        /// </summary>
        public bool EventsDone;
        /// <summary>
        /// The last write of the folder (<c>Write</c>) got as far as its two reports, and wrote them
        /// (<see cref="ReportsWritten"/>): false when it failed before or at one of them, and for a try for the chart
        /// files alone that found the report showing them as they are, which leaves the reports as they were.
        /// </summary>
        public bool ReportsDone;
        /// <summary>What report.md shows of the folder, as this run last wrote it (<see cref="Shows"/>); null: this run has not written it.</summary>
        public Shows? Shown;
        /// <summary>When timeline.csv and processes.csv were last both written by one write (a full write, which draws the chart first), on the forward clock; null: never.</summary>
        public long? FullMs;
        /// <summary>
        /// What the chart, timeline.csv and processes.csv were each last written from, and for which close
        /// (<see cref="ChartFile"/>): once the history no longer reaches back to the start of a closed incident's
        /// window, a write makes each of the three only as <see cref="Leaves"/> allows.
        /// </summary>
        public readonly ChartFile Chart = new("chart"), Timeline = new("timeline.csv"), Processes = new("processes.csv");
        /// <summary>
        /// The window's start (<see cref="CorrelationWindow.FromMs"/>) and its first sample's place in the history's
        /// own count (<see cref="History.History.SeqAt"/>), as a write last found it while the history still held
        /// that sample: once the history has let it go, how many of the window's samples a file holds is counted
        /// from here.
        /// </summary>
        public (long FromMs, long Seq)? WindowStart;
        /// <summary>
        /// Those of the chart, timeline.csv and processes.csv that the last write of the folder left as they are
        /// though they were never written for the close as it stands, by name (<see cref="ChartFilesLeft"/>; a
        /// chart only when the folder has one). Each write starts with none.
        /// </summary>
        public List<string> ChartLeft = [];
        /// <summary>
        /// Why the last write of the folder could not write timeline.csv or processes.csv (the first of them that
        /// failed); null: it wrote those it was to write (<see cref="ChartFailure"/>). Each write starts with none.
        /// </summary>
        public Exception? ChartFailure;
        /// <summary>How many events the incident had kept at the last write of events.csv (it keeps more while open).</summary>
        public int KeptSeen;
        /// <summary>
        /// It holds both ends of the window, written whole (<see cref="TailKey"/>: what it holds of the latest part, and how
        /// many it left out), or, after a last row for rows the write budget never had room for, that row's: null while it
        /// holds the first part only (rows after its last would then come after the latest part).
        /// </summary>
        public (int Rows, long First, long Last, int LeftOut)? Tail;
        /// <summary>The row it holds that says how many events it leaves out and between which times; null: none.</summary>
        public EventGap? Gap;
        /// <summary>
        /// Its <see cref="Gap"/> row counts events from after the window's end as the file was written (the count was made
        /// while the window ended later, and PoolWatch no longer held the events to count them again:
        /// <see cref="IncidentEventSet.EndUtc"/>): where the window ended then, which the row gives; else null.
        /// </summary>
        public DateTime? GapPastEndUtc;
        /// <summary>The latest time of day of a row (an event's or an alert's) the file holds; null: it holds none.</summary>
        public DateTime? LastRowUtc;
        /// <summary>
        /// A row (an event's or an alert's) was added to the file after one of a later time: while its incident is open the
        /// file is in the order PoolWatch added its rows (an alert's row comes with the write it fired at, the window's
        /// earlier events after it as the write budget allows; a late event after later ones). A closed incident's file is
        /// in time order: a write that must be made writes it whole then (<see cref="WriteEventsCsv"/>).
        /// </summary>
        public bool OutOfOrder;
        /// <summary>
        /// What the report says from the history as it was at one moment: the PC's counters at the last sample, with that
        /// sample's time, the tag's allocation rates over the growth, with the span they are of, and the tag's paged and
        /// non-paged bytes at the last sample (the plain answer names the tag's pool by them). The history moves on
        /// (it keeps <see cref="Settings.RawHistoryMinutes"/>), so a closed incident's are kept as the first write after
        /// its close read them (<see cref="ThenFor"/>): a report written again later (an alert counted after the close, a
        /// late event, the incident leaving memory) says what was so then, with its time, not what the history holds by
        /// then (which gave the PC's counters of hours later as "the last sample", no allocation rates once the
        /// growth had left the history, and the tag's pool as it was hours later). An open incident's are read at every
        /// write.
        /// </summary>
        public ((SystemCounters Counters, DateTime TimeUtc)? System, ((TagRates During, TagRates? Before) Rates, string Span)? Shape, (long Paged, long NonPaged)? Level) Then;
        /// <summary>The close and the growth <see cref="Then"/> was read for: the incident's close, onset and end on the forward clock (a trace attached moves them, and they are read again); null: not for a closed incident.</summary>
        public (long ClosedMs, long OnsetMs, long? EndMs)? ThenFor;
    }

    /// <summary>
    /// Reads what the report says from the history at this write (<see cref="FileState.Then"/>): at every write of an open
    /// incident, and once for a closed one, at its first write after the close as it stands.
    /// </summary>
    private void ReadThen(Incident inc, FileState st)
    {
        (long, long, long?)? key = inc.ClosedMs is { } closed ? (closed, inc.OnsetMs, inc.EndMs) : null;
        if (key is not null && st.ThenFor == key) return;
        st.Then = (_history is { Count: > 0 } h ? (h.System[^1], h.Times[^1]) : null, Shape(inc) is { } rates ? (rates, ShapeSpan(inc)) : null,
            _history is { Count: > 0 } held ? held.TagAt(inc.Tag, held.Count - 1) : null);
        st.ThenFor = key;
        _plainFor = null; // the plain answer names the tag's pool by the level read here
    }

    /// <summary>
    /// The incident leaves memory with an events.csv that is not to be replaced whole (<see cref="BothEndsDue"/>: one that
    /// is, is written with its report, so the two agree: <c>IncidentManager.Process</c>): when the write budget never had
    /// room for some of its first part, a last row says how many it leaves out and between which times (only on a file as
    /// this run wrote it: another is left as it is; the day's events.csv has them with Detailed recording, and they are not
    /// recorded in Reports only). Written whatever the budget. Returns the bytes written.
    /// </summary>
    public long WriteLeaving(Incident inc, IncidentEventSet ev)
    {
        string path = Path.Combine(inc.Dir, "events.csv");
        var st = StateOf(inc);
        var p = Plan(inc, ev, st, path);
        if (p.BothEnds) return 0; // it holds both ends, or is replaced whole with them by the write that also writes its report
        if (!p.AsWritten) return 0; // not as written: left as it is
        if (p.Fresh.Count == 0 || st.NotedAt is not null || st.Tail is not null) return 0;
        var gap = EventGap.Of(p.Fresh, 0, p.Fresh.Count);
        long bytes = AppendRows(inc, path, st, [], null, (gap, LeftByBudget));
        (st.Gap, st.GapPastEndUtc, st.Tail, st.Pending) = (gap, null, (0, 0, 0, gap.Count), false); // every one of them is the window's (EventsPlan.Fresh)
        return bytes;
    }

    /// <summary>
    /// Whether the incident's events.csv is still to be replaced whole with both ends of its window: a closed incident whose
    /// file does not hold them as they are now (its closing write failed, or its window took more in since: late events).
    /// The end of a run writes it then, and so does the incident leaving memory, each with its report.
    /// </summary>
    public bool BothEndsDue(Incident inc, IncidentEventSet ev)
    {
        if (inc.IsOpen) return false;
        var p = Plan(inc, ev, StateOf(inc), Path.Combine(inc.Dir, "events.csv"));
        return p.BothEnds && !p.Holds;
    }

    /// <summary>
    /// Whether a closed incident's events.csv does not hold the events of its window that the incident holds for it
    /// (those its report lists and counts), each once and in time order: a file that is to hold both ends and does not
    /// hold them as they are now, or one with its first part alone that has rows of it still to get, holds a row that is
    /// no longer the first part's, or holds a row after one of a later time (a late event's, added as it came). Once
    /// every late event of the window can have come, a write that must be made gives it them
    /// (<c>IncidentManager.Process</c>); so does the end of a run. (A row that is no longer the first part's,
    /// <see cref="EventsPlan.Moved"/>: of an event the first part let go as it followed the window's start, or of one
    /// a late event moved out of it. For a closed incident the second takes a window's end measured earlier since the
    /// row was written, by a trace attached after the close: an event of the window that is moved out of the first
    /// part goes to the latest part, and the file is then to hold both ends; one from after the window's end is none
    /// of the window's, and neither is its row: the file starts again from the first part, up to the end.)
    /// </summary>
    public bool LacksEvents(Incident inc, IncidentEventSet ev)
    {
        if (inc.IsOpen) return false;
        var st = StateOf(inc);
        var p = Plan(inc, ev, st, Path.Combine(inc.Dir, "events.csv"));
        return p.BothEnds ? !p.Holds : p.Fresh.Count > 0 || p.Followed || p.Moved || (p.AsWritten && st.OutOfOrder);
    }

    /// <summary>
    /// Whether a closed incident's events.csv with its first part alone, as this run wrote it, holds a row after one of a
    /// later time (<see cref="FileState.OutOfOrder"/>): the write that must be made as the incident leaves memory
    /// writes it in time order then, as <see cref="BothEndsDue"/> has a file replaced with both ends.
    /// </summary>
    public bool OutOfOrder(Incident inc, IncidentEventSet ev)
    {
        if (inc.IsOpen) return false;
        var st = StateOf(inc);
        var p = Plan(inc, ev, st, Path.Combine(inc.Dir, "events.csv"));
        return !p.BothEnds && p.AsWritten && st.OutOfOrder;
    }

    /// <summary>
    /// Whether a write that can wait has rows to give a closed incident's events.csv (<see cref="EventsPending"/> is
    /// told by counts alone, and a closed incident's first part also takes events from after its window's end until
    /// it is full, none of which is its file's): a file with its first part alone that lacks rows of it, or is to
    /// start again from it. Not a file that is to hold both ends (only a write that must be made replaces it). When
    /// none waits, the file's state says so from here on, so nothing is tried for it at the next sample.
    /// </summary>
    public bool RowsWait(Incident inc, IncidentEventSet ev)
    {
        var st = StateOf(inc);
        var p = Plan(inc, ev, st, Path.Combine(inc.Dir, "events.csv"));
        if (!p.BothEnds && (p.Fresh.Count > 0 || p.Followed || p.Moved)) return true;
        (st.KeptSeen, st.Pending) = (ev.Kept.Count, false);
        return false;
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Incident, FileState> _state = new();

    private FileState StateOf(Incident inc) => _state.GetValue(inc, _ => new FileState());

    /// <summary>A write of the incident left rows of its window out of events.csv for later (<see cref="AppendEvents"/>).</summary>
    public bool EventsPending(Incident inc) => _state.TryGetValue(inc, out var st) && Pending(inc, st);

    /// <summary>
    /// Whether the last write of the incident's folder wrote its events.csv (<see cref="FileState.EventsDone"/>): when
    /// a write of its folder fails, the manager's log says by this which it was, the rows' own write (or what comes
    /// before it: the folder itself), or a file written after them (report.json, report.md).
    /// </summary>
    public bool EventsWritten(Incident inc) => _state.TryGetValue(inc, out var st) && st.EventsDone;

    /// <summary>Kept events not in events.csv yet: a write left some for later, or the incident kept more since its last write.</summary>
    private static bool Pending(Incident inc, FileState st) => st.Events is not null && st.NotedAt is null && (st.Pending || inc.Events.Count != st.KeptSeen);

    /// <summary>
    /// Whether the closed incident's events.csv holds both ends of its window as this run wrote them, as they are now: the
    /// manager then lets go of its latest part once it can no longer re-open (<see cref="IncidentEventSet.LatestReleased"/>).
    /// </summary>
    public bool HoldsBothEnds(Incident inc, IncidentEventSet ev)
    {
        if (inc.IsOpen) return false;
        var p = Plan(inc, ev, StateOf(inc), Path.Combine(inc.Dir, "events.csv"));
        return p.BothEnds && p.Holds;
    }

    /// <summary>When the incident's timeline.csv and processes.csv were last both written by one write this run (a full write, which draws the chart too), on the forward clock; null: never.</summary>
    public long? FullWrittenMs(Incident inc) => _state.TryGetValue(inc, out var st) ? st.FullMs : null;

    /// <summary>
    /// Those of a closed incident's chart, timeline.csv and processes.csv that the last write of its folder, one
    /// that was to make them, left as they are though they were never written for its close as it stands, by name
    /// ("chart", "timeline.csv", "processes.csv"; not a chart the folder does not have: <see cref="Leaves"/>); none
    /// when it left no such file. Such a file's write for that
    /// close had failed (another program held the file, or the folder could not be written), and by this write the
    /// history no longer reached back to the window's start and held fewer of the window's samples than the file
    /// holds already, or none (<see cref="Leaves"/>). No later try makes such a file (the history only
    /// lets more of the window's samples go): the manager's log names it, and does not say that the whole folder is
    /// written again.
    /// </summary>
    public IReadOnlyList<string> ChartFilesLeft(Incident inc) => _state.TryGetValue(inc, out var st) ? st.ChartLeft : [];

    /// <summary>
    /// Why the last write of the incident's folder could not write its timeline.csv or its processes.csv (another
    /// program holds the file): the first of the two that failed; null when it wrote those it was to write. Such a
    /// write does not stop there: it writes the other of the two, events.csv, report.json and report.md, and returns
    /// what it wrote, so the report is never behind for a chart file; the manager keeps the failure, and tries the
    /// file again (<c>IncidentManager.Write</c>, <c>IncidentManager.Retry</c>). (Before, the write stopped at that
    /// file: a closed incident's report.md could go on saying "still open" while the file was held.)
    /// </summary>
    public Exception? ChartFailure(Incident inc) => _state.TryGetValue(inc, out var st) ? st.ChartFailure : null;

    /// <summary>
    /// Whether the last write of the incident's folder wrote its report.json and its report.md
    /// (<see cref="FileState.ReportsDone"/>). Every write that returns does, but a try for the chart files alone that
    /// found the report showing them as they are (<c>Write</c>'s <c>chartsAlone</c>): the reports are then as the
    /// write before left them, and the manager keeps what it knows of that write (when it was, what it wrote of the
    /// incident, what still waits for a report).
    /// </summary>
    public bool ReportsWritten(Incident inc) => _state.TryGetValue(inc, out var st) && st.ReportsDone;

    private readonly TagKnowledge _knowledge;
    private readonly IIncidentHost _host;
    private readonly ProcessRegistry _registry;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        // Paths and names stay readable (and redactable) instead of \uXXXX; the files are never embedded in HTML.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Session details of the suspects being written (set per write; the writer runs under the history lock).</summary>
    private Dictionary<ProcessKey, SessionDetails?> _sessions = new();

    public IncidentWriter(TagKnowledge knowledge, IIncidentHost host, ProcessRegistry registry)
    {
        _knowledge = knowledge;
        _host = host;
        _registry = registry;
    }

    private History.History? _history;
    private Incident? _plainFor;
    private TagAnswer? _plain;

    /// <summary>
    /// The capture's ETL as a path relative to THIS incident's folder. When a capture covers several tags the file lives
    /// in the primary incident's folder; a co-incident gets a relative path to it, so the trace is always findable.
    /// </summary>
    private static string? EtlRef(Incident inc, CaptureResult cap)
    {
        if (cap.EtlPath is null) return null;
        try { return Path.GetRelativePath(inc.Dir, cap.EtlPath).Replace('\\', '/'); }
        catch { return Path.GetFileName(cap.EtlPath); }
    }

    /// <summary><see cref="Write(History.History, Incident, IncidentEventSet, int)"/> with <paramref name="events"/> as all the window's events there are.</summary>
    public long Write(History.History h, Incident inc, List<LifecycleEvent> events, int maxRows = MaxEventRows) => Write(h, inc, IncidentEventSet.Of(events), maxRows);

    /// <summary>
    /// Writes the folder: each file whose content changed, and up to <paramref name="maxRows"/> of the kept events not in
    /// events.csv yet (the rest left for a later write, <see cref="EventsPending"/>); a closed incident's events.csv with both
    /// ends of its window, replaced whole, when it does not hold them yet and the write must be made
    /// (<paramref name="must"/>). Without <paramref name="full"/> (an open incident's refresh, its opening) only report.json
    /// and report.md (what the window's details and Copy summary read) and the events: the chart, timeline.csv and
    /// processes.csv stay as they were (<see cref="FullWrittenMs"/>). When timeline.csv or processes.csv cannot be
    /// written (another program holds it), the write goes on with the other of the two, events.csv and the two
    /// reports, and says so afterwards (<see cref="ChartFailure"/>): it throws only when the folder itself, events.csv
    /// or a report cannot be written. Returns the bytes written.
    /// </summary>
    /// <param name="atMs">The write's moment on the forward clock, kept as <see cref="FullWrittenMs"/> for a full write.</param>
    /// <param name="must">A write that must be made (an opening, re-opening or closing, a trace attached, the end of a run):
    /// only such a write replaces a closed incident's events.csv whole with both ends, as it is made whatever the budget.</param>
    /// <param name="chartsAlone">A try for a closed incident's chart, timeline.csv and processes.csv alone
    /// (<c>IncidentManager.Retry</c>: all that its failed write could not write was its timeline.csv or its
    /// processes.csv, and no change of the incident waits to have its folder written). Such a try makes the three
    /// as a full write does, and stops there when report.md shows the folder as it then is (<see cref="Shows"/>:
    /// the chart's line, the files its draft bug report lists as attached). It goes on to events.csv and the two
    /// reports, as any write does, only when report.md does not: the try made a file the report does not show yet
    /// (it drew a chart the report has no line for, or made a timeline.csv or a processes.csv the folder did not
    /// have), so the report is true again. (Before, every try wrote both reports again. They give PoolWatch's own
    /// cost as it is at each write, so a report is hardly ever the same as the one before: for as long as another
    /// program held the file, about a report a minute was written, whole, as a write that must be made, though no
    /// change waited to be written.)</param>
    public long Write(History.History h, Incident inc, IncidentEventSet ev, int maxRows = MaxEventRows, bool full = true, long atMs = 0, bool must = true, bool chartsAlone = false)
    {
        _history = h;
        _plainFor = null;
        var st = StateOf(inc);
        st.EventsDone = false; // until this write has written events.csv (EventsWritten)
        st.ReportsDone = false; // and its two reports (ReportsWritten)
        // What this write leaves of the chart and the two CSVs, and what it cannot write of them, is said of this
        // write alone.
        st.ChartLeft = [];
        st.ChartFailure = null;
        var w = inc.Window;
        int from = w is null ? 0 : w.FromIndex(h);
        // The first sample after the window's end (a closed incident's window ends at its close).
        int after = w is null ? h.Count : inc.ClosedUtc is { } closed ? w.AfterIndex(h, closed, w.ToMs is null ? null : inc.ClosedMs) : w.AfterIndex(h);
        int to = Math.Min(Math.Max(from, after - 1), h.Count - 1);
        // The samples a full write makes the chart and the two CSVs from, the first and the last by their place in the
        // history's own count (none: the history holds no sample from the window's start on); and, while the history
        // still holds the window's first sample, that sample's place. That place is kept before anything is written:
        // a write that is refused its folder (below) still says where the window's samples begin, for the tries that
        // follow it. (Kept only by a write that got past its folder, a closing write refused the folder for nearly 7
        // minutes left a long incident's timeline.csv and processes.csv stopping 20 minutes before its close: the
        // try that could write them counted the samples they hold from before the window too, and so found fewer
        // to write than they held.)
        var (runFirst, runLast) = from <= to ? (h.SeqAt(from), h.SeqAt(to)) : (0L, -1L);
        if (w?.FromMs is { } startMs && from < h.Count && h.Ms[0] <= startMs) st.WindowStart = (startMs, h.SeqAt(from));
        Directory.CreateDirectory(_host.CheckWrite(inc.Dir));

        bool png = File.Exists(Path.Combine(inc.Dir, "timeline.png"));
        long bytes = 0;
        if (full)
        {
            // Once the history has stopped reaching back to the start of a closed incident's window, each of the
            // three is made only as Leaves allows: by the window's samples the history still holds (up to the
            // close), counted against those the file holds, from the window's first sample (as a write last found
            // it) to its last. Without the first sample's place, every sample the file was written from counts as
            // the window's: the file is then left rather than made from fewer. (The window is measured from the
            // history just before the closing write, which so finds the window's first sample in it; a closing
            // write that does not, because the window could not be measured then, makes the files by the same rule.)
            long? leftFor = w?.FromMs is { } windowFrom && h.Count > 0 && h.Ms[0] > windowFrom ? inc.ClosedMs : null;
            long have = Math.Max(0, after - from);
            long first = st.WindowStart is { } start && start.FromMs == w?.FromMs ? start.Seq : long.MinValue;
            long last = have > 0 ? h.SeqAt(after - 1) : -1;
            // A chart is said to be left only when the folder has one (png): a folder without a chart is whole.
            if (!Leaves(st.Chart, st, leftFor, have, first, last, named: png))
            {
                png = false;
                if (h.Count > 1 && to > from)
                {
                    try
                    {
                        (png, long pngBytes) = RenderChart(h, inc, from, to, Path.Combine(inc.Dir, "timeline.png"), st);
                        bytes += pngBytes;
                        if (png) st.Chart.Wrote(inc.ClosedMs, runFirst, runLast);
                    }
                    catch (Exception ex) { _host.Log($"incident {inc.Id}: chart failed: {ex.Message}"); }
                }
            }
            // timeline.csv, then processes.csv, each on its own: one that cannot be written (another program holds
            // it) does not keep the other, events.csv or the reports from being written. The first failure is kept
            // for the manager (ChartFailure), which tries the file again.
            bool both = true;
            foreach (var (file, write) in new (ChartFile, Func<long>)[]
                     {
                         (st.Timeline, () => WriteTimelineCsv(h, inc, from, to, st)), (st.Processes, () => WriteProcessesCsv(h, inc, from, to, st)),
                     })
            {
                if (Leaves(file, st, leftFor, have, first, last)) { both = false; continue; }
                try
                {
                    bytes += write();
                    file.Wrote(inc.ClosedMs, runFirst, runLast);
                }
                catch (Exception ex)
                {
                    st.ChartFailure ??= ex;
                    both = false;
                }
            }
            if (both) st.FullMs = atMs;
        }
        // A try for the chart files alone ends here when report.md shows the folder as it is now: the reports
        // stay as the write before made them.
        if (chartsAlone && st.Shown == Shows.Of(inc, png)) return bytes;
        var info = _knowledge.Lookup(inc.Tag);
        var details = inc.Suspects.Take(5).ToDictionary(s => s.Key, s => _host.DescribeFile(s.ImagePath));
        _sessions = inc.Suspects.Take(5).ToDictionary(s => s.Key, s => _host.DescribeSession(s));
        var system = _host.GetSystemInfo();
        bytes += WriteEventsCsv(inc, ev, st, maxRows, must);
        ReadThen(inc, st);
        var report = BuildJson(inc, info, details, system, ev.Window, st);
        bytes += Atomic(Path.Combine(inc.Dir, "report.json"), JsonSerializer.Serialize(report, Json), st);
        // What report.md shows of the folder, which is as this write leaves it by now (report.md is its last file).
        var shows = Shows.Of(inc, png);
        bytes += Atomic(Path.Combine(inc.Dir, "report.md"), BuildMarkdown(inc, info, details, system, ev, shows, st), st);
        (st.Shown, st.ReportsDone) = (shows, true);
        return bytes;
    }

    /// <summary>
    /// Whether a full write leaves one of a closed incident's chart files (the chart, timeline.csv, processes.csv) as
    /// it is. The three are made from the history's samples of the incident's window, and the history lets its
    /// oldest samples go (it keeps <see cref="Settings.RawHistoryMinutes"/>). A write makes the file while the
    /// incident is open, and while the history still holds the window's first sample
    /// (<paramref name="closedMs"/> null). After that (<paramref name="closedMs"/>: the close as it stands):
    /// a file written for that close stays as that write made it. (Before, a note, a release or an alert counted on
    /// a closed incident's timeline hours later made timeline.csv again as one row, of a sample hours after the
    /// incident, and the report lost its chart.)
    /// A file never written for that close (the write of it failed: another program held it, or the folder could not
    /// be written; or the chart could not be drawn) is made only when this write holds at least as many of the
    /// window's samples (<paramref name="have"/>, up to the close) as the file holds now, and at least one: a file
    /// that stops before the close is then replaced by one that reaches it. A write that holds fewer, or none,
    /// leaves the file, and its name is kept (<see cref="FileState.ChartLeft"/>): no later try makes it, as the
    /// history only lets more of the window's samples go. (Before, a try 71 minutes after the close replaced a
    /// timeline.csv of the window's 23 rows with one row of a sample from after the incident, and again every few
    /// minutes for as long as processes.csv was held. And a rule that left such a file whenever the history no
    /// longer held the window's first sample could leave an incident open 80 minutes, whose window starts at the
    /// history's oldest sample, with a timeline.csv that stopped 20 minutes before its close for good, after another
    /// program had held the file for 30 seconds: the history lets its oldest samples go 32 at a time.)
    /// A chart the folder does not have is left without its name being kept (<paramref name="named"/> false): a
    /// folder without a chart is whole (none is drawn of a window with fewer than two samples, or when the chart
    /// cannot be drawn, which is no failed write), so the log does not say of it that it stays as it is. (Kept,
    /// the log said of a folder whose chart could not be drawn, once another program had held its report.md for
    /// 80 minutes, that it was written again "but for its chart, which stays as it is": it never had one.)
    /// </summary>
    /// <param name="first">The window's first sample by its place in the history's own count, as a write last found
    /// it while the history held it (<see cref="FileState.WindowStart"/>); <see cref="long.MinValue"/> when none did.</param>
    /// <param name="last">The window's last sample (the last one at or before the close) by its place in that count.</param>
    /// <param name="named">Whether the file's name is kept when it is left though never written for the close: false for a chart the folder does not have.</param>
    private static bool Leaves(ChartFile f, FileState st, long? closedMs, long have, long first, long last, bool named = true)
    {
        if (closedMs is null) return false;
        if (f.ClosedMs == closedMs) return true;
        if (have > 0 && have >= f.Within(first, last)) return false;
        if (named) st.ChartLeft.Add(f.Name);
        return true;
    }

    /// <summary>
    /// The incident's summary as its report.json would say it if written now, from memory: the fields the window's details
    /// and Copy summary read (<see cref="CopyText.IncidentSummary"/>: id, kind, tag, headline, window, plain answer, the top
    /// five suspects with their evidence and command lines) and Report an issue's digest (<see cref="Report.IssueReport.IncidentDigest"/>:
    /// the suspects' images too), each made by the same code as report.json's (<see cref="Headline"/>, <see cref="PlainOwners"/>,
    /// the incident's and suspects' own fields), so an open incident's details are never behind what PoolWatch knows (its
    /// report.json can be, on a busy PC, as the write budget holds refreshes back). <c>LiveDetailsTests</c> holds each of its
    /// fields to report.json's, written at the same moment, with a Claude Code session and a trace. Writes nothing. The
    /// caller holds the history lock, as for a write.
    /// </summary>
    public string SummaryJson(History.History h, Incident inc)
    {
        _history = h;
        _plainFor = null;
        var info = _knowledge.Lookup(inc.Tag);
        // The headline names the top suspect's Claude Code session, as a write gives it (read on demand, its search cached).
        _sessions = inc.Suspects.Take(1).ToDictionary(s => s.Key, s => _host.DescribeSession(s));
        var summary = new
        {
            id = inc.Id,
            kind = inc.Kind,
            tag = inc.Tag.ToString(),
            headline = Headline(inc, info),
            plainAnswer = PlainOwners(inc)?.Line,
            window = inc.Window,
            suspects = inc.Suspects.Take(5).Select(s => new
            {
                rank = s.Rank, pid = s.Key.Pid, image = s.Image, displayName = s.DisplayName, commandLine = s.CommandLine, confidence = s.Confidence, evidence = s.Evidence,
            }),
        };
        return JsonSerializer.Serialize(summary, Json);
    }

    /// <summary>
    /// Appends up to <paramref name="maxRows"/> of the kept events not written yet to events.csv alone (rows a write left for
    /// later), a write that can wait (it never replaces a closed incident's file with both ends); returns the bytes written.
    /// </summary>
    public long AppendEvents(Incident inc, IncidentEventSet ev, int maxRows)
    {
        Directory.CreateDirectory(_host.CheckWrite(inc.Dir));
        return WriteEventsCsv(inc, ev, StateOf(inc), maxRows, must: false);
    }

    /// <summary>
    /// The incident's events.csv with both ends of its window as a whole write would make it now (<see cref="WriteWhole"/>),
    /// and its report.md as a write would make it beside that file (its line under the window's events counting that file's
    /// rows and saying what it leaves out), from memory: what an export of an open incident holds in place of its folder's
    /// two, as its folder's events.csv holds the first part only while it is open. <paramref name="ev"/> is the incident's
    /// events for the reports. Writes nothing. The caller holds the history lock, as for a write.
    /// </summary>
    public (string EventsCsv, string ReportMd) ExportTexts(History.History h, Incident inc, IncidentEventSet ev)
    {
        var p = Plan(inc, ev, StateOf(inc), Path.Combine(inc.Dir, "events.csv"), ends: true);
        string csv = BothEndsRows(inc, ev, p, out var rows, out var keys).ToString();
        // That file as a whole write would leave this run's knowledge of it.
        var exported = new FileState
        {
            Events = keys, EventRows = rows.Count, First = Earliest(rows),
            From = ev.FromUtc is { } from ? (ev.FromMs, from) : null, LostToUtc = ev.LostToUtc, CarriedAtStart = ev.Carried,
            Gap = p.Gap.Count > 0 ? p.Gap : null, Tail = p.Tail.Count > 0 || p.Gap.Count > 0 ? p.Key : null, GapPastEndUtc = PastEnd(ev, p.Gap),
        };
        _history = h;
        _plainFor = null;
        var info = _knowledge.Lookup(inc.Tag);
        var details = inc.Suspects.Take(5).ToDictionary(s => s.Key, s => _host.DescribeFile(s.ImagePath));
        _sessions = inc.Suspects.Take(5).ToDictionary(s => s.Key, s => _host.DescribeSession(s));
        bool png = File.Exists(Path.Combine(inc.Dir, "timeline.png"));
        ReadThen(inc, exported);
        return (csv, BuildMarkdown(inc, info, details, _host.GetSystemInfo(), ev, Shows.Of(inc, png), exported));
    }

    /// <summary><see cref="AppendEvents(Incident, IncidentEventSet, int)"/> with <paramref name="events"/> as all the window's events there are.</summary>
    public long AppendEvents(Incident inc, List<LifecycleEvent> events, int maxRows) => AppendEvents(inc, IncidentEventSet.Of(events), maxRows);

    /// <summary>
    /// The incident in one line, the top suspect's confidence in the words of Pool causes' Confidence column
    /// (<see cref="PlainWords.Confidence"/>), as everywhere in report.md: "...; top suspect Leaker.exe PID 7716, started
    /// 03:46:08 (strong evidence)". A report an earlier build wrote keeps its older words ("(strongly correlated)"): every
    /// reader takes the headline as written, and reads a suspect's confidence only from report.json's <c>confidence</c> name.
    /// An incident for the whole non-paged pool over its limit says "Non-paged pool over 5.0 GB and rising; ..." while
    /// it is open and "Non-paged pool rose over 5.0 GB; ..." once it is closed, each with the limit its alert is about
    /// (<see cref="NonPagedLimit"/>). An incident made for a trace of its own has no span until its trace is attached
    /// (it is made closed, its start and its end one moment): until then its headline is "`File` (...): its trace
    /// had not finished by 07:05:20", the time the newest sample's at that write (<see cref="NoTraceSpan"/>), with no
    /// word of a growth or of suspects. And of one whose trace recorded nothing PoolWatch could use, which leaves
    /// it without a span for good: "`File` (...): its trace recorded nothing PoolWatch could use". (Before, both
    /// said "`File` (...) grew 0 B in 0 s; no suspect stood out", or named a top suspect, which reads as what a
    /// trace found.)
    /// </summary>
    /// <param name="markdown">Escape free text for report.md (report.json keeps the raw headline).</param>
    public string Headline(Incident inc, TagInfo info, bool markdown = false)
    {
        string owner = markdown ? Md(Owner(info)) : Owner(info);
        string tag = $"`{inc.Tag.ToDisplay()}` ({owner})";
        if (NoTraceSpan(inc) is { } why) return $"{tag}: {why}";
        string what = inc.Kind switch
        {
            IncidentKind.Release => inc.Release is { } r
                ? $"{tag} dropped {Fmt.Bytes(r.Dropped)} at {Fmt.Clock(r.TimeUtc)}"
                : $"{tag} dropped sharply",
            // "and rising" only while the incident is open: a closed incident's report is written again hours after its
            // close (a note, an alert counted on its timeline), when the pool may long have fallen, so its headline says
            // what the pool did, in the past.
            IncidentKind.NonPagedTotal => (inc.IsOpen ? $"Non-paged pool over {NonPagedLimit(inc)} and rising" : $"Non-paged pool rose over {NonPagedLimit(inc)}") +
                                          $"; fastest-growing tag {tag} grew {Fmt.Bytes(inc.Growth)} in {Fmt.Duration(inc.GrowthSpan)}",
            _ => $"{tag} grew {Fmt.Bytes(inc.Growth)} in {Fmt.Duration(inc.GrowthSpan)}",
        };
        if (inc.TraceOwners is { Count: > 0 } to)
            return $"{what}; the confirmation trace shows {(markdown ? Md(to[0].Owner.Name) : to[0].Owner.Name)} allocating {Fmt.Bytes(to[0].Net)} of it";
        var top = inc.Suspects.FirstOrDefault();
        if (top is null) return what + "; no suspect stood out";
        string M(string s) => markdown ? Md(s) : s;
        var parts = new List<string> { $"{M(top.DisplayName)} PID {top.Key.Pid}" };
        if (top.IsOrphan) parts.Add("orphaned");
        if (top.StartUtc > DateTime.MinValue) parts.Add($"started {Fmt.Clock(top.StartUtc)}");
        if (_sessions.GetValueOrDefault(top.Key)?.Transcript is { } t)
            parts[^1] += $" by Claude Code session {M(Short(t.SessionId))}" + (t.WorkflowId is { } wf ? $" / workflow {M(wf)}" : "") + (t.SubagentId is { } sa && t.WorkflowId is null ? $" / sub-agent {M(Short(sa))}" : "");
        return $"{what}; top suspect {string.Join(", ", parts)} ({PlainWords.Confidence(top.Confidence)})";
    }

    private static string Short(string id) => id.Length > 8 ? id[..8] : id;

    /// <summary>
    /// The limit an incident for the whole non-paged pool is about, as its headline names it: the limit its alert
    /// carries (<see cref="Alert.LimitBytes"/>: the one set when the alert fired, which the pool was over then), "5.0
    /// GB"; "its limit" for an alert that carries none. True whenever the headline is written: the pool was over that
    /// limit when the alert fired, whatever it has done since. (Before, the headline gave the alert's
    /// <see cref="Alert.Bytes"/> there, which is the fastest-growing tag's rise in 10 minutes: "Non-paged pool over 100
    /// MB and rising" of a pool at 5.08 GB over a limit of 5.0 GB.)
    /// </summary>
    internal static string NonPagedLimit(Incident inc) => inc.Alerts.FirstOrDefault()?.LimitBytes is { } limit ? Fmt.Bytes(limit) : "its limit";

    private static string Owner(TagInfo info) =>
        string.IsNullOrEmpty(info.Description) ? info.Owner : $"{info.Owner} {info.Description}";

    private object BuildJson(Incident inc, TagInfo info, Dictionary<ProcessKey, FileDetails?> details, SystemInfo system, List<LifecycleEvent> events, FileState st) => new
    {
        notice = Exporter.UnredactedNotice, // The file as written is not for sharing as it is
        id = inc.Id,
        kind = inc.Kind,
        traceFor = inc.TraceFor, // what started the trace an incident of kind Capture was made for (null for another kind)
        tag = inc.Tag.ToString(),
        tagHex = inc.Tag.Raw.ToString("x8"),
        owner = info.Owner,
        description = info.Description,
        ownerSource = info.Source,
        fileSystemTag = _knowledge.IsFileSystemTag(inc.Tag),
        headline = Headline(inc, info),
        plainAnswer = PlainOwners(inc, st)?.Line,
        allocationShape = st.Then.Shape is { Rates: { During: var sh, Before: var before } } ? new
        {
            allocsPerMin = Math.Round(sh.AllocsPerMin, 1), freesPerMin = Math.Round(sh.FreesPerMin, 1), shape = sh.Shape.ToString(), text = sh.Text,
            before = before is { } b ? new { allocsPerMin = Math.Round(b.AllocsPerMin, 1), freesPerMin = Math.Round(b.FreesPerMin, 1) } : null,
        } : null,
        systemAtEnd = st.Then.System is { Counters: var sc, TimeUtc: var scAt } && sc.CommitLimitBytes > 0 ? new { timeUtc = scAt, commitBytes = sc.CommitBytes, commitLimitBytes = sc.CommitLimitBytes, availableBytes = sc.AvailableBytes, compressionWsBytes = sc.CompressionWsBytes, handles = sc.Handles, processes = sc.Processes } : null,
        openedUtc = inc.OpenedUtc,
        closedUtc = inc.ClosedUtc,
        onsetUtc = inc.OnsetUtc,
        endUtc = inc.EndUtc,
        startBytes = inc.StartBytes,
        peakBytes = inc.PeakBytes,
        currentBytes = inc.CurrentBytes,
        currentAtUtc = inc.CurrentAtUtc, // the sample currentBytes was read at: a closed incident's is from its close (or from when it was found retained), whenever the report is written
        clearedBytes = inc.ClearedBytes,
        clearedAtUtc = inc.ClearedAtUtc,
        growthBytes = inc.Growth,
        window = inc.Window,
        alerts = inc.Alerts.Select(a => new { kind = a.Kind, timeUtc = a.TimeUtc, rule = a.Rule, bytes = a.Bytes, onsetUtc = a.OnsetUtc }),
        release = inc.Release,
        retention = inc.Retention,
        capture = inc.Capture is { } cap ? new
        {
            cap.StartUtc, cap.EndUtc, etl = EtlRef(inc, cap), tags = cap.Tags.Select(t => t.ToString()),
            cap.PoolEventsParsed, cap.Library, cap.PoolEvents, cap.StackedPoolEvents, cap.FileEvents, cap.KernelFileRecorded, cap.Error, cap.Notes,
            pool = cap.Pool.Take(30).Select(u => new { u.Pid, u.Image, tag = u.Tag.ToString(), u.AllocBytes, u.FreedBytes, u.NetBytes, u.Allocs, u.Frees, u.TopFrames }),
            files = cap.Files.Take(20),
            cpu = cap.Cpu is { } cp ? new
            {
                cp.Seconds, cp.Samples, cp.Processors, cp.ContextSwitches, cp.PoolBytes, summary = cp.Summary(),
                topCpu = cp.TopCpu, topDpcIsr = cp.TopDpcIsr, topWait = cp.TopWait,
                topFunctions = cp.TopFunctions, topWaitFunctions = cp.TopWaitFunctions, functionsNote = cp.FunctionsNote,
            } : null,
        } : null,
        traceAnswer = TraceAnswer(inc, info),
        traceOwners = inc.TraceOwners?.Take(10).Select(o => new
        {
            owner = o.Owner.Name, ownerKind = o.Owner.Kind.ToString(), netBytes = o.Net, o.Allocs, o.Frees, processes = o.Processes.Count,
            perShortCommandBytes = o.PerShortCommand, shortCommands = o.ShortOnes.Count, o.StartsInWindow,
            top = o.Processes.OrderByDescending(p => p.Usage.NetBytes).Take(10).Select(p => new
            {
                name = p.Name, pid = p.Usage.Pid, createTime = p.Usage.CreateTime, netBytes = p.Usage.NetBytes, p.Usage.Allocs, p.Usage.Frees,
                commandLine = p.CommandLine, chain = p.Chain, startUtc = p.StartUtc, exitUtc = p.ExitUtc, resolved = p.Resolved,
            }),
        }),
        timeline = inc.Timeline.Select(t => new { timeUtc = t.TimeUtc, text = t.Text }),
        reopened = inc.Reopened,
        suspects = inc.Suspects.Select(s => new
        {
            rank = s.Rank,
            pid = s.Key.Pid,
            createTime = s.Key.CreateTime,
            image = s.Image,
            displayName = s.DisplayName,
            imagePath = s.ImagePath,
            commandLine = s.CommandLine,
            user = s.User is null ? null : _host.DisplayUser(s.User),
            startUtc = s.StartUtc > DateTime.MinValue ? s.StartUtc : (DateTime?)null,
            exitUtc = s.ExitUtc,
            exitCode = s.ExitCode,
            isOrphan = s.IsOrphan,
            orphanedUtc = s.OrphanedUtc,
            services = s.Services,
            ancestry = s.Ancestry,
            score = Math.Round(s.Score, 3),
            confidence = s.Confidence,
            direct = s.Direct,
            activity = new
            {
                present = s.Activity.Present, counter = s.Activity.Counter, spearman = Round(s.Activity.Spearman), bestLag = s.Activity.BestLag,
                laggedSpearman = Round(s.Activity.LaggedSpearman), ioShare = Math.Round(s.Activity.IoShare, 4), opsDelta = s.Activity.OpsDelta,
            },
            lifecycle = new { s.Lifecycle.StartedBeforeOnset, s.Lifecycle.ExitedAtEnd, s.Lifecycle.OrphanActive, present = s.Lifecycle.Present },
            evidence = s.Evidence,
            file = details.GetValueOrDefault(s.Key),
            session = _sessions.GetValueOrDefault(s.Key),
        }),
        // Only the events that matter for the suspects (the full list is in events.csv).
        events = events.Where(e => e.Kind is not (LifecycleKind.Start or LifecycleKind.Exit) || inc.Suspects.Any(s => s.Key == e.Key)).TakeLast(200).Select(e => new { timeUtc = e.TimeUtc, kind = e.Kind, pid = e.Pid, image = e.Image, exitCode = e.ExitCode, detail = e.Detail }),
        system,
    };

    private static double? Round(double v) => double.IsNaN(v) ? null : Math.Round(v, 3);

    private string BuildMarkdown(Incident inc, TagInfo info, Dictionary<ProcessKey, FileDetails?> details, SystemInfo system, IncidentEventSet ev, Shows shows,
        FileState st)
    {
        var events = ev.Window;
        var sb = new StringBuilder();
        string headline = Headline(inc, info, markdown: true);
        sb.AppendLine($"# {headline}").AppendLine();
        sb.AppendLine($"> {Exporter.UnredactedNotice}").AppendLine(); // first, that the report is not redacted
        sb.AppendLine($"- Incident `{inc.Id}` ({inc.Kind}{(inc.IsOpen ? ", still open" : "")}), opened {Fmt.Stamp(inc.OpenedUtc)}" +
                      (inc.ClosedUtc is { } c ? $", closed {Fmt.Stamp(c)}" : ""));
        sb.AppendLine($"- Tag `{inc.Tag}` (raw 0x{inc.Tag.Raw:x8}; tags are case-sensitive): owner **{Md(info.Owner)}**" +
                      (info.Description.Length > 0 ? $", {Md(info.Description)}" : "") + $" (source: {info.Source})");
        if (inc.Kind != IncidentKind.Release)
            // "Still climbing" only of a tag that grew: one that did not since the start (an incident made for a trace of its
            // own, at its making and when its tag held still through the trace) has no end and is not climbing. A closed
            // incident's growth was measured up to its close, and its folder can be written again hours later: its line
            // says "when it closed" and gives the level with its sample's time, where an open one's says "at the last
            // sample" and "now" (Incident.LevelSaid). An incident made for a trace that has no span (its trace is not
            // attached yet, or recorded nothing) has no growth to give: its line says why, as its title does
            // (NoTraceSpan), and the level the tag held as the incident was last measured, with that sample's time.
            // (Before: "100 MB -> 100 MB (+0 B) from 07:04:50; 100 MB at 07:04:50", which reads as what the trace
            // found.)
            sb.AppendLine(NoTraceSpan(inc) is { } why
                ? $"- Growth: {why}" + (inc.CurrentAtUtc is { } at ? $"; the tag held {Fmt.Bytes(inc.CurrentBytes)} at {Fmt.Clock(at)}" : "")
                : $"- Growth: {Fmt.Bytes(inc.StartBytes)} -> {Fmt.Bytes(inc.PeakBytes)} (+{Fmt.Bytes(inc.Growth)}) from {Fmt.Clock(inc.OnsetUtc)}" +
                  (inc.EndUtc is { } e ? $" to {Fmt.Clock(e)}" : inc.Growth <= 0 ? "" : inc.IsOpen ? " (still climbing at the last sample)" : " (still climbing when it closed)") +
                  inc.LevelSaid(Fmt.Clock) + (inc.OwnClearsFreed is { } own ? $", after {own} of it" : ""));
        if (st.Then.Shape is { Rates: { During: { Shape: not AllocShape.Idle } shape, Before: var before }, Span: var span })
            sb.AppendLine($"- Allocation shape {span}: {shape.AllocsPerMin:N0} allocations/min, {shape.FreesPerMin:N0} frees/min" +
                          (before is { } b ? $", against {b.AllocsPerMin:N0} and {b.FreesPerMin:N0} a minute just before it" : "") + $": {shape.Text}");
        if (st.Then.System is { Counters: { CommitLimitBytes: > 0 } sys, TimeUtc: var sysAt })
            sb.AppendLine($"- System at {Fmt.Clock(sysAt)}: commit {Fmt.Bytes((long)sys.CommitBytes)} of {Fmt.Bytes((long)sys.CommitLimitBytes)}, {Fmt.Bytes((long)sys.AvailableBytes)} RAM available, " +
                          $"compression store {Fmt.Bytes((long)sys.CompressionWsBytes)}, {sys.Handles:N0} handles, {sys.Processes:N0} processes");
        foreach (var a in inc.Alerts)
            sb.AppendLine($"- Alert {Fmt.Clock(a.TimeUtc)}: {a.Rule} ({Fmt.SignedBytes(a.Kind == AlertKind.Release ? -a.Bytes : a.Bytes)}, onset {Fmt.Clock(a.OnsetUtc)})");
        if (inc.Retention is { } ret) sb.AppendLine($"- **Retained**: {ret.Note}.");
        if (inc.Release is { } rel)
            sb.AppendLine($"- **Released**: {Fmt.Bytes(rel.FromBytes)} -> {Fmt.Bytes(rel.ToBytes)} between {Fmt.Clock(rel.FromUtc)} and {Fmt.Clock(rel.TimeUtc)}" +
                          (rel.ClearedBytes > 0 ? $"; {Fmt.OwnClearsFreed(rel.ClearedBytes, rel.ClearedAtUtc)} of it and {Fmt.Bytes(rel.Dropped)} fell on its own." : "."));
        sb.AppendLine("- Sizes are the tag's own usage from SystemPoolTagInformation; Task Manager's committed pool figures run higher (fragmentation, lazy release).");
        sb.AppendLine();

        if (shows.ChartLine) sb.AppendLine("![timeline](timeline.png)").AppendLine();

        // In plain words: the trace when there is one, else the suspects grouped by who they work for.
        var owners = PlainOwners(inc, st);
        string? traced = TraceAnswer(inc, info);
        if (traced is not null || owners is not null)
        {
            sb.AppendLine("## In plain words").AppendLine();
            sb.AppendLine(MdText.Line(traced ?? owners!.Line)).AppendLine();
            if (inc.TraceOwners is { Count: > 0 } tos)
            {
                foreach (var o in tos.Take(3))
                    sb.AppendLine($"- **{Md(o.Owner.Name)}** ({PlainWords.Confidence(Confidence.ConfirmedByTrace)}): {Fmt.SignedBytes(o.Net)} net in {o.Processes.Count:N0} process(es), " +
                                  $"{o.Allocs:N0} allocations, {o.Frees:N0} frees" + (o.PerShortCommand > 0 ? $"; about {Fmt.Bytes(o.PerShortCommand)} kept per short command ({o.ShortOnes.Count:N0} traced)" : "") +
                                  (o.StartsInWindow > 0 ? $"; {o.StartsInWindow:N0} process starts during {SpanName(inc)}" : "") + ".");
                var corr = owners?.Owners.Where(o => o.Meaningful && !tos.Any(t => t.Owner.Key == o.Owner.Key)).Take(3).ToList() ?? [];
                if (corr.Count > 0)
                {
                    sb.AppendLine().AppendLine("Correlation only (no allocation in the trace):").AppendLine();
                    foreach (var o in corr) sb.AppendLine($"- {Md(o.Name)} ({PlainWords.Confidence(o.Confidence)}, correlation only): {Md(string.Join("; ", o.Plain))}");
                }
            }
            else if (owners is { } pw)
                foreach (var o in pw.Owners.Where(o => o.Meaningful).Take(3))
                    sb.AppendLine($"- **{Md(o.Name)}** ({PlainWords.Confidence(o.Confidence)}): {Md(string.Join("; ", o.Plain))}. {Md(o.Summary)}");
            sb.AppendLine();
        }

        if (inc.Timeline.Count > 1)
        {
            sb.AppendLine("## Timeline").AppendLine();
            foreach (var (t, text) in inc.Timeline.TakeLast(50)) sb.AppendLine($"- {Fmt.Clock(t)} {Md(text)}");
            if (inc.Timeline.Count > 50) sb.AppendLine($"- ... {inc.Timeline.Count - 50} earlier entries in report.json");
            sb.AppendLine();
        }

        sb.AppendLine(inc.TraceOwners is { Count: > 0 } ? "## Suspects by correlation (the trace above is the evidence)" : "## Suspects").AppendLine();
        sb.AppendLine("Ranked by every signal: direct charge (handles of the matching object type, quota pool), activity correlation (Spearman of the " +
                      "tag's growth against the process's I/O rate, plus its share of all I/O), lifecycle (start before onset, exit when growth " +
                      "stopped, orphaned while busy). Correlation is not causation: only a confirmation trace proves an allocation.").AppendLine();
        if (inc.Suspects.Count == 0) sb.AppendLine("No process stood out in the window.").AppendLine();
        else
        {
            sb.AppendLine("| # | Process | PID | Confidence | Score | Evidence |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var s in inc.Suspects)
                sb.AppendLine($"| {s.Rank} | {Md(s.DisplayName)}{(s.IsOrphan ? " (orphan)" : "")} | {s.Key.Pid} | {PlainWords.Confidence(s.Confidence)} | {s.Score:0.00} | {Md(string.Join("; ", s.Evidence))} |");
            sb.AppendLine();
        }

        if (inc.Release is { } r)
        {
            sb.AppendLine("## Release").AppendLine();
            // PoolWatch's own clears in the span freed part of it: that part is their result, the rest fell on its own.
            string fell = r.ClearedBytes > 0
                ? $"fell {Fmt.Bytes(r.Dropped)} on its own ({Fmt.Bytes(r.FromBytes)} -> {Fmt.Bytes(r.ToBytes)}, of which {Fmt.OwnClearsFreed(r.ClearedBytes, r.ClearedAtUtc)})"
                : $"fell {Fmt.Bytes(r.Dropped)} ({Fmt.Bytes(r.FromBytes)} -> {Fmt.Bytes(r.ToBytes)})";
            sb.AppendLine($"`{inc.Tag.ToDisplay()}` {fell} between {Fmt.Clock(r.FromUtc)} and {Fmt.Clock(r.TimeUtc)}. " +
                          "Whatever stopped or was deleted at that moment was holding the memory.").AppendLine();
            if (r.Exits.Count > 0) { sb.AppendLine("Process exits around the drop:").AppendLine(); foreach (var x in r.Exits) sb.AppendLine($"- {Md(x)}"); sb.AppendLine(); }
            if (r.WorktreeNote is { } wn) sb.AppendLine($"**Git worktree sprawl:** {Md(wn)}").AppendLine();
            if (r.ActiveProcesses.Count > 0) { sb.AppendLine("Busiest processes during the drop (mass file deletes show up as I/O):").AppendLine(); foreach (var x in r.ActiveProcesses) sb.AppendLine($"- {Md(x)}"); sb.AppendLine(); }
        }

        if (inc.Capture is { } capture) AppendCapture(sb, inc, capture);

        sb.AppendLine("## Process details").AppendLine();
        foreach (var s in inc.Suspects.Take(5))
        {
            sb.AppendLine($"### {s.Rank}. {Md(s.DisplayName)} (PID {s.Key.Pid})").AppendLine();
            sb.AppendLine($"- Executable: `{MdCode(s.ImagePath ?? s.Image)}`");
            if (details.GetValueOrDefault(s.Key) is { } f)
            {
                sb.AppendLine($"- File version: {Md(f.FileVersion ?? "n/a")}; company: {Md(f.Company ?? "n/a")}; product: {Md(f.Product ?? "n/a")}");
                sb.AppendLine($"- Signature: {Md(f.SignatureStatus)}{(f.Signer is not null ? $", signer {Md(f.Signer)}" : "")}");
            }
            sb.AppendLine($"- Command line: `{MdCode(s.CommandLine ?? "(not captured)")}`");
            if (s.User is not null) sb.AppendLine($"- User: {Md(_host.DisplayUser(s.User))}");
            sb.AppendLine($"- Parent chain: {Md(string.Join(" › ", s.Ancestry))}");
            if (_sessions.GetValueOrDefault(s.Key) is { } sd)
            {
                sb.AppendLine($"- AICC / Claude Code: {Md(sd.Chain)}");
                if (sd.Context?.ConfigDir is { } cfg) sb.AppendLine($"- Claude config: `{MdCode(cfg)}`");
                if (sd.Transcript is { } t)
                    sb.AppendLine($"- Issued by Claude Code session `{MdCode(t.SessionId)}` in project `{MdCode(t.Project)}`" +
                                  (t.WorkflowId is { } wf ? $", workflow `{MdCode(wf)}`" : "") + (t.SubagentId is { } sa ? $", sub-agent `{MdCode(sa)}`" : "") +
                                  (t.TimestampUtc is { } ts ? $" at {Fmt.Stamp(ts)}" : "") + $" (`{MdCode(t.Command)}` in `{MdCode(Path.GetFileName(t.File))}`)");
                else if (sd.SearchPending) sb.AppendLine("- Claude Code transcript search: in progress (the report is rewritten when it finishes)");
                else sb.AppendLine("- Claude Code transcript search: no matching tool call found within the search window");
            }
            if (s.Services.Count > 0) sb.AppendLine($"- Services: {Md(string.Join(", ", s.Services))}");
            sb.AppendLine($"- Started: {(s.StartUtc > DateTime.MinValue ? Fmt.Stamp(s.StartUtc) : "unknown")}" +
                          (s.ExitUtc is { } x ? $"; exited {Fmt.Stamp(x)}{(s.ExitCode is { } code ? $" (code {code})" : "")}" : StillRunning(inc, s)) +
                          (s.OrphanedUtc is { } o ? $"; orphaned {Fmt.Stamp(o)}" : ""));
            sb.AppendLine();
        }

        sb.AppendLine("## Events in the window").AppendLine();
        var shown = events.Where(e => e.Kind != LifecycleKind.Start || inc.Suspects.Any(s => s.Key == e.Key)).Take(50).ToList();
        if (shown.Count == 0) sb.AppendLine("None recorded.");
        foreach (var e in shown)
            sb.AppendLine($"- {Fmt.Clock(e.TimeUtc)} {e.Kind} {Md(e.Image)} (PID {e.Pid}){(e.ExitCode is { } c2 ? $" code {c2}" : "")}{(e.Detail is { } d ? $": {Md(d)}" : "")}");
        // The line under them counts events in events.csv only when the folder holds it: a closed incident's file that
        // is to hold both ends, once gone from the folder (deleted by hand, say), is made again only by a write that
        // must be made, so a write that can wait writes this report without it.
        sb.AppendLine(shows.Events ? OtherEventsLine(inc, ev, shown, st) : "\n(events.csv is not in the folder as this report is written.)").AppendLine();

        AppendSystem(sb, system);
        AppendBugReport(sb, inc, info, details, system, headline, shows);
        return sb.ToString();
    }

    /// <summary>
    /// What a suspect's "Started" line says of a process PoolWatch has read no exit of. While the incident is open,
    /// "; still running": its suspects are as they were last ranked, before its refreshes are written and at least
    /// every 10 minutes. A closed incident's are as they were ranked at its close (or as its trace was attached,
    /// after it), and its folder is written again hours later (an alert counted after it closed, a late event, a
    /// note), when the process may long have exited: "; still running when it closed". Nothing for a process PoolWatch
    /// held no record of when it was ranked (<see cref="Suspect.Unresolved"/>): whether it still ran is not known.
    /// </summary>
    internal static string StillRunning(Incident inc, Suspect s) =>
        s.Unresolved ? "" : inc.IsOpen ? "; still running" : "; still running when it closed";

    /// <summary>
    /// report.md's line under the window's events: how many more events.csv holds than those listed (its rows counted as
    /// written, those listed among them aside), and what it leaves out: when it holds both ends, how many
    /// events it leaves out between them and between which times, as its <c>Gap</c> row says; while the incident is open and
    /// its file has its <c>Note</c> row, how many it holds, to when, how many more have come since (every event the
    /// incident took in from the file's start on, less the file's rows: those after the window's end as it is measured
    /// now too, which lags behind while the growth goes on and stands still in its quiet minutes), and what its close
    /// writes; while the write budget holds rows back, what the file gets and when (an open incident's: more of its first
    /// part as the budget allows, or, once its first part has followed the window's start, that the file starts again
    /// from it; both ends at its close when it has more events than that). A file written whole starts where the
    /// window did when it was written; an open incident's stays as written while its first part follows the window's
    /// start, until it starts again or the incident closes: where the file's rows are not from where the window starts
    /// now, the line says where they are from and where the window (the events listed above it) starts now, in every
    /// branch (<see cref="StartSaid"/>), and calls those rows the window's first events only while they are
    /// (<see cref="AreWindowsFirst"/>). Empty when there is nothing more to say. Starts with a line break, for the blank
    /// line before it.
    /// </summary>
    private string OtherEventsLine(Incident inc, IncidentEventSet ev, List<LifecycleEvent> shown, FileState st)
    {
        var c = CultureInfo.CurrentCulture;
        int inFile = st.Events is { } written ? Math.Max(0, st.EventRows - shown.Count(e => written.Contains(RowKey(e)))) : 0;
        bool windowsFirst = AreWindowsFirst(inc, st);
        if (st.Gap is { } g)
            return $"\n({inFile} other events in events.csv; it leaves out {g.Count:N0} events, between {Fmt.Clock(g.FromUtc)} and {Fmt.Clock(g.ToUtc)}, as its Gap row says" +
                   (st.GapPastEndUtc is { } end ? PastEndSaid(end, Fmt.Clock, " when the file was written") : "") + $"{StartSaid(inc, st, from: true, g)})";
        if (inc.IsOpen && st.Tail is null && st.NotedAt is { } noted)
        {
            // Every event the incident took in from the file's start on that is not one of the file's rows (a late event
            // that took a place in the first part since the file's last row counts as one: the one it moved on does). The
            // count goes on across the first part's follows (IncidentEventSet.TakenIn), as the file stays as written.
            long since = Math.Max(0, ev.TakenIn - st.CarriedAtStart - st.EventRows);
            return $"\n({inFile} other events in events.csv: while the incident is open it holds " +
                   (windowsFirst ? string.Create(c, $"the window's first {st.EventRows:N0} events") : FirstPartSaid(st.From?.Utc, st.LostToUtc, st.EventRows, Fmt.Clock, c)) +
                   $", to {Fmt.Clock(noted)}, as PoolWatch had them when it wrote them; {since:N0} more have come since, and {AtClose(c, "")}{StartSaid(inc, st, from: false)})";
        }
        // The write budget kept rows of the first part for later.
        int more = ev.Window.Count - shown.Count;
        if (st.Pending && more > 0)
        {
            // With Detailed recording on for the whole window the day's data\<yyyyMMdd>\events.csv has every one; in Reports
            // only there is no such file, and the line names none; after a switch in the window, it has those of the time
            // Detailed recording was on.
            string day = DayEventsFrom(ev.Window[0].TimeUtc) switch
            {
                DayCopy.Every => ", and the day's `data\\<yyyyMMdd>\\events.csv` has every one",
                DayCopy.Part => ", and the day's `data\\<yyyyMMdd>\\events.csv` has those of the time Detailed recording was on",
                _ => "",
            };
            if (!inc.IsOpen) return $"\n({more} other events in the window; events.csv gets the rest when PoolWatch's write budget has room{day}{StartSaid(inc, st, from: true)})";
            // The incident's first part followed the window's start since the file was begun: the file starts again from
            // it at its next write of rows (said by the times the incident's first part is from now).
            string gets = st.StartsAgain ? $"starts again with the first events {FromSaid(ev.FromUtc!.Value, ev.LostToUtc, Fmt.Clock)}"
                : "gets more of " + (windowsFirst ? "the window's first events" : $"the first events {FromSaid(st.From!.Value.Utc, st.LostToUtc, Fmt.Clock)}");
            return $"\n({more} other events in the window; events.csv {gets}, up to {MaxEventRows:N0} in all, as PoolWatch's write budget allows, " +
                   $"and {AtClose(c, " with more events than that,")}{day}{StartSaid(inc, st, from: st.StartsAgain)})";
        }
        string start = st.EventRows > 0 ? StartSaid(inc, st, from: true) : "";
        return inFile > 0 || start.Length > 0 ? $"\n({inFile} other events in events.csv{start})" : "";
    }

    /// <summary>Where the incident's window starts as it is measured now, as the report gives it (<c>IncidentManager.EventWindowStartUtc</c>): 2 minutes before its onset's time of day; a trace's own incident's at its start.</summary>
    private static DateTime WindowFromUtc(Incident inc) => IncidentManager.EventWindowStartUtc(inc);

    /// <summary>A time as report.md writes it (<see cref="Fmt.Clock"/>): to the second.</summary>
    private static DateTime ToSecond(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, t.Kind);

    /// <summary>
    /// Whether the file's first part is the window's first events as the window is measured now: the file was begun (or
    /// written whole) with its first part starting where the window starts now, on the clock that only goes forward, and
    /// PoolWatch held every event from there then (a file of events given as all there are counts as such). Only then
    /// does a text call them "the window's first".
    /// </summary>
    private static bool AreWindowsFirst(Incident inc, FileState st) =>
        st.From is not { } began || (IncidentManager.EventWindowStartMs(inc) == began.Ms && st.LostToUtc is null);

    /// <summary>
    /// From where a first part's events are, in the words that follow "the first 500 events": "from 06:01:40"; and when
    /// PoolWatch no longer held every event from there as that part was taken (<paramref name="lostTo"/>,
    /// <see cref="IncidentEventSet.LostToUtc"/>): "PoolWatch still held from 06:05:20 (by then it had already let go of
    /// events up to 06:13:19)", so no text gives the start as where the file's rows begin when they begin later.
    /// <paramref name="where"/>: what that time is, in a text that can say so (<see cref="WhenWritten"/>).
    /// </summary>
    private static string FromSaid(DateTime from, DateTime? lostTo, Func<DateTime, string> time, string where = "") => lostTo is { } gone
        ? $"PoolWatch still held from {time(from)}{where} (by then it had already let go of events up to {time(gone)})"
        : $"from {time(from)}{where}";

    /// <summary>
    /// What the time a file's first part starts at is, in a row of a file written whole: where the incident's window
    /// started then (the first part follows the window's start at once for such a write: <c>IncidentManager.Follow</c>).
    /// A row stays as written, so it says of when.
    /// </summary>
    private const string WhenWritten = ", where the incident's window started when this file was written";

    /// <summary>
    /// A first part in words, <paramref name="count"/> events of it: "the first 500 events from 06:01:40"
    /// (<see cref="FromSaid"/>); for events given as all there are (no start of their own, <paramref name="from"/> null),
    /// "the incident window's first 500 events". <paramref name="time"/> writes the time as the text it goes into does.
    /// </summary>
    private static string FirstPartSaid(DateTime? from, DateTime? lostTo, int count, Func<DateTime, string> time, IFormatProvider c, string where = "") => from is { } f
        ? string.Create(c, $"the first {count:N0} events {FromSaid(f, lostTo, time, where)}")
        : string.Create(c, $"the incident window's first {count:N0} events");

    /// <summary>
    /// What an open incident's close does with its events.csv, in words: replaces it whole with both ends of the window as
    /// it is measured then (the first part follows the window's start; the latest part may hold fewer, and a <c>Gap</c>
    /// row is there only when events were left out); <paramref name="when"/> says of which close (one with more events
    /// than the first part holds, for a file that has no Note row yet).
    /// </summary>
    private static string AtClose(IFormatProvider c, string when) => string.Create(c,
        $"when the incident closes{when} PoolWatch replaces the file with both ends of its window as it is measured then, its first events and its latest, up to {MaxEventRows:N0} of each, with a Gap row between them when it leaves any out");

    /// <summary>
    /// report.md's words for what the file's events are when they are not simply the window's as it is measured now: the
    /// window's start is not where the file's first part starts (<see cref="FileState.From"/>: an open incident's file
    /// stays as written while its first part follows the window's start, and so does a file that is final or whose
    /// write failed), judged on the times of day the line gives, to the second, so it never names one time twice as two;
    /// or PoolWatch no longer held every event from there as the file's first part was taken
    /// (<see cref="FileState.LostToUtc"/>); or the file holds its latest part alone; "" otherwise. With
    /// <paramref name="from"/> (a branch that has not said so, of a file that holds any event), first where the file's
    /// events are from (<see cref="FromSaid"/>), or, of a file that holds its latest part alone (no row before its
    /// <c>Gap</c> row), that it holds only those and where its first part would have started. Then, when the window
    /// starts somewhere else now, where; then, when that is later, that the file's events from before then are not the
    /// window's (when it has any), nor the first of those it leaves out (<paramref name="gap"/>, when they begin before
    /// then), and when it is earlier, that the window's events before the file's start are not in the file.
    /// </summary>
    private static string StartSaid(Incident inc, FileState st, bool from, EventGap gap = default)
    {
        if (st.From is not { } began) return "";
        var window = ToSecond(WindowFromUtc(inc));
        int moved = Math.Sign(window.CompareTo(ToSecond(began.Utc)));
        // Every event row is of its latest part (what it holds of it, FileState.Tail), after events it leaves out: it has
        // no first part (only a file written whole has such a row, and its branch has not said where its events are from).
        bool latestOnly = st.EventRows > 0 && st.Gap is not null && st.Tail is { } latest && latest.Rows >= st.EventRows;
        if (moved == 0 && st.LostToUtc is null && !latestOnly) return "";
        string first = Fmt.Clock(began.Utc), said = "";
        if (latestOnly)
            said = string.Create(CultureInfo.CurrentCulture, $"; it holds only the latest {st.EventRows:N0} events (its first part would have started at {first})");
        else if (from && st.EventRows > 0)
            said = st.LostToUtc is null ? $"; it holds events from {first} on" : $"; it holds the events {FromSaid(began.Utc, st.LostToUtc, Fmt.Clock)}";
        if (moved == 0) return said;
        // "Before then": before the second the line gives as the window's start.
        said += $"; the incident's window now starts at {Fmt.Clock(window)}";
        if (moved < 0) return said + $", and its events before {first} are not in the file";
        if (st.First is not { } earliest || earliest.Utc >= window) return said;
        return said + ": the file's events from before then are not the window's" + (gap.Count > 0 && gap.FromUtc < window ? ", nor are the first of those it leaves out" : "");
    }

    private static void AppendCapture(StringBuilder sb, Incident inc, CaptureResult c)
    {
        sb.AppendLine("## Confirmation trace").AppendLine();
        if (c.Error is not null) sb.AppendLine($"The trace failed: {Md(c.Error)}").AppendLine();
        if (c.EtlPath is not null)
            sb.AppendLine($"- Recorded {Fmt.Clock(c.StartUtc)}-{Fmt.Clock(c.EndUtc)} ({Fmt.Duration(c.Duration)}) with wpr.exe; saved as `{Path.GetFileName(c.EtlPath)}`; " +
                          $"tags {string.Join(", ", c.Tags.Select(t => $"`{t.ToDisplay()}`"))}.");
        if (c.PoolEventsParsed)
            sb.AppendLine($"- Parsed with {c.Library}: {c.PoolEvents:N0} pool events ({c.StackedPoolEvents:N0} with stacks), " +
                          (c.KernelFileRecorded ? $"{c.FileEvents:N0} Kernel-File events." : "Kernel-File not recorded."));
        foreach (var n in c.Notes) sb.AppendLine($"- {Md(n)}");
        sb.AppendLine();
        if (c.PoolEventsParsed && inc.Traced is { Count: > 0 } traced)
        {
            // Every traced process with who it was: identity, command line, parent chain and owner (PID + create time).
            sb.AppendLine($"Net `{inc.Tag.ToDisplay()}` bytes each process allocated during the trace (allocations minus frees of those allocations):").AppendLine();
            sb.AppendLine("| Process | PID | Started | Owner | Net | Allocs | Frees | Command line | Parent chain |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
            foreach (var t in traced.OrderByDescending(t => t.Usage.NetBytes).Take(30))
                sb.AppendLine($"| {Md(t.Name)} | {t.Usage.Pid} | {(t.StartUtc is { } st ? Fmt.Clock(st) : "before the trace")} | {Md(t.Identity.Owner.Name)} | " +
                              $"{Fmt.SignedBytes(t.Usage.NetBytes)} | {t.Usage.Allocs:N0} | {t.Usage.Frees:N0} | `{MdText.CodeCell(Short(t.CommandLine ?? "(not captured)", 120))}` | {Md(string.Join(" › ", t.Chain))} |");
            if (traced.Count > 30) sb.AppendLine($"\n({traced.Count - 30:N0} more processes in report.json)");
            sb.AppendLine();
            var others = c.Pool.Where(u => u.Tag != inc.Tag).Take(10).ToList();
            if (others.Count > 0)
            {
                sb.AppendLine("Other traced tags:").AppendLine();
                foreach (var u in others) sb.AppendLine($"- `{u.Tag.ToDisplay()}` {Md(u.Image)} (PID {u.Pid}): {Fmt.SignedBytes(u.NetBytes)}");
                sb.AppendLine();
            }
        }
        else if (c.PoolEventsParsed && c.Pool.Count > 0)
        {
            sb.AppendLine("Net pool bytes allocated during the trace (allocations minus frees of those allocations), by process and tag:").AppendLine();
            sb.AppendLine("| Process | PID | Tag | Net | Allocs | Frees | Top allocating modules |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var u in c.Pool.Take(15))
                sb.AppendLine($"| {Md(u.Image)} | {u.Pid} | `{u.Tag.ToDisplay()}` | {Fmt.SignedBytes(u.NetBytes)} | {u.Allocs:N0} | {u.Frees:N0} | " +
                              $"{Md(string.Join(", ", u.TopFrames.Take(3).Select(f => $"{f.Frame} {Fmt.Bytes(f.Bytes)}")))} |");
            sb.AppendLine();
        }
        else if (c.EtlPath is not null)
        {
            sb.AppendLine("Pool events could not be read automatically. To inspect them in Windows Performance Analyzer:").AppendLine();
            sb.AppendLine($"1. Open `{Path.GetFileName(c.EtlPath)}` in WPA (Windows Performance Toolkit).");
            sb.AppendLine("2. Graph Explorer > Memory > Pool Graphs > drag \"Outstanding Size by Paged, Tag\" (or \"Pool Summary\") into the analysis view.");
            sb.AppendLine($"3. Filter Tag to {string.Join(" / ", c.Tags.Select(t => t.ToDisplay()))}, group by Process then Stack; load symbols (Trace > Load Symbols) for driver frames.");
            sb.AppendLine("4. The Outstanding Size column is the net allocation per process and call stack.").AppendLine();
        }
        if (c.Cpu is { } cpu)
        {
            sb.AppendLine("### CPU during the trace").AppendLine();
            sb.AppendLine(MdText.Line(cpu.Summary())).AppendLine();
            sb.AppendLine("| Module | CPU (ms) | Share | | Waited in | Wait (ms) | Switches |").AppendLine("|---|---:|---:|---|---|---:|---:|");
            for (int i = 0; i < Math.Max(Math.Min(10, cpu.TopCpu.Count), Math.Min(10, cpu.TopWait.Count)); i++)
            {
                var m = i < cpu.TopCpu.Count ? cpu.TopCpu[i] : null;
                var w = i < cpu.TopWait.Count ? cpu.TopWait[i] : null;
                sb.AppendLine($"| {(m is null ? "" : Md(m.Module))} | {(m is null ? "" : $"{m.Ms:N0}")} | {(m is null ? "" : $"{m.Share:P1}")} | | {(w is null ? "" : Md(w.Module))} | {(w is null ? "" : $"{w.WaitMs:N0}")} | {(w is null ? "" : $"{w.Switches:N0}")} |");
            }
            if (cpu.TopDpcIsr.Count > 0) sb.AppendLine().AppendLine("DPC / ISR time by module: " + string.Join(", ", cpu.TopDpcIsr.Select(d => $"{Md(d.Module)} {d.Ms:N0} ms")));
            // The top kernel functions by CPU and by wait (named from local symbols, else module + offset).
            if (cpu.TopFunctions.Count > 0 || cpu.TopWaitFunctions.Count > 0)
            {
                // Without symbols each row is one instruction address (module + offset), not a whole function: said in the header.
                bool named = cpu.TopFunctions.Concat(cpu.TopWaitFunctions).Any(f => f.Symbolized);
                string what = named ? "Kernel function" : "Kernel address (no symbols)";
                sb.AppendLine().AppendLine($"| {what} (CPU) | CPU (ms) | Share | | {what} (waited in) | Wait (ms) |").AppendLine("|---|---:|---:|---|---|---:|");
                for (int i = 0; i < Math.Max(Math.Min(10, cpu.TopFunctions.Count), Math.Min(10, cpu.TopWaitFunctions.Count)); i++)
                {
                    var f = i < cpu.TopFunctions.Count ? cpu.TopFunctions[i] : null;
                    var w = i < cpu.TopWaitFunctions.Count ? cpu.TopWaitFunctions[i] : null;
                    sb.AppendLine($"| {(f is null ? "" : Md(f.Name))} | {(f is null ? "" : $"{f.Ms:N0}")} | {(f is null ? "" : $"{f.Share:P1}")} | | {(w is null ? "" : Md(w.Name))} | {(w is null ? "" : $"{w.Ms:N0}")} |");
                }
            }
            if (cpu.FunctionsNote.Length > 0) sb.AppendLine().AppendLine(MdText.Line(cpu.FunctionsNote));
            sb.AppendLine().AppendLine("File-system lookups show as FLTMGR.SYS / refs.sys / Ntfs.sys, memory-manager work as ntoskrnl.").AppendLine();
        }
        if (c.Files.Count > 0)
        {
            sb.AppendLine("File activity per process and volume (Microsoft-Windows-Kernel-File):").AppendLine();
            foreach (var f in c.Files.Take(10))
            {
                string dirs = string.Join(", ", f.TopDirectories.Take(3).Select(d => $"{d.Path} ({d.Opens:N0})"));
                sb.AppendLine($"- {Md(f.Image)} (PID {f.Pid}) on {f.Volume}: {f.Creates:N0} opens ({f.DirectoryOpens:N0} directories), {f.Deletes:N0} deletes" +
                              (dirs.Length > 0 ? $"; top folders {Md(dirs)}" : ""));
            }
            sb.AppendLine();
        }
    }

    /// <summary>
    /// Allocation / free rates of the tag over the growth episode (null when the history does not hold it), and its rates
    /// just before the growth when the shape was judged against them.
    /// </summary>
    private (TagRates During, TagRates? Before)? Shape(Incident inc)
    {
        if (_history is not { } h || h.Count < 2) return null;
        // The episode's samples are found on the clock that only goes forward (the incident's onset and end, the samples' Ms).
        int i0 = h.IndexAtOrAfterMs(inc.OnsetMs), i1 = Math.Min(h.Count - 1, h.IndexAtOrAfterMs((inc.EndMs ?? inc.ClosedMs ?? h.Ms[^1]) + 1) - 1);
        if (i1 <= i0) return null;
        var r = h.TagRatesBetween(inc.Tag, i0, i1);
        // The same span (at most 10 min) just before the onset: the tag's background churn to compare against. An idle
        // baseline (or none kept: the tag was too small) changes nothing, so it is not reported either.
        long spanMs = Math.Min(600_000, h.Ms[i1] - h.Ms[i0]);
        int b0 = h.IndexAtOrAfterMs(inc.OnsetMs - spanMs);
        if (b0 >= 0 && i0 - b0 >= 2 && h.TagRatesBetween(inc.Tag, b0, i0) is { Shape: not AllocShape.Idle } baseline)
            return (r with { Shape = LeakShapes.Classify(r.AllocsPerMin, r.FreesPerMin, baseline) }, baseline);
        return (r, null);
    }

    /// <summary>
    /// What the span an incident's report counts over is, in words (the tag's allocation rates, an owner's process
    /// starts: from the incident's onset to its end, else to its close): "the drop" for an incident made for a sharp
    /// drop, whose span is the drop; "the trace" for one made for a trace of its own whose tag did not grow in it (the
    /// span is then the whole trace, and there is no growth to name); "the growth" otherwise.
    /// </summary>
    internal static string SpanName(Incident inc) =>
        inc.Kind == IncidentKind.Release ? "the drop" : inc.Kind == IncidentKind.Capture && inc.Growth <= 0 ? "the trace" : "the growth";

    /// <summary>"during the growth" (the drop, the trace: <see cref="SpanName"/>), or "from 10:42 (...)" when the counters cover only part of it.</summary>
    private string ShapeSpan(Incident inc)
    {
        string during = "during " + SpanName(inc);
        if (_history is not { Count: > 1 } h) return during;
        int i0 = h.IndexAtOrAfterMs(inc.OnsetMs);
        int first = h.FirstStoredIndex(inc.Tag, i0, h.Count - 1);
        if (first < 0) return during;
        var from = h.Times[first];
        if (h.Ms[0] > inc.OnsetMs) return $"from {Fmt.Clock(from)} (the history no longer holds the start of {SpanName(inc)})";
        // Counted from the span's first sample: an onset between two samples (a trace's own start) is not a counter missing.
        return h.Ms[first] - h.Ms[i0] > 30_000 ? $"from {Fmt.Clock(from)} (the tag was too small to keep its counters before)" : during;
    }

    /// <summary>The answer from the confirmation trace, or null without a trace that holds the tag's growth.</summary>
    private static string? TraceAnswer(Incident inc, TagInfo info) =>
        inc.Capture is { } c && inc.TraceOwners is { Count: > 0 } o ? TraceAttribution.Answer(inc, info, o, c) : null;

    private static string Short(string s, int n) => s.Length > n ? s[..n] + "…" : s;

    /// <summary>
    /// The incident's suspects grouped by owner with the one-line answer (null without a window or history). The answer
    /// names the tag's pool (paged or non-paged) by its bytes at the last sample: a closed incident's as its first write
    /// after the close read them (<paramref name="st"/>, <see cref="FileState.Then"/>), so a report written again hours
    /// later names the pool the tag was in then, not the one it is in by now (or none, once it left the tag list).
    /// What the tag did over the incident's span is <see cref="Moved"/>, and a closed incident's answer is in the past
    /// throughout ("was flat over 1 min", "No clear cause was found"): its report is written again hours after that
    /// span. An incident made for a trace of its own has no span until its trace is attached (it is made closed, its
    /// start and its end one moment: <c>IncidentManager.CreateCapture</c>), and none ever when its trace recorded
    /// nothing: its answer, when it has one, then says that (<see cref="NoTraceSpan"/>), not what the tag did over
    /// no time at all ("... was flat over 0 s."): "File objects (File, non-paged): its trace had not finished by
    /// 07:05:20; this report is written again when it has.", and the write that attaches the trace says what the
    /// tag did over it; or "File objects (File, non-paged): its trace recorded nothing PoolWatch could use." It has
    /// an answer only when it has a suspect, which over a span of no length takes a handle scan after the newest
    /// sample that shows a process with more handles of the tag's type than the scan before (a report with no
    /// suspect has no answer: null, above).
    /// </summary>
    private TagAnswer? PlainOwners(Incident inc, FileState? st = null)
    {
        if (inc.Window is not { } w || _history is not { } h || inc.Suspects.Count == 0) return null;
        if (_plainFor == inc && _plain is not null) return _plain; // computed once per write (report.json and report.md)
        try
        {
            var owners = inc.Owners ?? OwnerRanking.Rank(h, inc.Suspects, inc.Tag, w, inc.Growth, s => Identity.ProcessIdentities.Of(_registry.Ancestry(s.Key) is { Count: > 0 } c ? c : [new ProcessRecord { Key = s.Key, Image = s.Image }]));
            var info = _knowledge.Lookup(inc.Tag);
            var span = TimeSpan.FromMilliseconds((inc.EndMs ?? inc.ClosedMs ?? w.ToMs ?? inc.LastAlertMs) - inc.OnsetMs);
            var (paged, nonPaged) = st?.Then.Level ?? h.TagAt(inc.Tag, h.Count - 1);
            var (moved, onItsOwn) = Moved(inc);
            _plainFor = inc;
            if (NoTraceSpan(inc) is { } why)
                return _plain = new TagAnswer($"{OwnerRanking.Named(info, inc.Tag, paged, nonPaged)}: {why}{(inc.Capture is null ? "; this report is written again when it has." : ".")}",
                    owners, 0, owners.Where(o => o.Meaningful).Sum(o => o.ExplainedBytes));
            return _plain = OwnerRanking.Answer(info, inc.Tag, paged, nonPaged, moved, Fmt.Duration(span), owners, closed: !inc.IsOpen, onItsOwn);
        }
        catch { return null; }
    }

    /// <summary>
    /// What a report says of an incident made for a trace of its own that has no span, in place of what the tag did
    /// over it (in its title, its Growth line, its answer in plain words, and its draft bug report's summary and
    /// "Actual" line, which is the title); null for any other incident (<see cref="Incident.NoTraceSpan"/>, which
    /// says when an incident has no span, and why each text is true). Until the trace is attached: "its trace had
    /// not finished by 07:05:20", the time the newest sample's at this write (the moment the incident was made,
    /// before any sample). Once a trace is attached that has no span of its own: "its trace recorded nothing
    /// PoolWatch could use" (the trace's error or note, when it has one, is under Confirmation trace).
    /// </summary>
    private string? NoTraceSpan(Incident inc) => inc.NoTraceSpan(_history is { Count: > 0 } h ? h.Times[^1] : null, Fmt.Clock);

    /// <summary>
    /// What the answer in plain words says the tag did over the incident's span: above 0 it grew by that much, below 0
    /// it fell by that much, 0 it did neither; and whether a fall is said as "on its own" (PoolWatch's own cache clears
    /// freed more of the tag in the span, which is their result and no part of the fall). An incident made for a sharp
    /// drop (<see cref="IncidentKind.Release"/>): the drop, as its headline and its "Released" line give it. (Before, a
    /// drop's answer was its <see cref="Incident.Growth"/>, the rise in the window before the drop, never below 0: "is
    /// flat over 1 min", or "grew 1.1 MB in 10 s", under "dropped 141.5 MB".) Any other incident: its growth, which
    /// counts back what PoolWatch's own clears freed; and for one that did not grow, how far its level fell from its
    /// start to the sample its level is of (<see cref="Incident.CurrentAtUtc"/>), counted the same way, so a trace of a
    /// tag that fell through it is not said to have been flat.
    /// </summary>
    internal static (long Bytes, bool OnItsOwn) Moved(Incident inc)
    {
        if (inc.Kind == IncidentKind.Release && inc.Release is { } drop) return (Math.Min(0, -drop.Dropped), drop.ClearedBytes > 0);
        if (inc.Growth > 0 || inc.CurrentAtUtc is null) return (inc.Growth, false);
        return (Math.Min(0, inc.CurrentBytes + inc.ClearedBytes - inc.StartBytes), inc.ClearedBytes > 0);
    }

    /// <summary>
    /// A volume's line in report.md's System section; "(Dev Drive)" only for one Windows said is a Dev Drive, never for one
    /// it gave no answer for (null).
    /// </summary>
    internal static string SystemVolume(VolumeInfo v) =>
        $"- Volume {v.Root} {Md(v.FileSystem)}{(v.IsDevDrive == true ? " (Dev Drive)" : "")} {v.DriveType}{(v.Label.Length > 0 ? $" \"{Md(v.Label)}\"" : "")}";

    /// <summary>A volume's line in the bug report's Environment; "Dev Drive" only for one Windows said is a Dev Drive.</summary>
    internal static string EnvironmentVolume(VolumeInfo v) => $"- {v.Root} {Md(v.FileSystem)}{(v.IsDevDrive == true ? " Dev Drive" : "")}";

    private static void AppendSystem(StringBuilder sb, SystemInfo s)
    {
        sb.AppendLine("## System").AppendLine();
        sb.AppendLine($"- Machine: {Md(s.Machine)}; {Md(s.OsDescription)} (build {Md(s.OsBuild)}); {s.LogicalCpus} logical CPUs; {Fmt.Bytes(s.RamBytes)} RAM" +
                      (s.BootUtc is { } b ? $"; booted {Fmt.Stamp(b)}" : ""));
        foreach (var v in s.Volumes) sb.AppendLine(SystemVolume(v));
        sb.AppendLine($"- Antivirus: {Md(s.AntiVirus)}");
        if (s.PoolWatchCost is { } cost) sb.AppendLine($"- {Md(cost)}");
        if (s.FilterDrivers.Count > 0)
        {
            sb.AppendLine("- File-system filter drivers (`fltmc filters`):").AppendLine();
            sb.AppendLine("```");
            foreach (var l in s.FilterDrivers) sb.AppendLine(MdCode(l)); // no backtick: the fence cannot be ended early
            sb.AppendLine("```");
        }
        sb.AppendLine();
    }

    /// <summary>
    /// The draft for the vendor, meant to be pasted elsewhere: every name, version and path in it comes from the suspect
    /// process or its file, so each goes through <see cref="Md"/> (outside a code span) or <see cref="MdCode"/> (inside).
    /// It ends with the files it has as evidence: report.md itself and report.json (written just before it), and, of
    /// timeline.csv, processes.csv, events.csv and timeline.png, those the folder holds as this report.md is written
    /// (<paramref name="shows"/>); its third step names timeline.csv only when the folder holds it, too. A folder
    /// can lack some of them: an incident opened by a growth has no timeline.csv, processes.csv or chart until its
    /// first refresh with them, no chart is drawn of a window with fewer than two samples, and another program can
    /// keep a file from being made. (Before, the draft listed timeline.csv, processes.csv and events.csv whatever
    /// the folder held.)
    /// </summary>
    private void AppendBugReport(StringBuilder sb, Incident inc, TagInfo info, Dictionary<ProcessKey, FileDetails?> details, SystemInfo s, string headline, Shows shows)
    {
        var top = inc.Suspects.FirstOrDefault();
        var f = top is null ? null : details.GetValueOrDefault(top.Key);
        string vendor = f?.Company ?? f?.Signer ?? top?.Image ?? info.Owner;
        sb.AppendLine($"## Draft bug report for {Md(vendor)}").AppendLine();
        sb.AppendLine("**Summary**").AppendLine();
        // An incident made for a trace that has no span (its trace not attached yet, or one that recorded nothing)
        // has no growth to sum up: the draft says what the title says (NoTraceSpan), not "... grows by 0 B in 0 s."
        sb.AppendLine(NoTraceSpan(inc) is { } why
            ? $"Kernel pool tag `{inc.Tag.ToDisplay()}` ({Md(Owner(info))}): {why}."
            : top is null
            ? $"Kernel pool tag `{inc.Tag.ToDisplay()}` ({Md(info.Owner)}) grows abnormally; no single process stood out."
            : $"While {Md(top.Image)}{(f?.FileVersion is { } fv ? $" {Md(fv)}" : "")} runs, kernel pool tag `{inc.Tag.ToDisplay()}` ({Md(Owner(info))}) grows by {Fmt.Bytes(inc.Growth)} " +
              $"in {Fmt.Duration(inc.GrowthSpan)}" +
              (inc.Retention is not null ? " and is not released after the activity stops" : inc.Release is not null ? "; the memory comes back only when the work is undone" : "") + ".");
        sb.AppendLine();
        sb.AppendLine("**Environment**").AppendLine();
        sb.AppendLine($"- {Md(s.OsDescription)}, build {Md(s.OsBuild)}, {s.LogicalCpus} logical CPUs, {Fmt.Bytes(s.RamBytes)} RAM");
        foreach (var v in s.Volumes) sb.AppendLine(EnvironmentVolume(v));
        sb.AppendLine($"- Antivirus: {Md(s.AntiVirus)}; filter drivers: {(s.FilterDrivers.Count > 0 ? "see the System section" : "unknown")}");
        if (top is not null) sb.AppendLine($"- Process: `{MdCode(top.ImagePath ?? top.Image)}`{(f?.FileVersion is { } v2 ? $", version {Md(v2)}" : "")}");
        sb.AppendLine();
        sb.AppendLine("**Steps to reproduce** (template: fill in)").AppendLine();
        sb.AppendLine($"1. Start {Md(top?.Image ?? "<process>")}{(top?.CommandLine is { } cl ? $" as `{MdCode(cl)}`" : "")}.");
        sb.AppendLine("2. <what it was doing: the workload, files or folders involved>.");
        sb.AppendLine($"3. Watch kernel pool tag `{inc.Tag.ToDisplay()}` (poolmon{(shows.Timeline ? ", or the attached timeline.csv" : "")}).");
        sb.AppendLine();
        sb.AppendLine("**Expected**: pool usage returns to its baseline when the work finishes.").AppendLine();
        sb.AppendLine($"**Actual**: {headline}.").AppendLine();
        var attached = new List<string> { "report.md", "report.json" };
        if (shows.Timeline) attached.Add("timeline.csv (tag and pool totals)");
        if (shows.Processes) attached.Add("processes.csv (suspects' counters)");
        if (shows.Events) attached.Add("events.csv (process lifecycle)");
        if (shows.Png) attached.Add("timeline.png");
        sb.AppendLine($"**Attached evidence**: {string.Join(", ", attached)}.");
    }

    private long WriteTimelineCsv(History.History h, Incident inc, int from, int to, FileState st)
    {
        var sb = new StringBuilder("time,tag_bytes,tag_paged_bytes,tag_nonpaged_bytes,committed_paged_bytes,committed_nonpaged_bytes,tagsum_paged_bytes,tagsum_nonpaged_bytes\n");
        for (int i = from; i <= to && i < h.Count; i++)
        {
            var (p, np) = h.TagAt(inc.Tag, i);
            var t = h.Totals[i];
            sb.AppendLine(Csv.Line(h.Times[i], p + np, p, np, t.CommittedPaged, t.CommittedNonPaged, t.TagSumPaged, t.TagSumNonPaged));
        }
        return Atomic(Path.Combine(inc.Dir, "timeline.csv"), sb.ToString(), st);
    }

    private long WriteProcessesCsv(History.History h, Incident inc, int from, int to, FileState st)
    {
        var sb = new StringBuilder("time,rank,pid,create_time,image,handles,read_ops,write_ops,other_ops,quota_paged,quota_nonpaged\n");
        foreach (var s in inc.Suspects.Take(5))
        {
            var pts = h.ProcessSeries(s.Key, from);
            for (int i = 0; i < pts.Length && from + i <= to; i++)
            {
                if (pts[i] is not { } p) continue;
                sb.AppendLine(Csv.Line(h.Times[from + i], s.Rank, s.Key.Pid, s.Key.CreateTime, s.Image, p.Handles, p.ReadOps, p.WriteOps, p.OtherOps, p.QuotaPaged, p.QuotaNonPaged));
            }
        }
        return Atomic(Path.Combine(inc.Dir, "processes.csv"), sb.ToString(), st);
    }

    private const string EventsHeader = "time,kind,pid,create_time,parent_pid,image,exit_code,user,command_line,detail\n";

    private string EventLine(LifecycleEvent e) => Csv.Line(e.TimeUtc, e.Kind.ToString(), e.Pid, e.CreateTime, e.ParentPid, e.Image, e.ExitCode,
        e.User is null ? null : _host.DisplayUser(e.User), e.CommandLine, e.Detail);

    private static string AlertLine(Alert a) => Csv.Line(a.TimeUtc, a.Kind == AlertKind.Release ? "Release" : "Alert", 0, 0, 0, "", null, null, null,
        $"{a.Tag.ToDisplay()} {a.Rule} ({Fmt.SignedBytes(a.Kind == AlertKind.Release ? -a.Bytes : a.Bytes)})");

    /// <summary>What the day's events.csv holds of the events from a time on (<see cref="DayEventsFrom"/>).</summary>
    private enum DayCopy { Every, None, Part }

    /// <summary>
    /// What the day's data\&lt;yyyyMMdd&gt;\events.csv holds of the events from <paramref name="fromUtc"/> on, by what the day's
    /// files record and since when (<see cref="IIncidentHost.DayRecords"/>): every one when Detailed recording has been on
    /// since before then; none when Reports only has; and those of the time Detailed recording was on when the choice was
    /// changed since then (a switch in the middle of an incident).
    /// </summary>
    private DayCopy DayEventsFrom(DateTime fromUtc)
    {
        var (detailed, since) = _host.DayRecords;
        return since > fromUtc ? DayCopy.Part : detailed ? DayCopy.Every : DayCopy.None;
    }

    /// <summary>
    /// The last row of an open incident's events.csv once its first part is full and all written: the file has no event after
    /// <paramref name="t"/>. Worded as a fact about this file, true however the run ends (a crash or a power loss before the
    /// incident closes leaves the file as it is) and wherever the onset is measured afterwards: how many events it holds
    /// (<paramref name="rows"/>) and from when (<see cref="FirstPartSaid"/>, by the time the file's first part starts at:
    /// not "the window's first", which an onset measured again would make untrue), as PoolWatch had them then (a late
    /// event can take a place among them after this row), with what PoolWatch does with the file at the close.
    /// </summary>
    private static string NoteLine(DateTime t, DateTime? from, DateTime? lostTo, int rows)
    {
        var c = CultureInfo.InvariantCulture;
        return Csv.Line(t, "Note", 0, 0, 0, "", null, null, null,
            $"events.csv holds {FirstPartSaid(from, lostTo, rows, x => Csv.Escape(x), c)}, to this row's time, as PoolWatch had them when it wrote this row; later events are not in this file ({AtClose(c, "")})");
    }

    /// <summary>
    /// Why a <c>Gap</c> row between the two ends is there: what the file keeps, counted from the rows written
    /// (<paramref name="first"/> of them before the row, <paramref name="latest"/> after it), and from where: the first
    /// part starts where the incident's window started when the file was written (<see cref="FirstPartSaid"/>,
    /// <see cref="WhenWritten"/>: the incident's first part follows the window's start for a file written whole; said
    /// only of a first part that does start there, which one made with its events may not), with what PoolWatch no
    /// longer held from there by then, when it had let events go; the latest part ends with the window. A file with no
    /// first part (the buffer let its events go before an incident the manager does not hold looked at them) keeps the
    /// latest part alone.
    /// </summary>
    private static string LeftBetweenEnds(Incident inc, IncidentEventSet ev, int first, int latest)
    {
        var c = CultureInfo.InvariantCulture;
        // The count of those left out runs past the window's end (PastEnd): said after what the file keeps.
        string past = PastEnd(ev, ev.Gap) is { } end ? PastEndSaid(end, t => Csv.Escape(t), " when this file was written") : "";
        if (first == 0)
            return (latest > 0 ? string.Create(c, $"events.csv keeps only the latest {latest:N0} events up to the incident window's end") : "events.csv keeps none of the incident window's events") + past;
        string where = ev.FromMs == IncidentManager.EventWindowStartMs(inc) ? WhenWritten : "";
        string keeps = "events.csv keeps " + FirstPartSaid(ev.FromUtc, ev.LostToUtc, first, t => Csv.Escape(t), c, where);
        if (latest == 0) return keeps + past;
        return keeps + (ev.FromUtc is null ? string.Create(c, $" and its latest {latest:N0}") : string.Create(c, $", and the latest {latest:N0} up to the window's end")) + past;
    }

    /// <summary>
    /// Where the window ends, when the events an incident counts as left out between its two parts run past it
    /// (<paramref name="gap"/>'s last is after <see cref="IncidentEventSet.EndMs"/>); else null. The incident counts each
    /// event by its own time against the window's end as it is then; when the end is measured again afterwards (to
    /// before events already counted, or to among those it set aside), it takes its latest part and its counts again
    /// from the events PoolWatch holds (<c>IncidentManager.Refit</c>), and only when PoolWatch no longer holds them
    /// does a count stay as it was made (so does that of an incident the manager does not hold, for events PoolWatch
    /// let go before it looked at them: it knows how many, not their times): the <c>Gap</c> row and report.md then say
    /// so, with where the window ends (<see cref="PastEndSaid"/>).
    /// </summary>
    private static DateTime? PastEnd(IncidentEventSet ev, EventGap gap) => gap.Count > 0 && gap.ToMs > ev.EndMs ? ev.EndUtc : null;

    /// <summary>
    /// What a <c>Gap</c> row and report.md add when the count of the events left out runs past the window's end
    /// (<see cref="PastEnd"/>): where the window ended (<paramref name="end"/>, written by <paramref name="time"/>) as
    /// the file was written (<paramref name="when"/> says so in the words of the text it goes into), and why the count
    /// holds later events.
    /// </summary>
    private static string PastEndSaid(DateTime end, Func<DateTime, string> time, string when) =>
        $"; the count runs past the window's end, {time(end)}{when}: PoolWatch no longer held those events, to count each by its own time against that end";

    /// <summary>Why a <c>Gap</c> row's events are not in the file: the write budget never had room for them.</summary>
    private const string LeftByBudget = "PoolWatch's write budget had no room for them before the incident left memory";

    /// <summary>
    /// The row that says how many events the file leaves out, and the first and last of their times: at the first one's
    /// time, so it sorts where they begin (events.csv's time format in its detail).
    /// </summary>
    private static string GapLine(EventGap g, string why) => Csv.Line(g.FromUtc, "Gap", 0, 0, 0, "", null, null, null,
        string.Create(CultureInfo.InvariantCulture, $"{g.Count:N0} events left out, between {Csv.Escape(g.FromUtc)} and {Csv.Escape(g.ToUtc)} ({why})"));

    /// <summary>
    /// Rows of events and alerts, sorted by time (the alerts' rows among the events'), each ending a line; a <c>Gap</c> row in
    /// its place by time (after rows of the same time); the note row (<see cref="NoteLine"/>) last.
    /// </summary>
    private string Rows(IEnumerable<LifecycleEvent> events, IEnumerable<Alert> alerts, string? note, (EventGap Gap, string Why)? gap = null) =>
        RowsInto(new StringBuilder(), events, alerts, note, gap).ToString();

    /// <summary><see cref="Rows"/> appended to <paramref name="sb"/>: sorted first, each row made as it is appended.</summary>
    private StringBuilder RowsInto(StringBuilder sb, IEnumerable<LifecycleEvent> events, IEnumerable<Alert> alerts, string? note, (EventGap Gap, string Why)? gap = null)
    {
        // Each row as what it is (an event, an alert or the Gap row), in time order (stable: an alert after the events of its
        // moment, the Gap row after both).
        IEnumerable<(DateTime TimeUtc, LifecycleEvent? Event, Alert? Alert)> rows =
            events.Select(e => (e.TimeUtc, (LifecycleEvent?)e, (Alert?)null)).Concat(alerts.Select(a => (a.TimeUtc, (LifecycleEvent?)null, (Alert?)a)));
        if (gap is { } g) rows = rows.Append((g.Gap.FromUtc, null, null));
        foreach (var (_, e, a) in rows.OrderBy(x => x.TimeUtc))
            sb.AppendLine(e is not null ? EventLine(e) : a is not null ? AlertLine(a) : GapLine(gap!.Value.Gap, gap.Value.Why));
        if (note is not null) sb.AppendLine(note);
        return sb;
    }

    /// <summary>
    /// What events.csv should hold now, against what it holds (<see cref="WriteEventsCsv"/>, <see cref="BothEndsDue"/>,
    /// <see cref="WriteLeaving"/>): its first part (<see cref="Head"/>; <see cref="Fresh"/>: those of it not in the file as
    /// written) and its latest part (<see cref="Tail"/>), each up to the window's end, and what it leaves out between them
    /// (<see cref="Gap"/>); whether it is to hold both ends (<see cref="BothEnds"/>: a closed incident with events after its
    /// first part, or whose file holds a note or both ends already), and whether it does as written (<see cref="Holds"/>);
    /// and whether the incident's first part followed the window's start since the file was begun or written whole
    /// (<see cref="Followed"/>: the file's rows are then of another first part than the incident has now, from another
    /// start or from the same one with other events let go by then, and a file with both ends does not hold them as
    /// they are now). Every event of the first part is the file's: the first part is the
    /// window's first events as its start was last followed (<c>IncidentManager.Follow</c>). <see cref="Moved"/>: a
    /// file with its first part alone, as written, holds an event that is no longer one of the incident's first part (a
    /// late event took a place in a full first part, and its last one moved on to the latest part): rows added to it
    /// would make it more than the first part, with a row after where the first part stops.
    /// </summary>
    private sealed record EventsPlan(bool AsWritten, List<LifecycleEvent> Head, List<LifecycleEvent> Fresh, List<LifecycleEvent> Tail, EventGap Gap, bool BothEnds, bool Holds,
        (int Rows, long First, long Last, int LeftOut) Key, bool Followed, bool Moved);

    /// <param name="ends">Both ends are wanted whatever the incident (an export of an open one): an open incident's plan
    /// otherwise holds only what an append reads (<see cref="EventsPlan.Fresh"/>), not its two parts.</param>
    /// <param name="freshMax">An open incident's plan stops at this many events not in the file and one more (a write adds
    /// at most this many; the one more says some are left for later).</param>
    private EventsPlan Plan(Incident inc, IncidentEventSet ev, FileState st, string path, bool ends = false, int freshMax = int.MaxValue)
    {
        bool asWritten = st.Events is not null && File.Exists(path) && new FileInfo(path).Length == st.Length;
        // Once the incident let go of its latest part (it can no longer re-open), its file as written is final.
        bool released = ev.LatestReleased && !inc.IsOpen;
        // The file was begun (or written whole) with another first part than the incident has now: from another start, or
        // from the same one with other events let go by then (a start measured somewhere else and back again, once the
        // buffer no longer held every event from it: the first part is then of later events than the file's rows, and
        // rows appended to the file would leave events out between its first row and its last).
        bool followed = asWritten && ev.FromUtc is not null && st.From is { } began && (began.Ms != ev.FromMs || st.LostToUtc != ev.LostToUtc);
        ends |= !inc.IsOpen;
        var head = new List<LifecycleEvent>(ends ? ev.Kept.Count : 0);
        var fresh = new List<LifecycleEvent>();
        int ofFile = 0; // the first part's events the file holds, those after the window's end too
        foreach (var e in ev.Kept)
        {
            bool has = asWritten && st.Events!.Contains(RowKey(e));
            if (has) ofFile++;
            if (e.Ms > ev.EndMs) continue;
            if (ends) head.Add(e);
            if (has) continue;
            if (ends || fresh.Count <= freshMax) fresh.Add(e);
        }
        var tail = new List<LifecycleEvent>(ends ? ev.Tail.Count : 0);
        if (ends) foreach (var e in ev.Tail) if (e.Ms <= ev.EndMs) tail.Add(e);
        bool bothEnds = !inc.IsOpen && (released || tail.Count > 0 || ev.Gap.Count > 0 || (asWritten && (st.NotedAt is not null || st.Tail is not null)));
        var key = released && asWritten && st.Tail is { } final ? final : TailKey(tail, ev.Gap);
        bool holds = bothEnds && asWritten && !followed && fresh.Count == 0 && st.Tail == key;
        // A file with its first part alone holds a row that is no longer one of the first part's events.
        bool moved = asWritten && st.Tail is null && ofFile < st.EventRows;
        return new(asWritten, head, fresh, tail, ev.Gap, bothEnds, holds, key, followed, moved);
    }

    /// <summary>
    /// The rows of a file with its first part alone that is written again whole from the incident's first part: every
    /// event of the first part up to the last one the file holds already (it loses none of those, and one of them the
    /// file did not hold, a late event's or an earlier one's after a start measured earlier, comes in its place), and
    /// up to <paramref name="maxRows"/> more of it, every one from its first to its last, in time order; and whether
    /// the first part has more.
    /// </summary>
    private static (List<LifecycleEvent> Rows, bool More) StartAgain(IncidentEventSet ev, FileState st, int maxRows)
    {
        var part = new List<LifecycleEvent>();
        int held = 0;
        foreach (var e in ev.Kept)
        {
            if (e.Ms > ev.EndMs) continue;
            part.Add(e);
            if (st.Events!.Contains(RowKey(e))) held = part.Count;
        }
        int take = (int)Math.Min(part.Count, held + (long)Math.Max(0, maxRows));
        return (take == part.Count ? part : part.GetRange(0, take), take < part.Count);
    }

    /// <summary>What a file holds of the latest part, as one value: how many rows, the first and last (<see cref="RowKey"/>), and how many it leaves out.</summary>
    private static (int Rows, long First, long Last, int LeftOut) TailKey(List<LifecycleEvent> tail, EventGap gap) =>
        (tail.Count, tail.Count > 0 ? RowKey(tail[0]) : 0, tail.Count > 0 ? RowKey(tail[^1]) : 0, gap.Count);

    /// <summary>The earliest of the events on the clock that only goes forward, with its time of day (<see cref="FileState.First"/>); null: none.</summary>
    private static (long Ms, DateTime Utc)? Earliest(List<LifecycleEvent> events) =>
        events.MinBy(e => e.Ms) is { } first ? (first.Ms, first.TimeUtc) : null;

    /// <summary>
    /// Both ends of the window as rows: the first part, then, when it left any out, the <c>Gap</c> row, then the latest part,
    /// each part in time order on the clock that only goes forward (as the incident keeps them), each event once (by
    /// <see cref="RowKey"/>), and the incident's alerts among them by time (after the events of their moment; before the Gap
    /// row when of its moment or earlier). The Gap row is placed by position, between the two parts, not by its time alone:
    /// an event of the latest part with the Gap row's time stays after it, and a clock set back between the two parts does not
    /// mix them. The Gap row says what the file keeps, counted from these rows, and from where (<see cref="LeftBetweenEnds"/>).
    /// <paramref name="events"/>: the events written.
    /// </summary>
    private StringBuilder BothEndsRows(Incident inc, IncidentEventSet ev, EventsPlan p, out List<LifecycleEvent> events, out HashSet<long> keys)
    {
        var (head, tail, gap) = (p.Head, p.Tail, p.Gap);
        keys = new HashSet<long>(head.Count + tail.Count);
        events = new List<LifecycleEvent>(head.Count + tail.Count);
        foreach (var e in head) if (keys.Add(RowKey(e))) events.Add(e);
        int firstPart = events.Count;
        foreach (var e in tail) if (keys.Add(RowKey(e))) events.Add(e);
        // About 300 characters a row at the owner's command-line lengths: room for all of them at once.
        var sb = new StringBuilder(EventsHeader, EventsHeader.Length + 320 * (events.Count + inc.Alerts.Count + 1));
        var alerts = AlertRows(inc).Values.OrderBy(a => a.TimeUtc).ToList();
        int next = 0;
        for (int i = 0; i <= events.Count; i++)
        {
            if (i == firstPart && gap.Count > 0)
            {
                while (next < alerts.Count && alerts[next].TimeUtc <= gap.FromUtc) sb.AppendLine(AlertLine(alerts[next++]));
                sb.AppendLine(GapLine(gap, LeftBetweenEnds(inc, ev, firstPart, events.Count - firstPart)));
            }
            if (i == events.Count) break;
            while (next < alerts.Count && alerts[next].TimeUtc < events[i].TimeUtc) sb.AppendLine(AlertLine(alerts[next++]));
            sb.AppendLine(EventLine(events[i]));
        }
        while (next < alerts.Count) sb.AppendLine(AlertLine(alerts[next++]));
        return sb;
    }

    /// <summary>Replaces events.csv whole in one step (temp file and move) with both ends of the window (<see cref="BothEndsRows"/>); returns the bytes written.</summary>
    private long WriteWhole(Incident inc, IncidentEventSet ev, string path, FileState st, EventsPlan p)
    {
        var gap = p.Gap;
        var text = BothEndsRows(inc, ev, p, out var rows, out var keys);
        string tmp = _host.CheckWrite(path + ".tmp");
        long length;
        // Written from the builder as it is, without a copy of the whole file as one string.
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using (var w = new StreamWriter(fs, Utf8, 1 << 16, leaveOpen: true))
                foreach (var chunk in text.GetChunks()) w.Write(chunk.Span);
            length = fs.Length;
        }
        File.Move(tmp, _host.CheckWrite(path), overwrite: true);
        Profiler.Wrote(path, length);
        st.Events = keys;
        st.Alerts = AlertRows(inc).Keys.ToHashSet();
        (st.EventRows, st.Length, st.NotedAt, st.Pending, st.StartsAgain) = (rows.Count, length, null, false, false);
        st.First = Earliest(rows);
        InOrder(st, rows, inc);
        Begun(st, ev);
        // A file with no latest part and nothing left out is the first part again: later rows can follow it.
        bool ends = p.Tail.Count > 0 || gap.Count > 0;
        st.Tail = ends ? p.Key : null;
        st.Gap = gap.Count > 0 ? gap : null;
        st.GapPastEndUtc = PastEnd(ev, gap);
        return length;
    }

    /// <summary>The file was written whole, its rows in time order: the latest time of a row it holds (<see cref="FileState.LastRowUtc"/>), its events' and its alerts'.</summary>
    private static void InOrder(FileState st, List<LifecycleEvent> events, Incident inc)
    {
        DateTime? last = null;
        foreach (var e in events) if (last is not { } l || e.TimeUtc > l) last = e.TimeUtc;
        foreach (var a in AlertRows(inc).Values) if (last is not { } l || a.TimeUtc > l) last = a.TimeUtc;
        (st.LastRowUtc, st.OutOfOrder) = (last, false);
    }

    /// <summary>
    /// Whether a row added to the file now would come after one of a later time: an event of <paramref name="fresh"/>,
    /// or an alert's row the file does not hold yet, timed before the latest row it holds.
    /// </summary>
    private static bool AddsEarlier(FileState st, List<LifecycleEvent> fresh, Incident inc)
    {
        if (st.LastRowUtc is not { } last) return false;
        foreach (var e in fresh) if (e.TimeUtc < last) return true;
        foreach (var (key, a) in AlertRows(inc)) if (!st.Alerts!.Contains(key) && a.TimeUtc < last) return true;
        return false;
    }

    /// <summary>
    /// Appends whole rows in one write at the end of the file: <paramref name="fresh"/> events, the alerts' rows not in it yet,
    /// and a note (its time and its row, <see cref="NoteLine"/>) or <c>Gap</c> row; returns the bytes written (0: nothing to add).
    /// </summary>
    private long AppendRows(Incident inc, string path, FileState st, List<LifecycleEvent> fresh, (DateTime At, string Line)? note, (EventGap Gap, string Why)? gap = null)
    {
        var alerts = AlertRows(inc).Where(kv => !st.Alerts!.Contains(kv.Key)).ToList();
        if (fresh.Count == 0 && alerts.Count == 0 && note is null && gap is null) return 0;
        var add = Utf8.GetBytes(Rows(fresh, alerts.Select(kv => kv.Value), note?.Line, gap));
        bool earlier = AddsEarlier(st, fresh, inc);
        long length;
        using (var fs = new FileStream(_host.CheckWrite(path), FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            fs.Write(add);
            length = fs.Length;
        }
        Profiler.Wrote(path, add.Length);
        // The rows added are in time order among themselves, after the rows the file held: out of order when one of them
        // is timed before the latest of those.
        if (earlier) st.OutOfOrder = true;
        foreach (var e in fresh) if (st.LastRowUtc is not { } l || e.TimeUtc > l) st.LastRowUtc = e.TimeUtc;
        foreach (var (_, a) in alerts) if (st.LastRowUtc is not { } l || a.TimeUtc > l) st.LastRowUtc = a.TimeUtc;
        foreach (var e in fresh) st.Events!.Add(RowKey(e));
        if (Earliest(fresh) is { } first && (st.First is not { } had || first.Ms < had.Ms)) st.First = first;
        foreach (var (k, _) in alerts) st.Alerts!.Add(k);
        st.EventRows += fresh.Count;
        st.Length = length;
        if (note is { } noted) st.NotedAt = noted.At;
        return add.Length;
    }

    /// <summary>
    /// events.csv. While the incident is open, its first part: up to <paramref name="maxRows"/> of the kept events not in it
    /// yet (<see cref="IncidentEventSet.Kept"/>, the earliest first: the file holds every kept event from its first row to
    /// its last) and the alert rows not in it yet (<see cref="AlertRow"/>), appended as whole rows; once it holds all of a
    /// full first part, a last row says to when. A closed incident's with events after its first part (or whose file holds
    /// a note or both ends already) holds both ends: a write that must be made (<paramref name="must"/>: its closing write,
    /// once; the end of a run; a trace attached) replaces it whole in one step with them when it does not hold them as they
    /// are (<see cref="WriteWhole"/>), every row of the first part included; until then it gets only new alerts' rows. A
    /// file that holds both ends while its incident is open again (re-opened) gets only new alerts' rows until it closes. The
    /// whole file is written again otherwise when it is not as this run last wrote it (or not there), and when the
    /// incident's first part followed the window's start since the file was begun (<see cref="EventsPlan.Followed"/>): the
    /// file then starts again from the first part, with the rows of it the file held and up to
    /// <paramref name="maxRows"/> more (<see cref="StartAgain"/>), so an open incident's file never loses a row but for
    /// being written again from the new start with the events the first part then holds. Not a file whose Note row says
    /// to when it holds its events (it stays as written until the close), not by a write without rows, and not while an
    /// open incident's first part holds no event: the file is then left as it is, with no Note row (a closed
    /// incident's is written again all the same, with no event row: its rows from before the start are not its window's).
    /// The file starts again in the same way when it holds a row that is no longer one of the first part's
    /// (<see cref="EventsPlan.Moved"/>: a late event took a place in a full first part), so it never holds more than the
    /// first part, nor a row after its Note row's time. A closed incident's file is in time order: one with its first
    /// part alone was added to in the order PoolWatch added its rows while the incident was open, and a write that must
    /// be made writes it whole, in time order, when a row of it comes after one of a later time, or would with this
    /// write's rows (a late event's). Events left for a later write make it pending (<see cref="EventsPending"/>); so
    /// does a write that failed (its rows still wait).
    /// </summary>
    private long WriteEventsCsv(Incident inc, IncidentEventSet ev, FileState st, int maxRows, bool must)
    {
        try
        {
            long bytes = WriteEvents(inc, ev, st, maxRows, must);
            st.EventsDone = true;
            return bytes;
        }
        catch
        {
            // Not written (another program holds the file, the folder is gone): what this write was to add still waits,
            // and is tried again (IncidentManager.AppendEvents).
            st.Pending = true;
            throw;
        }
    }

    private long WriteEvents(Incident inc, IncidentEventSet ev, FileState st, int maxRows, bool must)
    {
        string path = Path.Combine(inc.Dir, "events.csv");
        var p = Plan(inc, ev, st, path, freshMax: maxRows);
        if (!p.AsWritten) (st.NotedAt, st.Tail, st.Gap, st.GapPastEndUtc) = (null, null, null, null);
        st.KeptSeen = ev.Kept.Count;
        st.StartsAgain = false;
        if (p.BothEnds)
        {
            st.Pending = false;
            if (!p.Holds && must) return WriteWhole(inc, ev, path, st, p);
            // It holds them, or waits for a write that must be made (its rows would otherwise follow its latest part).
            return p.AsWritten ? AppendRows(inc, path, st, [], null) : 0;
        }
        if (p.AsWritten && st.Tail is not null)
        {
            // Both ends from its last close, the incident open again: its new rows wait for its next close.
            st.Pending = false;
            return AppendRows(inc, path, st, [], null);
        }
        var fresh = p.Fresh;
        // Once its last row says to when it holds the first part (the Note row), an open incident's file gets no more event
        // rows: a late event that takes a place in the first part since (its last one moving on) waits for the close, which
        // writes the file whole; and it stays as written when the first part follows the window's start, too.
        bool noted = inc.IsOpen && st.NotedAt is not null;
        if (noted) fresh = [];
        bool more = fresh.Count > maxRows;
        if (more) fresh = fresh.GetRange(0, maxRows);
        // The incident's first part followed the window's start since the file was begun, or the file holds a row that is
        // no longer one of the first part's (a late event took its place there): the file starts again from the first part.
        bool again = p.AsWritten && (p.Followed || p.Moved) && !noted;
        // A closed incident's file is in time order: written whole by a write that must be made when it is not, or would
        // not be with the rows this write adds (it was added to in the order PoolWatch added its rows).
        bool sort = !again && p.AsWritten && !inc.IsOpen && must && maxRows > 0 && (st.OutOfOrder || AddsEarlier(st, fresh, inc));
        if (again)
        {
            (fresh, more) = StartAgain(ev, st, maxRows);
            if ((fresh.Count == 0 && inc.IsOpen) || maxRows <= 0)
            {
                // An open incident's first part holds no event to start it with, or this write brings no rows: the file
                // stays as it is, every row of it, and gets no Note row; its start again waits for a write of rows.
                (st.StartsAgain, st.Pending) = (fresh.Count > 0 || more, fresh.Count > 0 || more);
                return AppendRows(inc, path, st, [], null);
            }
        }
        else if (sort) (fresh, more) = StartAgain(ev, st, maxRows);
        // An open incident's first part is full and all in the file: a last row says how many events the file holds, from
        // when and to when: the time of the first event left out of the first part, wherever the window starts now (the
        // file has every event the incident had from its first row to there, and none after). Never on a file with no
        // event row.
        var (from, lostTo) = p.AsWritten && !again ? (st.From?.Utc, st.LostToUtc) : (ev.FromUtc, ev.LostToUtc);
        int rows = (p.AsWritten && !again ? st.EventRows : 0) + fresh.Count;
        (DateTime At, string Line)? note = null;
        if (inc.IsOpen && st.NotedAt is null && !more && rows > 0 && ev.CutMs is { } cutMs && cutMs <= ev.EndMs)
            note = (ev.CutUtc!.Value, NoteLine(ev.CutUtc.Value, from, lostTo, rows));
        if (!p.AsWritten || again || sort)
        {
            // Not as this run last wrote it (or not there), starting again, or put in time order: written whole, the
            // first part's rows so far.
            var bytes = Utf8.GetBytes(EventsHeader + Rows(fresh, AlertRows(inc).Values, note?.Line));
            string tmp = _host.CheckWrite(path + ".tmp");
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, _host.CheckWrite(path), overwrite: true);
            Profiler.Wrote(path, bytes.Length);
            st.Events = fresh.Select(RowKey).ToHashSet();
            st.Alerts = AlertRows(inc).Keys.ToHashSet();
            (st.EventRows, st.Length, st.NotedAt, st.Pending) = (fresh.Count, bytes.Length, note?.At, more);
            st.First = Earliest(fresh);
            InOrder(st, fresh, inc);
            Begun(st, ev);
            return bytes.Length;
        }
        long added = AppendRows(inc, path, st, fresh, note);
        st.Pending = more;
        return added;
    }

    /// <summary>The file was begun, or written whole, from the incident's first part as it is now: where that starts, what PoolWatch no longer held from there, and the incident's count then (<see cref="FileState.From"/>).</summary>
    private static void Begun(FileState st, IncidentEventSet ev) =>
        (st.From, st.LostToUtc, st.CarriedAtStart) = (ev.FromUtc is { } from ? (ev.FromMs, from) : null, ev.LostToUtc, ev.Carried);

    /// <summary>The incident's alerts by their row (<see cref="AlertRow"/>).</summary>
    private static Dictionary<AlertRow, Alert> AlertRows(Incident inc)
    {
        var d = new Dictionary<AlertRow, Alert>();
        foreach (var a in inc.Alerts) d.TryAdd(AlertRow.Of(a), a);
        return d;
    }

    /// <summary>
    /// An event's row in events.csv as one number (64-bit FNV-1a of its time, kind, process, parent, image, count and detail),
    /// so the rows written are known without keeping the events: two rows alike in all of those are one row.
    /// </summary>
    internal static long RowKey(LifecycleEvent e)
    {
        ulong h = 14695981039346656037UL;
        void Mix(long v) { for (int i = 0; i < 8; i++, v >>= 8) h = (h ^ (byte)v) * 1099511628211UL; }
        void MixText(string? s)
        {
            Mix(s?.Length ?? -1);
            if (s is not null) foreach (char c in s) h = (((h ^ (byte)c) * 1099511628211UL) ^ (byte)(c >> 8)) * 1099511628211UL;
        }
        Mix(e.TimeUtc.Ticks);
        Mix((long)e.Kind);
        Mix(e.Pid);
        Mix(e.CreateTime);
        Mix(e.ParentPid);
        Mix(e.Count);
        MixText(e.Image);
        MixText(e.Detail);
        return (long)h;
    }

    /// <summary>A hash of the content, to tell an unchanged file from a changed one.</summary>
    private static UInt128 Hash(ReadOnlySpan<byte> content) => System.Buffers.Binary.BinaryPrimitives.ReadUInt128LittleEndian(System.Security.Cryptography.SHA256.HashData(content));

    private (bool written, long bytes) RenderChart(History.History h, Incident inc, int from, int to, string path, FileState st)
    {
        var times = new List<DateTime>();
        var tag = new List<long>();
        var total = new List<long>();
        bool nonPaged = h.TagAt(inc.Tag, to).nonPaged >= h.TagAt(inc.Tag, to).paged;
        for (int i = from; i <= to; i++)
        {
            times.Add(h.Times[i]);
            var (p, np) = h.TagAt(inc.Tag, i);
            tag.Add(p + np);
            total.Add((long)(nonPaged ? h.Totals[i].CommittedNonPaged : h.Totals[i].CommittedPaged));
        }
        var markers = new List<(DateTime, string)> { (inc.OnsetUtc, "onset") };
        if (inc.EndUtc is { } e) markers.Add((e, "end"));
        foreach (var a in inc.Alerts.Take(5)) markers.Add((a.TimeUtc, a.Kind == AlertKind.Release ? "release" : "alert"));
        if (inc.Suspects.FirstOrDefault() is { } top)
        {
            if (top.StartUtc >= times[0]) markers.Add((top.StartUtc, $"{top.Image} start"));
            if (top.ExitUtc is { } x && x <= times[^1]) markers.Add((x, $"{top.Image} exit"));
            if (top.OrphanedUtc is { } o && o >= times[0] && o <= times[^1]) markers.Add((o, $"{top.Image} orphaned"));
        }
        string title = $"{inc.Tag.ToDisplay()} ({_knowledge.Lookup(inc.Tag).Owner})", totalLabel = nonPaged ? "committed non-paged pool" : "committed paged pool";
        // Drawn again only when what it shows changed (a closed incident's chart is drawn once).
        var key = new StringBuilder(title).Append('|').Append(totalLabel);
        for (int i = 0; i < times.Count; i++) key.Append('|').Append(times[i].Ticks).Append(',').Append(tag[i]).Append(',').Append(total[i]);
        foreach (var (t, label) in markers) key.Append('|').Append(t.Ticks).Append(',').Append(label);
        var hash = Hash(Utf8.GetBytes(key.ToString()));
        if (st.Hashes.TryGetValue("timeline.png", out var was) && was == hash && File.Exists(path)) return (true, 0);
        var chart = new TimelineChart(title, times, tag, total, totalLabel, markers);
        string tmp = _host.CheckWrite(path + ".tmp");
        if (!_host.RenderTimeline(chart, tmp)) return (false, 0);
        File.Move(tmp, _host.CheckWrite(path), overwrite: true);
        long length = new FileInfo(path).Length;
        Profiler.Wrote(path, length);
        st.Hashes["timeline.png"] = hash;
        return (true, length);
    }

    /// <summary>
    /// Replaces <paramref name="path"/> with <paramref name="content"/> in one step (temp file + move), unless it holds just
    /// that as this run last wrote it; returns the bytes written (0 when unchanged).
    /// </summary>
    private long Atomic(string path, string content, FileState st)
    {
        var bytes = Utf8.GetBytes(content);
        var hash = Hash(bytes);
        string name = Path.GetFileName(path);
        if (st.Hashes.TryGetValue(name, out var was) && was == hash && File.Exists(path)) return 0;
        string tmp = _host.CheckWrite(path + ".tmp");
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, _host.CheckWrite(path), overwrite: true);
        Profiler.Wrote(path, bytes.Length);
        st.Hashes[name] = hash;
        return bytes.Length;
    }

    /// <summary>Text inside a `code span` (<see cref="MdText.Code"/>): only what would end the span or the line changes.</summary>
    private static string MdCode(string s) => MdText.Code(s);

    /// <summary>
    /// Free text outside a code span (<see cref="MdText.Text"/>): it cannot start a link, an image, a code span, an HTML tag,
    /// a table cell or a character reference, or break the line (a web or e-mail address, an @mention, an issue reference or
    /// a commit hash goes in a code span, and an '&amp;' that could start a reference such as "&amp;commat;" is escaped, so
    /// GitHub.com makes no link, mention or reference of it either); characters a reader may not see (bidi, zero-width, tag
    /// characters, variation selectors) are left out.
    /// </summary>
    private static string Md(string s) => MdText.Text(s);
}

/// <summary>
/// An incident's events at a write. <see cref="Window"/>: the events of the window as it is measured now that PoolWatch
/// holds (what the incident kept and the others still in the shared buffer), in time order, for the reports.
/// <see cref="Kept"/>: the first part of what the incident kept (<see cref="Incident.Events"/>), every event from
/// <see cref="FromMs"/> on that PoolWatch still held as the first part was taken (<see cref="LostToUtc"/>; the window's
/// start as the first part last followed it: a closed incident's, and one whose both ends are wanted, where the window
/// starts now; an open one's up to 10 minutes behind it), in time order, up to
/// <see cref="IncidentWriter.MaxEventRows"/>, for events.csv; <see cref="CutMs"/>, where that part stops (the first event
/// it left out of it), else null; <see cref="Tail"/>: the latest part, the latest <see cref="IncidentWriter.MaxEventRows"/>
/// events from the cut to the window's end, in time order, and <see cref="Gap"/>, every event it left out between the two
/// parts; <see cref="EndMs"/>, the window's end now (events after it are not the window's). All on the clock that only
/// goes forward.
/// </summary>
internal sealed record IncidentEventSet(List<LifecycleEvent> Window, IReadOnlyList<LifecycleEvent> Kept, long FromMs, long EndMs, DateTime? CutUtc, long? CutMs)
{
    /// <summary><see cref="FromMs"/> as a time of day, for the texts that say where the first part starts; null for events given as all there are (<see cref="Of"/>).</summary>
    public DateTime? FromUtc { get; init; }
    /// <summary>
    /// When PoolWatch no longer held every event from <see cref="FromMs"/> on as the first part was taken (its shared
    /// buffer had let some go), the time of day of the latest event it had let go by then: <see cref="Kept"/> is the
    /// earliest of those it still held, and the texts say so; null when it held every one.
    /// </summary>
    public DateTime? LostToUtc { get; init; }
    /// <summary>
    /// <see cref="EndMs"/> as a time of day, when the window has an end of its own (a closed incident's, or the measured
    /// end of an open one's growth, with its minute): for the texts that say where the window ends when the count of
    /// the events left out runs past it. null for events given as all there are (<see cref="Of"/>), and for an open
    /// incident whose growth has not ended (its window runs to now).
    /// </summary>
    public DateTime? EndUtc { get; init; }
    public IReadOnlyList<LifecycleEvent> Tail { get; init; } = [];
    public EventGap Gap { get; init; }
    /// <summary>
    /// The incident let go of its latest part (it can no longer re-open, and its events.csv held both ends as written): what
    /// the file holds of it is final, and <see cref="Gap"/> counts those events now (<see cref="Tail"/> is empty).
    /// </summary>
    public bool LatestReleased { get; init; }
    /// <summary>
    /// Every event an open incident took in, wherever its window ends as it is measured now: its first part, those it
    /// left out, its latest part, and those after the window's end (the measured end lags behind while the growth goes
    /// on, and stands still in the incident's quiet minutes), on a count that goes on when its first part follows the
    /// window's start (<see cref="Carried"/> is what the follows took out of what it holds, added back here). The
    /// difference between two readings is how many events came between them; report.md counts from it how many have
    /// come since an open incident's file was full.
    /// </summary>
    public long TakenIn { get; init; }
    /// <summary>What the incident's follows of its window's start took out of its count so far (<see cref="TakenIn"/> has it added back): a file notes it as it is begun, and counts the events from its own start on from there.</summary>
    public long Carried { get; init; }

    /// <summary>
    /// Events taken as all there are: sorted, the first <see cref="IncidentWriter.MaxEventRows"/> kept, the latest
    /// <see cref="IncidentWriter.MaxEventRows"/> of the rest as the latest part and those between left out.
    /// </summary>
    public static IncidentEventSet Of(List<LifecycleEvent> events)
    {
        const int n = IncidentWriter.MaxEventRows;
        var sorted = events.OrderBy(e => e.Ms).ToList();
        var kept = sorted.Count > n ? sorted.GetRange(0, n) : sorted;
        var cut = sorted.Count > n ? sorted[n] : null;
        var rest = sorted.Count > n ? sorted.GetRange(n, sorted.Count - n) : [];
        int skip = Math.Max(0, rest.Count - n);
        return new(sorted, kept, long.MinValue, long.MaxValue, cut?.TimeUtc, cut?.Ms) { Tail = rest.GetRange(skip, rest.Count - skip), Gap = EventGap.Of(rest, 0, skip), TakenIn = sorted.Count };
    }
}

/// <summary>
/// Events an incident's events.csv leaves out (between the two ends it keeps): how many, and the first and
/// last of their times, on the clock that only goes forward and as times of day. <see cref="Count"/> 0: none.
/// </summary>
internal readonly record struct EventGap(int Count, DateTime FromUtc, DateTime ToUtc, long FromMs, long ToMs)
{
    /// <summary>These and <paramref name="o"/> together.</summary>
    public EventGap With(EventGap o) => o.Count == 0 ? this : Count == 0 ? o
        : new(Count + o.Count, o.FromMs < FromMs ? o.FromUtc : FromUtc, o.ToMs > ToMs ? o.ToUtc : ToUtc, Math.Min(FromMs, o.FromMs), Math.Max(ToMs, o.ToMs));

    /// <summary>One event.</summary>
    public static EventGap One(LifecycleEvent e) => new(1, e.TimeUtc, e.TimeUtc, e.Ms, e.Ms);

    /// <summary><paramref name="count"/> events of a list in time order, from <paramref name="index"/>.</summary>
    public static EventGap Of(List<LifecycleEvent> events, int index, int count)
    {
        if (count <= 0) return default;
        var (first, last) = (events[index], events[index + count - 1]);
        return new(count, first.TimeUtc, last.TimeUtc, first.Ms, last.Ms);
    }
}
