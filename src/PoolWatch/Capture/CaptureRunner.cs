using System.Diagnostics;
using System.Security;
using System.Text;
using PoolWatch.Core;
using PoolWatch.Core.Incidents;
using PoolWatch.Engine;

namespace PoolWatch.Capture;

/// <summary>
/// The bounded confirmation trace (DESIGN.md section 8): in-box wpr.exe with a generated profile that records kernel pool
/// allocations / frees with stacks (filtered to the alerting tags), process / thread / image-load events and, for Capture
/// now or with DeeperCapture, Microsoft-Windows-Kernel-File. One capture at a time, default 60 s, hard cap 5 minutes. When
/// a capture may start, and what it records, is the capture governor's call (C1). WPR's temp files and the .etl stay inside
/// the incident folder.
/// </summary>
internal sealed class CaptureRunner
{
    private readonly AppPaths _paths;
    private readonly Log _log;
    private readonly Func<string?>? _symbolPath;
    private int _running;

    /// <summary>What one capture does (WPR + parse); a test passes its own (review round 2: nothing records a real trace there).</summary>
    internal delegate CaptureResult RunCapture(string dir, IReadOnlyCollection<PoolTag> tags, CaptureScope scope, int seconds, CancellationToken stop, int cpuSeconds);
    private readonly RunCapture _run;

    /// <param name="symbolPath">Local symbol folders for the CPU trace's kernel function names (null: module + offset).</param>
    public CaptureRunner(AppPaths paths, Log log, Func<string?>? symbolPath = null) : this(paths, log, symbolPath, null) { }

    internal CaptureRunner(AppPaths paths, Log log, Func<string?>? symbolPath, RunCapture? run)
    {
        _paths = paths;
        _log = log;
        _symbolPath = symbolPath;
        _run = run ?? Run;
    }

    public bool Busy => Volatile.Read(ref _running) != 0;

    private long _lastEndMs, _startedMs;

    /// <summary>When the last capture ended (Environment.TickCount64, monotonic); null before the first. The lag probe pauses
    /// for 30 s after it (C1).</summary>
    public TimeSpan? LastEnd => Volatile.Read(ref _lastEndMs) is var ms && ms > 0 ? TimeSpan.FromMilliseconds(ms) : null;

    /// <summary>When the capture running now started (monotonic); null when none runs. C1 b (review round 2): the lag probe's
    /// pause is capped at 10 min from this, so a hung capture never silences the probe for good.</summary>
    public TimeSpan? BusySince => Busy && Volatile.Read(ref _startedMs) is var ms && ms > 0 ? TimeSpan.FromMilliseconds(ms) : null;

    /// <summary>Starts a capture on its own thread; returns false if one is already running.</summary>
    /// <param name="scope">What to record (C1, <see cref="CaptureGovernor.ScopeFor"/>): the pool events (with Kernel-File or
    /// not) and / or the CPU recording.</param>
    /// <param name="cpuSeconds">Length of the CPU recording when <paramref name="scope"/> asks for one: sampling with stacks,
    /// context switches and DPC / ISR, after the pool trace. Its own file, so it never crowds out the pool events.</param>
    public bool TryStart(string incidentDir, IReadOnlyCollection<PoolTag> tags, CaptureScope scope, int seconds, Action<CaptureResult> done, CancellationToken stop, int cpuSeconds = 0)
    {
        if (!scope.Any) return false;
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;
        Volatile.Write(ref _startedMs, Math.Max(1, Environment.TickCount64));
        seconds = Math.Clamp(seconds, 5, Settings.CaptureHardCapSeconds);
        var t = new Thread(() =>
        {
            try
            {
                // C1 a (review round 2): the done callback runs whatever happens. It used to be skipped when the capture threw,
                // so the governor never heard that the trace ended and the incident never said why it had no trace.
                var startUtc = DateTime.UtcNow;
                CaptureResult result;
                try { result = _run(incidentDir, tags, scope, seconds, stop, cpuSeconds); }
                catch (Exception ex)
                {
                    _log.Info("capture failed: " + ex);
                    result = new CaptureResult { Tags = tags.ToList(), StartUtc = startUtc, EndUtc = DateTime.UtcNow, Error = "capture failed: " + ex.Message };
                }
                try { done(result); }
                catch (Exception ex) { _log.Info("capture result not applied: " + ex); }
            }
            finally
            {
                // The end stamp first: whoever sees "not busy" also sees when it ended.
                Volatile.Write(ref _lastEndMs, Math.Max(1, Environment.TickCount64));
                Volatile.Write(ref _running, 0);
            }
        }) { IsBackground = true, Name = "PoolWatch capture" };
        t.Start();
        return true;
    }

