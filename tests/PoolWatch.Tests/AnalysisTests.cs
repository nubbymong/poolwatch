using System.Text.Json;
using PoolWatch.Core;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Detection;
using PoolWatch.Core.History;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;

namespace PoolWatch.Tests;

/// <summary>Builds a synthetic world: a tag series plus processes with scripted I/O, quota and handles.</summary>
internal sealed class World
{
    public static readonly DateTime T0 = new(2026, 10, 20, 6, 0, 0, DateTimeKind.Utc);
    public readonly History History = new();
    public readonly ProcessRegistry Registry = new();
    public readonly Dictionary<int, ProcessSample> Procs = new();
    private readonly Dictionary<int, Dictionary<string, int>> _handles = new();
    public int Index;
    public TimeSpan Interval = TimeSpan.FromSeconds(2);
    public DateTime Now => T0 + Interval * Index;

    public ProcessSample AddProcess(int pid, int ppid, string image, int startIndex = -1000)
    {
        var p = new ProcessSample { Pid = pid, ParentPid = ppid, ImageName = image, CreateTime = (T0 + Interval * startIndex).ToFileTimeUtc() };
        Procs[pid] = p;
        return p;
    }

    public void Io(int pid, long other = 0, long read = 0, int handles = 0, long quota = 0)
    {
        var p = Procs[pid];
        Procs[pid] = p with
        {
            OtherOperationCount = p.OtherOperationCount + other, ReadOperationCount = p.ReadOperationCount + read,
            HandleCount = p.HandleCount + handles, QuotaNonPagedPoolUsage = p.QuotaNonPagedPoolUsage + (ulong)quota,
        };
        if (handles != 0)
        {
            if (!_handles.TryGetValue(pid, out var d)) _handles[pid] = d = new();
            d["File"] = d.GetValueOrDefault("File") + handles;
        }
    }

    public void Exit(int pid) => Procs.Remove(pid);

    public void Sample(params (string tag, long bytes)[] tags)
    {
        var entries = tags.Select(t => new PoolTagEntry(PoolTag.Parse(t.tag), 0, 0, 0, 1, 0, (ulong)t.bytes)).ToList();
        entries.Add(new PoolTagEntry(PoolTag.Parse("Toke"), 0, 0, 0, 1, 0, 2_000_000));
        var s = new Sample(Now, new PoolTotals(400_000_000, 300_000_000, 350_000_000, 250_000_000), entries, Procs.Values.ToList());
        Registry.ObserveSnapshot(s.Processes, Now);
        History.Add(s);
        Index++;
    }

    public void ScanHandles()
    {
        var by = Procs.Values.ToDictionary(p => p.Key, p => new Dictionary<string, int>(_handles.GetValueOrDefault(p.Pid) ?? new()));
        History.AddHandles(new TypedHandles(T0 + Interval * (Index - 1), by)); // same tick as the last sample, as in the engine
    }
}

public class DetectionTests
{
    private static Settings Ci() => new()
    {
        SampleIntervalSeconds = 2, GrowthRules = [new(1, 3 * Settings.MB)], ReleaseBytes = 2 * Settings.MB, ReleaseWindowMinutes = 1,
    };

    [Fact]
    public void GrowthAlertWithOnsetAndReleaseAfterDrop()
    {
        var w = new World();
        var d = new Detector(Ci());
        long v = 10 * Settings.MB;
        var alerts = new List<Alert>();
        for (int i = 0; i < 20; i++) { w.Sample(("Reff", v)); alerts.AddRange(d.Evaluate(w.History)); }
        Assert.Empty(alerts);
        for (int i = 0; i < 20; i++) { v += 400_000; w.Sample(("Reff", v)); alerts.AddRange(d.Evaluate(w.History)); }
        var growth = alerts.Where(a => a.Kind == AlertKind.Growth).ToList();
        Assert.NotEmpty(growth);
        Assert.All(growth, a => Assert.Equal(PoolTag.Parse("Reff"), a.Tag));
        Assert.InRange(growth[0].OnsetUtc, World.T0.AddSeconds(30), World.T0.AddSeconds(42));
        Assert.Equal(TagState.Growing, d.Status[PoolTag.Parse("Reff")].State);

        alerts.Clear();
        for (int i = 0; i < 5; i++) { v -= 1_000_000; w.Sample(("Reff", v)); alerts.AddRange(d.Evaluate(w.History)); }
        Assert.Single(alerts, a => a.Kind == AlertKind.Release);
        Assert.Equal(TagState.Released, d.Status[PoolTag.Parse("Reff")].State);
    }

