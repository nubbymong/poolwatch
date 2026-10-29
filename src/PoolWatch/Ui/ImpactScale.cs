using System.Drawing.Drawing2D;
using PoolWatch.Core.Impact;

namespace PoolWatch.Ui;

/// <summary>
/// The impact scale as every window shows it, from one rule: a level's word, its colour (the design's yellow, orange and
/// red, <see cref="UiColors.Low"/>, <see cref="UiColors.Medium"/> and <see cref="UiColors.High"/>; None has none), how many
/// of the meter's three bars it fills, and the meter itself: three rounded bars, the filled ones in the level's colour and
/// the rest in the track's grey. The Pool causes Impact column draws it. With high contrast on, the filled bars are in the text colour and the rest are outlined.
/// </summary>
internal static class ImpactScale
{
    /// <summary>The meter in an Impact cell, in pixels at 100%: each bar, the gap between bars, their height and the gap before the word.</summary>
    internal const int MeterBar = 10, MeterGap = 2, MeterHeight = 4, MeterTextGap = 7;

    /// <summary>"None", "Low", "Medium", "High".</summary>
    public static string Word(ImpactLevel level) => level switch
    {
        ImpactLevel.Low => "Low",
        ImpactLevel.Medium => "Medium",
        ImpactLevel.High => "High",
        _ => "None",
    };

    /// <summary>A level's word; empty when there is no level (not measured yet).</summary>
    public static string Text(ImpactLevel? level) => level is { } l ? Word(l) : "";

    public static ImpactLevel? Parse(string text) => Enum.TryParse<ImpactLevel>(text, out var level) && Enum.IsDefined(level) ? level : null;

    /// <summary>The colour of the meter's filled bars: the design's yellow, orange and red; null for None.</summary>
    public static Color? ColorOf(ImpactLevel level) => level switch
    {
        ImpactLevel.Low => UiColors.Low,
        ImpactLevel.Medium => UiColors.Medium,
        ImpactLevel.High => UiColors.High,
        _ => null,
    };

    /// <summary>How many of the three bars are filled: Low one, Medium two, High three, None none.</summary>
    public static int Filled(ImpactLevel level) => Math.Clamp((int)level, 0, 3);

    /// <summary>
    /// Draws the meter: three bars from <paramref name="first"/> (the first bar's place and size), <paramref name="gap"/>
    /// apart, rounded at the ends: <see cref="Filled"/> of them in the level's colour (none without a level), the rest in
    /// the track's grey. With <paramref name="highContrast"/>, the filled bars are in <paramref name="ink"/> and the rest
    /// outlined.
    /// </summary>
    /// <param name="ink">
    /// High contrast only: the text colour of what the meter sits on, so the bars read as its words do (a selected row's
    /// highlight text on the highlight); the window's text colour when null.
    /// </param>
    public static void PaintMeter(Graphics g, RectangleF first, float gap, ImpactLevel? level, bool highContrast, Color? ink = null)
    {
        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int filled = level is { } l ? Filled(l) : 0;
        for (int i = 0; i < 3; i++)
        {
            var bar = first with { X = first.X + i * (first.Width + gap) };
            bool on = i < filled;
            if (highContrast)
            {
                if (on) { using var b = new SolidBrush(ink ?? SystemColors.WindowText); g.FillRectangle(b, bar); }
                else { using var p = new Pen(SystemColors.GrayText); g.DrawRectangle(p, bar.X, bar.Y, bar.Width, bar.Height); }
                continue;
            }
            using var path = Rounded(bar, bar.Height / 2f);
            using var brush = new SolidBrush(on && level is { } shown && ColorOf(shown) is { } colour ? colour : UiColors.Track);
            g.FillPath(brush, path);
        }
        g.SmoothingMode = mode;
    }

    /// <summary>
    /// One Pool causes Impact cell: the background (the row's tone, or the selection), the meter (<see cref="MeterBar"/>), then
    /// the word, inside <paramref name="cell"/>. The meter
    /// and the word are in <paramref name="fore"/> under high contrast, the selection's text colour on a selected row.
    /// </summary>
    public static void DrawCell(Graphics g, Rectangle cell, string text, Font font, Color back, Color fore) =>
        DrawCell(g, cell, text, font, back, fore, SystemInformation.HighContrast);

    /// <summary><see cref="DrawCell(Graphics, Rectangle, string, Font, Color, Color)"/> with high contrast on or off (tests).</summary>
    internal static void DrawCell(Graphics g, Rectangle cell, string text, Font font, Color back, Color fore, bool highContrast)
    {
        using (var b = new SolidBrush(back)) g.FillRectangle(b, cell);
        if (Parse(text) is not { } level) return;
        var (meter, gap, words) = CellLayout(cell, font);
        PaintMeter(g, meter, gap, level, highContrast, ink: fore);
        TextRenderer.DrawText(g, text, font, words, fore, CellText);
    }

    private const TextFormatFlags CellText = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;

    /// <summary>Where an Impact cell draws its meter (the first bar, and the gap between bars) and its word, for a list in <paramref name="font"/>.</summary>
    internal static (RectangleF meter, float gap, Rectangle words) CellLayout(Rectangle cell, Font font)
    {
        float unit = Unit(font);
        float bar = MeterBar * unit, gap = MeterGap * unit, h = Math.Max(2f, MeterHeight * unit);
        float x = cell.Left + 6 * unit, y = cell.Top + (cell.Height - h) / 2f;
        int tx = (int)Math.Ceiling(x + 3 * bar + 2 * gap + MeterTextGap * unit);
        return (new RectangleF(x, y, bar, h), gap, new Rectangle(tx, cell.Top, Math.Max(0, cell.Right - tx), cell.Height));
    }

    /// <summary>
    /// The Impact column's width in a list in <paramref name="font"/>: <paramref name="least"/> (the design's width, scaled),
    /// or wider when the meter, the longest level word and a little room after it need more, so no word is ever cut to an
    /// ellipsis (at 100% the meter, "Medium" and the room after it take 108 px; the design has 96).
    /// </summary>
    public static int ColumnWidth(Font font, int least)
    {
        int start = CellLayout(Rectangle.Empty, font).words.X;
        // Measured as it is drawn, less the ellipsis (with it, a measure against no width is not the word's).
        int longest = Enum.GetValues<ImpactLevel>().Max(l => TextRenderer.MeasureText(Word(l), font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width);
        return Math.Max(least, start + longest + (int)Math.Ceiling(4 * Unit(font)));
    }

    /// <summary>The meter's scale in a list in <paramref name="font"/>: one at 100% (a 15 px line), more as the text grows.</summary>
    private static float Unit(Font font) => Math.Max(1f, font.Height / 15f);

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 1) { p.AddRectangle(r); return p; }
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
