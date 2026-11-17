using PoolWatch.Engine;
using PoolWatch.Ui.Setup;

namespace PoolWatch.Ui;

/// <summary>The answers to the urgent clear's question after an upgrade (<see cref="UrgentQuestion"/>).</summary>
internal enum UrgentAnswer { TurnOn, LeaveOff, Later }

/// <summary>
/// The urgent clear's question after an upgrade. A version without the urgent clear left a settings.json without it, which
/// reads it off and still to be chosen (<see cref="Core.Settings.AutoClearUrgentAsk"/>). While automatic clearing is on with
/// a rule (<see cref="Core.Settings.UrgentClearToAsk"/>), the tray app asks at its start: once per start at most, never in
/// the CI UI test, and only once setup was completed (until then setup opens at each start, and its Standby cache step has
/// the switch). It asks as the token-leak guard asks for its click and as the non-paged pool's notice stays: a small
/// window at the bottom right of the screen that holds the tray, above other windows, never taking the focus
/// (<see cref="UrgentQuestionWindow"/>), with the title and words of setup's switch and three answers, greyed for a
/// second as it opens (<see cref="UrgentQuestionWindow.HeldFor"/>). Turn on (green, the safe choice for the PC) turns
/// the urgent clear on; Leave off keeps it off, and it is not asked about again; Decide later, and a close without an
/// answer, write nothing, so it stays off and is asked about at the next start. An answer is written at once and in
/// settings.json (<see cref="LiveSettings.Apply"/>), and each is a line in the log. UI thread.
/// </summary>
/// <param name="live">The running settings, and settings.json.</param>
/// <param name="log">The engine log.</param>
/// <param name="show">Shows the window (the tests keep it, never shown); null: shown, without taking the focus.</param>
internal sealed class UrgentQuestion(LiveSettings live, Action<string> log, Action<UrgentQuestionWindow>? show = null)
{
    public const string TurnOnLabel = "Turn on", LeaveOffLabel = "Leave off", LaterLabel = "Decide later";

    private bool _asked;
    private UrgentQuestionWindow? _open;

    /// <summary>The window open now; null when none.</summary>
    public UrgentQuestionWindow? Open => _open is { IsDisposed: false } open ? open : null;

    /// <summary>
    /// The tray app's start: asks when the question is due (<see cref="Core.Settings.UrgentClearToAsk"/>), setup was
    /// completed and this is not the CI UI test; once per start at most, whatever the answer. True when it asked.
    /// </summary>
    public bool AtStart(bool setupCompleted, bool smoke)
    {
        if (_asked || smoke || !setupCompleted || !live.Running.UrgentClearToAsk()) return false;
        _asked = true;
        var window = new UrgentQuestionWindow(live.Running.NonPagedTotalAlertBytes, Answered);
        _open = window;
        if (show is not null) show(window);
        else window.Show(); // non-modal, and without taking the focus (UrgentQuestionWindow.ShowWithoutActivation)
        log("the urgent clear's question after the upgrade is shown (automatic clearing is on, and the urgent clear is still to be chosen)");
        return true;
    }

    /// <summary>
    /// An answer (null: the window closed without one, which counts as Decide later). Turn on and Leave off are written at
    /// once and in settings.json, and the question is not asked again; Decide later writes nothing. Each is logged. A write
    /// that fails is logged too: nothing was chosen then, and the question comes again at the next start.
    /// </summary>
    public void Answered(UrgentAnswer? answer)
    {
        try
        {
            if (answer == UrgentAnswer.TurnOn)
                live.Apply("the urgent clear on (its question after the upgrade)", s => { s.AutoClearUrgent = true; s.AutoClearUrgentAsk = false; });
            else if (answer == UrgentAnswer.LeaveOff)
                live.Apply("the urgent clear left off (its question after the upgrade)", s => { s.AutoClearUrgent = false; s.AutoClearUrgentAsk = false; });
            log(LogLine(answer));
        }
        catch (Exception ex)
        {
            log($"the urgent clear's question after the upgrade: {(answer == UrgentAnswer.TurnOn ? TurnOnLabel : LeaveOffLabel)} was not saved, " +
                "so the urgent clear is still to be chosen and is asked about again at the next start: " + ex.Message);
        }
    }

