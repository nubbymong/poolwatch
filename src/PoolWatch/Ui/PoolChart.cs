using System.Drawing.Drawing2D;
using PoolWatch.Core.Detection;

namespace PoolWatch.Ui;

/// <summary>
/// Header chart: committed paged / non-paged pool (what Task Manager shows) and the tag sums, over the last 6 h,
/// with incident markers. Paints only when invalidated; the form invalidates it only while visible.
/// </summary>
internal sealed class PoolChart : Control
{
    private IReadOnlyList<ChartPoint> _points = [];
    private IReadOnlyList<DateTime> _markers = [];
    private readonly Font _font = new("Segoe UI", 8.25f);
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Title { get; set; } = "Kernel pool, last 6 h (solid: committed, as Task Manager; dashed: sum of tag usage)";

    public PoolChart()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        BackColor = Color.White;
    }

    public void SetData(IReadOnlyList<ChartPoint> points, IReadOnlyList<DateTime> markers)
    {
        // Repaint only on new data (same last point and marker count: nothing to redraw).
        if (_points.Count == points.Count && points.Count > 0 && _points[^1] == points[^1] && _markers.Count == markers.Count) return;
        _points = points;
        _markers = markers;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var _ = Core.Util.Profiler.Stage("ui.chartPaint");
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var plot = new Rectangle(70, 22, Math.Max(10, Width - 90), Math.Max(10, Height - 46));
        var font = _font;
        g.DrawString(Title, font, Brushes.DimGray, 4, 3);
        if (_points.Count < 2) { g.DrawString("Collecting…", font, Brushes.Gray, plot.Left, plot.Top + 10); return; }

        DateTime t0 = _points[0].TimeUtc, t1 = _points[^1].TimeUtc;
        double span = Math.Max(1, (t1 - t0).TotalSeconds);
        double max = _points.Max(p => (double)Math.Max(Math.Max(p.Totals.CommittedPaged, p.Totals.CommittedNonPaged), Math.Max(p.Totals.TagSumPaged, p.Totals.TagSumNonPaged))) * 1.1;
        float X(DateTime t) => plot.Left + (float)((t - t0).TotalSeconds / span * plot.Width);
        float Y(double v) => plot.Bottom - (float)(v / max * plot.Height);

        using var grid = new Pen(Color.FromArgb(235, 235, 235));
        for (int i = 0; i <= 4; i++)
        {
            float y = plot.Top + plot.Height * i / 4f;
            g.DrawLine(grid, plot.Left, y, plot.Right, y);
            g.DrawString(Fmt.Bytes((long)(max * (4 - i) / 4)), font, Brushes.Gray, 2, y - 7);
        }
        g.DrawString(Fmt.Clock(t0), font, Brushes.Gray, plot.Left, plot.Bottom + 3);
        g.DrawString(Fmt.Clock(t1), font, Brushes.Gray, plot.Right - 50, plot.Bottom + 3);

        Series(g, Color.SteelBlue, false, p => p.Totals.CommittedPaged);
        Series(g, Color.DarkOrange, false, p => p.Totals.CommittedNonPaged);
        Series(g, Color.SteelBlue, true, p => p.Totals.TagSumPaged);
        Series(g, Color.DarkOrange, true, p => p.Totals.TagSumNonPaged);

        using var mark = new Pen(Color.Crimson, 1.5f) { DashStyle = DashStyle.Dot };
        foreach (var m in _markers.Where(m => m >= t0 && m <= t1)) g.DrawLine(mark, X(m), plot.Top, X(m), plot.Bottom);

        var last = _points[^1].Totals;
        g.DrawString($"paged {Fmt.Bytes((long)last.CommittedPaged)}", font, Brushes.SteelBlue, plot.Right - 260, 3);
        g.DrawString($"non-paged {Fmt.Bytes((long)last.CommittedNonPaged)}", font, Brushes.DarkOrange, plot.Right - 140, 3);

        void Series(Graphics gr, Color color, bool dashed, Func<ChartPoint, ulong> value)
        {
            using var pen = new Pen(color, dashed ? 1f : 2f) { DashStyle = dashed ? DashStyle.Dash : DashStyle.Solid };
            gr.DrawLines(pen, _points.Select(p => new PointF(X(p.TimeUtc), Y(value(p)))).ToArray());
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _font.Dispose();
        base.Dispose(disposing);
    }
}
