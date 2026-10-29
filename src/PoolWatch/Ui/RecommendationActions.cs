using PoolWatch.Core.Cache;
using PoolWatch.Core.Recommendations;
using PoolWatch.Core.Setup;
using PoolWatch.Core.TokenGuard;
using PoolWatch.Engine;
using PoolWatch.Engine.Setup;

namespace PoolWatch.Ui;

/// <summary>
/// What a suggestion's button changes on the PC or reads from it (<see cref="WindowsSetupSystem"/>), so tests stand in
/// for Windows: no test registers a task, clears the cache, runs the leak check or writes a shortcut.
/// </summary>
internal interface ISetupSystem
{
    /// <summary>The facts the suggestions were made from (cached), and a new scan of them (worker thread).</summary>
    EnvironmentFacts? Facts { get; }
    Task<EnvironmentFacts> Rescan();
    bool Elevated { get; }
    /// <summary>A clear task of your own (RAMMap) exists, as last read.</summary>
    bool OwnClearTask { get; }
    /// <summary>Reads the clear status again, so the automatic clear and the next-clear line see a change at once.</summary>
    void RefreshClearStatus();
    /// <summary>Registers PoolWatch to start at sign-in; null, or why not.</summary>
    string? RegisterLogonTask();
    (IReadOnlyList<CacheHelper.LegacyTaskInfo> Removed, string? Error) ReplaceClearTasks(LiveSettings live, string via);
    Task<LeakVerdict?> CheckTokenLeak(CancellationToken stop);
    (bool Ok, string Message) CreateChatGptShortcut(string? iconPath, string via);
    /// <summary>Starts a full clear now and returns its record (measured 2 minutes later); null when one runs or there is no sample yet.</summary>
    ClearRecord? StartClear();
    string IncidentsDir { get; }
    /// <summary>A line on the timeline (findings), from any thread.</summary>
    void Timeline(string line);
}

/// <summary>
/// The one button of each suggestion and setup-guide row. Each runs its change off the UI thread and answers in plain
/// words; each change that worked goes on the timeline, and the lasting ones are listed under Active changes by the code
/// that makes them. Called on the UI thread; dialogs and the shell are used there. Refuses everything in the CI UI test.
/// </summary>
internal sealed class RecommendationActions
{
    private static readonly TimeSpan MeasurePoll = TimeSpan.FromSeconds(5), MeasureWait = TimeSpan.FromMinutes(4);
    private readonly ISetupSystem _system;
    private readonly LiveSettings _live;
    private readonly IShell _shell;
    private readonly RecommendationStore _store;
    private readonly bool _smoke;
    private readonly Action<IWin32Window, string, string> _showInstructions;

    /// <param name="showInstructions">Opens a read-only text (title, text) over a window (default: <see cref="InstructionsDialog"/>).</param>
    public RecommendationActions(ISetupSystem system, LiveSettings live, IShell shell, RecommendationStore store, bool smoke,
        Action<IWin32Window, string, string>? showInstructions = null)
    {
        _system = system;
        _live = live;
        _shell = shell;
        _store = store;
        _smoke = smoke;
        _showInstructions = showInstructions ?? ((owner, title, text) => new InstructionsDialog(title, text).Show(owner));
    }

    /// <summary>The CI UI test's answer to every button.</summary>
    internal const string Refused = "Not in the test run: nothing is changed.";

    /// <summary>
    /// This session's token-leak check, once one gave a verdict. A check that could not run leaves it null, so the Simple
    /// view can still offer the check.
    /// </summary>
    public LeakVerdict? LeakTestNow { get; private set; }

