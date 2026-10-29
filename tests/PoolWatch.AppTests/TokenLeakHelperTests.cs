using System.Diagnostics;
using System.Text.RegularExpressions;
using PoolWatch.Core.Findings;
using PoolWatch.Core.TokenGuard;
using PoolWatch.Engine;
using PoolWatch.Native;

namespace PoolWatch.AppTests;

/// <summary>
/// How the token-leak guard holds its hidden helper. The setup guide's check (which opens on a first start, also with
/// --tray) runs it in a job that ends it with PoolWatch: when PoolWatch exits, when the check is stopped, and, through the
/// job, when PoolWatch is killed. Its result is thrown away then, and the helper no longer keeps PoolWatch.exe in use after
/// PoolWatch has gone, which an uninstall or an upgrade had to wait for. When Windows refuses the job, the helper runs as
/// it did before there was one: ended only at the guard's timeout. The logon check's helper is left as it was: orphaned,
/// it offers to protect the logon. The helper runs here are fakes: no test starts or ends a process.
/// </summary>
public sealed class TokenLeakHelperTests : IDisposable
{
    private const string Build = "26220.7344";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pw-helper-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly Log _log;
    private readonly List<Finding> _findings = new();
    private readonly List<FakeRun> _runs = new();
    private int _sets;

    public TokenLeakHelperTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        _paths = new AppPaths(Path.Combine(_root, "data"), null, secure: false);
        _log = new Log(Path.Combine(_root, "data", "guard.log"));
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>
    /// A helper run that finishes at once with the result the real helper writes, or (<c>finishes</c> false) one that runs
    /// until it is ended, for at most 10 seconds. A run that asked for the job (<c>endWithPoolWatch</c>) is in one unless
    /// Windows refused it (<c>jobRefused</c>).
    /// </summary>
    private sealed class FakeRun(ProcessStartInfo psi, bool endWithPoolWatch, bool finishes, bool jobRefused) : IHelperRun
    {
        public string Arguments { get; } = psi.Arguments;
        public bool EndWithPoolWatch { get; } = endWithPoolWatch;
        public ManualResetEventSlim Waiting { get; } = new();
        public ManualResetEventSlim Ended { get; } = new();

        public bool WaitForExit(int milliseconds)
        {
            Waiting.Set();
            if (!finishes) Ended.Wait(TimeSpan.FromSeconds(10));
            return true;
        }

        public int ExitCode => Ended.IsSet ? 1 : 0;
        public bool InJob { get; } = endWithPoolWatch && !jobRefused;
        public void End() => Ended.Set();
        public void Dispose() { }
    }

    private TokenLeakGuard Guard(bool finishes, bool jobRefused = false, Action<Action>? hookExit = null) => new(_paths, _log,
        f => { lock (_findings) _findings.Add(f); },
        setNow: () => { Interlocked.Increment(ref _sets); return true; }, getLive: () => 20000,
        attemptGap: TimeSpan.FromMilliseconds(1), clickRetryGap: TimeSpan.FromMilliseconds(1), build: () => Build,
        startHelper: (psi, end) => Start(psi, end, finishes, jobRefused), hookExit: hookExit);

    private FakeRun Start(ProcessStartInfo psi, bool endWithPoolWatch, bool finishes, bool jobRefused)
    {
        if (finishes)
        {
            // What the real helper writes: its three differences, to the file named on its command line.
            string file = Regex.Match(psi.Arguments, "--token-leak-test \\d+ \"([^\"]+)\"").Groups[1].Value;
            File.WriteAllText(file, "{\"deltas\":[0,0,1]}");
        }
        var run = new FakeRun(psi, endWithPoolWatch, finishes, jobRefused);
        lock (_runs) _runs.Add(run);
        return run;
    }

    /// <summary>The first helper run, once the guard waits for it.</summary>
    private FakeRun Running()
    {
        Assert.True(SpinWait.SpinUntil(() => { lock (_runs) return _runs.Count > 0 && _runs[0].Waiting.IsSet; }, TimeSpan.FromSeconds(10)),
            "no helper run started");
        lock (_runs) return _runs[0];
    }

    private string LogText()
    {
        string file = Path.Combine(_root, "data", "guard.log");
        return File.Exists(file) ? File.ReadAllText(file) : "";
    }