    /// <summary>The log's line for an answer (null: the window closed without one).</summary>
    public static string LogLine(UrgentAnswer? answer) => "the urgent clear's question after the upgrade: " + (answer switch
    {
        UrgentAnswer.TurnOn => TurnOnLabel + " (the urgent clear is on now)",
        UrgentAnswer.LeaveOff => LeaveOffLabel + " (it stays off, and is not asked about again)",
        UrgentAnswer.Later => LaterLabel + AskedAgain,
        _ => "closed without an answer, as " + LaterLabel + AskedAgain,
    });

    private const string AskedAgain = " (it stays off until it is chosen, and is asked about again at the next start)";

    /// <summary>Closes the window open now, as PoolWatch exits: a close without an answer, which writes nothing.</summary>
    public void Close() => Open?.Close();
}

/// <summary>
/// The urgent clear's question (<see cref="UrgentQuestion"/>): setup's switch's title in its title bar
/// (<see cref="SetupText.UrgentTitle"/>), the switch's row and the step's line on what a clear is like as its words,
/// and Turn on, Leave off and Decide later, left to right. A window as the non-paged pool's notice is
/// (<see cref="NoticeWindow"/>): above other windows from the start (a style it is made with, not
/// <see cref="Form.TopMost"/>, whose change brings a window forward) and shown without taking the focus, so a key meant
/// for the program you type in never answers it, at the bottom right of the screen that holds the tray, with a taskbar
/// button and the one shared font. Turn on is green, the safe choice for the PC, as setup shows its safety in green (the
/// system's own button under high contrast). It opens by itself, in the corner where a click aimed at what was there just
/// before could land, so its buttons are greyed, and a press answers nothing, until its window is first shown and for
/// <see cref="SetupSession.NextWait"/> (a second) from then, as the Records step that opens alone holds its Done
/// (<see cref="HeldFor"/>). Tab goes through the buttons left to right; once they can be pressed, Decide later has the
/// focus. Esc, once it has the focus, is Decide later; Enter presses the button with the focus, Decide later until you
/// move it. An answer is given once; a close with none (its close box, Alt+F4, PoolWatch exiting) is given as none.
/// </summary>
internal sealed class UrgentQuestionWindow : Form
{
    private readonly Action<UrgentAnswer?> _answered;
    private readonly TextBox _words;
    private readonly Dictionary<UrgentAnswer, Button> _buttons = [];
    private bool _done;
    /// <summary>The time the hold is measured by (<see cref="System.Diagnostics.Stopwatch"/> ticks; the tests move it on).</summary>
    private readonly Func<long> _clock;
    /// <summary>When the window was first shown; null until then, while the buttons are held whatever the time.</summary>
    private long? _shownAt;
    /// <summary>Gives the buttons back once the hold is over (<see cref="HoldEnds"/>).</summary>
    private readonly System.Windows.Forms.Timer _hold = new();

