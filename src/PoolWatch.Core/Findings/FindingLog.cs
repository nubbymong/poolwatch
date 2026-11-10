using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PoolWatch.Core.Detection;

namespace PoolWatch.Core.Findings;

/// <summary>
/// The findings recorded in this run (the most recent <see cref="MaxKept"/> are kept in memory). Thread-safe;
/// <see cref="Added"/> is raised on the caller's thread, outside the lock. The high-volume event-log
/// findings (package activations from the AppModel-Runtime channel, subscribed with "*", and plain filter load / unload
/// events) are kept in their own buffer of <see cref="MaxKeptEvents"/>, so a burst of them can never evict the other
/// findings (alerts, crashes, storms, marks). Snapshots merge the two in the order they were added. Each finding's time is
/// read onto the clock that only goes forward as it is added (<see cref="Model.Moment.MsOf"/>): how old it is
/// (<see cref="Within"/>) is timed on it.
/// </summary>
public sealed class FindingLog
{
    public const int MaxKept = 1000;
    public const int MaxKeptEvents = 300;
    private readonly object _lock = new();
    private readonly List<(long seq, long ms, Finding f)> _items = new();
    private readonly List<(long seq, long ms, Finding f)> _events = new();
    private long _seq;
    private readonly Func<Model.Moment> _clock;

    /// <param name="clock">Both clocks (<see cref="Model.Moment.Now"/>; a test gives its own).</param>
    public FindingLog(Func<Model.Moment>? clock = null) => _clock = clock ?? Model.Moment.Now;

    public event Action<Finding>? Added;

    /// <summary>A routine event-log record turned into a finding (kept in the separate, smaller buffer).</summary>
    public static bool IsHighVolume(Finding f) =>
        f.Kind == FindingKind.PackageActivation || (f.Kind == FindingKind.FilterEvent && f.Severity == FindingSeverity.Info);

    public void Add(Finding f)
    {
        long ms = _clock().MsOf(f.TimeUtc);
        lock (_lock)
        {
            var list = IsHighVolume(f) ? _events : _items;
            int cap = ReferenceEquals(list, _events) ? MaxKeptEvents : MaxKept;
            list.Add((++_seq, ms, f));
            if (list.Count > cap) list.RemoveRange(0, list.Count - cap);
        }
        Added?.Invoke(f);
    }

    public List<Finding> Snapshot() { lock (_lock) return Merged().Select(x => x.f).ToList(); }

    /// <summary>
    /// The findings of the last <paramref name="span"/> before <paramref name="now"/> on the clock that only goes forward,
    /// in the order they were added.
    /// </summary>
    public List<Finding> Within(TimeSpan span, Model.Moment now)
    {
        long from = now.Ms - (long)span.TotalMilliseconds;
        lock (_lock) return Merged().Where(x => x.ms >= from && x.ms <= now.Ms).Select(x => x.f).ToList();
    }

    public int Count { get { lock (_lock) return _items.Count + _events.Count; } }

    /// <summary>Both buffers in the order the findings were added (lock held).</summary>
    private IEnumerable<(long seq, long ms, Finding f)> Merged()
    {
        int i = 0, j = 0;
        while (i < _items.Count || j < _events.Count)
        {
            if (j >= _events.Count || (i < _items.Count && _items[i].seq < _events[j].seq)) yield return _items[i++];
            else yield return _events[j++];
        }
    }

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>One findings.jsonl line.</summary>
    public static string ToJsonLine(Finding f) => JsonSerializer.Serialize(new
    {
        time = f.TimeUtc, kind = f.Kind, severity = f.Severity, title = f.Title, details = f.Details.Count > 0 ? f.Details : null,
        pid = f.Pid != 0 ? f.Pid : (int?)null, image = f.Image, data = f.Data.Count > 0 ? f.Data : null, reportDir = f.ReportDir,
    }, Json);
}

/// <summary>
/// Writes a report folder for an alert-level finding: report.md, report.json and any extra files (e.g. a spawn
/// trace CSV). Atomic replace, like the incident writer, so a reader never sees half a file.
/// </summary>
public static class FindingReport
{
    private static readonly JsonSerializerOptions Indented = new(FindingLog.Json) { WriteIndented = true };

    /// <summary>Folder name: <c>yyyyMMdd-HHmmss-Kind-subject</c>, made unique within <paramref name="incidentsDir"/>.</summary>
    public static string NewDir(string incidentsDir, Finding f, string? subject)
    {
        string id = $"{f.TimeUtc.ToLocalTime():yyyyMMdd-HHmmss}-{f.Kind}" + (subject is null ? "" : "-" + FileSafe(subject));
        string dir = Path.Combine(incidentsDir, id);
        for (int i = 2; Directory.Exists(dir); i++) dir = Path.Combine(incidentsDir, $"{id}-{i}");
        return dir;
    }

    /// <summary>Writes the folder's files; returns the bytes written (UTF-8).</summary>
    public static long Write(string dir, Finding f, string markdownBody, IReadOnlyDictionary<string, string>? extraFiles, Func<string, string> checkWrite)
    {
        Directory.CreateDirectory(checkWrite(dir));
        var md = new StringBuilder();
        md.AppendLine($"# {Md(f.Title)}").AppendLine();
        md.AppendLine($"> {Incidents.Exporter.UnredactedNotice}").AppendLine(); // first, that the report is not redacted
        md.AppendLine($"- Finding `{f.Kind}` ({f.Severity}), {Fmt.Stamp(f.TimeUtc)}");
        foreach (var d in f.Details) md.AppendLine($"- {Md(d)}");
        md.AppendLine().Append(markdownBody);
        long written = Atomic(Path.Combine(dir, "report.md"), md.ToString(), checkWrite);
        var json = new
        {
            notice = Incidents.Exporter.UnredactedNotice,
            id = Path.GetFileName(dir), kind = f.Kind, severity = f.Severity, headline = f.Title, time = f.TimeUtc, details = f.Details,
            pid = f.Pid, image = f.Image, data = f.Data,
        };
        written += Atomic(Path.Combine(dir, "report.json"), JsonSerializer.Serialize(json, Indented), checkWrite);
        if (extraFiles is not null)
            foreach (var (name, content) in extraFiles) written += Atomic(Path.Combine(dir, name), content, checkWrite);
        return written;
    }

    /// <summary>Replaces <paramref name="path"/> with <paramref name="content"/> in one step (temp file + move); returns the bytes written.</summary>
    internal static long Atomic(string path, string content, Func<string, string> checkWrite)
    {
        var bytes = new UTF8Encoding(false).GetBytes(content);
        string tmp = checkWrite(path + ".tmp");
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, checkWrite(path), overwrite: true);
        Util.Profiler.Wrote(path, bytes.Length);
        return bytes.Length;
    }

    private static string FileSafe(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        return sb.Length > 40 ? sb.ToString(0, 40) : sb.ToString();
    }

    /// <summary>Markdown-safe text (the incident writer's rules: <see cref="Util.MdText.Text"/>).</summary>
    public static string Md(string s) => Util.MdText.Text(s);

    /// <summary>Text inside a `code span` (<see cref="Util.MdText.Code"/>).</summary>
    public static string MdCode(string s) => Util.MdText.Code(s);
}
