using PoolWatch.Core;

namespace PoolWatch.Ui;

/// <summary>
/// The values on the plain Settings form (automatic cache clearing, deeper capture and the token-leak guard; Start at
/// logon is a scheduled task, not a setting) and how they map onto <see cref="Settings"/>. Pure, so the mapping and the
/// checks are unit-tested; the full set of settings stays in Advanced.
/// </summary>
/// <param name="AutoClear"><see cref="Settings.AutoClearEnabled"/>.</param>
/// <param name="EveryHours"><see cref="Settings.AutoClearEveryHours"/>: clear every this many hours; 0 = no schedule.</param>
/// <param name="PoolGb"><see cref="Settings.AutoClearPoolGb"/>: clear when the kernel pool is over this many GB; 0 = no size rule.</param>
/// <param name="DeeperCapture"><see cref="Settings.DeeperCapture"/>.</param>
/// <param name="TokenGuard"><see cref="Settings.TokenGuardEnabled"/>: the token-leak guard, started with the tray app, so a change applies at its next start.</param>
internal sealed record SimpleSettings(bool AutoClear, decimal EveryHours, decimal PoolGb, bool DeeperCapture, bool TokenGuard)
{
    /// <summary>The ranges <see cref="Settings.Normalize"/> keeps them in (a week; a terabyte).</summary>
    public const decimal MaxHours = 168, MaxPoolGb = 1024;
    /// <summary>A schedule shorter than this would only be held back: automatic clears are never closer than 30 minutes.</summary>
    public const decimal MinHours = 0.5m;

    public static SimpleSettings From(Settings s) =>
        new(s.AutoClearEnabled, Field(s.AutoClearEveryHours, MaxHours), Field(s.AutoClearPoolGb, MaxPoolGb), s.DeeperCapture, s.TokenGuardEnabled);

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
        return true;
    }

    /// <summary>The token leak guard's box.</summary>
    public const string TokenGuardLabel = "Guard against a memory leak in some Windows 11 Insider builds";
    /// <summary>The line under the box, always shown: the one trade-off, how long it lasts, and when a change applies.</summary>
    public const string TokenGuardHint =
        "To stop the leak, it lets any program bring its window to the front until you sign out. A change here applies the next time PoolWatch starts.";
    /// <summary>
    /// The box's tooltip, one sentence a line (a tooltip wraps only at the screen's edge). True to
    /// <see cref="Core.TokenGuard.GuardPolicy"/>: the workaround goes on unless two tests in a row on the build were clean, and
    /// nothing sets it back before sign-out.
    /// </summary>
    public const string TokenGuardTip =
        "Some Windows 11 Insider builds leak a little memory each time one program starts another.\n" +
        "When PoolWatch starts, it tests for the leak.\n" +
        "Unless two tests in a row on this build found none, it turns the workaround on.\n" +
        "Windows lets only the program in front turn it on, so PoolWatch may ask you for one click.\n" +
        "Turning this off does not undo a workaround already on: that lasts until you sign out.";

    /// <summary>The one-line explanation under Deeper capture, honest about whether it does anything with the current settings.</summary>
    public static string DeeperCaptureHint(Settings s) =>
        "Traces PoolWatch records by itself also record file activity, which helps name the process behind the growth (a heavier trace)." +
        // The governor: a pool alert records only with CaptureOnAlert, a lag trip only with CpuTraceEnabled (CaptureGovernor).
        (s.CaptureOnAlert || s.CpuTraceEnabled ? "" : " PoolWatch records traces by itself only with CaptureOnAlert on (Advanced), so this has no effect yet.");

    /// <summary>The line under automatic clearing: the spacing rule from <see cref="Settings.AutoClearMinMinutes"/>.</summary>
    public static string AutoClearHint(Settings s) =>
        $"Never more than once every {Math.Max(30, s.AutoClearMinMinutes):0} minutes. Turning it on first offers to remove a clear task of your own (RAMMap), so the two never both run.";
}
