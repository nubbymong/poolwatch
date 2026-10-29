using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;

namespace PoolWatch.Core.Detection;

public enum TagState { Normal, Growing, Retained, Released }

public enum AlertKind { Growth, NonPagedTotal, Release }

/// <summary>
/// Per-tag rolling view: size, signed change over 1 / 10 / 60 minutes and since the start of history. The changes over
/// 1 / 10 / 60 minutes leave out what PoolWatch's own cache clears did (<see cref="ClearSpans"/>): a clear's drop is its
/// result, not the tag falling, growth after it counts from the level it left, and growth before it still counts.
/// </summary>
public sealed record TagStatus(PoolTag Tag, long Bytes, long PagedBytes, long NonPagedBytes, long Delta1m, long Delta10m, long Delta60m,
    long DeltaSinceStart, TagState State, DateTime? StateSinceUtc)
{
    /// <summary>Allocations / frees per minute over the last 10 minutes (an own clear's interval left out) and their shape (leak vs churn).</summary>
    public TagRates Rates { get; init; }
}

/// <summary>A detection result for one evaluation. Growth alerts repeat while the rule holds; the incident
/// manager de-duplicates them into one open incident per tag.</summary>
public sealed record Alert(AlertKind Kind, PoolTag Tag, DateTime TimeUtc, string Rule, long Bytes, TimeSpan Window, DateTime OnsetUtc);

