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
}
