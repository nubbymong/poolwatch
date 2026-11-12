using PoolWatch.Core.Detection;
using PoolWatch.Core.History;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;

namespace PoolWatch.Core.Correlation;

/// <summary>How strongly the evidence ties a suspect to the growth (DESIGN.md section 7, point 5).</summary>
public enum Confidence
{
    /// <summary>Ranked only because it was active; no signal passed its threshold.</summary>
    Weak,
    /// <summary>One of activity / lifecycle (signals 2-4) agrees.</summary>
    Correlated,
    /// <summary>A direct charge, or several signals agree.</summary>
    StronglyCorrelated,
    /// <summary>The confirmation trace attributed the allocations (milestone 3).</summary>
    ConfirmedByTrace,
}

/// <summary>Signal 1: the process's handles of the tag's object type and its quota pool usage rose.</summary>
public sealed record DirectSignal(string? HandleType, int HandlesBefore, int HandlesAfter, double HandleShare, long QuotaDelta, double QuotaShare, bool Present)
{
    public int HandleDelta => HandlesAfter - HandlesBefore;
}

/// <summary>Signal 2: rank correlation of the tag's growth with the process's I/O rate, and its share of all I/O.</summary>
public sealed record ActivitySignal(string Counter, double Spearman, int BestLag, double LaggedSpearman, double IoShare, long OpsDelta, bool Present)
{
    public double Best => double.IsNaN(LaggedSpearman) ? Spearman : double.IsNaN(Spearman) ? LaggedSpearman : Math.Max(Spearman, LaggedSpearman);
}

/// <summary>Signal 3: start before onset, exit when growth stopped, orphaned while busy.</summary>
public sealed record LifecycleSignal(bool StartedBeforeOnset, bool ExitedAtEnd, bool OrphanActive, List<string> Notes)
{
    public bool Present => StartedBeforeOnset || ExitedAtEnd || OrphanActive;
}

/// <summary>One ranked suspect with the evidence behind its rank.</summary>
public sealed class Suspect
{
    public required ProcessKey Key { get; init; }
    public required string Image { get; init; }
    public string DisplayName { get; set; } = "";
    public string? CommandLine { get; init; }
    public string? ImagePath { get; init; }
    public string? User { get; init; }
    public DateTime StartUtc { get; init; }
    /// <summary>
    /// PoolWatch held no record of the process when it was ranked (the registry had let it go: it is named by when the
    /// history saw it): its exit is not known, so an empty <see cref="ExitUtc"/> does not mean it still ran.
    /// </summary>
    public bool Unresolved { get; init; }
    public DateTime? ExitUtc { get; init; }
    /// <summary><see cref="ExitUtc"/> on the clock that only goes forward (<see cref="ProcessRecord.ExitMs"/>): whether it exited before a trace began is timed on it.</summary>
    public long? ExitMs { get; init; }
    public int? ExitCode { get; init; }
    public bool IsOrphan { get; init; }
    public DateTime? OrphanedUtc { get; init; }
    public List<string> Services { get; init; } = [];
    public List<string> Ancestry { get; init; } = [];
    public required DirectSignal Direct { get; init; }
    public required ActivitySignal Activity { get; init; }
    public required LifecycleSignal Lifecycle { get; init; }
    public double Score { get; set; }
    public Confidence Confidence { get; set; }
    public List<string> Evidence { get; } = [];
    public int Rank { get; set; }
    /// <summary>Its ancestry passes through WmiPrvSE.exe: the real requester is hidden behind WMI.</summary>
    public bool ViaWmi { get; init; }
    /// <summary>Net bytes of the tag the confirmation trace attributed to this process (0 without a trace).</summary>
    public long TraceNetBytes { get; set; }
}

/// <summary>The time range the correlation looks at: a baseline before onset, the growth episode, and its end.</summary>
public sealed record CorrelationWindow(DateTime FromUtc, DateTime OnsetUtc, DateTime? EndUtc, DateTime ToUtc)
{
    // The same window on the clock that only goes forward (the samples' History.Ms). Every window PoolWatch makes is made
    // from samples (an incident's ranking, Of, Recent) and carries these: its samples are then found by them, so a system
    // clock set back or forward inside the window moves none of them. A window given only as times of day (a test's) is
    // found by those.
    public long? FromMs { get; init; }
    public long? OnsetMs { get; init; }
    public long? EndMs { get; init; }
    public long? ToMs { get; init; }

