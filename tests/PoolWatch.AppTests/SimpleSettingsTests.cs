using PoolWatch.Core;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// The plain Settings form's mapping onto <see cref="Settings"/> (automatic cache clearing, deeper capture and the token
/// leak guard), its checks, its words and its fields. Nothing is saved and no window is opened.
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
    public void TheTokenLeakGuardIsExplainedInShortPlainWords()
    {
        string label = SimpleSettings.TokenGuardLabel, hint = SimpleSettings.TokenGuardHint, tip = SimpleSettings.TokenGuardTip;
        Assert.Contains("Windows 11 Insider", label);
        // Under the box, so it is read without a mouse, and short: the one trade-off and how long it lasts.
        Assert.Contains("lets any program bring its window to the front until you sign out", hint);
        Assert.True(hint.Length <= 150, $"{hint.Length} characters");
        // The tooltip: when the workaround really goes on, the one click and why, and that turning it off undoes nothing.
        Assert.Contains("Unless two tests in a row on this build found none", tip);
        Assert.Contains("one click", tip);
        Assert.Contains("does not undo", tip);
        // A tooltip wraps only at the screen's edge, so it breaks its own lines.
        Assert.All(tip.Split('\n'), line => Assert.True(line.Length <= 100, line));
        // No setting names or technical terms (those stay in Advanced and the README).
        foreach (var text in new[] { label, hint, tip })
            foreach (var word in new[] { "TokenGuard", "registry", "kernel token", "child", "timeout", "foreground", "session" })
                Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheLineUnderTheBoxSaysAChangeAppliesAtTheNextStart()
    {
        // The guard starts with the tray app (UiRunner), so a change waits for PoolWatch's next start: said where the box is,
        // not only in its tooltip.
        Assert.EndsWith("A change here applies the next time PoolWatch starts.", SimpleSettings.TokenGuardHint);
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
