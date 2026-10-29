using System.Globalization;

namespace PoolWatch.Core.Services;

/// <summary>A service as PoolWatch sees it on this PC, with the facts the page and the gate need.</summary>
public sealed record ServiceView
{
    public required string Name { get; init; }
    public string DisplayName { get; init; } = "";
    public ServiceStartType StartType { get; init; } = ServiceStartType.Unknown;
    public bool Running { get; init; }
    /// <summary>
    /// Neither running nor stopped when it was read: starting, stopping, resuming, pausing or paused, or Windows could not
    /// say (<see cref="Running"/> is false then too). Only a service that is neither this nor running was seen stopped.
    /// </summary>
    public bool NeitherRunningNorStopped { get; init; }
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
    /// <summary>
    /// The services this one needs, as Windows lists its dependencies (load-order groups left out). Windows does not stop
    /// a service while one that depends on it runs, and does not start one while a service it needs is Disabled.
    /// </summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];
}

/// <summary>
/// What Windows did not do once PoolWatch had set a service's start type, and why: stop it (Turn off, or putting back an
/// original of Disabled), or start it again (Put back). <see cref="Why"/> is Windows' answer in plain words;
/// <see cref="Cause"/> says what the words around it may add.
/// </summary>
public sealed record NotDone(string Why, NotDoneCause Cause = NotDoneCause.Other)
{
    /// <summary>Windows refused the stop of a service that was paused: it is still paused, so neither running nor stopped.</summary>
    public bool Paused { get; init; }
}

/// <summary>What a <see cref="NotDone"/> lets the result say besides its reason.</summary>
public enum NotDoneCause
{
    /// <summary>Any other answer, or none in time: the reason alone (a stop not done leaves the service running, or paused: <see cref="NotDone.Paused"/>).</summary>
    Other,
    /// <summary>Windows refused the stop because a service that depends on it is running (Windows error 1051).</summary>
    DependentsRunning,
    /// <summary>Windows could not say whether the service runs: it is not known to have stopped, nor to run.</summary>
    StateUnknown,
    /// <summary>
    /// When the wait ended the service was starting, stopping, resuming, pausing or paused (<see cref="NotDone.Why"/> names
    /// which): not stopped yet, and not running either.
    /// </summary>
    NotSettled,
    /// <summary>Windows did not start it because a service it needs did not start (Windows error 1068).</summary>
    NeededServiceNotStarted,
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

/// <summary>
/// The presets: a preset only TICKS boxes; nothing changes until Apply (addendum 4 B controls). <see cref="AgenticCodingPc"/>
/// is the built-in "Agentic coding PC" profile (<see cref="ServiceProfileRules.AgenticCodingPc"/>).
/// </summary>
public enum Preset { TelemetryOnly, AgenticCodingPc, Custom }

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
    /// The State column: PoolWatch's own change ("off (turned off by PoolWatch 2026-10-26 11:40)"; one Apply set to Manual,
    /// which it does not stop, "set to Manual, running (turned off by PoolWatch ...)"; one Apply set to Disabled but could
    /// not stop, while it keeps running, "set to Disabled, still running (...)"; one set to Disabled that is starting,
    /// stopping, resuming, pausing or paused, "set to Disabled, not known to have stopped (...)"), off outside PoolWatch (an
    /// owner choice or a Windows default), else running / the start type.
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
            if (s.StartType == c.NewStart && c.NewStart == ServiceStartType.Manual)
                return $"set to Manual, {(s.Running ? "running" : "stopped")} (turned off by PoolWatch {when})"; // as Apply left it: it does not stop these
            if (KeptRunning(c, s))
                return $"set to {c.NewStart}, still running (turned off by PoolWatch {when}; it stops when Windows restarts)";
            if (s.StartType == c.NewStart && c.NewStart == ServiceStartType.Disabled && !s.Running && s.NeitherRunningNorStopped)
                return $"set to Disabled, not known to have stopped (turned off by PoolWatch {when})"; // not off: it is starting, stopping, paused...
            if (s.StartType == c.NewStart && !s.Running)
                return $"{(c.NewStart == ServiceStartType.Disabled ? "off" : $"{c.NewStart}, stopped")} (turned off by PoolWatch {when})";
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

