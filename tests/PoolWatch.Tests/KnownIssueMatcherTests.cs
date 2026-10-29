using System.Globalization;
using PoolWatch.Core;
using PoolWatch.Core.Cache;
using PoolWatch.Core.Detection;
using PoolWatch.Core.History;
using PoolWatch.Core.Knowledge;
using PoolWatch.Core.Model;
using PoolWatch.Core.Recommendations;
using PoolWatch.Core.Setup;

namespace PoolWatch.Tests;

/// <summary>
/// Known issues against evidence: each shipped issue is named on its own evidence, and not on unrelated evidence or while
/// an action in effect stops it; the evidence is gathered from memory, with a cache clear's drop and refill kept out of
/// the growth; and a matched issue gives the rules' own suggestion its reason without ever adding or removing one.
/// </summary>
public class KnownIssueMatcherTests
{
    /// <summary>Noon, local time: a clear a few hours before it was today wherever the tests run.</summary>
    private static readonly DateTime Now = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();
    private const long MB = 1 << 20, GB = 1 << 30;

    private static readonly RunningProcess CodexDesktop = new("ChatGPT.exe", "OpenAI.Codex");
    private static readonly RunningProcess ClaudeDesktop = new("claude.exe", "Claude");
    private static readonly RunningProcess ClaudeCode = new("claude.exe", null);

    /// <summary>A 25H2 PC with an NTFS system drive, where nothing grew and nothing was cleared.</summary>
    private static KnowledgeEvidence Quiet() => new() { NowUtc = Now, WindowsBuild = "26200.9106", FileSystems = ["NTFS"] };

    /// <summary>Growth over the last hour, per tag: (tag, paged MB, non-paged MB).</summary>
    private static KnowledgeEvidence Grew(KnowledgeEvidence e, params (string Tag, long PagedMb, long NonPagedMb)[] tags) => e with
    {
        GrowthOver = TimeSpan.FromHours(1),
        Growth = tags.ToDictionary(t => PoolTag.Parse(t.Tag), t => new TagGrowth(t.PagedMb * MB, t.NonPagedMb * MB)),
    };

    private static KnowledgeEvidence Cleared(KnowledgeEvidence e, DateTime at, params (string Tag, long FreedMb)[] tags) =>
        e with { Clears = [new ClearResult(at, tags.ToDictionary(t => PoolTag.Parse(t.Tag), t => t.FreedMb * MB))] };

    private static List<string> Ids(KnowledgeEvidence e) => KnownIssueMatcher.Match(KnowledgeBase.Embedded(), e).Select(m => m.Issue.Id).ToList();

    private static KnownIssueMatch Single(KnowledgeEvidence e, string id) => KnownIssueMatcher.Match(KnowledgeBase.Embedded(), e).Single(m => m.Issue.Id == id);

    private static string Clock(DateTime utc) => utc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ each shipped issue, on its evidence and not without it

    [Fact]
    public void TheTokenLeakIsSecurityTokensGrowingOnAnAffectedBuild()
    {
        var m = Single(Grew(Quiet(), ("Toke", 60, 0)), "windows-token-leak");
        Assert.Equal(["Security tokens grew 60 MB in the last hour.", "This PC runs Windows build 26200.9106."], m.Seen);
        Assert.Equal(["Toke"], m.Tags.Select(t => t.ToDisplay()));
        Assert.Equal(["windows-token-leak"], Ids(Grew(Quiet() with { WindowsBuild = "26100.4061" }, ("Toke", 60, 0))));
    }

    [Theory]
    [InlineData("22631.4317", 60, 0)]   // Windows 11 23H2: not an affected build
    [InlineData(null, 60, 0)]           // the build is not known yet
    [InlineData("26200.9106", 15, 0)]   // too slow to be the leak
    [InlineData("26200.9106", 0, 60)]   // tokens live in paged memory
    public void TheTokenLeakIsNotNamedWithoutItsEvidence(string? build, long pagedMb, long nonPagedMb) =>
        Assert.Empty(Ids(Grew(Quiet() with { WindowsBuild = build }, ("Toke", pagedMb, nonPagedMb))));

    [Fact]
    public void NtfcGrowingWhileAnAiDesktopAppRunsMatchesTheReports()
    {
        var claude = Grew(Quiet() with { Running = [ClaudeDesktop] }, ("NtFC", 0, 2048));
        var m = Single(claude, "ntfc-ai-desktop-apps");
        Assert.Equal(["NTFS file records grew 2.0 GB in the last hour.", "Claude desktop is running."], m.Seen);
        Assert.Equal(["ntfc-ai-desktop-apps"], Ids(claude)); // the shader cache is the Codex app's

        // With the Codex desktop app both are named: the shader cache as what may help, the reports as what is known.
        Assert.Equal(["codex-gpu-shader-cache", "ntfc-ai-desktop-apps"], Ids(Grew(Quiet() with { Running = [CodexDesktop] }, ("NtFC", 0, 2048))));
        Assert.Equal(["NTFS file records grew 2.0 GB in the last hour.", "Codex desktop is running."],
            Single(Grew(Quiet() with { Running = [CodexDesktop] }, ("NtFC", 0, 2048)), "codex-gpu-shader-cache").Seen);
        // A newer Claude package name counts too. No report names the ChatGPT desktop app, so it names neither.
        Assert.Equal(["ntfc-ai-desktop-apps"], Ids(Grew(Quiet() with { Running = [new("claude.exe", "AnthropicPBC.Claude")] }, ("NtFC", 0, 2048))));
        Assert.Empty(Ids(Grew(Quiet() with { Running = [new("ChatGPT.exe", "OpenAI.ChatGPT-Desktop")] }, ("NtFC", 0, 2048))));
    }

