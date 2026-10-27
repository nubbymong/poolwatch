using PoolWatch.Core.Model;
using PoolWatch.Native;

namespace PoolWatch.Collectors;

/// <summary>Running Win32 services and their host PIDs (EnumServicesStatusEx, SC_ENUM_PROCESS_INFO).</summary>
internal static class ServiceCollector
{
    private const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    private const uint SERVICE_WIN32 = 0x30;
    private const uint SERVICE_ACTIVE = 0x1;

    /// <summary>The running services. Throws when the service control manager cannot list them (the caller logs it).</summary>
    public static List<ServiceEntry> Query()
    {
        var scm = NativeMethods.OpenSCManagerW(null, null, SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == IntPtr.Zero) return [];
        try
        {
            return ServiceEnumeration.Query(scm, SERVICE_WIN32, SERVICE_ACTIVE).Select(e => new ServiceEntry(e.Name, e.DisplayName, e.ProcessId)).ToList();
        }
        finally { NativeMethods.CloseServiceHandle(scm); }
    }
}
