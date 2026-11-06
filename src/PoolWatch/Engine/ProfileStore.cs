using PoolWatch.Core.Services;

namespace PoolWatch.Engine;

/// <summary>
/// The owner's service profiles (the Services tab): read from the running settings, and saved, renamed and deleted through
/// <see cref="LiveSettings.Apply"/>, so settings.json in the admin-only data root is written first (a failed write changes
/// nothing), the running copy follows, and an open Settings form follows too (it never writes an older list back). A save
/// keeps only services the catalogue offers and the protected gate allows (<see cref="Core.Settings.Normalize"/>). Deleting
/// a profile changes no service.
/// </summary>
internal sealed class ProfileStore(LiveSettings live)
{
    public ServiceProfileSet Current => live.Running.ServiceProfiles;

    /// <summary>Saves (or replaces) <paramref name="name"/>; with <paramref name="atStart"/> it is the one applied at start, after <paramref name="delayMinutes"/>.</summary>
    public void Save(string name, IEnumerable<string> services, bool atStart, int delayMinutes)
    {
        var list = services.ToList();
        live.Apply($"service profile {ServiceProfileRules.Shown(name.Trim())} saved",
            s => s.ServiceProfiles = ServiceProfileRules.Saved(s.ServiceProfiles, name, list, atStart, delayMinutes));
    }

    public void Rename(string from, string to) =>
        live.Apply($"service profile {ServiceProfileRules.Shown(from)} renamed {ServiceProfileRules.Shown(to.Trim())}",
            s => s.ServiceProfiles = ServiceProfileRules.Renamed(s.ServiceProfiles, from, to));

    public void Delete(string name) =>
        live.Apply($"service profile {ServiceProfileRules.Shown(name)} deleted (no service changed)",
            s => s.ServiceProfiles = ServiceProfileRules.Deleted(s.ServiceProfiles, name));

    /// <summary>
    /// After a Restore or Undo the owner confirmed: <paramref name="services"/> are taken out of the profile applied at
    /// start, so it does not turn them off again. Its name, or null when it lists none of them. Throws when settings.json
    /// cannot be written (nothing changes then).
    /// </summary>
    public string? KeepOn(IReadOnlyCollection<string> services)
    {
        var (profile, held) = ServiceProfileRules.AtStartHolding(Current, services);
        if (profile is null) return null;
        live.Apply($"service profile {ServiceProfileRules.Shown(profile)}: {string.Join(", ", held.Select(ServiceProfileRules.Shown))} taken out (put back)",
            s => s.ServiceProfiles = ServiceProfileRules.Without(s.ServiceProfiles, profile, held));
        return profile;
    }

    /// <summary>
    /// Put back (the selected service) or Put all back on the Services tab, after the owner was told what it does to the profile applied
    /// at start: of the services it put back, those that are on now (<see cref="ServicePlan.OnAfterRestore"/>) and that
    /// the question named (<paramref name="named"/>, which the owner confirmed) are taken out of that profile, so it does
    /// not turn them off again; one put back to off stays in it. One the question did not name (turned off after it was
    /// asked, by the run at start or the command line) stays in it too, and a line says so. The take-out runs in the
    /// restore's own operation, so the run at start never turns off again a service it took out. The results, then the
    /// lines on the profile.
    /// </summary>
    public List<(string service, string? error)> RestoreKeepingOn(ServicesController services, string? service, IReadOnlyCollection<string> named) =>
        services.Restore(service, out _, putBack =>
        {
            var on = putBack.Where(c => ServicePlan.OnAfterRestore(c, services.Catalogue.Find(c.Service))).Select(c => c.Service).ToList();
            var asked = named.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var said = new List<(string service, string? error)>();
            if (on.Where(asked.Contains).ToList() is { Count: > 0 } take) said.AddRange(KeepOnResult(take));
            said.AddRange(LeftInResult(on.Where(s => !asked.Contains(s)).ToList(), service is null ? "Put all back" : "Put back"));
            return said;
        });

    /// <summary>
    /// "Set to its Windows default" on the Services tab, after the owner was told: when it set the service to a default
    /// that is on (<see cref="ServicePlan.OnAfterDefault"/>) and the question said so (<paramref name="confirmed"/>), the
    /// service is taken out of the profile applied at start, in the same operation, so it does not turn it off again; else
    /// it stays in it, and a line says so.
    /// </summary>
    public List<(string service, string? error)> DefaultKeepingOn(ServicesController services, ServiceOption option, bool confirmed) =>
        services.RestoreToDefault(option.Service.Name, set => !ServicePlan.OnAfterDefault(set) ? []
            : confirmed ? KeepOnResult([set.Service.Name]) : LeftInResult([set.Service.Name], "Set to its Windows default"));

    /// <summary>
    /// <paramref name="services"/>, turned on by a restore whose question did not name them, stay in the profile applied
    /// at start: "Left in Build box, as Put all back did not ask about it, so PoolWatch turns it off again when it starts:
    /// SysMain." (none when it lists none of them).
    /// </summary>
    private List<(string service, string? error)> LeftInResult(IReadOnlyCollection<string> services, string action)
    {
        var (profile, held) = ServiceProfileRules.AtStartHolding(Current, services);
        if (profile is null) return [];
        string them = held.Count == 1 ? "it" : "them";
        return [("(profile)", $"Left in {profile}, as {action} did not ask about {them}, so PoolWatch turns {them} off again when it starts: {string.Join(", ", held)}.")];
    }

    /// <summary>Takes <paramref name="services"/> out of the profile applied at start; what that did, as a result line (none when it lists none of them).</summary>
    private List<(string service, string? error)> KeepOnResult(IReadOnlyCollection<string> services)
    {
        string them = services.Count == 1 ? "it" : "them";
        try
        {
            return KeepOn(services) is { } p ? [("(profile)", $"Taken out of {p}, so PoolWatch no longer turns {them} off when it starts.")] : [];
        }
        catch (Exception ex) { return [("(profile)", $"Not taken out of the profile applied at start, which turns {them} off again then: {ex.Message}")]; }
    }
}
