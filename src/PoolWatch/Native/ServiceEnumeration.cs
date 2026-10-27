using System.Runtime.InteropServices;
using PoolWatch.Core.Parsing;

namespace PoolWatch.Native;

/// <summary>
/// EnumServicesStatusExW(SC_ENUM_PROCESS_INFO), read safely. The API writes the service and display names into the
/// caller's buffer and stores their absolute addresses next to each entry, so the buffer must not move between the call
/// and the read: it is unmanaged memory here (a managed array can be moved by any compacting collection, including the
/// large-object-heap compaction PoolWatch's own memory trim asks for). Each name is read through
/// <see cref="ServiceStatusParser"/>, which only accepts addresses inside the buffer, and is copied to a managed string
/// before the buffer is freed. ERROR_MORE_DATA is followed with the resume handle until the list is complete.
/// </summary>
internal static unsafe class ServiceEnumeration
{
    private const int SC_ENUM_PROCESS_INFO = 0, ERROR_MORE_DATA = 234;
    private const int InitialSize = 64 * 1024, MaxSize = 16 * 1024 * 1024, MaxCalls = 256;

    /// <summary>The services of <paramref name="serviceType"/> in <paramref name="serviceState"/> (an SCM handle with SC_MANAGER_ENUMERATE_SERVICE).</summary>
    /// <exception cref="System.ComponentModel.Win32Exception">The enumeration failed (other than needing more room).</exception>
    public static List<ServiceStatusParser.Entry> Query(IntPtr scm, uint serviceType, uint serviceState)
    {
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("the service list is parsed with the x64 layout");
        var all = new List<ServiceStatusParser.Entry>();
        int resume = 0, size = InitialSize;
        for (int call = 0; call < MaxCalls; call++)
        {
            var buf = (byte*)NativeMemory.Alloc((nuint)size);
            try
            {
                bool ok = NativeMethods.EnumServicesStatusExW(scm, SC_ENUM_PROCESS_INFO, serviceType, serviceState, buf, size,
                    out int needed, out int count, ref resume, null);
                int error = ok ? 0 : Marshal.GetLastWin32Error();
                if (!ok && error != ERROR_MORE_DATA) throw new System.ComponentModel.Win32Exception(error);
                all.AddRange(ServiceStatusParser.Parse(new ReadOnlySpan<byte>(buf, size), (ulong)buf, count));
                if (ok) return all;
                // More to come from where the resume handle points; make room for what is left if it is bigger.
                if (needed > size) size = Math.Min(MaxSize, Math.Max(needed, size * 2));
                else if (count == 0) size = Math.Min(MaxSize, size * 2);
                if (count == 0 && size == MaxSize && needed > MaxSize) throw new InvalidOperationException($"the service list needs {needed} bytes");
            }
            finally { NativeMemory.Free(buf); }
        }
        throw new InvalidOperationException($"the service list was not complete after {MaxCalls} calls");
    }
}
