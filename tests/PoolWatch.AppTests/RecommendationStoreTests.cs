using PoolWatch.Core.Recommendations;
using PoolWatch.Engine;
using PoolWatch.Engine.Setup;

namespace PoolWatch.AppTests;

/// <summary>setup.json: the guide's completion and the hidden suggestions survive a restart; a bad file counts as empty.</summary>
public sealed class RecommendationStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pw-store-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly Log _log;

    public RecommendationStoreTests()
    {
        _paths = new AppPaths(Path.Combine(_root, "data"), null, secure: false);
        _log = new Log(Path.Combine(_root, "data", "store.log"));
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private string File_ => Path.Combine(_paths.DataRoot, RecommendationStore.FileName);

    [Fact]
    public void RoundTrip()
    {
        var now = DateTime.UtcNow;
        var store = new RecommendationStore(_paths, _log);
        Assert.False(store.GuideCompleted);
        Assert.Empty(store.Dismissed);
        Assert.False(File.Exists(File_)); // nothing is written until something changed

        store.MarkGuideCompleted(now);
        store.Dismiss("TokenLeakGuard", now.AddDays(-2));
        store.Dismiss("ReportIncident:20261028-0712-Reff", now);

        var again = new RecommendationStore(_paths, _log);
        Assert.True(again.GuideCompleted);
        Assert.Equal(2, again.Dismissed.Count);
        Assert.Equal(now.AddDays(-2), again.Dismissed["TokenLeakGuard"]);
        Assert.Equal(now, again.Dismissed["ReportIncident:20261028-0712-Reff"]);
    }

    [Fact]
    public void HidesOlderThanThirtyDaysAreForgotten()
    {
        var now = DateTime.UtcNow;
        var store = new RecommendationStore(_paths, _log);
        store.Dismiss("HourlyCacheClear", now.AddDays(-40));
        store.Dismiss("StartAtSignIn", now);
        Assert.Equal(["StartAtSignIn"], new RecommendationStore(_paths, _log).Dismissed.Keys);
    }

    [Fact]
    public void AHideDatedAfterNowRunsOutThirtyDaysFromNow()
    {
        File.WriteAllText(File_, """{ "dismissed": { "TokenLeakGuard": "2099-01-01T00:00:00Z" } }""");
        var now = DateTime.UtcNow;
        var hidden = new RecommendationStore(_paths, _log).Dismissed;
        Assert.InRange(hidden["TokenLeakGuard"], now.AddMinutes(-1), DateTime.UtcNow);
        Assert.False(RecommendationRules.Hidden(hidden, "TokenLeakGuard", now.AddDays(31)));
    }

    [Fact]
    public void AlwaysInTheDataRoot()
    {
        var headless = new AppPaths(Path.Combine(_root, "data"), Path.Combine(_root, "out"), secure: false);
        new RecommendationStore(headless, _log).MarkGuideCompleted(DateTime.UtcNow);
        Assert.True(File.Exists(File_));
        Assert.False(File.Exists(Path.Combine(_root, "out", RecommendationStore.FileName)));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("""{ "guideCompletedUtc": 3, "dismissed": { "a": 5, "b": "not a time" } }""")]
    [InlineData("""{ "dismissed": [ "TokenLeakGuard" ] }""")]
    public void ABadFileCountsAsEmpty(string text)
    {
        File.WriteAllText(File_, text);
        var store = new RecommendationStore(_paths, _log);
        Assert.False(store.GuideCompleted);
        Assert.Empty(store.Dismissed);
    }

    [Fact]
    public void AnOversizeFileIsNotReadAndKeysAreBounded()
    {
        File.WriteAllText(File_, "{ \"guideCompletedUtc\": \"2026-10-28T09:00:00Z\", \"pad\": \"" + new string('x', 70_000) + "\" }");
        Assert.False(new RecommendationStore(_paths, _log).GuideCompleted);

        var now = DateTime.UtcNow;
        var store = new RecommendationStore(_paths, _log);
        store.Dismiss("", now);
        store.Dismiss(new string('k', 201), now);
        Assert.Empty(store.Dismissed);
        for (int i = 0; i < 250; i++) store.Dismiss($"ReportIncident:{i}", now.AddMinutes(-i));
        var kept = new RecommendationStore(_paths, _log).Dismissed;
        Assert.Equal(200, kept.Count);
        Assert.Contains("ReportIncident:0", kept.Keys);      // the newest are kept
        Assert.DoesNotContain("ReportIncident:249", kept.Keys);
    }
}
