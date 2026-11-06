using PoolWatch.Core.Services;

namespace PoolWatch.Tests;

/// <summary>
/// What the services page and Put back say and decide about a service's state: an unknown state is never read as off,
/// stopped or "was not running"; paused is said as paused; a refused stop names every service that kept it from stopping,
/// each with its state; and a record's running state is a fact only when it was seen.
/// </summary>
public class ServiceStateWordsTests
{
    private static readonly DateTime T0 = new(2026, 10, 26, 11, 40, 0, DateTimeKind.Utc);
    private static readonly ServiceCatalogue Cat = DefaultCatalogue.Load();

    private static ServiceView V(string name, ServiceStartType start, bool running = false, bool between = false, bool unknown = false, ServiceRunState? state = null) =>
        new() { Name = name, DisplayName = name, StartType = start, Running = running, NeitherRunningNorStopped = between || unknown, StateUnknown = unknown, Between = state, MicrosoftSigned = true };

    private static ServiceOption O(ServiceView s, ServiceGroup group = ServiceGroup.Optional) => new() { Service = s, Group = group, Catalogue = Cat.Find(s.Name) };

    // ---------------------------------------------------------------- IsOff and an unknown state

    /// <summary>
    /// A Disabled service whose state Windows could not give is not off: Turn off asks for its stop again (if Windows can
    /// then say it runs), the preset ticks it, and Apply does not call it "off already". Only one seen stopped is off.
    /// </summary>
    [Theory]
    [InlineData(false, false, false, true)]  // seen stopped
    [InlineData(true, false, false, false)]  // running
    [InlineData(false, true, false, false)]  // paused, starting, stopping...
    [InlineData(false, false, true, false)]  // Windows could not give its state
    public void ADisabledServiceIsOffOnlyWhenSeenStopped(bool running, bool between, bool unknown, bool off)
    {
        var o = O(V("DiagTrack", ServiceStartType.Disabled, running, between, unknown));
        Assert.Equal(off, ServicePlan.IsOff(o));
        Assert.Equal(off, ServicePlan.OffAlready(o));
        Assert.Equal(!off, ServicePlan.Tick(Preset.TelemetryOnly, [o]).Contains("DiagTrack"));
    }

    /// <summary>
    /// A read says whether a service is on only when its start type is one Windows defines and Windows gave its state: one
    /// PoolWatch could not open, one whose start type alone or state alone could not be read, or a start type out of range
    /// is not read (the self-repair check reports, unmarks and forgets nothing on it). Paused, starting and the rest are
    /// states Windows gave: read.
    /// </summary>
    [Theory]
    [InlineData(ServiceStartType.Automatic, true, false, false, null, false)]
    [InlineData(ServiceStartType.Disabled, false, false, false, null, false)]
    [InlineData(ServiceStartType.Disabled, false, true, false, ServiceRunState.Paused, false)]
    [InlineData(ServiceStartType.Manual, false, true, false, ServiceRunState.Starting, false)]
    [InlineData(ServiceStartType.Unknown, true, false, false, null, true)]   // the configuration could not be read
    [InlineData((ServiceStartType)9, false, false, false, null, true)]       // a start type Windows does not define
    [InlineData(ServiceStartType.Disabled, false, true, true, null, true)]   // the state could not be read
    [InlineData(ServiceStartType.Automatic, false, true, true, null, true)]
    public void AReadThatDoesNotSayWhetherTheServiceIsOnIsNotRead(ServiceStartType start, bool running, bool between, bool unknown, ServiceRunState? state, bool notRead)
    {
        Assert.Equal(notRead, ServicePlan.NotRead(V("MapsBroker", start, running, between, unknown, state)));
        Assert.True(ServicePlan.NotRead(new ServiceView { Name = "MapsBroker", StartType = ServiceStartType.Unknown, StateUnknown = true, NeitherRunningNorStopped = true }));
    }

