using System.Globalization;
using PoolWatch.Core.Cache;
using PoolWatch.Core.Growth;
using PoolWatch.Core.Util;

namespace PoolWatch.Ui;

/// <summary>
/// The colours the stats controls paint with: the design's greys, or under high contrast the system's own text and grey
/// text colours (fixed greys on a black high-contrast window would be dark on black).
/// </summary>
internal static class StatColors
{
    public static (Color Caption, Color Heading, Color Line) For(bool highContrast) => highContrast
        ? (SystemColors.GrayText, SystemColors.WindowText, SystemColors.GrayText)
        : (UiColors.Muted, UiColors.Ink, UiColors.Line);

    /// <summary>
    /// What a screen reader says for painted stats: each caption with its value, as sentences ("Growth per hour 1.3 GB,
    /// without the clears, last 6 h. Cleared 7.1 GB, 6 clears, last 6 h.").
    /// </summary>
    public static string Spoken(IEnumerable<string> sentences) => string.Join(". ", sentences.Select(t => t.TrimEnd('.'))) + ".";
}

/// <summary>One of the Simple view's tiles: a grey caption, the value, and a small line under it.</summary>
internal sealed record Tile(string Caption, string Value, string Sub);

/// <summary>Memory now: RAM in use, the cache (standby) and the PC's RAM, in bytes.</summary>
internal readonly record struct RamNow(long InUse, long Cache, long Total);

/// <summary>
/// The Simple view's four tiles in words (pure): growth per hour with PoolWatch's own clears added back, what the clears
/// freed, the net change, all over the same window as the graph, and the memory in use. Amounts are rounded as the
/// header rounds them (<see cref="PlainWords.Amount"/>), so a clear's "freed 914 MB" and "Cleared 914 MB" agree.
/// </summary>
internal static class TileText
{
    public const string RateCaption = "Growth per hour", ClearedCaption = "Cleared", NetCaption = "Net change", RamCaption = "Memory in use";
    public const string NotKnown = "not known yet";

    public static IReadOnlyList<Tile> Of(GrowthWindow w, RamNow? ram) => [Rate(w), Cleared(w), Net(w), Ram(ram)];

    /// <summary>"last 6 h", "last hour"; "since 08:05" (or "since yesterday at 23:10") while PoolWatch has run for less than the window.</summary>
    public static string WindowWords(GrowthWindow w) =>
        w.StartedInside && w.StartedUtc is { } started ? "since " + GrowthText.Since(started, w.NowUtc)
        : w.Span == TimeSpan.FromHours(1) ? "last hour" : FormattableString.Invariant($"last {w.Span.TotalHours:0} h");

    /// <summary>"1.3 GB" an hour with the clears added back, "without the clears, last 6 h"; "none" when it did not grow; "known in 22 min" until 30 minutes are measured.</summary>
    public static Tile Rate(GrowthWindow w)
    {
        if (w.PerHour(w.Total) is not { } perHour) return new(RateCaption, GrowthText.KnownIn(w), WindowWords(w));
        if (perHour <= 0) return new(RateCaption, "none", WindowWords(w));
        return new(RateCaption, PlainWords.Amount(perHour), (w.Clears > 0 ? "without the clears, " : "") + WindowWords(w));
    }

    /// <summary>"7.1 GB", "6 clears, last 6 h"; "nothing" when the clears freed nothing; "no clears, last 6 h".</summary>
    public static Tile Cleared(GrowthWindow w) => new(ClearedCaption, !w.HasData ? NotKnown : HeaderText.Freed(w.Total.Cleared),
        (w.Clears == 0 ? "no clears" : w.Clears == 1 ? "1 clear" : $"{w.Clears} clears") + ", " + WindowWords(w));

    /// <summary>"up 307 MB", "down 1.2 GB", "unchanged".</summary>
    public static Tile Net(GrowthWindow w) => new(NetCaption, !w.HasData ? NotKnown : Signed(w.Total.Net), WindowWords(w));

