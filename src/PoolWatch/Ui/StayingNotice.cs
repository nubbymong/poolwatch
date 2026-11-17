namespace PoolWatch.Ui;

/// <summary>
/// The kernel pool notice that stays until you close it: the non-paged pool over its limit and still rising
/// (<see cref="NoticeText.Stays"/>). A notice by the clock leaves the screen after about 5 seconds, as Windows' own
/// setting has it, and goes into the notification centre (Windows says it closed, and the log says "notice closed" 7
/// seconds after it showed); on a PC that hung 6 minutes after that notice had gone, no one had seen it. So this one is
/// shown as the token-leak guard's notices that need your click are (<see cref="GuardNotices"/>): a small window at the
/// bottom right of the screen that holds the tray, never a modal box, above other windows, with a taskbar button. Unlike
/// them it never takes the keyboard focus when it appears (it comes while you type in another program, and a key meant
/// there must not press its button). It waits while a full-screen Direct3D program (exclusive full screen, as some games
/// run) or presentation mode runs (<see cref="Native.NotificationState.Presenting"/>), and shows when that ends: a window
/// above all others, with a pool tag's and a program's names, would otherwise show over such a game, or a presentation
/// with presentation mode on. It waits <see cref="MaxHoldMs"/> (2 minutes) at most, then shows whatever runs: this notice
/// can come minutes before a hang. A slide show without presentation mode, or a game in a borderless window, never
/// holds it (Windows says busy for those). One at a time: a newer one puts its words in the one open, or in the one
/// waiting. Open PoolWatch opens the main window and closes it; Close, its close box, or Esc once it has the focus,
/// closes it. Each one shown, held and closed is one line in the log, and one shown after it waited says for how long.
/// UI thread.
/// </summary>
/// <param name="openWindow">What Open PoolWatch opens (the main window, or setup while it was never completed).</param>
/// <param name="log">The engine log.</param>
/// <param name="presenting">Whether a full-screen Direct3D program or presentation mode runs now (the tests give their own).</param>
/// <param name="clockMs">The clock that only goes forward, in milliseconds (<see cref="Environment.TickCount64"/>, as
/// <see cref="Core.Model.Moment.Ms"/>; the tests give their own): how long a notice has waited is timed on it.</param>
internal sealed class StayingNotice(Action openWindow, Action<string> log, Func<bool>? presenting = null, Func<long>? clockMs = null)
{
    /// <summary>How often a notice that waits asks again whether it may show.</summary>
    internal const int WaitCheckMs = 5000;

    /// <summary>
    /// The longest a notice waits, 2 minutes: the non-paged pool's notice came 6 minutes before a hang, so a game or a
    /// presentation that runs on must not keep it back until after it. Then it shows whatever runs, and the log says how
    /// long it waited. A newer one that takes its place is timed from when the first began to wait.
    /// </summary>
    internal const int MaxHoldMs = 2 * 60 * 1000;

    private readonly Func<bool> _presenting = presenting ?? Native.NotificationState.Presenting;
    private readonly Func<long> _clockMs = clockMs ?? (() => Environment.TickCount64);
    private NoticeWindow? _open;
    private NoticeText? _held;
    private long _heldSinceMs;
    private System.Windows.Forms.Timer? _wait;

    /// <summary>A notice that waited, taken to be shown, and how long it waited, in milliseconds.</summary>
    internal readonly record struct Waited(NoticeText Notice, long HeldMs);

    /// <summary>The window open now; null when none (the tests).</summary>
    internal NoticeWindow? Open => _open is { IsDisposed: false } open ? open : null;

    /// <summary>The notice that waits for a full-screen program or presentation mode to end; null when none (the tests).</summary>
    internal NoticeText? Held => _held;

    /// <summary>
    /// Shows <paramref name="notice"/>: in the window open now, its words replaced; while a full-screen Direct3D program or
    /// presentation mode runs, it waits (<see cref="Hold"/>); else in a new window, shown without taking the focus.
    /// </summary>
    public void Show(NoticeText notice)
    {
        if (Open is { } open)
        {
            open.Say(notice);
            log($"notice shown again, in the one still open: {notice.Title}");
            return;
        }
        if (Hold(notice))
        {
            WaitForTheEnd();
            return;
        }
        // Shown now. One that still waited (for 2 minutes, or until what ran full screen ended) is older, and must not put
        // its words in this one later; the log says how long it waited.
        ShowNow(notice, _held is { } older ? Take(older).HeldMs : null);
    }

