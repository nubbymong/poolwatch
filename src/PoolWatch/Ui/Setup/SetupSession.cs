using PoolWatch.Core;
using PoolWatch.Core.Changes;
using PoolWatch.Core.Recommendations;
using PoolWatch.Core.Setup;
using PoolWatch.Core.TokenGuard;
using PoolWatch.Engine;
using PoolWatch.Engine.Setup;

namespace PoolWatch.Ui.Setup;

/// <summary>
/// What setup asks of the app (<see cref="SetupHost"/> on the real PC; the tests pass a fake). Every change goes through
/// the code the rest of the app uses for it: the suggestions' buttons (<see cref="RecommendationActions"/>), the running
/// settings (<see cref="LiveSettings"/>) and the change list, so each is listed under Active changes as before.
/// </summary>
internal interface ISetupHost
{
    /// <summary>The CI UI test: nothing is changed, and the token-leak check does not run.</summary>
    bool Smoke { get; }
    /// <summary>The last scan of the PC (cached), and a new one (worker thread).</summary>
    EnvironmentFacts? Facts { get; }
    Task<EnvironmentFacts> Scan();
    Settings Running { get; }
    /// <summary>This session's token-leak verdict, once a check gave one.</summary>
    LeakVerdict? LeakTestNow { get; }
    bool ShortcutMade { get; }
    bool LauncherInstalled { get; }
    /// <summary>The update notices' switch; null leaves it out.</summary>
    IUpdateCheck? Updates { get; }
    /// <summary>A suggestion's own button, run as the setup guide (<see cref="RecommendationActions.Run"/>).</summary>
    Task<(bool Ok, string Message)> Run(Recommendation r, IWin32Window owner);
    Task<(bool Ok, string Message)> CheckTokenLeak(CancellationToken stop);
    /// <summary>The Standby cache step's choice (<see cref="StandbyCacheSetup"/>).</summary>
    /// <param name="replaceOwnTask">The step showed that a rule replaces your own RAMMap clear task (the scan found one).</param>
    Task<(bool Ok, string Message)> ApplyCache(CacheChoice choice, double hours, double gb, bool replaceOwnTask);
    /// <summary>Every change Active changes lists, and how many of them can be put back (worker thread); null when the list cannot be read.</summary>
    Task<(int All, int CanPutBack)?> ActiveChanges();
    void MarkCompleted();
    void OpenWebPage(IWin32Window owner, string url);
    void Log(string line);
}

/// <summary>The real PC behind setup, over the app's <see cref="SetupContext"/>.</summary>
internal sealed class SetupHost : ISetupHost
{
    private readonly SetupContext _c;
    private readonly StandbyCacheSetup _cache;

    public SetupHost(SetupContext c)
    {
        _c = c;
        _cache = new StandbyCacheSetup(new WindowsSetupSystem(c), c.Live, c.Smoke);
    }

    public bool Smoke => _c.Smoke;
    public EnvironmentFacts? Facts => _c.Probe.Last;
    public Task<EnvironmentFacts> Scan() => _c.Probe.ScanToTheEnd(); // setup sets its own limit on the wait (scanWait)
    public Settings Running => _c.Live.Running;
    public LeakVerdict? LeakTestNow => _c.Recommendations.LeakTestNow;
    public bool ShortcutMade => _c.Ledger.Records.Has(RecordKind.Shortcut);
    public bool LauncherInstalled => _c.LauncherInstalled;
    public IUpdateCheck? Updates => _c.Updates;
    public Task<(bool Ok, string Message)> Run(Recommendation r, IWin32Window owner) => _c.Recommendations.Run(r, owner, SetupSession.Via);
    public Task<(bool Ok, string Message)> CheckTokenLeak(CancellationToken stop) => _c.Recommendations.CheckTokenLeak(stop);
    public Task<(bool Ok, string Message)> ApplyCache(CacheChoice choice, double hours, double gb, bool replaceOwnTask) => _cache.Apply(choice, hours, gb, replaceOwnTask);

