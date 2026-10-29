using System.Drawing.Drawing2D;
using PoolWatch.Core.Impact;

namespace PoolWatch.Ui;

/// <summary>
/// The look of the Services and the Tasks and startup apps tabs, from the approved app design: its colours, its four
/// kinds of button (the primary action filled blue, an accent outline, a red one for what cannot be undone, and a plain
/// one), the cost meter (three short bars filled by level, yellow, orange or red) and the green "can be put back" note.
/// With high contrast on, buttons keep Windows' own look and colours come from the system palette.
/// </summary>
internal static class ListTabStyle
{
    // The app's one palette (UiColors), as these tabs name its colours.
    public static readonly Color Accent = UiColors.Accent, AccentHover = UiColors.AccentHover, AccentDown = UiColors.AccentDown;
    public static readonly Color AccentSoft = UiColors.AccentSoft;
    public static readonly Color Good = UiColors.Good;
    public static readonly Color Ink = UiColors.Ink, Muted = UiColors.Muted, Faint = UiColors.Faint;
    public static readonly Color Line = UiColors.Line, Border = UiColors.Border, Panel = UiColors.Panel;
    public static readonly Color Hover = UiColors.Hover, Pressed = UiColors.Pressed;
    public static readonly Color Danger = UiColors.Danger;
    public static readonly Color DisabledBack = UiColors.Hover, DisabledBorder = UiColors.DisabledBorder;

    /// <summary>Quiet grey text (a row's source, who an app is from): <see cref="Faint"/>, or the system's grey with high contrast on.</summary>
    public static Color FaintText => SystemInformation.HighContrast ? SystemColors.GrayText : Faint;

    /// <summary>
    /// The widths of a details list's columns for a list <paramref name="client"/> pixels wide. When every column fits at
    /// its <paramref name="ideal"/> width, each has it and the <paramref name="fill"/> column takes the room left. When
    /// not, every column gives up the same share of the way from its ideal width to its <paramref name="least"/>, so the
    /// columns fill the list exactly and it needs no sideways scroll bar, down to the sum of the least widths (below it,
    /// each column keeps its least and the list scrolls).
    /// </summary>
    public static int[] FitColumns(int client, IReadOnlyList<int> ideal, IReadOnlyList<int> least, int fill)
    {
        int n = ideal.Count;
        var w = new int[n];
        int sumIdeal = ideal.Sum(), sumLeast = least.Sum();
        if (client >= sumIdeal)
        {
            for (int i = 0; i < n; i++) w[i] = ideal[i];
            w[fill] += client - sumIdeal;
            return w;
        }
        double share = sumIdeal <= sumLeast ? 0 : Math.Clamp((client - sumLeast) / (double)(sumIdeal - sumLeast), 0, 1);
        for (int i = 0; i < n; i++) w[i] = least[i] + (int)Math.Floor((ideal[i] - least[i]) * share);
        int left = client - w.Sum();
        if (left > 0) w[fill] += left; // what the rounding left over
        return w;
    }

    /// <summary>The kinds of button the tabs use.</summary>
    public enum Kind
    {
        /// <summary>White, a grey border, dark text: every other action.</summary>
        Plain,
        /// <summary>Filled blue, white bold text: the one main action of a bar ("Turn off 1 service", "Don't start at sign-in").</summary>
        Primary,
        /// <summary>White, a blue border and blue bold text: an action to notice, not the main one ("Decide with your AI agent").</summary>
        Accent,
        /// <summary>White, a grey border and red text: an action that cannot be undone ("End it now").</summary>
        Danger,
        /// <summary>No border, blue text, like a link: a small action beside a line ("Save as profile...").</summary>
        Link,
    }

    /// <summary>A button's colours: its background, text and border, for its kind and whether it is enabled.</summary>
    public readonly record struct Colours(Color Back, Color Fore, Color Border, Color HoverBack, Color DownBack, bool Bold);

    public static Colours ColoursOf(Kind kind, bool enabled)
    {
        if (!enabled)
            return kind == Kind.Link
                ? new(Color.White, SystemColors.GrayText, Color.White, Color.White, Color.White, false)
                : new(DisabledBack, SystemColors.GrayText, DisabledBorder, DisabledBack, DisabledBack, kind is Kind.Primary or Kind.Accent);
        return kind switch
        {
            Kind.Primary => new(Accent, Color.White, Accent, AccentHover, AccentDown, true),
            Kind.Accent => new(Color.White, Accent, Accent, AccentSoft, Line, true),
            Kind.Danger => new(Color.White, Danger, Border, Hover, Pressed, false),
            Kind.Link => new(Color.White, Accent, Color.White, AccentSoft, Line, false),
            _ => new(Color.White, Ink, Border, Hover, Pressed, false),
        };
    }

