using System.Runtime.InteropServices;

namespace PoolWatch.Ui;

/// <summary>
/// The tray icon after Explorer restarts. A new taskbar has no icon of PoolWatch's until it is added again, and Explorer
/// says it is there by broadcasting "TaskbarCreated" to every top-level window. Windows stops that message on its way from
/// Explorer to an elevated program (UIPI: a program at a lower integrity level may not send it to one at a higher), so
/// the NotifyIcon's own window, which would add the icon again, never gets it, and the icon stayed gone until PoolWatch
/// was started again. This hidden top-level window (a message-only one gets no broadcast) lets that one message through
/// for itself (ChangeWindowMessageFilterEx, MSGFLT_ALLOW: this window only, nothing else of the process) and calls
/// <c>created</c> when it comes, which adds the icon again. One window and one registered message; nothing runs until
/// Explorer sends it.
/// </summary>
internal sealed class TaskbarWatch : NativeWindow, IDisposable
{
    /// <summary>The message Explorer broadcasts once its taskbar is (back) in place; 0 when Windows would not register it.</summary>
    internal static readonly int TaskbarCreated = (int)RegisterWindowMessageW("TaskbarCreated");

    private const int WS_EX_TOOLWINDOW = 0x0000_0080;
    private const uint MSGFLT_ALLOW = 1;
    private readonly Action _created;

    /// <param name="created">Runs on this thread (the UI thread) each time Explorer's taskbar is created again.</param>
    public TaskbarWatch(Action created)
    {
        _created = created;
        CreateHandle(new CreateParams { ExStyle = WS_EX_TOOLWINDOW, Caption = "PoolWatch taskbar watch" });
        Allowed = Allow(Handle, out _);
    }

    /// <summary>Whether Windows lets "TaskbarCreated" through to this window from a program at a lower integrity level.</summary>
    public bool Allowed { get; }

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
        if (TaskbarCreated != 0 && m.Msg == TaskbarCreated)
        {
            try { _created(); }
            catch { /* the tray icon's own code logs; a broadcast must never take the UI thread down */ }
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
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
}
