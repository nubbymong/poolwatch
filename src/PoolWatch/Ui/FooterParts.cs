using System.Drawing.Drawing2D;

namespace PoolWatch.Ui;

/// <summary>
/// A check box drawn as a switch, its text on the left (the main window's "Advanced view"): the box's own behaviour (a
/// click, the space bar, a screen reader's check button), its look as the design has it. It sizes itself to its text.
/// </summary>
internal sealed class ToggleSwitch : CheckBox
{
    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = true;
    }

    private int S(int logical) => LogicalToDeviceUnits(logical);

    private Size Switch => new(S(36), S(18));

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, Flags);
        return new Size(text.Width + S(8) + Switch.Width + S(4), Math.Max(text.Height, Switch.Height) + S(6));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var back = new SolidBrush(Parent?.BackColor ?? SystemColors.Window)) g.FillRectangle(back, ClientRectangle);
        bool hc = SystemInformation.HighContrast;
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, Flags);
        var textRect = new Rectangle(0, 0, text.Width, Height);
        TextRenderer.DrawText(g, Text, Font, textRect, Enabled ? UiColors.Text : SystemColors.GrayText, Flags);
        var sw = new Rectangle(text.Width + S(8), (Height - Switch.Height) / 2, Switch.Width, Switch.Height);
        using var path = Pill(sw);
        Color on = hc ? SystemColors.Highlight : UiColors.Accent, edge = hc ? SystemColors.WindowText : Color.FromArgb(140, 149, 159);
        if (Checked) using (var fill = new SolidBrush(on)) g.FillPath(fill, path);
        using (var pen = new Pen(Checked ? on : edge, Math.Max(1f, S(1) * 1.5f))) g.DrawPath(pen, path);
        int knob = sw.Height - S(8);
        var dot = new Rectangle(Checked ? sw.Right - S(4) - knob : sw.Left + S(4), sw.Top + S(4), knob, knob);
        using (var k = new SolidBrush(Checked ? (hc ? SystemColors.HighlightText : Color.White) : Color.FromArgb(95, 102, 112))) g.FillEllipse(k, dot);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(textRect, 1, -2));
    }

    private static GraphicsPath Pill(Rectangle r)
    {
        var p = new GraphicsPath();
        int d = r.Height;
        p.AddArc(r.Left, r.Top, d, d, 90, 180);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 180);
        p.CloseFigure();
        return p;
    }
}

/// <summary>
/// When the count beside Active changes is read (the window reads it off the UI thread: the list reads Task Scheduler, the
/// service manager and the registry): when the window shows, when it comes back to the front at least
/// <see cref="AgainAfter"/> after the last read (back from Active changes, which may have put a change back), after a card
/// made a change, and every <see cref="Every"/> while it shows; never two reads at once. Pure; the window gives the time.
/// </summary>
internal sealed class ChangeCountSchedule
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(5), AgainAfter = TimeSpan.FromSeconds(30);
    private DateTime _readUtc = DateTime.MinValue;
    private bool _reading;

    /// <summary>Read at the next refresh: the window showed, or a card made a change.</summary>
    public void Soon() => _readUtc = DateTime.MinValue;

    /// <summary>The window came back to the front: read at the next refresh, unless the last read is newer than <see cref="AgainAfter"/>.</summary>
    public void Activated(DateTime nowUtc)
    {
        if (nowUtc - _readUtc > AgainAfter) _readUtc = DateTime.MinValue;
    }

    /// <summary>A refresh: true when a read is due and none is running (the caller reads, then calls <see cref="Done"/>).</summary>
    public bool Start(DateTime nowUtc)
    {
        if (_reading || nowUtc - _readUtc < Every) return false;
        _reading = true;
        _readUtc = nowUtc;
        return true;
    }

    /// <summary>The read finished, or could not start.</summary>
    public void Done() => _reading = false;
}

/// <summary>
/// A small count in a soft pill (the number of active changes beside the footer's link). It sizes itself to the number;
/// a screen reader reads its own name.
/// </summary>
internal sealed class CountBadge : Control
{
    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
    private readonly Font _font = new("Segoe UI Semibold", 8.25f);

    public CountBadge()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        AccessibleRole = AccessibleRole.StaticText;
        Visible = false;
    }

    /// <summary>The count shown; null hides the badge (not known yet).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int? Count
    {
        get => _count;
        set
        {
            if (_count == value) return;
            _count = value;
            Text = value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            AccessibleName = value is { } n ? (n == 1 ? "1 active change" : $"{n} active changes") : "";
            Visible = value is not null;
            Size = GetPreferredSize(Size.Empty);
            Invalidate();
        }
    }
    private int? _count;

    private int S(int logical) => LogicalToDeviceUnits(logical);

    public override Size GetPreferredSize(Size proposedSize)
    {
        var t = TextRenderer.MeasureText(Text.Length > 0 ? Text : "0", _font, Size.Empty, Flags);
        return new Size(Math.Max(t.Width + S(14), t.Height + S(4)), t.Height + S(4));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var back = new SolidBrush(Parent?.BackColor ?? SystemColors.Window)) g.FillRectangle(back, ClientRectangle);
        bool hc = SystemInformation.HighContrast;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = new GraphicsPath();
        int d = r.Height;
        path.AddArc(r.Left, r.Top, d, d, 90, 180);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 180);
        path.CloseFigure();
        using (var fill = new SolidBrush(hc ? SystemColors.Window : UiColors.AccentSoft)) g.FillPath(fill, path);
        if (hc) using (var edge = new Pen(SystemColors.WindowText)) g.DrawPath(edge, path);
        TextRenderer.DrawText(g, Text, _font, ClientRectangle, hc ? SystemColors.WindowText : UiColors.Accent,
            Flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _font.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>The small list icon before the footer's Active changes link, as the design draws it: three lines, each with a dot.</summary>
internal sealed class ListIcon : Control
{
    public ListIcon()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        AccessibleRole = AccessibleRole.Graphic;
        Size = new Size(16, 16);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var back = new SolidBrush(Parent?.BackColor ?? SystemColors.Window)) g.FillRectangle(back, ClientRectangle);
        var color = SystemInformation.HighContrast ? SystemColors.WindowText : UiColors.Icon;
        float s = Math.Min(Width, Height) / 20f;
        using var pen = new Pen(color, Math.Max(1.2f, 1.5f * s)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
        using var dot = new SolidBrush(color);
        foreach (float y in new[] { 5.5f, 10f, 14.5f })
        {
            g.DrawLine(pen, 7 * s, y * s, 17 * s, y * s);
            g.FillEllipse(dot, 2.7f * s, (y - 1.1f) * s, 2.2f * s, 2.2f * s);
        }
    }
}