    /// <summary>The WPR instance name PoolWatch uses: start / stop / cancel never touch anyone else's recording.</summary>
    public const string Instance = "PoolWatch";

    /// <summary>
    /// At startup: a PoolWatch recording left by a crash keeps writing forever, so cancel our own instance and delete
    /// stale WPR temp folders in the incidents directory.
    /// </summary>
    public void CleanupStale(string incidentsDir)
    {
        Wpr($"-cancel -instancename {Instance}", quiet: true);
        try
        {
            // Review round 2 (S3): CheckReal, not the name test: a wpr-temp reached through a link is never deleted.
            if (Directory.Exists(incidentsDir))
                foreach (var d in Directory.EnumerateDirectories(incidentsDir, "wpr-temp", SearchOption.AllDirectories))
                    try { Directory.Delete(_paths.Guard.CheckReal(d), true); } catch { }
        }
        catch { }
    }

    /// <summary>Cancels a running PoolWatch recording (process exit while capturing).</summary>
    public void CancelIfRunning()
    {
        if (Busy) Wpr($"-cancel -instancename {Instance}", quiet: true, timeoutSeconds: 10);
    }

    public CaptureResult Run(string dir, IReadOnlyCollection<PoolTag> tags, CaptureScope scope, int seconds, CancellationToken stop, int cpuSeconds = 0)
    {
        var r = scope.PoolEvents ? RunPool(dir, tags, seconds, stop, scope.KernelFile) : new CaptureResult { Tags = [], StartUtc = DateTime.UtcNow, EndUtc = DateTime.UtcNow };
        r.KernelFileRecorded = scope.PoolEvents && scope.KernelFile;
        if (scope.Cpu && cpuSeconds > 0 && !stop.IsCancellationRequested)
        {
            var (cpu, error) = RunCpu(dir, Math.Clamp(cpuSeconds, 5, 120), stop);
            if (cpu is not null) r.Cpu = cpu;
            else if (error is not null) r.Notes.Add("CPU trace: " + error);
            if (!scope.PoolEvents && cpu is not null) { r.StartUtc = cpu.StartUtc; r.EndUtc = cpu.EndUtc; }
        }
        return r;
    }

    /// <summary>The CPU-only recording (cpu.etl), after the pool trace: the two never share one event budget.</summary>
    private (CpuProfile? cpu, string? error) RunCpu(string dir, int seconds, CancellationToken stop)
    {
        string tempDir = _paths.EnsureDir(Path.Combine(dir, "wpr-temp"));
        string profile = _paths.Guard.CheckReal(Path.Combine(dir, "poolwatch-cpu.wprp"));
        string etl = _paths.Guard.CheckReal(Path.Combine(dir, "cpu.etl"));
        // No Kernel-File here: only the CPU part of cpu.etl is read.
        File.WriteAllText(profile, Profile([], withTagFilter: false, cpu: true, pool: false, kernelFile: false), new UTF8Encoding(false));
        var start = Wpr($"-start \"{profile}!PoolWatch\" -filemode -recordtempto \"{tempDir}\" -instancename {Instance}");
        if (start.code != 0) { Cleanup(tempDir); return (null, $"wpr -start failed ({start.code}): {start.output.Trim()}"); }
        var startUtc = DateTime.UtcNow;
        _log.Info($"capture: recording CPU for {seconds} s");
        stop.WaitHandle.WaitOne(TimeSpan.FromSeconds(seconds));
        var stopResult = Wpr($"-stop \"{etl}\" \"PoolWatch CPU trace\" -instancename {Instance} -skipPdbGen", timeoutSeconds: 300);
        var endUtc = DateTime.UtcNow;
        if (stopResult.code != 0 || !File.Exists(etl))
        {
            Wpr($"-cancel -instancename {Instance}");
            Cleanup(tempDir);
            return (null, $"wpr -stop failed ({stopResult.code}): {stopResult.output.Trim()}");
        }
        Cleanup(tempDir);
        try
        {
            string? symbols = null;
            try { symbols = _symbolPath?.Invoke(); } catch (Exception ex) { _log.Info("symbol path not read: " + ex.Message); }
            var cpu = EtlParser.Parse(etl, [], startUtc, endUtc, _log.Info, symbols, _paths.Guard.CheckReal).Cpu;
            if (cpu is not null) { cpu.StartUtc = startUtc; cpu.EndUtc = endUtc; }
            return (cpu, cpu is null ? "no CPU events in the trace" : null);
        }
        catch (Exception ex) { return (null, "parsing failed: " + ex.Message); }
    }

