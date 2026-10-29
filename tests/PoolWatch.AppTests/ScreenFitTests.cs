using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// A window that sizes itself to its content, opened on a screen too short for it (About at a high scaling on a small
/// display, or on CI's runner): it keeps its content's width and scrolls vertically, and scrolls sideways only on a screen
/// narrower than its content. The windows here get a native window but are never shown.
/// </summary>
/// <remarks>
/// WinForms never makes a form larger than the largest window the desktop allows: <c>Form.SetBoundsCore</c> cuts a
/// form's size to <see cref="SystemInformation.MaxWindowTrackSize"/>, the whole desktop and a frame (1044 x 788 on CI's
/// 1024 x 768 runner). The test host is DPI-unaware, so the desktop it sees is the screen divided by its scaling
/// (1920 x 1080 at 175% is 1097 x 617). A test window larger than that is cut on one machine and not on another, so the
/// test checks something else on each. Every window here, as it opens and once it is fitted, is therefore at most
/// 833 x 479 px and fits whole on a desktop the test host sees as 1024 x 600 or larger (<see cref="Open"/> checks it and
/// says so on a smaller one), except the one that is meant to be cut, which is made taller than this machine's largest
/// window.
/// </remarks>
public class ScreenFitTests
{
    /// <summary>About's column (620 logical px with 16 on each side), 440 px of it, so its window (668 x 479) fits a 1024 x 600 desktop.</summary>
    private static readonly Size AboutLike = new(652, 440);

    /// <summary>One column of content as at <paramref name="percent"/> scaling: small enough that at 200% its window (816 x 439, and 833 px wide once it scrolls) still fits a 1024 x 600 desktop.</summary>
    private static Size Column(int percent) => new(400 * percent / 100, 200 * percent / 100);

    /// <summary>Shaped like About: a fixed dialog that sizes itself to one column of content.</summary>
    private sealed class Dialog : Form
    {
        public Panel Content { get; }

        public Dialog(Size content)
        {
            FormBorderStyle = FormBorderStyle.FixedDialog;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Content = new Panel { Size = content, Location = Point.Empty, Margin = Padding.Empty };
            Controls.Add(Content);
        }
    }

    private static Dialog Created(Size content)
    {
        var d = new Dialog(content);
        _ = d.Handle;      // the native window, never shown
        d.PerformLayout(); // sized to its content, as when it opens
        return d;
    }

    /// <summary>The dialog as it opens, sized to the whole of its content (not cut to the desktop, see the remarks above).</summary>
    private static Dialog Open(Size content)
    {
        var d = Created(content);
        if (d.ClientSize != content)
        {
            var shown = d.ClientSize;
            d.Dispose();
            Assert.Fail($"the window shows {shown} of its content's {content}: this desktop's largest window is {SystemInformation.MaxWindowTrackSize}");
        }
        return d;
    }

    [Theory]
    [InlineData(100)]
    [InlineData(125)]
    [InlineData(150)]
    [InlineData(200)]
    public void ATooTallWindowKeepsItsContentsWidthAndScrollsOnlyVertically(int percent)
    {
        // On a working area as wide as CI's screen and half as tall as the content.
        var content = Column(percent);
        using var d = Open(content);
        var area = new Rectangle(0, 0, 1024, content.Height / 2);

        Assert.True(ScreenFit.ToArea(d, area), $"the window is {d.Size} on an area of {area.Size}");

        Assert.Equal(area.Height, d.Height);
        Assert.True(d.AutoScroll && !d.AutoSize, $"AutoScroll is {d.AutoScroll} and AutoSize {d.AutoSize}");
        // Turning AutoSize off first put the window back to 300 x 300, and the old code kept that width: 284 px shown.
        Assert.True(d.ClientSize.Width >= content.Width, $"the window shows {d.ClientSize.Width} px of its content's {content.Width}");
        Assert.True(d.Content.Left >= 0 && d.Content.Right <= d.ClientSize.Width, $"the content is at {d.Content.Bounds} in {d.ClientSize}");
        Assert.True(d.VerticalScroll.Visible, "it scrolls vertically");
        Assert.False(d.HorizontalScroll.Visible, "it does not scroll sideways");
    }

