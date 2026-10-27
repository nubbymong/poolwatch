using System.Runtime.InteropServices;
using PoolWatch.Native;

namespace PoolWatch.Collectors;

/// <summary>
/// Reads a process's current directory and selected environment variables from its PEB (read-only:
/// NtQueryInformationProcess + ReadProcessMemory with PROCESS_VM_READ). x64 processes only.
/// </summary>
internal static unsafe class PebReader
{
    private const int ProcessBasicInformation = 0;
    // x64 offsets: PEB.ProcessParameters; RTL_USER_PROCESS_PARAMETERS.CurrentDirectory.DosPath, Environment, EnvironmentSize.
    private const int PebProcessParameters = 0x20, ParamsCurrentDirectory = 0x38, ParamsEnvironment = 0x80, ParamsEnvironmentSize = 0x3F0;
    private const int MaxEnvironmentBytes = 256 * 1024;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    /// <summary>Returns (cwd, wanted environment variables) or null if the process cannot be read.</summary>
    public static (string? cwd, Dictionary<string, string> env)? Read(int pid, long createTime, IReadOnlyCollection<string> wanted)
    {
        using var h = SafeProcess.Open(pid, NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION | NativeMethods.PROCESS_VM_READ);
        if (h is null) return null;
        if (createTime != 0 && NativeMethods.GetProcessTimes(h.Handle, out long created, out _, out _, out _) && created != createTime) return null; // PID reused
        if (NativeMethods.IsWow64Process(h.Handle, out bool wow64) && wow64) return null;
        PROCESS_BASIC_INFORMATION pbi;
        if (NativeMethods.NtQueryInformationProcess(h.Handle, ProcessBasicInformation, &pbi, sizeof(PROCESS_BASIC_INFORMATION), out _) != NtStatus.Success) return null;
        if (!ReadPtr(h.Handle, pbi.PebBaseAddress + PebProcessParameters, out var parameters) || parameters == IntPtr.Zero) return null;

        string? cwd = null;
        NativeMethods.UNICODE_STRING us;
        if (Read(h.Handle, parameters + ParamsCurrentDirectory, &us, sizeof(NativeMethods.UNICODE_STRING)) && us.Length > 0 && us.Length < 4096)
            cwd = ReadString(h.Handle, us.Buffer, us.Length);

        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count > 0 && ReadPtr(h.Handle, parameters + ParamsEnvironment, out var block) && block != IntPtr.Zero &&
            ReadPtr(h.Handle, parameters + ParamsEnvironmentSize, out var sizePtr))
        {
            long size = Math.Min((long)sizePtr, MaxEnvironmentBytes);
            if (size > 0 && ReadString(h.Handle, block, (int)size) is { } text)
            {
                foreach (var (k, v) in Core.Aicc.EnvironmentBlock.Parse(text, wanted)) env[k] = v;
            }
        }
        return (cwd, env);
    }

    private static bool Read(IntPtr process, IntPtr address, void* buffer, int size) =>
        NativeMethods.ReadProcessMemory(process, address, buffer, size, out nint read) && read == size;

    private static bool ReadPtr(IntPtr process, IntPtr address, out IntPtr value)
    {
        IntPtr v;
        bool ok = Read(process, address, &v, sizeof(IntPtr));
        value = ok ? v : IntPtr.Zero;
        return ok;
    }

    private static string? ReadString(IntPtr process, IntPtr address, int bytes)
    {
        bytes &= ~1;
        var buf = new char[bytes / 2];
        fixed (char* p = buf)
            return Read(process, address, p, bytes) ? new string(p, 0, buf.Length) : null;
    }
}
