using FsCheck;
using FsCheck.Fluent;
using PoolWatch.Core.Changes;
using PoolWatch.Core.Knowledge;
using PoolWatch.Core.Links;
using PoolWatch.Core.Model;
using PoolWatch.Core.Recommendations;
using static PoolWatch.Tests.PropertyInputs;

namespace PoolWatch.Tests;

/// <summary>
/// The pure parts of the AppData links finder and its fixes: the PATH value read and written back one entry at a time,
/// the path checks, which apps count, the change records of a PATH entry and of a variable (and the ones refused), the
/// window's words, the AI agent's prompt (no user name or path from the PC), and the suggestion and known issue that point
/// at the window. The PATH value is text an account can write, so its reader has property tests too.
/// </summary>
public class AppDataLinksTests
{
    private static readonly DateTime When = new(2026, 11, 16, 23, 50, 0, DateTimeKind.Utc);
    private const string Profile = @"C:\Users\someone";

    internal static ChangeRecord PathRecord(string scope = EnvScopes.User, string before = @"%LOCALAPPDATA%\Programs\Example\bin",
        string after = @"C:\Users\someone\.example\current\bin", int index = 3) => new()
    {
        Id = ChangeIds.PathEntry(scope, before, When), Kind = RecordKind.PathEntry, ChangedUtc = When, Via = "the AppData links window",
        Location = scope, Before = before, After = after, Index = index,
    };

    internal static ChangeRecord VariableRecord(string name = LinkFinder.UvPythons, string? before = null, string? kind = null,
        string after = @"C:\Users\someone\.uv\python") => new()
    {
        Id = ChangeIds.EnvironmentVariable(name), Kind = RecordKind.EnvironmentVariable, ChangedUtc = When, Via = "the AppData links window",
        Location = EnvScopes.User, Name = name, Before = before, ValueKind = kind, After = after,
    };

    // ------------------------------------------------------------------ the PATH value

    [Fact]
    public void APathEntryIsReplacedAloneAndEveryOtherCharacterStays()
    {
        const string path = @"%SystemRoot%\system32; ""C:\Program Files\Tool"" ;;%LOCALAPPDATA%\Programs\Example\bin;C:\Last\";
        var entries = PathList.Split(path);
        Assert.Equal(5, entries.Length);
        Assert.Equal(path, PathList.Join(entries));
        Assert.Equal(@"%SystemRoot%\system32; ""C:\Program Files\Tool"" ;;D:\Example\1.2\bin;C:\Last\",
            PathList.Replace(path, 3, @"%LOCALAPPDATA%\Programs\Example\bin", @"D:\Example\1.2\bin"));
        // The entry must be there, exactly as shown: another entry, another index or a changed PATH change nothing.
        Assert.Null(PathList.Replace(path, 2, @"%LOCALAPPDATA%\Programs\Example\bin", @"D:\Example\1.2\bin"));
        Assert.Null(PathList.Replace(path, 3, @"%localappdata%\Programs\Example\bin", @"D:\Example\1.2\bin"));
        Assert.Null(PathList.Replace(path, 5, "", @"D:\x"));
        Assert.Null(PathList.Replace(path, -1, "", @"D:\x"));
        Assert.Null(PathList.Replace("a", 0, "a", new string('x', PathList.MaxChars + 1))); // longer than a variable can be
    }

    [Fact]
    public void PutBackFindsTheFolderPoolWatchWroteWhereItWroteItOrOnceElsewhere()
    {
        const string written = @"D:\Example\1.2\bin", original = @"%LOCALAPPDATA%\Programs\Example\bin";
        Assert.Equal($@"C:\A;{original};C:\B", PathList.PutBack($@"C:\A;{written};C:\B", 1, written, original));
        // A tool put its link in front since (codex update does): the entry is found once, one place on.
        Assert.Equal($@"{original};C:\A;{original};C:\B", PathList.PutBack($@"{original};C:\A;{written};C:\B", 1, written, original));
        // Gone, or there twice somewhere else: nothing PoolWatch can tell is its own.
        Assert.Null(PathList.PutBack(@"C:\A;C:\B", 1, written, original));
        Assert.Null(PathList.PutBack($@"{written};C:\A;{written}", 1, written, original));
        // There at its place and again elsewhere: the one at its place.
        Assert.Equal($@"{written};{original}", PathList.PutBack($@"{written};{written}", 1, written, original));
    }

