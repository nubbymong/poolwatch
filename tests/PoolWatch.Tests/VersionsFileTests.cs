using PoolWatch.Core.Versions;

namespace PoolWatch.Tests;

/// <summary>
/// cache\versions.txt holds one "name=version" line per app. The versions come from places a standard user's programs can
/// name (a Claude Code folder in the profile, any exe called "AI Code Conductor.exe"): only a well-formed version of an
/// app PoolWatch tracks is written or read back, so a value cannot add a line or another app's entry.
/// </summary>
public class VersionsFileTests
{
    [Fact]
    public void AVersionWithALineBreakForgesNoOtherEntry()
    {
        var current = new Dictionary<string, string> { [VersionMarkers.Aicc] = "1.0.0\nClaude Code=9.9.9\nSome App=1.0", [VersionMarkers.ClaudeCode] = "2.1.4" };
        string text = VersionMarkers.Serialize(current);
        Assert.Equal("Claude Code=2.1.4\n", text); // the malformed one is not written at all
        var previous = VersionMarkers.Parse(text);
        Assert.False(previous.ContainsKey("Some App"));
        Assert.Equal("2.1.4", previous[VersionMarkers.ClaudeCode]);
        Assert.Empty(VersionMarkers.Diff(previous, new Dictionary<string, string> { [VersionMarkers.ClaudeCode] = "2.1.4" }, World.T0));
    }

    [Fact]
    public void VersionsTxtWritesOnlyTrackedAppsSoNoNameAddsALine()
    {
        var current = new Dictionary<string, string>
        {
            ["Some App\nClaude Code"] = "9.9.9", ["Codex=x"] = "1.0", ["Some App"] = "1.0", [VersionMarkers.ClaudeCode] = "2.1.4", [VersionMarkers.Aicc] = "2.1.0-rc.9",
        };
        Assert.Equal("AI Code Conductor=2.1.0-rc.9\nClaude Code=2.1.4\n", VersionMarkers.Serialize(current));
    }

    [Fact]
    public void VersionsTxtReadsOnlyTrackedAppsWithWellFormedVersions()
    {
        var read = VersionMarkers.Parse("Claude Code=2.1.4\nSome App=1.0\nChatGPT=1.2025.112.0 x\nCodex=26.1\nAI Code Conductor=2.1.0-rc.9\n");
        Assert.Equal(new Dictionary<string, string> { ["Claude Code"] = "2.1.4", ["Codex"] = "26.1", ["AI Code Conductor"] = "2.1.0-rc.9" }, read);
    }

    [Theory]
    [InlineData("2.1.0", "2.1.0")]
    [InlineData("2.1.0-rc.9+abc.def", "2.1.0-rc.9")]
    [InlineData(" 1.2025.112.0 ", "1.2025.112.0")]
    [InlineData("1.0.0\nX=1", null)]
    [InlineData("1.0=2", null)]
    [InlineData("1", null)]
    [InlineData("v1.0", null)]
    [InlineData("1.0 beta", null)]
    [InlineData("", null)]
    public void OnlyAWellFormedVersionIsKept(string raw, string? expected) => Assert.Equal(expected, VersionMarkers.Clean(raw));

    [Fact]
    public void AiccIsKnownByItsVersionResourceNotOnlyItsFileName()
    {
        Assert.Equal("2.1.0-rc.9", VersionMarkers.AiccVersion("AI Code Conductor", "2.1.0-rc.9+sha", "2.1.0.0"));
        Assert.Equal("2.1.0.0", VersionMarkers.AiccVersion("AI Code Conductor", "not a version", "2.1.0.0"));
        Assert.Null(VersionMarkers.AiccVersion("Something Else", "2.1.0", "2.1.0.0"));
        Assert.Null(VersionMarkers.AiccVersion(null, "2.1.0", "2.1.0.0"));
        Assert.Null(VersionMarkers.AiccVersion("AI Code Conductor", "1.0.0\nClaude Code=9.9.9", null));
    }

    [Fact]
    public void AClaudeCodeFolderNameThatIsNotAVersionIsNotTaken()
    {
        Assert.Equal("2.0.15", VersionMarkers.NewestClaudeCode(["2.0.15", "9.9.9\nCodex=1.0", "9.9.9=x", "9.9.9 x"]));
        Assert.Null(VersionMarkers.NewestClaudeCode(["9.9.9\nCodex=1.0"]));
    }
}
