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
        // the default write budget: the window passes the 1,000 events of its first part long before its events.csv has
        // caught up. Its rows are the window's events from its first row on, with none missing between its first row and
        // its last (each event's PID is one more than the one before, so a gap shows as a missing PID); at the end of the
        // run it holds both ends of the window: all of the first part, a Gap row, and the latest 1,000. The incident
        // outlives the raw history (70 minutes): its onset stays where the growth began, so the rows it kept stay in its
        // window. (Before both ends were kept, its file at the end of the run ended with the first part and a Note row.)
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
            host.Settle();
        }
        var inc = Assert.Single(mgr.Open);
        var open = EventPids(inc.Dir);
        Assert.NotEmpty(open);
        Assert.Equal(Enumerable.Range(open.Min(), open.Count), open.Order()); // no event missing between its first row and its last
        Assert.True(open.Max() < open.Min() + IncidentWriter.MaxEventRows); // while open, the first part only

        mgr.Finish(w.History, w.Now);
        var (rows, latest) = EventPidsByEnd(inc.Dir);
        Assert.Equal(Enumerable.Range(rows.Min(), rows.Count), rows.Order());
        // Every event of the first part is there (the first 1,000 from the window's start as it first wrote it), less the
        // rows a later onset left before its window; then the latest 1,000, to the window's last event (its end: a minute
        // after the growth's end as last measured, or after its last rise).
        Assert.Equal(open.Min() + IncidentWriter.MaxEventRows - 1, rows.Max());
        Assert.True(rows.Min() >= open.Min());
        int lastOfWindow = sampled.Where(s => s.Ms <= IncidentManager.EventWindowEndMs(inc, w.History.Ms[^1])).Max(s => s.LastPid);
        Assert.Equal(Enumerable.Range(lastOfWindow - IncidentWriter.MaxEventRows + 1, IncidentWriter.MaxEventRows), latest);
        var lines = File.ReadAllLines(Path.Combine(inc.Dir, "events.csv"));
        Assert.DoesNotContain(lines, l => l.Contains(",Note,"));
        string said = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{latest[0] - rows.Max() - 1:N0} events left out");
        Assert.Single(lines, l => l.Contains(",Gap,") && l.Contains(said)); // every event between the two ends counted
    }

    /// <summary>
    /// A growth incident on a PC that records <paramref name="perSample"/> events every 10 s sample, the write budget with no
    /// room from its opening on: the growth, the 10 quiet minutes to its close, 6 hours, a little more (it leaves memory).
    /// <paramref name="closed"/> is called once, the sample it closes. <c>Sampled</c>: each sample's first PID, how many events it
    /// had and the moment they are read at (they are timed after it, so all at it).
    /// </summary>
    private (Incident Inc, IncidentManager Mgr, List<(int FirstPid, int Count, long Ms)> Sampled) NoRoomUntilItLeaves(int perSample, Action<Incident> closed)
    {
        var sampled = new List<(int FirstPid, int Count, long Ms)>();
        Directory.CreateDirectory(_dir);
        var host = new CountingHost(_dir);
        var w = new World { Interval = TimeSpan.FromSeconds(10) };
        var settings = new Settings { GrowthRules = [new(10, 50 * Settings.MB)] };
        var mgr = new IncidentManager(settings, new TagKnowledge(null, null), host, w.Registry);
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
    public void AClosedIncidentThatLeavesMemoryWithRowsNotWrittenSaysHowManyItsEventsCsvLeavesOut()
    {
        // 4 events a sample: its window has under 1,000 events, so it keeps no latest part. The write budget never has room
        // after it opened: its close writes the first rows (a write that must be made), the rest wait; 6 hours after closing
        // it leaves memory, and a last Gap row says how many of the window's events its events.csv leaves out and between
        // which times (the first and last of those not written). (Before, a Note row said to when the file held them, and
        // that the day's events.csv had the rest.)
        var (inc, _, sampled) = NoRoomUntilItLeaves(4, _ => { });
        var lines = File.ReadAllLines(Path.Combine(inc.Dir, "events.csv"));
        var pids = EventPids(inc.Dir);
        Assert.Equal(Enumerable.Range(pids.Min(), pids.Count), pids.Order());
        var last = PoolWatch.Core.Util.Csv.Split(lines[^1]);
        Assert.Equal("Gap", last[1]);
        Assert.Contains("(PoolWatch's write budget had no room for them before the incident left memory)", last[9]);
        Assert.DoesNotContain(lines, l => l.Contains(",Note,"));
        // The row is at the time of the first event not written, the one after the last written.
        static DateTime T(string s) => DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
        var written = lines.Skip(1).Select(PoolWatch.Core.Util.Csv.Split).Where(c => c[1] == "Start").Select(c => T(c[0])).Max();
        Assert.True(T(last[0]) > written);
        var m = System.Text.RegularExpressions.Regex.Match(last[9], @"^([0-9,]+) events left out, between (\S+) and (\S+) \(");
        Assert.True(m.Success, last[9]);
        int left = int.Parse(m.Groups[1].Value, System.Globalization.NumberStyles.AllowThousands, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(left > 0 && pids.Count + left < IncidentWriter.MaxEventRows, $"{pids.Count} written, {left} left out");
        Assert.Equal(T(last[0]), T(m.Groups[2].Value));
        Assert.True(T(m.Groups[3].Value) >= T(m.Groups[2].Value));
        // Every event of the window from the file's first row to the window's end is in the file or counted in its Gap row.
        long firstMs = sampled.Single(s => pids.Min() >= s.FirstPid && pids.Min() < s.FirstPid + s.Count).Ms;
        int inWindow = sampled.Where(s => s.Ms >= firstMs && s.Ms <= IncidentManager.EventWindowEndMs(inc, 0)).Sum(s => s.Count);
        Assert.Equal(inWindow, pids.Count + left);
    }

    [Fact]
    public void AnIncidentCountsTheEventsTheBufferLetGoBeforeItLookedAtThemAsLeftOut()
    {
        // A closed incident kept its 100 events; then the shared buffer took in 25,000 more and let the oldest go, among them
        // 5,000 events the incident had not looked at (it is closed). When it looks again (re-opened, say), its first part
        // ends where it was, the 5,000 are left out, and from the rest it keeps its window's latest 1,000: its events.csv
        // holds its 100, a Gap row for the 24,000 between (the 5,000 and the 19,000 before its latest), and its latest 1,000.
        // The incident is not one the manager holds, so the first event let go is not known: the row's first time is the
        // last event it looked at, its last time the latest event left out. (Before both ends were kept, it kept none from
        // that point on, and its file ended with its 100 and a Note row.)
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
        Assert.Equal(Enumerable.Range(1000 + 24_100, 1000), again.Tail.Select(e => e.Pid)); // its latest 1,000
        Assert.Equal(24_000, again.Gap.Count);
        Assert.Equal(E(99).TimeUtc, again.Gap.FromUtc);
        Assert.Equal(E(24_099).TimeUtc, again.Gap.ToUtc);
        Assert.True(again.Window.Count > 1100); // the reports still have the window's events the buffer holds

        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        writer.Write(new History(), inc, again);
        var (head, latest) = EventPidsByEnd(inc.Dir);
        Assert.Equal(Enumerable.Range(1000, 100), head);
        Assert.Equal(Enumerable.Range(1000 + 24_100, 1000), latest);
        var lines = File.ReadAllLines(Path.Combine(inc.Dir, "events.csv"));
        Assert.Single(lines, l => l.Contains(",Gap,") && l.Contains("24,000 events left out"));
        Assert.DoesNotContain(lines, l => l.Contains(",Note,"));
    }

    [Fact]
    public void ALatestPartAfterEventsSetAsideHasNoHole()
    {
        // A closed incident within its hour holds the events after its window's end (at most 4,000; the earliest of more are
        // set aside and counted). When its window's end then moves in among those it kept, its latest part is only of the
        // events after those set aside: every event before them is counted as left out with them, so the latest part has no
        // hole and the Gap row's range holds no row of the file.
        var mgr = new IncidentManager(new Settings(), new TagKnowledge(null, null), new FakeHost(_dir), new ProcessRegistry());
        var t0 = new DateTime(2026, 11, 9, 22, 0, 0, DateTimeKind.Utc);
        LifecycleEvent E(int i) => new() { TimeUtc = t0.AddSeconds(i * 0.1), Kind = LifecycleKind.Start, Pid = 1000 + i, CreateTime = 1 + i, Image = "x.exe" };
        // Its window: from t0 (2 minutes before its onset) to t0 + 7 min (a minute after its last rise), events 0 to 4,200;
        // then 6,000 events after its end.
        var inc = new Incident
        {
            Id = "x", Dir = Path.Combine(_dir, "x"), Tag = PoolTag.Parse("File"), Kind = IncidentKind.Growth,
            OnsetUtc = t0.AddMinutes(2), OnsetMs = At(t0.AddMinutes(2)).Ms, LastRiseUtc = t0.AddMinutes(6), LastRiseMs = At(t0.AddMinutes(6)).Ms,
            ClosedUtc = t0.AddMinutes(16), ClosedMs = At(t0.AddMinutes(16)).Ms,
        };
        var now = At(t0.AddMinutes(18));
        mgr.AddEvents(Enumerable.Range(0, 10_201).Select(E), now);
        var before = mgr.IncidentEvents(inc, now.Ms, forReports: false);
        Assert.Equal(Enumerable.Range(1000 + 3_201, 1000), before.Tail.Select(e => e.Pid)); // the latest 1,000 to its end
        Assert.Equal(2_201, before.Gap.Count);

        // Its end moves to event 6,500's time: past the 2,000 set aside (4,201 to 6,200), among the 4,000 kept after them.
        (inc.EndUtc, inc.EndMs) = (E(6_500).TimeUtc.AddMinutes(-1), At(E(6_500).TimeUtc).Ms - 60_000);
        var after = mgr.IncidentEvents(inc, now.Ms, forReports: false);
        Assert.Equal(Enumerable.Range(1000 + 6_201, 300), after.Tail.Select(e => e.Pid)); // only those after the ones set aside, no hole
        Assert.Equal(5_201, after.Gap.Count); // 1,000 to 6,200: every event between the first part and the latest
        Assert.Equal(E(1_000).TimeUtc, after.Gap.FromUtc);
        Assert.Equal(E(6_200).TimeUtc, after.Gap.ToUtc);

        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        writer.Write(new History(), inc, after);
        var (head, latest) = EventPidsByEnd(inc.Dir);
        Assert.Equal(Enumerable.Range(1000, 1000), head);
        Assert.Equal(Enumerable.Range(1000 + 6_201, 300), latest);
        Assert.Single(File.ReadAllLines(Path.Combine(inc.Dir, "events.csv")), l => l.Contains(",Gap,") && l.Contains("5,201 events left out"));
    }

    [Fact]
    public void ALateEventAmongThoseLeftOutIsCountedWithThemSoTheLatestPartFollowsThem()
    {
        // An open incident's latest part has let go of its earliest events (counted). A late event (a rollup timed at its first
        // start) then falls among those: it is counted with them, not held at the front of the latest part. Its window's end is
        // then measured earlier than the latest part's slack covers, so the latest part is fewer than 1,000: it still starts
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
        Assert.Equal(1_000, first.Kept.Count);
        // Its latest part held 2,000 (events 1,000 to 2,999) and let go of the earliest 500 (1,000 to 1,499), keeping the latest
        // 1,000 and 500 more while it is open.
        Assert.Equal(Enumerable.Range(1000 + 2_000, 1000), first.Tail.Select(e => e.Pid));
        // Late: timed among those it let go (between events 1,200 and 1,201).
        var late = new LifecycleEvent { TimeUtc = t0.AddSeconds(120.05), Kind = LifecycleKind.Rollup, Pid = 99_999, CreateTime = 1, Image = "bash.exe" };
        mgr.AddEvents([late], At(t0.AddMinutes(5).AddSeconds(10)));
        // Its end is measured at event 1,999's time: 500 of the latest part are in its window now.
        (inc.EndUtc, inc.EndMs) = (E(1_999).TimeUtc.AddMinutes(-1), At(E(1_999).TimeUtc).Ms - 60_000);
        var ev = mgr.IncidentEvents(inc, At(t0.AddMinutes(5).AddSeconds(10)).Ms, forReports: false);
        Assert.DoesNotContain(ev.Tail, e => e.Pid == late.Pid);
        Assert.All(ev.Tail, e => Assert.True(e.Ms > ev.Gap.ToMs, $"{e.Pid} at {e.Ms} inside the Gap row's range (to {ev.Gap.ToMs})"));
        Assert.Equal(Enumerable.Range(1000 + 1_500, 500), ev.Tail.Select(e => e.Pid));
        Assert.Equal(501, ev.Gap.Count); // the 500 it let go, and the late one counted with them
        Assert.Equal(E(1_499).TimeUtc, ev.Gap.ToUtc);
    }

    [Fact]
    public void AHeldIncidentCountsTheEventsTheBufferLetGoFromTheFirstOfThem()
    {
        // The same with an incident the manager holds (closed, so it does not look at the buffer until it is written): the
        // buffer notes the first event it lets go that the incident had not looked at, so the Gap row starts there.
        var settings = new Settings { GrowthRules = [new(1, 3 * Settings.MB)], IncidentQuietMinutes = 0.5, RetentionMinutes = 600 };
        var (mgr, w, _, step) = Closing(_dir, settings);
        Incident? inc = null;
        for (int i = 0; i < 200 && inc is null; i++) { step(); inc = mgr.Closed.FirstOrDefault(); }
        Assert.NotNull(inc);
        var t = inc!.LastRiseUtc; // events of its window (to a minute after its last rise), come late
        LifecycleEvent E(int i) => new() { TimeUtc = t.AddMilliseconds(i), Kind = LifecycleKind.Start, Pid = 100_000 + i, CreateTime = 1 + i, Image = "x.exe" };
        mgr.AddEvents(Enumerable.Range(0, 25_000).Select(E), At(w.Now));
        var ev = mgr.IncidentEvents(inc, At(w.Now).Ms);
        Assert.True(ev.Gap.Count >= 5_000, $"{ev.Gap.Count} left out");
        Assert.Equal(E(0).TimeUtc, ev.Gap.FromUtc); // the first event let go, not the last it looked at
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
