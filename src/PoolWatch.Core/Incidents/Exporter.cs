using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace PoolWatch.Core.Incidents;

/// <summary>What the redaction pass replaces (DESIGN.md section 10): user name, home paths, machine name (and the user's SID).</summary>
/// <param name="TempPaths">The TEMP / TMP folders (long and 8.3 forms), replaced by <c>&lt;TEMP&gt;</c>. C6: they used to be
/// listed as home paths, so <c>...\Temp\x</c> came out as <c>&lt;HOME&gt;\x</c>; the longer TEMP path wins over the home path.</param>
public sealed record RedactionInfo(string UserName, string MachineName, IReadOnlyList<string> HomePaths, string? Domain = null, string? UserSid = null,
    IReadOnlyList<string>? TempPaths = null);

/// <summary>
/// M1: a best-effort scrub of secrets that sit inside command lines and transcript text, which evidence packs hold: the
/// value after a password / token / API-key option or key (<c>--password x</c>, <c>/token:x</c>, <c>API_KEY=x</c>,
/// <c>pwd=x</c> in a connection string, <c>sig=x</c> in a SAS URL), an Authorization header or a Bearer token, the
/// user:password part of a URL, a PEM private key, and tokens with a well-known shape (OpenAI / Anthropic <c>sk-</c>,
/// GitHub, GitLab, npm, Slack, Google API keys, AWS access key ids, JWTs). The value becomes <see cref="Placeholder"/>; the
/// option name stays, so the report still says what was passed. It cannot know every secret format: the export notice
/// says to read the result before posting it.
/// </summary>
public static class SecretScrubber
{
    public const string Placeholder = "<SECRET>";

    private const string Names = @"password|passwd|pwd|passphrase|token|access[-_]?token|refresh[-_]?token|auth[-_]?token|id[-_]?token|" +
                                 @"api[-_]?key|apikey|secret|client[-_]?secret|private[-_]?key|secret[-_]?key|access[-_]?key|" +
                                 @"aws[-_]?secret[-_]?access[-_]?key|credentials?|sas[-_]?token|sig";

    // Quoted value forms, shared by every grammar below. The JSON-escaped form (\"...\") consumes escape pairs, so it can
    // never eat the JSON string's own closing quote and leave report.json invalid (N1). None can hold a raw double quote, so
    // in JSON / CSV text a match never runs past the string or field it began in.
    private static string Quoted(bool structured)
    {
        string forms = @"\\""(?:[^""\\\r\n]|\\[^""\r\n])*\\""|\\u0022[^""\r\n]*?\\u0022|\\u0027[^""\r\n]*?\\u0027|""""[^""\r\n]*""""|'[^'""\r\n]*'";
        return structured ? forms : forms + @"|""[^""\r\n]*""";
    }

    // A bare (unquoted) value.
    //  - optionForm (--password x, /token:x): in plain text (a report.md `code span`, a copied line) it stops ONLY at
    //    whitespace, a quote and a backtick, so the WHOLE secret goes even when it holds & ; | < > ) ] } , or a backslash
    //    (S2), while the closing backtick of the code span survives (S1; MdCode turns any backtick in the value into ').
    //    In JSON / CSV it also stops at a comma, the one separator that can sit unquoted next to a value (an unquoted CSV
    //    field), so a value never swallows the field separator and the following field; other data bytes (& ; | < >) are
    //    still taken, because inside a JSON string or a quoted CSV field they are part of the value.
    //  - key=value form (connection strings, URLs): also stops at , ; & | < > ) ] }, the separators that end a value there.
    //  In structured text a backslash is an escape, so \\ and \uXXXX (not a quote) are consumed and a lone backslash
    //  (such as the JSON \") ends the value.
    private static string Bare(bool structured, bool optionForm)
    {
        string stops = @"\s""'`" + (optionForm ? (structured ? "," : "") : @",;&|<>)\]}");
        return structured ? @"(?:\\\\|\\u(?!002[27])[0-9a-f]{4}|[^" + stops + @"\\])+" : @"(?:[^" + stops + @"])+";
    }

    private static string Value(bool structured, bool optionForm) => Quoted(structured) + "|" + Bare(structured, optionForm);

