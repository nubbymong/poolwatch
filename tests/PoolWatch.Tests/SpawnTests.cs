using PoolWatch.Core;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Model;
using PoolWatch.Core.Spawn;

using static PoolWatch.Tests.Clocks;

namespace PoolWatch.Tests;

/// <summary>An in-memory file system for the git-root resolver (Windows paths, case-insensitive).</summary>
internal sealed class FakeProbe : IFileProbe
{
    public readonly HashSet<string> Dirs = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, byte[]> Files = new(StringComparer.OrdinalIgnoreCase);
    public int Calls;
    public bool DirectoryExists(string path) { Calls++; return Dirs.Contains(path); }
    public bool FileExists(string path) { Calls++; return Files.ContainsKey(path); }
    public byte[]? ReadHead(string path, int count) => Files.TryGetValue(path, out var b) ? b.Take(count).ToArray() : null;
    public bool IsLocalFixed(string path) => path.StartsWith("C:", StringComparison.OrdinalIgnoreCase) || path.StartsWith("E:", StringComparison.OrdinalIgnoreCase);

    public static byte[] Index(int entries) => [(byte)'D', (byte)'I', (byte)'R', (byte)'C', 0, 0, 0, 2, (byte)(entries >> 24), (byte)(entries >> 16), (byte)(entries >> 8), (byte)entries];

    /// <summary>A repository at <paramref name="root"/> with HEAD and an index of <paramref name="tracked"/> entries (null: no index).</summary>
    public void Repo(string root, int? tracked)
    {
        Dirs.Add(root + "\\.git");
        Files[root + "\\.git\\HEAD"] = "ref: refs/heads/main\n"u8.ToArray();
        if (tracked is { } n) Files[root + "\\.git\\index"] = Index(n);
    }
}

public class CommandLineTokenTests
{
    [Theory]
    [InlineData(@"git.exe -C ""C:\Users\me\My Repo"" status", new[] { "git.exe", "-C", @"C:\Users\me\My Repo", "status" })]
    [InlineData(@"a ""b\""c"" d\\e", new[] { "a", "b\"c", @"d\\e" })]
    [InlineData(@"""C:\Program Files\Git\cmd\git.exe""  rev-parse", new[] { @"C:\Program Files\Git\cmd\git.exe", "rev-parse" })]
    public void SplitsLikeWindows(string cl, string[] expected) => Assert.Equal(expected, CommandLineTokens.Split(cl));

    [Fact]
    public void PatternFoldsPathsAndNumbers()
    {
        Assert.Equal("git -C <path> rev-parse --show-toplevel",
            CommandLineTokens.Pattern("git.exe", @"""C:\Program Files\Git\cmd\git.exe"" -C C:\Users\me rev-parse --show-toplevel"));
        Assert.Equal("git -c core.hooksPath=NUL -c core.fsmonitor= ls-files --others --exclude-standard -z",
            CommandLineTokens.Pattern("git.exe", "git -c core.hooksPath=NUL -c core.fsmonitor= ls-files --others --exclude-standard -z"));
        Assert.Equal("taskkill /pid <n> /t /f", CommandLineTokens.Pattern("taskkill.exe", "taskkill /pid 4242 /T /F".ToLowerInvariant()));
        Assert.Equal("node --inspect=<path> <id>", CommandLineTokens.Pattern("node.exe", @"node --inspect=C:\x\y 0123456789abcdef0123"));
    }
}

public class GitRootTests
{
    private static readonly GitRootContext Ctx = new([@"C:\Users\me"], [@"C:\Users\me\OneDrive"], @"C:\Users");

    [Theory]
    [InlineData(@"git -C C:\repo status", @"C:\x", @"C:\repo")]
    [InlineData(@"git -C sub -C ..\other status", @"C:\work", @"C:\work\other")]
    [InlineData(@"git status --porcelain", @"C:\work\proj", @"C:\work\proj")]
    [InlineData(@"git -c core.fsmonitor= rev-parse --show-toplevel", @"C:\work", @"C:\work")]
    [InlineData(@"git --work-tree=C:\wt -C C:\a status", null, @"C:\wt")]
    [InlineData(@"git status -C C:\notglobal", @"C:\cwd", @"C:\cwd")]
    public void WorkingDirectory(string cl, string? cwd, string expected) => Assert.Equal(expected, GitRootResolver.WorkingDirectory(cl, cwd));

