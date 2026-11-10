using System.Text.Json;
using PoolWatch.Core;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Model;
using PoolWatch.Core.Spawn;

using static PoolWatch.Tests.Clocks;

namespace PoolWatch.Tests;

/// <summary>
/// What a spawn storm counts (fork copies are not launches; a start's parent is one process however its key was read) and
/// where its report goes (one folder per parent identity while its storms keep coming within an hour; at most 10 new
/// folders an hour). The shapes are the owner's: Git Bash fork copies under pytest, a Claude Code session's bursts every
/// 2.5 minutes (tools/Leaker agent-load), hundreds of different parents in an hour.
/// </summary>
public class StormFolderTests
{
    private static readonly DateTime T0 = new(2026, 11, 9, 23, 0, 0, DateTimeKind.Utc);

    /// <summary>Git Bash's own command line for a script it runs, as the owner's report had it.</summary>
    private const string BashCl = "\"C:\\Program Files\\Git\\usr\\bin\\bash.exe\" -c \"set -euo pipefail; source \\\"$1\\\"; install_promote\" promote-test /c/Users/me/AppData/Local/Temp/t/promote_setup.sh";

    private sealed class Rig
    {
        public DateTime Now = T0;
        public readonly ProcessRegistry Registry;
        public readonly SpawnTracker Tracker;
        public readonly Settings Settings = new() { SpawnStormThreshold = 30, SpawnStormWindowSeconds = 30, ShortLivedSeconds = 10 };
        public int NextPid = 2000;
        private long _seq;
        public readonly List<Finding> Findings = new();
        public readonly HashSet<StormFolder> Folders = new(ReferenceEqualityComparer.Instance);
        public int Writes;

        public Rig()
        {
            Registry = new(clock: () => At(Now));
            Tracker = new SpawnTracker(Settings, Registry, new GitRootResolver(new FakeProbe()), new([@"C:\Users\me"], [], @"C:\Users"), _ => null);
        }

        /// <summary>A process start through the registry (as the ETW start comes), each created a little after the one before.</summary>
        public ProcessRecord Start(int ppid, string image, string cl, int? pid = null, bool estimated = false) =>
            Registry.OnStart(pid ?? NextPid++, ppid, Now.ToFileTimeUtc() + ++_seq, image, null, cl, null, Now, createTimeEstimated: estimated);

        public void Stop(ProcessRecord r) => Registry.OnStop(r.Pid, r.Key.CreateTime, 0, Now.AddMilliseconds(40));

        /// <summary>A sample: the registry's events into the tracker; each folder due is "written" (its state as the writer leaves it).</summary>
        public void Tick(IEnumerable<LifecycleEvent>? extra = null)
        {
            var events = Registry.DrainEvents();
            if (extra is not null) events.AddRange(extra);
            var (f, toWrite) = Tracker.Observe(events, At(Now));
            Findings.AddRange(f);
            foreach (var s in Tracker.OpenStorms) if (s.Folder is { } sf) Folders.Add(sf);
            foreach (var folder in toWrite)
            {
                Folders.Add(folder);
                folder.Dir ??= "written";
                folder.MarkWritten(At(Now).Ms);
                Writes++;
            }
        }

        public List<Finding> Opened => Findings.Where(f => f.Kind == FindingKind.SpawnStorm && f.Severity == FindingSeverity.Alert).ToList();
    }

    [Fact]
    public void FortyEightForkCopiesOfGitBashAreNoStorm()
    {
        // The owner's 20261109-231314-SpawnStorm-bash: 48 bash.exe children with the parent's exact command line, living
        // 13 to 1,018 ms (Git Bash running a script's subshells and pipeline stages as fork copies): no launch, no storm.
        var rig = new Rig();
        rig.Start(4, "bash.exe", BashCl, pid: 600);
        rig.Registry.DrainEvents();
        for (int s = 0; s < 40; s++, rig.Now = rig.Now.AddSeconds(1))
        {
            if (s < 24)
                for (int k = 0; k < 2; k++) rig.Stop(rig.Start(600, "bash.exe", BashCl));
            rig.Tick();
        }
        Assert.Empty(rig.Opened);
        Assert.Equal(48, rig.Tracker.ForkCopiesSeen);
        Assert.Empty(rig.Tracker.Rates(At(rig.Now))); // not counted in the window's rates either
    }

