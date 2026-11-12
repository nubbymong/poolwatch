using System.Text.Json;
using System.Text.RegularExpressions;
using PoolWatch.Analysis;
using PoolWatch.Core;
using PoolWatch.Core.Cache;
using PoolWatch.Core.Knowledge;
using PoolWatch.Core.Model;
using PoolWatch.Core.Recommendations;
using PoolWatch.Core.Setup;
using PoolWatch.Core.TokenGuard;
using PoolWatch.Engine;
using PoolWatch.Engine.Setup;
using PoolWatch.Ui;
using PoolWatch.Ui.Setup;

namespace PoolWatch.AppTests;

/// <summary>
/// The button of each suggestion (setup runs the same buttons) calls the right thing, says what happened in plain words,
/// and does nothing in the CI UI test. Settings go to a settings.json in TEMP; everything else that would touch Windows is a
/// fake.
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

    // ------------------------------------------------------------------ the token-leak watch

    /// <summary>Windows 11 25H2 where the check can run: the guard off, the live setting not 0, no verdict for this build.</summary>
    private static RecommendationInputs Watching(string build = "26200.7000", string? savedFor = "26100.4000") => new()
    {
        NowUtc = DateTime.UtcNow, NowMs = Moment.Now().Ms, Facts = Facts() with { Build = build, TokenLeak = TokenLeakStatus.NeedsTest }, SavedVerdictBuild = savedFor,
        SavedVerdict = GuardResult.CleanOnce, AutoClearOn = true,
    };

    [Fact]
    public async Task AfterAWindowsUpdateTheWatchChecksAgainByItselfOncePerBuildAndNeverTurnsTheGuardOn()
    {
        var actions = Actions();
        _system.Verdict = LeakVerdict.Leak;
        var check = actions.WatchTokenLeak(Watching());
        Assert.NotNull(check);
        Assert.Equal((true, "This Windows build has the token leak."), await check);
        Assert.Equal(["check token leak"], _system.Calls);
        Assert.Equal(LeakVerdict.Leak, actions.LeakTestNow);
        // It changed nothing: the guard stays off until the owner turns it on, and no notice or timeline line was written.
        Assert.False(_live.Running.TokenGuardEnabled);
        Assert.Equal(0, _shell.GuardStarts);
        Assert.Empty(_system.TimelineLines);
        // The leak it found is offered as a card (which says what you will notice and how to undo it).
        var offer = RecommendationRules.Current(Watching() with { LeakTestNow = actions.LeakTestNow }).Single();
        Assert.Equal(RecommendationId.TokenLeakGuard, offer.Id);
        Assert.StartsWith("PoolWatch's check found that this Windows build leaks", offer.Why);
        // Once a verdict is in for this session, nothing more is checked.
        Assert.Null(actions.WatchTokenLeak(Watching() with { LeakTestNow = actions.LeakTestNow }));
        Assert.Single(_system.Calls);
    }

    [Fact]
    public async Task ACheckThatCouldNotRunIsNotRepeatedForTheSameBuild()
    {
        var actions = Actions();
        _system.Verdict = null; // another check running, or the live setting 0
        Assert.False((await actions.WatchTokenLeak(Watching())!).Ok);
        Assert.Null(actions.WatchTokenLeak(Watching())); // the next minute: not again for this build
        _system.Verdict = LeakVerdict.Clean;
        Assert.NotNull(actions.WatchTokenLeak(Watching(build: "26200.7100"))); // Windows updated again: once more
        Assert.Equal(["check token leak", "check token leak"], _system.Calls);
    }

    [Fact]
    public void TheWatchWaitsForAWindowsUpdateAndNeverRunsInTheCiTest()
    {
        var actions = Actions();
        Assert.Null(actions.WatchTokenLeak(Watching(savedFor: "26200.7000")));                            // a verdict for this build
        Assert.Null(actions.WatchTokenLeak(Watching() with { TokenGuardOn = true }));                      // the guard is on
        Assert.Null(actions.WatchTokenLeak(Watching() with { Facts = Facts() with { TokenLeak = TokenLeakStatus.NotAffected } }));
        Assert.Null(actions.WatchTokenLeak(Watching() with { Facts = Facts() with { TokenLeak = TokenLeakStatus.WorkedAroundAlready } }));
        Assert.Null(actions.WatchTokenLeak(Watching() with { Facts = null }));
        Assert.Null(Actions(smoke: true).WatchTokenLeak(Watching()));
        Assert.Empty(_system.Calls);
        // No check ever gave a verdict (setup's could not run): the watch runs it.
        Assert.NotNull(actions.WatchTokenLeak(Watching(savedFor: null)));
    }

    [Fact]
    public void TheTraysWatchLooksInOnceSetupWasDoneAndSaysSoOnlyWhenItFoundTheLeakWithTheGuardOff()
    {
        // The tray looks in every half hour, also while the window is hidden, once setup was completed and where the check
        // could run at all.
        Assert.Equal(TimeSpan.FromMinutes(30), TrayApp.TokenLeakWatchEvery);
        Assert.True(TrayApp.WatchFromTray(setupCompleted: true, TokenLeakStatus.NeedsTest));
        Assert.False(TrayApp.WatchFromTray(setupCompleted: false, TokenLeakStatus.NeedsTest));
        Assert.False(TrayApp.WatchFromTray(setupCompleted: true, TokenLeakStatus.WorkedAroundAlready));
        Assert.False(TrayApp.WatchFromTray(setupCompleted: true, null));
        // A check it started found the leak and the guard is off: the tray says so, and a click opens the window's offer.
        Assert.Equal(TrayApp.TokenLeakFoundNotice, TrayApp.WatchNotice(LeakVerdict.Leak, guardOn: false));
        Assert.Null(TrayApp.WatchNotice(LeakVerdict.Leak, guardOn: true));
        Assert.Null(TrayApp.WatchNotice(LeakVerdict.Clean, guardOn: false));
        Assert.Null(TrayApp.WatchNotice(LeakVerdict.Unclear, guardOn: false));
        Assert.Null(TrayApp.WatchNotice(null, guardOn: false));
        Assert.True(TrayApp.TokenLeakFoundNotice.Length <= ProfileAtStart.MaxNotice);
        // The click shows the guard where the view offers it: the Simple view's card, or Settings (its switch) in the
        // Advanced view, which has no card.
        Assert.Equal(TokenGuardOffer.Card, MainForm.TokenGuardOfferIn(advanced: false));
        Assert.Equal(TokenGuardOffer.Settings, MainForm.TokenGuardOfferIn(advanced: true));
    }

    [Fact]
    public void TheTraysWatchBeatIsStartedEveryHalfHourButNeverInTheCiUiTest()
    {
        int ticks = 0;
        using (var beat = TrayApp.WatchTimer(smoke: false, () => ticks++))
        {
            Assert.True(beat.Enabled);
            Assert.Equal((int)TrayApp.TokenLeakWatchEvery.TotalMilliseconds, beat.Interval);
            beat.Stop();
        }
        using (var none = TrayApp.WatchTimer(smoke: true, () => ticks++))
            Assert.False(none.Enabled);
        Assert.Equal(0, ticks);
    }

    [Fact]
    public async Task SettingsMakesTheCodexShortcutAgainButNotInTheCiTest()
    {
        _system.FactsNow = Facts() with { ChatGptInstalled = true, ChatGptIconPath = @"C:\Program Files\WindowsApps\OpenAI.ChatGPT\app\ChatGPT.exe" };
        var (ok, message) = await Actions().CreateCodexShortcut("Settings");
        Assert.True(ok);
        Assert.Contains("ChatGPT (no shader cache)", message);
        Assert.Equal(["shortcut with C:\\Program Files\\WindowsApps\\OpenAI.ChatGPT\\app\\ChatGPT.exe via Settings"], _system.Calls);
        Assert.Equal((false, RecommendationActions.Refused), await Actions(smoke: true).CreateCodexShortcut("Settings"));
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
        Assert.True(RecommendationRules.Hidden(_store.Dismissed, "ReportIncident:20261028-0412-Reff", Moment.Now().Ms)); // that card goes
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
        Assert.Empty(_system.Calls);
        Assert.Empty(_system.TimelineLines);
        Assert.Equal((0, 0, 0), (_shell.GuardStarts, _shell.AdvancedTabs.Count, _shell.Reports.Count));
        Assert.Empty(_instructions);
        Assert.False(File.Exists(_f.Paths.SettingsFile));
    }

    /// <summary>
    /// The first-run setup runs no test clear, and nothing else offered one: the test-clear button and the guide's hourly
    /// row that offered it (ClearAdvice), the test clear itself (RecommendationActions) and the call it started its clear
    /// through (ISetupSystem) are gone.
    /// </summary>
    [Fact]
    public void TheTestClearNothingOfferedIsGone()
    {
        const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                                                   System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;
        Assert.Null(typeof(ClearAdvice).GetField("CheckButton", all));
        Assert.Null(typeof(ClearAdvice).GetMethod("ForGuide", all));
        Assert.Null(typeof(RecommendationActions).GetMethod("TestClear", all));
        Assert.Null(typeof(ISetupSystem).GetMethod("StartClear", all));
    }

    // ------------------------------------------------------------------ whether to suggest the hourly clear

    private static EnvironmentFacts Facts(params VolumeFact[] volumes) => new()
    {
        ScannedUtc = DateTime.UtcNow, WindowsName = "Windows 11 Pro", Build = "26200.7000", Architecture = "x64", Volumes = volumes,
    };

    private static readonly VolumeFact Ntfs = new(@"C:\", "NTFS", false, null), DevDrive = new(@"D:\", "ReFS", true, true);

    private static Recommendation Hourly() => RecommendationRules.FirstRun(new RecommendationInputs { NowUtc = DateTime.UtcNow, NowMs = Moment.Now().Ms, Facts = Facts() })
        .Single(r => r.Id == RecommendationId.HourlyCacheClear);

    private static ClearEvidence Evidence(bool? helps, double freedGb = 0, string tag = "Ntff")
    {
        if (helps is null) return new(null, null);
        var r = new ClearRecord
        {
            TimeUtc = DateTime.UtcNow, TickMs = AppClocks.Ms(DateTime.UtcNow), Mode = ClearMode.Standby, Trigger = ClearTrigger.Manual, Before = new PoolTotals(4 * GB, 2 * GB, 0, 0),
            After = new PoolTotals((ulong)((4 - freedGb) * GB), 2 * GB, 0, 0), TagsBefore = [(PoolTag.Parse(tag), 4 * GB)], TagsAfter = [(PoolTag.Parse(tag), (long)((4 - freedGb) * GB))],
        };
        return new(helps, r);
    }

    [Fact]
    public void WhatEachClearFreedSurvivesARestartButNotForAClearThatDidNotRun()
    {
        var measured = new ClearRecord
        {
            TimeUtc = DateTime.UtcNow, TickMs = AppClocks.Ms(DateTime.UtcNow), Mode = ClearMode.Standby, Trigger = ClearTrigger.Escalation, Before = new PoolTotals(6 * GB, 5 * GB, 0, 0),
            After = new PoolTotals(3 * GB, 4 * GB, 0, 0),
            TagsBefore = [(PoolTag.Parse("Reff"), 3 * GB), (PoolTag.Parse("Ntff"), 200L << 20)], TagsAfter = [(PoolTag.Parse("Reff"), GB), (PoolTag.Parse("Ntff"), 200L << 20)],
        };
        static CacheClearer.Saved ThroughJson(CacheClearer.Saved s) => JsonSerializer.Deserialize<CacheClearer.Saved>(JsonSerializer.Serialize(s))!;
        var back = CacheClearer.FromSaved(ThroughJson(CacheClearer.ToSaved(measured)), AppClocks.At(DateTime.UtcNow));
        Assert.Equal((measured.TimeUtc, ClearMode.Standby, ClearTrigger.Escalation, 4 * GB), (back.TimeUtc, back.Mode, back.Trigger, back.Freed));
        Assert.Equal([PoolTag.Parse("Reff")], back.TagsBefore.Select(t => t.Tag));            // only what it freed is kept
        Assert.Equal(FileRecords.ReFS, ClearEvidence.MostlyFrom(back));

        // A clear that did not run freed nothing: no tags are kept for it, whatever was read before it.
        var failed = new ClearRecord
        {
            TimeUtc = DateTime.UtcNow, TickMs = AppClocks.Ms(DateTime.UtcNow), Mode = ClearMode.Standby, Trigger = ClearTrigger.Manual, Before = new PoolTotals(6 * GB, 5 * GB, 0, 0),
            TagsBefore = [(PoolTag.Parse("Reff"), 3 * GB)], Error = "the purge returned 0xC0000061",
        };
        Assert.Null(CacheClearer.ToSaved(failed).Tags);
        var failedBack = CacheClearer.FromSaved(ThroughJson(CacheClearer.ToSaved(failed)), AppClocks.At(DateTime.UtcNow));
        Assert.Equal(("the purge returned 0xC0000061", 0L), (failedBack.Error, failedBack.FreedFrom(ClearEvidence.ReFsTags)));

        // An older file has no tags; a tag that is not a pool tag is left out.
        var old = CacheClearer.FromSaved(JsonSerializer.Deserialize<CacheClearer.Saved>("""
            {"TimeUtc":"2026-10-27T01:58:00Z","Mode":"LowPriority","Trigger":"Schedule","PoolBefore":12884901888,"PoolAfter":12884901888,
             "PagedBefore":6904159928,"PagedAfter":6904159928,"StandbyBefore":0,"StandbyAfter":0,"Error":null}
            """)!, AppClocks.At(DateTime.UtcNow));
        Assert.Equal((ClearMode.LowPriority, 0L, 0), (old.Mode, old.Freed, old.TagsBefore.Count));
        var bad = CacheClearer.ToSaved(measured) with { Tags = [new("not a tag", GB, 0), new("Reff", 3 * GB, GB)] };
        Assert.Equal([PoolTag.Parse("Reff")], CacheClearer.FromSaved(bad, AppClocks.At(DateTime.UtcNow)).TagsBefore.Select(t => t.Tag));
    }

    /// <summary>
    /// Drives the scan could not read are never taken for NTFS: with none read (the list could not be read), or one not
    /// read beside an NTFS drive (said in the setup's impact rows' words), and before the scan has read them (no facts
    /// yet), a ReFS drive may be there, and the Simple view shows the hourly clear, also after a full clear that freed
    /// little. Only a scan that read every drive and found none on ReFS says NTFS.
    /// </summary>
    [Fact]
    public void DrivesTheScanCouldNotReadAreNeverTakenForNtfs()
    {
        var noneRead = Facts() with { VolumesNotRead = true };
        var oneUnread = Facts(Ntfs) with { UnreadVolumes = [@"D:\"] };
        var signIn = new Recommendation(RecommendationId.StartAtSignIn, "Start PoolWatch when you sign in", "So it watches.", "Turn on", RecommendationAction.Apply, 40);
        foreach (var (facts, said) in new[] { (noneRead, "PoolWatch could not read this PC's drives."), (oneUnread, "PoolWatch could not read D:.") })
        {
            Assert.True(facts.VolumesNotAllRead);
            Assert.False(ClearAdvice.HasReFs(facts));
            Assert.True(ClearAdvice.MayHaveReFs(facts));
            Assert.Equal(said, ImpactRows.DrivesNotRead(facts));
            foreach (var evidence in new[] { Evidence(null), Evidence(false, 0.05) })
                Assert.Equal([Hourly(), signIn], ClearAdvice.ForSimpleView([Hourly(), signIn], facts, evidence));
        }
        // No facts yet: not known either.
        Assert.True(ClearAdvice.MayHaveReFs(null));
        Assert.Equal([Hourly(), signIn], ClearAdvice.ForSimpleView([Hourly(), signIn], null, Evidence(null)));
        // Read whole, NTFS only: not shown until a clear helps (the test below).
        Assert.False(ClearAdvice.MayHaveReFs(Facts(Ntfs)));
        Assert.Equal([signIn], ClearAdvice.ForSimpleView([Hourly(), signIn], Facts(Ntfs), Evidence(null)));
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
        // File records a clear frees grew in the last day: suggested on an NTFS-only PC too, before any clear ran.
        Assert.Equal([Hourly(), signIn, report], ClearAdvice.ForSimpleView(current, Facts(Ntfs), Evidence(null), fileRecordsGrew: true));
    }

    /// <summary>
    /// DESIGN.md and ClearAdvice's summaries name the cases in which the Simple view shows the hourly clear as the code
    /// holds them, before the scan has read the drives among them (the test above holds that with no facts yet), in these
    /// words (comment markers and line breaks left out): a text whose sentence is changed turns this red.
    /// </summary>
    [Fact]
    public void TheTextsSayTheHourlyClearIsShownBeforeTheScanHasReadTheDrives()
    {
        static string Prose(params string[] path) =>
            UninstallerTests.OneLine(Regex.Replace(File.ReadAllText(Path.Combine([TrayIconsTests.RepoRoot(), .. path])), @"(?m)^\s*///?\s?", " "));
        Assert.True(ClearAdvice.MayHaveReFs(null));
        Assert.Contains("the Simple view offers the hourly clear on a PC with a ReFS volume or a Dev Drive, or whose drives the scan could not all " +
                        "read, or before the scan has read them (one of them may be such a drive: `ClearAdvice.MayHaveReFs`)", Prose("docs", "DESIGN.md"));
        string advice = Prose("src", "PoolWatch", "Ui", "RecommendationActions.cs");
        Assert.Contains("It is shown with a ReFS drive or a Dev Drive, or where the drives could not all be read or before the scan has read them " +
                        "(<see cref=\"MayHaveReFs\"/>)", advice);
        Assert.Contains("and so it does where the drives could not all be read or before the scan has read them, as one of them may be such a " +
                        "drive (<see cref=\"MayHaveReFs\"/>)", advice);
    }

    [Fact]
    public void DecideLaterOnAnNtfsOnlyPcIsFollowedByTheHourlyCardOnceFileRecordsGrow()
    {
        // Setup's "Decide later" left clearing off on a PC with NTFS only, and no clear ran: the card waits for the evidence
        // the window gathers each minute, then offers the clear.
        var facts = Facts(Ntfs);
        var quiet = new RecommendationInputs { NowUtc = DateTime.UtcNow, NowMs = Moment.Now().Ms, Facts = facts };
        Assert.DoesNotContain(SetupContext.Suggest(quiet, [], facts, Evidence(null)), r => r.Id == RecommendationId.HourlyCacheClear);
        var grew = quiet with { FileSystemMetadataGrewLast24h = true };
        Assert.Contains(SetupContext.Suggest(grew, [], facts, Evidence(null)), r => r.Id == RecommendationId.HourlyCacheClear);
        // Clearing on already: nothing to offer.
        Assert.DoesNotContain(SetupContext.Suggest(grew with { AutoClearOn = true }, [], facts, Evidence(null)), r => r.Id == RecommendationId.HourlyCacheClear);
    }

    [Fact]
    public void AKnownIssueOnThisPcGivesTheCardItExplainsItsReason()
    {
        var facts = Facts(Ntfs, DevDrive);
        var inputs = new RecommendationInputs { NowUtc = DateTime.UtcNow, NowMs = Moment.Now().Ms, Facts = facts, TokenSpawnLast24h = true };
        var seen = new KnowledgeEvidence
        {
            NowUtc = inputs.NowUtc, WindowsBuild = facts.Build, FileSystems = ["NTFS", "ReFS"], GrowthOver = TimeSpan.FromHours(1),
            Growth = new Dictionary<PoolTag, TagGrowth> { [PoolTag.Parse("Toke")] = new(300L << 20, 0), [PoolTag.Parse("Reff")] = new(400L << 20, 0) },
        };
        var matches = KnownIssueMatcher.Match(KnowledgeBase.Embedded(), seen);
        Recommendation Card(ClearEvidence evidence, RecommendationId id) => SetupContext.Suggest(inputs, matches, facts, evidence).Single(r => r.Id == id);

        var guard = Card(Evidence(null), RecommendationId.TokenLeakGuard);
        Assert.StartsWith("Security tokens grew 300 MB in the last hour. This is the known Windows token leak.", guard.Why);
        Assert.DoesNotContain("to the front", TokenGuardWords.CardWhy(guard.Why)); // the card's details say the trade-off instead
        Assert.StartsWith("ReFS file records grew 400 MB in the last hour.", Card(Evidence(null), RecommendationId.HourlyCacheClear).Why);
        // Once a clear here helped, what it freed is the hourly card's reason.
        Assert.StartsWith("On this PC, clearing the cache freed 2.0 GB", Card(Evidence(true, 2, "Reff"), RecommendationId.HourlyCacheClear).Why);
    }

    [Fact]
    public void TheTextsArePlainWords()
    {
        var setting = new Regex(@"\b[A-Z][a-z]+(?:[A-Z][a-z]+)+(Enabled|Hours|Gb|Minutes)\b|AutoClear|TokenGuard", RegexOptions.CultureInvariant);
        string[] texts = [TokenGuardWords.Why, TokenGuardWords.Details, RecommendationActions.Refused];
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
    public void WhereTurningTheGuardOffIsSaidToPutTheSettingBackItSaysWhenItCannot()
    {
        // Turning the guard off puts back only a setting the guard changed, and only when Windows allows it; one it found
        // already changed lasts until you sign out. The offer's undo line, the README, the setting's description, the guard's
        // row and button in Active changes, and every sentence of the docs that says it puts the setting back say so.
        Assert.Contains("which also puts back the setting it changed when Windows allows it; one it found already changed lasts until you sign out.",
            TokenGuardWords.Undo);
        string readme = UninstallerTests.OneLine(File.ReadAllText(Path.Combine(TokenLeakHelperStartTests.RepoDir(), "README.md")));
        Assert.Contains("turn it off in Active changes, which also puts back the setting it changed when Windows allows it (one it found already changed lasts until you sign out)", readme);
        string description = typeof(PoolWatch.Core.Settings).GetProperty(nameof(PoolWatch.Core.Settings.TokenGuardEnabled))!
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false).Cast<System.ComponentModel.DescriptionAttribute>().Single().Description;
        Assert.Contains("(pool tag Toke). Turning the guard off puts it back if Windows allows; if not, or if it was already changed, it can stay until you sign out.", description);
        // The guard's row in Active changes and its Turn off button say it too.
        Assert.Contains("puts back the Windows setting it changed when Windows allows it; one it found already changed stays until you sign out.",
            PoolWatch.Core.Changes.ChangeWords.TokenGuardNote);
        Assert.Contains("puts back the Windows setting it changed when Windows allows it (one it found already changed stays until you sign out).",
            PoolWatch.Core.Changes.ChangeWords.TokenGuardTurnOffTip);
        // And so does every sentence of README.md, SECURITY.md, the .md files directly in docs, the guide the app ships for AI
        // assistants and the comments of the C# code under src that says turning the guard off, unticking it or
        // uninstalling puts its setting back (UnqualifiedPutBacks).
        var unqualified = UnqualifiedPutBacks();
        Assert.True(unqualified.Count == 0, string.Join(Environment.NewLine, unqualified));
    }

    [Fact]
    public void TheScanForUnqualifiedPutBacksReadsTheCodesComments()
    {
        // The scan above finds such a sentence in a code comment, also one spread over several comment lines or written the
        // other way round, passive ("is put back by ... Turn off") or active ("puts the setting back when the guard is
        // turned off"), and passes one that says when Windows does not allow it, in either order.
        string[] code =
        [
            "    /// <summary>",
            "    /// The setting the guard changed itself is put back by the uninstaller's step as by Turn off",
            "    /// (<see cref=\"TokenGuardSetting.PutBack\"/>).",
            "    /// </summary>",
            "    public string? PutBackCaveat { get; init; }",
            "    // Unticking the guard's box puts back what",
            "    // PoolWatch changed.",
            "    // Turning the guard off puts the setting back when Windows allows it.",
            "    // PoolWatch puts the setting back when the guard is turned off.",
            "    // PoolWatch puts the setting back when the guard is turned off, when Windows allows it.",
        ];
        Assert.Equal(["The setting the guard changed itself is put back by the uninstaller's step as by Turn off (TokenGuardSetting.PutBack).",
            "Unticking the guard's box puts back what PoolWatch changed.", "PoolWatch puts the setting back when the guard is turned off."],
            UnqualifiedPutBacks(CommentTexts(code)));
        // So is one in a comment after code on its line, or between /* and */.
        Assert.Equal(["Unticking the guard's box puts back what PoolWatch changed.", "Uninstalling puts the guard's setting back."],
            UnqualifiedPutBacks(CommentTexts(["    var x = 1; // Unticking the guard's box puts back what PoolWatch changed.",
                "    Run(x /* Uninstalling puts the guard's setting back. */);"])));
        // And it reads the comments of the code under src: here the one on ActiveChange.PutBackCaveat, and one after code on
        // its line in AppPaths.
        var scanned = ScannedTexts().ToList();
        Assert.Contains(scanned, t => t.File == "ActiveChange.cs"
            && t.Text.Contains("The setting the guard changed itself is put back by the uninstaller's step as by Turn off, when Windows allows it"));
        Assert.Contains(scanned, t => t.File == "AppPaths.cs" && t.Text == "tests, the Linux build, an unelevated run (it changes nothing elevated)");
    }

    [Fact]
    public void TheScansReadEveryCommentOfTheCodeAndNoString()
    {
        // A run of comments on lines of their own, one line after the other, is one text; a comment after code on its line,
        // and a /* */ comment, on one line or more, are each a text of their own; a "//" or "/*" in a string (a verbatim, a
        // raw or an interpolated one too) or in a character is no comment, while one in an interpolated string's hole (a
        // raw one's too), and the rest of a preprocessor line from its first "//", are (where that rest is the directive's
        // text, as after #region, #endregion, #error and #warning, it is read as a comment all the same).
        string[] code =
        [
            "#pragma warning disable CA1000 // After a directive.",
            "    // The first run of comments",
            "    /// goes on here.",
            "    int x = 1; // A comment after code.",
            "    // A run after it.",
            "    int y = F(2 /* a block comment */, 3);",
            "    /* A block comment",
            "     * over two lines. */",
            "    string u = \"https://example.com/* no comment */\", v = @\"// nor \"\"this\"\"\", w = \"\"\"// nor this\"\"\"; char c = '/';",
            "    string i = $\"{(x > 0 ? \"//\" : \"\")} // still the string\"; // But this is one.",
            "    string h = $\"/* no comment */ {F(1 /* in a hole */)}\", r = $$\"\"\"{ /* no comment */ {{F(2 /* in a raw hole */)}} }\"\"\"; // After both.",
            "    // A run",
            "",
            "    // and another after a blank line.",
        ];
        Assert.Equal(["After a directive.", "The first run of comments goes on here.", "A comment after code.", "A run after it.", "a block comment",
            "A block comment over two lines.", "But this is one.", "in a hole", "in a raw hole", "After both.", "A run", "and another after a blank line."],
            CommentTexts(code));
        // So the wider scans read the tests' comment after a preprocessor directive.
        Assert.Contains(MoreTexts(), t => t.File == "NoDialogs.cs" && t.Text == "a test assembly is the place for this: it must run before any test creates a window");
    }

    [Fact]
    public void ADirectivesOwnTextFromADoubleSlashIsReadAsAComment()
    {
        // After #region, #endregion, #error and #warning the rest of the line is the directive's own text in C#, a "//" in
        // it too (the compiler shows the text of "#warning a // b" as "a // b"). From the first "//" on such a line the
        // finder reads that text as a comment all the same, so a scan reads more there, not less; on the line of another
        // directive (#pragma, #if, #nullable) the first "//" starts a comment, as in C#, and a later "//" is part of it.
        Assert.Equal(["as the region's name", "as the end's text", "as the error's text", "as the warning's text", "a comment", "another", "one more",
                "b // c", "a // b"],
            CommentTexts(["#region A region // as the region's name", "    #endregion // as the end's text", "#error An error // as the error's text",
                "  #warning A warning // as the warning's text", "#pragma warning disable CA1000 // a comment", "#if DEBUG // another",
                "#nullable enable // one more", "#warning a // b // c", "#pragma warning restore CA1000 // a // b", "#endif"]));
    }

    /// <summary>
    /// The sentences (a sentence, a clause between semicolons, or a table cell) of README.md, SECURITY.md, the .md files
    /// directly in docs, the guide the app ships for AI assistants (src/PoolWatch/Assets/AGENTS.md) and the comments of the
    /// C# code under src (<see cref="ScannedTexts"/>) that name the guard and say that turning it off, unticking it or
    /// uninstalling puts something back, and do not say when Windows does not allow it.
    /// </summary>
    internal static List<string> UnqualifiedPutBacks() =>
        ScannedTexts().SelectMany(t => UnqualifiedPutBacks([t.Text]).Select(s => $"{t.File}: {s}")).ToList();

    /// <summary>
    /// What <see cref="UnqualifiedPutBacks()"/> reads: README.md, SECURITY.md, the .md files directly in docs and the guide
    /// the app ships for AI assistants, each as one line, and every comment of each C# file under src
    /// (<see cref="CommentTexts"/>), but for the build's bin and obj folders.
    /// </summary>
    internal static IEnumerable<(string File, string Text)> ScannedTexts()
    {
        string repo = TokenLeakHelperStartTests.RepoDir();
        var files = new List<string> { Path.Combine(repo, "README.md"), Path.Combine(repo, "SECURITY.md"), Path.Combine(repo, "src", "PoolWatch", "Assets", "AGENTS.md") };
        files.AddRange(Directory.GetFiles(Path.Combine(repo, "docs"), "*.md"));
        foreach (string file in files) yield return (Path.GetFileName(file), UninstallerTests.OneLine(File.ReadAllText(file)));
        char sep = Path.DirectorySeparatorChar;
        foreach (string file in Directory.GetFiles(Path.Combine(repo, "src"), "*.cs", SearchOption.AllDirectories))
            if (!file.Contains($"{sep}bin{sep}") && !file.Contains($"{sep}obj{sep}"))
                foreach (string text in CommentTexts(File.ReadAllLines(file))) yield return (Path.GetFileName(file), text);
    }

    /// <summary>
    /// The repository's other texts, for the scans that read more than <see cref="ScannedTexts"/>: the root AGENTS.md (the
    /// brief for contributors and AI coding agents, not the guide the app ships), CONTRIBUTING.md and CHANGELOG.md, each as
    /// one line; the installer's script (<see cref="ScriptAsShown"/>); and every comment of each C# file under tests
    /// (<see cref="CommentTexts"/>), but for the build's bin and obj folders. With <see cref="ScannedTexts"/>, these are
    /// all the files such a scan reads: not
    /// the CI scripts, the installer's other scripts, the knowledge packs, the GitHub templates, the tools' code or a file
    /// in a folder below docs.
    /// </summary>
    internal static IEnumerable<(string File, string Text)> MoreTexts()
    {
        string repo = TokenLeakHelperStartTests.RepoDir();
        foreach (string name in new[] { "AGENTS.md", "CONTRIBUTING.md", "CHANGELOG.md" })
            yield return ("the repository's " + name, UninstallerTests.OneLine(File.ReadAllText(Path.Combine(repo, name))));
        yield return ("PoolWatch.iss", ScriptAsShown(File.ReadAllText(Path.Combine(repo, "installer", "PoolWatch.iss"))));
        char sep = Path.DirectorySeparatorChar;
        foreach (string file in Directory.GetFiles(Path.Combine(repo, "tests"), "*.cs", SearchOption.AllDirectories))
            if (!file.Contains($"{sep}bin{sep}") && !file.Contains($"{sep}obj{sep}"))
                foreach (string text in CommentTexts(File.ReadAllLines(file))) yield return (Path.GetFileName(file), text);
    }

    /// <summary>
    /// The installer's script (Pascal source) as one line (<see cref="UninstallerTests.OneLine"/>), with each pair of its
    /// string literals that only a <c>+</c> joins made one (<see cref="UninstallerTests.JoinedLiterals"/>): a message
    /// written over several such literals, as the script writes a long one, is read whole, as it is shown (but for a
    /// quote in it, still written twice), and its comments as written.
    /// </summary>
    internal static string ScriptAsShown(string pascal) => UninstallerTests.JoinedLiterals(UninstallerTests.OneLine(pascal));

    /// <summary>The sentences of <paramref name="texts"/> that <see cref="UnqualifiedPutBacks()"/> lists.</summary>
    private static List<string> UnqualifiedPutBacks(IEnumerable<string> texts)
    {
        // "puts", "putting" or "is put": a claim of what happens, not the Put back button's name nor an instruction to put
        // it back; and the same claim the other way round, passive ("is put back by ... Turn off") and active ("puts the
        // setting back when the guard is turned off").
        const string off = @"(turn(s|ing|ed)?\b[^.;|]{0,40}\boff|untick\w*|uninstall\w*)";
        var claim = new Regex(@"\b" + off + @"\b[^.;|]*\b(put(s|ting)|(is|are|be|gets?) put)\b[^.;|]{0,60}\bback\b|\b(is|are|be|gets?) put back\b[^.;|]*\b" + off +
            @"|\bput(s|ting)\b[^.;|]{0,60}\bback\s+(when|whenever|once|as|on|at|after|by|if)\b[^.;|]*\b" + off, RegexOptions.IgnoreCase);
        var qualified = new Regex(@"when Windows allows it|Windows (did|does) not (allow|let)|when Windows lets it|if Windows");
        return texts.SelectMany(text => Regex.Split(text, @"(?<=[.;|])\s+"))
            .Where(sentence => sentence.Contains("guard", StringComparison.OrdinalIgnoreCase) && claim.IsMatch(sentence) && !qualified.IsMatch(sentence))
            .ToList();
    }

    /// <summary>
    /// Every comment of a C# file, each as one line of text: a run of <c>//</c> or <c>///</c> comments on lines of their
    /// own, one line after the other, as one text; a comment after code on its line, and a <c>/* */</c> comment (its lines
    /// without the <c>*</c> that starts them), each as a text of its own. The comments are found by the C# tokens
    /// (<see cref="ScriptCode.CSharpComments"/>), so a <c>//</c> or <c>/*</c> in a string or a character is not one,
    /// while one in an interpolated string's hole is, and so is the rest of a preprocessor line from the first <c>//</c>
    /// in it (where that is the directive's text, as after <c>#region</c>, <c>#endregion</c>, <c>#error</c> and
    /// <c>#warning</c>, it is read as a comment all the same). A <c>see</c> or <c>paramref</c> tag is replaced by what it
    /// names and the other documentation tags are removed.
    /// </summary>
    internal static List<string> CommentTexts(IEnumerable<string> lines)
    {
        string source = string.Join("\n", lines);
        var texts = new List<string>();
        var run = new List<string>();
        int line = 0, counted = 0, runLine = -2;
        foreach (var (start, end) in ScriptCode.CSharpComments(source))
        {
            for (; counted < start; counted++) if (source[counted] == '\n') line++;
            string comment = source[start..end];
            int lineStart = start == 0 ? 0 : source.LastIndexOf('\n', start - 1) + 1;
            bool ownLine = source[lineStart..start].All(c => c is ' ' or '\t');
            if (comment.StartsWith("/*", StringComparison.Ordinal))
            {
                Close();
                string inner = comment.Length >= 4 && comment.EndsWith("*/", StringComparison.Ordinal) ? comment[2..^2] : comment[2..];
                texts.Add(Plain(string.Join(" ", inner.Split('\n').Select(l => l.Trim().TrimStart('*').Trim()))));
                runLine = -2;
                continue;
            }
            if (!ownLine || line != runLine + 1) Close();
            run.Add(comment.TrimStart('/').Trim());
            runLine = ownLine ? line : -2; // a comment after code is a text of its own: the next line's starts another
        }
        Close();
        return texts;

        void Close()
        {
            if (run.Count > 0) texts.Add(Plain(string.Join(" ", run)));
            run.Clear();
        }

        static string Plain(string text)
        {
            text = Regex.Replace(text, @"<(see|seealso|paramref|typeparamref)\s+(cref|name|langword)=""([^""]*)""\s*/>", "$3");
            text = Regex.Replace(text, @"</?(summary|remarks|returns|para|c|code|param|typeparam|value|example|list|item|description|term|exception|inheritdoc)\b[^>]*>", "");
            return UninstallerTests.OneLine(text);
        }
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
        public string IncidentsDir => @"C:\ProgramData\PoolWatch\incidents";
        public void Timeline(string line) => TimelineLines.Add(line);
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
        public void ShowSettings() { }
        public Task<bool> StartTokenGuard() { GuardStarts++; return Task.FromResult(GuardProtects); }
    }
}