    /// <summary>
    /// Put back's gate refuses a record whose service's start type PoolWatch could not read now (one it cannot open, say),
    /// and says that: never that the service "was changed since", which nobody knows. Read as PoolWatch left it, it passes.
    /// </summary>
    [Fact]
    public void PutBackOfAServiceWhoseStartTypeCouldNotBeReadIsNotSaidToBeChangedSince()
    {
        var record = new ServiceChange
        {
            Service = "MapsBroker", OriginalStart = ServiceStartType.Automatic, OriginalRunning = true, NewStart = ServiceStartType.Disabled,
            ChangedUtc = T0, Via = "manual",
        };
        var unread = O(new ServiceView { Name = "MapsBroker", StateUnknown = true, NeitherRunningNorStopped = true });
        var e = Assert.Throws<InvalidOperationException>(() => ServicePlan.GateRestore(unread, record, false));
        Assert.Equal("'MapsBroker': PoolWatch could not read its start type now, so it cannot tell whether it is still Disabled as PoolWatch left it. " +
                     "Nothing was changed: Put back tries again when it can read it", e.Message);
        ServicePlan.GateRestore(O(V("MapsBroker", ServiceStartType.Disabled)), record, false);
    }

    // ---------------------------------------------------------------- the State and Cost columns

    /// <summary>
    /// A protected service, or one PoolWatch set to its Windows default, that is paused (or starting, stopping, resuming,
    /// pausing) reads as that state, never "off" or "stopped"; one whose state Windows could not give says so.
    /// </summary>
    [Theory]
    [InlineData(ServiceStartType.Disabled, ServiceRunState.Paused, false, "Disabled, paused")]
    [InlineData(ServiceStartType.Manual, ServiceRunState.Paused, false, "Manual, paused")]
    [InlineData(ServiceStartType.Automatic, ServiceRunState.Starting, false, "Automatic, starting")]
    [InlineData(ServiceStartType.Disabled, ServiceRunState.Stopping, false, "Disabled, stopping")]
    [InlineData(ServiceStartType.Automatic, null, true, "Automatic, state not known")]
    [InlineData(ServiceStartType.Disabled, null, false, "off")]
    [InlineData(ServiceStartType.Manual, null, false, "Manual, stopped")]
    public void AProtectedServiceReadsAsItsStateNeverOffWhilePaused(ServiceStartType start, ServiceRunState? state, bool unknown, string expected)
    {
        var s = V("RpcSs", start, between: state is not null, unknown: unknown, state: state);
        Assert.Equal(expected, ServicePlan.StateText(O(s, ServiceGroup.Protected)));
    }

    [Theory]
    [InlineData(ServiceStartType.Automatic, ServiceRunState.Paused, "Automatic, paused (put back to its Windows default by PoolWatch ")]
    [InlineData(ServiceStartType.Disabled, ServiceRunState.Paused, "Disabled, paused (changed outside PoolWatch after PoolWatch put back its Windows default ")]
    public void AServiceSetToItsWindowsDefaultReadsAsItsStateWhilePaused(ServiceStartType start, ServiceRunState state, string expected)
    {
        var change = new ServiceChange
        {
            Service = "MapsBroker", OriginalStart = ServiceStartType.Disabled, OriginalRunning = false, NewStart = ServiceStartType.Automatic,
            ChangedUtc = T0, Via = ServicePlan.ViaWindowsDefault,
        };
        var o = O(V("MapsBroker", start, between: true, state: state)) with { PoolWatchChange = change };
        Assert.StartsWith(expected, ServicePlan.StateText(o));
        Assert.DoesNotContain("off", ServicePlan.StateText(o).Split(' ')[0]);
    }

