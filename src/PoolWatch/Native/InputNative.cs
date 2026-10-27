using System.Runtime.InteropServices;

namespace PoolWatch.Native;

/// <summary>
/// The session's last keyboard / mouse input (C1: no automatic trace while the owner is typing). GetLastInputInfo reads
/// the input time of the session PoolWatch runs in; nothing is hooked and no input is read.
/// </summary>
internal static class InputNative
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("kernel32.dll")] private static extern uint GetTickCount();

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    /// <summary>The process whose window is in front (addendum 4 C: "no window in front"); 0 when none or unknown.</summary>
    public static int ForegroundPid()
    {
        try
        {
            var h = GetForegroundWindow();
            if (h == IntPtr.Zero) return 0;
            GetWindowThreadProcessId(h, out uint pid);
            return (int)pid;
        }
        catch { return 0; }
    }

    /// <summary>Time since the last input in this session; null when it cannot be read (no interactive session).</summary>
    public static TimeSpan? SinceLastInput()
    {
        try
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref info)) return null;
            // Both are 32-bit tick counts: the unsigned difference is right across the 49.7-day wrap.
            return TimeSpan.FromMilliseconds(unchecked(GetTickCount() - info.dwTime));
        }
        catch { return null; }
    }
}
