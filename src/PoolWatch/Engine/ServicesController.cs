using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Services;
using PoolWatch.Engine.Changes;

namespace PoolWatch.Engine;

/// <summary>
/// The background-services page's engine side (addendum 4 B): enumerate the installed services, classify them against
/// the catalogue and the protected gate, apply and restore changes through the gated <see cref="ServiceControl"/>, and
/// keep the change log (service-changes.json) so Restore puts back exactly the original. Every change (Apply, Restore,
/// Restore to Windows default) goes through <see cref="ServicePlan"/>'s gate and then the live gate; there is no other
/// path. Watches for self-repair (a service the owner turned off that is switched back on by something else) and measures
/// what a change saved over the hour after it. The change log is read again before every edit and every listing, so a
/// record the command line wrote while the app runs is kept, listed and restored; only two processes saving at the same
/// moment can lose one of the two changes, as no lock spans processes. A service is changed only once its record is
/// saved; and a change log that is there but cannot be read is never written over (it may hold the only originals), is
/// listed in Active changes as a change that cannot be put back, and <see cref="RecordError"/> says why.
/// </summary>
internal sealed class ServicesController
{
    private readonly AppPaths _paths;
    private readonly Log _log;
    private readonly Action<Finding> _finding;
    private readonly object _lock = new();
    /// <summary>
    /// One change of services at a time (Apply, a profile at start, Restore, Restore to Windows default): each reads the
    /// record and the live services and acts on them, so a Restore from the window can never run between a profile's
    /// record and its change. Taken before <see cref="_lock"/>, never inside it.
    /// </summary>
    private readonly object _opLock = new();
    private int _version;
    private List<ServiceChange> _changes = new();
    private DateTime _nextSelfRepairCheck = DateTime.MinValue;
    private readonly ServiceSavings _savings = new();
    private readonly Func<int, (double cores, long faultsPerSec)?> _liveCost, _recentCost;
    private readonly IServiceBackend _backend;

    /// <param name="liveCost">A PID's cost in the latest window (the Cost column, the after-cost).</param>
    /// <param name="recentCost">A PID's average cost over the last minutes (the before-cost of a change), else the live one.</param>
    /// <param name="backend">The live services (reading, and the gated change); null: Windows. Tests pass a fake, so no test
    /// changes a real service (review round 2, item 12).</param>
    public ServicesController(AppPaths paths, Log log, Action<Finding> finding, Func<int, (double, long)?> liveCost, Func<int, (double, long)?> recentCost,
        IServiceBackend? backend = null)
    {
        _paths = paths;
        _log = log;
        _finding = finding;
        _liveCost = liveCost;
        _recentCost = recentCost;
        _backend = backend ?? new WindowsServiceBackend();
        // S2: the embedded catalogue only. An elevated PoolWatch must not take its "optional vs protected" decision from a
        // file an unelevated user could write; the hard protected gate is hard-coded regardless. (An admin-only override
        // can be reintroduced once the data root is admin-only, S3.)
        Catalogue = DefaultCatalogue.Load();
        Load();
    }

    public ServiceCatalogue Catalogue { get; }

    /// <summary>The profiles in the running settings (the tray app sets it): the self-repair line names the one applied at start.</summary>
    public Func<ServiceProfileSet>? Profiles { get; set; }

    private volatile string? _waitingAtStart;

    /// <summary>
    /// The profile the run at this start is still waiting to apply, or null (<see cref="ProfileAtStart.ForApp"/> sets it
    /// until that run ends): "Turn them off now?" then says the run turns off what it listed, whatever the answer.
    /// </summary>
    public string? WaitingAtStart { get => _waitingAtStart; set => _waitingAtStart = value; }

    /// <summary>Grows with every saved change of the record (the Services tab reads the list again when it moved, after a profile at start, say).</summary>
    public int Version { get { lock (_lock) return _version; } }
    /// <summary>The records as last read or written (the page and the self-repair check); <see cref="Current"/> reads the file.</summary>
    public IReadOnlyList<ServiceChange> Changes { get { lock (_lock) return _changes.ToList(); } }

    /// <summary>
    /// The records as service-changes.json holds them now (Active changes and the uninstaller). Throws when the file is
    /// there but cannot be read, so it is listed as a change that cannot be undone, never as no changes.
    /// </summary>
    public IReadOnlyList<ServiceChange> Current() { lock (_lock) return Read().ToList(); }

    /// <summary>
    /// Why service-changes.json could not be read the last time it was read, or null when it was read. While it is set,
    /// <see cref="Changes"/> is not the whole record, so "nothing to restore" cannot be told from it: the Services page's
    /// status line says why (Ui.ServicesPageText), and its Put back items go to <see cref="Restore"/>, which refuses with
    /// this reason while the file still cannot be read.
    /// </summary>
    public string? RecordError { get { lock (_lock) return _recordError; } }
    private string? _recordError;

    /// <summary>
    /// Records of services that a Restore put back but whose removal could not be saved: left out of every read while this
    /// PoolWatch runs, until a save succeeds (which then leaves them out of the file), so a restored service is not read
    /// as changed again. In memory only: after a restart with no such save, the record is read again, and Restore then
    /// finds the service already back and removes it.
    /// </summary>
    private readonly List<ServiceChange> _restoredNotRemoved = new();

    /// <summary>
    /// Always in the admin-only data root (Part 3 review), never under a headless --out folder, which can be one anyone may
    /// write: Restore acts on these records as an administrator.
    /// </summary>
    private string ChangeFile => _paths.Guard.CheckReal(Path.Combine(_paths.EnsureDir(_paths.DataRoot), "service-changes.json"));

