namespace PoolWatch.Ui;

/// <summary>
/// The CI UI soak's walk through the main window (<see cref="MainForm.StartSoakCycle"/>): a minute on each view in turn, as
/// a user would look, the Simple view and then each of the Advanced view's tabs, every refresh profiled under
/// <see cref="MainForm.RefreshStage"/> (ui.simple, ui.page0, ui.page1, ...). ci/soak.ps1 expects exactly
/// <see cref="Stages"/>. The walk is stopped before the end-of-run screenshots: the run is a whole number of minutes, so
/// its last step falls due with them, and run from inside their Application.DoEvents it put the window back on the
/// Simple view under every shot after the first.
/// </summary>
internal sealed class SoakWalk(int tabs)
{
    private int _step;

    /// <summary>The walk changes nothing more.</summary>
    public bool Stopped { get; private set; }

    /// <summary>The view the next minute shows: -1 for the Simple view, else the Advanced view's tab; null once stopped.</summary>
    public int? Next() => Stopped ? null : _step++ % (tabs + 1) - 1;

    public void Stop() => Stopped = true;

    /// <summary>The profiled stages one round of the walk refreshes, in its order.</summary>
    public static IReadOnlyList<string> Stages(int tabs)
    {
        var walk = new SoakWalk(tabs);
        var stages = new List<string>();
        for (int i = 0; i <= tabs; i++)
        {
            int page = walk.Next()!.Value;
            stages.Add(MainForm.RefreshStage(advanced: page >= 0, Math.Max(page, 0)));
        }
        return stages;
    }
}
