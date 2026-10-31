using System.ComponentModel;
using System.Windows.Forms.VisualStyles;
using PoolWatch.Core.Impact;
using PoolWatch.Core.Services;

namespace PoolWatch.Ui;

/// <summary>
/// The Services list: every service the filter shows, with its tick, in two groups (Windows services, then the
/// services of other apps; the protected ones in a third when they are shown), each under a header row, sortable by any
/// column. A ListView cannot put a tick box on a group header, so the header rows are drawn here (owner draw), and so is
/// each Cost cell's meter; every other cell is drawn by Windows. Which services are ticked is kept here
/// (<see cref="Ticked"/>), so a folded group, a filter, a new sort or a refresh never loses a tick. Keyboard and screen
/// readers: a header row is an item whose text says its name, its note, how many are ticked and whether it is folded;
/// Space ticks or unticks every selected row (a selected header stands for its group's shown services), Left / Right
/// fold and unfold a header, Enter toggles it, Ctrl+A selects all; the right-click menu has Tick selected / Untick
/// selected. Drawn off screen (its Cost cells change while it shows).
/// </summary>
internal sealed class ServiceListView : ListView
{
    private readonly HashSet<string> _ticked = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private IReadOnlyList<ServiceRow> _rows = [];
    /// <summary>Each group's services as the filter shows them now: what its header's tick and Space act on.</summary>
    private Dictionary<string, List<ServiceRow>> _members = new();
    private ServiceFilter _filter = ServiceFilter.Default;
    /// <summary>The list itself is setting ticks (a rebuild, a group tick): not the owner's click.</summary>
    private bool _syncing;
    private Font? _bold;
    private int _hot = -1;
    private readonly ToolStripMenuItem _tickItem = new("Tick selected"), _untickItem = new("Untick selected");

    public static readonly Color GroupText = UiColors.Ink, Chevron = Color.FromArgb(0x4a, 0x4a, 0x4a);

    public ServiceListView()
    {
        DoubleBuffered = true;
        OwnerDraw = true;
        View = View.Details;
        FullRowSelect = true;
        CheckBoxes = true;
        MultiSelect = true;
        HideSelection = false;
        ShowItemToolTips = true;
        AccessibleName = "Services";
        _tickItem.Click += (_, _) => TickSelected(true);
        _untickItem.Click += (_, _) => TickSelected(false);
        var menu = new ContextMenuStrip();
        menu.Items.AddRange([_tickItem, _untickItem]);
        menu.Opening += (_, _) => _tickItem.Enabled = _untickItem.Enabled = SelectionTargets().Count > 0;
        ContextMenuStrip = menu;
    }

    /// <summary>Raised when the owner changed which services are ticked (a click, Space, the menu, a group tick, <see cref="SetTicks"/>).</summary>
    public event EventHandler? TicksChanged;

    /// <summary>Raised after the list shows its rows again (new rows, a filter, ticks set, a fold, a sort): <see cref="ShownCount"/> may have changed.</summary>
    public event EventHandler? ListRebuilt;

    public int SortColumn { get; private set; }
    public bool SortAscending { get; private set; } = true;

    /// <summary>The rows as last given (the filter's hidden ones too).</summary>
    public IReadOnlyList<ServiceRow> Rows => _rows;

    /// <summary>How many services the filter shows now (folded groups' too).</summary>
    public int ShownCount => _members.Values.Sum(m => m.Count);

    /// <summary>The ticked services that may be turned off, folded groups' ones too, in the rows' order.</summary>
    public List<string> Ticked => _rows.Where(r => r.Tickable && _ticked.Contains(r.Name)).Select(r => r.Name).ToList();

    public bool IsTicked(string service) => _ticked.Contains(service);

    /// <summary>What the list shows (the search box and the two "Show" ticks); a new filter shows the rows again.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ServiceFilter Filter
    {
        get => _filter;
        set
        {
            if (_filter == value) return;
            _filter = value;
            Rebuild();
        }
    }

