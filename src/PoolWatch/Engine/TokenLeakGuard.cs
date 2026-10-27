using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using PoolWatch.Core.Findings;
using PoolWatch.Core.TokenGuard;
using PoolWatch.Native;

namespace PoolWatch.Engine;

/// <summary>What the guard needs from the UI (null when headless): the outcome notice and the one-click request.</summary>
internal interface IGuardUi
{
    /// <summary>One non-modal notice with the outcome; <paramref name="protectedNow"/> false offers "turn on".</summary>
    void ShowOutcome(string message, bool protectedNow);

    /// <summary>
    /// Asks the user for one click so PoolWatch is in front (Windows only lets the foreground program change the
    /// setting). Completes true after the click (PoolWatch is then in front), false when the user cancels.
    /// </summary>
    Task<bool> AskForClick(string message);
}

/// <summary>
/// The token-leak guard (addendum 3), re-implemented from the public fix gunrgbcr/windows-token-leak-logon-fix: on some
/// Windows 11 Insider builds the kernel leaks a token reference (Toke) each time a process that is not allowed to take
/// the foreground starts a child. The workaround sets the LIVE ForegroundLockTimeout to 0 for the logon (never the
/// registry). At start-up it decides whether to test first, runs the leak test in a hidden helper, turns the workaround
/// on or leaves Windows alone, records the state and one timeline finding, and while running re-checks the live value
/// every few minutes. It runs only when the TokenGuardEnabled setting is on (off by default) or headless with --token-guard.
/// </summary>
internal sealed class TokenLeakGuard
{
    public static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(300);
    private const int Attempts = 4;
    /// <summary>
    /// M3: written in the admin-only cache folder right before the leak test, removed once the logon check has saved its
    /// outcome. Found at a start, it means the last test never finished: PoolWatch was killed or crashed in native code
    /// during it, so no managed failsafe ran. That start turns the workaround on without testing.
    /// </summary>
    internal const string TestMarkerName = "token-test-running.json";

    private readonly AppPaths _paths;
    private readonly Log _log;
    private readonly Action<Finding> _finding;
    private readonly object _lock = new();
    private int _checking;
    private int _asking;
    /// <summary>C4 (review round 2): the ONE gate for every "click to turn it on" prompt (logon check, Turn on, recheck).</summary>
    private int _prompting;
    private DateTime _nextRecheck = DateTime.MinValue;
    private readonly Func<bool> _setNow;
    private readonly Func<uint?> _getLive;
    private readonly TimeSpan _attemptGap, _clickRetryGap;
    private readonly Func<CancellationToken, (List<long> numbers, string? error)> _runTest;
    private readonly Func<string> _build;

    public TokenLeakGuard(AppPaths paths, Log log, Action<Finding> finding)
        : this(paths, log, finding, TokenGuardNative.SetForegroundLockTimeoutZero, TokenGuardNative.GetForegroundLockTimeout,
            TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(500)) { }

    /// <param name="setNow">Sets the live foreground lock timeout to 0 (true when Windows accepted). A test passes a fake: no
    /// test changes the real setting.</param>
    /// <param name="getLive">Reads the live timeout.</param>
    /// <param name="runTest">The leak test (default: <see cref="RunTest"/>, the hidden helper runs). A test passes a fake: no
    /// test starts a process.</param>
    /// <param name="build">The Windows build (default: <see cref="Build"/>).</param>
    internal TokenLeakGuard(AppPaths paths, Log log, Action<Finding> finding, Func<bool> setNow, Func<uint?> getLive, TimeSpan attemptGap, TimeSpan clickRetryGap,
        Func<CancellationToken, (List<long> numbers, string? error)>? runTest = null, Func<string>? build = null)
    {
        _paths = paths;
        _log = log;
        _finding = finding;
        _setNow = setNow;
        _getLive = getLive;
        _attemptGap = attemptGap;
        _clickRetryGap = clickRetryGap;
        _runTest = runTest ?? RunTest;
        _build = build ?? Build;
    }

