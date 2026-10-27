using PoolWatch.Core.Detection;

namespace PoolWatch.Ui;

/// <summary>
/// The selected tag's line over the view's window, and under it one band per suspect owner shaded by how busy it was
/// in each sample: the growth lining up with an app can be seen, not just read as a correlation number.
/// </summary>
internal sealed class ActivityStrip : Control
{
    private TagViewData? _data;
    private readonly Font _font = new("Segoe UI", 8.25f);
    private static readonly Color[] BandColors = [Color.SeaGreen, Color.SteelBlue, Color.DarkOrange, Color.MediumPurple];

    public ActivityStrip()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        BackColor = Color.White;
    }

    public void SetData(TagViewData? data)
    {
        if (ReferenceEquals(data, _data)) return;
        _data = data;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var d = _data;
        const int labelW = 170;
        // C5: never index or Min/Max an empty series in a paint (an exception here stops the control painting).
        if (d is null || d.Times.Count < 2 || d.TagSeries.Count < 2) { g.DrawString("Select a tag to see its growth and who was busy when it grew.", _font, Brushes.Gray, 4, 4); return; }
        var plot = new Rectangle(labelW, 4, Math.Max(10, Width - labelW - 8), Math.Max(10, (Height - 8) * 2 / 5));
        DateTime t0 = d.Times[0], t1 = d.Times[^1];
        double span = Math.Max(1, (t1 - t0).TotalSeconds);
        float X(DateTime t) => plot.Left + (float)((t - t0).TotalSeconds / span * plot.Width);
        long min = d.TagSeries.Min(), max = Math.Max(min + 1, d.TagSeries.Max());
        g.DrawString($"{d.Tag.ToDisplay()} {Fmt.Bytes(d.TagSeries[^1])}", _font, Brushes.Black, 2, plot.Top);
        g.DrawString($"{Fmt.Clock(t0)}–{Fmt.Clock(t1)}", _font, Brushes.Gray, 2, plot.Top + 14);
        var pts = new PointF[Math.Min(d.Times.Count, d.TagSeries.Count)];
        for (int i = 0; i < pts.Length; i++) pts[i] = new PointF(X(d.Times[i]), plot.Bottom - (float)((d.TagSeries[i] - min) / (double)(max - min) * plot.Height));
        using (var pen = new Pen(Color.Crimson, 2f)) if (pts.Length > 1) g.DrawLines(pen, pts);
        using (var onset = new Pen(Color.Gray) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot }) g.DrawLine(onset, X(d.OnsetUtc), plot.Top, X(d.OnsetUtc), Height - 2);
        int bandH = d.Bands.Count == 0 ? 0 : Math.Max(10, (Height - plot.Bottom - 8) / d.Bands.Count);
        int y = plot.Bottom + 6;
        if (d.Bands.Count == 0) g.DrawString("No suspect owner was busy enough to show.", _font, Brushes.Gray, labelW, y);
        for (int b = 0; b < d.Bands.Count; b++)
        {
            var band = d.Bands[b];
            string label = band.Owner.Length > 26 ? band.Owner[..26] + "…" : band.Owner;
            g.DrawString(label, _font, Brushes.Black, 2, y);
            double peak = band.Ops.Length == 0 ? 1 : Math.Max(1, band.Ops.Max());
            var color = BandColors[b % BandColors.Length];
            for (int i = 1; i < band.Ops.Length && i < d.Times.Count; i++)
            {
                if (band.Ops[i] <= 0) continue;
                int alpha = (int)Math.Clamp(40 + 215 * band.Ops[i] / peak, 0, 255);
                using var br = new SolidBrush(Color.FromArgb(alpha, color));
                float x0 = X(d.Times[i - 1]), x1 = X(d.Times[i]);
                g.FillRectangle(br, x0, y, Math.Max(1, x1 - x0), bandH - 3);
            }
            y += bandH;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _font.Dispose();
        base.Dispose(disposing);
    }
}
