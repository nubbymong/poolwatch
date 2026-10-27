using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;

namespace PoolWatch.Core.Detection;

public enum TagState { Normal, Growing, Retained, Released }

public enum AlertKind { Growth, NonPagedTotal, Release }

/// <summary>Per-tag rolling view: size, signed change over 1 / 10 / 60 minutes and since the start of history.</summary>
public sealed record TagStatus(PoolTag Tag, long Bytes, long PagedBytes, long NonPagedBytes, long Delta1m, long Delta10m, long Delta60m,
    long DeltaSinceStart, TagState State, DateTime? StateSinceUtc)
{
    /// <summary>Allocations / frees per minute over the last 10 minutes and their shape (leak vs churn).</summary>
    public TagRates Rates { get; init; }
}

/// <summary>A detection result for one evaluation. Growth alerts repeat while the rule holds; the incident
/// manager de-duplicates them into one open incident per tag.</summary>
public sealed record Alert(AlertKind Kind, PoolTag Tag, DateTime TimeUtc, string Rule, long Bytes, TimeSpan Window, DateTime OnsetUtc);

/// <summary>Onset / end of the latest growth episode in a series.</summary>
public readonly record struct ChangePoints(int OnsetIndex, int PeakIndex, int? EndIndex, long Rise);

/// <summary>
/// Growth, release and state detection on the raw history (DESIGN.md section 6). Call <see cref="Evaluate"/> after each
/// sample while holding the history lock.
/// </summary>
public sealed class Detector
{
    private readonly Settings _settings;
    private readonly Dictionary<PoolTag, TagStatus> _status = new();
    private readonly Dictionary<PoolTag, DateTime> _lastRelease = new();
    private readonly Dictionary<PoolTag, (DateTime since, long floor)> _retained = new();

    public Detector(Settings settings) => _settings = settings;

    public IReadOnlyDictionary<PoolTag, TagStatus> Status => _status;

    /// <summary>Marks a tag as retained (set by the incident manager, which knows when the activity stopped).</summary>
    /// <param name="floorBytes">Below this the tag no longer counts as retained (it has given back most of its rise).</param>
    public void MarkRetained(PoolTag tag, DateTime sinceUtc, long floorBytes = 0) => _retained[tag] = (sinceUtc, floorBytes);

    public List<Alert> Evaluate(History.History h)
    {
        var alerts = new List<Alert>();
        int n = h.Count;
        if (n < 2) return alerts;
        var now = h.Times[n - 1];
        int i1 = h.IndexAtOrAfter(now.AddMinutes(-1)), i10 = h.IndexAtOrAfter(now.AddMinutes(-10)), i60 = h.IndexAtOrAfter(now.AddMinutes(-60));
        var ruleStarts = _settings.GrowthRules.Select(r => (rule: r, from: h.IndexAtOrAfter(now.AddMinutes(-r.WindowMinutes)))).ToList();
        int iRel = h.IndexAtOrAfter(now.AddMinutes(-_settings.ReleaseWindowMinutes));
        int minFrom = Math.Min(i60, Math.Min(iRel, ruleStarts.Count > 0 ? ruleStarts.Min(r => r.from) : i60));

        _status.Clear();
        foreach (var tag in h.KnownTags)
        {
            var (paged, nonPaged) = h.TagAt(tag, n - 1);
            long cur = paged + nonPaged;
            var (p0, np0) = h.TagAt(tag, minFrom);
            // Cheap pre-filter: most of the ~3000 tags are tiny and flat.
            bool significant = cur >= 256 * 1024 || p0 + np0 >= 256 * 1024 || BuiltinTags.AlwaysTracked.Contains(tag);
            if (!significant) continue;
            var series = h.TagSeries(tag, minFrom);
            long At(int idx) => series[Math.Clamp(idx - minFrom, 0, series.Length - 1)];
            var (pf, npf) = h.TagAt(tag, 0);
            long first = pf + npf;
            long d1 = cur - At(i1), d10 = cur - At(i10), d60 = cur - At(i60);

            var state = TagState.Normal;
            DateTime? since = null;
            // Growth rules: current value minus the window minimum (catches ongoing climbs with small dips).
            foreach (var (rule, from) in ruleStarts)
            {
                if (from >= n - 1) continue;
                long min = long.MaxValue; int minIdx = from;
                // "<=": the latest sample at the minimum, i.e. the last flat point before the climb.
                for (int i = from; i < n - 1; i++) { long v = At(i); if (v <= min) { min = v; minIdx = i; } }
                long growth = cur - min;
                if (growth > rule.Bytes)
                {
                    alerts.Add(new Alert(AlertKind.Growth, tag, now, $"grew > {Fmt.Bytes(rule.Bytes)} in {rule.WindowMinutes:0.#} min", growth,
                        TimeSpan.FromMinutes(rule.WindowMinutes), h.Times[minIdx]));
                }
            }
            if (d10 * 10.0 / Math.Max(0.1, Math.Min(10, (now - h.Times[i10]).TotalMinutes)) >= _settings.GrowingDisplayBytesPer10Min)
            {
                state = TagState.Growing;
            }
            // Release: a sharp drop from the window maximum.
            if (iRel < n - 1)
            {
                long max = long.MinValue; int maxIdx = iRel;
                for (int i = iRel; i < n - 1; i++) { long v = At(i); if (v > max) { max = v; maxIdx = i; } }
                long drop = max - cur;
                if (drop >= _settings.ReleaseBytes)
                {
                    state = TagState.Released;
                    since = h.Times[maxIdx];
                    if (!_lastRelease.TryGetValue(tag, out var last) || now - last > TimeSpan.FromMinutes(_settings.ReleaseWindowMinutes))
                    {
                        _lastRelease[tag] = now;
                        alerts.Add(new Alert(AlertKind.Release, tag, now, $"dropped {Fmt.Bytes(drop)} within {_settings.ReleaseWindowMinutes:0.#} min", drop,
                            TimeSpan.FromMinutes(_settings.ReleaseWindowMinutes), h.Times[maxIdx]));
                    }
                }
            }
            if (_retained.TryGetValue(tag, out var ret) && cur < ret.floor) _retained.Remove(tag); // declined slowly: no longer retained
            else if (state == TagState.Normal && _retained.TryGetValue(tag, out var r)) { state = TagState.Retained; since = r.since; }
            if (state == TagState.Released) _retained.Remove(tag);
            _status[tag] = new TagStatus(tag, cur, paged, nonPaged, d1, d10, d60, cur - first, state, since) { Rates = h.TagRatesBetween(tag, i10, n - 1) };
        }

        // Total non-paged pool above the threshold and really rising (not noise on a plateau): attribute to the
        // fastest-growing non-paged tag, if one carries a fair part of the rise.
        var totals = h.Totals[n - 1];
        long rise = (long)totals.CommittedNonPaged - (long)h.Totals[i10].CommittedNonPaged;
        if ((long)totals.CommittedNonPaged > _settings.NonPagedTotalAlertBytes && rise >= _settings.NonPagedTotalRiseBytesPer10Min)
        {
            PoolTag? best = null; long bestGrowth = 0;
            foreach (var tag in h.KnownTags)
            {
                var np = h.TagSeries(tag, i10, nonPagedOnly: true);
                if (np.Length < 2) continue;
                long g = np[^1] - np[0];
                if (g > bestGrowth) { bestGrowth = g; best = tag; }
            }
            if (best is { } b && bestGrowth >= rise / 4)
                alerts.Add(new Alert(AlertKind.NonPagedTotal, b, now, $"non-paged pool {Fmt.Bytes((long)totals.CommittedNonPaged)} > {Fmt.Bytes(_settings.NonPagedTotalAlertBytes)} and rising ({Fmt.SignedBytes(rise)} in 10 min)",
                    bestGrowth, TimeSpan.FromMinutes(10), h.Times[i10]));
        }
        return alerts;
    }

