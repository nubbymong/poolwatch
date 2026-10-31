using PoolWatch.Core.Correlation;
using PoolWatch.Core.Detection;

namespace PoolWatch.Core.Incidents;

/// <summary>
/// Net pool bytes one process instance allocated under one tag during the trace, with the top driver frames. A process
/// instance is its PID and create time: on a busy PC a PID is reused within minutes, so the PID alone names the wrong one.
/// </summary>
/// <param name="CreateTime">FILETIME (UTC) of the process start, from the trace; 0 when it started before the trace.</param>
/// <param name="CommandLine">From the trace's process start / rundown record.</param>
/// <param name="FirstAllocUtc">When this process made its first allocation of the tag in the trace (to find the instance).</param>
/// <param name="ExitUtc">When it exited, if that was before the trace ended.</param>
public sealed record CapturePoolUsage(int Pid, string Image, PoolTag Tag, long AllocBytes, long FreedBytes, int Allocs, int Frees,
    List<CaptureFrame> TopFrames, long CreateTime = 0, string? CommandLine = null, int ParentPid = 0, DateTime FirstAllocUtc = default, DateTime? ExitUtc = null)
{
    /// <summary>Allocations minus frees of allocations made during the trace (older blocks freed are not counted).</summary>
    public long NetBytes => AllocBytes - FreedBytes;
}

public sealed record CaptureFrame(string Frame, long Bytes);

/// <summary>File churn per process per volume from Microsoft-Windows-Kernel-File.</summary>
public sealed record CaptureFileActivity(int Pid, string Image, string Volume, int Creates, int DirectoryOpens, int Deletes, List<CaptureDirectory> TopDirectories);

public sealed record CaptureDirectory(string Path, int Opens);

/// <summary>CPU time of one kernel module (or "user mode") in the trace.</summary>
public sealed record CpuModule(string Module, double Ms, double Share);

/// <summary>Time threads spent waiting, attributed to the kernel module on their stack when they resumed.</summary>
public sealed record WaitModule(string Module, double WaitMs, long Switches);

/// <summary>
/// A kernel function in the CPU trace (addendum 2 C): "refs.sys!RefsLookupFcb" when symbols named it, else "refs.sys+0x1a2b0"
/// (module + offset, which can be looked up later with the module's symbols). <paramref name="Ms"/> is CPU or wait time.
/// </summary>
public sealed record CpuFunction(string Name, double Ms, double Share, bool Symbolized);

/// <summary>
/// The CPU part of a trace (addendum 2 C): where the processors spent the time (sampled, by module and by kernel function),
/// DPC / ISR time by module, and where threads waited (context switches with stacks, by module and function). Function names
/// need symbols from a local, admin-only folder (<see cref="Settings.SymbolPath"/>, judged by <see cref="Util.SymbolPaths"/>;
/// a symbol server is never contacted); without them functions are module + offset and <see cref="FunctionsNote"/> says so.
/// </summary>
public sealed class CpuProfile
{
    public double Seconds { get; init; }
    public long Samples { get; init; }
    public int Processors { get; init; }
    public List<CpuModule> TopCpu { get; init; } = [];
    public List<CpuModule> TopDpcIsr { get; init; } = [];
    public List<WaitModule> TopWait { get; init; } = [];
    /// <summary>Kernel-mode CPU by function (or module + offset).</summary>
    public List<CpuFunction> TopFunctions { get; init; } = [];
    /// <summary>Wait time by the kernel function on the resuming thread's stack (or module + offset).</summary>
    public List<CpuFunction> TopWaitFunctions { get; init; } = [];
    /// <summary>Where the function names came from, or why they are module + offset.</summary>
    public string FunctionsNote { get; init; } = "";
    public long ContextSwitches { get; init; }
    /// <summary>When the CPU recording ran (its own trace, after the pool trace).</summary>
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    /// <summary>Paged + non-paged pool when the trace was taken (set by the caller).</summary>
    public long PoolBytes { get; set; }

    /// <summary>"CPU: 38% busy; kernel time mostly in refs.sys (21%), FLTMGR.SYS (9%), ntoskrnl (8%); waits: refs.sys 4.1 s".</summary>
    public string Summary()
    {
        double total = Seconds * Math.Max(1, Processors) * 1000;
        double busy = TopCpu.Where(m => m.Module != "idle").Sum(m => m.Ms);
        var kernel = TopCpu.Where(m => m.Module is not ("user mode" or "idle")).Take(3).Select(m => $"{m.Module} ({Pct(m.Share)})");
        var waits = TopWait.Take(3).Select(w => $"{w.Module} {(w.WaitMs / 1000).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} s");
        return $"CPU {Pct(total > 0 ? busy / total : 0)} busy over {Seconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture)} s; kernel time mostly in {string.Join(", ", kernel)}" +
               // Only named functions: without symbols the rows are single instruction addresses, not functions (Part 3 review).
               (TopFunctions.Any(f => f.Symbolized) ? $"; top kernel functions {string.Join(", ", TopFunctions.Where(f => f.Symbolized).Take(3).Select(f => $"{f.Name} ({Pct(f.Share)})"))}" : "") +
               (TopDpcIsr.Count > 0 ? $"; DPC / ISR: {string.Join(", ", TopDpcIsr.Take(2).Select(d => $"{d.Module} {d.Ms.ToString("0", System.Globalization.CultureInfo.InvariantCulture)} ms"))}" : "") +
               (TopWait.Count > 0 ? $"; waits: {string.Join(", ", waits)}" : "") + (PoolBytes > 0 ? $" (pool {Detection.Fmt.Bytes(PoolBytes)} then)" : "");
    }

