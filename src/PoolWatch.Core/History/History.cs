using PoolWatch.Core.Detection;
using PoolWatch.Core.Model;

namespace PoolWatch.Core.History;

/// <summary>Per-process counters at one sample (compact; the full ProcessSample is not retained).</summary>
public readonly record struct ProcPoint(int Handles, long ReadOps, long WriteOps, long OtherOps, long QuotaPaged, long QuotaNonPaged)
{
    public long TotalOps => ReadOps + WriteOps + OtherOps;
}

/// <summary>
/// One process's raw points, read in place by raw sample index (<see cref="History.Points"/>): no copy, valid under the
/// history lock until the next sample. A process's points are contiguous, from <see cref="First"/> to <see cref="Last"/>.
/// </summary>
public readonly struct ProcessPoints
{
    private readonly List<ProcPoint>? _points;
    private readonly int _first;

    internal ProcessPoints(List<ProcPoint> points, int first)
    {
        _points = points;
        _first = first;
    }

    /// <summary>The raw sample index of the first point.</summary>
    public int First => _first;
    /// <summary>The raw sample index of the last point (below <see cref="First"/> when the process has none).</summary>
    public int Last => _points is null ? _first - 1 : _first + _points.Count - 1;
    public bool Has(int index) => index >= First && index <= Last;
    public ProcPoint this[int index] => _points![index - _first];
}

/// <summary>
/// Handles by type name per process instance at one handle scan. Keyed by (PID, create time) so a reused PID
/// is never compared with the process that owned it earlier.
/// </summary>
public sealed record TypedHandles(DateTime TimeUtc, IReadOnlyDictionary<ProcessKey, Dictionary<string, int>> ByProcess)
{
    /// <summary>Handles of <paramref name="type"/> held by the process; "*" counts all types.</summary>
    public int Count(ProcessKey key, string type)
    {
        if (!ByProcess.TryGetValue(key, out var d)) return 0;
        return type == "*" ? d.Values.Sum() : d.GetValueOrDefault(type);
    }

    public bool Has(ProcessKey key) => ByProcess.ContainsKey(key);
}

/// <summary>
/// One minute of downsampled history: average tag usage for significant tags, and the I/O of each process that did any
/// in the minute (what the since-boot report splits each hour's growth by).
/// </summary>
public sealed class MinutePoint
{
    public DateTime TimeUtc { get; init; }
    public PoolTotals Totals { get; init; }
    public SystemCounters System { get; init; }
    public required PoolTag[] Tags { get; init; }
    public required long[] TagBytes { get; init; }
    public required ProcessKey[] Processes { get; init; }
    public required long[] ProcessOps { get; init; }
}

/// <summary>
/// In-memory history. Raw samples (tag usage in KB per tag index, per-process counters) are kept for
/// <see cref="RawSpan"/> (capped by sample count); one-minute averages of significant tags are kept for
/// <see cref="MinuteSpan"/> so the since-boot / 24-hour views work without re-reading disk.
/// Not thread-safe: the sampler thread writes; readers take <see cref="SyncRoot"/>.
/// </summary>
public sealed class History
{
    public object SyncRoot { get; } = new();
    public TimeSpan RawSpan { get; }
    public TimeSpan MinuteSpan { get; }
    public int MaxRawSamples { get; }
    public long MinuteMinBytes { get; set; } = 256 * 1024;

    private readonly Dictionary<PoolTag, int> _tagIndex = new();
    private readonly List<PoolTag> _tags = new();
    private readonly List<DateTime> _times = new();
    private readonly List<PoolTotals> _totals = new();
    private readonly List<SystemCounters> _system = new();
    /// <summary>
    /// One raw sample of tag usage, sparse: only tags of at least <see cref="SparseMinKb"/> (or always-tracked ones), sorted
    /// by tag index. A PC has ~3,000 tags, most of them a few KB and flat; storing all of them every sample made the raw
    /// history grow ~7 MB/h until it filled. A tag under the threshold reads as 0 (an error of at most 16 KB).
    /// </summary>
    private sealed record TagRow(int[] Idx, int[] PagedKb, int[] NonPagedKb, uint[] Allocs, uint[] Frees)
    {
        public (uint allocs, uint frees) Counts(int idx)
        {
            int i = Array.BinarySearch(Idx, idx);
            return i >= 0 ? (Allocs[i], Frees[i]) : (0u, 0u);
        }

        public (int paged, int nonPaged) Get(int idx)
        {
            int i = Array.BinarySearch(Idx, idx);
            return i >= 0 ? (PagedKb[i], NonPagedKb[i]) : (0, 0);
        }
    }

