using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PoolWatch.Core.Detection;

namespace PoolWatch.Core.Findings;

/// <summary>
/// The findings recorded in this run (the most recent <see cref="MaxKept"/> are kept in memory). Thread-safe;
/// <see cref="Added"/> is raised on the caller's thread, outside the lock. C6 (review Part 2): the high-volume event-log
/// findings (package activations from the AppModel-Runtime channel, subscribed with "*", and plain filter load / unload
/// events) are kept in their own buffer of <see cref="MaxKeptEvents"/>, so a burst of them can never evict the other
/// findings (alerts, crashes, storms, marks). Snapshots merge the two in the order they were added.
/// </summary>
public sealed class FindingLog
{
    public const int MaxKept = 1000;
    public const int MaxKeptEvents = 300;
    private readonly object _lock = new();
    private readonly List<(long seq, Finding f)> _items = new();
    private readonly List<(long seq, Finding f)> _events = new();
    private long _seq;

    public event Action<Finding>? Added;

    /// <summary>A routine event-log record turned into a finding (kept in the separate, smaller buffer).</summary>
    public static bool IsHighVolume(Finding f) =>
        f.Kind == FindingKind.PackageActivation || (f.Kind == FindingKind.FilterEvent && f.Severity == FindingSeverity.Info);

    public void Add(Finding f)
    {
        lock (_lock)
        {
            var list = IsHighVolume(f) ? _events : _items;
            int cap = ReferenceEquals(list, _events) ? MaxKeptEvents : MaxKept;
            list.Add((++_seq, f));
            if (list.Count > cap) list.RemoveRange(0, list.Count - cap);
        }
        Added?.Invoke(f);
    }

    public List<Finding> Snapshot() { lock (_lock) return Merged().ToList(); }

    public List<Finding> Between(DateTime fromUtc, DateTime toUtc)
    {
        lock (_lock) return Merged().Where(f => f.TimeUtc >= fromUtc && f.TimeUtc <= toUtc).ToList();
    }

    public int Count { get { lock (_lock) return _items.Count + _events.Count; } }

    /// <summary>Both buffers in the order the findings were added (lock held).</summary>
    private IEnumerable<Finding> Merged()
    {
        int i = 0, j = 0;
        while (i < _items.Count || j < _events.Count)
        {
            if (j >= _events.Count || (i < _items.Count && _items[i].seq < _events[j].seq)) yield return _items[i++].f;
            else yield return _events[j++].f;
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

    public static void Write(string dir, Finding f, string markdownBody, IReadOnlyDictionary<string, string>? extraFiles, Func<string, string> checkWrite)
    {
        Directory.CreateDirectory(checkWrite(dir));
        var md = new StringBuilder();
        md.AppendLine($"# {Md(f.Title)}").AppendLine();
        md.AppendLine($"> {Incidents.Exporter.UnredactedNotice}").AppendLine(); // M1
        md.AppendLine($"- Finding `{f.Kind}` ({f.Severity}), {Fmt.Stamp(f.TimeUtc)}");
        foreach (var d in f.Details) md.AppendLine($"- {Md(d)}");
        md.AppendLine().Append(markdownBody);
        Atomic(Path.Combine(dir, "report.md"), md.ToString(), checkWrite);
        var json = new
        {
            notice = Incidents.Exporter.UnredactedNotice,
            id = Path.GetFileName(dir), kind = f.Kind, severity = f.Severity, headline = f.Title, time = f.TimeUtc, details = f.Details,
            pid = f.Pid, image = f.Image, data = f.Data,
        };
        Atomic(Path.Combine(dir, "report.json"), JsonSerializer.Serialize(json, Indented), checkWrite);
        if (extraFiles is not null)
            foreach (var (name, content) in extraFiles) Atomic(Path.Combine(dir, name), content, checkWrite);
    }

    private static void Atomic(string path, string content, Func<string, string> checkWrite)
    {
        string tmp = checkWrite(path + ".tmp");
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        File.Move(tmp, checkWrite(path), overwrite: true);
        Util.Profiler.Wrote(path, content.Length);
    }

    private static string FileSafe(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        return sb.Length > 40 ? sb.ToString(0, 40) : sb.ToString();
    }

    /// <summary>Markdown-safe text (same rules as the incident writer).</summary>
    public static string Md(string s) =>
        s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ").Replace("\0", " ").Replace("<", "&lt;");

    /// <summary>Text inside a `code span`.</summary>
    public static string MdCode(string s) => s.Replace("`", "'").Replace("\r", " ").Replace("\n", " ").Replace("\0", " ");
}
