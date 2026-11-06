using System.Collections;
using System.ComponentModel;
using System.Diagnostics;

namespace PoolWatch.Engine;

/// <summary>
/// How PoolWatch is laid out and how it starts as administrator. The install folder's PoolWatch.exe is its launcher
/// (src/PoolWatch.Launcher, native code: no .NET runtime is loaded into it), and this program is app\PoolWatch.exe below
/// it, with the same name. Everything that starts PoolWatch starts the launcher: the logon task, the Start menu entry,
/// Explorer, the setup and the uninstaller, and a logon task or a shortcut made by a version before 1.0, which named the
/// program's place then. The launcher starts this program with an environment it builds from Windows' own values (a
/// short allowlist), so nothing a program running as the user put in the account's environment (.NET's start-up
/// variables among it, which make a .NET program load a profiler or other code before its own code runs) reaches it.
/// This is for every other start of this program, checked first thing in Program.Main:
/// <list type="bullet">
/// <item>Not elevated (app\PoolWatch.exe opened by hand: its manifest does not ask for administrator rights): the start
/// is handed to the launcher, whose own manifest asks, so the administrator prompt is the launcher's. This program never
/// runs elevated from here.</item>
/// <item>Elevated but not started by the launcher (an administrator who starts app\PoolWatch.exe from their own elevated
/// console, or with Run as administrator on that file): PoolWatch starts again through the launcher, and this copy does
/// no work of its own (in a console mode it waits for that PoolWatch and passes its output and exit code on; otherwise it
/// ends at once). .NET read this copy's environment before Main ran, which nothing in PoolWatch can change (see
/// SECURITY.md); start-up hooks are off in the build (StartupHookSupport).</item>
/// <item>Started by the launcher: it runs, unless its environment still holds one of .NET's start-up variables, which
/// the launcher never passes on; then it refuses (it never starts the launcher again, so the two cannot loop).</item>
/// </list>
/// The switches that skip the location check (<see cref="InstallGuard.Applies"/>: CI, development and the token-leak
/// test's helper, which a PoolWatch started by the launcher starts and which inherits its clean environment) run as they
/// are once elevated, without the launcher.
/// </summary>
internal static class LaunchGate
{
    /// <summary>The folder in the install folder that holds this program (the launcher's Program.AppFolder; a test holds the two together).</summary>
    public const string AppFolderName = "app";

    /// <summary>This program's file name (the launcher's Program.ProgramName; a test holds the two together).</summary>
    public const string ProgramFileName = "PoolWatch.exe";

    /// <summary>
    /// The names the launcher has in an install folder, the first one the usual one: PoolWatch.exe and
    /// PoolWatch-selfcontained.exe. Before 1.0 the program itself had these names there (the setup's and the
    /// framework-dependent zip's, and the self-contained zip's). Both zips carry the launcher under both names, and the
    /// setup puts it under the second one too where a file of that name is there already, so a logon task or a shortcut
    /// made then starts the launcher now, whichever copy a zip was unpacked over. A file with one of these names counts as
    /// the launcher only when it is of the same build as the program below it (<see cref="Judge"/>).
    /// </summary>
    public static readonly IReadOnlyList<string> LauncherFileNames = [ProgramFileName, "PoolWatch-selfcontained.exe"];

    /// <summary>What the launcher adds to the environment it builds (its EnvironmentBlock.Marker; a test holds the two together).</summary>
    public const string Marker = "POOLWATCH_LAUNCHER";

    /// <summary>The variables that make .NET load code or change how it runs (the launcher's EnvironmentBlock.KnobPrefixes; a test holds the two together).</summary>
    public static readonly IReadOnlyList<string> KnobPrefixes = ["DOTNET_", "COMPlus_", "CORECLR_", "COR_"];

    public enum Step { Run, Elevate, Restart, Refuse }

    /// <summary>
    /// Pure: what this start does (see the class). <paramref name="exempt"/>: a switch that skips the location check;
    /// <paramref name="fromLauncher"/>: the launcher's marker is set; <paramref name="hasKnob"/>: one of .NET's start-up
    /// variables is in the environment.
    /// </summary>
    public static Step Decide(bool elevated, bool exempt, bool fromLauncher, bool hasKnob)
    {
        if (!elevated) return fromLauncher ? Step.Refuse : Step.Elevate;
        if (exempt) return Step.Run;
        if (!fromLauncher) return Step.Restart;
        return hasKnob ? Step.Refuse : Step.Run;
    }

    public static bool IsKnob(string name) => KnobPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>The names of .NET's start-up variables in <paramref name="environment"/>, sorted.</summary>
    public static List<string> KnobsIn(IDictionary environment) =>
        environment.Keys.OfType<string>().Where(IsKnob).Order(StringComparer.OrdinalIgnoreCase).ToList();

    public static bool StartedByLauncher(IDictionary environment) =>
        environment.Contains(Marker) && environment[Marker] is "1";

