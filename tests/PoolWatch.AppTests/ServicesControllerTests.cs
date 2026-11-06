using PoolWatch.Core.Findings;
using PoolWatch.Core.Services;
using PoolWatch.Engine;

namespace PoolWatch.AppTests;

/// <summary>
/// The services page's record-keeping and its gate in the app layer, with a fake service
/// backend (no real service is read or changed) and the data root in TEMP (no ACL is touched).
/// </summary>
public sealed class ServicesControllerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pw-svc-" + Guid.NewGuid().ToString("N"));
    private readonly List<Finding> _findings = new();

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>
    /// The live services, as the fake service control manager has them; every change call is recorded. As Windows does, it
    /// sets the start type but does not stop a service while one that depends on it has not stopped, and does not start one
    /// while a service it needs is Disabled (a stopped one it needs starts with it).
    /// </summary>
    private sealed class FakeServices : IServiceBackend
    {
        public readonly Dictionary<string, ServiceView> Live = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Calls = new();
        public string? ApplyError;
        /// <summary>
        /// Why Windows does not stop a service it is asked to, one that runs or is neither running nor stopped (null: it
        /// stops, unless one that depends on it has not stopped). One not stopped is left as it was, at its new start type.
        /// </summary>
        public NotDone? StopRefused;
        /// <summary>Runs once the service is restored, before the controller records it.</summary>
        public Action? OnRestore;
        /// <summary>Services Windows does not start (error 1067); one that needs such a service does not start either (1068).</summary>
        public readonly HashSet<string> CannotStart = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Runs once, when a change is asked for and before it is made (or fails with <see cref="ApplyError"/>).</summary>
        public Action? OnApply;
        /// <summary>Runs once, when the services are next read, before they are.</summary>
        public Action? OnEnumerate;

        public void Add(string name, ServiceStartType start, bool running, int pid = 0, params string[] dependsOn) =>
            Live[name] = new ServiceView { Name = name, DisplayName = name, StartType = start, Running = running, MicrosoftSigned = true, ProcessId = pid, DependsOn = dependsOn };

        /// <summary>A service of another app (not Microsoft's, not on the catalogue: Turn off sets it to Disabled), running.</summary>
        public void AddThirdParty(string name, int pid, params string[] dependsOn) =>
            Live[name] = new ServiceView { Name = name, DisplayName = name, StartType = ServiceStartType.Automatic, Running = true, ProcessId = pid, DependsOn = dependsOn };

        /// <summary>The service control manager cannot be opened: the read throws, as <see cref="ServiceControl.Enumerate"/> does.</summary>
        public bool Unreadable;
        /// <summary>Windows lists no service at all (no PC has none): an empty list.</summary>
        public bool ListsNone;

        /// <summary>How many times the services were read (the self-repair check reads them once a check).</summary>
        public int Reads;

        public IReadOnlyList<ServiceView> Enumerate()
        {
            Reads++;
            if (OnEnumerate is { } hook) { OnEnumerate = null; hook(); }
            if (Unreadable) throw new InvalidOperationException("the service control manager could not be opened (error 5)");
            return ListsNone ? [] : Live.Values.ToList();
        }

        public bool InsiderEnrolled() => false;

        /// <summary>As Windows answers a stop refused because a service that depends on it has not stopped (error 1051).</summary>
        public static readonly NotDone DependentsRunning = ServiceControl.Answered(1051, lookedAgain: false).Why!;

        public (string? error, NotDone? notStopped, bool? wasRunning) Apply(ServicePlan.Operation op, IReadOnlySet<string> allowed, bool insiderEnrolled)
        {
            string name = op.Option.Service.Name;
            Calls.Add("apply " + name);
            if (OnApply is { } hook) { OnApply = null; hook(); }
            if (ApplyError is { } e) return (e, null, null);
            var live = Live[name];
            NotDone? notStopped = op.NewStart == ServiceStartType.Disabled && (live.Running || live.NeitherRunningNorStopped)
                ? StopRefused ?? (Live.Values.Any(v => (v.Running || v.NeitherRunningNorStopped) && v.DependsOn.Contains(name, StringComparer.OrdinalIgnoreCase))
                    ? DependentsRunning : null)
                : null;
            Live[name] = notStopped is null ? live with { StartType = op.NewStart, Running = false, NeitherRunningNorStopped = false }
                : live with { StartType = op.NewStart };
            return (null, notStopped, SawNothing ? null : SawRunning ?? ServicePlan.WasRunning(live)); // what the change saw, as the service is when it acts
        }

        /// <summary>What the change sees of whether the service runs, when not the service as <see cref="Live"/> has it (it started meanwhile, say).</summary>
        public bool? SawRunning;
        /// <summary>The change could not tell whether the service was running (Windows could not give its state when it looked).</summary>
        public bool SawNothing;

        public (string? error, NotDone? notDone) Restore(ServiceChange change, IReadOnlySet<string> allowed, bool insiderEnrolled)
        {
            Calls.Add("restore " + change.Service);
            if (OnRestoreStart is { } first) { OnRestoreStart = null; first(); }
            if (change.OriginalStart == ServiceStartType.Disabled && Live[change.Service].Running && StopRefused is { } refused)
            {
                // Putting back Disabled stops it too; Windows refuses the stop, the start type is back all the same.
                Live[change.Service] = Live[change.Service] with { StartType = ServiceStartType.Disabled };
                OnRestore?.Invoke();
                return (null, refused);
            }
            // As ServiceControl.Restore: started again only when the record says it was running; an original of Disabled is
            // stopped; else it is left running or not, as it is.
            bool start = ServicePlan.StartAfterRestore(change), running = start;
            NotDone? notStarted = null;
            var needs = Live[change.Service].DependsOn;
            if (running && CannotStart.Contains(change.Service))
                (running, notStarted) = (false, ServiceControl.Started(1067));
            else if (running && needs.Any(d => CannotStart.Contains(d) || Live.TryGetValue(d, out var v) && v.StartType == ServiceStartType.Disabled))
                (running, notStarted) = (false, ServiceControl.Started(1068)); // as Windows answers: one it needs did not start
            else if (running)
                foreach (var d in needs.Where(Live.ContainsKey)) Live[d] = Live[d] with { Running = true };
            var now = Live[change.Service];
            Live[change.Service] = start || change.OriginalStart == ServiceStartType.Disabled
                ? now with { StartType = change.OriginalStart, Running = running, NeitherRunningNorStopped = false }
                : now with { StartType = change.OriginalStart };
            OnRestore?.Invoke();
            return (null, notStarted);
        }

        /// <summary>Runs once, when a Put back reaches the service, before it changes it.</summary>
        public Action? OnRestoreStart;

        public string? SetDefault(ServicePlan.Operation op, IReadOnlySet<string> allowed, bool insiderEnrolled)
        {
            Calls.Add("default " + op.Option.Service.Name);
            return null;
        }
    }

    /// <param name="file">service-changes.json as it is on disk before the controller starts (written as is).</param>
    private (ServicesController ctl, FakeServices svc, AppPaths paths) New(Action<FakeServices>? setup = null, IEnumerable<ServiceChange>? saved = null,
        string? file = null)
    {
        var paths = new AppPaths(Path.Combine(_root, "data"), null, secure: false);
        if (saved is not null) File.WriteAllText(ChangeFile(paths), ServiceChangeLog.ToJson(saved));
        if (file is not null) File.WriteAllText(ChangeFile(paths), file);
        var svc = new FakeServices();
        svc.Add("MapsBroker", ServiceStartType.Automatic, running: true); // a catalogue service ("Downloaded Maps Manager")
        setup?.Invoke(svc);
        return (new ServicesController(paths, LogOf(paths), _findings.Add, _ => null, _ => null, svc, Host()), svc, paths);
    }

    /// <summary>
    /// The clock every PoolWatch in a test reads (it moves only when the test moves it), and the PoolWatch processes that
    /// run, by number and start time.
    /// </summary>
    private sealed class Clock
    {
        public long NowMs = 50_000_000;
        public readonly HashSet<(int, long)> Running = [(Tray, 1)];
        public int ControlWaitSeconds = ServiceControl.DefaultControlWaitSeconds;
    }

    /// <summary>A PoolWatch process (<paramref name="pid"/>, started at 1) on <paramref name="clock"/>.</summary>
    private sealed class FakeHost(Clock clock, int pid) : IServicesHost
    {
        public long NowMs => clock.NowMs;
        public (int Pid, long Started) Self => (pid, 1);
        public bool Runs(int p, long started) => clock.Running.Contains((p, started));
        public int ControlWaitSeconds => clock.ControlWaitSeconds;
    }

    /// <summary>The app's PoolWatch, and one run from the command line while it runs.</summary>
    private const int Tray = 100, CommandLine = 200;

    private readonly Clock _clock = new();

    private FakeHost Host(int pid = Tray) => new(_clock, pid);

    /// <summary>A self-repair check by <paramref name="ctl"/>, the next one it makes (the clock moves on 6 minutes first).</summary>
    private void Check(ServicesController ctl)
    {
        _clock.NowMs += 6 * 60 * 1000;
        ctl.CheckSelfRepair();
    }

    private static Log LogOf(AppPaths paths) => new(Path.Combine(paths.DataRoot, "test.log"));

    private static string ChangeFile(AppPaths paths) => Path.Combine(paths.DataRoot, "service-changes.json");

    private static List<ServiceChange> Saved(AppPaths paths) => ServiceChangeLog.Parse(File.ReadAllText(ChangeFile(paths)))!;

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
    public void AServiceThatIsOffAlreadyIsLeftAsItIsAndNotRecorded()
    {
        // The Edge updater's catalogue entry turns edgeupdatem off to Manual, and it is Manual (as Windows ships it); Fax was
        // Disabled by its owner. Neither changes, so neither is recorded or listed. A Disabled service that runs is stopped: a change.
        var (ctl, svc, paths) = New(s =>
        {
            s.Add("edgeupdatem", ServiceStartType.Manual, running: false);
            s.Add("Fax", ServiceStartType.Disabled, running: false);
            s.Add("RetailDemo", ServiceStartType.Disabled, running: true);
        });
        var results = ctl.Apply(["edgeupdatem", "Fax", "MapsBroker", "RetailDemo"], "profile Gaming");
        Assert.Equal(["apply MapsBroker", "apply RetailDemo"], svc.Calls);
        Assert.Equal([("MapsBroker", null), ("RetailDemo", null), ("edgeupdatem", ServicesController.OffAlready), ("Fax", ServicesController.OffAlready)], results);
        Assert.Equal(["MapsBroker", "RetailDemo"], Saved(paths).Select(c => c.Service).ToArray());
        Assert.Equal(2, _findings.Count(f => f.Title.StartsWith("Service turned off:", StringComparison.Ordinal)));
        Assert.Equal("MapsBroker, RetailDemo: done\nedgeupdatem, Fax: off already, so left as it is", Ui.ServicesPageText.ResultMessage(results, ""));

        // Only services off already: nothing is saved at all.
        int version = ctl.Version;
        Assert.Equal([("edgeupdatem", ServicesController.OffAlready), ("Fax", ServicesController.OffAlready)], ctl.Apply(["edgeupdatem", "Fax"], "manual"));
        Assert.Equal(2, svc.Calls.Count);
        Assert.Equal(version, ctl.Version);
    }

    [Fact]
    public void ApplyRecordsAgainAServiceThatIsOffButWhoseRecordNoLongerMatchesItSoRestoreWorksAgain()
    {
        // edgeupdate's entry turns it off to Manual. PoolWatch did; then something outside it set Disabled. Restore refuses
        // the record, and says "turn it off again on the Services tab, then Put back": Turn off records it again, so that works.
        var (ctl, svc, _) = New(s => s.Add("edgeupdate", ServiceStartType.Automatic, running: true));
        Assert.Null(Assert.Single(ctl.Apply(["edgeupdate"], "manual")).error);
        svc.Live["edgeupdate"] = svc.Live["edgeupdate"] with { StartType = ServiceStartType.Disabled, Running = false };
        Assert.Contains("turn it off again on the Services tab, then Put back", Assert.Single(ctl.Restore("edgeupdate")).error);
        var (change, offAlready) = Ui.ServicesPageText.ApplyPlan(["edgeupdate"], ctl.Options());
        Assert.Equal(["edgeupdate"], change); // Apply's question lists it
        Assert.Equal(0, offAlready);

        Assert.Equal([("edgeupdate", (string?)null)], ctl.Apply(["edgeupdate"], "manual"));
        var again = Assert.Single(ctl.Changes);
        Assert.Equal((ServiceStartType.Automatic, ServiceStartType.Disabled), (again.OriginalStart, again.NewStart)); // the first original kept
        Assert.Null(Assert.Single(ctl.Restore("edgeupdate")).error);
        Assert.Equal(ServiceStartType.Automatic, svc.Live["edgeupdate"].StartType);
        Assert.Empty(ctl.Changes);

        // edgeupdatem was Disabled and running: Apply stopped it (Disabled to Disabled). Windows then set it to Manual, which
        // its entry counts as off: Apply records it again, and Restore puts Disabled back.
        svc.Add("edgeupdatem", ServiceStartType.Disabled, running: true);
        Assert.Null(Assert.Single(ctl.Apply(["edgeupdatem"], "manual")).error);
        svc.Live["edgeupdatem"] = svc.Live["edgeupdatem"] with { StartType = ServiceStartType.Manual };
        Assert.Equal([("edgeupdatem", (string?)null)], ctl.Apply(["edgeupdatem"], "manual"));
        Assert.Equal((ServiceStartType.Disabled, ServiceStartType.Manual), (Assert.Single(ctl.Changes).OriginalStart, Assert.Single(ctl.Changes).NewStart));
        Assert.Null(Assert.Single(ctl.Restore("edgeupdatem")).error);
        Assert.Equal(ServiceStartType.Disabled, svc.Live["edgeupdatem"].StartType);
        Assert.Empty(ctl.Changes);

        // Back at its original (a Windows default put back, then Disabled again by its owner): Restore only drops that
        // record, so Apply leaves it as it is and records nothing, as for a service PoolWatch never changed.
        svc.Add("DiagTrack", ServiceStartType.Disabled, running: false);
        Assert.Null(Assert.Single(ctl.RestoreToDefault("DiagTrack")).error);
        svc.Live["DiagTrack"] = svc.Live["DiagTrack"] with { StartType = ServiceStartType.Disabled };
        Assert.Equal([("DiagTrack", ServicesController.OffAlready)], ctl.Apply(["DiagTrack"], "manual"));
        Assert.Equal(ServicePlan.ViaWindowsDefault, Assert.Single(ctl.Changes).Via);
    }

    [Fact]
    public void ApplysQuestionListsOnlyTheServicesItChanges()
    {
        var options = SomeServices.Options(); // InventorySvc is Disabled, edgeupdatem Manual (as its entry turns it off): both off
        var (change, off) = Ui.ServicesPageText.ApplyPlan(["SysMain", "InventorySvc", "Fax", "edgeupdatem"], options);
        Assert.Equal(["SysMain", "Fax"], change);
        Assert.Equal(2, off);
        Assert.Equal("2 services will be stopped and set to not start:\n\n  SysMain\n  Fax\n\n" +
                     "If a service you did not tick depends on one of them and has not stopped, Windows does not stop that one: it is set to not start " +
                     "and stops when Windows restarts.\n\n2 more ticked services are off already and are left as they are.\n\n" +
                     "PoolWatch saves how each one was set first, so you can put it back. Continue?", Ui.ServicesPageText.ApplyQuestion(change, off));
        Assert.Contains("\n\n1 more ticked service is off already and is left as it is.\n\n", Ui.ServicesPageText.ApplyQuestion(["SysMain"], 1));
        var (none, one) = Ui.ServicesPageText.ApplyPlan(["inventorysvc"], options);
        Assert.Equal((0, 1), (none.Count, one));
        Assert.Equal("The ticked service is off already, so there is nothing to turn off.", Ui.ServicesPageText.AllOffAlready(1));
        Assert.Equal("The ticked services are all off already, so there is nothing to turn off.", Ui.ServicesPageText.AllOffAlready(3));
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
        // Records read back from service-changes.json are untrusted: each is re-gated against the live service first.
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

    // ---------------------------------------------------------------- the record on disk

    [Theory]
    [InlineData("")]                                              // empty, as a power cut can leave a file just written
    [InlineData("\0\0\0\0\0\0\0\0")]                              // zero-filled, the same way
    [InlineData("[{\"service\": \"Fax\", \"originalStart\": \"Ma")] // cut off
    [InlineData("[null]")]
    public void ARecordThatCannotBeReadIsNeverWrittenOverAndNoServiceChanges(string damaged)
    {
        var (ctl, svc, paths) = New(file: damaged);

        var (_, error) = Assert.Single(ctl.Apply(["MapsBroker"], "test"));
        Assert.Contains("could not save how it was set", error);
        Assert.Equal(ServiceStartType.Automatic, svc.Live["MapsBroker"].StartType);
        Assert.Empty(svc.Calls);                               // the service was never changed
        Assert.Equal(damaged, File.ReadAllText(ChangeFile(paths))); // nor the file that may hold the only originals
    }

    [Fact]
    public void ARecordThatCannotBeReadIsAChangeThatCannotBeUndone()
    {
        var (ctl, _, paths) = New(file: "[{\"service\": \"Fax\", \"originalStart\": \"Ma");
        var log = LogOf(paths);
        var ledger = new Engine.Changes.ChangeLedger([new Engine.Changes.ServicesSource(ctl)], new Engine.Changes.ChangeRecords(paths, log), log);

        var row = Assert.Single(ledger.List()); // listed, so the uninstaller does not report every change put back
        Assert.Equal((Engine.Changes.ChangeKind.Service, false), (row.Kind, row.CanUndo));
        Assert.False(Assert.Single(ledger.UndoAll()).Result.Done);
    }

    [Fact]
    public void AServiceIsNotTurnedOffWhenHowItWasSetCannotBeSaved()
    {
        var (ctl, svc, paths) = New();
        Directory.CreateDirectory(ChangeFile(paths) + ".tmp"); // the record cannot be written

        var (_, error) = Assert.Single(ctl.Apply(["MapsBroker"], "test"));
        Assert.Contains("could not save how it was set", error);
        Assert.Empty(svc.Calls);
        Assert.Equal(ServiceStartType.Automatic, svc.Live["MapsBroker"].StartType);
        Assert.Empty(ctl.Changes);
        Assert.False(File.Exists(ChangeFile(paths)));
    }

    [Fact]
    public void AServiceIsNotPutBackToItsDefaultWhenHowItWasSetCannotBeSaved()
    {
        // Off outside PoolWatch, with a Windows default the catalogue knows: "Restore to Windows default" applies.
        var (ctl, svc, paths) = New(s => s.Live["MapsBroker"] = s.Live["MapsBroker"] with { StartType = ServiceStartType.Disabled, Running = false });
        Directory.CreateDirectory(ChangeFile(paths) + ".tmp");

        var (_, error) = Assert.Single(ctl.RestoreToDefault("MapsBroker"));
        Assert.Contains("could not save how it was set", error);
        Assert.Empty(svc.Calls);
        Assert.Empty(ctl.Changes);
    }

    [Fact]
    public void AChangeTheCommandLineMadeIsKeptListedAndRestoredByTheRunningApp()
    {
        var (app, svc, paths) = New(s => s.Add("RetailDemo", ServiceStartType.Manual, running: false));
        // The command line, while the app runs: a controller of its own over the same file.
        var commandLine = new ServicesController(paths, LogOf(paths), _ => { }, _ => null, _ => null, svc, Host());
        Assert.Null(Assert.Single(commandLine.Apply(["MapsBroker"], "command line")).error);

        Assert.Null(Assert.Single(app.Apply(["RetailDemo"], "test")).error); // later, from the Services tab
        Assert.Equal(["MapsBroker", "RetailDemo"], Saved(paths).Select(c => c.Service).Order().ToArray());
        Assert.Equal(ServiceStartType.Automatic, Saved(paths).Single(c => c.Service == "MapsBroker").OriginalStart);

        Assert.Contains(Core.Changes.ChangeIds.Service("MapsBroker"), new Engine.Changes.ServicesSource(app).List().Select(r => r.Id));
        Assert.Null(Assert.Single(app.Restore("MapsBroker")).error);
        Assert.Equal(ServiceStartType.Automatic, svc.Live["MapsBroker"].StartType);
        Assert.Equal(["RetailDemo"], Saved(paths).Select(c => c.Service).ToArray());
    }

    [Fact]
    public void ARestoreKeepsARecordSavedAgainSinceItRead()
    {
        var (app, svc, paths) = New();
        Assert.Null(Assert.Single(app.Apply(["MapsBroker"], "test")).error);
        var commandLine = new ServicesController(paths, LogOf(paths), _ => { }, _ => null, _ => null, svc, Host());
        // While the app restores it, the command line turns it off again: its record is the one that counts now.
        svc.OnRestore = () =>
        {
            svc.OnRestore = null;
            Assert.Null(Assert.Single(commandLine.Apply(["MapsBroker"], "command line")).error);
        };

        app.Restore("MapsBroker");
        var kept = Assert.Single(Saved(paths));
        Assert.Equal(("command line", ServiceStartType.Automatic), (kept.Via, kept.OriginalStart));
    }

    [Fact]
    public void AChangeThatFailsKeepsARecordSavedAgainSinceItSaved()
    {
        var (app, svc, paths) = New();
        var commandLine = new ServicesController(paths, LogOf(paths), _ => { }, _ => null, _ => null, svc, Host());
        // The app's record is saved; before its change fails, the command line turns the service off and saves its own.
        svc.ApplyError = "access denied";
        svc.OnApply = () =>
        {
            svc.ApplyError = null;
            Assert.Null(Assert.Single(commandLine.Apply(["MapsBroker"], "command line")).error);
            svc.ApplyError = "access denied";
        };

        Assert.Equal("access denied", Assert.Single(app.Apply(["MapsBroker"], "test")).error);
        var kept = Assert.Single(Saved(paths)); // the command line's record, not taken back with the app's
        Assert.Equal(("command line", ServiceStartType.Disabled), (kept.Via, svc.Live["MapsBroker"].StartType));
    }

    [Fact]
    public void TheServicesPageShowsAChangeTheCommandLineMade()
    {
        var (app, svc, paths) = New();
        var commandLine = new ServicesController(paths, LogOf(paths), _ => { }, _ => null, _ => null, svc, Host());
        Assert.Null(Assert.Single(commandLine.Apply(["MapsBroker"], "command line")).error);

        var maps = app.Options().Single(o => o.Service.Name == "MapsBroker");
        Assert.Equal("command line", maps.PoolWatchChange?.Via); // PoolWatch's own change, not "changed outside PoolWatch"
        Assert.Equal("MapsBroker", Assert.Single(app.Changes).Service);
    }

    [Fact]
    public void ARecordOverItsBoundIsNotReadAndNothingIsChanged()
    {
        // Read through one handle, with a bound on the bytes read: one byte more than PoolWatch ever writes is refused.
        var (ctl, svc, paths) = New(file: new string(' ', ServicesController.MaxRecordBytes + 1));
        Assert.Contains(ChangeFile(paths) + " is over 1 MB, more than PoolWatch writes.", ctl.RecordError);
        Assert.StartsWith("PoolWatch cannot read how the services were set: ", Assert.Single(ctl.Restore(null)).error);
        Assert.Contains("over 1 MB", Assert.Single(ctl.Apply(["MapsBroker"], "test")).error);
        Assert.Empty(svc.Calls);
        Assert.Equal(ServicesController.MaxRecordBytes + 1, new FileInfo(ChangeFile(paths)).Length); // left as it is
    }

    [Fact]
    public void ASaveOverTheBoundIsRefusedAndTheRecordStaysReadable()
    {
        // A record PoolWatch reads (about 600 KB: each U+00E9 is two bytes on disk) that its own save would write past the
        // bound, since the save writes each one as a six-byte escape. A save that went through would leave a file its
        // next read refuses, blocking every later Apply and Restore.
        string file = $"[{{\"service\": \"Fax\", \"displayName\": \"{new string('é', 300_000)}\", \"originalStart\": \"Manual\", " +
                      "\"originalRunning\": false, \"newStart\": \"Disabled\"}]";
        var (ctl, svc, paths) = New(file: file);
        Assert.Null(ctl.RecordError);
        Assert.True(new FileInfo(ChangeFile(paths)).Length < ServicesController.MaxRecordBytes);

        var (_, error) = Assert.Single(ctl.Apply(["MapsBroker"], "test"));
        Assert.Contains("service-changes.json would be over 1 MB", error);
        Assert.Empty(svc.Calls); // the service was never changed
        Assert.Equal(ServiceStartType.Automatic, svc.Live["MapsBroker"].StartType);
        Assert.Equal(file, File.ReadAllText(ChangeFile(paths))); // nor the file, which still reads
        Assert.Equal("Fax", Assert.Single(ctl.Current()).Service);
        Assert.Null(ctl.RecordError);
    }

    [Fact]
    public void ARestoreOfARecordThatCannotBeReadChangesNothingAndSaysWhy()
    {
        var (ctl, svc, paths) = New(s => s.Live["MapsBroker"] = s.Live["MapsBroker"] with { StartType = ServiceStartType.Disabled, Running = false },
            file: "[{\"service\": \"MapsBroker\", \"originalStart\": \"Auto");
        Assert.Contains("damaged or empty", ctl.RecordError); // the page cannot read "nothing to restore" from Changes then
        Assert.Empty(ctl.Changes);

        var (service, error) = Assert.Single(ctl.Restore(null));
        Assert.Equal("(services)", service);
        Assert.Contains("cannot read how the services were set", error);
        Assert.Empty(svc.Calls);

        File.Delete(ChangeFile(paths)); // deleted, as the message offers: a new record starts
        ctl.Options();
        Assert.Null(ctl.RecordError);
    }

    [Fact]
    public void TheServicesPageSaysWhyItCannotRestoreAndShowsEachResultReadably()
    {
        var (ctl, svc, paths) = New(s => s.Add("RetailDemo", ServiceStartType.Manual, running: false), file: "[{\"service\": \"MapsBroker\", \"originalStart\": \"Auto");
        string damaged = ChangeFile(paths) + " is damaged or empty.";

        // The status line says so in short, not just a count of changes (there are none it could read); Put back says why.
        string? status = Ui.ServicesPageText.RecordProblem(ctl.RecordError);
        Assert.Equal("PoolWatch cannot read how it changed the services, so it cannot put them back yet (Put all back says why).", status);

        // Put all back and a service's own Put back go to the controller, which says why; nothing is changed.
        Assert.Null(Ui.ServicesPageText.StopsBeforeRestore(null, ctl));
        Assert.Null(Ui.ServicesPageText.StopsBeforeRestore("MapsBroker", ctl));
        Assert.StartsWith("PoolWatch cannot read how the services were set: " + damaged, Ui.ServicesPageText.ResultMessage(ctl.Restore(null), "")); // not "(services): ..."
        Assert.StartsWith("MapsBroker: PoolWatch cannot read how the services were set: " + damaged, Ui.ServicesPageText.ResultMessage(ctl.Restore("MapsBroker"), ""));

        // Apply leaves every ticked service as it is, and says the reason they share once.
        string applied = Ui.ServicesPageText.ResultMessage(ctl.Apply(["MapsBroker", "RetailDemo"], "test"), "");
        Assert.StartsWith("MapsBroker, RetailDemo: not changed, because PoolWatch could not save how it was set: " + damaged, applied);
        Assert.DoesNotContain("\n", applied);
        Assert.Empty(svc.Calls);

        // Once the record reads again (deleted, as the message offers), the page goes by it: no changes, nothing to put back.
        File.Delete(ChangeFile(paths));
        ctl.Options();
        Assert.Null(Ui.ServicesPageText.RecordProblem(ctl.RecordError));
        Assert.Equal("Nothing to put back: PoolWatch has not changed any service.", Ui.ServicesPageText.StopsBeforeRestore(null, ctl));
        Assert.Equal("PoolWatch has not changed MapsBroker: nothing to put back.", Ui.ServicesPageText.StopsBeforeRestore("MapsBroker", ctl));
        Assert.Null(Assert.Single(ctl.Apply(["MapsBroker"], "test")).error);
        Assert.Null(Ui.ServicesPageText.RecordProblem(ctl.RecordError));
        Assert.Null(Ui.ServicesPageText.StopsBeforeRestore(null, ctl));
        Assert.Null(Ui.ServicesPageText.StopsBeforeRestore("mapsbroker", ctl)); // the one changed, by any case
        Assert.Equal("PoolWatch has not changed RetailDemo: nothing to put back.", Ui.ServicesPageText.StopsBeforeRestore("RetailDemo", ctl));
    }

    [Fact]
    public void TheTabsWordsForApplyAReadThatFailedAndWhatToDo()
    {
        // The one exception to "stopped": a service nobody ticked depends on it and has not stopped.
        const string ItKeepsRunning = "If a service you did not tick depends on it and has not stopped, Windows does not stop it: it is set to not start and " +
                                      "stops when Windows restarts.\n\n";
        Assert.Equal("1 service will be stopped and set to not start:\n\n  SysMain\n\n" + ItKeepsRunning +
                     "PoolWatch saves how each one was set first, so you can put it back. Continue?",
            Ui.ServicesPageText.ApplyQuestion(["SysMain"]));
        Assert.StartsWith("2 services will be stopped and set to not start:\n\n  SysMain\n  Fax\n\nIf a service you did not tick depends on one of them and has not stopped, ",
            Ui.ServicesPageText.ApplyQuestion(["SysMain", "Fax"]));
        // Services Apply sets to Manual, which it does not stop, are said apart.
        Assert.Equal("1 service will be stopped and set to not start:\n\n  SysMain\n\n" + ItKeepsRunning +
                     "2 services will be set to Manual, so they start only when something asks for them (PoolWatch does not stop them):\n\n  edgeupdate\n  edgeupdatem\n\n" +
                     "1 more ticked service is off already and is left as it is.\n\nPoolWatch saves how each one was set first, so you can put it back. Continue?",
            Ui.ServicesPageText.ApplyQuestion(["SysMain", "edgeupdate", "edgeupdatem"], 1, ["edgeupdatem", "edgeupdate"]));
        Assert.Equal("1 service will be set to Manual, so it starts only when something asks for it (PoolWatch does not stop it):\n\n  edgeupdate\n\n" +
                     "PoolWatch saves how each one was set first, so you can put it back. Continue?", Ui.ServicesPageText.ApplyQuestion(["edgeupdate"], 0, ["edgeupdate"]));
        // Which ones go to Manual: the catalogue's, unless the service is Disabled now (never loosened).
        var options = ServicePlan.Options([new ServiceView { Name = "edgeupdate", StartType = ServiceStartType.Automatic, MicrosoftSigned = true },
            new ServiceView { Name = "edgeupdatem", StartType = ServiceStartType.Disabled, Running = true, MicrosoftSigned = true },
            new ServiceView { Name = "SysMain", StartType = ServiceStartType.Automatic, MicrosoftSigned = true }], DefaultCatalogue.Shared, false, new HashSet<string>());
        Assert.Equal(["edgeupdate"], Ui.ServicesPageText.ToManual(["SysMain", "edgeupdate", "edgeupdatem"], options));
        Assert.Equal("PoolWatch could not read the services: Access is denied. Refresh tries again.", Ui.ServicesPageText.ReadFailed("Access is denied."));
    }

    [Fact]
    public void ServiceResultsWithTheSameOutcomeShareALine()
    {
        Assert.Equal("MapsBroker, RetailDemo: done\nDiagTrack: access denied", Ui.ServicesPageText.ResultMessage([("MapsBroker", null), ("DiagTrack", "access denied"), ("RetailDemo", null)], ""));
        Assert.Equal("a protected service was ticked", Ui.ServicesPageText.ResultMessage([("(plan)", "a protected service was ticked")], ""));
        Assert.Equal(Ui.ServicesPageText.NothingToRestoreText(null), Ui.ServicesPageText.ResultMessage([], Ui.ServicesPageText.NothingToRestoreText(null)));
    }

    [Fact]
    public void ARestoredServiceIsNotReadAsChangedWhenItsRecordCouldNotBeRemoved()
    {
        var (ctl, svc, paths) = New(s => s.Add("RetailDemo", ServiceStartType.Manual, running: false));
        Assert.Null(Assert.Single(ctl.Apply(["MapsBroker"], "test")).error);
        var tmp = Directory.CreateDirectory(ChangeFile(paths) + ".tmp"); // the save after the Restore fails

        Assert.Null(Assert.Single(ctl.Restore("MapsBroker")).error);
        Assert.Equal("MapsBroker", Assert.Single(Saved(paths)).Service); // still in the file
        ctl.Options();                                                   // read again, as every page refresh does
        Assert.Empty(ctl.Changes);
        Assert.Empty(ctl.Current());

        tmp.Delete();
        Assert.Null(Assert.Single(ctl.Apply(["RetailDemo"], "test")).error); // the next save leaves it out of the file
        Assert.Equal(["RetailDemo"], Saved(paths).Select(c => c.Service).ToArray());
    }

    /// <summary>
    /// The Services tab's buttons are Turn off, Put back (the selected service, or to its Windows default) and Put all
    /// back; Active changes' are Turn off, Remove and Put back. No text the code behind them shows (a result, a refusal,
    /// a timeline line, a question) names a button they no longer have: "Restore", "Restore all", "Restore to Windows
    /// default", or "Apply" for Turn off. Read from the source, so a message added later is held to it too.
    /// </summary>
    [Fact]
    public void NoServicesOrActiveChangesTextNamesAButtonTheyNoLongerHave()
    {
        string[] files =
        [
            "src/PoolWatch/Engine/ServicesController.cs", "src/PoolWatch/Engine/ProfileStore.cs", "src/PoolWatch/Engine/ProfileAtStart.cs",
            "src/PoolWatch.Core/Services/ServicePlan.cs", "src/PoolWatch.Core/Services/ServiceProfiles.cs", "src/PoolWatch.Core/Changes/ChangeWords.cs",
            "src/PoolWatch/Engine/Changes/ChangeSources.cs", "src/PoolWatch/Engine/Changes/ChangeLedger.cs", "src/PoolWatch/Engine/Changes/ChangeRecords.cs",
            "src/PoolWatch/Ui/ServicesTab.cs", "src/PoolWatch/Ui/ServicesTabView.cs", "src/PoolWatch/Ui/ServicesPageText.cs", "src/PoolWatch/Ui/ProfileText.cs",
            "src/PoolWatch/Ui/ProfilePicker.cs", "src/PoolWatch/Ui/ProfileDialogs.cs", "src/PoolWatch/Ui/ActiveChangesForm.cs",
        ];
        var gone = new System.Text.RegularExpressions.Regex(@"\bRestore\b|\(Apply\)|\b(with|use) Apply\b");
        var literal = new System.Text.RegularExpressions.Regex(@"\$?@?""(?:[^""\\\r\n]|\\.)*""");
        var found = new List<string>();
        foreach (string file in files)
        {
            var lines = File.ReadAllLines(Path.Combine(TrayIconsTests.RepoRoot(), file));
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal)) continue; // a comment, not a text
                foreach (System.Text.RegularExpressions.Match m in literal.Matches(lines[i]))
                    if (gone.IsMatch(m.Value)) found.Add($"{file}:{i + 1}: {m.Value}");
            }
        }
        Assert.True(found.Count == 0, string.Join(Environment.NewLine, found));
    }

    // ---------------------------------------------------------------- a service another one depends on

    /// <summary>
    /// MapsBroker, running, and another app's service that depends on it, running too (Acme Maps; its other dependency is
    /// not listed here). Acme Maps is these tests' own, not a catalogue row; MapsBroker is one, and these tests rest on its
    /// row turning it off to Disabled (no other catalogue row drives them).
    /// </summary>
    private static void MapsPair(FakeServices s)
    {
        s.Add("MapsBroker", ServiceStartType.Automatic, running: true, pid: 40);
        s.AddThirdParty("AcmeMaps", 41, "RpcSs", "MapsBroker");
    }

    /// <summary>The pair, and a second app's service that depends on MapsBroker too (Acme Sync).</summary>
    private static void MapsTrio(FakeServices s)
    {
        MapsPair(s);
        s.AddThirdParty("AcmeSync", 42, "MapsBroker");
    }

    private List<string> SelfRepairWarnings() =>
        _findings.Where(f => f.Title.StartsWith("Service re-enabled outside PoolWatch", StringComparison.Ordinal)).Select(f => f.Title).ToList();

    /// <summary>
    /// Ticked together, here the one the other depends on first, Acme Maps is stopped before MapsBroker, so Windows stops
    /// both and each is done.
    /// </summary>
    [Fact]
    public void TurnedOffTogetherTheServiceThatDependsOnTheOtherIsStoppedFirst()
    {
        var (ctl, svc, _) = New(MapsPair);
        Assert.Equal([("AcmeMaps", (string?)null), ("MapsBroker", null)], ctl.Apply(["MapsBroker", "AcmeMaps"], "manual"));
        Assert.Equal(["apply AcmeMaps", "apply MapsBroker"], svc.Calls);
        foreach (var name in new[] { "MapsBroker", "AcmeMaps" })
            Assert.Equal((ServiceStartType.Disabled, false), (svc.Live[name].StartType, svc.Live[name].Running));
        Assert.All(ctl.Changes, c => Assert.Null(c.NotStoppedPid));
    }

    /// <summary>
    /// Put all back puts MapsBroker back before Acme Maps, which needs it, so both run again: Windows does not start a
    /// service while one it needs is Disabled.
    /// </summary>
    [Fact]
    public void PutAllBackPutsTheServiceTheOtherDependsOnBackFirst()
    {
        var (ctl, svc, paths) = New(MapsPair);
        Assert.All(ctl.Apply(["MapsBroker", "AcmeMaps"], "manual"), r => Assert.Null(r.error));
        Assert.Equal(["AcmeMaps", "MapsBroker"], Saved(paths).Select(c => c.Service)); // in the order they were turned off
        svc.Calls.Clear();
        Assert.Equal([("MapsBroker", (string?)null), ("AcmeMaps", null)], ctl.Restore(null));
        Assert.Equal(["restore MapsBroker", "restore AcmeMaps"], svc.Calls);
        foreach (var name in new[] { "MapsBroker", "AcmeMaps" })
            Assert.Equal((ServiceStartType.Automatic, true), (svc.Live[name].StartType, svc.Live[name].Running));
        Assert.Empty(Saved(paths));
    }

    /// <summary>
    /// Only MapsBroker is ticked while Acme Maps, which depends on it, runs: Windows does not stop it, and PoolWatch never
    /// stops Acme Maps, which nobody chose. MapsBroker is set to Disabled and keeps running. The result says so and when
    /// it stops, never done; its record stays, so Put back works, and says it ran on; the State column says so; the run at
    /// start lists it as not turned off.
    /// </summary>
    [Fact]
    public void AServiceThatDidNotStopIsNeverDoneAndTheResultSaysWhenItStops()
    {
        const string Why = "set to Disabled, but still running: AcmeMaps depends on it and is running. It stops when Windows restarts, " +
                           "or when you turn it off again after that service has stopped";
        var (ctl, svc, paths) = New(MapsPair);
        var results = ctl.Apply(["MapsBroker"], "manual");
        Assert.Equal([("MapsBroker", (string?)Why)], results);
        Assert.True(ServicePlan.IsNotStoppedText(Why));
        Assert.Equal("MapsBroker: " + Why, Ui.ServicesPageText.ResultMessage(results, "Nothing was changed."));
        Assert.Equal(["apply MapsBroker"], svc.Calls); // Acme Maps is left as it is
        Assert.Equal((ServiceStartType.Disabled, true), (svc.Live["MapsBroker"].StartType, svc.Live["MapsBroker"].Running));
        Assert.Equal((ServiceStartType.Automatic, true), (svc.Live["AcmeMaps"].StartType, svc.Live["AcmeMaps"].Running));
        var record = Assert.Single(Saved(paths));
        Assert.Equal(("MapsBroker", ServiceStartType.Automatic, true, ServiceStartType.Disabled, (int?)40),
            (record.Service, record.OriginalStart, record.OriginalRunning, record.NewStart, record.NotStoppedPid));
        Assert.DoesNotContain(_findings, f => f.Title.StartsWith("Service turned off", StringComparison.Ordinal));
        var finding = Assert.Single(_findings, f => f.Title.StartsWith("Service not stopped: MapsBroker", StringComparison.Ordinal));
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.EndsWith(": " + Why, finding.Title);
        Assert.Null(ctl.SavingsText("MapsBroker")); // nothing was saved, so nothing is measured
        string state = ServicePlan.StateText(ctl.Options().Single(o => o.Service.Name == "MapsBroker"));
        Assert.StartsWith("set to Disabled, still running (turned off by PoolWatch ", state);
        Assert.EndsWith("; it stops when Windows restarts)", state);

        // The run at start reads it as on and tries again: not turned off, nor "could not turn off" (its start type is
        // set: a change, which Active changes lists), and the first original is kept.
        var run = ctl.ApplyProfile(new ServiceProfile("P", ["MapsBroker"]), ProfileAtStart.Via("P"));
        Assert.Empty(run.TurnedOff);
        Assert.Empty(run.Failed);
        Assert.Equal([("MapsBroker", Why)], run.NotStopped);
        Assert.Equal(@"P set 1 service to Disabled, but it is not known to have stopped: C:\log says why. Click here to see it in Active changes.",
            ProfileAtStart.Notice("P", run, @"C:\log"));
        Assert.Equal(ServiceStartType.Automatic, Assert.Single(Saved(paths)).OriginalStart);
        // The run's log line counts it apart, at the end of the line.
        Assert.Contains("Service profile P at start: 0 turned off, 0 already off, 0 left as they are, 0 not turned off, 1 set to Disabled but not known " +
                        "to have stopped" + Environment.NewLine, File.ReadAllText(Path.Combine(paths.DataRoot, "test.log")));
    }

    /// <summary>
    /// Two services depend on MapsBroker and run. Ticked with one of them, which this run stops first, MapsBroker does
    /// not stop: its result names only the one still running, never the one just stopped.
    /// </summary>
    [Fact]
    public void ADependentThisRunStoppedIsNotNamedAsRunning()
    {
        var (ctl, svc, _) = New(MapsTrio);
        Assert.Equal([("AcmeMaps", (string?)null), ("MapsBroker", "set to Disabled, but still running: AcmeSync depends on it and is running. " +
                      "It stops when Windows restarts, or when you turn it off again after that service has stopped")],
            ctl.Apply(["MapsBroker", "AcmeMaps"], "manual"));
        Assert.Equal(["apply AcmeMaps", "apply MapsBroker"], svc.Calls);
        Assert.Equal((ServiceStartType.Automatic, true), (svc.Live["AcmeSync"].StartType, svc.Live["AcmeSync"].Running));
    }

    /// <summary>
    /// Error 1051 while one service that depends on it is paused and another runs: when Windows' own list could not be
    /// read, the result names both, each with its state, as the services were read; never only the running one. When
    /// Windows listed them, its list is the one named.
    /// </summary>
    [Fact]
    public void ADependentThatIsPausedIsNamedToo()
    {
        var (ctl, svc, _) = New(s =>
        {
            MapsTrio(s);
            s.Live["AcmeSync"] = s.Live["AcmeSync"] with { Running = false, NeitherRunningNorStopped = true, Between = ServiceRunState.Paused, ProcessId = 0 };
        });
        svc.StopRefused = FakeServices.DependentsRunning;
        Assert.Equal([("MapsBroker", (string?)"set to Disabled, but still running: AcmeMaps (running), AcmeSync (paused) depend on it and have not stopped. " +
                      "It stops when Windows restarts, or when you turn it off again after those have stopped")],
            ctl.Apply(["MapsBroker"], "manual"));

        var (again, svc2, _) = New(MapsTrio);
        svc2.StopRefused = FakeServices.DependentsRunning with { Dependents = [new("AcmeSync", ServiceRunState.Starting)] };
        Assert.Equal([("MapsBroker", (string?)"set to Disabled, but still running: AcmeSync depends on it and is starting. " +
                      "It stops when Windows restarts, or when you turn it off again after that service has stopped")],
            again.Apply(["MapsBroker"], "manual"));
    }

    /// <summary>Ticked alone, MapsBroker's result names both running services that depend on it, in the plural.</summary>
    [Fact]
    public void SeveralRunningDependentsAreNamedInThePlural()
    {
        var (ctl, _, _) = New(MapsTrio);
        Assert.Equal([("MapsBroker", (string?)"set to Disabled, but still running: AcmeMaps, AcmeSync depend on it and are running. " +
                      "It stops when Windows restarts, or when you turn it off again after those have stopped")],
            ctl.Apply(["MapsBroker"], "manual"));
    }

    /// <summary>
    /// A stop Windows took that did not end in time, while a service that depends on it runs: the result gives that
    /// reason, not the dependent, as Windows did not refuse because of it; "still running" only when it runs.
    /// </summary>
    [Theory]
    [InlineData(ServiceControl.SERVICE_RUNNING, "set to Disabled, but still running: it did not stop within 20 seconds. It stops when Windows " +
                                                "restarts, or when you turn it off again")]
    [InlineData(ServiceControl.SERVICE_STOP_PENDING, "set to Disabled, not stopped yet: it was still stopping 20 seconds after Windows took the stop. " +
                                                     "It stops when Windows restarts, if not before")]
    public void TheRunningDependentsAreNamedOnlyWhenWindowsRefusedForThem(uint after, string expected)
    {
        var (ctl, svc, paths) = New(MapsPair);
        svc.StopRefused = ServiceControl.Looked(after, asked: true, timeUp: true).Why;
        Assert.Equal([("MapsBroker", (string?)expected)], ctl.Apply(["MapsBroker"], "manual"));
        Assert.Equal(40, Assert.Single(Saved(paths)).NotStoppedPid);
    }

    /// <summary>
    /// Another reason Windows gives for not stopping a service is said in its place; the process is not known here. One that
    /// does not take a stop (error 1052) is not said to stop when it is turned off again: only when Windows restarts.
    /// </summary>
    [Theory]
    [InlineData(1061, "set to Disabled, but still running: it cannot take a stop now (Windows error 1061). It stops when Windows restarts, or when " +
                      "you turn it off again")]
    [InlineData(1052, "set to Disabled, but still running: it does not take a stop (Windows error 1052). It stops when Windows restarts")]
    public void AStopWindowsRefusesForAnotherReasonIsSaidAsWindowsSaysIt(int answer, string expected)
    {
        var (ctl, svc, paths) = New();
        svc.StopRefused = ServiceControl.Answered(answer, lookedAgain: true).Why;
        Assert.Equal([("MapsBroker", (string?)expected)], ctl.Apply(["MapsBroker"], "manual"));
        Assert.Equal((ServiceStartType.Disabled, true), (svc.Live["MapsBroker"].StartType, svc.Live["MapsBroker"].Running));
        Assert.Equal(0, Assert.Single(Saved(paths)).NotStoppedPid);
    }

    /// <summary>
    /// A service caught starting, resuming or pausing that does not settle in time, one whose stop Windows took but that
    /// did not stop in time, or one whose state Windows cannot give, is never done: its start type is set, the result
    /// says why (never "still running" of one that was starting, resuming, pausing or stopping), its record is marked as
    /// for a refused stop, nothing is measured, and the self-repair check does not blame something outside PoolWatch for it.
    /// </summary>
    [Theory]
    [InlineData(ServiceControl.SERVICE_START_PENDING, false, "set to Disabled, not stopped yet: it was still starting after 20 seconds. It stops when Windows restarts, if not before")]
    [InlineData(ServiceControl.SERVICE_CONTINUE_PENDING, false, "set to Disabled, not stopped yet: it was still resuming after 20 seconds. It stops when Windows restarts, if not before")]
    [InlineData(ServiceControl.SERVICE_PAUSE_PENDING, false, "set to Disabled, not stopped yet: it was still pausing after 20 seconds. It stops when Windows restarts, if not before")]
    [InlineData(ServiceControl.SERVICE_STOP_PENDING, false, "set to Disabled, not stopped yet: it was still stopping after 20 seconds. It stops when Windows restarts, if not before")]
    [InlineData(ServiceControl.SERVICE_STOP_PENDING, true, "set to Disabled, not stopped yet: it was still stopping 20 seconds after Windows took the stop. It stops when Windows restarts, if not before")]
    [InlineData(ServiceControl.SERVICE_RUNNING, true, "set to Disabled, but still running: it did not stop within 20 seconds. It stops when Windows restarts, or when you turn it off again")]
    [InlineData(0u, false, "set to Disabled, but Windows could not say whether it is running, so it is not known to have stopped. If it is still running, it stops when Windows restarts, or when you turn it off again")]
    public void AServiceNotKnownToHaveStoppedIsNeverDoneAndNotBlamedOutside(uint state, bool asked, string expected)
    {
        var (ctl, svc, paths) = New(s => s.Add("MapsBroker", ServiceStartType.Automatic, running: true, pid: 40));
        svc.StopRefused = ServiceControl.Looked(state, asked, timeUp: true).Why;
        Assert.Equal([("MapsBroker", (string?)expected)], ctl.Apply(["MapsBroker"], "manual"));
        Assert.True(ServicePlan.IsNotStoppedText(expected));
        Assert.Equal(ServiceStartType.Disabled, svc.Live["MapsBroker"].StartType);
        Assert.Equal(40, Assert.Single(Saved(paths)).NotStoppedPid);
        string summary = Assert.Single(ctl.Changes).Summary();
        Assert.Single(_findings, f => f.Title == $"Service not stopped: {summary}: {expected}" && f.Severity == FindingSeverity.Warning);
        Assert.Null(ctl.SavingsText("MapsBroker"));
        Check(ctl);
        Assert.Empty(SelfRepairWarnings());
    }

    /// <summary>
    /// Putting back an owner's own Disabled after "Set to its Windows default" stops the service too: when Windows does not
    /// stop it, the result says it is still running (still paused, for one that was paused), not that starting it failed;
    /// when Windows cannot say, it says that; when it was still stopping, it says it is not stopped yet.
    /// </summary>
    [Theory]
    [InlineData("1051", "it is still running: a service that depends on it has not stopped (Windows error 1051). It stops when Windows restarts")]
    [InlineData("unknown", "Windows could not say whether it is running, so it is not known to have stopped. If it is still running, it stops when Windows restarts")]
    [InlineData("stopping", "it is not stopped yet: it was still stopping 20 seconds after Windows took the stop. It stops when Windows restarts, if not before")]
    [InlineData("paused", "it is still paused: it does not take a stop (Windows error 1052). It stops when Windows restarts")]
    public void PuttingBackDisabledSaysWhenTheServiceDidNotStop(string answer, string still)
    {
        var setDefault = new ServiceChange
        {
            Service = "MapsBroker", OriginalStart = ServiceStartType.Disabled, OriginalRunning = false, NewStart = ServiceStartType.Automatic,
            ChangedUtc = DateTime.UtcNow, Via = ServicePlan.ViaWindowsDefault,
        };
        var (ctl, svc, paths) = New(saved: [setDefault]);
        svc.StopRefused = answer switch
        {
            "unknown" => ServiceControl.Looked(0, asked: false, timeUp: false).Why,
            "stopping" => ServiceControl.Looked(ServiceControl.SERVICE_STOP_PENDING, asked: true, timeUp: true).Why,
            "paused" => ServiceControl.Answered(1052, lookedAgain: false).Why! with { Paused = true },
            _ => FakeServices.DependentsRunning,
        };
        Assert.Equal([("MapsBroker", (string?)("the start type is back to Disabled, but " + still))], ctl.Restore("MapsBroker"));
        Assert.Equal(ServiceStartType.Disabled, svc.Live["MapsBroker"].StartType);
        Assert.Empty(Saved(paths)); // the start type is back: the record goes
        Assert.Contains(_findings, f => f.Title == "Service put back: MapsBroker -> Disabled, but " + still);
    }

    /// <summary>
    /// Both turned off, then only Acme Maps put back while MapsBroker, which it needs, is still Disabled: Windows does not
    /// start it. The result names MapsBroker and says to put it back too. Acme Maps' start type is back and its record goes;
    /// putting MapsBroker back then runs MapsBroker again, and Acme Maps starts when something asks for it (Windows does
    /// not start it with the one it needs).
    /// </summary>
    [Fact]
    public void PuttingBackOnlyTheServiceThatNeedsAnotherNamesTheOneStillDisabled()
    {
        const string Needs = "starting it again failed: it needs MapsBroker, which is still Disabled. " +
                             "Put back MapsBroker too: after that, Windows starts AcmeMaps when something asks for it";
        var (ctl, svc, paths) = New(MapsPair);
        Assert.All(ctl.Apply(["MapsBroker", "AcmeMaps"], "manual"), r => Assert.Null(r.error));
        Assert.Equal([("AcmeMaps", (string?)("the start type is back to Automatic, but " + Needs))], ctl.Restore("AcmeMaps"));
        Assert.Equal((ServiceStartType.Automatic, false), (svc.Live["AcmeMaps"].StartType, svc.Live["AcmeMaps"].Running));
        Assert.Equal(["MapsBroker"], Saved(paths).Select(c => c.Service));
        var finding = Assert.Single(_findings, f => f.Title.StartsWith("Service put back: AcmeMaps", StringComparison.Ordinal));
        Assert.Equal(("Service put back: AcmeMaps -> Automatic, but " + Needs, FindingSeverity.Warning), (finding.Title, finding.Severity));

        Assert.Equal([("MapsBroker", (string?)null)], ctl.Restore("MapsBroker"));
        Assert.Equal((ServiceStartType.Automatic, true), (svc.Live["MapsBroker"].StartType, svc.Live["MapsBroker"].Running));
        Assert.False(svc.Live["AcmeMaps"].Running);
        Assert.Empty(Saved(paths));
    }

    /// <summary>
    /// The one it needs was set to Disabled outside PoolWatch, which has no change of it to put back: the result says so,
    /// and when Windows starts it again.
    /// </summary>
    [Fact]
    public void PuttingBackAServiceWhoseNeededOneIsDisabledOutsidePoolWatchSaysSo()
    {
        var turnedOff = new ServiceChange
        {
            Service = "AcmeMaps", OriginalStart = ServiceStartType.Automatic, OriginalRunning = true, NewStart = ServiceStartType.Disabled,
            ChangedUtc = DateTime.UtcNow, Via = "manual",
        };
        var (ctl, svc, _) = New(s =>
        {
            MapsPair(s);
            s.Live["AcmeMaps"] = s.Live["AcmeMaps"] with { StartType = ServiceStartType.Disabled, Running = false, ProcessId = 0 };
            s.Live["MapsBroker"] = s.Live["MapsBroker"] with { StartType = ServiceStartType.Disabled, Running = false, ProcessId = 0 };
        }, saved: [turnedOff]);
        Assert.Equal([("AcmeMaps", (string?)("the start type is back to Automatic, but starting it again failed: it needs MapsBroker, which is Disabled " +
                                             "outside PoolWatch. Once MapsBroker is no longer Disabled, Windows starts AcmeMaps when something asks for it"))],
            ctl.Restore("AcmeMaps"));
        Assert.Equal(ServiceStartType.Disabled, svc.Live["MapsBroker"].StartType);
    }

    /// <summary>
    /// The one it needs is Disabled outside PoolWatch again, and PoolWatch's change of it is a "Set to its Windows default"
    /// whose original is Disabled: putting that back leaves it Disabled, so the result must not say to put it back too.
    /// Both stopped and Disabled; Acme Maps turned off by PoolWatch while it ran.
    /// </summary>
    private (ServicesController ctl, FakeServices svc) NeededOneBackToDisabledOutside()
    {
        var setDefault = new ServiceChange
        {
            Service = "MapsBroker", OriginalStart = ServiceStartType.Disabled, OriginalRunning = false, NewStart = ServiceStartType.Automatic,
            ChangedUtc = DateTime.UtcNow, Via = ServicePlan.ViaWindowsDefault,
        };
        var turnedOff = new ServiceChange
        {
            Service = "AcmeMaps", OriginalStart = ServiceStartType.Automatic, OriginalRunning = true, NewStart = ServiceStartType.Disabled,
            ChangedUtc = DateTime.UtcNow, Via = "manual",
        };
        var (ctl, svc, _) = New(s =>
        {
            MapsPair(s);
            s.Live["AcmeMaps"] = s.Live["AcmeMaps"] with { StartType = ServiceStartType.Disabled, Running = false, ProcessId = 0 };
            s.Live["MapsBroker"] = s.Live["MapsBroker"] with { StartType = ServiceStartType.Disabled, Running = false, ProcessId = 0 };
        }, saved: [setDefault, turnedOff]);
        return (ctl, svc);
    }

    private const string NeededDisabledOutside = "the start type is back to Automatic, but starting it again failed: it needs MapsBroker, which is Disabled " +
                                                 "outside PoolWatch. Once MapsBroker is no longer Disabled, Windows starts AcmeMaps when something asks for it";

    /// <summary>
    /// Put back Acme Maps alone: MapsBroker's own Put back would leave it Disabled (its original), so the result does not
    /// say "Put back MapsBroker too"; it says MapsBroker is Disabled outside PoolWatch.
    /// </summary>
    [Fact]
    public void PuttingBackOneWhoseNeededServiceItsOwnPutBackLeavesDisabledDoesNotSayToPutThatBack()
    {
        var (ctl, svc) = NeededOneBackToDisabledOutside();
        Assert.Equal([("AcmeMaps", (string?)NeededDisabledOutside)], ctl.Restore("AcmeMaps"));
        Assert.Equal((ServiceStartType.Disabled, false), (svc.Live["MapsBroker"].StartType, svc.Live["MapsBroker"].Running));
        Assert.Equal("MapsBroker", Assert.Single(ctl.Changes).Service); // its own record is kept
    }

    /// <summary>
    /// Put all back: MapsBroker comes first and is already at its original, Disabled, so only its record goes. It is still
    /// Disabled, so Acme Maps' result still names it, as Disabled outside PoolWatch.
    /// </summary>
    [Fact]
    public void PutAllBackStillNamesANeededServiceItsOwnPutBackLeftDisabled()
    {
        var (ctl, svc) = NeededOneBackToDisabledOutside();
        Assert.Equal([("MapsBroker", (string?)null), ("AcmeMaps", NeededDisabledOutside)], ctl.Restore(null));
        Assert.Equal(ServiceStartType.Disabled, svc.Live["MapsBroker"].StartType);
        Assert.Empty(ctl.Changes);
    }

    /// <summary>
    /// Put all back puts MapsBroker back first, but Windows cannot start it; Acme Maps then does not start either, for a
    /// service it needs did not start. MapsBroker is no longer Disabled, so the result gives Windows' reason, not "still
    /// Disabled".
    /// </summary>
    [Fact]
    public void ANeededServiceThisRunPutBackIsNotSaidToBeStillDisabled()
    {
        var (ctl, svc, _) = New(MapsPair);
        Assert.All(ctl.Apply(["MapsBroker", "AcmeMaps"], "manual"), r => Assert.Null(r.error));
        svc.CannotStart.Add("MapsBroker");
        Assert.Equal([("MapsBroker", (string?)"the start type is back to Automatic, but starting it again failed: error 1067"),
                      ("AcmeMaps", "the start type is back to Automatic, but starting it again failed: a service it needs did not start (Windows error 1068)")],
            ctl.Restore(null));
    }

    /// <summary>
    /// Windows did not start Acme Maps for a reason of its own (error 1067), while MapsBroker, which it needs, is still
    /// Disabled: the result gives Windows' reason, as that is not why it did not start.
    /// </summary>
    [Fact]
    public void TheNeededServiceIsNamedOnlyWhenWindowsSaidOneItNeedsDidNotStart()
    {
        var (ctl, svc, _) = New(MapsPair);
        Assert.All(ctl.Apply(["MapsBroker", "AcmeMaps"], "manual"), r => Assert.Null(r.error));
        svc.CannotStart.Add("AcmeMaps");
        Assert.Equal([("AcmeMaps", (string?)"the start type is back to Automatic, but starting it again failed: error 1067")], ctl.Restore("AcmeMaps"));
    }

    /// <summary>
    /// A service PoolWatch set to Disabled but could not stop, which has run on since in the same process, is not reported
    /// as re-enabled outside PoolWatch, also after PoolWatch restarts. Once seen stopped it is read as any other: started
    /// again, even with the same process number, it is reported.
    /// </summary>
    [Fact]
    public void TheSelfRepairCheckDoesNotBlameOutsideForAServiceItsOwnChangeCouldNotStop()
    {
        var (ctl, svc, paths) = New(MapsPair);
        Assert.NotNull(Assert.Single(ctl.Apply(["MapsBroker"], "manual")).error); // not stopped: Acme Maps runs
        Check(ctl);
        Assert.Empty(SelfRepairWarnings());
        Check(new ServicesController(paths, LogOf(paths), _findings.Add, _ => null, _ => null, svc, Host())); // PoolWatch started again
        Assert.Empty(SelfRepairWarnings());

        // Acme Maps stops, then MapsBroker: off as PoolWatch meant, and its record no longer says it ran on.
        svc.Live["AcmeMaps"] = svc.Live["AcmeMaps"] with { Running = false };
        svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { Running = false, ProcessId = 0 };
        Check(ctl);
        Assert.Empty(SelfRepairWarnings());
        Assert.Null(Assert.Single(Saved(paths)).NotStoppedPid);

        svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { Running = true, ProcessId = 40 };
        Check(ctl);
        Assert.Equal(["Service re-enabled outside PoolWatch: MapsBroker is Disabled, running again (you turned it off). Turn it off again with " +
                      "Turn off; Put back works again after that on the Services tab."], SelfRepairWarnings());
    }

    /// <summary>
    /// When the process it ran in was not known (it was not running when the services were read, but was when Turn off
    /// asked it to stop), the record's mark is 0: any process it runs in at Disabled is the one it kept running in, so
    /// PoolWatch's own change is not blamed on something outside it. Windows does not start a Disabled service, so it
    /// cannot have been started again in another one without its start type changing, which is reported.
    /// </summary>
    [Fact]
    public void AServiceWhoseProcessWasNotKnownIsReadAsKeptRunningInAnyProcess()
    {
        var (ctl, svc, paths) = New(); // MapsBroker, its process not known
        svc.StopRefused = ServiceControl.Answered(1061, lookedAgain: true).Why;
        Assert.NotNull(Assert.Single(ctl.Apply(["MapsBroker"], "manual")).error);
        Assert.Equal(0, Assert.Single(Saved(paths)).NotStoppedPid);
        svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { ProcessId = 55 };
        Check(ctl);
        Assert.Empty(SelfRepairWarnings());
        Assert.Equal(0, Assert.Single(Saved(paths)).NotStoppedPid);
    }

    /// <summary>Such a service set to another start type, or running in another process, was switched back on: reported.</summary>
    [Theory]
    [InlineData(ServiceStartType.Manual, 40)]
    [InlineData(ServiceStartType.Automatic, 40)]
    [InlineData(ServiceStartType.Disabled, 77)]
    public void TheSelfRepairCheckReportsAServiceItCouldNotStopOnceItIsChangedOrStartedAgain(ServiceStartType start, int pid)
    {
        var (ctl, svc, paths) = New(MapsPair);
        Assert.NotNull(Assert.Single(ctl.Apply(["MapsBroker"], "manual")).error);
        svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { StartType = start, ProcessId = pid };
        Check(ctl);
        Assert.StartsWith($"Service re-enabled outside PoolWatch: MapsBroker is {start}, running again (you turned it off).", Assert.Single(SelfRepairWarnings()));
        Assert.Null(Assert.Single(Saved(paths)).NotStoppedPid);
    }

    /// <summary>
    /// Turn off set it to Disabled but it was not stopped, and at the self-repair check it is still starting, stopping or
    /// paused: not seen stopped, so its mark stays (and the State column does not call it off). When it then runs on at
    /// Disabled, that is PoolWatch's own change, not something outside it. Once it is seen STOPPED the mark goes, and a
    /// later start is reported.
    /// </summary>
    [Theory]
    [InlineData("starting")] // caught starting, still starting after the wait; then it runs
    [InlineData("stopping")] // running, its stop taken, still stopping after the wait; then it runs on after all
    [InlineData("paused")]   // paused, its stop taken, still paused after the wait; then it is resumed
    public void AServiceNotStoppedYetAtTheCheckKeepsItsMarkUntilItIsSeenStopped(string state)
    {
        bool running = state == "stopping";
        var (ctl, svc, paths) = New(s =>
        {
            s.Add("MapsBroker", ServiceStartType.Automatic, running, pid: running ? 40 : 0);
            s.Live["MapsBroker"] = s.Live["MapsBroker"] with { NeitherRunningNorStopped = !running };
        });
        svc.StopRefused = state switch
        {
            "starting" => ServiceControl.Looked(ServiceControl.SERVICE_START_PENDING, asked: false, timeUp: true).Why,
            "stopping" => ServiceControl.Looked(ServiceControl.SERVICE_STOP_PENDING, asked: true, timeUp: true).Why,
            _ => ServiceControl.Looked(ServiceControl.SERVICE_PAUSED, asked: true, timeUp: true).Why,
        };
        Assert.StartsWith("set to Disabled, not stopped yet: ", Assert.Single(ctl.Apply(["MapsBroker"], "manual")).error);
        int? mark = Assert.Single(Saved(paths)).NotStoppedPid;
        Assert.NotNull(mark);

        svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { Running = false, ProcessId = 0, NeitherRunningNorStopped = true }; // at the check
        Check(ctl);
        Assert.Equal(mark, Assert.Single(Saved(paths)).NotStoppedPid);
        Assert.StartsWith("set to Disabled, not known to have stopped (turned off by PoolWatch ",
            ServicePlan.StateText(ctl.Options().Single(o => o.Service.Name == "MapsBroker")));

        svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { Running = true, ProcessId = 40, NeitherRunningNorStopped = false };
        Check(ctl);
        Assert.Empty(SelfRepairWarnings());

        svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { Running = false, ProcessId = 0 }; // STOPPED
        Check(ctl);
        Assert.Null(Assert.Single(Saved(paths)).NotStoppedPid);
        svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { Running = true, ProcessId = 40 };
        Check(ctl);
        Assert.Equal(["Service re-enabled outside PoolWatch: MapsBroker is Disabled, running again (you turned it off). Turn it off again with " +
                      "Turn off; Put back works again after that on the Services tab."], SelfRepairWarnings());
    }

    /// <summary>
    /// A self-repair check that comes while Turn off is under way (the record saved, the start type set, the stop not
    /// done yet, so Disabled and still running with no mark) skips the service: PoolWatch's own change is never read as
    /// switched back on outside it, however long the change takes (the last of several services turned off at once, say).
    /// The record says the change is under way in this PoolWatch until it is done; then the check reads it again, and its
    /// mark holds.
    /// </summary>
    [Fact]
    public void TheSelfRepairCheckSkipsAServiceWhoseTurnOffIsUnderWay()
    {
        var (ctl, svc, paths) = New(MapsPair);
        svc.OnApply = () =>
        {
            var record = Assert.Single(Saved(paths));
            Assert.Null(record.NotStoppedPid); // saved, not marked yet
            Assert.Equal(new ChangeUnderWay { Pid = Tray, ProcessStarted = 1, TickMs = _clock.NowMs }, record.UnderWay);
            svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { StartType = ServiceStartType.Disabled };
            _clock.NowMs += 60 * 60 * 1000; // an hour on
            Check(ctl);
        };
        Assert.NotNull(Assert.Single(ctl.Apply(["MapsBroker"], "manual")).error); // not stopped: Acme Maps runs
        Assert.Empty(SelfRepairWarnings());
        var done = Assert.Single(Saved(paths));
        Assert.Equal((40, (ChangeUnderWay?)null), (done.NotStoppedPid, done.UnderWay));
        Check(ctl);
        Assert.Empty(SelfRepairWarnings());
    }

    /// <summary>
    /// A Turn off whose change throws still lets go of the service: the self-repair check reads it again (its record still
    /// says the change is under way, but in this PoolWatch, which reads its own changes from what it is doing now), so a
    /// switch-on is not hidden for good.
    /// </summary>
    [Fact]
    public void ATurnOffThatThrowsLetsTheSelfRepairCheckReadTheServiceAgain()
    {
        var (ctl, svc, _) = New();
        svc.OnApply = () => throw new InvalidOperationException("the service control manager went away");
        Assert.Throws<InvalidOperationException>(() => ctl.Apply(["MapsBroker"], "manual"));
        Assert.Equal("MapsBroker", Assert.Single(ctl.Changes).Service); // its record was saved first
        svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { StartType = ServiceStartType.Disabled };
        Assert.NotNull(Assert.Single(ctl.Changes).UnderWay); // its own mark stays, as the change never ended
        ctl.CheckSelfRepair(); // at once: this PoolWatch's own mark does not hold its check back
        Assert.StartsWith("Service re-enabled outside PoolWatch: MapsBroker is Disabled, running again", Assert.Single(SelfRepairWarnings()));
    }

    /// <summary>MapsBroker at Disabled, not seen stopped: paused (or still starting or stopping), its process not read.</summary>
    private static ServiceView NotSeenStopped(ServiceView s) => s with { StartType = ServiceStartType.Disabled, Running = false, ProcessId = 0, NeitherRunningNorStopped = true };

    /// <summary>
    /// A service at Disabled that is not seen stopped is not off yet: Turn off's question counts it as one to change, and
    /// Turn off and the run at start ask for its stop again, through the same gate, never "off already". The same whether
    /// PoolWatch turned it off before (its first original is kept) or it was Disabled outside PoolWatch.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ADisabledServiceNotSeenStoppedIsNotOffSoTurnOffAsksForItsStopAgain(bool poolWatchTurnedItOff)
    {
        ServiceChange[]? saved = poolWatchTurnedItOff
            ? [new ServiceChange { Service = "MapsBroker", OriginalStart = ServiceStartType.Automatic, OriginalRunning = true, NewStart = ServiceStartType.Disabled,
                                   ChangedUtc = DateTime.UtcNow.AddHours(-1), Via = "manual", NotStoppedPid = 40 }]
            : null;
        var (ctl, svc, paths) = New(s => s.Live["MapsBroker"] = NotSeenStopped(s.Live["MapsBroker"]), saved);
        var (change, offAlready) = Ui.ServicesPageText.ApplyPlan(["MapsBroker"], ctl.Options());
        Assert.Equal(["MapsBroker"], change);
        Assert.Equal(0, offAlready);
        Assert.Equal([("MapsBroker", (string?)null)], ctl.Apply(["MapsBroker"], "manual"));
        Assert.Equal(["apply MapsBroker"], svc.Calls);
        Assert.Equal((ServiceStartType.Disabled, false, false),
            (svc.Live["MapsBroker"].StartType, svc.Live["MapsBroker"].Running, svc.Live["MapsBroker"].NeitherRunningNorStopped));
        var record = Assert.Single(Saved(paths));
        Assert.Equal((poolWatchTurnedItOff ? ServiceStartType.Automatic : ServiceStartType.Disabled, (int?)null), (record.OriginalStart, record.NotStoppedPid));

        // Paused again when the profile applied at start runs: turned off, not "already off".
        svc.Live["MapsBroker"] = NotSeenStopped(svc.Live["MapsBroker"]);
        var run = ctl.ApplyProfile(new ServiceProfile("P", ["MapsBroker"]), ProfileAtStart.Via("P"));
        Assert.Equal(["MapsBroker"], run.TurnedOff);
        Assert.Empty(run.AlreadyOff);
        Assert.Equal(["apply MapsBroker", "apply MapsBroker"], svc.Calls);
    }

    /// <summary>
    /// A service that is paused when Windows refuses its stop is set to Disabled and said to be still paused, never still
    /// running (the running dependents named when Windows refused for them). It is not off yet, so turning it off again
    /// asks for its stop again. Its result says that this stops it after error 1051 (once that service has stopped), never
    /// after error 1052: a service that does not take a stop stops when Windows restarts.
    /// </summary>
    [Theory]
    [InlineData(1051, "set to Disabled, but still paused: AcmeMaps depends on it and is running. It stops when Windows restarts, or when you turn it " +
                      "off again after that service has stopped")]
    [InlineData(1052, "set to Disabled, but still paused: it does not take a stop (Windows error 1052). It stops when Windows restarts")]
    public void APausedServiceWhoseStopWindowsRefusesIsSaidToBeStillPaused(int answer, string expected)
    {
        var (ctl, svc, paths) = New(s =>
        {
            MapsPair(s);
            s.Live["MapsBroker"] = s.Live["MapsBroker"] with { Running = false, ProcessId = 0, NeitherRunningNorStopped = true }; // Automatic, paused
        });
        svc.StopRefused = ServiceControl.Answered(answer, lookedAgain: false).Why! with { Paused = true };
        Assert.Equal([("MapsBroker", (string?)expected)], ctl.Apply(["MapsBroker"], "manual"));
        Assert.True(ServicePlan.IsNotStoppedText(expected));
        Assert.Equal(ServiceStartType.Disabled, svc.Live["MapsBroker"].StartType);
        Assert.Equal(0, Assert.Single(Saved(paths)).NotStoppedPid); // the process of a paused service is not read
        Assert.StartsWith("set to Disabled, not known to have stopped (turned off by PoolWatch ",
            ServicePlan.StateText(ctl.Options().Single(o => o.Service.Name == "MapsBroker")));

        // Turned off again once Windows can take the stop (Acme Maps has stopped): asked for again, and done.
        svc.StopRefused = null;
        svc.Live["AcmeMaps"] = svc.Live["AcmeMaps"] with { Running = false };
        Assert.Equal([("MapsBroker", (string?)null)], ctl.Apply(["MapsBroker"], "manual"));
        Assert.Equal(["apply MapsBroker", "apply MapsBroker"], svc.Calls);
        Assert.Null(Assert.Single(Saved(paths)).NotStoppedPid);
    }

    private const string MapsBrokerSwitchedOn = "Service re-enabled outside PoolWatch: MapsBroker is Automatic, running again (you turned it off). " +
                                                "Turn it off again with Turn off, or keep it this way with Put back on the Services tab.";

    /// <summary>
    /// The self-repair warning comes once per state a service is switched back to, but a Turn off starts that again: once
    /// the owner has turned the service off again, in this PoolWatch or from the command line (another PoolWatch, whose
    /// record the check reads), a switch back on to the same state outside PoolWatch is reported again, whether that Turn
    /// off stopped it (and nothing looked before it was switched on again) or not (one that depends on it runs: it is then
    /// read as that Turn off left it).
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ASwitchOnAfterATurnOffIsReportedAgainInAStateReportedBefore(bool notStopped, bool fromCommandLine)
    {
        var (ctl, svc, paths) = New(notStopped ? MapsPair : (Action<FakeServices>?)null);
        void SwitchedOn() => svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { StartType = ServiceStartType.Automatic, Running = true, ProcessId = 40 };
        Assert.Equal(notStopped, Assert.Single(ctl.Apply(["MapsBroker"], "manual")).error is not null);
        SwitchedOn();
        Check(ctl);
        Assert.Equal([MapsBrokerSwitchedOn], SelfRepairWarnings());

        // Turned off again; --service-off runs a PoolWatch of its own, with its own memory, on the same record.
        var again = fromCommandLine ? new ServicesController(paths, LogOf(paths), _ => { }, _ => null, _ => null, svc, Host()) : ctl;
        Assert.Equal(notStopped, Assert.Single(again.Apply(["MapsBroker"], fromCommandLine ? "command line" : "manual")).error is not null);
        if (notStopped)
        {
            Check(ctl); // running on at Disabled, as Turn off left it: nothing to report
            Assert.Equal([MapsBrokerSwitchedOn], SelfRepairWarnings());
        }
        SwitchedOn();
        Check(ctl);
        Assert.Equal([MapsBrokerSwitchedOn, MapsBrokerSwitchedOn], SelfRepairWarnings());
    }

    /// <summary>
    /// A Turn off whose change fails takes its record back and changes nothing, so a state the check reported already is
    /// not reported again.
    /// </summary>
    [Fact]
    public void ATurnOffThatFailsDoesNotRepeatAWarningGivenBefore()
    {
        var (ctl, svc, _) = New();
        Assert.Null(Assert.Single(ctl.Apply(["MapsBroker"], "manual")).error);
        svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { StartType = ServiceStartType.Automatic, Running = true, ProcessId = 40 };
        Check(ctl);
        Assert.Equal([MapsBrokerSwitchedOn], SelfRepairWarnings());

        svc.ApplyError = "access denied";
        Assert.Equal([("MapsBroker", (string?)"access denied")], ctl.Apply(["MapsBroker"], "manual"));
        Assert.Equal(["apply MapsBroker", "apply MapsBroker"], svc.Calls);
        Check(ctl);
        Assert.Equal([MapsBrokerSwitchedOn], SelfRepairWarnings());
    }

    /// <summary>
    /// A self-repair check that reads the services while a Turn off of one it reported is under way skips it, and keeps
    /// what it reported of it: when that Turn off then fails, it takes its record back, so the next check does not report
    /// a state it reported already.
    /// </summary>
    [Fact]
    public async Task ACheckDuringATurnOffThatFailsDoesNotRepeatAWarningGivenBefore()
    {
        var (ctl, svc, _) = New();
        Assert.Null(Assert.Single(ctl.Apply(["MapsBroker"], "manual")).error);
        svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { StartType = ServiceStartType.Automatic, Running = true, ProcessId = 40 };
        Check(ctl);
        Assert.Equal([MapsBrokerSwitchedOn], SelfRepairWarnings());

        // The next check reads the services just as a Turn off (on another thread) has saved its record and waits for its
        // change, which then fails; it ends before that change does.
        svc.ApplyError = "access denied";
        using var applying = new ManualResetEventSlim();
        using var checkDone = new ManualResetEventSlim();
        Task<List<(string service, string? error)>>? turnOff = null;
        svc.OnApply = () =>
        {
            applying.Set();
            Assert.True(checkDone.Wait(TimeSpan.FromSeconds(30)), "the check did not end");
        };
        svc.OnEnumerate = () =>
        {
            turnOff = Task.Run(() => ctl.Apply(["MapsBroker"], "manual"));
            Assert.True(applying.Wait(TimeSpan.FromSeconds(30)), "the Turn off did not get under way");
        };
        Check(ctl);
        checkDone.Set();
        Assert.Equal([("MapsBroker", (string?)"access denied")], await turnOff!);
        Assert.Equal([MapsBrokerSwitchedOn], SelfRepairWarnings());

        Check(ctl);
        Assert.Equal([MapsBrokerSwitchedOn], SelfRepairWarnings());
    }

    /// <summary>
    /// A service the check finds no longer installed starts its warnings again: installed again later (its record still
    /// there) and in the state reported before, it is reported again.
    /// </summary>
    [Fact]
    public void AServiceNoLongerInstalledIsReportedAgainOnceItIsBackInAStateReportedBefore()
    {
        var (ctl, svc, _) = New(s => s.Add("Spooler", ServiceStartType.Automatic, running: true)); // the PC has other services
        Assert.Null(Assert.Single(ctl.Apply(["MapsBroker"], "manual")).error);
        svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { StartType = ServiceStartType.Automatic, Running = true, ProcessId = 40 };
        var switchedOn = svc.Live["MapsBroker"];
        Check(ctl);
        Assert.Equal([MapsBrokerSwitchedOn], SelfRepairWarnings());

        svc.Live.Remove("MapsBroker"); // uninstalled
        Check(ctl);
        Assert.Equal([MapsBrokerSwitchedOn], SelfRepairWarnings());

        svc.Live["MapsBroker"] = switchedOn; // installed again, as it was
        Check(ctl);
        Assert.Equal([MapsBrokerSwitchedOn, MapsBrokerSwitchedOn], SelfRepairWarnings());
    }

    /// <summary>
    /// A Turn off from the command line runs in a PoolWatch of its own, whose skip of a Turn off under way this one cannot
    /// see. Its record says the change is under way in that process, from before it sets the start type until it is done.
    /// A self-repair check here meanwhile does not read it as switched back on outside PoolWatch, before or after the start
    /// type is set, however long Windows holds the stop; it looks again soon, and once the change is done a switch-on is
    /// reported at that next look, not a whole check later.
    /// </summary>
    [Theory]
    [InlineData(false, 40)]  // the start type not set yet: still Automatic and running
    [InlineData(true, 40)]   // set to Disabled, the stop not done yet
    [InlineData(true, 100)]  // set to Disabled, both stop requests held by Windows while a service is busy
    public void ATurnOffFromTheCommandLineStillUnderWayIsNotReadAsSwitchedOnOutside(bool startTypeSet, int stopSeconds)
    {
        var (tray, svc, paths) = New();
        _clock.Running.Add((CommandLine, 1));
        var commandLine = new ServicesController(paths, LogOf(paths), _ => { }, _ => null, _ => null, svc, Host(CommandLine));
        svc.OnApply = () =>
        {
            var record = Assert.Single(Saved(paths));
            Assert.Equal((null, CommandLine), (record.NotStoppedPid, record.UnderWay?.Pid)); // saved, under way in that PoolWatch
            if (startTypeSet) svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { StartType = ServiceStartType.Disabled };
            _clock.NowMs += stopSeconds * 1000L;
            tray.CheckSelfRepair(); // the tray's check, while the stop is under way
        };
        Assert.Null(Assert.Single(commandLine.Apply(["MapsBroker"], "command line")).error);
        Assert.Empty(SelfRepairWarnings());
        Assert.Null(Assert.Single(Saved(paths)).UnderWay); // done

        // Switched on again outside PoolWatch at once: the tray's next look, within LookAgainMs, reports it.
        svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { StartType = ServiceStartType.Automatic, Running = true, ProcessId = 40 };
        _clock.NowMs += ServicesController.LookAgainMs;
        tray.CheckSelfRepair();
        Assert.Equal([MapsBrokerSwitchedOn], SelfRepairWarnings());
    }

    /// <summary>A change under way in another PoolWatch: its record as that PoolWatch saved it before it acted.</summary>
    private ServiceChange UnderWayIn(int pid, long tickMs) => new()
    {
        Service = "MapsBroker", OriginalStart = ServiceStartType.Automatic, OriginalRunning = true, NewStart = ServiceStartType.Disabled,
        ChangedUtc = DateTime.UtcNow, Via = "command line", UnderWay = new ChangeUnderWay { Pid = pid, ProcessStarted = 1, TickMs = tickMs },
    };

    /// <summary>
    /// The check holds back for another PoolWatch's change only while that change can still be under way: the process
    /// that marked it still runs (the same one: a later process given its number is not it), and the change began less
    /// than the longest a change can take ago, on Windows' own clock. A PoolWatch that ended mid-change, or that hangs past
    /// that, no longer holds a switch-on back.
    /// </summary>
    [Theory]
    [InlineData("runs", false)]
    [InlineData("ended", true)]
    [InlineData("reused", true)]  // its number now another process's
    [InlineData("hangs", true)]   // still running, longer than a change can take
    [InlineData("future", true)]  // a mark from a clock that is not this boot's
    public void AnotherPoolWatchsMarkHoldsTheCheckBackOnlyWhileItsChangeCanBeUnderWay(string how, bool reported)
    {
        long atMost = (long)ServicesController.ChangeAtMost(ServiceControl.DefaultControlWaitSeconds).TotalMilliseconds;
        long began = how switch { "hangs" => _clock.NowMs - atMost - 1, "future" => _clock.NowMs + 60_000, _ => _clock.NowMs - 1000 };
        var (tray, svc, _) = New(s => s.Live["MapsBroker"] = s.Live["MapsBroker"] with { ProcessId = 40 }, saved: [UnderWayIn(CommandLine, began)]);
        if (how is not "ended") _clock.Running.Add((CommandLine, how == "reused" ? 2 : 1));
        _clock.NowMs += 1; // the check comes now
        tray.CheckSelfRepair();
        Assert.Equal(reported ? [MapsBrokerSwitchedOn] : [], SelfRepairWarnings());
    }

    /// <summary>
    /// The longest a change can take follows Windows' wait on a busy service on this PC (ServicesPipeTimeout): a change
    /// from the command line whose stop requests Windows holds 3 minutes each still holds the check back, as long as it can
    /// still be under way.
    /// </summary>
    [Fact]
    public void ALongerServicesPipeTimeoutHoldsTheCheckBackForAsLong()
    {
        _clock.ControlWaitSeconds = 180;
        long began = _clock.NowMs - 300_000; // 5 minutes ago: more than 105 seconds, less than 20 + 20 + 2 * 180 + 5
        var (tray, _, _) = New(s => s.Live["MapsBroker"] = s.Live["MapsBroker"] with { ProcessId = 40 }, saved: [UnderWayIn(CommandLine, began)]);
        _clock.Running.Add((CommandLine, 1));
        tray.CheckSelfRepair();
        Assert.Empty(SelfRepairWarnings());
        _clock.ControlWaitSeconds = ServiceControl.DefaultControlWaitSeconds;
        _clock.NowMs += ServicesController.LookAgainMs;
        tray.CheckSelfRepair();
        Assert.Equal([MapsBrokerSwitchedOn], SelfRepairWarnings());
    }

    /// <summary>
    /// The longest a change can take is the sum of its waits: the wait to settle and the wait for the stop, Windows' wait
    /// on a busy service for each stop request (30 seconds unless ServicesPipeTimeout sets it longer), and the margin for
    /// the rest. One left out, the check could read a change another PoolWatch still has under way, but hanging or slow, as
    /// switched back on outside PoolWatch. With Windows' default wait that is 105 seconds.
    /// </summary>
    [Fact]
    public void TheLongestChangeIsTheSumOfItsWaits()
    {
        foreach (int wait in new[] { ServiceControl.DefaultControlWaitSeconds, 180 })
            Assert.Equal(TimeSpan.FromSeconds(ServiceControl.StopWaitSeconds + ServiceControl.StopWaitSeconds
                                              + ServiceControl.StopRequestsAtMost * wait + ServicesController.ChangeMarginSeconds),
                ServicesController.ChangeAtMost(wait));
        Assert.Equal(TimeSpan.FromSeconds(105), ServicesController.ChangeAtMost(ServiceControl.DefaultControlWaitSeconds));
    }

    /// <summary>
    /// The check is timed on the host's clock alone (<see cref="IServicesHost.NowMs"/>, which only goes forward), every 5
    /// minutes: not before 5 minutes on that clock, and at once when they are up, with no other clock read. That the real
    /// host's clock is Environment.TickCount64, which setting the system clock does not move, is
    /// <c>ServiceControlTests.TheRealCheckThatAProcessStillRunsKnowsThisOne</c>.
    /// </summary>
    [Fact]
    public void TheCheckRunsEveryFiveMinutesOnTheHostsClockAlone()
    {
        var (ctl, svc, _) = New();
        Assert.Null(Assert.Single(ctl.Apply(["MapsBroker"], "manual")).error);
        ctl.CheckSelfRepair();
        svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { StartType = ServiceStartType.Automatic, Running = true, ProcessId = 40 };
        _clock.NowMs += ServicesController.SelfRepairEveryMs - 1;
        ctl.CheckSelfRepair();
        Assert.Empty(SelfRepairWarnings()); // not due yet
        _clock.NowMs += 1;
        ctl.CheckSelfRepair();
        Assert.Equal([MapsBrokerSwitchedOn], SelfRepairWarnings());
    }

    /// <summary>
    /// "Saved about N cores over the hour after" is timed on the same clock that only goes forward: the finding comes once
    /// that clock has moved an hour since the Turn off, not before, whatever the system clock does meanwhile.
    /// </summary>
    [Fact]
    public void TheSavedFindingComesAnHourAfterTheTurnOffOnTheClockThatOnlyGoesForward()
    {
        var (ctl, _, _) = New();
        Assert.Null(Assert.Single(ctl.Apply(["MapsBroker"], "manual")).error);
        IReadOnlyList<Core.Model.ServiceEntry> none = [];
        bool Reported() => _findings.Any(f => f.Title.StartsWith("Service turned off an hour ago: MapsBroker", StringComparison.Ordinal));
        for (int sec = 10; sec < 3600; sec += 10) { _clock.NowMs += 10_000; ctl.ObserveSavings(none); }
        Assert.False(Reported());
        Assert.Contains("so far (59 min measured)", ctl.SavingsText("MapsBroker"));
        _clock.NowMs += 10_000;
        ctl.ObserveSavings(none);
        Assert.True(Reported());
    }

    /// <summary>
    /// "--service-restore" while the app runs: the Put back from the command line marks its record before it acts, so the
    /// app's check, reading the service already back on but the record still there, does not report it as switched back
    /// on outside PoolWatch; once the Put back is done the record is gone, and there is nothing to report.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)] // the check read the record just before the Put back marked it
    public void APutBackFromTheCommandLineIsNotReadAsSwitchedOnOutside(bool readBeforeTheMark)
    {
        var (tray, svc, paths) = New();
        Assert.Null(Assert.Single(tray.Apply(["MapsBroker"], "manual")).error);
        _clock.Running.Add((CommandLine, 1));
        var commandLine = new ServicesController(paths, LogOf(paths), _ => { }, _ => null, _ => null, svc, Host(CommandLine));
        List<(string service, string? error)>? restored = null;
        if (readBeforeTheMark)
        {
            // The app's check reads the record, then the whole Put back runs while the check reads the services.
            svc.OnEnumerate = () => restored = commandLine.Restore("MapsBroker");
            Check(tray);
        }
        else
        {
            // The app's check comes while the Put back is under way: the service back on and started again, its record still there.
            svc.OnRestore = () =>
            {
                Assert.Equal(CommandLine, Assert.Single(Saved(paths)).UnderWay?.Pid); // marked before it acted
                tray.CheckSelfRepair(); // its first check: due now
            };
            restored = commandLine.Restore("MapsBroker");
        }
        Assert.Equal([("MapsBroker", (string?)null)], restored);
        Assert.Equal((ServiceStartType.Automatic, true), (svc.Live["MapsBroker"].StartType, svc.Live["MapsBroker"].Running));
        Assert.Empty(Saved(paths));
        Check(tray);
        Assert.Empty(SelfRepairWarnings());
    }

    /// <summary>
    /// A Put back that fails keeps its record, and takes its under-way mark off again, so the check reads the service as
    /// before.
    /// </summary>
    [Fact]
    public void APutBackThatFailsTakesItsMarkOff()
    {
        var (ctl, svc, paths) = New();
        Assert.Null(Assert.Single(ctl.Apply(["MapsBroker"], "manual")).error);
        svc.OnRestoreStart = () => throw new InvalidOperationException("access denied");
        Assert.Equal([("MapsBroker", (string?)"access denied")], ctl.Restore("MapsBroker"));
        var kept = Assert.Single(Saved(paths));
        Assert.Null(kept.UnderWay);
    }

    /// <summary>
    /// "--service-restore" while the app runs puts MapsBroker back (marked, restored, started again), but cannot remove its
    /// record (the file held open by a reader), and exits. The record still carries that Put back's mark, so the app's
    /// check never reports PoolWatch's own Put back as switched back on outside it, then or later; the State column says
    /// a Put back began, and the app's own Put back finishes it (it finds the service back and removes the record).
    /// </summary>
    [Fact]
    public void ACommandLinePutBackWhoseRecordWasNotRemovedIsNotReportedAfterItExits()
    {
        var (tray, svc, paths) = New();
        Assert.Null(Assert.Single(tray.Apply(["MapsBroker"], "manual")).error);
        _clock.Running.Add((CommandLine, 1));
        var commandLine = new ServicesController(paths, LogOf(paths), _ => { }, _ => null, _ => null, svc, Host(CommandLine));
        FileStream? held = null;
        svc.OnRestore = () => held = new FileStream(ChangeFile(paths), FileMode.Open, FileAccess.Read, FileShare.Read);
        try { Assert.Equal([("MapsBroker", (string?)null)], commandLine.Restore("MapsBroker")); }
        finally { held?.Dispose(); }
        Assert.Equal((ServiceStartType.Automatic, true), (svc.Live["MapsBroker"].StartType, svc.Live["MapsBroker"].Running));
        var left = Assert.Single(Saved(paths));
        Assert.Equal((CommandLine, true), (left.UnderWay?.Pid, left.UnderWay?.PutBack));
        _clock.Running.Remove((CommandLine, 1)); // the command line has exited

        Check(tray);
        Check(tray);
        Assert.Empty(SelfRepairWarnings());
        Assert.StartsWith("running (PoolWatch began to put back its change of ", ServicePlan.StateText(tray.Options().Single(o => o.Service.Name == "MapsBroker")));

        Assert.Equal([("MapsBroker", (string?)null)], tray.Restore("MapsBroker"));
        Assert.Empty(Saved(paths));
        Assert.Contains("was already back to Automatic", _findings.Last().Title);
    }

    /// <summary>
    /// A Put back from the command line whose start takes longer than the bound on a change (Windows starts the services
    /// it depends on first): the app's check, past that bound while the Put back still runs, reads the service at its
    /// original start type, as that Put back leaves it, and does not report it as switched back on outside PoolWatch.
    /// </summary>
    [Fact]
    public void APutBackThatOutlastsTheBoundOnAChangeIsNotReported()
    {
        var (tray, svc, paths) = New();
        Assert.Null(Assert.Single(tray.Apply(["MapsBroker"], "manual")).error);
        _clock.Running.Add((CommandLine, 1));
        var commandLine = new ServicesController(paths, LogOf(paths), _ => { }, _ => null, _ => null, svc, Host(CommandLine));
        svc.OnRestore = () =>
        {
            Assert.True(Assert.Single(Saved(paths)).UnderWay?.PutBack);
            _clock.NowMs += (long)ServicesController.ChangeAtMost(_clock.ControlWaitSeconds).TotalMilliseconds + 1;
            tray.CheckSelfRepair(); // due, and past the bound on a change: the mark alone no longer holds it back
        };
        Assert.Equal([("MapsBroker", (string?)null)], commandLine.Restore("MapsBroker"));
        Assert.Empty(SelfRepairWarnings());
        Assert.Empty(Saved(paths));
    }

    /// <summary>
    /// A Turn off's mark is not a Put back's: a record whose Turn off from the command line was left marked (its process
    /// gone), with the service switched back on outside PoolWatch to the original start type, is reported as usual.
    /// </summary>
    [Fact]
    public void ATurnOffsLeftOverMarkDoesNotHideASwitchOn()
    {
        var (tray, svc, paths) = New();
        _clock.Running.Add((CommandLine, 1));
        var commandLine = new ServicesController(paths, LogOf(paths), _ => { }, _ => null, _ => null, svc, Host(CommandLine));
        FileStream? held = null;
        svc.OnApply = () => held = new FileStream(ChangeFile(paths), FileMode.Open, FileAccess.Read, FileShare.Read);
        try { Assert.Null(Assert.Single(commandLine.Apply(["MapsBroker"], "manual")).error); }
        finally { held?.Dispose(); }
        Assert.Equal((CommandLine, false), (Assert.Single(Saved(paths)).UnderWay?.Pid, Assert.Single(Saved(paths)).UnderWay?.PutBack));
        _clock.Running.Remove((CommandLine, 1));
        svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { StartType = ServiceStartType.Automatic, Running = true, ProcessId = 40 };
        Check(tray);
        Assert.Equal([MapsBrokerSwitchedOn], SelfRepairWarnings());
    }

    /// <summary>
    /// The uninstaller's path for a service: a record whose running state is not known is put back through the Undo the
    /// uninstaller runs (ServicesSource, then Program.UninstallSummary): the summary says it was put back only in part,
    /// that it was not started again and why, never a plain "Put back"; so does one whose start failed (Windows error
    /// 1067), and a record of the published beta that says "not running" (that version saved it also for a service it
    /// turned off while paused or starting). A plain one stays plain.
    /// </summary>
    [Theory]
    [InlineData("not known")]
    [InlineData("start failed")]
    [InlineData("beta, not running")]
    [InlineData("plain")]
    public void TheUninstallSummarySaysWhatAPutBackDidNotDo(string how)
    {
        var record = new ServiceChange
        {
            Service = "MapsBroker", DisplayName = "Downloaded Maps Manager", OriginalStart = ServiceStartType.Automatic, OriginalRunning = how is "start failed" or "plain",
            OriginalRunningNotKnown = how == "not known", NewStart = ServiceStartType.Disabled, ChangedUtc = new DateTime(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc), Via = "manual",
        };
        string? beta = how != "beta, not running" ? null : """
            [{"service":"MapsBroker","displayName":"Downloaded Maps Manager","originalStart":"Automatic","originalRunning":false,"newStart":"Disabled",
              "changedUtc":"2026-10-30T09:00:00Z","via":"manual","beforeCores":null,"beforePageFaultsPerSec":null,"notStoppedPid":null}]
            """;
        var (ctl, svc, _) = New(s =>
        {
            s.Live["MapsBroker"] = s.Live["MapsBroker"] with { StartType = ServiceStartType.Disabled, Running = false };
            if (how == "start failed") s.CannotStart.Add("MapsBroker");
        }, saved: beta is null ? [record] : null, file: beta);
        var source = new Engine.Changes.ServicesSource(ctl);
        var change = Assert.Single(source.List());
        var result = source.Undo(change, confirmed: false);
        Assert.True(result.Done);
        Assert.Equal(how == "plain", svc.Live["MapsBroker"].Running); // started again only when it was known to run
        string line = Assert.Single(Program.UninstallSummary([change], null, [(change, result)], DateTime.Now).Split(Environment.NewLine),
            l => l.Contains("ut back: ", StringComparison.Ordinal));
        Assert.Equal(how switch
        {
            "start failed" => "Partly put back: Turned off Downloaded Maps Manager (MapsBroker). The start type is back to Automatic, but starting it again failed: error 1067.",
            "plain" => "Put back: Turned off Downloaded Maps Manager (MapsBroker)",
            _ => "Partly put back: Turned off Downloaded Maps Manager (MapsBroker). The start type is back to Automatic, but " + ServicePlan.RunningNotKnownText + ".",
        }, line);
    }

    /// <summary>
    /// --service-off and --service-restore exit with a code that says whether everything asked for was done: 0 when it
    /// was (a service off already, and a Put back whose start type is back but that was not started again, count as
    /// done, and so does "all" with nothing recorded); 10 when a service was not turned off, or not stopped, or the services
    /// could not be read; 8 when a service was not put back (its record kept), also when another was put back in the same
    /// run, and when no change of the service named is recorded. Each result is printed.
    /// </summary>
    [Fact]
    public void TheCommandLineSaysByItsExitCodeWhetherEverythingWasDone()
    {
        var (ctl, svc, _) = New(s =>
        {
            s.Add("Fax", ServiceStartType.Disabled, running: false);
            s.Add("XblGameSave", ServiceStartType.Automatic, true, 0, "MapsBroker");
            s.Add("WinDefend", ServiceStartType.Automatic, running: true);
        });
        int Run(string? off, string? restore, out string printed)
        {
            var output = new StringWriter();
            int code = Program.Services(ctl, null, off, restore, output);
            printed = output.ToString();
            return code;
        }

        Assert.Equal(0, Run("Fax", null, out var said)); // off already
        Assert.Equal("Fax: " + ServicesController.OffAlready + Environment.NewLine, said);
        Assert.Equal(Engine.ExitCodes.NotAllTurnedOff, Run("WinDefend", null, out said)); // refused: protected
        Assert.Contains("protected", said);
        Assert.Equal(Engine.ExitCodes.NotAllTurnedOff, Run("MapsBroker", null, out said)); // set to Disabled, not stopped: XblGameSave runs
        Assert.True(ServicePlan.IsNotStoppedText(said["MapsBroker: ".Length..].TrimEnd()), said);
        svc.Live["XblGameSave"] = svc.Live["XblGameSave"] with { Running = false };
        svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { Running = false };
        Assert.Equal(0, Run(null, "all", out said)); // put back
        Assert.Equal("MapsBroker: restored" + Environment.NewLine, said);

        Assert.Equal(0, Run("MapsBroker", null, out said));
        svc.CannotStart.Add("MapsBroker");
        Assert.Equal(0, Run(null, "MapsBroker", out said)); // put back, not started again: said, and put back all the same
        Assert.Contains("starting it again failed", said);

        Assert.Equal(0, Run("MapsBroker", null, out _));
        svc.OnRestoreStart = () => throw new InvalidOperationException("access denied");
        Assert.Equal(Engine.ExitCodes.NotAllPutBack, Run(null, "MapsBroker", out said)); // not put back: its record is kept
        Assert.Equal("MapsBroker: access denied" + Environment.NewLine, said);

        // "all", one put back and one not: 8, by the one not put back, whatever the other did.
        svc.Live["Fax"] = svc.Live["Fax"] with { StartType = ServiceStartType.Manual };
        Assert.Equal(0, Run("Fax", null, out _));
        Assert.Equal(["MapsBroker", "Fax"], ctl.Current().Select(c => c.Service));
        svc.OnRestoreStart = () => throw new InvalidOperationException("access denied"); // the first one reached: MapsBroker
        Assert.Equal(Engine.ExitCodes.NotAllPutBack, Run(null, "all", out said));
        Assert.Equal("MapsBroker: access denied" + Environment.NewLine + "Fax: restored" + Environment.NewLine, said);
        Assert.Equal("MapsBroker", Assert.Single(ctl.Current()).Service);

        // A name PoolWatch holds no change of (typed wrong, say): said, and not done.
        Assert.Equal(Engine.ExitCodes.NotAllPutBack, Run(null, "MapsBrokr", out said));
        Assert.Equal("PoolWatch has not changed MapsBrokr: nothing to put back." + Environment.NewLine, said);
        Assert.Equal("MapsBroker", Assert.Single(ctl.Current()).Service); // nothing else was touched
        Assert.Equal(0, Run(null, "all", out said)); // put back
        Assert.Equal(0, Run(null, "all", out said)); // "all" with nothing recorded: done
        Assert.Equal("Nothing to put back: PoolWatch has not changed any service." + Environment.NewLine, said);

        svc.Unreadable = true;
        Assert.Equal(Engine.ExitCodes.NotAllTurnedOff, Run("MapsBroker", null, out said));
        Assert.Contains("could not read the services", said);
        Assert.Equal(Engine.ExitCodes.NotAllPutBack, Run(null, "all", out said));
        Assert.Contains("could not read the services", said);
    }

    // ---------------------------------------------------------------- what PoolWatch records of whether it ran

    /// <summary>
    /// The services list read MapsBroker as not known to run (Windows could not give its state), as starting or paused, or
    /// as stopped (it started after the list was read), but Turn off found it running and stopped it: its record says it
    /// was running, from what the stop saw, and Put back starts it again and says so. For the stopped row only what the
    /// change saw can say so: the record saved first says it is not known.
    /// </summary>
    [Theory]
    [InlineData(0u)]                                    // Windows could not say when the list was read
    [InlineData(ServiceControl.SERVICE_START_PENDING)]  // starting (a delayed-start service at sign-in, say)
    [InlineData(ServiceControl.SERVICE_PAUSED)]
    [InlineData(ServiceControl.SERVICE_STOPPED)]        // stopped when the list was read; running when Turn off looked
    public void TurnOffRecordsWhatItsStopSawAndPutBackStartsItAgain(uint stateWhenRead)
    {
        var (running, between) = ServiceControl.RunState(stateWhenRead);
        var (ctl, svc, paths) = New(s => s.Live["MapsBroker"] = s.Live["MapsBroker"] with
        {
            Running = running, NeitherRunningNorStopped = between, StateUnknown = ServiceControl.StateUnknown(stateWhenRead), Between = ServiceControl.BetweenOf(stateWhenRead),
        });
        svc.SawRunning = true; // by the time Turn off looks, Windows says it runs, and it is stopped
        Assert.Null(Assert.Single(ctl.Apply(["MapsBroker"], "manual")).error);
        var record = Assert.Single(Saved(paths));
        Assert.Equal((true, false), (record.OriginalRunning, record.OriginalRunningNotKnown));

        Assert.Equal([("MapsBroker", (string?)null)], ctl.Restore("MapsBroker"));
        Assert.Contains("restore MapsBroker", svc.Calls);
        Assert.True(svc.Live["MapsBroker"].Running);
        Assert.Equal(("Service put back: MapsBroker -> Automatic, started again (it was running before)", FindingSeverity.Info),
            (_findings.Last().Title, _findings.Last().Severity));
    }

    /// <summary>
    /// The record saved before the change says "running" only when the list read it so, and "not known" otherwise, also
    /// for a service the list read stopped (it may start before Turn off reaches it). The change then records what it saw:
    /// running, not running, or, when it could not tell either, the saved value stands.
    /// </summary>
    [Theory]
    [InlineData("unknown", null, false, true)]
    [InlineData("unknown", false, false, false)]
    [InlineData("unknown", true, true, false)]
    [InlineData("stopped", null, false, true)]
    [InlineData("stopped", false, false, false)]
    [InlineData("stopped", true, true, false)]   // it started after the list was read: the stop found it running
    [InlineData("running", false, false, false)] // it stopped after the list was read: Turn off found it stopped
    public void ARecordSavesNotKnownUntilTheChangeSawWhetherItRan(string read, bool? sawRunning, bool running, bool notKnown)
    {
        var (ctl, svc, paths) = New(s => s.Live["MapsBroker"] = read switch
        {
            "unknown" => s.Live["MapsBroker"] with { Running = false, NeitherRunningNorStopped = true, StateUnknown = true },
            "stopped" => s.Live["MapsBroker"] with { Running = false },
            _ => s.Live["MapsBroker"],
        });
        (bool, bool)? savedFirst = null;
        svc.OnApply = () => { var first = Assert.Single(Saved(paths)); savedFirst = (first.OriginalRunning, first.OriginalRunningNotKnown); };
        svc.SawRunning = sawRunning;
        svc.SawNothing = sawRunning is null; // Windows could not say when Turn off looked
        ctl.Apply(["MapsBroker"], "manual");
        Assert.Equal(read == "running" ? (true, false) : (false, true), savedFirst);
        var record = Assert.Single(Saved(paths));
        Assert.Equal((running, notKnown), (record.OriginalRunning, record.OriginalRunningNotKnown));
    }

    /// <summary>
    /// The list read MapsBroker stopped; it started before Turn off reached it, so the stop was asked for and taken; then
    /// the save at the end of the change failed (the record file held open by a reader). The record never says "was not
    /// running": it says not known, so Put back sets the start type back without starting it and says it does not know
    /// whether it was running, never a plain "put back".
    /// </summary>
    [Fact]
    public void AFailedEndOfChangeSaveNeverLeavesAPlainWasNotRunning()
    {
        var (ctl, svc, paths) = New(s => s.Live["MapsBroker"] = s.Live["MapsBroker"] with { Running = false });
        svc.SawRunning = true; // running by the time Turn off looked: its stop was asked for and taken
        FileStream? held = null;
        svc.OnApply = () => held = new FileStream(ChangeFile(paths), FileMode.Open, FileAccess.Read, FileShare.Read);
        try { Assert.Null(Assert.Single(ctl.Apply(["MapsBroker"], "manual")).error); }
        finally { held?.Dispose(); }
        var record = Assert.Single(Saved(paths));
        Assert.NotNull(record.UnderWay); // the end of the change was not saved
        Assert.Equal((false, true), (record.OriginalRunning, record.OriginalRunningNotKnown));

        Assert.Equal([("MapsBroker", (string?)("the start type is back to Automatic, but " + ServicePlan.RunningNotKnownText))], ctl.Restore("MapsBroker"));
        Assert.Equal((ServiceStartType.Automatic, false), (svc.Live["MapsBroker"].StartType, svc.Live["MapsBroker"].Running));
        Assert.Empty(Saved(paths));
    }

    /// <summary>
    /// A second Turn off keeps what the first record says of whether the service was running, whatever the second change
    /// saw: MapsBroker was turned off while it ran (or when that was not known); later something set it back to Automatic
    /// but it stayed stopped, and it is turned off again. The record still says what the first change saw, so Put back
    /// starts it again (or says it does not know).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)] // the first change could not tell
    public void AReApplyKeepsWhetherTheServiceWasRunningAtTheFirstChange(bool firstSawRunning)
    {
        var (ctl, svc, paths) = New(s =>
        {
            if (!firstSawRunning) s.Live["MapsBroker"] = s.Live["MapsBroker"] with { Running = false, NeitherRunningNorStopped = true, StateUnknown = true };
        });
        svc.SawNothing = !firstSawRunning;
        Assert.Null(Assert.Single(ctl.Apply(["MapsBroker"], "manual")).error);
        var first = Assert.Single(Saved(paths));
        Assert.Equal((firstSawRunning, !firstSawRunning), (first.OriginalRunning, first.OriginalRunningNotKnown));

        svc.SawNothing = false;
        svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { StartType = ServiceStartType.Automatic, Running = false, NeitherRunningNorStopped = false, StateUnknown = false };
        Assert.Null(Assert.Single(ctl.Apply(["MapsBroker"], "manual")).error); // this change sees it stopped
        var again = Assert.Single(Saved(paths));
        Assert.Equal((ServiceStartType.Automatic, firstSawRunning, !firstSawRunning), (again.OriginalStart, again.OriginalRunning, again.OriginalRunningNotKnown));

        var putBack = Assert.Single(ctl.Restore("MapsBroker"));
        Assert.Equal(firstSawRunning, svc.Live["MapsBroker"].Running);
        Assert.Equal(firstSawRunning ? null : "the start type is back to Automatic, but " + ServicePlan.RunningNotKnownText, putBack.error);
    }

    /// <summary>
    /// The list saw MapsBroker running, and the change could not tell (Windows could not give its state when Turn off
    /// looked): what the list saw stands, so the record says it was running (never "not known"), Active changes says so,
    /// and Put back starts it again.
    /// </summary>
    [Fact]
    public void TheListSawItRunningAndTheChangeCouldNotTellKeepsRunning()
    {
        var (ctl, svc, paths) = New();
        svc.SawNothing = true;
        Assert.Null(Assert.Single(ctl.Apply(["MapsBroker"], "manual")).error);
        var record = Assert.Single(Saved(paths));
        Assert.Equal((true, false), (record.OriginalRunning, record.OriginalRunningNotKnown));
        Assert.Equal("service · was Automatic and running", Assert.Single(new Engine.Changes.ServicesSource(ctl).List()).Detail);

        Assert.Equal([("MapsBroker", (string?)null)], ctl.Restore("MapsBroker"));
        Assert.True(svc.Live["MapsBroker"].Running);
        Assert.Equal("Service put back: MapsBroker -> Automatic, started again (it was running before)", _findings.Last().Title);
    }

    /// <summary>
    /// A record whose running state is not known says so in Active changes ("not known if it was running"), never the words
    /// of one that was not running ("was Automatic" alone).
    /// </summary>
    [Fact]
    public void ActiveChangesSaysWhenItIsNotKnownWhetherTheServiceRan()
    {
        var (ctl, svc, _) = New(s => s.Live["MapsBroker"] = s.Live["MapsBroker"] with { Running = false, NeitherRunningNorStopped = true, StateUnknown = true });
        svc.StopRefused = ServiceControl.Looked(0, asked: false, timeUp: false).Why; // Windows could not say when Turn off looked either
        svc.SawNothing = true;
        ctl.Apply(["MapsBroker"], "manual");
        Assert.Equal("service · was Automatic, not known if it was running", Assert.Single(new Engine.Changes.ServicesSource(ctl).List()).Detail);
    }

    /// <summary>
    /// A record whose running state is not known: Put back sets the start type back, does not start the service, and says
    /// plainly that it could not know (never a plain "put back"); the record goes. A service running by then is back as it
    /// was, and nothing more is said.
    /// </summary>
    [Fact]
    public void PutBackSaysPlainlyWhenItCouldNotKnowWhetherTheServiceRan()
    {
        var notKnown = new ServiceChange
        {
            Service = "MapsBroker", OriginalStart = ServiceStartType.Automatic, OriginalRunning = false, OriginalRunningNotKnown = true,
            NewStart = ServiceStartType.Disabled, ChangedUtc = DateTime.UtcNow.AddHours(-1), Via = "manual",
        };
        var (ctl, svc, paths) = New(s => s.Live["MapsBroker"] = s.Live["MapsBroker"] with { StartType = ServiceStartType.Disabled, Running = false }, saved: [notKnown]);
        string expected = "the start type is back to Automatic, but " + ServicePlan.RunningNotKnownText;
        Assert.Equal([("MapsBroker", (string?)expected)], ctl.Restore("MapsBroker"));
        Assert.Equal((ServiceStartType.Automatic, false), (svc.Live["MapsBroker"].StartType, svc.Live["MapsBroker"].Running));
        Assert.Empty(Saved(paths));
        Assert.Equal(("Service put back: MapsBroker -> Automatic, but " + ServicePlan.RunningNotKnownText, FindingSeverity.Warning),
            (_findings.Last().Title, _findings.Last().Severity));

        // Already back and running: only the record goes.
        var (again, svc2, paths2) = New(s => s.Live["MapsBroker"] = s.Live["MapsBroker"] with { StartType = ServiceStartType.Automatic, Running = true }, saved: [notKnown]);
        Assert.Equal([("MapsBroker", (string?)null)], again.Restore("MapsBroker"));
        Assert.DoesNotContain("restore MapsBroker", svc2.Calls);
        Assert.Empty(Saved(paths2));
    }

    /// <summary>
    /// A Turn off record service-changes.json holds from the published beta (no "originalRunningNotKnown", no "underWay"),
    /// of an Automatic original, still loads and is put back. That version saved "running" only when the services list read
    /// the service as RUNNING: its "running" is started again; its "not running" (also what it saved for a service it
    /// turned off while paused, starting or in a state Windows could not give) is not known, so Put back of the service,
    /// stopped then, sets the start type back, does not start it, and says PoolWatch does not know whether it was running.
    /// Saved again, the record says whether that is known. Not every "not running" record of that version gets those
    /// words: one whose original is Disabled is put back whole (<see cref="ABetaWindowsDefaultRecordIsPutBackWhole"/>), and
    /// so is one whose service runs by the time it is put back.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARecordFromThePublishedBetaIsPutBackAsItSays(bool originalRunning)
    {
        string beta = $$"""
            [
              {
                "service": "MapsBroker",
                "displayName": "Downloaded Maps Manager",
                "originalStart": "Automatic",
                "originalRunning": {{(originalRunning ? "true" : "false")}},
                "newStart": "Disabled",
                "changedUtc": "2026-10-30T09:15:00Z",
                "via": "manual",
                "beforeCores": 0.2,
                "beforePageFaultsPerSec": 1500,
                "notStoppedPid": null
              }
            ]
            """;
        var (ctl, svc, paths) = New(s => s.Live["MapsBroker"] = s.Live["MapsBroker"] with { StartType = ServiceStartType.Disabled, Running = false }, file: beta);
        var record = Assert.Single(ctl.Current());
        Assert.Equal((originalRunning, !originalRunning, (ChangeUnderWay?)null), (record.OriginalRunning, record.OriginalRunningNotKnown, record.UnderWay));
        Assert.Equal([("MapsBroker", originalRunning ? null : "the start type is back to Automatic, but " + ServicePlan.RunningNotKnownText)],
            ctl.Restore("MapsBroker"));
        Assert.Equal((ServiceStartType.Automatic, originalRunning), (svc.Live["MapsBroker"].StartType, svc.Live["MapsBroker"].Running));
        Assert.Empty(Saved(paths));
        Assert.Equal(originalRunning ? FindingSeverity.Info : FindingSeverity.Warning, _findings.Last().Severity);

        string written = ServiceChangeLog.ToJson([record]);
        Assert.Contains($"\"originalRunningNotKnown\": {(originalRunning ? "false" : "true")}", written);
        Assert.DoesNotContain("underWay", written);
    }

    /// <summary>
    /// A "Set to its Windows default" record of the published beta, in that version's file shape: its original is Disabled
    /// and it says "not running", which reads as not known. Put back sets an original of Disabled back to Disabled and stops
    /// the service if it runs, whatever it did before, so nothing is left undone: the service, stopped or running at Put
    /// back, is Disabled and stopped, the record goes, the finding is plain (Info), the result has no caveat, and the
    /// uninstaller's summary has a plain "Put back:" line for it, never "Partly put back:".
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ABetaWindowsDefaultRecordIsPutBackWhole(bool runningNow)
    {
        string beta = """
            [
              {
                "service": "MapsBroker",
                "displayName": "Downloaded Maps Manager",
                "originalStart": "Disabled",
                "originalRunning": false,
                "newStart": "Automatic",
                "changedUtc": "2026-10-30T09:15:00Z",
                "via": "Windows default",
                "beforeCores": null,
                "beforePageFaultsPerSec": null,
                "notStoppedPid": null
              }
            ]
            """;
        var (ctl, svc, paths) = New(s => s.Live["MapsBroker"] = s.Live["MapsBroker"] with
        {
            StartType = ServiceStartType.Automatic, Running = runningNow, ProcessId = runningNow ? 40 : 0,
        }, file: beta);
        var record = Assert.Single(ctl.Current());
        Assert.Equal((false, true), (record.OriginalRunning, record.OriginalRunningNotKnown));
        var source = new Engine.Changes.ServicesSource(ctl);
        var row = Assert.Single(source.List());
        var result = source.Undo(row, confirmed: true);
        Assert.Equal((true, (string?)null), (result.Done, result.Caveat));
        Assert.Equal(["restore MapsBroker"], svc.Calls);
        Assert.Equal((ServiceStartType.Disabled, false), (svc.Live["MapsBroker"].StartType, svc.Live["MapsBroker"].Running));
        Assert.Empty(Saved(paths));
        Assert.Equal(("Service put back: MapsBroker -> Disabled", FindingSeverity.Info), (_findings.Last().Title, _findings.Last().Severity));
        string summary = Program.UninstallSummary([row], null, [(row, result)], new DateTime(2026, 11, 2, 9, 0, 0));
        Assert.Equal("Put back: " + row.What, summary.Split(Environment.NewLine)[1]);
    }

    /// <summary>
    /// Put back of a record whose original is Disabled (undoing "Set to its Windows default") does not read a service that
    /// is Disabled but still running, paused or not seen stopped as already back: it goes through Put back, which stops it,
    /// and the record goes only then. One Disabled and seen stopped is already back.
    /// </summary>
    [Theory]
    [InlineData("running", false)]
    [InlineData("paused", false)]
    [InlineData("stopped", true)]
    public void PuttingBackDisabledReadsARunningServiceAsNotBackYet(string state, bool already)
    {
        var setDefault = new ServiceChange
        {
            Service = "MapsBroker", OriginalStart = ServiceStartType.Disabled, OriginalRunning = false, NewStart = ServiceStartType.Automatic,
            ChangedUtc = DateTime.UtcNow.AddHours(-1), Via = ServicePlan.ViaWindowsDefault,
        };
        var (ctl, svc, paths) = New(s => s.Live["MapsBroker"] = s.Live["MapsBroker"] with
        {
            StartType = ServiceStartType.Disabled, Running = state == "running", NeitherRunningNorStopped = state == "paused",
            Between = state == "paused" ? ServiceRunState.Paused : null, ProcessId = state == "running" ? 40 : 0,
        }, saved: [setDefault]);
        Assert.Equal([("MapsBroker", (string?)null)], ctl.Restore("MapsBroker"));
        Assert.Equal(already ? [] : ["restore MapsBroker"], svc.Calls);
        Assert.Equal((false, false), (svc.Live["MapsBroker"].Running, svc.Live["MapsBroker"].NeitherRunningNorStopped));
        Assert.Empty(Saved(paths));
        Assert.Equal(already, _findings.Last().Title.Contains("was already back to Disabled", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- a service list that cannot be read

    /// <summary>
    /// When the services cannot be read (the service control manager cannot be opened, or Windows lists none), that is not
    /// known, never "nothing installed": Turn off and the profile at start change nothing and say why, Put back keeps every
    /// record and says why (never "no longer installed"), "Set to its Windows default" refuses, and the self-repair check
    /// neither reports nor forgets what it reported: once the list reads again, a state reported before is not reported again.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AServiceListThatCannotBeReadIsNotKnownNeverNothingInstalled(bool throws)
    {
        var (ctl, svc, paths) = New();
        Assert.Null(Assert.Single(ctl.Apply(["MapsBroker"], "manual")).error);
        svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { StartType = ServiceStartType.Automatic, Running = true, ProcessId = 41 };
        Check(ctl);
        Assert.Equal([MapsBrokerSwitchedOn], SelfRepairWarnings());

        if (throws) svc.Unreadable = true; else svc.ListsNone = true;
        string why = "PoolWatch could not read the services: " + (throws ? "the service control manager could not be opened (error 5)" : "Windows listed no services");
        Check(ctl);
        Assert.Equal([MapsBrokerSwitchedOn], SelfRepairWarnings()); // nothing said, nothing forgotten
        var run = ctl.ApplyProfile(new ServiceProfile("P", ["MapsBroker"]), ProfileAtStart.Via("P"));
        Assert.Equal([("MapsBroker", why)], run.Skipped);
        Assert.Empty(run.TurnedOff);
        Assert.Equal([("(services)", (string?)why)], ctl.Apply(["MapsBroker"], "manual"));
        Assert.Equal([("MapsBroker", (string?)why)], ctl.Restore("MapsBroker"));
        Assert.Equal([("(services)", (string?)why)], ctl.Restore(null));
        Assert.Equal([("MapsBroker", (string?)why)], ctl.RestoreToDefault("MapsBroker"));
        Assert.Equal("MapsBroker", Assert.Single(Saved(paths)).Service); // kept
        Assert.Equal(["apply MapsBroker"], svc.Calls);
        Assert.DoesNotContain(_findings, f => f.Title.Contains("no longer installed", StringComparison.Ordinal));
        Assert.Throws<ServicesNotReadException>(() => ctl.Options());

        (svc.Unreadable, svc.ListsNone) = (false, false);
        Check(ctl);
        Assert.Equal([MapsBrokerSwitchedOn], SelfRepairWarnings()); // the same state, reported once
    }

    /// <summary>
    /// The warned-once memory: a state reported is not reported again at the next checks while it stays; a different state
    /// it is switched to is; once the service is seen off again, the same state is reported again; and a check that could
    /// not read the services in between changes none of that.
    /// </summary>
    [Fact]
    public void TheWarnedOnceMemoryHoldsAcrossAListThatCouldNotBeRead()
    {
        var (ctl, svc, _) = New();
        Assert.Null(Assert.Single(ctl.Apply(["MapsBroker"], "manual")).error);
        void Set(ServiceStartType start, bool running) => svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { StartType = start, Running = running, ProcessId = running ? 41 : 0 };
        Set(ServiceStartType.Automatic, true);
        Check(ctl);
        Check(ctl);
        Assert.Single(SelfRepairWarnings());
        svc.Unreadable = true;
        Set(ServiceStartType.Disabled, false); // off again while it could not be read: not seen
        Check(ctl);
        Set(ServiceStartType.Automatic, true);
        svc.Unreadable = false;
        Check(ctl);
        Assert.Single(SelfRepairWarnings()); // the same state, never seen off: not again
        Set(ServiceStartType.Manual, true);
        Check(ctl);
        Assert.Equal(2, SelfRepairWarnings().Count); // a new state
        Set(ServiceStartType.Disabled, false);
        Check(ctl);
        Set(ServiceStartType.Manual, true);
        Check(ctl);
        Assert.Equal(3, SelfRepairWarnings().Count); // seen off, then on again: reported again
    }

    // ---------------------------------------------------------------- a service the check could not read

    /// <summary>MapsBroker as a listed service PoolWatch could not open: neither its start type nor its state is known.</summary>
    private static void Unopenable(FakeServices s) => s.Live["MapsBroker"] = ServiceControl.Unread("MapsBroker", "Downloaded Maps Manager");

    /// <summary>
    /// A service PoolWatch turned off that it then cannot open, or whose start type alone, or state alone, it cannot read, is
    /// not read by the self-repair check: no "re-enabled outside PoolWatch ... is Unknown again" (nor any warning) while it
    /// reads so. Once it reads again, a real switch-on is reported.
    /// </summary>
    [Theory]
    [InlineData("unopenable")]
    [InlineData("start type")] // the configuration could not be read; the state could
    [InlineData("state")]      // the state could not be read; the start type could (and is not Disabled)
    public void AServiceTheCheckCouldNotReadIsNotReportedAsReEnabled(string unread)
    {
        var (ctl, svc, _) = New();
        Assert.Null(Assert.Single(ctl.Apply(["MapsBroker"], "manual")).error);
        if (unread == "unopenable") Unopenable(svc);
        else if (unread == "start type") svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { StartType = ServiceStartType.Unknown, Running = true, ProcessId = 41 };
        else svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { StartType = ServiceStartType.Automatic, Running = false, NeitherRunningNorStopped = true, StateUnknown = true };
        Check(ctl);
        Check(ctl);
        Assert.Empty(SelfRepairWarnings());

        svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { StartType = ServiceStartType.Automatic, Running = true, NeitherRunningNorStopped = false, StateUnknown = false, ProcessId = 41 };
        Check(ctl);
        Assert.Equal([MapsBrokerSwitchedOn], SelfRepairWarnings());
    }

    /// <summary>
    /// The warned-once memory across a check that could not read the service: a switch-on already reported is not reported
    /// again once the service reads again in the same state (whether it could not be opened, or was Disabled with its state
    /// not given), and the unread check reports nothing of its own.
    /// </summary>
    [Theory]
    [InlineData("unopenable")]
    [InlineData("Disabled, state not given")]
    public void ACheckThatCouldNotReadTheServiceDoesNotResetTheWarnedOnceMemory(string unread)
    {
        var (ctl, svc, _) = New();
        Assert.Null(Assert.Single(ctl.Apply(["MapsBroker"], "manual")).error);
        var on = svc.Live["MapsBroker"] with { StartType = ServiceStartType.Automatic, Running = true, ProcessId = 41 };
        svc.Live["MapsBroker"] = on;
        Check(ctl);
        if (unread == "unopenable") Unopenable(svc);
        else svc.Live["MapsBroker"] = on with { StartType = ServiceStartType.Disabled, Running = false, ProcessId = 0, NeitherRunningNorStopped = true, StateUnknown = true };
        Check(ctl);
        svc.Live["MapsBroker"] = on;
        Check(ctl);
        Assert.Equal([MapsBrokerSwitchedOn], SelfRepairWarnings());
    }

    /// <summary>
    /// A service whose Turn off could not stop it (Acme Maps, which depends on it, runs) keeps its mark across a check that
    /// could not read it: read again as Turn off left it (Disabled, running in the same process), it is not reported as
    /// switched back on, and its record still says it ran on.
    /// </summary>
    [Fact]
    public void AKeptRunningServiceKeepsItsMarkAcrossACheckThatCouldNotReadIt()
    {
        var (ctl, svc, paths) = New(MapsPair);
        Assert.NotNull(Assert.Single(ctl.Apply(["MapsBroker"], "manual")).error);
        var asLeft = svc.Live["MapsBroker"];
        Assert.Equal((ServiceStartType.Disabled, true, 40), (asLeft.StartType, asLeft.Running, Assert.Single(Saved(paths)).NotStoppedPid));
        Unopenable(svc);
        Check(ctl);
        Assert.Equal(40, Assert.Single(Saved(paths)).NotStoppedPid);
        svc.Live["MapsBroker"] = asLeft;
        Check(ctl);
        Assert.Empty(SelfRepairWarnings());
        Assert.Equal(40, Assert.Single(Saved(paths)).NotStoppedPid);
    }

    /// <summary>
    /// What was reported is forgotten only once the service is seen Disabled and STOPPED: one paused at Disabled (its process
    /// still there) and then resumed is still in the state reported before, so "running again" is not repeated. Seen
    /// stopped, a later start is reported again.
    /// </summary>
    [Fact]
    public void APausedServiceDoesNotResetTheWarnedOnceMemory()
    {
        var (ctl, svc, _) = New();
        Assert.Null(Assert.Single(ctl.Apply(["MapsBroker"], "manual")).error);
        var running = svc.Live["MapsBroker"] with { StartType = ServiceStartType.Disabled, Running = true, ProcessId = 41 };
        svc.Live["MapsBroker"] = running;
        Check(ctl);
        svc.Live["MapsBroker"] = running with { Running = false, NeitherRunningNorStopped = true, Between = ServiceRunState.Paused };
        Check(ctl);
        svc.Live["MapsBroker"] = running;
        Check(ctl);
        const string again = "Service re-enabled outside PoolWatch: MapsBroker is Disabled, running again (you turned it off). Turn it off again with " +
                             "Turn off; Put back works again after that on the Services tab.";
        Assert.Equal([again], SelfRepairWarnings());

        svc.Live["MapsBroker"] = running with { Running = false, ProcessId = 0 }; // STOPPED
        Check(ctl);
        svc.Live["MapsBroker"] = running;
        Check(ctl);
        Assert.Equal([again, again], SelfRepairWarnings());
    }

    /// <summary>
    /// Put back of a service PoolWatch can no longer open changes nothing, keeps the record, and says it could not read the
    /// service's start type (never that it "was changed since"); once it reads again as PoolWatch left it, Put back works.
    /// </summary>
    [Fact]
    public void PutBackOfAServiceItCannotOpenChangesNothingAndSaysWhy()
    {
        var (ctl, svc, paths) = New();
        Assert.Null(Assert.Single(ctl.Apply(["MapsBroker"], "manual")).error);
        var asLeft = svc.Live["MapsBroker"];
        Unopenable(svc);
        var (service, error) = Assert.Single(ctl.Restore("MapsBroker"));
        Assert.Equal("MapsBroker", service);
        Assert.StartsWith("'MapsBroker': PoolWatch could not read its start type now", error);
        Assert.DoesNotContain("changed since", error);
        Assert.DoesNotContain("restore MapsBroker", svc.Calls);
        Assert.Single(Saved(paths));

        svc.Live["MapsBroker"] = asLeft;
        Assert.Equal([("MapsBroker", (string?)null)], ctl.Restore("MapsBroker"));
        Assert.Empty(Saved(paths));
    }

    /// <summary>
    /// A check that could not read a service looks again soon (<see cref="ServicesController.LookAgainMs"/>), once: while it
    /// stays unreadable, the checks after that come at the usual pace, so a service that never reads does not have the
    /// services read every few seconds.
    /// </summary>
    [Fact]
    public void ACheckThatCouldNotReadAServiceLooksAgainSoonOnce()
    {
        var (ctl, svc, _) = New();
        Assert.Null(Assert.Single(ctl.Apply(["MapsBroker"], "manual")).error);
        Unopenable(svc);
        Check(ctl);
        int reads = svc.Reads;
        _clock.NowMs += ServicesController.LookAgainMs;
        ctl.CheckSelfRepair();
        Assert.Equal(reads + 1, svc.Reads); // looked again soon
        _clock.NowMs += ServicesController.LookAgainMs;
        ctl.CheckSelfRepair();
        Assert.Equal(reads + 1, svc.Reads); // still unreadable: not again so soon
        _clock.NowMs += ServicesController.SelfRepairEveryMs;
        ctl.CheckSelfRepair();
        Assert.Equal(reads + 2, svc.Reads); // at the usual pace
    }

    /// <summary>
    /// The Services tab's status line when the services cannot be read says "PoolWatch could not read the services" once,
    /// whether the read failed or Windows listed none: built, as the tab builds it, from the exception of its background
    /// read (a task's, so wrapped). Any other error gives its own message.
    /// </summary>
    [Theory]
    [InlineData(true, "PoolWatch could not read the services: the service control manager could not be opened (error 5). Refresh tries again.")]
    [InlineData(false, "PoolWatch could not read the services: Windows listed no services. Refresh tries again.")]
    public async Task TheTabSaysTheServicesCouldNotBeReadOnce(bool throws, string expected)
    {
        var (ctl, svc, _) = New();
        if (throws) svc.Unreadable = true; else svc.ListsNone = true;
        var read = Task.Run(() => ctl.Options());
        await Assert.ThrowsAsync<ServicesNotReadException>(() => read);
        Assert.Equal(expected, Ui.ServicesPageText.ReadFailed(read.Exception!));

        var other = Task.Run(new Action(() => throw new UnauthorizedAccessException("Access is denied.")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => other);
        Assert.Equal("PoolWatch could not read the services: Access is denied. Refresh tries again.", Ui.ServicesPageText.ReadFailed(other.Exception!));
    }

    /// <summary>
    /// The self-repair check reads the record again each time. When the file cannot be read, it goes on with the records
    /// as last read, and RecordError says why: the check does not stop, nor does it miss a switch-on.
    /// </summary>
    [Fact]
    public void ASelfRepairCheckThatCannotReadTheRecordGoesOnWithTheRecordsLastRead()
    {
        var (ctl, svc, paths) = New();
        Assert.Null(Assert.Single(ctl.Apply(["MapsBroker"], "manual")).error);
        svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { StartType = ServiceStartType.Automatic, Running = true, ProcessId = 40 };
        File.WriteAllText(ChangeFile(paths), "{ damaged");
        Check(ctl);
        Assert.Equal([MapsBrokerSwitchedOn], SelfRepairWarnings());
        Assert.Contains("damaged or empty", ctl.RecordError);
        Assert.Equal("{ damaged", File.ReadAllText(ChangeFile(paths))); // left as it is
    }
}