/// <summary>
/// A retained tag that PoolWatch's own cache clear took under its floor (<see cref="Detector.ClearedRetentions"/>): the clear
/// freed <paramref name="FreedBytes"/> of it, at <paramref name="ClearAtUtc"/> (null for clears merged into one interval).
/// </summary>
public readonly record struct ClearedRetention(PoolTag Tag, long FreedBytes, DateTime? ClearAtUtc);

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
    /// <summary>The tags the last evaluation read as released (a release found before a clear stays across it).</summary>
    private readonly HashSet<PoolTag> _wasReleased = new();

    public Detector(Settings settings) => _settings = settings;

    public IReadOnlyDictionary<PoolTag, TagStatus> Status => _status;

    /// <summary>
    /// The last evaluation held everything as it was (<see cref="Status"/> unchanged, no alert), because PoolWatch's own
    /// cache clear was still being measured: until then nothing can be told about a change across it.
    /// </summary>
    public bool Holding { get; private set; }

    /// <summary>The clears the last evaluation read (<see cref="OwnClears.None"/> before the first or without any).</summary>
    public OwnClears Clears { get; private set; } = OwnClears.None;

    private readonly List<ClearedRetention> _clearedRetentions = [];

    /// <summary>
    /// The retained tags the last evaluation stopped counting as retained because PoolWatch's own clear measured then took
    /// them under their floor: what they held was the cache's, and the incident manager says so. Empty at any other time.
    /// </summary>
    public IReadOnlyList<ClearedRetention> ClearedRetentions => _clearedRetentions;

    /// <summary>Marks a tag as retained (set by the incident manager, which knows when the activity stopped).</summary>
    /// <param name="floorBytes">
    /// Below this the tag no longer counts as retained: it has given back most of its rise, on its own or because PoolWatch's
    /// own clear freed it (<see cref="ClearedRetentions"/>).
    /// </param>
    public void MarkRetained(PoolTag tag, DateTime sinceUtc, long floorBytes = 0) => _retained[tag] = (sinceUtc, floorBytes);

    /// <summary>The evaluation without PoolWatch's own clears (<see cref="OwnClears.None"/>).</summary>
    public List<Alert> Evaluate(History.History h) => Evaluate(h, OwnClears.None);

    /// <summary>
    /// One evaluation. PoolWatch's own cache clears (<paramref name="clears"/>) are its own impact, not the pool's:
    /// <list type="bullet">
    /// <item>while one is being measured, nothing is raised or changed (<see cref="Holding"/>);</item>
    /// <item>everything else is measured on one baseline, the stitch (<see cref="ClearSpans"/>): a tag's changes, its growing
    /// and released states, its release alert and rates, the growth rules and the non-paged total's rise all add each
    /// clear's drop back and skip the samples inside its interval (the dip and the refill it measured). So the drop is never
    /// a release, growth after the clear counts from the level it left, and growth before it still counts.</item>
    /// <item>every window is the clock's, judged with the rule's own bytes. What a series did inside a clear's interval is
    /// the clear's: for a series the clear dropped, its growth there is not counted, so a window that holds the interval
    /// finds as much as the same series holding still through the clear's 2 minutes would. A clear never lowers a rule's
    /// bar, and never raises a growth finding that curve holding still would not (but for a series' own release inside
    /// those 2 minutes, which is credited to the clear). A leak just over a rule that the clears keep trimming can go
    /// unfound while its window holds a clear (DESIGN.md section 6, the limits): at the default 10-second sampling up to
    /// about 4% over the hour rule with hourly clears and about 8% with clears every 30 minutes. A clear's interval is its
    /// 2 minutes rounded up to the sample grid, so a longer sampling interval widens that band.</item>
    /// <item>a release is never read across a clear: the stitch takes a tag's own growth inside a clear's interval as the
    /// clear's, so a fall before the clear and one after it would add up past what the tag fell. Where the release window
    /// holds a clear's interval, a release is raised only on a fall since the last such clear's measurement, and a tag
    /// released before the clear stays released while its window's fall on the stitch still holds the rule's bytes.</item>
    /// </list>
    /// With no clear in a window, every result is exactly what it was without clears.
    /// </summary>
    public List<Alert> Evaluate(History.History h, OwnClears clears)
    {
        var alerts = new List<Alert>();
        int n = h.Count;
        Clears = clears;
        Holding = false;
        _clearedRetentions.Clear();
        if (n < 2) return alerts;
        var now = h.Times[n - 1];
        if (clears.Measuring(now) is not null)
        {
            Holding = true;
            return alerts;
        }
        int i1 = h.IndexAtOrAfter(now.AddMinutes(-1)), i10 = h.IndexAtOrAfter(now.AddMinutes(-10)), i60 = h.IndexAtOrAfter(now.AddMinutes(-60));
        var ruleStarts = _settings.GrowthRules.Select(r => (rule: r, from: h.IndexAtOrAfter(now.AddMinutes(-r.WindowMinutes)))).ToList();
        int iRel = h.IndexAtOrAfter(now.AddMinutes(-_settings.ReleaseWindowMinutes));
        int minFrom = Math.Min(i60, Math.Min(iRel, ruleStarts.Count > 0 ? ruleStarts.Min(r => r.from) : i60));
        var spans = clears.SpansIn(h);
        // Where each change to now is measured from: its span's start, or the measurement of the clear whose interval holds it.
        int f1 = spans.Start(i1), f10 = spans.Start(i10), f60 = spans.Start(i60), fRel = spans.Start(iRel);
        // The samples inside a clear's interval, from minFrom: never a start, a minimum or a maximum.
        var inClear = spans.InsideFrom(minFrom, n);
        // The clear measured at this sample, if any: its drop is seen now.
        ClearSpan? measuredNow = null;
        // The release window's last own clear: a release is raised only on a fall since its measurement (relSince).
        int relSince = fRel;
        for (int k = 0; k < spans.Count; k++)
        {
            if (spans.Spans[k] is { Before: >= 0 } s && s.Measured == n - 1) measuredNow = s;
            if (spans.Spans[k].Measured > fRel) relSince = Math.Max(relSince, spans.Spans[k].Measured);
        }
        _wasReleased.Clear();
        foreach (var (t, st) in _status) if (st.State == TagState.Released) _wasReleased.Add(t);

        _status.Clear();
        foreach (var tag in h.KnownTags)
        {
            var (paged, nonPaged) = h.TagAt(tag, n - 1);
            long cur = paged + nonPaged;
            var (p0, np0) = h.TagAt(tag, minFrom);
            // Cheap pre-filter: most of the ~3000 tags are tiny and flat.
            bool significant = cur >= 256 * 1024 || p0 + np0 >= 256 * 1024 || BuiltinTags.AlwaysTracked.Contains(tag);
            if (!significant) continue;
            // The tag's values with each own clear's drop added back: the difference of two of them (outside the intervals)
            // is the change between them with no clear's drop in it. Unchanged with no clear in the window.
            var series = h.TagSeries(tag, minFrom);
            spans.Stitch(series, minFrom);
            long At(int idx) => series[Math.Clamp(idx - minFrom, 0, series.Length - 1)];
            long latest = series[^1];
            var (pf, npf) = h.TagAt(tag, 0);
            long first = pf + npf;
            long d1 = latest - At(f1), d10 = latest - At(f10), d60 = latest - At(f60);

            var state = TagState.Normal;
            DateTime? since = null;
            // Growth rules: current value minus the window minimum (catches ongoing climbs with small dips), the samples
            // inside a clear's interval left out.
            foreach (var (rule, from) in ruleStarts)
            {
                if (from >= n - 1) continue;
                long min = long.MaxValue; int minIdx = from;
                // "<=": the latest sample at the minimum, i.e. the last flat point before the climb.
                for (int i = from; i < n - 1; i++)
                {
                    if (inClear[i - minFrom]) continue;
                    long v = At(i);
                    if (v <= min) { min = v; minIdx = i; }
                }
                if (min == long.MaxValue) continue; // the whole window was the clear's
                long growth = latest - min;
                if (growth > rule.Bytes)
                {
                    alerts.Add(new Alert(AlertKind.Growth, tag, now, FormattableString.Invariant($"grew > {Fmt.Bytes(rule.Bytes)} in {rule.WindowMinutes:0.#} min"), growth,
                        TimeSpan.FromMinutes(rule.WindowMinutes), h.Times[minIdx]));
                }
            }
            // The rate over the 10-minute window, as for the same curve without a clear (a few seconds after a clear's
            // measurement say nothing about a rate).
            if (d10 * 10.0 / Math.Max(0.1, Math.Min(10, (now - h.Times[i10]).TotalMinutes)) >= _settings.GrowingDisplayBytesPer10Min)
            {
                state = TagState.Growing;
            }
            // Release: a sharp drop from the window maximum.
            if (fRel < n - 1)
            {
                long max = long.MinValue; int maxIdx = fRel;
                for (int i = fRel; i < n - 1; i++)
                {
                    if (inClear[i - minFrom]) continue;
                    long v = At(i);
                    if (v > max) { max = v; maxIdx = i; }
                }
                long drop = max - latest;
                bool released = drop >= _settings.ReleaseBytes, raise = released;
                if (released && relSince > fRel)
                {
                    // The window holds a clear's interval: only a fall since the last one's measurement raises a release
                    // (its samples hold no clear, so their fall is the tag's own); a tag released before it stays released.
                    long top = long.MinValue;
                    for (int i = relSince; i < n - 1; i++) top = Math.Max(top, At(i));
                    raise = top != long.MinValue && top - latest >= _settings.ReleaseBytes;
                    released = raise || _wasReleased.Contains(tag);
                }
                if (released)
                {
                    state = TagState.Released;
                    since = h.Times[maxIdx];
                    if (raise && (!_lastRelease.TryGetValue(tag, out var last) || now - last > TimeSpan.FromMinutes(_settings.ReleaseWindowMinutes)))
                    {
                        _lastRelease[tag] = now;
                        alerts.Add(new Alert(AlertKind.Release, tag, now, $"dropped {Fmt.Bytes(drop)} within {_settings.ReleaseWindowMinutes:0.#} min", drop,
                            TimeSpan.FromMinutes(_settings.ReleaseWindowMinutes), h.Times[maxIdx]));
                    }
                }
            }
            if (_retained.TryGetValue(tag, out var ret) && cur < ret.floor)
            {
                // Under its floor: it gave back most of its rise. Where a clear is measured now, that clear took it there (it held
                // its floor at the sample before the clear, or it would have ended there): that memory was the cache's. The state
                // ends, and the clear's result is said (ClearedRetentions).
                _retained.Remove(tag);
                if (measuredNow is { } c) _clearedRetentions.Add(new ClearedRetention(tag, ClearSpan.Drop(Level(h, tag, c.Before), cur), spans.TimeOf(c)));
            }
            else if (state == TagState.Normal && _retained.TryGetValue(tag, out var r)) { state = TagState.Retained; since = r.since; }
            if (state == TagState.Released) _retained.Remove(tag);
            _status[tag] = new TagStatus(tag, cur, paged, nonPaged, d1, d10, d60, cur - first, state, since) { Rates = spans.Rates(h, tag, f10, n - 1) };
        }

        // The rise over 10 minutes on the same stitch: from their start (or the measurement of the clear whose interval
        // holds it), each clear's drop in between added back.
        long RiseOf(long[] series)
        {
            spans.Stitch(series, i10);
            return series[^1] - series[f10 - i10];
        }

        // Total non-paged pool above the threshold and really rising (not noise on a plateau): attribute to the
        // fastest-growing non-paged tag, if one carries a fair part of the rise.
        var totals = h.Totals[n - 1];
        var total = new long[n - i10];
        for (int i = i10; i < n; i++) total[i - i10] = (long)h.Totals[i].CommittedNonPaged;
        long rise = RiseOf(total);
        if ((long)totals.CommittedNonPaged > _settings.NonPagedTotalAlertBytes && rise >= _settings.NonPagedTotalRiseBytesPer10Min)
        {
            PoolTag? best = null; long bestGrowth = 0;
            foreach (var tag in h.KnownTags)
            {
                var np = h.TagSeries(tag, i10, nonPagedOnly: true);
                if (np.Length < 2) continue;
                long g = RiseOf(np);
                if (g > bestGrowth) { bestGrowth = g; best = tag; }
            }
            if (best is { } b && bestGrowth >= rise / 4)
                alerts.Add(new Alert(AlertKind.NonPagedTotal, b, now, $"non-paged pool {Fmt.Bytes((long)totals.CommittedNonPaged)} > {Fmt.Bytes(_settings.NonPagedTotalAlertBytes)} and rising ({Fmt.SignedBytes(rise)} in 10 min)",
                    bestGrowth, TimeSpan.FromMinutes(10), h.Times[i10]));
        }
        return alerts;
    }

    private static long Level(History.History h, PoolTag tag, int index)
    {
        var (p, np) = h.TagAt(tag, index);
        return p + np;
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

    /// <summary>
    /// What PoolWatch's own cache clears freed of something, said as their result: "PoolWatch's own cache clear at 06:20:01
    /// freed 413.3 MB" for one clear (its time, local; to the minute without <paramref name="seconds"/>), "PoolWatch's own
    /// cache clears freed 413.3 MB" for several.
    /// </summary>
    public static string OwnClearsFreed(long bytes, DateTime? oneAtUtc, bool seconds = true) => oneAtUtc is { } t
        ? $"PoolWatch's own cache clear at {(seconds ? Clock(t) : t.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture))} freed {Bytes(bytes)}"
        : $"PoolWatch's own cache clears freed {Bytes(bytes)}";

    /// <summary>Local wall-clock time of a UTC instant (reports are read by the owner, in local time).</summary>
    public static string Clock(DateTime utc) => utc.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    public static string Stamp(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>"20 s before" / "5 s after" for an offset relative to a reference instant.</summary>
    public static string Relative(TimeSpan offset) =>
        Duration(offset.Duration()) + (offset < TimeSpan.Zero ? " before" : " after");

    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min" : $"{(int)t.TotalSeconds} s";
}
