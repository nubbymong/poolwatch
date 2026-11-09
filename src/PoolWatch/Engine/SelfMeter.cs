using System.Diagnostics;
using System.Text.Json;
using PoolWatch.Native;

namespace PoolWatch.Engine;

/// <summary>
/// Measures PoolWatch's own overhead against the DESIGN.md section 3 budget. Two marks split the run. The warm-up (the
/// sample interval three times, at least 30 s) starts the CPU and disk-write figures of overhead.json, which the CI
/// overhead check reads, and the rolling figures of <see cref="OwnCostLine"/>. Steady state starts
/// <see cref="SteadyFromMinutes"/> minutes after start (or at the warm-up, where that is later), the window the soak
/// judges (DESIGN.md section 12): only figures from it are held against the budget, in About and in the rolling line.
/// Before that PoolWatch is still starting. In a CI soak with the window open on a busy runner it used 1.4% of one core
/// between 30 s and 15 minutes and 0.46% after. In the CI UI test About was drawn about 90 s after start, right after the
/// test built and drew every window, before its next trim: the 138 MB of working set it showed then was that start-up.
/// At the end of the CI UI soak, drawn after the 8 windows (16 screenshots) of that walk, it read 127 to 131 MB of working
/// set where the steady peak was 86 to 96 MB: the CI tests now draw it first, before any other window.
/// </summary>
internal sealed class SelfMeter
{
    /// <summary>The 2026-10-26 budget, measured on a busy PC (was 1% / 150 MB).</summary>
    public const double CpuBudgetPercent = 0.5;
    public const long RamBudgetBytes = 100L * 1024 * 1024;
    public const long DiskBudgetBytesPerHour = 5L * 1024 * 1024;

    /// <summary>
    /// Steady state starts this long after PoolWatch starts, as the soak judges it (ci/soak.ps1, DESIGN.md section 12): by
    /// then its first scans, the windows built, the JIT and the start-up trims (30 s, 90 s and 3 minutes) are over. Its
    /// in-memory windows are not all full by then at their defaults: the samples (RawHistoryMinutes, 70) fill until 70
    /// minutes after start and the process events (IncidentEventKeepMinutes, 90) until 90, and the steady figures include
    /// that filling. Only the soak shortens them (ci/soak-config.json), so that they are full by then and its flat-memory
    /// check sees whether anything grows without bound.
    /// </summary>
    public const double SteadyFromMinutes = 15;

    /// <summary>The budget is judged over at least this much steady state (About), or a rolling window this long (<see cref="OwnCostLine"/>).</summary>
    public const double MinJudgedMinutes = 5;

    private readonly Stopwatch _wall = Stopwatch.StartNew();
    private readonly TimeSpan _warmup;
    private readonly TimeSpan _steadyFrom;
    private TimeSpan _cpuAtWarmup;
    private ulong _writesAtWarmup;
    private double _wallAtWarmup = -1;
    private TimeSpan _cpuAtSteady;
    private ulong _writesAtSteady;
    private double _wallAtSteady = -1;
    private long _peakWorkingSet;
    /// <summary>The highest working set read after steady state started: what About holds against the budget.</summary>
    private long _peakSteadyWorkingSet;
    private long _peakPrivate;
    private long _privateSum, _privateCount;

    public SelfMeter(TimeSpan warmup)
    {
        _warmup = warmup;
        var steady = TimeSpan.FromMinutes(SteadyFromMinutes);
        _steadyFrom = warmup > steady ? warmup : steady;
    }

    /// <summary>The start-up the CPU and the disk writes of overhead.json are measured after (the sample interval three times, at least 30 s).</summary>
    public TimeSpan Warmup => _warmup;

    /// <summary>When steady state starts: <see cref="SteadyFromMinutes"/> after start, or the warm-up where that is later.</summary>
    public TimeSpan SteadyFrom => _steadyFrom;

    /// <summary>(time, CPU, bytes written) once a minute after the warm-up, for the rolling figures in the reports and About.</summary>
    private readonly Queue<(double wall, TimeSpan cpu, ulong writes)> _recent = new();
    private double _lastRecent = -60;

