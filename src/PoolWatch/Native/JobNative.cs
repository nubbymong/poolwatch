using System.Runtime.InteropServices;

namespace PoolWatch.Native;

/// <summary>
/// A job object that ends every process in it once its last handle is closed (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE), and a
/// process started in one: created suspended, put in the job, then let run, so every process it starts is in the job
/// too. PoolWatch holds the job's only handle, so the processes in it end when PoolWatch ends the job, closes it, or exits
/// in any way (Windows closes the handles of a process that ends, also one that is killed).
/// </summary>
internal static unsafe class JobNative
{
    internal const uint KillOnJobClose = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
    private const int ExtendedLimitInformation = 9; // JobObjectExtendedLimitInformation
    private const uint CREATE_SUSPENDED = 0x4, CREATE_NO_WINDOW = 0x08000000;

    /// <summary>JOBOBJECT_EXTENDED_LIMIT_INFORMATION, its JOBOBJECT_BASIC_LIMIT_INFORMATION and IO_COUNTERS laid out in line.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern nint CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(nint job, int infoClass, void* info, int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(nint job, int infoClass, void* info, int length, out int returned);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(nint job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(nint process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(nint process, out uint exitCode);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? app, char* commandLine, nint procAttr, nint threadAttr, bool inherit, uint flags, nint env, string? cwd, ref STARTUPINFOW si, out PROCESS_INFORMATION pi);

    /// <summary>A new job that ends its processes when its last handle closes. Throws when Windows refuses.</summary>
    public static nint CreateKillOnClose()
    {
        nint job = CreateJobObjectW(0, null);
        if (job == 0) throw new InvalidOperationException("CreateJobObject failed: " + Marshal.GetLastWin32Error());
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { LimitFlags = KillOnJobClose };
        if (SetInformationJobObject(job, ExtendedLimitInformation, &info, sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION))) return job;
        int error = Marshal.GetLastWin32Error();
        CloseHandle(job);
        throw new InvalidOperationException("SetInformationJobObject failed: " + error);
    }

    /// <summary>The job's limit flags as Windows holds them.</summary>
    public static uint LimitFlags(nint job)
    {
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION info;
        if (!QueryInformationJobObject(job, ExtendedLimitInformation, &info, sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION), out _))
            throw new InvalidOperationException("QueryInformationJobObject failed: " + Marshal.GetLastWin32Error());
        return info.LimitFlags;
    }

    /// <summary>
    /// Starts <c>"app" arguments</c> in <paramref name="job"/> with no window (CREATE_NO_WINDOW, as
    /// ProcessStartInfo.CreateNoWindow starts it): suspended, put in the job, then let run. Returns the process handle. On
    /// any failure nothing runs and it throws.
    /// </summary>
    public static nint StartInJob(nint job, string app, string arguments, string workingDirectory)
    {
        var si = new STARTUPINFOW { cb = sizeof(STARTUPINFOW) };
        // CreateProcessW may write to the command line, so it gets a buffer of its own, never a .NET string's characters.
        char[] line = $"\"{app}\" {arguments}\0".ToCharArray();
        PROCESS_INFORMATION pi;
        fixed (char* p = line)
            if (!CreateProcessW(app, p, 0, 0, false, CREATE_SUSPENDED | CREATE_NO_WINDOW, 0, workingDirectory, ref si, out pi))
                throw new InvalidOperationException("CreateProcess failed: " + Marshal.GetLastWin32Error());
        try
        {
            if (!AssignProcessToJobObject(job, pi.hProcess)) throw new InvalidOperationException("AssignProcessToJobObject failed: " + Marshal.GetLastWin32Error());
            if (ResumeThread(pi.hThread) == uint.MaxValue) throw new InvalidOperationException("ResumeThread failed: " + Marshal.GetLastWin32Error());
        }
        catch
        {
            TerminateProcess(pi.hProcess, 1); // still suspended: it ran nothing
            CloseHandle(pi.hProcess);
            throw;
        }
        finally { CloseHandle(pi.hThread); }
        return pi.hProcess;
    }

    /// <summary>Ends every process in the job.</summary>
    public static bool Terminate(nint job) => TerminateJobObject(job, 1);

    /// <summary>True once the process has ended (within <paramref name="milliseconds"/>).</summary>
    public static bool WaitForExit(nint process, int milliseconds) => WaitForSingleObject(process, (uint)milliseconds) == 0;

    public static int ExitCode(nint process) => GetExitCodeProcess(process, out uint code) ? unchecked((int)code) : -1;

    public static void Close(nint handle)
    {
        if (handle != 0) CloseHandle(handle);
    }
}