    /// <summary>A Disabled service not seen stopped names its state when Windows gave it; "not known to have stopped" when not.</summary>
    [Theory]
    [InlineData(ServiceRunState.Paused, false, "set to Disabled, paused (")]
    [InlineData(null, true, "set to Disabled, not known to have stopped (")]
    public void ADisabledServiceNotSeenStoppedNamesItsState(ServiceRunState? state, bool unknown, string expected)
    {
        var change = new ServiceChange
        {
            Service = "MapsBroker", OriginalStart = ServiceStartType.Automatic, OriginalRunning = true, NewStart = ServiceStartType.Disabled, ChangedUtc = T0, Via = "manual",
        };
        var s = V("MapsBroker", ServiceStartType.Disabled, between: state is not null, unknown: unknown, state: state);
        Assert.StartsWith(expected + "turned off by PoolWatch ", ServicePlan.StateText(O(s) with { PoolWatchChange = change }));
        var outside = ServicePlan.Options([s with { Name = "DiagTrack", DisplayName = "DiagTrack" }], Cat, false, new HashSet<string>()).Single();
        Assert.Equal(expected + "changed outside PoolWatch)", ServicePlan.StateText(outside));
    }

    /// <summary>The Cost column of a service that is not running says its state: "paused", never "not running", and "could not read".</summary>
    [Theory]
    [InlineData(false, false, null, ServiceStartType.Manual, "not running")]
    [InlineData(true, false, ServiceRunState.Paused, ServiceStartType.Manual, "paused")]
    [InlineData(true, true, null, ServiceStartType.Manual, "state not known")]
    [InlineData(true, true, null, ServiceStartType.Unknown, ServicePlan.CouldNotRead)]
    public void TheCostColumnSaysTheStateOfAServiceNotRunning(bool between, bool unknown, ServiceRunState? state, ServiceStartType start, string expected) =>
        Assert.Equal(expected, ServicePlan.CostText(V("MapsBroker", start, between: between, unknown: unknown, state: state)));

    // ---------------------------------------------------------------- a refused stop names every service that kept it

    /// <summary>
    /// Error 1051 names every service that depends on it and had not stopped, each with its state as Windows listed it
    /// (running or not: one paused, starting or stopping keeps it from stopping too), Windows' list before the one read
    /// earlier.
    /// </summary>
    [Fact]
    public void ARefusedStopNamesEveryDependentWithItsState()
    {
        var refused = new NotDone("a service that depends on it has not stopped (Windows error 1051)", NotDoneCause.DependentsRunning);
        IReadOnlyList<DependentService> listed = [new("AcmeMaps", ServiceRunState.Running)];

        Assert.Equal("set to Disabled, but still running: AcmeMaps depends on it and is running. It stops when Windows restarts, or when you turn it off again " +
                     "after that service has stopped", ServicePlan.NotStoppedText(ServiceStartType.Disabled, refused, listed));
        Assert.Equal("set to Disabled, but still running: AcmeSync depends on it and is paused. It stops when Windows restarts, or when you turn it off again " +
                     "after that service has stopped",
            ServicePlan.NotStoppedText(ServiceStartType.Disabled, refused with { Dependents = [new("AcmeSync", ServiceRunState.Paused)] }, listed));
        Assert.Equal("set to Disabled, but still running: AcmeMaps, AcmeSync depend on it and are running. It stops when Windows restarts, or when you turn it " +
                     "off again after those have stopped",
            ServicePlan.NotStoppedText(ServiceStartType.Disabled, refused with { Dependents = [new("AcmeMaps", ServiceRunState.Running), new("AcmeSync", ServiceRunState.Running)] }, []));
        Assert.Equal("set to Disabled, but still running: AcmeMaps (running), AcmeSync (starting) depend on it and have not stopped. It stops when Windows " +
                     "restarts, or when you turn it off again after those have stopped",
            ServicePlan.NotStoppedText(ServiceStartType.Disabled, refused with { Dependents = [new("AcmeMaps", ServiceRunState.Running), new("AcmeSync", ServiceRunState.Starting)] }, []));
        Assert.Equal("set to Disabled, but still running: a service that depends on it has not stopped (Windows error 1051). It stops when Windows restarts, " +
                     "or when you turn it off again", ServicePlan.NotStoppedText(ServiceStartType.Disabled, refused, []));
        // Another answer names none, whatever the list.
        Assert.DoesNotContain("AcmeMaps", ServicePlan.NotStoppedText(ServiceStartType.Disabled, new NotDone("Windows did not stop it (error 5)"), listed));
        Assert.Equal("it is still running: AcmeSync depends on it and is paused. It stops when Windows restarts",
            ServicePlan.StillRunningAfterPutBack(refused with { Dependents = [new("AcmeSync", ServiceRunState.Paused)] }));
        Assert.Equal("it is still running: AcmeMaps depends on it and is running. It stops when Windows restarts", ServicePlan.StillRunningAfterPutBack(refused, listed));
    }

