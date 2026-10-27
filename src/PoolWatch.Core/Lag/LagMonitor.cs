using PoolWatch.Core.Detection;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Model;

namespace PoolWatch.Core.Lag;

/// <summary>One responsiveness probe (no process is created: a process start feeds the token tags).</summary>
/// <param name="TimerMs">Actual wait for a 15 ms sleep.</param>
/// <param name="MessageMs">Post + peek of a message to the probe thread's own queue (win32k round trip; null when it failed).</param>
/// <param name="FileMs">Create, write and delete a small file under PoolWatch's own folder.</param>
/// <param name="DwmMissed">Frames missed or dropped by the compositor since the previous probe (null without DWM).</param>
public sealed record LagProbe(DateTime TimeUtc, double TimerMs, double? MessageMs, double FileMs, long? DwmMissed, PoolTotals Pool);

/// <summary>A probe compared with the baseline: the worst ratio and which measure it came from.</summary>
public sealed record LagStatus(LagProbe Probe, double Ratio, string Worst, bool Lagging)
{
    public string Text => Lagging ? $"LAG: {Worst} {Ratio:0.#}x the baseline" : $"normal ({Worst} {Ratio:0.#}x the baseline)";
}

/// <summary>
/// Turns probes into "typing lag starts at about X GB of non-paged pool". The baseline is the median of the first
/// <see cref="Settings.LagBaselineSamples"/> probes (shortly after boot when PoolWatch starts at logon). Lag = a measure at
/// <see cref="Settings.LagAlertRatio"/> times its baseline (with an absolute floor, so a 0.2 ms window-message round trip becoming
/// 1 ms is not lag) on two probes in a row; the pool size at that moment is recorded in a finding. Sampler / probe thread.
/// </summary>
public sealed class LagMonitor
{
    private readonly Settings _settings;
    private readonly List<LagProbe> _baselineProbes = new();
    private readonly Queue<LagProbe> _recent = new();
    private int _consecutive;
    private bool _lagging;
    private DateTime _lastFinding = DateTime.MinValue;

    /// <summary>Absolute floors (ms): below these a ratio means nothing.</summary>
    public const double TimerFloorMs = 50, MessageFloorMs = 20, FileFloorMs = 50;
    /// <summary>A timer this late (the probe's own schedule included) is lag by itself: a starved PC may give one probe.</summary>
    public const double DecisiveTimerMs = 1000;

    public LagMonitor(Settings settings) => _settings = settings;

    public (double timer, double? message, double file, double dwm)? Baseline { get; private set; }
    public LagStatus? Latest { get; private set; }
    public LagProbe? LagOnset { get; private set; }
    public IReadOnlyCollection<LagProbe> Recent => _recent;

