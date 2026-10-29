using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoolWatch.Core.Services;

/// <summary>One of the owner's named profiles on the Services tab: the services it keeps off.</summary>
public sealed record ServiceProfile(string Name, IReadOnlyList<string> Services);

/// <summary>
/// The owner's service profiles as settings.json keeps them, the one PoolWatch applies when it starts (at most one) and
/// how many minutes after it starts. Immutable: a change makes a new set (<see cref="ServiceProfileRules"/>). Read
/// leniently (<see cref="ServiceProfileSetConverter"/>): a profile that is not in the form PoolWatch writes is left out
/// with why (<see cref="Problems"/>, logged at start), and never stops the rest of settings.json being read.
/// </summary>
[JsonConverter(typeof(ServiceProfileSetConverter))]
public sealed class ServiceProfileSet
{
    public IReadOnlyList<ServiceProfile> Profiles { get; init; } = [];
    /// <summary>The name of the profile applied at start, or null (none). It may name a profile that is not there: the start then applies nothing and says so.</summary>
    public string? AtStart { get; init; }
    /// <summary>Minutes after PoolWatch starts before the profile is applied (1 to 15).</summary>
    public int DelayMinutes { get; init; } = ServiceProfileRules.DefaultDelayMinutes;
    /// <summary>What was left out or ignored when this set was read, and why. Never saved.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    /// <summary>The profile with this name (any case, spaces at the ends ignored), or null.</summary>
    public ServiceProfile? Find(string? name) => name is null ? null : Profiles.FirstOrDefault(p => ServiceProfileRules.SameName(p.Name, name));

    /// <summary>Whether <paramref name="name"/> is the profile applied at start.</summary>
    public bool IsAtStart(string name) => AtStart is { } a && ServiceProfileRules.SameName(a, name);
}

/// <summary>
/// The rules for the owner's service profiles. A profile lists only services the catalogue knows and the protected gate
/// allows: an entry naming a protected service or one the catalogue does not list is ignored when the set is read
/// (<see cref="Clean"/>), so a profile can never reach anything Apply would not offer. The built-in profiles
/// ("Telemetry only", "Agentic coding PC") are not stored: they tick what the catalogue marks (<see cref="ServicePlan.Tick"/>).
/// </summary>
public static class ServiceProfileRules
{
    public const int DefaultDelayMinutes = 3, MinDelayMinutes = 1, MaxDelayMinutes = 15;
    public const int MaxProfiles = 50, MaxNameLength = 60, MaxServicesPerProfile = 200, MaxServiceNameLength = 256;

    /// <summary>
    /// The built-in profiles' names and the combo's hand-ticking entry: no profile of the owner's may take one.
    /// <see cref="AgenticCodingPc"/> is the "Agentic coding PC" profile (it was called "Dev workstation" before 1.0.0): it
    /// ticks the catalogue's preset rows that have an official source (<see cref="ServicePlan.Tick"/>).
    /// </summary>
    public const string TelemetryOnly = "Telemetry only", AgenticCodingPc = "Agentic coding PC", Custom = "Custom (tick by hand)";

    public static IReadOnlyList<string> BuiltIns { get; } = [TelemetryOnly, AgenticCodingPc];