    /// <summary>What the launcher passes the folder it was started in through (its EnvironmentBlock.StartedIn; a test holds the two together).</summary>
    public const string StartedInVariable = "POOLWATCH_STARTED_IN";

    /// <summary>
    /// Pure over <paramref name="environment"/>: the folder a file named on the command line by a relative path is read
    /// against. Started by the launcher, which starts PoolWatch in its own app folder, it is the folder the launcher was
    /// started in (<see cref="StartedInVariable"/>, a full path); otherwise <paramref name="current"/>, this process's own
    /// current folder.
    /// </summary>
    public static string StartedIn(IDictionary environment, string current) =>
        StartedByLauncher(environment) && environment.Contains(StartedInVariable) && environment[StartedInVariable] is string folder &&
        Path.IsPathFullyQualified(folder) ? folder : current;

    /// <summary>
    /// Pure over <paramref name="environment"/> (this process's, in <see cref="Check"/>): what this start does (<see
    /// cref="Decide"/>), and the names of .NET's start-up variables in it.
    /// </summary>
    internal static (Step Step, List<string> Knobs) Classify(IDictionary environment, bool elevated, bool exempt)
    {
        var knobs = KnobsIn(environment);
        return (Decide(elevated, exempt, StartedByLauncher(environment), knobs.Count > 0), knobs);
    }

    /// <summary>
    /// Pure: the install folder of the program at <paramref name="programExe"/>: the folder above its own, when its own
    /// is the install's <see cref="AppFolderName"/> folder; null otherwise (a build output, a copy laid out another way).
    /// </summary>
    public static string? InstallFolderOf(string programExe)
    {
        string? folder = Path.GetDirectoryName(Path.GetFullPath(programExe));
        return folder is not null && Path.GetFileName(folder).Equals(AppFolderName, StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(folder) : null;
    }

    /// <summary>
    /// The folder this copy is installed in: the install folder, above this program's app folder, which holds the launcher
    /// and the files that ship beside it (the installer's uninstaller, LICENSE, THIRD-PARTY-NOTICES.md, the Codex
    /// shortcut's script), and which PoolWatch never writes into (AppPaths' PathGuard); this program's own folder when it
    /// is not in an app folder (a build output).
    /// </summary>
    public static string InstallFolder() =>
        Environment.ProcessPath is { } exe && InstallFolderOf(exe) is { } install ? install : AppContext.BaseDirectory;

    /// <summary>Pure: where the launcher of the program at <paramref name="programExe"/> may be, by each of its names; none when the program is not in an install's app folder.</summary>
    public static IEnumerable<string> LaunchersOf(string programExe) =>
        InstallFolderOf(programExe) is { } install ? LauncherFileNames.Select(n => Path.Combine(install, n)) : [];

    /// <summary>The launcher that starts the program at <paramref name="programExe"/>: the first of <see cref="LaunchersOf"/> that is one (<see cref="IsLauncher"/>), or null.</summary>
    public static string? LauncherOf(string programExe, LayoutFiles files) => LaunchersOf(programExe).FirstOrDefault(p => IsLauncher(p, files));

    /// <summary>Pure: where the usual launcher of the program at <paramref name="programExe"/> belongs, for what is said when it is not there.</summary>
    public static string ExpectedLauncher(string programExe) =>
        Path.Combine(InstallFolderOf(programExe) ?? Path.GetDirectoryName(Path.GetFullPath(programExe)) ?? "", LauncherFileNames[0]);

    /// <summary>What a file with one of the launcher's names is (<see cref="Judge"/>).</summary>
    public enum LauncherState
    {
        /// <summary>The launcher of the program below it: that program is there, and the two have the same product version.</summary>
        Launcher,
        /// <summary>No file there.</summary>
        NotThere,
        /// <summary>No app\PoolWatch.exe below it: PoolWatch's program itself as it was installed before 1.0.</summary>
        NoProgramBelow,
        /// <summary>A program is below it, but the two have other product versions (or one could not be read): an older PoolWatch left under the launcher's name.</summary>
        OtherVersion,
    }

    /// <summary>
    /// Pure over <paramref name="files"/>: what the file at <paramref name="path"/> is. The launcher is a file with one of
    /// <see cref="LauncherFileNames"/> with this program below it (app\PoolWatch.exe) whose product version (the build's
    /// version and commit, which CI holds equal for the launcher and the program of a build) is that program's. By file
    /// name alone the launcher and an older PoolWatch.exe, which was the program itself, look the same, and a folder where
    /// one zip was unpacked over a copy of the other can hold an older program under a launcher's name above this program;
    /// a file of another build has another product version.
    /// </summary>
    public static LauncherState Judge(string path, LayoutFiles files)
    {
        if (!files.Exists(path)) return LauncherState.NotThere;
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is not { } folder) return LauncherState.NoProgramBelow;
        string program = Path.Combine(folder, AppFolderName, ProgramFileName);
        if (!files.Exists(program)) return LauncherState.NoProgramBelow;
        return files.VersionOf(path) is { Length: > 0 } version && string.Equals(version, files.VersionOf(program), StringComparison.Ordinal)
            ? LauncherState.Launcher : LauncherState.OtherVersion;
    }

