using PoolWatch.Core;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// The plain Settings form's mapping onto <see cref="Settings"/> (automatic cache clearing, deeper capture, the token leak
/// guard and update notices), its checks, its words and its fields, and what Save puts into effect at once. Nothing is
/// saved and no window is opened.
/// </summary>
public class SimpleSettingsTests
{
    [Fact]
    public void TheFormShowsTheSettingsItMaps()
    {
        var s = new Settings { AutoClearEnabled = true, AutoClearEveryHours = 2.5, AutoClearPoolGb = 12, DeeperCapture = true, TokenGuardEnabled = true, CheckForUpdates = false };
        Assert.Equal(new SimpleSettings(true, 2.5m, 12m, true, true, false), SimpleSettings.From(s));
        // The defaults: off, every hour, over 8 GB, no deeper capture, no token-leak guard, update notices on.
        Assert.Equal(new SimpleSettings(false, 1m, 8m, false, false, true), SimpleSettings.From(new Settings()));
    }

    [Fact]
    public void TheTokenLeakGuardBoxMapsToTokenGuardEnabledBothWays()
    {
        Assert.True(SimpleSettings.From(new Settings { TokenGuardEnabled = true }).TokenGuard);
        var s = new Settings { TokenGuardEnabled = true };
        var off = SimpleSettings.From(s) with { TokenGuard = false }; // the box unticked
        Assert.True(off.ApplyTo(s, out _));
        Assert.False(s.TokenGuardEnabled);
        Assert.True(new SimpleSettings(false, 1m, 8m, false, true, true).ApplyTo(s, out var problems));
        Assert.Empty(problems);
        Assert.True(s.TokenGuardEnabled);
        // A refused form writes nothing, the guard included.
        var t = new Settings();
        Assert.False(new SimpleSettings(true, 0m, 0m, false, true, true).ApplyTo(t, out _));
        Assert.False(t.TokenGuardEnabled);
    }

    [Fact]
    public void TheUpdateNoticesBoxMapsToCheckForUpdatesBothWaysInTheGuidesWords()
    {
        var s = new Settings();
        Assert.True(SimpleSettings.From(s).CheckForUpdates);
        Assert.True((SimpleSettings.From(s) with { CheckForUpdates = false }).ApplyTo(s, out _));
        Assert.False(s.CheckForUpdates);
        // The owner's words, the same in the setup guide: what it does, how often, and that nothing is installed.
        Assert.Equal("Tell me when a new version is out. Checks GitHub once a day; information only, nothing is downloaded or installed.",
            SimpleSettings.UpdatesLabel + " " + SimpleSettings.UpdatesHint);
        Assert.Equal(SimpleSettings.UpdatesLabel, new SimpleSettingsFields().Updates.Text);
    }

    [Fact]
    public void TheFormsTokenLeakGuardBoxShowsTheSettingAndReadsBackWhatIsTicked()
    {
        var fields = new SimpleSettingsFields();
        Assert.Equal(SimpleSettings.TokenGuardLabel, fields.TokenGuard.Text);
        fields.Fill(SimpleSettings.From(new Settings { TokenGuardEnabled = true }));
        Assert.True(fields.TokenGuard.Checked);
        fields.TokenGuard.Checked = false; // the user unticks it
        Assert.False(fields.Values().TokenGuard);
        fields.Fill(SimpleSettings.From(new Settings()));
        Assert.False(fields.TokenGuard.Checked);
        fields.TokenGuard.Checked = true;
        Assert.True(fields.Values().TokenGuard);
    }

    [Fact]
    public void TheFormsFieldsShowAndReadBackEveryValue()
    {
        // Each box ticked on its own once, so a box wired to another box's value fails.
        var fields = new SimpleSettingsFields();
        foreach (var v in new SimpleSettings[]
                 {
                     new(true, 2.5m, 12m, false, false, false), new(false, 6m, 0m, true, false, false),
                     new(false, 1m, 8m, false, true, false), new(false, 0.5m, 1m, false, false, true),
                 })
        {
            fields.Fill(v);
            Assert.Equal(v, fields.Values());
        }
    }

