using PoolWatch.Core;
using PoolWatch.Core.Detection;
using PoolWatch.Core.History;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;

namespace PoolWatch.Tests;

/// <summary>
/// An incident's events.csv is appended to while it is open, not written again whole at every refresh: each event of the
/// window's first part once, the earliest first, never more than 1,000; when it closes it is replaced whole, once, with both
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
        Assert.Equal(1 + IncidentWriter.MaxEventRows + 1, lines.Length); // the header, the first 1,000 events and the row that says the rest come at its close
        // The row says, as a fact about this file (true however the run ends), to when it holds the window's events: the time
        // of the first event not in its first part (the 1,001st); and what PoolWatch does with it at the close.
        Assert.Equal(1000, IncidentWriter.MaxEventRows);
        Assert.Contains("events.csv holds the incident window's first 1,000 events, to this row's time; the window's later events are not in this file " +
                        "(when the incident closes PoolWatch replaces it with the first 1,000, a Gap row and the latest 1,000)", PoolWatch.Core.Util.Csv.Split(lines[^1])[9]);
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
        // The header, events 0 to 999, the Gap row, events 5,000 to 5,999: sorted by time, each once, no note.
        Assert.Equal(1 + 2 * IncidentWriter.MaxEventRows + 1, lines.Length);
        Assert.Contains("--step 999 ", lines[IncidentWriter.MaxEventRows]);
        string gap = lines[IncidentWriter.MaxEventRows + 1];
        Assert.StartsWith(PoolWatch.Core.Util.Csv.Line(T0.AddSeconds(1000), "Gap"), gap);
        Assert.Contains($"4,000 events left out, between {PoolWatch.Core.Util.Csv.Escape(T0.AddSeconds(1000))} and {PoolWatch.Core.Util.Csv.Escape(T0.AddSeconds(4999))}", gap);
        Assert.Contains("--step 5000 ", lines[IncidentWriter.MaxEventRows + 2]);
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
    /// open incident's while the write budget holds rows back (the rest of the first 1,000 as the budget allows, the latest
    /// 1,000 at its close; never "the rest" of the window), then once its first part is all written (how many it holds, to
    /// when, and how many more the window has so far), and a closed short one's while rows wait.
    /// </summary>
    [Fact]
    public void ReportMdSaysWhatEventsCsvHoldsAndGetsWhileTheIncidentIsOpenOrItsRowsWait()
    {
        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        var inc = NewIncident();
        var h = new History();
        var events = Events(0, 6000);
        string Md() => File.ReadAllText(Path.Combine(inc.Dir, "report.md"));
        writer.Write(h, inc, events, maxRows: 300);
        Assert.True(writer.EventsPending(inc));
        // None is listed (no suspect's start): all 6,000 are the others.
        Assert.Contains("(6000 other events in the window; events.csv gets the rest of the window's first 1,000 events as PoolWatch's write budget allows " +
                        "and its latest 1,000 when the incident closes, and the day's `data\\<yyyyMMdd>\\events.csv` has every one)", Md());
        writer.AppendEvents(inc, events, maxRows: 10_000);
        writer.Write(h, inc, events);
        Assert.Contains($"(1000 other events in events.csv: while the incident is open it holds the window's first 1,000 events, to {Fmt.Clock(T0.AddSeconds(1000))}; " +
                        $"the window has {5000:N0} more so far, and the latest of them, up to 1,000, are added when it closes)", Md());

        var shortOne = new Incident
        {
            Id = "20261109-230000-File-656c6946", Dir = Path.Combine(_dir, "20261109-230000-File-656c6946"), Tag = PoolTag.Parse("File"), Kind = IncidentKind.Growth,
            OpenedUtc = T0, OpenedMs = 1_000_000, OnsetUtc = T0, OnsetMs = 1_000_000, ClosedUtc = T0.AddHours(1), ClosedMs = 1_000_000 + 3_600_000,
        };
        writer.Write(h, shortOne, Events(0, 500), maxRows: 100);
        Assert.Contains("(500 other events in the window; events.csv gets the rest when PoolWatch's write budget has room, and the day's `data\\<yyyyMMdd>\\events.csv` has every one)",
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
        // Late: timed between events 500 and 501, it takes a place in the first part; event 999 moves on.
        events.Add(Events(0, 1)[0] with { TimeUtc = T0.AddSeconds(500.5), Ms = 1_000_000 + 500_500, Pid = 99_999, CommandLine = "node late.js" });
        writer.Write(h, inc, events, maxRows: 10_000);
        Assert.Equal(open, File.ReadAllLines(path)); // as it was
        (inc.ClosedUtc, inc.ClosedMs) = (T0.AddHours(2), 1_000_000 + 7_200_000);
        writer.Write(h, inc, events);
        var closed = File.ReadAllLines(path).Skip(1).ToList();
        Assert.Contains("late.js", closed[501]);
        Assert.Contains("--step 998 ", closed[IncidentWriter.MaxEventRows - 1]);
        Assert.Contains(",Gap,", closed[IncidentWriter.MaxEventRows]);
        Assert.DoesNotContain(closed, l => l.Contains("--step 999 "));
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
        // Events 0 to 2,000: the first part 0 to 999, event 1,000 left out, the latest part 1,001 to 2,000.
        var tie = Closed("20261109-220000-File-656c6946");
        var events = Events(0, 2001);
        events[1001] = events[1001] with { TimeUtc = events[1000].TimeUtc };
        writer.Write(new History(), tie, events);
        var lines = File.ReadAllLines(Path.Combine(tie.Dir, "events.csv")).Skip(1).ToList();
        Assert.Contains(",Gap,", lines[IncidentWriter.MaxEventRows]);
        Assert.Contains("--step 999 ", lines[IncidentWriter.MaxEventRows - 1]);
        Assert.Contains("--step 1001 ", lines[IncidentWriter.MaxEventRows + 1]);

        var setBack = Closed("20261109-220001-File-656c6946");
        events = Events(0, 2001);
        for (int i = 1001; i <= 2000; i++) events[i] = events[i] with { TimeUtc = events[i].TimeUtc.AddHours(-1) };
        writer.Write(new History(), setBack, events);
        lines = File.ReadAllLines(Path.Combine(setBack.Dir, "events.csv")).Skip(1).ToList();
        Assert.Equal(1 + 2 * IncidentWriter.MaxEventRows, lines.Count);
        for (int i = 0; i < IncidentWriter.MaxEventRows; i++) Assert.Contains($"--step {i} ", lines[i]);
        Assert.Contains(",Gap,", lines[IncidentWriter.MaxEventRows]);
        for (int i = 0; i < IncidentWriter.MaxEventRows; i++) Assert.Contains($"--step {1001 + i} ", lines[IncidentWriter.MaxEventRows + 1 + i]);
    }

    [Fact]
    public void AClosedIncidentWithFewerThanTwoThousandEventsHoldsEachOnceAndNoGapRow()
    {
        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        var inc = NewIncident();
        (inc.ClosedUtc, inc.ClosedMs) = (T0.AddHours(1), 1_000_000 + 3_600_000);
        writer.Write(new History(), inc, Events(0, 1500));
        var lines = File.ReadAllLines(Path.Combine(inc.Dir, "events.csv")).Skip(1).ToList();
        Assert.Equal(1500, lines.Count);
        for (int i = 0; i < 1500; i++) Assert.Contains($"--step {i} ", lines[i]);
        Assert.DoesNotContain(lines, l => l.Contains(",Gap,") || l.Contains(",Note,"));
    }

    [Fact]
    public void AClosedIncidentWhoseOnsetMovedLaterLosesTheRowsBeforeItsWindow()
    {
        var host = new CountingHost(_dir);
        var writer = new IncidentWriter(new TagKnowledge(null, null), host, new ProcessRegistry());
        var inc = NewIncident();
        var h = new History();
        var events = Events(0, 400);
        writer.Write(h, inc, events);
        // The ranking moved the onset 5 minutes later; the incident closes. Its window starts 2 minutes before the onset:
        // the rows before that go, the rest stay.
        inc.OnsetMs += 300_000;
        (inc.ClosedUtc, inc.ClosedMs) = (T0.AddMinutes(7), 1_000_000 + 420_000);
        writer.Write(h, inc, events);
        var times = File.ReadAllLines(Path.Combine(inc.Dir, "events.csv")).Skip(1).Select(l => DateTime.Parse(l[..l.IndexOf(',')], null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime()).ToList();
        Assert.Equal(400 - 180, times.Count);
        Assert.All(times, t => Assert.True(t >= T0.AddSeconds(180)));
    }
}
