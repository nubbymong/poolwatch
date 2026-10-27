using System.Text.Json;
using Microsoft.Win32;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Services;

namespace PoolWatch.Engine;

/// <summary>
/// The background-services page's engine side (addendum 4 B): enumerate the installed services, classify them against
/// the catalogue and the protected gate, apply and restore changes through the gated <see cref="ServiceControl"/>, and
/// keep the change log (service-changes.json) so Restore puts back exactly the original. Every change (Apply, Restore,
/// Restore to Windows default) goes through <see cref="ServicePlan"/>'s gate and then the live gate; there is no other
/// path. Watches for self-repair (a service the owner turned off that is switched back on by something else) and measures
/// what a change saved over the hour after it.
/// </summary>
internal sealed class ServicesController
{
    private readonly AppPaths _paths;
    private readonly Log _log;
    private readonly Action<Finding> _finding;
    private readonly object _lock = new();
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
    public IReadOnlyList<ServiceChange> Changes { get { lock (_lock) return _changes.ToList(); } }

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

    /// <summary>Turns off the ticked services (gated). Returns each service's result; records the originals first.</summary>
    public List<(string service, string? error)> Apply(IReadOnlyCollection<string> ticked, string via)
    {
        var options = Options();
        bool insider = _backend.InsiderEnrolled();
        var allowed = Allowed(options);
        List<ServicePlan.Operation> ops;
        try { ops = ServicePlan.BuildApply(ticked, options, insider, via, DateTime.UtcNow); }
        catch (Exception ex) { _log.Info("services: apply refused: " + ex.Message); return [("(plan)", ex.Message)]; }
        var results = new List<(string, string?)>();
        foreach (var op in ops)
        {
            // S1: keep the FIRST saved original if PoolWatch already changed this service (a re-Apply must not record
            // the already-off state as the "original"). The record is saved BEFORE the change so Restore works even if
            // PoolWatch dies mid-apply; if the change then fails and there was no prior record, it is rolled back.
            ServiceChange? prior;
            lock (_lock) prior = _changes.FirstOrDefault(x => x.Service.Equals(op.Option.Service.Name, StringComparison.OrdinalIgnoreCase));
            // What its process cost just before (its share of a shared host), for "saved about N cores" afterwards.
            var before = BeforeCost(op.Option.Service);
            var record = (prior is null ? op.Record
                : op.Record with { OriginalStart = prior.OriginalStart, OriginalRunning = prior.OriginalRunning })
                with { BeforeCores = before.cores, BeforePageFaultsPerSec = before.faultsPerSec };
            lock (_lock) { _changes = ServiceChangeLog.Upsert(_changes, record); Save(); }
            string? err = _backend.Apply(op, allowed, insider);
            lock (_lock)
            {
                if (err is not null)
                {
                    // Leave no record for a change that did not happen (keep a pre-existing one untouched).
                    _changes = prior is null
                        ? _changes.Where(x => !x.Service.Equals(op.Option.Service.Name, StringComparison.OrdinalIgnoreCase)).ToList()
                        : ServiceChangeLog.Upsert(_changes, prior);
                    Save();
                }
                else _savings.Start(op.Option.Service.Name, DateTime.UtcNow, before.cores, before.faultsPerSec);
            }
            results.Add((op.Option.Service.Name, err));
            string msg = err is null
                ? $"Service turned off: {record.Summary()}; before: {ServicePlan.Cost(before.cores, before.faultsPerSec)}"
                : $"Service not turned off: {op.Option.Service.Name}: {err}";
            _finding(Finding(msg, err is null ? FindingSeverity.Info : FindingSeverity.Warning, op.Option.Service.Name));
            _log.Info(msg);
        }
        return results;
    }

