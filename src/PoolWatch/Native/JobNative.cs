using System.Runtime.InteropServices;
using System.Text;

namespace PoolWatch.Native;

/// <summary>
/// A job object, and a process started in it. The job ends its processes when its last handle is closed
/// (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE), and PoolWatch holds its only handle: the job is unnamed and its handle is not
/// inheritable. Windows closes the handles of a process that ends, also one that is killed or crashes, so the job ends
/// what runs in it when PoolWatch ends the job, closes it, or exits in any way. The process is created in the job
/// (PROC_THREAD_ATTRIBUTE_JOB_LIST), so there is no moment at which it exists outside it; it is created suspended, checked
/// to be in the job (IsProcessInJob), and only then let run. So a kill of PoolWatch at any moment of the start leaves
/// nothing behind, not even a suspended process. It inherits no handle (bInheritHandles FALSE) and gets the environment it
/// is given, nothing of PoolWatch's own. Every process it starts is in the job too (the job allows no breakaway). A start
/// asked to outlive PoolWatch has the kill-on-close limit taken off its job once it runs; PoolWatch then ends what runs
/// in it only through the job's handle (TerminateJobObject), never by process ID.
/// </summary>
internal static unsafe class JobNative
{
    internal const uint KillOnJobClose = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
    private const int ExtendedLimitInformation = 9; // JobObjectExtendedLimitInformation
    internal const uint CREATE_SUSPENDED = 0x4, CREATE_UNICODE_ENVIRONMENT = 0x400, EXTENDED_STARTUPINFO_PRESENT = 0x80000, CREATE_NO_WINDOW = 0x08000000;
    /// <summary>PROC_THREAD_ATTRIBUTE_JOB_LIST: ProcThreadAttributeValue(13, FALSE, TRUE, FALSE).</summary>
    internal const int ProcThreadAttributeJobList = 0x2000D;

