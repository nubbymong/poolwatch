namespace PoolWatch.Core.Model;

/// <summary>One collection tick: pool tags, totals and all processes.</summary>
public sealed record Sample(DateTime TimeUtc, PoolTotals Totals, IReadOnlyList<PoolTagEntry> Tags, IReadOnlyList<ProcessSample> Processes)
{
    /// <summary>Commit, available RAM, compression store, total handles (zero where not collected, e.g. tests).</summary>
    public SystemCounters System { get; init; }
    /// <summary>
    /// When the sample was taken, in milliseconds since Windows started (Environment.TickCount64), a clock that only goes
    /// forward whatever the system clock does: what the intervals between samples are timed on (the load per process,
    /// the minute cost points, the detector's windows, how long load has lasted before "PC is saturated"). <see cref="TimeUtc"/>
    /// stamps it for people and reports. Every sample is given it.
    /// </summary>
    public required long TickMs { get; init; }
}
