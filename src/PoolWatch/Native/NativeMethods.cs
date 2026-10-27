using System.Runtime.InteropServices;
using System.Text;

namespace PoolWatch.Native;

internal static class NtStatus
{
    public const int Success = 0;
    public const int InfoLengthMismatch = unchecked((int)0xC0000004);
    public const int BufferTooSmall = unchecked((int)0xC0000023);
    public const int BufferOverflow = unchecked((int)0x80000005);
    public static bool IsSizeError(int s) => s is InfoLengthMismatch or BufferTooSmall or BufferOverflow;
}

internal static class SystemInfoClass
{
    public const int SystemProcessInformation = 5;
    public const int SystemPoolTagInformation = 22;
    public const int SystemExtendedHandleInformation = 64;
}

internal static unsafe class NativeMethods
{
    public const int ObjectTypesInformation = 3;
    public const int ProcessBasicInformation = 0;
    public const int ProcessCommandLineInformation = 60;

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint PROCESS_QUERY_INFORMATION = 0x0400;
    public const uint PROCESS_VM_READ = 0x0010;
    public const uint TOKEN_QUERY = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS_EX
    {
        public int cb, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage,
            PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }

    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")]
    private static extern bool GetProcessMemoryInfo(IntPtr process, out PROCESS_MEMORY_COUNTERS_EX counters, int size);

    /// <summary>This process's working set and private bytes, cheaply (no system-wide process snapshot).</summary>
    public static bool GetOwnMemory(out long workingSet, out long privateBytes)
    {
        bool ok = GetProcessMemoryInfo(GetCurrentProcess(), out var c, Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX>());
        workingSet = ok ? (long)c.WorkingSetSize : 0;
        privateBytes = ok ? (long)c.PrivateUsage : 0;
        return ok;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

    /// <summary>Kernel + user CPU time of the calling thread, in 100 ns ticks (for the profiler).</summary>
    public static long CurrentThreadCpuTicks() => GetThreadTimes(GetCurrentThread(), out _, out _, out long k, out long u) ? k + u : 0;

    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(int infoClass, void* buffer, int length, out int returnLength);

    [DllImport("ntdll.dll")]
    public static extern int NtQueryObject(IntPtr handle, int infoClass, void* buffer, int length, out int returnLength);

    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationProcess(IntPtr process, int infoClass, void* buffer, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, char* buffer, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessIoCounters(IntPtr process, out IO_COUNTERS counters);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetExitCodeProcess(IntPtr process, out int exitCode);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetLogicalDriveStringsW(int length, char* buffer);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool GetVolumeInformationW(string root, char* volumeName, int volumeNameSize, out uint serial,
        out uint maxComponentLength, out uint fsFlags, char* fsName, int fsNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern uint GetDriveTypeW(string root);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int QueryDosDeviceW(string deviceName, char* target, int max);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetFinalPathNameByHandleW(IntPtr file, char* path, int length, int flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(IntPtr device, uint code, void* inBuf, int inSize, void* outBuf, int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadProcessMemory(IntPtr process, IntPtr address, void* buffer, nint size, out nint read);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool IsWow64Process(IntPtr process, out bool wow64);

    [DllImport("psapi.dll", SetLastError = true)]
    public static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION info, int size);

    [DllImport("psapi.dll", SetLastError = true)]
    public static extern bool EnumDeviceDrivers(IntPtr* imageBases, int cb, out int needed);

    [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetDeviceDriverFileNameW(IntPtr imageBase, char* name, int size);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool EnumServicesStatusExW(IntPtr scm, int infoLevel, uint serviceType, uint serviceState,
        byte* services, int bufSize, out int bytesNeeded, out int servicesReturned, ref int resumeHandle, string? groupName);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool GetTokenInformation(IntPtr token, int infoClass, void* buffer, int length, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool LookupAccountSidW(string? system, IntPtr sid, char* name, ref int nameLen, char* domain, ref int domainLen, out int use);

    [DllImport("kernel32.dll")]
    public static extern bool AttachConsole(int pid);

    public static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PERFORMANCE_INFORMATION
    {
        public int cb;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache;
        public nuint KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct UNICODE_STRING
    {
        public ushort Length, MaximumLength;
        public IntPtr Buffer;
    }

    public static string? ReadUnicodeString(UNICODE_STRING* s)
    {
        if (s->Buffer == IntPtr.Zero || s->Length == 0) return null;
        return new string((char*)s->Buffer, 0, s->Length / 2);
    }
}

/// <summary>A reusable native buffer that grows on STATUS_INFO_LENGTH_MISMATCH.</summary>
internal sealed unsafe class NativeBuffer : IDisposable
{
    public void* Pointer { get; private set; }
    public int Size { get; private set; }

    public NativeBuffer(int initial) => Resize(initial);

    public void Resize(int size)
    {
        if (Pointer != null) NativeMemory.Free(Pointer);
        Pointer = NativeMemory.Alloc((nuint)size);
        Size = size;
    }

    public ReadOnlySpan<byte> Span(int length) => new(Pointer, Math.Min(length, Size));
    public ulong Address => (ulong)Pointer;

    /// <summary>Runs an NtQuery*-style call, growing the buffer until it fits. Returns the used length.</summary>
    public int Query(Func<IntPtr, int, (int status, int returned)> call, int maxSize = 512 * 1024 * 1024)
    {
        for (int attempt = 0; attempt < 16; attempt++)
        {
            var (status, returned) = call((IntPtr)Pointer, Size);
            if (status == NtStatus.Success) return returned > 0 ? returned : Size;
            if (!NtStatus.IsSizeError(status)) throw new InvalidOperationException($"NT call failed: 0x{status:X8}");
            int next = Math.Max(returned + 64 * 1024, Size * 2);
            if (next > maxSize) throw new InvalidOperationException($"NT call needs more than {maxSize} bytes");
            Resize(next);
        }
        throw new InvalidOperationException("NT call kept reporting a size mismatch");
    }

    public void Dispose()
    {
        if (Pointer != null) NativeMemory.Free(Pointer);
        Pointer = null;
        Size = 0;
    }
}

internal sealed class SafeProcess : IDisposable
{
    public IntPtr Handle { get; }
    private SafeProcess(IntPtr h) => Handle = h;

    public static SafeProcess? Open(int pid, uint access)
    {
        var h = NativeMethods.OpenProcess(access, false, pid);
        return h == IntPtr.Zero ? null : new SafeProcess(h);
    }

    public void Dispose() => NativeMethods.CloseHandle(Handle);
}
