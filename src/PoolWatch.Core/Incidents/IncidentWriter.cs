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
/// timeline.png. Files are replaced atomically (temp file + move) so a reader never sees half a report.
/// </summary>
internal sealed class IncidentWriter
{
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

    public void Write(History.History h, Incident inc, List<LifecycleEvent> events)
    {
        _history = h;
        _plainFor = null;
        Directory.CreateDirectory(_host.CheckWrite(inc.Dir));
        var info = _knowledge.Lookup(inc.Tag);
        var details = inc.Suspects.Take(5).ToDictionary(s => s.Key, s => _host.DescribeFile(s.ImagePath));
        _sessions = inc.Suspects.Take(5).ToDictionary(s => s.Key, s => _host.DescribeSession(s));
        var system = _host.GetSystemInfo();
        var w = inc.Window;
        int from = w is null ? 0 : h.IndexAtOrAfter(w.FromUtc);
        int to = w is null ? h.Count - 1 : Math.Max(from, h.IndexAtOrAfter((inc.ClosedUtc ?? w.ToUtc).AddTicks(1)) - 1);
        to = Math.Min(to, h.Count - 1);

        bool png = false;
        if (h.Count > 1 && to > from)
        {
            try { png = RenderChart(h, inc, from, to, Path.Combine(inc.Dir, "timeline.png")); }
            catch (Exception ex) { _host.Log($"incident {inc.Id}: chart failed: {ex.Message}"); }
        }
        WriteTimelineCsv(h, inc, from, to);
        WriteProcessesCsv(h, inc, from, to);
        WriteEventsCsv(inc, events);
        var report = BuildJson(inc, info, details, system, events);
        Atomic(Path.Combine(inc.Dir, "report.json"), JsonSerializer.Serialize(report, Json));
        Atomic(Path.Combine(inc.Dir, "report.md"), BuildMarkdown(inc, info, details, system, events, png));
    }

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
                                          $"grew {Fmt.Bytes(inc.Growth)} in {Fmt.Duration((inc.EndUtc ?? inc.ClosedUtc ?? inc.LastAlertUtc) - inc.OnsetUtc)}",
            _ => $"{tag} grew {Fmt.Bytes(inc.Growth)} in {Fmt.Duration((inc.EndUtc ?? inc.ClosedUtc ?? inc.LastAlertUtc) - inc.OnsetUtc)}",
        };
        if (inc.TraceOwners is { Count: > 0 } to)
            return $"{what}; the confirmation trace shows {(markdown ? Md(to[0].Owner.Name) : to[0].Owner.Name)} allocating {Fmt.Bytes(to[0].Net)} of it";
        var top = inc.Suspects.FirstOrDefault();
        if (top is null) return what + "; no suspect stood out";
        var parts = new List<string> { $"{top.DisplayName} PID {top.Key.Pid}" };
        if (top.IsOrphan) parts.Add("orphaned");
        if (top.StartUtc > DateTime.MinValue) parts.Add($"started {Fmt.Clock(top.StartUtc)}");
        if (_sessions.GetValueOrDefault(top.Key)?.Transcript is { } t)
            parts[^1] += $" by Claude Code session {Short(t.SessionId)}" + (t.WorkflowId is { } wf ? $" / workflow {wf}" : "") + (t.SubagentId is { } sa && t.WorkflowId is null ? $" / sub-agent {Short(sa)}" : "");
        return $"{what}; top suspect {string.Join(", ", parts)} ({ConfidenceText(top.Confidence)})";
    }

    private static string Short(string id) => id.Length > 8 ? id[..8] : id;

    private static string Owner(TagInfo info) =>
        string.IsNullOrEmpty(info.Description) ? info.Owner : $"{info.Owner} {info.Description}";

    public static string ConfidenceText(Confidence c) => c switch
    {
        Confidence.ConfirmedByTrace => "confirmed by trace",
        Confidence.StronglyCorrelated => "strongly correlated",
        Confidence.Correlated => "correlated",
        _ => "weak signal",
    };

    private object BuildJson(Incident inc, TagInfo info, Dictionary<ProcessKey, FileDetails?> details, SystemInfo system, List<LifecycleEvent> events) => new
    {
        notice = Exporter.UnredactedNotice, // M1: the file as written is not for sharing as it is
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
        allocationShape = Shape(inc) is { } sh ? new { allocsPerMin = Math.Round(sh.AllocsPerMin, 1), freesPerMin = Math.Round(sh.FreesPerMin, 1), shape = sh.Shape.ToString(), text = sh.Text } : null,
        systemAtEnd = SystemAtEnd() is { } sc && sc.CommitLimitBytes > 0 ? new { commitBytes = sc.CommitBytes, commitLimitBytes = sc.CommitLimitBytes, availableBytes = sc.AvailableBytes, compressionWsBytes = sc.CompressionWsBytes, handles = sc.Handles, processes = sc.Processes } : null,
        openedUtc = inc.OpenedUtc,
        closedUtc = inc.ClosedUtc,
        onsetUtc = inc.OnsetUtc,
        endUtc = inc.EndUtc,
        startBytes = inc.StartBytes,
        peakBytes = inc.PeakBytes,
        currentBytes = inc.CurrentBytes,
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

    private string BuildMarkdown(Incident inc, TagInfo info, Dictionary<ProcessKey, FileDetails?> details, SystemInfo system, List<LifecycleEvent> events, bool png)
    {
        var sb = new StringBuilder();
        string headline = Headline(inc, info, markdown: true);
        sb.AppendLine($"# {headline}").AppendLine();
        sb.AppendLine($"> {Exporter.UnredactedNotice}").AppendLine(); // M1
        sb.AppendLine($"- Incident `{inc.Id}` ({inc.Kind}{(inc.IsOpen ? ", still open" : "")}), opened {Fmt.Stamp(inc.OpenedUtc)}" +
                      (inc.ClosedUtc is { } c ? $", closed {Fmt.Stamp(c)}" : ""));
        sb.AppendLine($"- Tag `{inc.Tag}` (raw 0x{inc.Tag.Raw:x8}; tags are case-sensitive): owner **{Md(info.Owner)}**" +
                      (info.Description.Length > 0 ? $", {Md(info.Description)}" : "") + $" (source: {info.Source})");
        if (inc.Kind != IncidentKind.Release)
            sb.AppendLine($"- Growth: {Fmt.Bytes(inc.StartBytes)} -> {Fmt.Bytes(inc.PeakBytes)} (+{Fmt.Bytes(inc.Growth)}) from {Fmt.Clock(inc.OnsetUtc)}" +
                          (inc.EndUtc is { } e ? $" to {Fmt.Clock(e)}" : " (still climbing at the last sample)") + $"; now {Fmt.Bytes(inc.CurrentBytes)}");
        if (Shape(inc) is { Shape: not AllocShape.Idle } shape)
            sb.AppendLine($"- Allocation shape {ShapeSpan(inc)}: {shape.AllocsPerMin:N0} allocations/min, {shape.FreesPerMin:N0} frees/min: {shape.Text}");
        if (SystemAtEnd() is { CommitLimitBytes: > 0 } sys)
            sb.AppendLine($"- System at the last sample: commit {Fmt.Bytes((long)sys.CommitBytes)} of {Fmt.Bytes((long)sys.CommitLimitBytes)}, {Fmt.Bytes((long)sys.AvailableBytes)} RAM available, " +
                          $"compression store {Fmt.Bytes((long)sys.CompressionWsBytes)}, {sys.Handles:N0} handles, {sys.Processes:N0} processes");
        foreach (var a in inc.Alerts)
            sb.AppendLine($"- Alert {Fmt.Clock(a.TimeUtc)}: {a.Rule} ({Fmt.SignedBytes(a.Kind == AlertKind.Release ? -a.Bytes : a.Bytes)}, onset {Fmt.Clock(a.OnsetUtc)})");
        if (inc.Retention is { } ret) sb.AppendLine($"- **Retained**: {ret.Note}.");
        if (inc.Release is { } rel) sb.AppendLine($"- **Released**: {Fmt.Bytes(rel.FromBytes)} -> {Fmt.Bytes(rel.ToBytes)} between {Fmt.Clock(rel.FromUtc)} and {Fmt.Clock(rel.TimeUtc)}.");
        sb.AppendLine("- Sizes are the tag's own usage from SystemPoolTagInformation; Task Manager's committed pool figures run higher (fragmentation, lazy release).");
        sb.AppendLine();

        if (png) sb.AppendLine("![timeline](timeline.png)").AppendLine();

        // In plain words: the trace when there is one (2a), else the suspects grouped by who they work for.
        var owners = PlainOwners(inc);
        string? traced = TraceAnswer(inc, info);
        if (traced is not null || owners is not null)
        {
            sb.AppendLine("## In plain words").AppendLine();
            sb.AppendLine(Md(traced ?? owners!.Line)).AppendLine();
            if (inc.TraceOwners is { Count: > 0 } tos)
            {
                foreach (var o in tos.Take(3))
                    sb.AppendLine($"- **{Md(o.Owner.Name)}** (confirmed by trace): {Fmt.SignedBytes(o.Net)} net in {o.Processes.Count:N0} process(es), " +
                                  $"{o.Allocs:N0} allocations, {o.Frees:N0} frees" + (o.PerShortCommand > 0 ? $"; about {Fmt.Bytes(o.PerShortCommand)} kept per short command ({o.ShortOnes.Count:N0} traced)" : "") +
                                  (o.StartsInWindow > 0 ? $"; {o.StartsInWindow:N0} process starts during the growth" : "") + ".");
                var corr = owners?.Owners.Where(o => o.Meaningful && !tos.Any(t => t.Owner.Key == o.Owner.Key)).Take(3).ToList() ?? [];
                if (corr.Count > 0)
                {
                    sb.AppendLine().AppendLine("Correlation only (no allocation in the trace):").AppendLine();
                    foreach (var o in corr) sb.AppendLine($"- {Md(o.Name)} ({OwnerRanking.Strength(o.Confidence)}, correlation only): {Md(string.Join("; ", o.Plain))}");
                }
            }
            else if (owners is { } pw)
                foreach (var o in pw.Owners.Where(o => o.Meaningful).Take(3))
                    sb.AppendLine($"- **{Md(o.Name)}** ({OwnerRanking.Strength(o.Confidence)}): {Md(string.Join("; ", o.Plain))}. {Md(o.Summary)}");
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
                sb.AppendLine($"| {s.Rank} | {Md(s.DisplayName)}{(s.IsOrphan ? " (orphan)" : "")} | {s.Key.Pid} | {ConfidenceText(s.Confidence)} | {s.Score:0.00} | {Md(string.Join("; ", s.Evidence))} |");
            sb.AppendLine();
        }

        if (inc.Release is { } r)
        {
            sb.AppendLine("## Release").AppendLine();
            sb.AppendLine($"`{inc.Tag.ToDisplay()}` fell {Fmt.Bytes(r.Dropped)} ({Fmt.Bytes(r.FromBytes)} -> {Fmt.Bytes(r.ToBytes)}) between {Fmt.Clock(r.FromUtc)} and {Fmt.Clock(r.TimeUtc)}. " +
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
            sb.AppendLine($"- Executable: `{s.ImagePath ?? s.Image}`");
            if (details.GetValueOrDefault(s.Key) is { } f)
            {
                sb.AppendLine($"- File version: {f.FileVersion ?? "n/a"}; company: {f.Company ?? "n/a"}; product: {f.Product ?? "n/a"}");
                sb.AppendLine($"- Signature: {f.SignatureStatus}{(f.Signer is not null ? $", signer {f.Signer}" : "")}");
            }
            sb.AppendLine($"- Command line: `{(s.CommandLine ?? "(not captured)").Replace("`", "'")}`");
            if (s.User is not null) sb.AppendLine($"- User: {_host.DisplayUser(s.User)}");
            sb.AppendLine($"- Parent chain: {Md(string.Join(" › ", s.Ancestry))}");
            if (_sessions.GetValueOrDefault(s.Key) is { } sd)
            {
                sb.AppendLine($"- AICC / Claude Code: {Md(sd.Chain)}");
                if (sd.Context?.ConfigDir is { } cfg) sb.AppendLine($"- Claude config: `{cfg}`");
                if (sd.Transcript is { } t)
                    sb.AppendLine($"- Issued by Claude Code session `{t.SessionId}` in project `{t.Project}`" +
                                  (t.WorkflowId is { } wf ? $", workflow `{wf}`" : "") + (t.SubagentId is { } sa ? $", sub-agent `{sa}`" : "") +
                                  (t.TimestampUtc is { } ts ? $" at {Fmt.Stamp(ts)}" : "") + $" (`{MdCode(t.Command)}` in `{Path.GetFileName(t.File)}`)");
                else if (sd.SearchPending) sb.AppendLine("- Claude Code transcript search: in progress (the report is rewritten when it finishes)");
                else sb.AppendLine("- Claude Code transcript search: no matching tool call found within the search window");
            }
            if (s.Services.Count > 0) sb.AppendLine($"- Services: {string.Join(", ", s.Services)}");
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
        sb.AppendLine(more > 0 ? $"\n({more} other events in events.csv)" : "").AppendLine();

        AppendSystem(sb, system);
        AppendBugReport(sb, inc, info, details, system, headline);
        return sb.ToString();
    }

    private static void AppendCapture(StringBuilder sb, Incident inc, CaptureResult c)
    {
        sb.AppendLine("## Confirmation trace").AppendLine();
        if (c.Error is not null) sb.AppendLine($"The trace failed: {Md(c.Error)}").AppendLine();
        if (c.EtlPath is not null)
            sb.AppendLine($"- Recorded {Fmt.Clock(c.StartUtc)}-{Fmt.Clock(c.EndUtc)} ({Fmt.Duration(c.EndUtc - c.StartUtc)}) with wpr.exe; saved as `{Path.GetFileName(c.EtlPath)}`; " +
                          $"tags {string.Join(", ", c.Tags.Select(t => $"`{t.ToDisplay()}`"))}.");
        if (c.PoolEventsParsed)
            sb.AppendLine($"- Parsed with {c.Library}: {c.PoolEvents:N0} pool events ({c.StackedPoolEvents:N0} with stacks), " +
                          (c.KernelFileRecorded ? $"{c.FileEvents:N0} Kernel-File events." : "Kernel-File not recorded."));
        foreach (var n in c.Notes) sb.AppendLine($"- {Md(n)}");
        sb.AppendLine();
        if (c.PoolEventsParsed && inc.Traced is { Count: > 0 } traced)
        {
            // 2b: every traced process with who it was: identity, command line, parent chain and owner (PID + create time).
            sb.AppendLine($"Net `{inc.Tag.ToDisplay()}` bytes each process allocated during the trace (allocations minus frees of those allocations):").AppendLine();
            sb.AppendLine("| Process | PID | Started | Owner | Net | Allocs | Frees | Command line | Parent chain |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
            foreach (var t in traced.OrderByDescending(t => t.Usage.NetBytes).Take(30))
                sb.AppendLine($"| {Md(t.Name)} | {t.Usage.Pid} | {(t.StartUtc is { } st ? Fmt.Clock(st) : "before the trace")} | {Md(t.Identity.Owner.Name)} | " +
                              $"{Fmt.SignedBytes(t.Usage.NetBytes)} | {t.Usage.Allocs:N0} | {t.Usage.Frees:N0} | `{MdCode(Short(t.CommandLine ?? "(not captured)", 120))}` | {Md(string.Join(" › ", t.Chain))} |");
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
            sb.AppendLine(Md(cpu.Summary())).AppendLine();
            sb.AppendLine("| Module | CPU (ms) | Share | | Waited in | Wait (ms) | Switches |").AppendLine("|---|---:|---:|---|---|---:|---:|");
            for (int i = 0; i < Math.Max(Math.Min(10, cpu.TopCpu.Count), Math.Min(10, cpu.TopWait.Count)); i++)
            {
                var m = i < cpu.TopCpu.Count ? cpu.TopCpu[i] : null;
                var w = i < cpu.TopWait.Count ? cpu.TopWait[i] : null;
                sb.AppendLine($"| {(m is null ? "" : Md(m.Module))} | {(m is null ? "" : $"{m.Ms:N0}")} | {(m is null ? "" : $"{m.Share:P1}")} | | {(w is null ? "" : Md(w.Module))} | {(w is null ? "" : $"{w.WaitMs:N0}")} | {(w is null ? "" : $"{w.Switches:N0}")} |");
            }
            if (cpu.TopDpcIsr.Count > 0) sb.AppendLine().AppendLine("DPC / ISR time by module: " + string.Join(", ", cpu.TopDpcIsr.Select(d => $"{Md(d.Module)} {d.Ms:N0} ms")));
            // Addendum 2 C: the top kernel functions by CPU and by wait (named from local symbols, else module + offset).
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
            if (cpu.FunctionsNote.Length > 0) sb.AppendLine().AppendLine(Md(cpu.FunctionsNote));
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

    /// <summary>Allocation / free rates of the tag over the growth episode (null when the history does not hold it).</summary>
    private TagRates? Shape(Incident inc)
    {
        if (_history is not { } h || h.Count < 2) return null;
        int i0 = h.IndexAtOrAfter(inc.OnsetUtc), i1 = Math.Min(h.Count - 1, h.IndexAtOrAfter((inc.EndUtc ?? inc.ClosedUtc ?? h.Times[^1]).AddTicks(1)) - 1);
        if (i1 <= i0) return null;
        var r = h.TagRatesBetween(inc.Tag, i0, i1);
        // The same span (at most 10 min) just before the onset: the tag's background churn to compare against.
        var span = TimeSpan.FromMinutes(Math.Min(10, (h.Times[i1] - h.Times[i0]).TotalMinutes));
        int b0 = h.IndexAtOrAfter(inc.OnsetUtc - span);
        if (b0 >= 0 && i0 - b0 >= 2)
        {
            var baseline = h.TagRatesBetween(inc.Tag, b0, i0);
            r = r with { Shape = LeakShapes.Classify(r.AllocsPerMin, r.FreesPerMin, baseline) };
        }
        return r;
    }

    /// <summary>"during the growth", or "from 10:42 (...)" when the counters cover only part of it.</summary>
    private string ShapeSpan(Incident inc)
    {
        if (_history is not { Count: > 1 } h) return "during the growth";
        int i0 = h.IndexAtOrAfter(inc.OnsetUtc);
        int first = h.FirstStoredIndex(inc.Tag, i0, h.Count - 1);
        if (first < 0) return "during the growth";
        var from = h.Times[first];
        if (h.Times[0] > inc.OnsetUtc) return $"from {Fmt.Clock(from)} (the history no longer holds the start of the growth)";
        return from - inc.OnsetUtc > TimeSpan.FromSeconds(30) ? $"from {Fmt.Clock(from)} (the tag was too small to keep its counters before)" : "during the growth";
    }

    private SystemCounters? SystemAtEnd() => _history is { Count: > 0 } h ? h.System[^1] : null;
    /// <summary>The answer from the confirmation trace (2a), or null without a trace that holds the tag's growth.</summary>
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
            var span = (inc.EndUtc ?? inc.ClosedUtc ?? w.ToUtc) - inc.OnsetUtc;
            var (paged, nonPaged) = h.TagAt(inc.Tag, h.Count - 1);
            _plainFor = inc;
            return _plain = OwnerRanking.Answer(info, inc.Tag, paged, nonPaged, inc.Growth, Fmt.Duration(span), owners);
        }
        catch { return null; }
    }

    private static void AppendSystem(StringBuilder sb, SystemInfo s)
    {
        sb.AppendLine("## System").AppendLine();
        sb.AppendLine($"- Machine: {s.Machine}; {s.OsDescription} (build {s.OsBuild}); {s.LogicalCpus} logical CPUs; {Fmt.Bytes(s.RamBytes)} RAM" +
                      (s.BootUtc is { } b ? $"; booted {Fmt.Stamp(b)}" : ""));
        foreach (var v in s.Volumes)
            sb.AppendLine($"- Volume {v.Root} {v.FileSystem}{(v.IsDevDrive == true ? " (Dev Drive)" : "")} {v.DriveType}{(v.Label.Length > 0 ? $" \"{v.Label}\"" : "")}");
        sb.AppendLine($"- Antivirus: {s.AntiVirus}");
        if (s.PoolWatchCost is { } cost) sb.AppendLine($"- {Md(cost)}");
        if (s.FilterDrivers.Count > 0)
        {
            sb.AppendLine("- File-system filter drivers (`fltmc filters`):").AppendLine();
            sb.AppendLine("```");
            foreach (var l in s.FilterDrivers) sb.AppendLine(l);
            sb.AppendLine("```");
        }
        sb.AppendLine();
    }

    private void AppendBugReport(StringBuilder sb, Incident inc, TagInfo info, Dictionary<ProcessKey, FileDetails?> details, SystemInfo s, string headline)
    {
        var top = inc.Suspects.FirstOrDefault();
        var f = top is null ? null : details.GetValueOrDefault(top.Key);
        string vendor = f?.Company ?? f?.Signer ?? top?.Image ?? info.Owner;
        sb.AppendLine($"## Draft bug report for {vendor}").AppendLine();
        sb.AppendLine("**Summary**").AppendLine();
        sb.AppendLine(top is null
            ? $"Kernel pool tag `{inc.Tag.ToDisplay()}` ({info.Owner}) grows abnormally; no single process stood out."
            : $"While {top.Image}{(f?.FileVersion is { } fv ? $" {fv}" : "")} runs, kernel pool tag `{inc.Tag.ToDisplay()}` ({Md(Owner(info))}) grows by {Fmt.Bytes(inc.Growth)} " +
              $"in {Fmt.Duration((inc.EndUtc ?? inc.ClosedUtc ?? inc.LastAlertUtc) - inc.OnsetUtc)}" +
              (inc.Retention is not null ? " and is not released after the activity stops" : inc.Release is not null ? "; the memory comes back only when the work is undone" : "") + ".");
        sb.AppendLine();
        sb.AppendLine("**Environment**").AppendLine();
        sb.AppendLine($"- {s.OsDescription}, build {s.OsBuild}, {s.LogicalCpus} logical CPUs, {Fmt.Bytes(s.RamBytes)} RAM");
        foreach (var v in s.Volumes) sb.AppendLine($"- {v.Root} {v.FileSystem}{(v.IsDevDrive == true ? " Dev Drive" : "")}");
        sb.AppendLine($"- Antivirus: {s.AntiVirus}; filter drivers: {(s.FilterDrivers.Count > 0 ? "see the System section" : "unknown")}");
        if (top is not null) sb.AppendLine($"- Process: `{top.ImagePath ?? top.Image}`{(f?.FileVersion is { } v2 ? $", version {v2}" : "")}");
        sb.AppendLine();
        sb.AppendLine("**Steps to reproduce** (template: fill in)").AppendLine();
        sb.AppendLine($"1. Start {(top?.Image ?? "<process>")}{(top?.CommandLine is { } cl ? $" as `{cl.Replace("`", "'")}`" : "")}.");
        sb.AppendLine("2. <what it was doing: the workload, files or folders involved>.");
        sb.AppendLine($"3. Watch kernel pool tag `{inc.Tag.ToDisplay()}` (poolmon, or the attached timeline.csv).");
        sb.AppendLine();
        sb.AppendLine("**Expected**: pool usage returns to its baseline when the work finishes.").AppendLine();
        sb.AppendLine($"**Actual**: {headline}.").AppendLine();
        sb.AppendLine("**Attached evidence**: report.md, report.json, timeline.csv (tag and pool totals), processes.csv (suspects' counters), events.csv (process lifecycle)" +
                      (File.Exists(Path.Combine(inc.Dir, "timeline.png")) ? ", timeline.png" : "") + ".");
    }

    private void WriteTimelineCsv(History.History h, Incident inc, int from, int to)
    {
        var sb = new StringBuilder("time,tag_bytes,tag_paged_bytes,tag_nonpaged_bytes,committed_paged_bytes,committed_nonpaged_bytes,tagsum_paged_bytes,tagsum_nonpaged_bytes\n");
        for (int i = from; i <= to && i < h.Count; i++)
        {
            var (p, np) = h.TagAt(inc.Tag, i);
            var t = h.Totals[i];
            sb.AppendLine(Csv.Line(h.Times[i], p + np, p, np, t.CommittedPaged, t.CommittedNonPaged, t.TagSumPaged, t.TagSumNonPaged));
        }
        Atomic(Path.Combine(inc.Dir, "timeline.csv"), sb.ToString());
    }

    private void WriteProcessesCsv(History.History h, Incident inc, int from, int to)
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
        Atomic(Path.Combine(inc.Dir, "processes.csv"), sb.ToString());
    }

    private void WriteEventsCsv(Incident inc, List<LifecycleEvent> events)
    {
        var sb = new StringBuilder("time,kind,pid,create_time,parent_pid,image,exit_code,user,command_line,detail\n");
        var rows = events.TakeLast(5000).Select(e => (e.TimeUtc, line: Csv.Line(e.TimeUtc, e.Kind.ToString(), e.Pid, e.CreateTime, e.ParentPid, e.Image, e.ExitCode,
            e.User is null ? null : _host.DisplayUser(e.User), e.CommandLine, e.Detail)))
            .Concat(inc.Alerts.Select(a => (a.TimeUtc, line: Csv.Line(a.TimeUtc, a.Kind == AlertKind.Release ? "Release" : "Alert", 0, 0, 0, "", null, null, null,
                $"{a.Tag.ToDisplay()} {a.Rule} ({Fmt.SignedBytes(a.Kind == AlertKind.Release ? -a.Bytes : a.Bytes)})"))))
            .OrderBy(x => x.TimeUtc);
        foreach (var (_, line) in rows) sb.AppendLine(line);
        Atomic(Path.Combine(inc.Dir, "events.csv"), sb.ToString());
    }

    private bool RenderChart(History.History h, Incident inc, int from, int to, string path)
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
        var chart = new TimelineChart($"{inc.Tag.ToDisplay()} ({_knowledge.Lookup(inc.Tag).Owner})", times, tag, total,
            nonPaged ? "committed non-paged pool" : "committed paged pool", markers);
        string tmp = _host.CheckWrite(path + ".tmp");
        if (!_host.RenderTimeline(chart, tmp)) return false;
        File.Move(tmp, _host.CheckWrite(path), overwrite: true);
        Profiler.Wrote(path, new FileInfo(path).Length);
        return true;
    }

    private void Atomic(string path, string content)
    {
        string tmp = _host.CheckWrite(path + ".tmp");
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        File.Move(tmp, _host.CheckWrite(path), overwrite: true);
        Profiler.Wrote(path, content.Length);
    }

    /// <summary>Text inside a `code span`: no entity escaping there, only what would end or break the span.</summary>
    private static string MdCode(string s) => s.Replace("`", "'").Replace("\r", " ").Replace("\n", " ").Replace("\0", " ");

    /// <summary>Markdown-safe text: table pipes, line breaks, NULs, and and "<" (GitHub drops unknown tags like &lt;unknown&gt;).</summary>
    private static string Md(string s) =>
        s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ").Replace("\0", " ").Replace("<", "&lt;");
}