    /// <summary>The window over samples [<paramref name="from"/>, <paramref name="to"/>] of <paramref name="h"/>, with its onset and end at those samples, on both clocks.</summary>
    public static CorrelationWindow Of(History.History h, int from, int onset, int? end, int to) =>
        new(h.Times[from], h.Times[onset], end is { } e ? h.Times[e] : null, h.Times[to])
        {
            FromMs = h.Ms[from], OnsetMs = h.Ms[onset], EndMs = end is { } e2 ? h.Ms[e2] : null, ToMs = h.Ms[to],
        };

    /// <summary>
    /// The last <paramref name="span"/> of <paramref name="h"/> up to its newest sample, timed on the clock that only goes
    /// forward (all of it when it holds less), with the onset and end of <paramref name="tag"/>'s growth in it: the window a
    /// tag's live view ranks over. The history holds at least one sample; the caller holds its lock.
    /// </summary>
    public static RecentWindow Recent(History.History h, PoolTag tag, TimeSpan span)
    {
        int last = h.Count - 1;
        long sinceMs = Math.Max(h.Ms[0], h.Ms[last] - (long)span.TotalMilliseconds);
        int from = h.IndexAtOrAfterMs(sinceMs);
        var series = h.TagSeries(tag, from);
        var cp = Detector.FindChangePoints(series);
        var w = Of(h, from, Math.Min(last, from + cp.OnsetIndex), cp.EndIndex is { } e ? Math.Min(last, from + e) : null, last);
        return new RecentWindow(w, from, series, cp) { SinceMs = sinceMs };
    }

    /// <summary>The first sample of the window in <paramref name="h"/>.</summary>
    public int FromIndex(History.History h) => FromMs is { } m ? h.IndexAtOrAfterMs(m) : h.IndexAtOrAfter(FromUtc);
    /// <summary>The first sample after the window's end <paramref name="toUtc"/> / <paramref name="toMs"/> (its own when null).</summary>
    public int AfterIndex(History.History h, DateTime? toUtc = null, long? toMs = null) =>
        (toMs ?? (toUtc is null ? ToMs : null)) is { } m ? h.IndexAtOrAfterMs(m + 1) : h.IndexAtOrAfter((toUtc ?? ToUtc).AddTicks(1));
    /// <summary>The window's onset sample.</summary>
    public int OnsetIndex(History.History h) => OnsetMs is { } m ? h.IndexAtOrAfterMs(m) : h.IndexAtOrAfter(OnsetUtc);
    /// <summary>The window's end sample; null when the growth has not ended.</summary>
    public int? EndIndex(History.History h) => EndUtc is not { } e ? null : EndMs is { } m ? h.IndexAtOrAfterMs(m) : h.IndexAtOrAfter(e);
}

/// <summary>A tag's recent window (<see cref="CorrelationWindow.Recent"/>): the window, its first sample, the tag's series from it and the change points in it.</summary>
public sealed record RecentWindow(CorrelationWindow Window, int From, long[] Series, ChangePoints ChangePoints)
{
    /// <summary>
    /// Where the span begins on the clock that only goes forward: the newest sample's time less the span, or the first
    /// sample's when the history holds less (the window's first sample is the first at or after it). The events of the span
    /// are read from here, and its length is said from here.
    /// </summary>
    public long SinceMs { get; init; }
}

/// <summary>
/// Ranks processes as suspects for one tag's growth, using every signal of DESIGN.md section 7. Works on the raw
/// <see cref="History"/>; the caller holds the history lock. Never states causation: the confidence label says
/// how many independent signals agree.
/// </summary>
public static class Correlator
{
    /// <summary>Spearman needed for the activity signal (with an I/O share of at least <see cref="MinIoShare"/>).</summary>
    public const double MinSpearman = 0.5;
    public const double MinIoShare = 0.05;
    /// <summary>A process doing this share of all I/O counts as active with a weaker correlation (0.3).</summary>
    public const double DominantIoShare = 0.5;
    public const int MinHandleDelta = 100;
    public const double MinHandleShare = 0.3;
    public const int MaxLag = 3;

