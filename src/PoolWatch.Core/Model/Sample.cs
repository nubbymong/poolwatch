namespace PoolWatch.Core.Model;

/// <summary>One collection tick: pool tags, totals and all processes.</summary>
public sealed record Sample(DateTime TimeUtc, PoolTotals Totals, IReadOnlyList<PoolTagEntry> Tags, IReadOnlyList<ProcessSample> Processes)
{
    /// <summary>Commit, available RAM, compression store, total handles (zero where not collected, e.g. tests).</summary>
    public SystemCounters System { get; init; }
}
