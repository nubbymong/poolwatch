namespace PoolWatch.Ui;

/// <summary>
/// Updates a details-view ListView in place: rows are matched by key, existing ListViewItem objects are kept and only
/// changed cells are written, new rows are added and gone rows removed. Selection and scroll position survive a
/// refresh. While a row is selected or the mouse is over the list the order is held (new rows go to the end) and
/// <see cref="OrderHeld"/> says so. Nothing is cleared and rebuilt on a tick (that was the flashing).
/// </summary>
internal sealed class ListSync
{
    private readonly ListView _list;
    private readonly Dictionary<object, ListViewItem> _items = new();
    private bool _mouseOver;

    public ListSync(ListView list)
    {
        _list = list;
        list.MouseEnter += (_, _) => _mouseOver = true;
        list.MouseLeave += (_, _) => _mouseOver = false;
        list.VisibleChanged += (_, _) => { if (!list.Visible) _mouseOver = false; }; // a tab switched by keyboard leaves no MouseLeave
        typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(list, true);
    }

    /// <summary>True when the last update kept the old order although the data's order changed.</summary>
    public bool OrderHeld { get; private set; }

    public bool Updating { get; private set; }

    public sealed record Row(object Key, string[] Cells, Color? Back = null, Font? Font = null, Color? Fore = null);

    /// <param name="forceOrder">The user asked for this order (a column sort): apply it even with a selection.</param>
    public void Update(IReadOnlyList<Row> rows, bool forceOrder = false)
    {
        Updating = true;
        // C5 item 3: remember where the user is (the top visible row and the rows below it, the selection and the focused
        // row) so that removals, insertions above and moves cannot scroll the list or drop the selection.
        var (topIndex, fromTop) = VisibleKeys();
        List<object> selected;
        object? focused;
        try
        {
            selected = _list.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag).OfType<object>().ToList();
            focused = _list.FocusedItem?.Tag;
        }
        catch (Exception) { selected = new(); focused = null; }
        _list.BeginUpdate();
        try
        {
            var wanted = new HashSet<object>(rows.Select(r => r.Key));
            foreach (var key in _items.Keys.Where(k => !wanted.Contains(k)).ToList())
            {
                _list.Items.Remove(_items[key]);
                _items.Remove(key);
            }
            bool hold = !forceOrder && (_list.SelectedItems.Count > 0 || _mouseOver);
            OrderHeld = false;
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (!_items.TryGetValue(r.Key, out var item))
                {
                    item = new ListViewItem(r.Cells) { Tag = r.Key };
                    _items[r.Key] = item;
                    if (hold || i >= _list.Items.Count) _list.Items.Add(item);
                    else _list.Items.Insert(i, item);
                }
                else
                {
                    for (int c = 0; c < r.Cells.Length; c++)
                    {
                        if (c < item.SubItems.Count) { if (item.SubItems[c].Text != r.Cells[c]) item.SubItems[c].Text = r.Cells[c]; }
                        else item.SubItems.Add(r.Cells[c]);
                    }
                    if (item.Index != i && i < _list.Items.Count)
                    {
                        if (hold) OrderHeld = true;
                        else
                        {
                            _list.Items.Remove(item);
                            _list.Items.Insert(Math.Min(i, _list.Items.Count), item);
                        }
                    }
                }
                var back = r.Back ?? _list.BackColor;
                if (item.BackColor != back) item.BackColor = back;
                var fore = r.Fore ?? _list.ForeColor;
                if (item.ForeColor != fore) item.ForeColor = fore;
                if (r.Font is { } f && item.Font != f) item.Font = f;
            }
        }
        finally
        {
            try { RestoreSelection(selected, focused); } catch (Exception) { /* best effort: never fail a refresh */ }
            _list.EndUpdate();
            try { RestoreTop(topIndex, fromTop); } catch (Exception) { }
            Updating = false;
        }
    }

    public ListViewItem? ItemFor(object key) => _items.GetValueOrDefault(key);

    /// <summary>The top visible row's index and the keys from it downwards (a page and a half is plenty to find a survivor).</summary>
    private (int topIndex, List<object> fromTop) VisibleKeys()
    {
        var keys = new List<object>();
        try
        {
            if (!_list.IsHandleCreated || _list.Items.Count == 0) return (0, keys);
            var top = _list.TopItem;
            if (top is null) return (0, keys);
            for (int i = top.Index; i < _list.Items.Count && keys.Count < 64; i++)
                if (_list.Items[i].Tag is { } k) keys.Add(k);
            return (top.Index, keys);
        }
        catch (Exception) { return (0, new List<object>()); } // best effort: the update itself must still run
    }

    /// <summary>Scrolls the remembered row back to the top (a list at the very top stays there).</summary>
    private void RestoreTop(int topIndex, List<object> fromTop)
    {
        if (!_list.IsHandleCreated) return;
        if (PoolWatch.Core.Util.ScrollAnchor.Pick(topIndex, fromTop, k => _items.ContainsKey(k)) is not { } key) return;
        if (_items.TryGetValue(key, out var item) && item.ListView == _list && !ReferenceEquals(_list.TopItem, item))
            _list.TopItem = item;
    }

    /// <summary>Rows that were selected and are still listed stay selected (a row moved by a forced sort is re-inserted).</summary>
    private void RestoreSelection(List<object> selected, object? focused)
    {
        foreach (var k in selected)
            if (_items.TryGetValue(k, out var item) && item.ListView == _list && !item.Selected) item.Selected = true;
        if (focused is not null && _items.TryGetValue(focused, out var f) && f.ListView == _list && !f.Focused) f.Focused = true;
    }
}