    /// <summary>
    /// CPU % of one core, the working set now and the write rate over the last ~10 minutes after the warm-up, the window's
    /// length, and whether all of it is steady state (its first reading at or after <see cref="SteadyFrom"/>).
    /// </summary>
    internal (double cpuPercent, long workingSet, double writesPerHour, double minutes, bool steady) Recent(TimeSpan elapsed, TimeSpan cpuNow, long workingSetNow, ulong writesNow)
    {
        double wall = elapsed.TotalSeconds;
        (double w0, TimeSpan c0, ulong b0) first;
        lock (_recent)
        {
            if (_recent.Count == 0) return (0, workingSetNow, 0, 0, false);
            first = _recent.Peek();
        }
        double span = wall - first.w0;
        if (span <= 1) return (0, workingSetNow, 0, 0, false);
        return ((cpuNow - first.c0).TotalSeconds / span * 100, workingSetNow, (writesNow - first.b0) / span * 3600, span / 60,
            _wallAtSteady >= 0 && first.w0 >= _wallAtSteady);
    }

    /// <summary>
    /// "PoolWatch itself (last 10 min): CPU 0.21% of one core, 64 MB now, 1.2 MB/h written", with a warning when over
    /// budget: the CPU and the writes over the window, the working set as it is now (it read "64 MB", which looked like a
    /// figure over the window too). The warning is given only over a window that is all steady state: a window that holds
    /// part of the first <see cref="SteadyFromMinutes"/> minutes gives its figures and says that they are not judged.
    /// </summary>
    public string OwnCostLine() => OwnCostLine(_wall.Elapsed, Cpu(), Memory().workingSet, WriteBytes());

    /// <summary>The line at <paramref name="elapsed"/> with the process's figures then (tests give their own).</summary>
    internal string OwnCostLine(TimeSpan elapsed, TimeSpan cpuNow, long workingSetNow, ulong writesNow)
    {
        var (cpu, ws, wph, minutes, steady) = Recent(elapsed, cpuNow, workingSetNow, writesNow);
        if (minutes < MinJudgedMinutes)
            return $"PoolWatch itself: {ws / 1048576.0:0} MB now; CPU and writes measured after its {StartupWords(_warmup)} start-up" +
                   (minutes > 0 ? $" ({minutes:0} of {MinJudgedMinutes:0} min so far)" : "");
        string line = $"PoolWatch itself (last {minutes:0} min): CPU {cpu:0.00}% of one core, {ws / 1048576.0:0} MB now, {wph / 1048576.0:0.0} MB/h written";
        if (!steady) return line + $" (not judged against the budget: PoolWatch's first {SteadyFromWords(_steadyFrom)} are start-up)";
        var over = new List<string>();
        if (cpu >= CpuBudgetPercent) over.Add("CPU");
        if (ws >= RamBudgetBytes) over.Add("memory");
        if (wph >= DiskBudgetBytesPerHour) over.Add("disk writes");
        return line + (over.Count > 0 ? $" - OVER BUDGET ({string.Join(", ", over)}; budget <{CpuBudgetPercent}%, <{RamBudgetBytes >> 20} MB, <{DiskBudgetBytesPerHour >> 20} MB/h)" : "");
    }

    /// <summary>
    /// The start-up's length in words: "30-second", "1-minute", "2-minute 30-second". It said "1-minute" for the 30 s
    /// start-up (the minutes rounded).
    /// </summary>
    internal static string StartupWords(TimeSpan warmup)
    {
        int minutes = (int)warmup.TotalMinutes, seconds = warmup.Seconds;
        return (minutes, seconds) switch
        {
            (0, var s) => $"{s}-second",
            (var m, 0) => $"{m}-minute",
            var (m, s) => $"{m}-minute {s}-second",
        };
    }

    /// <summary>Where steady state starts, in words: "15 minutes", "16 minutes 30 seconds".</summary>
    internal static string SteadyFromWords(TimeSpan from)
    {
        int minutes = (int)from.TotalMinutes, seconds = from.Seconds;
        string m = minutes == 1 ? "1 minute" : $"{minutes} minutes";
        return seconds == 0 ? m : $"{m} {seconds} seconds";
    }

