using PoolWatch.Core.Findings;
using PoolWatch.Core.Services;
using PoolWatch.Engine;
using PoolWatch.Engine.Changes;

namespace PoolWatch.AppTests;

/// <summary>
/// The service profile applied when PoolWatch starts: after its delay, through Apply's own path (the catalogue, the plan's
/// gate, the live gate, the original recorded first, a log line, a finding and an Active changes row), only for the
/// services that are back on; anything else is left as it is and the log says why. A fake service backend (no real
/// service is read or changed) and a data root in TEMP (no ACL is touched).
/// </summary>
public sealed class ProfileAtStartTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pw-prof-" + Guid.NewGuid().ToString("N"));
    private readonly List<Finding> _findings = new();

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>The live services as a fake service control manager has them; each change call and the allow-list it got is recorded.</summary>
    private sealed class FakeServices : IServiceBackend
    {
        public readonly Dictionary<string, ServiceView> Live = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Calls = new();
        public readonly List<IReadOnlySet<string>> Allowed = new();
        public readonly List<bool> InsiderSeen = new();
        public bool Insider;
        /// <summary>What the service control manager answers to a change to the Windows default (null: done).</summary>
        public string? DefaultError;

        public void Add(string name, ServiceStartType start, bool running, bool microsoft = true) =>
            Live[name] = new ServiceView { Name = name, DisplayName = name, StartType = start, Running = running, MicrosoftSigned = microsoft };

        public IReadOnlyList<ServiceView> Enumerate() => Live.Values.ToList();
        public bool InsiderEnrolled() => Insider;
        /// <summary>Runs inside each change, before the service is changed (a test holds a change there).</summary>
        public Action? BeforeChange;

        public (string? error, NotDone? notStopped) Apply(ServicePlan.Operation op, IReadOnlySet<string> allowed, bool insiderEnrolled)
        {
            BeforeChange?.Invoke();
            Calls.Add("apply " + op.Option.Service.Name);
            Allowed.Add(allowed);
            InsiderSeen.Add(insiderEnrolled);
            // The live gate as ServiceControl applies it: protected or not offered on this PC is refused.
            if (ServiceCatalogue.IsProtected(op.Option.Service.Name, insiderEnrolled) || !allowed.Contains(op.Option.Service.Name)) return ("refused by the live gate", null);
            Live[op.Option.Service.Name] = Live[op.Option.Service.Name] with { StartType = op.NewStart, Running = op.NewStart == ServiceStartType.Disabled ? false : Live[op.Option.Service.Name].Running };
            return (null, null);
        }

        public (string? error, NotDone? notDone) Restore(ServiceChange change, IReadOnlySet<string> allowed, bool insiderEnrolled)
        {
            Calls.Add("restore " + change.Service);
            Live[change.Service] = Live[change.Service] with { StartType = change.OriginalStart, Running = change.OriginalRunning };
            return (null, null);
        }
        public string? SetDefault(ServicePlan.Operation op, IReadOnlySet<string> allowed, bool insiderEnrolled)
        {
            BeforeChange?.Invoke();
            Calls.Add("default " + op.Option.Service.Name);
            if (DefaultError is { } refused) return refused;
            Live[op.Option.Service.Name] = Live[op.Option.Service.Name] with { StartType = op.NewStart };
            return null;
        }
    }

    private (ServicesController ctl, FakeServices svc, AppPaths paths, string log) New(Action<FakeServices>? setup = null, string? changes = null)
    {
        var paths = new AppPaths(Path.Combine(_root, "data"), null, secure: false);
        if (changes is not null) File.WriteAllText(Path.Combine(paths.DataRoot, "service-changes.json"), changes);
        var svc = new FakeServices();
        setup?.Invoke(svc);
        string log = Path.Combine(paths.DataRoot, "test.log");
        return (new ServicesController(paths, new Log(log), _findings.Add, _ => null, _ => null, svc), svc, paths, log);
    }

    private static string Logged(string log) => File.Exists(log) ? File.ReadAllText(log) : "";

    /// <summary>A PC after a Windows update: MapsBroker is back on, SysMain and edgeupdatem are still as Apply left them.</summary>
    private static void AfterAnUpdate(FakeServices s)
    {
        s.Add("MapsBroker", ServiceStartType.Automatic, running: true);  // back on
        s.Add("SysMain", ServiceStartType.Disabled, running: false);     // still off
        s.Add("edgeupdatem", ServiceStartType.Manual, running: false);   // off as its catalogue entry says (Manual)
        s.Add("WinDefend", ServiceStartType.Automatic, running: true);   // protected
        s.Add("SomeVendorSvc", ServiceStartType.Automatic, running: true, microsoft: false); // a third party the catalogue does not list
    }

    [Fact]
    public void OnlyTheServicesThatAreBackOnAreTurnedOffThroughApplysPath()
    {
        var (ctl, svc, paths, log) = New(AfterAnUpdate);
        // Not a profile read from settings.json (that would never hold WinDefend or SomeVendorSvc): the run checks again.
        var profile = new ServiceProfile("Build box", ["MapsBroker", "SysMain", "edgeupdatem", "WinDefend", "SomeVendorSvc", "DiagTrack"]);

        var run = ctl.ApplyProfile(profile, ProfileAtStart.Via(profile.Name));

        Assert.Equal(["apply MapsBroker"], svc.Calls);                  // nothing else reached the service control
        Assert.Equal(["MapsBroker"], run.TurnedOff);
        Assert.Equal(["SysMain", "edgeupdatem"], run.AlreadyOff);
        Assert.Equal(["WinDefend", "SomeVendorSvc", "DiagTrack"], run.Skipped.Select(s => s.service));
        Assert.Contains("protected", run.Skipped[0].why);
        Assert.Equal("it is not on PoolWatch's list of services you may turn off", run.Skipped[1].why);
        Assert.Equal("it is not installed on this PC", run.Skipped[2].why);
        Assert.Empty(run.Failed);
        // The live gate got the page's allow-list: the services that may be turned off on this PC, never a protected one.
        Assert.Contains("MapsBroker", svc.Allowed.Single());
        Assert.DoesNotContain("WinDefend", svc.Allowed.Single());
        Assert.Equal(ServiceStartType.Disabled, svc.Live["MapsBroker"].StartType);

        // The original was recorded, by the profile at start, as every Apply records it, and it is an Active changes row.
        var record = Assert.Single(ctl.Current());
        Assert.Equal(("MapsBroker", ServiceStartType.Automatic, true, "profile Build box, at start"), (record.Service, record.OriginalStart, record.OriginalRunning, record.Via));
        Assert.Contains(Core.Changes.ChangeIds.Service("MapsBroker"), new ServicesSource(ctl).List().Select(c => c.Id));
        Assert.Contains(_findings, f => f.Title.StartsWith("Service turned off: MapsBroker", StringComparison.Ordinal));

        string said = Logged(log);
        Assert.Contains("Service turned off: MapsBroker", said);
        Assert.Contains("Service profile Build box at start: WinDefend left as it is: 'WinDefend' is a protected service and is never changed", said);
        Assert.Contains("Service profile Build box at start: SomeVendorSvc left as it is: it is not on PoolWatch's list of services you may turn off", said);
        Assert.Contains("Service profile Build box at start: DiagTrack left as it is: it is not installed on this PC", said);
        Assert.Contains("Service profile Build box at start: 1 turned off, 2 already off, 3 left as they are, 0 not turned off" + Environment.NewLine, said); // nothing more
    }

    [Fact]
    public void AServiceTheOwnerDisabledIsNeverLoosenedToManual()
    {
        // The Edge updater's catalogue entry turns its services off to Manual. Disabled by its owner, edgeupdate is off
        // already, and left as it is; Disabled but running, edgeupdatem stays Disabled and is stopped, never set to Manual.
        var (ctl, svc, _, _) = New(s => { s.Add("edgeupdate", ServiceStartType.Disabled, running: false); s.Add("edgeupdatem", ServiceStartType.Disabled, running: true); });
        var run = ctl.ApplyProfile(new ServiceProfile("Updaters off", ["edgeupdate", "edgeupdatem"]), ProfileAtStart.Via("Updaters off"));
        Assert.Equal(["edgeupdate"], run.AlreadyOff);
        Assert.Equal(["edgeupdatem"], run.TurnedOff);
        Assert.Equal(["apply edgeupdatem"], svc.Calls);
        Assert.Equal((ServiceStartType.Disabled, false), (svc.Live["edgeupdatem"].StartType, svc.Live["edgeupdatem"].Running));
        Assert.Equal(ServiceStartType.Disabled, svc.Live["edgeupdate"].StartType);
    }

    [Fact]
    public void AServiceWhoseStartTypeCannotBeReadIsLeftAsItIsWithNoRecord()
    {
        // The service control manager could not tell MapsBroker's start type: recorded as its original, Restore and Undo
        // would refuse it, so the run leaves it as it is, says why, and goes on with the rest.
        var (ctl, svc, _, log) = New(s => { s.Add("MapsBroker", ServiceStartType.Unknown, running: true); s.Add("SysMain", ServiceStartType.Automatic, running: true); });
        var run = ctl.ApplyProfile(new ServiceProfile("P", ["MapsBroker", "SysMain"]), ProfileAtStart.Via("P"));
        Assert.Equal(["apply SysMain"], svc.Calls);
        Assert.Equal(["SysMain"], run.TurnedOff);
        Assert.Equal(("MapsBroker", "'MapsBroker': its current start type cannot be read, so it could not be put back later"), Assert.Single(run.Skipped));
        Assert.Equal(["SysMain"], ctl.Current().Select(c => c.Service));
        Assert.Contains("Service profile P at start: MapsBroker left as it is: 'MapsBroker': its current start type cannot be read", Logged(log));
        // Apply refuses it too, and changes nothing.
        Assert.Equal(("(plan)", "'MapsBroker': its current start type cannot be read, so it could not be put back later"), Assert.Single(ctl.Apply(["MapsBroker"], "manual")));
        Assert.Equal(["apply SysMain"], svc.Calls);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("apply")]
    [InlineData("default")]
    public async Task ARestoreWaitsForAChangeThatIsUnderWay(string first)
    {
        // A profile at start (or an Apply, or a Restore to Windows default) has saved the service's record and is changing it
        // when the owner presses Restore: the Restore waits, then puts it back, so the service is never left changed
        // without its record.
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var (ctl, svc, _, _) = New(s => { s.Add("MapsBroker", ServiceStartType.Automatic, running: true); s.Add("PcaSvc", ServiceStartType.Disabled, running: false); });
        svc.BeforeChange = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); };
        string service = first == "default" ? "PcaSvc" : "MapsBroker";
        var change = Task.Run(() => first switch
        {
            "profile" => ctl.ApplyProfile(new ServiceProfile("P", ["MapsBroker"]), ProfileAtStart.Via("P")).Failed.Count,
            "apply" => ctl.Apply(["MapsBroker"], "manual").Count(r => r.error is not null),
            _ => ctl.RestoreToDefault("PcaSvc").Count(r => r.error is not null),
        });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var restore = Task.Run(() => ctl.Restore(service));
        await Task.WhenAny(restore, Task.Delay(400));
        Assert.False(restore.IsCompleted); // not while the change is under way
        release.Set();
        Assert.Equal(0, await change.WaitAsync(TimeSpan.FromSeconds(10)));
        var restored = await restore.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(Assert.Single(restored).error);
        Assert.Equal([(first == "default" ? "default " : "apply ") + service, "restore " + service], svc.Calls);
        Assert.Empty(ctl.Current()); // back, and its record gone with it
    }

    [Fact]
    public void EverySavedChangeMovesTheVersionTheServicesTabWatches()
    {
        var (ctl, svc, _, _) = New(AfterAnUpdate);
        int before = ctl.Version;
        ctl.ApplyProfile(new ServiceProfile("P", ["MapsBroker"]), ProfileAtStart.Via("P"));
        Assert.True(ctl.Version > before);
        int after = ctl.Version;
        ctl.ApplyProfile(new ServiceProfile("P", ["SysMain"]), ProfileAtStart.Via("P")); // off already: nothing saved
        Assert.Equal(after, ctl.Version);
    }

    [Fact]
    public void ASelfRepairWarningNamesTheTabAndTheProfileThatTurnsTheServiceOffAgain()
    {
        var (ctl, svc, _, _) = New(s => s.Add("MapsBroker", ServiceStartType.Automatic, running: true));
        Assert.Null(Assert.Single(ctl.Apply(["MapsBroker"], "manual")).error);
        svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { StartType = ServiceStartType.Automatic, Running = true }; // switched back on
        ctl.Profiles = () => new ServiceProfileSet { Profiles = [new("Build box", ["MapsBroker"])], AtStart = "Build box" };
        ctl.CheckSelfRepair(DateTime.UtcNow);
        var warning = Assert.Single(_findings, f => f.Title.StartsWith("Service re-enabled outside PoolWatch", StringComparison.Ordinal));
        Assert.Equal("Service re-enabled outside PoolWatch: MapsBroker is Automatic, running again (you turned it off). Build box, the profile applied when " +
                     "PoolWatch starts, turns it off again then. Turn it off again with Turn off, or keep it this way with Put back on the Services tab.", warning.Title);
        Assert.Equal("Service re-enabled outside PoolWatch: DiagTrack is Manual again (you turned it off). Turn it off again with Turn off; Put back works again " +
                     "after that on the Services tab.", ServicePlan.SelfRepairText("DiagTrack", ServiceStartType.Manual, false, "Turn it off again with Turn off; Put back works again after that", null));
    }

    [Fact]
    public void ARunKeepsTheFirstOriginalOfAServiceItTurnedOffBefore()
    {
        var (ctl, svc, _, _) = New(s => s.Add("MapsBroker", ServiceStartType.Automatic, running: true));
        Assert.Null(Assert.Single(ctl.Apply(["MapsBroker"], "manual")).error);
        svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { StartType = ServiceStartType.Manual, Running = true }; // a Windows update switched it on
        ctl.ApplyProfile(new ServiceProfile("P", ["MapsBroker"]), ProfileAtStart.Via("P"));
        Assert.Equal(ServiceStartType.Automatic, Assert.Single(ctl.Current()).OriginalStart); // Restore still puts back the owner's original
    }

    [Fact]
    public void WindowsInsidersServiceIsNeverTouchedOnAnEnrolledPc()
    {
        var (ctl, svc, _, _) = New(s => { s.Insider = true; s.Add("wisvc", ServiceStartType.Manual, running: true); s.Add("MapsBroker", ServiceStartType.Disabled, running: false); });
        var run = ctl.ApplyProfile(new ServiceProfile("P", ["wisvc", "MapsBroker"]), ProfileAtStart.Via("P"));
        Assert.Empty(svc.Calls);
        Assert.Equal("wisvc", Assert.Single(run.Skipped).service);

        // The live gate is told the PC is enrolled too, as for every Apply.
        svc.Live["MapsBroker"] = svc.Live["MapsBroker"] with { StartType = ServiceStartType.Automatic };
        ctl.ApplyProfile(new ServiceProfile("P", ["MapsBroker"]), ProfileAtStart.Via("P"));
        Assert.Equal(["apply MapsBroker"], svc.Calls);
        Assert.Equal([true], svc.InsiderSeen);
    }

    [Fact]
    public void NothingIsTurnedOffWhileTheRecordOfChangesCannotBeRead()
    {
        // The originals could not be saved first: no service is changed, and the log says why.
        var (ctl, svc, _, log) = New(AfterAnUpdate, changes: "[{\"service\": \"Fax\", \"originalStart\": \"Ma");
        var run = ctl.ApplyProfile(new ServiceProfile("P", ["MapsBroker"]), ProfileAtStart.Via("P"));
        Assert.Empty(svc.Calls);
        Assert.Empty(run.TurnedOff);
        Assert.Equal("MapsBroker", Assert.Single(run.Skipped).service);
        Assert.Contains("Service profile P at start: nothing turned off: PoolWatch cannot read how the services were set:", Logged(log));
    }

    [Fact]
    public void AServiceWhoseOriginalCannotBeSavedIsLeftAsItIs()
    {
        var (ctl, svc, paths, _) = New(AfterAnUpdate);
        Directory.CreateDirectory(Path.Combine(paths.DataRoot, "service-changes.json") + ".tmp"); // the record cannot be written
        var run = ctl.ApplyProfile(new ServiceProfile("P", ["MapsBroker"]), ProfileAtStart.Via("P"));
        Assert.Empty(svc.Calls);
        Assert.Contains("could not save how it was set", Assert.Single(run.Failed).error);
        Assert.Equal(ServiceStartType.Automatic, svc.Live["MapsBroker"].StartType);
    }

    // ---------------------------------------------------------------- the start

    private static ServiceProfileSet Set(string? atStart, int delay = 3, params ServiceProfile[] profiles) =>
        new() { Profiles = profiles, AtStart = atStart, DelayMinutes = delay };

    [Fact]
    public async Task TheProfileIsAppliedAfterItsDelayAndAnEditMeanwhileOnlyNarrowsIt()
    {
        var log = new List<string>();
        var applied = new List<(ServiceProfile profile, string via)>();
        var waited = new List<TimeSpan>();
        var gate = new TaskCompletionSource();
        var atStart = Set("Build box", 5, new ServiceProfile("Build box", ["SysMain", "WerSvc"]));
        var now = atStart;
        Task Delay(TimeSpan t, CancellationToken _) { waited.Add(t); return gate.Task; }

        var run = ProfileAtStart.Start(atStart, () => now, (p, via) => { applied.Add((p, via)); return new([], [], [], []); }, log.Add, Delay, CancellationToken.None);
        SpinWait.SpinUntil(() => waited.Count == 1, 5000);
        Assert.Equal([TimeSpan.FromMinutes(5)], waited);
        Assert.Empty(applied);                                    // not before the delay is up
        Assert.Contains("Service profile Build box is applied at start in 5 minutes.", log);

        // Edited meanwhile on the Services tab: WerSvc taken out, MapsBroker added. The run turns off only what the profile
        // listed when PoolWatch started and still lists; MapsBroker waits for the next start, as "Turn them off now?" said.
        now = Set("Build box", 9, new ServiceProfile("Build box", ["MapsBroker", "SysMain"]));
        gate.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        var (profile, via) = Assert.Single(applied);
        Assert.Equal(["SysMain"], profile.Services);
        Assert.Equal("profile Build box, at start", via);
        Assert.Contains("Service profile Build box: MapsBroker added after PoolWatch started: turned off from the next start.", log);
    }

    [Theory]
    [InlineData("Other", "Service profile Other is the one applied at start now, but Build box was when PoolWatch started: nothing is applied now; Other is applied from the next start.")]
    [InlineData("Renamed", "Service profile Renamed is the one applied at start now, but Build box was when PoolWatch started: nothing is applied now; Renamed is applied from the next start.")]
    public async Task AnotherProfileChosenOrTheChosenOneRenamedMeanwhileWaitsForTheNextStart(string nowAtStart, string said)
    {
        var log = new List<string>();
        int applied = 0;
        var atStart = Set("Build box", 3, new ServiceProfile("Build box", ["SysMain"]), new ServiceProfile("Other", ["MapsBroker"]));
        var now = nowAtStart == "Other" ? Set("Other", 3, new ServiceProfile("Build box", ["SysMain"]), new ServiceProfile("Other", ["MapsBroker"]))
            : Set("Renamed", 3, new ServiceProfile("Renamed", ["SysMain"]), new ServiceProfile("Other", ["MapsBroker"]));
        var run = ProfileAtStart.Start(atStart, () => now, (_, _) => { applied++; return new([], [], [], []); }, log.Add, (_, _) => Task.CompletedTask, CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, applied);
        Assert.Contains(said, log);
    }

    [Fact]
    public void AMissingProfileIsNotAppliedAndTheLogSaysSo()
    {
        var log = new List<string>();
        int applied = 0, waited = 0;
        var set = new ServiceProfileSet { Profiles = [new("Other", ["SysMain"])], AtStart = "Build box", Problems = ["Service profile \"Telemetry only\" left out: Telemetry only is a built-in profile."] };

        var run = ProfileAtStart.Start(set, () => set, (_, _) => { applied++; return new([], [], [], []); }, log.Add,
            (_, _) => { waited++; return Task.CompletedTask; }, CancellationToken.None);

        Assert.True(run.IsCompleted);
        Assert.Equal((0, 0), (applied, waited));
        Assert.Equal(["Service profile \"Telemetry only\" left out: Telemetry only is a built-in profile.",
            "Service profile Build box is chosen to apply at start, but settings.json holds no usable profile of that name: nothing is applied at start."], log);
    }

    [Fact]
    public void NoProfileChosenMeansNothingAtStart()
    {
        var log = new List<string>();
        var run = ProfileAtStart.Start(Set(null, 3, new ServiceProfile("A", ["SysMain"])), () => throw new InvalidOperationException(), (_, _) => throw new InvalidOperationException(),
            log.Add, (_, _) => throw new InvalidOperationException(), CancellationToken.None);
        Assert.True(run.IsCompleted);
        Assert.Empty(log);
    }

    [Fact]
    public async Task AProfileNoLongerChosenWhenTheDelayIsUpIsNotApplied()
    {
        var log = new List<string>();
        int applied = 0;
        var atStart = Set("A", 3, new ServiceProfile("A", ["SysMain"]));
        var run = ProfileAtStart.Start(atStart, () => Set(null, 3, new ServiceProfile("A", ["SysMain"])), (_, _) => { applied++; return new([], [], [], []); },
            log.Add, (_, _) => Task.CompletedTask, CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, applied);
        Assert.Contains("Service profile A is no longer chosen to apply at start: nothing is applied.", log);
    }

    [Fact]
    public async Task NothingRunsOncePoolWatchHasExited()
    {
        using var stop = new CancellationTokenSource();
        int applied = 0;
        string? waiting = null;
        var run = ProfileAtStart.Start(Set("A", 3, new ServiceProfile("A", ["SysMain"])), () => Set("A", 3, new ServiceProfile("A", ["SysMain"])),
            (_, _) => { applied++; return new([], [], [], []); }, _ => { }, (t, c) => Task.Delay(t, c), stop.Token, waiting: n => waiting = n);
        Assert.Equal("A", waiting);
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, applied);
        Assert.Null(waiting);                             // a run stopped while it waits no longer counts as waiting

        // A wait that ends without noticing the exit: still nothing runs.
        var late = ProfileAtStart.Start(Set("A", 3, new ServiceProfile("A", ["SysMain"])), () => Set("A", 3, new ServiceProfile("A", ["SysMain"])),
            (_, _) => { applied++; return new([], [], [], []); }, _ => { }, (_, _) => Task.CompletedTask, stop.Token);
        await late.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, applied);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(40, 15)]
    public async Task TheDelayIsOneToFifteenMinutes(int delay, int waitedMinutes)
    {
        var waited = new List<TimeSpan>();
        var set = Set("A", delay, new ServiceProfile("A", ["SysMain"]));
        var run = ProfileAtStart.Start(set, () => set, (_, _) => new([], [], [], []), _ => { }, (t, _) => { waited.Add(t); return Task.CompletedTask; }, CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([TimeSpan.FromMinutes(waitedMinutes)], waited);
    }

    [Fact]
    public async Task AFailedRunIsLoggedAndNeverThrows()
    {
        var log = new List<string>();
        var set = Set("A", 3, new ServiceProfile("A", ["SysMain"]));
        var run = ProfileAtStart.Start(set, () => set, (_, _) => throw new IOException("disk gone"), log.Add, (_, _) => Task.CompletedTask, CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("Service profile A not applied at start: disk gone", log);
    }

    // ---------------------------------------------------------------- Active changes

    [Fact]
    public void UndoOfAServiceTheProfileAppliedAtStartListsWarnsAndTakesItOutOfTheProfile()
    {
        var (ctl, svc, paths, _) = New(s =>
        {
            s.Add("MapsBroker", ServiceStartType.Automatic, running: true);
            s.Add("SysMain", ServiceStartType.Automatic, running: true);
            s.Add("edgeupdate", ServiceStartType.Automatic, running: false);
        });
        var live = new LiveSettings(paths, new Core.Settings(), persist: true, new Log(Path.Combine(paths.DataRoot, "live.log")));
        var store = new ProfileStore(live);
        store.Save("Build box", ["MapsBroker", "SysMain"], atStart: true, 3);
        ctl.ApplyProfile(store.Current.Find("Build box")!, ProfileAtStart.Via("Build box"));
        Assert.Null(Assert.Single(ctl.Apply(["edgeupdate"], "manual")).error); // turned off to Manual by hand, in no profile
        var source = new ServicesSource(ctl, store);
        var rows = source.List().ToList();
        var maps = rows.Single(r => r.Id == Core.Changes.ChangeIds.Service("MapsBroker"));
        Assert.Equal("Turned off MapsBroker (profile Build box, at start)", maps.What);
        Assert.Equal("Build box, the profile PoolWatch applies when it starts, turns MapsBroker off again then. Putting it back also takes it out of Build box, so it stays on.", maps.Warning);
        Assert.Equal(new AtStartHold("Build box", "MapsBroker"), maps.AtStart); // Undo all says it once for all of them
        var edge = rows.Single(r => r.Id == Core.Changes.ChangeIds.Service("edgeupdate"));
        Assert.Equal(("Turned off edgeupdate", (string?)null, (AtStartHold?)null), (edge.What, edge.Warning, edge.AtStart)); // turned off to Manual: "Turned off", not a Windows default
        Assert.All(new ServicesSource(ctl).List(), r => Assert.Null(r.Warning)); // no profiles known (the command line): no warning
        // The owner accepted the warning: back, and out of the profile, so the next start leaves it on.
        var undone = source.Undo(maps, confirmed: true);
        Assert.Equal((true, "MapsBroker is back to how it was. It is out of Build box now, so it stays on."), (undone.Done, undone.Message));
        Assert.Equal(["SysMain"], store.Current.Find("Build box")!.Services);
        // The command line (the uninstaller) never confirms: back, and the profile as it was.
        var sys = source.List().Single(r => r.Id == Core.Changes.ChangeIds.Service("SysMain"));
        var plain = source.Undo(sys, confirmed: false);
        Assert.Equal((true, "SysMain is back to how it was."), (plain.Done, plain.Message));
        Assert.Equal(["SysMain"], store.Current.Find("Build box")!.Services);
    }

    /// <summary>A controller over <paramref name="setup"/>'s services and a profile store whose "Build box" is applied at start.</summary>
    private (ServicesController ctl, FakeServices svc, ProfileStore store) WithProfile(Action<FakeServices> setup, params string[] services)
    {
        var (ctl, svc, paths, _) = New(setup);
        var store = new ProfileStore(new LiveSettings(paths, new Core.Settings(), persist: true, new Log(Path.Combine(paths.DataRoot, "live.log"))));
        store.Save("Build box", services, atStart: true, 3);
        return (ctl, svc, store);
    }

    [Fact]
    public void AnUndoThatLeavesTheServiceOffKeepsItInTheProfile()
    {
        // The Edge updater's catalogue entry turns edgeupdatem off to Manual: Disabled and running, it was stopped (a change,
        // recorded with Disabled as its original); its Undo puts it back to Disabled, so it stays off, and the profile stays as it was.
        var (ctl, svc, store) = WithProfile(s =>
        {
            s.Add("edgeupdatem", ServiceStartType.Disabled, running: true);
            s.Add("MapsBroker", ServiceStartType.Disabled, running: false); // off outside PoolWatch
        }, "edgeupdatem", "MapsBroker");
        Assert.Equal(["edgeupdatem"], ctl.ApplyProfile(store.Current.Find("Build box")!, ProfileAtStart.Via("Build box")).TurnedOff);
        // A Windows default put back by the owner: its Undo turns MapsBroker off again.
        Assert.Null(Assert.Single(ctl.RestoreToDefault("MapsBroker")).error);
        var source = new ServicesSource(ctl, store);
        var rows = source.List().ToList();
        Assert.All(rows, r => Assert.Null(r.Warning)); // neither is turned on by its Undo: nothing to warn of

        // Undo all confirms every row: neither leaves the profile, and neither says it stays on.
        foreach (var row in rows)
        {
            var undone = source.Undo(row, confirmed: true);
            Assert.True(undone.Done);
            Assert.DoesNotContain("stays on", undone.Message);
        }
        Assert.Equal((ServiceStartType.Disabled, ServiceStartType.Disabled), (svc.Live["edgeupdatem"].StartType, svc.Live["MapsBroker"].StartType));
        Assert.Equal(["edgeupdatem", "MapsBroker"], store.Current.Find("Build box")!.Services);
    }

    [Fact]
    public void AConfirmedUndoTakesAServiceOutOfTheProfileOnlyForTheWarningItsRowShowed()
    {
        var (ctl, _, store) = WithProfile(s => s.Add("MapsBroker", ServiceStartType.Automatic, running: true), "MapsBroker");
        Assert.Single(ctl.ApplyProfile(store.Current.Find("Build box")!, ProfileAtStart.Via("Build box")).TurnedOff);
        var source = new ServicesSource(ctl, store);
        var row = Assert.Single(source.List());
        Assert.NotNull(row.Warning);
        // Undo all confirms every row it runs; a row whose warning was not shown leaves the profile as it is.
        Assert.Equal("MapsBroker is back to how it was.", source.Undo(row with { Warning = null }, confirmed: true).Message);
        Assert.Equal(["MapsBroker"], store.Current.Find("Build box")!.Services);

        // The warning was shown, but the record Undo finds was saved again since (by the command line) with Disabled as
        // its original: putting it back leaves the service off, so the profile stays as it is.
        Assert.Single(ctl.ApplyProfile(store.Current.Find("Build box")!, ProfileAtStart.Via("Build box")).TurnedOff);
        row = Assert.Single(source.List());
        Assert.NotNull(row.Warning);
        var resaved = Assert.Single(ctl.Current()) with { OriginalStart = ServiceStartType.Disabled, Via = "command line" };
        var paths = new AppPaths(Path.Combine(_root, "data"), null, secure: false);
        File.WriteAllText(Path.Combine(paths.DataRoot, "service-changes.json"), Core.Services.ServiceChangeLog.ToJson([resaved]));
        Assert.Equal("MapsBroker is back to how it was.", source.Undo(row, confirmed: true).Message);
        Assert.Equal(["MapsBroker"], store.Current.Find("Build box")!.Services);
    }

    [Fact]
    public void AnOldRecordOfAServiceThatWasOffAlreadyHasNoWarningAndItsUndoKeepsTheProfile()
    {
        // Recorded by an Apply of a service that was off already (Manual, as its entry turns it off): nothing to put back on.
        string old = Core.Services.ServiceChangeLog.ToJson([new ServiceChange
        {
            Service = "edgeupdatem", DisplayName = "edgeupdatem", OriginalStart = ServiceStartType.Manual, OriginalRunning = false,
            NewStart = ServiceStartType.Manual, ChangedUtc = DateTime.UtcNow, Via = "profile Build box",
        }]);
        var (ctl, svc, paths, _) = New(s => s.Add("edgeupdatem", ServiceStartType.Manual, running: false), changes: old);
        var store = new ProfileStore(new LiveSettings(paths, new Core.Settings(), persist: true, new Log(Path.Combine(paths.DataRoot, "live.log"))));
        store.Save("Build box", ["edgeupdatem"], atStart: true, 3);
        var source = new ServicesSource(ctl, store);
        var row = Assert.Single(source.List());
        Assert.Null(row.Warning);
        Assert.Equal("edgeupdatem is back to how it was.", source.Undo(row, confirmed: true).Message);
        Assert.Equal(["edgeupdatem"], store.Current.Find("Build box")!.Services);
    }

    [Fact]
    public void RestoreFromTheTabTakesOutOfTheProfileOnlyTheServicesItTurnedOn()
    {
        var (ctl, svc, store) = WithProfile(s =>
        {
            s.Add("MapsBroker", ServiceStartType.Automatic, running: true);
            s.Add("XblAuthManager", ServiceStartType.Disabled, running: true);
        }, "MapsBroker", "XblAuthManager");
        Assert.Equal(2, ctl.ApplyProfile(store.Current.Find("Build box")!, ProfileAtStart.Via("Build box")).TurnedOff.Count);

        var results = store.RestoreKeepingOn(ctl, null, ["MapsBroker"]);                // Restore all's question named MapsBroker
        Assert.Equal(ServiceStartType.Automatic, svc.Live["MapsBroker"].StartType);   // on again: out of the profile
        Assert.Equal(ServiceStartType.Disabled, svc.Live["XblAuthManager"].StartType); // back to Disabled: still in it
        Assert.Equal(["XblAuthManager"], store.Current.Find("Build box")!.Services);
        Assert.Contains(("(profile)", "Taken out of Build box, so PoolWatch no longer turns it off when it starts."), results);
        Assert.Empty(ctl.Current());

        // Restore of one service that its Restore leaves off: the profile stays as it was, and nothing is said of it.
        Assert.Single(ctl.ApplyProfile(store.Current.Find("Build box")!, ProfileAtStart.Via("Build box")).AlreadyOff);
        svc.Live["XblAuthManager"] = svc.Live["XblAuthManager"] with { Running = true };
        Assert.Single(ctl.ApplyProfile(store.Current.Find("Build box")!, ProfileAtStart.Via("Build box")).TurnedOff);
        Assert.DoesNotContain(store.RestoreKeepingOn(ctl, "XblAuthManager", []), r => r.service == "(profile)");
        Assert.Equal(["XblAuthManager"], store.Current.Find("Build box")!.Services);
    }

    [Fact]
    public void ARestoreTakesOutOfTheProfileOnlyTheServicesItsQuestionNamed()
    {
        // Restore all asked about MapsBroker only: the run at start turned SysMain off while the question was open (or the
        // command line did). Both come back on; SysMain stays in the profile, and the result says so.
        var (ctl, svc, store) = WithProfile(s =>
        {
            s.Add("MapsBroker", ServiceStartType.Automatic, running: true);
            s.Add("SysMain", ServiceStartType.Automatic, running: true);
        }, "MapsBroker", "SysMain");
        Assert.Equal(2, ctl.ApplyProfile(store.Current.Find("Build box")!, ProfileAtStart.Via("Build box")).TurnedOff.Count);
        var results = store.RestoreKeepingOn(ctl, null, ["MapsBroker"]);
        Assert.Equal((ServiceStartType.Automatic, ServiceStartType.Automatic), (svc.Live["MapsBroker"].StartType, svc.Live["SysMain"].StartType));
        Assert.Equal(["SysMain"], store.Current.Find("Build box")!.Services);
        Assert.Equal("MapsBroker, SysMain: done\nTaken out of Build box, so PoolWatch no longer turns it off when it starts.\n" +
                     "Left in Build box, as Put all back did not ask about it, so PoolWatch turns it off again when it starts: SysMain.",
            Ui.ServicesPageText.ResultMessage(results, ""));

        // No question at all (none was in the profile when Put all back was pressed): the profile stays as it is.
        Assert.Single(ctl.ApplyProfile(store.Current.Find("Build box")!, ProfileAtStart.Via("Build box")).TurnedOff);
        Assert.Contains(("(profile)", "Left in Build box, as Put back did not ask about it, so PoolWatch turns it off again when it starts: SysMain."),
            store.RestoreKeepingOn(ctl, "SysMain", []));
        Assert.Equal(["SysMain"], store.Current.Find("Build box")!.Services);
    }

    [Fact]
    public void RestoreToWindowsDefaultWhoseQuestionDidNotNameTheProfileLeavesTheServiceInIt()
    {
        var (ctl, svc, store) = WithProfile(s => s.Add("DiagTrack", ServiceStartType.Disabled, running: false), "DiagTrack");
        var results = store.DefaultKeepingOn(ctl, ctl.Options().Single(o => o.Service.Name == "DiagTrack"), confirmed: false);
        Assert.Equal(ServiceStartType.Automatic, svc.Live["DiagTrack"].StartType);
        Assert.Equal(["DiagTrack"], store.Current.Find("Build box")!.Services);
        Assert.Equal(("(profile)", "Left in Build box, as Set to its Windows default did not ask about it, so PoolWatch turns it off again when it starts: DiagTrack."), results[1]);
    }

    [Fact]
    public void RestoreToWindowsDefaultTakesTheServiceOutOfTheProfileSoTheNextStartLeavesItOn()
    {
        // DiagTrack and edgeupdatem are off outside PoolWatch (Disabled by the owner), both in the profile applied at start.
        var (ctl, svc, store) = WithProfile(s =>
        {
            s.Add("DiagTrack", ServiceStartType.Disabled, running: false);
            s.Add("edgeupdatem", ServiceStartType.Disabled, running: false);
        }, "DiagTrack", "edgeupdatem");
        var options = ctl.Options().ToDictionary(o => o.Service.Name);
        Assert.True(options["DiagTrack"].CanRestoreDefault && options["edgeupdatem"].CanRestoreDefault);

        // DiagTrack's default is Automatic: on, so the profile would turn it off at the next start. It comes out of it.
        var results = store.DefaultKeepingOn(ctl, options["DiagTrack"], confirmed: true);
        Assert.Equal(("DiagTrack", (string?)null), results[0]);
        Assert.Equal(("(profile)", "Taken out of Build box, so PoolWatch no longer turns it off when it starts."), results[1]);
        Assert.Equal(ServiceStartType.Automatic, svc.Live["DiagTrack"].StartType);
        Assert.Equal(["edgeupdatem"], store.Current.Find("Build box")!.Services);
        // edgeupdatem's default is Manual, as its entry turns it off: the profile leaves it alone, so it stays in the profile.
        Assert.DoesNotContain(store.DefaultKeepingOn(ctl, options["edgeupdatem"], confirmed: false), r => r.service == "(profile)");
        Assert.Equal(["edgeupdatem"], store.Current.Find("Build box")!.Services);

        // The next start does not turn DiagTrack off again.
        var run = ctl.ApplyProfile(store.Current.Find("Build box")!, ProfileAtStart.Via("Build box"));
        Assert.Empty(run.TurnedOff);
        Assert.Equal(ServiceStartType.Automatic, svc.Live["DiagTrack"].StartType);
        Assert.Equal("Build box, the profile PoolWatch applies when it starts, turns DiagTrack off again then. Set to its Windows default also takes it out of " +
                     "Build box, so it stays on.", Ui.ProfileText.RestoreKeepsOn(new ServiceProfileSet { Profiles = [new("Build box", ["DiagTrack"])], AtStart = "Build box" },
                         ["DiagTrack"], action: "Set to its Windows default"));
    }

    [Fact]
    public void ARestoreThatFailsLeavesTheProfileAndTheTicksAsTheyWere()
    {
        // The profile applied at start changes, and the tab unticks, only when a restore put the service back on.
        var (ctl, svc, store) = WithProfile(s =>
        {
            s.Add("MapsBroker", ServiceStartType.Automatic, running: true);
            s.Add("DiagTrack", ServiceStartType.Disabled, running: false);
        }, "MapsBroker", "DiagTrack");
        var told = new List<string>();
        ctl.TurnedOn += services => told.AddRange(services);
        Assert.Single(ctl.ApplyProfile(store.Current.Find("Build box")!, ProfileAtStart.Via("Build box")).TurnedOff);

        // Restore all fails for MapsBroker: uninstalled since.
        svc.Live.Remove("MapsBroker");
        Assert.Equal(("MapsBroker", (string?)"no longer installed"), Assert.Single(store.RestoreKeepingOn(ctl, null, ["MapsBroker"])));
        Assert.Equal(["MapsBroker", "DiagTrack"], store.Current.Find("Build box")!.Services);

        // Restore to Windows default refused by the plan: DiagTrack was set to Manual after its row was read.
        var option = ctl.Options().Single(o => o.Service.Name == "DiagTrack");
        svc.Live["DiagTrack"] = svc.Live["DiagTrack"] with { StartType = ServiceStartType.Manual };
        Assert.NotNull(Assert.Single(store.DefaultKeepingOn(ctl, option, confirmed: true)).error);
        Assert.Equal(["MapsBroker", "DiagTrack"], store.Current.Find("Build box")!.Services);

        // Refused by the service control manager after its record was saved: the record is taken back, nothing else moves.
        svc.Live["DiagTrack"] = svc.Live["DiagTrack"] with { StartType = ServiceStartType.Disabled };
        svc.DefaultError = "access denied";
        option = ctl.Options().Single(o => o.Service.Name == "DiagTrack");
        Assert.Equal(("DiagTrack", (string?)"access denied"), Assert.Single(store.DefaultKeepingOn(ctl, option, confirmed: true)));
        Assert.Equal(["MapsBroker", "DiagTrack"], store.Current.Find("Build box")!.Services);
        Assert.DoesNotContain(ctl.Current(), c => c.Service == "DiagTrack");
        Assert.Empty(told);
    }

    [Fact]
    public void TheServicesTabIsToldWhichServicesARestoreOrAnUndoTurnedOn()
    {
        var (ctl, svc, store) = WithProfile(s =>
        {
            s.Add("MapsBroker", ServiceStartType.Automatic, running: true);
            s.Add("XblAuthManager", ServiceStartType.Disabled, running: true);
            s.Add("DiagTrack", ServiceStartType.Disabled, running: false);
            s.Add("edgeupdatem", ServiceStartType.Disabled, running: false);
        }, "MapsBroker", "XblAuthManager");
        var told = new List<string>();
        ctl.TurnedOn += services => told.Add(string.Join(", ", services));
        ctl.ApplyProfile(store.Current.Find("Build box")!, ProfileAtStart.Via("Build box"));
        Assert.Empty(told);
        // Restore all: MapsBroker is on again; XblAuthManager is back to Disabled, so not said.
        ctl.Restore(null);
        Assert.Equal(["MapsBroker"], told);
        // An Undo in Active changes is a Restore.
        ctl.ApplyProfile(store.Current.Find("Build box")!, ProfileAtStart.Via("Build box"));
        new ServicesSource(ctl, store).Undo(new ServicesSource(ctl, store).List().Single(), confirmed: false);
        Assert.Equal(["MapsBroker", "MapsBroker"], told);
        // Restore to Windows default: Automatic is on; edgeupdatem's Manual is as its entry turns it off.
        ctl.RestoreToDefault("DiagTrack");
        ctl.RestoreToDefault("edgeupdatem");
        Assert.Equal(["MapsBroker", "MapsBroker", "DiagTrack"], told);
    }

    [Fact]
    public void ASelfRepairWarningNamesTheProfileOnlyWhenItWouldTurnTheServiceOff()
    {
        // edgeupdatem's entry turns it off to Manual. Disabled and running, Apply stopped it (Disabled kept); Windows then set
        // it to Manual: switched back on as the record reads it, but the profile at start leaves a Manual edgeupdatem alone.
        var (ctl, svc, store) = WithProfile(s => s.Add("edgeupdatem", ServiceStartType.Disabled, running: true), "edgeupdatem");
        Assert.Single(ctl.ApplyProfile(store.Current.Find("Build box")!, ProfileAtStart.Via("Build box")).TurnedOff);
        ctl.Profiles = () => store.Current;
        svc.Live["edgeupdatem"] = svc.Live["edgeupdatem"] with { StartType = ServiceStartType.Manual, Running = false };
        ctl.CheckSelfRepair(DateTime.UtcNow);
        var warning = Assert.Single(_findings, f => f.Title.StartsWith("Service re-enabled outside PoolWatch", StringComparison.Ordinal));
        Assert.DoesNotContain("Build box", warning.Title);
    }

    // ---------------------------------------------------------------- the tray app's start

    private (LiveSettings live, ServicesController ctl, FakeServices svc, List<string> log) App(ServiceProfileSet profiles)
    {
        var (ctl, svc, paths, _) = New(AfterAnUpdate);
        var live = new LiveSettings(paths, new Core.Settings { ServiceProfiles = profiles }, persist: false, new Log(Path.Combine(paths.DataRoot, "live.log")));
        return (live, ctl, svc, new List<string>());
    }

    [Fact]
    public async Task TheTrayAppAppliesTheRunningSettingsProfileThroughTheServicesController()
    {
        var (live, ctl, svc, log) = App(Set("Build box", 3, new ServiceProfile("Build box", ["SysMain", "MapsBroker"])));
        svc.Add("Fax", ServiceStartType.Automatic, running: true);
        var gate = new TaskCompletionSource();
        var done = new List<(string profile, ServicesController.ProfileRun run)>();
        Assert.Null(ctl.WaitingAtStart);
        var run = ProfileAtStart.ForApp(live, ctl, log.Add, smoke: false, explicitConfig: false, elevated: true, CancellationToken.None,
            (p, r) => done.Add((p, r)), (_, _) => gate.Task);
        // While it waits, "Turn them off now?" for Build box says this run turns off what it listed then.
        Assert.Equal("Build box", ctl.WaitingAtStart);
        // Edited on the Services tab while it waits: the running settings are read when the delay is up, and Fax, added
        // meanwhile, waits for the next start.
        live.Apply("service profile Build box saved", s => s.ServiceProfiles = Set("Build box", 3, new ServiceProfile("Build box", ["MapsBroker", "Fax"])));
        gate.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(ctl.WaitingAtStart);                  // the run is over: the question no longer says it waits
        Assert.Equal(["apply MapsBroker"], svc.Calls);   // through the controller's own Apply path, to the service control
        var (name, result) = Assert.Single(done);
        Assert.Equal("Build box", name);
        Assert.Equal(["MapsBroker"], result.TurnedOff);
        Assert.Equal("profile Build box, at start", Assert.Single(ctl.Current()).Via);
    }

    [Fact]
    public async Task TheRunReadsTheProfileOnlyWhenItsTurnComesSoAnEditMadeWhileItWaitsIsSeen()
    {
        // An Apply from the window holds the operation lock when the delay is up; while the run waits for its turn, the
        // owner stops the profile applying at start. The run reads the profile in its turn, so it turns nothing off.
        var (live, ctl, svc, log) = App(Set("Build box", 3, new ServiceProfile("Build box", ["MapsBroker"])));
        svc.Add("Fax", ServiceStartType.Automatic, running: true);
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        svc.BeforeChange = () => { svc.BeforeChange = null; entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); };
        var apply = Task.Run(() => ctl.Apply(["Fax"], "manual"));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var run = ProfileAtStart.ForApp(live, ctl, log.Add, smoke: false, explicitConfig: false, elevated: true, CancellationToken.None, null, (_, _) => Task.CompletedTask);
        await Task.Delay(400); // the run is waiting for its turn now
        live.Apply("service profile Build box saved", s => s.ServiceProfiles = Set(null, 3, new ServiceProfile("Build box", ["MapsBroker"])));
        release.Set();
        await apply.WaitAsync(TimeSpan.FromSeconds(10));
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["apply Fax"], svc.Calls);
        Assert.Contains("Service profile Build box is no longer chosen to apply at start: nothing is applied.", log);
    }

    /// <summary>A controller, a profile store whose "Build box" is applied at start, and the settings it writes through.</summary>
    private (ServicesController ctl, FakeServices svc, ProfileStore store, LiveSettings live) WithProfileAndSettings(Action<FakeServices> setup, params string[] services)
    {
        var (ctl, svc, paths, _) = New(setup);
        var live = new LiveSettings(paths, new Core.Settings(), persist: true, new Log(Path.Combine(paths.DataRoot, "live.log")));
        var store = new ProfileStore(live);
        store.Save("Build box", services, atStart: true, 3);
        return (ctl, svc, store, live);
    }

    [Theory]
    [InlineData("restore")]
    [InlineData("undo")]
    [InlineData("default")]
    public async Task ARestoreTakesTheServiceOutOfTheProfileBeforeAnyOtherChangeCanRun(string how)
    {
        // The run at start reads the profile in its own turn; a restore takes the service out of it in the restore's own
        // turn. So no change, the run included, can come between the restore and that edit: while the profile is being
        // edited, another change of services waits.
        var (ctl, svc, store, live) = WithProfileAndSettings(s =>
        {
            s.Add("MapsBroker", ServiceStartType.Automatic, running: true);
            s.Add("DiagTrack", ServiceStartType.Disabled, running: false);
            s.Add("Fax", ServiceStartType.Automatic, running: true);
        }, "MapsBroker", "DiagTrack");
        Assert.Single(ctl.ApplyProfile(store.Current.Find("Build box")!, ProfileAtStart.Via("Build box")).TurnedOff);
        Task<List<(string service, string? error)>>? other = null;
        bool? otherRanMeanwhile = null;
        live.Applied += _ =>
        {
            if (other is not null) return;
            other = Task.Run(() => ctl.Apply(["Fax"], "manual"));
            otherRanMeanwhile = other.Wait(400);
        };
        string service = how == "default" ? "DiagTrack" : "MapsBroker";
        switch (how)
        {
            case "restore": store.RestoreKeepingOn(ctl, "MapsBroker", ["MapsBroker"]); break;
            case "undo":
                var source = new ServicesSource(ctl, store);
                Assert.True(source.Undo(source.List().Single(r => r.Id == Core.Changes.ChangeIds.Service("MapsBroker")), confirmed: true).Done);
                break;
            default: store.DefaultKeepingOn(ctl, ctl.Options().Single(o => o.Service.Name == "DiagTrack"), confirmed: true); break;
        }
        Assert.False(otherRanMeanwhile);
        Assert.DoesNotContain(service, store.Current.Find("Build box")!.Services);
        Assert.Null(Assert.Single(await other!.WaitAsync(TimeSpan.FromSeconds(10))).error); // it ran once the restore was done
    }

    [Theory]
    [InlineData(true, false, true, "")]
    [InlineData(false, true, true, "Service profile Build box is not applied at start: settings given with --config never apply a profile at start; only PoolWatch's own settings.json does.")]
    [InlineData(false, false, false, "Service profile Build box is not applied at start: PoolWatch is not running as administrator, so it could not record how the services were set first.")]
    public async Task TheCiTestAConfigFileAndARunWithoutAdministratorRightsApplyNothing(bool smoke, bool explicitConfig, bool elevated, string said)
    {
        var (live, ctl, svc, log) = App(Set("Build box", 3, new ServiceProfile("Build box", ["MapsBroker"])));
        int waited = 0;
        var run = ProfileAtStart.ForApp(live, ctl, log.Add, smoke, explicitConfig, elevated, CancellationToken.None, null, (_, _) => { waited++; return Task.CompletedTask; });
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, waited);
        Assert.Empty(svc.Calls);
        Assert.Equal(said.Length == 0 ? [] : [said], log);
    }

    /// <summary>
    /// A service the run set to Disabled but that is not known to have stopped is neither turned off nor "could not turn
    /// off": the notice says what was done, where the log says why, and (when nothing else was turned off) where to see it.
    /// </summary>
    [Fact]
    public void TheTraySaysAServiceSetToDisabledButNotKnownToHaveStoppedIsNeitherTurnedOffNorNotTurnedOff()
    {
        const string Log = @"C:\ProgramData\PoolWatch\poolwatch.log";
        static ServicesController.ProfileRun Run(string[] off, int set, int failed) =>
            new(off, [], [], Enumerable.Range(0, failed).Select(i => ($"F{i}", "access denied")).ToList())
            {
                NotStopped = Enumerable.Range(0, set).Select(i => ($"N{i}", "set to Disabled, not stopped yet: it was still stopping after 20 seconds")).ToList(),
            };
        Assert.Equal(@"Build box set 1 service to Disabled, but it is not known to have stopped: C:\ProgramData\PoolWatch\poolwatch.log says why. " +
                     "Click here to see it in Active changes.", ProfileAtStart.Notice("Build box", Run([], 1, 0), Log));
        Assert.Equal(@"Build box set 2 services to Disabled, but they are not known to have stopped: C:\ProgramData\PoolWatch\poolwatch.log says why. " +
                     "Click here to see them in Active changes.", ProfileAtStart.Notice("Build box", Run([], 2, 0), Log));
        Assert.Equal("Build box turned off 1 service that was on: MapsBroker. Click here to see it in Active changes. It set 1 more service to " +
                     @"Disabled, but it is not known to have stopped: C:\ProgramData\PoolWatch\poolwatch.log says why.",
            ProfileAtStart.Notice("Build box", Run(["MapsBroker"], 1, 0), Log));
        // All three, past what the tray shows whole with the name: the name gives way, as for long names.
        string all = ProfileAtStart.Notice("Build box", Run(["MapsBroker"], 1, 2), Log)!;
        Assert.Equal("Build box turned off 1 service that was on. Click here to see it in Active changes. It set 1 more service to " +
                     @"Disabled, but it is not known to have stopped, and could not turn off 2 services: C:\ProgramData\PoolWatch\poolwatch.log says why.", all);
        Assert.InRange(all.Length, 1, ProfileAtStart.MaxNotice);
    }

    [Fact]
    public void TheTraySaysWhatARunTurnedOffWhereToSeeItAndWhereTheLogSaysWhyOneWasNot()
    {
        const string Log = @"C:\ProgramData\PoolWatch\poolwatch.log";
        ServicesController.ProfileRun Run(string[] off, int failed) => new(off, ["SysMain"], [], Enumerable.Range(0, failed).Select(i => ($"S{i}", "access denied")).ToList());
        Assert.Null(ProfileAtStart.Notice("Build box", Run([], 0), Log));
        // One service is "it"; the first run after a save turns off services that were on, never "on again".
        Assert.Equal("Build box turned off 1 service that was on: MapsBroker. Click here to see it in Active changes.",
            ProfileAtStart.Notice("Build box", Run(["MapsBroker"], 0), Log));
        Assert.Equal("Build box turned off 4 services that were on: A, B, C and 1 more. Click here to see them in Active changes.",
            ProfileAtStart.Notice("Build box", Run(["A", "B", "C", "D"], 0), Log));
        Assert.Equal("Build box turned off 5 services that were on: A, B, C and 2 more. Click here to see them in Active changes. " +
                     @"It could not turn off 1 service: C:\ProgramData\PoolWatch\poolwatch.log says why.", ProfileAtStart.Notice("Build box", Run(["A", "B", "C", "D", "E"], 1), Log));
        Assert.Equal(@"Build box could not turn off 2 services: C:\ProgramData\PoolWatch\poolwatch.log says why.", ProfileAtStart.Notice("Build box", Run([], 2), Log));
        // Long names give way, so the tray shows it whole (the log's place last of all).
        string name = new('P', 60);
        string[] longOff = ["ZoomCptServiceForVDIPluginMgmt", "IntelTelemetryAgent", "IntelCollectorService", "D"];
        string text = ProfileAtStart.Notice(name, Run(longOff, 1), Log)!;
        Assert.InRange(text.Length, 1, ProfileAtStart.MaxNotice);
        Assert.StartsWith(name + " turned off 4 services that were on", text);
        Assert.EndsWith(@"C:\ProgramData\PoolWatch\poolwatch.log says why.", text);
    }

    /// <summary>
    /// Every mix of turned off, set to Disabled but not known to have stopped, and could not turn off, for a profile whose
    /// name is as long as a name may be, one service each or many: the tray shows the notice whole, with every count, the
    /// log's place and "says why." Fewer names first, then the profile is not named, then where to see them goes; no word
    /// is cut.
    /// </summary>
    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, 0, 1)]
    [InlineData(1, 1, 0)]
    [InlineData(1, 0, 1)]
    [InlineData(0, 1, 1)]
    [InlineData(1, 1, 1)]
    public void ANoticeForAProfileWithTheLongestNameFitsTheTrayWhole(int off, int set, int failed)
    {
        const string Log = @"C:\ProgramData\PoolWatch\poolwatch.log";
        string name = new('P', ServiceProfileRules.MaxNameLength);
        foreach (int many in new[] { 1, 2, 100 })
        {
            int o = off * many, n = set * many, f = failed * many;
            string text = ProfileAtStart.Notice(name, NoticeRun(o, n, f), Log)!;
            Assert.InRange(text.Length, 1, ProfileAtStart.MaxNotice);
            Assert.EndsWith(".", text);
            if (o > 0) Assert.Contains($"turned off {o} service{(o == 1 ? "" : "s")} that", text);
            if (n > 0) Assert.Contains($"set {n}{(o > 0 ? " more" : "")} service{(n == 1 ? "" : "s")} to Disabled, but", text);
            if (f > 0) Assert.Contains($"could not turn off {f} service{(f == 1 ? "" : "s")}", text);
            if (n + f > 0) Assert.Contains(Log + " says why.", text);
        }
    }

    /// <summary>The words of each mix that is too long with the longest name, as the tray shows them.</summary>
    [Fact]
    public void ANoticeTooLongWithTheProfilesNameCallsItTheProfileAppliedAtStart()
    {
        const string Log = @"C:\ProgramData\PoolWatch\poolwatch.log";
        string name = new('P', ServiceProfileRules.MaxNameLength);
        Assert.Equal("The profile applied at start turned off 2 services that were on. Click here to see them in Active changes. It set 2 more services " +
                     @"to Disabled, but they are not known to have stopped: C:\ProgramData\PoolWatch\poolwatch.log says why.",
            ProfileAtStart.Notice(name, NoticeRun(2, 2, 0), Log));
        Assert.Equal("The profile applied at start set 2 services to Disabled, but they are not known to have stopped, and could not turn off 2 " +
                     @"services: C:\ProgramData\PoolWatch\poolwatch.log says why. Click here to see them in Active changes.",
            ProfileAtStart.Notice(name, NoticeRun(0, 2, 2), Log));
        // All three: where to see them goes too (a click on the notice opens Active changes all the same).
        Assert.Equal("The profile applied at start turned off 2 services that were on. It set 2 more services to Disabled, but they are not known " +
                     @"to have stopped, and could not turn off 2 services: C:\ProgramData\PoolWatch\poolwatch.log says why.",
            ProfileAtStart.Notice(name, NoticeRun(2, 2, 2), Log));
        // A longer log path (a folder given with --out): where to see them goes also when nothing was turned off.
        const string LongLog = @"D:\PoolWatch output\the build box in the office, kept for a month\poolwatch.log";
        Assert.Equal("The profile applied at start set 2 services to Disabled, but they are not known to have stopped, and could not turn off 2 " +
                     $"services: {LongLog} says why.", ProfileAtStart.Notice(name, NoticeRun(0, 2, 2), LongLog));
    }

    /// <summary>A run that turned off <paramref name="off"/> services, set <paramref name="set"/> to Disabled not known to have stopped, and failed <paramref name="failed"/>.</summary>
    private static ServicesController.ProfileRun NoticeRun(int off, int set, int failed) =>
        new(Enumerable.Range(0, off).Select(i => $"ServiceName{i}").ToList(), [], [], Enumerable.Range(0, failed).Select(i => ($"F{i}", "access denied")).ToList())
        {
            NotStopped = Enumerable.Range(0, set).Select(i => ($"N{i}", "set to Disabled, not stopped yet: it was still stopping after 20 seconds")).ToList(),
        };

    [Fact]
    public async Task ANoticeThatCannotBeShownIsNotLoggedAsAProfileNotApplied()
    {
        var log = new List<string>();
        var set = Set("A", 3, new ServiceProfile("A", ["SysMain"]));
        int applied = 0;
        var run = ProfileAtStart.Start(set, () => set, (_, _) => { applied++; return new(["SysMain"], [], [], []); }, log.Add, (_, _) => Task.CompletedTask,
            CancellationToken.None, (_, _) => throw new InvalidOperationException("the tray is gone"));
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, applied);
        Assert.Contains("Service profile A was applied at start, but its notice could not be shown: the tray is gone", log);
        Assert.DoesNotContain(log, l => l.Contains("not applied at start", StringComparison.Ordinal));
    }

    [Fact]
    public void AClickOnABalloonRunsOnlyItsOwnActionOnce()
    {
        var opened = new List<string>();
        var click = new Ui.BalloonClick();
        click.Shown(() => opened.Add("Active changes"));
        click.Clicked();
        click.Clicked();                                    // once
        Assert.Equal(["Active changes"], opened);
        click.Shown(() => opened.Add("guide"));
        click.Shown(null);                                  // a newer balloon with nothing to open
        click.Clicked();
        click.Shown(() => opened.Add("Active changes"));
        click.Closed();                                     // gone (timed out or closed): a click later opens nothing
        click.Clicked();
        Assert.Equal(["Active changes"], opened);
    }

    // ---------------------------------------------------------------- where a profile may come from

    [Fact]
    public void TheOldPerUserSettingsNeverBringServiceProfiles()
    {
        // That file was one an unelevated program could write; a profile is applied as an administrator at start.
        string json = AppPaths.LegacySettingsJson("{ \"retentionDays\": 5, \"serviceProfiles\": { \"profiles\": [{ \"name\": \"P\", \"services\": [\"SysMain\"] }], \"atStart\": \"P\", \"delayMinutes\": 3 } }");
        var s = Core.Settings.Parse(json);
        Assert.Equal(5, s.RetentionDays);
        Assert.Empty(s.ServiceProfiles.Profiles);
        Assert.Null(s.ServiceProfiles.AtStart);
    }
}
