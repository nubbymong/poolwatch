namespace PoolWatch.Core.Model;

/// <summary>
/// A moment on both of PoolWatch's clocks, read together: <see cref="Utc"/> on the system clock, for the times shown and
/// written, and <see cref="Ms"/> in milliseconds since Windows started (Environment.TickCount64), a clock that only goes
/// forward whatever the system clock does. PoolWatch times on it the intervals, ages and schedules docs/DESIGN.md (section
/// 4) lists; the times it keeps as times of day are named there too. A sample's moment is its <see cref="Sample.TimeUtc"/>
/// and <see cref="Sample.TickMs"/> (<see cref="Of(Sample)"/>); <see cref="Now"/> reads both clocks.
/// </summary>
public readonly record struct Moment(DateTime Utc, long Ms)
{
    /// <summary>The moment a sample was taken.</summary>
    public static Moment Of(Sample s) => new(s.TimeUtc, s.TickMs);

    /// <summary>Both clocks, read now.</summary>
    public static Moment Now() => new(DateTime.UtcNow, Environment.TickCount64);

    /// <summary>
    /// A system time read at this moment on the clock that only goes forward: this moment's <see cref="Ms"/> less how long
    /// before this moment that time was, by the system clock. A time after this moment, which only a system clock set back
    /// since gives, is taken as this moment. So an interval between such times is timed on the forward clock from the
    /// moment each was read. What it cannot take out is a system clock set back or forward between the time and its
    /// reading, as the time is given only on the system clock (Windows gives a process's create time and the kernel's exit
    /// time on it, and a file keeps a time as it was written): for a process event read as it arrives, at most a sample
    /// apart; for a time read later (a process's create time first seen in a snapshot or read at an exit whose start was not
    /// held or whose listing as long-running was let go, an exit the registry no longer holds) or read back from a file, as
    /// long as the time is old.
    /// </summary>
    public long MsOf(DateTime utc) => Ms - Math.Max(0L, (long)(Utc - utc).TotalMilliseconds);

    /// <summary>
    /// A system time read at this moment, as a moment on both clocks: that time, and it on the forward clock
    /// (<see cref="MsOf"/>). A time after this moment (a system clock set back since, or a file edited) is this moment.
    /// </summary>
    public Moment Read(DateTime utc) => utc > Utc ? this : new(utc, MsOf(utc));

    /// <summary>The system time of <paramref name="ms"/> on the clock that only goes forward, by this moment: this moment's
    /// <see cref="Utc"/> moved by as much as the forward clock is from <see cref="Ms"/> (a time to show or write).</summary>
    public DateTime UtcOf(long ms) => Utc.AddMilliseconds(ms - Ms);

    /// <summary>The milliseconds from <paramref name="earlier"/> to this moment, on the clock that only goes forward.</summary>
    public long Since(Moment earlier) => Ms - earlier.Ms;
}
