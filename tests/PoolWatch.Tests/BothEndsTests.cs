using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using PoolWatch.Core;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;
using PoolWatch.Core.Util;
using Xunit.Abstractions;

using static PoolWatch.Tests.Clocks;

namespace PoolWatch.Tests;

/// <summary>
/// An incident's events.csv holds both ends of its window (the owner's decision, 2026-11-10): its first 1,000 events by time
/// from the onset less 2 minutes, a <c>Gap</c> row saying how many it left out and between which times (when it left any
/// out), and its latest 1,000, sorted by time, each event once, late events in their places. While the incident is open the
/// file holds the first part only, appended to as before; when it closes, at the end of a run, and as it leaves memory, the
/// file is replaced whole with both ends, once per close; a re-opened incident that closes again writes it again; Export
/// report of an open incident takes both ends from memory, with a report.md that counts them; a closed incident lets go of
/// its latest part once it can no longer re-open. What the latest parts hold and what the closing writes cost are measured
/// here. The load is the owner's: 26 events every 10 s sample, 9,400 an hour.
/// </summary>
[Collection(FootprintCollection.Name)]
public sealed class BothEndsTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pw-bothends-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>A row of an events.csv: its time, kind, PID and detail.</summary>
    private sealed record Row(DateTime Time, string Kind, int Pid, string Detail);

    private static DateTime Time(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static List<Row> RowsOf(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1)
        .Select(l => Csv.Split(l.TrimEnd('\r'))).Select(c => new Row(Time(c[0]), c[1], int.Parse(c[2], CultureInfo.InvariantCulture), c[9])).ToList();

    private static string EventsCsv(Incident inc) => File.ReadAllText(Path.Combine(inc.Dir, "events.csv"));

    /// <summary>report.md's line under the window's events ("(N other events ..."), or "" when it has none.</summary>
    private static string EventsLine(Incident inc) =>
        File.ReadAllLines(Path.Combine(inc.Dir, "report.md")).FirstOrDefault(l => l.StartsWith('(') && l.Contains(" other events in ")) ?? "";

    /// <summary>
    /// The file's two ends: the PIDs of its event rows before its <c>Gap</c> row and after it, and the row itself (null:
    /// none, every row is in the first list). At most one row is a <c>Gap</c> row, no event is in it twice, each end is a
    /// run of PIDs one more than the one before (every event between its first row and its last), and, written whole
    /// (<paramref name="whole"/>), its rows are in time order (appended to, each append is: an alert's row is where the
    /// file first saw it).
    /// </summary>
    private static (List<int> First, Row? Gap, List<int> Latest) Ends(string text, bool whole = true)
    {
        var rows = RowsOf(text);
        if (whole) Assert.True(rows.Zip(rows.Skip(1)).All(p => p.First.Time <= p.Second.Time), "the rows are in time order");
        var gaps = rows.Where(r => r.Kind == "Gap").ToList();
        Assert.True(gaps.Count <= 1, $"{gaps.Count} Gap rows");
        int at = gaps.Count == 1 ? rows.IndexOf(gaps[0]) : rows.Count;
        var first = rows.Take(at).Where(r => r.Kind == "Start").Select(r => r.Pid).ToList();
        var latest = rows.Skip(at + 1).Where(r => r.Kind == "Start").Select(r => r.Pid).ToList();
        var all = first.Concat(latest).ToList();
        Assert.Equal(all.Count, all.Distinct().Count()); // no event twice
        foreach (var part in new[] { first, latest })
            if (part.Count > 0) Assert.Equal(Enumerable.Range(part[0], part.Count), part);
        return (first, gaps.FirstOrDefault(), latest);
    }

    private static readonly Regex GapDetail = new(@"^([0-9,]+) events left out, between (\S+) and (\S+) \(", RegexOptions.CultureInvariant);

    /// <summary>What a <c>Gap</c> row says: how many events were left out, the first and last of their times.</summary>
    private static (int Count, DateTime From, DateTime To) Said(Row gap)
    {
        var m = GapDetail.Match(gap.Detail);
        Assert.True(m.Success, gap.Detail);
        return (int.Parse(m.Groups[1].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture), Time(m.Groups[2].Value), Time(m.Groups[3].Value));
    }

    /// <summary>
    /// The owner's PC: one growth incident on one tag (6 MB a minute while a step says it grows, the 50 MB in 10 minutes rule),
    /// <see cref="PerSample"/> events every 10 s sample, each a Start with a PID one more than the one before (so a missing
    /// event shows as a missing PID) and a command line of the owner's lengths with a home path in it.
    /// </summary>
    private sealed class Run
    {
        public readonly World W = new() { Interval = TimeSpan.FromSeconds(10) };
        public readonly CountingHost Host;
        public readonly IncidentManager Mgr;
        private readonly Detector _det;
        /// <summary>Each event's time, by PID.</summary>
        public readonly Dictionary<int, DateTime> Times = new();
        public int Pid = 500_000;
        public long Tag = 100L << 20;
        public int PerSample = 26;
        /// <summary>
        /// Half the events are the summarizer's rollups, as on the owner's PC: timed at their first start, up to a minute before
        /// they arrive (<c>EventSummarizer</c>); each event at its own millisecond, so no two share a time.
        /// </summary>
        public bool Late;
        private readonly HashSet<long> _usedMs = [];
        private readonly Random _rnd = new(5);

        public Run(string dir)
        {
            Directory.CreateDirectory(dir);
            Host = new CountingHost(dir);
            var settings = new Settings { GrowthRules = [new(10, 50 * Settings.MB)] };
            Mgr = new IncidentManager(settings, new TagKnowledge(null, null), Host, W.Registry);
            _det = new Detector(settings);
            W.AddProcess(4, 0, "System");
            for (int p = 0; p < 10; p++) W.AddProcess(100 + p, 4, "node.exe");
        }

        public void Step(bool grow)
        {
            for (int p = 0; p < 10; p++) W.Io(100 + p, other: 50 + _rnd.Next(500));
            if (grow) Tag += 1L << 20;
            W.Sample(("T000", Tag));
            var now = W.Now - W.Interval;
            var events = new List<LifecycleEvent>();
            for (int k = 0; k < PerSample; k++)
            {
                // Over the 10 s before the sample, so each is read onto the forward clock at its own time; a late one up to a
                // minute before.
                bool late = Late && k % 2 == 0;
                var t = late ? now.AddMilliseconds(-1 - _rnd.Next(60_000)) : now.AddMilliseconds(-10_000 + k * 300);
                if (Late) while (!_usedMs.Add(t.Ticks / TimeSpan.TicksPerMillisecond)) t = t.AddMilliseconds(1);
                Times[Pid] = t;
                events.Add(new LifecycleEvent
                {
                    TimeUtc = t, Kind = late ? LifecycleKind.Rollup : LifecycleKind.Start, Pid = Pid++, CreateTime = t.ToFileTimeUtc(), ParentPid = 100 + k % 10, Image = "node.exe",
                    CommandLine = @"node C:\Users\alice\repo\build.js --step " + new string('x', 200 + _rnd.Next(300)),
                });
            }
            Mgr.AddEvents(events, At(now));
            Mgr.Process(W.History, _det, _det.Evaluate(W.History), At(now));
            Host.Settle();
        }

        /// <summary>
        /// The last event of the incident's window as it ends now (<see cref="IncidentManager.EventWindowEndMs"/>: a minute after
        /// its growth's end, or its last rise once closed, or now while it climbs); the world's forward clock is its time of day.
        /// </summary>
        public int LastOfWindow(Incident inc) => Times.Where(kv => kv.Value <= EndOfWindow(inc)).Max(kv => kv.Key);

        /// <summary>The time of day the incident's window ends at now (<see cref="IncidentManager.EventWindowEndMs"/>, on the world's forward clock).</summary>
        public DateTime EndOfWindow(Incident inc) => new(IncidentManager.EventWindowEndMs(inc, W.History.Ms[^1]) * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    }

    /// <summary>5 quiet minutes, then 25 minutes of growth: one incident, open, its window far past 2,000 events.</summary>
    /// <param name="room">A write budget with room for every write that can wait (the rows of events.csv follow each new event).</param>
    private Run Grown(bool room = false)
    {
        var run = new Run(_dir);
        if (room) run.Mgr.Budget = new WriteBudget(bytesPerHour: 64L << 20, burst: 64L << 20);
        for (int i = 0; i < 30; i++) run.Step(grow: false);
        for (int i = 0; i < 150; i++) run.Step(grow: true);
        Assert.Single(run.Mgr.Open);
        return run;
    }

    /// <summary>Steps without growth until the incident closes (its 10 quiet minutes).</summary>
    private static void UntilClosed(Run run, Incident inc)
    {
        for (int i = 0; i < 6 * 15 && inc.IsOpen; i++) run.Step(grow: false);
        Assert.False(inc.IsOpen);
    }

    /// <summary>The file holds both ends of the window as it is: the first part's last event, a Gap row for every event between, the latest 1,000 to the window's last.</summary>
    private static (List<int> First, Row Gap, List<int> Latest) AssertBothEnds(Run run, Incident inc, string text, int firstKept)
    {
        var (first, gap, latest) = Ends(text);
        Assert.NotNull(gap);
        Assert.Equal(firstKept + IncidentWriter.MaxEventRows - 1, first[^1]); // the first part: the first 1,000 from the window's start
        Assert.True(first.Count <= IncidentWriter.MaxEventRows);
        Assert.Equal(IncidentWriter.MaxEventRows, latest.Count);
        Assert.Equal(run.LastOfWindow(inc), latest[^1]); // the latest part ends with the window's last event
        var (count, from, to) = Said(gap!);
        Assert.Equal(latest[0] - first[^1] - 1, count); // every event between the two ends counted, none twice
        Assert.Equal(run.Times[first[^1] + 1], from);
        Assert.Equal(run.Times[latest[0] - 1], to);
        Assert.Equal(from, gap!.Time); // the row sits where the events left out begin
        Assert.DoesNotContain(RowsOf(text), r => r.Kind == "Note");
        return (first, gap, latest);
    }

    [Fact]
    public void ALongIncidentsEventsCsvHoldsItsFirstPartWhileOpenAndBothEndsOnceClosed()
    {
        var run = Grown(room: true);
        var inc = run.Mgr.Open.Single();
        // Open, with room for every row: the first part only, all of it, and the Note row saying the rest come at its close,
        // though the window has over 3,000 events by now.
        var (first, gap, latest) = Ends(EventsCsv(inc), whole: false);
        Assert.Null(gap);
        Assert.Empty(latest);
        int firstKept = first[0];
        Assert.Equal(IncidentWriter.MaxEventRows, first.Count);
        Assert.Equal("Note", RowsOf(EventsCsv(inc))[^1].Kind);
        Assert.True(run.Pid - firstKept > 3 * IncidentWriter.MaxEventRows, $"the window has {run.Pid - firstKept} events");
        // report.md says so, written now: how many events.csv holds and to when (the first event after its first part), and
        // how many more the window has so far (every event after the first part, to the window's end as measured now).
        Assert.True(run.Mgr.WriteNow(run.W.History, inc));
        int more = run.Times.Count(kv => kv.Key >= firstKept + IncidentWriter.MaxEventRows && kv.Value <= run.EndOfWindow(inc));
        Assert.True(more > 2 * IncidentWriter.MaxEventRows, $"{more} more");
        Assert.Equal($"({first.Count} other events in events.csv: while the incident is open it holds the window's first 1,000 events, " +
                     $"to {Fmt.Clock(run.Times[firstKept + IncidentWriter.MaxEventRows])}; the window has {more:N0} more so far, " +
                     "and the latest of them, up to 1,000, are added when it closes)", EventsLine(inc));

        UntilClosed(run, inc);
        var (f, g, l) = AssertBothEnds(run, inc, EventsCsv(inc), firstKept);
        // report.md's line under the window's events says it too: how many events.csv holds (none of them is listed: they
        // are not the suspects' starts), how many it leaves out, and between which times.
        string md = File.ReadAllText(Path.Combine(inc.Dir, "report.md"));
        Assert.Contains($"({f.Count + l.Count} other events in events.csv; it leaves out {Said(g).Count:N0} events, " +
                        $"between {Fmt.Clock(Said(g).From)} and {Fmt.Clock(Said(g).To)}, as its Gap row says)", md);
    }

    [Fact]
    public void AShortIncidentsEventsCsvHoldsEveryEventOnceAndNoGapRow()
    {
        // 15 events a sample, 10 minutes of growth: its window has between 1,000 and 2,000 events, so both its parts are in
        // use and none is left out.
        var run = new Run(_dir) { PerSample = 15 };
        for (int i = 0; i < 30; i++) run.Step(grow: false);
        for (int i = 0; i < 60; i++) run.Step(grow: true);
        var inc = run.Mgr.Open.Single();
        UntilClosed(run, inc);
        var (first, gap, latest) = Ends(EventsCsv(inc));
        Assert.Null(gap);
        Assert.Empty(latest);
        Assert.Equal(run.LastOfWindow(inc), first[^1]); // every event from its first row to the window's last, each once
        Assert.InRange(first.Count, IncidentWriter.MaxEventRows + 1, 2 * IncidentWriter.MaxEventRows - 1);
        Assert.DoesNotContain(RowsOf(EventsCsv(inc)), r => r.Kind is "Gap" or "Note");
    }

    /// <summary>
    /// Half the events arrive late (the summarizer's rollups, timed at their first start, up to a minute late: 55% of the
    /// owner's events). The first part stays the window's earliest by time (a late event earlier than its last takes that
    /// one's place), and one that falls among those left out is counted with them, so the closed file is the first part, one
    /// Gap row, then exactly the latest 1,000, in time order, no row inside the Gap row's range, every event of the window
    /// between the first row and the last either in the file once or counted in the Gap row.
    /// </summary>
    [Fact]
    public void LateEventsLeaveTheGapRowBetweenTheTwoPartsAndNoRowInsideItsRange()
    {
        var run = new Run(_dir) { Late = true };
        run.Mgr.Budget = new WriteBudget(bytesPerHour: 64L << 20, burst: 64L << 20); // the rows follow each new event
        for (int i = 0; i < 30; i++) run.Step(grow: false);
        for (int i = 0; i < 150; i++) run.Step(grow: true);
        var inc = run.Mgr.Open.Single();
        // Open: its first part as it took them in, and its Note row; a late event that took a place in the first part after
        // that row waits for the close (no event row after the Note row), even through a write of every row there is (Export
        // report's).
        Assert.True(run.Mgr.WriteNow(run.W.History, inc));
        var open = RowsOf(EventsCsv(inc));
        int note = open.FindIndex(r => r.Kind == "Note");
        Assert.True(note > 0, "a Note row");
        Assert.DoesNotContain(open.Skip(note + 1), r => r.Kind is "Start" or "Rollup");
        Assert.Equal(IncidentWriter.MaxEventRows, open.Take(note).Count(r => r.Kind is "Start" or "Rollup"));
        UntilClosed(run, inc);
        var rows = RowsOf(EventsCsv(inc));
        Assert.True(rows.Zip(rows.Skip(1)).All(p => p.First.Time <= p.Second.Time), "the rows are in time order");
        static bool IsEvent(Row r) => r.Kind is "Start" or "Rollup";
        int at = rows.FindIndex(r => r.Kind == "Gap");
        Assert.Single(rows, r => r.Kind == "Gap");
        var first = rows.Take(at).Where(IsEvent).ToList();
        var latest = rows.Skip(at + 1).Where(IsEvent).ToList();
        Assert.InRange(first.Count, 1, IncidentWriter.MaxEventRows);
        Assert.Equal(IncidentWriter.MaxEventRows, latest.Count); // exactly the latest 1,000 after the Gap row
        var (count, from, to) = Said(rows[at]);
        Assert.All(first, r => Assert.True(r.Time <= from, $"first part {r.Time:O} after the Gap row's first {from:O}"));
        Assert.All(latest, r => Assert.True(r.Time >= to, $"latest part {r.Time:O} before the Gap row's last {to:O}"));
        Assert.DoesNotContain(rows, r => IsEvent(r) && r.Time > from && r.Time < to);
        // Each part is every event of its span, once; the Gap row counts every event between the two; the latest part ends
        // with the window's last event.
        var pids = first.Concat(latest).Select(r => r.Pid).ToList();
        Assert.Equal(pids.Count, pids.Distinct().Count());
        List<int> Between(DateTime a, DateTime b) => run.Times.Where(kv => kv.Value >= a && kv.Value <= b).Select(kv => kv.Key).Order().ToList();
        Assert.Equal(Between(first[0].Time, first[^1].Time), first.Select(r => r.Pid).Order());
        Assert.Equal(Between(latest[0].Time, latest[^1].Time), latest.Select(r => r.Pid).Order());
        Assert.Equal(run.Times.Count(kv => kv.Value > first[^1].Time && kv.Value < latest[0].Time), count);
        Assert.Equal(run.Times.Where(kv => kv.Value <= run.EndOfWindow(inc)).Max(kv => kv.Value), latest[^1].Time);
        // Late events did take places in the first part: one of its events arrived after an event later than the whole first
        // part had (PIDs follow arrival), which the first part then let go.
        Assert.True(first.Max(r => r.Pid) > run.Times.Where(kv => kv.Value > first[^1].Time).Min(kv => kv.Key), "no late event took a place in the first part");
    }

    [Fact]
    public void TheEndOfARunWritesBothEndsOfAnOpenIncidentAndOfAClosedOneThatDoesNotHoldThem()
    {
        var run = Grown();
        var inc = run.Mgr.Open.Single();
        int firstKept = Ends(EventsCsv(inc), whole: false).First[0];
        run.Mgr.Finish(run.W.History, run.W.Now);
        Assert.False(inc.IsOpen);
        AssertBothEnds(run, inc, EventsCsv(inc), firstKept);

        // A closed incident whose file is gone (its closing write lost): the end of the run writes both ends again.
        var second = Grown();
        var other = second.Mgr.Open.Single();
        UntilClosed(second, other);
        string closed = EventsCsv(other);
        File.Delete(Path.Combine(other.Dir, "events.csv"));
        second.Mgr.Finish(second.W.History, second.W.Now);
        Assert.Equal(closed, EventsCsv(other));
    }

    [Fact]
    public void AnIncidentLeavingMemoryWritesBothEndsWhenItsFileDoesNotHoldThem()
    {
        // Closed, its file gone (its closing write lost); no events after. 6 hours after closing it leaves memory, and its
        // events.csv is written whole with both ends then, so a later run has them.
        var run = Grown();
        var inc = run.Mgr.Open.Single();
        UntilClosed(run, inc);
        string closed = EventsCsv(inc);
        File.Delete(Path.Combine(inc.Dir, "events.csv"));
        run.PerSample = 0;
        for (int i = 0; i < 6 * 365 && run.Mgr.Closed.Count > 0; i++) run.Step(grow: false);
        Assert.Empty(run.Mgr.Closed); // it left memory
        Assert.Equal(closed, EventsCsv(inc));
    }

    [Fact]
    public void AReOpenedIncidentThatClosesAgainWritesBothEndsAgain()
    {
        var run = Grown();
        var inc = run.Mgr.Open.Single();
        int firstKept = Ends(EventsCsv(inc), whole: false).First[0];
        UntilClosed(run, inc);
        var (_, gap1, latest1) = AssertBothEnds(run, inc, EventsCsv(inc), firstKept);

        // It grows again within the hour: re-opened. While open its file stays as its close wrote it (an alert's row aside).
        for (int i = 0; i < 90; i++) run.Step(grow: true);
        Assert.True(inc.IsOpen);
        Assert.Equal(1, inc.Reopened);
        var (_, gapOpen, latestOpen) = Ends(EventsCsv(inc));
        Assert.Equal(latest1, latestOpen);
        Assert.Equal(gap1!.Detail, gapOpen!.Detail);

        UntilClosed(run, inc);
        var (_, gap2, latest2) = AssertBothEnds(run, inc, EventsCsv(inc), firstKept);
        Assert.True(latest2[^1] > latest1[^1] + 26 * 90, $"{latest2[^1]} after {latest1[^1]}");
        Assert.True(Said(gap2).Count > Said(gap1).Count);
    }

    [Fact]
    public void AnExportOfAnOpenIncidentHasBothEndsFromMemory()
    {
        var run = Grown();
        var inc = run.Mgr.Open.Single();
        Assert.True(run.Mgr.WriteNow(run.W.History, inc));
        string file = EventsCsv(inc), folderMd = File.ReadAllText(Path.Combine(inc.Dir, "report.md"));
        var (first, gap, _) = Ends(file, whole: false);
        Assert.Null(gap); // its folder: the first part only
        var live = run.Mgr.ExportFilesNow(run.W.History, inc)!;
        var redaction = new RedactionInfo("alice", "PC-1", [@"C:\Users\alice"]);
        string zip = Exporter.Export(inc.Dir, Path.Combine(_dir, "exports"), redaction, p => p, liveFiles: live);
        using var archive = ZipFile.OpenRead(zip);
        string Entry(string name) { using var reader = new StreamReader(archive.GetEntry(name)!.Open()); return reader.ReadToEnd(); }
        string exported = Entry("events.csv"), md = Entry("report.md");
        var (f, g, latest) = AssertBothEnds(run, inc, exported, first[0]);
        Assert.True(run.Pid - 1 - latest[^1] < 26 * 6 * 2, $"{latest[^1]}, newest {run.Pid - 1}"); // to within minutes of now: the window of an open incident
        Assert.DoesNotContain("alice", exported, StringComparison.OrdinalIgnoreCase); // redacted as the folder's files are
        Assert.Contains("<HOME>", exported);
        Assert.Equal(1, archive.Entries.Count(e => e.FullName == "events.csv"));
        // The zip agrees with itself: its report.md counts its events.csv's rows and says what its Gap row says (the folder's
        // report.md says what the folder's file holds while the incident is open), and is otherwise the folder's.
        var (left, fromUtc, toUtc) = Said(g);
        string line = $"({f.Count + latest.Count} other events in events.csv; it leaves out {left:N0} events, between {Fmt.Clock(fromUtc)} and {Fmt.Clock(toUtc)}, as its Gap row says)";
        Assert.Contains(line, md);
        Assert.Contains("while the incident is open it holds the window's first 1,000 events", folderMd);
        Assert.DoesNotContain("while the incident is open it holds", md);
        Assert.Equal(1, archive.Entries.Count(e => e.FullName == "report.md"));
        static string WithoutEventsLine(string text) => string.Join('\n', text.Split('\n').Where(l => !l.Contains("other events in")));
        Assert.Equal(WithoutEventsLine(Exporter.RedactText(folderMd, redaction)), WithoutEventsLine(md));
        Assert.Equal(file, EventsCsv(inc)); // the folder's files are as they were
        Assert.Equal(folderMd, File.ReadAllText(Path.Combine(inc.Dir, "report.md")));
        Assert.Null(run.Mgr.ExportFilesNow(run.W.History, Closed(run, inc))); // a closed incident's export reads its files
    }

    private static Incident Closed(Run run, Incident inc)
    {
        UntilClosed(run, inc);
        return inc;
    }

    /// <summary>The heap after a compacting full collection: what is held, not what a collection left behind.</summary>
    private static long Heap()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        return GC.GetTotalMemory(forceFullCollection: false);
    }

    private static string Long(string head, Random rnd, int min, int max) =>
        head + " " + string.Concat(Enumerable.Range(0, rnd.Next(min, max) / 9).Select(_ => rnd.Next(0x10000000, int.MaxValue).ToString("x8") + " "));

    /// <summary>
    /// A 10 s sample's 26 events at the owner's mix and sizes (WriteBudgetTests': rollups with long details, starts and exits
    /// with command lines of 150 to 700 characters, orphans, storm findings), 300 ms apart from <paramref name="now"/>.
    /// </summary>
    private static List<LifecycleEvent> OwnersMix(DateTime now, Random rnd, ref int pid)
    {
        var events = new List<LifecycleEvent>(26);
        for (int e = 0; e < 26; e++)
        {
            int kind = rnd.Next(100);
            events.Add(kind switch
            {
                < 55 => new LifecycleEvent { TimeUtc = now.AddMilliseconds(e * 300), Kind = LifecycleKind.Rollup, ParentPid = 100 + rnd.Next(40), Image = "bash.exe", Count = 1 + rnd.Next(9),
                    Owner = "Claude Code session x (PID 100)", OwnerKey = "claude:100", Detail = Long("12 short commands under bash.exe:", rnd, 60, 160) },
                < 73 => new LifecycleEvent { TimeUtc = now.AddMilliseconds(e * 300), Kind = LifecycleKind.Start, Pid = pid++, CreateTime = now.ToFileTimeUtc(), ParentPid = 100 + rnd.Next(40), Image = "node.exe",
                    CommandLine = Long("node.exe", rnd, 150, 700), User = @"PC\me" },
                < 91 => new LifecycleEvent { TimeUtc = now.AddMilliseconds(e * 300), Kind = LifecycleKind.Exit, Pid = pid++, CreateTime = now.ToFileTimeUtc(), ParentPid = 100 + rnd.Next(40), Image = "node.exe",
                    CommandLine = Long("node.exe", rnd, 150, 700), ExitCode = 0 },
                < 97 => new LifecycleEvent { TimeUtc = now.AddMilliseconds(e * 300), Kind = LifecycleKind.Orphaned, Pid = pid++, CreateTime = now.ToFileTimeUtc(), ParentPid = 100, Image = "bash.exe",
                    Detail = "parent bash.exe (100) exited at 23:57:45; still running 30 s later" },
                _ => new LifecycleEvent { TimeUtc = now.AddMilliseconds(e * 300), Kind = LifecycleKind.Finding, Image = "SpawnStorm", Detail = Long("Spawn storm: node.exe (PID 1) started 44 short-lived children", rnd, 20, 80) },
            });
        }
        return events;
    }

    /// <summary>
    /// 10 growth incidents open together for <paramref name="hours"/> at the owner's rate, mix and sizes of events
    /// (WriteBudgetTests': rollups with long details, starts and exits with command lines of 150 to 700 characters).
    /// Returns the manager and the time (100 ns ticks, on the stopwatch: the thread's CPU clock ticks every 15.6 ms, far
    /// more than one intake takes) and events of its event intake (AddEvents).
    /// </summary>
    private (IncidentManager Mgr, World W, long IntakeTicks, long Events) TenOpen(double hours) => TenOpen(hours, new CountingHost(_dir));

    private (IncidentManager Mgr, World W, long IntakeTicks, long Events) TenOpen(double hours, CountingHost host)
    {
        Directory.CreateDirectory(_dir);
        var w = new World { Interval = TimeSpan.FromSeconds(10) };
        var settings = new Settings { GrowthRules = [new(10, 50 * Settings.MB)], IncidentMaxMinutes = 60 * hours + 60 };
        var mgr = new IncidentManager(settings, new TagKnowledge(null, null), host, w.Registry);
        var det = new Detector(settings);
        var rnd = new Random(7);
        w.AddProcess(4, 0, "System");
        for (int p = 0; p < 40; p++) w.AddProcess(100 + p, 4, p % 2 == 0 ? "node.exe" : "claude.exe");
        string[] tags = Enumerable.Range(0, 10).Select(i => $"T{i:D3}").ToArray();
        var bytes = tags.ToDictionary(t => t, _ => 100L << 20);
        int pid = 500_000;
        long ticks = 0, count = 0;
        int samples = 30 + (int)(hours * 360);
        for (int i = 0; i < samples; i++)
        {
            for (int p = 0; p < 40; p++) w.Io(100 + p, other: 50 + rnd.Next(500));
            if (i >= 30) foreach (var t in tags) bytes[t] += 1L << 20;
            w.Sample(tags.Select(t => (t, bytes[t])).ToArray());
            var now = w.Now - w.Interval;
            var events = OwnersMix(now, rnd, ref pid);
            long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
            mgr.AddEvents(events, At(now));
            if (i >= 30) (ticks, count) = (ticks + (long)((System.Diagnostics.Stopwatch.GetTimestamp() - c0) * (1e7 / System.Diagnostics.Stopwatch.Frequency)), count + events.Count);
            mgr.Process(w.History, det, det.Evaluate(w.History), At(now));
        }
        Assert.Equal(10, mgr.Open.Count);
        return (mgr, w, ticks, count);
    }

    /// <summary>
    /// What the latest parts hold, with 10 incidents open for 3 hours at the owner's rate: the latest 1,000 events of each
    /// are the newest, which the 90-minute buffer holds too, so they add little more than their references. Measured as the
    /// heap after a compacting full collection with them, less the same without them (<see cref="IncidentManager.ForgetLatestParts"/>).
    /// </summary>
    [Fact]
    public void TheLatestPartsOfTenIncidentsOpenForHoursHoldLittleMemory()
    {
        var (mgr, w, ticks, events) = TenOpen(hours: 3);
        var held = mgr.Held();
        long with = Heap();
        mgr.ForgetLatestParts();
        long without = Heap();
        GC.KeepAlive(mgr);
        GC.KeepAlive(w);
        double mb = (with - without) / 1048576.0;
        output.WriteLine($"10 open incidents, 3 hours at 26 events a sample: the latest parts hold {mb:0.000} MB ({held.events:N0} events kept in all, " +
                         $"{mgr.EventCount:N0} in the shared buffer); the event intake {ticks * 100.0 / Math.Max(1, events):0} ns an event");
        Assert.True(with - without < 3L << 19, $"the latest parts hold {mb:0.000} MB"); // 1.5 MB
        // Each holds its first 1,000 and its latest 1,000, the 500 before them while it is open, and the events since its
        // growth's end as last measured (a minute or two of them while it climbs on).
        Assert.InRange(held.events, 10 * (2 * IncidentWriter.MaxEventRows + IncidentManager.LatestSlack),
            10 * (2 * IncidentWriter.MaxEventRows + IncidentManager.LatestSlack + IncidentManager.TrimBatch + IncidentManager.AfterEndMax));
    }

    /// <summary>
    /// Ten incidents open for an hour at the owner's load, then the end of the run closes them: each closing write replaces its
    /// events.csv once, with both ends (2,000 rows of the owner's lengths and the Gap row), and the rows after the Gap row are
    /// what the latest part adds.
    /// </summary>
    [Fact]
    public void TenIncidentsClosingAtTheOwnersLoadEachWriteBothEndsOnce()
    {
        var host = new CountingHost(_dir);
        var (mgr, w, _, _) = TenOpen(hours: 1, host);
        var incidents = mgr.Open.ToList();
        var before = incidents.ToDictionary(i => i, i => new FileInfo(Path.Combine(i.Dir, "events.csv")).Length);
        // While open, each events.csv was made once (its first write) and appended to after.
        var made = incidents.ToDictionary(i => i, i => host.Replaced.GetValueOrDefault(Path.Combine(i.Dir, "events.csv")));
        Assert.All(made.Values, n => Assert.Equal(1, n));
        long spent = mgr.Budget.SpentMust;
        mgr.Finish(w.History, w.Now);
        long whole = 0, latestPart = 0;
        foreach (var inc in incidents)
        {
            Assert.Equal(made[inc] + 1, host.Replaced.GetValueOrDefault(Path.Combine(inc.Dir, "events.csv"))); // its close replaced it once, whole
            string text = EventsCsv(inc);
            var rows = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
            int gap = rows.FindIndex(l => Csv.Split(l.TrimEnd('\r'))[1] == "Gap");
            Assert.True(gap > 0, inc.Id);
            Assert.Equal(IncidentWriter.MaxEventRows, rows.Skip(gap + 1).Count(l => Csv.Split(l.TrimEnd('\r'))[1] is not ("Alert" or "Release")));
            whole += new FileInfo(Path.Combine(inc.Dir, "events.csv")).Length;
            latestPart += rows.Skip(gap).Sum(l => System.Text.Encoding.UTF8.GetByteCount(l) + 1);
        }
        long must = mgr.Budget.SpentMust - spent;
        output.WriteLine($"10 closing writes: events.csv {whole / 10 / 1024.0:0} KB each replaced whole ({before.Values.Average() / 1024.0:0} KB of first part before), " +
                         $"of it the Gap row and the latest 1,000 rows {latestPart / 10 / 1024.0:0} KB; all the closing writes {must / 1048576.0:0.00} MB, through the budget as writes that must be made");
        Assert.True(must >= whole, $"{must:N0} spent, {whole:N0} written to events.csv"); // each went through the budget
        Assert.True(whole / 10 < 1L << 20, $"{whole / 10:N0} bytes an events.csv");
    }

    /// <summary>
    /// Long incidents closing one after another through the day at about the owner's rate (the maintainer's PC held 16 closed
    /// after 4 hours): 19 growth incidents 18 minutes apart, 15 minutes of growth each (windows of about 2,800 events),
    /// over 6 hours at the owner's event mix, with the default write budget. Each closing write replaces its events.csv once,
    /// whole, with both ends; what events.csv writes, per incident and per hour, is what DESIGN.md section 3 states (printed,
    /// with ceilings). A closed incident lets go of its latest part once it can no longer re-open (an hour after it closed)
    /// and its file holds both ends: the closed incidents held for their 6 hours then hold their first parts alone, and their
    /// files stay as their closes wrote them.
    /// </summary>
    [Fact]
    public void LongIncidentsClosingThroughTheDayReplaceTheirEventsCsvOnceAndThenLetGoOfTheirLatestParts()
    {
        Directory.CreateDirectory(_dir);
        var host = new CountingHost(_dir);
        var w = new World { Interval = TimeSpan.FromSeconds(10) };
        var settings = new Settings { GrowthRules = [new(10, 50 * Settings.MB)] };
        var mgr = new IncidentManager(settings, new TagKnowledge(null, null), host, w.Registry);
        var det = new Detector(settings);
        var rnd = new Random(11);
        w.AddProcess(4, 0, "System");
        for (int p = 0; p < 40; p++) w.AddProcess(100 + p, 4, p % 2 == 0 ? "node.exe" : "claude.exe");
        const int n = 19, spacing = 108, grow = 90;
        string[] tags = Enumerable.Range(0, n).Select(i => $"T{i:D3}").ToArray();
        var bytes = tags.ToDictionary(t => t, _ => 100L << 20);
        int pid = 500_000;
        int samples = 30 + (n - 1) * spacing + grow + 6 * 15;
        var closedAt = new Dictionary<Incident, string>();
        for (int i = 0; i < samples; i++)
        {
            for (int p = 0; p < 40; p++) w.Io(100 + p, other: 50 + rnd.Next(500));
            for (int t = 0; t < n; t++)
                if (i >= 30 + t * spacing && i < 30 + t * spacing + grow) bytes[tags[t]] += 1L << 20;
            w.Sample(tags.Select(t => (t, bytes[t])).ToArray());
            var now = w.Now - w.Interval;
            mgr.AddEvents(OwnersMix(now, rnd, ref pid), At(now));
            mgr.Process(w.History, det, det.Evaluate(w.History), At(now));
            host.Settle();
            foreach (var c in mgr.Closed) if (!closedAt.ContainsKey(c)) closedAt[c] = EventsCsv(c); // its file as its close wrote it
        }
        double hours = samples * 10 / 3600.0;
        Assert.Equal(n, mgr.Closed.Count);
        long nowMs = w.History.Ms[^1];
        int released = 0;
        foreach (var inc in mgr.Closed)
        {
            string path = Path.Combine(inc.Dir, "events.csv");
            Assert.Equal(2, host.Replaced.GetValueOrDefault(path)); // made at its opening, replaced whole once at its close
            Assert.Equal(closedAt[inc], EventsCsv(inc)); // as its close wrote it
            var rows = File.ReadAllLines(path).Skip(1).Select(l => Csv.Split(l)).ToList();
            int gap = rows.FindIndex(c => c[1] == "Gap");
            Assert.True(gap > 0, inc.Id);
            Assert.Equal(IncidentWriter.MaxEventRows, rows.Skip(gap + 1).Count(c => c[1] is not ("Alert" or "Release")));
            // An hour after it closed it can no longer re-open: its latest part is let go (its file holds it).
            bool past = nowMs - inc.ClosedMs!.Value >= 3_600_000 + 10_000;
            if (past) { Assert.Equal(0, mgr.LatestHeld(inc)); released++; }
            else Assert.True(mgr.LatestHeld(inc) > 0, inc.Id);
        }
        Assert.True(released >= n - 4, $"{released} of {n} let go of their latest parts");
        // The end of the run writes none of them again (their files hold both ends, as written).
        mgr.Finish(w.History, w.Now);
        Assert.All(mgr.Closed, inc => Assert.Equal(2, host.Replaced.GetValueOrDefault(Path.Combine(inc.Dir, "events.csv"))));
        var held = mgr.Held();
        long with = Heap();
        mgr.ForgetLatestParts();
        long without = Heap();
        GC.KeepAlive(mgr);
        GC.KeepAlive(w);
        long csv = host.BytesByName.GetValueOrDefault("events.csv"), whole = host.ReplacedBytesByName.GetValueOrDefault("events.csv");
        output.WriteLine($"{n} long incidents closing 18 minutes apart over {hours:0.0} h: events.csv wrote {csv / 1048576.0:0.00} MB, {csv / n / 1024.0:0} KB an incident " +
                         $"({whole / n / 1024.0:0} KB of it each one's opening and closing writes, which must be made), {csv / hours / 1048576.0:0.00} MB an hour " +
                         $"({whole / hours / 1048576.0:0.00} MB of it must); all report-folder writes {host.Bytes / hours / 1048576.0:0.00} MB an hour; " +
                         $"{released} of {n} closed let go of their latest parts, which hold {(with - without) / 1048576.0:0.000} MB; {held.events:N0} events held");
        Assert.True(csv / n < 800L << 10, $"{csv / n:N0} bytes of events.csv an incident");
        Assert.True(whole / n < 600L << 10, $"{whole / n:N0} bytes of events.csv's whole writes an incident");
        Assert.True(with - without < 1L << 20, $"the latest parts hold {(with - without) / 1048576.0:0.000} MB"); // only those of the last hour's, shared with the buffer
    }
}
