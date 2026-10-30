using System.ComponentModel;

namespace PoolWatch.Ui.Setup;

/// <summary>
/// The setup window's content, as the approved design lays it out: the step rail on the left, the step's page (it
/// scrolls when the window is shorter than the page), and the footer with "How PoolWatch works" (on every step; on
/// Welcome after the design's "Setup takes about 3 minutes") and the step's buttons. Every page is built once; a change in
/// the session updates only what it concerns (<see cref="StepPage.Sync"/>). Next is enabled only while it can go on
/// (<see cref="SetupSession.CanGoNext"/>).
/// </summary>
internal sealed class SetupWizardView : ContainerControl
{
    /// <summary>The scrolling area the page sits in; a page that changed height asks it to lay out again.</summary>
    internal sealed class ContentHost : Panel
    {
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] // set in code, never by a designer
        public required SetupWizardView View { get; init; }
        public void Relayout() => View.LayoutPage(keepScroll: true);
    }

    /// <summary>The footer, with the line above it.</summary>
    private sealed class Footer : Panel
    {
        public Footer()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = SetupTheme.Window;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(SetupTheme.Line);
            e.Graphics.DrawLine(pen, 0, 0, Width, 0);
        }
    }

    private readonly SetupSession _session;
    private readonly SetupScale _s;
    private readonly StepRail _rail;
    private readonly ContentHost _content;
    private readonly Footer _footer;
    private readonly TextBlock _duration;
    private readonly LinkText _how;
    private readonly SetupButton _back, _next;
    private readonly Dictionary<SetupStep, StepPage> _pages;
    private readonly Func<IWin32Window> _owner;
    /// <summary>
    /// Enables Next again once its wait is over: a step that applies changes was brought up, the scan's facts landed on it,
    /// a change on it did not work, or a new Fixes row appeared (<see cref="SetupSession.NextHeldFor"/>).
    /// </summary>
    private readonly System.Windows.Forms.Timer _nextWait = new();

    /// <summary>
    /// Whether a control has the keyboard focus, and moving it there. The window does both; the tests, whose window is never
    /// shown (so no control can take the focus), record them instead.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] // set in code, never by a designer
    internal Func<Control, bool> HasFocus { get; set; } = static c => c.Focused;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Action<Control> GiveFocus { get; set; } = static c => c.Focus();

    /// <param name="clock">The time now, for the Ready step's picture of the clock (the tests give a fixed one).</param>
    public SetupWizardView(SetupSession session, SetupScale scale, SetupFonts fonts, Icon appIcon, Func<IWin32Window> owner, Action<string> openWebPage,
        Func<DateTime> clock)
    {
        _session = session;
        _s = scale;
        _owner = owner;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        BackColor = SetupTheme.Window;
        _rail = new StepRail(session.Flow, scale, fonts, appIcon);
        _content = new ContentHost { View = this, AutoScroll = true, BackColor = SetupTheme.Window };
        _footer = new Footer();
        _duration = new TextBlock { Text = SetupText.Duration, Font = fonts.Small, ForeColor = SetupTheme.Faint };
        _how = new LinkText { Text = SetupText.HowItWorks, Font = fonts.Small, ForeColor = SetupTheme.Accent, Icon = SetupIcon.Book };
        _how.Click += (_, _) => HowItWorks?.Invoke();
        _back = new SetupButton(scale, fonts, SetupText.Back, primary: false);
        _back.Click += (_, _) => _session.Back();
        _next = new SetupButton(scale, fonts, SetupText.Next, primary: true);
        _next.Click += async (_, _) => await _session.NextAsync(_owner());
        _footer.Controls.AddRange([_duration, _how, _back, _next]);

        var c = new PageContext(session, scale, fonts, appIcon, owner, openWebPage, clock);
        _pages = new()
        {
            [SetupStep.Welcome] = new WelcomePage(c),
            [SetupStep.YourPc] = new ImpactPage(c),
            [SetupStep.Safety] = new SafetyPage(c),
            [SetupStep.StandbyCache] = new CachePage(c),
            [SetupStep.Fixes] = new FixesPage(c),
            [SetupStep.Services] = new ServicesPage(c),
            [SetupStep.Ready] = new ReadyPage(c),
        };
        foreach (var page in _pages.Values) _content.Controls.Add(page);
        // The content first in the tab order, then the footer's buttons.
        Controls.AddRange([_content, _footer, _rail]);
        _nextWait.Tick += (_, _) => NextWaitOver();
        _session.Changed += OnChanged;
        ShowStep();
    }

    /// <summary>The timer for Next's wait is running (the tests: no message loop runs it there).</summary>
    internal bool NextWaitRunning => _nextWait.Enabled;

    /// <summary>The timer for Next's wait went off (the tests call it once they moved the session's clock on).</summary>
    internal void NextWaitOver()
    {
        _nextWait.Stop();
        UpdateFooter(); // starts it again while there is still time left
    }

    /// <summary>"How PoolWatch works" was clicked.</summary>
    public event Action? HowItWorks;

    /// <summary>The step's main button (Enter presses it).</summary>
    public SetupButton NextButton => _next;
    public SetupButton BackButton => _back;
    /// <summary>"How PoolWatch works" in the footer.</summary>
    internal LinkText HowLink => _how;
    public StepPage Page => _pages[_session.Flow.Step];
    internal IReadOnlyDictionary<SetupStep, StepPage> Pages => _pages;
    internal ContentHost Content => _content;
    internal StepRail Rail => _rail;

    /// <summary>
    /// What the footer shows on the step: the explainer link on every step (after the setup time on Welcome); Back on the
    /// steps between; the main button.
    /// </summary>
    private (bool Duration, bool How, bool Back) FooterParts => _session.Flow.Step switch
    {
        SetupStep.Welcome => (true, true, false),
        SetupStep.Ready => (false, true, false),
        _ => (false, true, true),
    };

    internal string NextText => _session.Busy ? SetupText.Working : _session.Flow.Step switch
    {
        SetupStep.Welcome => SetupText.GetStarted,
        SetupStep.Ready => SetupText.OpenApp,
        _ => SetupText.Next,
    };

    private void OnChanged(SetupPart part)
    {
        if (IsDisposed) return;
        if (part == SetupPart.Step) { ShowStep(); return; }
        if (part is SetupPart.Busy or SetupPart.Scan or SetupPart.Next) UpdateFooter(); // Next waits for a change, the scan, or its wait
        foreach (var page in _pages.Values) page.Sync(part);
        LayoutPage(keepScroll: true);
    }

    private void ShowStep()
    {
        var step = _session.Flow.Step;
        // The page area holds the keyboard focus while Next waits: a screen reader names it by its step, not only "pane".
        _content.AccessibleName = SetupText.Rail(step);
        foreach (var (s, page) in _pages)
        {
            if (s == step) page.Sync(SetupPart.Step);
            page.Visible = s == step;
        }
        _rail.ShowStep();
        UpdateFooter();
        LayoutPage(keepScroll: false);
        // Next has the focus on every step. While it waits (the step's moment, or the scan), the page area holds the focus,
        // where Enter presses nothing (Next is the form's AcceptButton, and disabled); Next takes it once it can act.
        GiveFocus(_next.Enabled ? _next : _content);
    }

    private void UpdateFooter()
    {
        var (duration, how, back) = FooterParts;
        _duration.Visible = duration;
        _how.Visible = how;
        _back.Visible = back;
        _back.Enabled = !_session.Busy;
        _next.Text = NextText;
        bool can = _session.CanGoNext;
        // Every time Next waits (a change runs, the scan, a wait), the page area takes the focus from it first: a disabled
        // control's focus moves on to the next control, which may be a "Why?" link that takes Enter itself. Next takes it
        // back once it can act, if it is still there.
        if (!can && HasFocus(_next)) GiveFocus(_content);
        _next.Enabled = can;
        if (can && HasFocus(_content)) GiveFocus(_next);
        // Held on a step brought up, after the scan landed on it, after a change on it that did not work, or after a new
        // Fixes row: enabled again when the wait is over (each starts it again).
        if (_session.NextHeldFor is { Ticks: > 0 } left && !_session.Busy && !_nextWait.Enabled)
        {
            _nextWait.Interval = Math.Max(1, (int)Math.Ceiling(left.TotalMilliseconds));
            _nextWait.Start();
        }
        LayoutFooter();
    }

    /// <summary>The footer's height: the design's padding around a button.</summary>
    private int FooterHeight => 2 * _s.S(14) + _next.Needed.Height + 1;

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int rail = Math.Min(_rail.RailWidth, Width / 3), foot = FooterHeight;
        _rail.Bounds = new Rectangle(0, 0, rail, Height);
        _footer.Bounds = new Rectangle(rail, Height - foot, Width - rail, foot);
        _content.Bounds = new Rectangle(rail, 0, Width - rail, Math.Max(0, Height - foot));
        LayoutFooter();
        LayoutPage(keepScroll: true);
    }

    private void LayoutFooter()
    {
        var (duration, how, back) = FooterParts;
        int pad = _s.S(24), gap = _s.S(10), h = _footer.Height;
        var next = _next.Needed;
        _next.Bounds = new Rectangle(_footer.Width - pad - next.Width, (h - next.Height) / 2, next.Width, next.Height);
        int right = _next.Left;
        if (back)
        {
            var b = _back.Needed;
            _back.Bounds = new Rectangle(right - gap - b.Width, (h - b.Height) / 2, b.Width, b.Height);
            right = _back.Left;
        }
        // On the left, one after the other: the setup time (Welcome) and the explainer link.
        int x = pad, end = right - _s.S(12);
        foreach (var left in new TextBlock[] { _duration, _how }.Where(t => t == _duration ? duration : how))
        {
            int w = Math.Max(1, Math.Min(end - x, left is LinkText l ? l.OneLine : left.OneLine));
            left.Location = new Point(x, 0);
            left.Size = new Size(w, left.Measure(w));
            left.Top = (h - left.Height) / 2;
            x += w + _s.S(24);
        }
    }

    /// <summary>
    /// Lays the step's page out at the content's width, less the scroll bar when it needs more height than there is room
    /// for (so it never scrolls sideways). <paramref name="keepScroll"/>: the same page again (a row changed), where it was.
    /// </summary>
    internal void LayoutPage(bool keepScroll)
    {
        if (_content.Width <= 0) return;
        var page = Page;
        if (!keepScroll) _content.AutoScrollPosition = Point.Empty;
        int width = _content.Width, height = page.Arrange(width);
        if (height > _content.Height)
        {
            width -= _s.ScrollBarWidth;
            height = page.Arrange(width);
        }
        int y = keepScroll ? _content.AutoScrollPosition.Y : 0;
        page.SetBounds(0, y, width, height);
    }

    /// <summary>The page's content needs more room than the window has (it scrolls).</summary>
    internal bool Scrolls => Page.Height > _content.Height;

    /// <summary>
    /// A control outside the one around it (sideways anywhere; up and down except in the scrolling page), a text, button,
    /// meter or field smaller than its content, the rail's steps not fitting, or the footer's parts on each other. Judged
    /// from the layout itself, so it holds before the window is shown (the tests, the CI UI test).
    /// </summary>
    internal List<string> LayoutProblems()
    {
        PerformLayout();
        var problems = new List<string>();
        if (_rail.Needed > _rail.Height + 1) problems.Add($"the rail needs {_rail.Needed} px, has {_rail.Height}");
        if (_rail.WidestStepName > _rail.TextRoom + 1) problems.Add($"a step name needs {_rail.WidestStepName} px beside its dot, has {_rail.TextRoom}");
        int room = _content.Width - (Scrolls ? _s.ScrollBarWidth : 0);
        if (Page.Width > room + 1) problems.Add($"the page ({Page.Width} px) is wider than its room ({room} px{(Scrolls ? " beside the scroll bar" : "")})");
        var footer = _footer.Controls.Cast<Control>().Where(c => c.Visible).OrderBy(c => c.Left).ToList();
        for (int i = 1; i < footer.Count; i++)
            if (footer[i - 1].Right > footer[i].Left) problems.Add($"in the footer, \"{footer[i - 1].Text}\" runs into \"{footer[i].Text}\"");
        Check(this, problems);
        return problems;
    }

    internal static void Check(Control parent, List<string> problems)
    {
        bool scrolls = parent is ScrollableControl { AutoScroll: true };
        foreach (Control c in parent.Controls)
        {
            if (!c.Visible) continue;
            string name = $"{c.GetType().Name} \"{(c.Text.Length > 40 ? c.Text[..40] + "..." : c.Text)}\"";
            var area = parent.ClientRectangle;
            if (c.Left < -1 || c.Right > area.Width + 1 || (!scrolls && (c.Top < -1 || c.Bottom > area.Height + 1)))
                problems.Add($"{name} at {c.Bounds} is outside its {parent.GetType().Name} ({area.Size})");
            string? short_ = c switch
            {
                LinkText { Icon: not null } l when l.OneLine > l.Width + 1 => $"{l.OneLine} px wide",
                TextBlock t and not LinkText { Icon: not null } when t.Measure(t.Width) > t.Height + 1 => $"{t.Measure(t.Width)} px tall",
                SetupButton b when b.Needed.Width > b.Width + 1 || b.Needed.Height > b.Height + 1 => b.Needed.ToString(),
                ImpactMeter m when !m.Fits || m.Needed > m.Height + 1 => $"{m.Needed} px tall, its words in {m.Width - 1} px",
                Toggle t when t.Needed.Width > t.Width + 1 || t.Needed.Height > t.Height + 1 => t.Needed.ToString(),
                YesNo y when y.Needed.Width > y.Width + 1 || y.Needed.Height > y.Height + 1 => y.Needed.ToString(),
                FixesPage.StatusText s when s.Needed.Width > s.Width + 1 || s.Needed.Height > s.Height + 1 => s.Needed.ToString(),
                CheckBox b when b.GetPreferredSize(Size.Empty) is var need && (need.Width > b.Width + 1 || need.Height > b.Height + 1) => need.ToString(),
                ComboBox box when box.Items.Cast<object>().Select(i => SetupDraw.Line(i.ToString() ?? "", box.Font).Width).DefaultIfEmpty(0).Max() + box.Height > box.Width + 1 =>
                    "room for its longest choice",
                _ => null,
            };
            if (short_ is not null) problems.Add($"{name} needs {short_}, has {c.Size}");
            Check(c, problems);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _session.Changed -= OnChanged;
            _nextWait.Dispose();
        }
        base.Dispose(disposing);
    }
}