    public static bool SameName(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    public static bool IsBuiltIn(string name) => BuiltIns.Any(b => SameName(b, name));

    /// <summary>
    /// Null when <paramref name="name"/> (trimmed) may name one of the owner's profiles; else why not. Other profiles of
    /// the owner's are the caller's to check (a save replaces one of the same name; a rename may not take one).
    /// </summary>
    public static string? NameProblem(string? name)
    {
        string n = name?.Trim() ?? "";
        if (n.Length == 0) return "a profile needs a name";
        if (n.Length > MaxNameLength) return $"a profile's name is at most {MaxNameLength} characters";
        if (n.Any(char.IsControl)) return "a profile's name cannot hold a control character";
        if (IsBuiltIn(n)) return $"{n} is a built-in profile";
        if (SameName(n, Custom)) return $"{Custom} is not a profile";
        return null;
    }

    /// <summary>A Windows service name: 1 to 256 characters, no slash, backslash or control character.</summary>
    public static bool IsServiceName(string? s) =>
        s is { Length: > 0 and <= MaxServiceNameLength } && s.Trim().Length == s.Length && s.IndexOfAny(['/', '\\']) < 0 && !s.Any(char.IsControl);

    /// <summary>A name as the log may show it: control characters as '?', at most 80 characters.</summary>
    public static string Shown(string? s)
    {
        string t = new((s ?? "").Select(c => char.IsControl(c) ? '?' : c).ToArray());
        return t.Length > 80 ? t[..80] + "..." : t;
    }

    /// <summary>
    /// The set with every profile that cannot be used left out (a bad or taken name, too many profiles or services, an
    /// entry that is not a service name) and every entry naming a protected service or one the catalogue does not list
    /// ignored, each with why in <see cref="ServiceProfileSet.Problems"/>. The protected gate is applied as for a PC in
    /// Windows Insider (the stricter reading: whether it is enrolled is not known here). Services are kept in the
    /// catalogue's spelling, each once. Idempotent.
    /// </summary>
    public static ServiceProfileSet Clean(ServiceProfileSet set, ServiceCatalogue catalogue)
    {
        var problems = new List<string>(set.Problems);
        var kept = new List<ServiceProfile>();
        foreach (var p in set.Profiles)
        {
            if (p is null) continue;
            string name = p.Name?.Trim() ?? "";
            if (NameProblem(name) is { } why) { problems.Add($"Service profile \"{Shown(name)}\" left out: {why}."); continue; }
            if (kept.Any(k => SameName(k.Name, name))) { problems.Add($"Service profile {Shown(name)} left out: another profile has the same name."); continue; }
            if (kept.Count >= MaxProfiles) { problems.Add($"Service profile {Shown(name)} left out: settings.json holds more than {MaxProfiles} profiles."); continue; }
            var listed = p.Services ?? [];
            if (listed.Count > MaxServicesPerProfile) { problems.Add($"Service profile {Shown(name)} left out: it lists more than {MaxServicesPerProfile} services."); continue; }
            if (!listed.All(IsServiceName)) { problems.Add($"Service profile {Shown(name)} left out: it lists something that is not a service name."); continue; }
            var services = new List<string>();
            foreach (var s in listed.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (ServiceCatalogue.IsProtected(s, insiderEnrolled: true))
                {
                    problems.Add($"Service profile {Shown(name)}: {Shown(s)} ignored: it is a protected service, which PoolWatch never changes.");
                    continue;
                }
                if (catalogue.Find(s) is not { } entry)
                {
                    problems.Add($"Service profile {Shown(name)}: {Shown(s)} ignored: it is not on PoolWatch's list of services you may turn off.");
                    continue;
                }
                services.Add(entry.Services.First(x => x.Equals(s, StringComparison.OrdinalIgnoreCase)));
            }
            kept.Add(new ServiceProfile(name, services));
        }
        return new ServiceProfileSet { Profiles = kept, AtStart = set.AtStart?.Trim(), DelayMinutes = set.DelayMinutes, Problems = problems };
    }

    /// <summary>
    /// The set with the profile <paramref name="name"/> saved: a new name adds it at the end, a name already there replaces
    /// that profile in its place. <paramref name="atStart"/>: it becomes the profile applied at start (no other is), after
    /// <paramref name="delayMinutes"/> (clamped to 1 to 15); unticked, it stops being applied at start if it was, also when
    /// the choice named it before it was there. A new name past <see cref="MaxProfiles"/> profiles throws: reading the set
    /// would leave it out, and the choice of it at start would then name no profile.
    /// </summary>
    public static ServiceProfileSet Saved(ServiceProfileSet set, string name, IEnumerable<string> services, bool atStart, int delayMinutes)
    {
        name = name.Trim();
        if (NameProblem(name) is { } why) throw new ArgumentException(why, nameof(name));
        var profile = new ServiceProfile(name, services.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        var list = set.Profiles.ToList();
        int at = list.FindIndex(p => SameName(p.Name, name));
        if (at >= 0) list[at] = profile;
        else if (list.Count >= MaxProfiles) throw new InvalidOperationException(TooMany);
        else list.Add(profile);
        return new ServiceProfileSet
        {
            Profiles = list,
            AtStart = atStart ? name : set.IsAtStart(name) ? null : set.AtStart,
            DelayMinutes = atStart ? Math.Clamp(delayMinutes, MinDelayMinutes, MaxDelayMinutes) : set.DelayMinutes,
        };
    }

    /// <summary>Why a new profile cannot be saved when there are <see cref="MaxProfiles"/> already.</summary>
    public static string TooMany => $"there are {MaxProfiles} profiles already, the most PoolWatch keeps: delete one first, or save over one of them";

    /// <summary>
    /// The set with <paramref name="from"/> renamed <paramref name="to"/> in its place; the start follows it. A choice at
    /// start that named <paramref name="to"/> while no profile had that name is dropped, so a rename never makes a profile
    /// apply at start that was not chosen to.
    /// </summary>
    public static ServiceProfileSet Renamed(ServiceProfileSet set, string from, string to)
    {
        to = to.Trim();
        if (NameProblem(to) is { } why) throw new ArgumentException(why, nameof(to));
        if (set.Find(from) is not { } old) throw new ArgumentException($"there is no profile called {from}", nameof(from));
        if (set.Find(to) is { } other && !ReferenceEquals(other, old)) throw new ArgumentException($"there is already a profile called {other.Name}", nameof(to));
        return new ServiceProfileSet
        {
            Profiles = set.Profiles.Select(p => ReferenceEquals(p, old) ? p with { Name = to } : p).ToList(),
            AtStart = set.IsAtStart(old.Name) ? to : set.IsAtStart(to) ? null : set.AtStart,
            DelayMinutes = set.DelayMinutes,
        };
    }

    /// <summary>
    /// The profile applied at start and those of <paramref name="services"/> it lists (in its own order), or (null, none):
    /// Restore and Undo of such a service last only until the next start unless it is taken out of the profile.
    /// </summary>
    public static (string? Profile, IReadOnlyList<string> Services) AtStartHolding(ServiceProfileSet set, IEnumerable<string> services)
    {
        if (set.Find(set.AtStart) is not { } p) return (null, []);
        var asked = services.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var held = p.Services.Where(asked.Contains).ToList();
        return held.Count == 0 ? (null, []) : (p.Name, held);
    }

    /// <summary>The set with <paramref name="services"/> taken out of the profile <paramref name="name"/> (the rest as it was).</summary>
    public static ServiceProfileSet Without(ServiceProfileSet set, string name, IEnumerable<string> services)
    {
        if (set.Find(name) is not { } old) throw new ArgumentException($"there is no profile called {name}", nameof(name));
        var gone = services.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new ServiceProfileSet
        {
            Profiles = set.Profiles.Select(p => ReferenceEquals(p, old) ? p with { Services = p.Services.Where(s => !gone.Contains(s)).ToList() } : p).ToList(),
            AtStart = set.AtStart,
            DelayMinutes = set.DelayMinutes,
        };
    }

    /// <summary>The set without <paramref name="name"/>; when it was applied at start, none is now. No service is changed.</summary>
    public static ServiceProfileSet Deleted(ServiceProfileSet set, string name) => new()
    {
        Profiles = set.Profiles.Where(p => !SameName(p.Name, name)).ToList(),
        AtStart = set.IsAtStart(name) ? null : set.AtStart,
        DelayMinutes = set.DelayMinutes,
    };

    // ---------------------------------------------------------------- settings.json

    /// <summary>
    /// The set as settings.json holds it, read leniently: anything not in the form PoolWatch writes is left out with why,
    /// and the start applies nothing when its own part (which profile, after how long) cannot be read. Never throws.
    /// </summary>
    public static ServiceProfileSet FromJson(JsonElement e)
    {
        const string NotOurs = "Service profiles in settings.json are not in the form PoolWatch writes: none is used, and none is applied at start.";
        if (e.ValueKind == JsonValueKind.Null) return new ServiceProfileSet();
        if (e.ValueKind != JsonValueKind.Object) return new ServiceProfileSet { Problems = [NotOurs] };
        var problems = new List<string>();
        var profiles = new List<ServiceProfile>();
        if (e.TryGetProperty("profiles", out var list))
        {
            if (list.ValueKind != JsonValueKind.Array) return new ServiceProfileSet { Problems = [NotOurs] };
            foreach (var p in list.EnumerateArray())
            {
                if (ProfileFrom(p) is { } read) profiles.Add(read);
                else problems.Add(p.ValueKind == JsonValueKind.Object && p.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String &&
                                  TextOf(n) is { } named
                    ? $"Service profile {Shown(named)} left out: it is not in the form PoolWatch writes."
                    : "A service profile in settings.json is not in the form PoolWatch writes: it is left out.");
            }
        }
        string? atStart = null;
        bool startReadable = true;
        if (e.TryGetProperty("atStart", out var a))
        {
            if (a.ValueKind == JsonValueKind.String && TextOf(a) is { } chosen) atStart = chosen;
            else if (a.ValueKind != JsonValueKind.Null) problems.Add("The service profile to apply at start cannot be read from settings.json: none is applied at start.");
        }
        int delay = DefaultDelayMinutes;
        if (e.TryGetProperty("delayMinutes", out var d))
        {
            if (d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out int m) && m is >= MinDelayMinutes and <= MaxDelayMinutes) delay = m;
            else if (atStart is not null)
            {
                startReadable = false;
                problems.Add($"The delay before the service profile is applied at start is not {MinDelayMinutes} to {MaxDelayMinutes} minutes in settings.json: none is applied at start.");
            }
        }
        return new ServiceProfileSet { Profiles = profiles, AtStart = startReadable ? atStart : null, DelayMinutes = delay, Problems = problems };
    }

    /// <summary>{"name": "...", "services": ["...", ...]}, or null when it is not that.</summary>
    private static ServiceProfile? ProfileFrom(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object) return null;
        if (!p.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) return null;
        if (!p.TryGetProperty("services", out var s) || s.ValueKind != JsonValueKind.Array) return null;
        var services = new List<string>();
        foreach (var x in s.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.String || TextOf(x) is not { } service) return null;
            services.Add(service);
        }
        return TextOf(n) is { } name ? new ServiceProfile(name, services) : null;
    }

    /// <summary>
    /// A JSON string's text, or null when it is not valid text: a lone half of a surrogate pair, which
    /// <see cref="JsonElement.GetString"/> refuses with an exception (a hand-edited settings.json can hold one).
    /// </summary>
    private static string? TextOf(JsonElement e)
    {
        try { return e.GetString(); }
        catch (InvalidOperationException) { return null; }
    }

    public static void Write(Utf8JsonWriter w, ServiceProfileSet set)
    {
        w.WriteStartObject();
        w.WriteStartArray("profiles");
        foreach (var p in set.Profiles)
        {
            w.WriteStartObject();
            w.WriteString("name", p.Name);
            w.WriteStartArray("services");
            foreach (var s in p.Services) w.WriteStringValue(s);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        if (set.AtStart is { } a) w.WriteString("atStart", a);
        w.WriteNumber("delayMinutes", set.DelayMinutes);
        w.WriteEndObject();
    }
}

/// <summary>settings.json's "serviceProfiles": read leniently (<see cref="ServiceProfileRules.FromJson"/>), written as PoolWatch writes it.</summary>
public sealed class ServiceProfileSetConverter : JsonConverter<ServiceProfileSet>
{
    public override ServiceProfileSet Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        return ServiceProfileRules.FromJson(doc.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, ServiceProfileSet value, JsonSerializerOptions options) => ServiceProfileRules.Write(writer, value);
}
