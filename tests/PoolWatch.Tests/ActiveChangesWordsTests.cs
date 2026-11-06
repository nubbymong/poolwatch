using System.Globalization;
using System.Text.RegularExpressions;
using PoolWatch.Core;
using PoolWatch.Core.Changes;
using PoolWatch.Core.Services;

namespace PoolWatch.Tests;

/// <summary>
/// The Active changes window's words: each row's title and detail line, the group headings, the caveats, and the promise
/// in its footer. Every one is plain (no setting names, no codes), says "put back" or "turn off" where the window's
/// buttons do, and never "Undo" or the window's old name.
/// </summary>
public class ActiveChangesWordsTests
{
    [Theory]
    [InlineData(1, 0, "every hour")]
    [InlineData(2.5, 0, "every 2.5 hours")]
    [InlineData(0.5, 0, "every 30 minutes")]
    [InlineData(0, 8, "when the kernel pool reaches 8 GB")]
    [InlineData(1, 8, "every hour, or at 8 GB")]
    [InlineData(0, 0, "on, but with no schedule and no size rule")]
    public void TheStandbyCacheRowSaysWhenItIsEmptied(double hours, double gb, string expected) =>
        Assert.Equal(expected, ChangeWords.AutoClearWhen(hours, gb));

    [Fact]
    public void AServiceRowSaysHowItWasBeforePoolWatchChangedIt()
    {
        Assert.Equal("service · was Automatic and running", ChangeWords.ServiceDetail(ServiceStartType.Automatic, true, turnedOff: true));
        Assert.Equal("service · was Manual", ChangeWords.ServiceDetail(ServiceStartType.Manual, false, turnedOff: true));
        Assert.Equal("service · was Manual · profile Build box, at start",
            ChangeWords.ServiceDetail(ServiceStartType.Manual, false, turnedOff: true, "profile Build box, at start"));
        Assert.Equal("service · was Manual", ChangeWords.ServiceDetail(ServiceStartType.Manual, false, turnedOff: true, "manual"));
        Assert.Equal("service · set to its Windows default · was Disabled", ChangeWords.ServiceDetail(ServiceStartType.Disabled, false, turnedOff: false));
        // Not known whether it was running: said so, never the words of one that was not running.
        Assert.Equal("service · was Automatic, not known if it was running",
            ChangeWords.ServiceDetail(ServiceStartType.Automatic, false, turnedOff: true, "manual", runningNotKnown: true));
        Assert.Equal("service · was Automatic, not known if it was running · profile Build box, at start",
            ChangeWords.ServiceDetail(ServiceStartType.Automatic, false, turnedOff: true, "profile Build box, at start", runningNotKnown: true));
    }

    /// <summary>
    /// An original of Disabled: Put back sets it back to Disabled and stops it if it runs, whatever it did before, so its row
    /// says nothing of whether it ran: not "and running" (a service Disabled but running when it was turned off, or set to
    /// its Windows default), not "not known", in either form, "Set to its Windows default" or turned off.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)] // a record cannot say both, but nothing is said either way
    public void ADisabledOriginalsRowSaysNothingOfWhetherItRan(bool running, bool notKnown)
    {
        Assert.Equal("service · set to its Windows default · was Disabled",
            ChangeWords.ServiceDetail(ServiceStartType.Disabled, running, turnedOff: false, ServicePlan.ViaWindowsDefault, runningNotKnown: notKnown));
        Assert.Equal("service · was Disabled",
            ChangeWords.ServiceDetail(ServiceStartType.Disabled, running, turnedOff: true, "manual", runningNotKnown: notKnown));
        Assert.Equal("service · was Disabled · profile Build box, at start",
            ChangeWords.ServiceDetail(ServiceStartType.Disabled, running, turnedOff: true, "profile Build box, at start", runningNotKnown: notKnown));
        // Any other original says it, in both forms.
        string said = notKnown ? ChangeWords.RunningNotKnownWords : running ? " and running" : "";
        Assert.Equal("service · was Manual" + said, ChangeWords.ServiceDetail(ServiceStartType.Manual, running, turnedOff: true, "manual", runningNotKnown: notKnown));
        Assert.Equal("service · set to its Windows default · was Automatic" + said,
            ChangeWords.ServiceDetail(ServiceStartType.Automatic, running, turnedOff: false, ServicePlan.ViaWindowsDefault, runningNotKnown: notKnown));
    }

