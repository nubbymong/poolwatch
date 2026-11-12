using PoolWatch.Core;

namespace PoolWatch.Ui.Setup;

/// <summary>
/// "Set up PoolWatch": the first-run setup in eight steps, in a window of its own (the main window waits until it is
/// done), or its Records step alone, which opens by itself after an upgrade from a version without it (ending on Done,
/// and cancelled at once by its close button, Esc or Alt+F4). It ends on the Ready step's "Open PoolWatch". On the first run its close button (or Esc, or Alt+F4) asks first
/// (<see cref="ISetupHost.ConfirmCloseFirstRun"/>, No the default): a yes closes it and PoolWatch keeps running in the
/// tray, with any change already made with Next, and setup opens again when the owner opens PoolWatch and at the next
/// start, as it does when PoolWatch exits before the end (or Windows shuts down), which closes it without asking. Opened
/// again from the tray, closing it (its close button, Esc, Alt+F4) cancels it at once, without asking. It reads the PC
/// when it opens, and on a Windows build that needs it runs the token-leak check by itself (not in the CI UI test). As
/// big as the design, cut to the screen's working area when that is smaller (a page then scrolls), on the screen of the
/// window it opens over, else the one with the mouse; made again at the new scale when it moves to a monitor scaled
/// otherwise. Created when opened, disposed when closed.
/// </summary>
internal sealed class SetupWizardForm : Form
{
    /// <summary>
    /// CS_NOCLOSE, which disables Close in the window menu while Windows still draws the close button. The window never has
    /// it: it made the first run's close button one that did nothing (<see cref="CloseButtonEnabled"/>).
    /// </summary>
    private const int CsNoClose = 0x200;

    private readonly ISetupHost _host;
    /// <summary>Setup was never completed: a close the owner asks for asks first.</summary>
    private readonly bool _firstRun;
    private readonly float _text;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SetupScale _scale;
    private SetupFonts? _fonts;
    private SetupWizardView? _view;
    private HowItWorksForm? _how;
    private bool _mayClose;

    /// <param name="firstRun">Setup was never completed: closing it asks first.</param>
    /// <param name="advanced">The view the app shows now, which the Welcome step starts with.</param>
    /// <param name="recordsOnly">The Records step alone (after an upgrade from a version without it).</param>
    public SetupWizardForm(SetupContext context, bool firstRun, bool advanced, bool recordsOnly = false) : this(new SetupHost(context), firstRun, advanced, recordsOnly: recordsOnly) { }

    /// <param name="text">The tests: a larger text, laid out as at a higher scaling.</param>
    internal SetupWizardForm(ISetupHost host, bool firstRun, bool advanced, float text = 1f, bool recordsOnly = false)
    {
        _host = host;
        _firstRun = firstRun && !recordsOnly;
        _text = text;
        Session = new SetupSession(host, firstRun, advanced, recordsOnly: recordsOnly);
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
        // Settings, which the tray opens while setup is open (the Records step alone opens at each start until Done), may
        // put another record mode or cleanup into effect: the step shows it, on this window's thread (the change is told on
        // its caller's, a worker's for Undo), so its Next or Done never puts an older choice back over it. Before the window
        // has a handle there is nothing to show: it reads what is in effect as it gets one (OnHandleCreated).
        _onApplied = change =>
        {
            if (!IsHandleCreated || IsDisposed) return;
            try { BeginInvoke(new Action(() => { if (!IsDisposed) Session.FollowRecords(); })); }
            catch (InvalidOperationException) { } // its handle went meanwhile: it is closing
        };
        host.Applied += _onApplied;
    }