    public static List<Suspect> Rank(History.History h, ProcessRegistry registry, PoolTag tag, CorrelationWindow w, int maxSuspects = 10)
    {
        int from = w.FromIndex(h);
        int to = Math.Min(h.Count - 1, Math.Max(from, w.AfterIndex(h) - 1));
        if (h.Count < 3 || to - from < 2) return [];
        int onset = Math.Clamp(w.OnsetIndex(h), from, to);
        int end = w.EndIndex(h) is { } e ? Math.Clamp(e, onset, to) : to;
        if (end <= onset) end = Math.Min(to, onset + 1);

        var series = h.TagSeries(tag, from);
        int n = to - from + 1;
        var tagDeltas = new double[n - 1];
        for (int i = 0; i < n - 1; i++) tagDeltas[i] = series[i + 1] - series[i];
        long growth = series[end - from] - series[onset - from];

        // Direct-charge inputs: handle type(s) for the tag and the handle scans around the episode.
        BuiltinTags.HandleTypes.TryGetValue(tag, out var handleTypes);
        string? handleType = handleTypes?.FirstOrDefault();
        // The scans beside the onset and the end, on the clock that only goes forward.
        var scanBefore = h.HandlesAtOrBeforeMs(h.Ms[onset]);
        var scanAfter = h.HandlesAtOrBeforeMs(h.Ms[end]) is { } sa && scanBefore is not null && sa.Ms > scanBefore.Ms ? sa : h.LatestHandles;
        bool haveScans = handleType is not null && scanBefore is not null && scanAfter is not null && scanAfter.Ms > scanBefore.Ms;

        // Every process of the window is read in place: on a busy PC most are short commands seen once, which the filter
        // below drops before anything is copied or correlated for them.
        var keys = h.ProcessesSince(from);
        var rows = new List<(ProcessKey key, ProcessPoints pts, long ops, long quota, int hBefore, int hAfter)>();
        long totalOps = 0, totalQuotaUp = 0, totalHandleUp = 0;
        foreach (var key in keys)
        {
            var pts = h.Points(key);
            long ops = OpsDelta(pts, onset, end);
            long quota = QuotaDelta(pts, onset, end);
            int hb = 0, ha = 0;
            if (haveScans)
            {
                hb = scanBefore!.Count(key, handleType!);
                ha = scanAfter!.Has(key) ? scanAfter.Count(key, handleType!) : hb;
            }
            totalOps += Math.Max(0, ops);
            totalQuotaUp += Math.Max(0, quota);
            totalHandleUp += Math.Max(0, ha - hb);
            rows.Add((key, pts, ops, quota, hb, ha));
        }

        var suspects = new List<Suspect>();
        var tagRanks = new TagRanks(tagDeltas);
        var deltas = new double[tagDeltas.Length]; // one counter's deltas, reused for every process and counter
        foreach (var (key, pts, ops, quota, hb, ha) in rows)
        {
            if (key.Pid == 0) continue; // Idle
            double ioShare = totalOps > 0 ? Math.Max(0, ops) / (double)totalOps : 0;
            double hShare = totalHandleUp > 0 ? Math.Max(0, ha - hb) / (double)totalHandleUp : 0;
            double qShare = totalQuotaUp > 0 ? Math.Max(0, quota) / (double)totalQuotaUp : 0;
            bool directHandles = haveScans && growth > 0 && ha - hb >= MinHandleDelta && hShare >= MinHandleShare;
            // Quota alone counts only when it is large in absolute terms (it is charged for many object kinds).
            bool directQuota = growth > 0 && quota >= Math.Max(1L << 20, growth / 10) && qShare >= 0.5;

            // Start / exit timing only means something for a process that did something in the episode.
            bool active = ioShare >= 0.01 || ha - hb >= MinHandleDelta || quota >= 64 * 1024;
            // Nothing to rank it on: no direct charge, no I/O or handles in the episode, and not active (every lifecycle
            // signal needs the process to be active), so it is left out as below, without its record being read: on a busy
            // PC most processes of the window are short commands that did nothing in the episode.
            if (!directHandles && !directQuota && !active && ops <= 0 && ha - hb <= 0) continue;
            var rec = registry.Get(key);
            var direct = new DirectSignal(haveScans ? handleType : null, hb, ha, hShare, quota, qShare, directHandles || directQuota);
            var lifecycle = Lifecycle(rec, h, from, onset, end, to, ioShare, active);

            if (!direct.Present && !lifecycle.Present && ops <= 0 && ha - hb <= 0) continue;
            var activity = Activity(tagRanks, pts, from, ops, ioShare, deltas);

            var chain = registry.Ancestry(key);
            bool viaWmi = chain.Take(chain.Count - 1).Any(a => a.Image.Equals("WmiPrvSE.exe", StringComparison.OrdinalIgnoreCase));
            var s = new Suspect
            {
                Key = key,
                Image = rec?.Image ?? "unknown process",
                // Never "?": a process PoolWatch no longer holds is named by when it ran (from its history points).
                DisplayName = rec?.DisplayName ?? Incidents.TracedProcess.UnknownName(key.CreateTime > 0 ? key.CreateTimeUtc : null, LastSeen(h, pts)),
                CommandLine = rec?.CommandLine,
                ImagePath = rec?.ImagePath,
                User = rec?.User,
                StartUtc = key.CreateTimeUtc,
                Unresolved = rec is null,
                ExitUtc = rec?.ExitUtc,
                ExitMs = rec?.ExitMs,
                ExitCode = rec?.ExitCode,
                IsOrphan = rec?.IsOrphan ?? false,
                OrphanedUtc = rec?.OrphanedUtc,
                Services = rec?.Services.ToList() ?? [],
                Ancestry = chain.Select(r => r.DisplayName).ToList(),
                ViaWmi = viaWmi,
                Direct = direct,
                Activity = activity,
                Lifecycle = lifecycle,
            };
            s.Score = Score(s);
            s.Confidence = Classify(s);
            Explain(s, tag, growth, totalHandleUp);
            suspects.Add(s);
        }

        var ranked = suspects.OrderByDescending(s => s.Score).ThenByDescending(s => s.Activity.IoShare).Take(maxSuspects).ToList();
        for (int i = 0; i < ranked.Count; i++) ranked[i].Rank = i + 1;
        // A shared runtime (node, python, pwsh, cmd, git, ...) is named by what it runs and for whom, never by image alone.
        // Only for the suspects anyone reads (the identity walks the command line and the ancestry).
        foreach (var s in ranked.Take(IdentityTop))
            if (Identity.ProcessIdentities.SharedRuntimes.Contains(s.Image) || s.ViaWmi)
                if (registry.Ancestry(s.Key) is { Count: > 0 } chain) s.DisplayName = Identity.ProcessIdentities.Of(chain).Display;
        return ranked;
    }