    [Fact]
    public void ForkCopiesRunningGitCountToTheBashTheyWereForkedFrom()
    {
        // 48 fork copies, each running one git.exe: one storm, of the original bash (PID 600), naming git.exe; the copies
        // are said as fork copies, not counted as starts.
        var rig = new Rig();
        rig.Start(4, "bash.exe", BashCl, pid: 600);
        rig.Registry.DrainEvents();
        for (int s = 0; s < 40; s++, rig.Now = rig.Now.AddSeconds(1))
        {
            if (s < 24)
                for (int k = 0; k < 2; k++)
                {
                    var copy = rig.Start(600, "bash.exe", BashCl);
                    var git = rig.Start(copy.Pid, "git.exe", "git status --porcelain");
                    rig.Stop(git);
                    rig.Stop(copy);
                }
            rig.Tick();
        }
        var opened = Assert.Single(rig.Opened);
        Assert.Equal(600, opened.Pid);
        Assert.Contains("(PID 600)", opened.Title);
        Assert.Contains("git.exe", opened.Title);
        Assert.DoesNotContain("bash.exe)", opened.Title.Split('(').Last()); // the children named are the gits
        var storm = Assert.Single(rig.Tracker.OpenStorms);
        Assert.Equal(["git.exe"], storm.ByImage.Keys);
        Assert.Equal(48, storm.TotalStarts);
        Assert.Equal(48, storm.ForkCopies);
        Assert.Contains(opened.Details, d => d.Contains("fork copies of the parent") && d.Contains("not counted as starts"));
    }

    [Fact]
    public void ChildrenWithoutARegistryRecordCountToTheSameParent()
    {
        // 4 short commands a second from one parent; half of them reach the tracker without a registry record (the registry
        // had none when they started, or forgot it): their parent is the live process with that PID, so one storm, not two.
        var rig = new Rig();
        rig.Start(4, "node.exe", "node runner.js", pid: 700);
        rig.Registry.DrainEvents();
        int bare = 50_000;
        for (int s = 0; s < 45; s++, rig.Now = rig.Now.AddSeconds(1))
        {
            var extra = new List<LifecycleEvent>();
            if (s < 30)
                for (int k = 0; k < 4; k++)
                {
                    if (k % 2 == 0) { rig.Stop(rig.Start(700, "cmd.exe", "cmd /c echo")); continue; }
                    long create = rig.Now.ToFileTimeUtc() + k;
                    int pid = bare++;
                    extra.Add(new LifecycleEvent { TimeUtc = rig.Now, Kind = LifecycleKind.Start, Pid = pid, CreateTime = create, ParentPid = 700, Image = "cmd.exe", CommandLine = "cmd /c echo" });
                    extra.Add(new LifecycleEvent { TimeUtc = rig.Now.AddMilliseconds(40), Kind = LifecycleKind.Exit, Pid = pid, CreateTime = create, ParentPid = 700, Image = "cmd.exe", ExitCode = 0 });
                }
            rig.Tick(extra);
        }
        var opened = Assert.Single(rig.Opened);
        Assert.Equal(700, opened.Pid);
        Assert.Equal(120, Assert.Single(rig.Tracker.OpenStorms).TotalStarts);
    }

