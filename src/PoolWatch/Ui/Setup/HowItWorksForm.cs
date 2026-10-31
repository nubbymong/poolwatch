namespace PoolWatch.Ui.Setup;

/// <summary>
/// "How PoolWatch works", from every setup step's footer: the picture, the kernel pool, why it grows with AI agents, and
/// in green why emptying the standby cache is safe, with the pages to read more. Little text, as the design has it. Made
/// again at the new scale when it moves to a monitor scaled otherwise. Created when opened, disposed when closed.
/// </summary>
internal sealed class HowItWorksForm : Form
{
    private readonly SetupSession _session;
    private readonly Action<string> _openWebPage;
    private readonly float _text;
    private SetupFonts? _fonts;
    private HowItWorksView? _view;
    private SetupScale _scale;

    public HowItWorksForm(SetupSession session, Action<string> openWebPage, float text = 1f)
    {
        _session = session;
        _openWebPage = openWebPage;
        _text = text;
        Text = SetupText.HowItWorks;
        Icon = TrayIcons.App;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = SetupTheme.Window;
        Build();
    }

    internal HowItWorksView View => _view!;

    private Size Preferred => new(_scale.S(SetupWindowFit.Design.Width), _scale.S(SetupWindowFit.Design.Height));

    /// <summary>The view made for this window's scale (again after the window moved to a monitor scaled otherwise).</summary>
    private void Build()
    {
        var oldView = _view;
        var oldFonts = _fonts;
        _scale = SetupScale.For(this, _text);
        _fonts = new SetupFonts(_scale);
        _view = new HowItWorksView(new PageContext(_session, _scale, _fonts, Icon!, () => this, _openWebPage)) { Dock = DockStyle.Fill };
        _view.CloseButton.Click += (_, _) => Close();
        SuspendLayout();
        if (oldView is not null)
        {
            Controls.Remove(oldView);
            oldView.Dispose();
        }
        Controls.Add(_view);
        AcceptButton = _view.CloseButton;
        CancelButton = _view.CloseButton;
        ClientSize = Preferred;
        ResumeLayout(true);
        oldFonts?.Dispose();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        FitTo(Screen.FromControl(Owner ?? this).WorkingArea, Owner?.Bounds);
    }

    /// <summary>As big as the design, cut to <paramref name="area"/> (the page then scrolls), over its owner when it has one.</summary>
    internal void FitTo(Rectangle area, Rectangle? over = null)
    {
        ClientSize = SetupWindowFit.Client(Preferred, Size - ClientSize, area);
        Location = SetupWindowFit.Centre(Size, area, over);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Rescale(e.SuggestedRectangle);
    }

    /// <summary>On a monitor scaled otherwise (<paramref name="suggested"/>: where Windows puts it there): made again at the new scale and fitted to that monitor.</summary>
    private void Rescale(Rectangle suggested)
    {
        Build();
        FitTo(Screen.FromRectangle(suggested).WorkingArea, suggested);
    }

    /// <summary>CI: its words, its links and whether each opens, and anything clipped.</summary>
    internal object SmokeStats()
    {
        var area = Screen.FromControl(this).WorkingArea;
        return new
        {
            title = Text,
            texts = View.Page.Texts().ToList(),
            picture = ExplainerDiagram.Words,
            links = HowPage.Links.Select(l => new { text = l.Text, opens = l.Opens }).ToList(),
            fits = area.Contains(Bounds),
            layoutProblems = View.LayoutProblems(),
        };
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _fonts?.Dispose();
    }
}

/// <summary>The explainer's content (its page, which scrolls when the window is short) and its Close button.</summary>
internal sealed class HowItWorksView : ContainerControl
{
    private readonly SetupScale _s;
    private readonly Panel _content, _footer;

