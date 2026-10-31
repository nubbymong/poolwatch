using PoolWatch.Core.Impact;

namespace PoolWatch.Ui;

/// <summary>
/// The Tasks and startup apps tab's controls and their layout, as the approved app design has them; <see cref="TasksTab"/>
/// wires what they do. Top to bottom: the toolbar (All / Startup apps / Scheduled tasks / Busy in the background, Find,
/// "Show not running" and "Show Microsoft", both off at first, and Refresh at its right end), the list ranked by what each
/// costs now (a meter before the cost), the detail panel for the selected row (who it is from and when it starts, what
/// it costs, what the two actions do, the full command; "Don't start at sign-in" or "Turn this task off", the red "End
/// it now", "Open file location"), and the green line that anything switched off can be put back. Made on its own so
/// the tests lay it out at the window's sizes. Any change of the toolbar's filter raises <see cref="FilterChanged"/>.
/// </summary>
internal sealed class TasksTabView
{
    private readonly Control _scale;

    /// <summary>Each column's width at 100% when there is room (Starts takes what is left), and the least it shrinks to.</summary>
    internal static readonly int[] IdealWidths = [300, 150, 200, 220], LeastWidths = [170, 120, 140, 120];

    public TasksTabView(Control scale, ToolTip tips)
    {
        _scale = scale;
        foreach (var (h, w) in TaskRows.Columns.Zip(IdealWidths)) List.Columns.Add(h, S(w));
        List.ClientSizeChanged += (_, _) => FitColumns();

        foreach (var s in Enum.GetValues<TaskSegment>())
        {
            var b = new RadioButton
            {
                Text = TaskRows.SegmentText(s), Tag = s, AutoSize = true, Appearance = Appearance.Button, TextAlign = ContentAlignment.MiddleCenter,
                Padding = Px(8, 0, 8, 0), Margin = Padding.Empty, Checked = s == TaskSegment.All,
            };
            StyleSegment(b);
            b.CheckedChanged += (_, _) =>
            {
                StyleSegment(b);
                if (b.Checked) FilterChanged?.Invoke(this, EventArgs.Empty); // once: the segment picked, not the one left
            };
            Segments.Controls.Add(b);
        }
        Search.TextChanged += (_, _) => FilterChanged?.Invoke(this, EventArgs.Empty);
        ShowNotRunning.CheckedChanged += (_, _) => FilterChanged?.Invoke(this, EventArgs.Empty);
        ShowMicrosoft.CheckedChanged += (_, _) => FilterChanged?.Invoke(this, EventArgs.Empty);
        Segments.Margin = Px(0, 3, 12, 3);
        Search.Width = S(170);
        Search.Margin = Px(0, 4, 12, 3);
        Search.PlaceholderText = "Find";
        Search.AccessibleName = "Find a startup app or task";
        ShowNotRunning.Margin = ShowMicrosoft.Margin = Px(0, 7, 12, 0);
        tips.SetToolTip(ShowNotRunning, "Also list the startup apps and scheduled tasks whose program is not running now.");
        tips.SetToolTip(ShowMicrosoft, "Also list Microsoft's own startup apps. PoolWatch leaves them alone.");
        Refresh = ListTabStyle.Style(new Button { Text = "Refresh", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = Px(8, 1, 8, 1), Margin = Px(0, 3, 0, 3) },
            ListTabStyle.Kind.Plain);
        Toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = Px(0, 2, 0, 4) };
        Toolbar.Controls.AddRange([Segments, Search, ShowNotRunning, ShowMicrosoft, Refresh]);
        ListTabStyle.KeepRight(Toolbar, Refresh, S(12));

