using PoolWatch.Core.Cache;
using PoolWatch.Core.Recommendations;
using PoolWatch.Core.Setup;
using PoolWatch.Core.TokenGuard;
using PoolWatch.Engine;
using PoolWatch.Engine.Setup;
using PoolWatch.Ui.Setup;

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
    private readonly ISetupSystem _system;
    private readonly LiveSettings _live;
    private readonly IShell _shell;
    private readonly RecommendationStore _store;
    private readonly bool _smoke;
    private readonly Action<IWin32Window, string, string> _showInstructions;
    private readonly Func<Core.Model.Moment> _clock;

    /// <param name="showInstructions">Opens a read-only text (title, text) over a window (default: <see cref="InstructionsDialog"/>).</param>
    /// <param name="clock">Reads the two clocks (<see cref="Core.Model.Moment.Now"/>; a test gives its own).</param>
    public RecommendationActions(ISetupSystem system, LiveSettings live, IShell shell, RecommendationStore store, bool smoke,
        Action<IWin32Window, string, string>? showInstructions = null, Func<Core.Model.Moment>? clock = null)
    {
        _clock = clock ?? Core.Model.Moment.Now;
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

    /// <summary>The Windows builds the token-leak watch has checked (or tried to) in this session.</summary>
    private readonly HashSet<string> _watchTried = new(StringComparer.Ordinal);
    private Task<(bool Ok, string Message)>? _watching;

    /// <summary>
    /// The token-leak watch (<see cref="TokenLeakWatch.ShouldCheck"/>): after setup found no leak, or could not tell,
    /// PoolWatch runs the check-only test again by itself in the background once Windows was updated (or when no check
    /// ever gave a verdict), once per Windows build in a session. It changes nothing on the PC and never turns the guard
    /// on: when the leak shows, the Simple view offers the guard. Returns the check it started, else null. UI thread.
    /// </summary>
    public Task<(bool Ok, string Message)>? WatchTokenLeak(RecommendationInputs inputs)
    {
        if (_smoke || _watching is { IsCompleted: false } || !TokenLeakWatch.ShouldCheck(inputs, _watchTried)) return null;
        _watchTried.Add(inputs.Facts!.Build);
        return _watching = CheckTokenLeak(CancellationToken.None);
    }

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
    /// Settings' "Create shortcut": the Codex shortcut made again (the desktop shortcut that starts ChatGPT desktop without
    /// its shader cache), recorded for Undo as every shortcut PoolWatch makes. Refused in the CI UI test.
    /// </summary>
    public async Task<(bool Ok, string Message)> CreateCodexShortcut(string via)
    {
        if (_smoke) return (false, Refused);
        try { return await CreateShortcut(via); }
        catch (Exception ex) { return (false, $"That did not work: {ex.Message}"); }
    }

    /// <summary>
    /// The token-leak check (usually about 20 seconds; it changes nothing). <paramref name="stop"/>: the setup guide closed.
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

    private async Task<(bool, string)> StartAtSignIn(string via)
    {
        if (await Task.Run(_system.RegisterLogonTask) is { } why) return (false, "PoolWatch could not set itself to start when you sign in. " + why);
        _system.Timeline($"PoolWatch starts when you sign in now (turned on from {via}).");
        await Rescan(); // the rules read from the facts whether PoolWatch starts when you sign in
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
        _store.Dismiss(r.Key, _clock());
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
/// or a Dev Drive it applies (ReFS keeps per-file records alive while their pages are cached), and so it does where the
/// drives could not all be read or before the scan has read them, as one of them may be such a drive (<see
/// cref="MayHaveReFs"/>). With NTFS only it is suggested once a clear showed that it helps, or when file records a clear
/// frees grew in the last day (<see cref="ForSimpleView"/>).
/// </summary>
internal static class ClearAdvice
{
    /// <summary>A ReFS drive or a Dev Drive was found on this PC.</summary>
    public static bool HasReFs(EnvironmentFacts? facts) =>
        facts?.Volumes.Any(v => v.IsDevDrive == true || v.FileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>
    /// A ReFS drive or a Dev Drive was found, or may be there: the drives were not read (no facts yet), or not all of them
    /// (<see cref="EnvironmentFacts.VolumesNotAllRead"/>). Only a scan that read every drive and found none of them on ReFS
    /// says this PC has NTFS only.
    /// </summary>
    public static bool MayHaveReFs(EnvironmentFacts? facts) => facts is null || facts.VolumesNotAllRead || HasReFs(facts);

    /// <summary>
    /// The Simple view's cards: <paramref name="current"/> without the hourly clear where it is not shown to help, at most
    /// <paramref name="max"/>. It is shown with a ReFS drive or a Dev Drive, or where the drives could not all be read or
    /// before the scan has read them (<see cref="MayHaveReFs"/>), once a clear here freed enough, or when file records a
    /// clear frees grew in the last day (<paramref name="fileRecordsGrew"/>): so on an NTFS-only PC where setup's "Decide
    /// later" left clearing off, PoolWatch still suggests it when the pool shows it would help.
    /// </summary>
    public static IReadOnlyList<Recommendation> ForSimpleView(IReadOnlyList<Recommendation> current, EnvironmentFacts? facts, ClearEvidence evidence, int max = 3,
        bool fileRecordsGrew = false) =>
        current.Where(r => r.Id != RecommendationId.HourlyCacheClear || MayHaveReFs(facts) || evidence.Helps == true || fileRecordsGrew)
            .Select(r => r.Id == RecommendationId.HourlyCacheClear && evidence is { Helps: true, Sentence: { } shown } ? r with { Why = shown } : r)
            .Take(max).ToList();
}

/// <summary>
/// What the token-leak guard does to the PC, said wherever it is offered: why, what you will notice (the focus trade-off),
/// and how long it lasts and how to stop it.
/// </summary>
internal static class TokenGuardWords
{
    /// <summary>Why the guard is there, without saying that a check found the leak here (<see cref="Tested"/> says that).</summary>
    public const string Reason = $"On some Windows 11 versions, a Windows bug leaks a little of {PoolName.InText} every time one program starts another. " +
                                 "Coding assistants start thousands of programs an hour, so it adds up quickly.";
    /// <summary>The sentence said only where a check found the leak on this PC.</summary>
    public const string Tested = "PoolWatch tested this PC and found the leak.";
    public const string Why = Reason + " " + Tested;
    public const string Notice = "Other programs can bring their windows to the front by themselves, even while you're typing somewhere else (for example " +
                                 "a chat app or an installer popping up and taking your keystrokes). Most people find it rare and easy to live with.";
    public const string Undo = "It lasts until you sign out, and PoolWatch turns it back on each time it starts. To stop it, turn it off in Active changes, " +
                               "which also puts back the setting it changed when Windows allows it; one it found already changed lasts until you sign out.";

    /// <summary>The lines shown under an offer of the guard: what you will notice, and how to undo it.</summary>
    public static string Details => $"What you'll notice: {Notice}{Environment.NewLine}Undo: {Undo}";

    /// <summary>The suggestion's one-line trade-off, the last sentence of the guard's reason (the suggestions' own words).</summary>
    private const string ShortTradeOff = "With it on, any app may bring its window to the front.";

    /// <summary>The guard's reason on a card that also shows <see cref="Details"/>: without its one-line trade-off, said there in full.</summary>
    public static string CardWhy(string why) =>
        why.EndsWith(" " + ShortTradeOff, StringComparison.Ordinal) ? why[..^(ShortTradeOff.Length + 1)] : why;
}