    /// <summary>Process CPU time without Process.GetCurrentProcess() (which snapshots every process on the machine).</summary>
    private static TimeSpan Cpu() => Environment.CpuUsage.TotalTime;

    /// <summary>Working set and private bytes (GetProcessMemoryInfo, this process only).</summary>
    private static (long workingSet, long privateBytes) Memory() =>
        NativeMethods.GetOwnMemory(out long ws, out long priv) ? (ws, priv) : (Environment.WorkingSet, 0);

    /// <summary>The working set the last <see cref="Tick"/> read (sampler thread; the memory trim looks at it).</summary>
    public long WorkingSetNow { get; private set; }

    public void Tick() => Tick(Cpu(), Memory(), _wall.Elapsed, WriteBytes);

    /// <summary>One reading: the CPU time, the working set and private bytes, and the time since start (tests give their own).</summary>
    internal void Tick(TimeSpan cpu, (long workingSet, long privateBytes) memory, TimeSpan elapsed, Func<ulong> writes)
    {
        var (ws, priv) = memory;
        WorkingSetNow = ws;
        double now = elapsed.TotalSeconds;
        if (_wallAtWarmup >= 0 && now - _lastRecent >= 60)
        {
            _lastRecent = now;
            lock (_recent)
            {
                _recent.Enqueue((now, cpu, writes()));
                while (_recent.Count > 11) _recent.Dequeue();
            }
        }
        _peakWorkingSet = Math.Max(_peakWorkingSet, ws);
        if (_wallAtSteady >= 0) _peakSteadyWorkingSet = Math.Max(_peakSteadyWorkingSet, ws);
        _peakPrivate = Math.Max(_peakPrivate, priv);
        _privateSum += priv;
        _privateCount++;
        if (_wallAtWarmup < 0 && elapsed >= _warmup)
        {
            _wallAtWarmup = now;
            _cpuAtWarmup = cpu;
            _writesAtWarmup = writes();
        }
        if (_wallAtSteady < 0 && elapsed >= _steadyFrom)
        {
            _wallAtSteady = now;
            _cpuAtSteady = cpu;
            _writesAtSteady = writes();
        }
    }

    /// <summary>
    /// The report now (About, overhead.json, the CI UI test), from this process's own reads, as each sample takes them: its
    /// CPU time and its working set and private bytes. It read them through Process.GetCurrentProcess(), whose working set
    /// and private bytes come from a snapshot of every process on the machine.
    /// </summary>
    public OverheadReport Report()
    {
        var (workingSet, privateBytes) = Memory();
        return Report(_wall.Elapsed, Cpu(), workingSet, privateBytes, WriteBytes());
    }

    /// <summary>The report at <paramref name="elapsed"/> with the process's figures then (tests give their own).</summary>
    internal OverheadReport Report(TimeSpan elapsed, TimeSpan cpuNow, long workingSetNow, long privateNow, ulong writesNow)
    {
        double wall = elapsed.TotalSeconds;
        double afterWarmup = _wallAtWarmup >= 0 ? wall - _wallAtWarmup : wall;
        var cpu = cpuNow - (_wallAtWarmup >= 0 ? _cpuAtWarmup : TimeSpan.Zero);
        ulong writes = writesNow - (_wallAtWarmup >= 0 ? _writesAtWarmup : 0);
        bool steady = _wallAtSteady >= 0;
        double steadyWall = steady ? wall - _wallAtSteady : 0;
        return new OverheadReport
        {
            WallSeconds = Math.Round(wall, 1),
            SteadyStateSeconds = Math.Round(afterWarmup, 1),
            CpuPercentOfOneCore = Math.Round(afterWarmup > 0 ? cpu.TotalSeconds / afterWarmup * 100 : 0, 3),
            PeakWorkingSetBytes = Math.Max(_peakWorkingSet, workingSetNow),
            PeakPrivateBytes = Math.Max(_peakPrivate, privateNow),
            AveragePrivateBytes = _privateCount > 0 ? _privateSum / _privateCount : privateNow,
            DiskWriteBytes = (long)writes,
            DiskWriteBytesPerHour = (long)(afterWarmup > 0 ? writes / afterWarmup * 3600 : 0),
            Judged = steady,
            JudgedFromSeconds = _steadyFrom.TotalSeconds,
            JudgedSeconds = Math.Round(steadyWall, 1),
            JudgedCpuPercentOfOneCore = Math.Round(steadyWall > 0 ? (cpuNow - _cpuAtSteady).TotalSeconds / steadyWall * 100 : 0, 3),
            JudgedPeakWorkingSetBytes = steady ? Math.Max(_peakSteadyWorkingSet, workingSetNow) : 0,
            JudgedDiskWriteBytesPerHour = (long)(steadyWall > 0 ? (writesNow - _writesAtSteady) / steadyWall * 3600 : 0),
            CpuBudgetPercent = CpuBudgetPercent,
            RamBudgetBytes = RamBudgetBytes,
            DiskBudgetBytesPerHour = DiskBudgetBytesPerHour,
        };
    }

