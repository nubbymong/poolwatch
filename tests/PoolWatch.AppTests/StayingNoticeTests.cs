using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// The non-paged pool's notice stays on screen until it is closed. A notice by the clock leaves the screen after about 5
/// seconds (Windows' own setting) and Windows then says it closed: the log said "notice closed" 7 seconds after each
/// "Non-paged pool over the limit" on a night that ended in a hang, so no one saw them. It is shown as the token-leak
/// guard's notices are: a window of its own, above other windows, with a taskbar button and its buttons, but one that never
/// takes the focus as it appears. The window is built here, never shown.
/// </summary>
public class StayingNoticeTests
{
    private static readonly NoticeText NonPaged = new(PoolCauses.NonPagedTitle, "Kernel pool: Open files (File) is growing. Open PoolWatch for details.", Stays: true);

    [Fact]
    public void TheNonPagedPoolsNoticeIsAWindowThatStaysAboveOthersAndNeverTakesTheFocus()
    {
        var log = new List<string>();
        int opened = 0;
        var staying = new StayingNotice(() => opened++, log.Add);
        using var window = staying.Make(NonPaged);
        Assert.Equal("PoolWatch: Non-paged pool over the limit", window.Text);
        Assert.Equal(NonPaged.Text, window.Words);
        Assert.Equal([NoticeWindow.OpenLabel, NoticeWindow.CloseLabel], window.Buttons);
        Assert.Equal(["Open PoolWatch", "Close"], new[] { NoticeWindow.OpenLabel, NoticeWindow.CloseLabel });
        // Above other windows from the start, by its style, never through TopMost (whose change brings a window forward),
        // and shown without taking the focus: a key meant for the program you type in never presses its button.
        Assert.Equal(NoticeWindow.TopMostStyle, window.ExtendedStyle & NoticeWindow.TopMostStyle);
        Assert.False(window.TopMost);
        Assert.False(window.TakesFocusAsItShows);
        Assert.True(window.ShowInTaskbar);
        Assert.Same(TrayIcons.App, window.Icon);
        Assert.Equal(FormBorderStyle.FixedToolWindow, window.FormBorderStyle);
        // A newer notice puts its words in it.
        window.Say(NonPaged with { Text = "Kernel pool: Open files (File) is growing. PoolWatch is keeping an eye on Codex." });
        Assert.Equal("Kernel pool: Open files (File) is growing. PoolWatch is keeping an eye on Codex.", window.Words);
        // Open PoolWatch opens the main window.
        window.OpenPressed();
        Assert.Equal(1, opened);
        // Built, never shown: none is open, and nothing was said in the log.
        Assert.Null(staying.Open);
        Assert.Empty(log);
        // Only the non-paged pool's notice stays; a growth's is one by the clock.
        Assert.True(NonPaged.Stays);
        Assert.False(new NoticeText("Kernel pool: Open files (File) is growing", PoolCauses.ClickForDetails).Stays);
    }

    /// <summary>
    /// A window per notice must not leak a font: a form does not dispose a font it is given, so every notice window
    /// shares one, made once. Esc closes it once it has the focus (its Close button is the window's cancel button).
    /// </summary>
    [Fact]
    public void EveryNoticeWindowSharesOneFontAndEscClosesIt()
    {
        var staying = new StayingNotice(() => { }, _ => { }, () => false);
        using var one = staying.Make(NonPaged);
        using var two = staying.Make(NonPaged);
        Assert.Same(NoticeWindow.NoticeFont, one.Font);
        Assert.Same(one.Font, two.Font);
        var cancel = Assert.IsType<Button>(one.CancelButton);
        Assert.Equal(NoticeWindow.CloseLabel, cancel.Text);
    }

