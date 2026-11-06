using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using PoolWatch.Core.Findings;
using PoolWatch.Core.TokenGuard;
using PoolWatch.Engine;
using PoolWatch.Native;

namespace PoolWatch.AppTests;

/// <summary>
/// How the token-leak guard holds its hidden helper. Every helper runs in a job of its own. The setup guide's check (which
/// opens on a first start, also with --tray) runs it in a job that ends it with PoolWatch: when PoolWatch exits, when the
/// check is stopped, and, through the job, when PoolWatch is killed. Its result is thrown away then, and the helper no
/// longer keeps PoolWatch.exe in use after PoolWatch has gone. The logon check's job lets its helper run on: orphaned, it
/// offers to protect the logon. A helper Windows will not start in a job is not started, and the run is unclear. Each run
/// writes its result to a new file of its own in the data root's cache folder, which the guard reads once and deletes; a
/// result that is not exactly what the helper writes is not used. The helper runs here are fakes: no test starts or ends a
/// process.
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
    /// A helper run that finishes at once, having written what the real helper writes (<see cref="TokenLeakGuard.WriteResult"/>
    /// into the file named on its command line, with exit code 0 when it could), or (<c>finishes</c> false) one that runs
    /// until it is ended, for at most 10 seconds.
    /// </summary>
    private sealed class FakeRun(HelperStart start, bool finishes, int exitCode) : IHelperRun
    {
        public HelperStart Start { get; } = start;
        public string Line => Start.Environment[HelperCommand.LineVariable];
        public bool EndWithPoolWatch => Start.EndWithPoolWatch;
        public ManualResetEventSlim Waiting { get; } = new();
        public ManualResetEventSlim Ended { get; } = new();

        public bool WaitForExit(int milliseconds)
        {
            Waiting.Set();
            if (!finishes) Ended.Wait(TimeSpan.FromSeconds(10));
            return true;
        }

        public int ExitCode => Ended.IsSet ? 1 : exitCode;
        public void End() => Ended.Set();
        public void Dispose() { }
    }

    /// <summary>The result file named on a helper's command line (its fourth argument).</summary>
    internal static string ResultFile(HelperStart start) => Argv(start.Environment[HelperCommand.LineVariable])[3];

    /// <summary>What the real helper's result looks like, for <paramref name="deltas"/> (n = 100).</summary>
    private static string Result(params long[] deltas) =>
        System.Text.Json.JsonSerializer.Serialize(new { deltas, n = GuardPolicy.Children, console = false });

    private TokenLeakGuard Guard(bool finishes, Func<HelperStart, string?>? write = null, Action<Action>? hookExit = null,
        Func<HelperStart, IHelperRun>? startHelper = null) => new(_paths, _log,
        f => { lock (_findings) _findings.Add(f); },
        setNow: () => { Interlocked.Increment(ref _sets); return true; }, getLive: () => 20000,
        attemptGap: TimeSpan.FromMilliseconds(1), clickRetryGap: TimeSpan.FromMilliseconds(1), build: () => Build,
        startHelper: startHelper ?? (start => Start(start, finishes, write ?? (_ => Result(0, 0, 1)))), hookExit: hookExit);

    private FakeRun Start(HelperStart start, bool finishes, Func<HelperStart, string?> write)
    {
        int exitCode = 0;
        if (finishes && write(start) is { } json)
            exitCode = TokenLeakGuard.WriteResult(ResultFile(start), json) ? 0 : ExitCodes.ResultNotWritten;
        var run = new FakeRun(start, finishes, exitCode);
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

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern nint CommandLineToArgvW(string line, out int count);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);

    /// <summary>How the helper's command-line parser splits <paramref name="line"/> (CommandLineToArgvW), argument 0 included.</summary>
    internal static string[] Argv(string line)
    {
        nint argv = CommandLineToArgvW(line, out int count);
        Assert.NotEqual(0, argv);
        try { return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * nint.Size))!).ToArray(); }
        finally { LocalFree(argv); }
    }

    private string GuardCache => Path.Combine(_paths.DataRoot, "cache");

    [Fact]
    public async Task OnlyTheGuidesCheckRunsItsHelperSoThatItEndsWithPoolWatch()
    {
        var guard = Guard(finishes: true);
        Assert.NotNull(await guard.TestOnly(CancellationToken.None));
        await guard.RunAtLogon(null, CancellationToken.None); // clean once on this build: the logon check tests again
        Assert.Equal(4, _runs.Count);
        Assert.All(_runs.Take(2), r => Assert.True(r.EndWithPoolWatch));
        Assert.All(_runs.Skip(2), r => Assert.False(r.EndWithPoolWatch));
        // The helper is asked to measure the same either way; only the logon check's helper watches PoolWatch, which it
        // knows by its process ID and creation time.
        Assert.All(_runs, r => Assert.Equal(new[] { "--token-leak-test", $"{GuardPolicy.Children}" }, Argv(r.Line)[1..3]));
        Assert.All(_runs.Take(2), r => Assert.Equal(4, Argv(r.Line).Length));
        Assert.All(_runs.Skip(2), r => Assert.Equal(new[] { "--token-test-parent", $"{Environment.ProcessId}", $"{TokenGuardNative.OwnCreationTime()}" }, Argv(r.Line)[4..]));
        // Each of the check's two runs says it got the job (the installer test looks for this line); the logon check's say
        // theirs, which does not match it.
        Assert.Equal(2, Regex.Matches(LogText(), "token guard: the check's helper runs in a job, so it ends with PoolWatch").Count);
        Assert.Equal(2, Regex.Matches(LogText(), "token guard: the logon check's helper is in a job that only PoolWatch ends").Count);
    }

    [Fact]
    public async Task EachRunWritesANewFileOfItsOwnThatIsReadOnceAndDeleted()
    {
        var guard = Guard(finishes: true);
        Assert.Equal(LeakVerdict.Clean, await guard.TestOnly(CancellationToken.None));
        await guard.RunAtLogon(null, CancellationToken.None);
        var files = _runs.Select(r => ResultFile(r.Start)).ToList();
        Assert.Equal(4, files.Count);
        Assert.Equal(4, files.Distinct(StringComparer.OrdinalIgnoreCase).Count()); // a name of its own for every run
        Assert.All(files, f =>
        {
            Assert.Equal(GuardCache, Path.GetDirectoryName(f));
            Assert.Matches("^token-test-[0-9a-f]{32}\\.json$", Path.GetFileName(f));
            Assert.False(File.Exists(f), "a result was left behind after it was read");
        });
        Assert.Empty(Directory.GetFiles(GuardCache, "token-test-*.json"));
    }

    [Fact]
    public async Task AnOldResultHeldOpenCannotBeReadAsThisRunsResult()
    {
        // The files an older PoolWatch left in the cache, holding a clean result, held open for reading by a program of the
        // signed-in user (who may read the data folder). This build leaks; the helper finds it.
        Directory.CreateDirectory(GuardCache);
        var held = new List<FileStream>();
        try
        {
            foreach (int run in new[] { 1, 2 })
            {
                string old = Path.Combine(GuardCache, $"token-test-{run}.json");
                File.WriteAllText(old, Result(0, 0, 1));
                held.Add(new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.Read));
            }
            var guard = Guard(finishes: true, write: _ => Result(100, 100, 100));
            Assert.Equal(LeakVerdict.Leak, await guard.TestOnly(CancellationToken.None));
            Assert.Equal(GuardResult.Leak, guard.ReadState()!.Result);
            await guard.RunAtLogon(null, CancellationToken.None); // same build, leak: on without a test
            Assert.True(guard.WorkaroundWanted);
            Assert.True(_sets >= 1);
        }
        finally { foreach (var f in held) f.Dispose(); }
        // Once nothing holds them, the next run deletes them: nothing reads them.
        Assert.Equal(LeakVerdict.Leak, await Guard(finishes: true, write: _ => Result(100, 100, 100)).TestOnly(CancellationToken.None));
        Assert.Empty(Directory.GetFiles(GuardCache, "token-test-*.json"));
    }

    [Fact]
    public async Task NoOtherProgramCanOpenARunsResultSoTheHelpersResultIsTheOneUsed()
    {
        // A program tries to open this run's own result to read it (the data folder lets the account read), as soon as the
        // file is made, after the helper wrote it, and with every share mode; and, after the helper wrote it, to rename or
        // delete it. The guard holds the file open and shares it only for writing, so none of that is let in: the result
        // cannot be read or swapped, and the helper's result, read through the guard's own handle, is the one used.
        int tries = 0;
        void TryToRead(string file)
        {
            foreach (var share in new[] { FileShare.Read, FileShare.ReadWrite, FileShare.ReadWrite | FileShare.Delete })
            {
                tries++;
                Assert.Throws<IOException>(() => new FileStream(file, FileMode.Open, FileAccess.Read, share).Dispose());
            }
        }
        var guard = Guard(finishes: true, write: start =>
        {
            TryToRead(ResultFile(start));
            Assert.True(TokenLeakGuard.WriteResult(ResultFile(start), Result(100, 100, 100)));
            TryToRead(ResultFile(start));
            Assert.ThrowsAny<IOException>(() => File.Move(ResultFile(start), ResultFile(start) + ".moved"));
            Assert.ThrowsAny<IOException>(() => File.Delete(ResultFile(start)));
            Assert.True(File.Exists(ResultFile(start)));
            return null; // written above
        });
        Assert.Equal(LeakVerdict.Leak, await guard.TestOnly(CancellationToken.None));
        Assert.Equal(GuardResult.Leak, guard.ReadState()!.Result);
        Assert.Equal(12, tries); // two runs, before and after each wrote
        Assert.Empty(Directory.GetFiles(GuardCache, "token-test-*.json"));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [Fact]
    public async Task AHandleThatOnlyReadsAttributesKeepsARunsResultListedButUnreadableUntilItCloses()
    {
        // A program opens the first run's result only to read its attributes (Windows checks no sharing for that, and the
        // folder lets the account list it). The guard's handle closes when that run ends, yet the name stays listed: nobody
        // can open the file, the second run's sweep finds it, cannot delete it and leaves it, saying so, and the result
        // used is still the helper's. Once that handle closes, Windows deletes the file.
        const uint readAttributes = 0x80, shareAll = 7, openExisting = 3;
        Microsoft.Win32.SafeHandles.SafeFileHandle? attributes = null;
        try
        {
            var guard = Guard(finishes: true, write: start =>
            {
                attributes ??= CreateFileW(ResultFile(start), readAttributes, shareAll, 0, openExisting, 0, 0);
                return Result(100, 100, 100);
            });
            Assert.Equal(LeakVerdict.Leak, await guard.TestOnly(CancellationToken.None));
            Assert.False(attributes!.IsInvalid);
            string kept = Assert.Single(Directory.GetFiles(GuardCache, "token-test-*.json"));
            Assert.ThrowsAny<Exception>(() => new FileStream(kept, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete).Dispose());
            Assert.Contains($"an old helper result was not removed ({Path.GetFileName(kept)})", File.ReadAllText(Path.Combine(_root, "data", "guard.log")));
        }
        finally { attributes?.Dispose(); }
        Assert.Empty(Directory.GetFiles(GuardCache, "token-test-*.json"));
    }

    [Fact]
    public async Task AHelperThatFailedOrAResultThatIsNotAWholeRunIsUnclear()
    {
        // A helper that exits with an error, whatever the file holds.
        var failed = Guard(finishes: true, startHelper: start =>
        {
            Assert.True(TokenLeakGuard.WriteResult(ResultFile(start), Result(0, 0, 1)));
            var run = new FakeRun(start, finishes: true, exitCode: 1);
            lock (_runs) _runs.Add(run);
            return run;
        });
        Assert.Equal(LeakVerdict.Unclear, await failed.TestOnly(CancellationToken.None));
        Assert.Contains("the helper failed (exit 1)", failed.ReadState()!.Detail);

        // Results that are not one whole run of 100 children each way: too few or too many differences, another n, none,
        // numbers that are not whole.
        foreach (string json in new[]
                 {
                     "{\"deltas\":[0],\"n\":1}", "{\"deltas\":[0,0,0],\"n\":1}", "{\"deltas\":[0,0,0]}", "{\"deltas\":[0,0],\"n\":100}",
                     "{\"deltas\":[0,0,0,0],\"n\":100}", "{\"deltas\":[0,0,0.5],\"n\":100}", "{\"deltas\":[0,0,\"0\"],\"n\":100}",
                     "{\"n\":100}", "[0,0,0]", "{\"deltas\":[0,0,0],\"n\":\"100\"}",
                 })
        {
            File.Delete(Path.Combine(GuardCache, "token-guard.json"));
            var guard = Guard(finishes: true, write: _ => json);
            Assert.True(LeakVerdict.Unclear == await guard.TestOnly(CancellationToken.None), json);
            Assert.Equal(GuardResult.Unclear, guard.ReadState()!.Result);
        }
        // The control: a whole run, as the helper writes it.
        File.Delete(Path.Combine(GuardCache, "token-guard.json"));
        Assert.Equal(LeakVerdict.Clean, await Guard(finishes: true).TestOnly(CancellationToken.None));
    }

    [Fact]
    public void TheHelperWritesOnlyIntoTheEmptyFileTheGuardMadeForIt()
    {
        Directory.CreateDirectory(GuardCache);
        string file = Path.Combine(GuardCache, "token-test-" + Guid.NewGuid().ToString("N") + ".json");
        Assert.False(TokenLeakGuard.WriteResult(file, Result(1, 2, 3))); // it never makes a file: the guard does
        Assert.False(File.Exists(file));
        using (var result = HelperResultFile.Create(file))
        {
            Assert.Throws<IOException>(() => HelperResultFile.Create(file).Dispose()); // made new, never one that is there
            Assert.True(TokenLeakGuard.WriteResult(file, Result(1, 2, 3)));
            Assert.False(TokenLeakGuard.WriteResult(file, Result(0, 0, 0))); // never over one that holds something
            var (deltas, error) = TokenLeakGuard.ReadResult(result, 0, GuardPolicy.Children);
            Assert.Null(error);
            Assert.Equal(new long[] { 1, 2, 3 }, deltas);
        }
        Assert.False(File.Exists(file)); // deleted as the guard's handle closed
        // A helper that wrote nothing: no result, with its exit code.
        using (var empty = HelperResultFile.Create(file))
            Assert.Equal($"no result (exit {ExitCodes.ResultNotWritten})", TokenLeakGuard.ReadResult(empty, ExitCodes.ResultNotWritten, GuardPolicy.Children).error);
        Assert.False(File.Exists(file));
        // A result is never as large as its bound: a whole run's result padded past it is not read (an unclear verdict).
        using (var large = HelperResultFile.Create(file))
        {
            Assert.True(TokenLeakGuard.WriteResult(file, Result(100, 100, 100) + new string(' ', HelperResultFile.MaxLength)));
            Assert.Equal("the result file is too large", TokenLeakGuard.ReadResult(large, 0, GuardPolicy.Children).error);
        }
        using (var within = HelperResultFile.Create(file))
        {
            Assert.True(TokenLeakGuard.WriteResult(file, Result(100, 100, 100) + new string(' ', HelperResultFile.MaxLength - 200)));
            Assert.Null(TokenLeakGuard.ReadResult(within, 0, GuardPolicy.Children).error);
        }
    }

    /// <summary>A helper run that writes its result and then does not end within the guard's time (or is stopped).</summary>
    private sealed class HangingRun(HelperStart start, CancellationTokenSource? stopAfterWrite = null) : IHelperRun
    {
        public HelperStart Start { get; } = start;
        public int Ends;
        public bool WaitForExit(int milliseconds)
        {
            Assert.Equal((int)TokenLeakGuard.HelperTimeout.TotalMilliseconds, milliseconds);
            Assert.True(TokenLeakGuard.WriteResult(ResultFile(Start), Result(0, 0, 1)));
            stopAfterWrite?.Cancel();
            return stopAfterWrite is not null; // a stopped run has ended; the other has not
        }
        public int ExitCode => 0;
        public void End() => Interlocked.Increment(ref Ends);
        public void Dispose() { }
    }

    [Fact]
    public async Task ARunThatPassesItsTimeIsEndedThroughItsJobAndLeavesNothing()
    {
        // The helper does not end within 300 s: the guard ends it (its job's processes, and only those), the run fails, and
        // its result file, which the helper had written, is gone. A check saves unclear; a logon check turns the workaround on.
        var runs = new List<HangingRun>();
        IHelperRun Hang(HelperStart s) { var r = new HangingRun(s); lock (runs) runs.Add(r); return r; }
        var check = Guard(finishes: true, startHelper: Hang);
        Assert.Equal(LeakVerdict.Unclear, await check.TestOnly(CancellationToken.None));
        Assert.EndsWith(": helper run 1: timed out after 300 s", check.ReadState()!.Detail);
        var logon = Guard(finishes: true, startHelper: Hang);
        await logon.RunAtLogon(null, CancellationToken.None);
        Assert.Equal(GuardResult.Unclear, logon.State!.Result);
        Assert.True(logon.WorkaroundWanted);
        Assert.Equal(2, runs.Count); // each check's first run fails it
        Assert.All(runs, r => Assert.Equal(1, r.Ends));
        Assert.False(runs[1].Start.EndWithPoolWatch); // the logon check's run, which no closed job would end: only End does
        Assert.All(runs, r => Assert.False(File.Exists(ResultFile(r.Start)), "a run that passed its time left its result"));

        // Stopped (the setup guide closed) after its helper wrote: ended, nothing saved, nothing left.
        using var stop = new CancellationTokenSource();
        HangingRun? stopped = null;
        var guide = Guard(finishes: true, startHelper: s => stopped = new HangingRun(s, stop));
        Assert.Null(await guide.TestOnly(stop.Token));
        Assert.Equal(1, stopped!.Ends);
        Assert.False(File.Exists(ResultFile(stopped.Start)));
        Assert.Empty(Directory.GetFiles(GuardCache, "token-test-*.json"));
    }

    [Fact]
    public async Task OldResultFilesAreRemovedAtTheNextRunButNotTheTestMarker()
    {
        // What older versions of PoolWatch left (token-test-1.json and token-test-2.json, with fixed names) and a run's file
        // left by another process: the next run deletes them. The test marker, which has the same start to its name, stays.
        Directory.CreateDirectory(GuardCache);
        string[] old = [Path.Combine(GuardCache, "token-test-1.json"), Path.Combine(GuardCache, "token-test-2.json"),
                        Path.Combine(GuardCache, $"token-test-{Guid.NewGuid():N}.json")];
        foreach (string f in old) File.WriteAllText(f, Result(0, 0, 1));
        string marker = Path.Combine(GuardCache, TokenLeakGuard.TestMarkerName);
        File.WriteAllText(marker, "{}");
        var guard = Guard(finishes: true);
        guard.SweepResultFiles();
        Assert.All(old, f => Assert.False(File.Exists(f), f));
        Assert.True(File.Exists(marker));
        // A run does it before it starts its helper, and leaves the marker too. (Once the check has saved its verdict, it
        // answers the marker, which cannot be read and so counts as this build's, and that goes.)
        foreach (string f in old) File.WriteAllText(f, Result(0, 0, 1));
        var seen = new List<string>();
        int markerAtStart = 0;
        var checking = Guard(finishes: true, startHelper: s =>
        {
            seen.AddRange(Directory.GetFiles(GuardCache, "token-test-*.json"));
            if (File.Exists(marker)) markerAtStart++;
            return Start(s, true, _ => Result(0, 0, 1));
        });
        Assert.Equal(LeakVerdict.Clean, await checking.TestOnly(CancellationToken.None));
        Assert.DoesNotContain(seen, f => old.Contains(f, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(2, markerAtStart);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task TheGuardsFilesNeverFollowAHeadlessOutFolder()
    {
        // A headless run's --out folder, which someone else may be able to write: a clean verdict for this build, a helper
        // result and no test marker planted there change nothing. The guard reads, writes and tests only in the data root's
        // cache folder.
        string outDir = Path.Combine(_root, "out");
        var paths = new AppPaths(Path.Combine(_root, "data"), outDir, secure: false);
        Directory.CreateDirectory(Path.Combine(outDir, "cache"));
        File.WriteAllText(Path.Combine(outDir, "cache", "token-guard.json"), new GuardState(Build, GuardResult.CleanOnce, DateTime.UtcNow, "planted").ToJson());
        File.WriteAllText(Path.Combine(outDir, "cache", "token-test-1.json"), Result(0, 0, 1));
        var starts = new List<HelperStart>();
        var guard = new TokenLeakGuard(paths, _log, _ => { }, () => { Interlocked.Increment(ref _sets); return true; }, () => 20000,
            TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), build: () => Build,
            startHelper: start => { lock (starts) starts.Add(start); return Start(start, finishes: true, _ => Result(100, 100, 100)); });
        Assert.Null(guard.ReadState()); // the planted verdict is not read
        await guard.RunAtLogon(null, CancellationToken.None);
        Assert.Equal(GuardStep.Test, guard.Step);
        Assert.Equal(GuardResult.Leak, guard.State!.Result);
        Assert.True(guard.WorkaroundWanted);
        Assert.All(starts, s => Assert.Equal(GuardCache, Path.GetDirectoryName(ResultFile(s))));
        Assert.All(starts, s => Assert.DoesNotContain("--out", Argv(s.Environment[HelperCommand.LineVariable])));
        Assert.Equal(GuardResult.Leak, GuardState.Parse(File.ReadAllText(Path.Combine(GuardCache, "token-guard.json")))!.Result);
        Assert.Equal(GuardResult.CleanOnce, GuardState.Parse(File.ReadAllText(Path.Combine(outDir, "cache", "token-guard.json")))!.Result); // untouched
    }

    [Fact]
    public async Task AnOutcomeThatCannotBeSavedLeavesTheLogonProtected()
    {
        // Clean once on this build already, and the test is clean again: the outcome would be "fixed", workaround off. But
        // the verdict file is held, so the outcome cannot be saved: this logon gets the workaround anyway, and the test
        // marker stays, so the next start turns it on without testing.
        Directory.CreateDirectory(GuardCache);
        string state = Path.Combine(GuardCache, "token-guard.json");
        File.WriteAllText(state, new GuardState(Build, GuardResult.CleanOnce, DateTime.UtcNow, "clean once").ToJson());
        using (new FileStream(state, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var guard = Guard(finishes: true);
            await guard.RunAtLogon(null, CancellationToken.None);
            Assert.Equal(GuardStep.Test, guard.Step);
            Assert.True(guard.WorkaroundWanted);
            Assert.True(_sets >= 1, "the workaround was not turned on");
            Assert.Contains("could not be saved", guard.LastMessage);
            Assert.True(File.Exists(Path.Combine(GuardCache, TokenLeakGuard.TestMarkerName)), "the test marker was removed");
        }
        // The next start finds the marker: on without a test.
        _sets = 0;
        var next = Guard(finishes: true);
        await next.RunAtLogon(null, CancellationToken.None);
        Assert.Equal(GuardStep.TurnOnInterrupted, next.Step);
        Assert.Equal(2, _runs.Count); // the two runs of the first logon only
        Assert.True(_sets >= 1);
    }

    [Fact]
    public async Task AnOutcomeThatCannotBeSavedKeepsTheTestMarkerAlsoWhenTheCheckThenFails()
    {
        // The verdict's new file cannot be written (a folder is in its place), and then the first finding fails, so the
        // check ends in its error path. That path turns the workaround on, and, as the outcome was not saved, leaves the test
        // marker, so the next start turns the workaround on without testing.
        Directory.CreateDirectory(Path.Combine(GuardCache, "token-guard.json.tmp"));
        int said = 0;
        var guard = new TokenLeakGuard(_paths, _log, _ => { if (Interlocked.Increment(ref said) == 1) throw new InvalidOperationException("the timeline is full"); },
            () => { Interlocked.Increment(ref _sets); return true; }, () => 20000, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1),
            build: () => Build, startHelper: s => Start(s, finishes: true, _ => Result(100, 100, 100)));
        await guard.RunAtLogon(null, CancellationToken.None);
        Assert.Equal(2, said); // the outcome's, which failed, then the error path's
        Assert.Equal("Token-leak guard: the check failed (see poolwatch.log), but the workaround is ON.", guard.LastMessage);
        Assert.True(_sets >= 1);
        Assert.True(File.Exists(Path.Combine(GuardCache, TokenLeakGuard.TestMarkerName)), "the test marker was removed although the outcome was not saved");
    }

    [Fact]
    public async Task ACheckWhoseVerdictCannotBeSavedSaysSo()
    {
        Directory.CreateDirectory(GuardCache);
        string state = Path.Combine(GuardCache, "token-guard.json");
        File.WriteAllText(state, new GuardState(Build, GuardResult.CleanOnce, DateTime.UtcNow, "clean once").ToJson());
        using (new FileStream(state, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var guard = Guard(finishes: true, write: _ => Result(100, 100, 100));
            Assert.Equal(LeakVerdict.Leak, await guard.TestOnly(CancellationToken.None)); // what it found
            Assert.Equal(GuardResult.Leak, guard.ReadState()!.Result);                     // this PoolWatch knows it
            var f = Assert.Single(_findings);
            Assert.Equal(FindingSeverity.Warning, f.Severity);
            Assert.EndsWith("The result could not be saved (see poolwatch.log).", f.Title);
        }
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
        Assert.Contains("hookExit: HookProcessExit, setTo: TokenGuardNative.SetForegroundLockTimeout, signIn: TokenGuardNative.SignInId) { }", UninstallerTests.OneLine(ScriptCode.CSharp(source))); // the code, not a comment
        // Read as a member of its type, TokenLeakGuard (the type's own text, then the member's in it), which declares it.
        string guard = ScriptCode.CSharpMember(source, "internal sealed class TokenLeakGuard").Value;
        var hook = ScriptCode.CSharpMember(guard, "private static void HookProcessExit(");
        Assert.Equal(typeof(TokenLeakGuard), typeof(TokenLeakGuard).GetMethod("HookProcessExit",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.DeclaringType);
        Assert.Equal(UninstallerTests.OneLine("""
            private static void HookProcessExit(Action onExit)
            {
                AppDomain.CurrentDomain.UnhandledException += (_, _) => onExit();
                AppDomain.CurrentDomain.ProcessExit += (_, _) => onExit();
            }
            """), UninstallerTests.OneLine(hook.Value));
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
    public async Task AHelperWindowsWillNotStartInAJobIsNotStartedAndTheRunIsUnclear()
    {
        // No helper ever runs outside a job: when the job start fails, nothing runs and the run's result is unclear. A check
        // saves that; the logon check turns the workaround on.
        int starts = 0;
        Func<HelperStart, IHelperRun> refused = _ => { Interlocked.Increment(ref starts); throw new InvalidOperationException("CreateProcess failed: 5"); };
        var check = Guard(finishes: true, startHelper: refused);
        Assert.Equal(LeakVerdict.Unclear, await check.TestOnly(CancellationToken.None));
        Assert.Contains("the helper could not start: CreateProcess failed: 5", check.ReadState()!.Detail);
        var logon = Guard(finishes: true, startHelper: refused);
        await logon.RunAtLogon(null, CancellationToken.None);
        Assert.Equal(GuardResult.Unclear, logon.State!.Result);
        Assert.True(logon.WorkaroundWanted);
        Assert.True(_sets >= 1);
        Assert.Equal(2, starts); // one try per check: its first run fails it
        Assert.Empty(Directory.GetFiles(GuardCache, "token-test-*.json"));
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
        Assert.False(File.Exists(Path.Combine(GuardCache, TokenLeakGuard.TestMarkerName)), "the logon check wrote its test marker");
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
    public void EveryHelperIsStartedInAJobOrNotAtAll()
    {
        // A file that is not there: nothing starts either way, and the log says the helper could not start in a job.
        var guard = Guard(finishes: true);
        foreach (bool endWithPoolWatch in new[] { false, true })
        {
            var missing = HelperCommand.For(Path.Combine(_root, "missing", "no-such-helper.exe"), 1, Path.Combine(GuardCache, "r.json"), null, endWithPoolWatch)
                with { Application = Path.Combine(_root, "missing", "no-such-cmd.exe") };
            var ex = Assert.ThrowsAny<InvalidOperationException>(() => guard.StartHelper(missing));
            Assert.Matches("^CreateProcess failed: [23]$", ex.Message); // the file is missing (2 or 3): its job list and environment were accepted
        }
        Assert.Equal(2, Regex.Matches(LogText(), "token guard: the helper could not start in a job, so it was not started: CreateProcess failed: ").Count);
    }

    [Fact]
    public async Task TheInstallerTestLooksForTheLinesTheGuardLogs()
    {
        // ci/installer-e2e.ps1 reads PoolWatch's log to tell whether each helper run of a check got its job. Its two
        // patterns (read as PowerShell's -match reads them: ignoring case) must each match their own line and not the other.
        string e2e = File.ReadAllText(Path.Combine(RepoDir(), "ci", "installer-e2e.ps1"));
        Regex Pattern(string variable) => new(ScriptCode.PowerShellVariable(e2e, variable), RegexOptions.IgnoreCase); // its one "$name = '...'" in its code
        Regex inJob = Pattern("helperInJob"), noJob = Pattern("helperNoJob");
        // The check's whole text, without its comments, with every run of white space made one space: it reads the log
        // lines written after the run started, matches each pattern into its own list, says so and returns when neither
        // matched, and otherwise fails on any no-job line and prints its CHECK-RAN marker (CheckMarkerTests), so a run of
        // the installer's CI job in which no run of it got that far fails. Any edit to its code fails this test, except one
        // that only changes white space in that way.
        var check = ScriptCode.PowerShellFunction(e2e, "Assert-HelperInJob"); // the script's only definition of it, in its code
        const string inJobStep = """
            function Assert-HelperInJob($running, [string]$name) {
                $tail = @(Get-Content $log -ErrorAction SilentlyContinue | Select-Object -Skip $running.Skip)
                $inJob = @($tail -match $helperInJob)
                $noJob = @($tail -match $helperNoJob)
                if ($inJob.Count + $noJob.Count -eq 0) {
                    Write-Host "  ${name}: the token-leak check started no helper in this run of PoolWatch, so its job is not checked"
                    return
                }
                foreach ($line in $noJob) { Write-Host "  ${name}: $line" }
                Assert-True ($noJob.Count -eq 0) "${name}: each helper run of the token-leak check got a job ($($inJob.Count) in a job, $($noJob.Count) without)"
                Write-Host 'CHECK-RAN: installer-helper-runs-in-a-job'
            }
            """;
        Assert.Equal(UninstallerTests.OneLine(inJobStep), UninstallerTests.OneLine(check.Value));
        Assert.Empty(UninstallerTests.ChecksWithoutAMarker(check.Value, [inJobStep])); // its check prints its marker where it must be
        // Each run is checked right after the check that it was stopped through its exit signal: after the uninstall
        // (tray-1) and after the upgrade (tray-2), each pair a run of checks with its CHECK-RAN marker right after it
        // (ScriptCode.CheckThenMarker): one statement of the script's top level, and only one, starts with the job check's
        // text, the statement right before it starts with the stop check's, and the one right after it is the marker, read
        // without the script's comments: not commented out, not in a string, and not inside a condition, a loop or another
        // block. That they run is what the markers show in CI.
        Assert.True(ScriptCode.CheckThenMarker(e2e, ["Assert-StoppedBySignal $pw 'uninstaller'", "Assert-HelperInJob $pw 'tray-1'"], "installer-checked-after-uninstall"),
            "tray-1's job is not checked right after the uninstall");
        Assert.True(ScriptCode.CheckThenMarker(e2e, ["Assert-StoppedBySignal $pw 'upgrade'", "Assert-HelperInJob $pw 'tray-2'"], "installer-checked-after-upgrade"),
            "tray-2's job is not checked right after the upgrade");

        // A check whose two runs got the job.
        Assert.NotNull(await Guard(finishes: true).TestOnly(CancellationToken.None));
        string[] lines = LogText().Split('\n');
        Assert.Equal(2, lines.Count(inJob.IsMatch));
        Assert.DoesNotContain(lines, noJob.IsMatch);

        // A run that Windows would not start in its job (the real StartHelper with a cmd.exe that is not there: nothing starts).
        var missing = HelperCommand.For(Path.Combine(_root, "missing", "no-such-helper.exe"), 1, Path.Combine(GuardCache, "r.json"), null, true)
            with { Application = Path.Combine(_root, "missing", "no-such-cmd.exe") };
        Assert.ThrowsAny<Exception>(() => Guard(finishes: true).StartHelper(missing));
        lines = LogText().Split('\n');
        Assert.Single(lines, noJob.IsMatch);
        Assert.Equal(2, lines.Count(inJob.IsMatch));
    }

    [Fact]
    public void TheInstallerTestKillsPoolWatchDuringTheCheckAndLooksForAHelperLeftRunning()
    {
        // ci/installer-e2e.ps1 kills the installed PoolWatch (never its exit signal) while the setup guide's check runs, and
        // fails if a helper (PoolWatch.exe --token-leak-test) still runs 15 seconds later (Windows' kill-on-close job ends it
        // with PoolWatch), or if it saw no helper at all (looked for from the moment PoolWatch starts), which would test nothing.
        // PoolWatch is started through its launcher, as its users start it, and what is killed is PoolWatch itself, the
        // launcher's child (app\PoolWatch.exe --tray), which holds the job: the launcher only waits for it.
        // The functions' whole text, with every run of white space made one space; any edit to them fails this
        // test, except one that only changes white space in that way. It runs after the reinstall, before the last uninstall.
        string e2e = File.ReadAllText(Path.Combine(RepoDir(), "ci", "installer-e2e.ps1"));
        var helpers = Regex.Match(e2e, @"(?ms)^function Get-Helpers \{.*?^\}");
        var kill = Regex.Match(e2e, @"(?ms)^function Assert-HelperEndsWithKill\(.*?^\}");
        Assert.True(helpers.Success && kill.Success, "the kill step's functions are not in ci/installer-e2e.ps1");
        Assert.Equal(UninstallerTests.OneLine("""
            function Get-Helpers {
                @(Get-CimInstance Win32_Process -Filter "Name = 'PoolWatch.exe'" -ErrorAction SilentlyContinue |
                    Where-Object { $_.CommandLine -like '*--token-leak-test*' })
            }
            """), UninstallerTests.OneLine(helpers.Value));
        const string killStep = """
            function Assert-HelperEndsWithKill([string]$name) {
                Remove-Item (Join-Path $data 'cache\token-guard.json') -ErrorAction SilentlyContinue
                $p = Start-Process $launcher -ArgumentList '--tray' -PassThru -RedirectStandardOutput "$Out\$name.txt"
                $null = $p.Handle   # keeps the exit code readable after the process ends
                $deadline = (Get-Date).AddSeconds(120)
                $helpers = @()
                while ($helpers.Count -eq 0 -and -not $p.HasExited -and (Get-Date) -lt $deadline) {
                    Start-Sleep -Milliseconds 250
                    $helpers = @(Get-Helpers)
                }
                foreach ($h in $helpers) { Write-Host "  ${name}: helper $($h.ProcessId) runs: $($h.CommandLine)" }
                $own = @(Get-CimInstance Win32_Process -Filter "Name = 'PoolWatch.exe'" -ErrorAction SilentlyContinue |
                    Where-Object { $_.ParentProcessId -eq $p.Id -and $_.CommandLine -notlike '*--token-leak-test*' })
                foreach ($o in $own) { Write-Host "  ${name}: PoolWatch itself is process $($o.ProcessId): $($o.CommandLine)" }
                Assert-True ($own.Count -eq 1) "${name}: PoolWatch itself ran below its launcher ($($own.Count) found)"
                Write-Host 'CHECK-RAN: installer-kill-pw-below-launcher'
                foreach ($o in $own) { Stop-Process -Id $o.ProcessId -Force -ErrorAction SilentlyContinue }
                Assert-True ($p.WaitForExit(30000)) "${name}: PoolWatch was killed, and its launcher ended with it"
                Assert-True ($helpers.Count -gt 0) "${name}: the token-leak check started a helper while PoolWatch ran, so the kill tests it"
                Write-Host 'CHECK-RAN: installer-kill-launcher-ended'
                if ($helpers.Count -eq 0) { return }
                $deadline = (Get-Date).AddSeconds(15)
                do {
                    Start-Sleep -Seconds 1
                    $left = @(Get-Helpers)
                } until ($left.Count -eq 0 -or (Get-Date) -gt $deadline)
                foreach ($h in $left) { Write-Host "  ${name}: still running: helper $($h.ProcessId): $($h.CommandLine)" }
                Assert-True ($left.Count -eq 0) "${name}: no token-leak helper ran on after PoolWatch was killed ($($left.Count) left)"
                Write-Host 'CHECK-RAN: installer-helper-ends-with-kill'
            }
            """;
        Assert.Equal(UninstallerTests.OneLine(killStep), UninstallerTests.OneLine(kill.Value));
        Assert.Empty(UninstallerTests.ChecksWithoutAMarker(kill.Value, [killStep])); // each of its checks prints a marker where it must be
        const string install3 = "Install 'install-3' Assert-True ((Get-StartMode $service) -eq 'Disabled') \"$service is still off, as /KEEPCHANGES left it\" " +
                                "Write-Host 'CHECK-RAN: reinstall-change-still-off' Assert-HelperInJobFromTask 'task-3' Assert-HelperEndsWithKill 'tray-3' ";
        Assert.Contains(install3, UninstallerTests.OneLine(e2e));
        Assert.Empty(UninstallerTests.ChecksWithoutAMarker(e2e, [install3]));
        // Before the kill, PoolWatch is started once through Task Scheduler, as the logon task starts it (its launcher), so it
        // runs in a job of Task Scheduler's; each helper run must still get a job of its own (Assert-HelperInJob fails on a
        // no-job line), and none may run on once that PoolWatch was stopped through its exit signal.
        var task = Regex.Match(e2e, @"(?ms)^function Assert-HelperInJobFromTask\(.*?^\}");
        Assert.True(task.Success, "Assert-HelperInJobFromTask is not in ci/installer-e2e.ps1");
        string taskStep = UninstallerTests.OneLine(task.Value);
        string[] taskPins =
        [
            "$taskPrincipal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Highest " +
                "Register-ScheduledTask -TaskName 'PoolWatch' -TaskPath '\\' -Action (New-ScheduledTaskAction -Execute $launcher -Argument '--tray') ` " +
                "-Trigger (New-ScheduledTaskTrigger -AtLogOn) -Principal $taskPrincipal -Force | Out-Null " +
                "try { Start-ScheduledTask -TaskName 'PoolWatch' -TaskPath '\\' }",
            "} until ($runs -ge 2 -or (Get-Date) -gt $deadline)",
            "$code = Invoke-Installed \"$name-stop\" @('--stop-running') Unregister-ScheduledTask -TaskName 'PoolWatch' -TaskPath '\\' -Confirm:$false " +
                "Assert-True ($code -eq 0) ",
            "Assert-True (@(Get-Helpers).Count -eq 0) \"${name}: no token-leak helper runs on after that PoolWatch exited\" Write-Host 'CHECK-RAN: installer-task-helper-ends' " +
                "Assert-HelperInJob ([pscustomobject]@{ Skip = $skip }) $name }",
        ];
        Assert.All(taskPins, p => Assert.Contains(p, taskStep));
        // Each check of the step these hold (in the function's own text) prints its marker where it must be.
        Assert.Empty(UninstallerTests.ChecksWithoutAMarker(task.Value, taskPins));
    }
}
