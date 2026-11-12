using System.Security.AccessControl;

namespace PoolWatch.Engine;

/// <summary>
/// How the installer and the uninstaller ask a running PoolWatch to exit: a named event that the tray app and a headless
/// run listen on. Exiting through it is the normal clean exit (open incidents are closed and written, the token guard's
/// failsafe runs), never a kill. The event and the one-instance mutex are in PoolWatch's private namespace
/// (<see cref="AdminNamespace"/>), which only an elevated administrator or SYSTEM can create or open, so a program
/// running without administrator rights can neither make PoolWatch exit nor create or open either object. Listening costs one
/// kernel event and one registered wait on the thread pool's shared wait thread; nothing runs until the event is set.
/// </summary>
internal static class ExitSignal
{
    /// <summary>Administrators and SYSTEM, full access; nobody else, and nothing inherited.</summary>
    internal const string AdminOnly = AdminNamespace.AdminOnly;
    /// <summary>How often a waiting request asks again (a PoolWatch that was still starting may be listening by then).</summary>
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The proof that a thread holds the one-instance mutex. Only the two steps that take that mutex make one, through
    /// its private constructor: <see cref="AsTheOneInstance"/> (the tray app and a headless run, which Program.Main
    /// runs only that way) and <see cref="StopAndHold(TimeSpan)"/> (the uninstall cleanup). It holds only on the thread
    /// that took the mutex, and only until it is disposed (<see cref="HeldHere"/>), as a mutex belongs to the thread
    /// that holds it. AppPaths takes it: only a start given one that holds where it runs may move data folders, and
    /// only the Log it opens then rolls the log over, through <see cref="WhileHeld"/> (a Log's gate can only be such a
    /// proof, which only the steps that take the mutex make: its constructors are private).
    /// </summary>
    internal sealed class Hold : IDisposable
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;
        /// <summary>
        /// The mutex released at <see cref="Dispose"/> (StopAndHold's), or null (AsTheOneInstance keeps its own).
        /// </summary>
        private readonly Mutex? _release;
        private readonly object _gate = new();
        private volatile bool _done;

        private Hold(Mutex? release) => _release = release;

        /// <summary>
        /// Runs <paramref name="act"/>, on any thread, only while this proof is not disposed, and keeps it from being
        /// disposed (so the mutex from being let go) until <paramref name="act"/> has returned: the log's roll-over, so
        /// a PoolWatch that takes the mutex next never rolls the log at the same time. False when it is disposed, and
        /// then <paramref name="act"/> does not run.
        /// </summary>
        internal bool WhileHeld(Action act)
        {
            lock (_gate)
            {
                // Read under the lock: read before it, a Dispose between the read and the lock would let a roll-over
                // start once the proof is let go.
                if (_done) return false;
                act();
                return true;
            }
        }

        /// <summary>
        /// Whether the calling thread holds the mutex through this proof now: it is the thread that took it, and the proof
        /// is not disposed.
        /// </summary>
        public bool HeldHere => !_done && Environment.CurrentManagedThreadId == _thread;

        /// <summary>
        /// Runs <paramref name="run"/> with a proof only when this thread takes the mutex <paramref name="mutexName"/> (it
        /// did not exist), holding it meanwhile; otherwise <paramref name="alreadyRunning"/> runs, and no proof is made.
        /// </summary>
        internal static int AsTheOneInstance(string mutexName, Func<int> alreadyRunning, Func<Hold, int> run)
        {
            using var mutex = new Mutex(true, mutexName, out bool first);
            if (!first) return alreadyRunning();
            using var hold = new Hold(release: null);
            return run(hold);
        }