    [Fact]
    public void WorkingDirectorySourceSeesDashCAfterConfigOption()
    {
        Assert.Equal(@"D:\p", GitRootResolver.WorkingDirectory(@"git -c core.x=y -C D:\p status", @"C:\cwd", out bool viaC));
        Assert.True(viaC);
        GitRootResolver.WorkingDirectory("git status", @"C:\cwd", out viaC);
        Assert.False(viaC);
    }

    [Fact]
    public void WorkingDirectoryUnknownWithoutCwd() => Assert.Null(GitRootResolver.WorkingDirectory("git -C rel status", null));

    [Fact]
    public void FindsNearestRootCachesAndStopsAtVolumeRoot()
    {
        var p = new FakeProbe();
        p.Repo(@"C:\Users\me", 0);
        var git = new GitRootResolver(p);
        var now = DateTime.UtcNow;
        Assert.Equal(@"C:\Users\me", git.FindRoot(@"C:\Users\me\AppData\Local\x", At(now).Ms));
        int calls = p.Calls;
        Assert.Equal(@"C:\Users\me", git.FindRoot(@"C:\Users\me\AppData\Local", At(now).Ms)); // cached on the way up
        Assert.Equal(calls, p.Calls);
        Assert.Null(git.FindRoot(@"C:\other\deep\dir", At(now).Ms));
        Assert.Null(git.FindRoot(@"\\server\share\x", At(now).Ms)); // never walks network paths
        Assert.Null(git.FindRoot(@"Z:\x", At(now).Ms)); // not a local fixed drive
        calls = p.Calls;
        Assert.Equal(@"C:\Users\me", git.FindRoot(@"C:\Users\me\AppData\Local\x", At(now.AddMinutes(9)).Ms)); // still cached
        Assert.Equal(calls, p.Calls);
        Assert.Equal(@"C:\Users\me", git.FindRoot(@"C:\Users\me\AppData\Local\x", At(now.AddMinutes(10)).Ms)); // re-probed when the cache expires
        Assert.True(p.Calls > calls);
    }

