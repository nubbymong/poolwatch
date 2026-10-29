using System.Text.RegularExpressions;
using PoolWatch.Core;
using PoolWatch.Core.Report;
using PoolWatch.Core.Updates;

namespace PoolWatch.Tests;

/// <summary>About's credits and the fixed list of web pages PoolWatch may open.</summary>
public class CreditsTests
{
    [Theory]
    [InlineData("TraceEvent", true)]
    [InlineData("KernelTraceControl", true)]
    [InlineData("NETCore.Client", true)]
    [InlineData("Microsoft.Extensions", true)]
    [InlineData("System.Management", true)]
    [InlineData("Windows Desktop runtime", true)]
    [InlineData("Inno Setup", true)]
    [InlineData("gunrgbcr/windows-token-leak-logon-fix", false)]
    [InlineData("bentoner/windows-token-leak", false)]
    [InlineData("Faarfarin", false)]
    [InlineData("RAMMap", false)]
    [InlineData("pooltag.txt", false)]
    [InlineData("xUnit", false)]
    public void EveryCreditIsListedAndSaysWhetherItShips(string name, bool shipped)
    {
        var credit = Assert.Single(Credits.All, c => c.Name.Contains(name, StringComparison.Ordinal));
        Assert.Equal(shipped, credit.Shipped);
    }

    [Fact]
    public void TheShaderCacheWorkaroundIsCreditedToItsComment() =>
        Assert.Equal("https://github.com/openai/codex/issues/31212#issuecomment-5635622083", Credits.All.Single(c => c.Name.StartsWith("Faarfarin", StringComparison.Ordinal)).Url);

    [Fact]
    public void EveryCreditHasAnHttpsPageAndPlainShortWords()
    {
        foreach (var c in Credits.All)
        {
            Assert.True(Uri.TryCreate(c.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps, c.Url);
            Assert.True(c.Url.IndexOfAny([' ', '"', '\'']) < 0, c.Url);
            Assert.All(new[] { c.Name, c.What, c.Licence }, t => Assert.False(string.IsNullOrWhiteSpace(t)));
            Assert.EndsWith(".", c.What);
            // About shows each credit as one line, two at most.
            Assert.True($"{c.Name} ({c.Licence}). {c.What}".Length <= 160, c.Name);
            Assert.DoesNotMatch(new Regex(@"\b[A-Z][a-z]+(?:[A-Z][a-z]+)+(Enabled|Hours|Gb|Minutes)\b"), c.What);
            Assert.DoesNotContain("!", c.What);
        }
    }

    [Fact]
    public void TheCreditsPagesTheRepositoryAndItsAdvisoryPageMayBeOpened()
    {
        Assert.All(Credits.All, c => Assert.True(Credits.IsAllowedLink(c.Url), c.Url));
        Assert.True(Credits.IsAllowedLink(Credits.RepoUrl));
        Assert.True(Credits.IsAllowedLink(Credits.NoticesUrl));
        Assert.True(Credits.IsAllowedLink(IssueReport.AdvisoryUrl));
    }

    [Theory]
    [InlineData("It \"crashed\" & then \"C:\\Windows\\System32\\calc.exe\"")]
    [InlineData("a\r\nb\tc %41 #x ?y=z")]
    public void AnIssueAddressPoolWatchBuiltMayBeOpenedWhateverTheUserTyped(string typed)
    {
        string url = IssueReport.Url(typed, "1.0.0", typed);
        Assert.True(Credits.IsAllowedLink(url), url);
        string many = string.Concat(Enumerable.Repeat(typed + " ", 50));
        Assert.True(Credits.IsAllowedLink(IssueReport.Url(many, many, many)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://evil.example/")]
    [InlineData("http://github.com/nubbymong/poolwatch")]
    [InlineData("https://github.com/nubbymong/poolwatchx")]
    [InlineData("https://github.com/nubbymong/poolwatch/")]
    [InlineData("https://github.com/nubbymong/poolwatch/issues/new")]
    [InlineData("https://github.com/nubbymong/poolwatch/issues/new?title=\"x\" calc.exe")]
    [InlineData("https://github.com/nubbymong/poolwatch/issues/new?title=a b")]
    [InlineData("https://github.com/nubbymong/poolwatch/issues/new?title=x#frag")]
    [InlineData("https://github.com/nubbymong/poolwatch/issues/newer?title=x")]
    [InlineData("https://github.com/nubbymong/poolwatch/issues/new?title=x\\..\\..")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("HTTPS://GITHUB.COM/NUBBYMONG/POOLWATCH")]
    public void NothingElseMayBeOpened(string url) => Assert.False(Credits.IsAllowedLink(url));

    [Fact]
    public void AnIssueAddressOverTheLimitMayNotBeOpened() =>
        Assert.False(Credits.IsAllowedLink(IssueReport.NewIssueUrl + "?title=" + new string('a', IssueReport.MaxUrlLength)));

    [Fact]
    public void AnUpdateNoticeMayOpenItsReleasesOwnPageAndNothingElseThere()
    {
        Assert.True(Credits.IsAllowedLink(ReleasePage.For("v1.2.0")));
        Assert.False(Credits.IsAllowedLink(ReleasePage.Prefix + "tag/v1\" x"));
        Assert.False(Credits.IsAllowedLink(ReleasePage.Prefix + "download/v1.2.0/PoolWatch-v1.2.0-setup-win-x64.exe"));
        Assert.False(Credits.IsAllowedLink(ReleasePage.Prefix + "latest"));
    }
}
