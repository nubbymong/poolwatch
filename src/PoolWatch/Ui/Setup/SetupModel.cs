using System.Globalization;
using PoolWatch.Core;
using PoolWatch.Core.Impact;
using PoolWatch.Core.Setup;
using PoolWatch.Core.TokenGuard;

namespace PoolWatch.Ui.Setup;

/// <summary>The seven steps of the first-run setup, numbered as the rail shows them.</summary>
internal enum SetupStep { Welcome = 1, YourPc, Safety, StandbyCache, Fixes, Services, Ready }

/// <summary>When PoolWatch empties the standby cache: the four choices of the Standby cache step.</summary>
internal enum CacheChoice { Schedule, PoolSize, Either, Later }

/// <summary>A known cause of a growing kernel pool, one row of "How your PC is impacted".</summary>
internal enum Cause { DevDriveRecords, Defender, CodexShaderCache, BackgroundServices, TokenLeak }

/// <summary>Where this session's look for the Windows token leak stands.</summary>
internal enum LeakState
{
    /// <summary>The check runs now (usually about 20 seconds).</summary>
    Checking,
    Found,
    /// <summary>The check found none on this build, or this build is older than the ones that have it.</summary>
    NotFound,
    NotAffected,
    Unclear,
    /// <summary>Something on the PC already works around it, so the check cannot measure it.</summary>
    WorkedAround,
    /// <summary>The check could not run, or PoolWatch could not tell.</summary>
    NotChecked,
    /// <summary>The token-leak guard is on: the leak cannot build up.</summary>
    GuardOn,
}

/// <summary>
/// The words of the first-run setup, from the approved design (first run, version 6), with the changes agreed after it:
/// no restore point, no tour button, and the token-leak guard's own words wherever it is offered.
/// </summary>
internal static class SetupText
{
    public const string WindowTitle = "Set up PoolWatch";
    public const string Brand = "PoolWatch";
    public const string HowItWorks = "How PoolWatch works";
    public const string Next = "Next", Back = "Back", Working = "Working...";
    /// <summary>Under a change that did not work, when Next stays on the step.</summary>
    public const string NextAgain = "Press Next again to go on without it.";

    // 1 Welcome
    public const string WelcomeTitle = "Welcome to PoolWatch";
    public const string Mission = "Keeps your Windows 11 PC fast and stable through long days of agentic coding.";
    public static readonly IReadOnlyList<(SetupIcon Icon, string Title, string Text)> Pillars =
    [
        (SetupIcon.Pool, "Watches the kernel pool", "Shows what is making it grow, in plain words."),
        (SetupIcon.Sweep, "Stops the build-up", "Empties the standby cache before the pool slows you down."),
        (SetupIcon.Wrench, "Fixes known issues", "The token leak, Codex's shader cache, Defender on Dev Drives."),
        (SetupIcon.Layers, "Trims background work", "If you want: services, scheduled tasks and startup apps you don't need."),
    ];
    public const string LevelQuestion = "Choose your setup and app experience";
    public const string Simple = "Simple", Advanced = "Advanced", Recommended = "Recommended";
    public const string SimpleText = "Setup picks safe settings for you. The app shows the essentials.";
    public const string AdvancedText = "Setup shows every choice. The app shows every number and tool.";
    public const string LevelLater = "You can change this later in the app.";
    public const string Duration = "Setup takes about 3 minutes";
    public const string GetStarted = "Get started";

    // 2 Your PC
    public const string ImpactTitle = "How your PC is impacted";
    public const string ImpactLead = "PoolWatch checked this PC for the known causes of a growing kernel pool.";
    public const string Looking = "Looking at your PC...";
    /// <summary>Under a step whose Next waits for the scan (its choices depend on what it finds).</summary>
    public const string WaitForScan = "Looking at your PC... Next is ready in a moment.";
    public const string NotRead = "PoolWatch could not read this PC's details. It keeps watching, and suggests fixes in the app.";
    public const string StillWatching = "Still watching";
    public const string CheckingNow = "Checking...";

