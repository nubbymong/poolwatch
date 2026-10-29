using System.Drawing.Drawing2D;
using System.Globalization;
using PoolWatch.Core.Cache;
using PoolWatch.Core.Model;
using PoolWatch.Core.Util;

namespace PoolWatch.Ui;

/// <summary>Which way the kernel pool went in the last 10 minutes, as the header's arrow shows it.</summary>
internal enum TrendKind { Collecting, Measuring, Steady, Up, Down }

/// <summary>How much a fact asks for attention: calm grey, amber to watch, red for a real risk, green for a fall.</summary>
internal enum HeaderTone { Calm, Watch, Risk, Good }

/// <summary>
/// What the main window's header says, one statement per fact: the kernel pool now (paged and non-paged), which way it
/// went in the last 10 minutes and by how much, when the next cache clear runs (with a ring for how close it is) and what
/// the last one freed, and, only when there is something to watch, one note (a likely cause PoolWatch is sure of, a
/// saturated PC, an open incident). Every amount is rounded one way (<see cref="PlainWords.Amount"/>), as the tiles round.
/// </summary>
internal sealed record HeaderFacts
{
    public const string PoolCaption = "Kernel pool now";

    public string PoolNow { get; init; } = "";
    public string PoolSplit { get; init; } = "";
    public TrendKind Trend { get; init; }
    public HeaderTone TrendTone { get; init; }
    /// <summary>"243 MB", "Steady"; empty while the trend is not known.</summary>
    public string TrendAmount { get; init; } = "";
    /// <summary>"up in the last 10 min", "collecting the first samples".</summary>
    public string TrendText { get; init; } = "";
    /// <summary>The smaller header's words for the same trend: "up in 10 min" (<see cref="HeaderText.Short"/>).</summary>
    public string TrendTextShort => HeaderText.Short(TrendText);
    /// <summary>"Next clear in 37 min", "Automatic clearing is off".</summary>
    public string NextTitle { get; init; } = "";
    /// <summary>"Last at 08:59: freed 914 MB"; empty while a clear runs and none finished before.</summary>
    public string NextText { get; init; } = "";
    /// <summary>How close the next clear is, 0 to 1; null when no clear is coming.</summary>
    public double? Ring { get; init; }
    public string? Note { get; init; }
    public HeaderTone NoteTone { get; init; }