    /// <summary>"41 GB", "of 127 GB, 71 GB cache"; whole gigabytes, as RAM is sold.</summary>
    public static Tile Ram(RamNow? ram) => ram is { Total: > 0 } r
        ? new(RamCaption, WholeGb(r.InUse), $"of {WholeGb(r.Total)}, {WholeGb(r.Cache)} cache")
        : new(RamCaption, NotKnown, "");

    /// <summary>A level change in words: "up 307 MB", "down 1.2 GB", "unchanged" under half a megabyte.</summary>
    public static string Signed(long bytes) =>
        Math.Abs(bytes) < ClearScheduleText.NothingBelow ? "unchanged" : (bytes > 0 ? "up " : "down ") + PlainWords.Amount(bytes);

    public static string WholeGb(long bytes) => FormattableString.Invariant($"{Math.Round(bytes / (double)(1L << 30)):0} GB");
}

/// <summary>
/// The Simple view's row of four tiles under the header: each a grey caption, its value and a line under it, in a thin
/// border; on a window too narrow for four in a row, two rows of two (its parent gives it <see cref="HeightFor"/>). It
/// draws itself (no label flashes), and repaints only when what it says changed.
/// </summary>
internal sealed class StatTiles : Control
{
    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
    private readonly Font _captionFont = new("Segoe UI", 9f), _valueFont = new("Segoe UI Semibold", 13f), _subFont = new("Segoe UI", 8.5f);
    private readonly ToolTip _tip = new() { AutoPopDelay = 30_000, InitialDelay = 400 };
    private IReadOnlyList<Tile> _tiles = TileText.Of(new GrowthWindow { Span = StatsWindow.Default.Span, NowUtc = DateTime.UtcNow }, null);
    private readonly List<(string Text, Rectangle Bounds, int Needed)> _painted = [];
    private Rectangle _rateArea;
    private string? _tipShown;