    [Fact]
    public void TagsAreCaseSensitive()
    {
        var w = new World();
        var d = new Detector(Ci());
        var alerts = new List<Alert>();
        for (int i = 0; i < 40; i++) { w.Sample(("Reff", 10 * Settings.MB + i * 500_000L), ("RefF", 10 * Settings.MB)); alerts.AddRange(d.Evaluate(w.History)); }
        Assert.Contains(alerts, a => a.Tag == PoolTag.Parse("Reff"));
        Assert.DoesNotContain(alerts, a => a.Tag == PoolTag.Parse("RefF"));
    }

    [Fact]
    public void ChangePointsFindOnsetAndEnd()
    {
        var s = Enumerable.Repeat(100L, 10).Concat(Enumerable.Range(1, 10).Select(i => 100L + i * 50)).Concat(Enumerable.Repeat(600L, 10)).ToList();
        var cp = Detector.FindChangePoints(s);
        Assert.InRange(cp.OnsetIndex, 9, 10);
        Assert.NotNull(cp.EndIndex);
        Assert.InRange(cp.EndIndex!.Value, 18, 20);
        Assert.Equal(500, cp.Rise);
        Assert.Null(Detector.FindChangePoints(s.Take(20).ToList()).EndIndex); // still climbing
    }
}

public class ReviewRegressionTests
{
    [Fact]
    public void NewProcessAtMinuteBoundaryDoesNotThrow()
    {
        var w = new World { Interval = TimeSpan.FromSeconds(10) };
        w.AddProcess(4, 0, "System");
        for (int i = 0; i < 5; i++) w.Sample(("File", 1 << 24)); // 06:00:00 .. 06:00:40
        w.AddProcess(77, 4, "late.exe", startIndex: w.Index);  // first seen in the 06:01:00 sample? no: 06:00:50
        w.Sample(("File", 1 << 24));                            // 06:00:50
        w.AddProcess(78, 4, "boundary.exe", startIndex: w.Index);
        w.Sample(("File", 1 << 24));                            // 06:01:00 closes the first minute
        w.Sample(("File", 1 << 24));
        Assert.Single(w.History.Minutes);
        Assert.DoesNotContain(new ProcessKey(78, (World.T0 + w.Interval * 6).ToFileTimeUtc()), w.History.Minutes[0].Processes);
    }

    [Fact]
    public void FlatNonPagedTotalAboveThresholdDoesNotFloodIncidents()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pw-np-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new Settings { NonPagedTotalAlertBytes = 5L << 30 };
            var w = new World { Interval = TimeSpan.FromSeconds(10) };
            var host = new FakeHost(dir);
            var mgr = new IncidentManager(settings, new TagKnowledge(null, null), host, w.Registry);
            var det = new Detector(settings);
            w.AddProcess(4, 0, "System");
            var rnd = new Random(1);
            for (int i = 0; i < 720; i++) // 2 hours at ~6 GB with noise
            {
                long total = (6L << 30) + rnd.Next(-20, 20) * (1L << 20);
                var tags = Enumerable.Range(0, 50).Select(k => ($"T{k:D3}", 50L * (1 << 20) + rnd.Next(-200_000, 200_000))).ToArray();
                var entries = tags.Select(t => new PoolTagEntry(PoolTag.Parse(t.Item1), 0, 0, 0, 1, 0, (ulong)t.Item2)).ToList();
                var s = new Sample(w.Now, new PoolTotals(1L << 30, (ulong)total, 1L << 30, (ulong)total), entries, w.Procs.Values.ToList());
                w.Registry.ObserveSnapshot(s.Processes, w.Now);
                w.History.Add(s);
                w.Index++;
                mgr.Process(w.History, det, det.Evaluate(w.History), s.TimeUtc);
            }
            Assert.Empty(mgr.All);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

public class CorrelationTests
{
    [Fact]
    public void SpearmanHandlesTiesAndLag()
    {
        Assert.Equal(1.0, Stats.Spearman([1, 2, 3, 4, 5], [10, 20, 30, 40, 50]), 6);
        Assert.Equal(-1.0, Stats.Spearman([1, 2, 3, 4, 5], [5, 4, 3, 2, 1]), 6);
        Assert.True(double.IsNaN(Stats.Spearman([1, 1, 1, 1], [1, 2, 3, 4])));
        // y leads x by 2 samples
        double[] y = [0, 5, 0, 7, 0, 9, 1, 3, 0, 8];
        var x = new double[10];
        for (int i = 2; i < 10; i++) x[i] = y[i - 2];
        Assert.Equal(1.0, Stats.SpearmanLagged(x, y, 2), 6);
    }

