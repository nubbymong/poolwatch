using System.Text.Json;
using PoolWatch.Analysis;
using PoolWatch.Core;
using PoolWatch.Engine;

namespace PoolWatch.Ui;

/// <summary>
/// The settings being edited, shared by the plain Settings form and the Advanced grid, and the one Save both use. Save
/// writes settings.json (<see cref="LiveSettings.SaveForNextStart"/>: never with --config, and with the service profiles
/// as they are then) and puts the automatic clear, the token-leak guard and update notices into effect at once
/// (<see cref="LiveSettings"/>, so the next-clear line and Active changes are right straight away); every other setting
/// applies at PoolWatch's next start. Turning the automatic clear on first deals with any clear task of your own;
/// turning the guard on starts its sign-in check. A change made elsewhere while Settings is open (a suggestion, the setup
/// guide, Undo) is followed (<see cref="Follow"/>), and Save puts into effect only the parts changed here, so an older
/// copy never undoes such a change. Lives as long as one opening of Settings, and starts from what is saved
/// (<see cref="StartFrom"/>), so a change saved for the next start shows again and is never written back over.
/// </summary>
internal sealed class SettingsEditor
{
    private readonly AnalysisPipeline _analysis;
    private readonly LiveSettings _live;
    private readonly IShell _shell;
    /// <summary>The settings as this opening of Settings last showed them: <see cref="Edit"/> without the edits made here.</summary>
    private readonly Settings _shown;

    public SettingsEditor(AnalysisPipeline analysis, UiActions actions, LiveSettings live, IShell shell)
    {
        _analysis = analysis;
        Actions = actions;
        _live = live;
        _shell = shell;
        Edit = StartingCopy(live);
        _shown = Clone(Edit);
    }

    /// <summary>The edited copy.</summary>
    public Settings Edit { get; }

    public UiActions Actions { get; }

    private static Settings Clone(Settings s) => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(s))!;

    /// <summary>
    /// What an opening of Settings starts from (<see cref="StartFrom"/>): settings.json as saved, else the running copy
    /// (with --config, which is never written, or a file that cannot be read).
    /// </summary>
    internal static Settings StartingCopy(LiveSettings live) => StartFrom(live.Saved() ?? Clone(live.Running), live.Running);

    /// <summary>
    /// What an opening of Settings edits: the <paramref name="saved"/> settings (deeper capture and the Advanced settings
    /// apply only at the next start, so they may differ from what runs), with the parts in effect at once as
    /// <paramref name="running"/> has them.
    /// </summary>
    internal static Settings StartFrom(Settings saved, Settings running)
    {
        AtOnceSettings.Of(running).ApplyTo(saved);
        saved.Normalize();
        return saved;
    }

    /// <summary>A change put into effect elsewhere while Settings is open (UI thread): the edited copy and what Settings showed follow it.</summary>
    public void Follow(Action<Settings> change)
    {
        change(Edit);
        change(_shown);
        Edit.Normalize();
        _shown.Normalize();
    }

    public void Save(IWin32Window owner)
    {
        Edit.Normalize();
        var running = AtOnceSettings.Of(_live.Running);
        var apply = AtOnceSettings.ToApply(AtOnceSettings.Of(_shown), AtOnceSettings.Of(Edit), running);
        string? clearStaysOff = apply.TurnsClearOn(running) ? ClearTaskStays(owner) : null;
        if (clearStaysOff is not null) apply = apply with { AutoClear = apply.AutoClear with { Enabled = false } };
        apply.ApplyTo(Edit);
        bool kept = _live.SaveForNextStart(Edit); // every setting, for the next start (nothing with --config)
        _live.Apply("Settings", apply.ApplyTo);   // and these at once
        apply.ApplyTo(_shown);
        if (apply.TurnsGuardOn(running)) _ = _shell.StartTokenGuard(); // its notices say how the check went
        else if (apply.TurnsGuardOff(running)) _analysis.Diagnostics.Guard.WorkaroundWanted = false;
        _analysis.Diagnostics.Cache.RefreshStatus();
        string said = kept ? SimpleSettings.Saved : SimpleSettings.NotSavedWithConfig;
        MessageBox.Show(owner, clearStaysOff is null ? said : said + "\n\n" + clearStaysOff, "PoolWatch");
    }

    /// <summary>
    /// Before the automatic clear goes on: finds any clear task of your own (RAMMap in a scheduled task), shows it, and
    /// removes it only on a yes (recorded first, so Active changes can put it back). Null when none is left; else why the
    /// automatic clear stays off: your No, the reason PoolWatch kept the task, or no administrator rights.
    /// </summary>
    private string? ClearTaskStays(IWin32Window owner)
    {
        if (!CacheHelper.IsElevated) return UiActions.NotElevated;
        return Actions.LegacyInProcess(owner, "Settings", out string? note) == CacheHelper.LegacyOutcome.Kept
            ? note ?? "Your own clear task stays, so PoolWatch's automatic clear stays off (the two should not both run)."
            : null;
    }
}

