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
    private readonly PointTrack? _points;
    private readonly int _first;

    internal ProcessPoints(PointTrack points, int first)
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
/// One process's points in the raw history, from raw sample <see cref="FirstSeq"/> on, given back exactly as they were
/// added and held as runs: a point equal to the one before it (a process that did no I/O and kept its handles and quota
/// between two samples) takes no space, a new value 28 bytes. A run's value is packed: the I/O counters as their rise from
/// a base (the smallest of each counter among the runs held, moved up as the oldest go, so it never falls behind a busy
/// process), the quota pool usage in 32 bits, 24 bytes instead of a <see cref="ProcPoint"/>'s 48. A point that does not
/// fit (a counter that fell below the base or rose more than 4 billion operations past it inside the raw history's 70
/// minutes, a million a second; a quota of 4 GB or more) turns the track into plain values, so nothing is ever rounded.
/// Reading the points in order finds each run at once; any other read searches the runs. Readers hold the history lock,
/// as for every other part of it.
/// </summary>
internal sealed class PointTrack
{
    private struct Packed : IEquatable<Packed>
    {
        public int Handles;
        public uint Read, Write, Other, QuotaPaged, QuotaNonPaged;

        public readonly bool Equals(Packed o) =>
            Handles == o.Handles && Read == o.Read && Write == o.Write && Other == o.Other && QuotaPaged == o.QuotaPaged && QuotaNonPaged == o.QuotaNonPaged;
    }

    /// <summary>The runs' values (packed, else plain in <see cref="_wide"/>), and where each run begins: its first point's place, counted from the track's first point ever.</summary>
    private Packed[] _packed = new Packed[1];
    private ProcPoint[]? _wide;
    private int[] _runAt = new int[1];
    /// <summary>The runs held are [<see cref="_first"/>, <see cref="_runs"/>) of the arrays; the first may begin before the first point held.</summary>
    private int _first, _runs;
    /// <summary>The place of the first point held (counted as <see cref="_runAt"/> is), and how many are held.</summary>
    private int _base, _count;
    /// <summary>The run the last read found: the next point in order is in it or the one after.</summary>
    private int _cursor;
    private long _read0, _write0, _other0;

    /// <summary>The raw sample (sequence number) of the first point held.</summary>
    public long FirstSeq;
    public long LastSeq => FirstSeq + _count - 1;
    public int Count => _count;
    /// <summary>Its values are held packed (24 bytes each), not as plain points.</summary>
    internal bool IsPacked => _wide is null;
    /// <summary>The runs held (each a value its points share).</summary>
    internal int Runs => _runs - _first;

    public ProcPoint this[int i]
    {
        get
        {
            if ((uint)i >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(i));
            return Value(RunOf(_base + i));
        }
    }

    public ProcPoint Last => Value(_runs - 1);

    private ProcPoint Value(int r)
    {
        if (_wide is { } wide) return wide[r];
        ref var p = ref _packed[r];
        return new ProcPoint(p.Handles, _read0 + p.Read, _write0 + p.Write, _other0 + p.Other, p.QuotaPaged, p.QuotaNonPaged);
    }

