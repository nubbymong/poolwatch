using System.Drawing.Drawing2D;

namespace PoolWatch.Ui.Setup;

/// <summary>The outline icons of the setup window and "How PoolWatch works", as the approved design draws them.</summary>
internal enum SetupIcon
{
    Pool, Sweep, Wrench, Layers, Chip, Windows, Shield, ShieldCheck, Gear, Clock, Gauge, Both, Power, Key, Chat, List, Later, Book, Check,
    /// <summary>The small arrow before "Why?": closed, and open.</summary>
    ChevronRight, ChevronDown,
}

/// <summary>
/// The icons' shapes, copied from the approved design's SVG symbols (a 20-unit square, or 16 for the check), and drawn
/// with GDI+ at any size: strokes are scaled with the icon, so they stay as thick as the design draws them.
/// </summary>
internal static class SetupIcons
{
    /// <summary>One shape of an icon: a path, a circle or a rounded rectangle, stroked (the default) or filled.</summary>
    private sealed record Part(string? Path = null, float[]? Circle = null, float[]? Rect = null, bool Fill = false, float Opacity = 1f);

    private sealed record Shape(float Box, float Stroke, IReadOnlyList<Part> Parts);

    private static Part P(string d, float opacity = 1f) => new(Path: d, Opacity: opacity);
    private static Part C(float x, float y, float r, bool fill = false) => new(Circle: [x, y, r], Fill: fill);
    private static Part R(float x, float y, float w, float h, float rx) => new(Rect: [x, y, w, h, rx]);

    private const string ShieldPath = "M10 2.5l6 2.3v4.7c0 3.8-2.6 6.6-6 8-3.4-1.4-6-4.2-6-8V4.8z";

    private static readonly Dictionary<SetupIcon, Shape> Shapes = new()
    {
        [SetupIcon.Pool] = new(20, 1.5f, [P("M2 8c2-1.2 3 1.2 5 0s3-1.2 5 0 3 1.2 6 0"), P("M2 13c2-1.2 3 1.2 5 0s3-1.2 5 0 3 1.2 6 0")]),
        [SetupIcon.Sweep] = new(20, 1.5f, [P("M3 15h14"), P("M5 15V9M9 15V5M13 15v-4"), P("M15 4l2 2-2 2")]),
        [SetupIcon.Wrench] = new(20, 1.5f, [P("M12.5 3a4 4 0 0 0-3.8 5.3L3.5 13.5l3 3 5.2-5.2A4 4 0 0 0 17 7.5l-2.3 2.3-2.4-.6-.6-2.4L14 4.5A4 4 0 0 0 12.5 3z")]),
        [SetupIcon.Layers] = new(20, 1.5f, [P("M10 3l7 3.5-7 3.5-7-3.5z"), P("M3 10l7 3.5 7-3.5", .7f), P("M3 13.5L10 17l7-3.5", .45f)]),
        [SetupIcon.Chip] = new(20, 1.5f, [R(5, 5, 10, 10, 1.5f), P("M8 2.5v2.5M12 2.5v2.5M8 15v2.5M12 15v2.5M2.5 8H5M2.5 12H5M15 8h2.5M15 12h2.5")]),
        [SetupIcon.Windows] = new(20, 1.5f, [R(3, 3, 6, 6, .5f), R(11, 3, 6, 6, .5f), R(3, 11, 6, 6, .5f), R(11, 11, 6, 6, .5f)]),
        [SetupIcon.Shield] = new(20, 1.5f, [P(ShieldPath)]),
        [SetupIcon.ShieldCheck] = new(20, 1.5f, [P(ShieldPath), P("M7 10l2.1 2.1L13.2 8")]),
        [SetupIcon.Gear] = new(20, 1.5f, [C(10, 10, 2.6f), P("M10 2.5v2.2M10 15.3v2.2M2.5 10h2.2M15.3 10h2.2M4.7 4.7l1.6 1.6M13.7 13.7l1.6 1.6M4.7 15.3l1.6-1.6M13.7 6.3l1.6-1.6")]),
        [SetupIcon.Clock] = new(20, 1.5f, [C(10, 10, 7), P("M10 5.5V10l3 2")]),
        [SetupIcon.Gauge] = new(20, 1.5f, [P("M3 14a7 7 0 0 1 14 0"), P("M10 14l3.5-4.5")]),
        [SetupIcon.Both] = new(20, 1.4f, [C(7, 8.5f, 4.6f), P("M7 5.8v2.7l1.8 1.2"), P("M10.5 16a4.5 4.5 0 0 1 9 0"), P("M15 16l2-2.4")]),
        [SetupIcon.Power] = new(20, 1.5f, [P("M10 3v6"), P("M6 5.5a6 6 0 1 0 8 0")]),
        [SetupIcon.Key] = new(20, 1.5f, [C(7, 10, 3.5f), P("M10.5 10H17M14.5 10v3M16.8 10v2")]),
        [SetupIcon.Chat] = new(20, 1.5f, [P("M3 4.5h14v9H9l-4 3v-3H3z")]),
        [SetupIcon.List] = new(20, 1.5f, [P("M7 5.5h10M7 10h10M7 14.5h10"), C(3.8f, 5.5f, 1.1f, fill: true), C(3.8f, 10, 1.1f, fill: true), C(3.8f, 14.5f, 1.1f, fill: true)]),
        [SetupIcon.Later] = new(20, 1.5f, [R(3, 4, 14, 13, 1.5f), P("M3 8h14M7 2.5v3M13 2.5v3")]),
        [SetupIcon.Book] = new(20, 1.4f, [P("M3 4.5c2.5-1 4.8-.8 7 .8v11c-2.2-1.6-4.5-1.8-7-.8zM17 4.5c-2.5-1-4.8-.8-7 .8v11c2.2-1.6 4.5-1.8 7-.8z")]),
        [SetupIcon.Check] = new(16, 2f, [P("M3.5 8.5l3 3 6-7")]),
        [SetupIcon.ChevronRight] = new(20, 2.4f, [P("M8 5.5l4.5 4.5L8 14.5")]),
        [SetupIcon.ChevronDown] = new(20, 2.4f, [P("M5.5 8l4.5 4.5L14.5 8")]),
    };