    /// <summary>
    /// The header as sentences (a screen reader, and the CI's status): "Kernel pool now 8.4 GB (paged 4.6 GB, non-paged
    /// 3.8 GB). 243 MB up in the last 10 min. Next clear in 37 min. Last at 08:59: freed 914 MB." No amount carries a sign.
    /// </summary>
    public string Spoken
    {
        get
        {
            var parts = new List<string> { $"{PoolCaption} {PoolNow}" + (PoolSplit.Length > 0 ? $" ({PoolSplit})" : "") };
            parts.Add(TrendAmount.Length > 0 ? $"{TrendAmount} {TrendText}" : Capital(TrendText));
            if (Note is { Length: > 0 } note) parts.Add(note);
            parts.Add(NextTitle);
            if (NextText.Length > 0) parts.Add(NextText);
            return string.Join(" ", parts.Where(p => p.Length > 0).Select(p => p.TrimEnd('.') + "."));
        }
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

/// <summary>Builds <see cref="HeaderFacts"/> from what the window reads (pure; unit-tested).</summary>
internal static class HeaderText
{
    /// <summary>
    /// The header's facts. <paramref name="t"/> is the status line's totals (the change measured across PoolWatch's own
    /// clears, so a clear's drop is never said as the pool falling: the last clear's result says it), <paramref name="pool"/>
    /// the newest sample's paged and non-paged pool, <paramref name="next"/> the automatic clear's next run and
    /// <paramref name="clears"/> PoolWatch's clears (oldest first), <paramref name="everyHours"/> the schedule (for the ring).
    /// </summary>
    public static HeaderFacts Of(TotalsNow t, PoolTotals pool, NextClear next, IReadOnlyList<ClearRecord> clears, double everyHours, DateTime nowUtc,
        string? note = null, HeaderTone noteTone = HeaderTone.Calm)
    {
        var (trend, tone, amount, text) = Trend(t);
        var (title, ring) = Next(next, everyHours, t.Total ?? 0, nowUtc);
        return new HeaderFacts
        {
            PoolNow = t.Total is { } total ? PlainWords.Amount(total) : "not known yet",
            PoolSplit = t.Total is null ? "" : $"paged {PlainWords.Amount((long)pool.CommittedPaged)}, non-paged {PlainWords.Amount((long)pool.CommittedNonPaged)}",
            Trend = trend, TrendTone = tone, TrendAmount = amount, TrendText = text,
            NextTitle = title, NextText = LastClear(clears, nowUtc, next.Kind == NextClearKind.Running), Ring = ring,
            Note = note, NoteTone = noteTone,
        };
    }

    /// <summary>Which way the pool went in the last 10 minutes: grey for a small move, amber for a rise, green for a fall of 100 MB or more.</summary>
    internal static (TrendKind Kind, HeaderTone Tone, string Amount, string Text) Trend(TotalsNow t)
    {
        if (t.Total is null) return (TrendKind.Collecting, HeaderTone.Calm, "", "collecting the first sample");
        if (t.MeasuringUtc is not null) return (TrendKind.Measuring, HeaderTone.Calm, "", "a cache clear is being measured");
        if (t.Held < PoolStatusLine.MinHistory) return (TrendKind.Collecting, HeaderTone.Calm, "", "the trend shows in a few minutes");
        string span = t.Held >= TimeSpan.FromMinutes(9.5) ? "the last 10 min" : $"the last {Math.Max(1, (int)Math.Round(t.Held.TotalMinutes))} min";
        long c = t.Change10Min;
        if (Math.Abs(c) < ClearScheduleText.NothingBelow) return (TrendKind.Steady, HeaderTone.Calm, "Steady", "in " + span);
        return c > 0
            ? (TrendKind.Up, c >= PoolStatusLine.GrowBytes10Min ? HeaderTone.Watch : HeaderTone.Calm, PlainWords.Amount(c), "up in " + span)
            : (TrendKind.Down, -c >= PoolStatusLine.DropBytes10Min ? HeaderTone.Good : HeaderTone.Calm, PlainWords.Amount(-c), "down in " + span);
    }

    /// <summary>
    /// The smaller header's trend words, as the design has them there: "up in 10 min" for "up in the last 10 min", "down in
    /// 6 min" for "down in the last 6 min". Steady keeps "in the last 10 min" (under "Steady", "in 10 min" reads like a time
    /// to come), and a trend not measured yet keeps its words.
    /// </summary>
    internal static string Short(string trendText) =>
        trendText.StartsWith("up in the last ", StringComparison.Ordinal) || trendText.StartsWith("down in the last ", StringComparison.Ordinal)
            ? trendText.Replace("in the last ", "in ", StringComparison.Ordinal)
            : trendText;

    /// <summary>When the next clear runs, and how close it is (the ring): the time left of the schedule, or the pool against the size rule.</summary>
    internal static (string Title, double? Ring) Next(NextClear next, double everyHours, long poolBytes, DateTime nowUtc) => next.Kind switch
    {
        NextClearKind.Scheduled => ($"Next clear in {In((next.AtUtc ?? nowUtc) - nowUtc)}" + (next.PoolBytes is { } limit ? $" or at {Limit(limit)}" : ""),
            everyHours > 0 ? Math.Clamp(1 - ((next.AtUtc ?? nowUtc) - nowUtc).TotalHours / everyHours, 0, 1) : null),
        NextClearKind.AtPoolSize => ($"Next clear at {Limit(next.PoolBytes ?? 0)}",
            next.PoolBytes is > 0 and var l ? Math.Clamp(poolBytes / (double)l, 0, 1) : null),
        NextClearKind.Running => ("Clearing now", 1),
        NextClearKind.OwnTask => ("Your own clear task clears it", null),
        NextClearKind.NotElevated => ("Clearing needs administrator rights", null),
        NextClearKind.Experiment => ("Clearing waits for the experiment", null),
        _ => ("Automatic clearing is off", null),
    };

    /// <summary>
    /// What the last finished clear did: "Last at 08:59: freed 914 MB", "Last yesterday at 23:00: freed nothing", "Last at
    /// 08:59: did not run"; "No clear yet" before any; empty while the first one runs.
    /// </summary>
    internal static string LastClear(IReadOnlyList<ClearRecord> clears, DateTime nowUtc, bool running)
    {
        for (int i = clears.Count - 1; i >= 0; i--)
        {
            var c = clears[i];
            if (!c.Done) continue;
            return c.Error is not null ? $"Last {When(c.TimeUtc, nowUtc)}: did not run" : $"Last {When(c.TimeUtc, nowUtc)}: freed {Freed(c.Freed)}";
        }
        return running || clears.Count > 0 ? "" : "No clear yet";
    }

    /// <summary>What a clear freed, rounded as every amount in the header and the tiles: "914 MB", "1.3 GB", "nothing".</summary>
    public static string Freed(long bytes) => bytes < ClearScheduleText.NothingBelow ? "nothing" : PlainWords.Amount(bytes);

    /// <summary>"under a minute", "23 min", "1 h", "1 h 20 min" (minutes rounded up, so never "0 min").</summary>
    internal static string In(TimeSpan t)
    {
        if (t < TimeSpan.FromMinutes(1)) return "under a minute";
        int minutes = (int)Math.Ceiling(t.TotalMinutes);
        if (minutes < 60) return $"{minutes} min";
        return minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60} min";
    }

    /// <summary>A size rule's limit as set: "8 GB", "7.5 GB".</summary>
    internal static string Limit(long bytes) => (bytes / (double)(1L << 30)).ToString("0.#", CultureInfo.InvariantCulture) + " GB";

    /// <summary>A past time: "at 09:12" today, "yesterday at 09:12", else "on 26 Oct at 09:12".</summary>
    internal static string When(DateTime utc, DateTime nowUtc)
    {
        var day = utc.ToLocalTime().Date;
        var today = nowUtc.ToLocalTime().Date;
        string at = "at " + utc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        return day >= today ? at : day == today.AddDays(-1) ? "yesterday " + at : $"on {day.ToString("d MMM", CultureInfo.InvariantCulture)} {at}";
    }
}

/// <summary>
/// The header's left part, painted (a refresh never blanks it): the kernel pool now with its paged and non-paged split,
/// the trend arrow with its amount, the note when there is one, and the next clear with its ring. <see cref="Compact"/>
/// (the Services tabs, which need the rows, and the Advanced view on a window too narrow for the full facts in a row)
/// keeps the pool, the trend in shorter words and when the next clear runs, smaller and on one line each. Every position
/// comes from the fonts' measured sizes. On a window too narrow for one row, the next clear goes on a second row; the note
/// shows only where it has room (its text is the tooltip anywhere on the header then).
/// </summary>
internal sealed class PoolHeaderView : Control
{
    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
    private const TextFormatFlags Wrapped = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis;
    private readonly Font _caption = new("Segoe UI", 9f), _value = new("Segoe UI Semibold", 20f), _trend = new("Segoe UI Semibold", 11.5f),
        _nextTitle = new("Segoe UI Semibold", 10f), _compactValue = new("Segoe UI Semibold", 14f);
    private readonly ToolTip _tip = new() { AutoPopDelay = 30_000, InitialDelay = 400 };
    private HeaderFacts _facts = new() { PoolNow = "not known yet", TrendText = "collecting the first sample", NextTitle = "" };
    private string _trendTip = "", _nextTip = "";
    private Rectangle _trendArea, _nextArea, _noteArea;
    private string? _tipShown;
    private readonly List<(string Text, Rectangle Bounds)> _painted = [];
    private bool _noteHidden;

