using System.Globalization;
using System.Text;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Model;

namespace PoolWatch.Core.Experiments;

public enum ExperimentPhase { Baseline, AppRunning, AfterClose, Done, Cancelled }

/// <summary>Everything compared between phases, taken from one sample at a phase boundary.</summary>
public sealed record PhaseSnapshot(DateTime TimeUtc, PoolTotals Totals, SystemCounters System, IReadOnlyDictionary<PoolTag, (long paged, long nonPaged)> Tags, int AppProcesses);

/// <summary>A tag's change in each of the three phases (bytes, paged + non-paged).</summary>
public sealed record PhaseDeltas(string Name, long Baseline, long WithApp, long AfterClose)
{
    /// <summary>Grew with the app: at least 1 MB in the phase and clearly more than the baseline phase.</summary>
    public bool GrewWithApp => WithApp >= Experiment.MinGrowth && WithApp >= 3 * Math.Max(0, Baseline) + Experiment.MinGrowth / 2;

    /// <summary>After closing it grew at most a quarter as fast as with the app (or no faster than the baseline).</summary>
    public bool Stopped => AfterClose <= Math.Max(WithApp / 4, Math.Max(0, Baseline) + Experiment.MinGrowth / 2);
}

/// <summary>What the operator is asked to do at a phase change (shown in the tray and on the timeline).</summary>
public sealed record ExperimentStep(ExperimentPhase Phase, string Prompt, Finding Finding);

/// <summary>
/// Guided A/B experiment: a baseline of N minutes, then "start &lt;app&gt; now" and N minutes, then "close &lt;app&gt;
/// now" and N minutes. At each boundary the whole tag table, the pool totals, commit and handles are snapshotted
/// (from the sample, so nothing extra is collected), and at the end every tag's change per phase is compared:
/// "Growth stopped when ChatGPT closed: non-paged pool grew 120 MB with it running and 1 MB after it closed".
/// Whether the app was really started and closed is checked from the process list (image name contains the app name).
/// Sampler thread only.
/// </summary>
public sealed class Experiment
{
    public const long MinGrowth = 1L << 20;
    private readonly List<PhaseSnapshot> _snapshots = new();
    private int _maxAppDuringRun = -1, _minAppAfterClose = int.MaxValue;

    public Experiment(string app, TimeSpan phaseLength, DateTime startUtc)
    {
        App = app.Trim();
        PhaseLength = phaseLength;
        StartedUtc = startUtc;
    }

    public string App { get; }
    public TimeSpan PhaseLength { get; }
    public DateTime StartedUtc { get; }
    public ExperimentPhase Phase { get; private set; } = ExperimentPhase.Baseline;
    public DateTime PhaseStartUtc { get; private set; }
    public IReadOnlyList<PhaseSnapshot> Snapshots => _snapshots;
    public ExperimentResult? Result { get; private set; }
    public bool IsActive => Phase is ExperimentPhase.Baseline or ExperimentPhase.AppRunning or ExperimentPhase.AfterClose;

    /// <summary>"Phase 2 of 3: start ChatGPT now (6 min left)".</summary>
    public string Status(DateTime nowUtc)
    {
        if (!IsActive) return Phase == ExperimentPhase.Done ? $"Experiment with {App} finished" : $"Experiment with {App} cancelled";
        if (_snapshots.Count == 0) return $"Experiment with {App}: waiting for the first sample";
        var left = PhaseStartUtc + PhaseLength - nowUtc;
        string what = Phase switch
        {
            ExperimentPhase.Baseline => $"phase 1 of 3: baseline, leave {App} closed",
            ExperimentPhase.AppRunning => $"phase 2 of 3: {App} running (start {App} now if you have not)",
            _ => $"phase 3 of 3: {App} closed (close {App} now if you have not)",
        };
        return $"Experiment: {what}, {Math.Max(0, Math.Ceiling(left.TotalMinutes)):0} min left";
    }

