using System.Security.Principal;

namespace PoolWatch.Engine;

/// <summary>
/// S4 (review): PoolWatch runs elevated (requireAdministrator) and starts at logon, so it must run from an admin-only
/// folder. If an elevated interactive run is launched from a user-writable location (the OneDrive Desktop, Downloads,
/// %LOCALAPPDATA%, %TEMP%, ...), an unelevated user could replace the executable. This refuses that run and says how to
/// install. Review round 2: every elevated mode is checked (tray, headless, self-test, the services / tasks / mark steps);
/// only the CI UI smoke, the token guard's own helper and an explicit <c>--allow-any-location</c> (CI, development) are
/// exempt (Program.Main). Since the release no longer self-extracts native DLLs into %TEMP% (PoolWatch.csproj), the DLLs
/// next to the exe are covered by the same rule.
/// </summary>
internal static class InstallGuard
{
    public static bool IsElevated
    {
        get { try { using var id = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); } catch { return false; } }
    }

    /// <summary>
    /// Whether this run is checked (S4, review round 2): every mode except the CI UI smoke, the token guard's own hidden
    /// helper (started by a PoolWatch that passed the check, from its own path) and an explicit --allow-any-location.
    /// </summary>
    public static bool Applies(Headless.CommandLineOptions opt) => opt.UiSmokeSeconds is null && opt.TokenLeakTest is null && !opt.AllowAnyLocation;

    /// <summary>
    /// The admin-only roots an elevated PoolWatch may run from: %ProgramFiles% and %ProgramFiles(x86)% only. Review round 3:
    /// not %WINDIR%, where some folders are writable by every user (Windows\Temp, Windows\Tasks, Windows\tracing, ...).
    /// </summary>
    internal static IEnumerable<string> AdminRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    }

    /// <summary>Whether this exe path is inside an admin-only install root (<see cref="AdminRoots"/>).</summary>
    public static bool IsTrustedInstallPath(string exePath) => IsUnderAdminRoot(exePath, AdminRoots());

    /// <summary>True when this elevated run is from a user-writable folder; <paramref name="message"/> says what to do.</summary>
    public static bool RefuseElevatedFromUserWritable(out string message)
    {
        message = "";
        if (!OperatingSystem.IsWindows() || !IsElevated) return false;
        string? exe = Environment.ProcessPath;
        if (exe is null) return false;
        string full = Path.GetFullPath(exe);
        if (IsUnderAdminRoot(full, AdminRoots())) return false;
        message = "PoolWatch runs as administrator and must not run from a folder you can write to without administrator " +
                  "rights (an attacker could replace it there).\n\n" +
                  $"It is at:\n{full}\n\n" +
                  $"Install it to {Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)}\\PoolWatch and start it from there " +
                  "(the logon task should point there too).";
        return true;
    }

    /// <summary>Pure, testable: is <paramref name="exePath"/> inside one of the admin-only <paramref name="adminRoots"/>?</summary>
    public static bool IsUnderAdminRoot(string exePath, IEnumerable<string> adminRoots)
    {
        string full = Path.GetFullPath(exePath);
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var root in adminRoots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full.Equals(r, cmp) || full.StartsWith(r + Path.DirectorySeparatorChar, cmp)) return true;
        }
        return false;
    }
}
