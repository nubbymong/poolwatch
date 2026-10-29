using System.Text.Json;
using System.Text.RegularExpressions;
using PoolWatch.Analysis;
using PoolWatch.Core;
using PoolWatch.Core.Cache;
using PoolWatch.Core.Model;
using PoolWatch.Core.Recommendations;
using PoolWatch.Core.Setup;
using PoolWatch.Core.TokenGuard;
using PoolWatch.Engine;
using PoolWatch.Engine.Setup;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// The button of each suggestion and setup-guide row calls the right thing, says what happened in plain words, and does
/// nothing in the CI UI test. Settings go to a settings.json in TEMP; everything else that would touch Windows is a fake.
/// </summary>
public sealed class RecommendationActionsTests : IDisposable
{
    private const long GB = 1L << 30;
    private readonly ChangeFixture _f = new();
    private readonly FakeSystem _system = new();
    private readonly FakeShell _shell = new();
    private readonly List<(string Title, string Text)> _instructions = new();
    private readonly LiveSettings _live;
    private readonly RecommendationStore _store;

    public RecommendationActionsTests()
    {
        _live = _f.Live();
        _store = new RecommendationStore(_f.Paths, _f.Log);
    }

    public void Dispose() => _f.Dispose();

    private RecommendationActions Actions(bool smoke = false) =>
        new(_system, _live, _shell, _store, smoke, (_, title, text) => _instructions.Add((title, text)));

    private static Recommendation Rec(RecommendationId id, string? subject = null, string? instructions = null) =>
        new(id, "A title", "Why.", "Do it", RecommendationAction.Apply, 10) { Subject = subject, Instructions = instructions };

    private Task<(bool Ok, string Message)> Run(RecommendationId id, string? subject = null, string? instructions = null) =>
        Actions().Run(Rec(id, subject, instructions), owner: null!, via: "the setup guide");

    [Fact]
    public async Task StartAtSignInRegistersTheTaskAndReadsTheFactsAgain()
    {
        var (ok, message) = await Run(RecommendationId.StartAtSignIn);
        Assert.True(ok);
        Assert.Equal("PoolWatch starts when you sign in.", message);
        Assert.Equal(["register logon task", "rescan"], _system.Calls);
        Assert.Contains("(turned on from the setup guide)", Assert.Single(_system.TimelineLines));

        _system.Calls.Clear();
        _system.LogonError = "PoolWatch did not create the logon task: it runs from a folder you can change.";
        (ok, message) = await Run(RecommendationId.StartAtSignIn);
        Assert.False(ok);
        Assert.EndsWith("it runs from a folder you can change.", message);
        Assert.Equal(["register logon task"], _system.Calls);
    }

    [Fact]
    public async Task TheHourlyClearAppliesAtOnceUnlessItCannotRun()
    {
        var (ok, _) = await Run(RecommendationId.HourlyCacheClear);
        Assert.True(ok);
        Assert.Equal((true, 1.0), (_live.Running.AutoClearEnabled, _live.Running.AutoClearEveryHours));
        Assert.True(_f.Saved().AutoClearEnabled);

        _live.Apply("off", s => s.AutoClearEnabled = false);
        _system.OwnClearTask = true;
        (ok, var message) = await Run(RecommendationId.HourlyCacheClear);
        Assert.False(ok);
        Assert.Contains("Replace it instead", message);
        _system.OwnClearTask = false;
        _system.Elevated = false;
        (ok, message) = await Run(RecommendationId.HourlyCacheClear);
        Assert.Equal((false, "Clearing the cache needs PoolWatch to run as administrator."), (ok, message));
        Assert.False(_live.Running.AutoClearEnabled);
    }

