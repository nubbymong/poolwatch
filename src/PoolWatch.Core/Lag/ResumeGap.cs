namespace PoolWatch.Core.Lag;

/// <summary>
/// C3 (review): a probe that wakes up long after it was due came back from sleep / hibernation, not from a starved
/// scheduler (which would be a hang long before). Then the wake-up lateness is not lag, and (review round 2) the compositor's
/// missed / dropped frame counter must be re-based: the frames "missed" while the PC slept are not lag either. Two clocks
/// are checked because either may hide the gap: the monotonic one may stop across sleep, the wall clock does not.
/// </summary>
public static class ResumeGap
{
    /// <summary>A probe this late (or this much wall time past its interval) is a suspend / resume gap.</summary>
    public static readonly TimeSpan SuspendGap = TimeSpan.FromMinutes(2);

    /// <param name="lateness">How late the probe woke up on the monotonic clock.</param>
    /// <param name="wallSinceLast">Wall-clock time since the previous probe (null for the first).</param>
    /// <param name="interval">The probe interval.</param>
    public static bool Detected(TimeSpan lateness, TimeSpan? wallSinceLast, TimeSpan interval) =>
        lateness > SuspendGap || (wallSinceLast is { } w && w > interval + SuspendGap);
}
