using PoolWatch.Core.Detection;
using PoolWatch.Core.Lag;

namespace PoolWatch.Ui;

/// <summary>Responsiveness over the last hour next to the pool: timer wake-up and file round-trip (ms) and the non-paged pool.</summary>
internal sealed class LagChart : Control
{
    private IReadOnlyList<LagProbe> _probes = [];
    private (double timer, double? message, double file, double dwm)? _baseline;
    private readonly Font _font = new("Segoe UI", 8.25f);

    public LagChart()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        BackColor = Color.White;
    }

    /// <summary>Settings.LagAlertRatio: the dashed threshold line.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public double AlertRatio { get; set; } = 5;

    public void SetData(IReadOnlyList<LagProbe> probes, (double timer, double? message, double file, double dwm)? baseline)
    {
        if (probes.Count == _probes.Count && probes.Count > 0 && ReferenceEquals(probes[^1], _probes[^1])) return;
        _probes = probes;
        _baseline = baseline;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.DrawString("Responsiveness, last hour: timer wake-up (blue) and file create/delete (green), ms; non-paged pool (orange, right scale)", _font, Brushes.DimGray, 4, 3);
        var plot = new Rectangle(60, 22, Math.Max(10, Width - 130), Math.Max(10, Height - 44));
        if (_probes.Count < 2) { g.DrawString("Collecting probes…", _font, Brushes.Gray, plot.Left, plot.Top + 10); return; }
        DateTime t0 = _probes[0].TimeUtc, t1 = _probes[^1].TimeUtc;
        double span = Math.Max(1, (t1 - t0).TotalSeconds);
        double maxMs = Math.Max(50, _probes.Max(p => Math.Max(p.TimerMs, p.FileMs)) * 1.1);
        double maxPool = Math.Max(1, _probes.Max(p => (double)p.Pool.CommittedNonPaged) * 1.1);
        float X(DateTime t) => plot.Left + (float)((t - t0).TotalSeconds / span * plot.Width);
        float Y(double v, double max) => plot.Bottom - (float)(v / max * plot.Height);
        using var grid = new Pen(Color.FromArgb(235, 235, 235));
        for (int i = 0; i <= 4; i++)
        {
            float y = plot.Top + plot.Height * i / 4f;
            g.DrawLine(grid, plot.Left, y, plot.Right, y);
            g.DrawString($"{maxMs * (4 - i) / 4:0} ms", _font, Brushes.Gray, 2, y - 7);
            g.DrawString(Fmt.Bytes((long)(maxPool * (4 - i) / 4)), _font, Brushes.DarkOrange, plot.Right + 4, y - 7);
        }
        if (_baseline is { } b)
        {
            using var dash = new Pen(Color.LightGray) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            g.DrawLine(dash, plot.Left, Y(b.timer * AlertRatio, maxMs), plot.Right, Y(b.timer * AlertRatio, maxMs));
        }
        Line(Color.SteelBlue, p => p.TimerMs, maxMs);
        Line(Color.SeaGreen, p => p.FileMs, maxMs);
        Line(Color.DarkOrange, p => p.Pool.CommittedNonPaged, maxPool);
        g.DrawString(Fmt.Clock(t0), _font, Brushes.Gray, plot.Left, plot.Bottom + 3);
        g.DrawString(Fmt.Clock(t1), _font, Brushes.Gray, plot.Right - 50, plot.Bottom + 3);

        void Line(Color c, Func<LagProbe, double> v, double max)
        {
            using var pen = new Pen(c, 1.5f);
            g.DrawLines(pen, _probes.Select(p => new PointF(X(p.TimeUtc), Y(v(p), max))).ToArray());
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _font.Dispose();
        base.Dispose(disposing);
    }
}