    /// <summary>Scenario 1 in miniature: one process opens File handles while the File tag grows; another is busier on I/O.</summary>
    [Fact]
    public void DirectChargeBeatsBusierBystander()
    {
        var w = new World();
        w.AddProcess(4, 0, "System");
        w.AddProcess(100, 4, "leaker.exe");
        w.AddProcess(200, 4, "busy.exe");
        long file = 50 * Settings.MB;
        for (int i = 0; i < 15; i++) { w.Io(200, other: 5000); w.Sample(("File", file)); }
        w.ScanHandles();
        int onset = w.Index;
        for (int i = 0; i < 20; i++)
        {
            w.Io(100, other: 2000, handles: 2000, quota: 20_000);
            w.Io(200, other: 5000 + (i % 3) * 700);
            file += 800_000;
            w.Sample(("File", file));
            if (i % 5 == 4) w.ScanHandles();
        }
        var window = new CorrelationWindow(World.T0, World.T0 + w.Interval * onset, null, w.Now);
        var ranked = Correlator.Rank(w.History, w.Registry, PoolTag.Parse("File"), window);
        var top = ranked[0];
        Assert.Equal("leaker.exe", top.Image);
        Assert.True(top.Direct.Present);
        Assert.Equal(40_000, top.Direct.HandleDelta);
        Assert.Equal(Confidence.StronglyCorrelated, top.Confidence);
        Assert.Contains(top.Evidence, e => e.StartsWith("direct charge: File handles"));
        Assert.False(ranked.Single(s => s.Image == "busy.exe").Direct.Present);
    }

    /// <summary>Scenario 2 in miniature: no handle mapping (Ntff); the process whose I/O tracks the growth wins.</summary>
    [Fact]
    public void ActivityCorrelationRanksTheChurner()
    {
        var w = new World();
        w.AddProcess(4, 0, "System");
        w.AddProcess(100, 4, "churn.exe");
        w.AddProcess(200, 4, "steady.exe");
        long fcb = 20 * Settings.MB;
        var rnd = new Random(7);
        for (int i = 0; i < 15; i++) { w.Io(200, read: 300 + rnd.Next(100)); w.Sample(("Ntff", fcb)); }
        int onset = w.Index;
        for (int i = 0; i < 25; i++)
        {
            int ops = 3000 + rnd.Next(2000);
            w.Io(100, other: ops);
            w.Io(200, read: 300 + rnd.Next(100));
            fcb += ops * 300;
            w.Sample(("Ntff", fcb));
        }
        var ranked = Correlator.Rank(w.History, w.Registry, PoolTag.Parse("Ntff"), new CorrelationWindow(World.T0, World.T0 + w.Interval * onset, null, w.Now));
        var top = ranked[0];
        Assert.Equal("churn.exe", top.Image);
        Assert.True(top.Activity.Present);
        Assert.True(top.Activity.Best > 0.7, $"spearman {top.Activity.Best}");
        Assert.True(top.Activity.IoShare > 0.8);
        Assert.Equal(Confidence.Correlated, top.Confidence);
        Assert.False(ranked.Single(s => s.Image == "steady.exe").Activity.Present);
    }