    // 3 Safety
    public const string SafetyTitle = "You stay in control";
    public static readonly IReadOnlyList<string> Promises =
    [
        "Nothing changes without your OK.",
        "Anything PoolWatch changes can be put back, one at a time or all at once.",
        "Uninstalling puts everything back.",
    ];

    // 4 Standby cache
    public const string CacheTitle = "When should PoolWatch empty the standby cache?";
    public const string CacheLead = "Spare copies of files already saved on disk. Emptying it also frees the kernel pool records kept for them. Nothing is lost.";
    public const string ChartLegend = "Kernel pool (paged + non-paged)";
    public const string Schedule = "On a schedule", PoolSize = "At a pool size", Either = "Whichever comes first", Later = "Decide later";
    public const string Every = "Every", WhenItReaches = "When it reaches", Or = "or";
    public const string LaterText = "PoolWatch watches the pool and suggests when.";
    /// <summary>What a clear is like while it runs, said where clearing is turned on.</summary>
    public const string Stutter = "Video or the mouse may stutter for a few seconds while it empties.";
    /// <summary>With a RAMMap clear task of your own: a rule here replaces it, so the two never both run.</summary>
    public const string ReplacesOwnTask =
        "A task of your own clears the cache with RAMMap. Choosing a rule here replaces it, so the two never both run; your task is saved, so you can put it back.";

    // 5 Fixes
    public const string FixesTitle = "Fixes for this PC";
    public const string Why = "Why?";
    public const string On = "On", Off = "Off", Yes = "Yes", No = "No";
    public const string ShowMeHow = "Show me how";

    // 6 Services
    public const string LaterTag = "For later, in the app";
    public const string ServicesTitle = "Trim background services, when you're ready";
    public const string ServicesLead = "Setup doesn't change any services. Some run all day without you needing them, and PoolWatch can help you trim them later.";
    public const string WindowsServices = "Windows services", KnowsThese = "PoolWatch knows these";
    public const string WindowsServicesText = "It tells you what each one does and what you'd lose, so you can turn off the ones you don't need.";
    public const string OtherServices = "Services from other apps", WithYourAgent = "Decide with your AI agent";
    public static readonly IReadOnlyList<string> AgentSteps = ["PoolWatch gives you a prompt", "Your agent asks how you use your apps", "PoolWatch imports its answer"];
    public const string PutBack = "Anything you turn off can be put back, one at a time or all at once.";
    public const string FindIt = "Find it in PoolWatch under Services.";

    // 7 Ready
    public const string ReadyTitle = "PoolWatch is watching";
    public const string ReadyLead = "You can change any of this later.";
    public const string TrayNote = "PoolWatch lives here, next to the clock.";
    public const string OpenApp = "Open PoolWatch";

    /// <summary>The rail's name for a step.</summary>
    public static string Rail(SetupStep step) => step switch
    {
        SetupStep.Welcome => "Welcome",
        SetupStep.YourPc => "Your PC",
        SetupStep.Safety => "Safety",
        SetupStep.StandbyCache => "Standby cache",
        SetupStep.Fixes => "Fixes",
        SetupStep.Services => "Services",
        _ => "Ready",
    };

    /// <summary>"Fix in step 4": where setup fixes a cause, never "Fixed" (nothing is fixed until that step).</summary>
    public static string FixIn(SetupStep step) => $"Fix in step {(int)step}";
    public const string TrimInApp = "Trim in the app";

    /// <summary>"High impact", "Medium impact", "Low impact", "None".</summary>
    public static string Level(ImpactLevel level) => level == ImpactLevel.None ? "None" : $"{level} impact";

    /// <summary>"1 hour", "2 hours", "1.5 hours".</summary>
    public static string Hours(double hours) => hours == 1 ? "1 hour" : $"{Number(hours)} hours";

    /// <summary>"8 GB".</summary>
    public static string Gb(double gb) => $"{Number(gb)} GB";

    public static string Number(double n) => n.ToString("0.#", CultureInfo.CurrentCulture);

