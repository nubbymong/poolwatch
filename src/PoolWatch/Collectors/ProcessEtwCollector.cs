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
            var (create, estimated) = ExactCreateTime(pid, e.TimeStamp.ToUniversalTime().ToFileTimeUtc());
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
            _registry.OnStop(KernelPid(e), 0, e.ExitStatus, e.TimeStamp.ToUniversalTime());
        });
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
                var exitTime = e.PayloadByName("ExitTime") is DateTime dt ? dt.ToUniversalTime() : e.TimeStamp.ToUniversalTime();
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