    [Fact]
    public void EachRecordedKindHasATitleAndADetailLine()
    {
        Assert.Equal("Your clear task \"Clear standby\"", ChangeWords.TitleOf(ChangeRecordTests.ClearTask()));
        Assert.Equal("scheduled task that ran RAMMap · PoolWatch's automatic clear took its place", ChangeWords.DetailOf(ChangeRecordTests.ClearTask()));
        Assert.Equal(@"Vendor\Updater", ChangeWords.TitleOf(ChangeRecordTests.Task()));
        Assert.Equal("scheduled task · was on", ChangeWords.DetailOf(ChangeRecordTests.Task()));
        Assert.Equal("scheduled task · was off", ChangeWords.DetailOf(ChangeRecordTests.Task() with { EnabledBefore = false }));
        Assert.Equal("Helper at sign-in", ChangeWords.TitleOf(ChangeRecordTests.Startup())); // the approved design's "<name> at sign-in"
        Assert.Equal("startup app · was on", ChangeWords.DetailOf(ChangeRecordTests.Startup()));
        // The approved design's words: PoolWatch makes one shortcut only, on the desktop.
        Assert.Equal("Codex shortcut on the desktop", ChangeWords.TitleOf(ChangeRecordTests.Shortcut()));
        Assert.Equal(ChangeWords.ShortcutDetail, ChangeWords.DetailOf(ChangeRecordTests.Shortcut()));
    }

    [Fact]
    public void WhenAChangeWasMadeIsInTheUsersOwnFormat()
    {
        var when = new DateTime(2026, 10, 30, 0, 54, 0);
        Assert.Equal("since 10/30/2026 00:54", ChangeWords.Since(when, CultureInfo.InvariantCulture));
        Assert.Equal("since 30/10/2026 00:54", ChangeWords.Since(when, CultureInfo.GetCultureInfo("en-GB")));
    }

    [Fact]
    public void TurningTheTokenLeakGuardOffSaysItPutsTheWindowsSettingBack()
    {
        // Where it is turned off: its row (the note), what its button does, what Turn off says in each case, the row of a
        // setting Windows did not let PoolWatch put back then, and the uninstaller's sentence, which stands on its own (said
        // only when the guard found the setting already changed and did not change it itself).
        // Both say when it does not: only when Windows allows it, and not one the guard found already changed.
        Assert.EndsWith("Turn off stops that and puts back the Windows setting it changed when Windows allows it; " +
                        "one it found already changed stays until you sign out.", ChangeWords.TokenGuardNote);
        Assert.Equal("Turns the token-leak guard off and puts back the Windows setting it changed when Windows allows it " +
                     "(one it found already changed stays until you sign out).", ChangeWords.TokenGuardTurnOffTip);
        Assert.Equal("The token-leak guard is off.", ChangeWords.TokenGuardOff);
        Assert.Equal("The token-leak guard is off, and the Windows setting it changed is back as it was.", ChangeWords.TokenGuardOffPutBack);
        // Refused just now: where to try again (the setting's own row), and that signing out puts it back too.
        Assert.Equal("The token-leak guard is off. Windows did not let PoolWatch put its setting back just now: " +
                     "Active changes lists the setting, and its Put back tries again. Signing out also puts it back.", ChangeWords.TokenGuardOffRefused);
        // Not the guard's change (it has no record of making it in this sign-in): left as it is, until sign-out.
        Assert.StartsWith("The token-leak guard is off. The guard found the Windows setting already changed and has no record of changing it itself", ChangeWords.TokenGuardOffNotOurs);
        Assert.EndsWith(" until you sign out.", ChangeWords.TokenGuardOffNotOurs);
        Assert.DoesNotContain("when PoolWatch started", ChangeWords.TokenGuardOffNotOurs); // the guard may have been turned on later
        Assert.Equal(("Token-leak guard's Windows setting", "The token-leak guard's Windows setting is still changed"), (ChangeWords.TokenGuardSettingTitle, ChangeWords.TokenGuardSettingStill));
        Assert.StartsWith("The guard is off, but Windows did not let PoolWatch put this setting back", ChangeWords.TokenGuardSettingStillNote);
        Assert.Contains("Put back tries again", ChangeWords.TokenGuardSettingStillNote);
        Assert.Equal("The token-leak guard's Windows setting stays until you sign out.", ChangeWords.TokenGuardSettingStays);
        // Not reported: nothing put back, and no row promised (a row is listed only for a setting reported as still changed).
        Assert.Equal("The token-leak guard is off. Windows did not report its setting just now, so PoolWatch did not put it back; " +
                     "signing out puts it back.", ChangeWords.TokenGuardOffUnreported);
        Assert.DoesNotContain("Active changes", ChangeWords.TokenGuardOffUnreported);
        Assert.True(ChangeWords.SettingNotPutBack(ChangeWords.TokenGuardOffRefused) && ChangeWords.SettingNotPutBack(ChangeWords.TokenGuardOffUnreported));
        Assert.False(ChangeWords.SettingNotPutBack(ChangeWords.TokenGuardOffPutBack) || ChangeWords.SettingNotPutBack(ChangeWords.TokenGuardOff)
                     || ChangeWords.SettingNotPutBack(ChangeWords.TokenGuardOffNotOurs));
        // The uninstaller's step, when Windows did not let it put the setting back: in words for an uninstall (no window
        // lists it afterwards), that it stays until sign-out.
        Assert.Equal("The guard is off, but Windows did not let PoolWatch put back the Windows setting it changed, or did not report it: " +
                     "the setting stays as it is until you sign out.", ChangeWords.TokenGuardSettingNotPutBack);
        // The setting's own row: its button puts the setting back, the guard being off already.
        Assert.Equal("Puts back the Windows setting the token-leak guard changed. The guard is already off.", ChangeWords.TokenGuardSettingPutBackTip);
    }

