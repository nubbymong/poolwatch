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
    public DateTime? ExitUtc { get; init; }
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
public sealed record CorrelationWindow(DateTime FromUtc, DateTime OnsetUtc, DateTime? EndUtc, DateTime ToUtc);

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
        int from = h.IndexAtOrAfter(w.FromUtc);
        int to = Math.Min(h.Count - 1, Math.Max(from, h.IndexAtOrAfter(w.ToUtc.AddTicks(1)) - 1));
        if (h.Count < 3 || to - from < 2) return [];
        int onset = Math.Clamp(h.IndexAtOrAfter(w.OnsetUtc), from, to);
        int end = w.EndUtc is { } e ? Math.Clamp(h.IndexAtOrAfter(e), onset, to) : to;
        if (end <= onset) end = Math.Min(to, onset + 1);

        var series = h.TagSeries(tag, from);
        int n = to - from + 1;
        var tagDeltas = new double[n - 1];
        for (int i = 0; i < n - 1; i++) tagDeltas[i] = series[i + 1] - series[i];
        long growth = series[end - from] - series[onset - from];

        // Direct-charge inputs: handle type(s) for the tag and the handle scans around the episode.
        BuiltinTags.HandleTypes.TryGetValue(tag, out var handleTypes);
        string? handleType = handleTypes?.FirstOrDefault();
        var scanBefore = h.HandlesAtOrBefore(h.Times[onset]);
        var scanAfter = h.HandlesAtOrBefore(h.Times[end]) is { } sa && scanBefore is not null && sa.TimeUtc > scanBefore.TimeUtc ? sa : h.LatestHandles;
        bool haveScans = handleType is not null && scanBefore is not null && scanAfter is not null && scanAfter.TimeUtc > scanBefore.TimeUtc;

        var keys = h.ProcessesSince(from);
        var rows = new List<(ProcessKey key, ProcPoint?[] pts, long ops, long quota, int hBefore, int hAfter)>();
        long totalOps = 0, totalQuotaUp = 0, totalHandleUp = 0;
        foreach (var key in keys)
        {
            var pts = h.ProcessSeries(key, from);
            long ops = OpsDelta(pts, onset - from, end - from);
            long quota = QuotaDelta(pts, onset - from, end - from);
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
        foreach (var (key, pts, ops, quota, hb, ha) in rows)
        {
            var rec = registry.Get(key);
            if (key.Pid == 0) continue; // Idle
            double ioShare = totalOps > 0 ? Math.Max(0, ops) / (double)totalOps : 0;
            double hShare = totalHandleUp > 0 ? Math.Max(0, ha - hb) / (double)totalHandleUp : 0;
            double qShare = totalQuotaUp > 0 ? Math.Max(0, quota) / (double)totalQuotaUp : 0;
            bool directHandles = haveScans && growth > 0 && ha - hb >= MinHandleDelta && hShare >= MinHandleShare;
            // Quota alone counts only when it is large in absolute terms (it is charged for many object kinds).
            bool directQuota = growth > 0 && quota >= Math.Max(1L << 20, growth / 10) && qShare >= 0.5;
            var direct = new DirectSignal(haveScans ? handleType : null, hb, ha, hShare, quota, qShare, directHandles || directQuota);

            var activity = Activity(tagDeltas, pts, ops, ioShare);
            // Start / exit timing only means something for a process that did something in the episode.
            bool active = ioShare >= 0.01 || ha - hb >= MinHandleDelta || quota >= 64 * 1024;
            var lifecycle = Lifecycle(rec, h, from, onset, end, to, ioShare, active);

            if (!direct.Present && !lifecycle.Present && ops <= 0 && ha - hb <= 0) continue;

            var chain = registry.Ancestry(key);
            bool viaWmi = chain.Take(chain.Count - 1).Any(a => a.Image.Equals("WmiPrvSE.exe", StringComparison.OrdinalIgnoreCase));
            var s = new Suspect
            {
                Key = key,
                Image = rec?.Image ?? "unknown process",
                // 2d: never "?": a process PoolWatch no longer holds is named by when it ran (from its history points).
                DisplayName = rec?.DisplayName ?? Incidents.TracedProcess.UnknownName(key.CreateTime > 0 ? key.CreateTimeUtc : null, LastSeen(h, pts, from)),
                CommandLine = rec?.CommandLine,
                ImagePath = rec?.ImagePath,
                User = rec?.User,
                StartUtc = key.CreateTimeUtc,
                ExitUtc = rec?.ExitUtc,
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
    private static DateTime? LastSeen(History.History h, ProcPoint?[] pts, int from)
    {
        for (int i = pts.Length - 1; i >= 0; i--)
            if (pts[i] is not null) return from + i >= h.Count - 1 ? null : h.Times[from + i];
        return null;
    }

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

    private static ActivitySignal Activity(double[] tagDeltas, ProcPoint?[] pts, long ops, double ioShare)
    {
        ActivitySignal? best = null;
        foreach (var counter in new[] { "total", "other", "read" })
        {
            var d = new double[tagDeltas.Length];
            for (int i = 0; i < d.Length; i++)
            {
                if (pts[i] is not { } a || pts[i + 1] is not { } b) continue;
                long v = counter switch { "other" => b.OtherOps - a.OtherOps, "read" => b.ReadOps - a.ReadOps, _ => b.TotalOps - a.TotalOps };
                d[i] = Math.Max(0, v);
            }
            double r = Stats.Spearman(tagDeltas, d);
            double bestLagged = double.NaN; int bestLag = 0;
            for (int lag = 1; lag <= MaxLag; lag++)
            {
                double rl = Stats.SpearmanLagged(tagDeltas, d, lag);
                if (!double.IsNaN(rl) && (double.IsNaN(bestLagged) || rl > bestLagged)) { bestLagged = rl; bestLag = lag; }
            }
            var cand = new ActivitySignal(counter, r, bestLag, bestLagged, ioShare, ops, false);
            if (best is null || Better(cand.Best, best.Best)) best = cand;
        }
        double rb = best!.Best;
        bool present = !double.IsNaN(rb) && ops > 0 &&
                       ((rb >= MinSpearman && ioShare >= MinIoShare) || (rb >= 0.3 && ioShare >= DominantIoShare));
        return best with { Present = present };
    }

    private static bool Better(double a, double b) => !double.IsNaN(a) && (double.IsNaN(b) || a > b);

    private static LifecycleSignal Lifecycle(ProcessRecord? rec, History.History h, int from, int onset, int end, int to, double ioShare, bool active)
    {
        var notes = new List<string>();
        if (rec is null) return new LifecycleSignal(false, false, false, notes);
        DateTime onsetT = h.Times[onset], endT = h.Times[end];
        // Tolerances scale with the sample interval so a 2 s CI run and a 10 s default both work.
        var interval = TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerSecond, (h.Times[to] - h.Times[from]).Ticks / Math.Max(1, to - from)));
        var startSlack = TimeSpan.FromTicks(Math.Max(TimeSpan.FromMinutes(3).Ticks, interval.Ticks * 6));
        var nearSlack = TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(30).Ticks, interval.Ticks * 3));

        bool started = active && rec.Key.CreateTime > 0 && rec.StartUtc >= onsetT - startSlack && rec.StartUtc <= onsetT + nearSlack;
        if (started) notes.Add($"started {Fmt.Clock(rec.StartUtc)}, {Fmt.Relative(rec.StartUtc - onsetT)} growth onset");

        // Only when the growth really ended inside the window (end < to): "still climbing" has no end to coincide with.
        bool ended = active && rec.ExitUtc is { } x && end < to && Math.Abs((x - endT).Ticks) <= nearSlack.Ticks;
        if (ended) notes.Add($"exited {Fmt.Clock(rec.ExitUtc!.Value)}{(rec.ExitCode is { } c ? $" (exit code {c})" : "")}, when growth stopped");

        // Orphans that keep working are top suspects (the find.exe case): alive and busy during the episode.
        bool aliveDuring = rec.OrphanedUtc is { } o && o <= endT && (rec.ExitUtc is null || rec.ExitUtc > onsetT);
        bool orphanActive = aliveDuring && ioShare >= MinIoShare;
        if (rec.IsOrphan)
        {
            var running = (rec.ExitUtc ?? h.Times[to]) - rec.StartUtc;
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

    private static long OpsDelta(ProcPoint?[] pts, int a, int b)
    {
        var first = FirstAtOrAfter(pts, a, b);
        var last = LastAtOrBefore(pts, b, a);
        return first is { } f && last is { } l ? l.TotalOps - f.TotalOps : 0;
    }

    private static long QuotaDelta(ProcPoint?[] pts, int a, int b)
    {
        var first = FirstAtOrAfter(pts, a, b);
        var last = LastAtOrBefore(pts, b, a);
        return first is { } f && last is { } l ? (l.QuotaPaged + l.QuotaNonPaged) - (f.QuotaPaged + f.QuotaNonPaged) : 0;
    }

    private static ProcPoint? FirstAtOrAfter(ProcPoint?[] pts, int a, int limit)
    {
        for (int i = Math.Max(0, a); i <= limit && i < pts.Length; i++) if (pts[i] is { } p) return p;
        return null;
    }

    private static ProcPoint? LastAtOrBefore(ProcPoint?[] pts, int b, int limit)
    {
        for (int i = Math.Min(b, pts.Length - 1); i >= limit && i >= 0; i--) if (pts[i] is { } p) return p;
        return null;
    }
}
