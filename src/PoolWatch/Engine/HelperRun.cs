using System.Diagnostics;
using PoolWatch.Native;

namespace PoolWatch.Engine;

/// <summary>
/// One run of the token-leak test's hidden helper as the guard holds it: the process it started (cmd.exe, which waits for
/// the helper). A test passes a fake: no test starts a process.
/// </summary>
internal interface IHelperRun : IDisposable
{
    /// <summary>True once the started process has ended (within <paramref name="milliseconds"/>).</summary>
    bool WaitForExit(int milliseconds);

    int ExitCode { get; }

    /// <summary>
    /// True when the run is in a job that ends it with PoolWatch (<see cref="JobHelperRun"/>). Only such a run is ended
    /// when its check is stopped or PoolWatch exits.
    /// </summary>
    bool InJob { get; }

    /// <summary>Ends the helper and every process it started.</summary>
    void End();
}

/// <summary>
/// The logon check's helper, as it always ran: a plain process, ended only on the guard's timeout. It may outlive PoolWatch
/// on purpose: orphaned, it offers to protect the logon (<c>--token-test-parent</c>). A check-only run's helper runs this
/// way too when Windows refuses its job, as it did before there was one: ended only on the timeout, so it may outlive
/// PoolWatch.
/// </summary>
internal sealed class PlainHelperRun(Process process) : IHelperRun
{
    public bool WaitForExit(int milliseconds) => process.WaitForExit(milliseconds);

    public int ExitCode => process.ExitCode;

    public bool InJob => false;

    public void End()
    {
        try { process.Kill(entireProcessTree: true); } catch { } // our own helper and its children
    }

    public void Dispose() => process.Dispose();
}

/// <summary>
/// A check-only run's helper (the setup guide's check, the Simple view's Check, the token-leak watch), in a job of its own
/// that ends it with PoolWatch (<see cref="JobNative"/>): when the guard ends it, and when PoolWatch exits in any way, also
/// when it is killed. Its result is thrown away when PoolWatch stops, so nothing is lost, and PoolWatch.exe is not left
/// in use after PoolWatch has gone.
/// </summary>
internal sealed class JobHelperRun : IHelperRun
{
    private nint _job, _process;

    private JobHelperRun(nint job, nint process)
    {
        _job = job;
        _process = process;
    }

    /// <summary>Starts <paramref name="psi"/>'s file and arguments in a new job, with no window. Throws, with nothing left running, when Windows refuses.</summary>
    public static JobHelperRun Start(ProcessStartInfo psi)
    {
        nint job = JobNative.CreateKillOnClose();
        try { return new JobHelperRun(job, JobNative.StartInJob(job, psi.FileName, psi.Arguments, psi.WorkingDirectory)); }
        catch
        {
            JobNative.Close(job);
            throw;
        }
    }

    public bool WaitForExit(int milliseconds) => JobNative.WaitForExit(_process, milliseconds);

    public int ExitCode => JobNative.ExitCode(_process);

    public bool InJob => true;

    public void End() => JobNative.Terminate(_job);

    /// <summary>Closes the handles: closing the job's only handle ends whatever still runs in it.</summary>
    public void Dispose()
    {
        JobNative.Close(Interlocked.Exchange(ref _process, 0));
        JobNative.Close(Interlocked.Exchange(ref _job, 0));
    }
}
