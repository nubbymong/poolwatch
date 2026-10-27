namespace PoolWatch.Core.Model;

/// <summary>One row of SystemPoolTagInformation.</summary>
public readonly record struct PoolTagEntry(
    PoolTag Tag,
    uint PagedAllocs,
    uint PagedFrees,
    ulong PagedUsed,
    uint NonPagedAllocs,
    uint NonPagedFrees,
    ulong NonPagedUsed)
{
    public ulong TotalUsed => PagedUsed + NonPagedUsed;
}
