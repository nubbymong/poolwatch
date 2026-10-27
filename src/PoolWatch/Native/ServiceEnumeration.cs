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
    private const int SC_ENUM_PROCESS_INFO = 0;
    internal const int ERROR_MORE_DATA = 234;
    internal const int InitialSize = 64 * 1024, MaxSize = 16 * 1024 * 1024, MaxCalls = 256;

    /// <summary>One EnumServicesStatusExW call into <paramref name="buffer"/>; <paramref name="error"/> is its last error when it returns false.</summary>
    internal delegate bool EnumCall(IntPtr buffer, int size, out int needed, out int count, ref int resume, out int error);

    /// <summary>The services of <paramref name="serviceType"/> in <paramref name="serviceState"/> (an SCM handle with SC_MANAGER_ENUMERATE_SERVICE).</summary>
    /// <exception cref="System.ComponentModel.Win32Exception">The enumeration failed (other than needing more room).</exception>
    public static List<ServiceStatusParser.Entry> Query(IntPtr scm, uint serviceType, uint serviceState) =>
        Query((IntPtr buffer, int size, out int needed, out int count, ref int resume, out int error) =>
        {
            bool ok = NativeMethods.EnumServicesStatusExW(scm, SC_ENUM_PROCESS_INFO, serviceType, serviceState, (byte*)buffer, size,
                out needed, out count, ref resume, null);
            error = ok ? 0 : Marshal.GetLastWin32Error();
            return ok;
        });

    /// <summary>
    /// The enumeration loop over <paramref name="call"/> (the real API, or a fake in a test). Each call gets a fresh
    /// unmanaged buffer; the entries it returns are parsed and copied before the buffer is freed. On ERROR_MORE_DATA the
    /// next call continues from the resume handle. When not even one entry fitted, the buffer grows towards what the API
    /// says is still needed, up to <paramref name="maxSize"/>; if nothing fits at that size, or the list is not complete
    /// after <paramref name="maxCalls"/> calls, it gives up with an exception rather than loop for ever.
    /// </summary>
    internal static List<ServiceStatusParser.Entry> Query(EnumCall call, int initialSize = InitialSize, int maxSize = MaxSize, int maxCalls = MaxCalls)
    {
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("the service list is parsed with the x64 layout");
        var all = new List<ServiceStatusParser.Entry>();
        int resume = 0, size = Math.Min(initialSize, maxSize);
        for (int calls = 0; calls < maxCalls; calls++)
        {
            var buf = (byte*)NativeMemory.Alloc((nuint)size);
            try
            {
                bool ok = call((IntPtr)buf, size, out int needed, out int count, ref resume, out int error);
                if (!ok && error != ERROR_MORE_DATA) throw new System.ComponentModel.Win32Exception(error);
                all.AddRange(ServiceStatusParser.Parse(new ReadOnlySpan<byte>(buf, size), (ulong)buf, count));
                if (ok) return all;
                if (count == 0)
                {
                    // Not even one entry fitted: grow (towards what is still needed), unless this was already the largest size.
                    if (size >= maxSize) throw new InvalidOperationException($"a service entry does not fit in {maxSize} bytes");
                    size = (int)Math.Min(maxSize, Math.Max((long)needed, (long)size * 2));
                }
                else if (needed > size) size = (int)Math.Min(maxSize, Math.Max((long)needed, (long)size * 2));
            }
            finally { NativeMemory.Free(buf); }
        }
        throw new InvalidOperationException($"the service list was not complete after {maxCalls} calls");
    }
}
