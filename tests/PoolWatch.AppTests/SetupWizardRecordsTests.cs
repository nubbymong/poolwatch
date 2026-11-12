using PoolWatch.Core;
using PoolWatch.Ui;
using PoolWatch.Ui.Setup;

namespace PoolWatch.AppTests;

/// <summary>
/// Setup's Records step (step 7 of 8, from its approved design), the Ready step's line about it, the step alone as it opens
/// after an upgrade from 1.0.0-beta.2 (whose setup had no such step), and where the tray app's starts lead until a mode
/// was chosen. The host and the PC are SetupWizardTests' fakes.
/// </summary>
public sealed partial class SetupWizardTests
{
    /// <summary>
    /// Setup's seventh step of eight: Reports only, recommended and chosen at a first run, and the cleanup's 7 days; Next
    /// writes the mode and the days together and records that a mode was chosen; the Ready step's Records line says what
    /// is in effect, "1 day" singular. The rail says "Step 7 of 8: Records". The page's words are the approved design's.
    /// </summary>
    [Fact]
    public async Task TheRecordsStepAppliesTheModeAndTheDaysAndTheReadyStepSaysThem()
    {
        Assert.Equal(8, SetupFlow.Steps.Count);
        Assert.Equal((7, 8), ((int)SetupStep.Records, (int)SetupStep.Ready));
        var host = new FakeHost(Busy());
        var session = await Session(Busy(), host);
        Assert.Equal((RecordMode.ReportsOnly, 7), (session.Records, session.Days));
        Assert.Equal([1, 3, 7, 14, 30, 60, 90], session.DayOptions);
        var (view, fonts) = View(session, 100, new Size(880, 586));
        using (view)
        using (fonts)
        {
            await session.GoTo(SetupStep.Records);
            var page = (RecordsPage)view.Pages[SetupStep.Records];
            Assert.Equal("Step 7 of 8: Records", view.Rail.AccessibleDescription);
            Assert.Equal((SetupText.Next, true), (view.NextText, view.BackButton.Visible));
            Assert.Equal(("Reports only", "7 days"), (page.RecommendedShown, page.DaysShown));
            Assert.True(page.Cards[RecordMode.ReportsOnly].Selected);
            Assert.Equal(
                [
                    "What should PoolWatch keep on disk?",
                    "PoolWatch watches the kernel pool live and tells you when it grows, whichever you choose. This decides what it writes down.",
                    "Reports only", "Recommended",
                    "A report each time the pool grows: what grew, the programs behind it, and a draft bug report for whoever makes the cause.",
                    "Enough to find a leak and report it. Most people need nothing more.",
                    "No all-day record is kept.", // the owner's truth fix of the approved "Nothing is written until the pool grows."
                    "Detailed recording",
                    "The reports, plus a record of everything PoolWatch watches, all day: the pool and its memory tags, programs and their handles, services, and every program started.",
                    "Useful when a leak builds up slowly over days, to compare a good day with a bad one, or when a developer asks for more than a report.",
                    "Up to about 60 MB a day on a PC doing heavy agentic coding.",
                    "Clean up reports and records after", "You can change both later in Settings.",
                    "Both stay on this PC and are never sent. They hold full command lines, which can contain passwords, so look before you share one.",
                ], page.Texts());
            // Settings' Reports only choice says the same in its tooltip, its last line the card's: no line that says nothing
            // is written before the pool grows (the boot report and the crash and restart reports are written without one).
            Assert.EndsWith("\nNo all-day record is kept.", SimpleSettings.ReportsOnlyTip);
            Assert.DoesNotContain("until the pool grows", SimpleSettings.ReportsOnlyTip);
            session.ChooseRecords(RecordMode.Detailed);
            page.DaysField.SelectedIndex = page.DaysField.Items.Cast<object>().Select(i => i.ToString()).ToList().IndexOf("30 days");
            Assert.Equal((RecordMode.Detailed, 30), (session.Records, session.Days));
            Assert.True(page.Cards[RecordMode.Detailed].Selected && !page.Cards[RecordMode.ReportsOnly].Selected);
            Assert.Equal("30 days", page.DaysShown);
            Assert.Empty(view.LayoutProblems());
            Assert.True(await session.NextAsync(null!));
        }
        Assert.Contains("records Detailed 30", host.Calls);
        Assert.Equal((RecordMode.Detailed, 30), (host.Running.RecordMode, host.Running.RetentionDays));
        Assert.Equal((1, 0), (host.RecordsChosen, host.Completed));
        Assert.Equal(SetupStep.Ready, session.Flow.Step);
        Assert.Equal("Detailed recording, cleaned up after 30 days", session.Summary.Single(r => r.Key == "Records").Value);
        (host.Running.RecordMode, host.Running.RetentionDays) = (RecordMode.ReportsOnly, 1);
        Assert.Equal("Reports only, cleaned up after 1 day", session.Summary.Single(r => r.Key == "Records").Value);
        Assert.True(await session.NextAsync(null!)); // Open PoolWatch: completed, and the choice recorded again (the store keeps the first)
        Assert.Equal((2, 1), (host.RecordsChosen, host.Completed));
    }

