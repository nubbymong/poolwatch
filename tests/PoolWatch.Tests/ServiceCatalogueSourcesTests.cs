using System.Text.Json;
using System.Text.RegularExpressions;
using PoolWatch.Core.Services;

namespace PoolWatch.Tests;

/// <summary>
/// The shipped catalogue's sources, checked against what the sources hold (copied here as facts, re-checked each release):
/// a row says "wdot" only for services Microsoft's tool lists, "cis" only for services the CIS benchmark's System Services
/// section lists, "both" only for services both list, and "none" only where neither does. A Windows row has a source, or is
/// one PoolWatch listed before the sources were added; a row is never added without one.
/// </summary>
public class ServiceCatalogueSourcesTests
{
    private static readonly ServiceCatalogue Cat = DefaultCatalogue.Load();
    private static readonly DateOnly Checked = new(2026, 10, 30);

    /// <summary>
    /// Every service in Microsoft's Windows Desktop Optimization Tool's Configurations/Templates/Services.json (the same in
    /// Configurations/2009) at commit ff2574c, 2026-05-20: 23 services, each shipped set to "Skip".
    /// </summary>
    private static readonly HashSet<string> Wdot = new(StringComparer.OrdinalIgnoreCase)
    {
        "InstallService", "autotimesvc", "BcastDVRUserService", "defragsvc", "DiagSvc", "DiagTrack", "DPS", "DusmSvc", "icssvc",
        "lfsvc", "MapsBroker", "MessagingService", "RmSvc", "SEMgrSvc", "SmsRouter", "SysMain", "VSS", "WdiSystemHost", "WerSvc",
        "XblAuthManager", "XblGameSave", "XboxGipSvc", "XboxNetApiSvc",
    };