    /// <param name="via">Where the button was, for the timeline and the change list: "a suggestion", "the setup guide".</param>
    public async Task<(bool Ok, string Message)> Run(Recommendation r, IWin32Window owner, string via = "a suggestion")
    {
        if (_smoke) return (false, Refused);
        try
        {
            switch (r.Id)
            {
                case RecommendationId.StartAtSignIn: return await StartAtSignIn(via);
                case RecommendationId.HourlyCacheClear: return await HourlyClear(via);
                case RecommendationId.ReplaceClearTask: return await ReplaceClearTask(via);
                case RecommendationId.TokenLeakGuard: return await TurnOnTokenGuard(via);
                case RecommendationId.CheckTokenLeak: return await CheckTokenLeak(CancellationToken.None);
                case RecommendationId.ChatGptShortcut: return await CreateShortcut(via);
                case RecommendationId.ReviewServices:
                    _shell.ShowAdvanced(1);
                    return (true, "");
                case RecommendationId.DefenderDevDrive:
                    _showInstructions(owner, r.Title, r.Instructions ?? "");
                    return (true, "");
                case RecommendationId.ReportIncident:
                    return Report(r);
                default:
                    return (false, "PoolWatch does not know this suggestion.");
            }
        }
        catch (Exception ex) { return (false, $"That did not work: {ex.Message}"); }
    }

    /// <summary>
    /// The token-leak check (about 20 seconds; it changes nothing). <paramref name="stop"/>: the setup guide closed.
    /// </summary>
    public async Task<(bool Ok, string Message)> CheckTokenLeak(CancellationToken stop)
    {
        if (_smoke) return (false, Refused);
        var verdict = await _system.CheckTokenLeak(stop);
        if (verdict is { } v) LeakTestNow = v;
        return verdict switch
        {
            LeakVerdict.Leak => (true, "This Windows build has the token leak."),
            LeakVerdict.Clean => (true, "PoolWatch's check found no token leak on this Windows build."),
            LeakVerdict.Unclear => (true, "The check gave no clear answer. PoolWatch suggests the guard if it sees the leak later."),
            _ => (false, "The check could not run now: another check is running, or something on this PC already works around the leak."),
        };
    }

    /// <summary>
    /// "Check if this applies to my PC": one full clear now, measured 2 minutes later like every clear, then what it freed
    /// and whose records. <paramref name="stop"/>: the setup guide closed (the clear itself still runs and is measured).
    /// </summary>
    public async Task<(bool Ok, string Message)> TestClear(CancellationToken stop)
    {
        if (_smoke) return (false, Refused);
        if (!_system.Elevated) return (false, "Clearing the cache needs PoolWatch to run as administrator.");
        if (_system.StartClear() is not { } clear) return (false, "A cache clear is running already, or PoolWatch has only just started. Try again in a few minutes.");
        var giveUp = DateTime.UtcNow + MeasureWait;
        while (!clear.Done)
        {
            if (DateTime.UtcNow > giveUp) return (false, "The clear has not been measured yet. Its result will be in the main window.");
            await Task.Delay(MeasurePoll, stop);
        }
        if (clear.Error is { } error) return (false, $"The clear did not run: {error.TrimEnd('.')}.");
        return (true, new ClearEvidence(!ClearPolicy.FreedLittle(clear), clear).Sentence!);
    }

    private async Task<(bool, string)> StartAtSignIn(string via)
    {
        if (await Task.Run(_system.RegisterLogonTask) is { } why) return (false, "PoolWatch could not set itself to start when you sign in. " + why);
        _system.Timeline($"PoolWatch starts when you sign in now (turned on from {via}).");
        await Rescan(); // the rules read whether the task exists from the facts
        return (true, "PoolWatch starts when you sign in.");
    }

    private async Task<(bool, string)> HourlyClear(string via)
    {
        if (!_system.Elevated) return (false, "Clearing the cache needs PoolWatch to run as administrator.");
        if (_system.OwnClearTask) return (false, "Your own clear task (RAMMap) clears the cache. Replace it instead, so the two never both run.");
        await Task.Run(() => _live.Apply($"the hourly automatic cache clear ({via})", s => { s.AutoClearEveryHours = 1; s.AutoClearEnabled = true; }));
        _system.Timeline($"Automatic cache clearing turned on, every hour (from {via}).");
        return (true, "PoolWatch clears the cache every hour.");
    }