    [Fact]
    public void NtfcIsNotNamedWithoutBothTheGrowthAndTheApp()
    {
        Assert.Empty(Ids(Grew(Quiet(), ("NtFC", 0, 2048))));                                          // no AI desktop app
        Assert.Empty(Ids(Grew(Quiet() with { Running = [ClaudeCode] }, ("NtFC", 0, 2048))));           // Claude Code is not a packaged app
        Assert.Empty(Ids(Grew(Quiet() with { Running = [new("ChatGPT.exe", null)] }, ("NtFC", 0, 2048)))); // a file name alone is not the package
        Assert.Empty(Ids(Quiet() with { Running = [CodexDesktop, ClaudeDesktop] }));                     // the apps, but no growth
        Assert.Empty(Ids(Grew(Quiet() with { Running = [CodexDesktop] }, ("NtFC", 0, 90))));           // under 100 MB an hour
        Assert.Empty(Ids(Grew(Quiet() with { Running = [CodexDesktop] }, ("NtFC", 2048, 0))));         // paged, not the reported leak
    }

    [Fact]
    public void AProgramIsMatchedByItsFileNameInAnyCase()
    {
        // No shipped pack names a program by its file name yet; this one does, as a contributor's might.
        var pack = """
            {
              "format": 1, "id": "by-file-name", "title": "By file name", "explanation": "Test.", "workaround": "Test.",
              "evidence": [ { "tagGrowth": { "tags": ["Abcd"], "name": "test records", "minMBPerHour": 1 },
                              "running": [ { "name": "Test app", "image": "ChatGPT.exe" } ] } ],
              "links": [ { "title": "A report", "url": "https://github.com/example/repo/issues/1" } ]
            }
            """;
        var knowledge = KnowledgeBase.Load([("by-file-name.json", pack)]);
        Assert.Empty(knowledge.Problems);
        IReadOnlyList<KnownIssueMatch> With(RunningProcess p) => KnownIssueMatcher.Match(knowledge, Grew(Quiet() with { Running = [p] }, ("Abcd", 10, 0)));

        Assert.Equal("Test app is running.", Assert.Single(With(new("chatgpt.exe", null))).Seen[1]);
        Assert.Single(With(new("CHATGPT.EXE", "OpenAI.Codex"))); // from a Store package as well
        Assert.Empty(With(new("ChatGPT2.exe", null)));
    }

    [Fact]
    public void ReFsRecordsFreedByAClearMatchOnAPcWithAReFsDrive()
    {
        var at = Now.AddHours(-2);
        // The shared file-name cache (FMfn) is freed too, but every file system has it: it is not counted as ReFS's.
        var refs = Cleared(Quiet() with { FileSystems = ["NTFS", "ReFS"] }, at,
            ("Reff", 2048), ("RefF", 256), ("RLin", 512), ("MSPi", 512), ("MSeb", 128), ("MSb+", 128), ("FMfn", 1024), ("MmSt", 4096));
        var m = Single(refs, "refs-standby-metadata");
        Assert.Equal([$"The cache clear at {Clock(at)} freed 3.5 GB of ReFS file records.", "This PC has a drive formatted as ReFS."], m.Seen);
        Assert.Equal(["Reff", "RefF", "RLin", "MSPi", "MSeb", "MSb+"], m.Tags.Select(t => t.ToDisplay()));

        // Without a clear, fast growth of the same records says the same on a ReFS PC.
        Assert.Equal("ReFS file records grew 400 MB in the last hour.",
            Single(Grew(Quiet() with { FileSystems = ["ReFS"] }, ("Reff", 300, 0), ("MSb+", 100, 0), ("FMfn", 100, 0)), "refs-standby-metadata").Seen[0]);
        // With both, the issue is named once, on the first rule that holds: what the clear freed.
        Assert.Equal(m.Seen, Single(Grew(refs, ("Reff", 300, 0)), "refs-standby-metadata").Seen);
    }

    [Fact]
    public void ReFsRecordsAreNotNamedOnAnNtfsPcOrForLittle()
    {
        var at = Now.AddHours(-2);
        Assert.Empty(Ids(Cleared(Quiet(), at, ("Reff", 2048), ("FMfn", 512))));                                   // NTFS only: do not assume
        Assert.Empty(Ids(Cleared(Quiet() with { FileSystems = ["ReFS"] }, at, ("Reff", 100), ("FMfn", 100))));      // under 256 MB
        Assert.Empty(Ids(Cleared(Quiet() with { FileSystems = ["ReFS"] }, at, ("MmSt", 4096), ("NtFC", 2048))));    // other tags
        Assert.Empty(Ids(Grew(Quiet() with { FileSystems = ["ReFS"] }, ("Reff", 100, 0))));                       // slow growth
        // The shared file-name cache alone, freed or growing, is not ReFS's, even on a PC with a ReFS drive.
        Assert.Empty(Ids(Cleared(Quiet() with { FileSystems = ["NTFS", "ReFS"] }, at, ("FMfn", 1024))));
        Assert.Empty(Ids(Grew(Quiet() with { FileSystems = ["NTFS", "ReFS"] }, ("FMfn", 300, 0))));
    }