    public HowItWorksView(PageContext c)
    {
        _s = c.Scale;
        BackColor = SetupTheme.Window;
        _content = new Panel { AutoScroll = true, BackColor = SetupTheme.Window };
        Page = new HowPage(c);
        _content.Controls.Add(Page);
        _footer = new Panel { BackColor = SetupTheme.Window };
        _footer.Paint += (_, e) =>
        {
            using var pen = new Pen(SetupTheme.Line);
            e.Graphics.DrawLine(pen, 0, 0, _footer.Width, 0);
        };
        CloseButton = new SetupButton(c.Scale, c.Fonts, "Close", primary: true);
        _footer.Controls.Add(CloseButton);
        Controls.AddRange([_content, _footer]);
    }

    public SetupButton CloseButton { get; }
    internal HowPage Page { get; }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var b = CloseButton.Needed;
        int foot = 2 * _s.S(14) + b.Height + 1;
        _footer.Bounds = new Rectangle(0, Height - foot, Width, foot);
        CloseButton.Bounds = new Rectangle(Width - _s.S(24) - b.Width, (foot - b.Height) / 2, b.Width, b.Height);
        _content.Bounds = new Rectangle(0, 0, Width, Math.Max(0, Height - foot));
        int width = _content.Width, height = Page.Arrange(width);
        if (height > _content.Height)
        {
            width -= _s.ScrollBarWidth;
            height = Page.Arrange(width);
        }
        Page.SetBounds(0, _content.AutoScrollPosition.Y, width, height);
    }

    internal List<string> LayoutProblems()
    {
        PerformLayout();
        var problems = new List<string>();
        bool scrolls = Page.Height > _content.Height;
        int room = _content.Width - (scrolls ? _s.ScrollBarWidth : 0);
        if (Page.Width > room + 1) problems.Add($"the page ({Page.Width} px) is wider than its room ({room} px{(scrolls ? " beside the scroll bar" : "")})");
        SetupWizardView.Check(this, problems);
        return problems;
    }
}

/// <summary>The explainer's page.</summary>
internal sealed class HowPage : StepPage
{
    /// <summary>The pages to read more, in the design's order.</summary>
    public static readonly IReadOnlyList<SetupLink> Links = [SetupLinks.ReFsMemory, SetupLinks.DevDriveReport, SetupLinks.RamMap];

    public static readonly IReadOnlyList<(string Title, string Text)> Columns =
    [
        ("The kernel pool", "Windows' own records of every open file, program and permission check. Task Manager shows it as Paged and Non-paged pool. " +
                            "The bigger it gets, the longer every file open waits."),
        ("Why it grows with AI agents", "Dev Drives keep a record for each file in the standby cache. With plenty of memory Windows rarely empties it, " +
                                        "and agents touch thousands of files an hour."),
        ("Emptying it is safe", "The cache only holds copies of files already on disk. Windows empties it by itself when memory runs short; " +
                                "PoolWatch just does it sooner."),
    ];

    private readonly TextBlock _title, _readMore;
    private readonly RoundedBox _frame, _safe;
    private readonly ExplainerDiagram _diagram;
    private readonly IconBox _shield;
    private readonly List<(TextBlock Title, TextBlock Text)> _columns = [];
    private readonly List<TextBlock> _links = [];

    public HowPage(PageContext c) : base(c)
    {
        _title = Words(SetupText.HowItWorks, Fonts.H1);
        _frame = new RoundedBox { Border = SetupTheme.Line, Radius = c.Scale.K * 8 };
        Controls.Add(_frame);
        _diagram = new ExplainerDiagram();
        _frame.Controls.Add(_diagram);
        _safe = new RoundedBox { Fill = SetupTheme.GoodSoft, Radius = c.Scale.K * 8 };
        Controls.Add(_safe);
        _shield = Icon(_safe, SetupIcon.Shield, SetupTheme.Good);
        for (int i = 0; i < Columns.Count; i++)
        {
            Control parent = i == Columns.Count - 1 ? _safe : this;
            var title = Words(parent, Columns[i].Title, Fonts.Bold, i == Columns.Count - 1 ? SetupTheme.Good : SetupTheme.Ink);
            _columns.Add((title, Words(parent, Columns[i].Text, Fonts.Small)));
        }
        _readMore = Words("Read more:", Fonts.Small, SetupTheme.Faint);
        foreach (var link in Links) _links.Add(Link(this, link, Fonts.Small));
    }