    /// <summary>
    /// The services a preset ticks (only catalogue entries; it never ticks Unknown or Protected). "Agentic coding PC"
    /// (<see cref="Preset.AgenticCodingPc"/>) ticks only preset entries with an official source: an entry marked for the
    /// preset whose source is none is left alone, so the preset never rests on PoolWatch's word alone.
    /// </summary>
    public static HashSet<string> Tick(Preset preset, IReadOnlyList<ServiceOption> options)
    {
        var ticked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (preset == Preset.Custom) return ticked;
        foreach (var o in options)
        {
            if (o.Group != ServiceGroup.Optional || o.Catalogue is not { } c || IsOff(o)) continue; // off already: nothing to tick
            bool pick = preset == Preset.AgenticCodingPc ? InAgenticPreset(c)
                : c.Category.StartsWith("Telemetry", StringComparison.OrdinalIgnoreCase); // "Telemetry only"
            if (pick) ticked.Add(o.Service.Name);
        }
        return ticked;
    }

    /// <summary>The entry is in the "Agentic coding PC" preset: marked for it, and with an official source.</summary>
    public static bool InAgenticPreset(CatalogueEntry entry) => entry.AgenticPreset && entry.Source.Kind != ServiceSourceKind.None;

    /// <summary>One service to change: what it will be set to, and the record to save first.</summary>
    public sealed record Operation(ServiceOption Option, ServiceStartType NewStart, ServiceChange Record);

