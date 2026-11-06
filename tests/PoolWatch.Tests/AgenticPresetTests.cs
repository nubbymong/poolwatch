using PoolWatch.Core.Services;

namespace PoolWatch.Tests;

/// <summary>
/// The built-in "Agentic coding PC" profile (it was "Dev workstation"): what it ticks, and that it rests only on rows with
/// an official source, never a protected service and never anything an agentic coding PC needs.
/// </summary>
public class AgenticPresetTests
{
    private static readonly ServiceCatalogue Cat = DefaultCatalogue.Load();

    /// <summary>What it turns off, and why each is not needed on a PC used for coding with AI agents (in the report too).</summary>
    private static readonly string[] Expected =
    [
        "DiagTrack",      // diagnostic data to Microsoft
        "SysMain",        // prefetching; the cost is a slightly slower first launch
        "MapsBroker",     // offline maps
        "SEMgrSvc",       // NFC payments
        "WMPNetworkSvc",  // Windows Media Player's library sharing
        "XblAuthManager", "XblGameSave", "XboxNetApiSvc", // Xbox app and Game Pass
    ];

    /// <summary>
    /// What an agentic coding PC needs, by service: networking, Windows Update, Defender and the firewall, audio, display,
    /// input (Bluetooth, game controllers, touch and pen), WSL, Hyper-V and containers, location for the time zone, the
    /// mobile hotspot, printing, search, and troubleshooting.
    /// </summary>
    private static readonly string[] Needed =
    [
        "Dhcp", "Dnscache", "NlaSvc", "nsi", "WlanSvc", "LanmanWorkstation", "LanmanServer", "netprofm", "iphlpsvc", "WinHttpAutoProxySvc", "icssvc",
        "SmsRouter", "autotimesvc", "RmSvc", "DusmSvc",
        "wuauserv", "UsoSvc", "BITS", "DoSvc", "TrustedInstaller", "InstallService",
        "WinDefend", "WdNisSvc", "Sense", "SecurityHealthService", "mpssvc", "wscsvc", "BFE", "MDCoreSvc",
        "Audiosrv", "AudioEndpointBuilder", "BTAGService", "bthserv",
        "DispBrokerDesktopSvc", "GraphicsPerfSvc", "Themes", "UxSms",
        "TabletInputService", "TextInputManagementService", "hidserv", "XboxGipSvc", "GameInputSvc",
        "WSLService", "LxssManager", "vmcompute", "vmms", "HvHost", "CmService", "hns", "vmicheartbeat",
        "lfsvc", "Spooler", "WSearch", "DPS", "WdiSystemHost", "DiagSvc", "VSS", "defragsvc", "WerSvc",
    ];

    private static ServiceView On(string name) => new() { Name = name, DisplayName = name, StartType = ServiceStartType.Automatic, Running = true, MicrosoftSigned = true };

    [Fact]
    public void ItIsCalledAgenticCodingPcAndTheOldNameIsFree()
    {
        Assert.Equal("Agentic coding PC", ServiceProfileRules.AgenticCodingPc);
        Assert.Contains("Agentic coding PC", ServiceProfileRules.BuiltIns);
        Assert.Equal("Agentic coding PC is a built-in profile", ServiceProfileRules.NameProblem(" Agentic coding PC "));
        Assert.True(ServiceProfileRules.IsBuiltIn("agentic CODING pc"));
        Assert.Null(ServiceProfileRules.NameProblem("Dev workstation"));
    }

