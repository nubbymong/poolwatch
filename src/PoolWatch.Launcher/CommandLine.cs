using System.Text;

namespace PoolWatch.Launcher;

/// <summary>
/// The command line PoolWatch's program is started with: its own full path in quotes, then each of the launcher's arguments
/// quoted so that PoolWatch reads back exactly the same arguments. The rules are the ones Windows programs use to split
/// a command line (the C runtime's, which CommandLineToArgvW shares): an argument with a space, a tab or a quote is put
/// in quotes; inside them a quote is written as \", and backslashes are doubled only where they come before a quote,
/// the closing one included.
/// </summary>
internal static class CommandLine
{
    /// <summary>CreateProcessW's limit on a command line, in characters, with its ending NUL.</summary>
    public const int MaxLength = 32_767;

    /// <summary>
    /// The whole command line; null when it cannot be written safely: a program path with a quote in it (no Windows
    /// path has one), an argument with a NUL, or a line longer than <see cref="MaxLength"/>.
    /// </summary>
    public static string? Build(string program, IReadOnlyList<string> args)
    {
        if (program.Contains('"') || program.Contains('\0')) return null;
        var line = new StringBuilder().Append('"').Append(program).Append('"');
        foreach (var arg in args)
        {
            if (arg.Contains('\0')) return null;
            Append(line.Append(' '), arg);
        }
        return line.Length < MaxLength ? line.ToString() : null;
    }

    /// <summary>One argument, quoted as needed (see the class).</summary>
    public static void Append(StringBuilder line, string arg)
    {
        if (arg.Length > 0 && !NeedsQuotes(arg))
        {
            line.Append(arg);
            return;
        }
        line.Append('"');
        for (int i = 0; ; i++)
        {
            int backslashes = 0;
            while (i < arg.Length && arg[i] == '\\')
            {
                backslashes++;
                i++;
            }
            if (i == arg.Length)
            {
                // Before the closing quote: each backslash doubled, so none escapes it.
                line.Append('\\', backslashes * 2);
                break;
            }
            if (arg[i] == '"')
                line.Append('\\', backslashes * 2 + 1).Append('"'); // the backslashes doubled, then \"
            else
                line.Append('\\', backslashes).Append(arg[i]);      // backslashes not before a quote stay as they are
        }
        line.Append('"');
    }

    private static bool NeedsQuotes(string arg)
    {
        foreach (char c in arg)
            if (char.IsWhiteSpace(c) || c == '"') return true;
        return false;
    }
}
