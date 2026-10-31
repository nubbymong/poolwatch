using System.Text.Json;
using PoolWatch.Core;
using PoolWatch.Core.Services;

namespace PoolWatch.Tests;

/// <summary>
/// The owner's named service profiles in settings.json: read leniently (a profile not in PoolWatch's form is left out, never
/// the rest of the file), holding only services the catalogue offers and the protected gate allows, and the at-start
/// choice failing closed.
/// </summary>
public class ServiceProfilesTests
{
    private static readonly ServiceCatalogue Cat = DefaultCatalogue.Load();

    private static ServiceProfileSet Read(string serviceProfilesJson) =>
        Settings.Parse($"{{ \"retentionDays\": 5, \"serviceProfiles\": {serviceProfilesJson} }}").ServiceProfiles;

    [Fact]
    public void ProfilesRoundTripThroughSettingsJson()
    {
        var s = new Settings
        {
            ServiceProfiles = new ServiceProfileSet
            {
                Profiles = [new("Build box", ["SysMain", "MapsBroker"]), new("Quiet evening", ["DiagTrack"])],
                AtStart = "Build box", DelayMinutes = 7,
            },
        };
        string json = s.ToJson();
        using (var doc = JsonDocument.Parse(json))
        {
            var p = doc.RootElement.GetProperty("serviceProfiles");
            Assert.Equal("Build box", p.GetProperty("atStart").GetString());
            Assert.Equal(7, p.GetProperty("delayMinutes").GetInt32());
            Assert.Equal(["SysMain", "MapsBroker"], p.GetProperty("profiles")[0].GetProperty("services").EnumerateArray().Select(x => x.GetString()));
        }
        var back = Settings.Parse(json).ServiceProfiles;
        Assert.Equal(["Build box", "Quiet evening"], back.Profiles.Select(p => p.Name));
        Assert.Equal(["SysMain", "MapsBroker"], back.Find(" build box ")!.Services); // any case, spaces at the ends ignored
        Assert.True(back.IsAtStart("BUILD BOX"));
        Assert.Equal(7, back.DelayMinutes);
        Assert.Empty(back.Problems);
    }

