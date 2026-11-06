using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using PoolWatch.Core.Changes;
using PoolWatch.Core.Findings;
using PoolWatch.Core.TokenGuard;
using PoolWatch.Native;

namespace PoolWatch.Engine;

/// <summary>
/// What the guard needs from the UI (null when headless): the notice that this sign-in is not protected, the click
/// prompt, and taking them back. Both are shown only when the user's click is needed, and only by the sign-in check and
/// by what the user clicked: the not-protected notice by the logon check and by "Turn on" in a notice, the click prompt
/// by those two and by a click on the tray icon that Windows still refuses. The background recheck later in a sign-in
/// shows neither: what it finds goes to the log and the findings, and while this sign-in is not protected the tray
/// app's icon shows it (<see cref="TokenLeakGuard.NotProtectedNow"/>). The notice is taken back when the guard
/// finds that nothing needs the click, and as a click prompt opens (which asks for the same click); the prompt when
/// the guard reads the workaround on or is turned off. What needs no click goes to the log and the findings only.
/// </summary>
internal interface IGuardUi
{
    /// <summary>
    /// One non-modal notice that this sign-in is not protected, offering "Turn on" (the user's click, which Windows
    /// needs before it accepts the change), in place of one open already. The guard shows it only when its caller could
    /// not turn the workaround on and needs it on (the logon check, and "Turn on" in a notice; never the recheck, nor a
    /// click on the tray icon, whose own line still says it), and, as it shows it, the guard is on, the live timeout is
    /// not 0 and no click prompt of the guard's is under way (<see cref="TokenLeakGuard"/>'s Report).
    /// </summary>
    void ShowNotProtected(string message);

    /// <summary>
    /// Asks the user for one click so PoolWatch is in front (Windows only lets the foreground program change the
    /// setting): for the logon check, "Turn on" in a notice and a click on the tray icon, when Windows refused their
    /// tries; never for the recheck. Completes true after the click (PoolWatch is then in front), false when the user
    /// cancels or the prompt is taken back (<see cref="Withdraw"/>).
    /// </summary>
    Task<bool> AskForClick(string message);

    /// <summary>
    /// Closes the not-protected notice when one is open, and, when <paramref name="prompt"/>, the click prompt too (which
    /// then completes false). Shows nothing. The guard calls it when nothing needs the click any more: when it says what
    /// happened and needs no click (the workaround is on or not needed, or the guard is off), when its recheck finds the
    /// workaround on, and when it is turned off; the prompt goes only when the guard reads the workaround on or is turned
    /// off (<see cref="TokenLeakGuard"/>'s Withdraw). It also calls it for the notice alone as it opens a click prompt,
    /// which asks for the same click. A recheck that Windows refuses does not call it: a notice of the logon check's is
    /// still true then, and stays.
    /// </summary>
    void Withdraw(bool prompt);
}

/// <summary>
/// The token-leak guard: an independent re-implementation of the workaround gunrgbcr/windows-token-leak-logon-fix describes
/// (no code copied). On Windows 11 24H2 and 25H2 builds (documented on several, retail and Insider; others are untested)
/// the kernel can leak a token reference (Toke) each time a process that is not allowed to take the foreground starts a
/// child. The workaround sets the LIVE ForegroundLockTimeout to 0 for the logon (never the registry). At start-up it
/// decides whether to test first, runs the leak test in a hidden helper, turns the workaround on or leaves Windows alone,
/// records the state and one timeline finding, and while running re-checks the live value every few minutes, turning
/// the workaround on again by itself when Windows accepts that and showing no window when it does not (<see cref="Recheck"/>).
/// It runs only when the TokenGuardEnabled setting is on (off by default) or headless with --token-guard.
/// </summary>
internal sealed class TokenLeakGuard
{
    public static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(300);
    private const int Attempts = 4;
    /// <summary>
    /// Written in the guard's admin-only folder right before the leak test, with the build it tests (and a run id of its
    /// own), and removed once the logon check has saved its outcome, or once a setup-guide check on that build has saved
    /// its verdict (when the guard was off at the start after, no logon check took it up:
    /// <see cref="RemoveMarkerAnsweredBy"/>). Found at a start, it means the last test never finished (PoolWatch was killed
    /// or crashed in native code during it, so no managed failsafe ran) or its outcome could not be saved. That start turns
    /// the workaround on without testing (or, when it is on already, leaves it on), and saves the build as interrupted, so
    /// each later logon on it does the same until a test finishes (<see cref="InterruptedOn"/>). A marker that names another
    /// build is not this build's: this build is tested as any new build is.
    /// </summary>
    internal const string TestMarkerName = "token-test-running.json";

    private readonly AppPaths _paths;
    private readonly Log _log;
    private readonly Action<Finding> _finding;
    private readonly object _lock = new();
    /// <summary>
    /// Which check runs: none, the logon check, or the setup guide's <see cref="TestOnly"/>. One field, claimed and released
    /// in one step, so the exit failsafe can never mistake one check for the other.
    /// </summary>
    private int _checking;
    private const int NotChecking = 0, LogonCheck = 1, CheckOnly = 2;
    /// <summary>
    /// Held for the whole of a <see cref="TestOnly"/> check. A logon check that starts meanwhile (the guard turned on while
    /// the setup guide checks) waits for it here, then decides from the verdict it saved, instead of finding a check
    /// running and doing nothing.
    /// </summary>
    private readonly SemaphoreSlim _testOnlyGate = new(1, 1);
    private bool _stateRead;

    /// <summary>
    /// The ONE gate for every "click to turn it on" prompt (the logon check, Turn on in a notice, a click on the tray icon;
    /// the recheck never asks).
    /// </summary>
    private int _prompting;
    /// <summary>
    /// The guard's last word asked for the user's click (a <see cref="Report"/> that needs it, shown or not); cleared by
    /// one that needs none, by a recheck that finds the workaround on, and by Turn off. With the workaround wanted, it
    /// decides whether the tray shows this sign-in not protected (<see cref="NotProtectedNow"/>).
    /// </summary>
    private volatile bool _clickAsked;
    /// <summary>
    /// A set-back the guard saw: the live timeout went from 0 (the workaround on) to <see cref="Value"/> ms, and
    /// <see cref="InFront"/> was the program in front then (null: it could not be told), read as Windows' setting-change
    /// message came (<see cref="AtChange"/>) or at a later look of the guard's own that found it (a recheck's).
    /// </summary>
    private sealed record SetBack(uint Value, string? InFront, bool AtChange);
    /// <summary>
    /// What the guard's looks saw (<see cref="Look"/>), under <see cref="_seenLock"/>: the value of the last look, the
    /// set-back since the workaround was last on (null while it is on, or when none was seen), and whether a recheck has
    /// said, in the findings, that Windows refused to turn it on again since (once until it is on again).
    /// </summary>
    private uint? _lastSeen;
    private SetBack? _setBack;
    private bool _refusalSaid;
    /// <summary>
    /// Taken last, after any other lock of the guard's, and never held while anything waits: the setting-change message
    /// that reaches <see cref="SettingChanged"/> on the UI thread can come while a set holds <see cref="_setLock"/>.
    /// </summary>
    private readonly object _seenLock = new();
    /// <summary>The program in front now (the app's guard: <see cref="TokenGuardNative.ProgramInFront"/>).</summary>
    private readonly Func<string?> _inFront;
    /// <summary>
    /// The UI the guard last showed its not-protected notice or its click prompt on (the app has one), which
    /// <see cref="Withdraw"/> takes them back from; null before it showed either.
    /// </summary>
    private IGuardUi? _shownOn;
    /// <summary>
    /// Every show and every take-back goes through this lock, and decides from what it reads under it (the live timeout,
    /// the guard off, a prompt under way), so the UI gets them in the order the guard decided them: whatever was shown
    /// before a take-back is on screen when the take-back comes.
    /// </summary>
    private readonly object _noticeLock = new();
    /// <summary>When the next recheck is due, on <see cref="_clockMs"/>; null before the first.</summary>
    private long? _nextRecheckMs;
    /// <summary>
    /// The monotonic clock the recheck is timed on, in milliseconds (the app's guard: Environment.TickCount64, time since
    /// Windows started, which only goes forward). Never the wall clock: a clock set back or forward neither holds a recheck
    /// back nor brings one early. The guard's other waits do not read a clock: the tries apart (Task.Delay) and the wait for
    /// a helper (WaitForSingleObject) are timed by Windows, which does not follow the wall clock either.
    /// </summary>
    private readonly Func<long> _clockMs;
    private readonly Func<bool> _setNow;
    private readonly Func<uint?> _getLive;
    private readonly TimeSpan _attemptGap, _clickRetryGap;
    private readonly Func<CancellationToken, (List<long> numbers, string? error)> _runTest;
    private readonly Func<string> _build;
    private readonly Func<HelperStart, IHelperRun> _startHelper;
    private readonly object _helperLock = new();
    /// <summary>A check-only run's helper while it runs in a job: <see cref="OnExit"/> ends it.</summary>
    private IHelperRun? _checkHelper;
    /// <summary>
    /// PoolWatch is exiting (<see cref="OnExit"/>): no check helper in a job runs on, and a check-only test saves nothing.
    /// </summary>
    private volatile bool _exiting;
    /// <summary>
    /// The guard is turned off (<see cref="TurnOff"/>) and not turned on since (<see cref="TurnedOn"/>): nothing sets the
    /// live timeout to 0 then, not a logon check or a recheck already under way, not a prompt already open. Read and written
    /// under <see cref="_setLock"/>, which every set goes through, so a set never lands after Turn off put the setting back;
    /// read without it only where a stale read sets nothing (<see cref="Look"/>, <see cref="SettingChanged"/>,
    /// <see cref="NotProtectedNow"/>).
    /// </summary>
    private volatile bool _off;
    private readonly object _setLock = new();