    /// <summary>Suspects whose display name gets the full identity.</summary>
    public const int IdentityTop = 50;

    /// <summary>The last sample a process was seen in (its exit, roughly), or null while it is still in the latest sample.</summary>
    private static DateTime? LastSeen(History.History h, ProcessPoints pts) =>
        pts.Last < pts.First || pts.Last >= h.Count - 1 ? null : h.Times[pts.Last];

    public static double Score(Suspect s)
    {
        double score = 0;
        if (s.Direct.Present) score += 3 + Math.Max(s.Direct.HandleShare, s.Direct.QuotaShare);
        double r = s.Activity.Best;
        if (s.Activity.Present) score += 2 * r;
        else if (!double.IsNaN(r) && r > 0) score += 0.5 * r;
        score += 2 * s.Activity.IoShare;
        if (s.Lifecycle.StartedBeforeOnset) score += 1;
        if (s.Lifecycle.ExitedAtEnd) score += 1;
        if (s.Lifecycle.OrphanActive) score += 1.5;
        return score;
    }

    public static Confidence Classify(Suspect s)
    {
        int corroborating = (s.Activity.Present ? 1 : 0) + (s.Lifecycle.Present ? 1 : 0);
        if (s.Direct.Present || corroborating >= 2) return Confidence.StronglyCorrelated;
        return corroborating == 1 ? Confidence.Correlated : Confidence.Weak;
    }