    public async Task<(int All, int CanPutBack)?> ActiveChanges()
    {
        try
        {
            var list = await Task.Run(_c.Ledger.List);
            return (list.Count, list.Count(c => c.CanUndo));
        }
        catch (Exception ex) { Log("setup: the list of changes was not read: " + ex.Message); return null; }
    }

    public void MarkCompleted() => _c.Store.MarkGuideCompleted(DateTime.UtcNow);
    public void OpenWebPage(IWin32Window owner, string url) => _c.Actions.Guarded(owner, "Open web page", () => UiActions.OpenWebPage(url));
    public void Log(string line) => _c.Engine.Log.Info(line);
}

/// <summary>
/// The Standby cache step's choice, applied through the running settings as Settings' Save and the suggestions apply it
/// (at once, and in settings.json): a schedule, a pool size, both, or off. Where the step said that a rule replaces your
/// own RAMMap clear task, a rule removes that task first, through the code the suggestions' Replace uses
/// (<see cref="ISetupSystem.ReplaceClearTasks"/>: your task is saved for putting back), so the two never both run.
/// Clearing needs PoolWatch to run as administrator. Nothing happens in the CI UI test.
/// </summary>
internal sealed class StandbyCacheSetup(ISetupSystem system, LiveSettings live, bool smoke)
{
    /// <param name="replaceOwnTask">
    /// The step showed that a rule replaces your own clear task. Decided from what the step showed (the scan), so Next never
    /// does other than the step said: a rule already in effect still replaces the task.
    /// </param>
    public async Task<(bool Ok, string Message)> Apply(CacheChoice choice, double hours, double gb, bool replaceOwnTask)
    {
        if (smoke) return (false, RecommendationActions.Refused);
        try
        {
            if (choice == CacheChoice.Later)
            {
                if (!live.Running.AutoClearEnabled) return (true, "");
                await Task.Run(() => live.Apply($"automatic cache clearing off ({SetupSession.Via})", s => s.AutoClearEnabled = false));
                system.Timeline($"Automatic cache clearing turned off (from {SetupSession.Via}).");
                return (true, "Automatic cache clearing is off. PoolWatch suggests it when it helps.");
            }
            if (!replaceOwnTask && CacheChoices.InEffect(live.Running, choice, hours, gb)) return (true, "");
            if (!system.Elevated) return (false, "Clearing the cache needs PoolWatch to run as administrator.");
            var (_, every, size) = CacheChoices.Writes(choice, hours, gb);
            string words = CacheChoices.Words(choice, hours, gb);
            // The rules and the switch in one change, so the sampler never sees the clear on with the old rules.
            Task WriteRule() => Task.Run(() => live.Apply($"automatic cache clearing {words} ({SetupSession.Via})", s =>
            {
                s.AutoClearEveryHours = every!.Value;
                s.AutoClearPoolGb = size!.Value;
                s.AutoClearEnabled = true;
            }));
            if (!replaceOwnTask) await WriteRule();
            else if (await ReplaceOwnTask(WriteRule) is { } why) return (false, why);
            system.Timeline($"Automatic cache clearing turned on, {words} (from {SetupSession.Via}).");
            return (true, $"PoolWatch empties the standby cache {words}.");
        }
        catch (Exception ex) { return (false, "That did not work: " + ex.Message); }
    }

    /// <summary>
    /// Your own clear tasks removed, each saved for Undo first (<see cref="Engine.Changes.ClearTaskReplacer"/>, as the
    /// suggestions' Replace runs it), then the rule chosen written at once, then the clear status and the scan read again;
    /// null, or why a task was left in place. Replace turns the hourly clear on, and PoolWatch's own clear stays paused for
    /// your task until the status is read again: the rule chosen is written before that, so no clear ever runs on an hourly
    /// rule nobody chose. The timeline names no schedule: the rule chosen follows on its own line.
    /// </summary>
    private async Task<string?> ReplaceOwnTask(Func<Task> writeRule)
    {
        string? error = null;
        try
        {
            (_, error) = await Task.Run(() => system.ReplaceClearTasks(live, SetupSession.Via));
            if (error is null)
            {
                system.Timeline($"Replaced your own clear task with PoolWatch's automatic cache clear (from {SetupSession.Via}).");
                await writeRule();
            }
        }
        finally
        {
            // Read again either way: tasks may have been removed before an error, and the automatic clear waits for the status.
            system.RefreshClearStatus();
            try { await system.Rescan(); }
            catch (Exception) { /* a scan that fails, or runs too long, leaves the facts from before */ }
        }
        return error;
    }
}

