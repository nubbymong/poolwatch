using System.Runtime.InteropServices;

namespace PoolWatch.Native;

/// <summary>
/// The PC's working-time timer, which retention counts on (<see cref="Core.Model.RetentionClock"/>):
/// QueryUnbiasedInterruptTime, the interrupt-time count less the time the system spent asleep or hibernated, from 0 when
/// Windows starts (Microsoft Learn, "QueryUnbiasedInterruptTime" and "Interrupt Time"). It goes on only while Windows
/// runs and is awake, and no clock setting moves it, unlike Environment.TickCount64, which goes on across sleep and
/// hibernation.
/// </summary>
internal static class WorkingTime
{
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);

    /// <summary>The timer, in milliseconds (Windows counts it in 100-nanosecond units).</summary>
    public static long Ms() =>
        QueryUnbiasedInterruptTime(out ulong unbiased) ? (long)(unbiased / 10_000) : throw new InvalidOperationException("Windows did not give its unbiased interrupt time.");
}
