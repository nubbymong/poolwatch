namespace PoolWatch.Core.Model;

public enum LifecycleKind { Start, Exit, Orphaned, ServiceChange, Capture, Alert, Release, Info, Finding, Rollup }

/// <summary>A process lifecycle (or app) event recorded for the timeline and events.csv.</summary>
public sealed record LifecycleEvent
{
    public DateTime TimeUtc { get; init; }
    public LifecycleKind Kind { get; init; }
    public int Pid { get; init; }
    public long CreateTime { get; init; }
    public int ParentPid { get; init; }
    public string Image { get; init; } = "";
    public string? CommandLine { get; init; }
    public string? User { get; init; }
    public int? ExitCode { get; init; }
    public string? Detail { get; init; }
    /// <summary>For rollups: who the parent works for (app, service, Claude Code / AICC session), resolved while it was alive.</summary>
    public string? Owner { get; init; }
    /// <summary>For rollups: that owner's identity key (item 4), which matches it to a trace owner (2e); the name is only for display.</summary>
    public string? OwnerKey { get; init; }
    /// <summary>For rollups: how many processes were started.</summary>
    public int Count { get; init; }
    public ProcessKey Key => new(Pid, CreateTime);
}