/// <summary>What changed in a <see cref="SetupSession"/>, so the window updates only that part, in place.</summary>
/// <see cref="Next"/>: Next's wait started by itself (the scan's facts landed on a step that applies changes, a change on one
/// did not work, or a Fixes row appeared), so whether Next can be pressed changes again when it ends.
internal enum SetupPart { Step, Scan, Leak, Cache, Fixes, Busy, Summary, Updates, Next }

/// <summary>
/// The setup window's state, without a window: the step, the scan and the token-leak check, every choice, what each
/// change said, and the Ready step's summary. A step's choices are applied when Next is pressed on it, each through
/// <see cref="ISetupHost"/>; a change that did not work keeps the step open with its message, and Next pressed again
/// without another choice goes on without it. The Standby cache and Fixes steps apply what the scan found, so their Next
/// waits until the scan has ended (or setup stopped waiting for it; a scan that ends later is still shown, never while a
/// change runs). Choices are held while a change runs. Buttons in a row (Defender's how-to) act at once. Next waits a moment
/// (<see cref="NextWait"/>) on the Standby cache and Fixes steps once Next or Back brings them up, again once the scan's
/// facts land while either shows or a change on either did not work, and on the Fixes step again when a switch or a Yes/No
/// appears there while it shows; Next on the Fixes step applies only the switches and Yes/Nos the window showed when it
/// was pressed. Called on the UI thread.
/// </summary>
internal sealed class SetupSession
{
    /// <summary>Where the changes were made, for the timeline and the change list.</summary>
    public const string Via = "setup";

    /// <summary>
    /// How long setup waits for the scan before it goes on with the facts PoolWatch had (it reads in seconds; a hung read
    /// must never keep the first run, which cannot be closed, from finishing). The rescan after each change gives up on
    /// the same scan at the same time (<see cref="EnvironmentProbe.ScanAsync(TimeSpan)"/>).
    /// </summary>
    public static readonly TimeSpan ScanWait = EnvironmentProbe.HungAfter;

    /// <summary>
    /// How long Next waits once Next or Back brought up a step that applies changes (Standby cache, Fixes), once the scan's
    /// facts landed while one of them showed (on time, or after setup had stopped waiting for them: with them can come the
    /// note that a rule replaces your own RAMMap clear task, or new rows), and once a Fixes row's switch or Yes/No appeared
    /// while the step showed (the token-leak check ended, or the shortcut was put back from Active changes). Their choices
    /// start on, so a press meant for the step before (the second click of a double-click, a second Enter), or for a Next
    /// that waited for what then appeared, must not apply a change the owner had no time to see. Also once a change on one
    /// of them did not work: Next pressed again goes on without it, so a second press must not skip its message.
    /// </summary>
    public static readonly TimeSpan NextWait = TimeSpan.FromSeconds(1);

    private readonly ISetupHost _host;
    private readonly TimeSpan _scanWait;
    /// <summary>The time Next's wait is measured by (<see cref="System.Diagnostics.Stopwatch"/> ticks; the tests move it on).</summary>
    private readonly Func<long> _clock;
    /// <summary>
    /// The Fixes rows' switches and Yes/Nos as the window was last told of them ("TokenGuard:Toggle"): what the Fixes page
    /// shows (<see cref="ShowsFixRows"/>).
    /// </summary>
    private HashSet<string> _toldInputs;
    /// <summary>
    /// When Next's wait last started: Next or Back brought up a step that applies changes, the scan's facts landed while one
    /// showed, a change on one did not work, or a switch or a Yes/No appeared on the Fixes step while it showed; null when
    /// none of these happened yet.
    /// </summary>
    private long? _heldFrom;
    /// <summary>
    /// A wait came due while a change ran (a switch or a Yes/No appeared on the Fixes step): Next's wait starts when that
    /// change ends. The scan's facts are never shown while a change runs (<see cref="TakeLate"/>).
    /// </summary>
    private bool _appearedWhileBusy;
    private readonly Dictionary<string, string> _errors = [];
    /// <summary>A change that did not work, and the choice it was for: Next pressed again with the same choice goes on.</summary>
    private readonly Dictionary<string, string> _failedWith = [];
    private bool _checking, _leakNotRun, _checkStarted;
    /// <summary>The window closed (from <see cref="LoadAsync"/>).</summary>
    private CancellationToken _stop;
    /// <summary>The facts of a scan that ended after setup stopped waiting for it, while a change ran: shown once it has ended.</summary>
    private EnvironmentFacts? _late;

