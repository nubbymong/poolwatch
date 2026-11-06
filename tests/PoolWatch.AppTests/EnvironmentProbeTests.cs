using PoolWatch.Core.Impact;
using PoolWatch.Core.Model;
using PoolWatch.Core.Setup;
using PoolWatch.Core.TokenGuard;
using PoolWatch.Engine;
using PoolWatch.Engine.Setup;
using PoolWatch.Ui.Setup;

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

    internal sealed class FakeReads : IEnvironmentReads
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
        /// <summary>The one read that fails, by its name ("Volumes"), the others answering.</summary>
        public string? FailOnly;
        public ManualResetEventSlim? Gate;
        public int Scans;

        private T Maybe<T>(T value) => Throw ? throw new InvalidOperationException("this read failed") : value;
        private T Maybe<T>(string read, T value) => read == FailOnly ? throw new InvalidOperationException("this read failed") : Maybe(value);
        public string? CurrentVersion(string name)
        {
            if (name == "ProductName") { Interlocked.Increment(ref Scans); Gate?.Wait(TimeSpan.FromSeconds(30)); }
            return Maybe(Registry.GetValueOrDefault(name));
        }
        public string? InsiderBranchName() => Maybe(Branch);
        public ushort? NativeMachine() => Maybe(Machine);
        public long RamBytes() => Maybe(32L << 30);
        public IReadOnlyList<VolumeInfo> Volumes() => Maybe(nameof(Volumes), Vols);
        public bool? DevDriveTrusted(string root) { TrustQueries.Add(root); return Maybe<bool?>(false); }
        public IReadOnlyDictionary<string, string> Versions() => Maybe(Versions_);
        public IReadOnlyList<string> PackageFullNames() => Maybe(nameof(PackageFullNames), Packages);
        public string? PackageRootFolder(string fullName) => Maybe(fullName == "OpenAI.Codex_1.2.0.0_x64__2p2nqsd0c76g0" ? PackageRoot : null);
        public bool PathExists(string path) => Maybe(Exists(path));
        public GuardState? SavedGuardState() => Maybe(Saved);
        public uint? LiveForegroundLockTimeout() => Maybe(Live);
        public IReadOnlyList<string> OwnClearTasks() => Maybe<IReadOnlyList<string>>([@"\Clear standby (RAMMap)"]);
        public SignInState SignIn = SignInState.Starts;
        public SignInState SignInTask() => Maybe(SignIn);
        public bool InstalledInProgramFiles() => Maybe(true);
        public IReadOnlyList<string> RunningOptionalServices() =>
            Maybe<IReadOnlyList<string>>(nameof(RunningOptionalServices), Enumerable.Range(1, 7).Select(n => $"Optional service {n}").ToList());
        public (bool? RealTime, bool? PerformanceMode) Defender() => Maybe<(bool?, bool?)>((true, false));
    }

    private EnvironmentProbe Probe(FakeReads reads, Func<PoolWatch.Core.Model.Moment>? now = null) => new(_paths, _log, reads, null, now);

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
        Assert.Equal([@"F:\"], f.UnreadVolumes);                                                                           // fixed, its file system not read
        Assert.Equal([@"D:\"], reads.TrustQueries);                                                                         // only Dev Drives are asked
        Assert.Equal(["Claude Code", "Codex desktop", "VS Code"], f.AiTools.Select(t => t.Name));                            // .codex is the desktop app's here
        Assert.Equal("2.1.4", f.AiTools[0].Version);
        Assert.Equal(TokenLeakStatus.Leaks, f.TokenLeak);
        Assert.Equal([@"\Clear standby (RAMMap)"], f.OwnClearTasks);
        Assert.Equal(SignInState.Starts, f.SignIn);
        Assert.True(f.InstalledInProgramFiles);
        Assert.Equal(7, f.OptionalServicesRunning);
        Assert.Equal(5, f.OptionalServiceExamples.Count);
        Assert.True(f.DefenderRealTimeOn);
        Assert.False(f.DefenderPerformanceModeOn);
        Assert.True(f.ChatGptInstalled);
        Assert.Equal(PackageRoot + @"\app\ChatGPT.exe", f.ChatGptIconPath); // the newest staged version
        Assert.Equal((false, false, false), (f.VolumesNotRead, f.PackagesNotRead, f.ServicesNotRead));
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

    /// <summary>
    /// The scan records the logon task's state as read, each state as itself (the guide and the suggestions word each),
    /// and in the cache. The real read is the logon task's own state, judged.
    /// </summary>
    [Fact]
    public async Task TheSignInStateIsRecordedAsRead()
    {
        foreach (var state in Enum.GetValues<SignInState>())
        {
            var f = await Probe(new FakeReads { SignIn = state }).ScanAsync();
            Assert.Equal(state, f.SignIn);
            Assert.Equal(state, EnvironmentFacts.Parse(File.ReadAllText(Path.Combine(_paths.CacheDir, "environment.json")))!.SignIn);
        }
        Assert.Contains("public SignInState SignInTask() => Ui.LogonTask.AtSignIn();",
            File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Engine", "Setup", "EnvironmentProbe.cs")));
    }

    /// <summary>
    /// After an upgrade, the cache an older PoolWatch wrote is not read, so there are no facts until the PC is read again:
    /// the tray's token-leak watch then reads it itself, once setup was completed, instead of waiting for the window. With
    /// facts, or before setup was completed (setup reads the PC), it reads nothing.
    /// </summary>
    [Fact]
    public async Task TheTraysWatchReadsThePcWhenAnUpgradeLeftNoFacts()
    {
        var written = await Probe(new FakeReads()).ScanAsync();
        string cache = Path.Combine(_paths.CacheDir, "environment.json");
        var older = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(cache))!.AsObject();
        Assert.True(older.Remove("signIn"));
        older["logonTaskExists"] = true; // the sign-in fact as 1.0.0-beta.1 wrote it
        File.WriteAllText(cache, older.ToJsonString());

        var probe = Probe(new FakeReads());
        Assert.Null(probe.Last);
        Assert.Null(await Ui.TrayApp.FactsForWatch(setupCompleted: false, probe));
        Assert.Null(probe.Last); // nothing read before setup was completed
        var read = await Ui.TrayApp.FactsForWatch(setupCompleted: true, probe);
        Assert.NotNull(read);
        Assert.NotSame(written, read);
        Assert.Same(read, probe.Last);
        Assert.Equal(TokenLeakStatus.Leaks, read!.TokenLeak);
        Assert.Same(read, await Ui.TrayApp.FactsForWatch(setupCompleted: true, probe)); // facts there: not read again
        string tray = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Ui", "TrayApp.cs")).Replace("\r\n", "\n");
        Assert.Contains("var facts = await FactsForWatch(Setup.Store.GuideCompleted, Setup.Probe);\n            if (!WatchFromTray(Setup.Store.GuideCompleted, facts?.TokenLeak)) return;", tray);
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
        Assert.Equal(0, f.OptionalServicesRunning);
        Assert.Equal((true, true, true), (f.VolumesNotRead, f.PackagesNotRead, f.ServicesNotRead)); // none of the three is "none"
        // A logon task that could not be read is not known: never "turned off" (the guide would then leave it), nor "starts".
        Assert.Equal(SignInState.NotKnown, f.SignIn);
        Assert.Contains("failed", File.ReadAllText(Path.Combine(_root, "data", "probe.log")));
    }

    /// <summary>
    /// The volumes, the Store apps or the running services the scan could not read are recorded, so setup says Not known
    /// for the causes that need them instead of "none", now and after the next start reads the cache.
    /// </summary>
    [Theory]
    [InlineData(nameof(FakeReads.Volumes), "DevDriveRecords Defender")] // Defender is on here: with no drive read, a Dev Drive cannot be ruled out
    [InlineData(nameof(FakeReads.PackageFullNames), "CodexShaderCache")]
    [InlineData(nameof(FakeReads.RunningOptionalServices), "BackgroundServices")]
    public async Task AListTheScanCouldNotReadIsRecordedSoSetupSaysNotKnown(string read, string notKnown)
    {
        var probe = Probe(new FakeReads { FailOnly = read });
        var f = await probe.ScanAsync();
        var flags = (f.VolumesNotRead, f.PackagesNotRead, f.ServicesNotRead);
        Assert.Equal((read == nameof(FakeReads.Volumes), read == nameof(FakeReads.PackageFullNames), read == nameof(FakeReads.RunningOptionalServices)), flags);
        Assert.Equal(notKnown, string.Join(" ", ImpactRows.From(f, LeakState.NotFound).Where(r => r.NotKnown).Select(r => r.Cause)));
        var cached = EnvironmentFacts.Parse(File.ReadAllText(Path.Combine(_paths.CacheDir, "environment.json")))!;
        Assert.Equal(flags, (cached.VolumesNotRead, cached.PackagesNotRead, cached.ServicesNotRead));
    }

    /// <summary>
    /// A volume the scan could not read whole is recorded as not read, never left out as a drive with no Dev Drive and no
    /// ReFS: one whose file system Windows could not give, fixed or of a drive type it could not tell, is named; a ReFS one
    /// Windows could not say is a Dev Drive is not known to be one either way. One whose file system was read counts by it,
    /// whatever its drive type: an NTFS one is never a Dev Drive. Setup then says Not known for the causes such a drive could
    /// change, naming the drives, now and after the next start reads the cache.
    /// </summary>
    [Fact]
    public async Task AVolumeTheScanCouldNotReadWholeIsNotKnownNeverADriveWithoutADevDrive()
    {
        var reads = new FakeReads
        {
            Vols =
            [
                new(@"C:\", "NTFS", "", null, "Fixed"), new(@"E:\", "", "", null, "Removable"), new(@"F:\", "", "", null, "Fixed"),
                new(@"G:\", "ReFS", "", null, "Fixed"), new(@"H:\", "NTFS", "", false, "Unknown"), new(@"I:\", "", "", null, "Unknown"),
                new(@"J:\", "ReFS", "", null, "Unknown"),
            ],
        };
        var f = await Probe(reads).ScanAsync();
        Assert.Equal([new VolumeFact(@"C:\", "NTFS", false, null), new VolumeFact(@"G:\", "ReFS", null, null), new VolumeFact(@"H:\", "NTFS", false, null),
            new VolumeFact(@"J:\", "ReFS", null, null)], f.Volumes);
        Assert.Equal([@"F:\", @"I:\"], f.UnreadVolumes); // never a removable one: setup does not look at those
        Assert.Empty(reads.TrustQueries);                  // no drive known to be a Dev Drive
        Assert.True(f.VolumesNotAllRead);
        var rows = ImpactRows.From(f, LeakState.NotFound);
        // G: is ReFS: the Dev Drive row's level stands. Defender is on with performance mode off here, so Low (no Dev Drive)
        // would be said of a PC whose G:, J:, F: or I: may be a Dev Drive Defender checks first: not known.
        Assert.Equal(ImpactLevel.High, rows[0].Level);
        Assert.True(rows[1].NotKnown);
        Assert.Null(rows[1].Level);
        Assert.Equal("PoolWatch could not read F: and I:. PoolWatch could not read whether G: and J: are Dev Drives.", rows[1].Detail);
        // Without G: and J:, no ReFS drive was found, and F: or I: may be one: not known, never "No Dev Drive or ReFS drive".
        var devDriveRow = ImpactRows.From(f with { Volumes = [f.Volumes[0], f.Volumes[2]] }, LeakState.NotFound)[0];
        Assert.True(devDriveRow.NotKnown);
        Assert.Equal("PoolWatch could not read F: and I:.", devDriveRow.Detail);
        var cached = EnvironmentFacts.Parse(File.ReadAllText(Path.Combine(_paths.CacheDir, "environment.json")))!;
        Assert.Equal(f.UnreadVolumes, cached.UnreadVolumes);
        Assert.Equal(f.Volumes, cached.Volumes);

        // A volume of a drive type Windows could not tell whose file system was read as NTFS leaves nothing not known.
        var ntfs = await Probe(new FakeReads { Vols = [new(@"C:\", "NTFS", "", null, "Fixed"), new(@"H:\", "NTFS", "", null, "Unknown")] }).ScanAsync();
        Assert.Equal([new VolumeFact(@"C:\", "NTFS", false, null), new VolumeFact(@"H:\", "NTFS", false, null)], ntfs.Volumes);
        Assert.Empty(ntfs.UnreadVolumes);
        Assert.False(ntfs.VolumesNotAllRead);
        Assert.DoesNotContain(ImpactRows.From(ntfs, LeakState.NotFound), r => r.NotKnown && r.Detail.Contains("H:"));
    }

    [Fact]
    public void NoRunningServiceAtAllMeansTheServicesCouldNotBeRead()
    {
        // Windows always runs services of its own: an empty list is a list the service control manager did not give.
        Assert.Throws<InvalidOperationException>(() => WindowsEnvironmentReads.OptionalOf([], _ => true));
        ServiceEntry[] running = [new("svcB", "Beta Service", 4), new("svcA", "Alpha Service", 8), new("svcX", "Example Device Service", 12), new("svcA2", "alpha service", 16)];
        Assert.Equal(["Alpha Service", "Beta Service"], WindowsEnvironmentReads.OptionalOf(running, name => name != "svcX"));
        Assert.Empty(WindowsEnvironmentReads.OptionalOf(running, _ => false)); // services run, none PoolWatch knows: read, and none
    }

    [Fact]
    public async Task TheScanIsCachedWithoutPathsAndGoesStale()
    {
        // The test's clocks a minute ahead of the real one (a scan stamps its facts with the real time).
        var clocks = new TwoClocks();
        clocks.SetSystemClock(DateTime.UtcNow.AddMinutes(1) - clocks.Utc);
        var reads = new FakeReads();
        var probe = Probe(reads, clocks.Now);
        Assert.Null(probe.Last);
        Assert.True(probe.Stale());
        var f = await probe.ScanAsync();
        Assert.False(probe.Stale());
        string json = File.ReadAllText(Path.Combine(_paths.CacheDir, "environment.json"));
        Assert.DoesNotContain("WindowsApps", json); // the icon path is never written

        // A later start: the cached facts, without the icon path, until the next scan. Their age is read onto the clock that
        // only goes forward as the file is read, and timed on it: the system clock set back or forward changes nothing.
        var later = Probe(reads, clocks.Now);
        Assert.Equal(f.Build, later.Last!.Build);
        Assert.Null(later.Last.ChatGptIconPath);
        clocks.Pass(TimeSpan.FromHours(23));
        Assert.False(later.Stale());
        string ubr = reads.Registry["UBR"];
        reads.Registry["UBR"] = "7400"; // Windows was updated
        Assert.True(later.Stale());
        reads.Registry["UBR"] = ubr;
        Assert.False(later.Stale());
        clocks.SetSystemClock(-TimeSpan.FromDays(2));
        Assert.False(later.Stale());
        clocks.SetSystemClock(TimeSpan.FromDays(5));
        Assert.False(later.Stale());
        clocks.Pass(TimeSpan.FromMinutes(61));
        Assert.True(later.Stale());
        Assert.True(probe.Stale()); // the scan of this run: 24 hours on the forward clock
        // A file dated after now when it is read (the clock was set back, or the file edited) would never age: stale.
        Assert.True(Probe(reads, () => new PoolWatch.Core.Model.Moment(f.ScannedUtc.AddMinutes(-5), 1000)).Stale());
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
