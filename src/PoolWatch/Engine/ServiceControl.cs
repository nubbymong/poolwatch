using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PoolWatch.Core.Services;

namespace PoolWatch.Engine;

/// <summary>
/// Reading and (gated) changing Windows services for the background-services page (addendum 4 B). Reading is unelevated;
/// changing a start type or stopping a service needs administrator rights, which PoolWatch has under its manifest.
/// Every change is checked against the protected gate here, after the pure <see cref="ServicePlan"/> gate, so neither a
/// crafted request nor a catalogue edit can touch a protected service.
/// </summary>
internal static class ServiceControl
{
    private const uint SC_MANAGER_CONNECT = 0x0001, SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    private const uint SERVICE_QUERY_CONFIG = 0x0001, SERVICE_QUERY_STATUS = 0x0004, SERVICE_CHANGE_CONFIG = 0x0002,
                       SERVICE_START = 0x0010, SERVICE_STOP = 0x0020;
    private const uint SERVICE_WIN32 = 0x30, SERVICE_STATE_ALL = 0x3;
    private const uint SERVICE_NO_CHANGE = 0xFFFFFFFF, SERVICE_CONTROL_STOP = 0x1;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool CloseServiceHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryServiceConfigW(IntPtr svc, IntPtr buf, int size, out int needed);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ChangeServiceConfigW(IntPtr svc, uint type, uint start, uint error, string? path, string? group, IntPtr tagId, string? deps, string? user, string? pwd, string? display);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(IntPtr svc, int level, IntPtr buf, int size, out int needed);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(IntPtr svc, uint control, IntPtr status);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool EnumServicesStatusExW(IntPtr scm, int level, uint type, uint state, byte[]? buf, int size, out int needed, out int count, ref int resume, string? group);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct QUERY_SERVICE_CONFIG { public uint ServiceType, StartType, ErrorControl; public IntPtr BinaryPathName, LoadOrderGroup; public uint TagId; public IntPtr Dependencies, ServiceStartName, DisplayName; }
    [StructLayout(LayoutKind.Sequential)] private struct SERVICE_STATUS_PROCESS { public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags; }

    public static bool IsElevated => CacheHelper.IsElevated;