    /// <summary>Feeds one sample; returns a step when a phase changes (the first sample starts the baseline).</summary>
    public ExperimentStep? Observe(Sample s)
    {
        if (!IsActive) return null;
        int app = AppProcessCount(s.Processes);
        if (Phase == ExperimentPhase.AppRunning) _maxAppDuringRun = Math.Max(_maxAppDuringRun, app);
        if (Phase == ExperimentPhase.AfterClose && s.TimeUtc - PhaseStartUtc >= PhaseLength / 2) _minAppAfterClose = Math.Min(_minAppAfterClose, app);
        if (_snapshots.Count == 0)
        {
            _snapshots.Add(Snap(s, app));
            PhaseStartUtc = s.TimeUtc;
            return Step(s.TimeUtc, ExperimentPhase.Baseline,
                $"Experiment started: {Span(PhaseLength)} baseline. Leave {App} closed{(app > 0 ? $" ({app} {App} process(es) are running now: close them for a clean baseline)" : "")}.");
        }
        if (s.TimeUtc - PhaseStartUtc < PhaseLength) return null;
        _snapshots.Add(Snap(s, app));
        PhaseStartUtc = s.TimeUtc;
        switch (Phase)
        {
            case ExperimentPhase.Baseline:
                Phase = ExperimentPhase.AppRunning;
                return Step(s.TimeUtc, Phase, $"Experiment: start {App} now and use it as usual for {Span(PhaseLength)}.");
            case ExperimentPhase.AppRunning:
                Phase = ExperimentPhase.AfterClose;
                return Step(s.TimeUtc, Phase, $"Experiment: close {App} now (all its windows, and its tray icon if it has one). {Span(PhaseLength)} to go.");
            default:
                Phase = ExperimentPhase.Done;
                Result = Compute();
                return new ExperimentStep(Phase, Result.Statement, new Finding
                {
                    TimeUtc = s.TimeUtc, Kind = FindingKind.Experiment, Severity = FindingSeverity.Warning, Title = "Experiment result: " + Result.Statement,
                    Details = Result.Notes, Data = Result.Data(App, PhaseLength),
                });
        }
    }

    /// <summary>Stops early (the owner cancelled, or PoolWatch exits); no result.</summary>
    public Finding? Cancel(DateTime nowUtc)
    {
        if (!IsActive) return null;
        Phase = ExperimentPhase.Cancelled;
        return new Finding { TimeUtc = nowUtc, Kind = FindingKind.Experiment, Title = $"Experiment with {App} cancelled" };
    }

    private ExperimentStep Step(DateTime t, ExperimentPhase phase, string prompt) =>
        new(phase, prompt, new Finding { TimeUtc = t, Kind = FindingKind.Experiment, Title = prompt, Data = new Dictionary<string, object?> { ["app"] = App, ["phase"] = phase.ToString() } });

    /// <summary>Processes of the app: the image name contains the app name, spaces ignored ("Stream Deck" = StreamDeck.exe).</summary>
    public int AppProcessCount(IReadOnlyList<ProcessSample> processes)
    {
        string needle = App.Replace(" ", "");
        if (needle.Length == 0) return 0;
        int n = 0;
        foreach (var p in processes)
            if (p.ImageName.Replace(" ", "").Contains(needle, StringComparison.OrdinalIgnoreCase)) n++;
        return n;
    }

    private static PhaseSnapshot Snap(Sample s, int app)
    {
        var tags = new Dictionary<PoolTag, (long, long)>(s.Tags.Count);
        foreach (var t in s.Tags) tags[t.Tag] = ((long)t.PagedUsed, (long)t.NonPagedUsed);
        return new PhaseSnapshot(s.TimeUtc, s.Totals, s.System, tags, app);
    }