    private static string RepoDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PoolWatch.slnx"))) return dir.FullName;
        throw new InvalidOperationException("PoolWatch.slnx not found above " + AppContext.BaseDirectory);
    }

    /// <summary>A text with every run of white space made one space, and none at either end.</summary>
    private static string OneLine(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    [Fact]
    public async Task OnlyTheGuidesCheckRunsItsHelperSoThatItEndsWithPoolWatch()
    {
        var guard = Guard(finishes: true);
        Assert.NotNull(await guard.TestOnly(CancellationToken.None));
        await guard.RunAtLogon(null, CancellationToken.None); // clean once on this build: the logon check tests again
        Assert.Equal(4, _runs.Count);
        Assert.All(_runs.Take(2), r => Assert.True(r.EndWithPoolWatch));
        Assert.All(_runs.Skip(2), r => Assert.False(r.EndWithPoolWatch));
        // The helper is asked to measure the same either way; only the logon check's helper watches PoolWatch.
        Assert.All(_runs, r => Assert.Contains($"--token-leak-test {GuardPolicy.Children} ", r.Arguments));
        Assert.All(_runs.Take(2), r => Assert.DoesNotContain("--token-test-parent", r.Arguments));
        Assert.All(_runs.Skip(2), r => Assert.Contains($"--token-test-parent {Environment.ProcessId}", r.Arguments));
        // Each of the check's two runs says it got the job; the logon check's do not.
        Assert.Equal(2, Regex.Matches(LogText(), "token guard: the check's helper runs in a job, so it ends with PoolWatch").Count);
    }

    [Fact]
    public async Task PoolWatchExitingEndsTheGuidesCheckHelperAndSavesNothing()
    {
        var guard = Guard(finishes: false);
        var check = guard.TestOnly(CancellationToken.None);
        var run = Running();
        guard.OnExit(); // the clean exit (Diagnostics.StopTokenGuard) and the exit failsafe (the next test) both call it
        Assert.True(run.Ended.IsSet, "the guide's check helper was not ended when PoolWatch exited");
        Assert.Null(await check); // its result is thrown away
        Assert.Single(_runs); // and no second run starts
        Assert.Null(guard.ReadState());
        Assert.Empty(_findings);
        Assert.Equal(0, _sets); // the workaround is left as it was
    }

    [Fact]
    public async Task TheExitFailsafeIsHookedWhenTheGuardIsMadeWhateverTheGuardSetting()
    {
        // The app's guard hooks it as it is made (Diagnostics makes the guard whatever the setting), before any check runs.
        // Its handler runs only at this test host's exit, and then finds nothing running: it does nothing.
        Assert.True(new TokenLeakGuard(_paths, _log, _ => { }).ExitHooked);

        // What the hook calls: with nothing running, nothing happens.
        Action? idle = null;
        Guard(finishes: false, hookExit: a => idle = a);
        Assert.NotNull(idle);
        idle!();
        Assert.Empty(_runs);
        Assert.Equal(0, _sets);

        // During the setup guide's check (the guard off, so no logon check ever started), the process's exit or an
        // unhandled exception ends the check's helper, and nothing is saved.
        Action? onExit = null;
        var guard = Guard(finishes: false, hookExit: a => onExit = a);
        Assert.NotNull(onExit);
        var check = guard.TestOnly(CancellationToken.None);
        var run = Running();
        onExit!();
        Assert.True(run.Ended.IsSet, "the exit failsafe did not end the guide's check helper");
        Assert.Null(await check);
        Assert.Null(guard.ReadState());
        Assert.Empty(_findings);
        Assert.Equal(0, _sets); // the workaround is left as it was
    }

    [Fact]
    public void HookProcessExitAddsTheFailsafeToTheProcessExitAndToUnhandledExceptions()
    {
        // No test can raise this process's exit or an unhandled exception, so this holds HookProcessExit's whole text, with
        // every run of white space made one space: its two += lines and nothing else. Any edit to it fails this test, except
        // one that only changes white space in that way. That the handlers run at a real exit is not tested. The app's guard
        // passes it as its hook.
        string source = File.ReadAllText(Path.Combine(RepoDir(), "src", "PoolWatch", "Engine", "TokenLeakGuard.cs"));
        Assert.Contains("hookExit: HookProcessExit) { }", OneLine(source));
        var hook = Regex.Match(source, @"(?ms)^    private static void HookProcessExit\(.*?^    \}");
        Assert.True(hook.Success, "HookProcessExit is not in TokenLeakGuard.cs");
        Assert.Equal(OneLine("""
            private static void HookProcessExit(Action onExit)
            {
                AppDomain.CurrentDomain.UnhandledException += (_, _) => onExit();
                AppDomain.CurrentDomain.ProcessExit += (_, _) => onExit();
            }
            """), OneLine(hook.Value));
    }

    [Fact]
    public async Task ACheckHelperStartedAsPoolWatchExitsIsEndedAtOnce()
    {
        var guard = Guard(finishes: false);
        guard.OnExit();
        Assert.Null(await guard.TestOnly(CancellationToken.None));
        var run = Assert.Single(_runs);
        Assert.True(run.Ended.IsSet, "a check helper started after PoolWatch began to exit ran on");
    }

    [Fact]
    public async Task StoppingTheGuidesCheckEndsItsHelper()
    {
        using var stop = new CancellationTokenSource();
        var guard = Guard(finishes: false);
        var check = guard.TestOnly(stop.Token);
        var run = Running();
        stop.Cancel(); // the guide was closed during the check
        Assert.True(run.Ended.IsSet, "the guide's check helper was not ended when the check was stopped");
        Assert.Null(await check);
        Assert.Null(guard.ReadState());
    }

    [Fact]
    public async Task WithoutAJobTheChecksHelperRunsOnAtStopAndExit()
    {
        // Windows refused the job: the helper runs as a plain process, as it did before there was a job. Neither stopping
        // the check nor PoolWatch's exit ends it (ending it would find its children by parent process ID). In the code
        // only the guard's timeout ends it, as before; this test does not reach the timeout.
        using var stop = new CancellationTokenSource();
        var guard = Guard(finishes: false, jobRefused: true);
        var check = guard.TestOnly(stop.Token);
        var run = Running();
        Assert.True(run.EndWithPoolWatch); // it asked for the job
        Assert.False(run.InJob); // and did not get it
        stop.Cancel();
        guard.OnExit();
        Assert.False(run.Ended.IsSet, "a check helper without a job was ended on stop or exit");
        Assert.DoesNotContain("runs in a job", LogText());
        run.End(); // it finishes by itself
        Assert.Null(await check); // and its result is thrown away, as after any stop
        Assert.Single(_runs);
        Assert.Null(guard.ReadState());
    }

    [Fact]
    public async Task ALogonCheckQueuedBehindTheGuidesCheckDoesNotStartWhilePoolWatchExits()
    {
        var guard = Guard(finishes: false);
        var check = guard.TestOnly(CancellationToken.None);
        var run = Running();
        // The guard turned on during the guide's check: its logon check waits for that check to finish.
        var logon = Task.Run(() => guard.RunAtLogon(null, CancellationToken.None));
        Assert.NotSame(logon, await Task.WhenAny(logon, Task.Delay(200)));
        guard.OnExit(); // ends the check's helper, so the check finishes now and lets the logon check go
        Assert.True(run.Ended.IsSet);
        Assert.Null(await check);
        Assert.Same(logon, await Task.WhenAny(logon, Task.Delay(TimeSpan.FromSeconds(5))));
        // It started nothing while PoolWatch exits: no helper, no test marker, no change, no notice.
        Assert.Single(_runs);
        Assert.False(File.Exists(Path.Combine(_paths.CacheDir, TokenLeakGuard.TestMarkerName)), "the logon check wrote its test marker");
        Assert.Equal(0, _sets);
        Assert.Empty(_findings);
        Assert.False(guard.Checking);
    }

    [Fact]
    public async Task TheLogonChecksHelperRunsOnWhenPoolWatchExits()
    {
        var guard = Guard(finishes: false);
        var logon = Task.Run(() => guard.RunAtLogon(null, CancellationToken.None));
        var run = Running();
        Assert.False(run.EndWithPoolWatch);
        guard.OnExit();
        Assert.False(run.Ended.IsSet); // orphaned, it offers to protect the logon
        Assert.True(_sets >= 1); // and the exit failsafe turned the workaround on, as before
        run.End();
        await logon;
    }

    [Fact]
    public void OnlyTheGuidesCheckHelperIsStartedInAJob()
    {
        // A file that is not there: nothing starts either way, and the log says which way was tried.
        var guard = Guard(finishes: true);
        var missing = new ProcessStartInfo(Path.Combine(_root, "missing", "no-such-helper.exe"), "")
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _root };
        Assert.ThrowsAny<Exception>(() => guard.StartHelper(missing, wantJob: false));
        Assert.DoesNotContain("without a job", LogText());
        Assert.ThrowsAny<Exception>(() => guard.StartHelper(missing, wantJob: true));
        Assert.Contains("token guard: the check's helper runs without a job, so it may outlive PoolWatch: CreateProcess failed: ", LogText());
    }

    [Fact]
    public void TheJobIsSetToEndItsProcessesWhenItsLastHandleCloses()
    {
        // A new, empty job: no process is put in it or ended. Windows holds its kill-on-close limit flag.
        nint job = JobNative.CreateKillOnClose();
        try
        {
            Assert.Equal(JobNative.KillOnJobClose, JobNative.LimitFlags(job) & JobNative.KillOnJobClose);
            Assert.True(JobNative.Terminate(job));
        }
        finally { JobNative.Close(job); }
    }
}
