using PoolWatch.Core;
using PoolWatch.Core.History;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;

namespace PoolWatch.Tests;

/// <summary>
/// An incident's events.csv is appended to, not written again whole at every refresh: each event of the window once, the
/// earliest first, never more than 5,000; and the folder's other files are written only when what they say changed.
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
    public void AWriteAddsAtMostItsRowsAndTheFileStopsAtFiveThousand()
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
        Assert.Equal(1 + IncidentWriter.MaxEventRows + 1, lines.Length); // the header, 5,000 events and the row that says the rest are elsewhere
        // The row says to when the file holds the window's events: the time of the first event not kept (the 5,001st).
        Assert.Contains("events.csv holds the incident window's events to this row's time (an incident keeps at most 5,000)", lines[^1]);
        Assert.StartsWith(PoolWatch.Core.Util.Csv.Line(T0.AddSeconds(IncidentWriter.MaxEventRows)), lines[^1]);
        Assert.Contains(",Note,", lines[^1]);
        Assert.False(writer.EventsPending(inc));
        Assert.Equal(0, writer.AppendEvents(inc, events, maxRows: 10_000)); // full: nothing more
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
