using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Globalization;
using PoolWatch.Core.Cache;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Util;

namespace PoolWatch.Ui;

/// <summary>
/// A cache clear the graph marks: when, what it freed (measured 2 minutes after it), whether PoolWatch started it, whether
/// it was a light (low-priority) one, and whether it has been measured yet.
/// </summary>
internal sealed record ChartClear(DateTime TimeUtc, long FreedBytes, bool Automatic, bool Light, bool Done)
{
    public static ChartClear Of(ClearRecord r) => new(r.TimeUtc, r.Freed, r.Automatic, r.Mode == ClearMode.LowPriority, r.Done);

    /// <summary>Its mark's hover text: "Cache clear at 09:12 (automatic): freed 4.7 GB".</summary>
    public string Text => ClearScheduleText.Mark(TimeUtc, FreedBytes, Automatic, Light, Done);

    /// <summary>What the status line says in place of the drop it caused: "The cache clear at 03:56 freed 1.3 GB."</summary>
    public string Result => ClearScheduleText.Result(TimeUtc, FreedBytes, Done);
}

/// <summary>Where the chart drew its text and clear marks last time (for the CI layout check: nothing may overlap or fall outside).</summary>
internal sealed record ChartLayout(Size Client, Rectangle Title, Rectangle Legend, Rectangle Plot, IReadOnlyList<Rectangle> YLabels, IReadOnlyList<Rectangle> XLabels,
    IReadOnlyList<Rectangle> Markers)
{
    /// <summary>Overlapping or clipped text, and clear marks outside the plot, in words; empty when the layout is clean.</summary>
    public List<string> Problems()
    {
        var p = new List<string>();
        var client = new Rectangle(Point.Empty, Client);
        void Inside(string what, Rectangle r) { if (!r.IsEmpty && !client.Contains(r)) p.Add($"{what} {r} is outside the chart {Client}"); }
        Inside("title", Title);
        Inside("legend", Legend);
        foreach (var r in YLabels) Inside("axis label", r);
        foreach (var r in XLabels) Inside("time label", r);
        foreach (var r in Markers)
            if (!Plot.Contains(r)) p.Add($"clear mark {r} is outside the plot {Plot}");
        if (!Legend.IsEmpty && Title.IntersectsWith(Legend)) p.Add("the legend overlaps the title");
        foreach (var r in YLabels)
            if (r.IntersectsWith(Title) || (!Legend.IsEmpty && r.IntersectsWith(Legend))) p.Add($"axis label {r} overlaps the title or legend");
        for (int i = 0; i < XLabels.Count; i++)
        {
            for (int j = i + 1; j < XLabels.Count; j++)
                if (XLabels[i].IntersectsWith(XLabels[j])) p.Add($"time labels {XLabels[i]} and {XLabels[j]} overlap");
            if (YLabels.Any(y => y.IntersectsWith(XLabels[i]))) p.Add($"time label {XLabels[i]} overlaps an axis label");
        }
        return p;
    }
}

/// <summary>
/// The main window's one graph: the committed paged and non-paged pool (the figures Task Manager shows) over the last
/// 6 hours, solid lines with a plain legend, and a mark at each cache clear that shows on hover what it freed, so a drop a
/// clear caused reads as the clear. Every position comes from the font's measured size, so the axis labels never reach the
/// title at any display scaling. Paints only when invalidated; the window feeds it only while visible.
/// </summary>
internal sealed class PoolChart : Control
{
    public static readonly TimeSpan Span = TimeSpan.FromHours(6);
    private static readonly Color PagedColor = Color.SteelBlue, NonPagedColor = Color.DarkOrange;
    private static readonly Color GridColor = Color.FromArgb(232, 232, 232), TextColor = Color.FromArgb(90, 90, 90);
    private static readonly Color MarkColor = Color.FromArgb(46, 125, 50), MarkLineColor = Color.FromArgb(190, 212, 192);
    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
    private IReadOnlyList<ChartPoint> _points = [];
    private IReadOnlyList<ChartClear> _clears = [];
    private readonly Font _font = new("Segoe UI", 9f);
    private readonly Font _titleFont = new("Segoe UI Semibold", 10f);