    private static ulong WriteBytes() =>
        NativeMethods.GetProcessIoCounters(NativeMethods.GetCurrentProcess(), out var io) ? io.WriteTransferCount : 0;
}

/// <summary>
/// PoolWatch's own cost (overhead.json, the CI checks, About). The first figures run from the warm-up, so they hold the
/// later part of the start-up; the Judged figures run from the start of steady state (<see cref="JudgedFromSeconds"/>)
/// and are the only ones About holds against the budget.
/// </summary>
internal sealed record OverheadReport
{
    public double WallSeconds { get; init; }
    /// <summary>Seconds since the warm-up: the window of <see cref="CpuPercentOfOneCore"/> and the disk writes (the CI overhead check reads it).</summary>
    public double SteadyStateSeconds { get; init; }
    public double CpuPercentOfOneCore { get; init; }
    /// <summary>The highest working set since start, start-up included (the CI overhead check holds it under the budget too).</summary>
    public long PeakWorkingSetBytes { get; init; }
    public long PeakPrivateBytes { get; init; }
    public long AveragePrivateBytes { get; init; }
    public long DiskWriteBytes { get; init; }
    public long DiskWriteBytesPerHour { get; init; }
    /// <summary>Steady state has started, so the Judged figures hold something (all 0 before it).</summary>
    public bool Judged { get; init; }
    /// <summary>When steady state starts, in seconds after start: 15 minutes, as the soak judges it, or the warm-up where that is later.</summary>
    public double JudgedFromSeconds { get; init; }
    /// <summary>How long steady state has lasted so far, in seconds (0 before it starts).</summary>
    public double JudgedSeconds { get; init; }
    /// <summary>CPU % of one core over the steady state.</summary>
    public double JudgedCpuPercentOfOneCore { get; init; }
    /// <summary>The highest working set read in the steady state (0 before it starts).</summary>
    public long JudgedPeakWorkingSetBytes { get; init; }
    /// <summary>Disk writes an hour over the steady state.</summary>
    public long JudgedDiskWriteBytesPerHour { get; init; }
    public double CpuBudgetPercent { get; init; }
    public long RamBudgetBytes { get; init; }
    public long DiskBudgetBytesPerHour { get; init; }

    public bool WithinBudget => CpuPercentOfOneCore < CpuBudgetPercent && PeakWorkingSetBytes < RamBudgetBytes && DiskWriteBytesPerHour < DiskBudgetBytesPerHour;

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    public string Summary() =>
        $"CPU {CpuPercentOfOneCore:0.00}% of one core (budget <{CpuBudgetPercent}%), " +
        $"RAM peak {PeakWorkingSetBytes / 1048576.0:0} MB (budget <{RamBudgetBytes / 1048576} MB), " +
        $"disk {DiskWriteBytesPerHour / 1048576.0:0.00} MB/h (budget <{DiskBudgetBytesPerHour / 1048576} MB/h)";
}
