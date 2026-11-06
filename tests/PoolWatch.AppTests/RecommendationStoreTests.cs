using PoolWatch.Core.Model;
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

    /// <summary>The moment <paramref name="t"/> before <paramref name="c"/>'s now, on both clocks.</summary>
    private static Moment Ago(TwoClocks c, TimeSpan t) => new(c.Utc - t, c.Ms - (long)t.TotalMilliseconds);

    [Fact]
    public void RoundTrip()
    {
        var c = new TwoClocks();
        var store = new RecommendationStore(_paths, _log, c.Now);
        Assert.False(store.GuideCompleted);
        Assert.Empty(store.Dismissed);
        Assert.False(File.Exists(File_)); // nothing is written until something changed

        store.MarkGuideCompleted(c.Utc);
        store.Dismiss("TokenLeakGuard", Ago(c, TimeSpan.FromDays(2)));
        store.Dismiss("ReportIncident:20261028-0712-Reff", c.Now());

        // Read back at the same moment: each hide's time of day, read onto the forward clock as the file loads.
        var again = new RecommendationStore(_paths, _log, c.Now);
        Assert.True(again.GuideCompleted);
        Assert.Equal(2, again.Dismissed.Count);
        Assert.Equal(Ago(c, TimeSpan.FromDays(2)), again.Dismissed["TokenLeakGuard"]);
        Assert.Equal(c.Now(), again.Dismissed["ReportIncident:20261028-0712-Reff"]);
    }

    [Fact]
    public void HidesOlderThanThirtyDaysAreForgotten()
    {
        var c = new TwoClocks();
        var store = new RecommendationStore(_paths, _log, c.Now);
        store.Dismiss("HourlyCacheClear", Ago(c, TimeSpan.FromDays(40)));
        store.Dismiss("StartAtSignIn", c.Now());
        Assert.Equal(["StartAtSignIn"], new RecommendationStore(_paths, _log, c.Now).Dismissed.Keys);
    }

    [Fact]
    public void AHideDatedAfterNowRunsOutThirtyDaysFromNow()
    {
        File.WriteAllText(File_, """{ "dismissed": { "TokenLeakGuard": "2099-01-01T00:00:00Z" } }""");
        var c = new TwoClocks();
        var hidden = new RecommendationStore(_paths, _log, c.Now).Dismissed;
        Assert.Equal(c.Now(), hidden["TokenLeakGuard"]);
        Assert.True(RecommendationRules.Hidden(hidden, "TokenLeakGuard", c.Ms + (long)TimeSpan.FromDays(29).TotalMilliseconds));
        Assert.False(RecommendationRules.Hidden(hidden, "TokenLeakGuard", c.Ms + (long)TimeSpan.FromDays(31).TotalMilliseconds));
    }

    [Fact]
    public void AHidesThirtyDaysAreTimedOnTheForwardClockFromWhenItWasHiddenOrLoaded()
    {
        // setup.json as an earlier start left it: a hide 29 days old by the system clock, read onto the forward clock as the
        // file loads. Half a day on, with the system clock set forward 40 days, it is still hidden; a day and a half on, with
        // the clock set back 80 days, it is not.
        var c = new TwoClocks();
        File.WriteAllText(File_, $$"""{ "dismissed": { "TokenLeakGuard": "{{c.Utc.AddDays(-29):O}}" } }""");
        var store = new RecommendationStore(_paths, _log, c.Now);
        var hidden = store.Dismissed;
        Assert.Equal(c.Ms - (long)TimeSpan.FromDays(29).TotalMilliseconds, hidden["TokenLeakGuard"].Ms);
        c.Pass(TimeSpan.FromHours(12));
        c.SetSystemClock(TimeSpan.FromDays(40));
        Assert.True(RecommendationRules.Hidden(hidden, "TokenLeakGuard", c.Ms));
        c.Pass(TimeSpan.FromDays(1));
        c.SetSystemClock(-TimeSpan.FromDays(80));
        Assert.False(RecommendationRules.Hidden(hidden, "TokenLeakGuard", c.Ms));
        // A hide made in this run is kept, and forgotten, by its own time on the forward clock: 29 days on, with the system
        // clock set forward 60 days, it is kept; 31 days on, with the clock set back 120 days, it is forgotten.
        store.Dismiss("HourlyCacheClear", c.Now());
        c.Pass(TimeSpan.FromDays(29));
        c.SetSystemClock(TimeSpan.FromDays(60));
        store.Dismiss("StartAtSignIn", c.Now());
        Assert.Contains("HourlyCacheClear", store.Dismissed.Keys);
        c.Pass(TimeSpan.FromDays(2));
        c.SetSystemClock(-TimeSpan.FromDays(120));
        store.Dismiss("CheckTokenLeak", c.Now());
        Assert.Equal(["CheckTokenLeak", "StartAtSignIn"], store.Dismissed.Keys.Order());
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

        var c = new TwoClocks();
        var store = new RecommendationStore(_paths, _log, c.Now);
        store.Dismiss("", c.Now());
        store.Dismiss(new string('k', 201), c.Now());
        Assert.Empty(store.Dismissed);
        for (int i = 0; i < 250; i++) store.Dismiss($"ReportIncident:{i}", Ago(c, TimeSpan.FromMinutes(i)));
        var kept = new RecommendationStore(_paths, _log, c.Now).Dismissed;
        Assert.Equal(200, kept.Count);
        Assert.Contains("ReportIncident:0", kept.Keys);      // the newest are kept
        Assert.DoesNotContain("ReportIncident:249", kept.Keys);
    }

    [Fact]
    public void AHiddenFeatureCardStaysHiddenForGood()
    {
        var store = new RecommendationStore(_paths, _log);
        Assert.Empty(store.HiddenFeatures);
        var marks = Discovery.All.Single(c => c.Id == DiscoveryId.Mark);
        store.HideFeature(marks.Key);
        store.HideFeature("TokenLeakGuard");          // not a feature card's key: an offer is hidden for 30 days, not for good
        store.HideFeature(DiscoveryCard.KeyPrefix);      // no card
        store.Dismiss("ReportIncident:x", Moment.Now());

        var again = new RecommendationStore(_paths, _log);
        Assert.Equal([marks.Key], again.HiddenFeatures);
        Assert.Equal(["ReportIncident:x"], again.Dismissed.Keys);      // the offers' hides are kept apart
        Assert.Equal(Discovery.All.Where(c => c.Id != DiscoveryId.Mark), Discovery.Shown(again.HiddenFeatures));
        // A hide of a feature has no time, so it never runs out.
        using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(File_)))
            Assert.Equal(System.Text.Json.JsonValueKind.Array, doc.RootElement.GetProperty("hiddenFeatures").ValueKind);
        // Hiding one again changes nothing; hiding them all hides every card.
        foreach (var card in Discovery.All) again.HideFeature(card.Key);
        Assert.Empty(Discovery.Shown(new RecommendationStore(_paths, _log).HiddenFeatures));
    }

    [Theory]
    [InlineData("""{ "hiddenFeatures": "feature:Mark" }""")]
    [InlineData("""{ "hiddenFeatures": [ 5, null, "TokenLeakGuard", "feature:", "feature:mark", "feature:Mark " ] }""")]
    public void ABadListOfHiddenFeaturesHidesNothing(string text)
    {
        File.WriteAllText(File_, text);
        Assert.Empty(new RecommendationStore(_paths, _log).HiddenFeatures);
    }

    [Fact]
    public void OnlyTheKeysOfCardsThereAreAreKept()
    {
        // A file written by hand (or by something else) with hundreds of made-up keys: none is kept, so none is saved back,
        // and the list is never longer than the cards.
        var keys = Enumerable.Range(0, 300).Select(i => $"feature:{i}").Append("feature:" + new string('k', 300))
            .Concat(Discovery.All.Select(c => c.Key)).Concat(Discovery.All.Select(c => c.Key)).ToList();
        File.WriteAllText(File_, System.Text.Json.JsonSerializer.Serialize(new { hiddenFeatures = keys }));
        var store = new RecommendationStore(_paths, _log);
        Assert.Equal(Discovery.All.Select(c => c.Key).Order(), store.HiddenFeatures.Order());
        store.HideFeature("feature:Other");
        Assert.DoesNotContain("feature:Other", store.HiddenFeatures);
        store.Dismiss("ReportIncident:y", Moment.Now()); // saves the file again
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(File_));
        Assert.Equal(Discovery.All.Count, doc.RootElement.GetProperty("hiddenFeatures").GetArrayLength());
    }

    [Fact]
    public void AKeyGivenOverAndOverOrTextThatIsNotValidNeverDropsARealHide()
    {
        // [A, A, A, B]: B stays hidden. A lone half of a surrogate pair (as a JSON escape) is skipped, not a reason to drop
        // the whole file (which would open setup again).
        string a = Discovery.All[0].Key, b = Discovery.All[^1].Key;
        File.WriteAllText(File_, $$"""{ "guideCompletedUtc": "2026-10-30T01:00:00Z", "hiddenFeatures": ["{{a}}", "{{a}}", "{{a}}", "\ud800", "{{b}}"] }""");
        var store = new RecommendationStore(_paths, _log);
        Assert.Equal(new[] { a, b }.Order(StringComparer.Ordinal), store.HiddenFeatures.Order(StringComparer.Ordinal));
        Assert.True(store.GuideCompleted);
    }
}