    private static Regex Build(bool structured)
    {
        string opt = Value(structured, optionForm: true), kv = Value(structured, optionForm: false);
        return new Regex(string.Join("|",
            // --password x, -token=x, /api-key:x, --db-password x (an option, then spaces, '=' or ':'): value to whitespace / quote.
            $@"(?<k>(?<![\w-])(?:--?|/)(?:[a-z0-9]+[-_])*(?:{Names}|pass)(?:[ \t]+|=|:))(?<v>{opt})",
            // PASSWORD=x, api_key: x, pwd=x; X-Api-Key: x (a key, then '=' or ':'): value stops at a connection-string separator.
            $@"(?<k>(?<![\w-])(?:[a-z0-9]+[-_])*(?:{Names})[ \t]*[=:][ \t]*)(?<v>{kv})",
            // Authorization: Bearer x, Proxy-Authorization: Basic x
            $@"(?<k>(?<![\w-])(?:proxy-)?authorization[ \t]*[:=][ \t]*(?:(?:bearer|basic|token|digest|negotiate|ntlm)[ \t]+)?)(?<v>{opt})",
            @"(?<k>(?<![\w-])bearer[ \t]+)(?<v>[A-Za-z0-9\-._~+/]{8,}=*)",
            // scheme://user:password@host (a lookbehind, not \b, so a long run does not restart the match at every boundary — N2).
            @"(?<k>(?<![a-z0-9+.\-])[a-z][a-z0-9+.\-]*://)(?<v>[^\s/:@""'\\]+:[^\s/@""'\\]+)(?=@)",
            // Well-known token shapes.
            @"(?<v>\bsk-(?:ant-|proj-)?[A-Za-z0-9_\-]{16,})",
            @"(?<v>\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|glpat-[A-Za-z0-9_\-]{20,}|npm_[A-Za-z0-9]{36}))",
            @"(?<v>\b(?:AKIA|ASIA)[0-9A-Z]{16}\b)",
            @"(?<v>\bxox[abposr]-[A-Za-z0-9\-]{10,})",
            @"(?<v>\bAIza[0-9A-Za-z_\-]{35})",
            @"(?<v>\beyJ[A-Za-z0-9_\-]{8,}\.eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,})",
            @"(?<v>-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z0-9 ]*PRIVATE KEY-----)"),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(10));
    }

    private static readonly Regex Plain = Build(false), Structured = Build(true);

    /// <summary>
    /// The text with every recognised secret value replaced by <see cref="Placeholder"/>, the option / key name kept.
    /// <paramref name="structured"/>: the text is JSON or CSV (a raw double quote ends a string or field there). A match
    /// timeout throws (<see cref="RegexMatchTimeoutException"/>): an export fails rather than go out unscrubbed.
    /// </summary>
    public static string Scrub(string text, bool structured = false) =>
        text.Length == 0 ? text : (structured ? Structured : Plain).Replace(text, m => m.Groups["k"].Value + Placeholder);
}

/// <summary>
/// Redacts identifying strings from text evidence in one pass (so a placeholder is never re-matched), case-insensitively,
/// longest match first, with boundaries: a home path must end at a separator, quote or the end; names must be whole
/// words. Each value is also matched in its JSON-escaped forms (\\ for backslashes, \uXXXX for non-ASCII). Secrets inside
/// command lines are scrubbed first (<see cref="SecretScrubber"/>, M1).
/// </summary>
public static class Redactor
{
    /// <summary>Account names that are also ordinary words: replaced only where they follow a path separator.</summary>
    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
        { "user", "users", "admin", "administrator", "owner", "test", "guest", "dev", "default", "public", "system" };