    /// <summary>The kind a button was styled as (null: not styled, or high contrast).</summary>
    public static Kind? KindOf(Button b) => b.Tag is StyleTag t ? t.Kind : null;

    private sealed record StyleTag(Kind Kind);

    /// <summary>
    /// Styles <paramref name="b"/> as <paramref name="kind"/> and keeps it so when it is enabled or disabled. With high
    /// contrast on it keeps Windows' own look (the system colours are the user's choice then). A bold kind's font is the
    /// bold of the font around it (its parent's), made again whenever that changes: a button styled before it has a
    /// parent, a new display scaling or another system font all reach it, as they reach the plain buttons beside it.
    /// </summary>
    public static Button Style(Button b, Kind kind)
    {
        b.Tag = new StyleTag(kind);
        if (SystemInformation.HighContrast)
        {
            b.FlatStyle = FlatStyle.Standard;
            b.UseVisualStyleBackColor = true;
            return b;
        }
        b.FlatStyle = FlatStyle.Flat;
        b.UseVisualStyleBackColor = false;
        b.Cursor = Cursors.Hand;
        void Apply()
        {
            var c = ColoursOf(kind, b.Enabled);
            b.BackColor = c.Back;
            b.ForeColor = c.Fore;
            b.FlatAppearance.BorderColor = c.Border;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.MouseOverBackColor = c.HoverBack;
            b.FlatAppearance.MouseDownBackColor = c.DownBack;
        }
        Apply();
        b.EnabledChanged += (_, _) => Apply();
        if (ColoursOf(kind, true).Bold) KeepBold(b);
        return b;
    }

    /// <summary>Gives <paramref name="b"/> the bold of its parent's font, and again each time it moves or that font changes.</summary>
    private static void KeepBold(Button b)
    {
        Font? bold = null;
        Control? parent = null;
        void Rebold(object? sender, EventArgs e)
        {
            var basis = b.Parent?.Font ?? Control.DefaultFont;
            if (bold is not null && bold.FontFamily.Equals(basis.FontFamily) && bold.SizeInPoints == basis.SizeInPoints) return;
            var old = bold;
            b.Font = bold = new Font(basis, basis.Style | FontStyle.Bold);
            old?.Dispose();
        }
        void Moved(object? sender, EventArgs e)
        {
            if (parent is not null) parent.FontChanged -= Rebold;
            parent = b.Parent;
            if (parent is not null) parent.FontChanged += Rebold;
            Rebold(sender, e);
        }
        Moved(b, EventArgs.Empty);
        b.ParentChanged += Moved;
        b.Disposed += (_, _) =>
        {
            if (parent is not null) parent.FontChanged -= Rebold;
            bold?.Dispose();
        };
    }

    /// <summary>The cost meter's size at 96 dpi: three bars of 10 and two gaps of 2, 4 high, and the gap before its word.</summary>
    public const int MeterWidth = 34, MeterBar = 10, MeterGap = 2, MeterHeight = 4, MeterTextGap = 7;

    /// <summary>
    /// Draws the cost meter at the left of <paramref name="cell"/>, vertically centred, scaled by <paramref name="scale"/>
    /// (logical to device pixels): the impact scale's one meter (<see cref="ImpactScale.PaintMeter"/>), at this size; under high
    /// contrast its bars are in <paramref name="ink"/>, the text colour of the row it sits on (the window's when null).
    /// </summary>
    public static void PaintMeter(Graphics g, Rectangle cell, ImpactLevel level, Func<int, int> scale, Color? ink = null)
    {
        int bar = scale(MeterBar), gap = scale(MeterGap), h = Math.Max(2, scale(MeterHeight));
        ImpactScale.PaintMeter(g, new RectangleF(cell.Left, cell.Top + (cell.Height - h) / 2, bar, h), gap, level, SystemInformation.HighContrast, ink);
    }

    /// <summary>
    /// A list's cost cell: the row's background (selected or not), the meter when <paramref name="level"/> is known, then
    /// the text. For an owner-drawn details list.
    /// </summary>
    public static void PaintCostCell(DrawListViewSubItemEventArgs e, ListView list, ImpactLevel? level, string text, Color fore)
    {
        bool selected = e.Item?.Selected == true;
        bool active = selected && list.Focused;
        Color back = selected ? (active ? SystemColors.Highlight : SystemColors.Control) : e.Item?.BackColor is { IsEmpty: false } b ? b : list.BackColor;
        using (var brush = new SolidBrush(back)) e.Graphics.FillRectangle(brush, e.Bounds);
        int pad = list.LogicalToDeviceUnits(6);
        var area = Rectangle.FromLTRB(e.Bounds.Left + pad, e.Bounds.Top, e.Bounds.Right - list.LogicalToDeviceUnits(2), e.Bounds.Bottom);
        if (level is { } l && area.Width > list.LogicalToDeviceUnits(MeterWidth))
        {
            PaintMeter(e.Graphics, area, l, list.LogicalToDeviceUnits, active ? SystemColors.HighlightText : null);
            area = Rectangle.FromLTRB(area.Left + list.LogicalToDeviceUnits(MeterWidth + MeterTextGap), area.Top, area.Right, area.Bottom);
        }
        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(e.Graphics, text, list.Font, area, active ? SystemColors.HighlightText : fore, flags);
    }

