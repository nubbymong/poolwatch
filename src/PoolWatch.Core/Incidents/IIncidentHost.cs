namespace PoolWatch.Core.Incidents;

/// <summary>Data for the incident timeline chart (rendered to PNG by the Windows host).</summary>
public sealed record TimelineChart(string Title, IReadOnlyList<DateTime> Times, IReadOnlyList<long> TagBytes, IReadOnlyList<long> CommittedPoolBytes,
    string CommittedLabel, IReadOnlyList<(DateTime TimeUtc, string Label)> Markers);

/// <summary>
/// Platform services the incident writer needs. The Windows app implements it with native lookups; unit tests
/// use a fake. Every file the incident manager writes goes through <see cref="CheckWrite"/>.
/// </summary>
public interface IIncidentHost
{
    string IncidentsDir { get; }
    /// <summary>Returns the path if the write policy allows it, otherwise throws. The Windows host's check
    /// (PathGuard.CheckReal) also looks for a junction or symlink on the path, before the write or delete that follows,
    /// and throws when it finds one or cannot check.</summary>
    string CheckWrite(string path);
    FileDetails? DescribeFile(string? path);
    SystemInfo GetSystemInfo();
    /// <summary>Renders the chart; returns false when PNG rendering is unavailable.</summary>
    bool RenderTimeline(TimelineChart chart, string pngPath);
    /// <summary>Asks the collector for a handle scan on the next tick (an alert just fired).</summary>
    void RequestHandleScan();
    /// <summary>Asks for the owner of tags that are not in pooltag.txt (bounded driver scan, off the sampler thread).</summary>
    void RequestTagScan(IReadOnlyCollection<PoolTag> tags);
    string DisplayUser(string user);
    /// <summary>AICC / Claude Code context for a suspect, or null when it is not under AICC or Claude Code. Never blocks:
    /// a transcript search runs in the background and the incident is rewritten when it finishes.</summary>
    SessionDetails? DescribeSession(Correlation.Suspect suspect);
    void Log(string message);
    /// <summary>
    /// Whether the day's files are recorded now (Detailed recording, <see cref="Settings.RecordMode"/>), and since when
    /// without a change: a report says that the day's data\&lt;yyyyMMdd&gt;\events.csv holds the events its own events.csv
    /// left out only for a time Detailed recording was on throughout, and that they are not recorded only for a time
    /// Reports only was. Default: recorded since ever (the tests' hosts); the Windows host says what the day's files do.
    /// </summary>
    (bool Detailed, DateTime SinceUtc) DayRecords => (true, DateTime.MinValue);
}