    /// <summary>
    /// While a full-screen Direct3D program or presentation mode runs, and the notice that waits, if one does, has waited
    /// less than <see cref="MaxHoldMs"/>, keeps <paramref name="notice"/> to show when that ends (a newer one in its place,
    /// timed from when the first began to wait) and logs it: true. Otherwise false, and nothing is kept.
    /// </summary>
    internal bool Hold(NoticeText notice)
    {
        if (!MustWait()) return false;
        if (_held is null) _heldSinceMs = _clockMs();
        _held = notice;
        log($"notice held while a full-screen Direct3D program or presentation mode runs (it shows when that ends, or after {MaxHoldMs / 60_000} minutes at most): {notice.Title}");
        return true;
    }

    /// <summary>
    /// The notice that waited, taken to be shown, with how long it waited: once the full-screen program or presentation
    /// mode has ended, or it has waited <see cref="MaxHoldMs"/>. Null while it still waits, or when none does.
    /// </summary>
    internal Waited? Release() => _held is { } held && !MustWait() ? Take(held) : null;

    /// <summary>
    /// Whether a notice waits now: while a full-screen Direct3D program or presentation mode runs, unless the one that
    /// waits has waited <see cref="MaxHoldMs"/> (the clock is read first, so Windows is not asked then).
    /// </summary>
    private bool MustWait() => (_held is null || _clockMs() - _heldSinceMs < MaxHoldMs) && _presenting();

    /// <summary>The notice that waits, taken from its place, with how long it waited.</summary>
    private Waited Take(NoticeText held)
    {
        _held = null;
        return new Waited(held, _clockMs() - _heldSinceMs);
    }

    /// <summary>The log's line for a notice shown, and for how long it waited first (<paramref name="heldMs"/>; null when it did not).</summary>
    internal static string ShownLine(NoticeText notice, long? heldMs) => heldMs is { } ms
        ? $"notice shown after it was held {Seconds(ms)} (it waits {MaxHoldMs / 60_000} minutes at most; it stays until it is closed): {notice.Title}"
        : $"notice shown (it stays until it is closed): {notice.Title}";

    /// <summary>"45 seconds", "1 second": how long a notice waited, to the nearest second.</summary>
    private static string Seconds(long ms)
    {
        long s = (ms + 500) / 1000;
        return s == 1 ? "1 second" : $"{s} seconds";
    }

    /// <summary>Shows <paramref name="notice"/> in a new window, without taking the focus, and logs it with how long it waited.</summary>
    private void ShowNow(NoticeText notice, long? heldMs)
    {
        _wait?.Stop();
        var window = Make(notice);
        _open = window;
        window.Show(); // non-modal, and without taking the focus (NoticeWindow.ShowWithoutActivation)
        log(ShownLine(notice, heldMs));
    }

    /// <summary>Asks every <see cref="WaitCheckMs"/> whether the notice that waits may show now, and shows it then.</summary>
    private void WaitForTheEnd()
    {
        if (_wait is null)
        {
            _wait = new System.Windows.Forms.Timer { Interval = WaitCheckMs };
            _wait.Tick += (_, _) =>
            {
                if (Release() is { } waited) ShowNow(waited.Notice, waited.HeldMs);
            };
        }
        _wait.Start();
    }

    /// <summary>A notice's window as <see cref="Show"/> shows it, not shown yet (the tests look at it here).</summary>
    internal NoticeWindow Make(NoticeText notice)
    {
        var window = new NoticeWindow(notice, OpenPoolWatch);
        window.FormClosed += (_, _) =>
        {
            if (_open == window) _open = null;
            log($"notice closed (it was \"{window.Title}\")");
        };
        return window;
    }

    private void OpenPoolWatch()
    {
        _open?.Close();
        openWindow();
    }

    /// <summary>Closes the window open now, and drops a notice that waits, as PoolWatch exits.</summary>
    public void Close()
    {
        _wait?.Stop();
        _wait?.Dispose();
        _wait = null;
        _held = null;
        Open?.Close();
    }
}

/// <summary>
/// The window of a notice that stays (<see cref="StayingNotice"/>): its title in the title bar, its words, and Open
/// PoolWatch and Close (Esc too, once it has the focus). Above other windows from the start (a style it is made with, not
/// <see cref="Form.TopMost"/>, whose change brings a window forward), and shown without taking the focus, at the bottom
/// right of the screen that holds the tray (<see cref="Area"/>). Its words are cut to what a notice holds
/// (<see cref="Cut"/>), so they fit its fixed size. Its font is one for every such window (<see cref="NoticeFont"/>):
/// a form does not dispose a font it is given, so one made for each window would be lost with it.
/// </summary>
internal sealed class NoticeWindow : Form
{
    /// <summary>WS_EX_TOPMOST: above every window that is not top-most itself.</summary>
    internal const int TopMostStyle = 0x00000008;