    [Fact]
    public void AParentReKeyedByASnapshotStaysOneStorm()
    {
        // The parent's start came with an estimated create time (it could not be opened); its children's records name that
        // key. A snapshot then re-keys it to the exact one, and the children started after it name the exact key: still the
        // same parent, so the storm goes on, not a second one.
        var rig = new Rig();
        var est = T0.AddMinutes(-1);
        rig.Registry.OnStart(800, 4, est.ToFileTimeUtc(), "python.exe", null, "python run.py", null, est, createTimeEstimated: true);
        rig.Registry.DrainEvents();
        for (int s = 0; s < 80; s++, rig.Now = rig.Now.AddSeconds(1))
        {
            if (s == 25)
                rig.Registry.ObserveSnapshot([new ProcessSample { Pid = 800, ParentPid = 4, ImageName = "python.exe", CreateTime = est.AddMilliseconds(500).ToFileTimeUtc() }], rig.Now);
            if (s < 60)
                for (int k = 0; k < 2; k++) rig.Stop(rig.Start(800, "git.exe", "git rev-parse HEAD"));
            rig.Tick();
        }
        Assert.Equal(1, rig.Registry.Rekeys);
        var opened = Assert.Single(rig.Opened);
        Assert.Equal(800, opened.Pid);
        var storm = Assert.Single(rig.Tracker.OpenStorms);
        Assert.Equal(est.AddMilliseconds(500).ToFileTimeUtc(), storm.ParentKey.CreateTime); // the key it has now
        Assert.Equal(120, storm.TotalStarts);
    }

    /// <summary>
    /// tools/Leaker agent-load's session (Program.cs, AgentSession): busy for 60 s at 100 short commands a minute, then 90 s at
    /// 20 a minute, so its storm opens and closes every 2.5 minutes. For an hour: one report folder, written at most every
    /// 10 minutes, with every storm in it.
    /// </summary>
    [Fact]
    public void TheAgentLoadShapeForAnHourIsOneReportFolder()
    {
        var rig = new Rig();
        rig.Start(4, "Leaker.exe", @"C:\t\Leaker.exe agent-session --dir C:\t\session0 --seconds 3600 --index 0", pid: 900);
        rig.Registry.DrainEvents();
        double owed = 0;
        int k = 0;
        for (int s = 0; s < 3600; s++, rig.Now = rig.Now.AddSeconds(1))
        {
            owed += (s % 150 < 60 ? 100 : 20) / 60.0;
            for (; owed >= 1; owed--) rig.Stop(rig.Start(900, "bash.exe", $"bash -c \"cd /tmp && echo {k++:x8} > /dev/null\""));
            if (s % 10 == 9) rig.Tick(); // a sample every 10 s
        }
        Assert.InRange(rig.Opened.Count, 20, 25); // a storm every 2.5 minutes
        var folder = Assert.Single(rig.Folders);
        Assert.Equal(rig.Opened.Count, folder.StormCount);
        Assert.Equal(rig.Opened.Count, folder.Episodes().Count);
        Assert.InRange(rig.Writes, 1, 8); // made at the first storm, then at most every 10 minutes
        // Every storm after the first says it went into the first one's folder.
        Assert.All(rig.Opened.Skip(1), f => Assert.Contains(f.Details, d => d.StartsWith("Written into the report folder of ", StringComparison.Ordinal)));
    }

    [Fact]
    public void FiveHundredStormsInAnHourMakeAtMostTenFolders()
    {
        // 500 different programs, one every 7 s, each starting 35 short commands in 7 s: 500 storms, each in its finding; the
        // first 10 in an hour get a report folder, the rest say why they have none.
        var rig = new Rig();
        var parents = new Dictionary<int, int>();
        for (int s = 0; s < 3540; s++, rig.Now = rig.Now.AddSeconds(1))
        {
            int i = s / 7;
            if (s % 7 == 0 && i < 500) parents[i] = rig.Start(4, $"app{i}.exe", $"app{i}.exe --serve").Pid;
            if (i < 500 && parents.TryGetValue(i, out int ppid))
                for (int k = 0; k < 5; k++) rig.Stop(rig.Start(ppid, "tool.exe", "tool.exe --once"));
            rig.Tick();
        }
        for (int s = 0; s < 120; s++, rig.Now = rig.Now.AddSeconds(1)) rig.Tick();
        Assert.Equal(500, rig.Opened.Count);
        Assert.Equal(SpawnTracker.MaxNewFoldersPerHour, rig.Folders.Count);
        Assert.Equal(500 - SpawnTracker.MaxNewFoldersPerHour, rig.Tracker.FoldersWithheld);
        var withheld = rig.Opened.Where(f => f.Details.Any(d => d.StartsWith("No report folder: ", StringComparison.Ordinal))).ToList();
        Assert.Equal(500 - SpawnTracker.MaxNewFoldersPerHour, withheld.Count);
        Assert.All(withheld, f => Assert.Null(f.ReportDir));
    }