    /// <summary>The run holding the point at place <paramref name="at"/> (one held): the cursor's, the next one, else a search.</summary>
    private int RunOf(int at)
    {
        int c = _cursor;
        if (c >= _first && c < _runs && _runAt[c] <= at)
        {
            if (c + 1 == _runs || _runAt[c + 1] > at) return c;
            if (c + 2 == _runs || _runAt[c + 2] > at) return _cursor = c + 1;
        }
        int lo = _first, hi = _runs - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >>> 1;
            if (_runAt[mid] <= at) lo = mid; else hi = mid - 1;
        }
        return _cursor = lo;
    }

    public void Add(ProcPoint p)
    {
        if (_count == 0)
        {
            (_first, _runs, _base, _cursor) = (0, 0, 0, 0);
            if (_wide is null) (_read0, _write0, _other0) = (p.ReadOps, p.WriteOps, p.OtherOps);
        }
        if (_wide is null && !Fits(p)) Widen();
        if (_count > 0 && (_wide is { } w ? w[_runs - 1] == p : Pack(p).Equals(_packed[_runs - 1])))
        {
            _count++; // the same as the point before: its run goes on
            return;
        }
        if (_runs == _runAt.Length)
        {
            Compact();
            if (_wide is null && !Fits(p)) Widen();
        }
        if (_wide is { } wide) wide[_runs] = p;
        else _packed[_runs] = Pack(p);
        _runAt[_runs++] = _base + _count++;
    }

    private Packed Pack(ProcPoint p) => new()
    {
        Handles = p.Handles, Read = (uint)(p.ReadOps - _read0), Write = (uint)(p.WriteOps - _write0), Other = (uint)(p.OtherOps - _other0),
        QuotaPaged = (uint)p.QuotaPaged, QuotaNonPaged = (uint)p.QuotaNonPaged,
    };

    /// <summary>Drops the first <paramref name="n"/> points.</summary>
    public void RemoveFirst(int n)
    {
        n = Math.Min(n, _count);
        _base += n;
        _count -= n;
        FirstSeq += n;
        if (_count == 0) { (_first, _runs, _base, _cursor) = (0, 0, 0, 0); return; }
        while (_first + 1 < _runs && _runAt[_first + 1] <= _base) _first++;
    }

    private bool Fits(ProcPoint p) =>
        In(p.ReadOps - _read0) && In(p.WriteOps - _write0) && In(p.OtherOps - _other0) && In(p.QuotaPaged) && In(p.QuotaNonPaged);

    private static bool In(long v) => v is >= 0 and <= uint.MaxValue;

    private void Widen()
    {
        var wide = new ProcPoint[_runAt.Length];
        for (int r = _first; r < _runs; r++) wide[r] = Value(r);
        _wide = wide;
        _packed = [];
    }

    /// <summary>
    /// The arrays are full to their end: the runs held move to the front when the dropped ones leave at least an eighth free
    /// (a track as long as the raw history, its oldest points dropped as new ones come), else the arrays double; then the
    /// packed values' base moves up to the smallest of each counter among the runs held (<see cref="Rebase"/>).
    /// </summary>
    private void Compact()
    {
        int len = _runAt.Length, held = _runs - _first;
        if (_first > 0 && held <= len - len / 8)
        {
            Array.Copy(_runAt, _first, _runAt, 0, held);
            if (_wide is { } w) Array.Copy(w, _first, w, 0, held);
            else Array.Copy(_packed, _first, _packed, 0, held);
        }
        else
        {
            int bigger = Math.Max(2, len * 2);
            var at = new int[bigger];
            Array.Copy(_runAt, _first, at, 0, held);
            _runAt = at;
            if (_wide is { } w) { var nw = new ProcPoint[bigger]; Array.Copy(w, _first, nw, 0, held); _wide = nw; }
            else { var np = new Packed[bigger]; Array.Copy(_packed, _first, np, 0, held); _packed = np; }
        }
        (_runs, _cursor, _first) = (held, Math.Max(0, _cursor - _first), 0);
        if (_wide is null) Rebase();
    }

    /// <summary>
    /// The base moves up to the smallest of each counter among the runs held: every rise stays at least 0 and no larger, so a
    /// value that fitted still fits, and a busy process's counters are measured from where its oldest point held is, not from
    /// the first point it ever had.
    /// </summary>
    private void Rebase()
    {
        if (_runs == 0) return;
        uint r = uint.MaxValue, w = uint.MaxValue, o = uint.MaxValue;
        for (int i = 0; i < _runs; i++)
        {
            ref var q = ref _packed[i];
            r = Math.Min(r, q.Read); w = Math.Min(w, q.Write); o = Math.Min(o, q.Other);
        }
        if ((r | w | o) == 0) return;
        for (int i = 0; i < _runs; i++)
        {
            ref var q = ref _packed[i];
            q.Read -= r; q.Write -= w; q.Other -= o;
        }
        (_read0, _write0, _other0) = (_read0 + r, _write0 + w, _other0 + o);
    }
}

/// <summary>
/// Handles by type name per process instance at one handle scan. Keyed by (PID, create time) so a reused PID
/// is never compared with the process that owned it earlier. Held as flat arrays (the processes sorted, each one's types
/// and counts in a row, the scan's type names once), a fifth of the per-process dictionaries a scan comes in: the raw
/// history keeps 70 minutes of scans, about 18 of a busy PC's 700 processes and 15 types each.
/// </summary>
public sealed class TypedHandles
{
    private readonly ProcessKey[] _keys;
    /// <summary>Where each process's row of <see cref="_types"/> and <see cref="_counts"/> begins (one more: the end).</summary>
    private readonly int[] _rows;
    private readonly int[] _types, _counts;
    private readonly string[] _names;

