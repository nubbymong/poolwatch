namespace PoolWatch.Ui.Setup;

/// <summary>
/// "Set up PoolWatch": the first-run setup in seven steps, in a window of its own (the main window waits until it is
/// done). On the first run it has no close button and cannot be closed: it ends on the Ready step's "Open PoolWatch", and
/// when PoolWatch exits before that (or Windows shuts down), the next start opens it again. Opened again from the tray it
/// can be cancelled (its close button, or Esc). It reads the PC when it opens, and on a Windows build that needs it runs
/// the token-leak check by itself (not in the CI UI test). As big as the design, cut to the screen's working area when
/// that is smaller (a page then scrolls), on the screen of the window it opens over, else the one with the mouse; made
/// again at the new scale when it moves to a monitor scaled otherwise. Created when opened, disposed when closed.
/// </summary>
internal sealed class SetupWizardForm : Form
{
    /// <summary>CS_NOCLOSE: no close button, and no Close in the window menu.</summary>
    private const int CsNoClose = 0x200;

    private readonly ISetupHost _host;
    private readonly bool _noClose;
    private readonly float _text;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SetupScale _scale;
    private SetupFonts? _fonts;
    private SetupWizardView? _view;
    private HowItWorksForm? _how;
    private bool _mayClose;

    /// <param name="firstRun">Setup was never completed: it cannot be closed or skipped.</param>
    /// <param name="advanced">The view the app shows now, which the Welcome step starts with.</param>
    public SetupWizardForm(SetupContext context, bool firstRun, bool advanced) : this(new SetupHost(context), firstRun, advanced) { }

    /// <param name="text">The tests: a larger text, laid out as at a higher scaling.</param>
    internal SetupWizardForm(ISetupHost host, bool firstRun, bool advanced, float text = 1f)
    {
        _host = host;
        _noClose = firstRun;
        _text = text;
        Session = new SetupSession(host, firstRun, advanced);
        Text = SetupText.WindowTitle;
        // The app's icon, from where the main window takes it.
        Icon = TrayIcons.App;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = true;
        BackColor = SetupTheme.Window;
        Build();
        Session.Changed += part =>
        {
            if (part != SetupPart.Step || !Session.Flow.Finished) return;
            _mayClose = true;
            Close();
        };
    }

    internal SetupSession Session { get; }

    /// <summary>"Open PoolWatch" was pressed: setup is complete.</summary>
    public bool Finished => Session.Flow.Finished;

    /// <summary>The Welcome step's choice: the app's Advanced view.</summary>
    public bool Advanced => Session.Advanced;

    /// <summary>CI: the PC has been read.</summary>
    internal Task Ready => _ready.Task;

    internal SetupWizardView View => _view!;

    /// <summary>The view made for this window's scale (again after the window moved to a monitor scaled otherwise).</summary>
    private void Build()
    {
        var oldView = _view;
        var oldFonts = _fonts;
        _scale = SetupScale.For(this, _text);
        _fonts = new SetupFonts(_scale);
        _view = new SetupWizardView(Session, _scale, _fonts, Icon!, () => this, url => _host.OpenWebPage(this, url), () => DateTime.Now) { Dock = DockStyle.Fill };
        _view.HowItWorks += ShowHowItWorks;
        SuspendLayout();
        if (oldView is not null)
        {
            Controls.Remove(oldView);
            oldView.Dispose();
        }
        Controls.Add(_view);
        AcceptButton = _view.NextButton;
        ClientSize = Preferred;
        ResumeLayout(true);
        oldFonts?.Dispose();
    }

