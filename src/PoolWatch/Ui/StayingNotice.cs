namespace PoolWatch.Ui;

/// <summary>
/// The kernel pool notice that stays until you close it: the non-paged pool over its limit and still rising
/// (<see cref="NoticeText.Stays"/>). A notice by the clock leaves the screen after about 5 seconds, as Windows' own
/// setting has it, and goes into the notification centre (Windows says it closed, and the log says "notice closed" 7
/// seconds after it showed); on a PC that hung 6 minutes after that notice had gone, no one had seen it. So this one is
/// shown as the token-leak guard's notices that need your click are (<see cref="GuardNotices"/>): a small window at the
/// bottom right of the screen, never a modal box, above other windows, with a taskbar button. Unlike them it never takes
/// the keyboard focus when it appears (it comes while you type in another program, and a key meant there must not press
/// its button). One at a time: a newer one puts its words in the one open. Open PoolWatch opens the main window and
/// closes it; Close, or its close box, closes it. Each one shown and each close is one line in the log. UI thread.
/// </summary>
/// <param name="openWindow">What Open PoolWatch opens (the main window, or setup while it was never completed).</param>
/// <param name="log">The engine log.</param>
internal sealed class StayingNotice(Action openWindow, Action<string> log)
{
    private NoticeWindow? _open;

    /// <summary>The window open now; null when none (the tests).</summary>
    internal NoticeWindow? Open => _open is { IsDisposed: false } open ? open : null;

    /// <summary>Shows <paramref name="notice"/>: in the window open now, its words replaced, else in a new one, shown without taking the focus.</summary>
    public void Show(NoticeText notice)
    {
        if (Open is { } open)
        {
            open.Say(notice);
            log($"notice shown again, in the one still open: {notice.Title}");
            return;
        }
        var window = Make(notice);
        _open = window;
        window.Show(); // non-modal, and without taking the focus (NoticeWindow.ShowWithoutActivation)
        log($"notice shown (it stays until it is closed): {notice.Title}");
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

    /// <summary>Closes the window open now, as PoolWatch exits.</summary>
    public void Close() => Open?.Close();
}

/// <summary>
/// The window of a notice that stays (<see cref="StayingNotice"/>): its title in the title bar, its words, and Open
/// PoolWatch and Close. Above other windows from the start (a style it is made with, not <see cref="Form.TopMost"/>, whose
/// change brings a window forward), and shown without taking the focus.
/// </summary>
internal sealed class NoticeWindow : Form
{
    /// <summary>WS_EX_TOPMOST: above every window that is not top-most itself.</summary>
    internal const int TopMostStyle = 0x00000008;

    private readonly TextBox _words;
    private readonly Action _openPoolWatch;

    public NoticeWindow(NoticeText notice, Action openPoolWatch)
    {
        _openPoolWatch = openPoolWatch;
        Icon = TrayIcons.App;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        Font = new Font("Segoe UI", 9f);
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
        Say(notice);
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        Location = new Point(area.Right - Width - 12, area.Bottom - Height - 12);
    }

    public const string OpenLabel = "Open PoolWatch", CloseLabel = "Close";

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
        _words.Text = notice.Text.Replace("\n", Environment.NewLine);
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
