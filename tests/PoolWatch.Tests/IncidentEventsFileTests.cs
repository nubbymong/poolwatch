using PoolWatch.Core;
using PoolWatch.Core.Detection;
using PoolWatch.Core.History;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;

namespace PoolWatch.Tests;

/// <summary>
/// An incident's events.csv is appended to while it is open, not written again whole at every refresh: each event of the
/// window's first part once, the earliest first, never more than 500; when it closes it is replaced whole, once, with both
/// ends of its window; and the folder's other files are written only when what they say changed.
/// </summary>
public sealed class IncidentEventsFileTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 9, 22, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pw-eventsfile-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private Incident NewIncident() => new()
    {
        Id = "20261109-220000-File-656c6946", Dir = Path.Combine(_dir, "20261109-220000-File-656c6946"), Tag = PoolTag.Parse("File"), Kind = IncidentKind.Growth,
        OpenedUtc = T0, OpenedMs = 1_000_000, OnsetUtc = T0, OnsetMs = 1_000_000,
    };

    /// <summary>Events <paramref name="from"/> to <paramref name="to"/> (exclusive), one a second from the incident's onset, as the manager keeps them.</summary>
    private static List<LifecycleEvent> Events(int from, int to) => Enumerable.Range(from, to - from).Select(i => new LifecycleEvent
    {
        TimeUtc = T0.AddSeconds(i), Ms = 1_000_000 + i * 1000L, Kind = LifecycleKind.Start, Pid = 10_000 + i, CreateTime = T0.AddSeconds(i).ToFileTimeUtc(),
        ParentPid = 4, Image = "node.exe", CommandLine = $"node build.js --step {i} " + new string('x', 200),
    }).ToList();

    private string EventsCsv(Incident inc) => File.ReadAllText(Path.Combine(inc.Dir, "events.csv"));

    [Fact]
    public void NewEventsAreAppendedAndTheRowsWrittenStayAsTheyWere()
    {
        var host = new CountingHost(_dir);
        var writer = new IncidentWriter(new TagKnowledge(null, null), host, new ProcessRegistry());
        var inc = NewIncident();
        var h = new History();
        var events = Events(0, 100);
        long first = writer.Write(h, inc, events);
        string before = EventsCsv(inc);
        Assert.Equal(101, before.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        long whole = new FileInfo(Path.Combine(inc.Dir, "events.csv")).Length;

        events.AddRange(Events(100, 150));
        long second = writer.Write(h, inc, events);
        string after = EventsCsv(inc);
        Assert.StartsWith(before, after); // appended: what was there is as it was
        Assert.Equal(151, after.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        long now = new FileInfo(Path.Combine(inc.Dir, "events.csv")).Length;
        // The second write wrote the 50 new rows and report.md (its count of events changed), not the 100 rows again: less
        // than events.csv holds now.
        Assert.True(second < now, $"{second} < {now}");
        Assert.True(second >= now - whole, $"{second} >= {now - whole}");
        Assert.True(second < first, $"{second} < {first}");

        // Nothing new: events.csv untouched, and the other files are not written again either (none of them changed).
        var stamp = File.GetLastWriteTimeUtc(Path.Combine(inc.Dir, "events.csv"));
        var report = File.GetLastWriteTimeUtc(Path.Combine(inc.Dir, "report.md"));
        Thread.Sleep(20);
        Assert.Equal(0, writer.Write(h, inc, events));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(inc.Dir, "events.csv")));
        Assert.Equal(report, File.GetLastWriteTimeUtc(Path.Combine(inc.Dir, "report.md")));
    }

    [Fact]
    public void AWriteAddsAtMostItsRowsAndTheFileStopsAtWhatTheIncidentKeeps()
    {
        var host = new CountingHost(_dir);
        var writer = new IncidentWriter(new TagKnowledge(null, null), host, new ProcessRegistry());
        var inc = NewIncident();
        var h = new History();
        var events = Events(0, 6000);
        writer.Write(h, inc, events, maxRows: 300);
        Assert.Equal(301, File.ReadAllLines(Path.Combine(inc.Dir, "events.csv")).Length); // the window's first 300
        Assert.True(writer.EventsPending(inc));
        Assert.Contains("--step 0 ", File.ReadAllLines(Path.Combine(inc.Dir, "events.csv"))[1]);
        writer.AppendEvents(inc, events, maxRows: 10_000);
        var lines = File.ReadAllLines(Path.Combine(inc.Dir, "events.csv"));
        Assert.Equal(1 + IncidentWriter.MaxEventRows + 1, lines.Length); // the header, the first 500 events and the row that says the rest come at its close
        // The row says, as a fact about this file (true however the run ends), how many events it holds and to when: the time
        // of the first event not in its first part (the 501st); and what PoolWatch does with it at the close. (These events
        // were given as all there are, with no start of their own, so they are "the incident window's first": for an incident
        // the manager holds the row says from where they are, IncidentPersistenceTests.) 500 at each end: the owner's choice
        // (2026-11-10), for a long incident's closing write (BothEndsTests).
        Assert.Equal(500, IncidentWriter.MaxEventRows);
        Assert.Equal("events.csv holds the incident window's first 500 events, to this row's time, as PoolWatch had them when it wrote this row; later events " +
                     "are not in this file (when the incident closes PoolWatch replaces the file with both ends of its window as it is measured then, its first events and its latest, " +
                     "up to 500 of each, with a Gap row between them when it leaves any out)",
            PoolWatch.Core.Util.Csv.Split(lines[^1])[9]);
        Assert.DoesNotContain("day's", lines[^1]);
        Assert.StartsWith(PoolWatch.Core.Util.Csv.Line(T0.AddSeconds(IncidentWriter.MaxEventRows)), lines[^1]);
        Assert.Contains(",Note,", lines[^1]);
        Assert.False(writer.EventsPending(inc));
        Assert.Equal(0, writer.AppendEvents(inc, events, maxRows: 10_000)); // full: nothing more
    }

    [Fact]
    public void AClosedIncidentsEventsCsvIsReplacedWholeOnceWithBothEndsOfItsWindow()
    {
        var host = new CountingHost(_dir);
        var writer = new IncidentWriter(new TagKnowledge(null, null), host, new ProcessRegistry());
        var inc = NewIncident();
        var h = new History();
        var events = Events(0, 6000);
        writer.Write(h, inc, events);
        Assert.Equal(1 + IncidentWriter.MaxEventRows + 1, File.ReadAllLines(Path.Combine(inc.Dir, "events.csv")).Length); // open: the first part
        string path = Path.Combine(inc.Dir, "events.csv");
        (inc.ClosedUtc, inc.ClosedMs) = (T0.AddHours(2), 1_000_000 + 7_200_000);
        long wrote = writer.Write(h, inc, events);
        var lines = File.ReadAllLines(path);
        // The header, events 0 to 499, the Gap row, events 5,500 to 5,999: sorted by time, each once, no note.
        Assert.Equal(1 + 2 * IncidentWriter.MaxEventRows + 1, lines.Length);
        Assert.Contains("--step 499 ", lines[IncidentWriter.MaxEventRows]);
        string gap = lines[IncidentWriter.MaxEventRows + 1];
        Assert.StartsWith(PoolWatch.Core.Util.Csv.Line(T0.AddSeconds(500), "Gap"), gap);
        Assert.Contains($"5,000 events left out, between {PoolWatch.Core.Util.Csv.Escape(T0.AddSeconds(500))} and {PoolWatch.Core.Util.Csv.Escape(T0.AddSeconds(5499))}", gap);
        Assert.EndsWith("(events.csv keeps the incident window's first 500 events and its latest 500)", PoolWatch.Core.Util.Csv.Split(gap)[9]); // what it keeps, as counted
        Assert.Contains("--step 5500 ", lines[IncidentWriter.MaxEventRows + 2]);
        Assert.Contains("--step 5999 ", lines[^1]);
        Assert.DoesNotContain(lines, l => l.Contains(",Note,"));
        Assert.True(wrote >= new FileInfo(path).Length, $"{wrote} >= {new FileInfo(path).Length}"); // replaced whole
        // Written once: the next write of the closed incident leaves it as it is.
        var stamp = File.GetLastWriteTimeUtc(path);
        Thread.Sleep(20);
        writer.Write(h, inc, events);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        Assert.False(writer.BothEndsDue(inc, IncidentEventSet.Of(events)));
    }

    /// <summary>
    /// report.md's line under the window's events says what events.csv holds and what it gets, from what the file holds: an
    /// open incident's while the write budget holds rows back (more of the window's first events as the budget allows, both
    /// ends at a close with more events than that; never "the rest" of the window), then once its first part is all written
    /// (how many it holds, to when, and how many more have come since), and a closed short one's while rows wait.
    /// </summary>
    [Fact]
    public void ReportMdSaysWhatEventsCsvHoldsAndGetsWhileTheIncidentIsOpenOrItsRowsWait()
    {
        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        var inc = NewIncident();
        var h = new History();
        var events = Events(0, 6000);
        string Md() => File.ReadAllText(Path.Combine(inc.Dir, "report.md"));
        const string atClose = "PoolWatch replaces the file with both ends of its window as it is measured then, its first events and its latest, up to 500 of each, with a Gap row between them when it leaves any out";
        writer.Write(h, inc, events, maxRows: 300);
        Assert.True(writer.EventsPending(inc));
        // None is listed (no suspect's start): all 6,000 are the others.
        Assert.Contains("(6000 other events in the window; events.csv gets more of the window's first events, up to 500 in all, as PoolWatch's write budget allows, " +
                        $"and when the incident closes with more events than that, {atClose}, and the day's `data\\<yyyyMMdd>\\events.csv` has every one)", Md());
        writer.AppendEvents(inc, events, maxRows: 10_000);
        writer.Write(h, inc, events);
        Assert.Contains($"(500 other events in events.csv: while the incident is open it holds the window's first 500 events, to {Fmt.Clock(T0.AddSeconds(500))}, " +
                        $"as PoolWatch had them when it wrote them; {5500:N0} more have come since, and when the incident closes {atClose})", Md());

        var shortOne = new Incident
        {
            Id = "20261109-230000-File-656c6946", Dir = Path.Combine(_dir, "20261109-230000-File-656c6946"), Tag = PoolTag.Parse("File"), Kind = IncidentKind.Growth,
            OpenedUtc = T0, OpenedMs = 1_000_000, OnsetUtc = T0, OnsetMs = 1_000_000, ClosedUtc = T0.AddHours(1), ClosedMs = 1_000_000 + 3_600_000,
        };
        writer.Write(h, shortOne, Events(0, 400), maxRows: 100);
        Assert.Contains("(400 other events in the window; events.csv gets the rest when PoolWatch's write budget has room, and the day's `data\\<yyyyMMdd>\\events.csv` has every one)",
            File.ReadAllText(Path.Combine(shortOne.Dir, "report.md")));
    }

    /// <summary>
    /// An open incident's file whose first part is full and all written ends with its Note row: a late event that takes a
    /// place in the first part since (its last one moving on) is not appended after that row, so the row stays true of the
    /// file; the close writes the file whole, the late event in its place and the one it moved on among those left out.
    /// </summary>
    [Fact]
    public void AnOpenIncidentsFileGetsNoEventRowAfterItsNoteRow()
    {
        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        var inc = NewIncident();
        var h = new History();
        string path = Path.Combine(inc.Dir, "events.csv");
        var events = Events(0, 3000);
        writer.Write(h, inc, events, maxRows: 10_000);
        var open = File.ReadAllLines(path);
        Assert.Contains(",Note,", open[^1]);
        // Late: timed between events 250 and 251, it takes a place in the first part; event 499 moves on.
        events.Add(Events(0, 1)[0] with { TimeUtc = T0.AddSeconds(250.5), Ms = 1_000_000 + 250_500, Pid = 99_999, CommandLine = "node late.js" });
        writer.Write(h, inc, events, maxRows: 10_000);
        Assert.Equal(open, File.ReadAllLines(path)); // as it was
        (inc.ClosedUtc, inc.ClosedMs) = (T0.AddHours(2), 1_000_000 + 7_200_000);
        writer.Write(h, inc, events);
        var closed = File.ReadAllLines(path).Skip(1).ToList();
        Assert.Contains("late.js", closed[251]);
        Assert.Contains("--step 498 ", closed[IncidentWriter.MaxEventRows - 1]);
        Assert.Contains(",Gap,", closed[IncidentWriter.MaxEventRows]);
        Assert.DoesNotContain(closed, l => l.Contains("--step 499 "));
    }

    /// <summary>
    /// A Note row says how many events the file holds, from where and to when: never on a file with no event row. An open
    /// incident whose first part ended with no event in it (the events from its start were gone before it looked at them,
    /// which an incident the manager does not hold can find) gets no such row, at its first write and at a later one: the
    /// file holds its header alone.
    /// </summary>
    [Fact]
    public void AnOpenIncidentsFileWithNoEventRowGetsNoNoteRow()
    {
        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        var inc = NewIncident();
        var h = new History();
        string path = Path.Combine(inc.Dir, "events.csv");
        var began = T0.AddMinutes(-2);
        // Its first part: no event, and cut at its start (none can join it), before its window's end.
        var none = new IncidentEventSet([], [], 880_000, long.MaxValue, began, 880_000) { FromUtc = began };
        writer.Write(h, inc, none, maxRows: 10_000);
        Assert.Single(File.ReadAllLines(path)); // its header: no row says "events.csv holds the first 0 events"
        writer.Write(h, inc, none, maxRows: 10_000);
        Assert.Single(File.ReadAllLines(path));
        Assert.DoesNotContain(",Note,", File.ReadAllText(path));
    }

    /// <summary>
    /// A file's rows are of the first part its incident had when the file was begun. When the incident's first part is
    /// another one since, from the same start but of later events (its start was measured somewhere else and back again
    /// once PoolWatch no longer held every event from it, so the first part is the first events it still holds from
    /// there), the file starts again from it, as it does for another start: rows appended to it would leave out the
    /// events between its last row and the first part's first, and its texts would give them as all there from its start.
    /// </summary>
    [Fact]
    public void AnOpenIncidentsFileStartsAgainWhenItsFirstPartIsOfLaterEventsFromTheSameStart()
    {
        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        var inc = NewIncident();
        var h = new History();
        var began = T0.AddMinutes(-2); // where the window starts, all through
        writer.Write(h, inc, From(Events(0, 300), began), maxRows: 100);
        Assert.Equal(Enumerable.Range(0, 100), Steps(inc));
        // Its first part now: the first events PoolWatch still holds from there, 300 on (it had let go of those up to event 299).
        var held = From(Events(300, 600), began) with { LostToUtc = T0.AddSeconds(299) };
        writer.Write(h, inc, held, maxRows: 100);
        Assert.Equal(Enumerable.Range(300, 100), Steps(inc)); // every event from its first row to its last
        Assert.Contains($"events.csv gets more of the first events PoolWatch still held from {Fmt.Clock(began)} (by then it had already let go of events up to {Fmt.Clock(T0.AddSeconds(299))})",
            EventsLine(inc));
        writer.Write(h, inc, held, maxRows: 100);
        Assert.Equal(Enumerable.Range(300, 200), Steps(inc)); // and it goes on from its rows
    }

    /// <summary>
    /// A file that starts again with all of its first part gets its Note row in that write: the row says where the
    /// file's rows are from now, the start the first part followed to, not where the file was begun before.
    /// </summary>
    [Fact]
    public void AFileStartedAgainWithAllOfItsFirstPartSaysInItsNoteRowWhereThatStarts()
    {
        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        var inc = NewIncident();
        var h = new History();
        var events = Events(0, 3000);
        writer.Write(h, inc, From(events, T0.AddMinutes(-2)), maxRows: 100);
        Assert.Equal(Enumerable.Range(0, 100), Steps(inc));
        // The onset is measured 3 minutes later, and the first part followed the window's start to event 60's time.
        (inc.OnsetUtc, inc.OnsetMs) = (T0.AddMinutes(3), 1_000_000 + 180_000);
        var later = T0.AddMinutes(1);
        writer.Write(h, inc, From(events.GetRange(60, 2940), later), maxRows: 10_000);
        Assert.Equal(Enumerable.Range(60, 500), Steps(inc));
        var note = PoolWatch.Core.Util.Csv.Split(File.ReadAllLines(Path.Combine(inc.Dir, "events.csv"))[^1]);
        Assert.Equal("Note", note[1]);
        Assert.Equal($"events.csv holds the first 500 events from {PoolWatch.Core.Util.Csv.Escape(later)}, to this row's time, as PoolWatch had them when it wrote this row; " +
                     $"later events are not in this file ({AtClose})", note[9]);
    }

    /// <summary>
    /// A Note row is about its file: it says where the file was begun, in a file that went on from its rows (not where
    /// the events of the write that added the row start, should those have no start of their own).
    /// </summary>
    [Fact]
    public void ANoteRowSaysWhereItsFileWasBegunWhenTheFileWentOnFromItsRows()
    {
        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        var inc = NewIncident();
        var h = new History();
        var began = T0.AddMinutes(-2);
        var events = Events(0, 3000);
        writer.Write(h, inc, From(events, began), maxRows: 300);
        Assert.Equal(Enumerable.Range(0, 300), Steps(inc));
        writer.Write(h, inc, events, maxRows: 10_000); // the same events, given as all there are
        Assert.Equal(Enumerable.Range(0, 500), Steps(inc));
        var note = PoolWatch.Core.Util.Csv.Split(File.ReadAllLines(Path.Combine(inc.Dir, "events.csv"))[^1]);
        Assert.Equal("Note", note[1]);
        Assert.Equal($"events.csv holds the first 500 events from {PoolWatch.Core.Util.Csv.Escape(began)}, to this row's time, as PoolWatch had them when it wrote this row; " +
                     $"later events are not in this file ({AtClose})", note[9]);
    }

    /// <summary>
    /// The Gap row sits between the two parts by its place, not by its time alone: when the latest part's first event has the
    /// time of the first event left out, it still comes after the Gap row; and when the clock was set back between the two
    /// parts (the latest part's times of day earlier than the first part's), the parts are not mixed.
    /// </summary>
    [Fact]
    public void TheGapRowSitsBetweenTheTwoPartsByItsPlaceNotByItsTimeAlone()
    {
        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        Incident Closed(string id)
        {
            var inc = new Incident
            {
                Id = id, Dir = Path.Combine(_dir, id), Tag = PoolTag.Parse("File"), Kind = IncidentKind.Growth,
                OpenedUtc = T0, OpenedMs = 1_000_000, OnsetUtc = T0, OnsetMs = 1_000_000,
            };
            (inc.ClosedUtc, inc.ClosedMs) = (T0.AddHours(2), 1_000_000 + 7_200_000);
            return inc;
        }
        // Events 0 to 1,000: the first part 0 to 499, event 500 left out, the latest part 501 to 1,000.
        var tie = Closed("20261109-220000-File-656c6946");
        var events = Events(0, 1001);
        events[501] = events[501] with { TimeUtc = events[500].TimeUtc };
        writer.Write(new History(), tie, events);
        var lines = File.ReadAllLines(Path.Combine(tie.Dir, "events.csv")).Skip(1).ToList();
        Assert.Contains(",Gap,", lines[IncidentWriter.MaxEventRows]);
        Assert.Contains("--step 499 ", lines[IncidentWriter.MaxEventRows - 1]);
        Assert.Contains("--step 501 ", lines[IncidentWriter.MaxEventRows + 1]);

        var setBack = Closed("20261109-220001-File-656c6946");
        events = Events(0, 1001);
        for (int i = 501; i <= 1000; i++) events[i] = events[i] with { TimeUtc = events[i].TimeUtc.AddHours(-1) };
        writer.Write(new History(), setBack, events);
        lines = File.ReadAllLines(Path.Combine(setBack.Dir, "events.csv")).Skip(1).ToList();
        Assert.Equal(1 + 2 * IncidentWriter.MaxEventRows, lines.Count);
        for (int i = 0; i < IncidentWriter.MaxEventRows; i++) Assert.Contains($"--step {i} ", lines[i]);
        Assert.Contains(",Gap,", lines[IncidentWriter.MaxEventRows]);
        for (int i = 0; i < IncidentWriter.MaxEventRows; i++) Assert.Contains($"--step {501 + i} ", lines[IncidentWriter.MaxEventRows + 1 + i]);
    }

    [Fact]
    public void AClosedIncidentWithFewerThanAThousandEventsHoldsEachOnceAndNoGapRow()
    {
        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        var inc = NewIncident();
        (inc.ClosedUtc, inc.ClosedMs) = (T0.AddHours(1), 1_000_000 + 3_600_000);
        writer.Write(new History(), inc, Events(0, 900));
        var lines = File.ReadAllLines(Path.Combine(inc.Dir, "events.csv")).Skip(1).ToList();
        Assert.Equal(900, lines.Count);
        for (int i = 0; i < 900; i++) Assert.Contains($"--step {i} ", lines[i]);
        Assert.DoesNotContain(lines, l => l.Contains(",Gap,") || l.Contains(",Note,"));
    }

    /// <summary>
    /// The events as an incident whose first part starts at <paramref name="fromUtc"/> has them (the window's start as its
    /// first part last followed it): the manager says from where an incident's first part is
    /// (<see cref="IncidentEventSet.Of"/> gives the events no start of their own).
    /// </summary>
    private static IncidentEventSet From(List<LifecycleEvent> events, DateTime fromUtc) =>
        IncidentEventSet.Of(events) with { FromMs = 1_000_000 + (long)(fromUtc - T0).TotalMilliseconds, FromUtc = fromUtc };

    /// <summary>report.md's line under the window's events ("(N other events ..."), or "" when it has none.</summary>
    private static string EventsLine(Incident inc) =>
        File.ReadAllLines(Path.Combine(inc.Dir, "report.md")).FirstOrDefault(l => l.StartsWith('(') && l.Contains(" other events in ")) ?? "";

    /// <summary>The event rows of an events.csv, each as its PID less 10,000 (the number <see cref="Events"/> gave it), in the file's order.</summary>
    private static List<int> Steps(Incident inc) => File.ReadAllLines(Path.Combine(inc.Dir, "events.csv")).Skip(1)
        .Select(PoolWatch.Core.Util.Csv.Split).Where(c => c[1] == "Start").Select(c => int.Parse(c[2]) - 10_000).ToList();

    private const string AtClose = "when the incident closes PoolWatch replaces the file with both ends of its window as it is measured then, its first events and its latest, " +
                                   "up to 500 of each, with a Gap row between them when it leaves any out";
    private const string DayHasEvery = ", and the day's `data\\<yyyyMMdd>\\events.csv` has every one";

    /// <summary>
    /// A file stays as written while its incident's first part has not followed the window's start (an open incident's
    /// follows at most every 10 minutes): report.md then says from where the file's rows are and where the window starts
    /// now, whichever way the onset moved, by the times it gives, and nothing more once the two are one again. (Before
    /// the first part followed the window's start, this was every file's state from the first onset measured again on.)
    /// </summary>
    [Fact]
    public void ReportMdSaysWhereAFilesRowsAreFromWhileTheWindowStartsSomewhereElse()
    {
        var host = new CountingHost(_dir);
        var writer = new IncidentWriter(new TagKnowledge(null, null), host, new ProcessRegistry());
        var inc = NewIncident();
        var h = new History();
        var events = Events(0, 400);
        var began = T0.AddMinutes(-2); // the window's start as the file was begun: 2 minutes before the onset then
        string path = Path.Combine(inc.Dir, "events.csv");
        writer.Write(h, inc, From(events, began));
        string written = EventsCsv(inc);
        Assert.Equal(401, written.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal("(400 other events in events.csv)", EventsLine(inc)); // the window starts where the file's rows are from: nothing more to say
        string holds = $"(400 other events in events.csv; it holds events from {Fmt.Clock(began)} on";
        string Now(DateTime onset) => $"; the incident's window now starts at {Fmt.Clock(onset.AddMinutes(-2))}";
        string Md(DateTime onset)
        {
            (inc.OnsetUtc, inc.OnsetMs) = (onset, 1_000_000 + (long)(onset - T0).TotalMilliseconds);
            writer.Write(h, inc, From(events, began)); // the first part has not followed yet
            Assert.Equal(written, EventsCsv(inc)); // every row, as written
            Assert.Equal(1, host.Replaced.GetValueOrDefault(path)); // made once, not written again
            return EventsLine(inc);
        }

        // The onset is measured 5 minutes later: the window now starts 3 minutes after the file's first row, and the report
        // says the 180 rows before that are from before the window.
        Assert.Equal($"{holds}{Now(T0.AddMinutes(5))}: the file's events from before then are not the window's)", Md(T0.AddMinutes(5)));
        // A minute later than it was: the window now starts before the file's first row (its events begin 2 minutes after
        // where the file's first part starts), so no row is from before it.
        Assert.Equal($"{holds}{Now(T0.AddMinutes(1))})", Md(T0.AddMinutes(1)));
        // 3 minutes earlier: the window's events from before the file's start are not in the file.
        Assert.Equal($"{holds}{Now(T0.AddMinutes(-3))}, and its events before {Fmt.Clock(began)} are not in the file)", Md(T0.AddMinutes(-3)));
        // Where it was when the file was begun.
        Assert.Equal("(400 other events in events.csv)", Md(T0));
    }

    /// <summary>
    /// Once the incident's first part has followed the window's start, an open incident's file that still gets rows starts
    /// again from it: written again from the new start with the rows the first part holds, the ones the file had among them
    /// (it loses none of those), and up to a write's rows more. A write that brings no rows leaves the file as it is, and
    /// report.md says what the file holds and that it starts again; and while the first part holds no event the file keeps
    /// every row it has and gets no Note row. (Before, a file whose rows were all before the window's start was written
    /// again with none of the events it had, and a Note row on the empty file, when the first part had not followed.)
    /// </summary>
    [Fact]
    public void AnOpenIncidentsFileStartsAgainFromItsFirstPartOnceThatFollowedTheWindowsStart()
    {
        var host = new CountingHost(_dir);
        var writer = new IncidentWriter(new TagKnowledge(null, null), host, new ProcessRegistry());
        var h = new History();
        var began = T0.AddMinutes(-2);
        var later = T0.AddMinutes(1); // the window's start once the onset is measured 3 minutes later: at event 60's time
        Incident Open(string id) => new()
        {
            Id = id, Dir = Path.Combine(_dir, id), Tag = PoolTag.Parse("File"), Kind = IncidentKind.Growth, OpenedUtc = T0, OpenedMs = 1_000_000, OnsetUtc = T0, OnsetMs = 1_000_000,
        };
        void Measured3MinutesLater(Incident inc) => (inc.OnsetUtc, inc.OnsetMs) = (T0.AddMinutes(3), 1_000_000 + 180_000);
        string was = $"; it holds events from {Fmt.Clock(began)} on; the incident's window now starts at {Fmt.Clock(later)}";

        // 100 rows written (events 0 to 99), the rest waiting; the first part then follows the start to event 60's time.
        var inc = Open("20261109-220010-File-656c6946");
        writer.Write(h, inc, From(Events(0, 400), began), maxRows: 100);
        Assert.Equal(Enumerable.Range(0, 100), Steps(inc));
        Measured3MinutesLater(inc);
        var followed = From(Events(60, 400), later);
        // A write without rows (a report's refresh): the file is as it was, and the report says so, and what it gets.
        writer.Write(h, inc, followed, maxRows: 0);
        Assert.Equal(Enumerable.Range(0, 100), Steps(inc));
        Assert.True(writer.EventsPending(inc));
        Assert.Equal($"(340 other events in the window; events.csv starts again with the first events from {Fmt.Clock(later)}, up to 500 in all, as PoolWatch's write budget allows, " +
                     $"and {AtClose.Replace("closes", "closes with more events than that,")}{DayHasEvery}{was}: the file's events from before then are not the window's)", EventsLine(inc));
        // A write of rows: from the new start, the 40 rows the file had from there on (60 to 99) and 100 more, none missing.
        writer.AppendEvents(inc, followed, maxRows: 100);
        Assert.Equal(Enumerable.Range(60, 140), Steps(inc));
        Assert.DoesNotContain(File.ReadAllLines(Path.Combine(inc.Dir, "events.csv")), l => l.Contains(",Note,"));
        writer.Write(h, inc, followed, maxRows: 0);
        Assert.Equal($"(340 other events in the window; events.csv gets more of the window's first events, up to 500 in all, as PoolWatch's write budget allows, " +
                     $"and {AtClose.Replace("closes", "closes with more events than that,")}{DayHasEvery})", EventsLine(inc));
        writer.AppendEvents(inc, followed, maxRows: 10_000);
        Assert.Equal(Enumerable.Range(60, 340), Steps(inc));

        // The start measured so much later that the first part holds none of the file's rows: the file starts again with
        // the first part's own (events 200 on).
        var past = Open("20261109-220011-File-656c6946");
        writer.Write(h, past, From(Events(0, 400), began), maxRows: 100);
        (past.OnsetUtc, past.OnsetMs) = (T0.AddSeconds(320), 1_000_000 + 320_000);
        writer.AppendEvents(past, From(Events(200, 400), T0.AddSeconds(200)), maxRows: 100);
        Assert.Equal(Enumerable.Range(200, 100), Steps(past));

        // The start measured earlier (from event 0's time on, where the file was begun 2 minutes before it): the first
        // part is the same events, and the file's rows are its leading ones: the file is written again with them and
        // 100 more, as a file from the new start.
        var earlier = Open("20261109-220012-File-656c6946");
        writer.Write(h, earlier, From(Events(0, 400), began), maxRows: 100);
        (earlier.OnsetUtc, earlier.OnsetMs) = (T0.AddMinutes(-1), 1_000_000 - 60_000);
        writer.AppendEvents(earlier, From(Events(0, 400), T0.AddMinutes(-3)), maxRows: 100);
        Assert.Equal(Enumerable.Range(0, 200), Steps(earlier));

        // The first part followed and holds no event (none has come from the new start on yet): the file keeps its 100
        // rows, gets no Note row and no more rows, and the report says where its rows are from. When events come, it
        // starts again with them.
        var none = Open("20261109-220013-File-656c6946");
        writer.Write(h, none, From(Events(0, 400), began), maxRows: 100);
        Measured3MinutesLater(none);
        var empty = From([], later) with { CutUtc = later, CutMs = 1_000_000 + 60_000 };
        writer.Write(h, none, empty, maxRows: 100);
        writer.AppendEvents(none, empty, maxRows: 100);
        Assert.Equal(Enumerable.Range(0, 100), Steps(none));
        Assert.DoesNotContain(File.ReadAllLines(Path.Combine(none.Dir, "events.csv")), l => l.Contains(",Note,"));
        Assert.False(writer.EventsPending(none));
        Assert.Equal($"(100 other events in events.csv{was}: the file's events from before then are not the window's)", EventsLine(none));
        writer.AppendEvents(none, From(Events(400, 450), later), maxRows: 100);
        Assert.Equal(Enumerable.Range(400, 50), Steps(none));

        // A file whose Note row says to when it holds its events stays as written when the first part follows: no row
        // more until the close. report.md says how many events it holds and from when (not "the window's first"), how
        // many more have come since on the incident's count across the follow (5,500 after its 500), and where the
        // window starts now.
        var noted = Open("20261109-220014-File-656c6946");
        writer.Write(h, noted, From(Events(0, 6000), began));
        string file = EventsCsv(noted);
        Assert.Contains(",Note,", File.ReadAllLines(Path.Combine(noted.Dir, "events.csv"))[^1]);
        Assert.Equal($"(500 other events in events.csv: while the incident is open it holds the window's first 500 events, to {Fmt.Clock(T0.AddSeconds(500))}, " +
                     $"as PoolWatch had them when it wrote them; {5500:N0} more have come since, and {AtClose})", EventsLine(noted));
        Measured3MinutesLater(noted);
        // The first part taken again from event 60 on: 5,940 events from there, and 60 taken out of its count.
        var again = From(Events(60, 6000), later) with { TakenIn = 6000, Carried = 60 };
        writer.Write(h, noted, again);
        writer.AppendEvents(noted, again, maxRows: 10_000);
        Assert.Equal(file, EventsCsv(noted));
        Assert.Equal($"(500 other events in events.csv: while the incident is open it holds the first 500 events from {Fmt.Clock(began)}, to {Fmt.Clock(T0.AddSeconds(500))}, " +
                     $"as PoolWatch had them when it wrote them; {5500:N0} more have come since, and {AtClose}" +
                     $"; the incident's window now starts at {Fmt.Clock(later)}: the file's events from before then are not the window's)", EventsLine(noted));
        // Closed: written whole from the new start, the first 500 from event 60 and the latest 500.
        (noted.ClosedUtc, noted.ClosedMs) = (T0.AddHours(2), 1_000_000 + 7_200_000);
        writer.Write(h, noted, again);
        var closed = Steps(noted);
        Assert.Equal(Enumerable.Range(60, 500).Concat(Enumerable.Range(5500, 500)), closed);
        Assert.Equal($"(1000 other events in events.csv; it leaves out {4940:N0} events, between {Fmt.Clock(T0.AddSeconds(560))} and {Fmt.Clock(T0.AddSeconds(5499))}, as its Gap row says)", EventsLine(noted));
    }

    /// <summary>
    /// A closed incident's file follows too: one with its first part alone (under 500 events) is written again from the
    /// new start by its next write, without the rows from before it, and one with both ends is replaced whole by a write
    /// that must be made even when the first part only let go of its rows from before the new start (the file would
    /// otherwise count as holding both ends as they are).
    /// </summary>
    [Fact]
    public void AClosedIncidentsFileIsWrittenAgainFromTheWindowsStartOnceItsFirstPartFollowedIt()
    {
        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        var h = new History();
        var began = T0.AddMinutes(-2);
        var shortOne = NewIncident();
        writer.Write(h, shortOne, From(Events(0, 400), began));
        (shortOne.ClosedUtc, shortOne.ClosedMs) = (T0.AddMinutes(7), 1_000_000 + 420_000);
        (shortOne.OnsetUtc, shortOne.OnsetMs) = (T0.AddMinutes(5), 1_000_000 + 300_000);
        var later = From(Events(180, 400), T0.AddMinutes(3));
        // Every event of its first part is in the file already, among rows from before the window: the file does not hold
        // the window's events as the report lists them, and a write gives it them.
        Assert.True(writer.LacksEvents(shortOne, later));
        writer.Write(h, shortOne, later);
        Assert.Equal(Enumerable.Range(180, 220), Steps(shortOne)); // the window's, from its start as measured at the close
        Assert.Equal("(220 other events in events.csv)", EventsLine(shortOne));
        Assert.False(writer.LacksEvents(shortOne, later));

        var both = new Incident
        {
            Id = "20261109-220020-File-656c6946", Dir = Path.Combine(_dir, "20261109-220020-File-656c6946"), Tag = PoolTag.Parse("File"), Kind = IncidentKind.Growth,
            OpenedUtc = T0, OpenedMs = 1_000_000, OnsetUtc = T0, OnsetMs = 1_000_000, ClosedUtc = T0.AddHours(2), ClosedMs = 1_000_000 + 7_200_000,
        };
        var events = Events(0, 6000);
        var whole = From(events, began);
        writer.Write(h, both, whole);
        Assert.Equal(Enumerable.Range(0, 500).Concat(Enumerable.Range(5500, 500)), Steps(both));
        Assert.False(writer.BothEndsDue(both, whole));
        // The onset is measured a minute later (a trace attached, say), and the first part lets go of its 60 rows from
        // before the new start: nothing of it is new to the file, and the file is still to be replaced.
        (both.OnsetUtc, both.OnsetMs) = (T0.AddMinutes(3), 1_000_000 + 180_000);
        var kept = whole with { Kept = events.GetRange(60, 440), FromMs = 1_000_000 + 60_000, FromUtc = T0.AddMinutes(1) };
        Assert.True(writer.BothEndsDue(both, kept));
        Assert.True(writer.LacksEvents(both, kept));
        writer.Write(h, both, kept);
        Assert.Equal(Enumerable.Range(60, 440).Concat(Enumerable.Range(5500, 500)), Steps(both));
        Assert.False(writer.LacksEvents(both, kept));
        static string T(DateTime t) => PoolWatch.Core.Util.Csv.Escape(t);
        var gap = PoolWatch.Core.Util.Csv.Split(Assert.Single(File.ReadAllLines(Path.Combine(both.Dir, "events.csv")), l => l.Contains(",Gap,")));
        Assert.Equal($"5,000 events left out, between {T(T0.AddSeconds(500))} and {T(T0.AddSeconds(5499))} " +
                     $"(events.csv keeps the first 440 events from {T(T0.AddMinutes(1))}, where the incident's window started when this file was written, and the latest 500 up to the window's end)", gap[9]);
    }

    /// <summary>
    /// A closed incident's file with its first part alone can come to hold a row that is no longer one of its first
    /// part's. Its window had exactly a first part's 500 events at its close, and the file holds them. A trace attached
    /// after the close then measures the window's end earlier: the first part's last 50 events are from after the
    /// window. An event that comes late, timed after that end and before the first part's last event, takes its place
    /// in the first part (the earliest 500 from the window's start), and the last one moves out: it is none of the
    /// window's, and the incident no longer holds it for its file. No event of the window is new to the file, and its
    /// first part did not follow a start measured somewhere else: the row that moved out is all that tells that the
    /// file is not as the incident holds it. It tells both checks (the write two minutes after the window's end,
    /// <c>IncidentWriter.LacksEvents</c>, and the rows that wait for a write that can wait, <c>RowsWait</c>), and the
    /// write that follows starts the file again from the first part: its events up to the window's end, none from
    /// after it.
    /// </summary>
    [Fact]
    public void AClosedIncidentsFileStartsAgainWhenItHoldsARowThatIsNoLongerOneOfItsFirstParts()
    {
        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        var h = new History();
        var inc = NewIncident();
        (inc.ClosedUtc, inc.ClosedMs) = (T0.AddMinutes(10), 1_000_000 + 600_000);
        var first = Events(0, 500);
        var whole = From(first, T0.AddMinutes(-2));
        writer.Write(h, inc, whole);
        Assert.Equal(Enumerable.Range(0, 500), Steps(inc));
        Assert.False(writer.LacksEvents(inc, whole));
        Assert.False(writer.RowsWait(inc, whole));

        // The window's end is measured at event 449's time, and a late event timed between events 460 and 461 comes:
        // the first part is events 0 to 460, the late one, and 461 to 498; event 499 has moved out of it.
        var late = new LifecycleEvent { TimeUtc = T0.AddSeconds(460.5), Ms = 1_000_000 + 460_500, Kind = LifecycleKind.Rollup, Pid = 19_000, Image = "git.exe", Count = 2 };
        var kept = first.Take(461).Append(late).Concat(first.Skip(461).Take(38)).ToList();
        Assert.Equal(500, kept.Count);
        var moved = whole with { Kept = kept, EndMs = first[449].Ms, CutUtc = first[499].TimeUtc, CutMs = first[499].Ms };
        Assert.True(writer.LacksEvents(inc, moved));
        Assert.True(writer.RowsWait(inc, moved));
        writer.Write(h, inc, moved);
        Assert.Equal(Enumerable.Range(0, 450), Steps(inc)); // the window's events, to its end: none from after it
        Assert.DoesNotContain(File.ReadAllLines(Path.Combine(inc.Dir, "events.csv")), l => l.Contains(",Gap,") || l.Contains(",Note,") || l.Contains(",Rollup,"));
        Assert.False(writer.LacksEvents(inc, moved));
        Assert.False(writer.RowsWait(inc, moved));
    }

    /// <summary>
    /// A file with both ends that stays as written while the window's start moves later (a re-opened incident's, as its
    /// last close wrote it): report.md says the file's events from before where the window starts now are not the
    /// window's, and, only when the events the file leaves out begin before then too, that the first of those are not
    /// either.
    /// </summary>
    [Fact]
    public void ReportMdSaysWhenTheEventsAFileLeavesOutBeginBeforeWhereTheWindowStartsNow()
    {
        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        var h = new History();
        var began = T0.AddMinutes(-2);
        var inc = new Incident
        {
            Id = "20261109-220030-File-656c6946", Dir = Path.Combine(_dir, "20261109-220030-File-656c6946"), Tag = PoolTag.Parse("File"), Kind = IncidentKind.Growth,
            OpenedUtc = T0, OpenedMs = 1_000_000, OnsetUtc = T0, OnsetMs = 1_000_000, ClosedUtc = T0.AddHours(2), ClosedMs = 1_000_000 + 7_200_000,
        };
        var whole = From(Events(0, 6000), began);
        writer.Write(h, inc, whole);
        Assert.Equal(Enumerable.Range(0, 500).Concat(Enumerable.Range(5500, 500)), Steps(inc)); // it leaves out events 500 to 5,499
        string written = EventsCsv(inc);
        // Open again: its file stays as its close wrote it, wherever the onset is measured meanwhile.
        (inc.ClosedUtc, inc.ClosedMs) = (null, null);
        string Md(int startsAtEvent)
        {
            (inc.OnsetUtc, inc.OnsetMs) = (T0.AddSeconds(startsAtEvent + 120), 1_000_000 + (startsAtEvent + 120) * 1000L);
            writer.Write(h, inc, whole);
            Assert.Equal(written, EventsCsv(inc));
            return EventsLine(inc);
        }
        string leaves = $"(1000 other events in events.csv; it leaves out {5000:N0} events, between {Fmt.Clock(T0.AddSeconds(500))} and {Fmt.Clock(T0.AddSeconds(5499))}, " +
                        $"as its Gap row says; it holds events from {Fmt.Clock(began)} on; the incident's window now starts at ";
        // From event 300's time: the file's first 300 rows are from before the window, and those it leaves out begin after its start.
        Assert.Equal($"{leaves}{Fmt.Clock(T0.AddSeconds(300))}: the file's events from before then are not the window's)", Md(300));
        // From event 600's time: past its whole first part, and past the first of the events it leaves out.
        Assert.Equal($"{leaves}{Fmt.Clock(T0.AddSeconds(600))}: the file's events from before then are not the window's, nor are the first of those it leaves out)", Md(600));
    }

    /// <summary>
    /// The same in report.md's other lines: a closed short incident whose rows wait for the write budget, and a file whose
    /// every row is listed in the report (nothing to say of it, but when the window's start is not where its rows are from).
    /// </summary>
    [Fact]
    public void ReportMdSaysWhereTheFilesRowsAreFromWhileRowsWaitAndWhenEveryRowIsListed()
    {
        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        var h = new History();
        var began = T0.AddMinutes(-2);
        Incident Closed(string id, DateTime onset) => new()
        {
            Id = id, Dir = Path.Combine(_dir, id), Tag = PoolTag.Parse("File"), Kind = IncidentKind.Growth, OpenedUtc = T0, OpenedMs = 1_000_000,
            OnsetUtc = onset, OnsetMs = 1_000_000 + (long)(onset - T0).TotalMilliseconds, ClosedUtc = T0.AddHours(1), ClosedMs = 1_000_000 + 3_600_000,
        };
        // Closed, its window starting 5 minutes later than its first part does (one that did not follow: made with its
        // events, say), 100 of its 400 rows written and the rest waiting.
        var waiting = Closed("20261109-230000-File-656c6946", T0.AddMinutes(5));
        writer.Write(h, waiting, From(Events(0, 400), began), maxRows: 100);
        Assert.Equal($"(400 other events in the window; events.csv gets the rest when PoolWatch's write budget has room{DayHasEvery}" +
                     $"; it holds events from {Fmt.Clock(began)} on; the incident's window now starts at {Fmt.Clock(T0.AddMinutes(3))}: the file's events from before then are not the window's)",
            EventsLine(waiting));

        // Three exits, each listed in the report: no line when the window starts where the file's first part does; when
        // it starts earlier, the line says the window's events before the file's are not in it.
        List<LifecycleEvent> Exits() => Events(0, 3).Select(e => e with { Kind = LifecycleKind.Exit }).ToList();
        var listed = Closed("20261109-230001-File-656c6946", T0);
        writer.Write(h, listed, From(Exits(), began));
        Assert.Equal(4, File.ReadAllLines(Path.Combine(listed.Dir, "events.csv")).Length);
        Assert.Equal("", EventsLine(listed));
        var earlier = Closed("20261109-230002-File-656c6946", T0.AddMinutes(-3));
        writer.Write(h, earlier, From(Exits(), began));
        Assert.Equal($"(0 other events in events.csv; it holds events from {Fmt.Clock(began)} on; the incident's window now starts at {Fmt.Clock(T0.AddMinutes(-5))}, " +
                     $"and its events before {Fmt.Clock(began)} are not in the file)", EventsLine(earlier));
        // A file with no event at all says nothing of where its events are from, wherever the window starts.
        var empty = Closed("20261109-230003-File-656c6946", T0.AddMinutes(-3));
        writer.Write(h, empty, From([], began));
        Assert.Single(File.ReadAllLines(Path.Combine(empty.Dir, "events.csv"))); // its header
        Assert.Equal("", EventsLine(empty));

        // PoolWatch no longer held every event from the window's start as the first part was taken (it had let go of
        // those up to a second before the first it still held): both lines say what the file's events are, though the
        // window starts where the first part does. First with rows waiting, then with every row written.
        var lost = Closed("20261109-230004-File-656c6946", T0);
        var lostTo = T0.AddSeconds(-1);
        string held = $"; it holds the events PoolWatch still held from {Fmt.Clock(began)} (by then it had already let go of events up to {Fmt.Clock(lostTo)}))";
        writer.Write(h, lost, From(Events(0, 400), began) with { LostToUtc = lostTo }, maxRows: 100);
        Assert.Equal($"(400 other events in the window; events.csv gets the rest when PoolWatch's write budget has room{DayHasEvery}" + held, EventsLine(lost));
        writer.Write(h, lost, From(Events(0, 400), began) with { LostToUtc = lostTo });
        Assert.Equal(401, File.ReadAllLines(Path.Combine(lost.Dir, "events.csv")).Length);
        Assert.Equal("(400 other events in events.csv" + held, EventsLine(lost));
    }

    /// <summary>
    /// A file whose rows are all of its latest part and that leaves no event out (its incident kept none in its first part,
    /// and under 500 after it) holds every event from where its first part starts: report.md says it "holds events from"
    /// there, not that it holds only its latest ones (which is said of a file that leaves events out before them:
    /// <c>IncidentPersistenceTests.AnIncidentThatKeptNoEventBeforeTheBufferLetThemGo...</c>).
    /// </summary>
    [Fact]
    public void AFileOfItsLatestPartAloneThatLeavesNoEventOutHoldsEveryEventFromWhereItsFirstPartStarts()
    {
        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        var began = T0.AddMinutes(-2);
        var onset = T0.AddMinutes(1); // the window starts a minute after the first part, and a minute before the first event
        var inc = new Incident
        {
            Id = "20261109-230005-File-656c6946", Dir = Path.Combine(_dir, "20261109-230005-File-656c6946"), Tag = PoolTag.Parse("File"), Kind = IncidentKind.Growth,
            OpenedUtc = T0, OpenedMs = 1_000_000, OnsetUtc = onset, OnsetMs = 1_000_000 + 60_000, ClosedUtc = T0.AddHours(1), ClosedMs = 1_000_000 + 3_600_000,
        };
        var events = Events(0, 300);
        writer.Write(new History(), inc, new IncidentEventSet(events, [], 880_000, long.MaxValue, began, 880_000) { FromUtc = began, Tail = events });
        var lines = File.ReadAllLines(Path.Combine(inc.Dir, "events.csv"));
        Assert.Equal(301, lines.Length);
        Assert.DoesNotContain(lines, l => l.Contains(",Gap,"));
        Assert.Equal($"(300 other events in events.csv; it holds events from {Fmt.Clock(began)} on; the incident's window now starts at {Fmt.Clock(onset.AddMinutes(-2))})", EventsLine(inc));
    }

    /// <summary>
    /// The Gap row says what the file keeps, counted from its rows, when it has only one of its two parts or neither: a
    /// closed incident that let go of its latest part and then lost its file (the file written again has the first part
    /// alone), with and without a start of its own, one whose first part does not start where its window does (the row
    /// then gives the time alone), and one that holds no event at all.
    /// </summary>
    [Fact]
    public void TheGapRowSaysWhatTheFileKeepsWhenItHasOneOfItsTwoPartsOrNeither()
    {
        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        var h = new History();
        var began = T0.AddMinutes(-2);
        string Gap(string id, IncidentEventSet ev)
        {
            var inc = new Incident
            {
                Id = id, Dir = Path.Combine(_dir, id), Tag = PoolTag.Parse("File"), Kind = IncidentKind.Growth,
                OpenedUtc = T0, OpenedMs = 1_000_000, OnsetUtc = T0, OnsetMs = 1_000_000, ClosedUtc = T0.AddHours(2), ClosedMs = 1_000_000 + 7_200_000,
            };
            writer.Write(h, inc, ev);
            var lines = File.ReadAllLines(Path.Combine(inc.Dir, "events.csv"));
            Assert.Equal(1 + ev.Kept.Count + 1, lines.Length); // the header, the first part and the Gap row
            return PoolWatch.Core.Util.Csv.Split(Assert.Single(lines, l => l.Contains(",Gap,")))[9];
        }
        string Md(string id) => File.ReadAllLines(Path.Combine(_dir, id, "report.md")).Single(l => l.Contains(" other events in "));
        static string T(DateTime t) => PoolWatch.Core.Util.Csv.Escape(t);
        // 5,500 events after the first 500, all let go (its latest part with them).
        var leftOut = new EventGap(5_500, T0.AddSeconds(500), T0.AddSeconds(5_999), 1_500_000, 6_999_000);
        string between = $"5,500 events left out, between {T(T0.AddSeconds(500))} and {T(T0.AddSeconds(5_999))} ";
        string leaves = $" other events in events.csv; it leaves out {5500:N0} events, between {Fmt.Clock(T0.AddSeconds(500))} and {Fmt.Clock(T0.AddSeconds(5_999))}, as its Gap row says";
        var first = Events(0, 500);
        IncidentEventSet FirstAlone() => new(first, first, long.MinValue, long.MaxValue, T0.AddSeconds(500), 1_500_000) { Gap = leftOut, LatestReleased = true };
        Assert.Equal($"{between}(events.csv keeps the incident window's first 500 events)", Gap("20261109-220000-File-656c6946", FirstAlone()));
        Assert.Equal($"{between}(events.csv keeps the first 500 events from {T(began)}, where the incident's window started when this file was written)",
            Gap("20261109-220001-File-656c6946", FirstAlone() with { FromMs = 880_000, FromUtc = began }));
        Assert.Equal($"(500{leaves})", Md("20261109-220001-File-656c6946"));
        // Its first part starts a minute before its window does (an incident made with its events, whose first part does
        // not follow): the row gives the time alone, and report.md says where the window starts.
        const string apart = "20261109-220003-File-656c6946";
        var before = began.AddMinutes(-1);
        Assert.Equal($"{between}(events.csv keeps the first 500 events from {T(before)})", Gap(apart, FirstAlone() with { FromMs = 820_000, FromUtc = before }));
        Assert.Equal($"(500{leaves}; it holds events from {Fmt.Clock(before)} on; the incident's window now starts at {Fmt.Clock(began)})", Md(apart));
        // No event at all, of an incident whose first part starts a minute earlier than its window: the row says the file
        // keeps none, and report.md says where the window starts now, and nothing of where the file's events are from.
        const string none = "20261109-220002-File-656c6946";
        Assert.Equal($"{between}(events.csv keeps none of the incident window's events)",
            Gap(none, new IncidentEventSet([], [], 820_000, long.MaxValue, before, 820_000) { FromUtc = before, Gap = leftOut, LatestReleased = true }));
        Assert.Equal($"(0{leaves}; the incident's window now starts at {Fmt.Clock(began)})", Md(none));
    }
}
