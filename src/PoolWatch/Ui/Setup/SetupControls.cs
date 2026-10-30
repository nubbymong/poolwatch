using System.ComponentModel;
using System.Drawing.Drawing2D;
using PoolWatch.Core.Impact;

namespace PoolWatch.Ui.Setup;

/// <summary>
/// The setup window's colours: the app's one palette (<see cref="UiColors"/>) and the few only setup uses (its rail, its
/// cards' lines and dots); with Windows' high contrast on, the system's colours instead. Impact levels are drawn by the app's
/// one impact scale (<see cref="ImpactScale"/>).
/// </summary>
internal static class SetupTheme
{
    private static bool HighContrast => SystemInformation.HighContrast;
    private static Color Hex(int rgb) => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
    private static Color Pick(int rgb, Color system) => HighContrast ? system : Hex(rgb);
    private static Color Pick(Color design, Color system) => HighContrast ? system : design;

    public static Color Accent => Pick(UiColors.Accent, SystemColors.Highlight);
    public static Color OnAccent => HighContrast ? SystemColors.HighlightText : Color.White;
    public static Color AccentSoft => Pick(UiColors.AccentSoft, SystemColors.Window);
    public static Color AccentLine => Pick(0xb9d3ee, SystemColors.WindowFrame);
    public static Color Good => Pick(UiColors.Good, SystemColors.WindowText);
    public static Color GoodSoft => Pick(UiColors.GoodSoft, SystemColors.Window);
    public static Color GoodTile => Pick(0xe9f6ec, SystemColors.Window);
    public static Color Ink => Pick(UiColors.Ink, SystemColors.WindowText);
    public static Color Muted => Pick(UiColors.Muted, SystemColors.WindowText);
    public static Color Faint => Pick(UiColors.Faint, SystemColors.GrayText);
    public static Color Icon => Pick(UiColors.Icon, SystemColors.WindowText);
    public static Color Line => Pick(UiColors.Line, SystemColors.WindowFrame);
    public static Color RailLine => Pick(0xdde1e6, SystemColors.WindowFrame);
    public static Color Dot => Pick(0xc3c9d0, SystemColors.WindowFrame);
    public static Color Rail => Pick(UiColors.Panel, SystemColors.Control);
    public static Color Window => HighContrast ? SystemColors.Window : Color.White;
    public static Color Border => Pick(UiColors.Border, SystemColors.WindowFrame);
    public static Color Radio => Pick(0x8c959f, SystemColors.WindowText);
    public static Color Soft => Pick(0xf6f8fa, SystemColors.Window);
    public static Color Taskbar => Pick(0xeef1f5, SystemColors.Control);
    public static Color Error => Pick(0xb42318, SystemColors.WindowText);
    public static Color Empty => Pick(UiColors.Track, SystemColors.Control);
}

/// <summary>
/// How big setup draws: the design's pixels at 100% times the display's scaling, times the text size it was given (the
/// tests give a larger text to lay it out as at 125, 150 and 200%, on a test machine at 100%). Fonts are made in points
/// for the DPI Windows converts them with, so a window on a monitor scaled unlike the main display still matches.
/// </summary>
internal readonly record struct SetupScale(float Dpi, float Text, float FontDpi)
{
    public static SetupScale For(Control c, float text = 1f)
    {
        float screen;
        try
        {
            using var g = Graphics.FromHwnd(IntPtr.Zero);
            screen = g.DpiY;
        }
        catch (Exception) { screen = c.DeviceDpi; }
        return new SetupScale(c.DeviceDpi, text, screen);
    }

    /// <summary>The scale factor from the design's pixels to this window's.</summary>
    public float K => Dpi / 96f * Text;

    public int S(float logical) => (int)Math.Round(logical * K);

    /// <summary>
    /// A vertical scroll bar's width at this scale: Windows' width at this window's DPI, times the tests' larger text. The
    /// tests stand in for a higher scaling with that text (in a test host that is not DPI aware, where Windows gives the
    /// 100% width at any DPI), so they leave the room the real bar takes at that scaling (at 200%, twice the 100% one).
    /// </summary>
    public int ScrollBarWidth => (int)Math.Round(SystemInformation.GetVerticalScrollBarWidthForDpi((int)Dpi) * Text);

    /// <summary>A font of the design's <paramref name="px"/> pixels (Segoe UI; Semibold for headings and buttons).</summary>
    public Font Font(float px, bool semibold = false) =>
        new(semibold ? "Segoe UI Semibold" : "Segoe UI", px * 0.75f * Text * Dpi / FontDpi, FontStyle.Regular, GraphicsUnit.Point);
}

/// <summary>Every font setup uses, made once for one scale and disposed with the window (the controls never dispose them).</summary>
internal sealed class SetupFonts : IDisposable
{
    public SetupFonts(SetupScale s)
    {
        Body = s.Font(13);
        Small = s.Font(12.5f);
        Tiny = s.Font(12);
        Caps = s.Font(11.5f);
        Bold = s.Font(13.5f, semibold: true);
        Card = s.Font(14, semibold: true);
        H1 = s.Font(21, semibold: true);
        Safety = s.Font(23, semibold: true);
        Hero = s.Font(25, semibold: true);
        Mission = s.Font(15);
        Brand = s.Font(15, semibold: true);
        Promise = s.Font(14.5f);
        Dot = s.Font(10.5f, semibold: true);
        SmallBold = s.Font(12, semibold: true);
        Level = s.Font(13, semibold: true);
        Button = s.Font(13);
        ButtonBold = s.Font(13, semibold: true);
        Rail = s.Font(13);
        RailNow = s.Font(13, semibold: true);
    }

