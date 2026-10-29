using PoolWatch.Core;

namespace PoolWatch.Tests;

/// <summary>
/// ci/soak-churn-config.json, read the way <c>--config</c> reads it: the churn soak must run with the in-memory windows a
/// busy PC has (the 45-minute soak shortens them), so a missing or clamped setting fails here instead of hours into a run.
/// </summary>
public class ChurnSoakConfigTests
{
    private static Settings Load()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PoolWatch.slnx")))
                return Settings.Load(Path.Combine(dir.FullName, "ci", "soak-churn-config.json"), explicitConfig: true);
        throw new InvalidOperationException("PoolWatch.slnx not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void TheChurnSoakKeepsTheDefaultWindowsAndClearsOnASchedule()
    {
        var s = Load();
        s.Normalize();
        var defaults = new Settings();
        Assert.Equal(defaults.RawHistoryMinutes, s.RawHistoryMinutes);
        Assert.Equal(defaults.ShortLivedKeepMinutes, s.ShortLivedKeepMinutes);
        Assert.Equal(defaults.IncidentEventKeepMinutes, s.IncidentEventKeepMinutes);
        Assert.True(s.AutoClearEnabled);
        Assert.Equal(0.5, s.AutoClearEveryHours);
        Assert.Equal(30, s.AutoClearMinMinutes); // the fewest minutes between automatic clears the settings allow
        Assert.False(s.CpuTraceEnabled);
    }
}
