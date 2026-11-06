using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using PoolWatch.Core.Changes;
using PoolWatch.Core.TokenGuard;
using PoolWatch.Engine;
using PoolWatch.Engine.Changes;
using PoolWatch.Native;

namespace PoolWatch.AppTests;

/// <summary>
/// How a helper run is started and who it trusts. The job start goes through a fake of the Windows calls, so no test starts
/// a process: what Windows does with what it is given (a process created in its job, suspended; a job's last handle closed
/// when PoolWatch dies) is Windows' documented behaviour, and CI's end-to-end runs show it on a real start.
/// </summary>
public sealed class TokenLeakHelperStartTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pw-start-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    internal static string RepoDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PoolWatch.slnx"))) return dir.FullName;
        throw new InvalidOperationException("PoolWatch.slnx not found above " + AppContext.BaseDirectory);
    }

    /// <summary>A fake of the Windows calls: it records each call in order, with what it was given.</summary>
    private sealed class FakeStarter : JobNative.IStarter
    {
        public readonly List<string> Calls = new();
        public bool Created = true, InTheJob = true;
        public int ResumeError, OutliveError;
        public (string app, string line, bool inherit, uint flags, string env, string cwd, nint[] jobs)? Seen;
        public Action<nint[]>? OnCreate;

        public bool Create(string application, string commandLine, bool inheritHandles, uint flags, string environment, string workingDirectory,
            nint[] jobs, out nint process, out nint thread, out int error)
        {
            Calls.Add("create");
            Seen = (application, commandLine, inheritHandles, flags, environment, workingDirectory, jobs);
            OnCreate?.Invoke(jobs);
            (process, thread, error) = Created ? (101, 102, 0) : (0, 0, 2);
            return Created;
        }

        public bool InJob(nint process, nint job) { Calls.Add($"injob {process} {job}"); return InTheJob; }
        public int Resume(nint thread) { Calls.Add($"resume {thread}"); return ResumeError; }
        public int LetOutlive(nint job) { Calls.Add($"outlive {job}"); return OutliveError; }
        public void Terminate(nint process) => Calls.Add($"terminate {process}");
        public void EndJob(nint job) => Calls.Add($"endjob {job}");
        public void Close(nint handle) => Calls.Add($"close {handle}");
    }

    private static readonly IReadOnlyDictionary<string, string> Env = new Dictionary<string, string> { ["SystemRoot"] = @"C:\Windows", ["B"] = "2" };

    [Fact]
    public void TheHelperIsCreatedInItsJobSuspendedInheritingNothingAndLetRunOnlyOnceItIsSeenThere()
    {
        foreach (bool outlive in new[] { false, true })
        {
            var w = new FakeStarter();
            nint process = JobNative.StartInJob(77, @"C:\Windows\System32\cmd.exe", "\"cmd\" /c x", @"C:\Windows\System32", Env, outlive, w);
            Assert.Equal(101, process);
            var seen = w.Seen!.Value;
            Assert.False(seen.inherit);                                          // bInheritHandles FALSE: no handle of PoolWatch's goes with it
            Assert.Equal([77], seen.jobs);                                       // created in its job (PROC_THREAD_ATTRIBUTE_JOB_LIST)
            Assert.Equal(JobNative.StartFlags, seen.flags);
            Assert.Equal(JobNative.CREATE_SUSPENDED, seen.flags & JobNative.CREATE_SUSPENDED);                         // runs nothing yet
            Assert.Equal(JobNative.EXTENDED_STARTUPINFO_PRESENT, seen.flags & JobNative.EXTENDED_STARTUPINFO_PRESENT); // the job list is read
            Assert.Equal(JobNative.CREATE_UNICODE_ENVIRONMENT, seen.flags & JobNative.CREATE_UNICODE_ENVIRONMENT);     // its own environment
            Assert.Equal(JobNative.CREATE_NO_WINDOW, seen.flags & JobNative.CREATE_NO_WINDOW);
            Assert.Equal("B=2\0SystemRoot=C:\\Windows\0\0", seen.env);           // exactly what it was given, sorted, as Windows reads it
            // Let run only once Windows says it is in the job; let outlive PoolWatch (the logon check's run) only once it runs,
            // so a kill of PoolWatch while it is suspended ends it with the job.
            Assert.Equal(outlive ? new[] { "create", "injob 101 77", "resume 102", "outlive 77", "close 102" } : new[] { "create", "injob 101 77", "resume 102", "close 102" }, w.Calls);
        }
    }

    [Fact]
    public void AProcessNotSeenInItsJobOrNotResumedIsEndedWhileStillSuspended()
    {
        var notIn = new FakeStarter { InTheJob = false };
        Assert.Equal("the process is not in its job", Assert.Throws<InvalidOperationException>(() => JobNative.StartInJob(77, "a", "b", "c", Env, true, notIn)).Message);
        Assert.Equal(["create", "injob 101 77", "terminate 101", "close 101", "close 102"], notIn.Calls); // never resumed

        var notResumed = new FakeStarter { ResumeError = 5 };
        Assert.Equal("ResumeThread failed: 5", Assert.Throws<InvalidOperationException>(() => JobNative.StartInJob(77, "a", "b", "c", Env, true, notResumed)).Message);
        Assert.Equal(["create", "injob 101 77", "resume 102", "terminate 101", "close 101", "close 102"], notResumed.Calls);

        // Its job could not be let outlive PoolWatch: it is ended (its job still ends whatever it started once closed).
        var notOutlived = new FakeStarter { OutliveError = 5 };
        Assert.Equal("SetInformationJobObject failed: 5", Assert.Throws<InvalidOperationException>(() => JobNative.StartInJob(77, "a", "b", "c", Env, true, notOutlived)).Message);
        Assert.Equal(["create", "injob 101 77", "resume 102", "outlive 77", "terminate 101", "close 101", "close 102"], notOutlived.Calls);

        var refused = new FakeStarter { Created = false };
        Assert.Equal("CreateProcess failed: 2", Assert.Throws<InvalidOperationException>(() => JobNative.StartInJob(77, "a", "b", "c", Env, true, refused)).Message);
        Assert.Equal(["create"], refused.Calls); // nothing was made, so nothing to end
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetHandleInformation(nint handle, out uint flags);

    [Fact]
    public void EveryHelpersJobEndsItWithPoolWatchUntilItRunsAndOnlyTheLogonChecksThenLetsItOutlivePoolWatch()
    {
        // The job each run is started in, looked at as the start is asked for (the fake then refuses it, so nothing runs).
        // Every job ends its processes when its last handle closes, and that handle is PoolWatch's alone (not inheritable):
        // when PoolWatch is killed, Windows closes it and the job ends the helper (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE), also
        // while it is still suspended. Only the logon check's run asks for its job to let it outlive PoolWatch, once it runs.
        foreach (bool endWithPoolWatch in new[] { true, false })
        {
            uint? limits = null, handleFlags = null;
            var w = new FakeStarter { OnCreate = jobs => { limits = JobNative.LimitFlags(jobs[0]); handleFlags = GetHandleInformation(jobs[0], out uint f) ? f : null; }, InTheJob = false };
            var start = HelperCommand.For(@"C:\Program Files\PoolWatch\PoolWatch.exe", 100, @"C:\ProgramData\PoolWatch\cache\r.json", null, endWithPoolWatch);
            Assert.ThrowsAny<InvalidOperationException>(() => JobHelperRun.Start(start, w)); // the fake says it is not in its job: nothing is resumed
            Assert.Equal(JobNative.KillOnJobClose, limits!.Value); // exactly: no breakaway, nothing else
            Assert.Equal(0u, handleFlags!.Value & 1u); // HANDLE_FLAG_INHERIT
            Assert.Equal(start.Application, w.Seen!.Value.app);
            Assert.Equal(start.CommandLine, w.Seen!.Value.line);
            Assert.Equal(start.WorkingDirectory, w.Seen!.Value.cwd);
            Assert.Equal(JobNative.EnvironmentBlock(start.Environment), w.Seen!.Value.env);

            var started = new FakeStarter();
            // A start that goes through, up to the point where the real handles would be used: the fake's process handle is
            // not a real one, so the run is not kept; what matters is the order of the calls.
            nint job = JobNative.CreateKillOnClose();
            try
            {
                JobNative.StartInJob(job, start.Application, start.CommandLine, start.WorkingDirectory, start.Environment, outlive: !endWithPoolWatch, started);
                Assert.Equal(!endWithPoolWatch, started.Calls.Contains($"outlive {job}"));
            }
            finally { JobNative.Close(job); }
        }
        // The real call takes the limit off.
        nint logon = JobNative.CreateKillOnClose();
        try
        {
            Assert.Equal(0, JobNative.LetOutlive(logon));
            Assert.Equal(0u, JobNative.LimitFlags(logon)); // no limit left, and none other was ever set
        }
        finally { JobNative.Close(logon); }
        // And JobHelperRun asks for it only for the logon check's run.
        string source = UninstallerTests.OneLine(File.ReadAllText(Path.Combine(RepoDir(), "src", "PoolWatch", "Engine", "HelperRun.cs")));
        Assert.Contains("nint job = JobNative.CreateKillOnClose();", source);
        Assert.Contains("start.Environment, outlive: !start.EndWithPoolWatch, starter));", source);
    }

    [Fact]
    public void TheJobIsSetToEndItsProcessesWhenItsLastHandleCloses()
    {
        // A new, empty job: no process is put in it or ended. Windows holds its kill-on-close limit flag.
        nint job = JobNative.CreateKillOnClose();
        try
        {
            Assert.Equal(JobNative.KillOnJobClose, JobNative.LimitFlags(job)); // exactly: no breakaway, nothing else
            Assert.True(JobNative.Terminate(job));
        }
        finally { JobNative.Close(job); }
    }

    [Fact]
    public void TheRealStartPassesWhatItIsGivenToWindowsAsItIs()
    {
        // No test can start a process, so this holds the lines of the real calls: CreateProcessW gets the arguments as given
        // (bInheritHandles from the caller, which always passes false), and the attribute list holds the job list.
        string source = UninstallerTests.OneLine(File.ReadAllText(Path.Combine(RepoDir(), "src", "PoolWatch", "Native", "JobNative.cs")));
        Assert.Contains("if (!CreateProcessW(application, p, 0, 0, inheritHandles, flags, env, workingDirectory, ref si, out var pi))", source);
        Assert.Contains("if (!UpdateProcThreadAttribute(list, 0, ProcThreadAttributeJobList, jobList, jobs.Length * sizeof(nint), 0, 0))", source);
        Assert.Contains("var si = new STARTUPINFOEXW { StartupInfo = { cb = sizeof(STARTUPINFOEXW) }, lpAttributeList = list };", source);
        Assert.Contains("if (!starter.Create(application, commandLine, inheritHandles: false, StartFlags, EnvironmentBlock(environment), workingDirectory, [job],", source);
        Assert.Contains("internal const int ProcThreadAttributeJobList = 0x2000D;", source);
        Assert.Contains("internal const uint StartFlags = CREATE_SUSPENDED | CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT | EXTENDED_STARTUPINFO_PRESENT;", source);
        Assert.Contains("public bool InJob(nint process, nint job) => IsProcessInJob(process, job, out bool result) && result;", source);
        Assert.Contains("public int LetOutlive(nint job) => JobNative.LetOutlive(job);", source);
    }

    // ---------------------------------------------------------------- the command line and the environment

    /// <summary>
    /// What cmd.exe does with the command line it is given (cmd /? for /S and /C; percent expansion on a command line): with
    /// /s it removes the first and the last quote after /c, then replaces each %NAME% that is set, once, from its own
    /// environment, and does not look for % again in what it put in.
    /// </summary>
    private static string AfterCmd(HelperStart start)
    {
        const string switches = " /d /v:off /s /c ";
        int at = start.CommandLine.IndexOf(switches, StringComparison.Ordinal);
        Assert.True(at > 0, start.CommandLine);
        string rest = start.CommandLine[(at + switches.Length)..];
        Assert.True(rest.StartsWith('"') && rest.EndsWith('"'), rest);
        rest = rest[1..^1];
        return Regex.Replace(rest, "%([^%]+)%", m => start.Environment.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
    }

    [Theory]
    [InlineData(@"C:\x\out\")]
    [InlineData(@"C:\x\out\\")]
    [InlineData(@"C:\")]
    [InlineData(@"C:\a b\%PATH%\!x!\&(^)|<>\")]
    [InlineData(@"C:\x\%SystemRoot%\%CD%\out")]
    [InlineData(@"\\server\share\x\")]
    public void AnyPathReachesTheHelperWhole(string dir)
    {
        string exe = Path.Combine(dir, "PoolWatch.exe"), result = Path.Combine(dir, "token-test-0.json");
        foreach (var parent in new HelperParent?[] { null, new HelperParent(1234, 133_000_000_000_000_000) })
        {
            var start = HelperCommand.For(exe, 100, result, parent, endWithPoolWatch: parent is null);
            // cmd.exe's own command line holds no path but its own: nothing in a path reaches its parser.
            Assert.Equal($"\"{Path.Combine(Environment.SystemDirectory, "cmd.exe")}\" /d /v:off /s /c \"%{HelperCommand.LineVariable}%\"", start.CommandLine);
            Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), start.Application);
            Assert.Equal(Environment.SystemDirectory, start.WorkingDirectory);
            string[] argv = TokenLeakHelperTests.Argv(AfterCmd(start));
            string[] expected = parent is { } p
                ? [exe, "--token-leak-test", "100", result, "--token-test-parent", "1234", "133000000000000000"]
                : [exe, "--token-leak-test", "100", result];
            Assert.Equal(expected, argv);
            // Every path is quoted, so cmd reads none of its characters as its own (& | < > ( ) ^ outside quotes).
            Assert.Matches("^\"[^\"]*\" --token-leak-test 100 \"[^\"]*\"( --token-test-parent 1234 133000000000000000)?$", start.Environment[HelperCommand.LineVariable]);
        }
    }

    [Fact]
    public void TheHelpersEnvironmentIsWindowsOwnFoldersAndItsCommandLineOnly()
    {
        var start = HelperCommand.For(@"C:\Program Files\PoolWatch\PoolWatch.exe", 100, @"C:\ProgramData\PoolWatch\cache\r.json", null, true);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows), system = Environment.SystemDirectory;
        Assert.Equal(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ComSpec"] = Path.Combine(system, "cmd.exe"),
            ["Path"] = system + ";" + windows,
            ["ProgramFiles"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ["ProgramFiles(x86)"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            ["ProgramW6432"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ["SystemDrive"] = Path.GetPathRoot(windows)!.TrimEnd('\\'),
            ["SystemRoot"] = windows,
            ["TEMP"] = Path.Combine(windows, "Temp"),
            ["TMP"] = Path.Combine(windows, "Temp"),
            ["windir"] = windows,
            [HelperCommand.LineVariable] = "\"C:\\Program Files\\PoolWatch\\PoolWatch.exe\" --token-leak-test 100 \"C:\\ProgramData\\PoolWatch\\cache\\r.json\"",
        }, start.Environment);
        // Nothing that changes how .NET starts, whatever this process's own environment holds (it is not read).
        Assert.DoesNotContain(start.Environment.Keys, k => Regex.IsMatch(k, "^(DOTNET_|COMPlus_|CORECLR_|COR_)", RegexOptions.IgnoreCase));
        string source = File.ReadAllText(Path.Combine(RepoDir(), "src", "PoolWatch", "Engine", "HelperRun.cs"));
        Assert.DoesNotContain("GetEnvironmentVariable", source);
    }

    [Theory]
    [InlineData(@"C:\x\out\")]
    [InlineData(@"C:\x\out\\")]
    [InlineData(@"C:\")]
    [InlineData(@"\\server\share\")]
    [InlineData(@"C:\a b\c")]
    [InlineData(@"C:\a\\b")]
    [InlineData("C:\\a\\\"b\\")]
    [InlineData("")]
    public void AQuotedPathIsReadBackWholeAndKeepsWhatFollowsIt(string path)
    {
        // A path that ends in backslashes keeps its closing quote: the backslashes in front of it are doubled, as the
        // helper's command-line parser reads them back (CommandLineToArgvW). So the next argument stays an argument.
        Assert.Equal(new[] { "x.exe", path, "--next" }, TokenLeakHelperTests.Argv("x.exe " + HelperCommand.QuotePath(path) + " --next"));
        Assert.StartsWith("\"", HelperCommand.QuotePath(path));
        Assert.EndsWith("\"", HelperCommand.QuotePath(path));
    }

    [Fact]
    public void AnEnvironmentBlockIsSortedAndRefusesWhatWindowsCannotHold()
    {
        Assert.Equal("a=1\0B=2\0c=3\0\0", JobNative.EnvironmentBlock(new Dictionary<string, string> { ["c"] = "3", ["B"] = "2", ["a"] = "1" }));
        Assert.Throws<ArgumentException>(() => JobNative.EnvironmentBlock(new Dictionary<string, string> { ["A=B"] = "1" }));
        Assert.Throws<ArgumentException>(() => JobNative.EnvironmentBlock(new Dictionary<string, string> { [""] = "1" }));
        Assert.Throws<ArgumentException>(() => JobNative.EnvironmentBlock(new Dictionary<string, string> { ["A"] = "1\02" }));
    }

    private sealed class NoResultRun : IHelperRun
    {
        public bool WaitForExit(int milliseconds) => true;
        public int ExitCode => 1;
        public void End() { }
        public void Dispose() { }
    }

    [Theory]
    [InlineData(@"out\")]
    [InlineData(@"out %PATH% !x!\")]
    public async Task AnOutFolderEndingInABackslashNoLongerSwallowsTheHelpersArguments(string outName)
    {
        // A headless --out folder that ends in a backslash once made the helper read the rest of its line as the folder's
        // name, losing --token-test-parent. The helper is no longer told --out at all (its result goes to the data root's
        // cache folder), and what it is told is quoted whole.
        var paths = new AppPaths(Path.Combine(_root, "data"), Path.Combine(_root, outName), secure: false);
        var log = new Log(Path.Combine(_root, "data", "g.log"));
        foreach (bool logon in new[] { true, false })
        {
            var starts = new List<HelperStart>();
            var guard = new TokenLeakGuard(paths, log, _ => { }, setNow: () => true, getLive: () => 20000,
                attemptGap: TimeSpan.FromMilliseconds(1), clickRetryGap: TimeSpan.FromMilliseconds(1), build: () => "26220.7344",
                startHelper: start => { lock (starts) starts.Add(start); return new NoResultRun(); });
            if (logon) await guard.RunAtLogon(null, CancellationToken.None);
            else await guard.TestOnly(CancellationToken.None);
            var argv = TokenLeakHelperTests.Argv(AfterCmd(starts[0]));
            Assert.DoesNotContain("--out", argv);
            Assert.Equal(Path.Combine(paths.DataRoot, "cache"), Path.GetDirectoryName(argv[3]));
            if (logon) Assert.Equal(["--token-test-parent", $"{Environment.ProcessId}", $"{TokenGuardNative.OwnCreationTime()}"], argv[4..]);
            else Assert.Equal(4, argv.Length);
        }
    }

    // ---------------------------------------------------------------- the helper's watch on PoolWatch

    [Fact]
    public void TheHelperKnowsPoolWatchByItsCreationTimeNotOnlyItsProcessId()
    {
        // This test host stands in for PoolWatch: its ID with its own creation time opens it, and it is running.
        long created = TokenGuardNative.OwnCreationTime();
        nint own = TokenGuardNative.OpenIfSame(Environment.ProcessId, created);
        Assert.NotEqual(0, own);
        try { Assert.False(TokenGuardNative.HasEnded(own)); }
        finally { TokenGuardNative.Close(own); }
        // The same ID with another creation time is a later process given that ID: PoolWatch has ended, whoever runs now.
        Assert.Equal(0, TokenGuardNative.OpenIfSame(Environment.ProcessId, created + 1));
        Assert.Equal(0, TokenGuardNative.OpenIfSame(Environment.ProcessId, created - 10_000_000));
        // No such process at all.
        Assert.Equal(0, TokenGuardNative.OpenIfSame(int.MaxValue - 3, created));
    }

    [Fact]
    public void TheHelpersParentIsReadWithItsCreationTime()
    {
        var o = PoolWatch.Headless.CommandLineOptions.Parse(["--token-leak-test", "100", @"C:\ProgramData\PoolWatch\cache\r.json", "--token-test-parent", "1234", "133000000000000000"]);
        Assert.Empty(o.Errors);
        Assert.Equal(new HelperParent(1234, 133_000_000_000_000_000), o.TokenTestParent);
        foreach (string[] bad in new[] { new[] { "--token-test-parent", "1234" }, ["--token-test-parent", "x", "1"], ["--token-test-parent", "1234", "-5"], ["--token-test-parent", "0", "5"] })
        {
            var b = PoolWatch.Headless.CommandLineOptions.Parse(["--token-leak-test", "100", "r.json", .. bad]);
            Assert.Null(b.TokenTestParent);
            Assert.Contains("--token-test-parent: <process id> <creation time>", b.Errors);
        }
    }

    [Fact]
    public void TheHelperTakesPoolWatchAsGoneWhenItCouldNotOpenItAtTheStart()
    {
        // The decision the orphaned helper's prompt rests on. PoolWatch could not be opened as the helper started (its process
        // had ended, or its ID now names a later process with another creation time): gone, without asking anything more.
        var who = new HelperParent(1234, 133_000_000_000_000_000);
        int asked = 0;
        bool Running(nint handle) { asked++; Assert.Equal(77, handle); return false; }
        Assert.True(TokenLeakGuard.ParentGone(who, 0, Running));
        Assert.Equal(0, asked);
        Assert.False(TokenLeakGuard.ParentGone(who, 77, Running));                                  // opened, and still running
        Assert.Equal(1, asked);
        Assert.True(TokenLeakGuard.ParentGone(who, 77, _ => true));                                  // opened, and ended since
        Assert.True(TokenLeakGuard.ParentGone(who, 77, _ => throw new InvalidOperationException())); // cannot be asked: ended
        Assert.False(TokenLeakGuard.ParentGone(null, 0, Running));                                   // a check's helper watches nothing
        // The helper asks for the click exactly when PoolWatch has gone and the setting is not 0 already.
        Assert.True(GuardPolicy.HelperShouldPrompt(true, 200000));
        Assert.True(GuardPolicy.HelperShouldPrompt(true, null));
        Assert.False(GuardPolicy.HelperShouldPrompt(true, 0));
        Assert.False(GuardPolicy.HelperShouldPrompt(false, 200000));
    }

    /// <summary>
    /// The orphaned helper's prompt is there only while the user's click is needed: not shown, and taken back while it is
    /// open (looked at every two seconds), once the live timeout is 0 or a tray app's token guard with its notices runs,
    /// which turns the workaround on when the logon needs it or asks for the same click itself. A timeout that cannot be
    /// read, or a look that fails, leaves it.
    /// </summary>
    [Fact]
    public void TheOrphanedHelpersPromptIsTakenBackOnceTheClickIsNoLongerNeeded()
    {
        Assert.True(GuardPolicy.HelperPromptStillNeeded(200000, trayGuardRuns: false));
        Assert.True(GuardPolicy.HelperPromptStillNeeded(null, trayGuardRuns: false));
        Assert.False(GuardPolicy.HelperPromptStillNeeded(0, trayGuardRuns: false));
        Assert.False(GuardPolicy.HelperPromptStillNeeded(200000, trayGuardRuns: true));

        uint? live = 200000;
        bool runs = false;
        int closed = 0;
        var look = new TokenLeakGuard.HelperPromptLook(() => GuardPolicy.HelperPromptStillNeeded(live, runs), () => closed++);
        Assert.False(look.Look());
        live = 0; // something turned the workaround on
        Assert.True(look.Look());
        Assert.Equal(1, closed);
        live = 200000;
        runs = true; // a tray app's guard with its notices runs: it takes over
        Assert.True(look.Look());
        Assert.Equal(2, closed);
        Assert.False(new TokenLeakGuard.HelperPromptLook(() => throw new InvalidOperationException(), () => closed++).Look());
        Assert.Equal(2, closed);

        // The prompt looks every two seconds with that rule, while it is open (it needs a desktop, so this holds its lines),
        // and looks for the guard-notices mark, never for the one-instance mutex.
        string source = UninstallerTests.OneLine(File.ReadAllText(Path.Combine(RepoDir(), "src", "PoolWatch", "Engine", "TokenLeakGuard.cs")));
        Assert.Contains("var look = new HelperPromptLook(() => GuardPolicy.HelperPromptStillNeeded(LiveOrNull(), GuardNoticesShown(GuardNoticesMutexName)), f.Close); " +
                        "using var timer = new System.Windows.Forms.Timer { Interval = (int)HelperPromptLook.LookEvery.TotalMilliseconds }; " +
                        "timer.Tick += (_, _) => { if (look.Look()) timer.Stop(); }; timer.Start(); System.Windows.Forms.Application.Run(f);", source);
        // The mark of this process's own Windows session: the tray app holds its session's, the helper looks for its own
        // session's (AdminNamespaceTests: each session's is a name of its own), through this one name step.
        Assert.Contains("internal static string GuardNoticesMutexName() => AdminNamespace.Shared.GuardNoticesMutexName(OwnSession());", source);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(source, @"\.GuardNoticesMutexName\("));
        Assert.DoesNotContain("MonitorMutexName", source);
        Assert.Equal(TimeSpan.FromSeconds(2), TokenLeakGuard.HelperPromptLook.LookEvery);
    }

    /// <summary>
    /// Whether a tray app's token guard with its notices runs is whether the mark that tray app holds can be opened (a
    /// test's own name here). A PoolWatch that holds only the one-instance mutex, as a headless run does, leaves the prompt
    /// wanted: it shows no notice. One that cannot be told, because the name step throws (PoolWatch's private namespace
    /// cannot be opened) or opening that name throws (an empty one here), is not taken for one, so the prompt stays.
    /// </summary>
    [Fact]
    public void OnlyATrayAppsGuardWithItsNoticesTakesTheOrphanedHelpersPromptBackAndOneThatCannotBeToldDoesNot()
    {
        string marks = @"Local\pw-test-notices-" + Guid.NewGuid().ToString("N"), monitor = @"Local\pw-test-monitor-" + Guid.NewGuid().ToString("N");
        Assert.False(TokenLeakGuard.GuardNoticesShown(() => marks));
        // A headless run holds the one-instance mutex as the tray app does, and no mark: the prompt is still wanted.
        int code = Engine.ExitSignal.AsTheOneInstance(monitor, () => -1,
            _ => GuardPolicy.HelperPromptStillNeeded(200000, TokenLeakGuard.GuardNoticesShown(() => marks)) ? 1 : 0);
        Assert.Equal(1, code);
        // The tray app's mark, from its first guard start until it exits.
        using (var held = TokenLeakGuard.HoldGuardNoticesMark(() => marks))
        {
            Assert.NotNull(held);
            Assert.True(TokenLeakGuard.GuardNoticesShown(() => marks));
            Assert.False(GuardPolicy.HelperPromptStillNeeded(200000, TokenLeakGuard.GuardNoticesShown(() => marks)));
        }
        Assert.False(TokenLeakGuard.GuardNoticesShown(() => marks));
        // Cannot be told: the namespace cannot be opened, or opening the name throws (an empty name).
        Assert.False(TokenLeakGuard.GuardNoticesShown(() => throw new Engine.AdminNamespaceException("the namespace could not be opened")));
        Assert.True(GuardPolicy.HelperPromptStillNeeded(200000, TokenLeakGuard.GuardNoticesShown(() => throw new Engine.AdminNamespaceException("x"))));
        Assert.ThrowsAny<ArgumentException>(() => Mutex.TryOpenExisting("", out _));
        Assert.False(TokenLeakGuard.GuardNoticesShown(() => ""));
        Assert.Null(TokenLeakGuard.HoldGuardNoticesMark(() => throw new Engine.AdminNamespaceException("x")));
        // The mark is not the one-instance mutex nor the exit event, and only the tray app holds it, as its guard starts.
        Assert.NotEqual(Engine.AdminNamespace.MonitorLeaf, Engine.AdminNamespace.GuardNoticesLeaf);
        Assert.NotEqual(Engine.AdminNamespace.ExitLeaf, Engine.AdminNamespace.GuardNoticesLeaf);
        string src = Path.Combine(RepoDir(), "src");
        var holders = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(f => File.ReadAllText(f).Contains("HoldGuardNoticesMark(")).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["TokenLeakGuard.cs", "TrayApp.cs"], holders);
        string tray = UninstallerTests.OneLine(File.ReadAllText(Path.Combine(src, "PoolWatch", "Ui", "TrayApp.cs")));
        Assert.Contains("_guardNoticesMark ??= TokenLeakGuard.HoldGuardNoticesMark(TokenLeakGuard.GuardNoticesMutexName); await d.StartTokenGuard(_guardNotices);", tray);
        Assert.Contains("_guardNoticesMark?.Dispose(); base.ExitThreadCore();", tray);
    }

    [Fact]
    public void TheHelperModeWritesItsResultExitsByItAndWatchesPoolWatchAsItIsTested()
    {
        // The helper runs only in a process of its own (no test starts one), so this holds RunHelperMode's whole text, with
        // every run of white space made one space: it opens PoolWatch at its start (by ID and creation time), writes its
        // result through WriteResult (tested above) into the file it was given, takes PoolWatch as gone through ParentGone
        // (tested above), and exits 0 only when it wrote the file. Any edit to it fails this test, except one that only changes
        // white space in that way.
        string source = File.ReadAllText(Path.Combine(RepoDir(), "src", "PoolWatch", "Engine", "TokenLeakGuard.cs"));
        var method = Regex.Match(source, @"(?ms)^    public static int RunHelperMode\(.*?^    \}");
        Assert.True(method.Success, "RunHelperMode is not in TokenLeakGuard.cs");
        Assert.Equal(UninstallerTests.OneLine("""
            public static int RunHelperMode(int n, string outFile, AppPaths paths, HelperParent? parent = null)
            {
                nint parentHandle = 0;
                if (parent is { } who)
                    try { parentHandle = TokenGuardNative.OpenIfSame(who.Pid, who.CreationTime); } catch { }
                object result = Measure(n);
                bool written;
                try { written = WriteResult(paths.Guard.CheckReal(outFile), JsonSerializer.Serialize(result)); }
                catch { written = false; }
                bool gone;
                try { gone = ParentGone(parent, parentHandle, TokenGuardNative.HasEnded); }
                finally { TokenGuardNative.Close(parentHandle); }
                uint? live = LiveOrNull();
                if (GuardPolicy.HelperShouldPrompt(gone, live) && GuardPolicy.HelperPromptStillNeeded(live, GuardNoticesShown(GuardNoticesMutexName)))
                    try { PromptTurnOnOrphaned(paths); } catch { /* best effort: the marker still makes the next start turn it on */ }
                return written ? 0 : ExitCodes.ResultNotWritten;
            }
            """), UninstallerTests.OneLine(method.Value));
        // And the program starts it with what the guard passes: the result file and the parent.
        string program = UninstallerTests.OneLine(File.ReadAllText(Path.Combine(RepoDir(), "src", "PoolWatch", "Program.cs")));
        Assert.Contains("if (opt.TokenLeakTest is { } tl) return Engine.TokenLeakGuard.RunHelperMode(tl.n, tl.file, new Engine.AppPaths(opt.OutDir), opt.TokenTestParent);", program);
    }

    // ---------------------------------------------------------------- the run's own job

    [Fact]
    public void EndingARunEndsItsOwnJobAndAFailedStartEndsAndClosesItsJob()
    {
        var start = HelperCommand.For(@"C:\Program Files\PoolWatch\PoolWatch.exe", 100, @"C:\ProgramData\PoolWatch\cache\r.json", null, endWithPoolWatch: true);
        // A start that goes through: End ends the processes of the run's own job, through the job's handle, never by process;
        // Dispose then closes the two handles.
        var ok = new FakeStarter();
        var run = JobHelperRun.Start(start, ok);
        nint job = ok.Seen!.Value.jobs[0];
        try
        {
            int started = ok.Calls.Count;
            run.End();
            run.Dispose();
            Assert.Equal([$"endjob {job}", "close 101", $"close {job}"], ok.Calls.Skip(started));
            Assert.DoesNotContain(ok.Calls, c => c.StartsWith("terminate", StringComparison.Ordinal));
        }
        finally { JobNative.Close(job); } // the fake closed nothing real

        // A start that fails once the helper may already run (the logon check's job could not be let outlive PoolWatch):
        // whatever runs in its job is ended through the job, and the job's only handle is closed, so nothing is left running.
        var failed = new FakeStarter { OutliveError = 5 };
        Assert.ThrowsAny<InvalidOperationException>(() => JobHelperRun.Start(start with { EndWithPoolWatch = false }, failed));
        nint job2 = failed.Seen!.Value.jobs[0];
        try { Assert.Equal(["create", $"injob 101 {job2}", "resume 102", $"outlive {job2}", "terminate 101", "close 101", "close 102", $"endjob {job2}", $"close {job2}"], failed.Calls); }
        finally { JobNative.Close(job2); }
        // A start Windows refused: nothing was made but the job, which is ended and closed all the same.
        var refused = new FakeStarter { Created = false };
        Assert.ThrowsAny<InvalidOperationException>(() => JobHelperRun.Start(start, refused));
        nint job3 = refused.Seen!.Value.jobs[0];
        try { Assert.Equal(["create", $"endjob {job3}", $"close {job3}"], refused.Calls); }
        finally { JobNative.Close(job3); }
    }

    [Fact]
    public void TheRealEndAndTheRealSetGoToWindowsAsTheyAreGiven()
    {
        // No test ends a real process or changes the real setting, so this holds the lines of those calls: the run's End is
        // TerminateJobObject on its own job's handle, and Turn off's put-back passes the value itself to SystemParametersInfo
        // (SPI_SETFOREGROUNDLOCKTIMEOUT takes it in pvParam), for this sign-in only (SPIF_SENDCHANGE, never the registry).
        string job = UninstallerTests.OneLine(File.ReadAllText(Path.Combine(RepoDir(), "src", "PoolWatch", "Native", "JobNative.cs")));
        Assert.Contains("public void EndJob(nint job) => JobNative.Terminate(job);", job);
        Assert.Contains("public static bool Terminate(nint job) => TerminateJobObject(job, 1);", job);
        string run = UninstallerTests.OneLine(File.ReadAllText(Path.Combine(RepoDir(), "src", "PoolWatch", "Engine", "HelperRun.cs")));
        Assert.Contains("public void End() => _starter.EndJob(_job);", run);
        Assert.Contains("starter ??= JobNative.Win32.Instance;", run);
        string spi = UninstallerTests.OneLine(File.ReadAllText(Path.Combine(RepoDir(), "src", "PoolWatch", "Native", "TokenGuardNative.cs")));
        Assert.Contains("private const uint SPI_GETFOREGROUNDLOCKTIMEOUT = 0x2000, SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001, SPIF_SENDCHANGE = 0x2;", spi);
        Assert.Contains("public static bool SetForegroundLockTimeout(uint milliseconds) { SystemParametersInfoW(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, (nint)milliseconds, SPIF_SENDCHANGE); return GetForegroundLockTimeout() == milliseconds; }", spi);
        Assert.Contains("public static bool SetForegroundLockTimeoutZero() => SetForegroundLockTimeout(0);", spi);
    }

    // ---------------------------------------------------------------- turning the guard off

    private sealed class Live
    {
        public uint? Value = 200000;
        public readonly List<uint> SetTo = new();
        public bool Accept = true;
        public int Sets;
    }

    private const string SignIn = "1:133000000000000000";
    private AppPaths Paths() => new(Path.Combine(_root, "data"), null, secure: false);
    private Log NewLog() => new(Path.Combine(_root, "data", "g.log"));
    private string Record => Path.Combine(_root, "data", "cache", TokenGuardSetting.FileName);

    private TokenLeakGuard Guard(Live live, string? signIn = SignIn, Func<CancellationToken, (List<long> numbers, string? error)>? runTest = null,
        Action<PoolWatch.Core.Findings.Finding>? finding = null, TimeSpan? attemptGap = null) =>
        new(Paths(), NewLog(), finding ?? (_ => { }),
            setNow: () => { live.Sets++; if (!live.Accept) return false; live.Value = 0; return true; }, getLive: () => live.Value,
            attemptGap: attemptGap ?? TimeSpan.FromMilliseconds(1), clickRetryGap: TimeSpan.FromMilliseconds(1), build: () => "26220.7344",
            runTest: runTest ?? (_ => ([1, 0, 2, 100, 98, 101], null)), // this build leaks
            setTo: v => { live.SetTo.Add(v); if (!live.Accept) return false; live.Value = v; return true; }, signIn: () => signIn);

    /// <summary>What the uninstaller's step has: no guard runs, only the record in the guard's folder.</summary>
    private TokenGuardSetting CommandLine(Live live, string? signIn = SignIn) =>
        new(() => Path.Combine(TokenLeakGuard.GuardDirOf(Paths()), TokenGuardSetting.FileName), p => p, () => signIn, () => live.Value,
            v => { live.SetTo.Add(v); if (!live.Accept) return false; live.Value = v; return true; }, NewLog());

    private SettingsSource SettingsRows(bool guardOn, Func<string>? tokenGuardOff, TokenGuardSetting setting) =>
        new(new LiveSettings(Paths(), new PoolWatch.Core.Settings { TokenGuardEnabled = guardOn }, persist: false, NewLog()), new ChangeRecords(Paths(), NewLog()), tokenGuardOff, setting);

    [Fact]
    public async Task TurningTheGuardOffPutsBackTheSettingItChanged()
    {
        var live = new Live();
        var guard = Guard(live);
        await guard.RunAtLogon(null, CancellationToken.None);
        Assert.Equal(0u, live.Value); // the workaround is on
        Assert.True(File.Exists(Record)); // what it replaced, saved with this sign-in
        Assert.Equal(ChangeWords.TokenGuardOffPutBack, guard.TurnOff());
        Assert.Equal([200000u], live.SetTo); // back to what it was before PoolWatch changed it
        Assert.Equal(200000u, live.Value);
        Assert.False(guard.WorkaroundWanted);
        Assert.False(File.Exists(Record));
        Assert.Equal(ChangeWords.TokenGuardOff, guard.TurnOff()); // nothing left to put back
        Assert.Single(live.SetTo);
    }

    [Fact]
    public async Task TurningTheGuardOffAfterAPoolWatchRestartInTheSameSignInPutsTheSettingBack()
    {
        // An upgrade, Exit then start again, or a crash then a restart: the later PoolWatch finds the setting at 0 and changes
        // nothing at its start, but the value the earlier one replaced in this sign-in is in the guard's folder.
        var live = new Live();
        await Guard(live).RunAtLogon(null, CancellationToken.None);
        Assert.Equal(0u, live.Value);
        var later = Guard(live);
        await later.RunAtLogon(null, CancellationToken.None);
        Assert.Equal(GuardStep.AlreadyOn, later.Step);
        Assert.Equal(ChangeWords.TokenGuardOffPutBack, later.TurnOff());
        Assert.Equal([200000u], live.SetTo);
        Assert.Equal(200000u, live.Value);
        Assert.False(File.Exists(Record));
    }

    [Fact]
    public async Task WhatComesBackIsTheValueTheSignInHadBeforeTheGuard()
    {
        // The guard set 0 over 200000; another program then set 5000, and the recheck set 0 again over that. Turning the guard
        // off, in this PoolWatch or a later one, puts back 200000, the sign-in's own value, not the other program's.
        var live = new Live();
        var guard = Guard(live);
        await guard.RunAtLogon(null, CancellationToken.None);
        live.Value = 5000;
        guard.Recheck(TimeSpan.FromMinutes(3), () => null);
        Assert.Equal(0u, live.Value);
        var later = Guard(live);
        await later.RunAtLogon(null, CancellationToken.None);
        Assert.Equal(ChangeWords.TokenGuardOffPutBack, later.TurnOff());
        Assert.Equal([200000u], live.SetTo);
    }

    [Fact]
    public async Task TheUninstallersStepPutsBackTheSettingTheGuardChanged()
    {
        // The running PoolWatch set it; the uninstaller stopped PoolWatch and runs --uninstall-cleanup, a process of its own
        // started by the uninstaller in front. Its list says nothing stays, and turning the guard off puts the setting back.
        var live = new Live();
        await Guard(live).RunAtLogon(null, CancellationToken.None);
        var source = SettingsRows(guardOn: true, tokenGuardOff: null, CommandLine(live));
        var row = Assert.Single(source.List());
        Assert.Equal(ChangeIds.TokenGuard, row.Id);
        Assert.Null(row.PutBackCaveat);
        Assert.Contains("\"what\": \"The token-leak guard is on\"", ChangeLedger.ToJson([row]));
        Assert.DoesNotContain("putBackCaveat", ChangeLedger.ToJson([row]));
        Assert.Equal(new UndoResult(true, ChangeWords.TokenGuardOffPutBack), source.Undo(row, false));
        Assert.Equal(200000u, live.Value);
        Assert.False(File.Exists(Record));
    }

    [Fact]
    public async Task ASettingSavedInAnotherSignInIsNeverPutBack()
    {
        // Saved in an earlier sign-in. Windows set the live value again at this sign-in; another program has set it to 0
        // since. The record holds no value of this sign-in, so nothing is put back; the earlier sign-in's value stays its own.
        var live = new Live();
        await Guard(live, signIn: "1:100").RunAtLogon(null, CancellationToken.None);
        Assert.True(File.Exists(Record));
        live.Value = 0;
        var source = SettingsRows(guardOn: true, tokenGuardOff: null, CommandLine(live, signIn: "2:200"));
        var row = Assert.Single(source.List());
        Assert.Equal(ChangeWords.TokenGuardSettingStays, row.PutBackCaveat); // the uninstaller says it stays
        Assert.Equal(new UndoResult(true, ChangeWords.TokenGuardOffNotOurs), source.Undo(row, false));
        Assert.Empty(live.SetTo);
        Assert.Equal(0u, live.Value);
        Assert.Equal(200000u, CommandLine(live, signIn: "1:100").Replaced);
        Assert.Null(CommandLine(live, signIn: "2:200").Replaced);
        // Without a sign-in Windows will say, nothing is saved, and the value is kept in memory only.
        string before = File.ReadAllText(Record);
        var unsaid = new Live();
        var guard = Guard(unsaid, signIn: null);
        await guard.RunAtLogon(null, CancellationToken.None);
        Assert.Equal(before, File.ReadAllText(Record));
        Assert.Equal(ChangeWords.TokenGuardOffPutBack, guard.TurnOff());
    }

    [Fact]
    public async Task TurningTheGuardOffSaysWhenWindowsRefusedOrTheSettingWasNotTheGuards()
    {
        // Windows refuses: the value is kept, and the setting gets a row of its own whose Put back tries again.
        var live = new Live();
        var guard = Guard(live);
        await guard.RunAtLogon(null, CancellationToken.None);
        live.Accept = false;
        Assert.Equal(ChangeWords.TokenGuardOffRefused, guard.TurnOff());
        Assert.Equal(0u, live.Value);
        Assert.True(File.Exists(Record));
        var source = SettingsRows(guardOn: false, guard.TurnOff, guard.Setting);
        var row = Assert.Single(source.List());
        Assert.Equal((ChangeIds.TokenGuardSetting, ChangeKind.TokenGuard, ChangeGroup.Other), (row.Id, row.Kind, row.Group));
        Assert.Equal((ChangeWords.TokenGuardSettingStill, ChangeWords.TokenGuardSettingTitle, ChangeWords.TokenGuardSettingStillNote), (row.What, row.Title, row.Note));
        Assert.Equal(ChangeWords.PutBack, ActiveChange.ButtonOf(row));
        Assert.Null(row.UninstallLeaves); // the uninstaller puts it back
        Assert.Equal(new UndoResult(false, ChangeWords.TokenGuardOffRefused), source.Undo(row, false));
        live.Accept = true;
        Assert.Equal(new UndoResult(true, ChangeWords.TokenGuardOffPutBack), source.Undo(row, false));
        Assert.Equal(200000u, live.Value);
        Assert.Empty(source.List());

        // Windows does not report the live value: nothing is known to put back over, so it is kept for a later try, and the
        // sentence says so without promising a row in Active changes (one is listed only for a setting reported as 0).
        var unreported = new Live();
        var quiet = Guard(unreported, signIn: "3:300");
        await quiet.RunAtLogon(null, CancellationToken.None);
        unreported.Value = null;
        Assert.Equal(ChangeWords.TokenGuardOffUnreported, quiet.TurnOff());
        Assert.Empty(SettingsRows(guardOn: false, quiet.TurnOff, quiet.Setting).List()); // no row is promised: nothing is known to list
        Assert.Empty(unreported.SetTo);
        unreported.Value = 0;
        Assert.Equal(ChangeWords.TokenGuardOffPutBack, quiet.Setting.PutBack());
        Assert.Equal(200000u, unreported.Value);

        // Already 0 at this logon, with no record of the guard changing it (another program): left as it is.
        var already = new Live { Value = 0 };
        var other = Guard(already, signIn: "4:400");
        await other.RunAtLogon(null, CancellationToken.None);
        Assert.Equal(GuardStep.AlreadyOn, other.Step);
        Assert.Equal(ChangeWords.TokenGuardSettingStays, Assert.Single(SettingsRows(guardOn: true, other.TurnOff, other.Setting).List()).PutBackCaveat);
        Assert.Equal(ChangeWords.TokenGuardOffNotOurs, other.TurnOff());
        Assert.Empty(already.SetTo);

        // Something else set it back already: nothing of the guard's to put back.
        var reset = new Live();
        var third = Guard(reset, signIn: "5:500");
        await third.RunAtLogon(null, CancellationToken.None);
        reset.Value = 50000;
        Assert.Equal(ChangeWords.TokenGuardOff, third.TurnOff());
        Assert.Empty(reset.SetTo);
        Assert.Equal(50000u, reset.Value);
        Assert.False(File.Exists(Record));
    }

    [Fact]
    public async Task TheUninstallersStepIsNotDoneAndSaysWhyWhenTheSettingWasNotPutBack()
    {
        // The guard set the setting and recorded it. At the uninstall Windows refuses to put it back (or does not report it):
        // the step turns the guard off but is not done, and says, in words for an uninstall, that the setting stays until
        // sign-out. The summary the uninstaller shows names it as not put back; the record stays, so a PoolWatch installed
        // again in this sign-in can still put it back.
        var live = new Live();
        await Guard(live).RunAtLogon(null, CancellationToken.None);
        live.Accept = false;
        var source = SettingsRows(guardOn: true, tokenGuardOff: null, CommandLine(live));
        var row = Assert.Single(source.List());
        Assert.Null(row.PutBackCaveat); // expected to be put back, so the list says nothing stays
        var result = source.Undo(row, false);
        Assert.Equal(new UndoResult(false, ChangeWords.TokenGuardSettingNotPutBack), result);
        Assert.Equal(0u, live.Value);
        Assert.True(File.Exists(Record));
        Assert.DoesNotContain("Active changes", result.Message);
        string summary = Program.UninstallSummary([row], null, [(row, result)], new DateTime(2026, 11, 1, 12, 0, 0));
        Assert.Contains("Could not put back: The token-leak guard is on. " + ChangeWords.TokenGuardSettingNotPutBack, summary);
        Assert.DoesNotContain("Put back: The token-leak guard is on", summary);
        // The setting's own row, on the command line, and a setting Windows does not report: the same words.
        var settingRow = Assert.Single(SettingsRows(guardOn: false, tokenGuardOff: null, CommandLine(live)).List());
        Assert.Equal(ChangeIds.TokenGuardSetting, settingRow.Id);
        Assert.Equal(new UndoResult(false, ChangeWords.TokenGuardSettingNotPutBack), SettingsRows(guardOn: false, null, CommandLine(live)).Undo(settingRow, false));
        live.Value = null;
        Assert.Equal(new UndoResult(false, ChangeWords.TokenGuardSettingNotPutBack), SettingsRows(guardOn: true, null, CommandLine(live)).Undo(row, false));
        // Once Windows lets it: put back, and done.
        live.Value = 0;
        live.Accept = true;
        Assert.Equal(new UndoResult(true, ChangeWords.TokenGuardOffPutBack), SettingsRows(guardOn: true, null, CommandLine(live)).Undo(row, false));
        Assert.Equal(200000u, live.Value);
    }

    [Fact]
    public async Task TheSettingsOwnRowSaysItsButtonPutsTheSettingBackWithTheGuardAlreadyOff()
    {
        var live = new Live();
        var guard = Guard(live);
        await guard.RunAtLogon(null, CancellationToken.None);
        Assert.Equal(ChangeWords.TokenGuardTurnOffTip, PoolWatch.Ui.ActiveChangesView.ButtonTip(Assert.Single(SettingsRows(guardOn: true, guard.TurnOff, guard.Setting).List())));
        live.Accept = false;
        guard.TurnOff();
        var row = Assert.Single(SettingsRows(guardOn: false, guard.TurnOff, guard.Setting).List());
        Assert.Equal(ChangeIds.TokenGuardSetting, row.Id);
        Assert.Equal(ChangeWords.TokenGuardSettingPutBackTip, PoolWatch.Ui.ActiveChangesView.ButtonTip(row));
    }

    [Fact]
    public async Task TheOrphanedHelpersTurnOnRecordsTheValueItReplacedSoTurnOffPutsItBack()
    {
        // PoolWatch was killed during the logon test; the helper's click turned the workaround on. The value it replaced is
        // recorded with this sign-in, as the guard records it: a later PoolWatch finds the setting at 0, and turning the
        // guard off puts it back, and nothing says it stays until sign-out.
        var live = new Live();
        Assert.True(TokenLeakGuard.TurnOnOrphaned(() => live.Value, () => { live.Value = 0; return true; }, CommandLine(live)));
        Assert.Equal(0u, live.Value);
        var later = Guard(live);
        await later.RunAtLogon(null, CancellationToken.None);
        Assert.Equal(GuardStep.AlreadyOn, later.Step);
        Assert.Null(Assert.Single(SettingsRows(guardOn: true, later.TurnOff, later.Setting).List()).PutBackCaveat);
        Assert.Equal(ChangeWords.TokenGuardOffPutBack, later.TurnOff());
        Assert.Equal(200000u, live.Value);
        // Refused, or already 0: nothing is recorded.
        var refused = new Live();
        Assert.False(TokenLeakGuard.TurnOnOrphaned(() => refused.Value, () => false, CommandLine(refused, signIn: "9:900")));
        var zero = new Live { Value = 0 };
        Assert.True(TokenLeakGuard.TurnOnOrphaned(() => zero.Value, () => true, CommandLine(zero, signIn: "9:900")));
        Assert.Null(CommandLine(refused, signIn: "9:900").Replaced);
        // The helper's Turn on button does this with Windows' own calls (it needs a desktop, so this holds its line).
        string source = UninstallerTests.OneLine(File.ReadAllText(Path.Combine(RepoDir(), "src", "PoolWatch", "Engine", "TokenLeakGuard.cs")));
        Assert.Contains("on.Click += (_, _) => { try { TurnOnOrphaned(TokenGuardNative.GetForegroundLockTimeout, TokenGuardNative.SetForegroundLockTimeoutZero, " +
                        "TokenGuardSetting.ForCommandLine(paths, new Log(paths.LogFile, paths.Guard.CheckReal))); } catch { } f.Close(); };", source);
        Assert.Contains("try { PromptTurnOnOrphaned(paths); }", source);
    }

    [Fact]
    public async Task TwoSignInsAtOnceKeepTheirOwnValues()
    {
        // Fast user switching: the guard set the setting in one sign-in, then in a second one at the same time. Neither
        // reads or removes the other's value; each puts back its own, in a later PoolWatch too.
        var first = new Live();
        var second = new Live { Value = 150000 };
        await Guard(first, signIn: "1:111").RunAtLogon(null, CancellationToken.None);
        var other = Guard(second, signIn: "2:222");
        await other.RunAtLogon(null, CancellationToken.None);
        Assert.Equal(200000u, CommandLine(first, signIn: "1:111").Replaced);
        Assert.Equal(ChangeWords.TokenGuardOffPutBack, other.TurnOff());
        Assert.Equal(150000u, second.Value);
        var firstLater = Guard(first, signIn: "1:111");
        await firstLater.RunAtLogon(null, CancellationToken.None);
        Assert.Equal(ChangeWords.TokenGuardOffPutBack, firstLater.TurnOff());
        Assert.Equal(200000u, first.Value);
        Assert.False(File.Exists(Record)); // both put back: nothing left
        // At most MaxSignIns are kept, the one saved last among them.
        for (int i = 0; i < TokenGuardSetting.MaxSignIns + 3; i++)
            await Guard(new Live(), signIn: $"5:{i}").RunAtLogon(null, CancellationToken.None);
        using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Record)))
            Assert.Equal(TokenGuardSetting.MaxSignIns, doc.RootElement.GetProperty("signIns").GetArrayLength());
        Assert.Equal(200000u, CommandLine(new Live(), signIn: $"5:{TokenGuardSetting.MaxSignIns + 2}").Replaced);
    }

    [Fact]
    public void ARecordLargerThanItsBoundIsNotReadAndIsDeleted()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Record)!);
        string record = "{\"signIns\":[{\"signIn\":\"" + SignIn + "\",\"before\":200000,\"savedUtc\":\"2026-11-01T00:00:00.0000000Z\"}]}";
        File.WriteAllText(Record, record);
        Assert.Equal(200000u, CommandLine(new Live()).Replaced); // within the bound: read
        File.WriteAllText(Record, record + new string(' ', TokenGuardSetting.MaxLength));
        Assert.Null(CommandLine(new Live()).Replaced);
        Assert.False(File.Exists(Record));
        // Not in the form it writes: ignored, and the next save writes over it.
        File.WriteAllText(Record, "{\"before\":5,\"signIn\":\"" + SignIn + "\"}");
        Assert.Null(CommandLine(new Live()).Replaced);
        File.WriteAllText(Record, "not json");
        var setting = CommandLine(new Live());
        setting.Remember(300000);
        Assert.Equal(300000u, CommandLine(new Live()).Replaced);
    }

    /// <summary>The click prompt: <paramref name="onAsk"/> runs as it is shown; the owner clicks OK the first time, Cancel after.</summary>
    private sealed class Clicks(Action onAsk) : IGuardUi
    {
        public readonly List<string> Shown = new();
        public void ShowNotProtected(string message) { lock (Shown) Shown.Add(message); }
        public int Asks;
        public Task<bool> AskForClick(string message) { onAsk(); return Task.FromResult(Interlocked.Increment(ref Asks) == 1); }
        public void Withdraw(bool prompt) { }
    }

    [Fact]
    public async Task TurningTheGuardOffWhileItChecksTurnsNothingOn()
    {
        // Turned off during the logon test: the check saves what it found, but turns nothing on, and says so.
        var live = new Live();
        using var testing = new SemaphoreSlim(0);
        using var turnedOff = new SemaphoreSlim(0);
        var guard = Guard(live, runTest: _ => { testing.Release(); turnedOff.Wait(TimeSpan.FromSeconds(10)); return ([1, 0, 2, 100, 98, 101], null); });
        var check = Task.Run(() => guard.RunAtLogon(null, CancellationToken.None));
        Assert.True(await testing.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(ChangeWords.TokenGuardOff, guard.TurnOff());
        // The exit failsafe, during this check, leaves the setting as Turn off left it, and says nothing of a failure.
        guard.OnExit();
        turnedOff.Release();
        await check;
        Assert.Equal(0, live.Sets);
        Assert.Equal(200000u, live.Value);
        Assert.False(guard.WorkaroundWanted);
        Assert.Equal(GuardResult.Leak, guard.State!.Result);
        Assert.Equal(TokenLeakGuard.TurnedOffDuringCheck, guard.LastMessage);
        // Nor does a recheck, even when the workaround was wanted before; nor does it warn (it never asks for a click).
        guard.WorkaroundWanted = true;
        guard.Recheck(TimeSpan.FromMinutes(3), () => null);
        Assert.Equal(0, live.Sets);
        string log = File.ReadAllText(Path.Combine(_root, "data", "g.log"));
        Assert.DoesNotContain("PoolWatch exited during the check", log);
        Assert.DoesNotContain("Windows refused", log);
        Assert.Equal(TokenLeakGuard.TurnedOffDuringCheck, guard.LastMessage);

        // Turned off while its click prompt is open (Windows refused from the background): the click turns nothing on.
        var refused = new Live { Accept = false };
        TokenLeakGuard? prompted = null;
        int setsAtTurnOff = -1;
        var ui = new Clicks(() => { prompted!.TurnOff(); setsAtTurnOff = refused.Sets; refused.Accept = true; });
        prompted = Guard(refused, signIn: "6:600");
        await prompted.RunAtLogon(ui, CancellationToken.None);
        Assert.Equal(setsAtTurnOff, refused.Sets);
        Assert.Equal(1, ui.Asks); // and it does not ask again
        Assert.Equal(200000u, refused.Value);
        Assert.Equal(TokenLeakGuard.TurnedOffDuringCheck, prompted.LastMessage);
        Assert.False(prompted.WorkaroundWanted);
        // An older notice's "Turn on" changes nothing while the guard is off.
        Assert.False(await prompted.TurnOnByOwner(ui));
        Assert.Equal(TokenLeakGuard.IsOff, prompted.LastMessage);
        Assert.Equal(200000u, refused.Value);

        // Turned on again: it sets the workaround again.
        prompted.TurnedOn();
        Assert.True(await prompted.TurnOnByOwner(ui));
        Assert.Equal(0u, refused.Value);
    }

    private string Marker => Path.Combine(_root, "data", "cache", TokenLeakGuard.TestMarkerName);
    private string StateFile => Path.Combine(_root, "data", "cache", "token-guard.json");

    private void Saved(GuardResult result, string build = "26220.7344")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        File.WriteAllText(StateFile, new GuardState(build, result, DateTime.UtcNow, "earlier").ToJson());
    }

    [Fact]
    public async Task AMarkerFoundWithTheSettingAlreadyOnStillSavesTheBuildAsNotTestedToTheEnd()
    {
        // A logon test was cut short, and the orphaned helper's click (or a start whose outcome could not be saved) turned
        // the workaround on; PoolWatch then starts again in the same sign-in. It changes nothing (the setting is on), but it
        // saves the build as interrupted before it removes the marker, so the next sign-in, where Windows has set the
        // timeout again, turns the workaround on without testing.
        Saved(GuardResult.CleanOnce);
        File.WriteAllText(Marker, "{\"build\":\"26220.7344\",\"pid\":1}");
        var live = new Live { Value = 0 };
        int tests = 0;
        var guard = Guard(live, runTest: _ => { tests++; return ([1, 0, 2, 100, 98, 101], null); });
        await guard.RunAtLogon(null, CancellationToken.None);
        Assert.Equal(GuardStep.AlreadyOn, guard.Step);
        Assert.Equal(GuardResult.Interrupted, GuardState.Parse(File.ReadAllText(StateFile))!.Result);
        Assert.False(File.Exists(Marker));
        Assert.Equal(0, live.Sets);
        live.Value = 200000; // the next sign-in
        var next = Guard(live, runTest: _ => { tests++; return ([1, 0, 2, 100, 98, 101], null); });
        await next.RunAtLogon(null, CancellationToken.None);
        Assert.Equal(GuardStep.TurnOnInterrupted, next.Step);
        Assert.Equal(0, tests);
        Assert.Equal(0u, live.Value);
    }

    [Fact]
    public async Task AMarkerOfAnotherBuildIsNotThisBuildsAndOneThatCannotBeReadCounts()
    {
        // A test on the build before a Windows update was cut short: this build was never tested, so it is tested as any
        // new build is, and the marker goes once the outcome is saved.
        Saved(GuardResult.CleanOnce, build: "26220.7000");
        File.WriteAllText(Marker, "{\"build\":\"26220.7000\",\"pid\":1}");
        var live = new Live();
        int tests = 0;
        var guard = Guard(live, runTest: _ => { tests++; return ([1, 0, 2, 100, 98, 101], null); });
        await guard.RunAtLogon(null, CancellationToken.None);
        Assert.Equal(GuardStep.Test, guard.Step);
        Assert.Equal(1, tests);
        Assert.False(File.Exists(Marker));
        // A marker that cannot be read counts as this build's, to be safe.
        File.WriteAllText(Marker, "not json");
        var after = new Live();
        var again = Guard(after, signIn: "7:700", runTest: _ => { tests++; return ([0, 0, 1, 0, 1, 0], null); });
        await again.RunAtLogon(null, CancellationToken.None);
        Assert.Equal(GuardStep.TurnOnInterrupted, again.Step);
        Assert.Equal(1, tests);
        Assert.Equal(0u, after.Value);
        // One larger than any the guard writes is not read, and counts too, though it begins by naming another build.
        Saved(GuardResult.CleanOnce, build: "26220.7000");
        File.WriteAllText(Marker, "{\"build\":\"26220.7000\",\"pad\":\"" + new string('x', 5000) + "\"}");
        var large = new Live();
        var third = Guard(large, signIn: "8:800", runTest: _ => { tests++; return ([0, 0, 1, 0, 1, 0], null); });
        await third.RunAtLogon(null, CancellationToken.None);
        Assert.Equal(GuardStep.TurnOnInterrupted, third.Step);
        Assert.Equal(1, tests);
        Assert.Equal(0u, large.Value);
    }

    [Fact]
    public async Task ASetupCheckThatSavesItsVerdictAnswersATestOfThisBuildThatDidNotFinish()
    {
        // A logon test was cut short and its marker stayed, and the guard was off at the start after (it was turned off,
        // or PoolWatch was uninstalled and installed again, keeping its data), so no logon check took the marker up. The
        // setup guide's check then runs a whole test on this build and saves its verdict: that answers the marker, which
        // goes. Turned on afterwards, the guard decides from that verdict (clean once: it tests again) instead of turning
        // the workaround on without testing and saving the build as not tested to the end, which threw the check away.
        Directory.CreateDirectory(Path.GetDirectoryName(Marker)!); // no result saved yet
        File.WriteAllText(Marker, "{\"build\":\"26220.7344\",\"pid\":1}");
        // Its time says nothing: here a day ahead, as when the clock was set back since it was written. The check compares
        // the marker's bytes with those it found as it began, never a time.
        File.SetLastWriteTimeUtc(Marker, DateTime.UtcNow.AddDays(1));
        int tests = 0;
        var live = new Live();
        var setup = Guard(live, runTest: _ => { tests++; return ([0, 0, 1, 0, 1, 0], null); });
        Assert.Equal(LeakVerdict.Clean, await setup.TestOnly(CancellationToken.None));
        Assert.Equal(GuardResult.CleanOnce, GuardState.Parse(File.ReadAllText(StateFile))!.Result);
        Assert.False(File.Exists(Marker));
        var logon = Guard(live, runTest: _ => { tests++; return ([0, 0, 1, 0, 1, 0], null); });
        await logon.RunAtLogon(null, CancellationToken.None);
        Assert.Equal(GuardStep.Test, logon.Step);
        Assert.Equal(2, tests);
        Assert.Equal(GuardResult.Fixed, GuardState.Parse(File.ReadAllText(StateFile))!.Result); // clean twice in a row
    }

    [Theory]
    [InlineData("{\"build\":\"26220.7000\",\"pid\":1}", null)] // another build's, there as the check began: not this check's to answer
    [InlineData(null, "{\"build\":\"26220.7344\",\"pid\":1}")] // none as the check began; one written while it ran
    [InlineData("{\"build\":\"26220.7344\",\"pid\":1}", "{\"build\":\"26220.7344\",\"pid\":2}")] // one there, replaced while it ran
    public async Task ASetupCheckLeavesAMarkerItDoesNotAnswer(string? before, string? during)
    {
        // A marker the check does not answer stays, whatever time the file system gives it: each marker here, the one there
        // as the check began and the one written while it ran, is stamped ten minutes before the check began, as an old
        // file or a clock set back meanwhile would stamp it. No time is compared, so nothing here depends on timing.
        Directory.CreateDirectory(Path.GetDirectoryName(Marker)!); // no result saved yet
        var live = new Live();
        if (before is not null)
        {
            File.WriteAllText(Marker, before);
            File.SetLastWriteTimeUtc(Marker, DateTime.UtcNow.AddMinutes(-10));
        }
        var setup = Guard(live, runTest: _ =>
        {
            if (during is not null)
            {
                File.WriteAllText(Marker, during);
                File.SetLastWriteTimeUtc(Marker, DateTime.UtcNow.AddMinutes(-10));
            }
            return ([0, 0, 1, 0, 1, 0], null);
        });
        Assert.Equal(LeakVerdict.Clean, await setup.TestOnly(CancellationToken.None));
        Assert.Equal(GuardResult.CleanOnce, GuardState.Parse(File.ReadAllText(StateFile))!.Result); // the verdict was saved
        Assert.True(File.Exists(Marker));
        Assert.Equal(during ?? before, File.ReadAllText(Marker));
    }

    [Fact]
    public async Task ASetupCheckWhoseVerdictCannotBeSavedLeavesTheMarkerOfATestThatDidNotFinish()
    {
        // A logon test on this build was cut short. The setup guide's check then runs a whole test, but its verdict cannot
        // be saved (a folder is where its new file goes): nothing on disk answers the marker, so it stays, and the next
        // logon check turns the workaround on without testing.
        Directory.CreateDirectory(Path.GetDirectoryName(Marker)!); // no result saved yet
        File.WriteAllText(Marker, "{\"build\":\"26220.7344\",\"pid\":1}");
        Directory.CreateDirectory(StateFile + ".tmp");
        int tests = 0;
        var setup = Guard(new Live(), runTest: _ => { tests++; return ([0, 0, 1, 0, 1, 0], null); });
        Assert.Equal(LeakVerdict.Clean, await setup.TestOnly(CancellationToken.None));
        Assert.False(File.Exists(StateFile));
        Assert.True(File.Exists(Marker), "the marker went although the verdict was not saved");
        Directory.Delete(StateFile + ".tmp");
        var live = new Live();
        var logon = Guard(live, runTest: _ => { tests++; return ([0, 0, 1, 0, 1, 0], null); });
        await logon.RunAtLogon(null, CancellationToken.None);
        Assert.Equal(GuardStep.TurnOnInterrupted, logon.Step);
        Assert.Equal(1, tests);
        Assert.Equal(0u, live.Value);
    }

    [Fact]
    public async Task EveryMarkerALogonCheckWritesIsItsOwn()
    {
        // Two logon tests cut short write two markers: each carries a run id of its own, so the bytes differ even when the
        // build, the start time and the process are the same, and a setup check never takes one for the other.
        var runs = new List<string>();
        for (int i = 0; i < 2; i++)
        {
            var guard = Guard(new Live(), runTest: _ => { runs.Add(File.ReadAllText(Marker)); throw new InvalidOperationException("cut short"); });
            await guard.RunAtLogon(null, CancellationToken.None);
            Assert.True(File.Exists(Marker)); // the outcome was not saved, so it stays
            File.Delete(Marker); // as a setup check that answered it would: the next logon check tests again
        }
        Assert.Equal(2, runs.Count);
        var ids = runs.Select(r => System.Text.Json.JsonDocument.Parse(r).RootElement.GetProperty("run").GetString()).ToList();
        Assert.All(ids, id => Assert.Matches("^[0-9a-f]{32}$", id));
        Assert.NotEqual(ids[0], ids[1]);
        Assert.All(runs, r => Assert.Contains("\"build\":\"26220.7344\"", r));
    }

    [Fact]
    public async Task ACheckThatFailsAfterItWasTurnedOffSaysSoAndLeavesNoMarkerOnceItsOutcomeIsSaved()
    {
        // Turned off during the test; the outcome is saved; then its finding fails (the timeline is full). The error path
        // turns nothing on, says it was turned off while it checked, and removes the marker: protection is not wanted, and
        // the saved outcome is what the next start decides from.
        var live = new Live();
        TokenLeakGuard? guard = null;
        int said = 0;
        guard = Guard(live, runTest: _ => { guard!.TurnOff(); return ([1, 0, 2, 100, 98, 101], null); },
            finding: _ => { if (Interlocked.Increment(ref said) == 1) throw new InvalidOperationException("the timeline is full"); });
        await guard.RunAtLogon(null, CancellationToken.None);
        Assert.Equal(TokenLeakGuard.TurnedOffDuringCheck, guard.LastMessage);
        Assert.Equal(0, live.Sets);
        Assert.Equal(200000u, live.Value);
        Assert.False(guard.WorkaroundWanted);
        Assert.Equal(GuardResult.Leak, GuardState.Parse(File.ReadAllText(StateFile))!.Result);
        Assert.False(File.Exists(Marker));
        Assert.Contains("token guard failed", File.ReadAllText(Path.Combine(_root, "data", "g.log")));
    }

    [Fact]
    public async Task TurnedOffTurnOnStopsAtOnceAndNeverLeavesTheWorkaroundWanted()
    {
        // Turned off: an older notice's Turn on says so (in the log and the findings) at once, before any of its tries 5 s
        // apart (here ten minutes apart).
        var live = new Live();
        var guard = Guard(live, attemptGap: TimeSpan.FromMinutes(10));
        guard.TurnOff();
        var turnOn = guard.TurnOnByOwner(new Clicks(() => { }));
        Assert.Same(turnOn, await Task.WhenAny(turnOn, Task.Delay(TimeSpan.FromSeconds(30))));
        Assert.False(await turnOn);
        Assert.Equal(TokenLeakGuard.IsOff, guard.LastMessage);
        Assert.Equal(0, live.Sets);

        // Turned off in the moment the set is accepted: "wanted" is decided under the same lock as Turn off, so it stays false.
        var racing = new Live();
        TokenLeakGuard? raced = null;
        raced = new TokenLeakGuard(Paths(), NewLog(), _ => { },
            setNow: () => { raced!.TurnOff(); racing.Value = 0; return true; }, getLive: () => racing.Value,
            attemptGap: TimeSpan.FromMilliseconds(1), clickRetryGap: TimeSpan.FromMilliseconds(1), signIn: () => "8:800");
        Assert.True(await raced.TurnOnByOwner(new Clicks(() => { })));
        Assert.False(raced.WorkaroundWanted);
    }

    [Fact]
    public async Task ARecheckRefusedAsksForNoClickAndOnceTurnedOffTurnsNothingOn()
    {
        // Something set the setting back; the recheck's silent set is refused from the background. It asks for no click (a
        // click here would turn the guard off) and shows no notice. The owner then turns the guard off: the next recheck
        // sets nothing and says nothing, and nothing says the logon is not protected: the guard is off, as the owner chose.
        var live = new Live();
        var guard = Guard(live);
        await guard.RunAtLogon(null, CancellationToken.None);
        live.Value = 20000;
        live.Accept = false;
        var ui = new Clicks(() => guard.TurnOff());
        int sets = live.Sets;
        guard.Recheck(TimeSpan.Zero, () => ui);
        Assert.Equal(sets + 1, live.Sets);
        Assert.Equal(0, ui.Asks);
        lock (ui.Shown) Assert.Empty(ui.Shown);
        Assert.False(guard.Off);
        Assert.Equal(20000u, live.Value);
        Assert.Contains("Windows refused to turn the workaround on again from the background", guard.LastMessage);
        guard.TurnOff();
        string said = guard.LastMessage;
        live.Value = 20000;
        sets = live.Sets;
        guard.Recheck(TimeSpan.Zero, () => ui);
        Assert.Equal(sets, live.Sets);
        Assert.Equal(0, ui.Asks);
        lock (ui.Shown) Assert.Empty(ui.Shown);
        Assert.Equal(20000u, live.Value);
        Assert.False(guard.WorkaroundWanted);
        Assert.False(guard.NotProtectedNow());
        Assert.Equal(said, guard.LastMessage);
    }

    [Fact]
    public void TurningTheGuardOnLetsItSetTheSettingAgain()
    {
        // Diagnostics.StartTokenGuard turns it on before it starts the logon check or hands back the one under way, which
        // needs the running app to be built, so this holds that line.
        string diagnostics = UninstallerTests.OneLine(File.ReadAllText(Path.Combine(RepoDir(), "src", "PoolWatch", "Analysis", "Diagnostics.cs")));
        Assert.Contains("Guard.TurnedOn(); lock (_guardStartLock) { if (_guardTask is not { IsCompleted: false }) _guardTask = Task.Run(() => Guard.RunAtLogon(ui, _guardStop.Token));", diagnostics);
        // Anchored to a line of its own, so a commented-out call does not match.
        Assert.Matches(new Regex(@"(?m)^[ \t]*Guard\.TurnedOn\(\);\r?$"), File.ReadAllText(Path.Combine(RepoDir(), "src", "PoolWatch", "Analysis", "Diagnostics.cs")));
    }

    [Fact]
    public void TheSignInIsTheSessionAndTheTimeItsUserSignedIn()
    {
        // WTSINFOW as wtsapi32.h lays it out on x64 (WINSTATIONNAME_LENGTH 32, DOMAIN_LENGTH 17, USERNAME_LENGTH 20).
        Assert.Equal(216, Marshal.SizeOf<TokenGuardNative.WTSINFOW>());
        Assert.Equal(200, (int)Marshal.OffsetOf<TokenGuardNative.WTSINFOW>(nameof(TokenGuardNative.WTSINFOW.LogonTime)));
        Assert.Equal(4, (int)Marshal.OffsetOf<TokenGuardNative.WTSINFOW>(nameof(TokenGuardNative.WTSINFOW.SessionId)));
        // Read twice, the same: this process's session, and when its user signed in (a session with no user says nothing).
        string? id = TokenGuardNative.SignInId();
        if (id is null) return;
        Assert.Matches("^[0-9]+:[0-9]+$", id);
        Assert.Equal(System.Diagnostics.Process.GetCurrentProcess().SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture), id.Split(':')[0]);
        Assert.Equal(id, TokenGuardNative.SignInId());
    }

    [Fact]
    public void TurningTheGuardOffInActiveChangesSettingsOrTheUninstallerPutsTheSettingBack()
    {
        // The places where the guard is turned off call the guard's TurnOff, or on the command line the record's PutBack
        // (tested above), and show what it says. They need the running app to be built, so this holds their lines.
        string ledger = UninstallerTests.OneLine(File.ReadAllText(Path.Combine(RepoDir(), "src", "PoolWatch", "Engine", "Changes", "ChangeLedger.cs")));
        Assert.Contains("new SettingsSource(live, records, d.Guard.TurnOff, d.Guard.Setting),", ledger);
        Assert.Contains("new SettingsSource(live, records, tokenGuardOff: null, TokenGuardSetting.ForCommandLine(paths, log)),", ledger);
        string setting = UninstallerTests.OneLine(File.ReadAllText(Path.Combine(RepoDir(), "src", "PoolWatch", "Engine", "TokenGuardSetting.cs")));
        Assert.Contains("new(() => Path.Combine(TokenLeakGuard.GuardDirOf(paths), FileName), paths.Guard.CheckReal, TokenGuardNative.SignInId, TokenGuardNative.GetForegroundLockTimeout, TokenGuardNative.SetForegroundLockTimeout, log);", setting);
        string editor = UninstallerTests.OneLine(File.ReadAllText(Path.Combine(RepoDir(), "src", "PoolWatch", "Ui", "SettingsEditor.cs")));
        Assert.Contains("else if (apply.TurnsGuardOff(running)) guardOff = _analysis.Diagnostics.Guard.TurnOff();", editor);
        Assert.Contains("foreach (string? more in new[] { guardOff, clearStaysOff }) if (more is not null) said += \"\\n\\n\" + more; MessageBox.Show(owner, said, \"PoolWatch\");", editor);
    }

    [Fact]
    public void WhatALogonOnABuildWhoseCheckDidNotFinishSaysTellsHowToCheckAgain()
    {
        // What the logon says once the workaround is on (in the log and the findings, with no notice, as no click is
        // needed) names the tray menu's own item. With the guard off (its setting put back), the setup guide checks this
        // build again: a test that did not finish is no answer for it (TokenLeakRule), so setup runs its check.
        Assert.Contains($"choose {PoolWatch.Ui.FirstRunGate.MenuItem.TrimEnd('.')} in the tray menu", GuardPolicy.InterruptedMessage);
        // The setting must be back for the check to run (TokenLeakRule says WorkedAroundAlready while it is 0): when Windows did
        // not let Turn off put it back, its own row in Active changes tries again, and the logon says so.
        Assert.Contains("turn the guard off (if Active changes then still lists its Windows setting, put that back too)", GuardPolicy.InterruptedMessage);
        Assert.Equal(PoolWatch.Core.Setup.TokenLeakStatus.WorkedAroundAlready, PoolWatch.Core.Setup.TokenLeakRule.Judge("26220.7344", new GuardState("26220.7344", GuardResult.Interrupted, DateTime.UtcNow, "x"), 0));
        var interrupted = new GuardState("26220.7344", GuardResult.Interrupted, DateTime.UtcNow, "a leak test on this build did not finish");
        Assert.Equal(PoolWatch.Core.Setup.TokenLeakStatus.NeedsTest, PoolWatch.Core.Setup.TokenLeakRule.Judge("26220.7344", interrupted, 200000));
        Assert.Equal(GuardPolicy.InterruptedMessage, GuardPolicy.Without(GuardStep.TurnOnInterrupted, interrupted, "26220.7344", DateTime.UtcNow).Message);
    }
}