    public const int SparseMinKb = 16;
    private readonly List<TagRow> _tagRows = new();
    private long _firstSeq; // sequence number of _times[0]
    private readonly Dictionary<ProcessKey, ProcTrack> _procs = new();
    private readonly List<TypedHandles> _handles = new();
    private readonly List<MinutePoint> _minutes = new();
    private DateTime _minuteStart = DateTime.MinValue;
    private int _minuteFromIndex;

    private sealed class ProcTrack
    {
        public long FirstSeq;
        public readonly List<ProcPoint> Points = new();
        public long LastSeq => FirstSeq + Points.Count - 1;
    }

    public History(TimeSpan? rawSpan = null, TimeSpan? minuteSpan = null, int maxRawSamples = 900)
    {
        RawSpan = rawSpan ?? TimeSpan.FromMinutes(70);
        MinuteSpan = minuteSpan ?? TimeSpan.FromHours(25);
        MaxRawSamples = maxRawSamples;
    }

    /// <summary>A history whose raw cap covers 70 minutes at the given interval (so the 60-minute rule sees 60 minutes), up to 2,200 samples.</summary>
    /// <param name="rawMinutes">Raw history kept (70: the 60-minute rule and the hour of correlation need 60).</param>
    public static History ForInterval(double sampleSeconds, double rawMinutes = 70) =>
        new(rawSpan: TimeSpan.FromMinutes(rawMinutes), maxRawSamples: (int)Math.Clamp(Math.Ceiling(rawMinutes * 60 / Math.Max(1, sampleSeconds)) + 10, 100, 2200));

    public DateTime FirstTime { get; private set; } = DateTime.MinValue;
    public int Count => _times.Count;
    public IReadOnlyList<DateTime> Times => _times;
    public IReadOnlyList<PoolTotals> Totals => _totals;
    public IReadOnlyList<SystemCounters> System => _system;
    public IReadOnlyList<PoolTag> KnownTags => _tags;
    public IReadOnlyList<MinutePoint> Minutes => _minutes;
    public IReadOnlyList<TypedHandles> HandleScans => _handles;
    /// <summary>Process instances with raw points held (for the cost report).</summary>
    public int ProcessTrackCount => _procs.Count;
    public long ProcessPointCount => _procs.Values.Sum(t => (long)t.Points.Count);

    public void Add(Sample s)
    {
        if (FirstTime == DateTime.MinValue) FirstTime = s.TimeUtc;
        foreach (var t in s.Tags)
            if (!_tagIndex.ContainsKey(t.Tag)) { _tagIndex[t.Tag] = _tags.Count; _tags.Add(t.Tag); }
        var kept = new List<(int idx, int p, int n, uint a, uint f)>();
        foreach (var t in s.Tags)
        {
            int p = ToKb(t.PagedUsed), n = ToKb(t.NonPagedUsed);
            if (p + (long)n >= SparseMinKb || Tags.BuiltinTags.AlwaysTracked.Contains(t.Tag))
                kept.Add((_tagIndex[t.Tag], p, n, unchecked(t.PagedAllocs + t.NonPagedAllocs), unchecked(t.PagedFrees + t.NonPagedFrees)));
        }
        kept.Sort((a, b) => a.idx.CompareTo(b.idx));
        var row = new TagRow(new int[kept.Count], new int[kept.Count], new int[kept.Count], new uint[kept.Count], new uint[kept.Count]);
        for (int k = 0; k < kept.Count; k++)
        {
            row.Idx[k] = kept[k].idx; row.PagedKb[k] = kept[k].p; row.NonPagedKb[k] = kept[k].n; row.Allocs[k] = kept[k].a; row.Frees[k] = kept[k].f;
        }
        long seq = _firstSeq + _times.Count;
        _times.Add(s.TimeUtc);
        _totals.Add(s.Totals);
        _system.Add(s.System);
        _tagRows.Add(row);
        foreach (var p in s.Processes)
        {
            if (!_procs.TryGetValue(p.Key, out var track)) _procs[p.Key] = track = new ProcTrack { FirstSeq = seq };
            if (track.LastSeq != seq - 1 && track.Points.Count > 0)
            {
                // Gap (should not happen for a live process): pad with the last value so indexes stay aligned.
                var last = track.Points[^1];
                while (track.LastSeq < seq - 1) track.Points.Add(last);
            }
            track.Points.Add(new ProcPoint(p.HandleCount, p.ReadOperationCount, p.WriteOperationCount, p.OtherOperationCount,
                (long)p.QuotaPagedPoolUsage, (long)p.QuotaNonPagedPoolUsage));
        }
        RollMinute(s.TimeUtc);
        Trim(s.TimeUtc);
    }

