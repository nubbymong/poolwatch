using PoolWatch.Analysis;
using PoolWatch.Core.Cache;
using PoolWatch.Core.Changes;
using PoolWatch.Core.Recommendations;
using PoolWatch.Core.Setup;
using PoolWatch.Core.TokenGuard;
using PoolWatch.Engine;
using PoolWatch.Engine.Changes;
using PoolWatch.Engine.Setup;
using PoolWatch.Native;

namespace PoolWatch.Ui;

/// <summary>What the setup guide and the suggestions ask of the app around them (the tray app implements it).</summary>
internal interface IShell
{
    /// <summary>Shows the main window in Advanced view on a tab: 0 Pool causes, 1 Services.</summary>
    void ShowAdvanced(int tab);
    void ShowActiveChanges();
    /// <summary>Opens Report an issue, with that incident's summary offered when a folder is given.</summary>
    void ShowReport(string? incidentDir);
    /// <summary>
    /// Runs the token-leak guard's sign-in check now, with its notices: true when this sign-in is protected or needs no
    /// protection, false when it could not be protected yet (its notice says what to do).
    /// </summary>
    Task<bool> StartTokenGuard();
}

/// <summary>The update notice's switch, as the setup guide shows it; the app binds it to its setting, applied at once.</summary>
internal interface IUpdateCheck
{
    bool Enabled { get; set; }
}

/// <summary>
/// Everything the setup guide and the Simple view's suggestions need, made once by the tray app: the PC's facts (scanned
/// on demand), the hidden suggestions and the guide's completion (setup.json), and the actions behind the buttons.
/// </summary>
internal sealed class SetupContext
{
    private readonly Lazy<bool> _launcherInstalled = new(() => ChatGptShortcut.Launcher() is not null);

    /// <param name="smoke">The CI UI test: every button refuses, and the guide does not start the token-leak check.</param>
    /// <param name="updates">The update notice's switch; null leaves its row out of the guide.</param>
    public SetupContext(MonitorEngine engine, AnalysisPipeline analysis, UiActions actions, LiveSettings live, ChangeLedger ledger, IShell shell, bool smoke,
        IUpdateCheck? updates = null)
    {
        Engine = engine;
        Analysis = analysis;
        Actions = actions;
        Live = live;
        Ledger = ledger;
        Shell = shell;
        Smoke = smoke;
        Updates = updates;
        Probe = new EnvironmentProbe(engine, analysis);
        Store = new RecommendationStore(engine.Paths, engine.Log);
        Recommendations = new RecommendationActions(new WindowsSetupSystem(this), live, shell, Store, smoke);
    }

    public MonitorEngine Engine { get; }
    public AnalysisPipeline Analysis { get; }
    public UiActions Actions { get; }
    public LiveSettings Live { get; }
    public ChangeLedger Ledger { get; }
    public IShell Shell { get; }
    public bool Smoke { get; }
    public IUpdateCheck? Updates { get; }
    public EnvironmentProbe Probe { get; }
    public RecommendationStore Store { get; }
    public RecommendationActions Recommendations { get; }

    /// <summary>The rules' inputs, from memory and the cached facts (nothing is scanned): cheap enough for a once-a-minute refresh.</summary>
    public RecommendationInputs Inputs() => RecommendationInputsBuilder.Build(Engine, Analysis, Probe.Last, Store, Ledger.Records.Has(RecordKind.Shortcut),
        _launcherInstalled.Value, Recommendations.LeakTestNow, DateTime.UtcNow);

    /// <summary>Whether clearing the cache frees kernel pool on this PC, from the clears so far.</summary>
    public ClearEvidence ClearEvidence() => Analysis.Diagnostics.Cache.Evidence();

    /// <summary>
    /// The Simple view's cards (<see cref="Suggest"/>), from what PoolWatch holds now (memory and the cached facts: nothing
    /// is scanned), cheap enough for the once-a-minute refresh.
    /// </summary>
    public IReadOnlyList<Recommendation> Suggestions() => Suggest(Inputs(), Probe.Last, ClearEvidence());

    /// <summary>
    /// Up to 3 offers. The hourly clear shows only where it applies, and once a clear helped here, what it freed is its
    /// reason (<see cref="ClearAdvice"/>).
    /// </summary>
    internal static IReadOnlyList<Recommendation> Suggest(RecommendationInputs inputs, EnvironmentFacts? facts, ClearEvidence evidence) =>
        ClearAdvice.ForSimpleView(RecommendationRules.Current(inputs, max: 4), facts, evidence);

}

/// <summary>The real PC behind <see cref="RecommendationActions"/>.</summary>
internal sealed class WindowsSetupSystem(SetupContext c) : ISetupSystem
{
    private CacheClearer Cache => c.Analysis.Diagnostics.Cache;

    public EnvironmentFacts? Facts => c.Probe.Last;
    public Task<EnvironmentFacts> Rescan() => c.Probe.ScanAsync();
    public bool Elevated => CacheHelper.IsElevated;
    public bool OwnClearTask => Cache.HelperStatus?.LegacyTaskPresent ?? c.Probe.Last?.OwnClearTasks.Count > 0;
    public void RefreshClearStatus() => Cache.RefreshStatus();
    public string? RegisterLogonTask() => new WindowsLogonTasks(c.Engine.Paths).RegisterCurrent();
    public (IReadOnlyList<CacheHelper.LegacyTaskInfo> Removed, string? Error) ReplaceClearTasks(LiveSettings live, string via) =>
        ClearTaskReplacer.Replace(c.Ledger.Records, live, via);
    public Task<LeakVerdict?> CheckTokenLeak(CancellationToken stop) => c.Analysis.Diagnostics.Guard.TestOnly(stop);
    public (bool Ok, string Message) CreateChatGptShortcut(string? iconPath, string via) => ChatGptShortcut.Create(c.Engine.Paths, c.Ledger.Records, iconPath, via);

    public ClearRecord? StartClear() =>
        // Request adds the clear's record before it returns, and only one clear runs at a time: the last record is this one.
        Cache.Request(ClearMode.Standby, ClearTrigger.Manual) ? Cache.History()[^1] : null;

    public string IncidentsDir => c.Engine.Paths.IncidentsDir;
    public void Timeline(string line) => c.Analysis.Diagnostics.RecordOwnerChange(line, null);
}
