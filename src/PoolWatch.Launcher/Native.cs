using System.Runtime.InteropServices;

// System DLLs only from System32, never from the launcher's folder or the current folder.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("PoolWatch.AppTests")]

namespace PoolWatch.Launcher;

/// <summary>The Windows calls the launcher makes (signatures as in the Windows headers).</summary>
internal static unsafe partial class Native
{
    internal const uint CREATE_UNICODE_ENVIRONMENT = 0x0000_0400;
    internal const int STARTF_USESHOWWINDOW = 0x0001, STARTF_USESTDHANDLES = 0x0100;
    /// <summary>The shortcut the caller was started from (lpTitle is its path), an AppUserModelID in lpTitle, and "do not pin".</summary>
    internal const int STARTF_TITLEISLINKNAME = 0x0800, STARTF_TITLEISAPPID = 0x1000, STARTF_PREVENTPINNING = 0x2000;
    private const uint INFINITE = 0xFFFF_FFFF, WAIT_OBJECT_0 = 0;
    private const uint ATTACH_PARENT_PROCESS = unchecked((uint)-1);
    private const uint MB_OK = 0, MB_ICONERROR = 0x10;
    private const int WinBuiltinAdministratorsSid = 26, SECURITY_MAX_SID_SIZE = 68;

    [StructLayout(LayoutKind.Sequential)]
    internal struct STARTUPINFOW
    {
        public int cb;
        public nint lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public nint hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(char* applicationName, char* commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, char* environment, char* currentDirectory,
        STARTUPINFOW* startupInfo, PROCESS_INFORMATION* processInformation);

    [LibraryImport("kernel32.dll")]
    private static partial void GetStartupInfoW(STARTUPINFOW* startupInfo);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(nint process, uint* exitCode);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleCtrlHandler(delegate* unmanaged<uint, int> handler, [MarshalAs(UnmanagedType.Bool)] bool add);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateWellKnownSid(int sidType, nint domainSid, byte* sid, uint* sidSize);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CheckTokenMembership(nint token, byte* sid, int* isMember);

    /// <summary>
    /// Whether the launcher stays on a console event: Ctrl+C and Ctrl+Break (0 and 1) reach the PoolWatch it started too,
    /// which stops on its own, so the launcher keeps waiting for it and ends with its exit code; a console closing, a
    /// sign-out or a shutdown ends it as usual.
    /// </summary>
    internal static bool KeepsWaitingOn(uint consoleEvent) => consoleEvent is 0 or 1;