    public StatTiles()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        SetStyle(ControlStyles.Selectable, false); // text, not a stop for the Tab key
        TabStop = false;
        BackColor = SystemColors.Window;
        AccessibleRole = AccessibleRole.StaticText;
        AccessibleName = Spoken(_tiles);
    }

    private static string Spoken(IReadOnlyList<Tile> tiles) => StatColors.Spoken(tiles.Select(t => $"{t.Caption} {t.Value}" + (t.Sub.Length > 0 ? ", " + t.Sub : "")));

    /// <summary>What the tiles say, in order: each tile's caption, value and line.</summary>
    internal IReadOnlyList<string> Texts => _tiles.SelectMany(t => new[] { t.Caption, t.Value, t.Sub }).ToList();

    internal IReadOnlyList<Tile> Tiles => _tiles;

    /// <summary>What its last paint drew, in order (tests: the same as <see cref="Texts"/>, empty lines left out).</summary>
    internal IReadOnlyList<string> Painted => _painted.Select(p => p.Text).ToList();

    public void Show(IReadOnlyList<Tile> tiles)
    {
        if (tiles.SequenceEqual(_tiles)) return;
        _tiles = tiles;
        AccessibleName = Spoken(tiles);
        Invalidate();
    }

    private int S(int logical) => LogicalToDeviceUnits(logical);

    /// <summary>One tile's height.</summary>
    private int TileHeight => S(10) + _captionFont.Height + _valueFont.Height + _subFont.Height + S(10);

    /// <summary>Its height at its width: one row of tiles, or two.</summary>
    public int NeededHeight => HeightFor(Width);

    /// <summary>Its height at <paramref name="width"/>: one row of tiles where four fit, else two.</summary>
    public int HeightFor(int width) => width >= NeededWidth ? TileHeight : TileHeight * 2 + S(12);

    /// <summary>The least width at which four tiles in a row cut no text (the CI layout check); narrower, they go two by two.</summary>
    public int NeededWidth => WidthFor(_tiles.Count);

    private int WidthFor(int columns)
    {
        int widest = _tiles.Max(t => Math.Max(Measure(t.Caption, _captionFont), Math.Max(Measure(t.Value, _valueFont), Measure(t.Sub, _subFont))));
        return columns * (widest + 2 * S(14)) + (columns - 1) * S(12);
    }

    /// <summary>How many tiles in a row at the current width: all four where they fit, else two.</summary>
    internal int Columns => Width >= NeededWidth ? _tiles.Count : 2;


    private static int Measure(string text, Font font) => text.Length == 0 ? 0 : TextRenderer.MeasureText(text, font, Size.Empty, Flags & ~TextFormatFlags.EndEllipsis).Width;

    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        _painted.Clear();
        var (caption, heading, line) = StatColors.For(SystemInformation.HighContrast);
        int gap = S(12), columns = Columns, w = Math.Max(1, (Width - (columns - 1) * gap) / columns), pad = S(14), h = TileHeight;
        using var border = new Pen(line);
        for (int i = 0; i < _tiles.Count; i++)
        {
            var t = _tiles[i];
            var box = new Rectangle(i % columns * (w + gap), i / columns * (h + gap), w - 1, h - 1);
            g.DrawRectangle(border, box);
            int x = box.Left + pad, y = box.Top + S(8), room = Math.Max(1, w - 2 * pad);
            void Draw(string text, Font font, Color color, int height)
            {
                if (text.Length > 0)
                {
                    var at = new Rectangle(x, y, room, height);
                    TextRenderer.DrawText(g, text, font, at, color, Flags);
                    _painted.Add((text, at, Measure(text, font)));
                }
                y += height;
            }
            Draw(t.Caption, _captionFont, caption, _captionFont.Height);
            Draw(t.Value, _valueFont, heading, _valueFont.Height);
            Draw(t.Sub, _subFont, caption, _subFont.Height);
            if (i == 0) _rateArea = box;
        }
    }

    /// <summary>Texts cut short at the current width (the CI layout check); empty when every tile says all of it.</summary>
    internal List<string> Problems() =>
        _painted.Where(p => p.Needed > p.Bounds.Width + 1).Select(p => $"the tile text \"{p.Text}\" needs {p.Needed} px, has {p.Bounds.Width}").ToList();

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        string tip = TipAt(e.Location);
        if (tip == _tipShown) return;
        _tipShown = tip;
        _tip.SetToolTip(this, tip);
    }

    /// <summary>The tooltip at a point of the last paint: the rate's meaning over its tile, else none.</summary>
    internal string TipAt(Point p) => _rateArea.Contains(p) ? GrowthText.PerHourTip : "";

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _captionFont.Dispose(); _valueFont.Dispose(); _subFont.Dispose(); _tip.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>
/// The Advanced view's growth and clear stats beside the graph, in words (pure): which stretch they cover ("Last 6 hours",
/// "Since 00:53 (8.8 h)"), Grew, Cleared (N clears), Fell on its own and Net change, and the rate in a sentence ("Without
/// the clears it would grow 1.3 GB per hour."). Amounts are rounded as the header rounds them.
/// </summary>
internal static class LedgerText
{
    public const string NotSampled = "The rest was not sampled, for example while the PC was asleep.";

    /// <summary>"Last 6 hours"; "Since 00:53 (8.8 h)" while PoolWatch has run for less; "Last 24 hours (18 h measured)" across a sleep.</summary>
    public static string Label(StatsWindow picked, GrowthWindow w) =>
        w.StartedInside && w.StartedUtc is { } started ? $"Since {GrowthText.Since(started, w.NowUtc)} ({Hours(w.Measured)})"
        : w.Whole || !w.HasData ? picked.Name : $"{picked.Name} ({Hours(w.Measured)} measured)";

    /// <summary>The rows: Grew, Cleared (N clears), Fell on its own, and Net change (bold, under a rule).</summary>
    public static (string Label, string Value, bool Bold)[] Rows(GrowthWindow w) =>
    [
        (GrowthText.GrewLabel, w.HasData ? Plus(w.Total.Grew) : TileText.NotKnown, false),
        (GrowthText.ClearedLabel(w.Clears), w.HasData ? Less(w.Total.Cleared) : TileText.NotKnown, false),
        (GrowthText.FellLabel, w.HasData ? Less(w.Total.Fell) : TileText.NotKnown, false),
        (GrowthText.NetLabel, w.HasData ? TileText.Signed(w.Total.Net) : TileText.NotKnown, true),
    ];