    /// <param name="structured">The text is JSON or CSV (see <see cref="SecretScrubber.Scrub"/>).</param>
    public static string Redact(string text, RedactionInfo r, bool structured = false)
    {
        text = SecretScrubber.Scrub(text, structured);
        var rules = new List<(string pattern, string replacement)>();
        void Add(string? value, string replacement, string before, string after)
        {
            if (string.IsNullOrEmpty(value) || value.Length < 2) return;
            foreach (var form in Forms(value))
            {
                // In the JSON-escaped form a backslash in the replacement must be escaped too, or the JSON breaks.
                bool escaped = form.Contains("\\\\") && !value.Contains("\\\\");
                rules.Add(($"{before}{Regex.Escape(form)}{after}", escaped ? replacement.Replace("\\", "\\\\") : replacement));
            }
        }
        const string word0 = @"(?<![A-Za-z0-9_])", word1 = @"(?![A-Za-z0-9_])", pathEnd = @"(?=$|[\\/""'\s,;)\]])";
        // TEMP is usually inside the home folder: as the longer literal it is matched first (longest first, below); added
        // before the home paths so that it also wins a tie (the sort is stable).
        foreach (var temp in (r.TempPaths ?? []).Where(t => t.Length > 3)) { Add(temp.TrimEnd('\\', '/'), "<TEMP>", word0, pathEnd); Add(temp.TrimEnd('\\', '/').Replace('\\', '/'), "<TEMP>", word0, pathEnd); }
        foreach (var home in r.HomePaths.Where(h => h.Length > 3)) { Add(home.TrimEnd('\\', '/'), "<HOME>", word0, pathEnd); Add(home.TrimEnd('\\', '/').Replace('\\', '/'), "<HOME>", word0, pathEnd); }
        if (r.Domain is { Length: > 1 } d)
        {
            Add($"{d}\\{r.UserName}", "<DOMAIN>\\<USER>", word0, word1);
        }
        Add(r.UserSid, "<SID>", word0, word1);
        Add(r.MachineName, "<MACHINE>", word0, word1);
        if (CommonWords.Contains(r.UserName)) Add(r.UserName, "<USER>", @"(?<=[\\/])", word1); // only as a path segment
        else Add(r.UserName, "<USER>", word0, word1);
        if (rules.Count == 0) return text;
        // Longest literal first, so "C:\Users\alice" wins over "alice".
        var ordered = rules.OrderByDescending(x => x.pattern.Length).ToList();
        var regex = new Regex(string.Join("|", ordered.Select((x, i) => $"(?<r{i}>{x.pattern})")), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return regex.Replace(text, m =>
        {
            for (int i = 0; i < ordered.Count; i++)
                if (m.Groups[$"r{i}"].Success) return ordered[i].replacement;
            return m.Value;
        });
    }

    /// <summary>The value as written in plain text and in JSON (backslashes doubled, non-ASCII as \uXXXX).</summary>
    private static IEnumerable<string> Forms(string value)
    {
        var forms = new HashSet<string>(StringComparer.Ordinal) { value, value.Replace("\\", "\\\\") };
        var json = System.Text.Json.JsonEncodedText.Encode(value).ToString();
        forms.Add(json);
        // The default encoder escapes non-ASCII as \u00F6 (upper-case hex); also cover lower-case writers.
        forms.Add(Regex.Replace(json, @"\\u([0-9A-F]{4})", m => "\\u" + m.Groups[1].Value.ToLowerInvariant()));
        return forms;
    }
}

/// <summary>Builds an incident's export zip: every file, text files optionally redacted, binaries (.etl, .png) as-is or left out.</summary>
public static class Exporter
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase) { ".md", ".json", ".jsonl", ".csv", ".txt", ".log", ".wprp", ".xml" };

    /// <summary>
    /// M1: the first thing in every report PoolWatch writes on disk (report.md, and the <c>notice</c> field of report.json):
    /// the incident folder itself is for you, not for posting. Plain ASCII with nothing JSON escapes, so the same text can be
    /// found and swapped in both files (<see cref="RedactText"/>).
    /// </summary>
    public const string UnredactedNotice = "NOT REDACTED: this report holds full command lines, user and machine names and paths, " +
        "and may hold secrets passed on a command line. Share it only through Export report in PoolWatch (redacted by default), " +
        "and read the export before you post it.";

    /// <summary>What a redacted export says in place of <see cref="UnredactedNotice"/>.</summary>
    public const string RedactedNotice = "REDACTED by PoolWatch: user, machine and home-path names, the SID and common secret " +
        "patterns were replaced. Redaction is best-effort: read this file before you post it.";

    /// <summary>A redacted export's text: the notice swapped, then the redaction (secrets first, then identities).</summary>
    /// <param name="structured">The text is JSON or CSV.</param>
    public static string RedactText(string text, RedactionInfo redaction, bool structured = false) =>
        Redactor.Redact(text.Replace(UnredactedNotice, RedactedNotice, StringComparison.Ordinal), redaction, structured);

    private static readonly HashSet<string> StructuredExtensions = new(StringComparer.OrdinalIgnoreCase) { ".json", ".jsonl", ".csv", ".wprp", ".xml" };

    /// <summary>JSON, CSV and XML are "structured" for the secret scrub: a raw double quote ends a string, field or attribute there.</summary>
    public static bool IsStructured(string fileName) => StructuredExtensions.Contains(Path.GetExtension(fileName));

    /// <param name="checkWrite">The write policy (returns the path or throws).</param>
    public static string Export(string incidentDir, string exportsDir, RedactionInfo? redaction, Func<string, string> checkWrite)
    {
        Directory.CreateDirectory(checkWrite(exportsDir));
        string name = Path.GetFileName(incidentDir.TrimEnd('\\', '/')) + (redaction is not null ? "-redacted" : "") + ".zip";
        string zipPath = checkWrite(Path.Combine(exportsDir, name));
        string tmp = checkWrite(zipPath + ".tmp");
        try
        {
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            foreach (var file in Directory.EnumerateFiles(incidentDir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(incidentDir, file).Replace('\\', '/');
                if (rel.StartsWith("wpr-temp/", StringComparison.OrdinalIgnoreCase)) continue; // a running capture's buffers
                string ext = Path.GetExtension(file);
                if (ext.Equals(".tmp", StringComparison.OrdinalIgnoreCase) || ext.Equals(".etlx", StringComparison.OrdinalIgnoreCase)) continue;
                if (redaction is not null && TextExtensions.Contains(ext))
                {
                    var entry = zip.CreateEntry(rel, CompressionLevel.Optimal);
                    using var s = entry.Open();
                    var bytes = new UTF8Encoding(false).GetBytes(RedactText(File.ReadAllText(file), redaction, IsStructured(file)));
                    s.Write(bytes);
                }
                else if (redaction is not null && ext.Equals(".etl", StringComparison.OrdinalIgnoreCase))
                {
                    // A trace contains paths and names that cannot be redacted reliably: leave it out and say so.
                    var entry = zip.CreateEntry(rel + ".omitted.txt");
                    using var s = new StreamWriter(entry.Open());
                    s.Write("The .etl trace was left out of the redacted export because it contains unredactable user and machine names. Share it separately if needed.");
                }
                else zip.CreateEntryFromFile(file, rel, CompressionLevel.Optimal);
            }
        }
        File.Move(tmp, zipPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
        return zipPath;
    }
}