    private static string Span(TimeSpan t) => t.TotalMinutes >= 1
        ? t.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture) + " min" : t.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s";

    private ExperimentResult Compute()
    {
        var (a, b, c, d) = (_snapshots[0], _snapshots[1], _snapshots[2], _snapshots[3]);
        static long Tag(PhaseSnapshot p, PoolTag t) => p.Tags.TryGetValue(t, out var v) ? v.paged + v.nonPaged : 0;
        var tags = a.Tags.Keys.Concat(b.Tags.Keys).Concat(c.Tags.Keys).Concat(d.Tags.Keys).Distinct()
            .Select(t => new PhaseDeltas(t.ToDisplay(), Tag(b, t) - Tag(a, t), Tag(c, t) - Tag(b, t), Tag(d, t) - Tag(c, t)))
            .OrderByDescending(x => x.WithApp).ToList();
        var np = new PhaseDeltas("non-paged pool", Diff(b.Totals.CommittedNonPaged, a.Totals.CommittedNonPaged), Diff(c.Totals.CommittedNonPaged, b.Totals.CommittedNonPaged), Diff(d.Totals.CommittedNonPaged, c.Totals.CommittedNonPaged));
        var pg = new PhaseDeltas("paged pool", Diff(b.Totals.CommittedPaged, a.Totals.CommittedPaged), Diff(c.Totals.CommittedPaged, b.Totals.CommittedPaged), Diff(d.Totals.CommittedPaged, c.Totals.CommittedPaged));
        var commit = new PhaseDeltas("commit", Diff(b.System.CommitBytes, a.System.CommitBytes), Diff(c.System.CommitBytes, b.System.CommitBytes), Diff(d.System.CommitBytes, c.System.CommitBytes));
        var handles = new PhaseDeltas("system handles", (long)b.System.Handles - a.System.Handles, (long)c.System.Handles - b.System.Handles, (long)d.System.Handles - c.System.Handles);

        var notes = new List<string>();
        if (_maxAppDuringRun <= 0) notes.Add($"PoolWatch saw no {App} process while it should have been running: the comparison may not be about {App}.");
        if (_minAppAfterClose is > 0 and < int.MaxValue) notes.Add($"{_minAppAfterClose} {App} process(es) were still running after the close prompt (a background or tray process keeps running).");
        if (a.AppProcesses > 0) notes.Add($"{a.AppProcesses} {App} process(es) were already running when the baseline started.");

        var grew = tags.Where(t => t.GrewWithApp).Take(5).ToList();
        var pool = new[] { np, pg }.Where(p => p.GrewWithApp).OrderByDescending(p => p.WithApp).FirstOrDefault();
        string statement;
        if (grew.Count == 0 && pool is null)
            statement = $"Nothing grew with {App} running: non-paged {Signed(np.Baseline)} / {Signed(np.WithApp)} / {Signed(np.AfterClose)} and paged {Signed(pg.Baseline)} / {Signed(pg.WithApp)} / {Signed(pg.AfterClose)} (baseline / running / closed).";
        else
        {
            var lead = pool ?? grew[0];
            bool stopped = (pool?.Stopped ?? true) && grew.All(t => t.Stopped);
            var still = grew.Where(t => !t.Stopped).Select(t => t.Name).ToList();
            string verdict = stopped ? $"Growth stopped when {App} closed" : still.Count > 0 && still.Count < grew.Count
                ? $"Growth partly stopped when {App} closed ({string.Join(", ", still)} kept growing)" : $"Growth did not stop when {App} closed";
            statement = $"{verdict}: {lead.Name} grew {Signed(lead.WithApp)} with it running and {Signed(lead.AfterClose)} after it closed (baseline {Signed(lead.Baseline)}).";
            if (grew.Count > 0) statement += " Top tags with it running: " + string.Join(", ", grew.Take(3).Select(t => $"{t.Name} {Signed(t.WithApp)} then {Signed(t.AfterClose)}")) + ".";
            if (stopped && lead.AfterClose <= -MinGrowth) statement += $" {Detection.Fmt.Bytes(-lead.AfterClose)} was released after closing.";
        }
        return new ExperimentResult(statement, notes, [np, pg, commit, handles], tags);
    }

    private static long Diff(ulong to, ulong from) => (long)to - (long)from;
    private static string Signed(long b) => Detection.Fmt.SignedBytes(b);
}

