using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using PoolWatch.Core;
using PoolWatch.Native;

namespace PoolWatch.Collectors;

/// <summary>
/// Real-time ETW process start / stop, so short-lived processes and the parents of orphans are recorded even
/// when they never appear in a 10-second snapshot.
/// Primary source: the kernel process events in a private system-logger session (Windows 8+), which carry the
/// command line and exit status in the event itself. Fallback: Microsoft-Windows-Kernel-Process, which carries
/// neither, so the command line is then read from the live process (lost if it has already exited).
/// </summary>
internal sealed class ProcessEtwCollector : IDisposable
{
    public const string DefaultSessionName = "PoolWatch-Process";
    private static readonly Guid KernelProcessProvider = new("22FB2CD6-0E7B-422B-A0C7-2FAD1FD0E716");
    private const ulong KeywordProcess = 0x10;

    private readonly ProcessRegistry _registry;
    private readonly Action<string> _log;
    private readonly string _sessionName;
    private TraceEventSession? _session;
    private Thread? _thread;
    private long _eventsSeen;
    private int _generation;

    public ProcessEtwCollector(ProcessRegistry registry, Action<string> log, string sessionName = DefaultSessionName)
    {
        _registry = registry;
        _log = log;
        _sessionName = sessionName;
    }

    public bool Running { get; private set; }
    public long EventsSeen => Interlocked.Read(ref _eventsSeen);
    public int EventsLost => _session?.EventsLost ?? 0;
    public string? Error { get; private set; }
    /// <summary>"kernel" (system logger with command lines) or "Microsoft-Windows-Kernel-Process".</summary>
    public string Mode { get; private set; } = "none";

    public void Start()
    {
        Stop();
        Error = null;
        try
        {
            StartKernel();
        }
        catch (Exception ex)
        {
            _log($"kernel process session unavailable ({ex.Message}); falling back to Microsoft-Windows-Kernel-Process");
            Stop();
            try { StartKernelProcessProvider(); }
            catch (Exception ex2)
            {
                Error = ex2.Message;
                Running = false;
                _log("ETW process session could not start (process starts will come from snapshots only): " + ex2.Message);
                Stop();
            }
        }
    }

