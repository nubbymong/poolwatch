using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoolWatch.Core.Services;

/// <summary>Which of the three groups a service falls in (addendum 4 B).</summary>
public enum ServiceGroup
{
    /// <summary>On the catalogue: a known optional service the owner may turn off.</summary>
    Optional,
    /// <summary>Not on the catalogue and not signed by Microsoft: "unknown: you decide" (a second confirmation).</summary>
    Unknown,
    /// <summary>Never offered: security, Windows Update, input/audio, Insider while enrolled, core/boot, drivers.</summary>
    Protected,
}

/// <summary>How risky turning a catalogue entry off is.</summary>
public enum ServiceRisk { Safe, CheckFirst }

/// <summary>What "Turn off" sets the start type to.</summary>
public enum OffStartType { Disabled, Manual }

/// <summary>One catalogue entry: a product's service family, what it is, and what is lost when it is off.</summary>
public sealed record CatalogueEntry
{
    /// <summary>The service names of this product (turned off together; a shared SurSvc can otherwise revive the rest).</summary>
    [JsonPropertyName("services")] public List<string> Services { get; init; } = new();
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("category")] public string Category { get; init; } = "";
    /// <summary>What the owner loses with it off.</summary>
    [JsonPropertyName("lose")] public string Lose { get; init; } = "";
    [JsonPropertyName("risk")] public ServiceRisk Risk { get; init; } = ServiceRisk.CheckFirst;
    /// <summary>In the "Dev workstation" preset.</summary>
    [JsonPropertyName("preset")] public bool DevPreset { get; init; }
    [JsonPropertyName("off")] public OffStartType Off { get; init; } = OffStartType.Disabled;
    /// <summary>An extra note (e.g. "may affect Insider flights", "turn the family off together").</summary>
    [JsonPropertyName("note")] public string? Note { get; init; }
    /// <summary>
    /// The start type Windows ships per service ("automatic" / "manual"), for "Restore to Windows default". Only where it
    /// is known; a vendor service has no Windows default and is left out.
    /// </summary>
    [JsonPropertyName("windowsDefault")] public Dictionary<string, string>? WindowsDefaults { get; init; }
}

/// <summary>
/// The optional-services catalogue (addendum 4 B.1), loaded from a data file so it grows without code changes, and the
/// protected gate (B.3) which no catalogue, request or preset can get past. The gate is hard-coded here, never from data.
/// </summary>
public sealed class ServiceCatalogue
{
    private readonly List<CatalogueEntry> _entries;
    private readonly Dictionary<string, CatalogueEntry> _byService;

    public ServiceCatalogue(IEnumerable<CatalogueEntry> entries)
    {
        _entries = entries.ToList();
        _byService = new(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _entries)
            foreach (var s in e.Services)
                _byService[s] = e;
    }

    public IReadOnlyList<CatalogueEntry> Entries => _entries;

    public static ServiceCatalogue Load(string json)
    {
        var entries = JsonSerializer.Deserialize<List<CatalogueEntry>>(json, Options) ?? new();
        return new ServiceCatalogue(entries.Where(e => e.Services.Count > 0));
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public CatalogueEntry? Find(string service) => _byService.GetValueOrDefault(service);

    /// <summary>
    /// The Windows default start type of a catalogued service: Automatic or Manual only (a default of Disabled, Boot or
    /// System, or anything unreadable, is null: "Restore to Windows default" is then not offered).
    /// </summary>
    public ServiceStartType? WindowsDefault(string service)
    {
        if (Find(service)?.WindowsDefaults is not { } d) return null;
        foreach (var (name, value) in d)
            if (name.Equals(service, StringComparison.OrdinalIgnoreCase))
                return value.Trim().ToLowerInvariant() switch
                {
                    "automatic" or "auto" => ServiceStartType.Automatic,
                    "manual" => ServiceStartType.Manual,
                    _ => null,
                };
        return null;
    }

    /// <summary>
    /// Whether a service binary counts as Microsoft's (the Protected vs Unknown split for services the catalogue does not
    /// list). C6 (review): <paramref name="signer"/> is the verified signer of the embedded signature OR of the system
    /// catalog that lists the file, so catalog-signed Microsoft binaries outside %WINDIR% (which have no embedded
    /// signature) are Microsoft too; they used to land in the turn-off-able "Unknown" group. The certificate's organization
    /// counts as well (Microsoft signs .NET binaries as CN=.NET, O=Microsoft Corporation). A file with no signature at all
    /// counts as Microsoft only under %WINDIR%. Wrongly saying "Microsoft" only makes a service protected (the safe direction).
    /// </summary>
    public static bool MicrosoftSigner(string? signer, bool underWindowsDir, string? organization = null)
    {
        if (organization is { Length: > 0 } o && o.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) return true;
        return signer is { Length: > 0 } s ? s.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) : underWindowsDir;
    }

