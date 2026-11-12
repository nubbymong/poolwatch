using PoolWatch.Core;
using PoolWatch.Core.Lag;
using PoolWatch.Core.Model;
using PoolWatch.Core.Services;
using PoolWatch.Core.Util;
using PoolWatch.Engine;
using PoolWatch.Engine.Setup;

namespace PoolWatch.AppTests;

/// <summary>
/// What PoolWatch keeps on disk (<see cref="Settings.RecordMode"/>). With Reports only, the default, the day's sink makes no
/// data\&lt;yyyyMMdd&gt;\ folder and writes none of its files, whatever it is handed; with Detailed recording it writes them as
/// before; and a change between the two applies at the next write, without a restart: turned on, the day's files start
/// again with the whole picture; turned off, they stop at once and are let go of. The spawn-storm folders and
/// findings.jsonl follow the same choice (read from Diagnostics' code, which needs the whole app to run), and setup.json
/// says whether a mode was chosen (1.0.0-beta.2's does not).
/// </summary>
public sealed class RecordModeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pw-records-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 11, 10, 10, 0, 0, DateTimeKind.Utc);

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private AppPaths Paths() => new(Path.Combine(_root, "data"), null, secure: false);

    /// <summary>The data root's day folders (data\&lt;yyyyMMdd&gt;\ under it).</summary>
    private string DayFolders => Path.Combine(_root, "data", "data");

    private static Sample S(int minute, params ProcessSample[] procs) =>
        new(Now.AddMinutes(minute), default, [new PoolTagEntry(PoolTag.Parse("Leak"), 0, 0, 4UL << 20, 0, 0, 0)], procs) { TickMs = minute * 60_000L };

    private static readonly ProcessSample[] Procs =
        [new() { Pid = 500, ParentPid = 4, CreateTime = 5, ImageName = "app.exe" }, new() { Pid = 600, ParentPid = 4, CreateTime = 6, ImageName = "tool.exe" }];

    private static HandleCounts Census()
    {
        var c = new HandleCounts();
        for (int i = 0; i < 40; i++) c.Add(500, 1);
        for (int i = 0; i < 30; i++) c.Add(4, 1);
        return c;
    }

    private static readonly Dictionary<int, string> Types = new() { [1] = "File" };

    /// <summary>One sample's every kind of write the engine makes, as the sampler makes them, at <paramref name="minute"/>.</summary>
    private static void Everything(CsvSink sink, int minute)
    {
        var s = S(minute, Procs);
        var utc = s.TimeUtc;
        sink.WriteServices([new ServiceEntry("Svc", "A service", 900)], utc);
        sink.WriteVolumes([new VolumeInfo(@"C:\", "NTFS", "", false, "Fixed")], utc);
        sink.WriteEvents([new LifecycleEvent { TimeUtc = utc, Kind = LifecycleKind.Start, Pid = 700, CreateTime = 7, ParentPid = 4, Image = "node.exe", CommandLine = "node x.js" }], utc);
        sink.AddRollup(new RollupParts(null, null, "bash.exe", "git.exe", 30, 30, 4, [4], utc, utc, new Dictionary<string, int> { ["git status"] = 30 }));
        sink.PostHandles(Census(), Types, Procs, utc); // the handle scan's own thread writes it, as the engine hands it over
        sink.WriteLag(new LagProbe(utc, 1.2, 3.4, 0.8, 0, s.Totals) { TickMs = s.TickMs });
        sink.WriteLoad(new LoadPoint(utc, s.TickMs, 0.43, 2.5, 32, false), 1200);
        sink.WriteSample(s, new ProcessRegistry());
        sink.Flush();
    }

    /// <summary>A file's lines, read beside the sink, which may hold it open.</summary>
    private static string[] Lines(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }

    private string DayFile(string name) => Path.Combine(DayFolders, Now.ToLocalTime().ToString("yyyyMMdd"), name);

    [Fact]
    public void ReportsOnlyMakesNoDayFolderAndWritesNoneOfItsFiles()
    {
        var settings = new Settings();
        Assert.Equal(RecordMode.ReportsOnly, settings.RecordMode); // the default
        var log = new List<string>();
        using (var sink = new CsvSink(Paths(), settings, verbose: false, l => { lock (log) log.Add(l); }))
        {
            Assert.False(sink.Recording);
            for (int m = 0; m < 3; m++) Everything(sink, m);
            sink.WriteHandles(Census(), Types, Procs, Now); // the handle writer's own call, made on its thread
            Assert.False(sink.TurnedOn());
            Assert.Equal(0, sink.HandleScansNotWritten);
        }
        Assert.False(Directory.Exists(DayFolders), "Reports only made a day folder");
        Assert.Empty(log); // nothing failed, nothing was turned on or off
        // The control: the same writes with Detailed recording make the day's folder and its files.
        using (var sink = new CsvSink(Paths(), new Settings { RecordMode = RecordMode.Detailed }, verbose: false))
            for (int m = 0; m < 3; m++) Everything(sink, m);
        foreach (string name in new[] { "totals.csv", "tags.csv", "processes.csv", "events.csv", "handles.csv", "services.csv", "volumes.csv", "system-handles.csv", "lag.csv", "load.csv" })
            Assert.True(File.Exists(DayFile(name)), name + " was not written with Detailed recording");
    }

    /// <summary>
    /// Detailed recording turned on while PoolWatch runs: nothing before it was written, and the next write starts the
    /// day's files with the whole picture (every process, the services, the volumes) as a start does; turned off, the
    /// files are let go of at once (another program may then take them whole) and nothing more is added; on again, the
    /// whole picture again.
    /// </summary>
    [Fact]
    public void ASwitchBetweenTheModesAppliesAtTheNextWriteWithoutARestart()
    {
        var settings = new Settings();
        var log = new List<string>();
        var started = DateTime.UtcNow;
        using var sink = new CsvSink(Paths(), settings, verbose: false, l => { lock (log) log.Add(l); });
        // What was logged so far, read under the lock it is written under: the handle scan's writer logs from its own
        // thread (its own sink follows the mode there), also while this thread reads. (Read without the lock, the list
        // changed under the read once: "Collection was modified; enumeration operation may not execute".)
        string[] Logged() { lock (log) return [.. log]; }
        Everything(sink, 0);
        Assert.False(Directory.Exists(DayFolders));
        // What the day's files record and since when (the incidents' reports say it): Reports only, since the sink began.
        var records = sink.Records;
        Assert.False(records.Detailed);
        Assert.InRange(records.SinceUtc, started, DateTime.UtcNow);

        var switched = DateTime.UtcNow;
        settings.RecordMode = RecordMode.Detailed; // Settings' Save, or setup, through LiveSettings
        Assert.Equal(records, sink.Records);       // followed at the next write, not before
        Assert.True(sink.TurnedOn());             // the engine then writes its services and volumes and asks for a handle scan
        Assert.False(sink.TurnedOn());            // once
        Assert.True(sink.Records.Detailed);
        Assert.InRange(sink.Records.SinceUtc, switched, DateTime.UtcNow);
        Everything(sink, 1);
        Assert.Contains(Logged(), l => l.StartsWith("Detailed recording turned on", StringComparison.Ordinal));
        var processes = Lines(DayFile("processes.csv")).Skip(1).Select(Csv.Split).ToList();
        Assert.Equal(["500", "600"], processes.Select(r => r[1]).Order()); // every process: the whole picture
        Assert.Single(Lines(DayFile("services.csv")).Skip(1));
        Assert.Single(Lines(DayFile("volumes.csv")).Skip(1));
        int totals = Lines(DayFile("totals.csv")).Length;
        Assert.Equal(2, totals); // the header and minute 1's row: nothing of minute 0
        // The handle scan is written on its own thread (a scan still waiting when recording is turned off is not written).
        Assert.True(SpinWait.SpinUntil(() => File.Exists(DayFile("handles.csv")), TimeSpan.FromSeconds(10)), "the handle scan was not written");

        var off = DateTime.UtcNow;
        settings.RecordMode = RecordMode.ReportsOnly;
        Everything(sink, 2);
        Assert.Contains(Logged(), l => l.StartsWith("Detailed recording turned off", StringComparison.Ordinal));
        Assert.False(sink.Records.Detailed);
        Assert.InRange(sink.Records.SinceUtc, off, DateTime.UtcNow);
        Assert.Equal(totals, Lines(DayFile("totals.csv")).Length); // nothing added
        // Let go of: each file opens with no sharing at all. The sampler's at once; the handle scan's two by its own thread,
        // which the switch-off does not wait for (HandleCsvWriterTests), as soon as it has seen it.
        foreach (string name in new[] { "totals.csv", "processes.csv", "events.csv", "services.csv", "lag.csv", "load.csv" })
            using (new FileStream(DayFile(name), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        Assert.True(SpinWait.SpinUntil(() =>
        {
            try
            {
                foreach (string name in new[] { "handles.csv", "system-handles.csv" })
                    using (new FileStream(DayFile(name), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                return true;
            }
            catch (IOException) { return false; }
        }, TimeSpan.FromSeconds(10)), "the handle scan's files stayed open");

        settings.RecordMode = RecordMode.Detailed;
        Assert.True(sink.TurnedOn());
        Everything(sink, 3);
        Assert.Equal(totals + 1, Lines(DayFile("totals.csv")).Length);
        // The whole picture again: each process listed once more, at minute 3.
        Assert.Equal(4, Lines(DayFile("processes.csv")).Skip(1).Count());
        Assert.Equal(2, Lines(DayFile("services.csv")).Skip(1).Count());
    }

    /// <summary>
    /// The writers the sink does not own follow the same choice, read from the code (they need the whole app to run): the
    /// findings' findings.jsonl line, a spawn-storm folder's write, whether a storm gets a folder at all, the engine's
    /// whole picture as recording turns on, and what an incident's report may say of the day's events.csv.
    /// </summary>
    [Fact]
    public void TheOtherWritersUnderDataAndTheStormFoldersFollowTheChoice()
    {
        string Src(params string[] path) => File.ReadAllText(Path.Combine([TrayIconsTests.RepoRoot(), "src", "PoolWatch", .. path]));
        string diagnostics = Src("Analysis", "Diagnostics.cs");
        string onFinding = UninstallerTests.OneLine(ScriptCode.CSharp(ScriptCode.CSharpMember(diagnostics, "private void OnFinding(Finding f)").Value));
        Assert.Contains("if (_engine.Recording) { try { string dir = _engine.Paths.EnsureDir(_engine.Paths.CsvDir(DateTime.UtcNow));", onFinding);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(onFinding, @"CsvDir\("));
        string writeStorm = UninstallerTests.OneLine(ScriptCode.CSharp(ScriptCode.CSharpMember(diagnostics, "private void WriteStorm(StormFolder folder, long nowMs)").Value));
        Assert.StartsWith("private void WriteStorm(StormFolder folder, long nowMs) { if (!_engine.Recording) return; try {", writeStorm);
        Assert.Contains("Spawns = new SpawnTracker(engine.Settings, engine.Registry, Git, GitContext(), CwdOf) { FoldersWanted = () => engine.Recording };", diagnostics);
        // Every other write under data\ is the sink's: the day's folder is named in no other code (CsvDir), and the storm
        // folders are written only by WriteStorm.
        var callers = Directory.EnumerateFiles(Path.Combine(TrayIconsTests.RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(ScriptCode.CSharp(File.ReadAllText(f)), @"\bCsvDir\(")).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["AppPaths.cs", "CsvSink.cs", "Diagnostics.cs"], callers);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(ScriptCode.CSharp(diagnostics), @"StormReports\.WriteWithin\("));
        string tick = UninstallerTests.OneLine(ScriptCode.CSharp(ScriptCode.CSharpMember(Src("Engine", "MonitorEngine.cs"), "private void Tick()").Value));
        Assert.Contains("if (_sink.TurnedOn()) { _handleScanRequest.Set(); _sink.WriteServices(Services, now); _sink.WriteVolumes(Volumes, now); }", tick);
        // The incidents' host passes on what the day's sink records and since when (its constructor starts a timer that runs
        // fltmc and WMI, so it is read here, not made); the sink's own state is held in ASwitchBetweenTheModes... above.
        Assert.Contains("public (bool Detailed, DateTime SinceUtc) DayRecords { get { var records = _engine.Records; return (records.Detailed, records.SinceUtc); } }",
            UninstallerTests.OneLine(ScriptCode.CSharp(Src("Analysis", "WindowsIncidentHost.cs"))));
        Assert.Contains("public CsvSink.RecordState Records => _sink.Records;", UninstallerTests.OneLine(ScriptCode.CSharp(Src("Engine", "MonitorEngine.cs"))));
    }

    [Fact]
    public void ASetupJsonFromBeta2HasNoRecordChoiceAndOnceMadeTheChoiceIsKept()
    {
        var paths = Paths();
        var log = new Log(Path.Combine(_root, "test.log"));
        string file = Path.Combine(paths.DataRoot, RecommendationStore.FileName);
        // 1.0.0-beta.2's setup.json: setup completed, a suggestion hidden, and no record choice (it had no such step).
        File.WriteAllText(file, "{\n  \"version\": 1,\n  \"guideCompletedUtc\": \"2026-11-01T09:00:00.0000000Z\",\n" +
                                "  \"dismissed\": {},\n  \"hiddenFeatures\": []\n}");
        var store = new RecommendationStore(paths, log);
        Assert.True(store.GuideCompleted);
        Assert.False(store.RecordsChosen);
        store.MarkRecordsChosen(new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc));
        store.MarkRecordsChosen(new DateTime(2026, 11, 11, 9, 0, 0, DateTimeKind.Utc)); // kept as first made
        var again = new RecommendationStore(paths, log);
        Assert.True(again.GuideCompleted && again.RecordsChosen);
        Assert.Contains("\"recordsChosenUtc\": \"2026-11-10T09:00:00.0000000Z\"", File.ReadAllText(file));
        Assert.Contains("\"guideCompletedUtc\": \"2026-11-01T09:00:00.0000000Z\"", File.ReadAllText(file)); // the rest as it was

        // A new install: neither, until setup.
        File.Delete(file);
        var fresh = new RecommendationStore(paths, log);
        Assert.False(fresh.GuideCompleted || fresh.RecordsChosen);
    }
}