    /// <summary>Every icon's paths, read once (the tests read each, so a path that cannot be read fails there).</summary>
    private static readonly Lazy<Dictionary<string, GraphicsPath>> Paths = new(() =>
        Shapes.Values.SelectMany(s => s.Parts).Where(p => p.Path is not null).Select(p => p.Path!).Distinct().ToDictionary(d => d, SvgPath.Parse));

    /// <summary>Reads every icon's paths (the tests: a path that cannot be read throws here).</summary>
    internal static int PathCount { get { lock (Paths) return Paths.Value.Count; } }

    /// <summary>Draws <paramref name="icon"/> into <paramref name="area"/> (a square fits it best) in <paramref name="color"/>.</summary>
    public static void Draw(Graphics g, SetupIcon icon, RectangleF area, Color color)
    {
        // A GraphicsPath is used by one thread at a time (the tests draw from several).
        lock (Paths) Draw(g, Shapes[icon], area, color);
    }

    private static void Draw(Graphics g, Shape shape, RectangleF area, Color color)
    {
        float scale = Math.Min(area.Width, area.Height) / shape.Box;
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(area.X + (area.Width - shape.Box * scale) / 2, area.Y + (area.Height - shape.Box * scale) / 2);
        g.ScaleTransform(scale, scale);
        foreach (var part in shape.Parts)
        {
            var c = part.Opacity < 1 ? Color.FromArgb((int)(255 * part.Opacity), color) : color;
            using var pen = new Pen(c, shape.Stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            if (part.Path is { } d) g.DrawPath(pen, Paths.Value[d]);
            else if (part.Circle is [var x, var y, var r])
            {
                if (part.Fill)
                {
                    using var brush = new SolidBrush(c);
                    g.FillEllipse(brush, x - r, y - r, 2 * r, 2 * r);
                }
                else g.DrawEllipse(pen, x - r, y - r, 2 * r, 2 * r);
            }
            else if (part.Rect is [var rx, var ry, var w, var h, var radius])
            {
                using var rounded = SetupDraw.Rounded(new RectangleF(rx, ry, w, h), radius);
                g.DrawPath(pen, rounded);
            }
        }
        g.Restore(state);
    }
}