    /// <summary>The value is the account's to write: any text at all splits and joins back exactly, and a change touches one entry only.</summary>
    [Fact]
    public void AnyPathValueComesBackExactlyAndAChangeTouchesOneEntryOnly()
    {
        var values = Gen.OneOf(AnyText, Gen.ArrayOf(Gen.Elements("C:\\a", "", " ", "%X%", "\"q;r\"", "D:\\b\\", "C:\\a")).Select(p => string.Join(";", p)));
        var cases = from value in values from index in Gen.Choose(-1, 8) from entry in Gen.Elements("D:\\x", "", "C:\\a") select (value, index, entry);
        ForAll(500, cases, c =>
        {
            var parts = PathList.Split(c.value);
            Assert.Equal(c.value, PathList.Join(parts));
            string expected = c.index >= 0 && c.index < parts.Length ? parts[c.index] : "";
            var next = PathList.Replace(c.value, c.index, expected, c.entry);
            if (c.index < 0 || c.index >= parts.Length) { Assert.Null(next); return; }
            if (c.value.Length - expected.Length + c.entry.Length > PathList.MaxChars) { Assert.Null(next); return; }
            Assert.NotNull(next);
            var after = PathList.Split(next!);
            Assert.Equal(parts.Length, after.Length);
            for (int i = 0; i < parts.Length; i++) Assert.Equal(i == c.index ? c.entry : parts[i], after[i]);
            // Put back where it was written gives the value as it was, every character of it.
            Assert.Equal(c.value, PathList.PutBack(next!, c.index, c.entry, expected));
            // Another entry than the one shown at that place changes nothing.
            if (expected != "D:\\other") Assert.Null(PathList.Replace(c.value, c.index, "D:\\other", c.entry));
        });
    }

    // ------------------------------------------------------------------ a link's reparse data

    [Fact]
    public void AJunctionsAndASymbolicLinksTargetAreReadFromTheirReparseData()
    {
        Assert.Equal((@"\??\C:\Users\someone\.codex\current\bin", false),
            ReparseData.Target(ReparseData.Make(ReparseData.MountPoint, @"\??\C:\Users\someone\.codex\current\bin", @"C:\Users\someone\.codex\current\bin")));
        Assert.Equal((@"..\cpython-3.12.13-windows-x86_64-none", true),
            ReparseData.Target(ReparseData.Make(ReparseData.SymbolicLink, @"..\cpython-3.12.13-windows-x86_64-none", @"..\cpython-3.12.13-windows-x86_64-none", relative: true)));
        Assert.Equal((@"\??\D:\x", false), ReparseData.Target(ReparseData.Make(ReparseData.SymbolicLink, @"\??\D:\x", @"D:\x")));
        // Another kind of reparse point (an app execution alias, OneDrive's cloud files) holds no target PoolWatch reads.
        Assert.Null(ReparseData.Target(ReparseData.Make(0x8000001B, @"\??\C:\x", "x")));
        Assert.Null(ReparseData.Target([]));
        Assert.Null(ReparseData.Target(new byte[ReparseData.MaxBytes + 1]));
    }

    /// <summary>A link's data is on a disk the account can write: any bytes are read without throwing, and a name is read only from inside them.</summary>
    [Fact]
    public void AnyReparseDataIsReadWithinItsBoundsOrNotAtAll()
    {
        byte[][] good =
        [
            ReparseData.Make(ReparseData.MountPoint, @"\??\C:\Users\someone\.codex\current\bin", @"C:\Users\someone\.codex\current\bin"),
            ReparseData.Make(ReparseData.SymbolicLink, @"..\x", @"..\x", relative: true),
        ];
        var data = Gen.OneOf(AnyBytes, Gen.Elements(good).SelectMany(Damaged));
        ForAll(500, data, bytes =>
        {
            if (ReparseData.Target(bytes) is not { } read) return;
            Assert.InRange(read.Target.Length, 1, bytes.Length / 2);
            Assert.DoesNotContain(read.Target, char.IsControl);
            uint tag = BitConverter.ToUInt32(bytes, 0);
            Assert.True(tag is ReparseData.MountPoint or ReparseData.SymbolicLink);
            Assert.True(!read.Relative || tag == ReparseData.SymbolicLink);
        });
    }

    // ------------------------------------------------------------------ the paths