    private static readonly string[] Counters = ["total", "other", "read"];

    /// <param name="from">The raw sample index of the window's first sample (the tag deltas start there).</param>
    /// <param name="d">A buffer as long as the tag deltas, overwritten.</param>
    private static ActivitySignal Activity(TagRanks tag, ProcessPoints pts, int from, long ops, double ioShare, double[] d)
    {
        // A delta exists only where the process has both points; its points are contiguous, so that is the window's deltas
        // [lo, hi], and the rest are 0. The best counter is kept as numbers, and one signal made of it.
        int lo = Math.Max(0, pts.First - from), hi = Math.Min(d.Length - 1, pts.Last - 1 - from);
        int best = -1, bestLag = 0;
        double bestR = double.NaN, bestLagged = double.NaN;
        for (int c = 0; c < Counters.Length; c++)
        {
            Array.Clear(d);
            for (int i = lo; i <= hi; i++)
            {
                ProcPoint a = pts[from + i], b = pts[from + i + 1];
                long v = c switch { 1 => b.OtherOps - a.OtherOps, 2 => b.ReadOps - a.ReadOps, _ => b.TotalOps - a.TotalOps };
                d[i] = Math.Max(0, v);
            }
            double r = tag.Spearman(d, 0);
            double lagged = double.NaN; int lagAt = 0;
            for (int lag = 1; lag <= MaxLag; lag++)
            {
                double rl = tag.Spearman(d, lag);
                if (!double.IsNaN(rl) && (double.IsNaN(lagged) || rl > lagged)) { lagged = rl; lagAt = lag; }
            }
            if (best < 0 || Better(BestOf(r, lagged), BestOf(bestR, bestLagged))) (best, bestR, bestLag, bestLagged) = (c, r, lagAt, lagged);
        }
        double rb = BestOf(bestR, bestLagged);
        bool present = !double.IsNaN(rb) && ops > 0 &&
                       ((rb >= MinSpearman && ioShare >= MinIoShare) || (rb >= 0.3 && ioShare >= DominantIoShare));
        return new ActivitySignal(Counters[best], bestR, bestLag, bestLagged, ioShare, ops, present);
    }

    /// <summary><see cref="ActivitySignal.Best"/> of these two correlations.</summary>
    private static double BestOf(double spearman, double lagged) =>
        double.IsNaN(lagged) ? spearman : double.IsNaN(spearman) ? lagged : Math.Max(spearman, lagged);

    private static bool Better(double a, double b) => !double.IsNaN(a) && (double.IsNaN(b) || a > b);

    /// <summary>
    /// Spearman of the tag's growth deltas against one process counter at each lag, as Stats.Spearman (lag 0) and
    /// Stats.SpearmanLagged compute it, without their allocations: the tag's series is ranked once per ranking (and its
    /// mean taken once), and each counter is ranked into reused buffers. A rank depends only on the values (ties share
    /// their mean rank), so sorting in place gives the same ranks, and Stats.Pearson the same result. A counter is 0 in most
    /// samples of a window (a short command is seen in one or two): its zeros, the lowest values, share one rank and only
    /// the rest are sorted, which gives the same ranks; a counter that is 0 throughout has no correlation (NaN), as Pearson
    /// finds for a constant series.
    /// </summary>
    internal sealed class TagRanks
    {
        private readonly double[]?[] _tag = new double[]?[MaxLag + 1];
        private readonly double[] _tagMean = new double[MaxLag + 1];
        private readonly double[]?[] _counter = new double[]?[MaxLag + 1];
        private readonly double[] _keys;
        private readonly int[] _order;

        public TagRanks(double[] tagDeltas)
        {
            int n = tagDeltas.Length;
            _keys = new double[n];
            _order = new int[n];
            for (int lag = 0; lag <= MaxLag && n - lag >= 4; lag++)
            {
                Rank(tagDeltas.AsSpan(lag), _tag[lag] = new double[n - lag]);
                _tagMean[lag] = _tag[lag]!.Average(); // as Stats.Pearson takes it
                _counter[lag] = new double[n - lag];
            }
        }