    private async Task<(bool, string)> ReplaceClearTask(string via)
    {
        var (removed, error) = await Task.Run(() => _system.ReplaceClearTasks(_live, via));
        // Read again either way: tasks may have been removed before an error, and the automatic clear waits for the status.
        _system.RefreshClearStatus();
        await Rescan();
        if (error is not null) return (false, error);
        _system.Timeline($"Replaced your own clear task with PoolWatch's hourly cache clear (from {via}).");
        return (true, removed.Count switch
        {
            0 => "PoolWatch clears the cache every hour.",
            1 => $"Replaced your clear task \"{removed[0].Path.TrimStart('\\')}\". PoolWatch clears the cache every hour now.",
            _ => $"Replaced your {removed.Count} clear tasks. PoolWatch clears the cache every hour now.",
        });
    }

    private async Task<(bool, string)> TurnOnTokenGuard(string via)
    {
        bool wasOn = _live.Running.TokenGuardEnabled;
        await Task.Run(() => _live.Apply($"the token-leak guard ({via})", s => s.TokenGuardEnabled = true));
        _system.Timeline($"Token-leak guard turned on (from {via}).");
        // Only when it was off: each call after a finished check starts another sign-in check.
        if (!wasOn && !await _shell.StartTokenGuard())
            return (false, "The token-leak guard is on, but it could not protect this sign-in yet. Its notice says what to do.");
        return (true, "The token-leak guard is on.");
    }

    private async Task<(bool, string)> CreateShortcut(string via)
    {
        var facts = _system.Facts;
        // The icon's path is never cached on disk: after a restart the facts have to be read again for it.
        if (facts is { ChatGptInstalled: true, ChatGptIconPath: null }) facts = await Rescan() ?? facts;
        var (ok, message) = await Task.Run(() => _system.CreateChatGptShortcut(facts?.ChatGptIconPath, via));
        if (ok) _system.Timeline($"Created the desktop shortcut \"ChatGPT (no shader cache)\" (from {via}).");
        return (ok, message);
    }

    private (bool, string) Report(Recommendation r)
    {
        // The incident's id is its folder's name; anything else is not opened.
        if (r.Subject is not { Length: > 0 } id || Path.GetFileName(id) != id || id is "." or "..") return (false, "PoolWatch cannot find that incident.");
        _shell.ShowReport(Path.Combine(_system.IncidentsDir, id));
        _store.Dismiss(r.Key, DateTime.UtcNow);
        return (true, "");
    }

    /// <summary>The facts read again after a change they hold; a scan that fails leaves the ones from before (null).</summary>
    private async Task<EnvironmentFacts?> Rescan()
    {
        try { return await _system.Rescan(); }
        catch (Exception) { return null; }
    }
}

/// <summary>
/// Whether to suggest clearing the cache every hour, by file system and by what clears freed on this PC. With a ReFS volume
/// or a Dev Drive it applies (ReFS keeps per-file records alive while their pages are cached). With NTFS only it is
/// suggested once a clear showed that it helps; until then the setup guide offers one test clear instead.
/// </summary>
internal static class ClearAdvice
{
    /// <summary>The guide's test-clear button: longer than a suggestion's 16 characters on purpose, since it says what the click finds out (the guide has the room).</summary>
    public const string CheckButton = "Check if this applies to my PC";