    [Fact]
    public void APathIsUnderAnAppDataFolderOnlyBelowItAndItsLevelsGoFromTheTopDown()
    {
        const string local = @"C:\Users\someone\AppData\Local";
        Assert.True(LinkPaths.Under(@"C:\Users\someone\AppData\Local\Programs\x", local));
        Assert.True(LinkPaths.Under(@"c:\users\SOMEONE\appdata\local\x\", local + "\\"));
        Assert.False(LinkPaths.Under(local, local));
        Assert.False(LinkPaths.Under(@"C:\Users\someone\AppData\LocalLow\x", local)); // not a folder below it, though it starts the same
        Assert.False(LinkPaths.Under(@"D:\x", ""));
        Assert.Equal([local + @"\Programs", local + @"\Programs\OpenAI", local + @"\Programs\OpenAI\Codex", local + @"\Programs\OpenAI\Codex\bin"],
            LinkPaths.Levels(local + @"\Programs\OpenAI\Codex\bin\", local));
        Assert.Empty(LinkPaths.Levels(@"D:\x", local));
        Assert.Empty(LinkPaths.Levels(local + @"\a\..\b", local));
        Assert.Empty(LinkPaths.Levels(local + @"\a\\b", local));
    }

    [Theory]
    [InlineData(@"C:\Users\someone\.uv\python", true)]
    [InlineData(@"D:\", true)]
    [InlineData(@"C:\a;b", false)]           // one entry would become two
    [InlineData(@"C:\%USERNAME%\x", false)]  // read as a variable
    [InlineData(@"\\server\share\bin", false)]
    [InlineData(@"C:\a""b", false)]
    [InlineData(@"C:\a:b", false)]
    [InlineData("bin", false)]
    [InlineData(null, false)]
    public void PoolWatchWritesOnlyAPlainLocalFolder(string? path, bool writable) => Assert.Equal(writable, LinkPaths.Writable(path));

    [Fact]
    public void UnquoteTakesOffOnePairOfQuotesAndTheSpaces()
    {
        Assert.Equal(@"C:\Program Files\x", LinkPaths.Unquote(@"  ""C:\Program Files\x""  "));
        Assert.Equal(@"C:\x", LinkPaths.Unquote(@"C:\x "));
        Assert.Equal(@"""C:\x", LinkPaths.Unquote(@"""C:\x"));
    }

    [Fact]
    public void ThePromptsFoldersNameNoUser()
    {
        Assert.Equal(@"%USERPROFILE%\.uv\python", LinkPaths.InProfileWords(@"C:\Users\someone\.uv\python", Profile));
        Assert.Equal("%USERPROFILE%", LinkPaths.InProfileWords(@"c:\users\someone", Profile + "\\"));
        Assert.Equal(@"D:\x", LinkPaths.InProfileWords(@"D:\x", Profile));
        Assert.Equal(@"C:\Users\someone2\x", LinkPaths.InProfileWords(@"C:\Users\someone2\x", Profile));
    }

    [Theory]
    [InlineData("Claude_5.11492.5.0_x64__pzs8sxrjxfjjc", true, false)]
    [InlineData("AnthropicPBC.Claude_1.0.0.0_x64__abc", true, false)]
    [InlineData("OpenAI.Codex_1.2.0.0_x64__2p2nqsd0c76g0", false, true)]
    [InlineData("OpenAI.ChatGPT-Desktop_1.0.0.0_x64__2p2nqsd0c76g0", false, false)]
    [InlineData("ClaudeHelper_1.0.0.0_x64__x", false, false)]
    [InlineData("Microsoft.WindowsTerminal_1.0.0.0_x64__8wekyb3d8bbwe", false, false)]
    public void OnlyTheClaudeAndCodexPackagesCount(string fullName, bool claude, bool codex)
    {
        Assert.Equal(claude, LinkApps.IsClaude(fullName));
        Assert.Equal(codex, LinkApps.IsCodex(fullName));
    }

    [Fact]
    public void EachVariableHasItsOwnFolderInTheProfile()
    {
        Assert.Equal(@"C:\Users\someone\.uv\python", LinkFinder.FolderFor(LinkFinder.UvPythons, Profile));
        Assert.Equal(@"C:\Users\someone\.uv\cache", LinkFinder.FolderFor(LinkFinder.UvCache, Profile + "\\"));
        Assert.Equal(@"C:\Users\someone\.pnpm", LinkFinder.FolderFor(LinkFinder.PnpmHome, Profile));
        Assert.Equal(@"C:\Users\someone\.codex\bin", LinkFinder.FolderFor(LinkFinder.CodexInstall, Profile));
        Assert.Throws<ArgumentException>(() => LinkFinder.FolderFor("PATH", Profile));
        Assert.All(LinkFinder.Variables, v => Assert.True(LinkPaths.Writable(LinkFinder.FolderFor(v, Profile))));
    }

    // ------------------------------------------------------------------ the change records

    [Fact]
    public void APathEntryAndAVariableAreRecordedAndReadBack()
    {
        ChangeRecord[] records = [PathRecord(), PathRecord(EnvScopes.Machine), VariableRecord(), VariableRecord(LinkFinder.PnpmHome, @"%LOCALAPPDATA%\pnpm", "ExpandString")];
        Assert.All(records, r => Assert.Null(ChangeRecordFile.Invalid(r)));
        Assert.Equal(records, ChangeRecordFile.Parse(ChangeRecordFile.ToJson(records)));
        // Each replacement of an entry is a record of its own, even of the same entry: a tool can put it back in between.
        Assert.NotEqual(ChangeIds.PathEntry(EnvScopes.User, @"%LOCALAPPDATA%\Programs\Example\bin", When),
            ChangeIds.PathEntry(EnvScopes.User, @"%LOCALAPPDATA%\Programs\Example\bin", When.AddMinutes(1)));
    }

    public static TheoryData<string, ChangeRecord> Refused => new()
    {
        { "a scope PoolWatch does not change", PathRecord() with { Location = "HKCU" } },
        { "an entry that would be two", PathRecord(before: @"C:\a;C:\b") },
        { "no entry", PathRecord() with { Before = null } },
        { "a folder with a ;", PathRecord(after: @"C:\a;b") },
        { "a folder with a %", PathRecord(after: @"%USERPROFILE%\x") },
        { "a share", PathRecord(after: @"\\server\x") },
        { "no place", PathRecord() with { Index = null } },
        { "a place past the most entries", PathRecord(index: PathList.MaxEntries) },
        { "an id of another time", PathRecord() with { ChangedUtc = When.AddTicks(1) } },
        { "another variable", VariableRecord("ComSpec") },
        { "the PATH as a variable", VariableRecord("Path") },
        { "a variable of the machine", VariableRecord() with { Location = EnvScopes.Machine } },
        { "a value before with no kind", VariableRecord(before: @"C:\old") },
        { "a kind with no value before", VariableRecord(kind: "String") },
        { "another kind of value", VariableRecord(before: @"C:\old", kind: "Binary") },
        { "a variable set to a share", VariableRecord(after: @"\\server\x") },
        { "an id of another variable", VariableRecord() with { Id = ChangeIds.EnvironmentVariable(LinkFinder.UvCache) } },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void ARecordPoolWatchWouldNotWriteIsNeverActedOn(string what, ChangeRecord record) => Assert.True(ChangeRecordFile.Invalid(record) is not null, what);

    [Fact]
    public void ActiveChangesSaysWhatChangedAndHowItWas()
    {
        Assert.Equal(@"Replaced %LOCALAPPDATA%\Programs\Example\bin on your PATH with the folder its link leads to, C:\Users\someone\.example\current\bin",
            ChangeWords.Of(PathRecord()));
        Assert.Equal(@"PATH entry %LOCALAPPDATA%\Programs\Example\bin", ChangeWords.TitleOf(PathRecord()));
        Assert.Equal(@"entry on the machine's PATH · now C:\Users\someone\.example\current\bin", ChangeWords.DetailOf(PathRecord(EnvScopes.Machine)));
        Assert.Equal(@"Set UV_PYTHON_INSTALL_DIR to C:\Users\someone\.uv\python", ChangeWords.Of(VariableRecord()));
        Assert.Equal("UV_PYTHON_INSTALL_DIR", ChangeWords.TitleOf(VariableRecord()));
        Assert.Equal(@"environment variable · was not set · now C:\Users\someone\.uv\python", ChangeWords.DetailOf(VariableRecord()));
        Assert.Equal(@"environment variable · was %LOCALAPPDATA%\pnpm", ChangeWords.DetailOf(VariableRecord(LinkFinder.PnpmHome, @"%LOCALAPPDATA%\pnpm", "ExpandString")));
    }

    // ------------------------------------------------------------------ the window's words and the prompt

    private static LinkFinding Uv() => new()
    {
        Kind = LinkKind.Uv, Link = @"%APPDATA%\uv\python", Count = 2, Names = ["cpython-3.12-windows-x86_64-none", "cpython-3.13-windows-x86_64-none"],
        EnvSettings = [new(LinkFinder.UvPythons, @"C:\Users\someone\.uv\python", null), new(LinkFinder.UvCache, @"C:\Users\someone\.uv\cache", @"%LOCALAPPDATA%\uv\cache")],
    };

    private static LinkFinding Entry(string? target = @"C:\Users\someone\.codex\packages\standalone\current\bin") => new()
    {
        Kind = LinkKind.PathEntry, Scope = EnvScope.User, Index = 4, Entry = @"%LOCALAPPDATA%\Programs\OpenAI\Codex\bin",
        Link = @"%LOCALAPPDATA%\Programs\OpenAI\Codex\bin", Target = target, WhyNot = target is null ? "the folder it leads to is reached through another link under AppData." : null,
    };

    [Fact]
    public void EachRowSaysWhatItCostsWhatFixesItAndWhoMeasuredIt()
    {
        var rows = new[]
        {
            Entry(), Entry(target: null), Entry() with { Kind = LinkKind.PathEntryGone, Target = null }, Uv(), Uv() with { Moved = true, EnvSettings = [] },
            new LinkFinding { Kind = LinkKind.WinGetLinks, Entry = @"%LOCALAPPDATA%\Microsoft\WinGet\Links", Count = 3 },
            new LinkFinding { Kind = LinkKind.Pnpm, Link = @"%LOCALAPPDATA%\pnpm", EnvSettings = [new(LinkFinder.PnpmHome, @"C:\Users\someone\.pnpm", null)] },
            new LinkFinding { Kind = LinkKind.Codex, Link = @"%LOCALAPPDATA%\Programs\OpenAI\Codex\bin", EnvSettings = [new(LinkFinder.CodexInstall, @"C:\Users\someone\.codex\bin", null)] },
            new LinkFinding { Kind = LinkKind.Projects },
        };
        foreach (var f in rows)
        {
            string details = LinkWords.Details(f);
            foreach (string part in new[] { "What it is: ", "What it costs: ", "What fixes it: ", "Read more: " + LinkFinder.IssueUrl }) Assert.Contains(part, details);
            Assert.Matches(@"\((peterlau03|thyhellcat|DraftingDreamer|0uternet|bronze1man|fsfxing2023-wq|morenogironadr)", details); // who measured it
            Assert.DoesNotContain("!", details);
            Assert.False(string.IsNullOrWhiteSpace(LinkWords.What(f) + LinkWords.Where(f) + LinkWords.Fix(f)));
        }
        Assert.Equal(["Replace on PATH", "Fix", "Fix", "Set the variables...", "Fix", "Fix", "Set the variables...", "Set the variables...", "Fix"], rows.Select(LinkWords.Button));
        Assert.Equal([true, false, false, true, false, false, true, true, false], rows.Select(f => f.CanFix));
        Assert.Contains(@"C:\Users\someone\.codex\packages\standalone\current\bin", LinkWords.Details(Entry()));
        Assert.Contains("PoolWatch does not replace this entry: the folder it leads to is reached through another link under AppData.", LinkWords.Details(Entry(null)));
        Assert.Contains("cpython-3.12-windows-x86_64-none, cpython-3.13-windows-x86_64-none", LinkWords.Details(Uv()));
        Assert.Contains("UV_PYTHON_INSTALL_DIR and UV_CACHE_DIR", LinkWords.Details(Uv()));
        Assert.Equal("your PATH", LinkWords.Where(Entry()));
        Assert.Equal("the machine's PATH", LinkWords.Where(Entry() with { Scope = EnvScope.Machine }));
    }

    [Fact]
    public void TheQuestionsSayExactlyWhatChanges()
    {
        string replace = LinkWords.ReplaceQuestion(Entry());
        Assert.Contains(@"%LOCALAPPDATA%\Programs\OpenAI\Codex\bin", replace);
        Assert.Contains(@"C:\Users\someone\.codex\packages\standalone\current\bin", replace);
        Assert.Contains("Active changes can put it back.", replace);
        string set = LinkWords.SetQuestion(Uv());
        Assert.Contains(@"UV_PYTHON_INSTALL_DIR = C:\Users\someone\.uv\python", set);
        Assert.Contains(@"UV_CACHE_DIR = C:\Users\someone\.uv\cache  (now %LOCALAPPDATA%\uv\cache)", set);
        Assert.Contains("does not reinstall, move or delete anything", set);
    }

    [Fact]
    public void TheStepsAndThePromptAreTheUsersToFollowAndHoldNoUserName()
    {
        foreach (var f in new[] { Uv(), new LinkFinding { Kind = LinkKind.Pnpm, EnvSettings = [new(LinkFinder.PnpmHome, @"C:\Users\someone\.pnpm", null)] },
                     new LinkFinding { Kind = LinkKind.Codex, EnvSettings = [new(LinkFinder.CodexInstall, @"C:\Users\someone\.codex\bin", null)] } })
        {
            string steps = LinkWords.StepsText(f, f.EnvSettings);
            Assert.StartsWith("PoolWatch set these for your account:", steps);
            Assert.Contains("PoolWatch never reinstalls, moves or deletes your tools or files", steps);
            Assert.Contains("1. Quit the Claude and Codex apps fully", steps);
            string prompt = LinkWords.Prompt(f, f.EnvSettings, Profile);
            Assert.DoesNotContain("someone", prompt);
            Assert.DoesNotContain(@"C:\Users", prompt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(@"= %USERPROFILE%\.", prompt);
            Assert.Contains(LinkFinder.IssueUrl, prompt);
            Assert.Contains("Ask me before you change, reinstall or delete anything", prompt);
            foreach (var s in f.EnvSettings) Assert.Contains(s.Name, prompt);
        }
    }

    // ------------------------------------------------------------------ the suggestion and the known issue

    private static RecommendationInputs Quiet() => new() { NowUtc = When, NowMs = Clocks.At(When).Ms, AutoClearOn = true };

    [Fact]
    public void TheSuggestionShowsOnlyWhileALookFoundSomethingToFixAndOpensTheWindow()
    {
        Assert.DoesNotContain(RecommendationRules.Current(Quiet()), r => r.Id == RecommendationId.FixAppDataLinks);
        var offer = Assert.Single(RecommendationRules.Current(Quiet() with { AppDataLinksToFix = 2 }));
        Assert.Equal(RecommendationId.FixAppDataLinks, offer.Id);
        Assert.Equal("Review", offer.Button);
        Assert.StartsWith("PoolWatch found 2 links under AppData", offer.Why);
        Assert.StartsWith("PoolWatch found a link under AppData", Assert.Single(RecommendationRules.Current(Quiet() with { AppDataLinksToFix = 1 })).Why);
        // Hidden for 30 days, like every suggestion.
        var hidden = new Dictionary<string, Moment> { [Recommendation.KeyOf(RecommendationId.FixAppDataLinks, null)] = Clocks.At(When.AddDays(-1)) };
        Assert.Empty(RecommendationRules.Current(Quiet() with { AppDataLinksToFix = 2, Dismissed = hidden }));
    }

    [Fact]
    public void TheKnownIssueNamesTheWindowAndWhatPoolWatchAlreadyChanged()
    {
        var issue = KnowledgeBase.Embedded().Entries.Single(e => e.Id == LinkFinder.KnownIssueId);
        Assert.Equal(["appdata-links"], issue.Actions);
        Assert.Equal(RecommendationId.FixAppDataLinks, KnownActions.Find("appdata-links")!.Suggestion);
        Assert.False(KnownActions.Find("appdata-links")!.Stops); // a link fixed does not stop the issue: another can come back
        Assert.Empty(KnownActions.InEffect(Quiet() with { AutoClearOn = false }, liveForegroundLockTimeout: 200_000));
        Assert.Equal(["appdata-links"], KnownActions.InEffect(Quiet() with { AutoClearOn = false, AppDataLinksFixed = true }, liveForegroundLockTimeout: 200_000));
        Assert.Contains(issue.Credits, c => c.Name == "peterlau03" && c.Url == LinkFinder.IssueUrl);
        Assert.All(issue.Credits, c => Assert.StartsWith(LinkFinder.IssueUrl, c.Url));
    }
}