    [UnmanagedCallersOnly]
    private static int OnConsoleEvent(uint consoleEvent) => KeepsWaitingOn(consoleEvent) ? 1 : 0;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetSystemWindowsDirectoryW(char* buffer, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetModuleFileNameW(nint module, char* buffer, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateFileW(char* name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetFinalPathNameByHandleW(nint file, char* buffer, uint size, uint flags);

    private const uint FILE_READ_ATTRIBUTES = 0x0080, FILE_SHARE_ALL = 0x0007, OPEN_EXISTING = 3;
    private const nint INVALID_HANDLE_VALUE = -1;

    /// <summary>
    /// The launcher's own exe as the kernel names it (GetFinalPathNameByHandleW on the open file, FILE_NAME_NORMALIZED with
    /// its drive letter): every folder in its long name, a junction or a subst drive it was started through resolved to the
    /// folder it is really in. Null when it cannot be read, or when it is not on a local drive (a network share).
    /// </summary>
    public static string? OwnFinalPath()
    {
        const int Max = 32_768;
        char* module = stackalloc char[Max];
        uint length = GetModuleFileNameW(0, module, Max);
        return length is 0 or >= Max ? null : FinalPathOf(new string(module, 0, (int)length));
    }

    /// <summary>
    /// The file at <paramref name="path"/> as the kernel names it once it is open (see <see cref="OwnFinalPath"/>): its
    /// long name in the case it has on disk, through any junction or subst drive; null when it cannot be opened or is not
    /// on a local drive.
    /// </summary>
    internal static string? FinalPathOf(string path) => FinalPathOf(path, 0);

    /// <summary>
    /// The folder at <paramref name="path"/> as the kernel names it once it is open (FILE_FLAG_BACKUP_SEMANTICS, which
    /// opening a folder needs): every link on the way followed, so the folder rules judge the folder it really is (<see
    /// cref="Engine.AdminPlaces.RealPath"/>); null when it cannot be opened or is not on a local drive.
    /// </summary>
    internal static string? FolderFinalPathOf(string path) => FinalPathOf(path, FILE_FLAG_BACKUP_SEMANTICS);

    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x0200_0000;

    private static string? FinalPathOf(string path, uint flags)
    {
        const int Max = 32_768;
        nint file;
        fixed (char* name = path) file = CreateFileW(name, FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, 0, OPEN_EXISTING, flags, 0);
        if (file == INVALID_HANDLE_VALUE) return null;
        try
        {
            char* final = stackalloc char[Max];
            uint got = GetFinalPathNameByHandleW(file, final, Max, 0); // FILE_NAME_NORMALIZED | VOLUME_NAME_DOS
            return got is 0 or >= Max ? null : LocalPath(new string(final, 0, (int)got));
        }
        finally { CloseHandle(file); }
    }

    /// <summary>
    /// Pure: a path as GetFinalPathNameByHandleW gives it (\\?\C:\...) as a plain local path (C:\...); null for one that
    /// is not on a drive letter (\\?\UNC\server\share\..., a volume GUID path).
    /// </summary>
    internal static string? LocalPath(string final)
    {
        string path = final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
        return EnvironmentBlock.IsFullPath(path) ? path : null;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(nint owner, string text, string caption, uint type);

    /// <summary>
    /// The console of the process that started the launcher, if it has one, so PoolWatch's console modes write there
    /// (PoolWatch attaches to the launcher's console); then Ctrl+C there leaves the launcher waiting (<see cref="KeepsWaitingOn"/>).
    /// </summary>
    public static void AttachParentConsole()
    {
        if (AttachConsole(ATTACH_PARENT_PROCESS)) SetConsoleCtrlHandler(&OnConsoleEvent, true);
    }

    /// <summary>The Windows folder, from the kernel (GetSystemWindowsDirectoryW, never %SystemRoot%); empty when it cannot be read.</summary>
    public static string SystemWindowsDirectory()
    {
        char* buffer = stackalloc char[261];
        uint length = GetSystemWindowsDirectoryW(buffer, 261);
        return length is > 0 and < 261 ? new string(buffer, 0, (int)length) : "";
    }

    /// <summary>
    /// Whether the launcher runs with administrator rights: the Administrators group is enabled in its token (the test
    /// PoolWatch makes too). Its manifest asks for them, but a compatibility setting that runs it as the invoker, or User
    /// Account Control turned off for a standard account, starts it without them. False when it cannot be told.
    /// </summary>
    public static bool IsElevated()
    {
        byte* sid = stackalloc byte[SECURITY_MAX_SID_SIZE];
        uint size = SECURITY_MAX_SID_SIZE;
        int member = 0;
        return CreateWellKnownSid(WinBuiltinAdministratorsSid, 0, sid, &size) && CheckTokenMembership(0, sid, &member) && member != 0;
    }

    public static void ShowError(string text) => MessageBoxW(0, text, "PoolWatch", MB_OK | MB_ICONERROR);

    /// <summary>
    /// The AppUserModelID the launcher gives the PoolWatch it starts when it was not started from a shortcut, always with
    /// STARTF_PREVENTPINNING (<see cref="ForChild"/>).
    /// </summary>
    internal const string AppUserModelId = "PoolWatch.Monitor";

    /// <summary>
    /// Pure: what the PoolWatch the launcher starts gets of the launcher's own start (<paramref name="caller"/>, from
    /// GetStartupInfoW): the window state (a shortcut's "run minimised"), the caller's standard handles (so a console
    /// mode's output reaches it), and the shortcut it was started from with its "do not pin" (so its window groups with
    /// that shortcut on the taskbar, and a pin of it is that shortcut). Started any other way (the logon task, Explorer, a
    /// console), PoolWatch gets an AppUserModelID (the caller's, or <paramref name="ownAppId"/>, a pointer to <see
    /// cref="AppUserModelId"/>) with STARTF_PREVENTPINNING, so none of its windows can be pinned to the taskbar: such a
    /// pin would name app\PoolWatch.exe itself, not the launcher. Nothing else: no desktop, position or size. <paramref
    /// name="inheritHandles"/>: only when the caller gave standard handles.
    /// </summary>
    internal static STARTUPINFOW ForChild(in STARTUPINFOW caller, nint ownAppId, out bool inheritHandles)
    {
        var si = new STARTUPINFOW { cb = sizeof(STARTUPINFOW) };
        if ((caller.dwFlags & STARTF_USESHOWWINDOW) != 0)
        {
            si.dwFlags |= STARTF_USESHOWWINDOW;
            si.wShowWindow = caller.wShowWindow;
        }
        inheritHandles = (caller.dwFlags & STARTF_USESTDHANDLES) != 0;
        if (inheritHandles)
        {
            si.dwFlags |= STARTF_USESTDHANDLES;
            si.hStdInput = caller.hStdInput;
            si.hStdOutput = caller.hStdOutput;
            si.hStdError = caller.hStdError;
        }
        if ((caller.dwFlags & STARTF_TITLEISLINKNAME) != 0 && caller.lpTitle != 0)
        {
            si.dwFlags |= caller.dwFlags & (STARTF_TITLEISLINKNAME | STARTF_PREVENTPINNING);
            si.lpTitle = caller.lpTitle;
        }
        else
        {
            si.dwFlags |= STARTF_TITLEISAPPID | STARTF_PREVENTPINNING;
            si.lpTitle = (caller.dwFlags & STARTF_TITLEISAPPID) != 0 && caller.lpTitle != 0 ? caller.lpTitle : ownAppId;
        }
        return si;
    }

    /// <summary>
    /// Starts <paramref name="program"/> with exactly <paramref name="commandLine"/> and <paramref name="environment"/>
    /// (a block from <see cref="EnvironmentBlock.Serialize"/>, passed with CREATE_UNICODE_ENVIRONMENT, so the child gets
    /// that block and nothing of the launcher's own environment), in <paramref name="folder"/> as its current folder (the
    /// launcher gives PoolWatch's own app folder, so nothing loaded by a bare name before PoolWatch's own code runs comes
    /// from the folder the caller was in; that folder goes to PoolWatch in the block, EnvironmentBlock.StartedIn), waits
    /// for it to end and gives its exit code. What it passes on of the launcher's own start is <see cref="ForChild"/>'s.
    /// Handles are inherited only when the caller gave standard handles, and then only those the caller made inheritable,
    /// as when it starts the program itself; the launcher makes none. False with <paramref name="error"/> when it could
    /// not be started.
    /// </summary>
    public static bool Run(string program, string commandLine, string environment, string folder, out int exitCode, out string? error)
    {
        exitCode = 0;
        error = null;
        STARTUPINFOW caller;
        GetStartupInfoW(&caller);
        PROCESS_INFORMATION pi;
        // CreateProcessW may write to the command line, so it gets a copy of its own, never a .NET string's characters.
        char[] line = new char[commandLine.Length + 1];
        commandLine.CopyTo(0, line, 0, commandLine.Length);
        fixed (char* app = program)
        fixed (char* cmd = line)
        fixed (char* env = environment)
        fixed (char* current = folder)
        fixed (char* appId = AppUserModelId)
        {
            var si = ForChild(caller, (nint)appId, out bool handles);
            if (!CreateProcessW(app, cmd, 0, 0, handles, CREATE_UNICODE_ENVIRONMENT, env, current, &si, &pi))
            {
                error = $"Windows could not start {program} (error {Marshal.GetLastPInvokeError()}).";
                return false;
            }
        }
        try
        {
            CloseHandle(pi.hThread);
            uint code;
            if (WaitForSingleObject(pi.hProcess, INFINITE) != WAIT_OBJECT_0 || !GetExitCodeProcess(pi.hProcess, &code))
            {
                error = $"PoolWatch started, but the launcher could not wait for it (error {Marshal.GetLastPInvokeError()}).";
                return false;
            }
            exitCode = unchecked((int)code);
            return true;
        }
        finally { CloseHandle(pi.hProcess); }
    }
}