    public PoolHeaderView()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        BackColor = SystemColors.Window;
        AccessibleRole = AccessibleRole.StaticText;
        AccessibleName = _facts.Spoken;
    }

    /// <summary>What it says now.</summary>
    public HeaderFacts Facts => _facts;

    /// <summary>The smaller header: no split, no ring, no last clear, the trend's shorter words (the window asks for the height again).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Compact
    {
        get => _compact;
        set { if (_compact == value) return; _compact = value; Invalidate(); }
    }
    private bool _compact;

    /// <summary>The facts, and the tooltips over the trend (the fuller status sentence) and the next clear (the whole line about clearing); repaints only when they changed.</summary>
    public void Show(HeaderFacts facts, string trendTip, string nextTip)
    {
        (_trendTip, _nextTip) = (trendTip, nextTip);
        if (facts == _facts) return;
        _facts = facts;
        AccessibleName = facts.Spoken;
        Invalidate(); // the window asks for its height again (NeededHeight) after each change
    }

    private int S(int logical) => LogicalToDeviceUnits(logical);

    private static int W(string text, Font font) => text.Length == 0 ? 0 : TextRenderer.MeasureText(text, font, Size.Empty, Flags).Width;

    // Each size for the full header, or the smaller one (c): the smaller has no split, no ring, no last clear, and shorter trend words.
    private Font ValueFor(bool c) => c ? _compactValue : _value;
    private Font TrendFontFor(bool c) => c ? _nextTitle : _trend;
    private static string SplitFor(HeaderFacts f, bool c) => c ? "" : f.PoolSplit;
    private static string LastFor(HeaderFacts f, bool c) => c ? "" : f.NextText;
    private static string TrendWordsFor(HeaderFacts f, bool c) => c ? f.TrendTextShort : f.TrendText;
    private int PoolWidth(HeaderFacts f, bool c) => Math.Max(W(HeaderFacts.PoolCaption, _caption), Math.Max(W(f.PoolNow, ValueFor(c)), W(SplitFor(f, c), _caption)));
    private int ArrowFor(bool c) => S(c ? 28 : 36);
    private int TrendWidth(HeaderFacts f, bool c) => S(24) + ArrowFor(c) + S(10) + Math.Max(W(f.TrendAmount, TrendFontFor(c)), W(TrendWordsFor(f, c), _caption));
    private int RingFor(bool c) => c ? 0 : S(38);
    private int NextWidth(HeaderFacts f, bool c) => (c ? 0 : RingFor(c) + S(10)) + Math.Max(W(f.NextTitle, _nextTitle), W(LastFor(f, c), _caption));
    private int RowHeightFor(bool c) => _caption.Height + ValueFor(c).Height + (c ? 0 : _caption.Height);
    private int OneRowWidthFor(bool c) => PoolWidth(_facts, c) + S(28) + TrendWidth(_facts, c) + S(28) + NextWidth(_facts, c);