    public Font Body { get; }
    public Font Small { get; }
    public Font Tiny { get; }
    public Font Caps { get; }
    public Font Bold { get; }
    public Font Card { get; }
    public Font H1 { get; }
    public Font Safety { get; }
    public Font Hero { get; }
    public Font Mission { get; }
    public Font Brand { get; }
    public Font Promise { get; }
    public Font Dot { get; }
    public Font SmallBold { get; }
    public Font Level { get; }
    public Font Button { get; }
    public Font ButtonBold { get; }
    public Font Rail { get; }
    public Font RailNow { get; }

    public void Dispose()
    {
        foreach (var f in new[] { Body, Small, Tiny, Caps, Bold, Card, H1, Safety, Hero, Mission, Brand, Promise, Dot, SmallBold, Level, Button, ButtonBold, Rail, RailNow })
            f.Dispose();
    }
}

internal static class SetupDraw
{
    /// <summary>A rectangle with round corners of <paramref name="radius"/>.</summary>
    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void Fill(Graphics g, RectangleF r, float radius, Color fill)
    {
        using var path = Rounded(r, radius);
        using var brush = new SolidBrush(fill);
        g.FillPath(brush, path);
    }

    public static void Outline(Graphics g, RectangleF r, float radius, Color color, float width = 1)
    {
        // Inside the rectangle, so the whole line shows.
        var inside = RectangleF.Inflate(r, -width / 2, -width / 2);
        using var path = Rounded(inside, radius);
        using var pen = new Pen(color, width);
        g.DrawPath(pen, path);
    }

    /// <summary>
    /// One line of text whose baseline is at <paramref name="baseline"/> (as an SVG text element places it), starting at
    /// <paramref name="x"/>, or centred on it. <paramref name="font"/> is sized in pixels.
    /// </summary>
    public static void AtBaseline(Graphics g, string text, Font font, float x, float baseline, Color color, bool centred = false)
    {
        var family = font.FontFamily;
        float ascent = font.Size * family.GetCellAscent(font.Style) / family.GetEmHeight(font.Style);
        var flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        var size = TextRenderer.MeasureText(g, text, font, Size.Empty, flags);
        float left = centred ? x - size.Width / 2f : x;
        TextRenderer.DrawText(g, text, font, new Point((int)Math.Round(left), (int)Math.Round(baseline - ascent)), color, flags);
    }

    /// <summary>The size of one line of <paramref name="text"/>.</summary>
    public static Size Line(string text, Font font) => TextRenderer.MeasureText(text.Length == 0 ? " " : text, font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
}

/// <summary>
/// Wrapped text that draws itself, double-buffered (a new text never blanks it first), on whatever its parent paints
/// behind it. <see cref="Fit"/> gives it the height its text needs at a width, as setup lays out.
/// </summary>
internal class TextBlock : Control
{
    protected const TextFormatFlags Flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;

    public TextBlock()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint |
                 ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Color.Transparent;
        TabStop = false;
        AccessibleRole = AccessibleRole.StaticText;
    }

    /// <summary>The height the text needs at <paramref name="width"/> pixels.</summary>
    public int Measure(int width) => TextRenderer.MeasureText(Text.Length == 0 ? " " : Text, Font, new Size(Math.Max(1, width), int.MaxValue), Flags).Height;

    /// <summary>The width of the text on one line.</summary>
    public int OneLine => TextRenderer.MeasureText(Text.Length == 0 ? " " : Text, Font, Size.Empty, TextFormatFlags.NoPrefix).Width;

    /// <summary>As wide as <paramref name="width"/> and as tall as its text needs there; returns the height.</summary>
    public int Fit(int width)
    {
        Size = new Size(width, Measure(width));
        return Height;
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        AccessibleName = Text;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e) => TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, Flags);
}

/// <summary>A text that acts like a link: a click, or Enter or Space while it has the focus, raises Click.</summary>
internal sealed class LinkText : TextBlock
{
    private bool _hover;

    public LinkText()
    {
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.Link;
    }

    /// <summary>An icon before the text (the book of "How PoolWatch works"), drawn as high as the text.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] // set in code, never by a designer
    public SetupIcon? Icon { get; set; }

    private int IconWidth => Icon is null ? 0 : Font.Height + Font.Height / 3;

    public new int OneLine => base.OneLine + IconWidth;

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space) { e.Handled = true; OnClick(EventArgs.Empty); }
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnPaint(PaintEventArgs e)
    {
        var area = ClientRectangle;
        if (Icon is { } icon)
        {
            int h = Font.Height;
            SetupIcons.Draw(e.Graphics, icon, new RectangleF(0, (Math.Min(area.Height, h) - h * 0.9f) / 2, h * 0.9f, h * 0.9f), ForeColor);
            area = Rectangle.FromLTRB(IconWidth, 0, area.Right, area.Bottom);
        }
        using var font = _hover || Focused ? new Font(Font, FontStyle.Underline) : null;
        TextRenderer.DrawText(e.Graphics, Text, font ?? Font, area, ForeColor, Flags);
    }
}

/// <summary>
/// The accessible object of a control drawn as a choice (a switch, a choice card, a Yes / No): it reports Checked to screen
/// readers while <paramref name="isChecked"/> says so, as a check box or radio button does.
/// </summary>
internal sealed class CheckedAccessibleObject(Control owner, Func<bool> isChecked) : Control.ControlAccessibleObject(owner)
{
    public override AccessibleStates State => isChecked() ? base.State | AccessibleStates.Checked : base.State & ~AccessibleStates.Checked;
}