    private CaptureResult RunPool(string dir, IReadOnlyCollection<PoolTag> tags, int seconds, CancellationToken stop, bool kernelFile)
    {
        _paths.EnsureDir(dir);
        string tempDir = _paths.EnsureDir(Path.Combine(dir, "wpr-temp"));
        string profile = _paths.Guard.CheckReal(Path.Combine(dir, "poolwatch.wprp"));
        string etl = _paths.Guard.CheckReal(Path.Combine(dir, "capture.etl"));
        var notes = new List<string>();
        string startArgs = $"-start \"{profile}!PoolWatch\" -filemode -recordtempto \"{tempDir}\" -instancename {Instance}";

        File.WriteAllText(profile, Profile(tags, withTagFilter: true, kernelFile: kernelFile), new UTF8Encoding(false));
        var start = Wpr(startArgs);
        if (start.code != 0 && IsProfileError(start.output))
        {
            // Older WPR builds reject the pool-tag filter: record all tags (bounded by the duration), filter in the parser.
            notes.Add($"wpr rejected the pool-tag filter ({start.output.Trim()}); recorded all tags instead.");
            File.WriteAllText(profile, Profile(tags, withTagFilter: false, kernelFile: kernelFile), new UTF8Encoding(false));
            start = Wpr(startArgs);
        }
        if (start.code != 0)
        {
            // Never cancel here: the failure may be another recording that owns the kernel logger.
            Cleanup(tempDir);
            return new CaptureResult { Tags = tags.ToList(), Error = $"wpr -start failed ({start.code}): {start.output.Trim()}", Notes = notes };
        }
        var startUtc = DateTime.UtcNow;
        _log.Info($"capture: recording {seconds} s for {string.Join(", ", tags.Select(t => t.ToDisplay()))}");
        stop.WaitHandle.WaitOne(TimeSpan.FromSeconds(seconds));
        var stopResult = Wpr($"-stop \"{etl}\" \"PoolWatch confirmation trace\" -instancename {Instance} -skipPdbGen", timeoutSeconds: 300);
        var endUtc = DateTime.UtcNow;
        if (stopResult.code != 0 || !File.Exists(etl))
        {
            Wpr($"-cancel -instancename {Instance}"); // our own instance only, and before touching its temp folder
            Cleanup(tempDir);
            return new CaptureResult { Tags = tags.ToList(), StartUtc = startUtc, EndUtc = endUtc, Error = $"wpr -stop failed ({stopResult.code}): {stopResult.output.Trim()}", Notes = notes };
        }
        Cleanup(tempDir);
        _log.Info($"capture: saved {etl} ({new FileInfo(etl).Length / 1048576.0:0.0} MB)");
        try
        {
            var r = EtlParser.Parse(etl, tags, startUtc, endUtc, _log.Info, checkWrite: _paths.Guard.CheckReal);
            r.Notes.InsertRange(0, notes);
            return r;
        }
        catch (Exception ex)
        {
            return new CaptureResult { Tags = tags.ToList(), StartUtc = startUtc, EndUtc = endUtc, EtlPath = etl, Error = "parsing failed: " + ex.Message, Notes = notes };
        }
    }

