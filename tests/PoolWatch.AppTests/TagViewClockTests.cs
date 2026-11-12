using PoolWatch.Core;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// A growing tag's view (a Pool causes row's details) timed on the clock that only goes forward: the span it says, the
/// trace it takes, the events it reads and how long each process it lists ran are the same with the system clock left
/// alone and set back or forward.
/// </summary>
public sealed class TagViewClockTests
{
    private static readonly DateTime T0 = new(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);
    private const long MB = 1L << 20;
    private static readonly PoolTag Reff = PoolTag.Parse("Reff");

    /// <summary><paramref name="count"/> samples <paramref name="intervalMs"/> apart from the forward clock's 1,000,000, Reff climbing a megabyte each, the system clock moved by <paramref name="shift"/>.</summary>
    private static Core.History.History Climb(int count, long intervalMs, Func<int, TimeSpan> shift)
    {
        var h = Core.History.History.ForInterval(intervalMs / 1000.0, rawMinutes: 130);
        for (int i = 0; i < count; i++)
            h.Add(new Sample(T0.AddMilliseconds(i * intervalMs) + shift(i), default, [new PoolTagEntry(Reff, 0, 0, 0, (uint)i, 0, (ulong)(64 * MB + i * MB))], [])
            { TickMs = 1_000_000 + i * intervalMs });
        return h;
    }

    /// <summary>An incident of Reff whose confirmation trace names leaker.exe, closed at <paramref name="closedUtc"/> / <paramref name="closedMs"/>.</summary>
    private static Incident Traced(DateTime closedUtc, long closedMs)
    {
        var start = T0.AddMinutes(10);
        var reg = new ProcessRegistry();
        reg.OnStart(5000, 4, start.ToFileTimeUtc(), "leaker.exe", @"C:\Tools\leaker.exe", "leaker", null, start);
        var usage = new CapturePoolUsage(5000, "leaker.exe", Reff, 300 * MB, 0, 1000, 0, [], start.ToFileTimeUtc(), "leaker", 4, start);
        var cap = new CaptureResult { PoolEventsParsed = true, Tags = [Reff], Pool = [usage] }.Recorded(new Moment(start, 2_000_000), new Moment(start.AddMinutes(1), 2_060_000));
        return new Incident
        {
            Id = "x", Dir = "x", Tag = Reff, Kind = IncidentKind.Growth, OnsetUtc = T0, OnsetMs = 1_000_000, ClosedUtc = closedUtc, ClosedMs = closedMs,
            StartBytes = 64 * MB, PeakBytes = 240 * MB, CurrentBytes = 240 * MB, Capture = cap,
            TraceOwners = TraceAttribution.Owners(TraceAttribution.Resolve(cap, Reff, reg)),
        };
    }

    [Fact]
    public void ATagsViewSaysItsSpanAndTakesALastHoursTraceOnTheForwardClock()
    {
        // 30 minutes of growth (180 samples 10 s apart), and an incident with a trace closed 30 minutes before the newest
        // sample on the forward clock. The system clock set back an hour 15 minutes in (by the samples' times of day the view
        // would span less than nothing), and set forward 2 hours since the incident closed (by the times of day it closed
        // more than an hour ago).
        string View(Func<int, TimeSpan> shift)
        {
            var h = Climb(180, 10_000, shift);
            var inc = Traced(h.Times[^1] - TimeSpan.FromMinutes(30) - shift(179) + shift(0) - TimeSpan.FromHours(2), h.Ms[^1] - 30 * 60_000);
            var view = TagViewBuilder.Build(h, new TagKnowledge(null, null), [inc], (_, _) => [], new ProcessRegistry(), Reff)!;
            return $"{view.Title} | {view.TraceBacked} | {view.Answer.Line}";
        }
        string steady = View(_ => TimeSpan.Zero);
        Assert.Contains(" in the last 29 min | True | ", steady);
        Assert.Equal(steady, View(i => i >= 90 ? -TimeSpan.FromHours(1) : TimeSpan.Zero));
    }

