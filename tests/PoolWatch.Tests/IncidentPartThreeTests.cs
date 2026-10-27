using PoolWatch.Core;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Identity;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;

namespace PoolWatch.Tests;

/// <summary>Review Part 3, addendum 1: 2a (the ranking follows the trace), 2b (window by time), 2e (owners by identity), 2f (every alert).</summary>
public class IncidentPartThreeTests
{
    private static readonly DateTime T0 = new(2026, 10, 26, 6, 34, 0, DateTimeKind.Utc);
    private static readonly PoolTag Reff = PoolTag.Parse("Reff");
    private const long MB = 1L << 20;

    private static Incident Inc(bool open = false, DateTime? end = null, DateTime? captureEnd = null) => new()
    {
        Id = "x", Dir = "x", Tag = Reff, Kind = IncidentKind.Growth, OnsetUtc = T0, LastRiseUtc = T0.AddMinutes(26), EndUtc = end,
        ClosedUtc = open ? null : T0.AddMinutes(40),
        Capture = captureEnd is { } ce ? new CaptureResult { StartUtc = ce.AddMinutes(-1), EndUtc = ce } : null,
    };

    [Fact]
    public void ProtectedWindowIsTheIncidentWindowPlusTenMinutes()
    {
        Assert.Equal((null, null), IncidentManager.ProtectWindow([]));
        Assert.Equal((T0.AddMinutes(-10), (DateTime?)null), IncidentManager.ProtectWindow([Inc(open: true)]));             // open: up to now
        Assert.Equal((T0.AddMinutes(-10), T0.AddMinutes(30)), IncidentManager.ProtectWindow([Inc(end: T0.AddMinutes(20))])); // growth end + 10
        Assert.Equal((T0.AddMinutes(-10), T0.AddMinutes(36)), IncidentManager.ProtectWindow([Inc()]));                       // last rise + 10
        // A trace taken after the growth ended (held back by the governor) is covered too.
        Assert.Equal(T0.AddMinutes(65), IncidentManager.ProtectWindow([Inc(end: T0.AddMinutes(20), captureEnd: T0.AddMinutes(55))]).until);
        // Any open incident keeps the window open-ended.
        Assert.Null(IncidentManager.ProtectWindow([Inc(end: T0.AddMinutes(20)), Inc(open: true)]).until);
    }

    [Fact]
    public void ShortCommandsPerOwnerAreMatchedByIdentityNotName()
    {
        // Two Claude sessions in folders with the same name: one display name, two owners.
        var events = new List<LifecycleEvent>
        {
            new() { TimeUtc = T0.AddMinutes(1), Kind = LifecycleKind.Rollup, Owner = "Claude Code session app", OwnerKey = "claude:4000", Count = 300 },
            new() { TimeUtc = T0.AddMinutes(2), Kind = LifecycleKind.Rollup, Owner = "Claude Code session app", OwnerKey = "claude:5000", Count = 112 },
            new() { TimeUtc = T0.AddMinutes(3), Kind = LifecycleKind.Rollup, Owner = "Claude Code session app", OwnerKey = "claude:4000", Count = 12 },
            new() { TimeUtc = T0.AddMinutes(90), Kind = LifecycleKind.Rollup, Owner = "x", OwnerKey = "claude:4000", Count = 999 }, // outside the window
            new() { TimeUtc = T0.AddMinutes(2), Kind = LifecycleKind.Rollup, Owner = "no key", Count = 5 },                     // no identity: not matched
        };
        var starts = IncidentManager.StartsByOwner(events, T0, T0.AddMinutes(30));
        Assert.Equal(312, starts["claude:4000"]);
        Assert.Equal(112, starts["claude:5000"]);
        Assert.Equal(2, starts.Count);
    }

    private static Alert A(double minutes, long bytes, double window = 10) =>
        new(AlertKind.Growth, Reff, T0.AddMinutes(minutes), $"grew > 200 MB in {window} min", bytes, TimeSpan.FromMinutes(window), T0);