    /// <summary>
    /// The select offers the step's days, and the one in effect when Settings set another, in order; that one stays in the
    /// list after another is picked, so it can be picked again (also on the page).
    /// </summary>
    [Fact]
    public async Task TheCleanupSelectOffersTheStepsDaysAndTheOneInEffect()
    {
        Assert.Equal([1, 3, 7, 14, 30, 60, 90], RecordChoices.DayOptions(7));
        Assert.Equal([1, 3, 7, 10, 14, 30, 60, 90], RecordChoices.DayOptions(10));
        Assert.Equal(("1 day", "7 days"), (SetupText.Days(1), SetupText.Days(7)));
        Assert.Equal("Reports only, cleaned up after 7 days", RecordChoices.Summary(new Settings()));
        var host = new FakeHost(Busy());
        host.Running.RetentionDays = 10; // set in Settings
        var session = await Session(Busy(), host);
        Assert.Equal(10, session.Days);
        Assert.Equal([1, 3, 7, 10, 14, 30, 60, 90], session.DayOptions);
        var (view, fonts) = View(session, 100, new Size(880, 586));
        using (view)
        using (fonts)
        {
            await session.GoTo(SetupStep.Records);
            var page = (RecordsPage)view.Pages[SetupStep.Records];
            Assert.Equal("10 days", page.DaysShown);
            session.SetDays(7);
            Assert.Equal(7, session.Days);
            Assert.Equal([1, 3, 7, 10, 14, 30, 60, 90], session.DayOptions);
            Assert.Equal("7 days", page.DaysShown);
            page.DaysField.SelectedIndex = page.DaysField.Items.Cast<object>().Select(i => i.ToString()).ToList().IndexOf("10 days");
            Assert.Equal(10, session.Days); // picked again
        }
    }

    /// <summary>
    /// A records choice that could not be saved keeps the step with why, as the Standby cache step does; Next waits a moment,
    /// then goes on without it (a mode was chosen all the same: setup went past it).
    /// </summary>
    [Fact]
    public async Task ARecordsChoiceThatCouldNotBeSavedStaysWithWhyAndNextAgainGoesOn()
    {
        var host = new FakeHost(Busy()) { RecordsResult = (false, "That did not work: settings.json is in use.") };
        long now = 0;
        var session = new SetupSession(host, firstRun: true, advanced: false, clock: () => now);
        await session.LoadAsync(CancellationToken.None);
        var (view, fonts) = View(session, 100, new Size(880, 586));
        using (view)
        using (fonts)
        {
            await session.GoTo(SetupStep.Records);
            session.ChooseRecords(RecordMode.Detailed);
            Assert.False(await session.NextAsync(null!));
            Assert.Equal(SetupStep.Records, session.Flow.Step);
            Assert.Equal("That did not work: settings.json is in use. " + SetupText.NextAgain, session.Error(SetupSession.RecordsKey));
            Assert.True(((RecordsPage)view.Pages[SetupStep.Records]).ErrorShown);
            Assert.Empty(view.LayoutProblems());
            Assert.Equal(0, host.RecordsChosen);
            Assert.False(session.CanGoNext); // its moment first, so a second press as quick as a double-click's reads it
            now += NextWaitTicks;
            Assert.True(await session.NextAsync(null!));
        }
        Assert.Equal(SetupStep.Ready, session.Flow.Step);
        Assert.Equal(1, host.RecordsChosen);
        Assert.Single(host.Calls, c => c.StartsWith("records ", StringComparison.Ordinal)); // not tried again
    }

