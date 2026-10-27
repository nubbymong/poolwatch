using PoolWatch.Core.Findings;
using PoolWatch.Core.Services;
using PoolWatch.Engine;

namespace PoolWatch.AppTests;

/// <summary>
/// S1 in the app layer (review round 2, item 12): the services page's record-keeping and its gate, with a fake service
/// backend (no real service is read or changed) and the data root in TEMP (no ACL is touched).
/// </summary>
public sealed class ServicesControllerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pw-svc-" + Guid.NewGuid().ToString("N"));
    private readonly List<Finding> _findings = new();

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>The live services, as the fake service control manager has them; every change call is recorded.</summary>
    private sealed class FakeServices : IServiceBackend
    {
        public readonly Dictionary<string, ServiceView> Live = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Calls = new();
        public string? ApplyError;

        public void Add(string name, ServiceStartType start, bool running) =>
            Live[name] = new ServiceView { Name = name, DisplayName = name, StartType = start, Running = running, MicrosoftSigned = true };

        public IReadOnlyList<ServiceView> Enumerate() => Live.Values.ToList();
        public bool InsiderEnrolled() => false;

        public string? Apply(ServicePlan.Operation op, IReadOnlySet<string> allowed, bool insiderEnrolled)
        {
            Calls.Add("apply " + op.Option.Service.Name);
            if (ApplyError is { } e) return e;
            Live[op.Option.Service.Name] = Live[op.Option.Service.Name] with { StartType = op.NewStart, Running = false };
            return null;
        }

        public (string? error, string? startError) Restore(ServiceChange change, IReadOnlySet<string> allowed, bool insiderEnrolled)
        {
            Calls.Add("restore " + change.Service);
            Live[change.Service] = Live[change.Service] with { StartType = change.OriginalStart, Running = change.OriginalRunning };
            return (null, null);
        }

        public string? SetDefault(ServicePlan.Operation op, IReadOnlySet<string> allowed, bool insiderEnrolled)
        {
            Calls.Add("default " + op.Option.Service.Name);
            return null;
        }
    }

    private (ServicesController ctl, FakeServices svc, AppPaths paths) New(Action<FakeServices>? setup = null, IEnumerable<ServiceChange>? saved = null)
    {
        var paths = new AppPaths(Path.Combine(_root, "data"), null, secure: false);
        if (saved is not null) File.WriteAllText(Path.Combine(paths.DataRoot, "service-changes.json"), ServiceChangeLog.ToJson(saved));
        var svc = new FakeServices();
        svc.Add("MapsBroker", ServiceStartType.Automatic, running: true); // a catalogue service ("Downloaded Maps Manager")
        setup?.Invoke(svc);
        var log = new Log(Path.Combine(paths.DataRoot, "test.log"));
        return (new ServicesController(paths, log, _findings.Add, _ => null, _ => null, svc), svc, paths);
    }

    private static List<ServiceChange> Saved(AppPaths paths) =>
        ServiceChangeLog.Parse(File.ReadAllText(Path.Combine(paths.DataRoot, "service-changes.json")));

    [Fact]
    public void AReApplyKeepsTheFirstOriginal()
    {
        var (ctl, svc, paths) = New();
        Assert.Null(Assert.Single(ctl.Apply(["MapsBroker"], "test")).error);
        var first = Assert.Single(ctl.Changes);
        Assert.Equal((ServiceStartType.Automatic, true, ServiceStartType.Disabled), (first.OriginalStart, first.OriginalRunning, first.NewStart));

        // Something switches it back on (Manual, running); the owner applies again.
        svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { StartType = ServiceStartType.Manual, Running = true };
        Assert.Null(Assert.Single(ctl.Apply(["MapsBroker"], "test")).error);
        var again = Assert.Single(ctl.Changes);
        Assert.Equal(ServiceStartType.Automatic, again.OriginalStart); // the FIRST original, not the switched-back Manual
        Assert.True(again.OriginalRunning);
        Assert.Equal(ServiceStartType.Automatic, Assert.Single(Saved(paths)).OriginalStart);
    }

    [Fact]
    public void AFailedApplyLeavesNoRecordAndKeepsAPriorOne()
    {
        var (ctl, svc, paths) = New();
        svc.ApplyError = "access denied";
        Assert.Equal("access denied", Assert.Single(ctl.Apply(["MapsBroker"], "test")).error);
        Assert.Empty(ctl.Changes);
        Assert.Empty(Saved(paths)); // rolled back on disk too

        svc.ApplyError = null;
        ctl.Apply(["MapsBroker"], "test");
        var prior = Assert.Single(ctl.Changes);
        svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { StartType = ServiceStartType.Manual, Running = true };
        svc.ApplyError = "access denied";
        Assert.NotNull(Assert.Single(ctl.Apply(["MapsBroker"], "test")).error);
        Assert.Equal(prior, Assert.Single(ctl.Changes));        // the prior record, untouched
        Assert.Equal(prior.OriginalStart, Assert.Single(Saved(paths)).OriginalStart);
    }

    [Fact]
    public void RestoreGoesThroughTheGateBeforeAnythingChanges()
    {
        // Records read back from service-changes.json are untrusted (S1): each is re-gated against the live service first.
        var protectedOne = new ServiceChange { Service = "WinDefend", OriginalStart = ServiceStartType.Automatic, OriginalRunning = true, NewStart = ServiceStartType.Disabled };
        var tampered = new ServiceChange { Service = "Fax", OriginalStart = (ServiceStartType)99, OriginalRunning = false, NewStart = ServiceStartType.Disabled };
        var changedSince = new ServiceChange { Service = "RetailDemo", OriginalStart = ServiceStartType.Manual, OriginalRunning = false, NewStart = ServiceStartType.Disabled };
        var good = new ServiceChange { Service = "MapsBroker", OriginalStart = ServiceStartType.Automatic, OriginalRunning = false, NewStart = ServiceStartType.Disabled };
        var (ctl, svc, _) = New(s =>
        {
            s.Add("WinDefend", ServiceStartType.Disabled, running: false);
            s.Add("Fax", ServiceStartType.Disabled, running: false);
            s.Add("RetailDemo", ServiceStartType.Automatic, running: false); // switched to Automatic since: neither new nor original
            s.Live["MapsBroker"] = s.Live["MapsBroker"] with { StartType = ServiceStartType.Disabled, Running = false };
        }, [protectedOne, tampered, changedSince, good]);

        var results = ctl.Restore(null).ToDictionary(r => r.service, r => r.error);
        Assert.Contains("protected", results["WinDefend"]);
        Assert.Contains("not valid", results["Fax"]);
        Assert.Contains("changed since", results["RetailDemo"]);
        Assert.Null(results["MapsBroker"]);
        Assert.Equal(["restore MapsBroker"], svc.Calls); // the refused records never reached the service control
        Assert.Equal(ServiceStartType.Automatic, svc.Live["MapsBroker"].StartType);
        Assert.Equal(["Fax", "RetailDemo", "WinDefend"], ctl.Changes.Select(c => c.Service).Order().ToArray()); // refused ones stay recorded
    }
}
