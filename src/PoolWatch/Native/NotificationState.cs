using System.Runtime.InteropServices;

namespace PoolWatch.Native;

/// <summary>
/// Whether Windows holds notices back now, as it does its own toasts: SHQueryUserNotificationState (shellapi.h), which
/// only reads the state. The notice that stays (Ui.StayingNotice) waits, for 2 minutes at most, while a full-screen
/// Direct3D program or presentation mode runs, so a window above all others with a pool tag's and a program's names does
/// not show over a game or a presentation. Focus Assist's "do not disturb" has no documented call to read it, so it is not
/// known here.
/// </summary>
internal static class NotificationState
{
    /// <summary>QUERY_USER_NOTIFICATION_STATE (shellapi.h): the values the call gives.</summary>
    internal const int NotPresent = 1, Busy = 2, RunningDirect3DFullScreen = 3, PresentationMode = 4, AcceptsNotifications = 5, QuietTime = 6, App = 7;

    /// <summary>
    /// The states in which the notice waits (2 minutes at most, Ui.StayingNotice.MaxHoldMs): a Direct3D program in
    /// exclusive full screen, as some games run (QUNS_RUNNING_D3D_FULL_SCREEN), and presentation mode, Windows'
    /// presentation settings (QUNS_PRESENTATION_MODE). Not busy (QUNS_BUSY), which Windows says for any full-screen window, such as a video in a
    /// browser or a Remote Desktop window: the non-paged pool's notice can come minutes before a hang, and must not wait
    /// out a film. In any other state, it shows.
    /// </summary>
    internal static bool Holds(int state) => state is RunningDirect3DFullScreen or PresentationMode;

    /// <summary>
    /// True while Windows says a full-screen Direct3D program or presentation mode runs (<see cref="Holds"/>); false when
    /// it says otherwise or cannot say (the call failed), so a notice is never lost to the question.
    /// </summary>
    public static bool Presenting()
    {
        try { return SHQueryUserNotificationState(out int state) == 0 && Holds(state); }
        catch (Exception) { return false; } // an older shell32 without it, or one the call cannot be made in
    }

    /// <summary>HRESULT SHQueryUserNotificationState(QUERY_USER_NOTIFICATION_STATE *pquns); the enumeration is 4 bytes.</summary>
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHQueryUserNotificationState(out int state);
}