    [Fact]
    public void ItHoldsExactlyTheseRows()
    {
        var inIt = Cat.Entries.Where(ServicePlan.InAgenticPreset).SelectMany(e => e.Services);
        Assert.Equal(Expected.Order(StringComparer.OrdinalIgnoreCase), inIt.Order(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>On a PC where every catalogued service is on, it ticks exactly its rows' services.</summary>
    [Fact]
    public void OnAPcWithEveryServiceOnItTicksExactlyThose()
    {
        var all = Cat.Entries.SelectMany(e => e.Services).Concat(Needed).Distinct(StringComparer.OrdinalIgnoreCase).Select(On);
        var options = ServicePlan.Options(all, Cat, insiderEnrolled: false, new HashSet<string>());
        Assert.Equal(Expected.Order(StringComparer.OrdinalIgnoreCase), ServicePlan.Tick(Preset.AgenticCodingPc, options).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void EveryRowInItHasASourceAndNoneIsProtectedOrNeeded()
    {
        foreach (var e in Cat.Entries.Where(ServicePlan.InAgenticPreset))
        {
            Assert.NotEqual(ServiceSourceKind.None, e.Source.Kind);
            Assert.NotEqual("Vendor helpers and updaters", e.Category);
            foreach (var s in e.Services)
            {
                Assert.False(ServiceCatalogue.IsProtected(s, insiderEnrolled: true), s);
                Assert.DoesNotContain(s, Needed, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// On a PC left at Windows defaults (every catalogued Windows service at its default start type, running), it ticks
    /// all eight of its services. Windows ships Xbox Live's three at Manual, and its row turns them off to Disabled, as both
    /// sources set them, so at the default they are on and ticked. Xbox accessories is turned off to Disabled too, so at its
    /// Manual default it reads as on and Turn off changes it; this profile leaves it out (game controllers). Windows Error
    /// Reporting is turned off to Manual, its default, so there it is off already.
    /// </summary>
    [Fact]
    public void AtWindowsDefaultsItTicksAllEightXboxLiveIncluded()
    {
        var options = OptionsAtDefaults(Cat);
        Assert.Equal(Expected.Order(StringComparer.OrdinalIgnoreCase),
            ServicePlan.Tick(Preset.AgenticCodingPc, options).Order(StringComparer.OrdinalIgnoreCase));

        foreach (var name in new[] { "XblAuthManager", "XblGameSave", "XboxNetApiSvc", "XboxGipSvc" })
        {
            var o = options.Single(x => x.Service.Name == name);
            Assert.Equal(ServiceStartType.Manual, o.Service.StartType); // as Windows ships it
            Assert.False(ServicePlan.IsOff(o), name);
            Assert.Equal(ServiceStartType.Disabled, ServicePlan.OffStart(o));
        }
    }

    /// <summary>
    /// "Telemetry only" on the same PC ticks the four telemetry services that are on there. Windows Error Reporting is
    /// turned off to Manual, the start type Windows ships it with, so it is off already and not ticked: PoolWatch's own
    /// crash findings read its records.
    /// </summary>
    [Fact]
    public void AtWindowsDefaultsTelemetryOnlyTicksFourAndNotErrorReporting()
    {
        var options = OptionsAtDefaults(Cat);
        Assert.Equal(["DiagTrack", "dmwappushservice", "InventorySvc", "PcaSvc"],
            ServicePlan.Tick(Preset.TelemetryOnly, options).Order(StringComparer.OrdinalIgnoreCase));
        var wer = options.Single(x => x.Service.Name == "WerSvc");
        Assert.Equal(ServiceStartType.Manual, wer.Service.StartType); // as Windows ships it
        Assert.True(ServicePlan.IsOff(wer));
        Assert.Equal(ServiceStartType.Manual, ServicePlan.OffStart(wer));
    }

    /// <summary>A PC whose catalogued Windows services are all at their Windows default and running, as the Services tab's options.</summary>
    private static List<ServiceOption> OptionsAtDefaults(ServiceCatalogue cat)
    {
        var atDefault = cat.Entries.SelectMany(e => e.Services)
            .Select(s => (Name: s, Start: cat.WindowsDefault(s)))
            .Where(x => x.Start is not null)
            .Select(x => new ServiceView { Name = x.Name, DisplayName = x.Name, StartType = x.Start!.Value, Running = true, MicrosoftSigned = true });
        return ServicePlan.Options(atDefault, cat, insiderEnrolled: false, new HashSet<string>());
    }

    /// <summary>A row marked for the preset but with no official source is not ticked: the preset never rests on PoolWatch's word alone.</summary>
    [Fact]
    public void ARowWithoutASourceIsNeverTickedEvenWhenMarked()
    {
        var cat = ServiceCatalogue.Load("""
            [
              { "services": ["ExampleA"], "name": "Example A", "category": "Unused features", "preset": true, "source": { "kind": "none" } },
              { "services": ["ExampleB"], "name": "Example B", "category": "Unused features", "preset": true },
              { "services": ["ExampleC"], "name": "Example C", "category": "Unused features", "preset": true, "source": { "kind": "cis" } },
              { "services": ["ExampleD"], "name": "Example D", "category": "Unused features", "preset": false, "source": { "kind": "wdot" } }
            ]
            """);
        var options = ServicePlan.Options(new[] { "ExampleA", "ExampleB", "ExampleC", "ExampleD" }.Select(On), cat, false, new HashSet<string>());
        Assert.Equal(["ExampleC"], ServicePlan.Tick(Preset.AgenticCodingPc, options));
    }
}