    [Fact]
    public void TheClearNamedIsTheNewestThatFreedEnoughAndOneFromYesterdaySaysSo()
    {
        static ClearResult Freed(DateTime at, long reffMb) => new(at, new Dictionary<PoolTag, long> { [PoolTag.Parse("Reff")] = reffMb * MB });
        var refs = Quiet() with { FileSystems = ["ReFS"] };
        var yesterday = Now.AddHours(-14); // 22:00 the day before, local time
        var lastHour = Now.AddHours(-1);

        // Yesterday's clear freed the most and the newest freed too little: the one named is the newest that freed enough.
        var e = refs with { Clears = [Freed(yesterday, 4096), Freed(lastHour, 512), Freed(Now.AddMinutes(-10), 100)] };
        Assert.Equal($"The cache clear at {Clock(lastHour)} freed 512 MB of ReFS file records.", Single(e, "refs-standby-metadata").Seen[0]);

        // Only yesterday's: "at 22:00" alone would read as later today.
        Assert.Equal($"The cache clear yesterday at {Clock(yesterday)} freed 4.0 GB of ReFS file records.",
            Single(refs with { Clears = [Freed(yesterday, 4096)] }, "refs-standby-metadata").Seen[0]);
    }

    [Fact]
    public void UnrelatedEvidenceNamesNothing()
    {
        Assert.Empty(Ids(Quiet()));
        var busy = Grew(Quiet() with { FileSystems = ["NTFS", "ReFS"], Running = [ClaudeCode, new("node.exe", null), new("Code.exe", null)] },
            ("File", 0, 5120), ("MmSt", 3072, 0), ("Even", 0, 512));
        Assert.Empty(Ids(Cleared(busy, Now.AddHours(-1), ("MmSt", 4096), ("CcVl", 512))));
        // Growth measured over too short a span says nothing, however large.
        Assert.Empty(Ids(Grew(Quiet() with { Running = [CodexDesktop] }, ("Toke", 900, 0), ("NtFC", 0, 9000)) with { GrowthOver = TimeSpan.FromMinutes(9) }));
    }

    [Fact]
    public void TheEvidenceSaysHowLongTheGrowthWasMeasuredOver()
    {
        // 10 MB in 25 minutes is 24 MB an hour: over the 20 MB an hour the token leak asks for, though 10 MB is not.
        var e = Grew(Quiet(), ("Toke", 10, 0)) with { GrowthOver = TimeSpan.FromMinutes(25) };
        Assert.Equal("Security tokens grew 10 MB in the last 25 minutes.", Single(e, "windows-token-leak").Seen[0]);
    }

    // ------------------------------------------------------------------ the notes

    [Fact]
    public void TheNoteSaysWhatPoolWatchSeesWhatHelpsAndWhoFoundIt()
    {
        var shown = RecommendationRules.OnEvidence(ThreeOffers());
        var lines = Single(Grew(Quiet(), ("Toke", 60, 0)), "windows-token-leak").Note(shown).Split(Environment.NewLine);
        Assert.Equal("Known issue: The Windows 11 token leak", lines[0]);
        Assert.Equal("What PoolWatch sees: Security tokens grew 60 MB in the last hour. This PC runs Windows build 26200.9106.", lines[1]);
        Assert.StartsWith("What is going on: On Windows 11 25H2", lines[2]);
        Assert.StartsWith("What helps: A Windows setting stops the leak", lines[3]);
        Assert.Equal($"PoolWatch can turn on its token-leak guard: untick Advanced view to see the suggestion \"{Title(RecommendationId.TokenLeakGuard)}\".", lines[4]);
        Assert.Equal("Read more: Windows 11 leaks a kernel token for every process that creates a process: https://github.com/bentoner/windows-token-leak", lines[5]);
        Assert.Equal("Credit: bentoner documented the leak, its cause and the setting that stops it (https://github.com/bentoner/windows-token-leak).", lines[7]);
        Assert.Equal("Pool tags: Toke", lines[^1]);

        var ntfc = Single(Grew(Quiet() with { Running = [ClaudeDesktop] }, ("NtFC", 0, 2048)), "ntfc-ai-desktop-apps").Note(shown);
        Assert.Contains("What helps: PoolWatch has no sure fix for this", ntfc);
        Assert.DoesNotContain("PoolWatch can ", ntfc);
    }

    /// <summary>The title of a suggestion the rules offer on <see cref="ThreeOffers"/>: the note names the card by it.</summary>
    private static string Title(RecommendationId id) => RecommendationRules.OnEvidence(ThreeOffers()).Single(r => r.Id == id).Title;

    [Fact]
    public void TheNoteSaysWhatPoolWatchAlreadyDoesAndPointsOnlyAtASuggestionOnOffer()
    {
        // The note's line about the action, with the actions in effect and the suggestions shown on these inputs.
        string? ActionLine(KnowledgeEvidence e, RecommendationInputs inputs, string id) =>
            Single(e with { ActionsOn = KnownActions.InEffect(inputs, DefaultLockTimeout) }, id).Note(RecommendationRules.OnEvidence(inputs))
                .Split(Environment.NewLine).SingleOrDefault(l => l.StartsWith("PoolWatch "));

        // The hourly clear is on and each clear still frees gigabytes of ReFS records: the issue is named, as already handled.
        var refs = Cleared(Quiet() with { FileSystems = ["ReFS"] }, Now.AddHours(-1), ("Reff", 2048));
        Assert.Equal("PoolWatch already clears the cache every hour and shows what each clear freed.",
            ActionLine(refs, ThreeOffers() with { AutoClearOn = true }, "refs-standby-metadata"));
        Assert.Equal($"PoolWatch can clear the cache every hour and show what each clear freed: untick Advanced view to see the suggestion \"{Title(RecommendationId.HourlyCacheClear)}\".",
            ActionLine(refs, ThreeOffers(), "refs-standby-metadata"));
        // Hidden in the last 30 days, so not offered: the note promises nothing.
        var hidden = new Dictionary<string, DateTime> { [Recommendation.KeyOf(RecommendationId.HourlyCacheClear, null)] = Now };
        Assert.Null(ActionLine(refs, ThreeOffers() with { Dismissed = hidden }, "refs-standby-metadata"));

        // The shortcut's words name no app: the line above them in the note has just said which one is running.
        var codex = Grew(Quiet() with { Running = [CodexDesktop] }, ("NtFC", 0, 2048));
        Assert.Equal($"PoolWatch can create a desktop shortcut that starts the app without its GPU shader disk cache: untick Advanced view to see the suggestion \"{Title(RecommendationId.ChatGptShortcut)}\".",
            ActionLine(codex, ThreeOffers(), "codex-gpu-shader-cache"));
        Assert.Equal("PoolWatch already made a desktop shortcut that starts the app without its GPU shader disk cache; use it instead of the usual one.",
            ActionLine(codex, ThreeOffers() with { ChatGptShortcutMade = true }, "codex-gpu-shader-cache"));
        // No growth linked to the app yet, so no shortcut is offered: the note promises nothing.
        Assert.Null(ActionLine(codex, ThreeOffers() with { ChatGptNamedInGrowth = false }, "codex-gpu-shader-cache"));

        // A setup guide row that is not an offer (this build tested clean) is never pointed at.
        var guide = RecommendationRules.FirstRun(ThreeOffers() with { Facts = Facts() with { TokenLeak = TokenLeakStatus.Clean } });
        Assert.Equal(RecommendationState.Info, guide.Single(r => r.Id == RecommendationId.TokenLeakGuard).State);
        Assert.DoesNotContain("PoolWatch can ", Single(Grew(Quiet(), ("Toke", 120, 0)), "windows-token-leak").Note(guide));
    }

