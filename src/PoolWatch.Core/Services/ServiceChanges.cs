using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoolWatch.Core.Services;

/// <summary>A Windows service's start type (the values advapi32 uses, so Restore puts back exactly the original).</summary>
public enum ServiceStartType { Boot = 0, System = 1, Automatic = 2, Manual = 3, Disabled = 4, Unknown = -1 }

/// <summary>
/// One saved change: the service's original start type and whether it was running, before PoolWatch
/// changed it, so "Restore" puts back exactly that. Kept in the admin-only data root (%ProgramData%\PoolWatch\service-changes.json).
/// </summary>
public sealed record ServiceChange : IJsonOnDeserializing, IJsonOnDeserialized
{
    [JsonPropertyName("service")] public required string Service { get; init; }
    [JsonPropertyName("displayName")] public string DisplayName { get; init; } = "";
    [JsonPropertyName("originalStart")] public required ServiceStartType OriginalStart { get; init; }
    /// <summary>
    /// It was running (or paused, or starting, resuming or pausing) when PoolWatch changed it: as the change saw it, and
    /// until then as the services were read. False also when that is not known (<see cref="OriginalRunningNotKnown"/>).
    /// </summary>
    [JsonPropertyName("originalRunning")] public required bool OriginalRunning { get; init; }
    /// <summary>
    /// PoolWatch does not know whether the service was running when it changed it, so Put back does not start it, and says
    /// so when its original is not Disabled and it is not running then (<see cref="ServicePlan.RunningNotKnown"/>; an
    /// original of Disabled is put back to Disabled and stopped if it runs, whatever it did before): Windows could not give
    /// its state, or the record saved before the change (which says "running" only when the
    /// services were read so, <see cref="ServicePlan.RunningBeforeTheChange"/>) was never given what the change saw (its
    /// save failed, or PoolWatch stopped before then), or the record is one of v1.0.0-beta.1 that says "not running"
    /// (<see cref="IJsonOnDeserialized.OnDeserialized"/>). Always written, true or false, so a record without it is one of
    /// that earlier version.
    /// </summary>
    [JsonPropertyName("originalRunningNotKnown")]
    public bool OriginalRunningNotKnown { get => _notKnown ?? false; init => _notKnown = value; }

    /// <summary><see cref="OriginalRunningNotKnown"/>: null only while a record read from a file has not given it (yet, or at all).</summary>
    private bool? _notKnown = false;

    void IJsonOnDeserializing.OnDeserializing() => _notKnown = null;

    /// <summary>
    /// A record read without "originalRunningNotKnown" was written by v1.0.0-beta.1, which saved "originalRunning" from the
    /// services list alone: true only when the list read the service as RUNNING, so false also for one it turned off while
    /// paused, starting or in a state Windows could not give. Its true stays "running"; its false reads as not known, never
    /// as "was not running".
    /// </summary>
    void IJsonOnDeserialized.OnDeserialized() => _notKnown ??= !OriginalRunning;
    [JsonPropertyName("newStart")] public required ServiceStartType NewStart { get; init; }
    [JsonPropertyName("changedUtc")] public DateTime ChangedUtc { get; init; }
    /// <summary>The preset or "manual" the change came from.</summary>
    [JsonPropertyName("via")] public string Via { get; init; } = "manual";
    /// <summary>What the service's process cost just before the change (cores, page faults/s), for "saved about N cores".</summary>
    [JsonPropertyName("beforeCores")] public double? BeforeCores { get; init; }
    [JsonPropertyName("beforePageFaultsPerSec")] public long? BeforePageFaultsPerSec { get; init; }
    /// <summary>
    /// Set when the change set the start type but the service did not stop (Windows does not stop a service while one that
    /// depends on it has not stopped, say): the process it went on running in, 0 when not known. Taken off once the
    /// service is seen stopped, in another process or at another start type (<see cref="ServicePlan.KeptRunning"/>).
    /// </summary>
    [JsonPropertyName("notStoppedPid")] public int? NotStoppedPid { get; init; }

    /// <summary>
    /// The PoolWatch process whose change of the service (Turn off, Put back) is under way: saved before it acts and taken
    /// off once it is done, so the self-repair check of another PoolWatch (the app, while one from the command line acts)
    /// does not read that change as the service switched back on outside PoolWatch. Not written when null.
    /// </summary>
    [JsonPropertyName("underWay"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ChangeUnderWay? UnderWay { get; init; }

    public string Summary() =>
        $"{Service}{(DisplayName.Length > 0 ? $" ({DisplayName})" : "")}: {OriginalStart} -> {NewStart}" +
        $" ({ChangedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}, {Via})";
}

/// <summary>
/// Who is changing a service now (<see cref="ServiceChange.UnderWay"/>): the process, by its number and the time it
/// started (a later process given the same number is not taken for it), and when the change began on the clock Windows
/// keeps since it started (GetTickCount64), which is the same in every process and only goes forward.
/// </summary>
public sealed record ChangeUnderWay
{
    [JsonPropertyName("pid")] public required int Pid { get; init; }
    /// <summary>When that process started (FILETIME, UTC).</summary>
    [JsonPropertyName("processStarted")] public required long ProcessStarted { get; init; }
    /// <summary>Milliseconds since Windows started, when the change began.</summary>
    [JsonPropertyName("tickMs")] public required long TickMs { get; init; }
    /// <summary>
    /// The change is a Put back of the record (not a Turn off). A record that still carries such a mark once that process
    /// is gone, or once the bound on a change is over, is one a Put back began on, and is read by what a Put back leaves
    /// (<see cref="ServicePlan.LeftByItsPutBack"/>): while its service is at the original start type (and seen stopped,
    /// for an original of Disabled), the self-repair check does not report it as switched back on outside PoolWatch and
    /// the State column says a Put back began, whoever set that start type. Not written when false.
    /// </summary>
    [JsonPropertyName("putBack"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool PutBack { get; init; }
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
