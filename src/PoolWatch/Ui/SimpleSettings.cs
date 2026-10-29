using PoolWatch.Core;
using PoolWatch.Core.Recommendations;

namespace PoolWatch.Ui;

/// <summary>
/// The values on the plain Settings form (automatic cache clearing, deeper capture, the token-leak guard and update
/// notices; Start at logon is a scheduled task, not a setting) and how they map onto <see cref="Settings"/>. Pure, so the
/// mapping and the checks are unit-tested; the full set of settings stays in Advanced.
/// </summary>
/// <param name="AutoClear"><see cref="Settings.AutoClearEnabled"/>.</param>
/// <param name="EveryHours"><see cref="Settings.AutoClearEveryHours"/>: clear every this many hours; 0 = no schedule.</param>
/// <param name="PoolGb"><see cref="Settings.AutoClearPoolGb"/>: clear when the kernel pool is over this many GB; 0 = no size rule.</param>
/// <param name="DeeperCapture"><see cref="Settings.DeeperCapture"/>.</param>
/// <param name="TokenGuard"><see cref="Settings.TokenGuardEnabled"/>: the token-leak guard.</param>
/// <param name="CheckForUpdates"><see cref="Settings.CheckForUpdates"/>: tell me when a new version is out.</param>
internal sealed record SimpleSettings(bool AutoClear, decimal EveryHours, decimal PoolGb, bool DeeperCapture, bool TokenGuard, bool CheckForUpdates)
{
    /// <summary>The ranges <see cref="Settings.Normalize"/> keeps them in (a week; a terabyte).</summary>
    public const decimal MaxHours = 168, MaxPoolGb = 1024;
    /// <summary>A schedule shorter than this would only be held back: automatic clears are never closer than 30 minutes.</summary>
    public const decimal MinHours = 0.5m;

    public static SimpleSettings From(Settings s) =>
        new(s.AutoClearEnabled, Field(s.AutoClearEveryHours, MaxHours), Field(s.AutoClearPoolGb, MaxPoolGb), s.DeeperCapture, s.TokenGuardEnabled, s.CheckForUpdates);

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
        return true;
    }

    /// <summary>The token-leak guard's box.</summary>
    public const string TokenGuardLabel = "Guard against a kernel pool leak in Windows 11 24H2 and 25H2";
    /// <summary>The lines under the box, always shown: what you will notice, how long it lasts and how to stop it.</summary>
    public const string TokenGuardHint =
        "While it is on, other programs can bring their windows to the front by themselves and take your keystrokes; most people find that rare. " +
        "It lasts until you sign out, and PoolWatch turns it on again each time it starts, until you untick this.";
    /// <summary>
    /// The box's tooltip, one sentence a line (a tooltip wraps only at the screen's edge). True to
    /// <see cref="Core.TokenGuard.GuardPolicy"/>: the workaround goes on unless two tests in a row on the build were clean, and
    /// nothing sets it back before sign-out.
    /// </summary>
    public const string TokenGuardTip =
        "Windows 11 24H2 and 25H2 leak a little of the kernel pool each time one program starts another.\n" +
        "PoolWatch checks for the leak when it starts, and when you turn this on.\n" +
        "Unless two tests in a row on this build found none, it turns the workaround on.\n" +
        "Windows lets only the program in front turn it on, so PoolWatch may ask you for one click.\n" +
        "Unticking this does not undo a workaround already on: that lasts until you sign out.";

    /// <summary>The update notices' box and the line under it, as the setup guide says them.</summary>
    public const string UpdatesLabel = "Tell me when a new version is out.";
    public const string UpdatesHint = "Checks GitHub once a day; information only, nothing is downloaded or installed.";

    /// <summary>What Save says: which settings apply at once, and that the rest wait for the next start.</summary>
    public const string Saved =
        "Saved. Automatic clearing, the token-leak guard and update notices apply now; any other change applies the next time PoolWatch starts.";

    /// <summary>The line at the bottom of the form.</summary>
    public const string WhenChangesApply =
        "Start at logon changes as soon as you tick it. Save puts automatic clearing, the token-leak guard and update notices into effect at once; " +
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
    /// <see cref="Settings.AutoClearMinMinutes"/>, that each rule works on its own, and what turning it on does to a clear
    /// task of your own.
    /// </summary>
    public static string AutoClearHint(Settings s) =>
        ClearStutter + $" Never more than once every {Math.Max(30, s.AutoClearMinMinutes):0} minutes. Each rule works on its own: 0 hours clears only by size, " +
        "0 GB only on the schedule. Turning it on first offers to remove a clear task of your own (RAMMap), so the two never both run.";
}