    /// <summary>"D:", "D: and E:", "D:, E: and F:".</summary>
    public static string Drives(IReadOnlyList<string> roots)
    {
        var letters = roots.Select(EnvironmentFacts.Letter).ToList();
        return letters.Count <= 1 ? string.Concat(letters) : string.Join(", ", letters.Take(letters.Count - 1)) + " and " + letters[^1];
    }
}

/// <summary>
/// Which step setup is on and whether its window may close. The first run cannot be closed or skipped: it ends only on the
/// Ready step's button (PoolWatch exiting, or Windows shutting down, still closes it, and the next start opens setup again).
/// Opened again later from the tray, it can be cancelled.
/// </summary>
internal sealed class SetupFlow(bool firstRun)
{
    public static readonly IReadOnlyList<SetupStep> Steps = Enum.GetValues<SetupStep>();

    public bool FirstRun { get; } = firstRun;
    public SetupStep Step { get; private set; } = SetupStep.Welcome;
    /// <summary>The Ready step's button was pressed: setup is complete.</summary>
    public bool Finished { get; private set; }

    public bool CanGoBack => Step != SetupStep.Welcome && !Finished;

    /// <summary>The next step; false on the last one.</summary>
    public bool Forward()
    {
        if (Step == SetupStep.Ready || Finished) return false;
        Step++;
        return true;
    }

    public bool Back()
    {
        if (!CanGoBack) return false;
        Step--;
        return true;
    }

    /// <summary>Straight to a step, without applying anything on the way (the CI UI test's screenshots).</summary>
    public void GoTo(SetupStep step) => Step = step;

    public void Finish() => Finished = true;

    /// <summary>
    /// Whether the window may close for <paramref name="reason"/>: always once setup is finished or when it is not the
    /// first run (a re-run is cancelled by closing it); on the first run only when PoolWatch or Windows closes it.
    /// </summary>
    public bool MayClose(CloseReason reason) => Finished || !FirstRun || reason is not (CloseReason.UserClosing or CloseReason.None);

    /// <summary>How the rail marks <paramref name="step"/>: done, the one shown, or still to come.</summary>
    public RailMark Mark(SetupStep step) => step < Step || Finished ? RailMark.Done : step == Step ? RailMark.Now : RailMark.Ahead;
}

internal enum RailMark { Done, Now, Ahead }

/// <summary>One row of "How your PC is impacted": the cause, what PoolWatch found, its level and where setup fixes it.</summary>
/// <param name="Level">Null while the token-leak check runs.</param>
/// <param name="Under">The line under the level: "Fix in step 4", "Trim in the app", "Still watching", or none (and none while the check runs).</param>
/// <param name="FixStep">The step that fixes it, when setup does.</param>
internal sealed record ImpactRow(Cause Cause, SetupIcon Icon, string Title, string Detail, ImpactLevel? Level, string? Under, SetupStep? FixStep);