    public bool IsCollapsed(string groupId) => _collapsed.Contains(groupId);

    /// <summary>The services to show (after a read of the list). Ticks of services no longer tickable go; the rest stay.</summary>
    public void SetRows(IReadOnlyList<ServiceRow> rows)
    {
        _rows = rows;
        var tickable = rows.Where(r => r.Tickable).Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int before = _ticked.Count;
        _ticked.RemoveWhere(n => !tickable.Contains(n));
        Rebuild();
        if (_ticked.Count != before) TicksChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Every refresh while the tab shows: the Cost cells and their meters only (the list itself is read again on Refresh
    /// and after a change). The rows keep the new text, so a fold or a sort shows it too.
    /// </summary>
    public void UpdateCosts(Func<ServiceOption, (string Text, ImpactLevel? Level)> cost)
    {
        var byName = new Dictionary<string, ServiceRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _rows)
        {
            var (text, level) = cost(r.Option);
            if (r.Cells.Length > ServiceRows.CostColumn && r.Cells[ServiceRows.CostColumn] != text) r.Cells[ServiceRows.CostColumn] = text;
            r.Level = level;
            byName[r.Name] = r;
        }
        foreach (ListViewItem it in Items)
        {
            if (it.Tag is not ServiceOption o || it.SubItems.Count <= ServiceRows.CostColumn || !byName.TryGetValue(o.Service.Name, out var row)) continue;
            var sub = it.SubItems[ServiceRows.CostColumn];
            bool changed = sub.Text != row.Cells[ServiceRows.CostColumn] || !Equals(sub.Tag, row.Level);
            sub.Text = row.Cells[ServiceRows.CostColumn];
            sub.Tag = row.Level;
            if (changed && IsHandleCreated) Invalidate(sub.Bounds);
        }
    }

    /// <summary>Ticks exactly these (a profile picked): those that may be turned off. A ticked service always shows.</summary>
    public void SetTicks(IEnumerable<string> services)
    {
        var tickable = _rows.Where(r => r.Tickable).Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _ticked.Clear();
        foreach (var s in services) if (tickable.Contains(s)) _ticked.Add(s);
        Rebuild();
        TicksChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Space and the right-click menu: every selected service that may be turned off, a selected header's shown services
    /// with it. <paramref name="on"/> null: tick them all, unless all are ticked already, then untick them all.
    /// </summary>
    public void TickSelected(bool? on)
    {
        var targets = SelectionTargets();
        if (targets.Count == 0) return;
        bool tick = on ?? targets.Any(n => !_ticked.Contains(n));
        foreach (var n in targets)
            if (tick) _ticked.Add(n);
            else _ticked.Remove(n);
        SyncChecks();
        TicksChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A header's tick: all of the group's shown services ticked, unless all are already, then none.</summary>
    public void ToggleGroup(string groupId)
    {
        if (!_members.TryGetValue(groupId, out var members)) return;
        var names = members.Where(r => r.Tickable).Select(r => r.Name).ToList();
        if (names.Count == 0) return;
        bool all = names.All(_ticked.Contains);
        foreach (var n in names)
            if (all) _ticked.Remove(n);
            else _ticked.Add(n);
        SyncChecks();
        TicksChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Folds a group's services away under its header, or shows them again; the header keeps the focus.</summary>
    public void SetCollapsed(string groupId, bool collapsed)
    {
        if (collapsed ? !_collapsed.Add(groupId) : !_collapsed.Remove(groupId)) return;
        Rebuild();
    }

    /// <summary>A header click: the same column reverses, another sorts by it ascending (<see cref="ListSort.Next"/>).</summary>
    public void SortBy(int column)
    {
        (SortColumn, SortAscending) = ListSort.Next((SortColumn, SortAscending), column);
        Rebuild();
    }

    /// <summary>The services that Space or the menu acts on.</summary>
    private List<string> SelectionTargets()
    {
        var names = new List<string>();
        foreach (ListViewItem it in SelectedItems)
        {
            if (it.Tag is ServiceOption o && o.CanTurnOff) names.Add(o.Service.Name);
            else if (it.Tag is ServiceGroupRow g && _members.TryGetValue(g.Id, out var m)) names.AddRange(m.Where(r => r.Tickable).Select(r => r.Name));
        }
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The key a row is matched by across rebuilds (its selection, the focus, the top row).</summary>
    internal static string? KeyOf(ListViewItem it) => it.Tag switch
    {
        ServiceOption o => o.Service.Name,
        ServiceGroupRow g => g.Key,
        _ => null,
    };

    /// <summary>The rows again (as they are now filtered, ordered and folded), keeping the selection, the focus and the top row.</summary>
    private void Rebuild()
    {
        var selected = SelectedItems.Cast<ListViewItem>().Select(KeyOf).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? focused = FocusedItem is { } f ? KeyOf(f) : null;
        int topIndex = 0;
        var fromTop = new List<object>();
        try
        {
            if (IsHandleCreated && Items.Count > 0 && TopItem is { } top)
            {
                topIndex = top.Index;
                for (int i = top.Index; i < Items.Count && fromTop.Count < 64; i++) if (KeyOf(Items[i]) is { } k) fromTop.Add(k);
            }
        }
        catch (Exception) { /* best effort */ }
        _members = ServiceRows.Shown(_rows, _filter, _ticked).GroupBy(r => ServiceRows.GroupOf(r.Option)).ToDictionary(g => g.Key, g => g.ToList());
        var view = ServiceRows.Build(_rows, _filter, _collapsed, SortColumn, SortAscending, _ticked);
        BeginUpdate();
        _syncing = true; // items going in with their ticks raise ItemCheck: not the owner's clicks
        try
        {
            Items.Clear();
            Items.AddRange(view.Select(MakeItem).ToArray());
            var byKey = new Dictionary<string, ListViewItem>(StringComparer.OrdinalIgnoreCase);
            foreach (ListViewItem it in Items) if (KeyOf(it) is { } k) byKey.TryAdd(k, it);
            foreach (var k in selected) if (byKey.TryGetValue(k, out var it)) it.Selected = true;
            if (focused is not null && byKey.TryGetValue(focused, out var fi)) fi.Focused = true;
            try
            {
                if (IsHandleCreated && Core.Util.ScrollAnchor.Pick(topIndex, fromTop, k => byKey.ContainsKey((string)k)) is string a && byKey.TryGetValue(a, out var t)) TopItem = t;
            }
            catch (Exception) { }
        }
        finally
        {
            _syncing = false;
            EndUpdate();
        }
        _hot = -1;
        ListSort.SetArrow(this, SortColumn, SortAscending);
        ListRebuilt?.Invoke(this, EventArgs.Empty);
    }

    private ListViewItem MakeItem(object row)
    {
        switch (row)
        {
            case ServiceGroupRow g:
                return new ListViewItem([g.AccessibleText, "", "", "", ""]) { Tag = g, Checked = g.Tick == CheckState.Checked };
            case ServiceRow r:
                var it = new ListViewItem(r.Cells) { Tag = r.Option, Checked = r.Tickable && _ticked.Contains(r.Name), UseItemStyleForSubItems = false, ToolTipText = r.Tip ?? "" };
                var fore = r.Fore ?? ForeColor;
                for (int c = 0; c < it.SubItems.Count; c++)
                    it.SubItems[c].ForeColor = c == ServiceRows.SourceColumn && r.Fore is null ? ListTabStyle.FaintText : fore;
                if (it.SubItems.Count > ServiceRows.CostColumn) it.SubItems[ServiceRows.CostColumn].Tag = r.Level;
                return it;
            default:
                throw new ArgumentException("not a row of the services list", nameof(row));
        }
    }

    /// <summary>Every row's tick as <see cref="_ticked"/> has it, and each header's count and tick.</summary>
    private void SyncChecks()
    {
        _syncing = true;
        try
        {
            foreach (ListViewItem it in Items)
                if (it.Tag is ServiceOption o && it.Checked != (o.CanTurnOff && _ticked.Contains(o.Service.Name))) it.Checked = !it.Checked;
            RefreshHeaders();
        }
        finally { _syncing = false; }
    }

    /// <summary>
    /// Each header row's count, tick and text, from the ticks now. While Windows is still putting the items into a new
    /// window (WM_CREATE, where an exception cannot be thrown back and ends the process), the list hands out no item for
    /// the rows not in yet: those are skipped, never read.
    /// </summary>
    private void RefreshHeaders()
    {
        bool was = _syncing;
        _syncing = true;
        try
        {
            for (int i = 0; i < Items.Count; i++)
            {
                if (Items[i] is not { Tag: ServiceGroupRow g } it || !_members.TryGetValue(g.Id, out var m)) continue;
                var now = g with { Ticked = m.Count(r => r.Tickable && _ticked.Contains(r.Name)) };
                if (now == g) continue;
                it.Tag = now;
                it.Text = now.AccessibleText;
                bool all = now.Tick == CheckState.Checked;
                if (it.Checked != all) it.Checked = all;
                if (IsHandleCreated) Invalidate(it.Bounds);
            }
        }
        finally { _syncing = was; }
    }

    // ---------------------------------------------------------------- the owner's ticks, clicks and keys

    protected override void OnHandleCreated(EventArgs e)
    {
        // The items go back into the new window with their ticks (not the owner's clicks), the columns after them.
        _syncing = true;
        try { base.OnHandleCreated(e); }
        finally { _syncing = false; }
        ListSort.SetArrow(this, SortColumn, SortAscending);
    }

    protected override void OnItemCheck(ItemCheckEventArgs e)
    {
        if (!_syncing && e.Index >= 0 && e.Index < Items.Count)
        {
            var tag = Items[e.Index].Tag;
            // A header's tick is drawn here and set through ToggleGroup (its click, Space); a protected service never ticks.
            if (tag is ServiceGroupRow) e.NewValue = e.CurrentValue;
            else if (tag is ServiceOption { CanTurnOff: false } && e.NewValue == CheckState.Checked) e.NewValue = CheckState.Unchecked;
        }
        base.OnItemCheck(e);
    }

    protected override void OnItemChecked(ItemCheckedEventArgs e)
    {
        base.OnItemChecked(e);
        if (_syncing || e.Item?.Tag is not ServiceOption o) return;
        bool on = e.Item.Checked && o.CanTurnOff;
        if (on == _ticked.Contains(o.Service.Name)) return;
        if (on) _ticked.Add(o.Service.Name);
        else _ticked.Remove(o.Service.Name);
        RefreshHeaders();
        TicksChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnColumnClick(ColumnClickEventArgs e)
    {
        base.OnColumnClick(e);
        SortBy(e.Column);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        // Raised before Windows handles the click: the header's part is acted on once it has (the row is selected then,
        // and a rebuild cannot hand the click to another row).
        if (e.Button == MouseButtons.Left && e.Clicks == 1 && ClickAction(e.Location) is { } act) BeginInvoke(act);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        // An owner-drawn details list gets one paint of a row the pointer reaches with no cell paints after it, so the
        // row is drawn again once (or its Cost cell would show without its meter).
        if (HitTest(e.Location).Item is { } item && item.Index != _hot)
        {
            _hot = item.Index;
            Invalidate(item.Bounds);
        }
    }

    /// <summary>What a click at <paramref name="p"/> does on a header row: fold or unfold it, or tick its group; null for Windows' own click.</summary>
    internal Action? ClickAction(Point p)
    {
        if (HitTest(p).Item is not { Tag: ServiceGroupRow g } item) return null;
        return PartAt(item.Bounds, p) switch
        {
            GroupPart.Chevron => () => SetCollapsed(g.Id, !IsCollapsed(g.Id)),
            GroupPart.Tick when !g.Protected => () => ToggleGroup(g.Id),
            _ => null,
        };
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left) DoubleClickAt(e.Location);
    }

    /// <summary>A double-click on a header's name folds or unfolds it (on its arrow or tick, the first click already did).</summary>
    internal void DoubleClickAt(Point p)
    {
        if (HitTest(p).Item is { Tag: ServiceGroupRow g } item && PartAt(item.Bounds, p) == GroupPart.None) SetCollapsed(g.Id, !IsCollapsed(g.Id));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (HandleKey(e.KeyCode, e.Modifiers)) e.Handled = e.SuppressKeyPress = true;
        base.OnKeyDown(e);
    }

    /// <summary>Space, Ctrl+A, and Left / Right / Enter on a header row; true when the key was used here.</summary>
    internal bool HandleKey(Keys key, Keys modifiers)
    {
        if (key == Keys.Space && modifiers == Keys.None)
        {
            TickSelected(null);
            return true;
        }
        if (key == Keys.A && modifiers == Keys.Control)
        {
            BeginUpdate();
            try { foreach (ListViewItem it in Items) it.Selected = true; }
            finally { EndUpdate(); }
            return true;
        }
        if (FocusedItem?.Tag is ServiceGroupRow header && modifiers == Keys.None && key is Keys.Left or Keys.Right or Keys.Enter)
        {
            SetCollapsed(header.Id, key switch { Keys.Left => true, Keys.Right => false, _ => !header.Collapsed });
            return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- drawing the header rows and the meters

    internal enum GroupPart { None, Chevron, Tick }

    /// <summary>Where a header row's fold arrow and tick sit (and are clicked), from its bounds.</summary>
    internal (Rectangle chevron, Rectangle tick) Parts(Rectangle row)
    {
        int box = LogicalToDeviceUnits(13);
        var chevron = new Rectangle(row.Left + LogicalToDeviceUnits(2), row.Top, LogicalToDeviceUnits(16), row.Height);
        var tick = new Rectangle(chevron.Right + LogicalToDeviceUnits(3), row.Top + (row.Height - box) / 2, box, box);
        return (chevron, tick);
    }

    internal GroupPart PartAt(Rectangle row, Point p)
    {
        var (chevron, tick) = Parts(row);
        if (p.Y < row.Top || p.Y >= row.Bottom) return GroupPart.None;
        if (p.X >= row.Left && p.X < chevron.Right + LogicalToDeviceUnits(1)) return GroupPart.Chevron;
        if (p.X >= tick.Left - LogicalToDeviceUnits(2) && p.X < tick.Right + LogicalToDeviceUnits(3)) return GroupPart.Tick;
        return GroupPart.None;
    }

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
    {
        e.DrawDefault = true; // Windows draws the headers and their sort arrow
        base.OnDrawColumnHeader(e);
    }

    protected override void OnDrawItem(DrawListViewItemEventArgs e)
    {
        if (e.Item.Tag is ServiceGroupRow g)
        {
            e.DrawDefault = false;
            PaintGroup(e.Graphics, e.Bounds, g, e.Item.Selected, e.Item.Focused);
        }
        else e.DrawDefault = true;
        base.OnDrawItem(e);
    }

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        // A header row is drawn whole in OnDrawItem; a Cost cell here, with its meter; every other cell by Windows.
        if (e.Item?.Tag is ServiceGroupRow) e.DrawDefault = false;
        else if (e.ColumnIndex == ServiceRows.CostColumn && e.SubItem is { } sub)
        {
            e.DrawDefault = false;
            ListTabStyle.PaintCostCell(e, this, sub.Tag as ImpactLevel?, sub.Text, sub.ForeColor);
        }
        else e.DrawDefault = true;
        base.OnDrawSubItem(e);
    }

    /// <summary>
    /// A header row: the fold arrow, the group's tick (ticked, some, none; greyed for the protected group), its name in
    /// bold, its note in grey (cut short with an ellipsis when the row is too narrow), and the count at the right.
    /// </summary>
    internal void PaintGroup(Graphics gr, Rectangle row, ServiceGroupRow g, bool selected, bool focusedItem)
    {
        bool contrast = SystemInformation.HighContrast;
        bool active = selected && Focused;
        Color back = selected ? (active ? SystemColors.Highlight : SystemColors.Control) : contrast ? BackColor : ListTabStyle.Panel;
        Color name = active ? SystemColors.HighlightText : contrast ? SystemColors.WindowText : GroupText;
        Color quiet = active ? SystemColors.HighlightText : contrast ? SystemColors.GrayText : ListTabStyle.Faint;
        using (var b = new SolidBrush(back)) gr.FillRectangle(b, row);
        var (chevronRect, tickRect) = Parts(row);
        DrawChevron(gr, chevronRect, g.Collapsed, active ? SystemColors.HighlightText : contrast ? SystemColors.WindowText : Chevron);
        var state = g.Protected ? CheckBoxState.UncheckedDisabled : g.Tick switch
        {
            CheckState.Checked => CheckBoxState.CheckedNormal,
            CheckState.Indeterminate => CheckBoxState.MixedNormal,
            _ => CheckBoxState.UncheckedNormal,
        };
        var glyph = CheckBoxRenderer.GetGlyphSize(gr, state);
        CheckBoxRenderer.DrawCheckBox(gr, new Point(tickRect.Left + (tickRect.Width - glyph.Width) / 2, row.Top + (row.Height - glyph.Height) / 2), state);
        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        _bold ??= new Font(Font, FontStyle.Bold);
        int right = Math.Min(row.Right, ClientSize.Width) - LogicalToDeviceUnits(8);
        var countSize = TextRenderer.MeasureText(gr, g.CountText, Font, new Size(int.MaxValue, row.Height), flags);
        int countLeft = right - countSize.Width;
        int x = tickRect.Right + LogicalToDeviceUnits(6);
        var nameSize = TextRenderer.MeasureText(gr, g.Name, _bold, new Size(int.MaxValue, row.Height), flags);
        int nameRight = Math.Min(x + nameSize.Width + 1, countLeft - LogicalToDeviceUnits(12));
        TextRenderer.DrawText(gr, g.Name, _bold, Rectangle.FromLTRB(x, row.Top, Math.Max(x, nameRight), row.Bottom), name, flags | TextFormatFlags.EndEllipsis);
        int noteLeft = nameRight + LogicalToDeviceUnits(10), noteRight = countLeft - LogicalToDeviceUnits(12);
        if (noteRight - noteLeft > LogicalToDeviceUnits(24))
            TextRenderer.DrawText(gr, g.Note, Font, Rectangle.FromLTRB(noteLeft, row.Top, noteRight, row.Bottom), quiet, flags | TextFormatFlags.EndEllipsis);
        if (countLeft > x) TextRenderer.DrawText(gr, g.CountText, Font, Rectangle.FromLTRB(countLeft, row.Top, right + 1, row.Bottom), quiet, flags);
        if (focusedItem && Focused) ControlPaint.DrawFocusRectangle(gr, row);
    }

    private void DrawChevron(Graphics gr, Rectangle r, bool collapsed, Color color)
    {
        int s = LogicalToDeviceUnits(3), cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
        Point[] pts = collapsed
            ? [new(cx - s / 2, cy - s), new(cx + s / 2 + 1, cy), new(cx - s / 2, cy + s)]
            : [new(cx - s, cy - s / 2), new(cx, cy + s / 2 + 1), new(cx + s, cy - s / 2)];
        using var pen = new Pen(color, Math.Max(1f, LogicalToDeviceUnits(12) / 10f));
        var mode = gr.SmoothingMode;
        gr.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        gr.DrawLines(pen, pts);
        gr.SmoothingMode = mode;
    }

    protected override void OnFontChanged(EventArgs e)
    {
        _bold?.Dispose();
        _bold = null;
        base.OnFontChanged(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bold?.Dispose();
            ContextMenuStrip?.Dispose();
        }
        base.Dispose(disposing);
    }
}
