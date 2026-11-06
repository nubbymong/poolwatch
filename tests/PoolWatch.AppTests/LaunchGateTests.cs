extern alias launcher;
using System.Collections;
using System.Text.Json;
using System.Text.RegularExpressions;
using PoolWatch.Engine;
using static PoolWatch.Engine.LaunchGate;
using EnvironmentBlock = launcher::PoolWatch.Launcher.EnvironmentBlock;
using LauncherProgram = launcher::PoolWatch.Launcher.Program;
using SystemPaths = launcher::PoolWatch.Launcher.SystemPaths;

namespace PoolWatch.AppTests;

/// <summary>
/// An elevated PoolWatch runs only with the environment its launcher built (Engine/LaunchGate.cs): the layout (the
/// launcher is the install folder's PoolWatch.exe, the program app\PoolWatch.exe below it), the decision for each kind of
/// start, the names the app and the launcher must agree on, the order in Program.Main, the two manifests, and the runtime
/// setting that turns start-up hooks off. Pure or read from the build and the repository; nothing is started.
/// </summary>
public sealed class LaunchGateTests
{
    [Theory]
    // Opened by hand without administrator rights: handed to the launcher, which asks for them.
    [InlineData(false, false, false, false, "Elevate")]
    [InlineData(false, false, false, true, "Elevate")]
    [InlineData(false, true, false, false, "Elevate")]   // a CI or development switch still needs an administrator start
    // Not elevated although the launcher started it: refused, never handed back to the launcher (no loop).
    [InlineData(false, false, true, false, "Refuse")]
    [InlineData(false, true, true, true, "Refuse")]
    // Elevated by something else (an administrator's own console started app\PoolWatch.exe): started again through it.
    [InlineData(true, false, false, false, "Restart")]
    [InlineData(true, false, false, true, "Restart")]
    // Started by the launcher: runs, unless a .NET start-up variable is there all the same.
    [InlineData(true, false, true, false, "Run")]
    [InlineData(true, false, true, true, "Refuse")]
    // CI, development and the token-leak test's helper, elevated: as they are.
    [InlineData(true, true, false, false, "Run")]
    [InlineData(true, true, false, true, "Run")]
    [InlineData(true, true, true, true, "Run")]
    public void EachStartRunsOnlyWithTheLaunchersEnvironment(bool elevated, bool exempt, bool fromLauncher, bool hasKnob, string step) =>
        Assert.Equal(Enum.Parse<Step>(step), Decide(elevated, exempt, fromLauncher, hasKnob));

    /// <summary>
    /// What a start does is decided from the environment itself (<see cref="Classify"/>): the launcher's block runs, the
    /// caller's own environment is started again through the launcher, and the marker set by hand next to a start-up
    /// variable is refused.
    /// </summary>
    [Fact]
    public void WhatTheLauncherBuildsPassesTheGateAndAHostileEnvironmentDoesNot()
    {
        var sys = new SystemPaths(@"C:\Windows", @"C:\Windows\system32", @"C:\Program Files", @"C:\Program Files (x86)", @"C:\Program Files\Common Files",
            @"C:\Program Files (x86)\Common Files", @"C:\ProgramData", @"C:\Users\Public", @"C:\Users\alice", @"C:\Users\alice\AppData\Roaming",
            @"C:\Users\alice\AppData\Local", "alice", "PC1", "PC1");
        var built = new Hashtable(EnvironmentBlock.Build(sys), StringComparer.OrdinalIgnoreCase);
        Assert.Equal((Step.Run, 0), Summary(Classify(built, elevated: true, exempt: false)));

        // The caller's own environment, as app\PoolWatch.exe started without the launcher has it.
        var direct = new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["CORECLR_ENABLE_PROFILING"] = "1", ["COR_PROFILER"] = "{x}", ["DOTNET_gcServer"] = "1", ["PATH"] = @"C:\Users\alice\bin",
        };
        var (step, knobs) = Classify(direct, elevated: true, exempt: false);
        Assert.Equal(Step.Restart, step);
        Assert.Equal(["CORECLR_ENABLE_PROFILING", "COR_PROFILER", "DOTNET_gcServer"], knobs);
        // The marker set by hand does not let a start-up variable through.
        direct[Marker] = "1";
        Assert.Equal(Step.Refuse, Classify(direct, elevated: true, exempt: false).Step);
        direct.Remove("CORECLR_ENABLE_PROFILING");
        direct.Remove("COR_PROFILER");
        Assert.Equal(Step.Refuse, Classify(direct, elevated: true, exempt: false).Step); // one is enough
        direct.Remove("DOTNET_gcServer");
        Assert.Equal(Step.Run, Classify(direct, elevated: true, exempt: false).Step);
        // Not elevated, whatever the environment: handed to the launcher, or refused once it has the marker.
        Assert.Equal(Step.Refuse, Classify(direct, elevated: false, exempt: false).Step);
        Assert.Equal(Step.Elevate, Classify(new Hashtable(), elevated: false, exempt: false).Step);

