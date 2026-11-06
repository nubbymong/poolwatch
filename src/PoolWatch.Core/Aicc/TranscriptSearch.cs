using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PoolWatch.Core.Aicc;

/// <summary>The Claude Code tool call that issued a command.</summary>
public sealed record TranscriptHit(string SessionId, string Project, string? WorkflowId, string? SubagentId, DateTime? TimestampUtc, string File, string Command);

/// <summary>Limits that keep the search from becoming a crawler (DESIGN.md section 3).</summary>
public sealed record SearchBudget(int MaxFiles = 400, int MaxEntries = 20_000, long MaxBytes = 16L << 20, int TailBytes = 8 << 20, int MaxLineBytes = 1 << 20);

/// <summary>
/// "Which Claude Code session issued it?" (DESIGN.md section 9). Looks for the command in transcripts
/// <c>&lt;config&gt;\projects\*\*.jsonl</c> and <c>&lt;config&gt;\projects\*\*\subagents\**\*.jsonl</c> modified within the window
/// of the process start, in <c>"type":"tool_use"</c> lines only. Bounded on every axis: directory entries enumerated,
/// files opened (closest modification time first), bytes read (only the tail of each file, where the call was written
/// just before the process started), line length; lines are scanned as UTF-8 bytes and only candidate lines become
/// strings. The session's own config dir is searched first; other profiles only if it has no hit. Read-only.
/// </summary>
public static partial class TranscriptSearch
{
    [GeneratedRegex(@"wf_[A-Za-z0-9]+")] private static partial Regex WorkflowRx();
    [GeneratedRegex(@"\beval\s+'(?<c>[^']*)'")] private static partial Regex EvalRx();