    [Theory]
    [InlineData(100)]
    [InlineData(125)]
    [InlineData(150)]
    [InlineData(200)]
    public void OnAWorkingAreaNarrowerThanItsContentTheWindowIsAsWideAsTheAreaAndScrollsBothWays(int percent)
    {
        // The content cannot keep its width here: the window is never wider than the screen, and the rest is scrolled to.
        var content = Column(percent);
        using var d = Open(content);
        var area = new Rectangle(0, 0, content.Width * 2 / 3, content.Height / 2);

        Assert.True(ScreenFit.ToArea(d, area), $"the window is {d.Size} on an area of {area.Size}");

        Assert.Equal(area.Size, d.Size);
        Assert.True(d.AutoScroll && !d.AutoSize, $"AutoScroll is {d.AutoScroll} and AutoSize {d.AutoSize}");
        Assert.True(d.VerticalScroll.Visible && d.HorizontalScroll.Visible, "it scrolls both ways");
        Assert.True(d.DisplayRectangle.Width >= content.Width && d.DisplayRectangle.Height >= content.Height,
            $"it scrolls over {d.DisplayRectangle.Size} of its content's {content}");
    }

    [Fact]
    public void AWindowAlreadyCutToTheLargestTheDesktopAllowsIsFittedTheSameWay()
    {
        // About on CI's 1024 x 768 runner: its window is taller than the desktop, so WinForms has cut it to the largest
        // window the desktop allows (788 px there) before the fit reads its size. The content here is taller than this
        // machine's largest window, so the window is cut on every machine.
        var largest = SystemInformation.MaxWindowTrackSize;
        var content = new Size(AboutLike.Width, largest.Height + 100);
        using var d = Created(content);
        Assert.True(d.Height == largest.Height && d.ClientSize.Width == content.Width, $"the window is {d.Size} for content of {content}");
        // CI's working area: 720 px, below a 48 px taskbar on a desktop 20 px shorter than its largest window. On a
        // shorter desktop it is the same 68 px shorter than the largest window, so the cut window is always taller.
        var area = new Rectangle(0, 0, 1024, Math.Min(720, largest.Height - 68));

        Assert.True(ScreenFit.ToArea(d, area), $"the window is {d.Size} on an area of {area.Size}");

        Assert.Equal(area.Height, d.Height);
        Assert.True(d.ClientSize.Width >= content.Width, $"the window shows {d.ClientSize.Width} px of its content's {content.Width}");
        Assert.True(d.VerticalScroll.Visible, "it scrolls vertically");
        Assert.False(d.HorizontalScroll.Visible, "it does not scroll sideways");
    }

    [Fact]
    public void AWindowThatFitsIsLeftAsItIs()
    {
        using var d = Open(new Size(652, 400));
        var before = d.Size;
        Assert.False(ScreenFit.ToArea(d, new Rectangle(0, 0, 1024, 728)), $"the window, {before}, was fitted to an area 728 px tall");
        Assert.Equal(before, d.Size);
        Assert.True(d.AutoSize && !d.AutoScroll, $"AutoSize is {d.AutoSize} and AutoScroll {d.AutoScroll}");
    }

    [Fact]
    public void AWindowThatAlreadyScrollsIsLeftAsItIsOnAShorterScreen()
    {
        // As when the setup guide fills in again after it was moved to a shorter screen: it no longer sizes itself, so
        // it is not widened by a second scroll bar.
        using var d = Open(AboutLike);
        Assert.True(ScreenFit.ToArea(d, new Rectangle(0, 0, 1920, 400)), $"the window is {d.Size} on an area 400 px tall");
        var size = d.Size;

        Assert.False(ScreenFit.ToArea(d, new Rectangle(0, 0, 1920, 300)), $"the window, {size}, was fitted again to an area 300 px tall");

        Assert.Equal(size, d.Size);
    }

    [Theory]
    [InlineData(17, 26)] // a monitor at 100% beside a main display at 150%: WinForms reckons with the main display's bar
    [InlineData(26, 17)] // a monitor at 150% beside a main display at 100%: Windows draws the wider bar
    public void OnAMonitorScaledUnlikeTheMainDisplayTheWindowMakesRoomForTheWiderBar(int windowBar, int systemBar)
    {
        using var d = Open(AboutLike);
        int width = d.Width;

        Assert.True(ScreenFit.ToArea(d, new Rectangle(0, 0, 1920, 400), windowBar, systemBar), $"the window is {d.Size} on an area 400 px tall");

        Assert.Equal(width + 26, d.Width);
    }

