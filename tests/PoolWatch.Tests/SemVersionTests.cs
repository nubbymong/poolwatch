using PoolWatch.Core.Updates;

namespace PoolWatch.Tests;

/// <summary>The version order and the release-page rule behind update notifications.</summary>
public class SemVersionTests
{
    private static SemVersion V(string text) => SemVersion.TryParse(text, out var v) ? v : throw new ArgumentException(text);

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("V0.0.0", "0.0.0")]
    [InlineData("1.0.0+78b1fecf5b43864bbf25ed282b5b5322c37ad1a0", "1.0.0")] // the running assembly's informational version
    [InlineData("1.2.0-rc.1", "1.2.0-rc.1")]
    [InlineData("1.2.0-0.3.7+build.5", "1.2.0-0.3.7")]
    [InlineData("1.2.0-x-y-z.--", "1.2.0-x-y-z.--")]
    public void Parses(string text, string expected) => Assert.Equal(expected, V(text).ToString());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("01.2.3")]       // leading zeros
    [InlineData("1.2.3-01")]     // a numeric pre-release identifier with a leading zero
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-rc..1")]
    [InlineData("1.2.3+")]
    [InlineData("vv1.2.3")]
    [InlineData(" 1.2.3")]
    [InlineData("1.2.3\n")]      // \z, not $
    [InlineData("1.2.3 ")]
    [InlineData("latest")]
    [InlineData("１.2.3")]   // a full-width digit: \d would have matched it
    [InlineData("2147483648.0.0")] // over int.MaxValue
    public void RefusesWhatIsNotAVersion(string? text) => Assert.False(SemVersion.TryParse(text, out _));

    [Fact]
    public void RefusesOverlongText() =>
        Assert.False(SemVersion.TryParse("1.0.0-" + new string('a', SemVersion.MaxLength), out _));

    [Fact]
    public void OrdersBySemVerPrecedence()
    {
        // semver.org's own example, then numbers compared as numbers, not as text.
        string[] ascending =
        [
            "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1",
            "1.0.0", "1.0.1", "1.9.0", "1.10.0", "1.11.0", "2.0.0-rc.1", "2.0.0", "10.0.0",
        ];
        for (int i = 1; i < ascending.Length; i++)
        {
            Assert.True(V(ascending[i]).CompareTo(V(ascending[i - 1])) > 0, $"{ascending[i]} after {ascending[i - 1]}");
            Assert.True(V(ascending[i - 1]).CompareTo(V(ascending[i])) < 0, $"{ascending[i - 1]} before {ascending[i]}");
        }
        Assert.True(V("1.0.0-rc.99999999999999999999").CompareTo(V("1.0.0-rc.9")) > 0); // a numeric identifier longer than a long
    }

    [Fact]
    public void BuildMetadataAndTheTagPrefixDoNotChangeTheOrder()
    {
        Assert.Equal(0, V("1.0.0+abc").CompareTo(V("v1.0.0")));
        Assert.Equal(V("1.0.0+abc"), V("v1.0.0"));
        Assert.True(V("1.0.0").CompareTo(null) > 0);
    }

    [Fact]
    public void KnowsAPreRelease()
    {
        Assert.True(V("1.2.0-rc.1").IsPreRelease);
        Assert.False(V("1.2.0+build").IsPreRelease);
    }

    [Theory]
    [InlineData("https://github.com/nubbymong/poolwatch/releases/tag/v1.2.0", true)]
    [InlineData("https://github.com/nubbymong/poolwatch/releases/tag/v1.2.0-rc_1", true)]
    [InlineData("https://github.com/nubbymong/poolwatch/releases/", false)]           // nothing after the prefix
    [InlineData("https://github.com/nubbymong/poolwatch/releases/tag/", false)]       // no tag
    [InlineData("https://github.com/nubbymong/poolwatch/releases/tag/v1.2.0/x", false)] // more than the tag
    [InlineData("https://github.com/nubbymong/poolwatch/releases/download/v1.2.0/PoolWatch-v1.2.0-win-x64.zip", false)] // a download
    [InlineData("https://github.com/nubbymong/poolwatch/releases/latest/download/PoolWatch-v1.2.0-win-x64.zip", false)]
    [InlineData("https://github.com/nubbymong/poolwatch/releases/new", false)]
    [InlineData("https://github.com/nubbymong/poolwatch/releases/latest", false)]      // another page of the releases
    [InlineData("https://github.com/nubbymong/poolwatch/releases/tag/..", false)]      // the browser makes it the list
    [InlineData("http://github.com/nubbymong/poolwatch/releases/tag/v1.2.0", false)]
    [InlineData("https://GitHub.com/nubbymong/poolwatch/releases/tag/v1.2.0", false)]
    [InlineData("https://github.com/nubbymong/poolwatch-fork/releases/tag/v1.2.0", false)]
    [InlineData("https://github.com/someone/poolwatch/releases/tag/v1.2.0", false)]
    [InlineData("https://github.com.example.net/nubbymong/poolwatch/releases/tag/v1.2.0", false)]
    [InlineData("https://github.com/nubbymong/poolwatch/releases/../../other/releases", false)]
    [InlineData("https://github.com/nubbymong/poolwatch/releases/tag/%2e%2e", false)]
    [InlineData("https://github.com/nubbymong/poolwatch/releases/tag/v1?x=1", false)]
    [InlineData("https://github.com/nubbymong/poolwatch/releases/tag/v1#x", false)]
    [InlineData("https://github.com/nubbymong/poolwatch/releases/tag/v1\" x", false)]   // a quote and a space
    [InlineData("https://github.com/nubbymong/poolwatch/releases/tag/v1\\x", false)]
    [InlineData(" https://github.com/nubbymong/poolwatch/releases/tag/v1.2.0", false)]
    [InlineData(null, false)]
    public void OpensOnlyPoolWatchReleasePages(string? url, bool valid) => Assert.Equal(valid, ReleasePage.IsValid(url));

    [Fact]
    public void NamesAReleasesPageByItsTag()
    {
        Assert.Equal("https://github.com/nubbymong/poolwatch/releases/tag/v1.2.0", ReleasePage.For("v1.2.0"));
        Assert.True(ReleasePage.IsValid(ReleasePage.For("v1.2.0")));
    }

    [Fact]
    public void RefusesAnOverlongPage() =>
        Assert.False(ReleasePage.IsValid(ReleasePage.Prefix + "tag/" + new string('a', ReleasePage.MaxLength)));
}
