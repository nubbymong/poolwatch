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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct QUERY_SERVICE_CONFIG { public uint ServiceType, StartType, ErrorControl; public IntPtr BinaryPathName, LoadOrderGroup; public uint TagId; public IntPtr Dependencies, ServiceStartName, DisplayName; }
    [StructLayout(LayoutKind.Sequential)] private struct SERVICE_STATUS_PROCESS { public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags; }

    public static bool IsElevated => CacheHelper.IsElevated;

    /// <summary>Every installed Win32 service with its start type, state, binary path and signer (unelevated).</summary>
    /// <remarks>
    /// The names come from <see cref="Native.ServiceEnumeration"/>, which reads them from unmanaged memory and copies them
    /// before this loop runs. (This used to read them from a managed array through the addresses the API stored in it:
    /// a compacting collection during the slow per-service queries below moved the array and the reads crashed.)
    /// </remarks>
    public static List<ServiceView> Enumerate()
    {
        var views = new List<ServiceView>();
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT | SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == IntPtr.Zero) return views;
        try
        {
            foreach (var e in Native.ServiceEnumeration.Query(scm, SERVICE_WIN32, SERVICE_STATE_ALL))
                views.Add(Read(scm, e.Name, e.DisplayName));
        }
        catch { }
        finally { CloseServiceHandle(scm); }
        return views;
    }

    private static ServiceView Read(IntPtr scm, string name, string display)
    {
        var svc = OpenServiceW(scm, name, SERVICE_QUERY_CONFIG | SERVICE_QUERY_STATUS);
        if (svc == IntPtr.Zero) return new ServiceView { Name = name, DisplayName = display };
        try
        {
            var start = ServiceStartType.Unknown;
            string? path = null;
            IReadOnlyList<string> dependsOn = [];
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
                        dependsOn = Dependencies(cfg.Dependencies, cfgBuf + need);
                    }
                }
                finally { Marshal.FreeHGlobal(cfgBuf); }
            }
            var (running, between) = RunState(0); // until it is read: Windows could not say
            int pid = 0;
            var stBuf = Marshal.AllocHGlobal(Marshal.SizeOf<SERVICE_STATUS_PROCESS>());
            try
            {
                if (QueryServiceStatusEx(svc, 0, stBuf, Marshal.SizeOf<SERVICE_STATUS_PROCESS>(), out _))
                {
                    var st = Marshal.PtrToStructure<SERVICE_STATUS_PROCESS>(stBuf);
                    (running, between) = RunState(st.CurrentState);
                    pid = running ? (int)st.ProcessId : 0;
                }
            }
            finally { Marshal.FreeHGlobal(stBuf); }
            string? exe = ImageOf(path);
            var (signer, isMs) = Signer(exe);
            return new ServiceView
            {
                Name = name, DisplayName = display, StartType = start, Running = running, NeitherRunningNorStopped = between, BinaryPath = path,
                Publisher = signer, MicrosoftSigned = isMs, ProcessId = pid, DependsOn = dependsOn,
            };
        }
        finally { CloseServiceHandle(svc); }
    }

    /// <summary>
    /// What a state Windows gives (pure; 0: it could not be read) means for a <see cref="ServiceView"/>: running only when
    /// RUNNING; neither running nor stopped (<see cref="ServiceView.NeitherRunningNorStopped"/>) for every state but RUNNING
    /// and STOPPED, so only STOPPED reads as stopped.
    /// </summary>
    internal static (bool running, bool between) RunState(uint state) => (state == SERVICE_RUNNING, state is not (SERVICE_RUNNING or SERVICE_STOPPED));

    /// <summary>
    /// The services a service depends on, from its configuration's dependency list (names one after another, each ending in
    /// a null character, the list in an empty one), read no further than <paramref name="end"/>, the end of the buffer.
    /// A load-order group (a name after "+") is not a service and is left out.
    /// </summary>
    internal static List<string> Dependencies(IntPtr list, IntPtr end)
    {
        var names = new List<string>();
        if (list == IntPtr.Zero) return names;
        for (var p = list; (long)p + sizeof(char) <= (long)end;)
        {
            int length = 0;
            while ((long)p + (length + 1) * sizeof(char) <= (long)end && Marshal.ReadInt16(p, length * sizeof(char)) != 0) length++;
            if (length == 0 || (long)p + (length + 1) * sizeof(char) > (long)end) break; // the empty name ends the list (or the buffer ended)
            string name = Marshal.PtrToStringUni(p, length);
            if (!name.StartsWith('+')) names.Add(name);
            p += (length + 1) * sizeof(char);
        }
        return names;
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
    /// <paramref name="allowed"/> (the group 1/2 options the page built) and never protected.
    /// </summary>
    /// <returns>
    /// <c>error</c>: the start type was not set (nothing changed). <c>notStopped</c>: the start type is set, but the service
    /// is not known to have stopped, and why (Windows does not stop a service while one that depends on it runs).
    /// </returns>
    public static (string? error, NotDone? notStopped) Apply(ServicePlan.Operation op, IReadOnlySet<string> allowed, bool insiderEnrolled)
    {
        if (Guard(op.Option.Service.Name, allowed, insiderEnrolled) is { } bad) return (bad, null);
        return SetStart(op.Option.Service.Name, op.NewStart, stopIfRunning: op.NewStart == ServiceStartType.Disabled);
    }

    /// <summary>
    /// Restores a service to its saved original start type and, when it was running before PoolWatch changed it, starts it
    /// again (Part 3 review: Restore left a stopped service stopped). The same live gate as Apply; the record was already
    /// re-validated by <see cref="ServicePlan.GateRestore"/>.
    /// </summary>
    /// <returns>
    /// <c>error</c>: the start type was not put back (nothing changed). <c>notDone</c>: the start type is back, but starting
    /// the service again failed, or for an original of Disabled, it is not known to have stopped (the restore itself
    /// happened: the record can go).
    /// </returns>
    public static (string? error, NotDone? notDone) Restore(ServiceChange change, IReadOnlySet<string> allowed, bool insiderEnrolled)
    {
        if (Guard(change.Service, allowed, insiderEnrolled) is { } bad) return (bad, null);
        // Putting Disabled back (undoing "Restore to Windows default") also stops it if it has started since.
        var (err, notStopped) = SetStart(change.Service, change.OriginalStart, stopIfRunning: change.OriginalStart == ServiceStartType.Disabled);
        if (err is not null) return (err, null);
        if (notStopped is not null) return (null, notStopped);
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
        return SetStart(op.Option.Service.Name, op.NewStart, stopIfRunning: false).error;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool StartServiceW(IntPtr svc, int argc, IntPtr argv);

    /// <summary>Starts a service (no arguments). Only called by <see cref="Restore"/>, after the gate.</summary>
    private static NotDone? Start(string name)
    {
        if (!IsElevated) return new("starting a service needs administrator rights");
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) return new("could not open the service control manager: " + Marshal.GetLastWin32Error());
        try
        {
            var svc = OpenServiceW(scm, name, SERVICE_START);
            if (svc == IntPtr.Zero) return new($"could not open '{name}': {Marshal.GetLastWin32Error()}");
            try { return StartServiceW(svc, 0, IntPtr.Zero) ? null : Started(Marshal.GetLastWin32Error()); }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    /// <summary>
    /// What Windows' answer to a start means (pure): 1056, it runs already (done); 1068, a service it needs did not start
    /// (one that is Disabled, say, which Put back then names); any other code, as Windows gave it.
    /// </summary>
    internal static NotDone? Started(int error) => error switch
    {
        1056 => null, // ERROR_SERVICE_ALREADY_RUNNING
        1068 => new("a service it needs did not start (Windows error 1068)", NotDoneCause.NeededServiceNotStarted), // ERROR_SERVICE_DEPENDENCY_FAIL
        _ => new($"error {error}"),
    };

    /// <summary>The live gate: refuse anything protected, not Win32, or not in the page's allowed set.</summary>
    private static string? Guard(string name, IReadOnlySet<string> allowed, bool insiderEnrolled)
    {
        if (ServiceCatalogue.IsProtected(name, insiderEnrolled)) return $"'{name}' is a protected service";
        if (!allowed.Contains(name)) return $"'{name}' is not one of the services offered on this PC";
        return null;
    }

    internal const uint SERVICE_STOPPED = 1, SERVICE_START_PENDING = 2, SERVICE_STOP_PENDING = 3, SERVICE_RUNNING = 4,
                        SERVICE_CONTINUE_PENDING = 5, SERVICE_PAUSE_PENDING = 6, SERVICE_PAUSED = 7;

    /// <summary>
    /// How long a service that is starting, resuming, pausing or stopping is waited for to settle, and then how long a stop
    /// Windows took is waited for, so that "done" means stopped.
    /// </summary>
    internal const int StopWaitSeconds = 20;

    /// <summary>How often a service being stopped is looked at again.</summary>
    internal const int StopLookMs = 250;

    /// <summary>
    /// Sets the start type and, with <paramref name="stopIfRunning"/>, stops the service and waits until it has stopped
    /// (<see cref="Change"/>). <c>error</c>: the start type was not set, and nothing was stopped. <c>notStopped</c>: it was
    /// set (that is what sticks), but the service is not known to have stopped, and why: Windows does not stop a service
    /// while one that depends on it runs, and PoolWatch never stops that other one.
    /// </summary>
    private static (string? error, NotDone? notStopped) SetStart(string name, ServiceStartType start, bool stopIfRunning)
    {
        if (!ServicePlan.IsValidStartType(start)) return ($"'{name}': refused start type {start}", null);
        if (!IsElevated) return ("changing a service needs administrator rights", null);
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) return ("could not open the service control manager: " + Marshal.GetLastWin32Error(), null);
        try
        {
            var svc = OpenServiceW(scm, name, SERVICE_CHANGE_CONFIG | SERVICE_STOP | SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero) return ($"could not open '{name}': {Marshal.GetLastWin32Error()}", null);
            try { return Change(new OpenService(svc), name, start, stopIfRunning); }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    /// <summary>What a change does to an open service (the tests give a fake: no real service is changed there).</summary>
    internal interface IServiceCalls
    {
        /// <summary>Sets the start type: 0 when done, else Windows' error.</summary>
        int SetStartType(ServiceStartType start);
        /// <summary>The service's state (<see cref="SERVICE_RUNNING"/>, ...), 0 when it cannot be read.</summary>
        uint State();
        /// <summary>Asks the service to stop: 0 when Windows took it, else Windows' error.</summary>
        int Stop();
        /// <summary>Milliseconds on a clock that only goes forward.</summary>
        long Now { get; }
        void Sleep(int ms);
    }

    /// <summary>
    /// The start type first, then, with <paramref name="stop"/>, the stop (<see cref="StopAndWait"/>): a service already set
    /// to Disabled cannot be started again between the two (by a trigger, say). <c>error</c>: the start type was not set,
    /// and no stop was asked for, so nothing changed. <c>notStopped</c>: it was set, but the service is not known to have
    /// stopped, and why.
    /// </summary>
    internal static (string? error, NotDone? notStopped) Change(IServiceCalls svc, string name, ServiceStartType start, bool stop)
    {
        int error = svc.SetStartType(start);
        if (error != 0) return ($"could not change '{name}': {error}", null);
        return (null, stop ? StopAndWait(svc) : null);
    }

    /// <summary>
    /// Stops the service and waits until it has stopped, as <see cref="Looked"/> and <see cref="Answered"/> decide: null when
    /// it has (or was stopped already), else why it is not known to have stopped. A service that is starting, resuming,
    /// pausing or stopping is waited for to settle first, up to <see cref="StopWaitSeconds"/>; a stop Windows took is then
    /// waited for up to as long again. A stop Windows cannot take now (error 1061: the service has just begun to pause,
    /// say) is looked at again once, within the wait to settle; a second such answer ends it. A stop Windows refuses while
    /// the service is paused (as the last look found it) says so (<see cref="NotDone.Paused"/>): it is still paused, not
    /// running.
    /// </summary>
    internal static NotDone? StopAndWait(IServiceCalls svc)
    {
        bool asked = false, lookedAgain = false;
        long until = svc.Now + StopWaitSeconds * 1000L;
        while (true)
        {
            uint state = svc.State();
            var step = Looked(state, asked, svc.Now >= until);
            if (step.Next == StopNext.Wait) { svc.Sleep(StopLookMs); continue; }
            if (step.Next != StopNext.AskStop) return step.Why;
            var answer = Answered(svc.Stop(), lookedAgain);
            if (answer.Next == StopNext.LookAgain) { lookedAgain = true; svc.Sleep(StopLookMs); continue; }
            if (answer.Next != StopNext.Wait) return state == SERVICE_PAUSED && answer.Why is { } refused ? refused with { Paused = true } : answer.Why;
            asked = true;
            until = svc.Now + StopWaitSeconds * 1000L;
        }
    }

    /// <summary>
    /// What a service being stopped needs next: nothing more (<see cref="StopStep.Why"/> says why when it is not stopped), a
    /// stop, a wait, or (once) another look before the stop is asked for again.
    /// </summary>
    internal enum StopNext { Done, AskStop, Wait, LookAgain }

    /// <summary>One step of a stop, and when it ends without the service known to have stopped, why.</summary>
    internal readonly record struct StopStep(StopNext Next, NotDone? Why = null);

    private static StopStep NotStopped(string why, NotDoneCause cause = NotDoneCause.Other) => new(StopNext.Done, new NotDone(why, cause));

    /// <summary>
    /// What one look at a service PoolWatch is stopping means (pure). <paramref name="state"/>: as Windows gives it now (0:
    /// it could not be read); <paramref name="asked"/>: Windows took a stop already; <paramref name="timeUp"/>: the wait is
    /// over. Stopped: done. Running or paused, before the stop: ask for it. Starting, resuming, pausing or stopping, or not
    /// stopped yet after the stop: wait, until the time is up; then not stopped, said as still running only when it runs,
    /// else with the state it is in (<see cref="NotDoneCause.NotSettled"/>). A state that cannot be read, or that Windows
    /// does not define: not known to have stopped. Never done unless stopped.
    /// </summary>
    internal static StopStep Looked(uint state, bool asked, bool timeUp)
    {
        if (state == SERVICE_STOPPED) return new(StopNext.Done);
        if (state is < SERVICE_START_PENDING or > SERVICE_PAUSED) return NotStopped("Windows could not say whether it is running", NotDoneCause.StateUnknown);
        if (!asked && state is SERVICE_RUNNING or SERVICE_PAUSED) return new(StopNext.AskStop);
        if (!timeUp) return new(StopNext.Wait);
        if (state == SERVICE_RUNNING) return NotStopped($"it did not stop within {StopWaitSeconds} seconds"); // only after the stop: it runs on
        return NotStopped(asked ? $"it was still {Between(state)} {StopWaitSeconds} seconds after Windows took the stop"
            : $"it was still {Between(state)} after {StopWaitSeconds} seconds", NotDoneCause.NotSettled);
    }

    /// <summary>A state that is neither running nor stopped, in words.</summary>
    private static string Between(uint state) => state switch
    {
        SERVICE_START_PENDING => "starting",
        SERVICE_CONTINUE_PENDING => "resuming",
        SERVICE_PAUSE_PENDING => "pausing",
        SERVICE_STOP_PENDING => "stopping",
        _ => "paused",
    };

    /// <summary>
    /// What Windows' answer to the stop means (pure): 0, it took the stop (wait until it has stopped); 1062, it is not
    /// running (it stopped meanwhile: done); 1051, a service that depends on it is running; 1061, it cannot take a stop
    /// now (it has just begun to start, stop, resume or pause): look again, unless it was looked at again
    /// (<paramref name="lookedAgain"/>) already; 1052, it takes no stop; any other code, as Windows gave it.
    /// </summary>
    internal static StopStep Answered(int error, bool lookedAgain) => error switch
    {
        0 => new(StopNext.Wait),
        1062 => new(StopNext.Done), // ERROR_SERVICE_NOT_ACTIVE
        1051 => NotStopped("a service that depends on it is running", NotDoneCause.DependentsRunning), // ERROR_DEPENDENT_SERVICES_RUNNING
        1061 when !lookedAgain => new(StopNext.LookAgain), // ERROR_SERVICE_CANNOT_ACCEPT_CTRL
        1061 => NotStopped("it cannot take a stop now (Windows error 1061)"),
        1052 => NotStopped("it does not take a stop (Windows error 1052)"), // ERROR_INVALID_SERVICE_CONTROL
        _ => NotStopped($"Windows did not stop it (error {error})"),
    };

    /// <summary>The real calls, on a service handle opened with SERVICE_CHANGE_CONFIG, SERVICE_STOP and SERVICE_QUERY_STATUS.</summary>
    private sealed class OpenService(IntPtr svc) : IServiceCalls
    {
        public int SetStartType(ServiceStartType start) =>
            ChangeServiceConfigW(svc, SERVICE_NO_CHANGE, (uint)(int)start, SERVICE_NO_CHANGE, null, null, IntPtr.Zero, null, null, null, null) ? 0 : Marshal.GetLastWin32Error();

        public uint State()
        {
            var buf = Marshal.AllocHGlobal(Marshal.SizeOf<SERVICE_STATUS_PROCESS>());
            try
            {
                return QueryServiceStatusEx(svc, 0, buf, Marshal.SizeOf<SERVICE_STATUS_PROCESS>(), out _) ? Marshal.PtrToStructure<SERVICE_STATUS_PROCESS>(buf).CurrentState : 0;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        public int Stop()
        {
            var buf = Marshal.AllocHGlobal(Marshal.SizeOf<SERVICE_STATUS_PROCESS>());
            try { return ControlService(svc, SERVICE_CONTROL_STOP, buf) ? 0 : Marshal.GetLastWin32Error(); }
            finally { Marshal.FreeHGlobal(buf); }
        }

        public long Now => Environment.TickCount64;
        public void Sleep(int ms) => Thread.Sleep(ms);
    }
}
