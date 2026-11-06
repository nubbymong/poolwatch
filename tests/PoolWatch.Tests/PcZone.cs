using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace PoolWatch.Tests;

/// <summary>
/// The PC's time zone as this test process's conversions see it, set for a test and put back after it:
/// <see cref="TimeZoneInfo.Local"/>, and what converts a time through it (ToLocalTime, ToUniversalTime, a local time's
/// "zzz" offset, System.Text.Json's reading of a time with an offset; <see cref="PcZoneTests"/> holds each). Not the
/// current time: on Windows, .NET reads DateTime.Now's offset from Windows' own zone, so under a set zone DateTime.Now
/// keeps Windows' local time and DateTimeOffset.Now pairs that time with the set zone's offset, a wrong instant. A test
/// that sets the zone uses fixed times, never those two. Windows' own setting is not touched: only .NET's cached copy of
/// it in this process. So a test can show which code reads a time through the PC's zone on any PC, CI's UTC one
/// included. The zone is the whole process's, so such a test runs apart from every other test (<see cref="Collection"/>).
/// </summary>
internal static class PcZone
{
    /// <summary>The tests that set the zone: run one at a time, never beside another test.</summary>
    public const string Collection = "PC time zone";

    /// <summary>A zone east or west of UTC by <paramref name="minutes"/>, with no daylight saving.</summary>
    public static TimeZoneInfo Fixed(int minutes) =>
        TimeZoneInfo.CreateCustomTimeZone($"PoolWatch test {minutes}", TimeSpan.FromMinutes(minutes), $"test {minutes}", $"test {minutes}");

    /// <summary>A zone on UTC in winter and an hour ahead of it in summer (from March's last Sunday to October's), as a PC's zone can be.</summary>
    public static TimeZoneInfo UtcInWinter() => TimeZoneInfo.CreateCustomTimeZone("PoolWatch test summer", TimeSpan.Zero, "test summer", "test winter", "test summer",
    [
        TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 1, 0, 0), 3, 5, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 10, 5, DayOfWeek.Sunday)),
    ]);

    /// <summary>
    /// Sets the process's local zone to <paramref name="zone"/> until the result is disposed, which reads Windows' own again.
    /// Through .NET's cache of it, found by name: a .NET that renames it fails here, by name, rather than leave the zone as it is.
    /// If setting it fails part way, Windows' own zone is read again before the failure goes on, so no test after it runs in
    /// the test zone. <paramref name="afterSet"/> (for <see cref="PcZoneTests"/>) runs once the zone is set.
    /// </summary>
    public static IDisposable Set(TimeZoneInfo zone, Action? afterSet = null)
    {
        TimeZoneInfo.ClearCachedData();
        try
        {
            object cache = Cache.GetValue(null) ?? throw new MissingFieldException("TimeZoneInfo", "s_cachedData (null)");
            var local = cache.GetType().GetField("_localTimeZone", BindingFlags.Instance | BindingFlags.NonPublic) ??
                        throw new MissingFieldException(cache.GetType().FullName, "_localTimeZone");
            local.SetValue(cache, zone);
            Assert.Same(zone, TimeZoneInfo.Local);
            afterSet?.Invoke();
            return new Restore();
        }
        catch
        {
            TimeZoneInfo.ClearCachedData();
            throw;
        }
    }

    private static readonly FieldInfo Cache = typeof(TimeZoneInfo).GetField("s_cachedData", BindingFlags.Static | BindingFlags.NonPublic) ??
                                              throw new MissingFieldException("TimeZoneInfo", "s_cachedData");

    private sealed class Restore : IDisposable
    {
        public void Dispose() => TimeZoneInfo.ClearCachedData();
    }
}

/// <summary>The tests that set the process's time zone (<see cref="PcZone"/>) run on their own, never beside another test.</summary>
[CollectionDefinition(PcZone.Collection, DisableParallelization = true)]
public sealed class PcZoneCollection
{
}

/// <summary>What <see cref="PcZone"/> says of itself: which conversions take the set zone, and that a failed set leaves none.</summary>
[Collection(PcZone.Collection)]
public class PcZoneTests
{
    [Fact]
    public void TheConversionsThroughTheLocalZoneTakeTheSetZone()
    {
        var utc = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var local = new DateTime(2026, 1, 15, 17, 30, 0, DateTimeKind.Local);
        using (PcZone.Set(PcZone.Fixed(330)))
        {
            Assert.Equal(TimeSpan.FromMinutes(330), TimeZoneInfo.Local.GetUtcOffset(utc));
            Assert.Equal((local, DateTimeKind.Local), (utc.ToLocalTime(), utc.ToLocalTime().Kind));
            Assert.Equal(utc, local.ToUniversalTime());
            Assert.Equal("+05:30", local.ToString("zzz", CultureInfo.InvariantCulture));
            using var doc = JsonDocument.Parse("\"2026-01-15T14:00:00+02:00\"");
            Assert.True(doc.RootElement.TryGetDateTime(out var read));
            Assert.Equal((local, DateTimeKind.Local), (read, read.Kind));
        }
        Assert.DoesNotContain("PoolWatch test", TimeZoneInfo.Local.Id); // Windows' own zone again
    }

    [Fact]
    public void ASetThatFailsPartWayLeavesWindowsZone()
    {
        var zone = PcZone.Fixed(330);
        Assert.Throws<InvalidOperationException>(() => PcZone.Set(zone, () => throw new InvalidOperationException("after the zone was set")));
        Assert.NotSame(zone, TimeZoneInfo.Local);
        Assert.DoesNotContain("PoolWatch test", TimeZoneInfo.Local.Id);
    }
}