    public TypedHandles(DateTime timeUtc, IReadOnlyDictionary<ProcessKey, Dictionary<string, int>> byProcess)
    {
        TimeUtc = timeUtc;
        _keys = byProcess.Keys.ToArray();
        Array.Sort(_keys, Order);
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        _rows = new int[_keys.Length + 1];
        int n = 0;
        foreach (var d in byProcess.Values) n += d.Count;
        _types = new int[n];
        _counts = new int[n];
        int at = 0;
        for (int i = 0; i < _keys.Length; i++)
        {
            _rows[i] = at;
            foreach (var (type, count) in byProcess[_keys[i]])
            {
                if (!names.TryGetValue(type, out int t)) names[type] = t = names.Count;
                _types[at] = t;
                _counts[at++] = count;
            }
        }
        _rows[_keys.Length] = at;
        _names = new string[names.Count];
        foreach (var (name, t) in names) _names[t] = name;
    }

    public DateTime TimeUtc { get; }
    /// <summary>When the scan was taken on the clock that only goes forward (its tick's <see cref="Model.Sample.TickMs"/>): how long it is kept is timed on it.</summary>
    public required long Ms { get; init; }

    private static readonly Comparison<ProcessKey> Order = (a, b) => a.Pid != b.Pid ? a.Pid.CompareTo(b.Pid) : a.CreateTime.CompareTo(b.CreateTime);

    private int IndexOf(ProcessKey key)
    {
        int lo = 0, hi = _keys.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1, c = Order(_keys[mid], key);
            if (c == 0) return mid;
            if (c < 0) lo = mid + 1; else hi = mid - 1;
        }
        return -1;
    }

    /// <summary>Handles of <paramref name="type"/> held by the process; "*" counts all types.</summary>
    public int Count(ProcessKey key, string type)
    {
        int i = IndexOf(key);
        if (i < 0) return 0;
        int t = type == "*" ? -1 : Array.IndexOf(_names, type);
        if (t < 0 && type != "*") return 0;
        int sum = 0;
        for (int k = _rows[i]; k < _rows[i + 1]; k++)
            if (t < 0) sum += _counts[k];
            else if (_types[k] == t) return _counts[k];
        return sum;
    }

    public bool Has(ProcessKey key) => IndexOf(key) >= 0;

    /// <summary>The processes the scan has.</summary>
    public int ProcessCount => _keys.Length;
}

/// <summary>
/// One hour of the minutes' process I/O summed up (<see cref="History.SummedHours"/>): the hour's busiest processes, at most
/// <see cref="History.HourTop"/>, busiest first, with their names as the registry gave them when the hour was summed, and
/// the I/O of every process that did any in the hour (PID 0 left out, as the since-boot report leaves it out). What the
/// since-boot report splits an hour's growth by: its top processes and their share of all the hour's I/O, as from the
/// minutes themselves.
/// </summary>
public sealed class HourProcesses
{
    /// <summary>The hour, as a time of day (what the since-boot report groups the minutes by).</summary>
    public required DateTime HourUtc { get; init; }
    public required ProcessKey[] Keys { get; init; }
    public required long[] Ops { get; init; }
    /// <summary>Each process's name when the hour was summed (null: not known then; the report asks the registry).</summary>
    public required string?[] Names { get; init; }
    /// <summary>The I/O of every process in the hour, those not listed too.</summary>
    public required long TotalOps { get; init; }
    /// <summary>How many processes did I/O in the hour.</summary>
    public required int Processes { get; init; }
    /// <summary>The newest minute summed into it, on the clock that only goes forward: it goes when the minutes no longer hold it.</summary>
    public long LastMs { get; init; }
}

