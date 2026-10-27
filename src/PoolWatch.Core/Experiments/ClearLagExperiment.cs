using System.Globalization;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Lag;
using PoolWatch.Core.Model;

namespace PoolWatch.Core.Experiments;

/// <summary>
/// The A/B preset "lag before vs after a clear" (addendum 2 C): N minutes of lag probes as they are, then PoolWatch
/// clears the standby cache itself (trigger Experiment), then N minutes more. The result compares the median probe
/// times of the two phases next to the pool: "Lag after a clear: timer wake-up 18 ms -> 2 ms, file create/delete
/// 40 ms -> 4 ms; pool 12.2 GB -> 7.5 GB (the clear freed 4.7 GB)". Sampler thread only.
/// </summary>
public sealed class ClearLagExperiment
{
    /// <summary>What the owner types (tray, <c>--experiment</c>) to get this preset instead of an app.</summary>
    public const string PresetName = "clear cache";
    /// <summary>A median that changed by less than this factor (either way) counts as unchanged.</summary>
    public const double Unchanged = 1.5;

    private readonly List<LagProbe> _before = new(), _after = new();
    private PoolTotals? _poolStart, _poolAtClear, _poolEnd;

    public ClearLagExperiment(TimeSpan phaseLength, DateTime startUtc)
    {
        PhaseLength = phaseLength;
        StartedUtc = startUtc;
    }

    public static bool IsPreset(string app) => app.Replace(" ", "").Equals(PresetName.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);

    public TimeSpan PhaseLength { get; }
    public DateTime StartedUtc { get; }
    public ExperimentPhase Phase { get; private set; } = ExperimentPhase.Baseline;
    public DateTime PhaseStartUtc { get; private set; }
    public bool IsActive => Phase is ExperimentPhase.Baseline or ExperimentPhase.AfterClose;
    public string? Statement { get; private set; }
    public List<string> Notes { get; } = new();
    /// <summary>Set by the caller once it has asked for the clear (its error, when the clear could not start).</summary>
    public string? ClearError { get; set; }

    public string Status(DateTime nowUtc)
    {
        if (!IsActive) return Phase == ExperimentPhase.Done ? "Experiment (lag before vs after a clear) finished" : "Experiment (lag before vs after a clear) cancelled";
        if (_poolStart is null) return "Experiment (lag before vs after a clear): waiting for the first sample";
        var left = PhaseStartUtc + PhaseLength - nowUtc;
        string what = Phase == ExperimentPhase.Baseline ? "phase 1 of 2: lag as it is" : "phase 2 of 2: lag after the clear";
        return $"Experiment: {what}, {Math.Max(0, Math.Ceiling(left.TotalMinutes)):0} min left";
    }

    public void AddProbe(LagProbe p)
    {
        if (_poolStart is null || !IsActive) return;
        var list = Phase == ExperimentPhase.Baseline ? _before : _after;
        if (list.Count < 10_000) list.Add(p);
    }

    /// <summary>
    /// Feeds one sample. Returns the step at a phase change; <c>ClearNow</c> is true on the step that ends the first
    /// phase: the caller clears the standby cache then.
    /// </summary>
    public (ExperimentStep step, bool clearNow)? Observe(Sample s)
    {
        if (!IsActive) return null;
        if (_poolStart is null)
        {
            _poolStart = s.Totals;
            PhaseStartUtc = s.TimeUtc;
            return (Step(s.TimeUtc, ExperimentPhase.Baseline,
                $"Experiment started: {Span(PhaseLength)} of lag measurements as things are, then PoolWatch clears the standby cache and measures {Span(PhaseLength)} more. Use the PC as usual."), false);
        }
        if (s.TimeUtc - PhaseStartUtc < PhaseLength) return null;
        PhaseStartUtc = s.TimeUtc;
        if (Phase == ExperimentPhase.Baseline)
        {
            _poolAtClear = s.Totals;
            Phase = ExperimentPhase.AfterClose;
            return (Step(s.TimeUtc, Phase, $"Experiment: clearing the standby cache now; {Span(PhaseLength)} of lag measurements after it."), true);
        }
        _poolEnd = s.Totals;
        Phase = ExperimentPhase.Done;
        Statement = Compute();
        return (new ExperimentStep(Phase, Statement, new Finding
        {
            TimeUtc = s.TimeUtc, Kind = FindingKind.Experiment, Severity = FindingSeverity.Warning, Title = "Experiment result: " + Statement,
            Details = Notes.ToList(), Data = Data(),
        }), false);
    }

