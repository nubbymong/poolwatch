using PoolWatch.Core;
using PoolWatch.Core.Detection;
using PoolWatch.Core.History;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;

using static PoolWatch.Tests.Clocks;

namespace PoolWatch.Tests;

/// <summary>
/// How an incident's folder is written on a busy PC: its events.csv has every event of its window from its first row to
/// its last however far the write budget held it back, an alert that keeps firing is one row, a closed incident's counted
/// repeats wait half an hour, the folder written longest ago gets the room first, and an unchanged chart is not drawn
/// again. The shapes are the owner's (about 26 events every 10 s sample, 2026-11-09).
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

    [Fact]
    public void UnderTheOwnersLoadEventsCsvHoldsEveryEventFromItsFirstRowToItsLast()
    {
        // One growth incident open for 100 minutes on a PC that records 26 events every 10 s sample (9,400 an hour), with
        // the default write budget: the window passes the 5,000 events an incident keeps long before its events.csv has
        // caught up. Its rows are the window's events from its first row on, with none missing between its first row and
        // its last (each event's PID is one more than the one before, so a gap shows as a missing PID); at the end of the
        // run it has all it kept, and a last row says to when.
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
            mgr.AddEvents(events, At(now));
            mgr.Process(w.History, det, det.Evaluate(w.History), At(now));
            host.Settle();
        }
        var inc = Assert.Single(mgr.Open);
        var open = EventPids(inc.Dir);
        Assert.NotEmpty(open);
        Assert.Equal(Enumerable.Range(open.Min(), open.Count), open.Order()); // no event missing between its first row and its last

        mgr.Finish(w.History, w.Now);
        var rows = EventPids(inc.Dir);
        Assert.Equal(Enumerable.Range(rows.Min(), rows.Count), rows.Order());
        // Every event it kept is there (the first 5,000 from the window's start as it first wrote it), less the rows a
        // later onset left before its window.
        Assert.Equal(open.Min() + IncidentWriter.MaxEventRows - 1, rows.Max());
        Assert.True(rows.Min() >= open.Min());
        var last = File.ReadAllLines(Path.Combine(inc.Dir, "events.csv"))[^1];
        Assert.Contains(",Note,", last);
        Assert.Contains("events.csv holds the incident window's events to this row's time", last);
    }

    [Fact]
    public void AClosedIncidentThatLeavesMemoryWithRowsNotWrittenSaysToWhenItsEventsCsvHoldsThem()
    {
        // The write budget never has room after the incident opened: its close writes the first rows (a write that must be
        // made), the rest wait; 6 hours after closing it leaves memory, and its events.csv's last row says to when it holds
        // the window's events (the day's events.csv has the rest).
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
        for (int i = 0; i < 30 + 120 + 60 + 6 * 360 + 30; i++) // the growth, the 10 quiet minutes to its close, 6 hours, a little more
        {
            w.Io(100, other: 500);
            if (i is >= 30 and < 150) tag += 1L << 20;
            w.Sample(("T000", tag));
            var now = w.Now - w.Interval;
            if (i < 200)
                mgr.AddEvents(Enumerable.Range(0, 26).Select(k => new LifecycleEvent
                {
                    TimeUtc = now.AddMilliseconds(k * 300), Kind = LifecycleKind.Start, Pid = pid++, CreateTime = now.ToFileTimeUtc() + k, ParentPid = 100,
                    Image = "node.exe", CommandLine = "node build.js",
                }).ToList(), At(now));
            mgr.Process(w.History, det, det.Evaluate(w.History), At(now));
            if (inc is null && mgr.Open.Count > 0) { inc = mgr.Open.Single(); mgr.Budget = new WriteBudget(bytesPerHour: 1, burst: 1); }
        }
        Assert.NotNull(inc);
        Assert.Empty(mgr.Open);
        Assert.Empty(mgr.Closed); // it left memory
        var lines = File.ReadAllLines(Path.Combine(inc!.Dir, "events.csv"));
        var pids = EventPids(inc.Dir);
        Assert.Equal(Enumerable.Range(pids.Min(), pids.Count), pids.Order());
        Assert.True(pids.Count < 26 * 120, $"{pids.Count} rows"); // not all of them
        Assert.Contains(",Note,", lines[^1]);
        var lastRow = lines.Skip(1).Select(PoolWatch.Core.Util.Csv.Split).Where(c => c[1] == "Start").Max(c => DateTime.Parse(c[0], null, System.Globalization.DateTimeStyles.RoundtripKind));
        Assert.Equal(lastRow, DateTime.Parse(PoolWatch.Core.Util.Csv.Split(lines[^1])[0], null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    [Fact]
    public void AnIncidentKeepsNoEventAfterOnesTheBufferLetGoBeforeItLookedAtThem()
    {
        // A closed incident kept its 100 events; then the shared buffer took in 25,000 more and let the oldest go, among them
        // events the incident had not looked at (it is closed). When it looks again (re-opened, say), it keeps none from
        // that point on, so its events.csv has no gap: it ends where what it kept ends, and says so.
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
        Assert.Equal(100, again.Kept.Count); // none after the point it looked to
        Assert.NotNull(again.CutMs);
        Assert.True(again.Window.Count > 100); // the reports still have the window's events the buffer holds

        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        writer.Write(new History(), inc, again);
        Assert.Equal(Enumerable.Range(1000, 100), EventPids(inc.Dir));
        Assert.Contains(",Note,", File.ReadAllLines(Path.Combine(inc.Dir, "events.csv"))[^1]);
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