/// <summary>
/// One minute of downsampled history: average tag usage for significant tags, and the I/O of each process that did any
/// in the minute (what the since-boot report splits each hour's growth by). A minute older than
/// <see cref="History.MinuteDetailSpan"/> keeps its tags and totals; its processes are summed into its hour
/// (<see cref="History.SummedHours"/>) and its own lists are empty.
/// </summary>
public sealed class MinutePoint
{
    /// <summary>When the minute began, as a time of day (shown, and what the since-boot report's hours are told by).</summary>
    public DateTime TimeUtc { get; init; }
    /// <summary>When the minute began on the clock that only goes forward: the minutes are cut on it, and how long they are kept is timed on it.</summary>
    public long Ms { get; init; }
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
/// <see cref="MinuteSpan"/> so the since-boot / 24-hour views work without re-reading disk. Each sample keeps both its
/// times: <see cref="Times"/> to show and write, and <see cref="Ms"/> on the clock that only goes forward
/// (<see cref="Model.Sample.TickMs"/>), on which how long the samples, minutes and handle scans are kept, where a minute
/// begins, and the windows read by <see cref="IndexAtOrAfterMs"/> are timed, so a system clock set back or forward moves
/// none of them.
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
    private readonly List<long> _ms = new();
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
    private readonly Dictionary<ProcessKey, PointTrack> _procs = new();
    private readonly List<TypedHandles> _handles = new();
    private readonly List<MinutePoint> _minutes = new();

