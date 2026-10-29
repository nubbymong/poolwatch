using PoolWatch.Core.Cache;
using PoolWatch.Core.Lag;
using PoolWatch.Core.Model;

namespace PoolWatch.Core.Growth;

/// <summary>
/// A series over a window, in bytes: what PoolWatch's own cache clears freed of it (<see cref="Cleared"/>), and its change
/// with those clears added back, the detector's stitched baseline, which is either growth (<see cref="Grew"/>) or a fall
/// on its own (<see cref="Fell"/>), never both. So <see cref="Net"/> = Grew - Cleared - Fell is its real level change
/// while PoolWatch measured it, and a series that only wobbles shows neither growth nor a fall.
/// </summary>
public readonly record struct GrowthCounts(long Grew, long Cleared, long Fell)
{
    /// <summary>The real level change: what grew, less what PoolWatch's own clears freed and what fell on its own.</summary>
    public long Net => Grew - Cleared - Fell;

    /// <summary>The change with PoolWatch's own clears added back (Grew - Fell): what the series would have done without them.</summary>
    public long Change => Grew - Fell;

    /// <summary>The counts of a window whose stitched change is <paramref name="change"/> and whose clears freed <paramref name="cleared"/>.</summary>
    public static GrowthCounts Of(long change, long cleared) => change >= 0 ? new(change, cleared, 0) : new(0, cleared, -change);
}

/// <summary>
/// One window of the ledger (<see cref="GrowthLedger.Window"/>): the kernel pool's counts and each tracked tag's, how
/// many of PoolWatch's own clears ran in it, and how long PoolWatch measured it (sleep and gaps left out).
/// </summary>
public sealed record GrowthWindow
{
    /// <summary>The rate per hour is said only once this much is measured: a few minutes say nothing about an hour.</summary>
    public static readonly TimeSpan RateAfter = TimeSpan.FromMinutes(30);

    /// <summary>The window asked for (the last hour, 6 hours, 24 hours).</summary>
    public required TimeSpan Span { get; init; }
    /// <summary>The newest sample's time: the window ends there.</summary>
    public required DateTime NowUtc { get; init; }
    /// <summary>The time PoolWatch sampled in the window, a sleep or other gap between two samples left out.</summary>
    public TimeSpan Measured { get; init; }
    /// <summary>The kernel pool total (paged and non-paged, committed).</summary>
    public GrowthCounts Total { get; init; }
    /// <summary>PoolWatch's own clears that ran in the window and were measured.</summary>
    public int Clears { get; init; }
    /// <summary>When the one clear ran, when the window holds exactly one; else null.</summary>
    public DateTime? OneClearUtc { get; init; }
    /// <summary>When the ledger's first sample was (PoolWatch started); null before any.</summary>
    public DateTime? StartedUtc { get; init; }
    /// <summary>Each tracked tag's counts in the window (a tag not tracked, too small to matter, is not in it).</summary>
    public IReadOnlyDictionary<PoolTag, GrowthCounts> Tags { get; init; } = new Dictionary<PoolTag, GrowthCounts>();

    public DateTime FromUtc => NowUtc - Span;
    /// <summary>Anything was measured in the window.</summary>
    public bool HasData => Measured > TimeSpan.Zero;
    /// <summary>Enough is measured for a rate per hour (<see cref="RateAfter"/>).</summary>
    public bool RateKnown => Measured >= RateAfter;
    /// <summary>The window is measured all through (up to a few minutes short, the edges of its minutes).</summary>
    public bool Whole => Measured >= Span - TimeSpan.FromMinutes(3);
    /// <summary>PoolWatch started inside the window (so the part before it was never measured).</summary>
    public bool StartedInside => StartedUtc is { } s && s > FromUtc + TimeSpan.FromMinutes(2);

    /// <summary>
    /// The change with PoolWatch's own clears added back (<see cref="GrowthCounts.Change"/>) per hour measured, a fall
    /// negative; null while <see cref="RateKnown"/> is false. Times the hours measured, less what the clears freed, it is
    /// the net change.
    /// </summary>
    public long? PerHour(GrowthCounts c) => RateKnown ? (long)(c.Change / Measured.TotalHours) : null;
}

