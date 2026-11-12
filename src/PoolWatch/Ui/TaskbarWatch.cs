using System.Runtime.InteropServices;

namespace PoolWatch.Ui;

/// <summary>
/// The tray icon after Explorer restarts. A new taskbar has no icon of PoolWatch's until it is added again, and Explorer
/// says it is there by broadcasting "TaskbarCreated" to every top-level window. Windows stops that message on its way from
/// Explorer to an elevated program (UIPI: a program at a lower integrity level may not send it to one at a higher), so
/// the NotifyIcon's own window, which would add the icon again, never gets it, and the icon stayed gone until PoolWatch
/// was started again. This hidden top-level window (a message-only one gets no broadcast) lets that one message through
/// for itself (ChangeWindowMessageFilterEx, MSGFLT_ALLOW: this window only, nothing else of the process).
///
/// Any program of the session can send that message to this window then, one without administrator rights too, so the
/// message alone changes nothing: it only has this window look which taskbar window there is now
/// (<see cref="LookNow"/>). <c>created</c>, which adds the icon again and writes a line in the log, runs only when that
/// window is another than the one it last ran for, and at most once every <see cref="MinGap"/>. With the same taskbar,
/// however many messages come after that, nothing is added and nothing is logged.
///
/// Another taskbar that a message finds sooner than <see cref="MinGap"/> after the last run waits for that time to be
/// over: each message's look that finds another taskbar records it as the one last heard of (a later one takes its
/// place), and one timer looks again when the time is over (<see cref="Look"/>), unless a message has meanwhile found no
/// taskbar, or the one <c>created</c> last ran for, which leaves no look due. That later look runs <c>created</c> only
/// when the window there then is the one last heard of, so an Explorer that restarts twice in a row still gets the icon.
/// When another window is there then, it is not the one the last message found. Usually it is a taskbar Explorer has
/// made since, for which no message has come yet, so nothing has said it is in place: an add made then might not take,
/// and with that window recorded its own message would be passed over, the icon staying missing. So that look adds
/// nothing, records nothing and starts no further look; the window's own message, when it comes, is handled as every
/// message is. (Whichever program made the window found then: one that is not the one last heard of gets nothing
/// until a later message.)
///
/// The watch starts with no taskbar window recorded, so the first message of a run that finds a taskbar runs
/// <c>created</c> once, whichever window that is. The taskbar there as the watch is made is not taken as one that has
/// the icon: it may have appeared after the tray app made its icon visible (a start that races Explorer's at sign-in),
/// or have been there but not ready, so that the add did not take. Were it taken as one that has the icon, Explorer's
/// message for it would be passed over, and the icon would stay missing for that sign-in.
///
/// The taskbar window is the top-level window of the class <c>Shell_TrayWnd</c> that Windows finds
/// (<see cref="FindTaskbar"/>), whichever program made it, and any program of the session can make a window of that
/// class. So a program without administrator rights that keeps making such windows and sending the message has
/// <c>created</c> run for as long as it does so, at most once every <see cref="MinGap"/>: that gap is the only bound on
/// it (SECURITY.md says what that costs: the icon taken off and put on, and a line in the log, each time). Such a
/// window, found in the place of Explorer's, can also keep <c>created</c> from running for a taskbar Explorer has just
/// made: when it is the window <c>created</c> last ran for, or when the timer's look finds it and it is not the one
/// last heard of. The icon is then missing from that taskbar at least until a later message finds a window other than
/// the one <c>created</c> last ran for (SECURITY.md says so too). One window and one registered message; nothing runs
/// until a message comes.
/// </summary>
internal sealed class TaskbarWatch : NativeWindow, IDisposable
{
    /// <summary>The message Explorer broadcasts once its taskbar is (back) in place; 0 when Windows would not register it.</summary>
    internal static readonly int TaskbarCreated = (int)RegisterWindowMessageW("TaskbarCreated");

