namespace PoolWatch.Ui;

/// <summary>
/// The main window's switch between the Simple and the Advanced view, and the Advanced view's tabs, in the order that
/// reads each view and tab once (<see cref="MainForm"/> gives it its steps). A switch of view arranges the view inside
/// one layout of the window, then reads it once. Opening the Advanced view on a tab from the Simple view picks the tab
/// inside that switch, before the view is arranged, so the one read is of that tab, and the tab's own change, raised by
/// that pick, does nothing. A tab picked in the Advanced view (a click on it, or a suggestion) is laid out and read by
/// the tab's own change. Picked after the switch, the tab shown before was read first, every time, then the new one.
/// </summary>
internal sealed class ViewSwitch
{
    private readonly TabControl _tabs;
    private readonly Action<Action> _layOutOnce;
    private readonly Action<bool> _arrange;
    private readonly Action _read;
    /// <summary>A tab is being picked inside a switch of view: the tab's own change stays out of it.</summary>
    private bool _picking;

    /// <param name="tabs">The Advanced view's tabs.</param>
    /// <param name="layOutOnce">Runs a change of view in one layout of the window (<see cref="MainForm.LayOutOnce"/>).</param>
    /// <param name="arrange">Arranges the view inside that layout (true: the Advanced view, on the tab picked now).</param>
    /// <param name="read">Reads the view shown, once, after it is laid out.</param>
    /// <param name="tabChanged">A tab picked in the Advanced view, outside a switch of view: lays it out and reads it.</param>
    public ViewSwitch(TabControl tabs, Action<Action> layOutOnce, Action<bool> arrange, Action read, Action tabChanged)
    {
        (_tabs, _layOutOnce, _arrange, _read) = (tabs, layOutOnce, arrange, read);
        tabs.SelectedIndexChanged += (_, _) =>
        {
            if (_picking) return; // picked inside a switch of view, which arranges the view and reads it once
            tabChanged();
        };
    }

    /// <summary>The Advanced view shows (false: the Simple view). Set first in a switch, so the view's own steps see it.</summary>
    public bool Advanced { get; private set; }

    /// <summary>
    /// Shows the Advanced or the Simple view, arranged in one layout of the window, then reads it once.
    /// <paramref name="tab"/>: the Advanced view opens on that tab, picked before the view is arranged.
    /// </summary>
    public void Show(bool advanced, int? tab = null)
    {
        _layOutOnce(() =>
        {
            Advanced = advanced;
            if (advanced && tab is { } t && _tabs.SelectedIndex != t)
            {
                _picking = true;
                try { _tabs.SelectedIndex = t; }
                finally { _picking = false; }
            }
            _arrange(advanced);
        });
        _read();
    }

    /// <summary>
    /// The Advanced view on <paramref name="tab"/> (0 Pool causes, 1 Services, 2 Tasks and startup apps; kept within the
    /// tabs): from the Simple view the tab is picked inside the switch, which reads only that tab; in the Advanced view the
    /// tab's own change lays it out and reads it, as a click on the tab does.
    /// </summary>
    public void ShowAdvanced(int tab)
    {
        tab = Math.Clamp(tab, 0, _tabs.TabCount - 1);
        if (!Advanced) Show(true, tab);
        else _tabs.SelectedIndex = tab;
    }
}