    [Fact]
    public async Task ReplacingTheClearTaskReadsTheStatusAgainEvenWhenItFails()
    {
        _system.Removed = [new CacheHelper.LegacyTaskInfo(@"\Empty standby list", "SYSTEM", "RAMMap64.exe -Ew", "hourly", true)];
        var (ok, message) = await Run(RecommendationId.ReplaceClearTask);
        Assert.True(ok);
        Assert.Equal("Replaced your clear task \"Empty standby list\". PoolWatch clears the cache every hour now.", message);
        Assert.Equal(["replace clear tasks via the setup guide", "refresh clear status", "rescan"], _system.Calls);

        _system.Calls.Clear();
        _system.ReplaceError = "Your clear task \"Empty standby list\" could not be removed: access denied";
        (ok, message) = await Run(RecommendationId.ReplaceClearTask);
        Assert.Equal((false, _system.ReplaceError), (ok, message));
        Assert.Equal(["replace clear tasks via the setup guide", "refresh clear status", "rescan"], _system.Calls);
    }

    [Fact]
    public async Task TheTokenLeakGuardStartsItsCheckOnlyWhenItWasOff()
    {
        var (ok, message) = await Run(RecommendationId.TokenLeakGuard);
        Assert.Equal((true, "The token-leak guard is on."), (ok, message));
        Assert.True(_live.Running.TokenGuardEnabled);
        Assert.Equal(1, _shell.GuardStarts);

        await Run(RecommendationId.TokenLeakGuard);       // already on: a second sign-in check would start
        Assert.Equal(1, _shell.GuardStarts);

        _live.Apply("off", s => s.TokenGuardEnabled = false);
        _shell.GuardProtects = false;
        (ok, message) = await Run(RecommendationId.TokenLeakGuard);
        Assert.False(ok);
        Assert.Contains("Its notice says what to do.", message);
    }

    [Fact]
    public async Task TheLeakCheckKeepsItsVerdictForTheSessionButNotACheckThatCouldNotRun()
    {
        var actions = Actions();
        _system.Verdict = null;
        Assert.False((await actions.Run(Rec(RecommendationId.CheckTokenLeak), null!)).Ok);
        Assert.Null(actions.LeakTestNow);                 // the Simple view can still offer the check
        _system.Verdict = LeakVerdict.Leak;
        Assert.Equal((true, "This Windows build has the token leak."), await actions.Run(Rec(RecommendationId.CheckTokenLeak), null!));
        Assert.Equal(LeakVerdict.Leak, actions.LeakTestNow);
    }

    [Fact]
    public async Task ServicesInstructionsAndReportOpenWhatTheySay()
    {
        Assert.True((await Run(RecommendationId.ReviewServices)).Ok);
        Assert.Equal([1], _shell.AdvancedTabs);

        Assert.True((await Run(RecommendationId.DefenderDevDrive, instructions: "    fsutil devdrv query D:")).Ok);
        Assert.Equal(("A title", "    fsutil devdrv query D:"), Assert.Single(_instructions));

        Assert.True((await Run(RecommendationId.ReportIncident, subject: "20261028-0412-Reff")).Ok);
        Assert.Equal([Path.Combine(_system.IncidentsDir, "20261028-0412-Reff")], _shell.Reports);
        Assert.True(RecommendationRules.Hidden(_store.Dismissed, "ReportIncident:20261028-0412-Reff", DateTime.UtcNow)); // that card goes
        Assert.False((await Run(RecommendationId.ReportIncident, subject: @"..\..\Windows")).Ok);                          // never a path
        Assert.Single(_shell.Reports);
    }

    [Fact]
    public async Task TheShortcutReadsTheFactsAgainForItsIconAfterARestart()
    {
        _system.FactsNow = Facts() with { ChatGptInstalled = true, ChatGptIconPath = null };  // from the cache: no icon path
        _system.RescanFacts = Facts() with { ChatGptInstalled = true, ChatGptIconPath = @"C:\Program Files\WindowsApps\OpenAI.ChatGPT\app\ChatGPT.exe" };
        var (ok, _) = await Run(RecommendationId.ChatGptShortcut);
        Assert.True(ok);
        Assert.Equal(["rescan", @"shortcut with C:\Program Files\WindowsApps\OpenAI.ChatGPT\app\ChatGPT.exe via the setup guide"], _system.Calls);
        Assert.Contains("ChatGPT (no shader cache)", Assert.Single(_system.TimelineLines));
    }

