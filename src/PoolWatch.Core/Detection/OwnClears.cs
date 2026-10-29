using PoolWatch.Core.Cache;

namespace PoolWatch.Core.Detection;

/// <summary>
/// PoolWatch's own cache clears as the detector reads them against a history, so that it knows its own impact. A clear's
/// interval runs from the last raw sample before it to the first sample at or after the clear plus its settle time (the
/// sample its result is measured at, as the clearer measures it): whatever a series does in between is the clear's
/// result, never the pool's. Clears less than the settle time apart (the button pressed again while a clear
/// is measured) are one interval, measured once the last of them is. Only clears that ran (no error) count. Immutable;
/// <see cref="None"/> is the detector without clears, exactly as before.
/// </summary>
public sealed class OwnClears
{
    public static OwnClears None { get; } = new([], TimeSpan.Zero);

    private readonly ClearRecord[] _ran;

    /// <param name="clears">The clears PoolWatch holds (any order; those that failed are left out).</param>
    /// <param name="settle">How long after a clear its result is measured (the cache refills meanwhile).</param>
    public OwnClears(IEnumerable<ClearRecord> clears, TimeSpan settle)
    {
        _ran = clears.Where(c => c.Error is null).OrderBy(c => c.TimeUtc).ToArray();
        Settle = settle;
    }

    public TimeSpan Settle { get; }

    /// <summary>The clear still being measured at <paramref name="nowUtc"/> (it ran, and its settle time has not passed), or null.</summary>
    public ClearRecord? Measuring(DateTime nowUtc) => _ran.LastOrDefault(c => c.TimeUtc <= nowUtc && nowUtc < c.TimeUtc + Settle);

    /// <summary>
    /// The intervals of the clears that ran by <paramref name="h"/>'s newest sample, placed on its samples, oldest first,
    /// overlapping ones merged. One still being measured has <see cref="ClearSpan.Measured"/> = the sample count (not
    /// measured yet). An interval wholly before the first sample held is left out; one that began before it has Before -1.
    /// Each keeps the time its clear ran, for saying it (<see cref="ClearSpans.TimeOf"/>).
    /// </summary>
    public ClearSpans SpansIn(History.History h)
    {
        if (_ran.Length == 0 || h.Count == 0) return ClearSpans.None;
        var spans = new List<ClearSpan>();
        var times = new List<DateTime?>();
        var newest = h.Times[^1];
        foreach (var c in _ran)
        {
            if (c.TimeUtc > newest) break;      // not run yet at this history's time (the clock was set back since)
            var measuredUtc = c.TimeUtc + Settle;
            int measured = measuredUtc > newest ? h.Count : h.IndexAtOrAfter(measuredUtc);
            // All of it before the history: it holds no sample. Left out, so that a history no clear touches has no span and
            // every reader skips its work at once (Count 0), as without clears.
            if (measured == 0) continue;
            var span = new ClearSpan(h.IndexAtOrAfter(c.TimeUtc) - 1, measured);
            // A clear that ran inside the previous one's interval extends it: the earlier measurement lies in this one's
            // dip, so the two are one interval, measured once both are (and said as clears, with no one time).
            if (spans.Count > 0 && span.Before < spans[^1].Measured)
            {
                spans[^1] = spans[^1] with { Measured = Math.Max(spans[^1].Measured, span.Measured) };
                times[^1] = null;
            }
            else
            {
                spans.Add(span);
                times.Add(c.TimeUtc);
            }
        }
        return spans.Count == 0 ? ClearSpans.None : new ClearSpans([.. spans], [.. times]);
    }
}

/// <summary>One own clear (or several less than its settle time apart) on a history's samples.</summary>
/// <param name="Before">The last sample before the clear; -1 when the history starts after the clear ran.</param>
/// <param name="Measured">The sample its result is measured at: the level the clear left (the sample count while it is still measured).</param>
public readonly record struct ClearSpan(int Before, int Measured)
{
    /// <summary>The sample lies inside the clear's interval (after it ran, before its measurement).</summary>
    public bool Holds(int index) => index > Before && index < Measured;

    /// <summary>
    /// What the clear took from a series that was <paramref name="before"/> at <see cref="Before"/> and
    /// <paramref name="measured"/> at <see cref="Measured"/>: its fall, or 0 when it rose (the growth stays the series').
    /// </summary>
    public static long Drop(long before, long measured) => Math.Max(0, before - measured);
}

/// <summary>
/// The own clears on a history's samples (<see cref="OwnClears.SpansIn"/>), oldest first and never overlapping, and the
/// one baseline every change across them is measured on: the stitch. A change from a sample to a later one is the plain
/// change with the drop of each clear in between added back (<see cref="ClearSpan.Drop"/>); a sample inside an interval is
/// never a start, a minimum or a maximum, and a span that starts inside one starts at its measurement. So a clear's drop
/// is never a fall, its refill is never growth, growth after it counts from the level it left, and growth before it still
/// counts; a span with no clear in it is measured exactly as without clears. A clear can only be told apart from what the
/// series did meanwhile once it is measured: whatever the series did inside the interval is the clear's. So a series the
/// clear dropped has its own growth there taken as the clear's too: a window over the interval holds none of it, and a
/// rule over that window judges the rest of its minutes with its own bytes (a clear never lowers a rule's bar).
/// </summary>
public sealed class ClearSpans
{
    public static ClearSpans None { get; } = new([], []);

    private readonly ClearSpan[] _spans;
    private readonly DateTime?[] _times;