    /// <summary>
    /// Finds the latest growth episode: onset where the series leaves its pre-peak minimum (first point above
    /// min + 5% of the rise), end where it reaches 95% of the rise and then stays flat (null while still climbing).
    /// </summary>
    public static ChangePoints FindChangePoints(IReadOnlyList<long> s, int flatSamples = 2)
    {
        int n = s.Count;
        if (n == 0) return new ChangePoints(0, 0, null, 0);
        int peak = 0;
        for (int i = 0; i < n; i++) if (s[i] >= s[peak]) peak = i;
        int minIdx = 0;
        for (int i = 0; i <= peak; i++) if (s[i] < s[minIdx]) minIdx = i;
        long rise = s[peak] - s[minIdx];
        if (rise <= 0) return new ChangePoints(peak, peak, peak, 0);
        int onset = minIdx;
        for (int i = minIdx; i <= peak; i++) { if (s[i] > s[minIdx] + rise / 20) { onset = Math.Max(minIdx, i - 1); break; } }
        int reach = peak;
        for (int i = onset; i <= peak; i++) { if (s[i] >= s[minIdx] + rise * 95 / 100) { reach = i; break; } }
        // Ended if, after reaching 95%, the series never exceeds the 95% level by more than 5% of the rise
        // and enough samples have passed.
        bool flat = n - 1 - reach >= flatSamples;
        for (int i = reach + 1; i < n && flat; i++) if (s[i] > s[reach] + rise / 20) flat = false;
        return new ChangePoints(onset, peak, flat ? reach : null, rise);
    }

    /// <summary>
    /// The tag view's growth figure: the last value minus the lowest value up to the onset (the pre-growth floor); if that
    /// is negative (the tag fell since), the plain change over the window. Total for any input (C5): an empty series is 0
    /// and an onset outside the series is clamped, so a refresh can never index past the end.
    /// </summary>
    public static long GrowthSinceOnset(IReadOnlyList<long> series, int onsetIndex)
    {
        if (series.Count == 0) return 0;
        int upTo = Math.Clamp(onsetIndex, 0, series.Count - 1);
        long floor = series[0];
        for (int i = 1; i <= upTo; i++) if (series[i] < floor) floor = series[i];
        long growth = series[^1] - floor;
        return growth < 0 ? series[^1] - series[0] : growth;
    }
}

public static class Fmt
{
    public static string Bytes(long b)
    {
        double a = Math.Abs(b);
        return a >= 1L << 30 ? FormattableString.Invariant($"{b / (double)(1L << 30):0.0#} GB")
            : a >= 1L << 20 ? FormattableString.Invariant($"{b / (double)(1L << 20):0.#} MB")
            : a >= 1024 ? FormattableString.Invariant($"{b / 1024.0:0} KB") : FormattableString.Invariant($"{b} B");
    }

    public static string SignedBytes(long b) => (b > 0 ? "+" : "") + Bytes(b);

    /// <summary>Local wall-clock time of a UTC instant (reports are read by the owner, in local time).</summary>
    public static string Clock(DateTime utc) => utc.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    public static string Stamp(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>"20 s before" / "5 s after" for an offset relative to a reference instant.</summary>
    public static string Relative(TimeSpan offset) =>
        Duration(offset.Duration()) + (offset < TimeSpan.Zero ? " before" : " after");

    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min" : $"{(int)t.TotalSeconds} s";
}
