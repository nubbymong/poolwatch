namespace PoolWatch.Core.Growth;

/// <summary>One reading of the memory lists, by the minute: RAM in use, the cache (standby list) and free (free and zeroed).</summary>
public readonly record struct RamPoint(DateTime MinuteUtc, long InUse, long Cache, long Free);

/// <summary>One column of the graph's RAM overlay: the average of the readings in it.</summary>
public readonly record struct RamColumn(DateTime FromUtc, DateTime ToUtc, long InUse, long Cache, long Free);

/// <summary>
/// The memory lists PoolWatch reads anyway (SystemMemoryListInformation, once a minute), kept as one point a minute for
/// <see cref="Keep"/> points (25 hours) in a fixed ring: nothing grows and nothing is written to disk. In use is what the
/// cache and free leave of the PC's RAM. Not thread-safe: the sampler writes, readers hold the same lock (the cache clearer's, which records it).
/// </summary>
public sealed class RamHistory
{
    /// <summary>25 hours of minutes: the 24-hour graph and its first column.</summary>
    public const int Keep = 25 * 60;

    private readonly RamPoint[] _ring = new RamPoint[Keep];
    private int _start, _count;

    /// <summary>The PC's RAM as the last reading was taken against; 0 before any.</summary>
    public long TotalBytes { get; private set; }

    public int Count => _count;

    /// <summary>About what the history holds, in bytes: its ring.</summary>
    public static long ApproxBytes => Keep * 32L + 64;

    /// <summary>
    /// A reading: <paramref name="totalRam"/> the PC's RAM, <paramref name="free"/> the free and zeroed lists,
    /// <paramref name="cache"/> the standby list. A second reading in the same minute replaces the first; one with no RAM
    /// size or no lists known is not kept.
    /// </summary>
    public void Add(DateTime utc, long totalRam, long free, long cache)
    {
        if (totalRam <= 0 || free + cache <= 0) return;
        TotalBytes = totalRam;
        free = Math.Clamp(free, 0, totalRam);
        cache = Math.Clamp(cache, 0, totalRam - free);
        var point = new RamPoint(new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc), totalRam - free - cache, cache, free);
        if (_count > 0 && At(_count - 1).MinuteUtc == point.MinuteUtc) { _ring[(_start + _count - 1) % Keep] = point; return; }
        if (_count < Keep) _ring[(_start + _count++) % Keep] = point;
        else
        {
            _ring[_start] = point;
            _start = (_start + 1) % Keep;
        }
    }

    public RamPoint At(int index) => _ring[(_start + index) % Keep];

    /// <summary>
    /// The columns of the <paramref name="span"/> up to <paramref name="nowUtc"/>, each <paramref name="width"/> wide and
    /// on the local clock's multiples of it (a quarter hour starts at :00, :15, ...), the average of the readings in each;
    /// a column with no reading (the PC asleep) is left out.
    /// </summary>
    public List<RamColumn> Columns(DateTime nowUtc, TimeSpan span, TimeSpan width)
    {
        var columns = new List<RamColumn>();
        if (_count == 0 || width <= TimeSpan.Zero) return columns;
        var fromUtc = nowUtc - span;
        long offset = (nowUtc.ToLocalTime() - nowUtc).Ticks; // the local clock's offset now
        DateTime Start(DateTime utc) => new(utc.Ticks - (utc.Ticks + offset) % width.Ticks, DateTimeKind.Utc);
        DateTime? colFrom = null;
        long inUse = 0, cache = 0, free = 0;
        int n = 0;
        void Flush()
        {
            if (colFrom is { } f && n > 0) columns.Add(new RamColumn(f < fromUtc ? fromUtc : f, Min(f + width, nowUtc), inUse / n, cache / n, free / n));
            inUse = cache = free = 0;
            n = 0;
        }
        for (int i = 0; i < _count; i++)
        {
            var p = At(i);
            if (p.MinuteUtc < fromUtc - width || p.MinuteUtc > nowUtc) continue;
            var start = Start(p.MinuteUtc);
            if (start + width <= fromUtc) continue;
            if (colFrom != start) { Flush(); colFrom = start; }
            inUse += p.InUse; cache += p.Cache; free += p.Free;
            n++;
        }
        Flush();
        return columns;
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
