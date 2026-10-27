using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PoolWatch.Core.Model;

namespace PoolWatch.Engine;

/// <summary>One open process handle with PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION.</summary>
internal interface IProcessHandle : IDisposable
{
    /// <summary>The process's create time (FILETIME, UTC) read from THIS handle (GetProcessTimes); null when it cannot be read.</summary>
    long? CreateTime();
    /// <summary>Ends the process through THIS handle; null on success, else the error.</summary>
    string? Terminate();
}

/// <summary>Opens a process by PID (the one place a PID is turned into a process).</summary>
internal interface IProcessOpener
{
    /// <summary>The process with that PID, opened with terminate + limited query rights; null (with the error) when it cannot be.</summary>
    IProcessHandle? Open(int pid, out string? error);
}

/// <summary>
/// S6 (review round 2): "End process" for a heavy background app. The PID in the list may be stale and a PID is reused, so:
/// the process is opened FIRST (PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION); its create time is read from that
/// handle (GetProcessTimes) and must equal the listed one exactly; the owner is asked; the create time is read again from
/// the same handle; and the process is ended through that SAME handle. An open handle keeps the process object, and so its
/// PID, from being reused, so what is ended is what was checked. Without a known create time (&lt;= 0) nothing is ended.
/// Process.StartTime (cached) and Process.Kill (which opens the PID again) are not used.
/// </summary>
internal static class ProcessEnder
{
    public enum Outcome { Ended, Refused, Gone, Cancelled, Failed }

    public sealed record Result(Outcome Outcome, string Message);

    /// <param name="confirm">Asks the owner (the dialog); runs while the handle is held. False: nothing is ended.</param>
    public static Result End(IProcessOpener opener, ProcessKey key, string identity, Func<bool> confirm)
    {
        if (key.CreateTime <= 0)
            return new(Outcome.Refused, $"PoolWatch does not know when {identity} (PID {key.Pid}) started, so it cannot be sure the PID still " +
                                        "belongs to it: it does not end it.");
        using var h = opener.Open(key.Pid, out string? error);
        if (h is null) return new(Outcome.Gone, $"{identity} (PID {key.Pid}) is no longer running, or cannot be opened{(error is null ? "" : $" ({error})")}.");
        if (h.CreateTime() is not { } before) return new(Outcome.Failed, $"the start time of PID {key.Pid} could not be read: not ending it.");
        if (before != key.CreateTime) return new(Outcome.Refused, $"PID {key.Pid} is now a different process than the one listed: not ending it.");
        if (!confirm()) return new(Outcome.Cancelled, "");
        // Still the same handle, so still the same process; checked again anyway before the one irreversible step.
        if (h.CreateTime() != key.CreateTime) return new(Outcome.Refused, $"PID {key.Pid} changed while you were asked: not ending it.");
        return h.Terminate() is { } failed
            ? new(Outcome.Failed, $"{identity} (PID {key.Pid}) could not be ended: {failed}")
            : new(Outcome.Ended, $"Ended {identity} (PID {key.Pid}).");
    }
}

/// <summary>The real opener: OpenProcess / GetProcessTimes / TerminateProcess on one handle.</summary>
internal sealed class WindowsProcessOpener : IProcessOpener
{
    private const uint PROCESS_TERMINATE = 0x0001, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    public IProcessHandle? Open(int pid, out string? error)
    {
        error = null;
        var h = OpenProcess(PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (!h.IsInvalid) return new Handle(h);
        int e = Marshal.GetLastWin32Error();
        h.Dispose();
        error = e == 87 ? null : $"error {e}"; // ERROR_INVALID_PARAMETER: no such process
        return null;
    }

    private sealed class Handle(SafeProcessHandle h) : IProcessHandle
    {
        public long? CreateTime() => GetProcessTimes(h, out long created, out _, out _, out _) ? created : null;

        public string? Terminate() => TerminateProcess(h, 1) ? null : $"error {Marshal.GetLastWin32Error()}";

        public void Dispose() => h.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle h, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(SafeProcessHandle h, uint exitCode);
}