    /// <param name="scanWait">The tests: a shorter wait for the scan than <see cref="ScanWait"/>.</param>
    /// <param name="clock">The tests: a clock they move on (<see cref="System.Diagnostics.Stopwatch"/> ticks), for Next's wait (<see cref="NextWait"/>).</param>
    public SetupSession(ISetupHost host, bool firstRun, bool advanced, TimeSpan? scanWait = null, Func<long>? clock = null)
    {
        _host = host;
        _scanWait = scanWait ?? ScanWait;
        _clock = clock ?? System.Diagnostics.Stopwatch.GetTimestamp;
        Flow = new SetupFlow(firstRun);
        Advanced = advanced;
        var running = host.Running;
        (_, Hours, PoolGb) = CacheChoices.Read(running);
        Cache = CacheChoices.Initial(running, firstRun);
        Leak = ImpactRows.Leak(null, running.TokenGuardEnabled, host.LeakTestNow, checking: false, notRun: false);
        _toldInputs = FixInputs();
    }

    public SetupFlow Flow { get; }
    /// <summary>The Welcome step's choice: the Advanced view (setup and the app); the app takes it when setup finishes.</summary>
    public bool Advanced { get; set; }
    /// <summary>The scan has ended, or setup stopped waiting for it (<see cref="Facts"/> is then what PoolWatch had).</summary>
    public bool Scanned { get; private set; }
    public EnvironmentFacts? Facts { get; private set; }
    public LeakState Leak { get; private set; }
    public CacheChoice Cache { get; private set; }
    public double Hours { get; private set; }
    public double PoolGb { get; private set; }
    /// <summary>The Fixes step's choices, each used only where its row offers it.</summary>
    public bool SignIn { get; private set; } = true;
    public bool Shortcut { get; private set; } = true;
    public bool Guard { get; private set; } = true;
    public bool Busy { get; private set; }
    public (int All, int CanPutBack)? ActiveChanges { get; private set; }
    /// <summary>What the update notices' switch said when it could not be changed; null otherwise.</summary>
    public string? UpdatesError { get; private set; }

    public event Action<SetupPart>? Changed;

    public bool Smoke => _host.Smoke;
    public Settings Running => _host.Running;
    public IUpdateCheck? Updates => _host.Updates;
    /// <summary>The scan found a RAMMap clear task of your own: the Standby cache step says a rule replaces it, and Next does so.</summary>
    public bool HasOwnClearTask => Facts?.OwnClearTasks.Count > 0;

    /// <summary>A check found the token leak on this Windows build: this session's, or the verdict saved for this build.</summary>
    public bool LeakFound => _host.LeakTestNow == LeakVerdict.Leak || Facts?.TokenLeak == TokenLeakStatus.Leaks;

    public IReadOnlyList<ImpactRow> Impact => ImpactRows.From(Facts, Leak);
    public IReadOnlyList<FixRow> Fixes => FixRows.From(Facts, Leak, _host.ShortcutMade, _host.LauncherInstalled, LeakFound);
    public IReadOnlyList<SummaryRow> Summary => FixRows.Summary(_host.Running, Facts, Leak, _host.ShortcutMade, ActiveChanges, ChangesRead);

    /// <summary>The Ready step read the list of changes (<see cref="ActiveChanges"/> is null when it could not).</summary>
    public bool ChangesRead { get; private set; }
    public CacheChoice Recommended => CacheChoices.Recommended;

