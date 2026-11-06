using PoolWatch.Engine;

namespace PoolWatch.Launcher;

/// <summary>
/// PoolWatch's launcher: the install folder's PoolWatch.exe (and PoolWatch-selfcontained.exe, the same file under a second name).
/// It starts PoolWatch itself, app\PoolWatch.exe below its own folder, in that app folder, with the arguments as given
/// and an environment built by <see cref="EnvironmentBlock"/> from Windows' own values (plus the folder the launcher was
/// started in, <see cref="EnvironmentBlock.StartedIn"/>), waits for it and ends with its exit code (the setup and the
/// uninstaller read those). Its manifest asks for administrator rights, so
/// PoolWatch gets them from it. It runs only with those rights, and only from a folder in Program Files that a standard
/// user cannot change, holding a PoolWatch program that a standard user cannot change either (<see cref="WhyNotStart"/>).
/// It takes no variable from the environment it was started with into the one it builds. Its own native .NET runtime does
/// read a few DOTNET_ settings of its own from that environment as it starts (its garbage collector's among them), and
/// none of them loads code into it.
/// </summary>
internal static class Program
{
    /// <summary>The folder below the launcher's own that holds PoolWatch itself.</summary>
    internal const string AppFolder = "app";

    /// <summary>PoolWatch's own program in <see cref="AppFolder"/>, which has the launcher's usual name, so Task Manager shows one name.</summary>
    internal const string ProgramName = "PoolWatch.exe";

    private static int Main(string[] args)
    {
        Native.AttachParentConsole();
        // Its own path as the kernel has it (a short 8.3 spelling, a junction or a subst drive resolved), so the checks
        // below and the program it starts are the files that are really there.
        if (Native.OwnFinalPath() is not { } self || Path.GetDirectoryName(self) is not { } folder)
            return Fail(args, "the launcher could not tell which folder it is in.");
        SystemPaths sys;
        try { sys = WindowsPaths.Read(); }
        catch (Exception ex) { return Fail(args, "the launcher could not read Windows' folders: " + ex.Message); }
        string app = ProgramIn(folder);
        if (WhyNotStart(Native.IsElevated(), folder, sys, self, app, File.Exists, ChangeableBy(sys)) is { } why) return Fail(args, why);
        var env = EnvironmentBlock.Build(sys);
        EnvironmentBlock.AddStartedIn(env, StartedFolder());
        string block = EnvironmentBlock.Serialize(env);
        if (CommandLine.Build(app, args) is not { } line) return Fail(args, "its command line is too long or holds a character it cannot pass on.");
        return Native.Run(app, line, block, AppFolderIn(folder), out int exit, out string? error) ? exit : Fail(args, error!);
    }

    /// <summary>Pure: PoolWatch's own program below the launcher's folder.</summary>
    internal static string ProgramIn(string folder) => Path.Combine(folder, AppFolder, ProgramName);

    /// <summary>Pure: PoolWatch's own app folder below the launcher's folder, where the launcher starts it.</summary>
    internal static string AppFolderIn(string folder) => Path.Combine(folder, AppFolder);

    /// <summary>
    /// The folder the launcher was started in (its current folder, which its caller chose), against which PoolWatch
    /// resolves the paths it is given; null when it cannot be read.
    /// </summary>
    internal static string? StartedFolder()
    {
        try { return Environment.CurrentDirectory; }
        catch { return null; }
    }

    /// <summary>
    /// Pure, over <paramref name="exists"/> and <paramref name="changeable"/>: why the launcher must not start PoolWatch,
    /// or null. It must have administrator rights itself (else PoolWatch would refuse to run and point back at the
    /// shortcut that was just used); it must be in a folder in Program Files (<see cref="IsInProgramFiles"/>); PoolWatch's
    /// program must be there below it (<paramref name="app"/>); and a standard user must be able to change neither the
    /// launcher (<paramref name="self"/>) nor that program, nor a folder either is in (<paramref name="changeable"/>: why
    /// one could, or null). A copy anywhere a standard user can put a program would lend its administrator prompt,
    /// PoolWatch's name and its signature to whatever program sat there under PoolWatch's name.
    /// </summary>
    internal static string? WhyNotStart(bool elevated, string folder, SystemPaths sys, string self, string app, Func<string, bool> exists,
        Func<string, string?> changeable)
    {
        if (!elevated)
            return "Windows started its launcher without administrator rights, which PoolWatch needs. A compatibility setting " +
                   "may run it without them (Properties, Compatibility), or User Account Control is off for an account that is " +
                   "not an administrator. Start it as an administrator.";
        if (!IsInProgramFiles(folder, sys))
            return $"PoolWatch runs as administrator only from a folder in {(sys.ProgramFiles.Length > 0 ? sys.ProgramFiles : "Program Files")}, " +
                   $"where only an administrator can change its files, and this copy is in {folder}. Install PoolWatch, or unpack it, " +
                   $"to {(sys.ProgramFiles.Length > 0 ? sys.ProgramFiles : "Program Files")}\\PoolWatch and start it from there.";
        if (!exists(app)) return $"{app} is not there. Install PoolWatch again.";
        foreach (var file in new[] { self, app })
            if (changeable(file) is { } open)
                return $"{open} So a program run without administrator rights could change what this launcher starts as administrator. " +
                       $"Install PoolWatch again, to {(sys.ProgramFiles.Length > 0 ? sys.ProgramFiles : "Program Files")}\\PoolWatch: the setup gives a " +
                       "new folder and its files the permissions of Program Files.";
        return null;
    }