    [Fact]
    public void AnIssueIsNotNamedWhileAnActionInEffectStopsIt()
    {
        // Tokens grew 60 MB over the hour on a 25H2 PC: the known leak, unless something already stops it, when the
        // growth has another cause (a program leaking token handles, say).
        var h = History(i => [("Toke", i * MB, 0)]);
        var inputs = new RecommendationInputs { NowUtc = Now, Facts = Facts() };
        Assert.Equal(["windows-token-leak"], Ids(Gather(h, inputs: inputs)));
        // Stopped while the Windows setting that stops the leak is on now, whoever set it.
        Assert.Empty(Ids(Gather(h, inputs: inputs, lockTimeout: 0)));
        // The guard's setting alone does not stop it (Windows refused the change, or the guard left Windows alone), nor
        // does a scan that once found the leak worked around; a timeout that cannot be read stops nothing either.
        Assert.Equal(["windows-token-leak"],
            Ids(Gather(h, inputs: inputs with { TokenGuardOn = true, Facts = Facts() with { TokenLeak = TokenLeakStatus.WorkedAroundAlready } })));
        Assert.Equal(["windows-token-leak"], Ids(Gather(h, inputs: inputs with { TokenGuardOn = true }, lockTimeout: null)));

        // The hourly clear only keeps the ReFS records down, so that issue is still named while it is on; and an action
        // that stops one issue leaves the others named.
        Assert.Equal(["refs-standby-metadata"],
            Ids(Cleared(Quiet() with { FileSystems = ["ReFS"], ActionsOn = ["auto-clear"] }, Now.AddHours(-1), ("Reff", 2048))));
        Assert.Equal(["refs-standby-metadata"],
            Ids(Cleared(Grew(Quiet() with { FileSystems = ["ReFS"], ActionsOn = ["token-guard", "auto-clear"] }, ("Toke", 120, 0)), Now.AddHours(-1), ("Reff", 2048))));
    }

    // ------------------------------------------------------------------ reasons on the rules' own suggestions

