namespace PoolWatch.Ui;

/// <summary>
/// A window that sizes itself to its content (AutoSize, grow and shrink) on a screen too short for it, such as About at
/// a high scaling on a small display: it keeps the width of its content, becomes as tall as the screen's working area
/// and scrolls vertically. It scrolls sideways only on a working area narrower than its content.
/// </summary>
internal static class ScreenFit
{
    /// <summary>Fits <paramref name="form"/> to the working area of the screen it is on. True when it now scrolls.</summary>
    internal static bool ToWorkingArea(Form form) => ToArea(form, Screen.FromControl(form).WorkingArea);

    /// <summary>
    /// An auto-sized <paramref name="form"/> taller than <paramref name="area"/> becomes as tall as the area, as wide as its
    /// content plus the vertical scroll bar (never wider than the area), and scrolls. A form that fits, or that no longer
    /// sizes itself, is left as it is. True when it now scrolls.
    /// </summary>
    internal static bool ToArea(Form form, Rectangle area) =>
        ToArea(form, area, SystemInformation.GetVerticalScrollBarWidthForDpi(form.DeviceDpi), SystemInformation.VerticalScrollBarWidth);

    /// <summary>
    /// <see cref="ToArea(Form, Rectangle)"/> with the two widths of the vertical scroll bar: <paramref name="windowBar"/>,
    /// the bar Windows draws at the window's DPI, and <paramref name="systemBar"/>, the width at the system DPI (the main
    /// display's scaling when the user signed in) that WinForms subtracts when it decides whether the content also needs
    /// a sideways bar (<c>ScrollableControl.ApplyScrollbarChanges</c>). They differ on a monitor scaled unlike the main
    /// display, or after its scaling changed; the window is widened by the wider of the two, so neither leaves the
    /// content short of room.
    /// </summary>
    internal static bool ToArea(Form form, Rectangle area, int windowBar, int systemBar)
    {
        if (!form.AutoSize || form.Height <= area.Height) return false;
        // Read the size first: turning AutoSize off puts a form back to the size it was last given, the default
        // 300 x 300 for these windows, so a width read after that is not the content's (About opened 300 px wide).
        // The size may already be cut to the largest window the desktop allows (WinForms limits every form to
        // SystemInformation.MaxWindowTrackSize, the whole desktop and a frame: About's 896 px is 788 on a 1024 x 768
        // desktop). A cut height is still taller than the working area and a cut width still wider, so the fit is the same.
        var size = Capped(form.Size, area.Size, Math.Max(windowBar, systemBar));
        form.AutoSize = false;
        form.Size = size;
        form.AutoScroll = true;
        return true;
    }

    /// <summary>
    /// The size of a <paramref name="window"/> that scrolls vertically on a working area of <paramref name="area"/>: as
    /// tall as the area, wider by the vertical scroll bar so its content keeps its width, and never wider than the area.
    /// </summary>
    internal static Size Capped(Size window, Size area, int scrollBar) =>
        new(Math.Min(window.Width + scrollBar, area.Width), Math.Min(window.Height, area.Height));
}
