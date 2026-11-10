using PoolWatch.Core;
using PoolWatch.Core.History;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;

namespace PoolWatch.Tests;

/// <summary>
/// What PoolWatch keeps on disk, as settings.json holds it (<see cref="Settings.RecordMode"/>): a file without it, as every
/// 1.0.0-beta.2 install and a new one have, is Reports only; and what an incident's report says of the day's events.csv
/// follows the mode it was written in (the file is there only with Detailed recording).
/// </summary>
public sealed class RecordModeSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pw-recordmode-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>1.0.0-beta.2's settings.json, as its Save wrote it (every setting it had, none about records).</summary>
    private const string Beta2Settings = """
        {
          "settingsVersion": 4,
          "sampleIntervalSeconds": 10,
          "handleIntervalSeconds": 300,
          "autoClearEnabled": true,
          "autoClearPoolGb": 8,
          "autoClearEveryHours": 1,
          "tokenGuardEnabled": true,
          "checkForUpdates": true,
          "poolNotices": "EveryGrowth",
          "retentionDays": 7,
          "redactCopies": true
        }
        """;

    [Fact]
    public void ASettingsFileWithoutTheKeyIsReportsOnly()
    {
        Assert.Equal(RecordMode.ReportsOnly, new Settings().RecordMode);
        Assert.Equal(RecordMode.ReportsOnly, Settings.Parse("{}").RecordMode);
        var beta2 = Settings.Parse(Beta2Settings);
        Assert.Equal(RecordMode.ReportsOnly, beta2.RecordMode);
        Assert.True(beta2.AutoClearEnabled && beta2.TokenGuardEnabled); // the rest as written
        Assert.Equal(7, beta2.RetentionDays);
        Assert.Equal(RecordMode.ReportsOnly, Settings.Parse(Beta2Settings, explicitConfig: true).RecordMode); // a --config file too
    }

    [Fact]
    public void TheModeIsReadByNameOrNumberAndAnythingElseIsReportsOnly()
    {
        Assert.Equal(RecordMode.Detailed, Settings.Parse("""{ "recordMode": "Detailed" }""").RecordMode);
        Assert.Equal(RecordMode.Detailed, Settings.Parse("""{ "recordMode": " detailed " }""").RecordMode);
        Assert.Equal(RecordMode.Detailed, Settings.Parse("""{ "recordMode": 1 }""").RecordMode);
        Assert.Equal(RecordMode.ReportsOnly, Settings.Parse("""{ "recordMode": "ReportsOnly" }""").RecordMode);
        // A name this build does not know, a number out of range, an object: Reports only, and the rest still loads.
        foreach (string odd in new[] { "\"Everything\"", "7", "-1", "{ \"x\": 1 }", "[1]", "true", "null" })
        {
            var s = Settings.Parse($$"""{ "recordMode": {{odd}}, "retentionDays": 30 }""");
            Assert.Equal(RecordMode.ReportsOnly, s.RecordMode);
            Assert.Equal(30, s.RetentionDays);
        }
        // Written by name, and read back the same.
        var detailed = new Settings { RecordMode = RecordMode.Detailed, RetentionDays = 14 };
        Assert.Contains("\"recordMode\": \"Detailed\"", detailed.ToJson());
        var back = Settings.Parse(detailed.ToJson());
        Assert.Equal((RecordMode.Detailed, 14), (back.RecordMode, back.RetentionDays));
        // The cleanup keeps its range and its default, for the reports, the records and the exports alike.
        Assert.Equal(1, Settings.Parse("""{ "retentionDays": 0 }""").RetentionDays);
        Assert.Equal(90, Settings.Parse("""{ "retentionDays": 365 }""").RetentionDays);
        Assert.Equal(7, new Settings().RetentionDays);
    }

    /// <summary>A host for the incident writer whose day's files record or not (Reports only), since a given time.</summary>
    private sealed class ReportsOnlyHost(string dir, bool detailed, DateTime since) : IIncidentHost
    {
        public string IncidentsDir { get; } = dir;
        public string CheckWrite(string path) => path.StartsWith(IncidentsDir, StringComparison.Ordinal) ? path : throw new UnauthorizedAccessException(path);
        public FileDetails? DescribeFile(string? path) => null;
        public SystemInfo GetSystemInfo() => new() { Machine = "TEST", OsDescription = "Windows 11 Pro 25H2", OsBuild = "26200.1", RamBytes = 64L << 30, LogicalCpus = 16 };
        public bool RenderTimeline(TimelineChart chart, string pngPath) { File.WriteAllBytes(pngPath, [0x89, 0x50]); return true; }
        public void RequestHandleScan() { }
        public void RequestTagScan(IReadOnlyCollection<PoolTag> tags) { }
        public string DisplayUser(string user) => user;
        public SessionDetails? DescribeSession(PoolWatch.Core.Correlation.Suspect suspect) => null;
        public void Log(string message) { }
        public (bool Detailed, DateTime SinceUtc) DayRecords { get; } = (detailed, since);
    }

    private static readonly DateTime T0 = new(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);

    private Incident NewIncident(string name) => new()
    {
        Id = name, Dir = Path.Combine(_dir, name), Tag = PoolTag.Parse("File"), Kind = IncidentKind.Growth,
        OpenedUtc = T0, OpenedMs = 1_000_000, OnsetUtc = T0, OnsetMs = 1_000_000,
    };

    private static List<LifecycleEvent> Events(int count) => Enumerable.Range(0, count).Select(i => new LifecycleEvent
    {
        TimeUtc = T0.AddSeconds(i), Ms = 1_000_000 + i * 1000L, Kind = LifecycleKind.Start, Pid = 10_000 + i, CreateTime = T0.AddSeconds(i).ToFileTimeUtc(),
        ParentPid = 4, Image = "node.exe", CommandLine = $"node build.js --step {i}",
    }).ToList();

    /// <summary>
    /// With Reports only a growth incident's folder is written as before (its report and its events.csv), and its report
    /// does not name the day's data\&lt;yyyyMMdd&gt;\events.csv, which is not there. With Detailed recording it names it, as
    /// before. After a switch in the middle of the incident, it says what the day's file holds of the window's events from
    /// its start: all of them when Detailed recording was on since before then, otherwise those of the time it was on. The
    /// last row of an open incident's full first part is a fact about the file alone, in every mode: it names no day file.
    /// </summary>
    [Fact]
    public void AReportWrittenWithReportsOnlyNamesNoDayFile()
    {
        // The window's 1,500 events are a second apart from its start; the incident keeps the first 1,000, to 16 min 39 s in.
        var cases = new (string Name, bool Detailed, DateTime Since, string? Report)[]
        {
            ("1", false, DateTime.MinValue, null),
            ("2", true, DateTime.MinValue, @"and the day's `data\<yyyyMMdd>\events.csv` has every one"),
            // Detailed recording turned on 10 minutes into the window: not all of the window's events are in the day's file.
            ("3", true, T0.AddMinutes(10), @"and the day's `data\<yyyyMMdd>\events.csv` has those of the time Detailed recording was on"),
            // Turned off 20 minutes in: the day's file has the window's first 20 minutes only.
            ("4", false, T0.AddMinutes(20), @"and the day's `data\<yyyyMMdd>\events.csv` has those of the time Detailed recording was on"),
        };
        foreach (var c in cases)
        {
            var writer = new IncidentWriter(new TagKnowledge(null, null), new ReportsOnlyHost(_dir, c.Detailed, c.Since), new ProcessRegistry());
            var inc = NewIncident($"20261110-090000-File-656c6946-{c.Name}");
            var events = Events(1500);
            writer.Write(new History(), inc, events, maxRows: 300); // the window's first 300; the rest left for later
            string report = File.ReadAllText(Path.Combine(inc.Dir, "report.md"));
            Assert.True(File.Exists(Path.Combine(inc.Dir, "report.json")));
            Assert.Contains("events.csv gets the rest of the window's first 1,000 events as PoolWatch's write budget allows", report);
            if (c.Report is { } said) Assert.Contains(said, report);
            else Assert.DoesNotContain(@"data\<yyyyMMdd>\events.csv", report);
            writer.AppendEvents(inc, events, maxRows: 10_000); // up to the 1,000 it keeps, then the row that says so
            string last = File.ReadAllLines(Path.Combine(inc.Dir, "events.csv"))[^1];
            Assert.Contains(",Note,", last);
            Assert.Contains("events.csv holds the incident window's first 1,000 events, to this row's time; the window's later events are not in this file", last);
            Assert.DoesNotContain(@"data\", last);
        }
    }
}
