using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Globalization;
using PoolWatch.Core.Cache;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Growth;
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

    /// <summary>The hover text on the graph that ends at <paramref name="nowUtc"/>: "Cache clear yesterday at 23:00 (automatic): freed 1.2 GB" for one before today.</summary>
    public string TextOn(DateTime nowUtc) => ClearScheduleText.Mark(TimeUtc, FreedBytes, Automatic, Light, Done, nowUtc);

    /// <summary>What the status line says in place of the drop it caused: "The cache clear at 03:56 freed 1.3 GB."</summary>
    public string Result => ClearScheduleText.Result(TimeUtc, FreedBytes, Done);
}

/// <summary>
/// Where the chart drew its text, clear marks and RAM overlay last time (for the CI layout check: nothing may overlap or
/// fall outside). <paramref name="RamLabels"/> is the right axis (percent of RAM) and <paramref name="RamBox"/> the Show
/// RAM switch, when the graph has them; <paramref name="SpanBox"/> the 1 h / 6 h / 24 h choice; <paramref name="Band"/>
/// the stretch before PoolWatch started and <paramref name="BandLabel"/> its words, when the window reaches back that far;
/// <paramref name="NoRoom"/>: there were points to draw and no room for the plot (none was drawn).
/// </summary>
internal sealed record ChartLayout(Size Client, Rectangle Title, Rectangle Legend, Rectangle Plot, IReadOnlyList<Rectangle> YLabels, IReadOnlyList<Rectangle> XLabels,
    IReadOnlyList<Rectangle> Markers, IReadOnlyList<Rectangle>? RamLabels = null, Rectangle RamBox = default, Rectangle SpanBox = default,
    Rectangle Band = default, Rectangle BandLabel = default, bool NoRoom = false)
{
    /// <summary>Overlapping or clipped text, clear marks outside the plot, and a graph with no room for its plot, in words; empty when the layout is clean.</summary>
    public List<string> Problems()
    {
        var p = new List<string>();
        if (NoRoom) p.Add($"the graph has no room for its plot ({Client})");
        var client = new Rectangle(Point.Empty, Client);
        void Inside(string what, Rectangle r) { if (!r.IsEmpty && !client.Contains(r)) p.Add($"{what} {r} is outside the chart {Client}"); }
        Inside("title", Title);
        Inside("legend", Legend);
        Inside("Show RAM", RamBox);
        Inside("the time window choice", SpanBox);
        foreach (var r in YLabels) Inside("axis label", r);
        foreach (var r in XLabels) Inside("time label", r);
        foreach (var r in RamLabels ?? []) Inside("RAM axis label", r);
        foreach (var r in Markers)
            if (!Plot.Contains(r)) p.Add($"clear mark {r} is outside the plot {Plot}");
        if (!Legend.IsEmpty && Title.IntersectsWith(Legend)) p.Add("the legend overlaps the title");
        if (!RamBox.IsEmpty && (RamBox.IntersectsWith(Title) || RamBox.IntersectsWith(Legend))) p.Add("Show RAM overlaps the title or legend");
        if (!SpanBox.IsEmpty && (SpanBox.IntersectsWith(Title) || SpanBox.IntersectsWith(Legend) || SpanBox.IntersectsWith(RamBox)))
            p.Add("the time window choice overlaps the title, legend or Show RAM");
        if (!BandLabel.IsEmpty && (!Band.Contains(BandLabel) || !Plot.Contains(BandLabel))) p.Add($"the band's words {BandLabel} are outside the band {Band}");
        foreach (var r in YLabels)
            if (r.IntersectsWith(Title) || (!Legend.IsEmpty && r.IntersectsWith(Legend))) p.Add($"axis label {r} overlaps the title or legend");
        foreach (var r in RamLabels ?? [])
            if (r.IntersectsWith(Title) || (!Legend.IsEmpty && r.IntersectsWith(Legend)) || (!RamBox.IsEmpty && r.IntersectsWith(RamBox)) || Plot.IntersectsWith(r))
                p.Add($"RAM axis label {r} overlaps the title, legend, Show RAM or the plot");
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
/// The span the graph's time axis covers: always the whole window chosen, ending at the newest sample, and the stretch at
/// its start before PoolWatch started (<see cref="BandEndUtc"/>), drawn as an empty, labelled band. Pure.
/// </summary>
internal readonly record struct ChartSpan(DateTime FromUtc, DateTime ToUtc, DateTime? BandEndUtc)
{
    /// <summary>A start this close to the window's start is no band: the edges of the samples' minutes.</summary>
    public static readonly TimeSpan BandFrom = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The axis for <paramref name="span"/> ending at <paramref name="newestUtc"/>; a band up to <paramref name="startedUtc"/>
    /// when PoolWatch started inside the window (more than <see cref="BandFrom"/> after its start), cut at its end.
    /// </summary>
    public static ChartSpan Of(DateTime newestUtc, TimeSpan span, DateTime? startedUtc)
    {
        var from = newestUtc - span;
        DateTime? band = startedUtc is { } s && s > from + BandFrom ? (s < newestUtc ? s : newestUtc) : null;
        return new ChartSpan(from, newestUtc, band);
    }

    /// <summary>Where <paramref name="utc"/> falls across a plot <paramref name="width"/> pixels wide, from its left edge.</summary>
    public float X(DateTime utc, float width) => (float)((utc - FromUtc).TotalSeconds / Math.Max(1, (ToUtc - FromUtc).TotalSeconds) * width);

    /// <summary>The band's words: "Before PoolWatch started (00:53)", "(yesterday 23:10)" for a start before today.</summary>
    public static string BandLabel(DateTime startedUtc, DateTime nowUtc) => $"Before PoolWatch started ({PlainWords.When(startedUtc, nowUtc)})";

    /// <summary>The band's shorter words, where the long ones do not fit.</summary>
    public const string ShortBandLabel = "Before PoolWatch started";
}

/// <summary>
/// The main window's one graph: the committed paged and non-paged pool (the figures Task Manager shows) over
/// <see cref="Span"/> (the last 6 hours in the Simple view; in the Advanced view the hour, 6 hours or 24 hours of its
/// 1 h / 6 h / 24 h choice, <see cref="SpanOption"/>), solid lines with a plain legend, broken where PoolWatch was not
/// sampling (the PC asleep), and a mark at each cache clear that shows on hover what it freed, so a drop a clear caused
/// reads as the clear. Its time axis always spans the whole window: the stretch before PoolWatch started is an empty,
/// labelled band (<see cref="StartedUtc"/>). In the Advanced view it has a Show RAM switch (<see cref="RamOption"/>): RAM
/// in use, the cache (standby) and free as muted stacked columns behind the lines, a column a minute, 5 minutes or a
/// quarter hour as the span is, on a right axis in percent of RAM, with GB on hover. Every position comes from the font's
/// measured size, so no text reaches another at any display scaling. Paints only when invalidated; the window feeds it
/// only while visible.
/// </summary>
internal sealed class PoolChart : Control
{
    /// <summary>The Simple view's span (and the Advanced view's until its picker says otherwise).</summary>
    public static readonly TimeSpan SimpleSpan = TimeSpan.FromHours(6);
    /// <summary>Two points further apart than this are a gap in the samples: the lines break there.</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromMinutes(10);
    private static readonly Color PagedColor = UiColors.Accent, NonPagedColor = UiColors.Medium;
    private static readonly Color GridColor = Color.FromArgb(232, 232, 232), TextColor = Color.FromArgb(90, 90, 90);
    private static readonly Color MarkColor = UiColors.Good, MarkLineColor = Color.FromArgb(178, 214, 188);
    private static readonly Color BandColor = UiColors.Line;
    // The RAM overlay's muted colours (the approved mockup's), its grid over the columns, and its axis and hover outline.
    internal static readonly Color RamInUse = Color.FromArgb(221, 212, 234), RamCache = Color.FromArgb(235, 230, 243), RamFree = Color.FromArgb(246, 244, 250);
    private static readonly Color RamEdge = Color.FromArgb(196, 185, 213), RamAxis = Color.FromArgb(133, 122, 156), RamHover = Color.FromArgb(138, 127, 163);
    private static readonly Color RamGrid = Color.FromArgb(23, 0, 0, 0);
    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
    private const string RamBoxText = "Show RAM";
    private static readonly string[] Percents = ["0%", "25%", "50%", "75%", "100%"];
    private IReadOnlyList<ChartPoint> _points = [];
    private IReadOnlyList<ChartClear> _clears = [];
    private IReadOnlyList<RamColumn> _ram = [];
    private long _ramTotal;
    private TimeSpan _span = SimpleSpan;
    private readonly Font _font = new("Segoe UI", 9f);
    private readonly Font _titleFont = new("Segoe UI Semibold", 10f);
    // On the graph's own white, in the title's dark grey: never the system's text colour, which high contrast makes white.
    private readonly CheckBox _ramBox = new() { Text = RamBoxText, AutoSize = true, Visible = false, BackColor = Color.White, ForeColor = Color.FromArgb(40, 40, 40), UseVisualStyleBackColor = true };
    /// <summary>The 1 h / 6 h / 24 h choice (the Advanced view), at the top right.</summary>
    private readonly SpanChoice _spanBox = new() { Visible = false };

    // One tooltip: over a clear mark (or its line) what the clear freed, over a RAM column its GB, elsewhere HelpText.
    private readonly ToolTip _tip = new() { AutoPopDelay = 30_000, InitialDelay = 400 };
    private List<(Rectangle Hit, string Text)> _markHits = [];
    private List<(Rectangle Hit, string Text)> _ramHits = [];
    private Rectangle _hover = Rectangle.Empty;
    /// <summary>Where the pointer is over the graph, or null: the outlined column is found again at each paint.</summary>
    private Point? _mouse;
    private string _helpText = "";
    private string? _tipText;

    public PoolChart()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        BackColor = Color.White;
        _ramBox.Font = _font;
        _ramBox.CheckedChanged += (_, _) =>
        {
            if (_ramBox.Checked == ShowRam) return; // set by the window itself
            ShowRam = _ramBox.Checked;
            ShowRamChanged?.Invoke(ShowRam);
        };
        Controls.Add(_ramBox);
        _tip.SetToolTip(_ramBox, RamBoxTip(0));
        _spanBox.Font = _font;
        _spanBox.Picked += w => SpanPicked?.Invoke(w);
        Controls.Add(_spanBox);
    }

    /// <summary>The owner picked another window with the 1 h / 6 h / 24 h choice (the window sets <see cref="Span"/>).</summary>
    public event Action<StatsWindow>? SpanPicked;

    /// <summary>The graph offers the 1 h / 6 h / 24 h choice (the Advanced view).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool SpanOption
    {
        get => _spanOption;
        set { if (_spanOption == value) return; _spanOption = value; _spanBox.Visible = value; PlaceControls(); Invalidate(); }
    }
    private bool _spanOption;

    /// <summary>The 1 h / 6 h / 24 h choice (tests, the window).</summary>
    internal SpanChoice SpanBox => _spanBox;

    /// <summary>When PoolWatch started (its first sample): the time before it, inside the window, is a labelled band. Null: the first point says it.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public DateTime? StartedUtc
    {
        get => _startedUtc;
        set { if (_startedUtc == value) return; _startedUtc = value; Invalidate(); }
    }
    private DateTime? _startedUtc;

    /// <summary>The owner turned Show RAM on or off (the window keeps it in the settings and feeds the columns).</summary>
    public event Action<bool>? ShowRamChanged;

    /// <summary>The span the graph shows, ending at its newest point.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public TimeSpan Span
    {
        get => _span;
        set { if (_span == value) return; _span = value; Invalidate(); }
    }

    /// <summary>The graph offers Show RAM (the Advanced view); without it the overlay is off.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool RamOption
    {
        get => _ramOption;
        set { if (_ramOption == value) return; _ramOption = value; _ramBox.Visible = value; PlaceControls(); Invalidate(); }
    }
    private bool _ramOption;

    /// <summary>The RAM overlay is on (shown only with <see cref="RamOption"/>).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowRam
    {
        get => _showRam;
        set
        {
            if (_showRam == value) return;
            _showRam = value;
            _ramBox.Checked = value;
            Invalidate();
        }
    }
    private bool _showRam;

    /// <summary>The RAM overlay is drawn: offered and on.</summary>
    private bool RamShown => RamOption && ShowRam;

    /// <summary>The tooltip shown anywhere but over a clear mark or a RAM column.</summary>
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

    /// <summary>The RAM columns (<see cref="RamHistory.Columns"/>) and the PC's RAM. Repaints only when they changed.</summary>
    public void SetRam(IReadOnlyList<RamColumn> columns, long totalBytes)
    {
        if (totalBytes == _ramTotal && columns.SequenceEqual(_ram)) return;
        if (totalBytes != _ramTotal) _tip.SetToolTip(_ramBox, RamBoxTip(totalBytes));
        _ram = columns;
        _ramTotal = totalBytes;
        if (RamShown) Invalidate();
    }

    /// <summary>Show RAM's tooltip: "RAM in use, cache (standby) and free, as a share of the PC's 127 GB (right axis)."</summary>
    internal static string RamBoxTip(long totalBytes) =>
        $"RAM in use, cache (standby) and free, as a share of the PC's {(totalBytes > 0 ? WholeGb(totalBytes) : "RAM")} (right axis).";

    /// <summary>A RAM column's hover text: "13:25: in use 41.2 GB, cache 71.5 GB, free 14.3 GB (of 127 GB RAM)", "Yesterday 23:15: ..." before today.</summary>
    internal static string RamTip(RamColumn c, long totalBytes, DateTime nowUtc)
    {
        var at = c.FromUtc.ToLocalTime();
        string day = at.Date < nowUtc.ToLocalTime().Date ? "Yesterday " : "";
        return $"{day}{at.ToString("HH:mm", CultureInfo.InvariantCulture)}: in use {Gb(c.InUse)}, cache {Gb(c.Cache)}, free {Gb(c.Free)} (of {WholeGb(totalBytes)} RAM)";
    }

    private static string Gb(long bytes) => FormattableString.Invariant($"{bytes / (double)(1L << 30):0.0} GB");
    private static string WholeGb(long bytes) => FormattableString.Invariant($"{Math.Max(1, Math.Round(bytes / (double)(1L << 30))):0} GB");

    /// <summary>
    /// The graph's title for its span: "Kernel pool, last 6 hours", whether or not PoolWatch has run that long (the band
    /// before it started says when it did); "Kernel pool" before there are two points to draw.
    /// </summary>
    internal static string TitleFor(TimeSpan span, int points)
    {
        if (points < 2) return PoolName.Title;
        string last = span == TimeSpan.FromHours(1) ? "last hour"
            : span.TotalHours == Math.Floor(span.TotalHours) ? FormattableString.Invariant($"last {span.TotalHours:0} hours") : $"last {Fmt.Duration(span)}";
        return $"{PoolName.Title}, {last}";
    }

    /// <summary>The tooltip at a point of the last paint: every clear marked there (marks close in time overlap), else the RAM column there, else <see cref="HelpText"/>.</summary>
    internal string TipAt(Point p)
    {
        var over = _markHits.Where(m => m.Hit.Contains(p)).Select(m => m.Text).ToList();
        if (over.Count > 0) return string.Join(Environment.NewLine, over);
        return RamHitAt(p) is { } ram ? ram.Text : _helpText;
    }

    private (Rectangle Hit, string Text)? RamHitAt(Point p)
    {
        if (!RamShown || _markHits.Any(m => m.Hit.Contains(p))) return null;
        foreach (var h in _ramHits) if (h.Hit.Contains(p)) return h;
        return null;
    }

    /// <summary>The tooltip shown now (tests).</summary>
    internal string? ShownTip => _tipText;

    /// <summary>The RAM column the pointer is over, outlined (empty when none).</summary>
    internal Rectangle HoverColumn => _hover;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = e.Location;
        ShowTip(TipAt(e.Location));
        SetHover(RamHitAt(e.Location)?.Hit ?? Rectangle.Empty);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _mouse = null;
        ShowTip(_helpText);
        SetHover(Rectangle.Empty);
    }

    private void SetHover(Rectangle column)
    {
        if (column == _hover) return;
        if (!_hover.IsEmpty) Invalidate(Rectangle.Inflate(_hover, 2, 2));
        _hover = column;
        if (!column.IsEmpty) Invalidate(Rectangle.Inflate(column, 2, 2));
    }

    private void ShowTip(string text)
    {
        if (text == _tipText) return;
        _tipText = text;
        _tip.SetToolTip(this, text);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        PlaceControls();
    }

    /// <summary>The 1 h / 6 h / 24 h choice at the top right, on the title's row, and Show RAM left of it.</summary>
    private void PlaceControls()
    {
        var (span, ram) = ControlBounds();
        if (_spanOption && _spanBox.Bounds != span) _spanBox.Bounds = span;
        if (_ramOption && _ramBox.Location != ram.Location) _ramBox.Location = ram.Location;
    }

    /// <summary>Where the 1 h / 6 h / 24 h choice and Show RAM go at the graph's width (empty for one it does not offer).</summary>
    private (Rectangle Span, Rectangle Ram) ControlBounds()
    {
        int pad = Math.Max(4, _font.Height / 2);
        int titleH = TextRenderer.MeasureText("Kernel pool", _titleFont, Size.Empty, Flags).Height;
        int right = Width - pad;
        Rectangle span = Rectangle.Empty, ram = Rectangle.Empty;
        if (_spanOption)
        {
            var size = _spanBox.GetPreferredSize(Size.Empty);
            span = new Rectangle(Math.Max(pad, right - size.Width), pad + (titleH - size.Height) / 2, size.Width, size.Height);
            right = span.Left - pad * 2;
        }
        if (_ramOption)
        {
            var size = _ramBox.GetPreferredSize(Size.Empty);
            ram = new Rectangle(Math.Max(pad, right - size.Width), pad + (titleH - size.Height) / 2, size.Width, size.Height);
        }
        return (span, ram);
    }

    /// <summary>The plot's least height, in lines of the graph's text (<see cref="LineHeight"/>): the main window gives the graph's row room for it.</summary>
    public const int MinPlotLines = 3;

    /// <summary>The height of a line of the graph's text (its labels and legend).</summary>
    internal int LineHeight => _font.Height;

    /// <summary>
    /// The least height at which the graph draws its plot <see cref="MinPlotLines"/> lines high, at its width now and for what
    /// it shows now: its title, the 1 h / 6 h / 24 h choice and Show RAM, the legend (on rows of its own on a narrow graph,
    /// more of them with the RAM's three), the line between them and the plot, and the time axis under it.
    /// </summary>
    internal int NeededHeight
    {
        get
        {
            int fh = _font.Height, pad = Math.Max(4, fh / 2);
            DateTime t1 = _points.Count > 0 ? _points[^1].TimeUtc : DateTime.UtcNow, t0 = t1 - _span;
            int points = _points.Count(p => p.TimeUtc >= t0);
            var titleSize = TextRenderer.MeasureText(TitleFor(_span, points), _titleFont, Size.Empty, Flags);
            var title = new Rectangle(pad, pad, titleSize.Width, titleSize.Height);
            var (span, ram) = ControlBounds();
            var controls = ram.IsEmpty ? span : span.IsEmpty ? ram : Rectangle.Union(ram, span);
            int headerBottom = Math.Max(title.Bottom, controls.Bottom);
            if (points >= 2)
            {
                var (rows, h, _) = LegendRows(LegendItems(_clears.Any(c => c.TimeUtc >= t0 && c.TimeUtc <= t1), RamShown), title, controls,
                    pad, fh, text => TextRenderer.MeasureText(text, _font, Size.Empty, Flags));
                if (rows.Count > 0) headerBottom = Math.Max(headerBottom, rows.Max(r => r.Y) + h);
            }
            return headerBottom + fh + MinPlotLines * fh + pad + fh + pad / 2;
        }
    }

    /// <summary>The title the last paint drew (tests, CI).</summary>
    internal string LastTitle { get; private set; } = "";

    /// <summary>The text positions of the last paint (null before the first).</summary>
    internal ChartLayout? LastLayout { get; private set; }

    /// <summary>How many points the chart holds (CI).</summary>
    internal int PointCount => _points.Count;

    /// <summary>The RAM columns drawn at the last paint (CI, tests).</summary>
    internal int RamColumnsDrawn { get; private set; }

    /// <summary>The points drawn as a dot at the last paint, each alone between two gaps (tests).</summary>
    internal int DotsDrawn { get; private set; }

    /// <summary>Show RAM, for the window's tests and the CI layout check.</summary>
    internal CheckBox RamBox => _ramBox;

    public void SetData(IReadOnlyList<ChartPoint> points)
    {
        // Repaint only on new data.
        if (_points.Count == points.Count && points.Count > 0 && _points[^1] == points[^1] && _points[0] == points[0]) return;
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
        bool ram = RamShown;

        // The points in range, on an axis that always spans the whole window (ChartSpan): the time before PoolWatch
        // started is a band of its own, never a stretched first hour.
        var pts = _points;
        DateTime t1 = pts.Count > 0 ? pts[^1].TimeUtc : DateTime.UtcNow;
        DateTime from = t1 - _span;
        if (pts.Count > 0 && pts[0].TimeUtc < from) pts = pts.Where(p => p.TimeUtc >= from).ToList();
        var axis = ChartSpan.Of(t1, _span, _startedUtc ?? (pts.Count > 0 ? pts[0].TimeUtc : null));
        DateTime t0 = axis.FromUtc;
        string title = TitleFor(_span, pts.Count);
        LastTitle = title;

        // Row 1: the title on the left, Show RAM on the right (the Advanced view), the legend between them; on rows of their
        // own when the chart is too narrow for that.
        var titleSize = TextRenderer.MeasureText(g, title, _titleFont, Size.Empty, Flags);
        var titleRect = new Rectangle(pad, pad, titleSize.Width, titleSize.Height);
        TextRenderer.DrawText(g, title, _titleFont, titleRect.Location, Color.FromArgb(40, 40, 40), Flags);
        var boxRect = _ramOption ? _ramBox.Bounds : Rectangle.Empty;
        var spanRect = _spanOption ? _spanBox.Bounds : Rectangle.Empty;
        var controls = boxRect.IsEmpty ? spanRect : spanRect.IsEmpty ? boxRect : Rectangle.Union(boxRect, spanRect);
        var legendRect = Rectangle.Empty;
        int headerBottom = Math.Max(titleRect.Bottom, controls.Bottom);
        bool clearsShown = _clears.Any(c => c.TimeUtc >= t0 && c.TimeUtc <= t1);
        if (pts.Count >= 2) legendRect = DrawLegend(g, LegendItems(clearsShown, ram), titleRect, controls, pad, fh, ref headerBottom);
        if (pts.Count < 2)
        {
            TextRenderer.DrawText(g, "Collecting the first samples…", _font, new Point(pad, headerBottom + fh), Color.Gray, Flags);
            LastLayout = new ChartLayout(ClientSize, titleRect, legendRect, Rectangle.Empty, [], [], [], [], boxRect, spanRect);
            _markHits = [];
            _ramHits = [];
            return;
        }

        // The value axis: round steps from zero (ChartScale), labels right-aligned left of the plot; with RAM, its percent
        // axis on the right.
        long max = pts.Max(p => (long)Math.Max(p.Totals.CommittedPaged, p.Totals.CommittedNonPaged));
        long step = ChartScale.Step(max);
        long top = ChartScale.Top(max, step);
        int ticks = (int)(top / step);
        var labels = Enumerable.Range(0, ticks + 1).Select(i => ChartScale.Label(i * step, step)).ToArray();
        int labelW = labels.Max(l => TextRenderer.MeasureText(g, l, _font, Size.Empty, Flags).Width);
        int ramW = ram ? Percents.Max(l => TextRenderer.MeasureText(g, l, _font, Size.Empty, Flags).Width) + pad : 0;
        // The plot's top sits a full line below the header: the top label (centred on the top gridline) cannot reach it.
        var plot = Rectangle.FromLTRB(pad + labelW + pad, headerBottom + fh, Width - 2 * pad - ramW, Height - pad - fh - pad / 2);
        if (plot.Width < 40 || plot.Height < fh * 2)
        {
            LastLayout = new ChartLayout(ClientSize, titleRect, legendRect, Rectangle.Empty, [], [], [], [], boxRect, spanRect, NoRoom: true);
            _markHits = [];
            _ramHits = [];
            return;
        }
        double span = Math.Max(60, (t1 - t0).TotalSeconds);
        float X(DateTime t) => plot.Left + axis.X(t, plot.Width);
        float Y(double v) => plot.Bottom - (float)(v / top * plot.Height);

        // Before PoolWatch started: an empty, hatched band with its words, behind everything else.
        var bandRect = Rectangle.Empty;
        var bandLabel = Rectangle.Empty;
        BandWords = null;
        if (axis.BandEndUtc is { } bandEnd)
        {
            bandRect = Rectangle.FromLTRB(plot.Left, plot.Top, Math.Max(plot.Left + 1, (int)Math.Round(X(bandEnd))), plot.Bottom);
            using (var hatch = new HatchBrush(HatchStyle.WideUpwardDiagonal, SystemInformation.HighContrast ? SystemColors.GrayText : BandColor, BackColor))
                g.FillRectangle(hatch, bandRect);
            foreach (var words in new[] { ChartSpan.BandLabel(bandEnd, t1), ChartSpan.ShortBandLabel })
            {
                var size = TextRenderer.MeasureText(g, words, _font, Size.Empty, Flags);
                if (size.Width + 2 * pad > bandRect.Width || size.Height + pad > bandRect.Height) continue;
                bandLabel = new Rectangle(bandRect.Left + (bandRect.Width - size.Width) / 2, bandRect.Top + (bandRect.Height - size.Height) / 2, size.Width, size.Height);
                using (var back = new SolidBrush(BackColor)) g.FillRectangle(back, Rectangle.Inflate(bandLabel, pad / 2, 1));
                TextRenderer.DrawText(g, words, _font, bandLabel.Location, TextColor, Flags);
                BandWords = words;
                break;
            }
        }

        // The RAM columns behind everything, stacked from the bottom: in use, the cache, then free up to 100%.
        var ramHits = new List<(Rectangle, string)>();
        var ramRects = new List<Rectangle>();
        RamColumnsDrawn = 0;
        if (ram && _ramTotal > 0)
        {
            using var inUse = new SolidBrush(RamInUse);
            using var cache = new SolidBrush(RamCache);
            using var free = new SolidBrush(RamFree);
            foreach (var c in _ram)
            {
                var a = c.FromUtc < t0 ? t0 : c.FromUtc;
                var b = c.ToUtc > t1 ? t1 : c.ToUtc;
                if (b <= a) continue;
                float x0 = X(a) + 0.5f, x1 = X(b) - 0.5f;
                if (x1 - x0 < 0.5f) x1 = x0 + 0.5f;
                float yU = plot.Bottom - (float)(c.InUse / (double)_ramTotal * plot.Height);
                float yC = yU - (float)(c.Cache / (double)_ramTotal * plot.Height);
                g.FillRectangle(inUse, x0, yU, x1 - x0, plot.Bottom - yU);
                g.FillRectangle(cache, x0, yC, x1 - x0, yU - yC);
                g.FillRectangle(free, x0, plot.Top, x1 - x0, Math.Max(0, yC - plot.Top));
                var hit = Rectangle.FromLTRB((int)Math.Floor(x0), plot.Top, Math.Max((int)Math.Floor(x0) + 1, (int)Math.Ceiling(x1)), plot.Bottom);
                ramHits.Add((hit, RamTip(c, _ramTotal, t1)));
                RamColumnsDrawn++;
            }
            for (int i = 0; i < Percents.Length; i++)
            {
                var size = TextRenderer.MeasureText(g, Percents[i], _font, Size.Empty, Flags);
                float y = plot.Bottom - i / 4f * plot.Height;
                var r = new Rectangle(plot.Right + pad, (int)Math.Round(y - size.Height / 2f), size.Width, size.Height);
                TextRenderer.DrawText(g, Percents[i], _font, r.Location, RamAxis, Flags);
                ramRects.Add(r);
            }
        }

        var yRects = new List<Rectangle>();
        using (var grid = new Pen(ram ? RamGrid : GridColor))
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
        using (var tickPen = new Pen(ram ? RamGrid : GridColor))
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
                hits.Add((Rectangle.FromLTRB(Math.Min(mark.Left, (int)x - pad / 2), plot.Top, Math.Max(mark.Right, (int)x + pad / 2), plot.Bottom), c.TextOn(t1)));
            }

        // The lines, broken where two points are a gap apart (the PC asleep, PoolWatch not running).
        int dots = 0;
        using (var pen = new Pen(PagedColor, LineWidth()) { LineJoin = LineJoin.Round })
            dots += DrawSegments(g, pen, pts, p => new PointF(X(p.TimeUtc), Y(p.Totals.CommittedPaged)));
        using (var pen = new Pen(NonPagedColor, LineWidth()) { LineJoin = LineJoin.Round })
            dots += DrawSegments(g, pen, pts, p => new PointF(X(p.TimeUtc), Y(p.Totals.CommittedNonPaged)));
        DotsDrawn = dots;

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
        _ramHits = ramHits;
        // The outlined column is the one under the pointer now: a new sample moves the columns under a still pointer.
        _hover = _mouse is { } at ? RamHitAt(at)?.Hit ?? Rectangle.Empty : Rectangle.Empty;
        // The tooltip follows too, so it says the GB of the column outlined, not of the one that was there before.
        if (_mouse is { } pointer) ShowTip(TipAt(pointer));
        if (!_hover.IsEmpty)
            using (var outline = new Pen(RamHover))
                g.DrawRectangle(outline, _hover.Left, _hover.Top, Math.Max(1, _hover.Width - 1), _hover.Height - 1);
        LastLayout = new ChartLayout(ClientSize, titleRect, legendRect, plot, yRects, xRects, marks, ramRects, boxRect, spanRect, bandRect, bandLabel);
    }

    /// <summary>The band's words the last paint drew, or null (tests).</summary>
    internal string? BandWords { get; private set; }

    /// <summary>How a legend item is marked: a line (a pool series), a square (a RAM column), the clear's mark.</summary>
    private enum LegendMark { Line, Square, Clear }

    /// <summary>The legend's items: the pool's two lines (the names only: the header says how much each holds now), the clear's mark when one is in the window, and the RAM's three.</summary>
    private static List<(string text, Color color, LegendMark mark)> LegendItems(bool clearsShown, bool ram)
    {
        var items = new List<(string text, Color color, LegendMark mark)> { ("paged", PagedColor, LegendMark.Line), ("non-paged", NonPagedColor, LegendMark.Line) };
        if (clearsShown) items.Add(("cache clear", MarkColor, LegendMark.Clear));
        if (ram) items.AddRange([("in use", RamInUse, LegendMark.Square), ("cache (standby)", RamCache, LegendMark.Square), ("free", RamFree, LegendMark.Square)]);
        return items;
    }

    /// <summary>
    /// Where the legend's rows go: on the title's row between the title and Show RAM when it fits there, else on a row of
    /// its own, and the RAM's three on a row of their own after the pool's two when even that is too narrow. The painting
    /// and <see cref="NeededHeight"/> place it the same way; <paramref name="measure"/> sizes a text.
    /// </summary>
    private (List<(int From, int To, int X, int Y)> Rows, int H, Size[] Sizes) LegendRows(List<(string text, Color color, LegendMark mark)> items, Rectangle title,
        Rectangle box, int pad, int fh, Func<string, Size> measure)
    {
        var sizes = items.Select(i => measure(i.text)).ToArray();
        int pool = items.Count(i => i.mark != LegendMark.Square);
        int h = Math.Max(fh, sizes.Max(s => s.Height));
        int right = box.IsEmpty ? Width - pad : box.Left - pad;
        var rows = new List<(int From, int To, int X, int Y)>();
        int all = LegendRowWidth(items, sizes, 0, items.Count, pad, fh);
        if (right - all >= title.Right + 2 * pad) rows.Add((0, items.Count, right - all, title.Top + (title.Height - h) / 2));
        else
        {
            int y = Math.Max(title.Bottom, box.Bottom) + pad / 2;
            if (all <= Width - 2 * pad || items.Count <= pool) rows.Add((0, items.Count, pad, y));
            else
            {
                rows.Add((0, pool, pad, y));
                rows.Add((pool, items.Count, pad, y + h + pad / 2));
            }
        }
        return (rows, h, sizes);
    }

    /// <summary>A legend item's mark: a line's swatch is wider than a square or the clear's mark.</summary>
    private static int LegendMarkWidth(LegendMark mark, int fh) => mark == LegendMark.Line ? fh * 3 / 2 : Math.Max(8, fh * 2 / 3);

    /// <summary>The width of the legend's items <paramref name="from"/> to <paramref name="to"/> on one row.</summary>
    private static int LegendRowWidth(List<(string text, Color color, LegendMark mark)> items, Size[] sizes, int from, int to, int pad, int fh)
    {
        int w = 0;
        for (int i = from; i < to; i++) w += LegendMarkWidth(items[i].mark, fh) + pad / 2 + sizes[i].Width + (i < to - 1 ? pad : 0);
        return w;
    }

    /// <summary>The legend, where <see cref="LegendRows"/> puts it.</summary>
    private Rectangle DrawLegend(Graphics g, List<(string text, Color color, LegendMark mark)> items, Rectangle title, Rectangle box, int pad, int fh, ref int headerBottom)
    {
        int swatch = LegendMarkWidth(LegendMark.Line, fh), square = LegendMarkWidth(LegendMark.Square, fh), gap = pad;
        var (rows, h, sizes) = LegendRows(items, title, box, pad, fh, text => TextRenderer.MeasureText(g, text, _font, Size.Empty, Flags));
        int Mark(int i) => LegendMarkWidth(items[i].mark, fh);
        int RowWidth(int from, int to) => LegendRowWidth(items, sizes, from, to, pad, fh);
        var bounds = Rectangle.Empty;
        using var swatchPen = new Pen(Color.Black, LineWidth()) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var edge = new Pen(RamEdge);
        foreach (var (from, to, x0, y) in rows)
        {
            int x = x0;
            for (int i = from; i < to; i++)
            {
                int mid = y + h / 2;
                switch (items[i].mark)
                {
                    case LegendMark.Square:
                        using (var fill = new SolidBrush(items[i].color)) g.FillRectangle(fill, x, mid - square / 2, square, square);
                        g.DrawRectangle(edge, x, mid - square / 2, square - 1, square - 1);
                        break;
                    case LegendMark.Clear:
                        // As on the graph: a small mark pointing down.
                        using (var fill = new SolidBrush(items[i].color))
                            g.FillPolygon(fill, [new PointF(x, mid - square / 2f), new PointF(x + square, mid - square / 2f), new PointF(x + square / 2f, mid + square / 2f)]);
                        break;
                    default:
                        swatchPen.Color = items[i].color;
                        g.DrawLine(swatchPen, x, mid, x + swatch, mid);
                        break;
                }
                x += Mark(i) + gap / 2;
                TextRenderer.DrawText(g, items[i].text, _font, new Point(x, y + (h - sizes[i].Height) / 2), TextColor, Flags);
                x += sizes[i].Width + gap;
            }
            var row = new Rectangle(x0, y, RowWidth(from, to), h);
            bounds = bounds.IsEmpty ? row : Rectangle.Union(bounds, row);
        }
        headerBottom = Math.Max(headerBottom, bounds.Bottom);
        return bounds;
    }

    /// <summary>
    /// A series as lines between its points, broken at a gap (<see cref="Gap"/>); a point alone between two gaps is a dot
    /// the line's width. Returns how many dots it drew.
    /// </summary>
    private static int DrawSegments(Graphics g, Pen pen, IReadOnlyList<ChartPoint> pts, Func<ChartPoint, PointF> at)
    {
        int dots = 0;
        foreach (var (from, count) in Segments(pts))
            if (count >= 2) g.DrawLines(pen, Enumerable.Range(from, count).Select(k => at(pts[k])).ToArray());
            else
            {
                var p = at(pts[from]);
                float d = Math.Max(2f, pen.Width * 1.5f);
                using var brush = new SolidBrush(pen.Color);
                g.FillEllipse(brush, p.X - d / 2, p.Y - d / 2, d, d);
                dots++;
            }
        return dots;
    }

    /// <summary>The runs of points with no gap between two of them (<see cref="Gap"/>): each is drawn as one line.</summary>
    internal static List<(int From, int Count)> Segments(IReadOnlyList<ChartPoint> pts)
    {
        var runs = new List<(int, int)>();
        int start = 0;
        for (int i = 1; i <= pts.Count; i++)
        {
            if (i < pts.Count && pts[i].TimeUtc - pts[i - 1].TimeUtc <= Gap) continue;
            runs.Add((start, i - start));
            start = i;
        }
        return runs;
    }

    private float LineWidth() => Math.Max(1.5f, 2f * DeviceDpi / 96f);

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _font.Dispose(); _titleFont.Dispose(); _tip.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>
/// The graph's 1 h / 6 h / 24 h choice (the Advanced view): three segments in one outline, the picked one filled. A click
/// or the arrow keys pick another (<see cref="Picked"/>); setting <see cref="SelectedIndex"/> from code raises nothing.
/// It draws itself; a screen reader reads its name and the window picked.
/// </summary>
internal sealed class SpanChoice : Control
{
    /// <summary>The segments, in the order of <see cref="StatsWindow.All"/>.</summary>
    public static readonly IReadOnlyList<string> Labels = ["1 h", "6 h", "24 h"];
    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
    private int _selected = StatsWindow.All.ToList().IndexOf(StatsWindow.Default);
    private Font? _bold;

