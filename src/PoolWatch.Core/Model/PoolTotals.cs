namespace PoolWatch.Core.Model;

/// <summary>
/// Pool totals at one sample. Committed* are what Task Manager shows (GetPerformanceInfo / PDH);
/// TagSum* are the sums of SystemPoolTagInformation usage, which run lower (fragmentation, lazy release).
/// </summary>
public readonly record struct PoolTotals(ulong CommittedPaged, ulong CommittedNonPaged, ulong TagSumPaged, ulong TagSumNonPaged);
