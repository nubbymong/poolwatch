using System.Diagnostics;
using System.Text.Json;
using PoolWatch.Native;

namespace PoolWatch.Engine;

/// <summary>Measures PoolWatch's own overhead against the DESIGN.md section 3 budget.</summary>
internal sealed class SelfMeter
{
    /// <summary>The 2026-10-26 budget, measured on a busy PC (was 1% / 150 MB).</summary>
    public const double CpuBudgetPercent = 0.5;
    public const long RamBudgetBytes = 100L * 1024 * 1024;
    public const long DiskBudgetBytesPerHour = 5L * 1024 * 1024;

    private readonly Stopwatch _wall = Stopwatch.StartNew();
    private readonly TimeSpan _warmup;
    private TimeSpan _cpuAtWarmup;
    private ulong _writesAtWarmup;
    private double _wallAtWarmup = -1;
    private long _peakWorkingSet;
    private long _peakPrivate;
    private long _privateSum, _privateCount;

    public SelfMeter(TimeSpan warmup) => _warmup = warmup;


    /// <summary>(time, CPU, bytes written) once a minute after the warm-up, for the rolling figures in the status bar.</summary>
    private readonly Queue<(double wall, TimeSpan cpu, ulong writes)> _recent = new();
    private double _lastRecent = -60;
    /// <summary>The status bar judges the budget over at least this much steady-state time (start-up is not steady state).</summary>
    private const double MinJudgedMinutes = 5;

    /// <summary>CPU % of one core, working set and write rate over the last ~10 minutes after the warm-up.</summary>
    public (double cpuPercent, long workingSet, double writesPerHour, double minutes) Recent()
    {
        var (ws, _) = Memory();
        double wall = _wall.Elapsed.TotalSeconds;
        (double w0, TimeSpan c0, ulong b0) first;
        lock (_recent)
        {
            if (_recent.Count == 0) return (0, ws, 0, 0);
            first = _recent.Peek();
        }
        double span = wall - first.w0;
        if (span <= 1) return (0, ws, 0, 0);
        return ((Cpu() - first.c0).TotalSeconds / span * 100, ws, (WriteBytes() - first.b0) / span * 3600, span / 60);
    }

    /// <summary>"PoolWatch itself (last 10 min): CPU 0.21% of one core, 64 MB, 1.2 MB/h written", with a warning when over budget.</summary>
    public string OwnCostLine()
    {
        var (cpu, ws, wph, minutes) = Recent();
        if (minutes < MinJudgedMinutes)
            return $"PoolWatch itself: {ws / 1048576.0:0} MB; CPU and writes measured after the {_warmup.TotalMinutes:0}-minute start-up" +
                   (minutes > 0 ? $" ({minutes:0} of {MinJudgedMinutes:0} min so far)" : "");
        var over = new List<string>();
        if (cpu >= CpuBudgetPercent) over.Add("CPU");
        if (ws >= RamBudgetBytes) over.Add("memory");
        if (wph >= DiskBudgetBytesPerHour) over.Add("disk writes");
        return $"PoolWatch itself (last {minutes:0} min): CPU {cpu:0.00}% of one core, {ws / 1048576.0:0} MB, {wph / 1048576.0:0.0} MB/h written" +
               (over.Count > 0 ? $" - OVER BUDGET ({string.Join(", ", over)}; budget <{CpuBudgetPercent}%, <{RamBudgetBytes >> 20} MB, <{DiskBudgetBytesPerHour >> 20} MB/h)" : "");
    }

    /// <summary>Process CPU time without Process.GetCurrentProcess() (which snapshots every process on the machine).</summary>
    private static TimeSpan Cpu() => Environment.CpuUsage.TotalTime;

    /// <summary>Working set and private bytes (GetProcessMemoryInfo, this process only).</summary>
    private static (long workingSet, long privateBytes) Memory() =>
        NativeMethods.GetOwnMemory(out long ws, out long priv) ? (ws, priv) : (Environment.WorkingSet, 0);

    public void Tick()
    {
        var cpu = Cpu();
        var (ws, priv) = Memory();
        double now = _wall.Elapsed.TotalSeconds;
        if (_wallAtWarmup >= 0 && now - _lastRecent >= 60)
        {
            _lastRecent = now;
            lock (_recent)
            {
                _recent.Enqueue((now, cpu, WriteBytes()));
                while (_recent.Count > 11) _recent.Dequeue();
            }
        }
        _peakWorkingSet = Math.Max(_peakWorkingSet, ws);
        _peakPrivate = Math.Max(_peakPrivate, priv);
        _privateSum += priv;
        _privateCount++;
        if (_wallAtWarmup < 0 && _wall.Elapsed >= _warmup)
        {
            _wallAtWarmup = _wall.Elapsed.TotalSeconds;
            _cpuAtWarmup = cpu;
            _writesAtWarmup = WriteBytes();
        }
    }

    public OverheadReport Report()
    {
        using var p = Process.GetCurrentProcess();
        double wall = _wall.Elapsed.TotalSeconds;
        double steadyWall = _wallAtWarmup >= 0 ? wall - _wallAtWarmup : wall;
        var cpu = p.TotalProcessorTime - (_wallAtWarmup >= 0 ? _cpuAtWarmup : TimeSpan.Zero);
        ulong writes = WriteBytes() - (_wallAtWarmup >= 0 ? _writesAtWarmup : 0);
        double cpuPct = steadyWall > 0 ? cpu.TotalSeconds / steadyWall * 100 : 0;
        double writesPerHour = steadyWall > 0 ? writes / steadyWall * 3600 : 0;
        return new OverheadReport
        {
            WallSeconds = Math.Round(wall, 1),
            SteadyStateSeconds = Math.Round(steadyWall, 1),
            CpuPercentOfOneCore = Math.Round(cpuPct, 3),
            PeakWorkingSetBytes = Math.Max(_peakWorkingSet, p.WorkingSet64),
            PeakPrivateBytes = Math.Max(_peakPrivate, p.PrivateMemorySize64),
            AveragePrivateBytes = _privateCount > 0 ? _privateSum / _privateCount : p.PrivateMemorySize64,
            DiskWriteBytes = (long)writes,
            DiskWriteBytesPerHour = (long)writesPerHour,
            CpuBudgetPercent = CpuBudgetPercent,
            RamBudgetBytes = RamBudgetBytes,
            DiskBudgetBytesPerHour = DiskBudgetBytesPerHour,
        };
    }

    private static ulong WriteBytes() =>
        NativeMethods.GetProcessIoCounters(NativeMethods.GetCurrentProcess(), out var io) ? io.WriteTransferCount : 0;
}

internal sealed record OverheadReport
{
    public double WallSeconds { get; init; }
    public double SteadyStateSeconds { get; init; }
    public double CpuPercentOfOneCore { get; init; }
    public long PeakWorkingSetBytes { get; init; }
    public long PeakPrivateBytes { get; init; }
    public long AveragePrivateBytes { get; init; }
    public long DiskWriteBytes { get; init; }
    public long DiskWriteBytesPerHour { get; init; }
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
