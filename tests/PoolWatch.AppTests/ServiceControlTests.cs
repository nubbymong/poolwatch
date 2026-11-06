using System.Runtime.InteropServices;
using PoolWatch.Core.Services;
using PoolWatch.Engine;
using static PoolWatch.Engine.ServiceControl;

namespace PoolWatch.AppTests;

/// <summary>
/// The gated change's own decisions, with no service involved: the dependency list read from a service's configuration
/// (on a buffer the test builds), what Windows' answers to a stop and a start mean, and the order and the wait of a change,
/// through a fake service whose clock only moves when the change waits. No real service is changed here.
/// </summary>
public sealed class ServiceControlTests
{
    // ---------------------------------------------------------------- the dependency list

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint type, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint protect, out uint old);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint type);

    private const int Page = 4096;

    /// <summary>
    /// The first <paramref name="endAt"/> characters of <paramref name="chars"/> as Windows lays the list out (UTF-16, '\0'
    /// after each name), at the very end of a buffer that a page no process may read follows: a read past the buffer's
    /// end is an access violation, so a test that passes read no further.
    /// </summary>
    private static List<string> Read(string chars, int endAt)
    {
        var two = VirtualAlloc(IntPtr.Zero, 2 * Page, 0x3000 /* MEM_COMMIT | MEM_RESERVE */, 0x04 /* PAGE_READWRITE */);
        Assert.NotEqual(IntPtr.Zero, two);
        try
        {
            var end = two + Page;
            var list = end - endAt * sizeof(char);
            for (int i = 0; i < endAt; i++) Marshal.WriteInt16(list, i * sizeof(char), (short)chars[i]);
            Assert.True(VirtualProtect(end, Page, 0x01 /* PAGE_NOACCESS */, out _));
            return Dependencies(list, end);
        }
        finally { VirtualFree(two, UIntPtr.Zero, 0x8000 /* MEM_RELEASE */); }
    }

    [Fact]
    public void TheDependencyListGivesEachNameUpToTheEmptyOne()
    {
        const string List = "NSI\0Afd\0\0Bowser\0\0";
        Assert.Equal(["NSI", "Afd"], Read(List, List.Length)); // the empty name ends the list: Bowser is past it
    }

    [Fact]
    public void ALoadOrderGroupIsNotAService()
    {
        const string List = "+NetworkProvider\0NSI\0+TDI\0\0";
        Assert.Equal(["NSI"], Read(List, List.Length));
    }

    /// <summary>A list the buffer's end cuts off is read up to that end and no further; a name with no end inside it is left out.</summary>
    [Fact]
    public void AListCutOffAtTheBufferEndIsReadNoFurther()
    {
        const string List = "NSI\0Afd\0\0";
        Assert.Equal(["NSI"], Read(List, "NSI\0Afd".Length)); // "Afd" has no end inside the buffer
        Assert.Equal(["NSI"], Read(List, "NSI\0Af".Length));
        Assert.Equal(["NSI"], Read(List, "NSI\0".Length)); // the buffer ends where the next name would start
        Assert.Equal(["NSI", "Afd"], Read(List, "NSI\0Afd\0".Length)); // no empty name: the buffer's end ends the list
    }

    /// <summary>
    /// The list of dependents Windows writes (EnumDependentServicesW, SERVICE_ACTIVE): each entry's name, read only from
    /// inside the buffer, and its state. The buffer sits at the very end of a page that a page no process may read
    /// follows, so a read past its end fails the test. An entry whose name lies outside the buffer, or has no end inside
    /// it, ends the list there; a count larger than the room is read no further than the room.
    /// </summary>
    [Fact]
    public void TheDependentsListGivesEachNameAndStateFromInsideTheBuffer()
    {
        var two = VirtualAlloc(IntPtr.Zero, 2 * Page, 0x3000 /* MEM_COMMIT | MEM_RESERVE */, 0x04 /* PAGE_READWRITE */);
        Assert.NotEqual(IntPtr.Zero, two);
        try
        {
            int entry = Marshal.SizeOf<ENUM_SERVICE_STATUS>();
            string[] names = ["AcmeMaps", "XblGameSave"];
            int size = 3 * entry + names.Sum(n => n.Length + 1) * sizeof(char);
            var end = two + Page;
            var buf = end - size;
            var at = buf + 3 * entry;
            var name = new List<IntPtr>();
            foreach (var n in names)
            {
                name.Add(at);
                for (int i = 0; i < n.Length; i++) Marshal.WriteInt16(at, i * sizeof(char), (short)n[i]);
                Marshal.WriteInt16(at, n.Length * sizeof(char), 0);
                at += (n.Length + 1) * sizeof(char);
            }
            void Entry(int i, IntPtr serviceName, uint state) =>
                Marshal.StructureToPtr(new ENUM_SERVICE_STATUS { ServiceName = serviceName, DisplayName = serviceName, CurrentState = state }, buf + i * entry, false);
            Entry(0, name[0], SERVICE_PAUSED);
            Entry(1, name[1], SERVICE_RUNNING);
            Marshal.WriteInt16(two, 'Z'); // a readable name ("Z") before the buffer: never read
            Marshal.WriteInt16(two, sizeof(char), 0);
            Entry(2, two, SERVICE_RUNNING);
            Assert.True(VirtualProtect(end, Page, 0x01 /* PAGE_NOACCESS */, out _));

            DependentService acme = new("AcmeMaps", ServiceRunState.Paused), xbl = new("XblGameSave", ServiceRunState.Running);
            Assert.Equal([acme, xbl], ParseDependents(buf, size, 3));
            Assert.Equal([acme], ParseDependents(buf, size, 1));
            Assert.Equal([acme, xbl], ParseDependents(buf, size, 99));
            Marshal.WriteInt16(end - sizeof(char), 'X'); // the last name has no end inside the buffer
            Assert.Equal([acme], ParseDependents(buf, size, 2));
            Assert.Empty(ParseDependents(buf, entry - 1, 1)); // not even one whole entry fits
        }
        finally { VirtualFree(two, UIntPtr.Zero, 0x8000 /* MEM_RELEASE */); }
    }

    [Fact]
    public void AnEmptyListOrNoneGivesNoService()
    {
        Assert.Empty(Read("\0\0", 2));
        Assert.Empty(Read("NSI\0\0", 0)); // a buffer that ends where the list starts
        Assert.Empty(Dependencies(IntPtr.Zero, IntPtr.Zero + 64));
    }

    // ---------------------------------------------------------------- what Windows' answers mean

    [Theory]
    [InlineData(0, false, "Wait", null, NotDoneCause.Other)]
    [InlineData(1062, false, "Done", null, NotDoneCause.Other)]
    [InlineData(1051, false, "Done", "a service that depends on it has not stopped (Windows error 1051)", NotDoneCause.DependentsRunning)]
    [InlineData(1061, false, "LookAgain", null, NotDoneCause.Other)] // it has just begun to pause, say: look once more
    [InlineData(1061, true, "Done", "it cannot take a stop now (Windows error 1061)", NotDoneCause.Other)]
    [InlineData(1052, false, "Done", "it does not take a stop (Windows error 1052)", NotDoneCause.TakesNoStop)]
    [InlineData(5, false, "Done", "Windows did not stop it (error 5)", NotDoneCause.Other)]
    [InlineData(1051, true, "Done", "a service that depends on it has not stopped (Windows error 1051)", NotDoneCause.DependentsRunning)]
    public void WindowsAnswerToAStop(int error, bool lookedAgain, string next, string? why, NotDoneCause cause)
    {
        var step = Answered(error, lookedAgain);
        Assert.Equal(Enum.Parse<StopNext>(next), step.Next);
        Assert.Equal(why is null ? null : new NotDone(why, cause), step.Why);
    }

    /// <summary>Every state a look can find, before and after Windows took the stop, with the wait over and not.</summary>
    [Theory]
    [InlineData(SERVICE_STOPPED, false, false, "Done", null, NotDoneCause.Other)]
    [InlineData(SERVICE_STOPPED, true, true, "Done", null, NotDoneCause.Other)]
    [InlineData(SERVICE_RUNNING, false, false, "AskStop", null, NotDoneCause.Other)]
    [InlineData(SERVICE_RUNNING, false, true, "AskStop", null, NotDoneCause.Other)] // it has just settled: its stop gets its own wait
    [InlineData(SERVICE_PAUSED, false, false, "AskStop", null, NotDoneCause.Other)]
    [InlineData(SERVICE_RUNNING, true, false, "Wait", null, NotDoneCause.Other)]
    [InlineData(SERVICE_STOP_PENDING, true, false, "Wait", null, NotDoneCause.Other)]
    [InlineData(SERVICE_PAUSED, true, false, "Wait", null, NotDoneCause.Other)]
    [InlineData(SERVICE_RUNNING, true, true, "Done", "it did not stop within 20 seconds", NotDoneCause.Other)] // it runs: "still running" is true
    // Not running when the wait ends, so never "still running": the state it is in, and not stopped yet.
    [InlineData(SERVICE_STOP_PENDING, true, true, "Done", "it was still stopping 20 seconds after Windows took the stop", NotDoneCause.NotSettled)]
    [InlineData(SERVICE_PAUSED, true, true, "Done", "it was still paused 20 seconds after Windows took the stop", NotDoneCause.NotSettled)]
    [InlineData(SERVICE_PAUSE_PENDING, true, true, "Done", "it was still pausing 20 seconds after Windows took the stop", NotDoneCause.NotSettled)]
    [InlineData(SERVICE_START_PENDING, true, true, "Done", "it was still starting 20 seconds after Windows took the stop", NotDoneCause.NotSettled)]
    [InlineData(SERVICE_CONTINUE_PENDING, true, true, "Done", "it was still resuming 20 seconds after Windows took the stop", NotDoneCause.NotSettled)]
    [InlineData(SERVICE_START_PENDING, false, false, "Wait", null, NotDoneCause.Other)]
    [InlineData(SERVICE_CONTINUE_PENDING, false, false, "Wait", null, NotDoneCause.Other)]
    [InlineData(SERVICE_PAUSE_PENDING, false, false, "Wait", null, NotDoneCause.Other)]
    [InlineData(SERVICE_STOP_PENDING, false, false, "Wait", null, NotDoneCause.Other)]
    [InlineData(SERVICE_START_PENDING, false, true, "Done", "it was still starting after 20 seconds", NotDoneCause.NotSettled)]
    [InlineData(SERVICE_CONTINUE_PENDING, false, true, "Done", "it was still resuming after 20 seconds", NotDoneCause.NotSettled)]
    [InlineData(SERVICE_PAUSE_PENDING, false, true, "Done", "it was still pausing after 20 seconds", NotDoneCause.NotSettled)]
    [InlineData(SERVICE_STOP_PENDING, false, true, "Done", "it was still stopping after 20 seconds", NotDoneCause.NotSettled)]
    [InlineData(0u, false, false, "Done", "Windows could not say whether it is running", NotDoneCause.StateUnknown)]
    [InlineData(0u, true, false, "Done", "Windows could not say whether it is running", NotDoneCause.StateUnknown)]
    [InlineData(8u, false, false, "Done", "Windows could not say whether it is running", NotDoneCause.StateUnknown)]
    public void OneLookAtAServiceBeingStopped(uint state, bool asked, bool timeUp, string next, string? why, NotDoneCause cause)
    {
        var step = Looked(state, asked, timeUp);
        Assert.Equal(Enum.Parse<StopNext>(next), step.Next);
        Assert.Equal(why is null ? null : new NotDone(why, cause), step.Why);
    }

    /// <summary>What the read of the services makes of each state: only RUNNING runs, and only STOPPED is stopped.</summary>
    [Theory]
    [InlineData(SERVICE_STOPPED, false, false)]
    [InlineData(SERVICE_RUNNING, true, false)]
    [InlineData(SERVICE_START_PENDING, false, true)]
    [InlineData(SERVICE_STOP_PENDING, false, true)]
    [InlineData(SERVICE_CONTINUE_PENDING, false, true)]
    [InlineData(SERVICE_PAUSE_PENDING, false, true)]
    [InlineData(SERVICE_PAUSED, false, true)]
    [InlineData(0u, false, true)] // Windows could not say
    [InlineData(8u, false, true)] // a state Windows does not define
    public void TheReadOfAServiceIsStoppedOnlyWhenWindowsSaysStopped(uint state, bool running, bool between) =>
        Assert.Equal((running, between), RunState(state));

    /// <summary>
    /// Which reads say Windows could not give the state (the AI agent's review row then says when the stop is asked for):
    /// one it could not read or does not define, never paused, starting, stopping, resuming or pausing.
    /// </summary>
    [Theory]
    [InlineData(SERVICE_STOPPED, false)]
    [InlineData(SERVICE_RUNNING, false)]
    [InlineData(SERVICE_START_PENDING, false)]
    [InlineData(SERVICE_STOP_PENDING, false)]
    [InlineData(SERVICE_CONTINUE_PENDING, false)]
    [InlineData(SERVICE_PAUSE_PENDING, false)]
    [InlineData(SERVICE_PAUSED, false)]
    [InlineData(0u, true)] // Windows could not say
    [InlineData(8u, true)] // a state Windows does not define
    public void TheReadOfAServiceKnowsWhenWindowsCouldNotGiveItsState(uint state, bool unknown) =>
        Assert.Equal(unknown, StateUnknown(state));

    [Theory]
    [InlineData(1056, null, NotDoneCause.Other)] // running already
    [InlineData(1068, "a service it needs did not start (Windows error 1068)", NotDoneCause.NeededServiceNotStarted)]
    [InlineData(1058, "error 1058", NotDoneCause.Other)]
    public void WindowsAnswerToAStart(int error, string? why, NotDoneCause cause) =>
        Assert.Equal(why is null ? null : new NotDone(why, cause), Started(error));

    // ---------------------------------------------------------------- the change: its order and its wait

    /// <summary>
    /// A service as the change sees it: the states it reads one after another (the last repeats), those after a stop
    /// Windows took, and what Windows answers (<see cref="StopAnswers"/> first, one each, then <see cref="StopError"/>).
    /// The clock moves only when the change waits, and while Windows holds a stop request (<see cref="StopHeldMs"/>). Asked
    /// to stop more than ten times, it throws: a change that never gives up fails the test rather than hanging it.
    /// </summary>
    private sealed class FakeService(params uint[] states) : IServiceCalls
    {
        public readonly List<string> Calls = new();
        public int StartTypeError;
        public int StopError;
        /// <summary>How long Windows holds each stop request before it answers (another service busy in its handler, say).</summary>
        public int StopHeldMs;
        public Queue<int> StopAnswers = new();
        public uint[] AfterStop = [SERVICE_STOPPED];
        private uint[] _states = states;
        private int _read;

        public int SetStartType(ServiceStartType start) { Calls.Add("start type " + start); return StartTypeError; }

        public uint State()
        {
            Calls.Add("state");
            return _states[Math.Min(_read++, _states.Length - 1)];
        }

        public int Stop()
        {
            Calls.Add("stop");
            if (Stops > 10) throw new InvalidOperationException("asked to stop again and again");
            Now += StopHeldMs;
            int error = StopAnswers.Count > 0 ? StopAnswers.Dequeue() : StopError;
            if (error == 0) (_states, _read) = (AfterStop, 0);
            return error;
        }

        /// <summary>What Windows lists as the services that depend on it and have not stopped (null: that list cannot be read).</summary>
        public IReadOnlyList<DependentService>? Dependents;
        public int DependentsAsked;

        public IReadOnlyList<DependentService>? ActiveDependents() { DependentsAsked++; return Dependents; }

        public long Now { get; private set; }
        public void Sleep(int ms) => Now += ms;

        public int Stops => Calls.Count(c => c == "stop");
    }

    /// <summary>Why the stop of <paramref name="svc"/> is not known to have stopped it (null: it stopped).</summary>
    private static NotDone? Why(FakeService svc) => StopAndWait(svc).why;

    private static uint[] Times(uint state, int n) => Enumerable.Repeat(state, n).ToArray();

    /// <summary>The start type is set first, then the stop is asked for, so a trigger cannot start the service again in between.</summary>
    [Fact]
    public void TheStartTypeIsSetBeforeTheStop()
    {
        var svc = new FakeService(SERVICE_RUNNING) { AfterStop = [SERVICE_STOP_PENDING, SERVICE_STOP_PENDING, SERVICE_STOPPED] };
        Assert.Equal((null, null, true), Change(svc, "Svc", ServiceStartType.Disabled, stop: true));
        Assert.Equal(["start type Disabled", "state", "stop", "state", "state", "state"], svc.Calls);
    }

    /// <summary>
    /// The start type cannot be set: nothing changed, so no stop is asked for, whatever Windows would have answered to
    /// one (the stop would have been taken, or refused).
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1051)]
    public void WhenTheStartTypeCannotBeSetNothingIsStopped(int stopWouldAnswer)
    {
        var svc = new FakeService(SERVICE_RUNNING) { StartTypeError = 5, StopError = stopWouldAnswer };
        Assert.Equal(("could not change 'Svc': 5", (NotDone?)null, (bool?)null), Change(svc, "Svc", ServiceStartType.Disabled, stop: true));
        Assert.Equal(["start type Disabled"], svc.Calls);
    }

    /// <summary>
    /// The start type is set but Windows refuses the stop: not stopped, and why; never done. It was running (a stop was
    /// asked for). Windows' own list of the services that kept it from stopping is asked for only after error 1051.
    /// </summary>
    [Fact]
    public void WhenTheStopIsRefusedTheStartTypeStaysAndTheResultSaysWhy()
    {
        var svc = new FakeService(SERVICE_RUNNING) { StopError = 1051 };
        Assert.Equal((null, new NotDone("a service that depends on it has not stopped (Windows error 1051)", NotDoneCause.DependentsRunning), true),
            Change(svc, "Svc", ServiceStartType.Disabled, stop: true));
        Assert.Equal(["start type Disabled", "state", "stop"], svc.Calls);
        Assert.Equal(1, svc.DependentsAsked);

        var other = new FakeService(SERVICE_RUNNING) { StopError = 1052, Dependents = [new("A", ServiceRunState.Running)] };
        Assert.Null(Why(other)!.Dependents);
        Assert.Equal(0, other.DependentsAsked);
    }

    /// <summary>
    /// Set to Manual (or a default), it is not stopped; its state is read once, so the record says whether it was running
    /// as the change found it.
    /// </summary>
    [Theory]
    [InlineData(SERVICE_RUNNING, true)]
    [InlineData(SERVICE_PAUSED, true)]
    [InlineData(SERVICE_START_PENDING, true)]
    [InlineData(SERVICE_STOPPED, false)]
    [InlineData(SERVICE_STOP_PENDING, false)]
    [InlineData(0u, null)]
    public void ASetToManualOrADefaultIsNotStopped(uint state, bool? wasRunning)
    {
        var svc = new FakeService(state);
        Assert.Equal((null, null, wasRunning), Change(svc, "Svc", ServiceStartType.Manual, stop: false));
        Assert.Equal(["start type Manual", "state"], svc.Calls);
    }

    /// <summary>
    /// What a Turn off records as "was running" comes from what its stop saw, not from the earlier read of the list: a
    /// stop asked for (it runs or is paused when PoolWatch looks) means it was running, also when that stop is refused, or
    /// its state cannot be read after it; a service seen stopped, or stopping on its own, was not; one whose state Windows
    /// could not give before any stop is not known.
    /// </summary>
    [Theory]
    [InlineData(new[] { SERVICE_RUNNING }, 0, true)]
    [InlineData(new[] { SERVICE_PAUSED }, 0, true)]
    [InlineData(new[] { SERVICE_START_PENDING, SERVICE_RUNNING }, 0, true)] // caught starting, stopped once it ran
    [InlineData(new[] { SERVICE_START_PENDING }, 0, true)]                  // still starting when the wait ended
    [InlineData(new[] { SERVICE_RUNNING }, 1051, true)]                     // refused: it runs on
    [InlineData(new[] { SERVICE_RUNNING }, 1062, true)]                     // it stopped just as the stop came
    [InlineData(new[] { SERVICE_STOPPED }, 0, false)]
    [InlineData(new[] { SERVICE_STOP_PENDING, SERVICE_STOPPED }, 0, false)] // stopping on its own
    [InlineData(new[] { SERVICE_START_PENDING, SERVICE_STOPPED }, 0, false)] // its start failed
    [InlineData(new[] { 0u }, 0, null)]                                       // Windows could not say
    [InlineData(new[] { SERVICE_START_PENDING, 0u }, 0, null)]
    public void WhatTheStopSawSaysWhetherTheServiceWasRunning(uint[] states, int stopAnswer, bool? wasRunning)
    {
        var svc = new FakeService(states) { StopError = stopAnswer };
        Assert.Equal(wasRunning, StopAndWait(svc).wasRunning);
    }

    /// <summary>A stop asked for and taken means it was running, also when its state cannot be read afterwards.</summary>
    [Fact]
    public void AStopTakenMeansItWasRunningEvenWhenItsStateIsThenNotKnown()
    {
        var svc = new FakeService(SERVICE_RUNNING) { AfterStop = [0u] };
        var (why, wasRunning) = StopAndWait(svc);
        Assert.Equal(NotDoneCause.StateUnknown, why?.Cause);
        Assert.True(wasRunning);
    }

    /// <summary>
    /// Error 1051: the services Windows lists right then as depending on it and not stopped come with the result (running
    /// or not: paused, starting...), for the result to name each one with its state.
    /// </summary>
    [Fact]
    public void ARefusedStopCarriesTheDependentsWindowsListed()
    {
        IReadOnlyList<DependentService> listed = [new("AcmeMaps", ServiceRunState.Paused), new("XblGameSave", ServiceRunState.Running)];
        var svc = new FakeService(SERVICE_PAUSED) { StopError = 1051, Dependents = listed };
        var why = Why(svc)!;
        Assert.Equal((NotDoneCause.DependentsRunning, true), (why.Cause, why.Paused));
        Assert.Same(listed, why.Dependents);
    }

    /// <summary>
    /// ServicesPipeTimeout (milliseconds, a DWORD) sets how long Windows holds a control on a busy service: 30 seconds
    /// unless it is set longer, rounded up to whole seconds, read as unsigned, and at most a day.
    /// </summary>
    [Theory]
    [InlineData(null, 30)]
    [InlineData(10_000, 30)]
    [InlineData(30_000, 30)]
    [InlineData(60_000, 60)]
    [InlineData(180_001, 181)]
    [InlineData(-1, 86_400)] // 0xFFFFFFFF
    [InlineData("60000", 30)] // not a DWORD
    public void TheControlWaitFollowsServicesPipeTimeout(object? value, int seconds) =>
        Assert.Equal(seconds, ControlWaitSecondsOf(value));

    /// <summary>
    /// The read of a service names the state it is in when it is neither running nor stopped, so the page can say
    /// "paused", never "stopped"; none for running, stopped, or a state Windows could not give.
    /// </summary>
    [Theory]
    [InlineData(SERVICE_PAUSED, ServiceRunState.Paused)]
    [InlineData(SERVICE_START_PENDING, ServiceRunState.Starting)]
    [InlineData(SERVICE_STOP_PENDING, ServiceRunState.Stopping)]
    [InlineData(SERVICE_CONTINUE_PENDING, ServiceRunState.Resuming)]
    [InlineData(SERVICE_PAUSE_PENDING, ServiceRunState.Pausing)]
    [InlineData(SERVICE_RUNNING, null)]
    [InlineData(SERVICE_STOPPED, null)]
    [InlineData(0u, null)]
    [InlineData(8u, null)]
    public void TheReadNamesAStateBetweenRunningAndStopped(uint state, ServiceRunState? between) => Assert.Equal(between, BetweenOf(state));

    /// <summary>
    /// A service listed but that PoolWatch cannot open is one whose state and start type are not known: the page says
    /// "could not read" (never "stopped"), the Cost column too, and no change acts on it.
    /// </summary>
    [Fact]
    public void AServiceThatCannotBeOpenedIsCouldNotRead()
    {
        var unread = Unread("MapsBroker", "Downloaded Maps Manager");
        Assert.Equal((ServiceStartType.Unknown, false, true, true), (unread.StartType, unread.Running, unread.NeitherRunningNorStopped, unread.StateUnknown));
        var o = new ServiceOption { Service = unread, Group = ServiceGroup.Optional };
        Assert.Equal(ServicePlan.CouldNotRead, ServicePlan.StateText(o));
        Assert.Equal(ServicePlan.CouldNotRead, ServicePlan.CostText(unread));
        Assert.Equal((ServicePlan.CouldNotRead, (Core.Impact.ImpactLevel?)null), Ui.ServiceRows.CostOf(unread));
        var change = new ServiceChange
        {
            Service = "MapsBroker", OriginalStart = ServiceStartType.Automatic, OriginalRunning = true, NewStart = ServiceStartType.Disabled,
            ChangedUtc = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc), Via = "manual",
        };
        string withChange = ServicePlan.StateText(o with { PoolWatchChange = change });
        Assert.StartsWith("could not read (turned off by PoolWatch ", withChange);
        Assert.DoesNotContain("switched back on", withChange);
        Assert.False(ServicePlan.IsOff(o));
        Assert.Throws<InvalidOperationException>(() => ServicePlan.BuildApply(["MapsBroker"], [o], false, "manual", DateTime.UtcNow));
    }

    /// <summary>
    /// The real read of one service, when Windows does not open it: OpenServiceW is given no manager handle, so it fails
    /// (ERROR_INVALID_HANDLE) before it looks up any service, and nothing is read or changed. The read gives what
    /// <see cref="Unread"/> gives, a service PoolWatch could not read, never a plain view that would read as stopped.
    /// </summary>
    [Fact]
    public void TheRealReadOfAServiceWindowsDoesNotOpenIsCouldNotRead()
    {
        var read = ServiceControl.Read(IntPtr.Zero, "PoolWatchTestNoSuchService", "No such service");
        Assert.Equal(("PoolWatchTestNoSuchService", "No such service", ServiceStartType.Unknown, false, true, true, (ServiceRunState?)null, 0),
            (read.Name, read.DisplayName, read.StartType, read.Running, read.NeitherRunningNorStopped, read.StateUnknown, read.Between, read.ProcessId));
        Assert.Equal(ServicePlan.CouldNotRead, ServicePlan.StateText(new ServiceOption { Service = read, Group = ServiceGroup.Optional }));
    }

    // ---------------------------------------------------------------- the list of services, at the Win32 boundary

    /// <summary>
    /// What <see cref="Enumerate(IServiceList)"/> asks Windows, faked: the service control manager opens (or fails with
    /// <see cref="OpenError"/>), lists <see cref="Names"/> (or the listing throws), and reads each (one in
    /// <see cref="ReadFails"/> throws). Every call is recorded.
    /// </summary>
    private sealed class FakeList : IServiceList
    {
        public int OpenError;
        public bool ListFails;
        public string[] Names = [];
        public readonly HashSet<string> ReadFails = [];
        public readonly List<string> Calls = [];
        private static readonly IntPtr Handle = new(0x1234);

        public IntPtr Open(out int error)
        {
            Calls.Add("open");
            error = OpenError;
            return OpenError == 0 ? Handle : IntPtr.Zero;
        }

        public IReadOnlyList<(string Name, string DisplayName)> List(IntPtr scm)
        {
            Calls.Add("list");
            Assert.Equal(Handle, scm);
            if (ListFails) throw new System.ComponentModel.Win32Exception(1115); // ERROR_SHUTDOWN_IN_PROGRESS, say
            return Names.Select(n => (n, n + " display")).ToList();
        }

        public ServiceView Read(IntPtr scm, string name, string display)
        {
            Calls.Add("read " + name);
            if (ReadFails.Contains(name)) throw new InvalidOperationException("the read of " + name + " failed");
            return new ServiceView { Name = name, DisplayName = display, StartType = ServiceStartType.Manual };
        }

        public void Close(IntPtr scm)
        {
            Assert.Equal(Handle, scm);
            Calls.Add("close");
        }
    }

    /// <summary>
    /// The service control manager cannot be opened: the read throws with Windows' error, and lists nothing (never an
    /// empty list, which would read as "nothing installed").
    /// </summary>
    [Fact]
    public void TheListThrowsWhenTheServiceControlManagerCannotBeOpened()
    {
        var windows = new FakeList { OpenError = 5, Names = ["MapsBroker"] };
        var e = Assert.Throws<InvalidOperationException>(() => Enumerate(windows));
        Assert.Equal("the service control manager could not be opened (error 5)", e.Message);
        Assert.Equal(["open"], windows.Calls);
    }

    /// <summary>The listing fails: the read throws (never an empty list), and the manager's handle is closed.</summary>
    [Fact]
    public void TheListThrowsWhenWindowsCannotListTheServices()
    {
        var windows = new FakeList { ListFails = true, Names = ["MapsBroker"] };
        Assert.Throws<System.ComponentModel.Win32Exception>(() => Enumerate(windows));
        Assert.Equal(["open", "list", "close"], windows.Calls);
    }

    /// <summary>
    /// A listed service whose read throws is in the list as one PoolWatch could not read (never left out, which would read
    /// as "not installed"); the others are read as they are, in Windows' order, and the handle is closed.
    /// </summary>
    [Fact]
    public void AServiceWhoseReadThrowsIsListedAsCouldNotRead()
    {
        var windows = new FakeList { Names = ["AppXSvc", "MapsBroker", "WSearch"], ReadFails = { "MapsBroker" } };
        var views = Enumerate(windows);
        Assert.Equal(["AppXSvc", "MapsBroker", "WSearch"], views.Select(v => v.Name));
        var unread = views[1];
        Assert.Equal(("MapsBroker display", ServiceStartType.Unknown, true, true, false),
            (unread.DisplayName, unread.StartType, unread.StateUnknown, unread.NeitherRunningNorStopped, unread.Running));
        Assert.Equal([ServiceStartType.Manual, ServiceStartType.Unknown, ServiceStartType.Manual], views.Select(v => v.StartType));
        Assert.Equal(["open", "list", "read AppXSvc", "read MapsBroker", "read WSearch", "close"], windows.Calls);
    }

    /// <summary>
    /// A change opens the service with exactly the rights it uses (change the start type, stop it, read its state): Windows
    /// opens a service only when every right asked for is granted, so one more (the list of its dependents) would refuse
    /// the whole change of a service whose access rules do not give it. That list is read on a handle of its own.
    /// </summary>
    [Fact]
    public void AChangeAsksOnlyForTheRightsItUses()
    {
        Assert.Equal(0x0002u /* SERVICE_CHANGE_CONFIG */ | 0x0020u /* SERVICE_STOP */ | 0x0004u /* SERVICE_QUERY_STATUS */, ChangeAccess);
        Assert.Equal(0x0008u /* SERVICE_ENUMERATE_DEPENDENTS */, DependentsAccess);
    }

    [Fact]
    public void AStoppedServiceIsDoneWithNoStop()
    {
        var svc = new FakeService(SERVICE_STOPPED);
        Assert.Null(Why(svc));
        Assert.Equal(0, svc.Stops);
    }

    /// <summary>It stopped between the look and the stop (Windows error 1062): stopped.</summary>
    [Fact]
    public void AServiceThatStoppedMeanwhileIsDone()
    {
        var svc = new FakeService(SERVICE_RUNNING) { StopError = 1062 };
        Assert.Null(Why(svc));
        Assert.Equal(1, svc.Stops);
    }

    /// <summary>Caught starting, resuming or pausing, it is waited for to settle, then stopped: done only once stopped.</summary>
    [Theory]
    [InlineData(SERVICE_START_PENDING, SERVICE_RUNNING)]
    [InlineData(SERVICE_CONTINUE_PENDING, SERVICE_RUNNING)]
    [InlineData(SERVICE_PAUSE_PENDING, SERVICE_PAUSED)]
    public void AServiceCaughtSettlingIsStoppedOnceItHasSettled(uint settling, uint settled)
    {
        var svc = new FakeService(settling, settling, settling, settled) { AfterStop = [SERVICE_STOP_PENDING, SERVICE_STOPPED] };
        Assert.Null(Why(svc));
        Assert.Equal(["state", "state", "state", "state", "stop", "state", "state"], svc.Calls);
    }

    [Fact]
    public void APausedServiceIsStopped()
    {
        var svc = new FakeService(SERVICE_PAUSED);
        Assert.Null(Why(svc));
        Assert.Equal(1, svc.Stops);
    }

    /// <summary>Something else is stopping it already: waited for, with no stop of PoolWatch's own.</summary>
    [Fact]
    public void AServiceAlreadyStoppingIsWaitedFor()
    {
        var svc = new FakeService(SERVICE_STOP_PENDING, SERVICE_STOP_PENDING, SERVICE_STOPPED);
        Assert.Null(Why(svc));
        Assert.Equal(0, svc.Stops);
    }

    /// <summary>Still starting after the wait: not stopped, and no stop was asked for (it could not take one).</summary>
    [Fact]
    public void AServiceThatDoesNotSettleIsNotStopped()
    {
        var svc = new FakeService(SERVICE_START_PENDING);
        Assert.Equal(new NotDone("it was still starting after 20 seconds", NotDoneCause.NotSettled), Why(svc));
        Assert.Equal(0, svc.Stops);
        Assert.InRange(svc.Now, StopWaitSeconds * 1000L, StopWaitSeconds * 1000L + StopLookMs);
    }

    /// <summary>A service whose state Windows cannot give is never done: no stop is asked for, and the result says so.</summary>
    [Fact]
    public void AServiceWhoseStateCannotBeReadIsNotKnownToHaveStopped()
    {
        var svc = new FakeService(0u);
        Assert.Equal(new NotDone("Windows could not say whether it is running", NotDoneCause.StateUnknown), Why(svc));
        Assert.Equal(0, svc.Stops);

        var asked = new FakeService(SERVICE_RUNNING) { AfterStop = [SERVICE_STOP_PENDING, 0u] };
        Assert.Equal(NotDoneCause.StateUnknown, Why(asked)?.Cause);
    }

    /// <summary>
    /// Windows took the stop, but the service does not stop: after the wait, not stopped, and why: still running only when
    /// it runs, else still stopping.
    /// </summary>
    [Theory]
    [InlineData(SERVICE_STOP_PENDING, "it was still stopping 20 seconds after Windows took the stop", NotDoneCause.NotSettled)]
    [InlineData(SERVICE_RUNNING, "it did not stop within 20 seconds", NotDoneCause.Other)]
    public void AStopThatDoesNotEndInTimeIsNotStopped(uint after, string why, NotDoneCause cause)
    {
        var svc = new FakeService(SERVICE_RUNNING) { AfterStop = [after] };
        Assert.Equal(new NotDone(why, cause), Why(svc));
        Assert.Equal(1, svc.Stops);
        Assert.InRange(svc.Now, StopWaitSeconds * 1000L, StopWaitSeconds * 1000L + StopLookMs);
    }

    /// <summary>
    /// Found running, it has begun to pause when the stop arrives (Windows error 1061, it cannot take one now): it is looked
    /// at again, waited for to settle, and then stopped.
    /// </summary>
    [Fact]
    public void AStopWindowsCannotTakeJustThenIsAskedForAgainOnceTheServiceHasSettled()
    {
        var svc = new FakeService(SERVICE_RUNNING, SERVICE_PAUSE_PENDING, SERVICE_PAUSE_PENDING, SERVICE_PAUSED)
        {
            StopAnswers = new([1061]), AfterStop = [SERVICE_STOP_PENDING, SERVICE_STOPPED],
        };
        Assert.Null(Why(svc));
        Assert.Equal(["state", "stop", "state", "state", "state", "stop", "state", "state"], svc.Calls);
    }

    /// <summary>
    /// Windows cannot take the stop the second time either: not stopped, and why; it is asked for twice, no more, and the
    /// second time only after one short wait.
    /// </summary>
    [Fact]
    public void AStopWindowsCannotTakeTwiceIsNotStopped()
    {
        var svc = new FakeService(SERVICE_RUNNING) { StopError = 1061 };
        Assert.Equal(new NotDone("it cannot take a stop now (Windows error 1061)"), Why(svc));
        Assert.Equal(2, svc.Stops);
        Assert.Equal(StopLookMs, svc.Now); // the look again waits once before the second stop
    }

    /// <summary>
    /// A first 1061 just before the wait to settle ends gets its look again within that wait: no new wait of 20 seconds
    /// opens. Found pausing then, it is not stopped yet.
    /// </summary>
    [Fact]
    public void ALookAgainStaysWithinTheWaitToSettle()
    {
        int looks = StopWaitSeconds * 1000 / StopLookMs - 1; // the stop is asked for one look before the wait ends
        var svc = new FakeService([.. Times(SERVICE_START_PENDING, looks), SERVICE_RUNNING, SERVICE_PAUSE_PENDING]) { StopAnswers = new([1061]) };
        Assert.Equal(new NotDone("it was still pausing after 20 seconds", NotDoneCause.NotSettled), Why(svc));
        Assert.Equal(1, svc.Stops);
        Assert.InRange(svc.Now, StopWaitSeconds * 1000L, StopWaitSeconds * 1000L + StopLookMs);
    }

    /// <summary>
    /// The longest stop fits in the longest change (<see cref="ServicesController.ChangeAtMost"/>). It asks for the stop
    /// at most <see cref="StopRequestsAtMost"/> times (Windows answers 1061 to each). The longest: starting until the wait to
    /// settle ends, then each stop request held the 30 seconds Windows documents for a busy service (the first answered
    /// 1061), and still stopping when the wait for the stop ends.
    /// </summary>
    [Fact]
    public void TheLongestStopFitsInTheLongestTurnOff()
    {
        var refused = new FakeService(SERVICE_RUNNING) { StopError = 1061 };
        Assert.NotNull(Why(refused));
        Assert.Equal(StopRequestsAtMost, refused.Stops);

        int looks = StopWaitSeconds * 1000 / StopLookMs; // starting at every look until the wait to settle has ended
        var svc = new FakeService([.. Times(SERVICE_START_PENDING, looks), SERVICE_RUNNING])
        {
            StopAnswers = new([1061]), AfterStop = [SERVICE_STOP_PENDING], StopHeldMs = 30_000,
        };
        Assert.Equal(new NotDone("it was still stopping 20 seconds after Windows took the stop", NotDoneCause.NotSettled), Why(svc));
        Assert.Equal(2, svc.Stops);
        Assert.InRange(svc.Now, 100_000L, (long)ServicesController.ChangeAtMost(DefaultControlWaitSeconds).TotalMilliseconds);
    }

    /// <summary>
    /// Windows refuses the stop of a service that is paused (any answer that ends the stop): not stopped, and still
    /// paused, never running (<see cref="NotDone.Paused"/>). Refused while it runs, it is still running.
    /// </summary>
    [Theory]
    [InlineData(SERVICE_PAUSED, 1051, "a service that depends on it has not stopped (Windows error 1051)", NotDoneCause.DependentsRunning, true)]
    [InlineData(SERVICE_PAUSED, 1052, "it does not take a stop (Windows error 1052)", NotDoneCause.TakesNoStop, true)]
    [InlineData(SERVICE_PAUSED, 1061, "it cannot take a stop now (Windows error 1061)", NotDoneCause.Other, true)] // twice
    [InlineData(SERVICE_PAUSED, 5, "Windows did not stop it (error 5)", NotDoneCause.Other, true)]
    [InlineData(SERVICE_RUNNING, 1052, "it does not take a stop (Windows error 1052)", NotDoneCause.TakesNoStop, false)]
    [InlineData(SERVICE_RUNNING, 1061, "it cannot take a stop now (Windows error 1061)", NotDoneCause.Other, false)]
    public void AStopRefusedWhileTheServiceIsPausedLeavesItPausedNotRunning(uint state, int error, string why, NotDoneCause cause, bool paused)
    {
        var svc = new FakeService(state) { StopError = error };
        Assert.Equal(new NotDone(why, cause) { Paused = paused }, Why(svc));
    }

    /// <summary>What the service was when Windows refused the stop is what the last look found, after a 1061 too.</summary>
    [Theory]
    [InlineData(SERVICE_RUNNING, SERVICE_PAUSED, true)]  // it paused meanwhile
    [InlineData(SERVICE_PAUSED, SERVICE_RUNNING, false)] // it was resumed meanwhile
    public void AStopRefusedAfterALookAgainSaysWhatTheServiceWasThen(uint first, uint then, bool paused)
    {
        var svc = new FakeService(first, then) { StopAnswers = new([1061]), StopError = 1052 };
        Assert.Equal(new NotDone("it does not take a stop (Windows error 1052)", NotDoneCause.TakesNoStop) { Paused = paused }, Why(svc));
        Assert.Equal(2, svc.Stops);
    }

    /// <summary>
    /// A service that settles only as the wait to settle ends still gets the whole wait for its stop: Windows took the
    /// stop, and it ends within that.
    /// </summary>
    [Fact]
    public void AStopTakenAfterTheServiceSettledHasAWaitOfItsOwn()
    {
        int looks = StopWaitSeconds * 1000 / StopLookMs; // the looks the wait to settle allows
        var svc = new FakeService([.. Times(SERVICE_START_PENDING, looks), SERVICE_RUNNING])
        {
            AfterStop = [.. Times(SERVICE_STOP_PENDING, looks / 2), SERVICE_STOPPED],
        };
        Assert.Null(Why(svc));
        Assert.Equal(1, svc.Stops);
    }
}