    /// <summary>The app's guard: its exit failsafe is hooked to the process's exit and crash events (<see cref="HookProcessExit"/>).</summary>
    public TokenLeakGuard(AppPaths paths, Log log, Action<Finding> finding)
        : this(paths, log, finding, TokenGuardNative.SetForegroundLockTimeoutZero, TokenGuardNative.GetForegroundLockTimeout,
            TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(500), inFront: TokenGuardNative.ProgramInFront,
            hookExit: HookProcessExit, setTo: TokenGuardNative.SetForegroundLockTimeout, signIn: TokenGuardNative.SignInId) { }

    /// <param name="setNow">Sets the live foreground lock timeout to 0 (true when Windows accepted). A test passes a fake: no
    /// test changes the real setting.</param>
    /// <param name="getLive">Reads the live timeout.</param>
    /// <param name="runTest">The leak test (default: <see cref="RunTest"/>, the hidden helper runs). A test passes a fake: no
    /// test starts a process.</param>
    /// <param name="build">The Windows build (default: <see cref="Build"/>).</param>
    /// <param name="startHelper">Starts one helper run in a job of its own (default: <see cref="StartHelper"/>). A test passes
    /// a fake: no test starts a process.</param>
    /// <param name="hookExit">Hooks the exit failsafe (a call of <see cref="OnExit"/>) once, as the guard is made, whatever
    /// the guard setting (the app's guard: <see cref="HookProcessExit"/>). Null: not hooked; a test passes a fake.</param>
    /// <param name="setTo">Sets the live timeout to a value (true when Windows accepted), for <see cref="TurnOff"/> (the
    /// app's guard: <see cref="TokenGuardNative.SetForegroundLockTimeout"/>). Null: refused, so a test that passes none
    /// never changes the real setting.</param>
    /// <param name="signIn">This sign-in, which the value the guard replaced is saved with (default, and the app's guard:
    /// <see cref="TokenGuardNative.SignInId"/>; a test's record goes to its own data folder). A sign-in that returns null:
    /// not saved, only kept in memory.</param>
    /// <param name="clockMs">The monotonic clock in milliseconds (default, and the app's guard: Environment.TickCount64). A
    /// test passes one it moves on.</param>
    /// <param name="inFront">The program in front now, as a set-back is seen (the app's guard:
    /// <see cref="TokenGuardNative.ProgramInFront"/>). Null: none is named (it could not be told); a test passes a fake.</param>
    internal TokenLeakGuard(AppPaths paths, Log log, Action<Finding> finding, Func<bool> setNow, Func<uint?> getLive, TimeSpan attemptGap, TimeSpan clickRetryGap,
        Func<CancellationToken, (List<long> numbers, string? error)>? runTest = null, Func<string>? build = null,
        Func<HelperStart, IHelperRun>? startHelper = null, Action<Action>? hookExit = null, Func<uint, bool>? setTo = null, Func<string?>? signIn = null,
        Func<long>? clockMs = null, Func<string?>? inFront = null)
    {
        _clockMs = clockMs ?? (() => Environment.TickCount64);
        _inFront = inFront ?? (() => null);
        _paths = paths;
        _log = log;
        _finding = finding;
        _setNow = setNow;
        _getLive = getLive;
        _attemptGap = attemptGap;
        _clickRetryGap = clickRetryGap;
        _runTest = runTest ?? RunTest;
        _build = build ?? Build;
        _startHelper = startHelper ?? StartHelper;
        Setting = new TokenGuardSetting(() => Path.Combine(GuardDir, TokenGuardSetting.FileName), paths.Guard.CheckReal, signIn ?? TokenGuardNative.SignInId, getLive,
            setTo ?? (_ => false), log);
        if (hookExit is null) return;
        hookExit(() => { try { OnExit(); } catch { } });
        ExitHooked = true;
    }

    /// <summary>
    /// The live value from before the guard first set it to 0 in this sign-in, which <see cref="TurnOff"/> puts back when
    /// Windows allows it; saved in the guard's folder, so a later PoolWatch and the uninstaller can put it back too.
    /// </summary>
    internal TokenGuardSetting Setting { get; }

