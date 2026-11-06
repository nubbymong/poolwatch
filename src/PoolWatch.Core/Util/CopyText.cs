using System.Globalization;
using System.Text;
using System.Text.Json;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Incidents;

namespace PoolWatch.Core.Util;

/// <summary>
/// Plain text for the clipboard: tables as tab-separated values with a header row, and incident summaries; and an
/// incident's summary as the Pool causes details show it.
/// </summary>
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

    /// <summary>
    /// An incident's summary from its report.json: headline, window, plain answer, ranked suspects with evidence. What Copy
    /// summary copies, as report.json has it: the headline's tag in a Markdown code span, the kind by its name in
    /// report.json ("NonPagedTotal"), the window's times in UTC; and each suspect's confidence in the words of Pool causes'
    /// Confidence column, as everywhere a person reads one (<see cref="PlainWords.Confidence"/>: "strong evidence"; a value
    /// that names none stays as written).
    /// </summary>
    public static string IncidentSummary(string reportJson) => Incident(reportJson, zone: null);

    /// <summary>
    /// The same summary as the Pool causes details show it: the headline without the backticks report.json writes its tag
    /// between (<c>IncidentWriter.Headline</c>; the summary's only Markdown mark, so a backtick in a name or a command line
    /// stays); the kind in the window's words for it (as the row above the details and the tray's notice name it,
    /// <see cref="PlainWords.IncidentKindInTheWindow"/>: "non-paged pool over the limit"; a value that names none stays as
    /// written); each suspect's confidence as Copy summary gives it; and the window's times in local time to the second,
    /// as the window prints times elsewhere (<see cref="Fmt.Stamp"/>, the onset <see cref="Fmt.Clock"/> on the window's
    /// first day). A time that does not read as one stays as written.
    /// </summary>
    public static string IncidentDetails(string reportJson) => IncidentDetails(reportJson, TimeZoneInfo.Local);

    /// <summary>
    /// The details with their times in <paramref name="zone"/>: the PC's own zone for the window (above), another in the
    /// tests, which can run where local time and UTC read the same.
    /// </summary>
    internal static string IncidentDetails(string reportJson, TimeZoneInfo zone) => Incident(reportJson, zone);

    /// <param name="zone">
    /// The zone the details show times in; null for the summary as report.json has it, but for the confidence (Copy summary).
    /// </param>
    private static string Incident(string reportJson, TimeZoneInfo? zone)
    {
        bool details = zone is not null;
        using var doc = JsonDocument.Parse(reportJson);
        var r = doc.RootElement;
        var sb = new StringBuilder();
        string tag = S(r, "tag").TrimEnd(), kind = S(r, "kind");
        sb.Append(details ? Unquoted(S(r, "headline"), tag) : S(r, "headline")).Append("\r\n");
        if (r.TryGetProperty("id", out _)) sb.Append($"Incident {S(r, "id")} ({(details ? PlainWords.IncidentKindInTheWindow(kind) ?? kind : kind)}), tag {tag}").Append("\r\n");
        if (r.TryGetProperty("window", out var w) && w.ValueKind == JsonValueKind.Object)
            sb.Append(zone is not null ? LocalWindow(w, zone) : $"Window: {S(w, "fromUtc")} to {S(w, "toUtc")} (onset {S(w, "onsetUtc")})").Append("\r\n");
        if (S(r, "plainAnswer") is { Length: > 0 } plain) sb.Append(plain).Append("\r\n");
        if (r.TryGetProperty("suspects", out var sus) && sus.ValueKind == JsonValueKind.Array)
        {
            sb.Append("Suspects:\r\n");
            int i = 0;
            foreach (var s in sus.EnumerateArray())
            {
                if (++i > 5) break;
                sb.Append($"{i}. {S(s, "displayName")} (PID {(s.TryGetProperty("pid", out var pid) ? pid.ToString() : "?")}), {ConfidenceWords(S(s, "confidence"))}\r\n");
                if (s.TryGetProperty("evidence", out var ev) && ev.ValueKind == JsonValueKind.Array)
                    foreach (var line in ev.EnumerateArray()) sb.Append("   - ").Append(line.GetString()).Append("\r\n");
                if (S(s, "commandLine") is { Length: > 0 } cl) sb.Append("   command line: ").Append(cl).Append("\r\n");
            }
        }
        return sb.ToString().TrimEnd() + "\r\n";
    }

    private static string S(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>The headline with its first <c>`tag`</c> as the tag alone: the code span <c>IncidentWriter.Headline</c> puts it in.</summary>
    private static string Unquoted(string headline, string tag)
    {
        string quoted = "`" + tag + "`";
        int at = tag.Length > 0 ? headline.IndexOf(quoted, StringComparison.Ordinal) : -1;
        return at < 0 ? headline : string.Concat(headline.AsSpan(0, at), tag, headline.AsSpan(at + quoted.Length));
    }

    /// <summary>A suspect's confidence as report.json names it ("StronglyCorrelated") in the window's words ("strong evidence"); any other text as written.</summary>
    private static string ConfidenceWords(string name) =>
        Enum.TryParse<Confidence>(name, out var c) && c.ToString() == name ? PlainWords.Confidence(c) : name;

    /// <summary>
    /// "Window: 2026-10-31 19:10:12 +00:00 to 2026-10-31 19:10:52 +00:00 (onset 19:10:22)", each time in
    /// <paramref name="zone"/>; the onset with its date when it falls on another day there than the window's start.
    /// </summary>
    private static string LocalWindow(JsonElement w, TimeZoneInfo zone)
    {
        DateTimeOffset? from = Time(w, "fromUtc", zone), to = Time(w, "toUtc", zone), onset = Time(w, "onsetUtc", zone);
        string Stamp(DateTimeOffset? t, string name) => t is { } at ? at.ToString(StampFormat, CultureInfo.InvariantCulture) : S(w, name);
        string start = onset is { } o && from is { } f && o.Date == f.Date ? o.ToString(ClockFormat, CultureInfo.InvariantCulture) : Stamp(onset, "onsetUtc");
        return $"Window: {Stamp(from, "fromUtc")} to {Stamp(to, "toUtc")} (onset {start})";
    }

    /// <summary>How <see cref="Fmt.Stamp"/> and <see cref="Fmt.Clock"/> print a time.</summary>
    private const string StampFormat = "yyyy-MM-dd HH:mm:ss zzz", ClockFormat = "HH:mm:ss";

    /// <summary>
    /// A time report.json holds, in <paramref name="zone"/>, or null when it is not one. Without an offset it is UTC, as
    /// the field's name says; with a Z (as IncidentWriter writes it) or another offset, that instant. System.Text.Json reads
    /// a time without an offset as unspecified, one with a Z as UTC, and one with another offset as the PC's local time,
    /// which ToUniversalTime turns back into the instant.
    /// </summary>
    private static DateTimeOffset? Time(JsonElement e, string name, TimeZoneInfo zone)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String || !v.TryGetDateTime(out var t)) return null;
        var utc = t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t.ToUniversalTime();
        return TimeZoneInfo.ConvertTime(new DateTimeOffset(utc), zone);
    }
}