    // ---------------------------------------------------------------- what the record says of whether it ran

    /// <summary>
    /// The record saved before a change says the service was running only when it was seen running (or paused, starting,
    /// resuming or pausing), and not known otherwise, also when it was seen stopped or stopping: it may start before the
    /// change reaches it, and only the change (which then records what it saw) can say it was not running. A record whose
    /// running state is not known is never started by Put back on its word. What the read itself says is
    /// <see cref="ServicePlan.WasRunning(ServiceView)"/>: stopped or stopping is "not running" there.
    /// </summary>
    [Theory]
    [InlineData(true, false, false, null, true, false, true)]
    [InlineData(false, false, false, null, false, true, false)] // seen stopped: not known until the change says
    [InlineData(false, true, false, ServiceRunState.Paused, true, false, true)]
    [InlineData(false, true, false, ServiceRunState.Starting, true, false, true)]
    [InlineData(false, true, false, ServiceRunState.Stopping, false, true, false)] // seen stopping: not known either
    [InlineData(false, true, true, null, false, true, null)]
    [InlineData(false, true, false, null, false, true, null)] // neither running nor stopped, which state not given
    public void TheRecordSaysItRanOnlyWhenThatWasSeen(bool running, bool between, bool unknown, ServiceRunState? state, bool originalRunning, bool notKnown,
        bool? read)
    {
        var s = V("MapsBroker", ServiceStartType.Automatic, running, between, unknown, state);
        var record = ServicePlan.BuildApply(["MapsBroker"], [O(s)], false, "manual", T0).Single().Record;
        Assert.Equal((originalRunning, notKnown), (record.OriginalRunning, record.OriginalRunningNotKnown));
        Assert.Equal(originalRunning, ServicePlan.StartAfterRestore(record));
        Assert.Equal(read, ServicePlan.WasRunning(s));
    }

    [Fact]
    public void ARecordWhoseRunningStateIsNotKnownIsNeverStartedOnItsWord()
    {
        var notKnown = new ServiceChange
        {
            Service = "MapsBroker", OriginalStart = ServiceStartType.Automatic, OriginalRunning = true, OriginalRunningNotKnown = true,
            NewStart = ServiceStartType.Disabled, ChangedUtc = T0,
        };
        Assert.False(ServicePlan.StartAfterRestore(notKnown));
        Assert.True(ServicePlan.RunningNotKnown(notKnown, V("MapsBroker", ServiceStartType.Disabled)));
        Assert.False(ServicePlan.RunningNotKnown(notKnown, V("MapsBroker", ServiceStartType.Disabled, running: true)));
        Assert.False(ServicePlan.AlreadyRestored(O(V("MapsBroker", ServiceStartType.Automatic)), notKnown));
        Assert.True(ServicePlan.AlreadyRestored(O(V("MapsBroker", ServiceStartType.Automatic, running: true)), notKnown));
    }