    [Fact]
    public void ATagsLiveViewSpeaksInThePresentOfAWindowThatRunsToNow()
    {
        // A tag's view (a Pool causes row's details) is of the last hour, or what the history holds of it, up to the
        // newest sample: a tag that held still over it "is flat", and one that grew with no clear cause has none
        // "yet". The past ("was flat", "No clear cause was found") is a closed incident's, whose report is written
        // again hours after its span (OwnerRanking.Answer's closed): the view never passes it.
        string View(long perSample)
        {
            var h = Core.History.History.ForInterval(10, rawMinutes: 130);
            for (int i = 0; i < 180; i++)
                h.Add(new Sample(T0.AddSeconds(10 * i), default, [new PoolTagEntry(Reff, 0, 0, 0, (uint)i, 0, (ulong)(64 * MB + i * perSample))], []) { TickMs = 1_000_000 + i * 10_000 });
            return TagViewBuilder.Build(h, new TagKnowledge(null, null), [], (_, _) => [], new ProcessRegistry(), Reff)!.Answer.Line;
        }
        Assert.Equal("FCB_DATA (per-file control block) (Reff, non-paged) is flat over the last 29 min.", View(0));
        Assert.Equal("FCB_DATA (per-file control block) (Reff, non-paged) grew 179 MB in the last 29 min. No clear cause yet: the suspects below account for 0 B of a 179 MB rise.", View(MB));
    }

    /// <summary>Two events at the forward clock's <paramref name="since"/> + 1 ms ("first") and - 1 ms ("before"), read back by forward milliseconds.</summary>
    private static Func<long, long, List<LifecycleEvent>> EventsAround(long since)
    {
        var events = new List<LifecycleEvent>
        {
            new() { TimeUtc = T0, Ms = since + 1, Kind = LifecycleKind.Alert, Image = "Reff", Detail = "first" },
            new() { TimeUtc = T0, Ms = since - 1, Kind = LifecycleKind.Alert, Image = "Reff", Detail = "before" },
        };
        return (from, to) => events.Where(e => e.Ms >= from && e.Ms <= to).ToList();
    }

    [Fact]
    public void ATagsViewReadsTheEventsOfItsWholeHour()
    {
        // Two hours of samples 7 s apart: the hour before the newest sample begins up to 7 s before the first sample in it.
        // An event 1 ms into the hour is among the view's events, with the system clock left alone and set back an hour
        // inside the hour.
        foreach (var shift in new Func<int, TimeSpan>[] { _ => TimeSpan.Zero, i => i >= 900 ? -TimeSpan.FromHours(1) : TimeSpan.Zero })
        {
            var h = Climb(1030, 7_000, shift);
            long since = h.Ms[^1] - 3_600_000;
            Assert.True(h.Ms[h.IndexAtOrAfterMs(since)] > since + 1);
            var view = TagViewBuilder.Build(h, new TagKnowledge(null, null), [], EventsAround(since), new ProcessRegistry(), Reff)!;
            Assert.Equal(["first"], view.Events.Select(e => e.Text.Split(' ')[^1]));
        }
    }

    [Fact]
    public void ASelectedTagsSuspectsReadTheEventsOfTheirWhole10Minutes()
    {
        // 200 samples 7 s apart: the 10 minutes before the newest sample begin up to 7 s before the first sample in them.
        // An event 1 ms into them is among the suspects' events, with the system clock left alone and set back an hour inside
        // them.
        foreach (var shift in new Func<int, TimeSpan>[] { _ => TimeSpan.Zero, i => i >= 150 ? -TimeSpan.FromHours(1) : TimeSpan.Zero })
        {
            var h = Climb(200, 7_000, shift);
            long since = h.Ms[^1] - 600_000;
            Assert.True(h.Ms[h.IndexAtOrAfterMs(since)] > since + 1);
            var (_, events) = UiData.Suspects(h, new ProcessRegistry(), Reff, EventsAround(since));
            Assert.Equal(["first"], events.Select(e => e.Detail));
        }
    }