    private static bool IsProfileError(string output) =>
        // Only a profile-content error (not e.g. "a profile is already running") justifies the unfiltered retry.
        output.Contains("PoolTag", StringComparison.OrdinalIgnoreCase) || output.Contains("schema", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("XML", StringComparison.OrdinalIgnoreCase) || output.Contains("element", StringComparison.OrdinalIgnoreCase);

    private void Cleanup(string tempDir)
    {
        try { if (Directory.Exists(tempDir)) Directory.Delete(_paths.Guard.CheckReal(tempDir), true); } catch { }
    }

    /// <summary>The WPR profile. Pool events need the PERF_POOL group: WPR's "Pool" system keyword plus pool stacks.</summary>
    /// <param name="cpu">Also CPU sampling with stacks, context switches (with stacks) and DPC / ISR time (addendum 2 C).</param>
    /// <param name="pool">The pool events (false for the CPU-only trace taken when the lag probe trips).</param>
    /// <param name="kernelFile">Microsoft-Windows-Kernel-File (file creates / deletes per process). C1: automatic captures
    /// leave it out unless DeeperCapture is on; the CPU recording never needs it.</param>
    internal static string Profile(IReadOnlyCollection<PoolTag> tags, bool withTagFilter, bool cpu = false, bool pool = true, bool kernelFile = true)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""<?xml version="1.0" encoding="utf-8"?>""");
        sb.AppendLine("""<WindowsPerformanceRecorder Version="1.0" Author="PoolWatch" Comments="PoolWatch confirmation trace">""");
        sb.AppendLine("  <Profiles>");
        // Small buffers: ETW buffers are non-paged pool (EtwB), on exactly the machines being diagnosed. File mode flushes them.
        sb.AppendLine("""    <SystemCollector Id="PW_SystemCollector" Name="PoolWatch System Collector"><BufferSize Value="256"/><Buffers Value="64"/></SystemCollector>""");
        if (kernelFile)
            sb.AppendLine("""    <EventCollector Id="PW_EventCollector" Name="PoolWatch Event Collector"><BufferSize Value="128"/><Buffers Value="32"/></EventCollector>""");
        sb.AppendLine("""    <SystemProvider Id="PW_SystemProvider">""");
        sb.Append("""      <Keywords><Keyword Value="ProcessThread"/><Keyword Value="Loader"/>""");
        if (pool) sb.Append("""<Keyword Value="Pool"/>""");
        if (cpu) sb.Append("""<Keyword Value="SampledProfile"/><Keyword Value="CSwitch"/><Keyword Value="DPC"/><Keyword Value="Interrupt"/>""");
        sb.AppendLine("</Keywords>");
        sb.Append("      <Stacks>");
        if (pool) sb.Append("""<Stack Value="PoolAllocation"/><Stack Value="PoolAllocationSession"/>""");
        if (cpu) sb.Append("""<Stack Value="SampledProfile"/><Stack Value="CSwitch"/>""");
        sb.AppendLine("</Stacks>");
        if (pool && withTagFilter && tags.Count > 0)
        {
            sb.AppendLine("      <PoolTags>");
            foreach (var t in tags) sb.AppendLine($"        <PoolTag Value=\"{SecurityElement.Escape(t.ToString())}\"/>");
            sb.AppendLine("      </PoolTags>");
        }
        sb.AppendLine("    </SystemProvider>");
        // Kernel-File: 0x10 file name, 0x80 create, 0x400 delete path, 0x1000 create new file.
        if (kernelFile)
            sb.AppendLine("""    <EventProvider Id="PW_KernelFile" Name="EDD08927-9CC4-4E65-B970-C2560FB5C289" NonPagedMemory="true"><Keywords><Keyword Value="0x1490"/></Keywords></EventProvider>""");
        sb.AppendLine("""    <Profile Id="PoolWatch.Verbose.File" Name="PoolWatch" Description="PoolWatch confirmation trace" LoggingMode="File" DetailLevel="Verbose">""");
        sb.AppendLine("      <Collectors>");
        sb.AppendLine("""        <SystemCollectorId Value="PW_SystemCollector"><SystemProviderId Value="PW_SystemProvider"/></SystemCollectorId>""");
        if (kernelFile)
            sb.AppendLine("""        <EventCollectorId Value="PW_EventCollector"><EventProviders><EventProviderId Value="PW_KernelFile"/></EventProviders></EventCollectorId>""");
        sb.AppendLine("      </Collectors>");
        sb.AppendLine("    </Profile>");
        sb.AppendLine("  </Profiles>");
        sb.AppendLine("</WindowsPerformanceRecorder>");
        return sb.ToString();
    }

    private (int code, string output) Wpr(string args, int timeoutSeconds = 60, bool quiet = false)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "wpr.exe"), args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        try
        {
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutSeconds * 1000))
            {
                try { p.Kill(); } catch { } // our own wpr.exe child, stuck
                return (-1, $"wpr {args} did not finish within {timeoutSeconds} s");
            }
            string output = o.Result + e.Result;
            if (!quiet || p.ExitCode == 0)
                _log.Info($"wpr {args} -> {p.ExitCode}{(output.Trim().Length > 0 ? ": " + output.Trim().Replace(Environment.NewLine, " ") : "")}");
            return (p.ExitCode, output);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }
}