    private void StartKernel()
    {
        // Taking over an existing session of the same name cleans up after a crash of this instance.
        _session = new TraceEventSession(_sessionName) { StopOnDispose = true, BufferSizeMB = 4 };
        _session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process);
        _session.Source.Kernel.ProcessStart += OnKernelStart;
        _session.Source.Kernel.ProcessStop += OnKernelStop;
        Mode = "kernel";
        Run();
    }

    private void StartKernelProcessProvider()
    {
        _session = new TraceEventSession(_sessionName) { StopOnDispose = true, BufferSizeMB = 4 };
        _session.EnableProvider(KernelProcessProvider, TraceEventLevel.Informational, KeywordProcess);
        var parser = new RegisteredTraceEventParser(_session.Source);
        parser.All += OnManifestEvent;
        Mode = "Microsoft-Windows-Kernel-Process";
        Run();
    }

    private void Run()
    {
        var session = _session!;
        int generation = Interlocked.Increment(ref _generation);
        _thread = new Thread(() =>
        {
            ProcessDetails.OnEtwThread = true;
            try { session.Source.Process(); }
            catch (Exception ex) { Error = ex.Message; _log("ETW process session ended: " + ex.Message); }
            // A thread from an earlier session that outlived Stop() must not clear the new session's state.
            if (Volatile.Read(ref _generation) == generation) Running = false;
        }) { IsBackground = true, Name = "PoolWatch ETW process" };
        Running = true;
        _thread.Start();
    }

    private void OnKernelStart(ProcessTraceData e)
    {
        using var _ = Core.Util.Profiler.Stage("etw.start");
        Guard(() =>
        {
            Interlocked.Increment(ref _eventsSeen);
            int pid = KernelPid(e);
            // The estimate (KernelStartUtc) is on the system clock now, as the exact create time GetProcessTimes gives is,
            // when the performance counter measured the delivery wait; otherwise it is TraceEvent's own time, off by as much
            // as the system clock was set since the session started, and by the two clocks' drift. Windows' create time is
            // taken when the estimate is within 5 s of it.
            var (eventQpc, nowQpc, arrivedUtc) = Arrived(e);
            var (create, estimated) = ExactCreateTime(pid,
                KernelStartUtc(eventQpc, nowQpc, System.Diagnostics.Stopwatch.Frequency, arrivedUtc, e.TimeStamp.ToUniversalTime()).ToFileTimeUtc());
            string path = DevicePaths.ToDosPath(e.KernelImageFileName);
            string image = !string.IsNullOrEmpty(e.ImageFileName) ? e.ImageFileName : Path.GetFileName(path);
            // Only a full path is a path: some sessions deliver just "node.exe" here, which must not block the enricher's real one.
            _registry.OnStart(pid, e.ParentID, create, image, FullPathOrNull(path),
                string.IsNullOrEmpty(e.CommandLine) ? null : e.CommandLine, null, DateTime.UtcNow, createTimeEstimated: estimated);
        });
    }

    private void OnKernelStop(ProcessTraceData e)
    {
        using var _ = Core.Util.Profiler.Stage("etw.stop");
        Guard(() =>
        {
            Interlocked.Increment(ref _eventsSeen);
            // Dated as it arrives less how long it waited to be delivered (KernelExitUtc); the registry reads that onto the
            // forward clock.
            var (eventQpc, nowQpc, arrivedUtc) = Arrived(e);
            _registry.OnStop(KernelPid(e), 0, e.ExitStatus, KernelExitUtc(eventQpc, nowQpc, System.Diagnostics.Stopwatch.Frequency, arrivedUtc));
        });
    }

    /// <summary>The longest wait between an event and its delivery that <see cref="DeliveredUtc"/> takes off.</summary>
    internal static readonly TimeSpan MaxDeliveryWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When a kernel event happened, as a system time read as it arrives: <paramref name="arrivedUtc"/> less how long before
    /// that the kernel stamped it, measured on the performance counter (<paramref name="eventQpc"/>, the event's stamp, to
    /// <paramref name="nowQpc"/>, read as it arrived, <paramref name="qpcPerSecond"/> a second), which a system clock set back
    /// or forward does not move. TraceEvent's own time of an event is reckoned from the system time the session started at,
    /// so after the system clock was set it is as far off the system time now. Null for a wait under 0 or over
    /// <see cref="MaxDeliveryWait"/> (a stamp on another counter, or an event delivered that late), or without a counter.
    /// </summary>
    internal static DateTime? DeliveredUtc(long eventQpc, long nowQpc, long qpcPerSecond, DateTime arrivedUtc)
    {
        if (qpcPerSecond <= 0) return null;
        double ticks = (nowQpc - eventQpc) * (double)TimeSpan.TicksPerSecond / qpcPerSecond;
        return ticks >= 0 && ticks <= MaxDeliveryWait.Ticks ? arrivedUtc.AddTicks(-(long)ticks) : null;
    }

    /// <summary>
    /// A kernel start's time, for its create-time estimate: <see cref="DeliveredUtc"/>, else TraceEvent's own time of it
    /// (<paramref name="traceEventUtc"/>). The create time Windows gives is taken only when the estimate is within 5 s of it
    /// (<see cref="ExactCreateTime"/>): a start delivered more than 30 s late, dated as it arrived, would miss that, while
    /// TraceEvent's time is off by as much as the system clock was set since the session started, and by the two clocks'
    /// drift (about 3 s a day, TraceEvent's own documentation of SynchronizeClock says).
    /// </summary>
    internal static DateTime KernelStartUtc(long eventQpc, long nowQpc, long qpcPerSecond, DateTime arrivedUtc, DateTime traceEventUtc) =>
        DeliveredUtc(eventQpc, nowQpc, qpcPerSecond, arrivedUtc) ?? traceEventUtc;

    /// <summary>
    /// A kernel exit's time: <see cref="DeliveredUtc"/>, else as it arrives (<paramref name="arrivedUtc"/>), so its place on
    /// the forward clock never rests on a system clock set since the session started.
    /// </summary>
    internal static DateTime KernelExitUtc(long eventQpc, long nowQpc, long qpcPerSecond, DateTime arrivedUtc) =>
        DeliveredUtc(eventQpc, nowQpc, qpcPerSecond, arrivedUtc) ?? arrivedUtc;

    /// <summary>
    /// The event's stamp on its session's counter, and the performance counter (<see cref="System.Diagnostics.Stopwatch"/>)
    /// and the system clock read together as it arrives.
    /// </summary>
    private static (long EventQpc, long NowQpc, DateTime ArrivedUtc) Arrived(TraceEvent e)
    {
        // TraceEvent marks TimeStampQPC "Not Obsolete but Discouraged": its unit is the session's clock's, which need not be
        // the performance counter. A stamp in another unit gives a wait out of bounds (DeliveredUtc is null).
#pragma warning disable CS0618
        long eventQpc = e.TimeStampQPC;
#pragma warning restore CS0618
        return (eventQpc, System.Diagnostics.Stopwatch.GetTimestamp(), DateTime.UtcNow);
    }

    private void OnManifestEvent(TraceEvent e)
    {
        using var _ = Core.Util.Profiler.Stage("etw.manifestEvent");
        Guard(() =>
        {
            Interlocked.Increment(ref _eventsSeen);
            int id = (int)e.ID;
            if (id == 1)
            {
                int pid = ToInt(e.PayloadByName("ProcessID"));
                // The payload's CreateTime is the kernel's own value, so it is exact even when the process cannot be opened.
                long create = ExactCreateTime(pid, ToFileTime(e.PayloadByName("CreateTime"))).value;
                string path = DevicePaths.ToDosPath(e.PayloadByName("ImageName") as string);
                _registry.OnStart(pid, ToInt(e.PayloadByName("ParentProcessID")), create, Path.GetFileName(path), FullPathOrNull(path), null, null, DateTime.UtcNow);
            }
            else if (id == 2)
            {
                int? exit = e.PayloadByName("ExitCode") is { } x ? ToInt(x) : null;
                // The kernel's own exit time when the event carries it (a system time), else the moment it arrives.
                var exitTime = e.PayloadByName("ExitTime") is DateTime dt ? dt.ToUniversalTime() : DateTime.UtcNow;
                _registry.OnStop(ToInt(e.PayloadByName("ProcessID")), ToFileTime(e.PayloadByName("CreateTime")), exit, exitTime);
            }
        });
    }

    /// <summary>The process the event is about (payload), not the header PID of the thread that logged it.</summary>
    private static int KernelPid(ProcessTraceData e) => e.PayloadByName("ProcessID") is { } v ? ToInt(v) : e.ProcessID;

    /// <summary>The kernel's exact FILETIME (so the key matches SystemProcessInformation), else the event's estimate.</summary>
    private static (long value, bool estimated) ExactCreateTime(int pid, long estimate)
    {
        using var h = SafeProcess.Open(pid, NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION);
        if (h is not null && NativeMethods.GetProcessTimes(h.Handle, out long exact, out _, out _, out _) &&
            (estimate == 0 || Math.Abs(exact - estimate) < TimeSpan.TicksPerSecond * 5))
            return (exact, false);
        return (estimate, true);
    }

    private void Guard(Action a)
    {
        try { a(); }
        catch (Exception ex) { _log("ETW process event error: " + ex.Message); }
    }

    private static int ToInt(object? v) => v switch
    {
        null => 0,
        int i => i,
        uint u => unchecked((int)u),
        long l => (int)l,
        ulong ul => (int)ul,
        _ => Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture),
    };

    private static long ToFileTime(object? v) => v switch
    {
        DateTime dt when dt.Year > 1601 => dt.ToUniversalTime().ToFileTimeUtc(),
        long l => l,
        ulong ul => (long)ul,
        _ => 0,
    };

    private void Stop()
    {
        try { _session?.Dispose(); } catch { }
        _thread?.Join(2000);
        _session = null;
        _thread = null;
        Running = false;
    }

    public void Dispose() => Stop();

    private static string? FullPathOrNull(string? path) => path is { Length: > 3 } p && p[1] == ':' && p[2] == '\\' ? p : null;
}
