using PoolWatch.Core.Findings;
using PoolWatch.Core.TokenGuard;
using PoolWatch.Engine;

namespace PoolWatch.AppTests;

/// <summary>
/// The setup guide's token-leak check: the leak test alone, the verdict saved for the next logon check, nothing changed.
/// The leak test and the live foreground lock timeout are fakes: no test starts a process or changes the setting.
/// </summary>
public sealed class TokenLeakTestOnlyTests : IDisposable
{
    private const string Build = "26220.7344";
    private static readonly List<long> LeakNumbers = [1, 0, 2, 100, 98, 101];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pw-testonly-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly Log _log;
    private readonly List<Finding> _findings = new();
    private int _sets, _tests;

    public TokenLeakTestOnlyTests()
    {
        _paths = new AppPaths(Path.Combine(_root, "data"), null, secure: false);
        _log = new Log(Path.Combine(_root, "data", "guard.log"));
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private TokenLeakGuard Guard(Func<CancellationToken, (List<long>, string?)> runTest, uint? live = 20000) => new(_paths, _log, f => { lock (_findings) _findings.Add(f); },
        setNow: () => { Interlocked.Increment(ref _sets); return true; }, getLive: () => live,
        attemptGap: TimeSpan.FromMilliseconds(1), clickRetryGap: TimeSpan.FromMilliseconds(1),
        runTest: stop => { Interlocked.Increment(ref _tests); return runTest(stop); }, build: () => Build);

    /// <summary>A leak test that waits for <paramref name="go"/> and then finds the leak.</summary>
    private static Func<CancellationToken, (List<long>, string?)> Blocked(ManualResetEventSlim go, ManualResetEventSlim started) => _ =>
    {
        started.Set();
        go.Wait(TimeSpan.FromSeconds(30));
        return (LeakNumbers, null);
    };

    [Fact]
    public async Task RefusesWhileTheWorkaroundIsOnOrTheTimeoutIsNotReported()
    {
        Assert.Null(await Guard(_ => (LeakNumbers, null), live: 0).TestOnly(CancellationToken.None));
        Assert.Null(await Guard(_ => (LeakNumbers, null), live: null).TestOnly(CancellationToken.None));
        Assert.Equal(0, _tests);
    }

    [Fact]
    public async Task SavesTheVerdictForTheNextLogonCheckAndChangesNothing()
    {
        var guard = Guard(_ => (LeakNumbers, null));
        Assert.Equal(LeakVerdict.Leak, await guard.TestOnly(CancellationToken.None));
        Assert.Equal(0, _sets); // the workaround is left as it was
        Assert.False(File.Exists(Path.Combine(_paths.CacheDir, TokenLeakGuard.TestMarkerName)));
        Assert.Equal(Build, guard.ReadState()!.Build);
        Assert.Equal(GuardResult.Leak, guard.ReadState()!.Result);
        var f = Assert.Single(_findings);
        Assert.Equal(FindingKind.TokenGuard, f.Kind);
        Assert.Equal("Token leak check: this Windows build has the token leak; nothing was changed.", f.Title);

        // The verdict is on disk: a later start reads it, and the logon check turns the workaround on without a test.
        var next = Guard(_ => throw new InvalidOperationException("no test expected"));
        Assert.Equal(GuardResult.Leak, next.ReadState()!.Result);
        await next.RunAtLogon(null, CancellationToken.None);
        Assert.Equal(GuardStep.TurnOnKnownLeak, next.Step);
        Assert.Equal(1, _tests);
        Assert.True(_sets >= 1);
    }

    [Fact]
    public async Task ReadStateReadsTheFileOnceThenKeepsItsCopy()
    {
        var guard = Guard(_ => ([0, 0, 1, 0, 1, 0], null));
        Assert.Null(guard.ReadState());
        Assert.Equal(LeakVerdict.Clean, await guard.TestOnly(CancellationToken.None));
        string file = Path.Combine(_paths.CacheDir, "token-guard.json");
        File.Delete(file);
        Assert.Equal(GuardResult.CleanOnce, guard.ReadState()!.Result); // this process saved it: no file read needed
    }

    [Fact]
    public async Task OneCheckAtATimeMarkedChecking()
    {
        using var go = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var guard = Guard(Blocked(go, started));
        var first = guard.TestOnly(CancellationToken.None);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(guard.Checking); // the spawn diagnostics ignore the helper's children meanwhile
        Assert.Null(await guard.TestOnly(CancellationToken.None));
        go.Set();
        Assert.Equal(LeakVerdict.Leak, await first);
        Assert.False(guard.Checking);
        Assert.Equal(1, _tests);
    }

    [Fact]
    public async Task ALogonCheckStartedMeanwhileWaitsAndDecidesFromTheVerdict()
    {
        using var go = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var guard = Guard(Blocked(go, started));
        var check = guard.TestOnly(CancellationToken.None);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        var logon = Task.Run(() => guard.RunAtLogon(null, CancellationToken.None)); // the guard turned on while the guide checks
        await Task.Delay(200);
        Assert.False(logon.IsCompleted);
        go.Set();
        Assert.Equal(LeakVerdict.Leak, await check);
        await logon;
        Assert.Equal(GuardStep.TurnOnKnownLeak, guard.Step); // not a second test: the check's verdict for this build
        Assert.Equal(1, _tests);
        Assert.True(guard.WorkaroundWanted);
    }

    [Fact]
    public async Task ExitingDuringTheCheckLeavesTheWorkaroundAlone()
    {
        using var go = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var guard = Guard(Blocked(go, started));
        var check = guard.TestOnly(CancellationToken.None);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        guard.OnExit();
        Assert.Equal(0, _sets);
        go.Set();
        await check;
    }

    [Fact]
    public async Task OnlyTheLogonCheckTellsItsHelperToWatchPoolWatch()
    {
        // A helper that watches PoolWatch offers "turn it on" when PoolWatch stops during the test: right for the logon
        // check, which may leave the logon unprotected, but not for the guide's check, which never turns anything on.
        var watched = new List<bool>();
        TokenLeakGuard? guard = null;
        guard = Guard(_ => { watched.Add(guard!.HelperWatchesParent); return ([0, 0, 1, 0, 1, 0], null); });
        Assert.Equal(LeakVerdict.Clean, await guard.TestOnly(CancellationToken.None));
        await guard.RunAtLogon(null, CancellationToken.None); // clean once on this build: the logon check tests again
        Assert.Equal([false, true], watched);
    }

    [Fact]
    public async Task TheLogonChecksNumbersAreNotTheGuidesCheck()
    {
        var runs = new Queue<List<long>>([[0, 0, 1, 0, 1, 0], [2, 1, 0, 1, 2, 1]]);
        var guard = Guard(_ => (runs.Dequeue(), null));
        await guard.TestOnly(CancellationToken.None);
        Assert.Empty(guard.TestNumbers); // "this logon's test": the guide's check is not one
        await guard.RunAtLogon(null, CancellationToken.None);
        Assert.Equal(new List<long> { 2, 1, 0, 1, 2, 1 }, guard.TestNumbers);
    }

    [Fact]
    public async Task ACheckStoppedPartWaySavesNothing()
    {
        using var started = new ManualResetEventSlim();
        using var stop = new CancellationTokenSource();
        var guard = Guard(token =>
        {
            started.Set();
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
            return ([], "helper run 1: cancelled"); // what the real helper run says when it is stopped
        });
        var check = guard.TestOnly(stop.Token);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        stop.Cancel(); // the guide was closed during the check
        Assert.Null(await check);
        Assert.False(guard.Checking);
        Assert.Null(guard.ReadState());
        Assert.False(File.Exists(Path.Combine(_paths.CacheDir, "token-guard.json")));
        Assert.Empty(_findings); // and nothing on the timeline: the next check is offered again
    }

    [Fact]
    public async Task AFailedRunIsUnclearAndACrashedOneSavesNothing()
    {
        var failed = Guard(_ => ([], "helper run 1: timed out after 300 s"));
        Assert.Equal(LeakVerdict.Unclear, await failed.TestOnly(CancellationToken.None));
        Assert.Equal(GuardResult.Unclear, failed.ReadState()!.Result);

        File.Delete(Path.Combine(_paths.CacheDir, "token-guard.json"));
        var crashed = Guard(_ => throw new IOException("the result file was not readable"));
        Assert.Null(await crashed.TestOnly(CancellationToken.None));
        Assert.False(crashed.Checking);
        Assert.Null(crashed.ReadState());
    }
}
