using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Setup;
using PoolWatch.Engine;
using static PoolWatch.Engine.LogonTaskPolicy;

namespace PoolWatch.AppTests;

/// <summary>
/// The logon task starts PoolWatch with highest privileges and no UAC prompt, so it may only start a
/// program in an admin-only install folder, and it starts PoolWatch's launcher there (the install folder's PoolWatch.exe),
/// which gives PoolWatch a clean environment; never PoolWatch's program itself (app\PoolWatch.exe) or an older PoolWatch
/// that has no launcher; and only as PoolWatch sets it up: one action, this PoolWatch's launcher by its literal path,
/// <c>--tray</c>, as you, signed in interactively, with highest privileges. Pure decisions plus the check with a fake Task
/// Scheduler: nothing is registered.
/// </summary>
public class LogonTaskTests
{
    private static readonly string[] Roots = [@"C:\Program Files", @"C:\Program Files (x86)"];
    private const string Install = @"C:\Program Files\PoolWatch";
    /// <summary>The running program, and its launcher (what a task made before 1.0 named too: the program had that place then).</summary>
    private const string Installed = Install + @"\app\PoolWatch.exe", Launcher = Install + @"\PoolWatch.exe";
    private const string Desktop = @"C:\Users\alice\OneDrive\Desktop\PoolWatch.exe";
    private const string You = @"PC\alice", Other = @"PC\bob";

    /// <summary>The account this PoolWatch runs as: by name, as Task Scheduler may give it.</summary>
    private static bool Me(string? userId) => userId == You;

    /// <summary>
    /// A task with the principal PoolWatch registers (this account, signed in interactively) wherever the test set none: the
    /// tests of a task's program, its triggers and its state judge those alone.
    /// </summary>
    private static ExistingTask? Mine(ExistingTask? task) =>
        task is null ? null : task with { UserId = task.UserId ?? You, LogonType = task.LogonType ?? InteractiveToken };

    /// <summary>A task as PoolWatch registers it, with one part changed by the caller (account and sign-in kind as given).</summary>
    private static ExistingTask Own(string command = Launcher, string? arguments = TaskArguments, string? workingDirectory = null,
        string? user = You, int? logonType = InteractiveToken, bool highest = true) =>
        new([new TaskAction(command, arguments, WorkingDirectory: workingDirectory)], highest, UserId: user, LogonType: logonType);

    /// <summary>A task judged as it is, its account and sign-in kind included (<see cref="Own"/>'s tests).</summary>
    private static Verdict JudgeAsIs(ExistingTask? task, string? current = Installed, bool trusted = true) =>
        LogonTaskPolicy.Judge(task, current, trusted, Roots, Me, Files);

    private const string ThisBuild = "1.0.0+1111111", Beta1 = "1.0.0-beta.1+9ffe77e";

    /// <summary>
    /// The files there, each with its product version: this install's layout (with the launcher under the self-contained
    /// zip's name too), another install in Program Files (x86), older PoolWatch programs with no launcher, and two folders
    /// where a zip was unpacked over a copy of the other zip made before 1.0 (an older program left under one launcher
    /// name, this build's launcher under the other).
    /// </summary>
    private static readonly Dictionary<string, string> Versions = new(StringComparer.OrdinalIgnoreCase)
    {
        [Installed] = ThisBuild, [Launcher] = ThisBuild, [Install + @"\PoolWatch-selfcontained.exe"] = ThisBuild,
        [@"C:\Program Files (x86)\PoolWatch\PoolWatch.exe"] = ThisBuild, [@"C:\Program Files (x86)\PoolWatch\app\PoolWatch.exe"] = ThisBuild,
        [@"C:\Program Files\Old PoolWatch\PoolWatch.exe"] = Beta1, [@"C:\Program Files\Old\PoolWatch-selfcontained.exe"] = Beta1,
        [@"C:\Program Files\OverFd\PoolWatch.exe"] = Beta1, [@"C:\Program Files\OverFd\PoolWatch-selfcontained.exe"] = ThisBuild, [@"C:\Program Files\OverFd\app\PoolWatch.exe"] = ThisBuild,
        [@"C:\Program Files\OverSc\PoolWatch.exe"] = ThisBuild, [@"C:\Program Files\OverSc\PoolWatch-selfcontained.exe"] = Beta1, [@"C:\Program Files\OverSc\app\PoolWatch.exe"] = ThisBuild,
    };

    private static readonly LayoutFiles Files = new(Versions.ContainsKey, p => Versions.GetValueOrDefault(p));

    private static Verdict Judge(ExistingTask? task, string? current = Installed, bool trusted = true) =>
        LogonTaskPolicy.Judge(Mine(task), current, trusted, Roots, Me, Files);

    /// <summary>
    /// The judgement Settings and the setup guide go by (LogonTask.AtSignIn): no running PoolWatch to compare with, and the
    /// run level left to <see cref="AtSignIn"/>, which judges it with this PC's need for it.
    /// </summary>
    private static Judgement ForSettings(ExistingTask? task) =>
        LogonTaskPolicy.Judge(Mine(task), null, false, Roots, Me, Files, judgeRunLevel: false).Judgement;

    [Fact]
    public void OnlyAnInstalledExeIsEverRegistered()
    {
        Assert.Null(WhyNotRegister(Installed, allowAnyLocation: false, uiSmoke: false, Roots));
        Assert.Contains("you can write to", WhyNotRegister(Desktop, false, false, Roots));
        Assert.Contains("--allow-any-location", WhyNotRegister(Installed, allowAnyLocation: true, uiSmoke: false, Roots));
        Assert.Contains("UI test", WhyNotRegister(Installed, false, uiSmoke: true, Roots));
        Assert.NotNull(WhyNotRegister(null, false, false, Roots));
        Assert.NotNull(WhyNotRegister(@"C:\Windows\Temp\PoolWatch.exe", false, false, Roots));
    }

    [Fact]
    public void TheTaskStartsTheLauncherNeverTheProgramItself()
    {
        string xml = Ui.LogonTask.TaskXml(Launcher, @"PC1\alice");
        Assert.Contains($"<Command>{Launcher}</Command><Arguments>--tray</Arguments>", xml);
        Assert.DoesNotContain(Installed, xml);
        Assert.Contains("<LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel>", xml);
        // What it registers is what the check then reads as fine.
        Assert.Equal(Judgement.Fine, Judge(new ExistingTask(Launcher, "--tray", Highest: true)).Judgement);
        // Settings' tick registers the launcher of the running program, found where the layout puts it.
        string set = MainWindowLayoutTests.Block(File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Ui", "TrayIcons.cs")),
            "public static (bool ok, string output) Set(bool enabled, string xmlPath, Func<string, string> checkWrite, bool on = true, bool signInTrigger = true)");
        Assert.Contains("if (Engine.LaunchGate.LauncherOf(exe, Engine.LayoutFiles.OnDisk) is not { } launcher)", set);
        Assert.Contains("TaskXml(launcher, System.Security.Principal.WindowsIdentity.GetCurrent().Name, on, signInTrigger)", set);
    }

    /// <summary>
    /// A task made by 1.0.0-beta.1 starts the install folder's PoolWatch.exe, which is now the launcher: it needs no change
    /// and is fine. Another install's launcher is a launcher too (a PoolWatch that is not installed, a development run,
    /// judges it so), but not this PoolWatch's: the installed PoolWatch asks about it. The same name in an older install
    /// elsewhere (no app folder below it) is PoolWatch's program itself.
    /// </summary>
    [Fact]
    public void ATaskMadeBeforeTheLauncherStartsTheLauncherNow()
    {
        Assert.Equal(Judgement.Fine, Judge(new ExistingTask(Launcher, "--tray", true)).Judgement);
        var other = new ExistingTask(@"""C:\Program Files (x86)\PoolWatch\PoolWatch.exe""", "--tray", true);
        Assert.Equal(Judgement.Fine, Judge(other, Desktop, trusted: false).Judgement);
        var notThis = Judge(other);
        Assert.Equal(Judgement.Untrusted, notThis.Judgement);
        Assert.Contains(@"it starts C:\Program Files (x86)\PoolWatch\PoolWatch.exe rather than this PoolWatch's launcher (" + Launcher + ")", notThis.Message);
        var older = Judge(new ExistingTask(@"C:\Program Files\Old PoolWatch\PoolWatch.exe", "--tray", true));
        Assert.Equal(Judgement.Outdated, older.Judgement);
        Assert.Contains("not PoolWatch's launcher", older.Message);
    }