    /// <summary>
    /// A storm of 48 short git.exe and one node.exe that keeps running, in a real folder: written as it comes (when made,
    /// then when its storm closed), until <paramref name="untilSeconds"/>.
    /// </summary>
    private static (Rig rig, string dir, Func<StormFolder, long> write) LongChildStorm(string dir, int untilSeconds)
    {
        var rig = new Rig();
        rig.Start(4, "bash.exe", "bash -c ./ci.sh", pid: 600);
        rig.Registry.DrainEvents();
        long Write(StormFolder f) => StormReports.Write(rig.Tracker, f, dir, "PoolWatch itself (test)", p => p, At(rig.Now).Ms);
        for (int s = 0; s < untilSeconds; s++, rig.Now = rig.Now.AddSeconds(1))
        {
            if (s < 24) for (int k = 0; k < 2; k++) rig.Stop(rig.Start(600, "git.exe", "git status --porcelain"));
            if (s == 3) rig.Start(600, "node.exe", "node server.js --watch"); // never exits
            var (findings, toWrite) = rig.Tracker.Observe(rig.Registry.DrainEvents(), At(rig.Now));
            rig.Findings.AddRange(findings);
            foreach (var f in toWrite) Write(f);
        }
        return (rig, dir, Write);
    }

    private static List<string> SpawnRows(string dir) => File.ReadAllLines(Path.Combine(Assert.Single(Directory.GetDirectories(dir)), "spawns.csv")).Skip(1).ToList();

