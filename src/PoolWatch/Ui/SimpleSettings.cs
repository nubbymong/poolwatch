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
/// <param name="TokenGuard"><see cref="Settings.TokenGuardEnabled"/>: the token-leak guard, started with the tray app (at sign-in).</param>
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

    /// <summary>The token-leak guard's checkbox, its two-line explanation, and its tooltip with the whole story.</summary>
    public const string TokenGuardLabel = "Guard against the kernel token leak of some Windows 11 Insider builds";
    public const string TokenGuardHint =
        "Tested at sign-in; if found, the public fix lasts this session only, never the registry. It may ask for one click to come to the front: only the front program may apply it.";
    public const string TokenGuardTip =
        "On some Windows 11 Insider builds, Windows leaks a kernel token reference each time a program starts a child. With this on, " +
        "PoolWatch tests for the leak when it starts (at sign-in) and, if the build leaks, applies the public workaround for this " +
        "session only: the live foreground lock timeout is set to 0, never in the registry. Windows lets only the program in front " +
        "change that setting, so PoolWatch may ask for one click to bring itself to the front. Off by default; takes effect from " +
        "PoolWatch's next start.";

    /// <summary>The one-line explanation under Deeper capture, honest about whether it does anything with the current settings.</summary>
    public static string DeeperCaptureHint(Settings s) =>
        "Traces PoolWatch records by itself also record file activity, which helps name the process behind the growth (a heavier trace)." +
        // The governor: a pool alert records only with CaptureOnAlert, a lag trip only with CpuTraceEnabled (CaptureGovernor).
        (s.CaptureOnAlert || s.CpuTraceEnabled ? "" : " PoolWatch records traces by itself only with CaptureOnAlert on (Advanced), so this has no effect yet.");

    /// <summary>The line under automatic clearing: the spacing rule from <see cref="Settings.AutoClearMinMinutes"/>.</summary>
    public static string AutoClearHint(Settings s) =>
        $"Never more than once every {Math.Max(30, s.AutoClearMinMinutes):0} minutes. Turning it on first offers to remove a clear task of your own (RAMMap), so the two never both run.";
}