    /// <summary>The font of every notice window, made once and kept for PoolWatch's life.</summary>
    internal static readonly Font NoticeFont = new("Segoe UI", 9f);

    private readonly TextBox _words;
    private readonly Action _openPoolWatch;

    public NoticeWindow(NoticeText notice, Action openPoolWatch)
    {
        _openPoolWatch = openPoolWatch;
        Icon = TrayIcons.App;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        Font = NoticeFont;
        Size = new Size(LogicalToDeviceUnits(460), LogicalToDeviceUnits(190));
        _words = new TextBox { Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, BackColor = SystemColors.Control, TabStop = false };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(6) };
        var close = new Button { Text = CloseLabel, AutoSize = true };
        close.Click += (_, _) => Close();
        var open = new Button { Text = OpenLabel, AutoSize = true };
        open.Click += (_, _) => OpenPressed();
        bar.Controls.Add(close);
        bar.Controls.Add(open);
        Controls.Add(_words);
        Controls.Add(bar);
        CancelButton = close; // Esc, once the window has the focus
        Say(notice);
        var area = Area(TaskbarWatch.FindTaskbar(), h => Screen.FromHandle(h).WorkingArea, Screen.PrimaryScreen?.WorkingArea);
        Location = new Point(area.Right - Width - 12, area.Bottom - Height - 12);
    }

    public const string OpenLabel = "Open PoolWatch", CloseLabel = "Close";

    /// <summary>
    /// Where the window goes: the working area of the screen that holds the taskbar with the tray
    /// (<paramref name="taskbar"/>, <see cref="TaskbarWatch.FindTaskbar"/>), else the primary screen's, else a 1280 x 800
    /// area. Not always the primary screen: the taskbar may be moved to another.
    /// </summary>
    internal static Rectangle Area(IntPtr taskbar, Func<IntPtr, Rectangle> areaOf, Rectangle? primary) =>
        taskbar != IntPtr.Zero ? areaOf(taskbar) : primary ?? new Rectangle(0, 0, 1280, 800);

    /// <summary>
    /// A notice's words as the window holds them: whole up to what a notice by the clock holds
    /// (<see cref="Engine.ProfileAtStart.MaxNotice"/>), else cut where a word ends, marked "...", so they fit the window's
    /// fixed size (a tag's long description could run past it, and its text box has no scroll bar).
    /// </summary>
    internal static string Cut(string text)
    {
        const int Max = Engine.ProfileAtStart.MaxNotice;
        if (text.Length <= Max) return text;
        int end = text.LastIndexOf(' ', Max - 3);
        return (end > 0 ? text[..end] : text[..(Max - 3)]).TrimEnd() + "...";
    }

    /// <summary>The notice's title, as the title bar shows it after "PoolWatch: ".</summary>
    internal string Title { get; private set; } = "";

    /// <summary>The words shown (the tests).</summary>
    internal string Words => _words.Text;

    /// <summary>The buttons' words, left to right (the tests).</summary>
    internal IReadOnlyList<string> Buttons => Controls.OfType<FlowLayoutPanel>().SelectMany(p => p.Controls.OfType<Button>()).Reverse().Select(b => b.Text).ToList();

    /// <summary>Puts <paramref name="notice"/>'s title and words in the window.</summary>
    public void Say(NoticeText notice)
    {
        Title = TrayApp.NoticeTitle(notice.Title);
        Text = "PoolWatch: " + Title;
        _words.Text = Cut(notice.Text).Replace("\n", Environment.NewLine);
    }

    /// <summary>Open PoolWatch: the main window, and this window closes.</summary>
    internal void OpenPressed() => _openPoolWatch();

    /// <summary>It never takes the focus as it shows: a key meant for the program you type in must not press its button.</summary>
    protected override bool ShowWithoutActivation => true;

    /// <summary>Whether it takes the focus as it shows (the tests: never).</summary>
    internal bool TakesFocusAsItShows => !ShowWithoutActivation;

    /// <summary>Above other windows from the start, and never through <see cref="Form.TopMost"/>'s change, which would activate it.</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= TopMostStyle;
            return cp;
        }
    }

    /// <summary>The extended window style it is made with (the tests).</summary>
    internal int ExtendedStyle => CreateParams.ExStyle;
}
