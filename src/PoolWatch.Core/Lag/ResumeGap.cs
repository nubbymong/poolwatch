namespace PoolWatch.Core.Lag;

/// <summary>
/// A probe that wakes up long after it was due came back from sleep / hibernation, not from a starved
/// scheduler (which would be a hang long before). Then the wake-up lateness is not lag, and the compositor's
/// missed / dropped frame counter must be re-based: the frames "missed" while the PC slept are not lag either. Two clocks
/// are read so that a sleep shows on at least one: the probe's lateness is timed on the Stopwatch, which may not count the
/// time asleep, and the time since the last probe on Environment.TickCount64, which goes on across sleep. Neither is the
/// system clock, which a clock setting moves, so a clock set forward is never taken for a sleep.
/// </summary>
public static class ResumeGap
{
    /// <summary>A probe this late (or this much past its interval since the last one) is a suspend / resume gap.</summary>
    public static readonly TimeSpan SuspendGap = TimeSpan.FromMinutes(2);

    /// <param name="lateness">How late the probe woke up on the monotonic clock.</param>
    /// <param name="sinceLast">The time since the previous probe on Environment.TickCount64 (null for the first).</param>
    /// <param name="interval">The probe interval.</param>
    public static bool Detected(TimeSpan lateness, TimeSpan? sinceLast, TimeSpan interval) =>
        lateness > SuspendGap || (sinceLast is { } w && w > interval + SuspendGap);
}
