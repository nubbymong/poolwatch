using System.Drawing.Imaging;
using PoolWatch.Core.Util;

namespace PoolWatch.Ui;

/// <summary>
/// The CI UI test's screenshots, for review and the README: any window drawn to a PNG as it is laid out now
/// (DrawToBitmap, so other windows on top of it never show).
/// </summary>
internal static class SmokeShots
{
    /// <summary>Draws <paramref name="window"/> into <paramref name="dir"/>\<paramref name="name"/>, through the write policy, and returns the file's path.</summary>
    public static string Save(Control window, string dir, string name, PathGuard guard)
    {
        Application.DoEvents(); // the last layout and paint first
        using var bmp = new Bitmap(window.Width, window.Height);
        window.DrawToBitmap(bmp, new Rectangle(0, 0, window.Width, window.Height));
        string path = Path.Combine(dir, name);
        bmp.Save(guard.CheckReal(path), ImageFormat.Png);
        return path;
    }
}