    // One tooltip: over a clear mark (or its line) what the clear freed, elsewhere HelpText.
    private readonly ToolTip _tip = new() { AutoPopDelay = 30_000, InitialDelay = 400 };
    private List<(Rectangle Hit, string Text)> _markHits = [];
    private string _helpText = "";
    private string? _tipText;

    public PoolChart()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        BackColor = Color.White;
    }

    /// <summary>The tooltip shown anywhere but over a clear mark.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] // set in code, never by a designer
    public string HelpText
    {
        get => _helpText;
        set { _helpText = value; ShowTip(value); }
    }

    /// <summary>The clears to mark (any span: those outside the graph's are left out). Repaints only when they changed.</summary>
    public void SetClears(IReadOnlyList<ChartClear> clears)
    {
        if (clears.SequenceEqual(_clears)) return;
        _clears = clears;
        Invalidate();
    }

    /// <summary>The tooltip at a point of the last paint: every clear marked there (marks close in time overlap), else <see cref="HelpText"/>.</summary>
    internal string TipAt(Point p)
    {
        var over = _markHits.Where(m => m.Hit.Contains(p)).Select(m => m.Text).ToList();
        return over.Count > 0 ? string.Join(Environment.NewLine, over) : _helpText;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        ShowTip(TipAt(e.Location));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        ShowTip(_helpText);
    }

    private void ShowTip(string text)
    {
        if (text == _tipText) return;
        _tipText = text;
        _tip.SetToolTip(this, text);
    }

    /// <summary>The text positions of the last paint (null before the first).</summary>
    internal ChartLayout? LastLayout { get; private set; }

    /// <summary>How many points the chart holds (CI).</summary>
    internal int PointCount => _points.Count;

    public void SetData(IReadOnlyList<ChartPoint> points)
    {
        // Repaint only on new data.
        if (_points.Count == points.Count && points.Count > 0 && _points[^1] == points[^1]) return;
        _points = points;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var _ = Core.Util.Profiler.Stage("ui.chartPaint");
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int fh = _font.Height;
        int pad = Math.Max(4, fh / 2);

        // The points in range: the last 6 hours, or since the first sample when PoolWatch has run for less.
        var pts = _points;
        DateTime t1 = pts.Count > 0 ? pts[^1].TimeUtc : DateTime.UtcNow;
        DateTime from = t1 - Span;
        if (pts.Count > 0 && pts[0].TimeUtc < from) pts = pts.Where(p => p.TimeUtc >= from).ToList();
        DateTime t0 = pts.Count > 0 ? pts[0].TimeUtc : from;
        string title = pts.Count < 2 ? PoolName.Title : t0 <= from + TimeSpan.FromMinutes(2) ? $"{PoolName.Title}, last 6 hours"
            : $"{PoolName.Title} since {t0.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)}";

        // Row 1: the title on the left, the legend on the right (on a row of its own when the chart is too narrow for both).
        var titleSize = TextRenderer.MeasureText(g, title, _titleFont, Size.Empty, Flags);
        var titleRect = new Rectangle(pad, pad, titleSize.Width, titleSize.Height);
        TextRenderer.DrawText(g, title, _titleFont, titleRect.Location, Color.FromArgb(40, 40, 40), Flags);
        var legendRect = Rectangle.Empty;
        int headerBottom = titleRect.Bottom;
        if (pts.Count >= 2)
        {
            var last = pts[^1].Totals;
            (string text, Color color)[] items = [($"paged {Fmt.Bytes((long)last.CommittedPaged)}", PagedColor), ($"non-paged {Fmt.Bytes((long)last.CommittedNonPaged)}", NonPagedColor)];
            int swatch = fh * 3 / 2, gap = pad;
            var sizes = items.Select(i => TextRenderer.MeasureText(g, i.text, _font, Size.Empty, Flags)).ToArray();
            int legendW = sizes.Sum(s => swatch + gap / 2 + s.Width) + gap * (items.Length - 1);
            int legendH = Math.Max(fh, sizes.Max(s => s.Height));
            int x = Width - pad - legendW, y = titleRect.Top + (titleRect.Height - legendH) / 2;
            if (x < titleRect.Right + 2 * pad) { x = pad; y = titleRect.Bottom + pad / 2; }
            legendRect = new Rectangle(x, y, legendW, legendH);
            headerBottom = Math.Max(headerBottom, legendRect.Bottom);
            using var swatchPen = new Pen(Color.Black, LineWidth()) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            for (int i = 0; i < items.Length; i++)
            {
                swatchPen.Color = items[i].color;
                int mid = y + legendH / 2;
                g.DrawLine(swatchPen, x, mid, x + swatch, mid);
                x += swatch + gap / 2;
                TextRenderer.DrawText(g, items[i].text, _font, new Point(x, y + (legendH - sizes[i].Height) / 2), TextColor, Flags);
                x += sizes[i].Width + gap;
            }
        }
        if (pts.Count < 2)
        {
            TextRenderer.DrawText(g, "Collecting the first samples…", _font, new Point(pad, headerBottom + fh), Color.Gray, Flags);
            LastLayout = new ChartLayout(ClientSize, titleRect, legendRect, Rectangle.Empty, [], [], []);
            _markHits = [];
            return;
        }

        // The value axis: round steps from zero (ChartScale), labels right-aligned left of the plot.
        long max = pts.Max(p => (long)Math.Max(p.Totals.CommittedPaged, p.Totals.CommittedNonPaged));
        long step = ChartScale.Step(max);
        long top = ChartScale.Top(max, step);
        int ticks = (int)(top / step);
        var labels = Enumerable.Range(0, ticks + 1).Select(i => ChartScale.Label(i * step, step)).ToArray();
        int labelW = labels.Max(l => TextRenderer.MeasureText(g, l, _font, Size.Empty, Flags).Width);
        // The plot's top sits a full line below the header: the top label (centred on the top gridline) cannot reach it.
        var plot = Rectangle.FromLTRB(pad + labelW + pad, headerBottom + fh, Width - 2 * pad, Height - pad - fh - pad / 2);
        if (plot.Width < 40 || plot.Height < fh * 2)
        {
            LastLayout = new ChartLayout(ClientSize, titleRect, legendRect, Rectangle.Empty, [], [], []);
            _markHits = [];
            return;
        }
        double span = Math.Max(60, (t1 - t0).TotalSeconds);
        float X(DateTime t) => plot.Left + (float)((t - t0).TotalSeconds / span * plot.Width);
        float Y(double v) => plot.Bottom - (float)(v / top * plot.Height);

        var yRects = new List<Rectangle>();
        using (var grid = new Pen(GridColor))
            for (int i = 0; i <= ticks; i++)
            {
                float y = Y(i * step);
                g.DrawLine(grid, plot.Left, y, plot.Right, y);
                var size = TextRenderer.MeasureText(g, labels[i], _font, Size.Empty, Flags);
                var r = new Rectangle(plot.Left - pad - size.Width, (int)Math.Round(y - size.Height / 2f), size.Width, size.Height);
                TextRenderer.DrawText(g, labels[i], _font, r.Location, TextColor, Flags);
                yRects.Add(r);
            }

        // The time axis: local clock times at a round interval, as many as fit without touching.
        var xRects = new List<Rectangle>();
        int clockW = TextRenderer.MeasureText(g, "00:00", _font, Size.Empty, Flags).Width;
        int maxLabels = Math.Max(2, plot.Width / (clockW * 2));
        var interval = new[] { 5, 10, 15, 30, 60, 120, 180, 360 }.Select(m => TimeSpan.FromMinutes(m))
            .FirstOrDefault(iv => span / iv.TotalSeconds <= maxLabels, TimeSpan.FromHours(6));
        var local0 = t0.ToLocalTime();
        var tick = new DateTime(local0.Ticks - local0.Ticks % interval.Ticks + interval.Ticks, DateTimeKind.Local);
        using (var tickPen = new Pen(GridColor))
            for (; tick.ToUniversalTime() <= t1; tick += interval)
            {
                float x = X(tick.ToUniversalTime());
                string text = tick.ToString("HH:mm", CultureInfo.InvariantCulture);
                var size = TextRenderer.MeasureText(g, text, _font, Size.Empty, Flags);
                var r = new Rectangle((int)Math.Round(x - size.Width / 2f), plot.Bottom + pad / 2, size.Width, size.Height);
                if (r.Left < plot.Left - pad || r.Right > Width - 1 || xRects.Count > 0 && r.Left < xRects[^1].Right + pad) continue;
                g.DrawLine(tickPen, x, plot.Bottom, x, plot.Bottom + pad / 3f);
                TextRenderer.DrawText(g, text, _font, r.Location, TextColor, Flags);
                xRects.Add(r);
            }

        // Each cache clear: a faint line behind the data, and a small mark at the top of the plot, kept inside it.
        var clears = _clears.Where(c => c.TimeUtc >= t0 && c.TimeUtc <= t1).ToList();
        var marks = new List<Rectangle>();
        var hits = new List<(Rectangle, string)>();
        int markSize = Math.Max(7, fh * 2 / 3);
        using (var line = new Pen(MarkLineColor) { DashStyle = DashStyle.Dash })
            foreach (var c in clears)
            {
                float x = X(c.TimeUtc);
                g.DrawLine(line, x, plot.Top, x, plot.Bottom);
                var mark = new Rectangle(Math.Clamp((int)Math.Round(x - markSize / 2f), plot.Left, plot.Right - markSize), plot.Top, markSize, markSize);
                marks.Add(mark);
                // The mark and its line answer to the pointer; a few pixels either side, so a thin line is easy to find.
                hits.Add((Rectangle.FromLTRB(Math.Min(mark.Left, (int)x - pad / 2), plot.Top, Math.Max(mark.Right, (int)x + pad / 2), plot.Bottom), c.Text));
            }

        using (var pen = new Pen(PagedColor, LineWidth()) { LineJoin = LineJoin.Round })
            g.DrawLines(pen, pts.Select(p => new PointF(X(p.TimeUtc), Y(p.Totals.CommittedPaged))).ToArray());
        using (var pen = new Pen(NonPagedColor, LineWidth()) { LineJoin = LineJoin.Round })
            g.DrawLines(pen, pts.Select(p => new PointF(X(p.TimeUtc), Y(p.Totals.CommittedNonPaged))).ToArray());

        // The marks on top: filled once the clear is measured, an outline while it is being measured.
        using (var fill = new SolidBrush(MarkColor))
        using (var outline = new Pen(MarkColor))
            for (int i = 0; i < clears.Count; i++)
            {
                var r = marks[i];
                PointF[] down = [new(r.Left, r.Top), new(r.Right, r.Top), new(r.Left + r.Width / 2f, r.Bottom)];
                if (clears[i].Done) g.FillPolygon(fill, down);
                else g.DrawPolygon(outline, down);
            }
        _markHits = hits;
        LastLayout = new ChartLayout(ClientSize, titleRect, legendRect, plot, yRects, xRects, marks);
    }

    private float LineWidth() => Math.Max(1.5f, 2f * DeviceDpi / 96f);

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _font.Dispose(); _titleFont.Dispose(); _tip.Dispose(); }
        base.Dispose(disposing);
    }
}