    /// <param name="spans">Oldest first and never overlapping (<see cref="OwnClears.SpansIn"/> builds them so).</param>
    /// <param name="times">When each span's clear ran; null for a span that merges several clears.</param>
    internal ClearSpans(ClearSpan[] spans, DateTime?[] times)
    {
        _spans = spans;
        _times = times;
    }

    public IReadOnlyList<ClearSpan> Spans => _spans;
    public int Count => _spans.Length;

    /// <summary>When the clear of a span ran; null for a span that merges several clears (or one not in this set).</summary>
    public DateTime? TimeOf(ClearSpan span)
    {
        int k = Array.IndexOf(_spans, span);
        return k < 0 ? null : _times[k];
    }

    /// <summary>Whether the sample lies inside a clear's interval.</summary>
    public bool Inside(int index)
    {
        for (int k = 0; k < _spans.Length; k++) if (_spans[k].Holds(index)) return true;
        return false;
    }

    /// <summary>Which of the samples [<paramref name="from"/>, <paramref name="to"/>) lie inside a clear's interval, as <see cref="Inside"/> says it, at once.</summary>
    public bool[] InsideFrom(int from, int to)
    {
        var inside = new bool[Math.Max(0, to - from)];
        for (int k = 0; k < _spans.Length; k++)
            for (int i = Math.Max(from, _spans[k].Before + 1); i < Math.Min(to, _spans[k].Measured); i++) inside[i - from] = true;
        return inside;
    }

    /// <summary>Where a change over a span that starts at <paramref name="index"/> is measured from: there, or at the measurement of the clear whose interval holds it.</summary>
    public int Start(int index)
    {
        for (int k = 0; k < _spans.Length; k++) if (_spans[k].Holds(index)) return _spans[k].Measured;
        return index;
    }

    /// <summary>
    /// Stitches <paramref name="series"/> (the values of the samples from <paramref name="from"/> on) in place: from each
    /// clear's measurement on, the clear's drop is added back (<paramref name="addBack"/>), so the difference of two values
    /// outside every interval is the change between them with no clear's drop in it. A clear whose last sample before it
    /// is not in the series adds nothing (every span from inside the series starts after it). With
    /// <paramref name="fill"/>, the samples inside an interval take the level just before it (the first after it, when the
    /// series starts inside one), so that a run of values can be read across it; otherwise they are left to be skipped.
    /// <paramref name="dropped"/>, when given, receives each span whose drop of this series was added back (it fell across
    /// it): for a series it dropped, what the series did inside the interval is the clear's, not the series' growth.
    /// </summary>
    public void Stitch(long[] series, int from, bool addBack = true, bool fill = false, List<ClearSpan>? dropped = null)
    {
        if (_spans.Length == 0) return;
        int last = from + series.Length - 1;
        long offset = 0;
        int next = from;
        foreach (var s in _spans)
        {
            if (s.Measured <= from || s.Before >= last) continue; // none of the series' samples is inside it
            int inside = Math.Max(from, s.Before + 1), after = Math.Min(last + 1, s.Measured);
            for (; next < inside; next++) series[next - from] += offset;
            long was = offset;
            long? level = s.Before >= from ? series[s.Before - from] : null; // already stitched
            if (s.Measured <= last)
            {
                long measured = series[s.Measured - from];
                if (addBack && s.Before >= from)
                {
                    long drop = ClearSpan.Drop(series[s.Before - from] - was, measured);
                    offset += drop;
                    if (drop > 0) dropped?.Add(s);
                }
                // Only a series that starts inside this span has no level before it; no earlier span reached the series
                // then, so nothing was added back yet: the measured value is its level.
                level ??= measured;
            }
            for (; next < after; next++) series[next - from] = fill && level is { } l ? l : series[next - from] + was;
        }
        for (; next <= last; next++) series[next - from] += offset;
    }

    /// <summary>
    /// A tag's allocation and free rates over the raw samples [<paramref name="from"/>, <paramref name="to"/>] with each
    /// interval left out (the frees a clear makes are its own), as <see cref="History.History.TagRatesBetween"/> gives them
    /// over each part; exactly that over the whole span when no interval is in it.
    /// </summary>
    public TagRates Rates(History.History h, PoolTag tag, int from, int to)
    {
        bool crossed = false;
        for (int k = 0; k < _spans.Length; k++) crossed |= _spans[k].Measured > from && _spans[k].Before < to;
        if (!crossed) return h.TagRatesBetween(tag, from, to);
        double allocs = 0, frees = 0, minutes = 0;
        int start = from;
        foreach (var s in _spans)
        {
            if (s.Measured <= start) continue;
            if (s.Before >= to) break;
            Part(start, s.Before);
            start = s.Measured;
        }
        Part(start, to);
        if (minutes <= 0) return new TagRates(0, 0, AllocShape.Idle);
        double a = allocs / minutes, f = frees / minutes;
        return new TagRates(a, f, LeakShapes.Classify(a, f));

        // One part, as TagRatesBetween reads it (from the first sample that stored the tag; 32-bit differences).
        void Part(int i, int j)
        {
            if (i < 0 || j >= h.Count || j <= i || (i = h.FirstStoredIndex(tag, i, j - 1)) < 0) return;
            var x = h.TagCountsAt(tag, i);
            var y = h.TagCountsAt(tag, j);
            if (x == (0u, 0u) || y == (0u, 0u)) return;
            allocs += unchecked(y.allocs - x.allocs);
            frees += unchecked(y.frees - x.frees);
            minutes += (h.Times[j] - h.Times[i]).TotalMinutes;
        }
    }
}
