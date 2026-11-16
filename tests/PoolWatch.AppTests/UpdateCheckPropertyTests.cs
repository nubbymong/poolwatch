using System.Net;
using System.Text;
using System.Text.Json;
using FsCheck;
using FsCheck.Fluent;
using PoolWatch.Core.Updates;
using PoolWatch.Engine;
using static PoolWatch.Tests.PropertyInputs;

namespace PoolWatch.AppTests;

/// <summary>
/// Property tests (FsCheck) of the update check, which reads an answer from the network while PoolWatch runs as
/// administrator. GitHub is a fake handler here, so no test reaches the network. Whatever the answer holds, a check ends
/// with an outcome, and an update is offered only for a stable release newer than the one running, with that release's
/// own page.
/// </summary>
public sealed class UpdateCheckPropertyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pw-update-props-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly Log _log;
    private static readonly DateTime Now = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);
    private static readonly SemVersion Running = new(1, 2, 3);

    public UpdateCheckPropertyTests()
    {
        _paths = new AppPaths(Path.Combine(_root, "data"), null, secure: false);
        _log = new Log(Path.Combine(_root, "data", "test.log"));
    }

    public void Dispose()
    {
        _log.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>GitHub giving the same answer each time it is asked.</summary>
    private sealed class OneAnswer(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }

    /// <summary>One check with a new UpdateCheck (nothing known from before), on a thread-pool thread, as its timer runs it.</summary>
    private (UpdateResult Result, UpdateRelease? Available) Ask(byte[] body)
    {
        using var check = new UpdateCheck(_paths, _log, () => true, () => new OneAnswer(body), Running, () => AppClocks.At(Now), TimeSpan.FromSeconds(30));
        var result = Task.Run(() => check.CheckNow(CancellationToken.None)).GetAwaiter().GetResult();
        return (result, check.Available);
    }

    private static string Release(string tag, string page, bool prerelease, bool draft) =>
        JsonSerializer.Serialize(new { tag_name = tag, html_url = page, prerelease, draft, name = "PoolWatch " + tag, body = "What changed.", assets = Array.Empty<object>() });

    private static Gen<SemVersion> Versions =>
        from major in Gen.Choose(0, 3)
        from minor in Gen.Choose(0, 3)
        from patch in Gen.Choose(0, 4)
        from pre in Gen.Elements("", "", "rc.1", "beta")
        select new SemVersion(major, minor, patch, pre);

    [Fact]
    public void AnUpdateIsOfferedOnlyForAStableNewerReleaseWithItsOwnPage()
    {
        var releases =
            from v in Versions
            from prefix in Gen.Elements("v", "", "V")
            from prerelease in Gen.Elements(false, false, true)
            from draft in Gen.Elements(false, false, true)
            from page in Gen.Elements("its own", "another release's", "a download")
            select (v, tag: prefix + v, prerelease, draft, page);
        Check.One(Cases(200), Prop.ForAll(Arb.From(releases), r =>
        {
            string url = r.page switch
            {
                "its own" => ReleasePage.For(r.tag),
                "another release's" => ReleasePage.For("v9.9.9"),
                _ => ReleasePage.Prefix + "download/" + r.tag + "/PoolWatch.exe",
            };
            var (result, available) = Ask(Encoding.UTF8.GetBytes(Release(r.tag, url, r.prerelease, r.draft)));
            bool stable = !r.v.IsPreRelease && !r.prerelease && !r.draft;
            var expected = !stable ? UpdateOutcome.UpToDate
                : url != ReleasePage.For(r.tag) || !ReleasePage.IsValid(url) ? UpdateOutcome.Failed
                : r.v.CompareTo(Running) > 0 ? UpdateOutcome.Newer
                : UpdateOutcome.UpToDate;
            Assert.Equal(new UpdateResult(Now, expected), result);
            Assert.Equal(expected == UpdateOutcome.Newer ? new UpdateRelease(r.v.ToString(), url) : null, available);
        }));
    }

    [Fact]
    public void AnyAnswerEndsTheCheckAndAnUpdateOnlyEverOpensThatReleasesPage()
    {
        string[] values =
        [
            "\"v1.2.4\"", "\"v99.0.0\"", "\"V2.0.0\"", "\"1.2.4\"", "\"v1.2.4+build\"", "\"v1.2.4-rc.1\"", "\"v1.2.3\"",
            JsonSerializer.Serialize(ReleasePage.For("v99.0.0")), JsonSerializer.Serialize(ReleasePage.Prefix + "download/v99.0.0/PoolWatch.exe"),
            "\"https://github.com/someone-else/poolwatch/releases/tag/v99.0.0\"", JsonSerializer.Serialize(ReleasePage.For("v99.0.0") + "?x=1"),
        ];
        var seeds = Gen.Elements(
            Release("v1.2.4", ReleasePage.For("v1.2.4"), false, false),
            Release("v2.0.0", ReleasePage.For("v2.0.0"), false, false),
            Release("v2.0.0-rc.1", ReleasePage.For("v2.0.0-rc.1"), true, false));
        var answers = Gen.OneOf(AsBytes(Mutated(seeds, values)), AnyBytes);
        Check.One(Cases(300), Prop.ForAll(Arb.From(answers), body =>
        {
            var (result, available) = Ask(body); // an answer that ends the check without an outcome fails here
            Assert.Equal(Now, result.CheckedUtc);
            Assert.Equal(available is not null, result.Outcome == UpdateOutcome.Newer);
            if (available is null) return;
            Assert.True(SemVersion.TryParse(available.Version, out var version), available.Version);
            Assert.False(version!.IsPreRelease);
            Assert.True(version.CompareTo(Running) > 0, available.Version);
            Assert.True(ReleasePage.IsValid(available.PageUrl), available.PageUrl);
            Assert.Contains(available.PageUrl, new[] { ReleasePage.For("v" + version), ReleasePage.For("V" + version), ReleasePage.For(version.ToString()) });
        }));
    }

    /// <summary>
    /// Cases the property found: reading the tag or the page threw (JsonElement.GetString refuses half of a surrogate pair
    /// written as an escape, and bytes that are not UTF-8), so the check ended with no outcome.
    /// </summary>
    [Theory]
    [InlineData("""{ "tag_name": "\ud800", "html_url": "x" }""")]
    [InlineData("""{ "tag_name": "v1.2.4", "prerelease": false, "draft": false, "html_url": "\udc00" }""")]
    public void AnAnswerWithHalfOfASurrogatePairFailsTheCheck(string json) =>
        Assert.Equal(new UpdateResult(Now, UpdateOutcome.Failed), Ask(Encoding.UTF8.GetBytes(json)).Result);

    [Fact]
    public void AnAnswerWithBytesThatAreNotUtf8FailsTheCheck()
    {
        byte[] body = [.. Encoding.UTF8.GetBytes("""{ "tag_name": "v1.2."""), 0xFF, .. Encoding.UTF8.GetBytes("""4", "html_url": "x" }""")];
        Assert.Equal(new UpdateResult(Now, UpdateOutcome.Failed), Ask(body).Result);
    }
}
