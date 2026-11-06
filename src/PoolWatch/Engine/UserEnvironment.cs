using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace PoolWatch.Engine;

/// <summary>
/// The account's own environment, read as data: the variables Windows gives a program the account starts (the
/// machine's, the account's own from HKCU\Environment, and its profile's), built by Windows' CreateEnvironmentBlock for
/// this process's account without inheriting anything of PoolWatch's own environment. PoolWatch itself runs with the
/// short allowlist its launcher builds, so what a program of the account will see is read from here: a task's or a
/// startup app's command line (%ChocolateyInstall%\..., %DriverData%\..., as Task Scheduler and Windows fill them in
/// from these values when that program starts) and the account's own TEMP folder (for the redaction). Only text is read
/// and filled in; nothing from it is applied to PoolWatch or loaded.
/// </summary>
internal static class UserEnvironment
{
    /// <summary>The account's environment (names without regard to case); empty when Windows could not build it.</summary>
    public static IReadOnlyDictionary<string, string> Read()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            if (!CreateEnvironmentBlock(out IntPtr block, id.Token, inherit: false)) return Empty;
            try { return Parse(block); }
            finally { DestroyEnvironmentBlock(block); }
        }
        catch { return Empty; }
    }

    /// <summary>
    /// <see cref="Expand"/> over the account's environment, read once now. When it could not be read nothing is filled in
    /// (a %name% stays as it is), never from PoolWatch's own environment.
    /// </summary>
    public static Func<string, string> Expander()
    {
        var vars = Read();
        return text => Expand(text, vars);
    }

    /// <summary>
    /// Pure: <paramref name="text"/> with each %name% that <paramref name="vars"/> holds replaced by its value, as Windows'
    /// ExpandEnvironmentStrings does: a name it does not hold is left as it is, and its closing % may open the next name.
    /// </summary>
    internal static string Expand(string text, IReadOnlyDictionary<string, string> vars)
    {
        var result = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            int start = text.IndexOf('%', i);
            int end = start < 0 ? -1 : text.IndexOf('%', start + 1);
            if (end < 0)
            {
                result.Append(text, i, text.Length - i);
                break;
            }
            result.Append(text, i, start - i);
            string name = text[(start + 1)..end];
            if (name.Length > 0 && vars.TryGetValue(name, out var value))
            {
                result.Append(value);
                i = end + 1;
            }
            else
            {
                result.Append(text, start, end - start); // "%name"; the closing % is read again
                i = end;
            }
        }
        return result.ToString();
    }

    /// <summary>A block as CreateEnvironmentBlock gives it: NAME=value strings, each ended by a NUL, then one more NUL. Windows' own =C:-style entries are left out.</summary>
    internal static unsafe Dictionary<string, string> Parse(IntPtr block)
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (char* p = (char*)block; *p != '\0';)
        {
            string entry = new(p);
            p += entry.Length + 1;
            int eq = entry.IndexOf('=');
            if (eq > 0) vars[entry[..eq]] = entry[(eq + 1)..];
        }
        return vars;
    }

    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);
}
