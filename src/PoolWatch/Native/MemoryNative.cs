using System.Runtime.InteropServices;
using PoolWatch.Core.Cache;

namespace PoolWatch.Native;

/// <summary>
/// The memory lists (free, zeroed, standby by priority, modified) and the standby purge (addendum 2). Reading uses
/// NtQuerySystemInformation(SystemMemoryListInformation), falling back to the in-box "Memory" performance counters
/// (PDH, no WMI) when that class is refused unelevated. Both the query and the purge (NtSetSystemInformation) need
/// SeProfileSingleProcessPrivilege, which PoolWatch (always elevated) enables in its own token; unelevated the purge is
/// refused (S5, review round 2: there is no SYSTEM helper any more).
/// </summary>
internal static unsafe class MemoryNative
{
    private const int SystemMemoryListInformation = 80;
    private const int MemoryPurgeStandbyList = 4, MemoryPurgeLowPriorityStandbyList = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_MEMORY_LIST_INFORMATION
    {
        public nuint ZeroPageCount, FreePageCount, ModifiedPageCount, ModifiedNoWritePageCount, BadPageCount;
        public fixed ulong PageCountByPriority[8];
        public fixed ulong RepurposedPagesByPriority[8];
        public nuint ModifiedPageCountPageFile;
    }

    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int infoClass, void* buffer, int length, out int returnLength);
    [DllImport("ntdll.dll")] private static extern int NtSetSystemInformation(int infoClass, void* buffer, int length);

    private static bool _ntRefused, _privilegeTried;
    private static readonly object Lock = new();

    public static MemoryLists Query()
    {
        lock (Lock) return QueryLocked();
    }

    private static MemoryLists QueryLocked()
    {
        if (!_privilegeTried) { _privilegeTried = true; EnablePrivilege("SeProfileSingleProcessPrivilege"); } // fails harmlessly unelevated
        if (!_ntRefused)
        {
            SYSTEM_MEMORY_LIST_INFORMATION info;
            int st = NtQuerySystemInformation(SystemMemoryListInformation, &info, sizeof(SYSTEM_MEMORY_LIST_INFORMATION), out _);
            if (st == 0)
            {
                long page = Environment.SystemPageSize;
                long standby = 0, low = 0;
                for (int i = 0; i < 8; i++)
                {
                    standby += (long)info.PageCountByPriority[i];
                    if (i == 0) low += (long)info.PageCountByPriority[i]; // priority 0: what the low-priority purge drops
                }
                return new MemoryLists((long)info.FreePageCount * page, (long)info.ZeroPageCount * page, standby * page,
                    (long)(info.ModifiedPageCount + info.ModifiedNoWritePageCount) * page, low * page);
            }
            _ntRefused = true; // STATUS_ACCESS_DENIED / PRIVILEGE_NOT_HELD unelevated: use the counters from now on
        }
        return Pdh.Query();
    }

    /// <summary>The purge itself, in an elevated PoolWatch (the SYSTEM helper carries its own copy). Returns the NTSTATUS (0 = success).</summary>
    public static int Purge(ClearMode mode)
    {
        if (!EnablePrivilege("SeProfileSingleProcessPrivilege")) return unchecked((int)0xC0000061); // STATUS_PRIVILEGE_NOT_HELD
        int command = mode == ClearMode.LowPriority ? MemoryPurgeLowPriorityStandbyList : MemoryPurgeStandbyList;
        return NtSetSystemInformation(SystemMemoryListInformation, &command, sizeof(int));
    }

    // --- privilege ---
    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct TOKEN_PRIVILEGES { public int Count; public LUID Luid; public int Attributes; }
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool LookupPrivilegeValue(string? system, string name, out LUID luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, int len, IntPtr prev, IntPtr retLen);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    private static bool EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x20 | 0x8, out var token)) return false; // ADJUST_PRIVILEGES | QUERY
        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) return false;
            var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = 2 }; // SE_PRIVILEGE_ENABLED
            return AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero) && Marshal.GetLastWin32Error() == 0;
        }
        finally { CloseHandle(token); }
    }

    /// <summary>The "Memory" counters, English names (locale-independent): read unelevated.</summary>
    private static class Pdh
    {
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
        [DllImport("pdh.dll")] private static extern int PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll")] private static extern int PdhGetFormattedCounterValue(IntPtr counter, int format, IntPtr type, out PDH_FMT_COUNTERVALUE value);
        [StructLayout(LayoutKind.Sequential)] private struct PDH_FMT_COUNTERVALUE { public int CStatus; public int Pad; public long LargeValue; }
        private const int PDH_FMT_LARGE = 0x400;

        private static IntPtr _query;
        private static IntPtr[]? _counters;
        private static readonly string[] Paths =
        [
            @"\Memory\Free & Zero Page List Bytes", @"\Memory\Standby Cache Core Bytes", @"\Memory\Standby Cache Normal Priority Bytes",
            @"\Memory\Standby Cache Reserve Bytes", @"\Memory\Modified Page List Bytes",
        ];

        public static MemoryLists Query()
        {
            try
            {
                if (_counters is null)
                {
                    if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) return default;
                    _counters = new IntPtr[Paths.Length];
                    for (int i = 0; i < Paths.Length; i++) PdhAddEnglishCounter(_query, Paths[i], IntPtr.Zero, out _counters[i]);
                }
                if (PdhCollectQueryData(_query) != 0) return default;
                long V(int i) => _counters![i] != IntPtr.Zero && PdhGetFormattedCounterValue(_counters[i], PDH_FMT_LARGE, IntPtr.Zero, out var v) == 0 ? v.LargeValue : 0;
                long reserve = V(3);
                // Reserve (priorities 0-1) is the closest the counters come to the priority-0 pages the low-priority purge drops.
                return new MemoryLists(V(0), 0, V(1) + V(2) + reserve, V(4), reserve);
            }
            catch { return default; }
        }
    }
}
