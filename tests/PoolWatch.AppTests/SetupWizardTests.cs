using System.Text.RegularExpressions;
using PoolWatch.Core;
using PoolWatch.Core.Cache;
using PoolWatch.Core.Changes;
using PoolWatch.Core.Impact;
using PoolWatch.Core.Model;
using PoolWatch.Core.Recommendations;
using PoolWatch.Core.Setup;
using PoolWatch.Core.TokenGuard;
using PoolWatch.Engine;
using PoolWatch.Engine.Changes;
using PoolWatch.Engine.Setup;
using PoolWatch.Ui;
using PoolWatch.Ui.Setup;

namespace PoolWatch.AppTests;

/// <summary>
/// The first-run setup without a window on the desktop: where each way into the app leads before and after setup was
/// completed, its steps and whether it may close, what the scan of the PC becomes on "How your PC is impacted", the standby cache's four choices and the settings they write (through the
/// running settings, so the change list shows them as before), what Next applies and what a change that did not work
/// does, the Ready step's summary, and the layout of every step and of "How PoolWatch works" at 100, 125, 150 and 200%.
/// Nothing here registers a task, clears the cache or writes a shortcut: the host and the PC are fakes, and settings go
/// to a settings.json in TEMP. No window is shown.
/// </summary>
[Collection(nameof(WholeWindowLayout))] // lays windows out: one class at a time, after the parallel ones
public sealed class SetupWizardTests : IDisposable
{
    private readonly ChangeFixture _f = new();

    public void Dispose() => _f.Dispose();

    private static EnvironmentFacts Facts(params VolumeFact[] volumes) => new()
    {
        ScannedUtc = DateTime.UtcNow, WindowsName = "Windows 11 Pro", Build = "26200.7000", Architecture = "x64", Volumes = volumes,
    };