    public SpanChoice()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.Selectable, true);
        TabStop = true;
        BackColor = Color.White;
        AccessibleRole = AccessibleRole.ComboBox;
        AccessibleName = GrowthText.PickerName;
        AccessibleDescription = "Use the left and right arrow keys to pick another window.";
    }

    /// <summary>The owner picked a window (a click or a key).</summary>
    public event Action<StatsWindow>? Picked;

    public StatsWindow Window => StatsWindow.All[_selected];

    /// <summary>The picked segment (0 the hour, 1 six hours, 2 a day); set from code without raising <see cref="Picked"/>.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selected;
        set
        {
            int i = Math.Clamp(value, 0, Labels.Count - 1);
            if (i == _selected) return;
            _selected = i;
            if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
            Invalidate();
        }
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new ChoiceAccessible(this);

    private sealed class ChoiceAccessible(SpanChoice owner) : ControlAccessibleObject(owner)
    {
        public override string? Value { get => StatsWindow.All[owner._selected].Name; set { } }
    }

    private void Pick(int index)
    {
        int i = Math.Clamp(index, 0, Labels.Count - 1);
        if (i == _selected) return;
        SelectedIndex = i;
        Picked?.Invoke(Window);
    }

    private int S(int logical) => LogicalToDeviceUnits(logical);

    private int SegmentWidth => Labels.Max(l => TextRenderer.MeasureText(l, BoldFont, Size.Empty, Flags).Width) + S(22);

    private Font BoldFont => _bold ??= new Font(Font, FontStyle.Bold);

    public override Size GetPreferredSize(Size proposedSize) =>
        new(SegmentWidth * Labels.Count + 1, Math.Max(BoldFont.Height, Font.Height) + S(8));

    protected override void OnFontChanged(EventArgs e)
    {
        _bold?.Dispose();
        _bold = null;
        base.OnFontChanged(e);
    }

    /// <summary>The segment at a point, or -1.</summary>
    internal int SegmentAt(Point p) => p.X < 0 || p.Y < 0 || p.Y >= Height ? -1 : Math.Min(Labels.Count - 1, p.X / Math.Max(1, SegmentWidth));

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();
        if (SegmentAt(e.Location) is var i and >= 0) Pick(i);
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Left: Pick(_selected - 1); e.Handled = true; break;
            case Keys.Right: Pick(_selected + 1); e.Handled = true; break;
            case Keys.Home: Pick(0); e.Handled = true; break;
            case Keys.End: Pick(Labels.Count - 1); e.Handled = true; break;
        }
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        bool hc = SystemInformation.HighContrast;
        Color edge = hc ? SystemColors.WindowText : UiColors.Border;
        Color fill = hc ? SystemColors.Highlight : UiColors.Accent, picked = hc ? SystemColors.HighlightText : Color.White, text = hc ? SystemColors.WindowText : UiColors.Ink;
        int w = SegmentWidth;
        using var pen = new Pen(edge);
        for (int i = 0; i < Labels.Count; i++)
        {
            var r = new Rectangle(i * w, 0, w, Height - 1);
            if (i == _selected) using (var b = new SolidBrush(fill)) g.FillRectangle(b, r);
            if (i > 0) g.DrawLine(pen, r.Left, 0, r.Left, Height - 1);
            var font = i == _selected ? BoldFont : Font;
            TextRenderer.DrawText(g, Labels[i], font, r, i == _selected ? picked : text, Flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        g.DrawRectangle(pen, 0, 0, w * Labels.Count, Height - 1);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(new Rectangle(_selected * w, 0, w, Height), -2, -2));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _bold?.Dispose();
        base.Dispose(disposing);
    }
}