    private Size Preferred => new(_scale.S(SetupWindowFit.Design.Width), _scale.S(SetupWindowFit.Design.Height));

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            if (_noClose) cp.ClassStyle |= CsNoClose;
            return cp;
        }
    }

    /// <summary>The window has a close button (Windows draws none with CS_NOCLOSE): only a setup opened again.</summary>
    internal bool HasCloseButton => (CreateParams.ClassStyle & CsNoClose) == 0;

    private const int WmKeyDown = 0x100;
    /// <summary>A key-down's lParam bit 30: the key was already down, so Windows is repeating a key held down.</summary>
    private const long KeyWasDown = 1L << 30;

    /// <summary>
    /// Enter acts once per press. Windows repeats a key held down, and Enter presses Next (the form's AcceptButton) or the
    /// control that has the focus, so a held Enter would press Next again each time its wait ends and walk through setup.
    /// The repeats of Enter are swallowed, with or without Shift (Shift+Enter presses Next too): only a fresh press acts.
    /// Every other key repeats as before. WinForms asks here (through the focused control's parents) before a control's own
    /// KeyDown and before the AcceptButton; no setup control takes keys any earlier.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (msg.Msg == WmKeyDown && (keyData & Keys.KeyCode) == Keys.Enter && ((long)msg.LParam & KeyWasDown) != 0) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Esc cancels a setup opened again; the first run has no way out but its last step.</summary>
    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape && !_noClose)
        {
            Close();
            return true;
        }
        return base.ProcessDialogKey(keyData);
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Before it is placed the window has no screen of its own: the one it opens over, else the one with the mouse.
        FitTo(SetupWindowFit.Area(Owner?.Bounds, Cursor.Position, r => Screen.FromRectangle(r).WorkingArea, p => Screen.FromPoint(p).WorkingArea), Owner?.Bounds);
        try { await Session.LoadAsync(_stop.Token); }
        catch (Exception ex) { _host.Log("setup: the PC could not be read: " + ex.Message); }
        _ready.TrySetResult();
    }

    /// <summary>As big as the design, cut to <paramref name="area"/>, centred over <paramref name="over"/> (or the area) and inside the area.</summary>
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

    /// <summary>
    /// On a monitor scaled otherwise (<paramref name="suggested"/>: where Windows puts the window there): the view made
    /// again at the new scale, on the same step with the same choices, then fitted to that monitor's working area.
    /// </summary>
    private void Rescale(Rectangle suggested)
    {
        Build();
        FitTo(Screen.FromRectangle(suggested).WorkingArea, suggested);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_mayClose && !Session.Flow.MayClose(e.CloseReason))
        {
            e.Cancel = true;
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _stop.Cancel(); // a running check stops
        _how?.Close();
        _ready.TrySetResult();
        base.OnFormClosed(e);
    }

    private void ShowHowItWorks()
    {
        if (_how is { IsDisposed: false } open)
        {
            open.Activate();
            return;
        }
        _how = new HowItWorksForm(Session, url => _host.OpenWebPage(_how ?? (IWin32Window)this, url), _text);
        _how.FormClosed += (_, _) => _how = null;
        _how.Show(this);
    }

    // ------------------------------------------------------------------ the CI UI test

    /// <summary>The screenshot's name for a step: "6-setup-1-welcome.png" ... "6-setup-7-ready.png".</summary>
    internal static string ShotName(SetupStep step) => $"6-setup-{(int)step}-{SetupText.Rail(step).ToLowerInvariant().Replace(' ', '-')}.png";

    internal const string HowItWorksShot = "6-setup-how-it-works.png";

    /// <summary>Straight to a step, nothing applied.</summary>
    internal Task ShowStepForTest(SetupStep step) => Session.GoTo(step);

    internal HowItWorksForm OpenHowItWorks()
    {
        ShowHowItWorks();
        return _how!;
    }

    /// <summary>Closes the window whatever the step (the test, which never finishes setup).</summary>
    internal void CloseForTest()
    {
        _mayClose = true;
        Close();
    }

    /// <summary>The window: its title, whether it has a close button, its size and whether it fits the working area.</summary>
    internal object WindowStats()
    {
        var area = Screen.FromControl(this).WorkingArea;
        return new { title = Text, closeButton = !_noClose, width = Width, height = Height, area = $"{area.Width} x {area.Height}", fits = area.Contains(Bounds) };
    }

    /// <summary>The step shown now: its words, its buttons, its rows and choices, and anything clipped.</summary>
    internal object SmokeStats()
    {
        var view = View;
        var page = view.Page;
        var step = Session.Flow.Step;
        return new
        {
            number = (int)step,
            rail = SetupText.Rail(step),
            texts = page.Texts().ToList(),
            buttons = new[] { view.BackButton, view.NextButton }.Where(b => b.Visible).Select(b => b.Text).ToList(),
            impact = (page as ImpactPage)?.RowsShown.Select(r => new { cause = r.Cause.ToString(), title = r.Title, detail = r.Detail, level = r.Level, under = r.Under }).ToList(),
            fixes = (page as FixesPage)?.RowsShown.Select(r => new { fix = r.Fix.ToString(), title = r.Title, detail = r.Detail, input = r.Input.ToString(), whyOpen = r.WhyOpen }).ToList(),
            choices = page switch
            {
                CachePage c => c.Cards.Select(kv => kv.Value.AccessibleName).ToList(),
                WelcomePage w => [w.SimpleCard.AccessibleName, w.AdvancedCard.AccessibleName],
                _ => null,
            },
            chosen = page switch
            {
                CachePage c => c.Cards.FirstOrDefault(kv => kv.Value.Selected).Value?.AccessibleName,
                WelcomePage w => (w.AdvancedCard.Selected ? w.AdvancedCard : w.SimpleCard).AccessibleName,
                _ => null,
            },
            recommended = (page as CachePage)?.RecommendedShown,
            stutter = (page as CachePage)?.StutterShown,
            summary = (page as ReadyPage)?.RowsShown.Select(s => new { key = s.Key, value = s.Value }).ToList(),
            updates = (page as ReadyPage)?.UpdatesBox?.Checked,
            scrolls = view.Scrolls,
            layoutProblems = view.LayoutProblems(),
        };
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _stop.Dispose();
            _fonts?.Dispose();
        }
    }
}
