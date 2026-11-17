using System.Runtime.InteropServices;

namespace PoolWatch.Native;

/// <summary>
/// Whether Windows holds notices back now, as it does its own toasts: SHQueryUserNotificationState (shellapi.h), which
/// only reads the state. The notice that stays (Ui.StayingNotice) waits while a presentation or a full-screen program
/// runs, so a window above all others with a pool tag's and a program's names never shows over a screen share or a
/// presentation. Focus Assist's "do not disturb" has no documented call to read it, so it is not known here.
/// </summary>
internal static class NotificationState
{
    /// <summary>QUERY_USER_NOTIFICATION_STATE (shellapi.h): the values the call gives.</summary>
    internal const int NotPresent = 1, Busy = 2, RunningDirect3DFullScreen = 3, PresentationMode = 4, AcceptsNotifications = 5, QuietTime = 6, App = 7;

    /// <summary>
    /// The states in which the notice waits: a full-screen program or presentation settings (QUNS_BUSY), a full-screen
    /// Direct3D program, a game among them (QUNS_RUNNING_D3D_FULL_SCREEN), and presentation mode (QUNS_PRESENTATION_MODE).
    /// In any other, it shows.
    /// </summary>
    internal static bool Holds(int state) => state is Busy or RunningDirect3DFullScreen or PresentationMode;

    /// <summary>
    /// True while Windows says a presentation or a full-screen program runs (<see cref="Holds"/>); false when it says
    /// otherwise or cannot say (the call failed), so a notice is never lost to the question.
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
