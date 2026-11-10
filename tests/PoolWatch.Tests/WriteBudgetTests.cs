using PoolWatch.Core;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Core.Spawn;
using PoolWatch.Core.Tags;
using Xunit.Abstractions;

using static PoolWatch.Tests.Clocks;

namespace PoolWatch.Tests;

/// <summary>
/// An incident host whose write check counts what is written through it: a file replaced in one step (a temp file moved
/// onto it) counts its whole new length, a file appended to counts what it grew by (<see cref="Settle"/>). Every write of the
/// incident writer and the storm reports goes through this check, so this is what reached the disk. The chart is a PNG of
/// the size a real one has.
/// </summary>
internal sealed class CountingHost(string dir) : IIncidentHost
{
    public string IncidentsDir { get; } = dir;
    public long Bytes { get; private set; }
    public List<string> Logs { get; } = [];
    /// <summary>How many times each file was replaced whole (a temp file moved onto it), by its path.</summary>
    public Dictionary<string, int> Replaced { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>What was written to files of each name (events.csv, report.md, ...) in all folders: replaced and appended.</summary>
    public Dictionary<string, long> BytesByName { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Of <see cref="BytesByName"/>, what replacing them whole wrote.</summary>
    public Dictionary<string, long> ReplacedBytesByName { get; } = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _tmp = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _appendFrom = new(StringComparer.OrdinalIgnoreCase);

    public string CheckWrite(string path)
    {
        if (!path.StartsWith(IncidentsDir, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException(path);
        if (path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) _tmp.Add(path);
        else if (_tmp.Remove(path + ".tmp"))
        {
            // Replaced: the whole new file (written, not yet moved).
            long n = new FileInfo(path + ".tmp").Length;
            Bytes += n;
            string name = Path.GetFileName(path);
            BytesByName[name] = BytesByName.GetValueOrDefault(name) + n;
            ReplacedBytesByName[name] = ReplacedBytesByName.GetValueOrDefault(name) + n;
            Replaced[path] = Replaced.GetValueOrDefault(path) + 1;
        }
        else if (!Directory.Exists(path)) _appendFrom.TryAdd(path, File.Exists(path) ? new FileInfo(path).Length : 0); // appended to (or a folder made)
        return path;
    }

    /// <summary>Counts what the files appended to since the last call grew by.</summary>
    public void Settle()
    {
        foreach (var (path, from) in _appendFrom)
            if (File.Exists(path))
            {
                long n = new FileInfo(path).Length - from;
                Bytes += n;
                string name = Path.GetFileName(path);
                BytesByName[name] = BytesByName.GetValueOrDefault(name) + n;
            }
        _appendFrom.Clear();
    }

    public FileDetails? DescribeFile(string? path) => new("1.2.3", "Contoso", "Leaker", null, "Contoso Ltd", "valid embedded signature");
    public SystemInfo GetSystemInfo() => new() { Machine = "TEST", OsDescription = "Windows 11 Pro 25H2", OsBuild = "26200.1", RamBytes = 64L << 30, LogicalCpus = 32 };
    /// <summary>Charts drawn.</summary>
    public int Renders { get; private set; }
    public bool RenderTimeline(TimelineChart chart, string pngPath) { Renders++; File.WriteAllBytes(pngPath, new byte[35_000]); return true; } // a real chart is about 30-38 KB
    public void RequestHandleScan() { }
    public void RequestTagScan(IReadOnlyCollection<PoolTag> tags) { }
    public string DisplayUser(string user) => user;
    public SessionDetails? DescribeSession(Suspect suspect) => null;
    public void Log(string message) => Logs.Add(message);
}

/// <summary>
/// PoolWatch's own writes under the owner's load stay inside their share of the overhead budget (DESIGN.md section 3:
/// under 5 MB an hour in all, of which the day's CSV files take about 2 to 3 MB, TECHNICAL.md): 10 growth incidents
/// opening and open for an hour on a PC that records about 9,400 events an hour (the owner's events.csv, 22:00 to 23:00 on
/// 2026-11-09: 5,158 rollups, 1,667 starts, 1,653 exits, 575 orphanings, 339 findings), with command lines of the owner's
/// lengths, and a storm-heavy hour beside them (three sessions bursting every 2.5 minutes, a new program storming every 2
/// minutes, Git Bash fork copies). Measured at the write check every file goes through; and the CPU and the allocations
/// of the incident manager, the spawn tracker and the report writers over that hour, on this thread.
/// </summary>
public sealed class WriteBudgetTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pw-budget-" + Guid.NewGuid().ToString("N"));

    /// <summary>The day's CSV files' rate as TECHNICAL.md gives it (about 2 to 3 MB an hour), its upper end: the report folders' share is what is left of the 5 MB.</summary>
    private const double DailyCsvMBPerHour = 3;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

    /// <summary>This thread's CPU time so far (100 ns ticks); 0 off Windows.</summary>
    private static long ThreadCpu() => OperatingSystem.IsWindows() && GetThreadTimes(GetCurrentThread(), out _, out _, out long k, out long u) ? k + u : 0;

    private static string Long(string head, Random rnd, int min, int max) =>
        head + " " + string.Concat(Enumerable.Range(0, rnd.Next(min, max) / 9).Select(_ => rnd.Next(0x10000000, int.MaxValue).ToString("x8") + " "));

    [Fact]
    public void TenOpenGrowthIncidentsAndAStormHeavyHourWriteUnderFiveMegabytes()
    {
        Directory.CreateDirectory(_dir);
        var host = new CountingHost(_dir);
        var w = new World { Interval = TimeSpan.FromSeconds(10) };
        var settings = new Settings { GrowthRules = [new(10, 50 * Settings.MB)], SpawnStormThreshold = 30, SpawnStormWindowSeconds = 30, ShortLivedSeconds = 10 };
        var mgr = new IncidentManager(settings, new TagKnowledge(null, null), host, w.Registry);
        var det = new Detector(settings);
        var rnd = new Random(7);
        // 40 long-running processes doing I/O, with command lines: the suspects of the incidents.
        w.AddProcess(4, 0, "System");
        for (int p = 0; p < 40; p++) w.AddProcess(100 + p, 4, p % 4 == 0 ? "claude.exe" : p % 4 == 1 ? "node.exe" : p % 4 == 2 ? "python.exe" : "git.exe");
        string[] tags = Enumerable.Range(0, 10).Select(i => $"T{i:D3}").ToArray();
        var bytes = tags.ToDictionary(t => t, t => 100L << 20);

        // The storms: their own registry and tracker on the same clocks, writing into the same incidents folder.
        DateTime stormNow = World.T0;
        var stormRegistry = new ProcessRegistry(clock: () => At(stormNow));
        var tracker = new SpawnTracker(settings, stormRegistry, new GitRootResolver(new FakeProbe()), new([@"C:\Users\me"], [], @"C:\Users"), _ => null);
        int stormPid = 100_000, eventPid = 500_000;
        long seq = 0;
        ProcessRecord Start(int ppid, string image, string cl) => stormRegistry.OnStart(stormPid++, ppid, stormNow.ToFileTimeUtc() + ++seq, image, null, cl, null, stormNow);
        void Stop(ProcessRecord r) => stormRegistry.OnStop(r.Pid, r.Key.CreateTime, 0, stormNow);
        const string BashCl = "\"C:\\Program Files\\Git\\usr\\bin\\bash.exe\" -c \"set -euo pipefail; source x.sh\" run";
        var sessions = Enumerable.Range(0, 3).Select(i => Start(4, "Leaker.exe", $"Leaker.exe agent-session --index {i}").Pid).ToArray();
        int forkParent = Start(4, "bash.exe", BashCl).Pid;
        stormRegistry.DrainEvents();
        double owed = 0;
        int stormsOpened = 0, newcomer = -1;

        const int Warmup = 30, Hour = 360; // 10 s samples: 5 minutes before the growth, then the hour measured
        long measuredFrom = 0, mustFrom = 0, canWaitFrom = 0;
        // PoolWatch's own work in the hour, on this thread: CPU (100 ns ticks) and bytes allocated, by part.
        long cpuIncidents = 0, cpuStorms = 0, allocIncidents = 0, allocStorms = 0;
        var reportWrites = new Dictionary<string, List<long>>();
        for (int i = 0; i < Warmup + Hour; i++)
        {
            if (i == Warmup) { host.Settle(); (measuredFrom, mustFrom, canWaitFrom) = (host.Bytes, mgr.Budget.SpentMust, mgr.Budget.SpentCanWait); }
            for (int p = 0; p < 40; p++) w.Io(100 + p, other: 50 + rnd.Next(500), read: rnd.Next(100), handles: p == 3 ? 5 : 0);
            if (i >= Warmup) foreach (var t in tags) bytes[t] += 1L << 20; // each tag 6 MB a minute: the 50 MB in 10 min rule fires
            w.Sample(tags.Select(t => (t, bytes[t])).ToArray());
            if (i == 0)
                foreach (var r in w.Registry.LiveRecords()) r.CommandLine = Long(r.Image, rnd, 200, 600);
            var now = w.Now - w.Interval;

            // About 26 events a sample (9,400 an hour) of the owner's mix and sizes.
            var events = new List<LifecycleEvent>();
            for (int e = 0; e < 26; e++)
            {
                int pid = eventPid++;
                int kind = rnd.Next(100);
                events.Add(kind switch
                {
                    < 55 => new LifecycleEvent { TimeUtc = now, Kind = LifecycleKind.Rollup, ParentPid = 100 + rnd.Next(40), Image = "bash.exe", Count = 1 + rnd.Next(9),
                        Owner = "Claude Code session x (PID 100)", OwnerKey = "claude:100", Detail = Long("12 short commands under bash.exe:", rnd, 60, 160) },
                    < 73 => new LifecycleEvent { TimeUtc = now, Kind = LifecycleKind.Start, Pid = pid, CreateTime = now.ToFileTimeUtc(), ParentPid = 100 + rnd.Next(40), Image = "node.exe",
                        CommandLine = Long("node.exe", rnd, 150, 700), User = @"PC\me" },
                    < 91 => new LifecycleEvent { TimeUtc = now, Kind = LifecycleKind.Exit, Pid = pid, CreateTime = now.ToFileTimeUtc(), ParentPid = 100 + rnd.Next(40), Image = "node.exe",
                        CommandLine = Long("node.exe", rnd, 150, 700), ExitCode = 0 },
                    < 97 => new LifecycleEvent { TimeUtc = now, Kind = LifecycleKind.Orphaned, Pid = pid, CreateTime = now.ToFileTimeUtc(), ParentPid = 100, Image = "bash.exe",
                        Detail = "parent bash.exe (100) exited at 23:57:45; still running 30 s later" },
                    _ => new LifecycleEvent { TimeUtc = now, Kind = LifecycleKind.Finding, Image = "SpawnStorm", Detail = Long("Spawn storm: node.exe (PID 1) started 44 short-lived children", rnd, 20, 80) },
                });
            }
            var alerts = det.Evaluate(w.History);
            long c0 = ThreadCpu(), a0 = GC.GetAllocatedBytesForCurrentThread();
            mgr.AddEvents(events, At(now));
            mgr.Process(w.History, det, alerts, At(now));
            if (i >= Warmup) (cpuIncidents, allocIncidents) = (cpuIncidents + ThreadCpu() - c0, allocIncidents + GC.GetAllocatedBytesForCurrentThread() - a0);
            foreach (var inc in mgr.Open)
            {
                var list = reportWrites.TryGetValue(inc.Id, out var l) ? l : reportWrites[inc.Id] = [];
                if (list.Count == 0 || list[^1] != inc.LastWriteMs) list.Add(inc.LastWriteMs);
            }

            // A storm-heavy 10 s in the measured hour: the sessions' bursts, a new program every 2 minutes, and fork copies of a bash.
            for (int s = 0; s < 10 && i >= Warmup; s++)
            {
                stormNow = now.AddSeconds(s);
                long sec = (i - Warmup) * 10L + s;
                owed += (sec % 150 < 60 ? 100 : 20) / 60.0;
                for (; owed >= 1; owed--)
                    foreach (int session in sessions) Stop(Start(session, "bash.exe", Long("bash -c", rnd, 100, 400)));
                if (sec % 120 == 0) newcomer = Start(4, $"tool{sec / 120}.exe", $"tool{sec / 120}.exe --watch").Pid;
                if (newcomer > 0 && sec % 120 < 8) for (int k = 0; k < 5; k++) Stop(Start(newcomer, "cmd.exe", "cmd /c exit 0"));
                for (int k = 0; k < 3; k++) { var copy = Start(forkParent, "bash.exe", BashCl); Stop(Start(copy.Pid, "grep.exe", "grep -q done")); Stop(copy); }
            }
            var stormAt = At(now.AddSeconds(9)); // the storms observed after their 10 s, as the next sample would
            var drained = stormRegistry.DrainEvents();
            long c1 = ThreadCpu(), a1 = GC.GetAllocatedBytesForCurrentThread();
            var (findings, toWrite) = tracker.Observe(drained, stormAt);
            foreach (var folder in toWrite)
                StormReports.WriteWithin(mgr.Budget, tracker, folder, _dir, "PoolWatch itself (test)", host.CheckWrite, stormAt.Ms);
            if (i >= Warmup) (cpuStorms, allocStorms) = (cpuStorms + ThreadCpu() - c1, allocStorms + GC.GetAllocatedBytesForCurrentThread() - a1);
            stormsOpened += findings.Count(f => f.Kind == FindingKind.SpawnStorm && f.Severity == FindingSeverity.Alert);
            host.Settle();
        }
        long written = host.Bytes - measuredFrom, must = mgr.Budget.SpentMust - mustFrom, canWait = mgr.Budget.SpentCanWait - canWaitFrom;
        static double MB(long b) => b / 1024.0 / 1024.0;
        static double Sec(long ticks) => ticks / 1e7;
        double shareMB = 5 - DailyCsvMBPerHour;
        // How far behind each open incident's report.json (what the window's details read) was at most in the hour: its
        // longest time between two writes, or from its last write to the end.
        long endMs = At(w.Now).Ms;
        var gaps = reportWrites.Values.Select(l => l.Skip(1).Append(endMs).Zip(l, (b, a) => b - a).Max() / 60_000.0).ToList();
        output.WriteLine($"written in the hour: {MB(written):0.00} MB (writes that must be made {MB(must):0.00} MB, that could wait {MB(canWait):0.00} MB); " +
                         $"share {shareMB:0.0} MB beside the day's CSV files' {DailyCsvMBPerHour:0.0} MB an hour; writes held {mgr.Budget.Held}; " +
                         $"{mgr.CreatedCount} incidents, {stormsOpened} storms, {Directory.GetDirectories(_dir).Length} folders; " +
                         $"report.json writes per incident {string.Join(",", reportWrites.Values.Select(l => l.Count))}, longest gap {gaps.Max():0} min");
        output.WriteLine($"CPU in the hour: incidents (ranking, writes) {Sec(cpuIncidents):0.00} s, storms (tracking, writes) {Sec(cpuStorms):0.00} s, " +
                         $"{(cpuIncidents + cpuStorms) / 1e7 / 3600 * 100:0.000}% of one core; allocated: incidents {MB(allocIncidents):0} MB, storms {MB(allocStorms):0} MB");

        // It is the scenario: 10 incidents open all hour, each with its events, and storms with their folders.
        Assert.Equal(10, mgr.Open.Count);
        Assert.Equal(10, mgr.CreatedCount);
        foreach (var inc in mgr.Open)
        {
            Assert.True(File.Exists(Path.Combine(inc.Dir, "report.json")));
            Assert.True(File.Exists(Path.Combine(inc.Dir, "events.csv")));
        }
        // Every open incident's report is written when it opens and kept within an hour of now, the one written longest
        // ago first (the row's figures are live; under this load the budget has room for about one refresh in five minutes).
        Assert.True(gaps.Max() <= 60, $"a report {gaps.Max():0} min behind");
        Assert.True(reportWrites.Values.Sum(l => l.Count - 1) >= 5, "refreshes in the hour");
        Assert.True(stormsOpened >= 40, $"{stormsOpened} storms");
        Assert.InRange(Directory.GetDirectories(_dir).Count(d => d.Contains("-SpawnStorm-")), 1, SpawnTracker.MaxNewFoldersPerHour);
        // What could wait was held to the budget's hour and burst; what had to be made (10 openings, the storm folders'
        // first and last writes) to its allowance; and the whole is inside the report folders' share of the 5 MB an hour.
        Assert.True(canWait <= mgr.Budget.BytesPerHour + mgr.Budget.Burst, $"{canWait:N0} bytes that could wait");
        Assert.True(must <= 1280L << 10, $"{must:N0} bytes that had to be made"); // 10 openings' reports (about 95 KB each here) and the storm folders' first writes
        Assert.True(written == must + canWait, $"{written:N0} written, {must + canWait:N0} spent"); // every write went through the budget
        Assert.True(MB(written) < shareMB, $"{MB(written):0.00} MB written in the hour");
        // The CPU and the allocations of this load (PoolWatch's own work in it): well inside the 0.5% of one core.
        if (OperatingSystem.IsWindows()) Assert.True(cpuIncidents + cpuStorms < 18 * 10_000_000L, $"{Sec(cpuIncidents + cpuStorms):0.00} s of CPU in the hour");
        Assert.True(allocStorms < 100L << 20, $"{MB(allocStorms):0} MB allocated for the storms");
        Assert.True(allocIncidents < 1L << 30, $"{MB(allocIncidents):0} MB allocated for the incidents");
    }

    [Fact]
    public void TheBudgetRefillsOnTheForwardClockAndAWriteThatMustBeMadeSpendsIt()
    {
        var b = new WriteBudget(bytesPerHour: 3_600_000, burst: 100_000); // 1,000 bytes a second
        Assert.True(b.HasRoom(0));
        Assert.True(b.HasRoomToSpare(0)); // a full burst
        b.Spend(150_000, 0, must: true); // a write that had to be made: 50,000 below 0
        Assert.False(b.HasRoom(0));
        Assert.False(b.HasRoom(49_000));
        Assert.True(b.HasRoom(51_000));
        Assert.False(b.HasRoomToSpare(51_000)); // room, but not half the burst
        Assert.True(b.HasRoom(10_000_000)); // never more than the burst
        Assert.True(b.HasRoomToSpare(10_000_000));
        b.Spend(100_000, 10_000_000);
        Assert.False(b.HasRoom(10_000_000));
        Assert.False(b.HasRoom(9_000_000)); // the forward clock read lower: nothing refilled
        Assert.Equal(250_000, b.Spent);
        Assert.Equal((150_000, 100_000), (b.SpentMust, b.SpentCanWait));
        // A burst of writes that had to be made owes at most the burst: what can wait is written again within a burst's
        // refill (here 100 s; by default a quarter of an hour), not held back an hour behind ten incidents opening together.
        b.Spend(50_000_000, 10_000_000, must: true);
        Assert.False(b.HasRoom(10_000_000 + 99_000));
        Assert.True(b.HasRoom(10_000_000 + 101_000));
        Assert.Equal(50_250_000, b.Spent);
    }
}
