using System.Runtime.InteropServices;
using System.Security.AccessControl;

namespace PoolWatch.Engine;

/// <summary>
/// How a second start of the tray app brings the running one's window forward instead of saying "already running": the
/// running tray app listens on a named event, one for each Windows session, and a start that finds the one-instance mutex
/// held sets the event of its own session (<see cref="Send()"/>), after letting the running one take the foreground. Windows
/// may start PoolWatch's Start menu entry when a notice is clicked in the notification centre; that start is such a second
/// start. The event is in PoolWatch's private namespace (<see cref="AdminNamespace.ShowEventName"/>, <c>PoolWatch\Show-1</c>
/// for session 1) with an admin-only DACL, as the exit signal is (<see cref="ExitSignal"/>): only an elevated administrator or SYSTEM can
/// create, open or set it, and an event that already exists, which this tray app did not create, is not used. The private
/// namespace is one for the whole PC, so the session's number keeps a start in one session from showing a window in
/// another: there, no tray app listens, and the start says "already running" as before. Listening costs one kernel event
/// and one registered wait on the thread pool's shared wait thread; nothing runs until the event is set.
/// </summary>
internal static class ShowSignal
{
    /// <summary>
    /// The tray app: creates its session's event and calls <paramref name="onShow"/> (on a pool thread) each time it is set.
    /// Null when the private namespace, the session or the event could not be had, or when the event already exists (this
    /// app did not create it): each case is logged, and a second start then says "already running" as before. Dispose to
    /// stop listening.
    /// </summary>
    public static IDisposable? Listen(Action onShow, Action<string> log)
    {
        string name;
        try { name = AdminNamespace.Shared.ShowEventName(TokenLeakGuard.OwnSession()); }
        catch (Exception ex)
        {
            log("show signal: not used (" + ex.Message + ")");
            return null;
        }
        return Listen(name, AdminNamespace.AdminOnly, onShow, log);
    }

    internal static IDisposable? Listen(string name, string sddl, Action onShow, Action<string> log)
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
                log($"show signal: not used, {name} already exists and PoolWatch did not create it");
                return null;
            }
        }
        catch (Exception ex)
        {
            log($"show signal: not used ({ex.Message})");
            return null;
        }
        var wait = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) =>
        {
            log("show signal: PoolWatch was started again, so its window is shown");
            try { onShow(); }
            catch (Exception ex) { log("show signal: " + ex.Message); }
        }, null, Timeout.Infinite, executeOnlyOnce: false);
        return new Listener(signal, wait, name);
    }

    /// <summary>
    /// A second start of the tray app: lets the running one take the foreground (this start has the right, being the one
    /// the user just started; it is passed on for the next set only), then sets its session's event. False when no tray
    /// app of this session listens (none runs here, a headless run holds the mutex, or the names could not be had): the
    /// caller then says "already running".
    /// </summary>
    public static bool Send()
    {
        string name;
        try { name = AdminNamespace.Shared.ShowEventName(TokenLeakGuard.OwnSession()); }
        catch (Exception) { return false; }
        AllowSetForegroundWindow(AsfwAny);
        return Send(name);
    }

    /// <summary>Sets the event <paramref name="name"/> when a tray app listens on it; false when none does.</summary>
    internal static bool Send(string name)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(name, out var signal)) return false;
            using (signal) return signal.Set();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException) { return false; }
    }

    /// <summary>ASFW_ANY: any process may take the foreground once, until the next input (the running PoolWatch's own process ID is not known here).</summary>
    private const uint AsfwAny = 0xFFFF_FFFF;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    /// <summary>Stops listening: once Dispose returns the event is gone, so a start finds none (<see cref="ExitSignal.StopListening"/>).</summary>
    private sealed class Listener(EventWaitHandle signal, RegisteredWaitHandle wait, string name) : IDisposable
    {
        public void Dispose() => ExitSignal.StopListening(wait, signal, name);
    }
}
