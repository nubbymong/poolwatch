using PoolWatch.Core;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Identity;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Core.Services;
using PoolWatch.Core.Setup;
using PoolWatch.Engine;
using PoolWatch.Engine.Setup;

namespace PoolWatch.AppTests;

/// <summary>The evidence the suggestions read from the incidents in memory: a reportable incident, and ChatGPT named in a growth.</summary>
public class RecommendationInputsBuilderTests
{
    private static readonly DateTime Now = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);
    private const string ChatGptExe = @"C:\Program Files\WindowsApps\OpenAI.Codex_1.2.0.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe";

    private static Suspect Suspect(string image, string? path, Confidence confidence = Confidence.Correlated) => new()
    {
        Key = new ProcessKey(4321, 1), Image = image, ImagePath = path, DisplayName = image, Confidence = confidence,
        Direct = new DirectSignal(null, 0, 0, 0, 0, 0, false), Activity = new ActivitySignal("ops", 0, 0, 0, 0, 0, false),
        Lifecycle = new LifecycleSignal(false, false, false, []),
    };

    private static OwnerSuspect Owner(string key, string name, Confidence confidence, bool meaningful = true, params Suspect[] members) => new()
    {
        Owner = new Owner(OwnerKind.App, key, name), Members = [.. members], Identities = [], Confidence = confidence, Meaningful = meaningful,
    };

    private static Incident Growth(string id, DateTime? closedUtc, params OwnerSuspect[] owners) => new()
    {
        Id = id, Dir = @"C:\ProgramData\PoolWatch\incidents\" + id, Tag = PoolTag.Parse("Reff"), Kind = IncidentKind.Growth, OpenedUtc = Now.AddHours(-3),
        OnsetUtc = Now.AddHours(-3), ClosedUtc = closedUtc, StartBytes = 1L << 30, PeakBytes = 3L << 30, Owners = [.. owners],
    };

    [Fact]
    public void TheNewestClosedIncidentWithACauseNotHidden()
    {
        var older = Growth("20261028-0400-Reff", Now.AddHours(-4), Owner("app:codex", "Codex", Confidence.StronglyCorrelated));
        var newer = Growth("20261028-0600-Reff", Now.AddHours(-2), Owner("app:chatgpt", "ChatGPT", Confidence.Correlated));
        var r = RecommendationInputsBuilder.Reportable([older, newer], new Dictionary<string, DateTime>(), Now);
        Assert.NotNull(r);
        Assert.Equal("20261028-0600-Reff", r.Value.Id);
        Assert.Equal(newer.Dir, r.Value.Dir);
        Assert.StartsWith("Kernel pool grew 2", r.Value.Headline);
        Assert.EndsWith("; the likely cause is ChatGPT, with some evidence.", r.Value.Headline);

        var hidden = new Dictionary<string, DateTime> { ["ReportIncident:20261028-0600-Reff"] = Now.AddDays(-1) };
        Assert.Equal("20261028-0400-Reff", RecommendationInputsBuilder.Reportable([older, newer], hidden, Now)!.Value.Id);
    }

    [Fact]
    public void NotWithoutAClearCauseNotOpenNotOld()
    {
        var none = new Dictionary<string, DateTime>();
        Assert.Null(RecommendationInputsBuilder.Reportable([Growth("weak", Now.AddHours(-1), Owner("app:x", "X", Confidence.Weak))], none, Now));
        Assert.Null(RecommendationInputsBuilder.Reportable([Growth("open", null, Owner("app:x", "X", Confidence.StronglyCorrelated))], none, Now));
        Assert.Null(RecommendationInputsBuilder.Reportable([Growth("old", Now.AddDays(-8), Owner("app:x", "X", Confidence.StronglyCorrelated))], none, Now));
        Assert.Null(RecommendationInputsBuilder.Reportable([Growth("nothing", Now.AddHours(-1))], none, Now));
    }

    [Fact]
    public void ChatGptNamedAsALikelyCause()
    {
        Assert.True(RecommendationInputsBuilder.NamesChatGpt(Growth("a", null, Owner("app:chatgpt", "ChatGPT", Confidence.Correlated))));
        Assert.True(RecommendationInputsBuilder.NamesChatGpt(Growth("b", null, Owner("app:openai.codex", "OpenAI.Codex", Confidence.StronglyCorrelated))));
        Assert.False(RecommendationInputsBuilder.NamesChatGpt(Growth("w", null, Owner("app:chatgpt", "ChatGPT", Confidence.Weak)))); // only active
        Assert.False(RecommendationInputsBuilder.NamesChatGpt(Growth("c", null, Owner("app:chatgpt", "ChatGPT", Confidence.Correlated, meaningful: false))));
        Assert.False(RecommendationInputsBuilder.NamesChatGpt(Growth("d", null, Owner("app:code", "VS Code", Confidence.StronglyCorrelated))));

        var suspect = Growth("e", null);
        suspect.Suspects = [Suspect("node.exe", @"C:\Program Files\nodejs\node.exe"), Suspect("ChatGPT.exe", ChatGptExe)];
        Assert.True(RecommendationInputsBuilder.NamesChatGpt(suspect));
        suspect.Suspects = [Suspect("ChatGPT.exe", ChatGptExe, Confidence.Weak)];
        Assert.False(RecommendationInputsBuilder.NamesChatGpt(suspect));
        suspect.Suspects = [Suspect("a.exe", null), Suspect("b.exe", null), Suspect("c.exe", null), Suspect("ChatGPT.exe", ChatGptExe)];
        Assert.False(RecommendationInputsBuilder.NamesChatGpt(suspect)); // fourth: not a top suspect

        var traced = Growth("t", null);
        traced.TraceOwners = [new TraceOwner { Owner = new Owner(OwnerKind.App, "app:chatgpt", "ChatGPT") }];
        Assert.True(RecommendationInputsBuilder.NamesChatGpt(traced)); // the trace confirmed it
    }

    private static EnvironmentFacts Facts(params string[] clearTasks) => new()
    {
        ScannedUtc = Now.AddHours(-20), WindowsName = "Windows 11 Pro", Build = "26220.7344", Architecture = "x64", OwnClearTasks = clearTasks,
    };

    private static CacheHelper.Status ClearTaskCheck(bool? present) =>
        new(true, present, present == true ? @"\Clear standby: runs RAMMap64.exe -Et as SYSTEM, hourly, enabled" : null, [], null);

    [Fact]
    public void TheHourlyClearTaskCheckUpdatesTheCachedFacts()
    {
        // Removed since the scan: gone. Made since the scan: there, so the hourly clear is not suggested next to it.
        Assert.Empty(RecommendationInputsBuilder.WithClearTasksNow(Facts(@"\Clear standby"), ClearTaskCheck(false))!.OwnClearTasks);
        Assert.Single(RecommendationInputsBuilder.WithClearTasksNow(Facts(), ClearTaskCheck(true))!.OwnClearTasks);

        // Agreeing, or not read yet: the scan's own list.
        Assert.Equal([@"\Clear standby"], RecommendationInputsBuilder.WithClearTasksNow(Facts(@"\Clear standby"), ClearTaskCheck(true))!.OwnClearTasks);
        Assert.Equal([@"\Clear standby"], RecommendationInputsBuilder.WithClearTasksNow(Facts(@"\Clear standby"), ClearTaskCheck(null))!.OwnClearTasks);
        Assert.Empty(RecommendationInputsBuilder.WithClearTasksNow(Facts(), null)!.OwnClearTasks);
        Assert.Null(RecommendationInputsBuilder.WithClearTasksNow(null, ClearTaskCheck(true)));
    }

    [Fact]
    public void ProcessorTimeCountsOnlyOverTheWholeHalfHour()
    {
        var costs = new ProcessCostHistory();
        var steady = new ProcessKey(100, 1);
        var burst = new ProcessKey(200, 1);
        double steadyCpu = 0, burstCpu = 0;
        for (int m = 0; m <= 31; m++)
        {
            steadyCpu += 0.3 * 60;                // 0.3 core all along
            burstCpu += m is 28 or 29 ? 30 : 4;   // mostly about 0.07 core, then a two-minute burst at 0.5
            costs.Observe(Now.AddMinutes(m - 31), [Sample(steady, steadyCpu), Sample(burst, burstCpu)], 0);
        }
        var halfHour = TimeSpan.FromMinutes(30);
        Assert.Equal(0.3, RecommendationInputsBuilder.CoresOverWhole(costs, steady, halfHour, Now)!.Value, 2);
        Assert.True(RecommendationInputsBuilder.CoresOverWhole(costs, burst, halfHour, Now) < 0.25);

        var late = new ProcessCostHistory(); // busy only for the last two minutes: not "over the last half hour"
        for (int m = 0; m <= 2; m++) late.Observe(Now.AddMinutes(m - 2), [Sample(burst, m * 30.0)], 0);
        Assert.Null(RecommendationInputsBuilder.CoresOverWhole(late, burst, halfHour, Now));
        Assert.Null(RecommendationInputsBuilder.CoresOverWhole(late, steady, halfHour, Now)); // never seen
    }

    private static ProcessSample Sample(ProcessKey key, double cpuSeconds) =>
        new() { Pid = key.Pid, CreateTime = key.CreateTime, ImageName = "x.exe", KernelTime = (long)(cpuSeconds * 10_000_000) };
}