        /// <summary>
        /// The tag's deltas against <paramref name="d"/> (a counter's deltas, none below 0) lagged by
        /// <paramref name="lag"/> samples; NaN when too short or when <paramref name="d"/> is 0 throughout.
        /// </summary>
        public double Spearman(double[] d, int lag)
        {
            if (_tag[lag] is not { } rx) return double.NaN;
            var ry = _counter[lag]!;
            if (!RankCounter(d.AsSpan(0, ry.Length), ry)) return double.NaN;
            return Stats.Pearson(rx, _tagMean[lag], ry);
        }

        /// <summary>
        /// <see cref="Rank"/> of a counter's deltas (none below 0); false, with nothing ranked, when they are all 0 (each
        /// rank would be the same, and Pearson NaN: the ranks are halves, so their mean is exact and every deviation 0).
        /// </summary>
        private bool RankCounter(ReadOnlySpan<double> values, double[] ranks)
        {
            int n = values.Length, nonZero = 0;
            for (int i = 0; i < n; i++) if (values[i] != 0) nonZero++;
            if (nonZero == 0) return false;
            if (nonZero * 4 > n) { Rank(values, ranks); return true; }
            // The zeros are the first `zeros` values in sorted order: one block, its mean rank; the rest after them.
            int zeros = n - nonZero;
            double zeroRank = (0 + (zeros - 1)) / 2.0 + 1;
            var keys = _keys.AsSpan(0, nonZero);
            var order = _order.AsSpan(0, nonZero);
            int m = 0;
            for (int i = 0; i < n; i++)
            {
                if (values[i] == 0) ranks[i] = zeroRank;
                else { keys[m] = values[i]; order[m] = i; m++; }
            }
            keys.Sort(order);
            for (int k = 0; k < nonZero;)
            {
                int j = k;
                while (j + 1 < nonZero && keys[j + 1] == keys[k]) j++;
                double r = (zeros + k + (zeros + j)) / 2.0 + 1;
                for (int q = k; q <= j; q++) ranks[order[q]] = r;
                k = j + 1;
            }
            return true;
        }

        /// <summary>Stats.Ranks into <paramref name="ranks"/>: 1-based, ties sharing their mean rank.</summary>
        private void Rank(ReadOnlySpan<double> values, double[] ranks)
        {
            int n = values.Length;
            var keys = _keys.AsSpan(0, n);
            var order = _order.AsSpan(0, n);
            values.CopyTo(keys);
            for (int i = 0; i < n; i++) order[i] = i;
            keys.Sort(order);
            for (int k = 0; k < n;)
            {
                int j = k;
                while (j + 1 < n && keys[j + 1] == keys[k]) j++;
                double r = (k + j) / 2.0 + 1;
                for (int m = k; m <= j; m++) ranks[order[m]] = r;
                k = j + 1;
            }
        }
    }

