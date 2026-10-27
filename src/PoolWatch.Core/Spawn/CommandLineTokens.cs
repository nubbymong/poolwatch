using System.Text;
using System.Text.RegularExpressions;

namespace PoolWatch.Core.Spawn;

/// <summary>Windows command-line splitting (CommandLineToArgvW rules for quotes and backslashes) and pattern folding.</summary>
public static partial class CommandLineTokens
{
    /// <summary>Splits a command line into arguments; element 0 is the program.</summary>
    public static List<string> Split(string? commandLine)
    {
        var args = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine)) return args;
        var cur = new StringBuilder();
        bool inQuotes = false, any = false;
        int i = 0, n = commandLine.Length;
        while (i < n)
        {
            char c = commandLine[i];
            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (any) { args.Add(cur.ToString()); cur.Clear(); any = false; }
                i++;
                continue;
            }
            if (c == '\\')
            {
                int bs = 0;
                while (i < n && commandLine[i] == '\\') { bs++; i++; }
                if (i < n && commandLine[i] == '"')
                {
                    cur.Append('\\', bs / 2);
                    if (bs % 2 == 1) { cur.Append('"'); i++; }
                }
                else cur.Append('\\', bs);
                any = true;
                continue;
            }
            if (c == '"')
            {
                if (inQuotes && i + 1 < n && commandLine[i + 1] == '"') { cur.Append('"'); i += 2; any = true; continue; }
                inQuotes = !inQuotes;
                any = true;
                i++;
                continue;
            }
            cur.Append(c);
            any = true;
            i++;
        }
        if (any) args.Add(cur.ToString());
        return args;
    }

    /// <summary>
    /// A command line folded into a pattern for grouping: the program name, then up to <paramref name="maxArgs"/>
    /// arguments with paths, numbers and long hex ids replaced by placeholders
    /// (<c>git -C &lt;path&gt; rev-parse --show-toplevel</c>).
    /// </summary>
    public static string Pattern(string image, string? commandLine, int maxArgs = 8)
    {
        var args = Split(commandLine);
        var sb = new StringBuilder(ProgramName(image, args.Count > 0 ? args[0] : null));
        int shown = 0;
        for (int k = 1; k < args.Count; k++)
        {
            if (shown == maxArgs) { sb.Append(" …"); break; }
            sb.Append(' ').Append(Fold(args[k]));
            shown++;
        }
        return sb.ToString();
    }

    private static string ProgramName(string image, string? argv0)
    {
        string name = !string.IsNullOrEmpty(image) ? image : Path.GetFileName(argv0 ?? "?");
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.ToLowerInvariant();
    }

    private static string Fold(string arg)
    {
        if (arg.Length == 0) return "\"\"";
        int eq = arg.IndexOf('=');
        if (eq > 0 && eq < arg.Length - 1 && !IsPath(arg[..eq])) return arg[..(eq + 1)] + FoldValue(arg[(eq + 1)..]);
        return FoldValue(arg);
    }

    private static string FoldValue(string v)
    {
        if (IsPath(v)) return "<path>";
        if (Number().IsMatch(v)) return "<n>";
        if (HexId().IsMatch(v)) return "<id>";
        return v.Length > 60 ? v[..60] + "…" : v;
    }

    /// <summary>Drive paths, UNC / rooted paths and relative paths with a separator; not options like <c>--a/b</c> or <c>/pid</c>.</summary>
    internal static bool IsPath(string v) =>
        (v.Length >= 2 && char.IsAsciiLetter(v[0]) && v[1] == ':') || v.StartsWith('\\') ||
        (v.StartsWith('/') ? v.IndexOfAny(['/', '\\'], 1) > 0 : !v.StartsWith('-') && (v.Contains('\\') || v.Contains('/')));

    [GeneratedRegex(@"^[+-]?\d+(\.\d+)?$")]
    private static partial Regex Number();

    [GeneratedRegex(@"^(\{?[0-9a-fA-F]{8}-[0-9a-fA-F-]{27,}\}?|[0-9a-fA-F]{12,})$")]
    private static partial Regex HexId();
}
