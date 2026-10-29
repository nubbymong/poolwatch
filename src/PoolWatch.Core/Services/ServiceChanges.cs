using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoolWatch.Core.Services;

/// <summary>A Windows service's start type (the values advapi32 uses, so Restore puts back exactly the original).</summary>
public enum ServiceStartType { Boot = 0, System = 1, Automatic = 2, Manual = 3, Disabled = 4, Unknown = -1 }

/// <summary>
/// One saved change (addendum 4 B): the service's original start type and whether it was running, before PoolWatch
/// changed it, so "Restore" puts back exactly that. Kept in the admin-only data root (%ProgramData%\PoolWatch\service-changes.json).
/// </summary>
public sealed record ServiceChange
{
    [JsonPropertyName("service")] public required string Service { get; init; }
    [JsonPropertyName("displayName")] public string DisplayName { get; init; } = "";
    [JsonPropertyName("originalStart")] public required ServiceStartType OriginalStart { get; init; }
    [JsonPropertyName("originalRunning")] public required bool OriginalRunning { get; init; }
    [JsonPropertyName("newStart")] public required ServiceStartType NewStart { get; init; }
    [JsonPropertyName("changedUtc")] public DateTime ChangedUtc { get; init; }
    /// <summary>The preset or "manual" the change came from.</summary>
    [JsonPropertyName("via")] public string Via { get; init; } = "manual";
    /// <summary>What the service's process cost just before the change (cores, page faults/s), for "saved about N cores".</summary>
    [JsonPropertyName("beforeCores")] public double? BeforeCores { get; init; }
    [JsonPropertyName("beforePageFaultsPerSec")] public long? BeforePageFaultsPerSec { get; init; }
    /// <summary>
    /// Set when the change set the start type but the service did not stop (Windows does not stop a service while one that
    /// depends on it runs): the process it went on running in, 0 when not known. Taken off once the service is seen
    /// stopped, in another process or at another start type (<see cref="ServicePlan.KeptRunning"/>).
    /// </summary>
    [JsonPropertyName("notStoppedPid")] public int? NotStoppedPid { get; init; }

    public string Summary() =>
        $"{Service}{(DisplayName.Length > 0 ? $" ({DisplayName})" : "")}: {OriginalStart} -> {NewStart}" +
        $" ({ChangedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}, {Via})";
}

/// <summary>Reads and writes the change log (one record per service PoolWatch changed; the newest write wins).</summary>
public static class ServiceChangeLog
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// The records, or null when the text is not a list of records shaped as PoolWatch writes them ("[]" when empty):
    /// damaged, cut off, empty or zero-filled (as a power cut can leave a file), or a record without its fields or its
    /// service. Null is never "no changes", so a file that may hold the only originals of the services PoolWatch changed
    /// is never written over. A start type out of range still parses: Restore's gate refuses that record alone.
    /// </summary>
    public static List<ServiceChange>? Parse(string json)
    {
        List<ServiceChange>? list;
        try { list = JsonSerializer.Deserialize<List<ServiceChange>>(json, Options); }
        catch (JsonException) { return null; }
        // "null", a null in the list, or a record whose service is null or "": none is anything PoolWatch writes.
        return list is not null && list.All(c => c is not null && !string.IsNullOrEmpty(c.Service)) ? list : null;
    }

    public static string ToJson(IEnumerable<ServiceChange> changes) => JsonSerializer.Serialize(changes.ToList(), Options);

    /// <summary>Adds or replaces the record for a service (one live record each).</summary>
    public static List<ServiceChange> Upsert(IEnumerable<ServiceChange> existing, ServiceChange change)
    {
        var list = existing.Where(c => !c.Service.Equals(change.Service, StringComparison.OrdinalIgnoreCase)).ToList();
        list.Add(change);
        return list;
    }
}