    /// <summary>Scenario 3 in miniature: the parent exits, the child keeps working: orphan + lifecycle.</summary>
    [Fact]
    public void BusyOrphanIsStronglyCorrelated()
    {
        var w = new World();
        w.AddProcess(4, 0, "System");
        w.AddProcess(50, 4, "bash.exe");
        long tag = 30 * Settings.MB;
        for (int i = 0; i < 15; i++) w.Sample(("RefF", tag));
        w.AddProcess(60, 50, "find.exe", startIndex: w.Index);
        w.Sample(("RefF", tag));
        w.Exit(50);
        int onset = w.Index;
        w.Interval = TimeSpan.FromSeconds(10); // the orphan rule waits 30 s after the parent's exit
        var rnd = new Random(3);
        for (int i = 0; i < 25; i++)
        {
            int ops = 2000 + rnd.Next(3000);
            w.Io(60, other: ops);
            tag += ops * 200;
            w.Sample(("RefF", tag));
        }
        var ranked = Correlator.Rank(w.History, w.Registry, PoolTag.Parse("RefF"), new CorrelationWindow(World.T0, World.T0 + w.Interval * onset, null, w.Now));
        var top = ranked[0];
        Assert.Equal("find.exe", top.Image);
        Assert.True(top.IsOrphan);
        Assert.True(top.Lifecycle.OrphanActive);
        Assert.True(top.Lifecycle.StartedBeforeOnset);
        Assert.Equal(Confidence.StronglyCorrelated, top.Confidence);
        Assert.Contains(top.Evidence, e => e.Contains("orphaned"));
        Assert.Equal(["bash.exe", "find.exe"], top.Ancestry);
    }
}

internal sealed class FakeHost : IIncidentHost
{
    public FakeHost(string dir) => IncidentsDir = dir;
    public string IncidentsDir { get; }
    public List<string> Logs { get; } = [];
    public int HandleScans, TagScans;
    public string CheckWrite(string path) => path.StartsWith(IncidentsDir) ? path : throw new UnauthorizedAccessException(path);
    public FileDetails? DescribeFile(string? path) => new("1.2.3", "Contoso", "Leaker", null, "Contoso Ltd", "valid embedded signature");
    public SystemInfo GetSystemInfo() => new() { Machine = "TEST", OsDescription = "Windows 11 Pro 24H2", OsBuild = "26100.1", RamBytes = 64L << 30, LogicalCpus = 16 };
    public bool RenderTimeline(TimelineChart chart, string pngPath) { File.WriteAllBytes(pngPath, [0x89, 0x50]); return true; }
    public void RequestHandleScan() => HandleScans++;
    public void RequestTagScan(IReadOnlyCollection<PoolTag> tags) => TagScans++;
    public string DisplayUser(string user) => user;
    public SessionDetails? Session { get; set; }
    public SessionDetails? DescribeSession(PoolWatch.Core.Correlation.Suspect suspect) => Session;
    public void Log(string message) => Logs.Add(message);
}

public class IncidentTests
{
    [Fact]
    public void GrowthOpensOneIncidentClosesAndWritesReport()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pw-incidents-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new Settings { GrowthRules = [new(1, 3 * Settings.MB)], IncidentQuietMinutes = 0.5, RetentionMinutes = 0.5, ReleaseBytes = 2 * Settings.MB, ReleaseWindowMinutes = 1 };
            var w = new World();
            var host = new FakeHost(dir);
            var mgr = new IncidentManager(settings, new TagKnowledge(null, null), host, w.Registry) { RewriteInterval = TimeSpan.Zero };
            var det = new Detector(settings);
            w.AddProcess(4, 0, "System");
            w.AddProcess(100, 4, "leaker.exe");
            long file = 50 * Settings.MB;
            void Step()
            {
                w.Sample(("File", file));
                mgr.Process(w.History, det, det.Evaluate(w.History), w.Now - w.Interval);
            }
            for (int i = 0; i < 15; i++) Step();
            w.ScanHandles();
            for (int i = 0; i < 20; i++) { w.Io(100, other: 2000, handles: 2000); file += 800_000; Step(); if (i % 5 == 4) w.ScanHandles(); }
            Assert.Single(mgr.Open);
            Assert.Equal(1, host.HandleScans);
            for (int i = 0; i < 25; i++) Step(); // quiet: closes after 30 s, then retention after another 30 s of no I/O
            Assert.True(mgr.Open.Count == 0, string.Join(";", mgr.Open.Select(o => $"{o.Tag} rise={o.LastRiseUtc:HH:mm:ss} alert={o.LastAlertUtc:HH:mm:ss} now={w.Now:HH:mm:ss}")));
            var inc = Assert.Single(mgr.Closed);
            Assert.NotNull(inc.Retention);
            file -= 16 * Settings.MB; // handles closed
            for (int i = 0; i < 3; i++) Step();
            Assert.NotNull(inc.Release);
            mgr.Finish(w.History, w.Now);

