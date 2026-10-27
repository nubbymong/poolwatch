namespace PoolWatch.Core.Model;

/// <summary>One process as reported by SystemProcessInformation at one sample.</summary>
public sealed record ProcessSample
{
    public int Pid { get; init; }
    public int ParentPid { get; init; }
    public long CreateTime { get; init; }
    public string ImageName { get; init; } = "";
    public int HandleCount { get; init; }
    public int ThreadCount { get; init; }
    public int SessionId { get; init; }
    public ulong QuotaPagedPoolUsage { get; init; }
    public ulong QuotaNonPagedPoolUsage { get; init; }
    public ulong PrivateBytes { get; init; }
    public ulong WorkingSet { get; init; }
    public long ReadOperationCount { get; init; }
    public long WriteOperationCount { get; init; }
    public long OtherOperationCount { get; init; }
    public long ReadTransferCount { get; init; }
    public long WriteTransferCount { get; init; }
    public long OtherTransferCount { get; init; }
    public long KernelTime { get; init; }
    public long UserTime { get; init; }
    /// <summary>All page faults the process has taken (soft and hard; cumulative, 32-bit). Addendum 4.</summary>
    public uint PageFaultCount { get; init; }

    public ProcessKey Key => new(Pid, CreateTime);
    public long TotalOperations => ReadOperationCount + WriteOperationCount + OtherOperationCount;
}