    public Finding? Cancel(DateTime nowUtc)
    {
        if (!IsActive) return null;
        Phase = ExperimentPhase.Cancelled;
        return new Finding { TimeUtc = nowUtc, Kind = FindingKind.Experiment, Title = "Experiment (lag before vs after a clear) cancelled" };
    }

    /// <summary>Median timer / file / window-message times per phase (ms); null where a phase has no probe of it.</summary>
    public (double? timer, double? file, double? message) Medians(bool after)
    {
        var l = after ? _after : _before;
        return (Median(l.Select(p => p.TimerMs)), Median(l.Select(p => p.FileMs)), Median(l.Where(p => p.MessageMs is not null).Select(p => p.MessageMs!.Value)));
    }

    private string Compute()
    {
        var (bt, bf, bm) = Medians(false);
        var (at, af, am) = Medians(true);
        if (ClearError is not null) Notes.Add("The clear did not run: " + ClearError);
        if (_before.Count < 3 || _after.Count < 3) Notes.Add($"Few lag probes ({_before.Count} before, {_after.Count} after): lengthen the phases.");
        var parts = new List<string>();
        void Part(string name, double? b, double? a)
        {
            if (b is { } x && a is { } y) parts.Add($"{name} {Ms(x)} -> {Ms(y)}");
        }
        Part("timer wake-up", bt, at);
        Part("file create/delete", bf, af);
        Part("window message", bm, am);
        long p0 = Pool(_poolAtClear), p1 = Pool(_poolEnd);
        string pool = $"pool {Fmt.Bytes(p0)} -> {Fmt.Bytes(p1)}" + (p0 - p1 >= Experiment.MinGrowth ? $" (the clear freed about {Fmt.Bytes(p0 - p1)})" : "");
        double worstBefore = Math.Max(Ratio(bt, at), Math.Max(Ratio(bf, af), Ratio(bm, am)));
        double worstAfter = Math.Max(Ratio(at, bt), Math.Max(Ratio(af, bf), Ratio(am, bm)));
        string verdict = ClearError is not null ? "Lag before vs after a clear: the clear did not run"
            : worstBefore >= Unchanged ? "Lag was lower after the clear"
            : worstAfter >= Unchanged ? "Lag was higher after the clear"
            : "Lag did not change with the clear";
        return $"{verdict}: {(parts.Count > 0 ? string.Join(", ", parts) : "no lag probes")}; {pool} (medians over {Span(PhaseLength)} each).";
    }

    private IReadOnlyDictionary<string, object?> Data()
    {
        var (bt, bf, bm) = Medians(false);
        var (at, af, am) = Medians(true);
        return new Dictionary<string, object?>
        {
            ["preset"] = "clear-cache", ["phaseMinutes"] = Math.Round(PhaseLength.TotalMinutes, 2), ["statement"] = Statement,
            ["timerMsBefore"] = bt, ["timerMsAfter"] = at, ["fileMsBefore"] = bf, ["fileMsAfter"] = af, ["messageMsBefore"] = bm, ["messageMsAfter"] = am,
            ["probesBefore"] = _before.Count, ["probesAfter"] = _after.Count, ["poolAtClearBytes"] = Pool(_poolAtClear), ["poolEndBytes"] = Pool(_poolEnd),
            ["clearError"] = ClearError,
        };
    }

    private static double Ratio(double? a, double? b) => a is { } x && b is { } y && y > 0 && x >= 1 ? x / y : 1; // under 1 ms is noise
    private static long Pool(PoolTotals? t) => t is { } p ? (long)(p.CommittedPaged + p.CommittedNonPaged) : 0;
    private static string Ms(double v) => v.ToString(v < 10 ? "0.#" : "0", CultureInfo.InvariantCulture) + " ms";

    private static double? Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToList();
        return v.Count == 0 ? null : v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
    }

    private ExperimentStep Step(DateTime t, ExperimentPhase phase, string prompt) =>
        new(phase, prompt, new Finding { TimeUtc = t, Kind = FindingKind.Experiment, Title = prompt, Data = new Dictionary<string, object?> { ["preset"] = "clear-cache", ["phase"] = phase.ToString() } });

    private static string Span(TimeSpan t) => t.TotalMinutes >= 1
        ? t.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture) + " min" : t.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s";
}
