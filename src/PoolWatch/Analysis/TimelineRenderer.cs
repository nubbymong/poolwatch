using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Incidents;

namespace PoolWatch.Analysis;

/// <summary>Draws the incident timeline (tag usage, committed pool total, markers) to a PNG with GDI+.</summary>
internal static class TimelineRenderer
{
    public static void Render(TimelineChart c, string path, int width = 960, int height = 380)
    {
        using var bmp = new Bitmap(width, height);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Color.White);
        using var font = new Font("Segoe UI", 9f);
        using var bold = new Font("Segoe UI", 10f, FontStyle.Bold);
        var plot = new Rectangle(70, 34, width - 140, height - 80);
        g.DrawString(c.Title, bold, Brushes.Black, 70, 8);

        if (c.Times.Count < 2) { bmp.Save(path, ImageFormat.Png); return; }
        DateTime t0 = c.Times[0], t1 = c.Times[^1];
        double span = Math.Max(1, (t1 - t0).TotalSeconds);
        float X(DateTime t) => plot.Left + (float)((t - t0).TotalSeconds / span * plot.Width);

        long tagMin = c.TagBytes.Min(), tagMax = Math.Max(tagMin + 1, c.TagBytes.Max());
        long totMin = c.CommittedPoolBytes.Min(), totMax = Math.Max(totMin + 1, c.CommittedPoolBytes.Max());
        float YTag(long v) => plot.Bottom - (float)((v - tagMin) / (double)(tagMax - tagMin) * plot.Height);
        float YTot(long v) => plot.Bottom - (float)((v - totMin) / (double)(totMax - totMin) * plot.Height);

        using var grid = new Pen(Color.FromArgb(230, 230, 230));
        for (int i = 0; i <= 4; i++)
        {
            float y = plot.Top + plot.Height * i / 4f;
            g.DrawLine(grid, plot.Left, y, plot.Right, y);
            long tv = tagMax - (tagMax - tagMin) * i / 4, cv = totMax - (totMax - totMin) * i / 4;
            g.DrawString(Fmt.Bytes(tv), font, Brushes.SteelBlue, 2, y - 7);
            g.DrawString(Fmt.Bytes(cv), font, Brushes.Gray, plot.Right + 4, y - 7);
        }
        for (int i = 0; i <= 4; i++)
        {
            var t = t0.AddSeconds(span * i / 4);
            float x = X(t);
            g.DrawLine(grid, x, plot.Top, x, plot.Bottom);
            g.DrawString(Fmt.Clock(t), font, Brushes.Black, x - 24, plot.Bottom + 4);
        }

        using (var totPen = new Pen(Color.Gray, 1.5f) { DashStyle = DashStyle.Dash })
            g.DrawLines(totPen, c.Times.Select((t, i) => new PointF(X(t), YTot(c.CommittedPoolBytes[i]))).ToArray());
        using (var tagPen = new Pen(Color.SteelBlue, 2.2f))
            g.DrawLines(tagPen, c.Times.Select((t, i) => new PointF(X(t), YTag(c.TagBytes[i]))).ToArray());

        using var markPen = new Pen(Color.IndianRed, 1f) { DashStyle = DashStyle.Dot };
        int row = 0;
        foreach (var (time, label) in c.Markers.Where(m => m.TimeUtc >= t0 && m.TimeUtc <= t1).OrderBy(m => m.TimeUtc))
        {
            float x = X(time);
            g.DrawLine(markPen, x, plot.Top, x, plot.Bottom);
            g.DrawString(label, font, Brushes.IndianRed, x + 2, plot.Top + 2 + 14 * (row++ % 4));
        }
        g.DrawString("— tag usage", font, Brushes.SteelBlue, plot.Left, height - 22);
        g.DrawString("- - " + c.CommittedLabel, font, Brushes.Gray, plot.Left + 110, height - 22);
        bmp.Save(path, ImageFormat.Png);
    }
}