    private Font Value => ValueFor(_compact);
    private Font TrendFont => TrendFontFor(_compact);
    private string Split(HeaderFacts f) => SplitFor(f, _compact);
    private string LastText(HeaderFacts f) => LastFor(f, _compact);
    private string TrendWords(HeaderFacts f) => TrendWordsFor(f, _compact);
    private int Arrow => ArrowFor(_compact);
    private int Ring => RingFor(_compact);
    private int NextWidth(HeaderFacts f) => NextWidth(f, _compact);
    private int RowHeight => RowHeightFor(_compact);

    /// <summary>The pool, the trend and the next clear do not fit side by side in <paramref name="width"/>.</summary>
    private bool TwoRows(int width) => !FitsOneRow(width, _compact);

    /// <summary>Whether the facts fit side by side in <paramref name="width"/>, in the full header or the smaller one (<paramref name="compact"/>).</summary>
    public bool FitsOneRow(int width, bool compact) => OneRowWidthFor(compact) <= width;

    /// <summary>The height it needs at <paramref name="width"/>: one row, or two when the next clear goes under.</summary>
    public int NeededHeight(int width) => RowHeight + (TwoRows(width) ? S(8) + Math.Max(Ring, _nextTitle.Height + (_compact ? 0 : _caption.Height)) : 0);

    /// <summary>The width it needs for one row (the pool, the trend and the next clear; the note takes what is left).</summary>
    public int OneRowWidth => OneRowWidthFor(_compact);

    /// <summary>What its last paint drew, with where (tests, the CI layout check).</summary>
    internal IReadOnlyList<(string Text, Rectangle Bounds)> Painted => _painted;

    /// <summary>The note did not fit at the last paint (it is the tooltip instead).</summary>
    internal bool NoteHidden => _noteHidden;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        _painted.Clear();
        var f = _facts;
        void Draw(string text, Font font, Point at, Color color)
        {
            if (text.Length == 0) return;
            TextRenderer.DrawText(g, text, font, at, color, Flags);
            // Its line box: as wide as the text, as high as the font's line, which is what the layout steps by.
            _painted.Add((text, new Rectangle(at, new Size(TextRenderer.MeasureText(g, text, font, Size.Empty, Flags).Width, font.Height))));
        }
        Color text = UiColors.Text, secondary = UiColors.Secondary;
        int rowH = RowHeight;