        /// <summary>
        /// Asks a running PoolWatch to exit until this thread takes the mutex <paramref name="mutexName"/> (a proof, which
        /// releases it when disposed), or until <paramref name="wait"/> has passed (null).
        /// </summary>
        internal static Hold? StopAndHold(string eventName, string mutexName, TimeSpan wait)
        {
            var mutex = new Mutex(false, mutexName);
            long deadline = Environment.TickCount64 + (long)wait.TotalMilliseconds;
            try
            {
                do
                {
                    Signal(eventName);
                    try
                    {
                        if (mutex.WaitOne(Poll)) return new Hold(mutex);
                    }
                    catch (AbandonedMutexException) { return new Hold(mutex); } // its holder ended without releasing it: held now
                }
                while (Environment.TickCount64 < deadline);
            }
            catch
            {
                mutex.Dispose();
                throw;
            }
            mutex.Dispose();
            return null;
        }

        public void Dispose()
        {
            lock (_gate) // waits for a WhileHeld under way
            {
                if (_done) return;
                _done = true;
            }
            if (_release is null) return;
            _release.ReleaseMutex();
            _release.Dispose();
        }
    }

    /// <summary>
    /// Runs <paramref name="run"/> (the tray app or a headless run: its start checks the data folder, moves included) only
    /// when this thread takes the one-instance mutex <paramref name="mutexName"/>, holding it while <paramref name="run"/>
    /// runs, and hands it the proof (<see cref="Hold"/>). When another PoolWatch holds it, <paramref
    /// name="alreadyRunning"/> runs instead, and no data folder is looked at. Program.Main passes the mutex's name in
    /// PoolWatch's private namespace (<see cref="AdminNamespace.MonitorMutexName"/>); tests pass a mutex name of their own.
    /// </summary>
    internal static int AsTheOneInstance(string mutexName, Func<int> alreadyRunning, Func<Hold, int> run) =>
        Hold.AsTheOneInstance(mutexName, alreadyRunning, run);

    /// <summary>
    /// Whether a PoolWatch may hold the one-instance mutex now: the mutex exists (a running PoolWatch keeps it open, and so
    /// do PoolWatch's stop steps while they wait for it or hold it). One that cannot be opened for want of rights counts as
    /// existing, and so does any when PoolWatch's private namespace, which holds the mutex, cannot be opened: then it
    /// cannot be told.
    /// </summary>
    public static bool InstanceRunning()
    {
        return InstanceRunning(() => AdminNamespace.Shared.MonitorMutexName);
    }

    /// <summary>
    /// <see cref="InstanceRunning()"/> with the mutex's name from <paramref name="mutexName"/> (the private namespace's; a
    /// test passes one of its own): when that throws <see cref="AdminNamespaceException"/>, it cannot be told, so yes.
    /// </summary>
    internal static bool InstanceRunning(Func<string> mutexName)
    {
        string name;
        try { name = mutexName(); }
        catch (AdminNamespaceException) { return true; }
        return InstanceRunning(name);
    }

    internal static bool InstanceRunning(string mutexName)
    {
        try
        {
            if (!Mutex.TryOpenExisting(mutexName, out var mutex)) return false;
            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>
    /// Creates the event and calls <paramref name="onExit"/> (on a pool thread) when it is set. Null when the private
    /// namespace or the event could not be created, or when the event already exists: PoolWatch did not create that one,
    /// so it is not trusted (inside the namespace only an administrator could have). Each case is logged. Dispose to stop
    /// listening.
    /// </summary>
    public static IDisposable? Listen(Action onExit, Action<string> log)
    {
        string name;
        try { name = AdminNamespace.Shared.ExitEventName; }
        catch (AdminNamespaceException ex)
        {
            log("exit signal: not used (" + ex.Message + ")");
            return null;
        }
        return Listen(name, AdminOnly, onExit, log);
    }

    internal static IDisposable? Listen(string name, string sddl, Action onExit, Action<string> log)
    {
        var security = new EventWaitHandleSecurity();
        security.SetSecurityDescriptorSddlForm(sddl);
        EventWaitHandle signal;
        try
        {
            signal = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, name, out bool createdNew, security);
            if (!createdNew)
            {
                signal.Dispose();
                log($"exit signal: not used, {name} already exists and PoolWatch did not create it");
                return null;
            }
        }
        catch (Exception ex)
        {
            log($"exit signal: not used ({ex.Message})");
            return null;
        }
        var wait = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) =>
        {
            log("exit signal: exit requested");
            // A pool thread: an app that is already exiting on its own (its window gone) must not be taken down by this.
            try { onExit(); }
            catch (Exception ex) { log("exit signal: " + ex.Message); }
        }, null, Timeout.Infinite, executeOnlyOnce: true);
        return new Listener(signal, wait, name);
    }

    /// <summary>Asks a running PoolWatch to exit and waits for it. False: still running after <paramref name="wait"/>.</summary>
    internal static bool RequestAndWait(string eventName, string mutexName, TimeSpan wait)
    {
        using var held = StopAndHold(eventName, mutexName, wait);
        return held is not null;
    }

    /// <summary>
    /// Asks a running PoolWatch to exit, waits for it, then holds the one-instance mutex until disposed, so no PoolWatch
    /// can start meanwhile (the setup's stop and the uninstall cleanup): the proof that this thread holds it (<see
    /// cref="Hold"/>). Null: still running after the wait. Throws <see cref="AdminNamespaceException"/> when PoolWatch's
    /// private namespace cannot be opened: then nothing was asked and nothing is known. Dispose it on the calling thread: a
    /// mutex belongs to the thread that holds it.
    /// </summary>
    public static Hold? StopAndHold(TimeSpan wait)
    {
        var names = AdminNamespace.Shared;
        return StopAndHold(names.ExitEventName, names.MonitorMutexName, wait);
    }

    internal static Hold? StopAndHold(string eventName, string mutexName, TimeSpan wait) => Hold.StopAndHold(eventName, mutexName, wait);

    /// <summary>Sets the event when a PoolWatch is listening (nothing happens when none is).</summary>
    private static void Signal(string name)
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(name, out var signal))
                using (signal) signal.Set();
        }
        catch (UnauthorizedAccessException) { } // the event is in PoolWatch's admin-only namespace, which the caller already opened to get this name; a rare refusal to open or set it here is skipped, and the wait then times out
    }

    private sealed class Listener(EventWaitHandle signal, RegisteredWaitHandle wait, string name) : IDisposable
    {
        public void Dispose() => StopListening(wait, signal, name);
    }

    /// <summary>
    /// Stops listening on <paramref name="signal"/> (named <paramref name="name"/>) and closes it, so the event is gone once
    /// this returns (the show signal's listener stops so too): a request or a start just after then finds no event, as when
    /// no PoolWatch runs. The thread pool's wait thread lets go of the event only when it next looks, after its
    /// unregistration has returned and even after the handle given to it was set, so the event stayed open a moment longer
    /// and a request could still set it with no PoolWatch listening (3 times in 4,000 in a test, and once in 4,000 when only
    /// that handle was waited for). So this waits for a callback running then (it only posts to the UI thread), closes the
    /// event, then waits until its name opens no event: each wait at most 1 s, so nothing stuck holds up the exit.
    /// </summary>
    internal static void StopListening(RegisteredWaitHandle wait, EventWaitHandle signal, string name)
    {
        using (var gone = new ManualResetEvent(false))
            if (wait.Unregister(gone)) gone.WaitOne(TimeSpan.FromSeconds(1));
        signal.Dispose();
        long until = Environment.TickCount64 + 1000;
        while (Opens(name) && Environment.TickCount64 < until) Thread.Sleep(1);
    }

    /// <summary>Whether <paramref name="name"/> still opens an event (one this account may not open counts as none: nothing of PoolWatch's can be told then).</summary>
    private static bool Opens(string name)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(name, out var left)) return false;
            left.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException) { return false; }
    }
}