    /// <summary>
    /// A folder where one zip was unpacked over a copy of the other made before 1.0: the older program left under one of
    /// the launcher's names is not the launcher, so a task that starts it is asked about, and what is said names the
    /// PoolWatch below it; the launcher under the other name is fine.
    /// </summary>
    [Fact]
    public void ATaskThatStartsAnOlderProgramLeftUnderALaunchersNameIsAskedAbout()
    {
        foreach (var (older, launcher) in new[] { (@"C:\Program Files\OverFd\PoolWatch.exe", @"C:\Program Files\OverFd\PoolWatch-selfcontained.exe"),
                                                  (@"C:\Program Files\OverSc\PoolWatch-selfcontained.exe", @"C:\Program Files\OverSc\PoolWatch.exe") })
        {
            var v = Judge(new ExistingTask(older, "--tray", true));
            Assert.Equal((Judgement.Outdated, older), (v.Judgement, v.Program));
            Assert.Contains("not of the same version as the PoolWatch below it (" + Path.Combine(Path.GetDirectoryName(older)!, "app", "PoolWatch.exe") + ")", v.Message);
            // Which of the two is older is not known here, so what follows from it is said only for that case.
            Assert.EndsWith("). If it is an older PoolWatch without a launcher, left there when a newer copy was unpacked over it, at sign-in " +
                            "PoolWatch would get your account's environment as it is, not the clean one its launcher gives it.", v.Message);
            Assert.DoesNotContain("may be", v.Message);
            Assert.True(v.CanRePoint);
            // The launcher beside it is fine for the PoolWatch installed there.
            Assert.Equal(Judgement.Fine, Judge(new ExistingTask(launcher, "--tray", true), Path.Combine(Path.GetDirectoryName(launcher)!, "app", "PoolWatch.exe")).Judgement);
        }
        // Where the program below it is the one that is not a launcher, it is said as certain.
        Assert.EndsWith("or an older PoolWatch without a launcher), so at sign-in PoolWatch would get your account's environment as it is, " +
                        "not the clean one its launcher gives it.", LogonTaskPolicy.OutdatedMessage(Installed, LaunchGate.LauncherState.NoProgramBelow, highest: true));
    }

    /// <summary>
    /// A task whose PoolWatch program is not there (a self-contained zip's launcher that an older setup deleted, say) starts
    /// nothing at sign-in: it is asked about, and what is said is that the file is not there, never that it is some other
    /// PoolWatch.
    /// </summary>
    [Fact]
    public void ATaskWhoseProgramIsNotThereSaysSo()
    {
        const string gone = Install + @"\PoolWatch-selfcontained.exe";
        var files = new LayoutFiles(p => !p.Equals(gone, StringComparison.OrdinalIgnoreCase) && Versions.ContainsKey(p), p => Versions.GetValueOrDefault(p));
        var v = LogonTaskPolicy.Judge(Mine(new ExistingTask(gone, "--tray", true)), Installed, true, Roots, Me, files);
        Assert.Equal((Judgement.Outdated, gone), (v.Judgement, v.Program));
        Assert.Equal($"Your PoolWatch logon task starts {gone} with highest privileges, which is not there, so PoolWatch does not start when you sign in.", v.Message);
        Assert.DoesNotContain("program itself", v.Message);
        Assert.True(v.CanRePoint);
        // Each state says its own thing.
        Assert.Contains("PoolWatch's program itself, or an older PoolWatch without a launcher",
            LogonTaskPolicy.OutdatedMessage(Installed, LaunchGate.LauncherState.NoProgramBelow, highest: false));
        Assert.StartsWith($"Your PoolWatch logon task starts {Installed}, which", LogonTaskPolicy.OutdatedMessage(Installed, LaunchGate.LauncherState.NoProgramBelow, highest: false));
    }

    [Fact]
    public void ADesktopLogonTaskIsUntrustedAndCanBeRepointed()
    {
        var v = Judge(new ExistingTask(Desktop, "--tray", Highest: true));
        Assert.Equal(Judgement.Untrusted, v.Judgement);
        Assert.Equal(Desktop, v.Program);
        Assert.Contains(Desktop, v.Message);
        Assert.Contains("highest privileges", v.Message);
        Assert.True(v.CanRePoint);
        // A PoolWatch that is not itself installed may not take its place.
        Assert.False(Judge(new ExistingTask(Desktop, "--tray", true), Desktop, trusted: false).CanRePoint);
    }

    [Theory]
    [InlineData(@"""C:\Program Files\PoolWatch\PoolWatch.exe""", "Fine")]                       // quoted: this install's launcher
    [InlineData(@"C:\Program Files (x86)\PoolWatch\PoolWatch.exe", "Fine")]                     // another install's launcher
    [InlineData(@"C:\Program Files\PoolWatch\app\PoolWatch.exe", "Outdated")]                   // the program itself, not its launcher
    [InlineData(@"C:\Program Files\PoolWatch\APP\poolwatch.EXE", "Outdated")]
    [InlineData(@"C:\Program Files\PoolWatch\PoolWatch-selfcontained.exe", "Fine")]             // the self-contained zip's name for the launcher
    [InlineData(@"C:\Program Files\Old\PoolWatch-selfcontained.exe", "Outdated")]               // the old self-contained program
    [InlineData(@"C:\Program Files\OverFd\PoolWatch.exe", "Outdated")]                          // an older program left under the launcher's name
    [InlineData(@"C:\Program Files\OverFd\PoolWatch-selfcontained.exe", "Fine")]                // the launcher beside it
    [InlineData(@"C:\Program Files\OverSc\PoolWatch-selfcontained.exe", "Outdated")]
    [InlineData(@"C:\Program Files\OverSc\PoolWatch.exe", "Fine")]
    [InlineData(@"C:\Program Files\Gone\PoolWatch-selfcontained.exe", "Outdated")]              // not there: it starts nothing
    [InlineData(@"C:\Program Files (x86)\Tool\tool.exe", "Fine")]                               // not PoolWatch's: judged by its place only
    [InlineData(@"%ProgramFiles%\PoolWatch\PoolWatch.exe", "Untrusted")]                        // named through a variable the account can change
    [InlineData(@"%USERPROFILE%\Desktop\PoolWatch.exe", "Untrusted")]
    [InlineData(@"C:\Program Files\%X%\PoolWatch.exe", "Untrusted")]
    [InlineData(@"C:\Windows\Temp\PoolWatch.exe", "Untrusted")]                                 // %WINDIR% is not admin-only
    [InlineData(@"C:\Program Files\..\Users\x\PoolWatch.exe", "Untrusted")]
    [InlineData(@"PoolWatch.exe", "Untrusted")]                                                 // relative: anywhere
    [InlineData("", "Untrusted")]
    public void TheTasksProgramIsJudgedByWhereItReallyIsAndWhatItStarts(string command, string judgement) =>
        // Judged by a PoolWatch that is not installed (a development run), which does not ask which installed copy the
        // task starts (TheTasksProgramIsJudgedByWhereItReallyIs: the installed one asks about any but its own launcher).
        Assert.Equal(Enum.Parse<Judgement>(judgement), Judge(new ExistingTask(command, "--tray", true), Desktop, trusted: false).Judgement);