    [Fact]
    public void AProfileWhoseTextIsNotValidIsLeftOutAndNothingThrows()
    {
        // A lone half of a surrogate pair (written as a JSON escape) is not text: reading it must not throw out of the reader,
        // which says it never throws, and the profile (or the start's choice) is left out with why.
        const string Lone = "\\ud800";
        var s = Settings.Parse("{ \"retentionDays\": 5, \"serviceProfiles\": { \"profiles\": [" +
                               $"{{ \"name\": \"Bad{Lone}\", \"services\": [\"SysMain\"] }}, " +
                               $"{{ \"name\": \"Build box\", \"services\": [\"Sys{Lone}Main\"] }}, " +
                               "{ \"name\": \"Quiet\", \"services\": [\"DiagTrack\"] } ], " +
                               $"\"atStart\": \"Quiet{Lone}\" }} }}");
        Assert.Equal(5, s.RetentionDays);
        Assert.Equal(["Quiet"], s.ServiceProfiles.Profiles.Select(p => p.Name));
        Assert.Null(s.ServiceProfiles.AtStart);
        Assert.Contains("A service profile in settings.json is not in the form PoolWatch writes: it is left out.", s.ServiceProfiles.Problems);
        Assert.Contains("Service profile Build box left out: it is not in the form PoolWatch writes.", s.ServiceProfiles.Problems);
        Assert.Contains(s.ServiceProfiles.Problems, p => p.StartsWith("The service profile to apply at start cannot be read", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("5")]                                                         // not an object
    [InlineData("[]")]
    [InlineData("{ \"profiles\": 5 }")]                                       // not a list
    [InlineData("{ \"profiles\": { \"name\": \"x\" } }")]
    public void ProfilesNotInPoolWatchsFormAreAllLeftOutAndTheRestOfTheFileIsRead(string bad)
    {
        var s = Settings.Parse($"{{ \"retentionDays\": 5, \"serviceProfiles\": {bad} }}");
        Assert.Equal(5, s.RetentionDays);           // the rest of settings.json is read as before
        Assert.Empty(s.ServiceProfiles.Profiles);
        Assert.Null(s.ServiceProfiles.AtStart);
        Assert.Contains(s.ServiceProfiles.Problems, p => p.Contains("not in the form PoolWatch writes"));
    }

    [Theory]
    [InlineData("{ \"services\": [\"SysMain\"] }", "A service profile in settings.json is not in the form PoolWatch writes: it is left out.")]
    [InlineData("{ \"name\": 7, \"services\": [\"SysMain\"] }", "A service profile in settings.json is not in the form PoolWatch writes: it is left out.")]
    [InlineData("{ \"name\": \"Bad\", \"services\": \"SysMain\" }", "Service profile Bad left out: it is not in the form PoolWatch writes.")]
    [InlineData("{ \"name\": \"Bad\", \"services\": [\"SysMain\", 3] }", "Service profile Bad left out: it is not in the form PoolWatch writes.")]
    [InlineData("{ \"name\": \"Bad\" }", "Service profile Bad left out: it is not in the form PoolWatch writes.")]
    [InlineData("\"Bad\"", "A service profile in settings.json is not in the form PoolWatch writes: it is left out.")]
    public void AProfileNotInPoolWatchsFormIsLeftOutAndTheOthersAreKept(string bad, string why)
    {
        var set = Read($"{{ \"profiles\": [{bad}, {{ \"name\": \"Good\", \"services\": [\"SysMain\"] }}], \"atStart\": \"Good\", \"delayMinutes\": 3 }}");
        Assert.Equal(["Good"], set.Profiles.Select(p => p.Name));
        Assert.True(set.IsAtStart("Good"));
        Assert.Equal([why], set.Problems);
    }

    [Fact]
    public void AnEntryNamingAProtectedServiceOrOneTheCatalogueDoesNotListIsIgnoredAtLoad()
    {
        var set = Read("{ \"profiles\": [{ \"name\": \"Mixed\", \"services\": [\"SysMain\", \"WinDefend\", \"SomeVendorSvc\", \"wisvc\", \"sysmain\", \"mapsbroker\"] }] }");
        Assert.Equal(["SysMain", "MapsBroker"], Assert.Single(set.Profiles).Services); // each once, in the catalogue's spelling
        Assert.Contains("Service profile Mixed: WinDefend ignored: it is a protected service, which PoolWatch never changes.", set.Problems);
        Assert.Contains("Service profile Mixed: SomeVendorSvc ignored: it is not on PoolWatch's list of services you may turn off.", set.Problems);
        // Whether the PC is enrolled in Windows Insider is not known when settings are read: its service counts as protected.
        Assert.Contains("Service profile Mixed: wisvc ignored: it is a protected service, which PoolWatch never changes.", set.Problems);
    }

    [Fact]
    public void AProtectedServiceIsIgnoredEvenWhenACatalogueListsIt()
    {
        // The gate is hard-coded: a catalogue that listed a protected service could not put it in a profile.
        var cat = new ServiceCatalogue([new CatalogueEntry { Services = ["WinDefend", "SysMain"], Name = "x", Category = "Telemetry and diagnostics" }]);
        var set = ServiceProfileRules.Clean(new ServiceProfileSet { Profiles = [new("P", ["WinDefend", "SysMain"])] }, cat);
        Assert.Equal(["SysMain"], Assert.Single(set.Profiles).Services);
        Assert.Single(set.Problems, p => p.Contains("WinDefend ignored: it is a protected service"));
    }

    [Theory]
    [InlineData("Telemetry only", "built-in profile")]
    [InlineData("agentic coding pc ", "built-in profile")]
    [InlineData("Custom (tick by hand)", "is not a profile")]
    [InlineData("", "needs a name")]
    [InlineData("Line\nbreak", "control character")]
    [InlineData("A name that is much longer than sixty characters, which no dialog of PoolWatch's lets you type", "at most 60 characters")]
    public void AProfileWithANameItCannotHaveIsLeftOut(string name, string why)
    {
        var set = ServiceProfileRules.Clean(new ServiceProfileSet { Profiles = [new(name, ["SysMain"]), new("Kept", ["SysMain"])] }, Cat);
        Assert.Equal(["Kept"], set.Profiles.Select(p => p.Name));
        Assert.Contains(why, Assert.Single(set.Problems));
        Assert.DoesNotContain('\n', set.Problems[0]); // a name is never written into the log as it is
    }

    [Fact]
    public void ADuplicateTooManyServicesOrSomethingThatIsNotAServiceNameLeavesThatProfileOut()
    {
        var many = Enumerable.Range(0, ServiceProfileRules.MaxServicesPerProfile + 1).Select(i => "Svc" + i).ToList();
        var set = ServiceProfileRules.Clean(new ServiceProfileSet
        {
            Profiles =
            [
                new("One", ["SysMain"]), new("one", ["MapsBroker"]), new("Many", many), new("Path", ["SysMain", @"..\SysMain"]), new("Slash", ["a/b"]),
                new("Empty", [""]), new("Long", [new string('a', ServiceProfileRules.MaxServiceNameLength + 1)]), new("Spaced", [" SysMain"]),
            ],
        }, Cat);
        Assert.Equal(["One"], set.Profiles.Select(p => p.Name));
        Assert.Equal(["SysMain"], set.Profiles[0].Services);       // the first of the two of the same name
        Assert.Equal(7, set.Problems.Count);
        foreach (var name in new[] { "Slash", "Empty", "Long", "Spaced" })
            Assert.Contains($"Service profile {name} left out: it lists something that is not a service name.", set.Problems);
        Assert.Contains("Service profile one left out: another profile has the same name.", set.Problems);
        Assert.Contains($"Service profile Many left out: it lists more than {ServiceProfileRules.MaxServicesPerProfile} services.", set.Problems);
        Assert.Contains("Service profile Path left out: it lists something that is not a service name.", set.Problems);
    }

    [Fact]
    public void AtMostFiftyProfilesAreKept()
    {
        var set = ServiceProfileRules.Clean(new ServiceProfileSet
        {
            Profiles = Enumerable.Range(1, ServiceProfileRules.MaxProfiles + 2).Select(i => new ServiceProfile("P" + i, ["SysMain"])).ToList(),
        }, Cat);
        Assert.Equal(ServiceProfileRules.MaxProfiles, set.Profiles.Count);
        Assert.Equal(2, set.Problems.Count);
    }

    [Theory]
    [InlineData("\"atStart\": 5, \"delayMinutes\": 3", "cannot be read")]           // which profile: not a name
    [InlineData("\"atStart\": \"Good\", \"delayMinutes\": 99", "not 1 to 15 minutes")]
    [InlineData("\"atStart\": \"Good\", \"delayMinutes\": 0", "not 1 to 15 minutes")]
    [InlineData("\"atStart\": \"Good\", \"delayMinutes\": \"3\"", "not 1 to 15 minutes")]
    [InlineData("\"atStart\": \"Good\", \"delayMinutes\": 2.5", "not 1 to 15 minutes")]
    public void WhenWhatToApplyAtStartCannotBeReadNothingIsAppliedAtStart(string start, string why)
    {
        var set = Read($"{{ \"profiles\": [{{ \"name\": \"Good\", \"services\": [\"SysMain\"] }}], {start} }}");
        Assert.Null(set.AtStart);                                   // fail closed: no profile is applied at start
        Assert.Equal(["Good"], set.Profiles.Select(p => p.Name));   // the profile itself is still there to pick
        Assert.Contains(why, Assert.Single(set.Problems));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    public void ADelayFromOneToFifteenMinutesIsKept(int minutes)
    {
        var set = Read($"{{ \"profiles\": [{{ \"name\": \"Good\", \"services\": [\"SysMain\"] }}], \"atStart\": \"Good\", \"delayMinutes\": {minutes} }}");
        Assert.Equal((true, minutes), (set.IsAtStart("Good"), set.DelayMinutes));
        Assert.Empty(set.Problems);
    }

    [Fact]
    public void ReadingTwiceAddsNoProblemAndANormalizedSetStaysTheSame()
    {
        var s = Settings.Parse("{ \"serviceProfiles\": { \"profiles\": [{ \"name\": \"P\", \"services\": [\"SysMain\", \"WinDefend\"] }] } }");
        s.Normalize();
        s.Normalize();
        Assert.Single(s.ServiceProfiles.Problems);
        Assert.Equal(["SysMain"], s.ServiceProfiles.Profiles[0].Services);
        Assert.Equal(s.ToJson(), Settings.Parse(s.ToJson()).ToJson());
    }

    [Fact]
    public void TheProfilesAreNotInTheAdvancedSettingsGrid()
    {
        // The Services tab saves them (each through the catalogue and the gate); the grid would write them as they are.
        Assert.False(System.ComponentModel.TypeDescriptor.GetProperties(typeof(Settings))[nameof(Settings.ServiceProfiles)]!.IsBrowsable);
    }

    [Fact]
    public void ANullOrMissingSetIsEmpty()
    {
        Assert.Empty(Settings.Parse("{ \"serviceProfiles\": null }").ServiceProfiles.Profiles);
        var none = Settings.Parse("{}").ServiceProfiles;
        Assert.Empty(none.Profiles);
        Assert.Null(none.AtStart);
        Assert.Equal(ServiceProfileRules.DefaultDelayMinutes, none.DelayMinutes);
        Assert.Empty(none.Problems);
    }

    // ---------------------------------------------------------------- save, rename, delete

    [Fact]
    public void SavingANewNameAddsItAndTheSameNameReplacesItInItsPlace()
    {
        var set = new ServiceProfileSet { Profiles = [new("A", ["SysMain"]), new("B", ["MapsBroker"])] };
        set = ServiceProfileRules.Saved(set, " C ", ["Fax"], atStart: false, 3);
        Assert.Equal(["A", "B", "C"], set.Profiles.Select(p => p.Name));
        set = ServiceProfileRules.Saved(set, "a", ["Fax", "fax", "RetailDemo"], atStart: false, 3);
        Assert.Equal(["a", "B", "C"], set.Profiles.Select(p => p.Name));
        Assert.Equal(["Fax", "RetailDemo"], set.Profiles[0].Services);
        Assert.Null(set.AtStart);
        Assert.Throws<ArgumentException>(() => ServiceProfileRules.Saved(set, "Agentic coding PC", ["Fax"], false, 3));
        Assert.Throws<ArgumentException>(() => ServiceProfileRules.Saved(set, "  ", ["Fax"], false, 3));
    }

    [Fact]
    public void AtMostOneProfileIsAppliedAtStart()
    {
        var set = new ServiceProfileSet { Profiles = [new("A", ["SysMain"]), new("B", ["MapsBroker"])], AtStart = "A", DelayMinutes = 3 };
        set = ServiceProfileRules.Saved(set, "B", ["MapsBroker"], atStart: true, 99);
        Assert.True(set.IsAtStart("B"));
        Assert.False(set.IsAtStart("A"));
        Assert.Equal(ServiceProfileRules.MaxDelayMinutes, set.DelayMinutes);       // clamped to 1 to 15
        set = ServiceProfileRules.Saved(set, "A", ["SysMain"], atStart: false, 1);
        Assert.True(set.IsAtStart("B"));                                           // saving another one without it leaves B
        Assert.Equal(ServiceProfileRules.MaxDelayMinutes, set.DelayMinutes);
        set = ServiceProfileRules.Saved(set, "b", ["MapsBroker"], atStart: false, 1);
        Assert.Null(set.AtStart);                                                  // unticked on the one at start: none is
    }

    [Fact]
    public void RenamingKeepsThePlaceAndTheStartFollowsIt()
    {
        var set = new ServiceProfileSet { Profiles = [new("A", ["SysMain"]), new("B", ["MapsBroker"])], AtStart = "a", DelayMinutes = 5 };
        set = ServiceProfileRules.Renamed(set, "A", "Build box");
        Assert.Equal(["Build box", "B"], set.Profiles.Select(p => p.Name));
        Assert.True(set.IsAtStart("Build box"));
        Assert.Equal(5, set.DelayMinutes);
        set = ServiceProfileRules.Renamed(set, "Build box", "build BOX");                     // the same profile, another case
        Assert.Equal("build BOX", set.Profiles[0].Name);
        Assert.Throws<ArgumentException>(() => ServiceProfileRules.Renamed(set, "build box", "b"));  // another profile's name
        Assert.Throws<ArgumentException>(() => ServiceProfileRules.Renamed(set, "B", "Telemetry only"));
        Assert.Throws<ArgumentException>(() => ServiceProfileRules.Renamed(set, "Missing", "C"));
    }

    [Fact]
    public void ARenameOrASaveOntoAChoiceAtStartThatNamedNoProfileNeverMakesThatProfileApplyAtStart()
    {
        // The choice at start names B while no profile is called B (written by hand, or B was left out when settings.json
        // was read). Renaming A to B does not make A apply at start: its box was never ticked.
        var set = new ServiceProfileSet { Profiles = [new("A", ["SysMain"]), new("C", ["Fax"])], AtStart = "B", DelayMinutes = 4 };
        var renamed = ServiceProfileRules.Renamed(set, "A", " b ");
        Assert.Equal(["b", "C"], renamed.Profiles.Select(p => p.Name));
        Assert.False(renamed.IsAtStart("b"));
        Assert.Null(renamed.AtStart);
        // A rename of another profile leaves the choice as it is; saving a new B unticked drops it, ticked it is B.
        Assert.Equal("B", ServiceProfileRules.Renamed(set, "A", "D").AtStart);
        Assert.Null(ServiceProfileRules.Saved(set, "B", ["MapsBroker"], atStart: false, 3).AtStart);
        Assert.True(ServiceProfileRules.Saved(set, "B", ["MapsBroker"], atStart: true, 3).IsAtStart("B"));
    }

    [Fact]
    public void ANewProfilePastFiftyIsRefusedAndTheFiftyCanStillBeReplacedAndRenamed()
    {
        var fifty = Enumerable.Range(1, ServiceProfileRules.MaxProfiles).Select(i => new ServiceProfile("P" + i, ["SysMain"])).ToList();
        var set = new ServiceProfileSet { Profiles = fifty, AtStart = "P1", DelayMinutes = 3 };
        // Reading the set would leave a 51st out, and the choice at start would then name no profile: it is refused.
        var refused = Assert.Throws<InvalidOperationException>(() => ServiceProfileRules.Saved(set, "P51", ["Fax"], atStart: true, 5));
        Assert.Equal(ServiceProfileRules.TooMany, refused.Message);
        set = ServiceProfileRules.Saved(set, "p50", ["Fax"], atStart: true, 5);       // one of the fifty, replaced in its place
        Assert.Equal(ServiceProfileRules.MaxProfiles, set.Profiles.Count);
        Assert.True(set.IsAtStart("P50"));
        set = ServiceProfileRules.Renamed(set, "P2", "Two");
        var read = ServiceProfileRules.Clean(set, DefaultCatalogue.Shared);
        Assert.Equal(ServiceProfileRules.MaxProfiles, read.Profiles.Count);
        Assert.Empty(read.Problems);
        Assert.NotNull(read.Find(read.AtStart));
    }

    [Fact]
    public void DeletingTheProfileAppliedAtStartLeavesNoneAtStart()
    {
        var set = new ServiceProfileSet { Profiles = [new("A", ["SysMain"]), new("B", ["MapsBroker"])], AtStart = "B", DelayMinutes = 5 };
        var other = ServiceProfileRules.Deleted(set, "a");
        Assert.Equal(["B"], other.Profiles.Select(p => p.Name));
        Assert.Equal("B", other.AtStart);
        var none = ServiceProfileRules.Deleted(set, "B");
        Assert.Equal(["A"], none.Profiles.Select(p => p.Name));
        Assert.Null(none.AtStart);
    }

    // ---------------------------------------------------------------- off or back on

    [Theory]
    [InlineData("SysMain", ServiceStartType.Disabled, false, true)]   // off as Apply leaves it
    [InlineData("SysMain", ServiceStartType.Disabled, true, false)]   // disabled, but still running
    [InlineData("SysMain", ServiceStartType.Automatic, false, false)] // back on (after a Windows update)
    [InlineData("SysMain", ServiceStartType.Manual, false, false)]
    [InlineData("edgeupdatem", ServiceStartType.Manual, false, true)]      // its catalogue entry turns it off to Manual
    [InlineData("edgeupdatem", ServiceStartType.Manual, true, true)]       // and Apply does not stop it then
    [InlineData("edgeupdatem", ServiceStartType.Automatic, false, false)]
    [InlineData("edgeupdatem", ServiceStartType.Disabled, false, true)]    // stricter than Apply leaves it: off, never loosened to Manual
    [InlineData("edgeupdatem", ServiceStartType.Disabled, true, false)]    // Disabled but running: Apply keeps it Disabled and stops it
    public void AServiceIsOffWhenItIsAsApplyLeavesIt(string name, ServiceStartType start, bool running, bool off)
    {
        var o = ServicePlan.Options([new ServiceView { Name = name, StartType = start, Running = running }], Cat, false, new HashSet<string>()).Single();
        Assert.Equal(off, ServicePlan.IsOff(o));
        Assert.Equal(name == "edgeupdatem" && start != ServiceStartType.Disabled ? ServiceStartType.Manual : ServiceStartType.Disabled, ServicePlan.OffStart(o));
    }

    /// <summary>
    /// Disabled is off only once the service is seen stopped: one that is paused, still starting or stopping, or whose
    /// state Windows could not give is not off yet (Apply asks for its stop again). Set to Manual, it is off as it is, as
    /// Apply does not stop it then.
    /// </summary>
    [Theory]
    [InlineData("SysMain", ServiceStartType.Disabled, false)]
    [InlineData("edgeupdatem", ServiceStartType.Disabled, false)]
    [InlineData("edgeupdatem", ServiceStartType.Manual, true)]
    public void ADisabledServiceNotSeenStoppedIsNotOffYet(string name, ServiceStartType start, bool off)
    {
        var view = new ServiceView { Name = name, StartType = start, Running = false, NeitherRunningNorStopped = true };
        var o = ServicePlan.Options([view], Cat, false, new HashSet<string>()).Single();
        Assert.Equal(off, ServicePlan.IsOff(o));
        Assert.Equal(off, ServicePlan.OffAlready(o));
    }

    [Fact]
    public void ABuiltInTicksOnlyTheServicesThatAreNotOffYet()
    {
        // WerSvc is turned off to Manual: Manual and stopped it is off already, so "Telemetry only" does not tick or count it.
        ServiceView V(string name, ServiceStartType start, bool running) => new() { Name = name, StartType = start, Running = running, MicrosoftSigned = true };
        var off = ServicePlan.Options([V("WerSvc", ServiceStartType.Manual, false), V("DiagTrack", ServiceStartType.Automatic, true)], Cat, false, new HashSet<string>());
        Assert.Equal(["DiagTrack"], ServicePlan.Tick(Preset.TelemetryOnly, off));
        var on = ServicePlan.Options([V("WerSvc", ServiceStartType.Automatic, false)], Cat, false, new HashSet<string>());
        Assert.Equal(["WerSvc"], ServicePlan.Tick(Preset.TelemetryOnly, on));
    }

    [Fact]
    public void TheProfileAppliedAtStartIsFoundForTheServicesItListsAndCanLoseSome()
    {
        var set = new ServiceProfileSet { Profiles = [new("Build box", ["DiagTrack", "SysMain", "MapsBroker"]), new("Other", ["Fax"])], AtStart = "build BOX" };
        var (profile, held) = ServiceProfileRules.AtStartHolding(set, ["mapsbroker", "Fax", "DiagTrack"]);
        Assert.Equal("Build box", profile);
        Assert.Equal(["DiagTrack", "MapsBroker"], held);
        var (none, nothing) = ServiceProfileRules.AtStartHolding(set, ["Fax"]);
        Assert.Null(none);
        Assert.Empty(nothing);
        Assert.Null(ServiceProfileRules.AtStartHolding(new ServiceProfileSet { Profiles = set.Profiles }, ["DiagTrack"]).Profile); // none applied at start
        var less = ServiceProfileRules.Without(set, "Build box", ["mapsbroker"]);
        Assert.Equal(["DiagTrack", "SysMain"], less.Find("Build box")!.Services);
        Assert.Equal(["Fax"], less.Find("Other")!.Services);
        Assert.Equal(("build BOX", set.DelayMinutes), (less.AtStart, less.DelayMinutes));
        Assert.Throws<ArgumentException>(() => ServiceProfileRules.Without(set, "Nope", ["Fax"]));
    }
}
