using PoolWatch.Core.TokenGuard;
using PoolWatch.Engine;

namespace PoolWatch.AppTests;

/// <summary>
/// C4 (review round 2): one gate for the token guard's "click to turn it on" prompt, shared by the notice's Turn on, the
/// background recheck and the logon check. The live foreground lock timeout is a fake here: no test changes it.
/// </summary>
public sealed class TokenGuardPromptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pw-guard-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private sealed class FakeUi : IGuardUi
    {
        private readonly TaskCompletionSource<bool> _click = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Asked;
        public readonly List<string> Outcomes = new();
        public void ShowOutcome(string message, bool protectedNow) { lock (Outcomes) Outcomes.Add(message); }
        public Task<bool> AskForClick(string message) { Interlocked.Increment(ref Asked); return _click.Task; }
        public void Click(bool ok) => _click.SetResult(ok);
    }

    [Fact]
    public async Task NeverTwoPromptsAtOnce()
    {
        var paths = new AppPaths(Path.Combine(_root, "data"), null, secure: false);
        var log = new Log(Path.Combine(_root, "data", "guard.log"));
        int sets = 0, accept = 0;
        var guard = new TokenLeakGuard(paths, log, _ => { },
            setNow: () => { Interlocked.Increment(ref sets); return Volatile.Read(ref accept) == 1; }, // Windows refuses until the click
            getLive: () => 20000u, attemptGap: TimeSpan.FromMilliseconds(1), clickRetryGap: TimeSpan.FromMilliseconds(1));
        var ui = new FakeUi();

        var first = guard.TurnOnByOwner(ui); // refused 4 times, then the prompt opens and waits for the owner
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref ui.Asked) == 1, TimeSpan.FromSeconds(10)));
        Assert.True(guard.Prompting);

        // A second "Turn on" while the prompt is open: no second prompt; it tries once more silently (and returns at once:
        // waiting for a prompt of its own would never end here).
        var second = guard.TurnOnByOwner(ui);
        Assert.Same(second, await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(10))));
        Assert.False(await second);
        Assert.Equal(1, ui.Asked);

        // The background recheck (something set the timeout back) while the prompt is open: no second prompt either.
        guard.WorkaroundWanted = true;
        int before = Volatile.Read(ref sets);
        guard.Recheck(DateTime.UtcNow, TimeSpan.FromMinutes(3), () => ui);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref sets) >= before + 6, TimeSpan.FromSeconds(10))); // 1 + 4 tries + 1 after the gate
        await Task.Delay(50);
        Assert.Equal(1, ui.Asked);

        // The owner clicks; Windows accepts now. The one prompt finishes the job and the gate opens again.
        Volatile.Write(ref accept, 1);
        ui.Click(true);
        Assert.True(await first);
        Assert.False(guard.Prompting);
    }

    [Fact]
    public async Task ALeakTestCutShortLeavesTheNextStartProtected()
    {
        // M3: a hard kill (Task Manager "End task") or a native crash during the leak test runs no managed handler, so the
        // exit failsafe never turns the workaround on. The marker written BEFORE the test survives that, and the next start
        // turns the workaround on without testing. The test and the setting are fakes: nothing is started or changed.
        var paths = new AppPaths(Path.Combine(_root, "data"), null, secure: false);
        var log = new Log(Path.Combine(_root, "data", "guard.log"));
        string marker = Path.Combine(paths.CacheDir, TokenLeakGuard.TestMarkerName);
        int sets = 0, tests = 0;
        bool markerDuringTest = false;
        TokenLeakGuard Start(Func<CancellationToken, (List<long>, string?)> runTest) => new(paths, log, _ => { },
            setNow: () => { Interlocked.Increment(ref sets); return true; }, getLive: () => 20000u,
            attemptGap: TimeSpan.FromMilliseconds(1), clickRetryGap: TimeSpan.FromMilliseconds(1), runTest: runTest, build: () => "26100.1000");

        // A logon that tests clean once: the marker is there while the test runs, and gone once the outcome is saved.
        var first = Start(_ => { tests++; markerDuringTest = File.Exists(marker); return ([0, 0, 1, 0, 1, 0], null); });
        await first.RunAtLogon(null, CancellationToken.None);
        Assert.Equal(GuardStep.Test, first.Step);
        Assert.True(markerDuringTest);
        Assert.False(File.Exists(marker));

        // The next logon's test is killed: nothing after the test ran, so its marker is still there.
        File.WriteAllText(marker, "{}");
        sets = 0;
        var next = Start(_ => { tests++; return ([0, 0, 0, 0, 0, 0], null); });
        await next.RunAtLogon(null, CancellationToken.None);
        Assert.Equal(GuardStep.TurnOnInterrupted, next.Step); // without the marker this build (clean once) would test again
        Assert.Equal(1, tests);                                // no new test: the workaround goes on at once
        Assert.True(sets >= 1);
        Assert.True(next.WorkaroundWanted);
        Assert.False(File.Exists(marker));                     // the logon after that decides from the saved state again
        Assert.Equal(GuardResult.Unclear, next.State!.Result);
    }
}