    [Theory]
    [InlineData(@"""C:\Program Files\PoolWatch\PoolWatch.exe""", true)]           // quoted: this PoolWatch's launcher
    [InlineData(@"C:\Program Files\PoolWatch\PoolWatch-selfcontained.exe", true)] // its second name in this install folder
    [InlineData(@"C:\Program Files (x86)\PoolWatch\PoolWatch.exe", false)]      // admin-only, but not this PoolWatch
    [InlineData(@"C:\Program Files (x86)\Tool\tool.exe", false)]
    [InlineData(@"%ProgramFiles%\PoolWatch\PoolWatch.exe", false)]              // your own environment decides where that is
    [InlineData(@"%USERPROFILE%\Desktop\PoolWatch.exe", false)]
    [InlineData(@"C:\Windows\Temp\PoolWatch.exe", false)]                       // %WINDIR% is not admin-only
    [InlineData(@"C:\Program Files\..\Users\x\PoolWatch.exe", false)]
    [InlineData(@"PoolWatch.exe", false)]                                       // relative: anywhere
    [InlineData("", false)]
    public void TheTasksProgramIsJudgedByWhereItReallyIs(string command, bool fine) =>
        Assert.Equal(fine ? Judgement.Fine : Judgement.Untrusted, JudgeAsIs(Own(command)).Judgement);

    /// <summary>
    /// Task Scheduler fills in a %name% in the task's program from the account's own environment when it starts it,
    /// and a program running as the user can change that environment (HKCU\Environment) at any time, so such a task is
    /// untrusted whatever the name holds today.
    /// </summary>
    [Fact]
    public void AProgramNamedThroughAVariableIsUntrustedWhateverItHoldsToday()
    {
        var v = Judge(new ExistingTask(@"%PoolWatchHome%\PoolWatch.exe", "--tray", true));
        Assert.Equal(Judgement.Untrusted, v.Judgement);
        Assert.Contains("named through an environment variable", v.Message);
        Assert.True(v.CanRePoint);
        // Nothing is filled in: the judgement takes no expander at all.
        Assert.DoesNotContain(typeof(Func<string, string>), typeof(LogonTaskPolicy).GetMethod(nameof(LogonTaskPolicy.Judge))!.GetParameters().Select(p => p.ParameterType));
    }

    [Fact]
    public void AnOutdatedTaskCanBePointedAtTheLauncherOnlyByTheInstalledPoolWatch()
    {
        var v = Judge(new ExistingTask(Installed, "--tray", Highest: true));
        Assert.Equal(Judgement.Outdated, v.Judgement);
        Assert.Equal(Installed, v.Program);
        Assert.Contains("environment", v.Message);
        Assert.True(v.CanRePoint);
        Assert.False(Judge(new ExistingTask(Installed, "--tray", true), trusted: false).CanRePoint);
        Assert.False(Judge(new ExistingTask(Installed, "--tray", true), current: null).CanRePoint);
        // Any action that starts the program itself, not only the first.
        var second = new ExistingTask([new TaskAction(Launcher, "--tray"), new TaskAction(Installed, "--tray")], Highest: true);
        Assert.Equal(Judgement.Outdated, Judge(second).Judgement);
        // A location that is not safe is still judged first: untrusted, with its question.
        var both = new ExistingTask([new TaskAction(Installed, "--tray"), new TaskAction(Desktop, "--tray")], Highest: true);
        Assert.Equal(Judgement.Untrusted, Judge(both).Judgement);
    }

    [Fact]
    public void NoTaskNothingToDo() => Assert.Equal(Judgement.NoTask, Judge(null).Judgement);

    [Fact]
    public void EveryActionIsJudgedNotOnlyTheFirst()
    {
        // Task Scheduler runs every action with the task's privileges. A safe first action used to hide an unsafe second one.
        var twoActions = new ExistingTask([new TaskAction(Launcher, "--tray"), new TaskAction(Desktop, "--tray")], Highest: true);
        var v = Judge(twoActions);
        Assert.Equal(Judgement.Untrusted, v.Judgement);
        Assert.Equal(Desktop, v.Program);
        Assert.True(v.CanRePoint); // re-pointing replaces the whole task with the one installed launcher

        // An action that is not a program (a COM handler can load code the user registered) cannot be checked: untrusted.
        var com = new ExistingTask([new TaskAction(Launcher, "--tray"), new TaskAction("a task action of type 5 (not a program)", null, IsExec: false)], true);
        var c = Judge(com);
        Assert.Equal(Judgement.Untrusted, c.Judgement);
        Assert.Contains("cannot check", c.Message);

        // No action at all: nothing safe to point at, untrusted (as before).
        Assert.Equal(Judgement.Untrusted, Judge(new ExistingTask([], true)).Judgement);

        // Every action in Program Files, but more than PoolWatch's one: not PoolWatch's task.
        var bothInProgramFiles = new ExistingTask([new TaskAction(Launcher, "--tray"), new TaskAction(@"C:\Program Files (x86)\Tool\tool.exe", null)], true);
        var both = Judge(bothInProgramFiles);
        Assert.Equal(Judgement.Untrusted, both.Judgement);
        Assert.Contains("it has 2 actions", both.Message);
    }

    [Theory]
    [InlineData("--other-mode", null, You, InteractiveToken, true, "with \"--other-mode\" rather than --tray")]
    [InlineData("--tray --out C:\\Users\\alice\\x", null, You, InteractiveToken, true, "rather than --tray")]
    [InlineData("", null, You, InteractiveToken, true, "rather than --tray")]
    [InlineData("--tray", @"C:\Users\alice\Downloads", You, InteractiveToken, true, @"it starts PoolWatch in C:\Users\alice\Downloads")]
    [InlineData("--tray", null, Other, InteractiveToken, true, @"it runs as PC\bob, not as you")]
    [InlineData("--tray", null, null, InteractiveToken, true, "an account PoolWatch could not read")]
    [InlineData("--tray", null, You, 1 /* a stored password */, true, "does not run in your session")]
    [InlineData("--tray", null, You, null, true, "does not run in your session")]
    [InlineData("--tray", null, You, InteractiveToken, false, "does not run with highest privileges")]
    public void TheWholeTaskMustBeTheOnePoolWatchMakes(string arguments, string? workingDirectory, string? user, int? logonType, bool highest, string what)
    {
        var v = JudgeAsIs(Own(arguments: arguments, workingDirectory: workingDirectory, user: user, logonType: logonType, highest: highest));
        Assert.Equal(Judgement.Untrusted, v.Judgement);
        Assert.StartsWith("Your PoolWatch logon task is not the one PoolWatch makes:", v.Message);
        Assert.Contains(what, v.Message);
        Assert.True(v.CanRePoint); // PoolWatch's own task, for this PoolWatch's launcher, takes its place on a yes
        Assert.Equal(Judgement.Fine, JudgeAsIs(Own(arguments: " --tray ")).Judgement);
        // A run level that could not be read is not taken for highest either.
        Assert.Contains("could not read whether it runs with highest privileges", JudgeAsIs(Own() with { Highest = null }).Message);
    }

    [Fact]
    public void AProgramNamedThroughAnEnvironmentVariableIsNotPoolWatchsTaskWhoeverJudgesIt()
    {
        // A development run (not the installed PoolWatch) skips the this-launcher comparison: the variable itself must be refused.
        var v = JudgeAsIs(Own(@"%ProgramFiles%\PoolWatch\PoolWatch.exe"), Desktop, trusted: false);
        Assert.Equal(Judgement.Untrusted, v.Judgement);
        Assert.Contains("through an environment variable", v.Message);
        Assert.Equal(Judgement.Fine, JudgeAsIs(Own(), Desktop, trusted: false).Judgement);
    }

