using System.Globalization;

namespace PoolWatch.Core.Services;

/// <summary>A service as PoolWatch sees it on this PC, with the facts the page and the gate need.</summary>
public sealed record ServiceView
{
    public required string Name { get; init; }
    public string DisplayName { get; init; } = "";
    public ServiceStartType StartType { get; init; } = ServiceStartType.Unknown;
    public bool Running { get; init; }
    public bool MicrosoftSigned { get; init; }
    public bool IsWin32 { get; init; } = true;
    public string? BinaryPath { get; init; }
    public string? Publisher { get; init; }
    public string? Description { get; init; }
    /// <summary>Live cost in the latest window, when the host process is known.</summary>
    public double Cores { get; init; }
    public long PageFaultsPerSec { get; init; }
    /// <summary>The PID of the process the service runs in (0 when it is not running).</summary>
    public int ProcessId { get; init; }
    /// <summary>Other running services in the same host process (a shared svchost): the cost is the host's.</summary>
    public int SharedWith { get; init; }
}

/// <summary>One box the owner may tick on the page: the service, its group, the catalogue entry if any, and its state.</summary>
public sealed record ServiceOption
{
    public required ServiceView Service { get; init; }
    public required ServiceGroup Group { get; init; }
    public CatalogueEntry? Catalogue { get; init; }
    public string? ProtectedReason { get; init; }
    /// <summary>A service that is off and that PoolWatch did not turn off, and why it reads that way.</summary>
    public string? OffOutsidePoolWatch { get; init; }
    /// <summary>PoolWatch's own saved change of this service (service-changes.json), if any.</summary>
    public ServiceChange? PoolWatchChange { get; init; }
    /// <summary>The start type Windows ships for this service, when the catalogue knows it (Automatic or Manual).</summary>
    public ServiceStartType? WindowsDefault { get; init; }
    public bool CanTurnOff => Group is ServiceGroup.Optional or ServiceGroup.Unknown;
    /// <summary>"Restore to Windows default" applies: off outside PoolWatch, changeable, and the default is known.</summary>
    public bool CanRestoreDefault => CanTurnOff && OffOutsidePoolWatch == ServicePlan.OffOutside && WindowsDefault is not null;
}

/// <summary>The presets: a preset only TICKS boxes; nothing changes until Apply (addendum 4 B controls).</summary>
public enum Preset { TelemetryOnly, DevWorkstation, Custom }

/// <summary>
/// Turns the installed services into options, applies a preset's ticks, and builds the Apply operation list. Every
/// operation is checked against the protected gate again here, so a crafted request cannot slip a protected service in.
/// Pure: the live service control is the caller's.
/// </summary>
public static class ServicePlan
{
    public const string OffOutside = "off (changed outside PoolWatch)";
    public const string OffWindowsDefault = "off (Windows default)";
    /// <summary>The <see cref="ServiceChange.Via"/> of a "Restore to Windows default" change.</summary>
    public const string ViaWindowsDefault = "Windows default";