    /// <summary>The step's Next applies changes: the Standby cache and Fixes steps.</summary>
    private bool AppliesChanges => Flow.Step is SetupStep.StandbyCache or SetupStep.Fixes;

    /// <summary>The step's Next waits for the scan: the Standby cache and Fixes steps apply what it found.</summary>
    public bool WaitsForScan => !Scanned && AppliesChanges;

    /// <summary>
    /// Next can be pressed now: no change is running, setup is not finished, the step does not wait for the scan, and Next's
    /// wait is over (<see cref="NextHeldFor"/>: the step, what the scan found for it and each Fixes row as it is now have
    /// been on screen for a moment).
    /// </summary>
    public bool CanGoNext => !Busy && !Flow.Finished && !WaitsForScan && NextHeldFor <= TimeSpan.Zero;

    /// <summary>
    /// How much longer Next waits on the Standby cache or Fixes step: <see cref="NextWait"/> from when Next or Back brought
    /// the step up, from when the scan's facts landed while it showed, from when a change on it did not work, or from when
    /// a switch or a Yes/No appeared on the Fixes step while it showed (from when the change ended, when it appeared while a
    /// change ran). Zero on every other step, or once the wait is over. The window enables Next again when it ends.
    /// </summary>
    public TimeSpan NextHeldFor
    {
        get
        {
            if (!AppliesChanges || _heldFrom is not { } from) return TimeSpan.Zero;
            var left = NextWait - System.Diagnostics.Stopwatch.GetElapsedTime(from, _clock());
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    /// <summary>A Fixes row's switch or Yes/No, by row and kind ("TokenGuard:Toggle").</summary>
    private static string InputKey(FixRow r) => $"{r.Fix}:{r.Input}";

    /// <summary>The Fixes step's switches and Yes/Nos (<see cref="InputKey"/>).</summary>
    private HashSet<string> FixInputs() =>
        Fixes.Where(r => r.Input is FixInput.Toggle or FixInput.YesNo).Select(InputKey).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The notices the Fixes page shows its rows again on (<see cref="FixesPage.Sync"/>), and so the only ones that tell the
    /// window of the rows' switches and Yes/Nos: what it was told of is what the page shows, and Next applies only that.
    /// </summary>
    internal static bool ShowsFixRows(SetupPart part) =>
        part is SetupPart.Step or SetupPart.Scan or SetupPart.Leak or SetupPart.Fixes or SetupPart.Busy;

    /// <summary>
    /// Tells the window what changed. Next waits <see cref="NextWait"/> from now, and the window is told that too
    /// (<see cref="SetupPart.Next"/>), when <paramref name="hold"/> (the scan's facts landed on a step that applies changes,
    /// or a change on one did not work), or when the Fixes step, while it shows, now has a switch or a Yes/No the window was
    /// not told of (the scan or the token-leak check ended, or the shortcut was put back from Active changes, which tells
    /// setup nothing). The rows are told only with the notices the Fixes page shows them again on
    /// (<see cref="ShowsFixRows"/>). While a change runs, Next is held anyway and the change applies only what was shown
    /// before it: the wait then starts when the change ends.
    /// </summary>
    private void Tell(SetupPart part, bool hold = false)
    {
        bool wait = hold;
        if (ShowsFixRows(part))
        {
            var inputs = FixInputs();
            wait |= Flow.Step == SetupStep.Fixes && inputs.Except(_toldInputs).Any();
            _toldInputs = inputs;
        }
        if (Busy)
        {
            _appearedWhileBusy |= wait;
            wait = false;
        }
        else if (part == SetupPart.Busy && _appearedWhileBusy)
        {
            _appearedWhileBusy = false;
            wait = AppliesChanges; // the change ended; when it did not work, the step stays with what appeared
        }
        if (wait) _heldFrom = _clock();
        Changed?.Invoke(part);
        if (wait) Changed?.Invoke(SetupPart.Next);
    }

    /// <summary>
    /// Next or Back brought up another step. On a step that applies changes, Next waits <see cref="NextWait"/> from now: a
    /// press meant for the step before (the second click of a double-click, a second Enter) must not apply what this step
    /// offers before it was on screen.
    /// </summary>
    private void Entered()
    {
        if (AppliesChanges) _heldFrom = _clock();
        Tell(SetupPart.Step);
    }

    /// <summary>What a row's change said when it did not work (key: "cache", or a <see cref="Fix"/>), or null.</summary>
    public string? Error(string key) => _errors.GetValueOrDefault(key);

    public static string Key(Fix fix) => fix.ToString();
    public const string CacheKey = "cache";

    /// <summary>A suggestion's button as setup runs it: <see cref="RecommendationActions.Run"/> goes by its id (and a how-to's text).</summary>
    internal static Recommendation Rec(RecommendationId id, string title = "", string? instructions = null) =>
        new(id, title, "", "", RecommendationAction.Apply, 0) { Instructions = instructions };

    /// <summary>
    /// Reads the PC (a new scan; a scan that fails, or takes longer than the wait, leaves the last one, and one that ends
    /// after the wait is shown then), then, on a Windows build that needs it, runs the token-leak check (usually about 20
    /// seconds, never in the CI UI test). <paramref name="stop"/>: the window closed.
    /// </summary>
    public async Task LoadAsync(CancellationToken stop)
    {
        _stop = stop;
        Task<EnvironmentFacts> scan;
        try { scan = _host.Scan(); }
        catch (Exception ex) { scan = Task.FromException<EnvironmentFacts>(ex); }
        Task first;
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            first = await Task.WhenAny(scan, Task.Delay(_scanWait, wait.Token));
            wait.Cancel(); // the wait's timer goes once the scan has ended
        }
        bool late = first != scan;
        if (late)
        {
            if (stop.IsCancellationRequested) return; // the window closed
            _host.Log($"setup: reading the PC took longer than {_scanWait.TotalSeconds:0} seconds; setup goes on with what PoolWatch knew");
            Facts = _host.Facts;
        }
        else
        {
            try { Facts = await scan; }
            catch (Exception ex)
            {
                _host.Log("setup: the PC could not be read: " + ex.Message);
                Facts = _host.Facts;
            }
        }
        Scanned = true;
        if (late) LateScan = TakeLateScan(scan);
        await ShowFacts();
    }

    /// <summary>
    /// The scan setup stopped waiting for, shown when it ends (<see cref="TakeLate"/>); a scan that fails leaves the facts
    /// setup has. The tests wait for it.
    /// </summary>
    internal Task LateScan { get; private set; } = Task.CompletedTask;

    private async Task TakeLateScan(Task<EnvironmentFacts> scan)
    {
        try { _late = await scan; }
        catch (Exception ex)
        {
            _host.Log("setup: the PC could not be read: " + ex.Message);
            return;
        }
        if (!Busy) await TakeLate();
    }

    /// <summary>
    /// The scan that ended after setup stopped waiting for it, shown on every step, never while a change runs (that change
    /// applies the rows as they were shown; Next takes it once the change has ended). The newest facts PoolWatch holds are
    /// taken: a change's own rescan may have read the PC again since.
    /// </summary>
    private Task TakeLate()
    {
        if (_late is not { } facts || _stop.IsCancellationRequested) return Task.CompletedTask;
        _late = null;
        _host.Log("setup: reading the PC ended after setup stopped waiting for it; setup shows what it read");
        Facts = _host.Facts ?? facts;
        return ShowFacts();
    }

    /// <summary>
    /// The facts are in (the scan's, or one that ended late): every step shows them, and on a Windows build that needs it
    /// the token-leak check runs, once (never in the CI UI test). On the Standby cache or Fixes step Next waits a moment
    /// from then (<see cref="NextWait"/>): what its Next applies comes with them (the note that a rule replaces your own
    /// clear task, the rows), and Next may have been waiting for them with the keyboard focus close by.
    /// </summary>
    private async Task ShowFacts()
    {
        bool wanted = !_checkStarted && ImpactRows.NeedsCheck(Facts, _host.Running.TokenGuardEnabled, _host.LeakTestNow);
        if (wanted)
        {
            _checkStarted = true;
            _checking = !_host.Smoke;
            _leakNotRun = _host.Smoke;
        }
        UpdateLeak();
        Tell(SetupPart.Scan, hold: AppliesChanges);
        if (!wanted || !_checking) return;
        bool ok;
        try { (ok, _) = await _host.CheckTokenLeak(_stop); }
        catch (OperationCanceledException) { return; } // the window closed
        catch (Exception ex)
        {
            _host.Log("setup: the token-leak check failed: " + ex.Message);
            ok = false;
        }
        _checking = false;
        _leakNotRun = !ok;
        UpdateLeak();
        Tell(SetupPart.Leak);
    }

    private void UpdateLeak() => Leak = ImpactRows.Leak(Facts, _host.Running.TokenGuardEnabled, _host.LeakTestNow, _checking, _leakNotRun);

    /// <summary>A Standby cache card. Like every choice, held while a change runs (the page shows them disabled then).</summary>
    public void ChooseCache(CacheChoice choice)
    {
        if (Busy || choice == Cache) return;
        Cache = choice;
        Forget(CacheKey);
        Tell(SetupPart.Cache);
    }

    public void SetHours(double hours)
    {
        if (Busy || hours <= 0 || hours == Hours) return;
        Hours = hours;
        Forget(CacheKey);
        Tell(SetupPart.Cache);
    }

    public void SetPoolGb(double gb)
    {
        if (Busy || gb <= 0 || gb == PoolGb) return;
        PoolGb = gb;
        Forget(CacheKey);
        Tell(SetupPart.Cache);
    }

    /// <summary>A Fixes row's switch or Yes/No.</summary>
    public void Choose(Fix fix, bool yes)
    {
        if (Busy) return;
        switch (fix)
        {
            case Fix.SignIn: SignIn = yes; break;
            case Fix.Shortcut: Shortcut = yes; break;
            case Fix.TokenGuard: Guard = yes; break;
            default: return;
        }
        Forget(Key(fix));
        Tell(SetupPart.Fixes);
    }

    /// <summary>A choice changed after its change did not work: its message goes, and Next tries it again.</summary>
    private void Forget(string key)
    {
        _failedWith.Remove(key);
        _errors.Remove(key);
    }

    /// <summary>The update notices' switch, applied at once (a setting, not a change to the PC); its message when it cannot be.</summary>
    public void SetUpdates(bool on)
    {
        if (_host.Updates is not { } updates) return;
        try
        {
            updates.Enabled = on;
            UpdatesError = null;
        }
        catch (Exception ex)
        {
            _host.Log("setup: update notices not changed: " + ex.Message);
            UpdatesError = "PoolWatch could not change this: " + ex.Message;
        }
        Tell(SetupPart.Updates);
    }

    /// <summary>A row's button that acts at once: the Defender how-to (instructions only).</summary>
    public async Task Press(Fix fix, IWin32Window owner)
    {
        if (fix != Fix.Defender || Fixes.FirstOrDefault(r => r.Fix == fix)?.Instructions is not { } text) return;
        var (ok, message) = await _host.Run(Rec(RecommendationId.DefenderDevDrive, "Microsoft Defender's performance mode", text), owner);
        if (ok) _errors.Remove(Key(fix));
        else _errors[Key(fix)] = message;
        Tell(SetupPart.Fixes);
    }

    /// <summary>
    /// Next: the step's choices applied, then the next step. False when it stays: a change did not work (its row says why,
    /// and Next pressed again goes on), a change is still running, or the step waits for the scan.
    /// </summary>
    public async Task<bool> NextAsync(IWin32Window owner)
    {
        if (!CanGoNext) return false;
        if (Flow.Step == SetupStep.Ready) return Finish();
        var shown = _toldInputs; // the switches and Yes/Nos on screen as Next was pressed (telling the window Busy shows any new one)
        Busy = true;
        Tell(SetupPart.Busy);
        bool ok = false;
        try
        {
            ok = Flow.Step switch
            {
                SetupStep.StandbyCache => await ApplyCache(),
                SetupStep.Fixes => await ApplyFixes(owner, shown),
                _ => true,
            };
        }
        finally
        {
            Busy = false;
            // A change that did not work (only the Standby cache and Fixes steps apply one) keeps the step open with its
            // message, and Next pressed again goes on without it: Next waits a moment first, so a second press as quick as a
            // double-click's does not go on past the message unread.
            Tell(SetupPart.Busy, hold: !ok);
            if (_late is not null) LateScan = TakeLate(); // a scan that ended while the change ran
        }
        if (!ok) return false;
        Flow.Forward();
        Entered();
        if (Flow.Step == SetupStep.Ready) await ReadActiveChanges();
        return true;
    }

    public bool Back()
    {
        if (Busy || !Flow.Back()) return false;
        Entered();
        return true;
    }

    /// <summary>The Ready step's button: setup is complete (it does not open again by itself).</summary>
    public bool Finish()
    {
        if (Flow.Finished) return false;
        _host.MarkCompleted();
        Flow.Finish();
        Tell(SetupPart.Step);
        return true;
    }

    /// <summary>
    /// The CI UI test's screenshots: straight to a step, nothing applied (every change refuses in that test). It starts no
    /// wait for Next (<see cref="Entered"/>), so each shot shows Next as the step does once its moment is over; a wait
    /// already running (started on the step before) carries over to the step it opens.
    /// </summary>
    public async Task GoTo(SetupStep step)
    {
        Flow.GoTo(step);
        Tell(SetupPart.Step);
        if (step == SetupStep.Ready) await ReadActiveChanges();
    }

    private async Task ReadActiveChanges()
    {
        ActiveChanges = null;
        ChangesRead = false;
        Tell(SetupPart.Summary);
        ActiveChanges = await _host.ActiveChanges();
        ChangesRead = true;
        Tell(SetupPart.Summary);
    }

    /// <summary>
    /// The Standby cache step's choice. A rule replaces your own clear task exactly when the step says so: the note shows
    /// for a rule while the scan found such a task (<see cref="HasOwnClearTask"/>), and that same read decides here.
    /// </summary>
    private async Task<bool> ApplyCache()
    {
        string choice = $"{Cache} {Hours} {PoolGb}";
        if (_failedWith.GetValueOrDefault(CacheKey) == choice) return true; // Next again: on without it
        bool replace = Cache != CacheChoice.Later && HasOwnClearTask;
        var (ok, message) = await _host.ApplyCache(Cache, Hours, PoolGb, replace);
        if (replace) Facts = _host.Facts ?? Facts; // Replace read the PC again: the task may be gone now
        return Outcome(CacheKey, choice, ok, message);
    }

    /// <param name="shown">
    /// The switches and Yes/Nos the window showed when Next was pressed. One it did not show is left as it is: it appeared
    /// as Next was pressed, or its row changed with nothing told (the shortcut put back from Active changes), and the step
    /// shows it now.
    /// </param>
    private async Task<bool> ApplyFixes(IWin32Window owner, IReadOnlySet<string> shown)
    {
        bool all = true;
        foreach (var row in Fixes)
        {
            var (wanted, id) = row switch
            {
                { Fix: Fix.SignIn, Input: FixInput.Toggle } => (SignIn, RecommendationId.StartAtSignIn),
                { Fix: Fix.Shortcut, Input: FixInput.YesNo } => (Shortcut, RecommendationId.ChatGptShortcut),
                { Fix: Fix.TokenGuard, Input: FixInput.Toggle } => (Guard, RecommendationId.TokenLeakGuard),
                _ => (false, default(RecommendationId)),
            };
            string key = Key(row.Fix);
            if (!wanted || !shown.Contains(InputKey(row)) || _failedWith.GetValueOrDefault(key) == "yes") continue;
            var (ok, message) = await _host.Run(Rec(id, row.Title), owner);
            all &= Outcome(key, "yes", ok, message);
            if (ok) Facts = _host.Facts ?? Facts; // a change that worked read the PC again (the sign-in task)
        }
        UpdateLeak();
        Tell(SetupPart.Fixes);
        return all;
    }

    private bool Outcome(string key, string choice, bool ok, string message)
    {
        if (ok)
        {
            Forget(key);
            return true;
        }
        _failedWith[key] = choice;
        _errors[key] = message + " " + SetupText.NextAgain;
        return false;
    }
}