/// <summary>One of setup's outline icons.</summary>
internal sealed class IconBox : Control
{
    public IconBox(SetupIcon icon, Color? color = null)
    {
        Icon = icon;
        IconColor = color ?? SetupTheme.Icon;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint |
                 ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Color.Transparent;
        TabStop = false;
        AccessibleRole = AccessibleRole.Graphic;
    }

    public SetupIcon Icon { get; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] // set in code, never by a designer
    public Color IconColor { get; set; }

    protected override void OnPaint(PaintEventArgs e) => SetupIcons.Draw(e.Graphics, Icon, ClientRectangle, IconColor);
}

/// <summary>The app's own icon (the one the main window uses), drawn at this control's size from its best frame.</summary>
internal sealed class AppMark : Control
{
    private readonly Icon _source;
    private Icon? _sized;

    public AppMark(Icon source)
    {
        _source = source;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint |
                 ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Color.Transparent;
        TabStop = false;
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "PoolWatch";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 0 || Height <= 0) return;
        if (_sized is null || _sized.Width != Width || _sized.Height != Height)
        {
            _sized?.Dispose();
            _sized = new Icon(_source, Width, Height);
        }
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        e.Graphics.DrawIcon(_sized, ClientRectangle);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _sized?.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// A panel that paints a rounded box behind its children: a fill, a border, or both (the design's cards, the green
/// safety line, the "Why?" box, the tag above the Services step).
/// </summary>
internal class RoundedBox : Panel
{
    public RoundedBox()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] // set in code, never by a designer
    public Color? Fill { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] // set in code, never by a designer
    public Color? Border { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] // set in code, never by a designer
    public float BorderWidth { get; set; } = 1;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] // set in code, never by a designer
    public float Radius { get; set; }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0, 0, Width, Height);
        if (Fill is { } fill) SetupDraw.Fill(e.Graphics, r, Radius, fill);
        if (Border is { } border) SetupDraw.Outline(e.Graphics, r, Radius, border, BorderWidth);
    }
}

/// <summary>A filled circle with a white tick (the Safety step's promises), or an outline one with a number (the Services step's flow).</summary>
internal sealed class Dot : Control
{
    public Dot(Color fill, Color mark, string? number = null)
    {
        DotFill = fill;
        Mark = mark;
        Number = number;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint |
                 ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Color.Transparent;
        TabStop = false;
    }

    public Color DotFill { get; }
    public Color Mark { get; }
    public string? Number { get; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var b = new SolidBrush(DotFill)) g.FillEllipse(b, 0, 0, Width - 1, Height - 1);
        if (Number is { } n)
            TextRenderer.DrawText(g, n, Font, ClientRectangle, Mark, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        else
        {
            float s = Width * 0.54f;
            SetupIcons.Draw(g, SetupIcon.Check, new RectangleF((Width - s) / 2, (Height - s) / 2, s, s), Mark);
        }
    }
}

/// <summary>
/// The left rail: the app's mark and name, then the seven steps, each a dot (a tick once done, filled for the one shown)
/// joined by a line, as the design draws them. It paints itself from the flow, so moving to a step only repaints it.
/// </summary>
internal sealed class StepRail : Control
{
    private readonly SetupFlow _flow;
    private readonly SetupScale _s;
    private readonly SetupFonts _fonts;
    private readonly Icon _icon;
    private Icon? _mark;

    public StepRail(SetupFlow flow, SetupScale scale, SetupFonts fonts, Icon icon)
    {
        _flow = flow;
        _s = scale;
        _fonts = fonts;
        _icon = icon;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        AccessibleRole = AccessibleRole.List;
        AccessibleName = "Setup steps";
    }

    /// <summary>The rail's width at this scale (the design's 200 pixels).</summary>
    public int RailWidth => _s.S(200);

    /// <summary>The height the brand and every step need.</summary>
    public int Needed => _s.S(82 + 38 * SetupFlow.Steps.Count + 24);

    /// <summary>The widest step name fits beside its dot.</summary>
    internal int TextRoom => RailWidth - _s.S(50 + 18);

    internal int WidestStepName => SetupFlow.Steps.Max(st => SetupDraw.Line(SetupText.Rail(st), _fonts.RailNow).Width);

    public void ShowStep() { AccessibleDescription = $"Step {(int)_flow.Step} of {SetupFlow.Steps.Count}: {SetupText.Rail(_flow.Step)}"; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var bg = new SolidBrush(SetupTheme.Rail)) g.FillRectangle(bg, ClientRectangle);
        using (var edge = new Pen(SetupTheme.Line)) g.DrawLine(edge, Width - 1, 0, Width - 1, Height);

