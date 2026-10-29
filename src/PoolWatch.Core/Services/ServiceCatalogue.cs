using System.Globalization;
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
    /// <summary>Never offered: security, Windows Update, input/audio, Hyper-V and WSL, Insider while enrolled, core/boot, drivers.</summary>
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
    /// <summary>
    /// In the "Agentic coding PC" preset: ticked only when the entry has an official source too (<see cref="ServicePlan.InAgenticPreset"/>).
    /// </summary>
    [JsonPropertyName("preset")] public bool AgenticPreset { get; init; }
    [JsonPropertyName("off")] public OffStartType Off { get; init; } = OffStartType.Disabled;
    /// <summary>An extra note (e.g. "may affect Insider flights", "turn the family off together").</summary>
    [JsonPropertyName("note")] public string? Note { get; init; }
    /// <summary>
    /// The start type Windows ships per service ("automatic" / "manual"), for "Restore to Windows default". Only where it
    /// is known; a vendor service has no Windows default and is left out.
    /// </summary>
    [JsonPropertyName("windowsDefault")] public Dictionary<string, string>? WindowsDefaults { get; init; }

    /// <summary>
    /// Where the advice to turn it off comes from (its "source" object); <see cref="ServiceSource.None"/> when the entry
    /// has none, or "source" is null.
    /// </summary>
    [JsonPropertyName("source")] public ServiceSource Source { get => _source; init => _source = value ?? ServiceSource.None; }
    private readonly ServiceSource _source = ServiceSource.None;
}

/// <summary>Which official source turns a catalogue entry's services off: the "kind" of its "source" object.</summary>
public enum ServiceSourceKind
{
    /// <summary>"none": no official source. Also an entry with no "source" at all.</summary>
    None,
    /// <summary>"wdot": Microsoft's Windows Desktop Optimization Tool turns it off. The tool is written for virtual desktops.</summary>
    Wdot,
    /// <summary>"cis": the CIS Microsoft Windows 11 Benchmark turns it off. It is a security checklist for company PCs.</summary>
    Cis,
    /// <summary>"both": both of them turn it off.</summary>
    Both,
}

/// <summary>
/// Where the advice to turn a catalogue entry off comes from, as services-catalogue.json gives it:
/// <c>"source": { "kind": "wdot" | "cis" | "both" | "none", "wdotRef": "...", "cisRef": "...", "checked": "YYYY-MM-DD" }</c>.
/// "kind" is required inside the object and must be one of those four words, written in lower case; the other three are
/// optional. A missing kind, any other kind, or a value of the wrong type fails the catalogue's load
/// (<see cref="ServiceSourceConverter"/>), so a typo in the data is caught by the tests rather than read as some other
/// source. Other properties in the object are skipped.
/// </summary>
/// <param name="Kind">Which source turns it off.</param>
/// <param name="WdotRef">Where in Microsoft's tool it is turned off, for whoever checks the row again.</param>
/// <param name="CisRef">Which CIS Benchmark recommendation turns it off.</param>
/// <param name="Checked">When the row was last checked against its sources.</param>
[JsonConverter(typeof(ServiceSourceConverter))]
public sealed record ServiceSource(ServiceSourceKind Kind, string? WdotRef = null, string? CisRef = null, DateOnly? Checked = null)
{
    /// <summary>No official source: what an entry without a "source" has.</summary>
    public static readonly ServiceSource None = new(ServiceSourceKind.None);
}

/// <summary>
/// Reads and writes a "source" object strictly. The load fails (a <see cref="JsonException"/>) when the source is not an
/// object, its "kind" is missing, null or not exactly "wdot", "cis", "both" or "none", a reference is not text, or
/// "checked" is not a date written YYYY-MM-DD; the error says what was read instead. Property names are matched in any
/// case, as in the rest of the catalogue, and other properties are skipped. An empty reference is no reference. Writing
/// a kind that is not one of the four fails the same way, so nothing is written that would not read back.
/// </summary>
internal sealed class ServiceSourceConverter : JsonConverter<ServiceSource>
{
    private const string DateFormat = "yyyy-MM-dd";

    private static readonly (string Word, ServiceSourceKind Kind)[] Kinds =
        [("none", ServiceSourceKind.None), ("wdot", ServiceSourceKind.Wdot), ("cis", ServiceSourceKind.Cis), ("both", ServiceSourceKind.Both)];