/// <summary>
/// The kernel pool's growth and PoolWatch's own clears, kept as per-minute counts for 25 hours, so the last hour, 6 hours
/// and 24 hours can be told without the raw samples (kept for 70 minutes). Each change between two samples is counted
/// once, on the detector's baseline across PoolWatch's own clears (<see cref="Detection.ClearSpans"/>): a clear's interval
/// (the last sample before it to the sample its result is measured at, clears less than the settle time apart being one)
/// is never a fall, its drop is the clear's ("Cleared"); every other change, a rise across a clear's interval included, is
/// the series' own. A minute keeps the sum of those own changes, signed, so a window adds them up before saying whether
/// the series grew or fell (<see cref="GrowthCounts.Of"/>): a pool that wobbles up and down between samples shows no
/// growth and no fall, whatever the sampling interval. A change PoolWatch did not measure is not counted at all: across a
/// sleep or other gap (<see cref="ResumeGap.SuspendGap"/> past the interval) or a clock set back, the next sample starts
/// again from where the series is then (a clear left open across a sleep still has its drop, as the detector says). A
/// clock set back also drops the minutes that now lie wholly ahead of it (the one it is set back into is kept), so a
/// window counts at most the step's length twice, and a clear already counted is never counted again when the clock
/// passes it a second time. The total and each tag of at least
/// <see cref="TrackFromBytes"/> (at most <see cref="MaxTags"/>) are counted from the sample they are first seen there. A
/// tag's minute is kept only once its change reaches <see cref="StoreFromKb"/> (a tag that wobbles by a few kilobytes is
/// rarely kept, its change carried until then), so a tag's counts over any window are true to that much. Not
/// thread-safe: the sampler writes, readers hold the same lock (the history's).
/// </summary>
public sealed class GrowthLedger
{
    /// <summary>How long the minutes are kept: the 24-hour window and its first minute.</summary>
    public static readonly TimeSpan Keep = TimeSpan.FromHours(25);
    /// <summary>A tag is counted from the first sample it holds this much (the rows that matter grow by megabytes).</summary>
    public const long TrackFromBytes = 4L << 20;
    /// <summary>The most tags counted at once (a PC has a few dozen this large).</summary>
    public const int MaxTags = 256;
    /// <summary>A tag's change is kept in a minute once it reaches this many kilobytes; until then it is carried.</summary>
    public const int StoreFromKb = 64;

    /// <summary>Each slot keeps two counts: its own change (signed) and what PoolWatch's clears freed of it.</summary>
    private const int Parts = 2;

    private readonly TimeSpan _settle, _gap;

    // The tracked tags, by slot. A slot is freed once its tag is small again and no kept minute holds it.
    private readonly Dictionary<PoolTag, int> _slots = new();
    private readonly PoolTag[] _tags = new PoolTag[MaxTags];
    private readonly bool[] _used = new bool[MaxTags];
    private readonly Stack<int> _free = new(Enumerable.Range(0, MaxTags).Reverse());
    /// <summary>Each tag's level at the last sample outside a clear's interval, and at this sample.</summary>
    private readonly long[] _last = new long[MaxTags], _now = new long[MaxTags];
    /// <summary>The minute each slot last counted something in.</summary>
    private readonly DateTime[] _lastActive = new DateTime[MaxTags];
    // A minute keeps kilobytes: what is not kept yet (the rounding, and a change under StoreFromKb) is carried in these
    // running totals, so it is kept in a later minute and nothing is lost.
    private readonly long[] _sumBytes = new long[Parts * MaxTags], _sumKb = new long[Parts * MaxTags];
    // The minute being filled: each slot's bytes, and the slots it touched.
    private readonly long[] _open = new long[Parts * MaxTags];
    private readonly List<int> _touched = [];
    private readonly bool[] _isTouched = new bool[MaxTags];
    private Minute? _openMinute;

    private readonly List<Minute> _minutes = [];
    private bool _started;
    private DateTime _lastSampleUtc, _lastOutsideUtc;
    private long _totalLast;
    /// <summary>A gap (a sleep, a stall) lies between the last sample outside a clear's interval and this one.</summary>
    private bool _unmeasured;
    /// <summary>Clears dated after a time the clock was set back to: counted (or passed) already, never counted again.</summary>
    private readonly HashSet<DateTime> _passed = new();

    /// <param name="settle">How long after a clear its result is measured (as the clearer measures it).</param>
    /// <param name="interval">The sampling interval: two samples further apart than it and a sleep's gap are a gap.</param>
    public GrowthLedger(TimeSpan settle, TimeSpan interval)
    {
        _settle = settle;
        _gap = interval + ResumeGap.SuspendGap;
    }