    /// <param name="alertBytes">The non-paged alert's level, as setup's switch says it ("over 5 GB").</param>
    /// <param name="answered">The answer, once: null when the window closed without one.</param>
    /// <param name="clock">The tests: a clock they move on (<see cref="System.Diagnostics.Stopwatch"/> ticks), for the buttons' hold.</param>
    public UrgentQuestionWindow(long alertBytes, Action<UrgentAnswer?> answered, Func<long>? clock = null)
    {
        _answered = answered;
        _clock = clock ?? System.Diagnostics.Stopwatch.GetTimestamp;
        _hold.Tick += (_, _) => HoldEnds();
        Icon = TrayIcons.App;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        Font = NoticeWindow.NoticeFont;
        Text = "PoolWatch: " + SetupText.UrgentTitle;
        Size = new Size(LogicalToDeviceUnits(520), LogicalToDeviceUnits(170));
        _words = new TextBox
        {
            Text = Say(alertBytes), Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill,
            BackColor = SystemColors.Control, TabStop = false,
        };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(6) };
        // Right to left: Decide later at the right end, Turn on at the left, where the words are read from. Tab goes as they
        // are read, left to right. Greyed until the hold is over (HoldEnds).
        foreach (var (answer, label, tab) in new[] { (UrgentAnswer.Later, UrgentQuestion.LaterLabel, 2), (UrgentAnswer.LeaveOff, UrgentQuestion.LeaveOffLabel, 1), (UrgentAnswer.TurnOn, UrgentQuestion.TurnOnLabel, 0) })
        {
            var button = new Button { Text = label, AutoSize = true, TabIndex = tab, Enabled = false };
            button.Click += (_, _) => Press(answer);
            _buttons[answer] = button;
            bar.Controls.Add(button);
        }
        Green(_buttons[UrgentAnswer.TurnOn]);
        Controls.Add(_words);
        Controls.Add(bar);
        CancelButton = _buttons[UrgentAnswer.Later]; // Esc, once the window has the focus
        FormClosed += (_, _) => Dismissed();
        var area = NoticeWindow.Area(TaskbarWatch.FindTaskbar(), h => Screen.FromHandle(h).WorkingArea, Screen.PrimaryScreen?.WorkingArea);
        Location = new Point(area.Right - Width - 12, area.Bottom - Height - 12);
    }

    /// <summary>The words: setup's switch's row, as a sentence, then the step's line on what a clear is like.</summary>
    internal static string Say(long alertBytes) => SetupText.UrgentRow(alertBytes) + "." + Environment.NewLine + SetupText.Stutter;

    /// <summary>Turn on in green, the safe choice for the PC; under high contrast, the system's own button.</summary>
    private static void Green(Button button)
    {
        if (UiColors.HighContrast) return;
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = UiColors.Good;
        button.ForeColor = Color.White;
        button.FlatAppearance.BorderColor = UiColors.Good;
    }

    /// <summary>The words shown (the tests).</summary>
    internal string Words => _words.Text;

    /// <summary>The buttons' words, left to right (the tests).</summary>
    internal IReadOnlyList<string> Buttons => Controls.OfType<FlowLayoutPanel>().SelectMany(p => p.Controls.OfType<Button>()).Reverse().Select(b => b.Text).ToList();

    /// <summary>The button of an answer (the tests).</summary>
    internal Button ButtonFor(UrgentAnswer answer) => _buttons[answer];

    /// <summary>
    /// How much longer the buttons are held: all of <see cref="SetupSession.NextWait"/> until the window is first shown,
    /// however long after it was made (a slow sign-in), and <see cref="SetupSession.NextWait"/> from then; zero once that
    /// is over.
    /// </summary>
    internal TimeSpan HeldFor
    {
        get
        {
            if (_shownAt is not { } from) return SetupSession.NextWait;
            var left = SetupSession.NextWait - System.Diagnostics.Stopwatch.GetElapsedTime(from, _clock());
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    /// <summary>Whether the hold's timer runs (the tests).</summary>
    internal bool HoldRunning => _hold.Enabled;

    /// <summary>The window is on screen for the first time: the buttons' hold runs from now.</summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_shownAt is not null) return;
        _shownAt = _clock();
        StartHold(SetupSession.NextWait);
    }

    /// <summary>Starts the hold's timer, to go off once <paramref name="left"/> is over.</summary>
    private void StartHold(TimeSpan left)
    {
        _hold.Interval = Math.Max(1, (int)Math.Ceiling(left.TotalMilliseconds));
        _hold.Start();
    }

    /// <summary>
    /// The hold's timer went off: with the hold over, the buttons can be pressed and Decide later has the focus (so Enter
    /// presses it until the focus is moved); a tick that came early waits the rest.
    /// </summary>
    internal void HoldEnds()
    {
        _hold.Stop();
        if (HeldFor is { Ticks: > 0 } left)
        {
            StartHold(left);
            return;
        }
        foreach (var button in _buttons.Values) button.Enabled = true;
        ActiveControl = _buttons[UrgentAnswer.Later];
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _hold.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>An answer's button: the answer, once, then the window closes. Nothing while the buttons are held.</summary>
    internal void Press(UrgentAnswer answer)
    {
        if (_done || HeldFor > TimeSpan.Zero) return;
        _done = true;
        _answered(answer);
        Close();
    }

    /// <summary>The window closed: with no answer given, none is given now (it counts as Decide later), once.</summary>
    internal void Dismissed()
    {
        if (_done) return;
        _done = true;
        _answered(null);
    }

    /// <summary>It never takes the focus as it shows: it comes at a start, perhaps while you type in another program.</summary>
    protected override bool ShowWithoutActivation => true;

    /// <summary>Whether it takes the focus as it shows (the tests: never).</summary>
    internal bool TakesFocusAsItShows => !ShowWithoutActivation;

    /// <summary>Above other windows from the start, and never through <see cref="Form.TopMost"/>'s change, which would activate it.</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NoticeWindow.TopMostStyle;
            return cp;
        }
    }

    /// <summary>The extended window style it is made with (the tests).</summary>
    internal int ExtendedStyle => CreateParams.ExStyle;
}
