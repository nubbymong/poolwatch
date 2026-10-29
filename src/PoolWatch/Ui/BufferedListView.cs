namespace PoolWatch.Ui;

/// <summary>
/// A list view drawn off screen (LVS_EX_DOUBLEBUFFER), for a list whose cells change while it shows (Pool causes,
/// Services): without it a changed cell is erased on screen before it is drawn again.
/// </summary>
internal sealed class BufferedListView : ListView
{
    public BufferedListView() => DoubleBuffered = true;
}
