using PoolWatch.Core.Setup;
using PoolWatch.Core.TokenGuard;

namespace PoolWatch.Tests;

/// <summary>What the setup guide knows about the PC: the token-leak rule, the Insider channel, the cache file, the plain lines.</summary>
public class EnvironmentFactsTests
{
    private static readonly DateTime T0 = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);
    private const long GB = 1L << 30;

    internal static EnvironmentFacts Sample() => new()
    {
        ScannedUtc = T0, WindowsName = "Windows 11 Pro", DisplayVersion = "25H2", Build = "26220.7344", InsiderChannel = "Dev",
        Architecture = "x64", RamBytes = 64 * GB - 300L * (1 << 20),
        Volumes = [new(@"C:\", "NTFS", false, null), new(@"D:\", "ReFS", true, true)],
        AiTools = [new("Claude Code", "2.1.4"), new("ChatGPT desktop", "1.2026.300.0"), new("VS Code", null)],
        TokenLeak = TokenLeakStatus.NeedsTest, OwnClearTasks = [@"\Clear standby list"], LogonTaskExists = true, InstalledInProgramFiles = true,
        OptionalServicesRunning = 3, OptionalServiceExamples = ["Xbox Live Auth Manager", "Fax"], DefenderRealTimeOn = true,
        DefenderPerformanceModeOn = false, ChatGptInstalled = true, ChatGptIconPath = @"C:\Program Files\WindowsApps\OpenAI.Codex_1.0.0.0_x64__abc\app\ChatGPT.exe",
    };

    private static GuardState Saved(GuardResult r, string build) => new(build, r, T0.AddDays(-1), "earlier");

    [Theory]
    [InlineData("19045.6456")]
    [InlineData("22631.4602")]
    [InlineData("25000.1")]
    public void OlderBuildsDoNotHaveTheLeak(string build)
    {
        Assert.Equal(TokenLeakStatus.NotAffected, TokenLeakRule.Judge(build, null, 200000));
        Assert.Equal(TokenLeakStatus.NotAffected, TokenLeakRule.Judge(build, Saved(GuardResult.Leak, build), 0)); // the build decides first
    }

    [Theory]
    [InlineData("26100.4061")]
    [InlineData("26200.8655")]
    [InlineData("26220.7344")]
    [InlineData("27000.1")]
    public void WindowsElevenTwentyFourHTwoAndLaterNeedTheCheckWithoutAVerdict(string build)
    {
        Assert.Equal(TokenLeakStatus.NeedsTest, TokenLeakRule.Judge(build, null, 200000));
        Assert.Equal(TokenLeakStatus.NeedsTest, TokenLeakRule.Judge(build, Saved(GuardResult.Leak, "26100.1000"), 200000)); // another build's verdict
        Assert.Equal(TokenLeakStatus.NeedsTest, TokenLeakRule.Judge(build, Saved(GuardResult.Unclear, build), 200000));     // no clear verdict here
    }

    [Fact]
    public void ASavedVerdictForThisBuildWins()
    {
        const string build = "26220.7344";
        foreach (uint? live in new uint?[] { 200000, 0, null })
        {
            Assert.Equal(TokenLeakStatus.Leaks, TokenLeakRule.Judge(build, Saved(GuardResult.Leak, build), live));
            Assert.Equal(TokenLeakStatus.Clean, TokenLeakRule.Judge(build, Saved(GuardResult.CleanOnce, build), live));
            Assert.Equal(TokenLeakStatus.Clean, TokenLeakRule.Judge(build, Saved(GuardResult.Fixed, build), live));
        }
    }

    [Fact]
    public void TheLiveTimeoutDecidesWhenThereIsNoVerdict()
    {
        Assert.Equal(TokenLeakStatus.WorkedAroundAlready, TokenLeakRule.Judge("26220.7344", null, 0));
        Assert.Equal(TokenLeakStatus.Unknown, TokenLeakRule.Judge("26220.7344", null, null));
        Assert.Equal(TokenLeakStatus.Unknown, TokenLeakRule.Judge("", null, 200000));
        Assert.Equal(TokenLeakStatus.Unknown, TokenLeakRule.Judge("not a build", null, 200000));
    }

    [Theory]
    [InlineData("Dev", "Dev")]
    [InlineData("Beta", "Beta")]
    [InlineData("ReleasePreview", "Release Preview")]
    [InlineData("CanaryChannel", "Canary")]
    [InlineData(" canarychannel ", "Canary")]
    [InlineData("rs_prerelease", "")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("<b>Dev</b>", null)]
    [InlineData("AVeryLongBranchNameThatIsNotAChannelAtAll", null)]
    public void InsiderChannelsInWords(string? branch, string? expected) => Assert.Equal(expected, InsiderChannels.FromBranchName(branch));

    [Fact]
    public void TheCacheFileRoundTripsWithoutTheIconPath()
    {
        var f = Sample();
        string json = f.ToJson();
        Assert.DoesNotContain("WindowsApps", json);  // no path except volume roots
        Assert.DoesNotContain("iconPath", json, StringComparison.OrdinalIgnoreCase);
        var back = EnvironmentFacts.Parse(json);
        Assert.NotNull(back);
        Assert.Null(back.ChatGptIconPath);
        Assert.Equal(json, back.ToJson());
        Assert.Equal(T0, back.ScannedUtc);
        Assert.Equal(TokenLeakStatus.NeedsTest, back.TokenLeak);
        Assert.Equal(f.Volumes, back.Volumes);
    }

    [Fact]
    public void TheCacheFileIsReadAsUntrusted()
    {
        Assert.Null(EnvironmentFacts.Parse(""));
        Assert.Null(EnvironmentFacts.Parse("null"));
        Assert.Null(EnvironmentFacts.Parse("[]"));
        Assert.Null(EnvironmentFacts.Parse("{ not json"));
        Assert.Null(EnvironmentFacts.Parse("""{ "windowsName": "Windows 11 Pro" }""")); // the required facts are missing
        Assert.Null(EnvironmentFacts.Parse(Sample().ToJson().Replace("\"x64\"", "\"sparc\"")));
        Assert.Null(EnvironmentFacts.Parse(Sample().ToJson().Replace("\"NeedsTest\"", "\"Maybe\"")));
        Assert.Null(EnvironmentFacts.Parse(Sample().ToJson() + new string(' ', EnvironmentFacts.MaxJsonChars)));

        var big = Sample() with
        {
            WindowsName = new string('W', 5000),
            Volumes = Enumerable.Range(0, 100).Select(n => new VolumeFact($@"{(char)('A' + n % 26)}:\", "ReFS", true, null)).ToList(),
            OptionalServiceExamples = Enumerable.Range(0, 20).Select(n => $"Service {n}").ToList(),
            OptionalServicesRunning = -4,
        };
        var bounded = EnvironmentFacts.Parse(big.ToJson());
        Assert.NotNull(bounded);
        Assert.Equal(200, bounded.WindowsName.Length);
        Assert.Equal(32, bounded.Volumes.Count);
        Assert.Equal(5, bounded.OptionalServiceExamples.Count);
        Assert.Equal(0, bounded.OptionalServicesRunning);

        // Null entries and entries without their name are dropped, not passed on.
        string holes = Sample().ToJson().Replace("\"aiTools\": [", "\"aiTools\": [ null, { \"name\": null, \"version\": \"1\" },");
        Assert.Equal(3, EnvironmentFacts.Parse(holes)!.AiTools.Count);
    }

    [Fact]
    public void OnlyDriveRootsAndFileSystemNamesAreReadBack()
    {
        // The roots go into the Defender advice's commands, which the user pastes into an administrator PowerShell.
        var edited = Sample() with
        {
            Volumes =
            [
                new(@"D:\", "ReFS", true, false), new(@"E:\ and more text", "ReFS", true, false), new(@"\\server\share\", "NTFS", false, null),
                new(@"F:\src", "ReFS", true, false), new("G:", "ReFS", true, false), new(@"1:\", "ReFS", true, false), new(@"H:\", "Re FS", true, false),
                new(@"I:\", "", false, null), new(@"J:\", "FAT32", false, null),
            ],
        };
        Assert.Equal([@"D:\", @"J:\"], EnvironmentFacts.Parse(edited.ToJson())!.Volumes.Select(v => v.Root));
        Assert.True(EnvironmentFacts.IsDriveRoot(@"d:\"));
        Assert.False(EnvironmentFacts.IsDriveRoot(null));
    }

    [Fact]
    public void YourPcInPlainLines()
    {
        Assert.Equal(
        [
            "Windows 11 Pro 25H2 (Insider, Dev channel), build 26220.7344",
            "64-bit (x64)",
            "64 GB of memory",
            "Dev Drive: D: (ReFS)",
            "AI tools: Claude Code, ChatGPT desktop, VS Code",
        ], Sample().PlainLines().Take(5));
        Assert.StartsWith("Why this matters:", Sample().PlainLines()[^1]);

        var plain = Sample() with
        {
            InsiderChannel = null, DisplayVersion = null, Volumes = [new(@"C:\", "NTFS", false, null)], AiTools = [], RamBytes = 0,
        };
        Assert.Equal(["Windows 11 Pro, build 26220.7344", "64-bit (x64)"], plain.PlainLines()); // nothing to explain without AI tools or a Dev Drive

        var two = Sample() with { Volumes = [new(@"D:\", "ReFS", true, true), new(@"E:\", "ReFS", true, false)] };
        Assert.Contains("Dev Drives: D: (ReFS) and E: (ReFS)", two.PlainLines());

        // Enrolled on a channel with no name in words: no branch code in the guide.
        Assert.Equal("Windows 11 Pro 25H2 (Insider), build 26220.7344", (Sample() with { InsiderChannel = "" }).PlainLines()[0]);
    }

    [Fact]
    public void AnArmPcIsToldThisBuildIsUntestedThere()
    {
        var arm = Sample() with { Architecture = "ARM64" };
        Assert.Equal("ARM64 processor. This PoolWatch is built for x64 and is not tested on ARM yet.", arm.PlainLines()[1]);
        Assert.DoesNotContain(Sample().PlainLines(), l => l.Contains("ARM"));
    }
}