    /// <summary>
    /// A Disabled original is back only once the service is Disabled and seen stopped; one running, paused or whose state
    /// is not known is not, so Put back stops it.
    /// </summary>
    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    public void ADisabledOriginalIsBackOnlyOnceSeenStopped(bool running, bool between, bool unknown, bool back)
    {
        var setDefault = new ServiceChange
        {
            Service = "MapsBroker", OriginalStart = ServiceStartType.Disabled, OriginalRunning = false, NewStart = ServiceStartType.Automatic,
            ChangedUtc = T0, Via = ServicePlan.ViaWindowsDefault,
        };
        Assert.Equal(back, ServicePlan.AlreadyRestored(O(V("MapsBroker", ServiceStartType.Disabled, running, between, unknown)), setDefault));
    }

    /// <summary>
    /// service-changes.json as the published beta wrote it (no "originalRunningNotKnown", no "underWay"): that version saved
    /// "originalRunning" true only when the services list read the service as RUNNING, so its true reads as running and its
    /// false as not known, never as "was not running". A record written now always carries "originalRunningNotKnown", so
    /// its "not running" reads back as known; a record built in code is known unless it says otherwise; and a record with
    /// both new fields reads back as written.
    /// </summary>
    [Fact]
    public void TheChangeLogReadsTheBetasShapeAndTheNewFields()
    {
        const string Beta = """
            [{"service":"MapsBroker","displayName":"","originalStart":"Automatic","originalRunning":false,"newStart":"Disabled",
              "changedUtc":"2026-10-30T09:15:00Z","via":"manual","beforeCores":null,"beforePageFaultsPerSec":null,"notStoppedPid":null},
             {"service":"Fax","displayName":"","originalStart":"Manual","originalRunning":true,"newStart":"Disabled",
              "changedUtc":"2026-10-30T09:15:00Z","via":"manual","beforeCores":null,"beforePageFaultsPerSec":null,"notStoppedPid":null}]
            """;
        var beta = ServiceChangeLog.Parse(Beta)!;
        Assert.Equal([(false, true, (ChangeUnderWay?)null), (true, false, null)],
            beta.Select(c => (c.OriginalRunning, c.OriginalRunningNotKnown, c.UnderWay)));
        // Written again (any save writes every record), they say so, and read back the same.
        string again = ServiceChangeLog.ToJson(beta);
        Assert.Equal(2, again.Split("\"originalRunningNotKnown\": ").Length - 1);
        Assert.Equal(beta, ServiceChangeLog.Parse(again)!);

        // Written now, a known "not running": the field is written as false, and it reads back known.
        var old = beta[0] with { OriginalRunningNotKnown = false };
        string known = ServiceChangeLog.ToJson([old]);
        Assert.Contains("\"originalRunningNotKnown\": false", known);
        Assert.Equal(old, Assert.Single(ServiceChangeLog.Parse(known)!));
        Assert.False(new ServiceChange { Service = "Fax", OriginalStart = ServiceStartType.Manual, OriginalRunning = false, NewStart = ServiceStartType.Disabled }
            .OriginalRunningNotKnown);

        var now = old with { OriginalRunningNotKnown = true, UnderWay = new ChangeUnderWay { Pid = 4242, ProcessStarted = 133_000_000_000_000_000, TickMs = 98_765 } };
        Assert.Equal(now, Assert.Single(ServiceChangeLog.Parse(ServiceChangeLog.ToJson([now]))!));
        Assert.DoesNotContain("putBack", ServiceChangeLog.ToJson([now])); // a Turn off's mark: not written when false
        var putBack = now with { UnderWay = now.UnderWay! with { PutBack = true } };
        Assert.Contains("\"putBack\": true", ServiceChangeLog.ToJson([putBack]));
        Assert.Equal(putBack, Assert.Single(ServiceChangeLog.Parse(ServiceChangeLog.ToJson([putBack]))!));
        Assert.Null(ServiceChangeLog.Parse("""[{"service":"MapsBroker","originalStart":"Automatic","originalRunning":true,"newStart":"Disabled","underWay":{"pid":1}}]"""));
    }

