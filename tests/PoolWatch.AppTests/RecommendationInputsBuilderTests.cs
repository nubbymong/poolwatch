using PoolWatch.Core;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Detection;
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
        OpenedMs = AppClocks.Ms(Now.AddHours(-3)), OnsetMs = AppClocks.Ms(Now.AddHours(-3)), ClosedMs = closedUtc is { } c ? AppClocks.Ms(c) : null,
    };

    [Fact]
    public void TheNewestClosedIncidentWithACauseNotHidden()
    {
        var older = Growth("20261028-0400-Reff", Now.AddHours(-4), Owner("app:codex", "Codex", Confidence.StronglyCorrelated));
        var newer = Growth("20261028-0600-Reff", Now.AddHours(-2), Owner("app:chatgpt", "ChatGPT", Confidence.Correlated));
        var r = RecommendationInputsBuilder.Reportable([older, newer], new Dictionary<string, Moment>(), AppClocks.At(Now));
        Assert.NotNull(r);
        Assert.Equal("20261028-0600-Reff", r.Value.Id);
        Assert.Equal(newer.Dir, r.Value.Dir);
        Assert.StartsWith("Kernel pool grew 2", r.Value.Headline);
        Assert.EndsWith("; the likely cause is ChatGPT, with some evidence.", r.Value.Headline);

        var hidden = new Dictionary<string, Moment> { ["ReportIncident:20261028-0600-Reff"] = AppClocks.At(Now.AddDays(-1)) };
        Assert.Equal("20261028-0400-Reff", RecommendationInputsBuilder.Reportable([older, newer], hidden, AppClocks.At(Now))!.Value.Id);
    }

    [Fact]
    public void WhatPoolWatchsOwnClearsFreedOfAGrowthIsSaidAsTheirResult()
    {
        // The growth counts back what PoolWatch's own clear freed (its drop is not the growth ending): the headline and the
        // bundle's line say that part as the clear's, with its time; with no clear they say nothing of clears.
        var clearUtc = Now.AddHours(-2.5);
        var cleared = Growth("20261028-0600-Reff", Now.AddHours(-2), Owner("app:x", "X", Confidence.StronglyCorrelated));
        cleared.ClearedBytes = 512L << 20;
        cleared.ClearedAtUtc = clearUtc;
        cleared.CurrentBytes = (3L << 30) - (512L << 20);
        cleared.CurrentAtUtc = cleared.ClosedUtc; // its level as its close measured it
        string at = clearUtc.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        string onset = cleared.OnsetUtc.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal($"Kernel pool grew 2.0 GB at {onset} (PoolWatch's own cache clear at {at} freed 512 MB of it); the likely cause is X, with strong evidence.",
            RecommendationInputsBuilder.Reportable([cleared], new Dictionary<string, Moment>(), AppClocks.At(Now))!.Value.Headline);
        // A closed incident's level is as it was last measured, and a bundle is made hours or days later: the line gives
        // it with the time of the sample it is of (before, "; now 2.5 GB" of a level two hours old); an open incident's is "now".
        Assert.EndsWith($", closed {Fmt.Stamp(cleared.ClosedUtc!.Value)}; 2.5 GB at {Fmt.Stamp(cleared.ClosedUtc.Value)}, after PoolWatch's own cache clear at {Fmt.Clock(clearUtc)} freed 512 MB of it",
            PoolWatch.Analysis.BundleBuilder.IncidentLine(cleared));
        var open = Growth("20261028-0610-Reff", null, Owner("app:x", "X", Confidence.StronglyCorrelated));
        (open.ClearedBytes, open.ClearedAtUtc, open.CurrentBytes, open.CurrentAtUtc) = (512L << 20, clearUtc, (3L << 30) - (512L << 20), Now);
        Assert.EndsWith($", open; now 2.5 GB, after PoolWatch's own cache clear at {Fmt.Clock(clearUtc)} freed 512 MB of it", PoolWatch.Analysis.BundleBuilder.IncidentLine(open));

        // A clear that freed more of the tag than the growth (it emptied a cache that was there before) freed it of the tag.
        var more = Growth("20261028-0630-Reff", Now.AddHours(-1.5), Owner("app:x", "X", Confidence.StronglyCorrelated));
        more.ClearedBytes = 3L << 30;
        more.ClearedAtUtc = clearUtc;
        Assert.Equal($"Kernel pool grew 2.0 GB at {onset} (PoolWatch's own cache clear at {at} freed 3.0 GB of the tag that grew); the likely cause is X, with strong evidence.",
            RecommendationInputsBuilder.Reportable([more], new Dictionary<string, Moment>(), AppClocks.At(Now))!.Value.Headline);
        more.ClearedBytes = 2L << 30;
        Assert.EndsWith(" freed 2.0 GB of it); the likely cause is X, with strong evidence.", RecommendationInputsBuilder.Reportable([more], new Dictionary<string, Moment>(), AppClocks.At(Now))!.Value.Headline);

        var plain = Growth("20261028-0700-Reff", Now.AddHours(-1), Owner("app:x", "X", Confidence.StronglyCorrelated));
        Assert.Equal($"Kernel pool grew 2.0 GB at {onset}; the likely cause is X, with strong evidence.",
            RecommendationInputsBuilder.Reportable([plain], new Dictionary<string, Moment>(), AppClocks.At(Now))!.Value.Headline);
        Assert.EndsWith($", closed {Fmt.Stamp(plain.ClosedUtc!.Value)}", PoolWatch.Analysis.BundleBuilder.IncidentLine(plain));
    }

    [Fact]
    public void ATracesIncidentWithNoSpanIsListedInTheBundleByWhatItsReportSays()
    {
        // An incident made for a trace of its own ("Capture now") is made closed, its start and its end the moment it
        // is made, and gets its span from its trace. A vendor bundle made while the trace still records, 40 s on,
        // lists it in incidents.md by what its report says until then: that its trace had not finished by the time
        // of the newest sample as the bundle is made (the moment the incident was made, when PoolWatch holds no
        // sample), with the level its tag held as it was last measured, when it was. One whose trace recorded
        // nothing PoolWatch could use never gets a span, and its line says that. Neither reads as a growth. Once a
        // trace with a span is attached, the line gives the growth over it, as before.
        // (Before, both were listed as "100 MB -> 100 MB (+0 B)": a growth of nothing, which reads as what a trace
        // found.)
        var made = Now.AddMinutes(-10);
        var newest = made.AddSeconds(40);
        Incident Trace() => new()
        {
            Id = "20261028-0850-File", Dir = @"C:\ProgramData\PoolWatch\incidents\20261028-0850-File", Tag = PoolTag.Parse("File"), Kind = IncidentKind.Capture,
            TraceFor = CaptureSource.Manual, OpenedUtc = made, OpenedMs = AppClocks.Ms(made), OnsetUtc = made, OnsetMs = AppClocks.Ms(made),
            ClosedUtc = made, ClosedMs = AppClocks.Ms(made), StartBytes = 100L << 20, PeakBytes = 100L << 20, CurrentBytes = 100L << 20, CurrentAtUtc = made,
        };
        string when = $", onset {Fmt.Stamp(made)}, closed {Fmt.Stamp(made)}", held = $" (the tag held 100 MB at {Fmt.Stamp(made)})";
        var recording = Trace();
        Assert.Equal($"- Capture on `File`: its trace had not finished by {Fmt.Stamp(newest)}{held}{when}", PoolWatch.Analysis.BundleBuilder.IncidentLine(recording, newest));
        Assert.Equal($"- Capture on `File`: its trace had not finished by {Fmt.Stamp(made)}{held}{when}", PoolWatch.Analysis.BundleBuilder.IncidentLine(recording));
        var unread = Trace();
        unread.CurrentAtUtc = null; // its level was never read from a sample
        Assert.Equal($"- Capture on `File`: its trace had not finished by {Fmt.Stamp(newest)}{when}", PoolWatch.Analysis.BundleBuilder.IncidentLine(unread, newest));

        var nothing = Trace();
        nothing.Capture = new CaptureResult { Error = "wpr.exe could not start" }.Recorded(AppClocks.At(made), AppClocks.At(made));
        Assert.Equal($"- Capture on `File`: its trace recorded nothing PoolWatch could use{held}{when}", PoolWatch.Analysis.BundleBuilder.IncidentLine(nothing, newest));

        // A trace of a minute, attached: the incident starts and closes with it, and its line gives the growth over it.
        var (start, end) = (made.AddSeconds(3.7), made.AddSeconds(63.7));
        var recorded = Trace();
        recorded.Capture = new CaptureResult { PoolEventsParsed = true }.Recorded(AppClocks.At(start), AppClocks.At(end));
        (recorded.OnsetUtc, recorded.ClosedUtc, recorded.PeakBytes, recorded.CurrentBytes, recorded.CurrentAtUtc) = (start, end, 118L << 20, 118L << 20, end);
        string grew = $"- Capture on `File`: 100 MB -> 118 MB (+18 MB), onset {Fmt.Stamp(start)}, closed {Fmt.Stamp(end)}";
        Assert.Equal(grew, PoolWatch.Analysis.BundleBuilder.IncidentLine(recorded, newest));

        // incidents.md, with the newest sample's time it is handed: no line of a trace without a span gives a growth.
        var lines = PoolWatch.Analysis.BundleBuilder.IncidentsMarkdown([recording, nothing, recorded], newest).Split(Environment.NewLine).Where(l => l.StartsWith("- Capture on ", StringComparison.Ordinal)).ToList();
        Assert.Equal([$"- Capture on `File`: its trace had not finished by {Fmt.Stamp(newest)}{held}{when}", $"- Capture on `File`: its trace recorded nothing PoolWatch could use{held}{when}", grew], lines);
        Assert.All(lines.Take(2), l => Assert.DoesNotContain(" -> ", l, StringComparison.Ordinal));
        Assert.All(lines.Take(2), l => Assert.DoesNotContain("(+", l, StringComparison.Ordinal));
    }

    [Fact]
    public void NotWithoutAClearCauseNotOpenNotOld()
    {
        var none = new Dictionary<string, Moment>();
        Assert.Null(RecommendationInputsBuilder.Reportable([Growth("weak", Now.AddHours(-1), Owner("app:x", "X", Confidence.Weak))], none, AppClocks.At(Now)));
        Assert.Null(RecommendationInputsBuilder.Reportable([Growth("open", null, Owner("app:x", "X", Confidence.StronglyCorrelated))], none, AppClocks.At(Now)));
        Assert.Null(RecommendationInputsBuilder.Reportable([Growth("old", Now.AddDays(-8), Owner("app:x", "X", Confidence.StronglyCorrelated))], none, AppClocks.At(Now)));
        Assert.Null(RecommendationInputsBuilder.Reportable([Growth("nothing", Now.AddHours(-1))], none, AppClocks.At(Now)));
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
        var now = TimeSpan.FromHours(30); // on the history's clock, which only goes forward
        double steadyCpu = 0, burstCpu = 0;
        for (int m = 0; m <= 31; m++)
        {
            steadyCpu += 0.3 * 60;                // 0.3 core all along
            burstCpu += m is 28 or 29 ? 30 : 4;   // mostly about 0.07 core, then a two-minute burst at 0.5
            costs.Observe(now + TimeSpan.FromMinutes(m - 31), [Sample(steady, steadyCpu), Sample(burst, burstCpu)], 0);
        }
        var halfHour = TimeSpan.FromMinutes(30);
        Assert.Equal(0.3, RecommendationInputsBuilder.CoresOverWhole(costs, steady, halfHour, now)!.Value, 2);
        Assert.True(RecommendationInputsBuilder.CoresOverWhole(costs, burst, halfHour, now) < 0.25);

        var late = new ProcessCostHistory(); // busy only for the last two minutes: not "over the last half hour"
        for (int m = 0; m <= 2; m++) late.Observe(now + TimeSpan.FromMinutes(m - 2), [Sample(burst, m * 30.0)], 0);
        Assert.Null(RecommendationInputsBuilder.CoresOverWhole(late, burst, halfHour, now));
        Assert.Null(RecommendationInputsBuilder.CoresOverWhole(late, steady, halfHour, now)); // never seen
    }

    private static ProcessSample Sample(ProcessKey key, double cpuSeconds) =>
        new() { Pid = key.Pid, CreateTime = key.CreateTime, ImageName = "x.exe", KernelTime = (long)(cpuSeconds * 10_000_000) };

    // ------------------------------------------------------------------ the token leak, seen in the history

    private const long MB = 1L << 20;

    /// <summary>A sample a minute for the last 70 minutes, security tokens (Toke) growing <paramref name="mbPerMinute"/> a minute in the paged pool.</summary>
    private static Core.History.History TokensGrowing(double mbPerMinute)
    {
        var h = new Core.History.History();
        for (int i = 0; i <= 70; i++)
        {
            ulong toke = (ulong)(200 * MB + i * mbPerMinute * MB);
            h.Add(new Sample(Now.AddMinutes(i - 70), new PoolTotals(8UL << 30, 4UL << 30, 0, 0), [new PoolTagEntry(PoolTag.Parse("Toke"), 1, 0, toke, 0, 0, 0)], []) { TickMs = AppClocks.At(Now.AddMinutes(i - 70)).Ms });
        }
        return h;
    }

    [Theory]
    [InlineData(1.0, "26220.7344", 200_000u, true)]    // 60 MB an hour on 25H2, the setting that stops it off: the guard is offered
    [InlineData(0.1, "26220.7344", 200_000u, false)]   // 6 MB an hour: slower than the pack's 20
    [InlineData(1.0, "22631.4317", 200_000u, false)]   // 23H2: not a build the pack names
    [InlineData(1.0, "26220.7344", 0u, false)]         // the setting is on already: what grows has another cause
    [InlineData(1.0, "26220.7344", null, true)]        // the setting could not be read: the evidence alone
    public void TheTokenLeakIsSeenInTheHistoryAsPoolCausesSeesIt(double mbPerMinute, string build, uint? lockTimeout, bool seen)
    {
        var inputs = new Core.Recommendations.RecommendationInputs { NowUtc = Now, NowMs = AppClocks.Ms(Now), Facts = Facts() with { Build = build } };
        var with = RecommendationInputsBuilder.WithTokenLeakSeen(inputs, TokensGrowing(mbPerMinute), [], lockTimeout, []);
        Assert.Equal(seen, with.TokenLeakSeen);
        Assert.Equal(inputs with { TokenLeakSeen = seen }, with); // nothing else is touched
        // And what the rules make of it: the guard offered, with the leak's reason, only while it is seen.
        Assert.Equal(seen, Core.Recommendations.RecommendationRules.Current(with with { AutoClearOn = true })
            .Any(r => r.Id == Core.Recommendations.RecommendationId.TokenLeakGuard));
    }
}
