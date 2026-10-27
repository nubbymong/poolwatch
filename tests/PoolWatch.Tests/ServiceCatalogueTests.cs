using PoolWatch.Core.Model;
using PoolWatch.Core.Services;

namespace PoolWatch.Tests;

/// <summary>Addendum 4: the catalogue, the protected gate, presets (tick only), and save/restore.</summary>
public class ServiceCatalogueTests
{
    private static readonly DateTime T0 = new(2026, 10, 26, 11, 35, 0, DateTimeKind.Utc);
    private static readonly ServiceCatalogue Cat = DefaultCatalogue.Load();

    private static readonly IReadOnlySet<string> NoDefaults = new HashSet<string>();

    private static ServiceView V(string name, ServiceStartType start = ServiceStartType.Automatic, bool ms = false, bool running = true, bool win32 = true) =>
        new() { Name = name, DisplayName = name, StartType = start, Running = running, MicrosoftSigned = ms, IsWin32 = win32 };

    [Fact]
    public void ShippedCatalogueCoversTheOwnersServices()
    {
        Assert.NotEmpty(Cat.Entries);
        var intel = Cat.Find("ESRV_SVC_QUEENCREEK");
        Assert.NotNull(intel);
        Assert.Equal("Intel System Usage Report", intel!.Name);
        Assert.Contains("USER_ESRV_SVC_QUEENCREEK", intel.Services); // the family is turned off together
        Assert.Equal(ServiceRisk.Safe, intel.Risk);
        Assert.True(intel.DevPreset);
        Assert.Equal(ServiceRisk.CheckFirst, Cat.Find("DiagTrack")!.Risk); // Insider note
    }

    [Theory]
    [InlineData("WinDefend")]
    [InlineData("WdNisSvc")]
    [InlineData("Sense")]
    [InlineData("mpssvc")]
    [InlineData("wuauserv")]
    [InlineData("TrustedInstaller")]
    [InlineData("Audiosrv")]
    [InlineData("TextInputManagementService")]
    [InlineData("RpcSs")]
    [InlineData("DcomLaunch")]
    public void SecurityUpdateInputAndCoreAreProtected(string service)
    {
        Assert.True(ServiceCatalogue.IsProtected(service, insiderEnrolled: false));
        Assert.Equal(ServiceGroup.Protected, Cat.GroupOf(service, microsoftSigned: true, insiderEnrolled: false));
        Assert.NotNull(ServiceCatalogue.ProtectedReason(service, false));
    }

    [Fact]
    public void InsiderServiceIsProtectedOnlyWhenEnrolled()
    {
        Assert.False(ServiceCatalogue.IsProtected("wisvc", insiderEnrolled: false));
        Assert.True(ServiceCatalogue.IsProtected("wisvc", insiderEnrolled: true));
    }

    [Fact]
    public void UnknownThirdPartyVsLeftAloneMicrosoft()
    {
        Assert.Equal(ServiceGroup.Unknown, Cat.GroupOf("SomeVendorSvc", microsoftSigned: false, insiderEnrolled: false));
        Assert.Equal(ServiceGroup.Protected, Cat.GroupOf("SomeMicrosoftSvc", microsoftSigned: true, insiderEnrolled: false));
        Assert.Equal(ServiceGroup.Optional, Cat.GroupOf("SysMain", microsoftSigned: true, insiderEnrolled: false));
    }

    [Fact]
    public void PresetsOnlyTickBoxesAndNeverTickUnknownOrProtected()
    {
        var services = new[]
        {
            V("ESRV_SVC_QUEENCREEK"), V("DiagTrack"), V("WerSvc"), V("WSearch"), V("SysMain"),
            V("WinDefend", ms: true), V("SomeVendorSvc"),
        };
        var opts = ServicePlan.Options(services, Cat, insiderEnrolled: false, NoDefaults);

        var telemetry = ServicePlan.Tick(Preset.TelemetryOnly, opts);
        Assert.Contains("ESRV_SVC_QUEENCREEK", telemetry);
        Assert.Contains("DiagTrack", telemetry);
        Assert.DoesNotContain("WSearch", telemetry);   // indexing, not telemetry
        Assert.DoesNotContain("WinDefend", telemetry);  // protected
        Assert.DoesNotContain("SomeVendorSvc", telemetry); // unknown

        var dev = ServicePlan.Tick(Preset.DevWorkstation, opts);
        Assert.Contains("SysMain", dev);                // preset: true
        Assert.DoesNotContain("WerSvc", dev);           // preset: false
        Assert.Empty(ServicePlan.Tick(Preset.Custom, opts));
    }