    [Fact]
    public async Task AFailedChangeIsSaidPlainlyAndChangesNothing()
    {
        Directory.CreateDirectory(_f.Paths.SettingsFile + ".tmp"); // settings.json cannot be written
        var (ok, message) = await Run(RecommendationId.HourlyCacheClear);
        Assert.False(ok);
        Assert.StartsWith("That did not work: ", message);
        Assert.False(_live.Running.AutoClearEnabled);
        Assert.Empty(_system.TimelineLines);
    }

    [Fact]
    public async Task TheTestRunRefusesEveryButton()
    {
        var actions = Actions(smoke: true);
        foreach (var id in Enum.GetValues<RecommendationId>())
            Assert.Equal((false, RecommendationActions.Refused), await actions.Run(Rec(id, "x", "y"), null!));
        Assert.Equal((false, RecommendationActions.Refused), await actions.CheckTokenLeak(CancellationToken.None));
        Assert.Equal((false, RecommendationActions.Refused), await actions.TestClear(CancellationToken.None));
        Assert.Empty(_system.Calls);
        Assert.Empty(_system.TimelineLines);
        Assert.Equal((0, 0, 0), (_shell.GuardStarts, _shell.AdvancedTabs.Count, _shell.Reports.Count));
        Assert.Empty(_instructions);
        Assert.False(File.Exists(_f.Paths.SettingsFile));
    }

    [Fact]
    public async Task TheTestClearSaysWhatItFreedAndFromWhichFileSystem()
    {
        _system.Clear = new ClearRecord
        {
            TimeUtc = DateTime.UtcNow, Mode = ClearMode.Standby, Trigger = ClearTrigger.Manual, Before = new PoolTotals(3 * GB, 2 * GB, 0, 0),
            After = new PoolTotals(2 * GB, (ulong)(1.8 * GB), 0, 0),
            TagsBefore = [(PoolTag.Parse("Ntff"), GB)], TagsAfter = [(PoolTag.Parse("Ntff"), 100L << 20)],
        };
        Assert.Equal((true, "On this PC, clearing the cache freed 1.2 GB of the kernel pool, mostly NTFS file records."), await Actions().TestClear(CancellationToken.None));

        _system.Clear = new ClearRecord { TimeUtc = DateTime.UtcNow, Mode = ClearMode.Standby, Trigger = ClearTrigger.Manual, Before = new PoolTotals(GB, GB, 0, 0), Error = "the purge returned 0xC0000061" };
        Assert.Equal((false, "The clear did not run: the purge returned 0xC0000061."), await Actions().TestClear(CancellationToken.None));
        _system.Clear = new ClearRecord { TimeUtc = DateTime.UtcNow, Mode = ClearMode.Standby, Trigger = ClearTrigger.Manual, Before = new PoolTotals(GB, GB, 0, 0), Error = "Access is denied." };
        Assert.Equal((false, "The clear did not run: Access is denied."), await Actions().TestClear(CancellationToken.None)); // one full stop
        _system.Clear = null;
        Assert.False((await Actions().TestClear(CancellationToken.None)).Ok);
        _system.Elevated = false;
        Assert.Equal((false, "Clearing the cache needs PoolWatch to run as administrator."), await Actions().TestClear(CancellationToken.None));
    }

    // ------------------------------------------------------------------ whether to suggest the hourly clear

    private static EnvironmentFacts Facts(params VolumeFact[] volumes) => new()
    {
        ScannedUtc = DateTime.UtcNow, WindowsName = "Windows 11 Pro", Build = "26200.7000", Architecture = "x64", Volumes = volumes,
    };

