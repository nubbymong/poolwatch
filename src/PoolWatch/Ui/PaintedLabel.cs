namespace PoolWatch.Ui;

/// <summary>
/// Text that draws itself, double-buffered, the way a Label draws it. A Label is a native static control: setting its
/// Text makes Windows redraw it at once, and that redraw leaves it blank (its parent's background) until WinForms draws
/// the new text in the next paint, after the rest of the refresh has run, so a line rewritten by a refresh flashed. Here
/// a new text only invalidates: the old text stays on screen until one buffered paint replaces it.
/// </summary>
internal class PaintedLabel : Control
{
    private const TextFormatFlags Flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;
    private readonly TextFormatFlags _paintFlags;

    /// <param name="centerVertically">Centres the text vertically (the status line); otherwise it starts at the top.</param>
    public PaintedLabel(bool centerVertically = false)
    {
        _paintFlags = centerVertically ? Flags | TextFormatFlags.VerticalCenter : Flags;
        // A transparent background is accepted, as a Label accepts it: on a themed tab page the page shows through.
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        AccessibleRole = AccessibleRole.StaticText;
    }

    /// <summary>The height <paramref name="text"/> needs at <paramref name="width"/> pixels, wrapped as this control draws it.</summary>
    public static int Measure(string text, Font font, int width) =>
        TextRenderer.MeasureText(text.Length == 0 ? " " : text, font, new Size(Math.Max(1, width), int.MaxValue), Flags).Height;

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var area = Rectangle.FromLTRB(Padding.Left, Padding.Top, ClientSize.Width - Padding.Right, ClientSize.Height - Padding.Bottom);
        TextRenderer.DrawText(e.Graphics, Text, Font, area, ForeColor, _paintFlags);
    }
}
