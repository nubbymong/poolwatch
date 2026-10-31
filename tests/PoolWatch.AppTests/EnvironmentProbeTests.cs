using PoolWatch.Core.Model;
using PoolWatch.Core.Setup;
using PoolWatch.Core.TokenGuard;
using PoolWatch.Engine;
using PoolWatch.Engine.Setup;

namespace PoolWatch.AppTests;

/// <summary>
/// The setup guide's scan over a fake Windows: registry values, Store packages, fixed paths, volumes, WMI. Nothing real is
/// read; the cache file goes to the test's own TEMP folder.
/// </summary>
public sealed class EnvironmentProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pw-probe-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly Log _log;

    public EnvironmentProbeTests()
    {
        _paths = new AppPaths(Path.Combine(_root, "data"), null, secure: false);
        _log = new Log(Path.Combine(_root, "data", "probe.log"));
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private const string PackageRoot = @"C:\Program Files\WindowsApps\OpenAI.Codex_1.2.0.0_x64__2p2nqsd0c76g0";

    private sealed class FakeReads : IEnvironmentReads
    {
        public Dictionary<string, string> Registry = new() { ["ProductName"] = "Windows 10 Pro", ["DisplayVersion"] = "25H2", ["CurrentBuild"] = "26220", ["UBR"] = "7344" };
        public string? Branch = "CanaryChannel";
        public ushort? Machine = 0x8664;
        public List<VolumeInfo> Vols =
        [
            new(@"C:\", "NTFS", "", false, "Fixed"), new(@"D:\", "ReFS", "Dev", true, "Fixed"), new(@"E:\", "", "", null, "Removable"),
            new(@"F:\", "", "", null, "Fixed"),
        ];
        public List<string> TrustQueries = new();
        public Dictionary<string, string> Versions_ = new() { ["Claude Code"] = "2.1.4", ["Codex"] = "1.2.0.0" };
        public List<string> Packages = ["OpenAI.Codex_1.0.0.0_x64__2p2nqsd0c76g0", "OpenAI.Codex_1.2.0.0_x64__2p2nqsd0c76g0", "Microsoft.Paint_11.0.0.0_x64__8wekyb3d8bbwe"];
        public Func<string, bool> Exists = p => p.EndsWith(@"Microsoft VS Code\Code.exe", StringComparison.OrdinalIgnoreCase) || p.EndsWith(@"\.codex") ||
                                               p.EndsWith(@"\.claude") || p == PackageRoot + @"\app\ChatGPT.exe";
        public GuardState? Saved = new("26220.7344", GuardResult.Leak, DateTime.UtcNow.AddDays(-2), "test");
        public uint? Live = 200000;
        public bool Throw;
        public ManualResetEventSlim? Gate;
        public int Scans;

        private T Maybe<T>(T value) => Throw ? throw new InvalidOperationException("this read failed") : value;
        public string? CurrentVersion(string name)
        {
            if (name == "ProductName") { Interlocked.Increment(ref Scans); Gate?.Wait(TimeSpan.FromSeconds(30)); }
            return Maybe(Registry.GetValueOrDefault(name));
        }
        public string? InsiderBranchName() => Maybe(Branch);
        public ushort? NativeMachine() => Maybe(Machine);
        public long RamBytes() => Maybe(32L << 30);
        public IReadOnlyList<VolumeInfo> Volumes() => Maybe(Vols);
        public bool? DevDriveTrusted(string root) { TrustQueries.Add(root); return Maybe<bool?>(false); }
        public IReadOnlyDictionary<string, string> Versions() => Maybe(Versions_);
        public IReadOnlyList<string> PackageFullNames() => Maybe(Packages);
        public string? PackageRootFolder(string fullName) => Maybe(fullName == "OpenAI.Codex_1.2.0.0_x64__2p2nqsd0c76g0" ? PackageRoot : null);
        public bool PathExists(string path) => Maybe(Exists(path));
        public GuardState? SavedGuardState() => Maybe(Saved);
        public uint? LiveForegroundLockTimeout() => Maybe(Live);
        public IReadOnlyList<string> OwnClearTasks() => Maybe<IReadOnlyList<string>>([@"\Clear standby (RAMMap)"]);
        public bool LogonTaskExists() => Maybe(true);
        public bool InstalledInProgramFiles() => Maybe(true);
        public IReadOnlyList<string> RunningOptionalServices() => Maybe<IReadOnlyList<string>>(Enumerable.Range(1, 7).Select(n => $"Optional service {n}").ToList());
        public (bool? RealTime, bool? PerformanceMode) Defender() => Maybe<(bool?, bool?)>((true, false));
    }

    private EnvironmentProbe Probe(FakeReads reads) => new(_paths, _log, reads);

    [Fact]
    public async Task ReadsEachFactFromItsPlace()
    {
        var reads = new FakeReads();
        var f = await Probe(reads).ScanAsync();
        Assert.Equal("Windows 11 Pro", f.WindowsName); // ProductName still says Windows 10 on 11
        Assert.Equal("25H2", f.DisplayVersion);
        Assert.Equal("26220.7344", f.Build);
        Assert.Equal("Canary", f.InsiderChannel);
        Assert.Equal("x64", f.Architecture);
        Assert.Equal(32L << 30, f.RamBytes);
        Assert.Equal([new VolumeFact(@"C:\", "NTFS", false, null), new VolumeFact(@"D:\", "ReFS", true, false)], f.Volumes); // fixed, with a file system
        Assert.Equal([@"D:\"], reads.TrustQueries);                                                                         // only Dev Drives are asked
        Assert.Equal(["Claude Code", "Codex desktop", "VS Code"], f.AiTools.Select(t => t.Name));                            // .codex is the desktop app's here
        Assert.Equal("2.1.4", f.AiTools[0].Version);
        Assert.Equal(TokenLeakStatus.Leaks, f.TokenLeak);
        Assert.Equal([@"\Clear standby (RAMMap)"], f.OwnClearTasks);
        Assert.True(f.LogonTaskExists);
        Assert.True(f.InstalledInProgramFiles);
        Assert.Equal(7, f.OptionalServicesRunning);
        Assert.Equal(5, f.OptionalServiceExamples.Count);
        Assert.True(f.DefenderRealTimeOn);
        Assert.False(f.DefenderPerformanceModeOn);
        Assert.True(f.ChatGptInstalled);
        Assert.Equal(PackageRoot + @"\app\ChatGPT.exe", f.ChatGptIconPath); // the newest staged version
    }

    [Fact]
    public async Task ArmAndTheCodexCliAndNoInsider()
    {
        var reads = new FakeReads { Machine = 0xAA64, Branch = null, Packages = [], Versions_ = new() };
        var f = await Probe(reads).ScanAsync();
        Assert.Equal("ARM64", f.Architecture); // an x64 PoolWatch on Windows on Arm
        Assert.Null(f.InsiderChannel);
        Assert.False(f.ChatGptInstalled);
        Assert.Null(f.ChatGptIconPath);
        Assert.Equal(["VS Code", "Codex CLI", "Claude Code"], f.AiTools.Select(t => t.Name));
    }

    [Fact]
    public async Task FailedReadsAreNotKnownAndTheScanStillFinishes()
    {
        var f = await Probe(new FakeReads { Throw = true }).ScanAsync();
        Assert.Equal("Windows", f.WindowsName);
        Assert.Equal("x64", f.Architecture);
        Assert.Equal(TokenLeakStatus.Unknown, f.TokenLeak);
        Assert.Empty(f.Volumes);
        Assert.Empty(f.AiTools);
        Assert.Null(f.DefenderRealTimeOn);
        Assert.Null(f.DefenderPerformanceModeOn);
        Assert.False(f.ChatGptInstalled);
        Assert.Contains("failed", File.ReadAllText(Path.Combine(_root, "data", "probe.log")));
    }

    [Fact]
    public async Task TheScanIsCachedWithoutPathsAndGoesStale()
    {
        var reads = new FakeReads();
        var probe = Probe(reads);
        Assert.Null(probe.Last);
        Assert.True(probe.Stale(DateTime.UtcNow));
        var f = await probe.ScanAsync();
        string json = File.ReadAllText(Path.Combine(_paths.CacheDir, "environment.json"));
        Assert.DoesNotContain("WindowsApps", json); // the icon path is never written

        // A later start: the cached facts, without the icon path, until the next scan.
        var later = Probe(reads);
        Assert.Equal(f.Build, later.Last!.Build);
        Assert.Null(later.Last.ChatGptIconPath);
        Assert.False(later.Stale(f.ScannedUtc.AddHours(23)));
        Assert.True(later.Stale(f.ScannedUtc.AddHours(25)));
        Assert.True(later.Stale(f.ScannedUtc.AddMinutes(-5))); // dated after now: the clock was set back
        reads.Registry["UBR"] = "7400"; // Windows was updated
        Assert.True(later.Stale(f.ScannedUtc.AddMinutes(5)));
    }

    [Fact]
    public async Task OneScanAtATime()
    {
        using var gate = new ManualResetEventSlim();
        var reads = new FakeReads { Gate = gate };
        var probe = Probe(reads);
        var first = probe.ScanToTheEnd();
        var second = probe.ScanToTheEnd();
        Assert.Same(first, second);
        gate.Set();
        await first;
        Assert.Equal(1, reads.Scans);
        var next = probe.ScanToTheEnd(); // a finished scan does not stop the next one
        Assert.NotSame(first, next);
        await next;
        Assert.Equal(2, reads.Scans);
    }

    /// <summary>
    /// A caller that names no limit (the rescan after a change, the window reading the PC again) waits at most
    /// <see cref="EnvironmentProbe.HungAfter"/>: only setup's own scan waits to the end, under a limit of its own.
    /// </summary>
    [Fact]
    public async Task EveryScanThatNamesNoLimitIsGivenUpOnAfterAMinute()
    {
        using var gate = new ManualResetEventSlim();
        long now = 0;
        var probe = new EnvironmentProbe(_paths, _log, new FakeReads { Gate = gate }, () => Interlocked.Read(ref now));
        var scan = probe.ScanToTheEnd(); // a read that hangs
        try
        {
            var early = probe.ScanAsync();   // joins it with the whole minute left: waits for it
            Assert.False(early.IsCompleted);
            Interlocked.Exchange(ref now, (long)((EnvironmentProbe.HungAfter.TotalSeconds + 1) * System.Diagnostics.Stopwatch.Frequency));
            var late = probe.ScanAsync();    // a minute and a second on: given up on at once
            Assert.True(late.IsFaulted);
            Assert.IsType<TimeoutException>(late.Exception!.InnerException);
            Assert.False(scan.IsCompleted);  // while the scan goes on
        }
        finally
        {
            gate.Set();
            await scan; // it writes the cache file as it ends: before the test's folder is removed, not after
        }
    }

    [Fact]
    public async Task AScanThatRunsPastItsLimitIsGivenUpOnAtOnceAndStillLands()
    {
        using var gate = new ManualResetEventSlim();
        var reads = new FakeReads { Gate = gate };
        var probe = Probe(reads);
        var scan = probe.ScanToTheEnd();                                    // setup's own wait for it
        try
        {
            var patient = probe.ScanAsync(TimeSpan.FromSeconds(30));        // joins it, with time left: waits for it
            var bounded = probe.ScanAsync(TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsAsync<TimeoutException>(() => bounded);     // given up on at its limit
            Assert.False(scan.IsCompleted || patient.IsCompleted);         // while the scan goes on
            // Joining a scan that has run past the limit waits for none of it: what is left of the limit is nothing.
            var late = probe.ScanAsync(TimeSpan.FromMilliseconds(100));
            Assert.True(late.IsFaulted);
            Assert.IsType<TimeoutException>(late.Exception!.InnerException);
            gate.Set();
            var facts = await patient;
            Assert.Same(await scan, facts);
            Assert.Same(facts, probe.Last);                                 // and it lands where everyone reads it
            Assert.Equal(1, reads.Scans);                                   // one scan for all of them
        }
        finally
        {
            gate.Set();
            await scan; // ended before the test's folder is removed, even when an assert above failed
        }

        // No scan running: a new one, waited for while it reads.
        var before = probe.Last;
        var again = await probe.ScanAsync(TimeSpan.FromSeconds(30));
        Assert.NotSame(before, again);
        Assert.Same(again, probe.Last);
        Assert.Equal(2, reads.Scans);
    }

    [Fact]
    public void TheNewestChatGptPackage()
    {
        Assert.Equal("OpenAI.ChatGPT-Desktop_1.2026.10.0_x64__2p2nqsd0c76g0",
            EnvironmentProbe.ChatGptPackage(["OpenAI.ChatGPT-Desktop_1.2025.1.0_x64__2p2nqsd0c76g0", "OpenAI.ChatGPT-Desktop_1.2026.10.0_x64__2p2nqsd0c76g0"]));
        Assert.Null(EnvironmentProbe.ChatGptPackage(["OpenAI.CodexHelper_1.0.0.0_x64__x", "Microsoft.Paint_11.0.0.0_x64__8wekyb3d8bbwe"]));
    }
}