    [Fact]
    public void ClassifiesSuspiciousRoots()
    {
        var p = new FakeProbe();
        p.Repo(@"C:\Users\me", 0);
        p.Repo(@"C:\", 12);
        p.Repo(@"C:\Users\other", null);
        p.Repo(@"C:\Users\me\OneDrive", 5);
        p.Repo(@"E:\proj", 120);
        p.Dirs.Add(@"E:\empty\.git"); // an empty .git folder (no HEAD), as found in one report
        var git = new GitRootResolver(p);
        Assert.Equal(["a user profile", "no tracked files"], git.Classify(@"C:\Users\me", Ctx).Flags);
        Assert.Contains("a volume root", git.Classify(@"C:\", Ctx).Flags);
        Assert.Equal(["a user profile", "no tracked files (no index)"], git.Classify(@"C:\Users\other", Ctx).Flags);
        Assert.Equal(["a OneDrive root"], git.Classify(@"C:\Users\me\OneDrive", Ctx).Flags);
        var ok = git.Classify(@"E:\proj", Ctx);
        Assert.False(ok.Suspicious);
        Assert.Equal(120, ok.TrackedFiles);
        Assert.Contains("not a valid repository (no HEAD)", git.Classify(@"E:\empty", Ctx).Flags);
        p.Repo(@"C:\Users\me\OneDrive\Desktop", 3);
        Assert.Contains("a OneDrive known folder (Desktop)", git.Classify(@"C:\Users\me\OneDrive\Desktop", Ctx).Flags);
    }

    [Fact]
    public void FollowsGitdirFile()
    {
        var p = new FakeProbe();
        p.Files[@"E:\wt\.git"] = "gitdir: E:/main/.git/worktrees/wt\n"u8.ToArray();
        p.Files[@"E:\main\.git\worktrees\wt\HEAD"] = "ref: x"u8.ToArray();
        p.Files[@"E:\main\.git\worktrees\wt\index"] = FakeProbe.Index(7);
        var git = new GitRootResolver(p);
        Assert.Equal(@"E:\wt", git.FindRoot(@"E:\wt\src", Environment.TickCount64));
        Assert.Equal(7, git.Classify(@"E:\wt", Ctx).TrackedFiles);
    }

    [Fact]
    public void WinPathHelpers()
    {
        Assert.Equal(@"C:\", WinPath.Parent(@"C:\x"));
        Assert.Null(WinPath.Parent(@"C:\"));
        Assert.Equal(@"C:\a", WinPath.Normalize(@"c:/a/./b/.."));
        Assert.True(WinPath.IsSameOrUnder(@"C:\Users\me\x", @"c:\users\ME"));
        Assert.False(WinPath.IsSameOrUnder(@"C:\Users\meX", @"C:\Users\me"));
        Assert.True(WinPath.IsVolumeRoot(@"D:\"));
    }
}

public class SpawnTrackerTests
{
    private static readonly DateTime T0 = new(2026, 10, 26, 9, 0, 0, DateTimeKind.Utc);

    private sealed class Rig
    {
        public readonly ProcessRegistry Registry;
        public readonly FakeProbe Probe = new();
        public readonly SpawnTracker Tracker;
        public readonly Settings Settings = new() { SpawnStormThreshold = 30, SpawnStormWindowSeconds = 30, ShortLivedSeconds = 10 };
        public int NextPid = 1000;
        public DateTime Now = T0;
        /// <summary>The forward clock now, when a test sets it apart from <see cref="Now"/> (else <see cref="Clocks.At(DateTime)"/>'s).</summary>
        public long? Ms;
        public readonly ProcessRecord Parent;
        /// <summary>How far after now the registry reads its clock (an exit read as it arrives, after the start).</summary>
        private int _laterMs;

        public Rig()
        {
            // The registry reads the rig's two clocks, as the tracker is given them: a start and an exit are each read onto
            // the forward clock as they arrive.
            Registry = new(clock: () => Ms is { } ms ? new Moment(Now.AddMilliseconds(_laterMs), ms + _laterMs) : At(Now.AddMilliseconds(_laterMs)));
            Probe.Repo(@"C:\Users\me", 0);
            Tracker = new SpawnTracker(Settings, Registry, new GitRootResolver(Probe), new([@"C:\Users\me"], [], @"C:\Users"), _ => @"C:\Users\me\AppData\Local\App");
            Parent = Registry.OnStart(500, 4, T0.AddHours(-1).ToFileTimeUtc(), "ChatGPT.exe", @"C:\Apps\ChatGPT.exe", "ChatGPT.exe", null, T0.AddHours(-1));
            Registry.DrainEvents();
        }

        /// <summary>Starts a git child of the parent that exits after <paramref name="lifeMs"/>; returns this tick's events.</summary>
        public List<LifecycleEvent> Spawn(string image, string cl, int lifeMs)
        {
            int pid = NextPid++;
            var create = Now.ToFileTimeUtc();
            Registry.OnStart(pid, 500, create, image, null, cl, null, Now);
            _laterMs = lifeMs;
            Registry.OnStop(pid, create, 0, Now.AddMilliseconds(lifeMs));
            _laterMs = 0;
            return Registry.DrainEvents();
        }
    }

    [Fact]
    public void TheWindowAndAStormsCloseAreTimedOnTheClockThatOnlyGoesForward()
    {
        // Two short-lived starts a second. Ten seconds in, the system clock is set forward a day; ten seconds more: 40
        // short-lived starts in 20 s by the forward clock, over the threshold of 30 in the 30 s window, so a storm opens
        // (by the system clock the first 20 lie a day back, out of the window). Then the clock is set back an hour and
        // nothing more starts: the storm closes a minute after its starts fell below half the threshold, by the forward
        // clock, and its span is the forward clock's.
        var rig = new Rig();
        long ms = 0;
        var all = new List<Finding>();
        for (int s = 0; s < 20; s++)
        {
            if (s == 10) rig.Now = rig.Now.AddDays(1);
            rig.Ms = ms;
            var events = rig.Spawn("cmd.exe", "cmd /c echo", 80).Concat(rig.Spawn("cmd.exe", "cmd /c echo", 80)).ToList();
            all.AddRange(rig.Tracker.Observe(events, At(rig.Now, ms)).findings);
            rig.Now = rig.Now.AddSeconds(1);
            ms += 1000;
        }
        var opened = Assert.Single(all, f => f.Kind == FindingKind.SpawnStorm);
        Assert.Equal(FindingSeverity.Alert, opened.Severity);
        Assert.Single(rig.Tracker.OpenStorms);
        all.Clear();
        for (int s = 0; s < 150 && rig.Tracker.OpenStorms.Count > 0; s++)
        {
            if (s == 30) rig.Now = rig.Now.AddHours(-1); // the starts have left the window: quiet since
            all.AddRange(rig.Tracker.Observe([], At(rig.Now, ms)).findings);
            rig.Now = rig.Now.AddSeconds(1);
            ms += 1000;
        }
        var ended = Assert.Single(all, f => f.Kind == FindingKind.SpawnStorm);
        Assert.StartsWith("Spawn storm ended: ", ended.Title);
        Assert.Empty(rig.Tracker.OpenStorms);
        Assert.Contains(" processes in 1 min", ended.Title); // opened at 15 s and closed at 102 s on the forward clock, not a day or an hour off
    }

    /// <summary>
    /// The system time at <paramref name="ms"/> on the forward clock in the set-back tests: as the forward clock until
    /// <paramref name="forwardAt"/>, then a day ahead of it, then from <paramref name="backAt"/> an hour behind it.
    /// </summary>
    private static DateTime SetBackAndForward(long ms, long forwardAt, long backAt) =>
        T0.AddMilliseconds(ms) + (ms < forwardAt ? TimeSpan.Zero : ms < backAt ? TimeSpan.FromDays(1) : TimeSpan.FromHours(-1));

    [Fact]
    public void AnOpenStormsReportIsRefreshedEvery30MinutesOnTheClockThatOnlyGoesForward()
    {
        // 20 short-lived starts every 10 s keep a storm open for over an hour. Its report is written when it opens (as
        // Diagnostics writes it), then refreshed every 30 minutes by the forward clock: the system clock set forward a day
        // just after it opened, and back an hour 15 minutes later, neither refreshes it early nor holds it back.
        var rig = new Rig();
        var written = new List<long>();
        for (long ms = 0; ms <= 4_000_000; ms += 10_000)
        {
            rig.Now = SetBackAndForward(ms, 20_000, 900_000);
            rig.Ms = ms;
            var events = new List<LifecycleEvent>();
            for (int k = 0; k < 20; k++) events.AddRange(rig.Spawn("cmd.exe", "cmd /c echo", 80));
            var (_, toWrite) = rig.Tracker.Observe(events, At(rig.Now, ms));
            foreach (var storm in toWrite) storm.LastWriteMs = ms;
            if (toWrite.Count > 0) written.Add(ms / 1000);
        }
        Assert.Single(rig.Tracker.OpenStorms);
        Assert.Equal([10L, 1810L, 3610L], written); // opened at 10 s, then 30 and 60 forward minutes on
    }

    [Fact]
    public void ASuspiciousRootIsReportedOnceAnHourOnTheClockThatOnlyGoesForward()
    {
        // A git start in a suspicious root (a user profile with no tracked files) is reported, then that root not again for
        // an hour by the forward clock: the system clock set forward a day does not report it a minute on, and set back an
        // hour does not hold it back past the hour.
        var rig = new Rig();
        var reported = new List<long>();
        foreach (long ms in new long[] { 0, 60_000, 1_800_000, 3_540_000, 3_600_000 })
        {
            rig.Now = SetBackAndForward(ms, 60_000, 1_800_000);
            rig.Ms = ms;
            var (findings, _) = rig.Tracker.Observe(rig.Spawn("git.exe", "git status", 50), At(rig.Now, ms));
            if (findings.Any(f => f.Kind == FindingKind.GitRoot)) reported.Add(ms / 60_000);
        }
        Assert.Equal([0L, 60L], reported);
    }

    [Fact]
    public void TheGitRootCacheIsTimedOnTheClockThatOnlyGoesForward()
    {
        // The folder a git start ran in is looked up once and its root kept for 10 minutes by the forward clock: the system
        // clock set forward a day does not look it up again 9 minutes on, and set back an hour does not keep it past its
        // 10 minutes.
        var rig = new Rig();
        rig.Ms = 0;
        rig.Tracker.Observe(rig.Spawn("git.exe", "git status", 50), At(rig.Now, 0));
        int calls = rig.Probe.Calls;
        Assert.True(calls > 0);
        rig.Now = T0.AddDays(1).AddMinutes(9);
        rig.Ms = 9 * 60_000;
        rig.Tracker.Observe(rig.Spawn("git.exe", "git status", 50), At(rig.Now, 9 * 60_000));
        Assert.Equal(calls, rig.Probe.Calls); // still kept
        rig.Now = T0.AddHours(-1).AddMinutes(10);
        rig.Ms = 10 * 60_000;
        rig.Tracker.Observe(rig.Spawn("git.exe", "git status", 50), At(rig.Now, 10 * 60_000));
        Assert.True(rig.Probe.Calls > calls); // looked up again
    }

    [Fact]
    public void AChildsStartAndExitAreTheRegistrysOnTheForwardClockWhenItsExitWasReadFirst()
    {
        // A command runs 5 s; the system clock is set forward a day between its start and its exit, each read by the
        // registry as it arrived, and the tracker sees the start only after the exit. Its start and lifetime are the
        // registry's on the forward clock: it started in the window and was short-lived.
        var rig = new Rig { Ms = 0 };
        int pid = rig.NextPid++;
        var create = rig.Now.ToFileTimeUtc();
        rig.Registry.OnStart(pid, 500, create, "git.exe", null, "git status", null, rig.Now);
        (rig.Now, rig.Ms) = (rig.Now.AddDays(1).AddSeconds(5), 5000);
        rig.Registry.OnStop(pid, create, 0, rig.Now);
        rig.Tracker.Observe(rig.Registry.DrainEvents(), At(rig.Now, 5000));
        var rate = Assert.Single(rig.Tracker.Rates(At(rig.Now, 5000)));
        Assert.Equal(("git.exe", 1, 1), (rate.Image, rate.Starts, rate.ShortLived));
    }

    [Fact]
    public void AChildsExitIsTheRegistrysOnTheForwardClockWhenItsExitEventComesAfterItsStart()
    {
        // The tracker sees a git child's start at one tick; the registry reads its exit as it arrives; the system clock is
        // set before the next tick, which brings the exit event. The child's lifetime is the registry's on the forward clock.
        (int starts, int shortLived) Rate(int lifeMs, TimeSpan setAfterExit)
        {
            var rig = new Rig { Ms = 0 };
            int pid = rig.NextPid++;
            var create = rig.Now.ToFileTimeUtc();
            rig.Registry.OnStart(pid, 500, create, "git.exe", null, "git status", null, rig.Now);
            rig.Tracker.Observe(rig.Registry.DrainEvents(), At(rig.Now, 0));
            (rig.Now, rig.Ms) = (T0.AddMilliseconds(lifeMs), lifeMs);
            rig.Registry.OnStop(pid, create, 0, rig.Now);
            var exit = rig.Registry.DrainEvents();
            (rig.Now, rig.Ms) = (rig.Now.AddSeconds(1) + setAfterExit, lifeMs + 1000);
            rig.Tracker.Observe(exit, At(rig.Now, lifeMs + 1000));
            var rate = Assert.Single(rig.Tracker.Rates(At(rig.Now, lifeMs + 1000)));
            Assert.Equal("git.exe", rate.Image);
            return (rate.Starts, rate.ShortLived);
        }
        // It ran 20 s, the clock set forward a day before the tick: not short-lived (by the exit's time of day read at the
        // tick it would have ended before it started).
        Assert.Equal((1, 0), Rate(20_000, TimeSpan.FromDays(1)));
        // It ran 9.5 s, the clock set back an hour before the tick: short-lived (by the exit's time of day read at the tick,
        // which is later than the tick's and so is read as the tick, it would have run 10.5 s, past the 10 s limit, as it
        // would be if timed by the tick alone).
        Assert.Equal((1, 1), Rate(9_500, -TimeSpan.FromHours(1)));
    }

    [Fact]
    public void StormOpensWithPatternsAndFlaggedRootThenCloses()
    {
        var rig = new Rig();
        var all = new List<Finding>();
        var written = new List<SpawnStorm>();
        // 2 starts per second for 40 s: 60 per 30 s, every one exits in 80 ms.
        for (int s = 0; s < 40; s++)
        {
            var events = new List<LifecycleEvent>();
            events.AddRange(rig.Spawn("git.exe", "git status --porcelain", 80));
            events.AddRange(rig.Spawn(s % 2 == 0 ? "git.exe" : "taskkill.exe", s % 2 == 0 ? "git -c core.fsmonitor= ls-files --others -z" : "taskkill /pid 77 /T /F", 80));
            var (f, w) = rig.Tracker.Observe(events, At(rig.Now));
            all.AddRange(f); written.AddRange(w);
            rig.Now = rig.Now.AddSeconds(1);
        }
        var opened = Assert.Single(all, f => f.Kind == FindingKind.SpawnStorm);
        Assert.Equal(FindingSeverity.Alert, opened.Severity);
        Assert.Equal(500, opened.Pid);
        Assert.Contains("ChatGPT.exe", opened.Title);
        Assert.DoesNotContain("started 0 ", opened.Title); // peaks are set before the opening finding is built
        Assert.True((int)opened.Data["peakShortLived"]! > 30);
        var root = Assert.Single(all, f => f.Kind == FindingKind.GitRoot);
        Assert.Contains("a user profile", root.Title);
        Assert.Contains("no tracked files", root.Title);
        var storm = Assert.Single(rig.Tracker.OpenStorms);
        Assert.True(storm.PeakShortLived > 30);
        Assert.Equal("git status --porcelain", storm.Patterns.OrderByDescending(kv => kv.Value).First().Key);
        Assert.True(storm.GitRoots[@"C:\Users\me"].Info.Suspicious);
        Assert.Contains(storm, written);
        Assert.Contains("| git.exe |", rig.Tracker.StormMarkdown(storm));
        Assert.Contains("parent cwd", SpawnTracker.TraceCsv(storm));

        // Quiet: the storm closes a minute after the rate fell below half the threshold.
        all.Clear();
        for (int s = 0; s < 100; s++) { all.AddRange(rig.Tracker.Observe([], At(rig.Now)).findings); rig.Now = rig.Now.AddSeconds(1); }
        var closed = Assert.Single(all);
        Assert.StartsWith("Spawn storm ended", closed.Title);
        Assert.Empty(rig.Tracker.OpenStorms);
        Assert.Equal(80, storm.TotalStarts);
    }

    [Fact]
    public void LongLivedChildrenOrSlowRateDoNotStorm()
    {
        var rig = new Rig();
        var all = new List<Finding>();
        for (int s = 0; s < 60; s++)
        {
            all.AddRange(rig.Tracker.Observe(rig.Spawn("node.exe", "node worker.js", 60_000), At(rig.Now)).findings); // long-lived
            if (s % 2 == 0) all.AddRange(rig.Tracker.Observe(rig.Spawn("cmd.exe", "cmd /c echo", 50), At(rig.Now)).findings); // 15 per 30 s
            rig.Now = rig.Now.AddSeconds(1);
        }
        Assert.DoesNotContain(all, f => f.Kind == FindingKind.SpawnStorm);
        var rates = rig.Tracker.Rates(At(rig.Now));
        Assert.Equal("node.exe", rates[0].Image);
        Assert.Equal(0, rates[0].ShortLived);
    }

    [Fact]
    public void AStormsCsvKeepsPoolWatchsOwnValuesAndItsReportKeepsEachPatternInItsCell()
    {
        var rig = new Rig();
        rig.Probe.Repo(@"E:\proj", 10);
        SpawnStorm? storm = null;
        for (int s = 0; s < 40 && storm is null; s++)
        {
            var events = new List<LifecycleEvent>();
            events.AddRange(rig.Spawn("git.exe", @"git -C E:\proj status", 80));
            events.AddRange(rig.Spawn("cmd.exe", "cmd /c dir | findstr [INJ](https://example.invalid/x)", 80));
            rig.Tracker.Observe(events, At(rig.Now));
            storm = rig.Tracker.OpenStorms.FirstOrDefault();
            rig.Now = rig.Now.AddSeconds(1);
        }
        Assert.NotNull(storm);

        // spawns.csv: PoolWatch's own "where the folder came from" is never a cell a spreadsheet reads as a formula, so it
        // is written as it is (ci/diag-e2e.ps1 reads it so).
        var rows = SpawnTracker.TraceCsv(storm).Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(PoolWatch.Core.Util.Csv.Split).ToList();
        var git = rows.Where(r => r[2] == "git.exe").ToList();
        Assert.NotEmpty(git);
        Assert.All(git, r => Assert.Equal(SpawnTracker.GitDashC, r[6]));
        Assert.Equal("git -C", SpawnTracker.GitDashC);
        Assert.False(PoolWatch.Core.Util.Csv.IsFormulaStart(SpawnTracker.GitDashC));

        // report.md: a child's pipe stays inside its code span and its cell.
        var row = Assert.Single(rig.Tracker.StormMarkdown(storm).Split('\n').Select(l => l.TrimEnd('\r')), l => l.Contains("findstr"));
        Assert.Contains(@"dir \| findstr", row);
        int unescaped = 0;
        for (int i = 0; i < row.Length; i++)
        {
            if (row[i] != '|') continue;
            int run = 0;
            for (int j = i - 1; j >= 0 && row[j] == '\\'; j--) run++;
            if (run % 2 == 0) unescaped++;
        }
        Assert.Equal(3, unescaped); // | count | `pattern` |: two cells
    }

    [Fact]
    public void GitDashCWinsOverCwd()
    {
        var rig = new Rig();
        rig.Probe.Repo(@"E:\proj", 10);
        var (findings, _) = rig.Tracker.Observe(rig.Spawn("git.exe", @"git -C E:\proj\src status", 50), At(rig.Now));
        Assert.Empty(findings); // a normal repository is not flagged
        Assert.Equal(1, rig.Tracker.GitStartsSeen);
    }
}

public class StrayGitTests
{
    [Fact]
    public void ReportsOnceAndWhenRemoved()
    {
        var p = new FakeProbe();
        p.Dirs.Add(@"C:\Users\me\.git"); // empty, as in the report
        var check = new StrayGitCheck(p, new GitRootResolver(p));
        var ctx = new GitRootContext([@"C:\Users\me"], [], @"C:\Users");
        (string, string)[] where = [(@"C:\Users\me", "the user profile"), (@"C:\", "a volume root"), (@"\\srv\x", "network"), (@"C:\Users\me\", "dup")];
        var f1 = check.Run(where, ctx, DateTime.UtcNow);
        var hit = Assert.Single(f1);
        Assert.Equal(FindingKind.StrayGit, hit.Kind);
        Assert.Contains("user profile", hit.Title);
        Assert.Equal(2, check.LastRun.Count); // network path skipped, duplicate folded
        Assert.Empty(check.Run(where, ctx, DateTime.UtcNow));
        p.Dirs.Clear();
        Assert.Contains("gone", Assert.Single(check.Run(where, ctx, DateTime.UtcNow)).Title);
    }

    [Fact]
    public void FindingReportWritesFolder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pw-finding-" + Guid.NewGuid().ToString("N"));
        try
        {
            var f = new Finding { TimeUtc = DateTime.UtcNow, Kind = FindingKind.SpawnStorm, Title = "Spawn storm: a|b <c>", Details = ["x"] };
            string d = FindingReport.NewDir(dir, f, "ChatGPT.exe");
            FindingReport.Write(d, f, "body\n", new Dictionary<string, string> { ["spawns.csv"] = "a,b\n" }, x => x);
            Assert.Contains(@"a\|b &lt;c>", File.ReadAllText(Path.Combine(d, "report.md")));
            Assert.Contains("\"kind\": \"SpawnStorm\"", File.ReadAllText(Path.Combine(d, "report.json")));
            Assert.True(File.Exists(Path.Combine(d, "spawns.csv")));
            Assert.Contains("\"kind\":\"SpawnStorm\"", FindingLog.ToJsonLine(f));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