    private static EnvironmentFacts Facts() => new()
    {
        ScannedUtc = Now.AddHours(-1), WindowsName = "Windows 11 Pro", DisplayVersion = "25H2", Build = "26200.9106", Architecture = "x64",
        Volumes = [new(@"C:\", "NTFS", false, null), new(@"D:\", "ReFS", true, true)], TokenLeak = TokenLeakStatus.Clean,
        LogonTaskExists = true, InstalledInProgramFiles = true, ChatGptInstalled = true,
    };

    /// <summary>Inputs on which the rules offer the hourly clear, the token-leak guard and the ChatGPT shortcut.</summary>
    private static RecommendationInputs ThreeOffers() => new()
    {
        NowUtc = Now, Facts = Facts(), AutoClearOn = false, TokenGuardOn = false, TokenSpawnLast24h = true,
        ChatGptNamedInGrowth = true, ChatGptLauncherInstalled = true,
    };

    [Fact]
    public void AMatchedIssueGivesTheRulesOwnSuggestionItsReason()
    {
        var rules = RecommendationRules.OnEvidence(ThreeOffers());
        Assert.Equal([RecommendationId.TokenLeakGuard, RecommendationId.HourlyCacheClear, RecommendationId.ChatGptShortcut], rules.Select(r => r.Id));
        var at = Now.AddHours(-1);
        var matches = KnownIssueMatcher.Match(KnowledgeBase.Embedded(),
            Cleared(Quiet() with { FileSystems = ["ReFS"] }, at, ("Reff", 3072), ("FMfn", 1024)));

        var explained = KnownIssueMatcher.Explain(rules, matches);
        Assert.Equal($"The cache clear at {Clock(at)} freed 3.0 GB of ReFS file records. That is ReFS keeping records for files in the " +
                     "cache, and an hourly clear keeps them down; your files are not touched.", explained[1].Why);
        // Only the reason changes: the same suggestion, button, place and Hide key.
        Assert.Equal(rules[1] with { Why = explained[1].Why }, explained[1]);
        Assert.Equal(rules[0], explained[0]);
        Assert.Equal(rules[2], explained[2]);
    }

    [Fact]
    public void TheGuardsReasonKeepsItsTradeOff()
    {
        var rules = RecommendationRules.OnEvidence(ThreeOffers());
        var matches = KnownIssueMatcher.Match(KnowledgeBase.Embedded(), Grew(Quiet(), ("Toke", 120, 0)));
        var guard = KnownIssueMatcher.Explain(rules, matches)[0];
        Assert.Equal("Security tokens grew 120 MB in the last hour. This is the known Windows token leak. The guard turns on the " +
                     "Windows setting that stops it, each time PoolWatch starts. With it on, any app may bring its window to the front.", guard.Why);
        // The notice is the rules' own trade-off sentence, so the two can never drift apart.
        string notice = KnownActions.Find("token-guard")!.Notice!;
        Assert.EndsWith(notice, rules[0].Why);
        Assert.EndsWith(notice, guard.Why);

        // The setup guide's row, offered when this build leaks, still says when the guard works once explained.
        var guide = RecommendationRules.FirstRun(ThreeOffers() with { Facts = Facts() with { TokenLeak = TokenLeakStatus.Leaks } });
        var row = KnownIssueMatcher.Explain(guide, matches).Single(r => r.Id == RecommendationId.TokenLeakGuard);
        Assert.Equal(guard.Why, row.Why);
        Assert.Contains("each time PoolWatch starts", row.Why);
        Assert.DoesNotContain("sign-in", row.Why);
    }

    [Fact]
    public void TheShortcutSuggestionGetsTheShaderCacheReason()
    {
        var rules = RecommendationRules.OnEvidence(ThreeOffers());
        var matches = KnownIssueMatcher.Match(KnowledgeBase.Embedded(), Grew(Quiet() with { Running = [CodexDesktop] }, ("NtFC", 0, 6144)));
        Assert.Equal("NTFS file records grew 6.0 GB in the last hour. Starting the app without its GPU shader disk cache stopped this for one " +
                     "user (found by Faarfarin on GitHub). The shortcut starts it that way.", KnownIssueMatcher.Explain(rules, matches)[2].Why);
    }

    [Fact]
    public void AKnownIssueNeverAddsOrRemovesASuggestion()
    {
        var everything = Cleared(Grew(Quiet() with { FileSystems = ["ReFS"], Running = [CodexDesktop] }, ("Toke", 120, 0), ("NtFC", 0, 6144)),
            Now.AddHours(-1), ("Reff", 3072));
        var matches = KnownIssueMatcher.Match(KnowledgeBase.Embedded(), everything);
        Assert.Equal(4, matches.Count);

        // Everything already done: the rules offer nothing, and the known issues add nothing.
        var done = ThreeOffers() with { AutoClearOn = true, TokenGuardOn = true, ChatGptShortcutMade = true };
        Assert.Empty(KnownIssueMatcher.Explain(RecommendationRules.OnEvidence(done), matches));

        // The guide's rows that are not offers keep their own words.
        var guide = RecommendationRules.FirstRun(done);
        Assert.Equal(guide, KnownIssueMatcher.Explain(guide, matches));

        // No match: the suggestions come back as they were.
        var rules = RecommendationRules.OnEvidence(ThreeOffers());
        Assert.Equal(rules, KnownIssueMatcher.Explain(rules, []));
    }

    [Fact]
    public void AReasonTooLongForACardLeavesTheRulesOwn()
    {
        // The longest suggestion and name allowed, plus the guard's notice: over the card's 240 characters.
        var pack = $$"""
            {
              "format": 1, "id": "wordy", "title": "Wordy", "explanation": "Long.", "workaround": "Long.",
              "actions": ["token-guard"], "suggestion": "{{new string('a', KnowledgeBase.MaxSuggestion)}}",
              "evidence": [ { "tagGrowth": { "tags": ["Abcd"], "name": "{{new string('b', KnowledgeBase.MaxName)}}", "minMBPerHour": 1 } } ],
              "links": [ { "title": "A report", "url": "https://github.com/example/repo/issues/1" } ]
            }
            """;
        var knowledge = KnowledgeBase.Load([("wordy.json", pack)]);
        var matches = KnownIssueMatcher.Match(knowledge, Grew(Quiet(), ("Abcd", 999_000, 0)));
        Assert.Single(matches);
        var rules = RecommendationRules.OnEvidence(ThreeOffers());
        Assert.Equal(rules, KnownIssueMatcher.Explain(rules, matches));
    }

    [Fact]
    public void EveryShippedReasonFitsOnACardWithTheLargestNumbers()
    {
        foreach (var issue in KnowledgeBase.Embedded().Entries.Where(i => i.Actions.Count > 0))
            foreach (var rule in issue.Evidence)
            {
                // Evidence that holds for this rule alone, with the widest numbers and words: a clear from yesterday at 23:59.
                var e = Quiet() with
                {
                    WindowsBuild = (rule.WindowsBuild?.Min ?? 26200) + ".9999", FileSystems = rule.FileSystems ?? [],
                    Running = rule.Running?.Take(1).Select(p => new RunningProcess(p.Image ?? "app.exe", p.StorePackage)).ToList() ?? [],
                    GrowthOver = TimeSpan.FromMinutes(54),
                    Growth = (rule.TagGrowth?.Tags ?? []).ToDictionary(PoolTag.Parse, _ => new TagGrowth(999 * GB, 999 * GB)),
                    Clears = rule.ClearFreed is { } c ? [new ClearResult(Now.ToLocalTime().Date.AddMinutes(-1).ToUniversalTime(),
                        c.Tags.ToDictionary(PoolTag.Parse, _ => 999 * GB))] : [],
                };
                var matches = KnownIssueMatcher.Match(KnowledgeBase.Embedded(), e).Where(m => m.Issue == issue).ToList();
                Assert.Single(matches);
                var offer = new Recommendation(KnownActions.Find(issue.Actions[0])!.Suggestion, "Title", "The rules' own reason.", "Turn on", RecommendationAction.Apply, 1);
                string why = KnownIssueMatcher.Explain([offer], matches)[0].Why;
                Assert.NotEqual(offer.Why, why);
                Assert.True(why.Length <= RecommendationRules.MaxWhy, $"{issue.Id}: {why.Length} characters");
            }
    }

    // ------------------------------------------------------------------ gathering the evidence from memory

    private static readonly DateTime T0 = Now.AddMinutes(-70);

    /// <summary>A history of one sample a minute from 70 minutes ago to now; <paramref name="tags"/> gives each tag's (paged, non-paged) bytes at a minute.</summary>
    private static History History(Func<int, IEnumerable<(string Tag, long Paged, long NonPaged)>> tags)
    {
        var h = new History();
        for (int i = 0; i <= 70; i++)
            h.Add(new Sample(T0.AddMinutes(i), default,
                tags(i).Select(t => new PoolTagEntry(PoolTag.Parse(t.Tag), 1, 0, (ulong)t.Paged, 1, 0, (ulong)t.NonPaged)).ToList(), []));
        return h;
    }

    /// <summary>Windows' own foreground lock timeout: the setting that stops the token leak is off.</summary>
    private const uint DefaultLockTimeout = 200_000;

    /// <summary>The evidence on this history; by default before the first scan, with nothing turned on.</summary>
    private static KnowledgeEvidence Gather(History h, IReadOnlyList<ClearRecord>? clears = null, RecommendationInputs? inputs = null,
        IEnumerable<ProcessRecord>? running = null, uint? lockTimeout = DefaultLockTimeout) =>
        KnowledgeEvidence.Gather(KnowledgeBase.Embedded(), h, clears ?? [], TimeSpan.FromMinutes(2), inputs ?? new RecommendationInputs { NowUtc = Now }, lockTimeout, running ?? []);

    private static ClearRecord Clear(DateTime at, params (string Tag, long Before, long After)[] tags) => new()
    {
        TimeUtc = at, Mode = ClearMode.Standby, Trigger = ClearTrigger.Schedule, Before = new PoolTotals(8UL << 30, 4UL << 30, 0, 0),
        TagsBefore = tags.Select(t => (PoolTag.Parse(t.Tag), t.Before)).ToList(),
        After = new PoolTotals(4UL << 30, 3UL << 30, 0, 0), TagsAfter = tags.Where(t => t.After >= 0).Select(t => (PoolTag.Parse(t.Tag), t.After)).ToList(),
    };

    [Fact]
    public void GrowthIsMeasuredOverTheLastHourForThePacksTagsOnly()
    {
        var h = History(i => [("Toke", 100 * MB + i * MB, 0), ("NtFC", 0, 50 * MB + 2 * i * MB), ("File", 0, i * 10 * MB)]);
        var e = Gather(h);
        Assert.Equal(TimeSpan.FromHours(1), e.GrowthOver);
        Assert.Equal(new TagGrowth(60 * MB, 0), e.Growth[PoolTag.Parse("Toke")]);
        Assert.Equal(new TagGrowth(0, 120 * MB), e.Growth[PoolTag.Parse("NtFC")]);
        Assert.False(e.Growth.ContainsKey(PoolTag.Parse("File"))); // no pack asks about it
        Assert.Equal(KnowledgeBase.Embedded().GrowthTags.Order(), e.Growth.Keys.Order());
    }

    [Fact]
    public void AFallWithinTheHourDoesNotHideTheClimbAfterIt()
    {
        // Toke falls from 500 MB to 100 MB (programs exited; no clear) and climbs to 400 MB: it grew 300 MB, not -100 MB.
        var h = History(i => [("Toke", (i < 30 ? 500 - i * 400 / 30 : 100 + (i - 30) * 300 / 40) * MB, 0)]);
        Assert.Equal(300 * MB, Gather(h).Growth[PoolTag.Parse("Toke")].PagedBytes);
    }

    [Fact]
    public void AClearsDropIsNotGrowthAndNeitherIsItsRefill()
    {
        // Reff held 2 GB; a clear 20 minutes ago dropped it to 100 MB, the cache refilled it to 200 MB within the 2 minutes
        // before the clear is measured, and it has grown to 500 MB since. Over the hour, growth is the 300 MB since the
        // clear's level: not a fall (the drop is the clear's doing) and not 400 MB (the refill is the clear's too).
        var clearAt = Now.AddMinutes(-20);
        long Reff(int minute) => T0.AddMinutes(minute) switch
        {
            var t when t < clearAt => 2 * GB,
            var t when t < clearAt.AddMinutes(2) => 100 * MB,
            var t => 200 * MB + (long)((t - clearAt.AddMinutes(2)).TotalMinutes * 300 / 18) * MB,
        };
        var h = History(i => [("Reff", Reff(i), 0)]);
        var e = Gather(h, [Clear(clearAt, ("Reff", 2 * GB, 200 * MB))]);
        Assert.Equal(TimeSpan.FromHours(1), e.GrowthOver);
        Assert.Equal(300 * MB, e.Growth[PoolTag.Parse("Reff")].PagedBytes);

        // A failed clear changed nothing, so the hour still counts whole.
        var failed = Clear(clearAt);
        failed.Error = "access denied";
        Assert.Equal(TimeSpan.FromHours(1), Gather(h, [failed]).GrowthOver);
    }

    [Fact]
    public void GrowthIsMeasuredAcrossClearsAsTheDetectorMeasuresIt()
    {
        // The detector's one baseline for PoolWatch's own clears: each clear's drop added back, its interval left out. Toke
        // grows 1 MB a minute. Clears dated after the newest sample (the clock was set back since), or that freed none of
        // it, leave the hour whole; a clear that freed 30 MB of it 20 minutes ago takes nothing off the growth before it
        // or after it, only its own interval (from the sample before it to the one that measured it: 3 MB here, Toke's
        // growth in the clear's 2 minutes being the clear's).
        var h = History(i => [("Toke", 100 * MB + i * MB, 0)]);
        var toke = PoolTag.Parse("Toke");
        foreach (var clears in new ClearRecord[][] { [Clear(Now.AddMinutes(1))], [Clear(Now.AddMinutes(-40)), Clear(Now.AddMinutes(-20))] })
        {
            var e = Gather(h, clears);
            Assert.Equal(TimeSpan.FromHours(1), e.GrowthOver);
            Assert.Equal(60 * MB, e.Growth[toke].PagedBytes);
        }
        var clearAt = Now.AddMinutes(-20);
        var trimmed = History(i => [("Toke", 100 * MB + i * MB - (T0.AddMinutes(i) >= clearAt ? 30 * MB : 0), 0)]);
        var cleared = Gather(trimmed, [Clear(clearAt)]);
        Assert.Equal(TimeSpan.FromHours(1), cleared.GrowthOver);
        Assert.Equal(57 * MB, cleared.Growth[toke].PagedBytes);
        // An hour that starts inside a clear's interval starts at its measurement, and its rate is judged over what is left.
        var early = Gather(h, [Clear(Now.AddMinutes(-61))]);
        Assert.Equal(TimeSpan.FromMinutes(59), early.GrowthOver);
        Assert.Equal(59 * MB, early.Growth[toke].PagedBytes);
        // A clear that finds Toke flat (it stopped growing 25 minutes ago) and leaves it a page lower: the page is the clear's,
        // added back, and the hour holds 35 MB of growth.
        const long KB = 1 << 10;
        var wobble = History(i => [("Toke", 100 * MB + Math.Min(i, 45) * MB - (T0.AddMinutes(i) >= clearAt ? 4 * KB : 0), 0)]);
        Assert.Equal(35 * MB, Gather(wobble, [Clear(clearAt)]).Growth[toke].PagedBytes);
    }

    [Fact]
    public void ATagGrowingJustOverAPacksRateThatAClearTrimsReadsUnderItAndTheRateIsNeverLowered()
    {
        // Tokens grow 350 KB a minute (20.5 MB an hour, over the token leak's 20 MB). With no clear the leak is named. A clear
        // 20 minutes ago trims 30 MB of them: their growth in the clear's 3 minutes is the clear's, and the pack keeps its own
        // 20 MB an hour, so the hour holds 57 minutes of growth (19.5 MB) and the leak is not named while the hour holds the
        // clear (the limit: here 3 of the hour's 60 samples at a sample a minute; about 4% over a rate with hourly clears at
        // the default 10-second sampling).
        const long KB = 1 << 10;
        var clearAt = Now.AddMinutes(-20);
        var inputs = new RecommendationInputs { NowUtc = Now, Facts = Facts() };
        var plain = Gather(History(i => [("Toke", 100 * MB + i * 350 * KB, 0)]), inputs: inputs);
        Assert.Equal((TimeSpan.FromHours(1), new TagGrowth(60 * 350 * KB, 0)), (plain.GrowthOver, plain.Growth[PoolTag.Parse("Toke")]));
        Assert.Equal(["windows-token-leak"], Ids(plain));
        Assert.Contains($"Security tokens grew {Fmt.Bytes(60 * 350 * KB)} in the last hour.", Single(plain, "windows-token-leak").Seen);
        var h = History(i => [("Toke", 100 * MB + i * 350 * KB - (T0.AddMinutes(i) >= clearAt ? 30 * MB : 0), 0)]);
        var e = Gather(h, [Clear(clearAt)], inputs: inputs);
        Assert.Equal((TimeSpan.FromHours(1), new TagGrowth(57 * 350 * KB, 0)), (e.GrowthOver, e.Growth[PoolTag.Parse("Toke")]));
        Assert.Empty(Ids(e));
        // ReFS file records name no pool, so both count. Reff's paged part grows 160 MB an hour and the clear trims it (152 MB
        // in the hour, its 3 minutes being the clear's); its non-paged part grows 110 MB an hour and the clear frees none of
        // it: 262 MB together, over the 256 MB the ReFS pack asks. Either part alone is under it.
        var refs = Gather(History(i => [("Reff", 100 * MB + i * 8 * MB / 3 - (T0.AddMinutes(i) >= clearAt ? 30 * MB : 0), 50 * MB + i * 110 * MB / 60)]),
            [Clear(clearAt)], inputs: inputs);
        var reff = refs.Growth[PoolTag.Parse("Reff")];
        Assert.Equal((152 * MB, 110 * MB), (reff.PagedBytes, reff.NonPagedBytes));
        Assert.Contains("refs-standby-metadata", Ids(refs));
        Assert.DoesNotContain("refs-standby-metadata", Ids(refs with { Growth = new Dictionary<PoolTag, TagGrowth> { [PoolTag.Parse("Reff")] = reff with { NonPagedBytes = 0 } } }));
        Assert.DoesNotContain("refs-standby-metadata", Ids(refs with { Growth = new Dictionary<PoolTag, TagGrowth> { [PoolTag.Parse("Reff")] = reff with { PagedBytes = 0 } } }));
    }

    [Fact]
    public void WhileAClearIsMeasuredNoGrowthIsKnown()
    {
        // Until the clear is measured nothing can be told across it (the detector holds then too); right after, the hour is
        // known again.
        var h = History(i => [("Toke", i * 10 * MB, 0)]);
        var measuring = Clear(Now.AddMinutes(-1));
        measuring.After = null;
        var e = Gather(h, [measuring]);
        Assert.Equal(TimeSpan.Zero, e.GrowthOver);
        Assert.Empty(e.Growth);
        Assert.Empty(KnownIssueMatcher.Match(KnowledgeBase.Embedded(), e with { WindowsBuild = "26200.9106" }));
        var after = Gather(h, [Clear(Now.AddMinutes(-9))]);
        Assert.Equal(TimeSpan.FromHours(1), after.GrowthOver);
        Assert.Equal(600 * MB, after.Growth[PoolTag.Parse("Toke")].PagedBytes); // the clear freed none of it
        Assert.Equal(TimeSpan.Zero, Gather(new History()).GrowthOver); // nothing sampled yet
    }

    [Fact]
    public void OnlyMeasuredClearsOfTheLastDayWithTheirTagsCount()
    {
        var recent = Clear(Now.AddHours(-3), ("Reff", 2 * GB, 200 * MB), ("FMfn", 100 * MB, 150 * MB), ("RLin", 300 * MB, -1));
        var old = Clear(Now.AddDays(-2), ("Reff", 2 * GB, 0));
        var readBack = Clear(Now.AddHours(-1)); // read back after a restart: totals only, no tags
        var measuring = Clear(Now.AddMinutes(-1), ("Reff", 2 * GB, 0));
        measuring.After = null;
        var halfWritten = Clear(Now.AddMinutes(-30), ("Reff", 2 * GB, 0));
        halfWritten.TagsAfter = []; // the totals are in, the tags not yet
        var failed = Clear(Now.AddHours(-2), ("Reff", 2 * GB, 0));
        failed.Error = "access denied";
        var future = Clear(Now.AddMinutes(30), ("Reff", 2 * GB, 0)); // the clock was set back since

        var e = Gather(History(i => []), [old, recent, failed, readBack, halfWritten, measuring, future]);
        var only = Assert.Single(e.Clears);
        Assert.Equal(recent.TimeUtc, only.TimeUtc);
        Assert.Equal(2 * GB - 200 * MB, only.Freed[PoolTag.Parse("Reff")]);
        Assert.Equal(0, only.Freed[PoolTag.Parse("FMfn")]);             // grew meanwhile: freed nothing
        Assert.Equal(300 * MB, only.Freed[PoolTag.Parse("RLin")]);      // gone from the list: fell to nothing
    }

    [Fact]
    public void TheFactsTheActionsOnAndTheRunningProgramsComeFromMemory()
    {
        ProcessRecord Proc(int pid, string image, string? path) => new() { Key = new ProcessKey(pid, 1), Image = image, ImagePath = path };
        var running = new[]
        {
            Proc(10, "ChatGPT.exe", @"C:\Program Files\WindowsApps\OpenAI.Codex_26.1016.6573.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe"),
            Proc(11, "ChatGPT.exe", @"\Device\HarddiskVolume3\Program Files\WindowsApps\OpenAI.Codex_26.1016.6573.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe"),
            Proc(12, "claude.exe", @"C:\Users\someone\.local\bin\claude.exe"),
            Proc(13, "notepad.exe", null),
        };
        var e = Gather(History(i => []), inputs: new RecommendationInputs { NowUtc = Now, Facts = Facts(), AutoClearOn = true }, running: running);
        Assert.Equal([CodexDesktop, ClaudeCode, new("notepad.exe", null)], e.Running);
        Assert.Equal("26200.9106", e.WindowsBuild);
        Assert.Equal(["NTFS", "ReFS"], e.FileSystems);
        Assert.Equal(["auto-clear"], e.ActionsOn);

        var none = Gather(History(i => []));
        Assert.Null(none.WindowsBuild);
        Assert.Empty(none.FileSystems);
        Assert.Empty(none.ActionsOn);
    }

    [Fact]
    public void TheActionsInEffectAreTheSettingsOnTheLeakStoppedAndTheShortcutMade()
    {
        var off = new RecommendationInputs { NowUtc = Now, Facts = Facts() };
        Assert.Empty(KnownActions.InEffect(off, DefaultLockTimeout));
        Assert.Equal(["auto-clear", "token-guard", "chatgpt-shortcut"],
            KnownActions.InEffect(off with { AutoClearOn = true, ChatGptShortcutMade = true }, liveForegroundLockTimeout: 0));
        // The guard's setting on is not the leak stopped: only the Windows setting itself, whoever set it, is.
        Assert.Empty(KnownActions.InEffect(off with { TokenGuardOn = true }, DefaultLockTimeout));
        Assert.Equal(["token-guard"], KnownActions.InEffect(off, liveForegroundLockTimeout: 0));
        Assert.Empty(KnownActions.InEffect(off with { TokenGuardOn = true }, liveForegroundLockTimeout: null));
    }

    [Theory]
    [InlineData(@"C:\Program Files\WindowsApps\Claude_1.5354.0.0_x64__pzs8sxrjxfjjc\app\claude.exe", "Claude")]
    [InlineData(@"c:\program files\windowsapps\OpenAI.ChatGPT-Desktop_1.2025.112.0_x64__2p2nqsd0c76g0\ChatGPT.exe", "OpenAI.ChatGPT-Desktop")]
    [InlineData(@"C:\Users\someone\AppData\Local\Microsoft\WindowsApps\ChatGPT.exe", null)]
    [InlineData(@"C:\Program Files\WindowsApps\NoVersionHere\app.exe", null)]
    [InlineData(@"C:\Program Files\Tools\app.exe", null)]
    [InlineData(null, null)]
    public void TheStorePackageComesFromTheProgramsFolder(string? path, string? package) => Assert.Equal(package, KnowledgeEvidence.StorePackageOf(path));
}
