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

/// <summary>
/// What happened when a tag dropped sharply: that explains what was holding the memory. <see cref="FromBytes"/> and
/// <see cref="ToBytes"/> are the tag's real levels; PoolWatch's own cache clears in between freed
/// <see cref="ClearedBytes"/> of it, and <see cref="Dropped"/> is what fell on its own (the clears' drop is their result).
/// </summary>
public sealed record ReleaseRecord(DateTime TimeUtc, DateTime FromUtc, long FromBytes, long ToBytes, List<string> Exits, List<string> ActiveProcesses,
    string? WorktreeNote = null)
{
    /// <summary>What PoolWatch's own cache clears between <see cref="FromUtc"/> and <see cref="TimeUtc"/> freed of the tag (0 when none did).</summary>
    public long ClearedBytes { get; init; }
    /// <summary>When the one own clear that freed part of it ran; null when none did or several did.</summary>
    public DateTime? ClearedAtUtc { get; init; }
    public long Dropped => FromBytes - ToBytes - ClearedBytes;
}

/// <summary>AICC / Claude Code context of a suspect (DESIGN.md section 9).</summary>
public sealed record SessionDetails(string Chain, Aicc.SessionContext? Context, Aicc.TranscriptHit? Transcript, bool SearchPending);

/// <summary>The tag grew during a burst and had not fallen N minutes after the correlated activity stopped.</summary>
public sealed record RetentionRecord(DateTime TimeUtc, DateTime ActivityStoppedUtc, long RetainedBytes, string Note);

/// <summary>
/// A rule that keeps firing (a growth rule fires on every sample while it holds): its alerts after the first are counted on
/// the first one's timeline entry ("...; fired 37 more times to 06:54:10, latest +612 MB"), so every alert is on the
/// timeline without one line per sample.
/// </summary>
public sealed class AlertRun
{
    public DateTime EntryTimeUtc { get; init; }
    public required string EntryText { get; init; }
    public DateTime LastUtc { get; set; }
    /// <summary>The run's last alert on the clock that only goes forward (<see cref="Alert.Ms"/>): the gap that starts a new run is timed on it.</summary>
    public long LastMs { get; set; }
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
    /// <summary>
    /// The measurement of PoolWatch's own cache clear that ran while the incident was rising: its quiet period counts from
    /// here when that is later than <see cref="LastRiseUtc"/> (the clear's interval hid the rise; it did not stop), while
    /// <see cref="LastRiseUtc"/> stays the last rise seen. Null when no such clear ran this episode.
    /// </summary>
    public DateTime? QuietFromUtc { get; set; }
    /// <summary>When the quiet period counts from: the later of <see cref="LastRiseUtc"/> and <see cref="QuietFromUtc"/>.</summary>
    public DateTime QuietSinceUtc => QuietFromUtc is { } q && q > LastRiseUtc ? q : LastRiseUtc;
    public long HighestSeenBytes { get; set; }
    public DateTime? ClosedUtc { get; set; }
    public DateTime OnsetUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    /// <summary>The tag's level at the onset.</summary>
    public long StartBytes { get; set; }
    /// <summary>
    /// The start plus the growth's highest point: the level the tag reached, or, when PoolWatch's own cache clears freed part
    /// of it meanwhile, would have reached had they freed nothing (their drop is not the growth ending).
    /// </summary>
    public long PeakBytes { get; set; }
    /// <summary>The tag's level at the last sample ranked.</summary>
    public long CurrentBytes { get; set; }
    /// <summary>What PoolWatch's own cache clears freed of the tag since the onset (0 when none did).</summary>
    public long ClearedBytes { get; set; }
    /// <summary>When the one own clear that freed part of it since the onset ran; null when none did or several did.</summary>
    public DateTime? ClearedAtUtc { get; set; }
    /// <summary>
    /// Each of PoolWatch's own clears that freed part of the tag while the incident was held, as it was measured (when, on the
    /// forward clock; when it ran; what it freed of the tag): an incident open longer than the raw history still counts those
    /// that left it in its growth and in <see cref="ClearedBytes"/>.
    /// </summary>
    internal List<(long Ms, DateTime? Utc, long Drop)> ClearDrops { get; } = [];
    /// <summary>"PoolWatch's own cache clear at 06:20:01 freed 413.3 MB" when its own clears freed part of the tag since the onset, else null.</summary>
    public string? OwnClearsFreed => ClearedBytes > 0 ? Fmt.OwnClearsFreed(ClearedBytes, ClearedAtUtc) : null;
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
    /// <summary>Per rule (kind + window): the timeline entry its current run of alerts is counted on.</summary>
    public Dictionary<(AlertKind Kind, TimeSpan Window), AlertRun> AlertRuns { get; } = new();
    public DateTime LastWriteUtc { get; set; }
    public DateTime LastRankUtc { get; set; }
    /// <summary>Top suspect and growth as of the last write (rewrites happen only on a material change).</summary>
    public ProcessKey? WrittenTop { get; set; }
    public long WrittenGrowth { get; set; }
    public bool Dirty { get; set; } = true;
    /// <summary>
    /// An alert was counted on an existing timeline entry of a CLOSED incident. Not a material change
    /// (a closed incident is not rewritten on every sample for it), but its folder is rewritten within
    /// <see cref="IncidentManager.MaxRewriteInterval"/> so the count reaches the report too.
    /// </summary>
    public bool CountPending { get; set; }