    /// <summary>When the first sample was (PoolWatch started); null before it.</summary>
    public DateTime? StartedUtc { get; private set; }

    /// <summary>The minutes kept (the cost report).</summary>
    public int MinuteCount => _minutes.Count + (_openMinute is null ? 0 : 1);

    /// <summary>The tags counted now.</summary>
    public int TagCount => _slots.Count;

    /// <summary>The kept minutes' tag entries (the cost report).</summary>
    public long TagEntries => _minutes.Sum(m => (long)m.Slots.Length);

    /// <summary>Clears remembered as passed after the clock was set back (tests: let go once the clock passes them again).</summary>
    internal int PassedClears => _passed.Count;

    /// <summary>About what the ledger holds, in bytes: its fixed tables and every kept minute.</summary>
    public long ApproxBytes()
    {
        const long fixedTables = MaxTags * (8 + 1 + 8 + 8 + 8 + 3 * Parts * 8 + 1) + 4096;
        // A minute: the object and its fields, and its two arrays when it has entries (a slot and two counts each).
        return fixedTables + 32L * _minutes.Capacity + _minutes.Sum(m => 88 + (m.Slots.Length == 0 ? 0 : 48 + 4L * (1 + Parts) * m.Slots.Length));
    }

    /// <summary>
    /// One sample (the sampler, in sample order): the pool total, the tags, and PoolWatch's clears as the clearer holds
    /// them now (a failed one is left out, as the detector leaves it out).
    /// </summary>
    public void Observe(DateTime timeUtc, long total, IReadOnlyList<PoolTagEntry> tags, IReadOnlyList<ClearRecord> clears)
    {
        if (!_started)
        {
            _started = true;
            StartedUtc = timeUtc;
            _lastSampleUtc = _lastOutsideUtc = timeUtc;
            _openMinute = new Minute(MinuteOf(timeUtc));
            _totalLast = total;
            ReadLevels(tags, timeUtc);
            return;
        }
        var dt = timeUtc - _lastSampleUtc;
        if (dt < TimeSpan.Zero)
        {
            ClockSetBack(timeUtc, total, tags, clears);
            return;
        }
        _lastSampleUtc = timeUtc;
        Roll(timeUtc);
        var open = _openMinute!;
        if (dt > _gap) _unmeasured = true;
        else open.Measured += dt;

        // PoolWatch's own clears since the last sample outside an interval: inside one until the last of them is measured.
        int count = 0;
        DateTime? first = null, measuredUtc = null;
        for (int i = 0; i < clears.Count; i++) // by index: nothing is allocated per sample
        {
            var c = clears[i];
            if (c.Error is not null || c.TimeUtc <= _lastOutsideUtc || c.TimeUtc > timeUtc || _passed.Count > 0 && _passed.Contains(c.TimeUtc)) continue;
            count++;
            if (first is null || c.TimeUtc < first) first = c.TimeUtc;
            var m = c.TimeUtc + _settle;
            if (measuredUtc is null || m > measuredUtc) measuredUtc = m;
        }
        if (count > 0 && timeUtc < measuredUtc) return; // the clear is still being measured: its interval is its own

        // Across a clear's interval a drop is the clear's; every other change is the series' own, unless it was not measured.
        bool across = count > 0, unmeasured = _unmeasured;
        _unmeasured = false;
        var (change, cleared) = Split(total - _totalLast, across, unmeasured);
        open.Change += change;
        open.Cleared += cleared;
        _totalLast = total;
        ReadLevels(tags, timeUtc);
        for (int k = 0; k < MaxTags; k++)
        {
            if (!_used[k]) continue;
            long d = _now[k] - _last[k];
            _last[k] = _now[k];
            if (d == 0) continue;
            var split = Split(d, across, unmeasured);
            if (!_isTouched[k]) { _isTouched[k] = true; _touched.Add(k); }
            _open[Parts * k] += split.change;
            _open[Parts * k + 1] += split.cleared;
        }
        _lastOutsideUtc = timeUtc;
        if (across)
        {
            open.OneClearUtc = first; // read only when the window holds this one clear
            open.Clears += count;
        }
    }

    /// <summary>A change as the series' own (signed) and what a clear freed: a drop across a clear's interval is the clear's.</summary>
    private static (long change, long cleared) Split(long d, bool acrossClear, bool unmeasured) =>
        acrossClear && d < 0 ? (0, -d) : unmeasured ? (0, 0) : (d, 0);