    private static readonly VolumeFact Ntfs = new(@"C:\", "NTFS", false, null), DevDrive = new(@"D:\", "ReFS", true, true);

    private static Recommendation Hourly() => RecommendationRules.FirstRun(new RecommendationInputs { NowUtc = DateTime.UtcNow, Facts = Facts() })
        .Single(r => r.Id == RecommendationId.HourlyCacheClear);

    private static ClearEvidence Evidence(bool? helps, double freedGb = 0, string tag = "Ntff")
    {
        if (helps is null) return new(null, null);
        var r = new ClearRecord
        {
            TimeUtc = DateTime.UtcNow, Mode = ClearMode.Standby, Trigger = ClearTrigger.Manual, Before = new PoolTotals(4 * GB, 2 * GB, 0, 0),
            After = new PoolTotals((ulong)((4 - freedGb) * GB), 2 * GB, 0, 0), TagsBefore = [(PoolTag.Parse(tag), 4 * GB)], TagsAfter = [(PoolTag.Parse(tag), (long)((4 - freedGb) * GB))],
        };
        return new(helps, r);
    }

    [Fact]
    public void WhatEachClearFreedSurvivesARestartButNotForAClearThatDidNotRun()
    {
        var measured = new ClearRecord
        {
            TimeUtc = DateTime.UtcNow, Mode = ClearMode.Standby, Trigger = ClearTrigger.Escalation, Before = new PoolTotals(6 * GB, 5 * GB, 0, 0),
            After = new PoolTotals(3 * GB, 4 * GB, 0, 0),
            TagsBefore = [(PoolTag.Parse("Reff"), 3 * GB), (PoolTag.Parse("Ntff"), 200L << 20)], TagsAfter = [(PoolTag.Parse("Reff"), GB), (PoolTag.Parse("Ntff"), 200L << 20)],
        };
        static CacheClearer.Saved ThroughJson(CacheClearer.Saved s) => JsonSerializer.Deserialize<CacheClearer.Saved>(JsonSerializer.Serialize(s))!;
        var back = CacheClearer.FromSaved(ThroughJson(CacheClearer.ToSaved(measured)));
        Assert.Equal((measured.TimeUtc, ClearMode.Standby, ClearTrigger.Escalation, 4 * GB), (back.TimeUtc, back.Mode, back.Trigger, back.Freed));
        Assert.Equal([PoolTag.Parse("Reff")], back.TagsBefore.Select(t => t.Tag));            // only what it freed is kept
        Assert.Equal(FileRecords.ReFS, ClearEvidence.MostlyFrom(back));

        // A clear that did not run freed nothing: no tags are kept for it, whatever was read before it.
        var failed = new ClearRecord
        {
            TimeUtc = DateTime.UtcNow, Mode = ClearMode.Standby, Trigger = ClearTrigger.Manual, Before = new PoolTotals(6 * GB, 5 * GB, 0, 0),
            TagsBefore = [(PoolTag.Parse("Reff"), 3 * GB)], Error = "the purge returned 0xC0000061",
        };
        Assert.Null(CacheClearer.ToSaved(failed).Tags);
        var failedBack = CacheClearer.FromSaved(ThroughJson(CacheClearer.ToSaved(failed)));
        Assert.Equal(("the purge returned 0xC0000061", 0L), (failedBack.Error, failedBack.FreedFrom(ClearEvidence.ReFsTags)));

        // An older file has no tags; a tag that is not a pool tag is left out.
        var old = CacheClearer.FromSaved(JsonSerializer.Deserialize<CacheClearer.Saved>("""
            {"TimeUtc":"2026-10-27T01:58:00Z","Mode":"LowPriority","Trigger":"Schedule","PoolBefore":12884901888,"PoolAfter":12884901888,
             "PagedBefore":6904159928,"PagedAfter":6904159928,"StandbyBefore":0,"StandbyAfter":0,"Error":null}
            """)!);
        Assert.Equal((ClearMode.LowPriority, 0L, 0), (old.Mode, old.Freed, old.TagsBefore.Count));
        var bad = CacheClearer.ToSaved(measured) with { Tags = [new("not a tag", GB, 0), new("Reff", 3 * GB, GB)] };
        Assert.Equal([PoolTag.Parse("Reff")], CacheClearer.FromSaved(bad).TagsBefore.Select(t => t.Tag));
    }

    [Fact]
    public void OnNtfsOnlyTheGuideOffersACheckFirst()
    {
        var (row, check) = ClearAdvice.ForGuide(Hourly(), Facts(Ntfs), Evidence(null));
        Assert.True(check);
        Assert.Equal(ClearAdvice.CheckButton, row.Button);
        Assert.StartsWith("PoolWatch found no ReFS drive or Dev Drive on this PC.", row.Why);
        Assert.True(row.Why.Length <= RecommendationRules.MaxWhy);
        Assert.Equal(row.Why, ClearAdvice.ForGuide(Hourly(), Facts(), Evidence(null)).Row.Why);                  // also when no drive was listed

        (row, check) = ClearAdvice.ForGuide(Hourly(), Facts(Ntfs), Evidence(true, 1.5));
        Assert.False(check);
        Assert.Equal(("Turn on", "On this PC, clearing the cache freed 1.5 GB of the kernel pool, mostly NTFS file records."), (row.Button, row.Why));

        (row, check) = ClearAdvice.ForGuide(Hourly(), Facts(Ntfs), Evidence(false, 0.05));
        Assert.Equal((RecommendationState.Info, "", false), (row.State, row.Button, check));
        Assert.Contains("an hourly clear is not needed now", row.Why);
    }

    [Fact]
    public void WithAReFsDriveTheHourlyClearIsOfferedAndSaysWhy()
    {
        var (row, check) = ClearAdvice.ForGuide(Hourly(), Facts(Ntfs, DevDrive), Evidence(null));
        Assert.False(check);
        Assert.Equal("Turn on", row.Button);
        Assert.StartsWith("This PC has a ReFS drive (D:).", row.Why);
        Assert.True(row.Why.Length <= RecommendationRules.MaxWhy);
        // A row that is not the hourly offer is left as it is.
        var done = Hourly() with { State = RecommendationState.Done };
        Assert.Equal((done, false), ClearAdvice.ForGuide(done, Facts(Ntfs), Evidence(null)));
    }

    [Fact]
    public void TheSimpleViewSuggestsTheHourlyClearOnlyWhereItHelps()
    {
        var signIn = new Recommendation(RecommendationId.StartAtSignIn, "Start PoolWatch when you sign in", "So it watches.", "Turn on", RecommendationAction.Apply, 40);
        var report = new Recommendation(RecommendationId.ReportIncident, "Report what PoolWatch found", "It grew.", "Report", RecommendationAction.ReportIssue, 50);
        var services = new Recommendation(RecommendationId.ReviewServices, "Review the background services", "Busy.", "Review", RecommendationAction.OpenServices, 60);
        IReadOnlyList<Recommendation> current = [Hourly(), signIn, report, services];

        Assert.Equal([signIn, report, services], ClearAdvice.ForSimpleView(current, Facts(Ntfs), Evidence(null)));       // NTFS, nothing shown yet
        Assert.Equal([signIn, report, services], ClearAdvice.ForSimpleView(current, Facts(Ntfs), Evidence(false, 0.05)));
        Assert.Equal([Hourly(), signIn, report], ClearAdvice.ForSimpleView(current, Facts(DevDrive), Evidence(null)));
        var helped = ClearAdvice.ForSimpleView(current, Facts(Ntfs), Evidence(true, 2));
        Assert.Equal(3, helped.Count);
        Assert.StartsWith("On this PC, clearing the cache freed 2.0 GB", helped[0].Why);
    }

    // ------------------------------------------------------------------ the setup guide's rows

    private static RecommendationInputs Inputs(EnvironmentFacts facts, bool autoClear = false, bool guard = false) =>
        new() { NowUtc = DateTime.UtcNow, Facts = facts, AutoClearOn = autoClear, TokenGuardOn = guard };

    [Fact]
    public void OnANewPcTheGuideOffersEachFirstRunStepAndChecksForTheLeak()
    {
        var facts = Facts(Ntfs) with { InstalledInProgramFiles = true, TokenLeak = TokenLeakStatus.NeedsTest };
        var rows = SetupGuideForm.Rows(Inputs(facts), Evidence(null), leakNotChecked: null);
        Assert.Equal([RecommendationId.StartAtSignIn, RecommendationId.HourlyCacheClear, RecommendationId.TokenLeakGuard], rows.Select(r => r.Row.Id));
        Assert.Equal(("Turn on", false), (rows[0].Row.Button, rows[0].TestClear));
        Assert.Equal((ClearAdvice.CheckButton, true), (rows[1].Row.Button, rows[1].TestClear));      // NTFS only: check first
        Assert.Equal(RecommendationState.Checking, rows[2].Row.State);

        // The check could not run: the row says why, once, in this window only.
        const string why = "The check could not run now: another check is running, or something on this PC already works around the leak.";
        var notChecked = SetupGuideForm.Rows(Inputs(facts), Evidence(null), leakNotChecked: why)[2].Row;
        Assert.Equal((RecommendationState.Info, why, ""), (notChecked.State, notChecked.Why, notChecked.Button));
        // A verdict from earlier in this session stands.
        var clean = SetupGuideForm.Rows(Inputs(facts) with { LeakTestNow = LeakVerdict.Clean }, Evidence(null), leakNotChecked: why)[2].Row;
        Assert.Equal("PoolWatch's check found no token leak on this Windows build.", clean.Why);
    }

    [Fact]
    public void OpenedAgainTheGuideShowsWhatIsAlreadySetUp()
    {
        var facts = Facts(Ntfs, DevDrive) with { InstalledInProgramFiles = true, LogonTaskExists = true, TokenLeak = TokenLeakStatus.Leaks };
        var rows = SetupGuideForm.Rows(Inputs(facts, autoClear: true, guard: true), Evidence(true, 3.5, "Reff"), leakNotChecked: null);
        Assert.All(rows, r => Assert.Equal(RecommendationState.Done, r.Row.State));
        // The hourly clear confirms what clearing freed here.
        Assert.EndsWith("On this PC, clearing the cache freed 3.5 GB of the kernel pool, mostly ReFS file records.", rows[1].Row.Why);

        // With a ReFS drive the hourly clear applies, so a later clear that freed little never says it is not needed.
        var refsLittle = SetupGuideForm.Rows(Inputs(facts, autoClear: true, guard: true), Evidence(false, 0.3, "Reff"), leakNotChecked: null)[1].Row;
        Assert.DoesNotContain("not needed", refsLittle.Why);
        // With NTFS only, it is said: there, what the clears free decides.
        var ntfsLittle = SetupGuideForm.Rows(Inputs(Facts(Ntfs) with { InstalledInProgramFiles = true }, autoClear: true), Evidence(false, 0.3), leakNotChecked: null)[1].Row;
        Assert.Contains("an hourly clear is not needed now", ntfsLittle.Why);
    }

    private static IEnumerable<T> All<T>(Control root) where T : Control =>
        root.Controls.Cast<Control>().SelectMany(c => (c is T t ? [t] : Array.Empty<T>()).Concat(All<T>(c)));

    [Theory]
    [InlineData(9f)]
    [InlineData(13.5f)]   // half as big again, as at 150%: the guide lays out from its fonts, in the same 620 px here
    public void TheGuideFitsItsWidthAtTheUsualAndALargerTextSize(float points)
    {
        // Every kind of row: an offer, the test clear running (the longest button), the token-leak offer with what you will
        // notice (the longest texts), a row with an error, and the services line.
        var facts = Facts(Ntfs) with { InstalledInProgramFiles = true, TokenLeak = TokenLeakStatus.Leaks, OptionalServicesRunning = 2 };
        var rows = SetupGuideForm.Rows(Inputs(facts), Evidence(null), leakNotChecked: null);
        Assert.Equal([RecommendationId.StartAtSignIn, RecommendationId.HourlyCacheClear, RecommendationId.TokenLeakGuard, RecommendationId.ReviewServices],
            rows.Select(r => r.Row.Id));
        using var host = new Form();
        using var font = new Font("Segoe UI", points);
        var page = new GuidePage(font);
        host.Controls.Add(page);
        page.Fill(new GuideState(facts.PlainLines(), rows, new FakeUpdates(), new HashSet<string> { rows[1].Row.Key },
            new Dictionary<string, string> { [rows[0].Row.Key] = "PoolWatch could not set itself to start when you sign in. It runs from a folder you can change." }));

        Assert.Empty(page.LayoutProblems());
        Assert.Equal(["Turn on", ClearAdvice.CheckButton, "Turn on", "Done"], All<Button>(page).Select(b => b.Text));
        Assert.Contains(All<Label>(page), l => l.Text == TokenGuardWords.Details);
    }

    [Fact]
    public void TheUpdateSwitchShowsWhatIsInEffectAfterAChangeThatDidNotWork()
    {
        using var host = new Form();
        using var font = new Font("Segoe UI", 9f);
        var page = new GuidePage(font);
        host.Controls.Add(page);
        var updates = new FakeUpdates();
        page.Fill(new GuideState([], [], updates, new HashSet<string>(), new Dictionary<string, string>()));
        page.UpdatesSwitched += on => { try { updates.Enabled = on; } catch (IOException) { } }; // as the window does, with its message
        var box = Assert.Single(All<CheckBox>(page));

        updates.Fails = true;
        box.Checked = false;
        Assert.Equal((true, true), (box.Checked, updates.Enabled));       // not changed: the switch says so
        updates.Fails = false;
        box.Checked = false;
        Assert.Equal((false, false), (box.Checked, updates.Enabled));
    }

    [Fact]
    public void AHeadingShowsOnlyWithSomethingUnderIt()
    {
        using var host = new Form();
        using var font = new Font("Segoe UI", 9f);
        var page = new GuidePage(font);
        host.Controls.Add(page);
        page.Fill(new GuideState([], [], null, new HashSet<string>(), new Dictionary<string, string>()));
        Assert.DoesNotContain(All<Label>(page), l => l.Text is "Suggested for this PC" or "Worth a look later");
        page.Fill(new GuideState([], [], new FakeUpdates(), new HashSet<string>(), new Dictionary<string, string>()));
        Assert.Contains(All<Label>(page), l => l.Text == "Suggested for this PC");   // the update switch alone has it
    }

    [Fact]
    public void TheTextsArePlainWords()
    {
        var setting = new Regex(@"\b[A-Z][a-z]+(?:[A-Z][a-z]+)+(Enabled|Hours|Gb|Minutes)\b|AutoClear|TokenGuard", RegexOptions.CultureInvariant);
        string[] texts =
        [
            ClearAdvice.ForGuide(Hourly(), Facts(Ntfs), Evidence(null)).Row.Why, ClearAdvice.ForGuide(Hourly(), Facts(DevDrive), Evidence(null)).Row.Why,
            TokenGuardWords.Why, TokenGuardWords.Details, RecommendationActions.Refused, ClearAdvice.CheckButton,
        ];
        Assert.All(texts, t => Assert.DoesNotMatch(setting, t));
        Assert.All(texts, t => Assert.DoesNotContain("!", t));
        Assert.All(texts, t => Assert.DoesNotContain("kernel memory", t));   // the user-facing term is "kernel pool"
    }

    [Fact]
    public void TheGuardsOfferSaysItIsTurnedOnAgainEachTimePoolWatchStarts()
    {
        // The guard is applied each time the tray app starts (from the sign-in task or by hand), not at a sign-in as such.
        Assert.Contains("PoolWatch turns it back on each time it starts", TokenGuardWords.Undo);
        Assert.DoesNotContain("sign in", TokenGuardWords.Undo.Replace("until you sign out", ""));
    }

    [Fact]
    public void TheInstructionsCopyOnlyTheirCommands()
    {
        string text = DefenderAdvice.Text([@"D:\"]);
        string[] commands = InstructionsDialog.Commands(text).Split(Environment.NewLine);
        Assert.Equal(["fsutil devdrv query D:", "fsutil devdrv trust D:", "Get-MpPreference | Select-Object PerformanceModeStatus",
            "Set-MpPreference -PerformanceModeStatus Enabled"], commands);
        Assert.DoesNotContain(commands, c => c.Contains("ExclusionPath")); // the bigger step is described, never copied
        using var dialog = new InstructionsDialog("Microsoft Defender's performance mode", text);
        Assert.Equal("Microsoft Defender's performance mode", dialog.Text);
    }

    // ------------------------------------------------------------------ fakes

    private sealed class FakeSystem : ISetupSystem
    {
        public readonly List<string> Calls = new(), TimelineLines = new();
        public EnvironmentFacts? FactsNow, RescanFacts;
        public string? LogonError, ReplaceError;
        public IReadOnlyList<CacheHelper.LegacyTaskInfo> Removed = [];
        public LeakVerdict? Verdict;
        public ClearRecord? Clear;

        public EnvironmentFacts? Facts => FactsNow;
        public Task<EnvironmentFacts> Rescan() { Calls.Add("rescan"); return Task.FromResult(RescanFacts ?? Facts()); }
        public bool Elevated { get; set; } = true;
        public bool OwnClearTask { get; set; }
        public void RefreshClearStatus() => Calls.Add("refresh clear status");
        public string? RegisterLogonTask() { Calls.Add("register logon task"); return LogonError; }
        public (IReadOnlyList<CacheHelper.LegacyTaskInfo> Removed, string? Error) ReplaceClearTasks(LiveSettings live, string via)
        {
            Calls.Add("replace clear tasks via " + via);
            return (Removed, ReplaceError);
        }
        public Task<LeakVerdict?> CheckTokenLeak(CancellationToken stop) { Calls.Add("check token leak"); return Task.FromResult(Verdict); }
        public (bool Ok, string Message) CreateChatGptShortcut(string? iconPath, string via)
        {
            Calls.Add($"shortcut with {iconPath} via {via}");
            return (true, "Created the shortcut \"ChatGPT (no shader cache)\" on your desktop.");
        }
        public ClearRecord? StartClear() { Calls.Add("clear"); return Clear; }
        public string IncidentsDir => @"C:\ProgramData\PoolWatch\incidents";
        public void Timeline(string line) => TimelineLines.Add(line);
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

    private sealed class FakeShell : IShell
    {
        public readonly List<int> AdvancedTabs = new();
        public readonly List<string?> Reports = new();
        public int GuardStarts;
        public bool GuardProtects = true;

        public void ShowAdvanced(int tab) => AdvancedTabs.Add(tab);
        public void ShowActiveChanges() { }
        public void ShowReport(string? incidentDir) => Reports.Add(incidentDir);
        public Task<bool> StartTokenGuard() { GuardStarts++; return Task.FromResult(GuardProtects); }
    }
}