    /// <summary>
    /// The creation flags of every start: suspended until it is seen in its job, no window (as ProcessStartInfo.CreateNoWindow
    /// starts it), an explicit Unicode environment, and the extended start-up information that carries the job list.
    /// </summary>
    internal const uint StartFlags = CREATE_SUSPENDED | CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT | EXTENDED_STARTUPINFO_PRESENT;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOW
    {
        public int cb;
        public nint lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)] private struct STARTUPINFOEXW { public STARTUPINFOW StartupInfo; public nint lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public nint hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern nint CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(nint job, int infoClass, void* info, int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(nint job, int infoClass, void* info, int length, out int returned);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessInJob(nint process, nint job, out bool result);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(nint job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(nint process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(nint process, out uint exitCode);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, void* value, nint size, nint previous, nint returnSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? app, char* commandLine, nint procAttr, nint threadAttr, bool inherit, uint flags, char* env, string? cwd, ref STARTUPINFOEXW si, out PROCESS_INFORMATION pi);

    /// <summary>A new job that ends its processes when its last handle closes. Throws when Windows refuses.</summary>
    public static nint CreateKillOnClose()
    {
        nint job = CreateJobObjectW(0, null);
        if (job == 0) throw new InvalidOperationException("CreateJobObject failed: " + Marshal.GetLastWin32Error());
        if (SetLimits(job, KillOnJobClose) is var error and not 0)
        {
            CloseHandle(job);
            throw new InvalidOperationException("SetInformationJobObject failed: " + error);
        }
        return job;
    }

    /// <summary>
    /// Takes the kill-on-close limit off <paramref name="job"/>: its processes then run on after PoolWatch (the logon check's
    /// helper, which, orphaned, offers to protect the logon). 0, or the Win32 error.
    /// </summary>
    public static int LetOutlive(nint job) => SetLimits(job, 0);

    private static int SetLimits(nint job, uint flags)
    {
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { LimitFlags = flags };
        if (SetInformationJobObject(job, ExtendedLimitInformation, &info, sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION))) return 0;
        int error = Marshal.GetLastWin32Error();
        return error != 0 ? error : -1;
    }

    /// <summary>The job's limit flags as Windows holds them.</summary>
    public static uint LimitFlags(nint job)
    {
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION info;
        if (!QueryInformationJobObject(job, ExtendedLimitInformation, &info, sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION), out _))
            throw new InvalidOperationException("QueryInformationJobObject failed: " + Marshal.GetLastWin32Error());
        return info.LimitFlags;
    }

    /// <summary>The Windows calls of a start (<see cref="Win32"/>). A test passes a fake: no test starts a process.</summary>
    internal interface IStarter
    {
        /// <summary>
        /// CreateProcessW with <paramref name="application"/> as lpApplicationName, a private copy of
        /// <paramref name="commandLine"/>, no process or thread attributes, and a STARTUPINFOEXW whose attribute list holds
        /// <paramref name="jobs"/> as PROC_THREAD_ATTRIBUTE_JOB_LIST. False, with the Win32 error, when Windows refuses.
        /// </summary>
        bool Create(string application, string commandLine, bool inheritHandles, uint flags, string environment, string workingDirectory,
            nint[] jobs, out nint process, out nint thread, out int error);
        /// <summary>IsProcessInJob: true only when Windows says the process is in the job.</summary>
        bool InJob(nint process, nint job);
        /// <summary>ResumeThread: 0, or the Win32 error.</summary>
        int Resume(nint thread);
        /// <summary><see cref="JobNative.LetOutlive"/>: 0, or the Win32 error.</summary>
        int LetOutlive(nint job);
        void Terminate(nint process);
        /// <summary>TerminateJobObject: ends every process in <paramref name="job"/>, through the job's handle.</summary>
        void EndJob(nint job);
        void Close(nint handle);
    }

    /// <summary>
    /// Starts <paramref name="application"/> with <paramref name="commandLine"/> (its whole command line, argument 0
    /// included) in <paramref name="job"/> (made by <see cref="CreateKillOnClose"/>), with no window and exactly
    /// <paramref name="environment"/>: created in the job, suspended, checked to be in the job, then let run, and then,
    /// with <paramref name="outlive"/>, let outlive PoolWatch. Returns the process handle. On a failure before it runs,
    /// nothing ran: the process, if Windows made one, is ended while still suspended. If its job cannot be let outlive
    /// PoolWatch, it is ended. Either way it throws, and closing the job ends anything left in it.
    /// </summary>
    public static nint StartInJob(nint job, string application, string commandLine, string workingDirectory,
        IReadOnlyDictionary<string, string> environment, bool outlive, IStarter? starter = null)
    {
        starter ??= Win32.Instance;
        if (!starter.Create(application, commandLine, inheritHandles: false, StartFlags, EnvironmentBlock(environment), workingDirectory, [job],
                out nint process, out nint thread, out int error))
            throw new InvalidOperationException("CreateProcess failed: " + error);
        try
        {
            if (!starter.InJob(process, job)) throw new InvalidOperationException("the process is not in its job");
            if (starter.Resume(thread) is var resume and not 0) throw new InvalidOperationException("ResumeThread failed: " + resume);
            // Only once it runs: until then the kill-on-close limit ends it with PoolWatch, so a kill of PoolWatch during the
            // start never leaves a suspended process behind.
            if (outlive && starter.LetOutlive(job) is var limits and not 0) throw new InvalidOperationException("SetInformationJobObject failed: " + limits);
        }
        catch
        {
            starter.Terminate(process); // still suspended (it ran nothing), or just started in a job that still ends it with PoolWatch
            starter.Close(process);
            throw;
        }
        finally { starter.Close(thread); }
        return process;
    }

    /// <summary>
    /// A Unicode environment block for CreateProcess: each <c>name=value</c> ended by a NUL, sorted by name (ignoring case,
    /// as Windows keeps it), and one more NUL at the end. Throws on a name or value Windows could not hold.
    /// </summary>
    internal static string EnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        var block = new StringBuilder();
        foreach (var (name, value) in environment.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (name.Length == 0 || name.Contains('=') || name.Contains('\0') || value.Contains('\0'))
                throw new ArgumentException("not an environment variable: " + name, nameof(environment));
            block.Append(name).Append('=').Append(value).Append('\0');
        }
        return block.Append('\0').ToString();
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

    /// <summary>The real Windows calls. Each passes its arguments to Windows as they are.</summary>
    internal sealed class Win32 : IStarter
    {
        public static readonly Win32 Instance = new();

        public bool Create(string application, string commandLine, bool inheritHandles, uint flags, string environment, string workingDirectory,
            nint[] jobs, out nint process, out nint thread, out int error)
        {
            process = thread = 0;
            // The job handles, and the attribute list that points at them, stay valid until the list is deleted.
            nint* jobList = stackalloc nint[jobs.Length];
            for (int i = 0; i < jobs.Length; i++) jobList[i] = jobs[i];
            nint size = 0;
            InitializeProcThreadAttributeList(0, 1, 0, ref size); // fails, giving the size the list needs
            nint list = Marshal.AllocHGlobal(size);
            try
            {
                if (!InitializeProcThreadAttributeList(list, 1, 0, ref size)) { error = Marshal.GetLastWin32Error(); return false; }
                try
                {
                    if (!UpdateProcThreadAttribute(list, 0, ProcThreadAttributeJobList, jobList, jobs.Length * sizeof(nint), 0, 0))
                    {
                        error = Marshal.GetLastWin32Error();
                        return false;
                    }
                    var si = new STARTUPINFOEXW { StartupInfo = { cb = sizeof(STARTUPINFOEXW) }, lpAttributeList = list };
                    // CreateProcessW may write to the command line, so it gets a buffer of its own, never a .NET string's characters.
                    char[] line = (commandLine + "\0").ToCharArray();
                    fixed (char* p = line)
                    fixed (char* env = environment)
                    {
                        if (!CreateProcessW(application, p, 0, 0, inheritHandles, flags, env, workingDirectory, ref si, out var pi))
                        {
                            error = Marshal.GetLastWin32Error();
                            return false;
                        }
                        process = pi.hProcess;
                        thread = pi.hThread;
                        error = 0;
                        return true;
                    }
                }
                finally { DeleteProcThreadAttributeList(list); }
            }
            finally { Marshal.FreeHGlobal(list); }
        }

        public bool InJob(nint process, nint job) => IsProcessInJob(process, job, out bool result) && result;

        public int Resume(nint thread)
        {
            if (ResumeThread(thread) != uint.MaxValue) return 0;
            int error = Marshal.GetLastWin32Error();
            return error != 0 ? error : -1;
        }

        public int LetOutlive(nint job) => JobNative.LetOutlive(job);

        public void Terminate(nint process) => TerminateProcess(process, 1);

        public void EndJob(nint job) => JobNative.Terminate(job);

        public void Close(nint handle) => JobNative.Close(handle);
    }
}