    [Fact]
    public void ApplyBuildsOperationsAndRecordsTheOriginal()
    {
        var services = new[] { V("SysMain", ServiceStartType.Automatic), V("XblAuthManager", ServiceStartType.Manual) };
        var opts = ServicePlan.Options(services, Cat, insiderEnrolled: false, NoDefaults);
        var ops = ServicePlan.BuildApply(["SysMain", "XblAuthManager"], opts, insiderEnrolled: false, "Dev workstation", T0);
        Assert.Equal(2, ops.Count);
        var sysmain = ops.Single(o => o.Option.Service.Name == "SysMain");
        Assert.Equal(ServiceStartType.Disabled, sysmain.NewStart);           // off: disabled
        Assert.Equal(ServiceStartType.Automatic, sysmain.Record.OriginalStart);
        Assert.True(sysmain.Record.OriginalRunning);
        var xbl = ops.Single(o => o.Option.Service.Name == "XblAuthManager");
        Assert.Equal(ServiceStartType.Manual, xbl.NewStart);                 // off: manual
    }

    [Fact]
    public void TheGateCannotBeBypassedByACraftedRequest()
    {
        // A request names a protected service, or a driver, or one not installed: all refused before any live change.
        var services = new[] { V("WinDefend", ServiceStartType.Automatic, ms: true), V("somedrv", ServiceStartType.Automatic, win32: false) };
        var opts = ServicePlan.Options(services, Cat, insiderEnrolled: false, NoDefaults);
        Assert.Throws<InvalidOperationException>(() => ServicePlan.BuildApply(["WinDefend"], opts, false, "manual", T0));
        Assert.Throws<InvalidOperationException>(() => ServicePlan.BuildApply(["somedrv"], opts, false, "manual", T0));
        Assert.Throws<InvalidOperationException>(() => ServicePlan.BuildApply(["NotInstalled"], opts, false, "manual", T0));
    }

    [Theory]
    [InlineData("vmms")]
    [InlineData("vmcompute")]
    [InlineData("HvHost")]
    [InlineData("WSLService")]
    [InlineData("BFE")]
    [InlineData("MDCoreSvc")]
    [InlineData("vmicheartbeat")] // vmic* prefix
    [InlineData("vmicvss")]
    public void VirtualizationAndFirewallAreProtected(string service)
    {
        Assert.True(ServiceCatalogue.IsProtected(service, insiderEnrolled: false));
        Assert.Equal(ServiceGroup.Protected, Cat.GroupOf(service, microsoftSigned: true, insiderEnrolled: false));
    }

    [Fact]
    public void ACatalogueCannotPromoteAHardProtectedService()
    {
        // S2: even if a catalogue (external or tampered) names a protected service, the hard gate still wins.
        var rogue = ServiceCatalogue.Load("""[{ "services": ["WinDefend"], "name": "x", "category": "Telemetry and diagnostics", "risk": "safe", "preset": true }]""");
        Assert.Equal(ServiceGroup.Protected, rogue.GroupOf("WinDefend", microsoftSigned: true, insiderEnrolled: false));
        Assert.True(ServiceCatalogue.IsProtected("WinDefend", insiderEnrolled: false));
    }

    [Fact]
    public void RestoreGateRefusesTamperedRecords()
    {
        // S1: a record read back from service-changes.json is re-validated against the live service.
        var good = new ServiceOption { Service = V("SysMain"), Group = ServiceGroup.Optional, Catalogue = Cat.Find("SysMain") };
        var rec = new ServiceChange { Service = "SysMain", OriginalStart = ServiceStartType.Automatic, OriginalRunning = true, NewStart = ServiceStartType.Disabled, ChangedUtc = T0 };
        ServicePlan.GateRestore(good, rec, insiderEnrolled: false); // ok

        // A service that is now protected: refused.
        var prot = new ServiceOption { Service = V("WinDefend", ms: true), Group = ServiceGroup.Protected };
        Assert.Throws<InvalidOperationException>(() => ServicePlan.GateRestore(prot, rec with { Service = "WinDefend" }, false));
        // A driver (not Win32): refused.
        var drv = new ServiceOption { Service = V("somedrv", win32: false), Group = ServiceGroup.Unknown };
        Assert.Throws<InvalidOperationException>(() => ServicePlan.GateRestore(drv, rec with { Service = "somedrv" }, false));
        // A tampered start type: refused.
        Assert.Throws<InvalidOperationException>(() => ServicePlan.GateRestore(good, rec with { OriginalStart = ServiceStartType.Unknown }, false));
        Assert.Throws<InvalidOperationException>(() => ServicePlan.GateRestore(good, rec with { OriginalStart = (ServiceStartType)99 }, false));
    }