    /// <summary>
    /// The window has a fixed size and its text box no scroll bar, so its words are cut to what a notice holds, where a
    /// word ends, marked "...": a tag's long description no longer runs out of sight.
    /// </summary>
    [Fact]
    public void ANoticesWordsAreCutToWhatTheWindowHolds()
    {
        string longText = "Kernel pool: " + string.Join(" ", Enumerable.Repeat("a long description of a pool tag", 30)) + ". Open PoolWatch for details.";
        var staying = new StayingNotice(() => { }, _ => { }, () => false);
        using var window = staying.Make(NonPaged with { Text = longText });
        Assert.True(window.Words.Length <= PoolWatch.Engine.ProfileAtStart.MaxNotice, $"{window.Words.Length} characters");
        Assert.EndsWith("...", window.Words);
        Assert.StartsWith(window.Words[..^3], longText);
        Assert.DoesNotContain(" ...", window.Words); // cut where a word ends, never a word in two
        Assert.Equal(NonPaged.Text, NoticeWindow.Cut(NonPaged.Text)); // a notice that fits is whole
        Assert.Equal(new string('x', PoolWatch.Engine.ProfileAtStart.MaxNotice - 3) + "...", NoticeWindow.Cut(new string('x', 400))); // one long word
    }

    /// <summary>
    /// It opens at the bottom right of the screen that holds the taskbar with the tray, which is not always the primary
    /// screen; with no taskbar found, the primary screen's.
    /// </summary>
    [Fact]
    public void ItOpensOnTheScreenThatHoldsTheTray()
    {
        var primary = new Rectangle(0, 0, 1920, 1040);
        var second = new Rectangle(1920, 0, 2560, 1400);
        var taskbar = new IntPtr(42);
        Assert.Equal(second, NoticeWindow.Area(taskbar, h => h == taskbar ? second : throw new ArgumentException("not the taskbar"), primary));
        Assert.Equal(primary, NoticeWindow.Area(IntPtr.Zero, _ => throw new ArgumentException("no taskbar to ask about"), primary));
        Assert.Equal(new Rectangle(0, 0, 1280, 800), NoticeWindow.Area(IntPtr.Zero, _ => second, null));
        // The window itself goes by it, with the taskbar Windows finds here.
        var staying = new StayingNotice(() => { }, _ => { }, () => false);
        using var window = staying.Make(NonPaged);
        var area = NoticeWindow.Area(TaskbarWatch.FindTaskbar(), h => Screen.FromHandle(h).WorkingArea, Screen.PrimaryScreen?.WorkingArea);
        Assert.Equal(new Point(area.Right - window.Width - 12, area.Bottom - window.Height - 12), window.Location);
    }

    /// <summary>
    /// As Windows holds its own toasts back, the notice waits while a full-screen Direct3D program (a game, most often) or
    /// presentation mode runs (a window above all others, with a pool tag's and a program's names, would show over a
    /// presentation), is logged as held, and shows once that ends; a newer one takes its place while it waits, timed from
    /// when the first began to wait, and the log says how long it was held. Windows' states: a full-screen Direct3D program
    /// and presentation mode hold it. Busy does not: Windows says it for any full-screen window (a video in a browser, a
    /// Remote Desktop window), and the non-paged pool's notice must not wait out a film. Any other state shows it.
    /// </summary>
    [Fact]
    public void ItWaitsWhileAFullScreenDirect3DProgramOrPresentationModeRuns()
    {
        var log = new List<string>();
        bool presenting = true;
        long now = 1_000_000;
        var staying = new StayingNotice(() => { }, log.Add, () => presenting, () => now);
        Assert.True(staying.Hold(NonPaged));
        Assert.Same(NonPaged, staying.Held);
        Assert.Null(staying.Open);
        Assert.Equal(["notice held while a full-screen Direct3D program or presentation mode runs (it shows when that ends, or after 2 minutes at most): " +
                      "Non-paged pool over the limit"], log);
        var newer = NonPaged with { Text = "Kernel pool: Open files (File) is growing. PoolWatch is keeping an eye on Codex." };
        now += 30_000;
        Assert.True(staying.Hold(newer));
        Assert.Null(staying.Release()); // still presenting
        presenting = false;
        now += 15_000;
        var released = staying.Release();
        Assert.True(released.HasValue);
        Assert.Same(newer, released.Value.Notice);
        Assert.Equal(45_000, released.Value.HeldMs); // from when the first began to wait
        Assert.Equal("notice shown after it was held 45 seconds (it waits 2 minutes at most; it stays until it is closed): Non-paged pool over the limit",
            StayingNotice.ShownLine(released.Value.Notice, released.Value.HeldMs));
        Assert.Equal("notice shown (it stays until it is closed): Non-paged pool over the limit", StayingNotice.ShownLine(NonPaged, null));
        Assert.Null(staying.Held);
        Assert.Null(staying.Release());
        Assert.False(staying.Hold(NonPaged)); // nothing runs full screen: it shows at once
        Assert.Null(staying.Held);
        // Windows' states.
        Assert.Equal(new[] { 3, 4 }, Enumerable.Range(0, 9).Where(PoolWatch.Native.NotificationState.Holds));
        Assert.Equal((2, 3, 4), (PoolWatch.Native.NotificationState.Busy, PoolWatch.Native.NotificationState.RunningDirect3DFullScreen,
            PoolWatch.Native.NotificationState.PresentationMode));
    }