    private static string Pct(double x) => (x * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";

    /// <summary>"module!function" when symbols named it, else "module+0x1a2b0".</summary>
    public static string FrameName(string module, string? function, ulong offset) =>
        !string.IsNullOrEmpty(function) ? $"{module}!{function}" : $"{module}+0x{offset.ToString("x", System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The note under the functions table: where the names came from, or that there were no local symbols (and how to
    /// get them) and the functions are module + offset.
    /// </summary>
    public static string NoteFor(string? symbolPath, bool anySymbolized, string? error = null)
    {
        if (error is not null) return $"Kernel functions are module + offset: the symbols could not be read ({error}).";
        if (symbolPath is null)
            return "Kernel functions are module + offset: no admin-only symbol folder (copy the PDBs, for example from a WinDbg or WPA symbol " +
                   "cache, into the 'symbols' folder of PoolWatch's data folder, or set SymbolPath to another admin-only folder; PoolWatch runs as " +
                   "administrator, so it never reads symbols from a folder users can write to, a network drive or a symbol server). The offsets " +
                   "can be looked up later with those symbols.";
        return anySymbolized
            ? $"Kernel function names from the local symbols in {symbolPath}; a module without symbols there shows as module + offset."
            : $"Kernel functions are module + offset: no matching symbols in {symbolPath} (open the trace once in WPA with symbols loaded to fill it).";
    }
}

/// <summary>The parsed confirmation trace (DESIGN.md section 8).</summary>
public sealed class CaptureResult
{
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public string? EtlPath { get; init; }
    public List<PoolTag> Tags { get; init; } = [];
    /// <summary>True when kernel pool allocation events were read from the .etl.</summary>
    public bool PoolEventsParsed { get; init; }
    public string Library { get; init; } = "";
    public long PoolEvents { get; init; }
    public long StackedPoolEvents { get; init; }
    public long FileEvents { get; init; }
    /// <summary>Whether Kernel-File was recorded (C1: automatic captures record pool events only unless DeeperCapture is on).</summary>
    public bool KernelFileRecorded { get; set; } = true;
    public List<CapturePoolUsage> Pool { get; init; } = [];
    public List<CaptureFileActivity> Files { get; init; } = [];
    public List<string> Notes { get; init; } = [];
    public string? Error { get; init; }
    /// <summary>The CPU part, when the trace recorded it.</summary>
    /// <summary>From the separate CPU recording (cpu.etl) that follows the pool trace, when one was asked for.</summary>
    public CpuProfile? Cpu { get; set; }
}

/// <summary>How a capture changes the ranking: a suspect that holds a large share of the traced net bytes is confirmed.</summary>
public static class CaptureAttribution
{
    /// <summary>Share of the tag's traced net growth a process must hold to count as "confirmed by trace".</summary>
    public const double ConfirmShare = 0.3;
    /// <summary>
    /// Also confirmed: at least this share of the traced growth, when the process's net also covers at least
    /// <see cref="ConfirmGrowthShare"/> of the tag's own growth during the trace (and at least <see cref="ConfirmMinBytes"/>).
    /// Unrelated churn in the same tag (antivirus scanning another volume, seen on CI) must not hide a process the trace
    /// caught allocating a real part of the growth; the floor scales with the leak, so it stays strict at GB scale.
    /// </summary>
    public const double ConfirmShareWithBytes = 0.15;
    public const double ConfirmGrowthShare = 0.25;
    public const long ConfirmMinBytes = 1L << 20;

    /// <param name="tagGrowthDuringTrace">The tag's measured growth between the trace's start and end (null: unknown).</param>
    public static void Apply(Incident inc, CaptureResult capture, long? tagGrowthDuringTrace = null)
    {
        long floor = Math.Max(ConfirmMinBytes, (long)(Math.Max(0, tagGrowthDuringTrace ?? 0) * ConfirmGrowthShare));
        inc.Capture = capture;
        if (!capture.PoolEventsParsed) return;
        var usage = capture.Pool.Where(u => u.Tag == inc.Tag).ToList();
        long total = usage.Where(u => u.NetBytes > 0).Sum(u => u.NetBytes);
        if (total <= 0) return;
        foreach (var s in inc.Suspects)
        {
            // The same process instance: PID and (when the trace knows it) create time; a reused PID is another process.
            // A row without a create time (alive when the trace began) is the instance alive then, not a dead one with the same PID.
            var mine = usage.Where(u => u.Pid == s.Key.Pid && (u.CreateTime != 0 && s.Key.CreateTime != 0
                ? Math.Abs(u.CreateTime - s.Key.CreateTime) < 2 * TimeSpan.TicksPerSecond
                : s.ExitUtc is not { } x || x >= capture.StartUtc)).ToList();
            long net = mine.Sum(u => u.NetBytes);
            if (net <= 0) continue;
            double share = net / (double)total;
            s.TraceNetBytes = net;
            string frames = string.Join(", ", mine.SelectMany(u => u.TopFrames).OrderByDescending(f => f.Bytes).Take(3).Select(f => f.Frame));
            s.Evidence.Insert(0, $"trace: {Fmt.SignedBytes(net)} net {inc.Tag.ToDisplay()} in {Fmt.Duration(capture.EndUtc - capture.StartUtc)} " +
                                 $"({share:P0} of the traced growth, {mine.Sum(u => u.Allocs):N0} allocations){(frames.Length > 0 ? $"; top frames {frames}" : "")}");
            if (share >= ConfirmShare || (share >= ConfirmShareWithBytes && net >= floor))
            {
                s.Confidence = Confidence.ConfirmedByTrace;
                s.Score += 5;
            }
        }
        var ranked = inc.Suspects.OrderByDescending(s => s.Score).ToList();
        for (int i = 0; i < ranked.Count; i++) ranked[i].Rank = i + 1;
        inc.Suspects = ranked;
    }
}