    /// <summary>
    /// How long a minute keeps each process's I/O; older minutes are summed into their hour (<see cref="SummedHours"/>, the
    /// busiest <see cref="HourTop"/> and the total). The minutes of a busy PC's day held 350,000 entries (about 9 MB).
    /// </summary>
    public TimeSpan MinuteDetailSpan { get; set; } = TimeSpan.FromHours(2);
    /// <summary>The processes a summed hour keeps by name, busiest first (the since-boot report names 3 an hour).</summary>
    public const int HourTop = 10;
    /// <summary>A process's name as an hour is summed (the registry's display name; the pipeline sets it). Null: none kept.</summary>
    public Func<ProcessKey, string?>? NameOf { get; set; }
    private readonly List<HourProcesses> _hours = new();
    /// <summary>The hour being summed: every process's I/O so far, in the order first seen (the report ranks ties in that order).</summary>
    private DateTime? _sumHour;
    private readonly Dictionary<ProcessKey, int> _sumIndex = new();
    private readonly List<(ProcessKey key, long ops)> _sum = new();
    private long _sumLastMs;
    /// <summary>How many of the oldest minutes have had their processes summed (their lists are empty).</summary>
    private int _folded;
    /// <summary>The minute being filled: when it began on the forward clock (null before the first sample) and as a time of day.</summary>
    private long? _minuteStartMs;
    private DateTime _minuteStartUtc;
    private int _minuteFromIndex;

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
    /// <summary>Each raw sample's time of day (<see cref="Model.Sample.TimeUtc"/>), to show and write.</summary>
    public IReadOnlyList<DateTime> Times => _times;
    /// <summary>Each raw sample's time on the clock that only goes forward (<see cref="Model.Sample.TickMs"/>), on which windows are timed.</summary>
    public IReadOnlyList<long> Ms => _ms;
    public IReadOnlyList<PoolTotals> Totals => _totals;
    public IReadOnlyList<SystemCounters> System => _system;
    public IReadOnlyList<PoolTag> KnownTags => _tags;
    public IReadOnlyList<MinutePoint> Minutes => _minutes;
    public IReadOnlyList<TypedHandles> HandleScans => _handles;
    /// <summary>Process instances with raw points held (for the cost report).</summary>
    public int ProcessTrackCount => _procs.Count;
    public long ProcessPointCount => _procs.Values.Sum(t => (long)t.Count);
    /// <summary>The hours summed from the minutes older than <see cref="MinuteDetailSpan"/> (for the cost report).</summary>
    public int SummedHourCount => _hours.Count + (_sumHour is null ? 0 : 1);
    /// <summary>The minutes' process entries held: those of the minutes and of the summed hours (for the cost report).</summary>
    public long MinuteProcessEntries => _minutes.Sum(m => (long)m.Processes.Length) + _hours.Sum(x => (long)x.Keys.Length) + _sum.Count;

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
        // The tags a row holds are nearly always the row before's (a tag crosses 16 KB now and then): it shares that row's
        // list of them, a fifth of a row (70 minutes of 400 such tags: 0.7 MB).
        var before = _tagRows.Count > 0 ? _tagRows[^1].Idx : null;
        bool same = before is not null && before.Length == kept.Count;
        for (int k = 0; same && k < kept.Count; k++) same = before![k] == kept[k].idx;
        var row = new TagRow(same ? before! : new int[kept.Count], new int[kept.Count], new int[kept.Count], new uint[kept.Count], new uint[kept.Count]);
        for (int k = 0; k < kept.Count; k++)
        {
            if (!same) row.Idx[k] = kept[k].idx;
            row.PagedKb[k] = kept[k].p; row.NonPagedKb[k] = kept[k].n; row.Allocs[k] = kept[k].a; row.Frees[k] = kept[k].f;
        }
        long seq = _firstSeq + _times.Count;
        _times.Add(s.TimeUtc);
        _ms.Add(s.TickMs);
        _totals.Add(s.Totals);
        _system.Add(s.System);
        _tagRows.Add(row);
        foreach (var p in s.Processes)
        {
            if (!_procs.TryGetValue(p.Key, out var track)) _procs[p.Key] = track = new PointTrack { FirstSeq = seq };
            if (track.LastSeq != seq - 1 && track.Count > 0)
            {
                // Gap (should not happen for a live process): pad with the last value so indexes stay aligned.
                var last = track.Last;
                while (track.LastSeq < seq - 1) track.Add(last);
            }
            track.Add(new ProcPoint(p.HandleCount, p.ReadOperationCount, p.WriteOperationCount, p.OtherOperationCount,
                (long)p.QuotaPagedPoolUsage, (long)p.QuotaNonPagedPoolUsage));
        }
        RollMinute(s);
        Trim(s.TickMs);
    }

    public void AddHandles(TypedHandles h)
    {
        _handles.Add(h);
        long cutoff = h.Ms - (long)RawSpan.TotalMilliseconds;
        int drop = 0;
        while (drop < _handles.Count - 1 && _handles[drop].Ms < cutoff) drop++;
        if (drop > 0) _handles.RemoveRange(0, drop);
        if (_handles.Count > 200) _handles.RemoveRange(0, _handles.Count - 200);
    }

    /// <summary>
    /// Index of the first raw sample at or after <paramref name="ms"/> on the clock that only goes forward (<see cref="Ms"/>):
    /// a window that ends at a sample and is timed on the forward clock starts here.
    /// </summary>
    public int IndexAtOrAfterMs(long ms)
    {
        int lo = 0, hi = _ms.Count;
        while (lo < hi) { int mid = (lo + hi) / 2; if (_ms[mid] < ms) lo = mid + 1; else hi = mid; }
        return lo;
    }

    /// <summary>
    /// Index of the first raw sample at or after <paramref name="t"/> by the samples' times of day (<see cref="Times"/>): for
    /// the graph's shown window, or a window given only as times of day (a test's). The times are in order only while the
    /// system clock was not set back among them; every window PoolWatch times from a sample is found on the forward clock
    /// (<see cref="IndexAtOrAfterMs"/>).
    /// </summary>
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
        FillTagSeries(tag, from, r, nonPagedOnly);
        return r;
    }

    /// <summary><see cref="TagSeries"/> into <paramref name="into"/> (its length: raw samples [from, Count)), for a reader that reads many tags' series in turn.</summary>
    internal void FillTagSeries(PoolTag tag, int from, long[] into, bool? nonPagedOnly = null)
    {
        if (!_tagIndex.TryGetValue(tag, out int idx)) { Array.Clear(into); return; }
        // Rows that share their tag list (nearly all in a row) have the tag at the same place: searched once per list.
        int[]? list = null;
        int at = -1;
        for (int i = from; i < _times.Count; i++)
        {
            var row = _tagRows[i];
            if (!ReferenceEquals(row.Idx, list)) { list = row.Idx; at = Array.BinarySearch(list, idx); }
            var (p, n) = at >= 0 ? (row.PagedKb[at], row.NonPagedKb[at]) : (0, 0);
            into[i - from] = 1024L * (nonPagedOnly switch { true => n, false => p, null => p + (long)n });
        }
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
        return LeakShapes.Rates(a, b, (_ms[to] - _ms[from]) / 60_000.0); // the minutes between them on the forward clock
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
            if (j >= 0 && j < track.Count) r[i - from] = track[(int)j];
        }
        return r;
    }

    /// <summary>The process's points in place (what <see cref="ProcessSeries"/> copies), for reading many processes cheaply.</summary>
    public ProcessPoints Points(ProcessKey key) =>
        _procs.TryGetValue(key, out var track) ? new ProcessPoints(track, (int)(track.FirstSeq - _firstSeq)) : default;

    /// <summary>The process's track (a test's look at how it is held).</summary>
    internal PointTrack? TrackOf(ProcessKey key) => _procs.GetValueOrDefault(key);

    /// <summary>Processes with at least one point in raw samples [from, Count).</summary>
    public List<ProcessKey> ProcessesSince(int from)
    {
        long seq = _firstSeq + from;
        return _procs.Where(kv => kv.Value.LastSeq >= seq).Select(kv => kv.Key).ToList();
    }

    /// <summary>
    /// The most recent handle scan at or before <paramref name="ms"/> on the clock that only goes forward (each scan's
    /// <see cref="TypedHandles.Ms"/>), else the earliest one.
    /// </summary>
    public TypedHandles? HandlesAtOrBeforeMs(long ms)
    {
        TypedHandles? best = null;
        foreach (var h in _handles) { if (h.Ms <= ms) best = h; else break; }
        return best ?? (_handles.Count > 0 ? _handles[0] : null);
    }

    public TypedHandles? LatestHandles => _handles.Count > 0 ? _handles[^1] : null;

    /// <summary>
    /// Starts a new minute when the sample is in a later one, closing the one before. The minutes are cut on the forward clock
    /// (whole minutes of <see cref="Model.Sample.TickMs"/>); each keeps the time of day it began at, by the sample that
    /// began it, and the minutes are kept for <see cref="MinuteSpan"/> on the forward clock.
    /// </summary>
    private void RollMinute(Model.Sample s)
    {
        long minuteMs = s.TickMs - (s.TickMs % 60_000 + 60_000) % 60_000;
        var minuteUtc = s.TimeUtc.AddMilliseconds(minuteMs - s.TickMs);
        if (_minuteStartMs is not { } startMs) { (_minuteStartMs, _minuteStartUtc, _minuteFromIndex) = (minuteMs, minuteUtc, _times.Count - 1); return; }
        if (minuteMs == startMs) return;
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
                var first = track[(int)Math.Max(0, seqFrom - track.FirstSeq)];
                var last = track[(int)Math.Min(track.Count - 1, seqTo - track.FirstSeq)];
                long dOps = last.TotalOps - first.TotalOps;
                // No I/O in the minute: nothing to attribute. On a busy PC that is most short commands, seen in one sample.
                if (dOps <= 0) continue;
                keys.Add(key); ops.Add(dOps);
            }
            _minutes.Add(new MinutePoint
            {
                TimeUtc = _minuteStartUtc, Ms = startMs, Totals = _totals[to - 1], System = _system[to - 1], Tags = tags.ToArray(), TagBytes = bytes.ToArray(),
                Processes = keys.ToArray(), ProcessOps = ops.ToArray(),
            });
            long cutoff = s.TickMs - (long)MinuteSpan.TotalMilliseconds;
            int drop = 0;
            while (drop < _minutes.Count && _minutes[drop].Ms < cutoff) drop++;
            if (drop > 0)
            {
                _minutes.RemoveRange(0, drop);
                _folded = Math.Max(0, _folded - drop);
                // A summed hour goes once the minutes hold none of it (the oldest one, partly gone, is still read whole).
                long firstMs = _minutes.Count > 0 ? _minutes[0].Ms : long.MaxValue;
                int gone = 0;
                while (gone < _hours.Count && _hours[gone].LastMs < firstMs) gone++;
                if (gone > 0) _hours.RemoveRange(0, gone);
                if (_sumHour is not null && _sumLastMs < firstMs) { _sumHour = null; _sum.Clear(); _sumIndex.Clear(); }
            }
            FoldMinutes(s.TickMs - (long)MinuteDetailSpan.TotalMilliseconds);
        }
        (_minuteStartMs, _minuteStartUtc, _minuteFromIndex) = (minuteMs, minuteUtc, _times.Count - 1);
    }

    /// <summary>The hour a minute is counted in by the since-boot report: its time of day, to the hour.</summary>
    public static DateTime HourOf(DateTime utc) => new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc);

    /// <summary>
    /// The minutes that began before <paramref name="beforeMs"/> (forward clock) have their processes summed into their hour,
    /// in order, and keep their tags and totals. An hour is summed up (<see cref="FinishHour"/>) when a minute of another
    /// hour comes; until then it is kept whole (every process's I/O), so what the report reads of it is as from its minutes.
    /// </summary>
    private void FoldMinutes(long beforeMs)
    {
        while (_folded < _minutes.Count && _minutes[_folded].Ms < beforeMs)
        {
            var m = _minutes[_folded];
            var hour = HourOf(m.TimeUtc);
            if (_sumHour is { } open && open != hour) FinishHour();
            _sumHour = hour;
            for (int i = 0; i < m.Processes.Length; i++)
            {
                var key = m.Processes[i];
                if (key.Pid == 0) continue; // never attributed, never in the total
                if (_sumIndex.TryGetValue(key, out int at)) _sum[at] = (key, _sum[at].ops + m.ProcessOps[i]);
                else { _sumIndex[key] = _sum.Count; _sum.Add((key, m.ProcessOps[i])); }
            }
            _sumLastMs = m.Ms;
            if (m.Processes.Length > 0)
                _minutes[_folded] = new MinutePoint { TimeUtc = m.TimeUtc, Ms = m.Ms, Totals = m.Totals, System = m.System, Tags = m.Tags, TagBytes = m.TagBytes, Processes = [], ProcessOps = [] };
            _folded++;
        }
    }

    /// <summary>The hour being summed is summed up: its <see cref="HourTop"/> busiest processes (ties in the order first seen) named now, and its total.</summary>
    private void FinishHour()
    {
        if (_sumHour is not { } hour) return;
        var top = _sum.OrderByDescending(x => x.ops).Take(HourTop).ToArray();
        _hours.Add(new HourProcesses
        {
            HourUtc = hour, Keys = top.Select(x => x.key).ToArray(), Ops = top.Select(x => x.ops).ToArray(),
            Names = top.Select(x => NameOf?.Invoke(x.key)).ToArray(), TotalOps = _sum.Sum(x => x.ops), Processes = _sum.Count, LastMs = _sumLastMs,
        });
        _sumHour = null;
        _sum.Clear();
        _sumIndex.Clear();
        if (_sum.Capacity > 4096) { _sum.Capacity = 0; _sumIndex.TrimExcess(); }
    }

    /// <summary>
    /// The hours summed from the minutes older than <see cref="MinuteDetailSpan"/>, oldest first, and the hour being summed
    /// (every process it has so far, in the order first seen, without names). The rest of an hour is in its minutes.
    /// </summary>
    public List<HourProcesses> SummedHours()
    {
        var list = new List<HourProcesses>(_hours);
        if (_sumHour is { } hour)
            list.Add(new HourProcesses
            {
                HourUtc = hour, Keys = _sum.Select(x => x.key).ToArray(), Ops = _sum.Select(x => x.ops).ToArray(), Names = new string?[_sum.Count],
                TotalOps = _sum.Sum(x => x.ops), Processes = _sum.Count, LastMs = _sumLastMs,
            });
        return list;
    }

    /// <summary>Drops the raw samples older than <see cref="RawSpan"/> on the forward clock (and beyond <see cref="MaxRawSamples"/>), in batches.</summary>
    private void Trim(long nowMs)
    {
        long cutoff = nowMs - (long)RawSpan.TotalMilliseconds;
        int drop = 0;
        while (drop < _times.Count - 1 && (_ms[drop] < cutoff || _times.Count - drop > MaxRawSamples)) drop++;
        if (drop == 0) return;
        if (drop < 32 && _times.Count - MaxRawSamples < 32) return; // trim in batches of 32

        _times.RemoveRange(0, drop);
        _ms.RemoveRange(0, drop);
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
            if (cut > 0) t.RemoveFirst(cut); // its FirstSeq moves with it, to _firstSeq
        }
    }

    private static int ToKb(ulong bytes) => (int)Math.Min(int.MaxValue, (bytes + 512) / 1024);
}