/// <summary>
/// The real read of the services' dependencies (a read-only service control manager query): a service every Windows
/// has lists the ones it needs, and no listed name is empty or a load-order group. Runs apart from the parallel tests, as
/// the other real read does.
/// </summary>
[Collection(nameof(GcStressCollection))]
public sealed class ServiceDependencyReadTests
{
    [Fact]
    public void TheRealReadGivesTheServicesEachOneNeeds()
    {
        var views = ServiceControl.Enumerate();
        var rpc = Assert.Single(views, v => v.Name.Equals("RpcSs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("RpcEptMapper", rpc.DependsOn, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("DcomLaunch", rpc.DependsOn, StringComparer.OrdinalIgnoreCase);
        Assert.All(views, v => Assert.All(v.DependsOn, d => Assert.False(d.Length == 0 || d.StartsWith('+') || d.Contains('\0'), $"{v.Name}: '{d}'")));
    }

    /// <summary>The real read of Windows' wait on a busy service (a read-only registry read): never under the default.</summary>
    [Fact]
    public void TheRealReadOfTheControlWaitIsAtLeastTheDefault() =>
        Assert.InRange(ServiceControl.ControlWaitSeconds(), ServiceControl.DefaultControlWaitSeconds, ServiceControl.MaxControlWaitSeconds);

    /// <summary>
    /// The real check that a PoolWatch which marked a change still runs, on this test's own process (read-only queries):
    /// its number and start time are this process's; it runs; the same number with another start time is not it; a
    /// number no process has does not run.
    /// </summary>
    [Fact]
    public void TheRealCheckThatAProcessStillRunsKnowsThisOne()
    {
        var host = new WindowsServicesHost();
        var (pid, started) = host.Self;
        using var me = System.Diagnostics.Process.GetCurrentProcess();
        Assert.Equal(me.Id, pid);
        Assert.InRange(started, me.StartTime.ToUniversalTime().ToFileTimeUtc() - TimeSpan.TicksPerSecond, me.StartTime.ToUniversalTime().ToFileTimeUtc() + TimeSpan.TicksPerSecond);
        Assert.True(host.Runs(pid, started));
        Assert.False(host.Runs(pid, started - 1));
        Assert.False(host.Runs(-1, started)); // not a process number Windows gives
        Assert.InRange(host.NowMs, Environment.TickCount64 - 60_000, Environment.TickCount64);
    }
}
