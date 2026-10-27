using System.Globalization;

namespace PoolWatch.Core.Services;

/// <summary>A process's live cost in a window, with its identity (addendum 4 A): for the "PC is saturated" answer.</summary>
public sealed record ProcessCost(int Pid, string Image, string Identity, double Cores, long PageFaultsPerSec, string? CatalogueService = null);

/// <summary>
/// The system-wide page-fault split per second (addendum 4 A): demand-zero (fresh zeroed memory: a vendor telemetry service's 887k/s storm in one report),
/// transition (pages taken back from the standby / modified lists: no disk read) and hard (pages read from disk:
/// "Pages Input/sec").
/// </summary>
public readonly record struct FaultSplit(double DemandZeroPerSec, double TransitionPerSec, double PagesInputPerSec)
{
    /// <summary>"880k demand-zero (fresh memory), 5k transition, 2k hard (read from disk)".</summary>
    public string Text() =>
        $"{Rate(DemandZeroPerSec)} demand-zero (fresh memory), {Rate(TransitionPerSec)} transition, {Rate(PagesInputPerSec)} hard (pages read from disk)";

    /// <summary>The largest part, in plain words, when one dominates (at least two thirds); else null.</summary>
    public string? Dominant()
    {
        double total = DemandZeroPerSec + TransitionPerSec + PagesInputPerSec;
        if (total < 1000) return null;
        if (DemandZeroPerSec >= total * 2 / 3) return "mostly demand-zero: a process is taking fresh memory very fast";
        if (TransitionPerSec >= total * 2 / 3) return "mostly transition: pages are being trimmed and taken straight back";
        if (PagesInputPerSec >= total * 2 / 3) return "mostly hard faults: pages are being read back from disk";
        return null;
    }

    private static string Rate(double n) => ServicePlan.Faults((long)n) + "/s";
}

/// <summary>
/// One moment of system-wide load: CPU busy fraction (0..1 over all cores), run queue (threads waiting for a core; null when
/// it was not measured: PDH unavailable), whether the lag probe trips, and the fault split.
/// </summary>
public readonly record struct LoadPoint(DateTime TimeUtc, double CpuFraction, double? RunQueue, int Cores, bool ProbeTripping, FaultSplit? Faults = null);

/// <summary>
/// "PC is saturated" (addendum 4 A): the run queue stays above the core count, or the responsiveness probe (item 9)
/// trips, for over <see cref="MinSustained"/>. The answer names the top processes by CPU and by page faults. One alert
/// per episode. Pure; the counters and process costs are the caller's.
/// C2 (review round 2): without PDH there is no run queue. The caller used to put the sum of the processes' cores in its
/// place, which can never exceed the core count, so the rule could never trip. Now an unmeasured run queue turns that rule
/// off and total CPU above <see cref="CpuRuleFraction"/> takes its place (the probe still counts); the answer says so.
/// </summary>
public sealed class SaturationDetector
{
    public static readonly TimeSpan MinSustained = TimeSpan.FromSeconds(60);
    /// <summary>Without a run queue reading: total CPU above this counts as saturated (the capture governor's 85% too).</summary>
    public const double CpuRuleFraction = 0.85;

    private DateTime? _since;
    private bool _alerting;

    public bool Saturated => _alerting;

    /// <summary>Whether the moment is "under load" (the condition that must hold for 60 s).</summary>
    public static bool UnderLoad(LoadPoint p) =>
        p.ProbeTripping || (p.RunQueue is { } q ? q > p.Cores : p.CpuFraction > CpuRuleFraction);

    /// <summary>Feeds one moment. Returns an answer line when an episode has lasted past the threshold (once per episode).</summary>
    public string? Observe(LoadPoint p, IReadOnlyList<ProcessCost> costs)
    {
        if (UnderLoad(p))
        {
            _since ??= p.TimeUtc;
            if (!_alerting && p.TimeUtc - _since.Value >= MinSustained)
            {
                _alerting = true;
                return Answer(p, costs);
            }
        }
        else { _since = null; _alerting = false; }
        return null;
    }

    /// <summary>
    /// "PC is saturated: all 16 cores busy, 25 threads waiting. Top: telemetry.exe (a vendor telemetry service):
    /// 887k page faults/s, 1.7 cores; chrome.exe: 1.7 cores."
    /// </summary>
    public static string Answer(LoadPoint p, IReadOnlyList<ProcessCost> costs)
    {
        string head = $"PC is saturated: CPU {(p.CpuFraction * 100).ToString("0", CultureInfo.InvariantCulture)}% of {p.Cores} core{(p.Cores == 1 ? "" : "s")}" +
                      (p.RunQueue is { } q && q >= 1 ? $", {q.ToString("0", CultureInfo.InvariantCulture)} thread{(q < 2 ? "" : "s")} waiting" : "") +
                      (p.RunQueue is null ? $" (run queue not measured: the performance counters are unavailable, so judged on CPU above {CpuRuleFraction * 100:0}%)" : "") +
                      (p.ProbeTripping ? ", typing lags" : "");
        // The system-wide split says what kind of fault storm it is (a demand-zero storm is a process eating fresh memory).
        if (p.Faults is { } f && f.DemandZeroPerSec + f.TransitionPerSec + f.PagesInputPerSec >= 10_000)
            head += $"; page faults {f.Text()}" + (f.Dominant() is { } d ? $": {d}" : "");
        var top = costs.Where(c => c.Cores >= 0.1 || c.PageFaultsPerSec >= 10_000)
            .OrderByDescending(c => Math.Max(c.Cores, c.PageFaultsPerSec / 100_000.0)).Take(3).ToList();
        if (top.Count == 0) return head + ".";
        string who = string.Join("; ", top.Select(Describe));
        return $"{head}. Top: {who}.";
    }

    private static string Describe(ProcessCost c)
    {
        var parts = new List<string>();
        if (c.PageFaultsPerSec >= 10_000) parts.Add($"{ServicePlan.Faults(c.PageFaultsPerSec)} page faults/s");
        if (c.Cores >= 0.1) parts.Add($"{c.Cores.ToString("0.0", CultureInfo.InvariantCulture)} core{(c.Cores < 1.05 ? "" : "s")}");
        return $"{c.Identity}: {string.Join(", ", parts)}";
    }
}