    public GuardState? State { get; private set; }
    /// <summary>Whether this logon wants the timeout at 0 (false when the leak looks fixed, or before the check ran).</summary>
    public bool WorkaroundWanted { get; internal set; }
    /// <summary>Whether a "click to turn it on" prompt is open now (C4: never two).</summary>
    public bool Prompting => Volatile.Read(ref _prompting) != 0;
    public uint? LiveTimeout { get; private set; }
    public string LastMessage { get; private set; } = "Token leak guard: checking…";
    public bool Checking => Volatile.Read(ref _checking) != 0;
    /// <summary>The raw helper numbers of this logon's test (two runs x three ways), for the summary and CI.</summary>
    public List<long> TestNumbers { get; } = new();
    public string? TestError { get; private set; }
    public GuardStep? Step { get; private set; }

    private string StateFile => _paths.Guard.CheckReal(Path.Combine(_paths.EnsureDir(_paths.CacheDir), "token-guard.json"));
    private string TestMarker => _paths.Guard.CheckReal(Path.Combine(_paths.EnsureDir(_paths.CacheDir), TestMarkerName));

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
        if (Interlocked.Exchange(ref _checking, 1) != 0) return;
        GuardOutcome? outcome = null;
        try
        {
            string build = _build();
            var last = LoadState();
            uint? live = _getLive();
            LiveTimeout = live;
            bool interrupted = File.Exists(TestMarker);
            if (interrupted) _log.Info("token guard: the last leak test did not finish (PoolWatch was stopped during it)");
            var step = GuardPolicy.First(last, build, live, interrupted);
            Step = step;
            _log.Info($"token guard: build {build}, live timeout {(live is { } l ? l.ToString() : "not reported")}, last {(last is null ? "none" : $"{GuardState.ResultText(last.Result)} on {last.Build}")} -> {step}");
            if (step == GuardStep.Test)
            {
                // M3: the marker goes down before the test, while the workaround is still off. If this process is killed or
                // crashes in native code during the test, no handler runs, but the next start finds the marker and turns the
                // workaround on without testing. (The test cannot run with the workaround on: the workaround removes the leak.)
                File.WriteAllText(TestMarker, JsonSerializer.Serialize(new { build, startedUtc = DateTime.UtcNow, pid = Environment.ProcessId }));
                var (numbers, error) = await Task.Run(() => _runTest(stop), stop);
                TestError = error;
                var verdict = error is null ? GuardPolicy.Verdict(numbers) : LeakVerdict.Unclear;
                string text = error ?? Numbers(numbers);
                outcome = GuardPolicy.AfterTest(verdict, last, build, DateTime.UtcNow, text);
                _log.Info($"token guard: test {text} -> {verdict}");
            }
            else outcome = GuardPolicy.Without(step, last, build, DateTime.UtcNow);
            WorkaroundWanted = outcome.TurnOn || step == GuardStep.AlreadyOn;
            bool on = step == GuardStep.AlreadyOn || (outcome.TurnOn && await TurnOn(ui, stop));
            Finish(outcome, on, ui);
            RemoveTestMarker(); // the outcome is saved: the next start decides from the state file again
        }
        catch (Exception ex)
        {
            // On any error, still try to protect the logon and say whether it worked.
            _log.Info("token guard failed: " + ex);
            bool on = outcome is { TurnOn: false } ? false : TrySetNow();
            WorkaroundWanted = on || WorkaroundWanted;
            // Not protected (and protection was wanted): the marker stays, so the next start turns the workaround on.
            if (on || outcome is { TurnOn: false }) RemoveTestMarker();
            Report(on ? "Token leak guard: the check failed (see poolwatch.log), but the workaround is ON."
                      : "Token leak guard: the check failed and the workaround is NOT on: click to turn it on.", on, ui, FindingSeverity.Warning);
        }
        finally { Volatile.Write(ref _checking, 0); }
    }

    private void RemoveTestMarker()
    {
        try { File.Delete(TestMarker); }
        catch (Exception ex) { _log.Info("token guard: the test marker was not removed: " + ex.Message); }
    }

    /// <summary>
    /// Failsafe: PoolWatch exits (cleanly, or via an unhandled exception / process exit) while the leak test is still
    /// running. C4 (review): during the test the workaround is deliberately OFF and the verdict is unknown, so on any
    /// exit the logon must be left protected - even on a build whose LAST result was "fixed" (we were re-testing it).
    /// A process that is killed (Task Manager "End task", TerminateProcess) or dies in native code runs no managed
    /// handler, so this failsafe is skipped. M3: the test marker it leaves behind makes the next PoolWatch start turn the
    /// workaround on without testing (until then, which is the next logon or a manual restart, that logon is unprotected).
    /// </summary>
    public void OnExit()
    {
        if (!Checking) return;
        bool on = TrySetNow();
        _log.Info(on ? "token guard: PoolWatch exited during the check; the workaround is ON" : "token guard: PoolWatch exited during the check; the workaround could NOT be turned on");
    }

    private void Finish(GuardOutcome outcome, bool on, IGuardUi? ui)
    {
        lock (_lock) State = outcome.State;
        SaveState(outcome.State);
        string message = outcome.TurnOn && !on
            ? "Token leak guard: NOT protected (Windows refused the change): click to turn it on."
            : outcome.Message + (on && outcome.TurnOn ? " With it on, any app may take the focus." : "");
        Report(message, on || !outcome.TurnOn, ui, outcome.TurnOn && !on ? FindingSeverity.Warning : FindingSeverity.Info, outcome.State);
    }

    private void Report(string message, bool protectedNow, IGuardUi? ui, FindingSeverity severity, GuardState? state = null)
    {
        LastMessage = message;
        LiveTimeout = _getLive();
        _finding(new Finding
        {
            TimeUtc = DateTime.UtcNow, Kind = FindingKind.TokenGuard, Severity = severity, Title = message,
            Data = new Dictionary<string, object?>
            {
                ["build"] = state?.Build, ["result"] = state is null ? null : GuardState.ResultText(state.Result), ["detail"] = state?.Detail,
                ["liveTimeout"] = LiveTimeout, ["numbers"] = TestNumbers.ToList(), ["step"] = Step?.ToString(),
            },
        });
        _log.Info(message);
        try { ui?.ShowOutcome(message, protectedNow); } catch (Exception ex) { _log.Info("token guard notice failed: " + ex.Message); }
    }

    /// <summary>
    /// Sets the timeout to 0: 4 tries 5 s apart, then (with a UI) one click from the owner, as often as needed.
    /// C4 (review round 2): every caller (the logon check, the notice's "Turn on", the background recheck) goes through
    /// ONE gate before it shows the click prompt: while one prompt is open, another caller does not open a second one; it
    /// tries once more silently and reports what it got (the open prompt finishes the job).
    /// </summary>
    public async Task<bool> TurnOn(IGuardUi? ui, CancellationToken stop)
    {
        for (int i = 0; i < Attempts; i++)
        {
            if (TrySetNow()) return true;
            if (i < Attempts - 1) await Task.Delay(_attemptGap, stop);
        }
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
                if (!await ui.AskForClick("Token leak guard: Windows only accepts this from the program in front. Click OK to put PoolWatch in front and turn the workaround on."))
                    return TrySetNow();
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

    /// <summary>"Turn on" in a NOT-protected notice: the owner's click has put PoolWatch in front.</summary>
    public async Task<bool> TurnOnByOwner(IGuardUi ui)
    {
        bool on = await TurnOn(ui, CancellationToken.None);
        if (on) WorkaroundWanted = true;
        Report(on ? "Token leak guard: workaround ON, safe to start work. With it on, any app may take the focus."
                  : "Token leak guard: still NOT protected: click to turn on.", on, ui, on ? FindingSeverity.Info : FindingSeverity.Warning, State);
        return on;
    }

    private bool TrySetNow()
    {
        try { return _setNow(); }
        catch (Exception ex) { _log.Info("token guard: set failed: " + ex.Message); return false; }
    }

    /// <summary>
    /// Sampler thread, every few minutes: if this logon wants the workaround and something set the timeout back, turn it
    /// on again (from the background this may be refused: then the owner is asked for the click).
    /// </summary>
    public void Recheck(DateTime nowUtc, TimeSpan every, Func<IGuardUi?> ui)
    {
        if (nowUtc < _nextRecheck || Checking) return;
        _nextRecheck = nowUtc + every;
        uint? live = _getLive();
        LiveTimeout = live;
        if (!WorkaroundWanted || live is null or 0) return;
        _log.Info($"token guard: the live timeout was set back to {live}; turning the workaround on again");
        bool on = TrySetNow();
        if (on) { Report($"Token leak guard: something set the foreground lock timeout back to {live} ms; turned the workaround on again.", true, null, FindingSeverity.Warning, State); return; }
        // C4 (review): the silent re-set failed, so we must ask the owner for a click. That prompt loop is long-lived;
        // without a guard every recheck interval would start another one and stack prompts. Allow only one recheck task at
        // a time; the prompt itself goes through the one gate in TurnOn shared with the logon check and "Turn on" (round 2).
        if (Interlocked.Exchange(ref _asking, 1) != 0) return;
        Task.Run(async () =>
        {
            try
            {
                var u = ui();
                bool ok = u is not null && await TurnOn(u, CancellationToken.None);
                Report(ok ? "Token leak guard: the timeout had been set back; workaround ON again."
                          : $"Token leak guard: something set the timeout back to {live} ms and it could not be turned on again: NOT protected, click to turn on.", ok, u, FindingSeverity.Warning, State);
            }
            finally { Volatile.Write(ref _asking, 0); }
        });
    }

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
        return $"Token leak guard: {(Checking ? "checking…; " : "")}{on}; {last}{toke}.";
    }

    // ------------------------------------------------------------------ state

    private GuardState? LoadState()
    {
        GuardState? s = null;
        try { if (File.Exists(StateFile) && new FileInfo(StateFile).Length < 64 * 1024) s = GuardState.Parse(File.ReadAllText(StateFile)); }
        catch (Exception ex) { _log.Info("token guard state not read: " + ex.Message); }
        lock (_lock) State = s;
        return s;
    }

    private void SaveState(GuardState s)
    {
        try
        {
            File.WriteAllText(_paths.Guard.CheckReal(StateFile + ".tmp"), s.ToJson());
            File.Move(_paths.Guard.CheckReal(StateFile + ".tmp"), _paths.Guard.CheckReal(StateFile), overwrite: true);
        }
        catch (Exception ex) { _log.Info("token guard state not saved: " + ex.Message); }
    }

    // ------------------------------------------------------------------ the leak test

    private static string Numbers(IReadOnlyList<long> n) =>
        n.Count == 6 ? $"run 1 {{{n[0]}, {n[1]}, {n[2]}}}, run 2 {{{n[3]}, {n[4]}, {n[5]}}} (n = {GuardPolicy.Children})" : string.Join(", ", n);

    /// <summary>Two hidden helper runs (each at most 300 s). Error text instead of numbers when a run failed.</summary>
    public (List<long> numbers, string? error) RunTest(CancellationToken stop)
    {
        TestNumbers.Clear();
        for (int run = 1; run <= 2; run++)
        {
            var (d, err) = RunHelper(run, stop);
            if (err is not null) return (TestNumbers.ToList(), $"helper run {run}: {err}");
            TestNumbers.AddRange(d);
        }
        return (TestNumbers.ToList(), null);
    }

    private (List<long> deltas, string? error) RunHelper(int run, CancellationToken stop)
    {
        string outFile = _paths.Guard.CheckReal(Path.Combine(_paths.EnsureDir(_paths.CacheDir), $"token-test-{run}.json"));
        try { File.Delete(outFile); } catch { }
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        string outArgs = _paths.OutDir is { } o ? $" --out \"{o}\"" : "";
        // A hidden console process (cmd with CREATE_NO_WINDOW) starts the helper, which attaches to that console: its
        // "inherited console" children then share a console that has no window, and the helper is never in front.
        // M3 (review S2): the helper is told this PoolWatch's PID, so if PoolWatch is killed or crashes while the test runs
        // (the window in which the workaround is off), the orphaned helper sees its starter gone and offers the one click
        // that protects this logon — the only way, since a background process cannot set the foreground lock timeout itself.
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            $"/d /c \"\"{exe}\" --token-leak-test {GuardPolicy.Children} \"{outFile}\"{outArgs} --token-test-parent {Environment.ProcessId}\"")
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.SystemDirectory };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("the helper did not start");
        if (!p.WaitForExit((int)HelperTimeout.TotalMilliseconds))
        {
            try { p.Kill(entireProcessTree: true); } catch { } // our own helper and its children
            return ([], "timed out after 300 s");
        }
        if (stop.IsCancellationRequested) return ([], "cancelled");
        if (!File.Exists(outFile)) return ([], $"no result (exit {p.ExitCode})");
        using var doc = JsonDocument.Parse(File.ReadAllText(outFile));
        if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String) return ([], e.GetString());
        return (doc.RootElement.GetProperty("deltas").EnumerateArray().Select(x => x.GetInt64()).ToList(), null);
    }

    /// <summary>
    /// The helper itself (<c>--token-leak-test n file [--token-test-parent pid]</c>): n children each way, the reference
    /// count on its own primary token before and after each way (1 s after the last child), the three differences as JSON.
    /// M3 (review S2): if the PoolWatch that started it (<paramref name="parentPid"/>) exits while the test runs — the window
    /// in which the workaround is off — the orphaned helper raises the one-click "turn it on" notice, the only way to protect
    /// this logon from the background. The result JSON is unchanged (its reader, the parent, is gone in that case).
    /// </summary>
    public static int RunHelperMode(int n, string outFile, AppPaths paths, int? parentPid = null)
    {
        Process? parent = null;
        bool parentExited = false;
        if (parentPid is { } pid) { try { parent = Process.GetProcessById(pid); } catch { parentExited = true; } }
        object result;
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
            result = new { deltas, n, console = TokenGuardNative.GetConsoleWindow() != 0 };
        }
        catch (Exception ex) { result = new { error = ex.Message }; }
        try { File.WriteAllText(paths.Guard.CheckReal(outFile), JsonSerializer.Serialize(result)); } catch { }
        if (parent is not null) { try { parentExited = parent.HasExited; } catch { parentExited = true; } finally { parent.Dispose(); } }
        uint? live = null;
        try { live = TokenGuardNative.GetForegroundLockTimeout(); } catch { }
        if (GuardPolicy.HelperShouldPrompt(parentExited, live))
            try { PromptTurnOnOrphaned(); } catch { /* best effort: the marker still makes the next start turn it on */ }
        return 0;
    }

    /// <summary>
    /// M3 (review S2): a small top-most notice, shown by the orphaned helper when PoolWatch died during the test. On the
    /// click the helper (now the foreground program) sets the live foreground lock timeout to 0 itself. Self-contained so
    /// the helper need not build the full guard; it mirrors the main app's GuardNotices "click to turn it on".
    /// </summary>
    private static void PromptTurnOnOrphaned()
    {
        System.Windows.Forms.Application.EnableVisualStyles();
        using var f = new System.Windows.Forms.Form
        {
            Text = "PoolWatch: token leak guard", FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedToolWindow,
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
        on.Click += (_, _) => { try { TokenGuardNative.SetForegroundLockTimeoutZero(); } catch { } f.Close(); };
        var no = new System.Windows.Forms.Button { Text = "Not now", AutoSize = true };
        no.Click += (_, _) => f.Close();
        bar.Controls.Add(on);
        bar.Controls.Add(no);
        f.Controls.Add(label);
        f.Controls.Add(bar);
        var area = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1280, 800);
        f.Location = new System.Drawing.Point(area.Right - f.Width - 12, area.Bottom - f.Height - 12);
        System.Windows.Forms.Application.Run(f);
    }
}