    /// <summary>
    /// It never waits more than 2 minutes, whatever runs: on the night that ended in a hang this notice came 6 minutes
    /// before it, so a game or a presentation that runs on must not keep it back until after it. A newer notice in its
    /// place is timed from when the first began to wait, so notices that keep coming never hold it longer; once one has
    /// waited 2 minutes, a newer one is not held either (it shows at once, in its place). A notice that comes later waits
    /// its own 2 minutes at most. The log says it was held and for how long.
    /// </summary>
    [Fact]
    public void ItNeverWaitsMoreThanTwoMinutesWhateverRuns()
    {
        var log = new List<string>();
        long now = 5_000_000;
        var staying = new StayingNotice(() => { }, log.Add, () => true, () => now); // a full-screen program that never ends
        Assert.Equal(2 * 60 * 1000, StayingNotice.MaxHoldMs);
        Assert.True(staying.Hold(NonPaged));
        now += 60_000;
        var newer = NonPaged with { Text = "Kernel pool: Open files (File) is growing. PoolWatch is keeping an eye on Codex." };
        Assert.True(staying.Hold(newer));
        now += 59_999;
        Assert.Null(staying.Release()); // a millisecond short of 2 minutes
        now += 1;
        var released = staying.Release();
        Assert.True(released.HasValue);
        Assert.Same(newer, released.Value.Notice);
        Assert.Equal(StayingNotice.MaxHoldMs, released.Value.HeldMs);
        Assert.Null(staying.Held);
        Assert.Equal("notice shown after it was held 120 seconds (it waits 2 minutes at most; it stays until it is closed): Non-paged pool over the limit",
            StayingNotice.ShownLine(released.Value.Notice, released.Value.HeldMs));
        // A notice that comes later waits its own 2 minutes at most.
        now += 10_000;
        Assert.True(staying.Hold(NonPaged));
        now += StayingNotice.MaxHoldMs - 1;
        Assert.Null(staying.Release());
        // Once it has waited 2 minutes, a newer one is not held: it shows at once, in place of the one that waited (Show
        // takes that one, with how long it waited, as Release does here).
        now += 1;
        Assert.False(staying.Hold(newer));
        var older = staying.Release();
        Assert.True(older.HasValue);
        Assert.Same(NonPaged, older.Value.Notice);
        Assert.Equal(StayingNotice.MaxHoldMs, older.Value.HeldMs);
        Assert.Equal(3, log.Count(l => l.StartsWith("notice held", StringComparison.Ordinal))); // the one not held is not logged as held
    }
}
