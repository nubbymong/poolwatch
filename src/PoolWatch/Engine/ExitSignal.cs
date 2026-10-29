using System.Security.AccessControl;

namespace PoolWatch.Engine;

/// <summary>
/// How the installer and the uninstaller ask a running PoolWatch to exit: a named event that the tray app and a headless
/// run listen on. Exiting through it is the normal clean exit (open incidents are closed and written, the token guard's
/// failsafe runs), never a kill. Only Administrators and SYSTEM may open the event, so a program running without
/// administrator rights cannot make PoolWatch exit. Listening costs one kernel event and one registered wait on the thread
/// pool's shared wait thread; nothing runs until the event is set.
/// </summary>
internal static class ExitSignal
{
    public const string Name = @"Global\PoolWatch-Exit";
    /// <summary>The one-instance mutex a running PoolWatch holds (Program.Main): free means no PoolWatch is running.</summary>
    public const string MonitorMutexName = @"Global\PoolWatch-Monitor";
    /// <summary>Administrators and SYSTEM, full access; nobody else, and nothing inherited.</summary>
    internal const string AdminOnly = "D:P(A;;GA;;;BA)(A;;GA;;;SY)";
    /// <summary>How often a waiting request asks again (a PoolWatch that was still starting may be listening by then).</summary>
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Creates the event and calls <paramref name="onExit"/> (on a pool thread) when it is set. Null when the event could
    /// not be created, or when the name already exists: PoolWatch did not create that one, so it is not trusted (the same
    /// exposure the one-instance mutex has). Dispose to stop listening.
    /// </summary>
    public static IDisposable? Listen(Action onExit, Action<string> log) => Listen(Name, AdminOnly, onExit, log);

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
        return new Listener(signal, wait);
    }

    /// <summary>Asks a running PoolWatch to exit and waits for it. False: still running after <paramref name="wait"/>.</summary>
    public static bool RequestAndWait(TimeSpan wait) => RequestAndWait(Name, MonitorMutexName, wait);

    internal static bool RequestAndWait(string eventName, string mutexName, TimeSpan wait)
    {
        using var held = StopAndHold(eventName, mutexName, wait);
        return held is not null;
    }

    /// <summary>
    /// As <see cref="RequestAndWait"/>, then holds the one-instance mutex until disposed, so no PoolWatch can start
    /// meanwhile (the uninstall cleanup). Null: still running after the wait. Dispose it on the calling thread: a mutex
    /// belongs to the thread that holds it.
    /// </summary>
    public static IDisposable? StopAndHold(TimeSpan wait) => StopAndHold(Name, MonitorMutexName, wait);

    internal static IDisposable? StopAndHold(string eventName, string mutexName, TimeSpan wait)
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
                    if (mutex.WaitOne(Poll)) return new Held(mutex);
                }
                catch (AbandonedMutexException) { return new Held(mutex); } // its holder ended without releasing it: held now
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

    /// <summary>Sets the event when a PoolWatch is listening (nothing happens when none is).</summary>
    private static void Signal(string name)
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(name, out var signal))
                using (signal) signal.Set();
        }
        catch (UnauthorizedAccessException) { } // not an administrator: it cannot be asked, and the wait then times out
    }

    private sealed class Listener(EventWaitHandle signal, RegisteredWaitHandle wait) : IDisposable
    {
        public void Dispose()
        {
            wait.Unregister(null);
            signal.Dispose();
        }
    }

    private sealed class Held(Mutex mutex) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}
