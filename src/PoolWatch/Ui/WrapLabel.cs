namespace PoolWatch.Ui;

/// <summary>
/// A label docked to the top that wraps its text and sets its own height to fit it, measured with its own font, so it
/// is never cut off at any display scaling (a fixed-height label clipped its last line at 150%). It draws its own text
/// (<see cref="PaintedLabel"/>), so a new text never blanks it.
/// </summary>
internal sealed class WrapLabel : PaintedLabel
{
    /// <summary>The height this label needs at its current width.</summary>
    public int NeededHeight => Measure(Text, Font, Width - Padding.Horizontal) + Padding.Vertical + 2;

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); FitHeight(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); FitHeight(); }
    protected override void OnPaddingChanged(EventArgs e) { base.OnPaddingChanged(e); FitHeight(); }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); FitHeight(); }

    private void FitHeight()
    {
        if (Width <= Padding.Horizontal) return;
        int h = NeededHeight;
        if (h != Height) Height = h; // the width is unchanged, so this settles at once
    }
}