    /// <summary>The rate as a sentence: "Without the clears it would grow 1.3 GB per hour.", "It grows 120 MB per hour.", "Growth per hour: known in 22 min."</summary>
    public static string Rate(GrowthWindow w)
    {
        if (w.PerHour(w.Total) is not { } perHour) return $"Growth per hour: {GrowthText.KnownIn(w)}.";
        if (Math.Abs(perHour) < ClearScheduleText.NothingBelow) return w.Clears > 0 ? "Without the clears it would hold steady." : "It holds steady.";
        string amount = PlainWords.Amount(perHour);
        return (w.Clears > 0, perHour > 0) switch
        {
            (true, true) => $"Without the clears it would grow {amount} per hour.",
            (true, false) => $"Without the clears it would fall {amount} per hour.",
            (false, true) => $"It grows {amount} per hour.",
            _ => $"It falls {amount} per hour.",
        };
    }

    /// <summary>The line about a stretch not sampled (a sleep), only when there was one and PoolWatch had started before it; else null.</summary>
    public static string? Measured(GrowthWindow w) => w.HasData && !w.Whole && !w.StartedInside ? NotSampled : null;

    /// <summary>"8.8 h", "40 min", "less than a minute".</summary>
    public static string Hours(TimeSpan t) =>
        t >= TimeSpan.FromHours(1) ? (Math.Round(t.TotalHours, 1)).ToString("0.#", CultureInfo.InvariantCulture) + " h" : GrowthText.Duration(t, down: true);

    /// <summary>"+11.2 GB", "0".</summary>
    public static string Plus(long bytes) => bytes < ClearScheduleText.NothingBelow ? "0" : "+" + PlainWords.Amount(bytes);

    /// <summary>"−10.8 GB" (what the clears freed, or what fell on its own), "0".</summary>
    public static string Less(long bytes) => bytes < ClearScheduleText.NothingBelow ? "0" : GrowthText.Minus + PlainWords.Amount(bytes);
}

/// <summary>
/// The Advanced view's growth and clear stats beside the graph (the design's ledger): which stretch they cover, Grew,
/// Cleared (N clears), Fell on its own and Net change, and the rate in a sentence. The graph's 1 h / 6 h / 24 h choice
/// sets the window. It draws its texts itself; the rate sentence wraps, and when the panel is too low for the last line,
/// that line is its tooltip instead of being cut off.
/// </summary>
internal sealed class GrowthPanel : Control
{
    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
    private const TextFormatFlags Wrapped = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak;
    private readonly Font _textFont = new("Segoe UI", 9f), _boldFont = new("Segoe UI Semibold", 9f), _noteFont = new("Segoe UI", 8.5f);
    private readonly ToolTip _tip = new() { AutoPopDelay = 30_000, InitialDelay = 400 };
    private string _label = StatsWindow.Default.Name, _rate = LedgerText.Rate(new GrowthWindow { Span = StatsWindow.Default.Span, NowUtc = DateTime.UtcNow });
    private string? _measured;
    private (string Label, string Value, bool Bold)[] _rows = LedgerText.Rows(new GrowthWindow { Span = StatsWindow.Default.Span, NowUtc = DateTime.UtcNow });
    private string? _tipShown;
    private int _needed;
    private readonly List<string> _painted = [];
    /// <summary>The notes that did not fit under the rows at the last paint: they are the panel's tooltip instead.</summary>
    private string _hidden = "";

