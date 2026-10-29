namespace PoolWatch.Ui;

/// <summary>
/// A label that wraps its text and sets its own height to fit it at its width, measured with its own font, so it is
/// never cut off at any display scaling (a fixed-height label clipped its last line at 150%). It is docked at the top,
/// placed by its parent, or docked at the bottom with AutoSize on, which docks it at that height in the same layout
/// (<see cref="GetPreferredSize"/>). It draws its own text (<see cref="PaintedLabel"/>), so a new text never blanks it.
/// </summary>
internal sealed class WrapLabel : PaintedLabel
{
    /// <summary>The height this label needs at its current width.</summary>
    public int NeededHeight => HeightAt(Width);

    /// <summary>The height this label needs at <paramref name="width"/> pixels (what it sets itself to at that width).</summary>
    public int HeightAt(int width) => Measure(Text, Font, width - Padding.Horizontal) + Padding.Vertical + 2;

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

    /// <summary>
    /// Its height at <paramref name="proposedSize"/>'s width (its own width when none is given). Docked with AutoSize on,
    /// its parent docks it at that height, so a new width and the height for it come in the parent's one layout. Setting
    /// its own height once docked at a new width came in the middle of that layout, which drops the layout the change asks
    /// for: docked at the bottom it kept its top and ran past its parent's edge until something laid the parent out again.
    /// </summary>
    public override Size GetPreferredSize(Size proposedSize)
    {
        int width = proposedSize.Width is > 0 and < int.MaxValue ? proposedSize.Width : Width;
        return width <= Padding.Horizontal ? Size : new Size(width, HeightAt(width));
    }
}