    public void AddHandles(TypedHandles h)
    {
        _handles.Add(h);
        var cutoff = h.TimeUtc - RawSpan;
        int drop = 0;
        while (drop < _handles.Count - 1 && _handles[drop].TimeUtc < cutoff) drop++;
        if (drop > 0) _handles.RemoveRange(0, drop);
        if (_handles.Count > 200) _handles.RemoveRange(0, _handles.Count - 200);
    }

    /// <summary>Index of the first raw sample at or after <paramref name="t"/>.</summary>
    public int IndexAtOrAfter(DateTime t)
    {
        int lo = 0, hi = _times.Count;
        while (lo < hi) { int mid = (lo + hi) / 2; if (_times[mid] < t) lo = mid + 1; else hi = mid; }
        return lo;
    }

    /// <summary>Tag usage in bytes (paged + non-paged) for raw samples [from, Count).</summary>
    public long[] TagSeries(PoolTag tag, int from = 0, bool? nonPagedOnly = null)
    {
        var r = new long[Math.Max(0, _times.Count - from)];
        if (!_tagIndex.TryGetValue(tag, out int idx)) return r;
        for (int i = from; i < _times.Count; i++)
        {
            var (p, n) = _tagRows[i].Get(idx);
            r[i - from] = 1024L * (nonPagedOnly switch { true => n, false => p, null => p + (long)n });
        }
        return r;
    }

    /// <summary>A tag's cumulative alloc / free counters (paged + non-paged, 32-bit) at a raw sample; (0, 0) when not stored.</summary>
    public (uint allocs, uint frees) TagCountsAt(PoolTag tag, int index)
    {
        if (!_tagIndex.TryGetValue(tag, out int idx) || index < 0 || index >= _times.Count) return (0, 0);
        return _tagRows[index].Counts(idx);
    }

    /// <summary>The first raw sample in [from, to] that stored the tag's counters (raw rows keep tags of 16 KB and up), or -1.</summary>
    public int FirstStoredIndex(PoolTag tag, int from, int to)
    {
        if (!_tagIndex.ContainsKey(tag)) return -1;
        for (int i = Math.Max(0, from); i <= to && i < _times.Count; i++)
            if (TagCountsAt(tag, i) != (0u, 0u)) return i;
        return -1;
    }

    /// <summary>
    /// Allocation / free rates of a tag between two raw samples. A fresh leak starts below the 16 KB storage threshold:
    /// the start moves forward to the first sample that stored the tag, and the rate is over the span actually covered.
    /// </summary>
    public TagRates TagRatesBetween(PoolTag tag, int from, int to)
    {
        if (from < 0 || to >= _times.Count || to <= from) return new TagRates(0, 0, AllocShape.Idle);
        from = FirstStoredIndex(tag, from, to - 1);
        if (from < 0) return new TagRates(0, 0, AllocShape.Idle);
        var a = TagCountsAt(tag, from);
        var b = TagCountsAt(tag, to);
        if (a == (0u, 0u) || b == (0u, 0u)) return new TagRates(0, 0, AllocShape.Idle);
        return LeakShapes.Rates(a, b, (_times[to] - _times[from]).TotalMinutes);
    }

    public (long paged, long nonPaged) TagAt(PoolTag tag, int index)
    {
        if (!_tagIndex.TryGetValue(tag, out int idx) || index < 0 || index >= _times.Count) return (0, 0);
        var (p, n) = _tagRows[index].Get(idx);
        return (p * 1024L, n * 1024L);
    }

    /// <summary>Process points aligned with raw samples [from, Count); null entries where the process did not exist.</summary>
    public ProcPoint?[] ProcessSeries(ProcessKey key, int from = 0)
    {
        var r = new ProcPoint?[Math.Max(0, _times.Count - from)];
        if (!_procs.TryGetValue(key, out var track)) return r;
        for (int i = from; i < _times.Count; i++)
        {
            long seq = _firstSeq + i;
            long j = seq - track.FirstSeq;
            if (j >= 0 && j < track.Points.Count) r[i - from] = track.Points[(int)j];
        }
        return r;
    }

    /// <summary>The process's points in place (what <see cref="ProcessSeries"/> copies), for reading many processes cheaply.</summary>
    public ProcessPoints Points(ProcessKey key) =>
        _procs.TryGetValue(key, out var track) ? new ProcessPoints(track.Points, (int)(track.FirstSeq - _firstSeq)) : default;