        static (Step, int) Summary((Step Step, List<string> Knobs) c) => (c.Step, c.Knobs.Count);
    }

    /// <summary>Check decides from this process's own environment and rights, and refuses or hands over on what <see cref="Classify"/> says.</summary>
    [Fact]
    public void TheStartCheckReadsThisProcesssEnvironment()
    {
        string check = MainWindowLayoutTests.Block(File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Engine", "LaunchGate.cs")),
            "public static int? Check(string[] args, bool exempt, bool wait)");
        Assert.StartsWith("var (step, knobs) = Classify(Environment.GetEnvironmentVariables(), InstallGuard.IsElevated, exempt); if (step == Step.Run) return null; " +
                          "if (step == Step.Refuse) return Refuse(wait,", Regex.Replace(check, @"\s+", " ").Trim());
        Assert.Contains("return HandOver(launcher, args, elevate: step == Step.Elevate, wait);", check);
        Assert.Contains("if (LauncherOf(exe, LayoutFiles.OnDisk) is not { } launcher)", check);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData("yes", false)]
    [InlineData(" 1", false)]
    public void OnlyTheLaunchersOwnMarkerValueCounts(string value, bool fromLauncher)
    {
        Assert.Equal(fromLauncher, StartedByLauncher(new Hashtable(StringComparer.OrdinalIgnoreCase) { [Marker] = value }));
        Assert.False(StartedByLauncher(new Hashtable(StringComparer.OrdinalIgnoreCase)));
    }

    private const string Install = @"C:\Program Files\PoolWatch", Program = Install + @"\app\PoolWatch.exe";
    private const string Usual = Install + @"\PoolWatch.exe", SelfContained = Install + @"\PoolWatch-selfcontained.exe";
    private const string ThisBuild = "1.0.0+1111111", Beta1 = "1.0.0-beta.1+9ffe77e";

    /// <summary>Made-up files: each path there with its product version (null: a file with no version resource).</summary>
    private static LayoutFiles Files(params (string Path, string? Version)[] files)
    {
        var map = files.ToDictionary(f => f.Path, f => f.Version, StringComparer.OrdinalIgnoreCase);
        return new LayoutFiles(map.ContainsKey, p => map.GetValueOrDefault(p));
    }

    /// <summary>
    /// The layout: the program is app\PoolWatch.exe in the install folder, and its launcher is that folder's PoolWatch.exe
    /// or PoolWatch-selfcontained.exe, of the same build as the program. A program anywhere else has no launcher.
    /// </summary>
    [Fact]
    public void TheLauncherIsTheInstallFoldersPoolWatchExeAndTheProgramIsBelowIt()
    {
        Assert.Equal(Install, InstallFolderOf(Program));
        Assert.Equal(Install, InstallFolderOf(@"C:\Program Files\PoolWatch\APP\poolwatch.exe"));
        Assert.Null(InstallFolderOf(Usual));          // not in an app folder: a build output, an older layout
        Assert.Null(InstallFolderOf(@"C:\src\bin\Release\PoolWatch.exe"));
        Assert.Equal([Usual, SelfContained], LaunchersOf(Program));
        Assert.Empty(LaunchersOf(Usual));
        // The first that is a launcher.
        Assert.Equal(Usual, LauncherOf(Program, Files((Program, ThisBuild), (Usual, ThisBuild), (SelfContained, ThisBuild))));
        Assert.Equal(SelfContained, LauncherOf(Program, Files((Program, ThisBuild), (SelfContained, ThisBuild))));
        Assert.Null(LauncherOf(Program, Files((Program, ThisBuild))));
        Assert.Equal(Usual, ExpectedLauncher(Program));
        // A launcher is there and has the program of its own version below it; an older PoolWatch.exe (the program itself) has not.
        var files = Files((Program, ThisBuild), (Usual, ThisBuild), (SelfContained, ThisBuild), (@"C:\Program Files\Old\PoolWatch.exe", Beta1));
        Assert.True(IsLauncher(Usual, files));
        Assert.True(IsLauncher(SelfContained, files));
        Assert.False(IsLauncher(Program, files));                 // the program itself
        Assert.Equal(LauncherState.NoProgramBelow, Judge(@"C:\Program Files\Old\PoolWatch.exe", files));
        Assert.Equal(LauncherState.NotThere, Judge(SelfContained, Files((Program, ThisBuild), (Usual, ThisBuild)))); // a setup deleted it
        Assert.False(IsLauncher(Install + @"\Other.exe", Files((Install + @"\Other.exe", ThisBuild), (Program, ThisBuild))));
        Assert.True(IsPoolWatchName(@"C:\x\poolwatch.EXE") && IsPoolWatchName(@"C:\x\PoolWatch-selfcontained.exe") && !IsPoolWatchName(@"C:\x\Tool.exe"));
    }

    /// <summary>
    /// A zip unpacked over a copy of the other zip made before 1.0 can leave that version's program under one launcher name
    /// and this build's launcher under the other, above this build's program. The older program is never taken for the
    /// launcher: not to hand a start to, not to register a logon task for, not to call a task that starts it fine. Nor is a
    /// file whose version cannot be read.
    /// </summary>
    [Fact]
    public void AnOlderProgramUnderALaunchersNameIsNeverTakenForTheLauncher()
    {
        // The self-contained zip over a framework-dependent copy: the older program is still PoolWatch.exe.
        var overFd = Files((Program, ThisBuild), (Usual, Beta1), (SelfContained, ThisBuild));
        Assert.Equal(SelfContained, LauncherOf(Program, overFd));
        Assert.Equal(LauncherState.OtherVersion, Judge(Usual, overFd));
        Assert.False(IsLauncher(Usual, overFd));
        // The framework-dependent zip over a self-contained copy: the older program is still PoolWatch-selfcontained.exe.
        var overSc = Files((Program, ThisBuild), (Usual, ThisBuild), (SelfContained, Beta1));
        Assert.Equal(Usual, LauncherOf(Program, overSc));
        Assert.Equal(LauncherState.OtherVersion, Judge(SelfContained, overSc));
        // Only older programs under both names: no launcher at all.
        Assert.Null(LauncherOf(Program, Files((Program, ThisBuild), (Usual, Beta1), (SelfContained, Beta1))));
        // A version that cannot be read is no version, also when neither can be read.
        Assert.Null(LauncherOf(Program, Files((Program, null), (Usual, null))));
        Assert.Null(LauncherOf(Program, Files((Program, ThisBuild), (Usual, null))));
        Assert.Null(LauncherOf(Program, Files((Program, ThisBuild), (Usual, ""))));
        // The versions are compared exactly: a build of another commit has another version.
        Assert.Null(LauncherOf(Program, Files((Program, ThisBuild), (Usual, "1.0.0+2222222"))));
        Assert.Null(LauncherOf(Program, Files((Program, ThisBuild), (Usual, "1.0.0+1111111 "))));
    }

    /// <summary>On disk, a file's product version is read from its version resource; one that cannot be read has none.</summary>
    [Fact]
    public void TheRealFilesProductVersionIsItsVersionResource()
    {
        string own = typeof(LaunchGate).Assembly.Location;
        Assert.Equal(System.Diagnostics.FileVersionInfo.GetVersionInfo(own).ProductVersion, ProductVersionOf(own));
        Assert.StartsWith("1.", ProductVersionOf(own));
        Assert.Null(ProductVersionOf(Path.Combine(Path.GetTempPath(), "pw-no-such-file-" + Guid.NewGuid().ToString("N") + ".exe")));
        Assert.Equal(((Func<string, string?>)ProductVersionOf).Method, LayoutFiles.OnDisk.VersionOf.Method);
        Assert.Equal(((Func<string, bool>)File.Exists).Method, LayoutFiles.OnDisk.Exists.Method);
    }

    [Fact]
    public void TheAppAndTheLauncherAgreeOnNamesPrefixesAndExitCodes()
    {
        Assert.Equal(EnvironmentBlock.KnobPrefixes, KnobPrefixes);
        Assert.Equal(EnvironmentBlock.Marker, Marker);
        Assert.Equal(EnvironmentBlock.StartedIn, StartedInVariable);
        Assert.False(IsKnob(StartedInVariable));
        Assert.Equal(LauncherProgram.AppFolder, AppFolderName);
        Assert.Equal(LauncherProgram.ProgramName, ProgramFileName);
        Assert.Equal(ProgramFileName, typeof(LaunchGate).Assembly.GetName().Name + ".exe");
        Assert.Equal(ProgramFileName, LauncherFileNames[0]);
        // The native launcher takes the same name (its project names its assembly PoolWatch when it is compiled ahead of time).
        string project = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch.Launcher", "PoolWatch.Launcher.csproj"));
        Assert.Contains("<AssemblyName Condition=\"'$(PublishAot)' == 'true'\">PoolWatch</AssemblyName>", project);
        Assert.True(IsKnob("dotnet_startup_hooks") && IsKnob("COMPLUS_x") && !IsKnob("POOLWATCH_LAUNCHER"));
    }

    /// <summary>
    /// Program.Main: the start is checked once the arguments are read and a usage error is reported (which needs no
    /// rights), before every mode; then the files named on the command line are made full paths against the folder it was
    /// started in, and its own folder becomes its current one, before any mode does work.
    /// </summary>
    [Fact]
    public void ProgramMainChecksTheStartBeforeItDoesAnyWork()
    {
        string main = MainWindowLayoutTests.Block(File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Program.cs")),
            "private static int Main(string[] args)");
        int gate = main.IndexOf("Engine.LaunchGate.Check(args, exempt: !Engine.InstallGuard.Applies(opt), wait: consoleMode) is { } handedOver) return handedOver;",
            StringComparison.Ordinal);
        Assert.True(gate > 0, "Program.Main does not check the start (LaunchGate.Check)");
        Assert.True(main.IndexOf("CommandLineOptions.Parse(args)", StringComparison.Ordinal) < gate);
        Assert.True(main.IndexOf("return 2;", StringComparison.Ordinal) < gate);
        int resolve = main.IndexOf("opt.ResolvePaths(Engine.LaunchGate.StartedIn(Environment.GetEnvironmentVariables(), Environment.CurrentDirectory));", StringComparison.Ordinal);
        int own = main.IndexOf("UseOwnFolder();", StringComparison.Ordinal);
        Assert.True(gate < resolve && resolve < own, "the paths are not resolved, then the folder changed, after the start check");
        foreach (var work in new[] { "InstallGuard.RefuseElevatedFromUserWritable", "SelfTest.Run", "MarkRequest(opt)", "CacheSetup(what, opt)", "RunHelperMode",
                                     "Services(opt)", "TasksAndStartup(", "ChangesDump(cd, opt)", "StopRunning(opt)", "UninstallCleanup(opt)",
                                     "RunAsTheOnlyMonitor(MonitorMutexName", "HeadlessRunner.Run", "UiRunner.Run", "TellingFor(" })
        {
            int at = main.IndexOf(work, StringComparison.Ordinal);
            Assert.True(at > own, $"{work} is not after the start check and the change of folder");
        }
    }

    [Fact]
    public void OnlyTheLauncherAsksForAdministratorRights()
    {
        string root = TrayIconsTests.RepoRoot();
        string Level(params string[] path) =>
            Regex.Match(File.ReadAllText(Path.Combine([root, .. path])), @"<requestedExecutionLevel level=""(\w+)""").Groups[1].Value;
        Assert.Equal("asInvoker", Level("src", "PoolWatch", "app.manifest"));
        Assert.Equal("requireAdministrator", Level("src", "PoolWatch.Launcher", "app.manifest"));
    }

    [Fact]
    public void TheRuntimeIgnoresStartUpHooksInPoolWatch()
    {
        // The build's own runtimeconfig (the single-file publish carries the same one inside the exe).
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PoolWatch.runtimeconfig.json")));
        var properties = config.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties");
        Assert.Equal(JsonValueKind.False, properties.GetProperty("System.StartupHookProvider.IsSupported").ValueKind);
    }

    [Fact]
    public void TheLaunchersNativeBuildLinksNoStartUpHookLoadableGcOrDiagnosticServer()
    {
        string project = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch.Launcher", "PoolWatch.Launcher.csproj"));
        Assert.Contains("<StartupHookSupport>false</StartupHookSupport>", project);
        Assert.Contains("<IlcStandaloneGCSupport>false</IlcStandaloneGCSupport>", project);
        Assert.Contains("<EventSourceSupport>false</EventSourceSupport>", project);
        Assert.Contains("<OutputType>WinExe</OutputType>", project);
    }

    /// <summary>The files named on the command line stay the ones meant once PoolWatch's current folder is its own.</summary>
    [Fact]
    public void FilesNamedOnTheCommandLineAreReadAgainstTheFolderPoolWatchWasStartedIn()
    {
        var o = Headless.CommandLineOptions.Parse(["--headless", "--out", "run", "--config", @"cfg\settings.json", "--tasks-dump", @"C:\abs\tasks.txt"]);
        o.ResolvePaths(@"D:\work");
        Assert.Equal((@"D:\work\run", @"D:\work\cfg\settings.json", @"C:\abs\tasks.txt"), (o.OutDir, o.ConfigFile, o.TasksDump));
        var dumps = Headless.CommandLineOptions.Parse(["--services-dump", "s.txt"]);
        dumps.ResolvePaths(@"D:\work");
        Assert.Equal(@"D:\work\s.txt", dumps.ServicesDump);
        var changes = Headless.CommandLineOptions.Parse(["--changes-dump", @"..\c.json"]);
        changes.ResolvePaths(@"D:\work\x");
        Assert.Equal(@"D:\work\c.json", changes.ChangesDump);
        var none = Headless.CommandLineOptions.Parse(["--tray"]);
        none.ResolvePaths(@"D:\work");
        Assert.Null(none.OutDir);
    }

    /// <summary>
    /// The folder those files are read against: started by the launcher, which starts PoolWatch in its own app folder, the
    /// folder the launcher was started in, as the launcher passes it on; otherwise (no marker, no such folder, not a full
    /// path) PoolWatch's own current folder.
    /// </summary>
    [Fact]
    public void TheFolderStartedInIsTheLaunchersWhenTheLauncherStartedPoolWatch()
    {
        const string current = @"C:\Program Files\PoolWatch\app";
        Hashtable Env(params (string Name, string Value)[] vars)
        {
            var env = new Hashtable(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in vars) env[name] = value;
            return env;
        }
        Assert.Equal(@"D:\work", StartedIn(Env((Marker, "1"), (StartedInVariable, @"D:\work")), current));
        Assert.Equal(@"\\server\share\work", StartedIn(Env((Marker, "1"), (StartedInVariable, @"\\server\share\work")), current));
        Assert.Equal(current, StartedIn(Env((StartedInVariable, @"D:\work")), current));                 // not started by the launcher
        Assert.Equal(current, StartedIn(Env((Marker, "1")), current));                                     // the launcher could not read its folder
        Assert.Equal(current, StartedIn(Env((Marker, "1"), (StartedInVariable, @"work")), current));      // not a full path
        Assert.Equal(current, StartedIn(Env((Marker, "1"), (StartedInVariable, @"C:work")), current));
        // What the launcher puts in its block: the folder it was started in, as that variable, only when it is a full path.
        var block = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        EnvironmentBlock.AddStartedIn(block, @"D:\work");
        Assert.Equal(@"D:\work", block[StartedInVariable]);
        foreach (var folder in new[] { null, "", "work", @"C:work" })
        {
            var none = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            EnvironmentBlock.AddStartedIn(none, folder);
            Assert.Empty(none);
        }
    }
}