/// <summary>The comparison at the end: the statement, the totals, and every tag's change per phase.</summary>
public sealed record ExperimentResult(string Statement, List<string> Notes, IReadOnlyList<PhaseDeltas> Totals, IReadOnlyList<PhaseDeltas> Tags)
{
    public IReadOnlyDictionary<string, object?> Data(string app, TimeSpan phase) => new Dictionary<string, object?>
    {
        ["app"] = app,
        ["phaseMinutes"] = Math.Round(phase.TotalMinutes, 2),
        ["statement"] = Statement,
        ["totals"] = Totals.Select(t => string.Create(CultureInfo.InvariantCulture, $"{t.Name}: {t.Baseline} / {t.WithApp} / {t.AfterClose}")).ToList(),
        ["grewWithApp"] = Tags.Where(t => t.GrewWithApp).Take(10).Select(t => t.Name).ToList(),
    };

    /// <summary>experiment.csv: every tag that changed by at least 64 KB in some phase, bytes per phase.</summary>
    public string Csv()
    {
        var sb = new StringBuilder("name,baseline_bytes,with_app_bytes,after_close_bytes,grew_with_app,stopped\n");
        foreach (var t in Totals.Concat(Tags.Where(t => Math.Max(Math.Abs(t.Baseline), Math.Max(Math.Abs(t.WithApp), Math.Abs(t.AfterClose))) >= 64 * 1024)))
            sb.Append(CultureInfo.InvariantCulture, $"{Csv(t.Name)},{t.Baseline},{t.WithApp},{t.AfterClose},{(t.GrewWithApp ? 1 : 0)},{(t.Stopped ? 1 : 0)}\n");
        return sb.ToString();
    }

    public string Markdown(string app, TimeSpan phase)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## In plain words").AppendLine().AppendLine(FindingReport.Md(Statement)).AppendLine();
        foreach (var n in Notes) sb.AppendLine("- " + FindingReport.Md(n));
        if (Notes.Count > 0) sb.AppendLine();
        sb.AppendLine($"## Per phase ({phase.TotalMinutes:0.#} min each)").AppendLine();
        sb.AppendLine($"| | Baseline ({FindingReport.Md(app)} closed) | {FindingReport.Md(app)} running | After closing |").AppendLine("|---|---:|---:|---:|");
        foreach (var t in Totals)
            sb.AppendLine(t.Name == "system handles" ? $"| {t.Name} | {t.Baseline:+#,0;-#,0;0} | {t.WithApp:+#,0;-#,0;0} | {t.AfterClose:+#,0;-#,0;0} |"
                : $"| {t.Name} | {Fmt.SignedBytes(t.Baseline)} | {Fmt.SignedBytes(t.WithApp)} | {Fmt.SignedBytes(t.AfterClose)} |");
        sb.AppendLine().AppendLine("## Tags that changed most with the app running").AppendLine();
        sb.AppendLine("| Tag | Baseline | Running | After closing | |").AppendLine("|---|---:|---:|---:|---|");
        foreach (var t in Tags.Where(t => Math.Abs(t.WithApp) >= 64 * 1024).OrderByDescending(t => Math.Abs(t.WithApp)).Take(20))
            sb.AppendLine($"| {FindingReport.Md(t.Name)} | {Fmt.SignedBytes(t.Baseline)} | {Fmt.SignedBytes(t.WithApp)} | {Fmt.SignedBytes(t.AfterClose)} | {(t.GrewWithApp ? t.Stopped ? "grew, stopped" : "grew, kept growing" : "")} |");
        sb.AppendLine().AppendLine("All tags: experiment.csv (bytes per phase).");
        return sb.ToString();
    }

    private static string Csv(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
