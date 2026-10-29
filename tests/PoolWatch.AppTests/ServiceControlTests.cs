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
    [InlineData(1051, false, "Done", "a service that depends on it is running", NotDoneCause.DependentsRunning)]
    [InlineData(1061, false, "LookAgain", null, NotDoneCause.Other)] // it has just begun to pause, say: look once more
    [InlineData(1061, true, "Done", "it cannot take a stop now (Windows error 1061)", NotDoneCause.Other)]
    [InlineData(1052, false, "Done", "it does not take a stop (Windows error 1052)", NotDoneCause.Other)]
    [InlineData(5, false, "Done", "Windows did not stop it (error 5)", NotDoneCause.Other)]
    [InlineData(1051, true, "Done", "a service that depends on it is running", NotDoneCause.DependentsRunning)]
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
    /// The clock moves only when the change waits. Asked to stop more than ten times, it throws: a change that never
    /// gives up fails the test rather than hanging it.
    /// </summary>
    private sealed class FakeService(params uint[] states) : IServiceCalls
    {
        public readonly List<string> Calls = new();
        public int StartTypeError;
        public int StopError;
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
            int error = StopAnswers.Count > 0 ? StopAnswers.Dequeue() : StopError;
            if (error == 0) (_states, _read) = (AfterStop, 0);
            return error;
        }

        public long Now { get; private set; }
        public void Sleep(int ms) => Now += ms;

        public int Stops => Calls.Count(c => c == "stop");
    }

    private static uint[] Times(uint state, int n) => Enumerable.Repeat(state, n).ToArray();

    /// <summary>The start type is set first, then the stop is asked for, so a trigger cannot start the service again in between.</summary>
    [Fact]
    public void TheStartTypeIsSetBeforeTheStop()
    {
        var svc = new FakeService(SERVICE_RUNNING) { AfterStop = [SERVICE_STOP_PENDING, SERVICE_STOP_PENDING, SERVICE_STOPPED] };
        Assert.Equal((null, null), Change(svc, "Svc", ServiceStartType.Disabled, stop: true));
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
        Assert.Equal(("could not change 'Svc': 5", (NotDone?)null), Change(svc, "Svc", ServiceStartType.Disabled, stop: true));
        Assert.Equal(["start type Disabled"], svc.Calls);
    }

    /// <summary>The start type is set but Windows refuses the stop: not stopped, and why; never done.</summary>
    [Fact]
    public void WhenTheStopIsRefusedTheStartTypeStaysAndTheResultSaysWhy()
    {
        var svc = new FakeService(SERVICE_RUNNING) { StopError = 1051 };
        Assert.Equal((null, new NotDone("a service that depends on it is running", NotDoneCause.DependentsRunning)),
            Change(svc, "Svc", ServiceStartType.Disabled, stop: true));
        Assert.Equal(["start type Disabled", "state", "stop"], svc.Calls);
    }

    [Fact]
    public void ASetToManualOrADefaultIsNotStopped()
    {
        var svc = new FakeService(SERVICE_RUNNING);
        Assert.Equal((null, null), Change(svc, "Svc", ServiceStartType.Manual, stop: false));
        Assert.Equal(["start type Manual"], svc.Calls);
    }

    [Fact]
    public void AStoppedServiceIsDoneWithNoStop()
    {
        var svc = new FakeService(SERVICE_STOPPED);
        Assert.Null(StopAndWait(svc));
        Assert.Equal(0, svc.Stops);
    }

    /// <summary>It stopped between the look and the stop (Windows error 1062): stopped.</summary>
    [Fact]
    public void AServiceThatStoppedMeanwhileIsDone()
    {
        var svc = new FakeService(SERVICE_RUNNING) { StopError = 1062 };
        Assert.Null(StopAndWait(svc));
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
        Assert.Null(StopAndWait(svc));
        Assert.Equal(["state", "state", "state", "state", "stop", "state", "state"], svc.Calls);
    }

    [Fact]
    public void APausedServiceIsStopped()
    {
        var svc = new FakeService(SERVICE_PAUSED);
        Assert.Null(StopAndWait(svc));
        Assert.Equal(1, svc.Stops);
    }

    /// <summary>Something else is stopping it already: waited for, with no stop of PoolWatch's own.</summary>
    [Fact]
    public void AServiceAlreadyStoppingIsWaitedFor()
    {
        var svc = new FakeService(SERVICE_STOP_PENDING, SERVICE_STOP_PENDING, SERVICE_STOPPED);
        Assert.Null(StopAndWait(svc));
        Assert.Equal(0, svc.Stops);
    }

    /// <summary>Still starting after the wait: not stopped, and no stop was asked for (it could not take one).</summary>
    [Fact]
    public void AServiceThatDoesNotSettleIsNotStopped()
    {
        var svc = new FakeService(SERVICE_START_PENDING);
        Assert.Equal(new NotDone("it was still starting after 20 seconds", NotDoneCause.NotSettled), StopAndWait(svc));
        Assert.Equal(0, svc.Stops);
        Assert.InRange(svc.Now, StopWaitSeconds * 1000L, StopWaitSeconds * 1000L + StopLookMs);
    }

    /// <summary>A service whose state Windows cannot give is never done: no stop is asked for, and the result says so.</summary>
    [Fact]
    public void AServiceWhoseStateCannotBeReadIsNotKnownToHaveStopped()
    {
        var svc = new FakeService(0u);
        Assert.Equal(new NotDone("Windows could not say whether it is running", NotDoneCause.StateUnknown), StopAndWait(svc));
        Assert.Equal(0, svc.Stops);

        var asked = new FakeService(SERVICE_RUNNING) { AfterStop = [SERVICE_STOP_PENDING, 0u] };
        Assert.Equal(NotDoneCause.StateUnknown, StopAndWait(asked)?.Cause);
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
        Assert.Equal(new NotDone(why, cause), StopAndWait(svc));
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
        Assert.Null(StopAndWait(svc));
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
        Assert.Equal(new NotDone("it cannot take a stop now (Windows error 1061)"), StopAndWait(svc));
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
        Assert.Equal(new NotDone("it was still pausing after 20 seconds", NotDoneCause.NotSettled), StopAndWait(svc));
        Assert.Equal(1, svc.Stops);
        Assert.InRange(svc.Now, StopWaitSeconds * 1000L, StopWaitSeconds * 1000L + StopLookMs);
    }

    /// <summary>
    /// Windows refuses the stop of a service that is paused (any answer that ends the stop): not stopped, and still
    /// paused, never running (<see cref="NotDone.Paused"/>). Refused while it runs, it is still running.
    /// </summary>
    [Theory]
    [InlineData(SERVICE_PAUSED, 1051, "a service that depends on it is running", NotDoneCause.DependentsRunning, true)]
    [InlineData(SERVICE_PAUSED, 1052, "it does not take a stop (Windows error 1052)", NotDoneCause.Other, true)]
    [InlineData(SERVICE_PAUSED, 1061, "it cannot take a stop now (Windows error 1061)", NotDoneCause.Other, true)] // twice
    [InlineData(SERVICE_PAUSED, 5, "Windows did not stop it (error 5)", NotDoneCause.Other, true)]
    [InlineData(SERVICE_RUNNING, 1052, "it does not take a stop (Windows error 1052)", NotDoneCause.Other, false)]
    [InlineData(SERVICE_RUNNING, 1061, "it cannot take a stop now (Windows error 1061)", NotDoneCause.Other, false)]
    public void AStopRefusedWhileTheServiceIsPausedLeavesItPausedNotRunning(uint state, int error, string why, NotDoneCause cause, bool paused)
    {
        var svc = new FakeService(state) { StopError = error };
        Assert.Equal(new NotDone(why, cause) { Paused = paused }, StopAndWait(svc));
    }

    /// <summary>What the service was when Windows refused the stop is what the last look found, after a 1061 too.</summary>
    [Theory]
    [InlineData(SERVICE_RUNNING, SERVICE_PAUSED, true)]  // it paused meanwhile
    [InlineData(SERVICE_PAUSED, SERVICE_RUNNING, false)] // it was resumed meanwhile
    public void AStopRefusedAfterALookAgainSaysWhatTheServiceWasThen(uint first, uint then, bool paused)
    {
        var svc = new FakeService(first, then) { StopAnswers = new([1061]), StopError = 1052 };
        Assert.Equal(new NotDone("it does not take a stop (Windows error 1052)") { Paused = paused }, StopAndWait(svc));
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
        Assert.Null(StopAndWait(svc));
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
}