    /// <summary>The least time between two runs of <c>created</c>, however many messages come and whatever taskbar they find.</summary>
    internal static readonly TimeSpan MinGap = TimeSpan.FromSeconds(5);

    private const int WS_EX_TOOLWINDOW = 0x0000_0080;
    private const uint MSGFLT_ALLOW = 1;
    private readonly Action _created;
    /// <summary>The taskbar window there is now, or zero when there is none.</summary>
    private readonly Func<IntPtr> _taskbar;
    /// <summary>The time <see cref="MinGap"/> is measured by (<see cref="System.Diagnostics.Stopwatch"/> ticks; the tests move it on).</summary>
    private readonly Func<long> _clock;
    /// <summary>
    /// The taskbar window <c>created</c> last ran for. Zero: none yet in this run. It starts at zero and is never set
    /// from the taskbar there as this watch is made: the tray app made its icon visible before, but whether that taskbar
    /// took it is not known here, so the first look of a run that finds a taskbar runs <c>created</c> for it.
    /// </summary>
    private IntPtr _addedFor;
    /// <summary>
    /// The taskbar window last heard of: the one found by the last message's look that found one other than
    /// <see cref="_addedFor"/> (zero: none yet in this run). A message does not say which window it is about, so the one
    /// found as it comes is taken for it. It is set by a message's look only, also by one that adds nothing because
    /// <see cref="MinGap"/> is not over or a look is already due, and never by the timer's look, which reads it: that
    /// look runs <c>created</c> for this window alone.
    /// </summary>
    private IntPtr _heardOf;
    /// <summary>When <c>created</c> last ran (<see cref="_clock"/>); null: not yet.</summary>
    private long? _lastRun;
    /// <summary>
    /// Looks again once <see cref="MinGap"/> is over (<see cref="Look"/>), after a message's look that found another
    /// taskbar sooner; made only then.
    /// </summary>
    private System.Windows.Forms.Timer? _again;

    /// <param name="created">
    /// Runs on this thread (the UI thread) when a message's look finds a taskbar window other than the one it last ran
    /// for (the first taskbar a run's messages find is one, as none is recorded before), or, when that was sooner than
    /// <see cref="MinGap"/> after its last run, when the timer's look finds the window last heard of still there
    /// (<see cref="LookNow"/>).
    /// </param>
    public TaskbarWatch(Action created) : this(created, FindTaskbar, System.Diagnostics.Stopwatch.GetTimestamp) { }

    /// <param name="taskbar">
    /// The tests: the taskbar window there is now (zero: none), in place of <see cref="FindTaskbar"/>. It is asked only
    /// in a look (<see cref="LookNow"/>), never as the watch is made.
    /// </param>
    /// <param name="clock">The tests: a clock they move on (<see cref="System.Diagnostics.Stopwatch"/> ticks).</param>
    internal TaskbarWatch(Action created, Func<IntPtr> taskbar, Func<long> clock)
    {
        _created = created;
        _taskbar = taskbar;
        _clock = clock;
        CreateHandle(new CreateParams { ExStyle = WS_EX_TOOLWINDOW, Caption = "PoolWatch taskbar watch" });
        Allowed = Allow(Handle, out _);
    }

    /// <summary>Whether Windows lets "TaskbarCreated" through to this window from a program at a lower integrity level.</summary>
    public bool Allowed { get; }

    /// <summary>
    /// The taskbar window: the top-level window of the class <c>Shell_TrayWnd</c> that Windows finds, or zero when there
    /// is none. Explorer's taskbar is one; which program a window found belongs to is not looked at, and any program of
    /// the session can make one of that class.
    /// </summary>
    internal static IntPtr FindTaskbar() => FindWindowW("Shell_TrayWnd", null);

    /// <summary>A look is due once <see cref="MinGap"/> is over (the tests: no message loop runs its timer there, so they call <see cref="Look"/>).</summary>
    internal bool LookDue => _again is { Enabled: true };