/// <summary>The automatic clear's settings, which take effect together.</summary>
internal sealed record AutoClearSettings(bool Enabled, double EveryHours, double PoolGb, double MinMinutes, string Mode)
{
    public static AutoClearSettings Of(Settings s) => new(s.AutoClearEnabled, s.AutoClearEveryHours, s.AutoClearPoolGb, s.AutoClearMinMinutes, s.AutoClearMode);

    public void ApplyTo(Settings s)
    {
        s.AutoClearEnabled = Enabled;
        s.AutoClearEveryHours = EveryHours;
        s.AutoClearPoolGb = PoolGb;
        s.AutoClearMinMinutes = MinMinutes;
        s.AutoClearMode = Mode;
    }
}

/// <summary>
/// The settings Save puts into effect at once, in the parts the owner changes together: the automatic clear (all its
/// values), the token-leak guard and update notices. Every other setting applies at the next start.
/// </summary>
internal sealed record AtOnceSettings(AutoClearSettings AutoClear, bool TokenGuard, bool CheckForUpdates)
{
    public static AtOnceSettings Of(Settings s) => new(AutoClearSettings.Of(s), s.TokenGuardEnabled, s.CheckForUpdates);

    public void ApplyTo(Settings s)
    {
        AutoClear.ApplyTo(s);
        s.TokenGuardEnabled = TokenGuard;
        s.CheckForUpdates = CheckForUpdates;
    }

    /// <summary>The automatic clear goes from off to on: a clear task of your own is dealt with first.</summary>
    public bool TurnsClearOn(AtOnceSettings running) => AutoClear.Enabled && !running.AutoClear.Enabled;

    /// <summary>The guard goes from off to on: its sign-in check starts, once (each start after a finished check runs a new one).</summary>
    public bool TurnsGuardOn(AtOnceSettings running) => TokenGuard && !running.TokenGuard;

    /// <summary>The guard goes from on to off: it stops putting Windows' setting back, as Undo does.</summary>
    public bool TurnsGuardOff(AtOnceSettings running) => !TokenGuard && running.TokenGuard;

    /// <summary>
    /// What Save puts into effect: each part Settings changed (<paramref name="edited"/> differs from what it showed,
    /// <paramref name="shown"/>), and for each part it left alone what is in effect now (<paramref name="running"/>), which
    /// a suggestion, the setup guide or Undo may have changed while Settings was open.
    /// </summary>
    public static AtOnceSettings ToApply(AtOnceSettings shown, AtOnceSettings edited, AtOnceSettings running) => new(
        edited.AutoClear != shown.AutoClear ? edited.AutoClear : running.AutoClear,
        edited.TokenGuard != shown.TokenGuard ? edited.TokenGuard : running.TokenGuard,
        edited.CheckForUpdates != shown.CheckForUpdates ? edited.CheckForUpdates : running.CheckForUpdates);
}
