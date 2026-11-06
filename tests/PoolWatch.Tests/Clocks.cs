using PoolWatch.Core.Model;

namespace PoolWatch.Tests;

/// <summary>The tests' moments: a system time and the forward clock's milliseconds, which follow it unless a test sets the system clock apart.</summary>
internal static class Clocks
{
    /// <summary>The moment <paramref name="utc"/> with the forward clock at the same count of milliseconds.</summary>
    public static Moment At(DateTime utc) => new(utc, utc.Ticks / TimeSpan.TicksPerMillisecond);

    /// <summary>The moment <paramref name="utc"/> on the system clock with the forward clock at <paramref name="ms"/> (a clock set back or forward).</summary>
    public static Moment At(DateTime utc, long ms) => new(utc, ms);

    /// <summary>The forward clock's milliseconds at <paramref name="utc"/> as <see cref="At(DateTime)"/> gives them; null for null.</summary>
    public static long? Ms(DateTime? utc) => utc is { } t ? At(t).Ms : null;
}
