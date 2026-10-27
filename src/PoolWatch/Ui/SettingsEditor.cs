using PoolWatch.Analysis;
using PoolWatch.Core;
using PoolWatch.Engine;

namespace PoolWatch.Ui;

/// <summary>
/// The settings being edited, shared by the plain Settings form and the Advanced grid, and the one Save both use (the
/// behaviour of the old Settings tab): settings.json is written now and the running PoolWatch takes it at its next start;
/// turning the automatic clear on first deals with any clear task of your own. It lives as long as the window, as the
/// tab's state did.
/// </summary>
internal sealed class SettingsEditor
{
    private readonly MonitorEngine _engine;
    private readonly AnalysisPipeline _analysis;
    private readonly UiActions _actions;
    private bool _autoClearConfirmed;

    public SettingsEditor(MonitorEngine engine, AnalysisPipeline analysis, UiActions actions)
    {
        _engine = engine;
        _analysis = analysis;
        _actions = actions;
        Edit = Clone(engine.Settings);
    }

    /// <summary>The edited copy (the running engine keeps its own until restart).</summary>
    public Settings Edit { get; }

    public UiActions Actions => _actions;

    private static Settings Clone(Settings s) => System.Text.Json.JsonSerializer.Deserialize<Settings>(System.Text.Json.JsonSerializer.Serialize(s))!;

    public void Save(IWin32Window owner)
    {
        Edit.Normalize();
        if (!Edit.AutoClearEnabled) _autoClearConfirmed = false;
        bool turningOnAutoClear = Edit.AutoClearEnabled && !_engine.Settings.AutoClearEnabled && !_autoClearConfirmed;
        if (turningOnAutoClear)
        {
            // The automatic clear goes on only after the elevated step has dealt with any clear task of your own.
            Edit.AutoClearEnabled = false;
            Write(owner, null);
            ConfirmAutoClear(owner);
            return;
        }
        Write(owner, "Saved. Changes take effect after PoolWatch restarts.");
    }

    private void Write(IWin32Window owner, string? message)
    {
        Edit.Save(_engine.Paths.Guard.CheckReal(_engine.Paths.SettingsFile));
        if (message is not null) MessageBox.Show(owner, message, "PoolWatch");
    }

    /// <summary>
    /// Turning the automatic clear on: finds any clear task of your own (RAMMap in a scheduled task), shows it, and removes
    /// it only on a yes; kept, the automatic clear stays off. In-process (S5, review round 2: no UAC step any more;
    /// PoolWatch is elevated).
    /// </summary>
    private void ConfirmAutoClear(IWin32Window owner)
    {
        if (!CacheHelper.IsElevated) { MessageBox.Show(owner, UiActions.NotElevated, "PoolWatch"); return; }
        if (_actions.LegacyInProcess(owner) == 0)
        {
            _autoClearConfirmed = true;
            Edit.AutoClearEnabled = true;
            Write(owner, "The automatic clear is on (from the next start of PoolWatch).");
        }
        else Write(owner, "Your own clear task stays, so PoolWatch's automatic clear stays off (the two should not both run).");
        _analysis.Diagnostics.Cache.RefreshStatus();
    }
}
