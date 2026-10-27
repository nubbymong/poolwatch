using PoolWatch.Core.Correlation;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Model;

namespace PoolWatch.Core.Incidents;

public enum IncidentKind { Growth, NonPagedTotal, Release, Capture }

/// <summary>A file's identity for the report: version resource and Authenticode signer.</summary>
public sealed record FileDetails(string? FileVersion, string? Company, string? Product, string? Description, string? Signer, string SignatureStatus);

/// <summary>Machine context for the bug report (DESIGN.md section 10).</summary>
public sealed record SystemInfo
{
    public string Machine { get; init; } = "";
    public string OsDescription { get; init; } = "";
    public string OsBuild { get; init; } = "";
    public long RamBytes { get; init; }
    public int LogicalCpus { get; init; }
    public DateTime? BootUtc { get; init; }
    public List<VolumeInfo> Volumes { get; init; } = [];
    public List<string> FilterDrivers { get; init; } = [];
    public string AntiVirus { get; init; } = "unknown";
    /// <summary>PoolWatch's own recent CPU, memory and write rate (a watcher must show what it costs).</summary>
    public string? PoolWatchCost { get; init; }
}

/// <summary>What happened when a tag dropped sharply: that explains what was holding the memory.</summary>
public sealed record ReleaseRecord(DateTime TimeUtc, DateTime FromUtc, long FromBytes, long ToBytes, List<string> Exits, List<string> ActiveProcesses,
    string? WorktreeNote = null)
{
    public long Dropped => FromBytes - ToBytes;
}

/// <summary>AICC / Claude Code context of a suspect (DESIGN.md section 9).</summary>
public sealed record SessionDetails(string Chain, Aicc.SessionContext? Context, Aicc.TranscriptHit? Transcript, bool SearchPending);

/// <summary>The tag grew during a burst and had not fallen N minutes after the correlated activity stopped.</summary>
public sealed record RetentionRecord(DateTime TimeUtc, DateTime ActivityStoppedUtc, long RetainedBytes, string Note);

/// <summary>
/// A rule that keeps firing (a growth rule fires on every sample while it holds): its alerts after the first are counted on
/// the first one's timeline entry ("...; fired 37 more times to 06:54:10, latest +612 MB"), so every alert is on the
/// timeline without one line per sample (2f).
/// </summary>
public sealed class AlertRun
{
    public DateTime EntryTimeUtc { get; init; }
    public required string EntryText { get; init; }
    public DateTime LastUtc { get; set; }
    public int More { get; set; }
    /// <summary>The growth the entry was written with: 25% more starts a new entry.</summary>
    public long EntryBytes { get; init; }
    public long LatestBytes { get; set; }
}

/// <summary>One alert (or capture) and everything gathered about it; written to its own incident folder.</summary>
public sealed class Incident
{
    public required string Id { get; init; }
    public required string Dir { get; init; }
    public required PoolTag Tag { get; init; }
    public required IncidentKind Kind { get; init; }
    public DateTime OpenedUtc { get; init; }
    public DateTime LastAlertUtc { get; set; }
    /// <summary>Last time an alert saw the tag above its previous high: the quiet period counts from here, so a
    /// plateau (where the window rule keeps firing) still closes the incident.</summary>
    public DateTime LastRiseUtc { get; set; }
    public long HighestSeenBytes { get; set; }
    public DateTime? ClosedUtc { get; set; }
    public DateTime OnsetUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    public long StartBytes { get; set; }
    public long PeakBytes { get; set; }
    public long CurrentBytes { get; set; }
    public long Growth => PeakBytes - StartBytes;
    public List<Alert> Alerts { get; } = [];
    public List<Suspect> Suspects { get; set; } = [];
    /// <summary>Suspects grouped by owner, from every active process (not only the top 10).</summary>
    public List<OwnerSuspect>? Owners { get; set; }
    public ReleaseRecord? Release { get; set; }
    public RetentionRecord? Retention { get; set; }
    /// <summary>The confirmation trace taken for this incident (milestone 3), if any.</summary>
    public CaptureResult? Capture { get; set; }
    /// <summary>The trace's process instances for this tag with their identities, and the same rolled up by owner.</summary>
    public List<TracedProcess>? Traced { get; set; }
    public List<TraceOwner>? TraceOwners { get; set; }
    /// <summary>The capture <see cref="Traced"/> was resolved from (resolution happens once per trace).</summary>
    public CaptureResult? TracedFrom { get; set; }
    /// <summary>When the current episode began (a re-open restarts the maximum-duration clock); null = <see cref="OpenedUtc"/>.</summary>
    public DateTime? EpisodeStartUtc { get; set; }
    /// <summary>The events written with this incident (kept so a rewrite never loses the onset).</summary>
    public List<LifecycleEvent> Events { get; set; } = [];
    /// <summary>Every alert and re-open, in order (one incident per tag per episode keeps them all; capped at 500).</summary>
    public List<(DateTime TimeUtc, string Text)> Timeline { get; } = [];
    /// <summary>Per rule (kind + window): the timeline entry its current run of alerts is counted on (2f).</summary>
    public Dictionary<(AlertKind Kind, TimeSpan Window), AlertRun> AlertRuns { get; } = new();
    public DateTime LastWriteUtc { get; set; }
    public DateTime LastRankUtc { get; set; }
    /// <summary>Top suspect and growth as of the last write (rewrites happen only on a material change).</summary>
    public ProcessKey? WrittenTop { get; set; }
    public long WrittenGrowth { get; set; }
    public bool Dirty { get; set; } = true;
    /// <summary>
    /// 2f (review round 2): an alert was counted on an existing timeline entry of a CLOSED incident. Not a material change
    /// (a closed incident is not rewritten on every sample for it), but its folder is rewritten within
    /// <see cref="IncidentManager.MaxRewriteInterval"/> so the count reaches the report too.
    /// </summary>
    public bool CountPending { get; set; }
    /// <summary>Window the last ranking used (for timeline.csv / processes.csv).</summary>
    public CorrelationWindow? Window { get; set; }
    public bool IsOpen => ClosedUtc is null;
    /// <summary>How many times the tag grew again within an hour of closing and re-opened this incident.</summary>
    public int Reopened { get; set; }
}
