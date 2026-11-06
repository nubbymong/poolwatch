using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using PoolWatch.Engine;

namespace PoolWatch.AppTests;

/// <summary>
/// The exit signal, always under names of the test's own in the session's local namespace: a test never signals, waits
/// for or creates the real one (in <see cref="AdminNamespace"/>), which a PoolWatch running on the same PC listens on.
/// </summary>
public sealed class ExitSignalTests
{
    private readonly string _event = @"Local\PoolWatch-Test-Exit-" + Guid.NewGuid().ToString("N");
    private readonly string _mutex = @"Local\PoolWatch-Test-Monitor-" + Guid.NewGuid().ToString("N");

    /// <summary>The admin-only descriptor plus the account running the tests, which may not be an administrator.</summary>
    private static string AdminOnlyAndMe => ExitSignal.AdminOnly + $"(A;;GA;;;{WindowsIdentity.GetCurrent().User!.Value})";

    /// <summary>
    /// A stand-in for a running PoolWatch: holds the mutex on its own thread and listens; exits when asked. With
    /// <paramref name="release"/> false it ends without releasing the mutex, as Program.Main does, which leaves it abandoned.
    /// </summary>
    private Thread RunningPoolWatch(List<string> log, ManualResetEventSlim listening, bool release = true)
    {
        var thread = new Thread(() =>
        {
            using var mutex = new Mutex(true, _mutex);
            using var exit = new ManualResetEventSlim();
            using var listener = ExitSignal.Listen(_event, AdminOnlyAndMe, exit.Set, s => { lock (log) log.Add(s); });
            listening.Set();
            if (listener is not null) exit.Wait(TimeSpan.FromSeconds(30));
            if (release) mutex.ReleaseMutex();
        }) { IsBackground = true };
        thread.Start();
        Assert.True(listening.Wait(TimeSpan.FromSeconds(10)));
        return thread;
    }

    [Fact]
    public void ARunningPoolWatchExitsWhenAsked()
    {
        var log = new List<string>();
        using var listening = new ManualResetEventSlim();
        var running = RunningPoolWatch(log, listening);

        Assert.True(ExitSignal.RequestAndWait(_event, _mutex, TimeSpan.FromSeconds(20)));
        Assert.True(running.Join(TimeSpan.FromSeconds(10)));
        lock (log) Assert.Contains("exit signal: exit requested", log);
        Assert.True(FreeForOthers()); // the request let go of the mutex again
    }

    [Fact]
    public void TheCleanupHoldsTheMutexUntilItIsDone()
    {
        var log = new List<string>();
        using var listening = new ManualResetEventSlim();
        var running = RunningPoolWatch(log, listening);

        using (var held = ExitSignal.StopAndHold(_event, _mutex, TimeSpan.FromSeconds(20)))
        {
            Assert.NotNull(held);
            Assert.True(running.Join(TimeSpan.FromSeconds(10)));
            lock (log) Assert.Contains("exit signal: exit requested", log);
            Assert.False(FreeForOthers()); // nothing can start while the cleanup holds it
        }
        Assert.True(FreeForOthers());
    }

    [Fact]
    public void TheCleanupHoldsTheMutexThatTheExitingPoolWatchLeftAbandoned()
    {
        var log = new List<string>();
        using var listening = new ManualResetEventSlim();
        var running = RunningPoolWatch(log, listening, release: false);

        using (var held = ExitSignal.StopAndHold(_event, _mutex, TimeSpan.FromSeconds(20)))
        {
            Assert.NotNull(held);
            Assert.True(running.Join(TimeSpan.FromSeconds(10)));
            Assert.False(FreeForOthers());
        }
        Assert.True(FreeForOthers());
    }

    /// <summary>Whether another thread can take the mutex now (it gives it back at once).</summary>
    private bool FreeForOthers()
    {
        bool free = false;
        var thread = new Thread(() =>
        {
            using var m = new Mutex(false, _mutex);
            free = m.WaitOne(0);
            if (free) m.ReleaseMutex();
        });
        thread.Start();
        thread.Join();
        return free;
    }

    [Fact]
    public void WithNothingRunningTheAnswerIsImmediate()
    {
        var clock = Stopwatch.StartNew();
        using var held = ExitSignal.StopAndHold(_event, _mutex, TimeSpan.FromSeconds(20));
        Assert.NotNull(held);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void APoolWatchThatDoesNotListenIsStillRunningAfterTheWait()
    {
        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            using var mutex = new Mutex(true, _mutex);
            holding.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            mutex.ReleaseMutex();
        }) { IsBackground = true };
        thread.Start();
        Assert.True(holding.Wait(TimeSpan.FromSeconds(10)));

        // StopAndHold times its wait on Environment.TickCount64, which moves in steps of the system timer (typically 10 to
        // 16 ms). Read as the wait starts, the tick count can be up to a step behind, so on a Stopwatch the wait can end up
        // to a step short of 800 ms (three 250 ms polls that each run a little long reach the deadline just before 800 ms).
        // So the wait is measured on the same tick count: read before the call, it is never later than the wait's own
        // start, and the 800 ms the code waits always show in full.
        long start = Environment.TickCount64;
        var held = ExitSignal.StopAndHold(_event, _mutex, TimeSpan.FromMilliseconds(800));
        long waited = Environment.TickCount64 - start;
        release.Set();

        Assert.Null(held);
        Assert.True(waited >= 800, $"waited {waited} ms");
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void ANameThatAlreadyExistsIsNotUsed()
    {
        using var squatter = new EventWaitHandle(false, EventResetMode.AutoReset, _event);
        var log = new List<string>();

        using var listener = ExitSignal.Listen(_event, AdminOnlyAndMe, () => { }, log.Add);

        Assert.Null(listener);
        Assert.Contains(log, l => l.Contains("already exists"));
    }

    [Fact]
    public void OnlyAdministratorsAndSystemMayOpenTheRealSignal()
    {
        using var listener = ExitSignal.Listen(_event, ExitSignal.AdminOnly, () => { }, _ => { });
        Assert.NotNull(listener);

        // The test account owns the event it created, so it may read its permissions whatever its own rights.
        using var opened = EventWaitHandleAcl.OpenExisting(_event, EventWaitHandleRights.ReadPermissions);
        var sd = new RawSecurityDescriptor(opened.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));

        Assert.True(sd.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected)); // nothing inherited
        var aces = sd.DiscretionaryAcl!.Cast<CommonAce>().ToList();
        Assert.Equal(2, aces.Count);
        Assert.All(aces, a => Assert.Equal(AceQualifier.AccessAllowed, a.AceQualifier));
        Assert.Equal(
            new[] { new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) }.Order(),
            aces.Select(a => a.SecurityIdentifier).Order());
    }
}
