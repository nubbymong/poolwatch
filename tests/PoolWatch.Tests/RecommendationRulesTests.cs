using System.Text.RegularExpressions;
using PoolWatch.Core;
using PoolWatch.Core.Recommendations;
using PoolWatch.Core.Setup;
using PoolWatch.Core.TokenGuard;

namespace PoolWatch.Tests;

/// <summary>
/// The recommendations model: the setup guide's first-run rows, and the Simple view's suggestions, each only on its
/// evidence, at most three, by priority, never one hidden in the last 30 days, in plain words.
/// </summary>
public class RecommendationRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>A PC where nothing is suggested: everything set up, no evidence of anything.</summary>
    private static EnvironmentFacts Facts() => new()
    {
        ScannedUtc = Now.AddHours(-1), WindowsName = "Windows 11 Pro", DisplayVersion = "25H2", Build = "26220.7344", Architecture = "x64",
        RamBytes = 32L << 30, Volumes = [new(@"C:\", "NTFS", false, null)], TokenLeak = TokenLeakStatus.NotAffected,
        LogonTaskExists = true, InstalledInProgramFiles = true,
    };

    private static RecommendationInputs Quiet(EnvironmentFacts? facts = null) =>
        new() { NowUtc = Now, Facts = facts ?? Facts(), AutoClearOn = true, TokenGuardOn = false };

    private static List<RecommendationId> Ids(RecommendationInputs i, int max = 100) => RecommendationRules.Current(i, max).Select(r => r.Id).ToList();

    private static Recommendation Row(RecommendationInputs i, RecommendationId id) => RecommendationRules.FirstRun(i).Single(r => r.Id == id);

    // ------------------------------------------------------------------ first run

    [Fact]
    public void TheGuideListsTheFirstRunRowsInOrder()
    {
        var i = Quiet(Facts() with { LogonTaskExists = false, OptionalServicesRunning = 4, OptionalServiceExamples = ["Fax", "Xbox Live Auth Manager", "Retail Demo"] }) with { AutoClearOn = false };
        var rows = RecommendationRules.FirstRun(i);
        Assert.Equal([RecommendationId.StartAtSignIn, RecommendationId.HourlyCacheClear, RecommendationId.TokenLeakGuard, RecommendationId.ReviewServices],
            rows.Select(r => r.Id));
        Assert.Equal(RecommendationState.Offer, rows[0].State);
        Assert.Equal("Start PoolWatch when you sign in", rows[0].Title);
        Assert.Equal("Clear the cache every hour", rows[1].Title);
        Assert.Equal(RecommendationState.Info, rows[3].State);
        Assert.Equal("4 background services you may not need are running (for example Fax, Xbox Live Auth Manager). You can review them any time in Advanced view, Services.", rows[3].Why);
        Assert.Empty(RecommendationRules.FirstRun(i with { Facts = null })); // the guide shows its rows once the scan is in
    }

    [Fact]
    public void YourOwnClearTaskReplacesTheHourlyRow()
    {
        var i = Quiet(Facts() with { OwnClearTasks = [@"\RAMMap standby"] }) with { AutoClearOn = false };
        var rows = RecommendationRules.FirstRun(i);
        Assert.Contains(rows, r => r.Id == RecommendationId.ReplaceClearTask && r.State == RecommendationState.Offer);
        Assert.DoesNotContain(rows, r => r.Id == RecommendationId.HourlyCacheClear); // the two must never both run

        Assert.Equal(RecommendationState.Done, Row(Quiet(), RecommendationId.HourlyCacheClear).State);
    }

    [Fact]
    public void SignInIsDoneOrOnlyInfoWhenItCannotBeOffered()
    {
        Assert.Equal(RecommendationState.Done, Row(Quiet(), RecommendationId.StartAtSignIn).State);
        var notInstalled = Row(Quiet(Facts() with { LogonTaskExists = false, InstalledInProgramFiles = false }), RecommendationId.StartAtSignIn);
        Assert.Equal(RecommendationState.Info, notInstalled.State);
        Assert.Equal("", notInstalled.Button);
        Assert.Contains("installer", notInstalled.Why);
    }

    [Fact]
    public void TheGuardIsOfferedOnlyWhenThisBuildLeaks()
    {
        foreach (var status in Enum.GetValues<TokenLeakStatus>())
        {
            var row = Row(Quiet(Facts() with { TokenLeak = status }), RecommendationId.TokenLeakGuard);
            var expected = status switch
            {
                TokenLeakStatus.Leaks => RecommendationState.Offer,
                TokenLeakStatus.NeedsTest => RecommendationState.Checking,
                _ => RecommendationState.Info,
            };
            Assert.Equal(expected, row.State);
        }
        // When it is offered, it says when the guard acts: each time PoolWatch starts (Windows resets the setting at each
        // sign-in, but PoolWatch may start at other times, and may not start at a sign-in).
        var offered = Row(Quiet(Facts() with { TokenLeak = TokenLeakStatus.Leaks }), RecommendationId.TokenLeakGuard);
        Assert.Contains("The guard turns on the Windows setting that stops it, each time PoolWatch starts.", offered.Why);
        Assert.DoesNotContain("sign-in", offered.Why);
        var checking = Row(Quiet(Facts() with { TokenLeak = TokenLeakStatus.NeedsTest }), RecommendationId.TokenLeakGuard);
        Assert.Equal(RecommendationAction.CheckTokenLeak, checking.Action);
        Assert.Equal("Checking whether this Windows build has the token leak (about 20 seconds)...", checking.Why);
        Assert.Equal("Something on this PC already works around the token leak.",
            Row(Quiet(Facts() with { TokenLeak = TokenLeakStatus.WorkedAroundAlready }), RecommendationId.TokenLeakGuard).Why);
        Assert.Equal("PoolWatch's check found no token leak on this Windows build.", // what one clean check shows, no more
            Row(Quiet(Facts() with { TokenLeak = TokenLeakStatus.Clean }), RecommendationId.TokenLeakGuard).Why);

        // This session's check is newer than the scan; the guard on is done whatever the build.
        var afterCheck = Quiet(Facts() with { TokenLeak = TokenLeakStatus.NeedsTest });
        Assert.Equal(RecommendationState.Offer, Row(afterCheck with { LeakTestNow = LeakVerdict.Leak }, RecommendationId.TokenLeakGuard).State);
        Assert.Equal(RecommendationState.Info, Row(afterCheck with { LeakTestNow = LeakVerdict.Clean }, RecommendationId.TokenLeakGuard).State);
        Assert.Equal(RecommendationState.Info, Row(afterCheck with { LeakTestNow = LeakVerdict.Unclear }, RecommendationId.TokenLeakGuard).State);
        Assert.Equal(RecommendationState.Done, Row(afterCheck with { TokenGuardOn = true }, RecommendationId.TokenLeakGuard).State);
    }

    // ------------------------------------------------------------------ later, on evidence

    [Fact]
    public void NothingIsSuggestedWithoutEvidence()
    {
        Assert.Empty(RecommendationRules.Current(Quiet()));
        Assert.Empty(RecommendationRules.Current(Quiet() with { Facts = null, AutoClearOn = false }));
    }

    [Fact]
    public void TheGuardOnTokenFindingsOrALeakVerdictForThisBuild()
    {
        Assert.Equal([RecommendationId.TokenLeakGuard], Ids(Quiet() with { TokenSpawnLast24h = true }));
        Assert.Equal([RecommendationId.TokenLeakGuard], Ids(Quiet() with { SavedVerdict = GuardResult.Leak, SavedVerdictBuild = "26220.7344" }));
        Assert.Equal([RecommendationId.TokenLeakGuard], Ids(Quiet() with { LeakTestNow = LeakVerdict.Leak }));
        Assert.Empty(Ids(Quiet() with { SavedVerdict = GuardResult.Leak, SavedVerdictBuild = "26100.1000" }));
        Assert.Empty(Ids(Quiet() with { SavedVerdict = GuardResult.CleanOnce, SavedVerdictBuild = "26220.7344" }));
        Assert.Empty(Ids(Quiet() with { TokenSpawnLast24h = true, TokenGuardOn = true }));
        Assert.Contains("saw it in the last day", RecommendationRules.Current(Quiet() with { TokenSpawnLast24h = true })[0].Why);
    }

    [Fact]
    public void TheClearTaskAndTheHourlyClear()
    {
        Assert.Equal([RecommendationId.ReplaceClearTask], Ids(Quiet(Facts() with { OwnClearTasks = [@"\Empty standby"] }) with { AutoClearOn = false }));
        Assert.Equal([RecommendationId.ReplaceClearTask], Ids(Quiet(Facts() with { OwnClearTasks = [@"\Empty standby"] })));
        Assert.Equal([RecommendationId.HourlyCacheClear], Ids(Quiet() with { AutoClearOn = false }));
    }

    [Fact]
    public void TheCheckAfterAWindowsUpdate()
    {
        var needs = Quiet(Facts() with { TokenLeak = TokenLeakStatus.NeedsTest });
        var never = RecommendationRules.Current(needs).Single();
        Assert.Equal(RecommendationId.CheckTokenLeak, never.Id);
        Assert.Equal(RecommendationAction.CheckTokenLeak, never.Action);
        Assert.StartsWith("Some Windows 11 builds", never.Why);
        var updated = RecommendationRules.Current(needs with { SavedVerdict = GuardResult.Fixed, SavedVerdictBuild = "26220.7000" }).Single();
        Assert.StartsWith("Windows was updated", updated.Why);
        Assert.Empty(Ids(needs with { SavedVerdict = GuardResult.Unclear, SavedVerdictBuild = "26220.7344" })); // a verdict for this build
        Assert.Empty(Ids(needs with { TokenGuardOn = true }));
        Assert.Empty(Ids(needs with { LeakTestNow = LeakVerdict.Clean }));
        Assert.Empty(Ids(Quiet(Facts() with { TokenLeak = TokenLeakStatus.WorkedAroundAlready })));
    }

    [Fact]
    public void SignInOnlyWhenInstalled()
    {
        Assert.Equal([RecommendationId.StartAtSignIn], Ids(Quiet(Facts() with { LogonTaskExists = false })));
        Assert.Empty(Ids(Quiet(Facts() with { LogonTaskExists = false, InstalledInProgramFiles = false })));
    }

    [Fact]
    public void ReportAnIncidentWithACause()
    {
        var i = Quiet() with { ReportableIncident = ("20261028-0712-Reff", @"C:\ProgramData\PoolWatch\incidents\20261028-0712-Reff", "Kernel pool grew 2.1 GB at 07:12; the likely cause is ChatGPT, with some evidence.") };
        var r = RecommendationRules.Current(i).Single();
        Assert.Equal(RecommendationId.ReportIncident, r.Id);
        Assert.Equal(RecommendationAction.ReportIssue, r.Action);
        Assert.Equal("20261028-0712-Reff", r.Subject);
        Assert.Equal("ReportIncident:20261028-0712-Reff", r.Key);
        Assert.StartsWith("Kernel pool grew 2.1 GB", r.Why);

        // Hiding it hides that incident only.
        var hidden = i with { Dismissed = new Dictionary<string, DateTime> { [r.Key] = Now.AddDays(-1), ["ReportIncident:another"] = Now } };
        Assert.Empty(Ids(hidden));
        Assert.Single(Ids(i with { Dismissed = new Dictionary<string, DateTime> { ["ReportIncident:another"] = Now } }));
    }

    [Fact]
    public void ReviewServicesOnTheirCostOrASaturation()
    {
        var costly = RecommendationRules.Current(Quiet() with { CostlyOptionalServices = ["Xbox Live Auth Manager", "Fax"] }).Single();
        Assert.Equal(RecommendationId.ReviewServices, costly.Id);
        Assert.Equal(RecommendationAction.OpenServices, costly.Action);
        Assert.StartsWith("Xbox Live Auth Manager and 1 more used", costly.Why);
        Assert.Equal([RecommendationId.ReviewServices], Ids(Quiet() with { SaturationOfferedTurnOffLast24h = true }));
    }

    private static EnvironmentFacts DevDrive(bool? trusted, bool? performanceMode = true, bool? realTime = true) => Facts() with
    {
        Volumes = [new(@"C:\", "NTFS", false, null), new(@"E:\", "ReFS", true, trusted)],
        DefenderRealTimeOn = realTime, DefenderPerformanceModeOn = performanceMode,
    };

    [Fact]
    public void DefenderAdviceOnlyWithADevDriveDefenderSlowsAndEvidence()
    {
        var r = RecommendationRules.Current(Quiet(DevDrive(trusted: false)) with { FileSystemMetadataGrewLast24h = true }).Single();
        Assert.Equal(RecommendationId.DefenderDevDrive, r.Id);
        Assert.Equal(RecommendationAction.ShowInstructions, r.Action);
        Assert.Contains("fsutil devdrv trust E:", r.Instructions);
        Assert.StartsWith("File activity made the kernel pool grow in the last day", r.Why);

        // Performance mode off: every Dev Drive, and Defender's own processor time is evidence too.
        var busy = RecommendationRules.Current(Quiet(DevDrive(trusted: true, performanceMode: false)) with { DefenderCoresAvg30Min = 0.3 }).Single();
        Assert.Contains("fsutil devdrv query E:", busy.Instructions);
        Assert.StartsWith("Microsoft Defender was busy in the last half hour", busy.Why);

        // Either way the card claims only what performance mode does (Microsoft: it is no fix for Defender's own processor use).
        foreach (var why in new[] { r.Why, busy.Why })
        {
            Assert.Contains("so programs wait less", why);
            Assert.DoesNotContain("processor", why);
        }

        Assert.Empty(Ids(Quiet(DevDrive(trusted: true)) with { FileSystemMetadataGrewLast24h = true }));           // already fast
        Assert.Empty(Ids(Quiet(DevDrive(trusted: null)) with { FileSystemMetadataGrewLast24h = true }));           // not known: no advice
        Assert.Empty(Ids(Quiet(DevDrive(trusted: false, realTime: false)) with { FileSystemMetadataGrewLast24h = true }));
        Assert.Empty(Ids(Quiet(DevDrive(trusted: false, realTime: null)) with { FileSystemMetadataGrewLast24h = true }));
        Assert.Empty(Ids(Quiet(DevDrive(trusted: false)) with { DefenderCoresAvg30Min = 0.2 }));                    // no evidence
        Assert.Empty(Ids(Quiet(Facts() with { DefenderRealTimeOn = true, DefenderPerformanceModeOn = false }) with { FileSystemMetadataGrewLast24h = true }));
    }

    [Fact]
    public void TheChatGptShortcutNeedsAllFourFacts()
    {
        var all = Quiet(Facts() with { ChatGptInstalled = true }) with { ChatGptLauncherInstalled = true, ChatGptNamedInGrowth = true };
        var r = RecommendationRules.Current(all).Single();
        Assert.Equal(RecommendationId.ChatGptShortcut, r.Id);
        Assert.Equal("Create shortcut", r.Button);
        Assert.Equal("Start Codex without its shader cache", r.Title); // the app as setup, Settings and Active changes name it
        Assert.Contains("Some users found the Codex app uses less of the kernel pool when it starts without its GPU shader disk cache " +
                        "(found by Faarfarin on GitHub).", r.Why);
        Assert.Empty(Ids(all with { Facts = Facts() }));
        Assert.Empty(Ids(all with { ChatGptLauncherInstalled = false }));
        Assert.Empty(Ids(all with { ChatGptNamedInGrowth = false }));
        Assert.Empty(Ids(all with { ChatGptShortcutMade = true }));
    }

    /// <summary>Every suggestion at once: each rule's evidence is there.</summary>
    private static RecommendationInputs Everything() => Quiet(DevDrive(trusted: false) with
    {
        TokenLeak = TokenLeakStatus.NeedsTest, LogonTaskExists = false, ChatGptInstalled = true,
    }) with
    {
        AutoClearOn = false, TokenSpawnLast24h = true, FileSystemMetadataGrewLast24h = true, CostlyOptionalServices = ["Fax"],
        ReportableIncident = ("inc", "dir", "Kernel pool grew 1 GB."), ChatGptLauncherInstalled = true, ChatGptNamedInGrowth = true,
    };

    [Fact]
    public void AtMostThreeByPriority()
    {
        Assert.Equal(
        [
            RecommendationId.TokenLeakGuard, RecommendationId.HourlyCacheClear, RecommendationId.CheckTokenLeak, RecommendationId.StartAtSignIn,
            RecommendationId.ReportIncident, RecommendationId.ReviewServices, RecommendationId.DefenderDevDrive, RecommendationId.ChatGptShortcut,
        ], Ids(Everything()));
        Assert.Equal([RecommendationId.TokenLeakGuard, RecommendationId.HourlyCacheClear, RecommendationId.CheckTokenLeak], Ids(Everything(), 3));
        Assert.Equal(3, RecommendationRules.Current(Everything()).Count);
    }

    [Fact]
    public void AHiddenSuggestionStaysHiddenForThirtyDays()
    {
        var hidden = Everything() with { Dismissed = new Dictionary<string, DateTime> { ["TokenLeakGuard"] = Now.AddDays(-29) } };
        Assert.Equal([RecommendationId.HourlyCacheClear, RecommendationId.CheckTokenLeak, RecommendationId.StartAtSignIn], Ids(hidden, 3));
        var back = Everything() with { Dismissed = new Dictionary<string, DateTime> { ["TokenLeakGuard"] = Now.AddDays(-31) } };
        Assert.Equal(RecommendationId.TokenLeakGuard, RecommendationRules.Current(back)[0].Id);
        Assert.True(RecommendationRules.Hidden(hidden.Dismissed, "TokenLeakGuard", Now));
        Assert.False(RecommendationRules.Hidden(hidden.Dismissed, "TokenLeakGuard", Now.AddDays(2)));
    }

    // ------------------------------------------------------------------ plain words

    /// <summary>Every text the rules can produce: each suggestion, and the guide's rows in every token-leak state.</summary>
    private static IEnumerable<Recommendation> AllTexts()
    {
        var long1 = new string('x', 300);
        foreach (var r in RecommendationRules.Current(Everything() with
                 {
                     CostlyOptionalServices = [string.Join(" ", Enumerable.Repeat("Very long service name", 10)), "Fax"],
                     ReportableIncident = ("inc", "dir", string.Join(" ", Enumerable.Repeat("Kernel pool grew a lot", 20))),
                 }, max: 100)) yield return r;
        foreach (var r in RecommendationRules.Current(Everything() with { TokenSpawnLast24h = false, LeakTestNow = LeakVerdict.Leak, FileSystemMetadataGrewLast24h = false, DefenderCoresAvg30Min = 1, CostlyOptionalServices = [], SaturationOfferedTurnOffLast24h = true }, max: 100)) yield return r;
        foreach (var r in RecommendationRules.Current(Quiet(Facts() with { OwnClearTasks = ["t"], TokenLeak = TokenLeakStatus.NeedsTest }) with { SavedVerdictBuild = "1" }, max: 100)) yield return r;
        foreach (var status in Enum.GetValues<TokenLeakStatus>())
            foreach (var r in RecommendationRules.FirstRun(Quiet(Facts() with
                     {
                         TokenLeak = status, OptionalServicesRunning = 1, OptionalServiceExamples = [long1, long1], LogonTaskExists = status == TokenLeakStatus.Clean,
                         InstalledInProgramFiles = status != TokenLeakStatus.Unknown, OwnClearTasks = status == TokenLeakStatus.Leaks ? ["t"] : [],
                     }) with { AutoClearOn = status == TokenLeakStatus.NotAffected, TokenGuardOn = status == TokenLeakStatus.WorkedAroundAlready })) yield return r;
        foreach (var verdict in Enum.GetValues<LeakVerdict>())
            foreach (var r in RecommendationRules.FirstRun(Quiet(Facts() with { TokenLeak = TokenLeakStatus.NeedsTest }) with { LeakTestNow = verdict })) yield return r;
    }

    /// <summary>A camelCase word ("autoClear"), a setting-like name ("AutoClearEnabled"), the CI check of the UI test.</summary>
    private static readonly Regex CamelCase = new(@"\b[a-z]+[A-Z][A-Za-z]*\b");
    private static readonly Regex SettingLike = new(@"\b[A-Z][a-z]+(?:[A-Z][a-z]+)+(Enabled|Hours|Gb|Minutes)\b");
    /// <summary>"the kernel pool" is the one name users see; any other pool ("paged pool", "pool tag") is jargon.</summary>
    private static readonly Regex PoolJargon = new(@"(?<!\bkernel )\bpool\b", RegexOptions.IgnoreCase);

    [Fact]
    public void EveryTextIsShortAndPlain()
    {
        var settingNames = typeof(Settings).GetProperties().Select(p => p.Name).Where(n => n.Length > 6).ToList();
        Assert.Matches(PoolJargon, "Paged pool grew"); // the check itself: it still stops every other pool
        Assert.DoesNotMatch(PoolJargon, "Kernel pool grew, and so did the kernel pool");
        var all = AllTexts().ToList();
        Assert.True(all.Count >= 25, $"{all.Count} texts");
        Assert.Equal(Enum.GetValues<RecommendationId>().Length, all.Select(r => r.Id).Distinct().Count()); // every id is covered
        foreach (var r in all)
        {
            Assert.True(r.Title.Length <= RecommendationRules.MaxTitle, $"title too long: {r.Title}");
            Assert.True(r.Why.Length <= RecommendationRules.MaxWhy, $"why too long ({r.Why.Length}): {r.Why}");
            Assert.True(r.Button.Length <= RecommendationRules.MaxButton, $"button too long: {r.Button}");
            Assert.Equal(r.State == RecommendationState.Offer, r.Button.Length > 0);
            foreach (var text in new[] { r.Title, r.Why, r.Button })
            {
                Assert.False(CamelCase.IsMatch(text), $"camelCase in: {text}");
                Assert.False(SettingLike.IsMatch(text), $"setting name in: {text}");
                Assert.DoesNotContain(settingNames, n => text.Contains(n, StringComparison.Ordinal));
                Assert.DoesNotContain("!", text);
                Assert.False(PoolJargon.IsMatch(text), $"pool jargon in: {text}");
            }
        }
    }
}
