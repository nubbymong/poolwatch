using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PoolWatch.Native;

/// <summary>
/// The Windows calls of the token-leak guard (addendum 3): the live ForegroundLockTimeout, the reference count on a
/// process's own primary token, and starting a hidden child three ways. The workaround is from
/// gunrgbcr/windows-token-leak-logon-fix (re-implemented here; see the README).
/// </summary>
internal static unsafe class TokenGuardNative
{
    private const uint SPI_GETFOREGROUNDLOCKTIMEOUT = 0x2000, SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001, SPIF_SENDCHANGE = 0x2;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool SystemParametersInfoW(uint action, uint uiParam, nint pvParam, uint winIni);

    /// <summary>The live value (ms), or null when Windows does not report it.</summary>
    public static uint? GetForegroundLockTimeout()
    {
        uint v = 0;
        return SystemParametersInfoW(SPI_GETFOREGROUNDLOCKTIMEOUT, 0, (nint)(&v), 0) ? v : null;
    }

    /// <summary>
    /// Sets the LIVE value to 0 for this logon (SPIF_SENDCHANGE only, never SPIF_UPDATEINIFILE: the registry value stays
    /// at its default). Windows accepts it only from the foreground program; read back to know whether it took.
    /// </summary>
    public static bool SetForegroundLockTimeoutZero()
    {
        SystemParametersInfoW(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, 0, SPIF_SENDCHANGE);
        return GetForegroundLockTimeout() == 0;
    }

    // ---- the leak test helper

    [StructLayout(LayoutKind.Sequential)]
    private struct PUBLIC_OBJECT_BASIC_INFORMATION
    {
        public uint Attributes, GrantedAccess, HandleCount, PointerCount;
        public fixed uint Reserved[10];
    }

    [DllImport("ntdll.dll")] private static extern int NtQueryObject(nint handle, int infoClass, void* info, int length, out int returned);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint h);

    /// <summary>The kernel's reference (pointer) count on this process's own primary token.</summary>
    public static long OwnTokenReferences()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0008 /* TOKEN_QUERY */, out var token)) throw new InvalidOperationException("OpenProcessToken failed: " + Marshal.GetLastWin32Error());
        try
        {
            PUBLIC_OBJECT_BASIC_INFORMATION info;
            int st = NtQueryObject(token, 0 /* ObjectBasicInformation */, &info, sizeof(PUBLIC_OBJECT_BASIC_INFORMATION), out _);
            if (st != 0) throw new InvalidOperationException($"NtQueryObject returned 0x{st:X8}");
            return info.PointerCount;
        }
        finally { CloseHandle(token); }
    }

    public enum ChildWay { InheritedConsole, NewConsoleHidden, NoWindow }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public int cb;
        public nint lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public nint hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? app, char* commandLine, nint procAttr, nint threadAttr, bool inherit, uint flags, nint env, string? cwd, ref STARTUPINFOW si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint h, uint ms);

    /// <summary>Starts <c>cmd.exe /d /c rem</c> one of the three ways and waits for it (at most 30 s).</summary>
    public static void RunChild(ChildWay way)
    {
        const uint CREATE_NEW_CONSOLE = 0x10, CREATE_NO_WINDOW = 0x08000000;
        const int STARTF_USESHOWWINDOW = 1;
        var si = new STARTUPINFOW { cb = sizeof(STARTUPINFOW) };
        uint flags = 0;
        if (way == ChildWay.NewConsoleHidden) { flags = CREATE_NEW_CONSOLE; si.dwFlags = STARTF_USESHOWWINDOW; si.wShowWindow = 0 /* SW_HIDE */; }
        else if (way == ChildWay.NoWindow) flags = CREATE_NO_WINDOW;
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        string line = $"\"{cmd}\" /d /c rem\0";
        fixed (char* p = line)
        {
            if (!CreateProcessW(cmd, p, 0, 0, false, flags, 0, Environment.SystemDirectory, ref si, out var pi))
                throw new InvalidOperationException($"CreateProcess ({way}) failed: {Marshal.GetLastWin32Error()}");
            WaitForSingleObject(pi.hProcess, 30_000);
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
        }
    }

    // ---- foreground for the one-click path
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("kernel32.dll")] public static extern bool AttachConsole(int pid);
    [DllImport("kernel32.dll")] public static extern nint GetConsoleWindow();
}
