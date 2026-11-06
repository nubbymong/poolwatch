using System.Security.Principal;

namespace PoolWatch.Engine;

/// <summary>
/// PoolWatch runs elevated (through its launcher) and starts at logon, so it must run from an admin-only
/// folder. If an elevated interactive run is launched from a user-writable location (the OneDrive Desktop, Downloads,
/// %LOCALAPPDATA%, %TEMP%, ...), an unelevated user could replace the executable. This refuses that run and says how to
/// install. Every elevated mode is checked (tray, headless, self-test, the services / tasks / mark steps);
/// only the CI UI smoke, the token guard's own helper and an explicit <c>--allow-any-location</c> (CI, development) are
/// exempt (Program.Main). A program counts as admin-only by its place (Program Files), by the access rules of its folders
/// and of the file itself, and, when a folder on the way is a link, by those of the folders it really is in (<see
/// cref="UserWritable"/>). PoolWatch itself also needs every file and folder in its own folder to be admin-only, by their
/// own rules, since it loads its DLLs from there (app\amd64\ among them: <see cref="ChangeableInItsFolder"/>), and its
/// launcher and every file below its own folder to be admin-only with the folders each is in (<see
/// cref="WhyNotStartFrom"/>). Every account and group that is not an administrator counts (<see cref="IsAdmin"/>),
/// whoever is signed in, except an account every program of which runs as administrator by this PC's UAC settings (<see
/// cref="AlwaysElevated"/>). Read only. The same judgement decides when putting back a clear task of your own needs your
/// yes (ClearTaskReplacer). The rules themselves are in AdminFolderRules.cs, which the launcher compiles too.
/// </summary>
internal static partial class InstallGuard
{
    public static bool IsElevated
    {
        get { try { using var id = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); } catch { return false; } }
    }

    /// <summary>
    /// Whether this run is checked: every mode except the CI UI smoke, the token guard's own hidden
    /// helper (started by a PoolWatch that passed the check, from its own path) and an explicit --allow-any-location.
    /// </summary>
    public static bool Applies(Headless.CommandLineOptions opt) => opt.UiSmokeSeconds is null && opt.TokenLeakTest is null && !opt.AllowAnyLocation;

    /// <summary>
    /// The admin-only roots an elevated PoolWatch may run from: %ProgramFiles% and %ProgramFiles(x86)% only,
    /// not %WINDIR%, where some folders are writable by every user (Windows\Temp, Windows\Tasks, Windows\tracing, ...).
    /// </summary>
    internal static IEnumerable<string> AdminRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    }

    /// <summary>Whether this exe path is inside an admin-only install root (<see cref="AdminRoots"/>), by its location only.</summary>
    public static bool IsTrustedInstallPath(string exePath) => IsUnderAdminRoot(exePath, AdminRoots());

    /// <summary>True when this elevated run is from a user-writable folder; <paramref name="message"/> says what to do.</summary>
    public static bool RefuseElevatedFromUserWritable(out string message)
    {
        message = "";
        if (!OperatingSystem.IsWindows() || !IsElevated) return false;
        string? exe = Environment.ProcessPath;
        if (exe is null) return false;
        message = WhyNotStartFrom(exe, AdminPlaces.OfThisPc(), File.Exists, FilesBelow) ?? "";
        return message.Length > 0;
    }

    /// <summary>
    /// <see cref="WhyNotFrom"/> for this program (app\PoolWatch.exe in the install folder), then for its launcher there
    /// (<see cref="LaunchGate.LaunchersOf"/>: PoolWatch.exe, and PoolWatch-selfcontained.exe when that one is there too),
    /// which the logon task starts as administrator with no prompt and the Start menu entry starts after one, then for
    /// every other file below the program's own folder (<paramref name="filesBelow"/>; on this PC <see cref="FilesBelow"/>:
    /// the native DLLs PoolWatch loads from app\amd64\ among them), each with the folders it is in (<see
    /// cref="UserWritable"/>): a run is refused when someone who is not an administrator could change any of them (or
    /// there is no launcher, or one is a link), and when the files below its folder cannot be listed.
    /// </summary>
    internal static string? WhyNotStartFrom(string exe, AdminPlaces places, Func<string, bool> exists, Func<string, IEnumerable<string>> filesBelow)
    {
        if (WhyNotFrom(exe, places) is { } why) return why;
        var launchers = LaunchGate.LaunchersOf(exe).Where(exists).ToList();
        if (launchers.Count == 0) launchers.Add(LaunchGate.ExpectedLauncher(exe)); // judged as not there
        foreach (var launcher in launchers)
            if (Explain(places.UserWritable(launcher), Path.GetFullPath(launcher)) is { } not) return not;
        string own = Path.GetFullPath(exe), folder = Path.GetDirectoryName(own) ?? own;
        List<string> files;
        try { files = filesBelow(folder).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return $"PoolWatch runs as administrator, and it could not list the files in {folder} to check that users without administrator " +
                   $"rights cannot change them ({ex.Message}). Uninstall PoolWatch, delete that folder, and install it again.";
        }
        foreach (var file in files)
            if (!Path.GetFullPath(file).Equals(own, StringComparison.OrdinalIgnoreCase) && Explain(places.UserWritable(file), Path.GetFullPath(file)) is { } not) return not;
        return null;
    }

    /// <summary>Every file in <paramref name="folder"/> and in the folders below it, none left out for its attributes; throws when one cannot be listed.</summary>
    internal static IEnumerable<string> FilesBelow(string folder) =>
        Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false });

    /// <summary>
    /// Pure over <paramref name="places"/>: why an elevated PoolWatch must not run from <paramref name="exe"/> (a folder
    /// outside the admin-only roots, a folder or file whose access rules let someone who is not an administrator change it
    /// or could not be read, an exe that is a link, a folder that leads elsewhere, or a file or folder in its own folder
    /// that is not admin-only), or null when it may.
    /// </summary>
    internal static string? WhyNotFrom(string exe, AdminPlaces places)
    {
        string full = Path.GetFullPath(exe);
        return Explain(places.UserWritable(full) ?? places.ChangeableInItsFolder(full), full);
    }

    /// <summary>What a refused run says about <paramref name="full"/> for <paramref name="open"/> (null: nothing, it may run).</summary>
    private static string? Explain(OpenPlace? open, string full)
    {
        if (open is null) return null;
        string start = "PoolWatch runs as administrator and must not run from a folder you can write to without administrator " +
                       "rights (an attacker could replace it there).\n\n" +
                       $"It is at:\n{full}\n\n";
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string reinstall = $"Uninstall PoolWatch, delete that folder, and install it again to {programFiles}\\PoolWatch: setup gives a new " +
                           "folder and its files the permissions of Program Files.";
        return open.Because switch
        {
            OpenBecause.OutsideAdminRoots =>
                start + $"Install it to {programFiles}\\PoolWatch and start it from there (the logon task should point there too).",
            OpenBecause.ProgramRules =>
                start + $"The permissions of {full} let users without administrator rights change it (a file moved there keeps the " +
                $"permissions it had). The folder is {open.Folder}. " + reinstall,
            OpenBecause.Unreadable or OpenBecause.NotThere => // not there: the running exe, renamed or moved since it started
                start + $"PoolWatch could not read the permissions of {open.Program}, {open.Folder} or a folder above it, so it cannot " +
                "tell whether users without administrator rights may change them. " + reinstall,
            OpenBecause.Link =>
                start + $"{full} is a link to a file elsewhere, so PoolWatch cannot check the folders that file is in. " + reinstall,
            OpenBecause.LinkedFolder =>
                start + $"{Path.GetDirectoryName(full)} leads through a link to {open.Folder}, a place outside Program Files. " + reinstall,
            OpenBecause.InItsFolder =>
                start + $"The permissions of {open.Program}, in PoolWatch's folder, let users without administrator rights change it, " +
                "and PoolWatch loads what is in its folder. " + reinstall,
            OpenBecause.LinkInItsFolder =>
                start + $"{open.Program}, in PoolWatch's folder, is a link to something elsewhere, which PoolWatch cannot check. " + reinstall,
            OpenBecause.TooMuchInItsFolder =>
                start + $"{open.Folder} holds more than {MaxEntriesInItsFolder:N0} files and folders, more than PoolWatch's own, so " +
                "PoolWatch does not check them all. " + reinstall,
            OpenBecause.TooDeepInItsFolder =>
                start + $"{open.Folder} holds folders more than {MaxDepthInItsFolder} levels deep ({open.Program}), far deeper than " +
                "PoolWatch's own, so PoolWatch does not check them all. " + reinstall,
            _ => start + $"The permissions of {open.Folder} or a folder above it let users without administrator rights change what is " +
                 "in it. " + reinstall,
        };
    }

    /// <summary>Where a folder really is, every link on the way followed (read only); null when it cannot be opened.</summary>
    internal static string? RealPath(string folder) => NoFollow.ResolvedPath(folder, dir: true);
}

internal sealed partial record AdminPlaces
{
    /// <summary>
    /// This PC's: Program Files, the access rules as they are, where folders really are, what they hold, and the accounts
    /// whose every program runs as administrator by its UAC settings.
    /// </summary>
    public static AdminPlaces OfThisPc() =>
        new(InstallGuard.AdminRoots().ToList(), InstallGuard.ReadSecurity, InstallGuard.RealPath, InstallGuard.FolderEntries) { AlwaysElevated = Engine.AlwaysElevated.OfThisPc() };
}