        int left = _s.S(18), top = _s.S(24), mark = _s.S(28);
        if (_mark is null || _mark.Width != mark)
        {
            _mark?.Dispose();
            _mark = new Icon(_icon, mark, mark);
        }
        g.DrawIcon(_mark, new Rectangle(left, top, mark, mark));
        TextRenderer.DrawText(g, SetupText.Brand, _fonts.Brand, new Rectangle(left + mark + _s.S(10), top, Width, mark), SetupTheme.Ink,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

        float dot = _s.K * 20, row = _s.K * 38, y0 = _s.K * 82;
        for (int i = 0; i < SetupFlow.Steps.Count; i++)
        {
            var step = SetupFlow.Steps[i];
            var state = _flow.Mark(step);
            float y = y0 + i * row, cx = left + dot / 2, cy = y + row / 2;
            if (i < SetupFlow.Steps.Count - 1)
            {
                using var join = new Pen(state == RailMark.Done ? SetupTheme.AccentLine : SetupTheme.RailLine, _s.K * 2);
                g.DrawLine(join, cx, cy + dot / 2, cx, cy + row - dot / 2);
            }
            var circle = new RectangleF(cx - dot / 2 + _s.K * 0.75f, cy - dot / 2 + _s.K * 0.75f, dot - _s.K * 1.5f, dot - _s.K * 1.5f);
            var (fill, ring, ink) = state switch
            {
                RailMark.Done => (SetupTheme.AccentSoft, SetupTheme.AccentLine, SetupTheme.Accent),
                RailMark.Now => (SetupTheme.Accent, SetupTheme.Accent, SetupTheme.OnAccent),
                _ => (SetupTheme.Window, SetupTheme.Dot, SetupTheme.Muted),
            };
            using (var b = new SolidBrush(fill)) g.FillEllipse(b, circle);
            using (var p = new Pen(ring, _s.K * 1.5f)) g.DrawEllipse(p, circle);
            var box = Rectangle.Round(new RectangleF(cx - dot / 2, cy - dot / 2, dot, dot));
            if (state == RailMark.Done) SetupIcons.Draw(g, SetupIcon.Check, RectangleF.Inflate(box, -_s.K * 4.5f, -_s.K * 4.5f), ink);
            else TextRenderer.DrawText(g, ((int)step).ToString(System.Globalization.CultureInfo.InvariantCulture), _fonts.Dot, box, ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            var text = new Rectangle(left + (int)dot + _s.S(12), (int)y, Width - left - (int)dot - _s.S(12), (int)row);
            TextRenderer.DrawText(g, SetupText.Rail(step), state == RailMark.Now ? _fonts.RailNow : _fonts.Rail, text,
                state == RailMark.Ahead ? SetupTheme.Muted : SetupTheme.Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _mark?.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// An impact row's level on the right: a three-segment meter in the level's own colour (yellow Low, orange Medium, red
/// High; empty for None), the level in words beside it (None in green), and under it where setup fixes it or "Still
/// watching". Meters and words line up in one column, whatever the level.
/// </summary>
internal sealed class ImpactMeter : Control
{
    private readonly SetupScale _s;
    private readonly SetupFonts _fonts;
    private ImpactLevel? _level;
    private string? _under;

    public ImpactMeter(SetupScale scale, SetupFonts fonts)
    {
        _s = scale;
        _fonts = fonts;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint |
                 ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Color.Transparent;
        TabStop = false;
        AccessibleRole = AccessibleRole.StaticText;
    }

    public ImpactLevel? Level => _level;
    public string LevelText => _level is { } l ? SetupText.Level(l) : SetupText.CheckingNow;
    public string? Under => _under;

    /// <summary>
    /// The design's column, 160 pixels with the meter in its first 50 and the words after it, or wider when the longest
    /// words a level or the line under it can have need it: every row's column is the same, so they line up.
    /// </summary>
    public int MeterWidth
    {
        get
        {
            int words = new[] { ImpactLevel.High, ImpactLevel.Medium, ImpactLevel.Low, ImpactLevel.None }.Select(SetupText.Level).Append(SetupText.CheckingNow)
                .Max(t => SetupDraw.Line(t, _fonts.Level).Width);
            int under = new[] { SetupText.FixIn(SetupStep.StandbyCache), SetupText.TrimInApp, SetupText.StillWatching }.Max(t => SetupDraw.Line(t, _fonts.Tiny).Width);
            return Math.Max(_s.S(160), _s.S(60) + Math.Max(words, under) + _s.S(2));
        }
    }

    public int Needed => _fonts.Body.Height + (_under is null ? 0 : _s.S(3) + _fonts.Tiny.Height);

    /// <summary>Whether its words fit the column.</summary>
    internal bool Fits => SetupDraw.Line(LevelText, _fonts.Level).Width <= Width - _s.S(60) + 1 &&
                          (_under is null || SetupDraw.Line(_under, _fonts.Tiny).Width <= Width - _s.S(60) + 1);

    public void Show(ImpactLevel? level, string? under)
    {
        if (level == _level && under == _under) return;
        _level = level;
        _under = under;
        AccessibleName = under is null ? LevelText : $"{LevelText}. {under}";
        Height = Needed;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int line = _fonts.Body.Height, textX = _s.S(60);
        float seg = _s.K * 14, gap = _s.K * 3, h = _s.K * 5, y = (line - h) / 2f;
        // The app's one impact meter, at setup's size (the same colours and bars as Pool causes and the cost meters).
        ImpactScale.PaintMeter(g, new RectangleF(_s.K * 2, y, seg, h), gap, _level, SystemInformation.HighContrast);
        var wordColour = _level switch { ImpactLevel.None => SetupTheme.Good, null => SetupTheme.Muted, _ => SetupTheme.Ink };
        TextRenderer.DrawText(g, LevelText, _fonts.Level, new Rectangle(textX, 0, Width - textX, line), wordColour,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        if (_under is { } under)
            TextRenderer.DrawText(g, under, _fonts.Tiny, new Rectangle(textX, line + _s.S(3), Width - textX, _fonts.Tiny.Height),
                _level is ImpactLevel.None ? SetupTheme.Muted : SetupTheme.Accent, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }
}

/// <summary>
/// A choice drawn as a card with a radio dot: the Welcome step's Simple and Advanced, and the four Standby cache choices.
/// The chosen one has a blue border. A click anywhere on it (its texts included), or Space or Enter while it has the
/// focus, raises <see cref="Chosen"/>; the arrow keys move to the next card of its group.
/// </summary>
internal sealed class ChoiceCard : Panel
{
    private readonly SetupScale _s;
    private bool _selected;

    public ChoiceCard(SetupScale scale, string title)
    {
        _s = scale;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint |
                 ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.RadioButton;
        AccessibleName = title;
    }

    public event Action? Chosen;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] // set in code, never by a designer
    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            AccessibleDescription = value ? "Chosen" : null;
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
            Invalidate();
        }
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new CheckedAccessibleObject(this, () => _selected);

    /// <summary>Where the radio dot is drawn (the card's own coordinates).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] // set in code, never by a designer
    public Point RadioAt { get; set; }

    /// <summary>A text on the card: a click on it chooses the card too.</summary>
    public void Clickable(Control c)
    {
        c.Click += (_, _) => { Focus(); Chosen?.Invoke(); };
        c.Cursor = Cursors.Hand;
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Focus();
        Chosen?.Invoke();
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Space or Keys.Enter || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter) { e.Handled = true; Chosen?.Invoke(); return; }
        if (e.KeyCode is not (Keys.Left or Keys.Right or Keys.Up or Keys.Down) || Parent is null) return;
        var group = Parent.Controls.OfType<ChoiceCard>().ToList();
        int i = group.IndexOf(this) + (e.KeyCode is Keys.Left or Keys.Up ? -1 : 1);
        if (i < 0 || i >= group.Count) return;
        e.Handled = true;
        group[i].Focus();
        group[i].Chosen?.Invoke();
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0, 0, Width, Height);
        SetupDraw.Fill(g, r, _s.K * 8, SetupTheme.Window);
        SetupDraw.Outline(g, r, _s.K * 8, _selected ? SetupTheme.Accent : SetupTheme.Line, _s.K * (_selected ? 2 : 1));
        float d = _s.K * 18, w = _s.K * 1.5f;
        var radio = new RectangleF(RadioAt.X + w / 2, RadioAt.Y + w / 2, d - w, d - w);
        using (var bg = new SolidBrush(SetupTheme.Window)) g.FillEllipse(bg, radio);
        using (var ring = new Pen(_selected ? SetupTheme.Accent : SetupTheme.Radio, w)) g.DrawEllipse(ring, radio);
        if (_selected)
        {
            float inner = _s.K * 9;
            using var fill = new SolidBrush(SetupTheme.Accent);
            g.FillEllipse(fill, RadioAt.X + (d - inner) / 2, RadioAt.Y + (d - inner) / 2, inner, inner);
        }
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -_s.S(3), -_s.S(3)));
    }
}

/// <summary>
/// The Yes / No choice of the Codex shortcut: two halves, the chosen one filled blue. For a screen reader it is a check
/// button named after its question, checked for Yes.
/// </summary>
internal sealed class YesNo : Control
{
    private readonly SetupScale _s;
    private readonly SetupFonts _fonts;
    private readonly string _name;
    private bool _yes = true;

