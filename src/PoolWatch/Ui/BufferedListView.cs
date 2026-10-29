namespace PoolWatch.Ui;

/// <summary>
/// A list view drawn off screen (LVS_EX_DOUBLEBUFFER), for a list whose cells change while it shows: without it a changed
/// cell is erased on screen before it is drawn again. A list kept in step by <see cref="ListSync"/> (Pool causes) gets the
/// same style from it.
/// </summary>
internal sealed class BufferedListView : ListView
{
    public BufferedListView() => DoubleBuffered = true;
}