    /// <summary>
    /// The folder rules PoolWatch's program applies to itself (Engine/AdminFolderRules.cs, the same source compiled into
    /// both): why someone who is not an administrator could change <c>file</c> or a folder it is in, up to Program Files
    /// as the machine's registry names it, where each folder really is (a link on the way followed, <see
    /// cref="Native.FolderFinalPathOf"/>), read only; null when only an administrator can. The account the launcher runs
    /// as counts as an administrator when every program of it runs as one (<see cref="ElevatedAccounts.OfThisProcess"/>).
    /// </summary>
    private static Func<string, string?> ChangeableBy(SystemPaths sys)
    {
        var places = new AdminPlaces([sys.ProgramFiles, sys.ProgramFilesX86], InstallGuard.ReadSecurity, Native.FolderFinalPathOf, InstallGuard.FolderEntries)
        {
            AlwaysElevated = ElevatedAccounts.OfThisProcess(),
        };
        return file => Describe(places.UserWritable(file));
    }

    /// <summary>Pure: what is said about a program a standard user could change (<see cref="OpenPlace"/>), or null for none.</summary>
    internal static string? Describe(OpenPlace? open) => open is null ? null : open.Because switch
    {
        OpenBecause.ProgramRules => $"The permissions of {open.Program} let users without administrator rights change it.",
        OpenBecause.FolderRules => $"The permissions of {open.Folder}, or of a folder above it, let users without administrator rights change what is in it.",
        OpenBecause.Link => $"{open.Program} is a link to a file elsewhere, whose folders cannot be checked.",
        OpenBecause.NotThere => $"{open.Program} is not there.",
        OpenBecause.OutsideAdminRoots => $"{open.Program} is not in Program Files.",
        OpenBecause.LinkedFolder => $"The folder of {open.Program} leads through a link to {open.Folder}, a place outside Program Files.",
        _ => $"The permissions of {open.Program}, {open.Folder} or a folder above it could not be read.",
    };

    /// <summary>
    /// Whether <paramref name="folder"/> is a folder inside Program Files or Program Files (x86), as the machine's
    /// registry names them (<see cref="SystemPaths"/>), not one of the two themselves; by its full path, without regard to case.
    /// </summary>
    internal static bool IsInProgramFiles(string folder, SystemPaths sys)
    {
        if (!EnvironmentBlock.IsFullPath(folder)) return false;
        string full;
        try { full = Path.GetFullPath(folder).TrimEnd('\\'); }
        catch { return false; }
        foreach (var root in new[] { sys.ProgramFiles, sys.ProgramFilesX86 })
            if (EnvironmentBlock.IsFullPath(root) && full.StartsWith(root.TrimEnd('\\') + @"\", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Says why PoolWatch could not start: on the console or in the output the caller redirected, and in a message box
    /// when someone started it to use it (the Start menu shortcut, the logon task); never a box for the setup's own
    /// steps, which must not wait for a click.
    /// </summary>
    private static int Fail(string[] args, string why)
    {
        string text = "PoolWatch could not start: " + why;
        try { Console.Error.WriteLine(text); } catch { }
        if (ShowsABox(args)) Native.ShowError(text);
        return ExitCodes.NotStarted;
    }

    /// <summary>A start to use PoolWatch: no arguments (the Start menu shortcut, the setup's "Start PoolWatch now") or only --tray (the logon task).</summary>
    internal static bool ShowsABox(IReadOnlyList<string> args) =>
        args.Count == 0 || (args.Count == 1 && string.Equals(args[0], "--tray", StringComparison.OrdinalIgnoreCase));
}