    /// <summary>
    /// The exit failsafe of the app's guard: the process's exit and an unhandled exception call <paramref name="onExit"/>
    /// (<see cref="OnExit"/>). Hooked when the guard is made, so also while the guard is off, when only a check-only run
    /// (such as the setup guide's check) can have a helper running. OnExit ends only a check-only helper that runs in a
    /// job, and turns the workaround on only during a logon check; otherwise it does nothing. The clean exit calls OnExit
    /// too (Diagnostics.StopTokenGuard); a second call only repeats those two steps, which is harmless.
    /// </summary>
    private static void HookProcessExit(Action onExit)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, _) => onExit();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => onExit();
    }

    /// <summary>Whether this guard's exit failsafe is hooked (always for the app's guard; a test's guard when it passes a hook).</summary>
    internal bool ExitHooked { get; }

    public GuardState? State { get; private set; }
    /// <summary>Whether this logon wants the timeout at 0 (false when the leak looks fixed, or before the check ran).</summary>
    public bool WorkaroundWanted { get; internal set; }
    /// <summary>
    /// Whether a "click to turn it on" prompt is under way now: open, or its click being tried before it asks again (never
    /// two).
    /// </summary>
    public bool Prompting => Volatile.Read(ref _prompting) != 0;
    public uint? LiveTimeout { get; private set; }
    public string LastMessage { get; private set; } = "Token-leak guard: checking…";
    public bool Checking => Volatile.Read(ref _checking) != NotChecking;
    /// <summary>
    /// Whether the leak test's helper is told who started it (process ID and creation time), so that, orphaned, it offers
    /// to protect the logon: only in the logon check. The setup guide's check leaves the workaround off before and after,
    /// so there is nothing to offer.
    /// </summary>
    internal bool HelperWatchesParent => Volatile.Read(ref _checking) != CheckOnly;
    /// <summary>The raw helper numbers of this logon's test (two runs x three ways), for the summary and CI.</summary>
    public List<long> TestNumbers { get; } = new();
    public string? TestError { get; private set; }
    public GuardStep? Step { get; private set; }

    /// <summary>
    /// The guard's own folder: the data root's cache folder (on Windows, %ProgramData%\PoolWatch\cache, which only
    /// administrators can write), also when a headless run writes everything else to its --out folder, which someone else
    /// may be able to write. The verdict, the test marker and the helper's results never follow --out.
    /// </summary>
    internal string GuardDir => GuardDirOf(_paths);

    /// <summary>The guard's own folder for <paramref name="paths"/>, made if it is not there.</summary>
    internal static string GuardDirOf(AppPaths paths) => paths.EnsureDir(Path.Combine(paths.DataRoot, "cache"));
    private string StateFile => _paths.Guard.CheckReal(Path.Combine(GuardDir, "token-guard.json"));
    private string TestMarker => _paths.Guard.CheckReal(Path.Combine(GuardDir, TestMarkerName));

    /// <summary>CurrentBuild.UBR, e.g. "26100.4061".</summary>
    public static string Build()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        string build = k?.GetValue("CurrentBuild") as string ?? Environment.OSVersion.Version.Build.ToString();
        return k?.GetValue("UBR") is int ubr ? $"{build}.{ubr}" : build;
    }

    // ------------------------------------------------------------------ the logon check

    /// <summary>The check at start-up (a worker thread). Never throws; whatever happens, it tries to leave Windows protected.</summary>
    public async Task RunAtLogon(IGuardUi? ui, CancellationToken stop)
    {
        await _testOnlyGate.WaitAsync(); // a setup-guide check finishes first (at most its two helper runs)
        bool claimed;
        try
        {
            // PoolWatch is exiting (OnExit, which also ends a check-only helper in a job, so a check this one waited for
            // can end just now): no logon check starts while PoolWatch shuts down. The next start runs it.
            if (_exiting) return;
            claimed = Interlocked.CompareExchange(ref _checking, LogonCheck, NotChecking) == NotChecking;
        }
        finally { _testOnlyGate.Release(); }
        if (!claimed) return;
        GuardOutcome? outcome = null;
        bool saved = false;
        try
        {
            string build = _build();
            var last = LoadState();
            uint? live = Look(atChange: false);
            LiveTimeout = live;
            bool interrupted = InterruptedOn(build);
            if (interrupted) _log.Info("token guard: the last leak test did not finish (PoolWatch was stopped during it)");
            var step = GuardPolicy.First(last, build, live, interrupted);
            Step = step;
            _log.Info($"token guard: build {build}, live timeout {(live is { } l ? l.ToString() : "not reported")}, last {(last is null ? "none" : $"{GuardState.ResultText(last.Result)} on {last.Build}")} -> {step}");
            if (step == GuardStep.Test)
            {
                // The marker goes down before the test, while the workaround is still off. If this process is killed or
                // crashes in native code during the test, no handler runs, but the next start finds the marker and turns the
                // workaround on without testing. (The test cannot run with the workaround on: the workaround removes the leak.)
                // Each marker carries a run id of its own, so no two are the same bytes (RemoveMarkerAnsweredBy compares them).
                File.WriteAllText(TestMarker, JsonSerializer.Serialize(new { build, startedUtc = DateTime.UtcNow, pid = Environment.ProcessId, run = Guid.NewGuid().ToString("N") }));
                var (numbers, error) = await Task.Run(() => _runTest(stop), stop);
                TestError = error;
                TestNumbers.Clear();
                TestNumbers.AddRange(numbers);
                var verdict = error is null ? GuardPolicy.Verdict(numbers) : LeakVerdict.Unclear;
                string text = error ?? Numbers(numbers);
                outcome = GuardPolicy.AfterTest(verdict, last, build, DateTime.UtcNow, text);
                _log.Info($"token guard: test {text} -> {verdict}");
            }
            else outcome = GuardPolicy.Without(step, last, build, DateTime.UtcNow, interrupted);
            lock (_lock) State = outcome.State;
            saved = SaveState(outcome.State);
            if (!saved && step != GuardStep.AlreadyOn)
            {
                // The outcome is not on disk, so the next start could not decide from it: this logon is protected whatever
                // the outcome said, and the test marker (written before a test, or found at this start) stays, so the next
                // start turns the workaround on without testing.
                _log.Info("token guard: the outcome could not be saved; the workaround goes on and the test marker stays");
                if (!outcome.TurnOn) outcome = outcome with { TurnOn = true, Message = "Token-leak guard: the result could not be saved (see poolwatch.log); workaround ON to be safe." };
            }
            Want(outcome.TurnOn || step == GuardStep.AlreadyOn);
            bool on = step == GuardStep.AlreadyOn || (outcome.TurnOn && await TurnOn(ui, stop));
            // Turned off while it checked (Active changes, Settings): it turns nothing on, and says so in the log and
            // the findings (no click is needed: the guard is off, as the user chose).
            if (Off) Report(TurnedOffDuringCheck, needsClick: false, ui, FindingSeverity.Info, outcome.State);
            else Finish(outcome, on, ui);
            if (saved) RemoveTestMarker(); // the outcome is saved: the next start decides from the state file again
        }
        catch (Exception ex)
        {
            // On any error, still try to protect the logon (also when the outcome said fixed but was not saved) and say
            // whether it worked.
            _log.Info("token guard failed: " + ex);
            bool fixedAndSaved = saved && outcome is { TurnOn: false };
            bool on = !fixedAndSaved && TrySetNow();
            Want(on || WorkaroundWanted);
            // Not protected while protection was wanted, or the outcome not saved: the marker stays, so the next start turns
            // the workaround on. Turned off meanwhile, with the outcome saved: protection is not wanted, so it goes.
            if (saved && (on || fixedAndSaved || Off)) RemoveTestMarker();
            // A notice only when the user's click is needed: Windows refused the workaround this check tried to turn
            // on (and Report shows none while another click prompt of the guard's is under way). Turned off, on, or an
            // outcome saved that turns nothing on (the leak looks fixed, or the workaround was on already): the log and
            // the findings say what happened.
            if (Off) Report(TurnedOffDuringCheck, needsClick: false, ui, FindingSeverity.Info);
            else if (on)
                Report("Token-leak guard: the check failed (see poolwatch.log), but the workaround is ON.", needsClick: false, ui, FindingSeverity.Warning);
            else if (fixedAndSaved) Report(FailedAfterSaved, needsClick: false, ui, FindingSeverity.Warning, outcome!.State);
            else
                Report("Token-leak guard: the check failed and the workaround is NOT on: click to turn it on.", needsClick: true, ui, FindingSeverity.Warning);
        }
        finally { Volatile.Write(ref _checking, NotChecking); }
    }

    /// <summary>What the guard says when it was turned off while it checked (it turns nothing on).</summary>
    internal const string TurnedOffDuringCheck = "Token-leak guard: it was turned off while it checked, so it did not turn the workaround on.";
    /// <summary>What "Turn on" in an older notice says once the guard is off (it changes nothing).</summary>
    internal const string IsOff = "Token-leak guard: it is off, so nothing was changed.";
    /// <summary>
    /// What the guard says when the logon check failed after it saved an outcome that turns nothing on (the leak looks
    /// fixed on this build, or the workaround was on already): it changes nothing.
    /// </summary>
    internal const string FailedAfterSaved =
        "Token-leak guard: the check failed (see poolwatch.log) after it saved its result, which turns nothing on, so nothing was changed.";

    private void RemoveTestMarker()
    {
        try { File.Delete(TestMarker); }
        catch (Exception ex) { _log.Info("token guard: the test marker was not removed: " + ex.Message); }
    }

    /// <summary>
    /// After a setup-guide check (<see cref="TestOnly"/>) on <paramref name="build"/> has saved its verdict: a test marker a
    /// logon check left on that build (or one whose contents do not read as a marker, which counts as this build's) is
    /// answered by that test, as the logon check's own marker is once its outcome is saved, so it goes, and the next logon
    /// check decides from the saved verdict instead of turning the workaround on without testing. Only the marker the check
    /// found when it began goes: <paramref name="found"/>, its bytes' SHA-256 then (<see cref="MarkerSnapshot"/>), must
    /// still be its bytes'. One written since stays (each marker a logon check writes carries a run id of its own), as does
    /// one that was not there or could not be read when the check began. No clock is compared: a file's last-write time
    /// follows the file system's coarse tick, so it cannot tell a marker written just after the check began from one
    /// written before.
    /// </summary>
    private void RemoveMarkerAnsweredBy(string build, byte[]? found)
    {
        try
        {
            if (found is null || !File.Exists(TestMarker)) return;
            if (MarkerSnapshot() is not { } now || !now.AsSpan().SequenceEqual(found))
            {
                _log.Info("token guard: the test marker changed while this check ran, so it stays");
                return;
            }
            if (!InterruptedOn(build)) return;
            _log.Info("token guard: this check answered the test that did not finish on this build; its marker goes");
            RemoveTestMarker();
        }
        catch (Exception ex) { _log.Info("token guard: the test marker was not looked at: " + ex.Message); }
    }

    /// <summary>The SHA-256 of the test marker's bytes as they are now; null when there is none or it cannot be read.</summary>
    private byte[]? MarkerSnapshot()
    {
        try
        {
            if (!File.Exists(TestMarker)) return null;
            using var marker = new FileStream(TestMarker, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return SHA256.HashData(marker);
        }
        catch (Exception ex)
        {
            _log.Info("token guard: the test marker was not read: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Whether the test marker (<see cref="TestMarkerName"/>) says that a leak test on <paramref name="build"/> did not
    /// finish: it is there, and it names this build, or it cannot be read (then it counts, to be safe). One that names
    /// another build does not count: this build is tested as a new build is, and the marker goes once this start's outcome
    /// is saved (or is written again before its test).
    /// </summary>
    private bool InterruptedOn(string build)
    {
        try
        {
            if (!File.Exists(TestMarker)) return false;
            if (new FileInfo(TestMarker).Length > 4096) return true;
            using var doc = JsonDocument.Parse(File.ReadAllText(TestMarker));
            return !(doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("build", out var b)
                     && b.ValueKind == JsonValueKind.String && b.GetString() is { Length: > 0 } other && other != build);
        }
        catch (Exception ex)
        {
            _log.Info("token guard: the test marker was not read, so it counts: " + ex.Message);
            return true;
        }
    }

    /// <summary>
    /// Sets <see cref="WorkaroundWanted"/> under the same lock as <see cref="TurnOff"/>, and only while the guard is not
    /// off, so a Turn off is never followed by a stale "wanted".
    /// </summary>
    private void Want(bool want)
    {
        lock (_setLock) WorkaroundWanted = !_off && want;
    }

    /// <summary>
    /// Failsafe: PoolWatch exits (cleanly, or via an unhandled exception / process exit) while the leak test is still
    /// running. During the test the workaround is deliberately OFF and the verdict is unknown, so on any
    /// exit the logon must be left protected - even on a build whose LAST result was "fixed" (we were re-testing it).
    /// A process that is killed (Task Manager "End task", TerminateProcess) or dies in native code runs no managed
    /// handler, so this failsafe is skipped. The test marker it leaves behind makes the next PoolWatch start turn the
    /// workaround on without testing (until then, which is the next logon or a manual restart, that logon is unprotected).
    /// Not during a check-only <see cref="TestOnly"/> run (the setup guide's check, the token-leak
    /// watch): the workaround was off before it (the guard may not even be on), and the check leaves it so. That check's
    /// helper ends here instead (its result would be thrown away), so it does not keep PoolWatch.exe in use after PoolWatch
    /// has gone; its job ends it too when PoolWatch is killed. The logon check's helper is left to run: orphaned, it offers
    /// to protect the logon.
    /// </summary>
    public void OnExit()
    {
        lock (_helperLock)
        {
            _exiting = true;
            _checkHelper?.End();
        }
        if (Volatile.Read(ref _checking) != LogonCheck || Off) return; // turned off: it leaves the setting as Turn off left it
        bool on = TrySetNow();
        _log.Info(on ? "token guard: PoolWatch exited during the check; the workaround is ON" : "token guard: PoolWatch exited during the check; the workaround could NOT be turned on");
    }

    /// <summary>
    /// The logon check's outcome. A notice only when the outcome turns the workaround on and Windows refused it (and
    /// <see cref="Report"/> finds the user's click still needed as it says so); the workaround turned on, at once or after
    /// the user's click, or not needed, goes to the log and the findings only.
    /// </summary>
    private void Finish(GuardOutcome outcome, bool on, IGuardUi? ui)
    {
        bool notProtected = outcome.TurnOn && !on;
        string message = notProtected
            ? "Token-leak guard: NOT protected (Windows refused the change): click to turn it on."
            : outcome.Message + (on && outcome.TurnOn ? " With it on, any app may take the focus." : "");
        Report(message, needsClick: notProtected, ui, notProtected ? FindingSeverity.Warning : FindingSeverity.Info, outcome.State);
    }

    /// <summary>
    /// Says what happened: always in the log and as a finding on the timeline. On screen only what needs the user's click,
    /// and only for the logon check and "Turn on" in a notice: with <paramref name="needsClick"/> (its caller could not turn
    /// the workaround on and needs it on), the not-protected notice (<see cref="IGuardUi.ShowNotProtected"/>), unless its
    /// caller passes <paramref name="show"/> false (the recheck, and a click on the tray icon: the tray shows this sign-in
    /// not protected itself, <see cref="NotProtectedNow"/>), or, as it would show it, a click prompt of the guard's is under
    /// way (that prompt asks for the same click: it takes back a notice still open as it opens, and its caller says what
    /// came of it once it ends), or the guard is off, or the live timeout is 0 (something turned the workaround on
    /// meanwhile). A report that needs the click and shows nothing leaves on screen what is there: a notice still open is
    /// still true. It decides from the live value it reads itself, under the lock, never from <see cref="LiveTimeout"/>,
    /// which the logon check, the recheck and Turn off also write, outside that lock. What needs no click (the workaround
    /// on, on again after something set it back, on after the user's click, not needed, or the guard off) shows nothing,
    /// and takes back what the guard showed before (<see cref="Withdraw"/>): the not-protected notice, and the click
    /// prompt when it reads the workaround on (<see cref="TurnOff"/> takes the prompt back itself). Whether its words ask
    /// for the click is kept for the tray (<see cref="_clickAsked"/>); <paramref name="extra"/> goes into the finding's data.
    /// </summary>
    private void Report(string message, bool needsClick, IGuardUi? ui, FindingSeverity severity, GuardState? state = null, bool show = true,
        IReadOnlyDictionary<string, object?>? extra = null)
    {
        lock (_noticeLock)
        {
            LastMessage = message;
            _clickAsked = needsClick;
            uint? live = _getLive();
            LiveTimeout = live;
            var data = new Dictionary<string, object?>
            {
                ["build"] = state?.Build, ["result"] = state is null ? null : GuardState.ResultText(state.Result), ["detail"] = state?.Detail,
                ["liveTimeout"] = live, ["numbers"] = TestNumbers.ToList(), ["step"] = Step?.ToString(),
            };
            foreach (var (key, value) in extra ?? new Dictionary<string, object?>()) data[key] = value;
            _finding(new Finding { TimeUtc = DateTime.UtcNow, Kind = FindingKind.TokenGuard, Severity = severity, Title = message, Data = data });
            _log.Info(message);
            if (needsClick && !Off && live is not 0)
            {
                if (!show || ui is null || Prompting) return;
                _shownOn = ui;
                try { ui.ShowNotProtected(message); }
                catch (Exception ex) { _log.Info("token guard notice failed: " + ex.Message); }
                return;
            }
            Withdraw(prompt: live == 0);
        }
    }

    /// <summary>
    /// Takes back, from the UI they were shown on, the not-protected notice and, when <paramref name="prompt"/>, the click
    /// prompt. It is called in two kinds of case. Nothing needs the click any more: a <see cref="Report"/> that finds no
    /// click needed, a recheck that finds the workaround on, and <see cref="TurnOff"/>; the prompt goes too when that
    /// caller is Turn off or reads the live value 0 (a report, a recheck). Or a click prompt opens (<see cref="TurnOn"/>):
    /// the notice alone goes, since that prompt asks for the same click. A prompt taken back completes as a cancel does,
    /// and its caller tries once more: finding the workaround on (the app's set reads the live value back) or the guard
    /// off, it says so with no notice; set back meanwhile, the logon check and Turn on in a notice show the notice, which
    /// is then true (a click on the tray icon shows none: the tray says it). Nothing when the guard never showed either,
    /// and nothing from a recheck that Windows refuses: what is on screen is still true then.
    /// </summary>
    private void Withdraw(bool prompt)
    {
        lock (_noticeLock)
        {
            try { _shownOn?.Withdraw(prompt); }
            catch (Exception ex) { _log.Info("token guard: taking a notice back failed: " + ex.Message); }
        }
    }

    /// <summary>
    /// Sets the timeout to 0 (true once it is 0): 4 tries 5 s apart, then (with a UI) one click from the owner, as often as
    /// needed. Its callers are the logon check, the notice's "Turn on" and a click on the tray icon (the background
    /// recheck never calls it: it tries once, silently, <see cref="Recheck"/>). Every caller goes through ONE gate before
    /// it shows the click prompt: while one prompt is under way, another caller does not open a second one; it tries once
    /// more silently and says what it got in the log and the findings, with no notice (<see cref="Report"/> shows none while
    /// a prompt is under way). Right before each prompt it reads the live value once more, under the lock every show and
    /// every take-back takes, so no prompt opens while the workaround is on (turned on by another part of the guard, say)
    /// or after Turn off. As a prompt opens, it takes back a not-protected notice still open (<see cref="Withdraw"/>): the
    /// prompt asks for the same click, and its caller shows the notice again, by <see cref="Report"/>'s rule, when the
    /// prompt ends without the workaround on. A report or a recheck that finds the workaround on, and Turn off, take back a
    /// prompt shown before them, and that prompt completes as a cancel. No set is made under that lock (a set Windows
    /// accepts, with SPIF_SENDCHANGE, broadcasts WM_SETTINGCHANGE to the top-level windows, PoolWatch's own among them).
    /// Turned off (<see cref="TurnOff"/>): false at once, before any try and before any prompt.
    /// </summary>
    private async Task<bool> TurnOn(IGuardUi? ui, CancellationToken stop)
    {
        for (int i = 0; i < Attempts; i++)
        {
            if (Off) return false;
            if (TrySetNow()) return true;
            if (i < Attempts - 1) await Task.Delay(_attemptGap, stop);
        }
        if (Off) return false;
        _log.Info("token guard: Windows refused the change 4 times (PoolWatch is not in the foreground)");
        if (ui is null) return false;
        if (Interlocked.Exchange(ref _prompting, 1) != 0)
        {
            _log.Info("token guard: a prompt is already open; not showing a second one");
            return TrySetNow();
        }
        try
        {
            while (!stop.IsCancellationRequested)
            {
                Task<bool> click;
                lock (_noticeLock)
                {
                    if (Off) return false;
                    if (_getLive() == 0) return true; // on meanwhile: no prompt is needed (a read: no set under this lock)
                    Withdraw(prompt: false); // a not-protected notice still open: this prompt asks for the same click
                    _shownOn = ui;
                    click = ui.AskForClick("Token-leak guard: Windows only accepts this from the program in front. Click OK to put PoolWatch in front and turn the workaround on.");
                }
                if (!await click) return TrySetNow();
                for (int i = 0; i < Attempts; i++)
                {
                    if (TrySetNow()) return true;
                    await Task.Delay(_clickRetryGap, stop);
                }
            }
            return false;
        }
        finally { Volatile.Write(ref _prompting, 0); }
    }

    /// <summary>
    /// "Turn on" in a NOT-protected notice: the owner's click has put PoolWatch in front. Turned on, or the guard off,
    /// it says so in the log and the findings only; still not on, a notice again (none while another click prompt of
    /// the guard's is under way, <see cref="Report"/>).
    /// </summary>
    public Task<bool> TurnOnByOwner(IGuardUi ui) => TurnOnByClick(ui, notice: true);

    /// <summary>
    /// A click on the tray icon, or on the line at the top of its menu that says this sign-in is not protected
    /// (<see cref="NotProtectedNow"/>; the tray calls this only then): the click is the user's own, on PoolWatch, so the
    /// guard tries the set at once, and then as Turn on does (<see cref="TurnOn"/>: three more tries 5 s apart, then, if
    /// Windows still refuses, its click prompt, which this click of the user's started). Turned on, or the guard off, it
    /// says so in the log and the findings only. Still not on, it says so there too and shows no not-protected notice: the
    /// tray's dot and line still say it, and another click on them tries again.
    /// </summary>
    public Task<bool> TurnOnFromTray(IGuardUi? ui) => TurnOnByClick(ui, notice: false);

    /// <summary>What Turn on and a click on the tray icon say once they turned the workaround on.</summary>
    internal const string OnByClick = "Token-leak guard: workaround ON, safe to start work. With it on, any app may take the focus.";
    /// <summary>What Turn on in a notice says when Windows still refused (its notice shows again).</summary>
    internal const string StillNotByClick = "Token-leak guard: still NOT protected: click to turn on.";
    /// <summary>What a click on the tray icon says when Windows still refused (no notice: the tray still says it).</summary>
    internal const string StillNotFromTray =
        "Token-leak guard: still NOT protected (Windows refused the change): click the PoolWatch icon by the clock to try again.";

    /// <param name="notice">Still not on: show the not-protected notice (Turn on in a notice), or not (the tray icon).</param>
    private async Task<bool> TurnOnByClick(IGuardUi? ui, bool notice)
    {
        bool on = await TurnOn(ui, CancellationToken.None);
        if (on) Want(true);
        if (!on && Off) { Report(IsOff, needsClick: false, ui, FindingSeverity.Info, State); return false; }
        Report(on ? OnByClick : notice ? StillNotByClick : StillNotFromTray,
            needsClick: !on, ui, on ? FindingSeverity.Info : FindingSeverity.Warning, State, show: notice);
        return on;
    }

    private bool TrySetNow()
    {
        try
        {
            lock (_setLock)
            {
                if (_off) return false; // turned off: nothing sets it until the guard is turned on again
                uint? was = _getLive();
                bool on = _setNow();
                // The first time the guard changes the setting in this sign-in, remember what it was (in memory and in the
                // guard's folder), so that turning the guard off can put it back, here or in a later PoolWatch. A value that
                // was already 0 was not the guard's change.
                if (on && was is { } w and not 0) Setting.Remember(w);
                // On: a value that is not 0 from now on is a set-back (Look), and a refusal is said again once there is one.
                if (on) Seen(0);
                return on;
            }
        }
        catch (Exception ex) { _log.Info("token guard: set failed: " + ex.Message); return false; }
    }

    /// <summary>
    /// What a look, or a set the guard made (<paramref name="live"/> 0), saw, under <see cref="_seenLock"/>. Read 0: the
    /// workaround is on, so no set-back is kept and a refusal is said again once there is one. Read a value that is not 0
    /// where the last look saw 0 (or saw another set-back), with the guard on and the workaround wanted: a set-back, kept
    /// with the program in front now and <paramref name="atChange"/>; returned. A value unchanged since the last look (as
    /// when the setting-change message was for another setting) keeps what there is, and so does a look that Windows
    /// did not answer (null), which tells nothing.
    /// </summary>
    private SetBack? Seen(uint? live, bool atChange = false)
    {
        if (live is not { } v) return null;
        bool track = !_off && WorkaroundWanted;
        lock (_seenLock)
        {
            SetBack? found = null;
            if (v == 0)
            {
                _setBack = null;
                _refusalSaid = false;
            }
            else if (v != _lastSeen && track && (_lastSeen == 0 || _setBack is not null))
                _setBack = found = new SetBack(v, InFront(), atChange);
            _lastSeen = v;
            return found;
        }
    }

    /// <summary>
    /// One look at the live timeout that may find a set-back (<see cref="Seen"/>): the logon check's first read, the
    /// recheck's, and the look as Windows' setting-change message comes (<paramref name="atChange"/>, which also writes
    /// the program in front to the log). Takes no lock that a set holds while Windows is told of the change.
    /// </summary>
    private uint? Look(bool atChange)
    {
        uint? live = _getLive();
        if (Seen(live, atChange) is { AtChange: true } back)
            _log.Info($"token guard: {SetBackWords(back)}");
        return live;
    }

    /// <summary>The program in front now (<see cref="_inFront"/>); null when it cannot be told.</summary>
    private string? InFront()
    {
        try { return _inFront(); }
        catch { return null; }
    }

    /// <summary>
    /// What the guard says of a set-back it saw: the value, and the program that was in front then, which Windows lets
    /// change this setting (the program in front may do so; that is all it shows, not that it did). Seen as Windows'
    /// setting-change message came, it names the program in front as PoolWatch saw the change; seen only by a later look,
    /// the program in front at that look, and says that it can be minutes after the change.
    /// </summary>
    internal static string SetBackWords(uint value, string? inFront, bool atChange) =>
        $"the foreground lock timeout was set back to {value} ms; " + (inFront, atChange) switch
        {
            ({ } name, true) => $"{name} was the program in front as PoolWatch saw the change (Windows lets the program in front change " +
                                "this setting; this is not proof that it changed it)",
            ({ } name, false) => $"{name} was the program in front when PoolWatch found it at its regular check, which can be minutes " +
                                 "after the change (this is not proof that it changed it)",
            (null, true) => "PoolWatch could not tell which program was in front as it saw the change",
            (null, false) => "PoolWatch could not tell which program was in front when it found it at its regular check",
        };

    private static string SetBackWords(SetBack back) => SetBackWords(back.Value, back.InFront, back.AtChange);

    /// <summary>
    /// Windows said that a setting changed (WM_SETTINGCHANGE, which reaches the tray app's window for Windows' events,
    /// SystemEvents' UserPreferenceChanged, on its UI thread). While the guard is on and wants the workaround, it looks
    /// at the live timeout at once (<see cref="Look"/>), so a set-back is kept with the program in front as PoolWatch saw
    /// the change, which the log says now and the next recheck's finding names. The event does not say which setting
    /// changed, so the guard goes by the value: unchanged since its last look, nothing is kept. When Windows does not tell
    /// PoolWatch of the change, the recheck finds the set-back itself and says so. It sets nothing, takes no lock that a
    /// set holds while Windows is told of the change, and never throws.
    /// </summary>
    public void SettingChanged()
    {
        if (_off || !WorkaroundWanted) return;
        try { LiveTimeout = Look(atChange: true); }
        catch (Exception ex)
        {
            try { _log.Info("token guard: the setting was not read after Windows said a setting changed: " + ex.Message); } catch { }
        }
    }

    /// <summary>
    /// Whether this sign-in is not protected right now, as the tray icon shows it (the amber dot, and a line in its
    /// tooltip and at the top of its menu; Ui.GuardTray): the guard is on, it wants the workaround or its last word asked
    /// for the user's click (a logon check that failed before it decided, a Turn on that Windows refused), no check runs
    /// (the logon check turns the workaround on itself, and a leak test needs it off), and the live timeout, read now, is
    /// reported and not 0. Read on the UI thread at each tray refresh: one read of the setting, and no lock. Never throws.
    /// </summary>
    public bool NotProtectedNow()
    {
        if (_off || Checking || !(WorkaroundWanted || _clickAsked)) return false;
        try { return _getLive() is { } v && v != 0; }
        catch { return false; }
    }

    /// <summary>Whether the guard is turned off (<see cref="TurnOff"/>) and has not been turned on since.</summary>
    internal bool Off
    {
        get { lock (_setLock) return _off; }
    }

    /// <summary>
    /// The guard is turned on (Diagnostics.StartTokenGuard, before it starts the logon check or hands back the one under
    /// way): it may set the live timeout again.
    /// </summary>
    public void TurnedOn()
    {
        lock (_setLock) _off = false;
    }

    /// <summary>
    /// The guard is turned off (Active changes, Settings): from now on nothing sets the live timeout to 0, not a logon
    /// check or a recheck already under way, and the live setting the guard changed in this sign-in, by this PoolWatch or an
    /// earlier one, is put back to the value it had before, when Windows allows it (<see cref="TokenGuardSetting.PutBack"/>).
    /// Windows accepts that only from a program that may bring a window to the front ("the calling thread must be able to change the foreground
    /// window", SystemParametersInfo), which PoolWatch is when the owner turns the guard off in its window. Nothing needs
    /// the user's click then: a not-protected notice or a click prompt of the guard's still open goes (<see cref="Withdraw"/>;
    /// the prompt completes as a cancel, and its caller, finding the guard off, shows no notice), also when putting the
    /// setting back throws (the exception then goes on to the caller), and the tray no longer shows this sign-in not
    /// protected (<see cref="NotProtectedNow"/>). What its looks saw is dropped too: the value it puts back is not a
    /// set-back, and a guard turned on again starts afresh. Returns the sentence that says what happened.
    /// </summary>
    public string TurnOff()
    {
        try
        {
            lock (_setLock)
            {
                _off = true;
                WorkaroundWanted = false;
                _clickAsked = false;
                lock (_seenLock)
                {
                    _lastSeen = null;
                    _setBack = null;
                    _refusalSaid = false;
                }
                string said = Setting.PutBack();
                LiveTimeout = _getLive();
                return said;
            }
        }
        finally { Withdraw(prompt: true); } // once the set lock is released: Report holds the notice lock as it takes that one
    }

    /// <summary>
    /// Sampler thread, at most once each <paramref name="every"/> on the monotonic clock (<see cref="_clockMs"/>): if this
    /// logon wants the workaround and the live timeout is not 0 (something set it back, or it was not on yet), it tries
    /// once to turn it on again, silently. Later in a sign-in the guard opens no window by itself: the recheck never asks for
    /// the click and never shows the not-protected notice, whatever it finds; something may set the timeout back again
    /// and again. Turned on again, it says so in the log and the findings, and takes back a not-protected notice or a
    /// click prompt still open (nothing needs the click). Refused (PoolWatch is not the program in front), it says so in
    /// the log, and in the findings once until the workaround is on again, leaves on screen what is there (a notice of
    /// the logon check's is still true), and tries again at the next recheck; meanwhile the tray app's icon shows that
    /// this sign-in is not protected (<see cref="NotProtectedNow"/>), and a click on it turns the workaround on
    /// (<see cref="TurnOnFromTray"/>). What it says of a set-back names the program that was in front when PoolWatch saw
    /// it (as Windows' setting-change message came, <see cref="SettingChanged"/>, or at this look when none came first),
    /// which Windows lets change the setting: not proof that it did (<see cref="SetBackWords(uint, string?, bool)"/>).
    /// Found on already (another program, say, set it to 0), wanted or not, it changes nothing and says nothing; still
    /// on when it looks again under the lock every show takes, a not-protected notice or a click prompt of the guard's
    /// still open goes (<see cref="Withdraw"/>): no click is needed. While a check runs (<see cref="Checking"/>: the logon
    /// check or the setup guide's) it does only that: it sets nothing, since the logon check turns the workaround on
    /// itself and a leak test needs it off; so a prompt of the logon check's goes once the recheck finds the workaround
    /// on. Each look starts a new period, whatever it finds (the guard off, the value not reported, a check running), so
    /// after a check the first recheck that may set the timeout waits for the end of the period that the last look
    /// during the check began.
    /// </summary>
    public void Recheck(TimeSpan every, Func<IGuardUi?> ui)
    {
        long now = _clockMs();
        if (_nextRecheckMs is { } due && now < due) return;
        _nextRecheckMs = now + (long)every.TotalMilliseconds;
        uint? live = Look(atChange: false);
        LiveTimeout = live;
        if (Off || live is not { } value) return; // turned off: a recheck never turns it on again
        if (value == 0)
        {
            // Read again under the lock every show takes: set back since, a notice shown meanwhile is still true and stays.
            lock (_noticeLock)
                if (Look(atChange: false) == 0)
                {
                    _clickAsked = false;
                    Withdraw(prompt: true);
                }
            return;
        }
        if (Checking || !WorkaroundWanted) return;
        SetBack? back;
        lock (_seenLock) back = _setBack;
        var data = new Dictionary<string, object?>
        {
            ["inFront"] = back?.InFront, ["inFrontSeen"] = back is null ? null : back.AtChange ? "as the setting changed" : "at the regular check",
        };
        var u = ui();
        if (TrySetNow())
        {
            Report(back is null ? $"Token-leak guard: the workaround was not on (the foreground lock timeout was {value} ms); turned it on."
                                : $"Token-leak guard: {SetBackWords(back)}; turned the workaround on again.",
                needsClick: false, u, FindingSeverity.Warning, State, extra: data);
            return;
        }
        if (Off) return; // turned off meanwhile: Turn off said what it did, and nothing is meant to go on
        bool first;
        lock (_seenLock)
        {
            first = !_refusalSaid;
            _refusalSaid = true;
        }
        if (!first)
        {
            _log.Info($"token guard: the live timeout is still {value} ms; Windows refused again to turn the workaround on from the background (the findings said so once)");
            return;
        }
        string again = $" PoolWatch tries again {Every(every)}" + (u is null ? "." : "; a click on its icon by the clock turns it on now.");
        Report((back is null
                ? $"Token-leak guard: the workaround is not on (the foreground lock timeout is {value} ms) and Windows refused to turn it on from the background, so this sign-in is not protected."
                : $"Token-leak guard: {SetBackWords(back)}. Windows refused to turn the workaround on again from the background, so this sign-in is not protected.") + again,
            needsClick: true, u, FindingSeverity.Warning, State, show: false, extra: data);
    }

    /// <summary>How often the recheck comes, in words: on the first sample at least <paramref name="every"/> after the last.</summary>
    internal static string Every(TimeSpan every) => every.TotalMinutes switch
    {
        1 => "about every minute",
        >= 1 and var m => $"about every {m.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} minutes",
        _ => "at its next check",
    };

    /// <summary>The Overview line: on / off, the last result and date, Toke outstanding.</summary>
    public string StatusLine(long? tokeOutstanding, long? tokeBytes)
    {
        string on = LiveTimeout switch
        {
            0 => "workaround ON (foreground lock timeout 0: any app may take the focus)",
            null => "the foreground lock timeout is not reported",
            { } v => $"workaround OFF (foreground lock timeout {v} ms)",
        };
        string last = State is { } s
            ? $"last test: {GuardState.ResultText(s.Result)} on {s.Build}" + (s.TestedUtc > DateTime.MinValue ? $" ({s.TestedUtc.ToLocalTime():yyyy-MM-dd})" : "")
            : "not tested yet";
        string toke = tokeOutstanding is { } n ? $"; Toke outstanding {n:N0}" + (tokeBytes is { } b ? $" ({Core.Detection.Fmt.Bytes(b)})" : "") : "";
        return $"Token-leak guard: {(Checking ? "checking…; " : "")}{on}; {last}{toke}.";
    }

    // ------------------------------------------------------------------ the check alone (the setup guide)

    /// <summary>
    /// The setup guide's "does this Windows build have the token leak?" (about 20 seconds): the logon check's two hidden
    /// helper runs and nothing else. The workaround is left as it is (the test needs it off, so it runs only while the live
    /// timeout is not 0), no notice is shown, and neither the exit failsafe nor an orphaned helper offers to change it.
    /// <see cref="Checking"/> is set meanwhile, so the spawn diagnostics ignore the helper's children as they do at logon.
    /// The verdict is saved as the logon check saves it (<see cref="GuardPolicy.AfterTest"/>), so the next logon check
    /// decides from it, also over a test marker an earlier logon check left on this build (<see cref="RemoveMarkerAnsweredBy"/>),
    /// and goes on the timeline; a helper run that fails (a timeout, an error) gives an unclear verdict,
    /// saved the same way. Null when it cannot run now (the timeout is 0 or not reported, or a check is running), when it is
    /// stopped (<paramref name="stop"/>: the guide closed, or PoolWatch is stopping; or PoolWatch exits, <see cref="OnExit"/>),
    /// which ends its helper too (it runs in a job that ends with PoolWatch), or when it fails with an exception; nothing is saved then.
    /// </summary>
    public async Task<LeakVerdict?> TestOnly(CancellationToken stop)
    {
        if (!_testOnlyGate.Wait(0)) return null;
        try
        {
            uint? live = _getLive();
            if (live is null or 0 || Interlocked.CompareExchange(ref _checking, CheckOnly, NotChecking) != NotChecking) return null;
            try
            {
                string build = _build();
                var last = LoadState();
                var marker = MarkerSnapshot(); // a marker a logon check left: the one this check may answer
                var (numbers, error) = await Task.Run(() => _runTest(stop), stop);
                if (stop.IsCancellationRequested || _exiting)
                {
                    _log.Info("token guard: the check (setup) was stopped; nothing saved");
                    return null;
                }
                var verdict = error is null ? GuardPolicy.Verdict(numbers) : LeakVerdict.Unclear;
                string text = error ?? Numbers(numbers);
                var state = GuardPolicy.AfterTest(verdict, last, build, DateTime.UtcNow, text).State;
                lock (_lock) State = state;
                // Not saved: this PoolWatch still knows the verdict and says so; a later start reads the file from before, and
                // a logon check tests again whatever that file says, unless it says leak, or interrupted for this build (then it
                // turns the workaround on without testing).
                bool saved = SaveState(state);
                if (saved) RemoveMarkerAnsweredBy(build, marker);
                string found = verdict switch
                {
                    LeakVerdict.Leak => "this Windows build has the token leak",
                    LeakVerdict.Clean => "no token leak on this Windows build",
                    _ => "the result was unclear",
                };
                _log.Info($"token guard: check only (setup): {text} -> {verdict}");
                _finding(new Finding
                {
                    TimeUtc = DateTime.UtcNow, Kind = FindingKind.TokenGuard, Severity = saved ? FindingSeverity.Info : FindingSeverity.Warning,
                    Title = $"Token leak check: {found}; nothing was changed." + (saved ? "" : " The result could not be saved (see poolwatch.log)."),
                    Data = new Dictionary<string, object?>
                    {
                        ["build"] = build, ["result"] = GuardState.ResultText(state.Result), ["detail"] = state.Detail, ["liveTimeout"] = live,
                        ["numbers"] = numbers, ["step"] = "CheckOnly",
                    },
                });
                return verdict;
            }
            catch (Exception ex)
            {
                _log.Info("token guard: the check (setup) did not finish: " + ex.Message);
                return null;
            }
            finally { Volatile.Write(ref _checking, NotChecking); }
        }
        finally { _testOnlyGate.Release(); }
    }

    // ------------------------------------------------------------------ state

    /// <summary>
    /// The saved verdict (token-guard.json): read from the file the first time, then the copy in memory, which every save
    /// of this process updates (so the Simple view's once-a-minute refresh reads no file).
    /// </summary>
    public GuardState? ReadState()
    {
        lock (_lock) if (_stateRead) return State;
        return LoadState();
    }

    private GuardState? LoadState()
    {
        GuardState? s = null;
        try { if (File.Exists(StateFile) && new FileInfo(StateFile).Length < 64 * 1024) s = GuardState.Parse(NoWaitOpen.ReadAllText(StateFile)); }
        catch (Exception ex) { _log.Info("token guard state not read: " + ex.Message); }
        lock (_lock)
        {
            State = s;
            _stateRead = true;
        }
        return s;
    }

    /// <summary>Saves the verdict (written to a new file, then moved over the old one). False when it could not be saved.</summary>
    private bool SaveState(GuardState s)
    {
        try
        {
            DurableFile.Replace(_paths.Guard, StateFile, s.ToJson());
            return true;
        }
        catch (Exception ex)
        {
            _log.Info("token guard state not saved: " + ex.Message);
            return false;
        }
    }

    // ------------------------------------------------------------------ the leak test

    private static string Numbers(IReadOnlyList<long> n) =>
        n.Count == 6 ? $"run 1 {{{n[0]}, {n[1]}, {n[2]}}}, run 2 {{{n[3]}, {n[4]}, {n[5]}}} (n = {GuardPolicy.Children})" : string.Join(", ", n);

    /// <summary>Two hidden helper runs (each at most 300 s). Error text instead of numbers when a run failed.</summary>
    public (List<long> numbers, string? error) RunTest(CancellationToken stop)
    {
        var numbers = new List<long>();
        for (int run = 1; run <= 2; run++)
        {
            var (d, err) = RunHelper(stop);
            if (err is not null) return (numbers, $"helper run {run}: {err}");
            numbers.AddRange(d);
        }
        return (numbers, null);
    }

    /// <summary>This process's creation time, which a logon check's helper checks PoolWatch's process ID against.</summary>
    private static readonly Lazy<long> OwnCreationTime = new(TokenGuardNative.OwnCreationTime);

    /// <summary>
    /// Deletes the helper result files a run left in the guard's folder (older versions of PoolWatch named them
    /// token-test-1.json and token-test-2.json and did not always delete them), never the test marker. Each run's own file is
    /// deleted by Windows once the last handle on it closes; a program that opened it only to read its attributes can keep
    /// its name listed, unreadable, after the run, so this can find such a name too. A file that cannot be deleted now is left
    /// for the next run; nothing reads it.
    /// </summary>
    internal void SweepResultFiles()
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(GuardDir, "token-test-*.json"))
            {
                string name = Path.GetFileName(file);
                if (name.Equals(TestMarkerName, StringComparison.OrdinalIgnoreCase) || !name.StartsWith("token-test-", StringComparison.OrdinalIgnoreCase)
                    || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(_paths.Guard.CheckReal(file)); }
                catch (Exception ex) { _log.Info($"token guard: an old helper result was not removed ({name}): {ex.Message}"); }
            }
        }
        catch (Exception ex) { _log.Info("token guard: old helper results not looked for: " + ex.Message); }
    }

    private (List<long> deltas, string? error) RunHelper(CancellationToken stop)
    {
        // A new name for every run (a GUID) in the guard's own folder, which only administrators can write and which never
        // follows --out: no result from an earlier run, and no file another program made, can be read as this run's. The
        // guard makes the file itself and holds it open, so no other program can open it to read, and reads it through its
        // own handle. Windows deletes it once the last handle on it closes: the guard's closes when the run ends, however it
        // ends (see HelperResultFile for a handle another program can hold).
        SweepResultFiles();
        using var result = HelperResultFile.Create(_paths.Guard.CheckReal(Path.Combine(GuardDir, $"token-test-{Guid.NewGuid():N}.json")));
        string resultFile = result.Path;
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        // The logon check's helper is told who started it (process ID and creation time), so if PoolWatch is
        // killed or crashes while the test runs (the window in which the workaround is off), the orphaned helper sees its
        // starter gone and offers the one click that protects this logon: the only way, since a background process cannot
        // set the foreground lock timeout itself. Not in a check-only run (HelperWatchesParent): the workaround was off
        // before it and stays off, so PoolWatch stopping during it leaves nothing to put right.
        bool checkOnly = !HelperWatchesParent;
        var start = HelperCommand.For(exe, GuardPolicy.Children, resultFile,
            checkOnly ? null : new HelperParent(Environment.ProcessId, OwnCreationTime.Value), endWithPoolWatch: checkOnly);
        IHelperRun p;
        try { p = _startHelper(start); }
        catch (Exception ex) { return ([], "the helper could not start: " + ex.Message); }
        using (p)
        {
            // A check-only run (the setup guide's check, the token-leak watch) runs in a job that ends it with PoolWatch: it
            // is ended when the check is stopped or PoolWatch exits (OnExit), its result thrown away then, so it does not keep
            // PoolWatch.exe in use after PoolWatch has gone. The logon check's helper runs in a job that does not: it is left
            // to finish. Either is ended at the timeout below, through its job.
            _log.Info(checkOnly ? "token guard: the check's helper runs in a job, so it ends with PoolWatch"
                                : "token guard: the logon check's helper is in a job that only PoolWatch ends; it outlives PoolWatch to protect this logon");
            using var onStop = checkOnly ? stop.Register(p.End) : default;
            if (checkOnly)
                lock (_helperLock)
                {
                    if (_exiting) p.End();
                    else _checkHelper = p;
                }
            bool ended;
            try { ended = p.WaitForExit((int)HelperTimeout.TotalMilliseconds); }
            finally
            {
                if (checkOnly)
                    lock (_helperLock)
                        if (ReferenceEquals(_checkHelper, p)) _checkHelper = null;
            }
            if (!ended)
            {
                p.End(); // the processes in its job, and only those
                return ([], "timed out after 300 s");
            }
            if (stop.IsCancellationRequested) return ([], "cancelled");
            return ReadResult(result, p.ExitCode, GuardPolicy.Children);
        }
    }

    /// <summary>
    /// One helper run's result, read once through the guard's own handle (<see cref="HelperResultFile"/>). It counts only
    /// when it is exactly what the helper writes: the helper exited 0 (it wrote the file itself), and the file holds the
    /// three differences of a run with <paramref name="children"/> children each way, or the helper's own error. Anything
    /// else is an error: the verdict is then unclear, which turns the workaround on at logon.
    /// </summary>
    internal static (List<long> deltas, string? error) ReadResult(HelperResultFile file, int exitCode, int children)
    {
        var (text, problem) = file.ReadAll();
        if (problem is not null) return ([], problem);
        if (text!.Length == 0) return ([], $"no result (exit {exitCode})");
        if (exitCode != 0) return ([], $"the helper failed (exit {exitCode})");
        try
        {
            using var doc = JsonDocument.Parse(text!);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ([], "the result is not what the helper writes");
            if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String) return ([], e.GetString());
            if (!root.TryGetProperty("n", out var n) || n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out int count) || count != children)
                return ([], $"the result is not for {children} children each way");
            if (!root.TryGetProperty("deltas", out var d) || d.ValueKind != JsonValueKind.Array || d.GetArrayLength() != 3)
                return ([], "the result does not hold three differences");
            var deltas = new List<long>(3);
            foreach (var x in d.EnumerateArray())
            {
                if (x.ValueKind != JsonValueKind.Number || !x.TryGetInt64(out long v)) return ([], "the result does not hold three differences");
                deltas.Add(v);
            }
            return (deltas, null);
        }
        catch (JsonException ex) { return ([], "the result is not valid: " + ex.Message); }
    }

    /// <summary>
    /// Starts one helper run in a job of its own (<see cref="JobHelperRun"/>): a check-only run's job ends it with
    /// PoolWatch, the logon check's does not. No helper runs outside a job: one that Windows will not start in its job is
    /// not started at all, and the run fails (an unclear verdict, which turns the workaround on at logon).
    /// </summary>
    internal IHelperRun StartHelper(HelperStart start)
    {
        try { return JobHelperRun.Start(start); }
        catch (Exception ex)
        {
            _log.Info("token guard: the helper could not start in a job, so it was not started: " + ex.Message);
            throw;
        }
    }

    /// <summary>
    /// The helper itself (<c>--token-leak-test n file [--token-test-parent pid creation-time]</c>): n children each way,
    /// the reference count on its own primary token before and after each way (1 s after the last child), the three
    /// differences as JSON in the empty file the guard made for this run and holds open (<see cref="WriteResult"/>). Exits 0
    /// when it wrote the file (with the numbers, or with its error), <see cref="ExitCodes.ResultNotWritten"/> when it could not.
    /// If the PoolWatch that started it (<paramref name="parent"/>) has gone by the end of the test, the window
    /// in which the workaround is off, the orphaned helper raises the one-click "turn it on" notice, the only way to protect
    /// this logon from the background (<see cref="ParentGone"/>, <see cref="GuardPolicy.HelperShouldPrompt"/>), while the click
    /// is needed (<see cref="GuardPolicy.HelperPromptStillNeeded"/>: not while a PoolWatch tray app runs in its session whose
    /// token guard shows its notices, and taken back once the workaround is on or such a tray app starts its guard,
    /// <see cref="GuardNoticesShown"/>, <see cref="HelperPromptLook"/>). It opens
    /// PoolWatch's process as it starts, and only when that process still has the creation time it was given, so a later
    /// process given the same ID is never taken for it.
    /// </summary>
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

    /// <summary>The live foreground lock timeout; null when Windows did not give it.</summary>
    private static uint? LiveOrNull()
    {
        try { return TokenGuardNative.GetForegroundLockTimeout(); }
        catch { return null; }
    }

    /// <summary>
    /// The name of the guard-notices mark of this process's Windows session (<see cref="OwnSession"/>) in PoolWatch's
    /// private namespace (<see cref="AdminNamespace.GuardNoticesMutexName"/>): the tray app holds its session's, and a
    /// helper left running looks for its own session's. Throws <see cref="AdminNamespaceException"/> when the namespace
    /// cannot be opened, and when the session cannot be read.
    /// </summary>
    internal static string GuardNoticesMutexName() => AdminNamespace.Shared.GuardNoticesMutexName(OwnSession());

    /// <summary>The Windows session this process runs in (ProcessIdToSessionId, through <see cref="System.Diagnostics.Process.SessionId"/>); throws when Windows does not say.</summary>
    internal static int OwnSession()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        return self.SessionId;
    }

    /// <summary>
    /// Whether a PoolWatch tray app runs in this session whose token guard shows its notices: the mark it holds from the
    /// time it starts that guard until it exits (<see cref="HoldGuardNoticesMark"/>; its name from
    /// <paramref name="mutexName"/>, its session's in PoolWatch's private namespace, or a test's own) could be opened. That
    /// guard runs a logon check of its own as it starts, which turns the workaround on when this logon needs it, or asks
    /// for the same click when Windows refuses it from the background; a tray app in another session protects that
    /// session only, so its mark is another name. The mark stays held when the user then turns that guard off in
    /// Settings: they chose to have no guard, so the helper's prompt to turn it on is not wanted then either. A headless
    /// run, which shows no notice, never holds the mark, and neither does any other step: the one-instance mutex they hold
    /// does not count. False when it cannot be told (the name step throws, as when the namespace cannot be opened or the
    /// session cannot be read, or opening that name throws, as for an empty one), so the orphaned helper's prompt is not
    /// taken back on a guess.
    /// </summary>
    internal static bool GuardNoticesShown(Func<string> mutexName)
    {
        try
        {
            if (!Mutex.TryOpenExisting(mutexName(), out var mutex)) return false;
            mutex.Dispose();
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// The tray app's guard-notices mark (<see cref="AdminNamespace.GuardNoticesLeaf"/>; its name from
    /// <paramref name="mutexName"/>, its own session's), held from the time it starts its token guard with its notices until
    /// it exits, so an orphaned helper in that session takes its own prompt back (<see cref="GuardNoticesShown"/>). Null
    /// when it cannot be made: the helper's prompt then stays, as when no tray app runs.
    /// </summary>
    internal static Mutex? HoldGuardNoticesMark(Func<string> mutexName)
    {
        try { return new Mutex(false, mutexName()); }
        catch { return null; }
    }

    /// <summary>
    /// What takes the orphaned helper's prompt back (<see cref="PromptTurnOnOrphaned"/>): looked at every <see cref="LookEvery"/>
    /// while it is open, it closes the prompt once the user's click is no longer needed (<paramref name="stillNeeded"/>,
    /// <see cref="GuardPolicy.HelperPromptStillNeeded"/>), so no stale "not protected" prompt is left after the workaround
    /// is on or a tray app's token guard has taken over. A look that throws leaves it open.
    /// </summary>
    internal sealed class HelperPromptLook(Func<bool> stillNeeded, Action close)
    {
        public static readonly TimeSpan LookEvery = TimeSpan.FromSeconds(2);

        /// <summary>One look; true when it closed the prompt (it then looks no more).</summary>
        public bool Look()
        {
            bool needed;
            try { needed = stillNeeded(); }
            catch { needed = true; }
            if (needed) return false;
            close();
            return true;
        }
    }

    /// <summary>The helper's measurement: the three differences (or its error), as the result file holds them.</summary>
    private static object Measure(int n)
    {
        try
        {
            TokenGuardNative.AttachConsole(-1); // the hidden console of the cmd.exe that started us
            var deltas = new List<long>();
            foreach (var way in new[] { TokenGuardNative.ChildWay.InheritedConsole, TokenGuardNative.ChildWay.NewConsoleHidden, TokenGuardNative.ChildWay.NoWindow })
            {
                long before = TokenGuardNative.OwnTokenReferences();
                for (int i = 0; i < n; i++) TokenGuardNative.RunChild(way);
                Thread.Sleep(1000);
                deltas.Add(TokenGuardNative.OwnTokenReferences() - before);
            }
            return new { deltas, n, console = TokenGuardNative.GetConsoleWindow() != 0 };
        }
        catch (Exception ex) { return new { error = ex.Message }; }
    }

    /// <summary>
    /// Whether the PoolWatch a logon check's helper was told about (<paramref name="parent"/>) has gone by the end of the
    /// test: it could not be opened as the helper started (<paramref name="openedAtStart"/> 0: its process had ended, or its
    /// ID now names a later process), or the process opened then has ended since (<paramref name="hasEnded"/>; one that
    /// cannot be asked counts as ended). False when the helper was told of no parent (a check-only run).
    /// </summary>
    internal static bool ParentGone(HelperParent? parent, nint openedAtStart, Func<nint, bool> hasEnded)
    {
        if (parent is null) return false;
        if (openedAtStart == 0) return true;
        try { return hasEnded(openedAtStart); }
        catch { return true; }
    }

    /// <summary>
    /// Writes <paramref name="json"/> into the result file the guard made for this run and holds open: it must be there and
    /// empty, so the helper never makes a file and never writes over one that holds something. False when it could not.
    /// </summary>
    internal static bool WriteResult(string file, string json)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length != 0) return false;
            stream.Write(System.Text.Encoding.UTF8.GetBytes(json));
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// The orphaned helper's Turn on: sets the live timeout to 0 (<paramref name="setZero"/>), and when Windows accepted it
    /// over a value that was not 0, records that value with this sign-in (<see cref="TokenGuardSetting.Remember"/>), as the
    /// guard does when it sets it. So a later PoolWatch in this sign-in, or the uninstaller, puts it back when the guard is
    /// turned off (when Windows allows it), instead of finding the setting changed with no record of the guard changing it.
    /// </summary>
    internal static bool TurnOnOrphaned(Func<uint?> getLive, Func<bool> setZero, TokenGuardSetting setting)
    {
        uint? was = getLive();
        if (!setZero()) return false;
        if (was is { } w and not 0) setting.Remember(w);
        return true;
    }

    /// <summary>
    /// A small top-most notice, shown by the orphaned helper when PoolWatch died during the test. On the
    /// click the helper (now the foreground program) sets the live foreground lock timeout to 0 itself, and records the value
    /// it replaced with this sign-in (<see cref="TurnOnOrphaned"/>). It closes itself once the click is no longer needed
    /// (<see cref="HelperPromptLook"/>). Self-contained so
    /// the helper need not build the full guard; it mirrors the main app's GuardNotices "click to turn it on".
    /// </summary>
    private static void PromptTurnOnOrphaned(AppPaths paths)
    {
        System.Windows.Forms.Application.EnableVisualStyles();
        using var f = new System.Windows.Forms.Form
        {
            Text = "PoolWatch: token-leak guard", FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedToolWindow,
            StartPosition = System.Windows.Forms.FormStartPosition.Manual, TopMost = true, ShowInTaskbar = true,
            Width = 460, Height = 170, Font = new System.Drawing.Font("Segoe UI", 9f),
        };
        var label = new System.Windows.Forms.TextBox
        {
            Text = "PoolWatch was stopped while it was testing for the token leak, so this logon is not protected. " +
                   "Click Turn on to let PoolWatch set the focus timeout now (any app may then take the focus).",
            Multiline = true, ReadOnly = true, BorderStyle = System.Windows.Forms.BorderStyle.None,
            Dock = System.Windows.Forms.DockStyle.Fill, BackColor = System.Drawing.SystemColors.Control, TabStop = false,
        };
        var bar = new System.Windows.Forms.FlowLayoutPanel { Dock = System.Windows.Forms.DockStyle.Bottom, FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft, AutoSize = true, Padding = new System.Windows.Forms.Padding(6) };
        var on = new System.Windows.Forms.Button { Text = "Turn on", AutoSize = true };
        // Recorded as the guard records it, so turning the guard off later in this sign-in, or uninstalling, puts it back
        // when Windows allows it.
        on.Click += (_, _) => { try { TurnOnOrphaned(TokenGuardNative.GetForegroundLockTimeout, TokenGuardNative.SetForegroundLockTimeoutZero, TokenGuardSetting.ForCommandLine(paths, new Log(paths.LogFile, paths.Guard.CheckReal))); } catch { } f.Close(); };
        var no = new System.Windows.Forms.Button { Text = "Not now", AutoSize = true };
        no.Click += (_, _) => f.Close();
        bar.Controls.Add(on);
        bar.Controls.Add(no);
        f.Controls.Add(label);
        f.Controls.Add(bar);
        var area = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1280, 800);
        f.Location = new System.Drawing.Point(area.Right - f.Width - 12, area.Bottom - f.Height - 12);
        // Taken back once the click is no longer needed: the workaround on, or a tray app's token guard with its notices running.
        var look = new HelperPromptLook(() => GuardPolicy.HelperPromptStillNeeded(LiveOrNull(), GuardNoticesShown(GuardNoticesMutexName)), f.Close);
        using var timer = new System.Windows.Forms.Timer { Interval = (int)HelperPromptLook.LookEvery.TotalMilliseconds };
        timer.Tick += (_, _) => { if (look.Look()) timer.Stop(); };
        timer.Start();
        System.Windows.Forms.Application.Run(f);
    }
}