    /// <summary>The PC is enrolled in Windows Insider (wisvc is then protected).</summary>
    public static bool InsiderEnrolled()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\WindowsSelfHost\Applicability");
            return k?.GetValue("Ring") is not null || k?.GetValue("BranchName") is not null;
        }
        catch { return false; }
    }

    /// <summary>Services Windows itself ships Disabled (so "off" reads as a default, not an owner choice).</summary>
    private static readonly HashSet<string> WindowsDefaultsOff = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppVClient", "DialogBlockingService", "MsKeyboardFilter", "NetTcpPortSharing", "RemoteAccess", "RemoteRegistry",
        "shpamsvc", "ssh-agent", "tzautoupdate", "UevAgentService",
    };

    /// <summary>
    /// The page's options: every installed service, grouped and annotated with the catalogue, its state (PoolWatch's own
    /// changes never read "changed outside PoolWatch") and the live cost of its process.
    /// </summary>
    public List<ServiceOption> Options()
    {
        // A change the command line made meanwhile then reads as PoolWatch's own. A file that cannot be read is reported
        // where it matters: Apply and Restore refuse, Active changes lists it, and RecordError (the page's status line) says why.
        try { lock (_lock) Read(); }
        catch (Exception) { }
        var services = ServiceCosts.Fill(_backend.Enumerate(), _liveCost);
        bool insider = _backend.InsiderEnrolled();
        return ServicePlan.Options(services, Catalogue, insider, WindowsDefaultsOff, Changes);
    }

    /// <summary>"saved about 1.7 cores over the hour after (...)" for a service PoolWatch turned off, while it is measured.</summary>
    public string? SavingsText(string service)
    {
        lock (_lock) return _savings.Get(service) is { } e ? ServiceSavings.Text(e, DateTime.UtcNow) : null;
    }

    /// <summary>
    /// Sampler thread, every sample: the after-cost of each service PoolWatch turned off in the last hour (its share of its
    /// host process when it runs again, else zero), and a finding when its hour is up.
    /// </summary>
    public void ObserveSavings(DateTime nowUtc, IReadOnlyList<Core.Model.ServiceEntry> running)
    {
        List<ServiceSavings.Entry> done;
        lock (_lock)
        {
            if (_savings.Entries.Count == 0) return;
            var perPid = running.Where(s => s.ProcessId > 0).GroupBy(s => s.ProcessId).ToDictionary(g => g.Key, g => g.Count());
            done = _savings.Observe(nowUtc, name =>
            {
                var svc = running.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && s.ProcessId > 0);
                if (svc is null || _liveCost(svc.ProcessId) is not { } host) return (0, 0);
                return ServiceCosts.Share(host, perPid.GetValueOrDefault(svc.ProcessId, 1));
            });
        }
        foreach (var e in done)
        {
            string msg = $"Service turned off an hour ago: {e.Service}: {ServiceSavings.Text(e, nowUtc)}";
            _finding(Finding(msg, FindingSeverity.Info, e.Service));
            _log.Info(msg);
        }
    }

    /// <summary>The set of services the page currently offers (group 1/2): the live gate's allow-list.</summary>
    private HashSet<string> Allowed(IReadOnlyList<ServiceOption> options) =>
        options.Where(o => o.CanTurnOff).Select(o => o.Service.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>What Apply says of a ticked service that is off already (<see cref="ServicePlan.OffAlready"/>): nothing is changed or recorded.</summary>
    public const string OffAlready = "off already, so left as it is";

    /// <summary>
    /// Turns off the ticked services (gated). Returns each service's result; records the originals first. A service that is
    /// off already is left as it is, with no record, as a profile applied at start leaves it: Active changes lists only
    /// what changed. Its result is <see cref="OffAlready"/>, in the error's place although nothing failed (the window and
    /// the command line print it as it is). One that is off but whose record no longer matches it is recorded again
    /// (<see cref="ServicePlan.OffAlready"/>), so Restore works again.
    /// </summary>
    public List<(string service, string? error)> Apply(IReadOnlyCollection<string> ticked, string via)
    {
        lock (_opLock)
        {
            var options = Options();
            bool insider = _backend.InsiderEnrolled();
            List<ServicePlan.Operation> ops;
            try { ops = ServicePlan.BuildApply(ticked, options, insider, via, DateTime.UtcNow); }
            catch (Exception ex) { _log.Info("services: apply refused: " + ex.Message); return [("(plan)", ex.Message)]; }
            var off = ops.Where(op => ServicePlan.OffAlready(op.Option)).Select(op => op.Option.Service.Name).ToList();
            if (off.Count > 0) _log.Info($"services: apply: {string.Join(", ", off)} {OffAlready}");
            var results = Run(ops.Where(op => !ServicePlan.OffAlready(op.Option)).ToList(), options, insider).Select(r => (r.service, r.error)).ToList();
            results.AddRange(off.Select(s => (s, (string?)OffAlready)));
            return results;
        }
    }

    /// <summary>
    /// Of <paramref name="names"/>, those the gate of <see cref="Apply"/> refuses on their own against one read of the
    /// services, each with why; none when the services cannot be read. It reads exactly what the gate reads (the services
    /// and the Insider flag through the same backend, under the same lock) and changes nothing.
    /// </summary>
    public List<(string service, string error)> RefusedAlone(IReadOnlyCollection<string> names, string via)
    {
        lock (_opLock)
        {
            List<ServiceOption> options;
            try { options = Options(); }
            catch (Exception) { return []; }
            bool insider = _backend.InsiderEnrolled();
            var refused = new List<(string, string)>();
            foreach (var name in names)
            {
                try { ServicePlan.BuildApply([name], options, insider, via, DateTime.UtcNow); }
                catch (Exception ex) { refused.Add((name, ex.Message)); } // as Apply's gate catches it
            }
            return refused;
        }
    }

    /// <summary>
    /// What a profile applied at start did: turned off, already off, left alone (and why), and failed (nothing changed).
    /// <see cref="NotStopped"/>: set to Disabled but not known to have stopped, and why (a change, which Active changes lists).
    /// </summary>
    public sealed record ProfileRun(IReadOnlyList<string> TurnedOff, IReadOnlyList<string> AlreadyOff,
        IReadOnlyList<(string service, string why)> Skipped, IReadOnlyList<(string service, string error)> Failed)
    {
        public IReadOnlyList<(string service, string why)> NotStopped { get; init; } = [];
    }

    /// <summary>
    /// Runs <paramref name="op"/> as one change of services, under the operation lock: no Apply, profile at start, Restore
    /// or Restore to Windows default runs while it does (each takes the same lock again inside it). The run at start reads
    /// its profile in here, so an edit, or a restore that took a service out of the profile, made before its turn is seen.
    /// </summary>
    public void OneChange(Action op)
    {
        lock (_opLock) op();
    }

    /// <summary>
    /// A profile applied when PoolWatch starts, asking nothing: each of its services that is installed, on the catalogue,
    /// passes Apply's own gate (<see cref="ServicePlan.BuildApply"/>, so a protected service and the Insider rules hold)
    /// and is back on goes through exactly Apply's path (<see cref="Run"/>: its original recorded first, then the live
    /// gate, a log line and a finding each, and an Active changes row). Every other one is left as it is, with why in the
    /// log. While PoolWatch's record of its changes cannot be read nothing is changed, since no original could be saved.
    /// </summary>
    public ProfileRun ApplyProfile(ServiceProfile profile, string via)
    {
        lock (_opLock) return ApplyProfileNow(profile, via);
    }

    private ProfileRun ApplyProfileNow(ServiceProfile profile, string via)
    {
        string who = $"Service profile {ServiceProfileRules.Shown(profile.Name)} at start";
        var options = Options();
        if (RecordError is { } unread)
        {
            _log.Info($"{who}: nothing turned off: PoolWatch cannot read how the services were set: {unread}");
            return new([], [], profile.Services.Select(s => (s, "PoolWatch cannot read how the services were set")).ToList(), []);
        }
        bool insider = _backend.InsiderEnrolled();
        var byName = options.ToDictionary(o => o.Service.Name, StringComparer.OrdinalIgnoreCase);
        var ops = new List<ServicePlan.Operation>();
        var already = new List<string>();
        var skipped = new List<(string, string)>();
        var now = DateTime.UtcNow;
        foreach (var name in profile.Services.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!byName.TryGetValue(name, out var o)) { skipped.Add((name, "it is not installed on this PC")); continue; }
            ServicePlan.Operation op;
            try { op = ServicePlan.BuildApply([o.Service.Name], options, insider, via, now).Single(); }
            catch (Exception ex) { skipped.Add((name, ex.Message)); continue; }
            if (o.Catalogue is null) { skipped.Add((name, "it is not on PoolWatch's list of services you may turn off")); continue; }
            if (ServicePlan.IsOff(o)) already.Add(o.Service.Name);
            else ops.Add(op);
        }
        foreach (var (s, why) in skipped) _log.Info($"{who}: {ServiceProfileRules.Shown(s)} left as it is: {why}");
        var results = Run(ops, options, insider);
        var off = results.Where(r => r.error is null).Select(r => r.service).ToList();
        var notStopped = results.Where(r => r.notStopped).Select(r => (r.service, r.error!)).ToList();
        var failed = results.Where(r => r.error is not null && !r.notStopped).Select(r => (r.service, r.error!)).ToList();
        _log.Info($"{who}: {off.Count} turned off, {already.Count} already off, {skipped.Count} left as they are, {failed.Count} not turned off" +
                  (notStopped.Count > 0 ? $", {notStopped.Count} set to Disabled but not known to have stopped" : ""));
        return new(off, already, skipped, failed) { NotStopped = notStopped };
    }

    /// <summary>
    /// Apply's path for each operation, the one every turn-off takes: the original saved first (a service whose record
    /// cannot be saved is not changed), then the live gate and the change, a record taken back when the change fails, and a
    /// log line and a finding for each. A service another of <paramref name="ops"/> depends on is changed after it, so that
    /// one is stopped first. One whose start type was set but that did not stop (one that depends on it runs, say) keeps its
    /// record, which says so, and its result says why and when it stops: never done (<c>notStopped</c>). From the save of a
    /// service's record until its mark is saved, the self-repair check skips it (<see cref="_applying"/>), so a check in
    /// between never reads PoolWatch's own change, still under way, as switched back on outside it.
    /// </summary>
    /// <param name="options">The services as read for these operations (their dependencies, and the live gate's allow-list).</param>
    private List<(string service, string? error, bool notStopped)> Run(List<ServicePlan.Operation> ops, IReadOnlyList<ServiceOption> options, bool insider)
    {
        var allowed = Allowed(options);
        var views = Views(options.Select(o => o.Service));
        ops = ServicePlan.InDependencyOrder(ops, op => op.Option.Service.Name, DependsOn(views), dependentsFirst: true);
        var stopped = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // by this run
        var results = new List<(string, string?, bool)>();
        foreach (var op in ops)
        {
            // S1: keep the FIRST saved original if PoolWatch already changed this service (a re-Apply must not record
            // the already-off state as the "original"), as the file holds it now. The record is saved BEFORE the change
            // so Restore works even if PoolWatch dies mid-apply; when it cannot be saved, the service is not changed. If
            // the change then fails, the record is taken back.
            // What its process cost just before (its share of a shared host), for "saved about N cores" afterwards.
            var before = BeforeCost(op.Option.Service);
            ServiceChange? prior = null;
            var record = op.Record;
            string? err = null;
            string? notStopped = null;
            lock (_lock) _applying.Add(op.Option.Service.Name);
            try
            {
                try
                {
                    Edit(now =>
                    {
                        prior = now.FirstOrDefault(x => Is(x, op.Option.Service.Name));
                        record = (prior is null ? op.Record
                            : op.Record with { OriginalStart = prior.OriginalStart, OriginalRunning = prior.OriginalRunning })
                            with { BeforeCores = before.cores, BeforePageFaultsPerSec = before.faultsPerSec };
                        return ServiceChangeLog.Upsert(now, record);
                    });
                }
                catch (Exception ex) { err = NotSaved(ex); }
                if (err is null)
                {
                    // Turned off again: a later switch-on is reported, even to a state one was reported in before.
                    lock (_lock) _repairWarned.Remove(op.Option.Service.Name);
                    NotDone? why;
                    (err, why) = _backend.Apply(op, allowed, insider);
                    if (err is not null) TakeBack(record, prior);
                    else if (why is not null)
                    {
                        // The start type is set, so the record stays (Put back works); it says the service went on running.
                        notStopped = ServicePlan.NotStoppedText(op.NewStart, why, RunningDependents(op.Option.Service.Name, options, stopped));
                        MarkNotStopped(record, op.Option.Service.ProcessId);
                    }
                    else
                    {
                        stopped.Add(op.Option.Service.Name);
                        lock (_lock) _savings.Start(op.Option.Service.Name, DateTime.UtcNow, before.cores, before.faultsPerSec);
                    }
                }
            }
            finally { lock (_lock) _applying.Remove(op.Option.Service.Name); } // also when the change throws
            results.Add((op.Option.Service.Name, err ?? notStopped, err is null && notStopped is not null));
            string msg = err is not null ? $"Service not turned off: {op.Option.Service.Name}: {err}"
                : notStopped is not null ? $"Service not stopped: {record.Summary()}: {notStopped}"
                : $"Service turned off: {record.Summary()}; before: {ServicePlan.Cost(before.cores, before.faultsPerSec)}";
            _finding(Finding(msg, err is null && notStopped is null ? FindingSeverity.Info : FindingSeverity.Warning, op.Option.Service.Name));
            _log.Info(msg);
        }
        return results;
    }

    /// <summary>The services as read, by name.</summary>
    private static Dictionary<string, ServiceView> Views(IEnumerable<ServiceView> services)
    {
        var byName = new Dictionary<string, ServiceView>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in services) byName.TryAdd(s.Name, s);
        return byName;
    }

    /// <summary>A service's dependencies as <paramref name="views"/> has them (none for one not there).</summary>
    private static Func<string, IReadOnlyList<string>> DependsOn(Dictionary<string, ServiceView> views) =>
        name => views.TryGetValue(name, out var s) ? s.DependsOn : [];

    /// <summary>
    /// The services that depend on <paramref name="service"/> and ran when the services were read, but those this run
    /// stopped: why Windows did not stop it, when it refused because of them. PoolWatch never stops them: nobody asked it to.
    /// </summary>
    private static List<string> RunningDependents(string service, IReadOnlyList<ServiceOption> options, IReadOnlySet<string> stopped) =>
        options.Where(o => o.Service.Running && !stopped.Contains(o.Service.Name)
                           && o.Service.DependsOn.Contains(service, StringComparer.OrdinalIgnoreCase))
            .Select(o => o.Service.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Records that the change set the start type but the service went on running, in process <paramref name="pid"/> (0:
    /// not known), so the self-repair check does not read it as switched back on outside PoolWatch
    /// (<see cref="ServicePlan.KeptRunning"/>). Only while the record there is still the one saved for this change; a save
    /// that fails is logged (the check may then warn about it).
    /// </summary>
    private void MarkNotStopped(ServiceChange saved, int pid)
    {
        try { Edit(now => now.Any(x => SameSave(x, saved)) ? now.Select(x => SameSave(x, saved) ? x with { NotStoppedPid = pid } : x).ToList() : null); }
        catch (Exception ex) { _log.Info($"service-changes.json: {saved.Service} did not stop, and that was not saved: {ex.Message}"); }
    }

    /// <summary>
    /// The service was seen stopped, in another process or at another start type since its change could not stop it: its
    /// record no longer says so, and from now on it is read as any other.
    /// </summary>
    private void Unmark(ServiceChange c)
    {
        try { Edit(now => now.Any(x => SameSave(x, c) && x.NotStoppedPid is not null) ? now.Select(x => SameSave(x, c) ? x with { NotStoppedPid = null } : x).ToList() : null); }
        catch (Exception ex) { _log.Info($"service-changes.json: {c.Service} is no longer as its change left it, but that was not saved: {ex.Message}"); }
    }

    /// <summary>Restores one service (or all) to its saved original start type. Every record is re-gated (S1).</summary>
    public List<(string service, string? error)> Restore(string? service) => Restore(service, out _);

    /// <param name="putBack">The records whose start type this Restore put back (their records are gone).</param>
    /// <param name="thenHeld">Run with those records before the operation lock is let go (taking services out of the profile
    /// applied at start), so the run at start never reads the profile between the restore and that edit; what it returns is
    /// added to the results.</param>
    public List<(string service, string? error)> Restore(string? service, out List<ServiceChange> putBack,
        Func<IReadOnlyList<ServiceChange>, IEnumerable<(string service, string? error)>>? thenHeld = null)
    {
        List<(string service, string? error)> results;
        lock (_opLock)
        {
            results = RestoreNow(service, putBack = []);
            if (thenHeld is not null) results.AddRange(thenHeld(putBack));
        }
        TellTurnedOn(putBack.Where(c => ServicePlan.OnAfterRestore(c, Catalogue.Find(c.Service))).Select(c => c.Service).ToList());
        return results;
    }

    /// <summary>
    /// Services a Restore (or an Undo, which is one) or "Restore to Windows default" turned on, on the thread that did it:
    /// the Services tab unticks them, so its next Apply does not turn them off again.
    /// </summary>
    public event Action<IReadOnlyList<string>>? TurnedOn;

    private void TellTurnedOn(IReadOnlyList<string> services)
    {
        if (services.Count == 0) return;
        try { TurnedOn?.Invoke(services); }
        catch (Exception ex) { _log.Info("services: the Services tab was not told what was turned on: " + ex.Message); }
    }

    private List<(string service, string? error)> RestoreNow(string? service, List<ServiceChange> putBack)
    {
        var options = Options();
        var byName = options.ToDictionary(o => o.Service.Name, StringComparer.OrdinalIgnoreCase);
        bool insider = _backend.InsiderEnrolled();
        var allowed = Allowed(options);
        List<ServiceChange> todo;
        try { lock (_lock) todo = Read().Where(c => service is null || Is(c, service)).ToList(); }
        catch (Exception ex)
        {
            string why = "PoolWatch cannot read how the services were set: " + ex.Message;
            _log.Info("Services not restored: " + why);
            return [(service ?? "(services)", why)];
        }
        // One that another of them depends on is put back first: Windows does not start a service while one it needs is Disabled.
        todo = ServicePlan.InDependencyOrder(todo, c => c.Service, DependsOn(Views(options.Select(o => o.Service))), dependentsFirst: false);
        var backOn = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // put back by this run to a start type that is not Disabled
        var results = new List<(string, string?)>();
        foreach (var c in todo)
        {
            // S1: the record from service-changes.json is untrusted. Re-validate it through the same gate as Apply,
            // against the service as it is live now, before changing anything.
            string? err = null, after = null;
            NotDone? notDone = null;
            bool already = false;
            lock (_lock) _restoring.Add(c.Service); // the self-repair check must not read PoolWatch's own undo as "outside"
            try
            {
                if (!byName.TryGetValue(c.Service, out var live)) err = "no longer installed";
                else
                {
                    ServicePlan.GateRestore(live, c, insider);
                    if (ServicePlan.AlreadyRestored(live, c)) already = true; // back to the original already: only the record goes
                    else (err, notDone) = _backend.Restore(c, allowed, insider);
                    if (err is null && notDone is not null) after = AfterPutBack(c, notDone, NeededDisabled(live, byName, backOn));
                }
            }
            catch (Exception ex) { err = ex.Message; }
            // The start type is back (even if starting it again failed): the record and the savings measure go.
            if (err is null)
            {
                Forget(c);
                putBack.Add(c);
                if (c.OriginalStart != ServiceStartType.Disabled) backOn.Add(c.Service);
            }
            lock (_lock) _restoring.Remove(c.Service);
            string? shown = err ?? (after is null ? null : $"the start type is back to {c.OriginalStart}, but {after}");
            results.Add((c.Service, shown));
            string msg = err is not null ? $"Service not put back: {c.Service}: {err}"
                : already ? $"Service put back: {c.Service} was already back to {c.OriginalStart}; PoolWatch's record of the change is removed"
                : after is not null ? $"Service put back: {c.Service} -> {c.OriginalStart}, but {after}"
                : $"Service put back: {c.Service} -> {c.OriginalStart}{(ServicePlan.StartAfterRestore(c) ? ", started again (it was running before)" : "")}";
            _finding(Finding(msg, err is null && after is null ? FindingSeverity.Info : FindingSeverity.Warning, c.Service));
            _log.Info(msg);
        }
        return results;
    }

    /// <summary>
    /// What did not happen once the start type was back: starting it again (a service it needs that is still Disabled is
    /// named, <paramref name="neededDisabled"/>), or for an original of Disabled (putting back your own setting after "Set
    /// to its Windows default"), stopping it, which happens when Windows restarts.
    /// </summary>
    private string AfterPutBack(ServiceChange c, NotDone why, string? neededDisabled) =>
        c.OriginalStart == ServiceStartType.Disabled
            ? ServicePlan.StillRunningAfterPutBack(why)
            : ServicePlan.NotStartedText(c.Service, why, neededDisabled, neededDisabled is not null && CanPutBackOn(neededDisabled));

    /// <summary>PoolWatch holds a change of <paramref name="service"/> whose Put back leaves it at a start type that is not Disabled.</summary>
    private bool CanPutBackOn(string service)
    {
        lock (_lock) return _changes.Any(x => Is(x, service) && x.OriginalStart != ServiceStartType.Disabled);
    }

    /// <summary>
    /// The first service <paramref name="live"/> needs that was Disabled when the services were read and that this run has
    /// not put back to another start type since (<paramref name="backOn"/>), or null.
    /// </summary>
    private static string? NeededDisabled(ServiceOption live, Dictionary<string, ServiceOption> byName, IReadOnlySet<string> backOn) =>
        live.Service.DependsOn.FirstOrDefault(d => byName.TryGetValue(d, out var o) && o.Service.StartType == ServiceStartType.Disabled && !backOn.Contains(d));

    /// <summary>
    /// <paramref name="items"/> in the order Put back runs them: one that another depends on first (Windows does not start
    /// a service while one it needs is Disabled), else as given. Reads the live services (as given when they cannot be
    /// read) and changes nothing.
    /// </summary>
    /// <param name="serviceOf">The service an item is about.</param>
    public List<T> PutBackOrder<T>(IReadOnlyList<T> items, Func<T, string> serviceOf)
    {
        if (items.Count < 2) return items.ToList();
        Dictionary<string, ServiceView> views;
        try { views = Views(_backend.Enumerate()); }
        catch (Exception ex) { _log.Info("services: put back in the order listed, as the services could not be read: " + ex.Message); return items.ToList(); }
        return ServicePlan.InDependencyOrder(items, serviceOf, DependsOn(views), dependentsFirst: false);
    }

    /// <summary>Services whose Restore is in progress (the self-repair check skips them).</summary>
    private readonly HashSet<string> _restoring = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Services whose Turn off is under way, from the save of the record until its mark is saved (the self-repair check
    /// skips them, as it skips <see cref="_restoring"/>).
    /// </summary>
    private readonly HashSet<string> _applying = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// "Restore to Windows default" for a service that is off outside PoolWatch (the owner's own choice, or another tool's):
    /// the SAME gate as Apply (<see cref="ServicePlan.BuildDefault"/>, then the live gate), with the current setting saved
    /// first so Restore can put the owner's choice back.
    /// </summary>
    /// <param name="thenHeld">Run only when the service was set to its default, with the service as it was read, before the
    /// operation lock is let go (as for Restore's); what it returns is added to the results.</param>
    public List<(string service, string? error)> RestoreToDefault(string service, Func<ServiceOption, IEnumerable<(string service, string? error)>>? thenHeld = null)
    {
        List<(string service, string? error)> results;
        ServiceOption? turnedOn;
        lock (_opLock)
        {
            results = RestoreToDefaultNow(service, out turnedOn);
            if (turnedOn is { } set && thenHeld is not null) results.AddRange(thenHeld(set));
        }
        if (turnedOn is { } o && ServicePlan.OnAfterDefault(o)) TellTurnedOn([o.Service.Name]);
        return results;
    }

    /// <param name="setDefault">The service as it was read, when it was set to its default.</param>
    private List<(string service, string? error)> RestoreToDefaultNow(string service, out ServiceOption? setDefault)
    {
        setDefault = null;
        var options = Options();
        bool insider = _backend.InsiderEnrolled();
        var allowed = Allowed(options);
        ServicePlan.Operation op;
        try { op = ServicePlan.BuildDefault(service, options, insider, DateTime.UtcNow); }
        catch (Exception ex) { _log.Info("services: restore to default refused: " + ex.Message); return [(service, ex.Message)]; }
        // The current setting is saved first, as the file holds it now; when it cannot be saved the service is not changed.
        bool changedBefore = false;
        string? err = null;
        try
        {
            Edit(now =>
            {
                if (changedBefore = now.Any(x => Is(x, op.Option.Service.Name))) return null;
                return ServiceChangeLog.Upsert(now, op.Record);
            });
        }
        catch (Exception ex) { err = NotSaved(ex); }
        if (changedBefore) return [(service, "PoolWatch already changed this service: use Put back")];
        if (err is null)
        {
            err = _backend.SetDefault(op, allowed, insider);
            if (err is not null) TakeBack(op.Record, prior: null);
        }
        string msg = err is null
            ? $"Service put back to its Windows default: {op.Record.Summary()} (Put back returns it to your own setting)"
            : $"Service not put back to its Windows default: {op.Option.Service.Name}: {err}";
        _finding(Finding(msg, err is null ? FindingSeverity.Info : FindingSeverity.Warning, op.Option.Service.Name));
        _log.Info(msg);
        if (err is null) setDefault = op.Option;
        return [(op.Option.Service.Name, err)];
    }

    /// <summary>A service's cost just before a change: its share of its process's recent average (zero when not running).</summary>
    private (double cores, long faultsPerSec) BeforeCost(ServiceView s)
    {
        if (!s.Running || s.ProcessId <= 0 || _recentCost(s.ProcessId) is not { } host) return (0, 0);
        return ServiceCosts.Share(host, 1 + s.SharedWith);
    }

    /// <summary>
    /// Sampler thread, every few minutes: did something switch a service the owner turned off back on? Not one that has
    /// kept running since PoolWatch's own change set it to Disabled but could not stop it (<see cref="ServicePlan.KeptRunning"/>):
    /// once it is seen stopped (STOPPED: not starting, stopping, resuming, pausing or paused), in another process or at
    /// another start type, it is read as any other. Not one whose Turn off or Put back is under way.
    /// </summary>
    public void CheckSelfRepair(DateTime nowUtc)
    {
        if (nowUtc < _nextSelfRepairCheck) return;
        _nextSelfRepairCheck = nowUtc.AddMinutes(5);
        List<ServiceChange> changed;
        lock (_lock) changed = _changes.Where(c => c.NewStart == ServiceStartType.Disabled && !_restoring.Contains(c.Service) && !_applying.Contains(c.Service)).ToList();
        if (changed.Count == 0) return;
        var live = _backend.Enumerate().ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var c in changed)
            if (live.TryGetValue(c.Service, out var s) && (s.StartType != ServiceStartType.Disabled || s.Running) && !BeingChanged(c.Service) && StillRecorded(c))
            {
                if (ServicePlan.KeptRunning(c, s)) continue; // as PoolWatch's change left it: not switched back on
                if (c.NotStoppedPid is not null) Unmark(c); // another process or start type: switched back on like any other
                // Once per state it is switched back to (not every 5 min while it stays so).
                lock (_lock)
                    if (_repairWarned.TryGetValue(c.Service, out var seen) && seen == (s.StartType, s.Running)) continue;
                    else _repairWarned[c.Service] = (s.StartType, s.Running);
                string next = s.StartType == c.OriginalStart
                    ? "Turn it off again with Turn off, or keep it this way with Put back"
                    : "Turn it off again with Turn off; Put back works again after that";
                // The profile applied at start is named only when it would turn the service off as it is now.
                string? profile = ServicePlan.OnAt(c.Service, s.StartType, s.Running, Catalogue.Find(c.Service)) ? AtStartProfileHolding(c.Service) : null;
                string msg = ServicePlan.SelfRepairText(c.Service, s.StartType, s.Running, next, profile);
                _finding(Finding(msg, FindingSeverity.Warning, c.Service));
                _log.Info(msg);
            }
            else
            {
                // Seen stopped: if it runs again, that is a switch-on like any other. Not yet while it is starting, stopping,
                // resuming, pausing or paused: one of those may still run on as PoolWatch's change left it.
                if (c.NotStoppedPid is not null && s is { StartType: ServiceStartType.Disabled, Running: false, NeitherRunningNorStopped: false }
                    && !BeingChanged(c.Service) && StillRecorded(c)) Unmark(c);
                lock (_lock) _repairWarned.Remove(c.Service); // off again: a later switch-on is reported again
            }
    }

    /// <summary>
    /// The state each re-enabled service was last reported in (self-repair warns once per state, not every check). A
    /// service's entry goes once the check finds it Disabled and not running again, and when Turn off saves its record.
    /// </summary>
    private readonly Dictionary<string, (ServiceStartType start, bool running)> _repairWarned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A Put back or a Turn off of <paramref name="service"/> is under way.</summary>
    private bool BeingChanged(string service) { lock (_lock) return _restoring.Contains(service) || _applying.Contains(service); }

    /// <summary>The profile applied at start when it lists <paramref name="service"/>, else null.</summary>
    private string? AtStartProfileHolding(string service)
    {
        try { return Profiles?.Invoke() is { } set ? ServiceProfileRules.AtStartHolding(set, [service]).Profile : null; }
        catch (Exception) { return null; }
    }

    /// <summary>The record is still the live one (a Restore that finished while the services were read has removed it).</summary>
    private bool StillRecorded(ServiceChange c) { lock (_lock) return _changes.Contains(c); }

    private static Finding Finding(string title, FindingSeverity severity, string service) => new()
    {
        TimeUtc = DateTime.UtcNow, Kind = FindingKind.ServiceChange, Severity = severity, Title = title, Image = service,
        Data = new Dictionary<string, object?> { ["service"] = service },
    };

    private static bool Is(ServiceChange c, string service) => c.Service.Equals(service, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="c"/> is the record <paramref name="saved"/> as it was saved (by service, time and caller),
    /// not one saved over it since.
    /// </summary>
    private static bool SameSave(ServiceChange c, ServiceChange saved) => Is(c, saved.Service) && c.ChangedUtc == saved.ChangedUtc && c.Via == saved.Via;

    /// <summary>Why a service was left as it was: its record could not be saved first.</summary>
    private static string NotSaved(Exception ex) => "not changed, because PoolWatch could not save how it was set: " + ex.Message;

    /// <summary>
    /// A change that did not happen leaves no record, and a record from before it is put back; only while the record
    /// there is still the one saved for it. A failure is logged: the service was not changed, and a later Restore finds it
    /// already back and removes the record.
    /// </summary>
    private void TakeBack(ServiceChange saved, ServiceChange? prior)
    {
        try
        {
            Edit(now => !now.Any(x => SameSave(x, saved)) ? null
                : prior is null ? now.Where(x => !Is(x, saved.Service)).ToList() : ServiceChangeLog.Upsert(now, prior));
        }
        catch (Exception ex) { _log.Info($"service-changes.json: {saved.Service} was not changed, but its record was not taken back: {ex.Message}"); }
    }

    /// <summary>
    /// After a Restore the service is back: its record goes with its savings measure, unless another PoolWatch (the
    /// command line) saved a newer one meanwhile. A save that fails is logged, and the record is left out of every read
    /// while this PoolWatch runs, until a later save leaves it out of the file; the service is back either way.
    /// </summary>
    private void Forget(ServiceChange restored)
    {
        lock (_lock)
        {
            _savings.Stop(restored.Service);
            try { Edit(now => now.Any(x => SameSave(x, restored)) ? now.Where(x => !SameSave(x, restored)).ToList() : null); }
            catch (Exception ex)
            {
                _restoredNotRemoved.Add(restored);
                _changes = _changes.Where(x => !SameSave(x, restored)).ToList();
                _log.Info($"service-changes.json: {restored.Service} is restored, but its record was not removed: {ex.Message}");
            }
        }
    }

    /// <summary>At start: the records for the page. A file that cannot be read is logged here and refused where it is acted on.</summary>
    private void Load()
    {
        try { lock (_lock) Read(); }
        catch (Exception ex) { _log.Info("service-changes.json not read: " + ex.Message); }
    }

    /// <summary>service-changes.json is read only up to this many bytes, and never saved past it.</summary>
    internal const int MaxRecordBytes = 1 << 20;

    /// <summary>
    /// Reads the file as it is now, edits that list and saves it (an edit that returns null changes nothing). Throws when
    /// the file cannot be read or saved, or would pass its bound; nothing changes then.
    /// </summary>
    private void Edit(Func<List<ServiceChange>, List<ServiceChange>?> edit)
    {
        lock (_lock)
        {
            if (edit(Read()) is not { } next) return;
            string json = ServiceChangeLog.ToJson(next);
            if (Encoding.UTF8.GetByteCount(json) > MaxRecordBytes)
                throw new InvalidOperationException($"service-changes.json would be over {MaxRecordBytes >> 20} MB");
            DurableFile.Replace(_paths.Guard, ChangeFile, json);
            _changes = next;
            _version++;
            _restoredNotRemoved.Clear(); // the list just saved was read without them
        }
    }

    /// <summary>
    /// The records in the file now (none when there is no file), without those a Restore put back whose removal is not
    /// saved yet. Throws when it is there but cannot be read: a read error, a link, or a file that is not a change log
    /// PoolWatch writes (damaged, cut off, empty); <see cref="RecordError"/> then says why. Under the lock.
    /// </summary>
    private List<ServiceChange> Read()
    {
        List<ServiceChange> read;
        try { read = ReadFile(); }
        catch (Exception ex) { _recordError = ex.Message; throw; }
        _recordError = null;
        return _changes = read.Where(c => !_restoredNotRemoved.Any(r => SameSave(c, r))).ToList();
    }

    private List<ServiceChange> ReadFile()
    {
        string file = ChangeFile, json;
        // One handle on the file itself (never what a link there points at) and a bound on the bytes read, as for the list
        // of changes (ChangeRecords.ReadPlainFile).
        try
        {
            json = ChangeRecords.ReadPlainFile(_paths.DataRoot, file, MaxRecordBytes) ?? throw new InvalidDataException(
                $"{file} is over {MaxRecordBytes >> 20} MB, more than PoolWatch writes. The file is left as it is: restore it from a backup, " +
                "or delete it to start a new record (the services in it can then no longer be put back from PoolWatch).");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return []; }
        // Worded to follow what it stops ("PoolWatch cannot read how the services were set: ..."), as a read error's own text does.
        return ServiceChangeLog.Parse(json) ?? throw new InvalidDataException(
            $"{file} is damaged or empty. The file is left as it is: restore it from a backup, or delete it to start a new " +
            "record (the services in it can then no longer be put back from PoolWatch).");
    }

    /// <summary>Writes the enumerated services (headless --services-dump) for CI and scripts.</summary>
    public void Dump(string file)
    {
        var options = Options();
        bool insider = _backend.InsiderEnrolled();
        var payload = new
        {
            insiderEnrolled = insider,
            services = options.Select(o => new
            {
                name = o.Service.Name, display = o.Service.DisplayName, group = o.Group.ToString(),
                start = o.Service.StartType.ToString(), running = o.Service.Running, microsoft = o.Service.MicrosoftSigned,
                publisher = o.Service.Publisher, path = o.Service.BinaryPath, catalogue = o.Catalogue?.Name,
                category = o.Catalogue?.Category, risk = o.Catalogue?.Risk.ToString(), preset = o.Catalogue?.AgenticPreset,
                protectedReason = o.ProtectedReason, offOutside = o.OffOutsidePoolWatch, state = ServicePlan.StateText(o),
                cost = ServicePlan.CostText(o.Service), pid = o.Service.ProcessId, windowsDefault = o.WindowsDefault?.ToString(),
                canRestoreDefault = o.CanRestoreDefault,
            }),
        };
        File.WriteAllText(_paths.Guard.CheckReal(file), JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>
/// The live services behind the page: reading them, and the gated change (<see cref="ServiceControl"/>, whose own live gate
/// runs after <see cref="ServicePlan"/>'s). An interface so the controller's record-keeping is tested without touching a
/// real service (review round 2, item 12).
/// </summary>
internal interface IServiceBackend
{
    IReadOnlyList<ServiceView> Enumerate();
    bool InsiderEnrolled();
    /// <summary>
    /// <c>error</c>: not changed. <c>notStopped</c>: the start type is set, but the service is not known to have stopped,
    /// and why (as <see cref="ServiceControl.Apply"/>).
    /// </summary>
    (string? error, NotDone? notStopped) Apply(ServicePlan.Operation op, IReadOnlySet<string> allowed, bool insiderEnrolled);
    /// <summary>
    /// <c>error</c>: not put back. <c>notDone</c>: the start type is back, but starting it again, or for an original of
    /// Disabled stopping it, did not happen, and why (as <see cref="ServiceControl.Restore"/>).
    /// </summary>
    (string? error, NotDone? notDone) Restore(ServiceChange change, IReadOnlySet<string> allowed, bool insiderEnrolled);
    string? SetDefault(ServicePlan.Operation op, IReadOnlySet<string> allowed, bool insiderEnrolled);
}

/// <summary>The real backend: the Windows service control manager.</summary>
internal sealed class WindowsServiceBackend : IServiceBackend
{
    public IReadOnlyList<ServiceView> Enumerate() => ServiceControl.Enumerate();
    public bool InsiderEnrolled() => ServicesController.InsiderEnrolled();
    public (string? error, NotDone? notStopped) Apply(ServicePlan.Operation op, IReadOnlySet<string> allowed, bool insiderEnrolled) => ServiceControl.Apply(op, allowed, insiderEnrolled);
    public (string? error, NotDone? notDone) Restore(ServiceChange change, IReadOnlySet<string> allowed, bool insiderEnrolled) => ServiceControl.Restore(change, allowed, insiderEnrolled);
    public string? SetDefault(ServicePlan.Operation op, IReadOnlySet<string> allowed, bool insiderEnrolled) => ServiceControl.SetDefault(op, allowed, insiderEnrolled);
}