        // The pool now.
        int x = 0;
        Draw(HeaderFacts.PoolCaption, _caption, new Point(x, 0), secondary);
        Draw(f.PoolNow, Value, new Point(x, _caption.Height), text);
        Draw(Split(f), _caption, new Point(x, _caption.Height + Value.Height), secondary);
        x += PoolWidth(f, _compact) + S(24);

        // The trend: a divider, the arrow in its circle, the amount over its words.
        using (var rule = new Pen(UiColors.Rule)) g.DrawLine(rule, x, S(4), x, rowH - S(4));
        x += S(24);
        int arrowTop = (rowH - Arrow) / 2;
        var circle = new Rectangle(x, arrowTop, Arrow, Arrow);
        DrawArrow(g, circle, f.Trend, f.TrendTone);
        int tx = circle.Right + S(10);
        int textTop = f.TrendAmount.Length > 0 ? (rowH - TrendFont.Height - _caption.Height) / 2 : (rowH - _caption.Height) / 2;
        Draw(f.TrendAmount, TrendFont, new Point(tx, textTop), ToneColor(f.TrendTone, text));
        Draw(TrendWords(f), _caption, new Point(tx, textTop + (f.TrendAmount.Length > 0 ? TrendFont.Height : 0)), secondary);
        int trendRight = tx + Math.Max(W(f.TrendAmount, TrendFont), W(TrendWords(f), _caption));
        _trendArea = Rectangle.FromLTRB(circle.Left, 0, trendRight, rowH);

        // The next clear: at the right of the first row, or alone on the second.
        bool two = TwoRows(Width);
        int nextW = NextWidth(f);
        string last = LastText(f);
        int nextH = _compact ? _nextTitle.Height : Ring;
        var nextAt = two ? new Point(0, rowH + S(8)) : new Point(Math.Max(trendRight + S(28), Width - nextW), (rowH - nextH) / 2);
        int nx = nextAt.X;
        if (!_compact)
        {
            DrawRing(g, new Rectangle(nextAt.X, nextAt.Y, Ring, Ring), f.Ring);
            nx += Ring + S(10);
        }
        int nTop = nextAt.Y + (nextH - _nextTitle.Height - (last.Length > 0 ? _caption.Height : 0)) / 2;
        Draw(f.NextTitle, _nextTitle, new Point(nx, nTop), text);
        Draw(last, _caption, new Point(nx, nTop + _nextTitle.Height), secondary);
        _nextArea = new Rectangle(nextAt, new Size(nextW, nextH));

