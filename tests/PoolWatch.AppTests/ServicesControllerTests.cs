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

    /// <summary>
    /// The live services, as the fake service control manager has them; every change call is recorded. As Windows does, it
    /// sets the start type but does not stop a service while one that depends on it runs, and does not start one while a
    /// service it needs is Disabled (a stopped one it needs starts with it).
    /// </summary>
    private sealed class FakeServices : IServiceBackend
    {
        public readonly Dictionary<string, ServiceView> Live = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Calls = new();
        public string? ApplyError;
        /// <summary>
        /// Why Windows does not stop a service it is asked to, one that runs or is neither running nor stopped (null: it
        /// stops, unless one that depends on it runs). One not stopped is left as it was, at its new start type.
        /// </summary>
        public NotDone? StopRefused;
        /// <summary>Runs once the service is restored, before the controller records it.</summary>
        public Action? OnRestore;
        /// <summary>Services Windows does not start (error 1067); one that needs such a service does not start either (1068).</summary>
        public readonly HashSet<string> CannotStart = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Runs once, when a change is asked for and before it is made (or fails with <see cref="ApplyError"/>).</summary>
        public Action? OnApply;

        public void Add(string name, ServiceStartType start, bool running, int pid = 0, params string[] dependsOn) =>
            Live[name] = new ServiceView { Name = name, DisplayName = name, StartType = start, Running = running, MicrosoftSigned = true, ProcessId = pid, DependsOn = dependsOn };

        /// <summary>A service of another app (not Microsoft's, not on the catalogue: Turn off sets it to Disabled), running.</summary>
        public void AddThirdParty(string name, int pid, params string[] dependsOn) =>
            Live[name] = new ServiceView { Name = name, DisplayName = name, StartType = ServiceStartType.Automatic, Running = true, ProcessId = pid, DependsOn = dependsOn };

        public IReadOnlyList<ServiceView> Enumerate() => Live.Values.ToList();
        public bool InsiderEnrolled() => false;

        /// <summary>As Windows answers a stop refused because a service that depends on it is running (error 1051).</summary>
        public static readonly NotDone DependentsRunning = new("a service that depends on it is running", NotDoneCause.DependentsRunning);

        public (string? error, NotDone? notStopped) Apply(ServicePlan.Operation op, IReadOnlySet<string> allowed, bool insiderEnrolled)
        {
            string name = op.Option.Service.Name;
            Calls.Add("apply " + name);
            if (OnApply is { } hook) { OnApply = null; hook(); }
            if (ApplyError is { } e) return (e, null);
            var live = Live[name];
            NotDone? notStopped = op.NewStart == ServiceStartType.Disabled && (live.Running || live.NeitherRunningNorStopped)
                ? StopRefused ?? (Live.Values.Any(v => v.Running && v.DependsOn.Contains(name, StringComparer.OrdinalIgnoreCase)) ? DependentsRunning : null)
                : null;
            Live[name] = notStopped is null ? live with { StartType = op.NewStart, Running = false, NeitherRunningNorStopped = false }
                : live with { StartType = op.NewStart };
            return (null, notStopped);
        }

        public (string? error, NotDone? notDone) Restore(ServiceChange change, IReadOnlySet<string> allowed, bool insiderEnrolled)
        {
            Calls.Add("restore " + change.Service);
            if (change.OriginalStart == ServiceStartType.Disabled && Live[change.Service].Running && StopRefused is { } refused)
            {
                // Putting back Disabled stops it too; Windows refuses the stop, the start type is back all the same.
                Live[change.Service] = Live[change.Service] with { StartType = ServiceStartType.Disabled };
                OnRestore?.Invoke();
                return (null, refused);
            }
            bool running = change.OriginalRunning;
            NotDone? notStarted = null;
            var needs = Live[change.Service].DependsOn;
            if (running && CannotStart.Contains(change.Service))
                (running, notStarted) = (false, ServiceControl.Started(1067));
            else if (running && needs.Any(d => CannotStart.Contains(d) || Live.TryGetValue(d, out var v) && v.StartType == ServiceStartType.Disabled))
                (running, notStarted) = (false, ServiceControl.Started(1068)); // as Windows answers: one it needs did not start
            else if (running)
                foreach (var d in needs.Where(Live.ContainsKey)) Live[d] = Live[d] with { Running = true };
            Live[change.Service] = Live[change.Service] with { StartType = change.OriginalStart, Running = running };
            OnRestore?.Invoke();
            return (null, notStarted);
        }

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
        return (new ServicesController(paths, LogOf(paths), _findings.Add, _ => null, _ => null, svc), svc, paths);
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
                     "If a running service you did not tick depends on one of them, Windows does not stop that one: it is set to not start and stops " +
                     "when Windows restarts.\n\n2 more ticked services are off already and are left as they are.\n\n" +
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
        var commandLine = new ServicesController(paths, LogOf(paths), _ => { }, _ => null, _ => null, svc);
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
        var commandLine = new ServicesController(paths, LogOf(paths), _ => { }, _ => null, _ => null, svc);
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
        var commandLine = new ServicesController(paths, LogOf(paths), _ => { }, _ => null, _ => null, svc);
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
        var commandLine = new ServicesController(paths, LogOf(paths), _ => { }, _ => null, _ => null, svc);
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
        // The one exception to "stopped": a running service nobody ticked depends on it.
        const string ItKeepsRunning = "If a running service you did not tick depends on it, Windows does not stop it: it is set to not start and stops when " +
                                      "Windows restarts.\n\n";
        Assert.Equal("1 service will be stopped and set to not start:\n\n  SysMain\n\n" + ItKeepsRunning +
                     "PoolWatch saves how each one was set first, so you can put it back. Continue?",
            Ui.ServicesPageText.ApplyQuestion(["SysMain"]));
        Assert.StartsWith("2 services will be stopped and set to not start:\n\n  SysMain\n  Fax\n\nIf a running service you did not tick depends on one of them, ",
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

    /// <summary>Another reason Windows gives for not stopping a service is said in its place; the process is not known here.</summary>
    [Fact]
    public void AStopWindowsRefusesForAnotherReasonIsSaidAsWindowsSaysIt()
    {
        var (ctl, svc, paths) = New();
        svc.StopRefused = ServiceControl.Answered(1061, lookedAgain: true).Why;
        Assert.Equal([("MapsBroker", (string?)"set to Disabled, but still running: it cannot take a stop now (Windows error 1061). It stops when Windows " +
                                               "restarts, or when you turn it off again")], ctl.Apply(["MapsBroker"], "manual"));
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
        ctl.CheckSelfRepair(DateTime.UtcNow);
        Assert.Empty(SelfRepairWarnings());
    }

    /// <summary>
    /// Putting back an owner's own Disabled after "Set to its Windows default" stops the service too: when Windows does not
    /// stop it, the result says it is still running (still paused, for one that was paused), not that starting it failed;
    /// when Windows cannot say, it says that; when it was still stopping, it says it is not stopped yet.
    /// </summary>
    [Theory]
    [InlineData("1051", "it is still running: a service that depends on it is running. It stops when Windows restarts")]
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
        var t = DateTime.UtcNow;
        ctl.CheckSelfRepair(t);
        Assert.Empty(SelfRepairWarnings());
        new ServicesController(paths, LogOf(paths), _findings.Add, _ => null, _ => null, svc).CheckSelfRepair(t); // PoolWatch started again
        Assert.Empty(SelfRepairWarnings());

        // Acme Maps stops, then MapsBroker: off as PoolWatch meant, and its record no longer says it ran on.
        svc.Live["AcmeMaps"] = svc.Live["AcmeMaps"] with { Running = false };
        svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { Running = false, ProcessId = 0 };
        ctl.CheckSelfRepair(t.AddMinutes(6));
        Assert.Empty(SelfRepairWarnings());
        Assert.Null(Assert.Single(Saved(paths)).NotStoppedPid);

        svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { Running = true, ProcessId = 40 };
        ctl.CheckSelfRepair(t.AddMinutes(12));
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
        ctl.CheckSelfRepair(DateTime.UtcNow);
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
        ctl.CheckSelfRepair(DateTime.UtcNow);
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

        var t = DateTime.UtcNow;
        svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { Running = false, ProcessId = 0, NeitherRunningNorStopped = true }; // at the check
        ctl.CheckSelfRepair(t);
        Assert.Equal(mark, Assert.Single(Saved(paths)).NotStoppedPid);
        Assert.StartsWith("set to Disabled, not known to have stopped (turned off by PoolWatch ",
            ServicePlan.StateText(ctl.Options().Single(o => o.Service.Name == "MapsBroker")));

        svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { Running = true, ProcessId = 40, NeitherRunningNorStopped = false };
        ctl.CheckSelfRepair(t.AddMinutes(6));
        Assert.Empty(SelfRepairWarnings());

        svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { Running = false, ProcessId = 0 }; // STOPPED
        ctl.CheckSelfRepair(t.AddMinutes(12));
        Assert.Null(Assert.Single(Saved(paths)).NotStoppedPid);
        svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { Running = true, ProcessId = 40 };
        ctl.CheckSelfRepair(t.AddMinutes(18));
        Assert.Equal(["Service re-enabled outside PoolWatch: MapsBroker is Disabled, running again (you turned it off). Turn it off again with " +
                      "Turn off; Put back works again after that on the Services tab."], SelfRepairWarnings());
    }

    /// <summary>
    /// A self-repair check that comes while Turn off is under way (the record saved, the start type set, the stop not
    /// done yet, so Disabled and still running with no mark) skips the service: PoolWatch's own change is never read as
    /// switched back on outside it. Once the change is done, the check reads it again, and its mark holds.
    /// </summary>
    [Fact]
    public void TheSelfRepairCheckSkipsAServiceWhoseTurnOffIsUnderWay()
    {
        var (ctl, svc, paths) = New(MapsPair);
        var t = DateTime.UtcNow;
        svc.OnApply = () =>
        {
            Assert.Null(Assert.Single(Saved(paths)).NotStoppedPid); // saved, not marked yet
            svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { StartType = ServiceStartType.Disabled };
            ctl.CheckSelfRepair(t);
        };
        Assert.NotNull(Assert.Single(ctl.Apply(["MapsBroker"], "manual")).error); // not stopped: Acme Maps runs
        Assert.Empty(SelfRepairWarnings());
        Assert.Equal(40, Assert.Single(Saved(paths)).NotStoppedPid);
        ctl.CheckSelfRepair(t.AddMinutes(6));
        Assert.Empty(SelfRepairWarnings());
    }

    /// <summary>
    /// A Turn off whose change throws still lets go of the service: the self-repair check reads it again, so a switch-on
    /// is not hidden for good.
    /// </summary>
    [Fact]
    public void ATurnOffThatThrowsLetsTheSelfRepairCheckReadTheServiceAgain()
    {
        var (ctl, svc, _) = New();
        svc.OnApply = () => throw new InvalidOperationException("the service control manager went away");
        Assert.Throws<InvalidOperationException>(() => ctl.Apply(["MapsBroker"], "manual"));
        Assert.Equal("MapsBroker", Assert.Single(ctl.Changes).Service); // its record was saved first
        svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { StartType = ServiceStartType.Disabled };
        ctl.CheckSelfRepair(DateTime.UtcNow);
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
    /// running (the running dependents named when Windows refused for them). It is not off yet, so turning it off again,
    /// as its result says, asks for its stop again.
    /// </summary>
    [Theory]
    [InlineData(1051, "set to Disabled, but still paused: AcmeMaps depends on it and is running. It stops when Windows restarts, or when you turn it " +
                      "off again after that service has stopped")]
    [InlineData(1052, "set to Disabled, but still paused: it does not take a stop (Windows error 1052). It stops when Windows restarts, or when you " +
                      "turn it off again")]
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

    /// <summary>
    /// The self-repair warning comes once per state a service is switched back to, but Turn off starts that again: once
    /// the owner has turned the service off again, a switch back on to the same state outside PoolWatch is reported
    /// again, whether that Turn off stopped it (and nothing looked before it was switched on again) or not (one that
    /// depends on it runs).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASwitchOnAfterATurnOffIsReportedAgainInAStateReportedBefore(bool notStopped)
    {
        const string Warned = "Service re-enabled outside PoolWatch: MapsBroker is Automatic, running again (you turned it off). Turn it off again with " +
                              "Turn off, or keep it this way with Put back on the Services tab.";
        var (ctl, svc, _) = New(notStopped ? MapsPair : (Action<FakeServices>?)null);
        void SwitchedOn() => svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { StartType = ServiceStartType.Automatic, Running = true, ProcessId = 40 };
        var t = DateTime.UtcNow;
        Assert.Equal(notStopped, Assert.Single(ctl.Apply(["MapsBroker"], "manual")).error is not null);
        SwitchedOn();
        ctl.CheckSelfRepair(t);
        Assert.Equal([Warned], SelfRepairWarnings());

        Assert.Equal(notStopped, Assert.Single(ctl.Apply(["MapsBroker"], "manual")).error is not null); // turned off again
        if (notStopped)
        {
            ctl.CheckSelfRepair(t.AddMinutes(6)); // running on at Disabled, as Turn off left it: nothing to report
            Assert.Equal([Warned], SelfRepairWarnings());
        }
        SwitchedOn();
        ctl.CheckSelfRepair(t.AddMinutes(12));
        Assert.Equal([Warned, Warned], SelfRepairWarnings());
    }
}