    /// <summary>
    /// The Records step alone, as it opens after an upgrade: the same page, the rail listing only Records, no Back, Done;
    /// it reads nothing of the PC and runs no check; Done waits a moment (it opened by itself), then applies the choice,
    /// records it (setup.json) and finishes, never marking setup completed again.
    /// </summary>
    [Fact]
    public async Task TheRecordsStepAloneShowsOnlyItselfAndDoneRecordsTheChoice()
    {
        var host = new FakeHost(Busy());
        long now = 0;
        var session = new SetupSession(host, firstRun: false, advanced: false, clock: () => now, recordsOnly: true);
        await session.LoadAsync(CancellationToken.None);
        Assert.DoesNotContain("scan", host.Calls);
        Assert.DoesNotContain("check", host.Calls);
        Assert.Equal([SetupStep.Records], session.Flow.Shown);
        Assert.Equal(SetupStep.Records, session.Flow.Step);
        Assert.False(session.Flow.CanGoBack);
        Assert.True(session.Flow.MayClose(CloseReason.UserClosing)); // closing it cancels it at once
        Assert.Equal((RecordMode.ReportsOnly, 7), (session.Records, session.Days)); // what is in effect: Reports only, the default
        var (view, fonts) = View(session, 100, new Size(880, 586));
        using (view)
        using (fonts)
        {
            Assert.Equal([SetupStep.Records], view.Pages.Keys);
            Assert.Equal((SetupText.Done, false, true), (view.NextText, view.BackButton.Visible, view.HowLink.Visible));
            Assert.Equal("Step 1 of 1: Records", view.Rail.AccessibleDescription);
            Assert.Empty(view.LayoutProblems());
            Assert.False(session.CanGoNext);
            Assert.False(session.Back());
            Assert.False(await session.NextAsync(null!)); // held for its moment
            now += NextWaitTicks;
            Assert.True(await session.NextAsync(null!)); // Done
        }
        Assert.True(session.Flow.Finished);
        Assert.Contains("records ReportsOnly 7", host.Calls);
        Assert.Equal((1, 0), (host.RecordsChosen, host.Completed));

        // Its failure says Done, not Next.
        var failing = new FakeHost(Busy()) { RecordsResult = (false, "That did not work.") };
        var again = new SetupSession(failing, firstRun: false, advanced: false, clock: () => now, recordsOnly: true);
        now += NextWaitTicks;
        Assert.False(await again.NextAsync(null!));
        Assert.Equal("That did not work. " + SetupText.DoneAgain, again.Error(SetupSession.RecordsKey));
        Assert.Equal(0, failing.RecordsChosen);
    }

    /// <summary>The Records step alone, closed without Done: cancelled at once, without asking, and nothing recorded.</summary>
    [Fact]
    public void TheRecordsStepAloneClosedWithoutDoneRecordsNothing()
    {
        var host = new FakeHost(Busy());
        using var form = new SetupWizardForm(host, firstRun: true, advanced: false, recordsOnly: true); // alone, it never asks
        _ = form.Handle;
        Assert.True(form.RecordsOnly);
        bool closed = false;
        form.FormClosed += (_, _) => closed = true;
        Assert.Contains("closeButtonEnabled = True, closeAsksFirst = False,", form.WindowStats().ToString());
        form.Close();
        Assert.True(closed);
        Assert.False(form.Finished);
        Assert.DoesNotContain("asked to close", host.Calls);
        Assert.Equal((0, 0), (host.RecordsChosen, host.Completed));
    }