    public YesNo(SetupScale scale, SetupFonts fonts, string name)
    {
        _s = scale;
        _fonts = fonts;
        _name = name;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint |
                 ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.CheckButton;
        Size = Needed;
        Describe();
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new CheckedAccessibleObject(this, () => _yes);

    public event Action<bool>? Changed;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] // set in code, never by a designer
    public bool Value
    {
        get => _yes;
        set
        {
            if (_yes == value) return;
            _yes = value;
            Describe();
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
            Invalidate();
        }
    }

    private void Describe()
    {
        AccessibleName = $"{_name}: {(_yes ? SetupText.Yes : SetupText.No)}";
        AccessibleDescription = _yes ? SetupText.Yes : SetupText.No;
    }

    private int Half => Math.Max(SetupDraw.Line(SetupText.Yes, _fonts.SmallBold).Width, SetupDraw.Line(SetupText.No, _fonts.SmallBold).Width) + _s.S(28);

    public Size Needed => new(2 * Half + _s.S(1), _fonts.Small.Height + _s.S(8));

    private void Set(bool yes)
    {
        if (yes == _yes) return;
        Value = yes;
        Changed?.Invoke(yes);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        Set(e.X < Width / 2);
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Left) Set(true);
        else if (e.KeyCode == Keys.Right) Set(false);
        else if (e.KeyCode == Keys.Space) Set(!_yes);
        else return;
        e.Handled = true;
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0, 0, Width, Height);
        float radius = _s.K * 5, mid = Width / 2f;
        SetupDraw.Fill(g, r, radius, SetupTheme.Window);
        using (var clip = SetupDraw.Rounded(r, radius))
        {
            var state = g.Save();
            g.SetClip(clip);
            using var fill = new SolidBrush(SetupTheme.Accent);
            g.FillRectangle(fill, _yes ? new RectangleF(0, 0, mid, Height) : new RectangleF(mid, 0, Width - mid, Height));
            g.Restore(state);
        }
        SetupDraw.Outline(g, r, radius, SetupTheme.Border);
        using (var sep = new Pen(SetupTheme.Border)) g.DrawLine(sep, mid, 0, mid, Height);
        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(g, SetupText.Yes, _yes ? _fonts.SmallBold : _fonts.Small, new Rectangle(0, 0, (int)mid, Height), _yes ? SetupTheme.OnAccent : SetupTheme.Ink, flags);
        TextRenderer.DrawText(g, SetupText.No, _yes ? _fonts.Small : _fonts.SmallBold, new Rectangle((int)mid, 0, Width - (int)mid, Height), _yes ? SetupTheme.Ink : SetupTheme.OnAccent, flags);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -_s.S(2), -_s.S(2)));
    }
}