    /// <summary>
    /// The timer's look, made once <see cref="MinGap"/> is over after a message found another taskbar sooner (the tests
    /// call it in the timer's place): <see cref="LookNow"/>, which then runs <c>created</c> only for the taskbar window
    /// last heard of.
    /// </summary>
    internal void Look() => LookNow(message: false);

    /// <summary>
    /// Looks which taskbar window there is now: as each "TaskbarCreated" has it do (<paramref name="message"/>), and as
    /// the timer does when <see cref="MinGap"/> is over. None, or the one <c>created</c> last ran for: nothing more, and
    /// no look left due (a message says nothing by itself: any program of the session can send it). Another one, which
    /// the first one a run finds always is (none is recorded before it):
    /// <list type="bullet">
    /// <item>A message's look records it as the one last heard of (<see cref="_heardOf"/>), whatever follows.</item>
    /// <item>The timer's look goes on only when it is the one last heard of. When it is not (usually a taskbar made
    /// since, for which no message has come yet), nothing is added, nothing is recorded and no look is left due, so the
    /// message for that window is not passed over when it comes.</item>
    /// <item>Then <c>created</c> runs for it, unless it ran less than <see cref="MinGap"/> ago: then the timer looks
    /// again when that time is over (one timer, started once, however many messages come meanwhile; a timer that fires
    /// early is started again for the time left).</item>
    /// </list>
    /// Never throws: a broadcast must never take the UI thread down, and the tray icon's own code logs what it could not
    /// do.
    /// </summary>
    /// <param name="message">True for the look a "TaskbarCreated" starts, false for the timer's.</param>
    private void LookNow(bool message)
    {
        try
        {
            IntPtr now = _taskbar();
            if (now == IntPtr.Zero || now == _addedFor)
            {
                _again?.Stop();
                return;
            }
            if (message) _heardOf = now;
            else if (now != _heardOf)
            {
                _again?.Stop(); // not the taskbar last heard of: nothing until a message comes for the one that is there
                return;
            }
            long at = _clock();
            if (_lastRun is { } last && MinGap - System.Diagnostics.Stopwatch.GetElapsedTime(last, at) is { Ticks: > 0 } left)
            {
                if (LookDue) return;
                var again = _again;
                if (again is null)
                {
                    var timer = _again = again = new System.Windows.Forms.Timer();
                    timer.Tick += (_, _) => { timer.Stop(); Look(); };
                }
                again.Interval = Math.Max(1, (int)Math.Ceiling(left.TotalMilliseconds));
                again.Start();
                return;
            }
            _again?.Stop();
            (_addedFor, _lastRun) = (now, at);
            _created();
        }
        catch { /* the tray icon's own code logs; a broadcast must never take the UI thread down */ }
    }

    /// <summary>
    /// Lets "TaskbarCreated" through UIPI to <paramref name="window"/> (one of this process's), with what Windows says of
    /// the window's filter before the call (<paramref name="status"/>: MSGFLTINFO_ALREADYALLOWED_FORWND, 1, once it was let
    /// through already). False when it could not be.
    /// </summary>
    internal static bool Allow(IntPtr window, out uint status)
    {
        var change = new CHANGEFILTERSTRUCT { cbSize = (uint)Marshal.SizeOf<CHANGEFILTERSTRUCT>() };
        bool ok = TaskbarCreated != 0 && ChangeWindowMessageFilterEx(window, (uint)TaskbarCreated, MSGFLT_ALLOW, ref change);
        status = change.ExtStatus;
        return ok;
    }

    protected override void WndProc(ref Message m)
    {
        if (TaskbarCreated != 0 && m.Msg == TaskbarCreated) LookNow(message: true);
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        _again?.Dispose();
        if (Handle != IntPtr.Zero) DestroyHandle();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CHANGEFILTERSTRUCT
    {
        public uint cbSize;
        public uint ExtStatus;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RegisterWindowMessageW(string message);

    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr window, uint message, uint action, ref CHANGEFILTERSTRUCT change);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr FindWindowW(string className, string? windowName);
}