    /// <summary>
    /// Builds the Apply list from the ticked services. Throws when a ticked service is not changeable (protected, a
    /// driver, or unknown to this plan): the gate, enforced here and again in the elevated helper. Throws too when its
    /// start type could not be read: that would be its recorded original, which Restore refuses, so the change could
    /// never be undone.
    /// </summary>
    public static List<Operation> BuildApply(IEnumerable<string> ticked, IReadOnlyList<ServiceOption> options, bool insiderEnrolled, string via, DateTime nowUtc)
    {
        var byName = options.ToDictionary(o => o.Service.Name, StringComparer.OrdinalIgnoreCase);
        var ops = new List<Operation>();
        foreach (var name in ticked.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!byName.TryGetValue(name, out var o)) throw new InvalidOperationException($"'{name}' is not an installed service on this PC");
            Gate(o, insiderEnrolled);
            if (!IsValidStartType(o.Service.StartType)) throw new InvalidOperationException(StartTypeUnread(o.Service.Name));
            var newStart = OffStart(o);
            ops.Add(new Operation(o, newStart, new ServiceChange
            {
                Service = o.Service.Name, DisplayName = o.Service.DisplayName, OriginalStart = o.Service.StartType,
                OriginalRunning = o.Service.Running, NewStart = newStart, ChangedUtc = nowUtc, Via = via,
            }));
        }
        return ops;
    }

    /// <summary>
    /// What Apply sets a service to: Manual where the catalogue says so, else Disabled; never looser than it is now, so a
    /// service already Disabled (by its owner, say) stays Disabled, and Apply stops it if it runs.
    /// </summary>
    public static ServiceStartType OffStart(ServiceOption o) =>
        o.Catalogue?.Off == OffStartType.Manual && o.Service.StartType != ServiceStartType.Disabled ? ServiceStartType.Manual : ServiceStartType.Disabled;

    /// <summary>
    /// The service is as Apply leaves it: at <see cref="OffStart"/>, and seen stopped when that is Disabled (Apply stops it
    /// only then); a Disabled service that is stopped is off whatever its catalogue entry says. One that runs, is starting,
    /// stopping, resuming, pausing or paused, or whose state Windows could not give is not off yet
    /// (<see cref="ServiceView.NeitherRunningNorStopped"/>), so Turn off asks for its stop again. A profile applied at
    /// start leaves a service that is off alone and turns off only the others.
    /// </summary>
    public static bool IsOff(ServiceOption o)
    {
        var off = OffStart(o);
        return o.Service.StartType == off && (off != ServiceStartType.Disabled || o.Service is { Running: false, NeitherRunningNorStopped: false });
    }

    /// <summary>
    /// PoolWatch's record of the service no longer matches it: the service is neither as PoolWatch left it nor back at
    /// its original (it was changed again since, outside PoolWatch), so Restore refuses the record (<see cref="GateRestore"/>).
    /// </summary>
    public static bool RecordDrifted(ServiceOption o) => o.PoolWatchChange is { } c && Drifted(o.Service.StartType, c);

    private static bool Drifted(ServiceStartType live, ServiceChange record) => live != record.NewStart && live != record.OriginalStart;

    /// <summary>
    /// Apply leaves the ticked service as it is and records nothing: it is off (<see cref="IsOff"/>), and PoolWatch's record
    /// of it, if any, still matches it. A service that reads as off but whose record drifted (<see cref="RecordDrifted"/>)
    /// goes through Apply, which records it again with the first original, so Restore works again.
    /// </summary>
    public static bool OffAlready(ServiceOption o) => IsOff(o) && !RecordDrifted(o);

    /// <summary>
    /// A service left at <paramref name="start"/> (running or not) is on as the profile applied at start reads it: not as
    /// Apply leaves it (<see cref="IsOff"/>), so a profile that lists it would turn it off.
    /// </summary>
    public static bool OnAt(string service, ServiceStartType start, bool running, CatalogueEntry? entry) =>
        !IsOff(new ServiceOption { Service = new ServiceView { Name = service, StartType = start, Running = running }, Group = ServiceGroup.Optional, Catalogue = entry });

    /// <summary>
    /// The service is on once <paramref name="record"/> is put back (Restore, Undo), so a profile applied at start that
    /// lists it would turn it off again. Never for a "Windows default" record (putting it back turns the service off), nor
    /// when its original is as Apply leaves a service (Disabled, or Manual for an entry turned off to Manual).
    /// </summary>
    public static bool OnAfterRestore(ServiceChange record, CatalogueEntry? entry) =>
        record.Via != ViaWindowsDefault && OnAt(record.Service, record.OriginalStart, StartAfterRestore(record), entry);

    /// <summary>
    /// The service is on once "Restore to Windows default" set it to its <see cref="ServiceOption.WindowsDefault"/>, so a
    /// profile applied at start that lists it would turn it off again (a Manual default of an entry turned off to Manual
    /// is not).
    /// </summary>
    public static bool OnAfterDefault(ServiceOption o) => o.WindowsDefault is { } d && OnAt(o.Service.Name, d, running: false, o.Catalogue);

    /// <summary>
    /// The self-repair warning: "Service re-enabled outside PoolWatch: DiagTrack is Automatic, running again (you turned it
    /// off). Turn it off again with Turn off, or keep it this way with Put back on the Services tab." When the profile applied at
    /// start lists the service, it says that the profile turns it off again.
    /// </summary>
    public static string SelfRepairText(string service, ServiceStartType start, bool running, string next, string? atStartProfile) =>
        $"Service re-enabled outside PoolWatch: {service} is {start}{(running ? ", running" : "")} again (you turned it off). " +
        (atStartProfile is null ? "" : $"{atStartProfile}, the profile applied when PoolWatch starts, turns it off again then. ") +
        $"{next} on the Services tab.";

    /// <summary>
    /// What Turn off says of a service it set to <paramref name="newStart"/> that is not known to have stopped, never
    /// "done": "set to Disabled, but still running: XblGameSave depends on it and is running. It stops when Windows
    /// restarts, or when you turn it off again after that service has stopped." The running dependents
    /// (<paramref name="runningDependents"/>, which PoolWatch never stops: nobody asked it to) are named only when Windows
    /// refused the stop because of them (<see cref="NotDoneCause.DependentsRunning"/>); for any other answer the result
    /// gives that answer, and when Windows could not say whether the service runs, it says so. One that was still
    /// starting, stopping, resuming, pausing or paused (<see cref="NotDoneCause.NotSettled"/>) is never said to be running:
    /// "set to Disabled, not stopped yet: it was still stopping 20 seconds after Windows took the stop. It stops when
    /// Windows restarts, if not before". One that was paused when Windows refused its stop (<see cref="NotDone.Paused"/>)
    /// is "still paused", never "still running". Until it is seen stopped it is not off (<see cref="IsOff"/>), so turning it
    /// off again asks for its stop again.
    /// </summary>
    public static string NotStoppedText(ServiceStartType newStart, NotDone why, IReadOnlyList<string> runningDependents)
    {
        if (why.Cause == NotDoneCause.StateUnknown)
            return $"{NotStoppedLead}{newStart}, but {why.Why}, so it is not known to have stopped. If it is still running, it stops when Windows " +
                   "restarts, or when you turn it off again";
        if (why.Cause == NotDoneCause.NotSettled)
            return $"{NotStoppedLead}{newStart}, not stopped yet: {why.Why}. {StopsAtRestartIfNotBefore}";
        if (why.Cause != NotDoneCause.DependentsRunning || runningDependents.Count == 0)
            return $"{NotStoppedLead}{newStart}, but {Still(why)}: {why.Why}. It stops when Windows restarts, or when you turn it off again";
        bool one = runningDependents.Count == 1;
        return $"{NotStoppedLead}{newStart}, but {Still(why)}: {string.Join(", ", runningDependents)} {(one ? "depends on it and is" : "depend on it and are")} running. " +
               $"It stops when Windows restarts, or when you turn it off again after {(one ? "that service has" : "those have")} stopped";
    }

    /// <summary>A service whose stop Windows refused, as it was: "still paused" (<see cref="NotDone.Paused"/>), else "still running".</summary>
    private static string Still(NotDone why) => why.Paused ? "still paused" : "still running";

    /// <summary>
    /// Whether a result of Turn off is <see cref="NotStoppedText"/>'s: the start type was set (a change, which Active
    /// changes lists), but the service is not known to have stopped. No other result of Turn off starts so.
    /// </summary>
    public static bool IsNotStoppedText(string? result) => result is not null && result.StartsWith(NotStoppedLead, StringComparison.Ordinal);

    private const string NotStoppedLead = "set to ";

    /// <summary>What follows for a service that was still starting, stopping, resuming, pausing or paused when the wait ended.</summary>
    private const string StopsAtRestartIfNotBefore = "It stops when Windows restarts, if not before";

    /// <summary>
    /// What putting back an original of Disabled says when the service did not stop: "it is still running: a service that
    /// depends on it is running. It stops when Windows restarts" (the start type is back all the same); "it is not stopped
    /// yet: ..." for one that was still starting, stopping, resuming, pausing or paused; "it is still paused: ..." for one
    /// that was paused when Windows refused its stop.
    /// </summary>
    public static string StillRunningAfterPutBack(NotDone why) => why.Cause switch
    {
        NotDoneCause.StateUnknown => $"{why.Why}, so it is not known to have stopped. If it is still running, it stops when Windows restarts",
        NotDoneCause.NotSettled => $"it is not stopped yet: {why.Why}. {StopsAtRestartIfNotBefore}",
        _ => $"it is {Still(why)}: {why.Why}. It stops when Windows restarts",
    };

    /// <summary>
    /// What Put back says when Windows did not start <paramref name="service"/> again. A service it needs that is Disabled
    /// (<paramref name="neededDisabled"/>, given only when Windows said one it needs did not start) is named, with what
    /// to do: "it needs XblAuthManager, which is still Disabled. Put back XblAuthManager too: after that, Windows starts
    /// XblGameSave when something asks for it" (<paramref name="neededPutBack"/>: PoolWatch has a change of it to put
    /// back). Else Windows' reason.
    /// </summary>
    public static string NotStartedText(string service, NotDone why, string? neededDisabled, bool neededPutBack)
    {
        if (why.Cause != NotDoneCause.NeededServiceNotStarted || neededDisabled is null) return $"starting it again failed: {why.Why}";
        return neededPutBack
            ? $"starting it again failed: it needs {neededDisabled}, which is still Disabled. Put back {neededDisabled} too: after that, Windows " +
              $"starts {service} when something asks for it"
            : $"starting it again failed: it needs {neededDisabled}, which is Disabled outside PoolWatch. Once {neededDisabled} is no longer " +
              $"Disabled, Windows starts {service} when something asks for it";
    }

    /// <summary>
    /// The service has kept running since PoolWatch's own change set its start type but could not stop it
    /// (<see cref="ServiceChange.NotStoppedPid"/>): still at that start type, running, in the same process (any process
    /// when it was not known). The self-repair check and the State column read it as PoolWatch's change, not as switched
    /// back on outside PoolWatch.
    /// </summary>
    public static bool KeptRunning(ServiceChange change, ServiceView live) =>
        change.NotStoppedPid is { } pid && live.StartType == change.NewStart && live.Running && (pid == 0 || live.ProcessId == pid);

    /// <summary>
    /// <paramref name="items"/> (services, by <paramref name="nameOf"/>) in the order Windows can act on them, else as
    /// given: with <paramref name="dependentsFirst"/> (turning off) a service comes after every listed one that depends on
    /// it, so that one is stopped first; else (putting back) after every listed one it depends on, so that one is back
    /// first. A dependency through a service that is not listed counts too (<paramref name="dependsOn"/>: a service's own
    /// dependencies, by name). It only reorders: nothing is added or left out.
    /// </summary>
    public static List<T> InDependencyOrder<T>(IReadOnlyList<T> items, Func<T, string> nameOf, Func<string, IReadOnlyList<string>> dependsOn, bool dependentsFirst)
    {
        var names = items.Select(nameOf).ToList();
        var needs = names.Select(n => AllDependencies(n, dependsOn)).ToList();
        // Item i has to come before item j.
        bool Before(int i, int j) => dependentsFirst ? needs[i].Contains(names[j]) : needs[j].Contains(names[i]);
        var left = Enumerable.Range(0, items.Count).ToList();
        var ordered = new List<T>(items.Count);
        while (left.Count > 0)
        {
            int next = left[0]; // a loop of dependencies (Windows refuses one): as given
            foreach (int i in left)
                if (!left.Any(j => j != i && Before(j, i))) { next = i; break; }
            ordered.Add(items[next]);
            left.Remove(next);
        }
        return ordered;
    }

    /// <summary>Every service <paramref name="service"/> depends on, directly or through another, never itself.</summary>
    private static HashSet<string> AllDependencies(string service, Func<string, IReadOnlyList<string>> dependsOn)
    {
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var todo = new Stack<string>();
        todo.Push(service);
        while (todo.Count > 0)
            foreach (var d in dependsOn(todo.Pop()))
                if (!d.Equals(service, StringComparison.OrdinalIgnoreCase) && all.Add(d)) todo.Push(d);
        return all;
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
        if (o.PoolWatchChange is not null) throw new InvalidOperationException($"'{o.Service.Name}': PoolWatch changed it; use Put back");
        if (o.OffOutsidePoolWatch != OffOutside) throw new InvalidOperationException($"'{o.Service.Name}' is not off outside PoolWatch");
        if (o.WindowsDefault is not ({ } d and (ServiceStartType.Automatic or ServiceStartType.Manual)))
            throw new InvalidOperationException($"'{o.Service.Name}': PoolWatch does not know its Windows default start type");
        if (!IsValidStartType(o.Service.StartType)) throw new InvalidOperationException(StartTypeUnread(o.Service.Name));
        return new Operation(o, d, new ServiceChange
        {
            Service = o.Service.Name, DisplayName = o.Service.DisplayName, OriginalStart = o.Service.StartType, OriginalRunning = o.Service.Running,
            NewStart = d, ChangedUtc = nowUtc, Via = ViaWindowsDefault,
        });
    }

    /// <summary>Why a service whose start type could not be read is not changed: it would be the original Restore puts back.</summary>
    private static string StartTypeUnread(string service) => $"'{service}': its current start type cannot be read, so it could not be put back later";

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
        if (Drifted(live.Service.StartType, record))
            throw new InvalidOperationException($"'{record.Service}' is {live.Service.StartType} now, not {record.NewStart} as PoolWatch left it: " +
                                                "it was changed since, so PoolWatch does not put it back (turn it off again on the Services tab, then Put back; " +
                                                $"or set it to {record.OriginalStart} in services.msc)");
    }

    /// <summary>The service is already at its saved original start type: Restore only drops the record (nothing to change).</summary>
    public static bool AlreadyRestored(ServiceOption live, ServiceChange record) =>
        live.Service.StartType == record.OriginalStart && (!StartAfterRestore(record) || live.Service.Running);
}