    /// <summary>
    /// The Records step alone is open (it opens at each start after an upgrade until Done) and Settings, opened from the tray,
    /// saves Detailed recording and 10 days: the step shows them, so its Done applies them, not the Reports only and 7 days
    /// it opened with. A change of anything else in Settings leaves the step's own pick as it is.
    /// </summary>
    [Fact]
    public async Task ASettingsSaveWhileTheRecordsStepIsOpenAloneIsWhatItsDoneKeeps()
    {
        var host = new FakeHost(Busy());
        long now = 0;
        var session = new SetupSession(host, firstRun: false, advanced: false, clock: () => now, recordsOnly: true);
        await session.LoadAsync(CancellationToken.None);
        Assert.Equal((RecordMode.ReportsOnly, 7), (session.Records, session.Days));
        var (view, fonts) = View(session, 100, new Size(880, 586));
        using (view)
        using (fonts)
        {
            var page = (RecordsPage)view.Pages[SetupStep.Records];
            host.ApplyElsewhere(s => { s.RecordMode = RecordMode.Detailed; s.RetentionDays = 10; }); // Settings' Save
            session.FollowRecords();
            Assert.Equal((RecordMode.Detailed, 10), (session.Records, session.Days));
            Assert.True(page.Cards[RecordMode.Detailed].Selected && !page.Cards[RecordMode.ReportsOnly].Selected);
            Assert.Equal("10 days", page.DaysShown);
            Assert.Equal([1, 3, 7, 10, 14, 30, 60, 90], session.DayOptions);
            // Then 20 days, which the step does not offer either: the select offers it in 10's place (as many days as before).
            host.ApplyElsewhere(s => s.RetentionDays = 20);
            session.FollowRecords();
            Assert.Equal([1, 3, 7, 14, 20, 30, 60, 90], session.DayOptions);
            Assert.Equal("20 days", page.DaysShown);
            Assert.Equal(session.DayOptions.Select(SetupText.Days), page.DaysField.Items.Cast<object>().Select(i => i.ToString()));
            // A change of anything else: the step's own pick stays.
            session.ChooseRecords(RecordMode.ReportsOnly);
            host.ApplyElsewhere(s => s.TokenGuardEnabled = !s.TokenGuardEnabled);
            session.FollowRecords();
            Assert.Equal((RecordMode.ReportsOnly, 20), (session.Records, session.Days));
            session.ChooseRecords(RecordMode.Detailed);
            now += NextWaitTicks;
            Assert.True(await session.NextAsync(null!)); // Done
        }
        Assert.Equal("records Detailed 20", host.Calls.Last(c => c.StartsWith("records ", StringComparison.Ordinal)));
        Assert.Equal((RecordMode.Detailed, 20), (host.Running.RecordMode, host.Running.RetentionDays));
    }

    /// <summary>
    /// The setup window has its session follow a setting put into effect elsewhere (posted to its own thread), from when it is
    /// made until it is disposed.
    /// </summary>
    [Fact]
    public void TheSetupWindowFollowsASettingPutIntoEffectElsewhereUntilItIsDisposed()
    {
        var host = new FakeHost(Busy());
        var form = new SetupWizardForm(host, firstRun: false, advanced: false, recordsOnly: true);
        _ = form.Handle;
        Assert.True(host.Followed);
        host.ApplyElsewhere(s => s.RecordMode = RecordMode.Detailed);
        Application.DoEvents();
        Assert.Equal(RecordMode.Detailed, form.Session.Records);
        form.Dispose();
        Assert.False(host.Followed);
        host.ApplyElsewhere(s => s.RecordMode = RecordMode.ReportsOnly); // nothing follows it now
        Application.DoEvents();
    }

    /// <summary>
    /// A record mode or cleanup put into effect before the setup window has a handle (the window is made, then shown:
    /// Settings can save in between, and the window cannot be told on its own thread before it has one) is what the Records
    /// step shows once it has one: the window reads what is in effect as its handle is created.
    /// </summary>
    [Fact]
    public void TheSetupWindowShowsARecordChoicePutIntoEffectBeforeItHadAHandle()
    {
        var host = new FakeHost(Busy());
        using var form = new SetupWizardForm(host, firstRun: false, advanced: false, recordsOnly: true);
        Assert.False(form.IsHandleCreated);
        host.ApplyElsewhere(s => { s.RecordMode = RecordMode.Detailed; s.RetentionDays = 30; });
        Application.DoEvents();
        Assert.False(form.IsHandleCreated);
        Assert.Equal((RecordMode.ReportsOnly, 7), (form.Session.Records, form.Session.Days)); // not told yet: no handle to be told on
        _ = form.Handle;
        Assert.Equal((RecordMode.Detailed, 30), (form.Session.Records, form.Session.Days));
    }