        // The note, between the trend and the next clear, where it has room (two lines at most).
        _noteArea = Rectangle.Empty;
        _noteHidden = false;
        if (f.Note is { Length: > 0 } note && !_compact)
        {
            int left = trendRight + S(24), right = (two ? Width : _nextArea.Left) - S(16);
            int lines = 2 * _caption.Height;
            var need = TextRenderer.MeasureText(g, note, _caption, new Size(Math.Max(1, right - left), int.MaxValue), Wrapped);
            if (right - left >= S(120) && need.Height <= lines)
            {
                _noteArea = new Rectangle(left, (rowH - need.Height) / 2, right - left, need.Height);
                TextRenderer.DrawText(g, note, _caption, _noteArea, ToneColor(f.NoteTone, secondary), Wrapped);
                _painted.Add((note, _noteArea));
            }
            else _noteHidden = true;
        }
        else _noteHidden = f.Note is { Length: > 0 };
    }

    private static Color ToneColor(HeaderTone tone, Color calm) => UiColors.HighContrast ? calm : tone switch
    {
        HeaderTone.Watch => UiColors.Watch,
        HeaderTone.Risk => UiColors.Risk,
        HeaderTone.Good => UiColors.Good,
        _ => calm,
    };

    /// <summary>The trend's arrow in a soft circle: up, down, level, or a dot while the trend is not known.</summary>
    private void DrawArrow(Graphics g, Rectangle r, TrendKind kind, HeaderTone tone)
    {
        bool hc = UiColors.HighContrast;
        Color soft = hc ? SystemColors.Window : tone switch { HeaderTone.Watch => UiColors.WatchSoft, HeaderTone.Good => UiColors.GoodSoft, HeaderTone.Risk => UiColors.RiskSoft, _ => UiColors.Panel };
        Color stroke = hc ? SystemColors.WindowText : tone == HeaderTone.Calm ? UiColors.Muted : ToneColor(tone, UiColors.Muted);
        using (var fill = new SolidBrush(soft)) g.FillEllipse(fill, r);
        if (hc) using (var edge = new Pen(SystemColors.WindowText)) g.DrawEllipse(edge, r);
        using var pen = new Pen(stroke, Math.Max(1.6f, S(2))) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f, a = r.Width * 0.24f, h = r.Width * 0.12f;
        switch (kind)
        {
            case TrendKind.Up:
                g.DrawLine(pen, cx, cy + a, cx, cy - a);
                g.DrawLines(pen, [new PointF(cx - a * 0.8f, cy - a + a * 0.8f), new PointF(cx, cy - a), new PointF(cx + a * 0.8f, cy - a + a * 0.8f)]);
                break;
            case TrendKind.Down:
                g.DrawLine(pen, cx, cy - a, cx, cy + a);
                g.DrawLines(pen, [new PointF(cx - a * 0.8f, cy + a - a * 0.8f), new PointF(cx, cy + a), new PointF(cx + a * 0.8f, cy + a - a * 0.8f)]);
                break;
            case TrendKind.Steady:
                g.DrawLine(pen, cx - a, cy, cx + a, cy);
                g.DrawLines(pen, [new PointF(cx + a - a * 0.7f, cy - a * 0.7f), new PointF(cx + a, cy), new PointF(cx + a - a * 0.7f, cy + a * 0.7f)]);
                break;
            default:
                using (var dot = new SolidBrush(stroke)) g.FillEllipse(dot, cx - h, cy - h, 2 * h, 2 * h);
                break;
        }
    }

    /// <summary>The next clear's ring: a grey track, and the accent arc for how close the clear is (from the top, clockwise).</summary>
    private void DrawRing(Graphics g, Rectangle r, double? fraction)
    {
        float w = Math.Max(3f, S(4));
        var box = RectangleF.Inflate(r, -w / 2, -w / 2);
        using (var track = new Pen(UiColors.HighContrast ? SystemColors.GrayText : UiColors.Track, w)) g.DrawEllipse(track, box);
        if (fraction is not { } f || f <= 0) return;
        using var arc = new Pen(UiColors.HighContrast ? SystemColors.Highlight : UiColors.Accent, w) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(arc, box, -90, (float)(Math.Min(1, f) * 360));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        string tip = TipAt(e.Location);
        if (tip == _tipShown) return;
        _tipShown = tip;
        _tip.SetToolTip(this, tip);
    }

    /// <summary>The tooltip at a point of the last paint: the fuller status over the trend, the whole line about clearing over the next clear; a note that did not fit, anywhere else.</summary>
    internal string TipAt(Point p) =>
        _trendArea.Contains(p) ? _trendTip : _nextArea.Contains(p) ? _nextTip : _noteHidden ? _facts.Note ?? "" : "";

    /// <summary>Texts painted outside the control, or overlapping each other (the CI layout check); empty when clean.</summary>
    internal List<string> Problems()
    {
        var p = new List<string>();
        var client = ClientRectangle;
        foreach (var (text, bounds) in _painted)
            if (!client.Contains(bounds)) p.Add($"the header's \"{text}\" at {bounds} is outside it ({ClientSize})");
        for (int i = 0; i < _painted.Count; i++)
            for (int j = i + 1; j < _painted.Count; j++)
                if (_painted[i].Bounds.IntersectsWith(_painted[j].Bounds)) p.Add($"the header's \"{_painted[i].Text}\" and \"{_painted[j].Text}\" overlap");
        return p;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _caption.Dispose(); _value.Dispose(); _trend.Dispose(); _nextTitle.Dispose(); _compactValue.Dispose(); _tip.Dispose(); }
        base.Dispose(disposing);
    }
}