    protected override int Place(int width, int room)
    {
        int y = Put(_title, Inset, S(26), room) + S(6) + S(6);
        _frame.Bounds = new Rectangle(Inset, y, room, S(10 + 128 + 4));
        _diagram.Bounds = new Rectangle(S(12), S(10), room - S(24), S(128));
        y = _frame.Bottom + S(18);

        int gap = S(26), col = (room - 2 * gap) / 3, bottom = y;
        for (int i = 0; i < _columns.Count; i++)
        {
            var (title, text) = _columns[i];
            int x = Inset + i * (col + gap);
            if (i == _columns.Count - 1)
            {
                // The green box reaches into the gap beside it and above the columns, as the design draws it.
                int padX = S(14), padY = S(12), icon = S(16);
                int ty = padY;
                _shield.Bounds = new Rectangle(padX, ty + (Fonts.Bold.Height - icon) / 2, icon, icon);
                Put(title, padX + icon + S(8), ty, col - icon - S(8));
                int end = Put(text, padX, title.Bottom + S(6), col);
                _safe.Bounds = new Rectangle(x - padX, y - padY, col + 2 * padX, end + padY);
                bottom = Math.Max(bottom, _safe.Bottom);
            }
            else bottom = Math.Max(bottom, Put(text, x, Put(title, x, y, col) + S(6), col));
        }
        y = bottom + S(8);
        Line(Inset, Inset + room, y);
        y += S(12);
        // "Read more:" and the links on one line, wrapping where they do not fit.
        int lx = Inset, lineH = Fonts.Small.Height;
        var items = new List<TextBlock> { _readMore };
        items.AddRange(_links);
        foreach (var t in items)
        {
            int w = Math.Min(room, t.OneLine);
            if (lx > Inset && lx + w > Inset + room) { lx = Inset; y += lineH + S(6); }
            Wrap(t, lx, y, room - (lx - Inset));
            lx += w + S(t == _readMore ? 6 : 22);
        }
        return y + lineH + S(10);
    }
}

/// <summary>The setup windows' size: the design's, cut to the working area when that is smaller (the page then scrolls).</summary>
internal static class SetupWindowFit
{
    /// <summary>The design's window content at 100% scaling (its 880 x 620 window less the title bar).</summary>
    public static readonly Size Design = new(880, 586);

    /// <summary>The client size for <paramref name="preferred"/> on <paramref name="area"/>, with the window's frame around it.</summary>
    public static Size Client(Size preferred, Size frame, Rectangle area) =>
        new(Math.Max(1, Math.Min(preferred.Width, area.Width - frame.Width)), Math.Max(1, Math.Min(preferred.Height, area.Height - frame.Height)));

    /// <summary>
    /// The working area a setup window opens on: that of the window it opens over (<paramref name="owner"/>), else that of
    /// the screen with the mouse (<paramref name="cursor"/>), as a window centred on the screen is placed. The lookups are
    /// Windows' screens (the tests pass their own).
    /// </summary>
    public static Rectangle Area(Rectangle? owner, Point cursor, Func<Rectangle, Rectangle> areaOf, Func<Point, Rectangle> areaAt) =>
        owner is { } o ? areaOf(o) : areaAt(cursor);

    /// <summary>Where a window of <paramref name="window"/> goes: centred over <paramref name="over"/> (or the area), always inside the area.</summary>
    public static Point Centre(Size window, Rectangle area, Rectangle? over = null)
    {
        var around = over ?? area;
        int x = around.X + (around.Width - window.Width) / 2, y = around.Y + (around.Height - window.Height) / 2;
        return new Point(Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - window.Width)), Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - window.Height)));
    }
}