    /// <summary>Processes with at least one point in raw samples [from, Count).</summary>
    public List<ProcessKey> ProcessesSince(int from)
    {
        long seq = _firstSeq + from;
        return _procs.Where(kv => kv.Value.LastSeq >= seq).Select(kv => kv.Key).ToList();
    }

    /// <summary>The most recent handle scan at or before <paramref name="t"/>, else the earliest one.</summary>
    public TypedHandles? HandlesAtOrBefore(DateTime t)
    {
        TypedHandles? best = null;
        foreach (var h in _handles) { if (h.TimeUtc <= t) best = h; else break; }
        return best ?? (_handles.Count > 0 ? _handles[0] : null);
    }

    public TypedHandles? LatestHandles => _handles.Count > 0 ? _handles[^1] : null;

    private void RollMinute(DateTime now)
    {
        var minute = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        if (_minuteStart == DateTime.MinValue) { _minuteStart = minute; _minuteFromIndex = _times.Count - 1; return; }
        if (minute == _minuteStart) return;
        // Close the previous minute: samples [_minuteFromIndex, Count-1).
        int from = Math.Max(0, _minuteFromIndex), to = _times.Count - 1;
        if (to > from)
        {
            int n = to - from;
            var sums = new long[_tags.Count];
            for (int i = from; i < to; i++)
            {
                var tr = _tagRows[i];
                for (int k = 0; k < tr.Idx.Length; k++) sums[tr.Idx[k]] += tr.PagedKb[k] + (long)tr.NonPagedKb[k];
            }
            var tags = new List<PoolTag>();
            var bytes = new List<long>();
            for (int k = 0; k < sums.Length; k++)
            {
                long avg = sums[k] * 1024 / n;
                if (avg >= MinuteMinBytes) { tags.Add(_tags[k]); bytes.Add(avg); }
            }
            long seqFrom = _firstSeq + from, seqTo = _firstSeq + to - 1;
            var keys = new List<ProcessKey>(); var ops = new List<long>();
            foreach (var (key, track) in _procs)
            {
                if (track.LastSeq < seqFrom || track.FirstSeq > seqTo) continue; // gone before, or new in the boundary sample
                var first = track.Points[(int)Math.Max(0, seqFrom - track.FirstSeq)];
                var last = track.Points[(int)Math.Min(track.Points.Count - 1, seqTo - track.FirstSeq)];
                long dOps = last.TotalOps - first.TotalOps;
                // No I/O in the minute: nothing to attribute. On a busy PC that is most short commands, seen in one sample.
                if (dOps <= 0) continue;
                keys.Add(key); ops.Add(dOps);
            }
            _minutes.Add(new MinutePoint
            {
                TimeUtc = _minuteStart, Totals = _totals[to - 1], System = _system[to - 1], Tags = tags.ToArray(), TagBytes = bytes.ToArray(),
                Processes = keys.ToArray(), ProcessOps = ops.ToArray(),
            });
            var cutoff = now - MinuteSpan;
            int drop = 0;
            while (drop < _minutes.Count && _minutes[drop].TimeUtc < cutoff) drop++;
            if (drop > 0) _minutes.RemoveRange(0, drop);
        }
        _minuteStart = minute;
        _minuteFromIndex = _times.Count - 1;
    }

    private void Trim(DateTime now)
    {
        var cutoff = now - RawSpan;
        int drop = 0;
        while (drop < _times.Count - 1 && (_times[drop] < cutoff || _times.Count - drop > MaxRawSamples)) drop++;
        if (drop == 0) return;
        if (drop < 32 && _times.Count - MaxRawSamples < 32) return; // trim in batches of 32

        _times.RemoveRange(0, drop);
        _totals.RemoveRange(0, drop);
        _system.RemoveRange(0, drop);
        _tagRows.RemoveRange(0, drop);
        _firstSeq += drop;
        _minuteFromIndex -= drop;
        foreach (var key in _procs.Keys.ToList())
        {
            var t = _procs[key];
            if (t.LastSeq < _firstSeq) { _procs.Remove(key); continue; }
            int cut = (int)(_firstSeq - t.FirstSeq);
            if (cut > 0) { t.Points.RemoveRange(0, cut); t.FirstSeq = _firstSeq; }
        }
    }

    private static int ToKb(ulong bytes) => (int)Math.Min(int.MaxValue, (bytes + 512) / 1024);
}