    [Fact]
    public void TheTokenLeakGuardIsExplainedInPlainWords()
    {
        string label = SimpleSettings.TokenGuardLabel, hint = SimpleSettings.TokenGuardHint, tip = SimpleSettings.TokenGuardTip;
        // Where the leak is documented, in the one name users see for the number.
        Assert.Contains("Windows 11 24H2 and 25H2", label);
        Assert.Contains("kernel pool", label);
        Assert.DoesNotContain("Insider", label + hint + tip);
        // Under the box, so it is read without a mouse: what you will notice, that it is rare, how long it lasts, and how
        // to stop it. Active changes says the same about its row.
        foreach (var text in new[] { hint, Core.Changes.ChangeWords.TokenGuardNote })
        {
            Assert.Contains("other programs can bring their windows to the front by themselves and take your keystrokes", text);
            Assert.Contains("most people find that rare", text);
        }
        Assert.Contains("until you sign out", hint);
        // The guard's check runs each time the tray app starts (by hand or from the sign-in task), not only at sign-in.
        Assert.Contains("again each time it starts, until you untick this", hint);
        Assert.DoesNotContain("sign-in", hint);
        Assert.True(hint.Length <= 250, $"{hint.Length} characters");
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
    public void TheFormSaysWhatSaveAppliesAtOnceAndWhatWaits()
    {
        foreach (var text in new[] { SimpleSettings.Saved, SimpleSettings.WhenChangesApply })
        {
            Assert.Contains("automatic clearing, the token-leak guard and update notices", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("the next time PoolWatch starts", text);
        }
        Assert.DoesNotContain("CaptureOnAlert", SimpleSettings.DeeperCaptureHint(new Settings())); // a setting's name is not plain words
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
    public void SavingWritesExactlyTheSixSettingsAndNothingElse()
    {
        var s = new Settings();
        string before = s.ToJson();
        Assert.True(new SimpleSettings(true, 6m, 10.5m, true, true, false).ApplyTo(s, out var problems));
        Assert.Empty(problems);
        Assert.True(s.AutoClearEnabled);
        Assert.Equal(6, s.AutoClearEveryHours);
        Assert.Equal(10.5, s.AutoClearPoolGb);
        Assert.True(s.DeeperCapture);
        Assert.True(s.TokenGuardEnabled);
        Assert.False(s.CheckForUpdates);
        // Every other setting is as it was.
        var back = new Settings();
        var other = Settings.Parse(s.ToJson());
        other.AutoClearEnabled = back.AutoClearEnabled; other.AutoClearEveryHours = back.AutoClearEveryHours;
        other.AutoClearPoolGb = back.AutoClearPoolGb; other.DeeperCapture = back.DeeperCapture;
        other.TokenGuardEnabled = back.TokenGuardEnabled; other.CheckForUpdates = back.CheckForUpdates;
        Assert.Equal(Settings.Parse(before).ToJson(), other.ToJson());
    }

    [Fact]
    public void SaveNeverPutsBackAnOlderValueOfAPartTheFormLeftAlone()
    {
        var shown = AtOnceSettings.Of(new Settings());
        // While Settings was open, a suggestion turned the guard on and the hourly clear on; the form changed only update notices.
        var running = AtOnceSettings.Of(new Settings { TokenGuardEnabled = true, AutoClearEnabled = true, AutoClearEveryHours = 1 });
        var edited = shown with { CheckForUpdates = false };
        Assert.Equal(running with { CheckForUpdates = false }, AtOnceSettings.ToApply(shown, edited, running));

        // A part the form changed wins, every value of the automatic clear together.
        var clear = shown.AutoClear with { Enabled = true, EveryHours = 2.5, PoolGb = 0 };
        var applied = AtOnceSettings.ToApply(shown, shown with { AutoClear = clear, TokenGuard = false }, running);
        Assert.Equal(clear, applied.AutoClear);
        Assert.True(applied.TokenGuard); // unchanged in the form: the guard stays on
        Assert.Equal(running, AtOnceSettings.ToApply(shown, shown, running)); // nothing changed in the form: nothing undone

        // What Save puts into effect goes into the settings object, and nothing else.
        var s = new Settings { DeeperCapture = true };
        applied.ApplyTo(s);
        Assert.Equal(applied, AtOnceSettings.Of(s));
        Assert.True(s.DeeperCapture);

        // The setup guide turned update notices off while Settings was open; the form left them alone: they stay off.
        var guideOff = running with { CheckForUpdates = false };
        Assert.False(AtOnceSettings.ToApply(shown, shown with { TokenGuard = true }, guideOff).CheckForUpdates);
    }

    [Fact]
    public void SaveActsOnTheGuardAndYourClearTaskOnlyWhenTheyChange()
    {
        var off = AtOnceSettings.Of(new Settings());
        var on = AtOnceSettings.Of(new Settings { TokenGuardEnabled = true, AutoClearEnabled = true });
        // The guard's sign-in check starts when the guard goes on, never at a Save while it is on already.
        Assert.True(on.TurnsGuardOn(off));
        Assert.False(on.TurnsGuardOn(on));
        Assert.False(off.TurnsGuardOn(off));
        // It stops putting Windows' setting back only when it goes off.
        Assert.True(off.TurnsGuardOff(on));
        Assert.False(off.TurnsGuardOff(off));
        Assert.False(on.TurnsGuardOff(on));
        // A clear task of your own is dealt with only when the automatic clear goes on.
        Assert.True(on.TurnsClearOn(off));
        Assert.False(on.TurnsClearOn(on));
        Assert.False(off.TurnsClearOn(off));
    }

    [Fact]
    public void SettingsOpensOnWhatIsSavedSoAChangeForTheNextStartIsNeverWrittenBackOver()
    {
        // Saved for the next start, so not running yet: deeper capture, and a value under Advanced.
        var saved = new Settings { DeeperCapture = true, SampleIntervalSeconds = 5 };
        // In effect now, as the setup guide or a suggestion left them: the hourly clear, the guard on, notices off.
        var running = new Settings { AutoClearEnabled = true, AutoClearEveryHours = 1, TokenGuardEnabled = true, CheckForUpdates = false };
        var edit = SettingsEditor.StartFrom(saved, running);
        Assert.True(edit.DeeperCapture);
        Assert.Equal(5, edit.SampleIntervalSeconds);
        Assert.Equal(AtOnceSettings.Of(running), AtOnceSettings.Of(edit));
    }

    [Fact]
    public void AutomaticClearingNeedsSomethingToGoBy()
    {
        var s = new Settings();
        Assert.False(new SimpleSettings(true, 0m, 0m, false, false, true).ApplyTo(s, out var problems));
        Assert.Contains(problems, p => p.Contains("nothing to go by"));
        Assert.False(s.AutoClearEnabled); // nothing was written
        // Off, the two zeros are fine; on, either rule alone is fine.
        Assert.Empty(new SimpleSettings(false, 0m, 0m, false, false, true).Problems());
        Assert.Empty(new SimpleSettings(true, 0m, 8m, false, false, true).Problems());
        Assert.Empty(new SimpleSettings(true, 1m, 0m, false, false, true).Problems());
    }

    [Fact]
    public void OutOfRangeValuesAreRefusedInPlainWords()
    {
        Assert.Contains(new SimpleSettings(true, 0.2m, 0m, false, false, true).Problems(), p => p.Contains("half hour"));
        Assert.Contains(new SimpleSettings(false, 200m, 8m, false, false, true).Problems(), p => p.StartsWith("How often"));
        Assert.Contains(new SimpleSettings(false, -1m, 8m, false, false, true).Problems(), p => p.StartsWith("How often"));
        Assert.Contains(new SimpleSettings(false, 1m, 2000m, false, false, true).Problems(), p => p.StartsWith("Pool size"));
        Assert.Contains(new SimpleSettings(false, 1m, -0.5m, false, false, true).Problems(), p => p.StartsWith("Pool size"));
        Assert.Empty(new SimpleSettings(true, SimpleSettings.MinHours, SimpleSettings.MaxPoolGb, true, false, true).Problems());
    }

    [Fact]
    public void TheDeeperCaptureLineSaysWhenItHasNoEffect()
    {
        Assert.Contains("no effect yet", SimpleSettings.DeeperCaptureHint(new Settings()));
        Assert.DoesNotContain("no effect", SimpleSettings.DeeperCaptureHint(new Settings { CaptureOnAlert = true }));
        Assert.DoesNotContain("no effect", SimpleSettings.DeeperCaptureHint(new Settings { CpuTraceEnabled = true }));
        Assert.StartsWith(SimpleSettings.ClearStutter + " Never more than once every 45 minutes.", SimpleSettings.AutoClearHint(new Settings { AutoClearMinMinutes = 45 }));
    }

    [Fact]
    public void TheAutomaticClearsLineSaysEachRuleWorksAloneAndThatAClearCanStutterOnce()
    {
        string hint = SimpleSettings.AutoClearHint(new Settings());
        Assert.Equal("Clearing takes a few seconds, and video or the mouse may stutter while it runs.", SimpleSettings.ClearStutter);
        Assert.Equal(1, hint.Split(SimpleSettings.ClearStutter).Length - 1);
        Assert.StartsWith(SimpleSettings.ClearStutter, hint); // said first, not after the rules
        Assert.Contains("Each rule works on its own: 0 hours clears only by size, 0 GB only on the schedule.", hint);
        // And either rule alone can be saved: a schedule with no size rule, a size rule with no schedule.
        Assert.Empty(new SimpleSettings(true, 1m, 0m, false, false, true).Problems());
        Assert.Empty(new SimpleSettings(true, 0m, 8m, false, false, true).Problems());
        var s = new Settings();
        Assert.True(new SimpleSettings(true, 0m, 8m, false, false, true).ApplyTo(s, out _));
        Assert.Equal((true, 0.0, 8.0), (s.AutoClearEnabled, s.AutoClearEveryHours, s.AutoClearPoolGb));
    }
}
