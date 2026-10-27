namespace PoolWatch.Core.Lag;

/// <summary>
/// C1 (review): the lag probe pauses while PoolWatch's own trace or cache clear runs and for <see cref="After"/> after it
/// ends, so lag PoolWatch caused itself is never recorded as a pool lag onset. Pure: the caller passes whether one is
/// running, since when, and when the last one ended, on a monotonic clock (a wall-clock jump must not end or extend a pause).
/// C1 b (review round 2): a hung capture thread must not pause the probe forever: after <see cref="MaxPause"/> of the
/// same work the probe runs again (<see cref="State.Overdue"/>; the caller notes it once).
/// </summary>
public static class ProbePause
{
    /// <summary>How long the probe stays paused after PoolWatch's own work ends (the disk and caches settle).</summary>
    public static readonly TimeSpan After = TimeSpan.FromSeconds(30);
    /// <summary>The longest pause for one piece of work: a capture is at most 5 min of recording plus parsing; past this it is stuck.</summary>
    public static readonly TimeSpan MaxPause = TimeSpan.FromMinutes(10);

    public enum State
    {
        /// <summary>Probe as usual.</summary>
        Probing,
        /// <summary>PoolWatch's own work runs (or ended under 30 s ago): no probe.</summary>
        Paused,
        /// <summary>PoolWatch's own work has run for over <see cref="MaxPause"/>: probe again, and say so.</summary>
        Overdue,
    }

    /// <param name="selfBusy">PoolWatch's own trace (recording or parsing) or cache clear is running now.</param>
    /// <param name="busySince">When the work running now started (monotonic); null when unknown (then no cap applies).</param>
    /// <param name="lastSelfEnd">When the last one ended (monotonic); null when none has run. Covers one that started and
    /// ended between two probes.</param>
    /// <param name="now">The same monotonic clock.</param>
    public static State StateOf(bool selfBusy, TimeSpan? busySince, TimeSpan? lastSelfEnd, TimeSpan now)
    {
        if (selfBusy) return busySince is { } since && now - since >= MaxPause ? State.Overdue : State.Paused;
        return lastSelfEnd is { } end && now - end < After ? State.Paused : State.Probing;
    }

    /// <summary>Whether the probe pauses now (<see cref="StateOf"/> is <see cref="State.Paused"/>).</summary>
    public static bool Paused(bool selfBusy, TimeSpan? busySince, TimeSpan? lastSelfEnd, TimeSpan now) =>
        StateOf(selfBusy, busySince, lastSelfEnd, now) == State.Paused;

    /// <summary>The later of two end times (either may be null).</summary>
    public static TimeSpan? Later(TimeSpan? a, TimeSpan? b) => a is null ? b : b is null ? a : a > b ? a : b;

    /// <summary>The earlier of two start times (either may be null): the longest-running work decides the cap.</summary>
    public static TimeSpan? Earlier(TimeSpan? a, TimeSpan? b) => a is null ? b : b is null ? a : a < b ? a : b;
}
