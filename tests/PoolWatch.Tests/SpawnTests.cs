using PoolWatch.Core;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Model;
using PoolWatch.Core.Spawn;

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
        Assert.Equal(@"C:\Users\me", git.FindRoot(@"C:\Users\me\AppData\Local\x", now));
        int calls = p.Calls;
        Assert.Equal(@"C:\Users\me", git.FindRoot(@"C:\Users\me\AppData\Local", now)); // cached on the way up
        Assert.Equal(calls, p.Calls);
        Assert.Null(git.FindRoot(@"C:\other\deep\dir", now));
        Assert.Null(git.FindRoot(@"\\server\share\x", now)); // never walks network paths
        Assert.Null(git.FindRoot(@"Z:\x", now)); // not a local fixed drive
        Assert.Equal(@"C:\Users\me", git.FindRoot(@"C:\Users\me\AppData\Local\x", now.AddMinutes(11))); // re-probed after the cache expires
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
        Assert.Equal(@"E:\wt", git.FindRoot(@"E:\wt\src", DateTime.UtcNow));
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
        public readonly ProcessRegistry Registry = new();
        public readonly FakeProbe Probe = new();
        public readonly SpawnTracker Tracker;
        public readonly Settings Settings = new() { SpawnStormThreshold = 30, SpawnStormWindowSeconds = 30, ShortLivedSeconds = 10 };
        public int NextPid = 1000;
        public DateTime Now = T0;
        public readonly ProcessRecord Parent;

        public Rig()
        {
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
            Registry.OnStop(pid, create, 0, Now.AddMilliseconds(lifeMs));
            return Registry.DrainEvents();
        }
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
            var (f, w) = rig.Tracker.Observe(events, rig.Now);
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
        for (int s = 0; s < 100; s++) { all.AddRange(rig.Tracker.Observe([], rig.Now).findings); rig.Now = rig.Now.AddSeconds(1); }
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
            all.AddRange(rig.Tracker.Observe(rig.Spawn("node.exe", "node worker.js", 60_000), rig.Now).findings); // long-lived
            if (s % 2 == 0) all.AddRange(rig.Tracker.Observe(rig.Spawn("cmd.exe", "cmd /c echo", 50), rig.Now).findings); // 15 per 30 s
            rig.Now = rig.Now.AddSeconds(1);
        }
        Assert.DoesNotContain(all, f => f.Kind == FindingKind.SpawnStorm);
        var rates = rig.Tracker.Rates(rig.Now);
        Assert.Equal("node.exe", rates[0].Image);
        Assert.Equal(0, rates[0].ShortLived);
    }

    [Fact]
    public void GitDashCWinsOverCwd()
    {
        var rig = new Rig();
        rig.Probe.Repo(@"E:\proj", 10);
        var (findings, _) = rig.Tracker.Observe(rig.Spawn("git.exe", @"git -C E:\proj\src status", 50), rig.Now);
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
