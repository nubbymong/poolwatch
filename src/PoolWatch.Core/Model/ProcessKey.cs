namespace PoolWatch.Core.Model;

/// <summary>A process identity: PIDs are reused, so the create time (FILETIME, UTC) is part of the key.</summary>
public readonly record struct ProcessKey(int Pid, long CreateTime)
{
    public DateTime CreateTimeUtc => CreateTime > 0 ? DateTime.FromFileTimeUtc(CreateTime) : DateTime.MinValue;
    public override string ToString() => $"{Pid}@{CreateTime:x}";
}