    // The same moments on the clock that only goes forward (milliseconds since Windows started, the samples'
    // Sample.TickMs): the quiet period, the maximum duration, the re-rank and rewrite intervals, the protected window, the 6
    // hours a closed incident is kept, a re-open's hour, the alert runs, the durations a report says and the events and
    // samples picked for its windows are timed on these, so setting the system clock back or forward neither holds them
    // back nor brings them early. The times of day beside them are shown and written.

    /// <summary><see cref="OpenedUtc"/> on the forward clock.</summary>
    public long OpenedMs { get; init; }
    /// <summary><see cref="LastRiseUtc"/> on the forward clock.</summary>
    public long LastRiseMs { get; set; }
    /// <summary><see cref="QuietFromUtc"/> on the forward clock.</summary>
    public long? QuietFromMs { get; set; }
    /// <summary><see cref="QuietSinceUtc"/> on the forward clock: the later of <see cref="LastRiseMs"/> and <see cref="QuietFromMs"/>.</summary>
    public long QuietSinceMs => QuietFromMs is { } q && q > LastRiseMs ? q : LastRiseMs;
    /// <summary><see cref="ClosedUtc"/> on the forward clock (null while it is open).</summary>
    public long? ClosedMs { get; set; }
    /// <summary><see cref="OnsetUtc"/> on the forward clock.</summary>
    public long OnsetMs { get; set; }
    /// <summary><see cref="EndUtc"/> on the forward clock (null while the growth has not ended).</summary>
    public long? EndMs { get; set; }
    /// <summary><see cref="LastAlertUtc"/> on the forward clock.</summary>
    public long LastAlertMs { get; set; }
    /// <summary><see cref="EpisodeStartUtc"/> on the forward clock; null = <see cref="OpenedMs"/>.</summary>
    public long? EpisodeStartMs { get; set; }
    /// <summary><see cref="LastWriteUtc"/> on the forward clock.</summary>
    public long LastWriteMs { get; set; }
    /// <summary><see cref="LastRankUtc"/> on the forward clock: when the episode was last measured.</summary>
    public long LastRankMs { get; set; }
    /// <summary>When the suspects were last ranked, on the forward clock (null: not yet, or due at the next re-rank).</summary>
    public long? SuspectsRankMs { get; set; }
    /// <summary>Window the last ranking used (for timeline.csv / processes.csv).</summary>
    public CorrelationWindow? Window { get; set; }
    public bool IsOpen => ClosedUtc is null;
    /// <summary>
    /// How long the growth went on, on the forward clock: from its onset to its end, else to the close, else to the last
    /// alert (the "grew 1.2 GB in 40 min" of the headline and the report).
    /// </summary>
    public TimeSpan GrowthSpan => TimeSpan.FromMilliseconds((EndMs ?? ClosedMs ?? LastAlertMs) - OnsetMs);
    /// <summary>How many times the tag grew again within an hour of closing and re-opened this incident.</summary>
    public int Reopened { get; set; }
}