/// <summary>
/// The scan of the PC as "How your PC is impacted" shows it: one row per known cause, its level from
/// <see cref="ImpactRules.FromFacts"/> (a true first run has nothing measured yet), and the step that fixes it. A level
/// of None says PoolWatch is still watching for it. Pure.
/// </summary>
internal static class ImpactRows
{
    /// <summary>Volumes on ReFS: every Dev Drive, and any other drive formatted with ReFS.</summary>
    public static IReadOnlyList<VolumeFact> ReFs(EnvironmentFacts f) =>
        f.Volumes.Where(v => v.IsDevDrive || v.FileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>The Dev Drives Defender checks before their files open (the drives the Defender advice is for).</summary>
    public static IReadOnlyList<string> ScannedFirst(EnvironmentFacts f) =>
        f.Volumes.Where(v => v.IsDevDrive && ImpactRules.FromFacts.ScannedFirst(f.DefenderPerformanceModeOn, v.Trusted)).Select(v => v.Root).ToList();

    public static ImpactLevel DefenderLevel(EnvironmentFacts f) =>
        ImpactRules.FromFacts.DefenderScanning(f.DefenderRealTimeOn, f.DefenderPerformanceModeOn, f.Volumes.Where(v => v.IsDevDrive).Select(v => v.Trusted).ToList());

    /// <summary>The rows, in the design's order; empty when the PC could not be read.</summary>
    public static IReadOnlyList<ImpactRow> From(EnvironmentFacts? f, LeakState leak)
    {
        if (f is null) return [];
        return [DevDrive(f), Defender(f), Codex(f), Services(f), TokenLeak(leak)];
    }

    private static ImpactRow Row(Cause cause, SetupIcon icon, string title, string detail, ImpactLevel? level, SetupStep? fix, string? action = null) =>
        new(cause, icon, title, detail, level,
            level switch { null => null, ImpactLevel.None => SetupText.StillWatching, _ => fix is { } s ? SetupText.FixIn(s) : action },
            level is null or ImpactLevel.None ? null : fix);

    private static ImpactRow DevDrive(EnvironmentFacts f)
    {
        var refs = ReFs(f);
        var level = ImpactRules.FromFacts.RefsMetadata(refs.Count > 0);
        string detail;
        if (refs.Count == 0) detail = "No Dev Drive or ReFS drive on this PC.";
        else
        {
            string drives = SetupText.Drives(refs.Select(v => v.Root).ToList());
            bool one = refs.Count == 1, allDevDrives = refs.All(v => v.IsDevDrive);
            detail = allDevDrives
                ? $"{drives} {(one ? "is a Dev Drive" : "are Dev Drives")}, and Windows rarely empties {(one ? "its" : "their")} cache."
                : $"{drives} {(one ? "uses" : "use")} ReFS, and Windows rarely empties {(one ? "its" : "their")} cache.";
        }
        return Row(Cause.DevDriveRecords, SetupIcon.Chip, "Dev Drive records piling up", detail, level, SetupStep.StandbyCache);
    }

    private static ImpactRow Defender(EnvironmentFacts f)
    {
        bool devDrives = f.Volumes.Any(v => v.IsDevDrive);
        string title = devDrives ? "Defender scanning your Dev Drives" : "Defender checking your files";
        var level = DefenderLevel(f);
        if (level == ImpactLevel.None)
        {
            // Only what was read is said: performance mode covers the Dev Drives only when it is on and each is trusted.
            bool covered = f.DefenderPerformanceModeOn == true && f.Volumes.Where(v => v.IsDevDrive).All(v => v.Trusted == true);
            string why = f.DefenderRealTimeOn switch
            {
                false => "Defender's real-time protection is off.",
                null => "PoolWatch could not read Defender's settings. Another antivirus may be in use.",
                true when covered => "Performance mode covers your Dev Drives.",
                true => "PoolWatch could not read whether performance mode covers your Dev Drives.",
            };
            return Row(Cause.Defender, SetupIcon.Shield, title, why, level, null);
        }
        if (!devDrives)
            // Nothing for setup to change: Dev Drive performance mode needs a Dev Drive.
            return Row(Cause.Defender, SetupIcon.Shield, title, "Real-time protection checks each file before it opens. On a Dev Drive it can check them after.", level, null);
        var drives = ScannedFirst(f);
        bool one = drives.Count == 1;
        string detail = f.DefenderPerformanceModeOn == false
            ? $"Performance mode is off for {SetupText.Drives(drives)}."
            : $"{SetupText.Drives(drives)} {(one ? "is" : "are")} not trusted, so performance mode does not cover {(one ? "it" : "them")}.";
        return Row(Cause.Defender, SetupIcon.Shield, title, detail, level, SetupStep.Fixes);
    }

    private static ImpactRow Codex(EnvironmentFacts f) =>
        Row(Cause.CodexShaderCache, SetupIcon.Chat, "Codex shader cache", f.ChatGptInstalled ? "The Codex app is installed." : "The Codex app is not installed.",
            ImpactRules.FromFacts.CodexShaderCache(f.ChatGptInstalled), SetupStep.Fixes);

    private static ImpactRow Services(EnvironmentFacts f) =>
        Row(Cause.BackgroundServices, SetupIcon.Gear, "Background services",
            f.OptionalServicesRunning > 0 ? "Services you may not need, running all day." : "None of the services PoolWatch knows is running.",
            ImpactRules.FromFacts.BackgroundServices(f.OptionalServicesRunning), null, SetupText.TrimInApp);

    private static ImpactRow TokenLeak(LeakState leak)
    {
        const string watching = "PoolWatch keeps watching, and offers the fix if it appears.";
        string detail = leak switch
        {
            LeakState.Checking => "Checking this Windows build for it (usually about 20 seconds)...",
            LeakState.Found => "PoolWatch's check found it on this Windows build.",
            LeakState.NotFound => "Not found now. " + watching,
            LeakState.NotAffected => "This Windows build does not have it. " + watching,
            LeakState.Unclear => "The check gave no clear answer. " + watching,
            LeakState.WorkedAround => "Something on this PC already works around it, so PoolWatch could not check. " + watching,
            LeakState.GuardOn => "The token-leak guard is on, so it cannot build up.",
            _ => "PoolWatch could not check for it now. " + watching,
        };
        ImpactLevel? level = leak == LeakState.Checking ? null : ImpactRules.FromFacts.TokenLeak(leak == LeakState.Found);
        return Row(Cause.TokenLeak, SetupIcon.Key, "Windows token leak", detail, level, SetupStep.Fixes);
    }

    /// <summary>
    /// Where the look for the token leak stands: the guard on; this session's verdict; the check running, or why it could
    /// not run; else what the scan's facts say (a saved verdict for this build, or the live setting).
    /// </summary>
    /// <param name="now">This session's verdict, once a check gave one.</param>
    /// <param name="checking">The check runs now.</param>
    /// <param name="notRun">The check was wanted but could not run (or it is the CI UI test, which never runs it).</param>
    public static LeakState Leak(EnvironmentFacts? f, bool guardOn, LeakVerdict? now, bool checking, bool notRun)
    {
        if (guardOn) return LeakState.GuardOn;
        if (now is { } v) return v switch { LeakVerdict.Leak => LeakState.Found, LeakVerdict.Clean => LeakState.NotFound, _ => LeakState.Unclear };
        if (checking) return LeakState.Checking;
        return f?.TokenLeak switch
        {
            TokenLeakStatus.Leaks => LeakState.Found,
            TokenLeakStatus.Clean => LeakState.NotFound,
            TokenLeakStatus.NotAffected => LeakState.NotAffected,
            TokenLeakStatus.WorkedAroundAlready => LeakState.WorkedAround,
            TokenLeakStatus.NeedsTest when !notRun => LeakState.Checking,
            _ => LeakState.NotChecked,
        };
    }

    /// <summary>Whether setup runs the token-leak check: the facts say this build needs one, and nothing answered it yet.</summary>
    public static bool NeedsCheck(EnvironmentFacts? f, bool guardOn, LeakVerdict? now) =>
        f is { TokenLeak: TokenLeakStatus.NeedsTest } && !guardOn && now is null;
}

/// <summary>
/// The Standby cache step's four choices and the two settings they stand for (0 = that rule is off): "On a schedule"
/// clears every N hours, "At a pool size" when the kernel pool reaches N GB, "Whichever comes first" both, and "Decide
/// later" leaves automatic clearing off (PoolWatch keeps watching and suggests it when it helps). Pure.
/// </summary>
internal static class CacheChoices
{
    public static readonly IReadOnlyList<double> HourOptions = [1, 2, 3, 4, 6, 8, 12, 24];
    public static readonly IReadOnlyList<double> GbOptions = [2, 4, 6, 8, 12, 16, 24, 32];