    [Fact]
    public void TheShortcutStartupAppAndServiceRowsUseTheApprovedDesignsWords()
    {
        Assert.Equal(("Codex shortcut on the desktop", "starts Codex without its shader cache"), (ChangeWords.ShortcutTitle, ChangeWords.ShortcutDetail));
        Assert.Equal("Example App at sign-in", ChangeWords.StartupTitle("Example App"));
        // A service by the name the Services console shows; by its service name only when it has no other.
        Assert.Equal("Example Device Service", ChangeWords.ServiceTitle("Example Device Service", "ExampleSvc"));
        Assert.Equal("ExampleSvc", ChangeWords.ServiceTitle("", "ExampleSvc"));
        Assert.Equal("ExampleSvc", ChangeWords.ServiceTitle("  ", "ExampleSvc"));
    }

    [Fact]
    public void ARowPoolWatchCannotPutBackSaysSoOnceWithWhy()
    {
        Assert.Equal("PoolWatch cannot put this back. It ran with a saved password.", ChangeWords.CannotPutBackLine("It ran with a saved password."));
        Assert.Equal(ChangeWords.CannotPutBack, ChangeWords.CannotPutBackLine(null));
        Assert.Equal(ChangeWords.CannotPutBack, ChangeWords.CannotPutBackLine(" "));
        // A note that already says it (a clear task's) is not repeated after the window's own line.
        const string note = "PoolWatch cannot put this task back: it runs another program.";
        Assert.Equal(note, ChangeWords.CannotPutBackLine(note));
        const string account = "It ran with your saved password, which PoolWatch never has, so PoolWatch cannot put it back.";
        Assert.Equal(account, ChangeWords.CannotPutBackLine(account));
    }

    [Fact]
    public void TheTimelineSaysWhatTheButtonDid()
    {
        Assert.Equal("Turned off in Active changes: Token-leak guard. The token-leak guard is off.",
            ChangeWords.Timeline(ChangeWords.TurnOff, true, "Token-leak guard", "The token-leak guard is off."));
        Assert.Equal("Removed in Active changes: Codex shortcut on the desktop. Deleted the shortcut.",
            ChangeWords.Timeline(ChangeWords.Remove, true, ChangeWords.ShortcutTitle, "Deleted the shortcut."));
        Assert.Equal("Put back in Active changes: Turned off the startup app Helper. Turned the startup app Helper back on.",
            ChangeWords.Timeline(ChangeWords.PutBack, true, "Turned off the startup app Helper", "Turned the startup app Helper back on."));
        Assert.Equal("Not turned off in Active changes: Token-leak guard. It failed.", ChangeWords.Timeline(ChangeWords.TurnOff, false, "Token-leak guard", "It failed."));
        Assert.Equal("Not removed in Active changes: x. y", ChangeWords.Timeline(ChangeWords.Remove, false, "x", "y"));
        Assert.Equal("Not put back in Active changes: x. y", ChangeWords.Timeline(ChangeWords.PutBack, false, "x", "y"));
    }

    [Fact]
    public void TheWindowsPromiseIsThatUninstallingPutsEachBackOrSaysWhyUnlessARowSaysOtherwise()
    {
        Assert.Equal("Uninstalling puts each of these back, or says why it could not.", ChangeWords.UninstallPutsAllBack);
        Assert.Equal("Uninstalling puts each of these back, except where marked, or says why it could not.", ChangeWords.UninstallPutsBackTheOthers);
        Assert.Equal("Active changes", ChangeWords.WindowTitle);
        Assert.Equal("What PoolWatch has changed on this PC and is still in effect.", ChangeWords.Lead);
        Assert.Equal("Features you turned on", ChangeWords.FeaturesHeading);
        Assert.Equal("Turned off by PoolWatch", ChangeWords.TurnedOffHeading);
        Assert.Equal("Put all back", ChangeWords.PutAllBack);
    }