    [Fact]
    public void TheTasksAccountIsYoursByNameOrBySid()
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        static bool NoLookup(string a, string b) => throw new InvalidOperationException("the domain cannot be reached");
        // The name PoolWatch registers the task with matches as text, even when no lookup can be made (a domain account offline).
        Assert.True(IsAccount(@"CONTOSO\alice", @"CONTOSO\alice", sid, NoLookup));
        Assert.True(IsAccount(@" contoso\ALICE ", @"CONTOSO\alice", sid, NoLookup));
        Assert.True(IsAccount(sid, @"CONTOSO\alice", sid, NoLookup));
        // Otherwise by SID once looked up; a lookup that fails is not the same account.
        Assert.True(IsAccount("alice@contoso.com", @"CONTOSO\alice", sid, (a, b) => a == "alice@contoso.com" && b == sid));
        Assert.False(IsAccount(@"CONTOSO\bob", @"CONTOSO\alice", sid, NoLookup));
        Assert.False(IsAccount(@"CONTOSO\bob", @"CONTOSO\alice", sid, (_, _) => false));
        Assert.False(IsAccount(null, @"CONTOSO\alice", sid, (_, _) => true));
        Assert.False(IsAccount("  ", @"CONTOSO\alice", sid, (_, _) => true));
        Assert.False(IsAccount(@"CONTOSO\bob", null, null, (_, _) => true));
    }

    [Fact]
    public void APoolWatchThatIsNotInstalledStillJudgesTheTaskButNotWhichInstalledCopyItStarts()
    {
        // A development run (--allow-any-location) is not the installed PoolWatch: the task may start an installed one.
        Assert.Equal(Judgement.Fine, JudgeAsIs(Own(@"C:\Program Files (x86)\PoolWatch\PoolWatch.exe"), Desktop, trusted: false).Judgement);
        var v = JudgeAsIs(Own(arguments: "--headless"), Desktop, trusted: false);
        Assert.Equal(Judgement.Untrusted, v.Judgement);
        Assert.False(v.CanRePoint);
    }

    /// <summary>
    /// By default the judgement looks at the real files: a PoolWatch.exe is the launcher only with app\PoolWatch.exe below
    /// it and the same product version in both files' version resources (here copies of PoolWatch's own assembly, and of
    /// Windows' cmd.exe for a program of another version).
    /// </summary>
    [Fact]
    public void TheRealFilesDecideWhatIsALauncher()
    {
        string dir = Directory.CreateTempSubdirectory("pw-layout-").FullName;
        try
        {
            string exe = Path.Combine(dir, "PoolWatch.exe"), program = Path.Combine(dir, "app", "PoolWatch.exe");
            string build = typeof(LaunchGate).Assembly.Location;
            Verdict Real() => LogonTaskPolicy.Judge(Mine(new ExistingTask(exe, "--tray", true)), program, true, [dir], Me);
            Assert.Contains("which is not there", Real().Message);
            File.Copy(build, exe);
            Assert.Equal(Judgement.Outdated, Real().Judgement);                       // no program below it
            Directory.CreateDirectory(Path.GetDirectoryName(program)!);
            File.Copy(build, program);
            Assert.Equal(Judgement.Fine, Real().Judgement);                           // the same build
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), exe, overwrite: true);
            Assert.Equal(Judgement.Outdated, Real().Judgement);                       // another version
            Assert.Contains("not of the same version", Real().Message);
            File.WriteAllText(exe, "");
            File.WriteAllText(program, "");
            Assert.Equal(Judgement.Outdated, Real().Judgement);                       // no version resource in either
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ------------------------------------------------------------------ reading the task

    /// <summary>
    /// Only "no such task" is no task. Task Scheduler's other errors (access denied, a damaged task) reach the check,
    /// which says so and asks, instead of reading as no task at all.
    /// </summary>
    [Fact]
    public void OnlyNoSuchTaskIsReadAsNoTask()
    {
        Assert.True(Ui.LogonTask.IsNoSuchTask(new FileNotFoundException("x")));
        Assert.True(Ui.LogonTask.IsNoSuchTask(new DirectoryNotFoundException("x")));
        Assert.True(Ui.LogonTask.IsNoSuchTask(new COMException("x", unchecked((int)0x80070002))));
        Assert.True(Ui.LogonTask.IsNoSuchTask(new COMException("x", unchecked((int)0x80070003))));
        Assert.True(Ui.LogonTask.IsNoSuchTask(new System.Reflection.TargetInvocationException(new FileNotFoundException("x"))));
        Assert.False(Ui.LogonTask.IsNoSuchTask(new UnauthorizedAccessException("x")));
        Assert.False(Ui.LogonTask.IsNoSuchTask(new COMException("x", unchecked((int)0x80070005))));
        Assert.False(Ui.LogonTask.IsNoSuchTask(new COMException("x", unchecked((int)0x8004130F)))); // SCHED_E_ACCOUNT_INFORMATION_NOT_SET
        Assert.False(Ui.LogonTask.IsNoSuchTask(new InvalidOperationException("x")));
        // And Read uses it, with no catch-all left.
        string read = MainWindowLayoutTests.Block(File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Ui", "TrayIcons.cs")),
            "public static Engine.LogonTaskPolicy.ExistingTask? Read()");
        Assert.Contains("try { task = com.Track(root.GetTask(Name)); }\n        catch (Exception ex) when (IsNoSuchTask(ex)) { return null; }", read.Replace("\r\n", "\n"));
        Assert.DoesNotContain("catch { return null; }", read);
    }

    // ------------------------------------------------------------------ the check, with a fake Task Scheduler

    private sealed class FakeTasks(ExistingTask? task) : ILogonTasks
    {
        public readonly List<string> Calls = new();
        public string? RegisterError, RemoveError;
        public Exception? ReadError;
        public ExistingTask? Current = task;
        public ExistingTask? Read() => ReadError is { } e ? throw e : Current;
        public string? RegisterCurrent(bool on = true, bool signInTrigger = true)
        {
            Calls.Add("register" + (on ? "" : " off") + (signInTrigger ? "" : " trigger off"));
            return RegisterError;
        }
        public string? Remove()
        {
            Calls.Add("remove");
            if (RemoveError is null) Current = null;
            return RemoveError;
        }
        public bool IsThisAccount(string? userId) => Me(userId);
    }

    private static (List<string> calls, List<Finding> findings, int asked, Verdict verdict) Check(ExistingTask? task, LogonTaskCheck.Answer answer,
        bool currentTrusted = true, string? registerError = null, string? removeError = null, Exception? readError = null)
    {
        var tasks = new FakeTasks(Mine(task)) { RegisterError = registerError, RemoveError = removeError, ReadError = readError };
        var findings = new List<Finding>();
        int asked = 0;
        var v = new LogonTaskCheck(tasks, _ => { asked++; return answer; }, _ => { }, findings.Add, Files).Run(Installed, currentTrusted, Roots);
        return (tasks.Calls, findings, asked, v);
    }

    [Fact]
    public void TheUserChoosesRepointRemoveOrLeave()
    {
        var desktop = new ExistingTask(Desktop, "--tray", true);
        var repointed = Check(desktop, LogonTaskCheck.Answer.RePoint);
        Assert.Equal(["register"], repointed.calls);
        Assert.Contains("replaced it with PoolWatch's own task, which starts " + Launcher, Assert.Single(repointed.findings).Title);

        var removed = Check(desktop, LogonTaskCheck.Answer.Remove);
        Assert.Equal(["remove"], removed.calls);

        var left = Check(desktop, LogonTaskCheck.Answer.Leave);
        Assert.Empty(left.calls);
        var f = Assert.Single(left.findings);
        Assert.Equal(FindingKind.Setup, f.Kind);
        Assert.Equal(FindingSeverity.Warning, f.Severity);
        Assert.Contains("asks again", f.Title);

        // A task in Program Files that another account runs, or with other arguments, is asked about the same way.
        Assert.Equal(["register"], Check(Own(user: Other), LogonTaskCheck.Answer.RePoint).calls);
        Assert.Equal(1, Check(Own(arguments: "--other-mode"), LogonTaskCheck.Answer.Leave).asked);
        // The check reads the task's account through Task Scheduler's own answer, as the real reader gives it.
        Assert.Equal(0, Check(Own(), LogonTaskCheck.Answer.Remove).asked);
    }

    /// <summary>A task that starts PoolWatch's program itself is asked about as an untrusted one is; nothing changes without an answer.</summary>
    [Fact]
    public void AnOutdatedTaskIsAskedAboutAndChangedOnlyOnAnAnswer()
    {
        var outdated = new ExistingTask(Installed, "--tray", true);
        var left = Check(outdated, LogonTaskCheck.Answer.Leave);
        Assert.Equal((1, 0), (left.asked, left.calls.Count));
        Assert.Contains("not PoolWatch's launcher", Assert.Single(left.findings).Title);
        Assert.Equal(["register"], Check(outdated, LogonTaskCheck.Answer.RePoint).calls);
        Assert.Equal(["remove"], Check(outdated, LogonTaskCheck.Answer.Remove).calls);
        // Headless passes "not trusted" and always answers Leave: said, nothing changed.
        var headless = Check(outdated, LogonTaskCheck.Answer.RePoint, currentTrusted: false);
        Assert.Empty(headless.calls);
        Assert.Contains("left as it is", Assert.Single(headless.findings).Title);
    }

    /// <summary>
    /// A task that is there but could not be read is never taken for no task: it is said, asked about, and left as it is
    /// unless the owner answers otherwise (a read that fails once removes nothing by itself).
    /// </summary>
    [Fact]
    public void ATaskThatCannotBeReadIsSaidAndAskedAboutNeverRemovedBehindTheOwnersBack()
    {
        var left = Check(null, LogonTaskCheck.Answer.Leave, readError: new UnauthorizedAccessException("Access is denied."));
        Assert.Equal(Judgement.Untrusted, left.verdict.Judgement);
        Assert.Equal((1, 0), (left.asked, left.calls.Count));
        Assert.Contains("could not be read (Access is denied.)", Assert.Single(left.findings).Title);
        Assert.Equal(["remove"], Check(null, LogonTaskCheck.Answer.Remove, readError: new IOException("x")).calls);
        Assert.Equal(["register"], Check(null, LogonTaskCheck.Answer.RePoint, readError: new IOException("x")).calls);
        Assert.Empty(Check(null, LogonTaskCheck.Answer.RePoint, currentTrusted: false, readError: new IOException("x")).calls); // only an installed copy re-points
    }

    [Fact]
    public void NothingIsAskedOrChangedForASafeTaskAndAnUninstalledPoolWatchNeverRepoints()
    {
        foreach (var safe in new ExistingTask?[] { null, new(Launcher, "--tray", true) })
        {
            var r = Check(safe, LogonTaskCheck.Answer.Remove);
            Assert.Equal((0, 0, 0), (r.asked, r.calls.Count, r.findings.Count));
        }
        var untrusted = Check(new ExistingTask(Desktop, "--tray", true), LogonTaskCheck.Answer.RePoint, currentTrusted: false);
        Assert.Empty(untrusted.calls);
    }

    /// <summary>Re-pointing when Task Scheduler refuses, or the data folder cannot be used: said, as the owner's answer's outcome.</summary>
    [Fact]
    public void ARePointThatFailsIsSaid()
    {
        var r = Check(new ExistingTask(Desktop, "--tray", true), LogonTaskCheck.Answer.RePoint, registerError: "access denied");
        Assert.Contains("could not be replaced with PoolWatch's own task, which starts " + Launcher + ": access denied", Assert.Single(r.findings).Title);
        // The real one returns any failure as its reason, so the owner's answer is always reported.
        string register = MainWindowLayoutTests.Block(File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Ui", "TrayIcons.cs")),
            "public string? RegisterCurrent(bool on = true, bool signInTrigger = true)");
        Assert.Contains("catch (Exception ex) { return ex.Message; }", register);
    }

    /// <summary>
    /// A re-point keeps the task and its sign-in trigger on or off as the owner had them: a task they turned off in Task
    /// Scheduler, or whose sign-in trigger they turned off, is registered so; a task with no sign-in trigger gets
    /// PoolWatch's, turned off, as its only trigger; one whose state could not be read is registered on. The question says
    /// which stays off and which is turned on, and so does the outcome. What PoolWatch registers carries both states, and
    /// what it reads is the task's own.
    /// </summary>
    [Fact]
    public void ARePointKeepsTheTaskOnOrOffAsItWas()
    {
        var off = Check(new ExistingTask(Desktop, "--tray", true, Enabled: false), LogonTaskCheck.Answer.RePoint);
        Assert.Equal(["register off"], off.calls);
        Assert.False(off.verdict.On);
        Assert.EndsWith("You replaced it with PoolWatch's own task, which starts " + Launcher + "; it stays off, as it was.", Assert.Single(off.findings).Title);
        var outdatedOff = Check(new ExistingTask(Installed, "--tray", true, Enabled: false), LogonTaskCheck.Answer.RePoint);
        Assert.Equal(["register off"], outdatedOff.calls);
        var on = Check(new ExistingTask(Desktop, "--tray", true), LogonTaskCheck.Answer.RePoint);
        Assert.Equal(["register"], on.calls);
        Assert.EndsWith("You replaced it with PoolWatch's own task, which starts " + Launcher + ".", Assert.Single(on.findings).Title);
        // Its sign-in trigger turned off: the task stays on, its trigger off.
        var triggerOff = Check(new ExistingTask(Desktop, "--tray", true, SignInTrigger: Trigger.Off), LogonTaskCheck.Answer.RePoint);
        Assert.Equal(["register trigger off"], triggerOff.calls);
        Assert.Equal(((bool?)true, (Trigger?)Trigger.Off), (triggerOff.verdict.On, triggerOff.verdict.SignInTrigger));
        Assert.EndsWith("You replaced it with PoolWatch's own task, which starts " + Launcher + "; its sign-in trigger stays off, as it was.", Assert.Single(triggerOff.findings).Title);
        var bothOff = Check(new ExistingTask(Installed, "--tray", true, Enabled: false, SignInTrigger: Trigger.Off), LogonTaskCheck.Answer.RePoint);
        Assert.Equal(["register off trigger off"], bothOff.calls);
        Assert.EndsWith("; it and its sign-in trigger stay off, as they were.", Assert.Single(bothOff.findings).Title);
        // No sign-in trigger: it gets PoolWatch's, turned off, so it still does not run when you sign in.
        var none = Check(new ExistingTask(Desktop, "--tray", true, SignInTrigger: Trigger.None), LogonTaskCheck.Answer.RePoint);
        Assert.Equal(["register trigger off"], none.calls);
        Assert.Equal((Trigger?)Trigger.None, none.verdict.SignInTrigger);
        Assert.EndsWith("You replaced it with PoolWatch's own task, which starts " + Launcher + "; it has PoolWatch's sign-in trigger, turned off, as it had none.", Assert.Single(none.findings).Title);
        // Not read: registered on, and the outcome says what was turned on.
        var unread = Check(null, LogonTaskCheck.Answer.RePoint, readError: new IOException("x"));
        Assert.Equal(["register"], unread.calls);
        Assert.Equal(((bool?)null, (Trigger?)null), (unread.verdict.On, unread.verdict.SignInTrigger));
        Assert.EndsWith("You replaced it with PoolWatch's own task, which starts " + Launcher + "; it and its sign-in trigger are turned on, as whether they were on could not be read.",
            Assert.Single(unread.findings).Title);
        Assert.Equal("; it is turned on, as whether it was on could not be read", LogonTaskCheck.RePointKept(null, Trigger.On));
        Assert.Equal("; its sign-in trigger stays off, as it was; it is turned on, as whether it was on could not be read", LogonTaskCheck.RePointKept(null, Trigger.Off));
        Assert.Equal("; it stays off, as it was; its sign-in trigger is turned on, as whether it was on could not be read", LogonTaskCheck.RePointKept(false, null));
        Assert.Equal("", LogonTaskCheck.RePointKept(true, Trigger.On));
        // What the question adds.
        const string tick = " so PoolWatch does not start when you sign in (tick Start PoolWatch when I sign in, in Settings, when you want it to)";
        Assert.Equal("; the task stays off, as it is now," + tick, LogonTaskCheck.RePointKeeps(false, Trigger.On));
        Assert.Equal("; its sign-in trigger stays off, as it is now," + tick, LogonTaskCheck.RePointKeeps(true, Trigger.Off));
        Assert.Equal("; the task and its sign-in trigger stay off, as they are now," + tick, LogonTaskCheck.RePointKeeps(false, Trigger.Off));
        Assert.Equal("; it has no sign-in trigger now, and gets PoolWatch's, turned off, as its only trigger," + tick, LogonTaskCheck.RePointKeeps(true, Trigger.None));
        Assert.Equal("; the task stays off, as it is now; it has no sign-in trigger now, and gets PoolWatch's, turned off, as its only trigger," + tick,
            LogonTaskCheck.RePointKeeps(false, Trigger.None));
        Assert.Equal("; the task is turned on, as whether it was on could not be read", LogonTaskCheck.RePointKeeps(null, Trigger.On));
        Assert.Equal("; the task and its sign-in trigger are turned on, as whether they were on could not be read", LogonTaskCheck.RePointKeeps(null, null));
        Assert.Equal("; its sign-in trigger stays off, as it is now," + tick + "; the task is turned on, as whether it was on could not be read",
            LogonTaskCheck.RePointKeeps(null, Trigger.Off));
        Assert.Equal("", LogonTaskCheck.RePointKeeps(true, Trigger.On));
        string ui = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Ui", "UiRunner.cs"));
        Assert.Contains("installed in an administrator-only folder{LogonTaskCheck.RePointKeeps(v.On, v.SignInTrigger)}.\\n", ui);
        // The definition carries both states; the task's states are read from Task Scheduler.
        string xml = Ui.LogonTask.TaskXml(Launcher, @"PC1\alice", on: false).Replace("\r\n", "\n");
        Assert.Contains("<Enabled>false</Enabled>\n    <Priority>7</Priority>", xml);
        Assert.Contains("<LogonTrigger><Enabled>true</Enabled>", xml);
        string triggerXml = Ui.LogonTask.TaskXml(Launcher, @"PC1\alice", signInTrigger: false).Replace("\r\n", "\n");
        Assert.Contains("<LogonTrigger><Enabled>false</Enabled>", triggerXml);
        Assert.Contains("<Enabled>true</Enabled>\n    <Priority>7</Priority>", triggerXml);
        string onXml = Ui.LogonTask.TaskXml(Launcher, @"PC1\alice").Replace("\r\n", "\n");
        Assert.Contains("<Enabled>true</Enabled>\n    <Priority>7</Priority>", onXml);
        Assert.Contains("<LogonTrigger><Enabled>true</Enabled>", onXml);
        string tray = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Ui", "TrayIcons.cs")).Replace("\r\n", "\n");
        string read = MainWindowLayoutTests.Block(tray, "public static Engine.LogonTaskPolicy.ExistingTask? Read()");
        Assert.Contains("try { signIn = SignInTriggers(com, (object)def.Triggers); } catch { }", read);
        Assert.Contains("return new Engine.LogonTaskPolicy.ExistingTask(actions, highest, enabled, signIn, userId, logonType);", read);
        Assert.Contains("LogonTask.Set(true, Path.Combine(p.DataRoot, \"logon-task.xml\"), p.Guard.CheckReal, on, signInTrigger);",
            MainWindowLayoutTests.Block(tray, "public string? RegisterCurrent(bool on = true, bool signInTrigger = true)"));
        Assert.Contains("TaskXml(launcher, System.Security.Principal.WindowsIdentity.GetCurrent().Name, on, signInTrigger)", tray);
    }

    /// <summary>A trigger as Task Scheduler's ITrigger gives it (its type and whether it is on), for <see cref="Ui.LogonTask.SignInTriggers"/>.</summary>
    public sealed record FakeTrigger(int Type, bool Enabled);

    /// <summary>
    /// The task's sign-in triggers as read: on when one sign-in trigger (TASK_TRIGGER_LOGON, 9) is on; off when it has
    /// sign-in triggers and each is off, whatever its other triggers; none when it has no sign-in trigger, even with a
    /// trigger of another kind that is on.
    /// </summary>
    [Fact]
    public void ATasksSignInTriggersAreReadByTheirKind()
    {
        Trigger Read(params FakeTrigger[] triggers)
        {
            using var com = new ComScope();
            return Ui.LogonTask.SignInTriggers(com, triggers);
        }
        Assert.Equal(Trigger.On, Read(new FakeTrigger(9, true)));
        Assert.Equal(Trigger.On, Read(new FakeTrigger(9, false), new FakeTrigger(9, true)));
        Assert.Equal(Trigger.Off, Read(new FakeTrigger(9, false)));
        Assert.Equal(Trigger.Off, Read(new FakeTrigger(9, false), new FakeTrigger(8, true))); // TASK_TRIGGER_BOOT, on
        Assert.Equal(Trigger.None, Read(new FakeTrigger(8, true)));
        Assert.Equal(Trigger.None, Read());
        Assert.Contains("private const int LogonTriggerType = 9;", File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Ui", "TrayIcons.cs")));
    }

    /// <summary>
    /// A task whose on or off state, sign-in triggers or run level Task Scheduler does not give is never taken for one that
    /// starts PoolWatch: what is read is "not known" (the types can say so, and the reader starts from it), the question
    /// says the re-point turns it on, and it is registered on.
    /// </summary>
    [Fact]
    public void AStateThatCannotBeReadIsNotTakenForOn()
    {
        Assert.Equal(typeof(bool?), typeof(ExistingTask).GetProperty(nameof(ExistingTask.Enabled))!.PropertyType);
        Assert.Equal(typeof(Trigger?), typeof(ExistingTask).GetProperty(nameof(ExistingTask.SignInTrigger))!.PropertyType);
        Assert.Equal(typeof(bool?), typeof(ExistingTask).GetProperty(nameof(ExistingTask.Highest))!.PropertyType);
        string tray = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Ui", "TrayIcons.cs")).Replace("\r\n", "\n");
        string read = MainWindowLayoutTests.Block(tray, "public static Engine.LogonTaskPolicy.ExistingTask? Read()");
        Assert.Contains("bool? highest = null;", read);
        Assert.Contains("try { highest = (int)com.Track(def.Principal)!.RunLevel == 1; } catch { }", read);
        // Its account and sign-in kind the same way (null: not PoolWatch's), and each action with its start folder.
        Assert.Contains("string? userId = null;", read);
        Assert.Contains("try { userId = (string?)com.Track(def.Principal)!.UserId; } catch { }", read);
        Assert.Contains("int? logonType = null;", read);
        Assert.Contains("try { logonType = (int)com.Track(def.Principal)!.LogonType; } catch { }", read);
        Assert.Contains("new Engine.LogonTaskPolicy.TaskAction((string)(a.Path ?? \"\"), (string?)a.Arguments, WorkingDirectory: (string?)a.WorkingDirectory)", read);
        Assert.Contains("bool? enabled = null;", read);
        Assert.Contains("try { enabled = (bool)task.Enabled; } catch { }", read);
        Assert.Contains("Engine.LogonTaskPolicy.Trigger? signIn = null;", read);
        foreach (var task in new[] { new ExistingTask(Desktop, "--tray", true, Enabled: null), new ExistingTask(Installed, "--tray", true, SignInTrigger: null) })
        {
            var r = Check(task, LogonTaskCheck.Answer.RePoint);
            Assert.Equal(["register"], r.calls);
            Assert.Contains("is turned on, as whether it was on could not be read", LogonTaskCheck.RePointKeeps(r.verdict.On, r.verdict.SignInTrigger));
            Assert.Equal(SignInState.NotKnown, AtSignIn(task, Judgement.Fine, highestNeeded: true));
        }
        var levelUnread = new ExistingTask(Launcher, "--tray", Highest: null);
        Assert.Equal(SignInState.NotKnown, AtSignIn(levelUnread, ForSettings(levelUnread), highestNeeded: true));
        Assert.Equal(SignInState.Starts, AtSignIn(levelUnread, ForSettings(levelUnread), highestNeeded: false)); // its run level does not matter then
    }

    /// <summary>
    /// Whether PoolWatch starts when you sign in, as Settings and the setup guide show it: only a task that is there, on,
    /// with a sign-in trigger on, fine by the check at start, and with highest privileges where this account needs them.
    /// A switch of its own that keeps it from running is said first, by which (even when it is not safe either); then one
    /// whose state was not read; then one that is not safe; then the run level.
    /// </summary>
    [Fact]
    public void ATaskStartsPoolWatchAtSignInOnlyWhenItIsOnItsTriggerIsOnItIsSafeAndItHasTheRightsItNeeds()
    {
        SignInState At(ExistingTask? task, bool highestNeeded = true) => AtSignIn(task, ForSettings(task), highestNeeded);
        var fine = new ExistingTask(Launcher, "--tray", true);
        Assert.Equal(SignInState.Starts, At(fine));
        Assert.Equal(SignInState.NoTask, At(null));
        foreach (var off in new[] { fine with { Enabled = false }, fine with { Enabled = false, SignInTrigger = Trigger.Off }, fine with { Enabled = false, SignInTrigger = null },
                                    fine with { Enabled = false, SignInTrigger = Trigger.None }, fine with { Enabled = false, Highest = false } })
            Assert.Equal(SignInState.TaskOff, At(off));
        foreach (var triggerOff in new[] { fine with { SignInTrigger = Trigger.Off }, fine with { Enabled = null, SignInTrigger = Trigger.Off } })
            Assert.Equal(SignInState.TriggerOff, At(triggerOff));
        foreach (var none in new[] { fine with { SignInTrigger = Trigger.None }, fine with { Enabled = null, SignInTrigger = Trigger.None } })
            Assert.Equal(SignInState.NoTrigger, At(none));
        foreach (var unknown in new[] { fine with { Enabled = null }, fine with { SignInTrigger = null }, fine with { Highest = null } })
            Assert.Equal(SignInState.NotKnown, At(unknown));
        var desktop = new ExistingTask(Desktop, "--tray", true);
        Assert.Equal(SignInState.NotSafe, At(desktop));
        Assert.Equal(SignInState.NotSafe, At(desktop with { Highest = false }));
        var gone = new ExistingTask(@"C:\Program Files\Old\PoolWatch.exe", "--tray", true);
        Assert.Equal(SignInState.NotSafe, At(gone));
        Assert.Equal(SignInState.TaskOff, At(desktop with { Enabled = false }));
        Assert.Equal(SignInState.NoTrigger, At(desktop with { SignInTrigger = Trigger.None }));
        // Without highest privileges, a task cannot start the launcher (it starts only as administrator) where UAC gives
        // it the account's token without administrator rights; where the token is not split, it can.
        var notHighest = fine with { Highest = false };
        Assert.Equal(SignInState.NotHighest, At(notHighest));
        Assert.Equal(SignInState.Starts, At(notHighest, highestNeeded: false));
        // The check at start still asks about such a task: it is not the task PoolWatch makes.
        Assert.Equal(Judgement.Untrusted, Judge(notHighest).Judgement);
        Assert.Contains("it does not run with highest privileges", Judge(notHighest).Message);
        // Whether they are needed goes by whether every program of the account runs as administrator, as the launcher
        // judges its own account. The task signs in in person, which UAC splits for an administrator unless UAC is off or
        // this is the built-in Administrator without Admin Approval Mode.
        var me = new System.Security.Principal.SecurityIdentifier("S-1-5-21-1-2-3-1001");
        UacSwitches uacOn = new(ForAll: true, ForBuiltInAdministrator: false), uacOff = new(ForAll: false, ForBuiltInAdministrator: false);
        bool Needed(int elevation, bool interactive, UacSwitches? uac) =>
            Ui.LogonTask.HighestNeeded(ElevatedAccounts.OfToken(me, elevation, administrator: true, interactive, () => uac, () => new("S-1-5-21-1-2-3")));
        Assert.True(Needed(UnelevatedAccount.ElevationFull, interactive: true, uacOn));
        Assert.True(Needed(UnelevatedAccount.ElevationLimited, interactive: true, uacOn));
        Assert.True(Needed(0, interactive: true, uacOn)); // not read: needed
        Assert.False(Needed(UnelevatedAccount.ElevationDefault, interactive: true, uacOn)); // a sign-in in person UAC did not split
        // PoolWatch run from a batch or service sign-in, whose token can be unsplit where UAC splits a sign-in in
        // person: its token cannot tell, UAC's settings do.
        Assert.True(Needed(UnelevatedAccount.ElevationDefault, interactive: false, uacOn));
        Assert.True(Needed(UnelevatedAccount.ElevationDefault, interactive: false, null)); // the settings cannot be read
        Assert.False(Needed(UnelevatedAccount.ElevationDefault, interactive: false, uacOff));
        Assert.Equal(SignInState.NotHighest, At(notHighest, highestNeeded: Needed(UnelevatedAccount.ElevationDefault, interactive: false, uacOn)));
        Assert.InRange(UnelevatedAccount.ElevationTypeOfThisProcess(), UnelevatedAccount.ElevationDefault, UnelevatedAccount.ElevationLimited); // this process's own token reads
        // The real reader judges what it reads as the check at start does, with this account's need for highest privileges.
        string tray = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Ui", "TrayIcons.cs"));
        Assert.Contains("Engine.LogonTaskPolicy.AtSignIn(task, Engine.LogonTaskPolicy.Judge(task, null, false, Engine.InstallGuard.AdminRoots(), IsThisAccount, judgeRunLevel: false).Judgement, " +
                        "HighestNeeded());", Regex.Replace(tray, @"\s+", " "));
        Assert.Contains("public static Core.Setup.SignInState AtSignIn() => AtSignIn(Read());", tray);
        Assert.Contains("public static bool HighestNeeded() => HighestNeeded(Engine.ElevatedAccounts.OfThisProcess());", tray);
        Assert.Contains("internal static bool HighestNeeded(Engine.ElevatedAccounts ownAccount) => ownAccount.Known.Count == 0;", tray);
    }

    /// <summary>
    /// Settings' "Start PoolWatch when I sign in" is ticked only when PoolWatch starts when you sign in, so a task that a
    /// re-point kept off shows unticked, and the question's advice to tick it can be followed; a task that is there but
    /// does not start PoolWatch, or could not be read, says why under the box, by which switch or reason. The box reads the
    /// task's state, never only whether a task is there, and every text that says to tick it calls it by its name.
    /// </summary>
    [Fact]
    public void SettingsTicksStartAtSignInOnlyWhenPoolWatchStartsThen()
    {
        var keptOff = Check(new ExistingTask(Desktop, "--tray", true, Enabled: false), LogonTaskCheck.Answer.RePoint);
        Assert.Contains("tick Start PoolWatch when I sign in, in Settings", LogonTaskCheck.RePointKeeps(keptOff.verdict.On, keptOff.verdict.SignInTrigger));
        // The task it registered: on Settings' next read it is off, and the box is not ticked, with why.
        var registered = new ExistingTask(Launcher, "--tray", true, Enabled: false);
        var shown = Ui.SettingsDialog.LogonTick(AtSignIn(registered, ForSettings(registered), highestNeeded: true), null);
        Assert.False(shown.Ticked);
        Assert.Equal("Its task is turned off, so PoolWatch does not start at sign-in. Tick to turn it on.", shown.Note);
        Assert.Equal((true, (string?)null), Ui.SettingsDialog.LogonTick(SignInState.Starts, null));
        Assert.Equal((false, (string?)null), Ui.SettingsDialog.LogonTick(SignInState.NoTask, null));
        Assert.Equal("Its task's sign-in trigger is turned off. Tick to turn it on.", Ui.SettingsDialog.LogonTick(SignInState.TriggerOff, null).Note);
        Assert.Equal("Its task has no sign-in trigger. Tick to set it up again.", Ui.SettingsDialog.LogonTick(SignInState.NoTrigger, null).Note);
        Assert.Equal("Its task runs without highest privileges, which PoolWatch needs. Tick to set it up again.", Ui.SettingsDialog.LogonTick(SignInState.NotHighest, null).Note);
        Assert.Equal("PoolWatch could not read all of its task's settings. Tick to set it up again.", Ui.SettingsDialog.LogonTick(SignInState.NotKnown, null).Note);
        Assert.Equal("PoolWatch asks about its task at each start. Tick to set it up again.", Ui.SettingsDialog.LogonTick(SignInState.NotSafe, null).Note);
        // A task that could not be read: said in the line, Windows' message in the line's tooltip (whatever its length).
        var unread = Ui.SettingsDialog.LogonTick(SignInState.NotKnown, "Access is denied.");
        Assert.Equal((false, "PoolWatch could not read its task. Tick to set it up again."), unread);
        foreach (var state in Enum.GetValues<SignInState>().Where(s => s != SignInState.Starts))
            Assert.False(Ui.SettingsDialog.LogonTick(state, null).Ticked);
        // One name for the box: its own label, the question's two answers, and the guide's and the suggestions' advice.
        Assert.Equal("Start PoolWatch when I sign in", Core.Setup.SignInStates.SettingsBox);
        string src = Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch");
        string runner = File.ReadAllText(Path.Combine(src, "Ui", "UiRunner.cs"));
        Assert.Equal(2, Regex.Matches(runner, Regex.Escape("{Core.Setup.SignInStates.TickInSettings}")).Count);
        Assert.DoesNotContain("tick Start at logon", runner);
        Assert.DoesNotMatch(@"tick Start PoolWatch when I sign in", runner + File.ReadAllText(Path.Combine(src, "Engine", "LogonTaskPolicy.cs"))); // written once, in Core
        // The dialog reads the task's state and shows it before it listens to the box; nothing asks only whether a task is there.
        string settings = File.ReadAllText(Path.Combine(src, "Ui", "SettingsDialog.cs")).Replace("\r\n", "\n");
        Assert.Contains("private readonly CheckBox _logon = new WrapCheckBox { Text = Core.Setup.SignInStates.SettingsBox, AutoSize = true };", settings);
        Assert.Contains("Task.Run(() => ReadSignIn(LogonTask.AtSignIn)).ContinueWith(", settings);
        string wire = MainWindowLayoutTests.Block(settings, "private void WireLogon((SignInState State, string? Error) read, UiActions actions)");
        Assert.True(wire.IndexOf("ShowSignIn(read);", StringComparison.Ordinal) is >= 0 and var shownAt && shownAt < wire.IndexOf("_logon.CheckedChanged +=", StringComparison.Ordinal), wire);
        Assert.Contains("LogonBoxChanged(\n            ticked => LogonTask.Set(ticked, Path.Combine(_engine!.Paths.DataRoot, \"logon-task.xml\"), _engine.Paths.Guard.CheckReal),", wire);
        Assert.DoesNotContain("LogonTask.Exists", settings);
        Assert.DoesNotContain("public static bool Exists()", File.ReadAllText(Path.Combine(src, "Ui", "TrayIcons.cs")));
    }

    /// <summary>
    /// The sign-in box as the owner uses it: a read that fails shows as not known with Windows' message in the line's
    /// tooltip; a tick that registers the task hides the line under the box; a tick or untick that does not change the
    /// task, or throws, puts the box back as it was, without running the change again, and says why once.
    /// </summary>
    [Fact]
    public void TheSignInBoxShowsTheTaskAsReadAndNeverAChangeThatWasNotMade()
    {
        var failed = Ui.SettingsDialog.ReadSignIn(() => throw new UnauthorizedAccessException("Access is denied."));
        Assert.Equal((SignInState.NotKnown, (string?)"Access is denied."), failed);
        Assert.Equal((SignInState.TaskOff, (string?)null), Ui.SettingsDialog.ReadSignIn(() => SignInState.TaskOff));
        var calls = new List<bool>();
        var told = new List<string>();
        // The change gives result the first time and works after that, so running it again would show in calls.
        Ui.SettingsDialog Shown((SignInState, string?) read, (bool, string) result)
        {
            var dialog = new Ui.SettingsDialog(new Core.Settings(), null);
            dialog.ShowSignIn(read);
            dialog.LogonBox.CheckedChanged += (_, _) => dialog.LogonBoxChanged(t => { calls.Add(t); return calls.Count == 1 ? result : (true, ""); }, told.Add);
            return dialog;
        }

        using (var notRead = Shown(failed, (true, "")))
        {
            Assert.False(notRead.LogonBox.Checked);
            Assert.Equal(("PoolWatch could not read its task. Tick to set it up again.", true, "Access is denied."),
                (notRead.LogonNote.Text, WindowFitTests.OwnVisible(notRead.LogonNote), notRead.LogonNoteTip));
            notRead.LogonBox.Checked = true; // registered: the line and its tooltip go
            Assert.Equal([true], calls);
            Assert.Equal((true, false, ""), (notRead.LogonBox.Checked, WindowFitTests.OwnVisible(notRead.LogonNote), notRead.LogonNoteTip));
            Assert.Empty(told);
        }
        calls.Clear();
        using (var off = Shown((SignInState.TaskOff, null), (false, "its launcher is not there")))
        {
            Assert.Equal(("Its task is turned off, so PoolWatch does not start at sign-in. Tick to turn it on.", true, ""),
                (off.LogonNote.Text, WindowFitTests.OwnVisible(off.LogonNote), off.LogonNoteTip));
            off.LogonBox.Checked = true; // not registered: the box goes back, the line stays, and why is told once
            Assert.Equal([true], calls);
            Assert.Equal((false, true), (off.LogonBox.Checked, WindowFitTests.OwnVisible(off.LogonNote)));
            Assert.Equal(["its launcher is not there"], told);
        }
        calls.Clear();
        told.Clear();
        using (var starts = Shown((SignInState.Starts, null), (false, "access denied")))
        {
            Assert.Equal((true, false), (starts.LogonBox.Checked, WindowFitTests.OwnVisible(starts.LogonNote)));
            starts.LogonBox.Checked = false; // not deleted: ticked again
            Assert.Equal([false], calls);
            Assert.True(starts.LogonBox.Checked);
            Assert.Equal(["access denied"], told);
        }
        calls.Clear();
        told.Clear();
        using var throws = new Ui.SettingsDialog(new Core.Settings(), null);
        throws.ShowSignIn((SignInState.TaskOff, null));
        throws.LogonBox.CheckedChanged += (_, _) => throws.LogonBoxChanged(t => { calls.Add(t); return calls.Count == 1 ? throw new IOException("the data folder") : (true, ""); }, told.Add);
        Assert.Throws<IOException>(() => throws.LogonBox.Checked = true); // the caller (UiActions.Guarded) says why
        Assert.Equal([true], calls);
        Assert.False(throws.LogonBox.Checked);
        Assert.Empty(told);
    }

    /// <summary>Both runners: the tray app asks (and only an installed copy may re-point); headless always leaves the task as it is.</summary>
    [Fact]
    public void TheTrayAppAsksAndHeadlessOnlySays()
    {
        string src = Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch");
        string ui = Regex.Replace(File.ReadAllText(Path.Combine(src, "Ui", "UiRunner.cs")), @"\s+", " ");
        Assert.Contains("new LogonTaskCheck(new WindowsLogonTasks(paths), v => AskAboutLogonTask(ui, v), log.Info, analysis.Diagnostics.RecordFromOtherThread) " +
                        ".Run(Environment.ProcessPath, LogonTaskPolicy.WhyNotRegister(Environment.ProcessPath, opt.AllowAnyLocation, false, InstallGuard.AdminRoots()) is null,", ui);
        string headless = Regex.Replace(File.ReadAllText(Path.Combine(src, "Headless", "HeadlessRunner.cs")), @"\s+", " ");
        Assert.Contains("new LogonTaskCheck(new Ui.WindowsLogonTasks(paths), _ => LogonTaskCheck.Answer.Leave, log.Info, analysis.Diagnostics.RecordFromOtherThread) " +
                        ".Run(Environment.ProcessPath, false, InstallGuard.AdminRoots());", headless);
    }
}