/// <summary>A switch with "On" or "Off" written before it: starting at sign-in and the token-leak guard.</summary>
internal sealed class Toggle : Control
{
    private readonly SetupScale _s;
    private readonly SetupFonts _fonts;
    private bool _on;

    public Toggle(SetupScale scale, SetupFonts fonts, string name)
    {
        _s = scale;
        _fonts = fonts;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint |
                 ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.CheckButton;
        Size = Needed;
        _name = name;
        Describe();
    }

    private readonly string _name;

    public event Action<bool>? Changed;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] // set in code, never by a designer
    public bool On
    {
        get => _on;
        set
        {
            if (_on == value) return;
            _on = value;
            Describe();
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
            Invalidate();
        }
    }

    private void Describe()
    {
        AccessibleName = $"{_name}: {(_on ? SetupText.On : SetupText.Off)}";
        AccessibleDescription = _on ? "On" : "Off";
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new CheckedAccessibleObject(this, () => _on);

    private int Label => Math.Max(SetupDraw.Line(SetupText.On, _fonts.Small).Width, SetupDraw.Line(SetupText.Off, _fonts.Small).Width);

    public Size Needed => new(Label + _s.S(10 + 40), Math.Max(_s.S(20), _fonts.Small.Height));

    private void Flip()
    {
        On = !_on;
        Changed?.Invoke(_on);
    }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); Flip(); }
    protected override bool IsInputKey(Keys keyData) => keyData == Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode != Keys.Space) return;
        e.Handled = true;
        Flip();
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        TextRenderer.DrawText(g, _on ? SetupText.On : SetupText.Off, _fonts.Small, new Rectangle(0, 0, Label, Height), SetupTheme.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        float w = _s.K * 40, h = _s.K * 20, x = Width - w - _s.K, y = (Height - h) / 2, line = _s.K * 1.5f;
        var track = new RectangleF(x, y, w, h);
        if (_on) SetupDraw.Fill(g, track, h / 2, SetupTheme.Accent);
        SetupDraw.Outline(g, track, h / 2, _on ? SetupTheme.Accent : SetupTheme.Radio, line);
        float knob = _s.K * 11, kx = _on ? x + w - _s.K * 6 - knob : x + _s.K * 5;
        using (var b = new SolidBrush(_on ? SetupTheme.OnAccent : SetupTheme.Icon)) g.FillEllipse(b, kx, y + (h - knob) / 2, knob, knob);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Round(RectangleF.Inflate(track, _s.K * 2, _s.K * 2)));
    }
}

/// <summary>A button drawn as the design draws it: the primary one filled blue, the others white with a grey border.</summary>
internal sealed class SetupButton : Button
{
    private readonly SetupScale _s;
    private readonly SetupFonts _fonts;
    private bool _primary, _hover, _down;

    public SetupButton(SetupScale scale, SetupFonts fonts, string text, bool primary, bool small = false)
    {
        _s = scale;
        _fonts = fonts;
        Small = small;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        FlatStyle = FlatStyle.Flat;
        UseVisualStyleBackColor = false;
        Text = text;
        Primary = primary;
    }

    public bool Small { get; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] // set in code, never by a designer
    public bool Primary
    {
        get => _primary;
        set
        {
            _primary = value;
            Font = value ? _fonts.ButtonBold : Small ? _fonts.Small : _fonts.Button;
            Invalidate();
        }
    }

    /// <summary>The size its text needs: the design's padding, and at least 96 pixels wide for a footer button.</summary>
    public Size Needed
    {
        get
        {
            var text = SetupDraw.Line(Text, Font);
            int padX = _s.S(Small ? 12 : 18), padY = _s.S(Small ? 5 : 7);
            return new Size(Math.Max(text.Width + 2 * padX, Small ? 0 : _s.S(96)), text.Height + 2 * padY + _s.S(2));
        }
    }

    public override Size GetPreferredSize(Size proposedSize) => Needed;

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _down = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _down = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _down = false; Invalidate(); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor is { A: 255 } back ? back : SetupTheme.Window);
        var r = new RectangleF(0, 0, Width, Height);
        float radius = _s.K * 5;
        Color fill, border, ink;
        if (!Enabled) (fill, border, ink) = (SetupTheme.Soft, SetupTheme.Line, SetupTheme.Faint);
        else if (_primary)
        {
            var accent = SetupTheme.Accent;
            fill = _down ? ControlPaint.Dark(accent, 0.1f) : _hover ? ControlPaint.Dark(accent, 0.05f) : accent;
            (border, ink) = (fill, SetupTheme.OnAccent);
        }
        else (fill, border, ink) = (_down ? SetupTheme.Line : _hover ? SetupTheme.Soft : SetupTheme.Window, SetupTheme.Border, SetupTheme.Ink);
        SetupDraw.Fill(g, r, radius, fill);
        SetupDraw.Outline(g, r, radius, border);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -_s.S(3), -_s.S(3)), ink, fill);
    }
}

/// <summary>
/// The Standby cache step's picture: the kernel pool (paged + non-paged) rising, and dropping each time the cache is
/// emptied, on the hour or at the chosen size (an illustration, the design's shape; its labels carry the values chosen).
/// </summary>
internal sealed class CacheChart : Control
{
    private static readonly PointF[] Pool =
        [new(20, 98), new(150, 76), new(150, 100), new(230, 80), new(262, 36), new(262, 99), new(370, 74), new(370, 100), new(470, 80), new(520, 58), new(520, 99), new(596, 88)];
    private static readonly float[] Marks = [150, 262, 370, 520];
    private readonly SetupScale _s;
    private string _hours = "1 h", _gb = "8 GB";