    public Finding? Add(LagProbe p)
    {
        _recent.Enqueue(p);
        while (_recent.Count > 0 && (p.TimeUtc - _recent.Peek().TimeUtc > TimeSpan.FromHours(1) || _recent.Count > 2000)) _recent.Dequeue(); // the last hour
        if (Baseline is null)
        {
            _baselineProbes.Add(p);
            if (_baselineProbes.Count >= _settings.LagBaselineSamples)
            {
                var clip = _baselineProbes.Where(x => x.MessageMs is not null).Select(x => x.MessageMs!.Value).ToList();
                Baseline = (Median(_baselineProbes.Select(x => x.TimerMs)), clip.Count > 0 ? Median(clip) : null,
                    Median(_baselineProbes.Select(x => x.FileMs)), Median(_baselineProbes.Select(x => (double)(x.DwmMissed ?? 0))));
            }
            Latest = new LagStatus(p, 1, "baseline", false);
            return null;
        }
        var b = Baseline.Value;
        var ratios = new List<(string name, double ratio)>
        {
            ("timer wake-up", p.TimerMs >= TimerFloorMs ? p.TimerMs / Math.Max(1, b.timer) : Math.Min(1, p.TimerMs / Math.Max(1, b.timer))),
            ("file create/delete", p.FileMs >= FileFloorMs ? p.FileMs / Math.Max(0.5, b.file) : Math.Min(1, p.FileMs / Math.Max(0.5, b.file))),
        };
        if (p.MessageMs is { } c && b.message is { } bc) ratios.Add(("message", c >= MessageFloorMs ? c / Math.Max(0.5, bc) : Math.Min(1, c / Math.Max(0.5, bc))));
        if (p.DwmMissed is { } m && m >= 30) ratios.Add(("missed frames", m / Math.Max(3, b.dwm)));
        var worst = ratios.MaxBy(r => r.ratio);
        bool over = worst.ratio >= _settings.LagAlertRatio;
        _consecutive = over ? _consecutive + (worst.name == "timer wake-up" && p.TimerMs >= DecisiveTimerMs ? 2 : 1) : 0;
        Finding? finding = null;
        if (_consecutive >= 2 && !_lagging)
        {
            _lagging = true;
            LagOnset = p;
            if (p.TimeUtc - _lastFinding >= TimeSpan.FromMinutes(30))
            {
                _lastFinding = p.TimeUtc;
                finding = new Finding
                {
                    TimeUtc = p.TimeUtc, Kind = FindingKind.Lag, Severity = FindingSeverity.Alert,
                    Title = $"Lag: {worst.name} {worst.ratio:0.#}x the baseline at {Fmt.Bytes((long)p.Pool.CommittedNonPaged)} non-paged / {Fmt.Bytes((long)p.Pool.CommittedPaged)} paged pool",
                    Details =
                    [
                        $"Typing / window lag starts at about {Fmt.Bytes((long)p.Pool.CommittedNonPaged)} of non-paged pool (paged {Fmt.Bytes((long)p.Pool.CommittedPaged)}).",
                        $"Now: timer {p.TimerMs:0.#} ms (baseline {b.timer:0.#}), file {p.FileMs:0.#} ms (baseline {b.file:0.#})" +
                        (p.MessageMs is { } cc ? $", window message {cc:0.#} ms (baseline {b.message ?? 0:0.#})" : "") + (p.DwmMissed is { } dm ? $", {dm} missed frames" : ""),
                    ],
                    Data = new Dictionary<string, object?>
                    {
                        ["ratio"] = Math.Round(worst.ratio, 2), ["measure"] = worst.name, ["nonPagedBytes"] = p.Pool.CommittedNonPaged, ["pagedBytes"] = p.Pool.CommittedPaged,
                        ["timerMs"] = Math.Round(p.TimerMs, 2), ["fileMs"] = Math.Round(p.FileMs, 2), ["messageMs"] = p.MessageMs, ["dwmMissed"] = p.DwmMissed,
                        ["baselineTimerMs"] = Math.Round(b.timer, 2), ["baselineFileMs"] = Math.Round(b.file, 2), ["baselineMessageMs"] = b.message,
                    },
                };
            }
        }
        else if (_consecutive == 0 && _lagging && worst.ratio < _settings.LagAlertRatio / 2) _lagging = false;
        Latest = new LagStatus(p, worst.ratio, worst.name, _lagging);
        return finding;
    }

    /// <summary>
    /// Whether the probe trips now (addendum 4 A: the saturation alert's second condition): the latest probe is lagging and
    /// is recent. A stale "lagging" (the probe paused for PoolWatch's own trace, or stopped) does not count.
    /// </summary>
    public bool Tripping(DateTime nowUtc, TimeSpan maxAge) => Latest is { Lagging: true } l && nowUtc - l.Probe.TimeUtc <= maxAge;

    /// <summary>The Overview line.</summary>
    public string Summary()
    {
        if (Latest is not { } l) return "Lag (typing / window response): measuring…";
        if (Baseline is null) return $"Lag: learning the baseline ({_baselineProbes.Count} of {_settings.LagBaselineSamples} probes; timer {l.Probe.TimerMs:0.#} ms)";
        string onset = LagOnset is { } o && l.Lagging ? $" since {Fmt.Clock(o.TimeUtc)}, which began at {Fmt.Bytes((long)o.Pool.CommittedNonPaged)} non-paged pool" : "";
        return $"Lag: {l.Text}{onset} (timer {l.Probe.TimerMs:0.#} ms, file {l.Probe.FileMs:0.#} ms{(l.Probe.MessageMs is { } c ? $", window message {c:0.#} ms" : "")})";
    }

    private static double Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToList();
        return v.Count == 0 ? 0 : v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
    }
}