    public static IEnumerable<object[]> Words()
    {
        string[] texts =
        [
            ChangeWords.LogonTask, ChangeWords.TokenGuard, ChangeWords.TokenGuardNote, ChangeWords.TokenGuardOff, ChangeWords.AutoClearKeepsTaskNote,
            ChangeWords.NotListed, ChangeWords.WindowTitle, ChangeWords.Lead, ChangeWords.FeaturesHeading, ChangeWords.TurnedOffHeading,
            ChangeWords.OtherHeading, ChangeWords.NothingActive, ChangeWords.PutAllBack, ChangeWords.UninstallPutsAllBack,
            ChangeWords.UninstallPutsBackTheOthers, ChangeWords.CannotPutBack, ChangeWords.UninstallLeaves, ChangeWords.UninstallNeedsYourYes,
            ChangeWords.LogonTaskOff, ChangeWords.LogonTaskOffDetail, ChangeWords.LogonTaskStateNotRead, ChangeWords.LogonTaskThere,
            ChangeWords.LogonTaskThereDetail, ChangeWords.LogonTaskAskedAbout, ChangeWords.LogonTaskTriggerOff, ChangeWords.LogonTaskTriggerOffDetail,
            ChangeWords.LogonTaskNoTrigger, ChangeWords.LogonTaskNoTriggerDetail, ChangeWords.LogonTaskNotHighest, ChangeWords.LogonTaskNotHighestDetail,
            ChangeWords.LogonTaskRemoved,
            ChangeWords.LogonTaskTitle, ChangeWords.LogonTaskDetail, ChangeWords.AutoClearTitle, ChangeWords.TokenGuardTitle, ChangeWords.ShortcutDetail,
            ChangeWords.AutoClearWhen(1, 8), ChangeWords.AutoClearWhen(0, 0), ChangeWords.ServiceDetail(ServiceStartType.Automatic, true, true, "profile Build box, at start"),
            ChangeWords.ServiceDetail(ServiceStartType.Manual, false, false), ChangeWords.KeptOffAtStart("Build box", "maps"), ChangeWords.UnreadNote(""),
            ChangeWords.TitleOf(ChangeRecordTests.ClearTask()), ChangeWords.DetailOf(ChangeRecordTests.ClearTask()),
            ChangeWords.TitleOf(ChangeRecordTests.Shortcut()), ChangeWords.DetailOf(ChangeRecordTests.Task()), ChangeWords.DetailOf(ChangeRecordTests.Startup()),
            ChangeWords.TokenGuardTurnOffTip, ChangeWords.TokenGuardSettingStays, ChangeWords.TitleOf(ChangeRecordTests.Startup()),
            ChangeWords.TokenGuardOffPutBack, ChangeWords.TokenGuardOffRefused, ChangeWords.TokenGuardOffNotOurs, ChangeWords.TokenGuardSettingTitle,
            ChangeWords.TokenGuardSettingStill, ChangeWords.TokenGuardSettingStillNote, ChangeWords.TokenGuardOffUnreported,
            ChangeWords.TokenGuardSettingNotPutBack, ChangeWords.TokenGuardSettingPutBackTip,
            ChangeWords.TurnOff, ChangeWords.Remove, ChangeWords.PutBack, ChangeWords.CannotPutBackLine("It ran with a saved password."),
            ChangeWords.Timeline(ChangeWords.TurnOff, false, ChangeWords.TokenGuardTitle, ChangeWords.TokenGuardOff),
            ChangeWords.Timeline(ChangeWords.Remove, true, ChangeWords.ShortcutTitle, "Deleted the shortcut."),
            ChangeWords.Timeline(ChangeWords.PutBack, true, ChangeWords.StartupEntry("Helper", false), "Turned the startup app Helper back on."),
        ];
        return texts.Select(t => new object[] { t });
    }

    [Theory]
    [MemberData(nameof(Words))]
    public void EveryWordIsPlainAndSaysPutBackNotUndo(string text)
    {
        var settingNames = typeof(Settings).GetProperties().Select(p => p.Name).ToList();
        var code = new Regex(@"\b([a-z]+[A-Z]\w*|[A-Z][a-z]+[A-Z][a-z]+\w*)\b");
        Assert.DoesNotContain(settingNames, n => text.Contains(n, StringComparison.Ordinal));
        Assert.DoesNotMatch(code, text.Replace("PoolWatch", "").Replace("RAMMap", ""));
        Assert.DoesNotContain("!", text);
        // The window's buttons are Turn off, Remove, Put back and Put all back; its name is Active changes.
        Assert.DoesNotContain("Undo", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Applied changes", text, StringComparison.OrdinalIgnoreCase);
        // Restore points are not part of this release: nothing offers or promises one.
        Assert.DoesNotContain("restore point", text, StringComparison.OrdinalIgnoreCase);
    }
}