    private static readonly HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase) { "bash", "sh", "zsh", "cmd", "pwsh", "powershell" };
    private static readonly HashSet<string> ShellCommandSwitches = new(StringComparer.OrdinalIgnoreCase) { "-c", "/c", "/k", "-command", "-lc", "-ic" };
    /// <summary>Interpreter switches that say nothing about the command itself.</summary>
    private static readonly HashSet<string> GenericSwitches = new(StringComparer.OrdinalIgnoreCase)
        { "/d", "/s", "/q", "/c", "/k", "-c", "-l", "-i", "-e", "-x", "-noprofile", "-nologo", "-noninteractive", "-command", "-executionpolicy", "bypass", "-file", "--login" };
    private static readonly byte[] ToolUse = Encoding.UTF8.GetBytes("\"tool_use\"");

    /// <summary>
    /// The distinctive, JSON-safe tokens of a command line, in order: up to six consecutive argument tokens (quotes
    /// stripped, as the shell strips them) with no backslashes, after unwrapping shells (<c>bash -c -l "…"</c>,
    /// <c>cmd /d /s /c "…"</c>, <c>pwsh -NoProfile -Command "…"</c>, <c>eval '…'</c>). For
    /// <c>find / -maxdepth 6 -type d -name "x.*"</c> that is <c>/ -maxdepth 6 -type d -name x.*</c>. Null when only
    /// generic switches or nothing distinctive remain.
    /// </summary>
    public static IReadOnlyList<string>? CommandCore(string? commandLine, int depth = 0)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || depth > 3) return null;
        var tokens = Tokenize(commandLine);
        if (tokens.Count == 0) return null;
        string exe = Path.GetFileNameWithoutExtension(tokens[0].Trim('"', '\'').Replace('\\', '/').Split('/')[^1]);
        tokens.RemoveAt(0);
        if (Shells.Contains(exe))
        {
            // Leading options up to the command switch; the rest is the command the shell runs.
            for (int i = 0; i < tokens.Count && i < 6; i++)
            {
                if (ShellCommandSwitches.Contains(tokens[i]))
                {
                    string inner = string.Join(' ', tokens.Skip(i + 1).SkipWhile(t => t.StartsWith('-') && t.Length <= 3)).Trim();
                    if (inner.Length >= 2 && (inner[0] is '"' or '\'') && inner[^1] == inner[0]) inner = inner[1..^1];
                    if (EvalRx().Match(inner) is { Success: true } ev) inner = ev.Groups["c"].Value; // Claude Code's shell wrapper
                    // The inner text is a command without its own "executable" token for the recursion to drop.
                    return CommandCore("sh-inner " + inner, depth + 1) is { } core && Distinctive(core) ? core : null;
                }
                if (!tokens[i].StartsWith('-') && !tokens[i].StartsWith('/')) break;
            }
        }
        var clean = tokens.Select(t => t.Trim('"', '\'')).ToList();
        var best = new List<string>();
        var cur = new List<string>();
        foreach (var t in clean.Append("\\"))
        {
            if (t.Contains('\\') || t.Contains('"') || t.Length == 0 || cur.Count == 6)
            {
                if (Score(cur) > Score(best)) best = [.. cur];
                cur.Clear();
                if (t.Contains('\\') || t.Contains('"') || t.Length == 0) continue;
            }
            cur.Add(t);
        }
        return best.Count >= 2 && string.Join(' ', best).Length >= 8 && Distinctive(best) ? best : null;

        static int Score(List<string> run) => run.Sum(x => x.Length) + 4 * run.Count(x => x.StartsWith('-') && !GenericSwitches.Contains(x));
    }

    /// <summary>At least one token that is not a generic interpreter switch and not a bare common word.</summary>
    private static bool Distinctive(IReadOnlyList<string> core) =>
        core.Count(t => !GenericSwitches.Contains(t)) >= 2 && core.Any(t => !GenericSwitches.Contains(t) && t.Length >= 3);

    /// <param name="configDirs">Search order: the session's own config dir first, then the others.</param>
    public static TranscriptHit? Find(IReadOnlyList<string> configDirs, string commandLine, DateTime processStartUtc, double windowMinutes = 30, SearchBudget? budget = null)
    {
        var core = CommandCore(commandLine);
        if (core is null || processStartUtc == DateTime.MinValue) return null;
        budget ??= new SearchBudget();
        var window = TimeSpan.FromMinutes(windowMinutes);
        long bytesLeft = budget.MaxBytes;
        int entriesLeft = budget.MaxEntries, filesLeft = budget.MaxFiles;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TranscriptHit? best = null;
        foreach (var dir in configDirs)
        {
            string norm = dir.TrimEnd('\\', '/');
            if (!seen.Add(norm)) continue;
            var files = Candidates(norm, processStartUtc - window, ref entriesLeft);
            foreach (var fi in files.OrderBy(f => Math.Abs((f.LastWriteTimeUtc - processStartUtc).Ticks)))
            {
                if (filesLeft-- <= 0 || bytesLeft <= 0) return best;
                var hit = SearchFile(fi, core, processStartUtc, window, budget, ref bytesLeft);
                if (hit is null) continue;
                // A call within seconds of the start is the one: stop. Otherwise keep the closest seen so far.
                if (hit.TimestampUtc is { } ts && ts >= processStartUtc.AddSeconds(-10)) return hit;
                if (best is null || (hit.TimestampUtc ?? DateTime.MinValue) > (best.TimestampUtc ?? DateTime.MinValue)) best = hit;
            }
            if (best is not null || entriesLeft <= 0) return best; // other profiles only when this one had nothing
        }
        return best;
    }

    private static List<FileInfo> Candidates(string configDir, DateTime notBefore, ref int entriesLeft)
    {
        var result = new List<FileInfo>();
        string projects = Directory.Exists(Path.Combine(configDir, "projects")) ? Path.Combine(configDir, "projects") : Path.Combine(configDir, ".claude", "projects");
        if (!Directory.Exists(projects)) return result;
        var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint };
        try
        {
            // FileInfo from enumeration carries the modification time: no extra stat per file.
            foreach (var project in new DirectoryInfo(projects).EnumerateDirectories("*", opts))
            {
                if (--entriesLeft <= 0) break;
                foreach (var f in project.EnumerateFiles("*.jsonl", opts))
                {
                    if (--entriesLeft <= 0) break;
                    if (f.LastWriteTimeUtc >= notBefore) result.Add(f);
                }
                foreach (var session in project.EnumerateDirectories("*", opts))
                {
                    if (--entriesLeft <= 0) break;
                    var sub = new DirectoryInfo(Path.Combine(session.FullName, "subagents"));
                    if (!sub.Exists) continue;
                    foreach (var f in sub.EnumerateFiles("*.jsonl", new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, MaxRecursionDepth = 3, AttributesToSkip = FileAttributes.ReparsePoint }))
                    {
                        if (--entriesLeft <= 0) break;
                        if (f.LastWriteTimeUtc >= notBefore) result.Add(f);
                    }
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return result;
    }

    /// <summary>
    /// Scans the file's tail as bytes, read a piece at a time into one pooled buffer longer than the longest line
    /// considered (the pool rounds the default 1 MB + 1 up to a 2 MB array, which it keeps for the next search); keeps the
    /// latest matching tool call not later than the process start.
    /// </summary>
    private static TranscriptHit? SearchFile(FileInfo fi, IReadOnlyList<string> core, DateTime startUtc, TimeSpan window, SearchBudget budget, ref long bytesLeft)
    {
        byte[]? buf = null;
        try
        {
            using var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            long len = fs.Length;
            long take = Math.Min(Math.Min(len, budget.TailBytes), Math.Max(0, bytesLeft));
            if (take <= 0) return null;
            fs.Seek(len - take, SeekOrigin.Begin);
            buf = ArrayPool<byte>.Shared.Rent(budget.MaxLineBytes + 1);
            var coreBytes = core.Select(Encoding.UTF8.GetBytes).ToArray();
            TranscriptHit? best = null;
            bool skipping = len > take; // the tail starts inside a line: from the first whole one
            int kept = 0;               // a line's start carried over to the front of the buffer
            while (true)
            {
                int n = take > 0 ? fs.Read(buf, kept, (int)Math.Min(buf.Length - kept, take)) : 0;
                if (n <= 0)
                {
                    if (!skipping && kept > 0) Consider(buf.AsSpan(0, kept)); // the last line, without a newline
                    break;
                }
                take -= n;
                bytesLeft -= n;
                var data = buf.AsSpan(0, kept + n);
                for (int nl; (nl = data.IndexOf((byte)'\n')) >= 0; data = data[(nl + 1)..])
                {
                    if (!skipping) Consider(data[..nl]);
                    skipping = false;
                }
                // The rest waits for the next read; a line that fills the whole buffer is over the limit: skipped to its end.
                if (skipping || data.Length == buf.Length) { skipping = true; kept = 0; }
                else { data.CopyTo(buf); kept = data.Length; }
            }
            return best;

            void Consider(ReadOnlySpan<byte> line)
            {
                if (line.Length > budget.MaxLineBytes || line.IndexOf(ToolUse) < 0 || !InOrder(line, coreBytes)) return;
                var hit = Parse(fi.FullName, Encoding.UTF8.GetString(line), core);
                if (hit is null) return;
                // The call must precede the process (allowing a little clock skew) and fall inside the window.
                if (hit.TimestampUtc is { } ts && (ts > startUtc.AddSeconds(5) || ts < startUtc - window)) return;
                best = hit; // later lines are later calls: the last match is the one closest to the start
            }
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        finally
        {
            if (buf is not null) ArrayPool<byte>.Shared.Return(buf);
        }
    }

    /// <summary>The tokens occur in order, each within a short gap (spaces, quotes, JSON escapes) of the previous one.</summary>
    internal static bool InOrder(ReadOnlySpan<byte> line, byte[][] tokens)
    {
        for (int start = line.IndexOf(tokens[0]); start >= 0;)
        {
            int pos = start + tokens[0].Length;
            bool ok = true;
            for (int i = 1; i < tokens.Length && ok; i++)
            {
                int gapLimit = Math.Min(line.Length - pos, 8 + tokens[i].Length);
                int at = gapLimit > 0 ? line.Slice(pos, gapLimit).IndexOf(tokens[i]) : -1;
                if (at < 0 || !Gap(line.Slice(pos, at))) ok = false;
                else pos += at + tokens[i].Length;
            }
            if (ok) return true;
            int next = line[(start + 1)..].IndexOf(tokens[0]);
            start = next < 0 ? -1 : start + 1 + next;
        }
        return false;

        static bool Gap(ReadOnlySpan<byte> g)
        {
            foreach (var b in g) if (b is not ((byte)' ' or (byte)'"' or (byte)'\'' or (byte)'\\' or (byte)'\t')) return false;
            return g.Length > 0;
        }
    }

    private static TranscriptHit? Parse(string path, string line, IReadOnlyList<string> core)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            string? command = FindToolCommand(root, core);
            if (command is null) return null; // the tokens were in some other text, not in a tool_use input
            string sessionId = root.TryGetProperty("sessionId", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : Path.GetFileNameWithoutExtension(path);
            DateTime? ts = root.TryGetProperty("timestamp", out var t) && t.ValueKind == JsonValueKind.String && DateTime.TryParse(t.GetString(), null,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt) ? dt : null;
            var parts = path.Replace('\\', '/').Split('/');
            int pi = Array.LastIndexOf(parts, "projects");
            string project = pi >= 0 && pi + 1 < parts.Length ? parts[pi + 1] : "";
            int si = Array.LastIndexOf(parts, "subagents");
            string? subagent = si >= 0 ? Path.GetFileNameWithoutExtension(path) : null;
            if (si >= 0 && pi + 2 < parts.Length && si > pi + 2 && !root.TryGetProperty("sessionId", out _)) sessionId = parts[pi + 2];
            string? agentId = root.TryGetProperty("agentId", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            string? wf = WorkflowRx().Match(path) is { Success: true } m1 ? m1.Value : WorkflowRx().Match(line) is { Success: true } m2 ? m2.Value : null;
            return new TranscriptHit(sessionId, project, wf, agentId ?? subagent, ts, path, command);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The tool_use input string that contains the tokens in order, searched anywhere in the line's JSON.</summary>
    private static string? FindToolCommand(JsonElement e, IReadOnlyList<string> core)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                if (e.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "tool_use" && e.TryGetProperty("input", out var input) &&
                    input.ValueKind == JsonValueKind.Object)
                {
                    var tokens = core.Select(Encoding.UTF8.GetBytes).ToArray();
                    foreach (var p in input.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.String && InOrder(Encoding.UTF8.GetBytes(p.Value.GetString()!), tokens)) return p.Value.GetString();
                }
                foreach (var p in e.EnumerateObject())
                    if (FindToolCommand(p.Value, core) is { } r) return r;
                return null;
            case JsonValueKind.Array:
                foreach (var x in e.EnumerateArray())
                    if (FindToolCommand(x, core) is { } r) return r;
                return null;
            default:
                return null;
        }
    }

    private static List<string> Tokenize(string s)
    {
        var tokens = new List<string>();
        var cur = new StringBuilder();
        char quote = '\0';
        foreach (char c in s)
        {
            if (quote == '\0' && c is '"' or '\'') { quote = c; cur.Append(c); continue; }
            if (c == quote) { quote = '\0'; cur.Append(c); continue; }
            if (char.IsWhiteSpace(c) && quote == '\0') { if (cur.Length > 0) { tokens.Add(cur.ToString()); cur.Clear(); } continue; }
            cur.Append(c);
        }
        if (cur.Length > 0) tokens.Add(cur.ToString());
        return tokens;
    }
}