    public static bool HasReFs(EnvironmentFacts? facts) =>
        facts?.Volumes.Any(v => v.IsDevDrive || v.FileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>The Simple view's cards: <paramref name="current"/> without the hourly clear where it is not shown to help, at most <paramref name="max"/>.</summary>
    public static IReadOnlyList<Recommendation> ForSimpleView(IReadOnlyList<Recommendation> current, EnvironmentFacts? facts, ClearEvidence evidence, int max = 3) =>
        current.Where(r => r.Id != RecommendationId.HourlyCacheClear || HasReFs(facts) || evidence.Helps == true)
            .Select(r => r.Id == RecommendationId.HourlyCacheClear && evidence is { Helps: true, Sentence: { } shown } ? r with { Why = shown } : r)
            .Take(max).ToList();

    /// <summary>
    /// The setup guide's hourly row, and whether its button is the test clear: offered with what a clear freed here, or
    /// because a ReFS drive is there; a plain line when a full clear freed little; else the test clear. Once the automatic
    /// clear is on, the row confirms what clearing freed here (on a ReFS PC only once a clear helped: a clear an hour after
    /// the last one can free little there, and the hourly clear still applies).
    /// </summary>
    public static (Recommendation Row, bool TestClear) ForGuide(Recommendation row, EnvironmentFacts? facts, ClearEvidence evidence)
    {
        if (row.Id != RecommendationId.HourlyCacheClear) return (row, false);
        if (row.State == RecommendationState.Done)
            return (evidence.Sentence is { } found && (evidence.Helps == true || !HasReFs(facts)) ? row with { Why = row.Why + " " + found } : row, false);
        if (row.State != RecommendationState.Offer) return (row, false);
        if (evidence is { Helps: true, Sentence: { } helped }) return (row with { Why = helped }, false);
        if (HasReFs(facts)) return (row with { Why = ReFsWhy(facts!) }, false);
        if (evidence is { Helps: false, Sentence: { } little }) return (row with { Why = little, Button = "", State = RecommendationState.Info }, false);
        return (row with
        {
            Why = "PoolWatch found no ReFS drive or Dev Drive on this PC. On NTFS, clearing the cache often frees little. PoolWatch can " +
                  "clear it once now and show what it freed (about 2 minutes).",
            Button = CheckButton,
        }, true);
    }

    private static string ReFsWhy(EnvironmentFacts facts)
    {
        var letters = facts.Volumes.Where(v => v.IsDevDrive || v.FileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase))
            .Select(v => EnvironmentFacts.Letter(v.Root)).ToList();
        string drives = letters.Count == 1 ? $"a ReFS drive ({letters[0]})" : $"ReFS drives ({string.Join(", ", letters)})";
        return $"This PC has {drives}. Windows keeps records of recently used files on ReFS in {PoolName.InText}, and they can grow " +
               "by gigabytes. An hourly clear frees them; your files are not touched.";
    }
}

/// <summary>
/// What the token-leak guard does to the PC, said wherever it is offered: why, what you will notice (the focus trade-off),
/// and how long it lasts and how to stop it.
/// </summary>
internal static class TokenGuardWords
{
    public const string Why = $"On some Windows 11 versions, a Windows bug leaks a little of {PoolName.InText} every time one program starts another. " +
                              "Coding assistants start thousands of programs an hour, so it adds up quickly. PoolWatch tested this PC and found the leak.";
    public const string Notice = "Other programs can bring their windows to the front by themselves, even while you're typing somewhere else (for example " +
                                 "a chat app or an installer popping up and taking your keystrokes). Most people find it rare and easy to live with.";
    public const string Undo = "It lasts until you sign out, and PoolWatch turns it back on each time it starts. To stop it, turn it off in Active changes.";

    /// <summary>The lines shown under an offer of the guard: what you will notice, and how to undo it.</summary>
    public static string Details => $"What you'll notice: {Notice}{Environment.NewLine}Undo: {Undo}";

    /// <summary>The suggestion's one-line trade-off, the last sentence of the guard's reason (the suggestions' own words).</summary>
    private const string ShortTradeOff = "With it on, any app may bring its window to the front.";

    /// <summary>The guard's reason on a card that also shows <see cref="Details"/>: without its one-line trade-off, said there in full.</summary>
    public static string CardWhy(string why) =>
        why.EndsWith(" " + ShortTradeOff, StringComparison.Ordinal) ? why[..^(ShortTradeOff.Length + 1)] : why;
}