    /// <summary>
    /// The CIS Microsoft Windows 11 Enterprise Benchmark v5.1.0's System Services items for the services the catalogue
    /// lists (item number, level), as Tenable's public audit listing for v5.1.0 shows them.
    /// </summary>
    private static readonly Dictionary<string, string> Cis = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MapsBroker"] = "5.4 (L2)", ["lfsvc"] = "5.6 (L2)", ["WerSvc"] = "5.33 (L2)", ["WMPNetworkSvc"] = "5.35 (L1)",
        ["icssvc"] = "5.36 (L1)", ["XboxGipSvc"] = "5.41 (L1)", ["XblAuthManager"] = "5.42 (L1)", ["XblGameSave"] = "5.43 (L1)",
        ["XboxNetApiSvc"] = "5.44 (L1)",
    };

    /// <summary>The Windows rows PoolWatch listed before the sources were added that neither source backs: kept, marked none.</summary>
    private static readonly HashSet<string> WindowsRowsKeptWithoutASource = new(StringComparer.OrdinalIgnoreCase)
        { "InventorySvc", "dmwappushservice", "PcaSvc", "WSearch", "RetailDemo", "Fax", "PhoneSvc", "WalletService" };

    /// <summary>The first service of each of the 30 rows the catalogue had before its sources were added.</summary>
    private static readonly string[] RowsBefore =
    [
        "ESRV_SVC_QUEENCREEK", "IntelTelemetryAgent", "InventorySvc", "DiagTrack", "dmwappushservice", "WerSvc", "PcaSvc", "WSearch",
        "SysMain", "MapsBroker", "lfsvc", "RetailDemo", "Fax", "icssvc", "PhoneSvc", "WalletService", "SEMgrSvc", "WMPNetworkSvc",
        "XblAuthManager", "XboxGipSvc", "IpOverUsbSvc", "CMigrationService", "ZoomCptServiceForVDIPluginMgmt", "PresentMonSharedService",
        "HPPSAService", "DSAService", "AdobeARMservice", "edgeupdate", "GoogleUpdaterService", "ipfsvc",
    ];

    private const string Vendors = "Vendor helpers and updaters";

    /// <summary>A Windows row: Windows ships its services (it has their default start types), and it is not an app's updater or helper.</summary>
    private static bool IsWindowsRow(CatalogueEntry e) => e.WindowsDefaults is { Count: > 0 } && e.Category != Vendors;

    public static TheoryData<string> Rows()
    {
        var data = new TheoryData<string>();
        foreach (var e in Cat.Entries) data.Add(e.Services[0]);
        return data;
    }

    [Fact]
    public void NoRowPoolWatchListedBeforeIsDropped()
    {
        foreach (var s in RowsBefore) Assert.NotNull(Cat.Find(s));
        Assert.Equal(RowsBefore.Length + 3, Cat.Entries.Count); // the 30 kept, and 3 rows the sources added
    }

    /// <summary>Each service's source kind is exactly what the two sources' lists say.</summary>
    [Theory]
    [MemberData(nameof(Rows))]
    public void ARowsSourceIsWhatTheSourcesSay(string first)
    {
        var e = Cat.Find(first)!;
        foreach (var s in e.Services)
        {
            bool wdot = Wdot.Contains(s), cis = Cis.ContainsKey(s);
            var expected = wdot && cis ? ServiceSourceKind.Both : wdot ? ServiceSourceKind.Wdot : cis ? ServiceSourceKind.Cis : ServiceSourceKind.None;
            Assert.True(expected == e.Source.Kind, $"{e.Name}: {s} is {expected} in the sources, the row says {e.Source.Kind}");
        }
    }

    /// <summary>A reference names every service of its row: the tool's file and the service, the benchmark item and the service.</summary>
    [Theory]
    [MemberData(nameof(Rows))]
    public void ARowsReferencesNameEachOfItsServices(string first)
    {
        var e = Cat.Find(first)!;
        var src = e.Source;
        if (src.Kind is ServiceSourceKind.Wdot or ServiceSourceKind.Both)
        {
            Assert.StartsWith("Configurations/Templates/Services.json: ", src.WdotRef);
            foreach (var s in e.Services) Assert.Matches($@"[:,] {Regex.Escape(s)}(,|$)", src.WdotRef!);
        }
        else Assert.Null(src.WdotRef);
        if (src.Kind is ServiceSourceKind.Cis or ServiceSourceKind.Both)
        {
            Assert.StartsWith("Windows 11 Enterprise v5.1.0: ", src.CisRef);
            foreach (var s in e.Services) Assert.Matches($@"{Regex.Escape(Cis[s])} [^;]*\({Regex.Escape(s)}\)", src.CisRef!);
        }
        else Assert.Null(src.CisRef);
    }

    /// <summary>Every Windows row has a source, or is one listed before the sources were added; each says when it was checked.</summary>
    [Theory]
    [MemberData(nameof(Rows))]
    public void AWindowsRowHasASourceOrWasListedBefore(string first)
    {
        var e = Cat.Find(first)!;
        if (!IsWindowsRow(e))
        {
            Assert.Equal(ServiceSourceKind.None, e.Source.Kind); // an app's service: the AI-agent route
            return;
        }
        Assert.Equal(Checked, e.Source.Checked);
        if (e.Source.Kind == ServiceSourceKind.None) Assert.Contains(first, WindowsRowsKeptWithoutASource);
    }

    [Fact]
    public void EveryRowAddedSinceHasASource()
    {
        var before = RowsBefore.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = Cat.Entries.Where(e => !e.Services.Any(before.Contains)).ToList();
        Assert.Equal(["DPS", "autotimesvc", "SmsRouter"], added.Select(e => e.Services[0]));
        Assert.All(added, e => Assert.NotEqual(ServiceSourceKind.None, e.Source.Kind));
        Assert.All(added, e => Assert.True(IsWindowsRow(e)));
    }

    /// <summary>Every row says its source in the file, none included, so a row without one is a choice that can be seen.</summary>
    [Fact]
    public void EveryRowWritesItsSource()
    {
        using var s = typeof(ServiceCatalogue).Assembly.GetManifestResourceStream("services-catalogue.json")!;
        using var doc = JsonDocument.Parse(s, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        foreach (var row in doc.RootElement.EnumerateArray())
            Assert.True(row.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.Object, row.GetProperty("name").GetString());
    }

    /// <summary>
    /// What a row says goes into the AI-agent prompt as it is, which never holds a folder path: no row's words name one. Nor
    /// does any say a service is safe to turn off: no source says that of a person's own PC.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rows))]
    public void ARowsWordsHoldNoPathAndNoSafeClaim(string first)
    {
        var e = Cat.Find(first)!;
        foreach (var text in new[] { e.Name, e.Lose, e.Note ?? "" })
        {
            Assert.DoesNotContain("\\", text);
            Assert.DoesNotMatch(@"[A-Za-z]:/", text);
            Assert.DoesNotContain("safe", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The one row a source backs that PoolWatch turns off to Manual, not to Disabled as both its sources set it, and why.
    /// PoolWatch's own crash findings read Windows Error Reporting's records (Application Error 1000, Windows Error
    /// Reporting 1001), which may stop while the service is Disabled; at Manual, its Windows default, Windows starts it when
    /// a crash asks for it. The row's note says so, where the Services tab and the AI agent read it.
    /// </summary>
    private static readonly Dictionary<string, string> TurnedOffToManualForPoolWatch = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WerSvc"] = "its own crash findings read Windows Error Reporting's records",
    };

    /// <summary>
    /// A row a source backs is turned off to Disabled, as its sources set it: the tool's Functions/Disable-WDOTServices.ps1
    /// sets every service it turns off to Disabled, and each System Services item the catalogue cites from the benchmark
    /// says Disabled. Never Manual (at a Manual default that would read as off already and change nothing), but for the one
    /// row named above, whose note says why.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rows))]
    public void ASourcedRowIsTurnedOffToDisabled(string first)
    {
        var e = Cat.Find(first)!;
        if (e.Source.Kind == ServiceSourceKind.None) return;
        if (TurnedOffToManualForPoolWatch.TryGetValue(first, out var why))
        {
            Assert.True(e.Off == OffStartType.Manual, $"{e.Name}: PoolWatch turns it off to Manual ({why}), the row turns it off to {e.Off}");
            Assert.Contains("to Manual, not to Disabled as its sources do", e.Note);
            Assert.Contains(why, e.Note);
        }
        else Assert.True(e.Off == OffStartType.Disabled, $"{e.Name}: its sources set it to Disabled, the row turns it off to {e.Off}");
    }

    /// <summary>
    /// Windows Error Reporting's row is as it was before its sources were checked (off to Manual, what you lose, risk), with
    /// a note on why it is not Disabled; set to Disabled, this fails. The reason still holds: the crash findings read the
    /// Application log's Windows Error Reporting records.
    /// </summary>
    [Fact]
    public void WindowsErrorReportingIsTurnedOffToManualForPoolWatchsCrashFindings()
    {
        var wer = Cat.Find("WerSvc")!;
        Assert.Equal((OffStartType.Manual, ServiceRisk.Safe, "crash reports to Microsoft and app vendors", false),
            (wer.Off, wer.Risk, wer.Lose, wer.AgenticPreset));
        Assert.Equal("PoolWatch turns this off to Manual, not to Disabled as its sources do: its own crash findings read Windows Error " +
                     "Reporting's records, which may stop while it is Disabled.", wer.Note);
        Assert.Equal(ServiceStartType.Manual, Cat.WindowsDefault("WerSvc"));
        Assert.Equal(ServiceSourceKind.Both, wer.Source.Kind);
        Assert.Contains(PoolWatch.Core.Versions.CrashMarkers.Logs, l => l.channel == "Application"
            && l.filter.Contains($"Provider[@Name='{PoolWatch.Core.Versions.CrashMarkers.WerProvider}'] and EventID=1001", StringComparison.Ordinal));
    }
}