        Detail.Margin = Padding.Empty;
        Note.Margin = Px(0, 6, 0, 0);
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = Px(0, 6, 0, 0) };
        bottom.Controls.Add(Note);
        NoteBar = bottom;

        Page = new TabPage("Tasks and startup apps") { UseVisualStyleBackColor = true, Padding = Px(8, 6, 8, 6) };
        Page.Controls.Add(List); // fill first, then the docked edges (the last added is the outermost)
        Page.Controls.Add(Gap);
        Page.Controls.Add(Detail);
        Page.Controls.Add(Toolbar);
        Page.Controls.Add(NoteBar);
        // The keyboard goes as the eye does: the toolbar, the list, then the detail panel's buttons.
        (Toolbar.TabIndex, List.TabIndex, Detail.TabIndex) = (0, 1, 2);
        Gap.Height = S(10);
    }

    private int S(int logical) => _scale.LogicalToDeviceUnits(logical);
    private Padding Px(int l, int t, int r, int b) => new(S(l), S(t), S(r), S(b));

    /// <summary>A segment of the toolbar's switch: the one picked filled blue, the others white with a grey border.</summary>
    private static void StyleSegment(RadioButton b)
    {
        if (SystemInformation.HighContrast) return;
        b.FlatStyle = FlatStyle.Flat;
        b.UseVisualStyleBackColor = false;
        b.FlatAppearance.BorderColor = ListTabStyle.Border;
        b.FlatAppearance.CheckedBackColor = ListTabStyle.Accent;
        b.FlatAppearance.MouseOverBackColor = b.Checked ? ListTabStyle.AccentHover : ListTabStyle.Hover;
        b.BackColor = b.Checked ? ListTabStyle.Accent : Color.White;
        b.ForeColor = b.Checked ? Color.White : ListTabStyle.Ink;
    }

    public TabPage Page { get; }
    public FlowLayoutPanel Toolbar { get; }
    public FlowLayoutPanel Segments { get; } = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, AccessibleName = "Show" };
    public TextBox Search { get; } = new();
    public CheckBox ShowNotRunning { get; } = new() { Text = "Show not running", AutoSize = true };
    public CheckBox ShowMicrosoft { get; } = new() { Text = "Show Microsoft", AutoSize = true };
    public Button Refresh { get; }
    public TaskListView List { get; } = new() { Dock = DockStyle.Fill };
    public TaskDetailPanel Detail { get; } = new() { Dock = DockStyle.Bottom };
    /// <summary>The room between the list and the detail panel.</summary>
    public Control Gap { get; } = new Panel { Dock = DockStyle.Bottom, Margin = Padding.Empty };
    public FlowLayoutPanel NoteBar { get; }
    public GreenNote Note { get; } = new("Anything you switch off can be put back from Active changes.");

    /// <summary>The segment picked now.</summary>
    public TaskSegment Segment => Segments.Controls.OfType<RadioButton>().FirstOrDefault(b => b.Checked)?.Tag is TaskSegment s ? s : TaskSegment.All;

    /// <summary>The filter the segments, the search box and the two "Show" ticks set now.</summary>
    public TaskFilter Filter => new(Segment, Search.Text, ShowNotRunning.Checked, ShowMicrosoft.Checked);

    /// <summary>The search box, a "Show" tick or the segment changed: the list shows the rows again.</summary>
    public event EventHandler? FilterChanged;

    /// <summary>
    /// The columns fill the list's width and never pass it (<see cref="ListTabStyle.FitColumns"/>): the list needs no
    /// sideways scroll bar down to the window's minimum size.
    /// </summary>
    internal void FitColumns()
    {
        if (List.Columns.Count < TaskRows.Columns.Length || List.ClientSize.Width <= 0) return;
        var widths = ListTabStyle.FitColumns(List.ClientSize.Width, IdealWidths.Select(S).ToArray(), LeastWidths.Select(S).ToArray(), TaskRows.StartsColumn);
        for (int i = 0; i < widths.Length; i++) if (List.Columns[i].Width != widths[i]) List.Columns[i].Width = widths[i];
    }
}

/// <summary>
/// The Tasks and startup apps list: Windows draws it, except the Name cell (the name, then who it is from in grey) and
/// the Cost now cell (its meter before the words). Drawn off screen: its cost cells change while it shows.
/// </summary>
internal sealed class TaskListView : ListView
{
    private int _hot = -1;

