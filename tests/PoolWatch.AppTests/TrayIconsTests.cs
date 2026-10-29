using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// The tray and window icons are the .ico files in src/PoolWatch/Assets. These tests read them from the checkout through
/// the loader the app uses for its embedded copies: the binoculars everywhere, every tray state with a dot in its own
/// colour, the windows' icon with none, and Dispose frees them all. One reads the copies embedded in PoolWatch.exe, so a
/// build that leaves one out, or embeds one under another's name, fails here, not when PoolWatch starts.
/// </summary>
public class TrayIconsTests
{
    [Fact]
    public void EveryStateHasItsOwnIcon()
    {
        using var icons = TrayIconSet.Load(FromCheckout);
        var all = All(icons);
        Assert.All(all, i => Assert.NotEqual(IntPtr.Zero, i.Handle));
        Assert.Equal(all.Count, all.Select(Colour).Distinct().Count()); // by what they show: two states on one file fail
    }

    [Fact]
    public void EachStateShowsItsColourInTheDot()
    {
        using var icons = TrayIconSet.Load(FromCheckout);
        Assert.InRange(Dot(icons.For(TrayState.Green)).GetHue(), 90, 170);
        Assert.InRange(Dot(icons.For(TrayState.Amber)).GetHue(), 25, 60);
        float red = Dot(icons.For(TrayState.Red)).GetHue();
        Assert.True(red < 15 || red > 340, $"the red dot's hue is {red}");
        Assert.True(Dot(icons.Starting).GetSaturation() < 0.15, "the starting icon's dot is grey");
        Assert.True(Colour(icons.Starting).GetSaturation() < 0.15, "the starting icon is grey binoculars");
    }

    [Fact]
    public void TheBinocularsAreOnEveryIconAndTheWindowsHaveNoStateDot()
    {
        using var icons = TrayIconSet.Load(FromCheckout);
        // The binoculars: blue, the program's own icon and each state's alike (the body is the same picture).
        foreach (var icon in new[] { icons.App, icons.For(TrayState.Green), icons.For(TrayState.Amber), icons.For(TrayState.Red) })
            Assert.InRange(Body(icon).GetHue(), 190, 230);
        // Where a state icon has its dot, the windows' icon has the binoculars' own colours: no green, amber or red.
        float hue = Dot(icons.App).GetHue();
        Assert.False((hue is > 25 and < 170 || hue < 15 || hue > 340) && Dot(icons.App).GetSaturation() > 0.5, $"the windows' icon shows a state's colour (hue {hue})");
    }

    [Fact]
    public void TheWindowsShowTheBinocularsNeverATrayState()
    {
        // The windows the tests can build, as they open: each has the app's own icon, the very one the app loads.
        using (var settings = new SettingsDialog(new PoolWatch.Core.Settings(), null)) Assert.Same(TrayIcons.App, settings.Icon);
        using (var about = new AboutDialog(new AboutFacts("1.0.0", "samples", "overhead", @"C:\ProgramData\PoolWatch"), actions: null)) Assert.Same(TrayIcons.App, about.Icon);
        using (var f = new ChangeFixture())
        using (var advanced = new AdvancedSettingsDialog(new SettingsEditor(engine: null!, analysis: null!, actions: null!, f.Live(), shell: null!)))
            Assert.Same(TrayIcons.App, advanced.Icon);
        using (var notice = GuardNotices.NewNotice("The token-leak guard is on.", ("OK", null))) Assert.Same(TrayIcons.App, notice.Icon);
        Assert.False(TrayIcons.App.Size.IsEmpty);
        // The main window needs the whole app to build: its source sets the same icon, and no window here sets a state's.
        string ui = Path.Combine(RepoRoot(), "src", "PoolWatch", "Ui");
        Assert.Contains("Icon = TrayIcons.App;", File.ReadAllText(Path.Combine(ui, "MainForm.cs")));
        foreach (var file in new[] { "MainForm.cs", "SettingsDialog.cs", "AboutDialog.cs", "AdvancedSettingsDialog.cs", "GuardNotice.cs" })
            Assert.DoesNotContain("Icon = TrayIcons.For(", File.ReadAllText(Path.Combine(ui, file)));
    }

