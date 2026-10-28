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
        var s = new Settings { AutoClearEnabled = true, AutoClearEveryHours = 2.5, AutoClearPoolGb = 12, DeeperCapture = true, TokenGuardEnabled = true };
        Assert.Equal(new SimpleSettings(true, 2.5m, 12m, true, true), SimpleSettings.From(s));
        // The defaults: off, every hour, over 8 GB, no deeper capture, no token-leak guard.
        Assert.Equal(new SimpleSettings(false, 1m, 8m, false, false), SimpleSettings.From(new Settings()));
        Assert.False(new Settings().TokenGuardEnabled); // off by default, so the form starts unticked
    }

    [Fact]
    public void TheTokenLeakGuardBoxMapsToTokenGuardEnabledBothWays()
    {
        Assert.True(SimpleSettings.From(new Settings { TokenGuardEnabled = true }).TokenGuard);
        var s = new Settings { TokenGuardEnabled = true };
        var off = SimpleSettings.From(s) with { TokenGuard = false }; // the box unticked
        Assert.True(off.ApplyTo(s, out _));
        Assert.False(s.TokenGuardEnabled);
        Assert.True(new SimpleSettings(false, 1m, 8m, false, true).ApplyTo(s, out var problems));
        Assert.Empty(problems);
        Assert.True(s.TokenGuardEnabled);
        // A refused form writes nothing, the guard included.
        var t = new Settings();
        Assert.False(new SimpleSettings(true, 0m, 0m, false, true).ApplyTo(t, out _));
        Assert.False(t.TokenGuardEnabled);
    }

    [Fact]
    public void TheFormsTokenLeakGuardBoxShowsTheSettingAndSavesWhatIsTicked()
    {
        var fields = new SimpleSettingsFields();
        Assert.Equal(SimpleSettings.TokenGuardLabel, fields.TokenGuard.Text);
        fields.Show(SimpleSettings.From(new Settings { TokenGuardEnabled = true }));
        Assert.True(fields.TokenGuard.Checked);
        fields.TokenGuard.Checked = false; // the user unticks it
        Assert.False(fields.Values().TokenGuard);
        fields.Show(SimpleSettings.From(new Settings()));
        Assert.False(fields.TokenGuard.Checked);
        fields.TokenGuard.Checked = true;
        Assert.True(fields.Values().TokenGuard);
    }

    [Fact]
    public void TheFormsFieldsShowAndReadBackEveryValue()
    {
        var fields = new SimpleSettingsFields();
        foreach (var v in new SimpleSettings[] { new(true, 2.5m, 12m, false, true), new(false, 6m, 0m, true, false) })
        {
            fields.Show(v);
            Assert.Equal(v, fields.Values());
        }
    }

    [Fact]
    public void TheTokenLeakGuardIsExplainedInPlainWords()
    {
        // What it does, that it is this session only and never the registry, the one click and why, and when it applies.
        Assert.Contains("Windows 11 Insider", SimpleSettings.TokenGuardLabel);
        Assert.Contains("this session only", SimpleSettings.TokenGuardHint);
        Assert.Contains("never the registry", SimpleSettings.TokenGuardHint);
        Assert.Contains("one click", SimpleSettings.TokenGuardHint);
        Assert.Contains("only the front program", SimpleSettings.TokenGuardHint);
        Assert.Contains("starts a child", SimpleSettings.TokenGuardTip);
        Assert.Contains("next start", SimpleSettings.TokenGuardTip);
        // Two lines on the form: the hint stays short (measured: about 95 characters a line at the form's width).
        Assert.True(SimpleSettings.TokenGuardHint.Length <= 180, $"{SimpleSettings.TokenGuardHint.Length} characters");
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
    public void SavingWritesExactlyTheFiveSettingsAndNothingElse()
    {
        var s = new Settings();
        string before = s.ToJson();
        Assert.True(new SimpleSettings(true, 6m, 10.5m, true, true).ApplyTo(s, out var problems));
        Assert.Empty(problems);
        Assert.True(s.AutoClearEnabled);
        Assert.Equal(6, s.AutoClearEveryHours);
        Assert.Equal(10.5, s.AutoClearPoolGb);
        Assert.True(s.DeeperCapture);
        Assert.True(s.TokenGuardEnabled);
        // Every other setting is as it was.
        var back = new Settings();
        var other = Settings.Parse(s.ToJson());
        other.AutoClearEnabled = back.AutoClearEnabled; other.AutoClearEveryHours = back.AutoClearEveryHours;
        other.AutoClearPoolGb = back.AutoClearPoolGb; other.DeeperCapture = back.DeeperCapture;
        other.TokenGuardEnabled = back.TokenGuardEnabled;
        Assert.Equal(Settings.Parse(before).ToJson(), other.ToJson());
    }

    [Fact]
    public void AutomaticClearingNeedsSomethingToGoBy()
    {
        var s = new Settings();
        Assert.False(new SimpleSettings(true, 0m, 0m, false, false).ApplyTo(s, out var problems));
        Assert.Contains(problems, p => p.Contains("nothing to go by"));
        Assert.False(s.AutoClearEnabled); // nothing was written
        // Off, the two zeros are fine; on, either rule alone is fine.
        Assert.Empty(new SimpleSettings(false, 0m, 0m, false, false).Problems());
        Assert.Empty(new SimpleSettings(true, 0m, 8m, false, false).Problems());
        Assert.Empty(new SimpleSettings(true, 1m, 0m, false, false).Problems());
    }

    [Fact]
    public void OutOfRangeValuesAreRefusedInPlainWords()
    {
        Assert.Contains(new SimpleSettings(true, 0.2m, 0m, false, false).Problems(), p => p.Contains("half hour"));
        Assert.Contains(new SimpleSettings(false, 200m, 8m, false, false).Problems(), p => p.StartsWith("How often"));
        Assert.Contains(new SimpleSettings(false, -1m, 8m, false, false).Problems(), p => p.StartsWith("How often"));
        Assert.Contains(new SimpleSettings(false, 1m, 2000m, false, false).Problems(), p => p.StartsWith("Pool size"));
        Assert.Contains(new SimpleSettings(false, 1m, -0.5m, false, false).Problems(), p => p.StartsWith("Pool size"));
        Assert.Empty(new SimpleSettings(true, SimpleSettings.MinHours, SimpleSettings.MaxPoolGb, true, false).Problems());
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