    /// <summary>
    /// A process's start, exit and orphaning beside the growth's onset and end. Timed on the clock that only goes forward:
    /// the samples' <see cref="History.History.Ms"/> and the record's <see cref="ProcessRecord.StartMs"/>,
    /// <see cref="ProcessRecord.ExitMs"/> and <see cref="ProcessRecord.OrphanedMs"/>; the times of day are only said.
    /// </summary>
    private static LifecycleSignal Lifecycle(ProcessRecord? rec, History.History h, int from, int onset, int end, int to, double ioShare, bool active)
    {
        var notes = new List<string>();
        if (rec is null) return new LifecycleSignal(false, false, false, notes);
        long onsetMs = h.Ms[onset], endMs = h.Ms[end];
        // Tolerances scale with the sample interval so a 2 s CI run and a 10 s default both work.
        long interval = Math.Max(1000, (h.Ms[to] - h.Ms[from]) / Math.Max(1, to - from));
        long startSlack = Math.Max(180_000, interval * 6), nearSlack = Math.Max(30_000, interval * 3);

        bool started = active && rec.Key.CreateTime > 0 && rec.StartMs >= onsetMs - startSlack && rec.StartMs <= onsetMs + nearSlack;
        if (started) notes.Add($"started {Fmt.Clock(rec.StartUtc)}, {Fmt.Relative(TimeSpan.FromMilliseconds(rec.StartMs - onsetMs))} growth onset");

        // Only when the growth really ended inside the window (end < to): "still climbing" has no end to coincide with.
        bool ended = active && rec.ExitMs is { } x && end < to && Math.Abs(x - endMs) <= nearSlack;
        if (ended) notes.Add($"exited {Fmt.Clock(rec.ExitUtc!.Value)}{(rec.ExitCode is { } c ? $" (exit code {c})" : "")}, when growth stopped");

        // Orphans that keep working are top suspects (the find.exe case): alive and busy during the episode.
        bool aliveDuring = rec.OrphanedMs is { } o && o <= endMs && (rec.ExitMs is not { } exit || exit > onsetMs);
        bool orphanActive = aliveDuring && ioShare >= MinIoShare;
        if (rec.IsOrphan)
        {
            var running = TimeSpan.FromMilliseconds((rec.ExitMs ?? h.Ms[to]) - rec.StartMs);
            notes.Add($"orphaned{(rec.OrphanedUtc is { } ou ? " at " + Fmt.Clock(ou) : "")} (parent PID {rec.ParentPid} gone), running {Fmt.Duration(running)}" +
                      (orphanActive ? $" with {ioShare:P0} of all I/O" : ""));
        }
        return new LifecycleSignal(started, ended, orphanActive, notes);
    }

    private static void Explain(Suspect s, PoolTag tag, long growth, long totalHandleUp)
    {
        var d = s.Direct;
        if (d.HandleType is not null && d.HandleDelta != 0)
        {
            string est = growth > 0 && totalHandleUp > 0 && d.HandleDelta > 0 ? $", ~{Fmt.Bytes((long)(growth * d.HandleShare))} of the {tag.ToDisplay()} growth" : "";
            s.Evidence.Add($"direct charge: {d.HandleType} handles {d.HandlesBefore:N0} -> {d.HandlesAfter:N0} ({(d.HandleDelta > 0 ? "+" : "")}{d.HandleDelta:N0}, {d.HandleShare:P0} of the {d.HandleType}-handle growth{est})");
        }
        if (d.QuotaDelta != 0 && (d.Present || Math.Abs(d.QuotaDelta) >= 64 * 1024))
            s.Evidence.Add($"quota pool usage {Fmt.SignedBytes(d.QuotaDelta)} ({d.QuotaShare:P0} of all quota growth)");
        var a = s.Activity;
        if (a.OpsDelta > 0)
        {
            string r = double.IsNaN(a.Spearman) ? "n/a" : a.Spearman.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            string lag = double.IsNaN(a.LaggedSpearman) ? "" : $", lag {a.BestLag} {a.LaggedSpearman.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}";
            s.Evidence.Add($"{(a.Present ? "activity correlation" : "activity")}: {a.OpsDelta:N0} I/O ops in the episode ({a.IoShare:P0} of all), Spearman vs {a.Counter} ops {r}{lag}");
        }
        foreach (var note in s.Lifecycle.Notes) s.Evidence.Add("lifecycle: " + note);
        if (s.ViaWmi) s.Evidence.Add($"launched via WMI (WmiPrvSE.exe in its parent chain: the real requester is hidden); command line: {s.CommandLine ?? "unknown"}");
    }

    /// <summary>I/O operations between the process's first and last points within raw samples [a, b].</summary>
    private static long OpsDelta(ProcessPoints pts, int a, int b) =>
        Within(pts, a, b) is (int f, int l) ? pts[l].TotalOps - pts[f].TotalOps : 0;

    private static long QuotaDelta(ProcessPoints pts, int a, int b) =>
        Within(pts, a, b) is (int f, int l) ? (pts[l].QuotaPaged + pts[l].QuotaNonPaged) - (pts[f].QuotaPaged + pts[f].QuotaNonPaged) : 0;

    /// <summary>The indexes of the process's first and last points within [a, b] (its points are contiguous), or null.</summary>
    private static (int first, int last)? Within(ProcessPoints pts, int a, int b)
    {
        int first = Math.Max(a, pts.First), last = Math.Min(b, pts.Last);
        return first <= last ? (first, last) : null;
    }
}