    [Fact]
    public void TheSetupGuideOnAScreenTooShortForItKeepsItsWidthAndSitsAtTheTopOfTheArea()
    {
        // The guide's fit (ScreenFit.IntoArea), on a working area below a taskbar at the top of the screen.
        var content = AboutLike;
        using var d = Open(content);
        d.Location = new Point(100, 300);
        var area = new Rectangle(0, 40, 1920, 400);

        Assert.True(ScreenFit.IntoArea(d, area), $"the window is {d.Size} on an area of {area.Size}");

        Assert.Equal(area.Height, d.Height);
        Assert.Equal(area.Top, d.Top);
        Assert.Equal(100, d.Left);
        // The guide had the same code as About: turning AutoSize off first left it 300 px wide.
        Assert.True(d.ClientSize.Width >= content.Width, $"the window shows {d.ClientSize.Width} px of its content's {content.Width}");
        Assert.True(d.VerticalScroll.Visible, "it scrolls vertically");
        Assert.False(d.HorizontalScroll.Visible, "it does not scroll sideways");
    }

    [Fact]
    public void ASetupGuideThatGrewPastTheBottomOfTheAreaMovesUpInsideIt()
    {
        // It still fits, so it keeps sizing itself; only its place changes.
        using var d = Open(new Size(652, 400));
        d.Location = new Point(100, 500);
        var size = d.Size;
        var area = new Rectangle(0, 40, 1920, 600);
        Assert.True(d.Bottom > area.Bottom, $"the window ends at {d.Bottom}, below the area's {area.Bottom}");

        Assert.False(ScreenFit.IntoArea(d, area), $"the window, {size}, was fitted to an area of {area.Size}");

        Assert.Equal(size, d.Size);
        Assert.True(d.AutoSize && !d.AutoScroll, $"AutoSize is {d.AutoSize} and AutoScroll {d.AutoScroll}");
        Assert.Equal(area.Bottom, d.Bottom);
        Assert.Equal(100, d.Left);
    }

    [Fact]
    public void AScrollingSetupGuideStillTooTallForTheAreaStaysAtTheAreasTop()
    {
        // A guide that already scrolls fills in again on a shorter screen: it keeps its size, and moving it up must not take
        // its title bar above the working area, where the window could no longer be dragged.
        using var d = Open(AboutLike);
        Assert.True(ScreenFit.ToArea(d, new Rectangle(0, 0, 1920, 400)), $"the window is {d.Size} on an area 400 px tall");
        var size = d.Size;
        d.Location = new Point(100, 300);

        Assert.False(ScreenFit.IntoArea(d, new Rectangle(0, 40, 1920, 300)), $"the window, {size}, was fitted again to an area 300 px tall");

        Assert.Equal(40, d.Top);
        Assert.Equal(100, d.Left);
        Assert.Equal(size, d.Size);
    }

    [Fact]
    public void TheScrollingWindowIsWiderByTheScrollBarButNeverWiderOrTallerThanTheScreen()
    {
        Assert.Equal(new Size(686, 700), ScreenFit.Capped(new Size(669, 900), new Size(1024, 700), 17));
        Assert.Equal(new Size(1024, 700), ScreenFit.Capped(new Size(1100, 900), new Size(1024, 700), 34));
    }

    [Fact]
    public void TheLayoutCheckLetsAScrollingParentRunPastItsBottomButNeverPastItsSides()
    {
        // A parent with no parent of its own counts as visible, so the check looks at its children (in an unshown
        // window it would skip them).
        using var parent = new Panel { Size = new Size(300, 200) };
        var content = new Panel { Bounds = new Rectangle(0, 0, 250, 500) };
        parent.Controls.Add(content);
        List<string> Problems()
        {
            var p = new List<string>();
            MainForm.ClippedControls(parent, p);
            return p;
        }

        Assert.Single(Problems());   // it does not scroll: what is below its bottom is cut off
        parent.AutoScroll = true;
        Assert.Empty(Problems());    // it scrolls: the user scrolls to it
        content.Width = 400;
        Assert.Single(Problems());   // wider than the parent: cut off, scrolling or not
        content.Bounds = new Rectangle(-16, 0, 250, 500);
        Assert.Single(Problems());   // scrolled sideways
    }
}