    /// <summary>The list a field offers, with <paramref name="current"/> in it when it is a value of its own (set in Settings).</summary>
    public static IReadOnlyList<double> Options(IReadOnlyList<double> list, double current) =>
        current > 0 && !list.Contains(current) ? list.Append(current).Order().ToList() : list;

    /// <summary>
    /// What the settings hold now, as a choice and its two numbers. A rule that is off keeps the default number for its
    /// field (1 hour, 8 GB, as <see cref="Settings"/> starts), so the other choices show a value.
    /// </summary>
    public static (CacheChoice Choice, double Hours, double PoolGb) Read(Settings s)
    {
        var defaults = new Settings();
        double hours = s.AutoClearEveryHours > 0 ? s.AutoClearEveryHours : defaults.AutoClearEveryHours;
        double gb = s.AutoClearPoolGb > 0 ? s.AutoClearPoolGb : defaults.AutoClearPoolGb;
        var choice = !s.AutoClearEnabled ? CacheChoice.Later : (s.AutoClearEveryHours > 0, s.AutoClearPoolGb > 0) switch
        {
            (true, true) => CacheChoice.Either,
            (true, false) => CacheChoice.Schedule,
            (false, true) => CacheChoice.PoolSize,
            _ => CacheChoice.Later,
        };
        return (choice, hours, gb);
    }