    /// <summary>
    /// The services as options, grouped and annotated. <paramref name="knownDefaultsOff"/>: services Windows ships Disabled.
    /// <paramref name="changes"/>: PoolWatch's own saved changes, so a service PoolWatch turned off never reads "changed
    /// outside PoolWatch" (Part 3 review).
    /// </summary>
    public static List<ServiceOption> Options(IEnumerable<ServiceView> services, ServiceCatalogue catalogue, bool insiderEnrolled, IReadOnlySet<string> knownDefaultsOff,
        IReadOnlyCollection<ServiceChange>? changes = null)
    {
        var byService = new Dictionary<string, ServiceChange>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in changes ?? []) byService[c.Service] = c;
        var list = new List<ServiceOption>();
        foreach (var s in services)
        {
            var group = catalogue.GroupOf(s.Name, s.MicrosoftSigned, insiderEnrolled);
            var entry = catalogue.Find(s.Name);
            var mine = byService.GetValueOrDefault(s.Name);
            string? offOutside = s.StartType == ServiceStartType.Disabled && group != ServiceGroup.Protected && mine is null
                ? knownDefaultsOff.Contains(s.Name) ? OffWindowsDefault : OffOutside
                : null;
            list.Add(new ServiceOption
            {
                Service = s, Group = group, Catalogue = entry,
                ProtectedReason = ServiceCatalogue.ProtectedReason(s.Name, insiderEnrolled),
                OffOutsidePoolWatch = offOutside,
                PoolWatchChange = mine,
                WindowsDefault = catalogue.WindowsDefault(s.Name),
            });
        }
        return list;
    }

    /// <summary>
    /// The State column: PoolWatch's own change ("off (turned off by PoolWatch 2026-10-26 11:40)"), off outside PoolWatch
    /// (an owner choice or a Windows default), else running / the start type.
    /// </summary>
    public static string StateText(ServiceOption o)
    {
        var s = o.Service;
        if (o.PoolWatchChange is { } c)
        {
            string when = c.ChangedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            if (c.Via == ViaWindowsDefault)
                return s.StartType == c.NewStart ? $"{Live(s)} (put back to its Windows default by PoolWatch {when})"
                    : $"{Live(s)} (changed outside PoolWatch after PoolWatch put back its Windows default {when})";
            if (s.StartType == c.NewStart && !s.Running)
                return $"{(c.NewStart == ServiceStartType.Disabled ? "off" : "set to Manual, stopped")} (turned off by PoolWatch {when})";
            return $"{Live(s)} (turned off by PoolWatch {when}, then switched back on outside it)";
        }
        return o.OffOutsidePoolWatch ?? Live(s);
    }

    private static string Live(ServiceView s) => s.Running ? "running" : s.StartType switch
    {
        ServiceStartType.Disabled => "off",
        ServiceStartType.Unknown => "stopped",
        var t => $"{t}, stopped",
    };

    /// <summary>The Cost column: "1.7 cores, 887k page faults/s" for the service's process, "not running"; a shared host says so.</summary>
    public static string CostText(ServiceView s)
    {
        if (!s.Running) return "not running";
        if (s.ProcessId == 0) return "";
        string cost = Cost(s.Cores, s.PageFaultsPerSec);
        return s.SharedWith > 0 ? $"{cost} (host shared with {s.SharedWith} other service{(s.SharedWith == 1 ? "" : "s")})" : cost;
    }

    /// <summary>"1.7 cores, 887k page faults/s", "0.1 core", or "idle".</summary>
    public static string Cost(double cores, long pageFaultsPerSec)
    {
        var parts = new List<string>();
        if (cores >= 0.05) parts.Add($"{cores.ToString("0.0", CultureInfo.InvariantCulture)} core{(cores < 1.05 ? "" : "s")}");
        if (pageFaultsPerSec >= 1000) parts.Add($"{Faults(pageFaultsPerSec)} page faults/s");
        return parts.Count == 0 ? "idle" : string.Join(", ", parts);
    }

    /// <summary>"887k", "1.2M", "640".</summary>
    public static string Faults(long n) => n >= 1_000_000 ? (n / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M"
        : n >= 1_000 ? (n / 1000.0).ToString("0", CultureInfo.InvariantCulture) + "k" : n.ToString(CultureInfo.InvariantCulture);

    /// <summary>The services a preset ticks (only catalogue entries; it never ticks Unknown or Protected).</summary>
    public static HashSet<string> Tick(Preset preset, IReadOnlyList<ServiceOption> options)
    {
        var ticked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (preset == Preset.Custom) return ticked;
        foreach (var o in options)
        {
            if (o.Group != ServiceGroup.Optional || o.Catalogue is not { } c || !o.Service.Running && o.Service.StartType == ServiceStartType.Disabled) continue;
            bool pick = preset == Preset.DevWorkstation ? c.DevPreset
                : c.Category.StartsWith("Telemetry", StringComparison.OrdinalIgnoreCase); // "Telemetry only"
            if (pick) ticked.Add(o.Service.Name);
        }
        return ticked;
    }

    /// <summary>One service to change: what it will be set to, and the record to save first.</summary>
    public sealed record Operation(ServiceOption Option, ServiceStartType NewStart, ServiceChange Record);

    /// <summary>
    /// Builds the Apply list from the ticked services. Throws when a ticked service is not changeable (protected, a
    /// driver, or unknown to this plan): the gate, enforced here and again in the elevated helper.
    /// </summary>
    public static List<Operation> BuildApply(IEnumerable<string> ticked, IReadOnlyList<ServiceOption> options, bool insiderEnrolled, string via, DateTime nowUtc)
    {
        var byName = options.ToDictionary(o => o.Service.Name, StringComparer.OrdinalIgnoreCase);
        var ops = new List<Operation>();
        foreach (var name in ticked.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!byName.TryGetValue(name, out var o)) throw new InvalidOperationException($"'{name}' is not an installed service on this PC");
            Gate(o, insiderEnrolled);
            var newStart = o.Catalogue?.Off == OffStartType.Manual ? ServiceStartType.Manual : ServiceStartType.Disabled;
            ops.Add(new Operation(o, newStart, new ServiceChange
            {
                Service = o.Service.Name, DisplayName = o.Service.DisplayName, OriginalStart = o.Service.StartType,
                OriginalRunning = o.Service.Running, NewStart = newStart, ChangedUtc = nowUtc, Via = via,
            }));
        }
        return ops;
    }

    /// <summary>The restore operation for a saved change: back to exactly the original start type.</summary>
    public static ServiceStartType RestoreTarget(ServiceChange change) => change.OriginalStart;

    /// <summary>Restore starts the service again when it was running before PoolWatch changed it (and is not put back to Disabled).</summary>
    public static bool StartAfterRestore(ServiceChange change) => change.OriginalRunning && change.OriginalStart != ServiceStartType.Disabled;

    /// <summary>
    /// "Restore to Windows default" for a service that is off outside PoolWatch: through the SAME gate as Apply (catalogue or
    /// unknown third party, never protected, Win32 only), to the catalogue's Windows default, which must be Automatic or
    /// Manual. The record saved first (the original is the start type as it is now) lets Restore put the owner's setting back.
    /// </summary>
    public static Operation BuildDefault(string service, IReadOnlyList<ServiceOption> options, bool insiderEnrolled, DateTime nowUtc)
    {
        var o = options.FirstOrDefault(x => x.Service.Name.Equals(service, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"'{service}' is not an installed service on this PC");
        Gate(o, insiderEnrolled);
        if (o.PoolWatchChange is not null) throw new InvalidOperationException($"'{o.Service.Name}': PoolWatch changed it; use Restore");
        if (o.OffOutsidePoolWatch != OffOutside) throw new InvalidOperationException($"'{o.Service.Name}' is not off outside PoolWatch");
        if (o.WindowsDefault is not ({ } d and (ServiceStartType.Automatic or ServiceStartType.Manual)))
            throw new InvalidOperationException($"'{o.Service.Name}': PoolWatch does not know its Windows default start type");
        if (!IsValidStartType(o.Service.StartType))
            throw new InvalidOperationException($"'{o.Service.Name}': its current start type cannot be read, so it could not be put back later");
        return new Operation(o, d, new ServiceChange
        {
            Service = o.Service.Name, DisplayName = o.Service.DisplayName, OriginalStart = o.Service.StartType, OriginalRunning = o.Service.Running,
            NewStart = d, ChangedUtc = nowUtc, Via = ViaWindowsDefault,
        });
    }

    /// <summary>A start type PoolWatch may write: a defined value in 0..4, never Unknown or garbage.</summary>
    public static bool IsValidStartType(ServiceStartType t) =>
        t is ServiceStartType.Boot or ServiceStartType.System or ServiceStartType.Automatic or ServiceStartType.Manual or ServiceStartType.Disabled;

    /// <summary>Throws unless the service may be changed (not protected, is a Win32 service, on the catalogue or a known third party).</summary>
    public static void Gate(ServiceOption o, bool insiderEnrolled)
    {
        if (ServiceCatalogue.IsProtected(o.Service.Name, insiderEnrolled) || o.Group == ServiceGroup.Protected)
            throw new InvalidOperationException($"'{o.Service.Name}' is a protected service and is never changed");
        if (!o.Service.IsWin32)
            throw new InvalidOperationException($"'{o.Service.Name}' is a driver or kernel service and is never changed");
        if (o.Group is not (ServiceGroup.Optional or ServiceGroup.Unknown))
            throw new InvalidOperationException($"'{o.Service.Name}' cannot be changed");
    }

    /// <summary>
    /// Restore (S1): a record read back from service-changes.json is untrusted. It goes through the SAME gate as Apply,
    /// against the service as it is live now, and its saved original start type must be a value PoolWatch could have
    /// written. Anything else (a service now protected, a driver, one no longer in a changeable group, or a tampered
    /// start type) is refused.
    /// </summary>
    public static void GateRestore(ServiceOption live, ServiceChange record, bool insiderEnrolled)
    {
        Gate(live, insiderEnrolled);
        if (!IsValidStartType(record.OriginalStart))
            throw new InvalidOperationException($"'{record.Service}': the saved original start type ({record.OriginalStart}) is not valid");
        // Part 3 review: the service must still be as PoolWatch left it (or already back to the original). A record that does
        // not match the live service (changed since, or not written by this change) is not acted on.
        if (live.Service.StartType != record.NewStart && live.Service.StartType != record.OriginalStart)
            throw new InvalidOperationException($"'{record.Service}' is {live.Service.StartType} now, not {record.NewStart} as PoolWatch left it: " +
                                                "it was changed since, so PoolWatch does not restore it (turn it off again with Apply, then Restore; " +
                                                $"or set it to {record.OriginalStart} in services.msc)");
    }

    /// <summary>The service is already at its saved original start type: Restore only drops the record (nothing to change).</summary>
    public static bool AlreadyRestored(ServiceOption live, ServiceChange record) =>
        live.Service.StartType == record.OriginalStart && (!StartAfterRestore(record) || live.Service.Running);
}