    public override ServiceSource Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException($"A service's \"source\" must be an object, not {Shown(ref reader)}.");
        ServiceSourceKind? kind = null;
        string? wdotRef = null, cisRef = null;
        DateOnly? checkedOn = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            string name = reader.GetString() ?? "";
            reader.Read();
            if (Is(name, "kind")) kind = ReadKind(ref reader);
            else if (Is(name, "wdotRef")) wdotRef = ReadText(ref reader, "wdotRef");
            else if (Is(name, "cisRef")) cisRef = ReadText(ref reader, "cisRef");
            else if (Is(name, "checked")) checkedOn = ReadDate(ref reader);
            else reader.Skip();
        }
        return kind is { } k ? new ServiceSource(k, wdotRef, cisRef, checkedOn) : throw new JsonException("A service's \"source\" has no \"kind\".");
    }

    public override void Write(Utf8JsonWriter writer, ServiceSource value, JsonSerializerOptions options)
    {
        string word = WordOf(value.Kind);
        writer.WriteStartObject();
        writer.WriteString("kind", word);
        if (value.WdotRef is { } wdot) writer.WriteString("wdotRef", wdot);
        if (value.CisRef is { } cis) writer.WriteString("cisRef", cis);
        if (value.Checked is { } date) writer.WriteString("checked", date.ToString(DateFormat, CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }

    private const string KindWords = "\"wdot\", \"cis\", \"both\" or \"none\"";

    private static bool Is(string name, string property) => name.Equals(property, StringComparison.OrdinalIgnoreCase);

    private static string WordOf(ServiceSourceKind kind)
    {
        foreach (var (w, k) in Kinds)
            if (k == kind) return w;
        throw new JsonException($"A service's source kind must be {KindWords}, not the value {(int)kind}.");
    }

    private static ServiceSourceKind ReadKind(ref Utf8JsonReader reader)
    {
        string? word = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        foreach (var (w, kind) in Kinds)
            if (string.Equals(word, w, StringComparison.Ordinal)) return kind;
        throw new JsonException($"A service's source \"kind\" must be {KindWords}, not {Shown(ref reader)}.");
    }

    private static string? ReadText(ref Utf8JsonReader reader, string property) => reader.TokenType switch
    {
        JsonTokenType.Null => null,
        JsonTokenType.String => reader.GetString() is { } s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null,
        _ => throw new JsonException($"A service's source \"{property}\" must be text, not {Shown(ref reader)}."),
    };

    private static DateOnly? ReadDate(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType == JsonTokenType.String
            && DateOnly.TryParseExact(reader.GetString(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;
        throw new JsonException($"A service's source \"checked\" must be a date written YYYY-MM-DD, not {Shown(ref reader)}.");
    }

    /// <summary>
    /// What was read where a value was refused, for the load error: text in quotes as written, else the kind of value.
    /// System.Text.Json does not add the JSON path to an error with its own message, so this is what lets a typo be found.
    /// </summary>
    private static string Shown(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.String => $"\"{reader.GetString()}\"",
        JsonTokenType.Number => "a number",
        JsonTokenType.True => "true",
        JsonTokenType.False => "false",
        JsonTokenType.Null => "null",
        JsonTokenType.StartObject => "an object",
        JsonTokenType.StartArray => "a list",
        _ => reader.TokenType.ToString(),
    };
}

/// <summary>
/// What the Services tab says about a catalogue entry's source. Each line says what the source is written for, and never
/// that a service is safe to turn off: Microsoft's tool is written for virtual desktops, which are copies of Windows run
/// on a server, and the CIS Benchmark is a security checklist for company PCs, so something they turn off may still
/// matter on your own PC.
/// </summary>
public static class ServiceSourceText
{
    public const string WdotLabel = "Microsoft's virtual-desktop optimisation tool turns this off";
    public const string CisLabel = "The CIS security checklist for company PCs turns this off";
    public const string BothLabel = "Microsoft's virtual-desktop tool and the CIS security checklist turn this off";
    public const string NoneLabel = "No official source";

    /// <summary>The one caveat line for a group of services that carries these sources (the Windows services group).</summary>
    public const string GroupCaveat = "These sources were written for virtual desktops and company PCs. Check what you lose first.";

    /// <summary>The line for a source; <see cref="NoneLabel"/> for none, and for a value that is not one of the four.</summary>
    public static string Label(ServiceSourceKind kind) => kind switch
    {
        ServiceSourceKind.Wdot => WdotLabel,
        ServiceSourceKind.Cis => CisLabel,
        ServiceSourceKind.Both => BothLabel,
        _ => NoneLabel,
    };
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
    /// <summary>The shipped catalogue, read once (settings use it to check the services a profile lists).</summary>
    public static ServiceCatalogue Shared => _shared.Value;
    private static readonly Lazy<ServiceCatalogue> _shared = new(() => Load());

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
