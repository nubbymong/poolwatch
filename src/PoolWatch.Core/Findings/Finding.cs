namespace PoolWatch.Core.Findings;

/// <summary>What a finding is about. New kinds are appended (the name is written to findings.jsonl and events.csv).</summary>
public enum FindingKind
{
    SpawnStorm,
    GitRoot,
    StrayGit,
    TokenSpawn,
    FilterEvent,
    PackageActivation,
    Container,
    FilterInstances,
    Lag,
    Experiment,
    Mark,
    VersionChange,
    Crash,
    ChildAccumulation,
    CacheClear,
    CpuTrace,
    TokenGuard,
    ServiceChange,
    Saturation,
    /// <summary>PoolWatch's own setup (review round 3): its data folder moved aside or repaired, a logon task that starts it
    /// from outside Program Files, an older build's clear-cache tasks removed.</summary>
    Setup,
}

public enum FindingSeverity { Info, Warning, Alert }

/// <summary>
/// A diagnostic observation that is not a pool-tag incident: a spawn storm, a suspicious git root, a stray .git,
/// and so on. Every finding goes to findings.jsonl and events.csv (and so onto the incident timelines); alert-level
/// findings also get a report folder under incidents\.
/// </summary>
public sealed record Finding
{
    public required DateTime TimeUtc { get; init; }
    public required FindingKind Kind { get; init; }
    public FindingSeverity Severity { get; init; } = FindingSeverity.Info;
    /// <summary>One line, plain text (escaped where it is written).</summary>
    public required string Title { get; init; }
    /// <summary>Optional longer explanation lines.</summary>
    public IReadOnlyList<string> Details { get; init; } = [];
    /// <summary>Machine-readable fields for report.json / findings.jsonl (values: string, number, bool, or lists of them).</summary>
    public IReadOnlyDictionary<string, object?> Data { get; init; } = new Dictionary<string, object?>();
    /// <summary>The process the finding is about, if any.</summary>
    public int Pid { get; init; }
    public string? Image { get; init; }
    /// <summary>Set when a report folder was written for it.</summary>
    public string? ReportDir { get; set; }
}