    /// <summary>The group of a service by its name, with whether Microsoft signs it (for the Unknown vs Protected split).</summary>
    public ServiceGroup GroupOf(string service, bool microsoftSigned, bool insiderEnrolled)
    {
        if (IsProtected(service, insiderEnrolled)) return ServiceGroup.Protected;
        if (_byService.ContainsKey(service)) return ServiceGroup.Optional;
        // Not catalogued: a Microsoft-signed service we do not know is left alone (Protected); a third party is Unknown.
        return microsoftSigned ? ServiceGroup.Protected : ServiceGroup.Unknown;
    }

    /// <summary>The hard gate: these are never offered, whatever the catalogue or a request says.</summary>
    public static bool IsProtected(string service, bool insiderEnrolled)
    {
        if (ProtectedNames.Contains(service)) return true;
        if (insiderEnrolled && service.Equals("wisvc", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var prefix in ProtectedPrefixes)
            if (service.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The reason a protected service is greyed out.</summary>
    public static string? ProtectedReason(string service, bool insiderEnrolled)
    {
        if (!IsProtected(service, insiderEnrolled)) return null;
        if (insiderEnrolled && service.Equals("wisvc", StringComparison.OrdinalIgnoreCase)) return "Windows Insider service (the PC is enrolled)";
        if (Security.Contains(service)) return "Windows security";
        if (Update.Contains(service) || service.StartsWith("DoSvc", StringComparison.OrdinalIgnoreCase)) return "Windows Update";
        if (Input.Contains(service)) return "text input or audio";
        return "core Windows service";
    }

    private static readonly HashSet<string> Security = new(StringComparer.OrdinalIgnoreCase)
        { "WinDefend", "WdNisSvc", "Sense", "SecurityHealthService", "mpssvc", "wscsvc", "WdFilter", "WdNisDrv" };
    private static readonly HashSet<string> Update = new(StringComparer.OrdinalIgnoreCase)
        { "wuauserv", "UsoSvc", "BITS", "DoSvc", "TrustedInstaller" };
    private static readonly HashSet<string> Input = new(StringComparer.OrdinalIgnoreCase)
        { "TextInputManagementService", "TabletInputService", "Audiosrv", "AudioEndpointBuilder" };
    private static readonly HashSet<string> Core = new(StringComparer.OrdinalIgnoreCase)
    {
        "RpcSs", "RpcEptMapper", "DcomLaunch", "LSM", "Power", "PlugPlay", "BrokerInfrastructure", "SystemEventsBroker",
        "Dhcp", "Dnscache", "NlaSvc", "nsi", "Winmgmt", "ProfSvc", "Themes", "gpsvc", "SamSs", "EventLog", "Schedule",
        "CoreMessagingRegistrar", "UserManager", "CryptSvc", "KeyIso", "EventSystem", "Netlogon", "SENS",
        // Virtualization / Hyper-V / WSL and base filtering: turning these off can break VMs, WSL or the firewall (review S2).
        "vmms", "vmcompute", "HvHost", "WSLService", "BFE", "MDCoreSvc",
    };
    private static readonly HashSet<string> ProtectedNames =
        new(Security.Concat(Update).Concat(Input).Concat(Core), StringComparer.OrdinalIgnoreCase);
    /// <summary>Protected name prefixes (vmic* are the Hyper-V integration services); the live gate also checks the service is Win32.</summary>
    private static readonly string[] ProtectedPrefixes = { "vmic" };
}

/// <summary>Loads the shipped catalogue (embedded services-catalogue.json) or an external data file.</summary>
public static class DefaultCatalogue
{
    public static ServiceCatalogue Load(string? externalFile = null)
    {
        if (externalFile is not null && System.IO.File.Exists(externalFile))
            try { return ServiceCatalogue.Load(System.IO.File.ReadAllText(externalFile)); } catch { }
        using var s = typeof(ServiceCatalogue).Assembly.GetManifestResourceStream("services-catalogue.json");
        if (s is null) return new ServiceCatalogue(System.Array.Empty<CatalogueEntry>());
        using var r = new System.IO.StreamReader(s);
        return ServiceCatalogue.Load(r.ReadToEnd());
    }
}
