using System.Text;
using System.Text.Json;
using PoolWatch.Core.Incidents;

namespace PoolWatch.Core.Util;

/// <summary>Plain text for the clipboard: tables as tab-separated values with a header row, and incident summaries.</summary>
public static class CopyText
{
    /// <summary>Header row plus one line per row; tabs and line breaks inside cells become spaces so columns stay aligned.</summary>
    public static string Tsv(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join('\t', headers.Select(Cell))).Append("\r\n");
        foreach (var r in rows) sb.Append(string.Join('\t', r.Select(Cell))).Append("\r\n");
        return sb.ToString();
    }

    public static string Cell(string s) => s.Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");

    /// <summary>Applies the copy redaction setting (user name, home paths, machine name), when on.</summary>
    public static string Apply(string text, RedactionInfo? redaction) => redaction is null ? text : Redactor.Redact(text, redaction);

    /// <summary>An incident's summary from its report.json: headline, window, plain answer, ranked suspects with evidence.</summary>
    public static string IncidentSummary(string reportJson)
    {
        using var doc = JsonDocument.Parse(reportJson);
        var r = doc.RootElement;
        var sb = new StringBuilder();
        string S(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        sb.Append(S(r, "headline")).Append("\r\n");
        if (r.TryGetProperty("id", out _)) sb.Append($"Incident {S(r, "id")} ({S(r, "kind")}), tag {S(r, "tag").TrimEnd()}").Append("\r\n");
        if (r.TryGetProperty("window", out var w) && w.ValueKind == JsonValueKind.Object)
            sb.Append($"Window: {S(w, "fromUtc")} to {S(w, "toUtc")} (onset {S(w, "onsetUtc")})").Append("\r\n");
        if (S(r, "plainAnswer") is { Length: > 0 } plain) sb.Append(plain).Append("\r\n");
        if (r.TryGetProperty("suspects", out var sus) && sus.ValueKind == JsonValueKind.Array)
        {
            sb.Append("Suspects:\r\n");
            int i = 0;
            foreach (var s in sus.EnumerateArray())
            {
                if (++i > 5) break;
                sb.Append($"{i}. {S(s, "displayName")} (PID {(s.TryGetProperty("pid", out var pid) ? pid.ToString() : "?")}), {S(s, "confidence")}\r\n");
                if (s.TryGetProperty("evidence", out var ev) && ev.ValueKind == JsonValueKind.Array)
                    foreach (var line in ev.EnumerateArray()) sb.Append("   - ").Append(line.GetString()).Append("\r\n");
                if (S(s, "commandLine") is { Length: > 0 } cl) sb.Append("   command line: ").Append(cl).Append("\r\n");
            }
        }
        return sb.ToString().TrimEnd() + "\r\n";
    }
}
