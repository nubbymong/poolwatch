namespace PoolWatch.Core.Incidents;

/// <summary>
/// PoolWatch's disk budget for its report folders (incident folders and finding reports such as spawn storms), so that
/// many open incidents and storms together stay inside the overhead budget (DESIGN.md section 3: under 5 MB an hour of
/// writes, the day's CSV files' share 2.5 MB of it). A write that can wait (an open incident's refresh, a closed
/// one's counted repeats, its events.csv rows, a storm folder's update) is made only while the budget has room; it stays
/// due and is made at a later sample, the one written longest ago first. A write a reader needs at once (an incident
/// opening, re-opening or closing, a trace attached, a new storm folder, the end of a run) is always made, and spends the
/// budget like any other. It refills at <see cref="BytesPerHour"/> up to <see cref="Burst"/>, timed on the clock that only
/// goes forward (the samples' <see cref="Model.Sample.TickMs"/>), and may go below 0 by what such a write cost, at most
/// by <see cref="Burst"/>: what is spent is then made up before anything that can wait is written again, so after a
/// burst of such writes (ten incidents opening together) the refreshes wait a quarter of an hour at most. Thread-safe.
/// </summary>
public sealed class WriteBudget
{
    /// <summary>1 MB an hour, with a burst of 256 KB: what waits is held to this.</summary>
    public const long DefaultBytesPerHour = 1L << 20, DefaultBurst = 256L << 10;

    private readonly object _lock = new();
    private double _room;
    private long? _atMs;

    public WriteBudget(long bytesPerHour = DefaultBytesPerHour, long burst = DefaultBurst)
    {
        BytesPerHour = bytesPerHour;
        Burst = burst;
        _room = burst;
    }

    public long BytesPerHour { get; }
    public long Burst { get; }
    /// <summary>Bytes spent in all (written to report folders).</summary>
    public long Spent { get { lock (_lock) return _spentCanWait + _spentMust; } }
    /// <summary>Of <see cref="Spent"/>, what writes that could wait wrote (each made while the budget had room).</summary>
    public long SpentCanWait { get { lock (_lock) return _spentCanWait; } }
    /// <summary>Of <see cref="Spent"/>, what writes a reader needed at once wrote (made whatever the budget).</summary>
    public long SpentMust { get { lock (_lock) return _spentMust; } }
    /// <summary>
    /// How many times a write that could wait was due and held back for a later sample: deferrals, not distinct writes
    /// (one refresh held back at 60 samples in a row counts 60).
    /// </summary>
    public long Held { get { lock (_lock) return _held; } }
    private long _spentCanWait, _spentMust, _held;
    private long? _firstMs;

    /// <summary>
    /// The most that writes that could wait can have written since the budget first counted (<see cref="SpentCanWait"/>
    /// stays within it): the refill over that time, the burst it starts with, and one burst more, as a write that can wait
    /// is made with any room left and then debited whole, at most down to a burst below 0. It holds while each such write
    /// is no bigger than a burst (an open incident's refresh, a storm folder's update and a batch of event rows are far
    /// smaller). About and profile.json compare <see cref="SpentCanWait"/> with it, as does the CI's agentic-load check.
    /// </summary>
    public long CanWaitAllowance
    {
        get
        {
            lock (_lock)
            {
                long ms = _firstMs is { } first && _atMs is { } at ? Math.Max(0, at - first) : 0;
                return (long)(ms * (BytesPerHour / 3_600_000.0)) + 2 * Burst;
            }
        }
    }

    /// <summary>Whether a write that can wait may be made at <paramref name="nowMs"/> (on the forward clock).</summary>
    public bool HasRoom(long nowMs)
    {
        lock (_lock)
        {
            Refill(nowMs);
            return _room > 0;
        }
    }

    /// <summary>
    /// Whether the budget has room to spare at <paramref name="nowMs"/>: at least half its burst, as it has when nothing
    /// waited for it lately. A write worth making only then (an open incident's chart and CSVs) is left out under load.
    /// </summary>
    public bool HasRoomToSpare(long nowMs)
    {
        lock (_lock)
        {
            Refill(nowMs);
            return _room >= Burst / 2.0;
        }
    }

    /// <summary>A write that could wait was held back (counted for the cost report).</summary>
    public void Hold() { lock (_lock) _held++; }

    /// <summary>A write of <paramref name="bytes"/> was made at <paramref name="nowMs"/>: one that could wait, or (<paramref name="must"/>) one a reader needed at once.</summary>
    public void Spend(long bytes, long nowMs, bool must = false)
    {
        if (bytes <= 0) return;
        lock (_lock)
        {
            Refill(nowMs);
            _room = Math.Max(_room - bytes, -Burst);
            if (must) _spentMust += bytes;
            else _spentCanWait += bytes;
        }
    }

    private void Refill(long nowMs)
    {
        _firstMs ??= nowMs;
        if (_atMs is { } at && nowMs > at) _room = Math.Min(Burst, _room + (nowMs - at) * (BytesPerHour / 3_600_000.0));
        if (_atMs is not { } was || nowMs > was) _atMs = nowMs;
    }
}