    public TaskListView()
    {
        DoubleBuffered = true;
        OwnerDraw = true;
        View = View.Details;
        FullRowSelect = true;
        MultiSelect = false;
        HideSelection = false;
        AccessibleName = "Startup apps, scheduled tasks and apps busy in the background";
    }

    /// <summary>The row an item stands for (its tag is the row's key); null when it is unknown.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<object?, TaskRow?> RowOf { get; set; } = _ => null;

    private string _empty = "";

    /// <summary>What the list says under its header while it has no row ("Nothing here is running now. ..."); "" says nothing.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string EmptyText
    {
        get => _empty;
        set
        {
            if (_empty == value) return;
            _empty = value;
            if (IsHandleCreated) Invalidate();
        }
    }

    private const int WM_PAINT = 0x000F, LVM_GETHEADER = 0x1000 + 31;

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        // Windows paints an empty list blank: the line goes under the header once it has painted.
        if (m.Msg == WM_PAINT && Items.Count == 0 && _empty.Length > 0) PaintEmpty();
    }

    private void PaintEmpty()
    {
        int top = 0;
        IntPtr header = SendMessage(Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
        if (header != IntPtr.Zero && GetWindowRect(header, out var r)) top = r.Bottom - r.Top;
        int pad = LogicalToDeviceUnits(12);
        var area = new Rectangle(pad, top + pad, Math.Max(0, ClientSize.Width - 2 * pad), Math.Max(0, ClientSize.Height - top - 2 * pad));
        if (area.Width <= 0 || area.Height <= 0) return;
        using var g = Graphics.FromHwnd(Handle);
        // Drawn opaque on the list's own colour: a paint of part of the list must not draw the text over itself.
        TextRenderer.DrawText(g, _empty, Font, area, ListTabStyle.FaintText, BackColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
    {
        e.DrawDefault = true;
        base.OnDrawColumnHeader(e);
    }

    protected override void OnDrawItem(DrawListViewItemEventArgs e)
    {
        e.DrawDefault = true;
        base.OnDrawItem(e);
    }

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        var row = RowOf(e.Item?.Tag);
        if (row is null || e.SubItem is null) e.DrawDefault = true;
        else if (e.ColumnIndex == TaskRows.CostColumn)
        {
            e.DrawDefault = false;
            ListTabStyle.PaintCostCell(e, this, row.Cost.Level, e.SubItem.Text, e.Item!.ForeColor);
        }
        else if (e.ColumnIndex == TaskRows.NameColumn && row.Entry.Publisher is { Length: > 0 } from)
        {
            e.DrawDefault = false;
            PaintName(e, e.SubItem.Text, from);
        }
        else e.DrawDefault = true;
        base.OnDrawSubItem(e);
    }

    /// <summary>The Name cell: the name, then who it is from in grey, cut short with an ellipsis when the column is narrow.</summary>
    private void PaintName(DrawListViewSubItemEventArgs e, string name, string from)
    {
        bool selected = e.Item?.Selected == true, active = selected && Focused;
        Color back = selected ? (active ? SystemColors.Highlight : SystemColors.Control) : BackColor;
        using (var b = new SolidBrush(back)) e.Graphics.FillRectangle(b, e.Bounds);
        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        int x = e.Bounds.Left + LogicalToDeviceUnits(6), right = e.Bounds.Right - LogicalToDeviceUnits(2);
        var size = TextRenderer.MeasureText(e.Graphics, name, Font, new Size(int.MaxValue, e.Bounds.Height), flags);
        int nameRight = Math.Min(right, x + size.Width + 1);
        TextRenderer.DrawText(e.Graphics, name, Font, Rectangle.FromLTRB(x, e.Bounds.Top, nameRight, e.Bounds.Bottom),
            active ? SystemColors.HighlightText : e.Item!.ForeColor, flags | TextFormatFlags.EndEllipsis);
        int fromLeft = nameRight + LogicalToDeviceUnits(8);
        if (right - fromLeft > LogicalToDeviceUnits(20))
            TextRenderer.DrawText(e.Graphics, from, Font, Rectangle.FromLTRB(fromLeft, e.Bounds.Top, right, e.Bounds.Bottom),
                active ? SystemColors.HighlightText : ListTabStyle.FaintText, flags | TextFormatFlags.EndEllipsis);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        // An owner-drawn details list gets one paint of a row the pointer reaches with no cell paints after it: the row
        // is drawn again once, or its own cells would show as Windows draws them.
        if (HitTest(e.Location).Item is { } item && item.Index != _hot)
        {
            _hot = item.Index;
            Invalidate(item.Bounds);
        }
    }
}

/// <summary>
/// The panel under the tasks list for the selected row (<see cref="TaskDetail"/>): the name in bold with who it is from
/// and when it starts beside it (under it when the line is too narrow), what it costs now, what the actions do, the full
/// command in a fixed-width font; the buttons stacked at its right. It sets its own height to what its text needs at its
/// width; the command's box is capped so the panel stays within <see cref="MaxShare"/> of the page it is on where its other
/// lines allow (they are not capped, and the box keeps one line at least): a long command (a browser's or an editor's
/// helper often runs to hundreds of characters) shows its first lines in a box that scrolls, so the list above keeps its
/// rows. The command can be selected and copied there. With nothing selected it says how to use it, or why the list is
/// empty. Its fonts follow the window's (display scaling, the system's message font).
/// </summary>
internal sealed class TaskDetailPanel : Panel
{
    /// <summary>The most of its page's height the command's box lets the panel take (a command that needs more scrolls in it).</summary>
    public const double MaxShare = 0.4;

    /// <summary>The most lines of the command shown at once; a longer one scrolls.</summary>
    public const int MaxCommandLines = 4;

    private Font? _bold, _mono;
    private TaskDetail? _shown;
    private (string Title, string Line)? _empty;
    private bool _busy, _filled;

    public TaskDetailPanel()
    {
        DoubleBuffered = true;
        BackColor = SystemInformation.HighContrast ? SystemColors.Window : ListTabStyle.Panel;
        Command = new TextBox
        {
            ReadOnly = true, Multiline = true, WordWrap = true, BorderStyle = BorderStyle.None, ScrollBars = ScrollBars.None,
            BackColor = BackColor, TabStop = true, AccessibleName = "Full command",
        };
        Command.ForeColor = Sub.ForeColor = SystemInformation.HighContrast ? SystemColors.GrayText : ListTabStyle.Muted;
        MakeFonts();
        Toggle = ListTabStyle.Style(Btn("Don't start at sign-in"), ListTabStyle.Kind.Primary);
        End = ListTabStyle.Style(Btn("End it now"), ListTabStyle.Kind.Danger);
        Open = ListTabStyle.Style(Btn("Open file location"), ListTabStyle.Kind.Plain);
        Controls.AddRange([Title, Sub, CostLine, Explain, Command, Toggle, End, Open]);
        Display(null);
    }

    private static Button Btn(string text) => new() { Text = text, AutoSize = false, Margin = Padding.Empty, TextAlign = ContentAlignment.MiddleCenter };

    public WrapLabel Title { get; } = new();
    public WrapLabel Sub { get; } = new();
    public WrapLabel CostLine { get; } = new();
    public WrapLabel Explain { get; } = new();
    /// <summary>The full command: read-only, selectable, scrolling when it is longer than the lines the panel gives it.</summary>
    public TextBox Command { get; }
    public Button Toggle { get; }
    public Button End { get; }
    public Button Open { get; }

    /// <summary>What it shows now (null: nothing is selected).</summary>
    public TaskDetail? Shown => _shown;

    /// <summary>The height its text and buttons need at its width, the command's box capped (see <see cref="MaxShare"/>).</summary>
    public int NeededHeight { get; private set; }

    /// <summary>The lines of the command its box shows at once, and whether the rest scrolls.</summary>
    public (int Lines, bool Scrolls) CommandView { get; private set; }

    /// <summary>The switch's button is on the panel (a row with a switch is shown).</summary>
    public bool ToggleShown { get; private set; }

    /// <summary>"End it now" and "Open file location" are on the panel (a row is shown).</summary>
    public bool ButtonsShown { get; private set; }

    /// <summary>
    /// Shows <paramref name="d"/>, or with nothing selected the hint (or <paramref name="empty"/>: why the list shows no
    /// row); set only when it changed. <paramref name="busy"/>: the list is being read again (after a switch, on Refresh),
    /// so the switch and "End it now" wait for it, and a second click cannot repeat what the button still says. What it
    /// shows is kept here, not read back from the controls: a control on a tab that is not showing reads as hidden.
    /// </summary>
    public void Display(TaskDetail? d, bool busy = false, (string Title, string Line)? empty = null)
    {
        if (_filled && d == _shown && busy == _busy && empty == _empty) return;
        _filled = true;
        (_shown, _busy, _empty) = (d, busy, empty);
        Set(Title, d?.Title ?? empty?.Title ?? "Select a row");
        Set(Sub, d?.Sub ?? "");
        Set(CostLine, d?.CostLine ?? (empty is { } e ? e.Line : "to see what it runs, when it starts, what it costs now and what you can do about it."));
        Set(Explain, d?.Explain ?? "");
        Set(Command, d?.Command ?? "");
        if (d?.Toggle is { } t && Toggle.Text != t) Toggle.Text = t;
        ToggleShown = d?.Toggle is not null;
        ButtonsShown = d is not null;
        Toggle.Visible = ToggleShown;
        End.Visible = Open.Visible = ButtonsShown;
        Toggle.Enabled = !busy;
        End.Enabled = d?.CanEnd == true && !busy;
        Open.Enabled = d?.CanOpen == true;
        PerformLayout();
        Invalidate();
    }

    private static void Set(Control c, string text)
    {
        if (c.Text != text) c.Text = text;
        c.Visible = text.Length > 0;
    }

    /// <summary>The title's and the command's fonts, from the panel's own (which follows the window's): 10 and 8.5 points where it is Segoe UI 9.</summary>
    private void MakeFonts()
    {
        var basis = Font;
        float pt = basis.SizeInPoints;
        var bold = basis.Name == "Segoe UI" ? new Font("Segoe UI Semibold", pt * 10f / 9f) : new Font(basis.FontFamily, pt * 10f / 9f, FontStyle.Bold);
        // The design's fixed-width font, else the one every Windows has (a missing family comes back as another name).
        var mono = new Font("Cascadia Mono", pt * 8.5f / 9f);
        if (mono.Name != "Cascadia Mono") { mono.Dispose(); mono = new Font("Consolas", pt * 8.5f / 9f); }
        Title.Font = bold;
        Command.Font = mono;
        _bold?.Dispose();
        _mono?.Dispose();
        (_bold, _mono) = (bold, mono);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        MakeFonts();
        PerformLayout();
    }

    private Control? _page;

    /// <summary>
    /// The command's box is capped by the page's height, so the panel lays itself out again when only the page's height
    /// changes (a window dragged shorter, or snapped): docked at the bottom, it is moved then, not laid out.
    /// </summary>
    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        if (_page is not null) _page.ClientSizeChanged -= PageResized;
        _page = Parent;
        if (_page is not null) _page.ClientSizeChanged += PageResized;
    }

    private void PageResized(object? sender, EventArgs e) => PerformLayout();

    private const TextFormatFlags CommandFlags = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

    /// <summary>The height of one line of the command, measured as its box draws it.</summary>
    private int CommandLineHeight() => Math.Max(1, TextRenderer.MeasureText("Ag", _mono, new Size(int.MaxValue, int.MaxValue), CommandFlags).Height);

    /// <summary>The lines <paramref name="text"/> wraps to in the command's box <paramref name="width"/> pixels wide.</summary>
    private int CommandLines(string text, int width, int lineH)
    {
        if (text.Length == 0 || _mono is null) return 0;
        int h = TextRenderer.MeasureText(text, _mono, new Size(Math.Max(1, width), int.MaxValue), CommandFlags).Height;
        return Math.Max(1, (int)Math.Round(h / (double)lineH));
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_bold is null || _mono is null) return;
        int pad = LogicalToDeviceUnits(12), gap = LogicalToDeviceUnits(8), line = LogicalToDeviceUnits(2);
        var buttons = new List<Button>();
        if (ToggleShown) buttons.Add(Toggle);
        if (ButtonsShown) buttons.AddRange([End, Open]);
        int bw = buttons.Count == 0 ? 0 : buttons.Max(b => b.GetPreferredSize(Size.Empty).Width) + LogicalToDeviceUnits(16);
        int bh = buttons.Count == 0 ? 0 : buttons.Max(b => b.GetPreferredSize(Size.Empty).Height);
        int textRight = ClientSize.Width - pad - (bw > 0 ? bw + LogicalToDeviceUnits(20) : 0);
        int x = pad, y = pad, w = Math.Max(LogicalToDeviceUnits(80), textRight - x);

        // The name, and who it is from and when it starts beside it, as the design has them; under it when they do not fit.
        const TextFormatFlags one = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        int titleW = Title.Text.Length == 0 ? 0 : TextRenderer.MeasureText(Title.Text, _bold, Size.Empty, one).Width + Title.Padding.Horizontal + 4;
        int subW = Sub.Text.Length == 0 ? 0 : TextRenderer.MeasureText(Sub.Text, Font, Size.Empty, one).Width + Sub.Padding.Horizontal + 4;
        if (titleW > 0 && subW > 0 && titleW + gap + subW <= w)
        {
            Title.SetBounds(x, y, titleW, Math.Max(1, Title.Height));
            Sub.SetBounds(x + titleW + gap, y, w - titleW - gap, Math.Max(1, Sub.Height));
            Sub.Top = y + Math.Max(0, Title.Height - Sub.Height); // on the title's line, at its foot
            y = Math.Max(Title.Bottom, Sub.Bottom) + line;
        }
        else
            foreach (var c in new Control[] { Title, Sub })
            {
                if (c.Text.Length == 0) continue;
                c.SetBounds(x, y, w, Math.Max(1, c.Height));
                y = c.Bottom + line;
            }
        foreach (var c in new Control[] { CostLine, Explain })
        {
            if (c.Text.Length == 0) continue;
            c.SetBounds(x, y, w, Math.Max(1, c.Height));
            y = c.Bottom + line;
        }

        // The command: all of it when the panel stays within its share of the page, else as many lines as fit there
        // (one at least, MaxCommandLines at most), the rest scrolling in its box.
        CommandView = (0, false);
        if (Command.Text.Length > 0)
        {
            int lineH = CommandLineHeight(), edge = LogicalToDeviceUnits(6);
            int need = CommandLines(Command.Text, w - LogicalToDeviceUnits(10), lineH); // less the box's own margins, so it never wraps to more
            int cap = Parent is { } p && p.DisplayRectangle.Height > 0 ? (int)(p.DisplayRectangle.Height * MaxShare) : int.MaxValue;
            int room = Math.Clamp((cap - y - pad - edge) / lineH, 1, MaxCommandLines);
            bool scrolls = need > room;
            int lines = Math.Min(need, room);
            var bars = scrolls ? ScrollBars.Vertical : ScrollBars.None;
            if (Command.ScrollBars != bars) Command.ScrollBars = bars;
            Command.SetBounds(x, y, w, lines * lineH + edge);
            CommandView = (lines, scrolls);
            y = Command.Bottom + line;
        }

        int by = pad;
        foreach (var b in buttons)
        {
            b.SetBounds(ClientSize.Width - pad - bw, by, bw, bh);
            by = b.Bottom + gap;
        }
        int textH = y - line + pad, buttonsH = buttons.Count == 0 ? 0 : by - gap + pad;
        NeededHeight = Math.Max(textH, buttonsH);
        if (Height != NeededHeight && Width > 0) Height = NeededHeight;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(SystemInformation.HighContrast ? SystemColors.WindowFrame : ListTabStyle.Line);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_page is not null) _page.ClientSizeChanged -= PageResized;
            _bold?.Dispose();
            _mono?.Dispose();
        }
        base.Dispose(disposing);
    }
}