    [Fact]
    public void AnExitTheRegistryHasNotMarkedYetIsTimedByItsOwnTimeOnTheForwardClock()
    {
        // git.exe starts, read by the registry at the forward clock's 1,000,000; its exit event comes 5 s later on the forward
        // clock, stamped a day later by the system clock, before the registry marked the process exited. It ran 5 s.
        var now = new Moment(T0, 1_000_000);
        var reg = new ProcessRegistry(clock: () => now);
        long create = T0.ToFileTimeUtc();
        reg.OnStart(600, 500, create, "git.exe", null, "git status", null, T0);
        var exit = new LifecycleEvent
        {
            TimeUtc = T0.AddDays(1).AddSeconds(5), Ms = 1_005_000, Kind = LifecycleKind.Exit, Pid = 600, ParentPid = 500, CreateTime = create, Image = "git.exe", ExitCode = 0,
        };
        Assert.Null(reg.Get(exit.Key)!.ExitMs);
        var row = Assert.Single(TagViewBuilder.EventRows([exit], reg, all: false));
        Assert.EndsWith(" git.exe (PID 600) exited with code 0 after 5 s", row.Text);
    }

    [Fact]
    public void HowLongAListedProcessRanIsTimedOnTheForwardClock()
    {
        // The registry reads each start and exit as it arrives. ChatGPT.exe starts; the system clock is set back two hours
        // (by its time of day it now started after its children); git.exe runs 5 s, the clock set forward a day in between:
        // a short command of ChatGPT's. node.exe runs 5 minutes, the clock set back an hour in between: not one, and it ran 5 min.
        var now = new Moment(T0, 1_000_000);
        var reg = new ProcessRegistry(clock: () => now);
        var parent = reg.OnStart(500, 4, T0.ToFileTimeUtc(), "ChatGPT.exe", @"C:\Apps\ChatGPT.exe", "ChatGPT.exe", null, T0);
        now = new Moment(T0 - TimeSpan.FromHours(2), 1_001_000);
        LifecycleEvent Start(int pid, string image) => new()
        {
            TimeUtc = now.Utc, Ms = now.Ms, Kind = LifecycleKind.Start, Pid = pid, ParentPid = 500, CreateTime = now.Utc.ToFileTimeUtc(), Image = image,
        };
        LifecycleEvent Exit(LifecycleEvent start) => start with { TimeUtc = now.Utc, Ms = now.Ms, Kind = LifecycleKind.Exit, ExitCode = 0 };
        var git = Start(600, "git.exe");
        reg.OnStart(600, 500, git.CreateTime, "git.exe", null, "git status", null, now.Utc);
        var node = Start(700, "node.exe");
        reg.OnStart(700, 500, node.CreateTime, "node.exe", null, "node x.js", null, now.Utc);
        now = new Moment(now.Utc.AddDays(1).AddSeconds(5), 1_006_000);
        reg.OnStop(600, git.CreateTime, 0, now.Utc);
        var gitExit = Exit(git);
        now = new Moment(T0 - TimeSpan.FromHours(2) + TimeSpan.FromMinutes(5) - TimeSpan.FromHours(1), 1_301_000);
        reg.OnStop(700, node.CreateTime, 0, now.Utc);
        var nodeExit = Exit(node);
        var rows = TagViewBuilder.EventRows([git, node, gitExit, nodeExit], reg, all: true).Select(r => r.Text[(r.Text.IndexOf(' ') + 1)..]).ToList();
        Assert.Equal(["ChatGPT ran 1 short command: git", "node.exe (PID 700) exited with code 0 after 5 min", "node.exe (PID 700) started (ran 5 min)"], rows.Order(StringComparer.Ordinal));
        Assert.Equal("ChatGPT", Core.Identity.ProcessIdentities.OwnerOf(reg.Ancestry(parent.Key)).Name); // the live parent's owner, not its image's name
    }
}