    /// <summary>
    /// The clock went back (a correction, a time zone set wrongly and put right): nothing is measured across the step, the
    /// minutes after the one the new time is in (now wholly ahead of the clock) are dropped, and every clear dated after
    /// the new time has been counted or passed already, so it is never counted again. The minute the new time is in keeps
    /// what it counted (at most the step's length is counted twice); when it was already closed, counting goes on in a new
    /// minute of the same time. Counting goes on from here. A clear still being measured (its 2 minutes not up) when the
    /// clock is set back loses its drop, which is then counted nowhere: neither Cleared nor the clear count has it.
    /// </summary>
    private void ClockSetBack(DateTime timeUtc, long total, IReadOnlyList<PoolTagEntry> tags, IReadOnlyList<ClearRecord> clears)
    {
        for (int i = 0; i < clears.Count; i++)
            if (clears[i].TimeUtc > timeUtc) _passed.Add(clears[i].TimeUtc);
        var minute = MinuteOf(timeUtc);
        if (_openMinute!.Utc > minute)
        {
            // The minute being filled lies wholly ahead of the clock now.
            foreach (int k in _touched)
            {
                _isTouched[k] = false;
                for (int j = 0; j < Parts; j++) _open[Parts * k + j] = 0;
            }
            _touched.Clear();
            int keep = FirstAtOrAfter(minute.AddMinutes(1));
            if (keep < _minutes.Count) _minutes.RemoveRange(keep, _minutes.Count - keep);
            _openMinute = new Minute(minute);
        }
        // No slot is held by a minute that is no longer kept.
        for (int k = 0; k < MaxTags; k++)
            if (_lastActive[k] > minute) _lastActive[k] = minute;
        if (StartedUtc > timeUtc) StartedUtc = timeUtc;
        _lastSampleUtc = _lastOutsideUtc = timeUtc;
        _totalLast = total;
        ReadLevels(tags, timeUtc);
        for (int k = 0; k < MaxTags; k++) _last[k] = _now[k];
    }

    /// <summary>This sample's level of every tracked tag (0 when it is gone), and newly tracked tags (their level is where they start).</summary>
    private void ReadLevels(IReadOnlyList<PoolTagEntry> tags, DateTime timeUtc)
    {
        for (int k = 0; k < MaxTags; k++) _now[k] = 0;
        for (int i = 0; i < tags.Count; i++)
        {
            var t = tags[i];
            long bytes = (long)Math.Min(t.TotalUsed, long.MaxValue);
            if (_slots.TryGetValue(t.Tag, out int k)) _now[k] = bytes;
            else if (bytes >= TrackFromBytes && _free.Count > 0)
            {
                k = _free.Pop();
                _slots[t.Tag] = k;
                _tags[k] = t.Tag;
                _used[k] = true;
                _now[k] = _last[k] = bytes; // counted from here
                _lastActive[k] = MinuteOf(timeUtc);
                for (int j = Parts * k; j < Parts * k + Parts; j++) _sumBytes[j] = _sumKb[j] = 0;
            }
        }
    }

    /// <summary>Starts a new minute when the sample is in a later one, keeping 25 hours.</summary>
    private void Roll(DateTime timeUtc)
    {
        var minute = MinuteOf(timeUtc);
        if (_openMinute is null || minute <= _openMinute.Utc) return;
        Close(_openMinute);
        _openMinute = new Minute(minute);
        var cutoff = timeUtc - Keep;
        int drop = 0;
        while (drop < _minutes.Count && _minutes[drop].Utc < cutoff) drop++;
        if (drop > 0) _minutes.RemoveRange(0, drop);
        // A clear the clock has passed again is left out by its time from here on.
        if (_passed.Count > 0) _passed.RemoveWhere(t => t <= _lastOutsideUtc);
        // A slot whose tag is small again and that no kept minute holds is free for another tag.
        var oldest = _minutes.Count > 0 ? _minutes[0].Utc : minute;
        for (int k = 0; k < MaxTags; k++)
            if (_used[k] && _now[k] < TrackFromBytes && _lastActive[k] < oldest)
            {
                _used[k] = false;
                _slots.Remove(_tags[k]);
                _free.Push(k);
            }
    }

