using System.Text.Json;

namespace PoolWatch.Core.Links;

/// <summary>
/// The home line of a venv's pyvenv.cfg ("home = C:\...\cpython-3.13-windows-x86_64-none"): the folder of the Python the
/// venv runs. The file is the account's to write, so it is read as untrusted: bounded, never throwing. Pure, so the Linux
/// tests give it any text.
/// </summary>
public static class VenvConfig
{
    /// <summary>The most of a pyvenv.cfg PoolWatch reads: a longer one is not read at all.</summary>
    public const int MaxBytes = 4096;

    /// <summary>The value of the first "home" line, trimmed; null when there is none, or it is empty, over 260 characters or holds a control character.</summary>
    public static string? Home(string? text)
    {
        if (text is null || text.Length > MaxBytes) return null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0 || !line[..eq].Trim().Equals("home", StringComparison.OrdinalIgnoreCase)) continue;
            string value = line[(eq + 1)..].Trim();
            return value.Length is > 0 and <= 260 && !value.Any(char.IsControl) ? value : null;
        }
        return null;
    }
}

/// <summary>
/// The hooks in a Claude Code settings.json ({"hooks": {"PreToolUse": [{"hooks": [{"type": "command", "command": "..."}]}]}}),
/// and whether a hook starts Python. The file is the account's to write, so it is read as untrusted: bounded, never
/// throwing. Pure, so the Linux tests give it any text.
/// </summary>
public static class ClaudeHooks
{
    /// <summary>The most of a settings file PoolWatch reads (a longer one is not read), the most commands kept, and the longest kept.</summary>
    public const int MaxBytes = 256 * 1024, MaxCommands = 64, MaxCommandChars = 400;

    private static readonly JsonDocumentOptions Options = new() { MaxDepth = 16, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>
    /// Each hook's command, in the file's order: at most <see cref="MaxCommands"/>, each of 1 to <see cref="MaxCommandChars"/>
    /// characters with no control character (any other is left out). None when the text is not JSON of that shape.
    /// </summary>
    public static IReadOnlyList<string> Commands(string? json)
    {
        var found = new List<string>();
        if (string.IsNullOrEmpty(json) || json.Length > MaxBytes) return found;
        try
        {
            using var doc = JsonDocument.Parse(json, Options);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("hooks", out var hooks) || hooks.ValueKind != JsonValueKind.Object) return found;
            foreach (var when in hooks.EnumerateObject())
            {
                if (when.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var group in when.Value.EnumerateArray())
                {
                    if (group.ValueKind != JsonValueKind.Object || !group.TryGetProperty("hooks", out var list) || list.ValueKind != JsonValueKind.Array) continue;
                    foreach (var hook in list.EnumerateArray())
                    {
                        if (found.Count >= MaxCommands) return found;
                        if (hook.ValueKind == JsonValueKind.Object && hook.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String &&
                            c.GetString() is { Length: > 0 and <= MaxCommandChars } command && !command.Any(char.IsControl))
                            found.Add(command);
                    }
                }
            }
        }
        // InvalidOperationException: a name or text holding half of a surrogate pair written as an escape ("\ud800").
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException) { found.Clear(); }
        return found;
    }

    /// <summary>The program a command starts: its first word, or the quoted text at its start, unquoted.</summary>
    public static string Program(string command)
    {
        string t = command.TrimStart();
        if (t.StartsWith('"'))
        {
            int end = t.IndexOf('"', 1);
            return end > 1 ? t[1..end] : "";
        }
        int space = t.IndexOfAny([' ', '\t']);
        return space < 0 ? t : t[..space];
    }

    /// <summary>
    /// Whether the program is Python or one of uv's runners, which start one: python, pythonw, python3, python3.12, py,
    /// pyw, uv or uvx, by name or by path, with or without .exe.
    /// </summary>
    public static bool StartsPython(string program)
    {
        string name = program.Replace('/', '\\');
        name = name[(name.LastIndexOf('\\') + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        name = name.ToLowerInvariant();
        if (name is "py" or "pyw" or "uv" or "uvx" or "pythonw") return true;
        return name.StartsWith("python", StringComparison.Ordinal) && name[6..].All(c => char.IsAsciiDigit(c) || c == '.');
    }
}

/// <summary>
/// The size of the blocks the leak keeps, which follows the link's reparse data: konrad-rzonca measured 208 bytes for a
/// link with 0xAE bytes of it, and 272 for 0xF0 and 0xF8; fsfxing2023-wq fitted round_up_16(8 + length) + 16 to such
/// readings ("a fit, not a proof"). With the average size of NtFC's blocks this hints at which link most of them came
/// from: a hint, never proof, since NtFC holds other records too.
/// </summary>
public static class LinkBlocks
{
    /// <summary>How close the average must be to a link's block size to say it matches.</summary>
    public const int Near = 8;

    /// <summary>The block retry loop leaves when Claude Code's temp folder is a junction: about 96 bytes (jallers, Ghostgfdsrhj).</summary>
    public const int TempLoop = 96;

    /// <summary>The size of the block an open through a link with this much reparse data keeps: round_up_16(8 + length) + 16.</summary>
    public static int Expected(int reparseDataLength) => (8 + reparseDataLength + 15) / 16 * 16 + 16;

    /// <summary>
    /// The average size of the blocks still held, in bytes: the bytes held over the blocks held (allocations less frees,
    /// counters that wrap at 32 bits). Null when none are held, or the counters do not make sense (more frees than
    /// allocations).
    /// </summary>
    public static long? Average(ulong used, uint allocs, uint frees)
    {
        uint held = unchecked(allocs - frees);
        return held == 0 || held > int.MaxValue ? null : (long)(used / held);
    }

    public static bool Matches(long average, int block) => Math.Abs(average - block) <= Near;
}