    // ---------------------------------------------------------------- a record a Put back began on

    private static ServiceChange Marked(ServiceStartType original, bool putBack) => new()
    {
        Service = "MapsBroker", OriginalStart = original, OriginalRunning = true, NewStart = ServiceStartType.Disabled, ChangedUtc = T0, Via = "manual",
        UnderWay = new ChangeUnderWay { Pid = 4242, ProcessStarted = 1, TickMs = 5, PutBack = putBack },
    };

    /// <summary>
    /// A record whose mark says a Put back began on it, with the service as a Put back leaves it (at the original start
    /// type, and seen stopped when that is Disabled), is one the self-repair check never reads as switched back on outside
    /// PoolWatch. A Turn off's mark, no mark, or a service at another start type is not.
    /// </summary>
    [Theory]
    [InlineData(true, ServiceStartType.Automatic, ServiceStartType.Automatic, false, false, true)]
    [InlineData(true, ServiceStartType.Automatic, ServiceStartType.Automatic, true, false, true)]   // started again
    [InlineData(true, ServiceStartType.Manual, ServiceStartType.Manual, false, true, true)]         // starting
    [InlineData(false, ServiceStartType.Automatic, ServiceStartType.Automatic, true, false, false)] // a Turn off's mark
    [InlineData(true, ServiceStartType.Automatic, ServiceStartType.Disabled, false, false, false)]  // not put back
    [InlineData(true, ServiceStartType.Automatic, ServiceStartType.Manual, true, false, false)]     // at another start type
    [InlineData(true, ServiceStartType.Disabled, ServiceStartType.Disabled, false, false, true)]
    [InlineData(true, ServiceStartType.Disabled, ServiceStartType.Disabled, true, false, false)]    // Put back stops an original of Disabled
    [InlineData(true, ServiceStartType.Disabled, ServiceStartType.Disabled, false, true, false)]    // starting: not stopped
    public void ARecordAPutBackBeganOnIsReadAsThatPutBackLeftIt(bool putBack, ServiceStartType original, ServiceStartType live, bool running, bool between, bool left)
    {
        var record = Marked(original, putBack);
        var s = V("MapsBroker", live, running, between, state: between ? ServiceRunState.Starting : null);
        Assert.Equal(left, ServicePlan.LeftByItsPutBack(record, s));
        Assert.False(ServicePlan.LeftByItsPutBack(record with { UnderWay = null }, s));
        string state = ServicePlan.StateText(O(s) with { PoolWatchChange = record });
        Assert.Equal(left, state.Contains("(PoolWatch began to put back its change of ", StringComparison.Ordinal));
    }

    /// <summary>
    /// The State column of such a record says a Put back began and that Put back finishes it, never that the service was
    /// switched back on, or changed, outside PoolWatch.
    /// </summary>
    [Fact]
    public void TheStateOfARecordAPutBackBeganOnSaysSo()
    {
        string when = T0.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal($"Automatic, stopped (PoolWatch began to put back its change of {when}; Put back finishes it)",
            ServicePlan.StateText(O(V("MapsBroker", ServiceStartType.Automatic)) with { PoolWatchChange = Marked(ServiceStartType.Automatic, putBack: true) }));
        Assert.Equal($"running (turned off by PoolWatch {when}, then switched back on outside it)",
            ServicePlan.StateText(O(V("MapsBroker", ServiceStartType.Automatic, running: true)) with { PoolWatchChange = Marked(ServiceStartType.Automatic, putBack: false) }));
        var setDefault = Marked(ServiceStartType.Disabled, putBack: true) with { NewStart = ServiceStartType.Automatic, Via = ServicePlan.ViaWindowsDefault };
        Assert.Equal($"off (PoolWatch began to put back its change of {when}; Put back finishes it)",
            ServicePlan.StateText(O(V("MapsBroker", ServiceStartType.Disabled)) with { PoolWatchChange = setDefault }));
    }
}