    [Fact]
    public void AStormsChildThatIsStillRunningIsInSpawnsCsvWhenPoolWatchStopsAndWhenItsFolderLeavesMemory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pw-stormlong-" + Guid.NewGuid().ToString("N"));
        try
        {
            // PoolWatch stops 15 minutes after the storm closed, its folder written since: the node.exe row is written then.
            var (rig, d, write) = LongChildStorm(Path.Combine(dir, "a"), 16 * 60);
            Assert.Single(rig.Opened);
            Assert.Empty(rig.Tracker.OpenStorms);
            Assert.DoesNotContain(SpawnRows(d), l => l.Contains(",node.exe,")); // still running: held for its exit
            var last = rig.Tracker.FoldersToFinish();
            Assert.Single(last);
            foreach (var f in last) write(f);
            Assert.Equal(48, SpawnRows(d).Count(l => l.Contains(",git.exe,")));
            Assert.Single(SpawnRows(d), l => l.Contains(",node.exe,"));

            // The folder leaves memory an hour after its storm closed: its last write has the node.exe row too.
            (rig, d, _) = LongChildStorm(Path.Combine(dir, "b"), 63 * 60);
            Assert.Empty(rig.Tracker.Folders); // let go
            Assert.Empty(rig.Tracker.FoldersToFinish());
            Assert.Single(SpawnRows(d), l => l.Contains(",node.exe,"));
            Assert.Equal(49, SpawnRows(d).Count);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void AFolderWhoseHourEndsInTheSampleThatOpensItsIdentitysNextStormGetsItsLastWrite()
    {
        // A storm closes; no sample comes for an hour (the PC slept); the first sample after it opens the next storm of the
        // same program. The old folder's hour ended in that very sample: it gets its last write (its storm's close, never
        // written) before the new folder replaces it.
        var rig = new Rig();
        rig.Start(4, "node.exe", "node runner.js", pid: 700);
        rig.Registry.DrainEvents();
        for (int s = 0; s < 30; s++, rig.Now = rig.Now.AddSeconds(1))
        {
            for (int k = 0; k < 2; k++) rig.Stop(rig.Start(700, "cmd.exe", "cmd /c echo"));
            rig.Tick();
        }
        for (int s = 0; s < 120; s++, rig.Now = rig.Now.AddSeconds(1)) rig.Tick(); // it closes
        var first = Assert.Single(rig.Folders);
        Assert.Single(first.Closed);
        Assert.True(first.EpisodesChanged); // its close is not written yet (within 10 minutes of its first write)
        rig.Now = rig.Now.AddMinutes(61);
        var written = new List<StormFolder>();
        for (int k = 0; k < 40; k++) rig.Stop(rig.Start(700, "cmd.exe", "cmd /c echo"));
        var (_, toWrite) = rig.Tracker.Observe(rig.Registry.DrainEvents(), At(rig.Now));
        written.AddRange(toWrite);
        Assert.Equal(2, rig.Tracker.OpenStorms.Count + rig.Tracker.Folders.Count); // the new storm, in a new folder
        Assert.Contains(first, written);
        Assert.True(first.Final);
        Assert.Contains(written, f => f != first && f.LastWriteMs is null);
    }

    [Fact]
    public void ChildrenSeenAfterTheirParentExitedCountToTheSameStorm()
    {
        // A runner starts 2 short commands a second, then exits; 40 more of its commands (started before it exited) reach
        // PoolWatch only after, without a record of their own. Their parent is the runner as PoolWatch remembers it, not a
        // second parent with that PID: one storm.
        var rig = new Rig();
        var runner = rig.Start(4, "node.exe", "node runner.js", pid: 700);
        rig.Registry.DrainEvents();
        for (int s = 0; s < 31; s++, rig.Now = rig.Now.AddSeconds(1))
        {
            for (int k = 0; k < 2; k++) rig.Stop(rig.Start(700, "cmd.exe", "cmd /c echo"));
            rig.Tick();
        }
        rig.Stop(runner);
        rig.Tick();
        rig.Now = rig.Now.AddSeconds(1);
        var late = new List<LifecycleEvent>();
        for (int k = 0; k < 40; k++)
        {
            var at = rig.Now.AddSeconds(-8 + k / 10.0);
            late.Add(new LifecycleEvent { TimeUtc = at, Kind = LifecycleKind.Start, Pid = 60_000 + k, CreateTime = at.ToFileTimeUtc(), ParentPid = 700, Image = "cmd.exe", CommandLine = "cmd /c echo" });
            late.Add(new LifecycleEvent { TimeUtc = at.AddMilliseconds(30), Kind = LifecycleKind.Exit, Pid = 60_000 + k, CreateTime = at.ToFileTimeUtc(), ParentPid = 700, Image = "cmd.exe", ExitCode = 0 });
        }
        rig.Tick(late);
        var opened = Assert.Single(rig.Opened);
        Assert.Equal(700, opened.Pid);
        var storm = Assert.Single(rig.Tracker.OpenStorms);
        Assert.Equal(runner.Key, storm.ParentKey);
        Assert.Equal(62 + 40, storm.TotalStarts);
    }

    [Fact]
    public void AForkCopysOriginGoesWithItUnderBothItsKeys()
    {
        // Fork copies whose start came with an estimated create time that a snapshot made exact before the tracker saw them:
        // their origin is kept under both keys, and both go once the copies leave the window.
        var rig = new Rig();
        rig.Start(4, "bash.exe", BashCl, pid: 600);
        rig.Registry.DrainEvents();
        var copies = new List<ProcessRecord>();
        for (int k = 0; k < 20; k++) copies.Add(rig.Start(600, "bash.exe", BashCl, estimated: true));
        rig.Registry.ObserveSnapshot(
            [new ProcessSample { Pid = 600, ParentPid = 4, ImageName = "bash.exe", CreateTime = rig.Registry.GetLive(600)!.Key.CreateTime },
             .. copies.Select(c => new ProcessSample { Pid = c.Pid, ParentPid = 600, ImageName = "bash.exe", CreateTime = c.Key.CreateTime + 3_000 })], rig.Now);
        Assert.Equal(20, rig.Registry.Rekeys);
        rig.Tick();
        Assert.Equal(20, rig.Tracker.ForkCopiesSeen);
        Assert.Equal(40, rig.Tracker.ForkOriginsHeld); // the event's key and the registry's
        for (int s = 0; s < 200; s++, rig.Now = rig.Now.AddSeconds(1)) rig.Tick();
        Assert.Equal(0, rig.Tracker.RecentCount);
        Assert.Equal(0, rig.Tracker.ForkOriginsHeld);
    }

    [Fact]
    public void AFolderIsWrittenWithEveryStormInItAndSpawnsCsvGetsEachStartOnce()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pw-stormfolder-" + Guid.NewGuid().ToString("N"));
        try
        {
            var rig = new Rig();
            rig.Start(4, "bash.exe", BashCl, pid: 600);
            rig.Registry.DrainEvents();
            var written = new List<string>();
            long bytes = 0;
            // Two bursts of fork copies running git, 3 minutes apart: two storms of the same bash, one folder.
            for (int s = 0; s < 400; s++, rig.Now = rig.Now.AddSeconds(1))
            {
                if (s < 24 || s is >= 200 and < 224)
                    for (int k = 0; k < 2; k++)
                    {
                        var copy = rig.Start(600, "bash.exe", BashCl);
                        rig.Stop(rig.Start(copy.Pid, "git.exe", "git status --porcelain"));
                        rig.Stop(copy);
                    }
                var (findings, toWrite) = rig.Tracker.Observe(rig.Registry.DrainEvents(), At(rig.Now));
                rig.Findings.AddRange(findings);
                foreach (var f in toWrite)
                {
                    bytes += StormReports.Write(rig.Tracker, f, dir, "PoolWatch itself (test)", p => p, At(rig.Now).Ms);
                    written.Add(f.Dir!);
                }
            }
            foreach (var f in rig.Tracker.FoldersToFinish())
            {
                bytes += StormReports.Write(rig.Tracker, f, dir, "PoolWatch itself (test)", p => p, At(rig.Now).Ms);
                written.Add(f.Dir!);
            }
            Assert.Equal(2, rig.Opened.Count);
            string folder = Assert.Single(written.Distinct());
            Assert.Single(Directory.GetDirectories(dir));
            string md = File.ReadAllText(Path.Combine(folder, "report.md"));
            Assert.Contains("## Storms in this folder", md);
            Assert.Contains("2 storms of bash.exe", md);
            Assert.Contains("48 fork copies of the parent", md);
            Assert.Contains("starts hundreds of short-lived processes a minute", md);
            Assert.DoesNotContain("relaunches the same command", md);
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "report.json")));
            var data = json.RootElement.GetProperty("data");
            Assert.Equal(2, data.GetProperty("storms").GetInt32());
            Assert.Equal(96, data.GetProperty("folderTotalStarts").GetInt64());
            Assert.Equal(2, data.GetProperty("episodes").GetArrayLength());
            var lines = File.ReadAllLines(Path.Combine(folder, "spawns.csv"));
            Assert.Equal(SpawnTracker.TraceHeader, lines[0]);
            Assert.Equal(96, lines.Length - 1); // each git start once, however often the folder was written
            Assert.Single(lines, l => l == SpawnTracker.TraceHeader);
            Assert.All(lines.Skip(1), l => Assert.Contains(",git.exe,", l));
            Assert.True(written.Count >= 2);
            Assert.True(bytes > 0);
            Assert.All(rig.Opened, f => Assert.Equal(folder, f.ReportDir));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
