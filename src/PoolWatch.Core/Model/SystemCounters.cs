namespace PoolWatch.Core.Model;

/// <summary>
/// System-wide counters at one sample (GetPerformanceInfo plus the Memory Compression process from the process
/// snapshot): commit and its limit, available RAM, the compression store's working set, total handles and processes.
/// </summary>
public readonly record struct SystemCounters(ulong CommitBytes, ulong CommitLimitBytes, ulong AvailableBytes, ulong CompressionWsBytes, uint Handles, uint Processes);