    /// <summary>Restores one service (or all) to its saved original start type. Every record is re-gated (S1).</summary>
    public List<(string service, string? error)> Restore(string? service)
    {
        var options = Options();
        var byName = options.ToDictionary(o => o.Service.Name, StringComparer.OrdinalIgnoreCase);
        bool insider = _backend.InsiderEnrolled();
        var allowed = Allowed(options);
        List<ServiceChange> todo;
        lock (_lock) todo = _changes.Where(c => service is null || c.Service.Equals(service, StringComparison.OrdinalIgnoreCase)).ToList();
        var results = new List<(string, string?)>();
        foreach (var c in todo)
        {
            // S1: the record from service-changes.json is untrusted. Re-validate it through the same gate as Apply,
            // against the service as it is live now, before changing anything.
            string? err = null, startErr = null;
            bool already = false;
            lock (_lock) _restoring.Add(c.Service); // the self-repair check must not read PoolWatch's own undo as "outside"
            try
            {
                if (!byName.TryGetValue(c.Service, out var live)) err = "no longer installed";
                else
                {
                    ServicePlan.GateRestore(live, c, insider);
                    if (ServicePlan.AlreadyRestored(live, c)) already = true; // back to the original already: only the record goes
                    else (err, startErr) = _backend.Restore(c, allowed, insider);
                }
            }
            catch (Exception ex) { err = ex.Message; }
            // The start type is back (even if starting it again failed): the record and the savings measure go.
            if (err is null)
                lock (_lock)
                {
                    _changes = _changes.Where(x => !x.Service.Equals(c.Service, StringComparison.OrdinalIgnoreCase)).ToList();
                    _savings.Stop(c.Service);
                    Save();
                }
            lock (_lock) _restoring.Remove(c.Service);
            string? shown = err ?? (startErr is null ? null : $"the start type is back to {c.OriginalStart}, but starting it again failed: {startErr}");
            results.Add((c.Service, shown));
            string msg = err is not null ? $"Service not restored: {c.Service}: {err}"
                : already ? $"Service restored: {c.Service} was already back to {c.OriginalStart}; PoolWatch's record of the change is removed"
                : startErr is not null ? $"Service restored: {c.Service} -> {c.OriginalStart}, but starting it again failed: {startErr}"
                : $"Service restored: {c.Service} -> {c.OriginalStart}{(ServicePlan.StartAfterRestore(c) ? ", started again (it was running before)" : "")}";
            _finding(Finding(msg, err is null && startErr is null ? FindingSeverity.Info : FindingSeverity.Warning, c.Service));
            _log.Info(msg);
        }
        return results;
    }

