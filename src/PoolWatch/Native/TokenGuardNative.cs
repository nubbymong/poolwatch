using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PoolWatch.Native;

/// <summary>
/// The Windows calls of the token-leak guard: the live ForegroundLockTimeout, the reference count on a
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
    public static bool SetForegroundLockTimeoutZero() => SetForegroundLockTimeout(0);

    /// <summary>
    /// Sets the LIVE value to <paramref name="milliseconds"/> (SPIF_SENDCHANGE only, never the registry). Windows accepts
    /// it only from a program that may bring a window to the front ("The calling thread must be able to change the
    /// foreground window, otherwise the call fails", SystemParametersInfo); read back to know whether it took.
    /// </summary>
    public static bool SetForegroundLockTimeout(uint milliseconds)
    {
        SystemParametersInfoW(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, (nint)milliseconds, SPIF_SENDCHANGE);
        return GetForegroundLockTimeout() == milliseconds;
    }

    // ---- the sign-in the live setting belongs to

    /// <summary>WTSINFOW (wtsapi32.h): WINSTATIONNAME_LENGTH 32, DOMAIN_LENGTH 17, USERNAME_LENGTH 20.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WTSINFOW
    {
        public int State;
        public uint SessionId, IncomingBytes, OutgoingBytes, IncomingFrames, OutgoingFrames, IncomingCompressedBytes, OutgoingCompressedBytes;
        public fixed char WinStationName[32];
        public fixed char Domain[17];
        public fixed char UserName[21];
        public long ConnectTime, DisconnectTime, LastInputTime, LogonTime, CurrentTime;
    }

    private const int WTSSessionInfo = 24;
    private const uint WTS_CURRENT_SESSION = uint.MaxValue;
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQuerySessionInformationW(nint server, uint session, int infoClass, out nint buffer, out uint bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(nint memory);

    /// <summary>
    /// This sign-in: the Windows session this process runs in and the time its user signed in to it, as
    /// "session:logon-time" (a FILETIME). Every process of the sign-in reads the same, elevated or not; another sign-in
    /// reads another (a new session, or the same number signed in at another time). Windows keeps the live
    /// ForegroundLockTimeout per session and sets it again at each sign-in. Null when Windows does not say.
    /// </summary>
    public static string? SignInId()
    {
        if (!WTSQuerySessionInformationW(0, WTS_CURRENT_SESSION, WTSSessionInfo, out nint buffer, out uint bytes) || buffer == 0) return null;
        try
        {
            if (bytes < sizeof(WTSINFOW)) return null;
            var info = (WTSINFOW*)buffer;
            return info->LogonTime > 0 ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{info->SessionId}:{info->LogonTime}") : null;
        }
        finally { WTSFreeMemory(buffer); }
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
        // CreateProcessW may write to the command line, so it gets a buffer of its own, never a .NET string's characters.
        char[] line = $"\"{cmd}\" /d /c rem\0".ToCharArray();
        fixed (char* p = line)
        {
            if (!CreateProcessW(cmd, p, 0, 0, false, flags, 0, Environment.SystemDirectory, ref si, out var pi))
                throw new InvalidOperationException($"CreateProcess ({way}) failed: {Marshal.GetLastWin32Error()}");
            WaitForSingleObject(pi.hProcess, 30_000);
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
        }
    }

    // ---- the process that started a logon check's helper

    private const uint SYNCHRONIZE = 0x00100000, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    /// <summary>This process's creation time (a FILETIME).</summary>
    public static long OwnCreationTime() =>
        GetProcessTimes(GetCurrentProcess(), out long created, out _, out _, out _) ? created : throw new InvalidOperationException("GetProcessTimes failed: " + Marshal.GetLastWin32Error());

    /// <summary>
    /// A handle to wait on process <paramref name="pid"/>, only while it is the process created at
    /// <paramref name="creationTime"/>; 0 when no such process can be opened or the ID now belongs to another process (the
    /// one that had it has ended). The handle keeps naming that process after it ends, whoever is given the ID next.
    /// </summary>
    public static nint OpenIfSame(int pid, long creationTime)
    {
        nint process = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0) return 0;
        if (GetProcessTimes(process, out long created, out _, out _, out _) && created == creationTime) return process;
        CloseHandle(process);
        return 0;
    }

    /// <summary>True once the process behind <paramref name="process"/> has ended.</summary>
    public static bool HasEnded(nint process) => WaitForSingleObject(process, 0) == 0;

    public static void Close(nint handle)
    {
        if (handle != 0) CloseHandle(handle);
    }

    // ---- the program in front

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(nint process, uint flags, char* name, ref uint size);

    /// <summary>
    /// The program in front now: the file name of the program of the process that owns the foreground window
    /// ("Code.exe"), or "process 1234" when that process cannot be opened to read it. Null when there is no foreground
    /// window (Windows has none while a window is losing activation, for one) or its process cannot be told. Only the file
    /// name: no folder, so no user name, goes into the log or the findings.
    /// </summary>
    public static string? ProgramInFront()
    {
        nint window = GetForegroundWindow();
        if (window == 0 || GetWindowThreadProcessId(window, out uint pid) == 0 || pid == 0) return null;
        nint process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (int)pid);
        if (process == 0) return $"process {pid}";
        try
        {
            const int Room = 1024;
            char* name = stackalloc char[Room];
            uint size = Room;
            return QueryFullProcessImageNameW(process, 0, name, ref size) && size > 0 ? Path.GetFileName(new string(name, 0, (int)size)) : $"process {pid}";
        }
        finally { CloseHandle(process); }
    }

    // ---- foreground for the one-click path
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("kernel32.dll")] public static extern bool AttachConsole(int pid);
    [DllImport("kernel32.dll")] public static extern nint GetConsoleWindow();
}
