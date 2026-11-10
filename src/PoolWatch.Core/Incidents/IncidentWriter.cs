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
/// window: while the incident is open it is appended to, from the first part of what the incident kept of its window's
/// events (<see cref="IncidentEventSet.Kept"/>: the window's earliest by time from its start, in time order, at most
/// <see cref="MaxEventRows"/>): each event once, the earliest not written yet first, so the file holds every event from its
/// first row to its last, however far the write budget held it back; when the incident's first part is full and the file
/// has all of it, a last row says to when it holds them, and no event row follows it. When the incident closes (and at the
/// end of a run, or as it leaves memory, when it has not been yet) it is replaced whole in one step with both ends: the
/// first part, a row between the two parts saying how many events it left out and between which times (when it left any
/// out), and the latest <see cref="MaxEventRows"/> events of the window (<see cref="IncidentEventSet.Tail"/>), all sorted
/// by time, each event once; once the incident let go of its latest part (<see cref="IncidentEventSet.LatestReleased"/>)
/// that file is final. It is rewritten whole otherwise only when it is not as PoolWatch last wrote it, or when a closed
/// incident's onset moved later than rows it holds (they are then left out, and the rest kept as written). Each write
/// returns the bytes it wrote, for the report folders' budget (<see cref="WriteBudget"/>).
/// </summary>
internal sealed class IncidentWriter
{
    /// <summary>
    /// The most events of each end of an incident's window its events.csv holds (the alerts' rows and the row between the
    /// two ends aside), and that an incident keeps in memory for it: the window's first 1,000 by time from the onset less 2
    /// minutes, about 6 minutes of a busy agentic PC's events (9,400 an hour), more on a quieter one, and its latest 1,000;
    /// with Detailed recording the day's events.csv has every one. An
    /// incident is kept up to 8 hours (open 2, closed 6) and 26 were held at once on the maintainer's PC: 5,000 each held up
    /// to 130,000 events, most of which the write budget could never write. An open incident shares its events with the
    /// 90-minute buffer only for those 90 minutes; past them, and once closed, what it kept is its own: in AgentLoadTests'
    /// world (10 incidents open all day, 20 closed) 5,000 held 3.2 MB more heap at 6 hours than 1,000 (42.1 against 38.9 MB),
    /// and with all 32 incidents held at the cap it would be about 29 MB more (4,000 more events each, at the 225 bytes an
    /// event the world's closed incidents hold), against a heap already over its target. Its latest 1,000 are the newest,
    /// which an open incident shares with the buffer, and a closed one lets them go once it can no longer re-open and its
    /// file holds them (an hour after it closed): <c>BothEndsTests</c> measures what they hold.
    /// </summary>
    public const int MaxEventRows = 1000;

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
        /// <summary>The earliest event it holds, on the clock that only goes forward.</summary>
        public long FirstMs = long.MaxValue;
        /// <summary>It holds no event before this (on the forward clock): where the incident started keeping them, later when a closed incident's onset moved later.</summary>
        public long StartMs = long.MinValue;
        /// <summary>Its last row says the events after that row's time are not in it yet (an open incident's first part is full; its latest come at its close): when, else null.</summary>
        public DateTime? NotedAt;
        /// <summary>A write left rows of the window for later (it may write only so many, or none while the write budget has no room).</summary>
        public bool Pending;
        /// <summary>When the chart, timeline.csv and processes.csv were last written (a full write), on the forward clock; null: never.</summary>
        public long? FullMs;
        /// <summary>The latest time of day of an event row it holds; null: none.</summary>
        public DateTime? LastUtc;
        /// <summary>How many events the incident had kept at the last write of events.csv (it keeps more while open).</summary>
        public int KeptSeen;
        /// <summary>
        /// It holds both ends of the window, written whole (<see cref="TailKey"/>: what it holds of the latest part, and how
        /// many it left out), or, after a last row for rows the write budget never had room for, that row's: null while it
        /// holds the first part only (rows after its last would then come after the latest part).
        /// </summary>
        public (int Rows, long First, long Last, int LeftOut)? Tail;
        /// <summary>The row it holds that says how many events of the window it leaves out and between which times; null: none.</summary>
        public EventGap? Gap;
    }

    /// <summary>
    /// The incident leaves memory: its events.csv is replaced whole with both ends of its window when it does not hold them
    /// (its closing write failed, or the file is gone, say), so a later run keeps them; else, when the write budget never had
    /// room for some of its first part, a last row says how many it leaves out and between which times (only on a file as
    /// this run wrote it: another is left as it is; the day's events.csv has them with Detailed recording, and they are not
    /// recorded in Reports only). Written whatever the budget. Returns the bytes written.
    /// </summary>
    public long WriteLeaving(Incident inc, IncidentEventSet ev)
    {
        string path = Path.Combine(inc.Dir, "events.csv");
        var st = StateOf(inc);
        var p = Plan(inc, ev, st, path);
        if (p.BothEnds)
        {
            if (p.Holds) return 0;
            Directory.CreateDirectory(_host.CheckWrite(inc.Dir));
            return WriteWhole(inc, path, st, p);
        }
        if (!p.AsWritten) return 0; // not as written: left as it is
        if (p.Fresh.Count == 0 || st.NotedAt is not null || st.Tail is not null) return 0;
        var gap = EventGap.Of(p.Fresh, 0, p.Fresh.Count);
        long bytes = AppendRows(inc, path, st, [], null, (gap, LeftByBudget));
        (st.Gap, st.Tail, st.Pending) = (gap, (0, 0, 0, gap.Count), false);
        return bytes;
    }

    /// <summary>
    /// Whether the incident's events.csv is still to be replaced whole with both ends of its window: a closed incident whose
    /// file does not hold them as they are now (its closing write failed, or its window took more in since). The end of a
    /// run writes it then.
    /// </summary>
    public bool BothEndsDue(Incident inc, IncidentEventSet ev)
    {
        if (inc.IsOpen) return false;
        var p = Plan(inc, ev, StateOf(inc), Path.Combine(inc.Dir, "events.csv"));
        return p.BothEnds && !p.Holds;
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Incident, FileState> _state = new();

    private FileState StateOf(Incident inc) => _state.GetValue(inc, _ => new FileState());

    /// <summary>A write of the incident left rows of its window out of events.csv for later (<see cref="AppendEvents"/>).</summary>
    public bool EventsPending(Incident inc) => _state.TryGetValue(inc, out var st) && Pending(inc, st);

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

    /// <summary>When the incident's chart, timeline.csv and processes.csv were last written this run (a full write), on the forward clock; null: never.</summary>
    public long? FullWrittenMs(Incident inc) => _state.TryGetValue(inc, out var st) ? st.FullMs : null;

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
    /// processes.csv stay as they were (<see cref="FullWrittenMs"/>). Returns the bytes written.
    /// </summary>
    /// <param name="atMs">The write's moment on the forward clock, kept as <see cref="FullWrittenMs"/> for a full write.</param>
    /// <param name="must">A write that must be made (an opening, re-opening or closing, a trace attached, the end of a run):
    /// only such a write replaces a closed incident's events.csv whole with both ends, as it is made whatever the budget.</param>
    public long Write(History.History h, Incident inc, IncidentEventSet ev, int maxRows = MaxEventRows, bool full = true, long atMs = 0, bool must = true)
    {
        var events = ev.Window;
        _history = h;
        _plainFor = null;
        var st = StateOf(inc);
        Directory.CreateDirectory(_host.CheckWrite(inc.Dir));
        var info = _knowledge.Lookup(inc.Tag);
        var details = inc.Suspects.Take(5).ToDictionary(s => s.Key, s => _host.DescribeFile(s.ImagePath));
        _sessions = inc.Suspects.Take(5).ToDictionary(s => s.Key, s => _host.DescribeSession(s));
        var system = _host.GetSystemInfo();
        var w = inc.Window;
        int from = w is null ? 0 : w.FromIndex(h);
        int to = w is null ? h.Count - 1 : Math.Max(from, (inc.ClosedUtc is { } closed ? w.AfterIndex(h, closed, w.ToMs is null ? null : inc.ClosedMs) : w.AfterIndex(h)) - 1);
        to = Math.Min(to, h.Count - 1);

        bool png = File.Exists(Path.Combine(inc.Dir, "timeline.png"));
        long bytes = 0;
        if (full)
        {
            png = false;
            if (h.Count > 1 && to > from)
            {
                try { (png, long pngBytes) = RenderChart(h, inc, from, to, Path.Combine(inc.Dir, "timeline.png"), st); bytes += pngBytes; }
                catch (Exception ex) { _host.Log($"incident {inc.Id}: chart failed: {ex.Message}"); }
            }
            bytes += WriteTimelineCsv(h, inc, from, to, st);
            bytes += WriteProcessesCsv(h, inc, from, to, st);
            st.FullMs = atMs;
        }
        bytes += WriteEventsCsv(inc, ev, st, maxRows, must);
        var report = BuildJson(inc, info, details, system, events);
        bytes += Atomic(Path.Combine(inc.Dir, "report.json"), JsonSerializer.Serialize(report, Json), st);
        bytes += Atomic(Path.Combine(inc.Dir, "report.md"), BuildMarkdown(inc, info, details, system, ev, png, st), st);
        return bytes;
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
        string csv = BothEndsRows(inc, p.Head, p.Tail, p.Gap, out var rows, out var keys).ToString();
        // That file as a whole write would leave this run's knowledge of it.
        var exported = new FileState { Events = keys, EventRows = rows.Count, Gap = p.Gap.Count > 0 ? p.Gap : null, Tail = p.Tail.Count > 0 || p.Gap.Count > 0 ? p.Key : null };
        _history = h;
        _plainFor = null;
        var info = _knowledge.Lookup(inc.Tag);
        var details = inc.Suspects.Take(5).ToDictionary(s => s.Key, s => _host.DescribeFile(s.ImagePath));
        _sessions = inc.Suspects.Take(5).ToDictionary(s => s.Key, s => _host.DescribeSession(s));
        bool png = File.Exists(Path.Combine(inc.Dir, "timeline.png"));
        return (csv, BuildMarkdown(inc, info, details, _host.GetSystemInfo(), ev, png, exported));
    }

    /// <summary><see cref="AppendEvents(Incident, IncidentEventSet, int)"/> with <paramref name="events"/> as all the window's events there are.</summary>
    public long AppendEvents(Incident inc, List<LifecycleEvent> events, int maxRows) => AppendEvents(inc, IncidentEventSet.Of(events), maxRows);

    /// <summary>
    /// The incident in one line, the top suspect's confidence in the words of Pool causes' Confidence column
    /// (<see cref="PlainWords.Confidence"/>), as everywhere in report.md: "...; top suspect Leaker.exe PID 7716, started
    /// 03:46:08 (strong evidence)". A report an earlier build wrote keeps its older words ("(strongly correlated)"): every
    /// reader takes the headline as written, and reads a suspect's confidence only from report.json's <c>confidence</c> name.
    /// </summary>
    /// <param name="markdown">Escape free text for report.md (report.json keeps the raw headline).</param>
    public string Headline(Incident inc, TagInfo info, bool markdown = false)
    {
        string owner = markdown ? Md(Owner(info)) : Owner(info);
        string tag = $"`{inc.Tag.ToDisplay()}` ({owner})";
        string what = inc.Kind switch
        {
            IncidentKind.Release => inc.Release is { } r
                ? $"{tag} dropped {Fmt.Bytes(r.Dropped)} at {Fmt.Clock(r.TimeUtc)}"
                : $"{tag} dropped sharply",
            IncidentKind.NonPagedTotal => $"Non-paged pool over {Fmt.Bytes(inc.Alerts.FirstOrDefault()?.Bytes ?? 0)} and rising; fastest-growing tag {tag} " +
                                          $"grew {Fmt.Bytes(inc.Growth)} in {Fmt.Duration(inc.GrowthSpan)}",
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

    private static string Owner(TagInfo info) =>
        string.IsNullOrEmpty(info.Description) ? info.Owner : $"{info.Owner} {info.Description}";

    private object BuildJson(Incident inc, TagInfo info, Dictionary<ProcessKey, FileDetails?> details, SystemInfo system, List<LifecycleEvent> events) => new
    {
        notice = Exporter.UnredactedNotice, // The file as written is not for sharing as it is
        id = inc.Id,
        kind = inc.Kind,
        tag = inc.Tag.ToString(),
        tagHex = inc.Tag.Raw.ToString("x8"),
        owner = info.Owner,
        description = info.Description,
        ownerSource = info.Source,
        fileSystemTag = _knowledge.IsFileSystemTag(inc.Tag),
        headline = Headline(inc, info),
        plainAnswer = PlainOwners(inc)?.Line,
        allocationShape = Shape(inc) is { During: var sh, Before: var before } ? new
        {
            allocsPerMin = Math.Round(sh.AllocsPerMin, 1), freesPerMin = Math.Round(sh.FreesPerMin, 1), shape = sh.Shape.ToString(), text = sh.Text,
            before = before is { } b ? new { allocsPerMin = Math.Round(b.AllocsPerMin, 1), freesPerMin = Math.Round(b.FreesPerMin, 1) } : null,
        } : null,
        systemAtEnd = SystemAtEnd() is { } sc && sc.CommitLimitBytes > 0 ? new { commitBytes = sc.CommitBytes, commitLimitBytes = sc.CommitLimitBytes, availableBytes = sc.AvailableBytes, compressionWsBytes = sc.CompressionWsBytes, handles = sc.Handles, processes = sc.Processes } : null,
        openedUtc = inc.OpenedUtc,
        closedUtc = inc.ClosedUtc,
        onsetUtc = inc.OnsetUtc,
        endUtc = inc.EndUtc,
        startBytes = inc.StartBytes,
        peakBytes = inc.PeakBytes,
        currentBytes = inc.CurrentBytes,
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

    private string BuildMarkdown(Incident inc, TagInfo info, Dictionary<ProcessKey, FileDetails?> details, SystemInfo system, IncidentEventSet ev, bool png,
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
            sb.AppendLine($"- Growth: {Fmt.Bytes(inc.StartBytes)} -> {Fmt.Bytes(inc.PeakBytes)} (+{Fmt.Bytes(inc.Growth)}) from {Fmt.Clock(inc.OnsetUtc)}" +
                          (inc.EndUtc is { } e ? $" to {Fmt.Clock(e)}" : " (still climbing at the last sample)") + $"; now {Fmt.Bytes(inc.CurrentBytes)}" +
                          (inc.OwnClearsFreed is { } own ? $", after {own} of it" : ""));
        if (Shape(inc) is { During: { Shape: not AllocShape.Idle } shape, Before: var before })
            sb.AppendLine($"- Allocation shape {ShapeSpan(inc)}: {shape.AllocsPerMin:N0} allocations/min, {shape.FreesPerMin:N0} frees/min" +
                          (before is { } b ? $", against {b.AllocsPerMin:N0} and {b.FreesPerMin:N0} a minute just before it" : "") + $": {shape.Text}");
        if (SystemAtEnd() is { CommitLimitBytes: > 0 } sys)
            sb.AppendLine($"- System at the last sample: commit {Fmt.Bytes((long)sys.CommitBytes)} of {Fmt.Bytes((long)sys.CommitLimitBytes)}, {Fmt.Bytes((long)sys.AvailableBytes)} RAM available, " +
                          $"compression store {Fmt.Bytes((long)sys.CompressionWsBytes)}, {sys.Handles:N0} handles, {sys.Processes:N0} processes");
        foreach (var a in inc.Alerts)
            sb.AppendLine($"- Alert {Fmt.Clock(a.TimeUtc)}: {a.Rule} ({Fmt.SignedBytes(a.Kind == AlertKind.Release ? -a.Bytes : a.Bytes)}, onset {Fmt.Clock(a.OnsetUtc)})");
        if (inc.Retention is { } ret) sb.AppendLine($"- **Retained**: {ret.Note}.");
        if (inc.Release is { } rel)
            sb.AppendLine($"- **Released**: {Fmt.Bytes(rel.FromBytes)} -> {Fmt.Bytes(rel.ToBytes)} between {Fmt.Clock(rel.FromUtc)} and {Fmt.Clock(rel.TimeUtc)}" +
                          (rel.ClearedBytes > 0 ? $"; {Fmt.OwnClearsFreed(rel.ClearedBytes, rel.ClearedAtUtc)} of it and {Fmt.Bytes(rel.Dropped)} fell on its own." : "."));
        sb.AppendLine("- Sizes are the tag's own usage from SystemPoolTagInformation; Task Manager's committed pool figures run higher (fragmentation, lazy release).");
        sb.AppendLine();

        if (png) sb.AppendLine("![timeline](timeline.png)").AppendLine();

        // In plain words: the trace when there is one, else the suspects grouped by who they work for.
        var owners = PlainOwners(inc);
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
                                  (o.StartsInWindow > 0 ? $"; {o.StartsInWindow:N0} process starts during the growth" : "") + ".");
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
                          (s.ExitUtc is { } x ? $"; exited {Fmt.Stamp(x)}{(s.ExitCode is { } code ? $" (code {code})" : "")}" : "; still running") +
                          (s.OrphanedUtc is { } o ? $"; orphaned {Fmt.Stamp(o)}" : ""));
            sb.AppendLine();
        }

        sb.AppendLine("## Events in the window").AppendLine();
        var shown = events.Where(e => e.Kind != LifecycleKind.Start || inc.Suspects.Any(s => s.Key == e.Key)).Take(50).ToList();
        if (shown.Count == 0) sb.AppendLine("None recorded.");
        foreach (var e in shown)
            sb.AppendLine($"- {Fmt.Clock(e.TimeUtc)} {e.Kind} {Md(e.Image)} (PID {e.Pid}){(e.ExitCode is { } c2 ? $" code {c2}" : "")}{(e.Detail is { } d ? $": {Md(d)}" : "")}");
        sb.AppendLine(OtherEventsLine(inc, ev, shown, st)).AppendLine();

        AppendSystem(sb, system);
        AppendBugReport(sb, inc, info, details, system, headline);
        return sb.ToString();
    }

    /// <summary>
    /// report.md's line under the window's events: how many more events.csv holds than those listed (its rows counted as
    /// written, those listed among them aside), and what it leaves out: when it holds both ends, how many of the window's
    /// events it leaves out between them and between which times, as its <c>Gap</c> row says; while the incident is open and
    /// its first part is full, how many more the window has so far, whose latest are added at its close; while the write
    /// budget holds rows back, what the file gets and when (an open incident's: the rest of its first part as the budget
    /// allows, its latest part at its close). Empty when there is nothing more to say. Starts with a line break, for the blank
    /// line before it.
    /// </summary>
    private string OtherEventsLine(Incident inc, IncidentEventSet ev, List<LifecycleEvent> shown, FileState st)
    {
        int inFile = st.Events is { } written ? Math.Max(0, st.EventRows - shown.Count(e => written.Contains(RowKey(e)))) : 0;
        if (st.Gap is { } g)
            return $"\n({inFile} other events in events.csv; it leaves out {g.Count:N0} events, between {Fmt.Clock(g.FromUtc)} and {Fmt.Clock(g.ToUtc)}, as its Gap row says)";
        if (inc.IsOpen && st.Tail is null && st.NotedAt is { } noted)
            return $"\n({inFile} other events in events.csv: while the incident is open it holds the window's first {MaxEventRows:N0} events, to {Fmt.Clock(noted)}; " +
                   $"the window has {ev.AfterCut:N0} more so far, and the latest of them, up to {MaxEventRows:N0}, are added when it closes)";
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
            return inc.IsOpen
                ? $"\n({more} other events in the window; events.csv gets the rest of the window's first {MaxEventRows:N0} events as PoolWatch's write budget allows " +
                  $"and its latest {MaxEventRows:N0} when the incident closes{day})"
                : $"\n({more} other events in the window; events.csv gets the rest when PoolWatch's write budget has room{day})";
        }
        return inFile > 0 ? $"\n({inFile} other events in events.csv)" : "";
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

    /// <summary>"during the growth", or "from 10:42 (...)" when the counters cover only part of it.</summary>
    private string ShapeSpan(Incident inc)
    {
        if (_history is not { Count: > 1 } h) return "during the growth";
        int i0 = h.IndexAtOrAfterMs(inc.OnsetMs);
        int first = h.FirstStoredIndex(inc.Tag, i0, h.Count - 1);
        if (first < 0) return "during the growth";
        var from = h.Times[first];
        if (h.Ms[0] > inc.OnsetMs) return $"from {Fmt.Clock(from)} (the history no longer holds the start of the growth)";
        return h.Ms[first] - inc.OnsetMs > 30_000 ? $"from {Fmt.Clock(from)} (the tag was too small to keep its counters before)" : "during the growth";
    }

    private SystemCounters? SystemAtEnd() => _history is { Count: > 0 } h ? h.System[^1] : null;
    /// <summary>The answer from the confirmation trace, or null without a trace that holds the tag's growth.</summary>
    private static string? TraceAnswer(Incident inc, TagInfo info) =>
        inc.Capture is { } c && inc.TraceOwners is { Count: > 0 } o ? TraceAttribution.Answer(inc, info, o, c) : null;

    private static string Short(string s, int n) => s.Length > n ? s[..n] + "…" : s;

    /// <summary>The incident's suspects grouped by owner with the one-line answer (null without a window or history).</summary>
    private TagAnswer? PlainOwners(Incident inc)
    {
        if (inc.Window is not { } w || _history is not { } h || inc.Suspects.Count == 0) return null;
        if (_plainFor == inc && _plain is not null) return _plain; // computed once per write (report.json and report.md)
        try
        {
            var owners = inc.Owners ?? OwnerRanking.Rank(h, inc.Suspects, inc.Tag, w, inc.Growth, s => Identity.ProcessIdentities.Of(_registry.Ancestry(s.Key) is { Count: > 0 } c ? c : [new ProcessRecord { Key = s.Key, Image = s.Image }]));
            var info = _knowledge.Lookup(inc.Tag);
            var span = TimeSpan.FromMilliseconds((inc.EndMs ?? inc.ClosedMs ?? w.ToMs ?? inc.LastAlertMs) - inc.OnsetMs);
            var (paged, nonPaged) = h.TagAt(inc.Tag, h.Count - 1);
            _plainFor = inc;
            return _plain = OwnerRanking.Answer(info, inc.Tag, paged, nonPaged, inc.Growth, Fmt.Duration(span), owners);
        }
        catch { return null; }
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
    /// </summary>
    private void AppendBugReport(StringBuilder sb, Incident inc, TagInfo info, Dictionary<ProcessKey, FileDetails?> details, SystemInfo s, string headline)
    {
        var top = inc.Suspects.FirstOrDefault();
        var f = top is null ? null : details.GetValueOrDefault(top.Key);
        string vendor = f?.Company ?? f?.Signer ?? top?.Image ?? info.Owner;
        sb.AppendLine($"## Draft bug report for {Md(vendor)}").AppendLine();
        sb.AppendLine("**Summary**").AppendLine();
        sb.AppendLine(top is null
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
        sb.AppendLine($"3. Watch kernel pool tag `{inc.Tag.ToDisplay()}` (poolmon, or the attached timeline.csv).");
        sb.AppendLine();
        sb.AppendLine("**Expected**: pool usage returns to its baseline when the work finishes.").AppendLine();
        sb.AppendLine($"**Actual**: {headline}.").AppendLine();
        sb.AppendLine("**Attached evidence**: report.md, report.json, timeline.csv (tag and pool totals), processes.csv (suspects' counters), events.csv (process lifecycle)" +
                      (File.Exists(Path.Combine(inc.Dir, "timeline.png")) ? ", timeline.png" : "") + ".");
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
    /// incident closes leaves the file as it is), with what PoolWatch does with it at the close.
    /// </summary>
    private static string NoteLine(DateTime t) => Csv.Line(t, "Note", 0, 0, 0, "", null, null, null,
        string.Create(CultureInfo.InvariantCulture, $"events.csv holds the incident window's first {MaxEventRows:N0} events, to this row's time; the window's later events are not in this file ") +
        string.Create(CultureInfo.InvariantCulture, $"(when the incident closes PoolWatch replaces it with the first {MaxEventRows:N0}, a Gap row and the latest {MaxEventRows:N0})"));

    /// <summary>Why a <c>Gap</c> row's events are not in the file: the window's two ends are kept, or the write budget never had room for them.</summary>
    private static readonly string LeftBetweenEnds = string.Create(CultureInfo.InvariantCulture,
            $"events.csv keeps the incident window's first {MaxEventRows:N0} events and its latest {MaxEventRows:N0}"),
        LeftByBudget = "PoolWatch's write budget had no room for them before the incident left memory";

    /// <summary>
    /// The row that says how many events the file leaves out, and the first and last of their times: at the first one's
    /// time, so it sorts where they begin (events.csv's time format in its detail).
    /// </summary>
    private static string GapLine(EventGap g, string why) => Csv.Line(g.FromUtc, "Gap", 0, 0, 0, "", null, null, null,
        string.Create(CultureInfo.InvariantCulture, $"{g.Count:N0} events left out, between {Csv.Escape(g.FromUtc)} and {Csv.Escape(g.ToUtc)} ({why})"));

    /// <summary>
    /// Rows of events and alerts, sorted by time (the alerts' rows among the events'), each ending a line; a <c>Gap</c> row in
    /// its place by time (after rows of the same time); the note row last.
    /// </summary>
    private string Rows(IEnumerable<LifecycleEvent> events, IEnumerable<Alert> alerts, DateTime? noteAt, (EventGap Gap, string Why)? gap = null) =>
        RowsInto(new StringBuilder(), events, alerts, noteAt, gap).ToString();

    /// <summary><see cref="Rows"/> appended to <paramref name="sb"/>: sorted first, each row made as it is appended.</summary>
    private StringBuilder RowsInto(StringBuilder sb, IEnumerable<LifecycleEvent> events, IEnumerable<Alert> alerts, DateTime? noteAt, (EventGap Gap, string Why)? gap = null)
    {
        // Each row as what it is (an event, an alert or the Gap row), in time order (stable: an alert after the events of its
        // moment, the Gap row after both).
        IEnumerable<(DateTime TimeUtc, LifecycleEvent? Event, Alert? Alert)> rows =
            events.Select(e => (e.TimeUtc, (LifecycleEvent?)e, (Alert?)null)).Concat(alerts.Select(a => (a.TimeUtc, (LifecycleEvent?)null, (Alert?)a)));
        if (gap is { } g) rows = rows.Append((g.Gap.FromUtc, null, null));
        foreach (var (_, e, a) in rows.OrderBy(x => x.TimeUtc))
            sb.AppendLine(e is not null ? EventLine(e) : a is not null ? AlertLine(a) : GapLine(gap!.Value.Gap, gap.Value.Why));
        if (noteAt is { } n) sb.AppendLine(NoteLine(n));
        return sb;
    }

    /// <summary>
    /// What events.csv should hold of the window now, against what it holds (<see cref="WriteEventsCsv"/>,
    /// <see cref="BothEndsDue"/>, <see cref="WriteLeaving"/>): its first part (<see cref="Head"/>: <see cref="Written"/> in
    /// the file as written, <see cref="Fresh"/> not yet) and its latest part (<see cref="Tail"/>), each from
    /// <see cref="Lower"/> to the window's end, and what it leaves out between them (<see cref="Gap"/>); whether it is to hold
    /// both ends (<see cref="BothEnds"/>: a closed incident with events after its first part, or whose file holds a note or
    /// both ends already), and whether it does as written (<see cref="Holds"/>).
    /// </summary>
    private sealed record EventsPlan(bool AsWritten, long Lower, bool Rewrite, List<LifecycleEvent> Head, List<LifecycleEvent> Written, List<LifecycleEvent> Fresh,
        List<LifecycleEvent> Tail, EventGap Gap, bool BothEnds, bool Holds, (int Rows, long First, long Last, int LeftOut) Key);

    /// <param name="ends">Both ends are wanted whatever the incident (an export of an open one): an open incident's plan
    /// otherwise holds only what an append reads (<see cref="EventsPlan.Fresh"/>, and what a rewrite of its first part
    /// does), not its two parts.</param>
    /// <param name="freshMax">An open incident's plan stops at this many events not in the file and one more (a write adds
    /// at most this many; the one more says some are left for later).</param>
    private EventsPlan Plan(Incident inc, IncidentEventSet ev, FileState st, string path, bool ends = false, int freshMax = int.MaxValue)
    {
        bool asWritten = st.Events is not null && File.Exists(path) && new FileInfo(path).Length == st.Length;
        // The rows start at what the incident kept from; a closed incident's at its window's start (the onset less 2 min),
        // and never earlier than they did (a re-opened incident's file does not get back the rows a close left out). A
        // re-opened one's window starts at its latest episode's onset: its file keeps the first part of its first episode.
        long lower = Math.Max(ev.FromMs, asWritten ? st.StartMs : ev.FromMs);
        if (!inc.IsOpen && inc.Reopened == 0) lower = Math.Max(lower, IncidentManager.EventWindowStartMs(inc));
        // Once the incident let go of its latest part (it can no longer re-open), its file as written is final: a later onset
        // does not write it again without the latest part.
        bool released = ev.LatestReleased && !inc.IsOpen;
        bool rewrite = !asWritten || (st.FirstMs < lower && !released);
        ends |= !inc.IsOpen;
        var head = new List<LifecycleEvent>(ends ? ev.Kept.Count : 0);
        var written = new List<LifecycleEvent>(ends || rewrite ? ev.Kept.Count : 0);
        var fresh = new List<LifecycleEvent>();
        foreach (var e in ev.Kept)
        {
            if (e.Ms < lower || e.Ms > ev.EndMs) continue;
            if (ends) head.Add(e);
            if (!asWritten || !st.Events!.Contains(RowKey(e)))
            {
                fresh.Add(e);
                if (!ends && fresh.Count > freshMax) break;
            }
            else if (ends || rewrite) written.Add(e);
        }
        var tail = new List<LifecycleEvent>(ends ? ev.Tail.Count : 0);
        if (ends) foreach (var e in ev.Tail) if (e.Ms >= lower && e.Ms <= ev.EndMs) tail.Add(e);
        bool bothEnds = !inc.IsOpen && (released || tail.Count > 0 || ev.Gap.Count > 0 || (asWritten && (st.NotedAt is not null || st.Tail is not null)));
        var key = released && asWritten && st.Tail is { } final ? final : TailKey(tail, ev.Gap);
        bool holds = bothEnds && asWritten && !rewrite && fresh.Count == 0 && st.Tail == key;
        return new(asWritten, lower, rewrite, head, written, fresh, tail, ev.Gap, bothEnds, holds, key);
    }

    /// <summary>What a file holds of the latest part, as one value: how many rows, the first and last (<see cref="RowKey"/>), and how many it leaves out.</summary>
    private static (int Rows, long First, long Last, int LeftOut) TailKey(List<LifecycleEvent> tail, EventGap gap) =>
        (tail.Count, tail.Count > 0 ? RowKey(tail[0]) : 0, tail.Count > 0 ? RowKey(tail[^1]) : 0, gap.Count);

    /// <summary>
    /// Both ends of the window as rows: the first part, then, when it left any out, the <c>Gap</c> row, then the latest part,
    /// each part in time order on the clock that only goes forward (as the incident keeps them), each event once (by
    /// <see cref="RowKey"/>), and the incident's alerts among them by time (after the events of their moment; before the Gap
    /// row when of its moment or earlier). The Gap row is placed by position, between the two parts, not by its time alone:
    /// an event of the latest part with the Gap row's time stays after it, and a clock set back between the two parts does not
    /// mix them. <paramref name="events"/>: the events written.
    /// </summary>
    private StringBuilder BothEndsRows(Incident inc, List<LifecycleEvent> head, List<LifecycleEvent> tail, EventGap gap, out List<LifecycleEvent> events, out HashSet<long> keys)
    {
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
                sb.AppendLine(GapLine(gap, LeftBetweenEnds));
            }
            if (i == events.Count) break;
            while (next < alerts.Count && alerts[next].TimeUtc < events[i].TimeUtc) sb.AppendLine(AlertLine(alerts[next++]));
            sb.AppendLine(EventLine(events[i]));
        }
        while (next < alerts.Count) sb.AppendLine(AlertLine(alerts[next++]));
        return sb;
    }

    /// <summary>Replaces events.csv whole in one step (temp file and move) with both ends of the window (<see cref="BothEndsRows"/>); returns the bytes written.</summary>
    private long WriteWhole(Incident inc, string path, FileState st, EventsPlan p)
    {
        var gap = p.Gap;
        var text = BothEndsRows(inc, p.Head, p.Tail, gap, out var rows, out var keys);
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
        (st.EventRows, st.Length, st.StartMs, st.NotedAt, st.Pending) = (rows.Count, length, p.Lower, null, false);
        st.FirstMs = rows.Count > 0 ? rows.Min(e => e.Ms) : long.MaxValue;
        st.LastUtc = rows.Count > 0 ? rows.Max(e => e.TimeUtc) : null;
        // A file with no latest part and nothing left out is the first part again: later rows can follow it.
        bool ends = p.Tail.Count > 0 || gap.Count > 0;
        st.Tail = ends ? p.Key : null;
        st.Gap = gap.Count > 0 ? gap : null;
        return length;
    }

    /// <summary>
    /// Appends whole rows in one write at the end of the file: <paramref name="fresh"/> events, the alerts' rows not in it yet,
    /// and a note or <c>Gap</c> row; returns the bytes written (0: nothing to add).
    /// </summary>
    private long AppendRows(Incident inc, string path, FileState st, List<LifecycleEvent> fresh, DateTime? noteAt, (EventGap Gap, string Why)? gap = null)
    {
        var alerts = AlertRows(inc).Where(kv => !st.Alerts!.Contains(kv.Key)).ToList();
        if (fresh.Count == 0 && alerts.Count == 0 && noteAt is null && gap is null) return 0;
        var add = Utf8.GetBytes(Rows(fresh, alerts.Select(kv => kv.Value), noteAt, gap));
        long length;
        using (var fs = new FileStream(_host.CheckWrite(path), FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            fs.Write(add);
            length = fs.Length;
        }
        Profiler.Wrote(path, add.Length);
        foreach (var e in fresh)
        {
            st.Events!.Add(RowKey(e));
            st.FirstMs = Math.Min(st.FirstMs, e.Ms);
            if (st.LastUtc is not { } l || e.TimeUtc > l) st.LastUtc = e.TimeUtc;
        }
        foreach (var (k, _) in alerts) st.Alerts!.Add(k);
        st.EventRows += fresh.Count;
        st.Length = length;
        if (noteAt is not null) st.NotedAt = noteAt;
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
    /// whole file is written again otherwise only when it is not as this run last wrote it (or not there), or when the
    /// incident is closed and its onset moved later than rows it holds (those rows are then left out, and the rows after them
    /// stay as written: all of them are among the kept events). Events left for a later write make it pending
    /// (<see cref="EventsPending"/>).
    /// </summary>
    private long WriteEventsCsv(Incident inc, IncidentEventSet ev, FileState st, int maxRows, bool must)
    {
        string path = Path.Combine(inc.Dir, "events.csv");
        var p = Plan(inc, ev, st, path, freshMax: maxRows);
        if (!p.AsWritten) (st.StartMs, st.NotedAt, st.Tail, st.Gap) = (ev.FromMs, null, null, null);
        st.KeptSeen = ev.Kept.Count;
        if (p.BothEnds)
        {
            st.Pending = false;
            if (!p.Holds && must) return WriteWhole(inc, path, st, p);
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
        // writes the file whole.
        if (inc.IsOpen && st.NotedAt is not null && !p.Rewrite) fresh = [];
        bool more = fresh.Count > maxRows;
        if (more) fresh = fresh.GetRange(0, maxRows);
        // An open incident's first part is full and all in the file: a last row says to when (not before the window).
        DateTime? note = null;
        if (inc.IsOpen && st.NotedAt is null && !more && ev.CutMs is { } cutMs && cutMs <= ev.EndMs)
        {
            var onsetLess2 = inc.OnsetUtc.AddMinutes(-2);
            note = ev.CutUtc!.Value > onsetLess2 ? ev.CutUtc.Value : onsetLess2;
        }
        st.Pending = more;
        if (p.Rewrite)
        {
            var rows = p.Written.Concat(fresh).OrderBy(e => e.Ms).ToList();
            note ??= st.NotedAt;
            var bytes = Utf8.GetBytes(EventsHeader + Rows(rows, AlertRows(inc).Values, note));
            string tmp = _host.CheckWrite(path + ".tmp");
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, _host.CheckWrite(path), overwrite: true);
            Profiler.Wrote(path, bytes.Length);
            st.Events = rows.Select(RowKey).ToHashSet();
            st.Alerts = AlertRows(inc).Keys.ToHashSet();
            (st.EventRows, st.Length, st.StartMs, st.NotedAt) = (rows.Count, bytes.Length, p.Lower, note);
            st.FirstMs = rows.Count > 0 ? rows[0].Ms : long.MaxValue;
            st.LastUtc = rows.Count > 0 ? rows.Max(e => e.TimeUtc) : null;
            return bytes.Length;
        }
        return AppendRows(inc, path, st, fresh, note);
    }

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
/// An incident's events at a write. <see cref="Window"/>: the window's events PoolWatch holds (what the incident kept and the
/// others still in the shared buffer), in time order, for the reports. <see cref="Kept"/>: the first part of what the
/// incident kept of them (<see cref="Incident.Events"/>), every event from <see cref="FromMs"/> on, in time order, up to
/// <see cref="IncidentWriter.MaxEventRows"/>, for events.csv; <see cref="CutMs"/>, where that part stops (the first event
/// it left out of it), else null; <see cref="Tail"/>: the latest part, the latest <see cref="IncidentWriter.MaxEventRows"/>
/// events from the cut to the window's end, in time order, and <see cref="Gap"/>, those of the window it left out between
/// the two parts; <see cref="EndMs"/>, the window's end now (events after it are not the window's). All on the clock that
/// only goes forward.
/// </summary>
internal sealed record IncidentEventSet(List<LifecycleEvent> Window, IReadOnlyList<LifecycleEvent> Kept, long FromMs, long EndMs, DateTime? CutUtc, long? CutMs)
{
    public IReadOnlyList<LifecycleEvent> Tail { get; init; } = [];
    public EventGap Gap { get; init; }
    /// <summary>
    /// The incident let go of its latest part (it can no longer re-open, and its events.csv held both ends as written): what
    /// the file holds of it is final, and <see cref="Gap"/> counts those events now (<see cref="Tail"/> is empty).
    /// </summary>
    public bool LatestReleased { get; init; }
    /// <summary>The window's events after its first part: the latest part and those left out.</summary>
    public int AfterCut => Tail.Count + Gap.Count;

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
        return new(sorted, kept, long.MinValue, long.MaxValue, cut?.TimeUtc, cut?.Ms) { Tail = rest.GetRange(skip, rest.Count - skip), Gap = EventGap.Of(rest, 0, skip) };
    }
}

/// <summary>
/// Events of an incident's window left out of its events.csv (between the two ends it keeps): how many, and the first and
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
