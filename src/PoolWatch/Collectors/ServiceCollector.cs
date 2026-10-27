using System.Runtime.InteropServices;
using PoolWatch.Core.Model;
using PoolWatch.Native;

namespace PoolWatch.Collectors;

/// <summary>Running Win32 services and their host PIDs (EnumServicesStatusEx, SC_ENUM_PROCESS_INFO).</summary>
internal static unsafe class ServiceCollector
{
    private const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    private const uint SERVICE_WIN32 = 0x30;
    private const uint SERVICE_ACTIVE = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct ENUM_SERVICE_STATUS_PROCESS
    {
        public IntPtr ServiceName;
        public IntPtr DisplayName;
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }

    public static List<ServiceEntry> Query()
    {
        var list = new List<ServiceEntry>();
        var scm = NativeMethods.OpenSCManagerW(null, null, SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == IntPtr.Zero) return list;
        try
        {
            int resume = 0;
            int size = 256 * 1024;
            while (true)
            {
                var buf = (byte*)NativeMemory.Alloc((nuint)size);
                try
                {
                    bool ok = NativeMethods.EnumServicesStatusExW(scm, 0, SERVICE_WIN32, SERVICE_ACTIVE, buf, size, out int needed, out int count, ref resume, null);
                    int err = ok ? 0 : Marshal.GetLastWin32Error();
                    if (!ok && err != 234 /* ERROR_MORE_DATA */) return list;
                    var entries = (ENUM_SERVICE_STATUS_PROCESS*)buf;
                    for (int i = 0; i < count; i++)
                    {
                        var e = entries[i];
                        list.Add(new ServiceEntry(Marshal.PtrToStringUni(e.ServiceName) ?? "", Marshal.PtrToStringUni(e.DisplayName) ?? "", (int)e.ProcessId));
                    }
                    if (ok) return list;
                    if (count == 0) size = Math.Max(size * 2, needed);
                }
                finally { NativeMemory.Free(buf); }
            }
        }
        finally { NativeMethods.CloseServiceHandle(scm); }
    }
}