    /// <summary>Follows a setting put into effect elsewhere (<see cref="ISetupHost.Applied"/>); let go of when the window is disposed.</summary>
    private readonly Action<Action<Settings>> _onApplied;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Session.FollowRecords(); // a change put into effect before the window had a handle
    }

    internal SetupSession Session { get; }

    /// <summary>"Open PoolWatch" (or Done, on the Records step alone) was pressed: setup is complete.</summary>
    public bool Finished => Session.Flow.Finished;

    /// <summary>It is the Records step alone (after an upgrade).</summary>
    public bool RecordsOnly => Session.Flow.RecordsOnly;

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

    /// <summary>Windows draws the close button enabled and sends its close (no CS_NOCLOSE), on the first run too.</summary>
    internal bool CloseButtonEnabled => (CreateParams.ClassStyle & CsNoClose) == 0;

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

    /// <summary>Esc closes setup as its close button does: a setup opened again at once, the first run once the owner says yes.</summary>
    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape)
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

    /// <summary>
    /// A close the owner asks for on the first run (the close button, Esc, Alt+F4, the taskbar's Close) asks first, and a
    /// no keeps setup open; PoolWatch exiting, Windows shutting down, a finished or re-run setup close it without asking
    /// (<see cref="SetupFlow.MayClose"/>).
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_mayClose && !Session.Flow.MayClose(e.CloseReason))
        {
            CloseQuestions++;
            if (!(_closeAnswer?.Invoke() ?? _host.ConfirmCloseFirstRun(this)))
            {
                e.Cancel = true;
                return;
            }
        }
        base.OnFormClosing(e);
    }

    /// <summary>How many times a close asked first (the CI reading's close included; tests).</summary>
    internal int CloseQuestions { get; private set; }

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

    /// <summary>The screenshot's name for a step: "6-setup-1-welcome.png" ... "6-setup-7-records.png", "6-setup-8-ready.png".</summary>
    internal static string ShotName(SetupStep step) => $"6-setup-{(int)step}-{SetupText.Rail(step).ToLowerInvariant().Replace(' ', '-')}.png";

    internal const string HowItWorksShot = "6-setup-how-it-works.png";

    /// <summary>The screenshot of the Records step alone, as it opens by itself after an upgrade.</summary>
    internal const string RecordsAloneShot = "6-setup-records-alone.png";

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

    /// <summary>
    /// The window: its title, whether its close button is enabled and whether a close asks first (read from the closing
    /// path, <see cref="CloseAsksFirst"/>), its size and whether it fits the working area.
    /// </summary>
    internal object WindowStats()
    {
        var area = Screen.FromControl(this).WorkingArea;
        return new
        {
            title = Text, closeButtonEnabled = CloseButtonEnabled, closeAsksFirst = CloseAsksFirst(),
            width = Width, height = Height, area = $"{area.Width} x {area.Height}", fits = area.Contains(Bounds),
        };
    }

    private const int WmSysCommand = 0x0112, ScClose = 0xF060;

    /// <summary>The answer the probe gives in place of the question (<see cref="CloseAsksFirst"/>); null: the host asks.</summary>
    private Func<bool>? _closeAnswer;

    /// <summary>
    /// Whether a close the owner asks for asks first, read from the closing path itself: the window gets the message its
    /// close button sends (WM_SYSCOMMAND with SC_CLOSE, as Alt+F4 does), with the question answered No by the probe, so
    /// no box shows. True when the question came once and the window stayed open. A setup opened again, which closes at
    /// once, is not probed: it would close. It was worked out from <see cref="SetupFlow.MayClose"/> alone, so a close
    /// button that did nothing again would still have read as one that asks first.
    /// </summary>
    private bool CloseAsksFirst() => _firstRun && !Session.Flow.Finished && AsksOnceAndStaysOpen(this, answer => _closeAnswer = answer);

    /// <summary>
    /// Sends <paramref name="window"/> the message its close button sends, with <paramref name="answerWith"/> given an
    /// answer of No to put in place of the question for that close (and null after it). True when the question came
    /// exactly once and the window is still open.
    /// </summary>
    internal static bool AsksOnceAndStaysOpen(Form window, Action<Func<bool>?> answerWith)
    {
        if (!window.IsHandleCreated) return false;
        int asked = 0;
        answerWith(() => { asked++; return false; });
        try { SendMessage(window.Handle, WmSysCommand, ScClose, 0); }
        finally { answerWith(null); }
        return asked == 1 && !window.IsDisposed && window.IsHandleCreated;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, nint wParam, nint lParam);

    /// <summary>The step shown now: its words, its buttons, its rows and choices, and anything clipped or overlapping.</summary>
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
                RecordsPage r => r.Cards.Select(kv => kv.Value.AccessibleName).ToList(),
                _ => null,
            },
            chosen = page switch
            {
                CachePage c => c.Cards.FirstOrDefault(kv => kv.Value.Selected).Value?.AccessibleName,
                WelcomePage w => (w.AdvancedCard.Selected ? w.AdvancedCard : w.SimpleCard).AccessibleName,
                RecordsPage r => r.Cards.FirstOrDefault(kv => kv.Value.Selected).Value?.AccessibleName,
                _ => null,
            },
            recommended = (page as CachePage)?.RecommendedShown ?? (page as RecordsPage)?.RecommendedShown,
            days = (page as RecordsPage)?.DaysShown,
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
            _host.Applied -= _onApplied;
            _stop.Dispose();
            _fonts?.Dispose();
        }
    }
}