            var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(inc.Dir, "report.json"))).RootElement;
            Assert.Equal("File", json.GetProperty("tag").GetString());
            Assert.Equal("leaker.exe", json.GetProperty("suspects")[0].GetProperty("image").GetString());
            Assert.True(json.GetProperty("suspects")[0].GetProperty("direct").GetProperty("present").GetBoolean());
            Assert.Equal(JsonValueKind.Object, json.GetProperty("release").ValueKind);
            var md = File.ReadAllText(Path.Combine(inc.Dir, "report.md"));
            Assert.Contains("top suspect leaker.exe PID 100", md);
            Assert.Contains("## Draft bug report for Contoso", md);
            Assert.Contains("**Retained**", md);
            foreach (var f in new[] { "timeline.csv", "processes.csv", "events.csv", "timeline.png" }) Assert.True(File.Exists(Path.Combine(inc.Dir, f)), f);
            Assert.Matches(@"^\d{8}-\d{6}-File-656c6946$", inc.Id);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void SinceBootRanksTagsAndAttributesHours()
    {
        var w = new World { Interval = TimeSpan.FromSeconds(10) };
        w.AddProcess(4, 0, "System");
        w.AddProcess(100, 4, "crawler.exe");
        long tag = 100 * Settings.MB;
        for (int i = 0; i < 400; i++) { w.Io(100, other: 1000); tag += 100_000; w.Sample(("Reff", tag), ("Ntff", 5 * Settings.MB)); }
        var tags = SinceBootReport.Build(w.History, w.Registry, new TagKnowledge(null, null));
        Assert.Equal(PoolTag.Parse("Reff"), tags[0].Tag);
        Assert.True(tags[0].GrowthObserved > 30 * Settings.MB);
        var hour = tags[0].Hours.First(h => h.Growth > 0);
        Assert.Equal("crawler.exe", hour.Attribution[0].Process);
        var md = SinceBootReport.ToMarkdown(tags, null, w.History.Minutes[0].TimeUtc, w.Now);
        Assert.Contains("`Reff` hour by hour", md);
    }
}

public class TagKnowledgeTests
{
    [Fact]
    public void ParsesPoolTagTxtCaseSensitively()
    {
        var map = PoolTagTxt.Parse([
            "// comment",
            "Reff - refs.sys    - FCB_DATA",
            "RefF - refs.sys    - FCB_INDEX",
            "Key  - <unknown>   - Key objects",
            "File - nt!io       - File objects",
            "File - other.sys   - also File",
        ]);
        Assert.Equal("FCB_DATA", map[PoolTag.Parse("Reff")].Description);
        Assert.Equal("FCB_INDEX", map[PoolTag.Parse("RefF")].Description);
        Assert.Equal("Key objects", map[PoolTag.Parse("Key")].Description);
        Assert.Equal("nt!io, other.sys", map[PoolTag.Parse("File")].Owner);
    }

    [Fact]
    public void DriverScanFindsLiteralAndFallsBackToBuiltin()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pw-drivers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "contoso.sys"), [0, 1, (byte)'C', (byte)'t', (byte)'s', (byte)'o', 9]);
            File.WriteAllBytes(Path.Combine(dir, "other.sys"), [0, 1, 2, 3]);
            var hits = DriverTagScanner.Scan([PoolTag.Parse("Ctso"), PoolTag.Parse("Nope")], Directory.GetFiles(dir));
            Assert.Equal(["contoso.sys"], hits[PoolTag.Parse("Ctso")]);
            Assert.Empty(hits[PoolTag.Parse("Nope")]);
            var cache = Path.Combine(dir, "cache.json");
            var k = new TagKnowledge(null, cache);
            k.AddDriverHits(hits);
            Assert.Equal("contoso.sys", new TagKnowledge(null, cache).Lookup(PoolTag.Parse("Ctso")).Owner);
            Assert.Equal("refs.sys", k.Lookup(PoolTag.Parse("Reff")).Owner);
            Assert.True(k.IsFileSystemTag(PoolTag.Parse("Reff")));
        }
        finally { Directory.Delete(dir, true); }
    }
}