    /// <summary>Services whose Restore is in progress (the self-repair check skips them).</summary>
    private readonly HashSet<string> _restoring = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// "Restore to Windows default" for a service that is off outside PoolWatch (the owner's own choice, or another tool's):
    /// the SAME gate as Apply (<see cref="ServicePlan.BuildDefault"/>, then the live gate), with the current setting saved
    /// first so Restore can put the owner's choice back.
    /// </summary>
    public List<(string service, string? error)> RestoreToDefault(string service)
    {
        var options = Options();
        bool insider = _backend.InsiderEnrolled();
        var allowed = Allowed(options);
        ServicePlan.Operation op;
        try { op = ServicePlan.BuildDefault(service, options, insider, DateTime.UtcNow); }
        catch (Exception ex) { _log.Info("services: restore to default refused: " + ex.Message); return [(service, ex.Message)]; }
        lock (_lock)
        {
            if (_changes.Any(x => x.Service.Equals(op.Option.Service.Name, StringComparison.OrdinalIgnoreCase)))
                return [(service, "PoolWatch already changed this service: use Restore")];
            _changes = ServiceChangeLog.Upsert(_changes, op.Record);
            Save();
        }
        string? err = _backend.SetDefault(op, allowed, insider);
        if (err is not null)
            lock (_lock)
            {
                _changes = _changes.Where(x => !x.Service.Equals(op.Option.Service.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                Save();
            }
        string msg = err is null
            ? $"Service put back to its Windows default: {op.Record.Summary()} (Restore puts your setting back)"
            : $"Service not put back to its Windows default: {op.Option.Service.Name}: {err}";
        _finding(Finding(msg, err is null ? FindingSeverity.Info : FindingSeverity.Warning, op.Option.Service.Name));
        _log.Info(msg);
        return [(op.Option.Service.Name, err)];
    }

    /// <summary>A service's cost just before a change: its share of its process's recent average (zero when not running).</summary>
    private (double cores, long faultsPerSec) BeforeCost(ServiceView s)
    {
        if (!s.Running || s.ProcessId <= 0 || _recentCost(s.ProcessId) is not { } host) return (0, 0);
        return ServiceCosts.Share(host, 1 + s.SharedWith);
    }

    /// <summary>Sampler thread, every few minutes: did something switch a service the owner turned off back on?</summary>
    public void CheckSelfRepair(DateTime nowUtc)
    {
        if (nowUtc < _nextSelfRepairCheck) return;
        _nextSelfRepairCheck = nowUtc.AddMinutes(5);
        List<ServiceChange> changed;
        lock (_lock) changed = _changes.Where(c => c.NewStart == ServiceStartType.Disabled && !_restoring.Contains(c.Service)).ToList();
        if (changed.Count == 0) return;
        var live = _backend.Enumerate().ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var c in changed)
            if (live.TryGetValue(c.Service, out var s) && (s.StartType != ServiceStartType.Disabled || s.Running) && !IsRestoring(c.Service) && StillRecorded(c))
            {
                // Once per state it is switched back to (not every 5 min while it stays so).
                lock (_lock)
                    if (_repairWarned.TryGetValue(c.Service, out var seen) && seen == (s.StartType, s.Running)) continue;
                    else _repairWarned[c.Service] = (s.StartType, s.Running);
                string next = s.StartType == c.OriginalStart
                    ? "Turn it off again (Apply), or Restore to keep it this way"
                    : "Turn it off again (Apply); Restore works again after that";
                string msg = $"Service re-enabled outside PoolWatch: {c.Service} is {s.StartType}{(s.Running ? ", running" : "")} again (you turned it off). {next} on the Background services page.";
                _finding(Finding(msg, FindingSeverity.Warning, c.Service));
                _log.Info(msg);
            }
            else lock (_lock) _repairWarned.Remove(c.Service); // off again: a later switch-on is reported again
    }

    /// <summary>The state each re-enabled service was last reported in (self-repair warns once per state, not every check).</summary>
    private readonly Dictionary<string, (ServiceStartType start, bool running)> _repairWarned = new(StringComparer.OrdinalIgnoreCase);

    private bool IsRestoring(string service) { lock (_lock) return _restoring.Contains(service); }

    /// <summary>The record is still the live one (a Restore that finished while the services were read has removed it).</summary>
    private bool StillRecorded(ServiceChange c) { lock (_lock) return _changes.Contains(c); }

    private static Finding Finding(string title, FindingSeverity severity, string service) => new()
    {
        TimeUtc = DateTime.UtcNow, Kind = FindingKind.ServiceChange, Severity = severity, Title = title, Image = service,
        Data = new Dictionary<string, object?> { ["service"] = service },
    };

    private void Load()
    {
        try { if (File.Exists(ChangeFile)) _changes = ServiceChangeLog.Parse(File.ReadAllText(ChangeFile)); }
        catch (Exception ex) { _log.Info("service-changes.json not read: " + ex.Message); }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_paths.Guard.CheckReal(ChangeFile + ".tmp"), ServiceChangeLog.ToJson(_changes));
            File.Move(_paths.Guard.CheckReal(ChangeFile + ".tmp"), ChangeFile, overwrite: true);
        }
        catch (Exception ex) { _log.Info("service-changes.json not saved: " + ex.Message); }
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
                category = o.Catalogue?.Category, risk = o.Catalogue?.Risk.ToString(), preset = o.Catalogue?.DevPreset,
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
    string? Apply(ServicePlan.Operation op, IReadOnlySet<string> allowed, bool insiderEnrolled);
    (string? error, string? startError) Restore(ServiceChange change, IReadOnlySet<string> allowed, bool insiderEnrolled);
    string? SetDefault(ServicePlan.Operation op, IReadOnlySet<string> allowed, bool insiderEnrolled);
}

/// <summary>The real backend: the Windows service control manager.</summary>
internal sealed class WindowsServiceBackend : IServiceBackend
{
    public IReadOnlyList<ServiceView> Enumerate() => ServiceControl.Enumerate();
    public bool InsiderEnrolled() => ServicesController.InsiderEnrolled();
    public string? Apply(ServicePlan.Operation op, IReadOnlySet<string> allowed, bool insiderEnrolled) => ServiceControl.Apply(op, allowed, insiderEnrolled);
    public (string? error, string? startError) Restore(ServiceChange change, IReadOnlySet<string> allowed, bool insiderEnrolled) => ServiceControl.Restore(change, allowed, insiderEnrolled);
    public string? SetDefault(ServicePlan.Operation op, IReadOnlySet<string> allowed, bool insiderEnrolled) => ServiceControl.SetDefault(op, allowed, insiderEnrolled);
}