    /// <summary>Whether <paramref name="path"/> is the launcher of an install: one of <see cref="LauncherFileNames"/>, and <see cref="Judge"/> finds it is.</summary>
    public static bool IsLauncher(string path, LayoutFiles files) => IsPoolWatchName(path) && Judge(path, files) == LauncherState.Launcher;

    /// <summary>The product version of the program at <paramref name="path"/> (its version resource), or null when it has none or cannot be read.</summary>
    public static string? ProductVersionOf(string path)
    {
        try { return FileVersionInfo.GetVersionInfo(path).ProductVersion; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>Whether <paramref name="path"/> has one of the names PoolWatch's programs have (<see cref="LauncherFileNames"/>), by its file name.</summary>
    public static bool IsPoolWatchName(string path) => LauncherFileNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Run first in Program.Main, once the arguments are read: null when this PoolWatch runs; otherwise the exit code of
    /// the start it handed over or refused. <paramref name="wait"/>: a console mode, which waits for the PoolWatch the
    /// launcher starts, passes on its output and ends with its exit code; the tray app hands over and ends at once.
    /// </summary>
    public static int? Check(string[] args, bool exempt, bool wait)
    {
        var (step, knobs) = Classify(Environment.GetEnvironmentVariables(), InstallGuard.IsElevated, exempt);
        if (step == Step.Run) return null;
        if (step == Step.Refuse)
            return Refuse(wait, InstallGuard.IsElevated
                ? $"PoolWatch was started with .NET start-up variables in its environment ({string.Join(", ", knobs)}), which its launcher " +
                  "never passes on, so it did not start. Start PoolWatch from its Start menu shortcut."
                : "PoolWatch needs administrator rights, and it was started without them. Start PoolWatch from its Start menu shortcut.");
        if (Environment.ProcessPath is not { } exe)
            return Refuse(wait, "PoolWatch could not tell where it is, so it could not start through its launcher. Start PoolWatch from its Start menu shortcut.", ExitCodes.NotStarted);
        if (LauncherOf(exe, LayoutFiles.OnDisk) is not { } launcher)
            return Refuse(wait, $"PoolWatch starts through its launcher, {ExpectedLauncher(exe)}, which is not there, or is not of this PoolWatch's version. Install PoolWatch again.", ExitCodes.NotStarted);
        return HandOver(launcher, args, elevate: step == Step.Elevate, wait);
    }

    /// <summary>
    /// Starts the launcher with <paramref name="args"/> as they are, by its full path: through the shell with the
    /// administrator prompt when this PoolWatch is not elevated (only the shell can show it), directly when it is.
    /// </summary>
    private static int HandOver(string launcher, string[] args, bool elevate, bool wait)
    {
        var psi = new ProcessStartInfo(launcher)
        {
            UseShellExecute = elevate, Verb = elevate ? "runas" : "", WorkingDirectory = Environment.CurrentDirectory,
            // An elevated console mode passes the output on itself: .NET gives a child no standard handles unless it redirects them.
            RedirectStandardOutput = wait && !elevate, RedirectStandardError = wait && !elevate,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        Process? started;
        try { started = Process.Start(psi); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return ExitCodes.NotStarted; } // ERROR_CANCELLED: the prompt was declined
        catch (Exception ex) { return Refuse(wait, $"PoolWatch could not start {launcher}: {ex.Message}", ExitCodes.NotStarted); }
        using var launched = started;
        if (!wait || launched is null) return ExitCodes.Done;
        // Ctrl+C reaches the PoolWatch the launcher started, which stops on its own; this copy waits for it.
        ConsoleCancelEventHandler keep = (_, e) => e.Cancel = true;
        Console.CancelKeyPress += keep;
        try
        {
            if (psi.RedirectStandardOutput)
            {
                var output = launched.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
                var errors = launched.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
                launched.WaitForExit();
                Task.WaitAll([output, errors], TimeSpan.FromSeconds(10));
            }
            else launched.WaitForExit();
            return launched.ExitCode;
        }
        finally { Console.CancelKeyPress -= keep; }
    }

    private static int Refuse(bool console, string why, int code = ExitCodes.Refused)
    {
        Console.Error.WriteLine(why);
        if (!console) MessageBox.Show(why, "PoolWatch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return code;
    }
}

/// <summary>
/// What <see cref="LaunchGate"/> knows of the files it judges: whether one is there, and its product version (null when
/// it has none or cannot be read). Tests pass made-up files.
/// </summary>
internal sealed record LayoutFiles(Func<string, bool> Exists, Func<string, string?> VersionOf)
{
    /// <summary>The real files.</summary>
    public static readonly LayoutFiles OnDisk = new(File.Exists, LaunchGate.ProductVersionOf);
}
