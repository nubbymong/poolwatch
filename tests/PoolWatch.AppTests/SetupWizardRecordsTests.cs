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
                    "Nothing is recorded until the pool grows.", // the agreed truth fix of the approved "Nothing is written until the pool grows."
                    "Detailed recording",
                    "The reports, plus a record of everything PoolWatch watches, all day: the pool and its memory tags, programs and their handles, services, and every program started.",
                    "Useful when a leak builds up slowly over days, to compare a good day with a bad one, or when a developer asks for more than a report.",
                    "Up to about 60 MB a day on a PC doing heavy agentic coding.",
                    "Clean up reports and records after", "You can change both later in Settings.",
                    "Both stay on this PC and are never sent. They hold full command lines, which can contain passwords, so look before you share one.",
                ], page.Texts());
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