    public GrowthPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        BackColor = SystemColors.Window;
        AccessibleRole = AccessibleRole.StaticText;
        AccessibleName = Spoken();
    }

    private string Spoken() => StatColors.Spoken([_label, .. _rows.Select(r => $"{r.Label} {r.Value}"), _rate, .. _measured is { } m ? new[] { m } : []]);

    /// <summary>What it says, in order: the label, each row's label and value, the rate, and the line about a stretch not sampled.</summary>
    internal IReadOnlyList<string> Texts => [_label, .. _rows.SelectMany(r => new[] { r.Label, r.Value }), _rate, .. _measured is { } m ? new[] { m } : []];

    /// <summary>What its last paint drew, in order (tests: the same as <see cref="Texts"/> when all of it fits).</summary>
    internal IReadOnlyList<string> Painted => _painted;

    /// <summary>The stats of <paramref name="w"/>, the window <paramref name="picked"/>; repaints only when what it says changed.</summary>
    public void Show(StatsWindow picked, GrowthWindow w)
    {
        string label = LedgerText.Label(picked, w), rate = LedgerText.Rate(w);
        var measured = LedgerText.Measured(w);
        var rows = LedgerText.Rows(w);
        if (label == _label && rate == _rate && measured == _measured && rows.SequenceEqual(_rows)) return;
        (_label, _rate, _measured, _rows) = (label, rate, measured, rows);
        AccessibleName = Spoken();
        Invalidate();
    }

    /// <summary>The stats of <paramref name="w"/>, labelled as the window of its span (the default window's name for a span that is none of them).</summary>
    public void Show(GrowthWindow w) => Show(StatsWindow.All.FirstOrDefault(s => s.Span == w.Span) ?? StatsWindow.Default, w);

    private int S(int logical) => LogicalToDeviceUnits(logical);

    /// <summary>The height its texts need at its width (the CI layout check); notes that do not fit are left out of it (they are the tooltip).</summary>
    internal int NeededHeight => _needed;

    /// <summary>The least height that shows every row whole (the label, Grew, Cleared, Fell on its own and Net change): the notes under them can be the tooltip.</summary>
    internal int RowsHeight => S(10) + _textFont.Height + S(5) + _rows.Sum(r => r.Bold ? S(6) + _boldFont.Height + S(3) : _textFont.Height + S(3));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        _painted.Clear();
        var (caption, heading, line) = StatColors.For(SystemInformation.HighContrast);
        using (var border = new Pen(line)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        int left = S(14), right = Width - S(14), width = Math.Max(S(40), right - left);
        int y = S(10);
        void Draw(string text, Font font, int x, int top, Color color)
        {
            TextRenderer.DrawText(g, text, font, new Point(x, top), color, Flags);
            _painted.Add(text);
        }
        Draw(_label, _textFont, left, y, caption);
        y += _textFont.Height + S(5);
        using var rule = new Pen(line);
        foreach (var (label, value, bold) in _rows)
        {
            var font = bold ? _boldFont : _textFont;
            if (bold)
            {
                g.DrawLine(rule, left, y + S(1), right, y + S(1));
                y += S(6);
            }
            Draw(label, font, left, y, heading);
            int vw = TextRenderer.MeasureText(g, value, font, Size.Empty, Flags).Width;
            Draw(value, font, right - vw, y, heading);
            y += font.Height + S(3);
        }
        // The notes under the rows; one too low to show is the tooltip instead of being cut off.
        _needed = y;
        var hidden = new List<string>();
        foreach (var note in _measured is { } m ? new[] { _rate, m } : [_rate])
        {
            var size = TextRenderer.MeasureText(g, note, _noteFont, new Size(width, int.MaxValue), Wrapped);
            if (hidden.Count > 0 || y + S(4) + size.Height + S(4) > Height) { hidden.Add(note); continue; }
            y += S(4);
            TextRenderer.DrawText(g, note, _noteFont, new Rectangle(left, y, width, size.Height), caption, Wrapped);
            _painted.Add(note);
            y += size.Height;
            _needed = y + S(4);
        }
        _hidden = string.Join(Environment.NewLine, hidden);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        string tip = TipAt(e.Location);
        if (tip == _tipShown) return;
        _tipShown = tip;
        _tip.SetToolTip(this, tip);
    }

    /// <summary>The tooltip: the notes that did not fit at the last paint, else the rate's meaning.</summary>
    internal string TipAt(Point p) => _hidden.Length > 0 ? _hidden : GrowthText.PerHourTip;

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _textFont.Dispose(); _boldFont.Dispose(); _noteFont.Dispose(); _tip.Dispose(); }
        base.Dispose(disposing);
    }
}