    public CacheChart(SetupScale scale)
    {
        _s = scale;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        AccessibleRole = AccessibleRole.Chart;
        AccessibleName = "Kernel pool, paged plus non-paged, rising and dropping at each emptying";
    }

    /// <summary>The chart's height inside its frame (the design's 124 pixels).</summary>
    public int ChartHeight => _s.S(124);

    public void Show(double hours, double gb)
    {
        string h = $"{SetupText.Number(hours)} h", g = SetupText.Gb(gb);
        if (h == _hours && g == _gb) return;
        (_hours, _gb) = (h, g);
        Invalidate();
    }

    /// <summary>The labels as drawn now (the tests).</summary>
    internal IReadOnlyList<string> Labels => [SetupText.ChartLegend, $"your limit, {_gb}", $"emptied, {_hours}", $"emptied, {_gb}"];

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(SetupTheme.Window);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // The design's 600 x 124 picture, scaled to fit and centred.
        float k = Math.Min(Width / 600f, Height / 124f), ox = (Width - 600 * k) / 2, oy = (Height - 124 * k) / 2;
        PointF P(float x, float y) => new(ox + x * k, oy + y * k);
        using var font = new Font("Segoe UI", 11.5f * k, GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", 11f * k, GraphicsUnit.Pixel);
        var accent = SetupTheme.Accent;
        var warn = HighContrastOr(UiColors.Warn);
        using (var legend = new Pen(accent, 2.2f * k)) g.DrawLine(legend, P(4, 10), P(20, 10));
        SetupDraw.AtBaseline(g, SetupText.ChartLegend, font, P(26, 14).X, P(26, 14).Y, SetupTheme.Ink);
        using (var dashed = new Pen(warn, 1.4f * k) { DashPattern = [4f / 1.4f, 3f / 1.4f] }) g.DrawLine(dashed, P(230, 10), P(246, 10));
        SetupDraw.AtBaseline(g, $"your limit, {_gb}", font, P(252, 14).X, P(252, 14).Y, SetupTheme.Muted);
        using (var limit = new Pen(warn, 1.3f * k) { DashPattern = [5f / 1.3f, 4f / 1.3f] }) g.DrawLine(limit, P(20, 36), P(596, 36));
        using (var axis = new Pen(SetupTheme.Line, k)) g.DrawLine(axis, P(20, 106), P(596, 106));
        using (var line = new Pen(accent, 2.2f * k) { LineJoin = LineJoin.Round }) g.DrawLines(line, Pool.Select(p => P(p.X, p.Y)).ToArray());
        using var dot = new SolidBrush(SetupTheme.Icon);
        for (int i = 0; i < Marks.Length; i++)
        {
            var c = P(Marks[i], 106);
            g.FillEllipse(dot, c.X - 3 * k, c.Y - 3 * k, 6 * k, 6 * k);
            SetupDraw.AtBaseline(g, $"emptied, {(i == 1 ? _gb : _hours)}", small, c.X, P(0, 121).Y, SetupTheme.Muted, centred: true);
        }
    }

    private static Color HighContrastOr(Color c) => SystemInformation.HighContrast ? SystemColors.WindowText : c;
}

/// <summary>
/// "How PoolWatch works"'s picture: the files your agents touch go into the standby cache, ReFS keeps a kernel pool record
/// for each, and emptying the standby cache frees both (the design's 800 x 128 drawing, scaled to fit).
/// </summary>
internal sealed class ExplainerDiagram : Control
{
    public ExplainerDiagram()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        AccessibleRole = AccessibleRole.Diagram;
        AccessibleName = "Files your agents touch are kept in the standby cache, and ReFS keeps a kernel pool record for each; emptying the cache frees both";
    }

    /// <summary>Its words, as drawn (the CI UI test checks them).</summary>
    internal static readonly IReadOnlyList<string> Words =
    [
        "Files your agents touch", "git, rg, builds, worktrees", "Standby cache", "spare copies of files on disk", "ReFS keeps", "Kernel pool",
        "a record for each cached file", "Emptying the standby cache frees both",
    ];

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(SetupTheme.Window);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float k = Math.Min(Width / 800f, Height / 128f), ox = (Width - 800 * k) / 2, oy = (Height - 128 * k) / 2;
        PointF P(float x, float y) => new(ox + x * k, oy + y * k);
        RectangleF R(float x, float y, float w, float h) => new(ox + x * k, oy + y * k, w * k, h * k);
        using var title = new Font("Segoe UI Semibold", 14 * k, GraphicsUnit.Pixel);
        using var sub = new Font("Segoe UI", 12 * k, GraphicsUnit.Pixel);
        using var label = new Font("Segoe UI", 11 * k, GraphicsUnit.Pixel);
        using var caption = new Font("Segoe UI Semibold", 12.5f * k, GraphicsUnit.Pixel);
        var accent = SetupTheme.Accent;
        var grey = SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(0x8c, 0x95, 0x9f);

        void Box(float x, float w, Color fill, Color border, float line, string top, string bottom)
        {
            var r = R(x, 10, w, 70);
            SetupDraw.Fill(g, r, 8 * k, fill);
            SetupDraw.Outline(g, r, 8 * k, border, line * k);
            var centre = P(x + w / 2, 0).X;
            SetupDraw.AtBaseline(g, top, title, centre, P(0, 40).Y, SetupTheme.Ink, centred: true);
            SetupDraw.AtBaseline(g, bottom, sub, centre, P(0, 60).Y, SetupTheme.Muted, centred: true);
        }

        void Arrow(float from, float to)
        {
            using var pen = new Pen(grey, 1.6f * k);
            g.DrawLine(pen, P(from, 45), P(to, 45));
            g.DrawLines(pen, [P(to - 6, 39), P(to + 2, 45), P(to - 6, 51)]);
        }

        Box(6, 200, SetupTheme.Soft, SystemInformation.HighContrast ? SystemColors.WindowFrame : UiColors.DisabledBorder, 1, Words[0], Words[1]);
        Arrow(214, 286);
        Box(294, 210, SetupTheme.Window, accent, 1.5f, Words[2], Words[3]);
        Arrow(512, 584);
        SetupDraw.AtBaseline(g, Words[4], label, P(548, 0).X, P(0, 35).Y, SetupTheme.Muted, centred: true);
        Box(592, 202, SetupTheme.Window, accent, 1.5f, Words[5], Words[6]);
        using (var bracket = new Pen(accent, 1.5f * k)) g.DrawLines(bracket, [P(300, 92), P(300, 102), P(788, 102), P(788, 92)]);
        SetupDraw.AtBaseline(g, Words[7], caption, P(544, 0).X, P(0, 121).Y, accent, centred: true);
    }
}