    /// <summary>
    /// The same through the app's own wiring, end to end: the Records step alone as the tray app opens it (the setup window
    /// over the app's <see cref="SetupContext"/>, its host the real <see cref="SetupHost"/>), and Settings' Save as Settings
    /// makes it (<see cref="SettingsEditor.Commit"/>, through <see cref="PoolWatch.Engine.LiveSettings"/>). The Save reaches
    /// the open step (<see cref="SetupHost.Applied"/> passes on what LiveSettings tells), which shows it, and its Done keeps
    /// it: Detailed recording and 30 days stay in effect and saved, where a step that had not followed would have put the
    /// Reports only and 7 days it opened with back. The window lets go of LiveSettings when it is disposed.
    /// </summary>
    [Fact]
    public async Task ASettingsSaveReachesTheRecordsStepTheTrayAppOpensAndItsDoneKeepsIt()
    {
        using var f = new ChangeFixture();
        var live = f.Live(new Settings(), persist: true);
        // The app's context over an engine that is never started (no sampler, no trace, no scan): the Records step alone
        // reads the settings in effect and setup.json, and nothing of the PC.
        using var engine = new PoolWatch.Engine.MonitorEngine(new Settings(), f.Paths, f.Log, verboseCsv: false);
        var context = new SetupContext(engine, analysis: null!, actions: null!, live, new PoolWatch.Engine.Changes.ChangeLedger([], f.Records, f.Log), shell: null!, smoke: false);
        // Who LiveSettings tells of a change put into effect (its Applied event's handlers).
        int Told() => (typeof(PoolWatch.Engine.LiveSettings).GetField("Applied", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(live) as Delegate)?.GetInvocationList().Length ?? 0;
        int before = Told();
        var form = new SetupWizardForm(context, firstRun: false, advanced: false, recordsOnly: true);
        try
        {
            _ = form.Handle;
            Assert.True(form.RecordsOnly);
            Assert.Equal(before + 1, Told()); // the window follows LiveSettings through its host
            Assert.Equal((RecordMode.ReportsOnly, 7), (form.Session.Records, form.Session.Days));

            // Settings, opened from the tray while the step is open, saves Detailed recording and 30 days.
            var editor = new SettingsEditor(analysis: null!, actions: null!, live, shell: null!, context.Store);
            editor.Edit.RecordMode = RecordMode.Detailed;
            editor.Edit.RetentionDays = 30;
            Assert.True(editor.Commit(editor.ToApply()));
            Application.DoEvents(); // the step follows on the window's thread
            Assert.Equal((RecordMode.Detailed, 30), (form.Session.Records, form.Session.Days));

            // Done (it waits a moment from when the step opened): what the step shows is in effect already, and stays.
            bool done = false;
            for (var waited = System.Diagnostics.Stopwatch.StartNew(); !done && waited.Elapsed < TimeSpan.FromSeconds(10);)
            {
                var next = form.Session.NextAsync(form);
                Assert.True(next.IsCompleted); // nothing to put into effect, so nothing to wait for (and no window's thread to wait on)
                done = await next;
                if (!done) Thread.Sleep(50);
            }
            Assert.True(done);
            Assert.True(form.Finished);
            Assert.Equal((RecordMode.Detailed, 30), (live.Running.RecordMode, live.Running.RetentionDays));
            Assert.Equal((RecordMode.Detailed, 30), (f.Saved().RecordMode, f.Saved().RetentionDays));
            Assert.True(new PoolWatch.Engine.Setup.RecommendationStore(f.Paths, f.Log).RecordsChosen);
        }
        finally { form.Dispose(); }
        Assert.Equal(before, Told());
    }

    /// <summary>
    /// After an upgrade from 1.0.0-beta.2 (setup completed, no record mode chosen) the Records step opens alone at each
    /// start, the main window after it when the start asked for one, until Done records the choice; before setup was
    /// completed, setup comes first (its own Records step records the choice); never in the CI UI test.
    /// </summary>
    [Fact]
    public void AfterAnUpgradeTheRecordsStepOpensAloneAtEachStartUntilDone()
    {
        var calls = new List<string>();
        bool completed = true, chosen = false;
        var gate = new FirstRunGate(completed: () => completed, smoke: false, openSetup: () => calls.Add("setup"), showMain: () => calls.Add("main"),
            switchToAdvanced: _ => calls.Add("advanced"), switchToSimple: () => calls.Add("simple"), rememberView: _ => calls.Add("remember"),
            startUpdates: () => calls.Add("updates"), recordsChosen: () => chosen, openRecords: () => calls.Add("records"));
        gate.Start(showWindow: true);   // the installer's "Start PoolWatch now": the step, then the window once it closes
        Assert.Equal(["records", "updates"], calls);
        calls.Clear();
        gate.RecordsClosed();           // closed without Done
        Assert.Equal(["main"], calls);
        calls.Clear();
        gate.RecordsClosed();           // the window was shown once
        gate.Start(showWindow: false);  // the next start, at sign-in: the step again, and no window after it
        gate.RecordsClosed();
        Assert.Equal(["records", "updates"], calls);
        calls.Clear();
        gate.ShowWindow();              // every other way in is as usual
        Assert.Equal(["main"], calls);
        calls.Clear();
        chosen = true;                  // Done
        gate.Start(showWindow: true);
        Assert.Equal(["main", "updates"], calls);
        calls.Clear();
        (completed, chosen) = (false, false); // a new install: setup first
        gate.Start(showWindow: true);
        Assert.Equal(["setup"], calls);
        Assert.True(FirstRunGate.RecordsFirst(setupCompleted: true, recordsChosen: false, smoke: false));
        Assert.False(FirstRunGate.RecordsFirst(setupCompleted: true, recordsChosen: true, smoke: false));
        Assert.False(FirstRunGate.RecordsFirst(setupCompleted: false, recordsChosen: false, smoke: false));
        Assert.False(FirstRunGate.RecordsFirst(setupCompleted: true, recordsChosen: false, smoke: true));
        // The CI UI test opens the main window at once, whatever setup.json says.
        var smoke = new FirstRunGate(completed: () => true, smoke: true, openSetup: () => calls.Add("setup"), showMain: () => calls.Add("main"),
            switchToAdvanced: _ => { }, switchToSimple: () => { }, rememberView: _ => { }, startUpdates: () => { }, recordsChosen: () => false,
            openRecords: () => calls.Add("records"));
        calls.Clear();
        smoke.Start(showWindow: true);
        Assert.Equal(["main"], calls);
    }

    /// <summary>
    /// A full setup, fresh or run again, records a mode as it goes past its Records step, so the step alone never opens after
    /// it; and the tray app wires the gate to setup.json's record and to the step alone (read from its code).
    /// </summary>
    [Fact]
    public async Task AFullSetupRecordsTheChoiceSoTheStepAloneNeverOpensAfterIt()
    {
        var host = new FakeHost(Busy());
        long now = 0;
        var session = new SetupSession(host, firstRun: true, advanced: false, clock: () => now);
        await session.LoadAsync(CancellationToken.None);
        while (session.Flow.Step != SetupStep.Ready)
        {
            Assert.True(await session.NextAsync(null!));
            now += NextWaitTicks;
            if (session.Flow.Step <= SetupStep.Records) Assert.Equal(0, host.RecordsChosen); // only past the Records step
        }
        Assert.Equal(1, host.RecordsChosen);
        string tray = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Ui", "TrayApp.cs"));
        Assert.Contains("recordsChosen: () => Setup.Store.RecordsChosen, openRecords: OpenRecords);", tray);
        Assert.Contains("new SetupWizardForm(Setup, firstRun: false, advanced: _view?.Advanced == true, recordsOnly: true);", tray);
        Assert.Contains("_gate.RecordsClosed();", tray);
        string host2 = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Ui", "Setup", "SetupSession.cs"));
        Assert.Contains("public void MarkRecordsChosen() => _c.Store.MarkRecordsChosen(DateTime.UtcNow);", host2);
    }
}
