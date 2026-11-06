using System.Text.RegularExpressions;
using PoolWatch.Core.TokenGuard;
using PoolWatch.Engine;

namespace PoolWatch.AppTests;

/// <summary>
/// One gate for the token guard's "click to turn it on" prompt, shared by the notice's Turn on, a click on the tray icon
/// and the logon check; the background recheck never asks. The live foreground lock timeout is a fake here: no test
/// changes it.
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
        public void ShowNotProtected(string message) { lock (Outcomes) Outcomes.Add(message); }
        public Task<bool> AskForClick(string message) { Interlocked.Increment(ref Asked); return _click.Task; }
        public void Withdraw(bool prompt) { }
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

        // A click on the tray icon while the prompt is open: its four tries, then no second prompt; it tries once more.
        guard.WorkaroundWanted = true;
        int before = Volatile.Read(ref sets);
        Assert.False(await guard.TurnOnFromTray(ui));
        Assert.Equal(before + 5, Volatile.Read(ref sets)); // 4 tries + 1 after the gate
        Assert.Equal(1, ui.Asked);

        // The background recheck (something set the timeout back) while the prompt is open: one silent try, and no prompt
        // (it never asks, prompt open or not).
        before = Volatile.Read(ref sets);
        guard.Recheck(TimeSpan.FromMinutes(3), () => ui);
        Assert.Equal(before + 1, Volatile.Read(ref sets));
        Assert.Equal(1, ui.Asked);
        lock (ui.Outcomes) Assert.Empty(ui.Outcomes);

        // The owner clicks; Windows accepts now. The one prompt finishes the job and the gate opens again.
        Volatile.Write(ref accept, 1);
        ui.Click(true);
        Assert.True(await first);
        Assert.False(guard.Prompting);
    }

    [Fact]
    public void TheRecheckIsTimedOnTheClockThatOnlyGoesForward()
    {
        // The recheck runs at most once every three minutes, timed on the monotonic clock. Here that clock moves three
        // minutes on while the wall clock does not, as when the clock is set back three minutes meanwhile: the recheck still
        // runs on time. Timed on the wall clock, it waited for the wall clock to catch up.
        var paths = new AppPaths(Path.Combine(_root, "data"), null, secure: false);
        var log = new Log(Path.Combine(_root, "data", "guard.log"));
        long clock = 5_000_000;
        uint live = 20000u;
        int sets = 0;
        var guard = new TokenLeakGuard(paths, log, _ => { }, setNow: () => { sets++; live = 0; return true; }, getLive: () => live,
            attemptGap: TimeSpan.FromMilliseconds(1), clickRetryGap: TimeSpan.FromMilliseconds(1), clockMs: () => clock) { WorkaroundWanted = true };
        guard.Recheck(TimeSpan.FromMinutes(3), () => null);
        Assert.Equal(1, sets); // the first recheck runs at once, and finds the setting set back
        live = 20000u;
        clock += 179_999;
        guard.Recheck(TimeSpan.FromMinutes(3), () => null);
        Assert.Equal(1, sets); // not due yet
        clock += 1;
        guard.Recheck(TimeSpan.FromMinutes(3), () => null);
        Assert.Equal(2, sets); // due: three minutes on, on the monotonic clock
        Assert.Equal(0u, live);
        // The spawn diagnostics leave the helper's children out for two minutes after the last one, on the same clock (the
        // running app is needed to drive them, so this holds their lines).
        string diagnostics = UninstallerTests.OneLine(File.ReadAllText(Path.Combine(TokenLeakHelperStartTests.RepoDir(), "src", "PoolWatch", "Analysis", "Diagnostics.cs")));
        Assert.Contains("if (own) _ownHelperUntilMs = Environment.TickCount64 + 120_000;", diagnostics);
        Assert.Contains("var spawnEvents = Guard.Checking || Environment.TickCount64 < _ownHelperUntilMs ?", diagnostics);
        Assert.Contains("try { Guard.Recheck(TimeSpan.FromMinutes(_engine.Settings.TokenGuardRecheckMinutes), () => GuardUi); }", diagnostics);
    }

    [Fact]
    public void TheTokenCorrelatorIsFedTheClockThatOnlyGoesForward()
    {
        // TokenCorrelator closes buckets, drops them from its window, spaces a repeated finding and gives a warning's span by
        // the clock it is passed (TokenTests show what a wall clock changed meanwhile would do). The running app passes the
        // monotonic clock, not the sample's wall-clock time; it needs the running app to drive it, so this holds the line,
        // on a line of its own so a commented-out call does not match, and the only call there is.
        string source = File.ReadAllText(Path.Combine(TokenLeakHelperStartTests.RepoDir(), "src", "PoolWatch", "Analysis", "Diagnostics.cs"));
        Assert.Matches(new Regex(@"(?m)^[ \t]*lock \(Tokens\) token = Tokens\.Observe\(s, events, Environment\.TickCount64\);\r?$"), source);
        Assert.Single(Regex.Matches(source, @"Tokens\.Observe\("));
    }

    [Fact]
    public async Task ALeakTestCutShortLeavesTheNextStartProtected()
    {
        // A hard kill (Task Manager "End task") or a native crash during the leak test runs no managed handler, so the
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
        Assert.False(File.Exists(marker));                     // the logon after that decides from the saved state
        Assert.Equal(GuardResult.Interrupted, next.State!.Result);

        // Which says a test on this build was cut short: the logon after that turns the workaround on without testing too,
        // so a test that keeps being cut short never leaves a logon without it.
        sets = 0;
        var after = Start(_ => { tests++; return ([0, 0, 0, 0, 0, 0], null); });
        await after.RunAtLogon(null, CancellationToken.None);
        Assert.Equal(GuardStep.TurnOnInterrupted, after.Step);
        Assert.Equal(1, tests);
        Assert.True(sets >= 1);
    }
}
