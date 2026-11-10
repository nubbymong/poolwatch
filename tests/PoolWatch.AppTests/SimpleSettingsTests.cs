using PoolWatch.Core;
using PoolWatch.Core.Updates;
using PoolWatch.Engine;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// The plain Settings form's mapping onto <see cref="Settings"/> (automatic cache clearing, deeper capture, the token leak
/// guard and update notices), its checks, its words and its fields, what Save puts into effect at once, and when an update
/// notice shows. Nothing is saved and no window is opened.
/// </summary>
public class SimpleSettingsTests
{
    [Fact]
    public void TheFormShowsTheSettingsItMaps()
    {
        var s = new Settings { AutoClearEnabled = true, AutoClearEveryHours = 2.5, AutoClearPoolGb = 12, DeeperCapture = true, TokenGuardEnabled = true, CheckForUpdates = false,
            PoolNotices = PoolNoticeLevel.NeedsAttention };
        Assert.Equal(new SimpleSettings(true, 2.5m, 12m, true, true, false, PoolNoticeLevel.NeedsAttention, RecordMode.ReportsOnly, 7), SimpleSettings.From(s));
        // The defaults: off, every hour, over 8 GB, no deeper capture, no token-leak guard, update notices on, a notice for every growth.
        Assert.Equal(new SimpleSettings(false, 1m, 8m, false, false, true, PoolNoticeLevel.EveryGrowth, RecordMode.ReportsOnly, 7), SimpleSettings.From(new Settings()));
    }