    /// <summary>
    /// A finished minute: each touched slot's change in kilobytes, kept once either of its counts reaches
    /// <see cref="StoreFromKb"/>; the rest is carried in the running totals to a later minute.
    /// </summary>
    private void Close(Minute m)
    {
        _touched.Sort();
        List<int>? slots = null, kb = null;
        Span<long> part = stackalloc long[Parts];
        foreach (int k in _touched)
        {
            _isTouched[k] = false;
            long most = 0;
            for (int j = 0; j < Parts; j++)
            {
                int i = Parts * k + j;
                _sumBytes[i] += _open[i];
                _open[i] = 0;
                // Whole kilobytes, rounded toward zero, so what is carried is under a kilobyte either way.
                part[j] = Math.Clamp(_sumBytes[i] / 1024 - _sumKb[i], int.MinValue, int.MaxValue);
                most = Math.Max(most, Math.Abs(part[j]));
            }
            if (most < StoreFromKb) continue;
            (slots ??= []).Add(k);
            kb ??= [];
            for (int j = 0; j < Parts; j++)
            {
                _sumKb[Parts * k + j] += part[j];
                kb.Add((int)part[j]);
            }
            _lastActive[k] = m.Utc;
        }
        _touched.Clear();
        if (slots is not null)
        {
            m.Slots = [.. slots];
            m.Kb = [.. kb!];
        }
        _minutes.Add(m);
    }

    /// <summary>
    /// The last <paramref name="span"/> up to the newest sample: the minutes from the one it starts in, each count's own
    /// changes added up before they are said as growth or a fall (<see cref="GrowthCounts.Of"/>).
    /// </summary>
    public GrowthWindow Window(TimeSpan span)
    {
        var now = _lastSampleUtc;
        if (!_started) return new GrowthWindow { Span = span, NowUtc = now };
        var from = MinuteOf(now - span);
        var sums = new long[Parts * MaxTags];
        var measured = TimeSpan.Zero;
        long change = 0, cleared = 0;
        int clears = 0;
        DateTime? one = null;
        void Add(Minute m)
        {
            measured += m.Measured;
            change += m.Change;
            cleared += m.Cleared;
            if (m.Clears == 0) return;
            one = clears == 0 && m.Clears == 1 ? m.OneClearUtc : null;
            clears += m.Clears;
        }
        int at = FirstAtOrAfter(from);
        for (int i = at; i < _minutes.Count; i++)
        {
            var m = _minutes[i];
            Add(m);
            for (int e = 0; e < m.Slots.Length; e++)
                for (int j = 0; j < Parts; j++) sums[Parts * m.Slots[e] + j] += 1024L * m.Kb[Parts * e + j];
        }
        if (_openMinute is { } open && open.Utc >= from)
        {
            Add(open);
            foreach (int k in _touched)
                for (int j = 0; j < Parts; j++) sums[Parts * k + j] += _open[Parts * k + j];
        }
        // Every tag counted now (one that did not change in the window has nothing in it).
        var tags = new Dictionary<PoolTag, GrowthCounts>();
        for (int k = 0; k < MaxTags; k++)
            if (_used[k]) tags[_tags[k]] = GrowthCounts.Of(sums[Parts * k], sums[Parts * k + 1]);
        return new GrowthWindow
        {
            Span = span, NowUtc = now, Measured = measured > span ? span : measured, Total = GrowthCounts.Of(change, cleared), Clears = clears,
            OneClearUtc = clears == 1 ? one : null, StartedUtc = StartedUtc, Tags = tags,
        };
    }

    private int FirstAtOrAfter(DateTime minute)
    {
        int lo = 0, hi = _minutes.Count;
        while (lo < hi) { int mid = (lo + hi) / 2; if (_minutes[mid].Utc < minute) lo = mid + 1; else hi = mid; }
        return lo;
    }

    private static DateTime MinuteOf(DateTime utc) => new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);

    /// <summary>One minute's counts: the total's exactly, each tag's in kilobytes (a slot and its two counts per entry).</summary>
    private sealed class Minute(DateTime utc)
    {
        public DateTime Utc { get; } = utc;
        public TimeSpan Measured { get; set; }
        /// <summary>The total's own change in the minute, signed (a rise across a clear's interval included).</summary>
        public long Change { get; set; }
        public long Cleared { get; set; }
        public int Clears { get; set; }
        public DateTime? OneClearUtc { get; set; }
        public int[] Slots { get; set; } = [];
        public int[] Kb { get; set; } = [];
    }
}