    [Theory]
    [InlineData("poolwatch.ico", new[] { 16, 20, 24, 32, 40, 48, 64, 256 })]
    [InlineData("tray-healthy.ico", new[] { 16, 20, 24, 32, 40, 48, 64 })]
    [InlineData("tray-warning.ico", new[] { 16, 20, 24, 32, 40, 48, 64 })]
    [InlineData("tray-critical.ico", new[] { 16, 20, 24, 32, 40, 48, 64 })]
    [InlineData("tray-inactive.ico", new[] { 16, 20, 24, 32, 40, 48, 64 })]
    public void EveryIconHoldsAFrameForEachSizeWindowsAsksFor(string file, int[] sizes)
    {
        // The .ico directory: a count, then 16 bytes an image, its width first (0 for 256).
        using var stream = FromCheckout(file)!;
        var head = new byte[6];
        stream.ReadExactly(head);
        int count = BitConverter.ToUInt16(head, 4);
        var found = new List<int>();
        for (int i = 0; i < count; i++)
        {
            var entry = new byte[16];
            stream.ReadExactly(entry);
            found.Add(entry[0] == 0 ? 256 : entry[0]);
        }
        Assert.Equal(sizes, found.Order());
        // Each frame opens at its own size (the 256 frame is a PNG, as Windows' own large icons are: Explorer and the
        // installer read it, the Icon class does not pick it).
        foreach (int size in sizes.Where(s => s < 256))
        {
            using var again = FromCheckout(file)!;
            using var icon = new Icon(again, size, size);
            Assert.Equal(new Size(size, size), icon.Size);
        }
    }

    [Fact]
    public void DisposeFreesEveryIcon()
    {
        var icons = TrayIconSet.Load(FromCheckout);
        var all = All(icons);
        icons.Dispose();
        Assert.All(all, i => Assert.Throws<ObjectDisposedException>(() => i.Handle));
    }

    [Fact]
    public void PoolWatchExeHoldsEveryIcon()
    {
        var exe = typeof(TrayIconSet).Assembly;
        var opened = new List<string>();
        using var icons = TrayIconSet.Load(name => { opened.Add(name); return exe.GetManifestResourceStream(name); });
        Assert.All(All(icons), i => Assert.NotEqual(IntPtr.Zero, i.Handle));
        // Each embedded copy is the file of its own name, so no state shows another state's colour.
        Assert.All(opened, name => Assert.Equal(Bytes(FromCheckout(name)), Bytes(exe.GetManifestResourceStream(name))));
    }

    [Fact]
    public void AMissingIconIsNamed()
    {
        var e = Assert.Throws<InvalidOperationException>(() => TrayIconSet.Load(_ => null));
        Assert.Contains("tray-healthy.ico", e.Message);
    }

    private static List<Icon> All(TrayIconSet icons) => [.. Enum.GetValues<TrayState>().Select(icons.For), icons.Starting, icons.App];

    private static Stream? FromCheckout(string name)
    {
        string path = Path.Combine(RepoRoot(), "src", "PoolWatch", "Assets", name);
        return File.Exists(path) ? File.OpenRead(path) : null;
    }

    /// <summary>The test runs from bin/&lt;config&gt;/&lt;tfm&gt;/ inside the checkout; the repo root is the folder holding PoolWatch.slnx.</summary>
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PoolWatch.slnx"))) return dir.FullName;
        throw new InvalidOperationException("PoolWatch.slnx not found above " + AppContext.BaseDirectory);
    }

    private static byte[] Bytes(Stream? stream)
    {
        Assert.NotNull(stream);
        using (stream)
        {
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }
    }

    /// <summary>The average colour of the icon's opaque pixels.</summary>
    private static Color Colour(Icon icon) => Average(icon, new RectangleF(0, 0, 1, 1));

    /// <summary>The colour at the middle of the state's dot, at the bottom right.</summary>
    private static Color Dot(Icon icon)
    {
        using var bmp = icon.ToBitmap();
        return bmp.GetPixel((int)(bmp.Width * 0.74), (int)(bmp.Height * 0.74));
    }

    /// <summary>The average colour of the binoculars' left half, away from the dot.</summary>
    private static Color Body(Icon icon) => Average(icon, new RectangleF(0, 0, 0.5f, 1));

    /// <summary>The average colour of the opaque pixels in a part of the icon (as fractions of its size).</summary>
    private static Color Average(Icon icon, RectangleF part)
    {
        using var bmp = icon.ToBitmap();
        long r = 0, g = 0, b = 0, n = 0;
        for (int y = (int)(part.Top * bmp.Height); y < (int)(part.Bottom * bmp.Height); y++)
            for (int x = (int)(part.Left * bmp.Width); x < (int)(part.Right * bmp.Width); x++)
            {
                var p = bmp.GetPixel(x, y);
                if (p.A < 250) continue;
                r += p.R; g += p.G; b += p.B; n++;
            }
        Assert.True(n > 0, "the icon has no opaque pixels");
        return Color.FromArgb((int)(r / n), (int)(g / n), (int)(b / n));
    }
}
