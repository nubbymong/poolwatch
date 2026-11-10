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
/// unchanged file, a closed incident's CSVs and chart among them, is not written again). events.csv is appended to, from
/// what the incident kept of its window's events (<see cref="IncidentEventSet.Kept"/>: every event from the window's
/// start in order, at most <see cref="MaxEventRows"/>): each event once, the earliest not written yet first, so the file
/// holds every event from its first row to its last, however far the write budget held it back; when the incident stopped
/// keeping events (it kept <see cref="MaxEventRows"/>) and the file has all it kept, a last row says up to when it holds
/// them. It is rewritten whole only when it is not as PoolWatch last wrote it, or when a closed incident's onset moved
/// later than rows it holds (they are then left out, and the rest kept as written). Each write returns the bytes it wrote,
/// for the report folders' budget (<see cref="WriteBudget"/>).
/// </summary>
internal sealed class IncidentWriter
{
    /// <summary>
    /// The most event rows an incident's events.csv holds (the alerts' rows aside), and the most events an incident keeps in
    /// memory for it: the window's first 1,000 from the onset less 2 minutes, about 6 minutes of a busy agentic PC's events
    /// (9,400 an hour), more on a quieter one. Its last row says to when it holds them; the day's events.csv has every one.
    /// An incident is kept up to 8 hours (open 2, closed 6) and 26 were held at once on the maintainer's PC: 5,000 each held
    /// up to 130,000 events, most of which the write budget could never write. An open incident shares its events with the
    /// 90-minute buffer only for those 90 minutes; past them, and once closed, what it kept is its own: in AgentLoadTests'
    /// world (10 incidents open all day, 20 closed) 5,000 held 3.2 MB more heap at 6 hours than 1,000 (42.1 against 38.9 MB),
    /// and with all 32 incidents held at the cap it would be about 29 MB more (4,000 more events each, at the 225 bytes an
    /// event the world's closed incidents hold), against a heap already over its target.
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
        /// <summary>Its last row says the events after that row's time are not in it (the incident stopped keeping them): when, else null.</summary>
        public DateTime? NotedAt;
        /// <summary>A write left rows of the window for later (it may write only so many, or none while the write budget has no room).</summary>
        public bool Pending;
        /// <summary>When the chart, timeline.csv and processes.csv were last written (a full write), on the forward clock; null: never.</summary>
        public long? FullMs;
        /// <summary>The latest time of day of an event row it holds; null: none.</summary>
        public DateTime? LastUtc;
        /// <summary>How many events the incident had kept at the last write of events.csv (it keeps more while open).</summary>
        public int KeptSeen;
    }

    /// <summary>
    /// The incident leaves memory with kept events its events.csv does not have yet (the write budget held them back): a
    /// last row says to when the file holds the window's events (the rest are in the day's events.csv). A row, written
    /// whatever the budget. Returns the bytes written.
    /// </summary>
    public long NoteUnwritten(Incident inc)
    {
        if (!_state.TryGetValue(inc, out var st) || !Pending(inc, st)) return 0;
        string path = Path.Combine(inc.Dir, "events.csv");
        if (!File.Exists(path) || new FileInfo(path).Length != st.Length) return 0; // not as written: left as it is
        var onsetLess2 = inc.OnsetUtc.AddMinutes(-2);
        var at = st.LastUtc is { } l && l > onsetLess2 ? l : onsetLess2;
        var add = Utf8.GetBytes(NoteLine(at) + Environment.NewLine);
        using (var fs = new FileStream(_host.CheckWrite(path), FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            fs.Write(add);
            st.Length = fs.Length;
        }
        Profiler.Wrote(path, add.Length);
        (st.NotedAt, st.Pending) = (at, false);
        return add.Length;
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Incident, FileState> _state = new();

    private FileState StateOf(Incident inc) => _state.GetValue(inc, _ => new FileState());

    /// <summary>A write of the incident left rows of its window out of events.csv for later (<see cref="AppendEvents"/>).</summary>
    public bool EventsPending(Incident inc) => _state.TryGetValue(inc, out var st) && Pending(inc, st);

    /// <summary>Kept events not in events.csv yet: a write left some for later, or the incident kept more since its last write.</summary>
    private static bool Pending(Incident inc, FileState st) => st.Events is not null && st.NotedAt is null && (st.Pending || inc.Events.Count != st.KeptSeen);

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
    /// events.csv yet (the rest left for a later write, <see cref="EventsPending"/>). Without <paramref name="full"/> (an
    /// open incident's refresh, its opening) only report.json and report.md (what the window's details and Copy summary
    /// read) and the events: the chart, timeline.csv and processes.csv stay as they were (<see cref="FullWrittenMs"/>).
    /// Returns the bytes written.
    /// </summary>
    /// <param name="atMs">The write's moment on the forward clock, kept as <see cref="FullWrittenMs"/> for a full write.</param>
    public long Write(History.History h, Incident inc, IncidentEventSet ev, int maxRows = MaxEventRows, bool full = true, long atMs = 0)
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
        bytes += WriteEventsCsv(inc, ev, st, maxRows);
        var report = BuildJson(inc, info, details, system, events);
        bytes += Atomic(Path.Combine(inc.Dir, "report.json"), JsonSerializer.Serialize(report, Json), st);
        bytes += Atomic(Path.Combine(inc.Dir, "report.md"), BuildMarkdown(inc, info, details, system, events, png, st), st);
        return bytes;
    }

    /// <summary>Appends up to <paramref name="maxRows"/> of the kept events not written yet to events.csv alone (rows a write left for later); returns the bytes written.</summary>
    public long AppendEvents(Incident inc, IncidentEventSet ev, int maxRows)
    {
        Directory.CreateDirectory(_host.CheckWrite(inc.Dir));
        return WriteEventsCsv(inc, ev, StateOf(inc), maxRows);
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

    private string BuildMarkdown(Incident inc, TagInfo info, Dictionary<ProcessKey, FileDetails?> details, SystemInfo system, List<LifecycleEvent> events, bool png,
        FileState st)
    {
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
        int more = events.Count - shown.Count;
        // events.csv holds the window's events from its start as written so far: to the time its last row says (the incident
        // kept MaxEventRows), or fewer while the write budget has kept new rows for later. The day's data\<yyyyMMdd>\events.csv
        // has every one.
        string where = st.NotedAt is { } noted ? $"; events.csv holds the window's events to {Fmt.Clock(noted)} and the day's `data\\<yyyyMMdd>\\events.csv` every one"
            : st.Pending ? "; events.csv gets the rest when PoolWatch's write budget has room, and the day's `data\\<yyyyMMdd>\\events.csv` has every one" : "";
        sb.AppendLine(more > 0 ? (where.Length == 0 ? $"\n({more} other events in events.csv)" : $"\n({more} other events in the window{where})") : "").AppendLine();

        AppendSystem(sb, system);
        AppendBugReport(sb, inc, info, details, system, headline);
        return sb.ToString();
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

    /// <summary>The last row of an events.csv whose incident stopped keeping events: the file has none after <paramref name="t"/>.</summary>
    private static string NoteLine(DateTime t) => Csv.Line(t, "Note", 0, 0, 0, "", null, null, null,
        $"events.csv holds the incident window's events to this row's time (an incident keeps at most {MaxEventRows:N0}); the later ones are in the day's data\\<yyyyMMdd>\\events.csv");

    /// <summary>Rows of events and alerts, sorted by time (the alerts' rows among the events'), each ending a line; the note row last.</summary>
    private string Rows(IEnumerable<LifecycleEvent> events, IEnumerable<Alert> alerts, DateTime? noteAt)
    {
        var rows = events.Select(e => (e.TimeUtc, line: EventLine(e))).Concat(alerts.Select(a => (a.TimeUtc, line: AlertLine(a)))).OrderBy(x => x.TimeUtc);
        var sb = new StringBuilder();
        foreach (var (_, line) in rows) sb.AppendLine(line);
        if (noteAt is { } n) sb.AppendLine(NoteLine(n));
        return sb.ToString();
    }

    /// <summary>
    /// events.csv: up to <paramref name="maxRows"/> of the kept events not in it yet (<see cref="IncidentEventSet.Kept"/>, the
    /// earliest first: the file holds every kept event from its first row to its last) and the alert rows not in it yet
    /// (<see cref="AlertRow"/>), appended as whole rows; once it holds everything the incident kept before it stopped keeping
    /// them, a last row says to when. The whole file is written again only when it is not as this run last wrote it (or not
    /// there), or when the incident is closed and its onset moved later than rows it holds (those rows are then left out,
    /// and the rows after them stay as written: all of them are among the kept events). Events left for a later write make
    /// it pending (<see cref="EventsPending"/>).
    /// </summary>
    private long WriteEventsCsv(Incident inc, IncidentEventSet ev, FileState st, int maxRows)
    {
        string path = Path.Combine(inc.Dir, "events.csv");
        bool asWritten = st.Events is not null && File.Exists(path) && new FileInfo(path).Length == st.Length;
        if (!asWritten) (st.StartMs, st.NotedAt) = (ev.FromMs, null);
        // The rows start at what the incident kept from; a closed incident's at its window's start (the onset less 2 min),
        // and never earlier than they did (a re-opened incident's file does not get back the rows a close left out).
        long lower = Math.Max(ev.FromMs, st.StartMs);
        if (!inc.IsOpen) lower = Math.Max(lower, IncidentManager.EventWindowStartMs(inc));
        bool rewrite = !asWritten || st.FirstMs < lower;
        var kept = new List<LifecycleEvent>();
        var fresh = new List<LifecycleEvent>();
        bool more = false;
        foreach (var e in ev.Kept)
        {
            if (e.Ms < lower || e.Ms > ev.EndMs) continue;
            if (asWritten && st.Events!.Contains(RowKey(e))) { if (rewrite) kept.Add(e); continue; }
            if (fresh.Count >= maxRows) { more = true; break; }
            fresh.Add(e);
        }
        // Everything kept before the incident stopped keeping events is in the file: a last row says to when (not before the window).
        DateTime? note = st.NotedAt;
        bool noting = false;
        if (note is null && !more && ev.CutMs is { } cutMs && cutMs <= ev.EndMs)
        {
            var onsetLess2 = inc.OnsetUtc.AddMinutes(-2);
            note = ev.CutUtc!.Value > onsetLess2 ? ev.CutUtc.Value : onsetLess2;
            noting = true;
        }
        st.Pending = more;
        st.KeptSeen = ev.Kept.Count;
        if (rewrite)
        {
            var rows = kept.Concat(fresh).OrderBy(e => e.Ms).ToList();
            var bytes = Utf8.GetBytes(EventsHeader + Rows(rows, AlertRows(inc).Values, note));
            string tmp = _host.CheckWrite(path + ".tmp");
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, _host.CheckWrite(path), overwrite: true);
            Profiler.Wrote(path, bytes.Length);
            st.Events = rows.Select(RowKey).ToHashSet();
            st.Alerts = AlertRows(inc).Keys.ToHashSet();
            (st.EventRows, st.Length, st.StartMs, st.NotedAt) = (rows.Count, bytes.Length, lower, note);
            st.FirstMs = rows.Count > 0 ? rows[0].Ms : long.MaxValue;
            st.LastUtc = rows.Count > 0 ? rows.Max(e => e.TimeUtc) : null;
            return bytes.Length;
        }
        var alerts = AlertRows(inc).Where(kv => !st.Alerts!.Contains(kv.Key)).ToList();
        if (fresh.Count == 0 && alerts.Count == 0 && !noting) return 0;
        var add = Utf8.GetBytes(Rows(fresh, alerts.Select(kv => kv.Value), noting ? note : null));
        long length;
        // Whole rows in one write at the end of the file.
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
        st.NotedAt = note;
        return add.Length;
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
/// newest still in the shared buffer), in time order, for the reports. <see cref="Kept"/>: what the incident kept of them
/// (<see cref="Incident.Events"/>), every event from <see cref="FromMs"/> on, in time order, up to
/// <see cref="IncidentWriter.MaxEventRows"/>, for events.csv; <see cref="CutMs"/>, when it stopped keeping them (the
/// first event it left out), else null; <see cref="EndMs"/>, the window's end now (events after it are not the window's).
/// All on the clock that only goes forward.
/// </summary>
internal sealed record IncidentEventSet(List<LifecycleEvent> Window, IReadOnlyList<LifecycleEvent> Kept, long FromMs, long EndMs, DateTime? CutUtc, long? CutMs)
{
    /// <summary>Events taken as all there are: sorted, the first <see cref="IncidentWriter.MaxEventRows"/> kept and the rest cut.</summary>
    public static IncidentEventSet Of(List<LifecycleEvent> events)
    {
        var sorted = events.OrderBy(e => e.Ms).ToList();
        var kept = sorted.Count > IncidentWriter.MaxEventRows ? sorted.GetRange(0, IncidentWriter.MaxEventRows) : sorted;
        var cut = sorted.Count > IncidentWriter.MaxEventRows ? sorted[IncidentWriter.MaxEventRows] : null;
        return new(sorted, kept, long.MinValue, long.MaxValue, cut?.TimeUtc, cut?.Ms);
    }
}