    [Fact]
    public void OffOutsidePoolWatchTellsDefaultsFromOwnerChoices()
    {
        var defaults = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "RemoteRegistry", "ssh-agent" };
        var services = new[]
        {
            V("DiagTrack", ServiceStartType.Disabled, running: false),     // owner choice
            V("RemoteRegistry", ServiceStartType.Disabled, running: false), // Windows default
        };
        var opts = ServicePlan.Options(services, Cat, insiderEnrolled: false, defaults);
        Assert.Equal("off (changed outside PoolWatch)", opts.Single(o => o.Service.Name == "DiagTrack").OffOutsidePoolWatch);
        Assert.Equal("off (Windows default)", opts.Single(o => o.Service.Name == "RemoteRegistry").OffOutsidePoolWatch);
    }

    [Fact]
    public void ChangeLogUpsertsAndRoundTrips()
    {
        var c1 = new ServiceChange { Service = "SysMain", OriginalStart = ServiceStartType.Automatic, OriginalRunning = true, NewStart = ServiceStartType.Disabled, ChangedUtc = T0 };
        var list = ServiceChangeLog.Upsert(new List<ServiceChange>(), c1);
        var c1b = c1 with { NewStart = ServiceStartType.Manual };
        list = ServiceChangeLog.Upsert(list, c1b); // one record per service
        Assert.Single(list);
        Assert.Equal(ServiceStartType.Manual, list[0].NewStart);
        var back = ServiceChangeLog.Parse(ServiceChangeLog.ToJson(list));
        Assert.Single(back);
        Assert.Equal(ServiceStartType.Automatic, ServicePlan.RestoreTarget(back[0])); // restores to the original
        Assert.Empty(ServiceChangeLog.Parse("not json"));
    }
}

/// <summary>Addendum 4 A: per-process CPU / page-fault rates and the whole-machine CPU fraction.</summary>
public class ProcessLoadTests
{
    private const double HundredNsPerSec = 10_000_000.0;

    private static ProcessSample S(int pid, long kernel, long user, uint faults = 0) =>
        new() { Pid = pid, CreateTime = 1000 + pid, ImageName = pid == 0 ? "Idle" : $"p{pid}.exe", KernelTime = kernel, UserTime = user, PageFaultCount = faults };

    [Fact]
    public void IdleProcessIsNotCountedAsBusyCpu()
    {
        // C2: on a 4-core box idle for the whole 10 s window, the Idle process (PID 0) accrues ~4 cores of "CPU".
        // One real process uses half a core. The machine CPU fraction must reflect the real process, not ~100%.
        int cores = 4;
        var elapsed = TimeSpan.FromSeconds(10);
        long idleTicks = (long)(HundredNsPerSec * 10 * cores);       // all cores idle for 10 s
        long busyTicks = (long)(HundredNsPerSec * 10 * 0.5);         // half a core for 10 s
        var prev = new[] { S(0, 0, 0), S(100, 0, 0) };
        var cur = new[] { S(0, idleTicks, 0), S(100, busyTicks, 0) };

        var r = ProcessLoad.Between(prev, cur, elapsed, cores);
        Assert.Equal(0.5 / cores, r.CpuFraction, 3);                 // 0.125, not ~1.0
        Assert.DoesNotContain(r.Processes, p => p.Key.Pid == 0);     // Idle never listed as a suspect
        var p100 = Assert.Single(r.Processes);
        Assert.Equal(0.5, p100.Cores, 3);
    }
}

public class StartupApprovedTests
{
    private static readonly DateTime T = new(2026, 10, 26, 11, 35, 0, DateTimeKind.Utc);

    [Fact]
    public void EnabledWhenMissingOrLowBitClear()
    {
        Assert.True(StartupApproved.IsEnabled(null));
        Assert.True(StartupApproved.IsEnabled(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }));
        Assert.False(StartupApproved.IsEnabled(new byte[] { 0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }));
    }

    [Fact]
    public void MakeAndSetAreTaskManagerCompatible()
    {
        var disabled = StartupApproved.Make(enabled: false, T);
        Assert.Equal(12, disabled.Length);
        Assert.Equal(0x03, disabled[0]);
        Assert.False(StartupApproved.IsEnabled(disabled));
        Assert.Equal(T.ToFileTimeUtc(), BitConverter.ToInt64(disabled, 4));

        var reEnabled = StartupApproved.Set(disabled, enabled: true, T);
        Assert.True(StartupApproved.IsEnabled(reEnabled));
        Assert.Equal(12, reEnabled.Length);
        // A longer vendor value keeps its length when flipped.
        var longVal = new byte[16]; longVal[0] = 0x02;
        Assert.Equal(16, StartupApproved.Set(longVal, enabled: false, T).Length);
        Assert.False(StartupApproved.IsEnabled(StartupApproved.Set(longVal, enabled: false, T)));
    }
}