    /// <summary>Every installed Win32 service with its start type, state, binary path and signer (unelevated).</summary>
    public static List<ServiceView> Enumerate()
    {
        var views = new List<ServiceView>();
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT | SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == IntPtr.Zero) return views;
        try
        {
            int resume = 0;
            EnumServicesStatusExW(scm, 0, SERVICE_WIN32, SERVICE_STATE_ALL, null, 0, out int needed, out _, ref resume, null);
            var buf = new byte[Math.Max(needed, 256 * 1024)];
            resume = 0;
            if (!EnumServicesStatusExW(scm, 0, SERVICE_WIN32, SERVICE_STATE_ALL, buf, buf.Length, out needed, out int count, ref resume, null))
                return views;
            // ENUM_SERVICE_STATUS_PROCESSW is 3 pointers + status; walk it for the names, then query each.
            int stride = IntPtr.Size == 8 ? 0x38 : 0x24;
            for (int i = 0; i < count; i++)
            {
                int off = i * stride;
                string name = PtrStr(buf, off) ?? "";
                string display = PtrStr(buf, off + IntPtr.Size) ?? name;
                if (name.Length == 0) continue;
                views.Add(Read(scm, name, display));
            }
        }
        catch { }
        finally { CloseServiceHandle(scm); }
        return views;
    }

    private static unsafe string? PtrStr(byte[] buf, int off)
    {
        fixed (byte* p = buf) { var ptr = *(IntPtr*)(p + off); return ptr == IntPtr.Zero ? null : Marshal.PtrToStringUni(ptr); }
    }

    private static ServiceView Read(IntPtr scm, string name, string display)
    {
        var svc = OpenServiceW(scm, name, SERVICE_QUERY_CONFIG | SERVICE_QUERY_STATUS);
        if (svc == IntPtr.Zero) return new ServiceView { Name = name, DisplayName = display };
        try
        {
            var start = ServiceStartType.Unknown;
            string? path = null;
            QueryServiceConfigW(svc, IntPtr.Zero, 0, out int need);
            if (need > 0)
            {
                var cfgBuf = Marshal.AllocHGlobal(need);
                try
                {
                    if (QueryServiceConfigW(svc, cfgBuf, need, out _))
                    {
                        var cfg = Marshal.PtrToStructure<QUERY_SERVICE_CONFIG>(cfgBuf);
                        start = (ServiceStartType)(int)cfg.StartType;
                        path = cfg.BinaryPathName == IntPtr.Zero ? null : Marshal.PtrToStringUni(cfg.BinaryPathName);
                    }
                }
                finally { Marshal.FreeHGlobal(cfgBuf); }
            }
            bool running = false;
            int pid = 0;
            var stBuf = Marshal.AllocHGlobal(Marshal.SizeOf<SERVICE_STATUS_PROCESS>());
            try
            {
                if (QueryServiceStatusEx(svc, 0, stBuf, Marshal.SizeOf<SERVICE_STATUS_PROCESS>(), out _))
                {
                    var st = Marshal.PtrToStructure<SERVICE_STATUS_PROCESS>(stBuf);
                    running = st.CurrentState == 4;
                    pid = running ? (int)st.ProcessId : 0;
                }
            }
            finally { Marshal.FreeHGlobal(stBuf); }
            string? exe = ImageOf(path);
            var (signer, isMs) = Signer(exe);
            return new ServiceView
            {
                Name = name, DisplayName = display, StartType = start, Running = running, BinaryPath = path,
                Publisher = signer, MicrosoftSigned = isMs, ProcessId = pid,
            };
        }
        finally { CloseServiceHandle(svc); }
    }

    /// <summary>The exe inside a service's binary path (quotes and svchost "-k group" trimmed to the host).</summary>
    public static string? ImageOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = path.Trim();
        if (path.StartsWith('"')) { int end = path.IndexOf('"', 1); return end > 0 ? path[1..end] : path[1..]; }
        int sp = path.IndexOf(" -", StringComparison.Ordinal);
        if (sp < 0) sp = path.IndexOf(" /", StringComparison.Ordinal);
        return sp > 0 ? path[..sp] : path;
    }

    private static readonly Dictionary<string, (string? signer, bool ms)> SignerCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The signer of a service binary and whether it is Microsoft. C6 (review): WinVerifyTrust on the embedded signature,
    /// else on the system catalog that lists the file (<see cref="Analysis.FileSignature.Verify"/>), so a catalog-signed
    /// Microsoft binary outside %WINDIR% is Microsoft (Protected unless the catalogue lists it), not "Unknown". The signer
    /// name comes from the verified trust state, not the obsolete X509Certificate.CreateFromSignedFile (SYSLIB0057).
    /// </summary>
    public static (string? signer, bool microsoft) Signer(string? exe)
    {
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return (null, false);
        lock (SignerCache)
        {
            if (SignerCache.TryGetValue(exe, out var cached)) return cached;
            bool inWindows = IsUnder(exe, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            (string? signer, bool ms) result;
            try
            {
                var sig = Analysis.FileSignature.Verify(exe);
                result = (sig.Signer, ServiceCatalogue.MicrosoftSigner(sig.Signer, inWindows, sig.Organization));
            }
            catch { result = (null, inWindows); } // could not be checked: Windows' own binaries live under %SystemRoot%
            SignerCache[exe] = result;
            return result;
        }
    }

    private static bool IsUnder(string path, string dir) =>
        dir.Length > 0 && path.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- the gated change (elevated)

    /// <summary>
    /// Sets a service's start type and, when turning it off, stops it. Gated: the service must be exactly one of
    /// <paramref name="allowed"/> (the group 1/2 options the page built) and never protected. Returns null on success.
    /// </summary>
    public static string? Apply(ServicePlan.Operation op, IReadOnlySet<string> allowed, bool insiderEnrolled)
    {
        if (Guard(op.Option.Service.Name, allowed, insiderEnrolled) is { } bad) return bad;
        return SetStart(op.Option.Service.Name, op.NewStart, stopIfRunning: op.NewStart == ServiceStartType.Disabled);
    }

    /// <summary>
    /// Restores a service to its saved original start type and, when it was running before PoolWatch changed it, starts it
    /// again (Part 3 review: Restore left a stopped service stopped). The same live gate as Apply; the record was already
    /// re-validated by <see cref="ServicePlan.GateRestore"/>.
    /// </summary>
    /// <returns>
    /// <c>error</c>: the start type was not put back (nothing changed). <c>startError</c>: the start type is back, but starting
    /// the service again failed (the restore itself happened: the record can go).
    /// </returns>
    public static (string? error, string? startError) Restore(ServiceChange change, IReadOnlySet<string> allowed, bool insiderEnrolled)
    {
        if (Guard(change.Service, allowed, insiderEnrolled) is { } bad) return (bad, null);
        // Putting Disabled back (undoing "Restore to Windows default") also stops it if it has started since.
        if (SetStart(change.Service, change.OriginalStart, stopIfRunning: change.OriginalStart == ServiceStartType.Disabled) is { } err) return (err, null);
        if (!ServicePlan.StartAfterRestore(change)) return (null, null);
        return (null, Start(change.Service));
    }

    /// <summary>
    /// "Restore to Windows default": the same live gate, then the start type only (the service starts at the next boot or
    /// when something asks for it). The operation came from <see cref="ServicePlan.BuildDefault"/>.
    /// </summary>
    public static string? SetDefault(ServicePlan.Operation op, IReadOnlySet<string> allowed, bool insiderEnrolled)
    {
        if (Guard(op.Option.Service.Name, allowed, insiderEnrolled) is { } bad) return bad;
        if (op.NewStart is not (ServiceStartType.Automatic or ServiceStartType.Manual)) return $"'{op.Option.Service.Name}': refused start type {op.NewStart}";
        return SetStart(op.Option.Service.Name, op.NewStart, stopIfRunning: false);
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool StartServiceW(IntPtr svc, int argc, IntPtr argv);

    /// <summary>Starts a service (no arguments). Only called by <see cref="Restore"/>, after the gate.</summary>
    private static string? Start(string name)
    {
        if (!IsElevated) return "starting a service needs administrator rights";
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) return "could not open the service control manager: " + Marshal.GetLastWin32Error();
        try
        {
            var svc = OpenServiceW(scm, name, SERVICE_START);
            if (svc == IntPtr.Zero) return $"could not open '{name}': {Marshal.GetLastWin32Error()}";
            try
            {
                if (StartServiceW(svc, 0, IntPtr.Zero)) return null;
                int e = Marshal.GetLastWin32Error();
                return e == 1056 /* ERROR_SERVICE_ALREADY_RUNNING */ ? null : $"error {e}";
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    /// <summary>The live gate: refuse anything protected, not Win32, or not in the page's allowed set.</summary>
    private static string? Guard(string name, IReadOnlySet<string> allowed, bool insiderEnrolled)
    {
        if (ServiceCatalogue.IsProtected(name, insiderEnrolled)) return $"'{name}' is a protected service";
        if (!allowed.Contains(name)) return $"'{name}' is not one of the services offered on this PC";
        return null;
    }

    private static string? SetStart(string name, ServiceStartType start, bool stopIfRunning)
    {
        if (!ServicePlan.IsValidStartType(start)) return $"'{name}': refused start type {start}";
        if (!IsElevated) return "changing a service needs administrator rights";
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) return "could not open the service control manager: " + Marshal.GetLastWin32Error();
        try
        {
            var svc = OpenServiceW(scm, name, SERVICE_CHANGE_CONFIG | SERVICE_STOP | SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero) return $"could not open '{name}': {Marshal.GetLastWin32Error()}";
            try
            {
                if (stopIfRunning)
                {
                    var stBuf = Marshal.AllocHGlobal(Marshal.SizeOf<SERVICE_STATUS_PROCESS>());
                    try
                    {
                        if (QueryServiceStatusEx(svc, 0, stBuf, Marshal.SizeOf<SERVICE_STATUS_PROCESS>(), out _) && Marshal.PtrToStructure<SERVICE_STATUS_PROCESS>(stBuf).CurrentState == 4)
                            ControlService(svc, SERVICE_CONTROL_STOP, stBuf); // best effort; the start-type change is what sticks
                    }
                    finally { Marshal.FreeHGlobal(stBuf); }
                }
                if (!ChangeServiceConfigW(svc, SERVICE_NO_CHANGE, (uint)(int)start, SERVICE_NO_CHANGE, null, null, IntPtr.Zero, null, null, null, null))
                    return $"could not change '{name}': {Marshal.GetLastWin32Error()}";
                return null;
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }
}