    /// <summary>
    /// The recommended choice, on every PC: whichever comes first, as the approved design marks it. It does not depend on
    /// the scan, so it never moves while the step is shown.
    /// </summary>
    public const CacheChoice Recommended = CacheChoice.Either;

    /// <summary>
    /// The choice the step opens with: what is in effect when automatic clearing is on; on the first run with it off, the
    /// recommended one; opened again with it off, "Decide later" (what is in effect).
    /// </summary>
    public static CacheChoice Initial(Settings s, bool firstRun) =>
        s.AutoClearEnabled ? Read(s).Choice : firstRun ? Recommended : CacheChoice.Later;

    /// <summary>The settings a choice writes: on with its rules, or (Decide later) off, the numbers left as they are.</summary>
    public static (bool Enabled, double? EveryHours, double? PoolGb) Writes(CacheChoice choice, double hours, double gb) => choice switch
    {
        CacheChoice.Schedule => (true, hours, 0),
        CacheChoice.PoolSize => (true, 0, gb),
        CacheChoice.Either => (true, hours, gb),
        _ => (false, null, null),
    };

    /// <summary>The settings already hold what the choice would write.</summary>
    public static bool InEffect(Settings s, CacheChoice choice, double hours, double gb)
    {
        var (enabled, every, size) = Writes(choice, hours, gb);
        return enabled ? s.AutoClearEnabled && s.AutoClearEveryHours == every && s.AutoClearPoolGb == size : !s.AutoClearEnabled;
    }

    /// <summary>The Ready step's line: "Emptied every hour, or at 8 GB", "Emptied every 2 hours", "Emptied at 8 GB", "PoolWatch suggests when".</summary>
    public static string Summary(Settings s)
    {
        if (!s.AutoClearEnabled || (s.AutoClearEveryHours <= 0 && s.AutoClearPoolGb <= 0)) return "PoolWatch suggests when";
        string? every = s.AutoClearEveryHours > 0 ? (s.AutoClearEveryHours == 1 ? "every hour" : $"every {SetupText.Hours(s.AutoClearEveryHours)}") : null;
        string? size = s.AutoClearPoolGb > 0 ? $"at {SetupText.Gb(s.AutoClearPoolGb)}" : null;
        return "Emptied " + (every, size) switch { ({ } e, { } g) => $"{e}, or {g}", ({ } e, null) => e, _ => size };
    }

    /// <summary>The timeline's words for a choice that turns clearing on: "every 1 hour, or at 8 GB".</summary>
    public static string Words(CacheChoice choice, double hours, double gb) => choice switch
    {
        CacheChoice.Schedule => $"every {SetupText.Hours(hours)}",
        CacheChoice.PoolSize => $"when the kernel pool reaches {SetupText.Gb(gb)}",
        CacheChoice.Either => $"every {SetupText.Hours(hours)}, or when the kernel pool reaches {SetupText.Gb(gb)}",
        _ => "off",
    };
}