/// <summary>
/// The Ready step's picture of the notification area by the clock, with PoolWatch's icon picked out: the language and the
/// clock show this PC's own (the time when the step opened, <see cref="ShowTime"/>).
/// </summary>
internal sealed class TrayPicture : Control
{
    private readonly SetupScale _s;
    private readonly SetupFonts _fonts;
    private readonly Icon _icon;
    private Icon? _sized;
    private readonly string _language;
    private string _time = "", _date = "";

    public TrayPicture(SetupScale scale, SetupFonts fonts, Icon icon, DateTime now)
    {
        _s = scale;
        _fonts = fonts;
        _icon = icon;
        _language = Language();
        SetTime(now);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint |
                 ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Color.Transparent;
        TabStop = false;
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "PoolWatch's icon in the notification area, next to the clock";
        Size = Needed;
    }

    /// <summary>The clock as drawn now: the time and the date (the tests).</summary>
    internal (string Time, string Date) Clock => (_time, _date);

    /// <summary>The clock at <paramref name="now"/>: the Ready step sets it each time it opens.</summary>
    public void ShowTime(DateTime now)
    {
        SetTime(now);
        Size = Needed;
        Invalidate();
    }

    private void SetTime(DateTime now)
    {
        _time = now.ToString("t", System.Globalization.CultureInfo.CurrentCulture);
        _date = now.ToString("d", System.Globalization.CultureInfo.CurrentCulture);
    }

    /// <summary>"ENG": the keyboard language Windows shows there; empty when it cannot be read.</summary>
    private static string Language()
    {
        try { return InputLanguage.CurrentInputLanguage.Culture.ThreeLetterISOLanguageName.ToUpperInvariant(); }
        catch (Exception) { return ""; }
    }

    private int ClockWidth => Math.Max(SetupDraw.Line(_time, _fonts.Caps).Width, SetupDraw.Line(_date, _fonts.Caps).Width);

    public Size Needed => new(_s.S(12 + 8 + 12 + 28 + 12) + SetupDraw.Line("^", _fonts.Caps).Width + SetupDraw.Line(_language, _fonts.Caps).Width + ClockWidth + _s.S(12),
        Math.Max(_s.S(40), 2 * _fonts.Caps.Height + _s.S(12)));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        SetupDraw.Fill(g, new RectangleF(0, 0, Width, Height), _s.K * 6, SetupTheme.Taskbar);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        int x = _s.S(12);
        int caret = SetupDraw.Line("^", _fonts.Caps).Width;
        TextRenderer.DrawText(g, "^", _fonts.Caps, new Rectangle(x, 0, caret, Height), SetupTheme.Ink, flags);
        x += caret + _s.S(12);
        int tile = _s.S(28), icon = _s.S(18);
        var box = new RectangleF(x, (Height - tile) / 2f, tile, tile);
        SetupDraw.Fill(g, box, _s.K * 5, SetupTheme.Window);
        SetupDraw.Outline(g, RectangleF.Inflate(box, _s.K, _s.K), _s.K * 6, SetupTheme.Accent, _s.K * 2);
        if (_sized is null || _sized.Width != icon)
        {
            _sized?.Dispose();
            _sized = new Icon(_icon, icon, icon);
        }
        g.DrawIcon(_sized, new Rectangle(x + (tile - icon) / 2, (Height - icon) / 2, icon, icon));
        x += tile + _s.S(12);
        int lang = SetupDraw.Line(_language, _fonts.Caps).Width;
        TextRenderer.DrawText(g, _language, _fonts.Caps, new Rectangle(x, 0, lang, Height), SetupTheme.Ink, flags);
        x += lang + _s.S(12);
        int line = _fonts.Caps.Height, top = (Height - 2 * line) / 2;
        TextRenderer.DrawText(g, _time, _fonts.Caps, new Rectangle(x, top, ClockWidth, line), SetupTheme.Ink, TextFormatFlags.Right | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, _date, _fonts.Caps, new Rectangle(x, top + line, ClockWidth, line), SetupTheme.Ink, TextFormatFlags.Right | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _sized?.Dispose();
        base.Dispose(disposing);
    }
}
