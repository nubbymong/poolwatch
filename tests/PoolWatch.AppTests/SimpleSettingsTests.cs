using PoolWatch.Core;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// The plain Settings form's mapping onto <see cref="Settings"/> (automatic cache clearing and deeper capture) and its
/// checks. Pure: nothing is saved and no window is opened.
/// </summary>
public class SimpleSettingsTests
{
    [Fact]
    public void TheFormShowsTheSettingsItMaps()
    {
        var s = new Settings { AutoClearEnabled = true, AutoClearEveryHours = 2.5, AutoClearPoolGb = 12, DeeperCapture = true };
        Assert.Equal(new SimpleSettings(true, 2.5m, 12m, true), SimpleSettings.From(s));
        // The defaults: off, every hour, over 8 GB, no deeper capture.
        Assert.Equal(new SimpleSettings(false, 1m, 8m, false), SimpleSettings.From(new Settings()));
    }

    [Fact]
    public void ValuesOutsideTheFormsRangeAreShownWithinIt()
    {
        var s = new Settings { AutoClearEveryHours = 1000, AutoClearPoolGb = double.NaN };
        var v = SimpleSettings.From(s);
        Assert.Equal(SimpleSettings.MaxHours, v.EveryHours);
        Assert.Equal(0m, v.PoolGb);
        Assert.Equal(1.3m, SimpleSettings.From(new Settings { AutoClearEveryHours = 1.26 }).EveryHours); // one decimal
    }

    [Fact]
    public void SavingWritesExactlyTheFourSettingsAndNothingElse()
    {
        var s = new Settings();
        string before = s.ToJson();
        Assert.True(new SimpleSettings(true, 6m, 10.5m, true).ApplyTo(s, out var problems));
        Assert.Empty(problems);
        Assert.True(s.AutoClearEnabled);
        Assert.Equal(6, s.AutoClearEveryHours);
        Assert.Equal(10.5, s.AutoClearPoolGb);
        Assert.True(s.DeeperCapture);
        // Every other setting is as it was.
        var back = new Settings { AutoClearEnabled = false, AutoClearEveryHours = 1, AutoClearPoolGb = 8, DeeperCapture = false };
        var other = Settings.Parse(s.ToJson());
        other.AutoClearEnabled = back.AutoClearEnabled; other.AutoClearEveryHours = back.AutoClearEveryHours;
        other.AutoClearPoolGb = back.AutoClearPoolGb; other.DeeperCapture = back.DeeperCapture;
        Assert.Equal(Settings.Parse(before).ToJson(), other.ToJson());
    }

    [Fact]
    public void AutomaticClearingNeedsSomethingToGoBy()
    {
        var s = new Settings();
        Assert.False(new SimpleSettings(true, 0m, 0m, false).ApplyTo(s, out var problems));
        Assert.Contains(problems, p => p.Contains("nothing to go by"));
        Assert.False(s.AutoClearEnabled); // nothing was written
        // Off, the two zeros are fine; on, either rule alone is fine.
        Assert.Empty(new SimpleSettings(false, 0m, 0m, false).Problems());
        Assert.Empty(new SimpleSettings(true, 0m, 8m, false).Problems());
        Assert.Empty(new SimpleSettings(true, 1m, 0m, false).Problems());
    }

    [Fact]
    public void OutOfRangeValuesAreRefusedInPlainWords()
    {
        Assert.Contains(new SimpleSettings(true, 0.2m, 0m, false).Problems(), p => p.Contains("half hour"));
        Assert.Contains(new SimpleSettings(false, 200m, 8m, false).Problems(), p => p.StartsWith("How often"));
        Assert.Contains(new SimpleSettings(false, -1m, 8m, false).Problems(), p => p.StartsWith("How often"));
        Assert.Contains(new SimpleSettings(false, 1m, 2000m, false).Problems(), p => p.StartsWith("Pool size"));
        Assert.Contains(new SimpleSettings(false, 1m, -0.5m, false).Problems(), p => p.StartsWith("Pool size"));
        Assert.Empty(new SimpleSettings(true, SimpleSettings.MinHours, SimpleSettings.MaxPoolGb, true).Problems());
    }

    [Fact]
    public void TheDeeperCaptureLineSaysWhenItHasNoEffect()
    {
        Assert.Contains("no effect yet", SimpleSettings.DeeperCaptureHint(new Settings()));
        Assert.DoesNotContain("no effect", SimpleSettings.DeeperCaptureHint(new Settings { CaptureOnAlert = true }));
        Assert.DoesNotContain("no effect", SimpleSettings.DeeperCaptureHint(new Settings { CpuTraceEnabled = true }));
        Assert.StartsWith("Never more than once every 45 minutes.", SimpleSettings.AutoClearHint(new Settings { AutoClearMinMinutes = 45 }));
    }
}
