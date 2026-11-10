using PoolWatch.Core;
using PoolWatch.Core.Recommendations;

namespace PoolWatch.Ui;

/// <summary>
/// The values on the plain Settings form (automatic cache clearing, deeper capture, the token-leak guard, update notices,
/// the kernel pool notices, and what PoolWatch keeps on disk with when it is cleaned up; Start at logon is a scheduled
/// task, not a setting) and how they map onto <see cref="Settings"/>. Pure, so the mapping and the checks are unit-tested;
/// the full set of settings stays in Advanced.
/// </summary>
/// <param name="AutoClear"><see cref="Settings.AutoClearEnabled"/>.</param>
/// <param name="EveryHours"><see cref="Settings.AutoClearEveryHours"/>: clear every this many hours; 0 = no schedule.</param>
/// <param name="PoolGb"><see cref="Settings.AutoClearPoolGb"/>: clear when the kernel pool is over this many GB; 0 = no size rule.</param>
/// <param name="DeeperCapture"><see cref="Settings.DeeperCapture"/>.</param>
/// <param name="TokenGuard"><see cref="Settings.TokenGuardEnabled"/>: the token-leak guard.</param>
/// <param name="CheckForUpdates"><see cref="Settings.CheckForUpdates"/>: tell me when a new version is out.</param>
/// <param name="PoolNotices"><see cref="Settings.PoolNotices"/>: which kernel pool notices the tray shows (Notifications).</param>
/// <param name="Records"><see cref="Settings.RecordMode"/>: Reports only or Detailed recording (Records).</param>
/// <param name="RetentionDays"><see cref="Settings.RetentionDays"/>: reports, records and exports are cleaned up after this many days.</param>
internal sealed record SimpleSettings(bool AutoClear, decimal EveryHours, decimal PoolGb, bool DeeperCapture, bool TokenGuard, bool CheckForUpdates,
    PoolNoticeLevel PoolNotices, RecordMode Records, int RetentionDays)
{
    /// <summary>The ranges <see cref="Settings.Normalize"/> keeps them in (a week; a terabyte).</summary>
    public const decimal MaxHours = 168, MaxPoolGb = 1024;
    /// <summary>A schedule shorter than this would only be held back: automatic clears are never closer than 30 minutes.</summary>
    public const decimal MinHours = 0.5m;
    /// <summary>The cleanup's range, as <see cref="Settings.Normalize"/> keeps it.</summary>
    public const int MinDays = 1, MaxDays = 90;

    public static SimpleSettings From(Settings s) =>
        new(s.AutoClearEnabled, Field(s.AutoClearEveryHours, MaxHours), Field(s.AutoClearPoolGb, MaxPoolGb), s.DeeperCapture, s.TokenGuardEnabled, s.CheckForUpdates,
            s.PoolNotices, s.RecordMode, s.RetentionDays);

    /// <summary>A setting as the form shows it: one decimal, within the form's range.</summary>
    private static decimal Field(double v, decimal max) => double.IsFinite(v) ? Math.Clamp(Math.Round((decimal)v, 1), 0, max) : 0;

    /// <summary>What is wrong, in plain words; empty when the values can be saved.</summary>
    public List<string> Problems()
    {
        var p = new List<string>();
        if (EveryHours < 0 || EveryHours > MaxHours)
            p.Add($"How often: between 0 and {MaxHours:0} hours (0 = no schedule).");
        else if (EveryHours > 0 && EveryHours < MinHours)
            p.Add("How often: at least every half hour (0.5); automatic clears are never closer than 30 minutes.");
        if (PoolGb < 0 || PoolGb > MaxPoolGb)
            p.Add($"Pool size: between 0 and {MaxPoolGb:0} GB (0 = no size rule).");
        if (AutoClear && EveryHours == 0 && PoolGb == 0)
            p.Add("Automatic clearing is on but has nothing to go by: set how often, a pool size, or both.");
        if (RetentionDays is < MinDays or > MaxDays)
            p.Add($"Clean up: after {MinDays} to {MaxDays} days.");
        return p;
    }

    /// <summary>Writes the values into <paramref name="s"/> when they pass <see cref="Problems"/>; otherwise changes nothing.</summary>
    public bool ApplyTo(Settings s, out List<string> problems)
    {
        problems = Problems();
        if (problems.Count > 0) return false;
        s.AutoClearEnabled = AutoClear;
        s.AutoClearEveryHours = (double)EveryHours;
        s.AutoClearPoolGb = (double)PoolGb;
        s.DeeperCapture = DeeperCapture;
        s.TokenGuardEnabled = TokenGuard;
        s.CheckForUpdates = CheckForUpdates;
        s.PoolNotices = PoolNotices;
        s.RecordMode = Records;
        s.RetentionDays = RetentionDays;
        return true;
    }

    /// <summary>
    /// What PoolWatch keeps on disk: its two choices with one short line each (from the setup step's approved words), the
    /// line shown under the choices for the one chosen, and the cleanup's select, as setup's Records step says them. Each
    /// choice's tooltip says more, in the step's words too.
    /// </summary>
    public const string RecordsHeading = "Records";
    public const string ReportsOnlyChoice = Setup.SetupText.ReportsOnly, DetailedChoice = Setup.SetupText.Detailed;
    public const string ReportsOnlyHint = "A report each time the pool grows.";
    public const string DetailedHint = "The reports, plus a record of everything, all day.";
    public const string CleanUpLabel = Setup.SetupText.CleanUpAfter;
    public const string ReportsOnlyTip =
        "A report each time the pool grows: what grew, the programs behind it,\nand a draft bug report for whoever makes the cause. " +
        "Most people need nothing more.\nNothing is recorded until the pool grows.";
    public const string DetailedTip =
        "The reports, plus a record of everything PoolWatch watches, all day:\nthe pool and its memory tags, programs and their handles, services, " +
        "and every program started.\nUp to about 60 MB a day on a PC doing heavy agentic coding.";
    public const string CleanUpTip = "Reports, records and exports are deleted once nothing in them was written for this long,\ncounted in the PC's awake time.";

    /// <summary>The line under the record modes, for the one chosen.</summary>
    public static string RecordsLine(RecordMode mode) => mode == RecordMode.Detailed ? DetailedHint : ReportsOnlyHint;

    /// <summary>The token-leak guard's box.</summary>
    public const string TokenGuardLabel = "Guard against a kernel pool leak in Windows 11 24H2 and 25H2";
    /// <summary>The lines under the box, always shown: what you will notice, how long it lasts and how to stop it.</summary>
    public const string TokenGuardHint =
        "While it is on, other programs can bring their windows to the front by themselves and take your keystrokes; most people find that rare. " +
        "It lasts until you sign out, and PoolWatch turns it on again each time it starts, until you untick this to end it.";
    /// <summary>
    /// The box's tooltip, one sentence a line (a tooltip wraps only at the screen's edge). True to
    /// <see cref="Core.TokenGuard.GuardPolicy"/>: the workaround goes on unless two tests in a row on the build were clean, and
    /// unticking puts back the setting PoolWatch changed when Windows allows it (<see cref="Engine.TokenLeakGuard.TurnOff"/>).
    /// </summary>
    public const string TokenGuardTip =
        "Windows 11 24H2 and 25H2 leak a little of the kernel pool each time one program starts another.\n" +
        "PoolWatch checks for the leak when it starts, and when you turn this on.\n" +
        "Unless two tests in a row on this build found none, it turns the workaround on.\n" +
        "Windows lets only the program in front turn it on, so PoolWatch may ask you for one click.\n" +
        "Unticking this puts back the setting PoolWatch changed; Save says if Windows did not allow it.";

    /// <summary>The update notices' box and the line under it, as the setup guide says them.</summary>
    public const string UpdatesLabel = "Tell me when a new version is out.";
    public const string UpdatesHint = "Checks GitHub once a day; information only, nothing is downloaded or installed.";

    /// <summary>The kernel pool notices' heading, its three choices and the line under them (<see cref="Settings.PoolNotices"/>).</summary>
    public const string NoticesHeading = "Notifications";
    public const string EveryGrowthChoice = "Every growth", NeedsAttentionChoice = "Only when it needs attention", NoticesOffChoice = "Off";
    public const string NoticesHint = "Notices about the kernel pool. The token-leak guard's need your click and always appear.";
    /// <summary>The choices' tooltips, a line each (a tooltip wraps only at the screen's edge): what each lets through.</summary>
    public const string EveryGrowthTip =
        "A notice by the clock for each growth PoolWatch finds,\nand when the non-paged pool goes over its limit.\nA click on a notice opens PoolWatch.";
    public const string NeedsAttentionTip =
        "Only for what turns the tray icon red:\nthe non-paged pool over its limit and still rising, or a growth whose cause a trace confirmed.";
    public const string NoticesOffTip = "No notice about the kernel pool.\nThe tray icon still shows its colour, and the window shows what grew.";

    /// <summary>What Save says: which settings apply at once, and that the rest wait for the next start.</summary>
    public const string Saved =
        "Saved. Automatic clearing, the token-leak guard, notifications, update notices, records and their cleanup apply now; any other change applies the next time PoolWatch starts.";

    /// <summary>What Save says when PoolWatch was started with --config, whose file it never writes (nor its own settings then).</summary>
    public const string NotSavedWithConfig =
        "Automatic clearing, the token-leak guard, notifications, update notices, records and their cleanup apply now. Nothing is saved for the next time PoolWatch starts: " +
        "it was started with --config, so it keeps no settings.";

    /// <summary>The line at the bottom of the form.</summary>
    public const string WhenChangesApply =
        "Start at logon changes as soon as you tick it. Save puts automatic clearing, the token-leak guard, notifications, update notices, records and their cleanup into effect at once; " +
        "deeper capture and the other settings under Advanced apply the next time PoolWatch starts.";

    /// <summary>The one-line explanation under Deeper capture, honest about whether it does anything with the current settings.</summary>
    public static string DeeperCaptureHint(Settings s) =>
        "Traces PoolWatch records by itself also record file activity, which helps name the process behind the growth (a heavier trace)." +
        // The governor: a pool alert records only with CaptureOnAlert, a lag trip only with CpuTraceEnabled (CaptureGovernor).
        (s.CaptureOnAlert || s.CpuTraceEnabled ? "" : " It has no effect yet: turn on a trace for new incidents under Advanced first.");

    /// <summary>
    /// What a clear is like while it runs: said once wherever a button starts clearing (here, the setup guide's step and
    /// the Simple view's card; <see cref="RunsAClear"/>).
    /// </summary>
    public const string ClearStutter = "Clearing takes a few seconds, and video or the mouse may stutter while it runs.";

    /// <summary>
    /// A suggestion whose button clears the cache: the hourly clear (turned on, or the one-time check of whether it helps)
    /// or replacing a clear task of your own with it. The guide and the Simple view offer one or the other, so what a
    /// clear is like is said once on each.
    /// </summary>
    public static bool RunsAClear(Recommendation r) =>
        r.State == RecommendationState.Offer && r.Id is RecommendationId.HourlyCacheClear or RecommendationId.ReplaceClearTask;

    /// <summary>
    /// The line under automatic clearing: first what a clear is like while it runs, then the spacing rule from
    /// <see cref="Settings.AutoClearMinMinutes"/>, and what turning it on does to a clear task of your own. The three rules
    /// are named by the choices above it (<see cref="ClearRule"/>).
    /// </summary>
    public static string AutoClearHint(Settings s) =>
        ClearStutter + $" Never more than once every {Math.Max(30, s.AutoClearMinMinutes):0} minutes. " +
        "Turning it on first offers to remove a clear task of your own (RAMMap), so the two never both run.";

    /// <summary>The choices' words, as the setup guide names them.</summary>
    public const string ScheduleChoice = "On a schedule", SizeChoice = "At a pool size", EitherChoice = "Whichever comes first";

    /// <summary>
    /// Which rule two values make: a schedule alone (no size rule, 0 GB), a pool size alone (no schedule, 0 hours), or
    /// whichever comes first (both). Neither set reads as whichever comes first: the form then shows its two fields to fill.
    /// </summary>
    public static ClearRule RuleOf(decimal everyHours, decimal poolGb) =>
        everyHours > 0 && poolGb <= 0 ? ClearRule.Schedule : poolGb > 0 && everyHours <= 0 ? ClearRule.PoolSize : ClearRule.Either;

    /// <summary>The values a rule keeps from the two fields: the one it does not use is 0, so it is off.</summary>
    public static (decimal EveryHours, decimal PoolGb) ForRule(ClearRule rule, decimal everyHours, decimal poolGb) => rule switch
    {
        ClearRule.Schedule => (everyHours, 0),
        ClearRule.PoolSize => (0, poolGb),
        _ => (everyHours, poolGb),
    };
}

/// <summary>When the automatic clear runs: on a schedule, at a pool size, or whichever comes first.</summary>
internal enum ClearRule { Schedule, PoolSize, Either }