    [Fact]
    public void EveryAlertIsOnTheTimeline()
    {
        var inc = Inc(open: true);
        IncidentManager.NoteAlert(inc, A(0, 230 * MB), "opened: grew > 200 MB in 10 min (+230 MB)");
        for (int i = 1; i <= 37; i++) IncidentManager.NoteAlert(inc, A(i / 6.0, 230 * MB + i * MB)); // every 10 s, +1 MB each
        var entry = Assert.Single(inc.Timeline);
        Assert.StartsWith("opened: grew > 200 MB in 10 min (+230 MB); fired 37 more times to ", entry.Text);
        Assert.EndsWith("latest +267 MB", entry.Text);

        // 25% more growth: its own entry.
        IncidentManager.NoteAlert(inc, A(6.5, 300 * MB));
        Assert.Equal(2, inc.Timeline.Count);
        Assert.Equal("alert: grew > 200 MB in 10 min (+300 MB)", inc.Timeline[1].Text);
        IncidentManager.NoteAlert(inc, A(6.6, 301 * MB));
        Assert.Contains("fired 1 more time to", inc.Timeline[1].Text);

        // The rule stops for over two minutes and fires again: a new run, a new entry.
        IncidentManager.NoteAlert(inc, A(10, 302 * MB));
        Assert.Equal(3, inc.Timeline.Count);
        // Another rule (the 60-minute one) has its own run.
        IncidentManager.NoteAlert(inc, A(10.1, 1100 * MB, window: 60));
        Assert.Equal(4, inc.Timeline.Count);
        // Every alert is accounted for: 1 + 37 + 1 + 1 + 1 + 1 = 42 alerts on 4 entries.
        int counted = inc.Timeline.Sum(t => 1 + (t.Text.Contains("; fired ") ? int.Parse(t.Text.Split("; fired ")[1].Split(' ')[0]) : 0));
        Assert.Equal(42, counted);
    }

    private static OwnerSuspect Corr(string key, string name, Confidence c, double score) =>
        new() { Owner = new Owner(OwnerKind.App, key, name), Members = [], Identities = [], Confidence = c, Score = score, Meaningful = true };

    private static TraceOwner Traced(string key, string name, OwnerKind kind, params long[] nets)
    {
        var o = new TraceOwner { Owner = new Owner(kind, key, name) };
        int pid = 100;
        foreach (var n in nets)
        {
            var u = new CapturePoolUsage(pid++, "grep.exe", Reff, n, 0, 10, 0, []);
            o.Processes.Add(new TracedProcess(u, new ProcessIdentity("grep.exe", null, o.Owner, false), "grep x", ["claude.exe", "bash.exe", "grep.exe"], T0, T0.AddSeconds(1), true));
        }
        o.Seal();
        return o;
    }

    [Fact]
    public void TheRankingFollowsTheTraceAndCorrelationGoesBelow()
    {
        // Correlation put Windows Update first (as in a real incident); the trace says a Claude session allocated it.
        var correlation = new List<OwnerSuspect>
        {
            Corr("svc:wuauserv", "svchost.exe (wuauserv)", Confidence.Correlated, 3),
            Corr("app:streamdeck", "Stream Deck", Confidence.Weak, 1),
            Corr("app:codex", "Codex", Confidence.Weak, 0.5),
        };
        correlation[1].Plain.Add("no meaningful activity in the window");
        var trace = new List<TraceOwner>
        {
            Traced("app:streamdeck", "Stream Deck", OwnerKind.App, 2 * MB),
            Traced("claude:4000", "Claude Code session sample-app (PID 4000)", OwnerKind.ClaudeSession, 82 * MB, 65 * MB),
        };
        var ranked = TraceAttribution.Rank(trace, correlation, Reff);
        Assert.Equal(["claude:4000", "app:streamdeck", "svc:wuauserv", "app:codex"], ranked.Select(o => o.Owner.Key));
        Assert.Equal([1, 2, 3, 4], ranked.Select(o => o.Rank));

        var claude = ranked[0];                         // only the trace saw it: added
        Assert.Same(trace[1], claude.Trace);
        Assert.Equal(Confidence.ConfirmedByTrace, claude.Confidence);
        Assert.False(claude.CorrelationOnly);
        Assert.StartsWith("the confirmation trace saw two processes of it allocate 147 MB of Reff, none freed", claude.Plain[0]);
        Assert.Equal("Claude Code session sample-app (PID 4000): 2 traced processes (grep.exe ×2)", claude.Summary);

        var deck = ranked[1];                           // both saw it: the correlation owner, with the trace line on top
        Assert.Same(correlation[1], deck);
        Assert.Equal(Confidence.ConfirmedByTrace, deck.Confidence);
        Assert.DoesNotContain("no meaningful activity in the window", deck.Plain);

        Assert.True(ranked[2].CorrelationOnly);         // correlation only, below
        Assert.Equal(Confidence.Correlated, ranked[2].Confidence);
        Assert.True(ranked[3].CorrelationOnly);
    }
}
