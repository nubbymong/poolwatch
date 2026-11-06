using System.Text.RegularExpressions;
using PoolWatch.Core.Findings;
using PoolWatch.Core.TokenGuard;
using PoolWatch.Engine;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// The token-leak guard shows a notice only when the user's click is needed, and only for the sign-in check and what the
/// user clicked: Windows refused the workaround from the background and the guard needs it on, and, as it would show the
/// notice, no click prompt of the guard's is under way, the guard is on and the live timeout is not 0. Later in a
/// sign-in, the recheck opens no window at all: it tries again silently at each recheck, says what happened in the log
/// and the findings (a refusal once until the workaround is on again), names the program that was in front as PoolWatch
/// saw the set-back, and leaves the tray icon to show that this sign-in is not protected, where a click turns it on.
/// Everything else (the workaround turned on, on again after something set it back, on after the user's click, not
/// needed, the guard off) is written to the log and listed in the findings, with no notice, and takes back what no
/// longer needs the click: the not-protected notice, and the click prompt when the guard reads the workaround on or is
/// turned off. A click prompt, as it opens, takes back a not-protected notice still open, since it asks for the same
/// click. The live foreground lock timeout, the program in front and the leak test are fakes: no test changes the
/// setting or starts a process, and no test shows a window.
/// </summary>
public sealed class TokenGuardNoticeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pw-notice-" + Guid.NewGuid().ToString("N"));
    private readonly List<Finding> _findings = new();

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>The Message of the outcome of a test that finds this build leaking (GuardPolicy.AfterTest).</summary>
    private const string LeakOutcome = "Token-leak guard: this build leaks; workaround ON, safe to start work.";
    private const string Leaks = LeakOutcome + " With it on, any app may take the focus.";
    private const string NotProtected = "Token-leak guard: NOT protected (Windows refused the change): click to turn it on.";
    private const string CheckFailedNotOn = "Token-leak guard: the check failed and the workaround is NOT on: click to turn it on.";
    private const string OnByClick = "Token-leak guard: workaround ON, safe to start work. With it on, any app may take the focus.";
    private const string StillNot = "Token-leak guard: still NOT protected: click to turn on.";
    private const string StillNotFromTray =
        "Token-leak guard: still NOT protected (Windows refused the change): click the PoolWatch icon by the clock to try again.";
    /// <summary>The program in front, as the fake says it (the guard's own look: no message came first).</summary>
    private const string Front = "Front.exe";
    private const string AtCheck =
        "Front.exe was the program in front when PoolWatch found it at its regular check, which can be minutes after the change " +
        "(this is not proof that it changed it)";
    private const string OnAgain = "Token-leak guard: the foreground lock timeout was set back to 20000 ms; " + AtCheck + "; turned the workaround on again.";
    private const string Refused =
        " Windows refused to turn the workaround on again from the background, so this sign-in is not protected. PoolWatch tries again " +
        "about every 3 minutes; a click on its icon by the clock turns it on now.";
    private const string NotAgain = "Token-leak guard: the foreground lock timeout was set back to 20000 ms; " + AtCheck + "." + Refused;
    /// <summary>What a recheck says when the workaround was not on before it (the sign-in check was refused, say), and Windows refuses again.</summary>
    private const string StillOff =
        "Token-leak guard: the workaround is not on (the foreground lock timeout is 200000 ms) and Windows refused to turn it on from the " +
        "background, so this sign-in is not protected. PoolWatch tries again about every 3 minutes; a click on its icon by the clock turns it on now.";
    private const string OnNow = "Token-leak guard: the workaround was not on (the foreground lock timeout was 200000 ms); turned it on.";

    /// <summary>Windows' live setting: refused (as from the background) while <see cref="Accept"/> is false.</summary>
    private sealed class Live
    {
        public uint? Value = 200000;
        public volatile bool Accept = true;
        /// <summary>How many sets the guard tried (accepted or not).</summary>
        public int Tries;
        /// <summary>Something sets the timeout back to 20000 ms right after each set the guard makes: its next look finds that.</summary>
        public bool SetBackAtOnce;
        /// <summary>Something sets the timeout back to 20000 ms right after the guard's next look finds it at 0 (once).</summary>
        public bool SetBackAfterLook;
        private bool _justSet;
        public void SetToZero() { Value = 0; _justSet = true; }
        /// <summary>Runs after each look the guard makes, with how many it made so far (a test steers a race with it).</summary>
        public Action<int>? Looked;
        private int _looks;
        public int Looks => Volatile.Read(ref _looks);
        /// <summary>The guard's next look throws, once (a read of the setting that fails).</summary>
        public volatile bool FailNextLook;
        public uint? Look()
        {
            if (FailNextLook)
            {
                FailNextLook = false;
                throw new InvalidOperationException("the setting could not be read");
            }
            if (SetBackAtOnce && _justSet) { _justSet = false; Value = 20000; }
            uint? seen = Value;
            if (SetBackAfterLook && seen == 0) { SetBackAfterLook = false; Value = 20000; }
            // Counted whether or not a test steers the looks (with ?., the count would be skipped while Looked is null).
            int looks = Interlocked.Increment(ref _looks);
            Looked?.Invoke(looks);
            return seen;
        }
    }

    /// <summary>
    /// Sets the live value to 0 when Windows accepts, and says whether it is 0 now: the app's set reads the value back
    /// (TokenGuardNative.SetForegroundLockTimeout), so a set Windows refuses still finds the workaround on when something
    /// else turned it on.
    /// </summary>
    private static bool SetZero(Live live)
    {
        Interlocked.Increment(ref live.Tries);
        if (live.Accept) live.SetToZero();
        return live.Value == 0;
    }

    /// <summary>
    /// What the guard put on screen, as GuardNotices keeps it: every not-protected notice it showed, the one open now (a new
    /// one replaces it; <see cref="IGuardUi.Withdraw"/> closes it), and the click prompts it asked.
    /// </summary>
    private abstract class Screen : IGuardUi
    {
        private readonly List<string> _notices = new();
        public int Asks;
        public string[] Notices { get { lock (_notices) return _notices.ToArray(); } }
        /// <summary>The not-protected notice on screen now; null once taken back (or before any).</summary>
        public volatile string? OpenNotice;
        /// <summary>Runs as a notice is shown, before it is on screen (GuardNotices puts it there with a post to the UI thread).</summary>
        public Action? OnShow;
        public void ShowNotProtected(string message)
        {
            lock (_notices) _notices.Add(message);
            OnShow?.Invoke();
            OpenNotice = message;
        }
        public abstract Task<bool> AskForClick(string message);
        public virtual void Withdraw(bool prompt) => OpenNotice = null;
    }

    /// <summary>The guard's UI with click prompts answered at once (<paramref name="onAsk"/> runs as each is shown).</summary>
    private sealed class Ui(Func<bool>? click = null, Action? onAsk = null) : Screen
    {
        public override Task<bool> AskForClick(string message)
        {
            Interlocked.Increment(ref Asks);
            onAsk?.Invoke();
            return Task.FromResult(click?.Invoke() ?? true);
        }
    }

    /// <summary>
    /// The guard's UI with a click prompt that stays open until <see cref="Click"/>, or until the guard takes it back, which
    /// completes it false as GuardNotices does. <paramref name="onAsk"/> runs before the prompt is on screen, as GuardNotices
    /// puts it there after AskForClick returns (a post to the UI thread).
    /// </summary>
    private sealed class OpenPrompt(Action? onAsk = null) : Screen
    {
        private readonly object _gate = new();
        private TaskCompletionSource<bool>? _click;
        public bool PromptOpen { get { lock (_gate) return _click is { Task.IsCompleted: false }; } }
        public override Task<bool> AskForClick(string message)
        {
            Interlocked.Increment(ref Asks);
            onAsk?.Invoke();
            lock (_gate) return (_click = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        public void Click(bool ok) { lock (_gate) _click!.SetResult(ok); }
        public override void Withdraw(bool prompt)
        {
            base.Withdraw(prompt);
            if (prompt) lock (_gate) _click?.TrySetResult(false);
        }
    }

    private string LogFile => Path.Combine(_root, "data", "g.log");
    private string StateFile => Path.Combine(_root, "data", "cache", "token-guard.json");

    private TokenLeakGuard Guard(Live live, Func<CancellationToken, (List<long> numbers, string? error)>? runTest = null, Action<Finding>? finding = null,
        Func<bool>? setNow = null, Func<long>? clockMs = null, Func<string?>? inFront = null) =>
        new(new AppPaths(Path.Combine(_root, "data"), null, secure: false), new Log(LogFile), finding ?? Record,
            setNow: setNow ?? (() => SetZero(live)), getLive: live.Look,
            attemptGap: TimeSpan.FromMilliseconds(1), clickRetryGap: TimeSpan.FromMilliseconds(1), build: () => "26220.7344",
            runTest: runTest ?? (_ => ([1, 0, 2, 100, 98, 101], null)), // this build leaks
            setTo: v => { if (live.Accept) live.Value = v; return live.Value == v; }, signIn: () => "7:700", clockMs: clockMs,
            inFront: inFront ?? (() => Front));

    private void Record(Finding f) { lock (_findings) _findings.Add(f); }

    /// <summary>A verdict saved for this build before the check.</summary>
    private void Saved(GuardResult result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        File.WriteAllText(StateFile, new GuardState("26220.7344", result, DateTime.UtcNow, "earlier").ToJson());
    }

    /// <summary>What the guard said, in each place it says it with no notice: its last message, the log and the findings.</summary>
    private void Said(TokenLeakGuard guard, string message, FindingSeverity severity)
    {
        Assert.Equal(message, guard.LastMessage);
        Assert.Contains(message, File.ReadAllText(LogFile));
        Finding last;
        lock (_findings) last = _findings.Last();
        Assert.Equal((FindingKind.TokenGuard, message, severity), (last.Kind, last.Title, last.Severity));
    }

    // ---------------------------------------------------------------- the logon check

    [Fact]
    public async Task ALogonCheckThatTurnsTheWorkaroundOnShowsNoNotice()
    {
        var live = new Live();
        var ui = new Ui();
        var guard = Guard(live);
        await guard.RunAtLogon(ui, CancellationToken.None);
        Assert.Equal(0u, live.Value);
        Assert.Equal(0, ui.Asks);
        Assert.Empty(ui.Notices);
        Said(guard, Leaks, FindingSeverity.Info);
    }

    [Fact]
    public async Task ALogonCheckThatTurnsTheWorkaroundOnAfterTheUsersClickShowsOnlyThePrompt()
    {
        // Windows refuses from the background; the prompt asks for the click, and with PoolWatch in front it accepts.
        var live = new Live { Accept = false };
        var ui = new Ui(click: () => true, onAsk: () => live.Accept = true);
        var guard = Guard(live);
        await guard.RunAtLogon(ui, CancellationToken.None);
        Assert.Equal(0u, live.Value);
        Assert.Equal(1, ui.Asks);
        Assert.Empty(ui.Notices);
        Said(guard, Leaks, FindingSeverity.Info);
    }

    [Fact]
    public async Task ALogonCheckThatWindowsRefusesShowsTheNotProtectedNotice()
    {
        // Windows refuses, and the user answers the prompt with Cancel: only the user's click can protect this sign-in now.
        var live = new Live { Accept = false };
        var ui = new Ui(click: () => false);
        var guard = Guard(live);
        await guard.RunAtLogon(ui, CancellationToken.None);
        Assert.Equal(200000u, live.Value);
        Assert.Equal(1, ui.Asks);
        Assert.Equal([NotProtected], ui.Notices);
        Said(guard, NotProtected, FindingSeverity.Warning);
    }

    [Fact]
    public async Task ALogonCheckThatNeedsNoWorkaroundOrFindsItOnShowsNoNotice()
    {
        var ui = new Ui();
        // The workaround is on at logon already.
        var already = Guard(new Live { Value = 0 });
        await already.RunAtLogon(ui, CancellationToken.None);
        Assert.Equal(GuardStep.AlreadyOn, already.Step);
        Said(already, "Token-leak guard: the workaround was already on at logon, nothing changed. Safe to start work.", FindingSeverity.Info);
        // The leak looks fixed: clean twice in a row on this build, so Windows is left alone.
        Saved(GuardResult.CleanOnce);
        var clean = new Live();
        var fixedBuild = Guard(clean, runTest: _ => ([0, 0, 1, 0, 1, 0], null));
        await fixedBuild.RunAtLogon(ui, CancellationToken.None);
        Assert.Equal(GuardResult.Fixed, fixedBuild.State!.Result);
        Assert.Equal(200000u, clean.Value);
        Said(fixedBuild, "Token-leak guard: the leak looks FIXED on this build (clean twice in a row); Windows left alone, will test again next logon.",
            FindingSeverity.Info);
        Assert.Equal(0, ui.Asks);
        Assert.Empty(ui.Notices);
    }

    [Fact]
    public async Task ALogonCheckWhoseResultCannotBeSavedTurnsTheWorkaroundOnWithNoNotice()
    {
        // A clean test on a build clean once before says fixed, but the result cannot be saved (a folder where the guard
        // writes its new state file), so the workaround goes on to be safe.
        Saved(GuardResult.CleanOnce);
        Directory.CreateDirectory(StateFile + ".tmp");
        var live = new Live();
        var ui = new Ui();
        var guard = Guard(live, runTest: _ => ([0, 0, 1, 0, 1, 0], null));
        await guard.RunAtLogon(ui, CancellationToken.None);
        Assert.Equal(0u, live.Value);
        Assert.Empty(ui.Notices);
        Said(guard, "Token-leak guard: the result could not be saved (see poolwatch.log); workaround ON to be safe. With it on, any app may take the focus.",
            FindingSeverity.Info);
    }

    [Fact]
    public async Task ALogonCheckTurnedOffWhileItChecksShowsNoNotice()
    {
        var live = new Live();
        var ui = new Ui();
        TokenLeakGuard? guard = null;
        guard = Guard(live, runTest: _ => { guard!.TurnOff(); return ([1, 0, 2, 100, 98, 101], null); });
        await guard.RunAtLogon(ui, CancellationToken.None);
        Assert.Equal(200000u, live.Value);
        Assert.Equal(0, ui.Asks);
        Assert.Empty(ui.Notices);
        Said(guard, TokenLeakGuard.TurnedOffDuringCheck, FindingSeverity.Info);
    }

    // ---------------------------------------------------------------- the logon check that fails

    [Fact]
    public async Task ACheckThatFailsButTurnsTheWorkaroundOnShowsNoNotice()
    {
        var live = new Live();
        var ui = new Ui();
        var guard = Guard(live, runTest: _ => throw new InvalidOperationException("the helper broke"));
        await guard.RunAtLogon(ui, CancellationToken.None);
        Assert.Equal(0u, live.Value);
        Assert.Empty(ui.Notices);
        Said(guard, "Token-leak guard: the check failed (see poolwatch.log), but the workaround is ON.", FindingSeverity.Warning);
    }

    [Fact]
    public async Task ACheckThatFailsAndCannotTurnTheWorkaroundOnShowsTheNotice()
    {
        var live = new Live { Accept = false };
        var ui = new Ui();
        var guard = Guard(live, runTest: _ => throw new InvalidOperationException("the helper broke"));
        await guard.RunAtLogon(ui, CancellationToken.None);
        Assert.Equal(200000u, live.Value);
        Assert.Equal([CheckFailedNotOn], ui.Notices);
        Said(guard, CheckFailedNotOn, FindingSeverity.Warning);
    }

    [Fact]
    public async Task ALogonCheckStoppedWhileItWaitsBetweenTriesSaysThatItFailed()
    {
        // PoolWatch stops while the logon check waits between two tries (Windows refused the first). The check had decided
        // (this build leaks, and that is saved), but what it says is that it failed and the workaround is not on, not the
        // outcome's words nor that Windows refused (GuardOutcome's summary names this case): its last message, the log, the
        // findings and the notice, the one thing on screen (the stop comes before any prompt).
        var live = new Live { Accept = false };
        var ui = new Ui();
        using var stop = new CancellationTokenSource();
        var guard = Guard(live, setNow: () => { stop.Cancel(); return SetZero(live); });
        await guard.RunAtLogon(ui, stop.Token);
        Assert.Equal(GuardResult.Leak, guard.State!.Result);
        Assert.Equal(GuardResult.Leak, GuardState.Parse(File.ReadAllText(StateFile))!.Result);
        Assert.Equal(200000u, live.Value);
        Said(guard, CheckFailedNotOn, FindingSeverity.Warning);
        Assert.Equal(0, ui.Asks);
        Assert.Equal([CheckFailedNotOn], ui.Notices);
        string log = File.ReadAllText(LogFile);
        Assert.Contains("token guard failed: System.Threading.Tasks.TaskCanceledException", log);
        Assert.DoesNotContain(LeakOutcome, log);
        Assert.DoesNotContain("Windows refused", log);
        lock (_findings)
            Assert.All(_findings, f => Assert.False(f.Title.Contains(LeakOutcome) || f.Title.Contains("Windows refused"), f.Title));
    }

    [Fact]
    public async Task ACheckThatFailsAfterSavingAnOutcomeThatTurnsNothingOnShowsNoNotice()
    {
        // The outcome is saved, then saying it fails (the timeline is full once): the outcome turned nothing on (on
        // already, or the leak looks fixed), so nothing needs the user's click, and nothing is changed.
        int said = 0;
        void FullOnce(Finding f) { if (Interlocked.Increment(ref said) == 1) throw new InvalidOperationException("the timeline is full"); Record(f); }
        var ui = new Ui();
        var on = new Live { Value = 0 };
        var already = Guard(on, finding: FullOnce);
        await already.RunAtLogon(ui, CancellationToken.None);
        Assert.Equal(GuardStep.AlreadyOn, already.Step);
        Assert.Equal(0u, on.Value);
        Said(already, TokenLeakGuard.FailedAfterSaved, FindingSeverity.Warning);

        Saved(GuardResult.CleanOnce);
        said = 0;
        var clean = new Live();
        var fixedBuild = Guard(clean, runTest: _ => ([0, 0, 1, 0, 1, 0], null), finding: FullOnce);
        await fixedBuild.RunAtLogon(ui, CancellationToken.None);
        Assert.Equal(GuardResult.Fixed, GuardState.Parse(File.ReadAllText(StateFile))!.Result);
        Assert.Equal(200000u, clean.Value);
        Said(fixedBuild, TokenLeakGuard.FailedAfterSaved, FindingSeverity.Warning);
        Assert.Empty(ui.Notices);
    }

    [Fact]
    public async Task ACheckThatFailsAfterItWasTurnedOffShowsNoNotice()
    {
        var live = new Live();
        var ui = new Ui();
        int said = 0;
        TokenLeakGuard? guard = null;
        guard = Guard(live, runTest: _ => { guard!.TurnOff(); return ([1, 0, 2, 100, 98, 101], null); },
            finding: f => { if (Interlocked.Increment(ref said) == 1) throw new InvalidOperationException("the timeline is full"); Record(f); });
        await guard.RunAtLogon(ui, CancellationToken.None);
        Assert.Equal(200000u, live.Value);
        Assert.Empty(ui.Notices);
        Said(guard, TokenLeakGuard.TurnedOffDuringCheck, FindingSeverity.Info);
    }

    // ---------------------------------------------------------------- Turn on in a notice

    [Fact]
    public async Task TurnOnThatTurnsTheWorkaroundOnShowsNoNotice()
    {
        var live = new Live();
        var ui = new Ui();
        var guard = Guard(live);
        Assert.True(await guard.TurnOnByOwner(ui));
        Assert.Equal(0u, live.Value);
        Assert.Equal(0, ui.Asks);
        Assert.Empty(ui.Notices);
        Said(guard, OnByClick, FindingSeverity.Info);
    }

    [Fact]
    public async Task TurnOnThatWindowsStillRefusesShowsTheNoticeAgain()
    {
        var live = new Live { Accept = false };
        var ui = new Ui(click: () => false);
        var guard = Guard(live);
        Assert.False(await guard.TurnOnByOwner(ui));
        Assert.Equal(1, ui.Asks);
        Assert.Equal([StillNot], ui.Notices);
        Said(guard, StillNot, FindingSeverity.Warning);
    }

    [Fact]
    public async Task TurnOnWithTheGuardOffShowsNoNotice()
    {
        var live = new Live();
        var ui = new Ui();
        var guard = Guard(live);
        guard.TurnOff();
        Assert.False(await guard.TurnOnByOwner(ui));
        Assert.Equal(200000u, live.Value);
        Assert.Empty(ui.Notices);
        Said(guard, TokenLeakGuard.IsOff, FindingSeverity.Info);
    }

    // ---------------------------------------------------------------- the recheck

    /// <summary>A guard whose logon check turned the workaround on; then something sets it back to 20000 ms.</summary>
    private async Task<(TokenLeakGuard guard, Live live)> SetBack()
    {
        var live = new Live();
        var guard = Guard(live);
        await guard.RunAtLogon(null, CancellationToken.None);
        Assert.Equal(0u, live.Value);
        live.Value = 20000;
        return (guard, live);
    }

    [Fact]
    public async Task ARecheckThatTurnsTheWorkaroundOnAgainShowsNoNotice()
    {
        var (guard, live) = await SetBack();
        var ui = new Ui();
        guard.Recheck(TimeSpan.FromMinutes(3), () => ui);
        Assert.Equal(0u, live.Value);
        Assert.Equal(0, ui.Asks);
        Assert.Empty(ui.Notices);
        Said(guard, OnAgain, FindingSeverity.Warning);
        Assert.False(guard.NotProtectedNow());
    }

    [Fact]
    public async Task ARecheckThatWindowsRefusesOpensNoWindowAndTriesAgainAtEachRecheck()
    {
        // Something sets the timeout back and Windows refuses the recheck's set from the background. The recheck asks for
        // no click and shows no notice (a click, were one asked for, would be answered OK and turn it on); it says so in
        // the log and the findings, and the tray shows the sign-in not protected. Each later recheck tries again: refused,
        // it says so in the log only (the findings said it once); accepted, it says so, and the tray shows nothing. A new
        // set-back is said again.
        long ms = 0;
        var every = TimeSpan.FromMinutes(3);
        var live = new Live();
        var guard = Guard(live, clockMs: () => ms);
        await guard.RunAtLogon(null, CancellationToken.None);
        Assert.Equal(0u, live.Value);
        live.Value = 20000;
        live.Accept = false;
        var ui = new Ui(click: () => true, onAsk: () => live.Accept = true);
        int tries = live.Tries;
        guard.Recheck(every, () => ui);
        Assert.Equal(tries + 1, live.Tries);
        Assert.Equal(20000u, live.Value);
        Assert.Equal(0, ui.Asks);
        Assert.Empty(ui.Notices);
        Said(guard, NotAgain, FindingSeverity.Warning);
        Assert.True(guard.NotProtectedNow());
        int findings;
        lock (_findings) findings = _findings.Count;

        ms += (long)every.TotalMilliseconds;
        guard.Recheck(every, () => ui);
        Assert.Equal(tries + 2, live.Tries);
        Assert.Equal(0, ui.Asks);
        Assert.Empty(ui.Notices);
        lock (_findings) Assert.Equal(findings, _findings.Count);
        Assert.Contains("token guard: the live timeout is still 20000 ms; Windows refused again to turn the workaround on from the background",
            File.ReadAllText(LogFile));
        Assert.True(guard.NotProtectedNow());

        live.Accept = true; // PoolWatch is in front at the next recheck, say
        ms += (long)every.TotalMilliseconds;
        guard.Recheck(every, () => ui);
        Assert.Equal(0u, live.Value);
        Said(guard, OnAgain, FindingSeverity.Warning);
        Assert.False(guard.NotProtectedNow());

        live.Value = 20000;
        live.Accept = false;
        ms += (long)every.TotalMilliseconds;
        guard.Recheck(every, () => ui);
        Said(guard, NotAgain, FindingSeverity.Warning);
        Assert.Equal(0, ui.Asks);
        Assert.Empty(ui.Notices);
    }

    [Fact]
    public async Task ARecheckWithNoTrayOffersNoClick()
    {
        // A headless run (no UI) has no tray icon to click: what it says offers none, only that it tries again.
        var (guard, live) = await SetBack();
        live.Accept = false;
        guard.Recheck(TimeSpan.FromMinutes(3), () => null);
        Assert.Equal(20000u, live.Value);
        Said(guard, "Token-leak guard: the foreground lock timeout was set back to 20000 ms; " + AtCheck + ". Windows refused to turn the " +
            "workaround on again from the background, so this sign-in is not protected. PoolWatch tries again about every 3 minutes.", FindingSeverity.Warning);
    }

    // ---------------------------------------------------------------- the program in front

    [Fact]
    public async Task ASetBackSeenAsWindowsSaysASettingChangedNamesTheProgramInFrontThen()
    {
        // Something sets the timeout back and Windows says a setting changed (the tray app's window for Windows' events):
        // the guard looks then, and keeps the program in front. By the recheck another program is in front: the log, at
        // once, and the recheck's finding name the one in front as PoolWatch saw the change, as the program in front, which
        // Windows lets change this setting, not as proof. A message for another setting (the value unchanged) and the
        // guard's own set (0) change nothing of it.
        string front = "Setter.exe";
        long ms = 0;
        var live = new Live();
        var guard = Guard(live, clockMs: () => ms, inFront: () => front);
        await guard.RunAtLogon(null, CancellationToken.None);
        live.Value = 2147483647;
        guard.SettingChanged();
        const string seen = "the foreground lock timeout was set back to 2147483647 ms; Setter.exe was the program in front as PoolWatch saw the change " +
                            "(Windows lets the program in front change this setting; this is not proof that it changed it)";
        Assert.Contains("token guard: " + seen, File.ReadAllText(LogFile));
        front = "Other.exe";
        guard.SettingChanged(); // a message for another setting: the value has not changed
        live.Accept = false;
        guard.Recheck(TimeSpan.FromMinutes(3), () => new Ui());
        Said(guard, "Token-leak guard: " + seen + "." + Refused, FindingSeverity.Warning);
        Finding last;
        lock (_findings) last = _findings.Last();
        Assert.Equal("Setter.exe", last.Data["inFront"]);
        Assert.Equal("as the setting changed", last.Data["inFrontSeen"]);
        Assert.DoesNotContain("Other.exe", File.ReadAllText(LogFile));

        // On again at the next recheck (accepted, with its own setting-change message, which reads 0): the next set-back is a
        // new one.
        live.Accept = true;
        ms += 180_000;
        guard.Recheck(TimeSpan.Zero, () => null);
        guard.SettingChanged();
        Assert.Equal(0u, live.Value);
        live.Value = 30000;
        guard.Recheck(TimeSpan.Zero, () => null);
        Said(guard, "Token-leak guard: the foreground lock timeout was set back to 30000 ms; Other.exe was the program in front when PoolWatch found " +
                    "it at its regular check, which can be minutes after the change (this is not proof that it changed it); turned the workaround on again.",
            FindingSeverity.Warning);
        lock (_findings) last = _findings.Last();
        Assert.Equal("Other.exe", last.Data["inFront"]);
        Assert.Equal("at the regular check", last.Data["inFrontSeen"]);
    }

    [Fact]
    public async Task ASetBackFoundAtTheRecheckNamesTheProgramInFrontAtThatLook()
    {
        // No setting-change message came first: the recheck names the program in front as it looked, and says that this
        // can be minutes after the change.
        var (guard, live) = await SetBack();
        guard.Recheck(TimeSpan.FromMinutes(3), () => null);
        Said(guard, OnAgain, FindingSeverity.Warning);
        Finding last;
        lock (_findings) last = _findings.Last();
        Assert.Equal(Front, last.Data["inFront"]);
        Assert.Equal("at the regular check", last.Data["inFrontSeen"]);
        Assert.Equal(0u, live.Value);
    }

    [Fact]
    public async Task ASetBackWhoseProgramInFrontCannotBeToldSaysSo()
    {
        // The program in front cannot be told (none is in front, or reading it throws): the guard says so, and names none.
        foreach (Func<string?> inFront in new Func<string?>[] { () => null, () => throw new InvalidOperationException("no foreground window") })
        {
            lock (_findings) _findings.Clear();
            var live = new Live();
            var guard = Guard(live, inFront: inFront);
            await guard.RunAtLogon(null, CancellationToken.None);
            live.Value = 20000;
            guard.SettingChanged();
            live.Value = 0;
            guard.SettingChanged();
            live.Value = 20000;
            guard.Recheck(TimeSpan.FromMinutes(3), () => null);
            Said(guard, "Token-leak guard: the foreground lock timeout was set back to 20000 ms; PoolWatch could not tell which program was in front when " +
                        "it found it at its regular check; turned the workaround on again.", FindingSeverity.Warning);
            Finding last;
            lock (_findings) last = _findings.Last();
            Assert.Null(last.Data["inFront"]);
            File.Delete(StateFile);
        }
    }

    [Fact]
    public async Task WindowsSayingASettingChangedReadsNothingWhileTheGuardIsNotInUse()
    {
        // The tray app passes on every setting-change message: with the guard never started, or turned off, it reads nothing
        // and keeps nothing; in use, it reads the setting once.
        var live = new Live();
        var idle = Guard(live);
        int before = live.Looks;
        idle.SettingChanged();
        Assert.Equal(before, live.Looks);
        var guard = Guard(live);
        await guard.RunAtLogon(null, CancellationToken.None);
        int inUse = live.Looks;
        guard.SettingChanged();
        Assert.Equal(inUse + 1, live.Looks); // the count is real: a look in use is counted
        guard.TurnOff();
        int looks = live.Looks;
        live.Value = 20000;
        guard.SettingChanged();
        Assert.Equal(looks, live.Looks);
    }

    /// <summary>
    /// With the guard off, the tray never shows the sign-in not protected, also when what the guard last said asked for
    /// the click: a report that asks for it can land just after a Turn off (one under way as the guard is turned off), and
    /// this test puts the guard in that state directly, as no seam reaches that moment.
    /// </summary>
    [Fact]
    public async Task TheTrayShowsNothingWithTheGuardOffEvenWhenItsLastWordAskedForTheClick()
    {
        var live = new Live();
        var guard = Guard(live);
        await guard.RunAtLogon(null, CancellationToken.None);
        guard.TurnOff();
        typeof(TokenLeakGuard).GetField("_clickAsked", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(guard, true);
        live.Value = 20000;
        Assert.False(guard.NotProtectedNow());
        guard.TurnedOn(); // on again, the same state shows it
        Assert.True(guard.NotProtectedNow());
    }

    // ---------------------------------------------------------------- one prompt open already

    [Fact]
    public async Task NoNoticeWhileTheGuardsClickPromptIsOpenAlready()
    {
        // Turn on opens the click prompt (Windows refuses from the background). While it is open, a second Turn on, a
        // logon check and a click on the tray icon are refused too: each opens no second prompt and shows no notice, since
        // the open prompt asks for the same click; a recheck, refused too, never opens either. The click then turns the
        // workaround on, with no notice either.
        var live = new Live { Accept = false };
        var ui = new OpenPrompt();
        var guard = Guard(live);
        var first = guard.TurnOnByOwner(ui);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref ui.Asks) == 1, TimeSpan.FromSeconds(10)));

        Assert.False(await guard.TurnOnByOwner(ui));
        Assert.Empty(ui.Notices);
        Said(guard, StillNot, FindingSeverity.Warning);

        await guard.RunAtLogon(ui, CancellationToken.None);
        Assert.Empty(ui.Notices);
        Said(guard, NotProtected, FindingSeverity.Warning);

        Assert.False(await guard.TurnOnFromTray(ui));
        Assert.Empty(ui.Notices);
        Said(guard, StillNotFromTray, FindingSeverity.Warning);

        guard.Recheck(TimeSpan.FromMinutes(3), () => ui);
        Assert.Empty(ui.Notices);
        Said(guard, StillOff, FindingSeverity.Warning);
        Assert.Equal(1, ui.Asks);

        live.Accept = true;
        ui.Click(true);
        Assert.True(await first);
        Assert.Equal(0u, live.Value);
        Assert.Empty(ui.Notices);
        Said(guard, OnByClick, FindingSeverity.Info);
    }

    [Fact]
    public async Task ACheckThatFailsWhileAClickPromptIsUnderWayShowsNoNoticeOfItsOwn()
    {
        // Turn on's prompt is open (Windows refuses from the background) when a logon check fails and cannot turn the
        // workaround on: the open prompt asks for the same click, so the check shows no notice of its own.
        var live = new Live { Accept = false };
        var ui = new OpenPrompt();
        var guard = Guard(live, runTest: _ => throw new InvalidOperationException("the helper broke"));
        var first = guard.TurnOnByOwner(ui);
        Assert.True(SpinWait.SpinUntil(() => ui.PromptOpen, TimeSpan.FromSeconds(10)));
        await guard.RunAtLogon(ui, CancellationToken.None);
        Assert.True(guard.Prompting);
        Assert.Empty(ui.Notices);
        Said(guard, CheckFailedNotOn, FindingSeverity.Warning);
        live.Accept = true;
        ui.Click(true);
        Assert.True(await first);
        Assert.Empty(ui.Notices);
    }

    // ---------------------------------------------------------------- what no longer needs the click goes

    [Fact]
    public async Task TheLogonChecksNoticeStaysWhileTheRecheckIsRefusedAndNoSecondPromptOpens()
    {
        // The logon check's prompt is answered with Cancel, so the not-protected notice opens, and the user leaves it. The
        // next recheck is refused from the background too: it asks for no click and shows nothing new, and the notice,
        // still true, stays. The tray shows the sign-in not protected meanwhile.
        var live = new Live { Accept = false };
        var ui = new Ui(click: () => true, onAsk: () => live.Accept = true); // were the recheck to ask, the click would work
        var guard = Guard(live);
        bool cancelled = false;
        var check = new Ui(click: () => { cancelled = true; return false; });
        await guard.RunAtLogon(check, CancellationToken.None);
        Assert.True(cancelled);
        Assert.Equal(NotProtected, check.OpenNotice);
        Assert.True(guard.NotProtectedNow());
        int tries = live.Tries;
        guard.Recheck(TimeSpan.FromMinutes(3), () => ui);
        Assert.Equal(tries + 1, live.Tries);
        Assert.Equal(200000u, live.Value);
        Assert.Equal(NotProtected, check.OpenNotice);
        Assert.Equal([NotProtected], check.Notices);
        Assert.Equal(1, check.Asks);
        Assert.Equal(0, ui.Asks);
        Assert.Empty(ui.Notices);
        Said(guard, StillOff, FindingSeverity.Warning);
        Assert.True(guard.NotProtectedNow());
    }

    [Fact]
    public async Task TheNotProtectedNoticeGoesAsAClickPromptOpens()
    {
        // The logon check's prompt is answered with Cancel, so the not-protected notice opens, and the user leaves it. Its
        // Turn on is refused from the background too and asks for the click again: that prompt asks for the same click, so
        // the notice goes as it opens (taken back before the prompt is asked for, so GuardNotices, which runs both in the
        // order they were made, closes the notice before it shows the prompt), and a notice comes again once the prompt is
        // answered with Cancel too.
        var live = new Live { Accept = false };
        OpenPrompt? ui = null;
        string? atSecondAsk = "not asked";
        ui = new OpenPrompt(onAsk: () => { if (Volatile.Read(ref ui!.Asks) == 2) atSecondAsk = ui.OpenNotice; });
        var guard = Guard(live);
        var check = guard.RunAtLogon(ui, CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => ui.PromptOpen, TimeSpan.FromSeconds(10)));
        ui.Click(false);
        await check.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(NotProtected, ui.OpenNotice);
        var turnOn = guard.TurnOnByOwner(ui);
        Assert.True(SpinWait.SpinUntil(() => ui.PromptOpen, TimeSpan.FromSeconds(10)));
        Assert.Equal(2, ui.Asks);
        Assert.Null(atSecondAsk);
        Assert.Null(ui.OpenNotice);
        ui.Click(false);
        Assert.False(await turnOn.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(StillNot, ui.OpenNotice);
        Assert.Equal([NotProtected, StillNot], ui.Notices);
        Said(guard, StillNot, FindingSeverity.Warning);
    }

    [Fact]
    public async Task TheNotProtectedNoticeGoesOnceTheRecheckTurnsTheWorkaroundOnByItself()
    {
        // The notice is left open; later Windows accepts the recheck's silent set (PoolWatch is in front then, say): the
        // notice goes, and nothing asks for a click.
        var live = new Live { Accept = false };
        var ui = new Ui(click: () => false);
        var guard = Guard(live);
        await guard.RunAtLogon(ui, CancellationToken.None);
        Assert.Equal(NotProtected, ui.OpenNotice);
        live.Accept = true;
        guard.Recheck(TimeSpan.FromMinutes(3), () => ui);
        Assert.Equal(0u, live.Value);
        Assert.Equal(1, ui.Asks);
        Assert.Null(ui.OpenNotice);
        Assert.Equal([NotProtected], ui.Notices);
        Said(guard, OnNow, FindingSeverity.Warning);
        Assert.False(guard.NotProtectedNow());
    }

    [Fact]
    public async Task TheNotProtectedNoticeGoesOnceTheRecheckFindsTheWorkaroundOnAlready()
    {
        // A logon check fails and cannot turn the workaround on (the notice opens; it never got as far as wanting the
        // workaround, so the recheck does not set it, but its words ask for the click, so the tray shows the sign-in not
        // protected). Then another program sets the timeout to 0: the recheck finds it on, changes nothing and says
        // nothing, but the notice goes, and the click it asked for is no longer asked: set back once more, the tray shows
        // nothing, as for any guard that does not want the workaround.
        var live = new Live { Accept = false };
        var ui = new Ui();
        var guard = Guard(live, runTest: _ => throw new InvalidOperationException("the helper broke"));
        await guard.RunAtLogon(ui, CancellationToken.None);
        Assert.False(guard.WorkaroundWanted);
        Assert.Equal(CheckFailedNotOn, ui.OpenNotice);
        Assert.True(guard.NotProtectedNow());
        int findings;
        lock (_findings) findings = _findings.Count;
        live.Value = 0;
        guard.Recheck(TimeSpan.FromMinutes(3), () => ui);
        Assert.Null(ui.OpenNotice);
        Assert.Equal([CheckFailedNotOn], ui.Notices);
        lock (_findings) Assert.Equal(findings, _findings.Count);
        Assert.Equal(CheckFailedNotOn, guard.LastMessage);
        live.Value = 20000;
        Assert.False(guard.NotProtectedNow());
    }

    [Fact]
    public async Task TheNotProtectedNoticeStaysWhenTheTimeoutIsSetBackAsTheRecheckFindsItOn()
    {
        // The recheck finds the timeout at 0, and right then something sets it back: the notice that says NOT protected
        // is true again, so it stays (the recheck looks again under the lock every notice takes before it takes one back).
        var live = new Live { Accept = false };
        var ui = new Ui(click: () => false);
        var guard = Guard(live);
        await guard.RunAtLogon(ui, CancellationToken.None);
        Assert.Equal(NotProtected, ui.OpenNotice);
        live.Value = 0;
        live.SetBackAfterLook = true;
        guard.Recheck(TimeSpan.FromMinutes(3), () => ui);
        Assert.Equal(20000u, live.Value);
        Assert.Equal(NotProtected, ui.OpenNotice);
    }

    [Fact]
    public async Task ANoticeShownWhileTheRecheckTakesOneBackStays()
    {
        // The recheck finds the timeout at 0 and looks again before it takes the notice back; right then the timeout is
        // set back and the user's Turn on (on another thread) is refused, so it shows the notice again. That notice comes
        // after the take-back, under the same lock, and stays: it is true.
        var live = new Live { Accept = false };
        var ui = new Ui(click: () => false);
        var guard = Guard(live);
        await guard.RunAtLogon(ui, CancellationToken.None);
        Assert.Equal(NotProtected, ui.OpenNotice);
        live.Value = 0;
        Task<bool>? turnOn = null;
        bool heldBack = false;
        int second = live.Looks + 2; // the recheck's own look, then the one before it takes the notice back
        live.Looked = n =>
        {
            if (n != second) return;
            live.Value = 20000;
            turnOn = Task.Run(() => guard.TurnOnByOwner(ui));
            heldBack = !turnOn.Wait(TimeSpan.FromSeconds(1));
        };
        guard.Recheck(TimeSpan.FromMinutes(3), () => ui);
        Assert.False(await turnOn!.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(heldBack);
        Assert.Equal(StillNot, ui.OpenNotice);
        Assert.Equal([NotProtected, StillNot], ui.Notices);
    }

    [Fact]
    public async Task AReportDecidesFromTheValueItReadsItself()
    {
        // The recheck reads the timeout at 0 (another program turned the workaround on); before it writes down what it
        // read, the timeout is set back and Turn on, refused, says it is still not protected. The recheck's 0 is written
        // while Turn on's report decides: the report decides from the 20000 ms it read itself (the click is needed), so the
        // notice shows, and the recheck, which looks again under the lock, leaves it: it is true.
        var live = new Live { Value = 0 };
        var ui = new Ui(click: () => false);
        using var recheckLooked = new ManualResetEventSlim();
        using var recheckGoesOn = new ManualResetEventSlim();
        TokenLeakGuard? guard = null;
        var recheck = new Thread(() => guard!.Recheck(TimeSpan.FromMinutes(3), () => ui)) { IsBackground = true };
        int recheckThread = recheck.ManagedThreadId;
        live.Looked = _ =>
        {
            if (Environment.CurrentManagedThreadId != recheckThread || recheckLooked.IsSet) return;
            recheckLooked.Set();
            recheckGoesOn.Wait(TimeSpan.FromSeconds(10));
        };
        bool written = false;
        guard = Guard(live, finding: f =>
        {
            if (f.Title == StillNot)
            {
                recheckGoesOn.Set();
                written = SpinWait.SpinUntil(() => guard!.LiveTimeout == 0, TimeSpan.FromSeconds(10));
            }
            Record(f);
        });
        recheck.Start();
        Assert.True(recheckLooked.Wait(TimeSpan.FromSeconds(10)));
        live.Value = 20000;
        live.Accept = false;
        Assert.False(await guard.TurnOnByOwner(ui));
        Assert.True(recheck.Join(TimeSpan.FromSeconds(10)));
        Assert.True(written);
        Assert.Equal(20000u, live.Value);
        Assert.Equal(StillNot, ui.OpenNotice);
        Assert.Equal([StillNot], ui.Notices);
        Said(guard, StillNot, FindingSeverity.Warning);
    }

    [Fact]
    public async Task AReportThatReadsTheWorkaroundOffLeavesAnOpenPrompt()
    {
        // Turn on's prompt is open (Windows refuses from the background). The recheck reads the timeout at 0 (another
        // program turned the workaround on); before it writes down what it read, the timeout is set back, and a second Turn
        // on is accepted, but the timeout is set back again at once. That Turn on's report reads 20000 ms while the
        // recheck's 0 is written: the click is still needed, so the open prompt stays, and the recheck, which looks again
        // under the lock, leaves it too.
        var live = new Live { Accept = false };
        var ui = new OpenPrompt();
        using var recheckLooked = new ManualResetEventSlim();
        using var recheckGoesOn = new ManualResetEventSlim();
        TokenLeakGuard? guard = null;
        var recheck = new Thread(() => guard!.Recheck(TimeSpan.FromMinutes(3), () => ui)) { IsBackground = true };
        int recheckThread = recheck.ManagedThreadId;
        live.Looked = _ =>
        {
            if (Environment.CurrentManagedThreadId != recheckThread || recheckLooked.IsSet) return;
            recheckLooked.Set();
            recheckGoesOn.Wait(TimeSpan.FromSeconds(10));
        };
        bool written = false;
        guard = Guard(live, finding: f =>
        {
            if (f.Title == OnByClick && !recheckGoesOn.IsSet)
            {
                recheckGoesOn.Set();
                written = SpinWait.SpinUntil(() => guard!.LiveTimeout == 0, TimeSpan.FromSeconds(10));
            }
            Record(f);
        });
        var first = guard.TurnOnByOwner(ui);
        Assert.True(SpinWait.SpinUntil(() => ui.PromptOpen, TimeSpan.FromSeconds(10)));
        live.Value = 0;
        recheck.Start();
        Assert.True(recheckLooked.Wait(TimeSpan.FromSeconds(10)));
        live.Value = 20000;
        live.Accept = true;
        live.SetBackAtOnce = true;
        Assert.True(await guard.TurnOnByOwner(ui));
        Assert.True(recheck.Join(TimeSpan.FromSeconds(10)));
        Assert.True(written);
        Assert.Equal(20000u, live.Value);
        Assert.True(ui.PromptOpen);
        Assert.Empty(ui.Notices);
        ui.Click(true);
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, ui.Asks);
        Assert.Empty(ui.Notices);
    }

    [Fact]
    public async Task TurningTheGuardOffTakesBackItsNotProtectedNotice()
    {
        // A logon check that fails and cannot turn the workaround on shows the notice (it asked for no click before); the
        // user then turns the guard off (Settings, Active changes): nothing needs the click any more, so the notice goes.
        var live = new Live { Accept = false };
        var ui = new Ui();
        var guard = Guard(live, runTest: _ => throw new InvalidOperationException("the helper broke"));
        await guard.RunAtLogon(ui, CancellationToken.None);
        Assert.Equal(0, ui.Asks);
        Assert.Equal(CheckFailedNotOn, ui.OpenNotice);
        guard.TurnOff();
        Assert.Null(ui.OpenNotice);
        Assert.Equal([CheckFailedNotOn], ui.Notices);
    }

    [Fact]
    public async Task TurningTheGuardOffTakesBackItsOpenClickPrompt()
    {
        // Turn on's prompt is open when the guard is turned off: its click would change nothing, so the prompt goes, and
        // Turn on says the guard is off, with no notice.
        var live = new Live { Accept = false };
        var ui = new OpenPrompt();
        var guard = Guard(live);
        var first = guard.TurnOnByOwner(ui);
        Assert.True(SpinWait.SpinUntil(() => ui.PromptOpen, TimeSpan.FromSeconds(10)));
        guard.TurnOff();
        Assert.False(ui.PromptOpen);
        Assert.False(await first.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, ui.Asks);
        Assert.Empty(ui.Notices);
        Said(guard, TokenLeakGuard.IsOff, FindingSeverity.Info);
    }

    [Fact]
    public async Task TurningTheGuardOffTakesBackItsClickPromptAlsoWhenPuttingTheSettingBackThrows()
    {
        // Turn on's prompt is open when the guard is turned off, and putting the setting back throws (the fake's read of
        // the setting fails once): the exception goes on to Turn off's caller, and the prompt still goes, so none stays
        // open with the guard off.
        var live = new Live { Accept = false };
        var ui = new OpenPrompt();
        var guard = Guard(live);
        var first = guard.TurnOnByOwner(ui);
        Assert.True(SpinWait.SpinUntil(() => ui.PromptOpen, TimeSpan.FromSeconds(10)));
        live.FailNextLook = true;
        Assert.Throws<InvalidOperationException>(() => guard.TurnOff());
        Assert.True(guard.Off);
        Assert.False(ui.PromptOpen);
        Assert.False(await first.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, ui.Asks);
        Assert.Empty(ui.Notices);
        Said(guard, TokenLeakGuard.IsOff, FindingSeverity.Info);
    }

    [Fact]
    public async Task TurnOffWhileThePromptIsBeingShownStillTakesItBack()
    {
        // Turn off comes from another thread (the Settings window) just as Turn on puts its prompt on screen. It waits for
        // the prompt to be shown, then takes it back, so no prompt stays open with the guard off.
        var live = new Live { Accept = false };
        TokenLeakGuard? guard = null;
        Task<string>? turnOff = null;
        bool heldBack = false;
        var ui = new OpenPrompt(onAsk: () =>
        {
            turnOff = Task.Run(() => guard!.TurnOff());
            heldBack = !turnOff.Wait(TimeSpan.FromSeconds(1));
        });
        guard = Guard(live);
        var first = guard.TurnOnByOwner(ui);
        Assert.False(await first.WaitAsync(TimeSpan.FromSeconds(10)));
        await turnOff!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(heldBack);
        Assert.True(guard.Off);
        Assert.False(ui.PromptOpen);
        Assert.Equal(1, ui.Asks);
        Assert.Empty(ui.Notices);
        Said(guard, TokenLeakGuard.IsOff, FindingSeverity.Info);
    }

    [Fact]
    public async Task TurnOffWhileTheNoticeIsBeingShownStillTakesItBack()
    {
        // Turn off comes from another thread just as the logon check puts its not-protected notice on screen. It waits for
        // the notice to be shown, then takes it back, so no notice stays open with the guard off.
        var live = new Live { Accept = false };
        TokenLeakGuard? guard = null;
        Task<string>? turnOff = null;
        bool heldBack = false;
        var ui = new Ui(click: () => false)
        {
            OnShow = () =>
            {
                turnOff = Task.Run(() => guard!.TurnOff());
                heldBack = !turnOff.Wait(TimeSpan.FromSeconds(1));
            },
        };
        guard = Guard(live);
        await guard.RunAtLogon(ui, CancellationToken.None);
        await turnOff!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(heldBack);
        Assert.True(guard.Off);
        Assert.Null(ui.OpenNotice);
        Assert.Equal([NotProtected], ui.Notices);
    }

    [Fact]
    public async Task AClickPromptGoesOnceAnotherPartOfTheGuardTurnsTheWorkaroundOn()
    {
        // Turn on's prompt is open when the recheck's silent set works (PoolWatch is in front then, say): the prompt goes,
        // and Turn on finds the workaround on, with no notice.
        var (guard, live) = await SetBack();
        live.Accept = false;
        var ui = new OpenPrompt();
        var first = guard.TurnOnByOwner(ui);
        Assert.True(SpinWait.SpinUntil(() => ui.PromptOpen, TimeSpan.FromSeconds(10)));
        live.Accept = true;
        guard.Recheck(TimeSpan.FromMinutes(3), () => ui);
        Assert.Equal(0u, live.Value);
        Assert.False(ui.PromptOpen);
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, ui.Asks);
        Assert.Empty(ui.Notices);
        Said(guard, OnByClick, FindingSeverity.Info);
    }

    [Fact]
    public async Task TheLogonChecksPromptGoesOnceTheRecheckFindsTheWorkaroundOnWhileItChecks()
    {
        // Windows refuses the logon check's tries, so its prompt opens. A recheck while the check runs sets nothing and asks
        // for nothing (the check turns the workaround on itself). Once another program sets the timeout to 0, the next
        // recheck finds the workaround on and takes the check's prompt back, and the check, trying once more, finds the
        // workaround on and shows nothing.
        var live = new Live { Accept = false };
        var ui = new OpenPrompt();
        var guard = Guard(live);
        var check = guard.RunAtLogon(ui, CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => ui.PromptOpen, TimeSpan.FromSeconds(10)));
        Assert.True(guard.Checking);
        Assert.True(guard.WorkaroundWanted);
        Assert.False(guard.NotProtectedNow()); // the check turns it on itself: the tray shows nothing while it runs
        int tries = live.Tries;
        guard.Recheck(TimeSpan.Zero, () => ui);
        Assert.True(ui.PromptOpen);
        Assert.Equal(tries, live.Tries);
        lock (_findings) Assert.Empty(_findings);
        live.Value = 0;
        guard.Recheck(TimeSpan.Zero, () => ui);
        Assert.False(ui.PromptOpen);
        await check.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0u, live.Value);
        Assert.Equal(1, ui.Asks);
        Assert.Empty(ui.Notices);
        Said(guard, Leaks, FindingSeverity.Info);
    }

    [Fact]
    public async Task ARecheckWhileACheckRunsCountsAsThatPeriodsRecheck()
    {
        // A recheck while the logon check's prompt is open sets nothing and asks for nothing, and its look starts a new
        // period all the same. The check ends without the workaround on (Cancel), so the not-protected notice opens; the
        // recheck then sets nothing until that period is over, and tries again once it is, silently: refused, it asks for
        // nothing and the notice stays as it was.
        long ms = 0;
        var every = TimeSpan.FromMinutes(3);
        var live = new Live { Accept = false };
        var ui = new OpenPrompt();
        var guard = Guard(live, clockMs: () => ms);
        var check = guard.RunAtLogon(ui, CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => ui.PromptOpen, TimeSpan.FromSeconds(10)));
        int during = live.Tries;
        guard.Recheck(every, () => ui);
        Assert.Equal(during, live.Tries);
        ui.Click(false);
        await check.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(guard.Checking);
        Assert.Equal(NotProtected, ui.OpenNotice);
        int tries = live.Tries;
        ms = (long)every.TotalMilliseconds - 1;
        guard.Recheck(every, () => ui);
        Assert.Equal(tries, live.Tries);
        Assert.Equal(1, ui.Asks);
        ms = (long)every.TotalMilliseconds;
        guard.Recheck(every, () => ui);
        Assert.Equal(tries + 1, live.Tries);
        Assert.Equal(1, ui.Asks);
        Assert.False(ui.PromptOpen);
        Assert.Equal(NotProtected, ui.OpenNotice);
        Assert.Equal([NotProtected], ui.Notices);
        Said(guard, StillOff, FindingSeverity.Warning);
    }

    [Fact]
    public async Task ARecheckThatFindsTheTimeoutNotReportedStartsAPeriodAllTheSame()
    {
        // The logon check turned the workaround on. A recheck finds the timeout not reported and does nothing, and its look
        // starts a new period all the same. Something then sets the timeout back: a recheck before that period is over
        // sets nothing, and one at its end turns the workaround on again.
        long ms = 0;
        var every = TimeSpan.FromMinutes(3);
        var live = new Live();
        var guard = Guard(live, clockMs: () => ms);
        await guard.RunAtLogon(null, CancellationToken.None);
        Assert.Equal(0u, live.Value);
        var ui = new Ui();
        live.Value = null;
        guard.Recheck(every, () => ui);
        live.Value = 20000;
        int tries = live.Tries;
        ms = (long)every.TotalMilliseconds - 1;
        guard.Recheck(every, () => ui);
        Assert.Equal(20000u, live.Value);
        Assert.Equal(tries, live.Tries);
        ms = (long)every.TotalMilliseconds;
        guard.Recheck(every, () => ui);
        Assert.Equal(0u, live.Value);
        Assert.Equal(0, ui.Asks);
        Said(guard, OnAgain, FindingSeverity.Warning);
    }

    [Fact]
    public async Task ARecheckWhileTheGuardIsOffStartsAPeriodAllTheSame()
    {
        // A recheck while the guard is off does nothing, and its look starts a new period all the same. The guard is then
        // turned on again, and the workaround with it, and something sets the timeout back: a recheck before that period
        // is over sets nothing, and one at its end turns the workaround on again.
        long ms = 0;
        var every = TimeSpan.FromMinutes(3);
        var live = new Live();
        var guard = Guard(live, clockMs: () => ms);
        await guard.RunAtLogon(null, CancellationToken.None);
        var ui = new Ui();
        guard.TurnOff();
        Assert.True(guard.Off);
        guard.Recheck(every, () => ui);
        guard.TurnedOn();
        Assert.True(await guard.TurnOnByOwner(ui));
        live.Value = 20000;
        int tries = live.Tries;
        ms = (long)every.TotalMilliseconds - 1;
        guard.Recheck(every, () => ui);
        Assert.Equal(20000u, live.Value);
        Assert.Equal(tries, live.Tries);
        ms = (long)every.TotalMilliseconds;
        guard.Recheck(every, () => ui);
        Assert.Equal(0u, live.Value);
        Assert.Equal(0, ui.Asks);
        Said(guard, OnAgain, FindingSeverity.Warning);
    }

    [Fact]
    public async Task ACheckThatFindsTheLeakFixedLeavesAnOpenPromptToItsCaller()
    {
        // Turn on's prompt is open when a logon check finds the leak fixed and leaves Windows alone. The workaround is not
        // on, so that prompt stays for its caller to finish (closed now, its caller would find the workaround still off and
        // say it is not protected); its OK turns the workaround on.
        Saved(GuardResult.CleanOnce);
        var live = new Live { Accept = false };
        var ui = new OpenPrompt();
        var guard = Guard(live, runTest: _ => ([0, 0, 1, 0, 1, 0], null));
        var first = guard.TurnOnByOwner(ui);
        Assert.True(SpinWait.SpinUntil(() => ui.PromptOpen, TimeSpan.FromSeconds(10)));
        await guard.RunAtLogon(ui, CancellationToken.None);
        Assert.Equal(GuardResult.Fixed, guard.State!.Result);
        Assert.True(ui.PromptOpen);
        Assert.Empty(ui.Notices);
        live.Accept = true;
        ui.Click(true);
        Assert.True(await first);
        Assert.Empty(ui.Notices);
    }

    [Fact]
    public async Task NoPromptOpensOnceTheWorkaroundIsOnRightBeforeItWouldAsk()
    {
        // Windows refuses Turn on's four tries, and just after the last one something else turns the workaround on. The
        // look right before the prompt finds it on, so no prompt opens.
        var live = new Live { Accept = false };
        int sets = 0;
        var ui = new Ui();
        var guard = Guard(live, setNow: () =>
        {
            if (Interlocked.Increment(ref sets) != 4) return SetZero(live);
            live.Value = 0;
            return false;
        });
        Assert.True(await guard.TurnOnByOwner(ui));
        Assert.Equal(4, sets);
        Assert.Equal(0, ui.Asks);
        Assert.Empty(ui.Notices);
        Said(guard, OnByClick, FindingSeverity.Info);
    }

    [Fact]
    public async Task NoNoticeOnceTheWorkaroundIsOnAsTheGuardSaysItIsNot()
    {
        // The user answers Turn on's prompt with Cancel; Windows refuses the try after it, and just then something else
        // turns the workaround on. By the time the guard says it is still not protected the live timeout is 0, so no
        // notice asks for a click.
        var live = new Live { Accept = false };
        bool asked = false;
        var ui = new Ui(click: () => false, onAsk: () => asked = true);
        var guard = Guard(live, setNow: () =>
        {
            if (!asked) return SetZero(live);
            live.Value = 0;
            return false;
        });
        Assert.False(await guard.TurnOnByOwner(ui));
        Assert.Equal(0u, live.Value);
        Assert.Empty(ui.Notices);
        Said(guard, StillNot, FindingSeverity.Warning);
    }

    [Fact]
    public async Task NoNoticeOnceTheGuardIsTurnedOffAsItSaysItIsNotProtected()
    {
        // The guard is turned off just as Turn on, refused, says it is still not protected: with the guard off nothing
        // needs the click, so no notice shows.
        var live = new Live { Accept = false };
        var ui = new Ui(click: () => false);
        TokenLeakGuard? guard = null;
        guard = Guard(live, finding: f => { if (f.Title == StillNot) guard!.TurnOff(); Record(f); });
        Assert.False(await guard.TurnOnByOwner(ui));
        Assert.True(guard.Off);
        Assert.Empty(ui.Notices);
        Said(guard, StillNot, FindingSeverity.Warning);
    }

    [Fact]
    public async Task TurningTheWorkaroundOnShowsNoNoticeAlsoWhenItIsSetBackAtOnce()
    {
        // Each part of the guard that turns the workaround on says so with no notice, also when something sets the timeout
        // back right after (the guard's next look, as it says so, finds 20000 ms): it needed no click, and the next recheck
        // looks again.
        var ui = new Ui();
        // The logon check.
        var live = new Live { SetBackAtOnce = true };
        var guard = Guard(live);
        await guard.RunAtLogon(ui, CancellationToken.None);
        Said(guard, Leaks, FindingSeverity.Info);
        Assert.Equal(20000u, guard.LiveTimeout);
        // Turn on.
        Assert.True(await guard.TurnOnByOwner(ui));
        Said(guard, OnByClick, FindingSeverity.Info);
        // The recheck's silent set.
        guard.Recheck(TimeSpan.Zero, () => ui);
        Said(guard, OnAgain, FindingSeverity.Warning);
        // A click on the tray icon.
        Assert.True(await guard.TurnOnFromTray(ui));
        Said(guard, OnByClick, FindingSeverity.Info);
        // The logon check that fails, but turns the workaround on (with no verdict saved, so it tests).
        File.Delete(StateFile);
        var failed = Guard(new Live { SetBackAtOnce = true }, runTest: _ => throw new InvalidOperationException("the helper broke"));
        await failed.RunAtLogon(ui, CancellationToken.None);
        Said(failed, "Token-leak guard: the check failed (see poolwatch.log), but the workaround is ON.", FindingSeverity.Warning);
        Assert.Equal(20000u, failed.LiveTimeout);
        Assert.Equal(0, ui.Asks);
        Assert.Empty(ui.Notices);
    }

    [Fact]
    public async Task SayingTheGuardIsOffShowsNoNoticeAlsoWhenItIsTurnedOnAgainJustThen()
    {
        // The guard is turned on again (Settings) just as it says it was off: that needed no click, so no notice shows.
        var leaks = (List<long>)[1, 0, 2, 100, 98, 101];
        var ui = new Ui();
        TokenLeakGuard? guard = null;
        void OnAgainJustThen(Finding f)
        {
            if (f.Title is TokenLeakGuard.TurnedOffDuringCheck or TokenLeakGuard.IsOff) guard!.TurnedOn();
            Record(f);
        }
        // Turned off while it checks.
        guard = Guard(new Live(), runTest: _ => { guard!.TurnOff(); return (leaks, null); }, finding: OnAgainJustThen);
        await guard.RunAtLogon(ui, CancellationToken.None);
        Said(guard, TokenLeakGuard.TurnedOffDuringCheck, FindingSeverity.Info);
        // "Turn on" in an older notice while the guard is off.
        guard.TurnOff();
        Assert.False(await guard.TurnOnByOwner(ui));
        Said(guard, TokenLeakGuard.IsOff, FindingSeverity.Info);
        // Turned off while it checks, and the check fails after that (the timeline is full once; no verdict saved, so it tests).
        File.Delete(StateFile);
        int said = 0;
        guard = Guard(new Live(), runTest: _ => { guard!.TurnOff(); return (leaks, null); },
            finding: f => { if (Interlocked.Increment(ref said) == 1) throw new InvalidOperationException("the timeline is full"); OnAgainJustThen(f); });
        await guard.RunAtLogon(ui, CancellationToken.None);
        Said(guard, TokenLeakGuard.TurnedOffDuringCheck, FindingSeverity.Info);
        Assert.Empty(ui.Notices);
    }

    // ---------------------------------------------------------------- the tray

    [Fact]
    public async Task TheTrayShowsTheSignInNotProtectedOnlyWhileItIsNot()
    {
        // Read at each tray refresh: never while the guard wants nothing, nor while the workaround is on, nor while the
        // value is not reported (not known), nor with the guard off; set back, at once, before any recheck.
        var live = new Live();
        var guard = Guard(live);
        Assert.False(guard.NotProtectedNow());
        await guard.RunAtLogon(null, CancellationToken.None);
        Assert.Equal(0u, live.Value);
        Assert.False(guard.NotProtectedNow());
        live.Value = 20000;
        Assert.True(guard.NotProtectedNow());
        live.Value = null;
        Assert.False(guard.NotProtectedNow());
        live.Value = 0;
        Assert.False(guard.NotProtectedNow());
        live.Value = 20000;
        guard.TurnOff();
        live.Value = 20000;
        Assert.False(guard.NotProtectedNow());
    }

    [Fact]
    public async Task AClickOnTheTrayIconTurnsTheWorkaroundOnAndTheAmberDotAndItsLineGo()
    {
        // Something set the timeout back and Windows refused the recheck's set: the tray shows it, with the amber dot and
        // the line in the tooltip. A click on the icon (the user's own click on PoolWatch; here Windows accepts the set
        // then) turns it on at once, with no window; the icon is refreshed and the dot and the line go. Protected, a click
        // changes nothing.
        var (guard, live) = await SetBack();
        live.Accept = false;
        var ui = new Ui(click: () => false);
        guard.Recheck(TimeSpan.FromMinutes(3), () => ui);
        int refreshed = 0;
        var tray = new GuardTray(guard.NotProtectedNow, () => guard.TurnOnFromTray(ui), () => refreshed++);
        Assert.True(tray.NotProtected);
        Assert.Equal(TrayState.Amber, GuardTray.State(TrayState.Green, tray.NotProtected));
        const string totals = "PoolWatch: paged 1.2 GB, non-paged 310 MB";
        Assert.Equal(totals + "\n" + GuardTray.Line, GuardTray.Tooltip(totals, tray.NotProtected, null));
        live.Accept = true;
        await tray.Clicked();
        Assert.Equal(0u, live.Value);
        Assert.Equal(1, refreshed);
        Assert.False(tray.NotProtected);
        Assert.Equal(TrayState.Green, GuardTray.State(TrayState.Green, tray.NotProtected));
        Assert.Equal(totals, GuardTray.Tooltip(totals, tray.NotProtected, null));
        Assert.Equal(0, ui.Asks);
        Assert.Empty(ui.Notices);
        Said(guard, OnByClick, FindingSeverity.Info);
        int tries = live.Tries;
        await tray.Clicked();
        Assert.Equal(tries, live.Tries);
        Assert.Equal(2, refreshed);
    }

    [Fact]
    public async Task AClickOnTheTrayIconThatWindowsStillRefusesShowsNoNotice()
    {
        // Windows refuses the click's tries too, so the guard asks for its one click; that prompt is answered with Cancel.
        // It says so in the log and the findings, and shows no not-protected notice: the tray's dot and line still say it.
        var (guard, live) = await SetBack();
        live.Accept = false;
        var ui = new Ui(click: () => false);
        var tray = new GuardTray(guard.NotProtectedNow, () => guard.TurnOnFromTray(ui), () => { });
        await tray.Clicked();
        Assert.Equal(20000u, live.Value);
        Assert.Equal(1, ui.Asks);
        Assert.Empty(ui.Notices);
        Said(guard, StillNotFromTray, FindingSeverity.Warning);
        Assert.True(tray.NotProtected);
    }

    [Fact]
    public void TheTrayIconsColourAndTooltipShowTheGuard()
    {
        // Amber over green while the guard is not protected; amber and red stay as they are.
        Assert.Equal(TrayState.Amber, GuardTray.State(TrayState.Green, guardNotProtected: true));
        Assert.Equal(TrayState.Green, GuardTray.State(TrayState.Green, guardNotProtected: false));
        Assert.Equal(TrayState.Amber, GuardTray.State(TrayState.Amber, guardNotProtected: true));
        Assert.Equal(TrayState.Amber, GuardTray.State(TrayState.Amber, guardNotProtected: false));
        Assert.Equal(TrayState.Red, GuardTray.State(TrayState.Red, guardNotProtected: true));
        Assert.Equal("Token-leak guard: not protected right now. Click to turn it on.", GuardTray.Line);
        // At most what a tray tooltip shows, the guard's line before an experiment's, so it is the last part cut.
        const string totals = "PoolWatch: paged 1.2 GB, non-paged 310 MB";
        string experiment = new('x', 80);
        string tip = GuardTray.Tooltip(totals, guardNotProtected: true, experiment);
        Assert.Equal(127, GuardTray.MaxTooltip);
        Assert.Equal(GuardTray.MaxTooltip, tip.Length);
        Assert.StartsWith(totals + "\n" + GuardTray.Line + "\n", tip);
        Assert.Equal(totals + "\n" + experiment, GuardTray.Tooltip(totals, guardNotProtected: false, experiment));
    }

    [Fact]
    public void TheTrayAppShowsTheGuardAndPassesOnWindowsSettingChanges()
    {
        // The tray app needs the running app (no test shows a window), so this holds its lines, each on a line of its own
        // (one commented out does not match): the guard's tray part, made with the guard's own reads and its tray turn-on;
        // the menu line, the first two items of the menu (the line and its rule), shown as the menu opens only while the
        // guard is not protected; a left click on the icon; the icon's colour and tooltip from it; and Windows'
        // setting-change message passed to the guard, and let go as the app exits.
        string source = File.ReadAllText(Path.Combine(TokenLeakHelperStartTests.RepoDir(), "src", "PoolWatch", "Ui", "TrayApp.cs"));
        foreach (string line in new[]
        {
            "var guard = analysis.Diagnostics.Guard;",
            "_guardTray = new GuardTray(guard.NotProtectedNow, () => guard.TurnOnFromTray(_guardNotices), UpdateIcon);",
            "var guardLine = new ToolStripMenuItem(GuardTray.Line, null, (_, _) => _ = _guardTray.Clicked()) { Visible = false };",
            "var guardRule = new ToolStripSeparator { Visible = false };",
            "guardLine.Visible = guardRule.Visible = _guardTray.NotProtected;",
            "_icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _ = _guardTray.Clicked(); };",
            "bool guardNotProtected = _guardTray.NotProtected;",
            "var state = GuardTray.State(UiData.State(_analysis), guardNotProtected);",
            "_icon.Text = GuardTray.Tooltip(text, guardNotProtected, experiment);",
            "_onSettingChange = (_, _) => guard.SettingChanged();",
            "Microsoft.Win32.SystemEvents.UserPreferenceChanged += _onSettingChange;",
            "Microsoft.Win32.SystemEvents.UserPreferenceChanged -= _onSettingChange;",
        })
            Assert.Matches(new Regex(@"(?m)^[ \t]*" + Regex.Escape(line) + @"[ \t]*\r?$"), source);
        var added = Regex.Matches(source, @"(?m)^[ \t]*menu\.Items\.(?:Add|Insert)\(([^;]*)\);").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(["guardLine", "guardRule"], added.Take(2));
    }

    [Fact]
    public void TheAppsGuardNamesTheProgramInFrontByItsFileNameOnly()
    {
        // The app's guard reads the program in front through Windows (its public constructor passes the reader: a line of
        // its own, so one commented out does not match). The reader only reads: whatever is in front of this test run, it
        // gives no name, or a file name with no folder in it, so no user name goes into the log or the findings.
        string source = File.ReadAllText(Path.Combine(TokenLeakHelperStartTests.RepoDir(), "src", "PoolWatch", "Engine", "TokenLeakGuard.cs"));
        Assert.Matches(new Regex(@"(?m)^[ \t]*TimeSpan\.FromSeconds\(5\), TimeSpan\.FromMilliseconds\(500\), inFront: TokenGuardNative\.ProgramInFront,\r?$"), source);
        string? name = PoolWatch.Native.TokenGuardNative.ProgramInFront();
        Assert.True(name is null || (name.Length > 0 && name.IndexOfAny(['\\', '/', ':']) < 0), $"the program in front reads \"{name}\"");
    }

    [Fact]
    public void TheAppsNoticesCloseWhatTheGuardTakesBack()
    {
        // GuardNotices' windows need the running app (no test shows a window), so this holds its lines: a take-back closes
        // the open not-protected notice and, when asked, the open click prompt; closing the prompt completes its click
        // false; and each window, once closed, is no longer the open one.
        string source = UninstallerTests.OneLine(File.ReadAllText(Path.Combine(TokenLeakHelperStartTests.RepoDir(), "src", "PoolWatch", "Ui", "GuardNotice.cs")));
        Assert.Contains("public void Withdraw(bool prompt) => _ui.Post(_ => { _notProtected?.Close(); if (prompt) _prompt?.Close(); }, null);", source);
        Assert.Contains("prompt.FormClosed += (_, _) => { done.TrySetResult(false); if (_prompt == prompt) _prompt = null; }; _prompt = prompt;", source);
        Assert.Contains("notice.FormClosed += (_, _) => { if (_notProtected == notice) _notProtected = null; }; _notProtected = notice;", source);
    }

    /// <summary>
    /// The guide, DESIGN.md, SECURITY.md and the guard's and the header's code comments say what the guard shows later in
    /// a sign-in in these words, each read as a sentence of its text (line breaks and comment markers left out): a text
    /// whose sentence is changed turns this red. What the guard does is held by the tests above; this holds only the
    /// words. It also reads the main window's refresh: the header is given the pool's state, not the tray's, as those
    /// texts say.
    /// </summary>
    [Fact]
    public void TheTextsSayWhatTheGuardShowsLaterInASignInAndWhatTheHeaderFollows()
    {
        static string Prose(params string[] path) => UninstallerTests.OneLine(File.ReadAllText(Path.Combine([TokenLeakHelperStartTests.RepoDir(), .. path])));
        static string Comments(params string[] path) => UninstallerTests.OneLine(System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(Path.Combine([TokenLeakHelperStartTests.RepoDir(), .. path])), @"(?m)^\s*///?\s?", " "));
        string guide = Prose("docs", "USER-GUIDE.md"), design = Prose("docs", "DESIGN.md"), security = Prose("SECURITY.md");
        string guard = Comments("src", "PoolWatch", "Engine", "TokenLeakGuard.cs"), header = Comments("src", "PoolWatch", "Ui", "MainForm.cs");
        foreach (var (text, sentence) in new[]
                 {
                     (guide, "or the token-leak guard is not protecting this sign-in right now (below)."),
                     (guide, "when the setting is still not on after the start's check or **Turn on** in a notice, one says this sign-in is not protected, " +
                             "with **Turn on** and **Not now**, and that one closes when the guard next asks you for **OK**; after a click on the tray icon " +
                             "none does, as the tray still says it."),
                     (guide, "Later in a sign-in, when something else sets the setting back, the guard opens no window by itself."),
                     (guide, "While it cannot, the tray icon's dot is amber (a red dot stays red), and its tooltip and the top of its right-click menu say " +
                             "**Token-leak guard: not protected right now. Click to turn it on.**"),
                     (guide, "it names the program that was in front, which Windows lets change this setting (other programs may be able to as well); " +
                             "this is not proof that it changed it."),
                     (guide, "The header follows the pool alone: while the token-leak guard is not protecting this sign-in, the tray icon's tooltip and " +
                             "menu say so."),
                     (design, "and, over green, while the token-leak guard says this sign-in is not protected (`GuardTray.State`"),
                     (design, "Later in a sign-in the guard opens no window by itself: the recheck"),
                     (design, "and tries again at the next recheck; the tray app's icon shows the sign-in not protected meanwhile."),
                     (design, "When Windows did not tell PoolWatch of the change, the recheck names the program in front at its own look"),
                     (design, "and `SettingChanged` takes no lock that a set holds while Windows is told of the change."),
                     (design, "The header follows the pool's state alone: the token-leak guard's amber is said by the tray icon's tooltip and menu " +
                              "(`GuardTray`)."),
                     (security, "at the guard's next check when Windows allows it then (it opens no window for that: while it cannot, the tray " +
                                "app's icon shows the sign-in not protected)"),
                     (security, "PoolWatch running in the background can set it again only when Windows lets it."),
                     (security, "while it cannot, the dot of the tray app's icon is amber (a red dot stays red) and its tooltip and the top of its menu " +
                                "say so"),
                     (security, "Its log and findings name the program that was in front as PoolWatch saw the change, which Windows lets change this " +
                                "setting (other programs may be able to as well); this is not proof that it changed it."),
                     (security, "When Windows did not tell PoolWatch of the change, PoolWatch names the program in front when its check found the change"),
                     (guard, "the program in front to the log). Takes no lock that a set holds while Windows is told of the change."),
                     (guard, "When Windows does not tell PoolWatch of the change, the recheck finds the set-back itself and says so. It sets nothing, " +
                             "takes no lock that a set holds while Windows is told of the change, and never throws."),
                     (guard, "once to turn it on again, silently. Later in a sign-in the guard opens no window by itself: the recheck never asks for the click"),
                     (guard, "and tries again at the next recheck; meanwhile the tray app's icon shows that this sign-in is not protected"),
                     (header, "The header follows the pool's state alone: the token-leak guard's amber is said by the tray icon's tooltip and menu " +
                              "(<see cref=\"GuardTray\"/>)."),
                 })
            Assert.Contains(sentence, text);
        // None of them has the words these replaced: that the guard opens no window at all later in a sign-in, that its
        // setting-change look takes no lock a set holds (with no "while"), or that Windows sends the message only when the
        // program that changed the setting asks for it.
        foreach (string text in new[] { guide, design, security, guard, header })
        {
            Assert.DoesNotContain("opens no window at all", text);
            Assert.DoesNotMatch(@"(?i)takes no lock that a set holds(?! while Windows is told of the change)", text);
            Assert.DoesNotMatch(@"only when the program that (made it|changed the setting|changes the setting) asks for", text);
        }
        // The header takes the pool's state (UiData.State), and the tray's colour with the guard's amber is the tray's alone.
        string refresh = UninstallerTests.OneLine(ScriptCode.CSharp(ScriptCode.CSharpMember(
            File.ReadAllText(Path.Combine(TokenLeakHelperStartTests.RepoDir(), "src", "PoolWatch", "Ui", "MainForm.cs")), "private void RefreshVisible(bool force)").Value));
        Assert.Contains("var state = UiData.State(_analysis);", refresh);
        Assert.Contains("UpdateHeader(state);", refresh);
        Assert.DoesNotContain("GuardTray", refresh);
    }
}
