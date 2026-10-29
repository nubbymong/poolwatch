using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// What a refresh of the main window repaints must never show blank. The status line flashed whenever a refresh changed
/// its text: it was a Label, a native static control, which Windows redraws blank the moment its text is set, ahead of
/// the paint that draws the new text. The labels the window refreshes now draw themselves (PaintedLabel), and the list
/// whose cells change on every refresh is drawn off screen. The controls here are never shown.
/// </summary>
public class RefreshRepaintTests
{
    private const int LVM_GETEXTENDEDLISTVIEWSTYLE = 0x1037, LVS_EX_DOUBLEBUFFER = 0x00010000;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [Fact]
    public void TheMainWindowShowsNoTextInANativeLabel()
    {
        // Every text a refresh writes is held in a field (the legend and the "Preset:" caption are never changed).
        var labels = typeof(MainForm).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(f => typeof(Label).IsAssignableFrom(f.FieldType)).Select(f => f.Name);
        Assert.Empty(labels);
    }

    [Theory]
    [InlineData(true, 13f, 0, 400, 60, "Kernel pool grew 709 MB in the last hour.")] // the status line
    [InlineData(false, 10f, 4, 260, 90, "File system metadata (Reff, refs.sys): 1.2 GB, a title long enough to wrap")] // as a WrapLabel draws (top, padded)
    public void ItDrawsItsTextExactlyAsALabelDid(bool centred, float points, int padding, int width, int height, string text)
    {
        using var host = new Form();
        using var font = new Font("Segoe UI Semibold", points);
        // GDI text, as in the app (ApplicationConfiguration.Initialize turns the GDI+ default off; a test host does not).
        var label = new Label { AutoSize = false, UseMnemonic = false, UseCompatibleTextRendering = false, TextAlign = centred ? ContentAlignment.MiddleLeft : ContentAlignment.TopLeft };
        var painted = new PaintedLabel(centerVertically: centred);
        foreach (var c in new Control[] { label, painted })
        {
            c.Font = font;
            c.ForeColor = Color.FromArgb(178, 34, 34);
            c.BackColor = Color.White;
            c.Padding = new Padding(padding);
            c.Size = new Size(width, height);
            c.Text = text;
            host.Controls.Add(c);
        }
        var expected = Pixels(label);
        Assert.True(expected.Count(p => p != Color.White.ToArgb()) > 200, "the Label drew its text");
        Assert.Equal(expected, Pixels(painted));
    }

    [Fact]
    public void ItShowsItsParentThroughAsALabelDid()
    {
        // A themed tab page reports a transparent background and its panels pass it on, so the details title and the
        // Services note show the page through, as a Label did. A control that does not accept Transparent falls back to
        // an opaque grey band.
        using var host = new Form();
        var panel = new Panel { BackColor = Color.Transparent, Size = new Size(300, 130) };
        host.Controls.Add(panel);
        var label = new Label { AutoSize = false, UseMnemonic = false, UseCompatibleTextRendering = false, Bounds = new Rectangle(10, 10, 260, 50) };
        var painted = new PaintedLabel { Bounds = new Rectangle(10, 70, 260, 50) };
        foreach (var c in new Control[] { label, painted })
        {
            c.Text = "Select a row to see its details.";
            panel.Controls.Add(c);
        }
        Assert.Equal(Color.Transparent, painted.BackColor);
        Assert.Equal(Pixels(label), Pixels(painted));
    }

    [Fact]
    public void ItIsDrawnOffScreenInOnePaint()
    {
        // Otherwise each paint first erases the text to the background on screen, then draws it again.
        using var label = new PaintedLabel();
        var getStyle = typeof(Control).GetMethod("GetStyle", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True((bool)getStyle.Invoke(label, [ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint])!);
    }

    [Fact]
    public void ANewTextIsDrawnInTheNextPaintAndTheSameTextChangesNothing()
    {
        using var host = new Form();
        var status = new PaintedLabel(centerVertically: true) { Size = new Size(400, 40) };
        host.Controls.Add(status);
        _ = status.Handle;
        int texts = 0, repaints = 0;
        status.TextChanged += (_, _) => texts++;
        status.Invalidated += (_, _) => repaints++;

        status.Text = "Kernel pool grew 709 MB in the last hour.";
        Assert.Equal(1, texts);
        Assert.True(repaints > 0, "a new text is painted");

        // The next refresh says the same thing: nothing is set or repainted.
        int painted = repaints;
        status.Text = "Kernel pool grew 709 MB in the last hour.";
        Assert.Equal(1, texts);
        Assert.Equal(painted, repaints);
    }

    [Fact]
    public void TheServicesListIsDrawnOffScreen()
    {
        // Its Cost cells change on every refresh; a list view that is not double-buffered erases a changed cell on screen
        // before it draws it again.
        Assert.Equal(typeof(BufferedListView), typeof(MainForm).GetField("_services", BindingFlags.Instance | BindingFlags.NonPublic)?.FieldType);
        using var host = new Form();
        var list = new BufferedListView();
        host.Controls.Add(list);
        Assert.NotEqual(0, (long)SendMessage(list.Handle, LVM_GETEXTENDEDLISTVIEWSTYLE, IntPtr.Zero, IntPtr.Zero) & LVS_EX_DOUBLEBUFFER);
    }

    /// <summary>The control as drawn (WM_PRINT, the same paint as on screen), one ARGB value per pixel.</summary>
    private static int[] Pixels(Control c)
    {
        using var bmp = new Bitmap(c.Width, c.Height, PixelFormat.Format32bppArgb);
        c.DrawToBitmap(bmp, new Rectangle(Point.Empty, c.Size));
        var data = bmp.LockBits(new Rectangle(Point.Empty, bmp.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new int[bmp.Width * bmp.Height]; // 32 bits a pixel: the rows have no padding
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            return pixels;
        }
        finally { bmp.UnlockBits(data); }
    }
}