    /// <summary>
    /// Notifications: the form's three choices show the kernel pool notices' setting and write it back, each choice its
    /// own value, in the owner's words, with the line under them saying what they are for and that the notices needing a
    /// click still appear.
    /// </summary>
    [Fact]
    public void TheNotificationsChoicesMapToPoolNoticesBothWays()
    {
        Assert.Equal(PoolNoticeLevel.EveryGrowth, new Settings().PoolNotices); // today's behaviour stays the default
        foreach (var level in Enum.GetValues<PoolNoticeLevel>())
        {
            var s = new Settings();
            Assert.True((SimpleSettings.From(s) with { PoolNotices = level }).ApplyTo(s, out _));
            Assert.Equal(level, s.PoolNotices);
            Assert.Equal(level, SimpleSettings.From(s).PoolNotices);
        }
        var fields = new SimpleSettingsFields();
        Assert.Equal([SimpleSettings.EveryGrowthChoice, SimpleSettings.NeedsAttentionChoice, SimpleSettings.NoticesOffChoice], fields.NoticeChoices.Select(c => c.Text));
        Assert.Equal(["Every growth", "Only when it needs attention", "Off"], fields.NoticeChoices.Select(c => c.Text));
        Assert.Equal(PoolNoticeLevel.EveryGrowth, fields.Values().PoolNotices); // a new form: the default picked
        var choice = new Dictionary<PoolNoticeLevel, RadioButton>
        {
            [PoolNoticeLevel.EveryGrowth] = fields.EveryGrowth, [PoolNoticeLevel.NeedsAttention] = fields.NeedsAttention, [PoolNoticeLevel.Off] = fields.NoticesOff,
        };
        foreach (var level in Enum.GetValues<PoolNoticeLevel>())
        {
            fields.Fill(SimpleSettings.From(new Settings { PoolNotices = level }));
            Assert.Equal([level], choice.Where(c => c.Value.Checked).Select(c => c.Key)); // one choice, the setting's
            Assert.Equal(level, fields.Values().PoolNotices);
        }
        foreach (var level in Enum.GetValues<PoolNoticeLevel>())
        {
            choice[level].Checked = true; // the owner picks it: the others clear, wherever they are laid out
            Assert.Equal([level], choice.Where(c => c.Value.Checked).Select(c => c.Key));
            Assert.Equal(level, fields.Values().PoolNotices);
        }
        Assert.Equal("Notifications", SimpleSettings.NoticesHeading);
        // The line under the choices says what they are for (the kernel pool's notices) and that the notices needing a click
        // always appear.
        Assert.Equal("Notices about the kernel pool. The token-leak guard's need your click and always appear.", SimpleSettings.NoticesHint);
        Assert.Contains("A click on a notice opens PoolWatch.", SimpleSettings.EveryGrowthTip);
        Assert.Contains("red", SimpleSettings.NeedsAttentionTip);
        foreach (var text in new[] { SimpleSettings.NoticesHint, SimpleSettings.EveryGrowthTip, SimpleSettings.NeedsAttentionTip, SimpleSettings.NoticesOffTip })
        {
            Assert.DoesNotContain(nameof(Settings.PoolNotices), text);
            Assert.All(text.Split('\n'), line => Assert.True(line.Length <= 100, line)); // a tooltip breaks its own lines
        }
        // The UI smoke test finds the line under the choices by a -like pattern, which matches it as it is now.
        string m4 = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "ci", "m4-ui.ps1"));
        string like = System.Text.RegularExpressions.Regex.Match(m4, @"\$_ -like '(?<p>Notices about the kernel pool(?:[^']|'')+)'").Groups["p"].Value.Replace("''", "'");
        Assert.Equal(SimpleSettings.NoticesHint, like);
    }

    [Fact]
    public void TheTokenLeakGuardBoxMapsToTokenGuardEnabledBothWays()
    {
        Assert.True(SimpleSettings.From(new Settings { TokenGuardEnabled = true }).TokenGuard);
        var s = new Settings { TokenGuardEnabled = true };
        var off = SimpleSettings.From(s) with { TokenGuard = false }; // the box unticked
        Assert.True(off.ApplyTo(s, out _));
        Assert.False(s.TokenGuardEnabled);
        Assert.True(new SimpleSettings(false, 1m, 8m, false, true, true, PoolNoticeLevel.EveryGrowth, RecordMode.ReportsOnly, 7).ApplyTo(s, out var problems));
        Assert.Empty(problems);
        Assert.True(s.TokenGuardEnabled);
        // A refused form writes nothing, the guard included.
        var t = new Settings();
        Assert.False(new SimpleSettings(true, 0m, 0m, false, true, true, PoolNoticeLevel.EveryGrowth, RecordMode.ReportsOnly, 7).ApplyTo(t, out _));
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
    public void ANewerReleaseIsToldAboutOnlyWhileUpdateNoticesAreOn()
    {
        var release = new UpdateRelease("1.0.1", ReleasePage.For("v1.0.1"));
        Assert.Same(release, TrayApp.ToShow(new Settings { CheckForUpdates = true }, release));
        Assert.Null(TrayApp.ToShow(new Settings { CheckForUpdates = false }, release));
        Assert.Null(TrayApp.ToShow(new Settings { CheckForUpdates = true }, available: null));
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
                     new(true, 2.5m, 12m, false, false, false, PoolNoticeLevel.Off, RecordMode.ReportsOnly, 7), new(false, 6m, 0m, true, false, false, PoolNoticeLevel.Off, RecordMode.ReportsOnly, 7),
                     new(false, 1m, 8m, false, true, false, PoolNoticeLevel.Off, RecordMode.ReportsOnly, 7), new(false, 0.5m, 1m, false, false, true, PoolNoticeLevel.Off, RecordMode.ReportsOnly, 7),
                     new(false, 0.5m, 1m, false, false, false, PoolNoticeLevel.NeedsAttention, RecordMode.ReportsOnly, 7),
                     new(false, 0.5m, 1m, false, false, false, PoolNoticeLevel.EveryGrowth, RecordMode.ReportsOnly, 7),
                     // The records' choice and the cleanup's days read back too (each value its own, so a field wired to another fails).
                     new(false, 0.5m, 1m, false, false, false, PoolNoticeLevel.EveryGrowth, RecordMode.Detailed, 30),
                     new(false, 0.5m, 1m, false, false, false, PoolNoticeLevel.EveryGrowth, RecordMode.ReportsOnly, 1),
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
        // The tooltip: when the workaround really goes on, the one click and why, and that unticking puts it back.
        Assert.Contains("Unless two tests in a row on this build found none", tip);
        Assert.Contains("one click", tip);
        Assert.Contains("Unticking this puts back the setting PoolWatch changed", tip);
        Assert.Contains("until you untick this to end it", hint);
        // The UI smoke test (ci/m4-ui.ps1) finds this line in the form by a -like pattern: the pattern matches the hint as
        // it is now, read as PowerShell's -like reads it (* any characters, without regard to case).
        string m4 = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "ci", "m4-ui.ps1"));
        string like = System.Text.RegularExpressions.Regex.Match(m4, @"\$_ -like '(?<p>While it is on, [^']+)'").Groups["p"].Value;
        Assert.NotEqual("", like);
        Assert.Matches(new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(like).Replace(@"\*", ".*") + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline), hint);
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
        // The notifications are said with the others, since the tray reads them at each notice; and the records, which the
        // engine reads at each write (and retention at each check).
        foreach (var text in new[] { SimpleSettings.Saved, SimpleSettings.WhenChangesApply })
        {
            Assert.Contains("automatic clearing, the token-leak guard, notifications, update notices, records and their cleanup", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("the next time PoolWatch starts", text);
        }
        // With --config nothing is saved for the next start, and Save says so.
        Assert.Contains("automatic clearing, the token-leak guard, notifications, update notices, records and their cleanup apply now", SimpleSettings.NotSavedWithConfig, StringComparison.OrdinalIgnoreCase);
        // The UI smoke test (ci/m4-ui.ps1) finds the bottom line by a -like pattern, which matches it as it is now.
        string m4 = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "ci", "m4-ui.ps1"));
        string like = System.Text.RegularExpressions.Regex.Match(m4, @"\$_ -like '(?<p>\*Save puts [^']+)'").Groups["p"].Value;
        Assert.NotEqual("", like);
        Assert.Matches(new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(like).Replace(@"\*", ".*") + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline), SimpleSettings.WhenChangesApply);
        Assert.Contains("Nothing is saved for the next time PoolWatch starts", SimpleSettings.NotSavedWithConfig);
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

    /// <summary>
    /// Records is in the plain form (no advanced option): its two choices, the line of the one chosen, and the cleanup's
    /// select with setup's days (and the one in effect when it is none of them); Save puts both into effect at once, as
    /// each at-once part: what the form changed, else what runs now.
    /// </summary>
    [Fact]
    public void RecordsAreInThePlainFormAndSaveAppliesThemAtOnce()
    {
        var fields = new SimpleSettingsFields();
        Assert.True(fields.ReportsOnly.Checked && !fields.Detailed.Checked); // before it is filled: the defaults
        Assert.Equal((SimpleSettings.ReportsOnlyHint, 7), (fields.RecordsHint.Text, fields.Days));
        fields.Fill(SimpleSettings.From(new Settings { RecordMode = RecordMode.Detailed, RetentionDays = 10 }));
        Assert.True(fields.Detailed.Checked && !fields.ReportsOnly.Checked);
        Assert.Equal(SimpleSettings.DetailedHint, fields.RecordsHint.Text);
        Assert.Equal(["1 day", "3 days", "7 days", "10 days", "14 days", "30 days", "60 days", "90 days"], fields.CleanUpDays.Items.Cast<object>().Select(i => i.ToString()));
        Assert.Equal("10 days", fields.CleanUpDays.SelectedItem?.ToString());
        fields.ReportsOnly.Checked = true;
        Assert.False(fields.Detailed.Checked);
        Assert.Equal(SimpleSettings.ReportsOnlyHint, fields.RecordsHint.Text);
        fields.CleanUpDays.SelectedIndex = 1;
        var v = fields.Values();
        Assert.Equal((RecordMode.ReportsOnly, 3), (v.Records, v.RetentionDays));
        var s = new Settings { RecordMode = RecordMode.Detailed };
        Assert.True(v.ApplyTo(s, out _));
        Assert.Equal((RecordMode.ReportsOnly, 3), (s.RecordMode, s.RetentionDays));
        Assert.Contains(new SimpleSettings(false, 1m, 8m, false, false, true, PoolNoticeLevel.EveryGrowth, RecordMode.Detailed, 0).Problems(), p => p.StartsWith("Clean up"));

        var shown = AtOnceSettings.Of(new Settings());
        var edited = AtOnceSettings.Of(new Settings { RecordMode = RecordMode.Detailed, RetentionDays = 30 });
        var running = AtOnceSettings.Of(new Settings { RetentionDays = 14 });
        var apply = AtOnceSettings.ToApply(shown, edited, running);
        Assert.Equal((RecordMode.Detailed, 30), (apply.Records, apply.RetentionDays));
        apply = AtOnceSettings.ToApply(shown, shown, running); // left alone here: what runs now (setup may have changed it)
        Assert.Equal((RecordMode.ReportsOnly, 14), (apply.Records, apply.RetentionDays));
        var t = new Settings();
        AtOnceSettings.ToApply(shown, edited, running).ApplyTo(t);
        Assert.Equal((RecordMode.Detailed, 30), (t.RecordMode, t.RetentionDays));
    }

    [Fact]
    public void SavingWritesExactlyTheNineSettingsAndNothingElse()
    {
        // Nine since the Records part (what PoolWatch keeps on disk, and when it is cleaned up) joined the plain form.
        var s = new Settings();
        string before = s.ToJson();
        Assert.True(new SimpleSettings(true, 6m, 10.5m, true, true, false, PoolNoticeLevel.Off, RecordMode.Detailed, 30).ApplyTo(s, out var problems));
        Assert.Empty(problems);
        Assert.True(s.AutoClearEnabled);
        Assert.Equal(6, s.AutoClearEveryHours);
        Assert.Equal(10.5, s.AutoClearPoolGb);
        Assert.True(s.DeeperCapture);
        Assert.True(s.TokenGuardEnabled);
        Assert.False(s.CheckForUpdates);
        Assert.Equal(PoolNoticeLevel.Off, s.PoolNotices);
        Assert.Equal((RecordMode.Detailed, 30), (s.RecordMode, s.RetentionDays));
        // Every other setting is as it was.
        var back = new Settings();
        var other = Settings.Parse(s.ToJson());
        Assert.Equal(PoolNoticeLevel.Off, other.PoolNotices); // as settings.json keeps it
        Assert.Equal((RecordMode.Detailed, 30), (other.RecordMode, other.RetentionDays));
        other.AutoClearEnabled = back.AutoClearEnabled; other.AutoClearEveryHours = back.AutoClearEveryHours;
        other.AutoClearPoolGb = back.AutoClearPoolGb; other.DeeperCapture = back.DeeperCapture;
        other.TokenGuardEnabled = back.TokenGuardEnabled; other.CheckForUpdates = back.CheckForUpdates;
        other.PoolNotices = back.PoolNotices; other.RecordMode = back.RecordMode; other.RetentionDays = back.RetentionDays;
        Assert.Equal(Settings.Parse(before).ToJson(), other.ToJson());
    }

    /// <summary>
    /// The notifications take effect at once, as the other at-once settings: Save puts the form's choice into the running
    /// settings and settings.json (LiveSettings.Apply), and the tray reads the running value at each notice, so the next
    /// incident's notice follows it with no restart. A choice the form left alone never undoes one made elsewhere.
    /// </summary>
    [Fact]
    public void TheNotificationsChoiceAppliesAtOnce()
    {
        var shown = AtOnceSettings.Of(new Settings());
        var running = new Settings();
        var apply = AtOnceSettings.ToApply(shown, shown with { PoolNotices = PoolNoticeLevel.Off }, AtOnceSettings.Of(running));
        Assert.Equal(PoolNoticeLevel.Off, apply.PoolNotices);
        using var f = new ChangeFixture();
        Directory.CreateDirectory(f.Paths.DataRoot);
        var live = f.Live(running, persist: true);
        live.Apply("Settings", apply.ApplyTo); // as SettingsEditor.Save does
        Assert.Equal(PoolNoticeLevel.Off, live.Running.PoolNotices);
        Assert.Equal(PoolNoticeLevel.Off, Settings.Load(f.Paths.SettingsFile).PoolNotices);
        // The tray's handler, given the running value as it reads it at each notice, shows nothing now.
        var info = new Core.Tags.TagInfo("<unknown>", "File objects", "pooltag.txt");
        var growth = new Core.Incidents.Incident { Id = "x", Dir = @"C:\data\incidents\x", Tag = PoolTag.Parse("File"), Kind = Core.Incidents.IncidentKind.Growth };
        Assert.Null(PoolCauses.TrayNotice(growth, live.Running.PoolNotices, _ => info, [], _ => { }));
        live.Apply("Settings", s => s.PoolNotices = PoolNoticeLevel.EveryGrowth);
        Assert.NotNull(PoolCauses.TrayNotice(growth, live.Running.PoolNotices, _ => info, [], _ => { }));
        // Left alone in the form, a value set elsewhere stays.
        var elsewhere = AtOnceSettings.Of(new Settings { PoolNotices = PoolNoticeLevel.NeedsAttention });
        Assert.Equal(PoolNoticeLevel.NeedsAttention, AtOnceSettings.ToApply(shown, shown with { CheckForUpdates = false }, elsewhere).PoolNotices);
        // The tray reads it at each notice and at each refresh of its icon (the wiring needs the running app, so its lines
        // are held).
        string tray = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Ui", "TrayApp.cs"));
        Assert.Contains("_notices.Opened(inc, _live.Running.PoolNotices)", tray);
        Assert.Contains("_notices.TurnedRed(_analysis.Incidents.Open, _live.Running.PoolNotices)", tray);
        Assert.Contains("PoolCauses.TrayNotice(i, level, lookup, services(), log)", File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Ui", "PoolCauses.cs")));
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
    public void AnOpeningOfSettingsStartsFromTheSavedFileWithThePartsInEffectNow()
    {
        using var f = new ChangeFixture();
        Directory.CreateDirectory(f.Paths.DataRoot);
        new Settings { DeeperCapture = true }.Save(f.Paths.SettingsFile); // saved for the next start
        var running = new Settings { AutoClearEnabled = true, AutoClearEveryHours = 1 }; // in effect now, not saved yet

        // The editor as Settings makes it; its constructor only keeps the analysis, the actions and the shell.
        static Settings Opened(LiveSettings live) => new SettingsEditor(analysis: null!, actions: null!, live, shell: null!).Edit;
        var edit = Opened(f.Live(running, persist: true));
        Assert.True(edit.DeeperCapture);
        Assert.True(edit.AutoClearEnabled);

        // With --config, settings.json is never written: the running copy is what the next start reads.
        var fromConfig = Opened(f.Live(running, persist: false));
        Assert.False(fromConfig.DeeperCapture);
        Assert.True(fromConfig.AutoClearEnabled);
    }

    [Fact]
    public void AutomaticClearingNeedsSomethingToGoBy()
    {
        var s = new Settings();
        Assert.False(new SimpleSettings(true, 0m, 0m, false, false, true, PoolNoticeLevel.EveryGrowth, RecordMode.ReportsOnly, 7).ApplyTo(s, out var problems));
        Assert.Contains(problems, p => p.Contains("nothing to go by"));
        Assert.False(s.AutoClearEnabled); // nothing was written
        // Off, the two zeros are fine; on, either rule alone is fine.
        Assert.Empty(new SimpleSettings(false, 0m, 0m, false, false, true, PoolNoticeLevel.EveryGrowth, RecordMode.ReportsOnly, 7).Problems());
        Assert.Empty(new SimpleSettings(true, 0m, 8m, false, false, true, PoolNoticeLevel.EveryGrowth, RecordMode.ReportsOnly, 7).Problems());
        Assert.Empty(new SimpleSettings(true, 1m, 0m, false, false, true, PoolNoticeLevel.EveryGrowth, RecordMode.ReportsOnly, 7).Problems());
    }

    [Fact]
    public void OutOfRangeValuesAreRefusedInPlainWords()
    {
        Assert.Contains(new SimpleSettings(true, 0.2m, 0m, false, false, true, PoolNoticeLevel.EveryGrowth, RecordMode.ReportsOnly, 7).Problems(), p => p.Contains("half hour"));
        Assert.Contains(new SimpleSettings(false, 200m, 8m, false, false, true, PoolNoticeLevel.EveryGrowth, RecordMode.ReportsOnly, 7).Problems(), p => p.StartsWith("How often"));
        Assert.Contains(new SimpleSettings(false, -1m, 8m, false, false, true, PoolNoticeLevel.EveryGrowth, RecordMode.ReportsOnly, 7).Problems(), p => p.StartsWith("How often"));
        Assert.Contains(new SimpleSettings(false, 1m, 2000m, false, false, true, PoolNoticeLevel.EveryGrowth, RecordMode.ReportsOnly, 7).Problems(), p => p.StartsWith("Pool size"));
        Assert.Contains(new SimpleSettings(false, 1m, -0.5m, false, false, true, PoolNoticeLevel.EveryGrowth, RecordMode.ReportsOnly, 7).Problems(), p => p.StartsWith("Pool size"));
        Assert.Empty(new SimpleSettings(true, SimpleSettings.MinHours, SimpleSettings.MaxPoolGb, true, false, true, PoolNoticeLevel.EveryGrowth, RecordMode.ReportsOnly, 7).Problems());
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
    public void TheAutomaticClearsLineSaysThatAClearCanStutterOnceAndEachRuleCanBeSavedAlone()
    {
        string hint = SimpleSettings.AutoClearHint(new Settings());
        Assert.Equal("Clearing takes a few seconds, and video or the mouse may stutter while it runs.", SimpleSettings.ClearStutter);
        Assert.Equal(1, hint.Split(SimpleSettings.ClearStutter).Length - 1);
        Assert.StartsWith(SimpleSettings.ClearStutter, hint); // said first, not after the rules
        // The three rules are named by the form's choices, as the setup guide names them.
        Assert.DoesNotContain("0 hours", hint);
        Assert.Equal(["On a schedule", "At a pool size", "Whichever comes first"], new[] { SimpleSettings.ScheduleChoice, SimpleSettings.SizeChoice, SimpleSettings.EitherChoice });
        // And either rule alone can be saved: a schedule with no size rule, a size rule with no schedule.
        Assert.Empty(new SimpleSettings(true, 1m, 0m, false, false, true, PoolNoticeLevel.EveryGrowth, RecordMode.ReportsOnly, 7).Problems());
        Assert.Empty(new SimpleSettings(true, 0m, 8m, false, false, true, PoolNoticeLevel.EveryGrowth, RecordMode.ReportsOnly, 7).Problems());
        var s = new Settings();
        Assert.True(new SimpleSettings(true, 0m, 8m, false, false, true, PoolNoticeLevel.EveryGrowth, RecordMode.ReportsOnly, 7).ApplyTo(s, out _));
        Assert.Equal((true, 0.0, 8.0), (s.AutoClearEnabled, s.AutoClearEveryHours, s.AutoClearPoolGb));
    }

    [Fact]
    public void TheThreeRulesAreChosenByNameAndKeepOnlyTheNumbersTheyUse()
    {
        Assert.Equal(ClearRule.Schedule, SimpleSettings.RuleOf(1m, 0m));
        Assert.Equal(ClearRule.PoolSize, SimpleSettings.RuleOf(0m, 8m));
        Assert.Equal(ClearRule.Either, SimpleSettings.RuleOf(1m, 8m));
        Assert.Equal(ClearRule.Either, SimpleSettings.RuleOf(0m, 0m)); // nothing set: both fields to fill
        Assert.Equal((2m, 0m), SimpleSettings.ForRule(ClearRule.Schedule, 2m, 8m));
        Assert.Equal((0m, 8m), SimpleSettings.ForRule(ClearRule.PoolSize, 2m, 8m));
        Assert.Equal((2m, 8m), SimpleSettings.ForRule(ClearRule.Either, 2m, 8m));

        var fields = new SimpleSettingsFields();
        Assert.Equal([SimpleSettings.ScheduleChoice, SimpleSettings.SizeChoice, SimpleSettings.EitherChoice], new[] { fields.Schedule.Text, fields.PoolSize.Text, fields.Either.Text });
        // A schedule alone: the size field shows the usual 8 GB, unused, and is saved as no size rule.
        fields.Fill(new SimpleSettings(true, 2m, 0m, false, false, true, PoolNoticeLevel.EveryGrowth, RecordMode.ReportsOnly, 7));
        Assert.Equal((ClearRule.Schedule, 2m, 8m), (fields.Rule, fields.Hours.Value, fields.PoolGb.Value));
        Assert.Equal((2m, 0m), (fields.Values().EveryHours, fields.Values().PoolGb));
        // The owner picks whichever comes first: both numbers count.
        fields.Either.Checked = true;
        Assert.Equal((2m, 8m), (fields.Values().EveryHours, fields.Values().PoolGb));
        // Then a pool size alone: the schedule is off.
        fields.PoolSize.Checked = true;
        Assert.Equal((0m, 8m), (fields.Values().EveryHours, fields.Values().PoolGb));
        Assert.Empty(fields.Values().Problems());
        // A size alone, read back as a size alone (the schedule field shows the usual hour).
        fields.Fill(new SimpleSettings(true, 0m, 12m, false, false, true, PoolNoticeLevel.EveryGrowth, RecordMode.ReportsOnly, 7));
        Assert.Equal((ClearRule.PoolSize, 1m, 12m), (fields.Rule, fields.Hours.Value, fields.PoolGb.Value));
    }
}