    /// <summary>
    /// Keeps <paramref name="right"/>, the last control of a wrapping <paramref name="bar"/>, at the right end of the line
    /// it lands on: on the line of the controls before it when it fits there, else at the right of a line of its own.
    /// It reads where the panel laid the control before it out, so it runs once the bar has its size: hook it to the
    /// bar's and to <paramref name="right"/>'s SizeChanged (<see cref="KeepRight"/>).
    /// </summary>
    public static void AlignRight(FlowLayoutPanel bar, Control right, int minGap)
    {
        int edge = bar.ClientSize.Width - bar.Padding.Right; // where the line ends
        int start = bar.Padding.Left;                        // where a new line starts
        if (edge - start <= 0) return;
        // The control before it, where the panel has laid it out for this width (it never depends on the last one).
        Control? before = null;
        foreach (Control c in bar.Controls)
            if (c != right) before = c;
        int need = right.Width + right.Margin.Right;
        int x = before is null ? start : before.Right + before.Margin.Right;
        bool sameLine = before is null || x + minGap + need <= edge;
        int left = sameLine ? Math.Max(before is null ? 0 : minGap, edge - x - need - 1) : Math.Max(0, edge - start - need - 1);
        if (right.Margin.Left != left) right.Margin = new Padding(left, right.Margin.Top, right.Margin.Right, right.Margin.Bottom);
    }

    /// <summary><see cref="AlignRight"/> each time the bar or its right-hand control changes size (a new width, a longer button text).</summary>
    public static void KeepRight(FlowLayoutPanel bar, Control right, int minGap)
    {
        bar.SizeChanged += (_, _) => AlignRight(bar, right, minGap);
        right.SizeChanged += (_, _) => AlignRight(bar, right, minGap);
    }
}

/// <summary>
/// A line in the approved design's green with a shield and a tick before it: "Anything you turn off can be put back from
/// Active changes." Sized to its text; draws itself.
/// </summary>
internal sealed class GreenNote : Control
{
    public GreenNote(string text)
    {
        Text = text;
        AccessibleName = text;
        AccessibleRole = AccessibleRole.StaticText;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        BackColor = Color.Transparent;
        AutoSize = true;
    }

    private int Icon => LogicalToDeviceUnits(16);
    private int Gap => LogicalToDeviceUnits(8);

    private const TextFormatFlags Flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, Flags);
        return new Size(Icon + Gap + text.Width + LogicalToDeviceUnits(2), Math.Max(Icon, text.Height) + LogicalToDeviceUnits(4));
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); AccessibleName = Text; if (AutoSize) Size = GetPreferredSize(Size.Empty); Invalidate(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); if (AutoSize) Size = GetPreferredSize(Size.Empty); }

    protected override void OnPaint(PaintEventArgs e)
    {
        bool contrast = SystemInformation.HighContrast;
        var colour = contrast ? SystemColors.WindowText : ListTabStyle.Good;
        int s = Icon, top = (Height - s) / 2;
        float k = s / 20f;
        var mode = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pen = new Pen(colour, Math.Max(1f, 1.5f * k)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
        using (var shield = new GraphicsPath())
        {
            // The design's shield (20 x 20): a pointed shield and a tick inside it.
            PointF P(float x, float y) => new(x * k, top + y * k);
            shield.AddLines([P(10, 2.5f), P(16, 4.8f), P(16, 9.5f)]);
            shield.AddBezier(P(16, 9.5f), P(16, 13.3f), P(13.4f, 16.1f), P(10, 17.5f));
            shield.AddBezier(P(10, 17.5f), P(6.6f, 16.1f), P(4, 13.3f), P(4, 9.5f));
            shield.AddLines([P(4, 9.5f), P(4, 4.8f)]);
            shield.CloseFigure();
            e.Graphics.DrawPath(pen, shield);
            e.Graphics.DrawLines(pen, [P(7, 10), P(9.1f, 12.1f), P(13.2f, 8)]);
        }
        e.Graphics.SmoothingMode = mode;
        var area = new Rectangle(s + Gap, 0, Math.Max(0, Width - s - Gap), Height);
        TextRenderer.DrawText(e.Graphics, Text, Font, area, colour, Flags);
    }
}
