using PoolWatch.Core;
using PoolWatch.Core.Detection;
using PoolWatch.Core.History;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;

using static PoolWatch.Tests.Clocks;

namespace PoolWatch.Tests;

/// <summary>
/// How an incident's folder is written on a busy PC: its events.csv has every event of its window's first part from its
/// first row to its last however far the write budget held it back, and both ends of its window once it closes, an alert
/// that keeps firing is one row, a closed incident's counted repeats wait half an hour, the folder written longest ago gets
/// the room first, and an unchanged chart is not drawn again. The shapes are the owner's (about 26 events every 10 s
/// sample, 2026-11-09). (<c>BothEndsTests</c> holds the two ends to their counts and times.)
/// </summary>
public sealed class IncidentPersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pw-persist-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>The event rows of an events.csv (its alerts and its last note aside), each as its PID.</summary>
    private static List<int> EventPids(string dir) => File.ReadAllLines(Path.Combine(dir, "events.csv")).Skip(1)
        .Select(PoolWatch.Core.Util.Csv.Split).Where(c => c[1] == "Start").Select(c => int.Parse(c[2])).ToList();

    /// <summary>The event rows' PIDs before the <c>Gap</c> row and after it (none after it when there is no such row).</summary>
    private static (List<int> First, List<int> Latest) EventPidsByEnd(string dir)
    {
        var rows = File.ReadAllLines(Path.Combine(dir, "events.csv")).Skip(1).Select(PoolWatch.Core.Util.Csv.Split).ToList();
        int gap = rows.FindIndex(c => c[1] == "Gap");
        List<int> Pids(IEnumerable<List<string>> part) => part.Where(c => c[1] == "Start").Select(c => int.Parse(c[2])).ToList();
        return gap < 0 ? (Pids(rows), []) : (Pids(rows.Take(gap)), Pids(rows.Skip(gap + 1)));
    }

    [Fact]
    public void UnderTheOwnersLoadEventsCsvHoldsEveryEventFromItsFirstRowToItsLast()
    {
        // One growth incident open for 100 minutes on a PC that records 26 events every 10 s sample (9,400 an hour), with
        // the default write budget: the window passes the 500 events of its first part long before its events.csv has
        // caught up. Its rows are the incident's events from its first row on, with none missing between its first row and
        // its last (each event's PID is one more than the one before, so a gap shows as a missing PID); at the end of the
        // run it holds both ends of the window its report gives: the first 500 events from the window's start as measured
        // then (below), a Gap row, and the latest 500 up to the window's end. The incident outlives the raw history (70
        // minutes): its onset is not moved to the history's first sample. (Before both ends were kept, its file at the end
        // of the run ended with the first part and a Note row.)
        Directory.CreateDirectory(_dir);
        var host = new CountingHost(_dir);
        var w = new World { Interval = TimeSpan.FromSeconds(10) };
        var settings = new Settings { GrowthRules = [new(10, 50 * Settings.MB)] };
        var mgr = new IncidentManager(settings, new TagKnowledge(null, null), host, w.Registry);
        var det = new Detector(settings);
        w.AddProcess(4, 0, "System");
        for (int p = 0; p < 10; p++) w.AddProcess(100 + p, 4, "node.exe");
        long tag = 100L << 20;
        int pid = 500_000;
        var rnd = new Random(3);
        var sampled = new List<(int LastPid, long Ms)>(); // each sample's last event, and the moment its events are read at
        long? opened = null; // where the incident's window started when it opened
        for (int i = 0; i < 630; i++)
        {
            for (int p = 0; p < 10; p++) w.Io(100 + p, other: 50 + rnd.Next(500));
            if (i >= 30) tag += 1L << 20; // 6 MB a minute from minute 5: the 50 MB in 10 min rule fires
            w.Sample(("T000", tag));
            var now = w.Now - w.Interval;
            var events = Enumerable.Range(0, 26).Select(k => new LifecycleEvent
            {
                TimeUtc = now.AddMilliseconds(k * 300), Kind = LifecycleKind.Start, Pid = pid++, CreateTime = now.ToFileTimeUtc() + k, ParentPid = 100 + k % 10,
                Image = "node.exe", CommandLine = "node build.js --step " + new string('x', 200 + rnd.Next(300)),
            }).ToList();
            sampled.Add((pid - 1, At(now).Ms));
            mgr.AddEvents(events, At(now));
            mgr.Process(w.History, det, det.Evaluate(w.History), At(now));
            if (opened is null && mgr.Open.Count > 0) opened = IncidentManager.EventWindowStartMs(mgr.Open.Single());
            host.Settle();
        }
        var inc = Assert.Single(mgr.Open);
        Assert.NotNull(opened);
        long MsOf(int p) { var s = sampled.First(x => x.LastPid >= p); return s.Ms + (p - (s.LastPid - 25)) * 300L; }
        int FirstFrom(long ms) => Enumerable.Range(500_000, pid - 500_000).First(p => MsOf(p) >= ms);
        var open = EventPids(inc.Dir);
        Assert.NotEmpty(open);
        Assert.Equal(Enumerable.Range(open.Min(), open.Count), open.Order()); // no event missing between its first row and its last
        Assert.True(open.Count <= IncidentWriter.MaxEventRows); // while open, the first part only

        mgr.Finish(w.History, w.Now);
        var (rows, latest) = EventPidsByEnd(inc.Dir);
        // The first part is exactly the window's first 500 events from its start as measured at the end, every one, in
        // order. The onset is where the growth first passed 5% of its rise, so a long, steady growth's onset is measured
        // later as its rise grows: here about 4 minutes later by the end than when the incident opened, so the window as
        // the report gives it starts past all of the 500 events from where it started then (about 3 minutes at the
        // owner's rate: a first part left there would hold none of the window's events). The incident's first part
        // followed the start, at most every 10 minutes while open: the onset is measured only within the raw history (70
        // minutes; once the history no longer reaches it, it stays as last measured), so the start is never more than 72
        // minutes back when it last moves, and 82 when the first part follows it, which the 90-minute buffer holds: every
        // follow was exact (here the start is over 90 minutes back at the end of the run). Then the latest 500, to the
        // window's last event (its end: a minute after the growth's end as last measured, or after its last rise).
        long windowFrom = IncidentManager.EventWindowStartMs(inc);
        Assert.True(w.History.Ms[^1] - windowFrom > 90 * 60_000, $"the window starts {(w.History.Ms[^1] - windowFrom) / 60_000} minutes back"); // past the buffer now
        int firstOfWindow = FirstFrom(windowFrom);
        Assert.True(firstOfWindow > FirstFrom(opened!.Value) + IncidentWriter.MaxEventRows, $"the window starts at {firstOfWindow} now, past the first 500 from {FirstFrom(opened.Value)}");
        Assert.Equal(Enumerable.Range(firstOfWindow, IncidentWriter.MaxEventRows), rows);
        int lastOfWindow = sampled.Where(s => s.Ms <= IncidentManager.EventWindowEndMs(inc, w.History.Ms[^1])).Max(s => s.LastPid);
        Assert.Equal(Enumerable.Range(lastOfWindow - IncidentWriter.MaxEventRows + 1, IncidentWriter.MaxEventRows), latest);
        var lines = File.ReadAllLines(Path.Combine(inc.Dir, "events.csv"));
        Assert.DoesNotContain(lines, l => l.Contains(",Note,"));
        // The Gap row counts every event between the first part's last and the latest part's first, once, and only the
        // window's: it is at the time of the first of them, after the window's start, and says what the file keeps and
        // from where.
        var c = System.Globalization.CultureInfo.InvariantCulture;
        static string T(DateTime t) => PoolWatch.Core.Util.Csv.Escape(t);
        var gap = PoolWatch.Core.Util.Csv.Split(Assert.Single(lines, l => l.Contains(",Gap,")));
        var firstLeftOut = new DateTime(MsOf(rows[^1] + 1) * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        var lastLeftOut = new DateTime(MsOf(latest[0] - 1) * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        Assert.Equal(firstLeftOut, DateTime.Parse(gap[0], c, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime());
        Assert.True(firstLeftOut > inc.OnsetUtc.AddMinutes(-2)); // the window's own
        Assert.Equal(string.Create(c, $"{latest[0] - rows[^1] - 1:N0} events left out, between {T(firstLeftOut)} and {T(lastLeftOut)} ") +
                     $"(events.csv keeps the first 500 events from {T(inc.OnsetUtc.AddMinutes(-2))}, where the incident's window started when this file was written, " +
                     "and the latest 500 up to the window's end)", gap[9]);
        Assert.Equal($"(1000 other events in events.csv; it leaves out {latest[0] - rows[^1] - 1:N0} events, between {Fmt.Clock(firstLeftOut)} and {Fmt.Clock(lastLeftOut)}, as its Gap row says)",
            File.ReadAllLines(Path.Combine(inc.Dir, "report.md")).Single(l => l.StartsWith('(') && l.Contains(" other events in ")));
    }

    /// <summary>
    /// A growth incident on a PC that records <paramref name="perSample"/> events every 10 s sample, the write budget with no
    /// room from its opening on: the growth, the 10 quiet minutes to its close, 6 hours, a little more (it leaves memory).
    /// <paramref name="closed"/> is called once, the sample it closes. <c>Sampled</c>: each sample's first PID, how many events it
    /// had and the moment they are read at (they are timed after it, so all at it). <paramref name="late"/>: events that
    /// come with the sample after its close (late ones of its window), the write for a window's late events then not
    /// being due before the incident leaves memory (its margin is set to 7 hours).
    /// </summary>
    private (Incident Inc, IncidentManager Mgr, List<(int FirstPid, int Count, long Ms)> Sampled) NoRoomUntilItLeaves(int perSample, Action<Incident> closed,
        Func<Incident, LifecycleEvent[]>? late = null)
    {
        var sampled = new List<(int FirstPid, int Count, long Ms)>();
        Directory.CreateDirectory(_dir);
        var host = new CountingHost(_dir);
        var w = new World { Interval = TimeSpan.FromSeconds(10) };
        var settings = new Settings { GrowthRules = [new(10, 50 * Settings.MB)] };
        var mgr = new IncidentManager(settings, new TagKnowledge(null, null), host, w.Registry);
        if (late is not null) mgr.LateEventMargin = TimeSpan.FromHours(7);
        var det = new Detector(settings);
        w.AddProcess(4, 0, "System");
        w.AddProcess(100, 4, "node.exe");
        long tag = 100L << 20;
        int pid = 500_000;
        Incident? inc = null;
        bool wasClosed = false;
        for (int i = 0; i < 30 + 120 + 60 + 6 * 360 + 30; i++)
        {
            w.Io(100, other: 500);
            if (i is >= 30 and < 150) tag += 1L << 20;
            w.Sample(("T000", tag));
            var now = w.Now - w.Interval;
            if (i < 200) sampled.Add((pid, perSample, At(now).Ms));
            if (i < 200)
                mgr.AddEvents(Enumerable.Range(0, perSample).Select(k => new LifecycleEvent
                {
                    TimeUtc = now.AddMilliseconds(k * 300), Kind = LifecycleKind.Start, Pid = pid++, CreateTime = now.ToFileTimeUtc() + k, ParentPid = 100,
                    Image = "node.exe", CommandLine = "node build.js",
                }).ToList(), At(now));
            if (wasClosed && late is not null)
            {
                mgr.AddEvents(late(inc!), At(now));
                late = null;
            }
            mgr.Process(w.History, det, det.Evaluate(w.History), At(now));
            if (inc is null && mgr.Open.Count > 0)
            {
                inc = mgr.Open.Single();
                mgr.Budget = new WriteBudget(bytesPerHour: 0, burst: 1); // no room from now on
                mgr.Budget.Spend(1, At(now).Ms);
            }
            if (inc is { IsOpen: false } && !wasClosed) { wasClosed = true; closed(inc); }
        }
        Assert.NotNull(inc);
        Assert.Empty(mgr.Open);
        Assert.Empty(mgr.Closed); // it left memory
        return (inc!, mgr, sampled);
    }

    [Fact]
    public void AClosedIncidentsClosingWriteHoldsBothEndsWhateverTheBudget()
    {
        // The write budget never has room after the incident opened, and its window has over 3,000 events: its close (a
        // write that must be made) replaces its events.csv whole with both ends of its window; 6 hours after closing it
        // leaves memory, and its file is not written again. (Before both ends were kept, its close wrote the first rows,
        // the rest waited, and a last Note row written as it left memory said to when the file held the window's events.)
        string? atClose = null;
        var (inc, _, _) = NoRoomUntilItLeaves(26, i => atClose = File.ReadAllText(Path.Combine(i.Dir, "events.csv")));
        Assert.Equal(atClose, File.ReadAllText(Path.Combine(inc.Dir, "events.csv"))); // as its close wrote it
        var (first, latest) = EventPidsByEnd(inc.Dir);
        Assert.Equal(Enumerable.Range(first.Min(), first.Count), first);
        Assert.Equal(Enumerable.Range(latest.Min(), latest.Count), latest);
        Assert.Equal(IncidentWriter.MaxEventRows, latest.Count);
        Assert.True(first.Count is > 0 and <= IncidentWriter.MaxEventRows, $"{first.Count} rows");
        var lines = File.ReadAllLines(Path.Combine(inc.Dir, "events.csv"));
        Assert.DoesNotContain(lines, l => l.Contains(",Note,"));
        string said = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{latest[0] - first[^1] - 1:N0} events left out");
        Assert.Single(lines, l => l.Contains(",Gap,") && l.Contains(said));
    }

    [Fact]
    public void AShortClosedIncidentsClosingWriteGivesItsEventsCsvEveryRowWhateverTheBudget()
    {
        // 3 events a sample: its window (about 23 minutes) has under 500 events, so it keeps no latest part. The write budget
        // never has room after it opened. Its close, a write that must be made, gives its events.csv every event of its
        // window, from the window's start as its report gives it to its end, each once and in time order, with no Gap
        // row, and its report counts them; 6 hours after closing it leaves memory, and its file is as its close wrote it.
        // (Before, a closing write gave a file with its first part alone 100 rows like a write that can wait: the rest
        // waited for a budget that never had room, report.md said "events.csv gets the rest when PoolWatch's write budget
        // has room" for 6 hours, and a last Gap row written as the incident left memory said how many the file left out.
        // With room in the budget the rows came, and report.md went on saying they would.)
        string? atClose = null, lineAtClose = null;
        var (inc, _, sampled) = NoRoomUntilItLeaves(3, i => (atClose, lineAtClose) = (File.ReadAllText(Path.Combine(i.Dir, "events.csv")), EventsLine(i)));
        Assert.Equal(atClose, File.ReadAllText(Path.Combine(inc.Dir, "events.csv"))); // as its close wrote it
        var lines = File.ReadAllLines(Path.Combine(inc.Dir, "events.csv"));
        Assert.DoesNotContain(lines, l => l.Contains(",Gap,") || l.Contains(",Note,"));
        long from = IncidentManager.EventWindowStartMs(inc), to = IncidentManager.EventWindowEndMs(inc, 0);
        var window = sampled.Where(s => s.Ms >= from && s.Ms <= to).SelectMany(s => Enumerable.Range(s.FirstPid, s.Count)).ToList();
        Assert.InRange(window.Count, 300, IncidentWriter.MaxEventRows - 1);
        Assert.Equal(window, EventPids(inc.Dir)); // every one, in time order (a later event has a higher PID)
        Assert.Equal($"({window.Count} other events in events.csv)", lineAtClose);
        Assert.Equal(lineAtClose, EventsLine(inc));
    }

    [Fact]
    public void AClosedIncidentThatLeavesMemoryWithRowsNotWrittenSaysHowManyItsEventsCsvLeavesOut()
    {
        // The same incident, and three late events of its window that come the sample after its close (rollups of its
        // window's last minute), with no room in the write budget, and the write for a window's late events not due
        // before the incident leaves memory (its margin set past the 6 hours it is held: an incident also leaves before
        // that write when 20 more close after it). Its close gave its file every row it had; the late events' rows wait
        // for the budget, which never has room; as it leaves memory a last Gap row says how many of the window's events
        // its events.csv leaves out and between which times (the first and last of those not written). (Before, a Note
        // row said to when the file held them, and that the day's events.csv had the rest.)
        LifecycleEvent[] late = [];
        var (inc, _, sampled) = NoRoomUntilItLeaves(3, _ => { }, i => late = Enumerable.Range(0, 3).Select(k => new LifecycleEvent
        {
            TimeUtc = (i.EndUtc ?? i.LastRiseUtc).AddSeconds(5.5 + 10 * k), Kind = LifecycleKind.Rollup, Pid = 70_000 + k, Image = "git.exe", Count = 2,
            Detail = "bash.exe started 2, 2 exited, short-lived",
        }).ToArray());
        Assert.Equal(3, late.Length);
        var lines = File.ReadAllLines(Path.Combine(inc.Dir, "events.csv"));
        var last = PoolWatch.Core.Util.Csv.Split(lines[^1]);
        Assert.Equal("Gap", last[1]);
        // The row is at the time of the first event not written, and says how many and between which times.
        Assert.Equal((Iso(late[0].TimeUtc), $"3 events left out, between {Iso(late[0].TimeUtc)} and {Iso(late[2].TimeUtc)} " +
                      "(PoolWatch's write budget had no room for them before the incident left memory)"), (last[0], last[9]));
        Assert.DoesNotContain(lines, l => l.Contains(",Note,") || l.Contains(",Rollup,"));
        // Every event of the window is in the file or counted in its Gap row: the rows are those its close wrote.
        long from = IncidentManager.EventWindowStartMs(inc), to = IncidentManager.EventWindowEndMs(inc, 0);
        Assert.All(late, e => Assert.InRange(At(e.TimeUtc).Ms, from, to));
        var window = sampled.Where(s => s.Ms >= from && s.Ms <= to).SelectMany(s => Enumerable.Range(s.FirstPid, s.Count)).ToList();
        Assert.True(window.Count + 3 < IncidentWriter.MaxEventRows, $"{window.Count} written, 3 left out");
        Assert.Equal(window, EventPids(inc.Dir));
    }

    [Fact]
    public void AnIncidentCountsTheEventsTheBufferLetGoBeforeItLookedAtThemAsLeftOut()
    {
        // An incident the manager does not hold (as one that has left its memory) kept its 100 events; then the shared
        // buffer took in 25,000 more and let the oldest go, among them 5,000 events the incident had not looked at (an
        // incident the manager holds looks at every event before it goes:
        // AClosedIncidentTheManagerHoldsTakesEveryEventBeforeTheBufferLetsItGo...). When it looks again, its first part
        // ends where it was, the 5,000 are left out, and from the rest it keeps its window's latest 500: its events.csv
        // holds its 100, a Gap row for the 24,500 between (the 5,000 and the 19,500 before its latest), and its latest 500.
        // The times of the events that went are gone with them: the row's first time is the last event the incident looked
        // at, its last time the latest event left out. (Before both ends were kept, it kept none from that point on, and
        // its file ended with its 100 and a Note row.)
        var mgr = new IncidentManager(new Settings(), new TagKnowledge(null, null), new FakeHost(_dir), new ProcessRegistry());
        var t0 = new DateTime(2026, 11, 9, 22, 0, 0, DateTimeKind.Utc);
        LifecycleEvent E(int i) => new() { TimeUtc = t0.AddSeconds(i * 0.1), Kind = LifecycleKind.Start, Pid = 1000 + i, CreateTime = 1 + i, Image = "x.exe" };
        mgr.AddEvents(Enumerable.Range(0, 100).Select(E), At(t0));
        var inc = new Incident
        {
            Id = "x", Dir = Path.Combine(_dir, "x"), Tag = PoolTag.Parse("File"), Kind = IncidentKind.Growth, OnsetUtc = t0, OnsetMs = At(t0).Ms,
            EndUtc = t0.AddHours(1), EndMs = At(t0.AddHours(1)).Ms, ClosedUtc = t0.AddMinutes(1), ClosedMs = At(t0.AddMinutes(1)).Ms,
        };
        var first = mgr.IncidentEvents(inc, At(t0).Ms);
        Assert.Equal(100, first.Kept.Count);
        Assert.Null(first.CutMs);
        mgr.AddEvents(Enumerable.Range(100, 25_000).Select(E), At(t0.AddMinutes(10)));
        var again = mgr.IncidentEvents(inc, At(t0.AddMinutes(10)).Ms);
        Assert.Equal(100, again.Kept.Count); // its first part ends where it was
        Assert.NotNull(again.CutMs);
        Assert.Equal(Enumerable.Range(1000 + 24_600, 500), again.Tail.Select(e => e.Pid)); // its latest 500
        Assert.Equal(24_500, again.Gap.Count);
        Assert.Equal(E(99).TimeUtc, again.Gap.FromUtc);
        Assert.Equal(E(24_599).TimeUtc, again.Gap.ToUtc);
        Assert.True(again.Window.Count > 1100); // the reports still have the window's events the buffer holds

        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        writer.Write(new History(), inc, again);
        var (head, latest) = EventPidsByEnd(inc.Dir);
        Assert.Equal(Enumerable.Range(1000, 100), head);
        Assert.Equal(Enumerable.Range(1000 + 24_600, 500), latest);
        var lines = File.ReadAllLines(Path.Combine(inc.Dir, "events.csv"));
        Assert.Single(lines, l => l.Contains(",Gap,") && l.Contains("24,500 events left out"));
        Assert.DoesNotContain(lines, l => l.Contains(",Note,"));
    }

    /// <summary>
    /// A closed incident within its hour (it can still re-open) that the manager's buffer has given 10,201 events 0.1 s
    /// apart from 22:00:00, event i with PID 1000 + i. Its window runs from 22:00:00 (2 minutes before its onset) to
    /// 22:07:00 (a minute after its last rise): events 0 to 4,200, and 6,000 events after its end. It keeps its first
    /// 500 and its latest 500 up to the end (3,701 to 4,200), counts the 3,201 between as left out, and of those after
    /// its end holds the latest 4,000 (6,201 to 10,200) and sets the earlier 2,000 aside, counted (4,201 to 6,200).
    /// <paramref name="ev"/>: the events, when they are not <see cref="Ev"/>'s (in the same order, by the same numbers).
    /// </summary>
    private (IncidentManager Mgr, Incident Inc) SetAsideWorld(Func<int, LifecycleEvent>? ev = null)
    {
        var mgr = new IncidentManager(new Settings(), new TagKnowledge(null, null), new FakeHost(_dir), new ProcessRegistry());
        var inc = new Incident
        {
            Id = "x", Dir = Path.Combine(_dir, "x"), Tag = PoolTag.Parse("File"), Kind = IncidentKind.Growth,
            OnsetUtc = Start.AddMinutes(2), OnsetMs = At(Start.AddMinutes(2)).Ms, LastRiseUtc = Start.AddMinutes(6), LastRiseMs = At(Start.AddMinutes(6)).Ms,
            ClosedUtc = Start.AddMinutes(16), ClosedMs = At(Start.AddMinutes(16)).Ms,
        };
        var now = At(Start.AddMinutes(18));
        mgr.AddEvents(Enumerable.Range(0, 10_201).Select(ev ?? Ev), now);
        var before = mgr.IncidentEvents(inc, now.Ms, forReports: false);
        Assert.Equal(Enumerable.Range(1000 + 3_701, 500), before.Tail.Select(e => e.Pid)); // the latest 500 to its end
        Assert.Equal(3_201, before.Gap.Count);
        return (mgr, inc);
    }

    /// <summary>The incident's window ends at event <paramref name="i"/>'s time from here on (its growth's end is measured a minute before it).</summary>
    private static void EndAt(Incident inc, int i) => (inc.EndUtc, inc.EndMs) = (Ev(i).TimeUtc.AddMinutes(-1), At(Ev(i).TimeUtc).Ms - 60_000);

    [Theory]
    [InlineData(true, 6_500)]
    [InlineData(false, 6_500)]
    [InlineData(false, 5_700)]
    [InlineData(false, 5_300)]
    [InlineData(false, 4_700)]
    public void ALatestPartAfterEventsSetAsideHasNoHole(bool held, int endAt)
    {
        // A closed incident within its hour holds the events after its window's end (at most 4,000; the earliest of more are
        // set aside and counted). Its window's end is then measured later: at event 6,500 (past the 2,000 set aside, among
        // the 4,000 kept after them), 5,700 or 4,700 (among those set aside). While PoolWatch still holds every event
        // (held), the incident takes its latest part and its counts again from them: the latest 500 up to the end, and a
        // count of exactly the events between its two parts. Not held: 15,000 more events came, and the buffer, which
        // keeps 20,000, has let go of events 0 to 5,200, 1,000 of those set aside among them. Those 1,000 stay counted
        // (their own times went with them), up to event 5,200, with every event before them, and the events PoolWatch
        // still holds, 5,201 on, are taken again by their own times:
        // - the end at 6,500 or 5,700 (after every event the buffer let go; 19,500 events have come since 5,700): the
        //   latest 500 up to the end, and a Gap row of exactly the events between the two parts, all of them the window's;
        // - the end at 5,300 (100 events after the last one the buffer let go): the latest part is those 100, which the
        //   Gap row says ("and the latest 100 up to the window's end"), after the same count, all of it the window's;
        // - the end at 4,700 (the buffer has let go of the 500 events after it: over 20,000 have come since): no event up
        //   to the end is held after the first part, and the count of those let go stays as it was made, to event 5,200.
        //   The Gap row and report.md say that it runs past the window's end, and where the window ends.
        // The latest part has no hole in any of them: the Gap row's range holds no row of the file. (Before, nothing was
        // taken again once the buffer had let go of any event set aside: the end at 6,500 gave the 300 events after those
        // set aside for a latest part while PoolWatch held the latest 500, and the end at 5,700 gave no latest part and
        // "5,701 events left out", to event 6,200, said to run past the window's end because "PoolWatch no longer held
        // those events", which it held, every one from after that end.)
        var (mgr, inc) = SetAsideWorld();
        var now = At(Start.AddMinutes(18));
        if (!held)
        {
            // 15,000 more events: the buffer lets go of its oldest 5,201 (events 0 to 5,200), 1,000 of those set aside among them.
            now = At(Start.AddMinutes(43));
            mgr.AddEvents(Enumerable.Range(10_201, 15_000).Select(Ev), now);
            Assert.Equal(20_000, mgr.EventCount);
            Assert.Equal(1000 + 5_201, mgr.EventsBetween(long.MinValue, long.MaxValue)[0].Pid);
        }
        int heldFrom = held ? 0 : 5_201; // the earliest event PoolWatch holds
        EndAt(inc, endAt);
        var after = mgr.IncidentEvents(inc, now.Ms, forReports: false);
        // The latest 500 up to the end of those PoolWatch holds, and every event between the first part and them left out;
        // with none held up to the end, those let go stay counted to the last of them.
        int latestFrom = Math.Max(endAt - 499, heldFrom), latestCount = Math.Max(0, endAt - latestFrom + 1);
        int lastLeft = latestCount > 0 ? latestFrom - 1 : heldFrom - 1;
        Assert.Equal(Enumerable.Range(1000 + latestFrom, latestCount), after.Tail.Select(e => e.Pid));
        Assert.Equal(lastLeft - 499, after.Gap.Count); // 500 to the last one left out, each once
        Assert.Equal((Ev(500).TimeUtc, Ev(lastLeft).TimeUtc), (after.Gap.FromUtc, after.Gap.ToUtc));
        bool past = lastLeft > endAt; // the count runs past the window's end
        Assert.Equal((held, endAt) is (false, 4_700), past);
        Assert.Equal(Ev(endAt).TimeUtc, after.EndUtc);

        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        writer.Write(new History(), inc, after);
        var (head, latest) = EventPidsByEnd(inc.Dir);
        Assert.Equal(Enumerable.Range(1000, 500), head);
        Assert.Equal(Enumerable.Range(1000 + latestFrom, latestCount), latest);
        var c = System.Globalization.CultureInfo.InvariantCulture;
        var gap = PoolWatch.Core.Util.Csv.Split(Assert.Single(File.ReadAllLines(Path.Combine(inc.Dir, "events.csv")), l => l.Contains(",Gap,")));
        string keeps = $"events.csv keeps the first 500 events from {Iso(Start)}{WhenWritten}";
        string runsPast = "the count runs past the window's end, {0} when {1} file was written: PoolWatch no longer held those events, to count each by its own time against that end";
        Assert.Equal(string.Create(c, $"{lastLeft - 499:N0} events left out, between {Iso(Ev(500).TimeUtc)} and {Iso(Ev(lastLeft).TimeUtc)} ") +
                     (past ? $"({keeps}; {string.Format(c, runsPast, Iso(Ev(endAt).TimeUtc), "this")})" : string.Create(c, $"({keeps}, and the latest {latestCount:N0} up to the window's end)")),
            gap[9]);
        Assert.Equal($"({500 + latestCount} other events in events.csv; it leaves out {lastLeft - 499:N0} events, between {Fmt.Clock(Ev(500).TimeUtc)} and {Fmt.Clock(Ev(lastLeft).TimeUtc)}, as its Gap row says" +
                     (past ? "; " + string.Format(c, runsPast, Fmt.Clock(Ev(endAt).TimeUtc), "the") : "") + ")", EventsLine(inc));
    }

    [Fact]
    public void TheLatestPartsEventsTheBufferLetGoStayWhenItIsTakenAgain()
    {
        // The same incident. 13,900 more events come: the buffer lets go of events 0 to 4,100, every one the incident
        // left out and 400 of its latest part (3,701 to 4,100), none of those it set aside. Its window's end is then
        // measured at event 6,500, among the events it kept after those set aside, and it takes its latest part again
        // from the events PoolWatch holds, 4,101 on. The 400 the buffer no longer holds are the incident's own still:
        // they come before every event taken again, and are counted as left out with those: the latest 500 up to the
        // end, and a Gap row of every event between the two parts, each once. (Dropped with the rest of the latest part,
        // they would be in no row and no count: "5,101 events left out" between rows 5,501 events apart.)
        var (mgr, inc) = SetAsideWorld();
        var now = At(Start.AddMinutes(41));
        mgr.AddEvents(Enumerable.Range(10_201, 13_900).Select(Ev), now);
        Assert.Equal(1000 + 4_101, mgr.EventsBetween(long.MinValue, long.MaxValue)[0].Pid);
        EndAt(inc, 6_500);
        var after = mgr.IncidentEvents(inc, now.Ms, forReports: false);
        Assert.Equal(Enumerable.Range(1000 + 6_001, 500), after.Tail.Select(e => e.Pid));
        Assert.Equal(5_501, after.Gap.Count); // 500 to 6,000
        Assert.Equal((Ev(500).TimeUtc, Ev(6_000).TimeUtc), (after.Gap.FromUtc, after.Gap.ToUtc));
    }

    [Fact]
    public void ALateEventTimedBeforeWhatTheBufferLetGoIsCountedOnceWhenTheLatestPartIsTakenAgain()
    {
        // The same again, with a late event (a rollup, timed at its first start) that comes with the 13,899 other events,
        // timed between events 600 and 601: among those the incident left out, which the buffer has let go by then. The
        // incident counts it with them. When its window's end is then measured at event 6,500 and it takes its latest
        // part again, what it left out at or before the latest event the buffer let go stays counted as it is, the late
        // one among it: the buffer still gives that event (it came late), and it is not counted a second time.
        var (mgr, inc) = SetAsideWorld();
        var now = At(Start.AddMinutes(41));
        var late = new LifecycleEvent { TimeUtc = Start.AddSeconds(60.05), Kind = LifecycleKind.Rollup, Pid = 90_001, CreateTime = 1, Image = "bash.exe" };
        mgr.AddEvents(Enumerable.Range(10_201, 13_899).Select(Ev).Append(late), now);
        var buffer = mgr.EventsBetween(long.MinValue, long.MaxValue);
        Assert.Equal(20_000, buffer.Count);
        Assert.Equal((late.Pid, 1000 + 4_101), (buffer[0].Pid, buffer[1].Pid)); // it holds the late one, and events 4,101 on
        Assert.Equal(3_202, mgr.IncidentEvents(inc, now.Ms, forReports: false).Gap.Count); // counted with those left out
        EndAt(inc, 6_500);
        var after = mgr.IncidentEvents(inc, now.Ms, forReports: false);
        Assert.Equal(Enumerable.Range(1000 + 6_001, 500), after.Tail.Select(e => e.Pid));
        Assert.Equal(5_502, after.Gap.Count); // events 500 to 6,000 and the late one, each once
        Assert.Equal((Ev(500).TimeUtc, Ev(6_000).TimeUtc), (after.Gap.FromUtc, after.Gap.ToUtc));
    }

    [Fact]
    public void TwoEventsOfOneMomentOneLeftOutAndOneKeptAreEachCountedOnceWhenTheLatestPartIsTakenAgain()
    {
        // The same incident, but its events 3,700 and 3,701 are of one millisecond: the last event it left out and the
        // first of its latest part. 11,800 more events come, and the buffer lets go of events 0 to 2,000: part of those
        // it left out. When its window's end is measured at event 6,500 and it takes its latest part again, those it
        // left out up to event 2,000 stay counted, and the count of them is made from the events the buffer still
        // gives between event 2,000's time and the last one left out: event 3,701, at that last one's very moment, is
        // the latest part's, not one of them. Every event between the two parts is counted once. (Counted with those
        // left out, it took one from the count that stays: "5,500 events left out" between rows 5,501 events apart.)
        LifecycleEvent Tied(int i) => i == 3_701 ? Ev(i) with { TimeUtc = Ev(3_700).TimeUtc } : Ev(i);
        var (mgr, inc) = SetAsideWorld(Tied);
        var now = At(Start.AddMinutes(38));
        mgr.AddEvents(Enumerable.Range(10_201, 11_800).Select(Ev), now);
        Assert.Equal(1000 + 2_001, mgr.EventsBetween(long.MinValue, long.MaxValue)[0].Pid); // it holds events 2,001 on
        EndAt(inc, 6_500);
        var after = mgr.IncidentEvents(inc, now.Ms, forReports: false);
        Assert.Equal(Enumerable.Range(1000 + 6_001, 500), after.Tail.Select(e => e.Pid));
        Assert.Equal(5_501, after.Gap.Count); // 500 to 6,000
        Assert.Equal((Ev(500).TimeUtc, Ev(6_000).TimeUtc), (after.Gap.FromUtc, after.Gap.ToUtc));
    }

    [Fact]
    public void ALateEventAmongThoseLeftOutIsCountedWithThemSoTheLatestPartFollowsThem()
    {
        // An open incident's latest part has let go of its earliest events (counted). A late event (a rollup timed at its first
        // start) then falls among those: it is counted with them, not held at the front of the latest part. Its window's end is
        // then measured earlier than the latest part's slack covers, so the latest part is fewer than 500: it still starts
        // after the last event left out, and the Gap row's range holds none of its events.
        var mgr = new IncidentManager(new Settings(), new TagKnowledge(null, null), new FakeHost(_dir), new ProcessRegistry());
        var t0 = new DateTime(2026, 11, 9, 22, 0, 0, DateTimeKind.Utc);
        LifecycleEvent E(int i) => new() { TimeUtc = t0.AddSeconds(i * 0.1), Kind = LifecycleKind.Start, Pid = 1000 + i, CreateTime = 1 + i, Image = "x.exe" };
        var inc = new Incident
        {
            Id = "x", Dir = Path.Combine(_dir, "x"), Tag = PoolTag.Parse("File"), Kind = IncidentKind.Growth,
            OnsetUtc = t0.AddMinutes(2), OnsetMs = At(t0.AddMinutes(2)).Ms, LastRiseUtc = t0.AddHours(1), LastRiseMs = At(t0.AddHours(1)).Ms,
        };
        Assert.True(inc.IsOpen);
        mgr.AddEvents(Enumerable.Range(0, 3_000).Select(E), At(t0.AddMinutes(5)));
        var first = mgr.IncidentEvents(inc, At(t0.AddMinutes(5)).Ms, forReports: false);
        Assert.Equal(500, first.Kept.Count);
        // Its latest part held 2,500 (events 500 to 2,999) and let go of the earliest 1,500 (500 to 1,999), keeping the latest
        // 500 and 500 more while it is open; the latest 500 are the window's latest part.
        Assert.Equal(Enumerable.Range(1000 + 2_500, 500), first.Tail.Select(e => e.Pid));
        // Late: timed among those it let go (between events 1,200 and 1,201).
        var late = new LifecycleEvent { TimeUtc = t0.AddSeconds(120.05), Kind = LifecycleKind.Rollup, Pid = 99_999, CreateTime = 1, Image = "bash.exe" };
        mgr.AddEvents([late], At(t0.AddMinutes(5).AddSeconds(10)));
        // Its end is measured at event 2,249's time: 250 of the latest part are in its window now.
        (inc.EndUtc, inc.EndMs) = (E(2_249).TimeUtc.AddMinutes(-1), At(E(2_249).TimeUtc).Ms - 60_000);
        var ev = mgr.IncidentEvents(inc, At(t0.AddMinutes(5).AddSeconds(10)).Ms, forReports: false);
        Assert.DoesNotContain(ev.Tail, e => e.Pid == late.Pid);
        Assert.All(ev.Tail, e => Assert.True(e.Ms > ev.Gap.ToMs, $"{e.Pid} at {e.Ms} inside the Gap row's range (to {ev.Gap.ToMs})"));
        Assert.Equal(Enumerable.Range(1000 + 2_000, 250), ev.Tail.Select(e => e.Pid));
        Assert.Equal(1_501, ev.Gap.Count); // the 1,500 it let go, and the late one counted with them
        Assert.Equal(E(1_999).TimeUtc, ev.Gap.ToUtc);
    }

    /// <summary>The events 0.1 s apart from 22:00:00 these tests read: event i has PID 1000 + i (an event before 22:00:00 has a number below 0).</summary>
    private static readonly DateTime Start = new(2026, 11, 9, 22, 0, 0, DateTimeKind.Utc);
    private static LifecycleEvent Ev(int i) => new() { TimeUtc = Start.AddSeconds(i * 0.1), Kind = LifecycleKind.Start, Pid = 1000 + i, CreateTime = 100_000 + i, Image = "x.exe" };
    private static string Iso(DateTime t) => PoolWatch.Core.Util.Csv.Escape(t);

    /// <summary>report.md's line under the window's events ("(N other events ..."), or "" when it has none.</summary>
    private static string EventsLine(Incident inc) =>
        File.ReadAllLines(Path.Combine(inc.Dir, "report.md")).FirstOrDefault(l => l.StartsWith('(') && l.Contains(" other events in ")) ?? "";

    /// <summary>What the close does with an open incident's file, as its Note row and report.md say it.</summary>
    private const string AtClose = "when the incident closes PoolWatch replaces the file with both ends of its window as it is measured then, its first events and its latest, " +
                                   "up to 500 of each, with a Gap row between them when it leaves any out";

    /// <summary>The same, of a file that has no Note row yet (its incident may close with no more events than its first part holds).</summary>
    private const string AtCloseWithMore = "when the incident closes with more events than that, PoolWatch replaces the file with both ends of its window as it is measured then, its first events and its latest, " +
                                           "up to 500 of each, with a Gap row between them when it leaves any out";

    /// <summary>What a Gap row says of a first part that starts where the incident's window started when its file was written.</summary>
    private const string WhenWritten = ", where the incident's window started when this file was written";

    private Incident GrowthIncident(string id, double onsetAt = 120) => new()
    {
        Id = id, Dir = Path.Combine(_dir, id), Tag = PoolTag.Parse("File"), Kind = IncidentKind.Growth,
        OnsetUtc = Start.AddSeconds(onsetAt), OnsetMs = At(Start.AddSeconds(onsetAt)).Ms, LastRiseUtc = Start.AddHours(1), LastRiseMs = At(Start.AddHours(1)).Ms,
    };

    private static void SetOnset(Incident inc, double at) => (inc.OnsetUtc, inc.OnsetMs) = (Start.AddSeconds(at), At(Start.AddSeconds(at)).Ms);

    /// <summary>The incident closes with event <paramref name="last"/> its window's last, <paramref name="minutes"/> after 22:00:00.</summary>
    private static void CloseWith(Incident inc, int last, double minutes)
    {
        (inc.EndUtc, inc.EndMs) = (Ev(last).TimeUtc.AddMinutes(-1), At(Ev(last).TimeUtc).Ms - 60_000);
        (inc.ClosedUtc, inc.ClosedMs) = (Start.AddMinutes(minutes), At(Start.AddMinutes(minutes)).Ms);
    }

    /// <summary>The Gap row of an events.csv: its time and its detail.</summary>
    private static (string Time, string Detail) GapRow(Incident inc)
    {
        var gap = PoolWatch.Core.Util.Csv.Split(Assert.Single(File.ReadAllLines(Path.Combine(inc.Dir, "events.csv")), l => l.Contains(",Gap,")));
        return (gap[0], gap[9]);
    }

    [Fact]
    public void TheFirstPartFollowsTheWindowsStartWhileTheBufferHoldsTheEventsFromIt()
    {
        // Incidents kept the first 500 events of their window (from 22:00:00, event 0) and their latest part. Their onset
        // is then measured 100 s later (a long, steady growth's is, as its rise grows): their window starts at event
        // 1,000 now, past every one of the first 500. A person reads the report's window and expects events.csv to show
        // it, so the first part follows the start. While open, at most every 10 minutes (each follow takes the whole
        // buffer again, and a long growth's onset moves every few minutes): not at once, not a minute on. One closes: its
        // first part follows at once. The other, 10 minutes on, follows too. The buffer holds every event from the new
        // start, so each first part is exactly the window's first 500 from it (1,000 to 1,499), the latest part is as it
        // was, and what is left out is the window's events between them alone (1,500 to 2,499), none from before it. The
        // closed one's events.csv holds those, and its Gap row and its report say so, by the times they give. (With the
        // first part left where the incident opened, it kept 0 to 499, all from before the growth as last measured: on
        // average 416 of the 500 after an hour at the owner's rate, BothEndsTests.)
        var mgr = new IncidentManager(new Settings(), new TagKnowledge(null, null), new FakeHost(_dir), new ProcessRegistry());
        var (inc, other, back) = (GrowthIncident("x"), GrowthIncident("y"), GrowthIncident("z", onsetAt: 180));
        var now = At(Start.AddMinutes(5));
        mgr.AddEvents(Enumerable.Range(0, 3_000).Select(Ev), now);
        foreach (var i in new[] { inc, other })
        {
            var first = mgr.IncidentEvents(i, now.Ms, forReports: false);
            Assert.Equal(Enumerable.Range(1000, 500), first.Kept.Select(e => e.Pid));
            Assert.Equal(Enumerable.Range(1000 + 2_500, 500), first.Tail.Select(e => e.Pid));
            Assert.Equal((Start, (DateTime?)null, 3_000L), (first.FromUtc, first.LostToUtc, first.TakenIn));
            SetOnset(i, 220); // the window starts at event 1,000's time
            foreach (int seconds in new[] { 0, 60, 599 })
                Assert.Equal(Enumerable.Range(1000, 500), mgr.IncidentEvents(i, now.Ms + seconds * 1000, forReports: false).Kept.Select(e => e.Pid)); // open: not yet
        }
        void Followed(IncidentEventSet set)
        {
            Assert.Equal(Enumerable.Range(1000 + 1_000, 500), set.Kept.Select(e => e.Pid));
            Assert.Equal(Enumerable.Range(1000 + 2_500, 500), set.Tail.Select(e => e.Pid));
            Assert.Equal((1_000, Ev(1_500).TimeUtc, Ev(2_499).TimeUtc), (set.Gap.Count, set.Gap.FromUtc, set.Gap.ToUtc));
            Assert.Equal((Ev(1_000).TimeUtc, (DateTime?)null), (set.FromUtc, set.LostToUtc));
            // Every event it took in is still counted once: the 2,000 from the new start on, and the 1,000 before it.
            Assert.Equal((3_000L, 1_000L), (set.TakenIn, set.Carried));
        }
        Followed(mgr.IncidentEvents(other, now.Ms + (long)IncidentManager.FollowEvery.TotalMilliseconds, forReports: false));
        Assert.Equal(1_000, mgr.LatestHeld(other)); // its latest 500 and 500 more while it is open, as before: taken again, not held twice

        // Closed, its window ending with event 2,999: at once, and its events.csv holds both ends of the window as measured now.
        CloseWith(inc, 2_999, 6);
        var ev = mgr.IncidentEvents(inc, At(Start.AddMinutes(6)).Ms);
        Followed(ev);
        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        writer.Write(new History(), inc, ev);
        var (head, latest) = EventPidsByEnd(inc.Dir);
        Assert.Equal(Enumerable.Range(1000 + 1_000, 500), head);
        Assert.Equal(Enumerable.Range(1000 + 2_500, 500), latest);
        Assert.Equal((Iso(Ev(1_500).TimeUtc), $"1,000 events left out, between {Iso(Ev(1_500).TimeUtc)} and {Iso(Ev(2_499).TimeUtc)} " +
                      $"(events.csv keeps the first 500 events from {Iso(Ev(1_000).TimeUtc)}{WhenWritten}, and the latest 500 up to the window's end)"), GapRow(inc));
        Assert.Equal($"(1000 other events in events.csv; it leaves out {1000:N0} events, between {Fmt.Clock(Ev(1_500).TimeUtc)} and {Fmt.Clock(Ev(2_499).TimeUtc)}, as its Gap row says)", EventsLine(inc));

        // Measured earlier: an incident whose window started at event 600's time kept 600 to 1,099; its onset is then
        // measured a minute earlier, the window from event 0's time. The buffer holds every event from there: the first
        // part is 0 to 499, and the events from 500 on are counted again (the 600 it had passed over taken in).
        var before = mgr.IncidentEvents(back, now.Ms, forReports: false);
        Assert.Equal(Enumerable.Range(1000 + 600, 500), before.Kept.Select(e => e.Pid));
        Assert.Equal(2_400L, before.TakenIn);
        SetOnset(back, 120);
        CloseWith(back, 2_999, 6);
        var earlier = mgr.IncidentEvents(back, At(Start.AddMinutes(6)).Ms, forReports: false);
        Assert.Equal(Enumerable.Range(1000, 500), earlier.Kept.Select(e => e.Pid));
        Assert.Equal(Enumerable.Range(1000 + 2_500, 500), earlier.Tail.Select(e => e.Pid));
        Assert.Equal((2_000, Ev(500).TimeUtc, Ev(2_499).TimeUtc), (earlier.Gap.Count, earlier.Gap.FromUtc, earlier.Gap.ToUtc));
        Assert.Equal((Start, (DateTime?)null, 2_400L, -600L), (earlier.FromUtc, earlier.LostToUtc, earlier.TakenIn, earlier.Carried));
    }

    [Fact]
    public void AnOpenIncidentsFileStartsAgainOnceItsFirstPartFollowedAndOneWithItsNoteRowStaysAsWritten()
    {
        // Two open incidents' events.csv: one the write budget let have 100 rows so far (events 0 to 99), one with all 500
        // of its first part and its Note row. Their onset is then measured 100 s later: the window starts at event 1,000
        // now, past every row they have. Until the first part follows (10 minutes on, or an export), the first file goes on
        // from its last row, as the first part it is written from has not changed, and report.md says where its rows are
        // from and where the window starts. Once the first part has followed (1,000 to 1,499), the file starts again from
        // the window's start: a write without rows leaves it as it is and the report says it starts again; a write of rows
        // writes it again with the first part's (1,000 to 1,099), not after its last row (which would leave 200 to 999 in
        // neither). The file with its Note row stays as written (it gets no rows until its close), and its report says how
        // many events it holds and from when, how many more have come since, and where the window starts now; it calls
        // them "the window's first" only while they are, and names no time twice when the start moved under a second.
        // (Before the first part followed, report.md said of such a file "it holds the window's first events, to
        // 22:00:50" of a window that started at 22:01:40.)
        var mgr = new IncidentManager(new Settings(), new TagKnowledge(null, null), new FakeHost(_dir), new ProcessRegistry());
        var (behind, noted) = (GrowthIncident("behind"), GrowthIncident("noted"));
        var now = At(Start.AddMinutes(5));
        mgr.AddEvents(Enumerable.Range(0, 3_000).Select(Ev), now);
        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        const string day = ", and the day's `data\\<yyyyMMdd>\\events.csv` has every one";
        string NowAt(double at) => $"; the incident's window now starts at {Fmt.Clock(Start.AddSeconds(at - 120))}";

        writer.Write(new History(), behind, mgr.IncidentEvents(behind, now.Ms), maxRows: 100);
        Assert.Equal(Enumerable.Range(1000, 100), EventPids(behind.Dir));
        Assert.Equal($"(3000 other events in the window; events.csv gets more of the window's first events, up to 500 in all, as PoolWatch's write budget allows, and {AtCloseWithMore}{day})", EventsLine(behind));
        writer.Write(new History(), noted, mgr.IncidentEvents(noted, now.Ms));
        string notedPath = Path.Combine(noted.Dir, "events.csv"), notedFile = File.ReadAllText(notedPath);
        var note = PoolWatch.Core.Util.Csv.Split(File.ReadAllLines(notedPath)[^1]);
        Assert.Equal(("Note", Iso(Ev(500).TimeUtc)), (note[1], note[0]));
        Assert.Equal($"events.csv holds the first 500 events from {Iso(Start)}, to this row's time, as PoolWatch had them when it wrote this row; later events are not in this file ({AtClose})", note[9]);
        string held = $"to {Fmt.Clock(Ev(500).TimeUtc)}, as PoolWatch had them when it wrote them; {2500:N0} more have come since, and {AtClose}";
        Assert.Equal($"(500 other events in events.csv: while the incident is open it holds the window's first 500 events, {held})", EventsLine(noted));

        // The onset is measured 100 s later. A minute on the first part has not followed: the first file goes on from its
        // last row, and each report says where its file's rows are from and where the window starts now.
        SetOnset(behind, 220);
        SetOnset(noted, 220);
        long soon = now.Ms + 60_000;
        writer.Write(new History(), behind, mgr.IncidentEvents(behind, soon), maxRows: 100);
        Assert.Equal(Enumerable.Range(1000, 200), EventPids(behind.Dir));
        Assert.Equal($"(2000 other events in the window; events.csv gets more of the first events from {Fmt.Clock(Start)}, up to 500 in all, as PoolWatch's write budget allows, " +
                     $"and {AtCloseWithMore}{day}{NowAt(220)}: the file's events from before then are not the window's)", EventsLine(behind));
        string Noted(long atMs)
        {
            writer.Write(new History(), noted, mgr.IncidentEvents(noted, atMs));
            writer.AppendEvents(noted, mgr.IncidentEvents(noted, atMs, forReports: false), maxRows: 10_000);
            Assert.Equal(notedFile, File.ReadAllText(notedPath)); // as written
            return EventsLine(noted);
        }
        string fromThere = $"(500 other events in events.csv: while the incident is open it holds the first 500 events from {Fmt.Clock(Start)}, {held}";
        Assert.Equal($"{fromThere}{NowAt(220)}: the file's events from before then are not the window's)", Noted(soon));
        // Measured a minute earlier instead, under a second later (one time as report.md writes them: it names it once), and
        // where it was.
        SetOnset(noted, 60);
        Assert.Equal($"{fromThere}{NowAt(60)}, and its events before {Fmt.Clock(Start)} are not in the file)", Noted(soon));
        SetOnset(noted, 120.4);
        Assert.Equal(fromThere + ")", Noted(soon));
        SetOnset(noted, 120);
        Assert.Equal($"(500 other events in events.csv: while the incident is open it holds the window's first 500 events, {held})", Noted(soon));
        SetOnset(noted, 220);

        // 10 minutes on the first part follows: 1,000 to 1,499, every event from the new start.
        long later = now.Ms + (long)IncidentManager.FollowEvery.TotalMilliseconds;
        Assert.Equal(Enumerable.Range(1000 + 1_000, 500), mgr.IncidentEvents(behind, later, forReports: false).Kept.Select(e => e.Pid));
        Assert.Equal(Enumerable.Range(1000 + 1_000, 500), mgr.IncidentEvents(noted, later, forReports: false).Kept.Select(e => e.Pid));
        // A report's refresh, without rows: the file is as it was, and the report says what it holds and that it starts again.
        writer.Write(new History(), behind, mgr.IncidentEvents(behind, later), maxRows: 0);
        Assert.Equal(Enumerable.Range(1000, 200), EventPids(behind.Dir));
        Assert.True(writer.EventsPending(behind));
        Assert.Equal($"(2000 other events in the window; events.csv starts again with the first events from {Fmt.Clock(Ev(1_000).TimeUtc)}, up to 500 in all, as PoolWatch's write budget allows, " +
                     $"and {AtCloseWithMore}{day}; it holds events from {Fmt.Clock(Start)} on{NowAt(220)}: the file's events from before then are not the window's)", EventsLine(behind));
        // A write of rows: from the window's start, no hole.
        writer.AppendEvents(behind, mgr.IncidentEvents(behind, later, forReports: false), maxRows: 100);
        Assert.Equal(Enumerable.Range(1000 + 1_000, 100), EventPids(behind.Dir));
        writer.Write(new History(), behind, mgr.IncidentEvents(behind, later), maxRows: 0);
        Assert.Equal($"(2000 other events in the window; events.csv gets more of the window's first events, up to 500 in all, as PoolWatch's write budget allows, and {AtCloseWithMore}{day})", EventsLine(behind));
        // The rest of it, and its Note row: 500 events from the window's start, to the first event after them.
        writer.AppendEvents(behind, mgr.IncidentEvents(behind, later, forReports: false), maxRows: 10_000);
        Assert.Equal(Enumerable.Range(1000 + 1_000, 500), EventPids(behind.Dir));
        note = PoolWatch.Core.Util.Csv.Split(File.ReadAllLines(Path.Combine(behind.Dir, "events.csv"))[^1]);
        Assert.Equal(("Note", Iso(Ev(1_500).TimeUtc)), (note[1], note[0]));
        Assert.Equal($"events.csv holds the first 500 events from {Iso(Ev(1_000).TimeUtc)}, to this row's time, as PoolWatch had them when it wrote this row; later events are not in this file ({AtClose})", note[9]);
        writer.Write(new History(), behind, mgr.IncidentEvents(behind, later));
        Assert.Equal($"(500 other events in events.csv: while the incident is open it holds the window's first 500 events, to {Fmt.Clock(Ev(1_500).TimeUtc)}, " +
                     $"as PoolWatch had them when it wrote them; {1500:N0} more have come since, and {AtClose})", EventsLine(behind));

        // The file with its Note row: as written, its 500 events from 22:00:00, and 2,500 more have come since (the 1,000
        // before the new start among them: the incident's count goes on across the follow).
        Assert.Equal($"{fromThere}{NowAt(220)}: the file's events from before then are not the window's)", Noted(later));
        // Its close replaces it with both ends of the window as it is measured then.
        CloseWith(noted, 2_999, 16);
        writer.Write(new History(), noted, mgr.IncidentEvents(noted, At(Start.AddMinutes(16)).Ms));
        var (head, latest) = EventPidsByEnd(noted.Dir);
        Assert.Equal(Enumerable.Range(1000 + 1_000, 500), head);
        Assert.Equal(Enumerable.Range(1000 + 2_500, 500), latest);
        Assert.DoesNotContain(File.ReadAllLines(notedPath), l => l.Contains(",Note,"));
        Assert.Equal($"(1000 other events in events.csv; it leaves out {1000:N0} events, between {Fmt.Clock(Ev(1_500).TimeUtc)} and {Fmt.Clock(Ev(2_499).TimeUtc)}, as its Gap row says)", EventsLine(noted));
    }

    /// <summary>
    /// An incident kept the first 500 events from 22:00:00 (events <paramref name="from"/> on; those before event 0 are from
    /// before its window) 4 minutes in; by 22:40 the shared buffer, which holds 20,000 events (a PC far busier than the
    /// owner's), has taken in events up to 21,999 and let go of every event before event 2,000. Returns the manager, the
    /// incident (open) and the moment.
    /// </summary>
    private (IncidentManager Mgr, Incident Inc, Moment Now) AfterTheBufferLetGoOfItsFirstEvents(string id, int from = 0)
    {
        var mgr = new IncidentManager(new Settings(), new TagKnowledge(null, null), new FakeHost(_dir), new ProcessRegistry());
        var inc = GrowthIncident(id);
        mgr.AddEvents(Enumerable.Range(from, 2_000 - from).Select(Ev), At(Start.AddMinutes(4)));
        Assert.Equal(Enumerable.Range(1000, 500), mgr.IncidentEvents(inc, At(Start.AddMinutes(4)).Ms, forReports: false).Kept.Select(e => e.Pid));
        var now = At(Start.AddMinutes(40));
        mgr.AddEvents(Enumerable.Range(2_000, 20_000).Select(Ev), now);
        Assert.Equal(20_000, mgr.EventCount);
        return (mgr, inc, now);
    }

    [Fact]
    public void WhenTheBufferNoLongerHoldsEveryEventFromTheNewStartTheFirstPartIsTheFirstPoolWatchStillHolds()
    {
        // On a PC busy enough that the buffer's 20,000 events are fewer than the window's start is back (over about
        // 14,600 events an hour), the buffer can no longer give every event from a start measured again. The first part
        // is then the first events PoolWatch still holds from the new start, and every text says which, by the times it
        // gives. Each incident here kept events 0 to 499 before the buffer let go of every event before event 2,000, and
        // is closed (it follows at once), its window's last event 21,999.
        (List<int> Head, IncidentEventSet Set, string Gap, string Md, Incident Inc) Closed(string id, double onsetAt, int from = 0)
        {
            var (mgr, inc, _) = AfterTheBufferLetGoOfItsFirstEvents(id, from);
            SetOnset(inc, onsetAt);
            CloseWith(inc, 21_999, 41);
            var ev = mgr.IncidentEvents(inc, At(Start.AddMinutes(41)).Ms);
            new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry()).Write(new History(), inc, ev);
            var (head, latest) = EventPidsByEnd(inc.Dir);
            Assert.Equal(Enumerable.Range(1000 + 21_500, 500), latest);
            Assert.Equal(head, ev.Kept.Select(e => e.Pid));
            return (head, ev, GapRow(inc).Detail, EventsLine(inc), inc);
        }
        string Left(int count, int first) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{count:N0} events left out, between {Iso(Ev(first).TimeUtc)} and {Iso(Ev(21_499).TimeUtc)} ");
        string Leaves(int rows, int count, int first) => $"({rows} other events in events.csv; it leaves out {count:N0} events, between {Fmt.Clock(Ev(first).TimeUtc)} and {Fmt.Clock(Ev(21_499).TimeUtc)}, as its Gap row says";
        const string latest500 = ", and the latest 500 up to the window's end)";

        // Measured 30 s later, the window from event 300's time: the incident's own first part holds events from there
        // on that the buffer has let go, earlier than anything the buffer could give. It keeps them, and lets go of the
        // 300 from before the window: its file holds the window's first 200 events exactly, and the Gap row counts the
        // window's events after them.
        var among = Closed("among", 150);
        Assert.Equal(Enumerable.Range(1000 + 300, 200), among.Head);
        Assert.Equal((Ev(300).TimeUtc, (DateTime?)null), (among.Set.FromUtc, among.Set.LostToUtc));
        Assert.Equal($"{Left(21_000, 500)}(events.csv keeps the first 200 events from {Iso(Ev(300).TimeUtc)}{WhenWritten}{latest500}", among.Gap);
        Assert.Equal(Leaves(700, 21_000, 500) + ")", among.Md);
        Assert.Equal((22_000L, 300L), (among.Set.TakenIn, among.Set.Carried)); // the 300 it let go still count as taken in

        // Measured 60 s later, the window from event 600's time, past its whole first part: it holds no event from
        // there, and the first part is taken again from the buffer, the first 500 it still holds (2,000 to 2,499). The
        // texts say so, with the time of the latest event PoolWatch had let go.
        var past = Closed("past", 180);
        Assert.Equal(Enumerable.Range(1000 + 2_000, 500), past.Head);
        Assert.Equal((Ev(600).TimeUtc, (DateTime?)Ev(1_999).TimeUtc), (past.Set.FromUtc, past.Set.LostToUtc));
        string held = $"PoolWatch still held from {Iso(Ev(600).TimeUtc)}{WhenWritten} (by then it had already let go of events up to {Iso(Ev(1_999).TimeUtc)})";
        Assert.Equal($"{Left(19_000, 2_500)}(events.csv keeps the first 500 events {held}{latest500}", past.Gap);
        Assert.Equal($"{Leaves(1000, 19_000, 2_500)}; it holds the events PoolWatch still held from {Fmt.Clock(Ev(600).TimeUtc)} " +
                     $"(by then it had already let go of events up to {Fmt.Clock(Ev(1_999).TimeUtc)}))", past.Md);

        // Measured a minute earlier, the window from 21:59:00, with no event known between there and 22:00:00: its own
        // first part is still the window's first 500 (and earlier than anything the buffer holds), exactly.
        var earlier = Closed("earlier", 60);
        Assert.Equal(Enumerable.Range(1000, 500), earlier.Head);
        Assert.Equal((Start.AddMinutes(-1), (DateTime?)null), (earlier.Set.FromUtc, earlier.Set.LostToUtc));
        Assert.Equal($"{Left(21_000, 500)}(events.csv keeps the first 500 events from {Iso(Start.AddMinutes(-1))}{WhenWritten}{latest500}", earlier.Gap);
        Assert.Equal(Leaves(1000, 21_000, 500) + ")", earlier.Md);

        // The same with events before 22:00:00 (from 21:59:30 on, the last of them at 21:59:59.9), which the incident
        // passed over and the buffer has let go since: the window's events from 21:59:40 to there are gone, its own first
        // part is the first PoolWatch still holds, and the texts say up to when it had let events go.
        var gone = Closed("gone", 100, from: -300);
        Assert.Equal(Enumerable.Range(1000, 500), gone.Head);
        Assert.Equal((Start.AddSeconds(-20), (DateTime?)Ev(-1).TimeUtc), (gone.Set.FromUtc, gone.Set.LostToUtc));
        held = $"PoolWatch still held from {Iso(Start.AddSeconds(-20))}{WhenWritten} (by then it had already let go of events up to {Iso(Ev(-1).TimeUtc)})";
        Assert.Equal($"{Left(21_000, 500)}(events.csv keeps the first 500 events {held}{latest500}", gone.Gap);
        Assert.Equal($"{Leaves(1000, 21_000, 500)}; it holds the events PoolWatch still held from {Fmt.Clock(Start.AddSeconds(-20))} " +
                     $"(by then it had already let go of events up to {Fmt.Clock(Ev(-1).TimeUtc)}))", gone.Md);

        // An incident whose first part the buffer still holds whole (it opened at 22:40 with its window from event 6,000's
        // time, the buffer holding events 2,000 on), measured earlier, the window from event 1,000's time: the buffer
        // holds earlier events of the window than the incident does, so the first part is taken again from it, the first
        // 500 it still holds.
        var (mgr, _, now) = AfterTheBufferLetGoOfItsFirstEvents("held");
        var late = GrowthIncident("late", onsetAt: 720);
        Assert.Equal(Enumerable.Range(1000 + 6_000, 500), mgr.IncidentEvents(late, now.Ms, forReports: false).Kept.Select(e => e.Pid));
        SetOnset(late, 220);
        CloseWith(late, 21_999, 41);
        var again = mgr.IncidentEvents(late, At(Start.AddMinutes(41)).Ms, forReports: false);
        Assert.Equal(Enumerable.Range(1000 + 2_000, 500), again.Kept.Select(e => e.Pid));
        Assert.Equal((Ev(1_000).TimeUtc, (DateTime?)Ev(1_999).TimeUtc), (again.FromUtc, again.LostToUtc));
    }

    [Fact]
    public void AFirstPartKeptThroughAStartMeasuredLaterAndThenEarlierSaysWhatPoolWatchLetGoOfByThen()
    {
        // The same busy PC. An incident opens at 22:40 with its window from event 1,000's time, further back than the
        // buffer reaches (it holds events 2,000 on): its first part is the first 500 PoolWatch still holds, 2,000 to
        // 2,499, after events let go up to 1,999. Then the buffer lets go of events up to 2,999, and the incident closes.
        var (mgr, _, now) = AfterTheBufferLetGoOfItsFirstEvents("buffer");
        var inc = GrowthIncident("kept", onsetAt: 220);
        var first = mgr.IncidentEvents(inc, now.Ms, forReports: false);
        Assert.Equal(Enumerable.Range(1000 + 2_000, 500), first.Kept.Select(e => e.Pid));
        Assert.Equal((Ev(1_000).TimeUtc, (DateTime?)Ev(1_999).TimeUtc), (first.FromUtc, first.LostToUtc));
        mgr.AddEvents(Enumerable.Range(22_000, 1_000).Select(Ev), At(Start.AddMinutes(42)));
        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        string left = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{20_000:N0} events left out, between {Iso(Ev(2_500).TimeUtc)} and {Iso(Ev(22_499).TimeUtc)} ");
        string leaves = $"(800 other events in events.csv; it leaves out {20_000:N0} events, between {Fmt.Clock(Ev(2_500).TimeUtc)} and {Fmt.Clock(Ev(22_499).TimeUtc)}, as its Gap row says";

        // Its start is measured later, the window from event 2,200's time: past everything PoolWatch had let go of when
        // the first part was taken, and its own first part holds the events from there on, which the buffer has let go
        // since. It keeps them and lets go of the 200 from before the window: they are the window's first 300 events
        // exactly, and no text says "still held" (before the new start, what it had let go of is not the window's).
        SetOnset(inc, 340);
        CloseWith(inc, 22_999, 43);
        var later = mgr.IncidentEvents(inc, At(Start.AddMinutes(43)).Ms);
        Assert.Equal(Enumerable.Range(1000 + 2_200, 300), later.Kept.Select(e => e.Pid));
        Assert.Equal((Ev(2_200).TimeUtc, (DateTime?)null), (later.FromUtc, later.LostToUtc));
        writer.Write(new History(), inc, later);
        var (head, latest) = EventPidsByEnd(inc.Dir);
        Assert.Equal(Enumerable.Range(1000 + 2_200, 300), head);
        Assert.Equal(Enumerable.Range(1000 + 22_500, 500), latest);
        Assert.Equal($"{left}(events.csv keeps the first 300 events from {Iso(Ev(2_200).TimeUtc)}{WhenWritten}, and the latest 500 up to the window's end)", GapRow(inc).Detail);
        Assert.Equal(leaves + ")", EventsLine(inc));

        // Then earlier again, the window from event 2,100's time: the events from there to 2,199 are the ones it let go
        // of at the start measured later, and the buffer no longer holds them either. Its first part is the first 300
        // events PoolWatch still holds from the new start, and the texts say up to when it had let events go: the
        // latest event before its first part that it knew of.
        SetOnset(inc, 330);
        var earlier = mgr.IncidentEvents(inc, At(Start.AddMinutes(43)).Ms);
        Assert.Equal(Enumerable.Range(1000 + 2_200, 300), earlier.Kept.Select(e => e.Pid));
        Assert.Equal((Ev(2_100).TimeUtc, (DateTime?)Ev(2_199).TimeUtc), (earlier.FromUtc, earlier.LostToUtc));
        writer.Write(new History(), inc, earlier);
        (head, latest) = EventPidsByEnd(inc.Dir);
        Assert.Equal(Enumerable.Range(1000 + 2_200, 300), head);
        Assert.Equal(Enumerable.Range(1000 + 22_500, 500), latest);
        Assert.Equal($"{left}(events.csv keeps the first 300 events PoolWatch still held from {Iso(Ev(2_100).TimeUtc)}{WhenWritten} " +
                     $"(by then it had already let go of events up to {Iso(Ev(2_199).TimeUtc)}), and the latest 500 up to the window's end)", GapRow(inc).Detail);
        Assert.Equal($"{leaves}; it holds the events PoolWatch still held from {Fmt.Clock(Ev(2_100).TimeUtc)} " +
                     $"(by then it had already let go of events up to {Fmt.Clock(Ev(2_199).TimeUtc)}))", EventsLine(inc));
    }

    [Fact]
    public void AnOpenIncidentsFileIsNeverLeftWithoutItsRowsWhenTheBufferNoLongerHoldsTheNewStart()
    {
        // The same busy PC, the incident kept open with 100 rows of its first part written (events 0 to 99), and its
        // start measured 60 s later, past its whole first part (the window from event 600's time), which the buffer no
        // longer holds. The first part follows (it was taken over 10 minutes ago): it is the first 500 events PoolWatch
        // still holds from the new start (2,000 on). A write without rows leaves every row of the file where it is,
        // with no Note row; a write of rows starts the file again from the first part, its texts saying which.
        // (Before, such a file was written again with no event at all and a Note row at the window's start, and
        // got nothing more until the close.)
        var (mgr, inc, now) = AfterTheBufferLetGoOfItsFirstEvents("open");
        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        string path = Path.Combine(inc.Dir, "events.csv");
        // Its first 100 rows, written before its start is measured again (the first part as it was).
        writer.Write(new History(), inc, mgr.IncidentEvents(inc, now.Ms), maxRows: 100);
        Assert.Equal(Enumerable.Range(1000, 100), EventPids(inc.Dir));
        SetOnset(inc, 180);
        var ev = mgr.IncidentEvents(inc, now.Ms);
        Assert.Equal(Enumerable.Range(1000 + 2_000, 500), ev.Kept.Select(e => e.Pid));
        Assert.Equal((Ev(600).TimeUtc, (DateTime?)Ev(1_999).TimeUtc), (ev.FromUtc, ev.LostToUtc));
        // A write without rows leaves every row where it is.
        writer.Write(new History(), inc, ev, maxRows: 0);
        Assert.Equal(Enumerable.Range(1000, 100), EventPids(inc.Dir));
        Assert.DoesNotContain(File.ReadAllLines(path), l => l.Contains(",Note,"));
        string heldMd = $"PoolWatch still held from {Fmt.Clock(Ev(600).TimeUtc)} (by then it had already let go of events up to {Fmt.Clock(Ev(1_999).TimeUtc)})";
        Assert.Equal($"(20000 other events in the window; events.csv starts again with the first events {heldMd}, up to 500 in all, as PoolWatch's write budget allows, " +
                     $"and {AtCloseWithMore}, and the day's `data\\<yyyyMMdd>\\events.csv` has every one; it holds events from {Fmt.Clock(Start)} on" +
                     $"; the incident's window now starts at {Fmt.Clock(Ev(600).TimeUtc)}: the file's events from before then are not the window's)", EventsLine(inc));
        // A write of rows: the file starts again with the events the first part holds now.
        writer.AppendEvents(inc, mgr.IncidentEvents(inc, now.Ms, forReports: false), maxRows: 100);
        Assert.Equal(Enumerable.Range(1000 + 2_000, 100), EventPids(inc.Dir));
        writer.Write(new History(), inc, mgr.IncidentEvents(inc, now.Ms), maxRows: 0);
        Assert.Equal($"(20000 other events in the window; events.csv gets more of the first events {heldMd}, up to 500 in all, as PoolWatch's write budget allows, " +
                     $"and {AtCloseWithMore}, and the day's `data\\<yyyyMMdd>\\events.csv` has every one)", EventsLine(inc));
        writer.AppendEvents(inc, mgr.IncidentEvents(inc, now.Ms, forReports: false), maxRows: 10_000);
        Assert.Equal(Enumerable.Range(1000 + 2_000, 500), EventPids(inc.Dir));
        var note = PoolWatch.Core.Util.Csv.Split(File.ReadAllLines(path)[^1]);
        Assert.Equal(("Note", Iso(Ev(2_500).TimeUtc)), (note[1], note[0]));
        Assert.Equal($"events.csv holds the first 500 events PoolWatch still held from {Iso(Ev(600).TimeUtc)} (by then it had already let go of events up to {Iso(Ev(1_999).TimeUtc)}), " +
                     $"to this row's time, as PoolWatch had them when it wrote this row; later events are not in this file ({AtClose})", note[9]);
        writer.Write(new History(), inc, mgr.IncidentEvents(inc, now.Ms));
        Assert.Equal($"(500 other events in events.csv: while the incident is open it holds the first 500 events {heldMd}, to {Fmt.Clock(Ev(2_500).TimeUtc)}, " +
                     $"as PoolWatch had them when it wrote them; {19500:N0} more have come since, and {AtClose})", EventsLine(inc));
    }

    [Fact]
    public void AnOpenIncidentsReportCountsEveryEventThatCameAfterItsFirstPartHoweverItHoldsThem()
    {
        // An open incident in its quiet minutes: its growth's end was measured at 22:03:00, so its window ends a minute
        // after (event 2,400's time), and events kept coming, 10,000 in all. After its first 500 it took in 9,500: those up
        // to the window's end it let go of (500 to 1,400), its latest part with the 500 more it holds while open (1,401 to
        // 2,400), the 4,000 after the end it holds should the end move (6,000 on), and those after the end it set aside
        // (2,401 to 5,999). report.md's "more have come since" counts every one of them. (Before, it counted those up to
        // the window's end as measured now, 1,901 here, which is not how many had come.)
        var mgr = new IncidentManager(new Settings(), new TagKnowledge(null, null), new FakeHost(_dir), new ProcessRegistry());
        var inc = new Incident
        {
            Id = "quiet", Dir = Path.Combine(_dir, "quiet"), Tag = PoolTag.Parse("File"), Kind = IncidentKind.Growth,
            OnsetUtc = Start.AddMinutes(2), OnsetMs = At(Start.AddMinutes(2)).Ms, LastRiseUtc = Start.AddMinutes(3), LastRiseMs = At(Start.AddMinutes(3)).Ms,
            EndUtc = Start.AddMinutes(3), EndMs = At(Start.AddMinutes(3)).Ms,
        };
        Assert.True(inc.IsOpen);
        var now = At(Start.AddMinutes(17));
        mgr.AddEvents(Enumerable.Range(0, 10_000).Select(Ev), now);
        var ev = mgr.IncidentEvents(inc, now.Ms);
        Assert.Equal(Enumerable.Range(1000, 500), ev.Kept.Select(e => e.Pid));
        Assert.Equal(Enumerable.Range(1000 + 1_901, 500), ev.Tail.Select(e => e.Pid)); // its latest 500 up to the window's end
        Assert.Equal(1_401, ev.Gap.Count);                                              // events 500 to 1,900
        Assert.Equal(5_000, mgr.LatestHeld(inc));                                       // 1,401 to 2,400 and 6,000 on
        Assert.Equal(10_000, ev.TakenIn);                                               // every event it took in: the first 500 and the 9,500 after them

        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        writer.Write(new History(), inc, ev);
        var note = PoolWatch.Core.Util.Csv.Split(File.ReadAllLines(Path.Combine(inc.Dir, "events.csv"))[^1]);
        Assert.Equal("Note", note[1]);
        Assert.Equal(Iso(Ev(500).TimeUtc), note[0]);
        Assert.Equal($"(500 other events in events.csv: while the incident is open it holds the window's first 500 events, to {Fmt.Clock(Ev(500).TimeUtc)}, " +
                     $"as PoolWatch had them when it wrote them; {9500:N0} more have come since, and {AtClose})", EventsLine(inc));
    }

    [Fact]
    public void AnIncidentThatKeptNoEventBeforeTheBufferLetThemGoKeepsItsLatestOnesAloneAndSaysSo()
    {
        // An incident the manager does not hold (one it holds looks at every event before the buffer lets it go, and
        // every incident PoolWatch writes is one it holds) looked at the buffer while it was empty; then the buffer took in
        // 25,000 events of its window and let the oldest 5,000 go before it looked again. Its first part is empty and ends
        // where it started: the Gap row counts the 5,000 and the 19,500 before its latest 500 from there, and says the
        // file keeps the latest alone; report.md says so too (it holds only its latest 500, and where its first part would
        // have started: not that it holds events from there on, which its first event, 40 minutes later, would make
        // untrue).
        var mgr = new IncidentManager(new Settings(), new TagKnowledge(null, null), new FakeHost(_dir), new ProcessRegistry());
        var inc = new Incident
        {
            Id = "x", Dir = Path.Combine(_dir, "x"), Tag = PoolTag.Parse("File"), Kind = IncidentKind.Growth, OnsetUtc = Start, OnsetMs = At(Start).Ms,
            EndUtc = Start.AddHours(1), EndMs = At(Start.AddHours(1)).Ms, ClosedUtc = Start.AddMinutes(1), ClosedMs = At(Start.AddMinutes(1)).Ms,
        };
        Assert.Empty(mgr.IncidentEvents(inc, At(Start).Ms).Kept);
        mgr.AddEvents(Enumerable.Range(0, 25_000).Select(Ev), At(Start.AddMinutes(42)));
        var ev = mgr.IncidentEvents(inc, At(Start.AddMinutes(42)).Ms);
        Assert.Empty(ev.Kept);
        Assert.Equal(Enumerable.Range(1000 + 24_500, 500), ev.Tail.Select(e => e.Pid));
        Assert.Equal(24_500, ev.Gap.Count);
        Assert.Equal(Start.AddMinutes(-2), ev.Gap.FromUtc); // from where it started keeping events
        Assert.Equal(Start.AddMinutes(-2), ev.CutUtc);
        Assert.Equal(Ev(24_499).TimeUtc, ev.Gap.ToUtc);

        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        writer.Write(new History(), inc, ev);
        var (head, latest) = EventPidsByEnd(inc.Dir);
        Assert.Empty(head);
        Assert.Equal(Enumerable.Range(1000 + 24_500, 500), latest);
        Assert.Equal($"24,500 events left out, between {Iso(Start.AddMinutes(-2))} and {Iso(Ev(24_499).TimeUtc)} (events.csv keeps only the latest 500 events up to the incident window's end)", GapRow(inc).Detail);
        string leaves = $"(500 other events in events.csv; it leaves out {24500:N0} events, between {Fmt.Clock(Start.AddMinutes(-2))} and {Fmt.Clock(Ev(24_499).TimeUtc)}, as its Gap row says";
        Assert.Equal($"{leaves}; it holds only the latest 500 events (its first part would have started at {Fmt.Clock(Start.AddMinutes(-2))}))", EventsLine(inc));
        Assert.True(Ev(24_500).TimeUtc > Start.AddMinutes(40)); // the file's first event

        // Its start is then measured 30 s later: its first part follows it, and is the first 500 events PoolWatch still
        // holds from there (5,000 on: it had let go of events up to 22:08:19.9), which its file, written again, holds
        // with its latest 500, and says so. What it leaves out is counted again from what PoolWatch holds: the events
        // it had let go are not counted anywhere.
        SetOnset(inc, 30);
        ev = mgr.IncidentEvents(inc, At(Start.AddMinutes(42)).Ms);
        writer.Write(new History(), inc, ev);
        (head, latest) = EventPidsByEnd(inc.Dir);
        Assert.Equal(Enumerable.Range(1000 + 5_000, 500), head);
        Assert.Equal(Enumerable.Range(1000 + 24_500, 500), latest);
        Assert.Equal($"19,000 events left out, between {Iso(Ev(5_500).TimeUtc)} and {Iso(Ev(24_499).TimeUtc)} " +
                     $"(events.csv keeps the first 500 events PoolWatch still held from {Iso(Start.AddSeconds(-90))}{WhenWritten} " +
                     $"(by then it had already let go of events up to {Iso(Ev(4_999).TimeUtc)}), and the latest 500 up to the window's end)", GapRow(inc).Detail);
        Assert.Equal($"(1000 other events in events.csv; it leaves out {19000:N0} events, between {Fmt.Clock(Ev(5_500).TimeUtc)} and {Fmt.Clock(Ev(24_499).TimeUtc)}, as its Gap row says" +
                     $"; it holds the events PoolWatch still held from {Fmt.Clock(Start.AddSeconds(-90))} (by then it had already let go of events up to {Fmt.Clock(Ev(4_999).TimeUtc)}))", EventsLine(inc));
    }

    /// <summary>
    /// The shared buffer took in 25,000 events (0.1 s apart from 22:00:00) and, holding 20,000, let the first 5,000 go, the
    /// latest of them at 22:08:19.9. An incident then opens with its window starting at 22:00:00: PoolWatch no longer holds
    /// the events from there, so its first part is the first 500 it still holds (events 5,000 on, from 22:08:20), and it
    /// knows up to when the buffer had let events go. With <paramref name="windowFrom"/>, its window starts at that event's
    /// time instead: from event 5,000 on, PoolWatch holds every event from there, and nothing is noted. Returns the
    /// manager, the incident (open) and the moment.
    /// </summary>
    private (IncidentManager Mgr, Incident Inc, long NowMs) OpenedAfterTheBufferLetGo(string id, int windowFrom = 0)
    {
        var mgr = new IncidentManager(new Settings(), new TagKnowledge(null, null), new FakeHost(_dir), new ProcessRegistry());
        var now = At(Start.AddMinutes(42));
        mgr.AddEvents(Enumerable.Range(0, 25_000).Select(Ev), now);
        Assert.Equal(20_000, mgr.EventCount);
        var onset = Ev(windowFrom).TimeUtc.AddMinutes(2);
        var inc = new Incident
        {
            Id = id, Dir = Path.Combine(_dir, id), Tag = PoolTag.Parse("File"), Kind = IncidentKind.Growth,
            OnsetUtc = onset, OnsetMs = At(onset).Ms, LastRiseUtc = Start.AddHours(1), LastRiseMs = At(Start.AddHours(1)).Ms,
        };
        var first = mgr.IncidentEvents(inc, now.Ms, forReports: false);
        Assert.Equal(Enumerable.Range(1000 + Math.Max(5_000, windowFrom), 500), first.Kept.Select(e => e.Pid)); // the first 500 it still held
        Assert.Equal(Ev(windowFrom).TimeUtc, first.FromUtc);
        // The latest event the buffer had let go, when that is of the window (at its start or after).
        Assert.Equal(windowFrom < 5_000 ? Ev(4_999).TimeUtc : (DateTime?)null, first.LostToUtc);
        return (mgr, inc, now.Ms);
    }

    [Fact]
    public void WhenPoolWatchNoLongerHeldTheEventsFromTheWindowsStartAsAnIncidentOpenedItsFileSaysWhatItHolds()
    {
        // The buffer holds 20,000 events (and 90 minutes): a slow growth on a very busy PC opens its incident with a window
        // that starts further back. The file's first part is then the first 500 events PoolWatch still held, 8 minutes
        // after the window's start here, and every text says that, with up to when PoolWatch had let events go: the Note
        // row, the Gap row and report.md's line in each of its forms. None says "the first 500 events from" the window's
        // start, or "the window's first events", of rows that begin later. (Before, they did: the events let go were not
        // in the file and nothing said so.)
        string Held(DateTime from, Func<DateTime, string> time, string where = "") =>
            $"PoolWatch still held from {time(from)}{where} (by then it had already let go of events up to {time(Ev(4_999).TimeUtc)})";
        string lostIso = Held(Start, Iso), lostMd = Held(Start, Fmt.Clock);
        string NowAt(double at) => $"; the incident's window now starts at {Fmt.Clock(Start.AddSeconds(at - 120))}";
        const string day = ", and the day's `data\\<yyyyMMdd>\\events.csv` has every one";

        // Open: its first 100 rows, the rest waiting for the budget.
        var (mgr, inc, nowMs) = OpenedAfterTheBufferLetGo("open");
        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        string path = Path.Combine(inc.Dir, "events.csv");
        writer.Write(new History(), inc, mgr.IncidentEvents(inc, nowMs), maxRows: 100);
        Assert.Equal(Enumerable.Range(1000 + 5_000, 100), EventPids(inc.Dir));
        Assert.Equal($"(20000 other events in the window; events.csv gets more of the first events {lostMd}, up to 500 in all, as PoolWatch's write budget allows, and {AtCloseWithMore}{day})", EventsLine(inc));

        // The rest of its first part and its Note row.
        writer.AppendEvents(inc, mgr.IncidentEvents(inc, nowMs, forReports: false), maxRows: 10_000);
        Assert.Equal(Enumerable.Range(1000 + 5_000, 500), EventPids(inc.Dir));
        var note = PoolWatch.Core.Util.Csv.Split(File.ReadAllLines(path)[^1]);
        Assert.Equal("Note", note[1]);
        Assert.Equal(Iso(Ev(5_500).TimeUtc), note[0]);
        Assert.Equal($"events.csv holds the first 500 events {lostIso}, to this row's time, as PoolWatch had them when it wrote this row; later events are not in this file ({AtClose})", note[9]);

        // report.md beside it: the window starts where the file's first part does, and the rows are still not "the
        // window's first 500 events"; then with the onset measured later (the window from event 5,300's time) and
        // earlier, before the first part has followed (the file stays as written either way: it has its Note row).
        string Md()
        {
            writer.Write(new History(), inc, mgr.IncidentEvents(inc, nowMs));
            return EventsLine(inc);
        }
        string held = $"(500 other events in events.csv: while the incident is open it holds the first 500 events {lostMd}, to {Fmt.Clock(Ev(5_500).TimeUtc)}, " +
                      $"as PoolWatch had them when it wrote them; {19500:N0} more have come since, and {AtClose}";
        Assert.Equal(held + ")", Md());
        SetOnset(inc, 650);
        Assert.Equal($"{held}{NowAt(650)}: the file's events from before then are not the window's)", Md());
        SetOnset(inc, 60);
        Assert.Equal($"{held}{NowAt(60)}, and its events before {Fmt.Clock(Start)} are not in the file)", Md());

        // Closed, with its onset measured at each place (its first part follows at once): a Gap row, its latest 500, and
        // the first events PoolWatch still holds from the window's start as measured then.
        (string Gap, string Md) Closed(string id, double onsetAt, int firstHeld, int windowFrom = 0)
        {
            var (m, i, _) = OpenedAfterTheBufferLetGo(id, windowFrom);
            SetOnset(i, onsetAt);
            CloseWith(i, 24_999, 50);
            new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry()).Write(new History(), i, m.IncidentEvents(i, At(Start.AddMinutes(50)).Ms));
            var (head, latest) = EventPidsByEnd(i.Dir);
            Assert.Equal(Enumerable.Range(1000 + firstHeld, 500), head);
            Assert.Equal(Enumerable.Range(1000 + 24_500, 500), latest);
            var (time, detail) = GapRow(i);
            Assert.Equal(Iso(Ev(firstHeld + 500).TimeUtc), time);
            return (detail, EventsLine(i));
        }
        string Between(int count, int first) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{count:N0} events left out, between {Iso(Ev(first).TimeUtc)} and {Iso(Ev(24_499).TimeUtc)} ");
        string Leaves(int count, int first) => $"(1000 other events in events.csv; it leaves out {count:N0} events, between {Fmt.Clock(Ev(first).TimeUtc)} and {Fmt.Clock(Ev(24_499).TimeUtc)}, as its Gap row says";
        const string latest500 = ", and the latest 500 up to the window's end)";
        // Where it was: the first 500 it still held when it opened.
        var same = Closed("same", 120, firstHeld: 5_000);
        Assert.Equal($"{Between(19_000, 5_500)}(events.csv keeps the first 500 events {Held(Start, Iso, WhenWritten)}{latest500}", same.Gap);
        Assert.Equal($"{Leaves(19_000, 5_500)}; it holds the events {lostMd})", same.Md);
        // Later, the window from event 5,300's time: the buffer holds every event from there, so the first part is the
        // window's first 500 exactly, and nothing is said of events let go.
        var among = Closed("among", 650, firstHeld: 5_300);
        Assert.Equal($"{Between(18_700, 5_800)}(events.csv keeps the first 500 events from {Iso(Ev(5_300).TimeUtc)}{WhenWritten}{latest500}", among.Gap);
        Assert.Equal(Leaves(18_700, 5_800) + ")", among.Md);
        // Later, the window from 22:00:30, still before the first event held; and a minute earlier, from 21:59:00: the
        // first 500 PoolWatch still holds from there are the same events, and the texts give the start as measured.
        var before = Closed("before", 150, firstHeld: 5_000);
        Assert.Equal($"{Between(19_000, 5_500)}(events.csv keeps the first 500 events {Held(Start.AddSeconds(30), Iso, WhenWritten)}{latest500}", before.Gap);
        Assert.Equal($"{Leaves(19_000, 5_500)}; it holds the events {Held(Start.AddSeconds(30), Fmt.Clock)})", before.Md);
        var earlier = Closed("earlier", 60, firstHeld: 5_000);
        Assert.Equal($"{Between(19_000, 5_500)}(events.csv keeps the first 500 events {Held(Start.AddMinutes(-1), Iso, WhenWritten)}{latest500}", earlier.Gap);
        Assert.Equal($"{Leaves(19_000, 5_500)}; it holds the events {Held(Start.AddMinutes(-1), Fmt.Clock)})", earlier.Md);

        // The buffer had let go only of events from before the window (its first event is event 5,000, and the window
        // starts at that event's time): PoolWatch held every event from the window's start, and the texts are the plain
        // ones. A window that starts one event earlier lost that one event, and the incident notes it.
        var all = Closed("all", 620, firstHeld: 5_000, windowFrom: 5_000);
        Assert.Equal($"{Between(19_000, 5_500)}(events.csv keeps the first 500 events from {Iso(Ev(5_000).TimeUtc)}{WhenWritten}{latest500}", all.Gap);
        Assert.Equal(Leaves(19_000, 5_500) + ")", all.Md);
        OpenedAfterTheBufferLetGo("one", windowFrom: 4_999);
    }

    [Fact]
    public void AnIncidentThatOpensAfterTheBufferLetGoOfItsWindowsFirstEventsSaysSoInItsFile()
    {
        // 80 events every 10 s sample (28,800 an hour, about three times the owner's rate): the shared buffer's 20,000
        // events are about 42 minutes. A slow growth (2 MB a minute) trips the 60-minute rule (100 MB) 50 minutes on, its
        // window starting further back than the buffer reaches: the incident's first part is the first 500 events
        // PoolWatch still held, minutes after the window's start, and its Note row, its report.md and, once closed, its
        // Gap row say so, with the time of the latest event PoolWatch had let go by then.
        Directory.CreateDirectory(_dir);
        var host = new CountingHost(_dir);
        var w = new World { Interval = TimeSpan.FromSeconds(10) };
        var settings = new Settings { GrowthRules = [new(60, 100 * Settings.MB)] };
        var mgr = new IncidentManager(settings, new TagKnowledge(null, null), host, w.Registry) { Budget = new WriteBudget(bytesPerHour: 64L << 20, burst: 64L << 20) };
        var det = new Detector(settings);
        w.AddProcess(4, 0, "System");
        w.AddProcess(100, 4, "node.exe");
        long tag = 100L << 20;
        int pid = 500_000, heldFrom = 0;
        var times = new Dictionary<int, DateTime>();
        Incident? inc = null;
        DateTime opened = default;
        void Step(long grow)
        {
            w.Io(100, other: 300);
            tag += grow;
            w.Sample(("T000", tag));
            var now = w.Now - w.Interval;
            var events = new List<LifecycleEvent>();
            for (int k = 0; k < 80; k++)
            {
                var t = now.AddMilliseconds(-10_000 + k * 125);
                times[pid] = t;
                events.Add(new LifecycleEvent { TimeUtc = t, Kind = LifecycleKind.Start, Pid = pid++, CreateTime = t.ToFileTimeUtc(), ParentPid = 100, Image = "node.exe", CommandLine = "node build.js" });
            }
            mgr.AddEvents(events, At(now));
            mgr.Process(w.History, det, det.Evaluate(w.History), At(now));
            if (inc is null && mgr.Open.Count > 0)
            {
                // As it opens: where its window starts, and the first event the buffer still holds.
                inc = mgr.Open.Single();
                opened = inc.OnsetUtc.AddMinutes(-2);
                heldFrom = pid - mgr.EventCount;
            }
            host.Settle();
        }
        for (int i = 0; i < 30; i++) Step(0);
        for (int i = 0; i < 420 && inc is null; i++) Step((2L << 20) / 6);
        Assert.NotNull(inc);
        Assert.Equal(20_000, mgr.EventCount);
        DateTime firstHeld = times[heldFrom], lostTo = times[heldFrom - 1];
        Assert.True(firstHeld > opened.AddMinutes(5), $"the window started at {opened:HH:mm:ss} when it opened, the first event still held is of {firstHeld:HH:mm:ss}");
        for (int i = 0; i < 12; i++) Step((2L << 20) / 6);
        Assert.True(mgr.WriteNow(w.History, inc!));

        // Open: the first 500 events PoolWatch still held, and the Note row saying so.
        string Held(DateTime from, Func<DateTime, string> time, string where = "") =>
            $"PoolWatch still held from {time(from)}{where} (by then it had already let go of events up to {time(lostTo)})";
        Assert.Equal(Enumerable.Range(heldFrom, IncidentWriter.MaxEventRows), EventPids(inc!.Dir));
        var note = PoolWatch.Core.Util.Csv.Split(File.ReadAllLines(Path.Combine(inc.Dir, "events.csv"))[^1]);
        Assert.Equal("Note", note[1]);
        Assert.Equal(Iso(times[heldFrom + IncidentWriter.MaxEventRows]), note[0]);
        Assert.Equal($"events.csv holds the first 500 events {Held(opened, Iso)}, to this row's time, as PoolWatch had them when it wrote this row; later events are not in this file ({AtClose})", note[9]);
        // report.md too; the 60-minute rule's onset is measured a little earlier by now, which it says as well.
        Assert.True(inc.OnsetUtc.AddMinutes(-2) < opened);
        Assert.Equal($"(500 other events in events.csv: while the incident is open it holds the first 500 events {Held(opened, Fmt.Clock)}, to {Fmt.Clock(times[heldFrom + IncidentWriter.MaxEventRows])}, " +
                     $"as PoolWatch had them when it wrote them; {pid - heldFrom - IncidentWriter.MaxEventRows:N0} more have come since, and {AtClose}" +
                     $"; the incident's window now starts at {Fmt.Clock(inc.OnsetUtc.AddMinutes(-2))}, and its events before {Fmt.Clock(opened)} are not in the file)", EventsLine(inc));

        // Closed: those 500, a Gap row for every event between the two parts, the latest 500; the Gap row and report.md say
        // what the first part is: the first 500 events PoolWatch still held from the window's start as it is measured at
        // the close (where it was when the incident opened, again: its own first part, earlier than anything the buffer
        // holds by then, stays as the start is measured earlier and back).
        for (int i = 0; i < 90 && inc.IsOpen; i++) Step(0);
        Assert.False(inc.IsOpen);
        Assert.Equal(opened, inc.OnsetUtc.AddMinutes(-2));
        var (first, latest) = EventPidsByEnd(inc.Dir);
        Assert.Equal(Enumerable.Range(heldFrom, IncidentWriter.MaxEventRows), first);
        Assert.Equal(IncidentWriter.MaxEventRows, latest.Count);
        int left = latest[0] - first[^1] - 1;
        Assert.Equal(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{left:N0} events left out, between {Iso(times[first[^1] + 1])} and {Iso(times[latest[0] - 1])} ") +
                     $"(events.csv keeps the first 500 events {Held(opened, Iso, WhenWritten)}, and the latest 500 up to the window's end)", GapRow(inc).Detail);
        Assert.Equal($"(1000 other events in events.csv; it leaves out {left:N0} events, between {Fmt.Clock(times[first[^1] + 1])} and {Fmt.Clock(times[latest[0] - 1])}, as its Gap row says" +
                     $"; it holds the events {Held(opened, Fmt.Clock)})", EventsLine(inc));
    }

    [Fact]
    public void ATraceThePersonStartedHoldsItsTracesOwnEventsWhereverTheTraceStartedAfterItWasMade()
    {
        // "Capture now" makes its incident with its window at that moment (no event yet: the latest came 2 s before) and,
        // when the trace finishes, sets the incident's start to the trace's own, a little later, on both clocks (File
        // holds still here; ATraceThePersonStartedStartsExactlyWhereItsTraceDid... has it rising, the usual case). The
        // trace is its window, exactly: its first part is taken again from the trace's start, so its events.csv holds the
        // trace's own events, every start from the first at or after the trace's start to the last at or before its end,
        // whether the trace started 0.4 s or 30 s after the incident was made; and report.md counts them and says nothing
        // of a start that moved, as the file's rows are from where the window starts. (Before, its events.csv started 2
        // minutes before the incident was made and kept those rows, and report.md said that its start "was set to" the
        // trace's and that the file's events from before then were not the window's.)
        foreach (double delay in new[] { 0.4, 30 })
        {
            string dir = Path.Combine(_dir, "cap" + delay.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dir);
            var w = new World { Interval = TimeSpan.FromSeconds(10) };
            var mgr = new IncidentManager(new Settings(), new TagKnowledge(null, null), new CountingHost(dir), w.Registry);
            w.AddProcess(4, 0, "System");
            int pid = 9000;
            var times = new Dictionary<int, DateTime>();
            void Step()
            {
                w.Sample(("File", 100 * Settings.MB));
                var now = w.Now - w.Interval;
                var events = Enumerable.Range(0, 5).Select(k => new LifecycleEvent { TimeUtc = now.AddSeconds(-10 + 2 * k), Kind = LifecycleKind.Start, Pid = pid++, CreateTime = 1 + pid, Image = "x.exe" }).ToList();
                foreach (var e in events) times[e.Pid] = e.TimeUtc;
                mgr.AddEvents(events, At(now));
            }
            for (int i = 0; i < 30; i++) Step();
            var made = w.Now - w.Interval;
            var inc = mgr.CreateCapture(w.History, [PoolTag.Parse("File")], made);
            Assert.Equal(made, inc.OnsetUtc);
            Assert.Empty(EventPids(inc.Dir)); // its window is that moment alone until its trace finishes
            Assert.Equal("", EventsLine(inc));
            for (int i = 0; i < 9; i++) Step();
            var (start, end) = (made.AddSeconds(delay), made.AddSeconds(delay + 60));
            mgr.ApplyCapture(w.History, inc, new CaptureResult { PoolEventsParsed = true, Tags = [PoolTag.Parse("File")] }.Recorded(At(start), At(end)));
            Assert.Equal((start, At(start).Ms), (inc.OnsetUtc, inc.OnsetMs)); // the trace's start, on both clocks
            var rows = EventPids(inc.Dir);
            Assert.Equal(times.Where(kv => kv.Value >= start && kv.Value <= end).Select(kv => kv.Key).Order(), rows);
            Assert.Equal(30, rows.Count); // a start every 2 s through the trace's minute
            Assert.Equal($"({rows.Count} other events in events.csv)", EventsLine(inc));
        }
    }

    /// <summary>The events report.md lists under "Events in the window", each as its PID, in the order listed.</summary>
    private static List<int> ListedPids(Incident inc)
    {
        var md = File.ReadAllLines(Path.Combine(inc.Dir, "report.md"));
        return md.Skip(Array.IndexOf(md, "## Events in the window") + 2).TakeWhile(l => l.StartsWith("- ", StringComparison.Ordinal))
            .Select(l => int.Parse(System.Text.RegularExpressions.Regex.Match(l, @"\(PID ([0-9]+)\)").Groups[1].Value)).ToList();
    }

    /// <summary>report.md's Growth line.</summary>
    private static string GrowthLine(Incident inc) =>
        Assert.Single(File.ReadAllLines(Path.Combine(inc.Dir, "report.md")), l => l.StartsWith("- Growth: ", StringComparison.Ordinal));

    [Fact]
    public void ATraceThePersonStartedStartsExactlyWhereItsTraceDidOnBothClocksAndIsNeverMoved()
    {
        // "Capture now" takes the tags that rose in the last 10 minutes (the usual case: File, rising 4 MB a 10 s sample;
        // or risen until 40 s before, then still), else File as it stands (held still). Its incident is made with its
        // start at that moment and, when the trace finishes, starts exactly where the trace did, on both clocks: here
        // 3.7 s after it was made, between two samples. The start is never measured (the owner's choice, 2026-11-11):
        // not moved to where the tag's rise began (before, a rising File's incident was made with its start a minute
        // earlier, and after the trace its start was 53.7 s before the trace's, which report.md said it "was set to ...
        // when its trace finished"), nor read off the next sample (before, a File that held still kept the trace's time
        // of day but started 6.3 s later on the forward clock, so its report listed its window from 3 events after the
        // start it printed). So report.json's onset, the ranking's window, the Growth line, events.csv and the events
        // listed are all from the one start: the first event listed is the first at or after the start that line gives
        // (the window is the trace itself, with no minutes before it). The tag's levels are read from the first sample at
        // or after the start, and the end of its rise is looked for from there on only: a rise that stopped before the
        // trace is not an end before the start.
        var shapes = new (string Name, Func<int, long> Rise)[]
        {
            ("rising", _ => 4 * Settings.MB),
            ("still", _ => 0),
            ("stopped", i => i is >= 14 and < 26 ? 4 * Settings.MB : 0),
        };
        foreach (var (name, rise) in shapes)
        {
            string dir = Path.Combine(_dir, name);
            Directory.CreateDirectory(dir);
            var w = new World { Interval = TimeSpan.FromSeconds(10) };
            var mgr = new IncidentManager(new Settings(), new TagKnowledge(null, null), new CountingHost(dir), w.Registry);
            w.AddProcess(4, 0, "System");
            long tag = 100 * Settings.MB;
            int pid = 9000, step = 0;
            var times = new Dictionary<int, DateTime>();
            void Step()
            {
                tag += rise(step++);
                w.Sample(("File", tag));
                var now = w.Now - w.Interval;
                // An exit every 2 s: report.md lists a window's exits (its starts only of a suspect).
                var events = Enumerable.Range(0, 5).Select(k => new LifecycleEvent { TimeUtc = now.AddSeconds(-10 + 2 * k), Kind = LifecycleKind.Exit, Pid = pid++, CreateTime = 1 + pid, Image = "x.exe" }).ToList();
                foreach (var e in events) times[e.Pid] = e.TimeUtc;
                mgr.AddEvents(events, At(now));
            }
            for (int i = 0; i < 30; i++) Step();
            var made = w.Now - w.Interval;
            long level = tag;

            // Made: its start is that moment on both clocks, whatever its tag did before, and nothing has grown since.
            var inc = mgr.CreateCapture(w.History, [PoolTag.Parse("File")], made);
            Assert.Equal((made, At(made).Ms), (inc.OnsetUtc, inc.OnsetMs));
            Assert.Equal((level, 0L, (DateTime?)null), (inc.StartBytes, inc.Growth, inc.EndUtc));
            // Made closed: its level with the sample it is of, not "now"; and no growth over no time ("... -> ... (+0 B) from ..."), which its trace has yet to give it.
            Assert.Equal($"- Growth: its trace had not finished by {Fmt.Clock(made)}; the tag held {Fmt.Bytes(level)} at {Fmt.Clock(made)}", GrowthLine(inc));
            Assert.Empty(ListedPids(inc)); // its window is that moment alone until its trace finishes (the latest exit was 2 s before)

            // The trace, from 3.7 s after it was made, for a minute.
            for (int i = 0; i < 9; i++) Step();
            var start = made.AddSeconds(3.7);
            mgr.ApplyCapture(w.History, inc, new CaptureResult { PoolEventsParsed = true, Tags = [PoolTag.Parse("File")] }.Recorded(At(start), At(start.AddMinutes(1))));
            Assert.Equal((start, At(start).Ms), (inc.OnsetUtc, inc.OnsetMs));
            Assert.Equal((start, (long?)At(start).Ms), (inc.Window!.OnsetUtc, inc.Window.OnsetMs));
            using (var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(inc.Dir, "report.json"))))
                Assert.Equal(start, json.RootElement.GetProperty("onsetUtc").GetDateTime().ToUniversalTime());
            // Its levels: from the first sample at or after the start (10 s after it was made) to the last of the trace (60 s
            // after). A rise through the trace has no end yet; no rise since the start has none either, and is not climbing.
            long from = level + rise(30), to = from + Enumerable.Range(31, 5).Sum(rise);
            Assert.Equal((from, to - from, (DateTime?)null), (inc.StartBytes, inc.Growth, inc.EndUtc));
            Assert.Equal(name == "rising", to > from);
            Assert.Equal($"- Growth: {Fmt.Bytes(from)} -> {Fmt.Bytes(to)} (+{Fmt.Bytes(to - from)}) from {Fmt.Clock(start)}" +
                         (to > from ? " (still climbing when it closed)" : "") + $"; {Fmt.Bytes(to)} at {Fmt.Clock(made.AddSeconds(60))}", GrowthLine(inc)); // the last sample of the trace
            // The window: the trace, from its start. The Growth line gives that to the second, and the events listed begin
            // with the first event at or after the second it gives (the exits are 2 s apart: 06:04:54 after 06:04:53),
            // and are the trace's own, to the last at or before its end; events.csv holds the same, each once, so
            // nothing is left to count under the list.
            var said = made.AddSeconds(3);
            var listed = ListedPids(inc);
            var rows = EventRows(inc.Dir).Select(r => r.Pid).ToList();
            Assert.Equal(30, listed.Count);
            Assert.Equal(listed, rows);
            Assert.Equal("", EventsLine(inc));
            Assert.Equal(made.AddSeconds(4), times.Values.Where(t => t >= said).Min());
            Assert.Equal(times.Values.Where(t => t >= said).Min(), times[listed[0]]);
            Assert.Equal(made.AddSeconds(62), times[listed[^1]]); // the last before the trace's end, 63.7 s after it was made

            // Measured again later, as any ranking would: it stays the trace's own start.
            for (int i = 0; i < 6; i++) Step();
            mgr.Rank(w.History, inc, w.Now - w.Interval);
            Assert.Equal((start, At(start).Ms), (inc.OnsetUtc, inc.OnsetMs));
            Assert.Equal((start, (long?)At(start).Ms), (inc.Window!.OnsetUtc, inc.Window.OnsetMs));
        }
    }

    [Fact]
    public void ATraceThatDidNotRecordLeavesItsIncidentsStartWhereItWasMade()
    {
        // "Capture now" on a rising File, and a trace that could not start (it has no start or end): the incident keeps the
        // start and the end it was made with, that moment on both clocks, and its report says nothing of a move. (Before, it was made
        // with its start a minute earlier, where the rise was measured to begin, and attaching the failed trace measured
        // it a minute earlier again.)
        Directory.CreateDirectory(_dir);
        var w = new World { Interval = TimeSpan.FromSeconds(10) };
        var mgr = new IncidentManager(new Settings(), new TagKnowledge(null, null), new CountingHost(_dir), w.Registry);
        w.AddProcess(4, 0, "System");
        int pid = 9000;
        void Step()
        {
            w.Sample(("File", 100 * Settings.MB + w.Index * 4 * Settings.MB));
            var now = w.Now - w.Interval;
            mgr.AddEvents(Enumerable.Range(0, 5).Select(k => new LifecycleEvent { TimeUtc = now.AddSeconds(-10 + 2 * k), Kind = LifecycleKind.Start, Pid = pid++, CreateTime = 1 + pid, Image = "x.exe" }).ToList(), At(now));
        }
        for (int i = 0; i < 30; i++) Step();
        var made = w.Now - w.Interval;
        var inc = mgr.CreateCapture(w.History, [PoolTag.Parse("File")], made);
        for (int i = 0; i < 3; i++) Step();
        mgr.ApplyCapture(w.History, inc, new CaptureResult { Tags = [PoolTag.Parse("File")], Error = "wpr -start failed (1): no" });
        Assert.Equal((made, At(made).Ms), (inc.OnsetUtc, inc.OnsetMs));
        Assert.Equal(made, inc.ClosedUtc);
        // Its window is that moment alone: its events.csv holds the one start of that very moment, and its report counts it.
        Assert.Equal([made], EventRows(inc.Dir).Select(r => r.Time));
        Assert.Equal("(1 other events in events.csv)", EventsLine(inc));
    }

    [Fact]
    public void ATraceThatEndedBeforeTheNextSampleStartsWhereItDidAndReadsItsTagAtTheLastSampleBeforeIt()
    {
        // The shortest trace, 5 s, from 3.7 s after its incident was made: over before the next 10 s sample. The incident
        // starts where the trace did all the same, on both clocks; with no sample from that start to the trace's end, its
        // tag's level is the last sample's before it, and nothing is measured as grown (File rises 4 MB a sample).
        Directory.CreateDirectory(_dir);
        var w = new World { Interval = TimeSpan.FromSeconds(10) };
        var mgr = new IncidentManager(new Settings(), new TagKnowledge(null, null), new CountingHost(_dir), w.Registry);
        w.AddProcess(4, 0, "System");
        long tag = 96 * Settings.MB;
        void Step() => w.Sample(("File", tag += 4 * Settings.MB));
        for (int i = 0; i < 30; i++) Step();
        var made = w.Now - w.Interval;
        long level = tag;
        var inc = mgr.CreateCapture(w.History, [PoolTag.Parse("File")], made);
        for (int i = 0; i < 2; i++) Step();
        var start = made.AddSeconds(3.7);
        mgr.ApplyCapture(w.History, inc, new CaptureResult { PoolEventsParsed = true, Tags = [PoolTag.Parse("File")] }.Recorded(At(start), At(start.AddSeconds(5))));
        Assert.Equal((start, At(start).Ms), (inc.OnsetUtc, inc.OnsetMs));
        Assert.Equal((level, 0L, (DateTime?)null), (inc.StartBytes, inc.Growth, inc.EndUtc));
        Assert.Equal($"- Growth: {Fmt.Bytes(level)} -> {Fmt.Bytes(level)} (+{Fmt.Bytes(0)}) from {Fmt.Clock(start)}; {Fmt.Bytes(level)} at {Fmt.Clock(made)}", GrowthLine(inc)); // the level is the last sample's before the trace, said with that sample's time
    }

    [Fact]
    public void ATraceThePersonStartedSaysAndCountsItsGrowthOverTheTraceItself()
    {
        // The trace of a "Capture now" incident names who allocated the tag: node.exe here, File rising 4 MB a sample.
        // What its answer says of the growth, and the process starts its owner's line counts, are over the incident's span:
        // from its start, the trace's own (3.7 s after it was made), to the trace's end, as the tag rose through it. The
        // incident's last rise is the moment it was made, before that start, so a span read to it is less than nothing.
        // (Before, the span was read to it from a start that was measured: a rising tag's answer gave its growth up to
        // the trace's end as "in 50 s", and the starts counted were those from where the rise was measured to begin to
        // the moment the incident was made, the 1,000 here and none of the trace's; a tag that held still had its start
        // read off the sample after the trace began, 10 s after that moment, a span of -10 s.)
        Directory.CreateDirectory(_dir);
        var file = PoolTag.Parse("File");
        var w = new World { Interval = TimeSpan.FromSeconds(10) };
        var mgr = new IncidentManager(new Settings(), new TagKnowledge(null, null), new CountingHost(_dir), w.Registry);
        w.AddProcess(4, 0, "System");
        w.AddProcess(100, 4, "node.exe");
        void Step()
        {
            w.Io(100, other: 300);
            w.Sample(("File", 100 * Settings.MB + w.Index * 4 * Settings.MB));
        }
        for (int i = 0; i < 30; i++) Step();
        var made = w.Now - w.Interval;
        var inc = mgr.CreateCapture(w.History, [file], made);
        for (int i = 0; i < 9; i++) Step();
        var start = made.AddSeconds(3.7);
        var cap = new CaptureResult
        {
            PoolEventsParsed = true, Tags = [file],
            Pool = [new CapturePoolUsage(100, "node.exe", file, 18 * Settings.MB, 0, 1000, 0, [], ParentPid: 4, FirstAllocUtc: start.AddSeconds(5))],
        }.Recorded(At(start), At(start.AddMinutes(1)));
        // Its owner's short commands, as the summarizer's rollups give them: 1,000 half a minute before the incident was
        // made, 100 between its making and the trace's start, 5 and 7 during the trace, 10,000 after the trace's end.
        string owner = Assert.Single(TraceAttribution.Owners(TraceAttribution.Resolve(cap, file, w.Registry))).Owner.Key;
        LifecycleEvent Rollup(double at, int count) => new() { TimeUtc = made.AddSeconds(at), Kind = LifecycleKind.Rollup, Pid = 100, OwnerKey = owner, Count = count };
        mgr.AddEvents([Rollup(-30, 1_000), Rollup(2, 100), Rollup(20, 5), Rollup(63, 7), Rollup(70, 10_000)], At(w.Now - w.Interval));
        mgr.ApplyCapture(w.History, inc, cap);

        Assert.Equal((start, At(start).Ms), (inc.OnsetUtc, inc.OnsetMs));
        Assert.Equal(TimeSpan.FromMinutes(1), inc.GrowthSpan);
        Assert.Equal(At(start.AddMinutes(1)).Ms, inc.SpanEndMs);
        long grew = 20 * Settings.MB; // from the first sample at or after the start to the last of the trace
        Assert.Equal(grew, inc.Growth);
        var md = File.ReadAllLines(Path.Combine(inc.Dir, "report.md"));
        string what = new TagKnowledge(null, null).Lookup(file).Description;
        // The incident is closed (made closed, then closed with its trace): what its answer says of the tag's level is of
        // the last sample of the trace, a minute after the incident was made, and says so.
        Assert.StartsWith($"File ({what}) grew {Fmt.Bytes(grew)} in 1 min and was still held at {Fmt.Clock(made.AddMinutes(1))}. The trace shows node allocating it: ", md[Array.IndexOf(md, "## In plain words") + 2]);
        Assert.EndsWith("; 12 process starts during the growth.", Assert.Single(md, l => l.StartsWith("- **", StringComparison.Ordinal) && l.Contains(" net in ", StringComparison.Ordinal)));
        Assert.Contains($"grew {Fmt.Bytes(grew)} in 1 min; the confirmation trace shows", md[0]); // the headline says the same span
    }

    [Fact]
    public void ATraceThatStartedBetweenTwoSamplesHasItsAllocationRatesDuringItsGrowth()
    {
        // Samples a minute apart (a setting), and a "Capture now" trace that began 20 s after one of them: the incident's
        // start is the trace's own, 40 s before the next sample, from which its allocation rates are read. That is not a
        // tag "too small to keep its counters before" (a tag whose counters begin over 30 s into the growth): the rates
        // are "during the growth". (Before, an incident's start on the forward clock was always a sample's, and the 30 s
        // were counted from it.)
        Directory.CreateDirectory(_dir);
        var file = PoolTag.Parse("File");
        var h = History.ForInterval(60);
        var t0 = new DateTime(2026, 11, 9, 22, 0, 0, DateTimeKind.Utc);
        void Add(int i) =>
            h.Add(new Sample(t0.AddMinutes(i), default, [new PoolTagEntry(file, 0, 0, 0, (uint)(3000 * i), (uint)(300 * i), (ulong)(64 * Settings.MB + i * Settings.MB))], []) { TickMs = At(t0.AddMinutes(i)).Ms });
        var mgr = new IncidentManager(new Settings(), new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        for (int i = 1; i <= 20; i++) Add(i);
        var made = h.Times[^1];
        var inc = mgr.CreateCapture(h, [file], made);
        for (int i = 21; i <= 27; i++) Add(i);
        var start = made.AddSeconds(20);
        mgr.ApplyCapture(h, inc, new CaptureResult { PoolEventsParsed = true, Tags = [file] }.Recorded(At(start), At(start.AddMinutes(5))));
        Assert.Equal((start, At(start).Ms), (inc.OnsetUtc, inc.OnsetMs));
        string shape = Assert.Single(File.ReadAllLines(Path.Combine(inc.Dir, "report.md")), l => l.StartsWith("- Allocation shape", StringComparison.Ordinal));
        Assert.StartsWith("- Allocation shape during the growth: 3,000 allocations/min, 300 frees/min", shape);
    }

    /// <summary>The event rows of an events.csv (its alerts' rows and its Gap and Note rows aside), each as its time, PID and kind, in the file's order.</summary>
    private static List<(DateTime Time, int Pid, string Kind)> EventRows(string dir) => File.ReadAllLines(Path.Combine(dir, "events.csv")).Skip(1)
        .Select(PoolWatch.Core.Util.Csv.Split).Where(c => c[1] is "Start" or "Exit" or "Rollup")
        .Select(c => (DateTime.Parse(c[0], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime(), int.Parse(c[2]), c[1])).ToList();

    [Fact]
    public void ATraceThePersonStartedHasItsTraceForAWindowExactlyFromItsStartToItsEnd()
    {
        // A "Capture now" incident's window is its trace, exactly (the owner's choice, 2026-11-11): from the trace's own
        // start to the trace's own end, on both clocks, with no minutes before the start and none added after the end,
        // whatever its tag did: File rising 4 MB a sample (the usual case: Capture now takes the tags that rose), holding
        // still, or rising until 100 s into the trace. Made, the incident's window is that moment alone. The trace, 5
        // minutes from 3.7 s after (neither of its ends is at a sample), then sets both ends once, and nothing moves them
        // afterwards. So events.csv holds every event from the first at or after the trace's start to the last at or
        // before its end, and the events report.md lists are those too, by the start its Growth line gives and the end it
        // gives (as the time the incident closed, and as the trace's). (Before, the window started 2 minutes before the
        // trace, and ended a minute after the end of the tag's rise when one was measured, else a minute after the
        // incident was made: a 5-minute trace's events.csv and report held the 2 minutes before it and its first minute.)
        var shapes = new (string Name, Func<int, long> Rise)[]
        {
            ("rising", _ => 4 * Settings.MB),
            ("still", _ => 0),
            ("stops", i => i < 40 ? 4 * Settings.MB : 0),
        };
        foreach (var (name, rise) in shapes)
        {
            string dir = Path.Combine(_dir, name);
            Directory.CreateDirectory(dir);
            var w = new World { Interval = TimeSpan.FromSeconds(10) };
            var mgr = new IncidentManager(new Settings(), new TagKnowledge(null, null), new CountingHost(dir), w.Registry);
            var det = new Detector(new Settings());
            w.AddProcess(4, 0, "System");
            long tag = 100 * Settings.MB;
            int pid = 9000, step = 0;
            var times = new Dictionary<int, DateTime>();
            // The incident is made at the 30th sample (06:04:50), and the trace runs from 06:04:53.7 to 06:09:53.6: that
            // is its window. Exits (report.md lists a window's exits; its starts only of a suspect) 2 s apart on each side
            // of both ends: 06:04:52 and 06:04:54, 06:09:52 and 06:09:54; and one 2 minutes before the trace, at 06:02:54.
            var made = World.T0.AddSeconds(290);
            var start = made.AddMilliseconds(3_700);
            var end = start.AddMilliseconds(299_900);
            var exits = new[] { -116, 2, 4, 302, 304 }
                .Select(s => new LifecycleEvent { TimeUtc = made.AddSeconds(s), Kind = LifecycleKind.Exit, Pid = 100_000 + s, CreateTime = 1, Image = "y.exe" }).ToList();
            void Step()
            {
                tag += rise(step++);
                w.Sample(("File", tag));
                var now = w.Now - w.Interval;
                // Two starts a sample, 10 s and 4 s before it, and each exit with the first sample after it.
                var events = new[] { -10, -4 }.Select(s => new LifecycleEvent { TimeUtc = now.AddSeconds(s), Kind = LifecycleKind.Start, Pid = pid++, CreateTime = 1 + pid, Image = "x.exe" }).ToList();
                events.AddRange(exits.Where(e => e.TimeUtc > now.AddSeconds(-10) && e.TimeUtc <= now));
                foreach (var e in events) times[e.Pid] = e.TimeUtc;
                mgr.AddEvents(events.OrderBy(e => e.TimeUtc).ToList(), At(now));
                mgr.Process(w.History, det, [], At(now));
            }
            for (int i = 0; i < 30; i++) Step();
            Assert.Equal(made, w.Now - w.Interval);

            // Made: its window is that moment alone, on both clocks (before, from 2 minutes before it to a minute after):
            // no event is of it yet.
            var inc = mgr.CreateCapture(w.History, [PoolTag.Parse("File")], made);
            Assert.Equal((made, (long?)At(made).Ms), (inc.ClosedUtc, inc.ClosedMs));
            Assert.Equal(At(made).Ms, IncidentManager.EventWindowStartMs(inc));
            Assert.Equal(At(made).Ms, IncidentManager.EventWindowEndMs(inc, At(made).Ms + 3_600_000));
            Assert.Empty(ListedPids(inc));
            Assert.Empty(EventRows(inc.Dir));

            // The trace, attached 6.4 s after it ended: the incident starts and closes where the trace did, on both clocks,
            // and that is its window.
            for (int i = 0; i < 31; i++) Step();
            mgr.ApplyCapture(w.History, inc, new CaptureResult { PoolEventsParsed = true, Tags = [PoolTag.Parse("File")], EtlPath = Path.Combine(inc.Dir, "capture.etl") }
                .Recorded(At(start), At(end)));
            Assert.Equal((start, At(start).Ms), (inc.OnsetUtc, inc.OnsetMs));
            Assert.Equal((end, (long?)At(end).Ms), (inc.ClosedUtc, inc.ClosedMs));
            Assert.Equal(At(start).Ms, IncidentManager.EventWindowStartMs(inc));
            Assert.Equal(At(end).Ms, IncidentManager.EventWindowEndMs(inc, At(end).Ms + 3_600_000));
            // Whatever its tag did: a rise that goes on has no end, and one that stopped 100 s into the trace has its end
            // there (before, the window ended a minute after that end; a minute after the incident was made without one).
            if (name == "stops") Assert.True(inc.EndUtc is { } stopped && stopped < end.AddMinutes(-2), $"the rise's end: {inc.EndUtc:o}");
            else Assert.Null(inc.EndUtc);

            // events.csv: every event from the trace's start to its end, each once. Its first is the exit at 06:04:54 (not
            // the one at 06:04:52, nor the start at 06:04:50, nor any of the 2 minutes before); its last the exit at
            // 06:09:52 (not the one at 06:09:54, nor the start at 06:09:56).
            var rows = EventRows(inc.Dir);
            Assert.Equal(times.Where(kv => kv.Value >= start && kv.Value <= end).Select(kv => kv.Key).Order(), rows.Select(r => r.Pid).Order());
            Assert.Equal(made.AddSeconds(4), rows.Min(r => r.Time));
            Assert.Equal(made.AddSeconds(302), rows.Max(r => r.Time));
            Assert.Equal(62, rows.Count); // the trace's 60 starts and the two exits
            // report.md: the events it lists are from the first at or after the start its Growth line gives (06:04:53: the
            // exit at 06:04:54, not the one at 06:04:52) to the last at or before the end it gives (06:09:53: the exit at
            // 06:09:52, not the one at 06:09:54), and it gives the end as the trace's and as when the incident closed.
            Assert.Equal([made.AddSeconds(4), made.AddSeconds(302)], ListedPids(inc).Select(p => times[p]));
            var md = File.ReadAllLines(Path.Combine(inc.Dir, "report.md"));
            Assert.Contains($"- Incident `{inc.Id}` (Capture), opened {Fmt.Stamp(made)}, closed {Fmt.Stamp(end)}", md);
            Assert.Contains(md, l => l.StartsWith($"- Recorded {Fmt.Clock(start)}-{Fmt.Clock(end)} (", StringComparison.Ordinal));
            Assert.Contains($" from {Fmt.Clock(start)}", GrowthLine(inc));
            Assert.Equal($"({rows.Count - 2} other events in events.csv)", EventsLine(inc));

            // Nothing moves either end afterwards: two more minutes of samples and events, a later ranking, the end of the run.
            string file = File.ReadAllText(Path.Combine(inc.Dir, "events.csv"));
            for (int i = 0; i < 12; i++) Step();
            mgr.Rank(w.History, inc, w.Now - w.Interval);
            mgr.Finish(w.History, w.Now);
            Assert.Equal((start, At(start).Ms), (inc.OnsetUtc, inc.OnsetMs));
            Assert.Equal((end, (long?)At(end).Ms), (inc.ClosedUtc, inc.ClosedMs));
            Assert.Equal(At(start).Ms, IncidentManager.EventWindowStartMs(inc));
            Assert.Equal(At(end).Ms, IncidentManager.EventWindowEndMs(inc, At(w.Now).Ms));
            Assert.Equal(file, File.ReadAllText(Path.Combine(inc.Dir, "events.csv")));
            Assert.Equal([made.AddSeconds(4), made.AddSeconds(302)], ListedPids(inc).Select(p => times[p]));
        }
    }

    /// <summary>
    /// A "Capture now" trace on a busy PC: 40 events every 10 s sample (PID 500,000 + i is at <c>Times[i]</c>), File holding
    /// still, the incident made at the 30th sample, and a 5-minute trace from <paramref name="startAfterMs"/> after (3.7 s:
    /// PoolWatch makes the incident at the newest sample's time, so a trace always starts a little after it), attached at the
    /// first sample after it ended. While the trace records, the incident's folder is written at every sample (a note on
    /// its timeline, as "capture started" is one), so the incident looks at each sample's events as they come.
    /// <c>Step</c>: one more sample with its 40 events and those given; <c>Quiet</c>: one with none; <c>Finish</c>:
    /// PoolWatch stops.
    /// </summary>
    private (IncidentManager Mgr, Incident Inc, List<DateTime> Times, DateTime Made, DateTime Start, DateTime End, Action<LifecycleEvent[]> Step, Action Quiet, Action Finish) BusyTrace(int startAfterMs = 3_700)
    {
        Directory.CreateDirectory(_dir);
        var w = new World { Interval = TimeSpan.FromSeconds(10) };
        var mgr = new IncidentManager(new Settings(), new TagKnowledge(null, null), new CountingHost(_dir), w.Registry) { Budget = new WriteBudget(1L << 30, 1L << 30) };
        var det = new Detector(new Settings());
        w.AddProcess(4, 0, "System");
        int pid = 500_000;
        var times = new List<DateTime>();
        Incident? inc = null;
        void Step(LifecycleEvent[] more)
        {
            w.Sample(("File", 100 * Settings.MB));
            var now = w.Now - w.Interval;
            var events = Enumerable.Range(0, 40).Select(k => new LifecycleEvent { TimeUtc = now.AddSeconds(-10).AddMilliseconds(k * 250), Kind = LifecycleKind.Start, Pid = pid++, CreateTime = 1 + pid, Image = "x.exe" }).ToList();
            times.AddRange(events.Select(e => e.TimeUtc));
            mgr.AddEvents(events.Concat(more).OrderBy(e => e.TimeUtc).ToList(), At(now));
            if (inc is { Capture: null }) mgr.Note(inc, now, $"still recording at {Fmt.Clock(now)}");
            mgr.Process(w.History, det, [], At(now));
        }
        void Quiet()
        {
            w.Sample(("File", 100 * Settings.MB));
            mgr.Process(w.History, det, [], At(w.Now - w.Interval));
        }
        for (int i = 0; i < 30; i++) Step([]);
        var made = w.Now - w.Interval;
        inc = mgr.CreateCapture(w.History, [PoolTag.Parse("File")], made);
        for (int i = 0; i < 31; i++) Step([]);
        var start = made.AddMilliseconds(startAfterMs);
        var end = start.AddMilliseconds(299_900);
        mgr.ApplyCapture(w.History, inc, new CaptureResult { PoolEventsParsed = true, Tags = [PoolTag.Parse("File")] }.Recorded(At(start), At(end)));
        return (mgr, inc, times, made, start, end, Step, Quiet, () => mgr.Finish(w.History, w.Now));
    }

    [Theory]
    [InlineData(3_700)]
    [InlineData(0)]
    public void ATracesIncidentOnABusyPcKeepsBothEndsOfItsTraceHoweverOftenItLookedAtItsEventsMeanwhile(int startAfterMs)
    {
        // 40 events a sample and a 5-minute trace: the window (the trace, from its start to its end) has 1,200 events, and
        // the incident's first part (500) is full two minutes after it is made. Until the trace is attached the incident's
        // window ends where it was made, and each time its folder is written meanwhile (a note on its timeline, a
        // suspect's session found, and before the buffer lets its events go) it looks at the events that came since: after
        // its window's end then, and held all the same, for the window the trace gives it. Attached, its events.csv holds
        // the first 500 events from the trace's start, a Gap row that counts every event between the two parts, and the
        // latest 500 up to the trace's end (5 minutes less a tenth of a second after its start), not one after it: taken
        // again from the trace's start when that is after the moment the incident was made (3.7 s after: as PoolWatch
        // makes it, the first part follows the window's start), and, when the trace started at that very moment, as the
        // incident held them while the trace recorded. (With the window's end at the trace's and nothing held meanwhile,
        // the events it looked at while the trace recorded were neither in the file nor counted.)
        var (_, inc, times, made, start, end, _, _, _) = BusyTrace(startAfterMs);
        Assert.Equal(made.AddMilliseconds(startAfterMs), start);
        int first = times.FindIndex(t => t >= start), last = times.FindLastIndex(t => t <= end);
        int left = last - first + 1 - 2 * IncidentWriter.MaxEventRows;
        Assert.True(left > 150, $"{left} events between the two parts");
        Assert.True(times[last + 1] > end && times.Count > last + 15); // events from after the trace's end had come too
        var (head, latest) = EventPidsByEnd(inc.Dir);
        Assert.Equal(Enumerable.Range(500_000 + first, IncidentWriter.MaxEventRows), head);
        Assert.Equal(Enumerable.Range(500_000 + last - IncidentWriter.MaxEventRows + 1, IncidentWriter.MaxEventRows), latest);
        var c = System.Globalization.CultureInfo.InvariantCulture;
        Assert.Equal((Iso(times[first + 500]), string.Create(c, $"{left:N0} events left out, between {Iso(times[first + 500])} and {Iso(times[last - 500])} ") +
                      $"(events.csv keeps the first 500 events from {Iso(start)}{WhenWritten}, and the latest 500 up to the window's end)"), GapRow(inc));
        Assert.Equal($"(1000 other events in events.csv; it leaves out {left:N0} events, between {Fmt.Clock(times[first + 500])} and {Fmt.Clock(times[last - 500])}, as its Gap row says)", EventsLine(inc));
    }

    [Fact]
    public void AnEventOfATracesLastSecondsThatComesAfterTheTraceIsAttachedIsInItsReportAndItsFileAtTheNextSample()
    {
        // The summarizer hands over a short command's rollup up to a minute and a half after its first start (a start is
        // held 30 s to see whether it is short-lived, and the rollup's minute then runs out), so the rollups of a trace's
        // last seconds can come after the trace is attached and the incident's folder written. The incident is closed,
        // and a closed incident took events only when it was written: such a rollup was in neither its report nor its
        // events.csv until something else had its folder written again, or its row was added to the file later with
        // report.md still counting the file's rows as they had been. Now a closed incident takes an event timed at or
        // before its window's end as it comes, and its report and the row are written together at the next sample (as
        // the write budget allows); the events from after its window's end stay out.
        var (_, _, inc, made, _, step, finish) = QuietTrace();
        int rows = EventRows(inc.Dir).Count;
        Assert.Equal(30, rows); // a start every 2 s through the trace: from 4 s after the incident was made (the trace started 3.7 s after) to 62 s after (it ended 63.7 s after)
        Assert.Empty(ListedPids(inc));
        Assert.Equal($"({rows} other events in events.csv)", EventsLine(inc));

        // The next sample, 16 s after the trace's end: its own five starts (from after the end), and a rollup whose first
        // start was 13 s before the end.
        step([Rollup(made, 50.5, 12)]);
        var after = EventRows(inc.Dir);
        Assert.Equal(rows + 1, after.Count); // the rollup's row; none of the starts from after the trace's end
        Assert.Contains((made.AddSeconds(50.5), 70_012, "Rollup"), after);
        Assert.Equal([70_012], ListedPids(inc)); // report.md lists it (it lists a window's events but for the starts)
        Assert.Equal($"({rows} other events in events.csv)", EventsLine(inc)); // the file's rows as they are, the one listed aside
        // Its row was added at the end of the file, after the rows of later times (a write that can wait adds rows; it
        // does not write the file again). The samples after it bring no event of its window, and nothing of its folder
        // is written until two minutes after its window's end, when every late event of the window can have come: then
        // one more write that must be made writes the file whole, in time order, the rollup's row in its place (before,
        // a closed incident's file with its first part alone stayed as appended, a late event's row after later ones,
        // while the docs said a closed incident's file is sorted by time). Nothing is written after that.
        Assert.Equal((made.AddSeconds(50.5), 70_012, "Rollup"), after[^1]);
        long wrote = inc.LastWriteMs;
        for (int i = 0; i < 10; i++) step([]); // to 116 s after the trace's end
        Assert.Equal(wrote, inc.LastWriteMs);
        Assert.Equal(after, EventRows(inc.Dir));
        step([]); // 126 s after it
        Assert.True(inc.LastWriteMs > wrote);
        var sorted = EventRows(inc.Dir);
        Assert.Equal(after.OrderBy(r => r.Time).ToList(), sorted); // the same rows, in time order
        Assert.NotEqual(after, sorted);
        Assert.Equal([70_012], ListedPids(inc));
        Assert.Equal($"({rows} other events in events.csv)", EventsLine(inc));
        wrote = inc.LastWriteMs;
        for (int i = 0; i < 15; i++) step([]);
        Assert.Equal(wrote, inc.LastWriteMs);

        // One more, and PoolWatch stops before the next sample: the end of the run writes it, with the report, and the
        // file in time order again.
        finish([Rollup(made, 55, 7)]);
        var atEnd = EventRows(inc.Dir);
        Assert.Equal(rows + 2, atEnd.Count);
        Assert.Contains((made.AddSeconds(55), 70_007, "Rollup"), atEnd);
        Assert.Equal(atEnd.OrderBy(r => r.Time).ToList(), atEnd);
        Assert.Equal([70_012, 70_007], ListedPids(inc));
        Assert.Equal($"({rows} other events in events.csv)", EventsLine(inc));
    }

    /// <summary>A short command's rollup, <paramref name="at"/> seconds after <paramref name="made"/> (PID 70,000 + <paramref name="n"/>).</summary>
    private static LifecycleEvent Rollup(DateTime made, double at, int n) =>
        new() { TimeUtc = made.AddSeconds(at), Kind = LifecycleKind.Rollup, Pid = 70_000 + n, Image = "git.exe", Count = n, Detail = $"bash.exe started {n}, {n} exited, short-lived" };

    /// <summary>
    /// A "Capture now" trace on a quiet PC: a start every 2 s, File holding still, the incident made at the 30th sample, and
    /// a minute's trace from 3.7 s after, attached 6.3 s after it ended. <c>Step</c>: one more sample with its five starts
    /// and the events given; <c>Finish</c>: the events given come, and PoolWatch stops before the next sample.
    /// </summary>
    private (IncidentManager Mgr, CountingHost Host, Incident Inc, DateTime Made, DateTime Start, Action<LifecycleEvent[]> Step, Action<LifecycleEvent[]> Finish) QuietTrace()
    {
        Directory.CreateDirectory(_dir);
        var host = new CountingHost(_dir);
        var w = new World { Interval = TimeSpan.FromSeconds(10) };
        var mgr = new IncidentManager(new Settings(), new TagKnowledge(null, null), host, w.Registry);
        var det = new Detector(new Settings());
        w.AddProcess(4, 0, "System");
        int pid = 9000;
        void Step(LifecycleEvent[] late)
        {
            w.Sample(("File", 100 * Settings.MB));
            var now = w.Now - w.Interval;
            var events = Enumerable.Range(0, 5).Select(k => new LifecycleEvent { TimeUtc = now.AddSeconds(-10 + 2 * k), Kind = LifecycleKind.Start, Pid = pid++, CreateTime = 1 + pid, Image = "x.exe" });
            mgr.AddEvents(events.Concat(late).OrderBy(e => e.TimeUtc).ToList(), At(now));
            mgr.Process(w.History, det, [], At(now));
        }
        void Finish(LifecycleEvent[] late)
        {
            mgr.AddEvents(late, At(w.Now - w.Interval));
            mgr.Finish(w.History, w.Now);
        }
        for (int i = 0; i < 30; i++) Step([]);
        var made = w.Now - w.Interval;
        var inc = mgr.CreateCapture(w.History, [PoolTag.Parse("File")], made);
        for (int i = 0; i < 7; i++) Step([]);
        var start = made.AddMilliseconds(3_700);
        mgr.ApplyCapture(w.History, inc, new CaptureResult { PoolEventsParsed = true, Tags = [PoolTag.Parse("File")] }.Recorded(At(start), At(start.AddMinutes(1))));
        return (mgr, host, inc, made, start, Step, Finish);
    }

    [Fact]
    public void AClosedIncidentsWriteForALateEventThatFailsIsNotTriedAgainAtEverySample()
    {
        // The write a closed incident is due when its window took an event in is made once. When it fails (a file is where
        // its folder was: nothing can be written into it), that is logged, its folder counts as behind, and the write is
        // not tried again at each sample after, as with any other write of a folder that fails. Two minutes after its
        // window's end the write for a file that lacks a late event is due, once: it fails too. A closed incident's
        // failed write is said once, with what PoolWatch does about it (it tries again a minute on, and a minute after
        // each try that fails: IncidentRewriteTests counts the tries), however many fail after it: one line in the
        // eight minutes here. (Before, each write that failed was logged: two lines here, and one more at every
        // change of the incident.)
        var (_, host, inc, made, _, step, _) = QuietTrace();
        Directory.Delete(inc.Dir, true);
        File.WriteAllText(inc.Dir, "");
        bool Failed(string log) => log.StartsWith($"incident {inc.Id}: write failed:", StringComparison.Ordinal);
        Assert.DoesNotContain(host.Logs, Failed);
        step([Rollup(made, 50.5, 12)]);
        Assert.EndsWith(" (while PoolWatch holds the incident it tries again, also as it stops; this is said once until the folder is written)", Assert.Single(host.Logs, Failed));
        Assert.True(inc.WriteFailed);
        for (int i = 0; i < 3; i++) step([]);
        Assert.Single(host.Logs, Failed);
        for (int i = 0; i < 12; i++) step([]); // to 3 minutes and 16 s after the trace's end
        Assert.Single(host.Logs, Failed);
        for (int i = 0; i < 30; i++) step([]);
        Assert.Single(host.Logs, Failed);
        Assert.True(inc.WriteFailed);
        Assert.DoesNotContain(host.Logs, l => l.Contains("its folder is written again", StringComparison.Ordinal));
    }

    [Fact]
    public void AFolderWhoseRowsCannotBeWrittenIsSaidOnceUntilTheyAreWrittenAgain()
    {
        // The same folder, gone and a file in its place, with rows of its events.csv waiting (the incident holds events
        // its file did not have as it was last written): each sample tries them again, and each try fails. The log says
        // so once, not at every sample (360 lines an hour), and once more when a write of the rows is made again (the
        // file removed: the folder can be made again).
        var (_, host, inc, made, _, step, _) = QuietTrace();
        Directory.Delete(inc.Dir, true);
        File.WriteAllText(inc.Dir, "");
        bool RowsFailed(string log) => log.StartsWith($"incident {inc.Id}: events.csv write failed:", StringComparison.Ordinal);
        string again = $"incident {inc.Id}: events.csv is written again";
        step([Rollup(made, 50.5, 12)]);
        for (int i = 0; i < 30; i++) step([]);
        Assert.EndsWith(" (its rows are tried again at each sample; this is said once until they are written)", Assert.Single(host.Logs, RowsFailed));
        Assert.DoesNotContain(again, host.Logs);
        File.Delete(inc.Dir);
        for (int i = 0; i < 6; i++) step([]);
        Assert.Single(host.Logs, RowsFailed);
        Assert.Single(host.Logs, l => l == again);
        Assert.Contains((made.AddSeconds(50.5), 70_012, "Rollup"), EventRows(inc.Dir));
        // With nothing waiting, nothing is tried and nothing said, whatever the folder is. When rows wait again (another
        // late event of its window) and cannot be written, that is said again, once.
        Directory.Delete(inc.Dir, true);
        File.WriteAllText(inc.Dir, "");
        for (int i = 0; i < 12; i++) step([]);
        Assert.Single(host.Logs, RowsFailed);
        step([Rollup(made, 55, 7)]);
        for (int i = 0; i < 12; i++) step([]);
        Assert.Equal(2, host.Logs.Count(RowsFailed));
        Assert.Single(host.Logs, l => l == again);
    }

    [Fact]
    public void AClosedIncidentsRowThatCouldNotBeWrittenWhileAnotherProgramHeldItsFileIsTriedAgain()
    {
        // The trace's incident again, its folder there, but another program holds its events.csv open, sharing nothing,
        // as a late rollup of its window comes: the write for it fails (logged as any write of a folder that fails), and
        // the rollup's row still waits. It is tried again at each sample from 30 s after the folder's last write, the
        // log saying so once; when the file is let go the row is written, with the report, and the log says so once.
        // (Before, a row whose write failed no longer counted as waiting: nothing tried it again until the write two
        // minutes after the window's end.)
        var (mgr, host, inc, made, _, step, _) = QuietTrace();
        int rows = EventRows(inc.Dir).Count;
        bool Failed(string log) => log.StartsWith($"incident {inc.Id}: write failed:", StringComparison.Ordinal);
        bool RowsFailed(string log) => log.StartsWith($"incident {inc.Id}: events.csv write failed:", StringComparison.Ordinal);
        string again = $"incident {inc.Id}: events.csv is written again";
        using (new FileStream(Path.Combine(inc.Dir, "events.csv"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            step([Rollup(made, 50.5, 12)]); // 10 s after the folder's last write
            Assert.Single(host.Logs, Failed);
            Assert.DoesNotContain(host.Logs, RowsFailed);
            step([]);
            step([]); // 30 s after it: the row is tried again
            Assert.EndsWith(" (its rows are tried again at each sample; this is said once until they are written)", Assert.Single(host.Logs, RowsFailed));
            step([]);
            Assert.Single(host.Logs, RowsFailed);
            Assert.DoesNotContain(again, host.Logs);
        }
        Assert.Equal(rows, EventRows(inc.Dir).Count);
        Assert.True(mgr.LateEventMargin > TimeSpan.FromSeconds(60)); // the write two minutes after the window's end is not due yet
        step([]);
        Assert.Contains((made.AddSeconds(50.5), 70_012, "Rollup"), EventRows(inc.Dir));
        Assert.Equal([70_012], ListedPids(inc));
        Assert.Single(host.Logs, l => l == again);
        Assert.Single(host.Logs, RowsFailed);
        Assert.Single(host.Logs, Failed);
    }

    [Fact]
    public void AnEventOfAClosedIncidentsWindowThatComesAfterTheHistoryMovedOnLeavesItsChartsCsvsAsTheyWere()
    {
        // An event can come long after its time (a process PoolWatch first sees in a snapshot is timed at its start). One
        // of a closed incident's window that comes 80 minutes on, when the raw history (70 minutes) no longer holds the
        // window's samples: its row and the report that lists it are written, and timeline.csv and processes.csv, which
        // are made from the history's samples, stay as the trace's attaching wrote them (a full write would make them
        // again from samples that are no longer the window's).
        var (_, _, inc, made, _, step, _) = QuietTrace();
        string timeline = File.ReadAllText(Path.Combine(inc.Dir, "timeline.csv")), processes = File.ReadAllText(Path.Combine(inc.Dir, "processes.csv"));
        Assert.True(timeline.Split('\n').Length > 8, timeline);
        int rows = EventRows(inc.Dir).Count;
        for (int i = 0; i < 480; i++) step([]);
        Assert.Equal(rows, EventRows(inc.Dir).Count);
        step([Rollup(made, 50.5, 12)]);
        Assert.Contains((made.AddSeconds(50.5), 70_012, "Rollup"), EventRows(inc.Dir));
        Assert.Equal([70_012], ListedPids(inc));
        Assert.Equal(timeline, File.ReadAllText(Path.Combine(inc.Dir, "timeline.csv")));
        Assert.Equal(processes, File.ReadAllText(Path.Combine(inc.Dir, "processes.csv")));
    }

    [Fact]
    public void AnIncidentClosedWhileItStillRoseTakesItsWindowsLastMinuteAsItComesAndCountsNoLaterEventAsLeftOut()
    {
        // A growth incident closed at its maximum duration (5 minutes here; 2 hours by default) while its tag still rose:
        // its window ends a minute after its last rise, a minute after its closing write. 5 events a sample go on coming.
        // Those of that minute are its window's: it takes them as they come, and its report and their rows are written
        // together. The events after that minute are not its window's, ever: 100 minutes on, when the buffer has let
        // them go, and at the end of the run, its events.csv is as it was a minute after it closed, with no Gap row.
        // (Before, a closed incident took events only when it was written: its window's last minute was never taken,
        // and when the buffer had let events go that it had not looked at, the end of the run counted every one of them
        // as left out of its window, the first of them being in it: "335 events left out", of which 30 were its
        // window's. A 30 s trace's incident whose window then ended 20 s after its last write said "300 events left out",
        // of which 10 were.)
        Directory.CreateDirectory(_dir);
        var settings = new Settings { IncidentMaxMinutes = 5 };
        var w = new World { Interval = TimeSpan.FromSeconds(10) };
        var mgr = new IncidentManager(settings, new TagKnowledge(null, null), new CountingHost(_dir), w.Registry) { Budget = new WriteBudget(1L << 30, 1L << 30) };
        var det = new Detector(settings);
        w.AddProcess(4, 0, "System");
        var file = PoolTag.Parse("File");
        long tag = 100 * Settings.MB;
        int pid = 9000;
        var times = new Dictionary<int, DateTime>();
        DateTime? onset = null;
        void Step(bool rises, bool alert)
        {
            if (rises) tag += 4 * Settings.MB;
            w.Sample(("File", tag));
            var now = w.Now - w.Interval;
            // A start every 2 s, the last a second before the sample.
            var events = Enumerable.Range(0, 5).Select(k => new LifecycleEvent { TimeUtc = now.AddSeconds(-9 + 2 * k), Kind = LifecycleKind.Start, Pid = pid++, CreateTime = 1 + pid, Image = "x.exe" }).ToList();
            foreach (var e in events) times[e.Pid] = e.TimeUtc;
            mgr.AddEvents(events, At(now));
            // The 1-minute rule, as the detector would fire it at every sample while the tag rises: its onset a minute before it first fired.
            onset ??= alert ? now.AddMinutes(-1) : null;
            Alert[] alerts = alert ? [new Alert(AlertKind.Growth, file, now, "grew > 20 MB in 1 min", 24 * Settings.MB, TimeSpan.FromMinutes(1), onset!.Value) { Ms = At(now).Ms, OnsetMs = At(onset.Value).Ms }] : [];
            mgr.Process(w.History, det, alerts, At(now));
        }
        for (int i = 0; i < 24; i++) Step(rises: false, alert: false);
        for (int i = 0; i < 6; i++) Step(rises: true, alert: false);
        Incident? inc = null;
        var from = default(DateTime); // where its window started when it opened
        for (int i = 0; i < 40 && mgr.Closed.Count == 0; i++)
        {
            Step(rises: true, alert: true);
            if (inc is null) { inc = mgr.Open.Single(); from = inc.OnsetUtc.AddMinutes(-2); }
        }
        Assert.Same(inc, Assert.Single(mgr.Closed));
        var closed = inc!.ClosedUtc!.Value;
        Assert.Contains(inc.Timeline, t => t.Text == "closed: open for the maximum 5 min");
        Assert.Equal((closed, (DateTime?)null), (inc.LastRiseUtc, inc.EndUtc)); // it rose to the sample it closed at
        long endMs = IncidentManager.EventWindowEndMs(inc, 0);
        Assert.Equal(At(closed).Ms + 60_000, endMs);
        // Its start was measured a sample later as it grew, and its first part followed it at its close: its file is
        // written from the window's start as its report gives it, and the report has nothing to say of rows from before it.
        Assert.Equal(from.AddSeconds(10), inc.OnsetUtc.AddMinutes(-2));
        from = inc.OnsetUtc.AddMinutes(-2);
        static string line(int rows) => $"({rows} other events in events.csv)";

        // 70 s on: the events of its window's last minute (to the one a second before its end) are in its file with the
        // rest, each once, none from after its end, and report.md counts them.
        for (int i = 0; i < 7; i++) Step(rises: false, alert: false);
        var rows = times.Where(kv => kv.Value >= from && At(kv.Value).Ms <= endMs).Select(kv => kv.Key).Order().ToList();
        Assert.Equal(closed.AddSeconds(59), times[rows[^1]]);
        Assert.Equal(30, rows.Count(p => times[p] > closed));
        Assert.Equal(rows, EventRows(inc.Dir).Select(r => r.Pid).Order());
        Assert.Equal(line(rows.Count), EventsLine(inc));
        // (The sample after that minute brought no event of its window, and the incident did not look at its events: those
        // from after its window's end it takes when it is written, and before the buffer lets them go.)
        Assert.Equal(rows, inc.Events.Select(e => e.Pid).Order());

        // 100 minutes on, the buffer (90 minutes) has let go of events the incident's window never held. Nothing of its
        // folder is written for them, and nothing of it says otherwise, then or at the end of the run.
        string file70 = File.ReadAllText(Path.Combine(inc.Dir, "events.csv"));
        long wrote = inc.LastWriteMs;
        for (int i = 0; i < 600; i++) Step(rises: false, alert: false);
        Assert.True(mgr.EventCount < 5 * 545, $"{mgr.EventCount} events held"); // it lets them go after 90 minutes
        Assert.Contains(inc, mgr.Closed);
        Assert.Equal(wrote, inc.LastWriteMs);
        mgr.Finish(w.History, w.Now);
        Assert.Equal(file70, File.ReadAllText(Path.Combine(inc.Dir, "events.csv")));
        Assert.DoesNotContain(",Gap,", file70);
        Assert.Equal(line(rows.Count), EventsLine(inc));
    }

    [Fact]
    public void AClosedIncidentThatNothingMadeLookAgainLooksAtEachEventBeforeTheBufferLetsItGo()
    {
        // The same growth, closed at its maximum duration while it still rose, on a PC that is silent from its close until
        // 3 minutes after it: no event of its window's last minute comes, so nothing has the incident look at the buffer
        // after its closing write (no late event, and so no write two minutes after its window's end either). Then 5
        // events a sample for 100 minutes, and the end of the run. None of those events is its window's. Before the
        // buffer lets the first of them go, 90 minutes on, the incident looks at them (IncidentManager.DropEvents), each
        // by its own time: they are after its window's end, and its events.csv is as its close wrote it, with no Gap row.
        // (Without that look, the end of the run found that the buffer had let go of events the incident had not looked
        // at, and counted as many as went as left out of its window: "300 events left out", none of them its window's.)
        Directory.CreateDirectory(_dir);
        var settings = new Settings { IncidentMaxMinutes = 5 };
        var w = new World { Interval = TimeSpan.FromSeconds(10) };
        var mgr = new IncidentManager(settings, new TagKnowledge(null, null), new CountingHost(_dir), w.Registry) { Budget = new WriteBudget(1L << 30, 1L << 30) };
        var det = new Detector(settings);
        w.AddProcess(4, 0, "System");
        var file = PoolTag.Parse("File");
        long tag = 100 * Settings.MB;
        int pid = 9000;
        DateTime? onset = null;
        void Step(bool rises, bool alert, bool events = true)
        {
            if (rises) tag += 4 * Settings.MB;
            w.Sample(("File", tag));
            var now = w.Now - w.Interval;
            if (events)
                mgr.AddEvents(Enumerable.Range(0, 5).Select(k => new LifecycleEvent { TimeUtc = now.AddSeconds(-9 + 2 * k), Kind = LifecycleKind.Start, Pid = pid++, CreateTime = 1 + pid, Image = "x.exe" }).ToList(), At(now));
            onset ??= alert ? now.AddMinutes(-1) : null;
            Alert[] alerts = alert ? [new Alert(AlertKind.Growth, file, now, "grew > 20 MB in 1 min", 24 * Settings.MB, TimeSpan.FromMinutes(1), onset!.Value) { Ms = At(now).Ms, OnsetMs = At(onset.Value).Ms }] : [];
            mgr.Process(w.History, det, alerts, At(now));
        }
        for (int i = 0; i < 24; i++) Step(rises: false, alert: false);
        for (int i = 0; i < 6; i++) Step(rises: true, alert: false);
        for (int i = 0; i < 40 && mgr.Closed.Count == 0; i++) Step(rises: true, alert: true);
        var inc = Assert.Single(mgr.Closed);
        Assert.Contains(inc.Timeline, t => t.Text == "closed: open for the maximum 5 min");
        string path = Path.Combine(inc.Dir, "events.csv");
        string atClose = File.ReadAllText(path);
        long wrote = inc.LastWriteMs;
        for (int i = 0; i < 18; i++) Step(rises: false, alert: false, events: false); // 3 silent minutes: past its window's end and the 2 minutes after it
        Assert.Equal(wrote, inc.LastWriteMs);
        for (int i = 0; i < 600; i++) Step(rises: false, alert: false); // 100 minutes of events after its window
        Assert.True(mgr.EventCount < 5 * 545, $"{mgr.EventCount} events held"); // the buffer let the first of them go
        Assert.Contains(inc, mgr.Closed);
        Assert.Equal(wrote, inc.LastWriteMs);
        mgr.Finish(w.History, w.Now);
        string atEnd = File.ReadAllText(path);
        Assert.DoesNotContain(",Gap,", atEnd);
        Assert.Equal(atClose, atEnd);
    }

    [Fact]
    public void AnOpenIncidentsFileStartsAgainWhenALateEventTakesTheLastRowsPlaceInItsFirstPart()
    {
        // A quiet PC. The incident's window starts at 22:00:00; 500 events have come and its file holds all of them: the
        // first part is full and nothing is left out yet, so the file has no Note row. The next sample brings two late
        // rollups (timed back among the 500, as the summarizer hands them over) and ten new events. The first part is the
        // window's earliest 500 by time: the two rollups take their places in it, and its last two events move on to the
        // latest part. The file is written again whole from the first part: its 500 rows, in time order, none of them
        // after its Note row's time, and the Note row and report.md say 500. (Before, the two rollups were appended to
        // the file with its 500 rows: 502 event rows, a Note row "the first 502 events ... to this row's time" at the
        // time of an event before the file's last two rows, and report.md "the window's first 502 events".)
        var mgr = new IncidentManager(new Settings(), new TagKnowledge(null, null), new FakeHost(_dir), new ProcessRegistry());
        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        var inc = GrowthIncident("late-in-first");
        var now = At(Start.AddSeconds(60));
        mgr.AddEvents(Enumerable.Range(0, 500).Select(Ev), now);
        writer.AppendEvents(inc, mgr.IncidentEvents(inc, now.Ms, forReports: false), maxRows: 500);
        Assert.Equal(Enumerable.Range(1000, 500), EventRows(inc.Dir).Select(r => r.Pid));
        string[] Lines() => File.ReadAllLines(Path.Combine(inc.Dir, "events.csv"));
        Assert.DoesNotContain(Lines(), l => l.Contains(",Note,"));

        LifecycleEvent Late(double at, int pid) => new() { TimeUtc = Start.AddSeconds(at), Kind = LifecycleKind.Rollup, Pid = pid, CreateTime = 1, Image = "bash.exe" };
        var next = At(Start.AddSeconds(70));
        mgr.AddEvents(new[] { Late(44.05, 90_001), Late(44.15, 90_002) }.Concat(Enumerable.Range(500, 10).Select(Ev)), next);
        writer.Write(new History(), inc, mgr.IncidentEvents(inc, next.Ms), maxRows: 100, full: false, must: false);
        var rows = EventRows(inc.Dir);
        // Events 0 to 440, the rollup timed between 440 and 441, 441, the rollup between 441 and 442, then 442 to 497.
        var expected = Enumerable.Range(1000, 441).Append(90_001).Append(1441).Append(90_002).Concat(Enumerable.Range(1442, 56)).ToList();
        Assert.Equal(IncidentWriter.MaxEventRows, expected.Count);
        Assert.Equal(expected, rows.Select(r => r.Pid));
        Assert.Equal(rows.OrderBy(r => r.Time).ToList(), rows);
        var note = PoolWatch.Core.Util.Csv.Split(Lines()[^1]);
        Assert.Equal(("Note", Iso(Ev(498).TimeUtc)), (note[1], note[0])); // the first event the first part leaves out
        Assert.All(rows, r => Assert.True(r.Time < Ev(498).TimeUtc));
        Assert.Equal($"events.csv holds the first 500 events from {Iso(Start)}, to this row's time, as PoolWatch had them when it wrote this row; later events are not in this file ({AtClose})", note[9]);
        // report.md lists the two rollups (it lists a window's events but for the starts) and counts the file's other 498
        // rows; 12 events are not in the file: the two that moved on, and the ten that came after.
        Assert.Equal($"(498 other events in events.csv: while the incident is open it holds the window's first 500 events, to {Fmt.Clock(Ev(498).TimeUtc)}, " +
                     $"as PoolWatch had them when it wrote them; {12:N0} more have come since, and {AtClose})", EventsLine(inc));
    }

    [Fact]
    public void AClosedIncidentsFileWithItsFirstPartAloneIsInTimeOrder()
    {
        // A short incident: 100 events, all in its file. A late rollup, timed among them, comes while it is open (an open
        // incident's file is in the order PoolWatch added its rows), and the incident closes with 101 events: no latest
        // part, so its file is not replaced with both ends. Its closing write, a write that must be made, writes the
        // file whole all the same, in time order: the rollup's row in its place. (Before, the rollup's row was appended
        // after the row of 22:00:09.9 and stayed there, while the docs said a closed incident's file is sorted by time.)
        var mgr = new IncidentManager(new Settings(), new TagKnowledge(null, null), new FakeHost(_dir), new ProcessRegistry());
        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        var inc = GrowthIncident("short");
        var h = new History();
        mgr.AddEvents(Enumerable.Range(0, 100).Select(Ev), At(Start.AddSeconds(20)));
        writer.Write(h, inc, mgr.IncidentEvents(inc, At(Start.AddSeconds(20)).Ms), maxRows: 500, full: false, must: false);
        LifecycleEvent Late(double at, int pid) => new() { TimeUtc = Start.AddSeconds(at), Kind = LifecycleKind.Rollup, Pid = pid, CreateTime = 1, Image = "bash.exe" };
        mgr.AddEvents([Late(5.05, 90_001)], At(Start.AddSeconds(30)));
        CloseWith(inc, 99, 12);
        long closing = At(Start.AddMinutes(12)).Ms;
        writer.Write(h, inc, mgr.IncidentEvents(inc, closing)); // its closing write: one that must be made, every row
        var inOrder = Enumerable.Range(1000, 51).Append(90_001).Concat(Enumerable.Range(1051, 49)).ToList();
        Assert.Equal(inOrder, EventRows(inc.Dir).Select(r => r.Pid));
        Assert.DoesNotContain(File.ReadAllLines(Path.Combine(inc.Dir, "events.csv")), l => l.Contains(",Gap,") || l.Contains(",Note,"));
        Assert.False(writer.LacksEvents(inc, mgr.IncidentEvents(inc, closing, forReports: false)));
        Assert.Equal("(100 other events in events.csv)", EventsLine(inc)); // the rollup is listed above the line

        // A late event of its window that comes after its close: a write that can wait adds its row at the end of the
        // file, after later rows, with the report that lists it; the file then lacks its time order, and the next write
        // that must be made (two minutes after the window's end, the end of the run, or as the incident leaves memory)
        // writes it whole again, in time order.
        mgr.AddEvents([Late(7.05, 90_002)], At(Start.AddMinutes(12).AddSeconds(10)));
        long later = At(Start.AddMinutes(12).AddSeconds(10)).Ms;
        writer.Write(h, inc, mgr.IncidentEvents(inc, later), maxRows: 100, full: false, must: false);
        Assert.Equal(inOrder.Append(90_002), EventRows(inc.Dir).Select(r => r.Pid));
        Assert.True(writer.LacksEvents(inc, mgr.IncidentEvents(inc, later, forReports: false)));
        Assert.True(writer.OutOfOrder(inc, mgr.IncidentEvents(inc, later, forReports: false)));
        writer.Write(h, inc, mgr.IncidentEvents(inc, later), maxRows: IncidentWriter.MaxEventRows, full: false, must: true);
        Assert.Equal(Enumerable.Range(1000, 51).Append(90_001).Concat(Enumerable.Range(1051, 20)).Append(90_002).Concat(Enumerable.Range(1071, 29)), EventRows(inc.Dir).Select(r => r.Pid));
        Assert.False(writer.LacksEvents(inc, mgr.IncidentEvents(inc, later, forReports: false)));
        Assert.Equal("(100 other events in events.csv)", EventsLine(inc)); // 102 rows, the two rollups listed
    }

    [Fact]
    public void WithBothEndsWantedTheLatestPartAndItsCountsAreTakenAgainEachTimeTheWindowsEndIsMeasuredSomewhereElse()
    {
        // An open incident with 6,000 events in its window so far: it holds its first 500, its latest 1,000 (500 and the
        // 500 more it holds while open), and has let go of the 4,500 between, counted. Each time its window's end is then
        // measured somewhere else, a look that wants both ends (an export, its close) takes the latest part and the
        // counts again from the events PoolWatch holds, so the latest part is the latest 500 up to the end as it is now
        // and the count is of exactly the events between the two parts, none of them from after the end.
        var mgr = new IncidentManager(new Settings(), new TagKnowledge(null, null), new FakeHost(_dir), new ProcessRegistry());
        var inc = GrowthIncident("refit");
        var now = At(Start.AddMinutes(10));
        mgr.AddEvents(Enumerable.Range(0, 6_000).Select(Ev), now);
        // The window's end, 50 ms after event `last`, its last (the events are 100 ms apart).
        void EndAt(int last) => (inc.EndUtc, inc.EndMs) = (Ev(last).TimeUtc.AddMinutes(-1).AddMilliseconds(50), At(Ev(last).TimeUtc).Ms - 60_000 + 50);
        void Holds(int latestFrom, int last)
        {
            var ev = mgr.IncidentEvents(inc, now.Ms, forReports: false, bothEnds: true);
            Assert.Equal(Enumerable.Range(1000, 500), ev.Kept.Select(e => e.Pid));
            Assert.Equal(Enumerable.Range(1000 + latestFrom, last - latestFrom + 1), ev.Tail.Select(e => e.Pid));
            Assert.Equal((latestFrom - 500, Ev(500).TimeUtc, Ev(latestFrom - 1).TimeUtc), (ev.Gap.Count, ev.Gap.FromUtc, ev.Gap.ToUtc));
            Assert.True(ev.Gap.ToMs <= ev.EndMs);
        }
        Holds(5_500, 5_999); // the window runs to now: its latest 500
        // A little earlier (700 events: more than the 500 it holds for that): it holds 300 events up to the new end,
        // and the 200 before them are among those it let go. Taken again: the latest 500 up to the end.
        EndAt(5_299);
        Holds(4_800, 5_299);
        // Much earlier, before events it has counted as left out (they would be counted as left out of a window that
        // ended before them, and the file would have no latest part).
        EndAt(2_499);
        Holds(2_000, 2_499);
        // Later, once more than 4,000 events came after the end: of those it holds the latest 4,000, and the 2,500
        // before them it set aside, counted together. The end is measured among those: all 2,500 would be counted as
        // left out, the 1,000 from after the end too, and the file would have no latest part.
        now = At(Start.AddMinutes(16));
        mgr.AddEvents(Enumerable.Range(6_000, 3_000).Select(Ev), now);
        Holds(2_000, 2_499);
        EndAt(3_999);
        Holds(3_500, 3_999);
        // Where it was first: the same again (each end is fitted once, and an end measured somewhere else again).
        EndAt(5_299);
        Holds(4_800, 5_299);
    }

    [Fact]
    public void ALateEventsRowIsPutInTimeOrderByTheEndOfTheRunAndAsItsIncidentLeavesMemory()
    {
        // A trace's incident whose file holds its first part alone, and a late rollup of its window: its row is added at
        // the end of the file, after rows of later times. When PoolWatch stops before the write two minutes after the
        // window's end, the end of the run writes the file in time order; and when that write is never due before the
        // incident leaves memory (its margin set past the 6 hours it is held), the file is written in time order as it
        // leaves, its report with it. Nothing else of the file changes: the same rows.
        foreach (bool leaves in new[] { false, true })
        {
            var (mgr, _, inc, made, _, step, finish) = QuietTrace();
            if (leaves) mgr.LateEventMargin = TimeSpan.FromHours(7);
            step([Rollup(made, 50.5, 12)]);
            var appended = EventRows(inc.Dir);
            Assert.Equal((made.AddSeconds(50.5), 70_012, "Rollup"), appended[^1]);
            Assert.NotEqual(appended.OrderBy(r => r.Time).ToList(), appended);
            if (leaves)
            {
                for (int i = 0; i < 6 * 360 + 12 && mgr.Closed.Contains(inc); i++) step([]);
                Assert.DoesNotContain(inc, mgr.Closed);
            }
            else finish([]);
            Assert.Equal(appended.OrderBy(r => r.Time).ToList(), EventRows(inc.Dir));
            Assert.Equal([70_012], ListedPids(inc));
            Assert.Equal($"({appended.Count - 1} other events in events.csv)", EventsLine(inc));
            Directory.Delete(_dir, true);
        }
    }

    [Fact]
    public void ALateEventOfAClosedIncidentsWindowIsInAFileWithBothEndsOnceEveryLateEventCanHaveCome()
    {
        // A busy PC's 5-minute trace: its incident's events.csv holds both ends of its window, replaced whole as the trace
        // was attached. A rollup whose first start was 20 s before the trace's end comes with the next sample (the
        // summarizer hands rollups over up to a minute and a half late): the incident takes it (it is among its latest
        // 500 now, and the earliest of those is left out instead), and the report lists it at once. A file with both
        // ends cannot be appended to, so it stays as it is for now, and the report goes on counting its rows and quoting
        // its Gap row as they are. Two minutes after the window's end, when every late event of the window can have
        // come, one more write that must be made replaces the file with what the incident holds, its report with it: the
        // two agree from then on, and nothing more is written while no more of its window's events come. (Before, the
        // file got the rollup only when PoolWatch stopped or the incident left memory, up to 6 hours later, and until
        // then held one event the report did not list and lacked one it did.)
        var (mgr, inc, times, _, start, end, step, _, finish) = BusyTrace();
        int first = times.FindIndex(t => t >= start), last = times.FindLastIndex(t => t <= end);
        int left = last - first + 1 - 2 * IncidentWriter.MaxEventRows;
        string path = Path.Combine(inc.Dir, "events.csv"), file = File.ReadAllText(path);
        string Line(int other, int leftOut, int lastLeftOut) =>
            $"({other} other events in events.csv; it leaves out {leftOut:N0} events, between {Fmt.Clock(times[first + 500])} and {Fmt.Clock(times[lastLeftOut])}, as its Gap row says)";
        Assert.Equal(Line(1000, left, last - 500), EventsLine(inc));

        var rollup = new LifecycleEvent { TimeUtc = end.AddSeconds(-20), Kind = LifecycleKind.Rollup, Pid = 70_000, Image = "git.exe", Count = 9, Detail = "bash.exe started 9, 9 exited, short-lived" };
        step([rollup]); // the sample 16.4 s after the trace's end
        Assert.Equal([70_000], ListedPids(inc));
        Assert.Equal(file, File.ReadAllText(path));
        Assert.Equal(Line(1000, left, last - 500), EventsLine(inc));
        // Up to 2 minutes after the window's end: the file is as it was.
        var due = end + mgr.LateEventMargin;
        int waited = 0;
        while (times[^1].AddSeconds(10.25) < due) { step([]); waited++; } // the next sample is 10.25 s after the latest event
        Assert.True(waited >= 9, $"{waited} samples before the write");
        Assert.Equal(file, File.ReadAllText(path));

        // The first sample at or after that: both ends as the incident holds them, the rollup among its latest 500 (499
        // starts and the rollup), and the Gap row and the report counting one more left out.
        void Replaced()
        {
            var (head, latest) = EventPidsByEnd(inc.Dir);
            Assert.Equal(Enumerable.Range(500_000 + first, IncidentWriter.MaxEventRows), head);
            Assert.Equal(Enumerable.Range(500_000 + last - IncidentWriter.MaxEventRows + 2, IncidentWriter.MaxEventRows - 1), latest);
            Assert.Contains((rollup.TimeUtc, 70_000, "Rollup"), EventRows(inc.Dir));
            var c = System.Globalization.CultureInfo.InvariantCulture;
            var gap = PoolWatch.Core.Util.Csv.Split(Assert.Single(File.ReadAllLines(path), l => l.Contains(",Gap,")));
            Assert.Equal(string.Create(c, $"{left + 1:N0} events left out, between {Iso(times[first + 500])} and {Iso(times[last - 499])} ") +
                         $"(events.csv keeps the first 500 events from {Iso(start)}, where the incident's window started when this file was written, and the latest 500 up to the window's end)", gap[9]);
            Assert.Equal([70_000], ListedPids(inc));
            Assert.Equal(Line(999, left + 1, last - 499), EventsLine(inc));
        }
        step([]);
        Assert.True(times[^1].AddSeconds(0.25) >= due && times[^1].AddSeconds(-9.75) < due); // this very sample (0.25 s after its latest event) is the first at or after the margin
        Replaced();

        // One more of its window's events comes after that write (a second late rollup): the report lists it at the next
        // sample, and the file gets it by one more such write, no sooner than the margin after the last one (a late
        // event at every sample would otherwise replace the file at every sample).
        string replaced = File.ReadAllText(path);
        var second = rollup with { TimeUtc = end.AddSeconds(-10), Pid = 70_001 };
        step([second]);
        Assert.Equal([70_000, 70_001], ListedPids(inc));
        Assert.Equal(replaced, File.ReadAllText(path));
        int spaced = 0;
        while (File.ReadAllText(path) == replaced && spaced < 30) { step([]); spaced++; }
        Assert.Equal(11, spaced); // 2 minutes of samples 10.25 s apart, from the write before: at its 12th sample, the 11th after the rollup's
        Assert.Contains((rollup.TimeUtc, 70_000, "Rollup"), EventRows(inc.Dir));
        Assert.Contains((second.TimeUtc, 70_001, "Rollup"), EventRows(inc.Dir));
        Assert.Equal(Line(998, left + 2, last - 498), EventsLine(inc));

        // Then no more of its window's events come, and nothing of its folder is written again, to the end of the run.
        string twice = File.ReadAllText(path);
        long wrote = inc.LastWriteMs;
        for (int i = 0; i < 60; i++) step([]);
        Assert.Equal(wrote, inc.LastWriteMs);
        finish();
        Assert.Equal(twice, File.ReadAllText(path));
        Assert.Equal(Line(998, left + 2, last - 498), EventsLine(inc));
    }

    [Fact]
    public void AFileWithBothEndsReplacedAsItsIncidentLeavesMemoryHasItsReportWrittenWithIt()
    {
        // The same trace and the same late rollup, with the write for late events never due before the incident leaves
        // memory (its margin set past the 6 hours it is held: as when that write could not be made). The file with both
        // ends stays as it is, and the report goes on counting its rows and quoting its Gap row as they are. Six hours
        // on the incident leaves memory, and its file is replaced with what it holds: the report is written with it, so
        // the two say the same of the file. (Before, only events.csv was written as an incident left memory: report.md
        // went on saying how many events the file had left out before.)
        var (mgr, inc, times, _, start, end, step, quiet, _) = BusyTrace();
        mgr.LateEventMargin = TimeSpan.FromHours(7);
        int first = times.FindIndex(t => t >= start), last = times.FindLastIndex(t => t <= end);
        int left = last - first + 1 - 2 * IncidentWriter.MaxEventRows;
        string path = Path.Combine(inc.Dir, "events.csv"), file = File.ReadAllText(path);
        string Line(int other, int leftOut, int lastLeftOut) =>
            $"({other} other events in events.csv; it leaves out {leftOut:N0} events, between {Fmt.Clock(times[first + 500])} and {Fmt.Clock(times[lastLeftOut])}, as its Gap row says)";
        Assert.Equal(Line(1000, left, last - 500), EventsLine(inc));

        var rollup = new LifecycleEvent { TimeUtc = end.AddSeconds(-20), Kind = LifecycleKind.Rollup, Pid = 70_000, Image = "git.exe", Count = 9, Detail = "bash.exe started 9, 9 exited, short-lived" };
        step([rollup]);
        Assert.Equal([70_000], ListedPids(inc));
        Assert.Equal(file, File.ReadAllText(path));
        Assert.Equal(Line(1000, left, last - 500), EventsLine(inc));

        // It leaves memory 6 hours after it closed (the trace's end); until then its file is as the trace's attaching wrote it.
        for (int i = 0; i < 360; i++) quiet();
        Assert.Contains(inc, mgr.Closed);
        Assert.Equal(file, File.ReadAllText(path));
        for (int i = 0; i < 6 * 360 && mgr.Closed.Count > 0; i++) quiet();
        Assert.Empty(mgr.Closed);
        var (head, latest) = EventPidsByEnd(inc.Dir);
        Assert.Equal(Enumerable.Range(500_000 + first, IncidentWriter.MaxEventRows), head);
        Assert.Equal(Enumerable.Range(500_000 + last - IncidentWriter.MaxEventRows + 2, IncidentWriter.MaxEventRows - 1), latest); // 499 starts and the rollup
        Assert.Contains((rollup.TimeUtc, 70_000, "Rollup"), EventRows(inc.Dir));
        var c = System.Globalization.CultureInfo.InvariantCulture;
        var gap = PoolWatch.Core.Util.Csv.Split(Assert.Single(File.ReadAllLines(path), l => l.Contains(",Gap,")));
        Assert.StartsWith(string.Create(c, $"{left + 1:N0} events left out, between {Iso(times[first + 500])} and {Iso(times[last - 499])} (events.csv keeps the first 500 events from "), gap[9]);
        Assert.Equal([70_000], ListedPids(inc));
        Assert.Equal(Line(999, left + 1, last - 499), EventsLine(inc));
    }

    [Fact]
    public void AFileToBeReplacedWithBothEndsGetsNoRowForTheBudgetAsItsIncidentLeavesMemory()
    {
        // A closed incident's events.csv holds its 400 events, all it had. 800 more of its window then come (late ones): it
        // has a latest part now, so its file is to be replaced whole with both ends, which the write that also writes its
        // report does (the end of a run, or the manager as the incident leaves memory). The writer's own step for an
        // incident leaving memory writes nothing into such a file: not the last row it adds to a file whose first part
        // the write budget never had room for, which would count the 100 as left out for want of room and leave out the
        // latest part.
        var inc = new Incident
        {
            Id = "x", Dir = Path.Combine(_dir, "x"), Tag = PoolTag.Parse("File"), Kind = IncidentKind.Growth, OnsetUtc = Start.AddMinutes(2), OnsetMs = At(Start.AddMinutes(2)).Ms,
            ClosedUtc = Start.AddMinutes(20), ClosedMs = At(Start.AddMinutes(20)).Ms,
        };
        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        LifecycleEvent Read(int i) => Ev(i) with { Ms = At(Ev(i).TimeUtc).Ms };
        writer.Write(new History(), inc, Enumerable.Range(0, 400).Select(Read).ToList());
        string path = Path.Combine(inc.Dir, "events.csv"), file = File.ReadAllText(path);
        Assert.Equal(Enumerable.Range(1000, 400), EventPids(inc.Dir));
        var more = IncidentEventSet.Of(Enumerable.Range(0, 1_200).Select(Read).ToList());
        Assert.True(writer.BothEndsDue(inc, more));
        Assert.Equal(0, writer.WriteLeaving(inc, more));
        Assert.Equal(file, File.ReadAllText(path));
        // The write that replaces it: both ends, and the 200 between them counted.
        writer.Write(new History(), inc, more);
        var (head, latest) = EventPidsByEnd(inc.Dir);
        Assert.Equal(Enumerable.Range(1000, 500), head);
        Assert.Equal(Enumerable.Range(1000 + 700, 500), latest);
        Assert.Single(File.ReadAllLines(path), l => l.Contains(",Gap,") && l.Contains("200 events left out"));
    }

    [Fact]
    public void AClosedIncidentTheManagerHoldsTakesEveryEventBeforeTheBufferLetsItGoAndCountsEachByItsOwnTime()
    {
        // A closed incident the manager holds, and 25,000 events of its window (timed in the 25 s after its last rise: its
        // window ends a minute after it) that come at once, 5,000 more than the buffer holds: the incident takes them
        // before the buffer lets any go. Its first part is the first 500 of them, its latest part the last 500, and the
        // 24,000 between are counted as left out, from the first to the last of them. (Before, a closed incident looked
        // at the buffer only when it was written: the 5,000 the buffer had let go by then it knew only as a number, which
        // it counted as left out from the first of them, as the buffer had noted it, and its first part, which had no
        // event yet, ended there with none: its file would have held its latest 500 alone.)
        var settings = new Settings { GrowthRules = [new(1, 3 * Settings.MB)], IncidentQuietMinutes = 0.5, RetentionMinutes = 600 };
        var (mgr, w, _, step) = Closing(_dir, settings);
        Incident? inc = null;
        for (int i = 0; i < 200 && inc is null; i++) { step(); inc = mgr.Closed.FirstOrDefault(); }
        Assert.NotNull(inc);
        var t = inc!.LastRiseUtc;
        LifecycleEvent E(int i) => new() { TimeUtc = t.AddMilliseconds(i), Kind = LifecycleKind.Start, Pid = 100_000 + i, CreateTime = 1 + i, Image = "x.exe" };
        mgr.AddEvents(Enumerable.Range(0, 25_000).Select(E), At(w.Now));
        Assert.Equal(20_000, mgr.EventCount);
        var ev = mgr.IncidentEvents(inc, At(w.Now).Ms);
        Assert.Equal(Enumerable.Range(100_000, 500), ev.Kept.Select(e => e.Pid));
        Assert.Equal(Enumerable.Range(100_000 + 24_500, 500), ev.Tail.Select(e => e.Pid));
        Assert.Equal((24_000, E(500).TimeUtc, E(24_499).TimeUtc), (ev.Gap.Count, ev.Gap.FromUtc, ev.Gap.ToUtc));
    }

    [Fact]
    public void AnAlertThatKeepsFiringIsOneRowOfEventsCsv()
    {
        // The 10-minute rule fires every sample while the growth holds, and the incident keeps the bigger alert each time
        // (with the first one's onset): one Alert row, not a row more at every write. A new onset (a re-opening's alert) is
        // a new row.
        var host = new CountingHost(_dir);
        var writer = new IncidentWriter(new TagKnowledge(null, null), host, new ProcessRegistry());
        var t0 = new DateTime(2026, 11, 9, 22, 0, 0, DateTimeKind.Utc);
        var tag = PoolTag.Parse("File");
        var inc = new Incident { Id = "x", Dir = Path.Combine(_dir, "x"), Tag = tag, Kind = IncidentKind.Growth, OnsetUtc = t0, OnsetMs = At(t0).Ms };
        Alert A(int minute, long mb, DateTime onset) => new(AlertKind.Growth, tag, t0.AddMinutes(minute), "grew > 50 MB in 10 min", mb << 20, TimeSpan.FromMinutes(10), onset)
            { Ms = At(t0.AddMinutes(minute)).Ms, OnsetMs = At(onset).Ms };
        var h = new History();
        inc.Alerts.Add(A(10, 60, t0));
        for (int m = 11; m < 20; m++)
        {
            writer.Write(h, inc, []);
            inc.Alerts[0] = A(m, 60 + m, t0); // bigger, same rule, same onset
        }
        writer.Write(h, inc, []);
        int AlertRows() => File.ReadAllLines(Path.Combine(inc.Dir, "events.csv")).Count(l => l.Contains(",Alert,"));
        Assert.Equal(1, AlertRows());
        inc.Alerts[0] = A(80, 70, t0.AddMinutes(70)); // a new episode's alert: its own onset
        writer.Write(h, inc, []);
        Assert.Equal(2, AlertRows());
    }

    /// <summary>A file's growth (the 1-minute rule), a stop, a close; the rule's last alerts are counted after it closed.</summary>
    private static (IncidentManager mgr, World w, Detector det, Action step) Closing(string dir, Settings settings, Action<IncidentManager>? setup = null)
    {
        var w = new World();
        var mgr = new IncidentManager(settings, new TagKnowledge(null, null), new FakeHost(dir), w.Registry) { RewriteInterval = TimeSpan.Zero };
        setup?.Invoke(mgr);
        var det = new Detector(settings);
        w.AddProcess(4, 0, "System");
        w.AddProcess(100, 4, "leaker.exe");
        long file = 50 * Settings.MB;
        bool grow = false;
        void Step()
        {
            if (grow) { w.Io(100, other: 2000, handles: 2000); file += 800_000; }
            w.Sample(("File", file));
            mgr.Process(w.History, det, det.Evaluate(w.History), At(w.Now - w.Interval));
        }
        for (int i = 0; i < 15; i++) Step();
        grow = true;
        for (int i = 0; i < 20; i++) Step();
        grow = false;
        return (mgr, w, det, Step);
    }

    [Fact]
    public void AClosedIncidentsCountedRepeatsAreWrittenHalfAnHourAfterItsLastWriteNotBefore()
    {
        // The rule fires on for a while after the incident closed: the first such alert is a new timeline entry (written at
        // once), the rest are counted on it ("fired 12 more times"); that count reaches the folder half an hour after the
        // folder was last written, not 5 minutes after.
        var settings = new Settings { GrowthRules = [new(1, 3 * Settings.MB)], IncidentQuietMinutes = 0.5, RetentionMinutes = 600 }; // nothing else to write
        var (mgr, w, _, step) = Closing(_dir, settings);
        Incident? inc = null;
        long? countedAt = null, writtenAt = null, lastWrite = null;
        for (int i = 0; i < 1100 && writtenAt is null; i++)
        {
            step();
            inc ??= mgr.Closed.FirstOrDefault();
            if (inc is null) continue;
            if (inc.CountPending && countedAt is null) (countedAt, lastWrite) = (At(w.Now - w.Interval).Ms, inc.LastWriteMs);
            if (countedAt is not null && !inc.CountPending) writtenAt = inc.LastWriteMs;
        }
        Assert.NotNull(countedAt);
        Assert.NotNull(writtenAt);
        long waited = writtenAt!.Value - lastWrite!.Value;
        Assert.InRange(waited, (long)mgr.ClosedRewriteInterval.TotalMilliseconds, (long)mgr.ClosedRewriteInterval.TotalMilliseconds + 10_000);
        Assert.Equal(TimeSpan.FromMinutes(30), mgr.ClosedRewriteInterval);
    }

    [Fact]
    public void WithRoomForOneWriteTheIncidentWrittenLongestAgoGetsIt()
    {
        // Two open incidents both due for a refresh, and the write budget has room for one write: the one whose folder was
        // written longest ago gets it, whichever opened first.
        var settings = new Settings { GrowthRules = [new(1, 3 * Settings.MB)], IncidentQuietMinutes = 600 };
        var w = new World();
        var mgr = new IncidentManager(settings, new TagKnowledge(null, null), new FakeHost(_dir), w.Registry) { RewriteInterval = TimeSpan.Zero, MaxRewriteInterval = TimeSpan.Zero };
        var det = new Detector(settings);
        w.AddProcess(4, 0, "System");
        w.AddProcess(100, 4, "leaker.exe");
        long a = 50 * Settings.MB, b = 50 * Settings.MB;
        void Step() { w.Sample(("AAAA", a), ("BBBB", b)); mgr.Process(w.History, det, det.Evaluate(w.History), At(w.Now - w.Interval)); }
        for (int i = 0; i < 15; i++) Step();
        for (int i = 0; i < 20; i++) { w.Io(100, other: 2000); a += 800_000; Step(); } // A opens
        for (int i = 0; i < 20; i++) { w.Io(100, other: 2000); b += 800_000; Step(); } // then B
        for (int i = 0; i < 60; i++) Step(); // a plateau: no alert, neither is due
        var incA = Assert.Single(mgr.Open, i => i.Tag == PoolTag.Parse("AAAA"));
        var incB = Assert.Single(mgr.Open, i => i.Tag == PoolTag.Parse("BBBB"));
        Assert.Equal([incA, incB], mgr.Open); // A first in the order they opened

        mgr.Budget = new WriteBudget(bytesPerHour: 1, burst: 1); // room for one write
        mgr.Note(incA, w.Now, "a note on A");
        Step();
        Assert.True(incA.LastWriteMs > incB.LastWriteMs); // A was written now: B is the one written longest ago

        mgr.Budget = new WriteBudget(bytesPerHour: 1, burst: 1);
        long aWritten = incA.LastWriteMs;
        mgr.Note(incA, w.Now, "another note on A");
        mgr.Note(incB, w.Now, "a note on B");
        Step();
        Assert.Equal(At(w.Now - w.Interval).Ms, incB.LastWriteMs); // B got the room
        Assert.Equal(aWritten, incA.LastWriteMs);                 // A waits
        Assert.True(incA.Dirty);
    }

    [Fact]
    public void RowsOfEventsCsvWaitWhileAnOpenIncidentsReportIsDue()
    {
        // Two open incidents with events not in their events.csv yet; both reports are due (5 minutes since written) and
        // wait for their next re-rank. The budget's room is not given to the rows meanwhile: it is there for the reports the
        // window reads, written at that re-rank.
        var settings = new Settings { GrowthRules = [new(1, 3 * Settings.MB)], IncidentQuietMinutes = 600 };
        var w = new World();
        var mgr = new IncidentManager(settings, new TagKnowledge(null, null), new FakeHost(_dir), w.Registry) { RewriteInterval = TimeSpan.FromHours(1) };
        var det = new Detector(settings);
        w.AddProcess(4, 0, "System");
        w.AddProcess(100, 4, "leaker.exe");
        long a = 50 * Settings.MB, b = 50 * Settings.MB;
        int pid = 9000;
        void Step()
        {
            var now = w.Now;
            w.Sample(("AAAA", a), ("BBBB", b));
            mgr.AddEvents([new LifecycleEvent { TimeUtc = now, Kind = LifecycleKind.Start, Pid = pid++, CreateTime = now.ToFileTimeUtc(), Image = "x.exe" }], At(now));
            mgr.Process(w.History, det, det.Evaluate(w.History), At(now));
        }
        for (int i = 0; i < 15; i++) Step();
        for (int i = 0; i < 20; i++) { w.Io(100, other: 2000); a += 800_000; b += 800_000; Step(); }
        for (int i = 0; i < 180; i++) Step(); // 6 minutes on: both reports overdue, their next re-rank not yet
        var incA = Assert.Single(mgr.Open, i => i.Tag == PoolTag.Parse("AAAA"));
        var incB = Assert.Single(mgr.Open, i => i.Tag == PoolTag.Parse("BBBB"));
        var rows = (EventPids(incA.Dir).Count, EventPids(incB.Dir).Count);
        // While the reports were current (their first 5 minutes), the rows followed each new event the incidents kept.
        Assert.True(rows.Item1 >= 150 && rows.Item2 >= 150, $"{rows} rows");
        mgr.Budget = new WriteBudget(bytesPerHour: 1, burst: 1); // room for one write
        Step();
        Assert.Equal(0, mgr.Budget.Spent); // no rows took it
        Assert.Equal(rows, (EventPids(incA.Dir).Count, EventPids(incB.Dir).Count));
        mgr.RewriteInterval = TimeSpan.Zero; // the re-rank comes
        mgr.Note(incA, w.Now, "a note on A");
        Step();
        Assert.True(mgr.Budget.Spent > 0);
        Assert.Equal(At(w.Now - w.Interval).Ms, incA.LastWriteMs); // the report got it
    }

    [Fact]
    public void AChartWhoseInputsDidNotChangeIsNotDrawnAgain()
    {
        // A closed incident written twice with nothing new: its chart is drawn once, and timeline.png is not written again.
        var host = new CountingHost(_dir);
        var writer = new IncidentWriter(new TagKnowledge(null, null), host, new ProcessRegistry());
        var w = new World();
        w.AddProcess(4, 0, "System");
        for (int i = 0; i < 30; i++) w.Sample(("File", (50L << 20) + i * 800_000L));
        var inc = new Incident
        {
            Id = "x", Dir = Path.Combine(_dir, "x"), Tag = PoolTag.Parse("File"), Kind = IncidentKind.Growth, OnsetUtc = World.T0, OnsetMs = At(World.T0).Ms,
            ClosedUtc = w.Now, ClosedMs = At(w.Now).Ms,
        };
        writer.Write(w.History, inc, []);
        Assert.Equal(1, host.Renders);
        string png = Path.Combine(inc.Dir, "timeline.png");
        var stamp = File.GetLastWriteTimeUtc(png);
        Thread.Sleep(20);
        writer.Write(w.History, inc, []);
        Assert.Equal(1, host.Renders);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(png));
        w.Sample(("File", 80L << 20)); // a new sample in its window: drawn again
        inc.ClosedUtc = w.Now;
        inc.ClosedMs = At(w.Now).Ms;
        writer.Write(w.History, inc, []);
        Assert.Equal(2, host.Renders);
    }

    [Theory]
    [InlineData("File")]
    [InlineData("MmSt")]
    [InlineData("Ntf")]
    [InlineData("a.b ")]
    public void AnIncidentsFolderNameIsToldFromAFindingReportsName(string tag)
    {
        var t = new DateTime(2026, 11, 9, 23, 13, 14, DateTimeKind.Utc);
        string name = IncidentManager.FolderName(t, PoolTag.Parse(tag));
        Assert.True(IncidentManager.IsFolderName(name), name);
        Assert.True(IncidentManager.IsFolderName(name + "-2"), name + "-2"); // two in one second
        Assert.True(IncidentManager.IsFolderName(name + "-13"));
        // A tag with no letter or digit at all (its file-safe form is its hex alone).
        string bare = IncidentManager.FolderName(t, PoolTag.FromBytes([0x01, 0x2E, 0x20, 0x7F]));
        Assert.True(IncidentManager.IsFolderName(bare), bare);
        // A finding report's folder (a spawn storm, a crash, an experiment) is not an incident's.
        var finding = new PoolWatch.Core.Findings.Finding { TimeUtc = t, Kind = PoolWatch.Core.Findings.FindingKind.SpawnStorm, Title = "" };
        Assert.False(IncidentManager.IsFolderName(Path.GetFileName(PoolWatch.Core.Findings.FindingReport.NewDir(_dir, finding, "bash"))));
        Assert.False(IncidentManager.IsFolderName(Path.GetFileName(PoolWatch.Core.Findings.FindingReport.NewDir(_dir, finding, "deadbeef"))));
        Assert.False(IncidentManager.IsFolderName(name + "-x"));
    }
}