    private static readonly VolumeFact Ntfs = new(@"C:\", "NTFS", false, null);
    private static VolumeFact DevDrive(char letter, bool? trusted = true) => new($@"{letter}:\", "ReFS", true, trusted);

    /// <summary>A PC with something for every row: two Dev Drives without performance mode, the Codex app, services, and the leak.</summary>
    private static EnvironmentFacts Busy() => Facts(Ntfs, DevDrive('E'), DevDrive('F')) with
    {
        DefenderRealTimeOn = true, DefenderPerformanceModeOn = false, ChatGptInstalled = true, OptionalServicesRunning = 3,
        TokenLeak = TokenLeakStatus.Leaks, InstalledInProgramFiles = true,
    };

    // ------------------------------------------------------------------ the flow and the first run

    [Fact]
    public void TheFirstRunCannotBeClosedOrSkippedButPoolWatchOrWindowsStillCloseIt()
    {
        var flow = new SetupFlow(firstRun: true);
        Assert.False(flow.MayClose(CloseReason.UserClosing));      // the close button, Alt+F4, the taskbar's Close
        Assert.True(flow.MayClose(CloseReason.ApplicationExitCall)); // PoolWatch exits: setup opens again at the next start
        Assert.True(flow.MayClose(CloseReason.WindowsShutDown));
        Assert.True(flow.MayClose(CloseReason.TaskManagerClosing));

        Assert.False(flow.Back());
        var seen = new List<SetupStep> { flow.Step };
        while (flow.Forward()) seen.Add(flow.Step);
        Assert.Equal(SetupFlow.Steps, seen);
        Assert.Equal(["Welcome", "Your PC", "Safety", "Standby cache", "Fixes", "Services", "Ready"], SetupFlow.Steps.Select(SetupText.Rail));
        Assert.False(flow.MayClose(CloseReason.UserClosing));      // the last step too, until its button
        Assert.Equal((RailMark.Done, RailMark.Now), (flow.Mark(SetupStep.Services), flow.Mark(SetupStep.Ready)));

        flow.Finish();
        Assert.True(flow.MayClose(CloseReason.UserClosing));
        Assert.False(flow.CanGoBack);
    }

    [Fact]
    public void SetupOpenedAgainCanBeCancelled()
    {
        var flow = new SetupFlow(firstRun: false);
        Assert.True(flow.MayClose(CloseReason.UserClosing));
        Assert.Equal(RailMark.Ahead, flow.Mark(SetupStep.YourPc));
    }

    /// <summary>What the tray app does for the gate, recorded: setup opened, the main window shown, its Advanced view, the view kept, the update check.</summary>
    private sealed class GateFake
    {
        public bool Completed;
        public readonly List<string> Calls = [];

        public FirstRunGate Gate(bool smoke = false) => new(completed: () => Completed, smoke: smoke, openSetup: () => Calls.Add("setup"),
            showMain: () => Calls.Add("main"), switchToAdvanced: tab => Calls.Add($"advanced {tab}"), switchToSimple: () => Calls.Add("simple"),
            rememberView: advanced => Calls.Add(advanced ? "remember advanced" : "remember simple"), startUpdates: () => Calls.Add("updates"));
    }

    [Fact]
    public void UntilSetupWasCompletedOnceEveryWayIntoTheAppBringsSetup()
    {
        var f = new GateFake();
        var gate = f.Gate();
        gate.Start(showWindow: true);                 // the first start: setup, no main window, no update check yet
        gate.Start(showWindow: false);                // started at sign-in: setup too
        Assert.Equal(["setup", "setup"], f.Calls);
        f.Calls.Clear();
        gate.ShowWindow();                            // Open PoolWatch, a double-click on the tray icon
        gate.ShowAdvanced(1);                         // a suggestion's way into the Advanced view: not switched behind setup's back
        Assert.Equal(["setup", "setup"], f.Calls);
        f.Calls.Clear();
        gate.SetupClosed(finished: false, advanced: true, advancedBefore: false); // closed without its last step (PoolWatch exiting)
        Assert.Empty(f.Calls);

        // "Open PoolWatch" marked setup completed: the choice is kept, the update check starts, the main window opens in it.
        f.Completed = true;
        gate.SetupClosed(finished: true, advanced: true, advancedBefore: false);
        Assert.Equal(["remember advanced", "updates", "main", "advanced 0"], f.Calls);
        Assert.True(FirstRunGate.SetupFirst(setupCompleted: false, smoke: false));
        Assert.False(FirstRunGate.SetupFirst(setupCompleted: true, smoke: false));
    }

    [Fact]
    public void OnceSetupWasCompletedTheAppOpensAndSetupOpenedAgainCanBeCancelled()
    {
        var f = new GateFake { Completed = true };
        var gate = f.Gate();
        gate.Start(showWindow: true);
        Assert.Equal(["main", "updates"], f.Calls);
        f.Calls.Clear();
        gate.Start(showWindow: false);                // at sign-in: the tray only
        Assert.Equal(["updates"], f.Calls);
        f.Calls.Clear();
        gate.ShowWindow();
        gate.ShowAdvanced(1);
        Assert.Equal(["main", "main", "advanced 1"], f.Calls);
        f.Calls.Clear();
        gate.SetupClosed(finished: false, advanced: true, advancedBefore: false); // cancelled: nothing changes
        Assert.Empty(f.Calls);
        gate.SetupClosed(finished: true, advanced: false, advancedBefore: true);  // Simple chosen: the open window switches to it
        Assert.Equal(["remember simple", "updates", "main", "simple"], f.Calls);
        f.Calls.Clear();
        gate.SetupClosed(finished: true, advanced: true, advancedBefore: true);   // Advanced already: not switched again
        Assert.Equal(["remember advanced", "updates", "main"], f.Calls);
        f.Calls.Clear();
        gate.SetupClosed(finished: true, advanced: false, advancedBefore: false); // Simple already: not switched again
        Assert.Equal(["remember simple", "updates", "main"], f.Calls);
    }

    [Fact]
    public void TheCiUiTestOpensTheMainWindowAtOnce()
    {
        var f = new GateFake();
        var gate = f.Gate(smoke: true);
        gate.Start(showWindow: true);
        gate.ShowAdvanced(0);
        Assert.Equal(["main", "main", "advanced 0"], f.Calls);
        Assert.False(FirstRunGate.SetupFirst(setupCompleted: false, smoke: true));
        Assert.Equal("Run setup again...", FirstRunGate.MenuItem);
    }

    [Fact]
    public void EscCancelsSetupOpenedAgainButNotTheFirstRun()
    {
        // The form's own key handling, as Windows calls it for Esc (the window is never shown).
        var processDialogKey = typeof(Form).GetMethod("ProcessDialogKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        using var first = new SetupWizardForm(new FakeHost(Busy()), firstRun: true, advanced: false);
        _ = first.Handle;
        bool closed = false;
        first.FormClosed += (_, _) => closed = true;
        Assert.Equal(false, processDialogKey.Invoke(first, [Keys.Escape]));  // not taken as "cancel" at all
        Assert.False(closed);

        using var again = new SetupWizardForm(new FakeHost(Busy()), firstRun: false, advanced: false);
        _ = again.Handle;
        bool cancelled = false;
        again.FormClosed += (_, _) => cancelled = true;
        Assert.Equal(true, processDialogKey.Invoke(again, [Keys.Escape]));
        Assert.True(cancelled);
        Assert.False(again.Finished);
    }

    [Fact]
    public void TheFirstRunWindowHasNoCloseButtonAndDoesNotCloseButOneOpenedAgainDoes()
    {
        using var first = new SetupWizardForm(new FakeHost(Busy()), firstRun: true, advanced: false);
        _ = first.Handle; // a native window, never shown
        Assert.False(first.HasCloseButton);
        bool closed = false;
        first.FormClosed += (_, _) => closed = true;
        first.Close();
        Assert.False(closed);

        using var again = new SetupWizardForm(new FakeHost(Busy()), firstRun: false, advanced: true);
        _ = again.Handle;
        Assert.True(again.HasCloseButton);
        bool cancelled = false;
        again.FormClosed += (_, _) => cancelled = true;
        again.Close();
        Assert.True(cancelled);
        Assert.False(again.Finished);
        Assert.True(again.Advanced);                // the view it was opened with, kept
    }

    [Fact]
    public async Task FinishingMarksSetupCompletedOnceAndOnlyOnTheReadyStep()
    {
        var host = new FakeHost(Busy());
        long now = 0;
        var session = new SetupSession(host, firstRun: true, advanced: false, clock: () => now);
        await session.LoadAsync(CancellationToken.None);
        session.Advanced = true;                    // the Welcome step's choice, which the app takes when setup is done
        for (int i = 0; i < 6; i++)
        {
            Assert.True(await session.NextAsync(null!));
            now += NextWaitTicks;                   // each step on screen for its moment (Standby cache and Fixes wait for it)
        }
        Assert.Equal((SetupStep.Ready, 0), (session.Flow.Step, host.Completed));
        Assert.True(await session.NextAsync(null!)); // "Open PoolWatch"
        Assert.Equal(1, host.Completed);
        Assert.True(session.Flow.Finished && session.Advanced);
        Assert.False(await session.NextAsync(null!));
        Assert.Equal(1, host.Completed);
    }

    // ------------------------------------------------------------------ the scan as "How your PC is impacted"

    [Fact]
    public void EachKnownCauseHasItsLevelFromTheFactsAndTheStepThatFixesIt()
    {
        var rows = ImpactRows.From(Busy(), ImpactRows.Leak(Busy(), guardOn: false, now: null, checking: false, notRun: false));
        Assert.Equal([Cause.DevDriveRecords, Cause.Defender, Cause.CodexShaderCache, Cause.BackgroundServices, Cause.TokenLeak], rows.Select(r => r.Cause));
        Assert.Equal(
        [
            ("E: and F: are Dev Drives, and Windows rarely empties their cache.", ImpactLevel.High, "Fix in step 4"),
            ("Performance mode is off for E: and F:.", ImpactLevel.Medium, "Fix in step 5"),
            ("The Codex app is installed.", ImpactLevel.Low, "Fix in step 5"),
            ("Services you may not need, running all day.", ImpactLevel.Low, "Trim in the app"),
            ("PoolWatch's check found it on this Windows build.", ImpactLevel.High, "Fix in step 5"),
        ], rows.Select(r => (r.Detail, r.Level!.Value, r.Under!)));
        Assert.Equal([SetupStep.StandbyCache, SetupStep.Fixes, SetupStep.Fixes, null, SetupStep.Fixes], rows.Select(r => r.FixStep));
    }

    [Fact]
    public void ACauseNotFoundIsNoneAndStillWatchedNeverFixed()
    {
        var f = Facts(Ntfs) with { DefenderRealTimeOn = false, TokenLeak = TokenLeakStatus.Clean };
        var rows = ImpactRows.From(f, ImpactRows.Leak(f, false, null, false, false));
        Assert.All(rows, r => Assert.Equal((ImpactLevel.None, "Still watching", (SetupStep?)null), (r.Level!.Value, r.Under!, r.FixStep)));
        Assert.Equal("No Dev Drive or ReFS drive on this PC.", rows[0].Detail);
        Assert.Equal("Defender's real-time protection is off.", rows[1].Detail);
        Assert.Equal("Not found now. PoolWatch keeps watching, and offers the fix if it appears.", rows[4].Detail);
        Assert.Equal("None", SetupText.Level(ImpactLevel.None));
        Assert.Equal(["Low impact", "Medium impact", "High impact"], new[] { ImpactLevel.Low, ImpactLevel.Medium, ImpactLevel.High }.Select(SetupText.Level));
        // Nothing is fixed before its step: the words say where it will be.
        Assert.DoesNotContain(ImpactRows.From(Busy(), LeakState.Found), r => r.Under?.Contains("Fixed", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Theory]
    [InlineData(LeakVerdict.Leak, "Found", "PoolWatch's check found it on this Windows build.")]
    [InlineData(LeakVerdict.Clean, "NotFound", "Not found now. PoolWatch keeps watching, and offers the fix if it appears.")]
    [InlineData(LeakVerdict.Unclear, "Unclear", "The check gave no clear answer. PoolWatch keeps watching, and offers the fix if it appears.")]
    public void TheTokenLeakChecksResultIsSaidPlainly(LeakVerdict verdict, string stateName, string said)
    {
        var f = Facts(Ntfs) with { TokenLeak = TokenLeakStatus.NeedsTest };
        var state = Enum.Parse<LeakState>(stateName);
        Assert.Equal(state, ImpactRows.Leak(f, guardOn: false, now: verdict, checking: false, notRun: false));
        Assert.Equal(said, ImpactRows.From(f, state)[4].Detail);
    }

    [Fact]
    public void WhileTheCheckRunsTheRowSaysSoAndACheckThatCouldNotRunSaysThat()
    {
        var needs = Facts(Ntfs) with { TokenLeak = TokenLeakStatus.NeedsTest };
        Assert.Equal(LeakState.Checking, ImpactRows.Leak(needs, false, null, checking: true, notRun: false));
        var checking = ImpactRows.From(needs, LeakState.Checking)[4];
        Assert.Equal(((ImpactLevel?)null, (string?)null), (checking.Level, checking.Under));
        // Its usual time, never a promise: a helper that hangs can take far longer.
        Assert.Equal("Checking this Windows build for it (usually about 20 seconds)...", checking.Detail);
        Assert.Contains("(usually about 20 seconds)", FixRows.GuardChecking);
        // The CI UI test never runs the check, and a check that could not run is not a verdict.
        Assert.Equal(LeakState.NotChecked, ImpactRows.Leak(needs, false, null, checking: false, notRun: true));
        Assert.StartsWith("PoolWatch could not check for it now.", ImpactRows.From(needs, LeakState.NotChecked)[4].Detail);
        Assert.True(ImpactRows.NeedsCheck(needs, guardOn: false, now: null));
        Assert.False(ImpactRows.NeedsCheck(needs, guardOn: true, now: null));
        Assert.False(ImpactRows.NeedsCheck(needs, guardOn: false, now: LeakVerdict.Clean));
        // The guard on: the leak cannot build up, whatever the facts.
        Assert.Equal(LeakState.GuardOn, ImpactRows.Leak(needs with { TokenLeak = TokenLeakStatus.Leaks }, true, null, false, false));
        Assert.Equal(LeakState.WorkedAround, ImpactRows.Leak(Facts() with { TokenLeak = TokenLeakStatus.WorkedAroundAlready }, false, null, false, false));
        Assert.Equal(LeakState.NotAffected, ImpactRows.Leak(Facts() with { TokenLeak = TokenLeakStatus.NotAffected }, false, null, false, false));
    }

    [Fact]
    public void TheDefenderRowNamesExactlyTheDrivesDefenderChecksFirst()
    {
        var oneUntrusted = Facts(Ntfs, DevDrive('D'), DevDrive('E', trusted: false)) with { DefenderRealTimeOn = true, DefenderPerformanceModeOn = true };
        var row = ImpactRows.From(oneUntrusted, LeakState.NotFound)[1];
        Assert.Equal(("Defender scanning your Dev Drives", "E: is not trusted, so performance mode does not cover it.", ImpactLevel.Medium),
            (row.Title, row.Detail, row.Level!.Value));
        Assert.Equal([@"E:\"], ImpactRows.ScannedFirst(oneUntrusted));

        var noDevDrive = Facts(Ntfs) with { DefenderRealTimeOn = true };
        row = ImpactRows.From(noDevDrive, LeakState.NotFound)[1];
        Assert.Equal(("Defender checking your files", ImpactLevel.Low, (SetupStep?)null), (row.Title, row.Level!.Value, row.FixStep)); // nothing for setup to set

        row = ImpactRows.From(Facts(Ntfs, DevDrive('D')), LeakState.NotFound)[1];                                                    // not read: no claim
        Assert.Equal((ImpactLevel.None, "PoolWatch could not read Defender's settings. Another antivirus may be in use."), (row.Level!.Value, row.Detail));
        // Covered only when that was read: performance mode on, and each Dev Drive trusted.
        var covered = Facts(DevDrive('D')) with { DefenderRealTimeOn = true, DefenderPerformanceModeOn = true };
        Assert.Equal("Performance mode covers your Dev Drives.", ImpactRows.From(covered, LeakState.NotFound)[1].Detail);
        row = ImpactRows.From(covered with { DefenderPerformanceModeOn = null }, LeakState.NotFound)[1];
        Assert.Equal((ImpactLevel.None, "PoolWatch could not read whether performance mode covers your Dev Drives."), (row.Level!.Value, row.Detail));
        row = ImpactRows.From(Facts(DevDrive('D', trusted: null)) with { DefenderRealTimeOn = true, DefenderPerformanceModeOn = true }, LeakState.NotFound)[1];
        Assert.Equal("PoolWatch could not read whether performance mode covers your Dev Drives.", row.Detail);
    }

    [Fact]
    public void AReFsDriveThatIsNotADevDriveIsNamedAsReFs()
    {
        var row = ImpactRows.From(Facts(new VolumeFact(@"G:\", "ReFS", false, null)), LeakState.NotFound)[0];
        Assert.Equal(("G: uses ReFS, and Windows rarely empties its cache.", ImpactLevel.High), (row.Detail, row.Level!.Value));
        Assert.Equal("D: is a Dev Drive, and Windows rarely empties its cache.", ImpactRows.From(Facts(DevDrive('D')), LeakState.NotFound)[0].Detail);
    }

    [Fact]
    public void APcThatCouldNotBeReadHasNoRowsAndNoFixes()
    {
        Assert.Empty(ImpactRows.From(null, LeakState.NotChecked));
        Assert.Empty(FixRows.From(null, LeakState.NotChecked, false, true));
    }

    // ------------------------------------------------------------------ the standby cache's four choices

    [Fact]
    public void EachChoiceWritesTheTwoSettingsWithZeroForARuleThatIsOff()
    {
        Assert.Equal((true, 2.0, 0.0), Writes(CacheChoice.Schedule, 2, 6));
        Assert.Equal((true, 0.0, 6.0), Writes(CacheChoice.PoolSize, 2, 6));
        Assert.Equal((true, 2.0, 6.0), Writes(CacheChoice.Either, 2, 6));
        Assert.Equal((false, (double?)null, (double?)null), CacheChoices.Writes(CacheChoice.Later, 2, 6));
        foreach (var choice in new[] { CacheChoice.Schedule, CacheChoice.PoolSize, CacheChoice.Either })
        {
            var (enabled, every, size) = CacheChoices.Writes(choice, 2, 6);
            var s = new Settings { AutoClearEnabled = enabled, AutoClearEveryHours = every!.Value, AutoClearPoolGb = size!.Value };
            Assert.Equal(choice, CacheChoices.Read(s).Choice);  // read back as it was chosen
            Assert.True(CacheChoices.InEffect(s, choice, 2, 6));
        }
        // A rule that is off keeps the field's usual number, so the other choices show one.
        Assert.Equal((CacheChoice.PoolSize, 1.0, 6.0), CacheChoices.Read(new Settings { AutoClearEnabled = true, AutoClearEveryHours = 0, AutoClearPoolGb = 6 }));
        Assert.Equal(CacheChoice.Later, CacheChoices.Read(new Settings()).Choice);

        static (bool, double, double) Writes(CacheChoice c, double h, double g)
        {
            var (e, every, size) = CacheChoices.Writes(c, h, g);
            return (e, every!.Value, size!.Value);
        }
    }

    [Fact]
    public void WhicheverComesFirstIsRecommendedOnEveryPcAndTheStepOpensWithWhatIsInEffect()
    {
        Assert.Equal(CacheChoice.Either, CacheChoices.Recommended);
        // The step opens with what is in effect; on the first run with clearing off, the recommendation; opened again with
        // it off, "Decide later" (what is in effect, so Next changes nothing unless you choose).
        var on = new Settings { AutoClearEnabled = true, AutoClearEveryHours = 3, AutoClearPoolGb = 0 };
        Assert.Equal(CacheChoice.Schedule, CacheChoices.Initial(on, firstRun: true));
        Assert.Equal(CacheChoice.Either, CacheChoices.Initial(new Settings(), firstRun: true));
        Assert.Equal(CacheChoice.Later, CacheChoices.Initial(new Settings(), firstRun: false));
    }

    [Fact]
    public void TheReadyStepSaysHowTheCacheIsEmptied()
    {
        Assert.Equal("Emptied every hour, or at 8 GB", CacheChoices.Summary(new Settings { AutoClearEnabled = true }));
        Assert.Equal("Emptied every 2 hours", CacheChoices.Summary(new Settings { AutoClearEnabled = true, AutoClearEveryHours = 2, AutoClearPoolGb = 0 }));
        Assert.Equal("Emptied at 6 GB", CacheChoices.Summary(new Settings { AutoClearEnabled = true, AutoClearEveryHours = 0, AutoClearPoolGb = 6 }));
        Assert.Equal("PoolWatch suggests when", CacheChoices.Summary(new Settings()));
        Assert.Equal(["1 hour", "2 hours", "8 GB"], new[] { SetupText.Hours(1), SetupText.Hours(2), SetupText.Gb(8) });
        // A number set in Settings that the list does not have is offered too.
        Assert.Contains(5.0, CacheChoices.Options(CacheChoices.HourOptions, 5));
        Assert.Equal(CacheChoices.GbOptions, CacheChoices.Options(CacheChoices.GbOptions, 8));
    }

    private static StandbyCacheSetup Cache(FakeSystem system, LiveSettings live, bool smoke = false) => new(system, live, smoke);

    [Fact]
    public async Task AChoiceTurnsClearingOnWithItsRulesAndIsListedAsBeforeUnderTheChanges()
    {
        var system = new FakeSystem();
        var live = _f.Live();
        var (ok, message) = await Cache(system, live).Apply(CacheChoice.Either, 2, 6, replaceOwnTask: false);
        Assert.True(ok, message);
        Assert.Equal((true, 2.0, 6.0), (live.Running.AutoClearEnabled, live.Running.AutoClearEveryHours, live.Running.AutoClearPoolGb));
        var saved = _f.Saved();
        Assert.Equal((true, 2.0, 6.0), (saved.AutoClearEnabled, saved.AutoClearEveryHours, saved.AutoClearPoolGb));
        Assert.Equal("Automatic cache clearing turned on, every 2 hours, or when the kernel pool reaches 6 GB (from setup).", Assert.Single(system.TimelineLines));
        // The change list reads the running settings, as for every other way of turning it on.
        var row = Assert.Single(new SettingsSource(live, _f.Records, null).List());
        Assert.Equal((ChangeKind.AutoClear, ChangeWords.AutoClear(2, 6), true), (row.Kind, row.What, row.CanUndo));

        // The same choice again changes nothing.
        Assert.Equal((true, ""), await Cache(system, live).Apply(CacheChoice.Either, 2, 6, replaceOwnTask: false));
        Assert.Single(system.TimelineLines);
    }

    [Fact]
    public async Task DecidingLaterLeavesClearingOffAndTurnsItOffOnlyWhenItWasOn()
    {
        var system = new FakeSystem();
        var live = _f.Live();
        Assert.Equal((true, ""), await Cache(system, live).Apply(CacheChoice.Later, 1, 8, replaceOwnTask: false));
        Assert.Empty(system.TimelineLines);
        Assert.False(File.Exists(_f.Paths.SettingsFile));

        live.Apply("on", s => s.AutoClearEnabled = true);
        var (ok, _) = await Cache(system, live).Apply(CacheChoice.Later, 1, 8, replaceOwnTask: false);
        Assert.True(ok);
        Assert.False(live.Running.AutoClearEnabled);
        Assert.False(_f.Saved().AutoClearEnabled);
    }

    [Fact]
    public async Task WhereTheStepSaidSoARuleReplacesYourOwnTaskFirstAndNothingChangesWhenThatFails()
    {
        var live = _f.Live();
        var system = new FakeSystem { ReplaceError = "Your clear task could not be removed: access denied", Live = live };
        var (ok, message) = await Cache(system, live).Apply(CacheChoice.PoolSize, 1, 6, replaceOwnTask: true);
        Assert.Equal((false, "Your clear task could not be removed: access denied"), (ok, message));
        Assert.Equal(["replace own task", "refresh clear status", "rescan"], system.Calls); // read again either way
        Assert.False(live.Running.AutoClearEnabled);
        Assert.Equal([(false, 1.0, 8.0)], system.RulesAtRefresh);                          // nothing turned on
        Assert.Empty(system.TimelineLines);

        system.Calls.Clear();
        system.RulesAtRefresh.Clear();
        system.ReplaceError = null;
        (ok, _) = await Cache(system, live).Apply(CacheChoice.PoolSize, 1, 6, replaceOwnTask: true);
        Assert.True(ok);
        Assert.Equal(["replace own task", "refresh clear status", "rescan"], system.Calls);
        // Replacing turns the hourly clear on, and PoolWatch's own clear waits for your task until the status is read
        // again: by then the rule chosen is in place, so no clear runs on the hourly rule nobody chose.
        Assert.Equal([(true, 0.0, 6.0)], system.RulesAtRefresh);
        // The rule chosen is what stays, and the timeline names no hourly clear.
        Assert.Equal((true, 0.0, 6.0), (live.Running.AutoClearEnabled, live.Running.AutoClearEveryHours, live.Running.AutoClearPoolGb));
        Assert.Equal(["Replaced your own clear task with PoolWatch's automatic cache clear (from setup).",
            "Automatic cache clearing turned on, when the kernel pool reaches 6 GB (from setup)."], system.TimelineLines);
    }

    [Fact]
    public async Task WhatTheStepShowedDecidesTheReplaceAndARuleInEffectStillReplacesYourTask()
    {
        // The hourly check read a task of your own that the step's scan did not show: the step said nothing, so nothing is removed.
        var system = new FakeSystem { OwnClearTask = true };
        var live = _f.Live();
        Assert.True((await Cache(system, live).Apply(CacheChoice.Either, 1, 8, replaceOwnTask: false)).Ok);
        Assert.DoesNotContain("replace own task", system.Calls);

        // The step said a rule replaces your task, and that rule is already in effect: the task is still replaced.
        system.Calls.Clear();
        system.TimelineLines.Clear();
        var (ok, message) = await Cache(system, live).Apply(CacheChoice.Either, 1, 8, replaceOwnTask: true);
        Assert.True(ok, message);
        Assert.Contains("replace own task", system.Calls);
        Assert.Equal((true, 1.0, 8.0), (live.Running.AutoClearEnabled, live.Running.AutoClearEveryHours, live.Running.AutoClearPoolGb));
        // Deciding later replaces nothing (the step shows no note for it).
        system.Calls.Clear();
        Assert.True((await Cache(system, live).Apply(CacheChoice.Later, 1, 8, replaceOwnTask: false)).Ok);
        Assert.DoesNotContain("replace own task", system.Calls);
    }

    [Fact]
    public async Task ClearingNeedsAdministratorAndTheTestRunChangesNothing()
    {
        var live = _f.Live();
        var (ok, message) = await Cache(new FakeSystem { Elevated = false }, live).Apply(CacheChoice.Schedule, 1, 8, replaceOwnTask: false);
        Assert.Equal((false, "Clearing the cache needs PoolWatch to run as administrator."), (ok, message));
        Assert.Equal((false, RecommendationActions.Refused), await Cache(new FakeSystem(), live, smoke: true).Apply(CacheChoice.Schedule, 1, 8, replaceOwnTask: false));
        Assert.False(live.Running.AutoClearEnabled);
        Assert.False(File.Exists(_f.Paths.SettingsFile));

        Directory.CreateDirectory(_f.Paths.SettingsFile + ".tmp"); // settings.json cannot be written
        (ok, message) = await Cache(new FakeSystem(), live).Apply(CacheChoice.Schedule, 1, 8, replaceOwnTask: false);
        Assert.False(ok);
        Assert.StartsWith("That did not work: ", message);
        Assert.False(live.Running.AutoClearEnabled);
    }

    // ------------------------------------------------------------------ the Fixes step

    [Fact]
    public void TheFixesStepOffersWhatThisPcNeeds()
    {
        var rows = FixRows.From(Busy(), LeakState.Found, shortcutMade: false, launcherInstalled: true);
        Assert.Equal([Fix.SignIn, Fix.Defender, Fix.Shortcut, Fix.TokenGuard], rows.Select(r => r.Fix));
        Assert.Equal([FixInput.Toggle, FixInput.Button, FixInput.YesNo, FixInput.Toggle], rows.Select(r => r.Input));
        var defender = rows[1];
        Assert.Equal("Defender performance mode for E: and F:", defender.Title);
        Assert.Contains("fsutil devdrv query E:", defender.Instructions);          // a how-to only: PoolWatch never changes Defender
        var shortcut = rows[2];
        Assert.Equal(("Create a Codex shortcut without its shader cache?", "PoolWatch doesn't change the Codex app. The shortcut just starts it without that cache."),
            (shortcut.Title, shortcut.Detail));
        var guard = rows[3];
        Assert.Equal(FixRows.GuardWhy, guard.Why); // what you will notice, before you agree
        Assert.True(guard.WhyOpen);
        Assert.Equal([SetupLinks.TokenLeak, SetupLinks.LogonFix, SetupLinks.ForegroundLock], guard.Links);

        // Already set up: a status, nothing to apply.
        var done = FixRows.From(Busy() with { LogonTaskExists = true }, LeakState.GuardOn, shortcutMade: true, launcherInstalled: true, leakFound: true);
        Assert.Equal([(Fix.SignIn, "On"), (Fix.Shortcut, "On your desktop"), (Fix.TokenGuard, "On")],
            done.Where(r => r.Input == FixInput.Status).Select(r => (r.Fix, r.Status!)));
        // Not found: no guard to offer (PoolWatch keeps watching). Not installed with the installer: said so, no switch.
        var plain = FixRows.From(Busy() with { InstalledInProgramFiles = false }, LeakState.NotFound, false, launcherInstalled: false);
        Assert.DoesNotContain(plain, r => r.Fix == Fix.TokenGuard);
        Assert.All(plain.Where(r => r.Fix is Fix.SignIn or Fix.Shortcut), r => Assert.Equal(FixInput.None, r.Input));
        Assert.DoesNotContain(FixRows.From(Busy() with { ChatGptInstalled = false }, LeakState.NotFound, false, true), r => r.Fix == Fix.Shortcut);
    }

    [Fact]
    public void AGuardThatIsOnSaysWhatYouWillNoticeAndClaimsATestOnlyWhereACheckFoundTheLeak()
    {
        IReadOnlyList<SetupLink> links = [SetupLinks.TokenLeak, SetupLinks.LogonFix, SetupLinks.ForegroundLock];
        // The guard's reason says PoolWatch tested this PC and found the leak: only where a check did.
        var found = FixRows.From(Busy(), LeakState.GuardOn, false, true, leakFound: true).Single(r => r.Fix == Fix.TokenGuard);
        Assert.Equal((FixInput.Status, FixRows.GuardWhy), (found.Input, found.Why));
        Assert.Contains("tested this PC and found the leak", found.Why);
        Assert.Equal(links, found.Links);
        // Turned on for another reason (the leak seen building up, a Windows update since the check): the same "Why?",
        // what you will notice, how to undo it and the pages about it, without the claim of a test.
        var other = FixRows.From(Busy(), LeakState.GuardOn, false, true, leakFound: false).Single(r => r.Fix == Fix.TokenGuard);
        Assert.Equal((FixInput.Status, FixRows.GuardWhyUntested), (other.Input, other.Why));
        Assert.Equal(links, other.Links);
        // The guard's own words, its reason and the sentence that claims a test kept apart (no text cut off another's).
        Assert.Equal(TokenGuardWords.Reason + " " + TokenGuardWords.Tested, TokenGuardWords.Why);
        Assert.Equal("PoolWatch tested this PC and found the leak.", TokenGuardWords.Tested);
        Assert.Equal(TokenGuardWords.Reason + Environment.NewLine + Environment.NewLine + TokenGuardWords.Details, other.Why);
        Assert.DoesNotContain(TokenGuardWords.Tested, other.Why);
        Assert.StartsWith("On some Windows 11 versions, a Windows bug leaks a little of ", other.Why);
        Assert.Contains("What you'll notice: ", other.Why);
        Assert.Contains("Undo: ", other.Why);
        Assert.DoesNotContain("tested", other.Why);
        Assert.DoesNotContain("tested", other.Detail);
    }

    [Fact]
    public async Task TheSessionKnowsWhetherACheckFoundTheLeak()
    {
        var on = new FakeHost(Busy() with { TokenLeak = TokenLeakStatus.WorkedAroundAlready }); // the guard's own workaround, no verdict saved
        on.Running.TokenGuardEnabled = true;
        var session = await Session(on.Scanned!, on);
        Assert.False(session.LeakFound);
        Assert.Equal(FixRows.GuardWhyUntested, session.Fixes.Single(r => r.Fix == Fix.TokenGuard).Why);

        var saved = new FakeHost(Busy());                                                       // the verdict saved for this build: the leak
        saved.Running.TokenGuardEnabled = true;
        session = await Session(saved.Scanned!, saved);
        Assert.True(session.LeakFound);
        Assert.Equal(FixRows.GuardWhy, session.Fixes.Single(r => r.Fix == Fix.TokenGuard).Why);
    }

    [Fact]
    public void ALinkIsALinkOnlyWhilePoolWatchMayOpenThatPage()
    {
        // Every page setup names may be opened: the credits' pages and the reference pages PoolWatch opens.
        Assert.All(SetupLinks.All, l => Assert.True(l.Opens, l.Url));
        Assert.All(SetupLinks.All, l => Assert.Equal(Credits.IsAllowedLink(l.Url), l.Opens));
        foreach (var link in new[] { SetupLinks.ForegroundLock, SetupLinks.ReFsMemory, SetupLinks.DevDriveReport })
            Assert.Contains(link.Url, Credits.ReferencePages);
        Assert.All(SetupLinks.All, l => Assert.StartsWith("https://", l.Url));
        // The credited pages are the credits' own addresses (written out, so a change to the credits never stops setup opening).
        foreach (var link in new[] { SetupLinks.TokenLeak, SetupLinks.LogonFix, SetupLinks.ShaderCache, SetupLinks.RamMap })
            Assert.Contains(Credits.All, c => c.Url == link.Url);
    }

    [Fact]
    public async Task NextAppliesTheStepsChoicesThroughTheSameButtonsAsTheSuggestions()
    {
        var host = new FakeHost(Busy());
        long now = 0;
        var session = new SetupSession(host, firstRun: true, advanced: false, clock: () => now);
        await session.LoadAsync(CancellationToken.None);
        await session.GoTo(SetupStep.StandbyCache);
        Assert.Equal(CacheChoice.Either, session.Cache);                       // the recommended one, with a Dev Drive
        session.SetHours(2);
        Assert.True(await session.NextAsync(null!));
        Assert.Equal(SetupStep.Fixes, session.Flow.Step);
        Assert.Contains("cache Either 2 8", host.Calls);

        host.Calls.Clear();
        now += NextWaitTicks;                                                  // the Fixes step on screen for its moment
        Assert.True(await session.NextAsync(null!));
        Assert.Equal(["run StartAtSignIn", "run ChatGptShortcut", "run TokenLeakGuard"], host.Calls);
        Assert.Equal(SetupStep.Services, session.Flow.Step);
        // Each row now shows what is in effect.
        Assert.All(session.Fixes.Where(r => r.Fix != Fix.Defender), r => Assert.Equal(FixInput.Status, r.Input));
        Assert.Equal(LeakState.GuardOn, session.Leak);
    }

    [Fact]
    public async Task ChoicesTurnedOffApplyNothing()
    {
        var host = new FakeHost(Busy());
        var session = new SetupSession(host, firstRun: true, advanced: false);
        await session.LoadAsync(CancellationToken.None);
        await session.GoTo(SetupStep.Fixes);
        session.Choose(Fix.SignIn, false);
        session.Choose(Fix.Shortcut, false);
        session.Choose(Fix.TokenGuard, false);
        Assert.True(await session.NextAsync(null!));
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("run", StringComparison.Ordinal));

        // The Defender row's button opens the how-to at once, through the suggestions' own button.
        await session.GoTo(SetupStep.Fixes);
        await session.Press(Fix.Defender, null!);
        Assert.Equal("run DefenderDevDrive", host.Calls[^1]);
        Assert.Contains("fsutil devdrv query E:", host.Instructions);
    }

    [Fact]
    public async Task AChangeThatDidNotWorkKeepsTheStepOpenAndNextAgainGoesOn()
    {
        var host = new FakeHost(Busy()) { CacheResult = (false, "Clearing the cache needs PoolWatch to run as administrator.") };
        long now = 0;
        var session = new SetupSession(host, firstRun: true, advanced: false, clock: () => now);
        await session.LoadAsync(CancellationToken.None);
        await session.GoTo(SetupStep.StandbyCache);
        Assert.False(await session.NextAsync(null!));
        Assert.Equal(SetupStep.StandbyCache, session.Flow.Step);
        Assert.Equal("Clearing the cache needs PoolWatch to run as administrator. " + SetupText.NextAgain, session.Error(SetupSession.CacheKey));
        Assert.Equal(SetupSession.NextWait, session.NextHeldFor);            // Next waits a moment, so its message is read
        Assert.False(await session.NextAsync(null!));                         // pressed again at once (a double-click): the step stays
        Assert.Equal(SetupStep.StandbyCache, session.Flow.Step);

        now += NextWaitTicks;                                                 // the message was on screen for the moment
        Assert.True(await session.NextAsync(null!));                         // again, the same choice: on without it
        Assert.Single(host.Calls, c => c.StartsWith("cache", StringComparison.Ordinal));

        await session.GoTo(SetupStep.StandbyCache);
        now += NextWaitTicks;                                                 // past the moment the Fixes step started
        session.ChooseCache(CacheChoice.Schedule);                            // another choice is tried again
        Assert.Null(session.Error(SetupSession.CacheKey));
        Assert.False(await session.NextAsync(null!));
        Assert.Equal(2, host.Calls.Count(c => c.StartsWith("cache", StringComparison.Ordinal)));

        var fixes = new FakeHost(Busy()) { RunResult = r => r.Id == RecommendationId.ChatGptShortcut ? (false, "A shortcut with that name is already on your desktop.") : (true, "") };
        var s2 = new SetupSession(fixes, firstRun: true, advanced: false, clock: () => now);
        await s2.LoadAsync(CancellationToken.None);
        await s2.GoTo(SetupStep.Fixes);
        Assert.False(await s2.NextAsync(null!));
        Assert.StartsWith("A shortcut with that name is already on your desktop.", s2.Error(SetupSession.Key(Fix.Shortcut)));
        fixes.Calls.Clear();
        Assert.False(await s2.NextAsync(null!));                              // pressed again at once: the step stays, nothing runs
        Assert.Empty(fixes.Calls);
        now += NextWaitTicks;
        Assert.True(await s2.NextAsync(null!));
        Assert.Empty(fixes.Calls);                                            // the ones that worked are not run twice
    }

    [Fact]
    public async Task TheScanThenTheLeakCheckEachUpdateTheirOwnPart()
    {
        var host = new FakeHost(Busy() with { TokenLeak = TokenLeakStatus.NeedsTest }) { CheckGives = LeakVerdict.Leak };
        var session = new SetupSession(host, firstRun: true, advanced: false);
        var parts = new List<SetupPart>();
        session.Changed += parts.Add;
        await session.LoadAsync(CancellationToken.None);
        Assert.Equal([SetupPart.Scan, SetupPart.Leak], parts);
        Assert.Equal(["scan", "check"], host.Calls);
        Assert.Equal(LeakState.Found, session.Leak);
        Assert.Contains(session.Fixes, r => r.Fix == Fix.TokenGuard && r.Input == FixInput.Toggle);

        // The CI UI test: no check, and the row says it could not check.
        var smoke = new FakeHost(Busy() with { TokenLeak = TokenLeakStatus.NeedsTest }) { Smoke = true };
        var s2 = new SetupSession(smoke, firstRun: true, advanced: false);
        await s2.LoadAsync(CancellationToken.None);
        Assert.Equal(["scan"], smoke.Calls);
        Assert.Equal(LeakState.NotChecked, s2.Leak);

        // A scan that fails leaves the facts PoolWatch had.
        var failing = new FakeHost(Busy()) { ScanError = new IOException("WMI is busy") };
        var s3 = new SetupSession(failing, firstRun: true, advanced: false);
        await s3.LoadAsync(CancellationToken.None);
        Assert.True(s3.Scanned);
        Assert.Equal(5, s3.Impact.Count);
    }

    [Fact]
    public async Task NextOnTheCacheAndFixesStepsWaitsForTheScan()
    {
        // A fresh install: nothing read yet. Steps 4 and 5 apply what the scan finds (your own clear task, the rows).
        var host = new FakeHost(Busy() with { OwnClearTasks = [@"\RAMMap clear"] }) { ScanGate = new() };
        long now = 0;
        var session = new SetupSession(host, firstRun: true, advanced: false, clock: () => now);
        var load = session.LoadAsync(CancellationToken.None);
        Assert.True(await session.NextAsync(null!));                         // Welcome, Your PC and Safety do not wait
        Assert.True(await session.NextAsync(null!));
        Assert.True(await session.NextAsync(null!));
        Assert.Equal(SetupStep.StandbyCache, session.Flow.Step);
        Assert.True(session.WaitsForScan);
        Assert.False(session.CanGoNext);
        Assert.False(await session.NextAsync(null!));                        // held: nothing applied, the step stays
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("cache", StringComparison.Ordinal));
        Assert.Equal(CacheChoice.Either, session.Cache);                      // the recommendation does not wait for the scan
        await session.GoTo(SetupStep.Fixes);
        Assert.False(await session.NextAsync(null!));
        Assert.Empty(session.Fixes);

        host.ScanGate.SetResult(host.Scanned!);                                // the scan lands
        await load;
        Assert.False(session.CanGoNext);                                      // its rows appeared as the step showed: Next waits a moment
        now += NextWaitTicks;
        Assert.True(session.CanGoNext);
        await session.GoTo(SetupStep.StandbyCache);
        Assert.Equal(CacheChoice.Either, session.Cache);                      // nothing moved under the user's eyes
        Assert.True(await session.NextAsync(null!));
        Assert.Contains("cache Either 1 8 replacing your task", host.Calls);  // as the step then said
        now += NextWaitTicks;                                                 // the Fixes step on screen for its moment
        Assert.True(await session.NextAsync(null!));                          // Fixes
        Assert.Equal(SetupStep.Services, session.Flow.Step);
    }

    [Fact]
    public async Task AScanThatDoesNotEndStopsHoldingSetupAfterTheWait()
    {
        // The first run cannot be closed: a read that hangs must not keep it from finishing. Setup goes on with what PoolWatch had.
        var cached = Facts(Ntfs) with { InstalledInProgramFiles = true };
        var host = new FakeHost(cached) { Scanned = Busy(), ScanGate = new() };
        long now = 0;
        var session = new SetupSession(host, firstRun: true, advanced: false, scanWait: TimeSpan.FromMilliseconds(50), clock: () => now);
        await session.GoTo(SetupStep.StandbyCache);
        await session.LoadAsync(CancellationToken.None);
        Assert.True(session.Scanned);
        Assert.Same(cached, session.Facts);
        Assert.Equal(SetupSession.NextWait, session.NextHeldFor);             // what it goes on with is shown for a moment first
        now += NextWaitTicks;
        Assert.True(session.CanGoNext);
        Assert.True(await session.NextAsync(null!));

        // Nothing read at all (a first start): steps 2 and 5 say so, and setup still goes on.
        var none = new FakeHost(Busy()) { ScanGate = new() };
        none.Facts = null;
        var s2 = new SetupSession(none, firstRun: true, advanced: false, scanWait: TimeSpan.FromMilliseconds(50));
        await s2.LoadAsync(CancellationToken.None);
        await s2.GoTo(SetupStep.Fixes);
        Assert.Equal((true, 0, 0), (s2.Scanned, s2.Impact.Count, s2.Fixes.Count));
        Assert.True(await s2.NextAsync(null!));
    }

    [Fact]
    public async Task AReadThatHangsHoldsNoChangeAfterTheWaitAndItsScanIsShownWhenItEnds()
    {
        // The real scan, and the suggestions' real buttons over a PC that reads the same way as Windows' (each change reads
        // the PC again afterwards: your own task replaced, the sign-in task, the shortcut's icon), over reads where one hangs.
        var limit = TimeSpan.FromMilliseconds(300);
        var reads = new HeldReads();
        var probe = new EnvironmentProbe(_f.Paths, _f.Log, reads);
        var cached = await probe.ScanAsync();                 // an earlier start read the PC: what setup goes on with
        reads.Gate.Reset();                                   // from now on one read hangs
        var system = new ProbeSystem(probe, limit);
        var live = _f.Live();
        var actions = new RecommendationActions(system, live, new NoShell(), new RecommendationStore(_f.Paths, _f.Log), smoke: false);
        long now = 0;
        var session = new SetupSession(new PartsHost(probe, live, actions, new StandbyCacheSetup(system, live, smoke: false)), firstRun: true, advanced: false,
            scanWait: limit, clock: () => now);
        try
        {
            await session.LoadAsync(CancellationToken.None);
            Assert.Same(cached, session.Facts);
            await session.GoTo(SetupStep.StandbyCache);
            session.ChooseCache(CacheChoice.PoolSize);        // a rule: your own task is replaced, then the PC is read again
            Assert.True(await Within(session.NextAsync(null!)), "Next on the Standby cache step waited for the hung read");
            now += NextWaitTicks;                             // the Fixes step on screen for its moment
            Assert.True(await Within(session.NextAsync(null!)), "Next on the Fixes step waited for the hung read");
            Assert.Equal(SetupStep.Services, session.Flow.Step);
            Assert.Equal(["replace own task", "refresh clear status", "register the sign-in task", "create the shortcut"], system.Calls);
            Assert.Equal((true, 0.0, 8.0), (live.Running.AutoClearEnabled, live.Running.AutoClearEveryHours, live.Running.AutoClearPoolGb));
        }
        finally { reads.Gate.Set(); }

        // The read ends: what it found is shown.
        Assert.True(await Task.WhenAny(session.LateScan, Task.Delay(TimeSpan.FromSeconds(10))) == session.LateScan, "the late scan was not taken");
        Assert.NotSame(cached, session.Facts);
        Assert.Same(probe.Last, session.Facts);
    }

    /// <summary>Next ended within 10 seconds (a hung read is held 30), and went on.</summary>
    private static async Task<bool> Within(Task<bool> next) => await Task.WhenAny(next, Task.Delay(TimeSpan.FromSeconds(10))) == next && await next;

    [Fact]
    public async Task AScanThatEndsAfterTheWaitIsShownThenAndChecksTheLeakWhereItNeedsTo()
    {
        var cached = Facts(Ntfs) with { InstalledInProgramFiles = true, TokenLeak = TokenLeakStatus.NotAffected };
        var fresh = Busy() with { TokenLeak = TokenLeakStatus.NeedsTest };
        var host = new FakeHost(cached) { Scanned = fresh, ScanGate = new(), CheckGives = LeakVerdict.Leak };
        var session = new SetupSession(host, firstRun: true, advanced: false, scanWait: TimeSpan.FromMilliseconds(50));
        var parts = new List<SetupPart>();
        session.Changed += parts.Add;
        await session.LoadAsync(CancellationToken.None);
        Assert.Same(cached, session.Facts);
        Assert.Equal(["scan"], host.Calls);                   // what PoolWatch had needs no check
        Assert.DoesNotContain(session.Fixes, r => r.Fix == Fix.Defender);

        host.Facts = fresh;                                   // the scan ends (PoolWatch keeps it first, then hands it back)
        host.ScanGate.SetResult(fresh);
        await session.LateScan;
        Assert.Same(fresh, session.Facts);                    // every step shows it
        Assert.Contains(session.Fixes, r => r.Fix == Fix.Defender);
        Assert.Equal(["scan", "check"], host.Calls);          // and this build's check runs now, once
        Assert.Equal(LeakState.Found, session.Leak);
        Assert.Equal([SetupPart.Scan, SetupPart.Scan, SetupPart.Leak], parts);

        // A check already run (here it could not give an answer) does not run again when the scan ends.
        var once = new FakeHost(cached with { TokenLeak = TokenLeakStatus.NeedsTest }) { Scanned = fresh, ScanGate = new() };
        var s2 = new SetupSession(once, firstRun: true, advanced: false, scanWait: TimeSpan.FromMilliseconds(50));
        await s2.LoadAsync(CancellationToken.None);
        once.Facts = fresh;
        once.ScanGate.SetResult(fresh);
        await s2.LateScan;
        Assert.Same(fresh, s2.Facts);
        Assert.Equal(["scan", "check"], once.Calls);
        Assert.Equal(LeakState.NotChecked, s2.Leak);
    }

    [Fact]
    public async Task AScanThatEndsWhileAChangeRunsIsShownOnceTheChangeHasEnded()
    {
        var cached = Facts(Ntfs) with { InstalledInProgramFiles = true };
        var landed = Busy();
        var newest = landed with { LogonTaskExists = true };
        var host = new FakeHost(cached) { Scanned = landed, ScanGate = new(), CacheGate = new() };
        var session = new SetupSession(host, firstRun: true, advanced: false, scanWait: TimeSpan.FromMilliseconds(50));
        await session.LoadAsync(CancellationToken.None);
        await session.GoTo(SetupStep.StandbyCache);
        var next = session.NextAsync(null!);                  // the change runs
        Assert.True(session.Busy);
        host.Facts = landed;
        host.ScanGate.SetResult(landed);
        await session.LateScan;
        Assert.Same(cached, session.Facts);                   // held: the change applies what the step showed
        host.Facts = newest;                                  // and a change read the PC again since
        host.CacheGate.SetResult();
        Assert.True(await next);
        await session.LateScan;
        Assert.Same(newest, session.Facts);                   // then shown: the newest PoolWatch has
        Assert.Equal(SetupStep.Fixes, session.Flow.Step);
        Assert.Equal(FixInput.Status, session.Fixes.Single(r => r.Fix == Fix.SignIn).Input);
    }

    [Fact]
    public async Task WhileTheLeakCheckRunsTheFixesStepSaysSoAndOffersTheGuardInPlaceWhenFound()
    {
        var host = new FakeHost(Busy() with { TokenLeak = TokenLeakStatus.NeedsTest }) { CheckGives = LeakVerdict.Leak, CheckGate = new() };
        var session = new SetupSession(host, firstRun: true, advanced: false);
        var (view, fonts) = View(session, 100, new Size(880, 586));
        using (view)
        using (fonts)
        {
            var load = session.LoadAsync(CancellationToken.None);
            await session.GoTo(SetupStep.Fixes);
            var fixes = (FixesPage)view.Pages[SetupStep.Fixes];
            var checking = fixes.RowsShown.Single(r => r.Fix == Fix.TokenGuard);
            Assert.Equal((FixRows.GuardTitle, FixRows.GuardChecking, FixInput.None), (checking.Title, checking.Detail, checking.Input));
            Assert.Equal(SetupText.CheckingNow, session.Summary.Single(r => r.Key == "Token leak").Value);
            var others = fixes.Ends.ToDictionary(kv => kv.Key, kv => kv.Value);
            Assert.Empty(view.LayoutProblems());

            host.CheckGate.SetResult();                                         // found: the guard is offered, in that row's place
            await load;
            var offer = fixes.RowsShown.Single(r => r.Fix == Fix.TokenGuard);
            Assert.Equal((FixInput.Toggle, true), (offer.Input, offer.WhyOpen));
            Assert.All(others, kv => Assert.Same(kv.Value, fixes.Ends[kv.Key])); // the other rows stay as they were
            // Found after the Fixes step was left: Ready says where to turn the guard on.
            Assert.Equal("Found; turn the guard on in the app", session.Summary.Single(r => r.Key == "Token leak").Value);
            Assert.Empty(view.LayoutProblems());
        }
    }

    /// <summary>
    /// A Fixes row that appears while the step shows starts on (the guard, once the token-leak check found the leak; the
    /// sign-in switch and the shortcut, once a scan setup stopped waiting for ends). Next pressed as it arrives must not
    /// turn it on unseen: Next waits a moment from then, and the window hears when it starts and when Next may be pressed.
    /// </summary>
    [Fact]
    public async Task ARowThatAppearsWhileTheFixesStepShowsIsNotTurnedOnByANextPressedAsItArrives()
    {
        long now = 0;
        var host = new FakeHost(Busy() with { TokenLeak = TokenLeakStatus.NeedsTest }) { CheckGives = LeakVerdict.Leak, CheckGate = new() };
        var session = new SetupSession(host, firstRun: true, advanced: false, clock: () => now);
        var load = session.LoadAsync(CancellationToken.None);
        await session.GoTo(SetupStep.Fixes);
        session.Choose(Fix.SignIn, false); // only the guard is in question here
        session.Choose(Fix.Shortcut, false);
        Assert.True(session.CanGoNext);      // the owner's own choices hold nothing
        var parts = new List<SetupPart>();
        session.Changed += parts.Add;

        host.CheckGate!.SetResult();         // found: the guard's switch appears in its row, on
        await load;
        Assert.Equal((FixInput.Toggle, true), (session.Fixes.Single(r => r.Fix == Fix.TokenGuard).Input, session.Guard));
        Assert.Equal([SetupPart.Leak, SetupPart.Next], parts);
        Assert.Equal(SetupSession.NextWait, session.NextHeldFor);
        Assert.False(session.CanGoNext);
        Assert.False(await session.NextAsync(null!));  // pressed as it arrived: nothing is turned on
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("run", StringComparison.Ordinal));
        Assert.Equal(SetupStep.Fixes, session.Flow.Step);

        now += NextWaitTicks;                          // the row was there for the moment
        Assert.Equal(TimeSpan.Zero, session.NextHeldFor);
        Assert.True(await session.NextAsync(null!));   // now Next applies it, as shown
        Assert.Equal(["run TokenLeakGuard"], host.Calls.Where(c => c.StartsWith("run", StringComparison.Ordinal)));

        // A scan setup stopped waiting for ends while the step shows: the sign-in switch and the shortcut appear, on.
        var late = new FakeHost(Busy() with { LogonTaskExists = true }) { ScanGate = new() };
        late.Facts = Busy() with { LogonTaskExists = true, ChatGptInstalled = false };
        var s2 = new SetupSession(late, firstRun: true, advanced: false, scanWait: TimeSpan.Zero, clock: () => now);
        await s2.LoadAsync(CancellationToken.None);
        await s2.GoTo(SetupStep.Fixes);
        Assert.DoesNotContain(s2.Fixes, r => r.Input is FixInput.Toggle or FixInput.YesNo && r.Fix is Fix.SignIn or Fix.Shortcut);
        Assert.True(s2.CanGoNext);
        late.Facts = Busy();
        late.ScanGate!.SetResult(Busy());
        await s2.LateScan;
        Assert.Contains(s2.Fixes, r => r.Fix == Fix.SignIn && r.Input == FixInput.Toggle);
        Assert.False(s2.CanGoNext);
        Assert.False(await s2.NextAsync(null!));
        Assert.DoesNotContain(late.Calls, c => c.StartsWith("run", StringComparison.Ordinal));
        now += NextWaitTicks;
        Assert.True(s2.CanGoNext);
    }

    /// <summary>Next's wait (<see cref="SetupSession.NextWait"/>) on the tests' clock.</summary>
    private static long NextWaitTicks => (long)(SetupSession.NextWait.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);

    private static IEnumerable<string> Runs(FakeHost host) => host.Calls.Where(c => c.StartsWith("run", StringComparison.Ordinal));
    private static IEnumerable<string> CacheCalls(FakeHost host) => host.Calls.Where(c => c.StartsWith("cache", StringComparison.Ordinal));

    /// <summary>
    /// Next keeps its place and takes the focus on every step, and Enter presses it, so the second click of a double-click
    /// on the step before, or a held Enter, lands on the Standby cache or Fixes step's Next as that step appears. Each applies
    /// what it shows by default (a rule that replaces your own RAMMap clear task; the sign-in task, the shortcut and the
    /// guard), so Next waits a moment once Next or Back brings either up. The session refuses it, so neither Enter nor a
    /// click that races the window's timer gets past, and the window shows it held.
    /// </summary>
    [Fact]
    public async Task ASecondPressMeantForTheStepBeforeAppliesNothingOnTheStandbyCacheOrFixesStep()
    {
        long now = 0;
        var host = new FakeHost(Busy() with { OwnClearTasks = [@"\RAMMap clear"] });
        var session = new SetupSession(host, firstRun: true, advanced: false, clock: () => now);
        var (view, fonts) = View(session, 100, new Size(880, 586));
        using (view)
        using (fonts)
        {
            await session.LoadAsync(CancellationToken.None);
            Assert.True(await session.NextAsync(null!));                       // Welcome and Your PC apply nothing: no wait
            Assert.True(await session.NextAsync(null!));
            Assert.True(view.NextButton.Enabled);
            Assert.True(await session.NextAsync(null!));                       // Safety's Next, the first press

            Assert.Equal((SetupStep.StandbyCache, SetupSession.NextWait), (session.Flow.Step, session.NextHeldFor));
            Assert.False(session.CanGoNext);
            Assert.False(view.NextButton.Enabled);                             // the window shows it held
            Assert.False(await session.NextAsync(null!));                      // the second press: nothing applied, the step stays
            Assert.Empty(CacheCalls(host));
            Assert.Equal(SetupStep.StandbyCache, session.Flow.Step);
            now += NextWaitTicks / 2;                                          // still within the moment
            Assert.False(await session.NextAsync(null!));
            Assert.Empty(CacheCalls(host));

            now += NextWaitTicks;                                              // on screen for its moment: Next applies as the step says
            Assert.True(await session.NextAsync(null!));
            Assert.Equal(["cache Either 1 8 replacing your task"], CacheCalls(host));

            Assert.Equal((SetupStep.Fixes, SetupSession.NextWait), (session.Flow.Step, session.NextHeldFor)); // brought up by that Next
            Assert.False(view.NextButton.Enabled);
            Assert.False(await session.NextAsync(null!));
            Assert.Empty(Runs(host));
            Assert.Equal(SetupStep.Fixes, session.Flow.Step);
            now += NextWaitTicks;
            Assert.True(await session.NextAsync(null!));
            Assert.Equal(["run StartAtSignIn", "run ChatGptShortcut", "run TokenLeakGuard"], Runs(host));
            Assert.Equal((SetupStep.Services, TimeSpan.Zero), (session.Flow.Step, session.NextHeldFor));
            Assert.True(view.NextButton.Enabled);                              // a step that applies nothing waits for nothing

            // Back brings a step up too (Enter on Back, then Enter again on the Next that takes the focus).
            Assert.True(session.Back());
            Assert.Equal((SetupStep.Fixes, SetupSession.NextWait), (session.Flow.Step, session.NextHeldFor));
            Assert.False(await session.NextAsync(null!));
            now += NextWaitTicks / 2;
            Assert.True(session.Back());                                       // a step brought up starts its own moment
            Assert.Equal((SetupStep.StandbyCache, SetupSession.NextWait), (session.Flow.Step, session.NextHeldFor));
            Assert.False(await session.NextAsync(null!));
            Assert.Single(CacheCalls(host));
            Assert.Equal(SetupStep.StandbyCache, session.Flow.Step);
        }
    }

    /// <summary>
    /// The token-leak check can end while a Fixes change runs (it goes on beside setup). That change applies the rows as
    /// they were shown, so the guard is not in it; when the change does not work the step stays, now with the guard's switch,
    /// on. Next then waits a moment from when the change ended, as for a row that appears while the step shows. A later
    /// change that does not work holds Next for its message, as every such change does; one that works starts no wait when
    /// it ends, for a row that appeared during an earlier change.
    /// </summary>
    [Fact]
    public async Task ARowThatAppearsWhileAFixesChangeRunsHoldsNextOnceTheChangeHasEnded()
    {
        long now = 0;
        var host = new FakeHost(Busy() with { TokenLeak = TokenLeakStatus.NeedsTest })
        {
            CheckGives = LeakVerdict.Leak, CheckGate = new(), RunGate = new(),
            RunResult = r => r.Id switch
            {
                RecommendationId.StartAtSignIn => (false, "PoolWatch could not set itself to start when you sign in."),
                RecommendationId.TokenLeakGuard => (false, "PoolWatch could not turn the guard on."),
                _ => (true, ""),
            },
        };
        var session = new SetupSession(host, firstRun: true, advanced: false, clock: () => now);
        var load = session.LoadAsync(CancellationToken.None);                  // read; the check runs
        await session.GoTo(SetupStep.Fixes);
        session.Choose(Fix.Shortcut, false);                                   // only the sign-in task runs
        Assert.Equal(FixInput.None, session.Fixes.Single(r => r.Fix == Fix.TokenGuard).Input); // "checking"
        Assert.True(session.CanGoNext);

        var next = session.NextAsync(null!);                                   // the sign-in task's change runs
        Assert.True(session.Busy);
        host.CheckGate!.SetResult();                                           // meanwhile the check finds the leak: the guard's switch, on
        await load;
        Assert.Equal((FixInput.Toggle, true), (session.Fixes.Single(r => r.Fix == Fix.TokenGuard).Input, session.Guard));
        var parts = new List<SetupPart>();
        session.Changed += parts.Add;
        host.RunGate!.SetResult();
        Assert.False(await next);                                              // the sign-in task did not work: the step stays
        Assert.Equal(["run StartAtSignIn"], Runs(host));                       // the guard was not shown when Next was pressed
        Assert.Equal(SetupStep.Fixes, session.Flow.Step);
        Assert.Contains(SetupPart.Next, parts);                                // the window hears that Next waits
        Assert.Equal(SetupSession.NextWait, session.NextHeldFor);
        Assert.False(session.CanGoNext);
        Assert.False(await session.NextAsync(null!));                          // pressed as Next came back: the guard stays off
        Assert.Equal(["run StartAtSignIn"], Runs(host));

        now += NextWaitTicks;                                                  // the guard's row was there for the moment
        Assert.False(await session.NextAsync(null!));                          // Next again: on without the sign-in task, with the guard,
        Assert.Equal(["run StartAtSignIn", "run TokenLeakGuard"], Runs(host)); // which does not work either: the step stays
        Assert.Equal(SetupStep.Fixes, session.Flow.Step);
        Assert.Equal(SetupSession.NextWait, session.NextHeldFor);              // nothing appeared while it ran, but its message holds Next
        Assert.False(await session.NextAsync(null!));
        Assert.Equal(["run StartAtSignIn", "run TokenLeakGuard"], Runs(host));

        now += NextWaitTicks;                                                  // the message was on screen for the moment
        parts.Clear();
        Assert.True(await session.NextAsync(null!));                           // and Next again goes on without it
        Assert.DoesNotContain(SetupPart.Next, parts);                          // a change that worked starts no wait: what appeared is long shown
        Assert.Equal(["run StartAtSignIn", "run TokenLeakGuard"], Runs(host));
        Assert.Equal(SetupStep.Services, session.Flow.Step);
    }

    /// <summary>
    /// The shortcut row reads Active changes as it is now, and putting the shortcut back there (opened from the tray while
    /// setup shows) tells setup nothing. Next applies only the switches and Yes/Nos the window showed: a shortcut put back
    /// is made again only once its Yes/No has been on screen for the moment.
    /// </summary>
    [Fact]
    public async Task AShortcutPutBackWhileSetupShowsIsNotMadeAgainUntilItsChoiceWasShown()
    {
        long now = 0;

        // Put back while the Fixes step shows "On your desktop": Next applies what the page showed, and the step goes on.
        var host = new FakeHost(Busy()) { ShortcutMade = true };
        var session = new SetupSession(host, firstRun: true, advanced: false, clock: () => now);
        var (view, fonts) = View(session, 100, new Size(880, 586));
        using (view)
        using (fonts)
        {
            await session.LoadAsync(CancellationToken.None);
            await session.GoTo(SetupStep.Fixes);
            session.Choose(Fix.SignIn, false);                                 // only the shortcut is in question here
            session.Choose(Fix.TokenGuard, false);
            var page = (FixesPage)view.Pages[SetupStep.Fixes];
            Assert.Equal(FixInput.Status, page.RowsShown.Single(r => r.Fix == Fix.Shortcut).Input);

            host.ShortcutMade = false;
            Assert.Equal((FixInput.YesNo, true), (session.Fixes.Single(r => r.Fix == Fix.Shortcut).Input, session.Shortcut)); // read now: Yes
            Assert.Equal(FixInput.Status, page.RowsShown.Single(r => r.Fix == Fix.Shortcut).Input);                         // not shown
            Assert.True(await session.NextAsync(null!));
            Assert.Empty(Runs(host));
            Assert.Equal(SetupStep.Services, session.Flow.Step);

            // Back on the step its Yes/No shows, and Next makes the shortcut once it has been on screen for the moment.
            Assert.True(session.Back());
            Assert.Equal(FixInput.YesNo, page.RowsShown.Single(r => r.Fix == Fix.Shortcut).Input);
            Assert.False(await session.NextAsync(null!));
            now += NextWaitTicks;
            Assert.True(await session.NextAsync(null!));
            Assert.Equal(["run ChatGptShortcut"], Runs(host));
        }

        // Put back while another step shows: the Fixes step shows the Yes/No as Back brings it up, before Next can apply it.
        var h2 = new FakeHost(Busy()) { ShortcutMade = true };
        var s2 = new SetupSession(h2, firstRun: true, advanced: false, clock: () => now);
        var (v2, f2) = View(s2, 100, new Size(880, 586));
        using (v2)
        using (f2)
        {
            await s2.LoadAsync(CancellationToken.None);
            await s2.GoTo(SetupStep.Fixes);
            s2.Choose(Fix.SignIn, false);
            s2.Choose(Fix.TokenGuard, false);
            Assert.True(await s2.NextAsync(null!));
            Assert.Equal(SetupStep.Services, s2.Flow.Step);
            h2.ShortcutMade = false;
            Assert.True(s2.Back());
            Assert.Equal(FixInput.YesNo, ((FixesPage)v2.Pages[SetupStep.Fixes]).RowsShown.Single(r => r.Fix == Fix.Shortcut).Input);
            now += NextWaitTicks;
            Assert.True(await s2.NextAsync(null!));
            Assert.Equal(["run ChatGptShortcut"], Runs(h2));
        }

        // Put back while the step shows, then a press on another row tells the page: the Yes/No appears with it, on, and
        // Next waits a moment as for any row that appears.
        var h3 = new FakeHost(Busy()) { ShortcutMade = true };
        var s3 = new SetupSession(h3, firstRun: true, advanced: false, clock: () => now);
        await s3.LoadAsync(CancellationToken.None);
        await s3.GoTo(SetupStep.Fixes);
        s3.Choose(Fix.TokenGuard, false);
        Assert.True(s3.CanGoNext);
        h3.ShortcutMade = false;
        s3.Choose(Fix.SignIn, false);
        Assert.Equal(SetupSession.NextWait, s3.NextHeldFor);
        Assert.False(await s3.NextAsync(null!));
        Assert.Empty(Runs(h3));
        now += NextWaitTicks;
        Assert.True(await s3.NextAsync(null!));
        Assert.Equal(["run ChatGptShortcut"], Runs(h3));
    }

    /// <summary>
    /// What the window was told of is what the Fixes page shows: the session tells it of the rows only with the notices
    /// the page shows them again on. A notice the page ignores (here the Standby cache step's choice) tells it nothing of a
    /// shortcut put back meanwhile, so while the page still says "On your desktop", Next does not make the shortcut.
    /// </summary>
    [Fact]
    public async Task TheRowsAreToldOnlyWithTheNoticesTheFixesPageShowsThemAgainOn()
    {
        long now = 0;
        var host = new FakeHost(Busy()) { ShortcutMade = true };
        var session = new SetupSession(host, firstRun: true, advanced: false, clock: () => now);
        var (view, fonts) = View(session, 100, new Size(880, 586));
        using (view)
        using (fonts)
        {
            await session.LoadAsync(CancellationToken.None);
            await session.GoTo(SetupStep.Fixes);
            session.Choose(Fix.SignIn, false);                                 // only the shortcut is in question here
            session.Choose(Fix.TokenGuard, false);
            var page = (FixesPage)view.Pages[SetupStep.Fixes];
            host.ShortcutMade = false;                                         // put back from Active changes: setup is told nothing
            session.ChooseCache(CacheChoice.Later);                            // a notice the Fixes page does not show its rows again on
            Assert.Equal(FixInput.Status, page.RowsShown.Single(r => r.Fix == Fix.Shortcut).Input); // still "On your desktop"
            Assert.Equal(TimeSpan.Zero, session.NextHeldFor);                  // so, for the window, nothing appeared either
            now += NextWaitTicks;
            Assert.True(await session.NextAsync(null!));
            Assert.Empty(Runs(host));                                          // and Next does not make it unseen
        }
    }

    /// <summary>
    /// The scan's facts can land while the Standby cache step shows, long after the step's own moment: on time, when the
    /// owner reached the step first and Next waited for the scan, or late, after setup had stopped waiting for it and Next
    /// was ready. With them can come the note that a rule replaces your own RAMMap clear task, which Next then does. Next
    /// waits a moment from then, with the keyboard focus on the page area, so a press meant for the Next that was waiting
    /// or ready removes no task the owner had no time to see. The Fixes step waits as well when the facts land on it,
    /// whether or not a switch or a Yes/No comes with them.
    /// </summary>
    [Fact]
    public async Task TheScanLandingWhileTheStandbyCacheStepShowsHoldsNextUntilWhatItFoundWasOnScreen()
    {
        long now = 0;

        // On time: Next waited for the scan, and the step's own moment was long over when it landed.
        var host = new FakeHost(Busy() with { OwnClearTasks = [@"\RAMMap clear"] }) { ScanGate = new() };
        var session = new SetupSession(host, firstRun: true, advanced: false, clock: () => now);
        var (view, fonts) = View(session, 100, new Size(880, 586));
        using (view)
        using (fonts)
        {
            var focus = new FocusFake(view);
            var page = (CachePage)view.Pages[SetupStep.StandbyCache];
            var load = session.LoadAsync(CancellationToken.None);
            for (int i = 0; i < 3; i++) Assert.True(await session.NextAsync(null!)); // Welcome, Your PC and Safety
            Assert.Equal((SetupStep.StandbyCache, true, false), (session.Flow.Step, session.WaitsForScan, page.OwnTaskShown));
            Assert.Same(view.Content, focus.On);                               // Next waits for the scan: the page area holds the focus
            now += 5 * NextWaitTicks;
            view.NextWaitOver();                                               // the step's own moment is over
            Assert.False(view.NextWaitRunning);

            host.ScanGate.SetResult(host.Scanned!);                            // the scan lands, and the note with it
            await load;
            Assert.True(page.OwnTaskShown);
            Assert.Equal(SetupSession.NextWait, session.NextHeldFor);
            Assert.Equal((false, true), (view.NextButton.Enabled, view.NextWaitRunning));
            Assert.Same(view.Content, focus.On);                               // still where Enter presses nothing
            Assert.False(await session.NextAsync(null!));                      // pressed as the note appeared: your task stays
            Assert.Empty(CacheCalls(host));

            now += NextWaitTicks;                                              // the note was on screen for the moment
            view.NextWaitOver();
            Assert.True(view.NextButton.Enabled);
            Assert.Same(view.NextButton, focus.On);                            // Next takes the focus back
            Assert.True(await session.NextAsync(null!));
            Assert.Equal(["cache Either 1 8 replacing your task"], CacheCalls(host));
        }

        // Late: setup went on with what PoolWatch had (no task of your own in it), and the scan lands while Next is ready.
        var landed = Busy() with { OwnClearTasks = [@"\RAMMap clear"] };
        var late = new FakeHost(Busy()) { Scanned = landed, ScanGate = new() };
        var s2 = new SetupSession(late, firstRun: true, advanced: false, scanWait: TimeSpan.Zero, clock: () => now);
        var (v2, f2) = View(s2, 100, new Size(880, 586));
        using (v2)
        using (f2)
        {
            var focus = new FocusFake(v2);
            var page = (CachePage)v2.Pages[SetupStep.StandbyCache];
            await s2.LoadAsync(CancellationToken.None);
            for (int i = 0; i < 3; i++) Assert.True(await s2.NextAsync(null!));
            now += NextWaitTicks;
            v2.NextWaitOver();                                                 // the step's own moment is over: Next is ready, with the focus
            Assert.Equal((false, true), (page.OwnTaskShown, v2.NextButton.Enabled));
            Assert.Same(v2.NextButton, focus.On);

            late.Facts = landed;                                               // the scan ends (PoolWatch keeps it first)
            late.ScanGate!.SetResult(landed);
            await s2.LateScan;
            Assert.True(page.OwnTaskShown);
            Assert.Equal(SetupSession.NextWait, s2.NextHeldFor);
            Assert.False(v2.NextButton.Enabled);
            Assert.Same(v2.Content, focus.On);                                 // taken from Next before it was disabled
            Assert.False(await s2.NextAsync(null!));
            Assert.Empty(CacheCalls(late));

            now += NextWaitTicks;
            v2.NextWaitOver();
            Assert.Same(v2.NextButton, focus.On);
            Assert.True(await s2.NextAsync(null!));
            Assert.Equal(["cache Either 1 8 replacing your task"], CacheCalls(late));
        }

        // On the Fixes step, facts that bring no new switch or Yes/No (here Defender's row goes) hold Next as well.
        var h3 = new FakeHost(Busy()) { Scanned = Busy() with { DefenderPerformanceModeOn = true }, ScanGate = new() };
        var s3 = new SetupSession(h3, firstRun: true, advanced: false, scanWait: TimeSpan.Zero, clock: () => now);
        await s3.LoadAsync(CancellationToken.None);
        await s3.GoTo(SetupStep.Fixes);
        Assert.Contains(s3.Fixes, r => r.Fix == Fix.Defender);
        Assert.True(s3.CanGoNext);
        h3.Facts = h3.Scanned;
        h3.ScanGate!.SetResult(h3.Scanned!);
        await s3.LateScan;
        Assert.DoesNotContain(s3.Fixes, r => r.Fix == Fix.Defender);
        Assert.Equal(SetupSession.NextWait, s3.NextHeldFor);
        Assert.False(await s3.NextAsync(null!));
        Assert.Empty(Runs(h3));
        now += NextWaitTicks;
        Assert.True(await s3.NextAsync(null!));
    }

    /// <summary>
    /// WinForms moves the keyboard focus on, to the next control, when the control that has it is disabled, and that can be
    /// a "Why?" link that takes Enter itself. So whenever Next is about to wait while it has the focus (a change runs, a
    /// Fixes row appears, or both), the page area takes the focus first, where Enter presses nothing, and Next takes it back
    /// once its wait is over, as when a step is brought up.
    /// </summary>
    [Fact]
    public async Task WhileNextWaitsThePageAreaHoldsTheKeyboardFocusAndNextTakesItBackAfter()
    {
        long now = 0;
        var host = new FakeHost(Busy() with { TokenLeak = TokenLeakStatus.NeedsTest })
        {
            CheckGives = LeakVerdict.Leak, CheckGate = new(), ShortcutMade = true,
            RunResult = r => r.Id == RecommendationId.StartAtSignIn ? (false, "PoolWatch could not set itself to start when you sign in.") : (true, ""),
        };
        var session = new SetupSession(host, firstRun: true, advanced: false, clock: () => now);
        var (view, fonts) = View(session, 100, new Size(880, 586));
        using (view)
        using (fonts)
        {
            var focus = new FocusFake(view);
            var load = session.LoadAsync(CancellationToken.None);              // read; the check runs
            await session.GoTo(SetupStep.Fixes);
            Assert.Same(view.NextButton, focus.On);

            // A row appears while the step shows: the guard's switch, once the check found the leak.
            host.CheckGate!.SetResult();
            await load;
            Assert.Equal((false, true), (view.NextButton.Enabled, view.NextWaitRunning));
            Assert.Same(view.Content, focus.On);
            now += NextWaitTicks;
            view.NextWaitOver();
            Assert.Same(view.NextButton, focus.On);

            // A change runs (the sign-in task, which does not work), and meanwhile the shortcut is put back from Active changes.
            session.Choose(Fix.TokenGuard, false);
            host.RunGate = new();
            var next = session.NextAsync(null!);
            Assert.Same(view.Content, focus.On);                               // Next waits for the change
            host.ShortcutMade = false;
            host.RunGate.SetResult();
            Assert.False(await next);
            Assert.Equal((false, true), (view.NextButton.Enabled, view.NextWaitRunning)); // its Yes/No appeared: then for a moment
            Assert.Same(view.Content, focus.On);
            now += NextWaitTicks;
            view.NextWaitOver();
            Assert.True(view.NextButton.Enabled);
            Assert.Same(view.NextButton, focus.On);
        }
    }

    /// <summary>
    /// The owner can move the keyboard focus off Next themselves (Tab to a link, a switch or a card). Next's waits leave it
    /// there: the page area takes the focus only from Next, and Next takes it back only from the page area, so a wait that
    /// ends never pulls the focus onto Next under the owner's next Enter.
    /// </summary>
    [Fact]
    public async Task AControlTheOwnerMovedTheFocusToKeepsItWhileNextWaitsAndAfter()
    {
        long now = 0;
        var host = new FakeHost(Busy() with { TokenLeak = TokenLeakStatus.NeedsTest }) { CheckGives = LeakVerdict.Leak, CheckGate = new() };
        var session = new SetupSession(host, firstRun: true, advanced: false, clock: () => now);
        var (view, fonts) = View(session, 100, new Size(880, 586));
        using (view)
        using (fonts)
        {
            var focus = new FocusFake(view);
            var load = session.LoadAsync(CancellationToken.None);              // read; the check runs
            await session.GoTo(SetupStep.Fixes);
            Assert.Same(view.NextButton, focus.On);
            focus.On = view.HowLink;                                           // the owner tabbed to "How PoolWatch works"

            host.CheckGate!.SetResult();                                       // a row appears: Next waits
            await load;
            Assert.Equal((false, true), (view.NextButton.Enabled, view.NextWaitRunning));
            Assert.Same(view.HowLink, focus.On);                               // left where the owner put it
            now += NextWaitTicks;
            view.NextWaitOver();
            Assert.True(view.NextButton.Enabled);
            Assert.Same(view.HowLink, focus.On);                               // not pulled onto Next: Enter there opens what it says
        }
    }

    /// <summary>
    /// Back brings a step up as Next does. On the Standby cache or Fixes step Next waits, so the page area takes the focus
    /// from Back (a second Enter there would go back again), and Next takes it once its wait is over. A screen reader names
    /// the page area by the step it shows, not only "pane".
    /// </summary>
    [Fact]
    public async Task BackOntoAStepThatAppliesChangesPutsTheFocusOnThePageAreaNamedByItsStep()
    {
        long now = 0;
        var session = new SetupSession(new FakeHost(Busy()), firstRun: true, advanced: false, clock: () => now);
        var (view, fonts) = View(session, 100, new Size(880, 586));
        using (view)
        using (fonts)
        {
            var focus = new FocusFake(view);
            Assert.Equal("Welcome", view.Content.AccessibleName);
            await session.LoadAsync(CancellationToken.None);
            await session.GoTo(SetupStep.Services);
            Assert.Equal("Services", view.Content.AccessibleName);
            focus.On = view.BackButton;                                        // Enter, or a click, on Back

            Assert.True(session.Back());
            Assert.Equal((SetupStep.Fixes, false), (session.Flow.Step, view.NextButton.Enabled));
            Assert.Same(view.Content, focus.On);                               // not left on Back
            Assert.Equal("Fixes", view.Content.AccessibleName);
            now += NextWaitTicks;
            view.NextWaitOver();
            Assert.Same(view.NextButton, focus.On);
        }
    }

    /// <summary>
    /// A change that does not work keeps its step open with its message, and Next pressed again goes on without it. So Next
    /// waits a moment from when the change ended, with the focus on the page area: a second press as quick as a double-click's
    /// second click, or a second Enter, does not go on past the message before it can be read.
    /// </summary>
    [Fact]
    public async Task AChangeThatDidNotWorkHoldsNextForAMomentSoItsMessageIsRead()
    {
        long now = 0;
        var host = new FakeHost(Busy()) { CacheResult = (false, "Clearing the cache needs PoolWatch to run as administrator."), CacheGate = new() };
        var session = new SetupSession(host, firstRun: true, advanced: false, clock: () => now);
        var (view, fonts) = View(session, 100, new Size(880, 586));
        using (view)
        using (fonts)
        {
            var focus = new FocusFake(view);
            await session.LoadAsync(CancellationToken.None);
            await session.GoTo(SetupStep.StandbyCache);
            Assert.Same(view.NextButton, focus.On);
            var next = session.NextAsync(null!);                               // the change runs
            Assert.Same(view.Content, focus.On);
            host.CacheGate!.SetResult();
            Assert.False(await next);                                          // it did not work: the step stays, with its message
            Assert.StartsWith("Clearing the cache needs PoolWatch to run as administrator.", session.Error(SetupSession.CacheKey));
            Assert.Equal(SetupSession.NextWait, session.NextHeldFor);
            Assert.Equal((false, true), (view.NextButton.Enabled, view.NextWaitRunning));
            Assert.Same(view.Content, focus.On);                               // where a second Enter presses nothing
            Assert.False(await session.NextAsync(null!));                      // a second click: refused, the step stays
            Assert.Equal(SetupStep.StandbyCache, session.Flow.Step);

            now += NextWaitTicks;                                              // the message was on screen for the moment
            view.NextWaitOver();
            Assert.True(view.NextButton.Enabled);
            Assert.Same(view.NextButton, focus.On);
            Assert.True(await session.NextAsync(null!));                       // now Next goes on without it
            Assert.Single(CacheCalls(host));
            Assert.Equal(SetupStep.Fixes, session.Flow.Step);
        }
    }

    /// <summary>
    /// Windows repeats a key held down, and Enter presses Next (the form's AcceptButton), so a held Enter would press Next
    /// again each time its wait ends and walk through setup. The form's key handling lets only a fresh press of Enter
    /// through. The window is never shown: its key handling is called as the message loop calls it for a key-down.
    /// </summary>
    [Fact]
    public void AHeldEnterActsOnceAndItsRepeatsAreSwallowed()
    {
        var processCmdKey = typeof(SetupWizardForm).GetMethod("ProcessCmdKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            [typeof(Message).MakeByRefType(), typeof(Keys)])!;
        using var form = new SetupWizardForm(new FakeHost(Busy()), firstRun: true, advanced: false);
        _ = form.Handle;
        const int keyDown = 0x100, keyUp = 0x101;
        const long fresh = 1, repeat = 1 | 1L << 30, letGo = 1 | 1L << 30 | 1L << 31; // lParam: count 1, the key was down, it goes up
        bool Swallowed(int message, Keys keys, long lParam) =>
            (bool)processCmdKey.Invoke(form, [Message.Create(form.View.NextButton.Handle, message, (nint)(keys & Keys.KeyCode), (nint)lParam), keys])!;

        Assert.Same(form.View.NextButton, form.AcceptButton);                 // Enter presses Next
        Assert.False(Swallowed(keyDown, Keys.Enter, fresh));                  // a fresh press goes on to it
        Assert.True(Swallowed(keyDown, Keys.Enter, repeat));                  // each repeat of a held Enter does not
        Assert.True(Swallowed(keyDown, Keys.Shift | Keys.Enter, repeat));     // Shift+Enter presses Next too
        Assert.False(Swallowed(keyUp, Keys.Enter, letGo));                    // letting go is no press
        Assert.False(Swallowed(keyDown, Keys.Tab, repeat));                   // every other key repeats as before
        Assert.False(Swallowed(keyDown, Keys.Down, repeat));
    }

    /// <summary>
    /// The repeats of a held Enter are swallowed whichever control has the focus: WinForms asks the focused control's
    /// parents, up to the form, before the control takes the key itself and before the default button is pressed. The
    /// key-downs go in as the message loop hands them to the page area, the footer's link and a Standby cache field, and a
    /// fresh press still reaches the default button (here one that counts its presses). The window is never shown.
    /// </summary>
    [Fact]
    public async Task AHeldEnterIsSwallowedWhicheverControlHasTheFocus()
    {
        using var form = new SetupWizardForm(new FakeHost(Busy()), firstRun: true, advanced: false);
        _ = form.Handle;
        await form.ShowStepForTest(SetupStep.StandbyCache);
        var box = ((CachePage)form.View.Pages[SetupStep.StandbyCache]).HourFields[0];
        using var accept = new PressCounter();
        form.AcceptButton = accept;
        const long fresh = 1, repeat = 1 | 1L << 30;                           // lParam: count 1; the key was down
        bool Taken(Control c, long lParam)
        {
            var msg = Message.Create(c.Handle, 0x100, (nint)Keys.Enter, (nint)lParam);
            return c.PreProcessMessage(ref msg);
        }

        // A repeat is taken, and never passed on to the default button (which would also take it), from:
        Assert.Equal((true, 0), (Taken(form.View.Content, repeat), accept.Presses)); // the page area, where the focus waits
        Assert.Equal((true, 0), (Taken(form.View.HowLink, repeat), accept.Presses)); // a link, which takes Enter itself
        Assert.Equal((true, 0), (Taken(box, repeat), accept.Presses));               // a field
        Assert.Equal((false, 0), (Taken(form.View.HowLink, fresh), accept.Presses)); // a fresh press: the link takes it itself
        Assert.Equal((true, 1), (Taken(form.View.Content, fresh), accept.Presses));  // from the page area it presses the default button
    }

    /// <summary>A default button that counts its presses (a real button does nothing when pressed on a window not shown).</summary>
    private sealed class PressCounter : Control, IButtonControl
    {
        public int Presses;
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public DialogResult DialogResult { get; set; }
        public void NotifyDefault(bool value) { }
        public void PerformClick() => Presses++;
    }

    [Fact]
    public async Task WhileNextAppliesAChoiceEveryChoiceIsHeld()
    {
        var host = new FakeHost(Busy()) { CacheGate = new() };
        var session = await Session(Busy(), host);
        var (view, fonts) = View(session, 100, new Size(880, 586));
        using (view)
        using (fonts)
        {
            await session.GoTo(SetupStep.StandbyCache);
            var page = (CachePage)view.Pages[SetupStep.StandbyCache];
            var next = session.NextAsync(null!);                               // the change runs
            Assert.True(session.Busy);
            Assert.False(view.NextButton.Enabled);
            Assert.All(page.Cards.Values, c => Assert.False(c.Enabled));
            Assert.All(page.HourFields.Concat(page.GbFields), f => Assert.False(f.Enabled));
            session.ChooseCache(CacheChoice.Schedule);                          // held, as a click would be
            session.SetHours(3);
            session.Choose(Fix.SignIn, false);
            Assert.Equal((CacheChoice.Either, 1.0, true), (session.Cache, session.Hours, session.SignIn));

            host.CacheGate.SetResult();
            Assert.True(await next);
            Assert.Contains("cache Either 1 8", host.Calls);                   // what was shown is what was applied
            Assert.All(page.Cards.Values, c => Assert.True(c.Enabled));
        }
    }

    [Fact]
    public async Task TheReadyStepCountsTheActiveChanges()
    {
        async Task<string> Said((int, int)? changes)
        {
            var session = new SetupSession(new FakeHost(Busy()) { Changes = changes }, firstRun: true, advanced: false);
            await session.LoadAsync(CancellationToken.None);
            await session.GoTo(SetupStep.Ready);
            return session.Summary.Single(r => r.Key == "Active changes").Value;
        }
        Assert.Equal("3, each can be put back", await Said((3, 3)));
        Assert.Equal("3, 2 of them can be put back", await Said((3, 2)));
        Assert.Equal("1, it can be put back", await Said((1, 1)));
        Assert.Equal("1, it cannot be put back", await Said((1, 0)));
        Assert.Equal("2, none can be put back", await Said((2, 0)));
        Assert.Equal("None yet", await Said((0, 0)));
        Assert.Equal("Listed under Active changes in the app", await Said(null));

        var keys = (await Session(Busy())).Summary.Select(r => r.Key);
        Assert.Equal(["Standby cache", "Codex shortcut", "Token leak", "Active changes"], keys);
    }

    [Fact]
    public void TheUpdateSwitchShowsWhatIsInEffectAfterAChangeThatDidNotWork()
    {
        var updates = new FakeUpdates();
        var session = new SetupSession(new FakeHost(Busy()) { Updates = updates }, firstRun: true, advanced: false);
        updates.Fails = true;
        session.SetUpdates(false);
        Assert.True(updates.Enabled);
        Assert.StartsWith("PoolWatch could not change this: ", session.UpdatesError);
        updates.Fails = false;
        session.SetUpdates(false);
        Assert.Equal((false, (string?)null), (updates.Enabled, session.UpdatesError));
    }

    // ------------------------------------------------------------------ the window's content, without a window

    private static async Task<SetupSession> Session(EnvironmentFacts facts, FakeHost? host = null)
    {
        var session = new SetupSession(host ?? new FakeHost(facts), firstRun: true, advanced: false);
        await session.LoadAsync(CancellationToken.None);
        return session;
    }

    /// <summary>The view as at <paramref name="percent"/> scaling (a larger text on this test machine), with no window around it.</summary>
    private static (SetupWizardView View, SetupFonts Fonts) View(SetupSession session, int percent, Size client, Func<DateTime>? clock = null)
    {
        var scale = new SetupScale(96, percent / 100f, 96);
        var fonts = new SetupFonts(scale);
        var view = new SetupWizardView(session, scale, fonts, TrayIcons.App, () => null!, _ => { }, clock ?? (() => new DateTime(2026, 10, 30, 1, 2, 0)))
            { Size = client };
        return (view, fonts);
    }

    /// <summary>
    /// The keyboard focus as the view moves it (the window is never shown, so no control can take it), and as WinForms moves
    /// it on when the control that has it is disabled: to the next control, here none of the view's (null).
    /// </summary>
    private sealed class FocusFake
    {
        public Control? On;

        public FocusFake(SetupWizardView view)
        {
            On = view.NextButton.Enabled ? view.NextButton : view.Content; // where the step shown put it
            view.HasFocus = c => c == On;
            view.GiveFocus = c => On = c;
            view.NextButton.EnabledChanged += (_, _) =>
            {
                if (!view.NextButton.Enabled && On == view.NextButton) On = null;
            };
        }
    }

    /// <summary>
    /// The window's client size on a working area of 1024 x 600 at <paramref name="percent"/>: the design's size, cut to
    /// the area with a frame of a title bar and borders at that scaling.
    /// </summary>
    private static Size SmallScreenClient(int percent)
    {
        float k = percent / 100f;
        var area = new Rectangle(0, 0, (int)(1024 * k), (int)(600 * k));
        var frame = new Size((int)(16 * k), (int)(39 * k));
        var client = SetupWindowFit.Client(new Size((int)(880 * k), (int)(586 * k)), frame, area);
        Assert.True(client.Width + frame.Width <= area.Width && client.Height + frame.Height <= area.Height, $"the window, {client} and its frame, is bigger than {area.Size}");
        return client;
    }

    [Theory]
    [InlineData(100)]
    [InlineData(125)]
    [InlineData(150)]
    [InlineData(200)]
    public async Task EveryStepFitsAWorkingAreaOf1024By600(int percent)
    {
        // Every row there is: two Dev Drives, the Codex app, services, the leak found (its "Why?" open), and an error under a row.
        var host = new FakeHost(Busy()) { Updates = new FakeUpdates(), RunResult = r => r.Id == RecommendationId.StartAtSignIn
            ? (false, "PoolWatch could not set itself to start when you sign in. It runs from a folder you can change.") : (true, "") };
        var session = await Session(Busy(), host);
        var (view, fonts) = View(session, percent, SmallScreenClient(percent));
        using (view)
        using (fonts)
        {
            foreach (var step in SetupFlow.Steps)
            {
                await session.GoTo(step);
                Assert.True(view.LayoutProblems() is { Count: 0 }, $"{step} at {percent}%: {string.Join("; ", view.LayoutProblems())}");
            }
            // An error under a row (Next stays on the step), and the page that scrolls.
            await session.GoTo(SetupStep.Fixes);
            Assert.False(await session.NextAsync(null!));
            Assert.Empty(view.LayoutProblems());
            Assert.True(view.Content.AutoScroll);
        }
    }

    [Theory]
    [InlineData(100)]
    [InlineData(200)]
    public async Task OnTheDesignsOwnSizeOnlyTheLongestStepScrolls(int percent)
    {
        var session = await Session(Busy(), new FakeHost(Busy()) { Updates = new FakeUpdates() });
        float k = percent / 100f;
        var (view, fonts) = View(session, percent, new Size((int)(880 * k), (int)(586 * k)));
        using (view)
        using (fonts)
        {
            var scrolling = new List<SetupStep>();
            foreach (var step in SetupFlow.Steps)
            {
                await session.GoTo(step);
                Assert.Empty(view.LayoutProblems());
                if (view.Scrolls) scrolling.Add(step);
            }
            // Only the Fixes step, with every row and the token-leak guard's "Why?" open (its own words in full), scrolls.
            Assert.Equal([SetupStep.Fixes], scrolling);
        }
    }

    [Fact]
    public async Task ARowIsUpdatedInPlaceNeverMadeAgain()
    {
        var host = new FakeHost(Busy() with { TokenLeak = TokenLeakStatus.NeedsTest }) { CheckGives = LeakVerdict.Leak };
        var session = new SetupSession(host, firstRun: true, advanced: false);
        var (view, fonts) = View(session, 100, new Size(880, 586));
        using (view)
        using (fonts)
        {
            await session.LoadAsync(CancellationToken.None);   // the scan, then the check: the token row changes
            var impact = (ImpactPage)view.Pages[SetupStep.YourPc];
            var fixes = (FixesPage)view.Pages[SetupStep.Fixes];
            var meters = impact.Meters.ToDictionary(kv => kv.Key, kv => kv.Value);
            var ends = fixes.Ends.ToDictionary(kv => kv.Key, kv => kv.Value);
            Assert.Equal("High impact", meters[Cause.TokenLeak].LevelText);

            session.Choose(Fix.Shortcut, false);                // a press on one row
            await session.GoTo(SetupStep.Fixes);
            Assert.True(await session.NextAsync(null!));       // the sign-in task and the guard are applied: their rows show "On"
            Assert.All(impact.Meters, kv => Assert.Same(meters[kv.Key], kv.Value));
            Assert.Same(ends[Fix.Shortcut], fixes.Ends[Fix.Shortcut]);
            Assert.Same(ends[Fix.Defender], fixes.Ends[Fix.Defender]);
            Assert.False(((YesNo)fixes.Ends[Fix.Shortcut]).Value);
            // A row that now offers something else (a switch become a status) is the only one replaced.
            Assert.IsType<FixesPage.StatusText>(fixes.Ends[Fix.TokenGuard]);
            Assert.IsType<FixesPage.StatusText>(fixes.Ends[Fix.SignIn]);
            Assert.Equal("None", meters[Cause.TokenLeak].LevelText);  // the guard on: the leak cannot build up
        }
    }

    [Fact]
    public async Task TheCacheStepWarnsOfTheStutterOnlyWhereClearingIsTurnedOn()
    {
        var session = await Session(Busy());
        var (view, fonts) = View(session, 100, new Size(880, 586));
        using (view)
        using (fonts)
        {
            await session.GoTo(SetupStep.StandbyCache);
            var page = (CachePage)view.Pages[SetupStep.StandbyCache];
            Assert.Equal((true, "Whichever comes first"), (page.StutterShown, page.RecommendedShown));
            Assert.True(page.Cards[CacheChoice.Either].Selected);
            Assert.Contains(SetupText.Stutter, page.Texts());
            session.ChooseCache(CacheChoice.Later);
            Assert.False(page.StutterShown);
            Assert.DoesNotContain(SetupText.Stutter, page.Texts());
            Assert.True(page.Cards[CacheChoice.Later].Selected && !page.Cards[CacheChoice.Either].Selected);

            // Changing a card's number chooses that card, and every field and the picture show it.
            page.HourFields[0].SelectedIndex = CacheChoices.HourOptions.ToList().IndexOf(3);
            Assert.Equal((CacheChoice.Schedule, 3.0), (session.Cache, session.Hours));
            Assert.All(page.HourFields, f => Assert.Equal("3 hours", f.Text));
            page.GbFields[1].SelectedIndex = CacheChoices.GbOptions.ToList().IndexOf(12);
            Assert.Equal((CacheChoice.Either, 12.0), (session.Cache, session.PoolGb));
            Assert.Equal([SetupText.ChartLegend, "your limit, 12 GB", "emptied, 3 h", "emptied, 12 GB"], page.Chart.Labels);
            Assert.Empty(view.LayoutProblems());
        }
    }

    [Fact]
    public async Task WhicheverComesFirstIsRecommendedAndChosenBeforeAndAfterTheScanOnAnyPc()
    {
        // An NTFS-only PC: the same recommendation as with Dev Drives, and it never moves when the scan lands.
        var host = new FakeHost(Facts(Ntfs) with { InstalledInProgramFiles = true }) { ScanGate = new() };
        long now = 0;
        var session = new SetupSession(host, firstRun: true, advanced: false, clock: () => now);
        var (view, fonts) = View(session, 100, new Size(880, 586));
        using (view)
        using (fonts)
        {
            var page = (CachePage)view.Pages[SetupStep.StandbyCache];
            var load = session.LoadAsync(CancellationToken.None);
            await session.GoTo(SetupStep.StandbyCache);
            Assert.Equal(("Whichever comes first", true, true), (page.RecommendedShown, page.Cards[CacheChoice.Either].Selected, page.WaitingShown));
            Assert.False(view.NextButton.Enabled);                              // Next waits for the scan, and says so
            Assert.Contains(SetupText.WaitForScan, page.Texts());
            Assert.Empty(view.LayoutProblems());

            host.ScanGate.SetResult(host.Scanned!);
            await load;
            Assert.Equal(("Whichever comes first", true, false), (page.RecommendedShown, page.Cards[CacheChoice.Either].Selected, page.WaitingShown));
            Assert.False(view.NextButton.Enabled);                              // what the scan found is on screen for a moment first
            now += NextWaitTicks;
            view.NextWaitOver();
            Assert.True(view.NextButton.Enabled);
            Assert.Equal(CacheChoice.Either, session.Cache);
        }
    }

    [Theory]
    [InlineData("Schedule", true)]
    [InlineData("PoolSize", true)]
    [InlineData("Either", true)]
    [InlineData("Later", false)]
    public async Task TheNoteThatARuleReplacesYourOwnTaskShowsExactlyWhereNextReplacesIt(string choiceName, bool replaces)
    {
        var choice = Enum.Parse<CacheChoice>(choiceName);
        var host = new FakeHost(Busy() with { OwnClearTasks = [@"\RAMMap clear"] });
        var session = await Session(host.Scanned!, host);
        var (view, fonts) = View(session, 100, new Size(880, 586));
        using (view)
        using (fonts)
        {
            await session.GoTo(SetupStep.StandbyCache);
            var page = (CachePage)view.Pages[SetupStep.StandbyCache];
            session.ChooseCache(choice);
            Assert.Equal(replaces, page.OwnTaskShown);
            Assert.Equal(replaces, page.Texts().Contains(SetupText.ReplacesOwnTask));
            Assert.Empty(view.LayoutProblems());
            Assert.True(await session.NextAsync(null!));
            Assert.Equal(replaces, Assert.Single(host.Calls, c => c.StartsWith("cache", StringComparison.Ordinal)).EndsWith(" replacing your task", StringComparison.Ordinal));
        }

        // No task of your own: no note, and Next replaces nothing.
        var plain = new FakeHost(Busy());
        var s2 = await Session(plain.Scanned!, plain);
        var (v2, f2) = View(s2, 100, new Size(880, 586));
        using (v2)
        using (f2)
        {
            await s2.GoTo(SetupStep.StandbyCache);
            s2.ChooseCache(choice);
            Assert.False(((CachePage)v2.Pages[SetupStep.StandbyCache]).OwnTaskShown);
            Assert.True(await s2.NextAsync(null!));
            Assert.DoesNotContain(plain.Calls, c => c.EndsWith(" replacing your task", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task EachStepHasTheDesignsButtonsAndTheReadyStepHasNoTour()
    {
        var session = await Session(Busy(), new FakeHost(Busy()) { Updates = new FakeUpdates() });
        var (view, fonts) = View(session, 100, new Size(880, 586));
        using (view)
        using (fonts)
        {
            Assert.Equal((SetupText.GetStarted, false, true), (view.NextText, view.BackButton.Visible, view.HowLink.Visible)); // after the setup time
            await session.GoTo(SetupStep.Safety);
            Assert.Equal((SetupText.Next, true, true), (view.NextText, view.BackButton.Visible, view.HowLink.Visible));
            await session.GoTo(SetupStep.Ready);
            Assert.Equal((SetupText.OpenApp, false, true), (view.NextText, view.BackButton.Visible, view.HowLink.Visible));
            var texts = view.Pages.Values.SelectMany(p => p.Texts()).ToList();
            Assert.DoesNotContain(texts, t => t.Contains("Show me around", StringComparison.Ordinal) || t.Contains("restore point", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(SimpleSettings.UpdatesLabel, view.Pages[SetupStep.Ready].Texts());
        }
    }

    [Fact]
    public async Task TheWelcomeChoiceIsSetupsAndTheAppsAndStartsFromTheViewInUse()
    {
        var session = new SetupSession(new FakeHost(Busy()), firstRun: false, advanced: true);
        var (view, fonts) = View(session, 100, new Size(880, 586));
        using (view)
        using (fonts)
        {
            var page = (WelcomePage)view.Pages[SetupStep.Welcome];
            Assert.True(page.AdvancedCard.Selected && !page.SimpleCard.Selected);
            Assert.Contains(SetupText.LevelQuestion, page.Texts());
            Assert.Contains(SetupText.LevelLater, page.Texts());
            await Task.CompletedTask;
        }
    }

    [Fact]
    public void TheTextsArePlainWords()
    {
        var setting = new Regex(@"\b[A-Z][a-z]+(?:[A-Z][a-z]+)+(Enabled|Hours|Gb|Minutes)\b|AutoClear|TokenGuard", RegexOptions.CultureInvariant);
        var facts = Busy();
        var texts = new List<string>
        {
            SetupText.Mission, SetupText.SimpleText, SetupText.AdvancedText, SetupText.LevelLater, SetupText.Duration, SetupText.ImpactLead, SetupText.NotRead,
            SetupText.CacheLead, SetupText.LaterText, SetupText.Stutter, SetupText.ReplacesOwnTask, SetupText.ServicesLead, SetupText.WindowsServicesText,
            SetupText.PutBack, SetupText.FindIt, SetupText.ReadyLead, SetupText.TrayNote, SetupText.NextAgain, SetupText.WaitForScan, FixRows.GuardChecking,
        };
        texts.AddRange(SetupText.Pillars.SelectMany(p => new[] { p.Title, p.Text }));
        texts.AddRange(SetupText.Promises);
        texts.AddRange(SetupText.AgentSteps);
        foreach (var state in Enum.GetValues<LeakState>()) texts.AddRange(ImpactRows.From(facts, state).SelectMany(r => new[] { r.Title, r.Detail, r.Under ?? "" }));
        texts.AddRange(ImpactRows.From(Facts(Ntfs), LeakState.NotFound).Select(r => r.Detail));
        texts.AddRange(FixRows.From(facts, LeakState.Found, false, true).SelectMany(r => new[] { r.Title, r.Detail }));
        texts.AddRange(HowPage.Columns.SelectMany(c => new[] { c.Title, c.Text }));
        texts.AddRange(SetupLinks.All.Select(l => l.Text));
        Assert.All(texts, t => Assert.DoesNotMatch(setting, t));
        Assert.All(texts, t => Assert.DoesNotContain("!", t));
        Assert.All(texts, t => Assert.DoesNotContain("kernel memory", t));
        // The words agreed after the design: nothing promised safe to turn off, no bare "Microsoft's guidance", the new
        // name of the list of changes.
        Assert.All(texts, t => Assert.DoesNotContain("safe to turn off", t, StringComparison.OrdinalIgnoreCase));
        Assert.All(texts, t => Assert.DoesNotContain("guidance", t, StringComparison.OrdinalIgnoreCase));
        Assert.All(texts, t => Assert.DoesNotContain("Applied changes", t));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(125)]
    [InlineData(150)]
    [InlineData(200)]
    public void HowPoolWatchWorksFitsAWorkingAreaOf1024By600(int percent)
    {
        var scale = new SetupScale(96, percent / 100f, 96);
        using var fonts = new SetupFonts(scale);
        var session = new SetupSession(new FakeHost(Busy()), firstRun: true, advanced: false);
        using var view = new HowItWorksView(new PageContext(session, scale, fonts, TrayIcons.App, () => null!, _ => { })) { Size = SmallScreenClient(percent) };
        Assert.True(view.LayoutProblems() is { Count: 0 }, string.Join("; ", view.LayoutProblems()));
        var texts = view.Page.Texts().ToList();
        Assert.Contains("Emptying it is safe", texts);
        Assert.Contains(SetupLinks.RamMap.Text, texts);
    }

    [Fact]
    public void TheWindowIsTheDesignsSizeCutToTheWorkingAreaAndInsideIt()
    {
        var frame = new Size(16, 39);
        Assert.Equal(new Size(880, 586), SetupWindowFit.Client(SetupWindowFit.Design, frame, new Rectangle(0, 0, 1920, 1040)));
        Assert.Equal(new Size(880, 561), SetupWindowFit.Client(SetupWindowFit.Design, frame, new Rectangle(0, 0, 1024, 600)));
        // Centred, and never above or left of the area (its title bar stays where it can be dragged).
        Assert.Equal(new Point(64, 0), SetupWindowFit.Centre(new Size(896, 600), new Rectangle(0, 0, 1024, 600)));
        Assert.Equal(new Point(0, 40), SetupWindowFit.Centre(new Size(1200, 700), new Rectangle(0, 40, 1024, 600)));
        Assert.Equal(new Point(500, 300), SetupWindowFit.Centre(new Size(200, 100), new Rectangle(0, 0, 1920, 1040), new Rectangle(400, 250, 400, 200)));
        // The page leaves room for the scroll bar Windows draws at that scaling, also where the tests stand in for it with a larger text.
        Assert.Equal(2 * SystemInformation.GetVerticalScrollBarWidthForDpi(96), new SetupScale(96, 2, 96).ScrollBarWidth);
        Assert.Equal(SystemInformation.GetVerticalScrollBarWidthForDpi(144), new SetupScale(144, 1, 144).ScrollBarWidth);
    }

    [Fact]
    public void TheSetupWindowFitsAWorkingAreaOf1024By600()
    {
        using var form = new SetupWizardForm(new FakeHost(Busy()), firstRun: true, advanced: false);
        _ = form.Handle;
        var area = new Rectangle(0, 40, 1024, 600);
        form.FitTo(area);
        Assert.True(area.Contains(form.Bounds), $"the window at {form.Bounds} is outside {area}");
        Assert.Equal(SetupText.WindowTitle, form.Text);
        Assert.Same(TrayIcons.App, form.Icon); // the app's icon (the binoculars, no state dot), as the main window has it
    }

    [Fact]
    public void TheCiUiTestLooksForTheScreenshotsAndTheMenuItemSetupHas()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PoolWatch.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        string script = File.ReadAllText(Path.Combine(dir!.FullName, "ci", "m4-ui.ps1"));
        var names = SetupFlow.Steps.Select(SetupWizardForm.ShotName).Append(SetupWizardForm.HowItWorksShot).ToList();
        Assert.Equal(["6-setup-1-welcome.png", "6-setup-2-your-pc.png", "6-setup-3-safety.png", "6-setup-4-standby-cache.png", "6-setup-5-fixes.png",
            "6-setup-6-services.png", "6-setup-7-ready.png", "6-setup-how-it-works.png"], names);
        Assert.All(names, n => Assert.Contains($"'{n}'", script));
        Assert.Contains($"'{FirstRunGate.MenuItem}'", script);
    }

    [Fact]
    public void SetupOpensOnTheScreenOfTheWindowItOpensOverElseTheOneWithTheMouse()
    {
        var left = new Rectangle(0, 0, 1920, 1040);
        var right = new Rectangle(1920, 0, 2560, 1400);
        Rectangle AreaOf(Rectangle r) => r.X >= 1920 ? right : left;
        Rectangle AreaAt(Point p) => p.X >= 1920 ? right : left;
        Assert.Equal(right, SetupWindowFit.Area(new Rectangle(2000, 100, 900, 600), new Point(10, 10), AreaOf, AreaAt)); // a re-run over the main window
        Assert.Equal(right, SetupWindowFit.Area(null, new Point(2500, 500), AreaOf, AreaAt));                           // the first run: the mouse's screen
        Assert.Equal(left, SetupWindowFit.Area(null, new Point(10, 10), AreaOf, AreaAt));
    }

    [Fact]
    public async Task MovedToAMonitorScaledOtherwiseEachWindowIsMadeAgainOnTheSameStepAndInsideThatMonitor()
    {
        using var form = new SetupWizardForm(new FakeHost(Busy()), firstRun: true, advanced: false);
        _ = form.Handle;
        await form.Session.LoadAsync(CancellationToken.None);
        await form.Session.GoTo(SetupStep.Fixes);
        form.Session.Choose(Fix.Shortcut, false);
        var old = form.View;
        // Where Windows suggests the window goes on the new monitor: across its edge, so only fitting brings it inside.
        var primary = Screen.PrimaryScreen!.WorkingArea;
        var suggested = new Rectangle(primary.Right - 100, primary.Bottom - 100, 900, 620);
        var area = Screen.FromRectangle(suggested).WorkingArea;
        form.Location = new Point(area.Right - 50, area.Bottom - 50);                         // where it was on the other monitor
        // Windows: now on a monitor at 150% (this test host is not DPI aware, so the window's DPI reads as before; the
        // message still goes the way it goes in PoolWatch).
        DpiChanged(form, 144, suggested);
        Assert.NotSame(old, form.View);
        Assert.True(old.IsDisposed);
        Assert.Same(form.View.Pages[SetupStep.Fixes], form.View.Page);                         // the same step
        Assert.False(((YesNo)((FixesPage)form.View.Page).Ends[Fix.Shortcut]).Value);          // with the same choices
        Assert.Same(form.View.NextButton, form.AcceptButton);
        Assert.True(area.Contains(form.Bounds), $"the window at {form.Bounds} is outside {area}");

        using var how = new HowItWorksForm(form.Session, _ => { });
        _ = how.Handle;
        var oldHow = how.View;
        how.Location = new Point(area.Right - 50, area.Bottom - 50);
        DpiChanged(how, 144, suggested);
        Assert.NotSame(oldHow, how.View);
        Assert.True(oldHow.IsDisposed);
        Assert.Same(how.View.CloseButton, how.CancelButton);
        Assert.True(area.Contains(how.Bounds), $"How PoolWatch works at {how.Bounds} is outside {area}");
    }

    [Fact]
    public void EachWindowOpensCentredOverTheWindowItOpensOverElseOnTheScreenWithTheMouse()
    {
        // As Windows loads a window just before it first shows it (the windows here are never shown).
        var onLoad = typeof(Form).GetMethod("OnLoad", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var area = Screen.PrimaryScreen!.WorkingArea;
        using var main = new Form { StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(area.Right - 300, area.Bottom - 200, 300, 200) };
        _ = main.Handle;
        using var again = new SetupWizardForm(new FakeHost(Busy()), firstRun: false, advanced: false) { Owner = main };
        _ = again.Handle;
        onLoad.Invoke(again, [EventArgs.Empty]);
        var expected = SetupWindowFit.Centre(again.Size, Screen.FromRectangle(main.Bounds).WorkingArea, main.Bounds);
        Assert.Equal(expected, again.Location);                                               // over the main window, inside its screen
        if (area.Width > again.Width && area.Height > again.Height)
            Assert.NotEqual(SetupWindowFit.Centre(again.Size, area), expected);              // which is not the screen's middle

        // The first run opens over no window: centred on the screen with the mouse.
        using var first = new SetupWizardForm(new FakeHost(Busy()), firstRun: true, advanced: false);
        _ = first.Handle;
        var before = Screen.FromPoint(Cursor.Position).WorkingArea;
        onLoad.Invoke(first, [EventArgs.Empty]);
        var after = Screen.FromPoint(Cursor.Position).WorkingArea;
        Assert.Contains(first.Location, new[] { SetupWindowFit.Centre(first.Size, before), SetupWindowFit.Centre(first.Size, after) });

        // How PoolWatch works opens over setup.
        again.Location = new Point(area.Right - again.Width, area.Bottom - again.Height);
        using var how = new HowItWorksForm(again.Session, _ => { }) { Owner = again };
        _ = how.Handle;
        onLoad.Invoke(how, [EventArgs.Empty]);
        Assert.Equal(SetupWindowFit.Centre(how.Size, Screen.FromControl(again).WorkingArea, again.Bounds), how.Location);
        if (area.Width > how.Width && area.Height > how.Height)
            Assert.NotEqual(SetupWindowFit.Centre(how.Size, area), how.Location);
    }

    private const int WmDpiChanged = 0x02E0;

    /// <summary>Windows telling a window that it is now on a monitor of <paramref name="dpi"/>, and where it suggests the window goes there.</summary>
    private static void DpiChanged(Form form, int dpi, Rectangle suggested)
    {
        var rect = new Rect { Left = suggested.Left, Top = suggested.Top, Right = suggested.Right, Bottom = suggested.Bottom };
        SendMessage(form.Handle, WmDpiChanged, (dpi << 16) | dpi, ref rect);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, nint wParam, ref Rect lParam);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [Fact]
    public void TheSwitchesCardsAndYesNoTellAScreenReaderWhetherTheyAreOn()
    {
        var scale = new SetupScale(96, 1, 96);
        using var fonts = new SetupFonts(scale);
        static bool Checked(Control c) => c.AccessibilityObject.State.HasFlag(AccessibleStates.Checked);

        using var toggle = new Toggle(scale, fonts, "Start when you sign in");
        Assert.Equal((AccessibleRole.CheckButton, false), (toggle.AccessibilityObject.Role, Checked(toggle)));
        toggle.On = true;
        Assert.True(Checked(toggle));

        using var card = new ChoiceCard(scale, SetupText.Later);
        Assert.Equal((AccessibleRole.RadioButton, false), (card.AccessibilityObject.Role, Checked(card)));
        card.Selected = true;
        Assert.True(Checked(card));

        using var yesNo = new YesNo(scale, fonts, "Create a Codex shortcut without its shader cache?");
        Assert.Equal((AccessibleRole.CheckButton, true, "Create a Codex shortcut without its shader cache?: Yes"),
            (yesNo.AccessibilityObject.Role, Checked(yesNo), yesNo.AccessibleName));
        yesNo.Value = false;
        Assert.Equal((false, "Create a Codex shortcut without its shader cache?: No"), (Checked(yesNo), yesNo.AccessibleName));
    }

    [Fact]
    public async Task TheReadyStepsClockShowsTheTimeTheStepOpened()
    {
        var now = new DateTime(2026, 10, 30, 1, 2, 0);
        var session = await Session(Busy());
        var (view, fonts) = View(session, 100, new Size(880, 586), () => now);
        using (view)
        using (fonts)
        {
            now = new DateTime(2026, 10, 30, 14, 35, 0);                        // setup took a while
            await session.GoTo(SetupStep.Ready);
            var tray = ((ReadyPage)view.Pages[SetupStep.Ready]).Tray;
            Assert.Equal(now.ToString("t", System.Globalization.CultureInfo.CurrentCulture), tray.Clock.Time);
            Assert.Empty(view.LayoutProblems());
        }
    }

    // ------------------------------------------------------------------ the icons

    [Fact]
    public void TheIconsPathsReadAndDrawWhereTheDesignDrawsThem()
    {
        Assert.True(SetupIcons.PathCount >= 20);
        using var gauge = SvgPath.Parse("M3 14a7 7 0 0 1 14 0");           // a half circle over its baseline
        var b = gauge.GetBounds();
        Assert.Equal((3f, 7f, 17f, 14f), (MathF.Round(b.Left, 1), MathF.Round(b.Top, 1), MathF.Round(b.Right, 1), MathF.Round(b.Bottom, 1)));
        using var wave = SvgPath.Parse("M2 8c2-1.2 3 1.2 5 0s3-1.2 5 0 3 1.2 6 0");  // relative, a repeated S
        Assert.Equal(new PointF(18, 8), wave.GetLastPoint());
        using var box = SvgPath.Parse("M3 4.5h14v9H9l-4 3v-3H3z");
        Assert.Equal(new RectangleF(3, 4.5f, 14, 12), box.GetBounds());
        Assert.Throws<FormatException>(() => SvgPath.Parse("3 4"));
        Assert.Throws<FormatException>(() => SvgPath.Parse("M3 4Q1 1 2 2"));
        using var bmp = new Bitmap(40, 40);
        using var g = Graphics.FromImage(bmp);
        foreach (var icon in Enum.GetValues<SetupIcon>()) SetupIcons.Draw(g, icon, new RectangleF(0, 0, 40, 40), Color.Black);
    }

    // ------------------------------------------------------------------ fakes

    /// <summary>The app around setup: what it changes is recorded, never made.</summary>
    private sealed class FakeHost(EnvironmentFacts facts) : ISetupHost
    {
        public readonly List<string> Calls = [];
        public string Instructions = "";
        public EnvironmentFacts? Scanned = facts;
        public Exception? ScanError;
        /// <summary>A scan that ends only when the test says so (or never).</summary>
        public TaskCompletionSource<EnvironmentFacts>? ScanGate;
        public LeakVerdict? CheckGives;
        /// <summary>A token-leak check that ends only when the test says so.</summary>
        public TaskCompletionSource? CheckGate;
        public (bool, string) CacheResult = (true, "");
        /// <summary>A cache change that ends only when the test says so (Next is busy meanwhile).</summary>
        public TaskCompletionSource? CacheGate;
        public Func<Recommendation, (bool, string)> RunResult = _ => (true, "");
        /// <summary>A suggestion's button that ends only when the test says so (Next is busy meanwhile).</summary>
        public TaskCompletionSource? RunGate;
        public (int, int)? Changes = (3, 3);
        public int Completed;

        public bool Smoke { get; set; }
        public EnvironmentFacts? Facts { get; set; } = facts;
        public Settings Running { get; } = new();
        public LeakVerdict? LeakTestNow { get; private set; }
        /// <summary>The shortcut is listed under Active changes (the tests put it back as Active changes would, telling setup nothing).</summary>
        public bool ShortcutMade { get; set; }
        public bool LauncherInstalled { get; set; } = true;
        public IUpdateCheck? Updates { get; set; }

        public Task<EnvironmentFacts> Scan()
        {
            Calls.Add("scan");
            if (ScanError is { } e) throw e;
            if (ScanGate is { } gate) return gate.Task;
            Facts = Scanned;
            return Task.FromResult(Facts!);
        }

        public async Task<(bool Ok, string Message)> Run(Recommendation r, IWin32Window owner)
        {
            Calls.Add("run " + r.Id);
            if (RunGate is { } gate) await gate.Task;
            var result = RunResult(r);
            if (result.Item1)
                switch (r.Id)
                {
                    case RecommendationId.StartAtSignIn: Facts = Facts! with { LogonTaskExists = true }; break;
                    case RecommendationId.TokenLeakGuard: Running.TokenGuardEnabled = true; break;
                    case RecommendationId.ChatGptShortcut: ShortcutMade = true; break;
                    case RecommendationId.DefenderDevDrive: Instructions = r.Instructions ?? ""; break;
                }
            return result;
        }

        public async Task<(bool Ok, string Message)> CheckTokenLeak(CancellationToken stop)
        {
            Calls.Add("check");
            if (CheckGate is { } gate) await gate.Task;
            LeakTestNow = CheckGives;
            return (CheckGives is not null, "checked");
        }

        public async Task<(bool Ok, string Message)> ApplyCache(CacheChoice choice, double hours, double gb, bool replaceOwnTask)
        {
            Calls.Add($"cache {choice} {hours} {gb}" + (replaceOwnTask ? " replacing your task" : ""));
            if (CacheGate is { } gate) await gate.Task;
            if (CacheResult.Item1)
            {
                var (enabled, every, size) = CacheChoices.Writes(choice, hours, gb);
                Running.AutoClearEnabled = enabled;
                if (every is { } h) Running.AutoClearEveryHours = h;
                if (size is { } g) Running.AutoClearPoolGb = g;
            }
            return CacheResult;
        }

        public Task<(int All, int CanPutBack)?> ActiveChanges() => Task.FromResult<(int All, int CanPutBack)?>(Changes);
        public void MarkCompleted() => Completed++;
        public void OpenWebPage(IWin32Window owner, string url) => Calls.Add("open " + url);
        public void Log(string line) { }
    }

    private sealed class FakeUpdates : IUpdateCheck
    {
        private bool _on = true;
        public bool Fails;

        public bool Enabled
        {
            get => _on;
            set => _on = Fails ? throw new IOException("settings.json is in use") : value;
        }
    }

    /// <summary>
    /// The PC behind the standby cache's choice: whether PoolWatch is elevated, a clear task of your own (as the hourly
    /// check read it), replacing it (as ClearTaskReplacer does: the hourly clear turned on, or why a task was left), the
    /// timeline.
    /// </summary>
    private sealed class FakeSystem : ISetupSystem
    {
        public readonly List<string> Calls = [], TimelineLines = [];
        public string? ReplaceError;
        /// <summary>The running settings, read when the clear status is read again (PoolWatch's own clear then stops waiting for your task).</summary>
        public LiveSettings? Live;
        public readonly List<(bool On, double EveryHours, double PoolGb)> RulesAtRefresh = [];
        public EnvironmentFacts? Facts => null;
        public Task<EnvironmentFacts> Rescan()
        {
            Calls.Add("rescan");
            return Task.FromResult(new EnvironmentFacts { ScannedUtc = DateTime.UtcNow, WindowsName = "Windows 11 Pro", Build = "26200.7000", Architecture = "x64" });
        }
        public bool Elevated { get; set; } = true;
        public bool OwnClearTask { get; set; }
        public void RefreshClearStatus()
        {
            Calls.Add("refresh clear status");
            if (Live?.Running is { } s) RulesAtRefresh.Add((s.AutoClearEnabled, s.AutoClearEveryHours, s.AutoClearPoolGb));
        }
        public string? RegisterLogonTask() => throw new NotSupportedException();
        public (IReadOnlyList<CacheHelper.LegacyTaskInfo> Removed, string? Error) ReplaceClearTasks(LiveSettings live, string via)
        {
            Calls.Add("replace own task");
            if (ReplaceError is { } error) return ([], error);
            live.Apply("the hourly automatic cache clear, in place of your own clear task", s => { s.AutoClearEveryHours = 1; s.AutoClearEnabled = true; });
            return ([], null);
        }
        public Task<LeakVerdict?> CheckTokenLeak(CancellationToken stop) => throw new NotSupportedException();
        public (bool Ok, string Message) CreateChatGptShortcut(string? iconPath, string via) => throw new NotSupportedException();
        public ClearRecord? StartClear() => throw new NotSupportedException();
        public string IncidentsDir => "";
        public void Timeline(string line) => TimelineLines.Add(line);
    }

    /// <summary>
    /// What a real scan reads, from a PC with the ChatGPT desktop app (its icon not found, so the shortcut reads the PC
    /// again), a RAMMap task of your own, no sign-in task, and installed with the installer. Windows' name can be held
    /// (<see cref="Gate"/> reset): the scan then hangs until it is set again (30 seconds at most).
    /// </summary>
    private sealed class HeldReads : IEnvironmentReads
    {
        public ManualResetEventSlim Gate { get; } = new(initialState: true);

        public string? CurrentVersion(string name)
        {
            if (name == "ProductName") Gate.Wait(TimeSpan.FromSeconds(30));
            return name switch { "ProductName" => "Windows 10 Pro", "CurrentBuild" => "26200", "UBR" => "7000", _ => null };
        }
        public string? InsiderBranchName() => null;
        public ushort? NativeMachine() => 0x8664;
        public long RamBytes() => 32L << 30;
        public IReadOnlyList<VolumeInfo> Volumes() => [new(@"C:\", "NTFS", "", false, "Fixed")];
        public bool? DevDriveTrusted(string root) => null;
        public IReadOnlyDictionary<string, string> Versions() => new Dictionary<string, string>();
        public IReadOnlyList<string> PackageFullNames() => ["OpenAI.Codex_1.2.0.0_x64__2p2nqsd0c76g0"];
        public string? PackageRootFolder(string fullName) => null;
        public bool PathExists(string path) => false;
        public GuardState? SavedGuardState() => null;
        public uint? LiveForegroundLockTimeout() => null;
        public IReadOnlyList<string> OwnClearTasks() => [@"\RAMMap clear"];
        public bool LogonTaskExists() => false;
        public bool InstalledInProgramFiles() => true;
        public IReadOnlyList<string> RunningOptionalServices() => [];
        public (bool? RealTime, bool? PerformanceMode) Defender() => (null, null);
    }

    /// <summary>The PC behind the suggestions' buttons as Windows has it, over a real scan: each change reads the PC again as WindowsSetupSystem does.</summary>
    private sealed class ProbeSystem(EnvironmentProbe probe, TimeSpan hungAfter) : ISetupSystem
    {
        public readonly List<string> Calls = [];
        public EnvironmentFacts? Facts => probe.Last;
        public Task<EnvironmentFacts> Rescan() => probe.ScanAsync(hungAfter);
        public bool Elevated => true;
        public bool OwnClearTask => probe.Last?.OwnClearTasks.Count > 0;
        public void RefreshClearStatus() => Calls.Add("refresh clear status");
        public string? RegisterLogonTask()
        {
            Calls.Add("register the sign-in task");
            return null;
        }
        public (IReadOnlyList<CacheHelper.LegacyTaskInfo> Removed, string? Error) ReplaceClearTasks(LiveSettings live, string via)
        {
            Calls.Add("replace own task");
            live.Apply("the hourly automatic cache clear, in place of your own clear task", s => { s.AutoClearEveryHours = 1; s.AutoClearEnabled = true; });
            return ([], null);
        }
        public Task<LeakVerdict?> CheckTokenLeak(CancellationToken stop) => Task.FromResult<LeakVerdict?>(null);
        public (bool Ok, string Message) CreateChatGptShortcut(string? iconPath, string via)
        {
            Calls.Add("create the shortcut");
            return (true, "");
        }
        public ClearRecord? StartClear() => throw new NotSupportedException();
        public string IncidentsDir => "";
        public void Timeline(string line) { }
    }

    /// <summary>The app around setup made of its real parts, as SetupHost puts them together: the scan, the running settings, the suggestions' buttons, the cache's choice.</summary>
    private sealed class PartsHost(EnvironmentProbe probe, LiveSettings live, RecommendationActions actions, StandbyCacheSetup cache) : ISetupHost
    {
        public bool Smoke => false;
        public EnvironmentFacts? Facts => probe.Last;
        public Task<EnvironmentFacts> Scan() => probe.ScanToTheEnd();
        public Settings Running => live.Running;
        public LeakVerdict? LeakTestNow => actions.LeakTestNow;
        public bool ShortcutMade => false;
        public bool LauncherInstalled => true;
        public IUpdateCheck? Updates => null;
        public Task<(bool Ok, string Message)> Run(Recommendation r, IWin32Window owner) => actions.Run(r, owner, SetupSession.Via);
        public Task<(bool Ok, string Message)> CheckTokenLeak(CancellationToken stop) => actions.CheckTokenLeak(stop);
        public Task<(bool Ok, string Message)> ApplyCache(CacheChoice choice, double hours, double gb, bool replaceOwnTask) =>
            cache.Apply(choice, hours, gb, replaceOwnTask);
        public Task<(int All, int CanPutBack)?> ActiveChanges() => Task.FromResult<(int All, int CanPutBack)?>((0, 0));
        public void MarkCompleted() { }
        public void OpenWebPage(IWin32Window owner, string url) { }
        public void Log(string line) { }
    }

    private sealed class NoShell : IShell
    {
        public void ShowAdvanced(int tab) { }
        public void ShowActiveChanges() { }
        public void ShowReport(string? incidentDir) { }
        public Task<bool> StartTokenGuard() => Task.FromResult(true);
    }
}
