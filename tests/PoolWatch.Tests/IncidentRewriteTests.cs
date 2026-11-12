using PoolWatch.Core;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Detection;
using PoolWatch.Core.History;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;

using static PoolWatch.Tests.Clocks;

namespace PoolWatch.Tests;

/// <summary>
/// What an incident's folder says when it is written again after its close, and of incidents that are not growths: an
/// incident stays in PoolWatch's memory while a trace that may be attached to it records, so the trace's write counts
/// each event by its own time; a drop's incident has the events of its drop; a trace's incident says what started the
/// trace; and a closed incident's report keeps what the history said at its close (the PC's counters, with their time,
/// the tag's allocation rates, its level with the time it is of, and its pool) when it is written again after the
/// history has moved on, and says what its trace's answer and its suspects' lines say with the time they are of
/// ("was still held at", "still running when it closed"). Also here: the window's end measured again after events
/// were counted (the latest part and the counts are taken again from the events PoolWatch holds, and what the texts
/// say where it no longer holds them, an export's among them);
/// a closed incident's writes that must be made (every row its file is to hold) and the rows that waited (written
/// with its report, 100 at a time); a trace's file when the trace has no event; what the span a report counts over
/// is called; and what the log says while another program holds an open incident's events.csv, or the report beside
/// a closed incident's rows. And: what the plain words say a tag did over its incident's span (a drop's say the
/// drop; a closed incident's are in the past, "was flat", "No clear cause was found"; a tag that only fell is said
/// to have fallen; a trace's incident whose trace is not attached yet says that, in its title and its Growth line
/// too, and one whose trace recorded nothing says that), an owner's line by what its handles
/// and its activity did over the window ("rose by", "rose and fell with", not "holds", "rises and falls with"), the
/// headline of an incident for the whole non-paged pool (the limit its alert is about, and "and rising" only while it
/// is open), and a closed incident's folder write that fails: tried again a minute on and at the end of a run, said
/// once in the log, and once when the folder is written again; its report written all the same while another
/// program holds its timeline.csv or its processes.csv (an open incident's too), and the tries that follow made for
/// the chart files alone, writing no report (so they spend none of the write budget, and an open incident beside
/// it is refreshed as without the hold) unless they made a file the report does not show, or more than those files
/// is still to write, or the incident changed since; once the history no longer
/// reaches back to its window's start, each of its chart and its two other CSVs made only by a try that holds at
/// least as many of the window's samples as the file does (a file that stops before the close replaced by one that
/// reaches it, none made from fewer, counted from the window's first sample only as kept for that window), and
/// until then made again by every write of the whole folder (a trace
/// attached after the close has processes.csv follow its ranking); an incident that re-opened meanwhile said
/// written again only once its chart and CSVs are; an incident open at the end of a run tried once; and the
/// draft bug report listing as attached the files the folder holds.
/// </summary>
public sealed class IncidentRewriteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pw-rewrite-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static readonly PoolTag FileTag = PoolTag.Parse("File");

    /// <summary>The rows of an events.csv after its header, each as its cells.</summary>
    private static List<List<string>> Rows(Incident inc) => File.ReadAllLines(Path.Combine(inc.Dir, "events.csv")).Skip(1).Select(PoolWatch.Core.Util.Csv.Split).ToList();

    private static DateTime Time(string s) => DateTime.Parse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();

    /// <summary>The events report.md lists under "Events in the window", each as its PID, in the order listed.</summary>
    private static List<int> ListedPids(Incident inc)
    {
        var md = File.ReadAllLines(Path.Combine(inc.Dir, "report.md"));
        return md.Skip(Array.IndexOf(md, "## Events in the window") + 2).TakeWhile(l => l.StartsWith("- ", StringComparison.Ordinal))
            .Select(l => int.Parse(System.Text.RegularExpressions.Regex.Match(l, @"\(PID ([0-9]+)\)").Groups[1].Value)).ToList();
    }

    [Fact]
    public void NoIncidentLeavesMemoryWhileATraceThatMayBeAttachedToItRecords()
    {
        // "Capture now" makes its incident, closed, and its trace records for a minute. Meanwhile 21 other incidents
        // close: more than the 20 closed incidents PoolWatch holds, so the trace's own, the oldest, would leave memory
        // before its trace is attached, and stop looking at events. Here the shared buffer keeps 5 minutes (a setting; 90
        // by default), and the trace is attached 6 minutes after the incident was made, when the buffer has let go of the
        // events of the trace's minute and of some seconds after its end. While a trace records or is read, no incident
        // leaves memory: the incident has looked at every event before the buffer let it go, and its events.csv holds
        // every event of the trace's own window, each once, with no Gap row. (Before, it had left memory: the trace's
        // write found that the buffer had let events go that it had not looked at, and counted as many as went as left
        // out, the 3 from after its window's end among them, in a Gap row "30 events left out".)
        Directory.CreateDirectory(_dir);
        var w = new World { Interval = TimeSpan.FromSeconds(10) };
        var settings = new Settings { IncidentEventKeepMinutes = 5 };
        var mgr = new IncidentManager(settings, new TagKnowledge(null, null), new CountingHost(_dir), w.Registry);
        var det = new Detector(settings);
        bool capturing = false;
        mgr.CaptureRunning = () => capturing;
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
            mgr.Process(w.History, det, [], At(now));
        }
        for (int i = 0; i < 30; i++) Step();
        var made = w.Now - w.Interval;
        var inc = mgr.CreateCapture(w.History, [FileTag], made);
        capturing = true;
        // The 21 that close while the trace records (made as traces' incidents are: closed).
        for (int i = 0; i < 21; i++) mgr.CreateCapture(w.History, [PoolTag.Parse($"T{i:D3}")], made);
        Assert.Equal(22, mgr.Closed.Count);
        for (int i = 0; i < 37; i++) Step();
        Assert.Contains(inc, mgr.Closed); // held, past the 20: its trace has not been attached yet
        Assert.Equal(22, mgr.Closed.Count);
        Assert.True(mgr.EventsBetween(At(made).Ms, At(made.AddSeconds(66)).Ms).Count == 0, "the buffer has let go of the trace's minute");

        var (start, end) = (made.AddSeconds(3.7), made.AddSeconds(63.7));
        mgr.ApplyCapture(w.History, inc, new CaptureResult { PoolEventsParsed = true, Tags = [FileTag] }.Recorded(At(start), At(end)));
        capturing = false;
        var rows = Rows(inc);
        Assert.DoesNotContain(rows, r => r[1] is "Gap" or "Note");
        var expected = times.Where(kv => kv.Value >= start && kv.Value <= end).Select(kv => kv.Key).Order().ToList();
        Assert.Equal(30, expected.Count);
        Assert.Equal(expected, rows.Where(r => r[1] == "Start").Select(r => int.Parse(r[2])));
        Assert.Contains($"({expected.Count} other events in events.csv)", File.ReadAllLines(Path.Combine(inc.Dir, "report.md")));

        // Once no trace runs, those past the 20 leave, the oldest first.
        Step();
        Assert.Equal(IncidentManager.MaxClosed, mgr.Closed.Count);
        Assert.DoesNotContain(inc, mgr.Closed);
    }

    [Fact]
    public void ADropsIncidentHasTheEventsOfItsDropInItsReportAndItsFile()
    {
        // A tag drops sharply with no growth incident of its own to attach to: an incident of kind Release, made closed.
        // Its window runs from 2 minutes before the drop began to a minute after the alert that saw it: its report lists
        // the window's exits and its events.csv holds the window's events, those of the minute after the alert as they
        // come, and none after. (Before, its window ended a minute after Windows started, as the moment a drop's window
        // ends from was never set: its report listed no event, and its events.csv held the alert's row alone.)
        Directory.CreateDirectory(_dir);
        var w = new World { Interval = TimeSpan.FromSeconds(10) };
        var mgr = new IncidentManager(new Settings(), new TagKnowledge(null, null), new CountingHost(_dir), w.Registry) { Budget = new WriteBudget(1L << 30, 1L << 30) };
        var det = new Detector(new Settings());
        w.AddProcess(4, 0, "System");
        int pid = 9000;
        var times = new Dictionary<int, DateTime>();
        long level = 900 * Settings.MB;
        DateTime seen = default, onset = default;
        void Step(bool dropSeen = false)
        {
            w.Sample(("File", level));
            var now = w.Now - w.Interval;
            // An exit every 2 s, the last 2 s before the sample: report.md lists a window's exits.
            var events = Enumerable.Range(0, 5).Select(k => new LifecycleEvent { TimeUtc = now.AddSeconds(-10 + 2 * k), Kind = LifecycleKind.Exit, Pid = pid++, CreateTime = 1 + pid, Image = "x.exe" }).ToList();
            foreach (var e in events) times[e.Pid] = e.TimeUtc;
            mgr.AddEvents(events, At(now));
            Alert[] alerts = [];
            if (dropSeen)
            {
                // The release rule's alert, as the detector would raise it at this sample: the drop began 20 s before.
                (seen, onset) = (now, now.AddSeconds(-20));
                alerts = [new Alert(AlertKind.Release, FileTag, seen, "dropped > 500 MB in 1 min", 600 * Settings.MB, TimeSpan.FromMinutes(1), onset) { Ms = At(seen).Ms, OnsetMs = At(onset).Ms }];
            }
            mgr.Process(w.History, det, alerts, At(now));
        }
        for (int i = 0; i < 40; i++) Step();
        level = 300 * Settings.MB;
        Step();
        Step(dropSeen: true);
        var inc = Assert.Single(mgr.Closed);
        Assert.Equal(IncidentKind.Release, inc.Kind);
        Assert.Equal((seen, (long?)At(seen).Ms), (inc.ClosedUtc, inc.ClosedMs));
        Assert.Equal(At(onset).Ms - 120_000, IncidentManager.EventWindowStartMs(inc));
        Assert.Equal(At(seen).Ms + 60_000, IncidentManager.EventWindowEndMs(inc, At(seen).Ms + 3_600_000));
        List<int> Window(DateTime to) => times.Where(kv => kv.Value >= onset.AddMinutes(-2) && kv.Value <= to).Select(kv => kv.Key).Order().ToList();
        List<int> InFile() => Rows(inc).Where(r => r[1] == "Exit").Select(r => int.Parse(r[2])).ToList();
        // As it is made: the window's events so far, in its file and (the first 50 of them) in its report.
        var soFar = Window(seen);
        Assert.Equal(70, soFar.Count); // the 2 minutes and 20 seconds before the alert, an exit every 2 s, the last 2 s before it
        Assert.Equal(soFar, InFile());
        Assert.Equal(soFar.Take(50), ListedPids(inc));
        Assert.Single(Rows(inc), r => r[1] == "Release");

        // The minute after the alert: its events are the window's, taken as they come; those after it are not.
        for (int i = 0; i < 12; i++) Step();
        var all = Window(seen.AddMinutes(1));
        Assert.Equal(soFar.Count + 31, all.Count); // from the alert's moment to a minute after it
        Assert.Equal(seen.AddMinutes(1), times[all[^1]]);
        Assert.Equal(all, InFile());
        Assert.Contains($"({all.Count - 50} other events in events.csv)", File.ReadAllLines(Path.Combine(inc.Dir, "report.md")));
        mgr.Finish(w.History, w.Now);
        Assert.Equal(all, InFile());
    }

    [Fact]
    public void ATracesIncidentSaysWhatStartedItsTrace()
    {
        // An incident made for a trace of its own is made for the person's Capture now, or for a lag trip (its CPU
        // trace): report.json says which (traceFor), and Pool causes labels it by that. (Before, both were "Manual
        // capture".) A growth incident has none.
        Directory.CreateDirectory(_dir);
        var w = new World { Interval = TimeSpan.FromSeconds(10) };
        var mgr = new IncidentManager(new Settings(), new TagKnowledge(null, null), new CountingHost(_dir), w.Registry);
        w.AddProcess(4, 0, "System");
        for (int i = 0; i < 6; i++) w.Sample(("File", 100 * Settings.MB));
        static string? TraceFor(Incident inc)
        {
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(inc.Dir, "report.json")));
            var said = json.RootElement.GetProperty("traceFor");
            return said.ValueKind == System.Text.Json.JsonValueKind.Null ? null : said.GetString();
        }
        var manual = mgr.CreateCapture(w.History, [FileTag], w.Now - w.Interval);
        Assert.Equal((CaptureSource?)CaptureSource.Manual, manual.TraceFor);
        Assert.Equal("Manual", TraceFor(manual));
        var lag = mgr.CreateCapture(w.History, [], w.Now - w.Interval, CaptureSource.LagTrip);
        Assert.Equal((CaptureSource?)CaptureSource.LagTrip, lag.TraceFor);
        Assert.Equal("LagTrip", TraceFor(lag));
        var growth = new Incident { Id = "g", Dir = Path.Combine(_dir, "g"), Tag = FileTag, Kind = IncidentKind.Growth, OnsetUtc = World.T0, OnsetMs = At(World.T0).Ms };
        new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry()).Write(new History(), growth, []);
        Assert.Null(growth.TraceFor);
        Assert.Null(TraceFor(growth));
    }

    /// <summary>
    /// A history of samples a minute apart from 22:00, kept 70 minutes: File's counters 3,000 allocations and 300 frees a
    /// minute, its level a megabyte more each minute, and the PC's commit a gigabyte more each sample.
    /// </summary>
    private static (History H, Action<int> Add, DateTime T0) Minutes()
    {
        var h = History.ForInterval(60);
        var t0 = new DateTime(2026, 11, 9, 22, 0, 0, DateTimeKind.Utc);
        void Add(int i) => h.Add(new Sample(t0.AddMinutes(i), default, [new PoolTagEntry(FileTag, 0, 0, 0, (uint)(3000 * i), (uint)(300 * i), (ulong)(64 * Settings.MB + i * Settings.MB))], [])
        {
            TickMs = At(t0.AddMinutes(i)).Ms, System = new SystemCounters((ulong)i << 30, 64UL << 30, 8UL << 30, 1UL << 30, (uint)(100_000 + i), 300),
        });
        return (h, Add, t0);
    }

    [Fact]
    public void AClosedIncidentsReportWrittenAgainLaterKeepsWhatTheHistorySaidAtItsClose()
    {
        // A trace's incident, closed with its trace (5 minutes from 22:20:20). Its report gives the PC's counters with
        // the time they are of, and the tag's allocation rates over its growth. Three hours on, when the history (70
        // minutes) holds none of its samples, a note is added to its timeline and the report is written again: the two
        // lines are as they were, of the incident's own time. (Before, the report written again said "System at the last
        // sample" with the counters of three hours later, and had no allocation rates, as the history no longer held
        // the growth.) While an incident is open its report's counters are those of the last sample, with its time.
        Directory.CreateDirectory(_dir);
        var (h, add, t0) = Minutes();
        var mgr = new IncidentManager(new Settings(), new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        for (int i = 1; i <= 20; i++) add(i);
        var made = h.Times[^1];
        var inc = mgr.CreateCapture(h, [FileTag], made);
        mgr.Note(inc, made, "capture started: pool events"); // as the pipeline notes it (the report shows a timeline of two entries or more)
        string SystemLine(Incident of) => Assert.Single(File.ReadAllLines(Path.Combine(of.Dir, "report.md")), l => l.StartsWith("- System at ", StringComparison.Ordinal));
        string? ShapeLine(Incident of) => File.ReadAllLines(Path.Combine(of.Dir, "report.md")).SingleOrDefault(l => l.StartsWith("- Allocation shape", StringComparison.Ordinal));
        Assert.StartsWith($"- System at {Fmt.Clock(made)}: commit {Fmt.Bytes(20L << 30)} of {Fmt.Bytes(64L << 30)}, ", SystemLine(inc));
        for (int i = 21; i <= 27; i++) add(i);
        var start = made.AddSeconds(20);
        mgr.ApplyCapture(h, inc, new CaptureResult { PoolEventsParsed = true, Tags = [FileTag] }.Recorded(At(start), At(start.AddMinutes(5))));
        // Its close moved to the trace's end: read again, at the write that attached the trace (22:27's sample).
        string system = SystemLine(inc), shape = ShapeLine(inc)!;
        string timeline = File.ReadAllText(Path.Combine(inc.Dir, "timeline.csv")), processes = File.ReadAllText(Path.Combine(inc.Dir, "processes.csv"));
        Assert.True(timeline.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length > 5, timeline);
        bool chart = File.ReadAllText(Path.Combine(inc.Dir, "report.md")).Contains("![timeline](timeline.png)");
        Assert.True(chart, "the report shows its chart");
        Assert.StartsWith($"- System at {Fmt.Clock(t0.AddMinutes(27))}: commit {Fmt.Bytes(27L << 30)} of {Fmt.Bytes(64L << 30)}, ", system);
        Assert.StartsWith("- Allocation shape during the growth: 3,000 allocations/min, 300 frees/min", shape);
        using (var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(inc.Dir, "report.json"))))
        {
            var at = json.RootElement.GetProperty("systemAtEnd");
            Assert.Equal((t0.AddMinutes(27), 27L << 30), (at.GetProperty("timeUtc").GetDateTime().ToUniversalTime(), at.GetProperty("commitBytes").GetInt64()));
            Assert.Equal(3000, json.RootElement.GetProperty("allocationShape").GetProperty("allocsPerMin").GetDouble());
        }

        // Three hours on: the history holds none of the incident's samples. A note on its timeline has its folder written.
        for (int i = 28; i <= 207; i++) add(i);
        Assert.True(h.Times[0] > inc.ClosedUtc!.Value.AddHours(1), $"the history starts at {h.Times[0]:T}");
        var det = new Detector(new Settings());
        mgr.Note(inc, h.Times[^1], "a note added three hours on");
        mgr.Process(h, det, [], At(h.Times[^1]));
        var md = File.ReadAllText(Path.Combine(inc.Dir, "report.md"));
        Assert.Contains("a note added three hours on", md); // written again
        Assert.Equal(system, SystemLine(inc));
        Assert.Equal(shape, ShapeLine(inc));
        // Its chart and the two CSVs made from the history's samples stay as its close left them (written again, they
        // would be of samples from three hours after the incident: timeline.csv came out as a single row of them, and
        // the report without its chart).
        Assert.Equal(timeline, File.ReadAllText(Path.Combine(inc.Dir, "timeline.csv")));
        Assert.Equal(processes, File.ReadAllText(Path.Combine(inc.Dir, "processes.csv")));
        Assert.Contains("![timeline](timeline.png)", md);
        using (var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(inc.Dir, "report.json"))))
        {
            Assert.Equal(t0.AddMinutes(27), json.RootElement.GetProperty("systemAtEnd").GetProperty("timeUtc").GetDateTime().ToUniversalTime());
            Assert.Equal(3000, json.RootElement.GetProperty("allocationShape").GetProperty("allocsPerMin").GetDouble());
        }

        // An open incident's are read at every write: the last sample's, with its time.
        var open = new Incident
        {
            Id = "open", Dir = Path.Combine(_dir, "open"), Tag = FileTag, Kind = IncidentKind.Growth,
            OnsetUtc = t0.AddMinutes(200), OnsetMs = At(t0.AddMinutes(200)).Ms, LastRiseUtc = h.Times[^1], LastRiseMs = h.Ms[^1],
        };
        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        writer.Write(h, open, []);
        Assert.StartsWith($"- System at {Fmt.Clock(t0.AddMinutes(207))}: commit {Fmt.Bytes(207L << 30)} of ", SystemLine(open));
        add(208);
        open.Timeline.Add((h.Times[^1], "another sample"));
        writer.Write(h, open, []);
        Assert.StartsWith($"- System at {Fmt.Clock(t0.AddMinutes(208))}: commit {Fmt.Bytes(208L << 30)} of ", SystemLine(open));
    }

    /// <summary>report.md's line under the window's events ("(N other events ..."), or "" when it has none.</summary>
    private static string EventsLine(Incident inc) =>
        File.ReadAllLines(Path.Combine(inc.Dir, "report.md")).FirstOrDefault(l => l.StartsWith('(') && l.Contains(" other events in ")) ?? "";

    /// <summary>A line of report.md by how it starts.</summary>
    private static string Line(Incident inc, string starts) =>
        Assert.Single(File.ReadAllLines(Path.Combine(inc.Dir, "report.md")), l => l.StartsWith(starts, StringComparison.Ordinal));

    private static string Iso(DateTime t) => PoolWatch.Core.Util.Csv.Escape(t);

    /// <summary>
    /// A growth on a PC that records <paramref name="perSample"/> events every 10 s sample (PIDs from 500,000, in time
    /// order), File's level at each sample given by <paramref name="level"/>, the 1-minute rule's alert at every sample
    /// from <paramref name="alertFrom"/> on with its onset at sample <paramref name="onsetAt"/>. The incident stays open
    /// (its quiet and maximum minutes are 10 hours) until <c>Finish</c>, the end of the run, closes it.
    /// <paramref name="each"/> is called after each sample with its number and the incident (null before it opens).
    /// </summary>
    private (IncidentManager Mgr, Incident Inc, List<DateTime> Times, World W) LongGrowth(int samples, int perSample, Func<int, long> level, int alertFrom, int onsetAt,
        Action<int, Incident?>? each = null, Settings? settings = null)
    {
        Directory.CreateDirectory(_dir);
        settings ??= new Settings();
        (settings.IncidentQuietMinutes, settings.IncidentMaxMinutes) = (600, 600);
        var w = new World { Interval = TimeSpan.FromSeconds(10) };
        var mgr = new IncidentManager(settings, new TagKnowledge(null, null), new CountingHost(_dir), w.Registry) { Budget = new WriteBudget(1L << 30, 1L << 30) };
        var det = new Detector(settings);
        w.AddProcess(4, 0, "System");
        int pid = 500_000;
        var times = new List<DateTime>();
        var onset = World.T0.AddSeconds(onsetAt * 10);
        Incident? inc = null;
        for (int i = 0; i < samples; i++)
        {
            w.Sample(("File", level(i)));
            var now = w.Now - w.Interval;
            // Timed through the 10 s before the sample, the last a little before it.
            var events = Enumerable.Range(0, perSample).Select(k => new LifecycleEvent { TimeUtc = now.AddSeconds(-10).AddMilliseconds((k + 1) * (9_900 / perSample)), Kind = LifecycleKind.Start, Pid = pid++, CreateTime = 1 + pid, Image = "x.exe" }).ToList();
            times.AddRange(events.Select(e => e.TimeUtc));
            mgr.AddEvents(events, At(now));
            Alert[] alerts = i >= alertFrom ? [new Alert(AlertKind.Growth, FileTag, now, "grew > 20 MB in 1 min", 24 * Settings.MB, TimeSpan.FromMinutes(1), onset) { Ms = At(now).Ms, OnsetMs = At(onset).Ms }] : [];
            mgr.Process(w.History, det, alerts, At(now));
            inc ??= mgr.Open.SingleOrDefault();
            each?.Invoke(i, inc);
        }
        Assert.NotNull(inc);
        Assert.True(inc!.IsOpen);
        return (mgr, inc, times, w);
    }

    /// <summary>
    /// Asserts that a closed incident's events.csv holds both ends of its window as its report gives it, exactly: the
    /// first 500 events from the window's start, a Gap row that counts every event between the two parts and no other,
    /// and the latest 500 up to the window's end; and that report.md says the same.
    /// </summary>
    private static void AssertBothEndsOfTheWindow(Incident inc, List<DateTime> times)
    {
        long from = IncidentManager.EventWindowStartMs(inc), to = IncidentManager.EventWindowEndMs(inc, 0);
        int first = times.FindIndex(t => At(t).Ms >= from), last = times.FindLastIndex(t => At(t).Ms <= to);
        int left = last - first + 1 - 2 * IncidentWriter.MaxEventRows;
        Assert.True(left > 0, $"the window holds {last - first + 1} events");
        var rows = Rows(inc);
        int gapAt = rows.FindIndex(r => r[1] == "Gap");
        Assert.True(gapAt > 0, "a Gap row between the two parts");
        List<int> Pids(IEnumerable<List<string>> part) => part.Where(r => r[1] == "Start").Select(r => int.Parse(r[2])).ToList();
        Assert.Equal(Enumerable.Range(500_000 + first, IncidentWriter.MaxEventRows), Pids(rows.Take(gapAt)));
        Assert.Equal(Enumerable.Range(500_000 + last - IncidentWriter.MaxEventRows + 1, IncidentWriter.MaxEventRows), Pids(rows.Skip(gapAt + 1)));
        var c = System.Globalization.CultureInfo.InvariantCulture;
        DateTime firstLeft = times[first + 500], lastLeft = times[last - 500];
        Assert.True(At(lastLeft).Ms <= to, "no event from after the window's end is counted as left out");
        Assert.Equal((Iso(firstLeft), string.Create(c, $"{left:N0} events left out, between {Iso(firstLeft)} and {Iso(lastLeft)} ") +
                      $"(events.csv keeps the first 500 events from {Iso(IncidentManager.EventWindowStartUtc(inc))}, where the incident's window started when this file was written, " +
                      "and the latest 500 up to the window's end)"), (rows[gapAt][0], rows[gapAt][9]));
        Assert.Equal($"(1000 other events in events.csv; it leaves out {left:N0} events, between {Fmt.Clock(firstLeft)} and {Fmt.Clock(lastLeft)}, as its Gap row says)", EventsLine(inc));
    }

    /// <summary>
    /// File's level for a growth whose end is measured at the top of its second step, and then, once the ranking's
    /// window reaches back to a slightly lower level before the first, at the top of its first: 98 MB, then 100 MB for
    /// 20 minutes, up to 195 MB in 2 minutes (sample 137), level for 20 minutes, then 200.1 MB (from sample 258).
    /// </summary>
    private static long Staircase(int i) => i <= 5 ? 98 * Settings.MB
        : i <= 125 ? 100 * Settings.MB
        : i <= 137 ? 100 * Settings.MB + 95 * Settings.MB * (i - 125) / 12
        : i <= 257 ? 195 * Settings.MB
        : 200 * Settings.MB + Settings.MB / 10;

    [Fact]
    public void WhenTheWindowsEndIsMeasuredEarlierItsLatestPartAndItsCountsAreTakenAgain()
    {
        // 60 events a sample. The growth's end is first measured at the top of its second step (sample 258), and the
        // incident lets go of the events before its latest 1,000 up to there, counting them as left out. Minutes later
        // the ranking's window reaches back to the 98 MB the tag held before, and the end is measured at the top of
        // its first step (sample 137), 20 minutes earlier: 7,000 events it had counted as left out are from after the
        // window's end now, and it holds none of the 500 before that end. At its close the incident takes its latest
        // part and its counts again from the events PoolWatch holds (IncidentManager.Refit), as its first part follows
        // the start: its events.csv holds the first 500 events of the window, a Gap row for exactly those between, and
        // the latest 500 up to the window's end. (Before, the file had its first 500 rows and no latest part, and its
        // Gap row said "7,320 events left out", up to 17 minutes after the window's end, of a window of 1,801 events.)
        var ends = new List<DateTime?>();
        var (mgr, inc, times, w) = LongGrowth(376, 60, Staircase, alertFrom: 129, onsetAt: 126, (_, i) => { if (i is not null && (ends.Count == 0 || ends[^1] != i.EndUtc)) ends.Add(i.EndUtc); });
        var top1 = World.T0.AddSeconds(137 * 10);
        Assert.Contains(ends, e => e > top1.AddMinutes(15)); // measured at the second step first
        Assert.Equal((DateTime?)top1, ends[^1]);              // and then at the first
        mgr.Finish(w.History, w.Now);
        Assert.False(inc.IsOpen);
        Assert.Equal((DateTime?)top1, inc.EndUtc);
        AssertBothEndsOfTheWindow(inc, times);
    }

    [Theory]
    [InlineData(90, 470)]
    [InlineData(70, 462)]
    public void WhenTheWindowsEndIsMeasuredLaterToAmongEventsSetAsideItsLatestPartAndItsCountsAreTakenAgain(int keepMinutes, int samples)
    {
        // 30 events a sample. File rises to 150 MB in 2 minutes (to sample 41), holds, and steps to 152 MB at sample 60:
        // the growth's end is measured at sample 41, and stays there for over an hour while the incident is open. Of
        // the events after its window's end the incident holds the latest 4,000 (in case the end moves) and sets the
        // earlier ones aside, counted. When the raw history (70 minutes) no longer holds the rise, the end is measured
        // on what it holds: at the step, sample 60, 3 minutes later, among the events set aside. Those cannot be told
        // apart by time any more (they were counted, not kept): counted as they were, all 8,000 of them, up to an hour
        // after the window's end, would be "left out", and the file would have no latest part. At its close the incident
        // takes its latest part and its counts again from the events PoolWatch holds: both ends of the window, exactly.
        // With the buffer kept 90 minutes (the default) PoolWatch holds every event. Kept 70 minutes, it has let go of
        // the earliest events the incident counted as left out, those of the first 7 minutes after its first part: they
        // stay counted as they are (they are before the window's end), and the rest is taken again, to the same file.
        static long Level(int i) => i <= 29 ? 100 * Settings.MB : i <= 41 ? 100 * Settings.MB + 50 * Settings.MB * (i - 29) / 12 : i < 60 ? 150 * Settings.MB : 152 * Settings.MB;
        var ends = new List<DateTime?>();
        var (mgr, inc, times, w) = LongGrowth(samples, 30, Level, alertFrom: 36, onsetAt: 30, (_, i) => { if (i is not null && (ends.Count == 0 || ends[^1] != i.EndUtc)) ends.Add(i.EndUtc); },
            new Settings { IncidentEventKeepMinutes = keepMinutes });
        DateTime rise = World.T0.AddSeconds(41 * 10), step = World.T0.AddSeconds(60 * 10);
        Assert.Contains((DateTime?)rise, ends);  // measured at the end of the rise first
        Assert.Equal((DateTime?)step, ends[^1]); // and then, later, at the step
        Assert.True(times.Count(t => t > rise.AddMinutes(1)) > IncidentManager.AfterEndMax + 30 * 19, "more events came after the first end than the incident holds for a later one");
        int first = times.FindIndex(t => At(t).Ms >= IncidentManager.EventWindowStartMs(inc));
        if (keepMinutes == 90) Assert.Equal(times.Count, mgr.EventCount); // PoolWatch still holds every event
        else
        {
            // It has let go of the first events the incident left out, and still holds the latest 500 up to the window's end.
            long held = At(times[times.Count - mgr.EventCount]).Ms;
            Assert.True(held > At(times[first + 500 + 100]).Ms && held < At(step.AddMinutes(1)).Ms - 3 * 60_000, $"it holds events from {times[times.Count - mgr.EventCount]:T}");
        }
        mgr.Finish(w.History, w.Now);
        Assert.Equal((DateTime?)step, inc.EndUtc);
        AssertBothEndsOfTheWindow(inc, times);
    }

    [Fact]
    public void WhenPoolWatchNoLongerHoldsTheEventsToCountAgainTheGapRowSaysItsCountRunsPastTheWindowsEnd()
    {
        // The staircase again, with the shared buffer kept 10 minutes (a setting; 90 by default, more than the 70 of the
        // raw history, so that it holds the events from after any end that is measured: with the defaults this happens
        // only past 20,000 events since that end). The end is measured 20 minutes earlier at the close, and PoolWatch
        // no longer holds the events between: the incident cannot take its latest part and its count again. The count
        // stays as it was made, and the Gap row and report.md say so, with where the window ends: no text gives the
        // count as the window's alone. Every event from the row's first time to its last is counted, each once.
        var (mgr, inc, times, w) = LongGrowth(376, 60, Staircase, alertFrom: 129, onsetAt: 126, settings: new Settings { IncidentEventKeepMinutes = 10 });
        mgr.Finish(w.History, w.Now);
        var top1 = World.T0.AddSeconds(137 * 10);
        Assert.Equal((DateTime?)top1, inc.EndUtc);
        long from = IncidentManager.EventWindowStartMs(inc), to = IncidentManager.EventWindowEndMs(inc, 0);
        var end = top1.AddMinutes(1); // the window's end
        Assert.Equal(At(end).Ms, to);
        var rows = Rows(inc);
        int gapAt = rows.FindIndex(r => r[1] == "Gap");
        int first = times.FindIndex(t => At(t).Ms >= from);
        Assert.Equal(Enumerable.Range(500_000 + first, IncidentWriter.MaxEventRows), rows.Take(gapAt).Where(r => r[1] == "Start").Select(r => int.Parse(r[2])));
        Assert.DoesNotContain(rows.Skip(gapAt + 1), r => r[1] == "Start"); // it holds no event up to the window's end after its first part
        var m = System.Text.RegularExpressions.Regex.Match(rows[gapAt][9], @"^([0-9,]+) events left out, between (\S+) and (\S+) \((.*)\)$");
        Assert.True(m.Success, rows[gapAt][9]);
        int left = int.Parse(m.Groups[1].Value, System.Globalization.NumberStyles.AllowThousands, System.Globalization.CultureInfo.InvariantCulture);
        DateTime firstLeft = Time(m.Groups[2].Value), lastLeft = Time(m.Groups[3].Value);
        Assert.Equal(times[first + 500], firstLeft);
        Assert.True(lastLeft > end.AddMinutes(10), $"the count runs to {lastLeft:T}, past the window's end {end:T}");
        Assert.Equal(times.Count(t => t >= firstLeft && t <= lastLeft), left); // every event between its two times, each once
        Assert.Equal($"events.csv keeps the first 500 events from {Iso(IncidentManager.EventWindowStartUtc(inc))}, where the incident's window started when this file was written; " +
                     $"the count runs past the window's end, {Iso(end)} when this file was written: PoolWatch no longer held those events, to count each by its own time against that end",
            m.Groups[4].Value);
        Assert.Equal($"(500 other events in events.csv; it leaves out {left:N0} events, between {Fmt.Clock(firstLeft)} and {Fmt.Clock(lastLeft)}, as its Gap row says; " +
                     $"the count runs past the window's end, {Fmt.Clock(end)} when the file was written: PoolWatch no longer held those events, to count each by its own time against that end)",
            EventsLine(inc));
    }

    [Fact]
    public void AnOpenIncidentWhoseEventsCsvAnotherProgramHoldsSaysSoOnceAndOnceWhenItsRowsAreWritten()
    {
        // An open incident on a PC with 30 events a sample: its first part (500 events) reaches its file 100 rows at a
        // time. Another program then holds events.csv open, sharing nothing, for an hour: no row can be appended, while
        // the incident's report, which is replaced whole, is still written at every refresh. The log says once that the
        // rows cannot be written, and nothing more until they are: then, once, that they are written again. (Before,
        // every report refresh that added no row counted as the rows being written: the log said "events.csv is written
        // again" 17 times in the hour, each untrue, and the failure 18 times.)
        Directory.CreateDirectory(_dir);
        var host = new CountingHost(_dir);
        var settings = new Settings { IncidentQuietMinutes = 600, IncidentMaxMinutes = 600 };
        var w = new World { Interval = TimeSpan.FromSeconds(10) };
        var mgr = new IncidentManager(settings, new TagKnowledge(null, null), host, w.Registry) { Budget = new WriteBudget(1L << 30, 1L << 30) };
        var det = new Detector(settings);
        w.AddProcess(4, 0, "System");
        long tag = 100 * Settings.MB;
        int pid = 9000;
        DateTime? onset = null;
        void Step(bool alert, bool rises = false)
        {
            if (rises) tag += 4 * Settings.MB;
            w.Sample(("File", tag));
            var now = w.Now - w.Interval;
            mgr.AddEvents(Enumerable.Range(0, 30).Select(k => new LifecycleEvent { TimeUtc = now.AddSeconds(-9).AddMilliseconds(k * 300), Kind = LifecycleKind.Start, Pid = pid++, CreateTime = 1 + pid, Image = "x.exe" }).ToList(), At(now));
            onset ??= alert ? now.AddMinutes(-1) : null;
            Alert[] alerts = alert ? [new Alert(AlertKind.Growth, FileTag, now, "grew > 20 MB in 1 min", 24 * Settings.MB, TimeSpan.FromMinutes(1), onset!.Value) { Ms = At(now).Ms, OnsetMs = At(onset.Value).Ms }] : [];
            mgr.Process(w.History, det, alerts, At(now));
        }
        // File holds, rises 32 MB in 80 s and holds again (the rule goes on firing): the growth's start is measured once and
        // stays, so the file is only ever added to (a file that is to start again from a new start is replaced whole).
        for (int i = 0; i < 24; i++) Step(alert: false);
        for (int i = 0; i < 6; i++) Step(alert: false, rises: true);
        Step(alert: true, rises: true);
        var inc = mgr.Open.Single();
        Step(alert: true, rises: true); // the sample after its opening: the first 100 rows
        long start = IncidentManager.EventWindowStartMs(inc);
        int before = Rows(inc).Count(r => r[1] == "Start");
        Assert.InRange(before, 1, IncidentWriter.MaxEventRows - 1);
        bool Failed(string log) => log.StartsWith($"incident {inc.Id}: events.csv write failed:", StringComparison.Ordinal);
        string again = $"incident {inc.Id}: events.csv is written again";
        Assert.DoesNotContain(host.Logs, l => Failed(l) || l == again);

        string path = Path.Combine(inc.Dir, "events.csv");
        string md = File.ReadAllText(Path.Combine(inc.Dir, "report.md"));
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            for (int i = 0; i < 360; i++) Step(alert: true); // an hour
            Assert.EndsWith(" (its rows are tried again at each sample; this is said once until they are written)", Assert.Single(host.Logs, Failed));
            Assert.DoesNotContain(again, host.Logs);
            Assert.NotEqual(md, File.ReadAllText(Path.Combine(inc.Dir, "report.md"))); // its report was written meanwhile
            Assert.Equal(start, IncidentManager.EventWindowStartMs(inc)); // and its window starts where it did
        }
        Assert.Equal(before, Rows(inc).Count(r => r[1] == "Start"));

        // The file is let go: its rows come, 100 at a write, and the log says so once, when none waits any more: not
        // after the first of those writes, which leaves rows waiting still.
        Step(alert: true);
        int afterOne = Rows(inc).Count(r => r[1] == "Start");
        Assert.InRange(afterOne, before + 1, IncidentWriter.MaxEventRows - 1);
        Assert.DoesNotContain(again, host.Logs);
        for (int i = 0; i < 11; i++) Step(alert: true);
        Assert.Equal(IncidentWriter.MaxEventRows, Rows(inc).Count(r => r[1] == "Start"));
        Assert.Single(host.Logs, Failed);
        Assert.Single(host.Logs, l => l == again);
    }

    [Fact]
    public void AClosedIncidentsReportWrittenAgainLaterSaysItsLevelAndItsPoolAsTheyWereAtItsClose()
    {
        // A trace's incident on a rising File (non-paged), node.exe busy beside it. As its trace is attached, its report
        // gives the growth with the level it reached and the time of the sample that is of, says it was still climbing
        // when it closed, and names the tag's pool in its plain words. Three hours on File has fallen to a tenth and
        // then left the tag list; a note on the incident's timeline has its report written again. The Growth line and
        // the plain words are as they were: of the incident's own time, which the line gives. (Before, the line ended
        // "(still climbing at the last sample); now 244 MB" three hours after that sample, of a tag that was gone, and
        // the plain words named the pool the tag was in at the newest sample: "(File, paged)" for a tag that had left
        // the list.)
        Directory.CreateDirectory(_dir);
        var host = new CountingHost(_dir);
        var w = new World { Interval = TimeSpan.FromSeconds(10) };
        var mgr = new IncidentManager(new Settings(), new TagKnowledge(null, null), host, w.Registry) { Budget = new WriteBudget(1L << 30, 1L << 30) };
        var det = new Detector(new Settings());
        w.AddProcess(4, 0, "System");
        w.AddProcess(100, 4, "node.exe");
        bool gone = false;
        long level = 100 * Settings.MB;
        void Step(bool rises)
        {
            w.Io(100, other: 300);
            if (rises) level += 4 * Settings.MB;
            if (gone) w.Sample(("Proc", 5 * Settings.MB)); else w.Sample(("File", level));
            mgr.Process(w.History, det, [], At(w.Now - w.Interval));
        }
        string Plain(Incident of)
        {
            var md = File.ReadAllLines(Path.Combine(of.Dir, "report.md"));
            return md[Array.IndexOf(md, "## In plain words") + 2];
        }
        (long Bytes, DateTime At, string Plain) Json(Incident of)
        {
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(of.Dir, "report.json")));
            return (json.RootElement.GetProperty("currentBytes").GetInt64(), json.RootElement.GetProperty("currentAtUtc").GetDateTime().ToUniversalTime(),
                json.RootElement.GetProperty("plainAnswer").GetString()!);
        }
        for (int i = 0; i < 30; i++) Step(rises: true);
        var made = w.Now - w.Interval;
        var inc = mgr.CreateCapture(w.History, [FileTag], made);
        // Made closed: its level is said with its sample's time from the start, never as "now"; and with no growth
        // over no time ("220 MB -> 220 MB (+0 B) from ..."), which its trace has yet to give it.
        Assert.Equal($"- Growth: its trace had not finished by {Fmt.Clock(made)}; the tag held {Fmt.Bytes(level)} at {Fmt.Clock(made)}", Line(inc, "- Growth: "));
        mgr.Note(inc, made, "capture started: pool events"); // as the pipeline notes it (the report shows a timeline of two entries or more)
        for (int i = 0; i < 9; i++) Step(rises: true);
        var start = made.AddSeconds(3.7);
        mgr.ApplyCapture(w.History, inc, new CaptureResult { PoolEventsParsed = true, Tags = [FileTag] }.Recorded(At(start), At(start.AddMinutes(1))));
        // The trace ran from 3.7 s to 63.7 s after the incident was made: the levels are of the samples 10 s and 60 s after it.
        long from = level - 8 * 4 * Settings.MB, to = level - 3 * 4 * Settings.MB;
        var lastOfTrace = made.AddSeconds(60);
        string growth = $"- Growth: {Fmt.Bytes(from)} -> {Fmt.Bytes(to)} (+{Fmt.Bytes(to - from)}) from {Fmt.Clock(start)} (still climbing when it closed); {Fmt.Bytes(to)} at {Fmt.Clock(lastOfTrace)}";
        Assert.Equal(growth, Line(inc, "- Growth: "));
        string plain = Plain(inc);
        Assert.Contains("(File, non-paged)", plain);
        var said = Json(inc);
        Assert.Equal((to, lastOfTrace), (said.Bytes, said.At));
        Assert.Contains("(File, non-paged)", said.Plain);

        // Three hours on: File fell to a tenth an hour ago, and has now left the tag list.
        for (int i = 0; i < 700; i++) Step(rises: false);
        level = 10 * Settings.MB;
        for (int i = 0; i < 300; i++) Step(rises: false);
        gone = true;
        for (int i = 0; i < 80; i++) Step(rises: false);
        mgr.Note(inc, w.Now - w.Interval, "a note added three hours on");
        Step(rises: false);
        Assert.DoesNotContain(host.Logs, l => l.Contains("failed", StringComparison.Ordinal));
        Assert.Contains("a note added three hours on", File.ReadAllText(Path.Combine(inc.Dir, "report.md"))); // written again
        Assert.Equal(growth, Line(inc, "- Growth: "));
        Assert.Equal(plain, Plain(inc));
        Assert.Equal(said, Json(inc)); // report.json: the level, the sample it is of and the plain answer, as they were

        // An open incident's line says "now" and "at the last sample": it is written from the newest sample.
        var open = new Incident
        {
            Id = "open", Dir = Path.Combine(_dir, "open"), Tag = FileTag, Kind = IncidentKind.Growth, OnsetUtc = made, OnsetMs = At(made).Ms,
            StartBytes = 100 * Settings.MB, PeakBytes = 140 * Settings.MB, CurrentBytes = 140 * Settings.MB, CurrentAtUtc = made.AddMinutes(1),
        };
        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        writer.Write(w.History, open, []);
        Assert.Equal($"- Growth: 100 MB -> 140 MB (+40 MB) from {Fmt.Clock(made)} (still climbing at the last sample); now 140 MB", Line(open, "- Growth: "));
        // A closed incident whose level was never read from a sample says no level.
        var unread = new Incident
        {
            Id = "unread", Dir = Path.Combine(_dir, "unread"), Tag = FileTag, Kind = IncidentKind.Growth, OnsetUtc = made, OnsetMs = At(made).Ms,
            ClosedUtc = made.AddMinutes(2), ClosedMs = At(made.AddMinutes(2)).Ms, EndUtc = made.AddMinutes(1), EndMs = At(made.AddMinutes(1)).Ms,
        };
        writer.Write(w.History, unread, []);
        Assert.Equal($"- Growth: 0 B -> 0 B (+0 B) from {Fmt.Clock(made)} to {Fmt.Clock(made.AddMinutes(1))}", Line(unread, "- Growth: "));
    }

    /// <summary>
    /// A "Capture now" trace of a minute on the owner's PC (26 events a sample, about 157 in the trace), File holding
    /// still: the incident made at the 30th sample, the trace from 3.7 s after, attached 16 s after it ended.
    /// <c>Step</c>: one more sample, with or without its events. <paramref name="host"/>: the host its folder is written
    /// through (by default one that counts what is written).
    /// </summary>
    private (IncidentManager Mgr, Incident Inc, int InTrace, Func<List<int>> InFile, Action<bool> Step, Action Finish) OwnersTrace(WriteBudget? budget = null, IIncidentHost? host = null)
    {
        var (mgr, inc, inTrace, inFile, step, finish, _) = OwnersTraceIn(budget, host);
        return (mgr, inc, inTrace, inFile, step, finish);
    }

    /// <summary><see cref="OwnersTrace"/> with its world, for a test that makes more incidents in it.</summary>
    private (IncidentManager Mgr, Incident Inc, int InTrace, Func<List<int>> InFile, Action<bool> Step, Action Finish, World W) OwnersTraceIn(WriteBudget? budget = null, IIncidentHost? host = null)
    {
        Directory.CreateDirectory(_dir);
        var w = new World { Interval = TimeSpan.FromSeconds(10) };
        var mgr = new IncidentManager(new Settings(), new TagKnowledge(null, null), host ?? new CountingHost(_dir), w.Registry);
        if (budget is not null) mgr.Budget = budget;
        var det = new Detector(new Settings());
        w.AddProcess(4, 0, "System");
        int pid = 9000;
        var times = new Dictionary<int, DateTime>();
        void Step(bool events)
        {
            w.Sample(("File", 100 * Settings.MB));
            var now = w.Now - w.Interval;
            if (events)
            {
                var added = Enumerable.Range(0, 26).Select(k => new LifecycleEvent { TimeUtc = now.AddSeconds(-10).AddMilliseconds(k * 380), Kind = LifecycleKind.Start, Pid = pid++, CreateTime = 1 + pid, Image = "x.exe" }).ToList();
                foreach (var e in added) times[e.Pid] = e.TimeUtc;
                mgr.AddEvents(added, At(now));
            }
            mgr.Process(w.History, det, [], At(now));
        }
        for (int i = 0; i < 30; i++) Step(true);
        var made = w.Now - w.Interval;
        var inc = mgr.CreateCapture(w.History, [FileTag], made);
        for (int i = 0; i < 8; i++) Step(true);
        var (start, end) = (made.AddSeconds(3.7), made.AddSeconds(63.7));
        mgr.ApplyCapture(w.History, inc, new CaptureResult { PoolEventsParsed = true, Tags = [FileTag] }.Recorded(At(start), At(end)));
        var trace = times.Where(kv => kv.Value >= start && kv.Value <= end).Select(kv => kv.Key).Order().ToList();
        List<int> InFile() => Rows(inc).Where(r => r[1] == "Start").Select(r => int.Parse(r[2])).ToList();
        Assert.Equal(trace, InFile().Intersect(trace)); // whatever it holds of the trace is in time order
        return (mgr, inc, trace.Count, InFile, Step, () => mgr.Finish(w.History, w.Now), w);
    }

    [Fact]
    public void ATracesAttachingWriteGivesItsEventsCsvEveryEventOfTheTraceWhateverTheBudget()
    {
        // The usual Capture now on a busy PC: a minute's trace has over 100 events and under 500, so its file holds its
        // first part alone. With no room in the write budget, and no late event after the trace is attached: the
        // attaching write, a write that must be made, gives the file every event of the trace, and report.md counts
        // them. Nothing more is written for it, ten minutes on or at the end of the run. (Before, that write gave it
        // 100 rows as a write that can wait does, report.md said "(156 other events in the window; events.csv gets the
        // rest when PoolWatch's write budget has room ...)", and with no late event of the window no write came two
        // minutes after its end either, although the docs said one would: the file stayed at 100 rows until PoolWatch
        // stopped.)
        var (mgr, inc, inTrace, inFile, step, finish) = OwnersTrace(new WriteBudget(1, 1));
        Assert.InRange(inTrace, 150, 160);
        Assert.Equal(inTrace, inFile().Count);
        Assert.Equal($"({inTrace} other events in events.csv)", EventsLine(inc));
        long wrote = inc.LastWriteMs;
        for (int i = 0; i < 60; i++) step(true);
        Assert.Equal(wrote, inc.LastWriteMs);
        finish();
        Assert.Equal(inTrace, inFile().Count);
        Assert.Equal($"({inTrace} other events in events.csv)", EventsLine(inc));
    }

    [Fact]
    public void AClosedIncidentsRowsThatWaitedAreWrittenWithItsReport()
    {
        // The same trace, with room in the budget. A sample after it is attached brings 150 late events of its window at
        // once (rollups of its last seconds), and then the PC is silent. A write that can wait adds 100 rows at a time:
        // the first, at that sample, gives the file 100 of them with a report that says the rest follow; the rest come
        // 30 s later, and the report is written with them: it counts the file's rows, and no longer says that any is to
        // come. (Before, the rows that waited were appended alone: report.md went on saying "events.csv gets the rest
        // when PoolWatch's write budget has room" of a file that had every row, until PoolWatch stopped.)
        var (mgr, inc, inTrace, inFile, step, _) = OwnersTrace();
        Assert.Equal(inTrace, inFile().Count);
        var end = inc.ClosedUtc!.Value;
        var late = Enumerable.Range(0, 150).Select(k => new LifecycleEvent { TimeUtc = end.AddSeconds(-20).AddMilliseconds(k * 100), Kind = LifecycleKind.Start, Pid = 80_000 + k, CreateTime = 1, Image = "late.exe" }).ToList();
        mgr.AddEvents(late, At(end.AddSeconds(26.3))); // with the sample 26.3 s after the trace's end
        step(false);
        Assert.Equal(inTrace + 100, inFile().Count);
        Assert.Equal($"({inTrace + 150} other events in the window; events.csv gets the rest when PoolWatch's write budget has room, and the day's `data\\<yyyyMMdd>\\events.csv` has every one)", EventsLine(inc));
        for (int i = 0; i < 3; i++) step(false); // 30 s on, before the write two minutes after the window's end
        Assert.True(mgr.LateEventMargin > TimeSpan.FromSeconds(66.3));
        Assert.Equal(inTrace + 150, inFile().Count);
        Assert.Equal($"({inTrace + 150} other events in events.csv)", EventsLine(inc));
    }

    [Fact]
    public void ATracesFileHoldsNoRowFromBeforeItsTraceWhenTheTraceItselfHasNoEvent()
    {
        // An event timed at the very moment a trace's incident is made comes with the next sample and is written to its
        // file (the incident's window is that moment until its trace finishes). No other event comes. The trace ran
        // from 3.7 s after that moment: its window holds no event. As it is attached, the file is written again from
        // the trace's start all the same: it holds no event row, as the window does. (Before, a file was left as it was
        // while the first part held no event to start it with, the rule meant for an open incident's file: this one
        // kept its row from before the trace to the end of the run, and report.md said so.)
        Directory.CreateDirectory(_dir);
        var w = new World { Interval = TimeSpan.FromSeconds(10) };
        var mgr = new IncidentManager(new Settings(), new TagKnowledge(null, null), new CountingHost(_dir), w.Registry) { Budget = new WriteBudget(1L << 30, 1L << 30) };
        var det = new Detector(new Settings());
        w.AddProcess(4, 0, "System");
        int pid = 9000;
        Incident? inc = null;
        void Step()
        {
            w.Sample(("File", 100 * Settings.MB));
            var now = w.Now - w.Interval;
            var events = Enumerable.Range(0, 5).Select(k => new LifecycleEvent { TimeUtc = now.AddSeconds(-10 + 2 * k), Kind = LifecycleKind.Start, Pid = pid++, CreateTime = 1 + pid, Image = "x.exe" }).ToList();
            // Once the incident is made: only the event of that very moment (it comes with the next sample), then none.
            if (inc is not null) events = now == inc.OpenedUtc.AddSeconds(10) ? events.Take(1).ToList() : [];
            mgr.AddEvents(events, At(now));
            if (inc is { Capture: null }) mgr.Note(inc, now, $"still recording at {Fmt.Clock(now)}"); // as the pipeline notes it: the folder is written
            mgr.Process(w.History, det, [], At(now));
        }
        for (int i = 0; i < 30; i++) Step();
        var made = w.Now - w.Interval;
        inc = mgr.CreateCapture(w.History, [FileTag], made);
        for (int i = 0; i < 8; i++) Step();
        Assert.Equal([Iso(made)], Rows(inc).Where(r => r[1] == "Start").Select(r => r[0])); // the event of the moment it was made
        var (start, end) = (made.AddSeconds(3.7), made.AddSeconds(63.7));
        mgr.ApplyCapture(w.History, inc, new CaptureResult { PoolEventsParsed = true, Tags = [FileTag] }.Recorded(At(start), At(end)));
        Assert.DoesNotContain(Rows(inc), r => r[1] == "Start");
        Assert.Equal("", EventsLine(inc));
        Assert.Contains("None recorded.", File.ReadAllLines(Path.Combine(inc.Dir, "report.md")));
        for (int i = 0; i < 30; i++) Step();
        mgr.Finish(w.History, w.Now);
        Assert.DoesNotContain(Rows(inc), r => r[1] == "Start");
        Assert.Equal("", EventsLine(inc));
    }

    [Fact]
    public void TheSpanAReportCountsOverIsNamedByWhatTheIncidentIsOf()
    {
        // report.md gives the tag's allocation rates over the incident's span. That span is a growth for a growth's
        // incident, the drop for a drop's (kind Release), and the trace for an incident made for a trace of its own
        // whose tag did not grow in it. (Before, all three said "during the growth": a drop of 600 MB had "Allocation
        // shape during the growth: ... freeing more than allocating" under "dropped 600 MB", and a trace of a tag that
        // held still had it under "Growth: 900 MB -> 900 MB (+0 B)".)
        Directory.CreateDirectory(_dir);
        // Samples 10 s apart; File busy in both counters: 500 allocations and 500 frees a sample while it holds still,
        // 600 and 100 while it grows, 100 and 3,000 while it drops.
        var h = History.ForInterval(10);
        var t0 = new DateTime(2026, 11, 9, 22, 0, 0, DateTimeKind.Utc);
        uint allocs = 0, frees = 0;
        long level = 900 * Settings.MB;
        int n = 0;
        void Add(int moves)
        {
            allocs += moves > 0 ? 600u : moves < 0 ? 100u : 500u;
            frees += moves > 0 ? 100u : moves < 0 ? 3000u : 500u;
            level += moves * 100 * Settings.MB;
            var t = t0.AddSeconds(10 * n++);
            h.Add(new Sample(t, default, [new PoolTagEntry(FileTag, 0, 0, 0, allocs, frees, (ulong)level)], [])
            {
                TickMs = At(t).Ms, System = new SystemCounters(20UL << 30, 64UL << 30, 8UL << 30, 1UL << 30, 100_000, 300),
            });
        }
        var mgr = new IncidentManager(new Settings(), new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry()) { Budget = new WriteBudget(1L << 30, 1L << 30) };
        var det = new Detector(new Settings());
        for (int i = 0; i < 60; i++) Add(0);

        // A trace of a tag that holds still: Capture now, a minute's trace.
        var made = h.Times[^1];
        var flat = mgr.CreateCapture(h, [FileTag], made);
        for (int i = 0; i < 8; i++) Add(0);
        mgr.ApplyCapture(h, flat, new CaptureResult { PoolEventsParsed = true, Tags = [FileTag] }.Recorded(At(made.AddSeconds(3.7)), At(made.AddSeconds(63.7))));
        Assert.Equal(0, flat.Growth);
        Assert.StartsWith("- Allocation shape during the trace: 3,000 allocations/min, 3,000 frees/min, against 3,000 and 3,000 a minute just before it: ", Line(flat, "- Allocation shape"));
        Assert.Equal("the trace", IncidentWriter.SpanName(flat));

        // A trace of a tag that grows through it: its span is its growth.
        made = h.Times[^1];
        var grew = mgr.CreateCapture(h, [FileTag], made);
        for (int i = 0; i < 8; i++) Add(1);
        mgr.ApplyCapture(h, grew, new CaptureResult { PoolEventsParsed = true, Tags = [FileTag] }.Recorded(At(made.AddSeconds(3.7)), At(made.AddSeconds(63.7))));
        Assert.True(grew.Growth > 0);
        Assert.StartsWith("- Allocation shape during the growth: 3,600 allocations/min, 600 frees/min", Line(grew, "- Allocation shape"));
        Assert.Equal("the growth", IncidentWriter.SpanName(grew));

        // A drop: 600 MB in a minute, seen by the release rule.
        for (int i = 0; i < 6; i++) Add(0);
        for (int i = 0; i < 6; i++) Add(-1);
        var seen = h.Times[^1];
        var onset = seen.AddSeconds(-60);
        mgr.Process(h, det, [new Alert(AlertKind.Release, FileTag, seen, "dropped > 500 MB in 1 min", 600 * Settings.MB, TimeSpan.FromMinutes(1), onset) { Ms = At(seen).Ms, OnsetMs = At(onset).Ms }], At(seen));
        var drop = mgr.Closed.Single(i => i.Kind == IncidentKind.Release);
        Assert.StartsWith("- Allocation shape during the drop: 600 allocations/min, 18,000 frees/min", Line(drop, "- Allocation shape"));
        Assert.Equal("the drop", IncidentWriter.SpanName(drop));
        Assert.DoesNotContain(File.ReadAllLines(Path.Combine(drop.Dir, "report.md")), l => l.Contains("during the growth"));
        Assert.DoesNotContain(File.ReadAllLines(Path.Combine(flat.Dir, "report.md")), l => l.Contains("during the growth"));

        // A growth's incident: the growth.
        var growth = new Incident { Id = "g", Dir = Path.Combine(_dir, "g"), Tag = FileTag, Kind = IncidentKind.Growth, OnsetUtc = t0, OnsetMs = At(t0).Ms };
        Assert.Equal("the growth", IncidentWriter.SpanName(growth));
    }

    [Fact]
    public void AnIncidentFoundStillHeldAfterItsCloseGivesItsLevelWithThatSamplesTime()
    {
        // A growth that stops: its incident closes after its quiet half minute, and a minute after its suspect went
        // quiet the tag still holds its rise (retained). The tag's level is read again at that sample, half a minute
        // after the close, and the report written then gives it with that sample's time, as report.json does.
        Directory.CreateDirectory(_dir);
        var settings = new Settings { GrowthRules = [new(1, 3 * Settings.MB)], IncidentQuietMinutes = 0.5, RetentionMinutes = 1 };
        var w = new World();
        var mgr = new IncidentManager(settings, new TagKnowledge(null, null), new CountingHost(_dir), w.Registry) { RewriteInterval = TimeSpan.Zero, Budget = new WriteBudget(1L << 30, 1L << 30) };
        var det = new Detector(settings);
        w.AddProcess(4, 0, "System");
        w.AddProcess(100, 4, "leaker.exe");
        long file = 50 * Settings.MB;
        void Step()
        {
            w.Sample(("File", file));
            mgr.Process(w.History, det, det.Evaluate(w.History), At(w.Now - w.Interval));
        }
        for (int i = 0; i < 15; i++) Step();
        for (int i = 0; i < 20; i++) { w.Io(100, other: 2000); file += 800_000; Step(); }
        Incident? inc = null;
        for (int i = 0; i < 90 && (inc = mgr.Closed.SingleOrDefault())?.Retention is null; i++) Step();
        Assert.NotNull(inc?.Retention);
        var found = inc!.Retention!.TimeUtc;
        Assert.True(found >= inc.ClosedUtc!.Value.AddSeconds(20), $"closed {inc.ClosedUtc:T}, found still held {found:T}");
        Assert.Equal((DateTime?)found, inc.CurrentAtUtc);
        Assert.EndsWith($"; {Fmt.Bytes(inc.CurrentBytes)} at {Fmt.Clock(found)}", Line(inc, "- Growth: "));
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(inc.Dir, "report.json")));
        Assert.Equal(found, json.RootElement.GetProperty("currentAtUtc").GetDateTime().ToUniversalTime());

        // Its "Retained" line is of that sample too, and says so, in the past tense: the incident is closed, and the
        // line stays in its report however much later that is written. Here the tag falls back to where it began, and
        // five minutes on a note has the report written again: the line is as it was, true of the time it gives.
        // (Before, it read "File still holds ... of its ... rise 1 min after leaker.exe (PID 100) went quiet", in the
        // present and with no time, in every later write: beside a level long fallen, or a "Released" line.)
        string note = $"File still held {Fmt.Bytes(inc.Retention.RetainedBytes)} of its {Fmt.Bytes(inc.Growth)} rise at {Fmt.Clock(found)}, 1 min after leaker.exe (PID 100) went quiet";
        Assert.Equal(note, inc.Retention.Note);
        Assert.Equal($"- **Retained**: {note}.", Line(inc, "- **Retained**"));
        Assert.Equal(note, json.RootElement.GetProperty("retention").GetProperty("note").GetString());
        file = 50 * Settings.MB;
        for (int i = 0; i < 150; i++) Step();
        mgr.Note(inc, w.Now - w.Interval, "a note added five minutes on");
        Step();
        string md = File.ReadAllText(Path.Combine(inc.Dir, "report.md"));
        Assert.Contains("a note added five minutes on", md); // written again
        Assert.Equal($"- **Retained**: {note}.", Line(inc, "- **Retained**"));
        Assert.DoesNotContain("still holds", md);
    }

    /// <summary>Late events of a trace's window, 0.1 s apart from 30 s before its end, as the rollups of its last seconds come after it is attached.</summary>
    private static List<LifecycleEvent> LateOf(Incident inc, int n) => Enumerable.Range(0, n)
        .Select(k => new LifecycleEvent { TimeUtc = inc.ClosedUtc!.Value.AddSeconds(-30).AddMilliseconds(k * 100), Kind = LifecycleKind.Start, Pid = 80_000 + k, CreateTime = 1, Image = "late.exe" }).ToList();

    [Fact]
    public void MoreLateEventsThanTwoWritesAddComeAHundredRowsAtATimeHalfAMinuteApart()
    {
        // The owner's trace again, and 250 late events of its window at once. A write that can wait adds 100 rows: 100
        // with the report at that sample, 100 more with the report again 30 s on, and the last 50 with it 30 s after
        // that. Until then the report says the file gets the rest; from then on it counts the file's rows. (The docs
        // said "the rest 30 seconds on", which holds only up to 200 late events.)
        var (mgr, inc, inTrace, inFile, step, _) = OwnersTrace(new WriteBudget(1L << 30, 1L << 30));
        mgr.AddEvents(LateOf(inc, 250), At(inc.ClosedUtc!.Value.AddSeconds(26.3))); // with the sample 26.3 s after the trace's end
        string rest = $"({inTrace + 250} other events in the window; events.csv gets the rest when PoolWatch's write budget has room, and the day's `data\\<yyyyMMdd>\\events.csv` has every one)";
        var late = new List<int>();
        var lines = new List<string>();
        for (int i = 0; i <= 6; i++)
        {
            step(false);
            late.Add(inFile().Count - inTrace);
            lines.Add(EventsLine(inc));
        }
        Assert.Equal([100, 100, 100, 200, 200, 200, 250], late); // at that sample, 30 s on and 60 s on
        Assert.All(lines.Take(6), l => Assert.Equal(rest, l));
        Assert.Equal($"({inTrace + 250} other events in events.csv)", lines[6]);
        Assert.True(mgr.LateEventMargin > TimeSpan.FromSeconds(26.3 + 60), "all before the write two minutes after the window's end");
    }

    /// <summary>
    /// A host through which one file of an incident's folder cannot be written while a test holds it, as when another
    /// program has that file open: its write check refuses the temp file the file is replaced from (every file is
    /// written so, but for rows added to an events.csv). The same on every OS: on Linux, where these tests run too, a file another program
    /// holds open can still be replaced. As the app's host does, it gives PoolWatch's own cost with the system's
    /// details, which differs at every reading: every write of a folder's reports then has a report to write (a
    /// report that says what the file holds already is not written again). It counts the writes it refused (<see cref="Refused"/>):
    /// each is one try at the folder that failed (two when a try was refused two held files: a write goes on after a
    /// timeline.csv or a processes.csv it cannot write).
    /// </summary>
    private sealed class HoldingHost(string dir) : IIncidentHost
    {
        private readonly CountingHost _inner = new(dir);
        private int _readings;
        /// <summary>The name of the file that cannot be written now (null: none).</summary>
        public string? Held { get; set; }
        /// <summary>The name of a second file that cannot be written now, held in the same folders as <see cref="Held"/> (null: none).</summary>
        public string? AlsoHeld { get; set; }
        /// <summary>The one folder the file is held in (null: in every incident's folder).</summary>
        public string? In { get; set; }
        /// <summary>An incident's folder that cannot be written at all now (null: none): its own check is refused, before any file of it.</summary>
        public string? Folder { get; set; }
        /// <summary>The host draws no chart (as one that cannot): no folder gets a timeline.png, which is no failed write.</summary>
        public bool NoChart { get; set; }
        /// <summary>How many writes of the held file, or of the folder that cannot be written, were refused.</summary>
        public int Refused { get; private set; }
        public List<string> Logs => _inner.Logs;
        public string IncidentsDir => _inner.IncidentsDir;
        public string CheckWrite(string path)
        {
            if (Folder is { } refused && string.Equals(path, refused, StringComparison.OrdinalIgnoreCase))
            {
                Refused++;
                throw new UnauthorizedAccessException($"Access to the path '{Path.GetFileName(refused)}' is denied.");
            }
            string? name = new[] { Held, AlsoHeld }.FirstOrDefault(n => n is not null && Path.GetFileName(path).Equals(n + ".tmp", StringComparison.OrdinalIgnoreCase));
            if (name is null) return _inner.CheckWrite(path);
            if (In is { } folder && !string.Equals(Path.GetDirectoryName(path), folder, StringComparison.OrdinalIgnoreCase)) return _inner.CheckWrite(path);
            Refused++;
            throw new IOException($"The process cannot access the file '{name}' because it is being used by another process.");
        }
        public FileDetails? DescribeFile(string? path) => _inner.DescribeFile(path);
        public SystemInfo GetSystemInfo() => _inner.GetSystemInfo() with { PoolWatchCost = $"PoolWatch itself: reading {++_readings}" };
        public bool RenderTimeline(TimelineChart chart, string pngPath) => !NoChart && _inner.RenderTimeline(chart, pngPath);
        public void RequestHandleScan() => _inner.RequestHandleScan();
        public void RequestTagScan(IReadOnlyCollection<PoolTag> tags) => _inner.RequestTagScan(tags);
        public string DisplayUser(string user) => _inner.DisplayUser(user);
        public SessionDetails? DescribeSession(Suspect suspect) => _inner.DescribeSession(suspect);
        public void Log(string message) => _inner.Log(message);
    }

    [Theory]
    [InlineData(150, 1)]
    [InlineData(250, 2)]
    public void AWaitingWriteWhoseRowsAreWrittenAndWhoseReportCannotBeIsLoggedAsAFailedWriteNotAsEventsCsvs(int lateEvents, int tries)
    {
        // The owner's trace, and late events of its window at once: 100 rows with the report at that sample, the rest
        // wait. From then on another program holds report.md. 30 s on the rows that waited are written (100 more), and
        // the report beside them cannot be: the log says that the write failed, as of any write, with no word of
        // events.csv, which was written. With 250 late events 50 rows still wait after that write: the next sample
        // writes them and fails at the report again. A closed incident's failed write is said once, with what
        // PoolWatch does about it, however many tries fail after it (before, each was logged).
        // No line says "events.csv write failed", and none "events.csv is written again" when report.md is let go and
        // the folder is written again, which the log says once. (Before, the failure was logged as "events.csv write
        // failed ... (its rows are tried again at each sample ...)" of a file that had every row, and "events.csv is
        // written again" followed at a later write.)
        var host = new HoldingHost(_dir);
        var (mgr, inc, inTrace, inFile, step, _) = OwnersTrace(new WriteBudget(1L << 30, 1L << 30), host);
        mgr.AddEvents(LateOf(inc, lateEvents), At(inc.ClosedUtc!.Value.AddSeconds(26.3)));
        step(false);
        Assert.Equal(inTrace + 100, inFile().Count);
        string rest = EventsLine(inc);
        Assert.StartsWith($"({inTrace + lateEvents} other events in the window; events.csv gets the rest when PoolWatch's write budget has room", rest);
        Assert.DoesNotContain(host.Logs, l => l.Contains("failed", StringComparison.Ordinal));

        host.Held = "report.md";
        for (int i = 0; i < 6; i++) step(false); // a minute: the rows that waited, 100 at a write from 30 s on
        Assert.Equal(inTrace + lateEvents, inFile().Count); // every row is in the file
        Assert.Equal(tries, host.Refused); // a write for each 100 rows that waited, each failing at the report
        string failed = Assert.Single(host.Logs, l => l.Contains("failed", StringComparison.Ordinal));
        Assert.Equal($"incident {inc.Id}: write failed: The process cannot access the file 'report.md' because it is being used by another process." +
                     " (while PoolWatch holds the incident it tries again, also as it stops; this is said once until the folder is written)", failed);
        Assert.DoesNotContain(host.Logs, l => l.Contains("events.csv is written again", StringComparison.Ordinal));
        Assert.Equal(rest, EventsLine(inc)); // the report is as it was: it could not be written
        string again = $"incident {inc.Id}: its folder is written again";
        Assert.DoesNotContain(again, host.Logs);

        // report.md is let go. The folder is written again within a minute of the last try that failed (and once
        // more two minutes after the window's end: the late rows sit after rows of later times): the report counts
        // the file's rows, the log says once that the folder is written again, and nothing of events.csv.
        host.Held = null;
        for (int i = 0; i < 9; i++) step(false);
        Assert.Equal($"({inTrace + lateEvents} other events in events.csv)", EventsLine(inc));
        Assert.Single(host.Logs, l => l.Contains("failed", StringComparison.Ordinal));
        Assert.Single(host.Logs, l => l == again);
        Assert.Equal(tries, host.Refused);
        Assert.False(inc.WriteFailed);
        Assert.DoesNotContain(host.Logs, l => l.Contains("events.csv is written again", StringComparison.Ordinal));
    }

    [Fact]
    public void AnExportWhoseCountRunsPastTheWindowsEndSaysSoInItsGapRowAndItsReport()
    {
        // The staircase with the buffer kept 10 minutes, the incident still open: Export report takes both ends of its
        // window from memory. Its end was measured 20 minutes earlier since its events were counted, and PoolWatch no
        // longer holds the events between: the count stays as it was made. The export's Gap row says that it runs past
        // the window's end, and so does the report.md that goes into the zip beside it, which counts that file's rows.
        // (The report's line comes from what the export's file holds: without it the zip's report gave the count as
        // the window's alone, beside a Gap row that said otherwise.)
        var (mgr, inc, times, w) = LongGrowth(376, 60, Staircase, alertFrom: 129, onsetAt: 126, settings: new Settings { IncidentEventKeepMinutes = 10 });
        var top1 = World.T0.AddSeconds(137 * 10);
        Assert.Equal((DateTime?)top1, inc.EndUtc);
        var end = top1.AddMinutes(1); // the window's end
        var files = mgr.ExportFilesNow(w.History, inc)!;
        var rows = files["events.csv"].Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(l => PoolWatch.Core.Util.Csv.Split(l.TrimEnd('\r'))).ToList();
        var gap = Assert.Single(rows, r => r[1] == "Gap");
        var m = System.Text.RegularExpressions.Regex.Match(gap[9], @"^([0-9,]+) events left out, between (\S+) and (\S+) \((.*)\)$");
        Assert.True(m.Success, gap[9]);
        int left = int.Parse(m.Groups[1].Value, System.Globalization.NumberStyles.AllowThousands, System.Globalization.CultureInfo.InvariantCulture);
        DateTime firstLeft = Time(m.Groups[2].Value), lastLeft = Time(m.Groups[3].Value);
        Assert.True(lastLeft > end.AddMinutes(10), $"the count runs to {lastLeft:T}, past the window's end {end:T}");
        Assert.Equal(times.Count(t => t >= firstLeft && t <= lastLeft), left); // every event between its two times, each once
        string past = "the count runs past the window's end, {0} when {1} file was written: PoolWatch no longer held those events, to count each by its own time against that end";
        Assert.EndsWith("; " + string.Format(past, Iso(end), "this"), m.Groups[4].Value);
        int inFile = rows.Count(r => r[1] == "Start");
        string line = files["report.md"].Split('\n').Select(l => l.TrimEnd('\r')).Single(l => l.StartsWith('(') && l.Contains(" other events in "));
        Assert.Equal($"({inFile} other events in events.csv; it leaves out {left:N0} events, between {Fmt.Clock(firstLeft)} and {Fmt.Clock(lastLeft)}, as its Gap row says; " +
                     string.Format(past, Fmt.Clock(end), "the") + ")", line);
    }

    /// <summary>
    /// A PC of 10 s samples with node.exe (PID 100) busy beside File, whose level a test moves: the incident manager
    /// over it, with room in its write budget, and <see cref="Step"/>, more samples.
    /// </summary>
    private sealed class NodeWorld
    {
        public readonly World W = new() { Interval = TimeSpan.FromSeconds(10) };
        public readonly CountingHost Host;
        public readonly IncidentManager Mgr;
        private readonly Detector _det = new(new Settings());
        /// <summary>File's level at the next sample.</summary>
        public long Level = 100 * Settings.MB;
        /// <summary>File has left the tag list.</summary>
        public bool Gone;
        private bool _exited;

        public NodeWorld(string dir, IIncidentHost? host = null)
        {
            Directory.CreateDirectory(dir);
            Host = new CountingHost(dir);
            Mgr = new IncidentManager(new Settings(), new TagKnowledge(null, null), host ?? Host, W.Registry) { Budget = new WriteBudget(1L << 30, 1L << 30) };
            W.AddProcess(4, 0, "System");
            W.AddProcess(100, 4, "node.exe");
        }

        /// <summary>The newest sample's time.</summary>
        public DateTime Now => W.Now - W.Interval;

        /// <summary><paramref name="samples"/> more samples, File <paramref name="rise"/> higher at each.</summary>
        public void Step(int samples = 1, long rise = 0)
        {
            for (int i = 0; i < samples; i++)
            {
                if (!_exited) W.Io(100, other: 300);
                Level += rise;
                if (Gone) W.Sample(("Proc", 5 * Settings.MB)); else W.Sample(("File", Level));
                Mgr.Process(W.History, _det, [], At(Now));
            }
        }

        /// <summary>node.exe exits: the next sample no longer lists it.</summary>
        public void Exit()
        {
            W.Exit(100);
            _exited = true;
        }

        /// <summary>
        /// "Capture now" at the newest sample: the incident, made closed, and its trace of a minute from 3.7 s after,
        /// attached 26.3 s after it ended, File <paramref name="rise"/> higher at each sample meanwhile. With
        /// <paramref name="node"/> the trace saw node.exe allocate 18 MB of File. <paramref name="beforeAttach"/> is
        /// called with the trace's result and the moment the incident was made, before the result is attached.
        /// </summary>
        public (Incident Inc, DateTime Made) Trace(long rise, bool node, Action<CaptureResult, DateTime>? beforeAttach = null)
        {
            var made = Now;
            var inc = Mgr.CreateCapture(W.History, [FileTag], made);
            Mgr.Note(inc, made, "capture started: pool events"); // as the pipeline notes it (the report shows a timeline of two entries or more)
            Step(9, rise);
            var start = made.AddSeconds(3.7);
            var cap = new CaptureResult
            {
                PoolEventsParsed = true, Tags = [FileTag],
                Pool = node ? [new CapturePoolUsage(100, "node.exe", FileTag, 18 * Settings.MB, 0, 1000, 0, [], ParentPid: 4, FirstAllocUtc: start.AddSeconds(5))] : [],
            }.Recorded(At(start), At(start.AddMinutes(1)));
            beforeAttach?.Invoke(cap, made);
            Mgr.ApplyCapture(W.History, inc, cap);
            return (inc, made);
        }
    }

    /// <summary>report.md's answer in plain words (the line under "In plain words").</summary>
    private static string Plain(Incident inc)
    {
        var md = File.ReadAllLines(Path.Combine(inc.Dir, "report.md"));
        return md[Array.IndexOf(md, "## In plain words") + 2];
    }

    /// <summary>report.json's <c>traceAnswer</c>.</summary>
    private static string? TraceAnswer(Incident inc)
    {
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(inc.Dir, "report.json")));
        return json.RootElement.GetProperty("traceAnswer").GetString();
    }

    [Fact]
    public void AClosedIncidentsTraceAnswerSaysTheTimeItsLevelIsOf()
    {
        // A trace's incident on a rising File, whose trace saw node.exe allocate it. Its answer says how much the tag
        // grew and where its level was against that: the incident is closed (with its trace), so the level is of the
        // last sample of the trace, and the answer says so, in report.md's plain words and in report.json's
        // traceAnswer, with the time the Growth line gives for that level. Three hours on File has fallen to a tenth
        // and then left the tag list; a note on the incident's timeline has its report written again, and the answer
        // is as it was: true of the time it gives. (Before, it read "grew 20 MB in 1 min and is still held", written
        // three hours after that sample, of a tag that was gone, beside a Growth line that said "244 MB at 07:05:50".)
        var n = new NodeWorld(_dir);
        n.Step(30, rise: 4 * Settings.MB);
        var (inc, made) = n.Trace(rise: 4 * Settings.MB, node: true);
        Assert.False(inc.IsOpen);
        var lastOfTrace = made.AddSeconds(60); // the trace ran from 3.7 s to 63.7 s after the incident was made
        Assert.Equal((DateTime?)lastOfTrace, inc.CurrentAtUtc);
        string what = new TagKnowledge(null, null).Lookup(FileTag).Description;
        string answer = $"File ({what}) grew {Fmt.Bytes(20 * Settings.MB)} in 1 min and was still held at {Fmt.Clock(lastOfTrace)}. The trace shows node allocating it: ";
        string plain = Plain(inc), json = TraceAnswer(inc)!;
        Assert.StartsWith(answer, plain);
        Assert.StartsWith(answer, json);
        Assert.EndsWith($"; {Fmt.Bytes(inc.CurrentBytes)} at {Fmt.Clock(lastOfTrace)}", Line(inc, "- Growth: ")); // the same level, with the same time

        // Three hours on: File fell to a tenth an hour ago, and has now left the tag list.
        n.Step(700);
        n.Level = 10 * Settings.MB;
        n.Step(300);
        n.Gone = true;
        n.Step(80);
        n.Mgr.Note(inc, n.Now, "a note added three hours on");
        n.Step();
        Assert.DoesNotContain(n.Host.Logs, l => l.Contains("failed", StringComparison.Ordinal));
        string md = File.ReadAllText(Path.Combine(inc.Dir, "report.md"));
        Assert.Contains("a note added three hours on", md); // written again
        Assert.Equal(plain, Plain(inc));
        Assert.Equal(json, TraceAnswer(inc));
        Assert.DoesNotContain(" is still held", md);
        Assert.DoesNotContain(" is still held", File.ReadAllText(Path.Combine(inc.Dir, "report.json")));
    }

    /// <summary>The "Started" line of the first process report.md gives details of (its top suspect).</summary>
    private static string Started(Incident inc) =>
        File.ReadAllLines(Path.Combine(inc.Dir, "report.md")).First(l => l.StartsWith("- Started: ", StringComparison.Ordinal));

    [Fact]
    public void AClosedIncidentsSuspectIsSaidAsStillRunningWhenItClosedNotNow()
    {
        // A trace's incident whose top suspect, node.exe, runs on when its trace is attached: its report says "still
        // running when it closed". node.exe exits ten minutes later; three hours on a note has the report written
        // again, with the suspects as they were ranked at the attach (a closed incident's are not ranked again): the
        // line is as it was, and true. (Before, the report written again three hours later said "still running" of a
        // process that had exited ten minutes after the close.)
        var n = new NodeWorld(_dir);
        n.Step(30, rise: 4 * Settings.MB);
        var (inc, made) = n.Trace(rise: 4 * Settings.MB, node: false);
        var top = inc.Suspects[0];
        Assert.Equal(("node.exe", 100, (DateTime?)null, false), (top.Image, top.Key.Pid, top.ExitUtc, top.Unresolved));
        string started = $"- Started: {Fmt.Stamp(top.StartUtc)}; still running when it closed";
        Assert.Equal(started, Started(inc));
        // The ranking marks a process the registry holds no record of (it is named by when the history saw it), and
        // no other: only of one it holds a record of does an empty exit mean that it ran.
        var unknown = Correlator.Rank(n.W.History, new ProcessRegistry(), FileTag, inc.Window!);
        Assert.NotEmpty(unknown);
        Assert.All(unknown, s => Assert.True(s.Unresolved, s.DisplayName));
        Assert.All(Correlator.Rank(n.W.History, n.W.Registry, FileTag, inc.Window!), s => Assert.False(s.Unresolved, s.DisplayName));
        n.Step(60);
        n.Exit();
        n.Step(6);
        Assert.NotNull(n.W.Registry.Get(top.Key)?.ExitUtc); // PoolWatch has read its exit, a minute on
        n.Step(1000);
        n.Mgr.Note(inc, n.Now, "a note added three hours on");
        n.Step();
        Assert.Contains("a note added three hours on", File.ReadAllText(Path.Combine(inc.Dir, "report.md"))); // written again
        Assert.Equal(started, Started(inc));

        // While an incident is open its suspects are as its last ranking found them, a few minutes ago at most: "still
        // running". A process PoolWatch held no record of when it was ranked (it is named by when the history saw
        // it) says neither: whether it still ran is not known.
        Suspect S(bool unresolved) => new()
        {
            Key = new ProcessKey(200, made.ToFileTimeUtc()), Image = "x.exe", DisplayName = "x.exe", StartUtc = made, Unresolved = unresolved,
            Direct = new DirectSignal(null, 0, 0, 0, 0, 0, false), Activity = new ActivitySignal("other", 0, 0, 0, 0, 0, false), Lifecycle = new LifecycleSignal(false, false, false, []),
        };
        Incident Of(string id, bool closed, bool unresolved) => new()
        {
            Id = id, Dir = Path.Combine(_dir, id), Tag = FileTag, Kind = IncidentKind.Growth, OnsetUtc = made, OnsetMs = At(made).Ms,
            ClosedUtc = closed ? made.AddMinutes(5) : null, ClosedMs = closed ? At(made.AddMinutes(5)).Ms : null, Suspects = [S(unresolved)],
        };
        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        string at = $"- Started: {Fmt.Stamp(made)}";
        foreach (var (id, closed, unresolved, said) in new[]
                 {
                     ("open", false, false, at + "; still running"), ("closed", true, false, at + "; still running when it closed"),
                     ("open-unknown", false, true, at), ("closed-unknown", true, true, at),
                 })
        {
            var of = Of(id, closed, unresolved);
            writer.Write(n.W.History, of, []);
            Assert.Equal(said, Started(of));
        }
    }

    [Fact]
    public void ATracesOwnersProcessStartsAreCountedDuringTheTraceWhenItsTagDidNotGrow()
    {
        // A trace's incident on a File that holds still, whose trace saw node.exe allocate 18 MB of it (and free as
        // much elsewhere: the level did not move). Its owner's line counts the owner's process starts over the
        // incident's span, which is the whole trace, and says so: "during the trace", as the allocation rates' line
        // does. (Before, both said "during the growth" under "Growth: 100 MB -> 100 MB (+0 B)".)
        var n = new NodeWorld(_dir);
        n.Step(30);
        var (inc, made) = n.Trace(rise: 0, node: true, (cap, at) =>
        {
            // Its owner's short commands, as the summarizer's rollups give them: 1,000 half a minute before the
            // incident was made, 5 and 7 during the trace, 10,000 after the trace's end.
            string owner = Assert.Single(TraceAttribution.Owners(TraceAttribution.Resolve(cap, FileTag, n.W.Registry))).Owner.Key;
            LifecycleEvent Rollup(double sec, int count) => new() { TimeUtc = at.AddSeconds(sec), Kind = LifecycleKind.Rollup, Pid = 100, OwnerKey = owner, Count = count };
            n.Mgr.AddEvents([Rollup(-30, 1_000), Rollup(20, 5), Rollup(63, 7), Rollup(70, 10_000)], At(n.Now));
        });
        Assert.Equal(0, inc.Growth);
        Assert.Equal("the trace", IncidentWriter.SpanName(inc));
        var md = File.ReadAllLines(Path.Combine(inc.Dir, "report.md"));
        Assert.EndsWith("; 12 process starts during the trace.", Assert.Single(md, l => l.StartsWith("- **", StringComparison.Ordinal) && l.Contains(" net in ", StringComparison.Ordinal)));
        Assert.DoesNotContain(md, l => l.Contains("during the growth", StringComparison.Ordinal));
    }

    [Fact]
    public void AllocationRatesFromAHistoryThatNoLongerHoldsTheStartNameTheSpanByWhatTheIncidentIsOf()
    {
        // The tag's allocation rates are counted from the incident's onset. When the history no longer reaches back to
        // it (a slow drop, a long trace: the raw history keeps 70 minutes), the line says from when they are counted and
        // why, naming the span as the incident's: the drop, the trace (of a tag that did not grow), or the growth.
        // (Before, all three said "(the history no longer holds the start of the growth)".)
        Directory.CreateDirectory(_dir);
        var (h, add, t0) = Minutes();
        for (int i = 1; i <= 30; i++) add(i);
        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        Incident Of(string id, IncidentKind kind) => new()
        {
            // Its onset a minute before the history's first sample, its close at the newest.
            Id = id, Dir = Path.Combine(_dir, id), Tag = FileTag, Kind = kind, OnsetUtc = t0, OnsetMs = At(t0).Ms, ClosedUtc = h.Times[^1], ClosedMs = h.Ms[^1],
        };
        Assert.True(h.Ms[0] > At(t0).Ms);
        foreach (var (kind, span) in new[] { (IncidentKind.Release, "the drop"), (IncidentKind.Capture, "the trace"), (IncidentKind.Growth, "the growth") })
        {
            var inc = Of(kind.ToString(), kind);
            Assert.Equal(span, IncidentWriter.SpanName(inc));
            writer.Write(h, inc, []);
            Assert.StartsWith($"- Allocation shape from {Fmt.Clock(h.Times[0])} (the history no longer holds the start of {span}): 3,000 allocations/min, 300 frees/min", Line(inc, "- Allocation shape"));
        }
    }

    /// <summary>An incident's answer in plain words: report.md's line under "In plain words", and report.json's <c>plainAnswer</c>.</summary>
    private static (string Md, string? Json) PlainAnswer(Incident inc)
    {
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(inc.Dir, "report.json")));
        return (Plain(inc), json.RootElement.GetProperty("plainAnswer").GetString());
    }

    /// <summary>How the plain answer names File in these worlds (its description, the tag and its pool).</summary>
    private const string FileSaid = "File objects (charged to the opening process) (File, non-paged)";

    [Theory]
    [InlineData(0, "fell 140 MB in 1 min.", "dropped 140 MB at ")]
    [InlineData(200_000, "fell 141.5 MB in 10 s.", "dropped 141.5 MB at ")]
    public void ADropsPlainWordsSayTheDropOverItsSpan(long riseBefore, string said, string headline)
    {
        // File holds 150 MB (or creeps up 1.6 MB in the 80 s before, under every growth rule) and drops to 10 MB at
        // one sample. The release rule sees the drop, and with no growth incident to attach it to, an incident of kind
        // Release is made. Its plain words say the drop over its span, as its headline and its "Released" line do, in
        // report.md and in report.json's plainAnswer: as it is made, and three hours on, when File is at 60 MB and a
        // note has the report written again. (Before, the answer was given the incident's growth, which is the rise in
        // the window before the drop and never below 0: "... is flat over 1 min." under "dropped 140 MB", and
        // "... grew 1.1 MB in 10 s. No clear cause yet: the suspects below account for 0 B of a 1.1 MB rise." under
        // "dropped 141.5 MB".)
        Directory.CreateDirectory(_dir);
        var settings = new Settings { ReleaseBytes = 2 * Settings.MB, ReleaseWindowMinutes = 1 };
        var w = new World { Interval = TimeSpan.FromSeconds(10) };
        var mgr = new IncidentManager(settings, new TagKnowledge(null, null), new CountingHost(_dir), w.Registry) { RewriteInterval = TimeSpan.Zero, Budget = new WriteBudget(1L << 30, 1L << 30) };
        var det = new Detector(settings);
        w.AddProcess(4, 0, "System");
        w.AddProcess(100, 4, "node.exe");
        long level = 150 * Settings.MB;
        void Step(long rise = 0)
        {
            w.Io(100, other: 300);
            level += rise;
            w.Sample(("File", level));
            mgr.Process(w.History, det, det.Evaluate(w.History), At(w.Now - w.Interval));
        }
        for (int i = 0; i < 30; i++) Step();
        if (riseBefore > 0) for (int i = 0; i < 8; i++) Step(riseBefore);
        level = 10 * Settings.MB;
        for (int i = 0; i < 12; i++) Step();
        var inc = Assert.Single(mgr.Closed);
        Assert.Equal(IncidentKind.Release, inc.Kind);
        string answer = $"{FileSaid} {said}";
        Assert.Equal((answer, answer), PlainAnswer(inc));
        string Head() => File.ReadAllLines(Path.Combine(inc.Dir, "report.md"))[0];
        Assert.StartsWith($"# `File` (nt File objects (charged to the opening process)) {headline}{Fmt.Clock(inc.Release!.TimeUtc)}; ", Head());
        Assert.Equal((0L, inc.Release.FromBytes - inc.Release.ToBytes), (inc.Release.ClearedBytes, inc.Release.Dropped)); // no clear of PoolWatch's own in it

        level = 60 * Settings.MB;
        for (int i = 0; i < 1080; i++) Step();
        mgr.Note(inc, w.Now - w.Interval, "a note added three hours on");
        Step();
        string md = File.ReadAllText(Path.Combine(inc.Dir, "report.md"));
        Assert.Contains("a note added three hours on", md); // written again
        Assert.Equal((answer, answer), PlainAnswer(inc));
        Assert.DoesNotContain(" is flat ", md);
        Assert.DoesNotContain(" grew ", md);
        Assert.Same(inc, Assert.Single(mgr.Closed)); // and File's later level opened nothing
    }

    /// <summary>A suspect with no evidence of its own, for an incident a test makes by hand.</summary>
    private static Suspect ASuspect(DateTime started) => new()
    {
        Key = new ProcessKey(200, started.ToFileTimeUtc()), Image = "x.exe", DisplayName = "x.exe", StartUtc = started,
        Direct = new DirectSignal(null, 0, 0, 0, 0, 0, false), Activity = new ActivitySignal("other", 0, 0, 0, 0, 0, false), Lifecycle = new LifecycleSignal(false, false, false, []),
    };

    [Fact]
    public void AClosedIncidentWhoseTagDidNotGrowIsSaidInThePastAndOneWhoseTagFellSaysSo()
    {
        // A trace's incident on a File that holds still at 100 MB, with a busy node.exe and no owner in the trace: its
        // plain words are the owners' answer, and the incident is closed (with its trace), so they say the tag "was
        // flat" over the trace's minute, in report.md and in report.json's plainAnswer. Three hours on File is at
        // 400 MB and a note has the report written again: the sentence is as it was, and true of its minute. (Before,
        // it read "... is flat over 1 min." in a report written three hours later, of a tag four times higher.)
        var n = new NodeWorld(Path.Combine(_dir, "flat"));
        n.Step(30);
        var (inc, _) = n.Trace(rise: 0, node: false);
        Assert.False(inc.IsOpen);
        string flat = $"{FileSaid} was flat over 1 min.";
        Assert.Equal((flat, flat), PlainAnswer(inc));
        n.Level = 400 * Settings.MB;
        n.Step(1080);
        n.Mgr.Note(inc, n.Now, "a note added three hours on");
        n.Step();
        string md = File.ReadAllText(Path.Combine(inc.Dir, "report.md"));
        Assert.Contains("a note added three hours on", md); // written again
        Assert.Equal((flat, flat), PlainAnswer(inc));
        Assert.DoesNotContain(" is flat ", md);
        Assert.DoesNotContain(" is flat ", File.ReadAllText(Path.Combine(inc.Dir, "report.json")));

        // A trace of a File that falls 4 MB a sample: its growth is 0 (an incident's growth is never below 0), and its
        // level fell 20 MB from the trace's first sample to its last. The plain words say that, as the line above them
        // gives it ("96 MB -> 96 MB (+0 B) ...; 76 MB at ..."), not that the tag was flat.
        var f = new NodeWorld(Path.Combine(_dir, "fell"));
        f.Step(30);
        var (fell, made) = f.Trace(rise: -4 * Settings.MB, node: false);
        Assert.Equal((0L, 96 * Settings.MB, 76 * Settings.MB), (fell.Growth, fell.StartBytes, fell.CurrentBytes));
        string fall = $"{FileSaid} fell 20 MB in 1 min.";
        Assert.Equal((fall, fall), PlainAnswer(fell));
        Assert.Equal($"- Growth: 96 MB -> 96 MB (+0 B) from {Fmt.Clock(made.AddSeconds(3.7))}; 76 MB at {Fmt.Clock(made.AddSeconds(60))}", Line(fell, "- Growth: "));
    }

    [Fact]
    public void ThePlainWordsSayWhatTheTagDidOverTheIncidentsSpanInThePresentOnlyWhileItIsOpen()
    {
        // What the answer in plain words says a tag did over its incident's span of 5 minutes, for incidents made by
        // hand, each from 100 MB: one that did not move is "flat", "is" while the incident is open (its span runs to
        // now) and "was" once it is closed; one whose level fell says how far, whether it is open or closed; where
        // PoolWatch's own cache clears freed part of what it lost, their part is no fall, and the rest fell "on its
        // own" (or the tag was flat, when they freed all of it), as a growth counts their part back; a drop's incident
        // says its drop, "on its own" when a clear freed more in its span; and one that grew says its growth, as
        // before, with "No clear cause yet" only while it is open: a closed incident's suspects are as they were
        // last ranked, so its report, written again hours later, says "No clear cause was found". A closed
        // incident whose level was never read from a sample says no fall. (Each incident made for a trace has its
        // trace, of the span's 5 minutes, as one that closed 5 minutes after its start has; the last case is one
        // whose trace is not attached yet.)
        Directory.CreateDirectory(_dir);
        var (h, add, t0) = Minutes();
        for (int i = 1; i <= 30; i++) add(i);
        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        const long MB = Settings.MB;
        DateTime onset = t0.AddMinutes(20), to = t0.AddMinutes(25);
        Incident Of(string id, IncidentKind kind, bool open, long peak, long? level, long cleared = 0, ReleaseRecord? drop = null) => new()
        {
            Id = id, Dir = Path.Combine(_dir, id), Tag = FileTag, Kind = kind, OnsetUtc = onset, OnsetMs = At(onset).Ms,
            ClosedUtc = open ? null : to, ClosedMs = open ? null : At(to).Ms, LastAlertUtc = to, LastAlertMs = At(to).Ms,
            StartBytes = 100 * MB, PeakBytes = peak * MB, CurrentBytes = (level ?? 0) * MB, CurrentAtUtc = level is null ? null : to, ClearedBytes = cleared * MB,
            Release = drop, Suspects = [ASuspect(t0)], Owners = [],
            Capture = kind == IncidentKind.Capture ? new CaptureResult { PoolEventsParsed = true, Tags = [FileTag] }.Recorded(At(onset), At(to)) : null,
            Window = new CorrelationWindow(onset.AddMinutes(-2), onset, null, to) { FromMs = At(onset.AddMinutes(-2)).Ms, OnsetMs = At(onset).Ms, ToMs = At(to).Ms },
        };
        ReleaseRecord Drop(long from, long toLevel, long cleared) => new(to, onset, from * MB, toLevel * MB, [], []) { ClearedBytes = cleared * MB };
        foreach (var (inc, moved, said) in new[]
                 {
                     (Of("flat-closed", IncidentKind.Capture, open: false, peak: 100, level: 100), (0L, false), "was flat over 5 min."),
                     (Of("flat-open", IncidentKind.Growth, open: true, peak: 100, level: 100), (0L, false), "is flat over 5 min."),
                     (Of("fell-closed", IncidentKind.Capture, open: false, peak: 100, level: 60), (-40 * MB, false), "fell 40 MB in 5 min."),
                     (Of("fell-open", IncidentKind.Growth, open: true, peak: 100, level: 60), (-40 * MB, false), "fell 40 MB in 5 min."),
                     (Of("fell-and-cleared", IncidentKind.Capture, open: false, peak: 100, level: 50, cleared: 40), (-10 * MB, true), "fell 10 MB on its own in 5 min."),
                     (Of("cleared-alone", IncidentKind.Capture, open: false, peak: 100, level: 60, cleared: 40), (0L, true), "was flat over 5 min."),
                     (Of("never-read", IncidentKind.Capture, open: false, peak: 100, level: null), (0L, false), "was flat over 5 min."),
                     (Of("drop", IncidentKind.Release, open: false, peak: 101, level: 10, drop: Drop(150, 10, 0)), (-140 * MB, false), "fell 140 MB in 5 min."),
                     (Of("drop-and-cleared", IncidentKind.Release, open: false, peak: 100, level: 50, drop: Drop(600, 50, 350)), (-200 * MB, true), "fell 200 MB on its own in 5 min."),
                     // What cannot be measured so is said as no move at all, never as a growth: a drop whose own part is not above 0, a level above the start with no growth.
                     (Of("drop-all-cleared", IncidentKind.Release, open: false, peak: 100, level: 90, drop: Drop(100, 90, 20)), (0L, true), "was flat over 5 min."),
                     (Of("above-start", IncidentKind.Capture, open: false, peak: 100, level: 120), (0L, false), "was flat over 5 min."),
                     (Of("grew", IncidentKind.Growth, open: false, peak: 130, level: 60), (30 * MB, false), "grew 30 MB in 5 min. No clear cause was found: the suspects below account for 0 B of a 30 MB rise."),
                     (Of("grew-open", IncidentKind.Growth, open: true, peak: 130, level: 130), (30 * MB, false), "grew 30 MB in 5 min. No clear cause yet: the suspects below account for 0 B of a 30 MB rise."),
                     (Of("grew-traced", IncidentKind.Capture, open: false, peak: 130, level: 130), (30 * MB, false), "grew 30 MB in 5 min. No clear cause was found: the suspects below account for 0 B of a 30 MB rise."),
                 })
        {
            Assert.Equal(moved, IncidentWriter.Moved(inc));
            writer.Write(h, inc, []);
            string answer = $"File objects (charged to the opening process) (File, non-paged) {said}";
            Assert.Equal((answer, answer), PlainAnswer(inc));
        }
        // An incident made for a trace whose trace is not attached yet (made closed, its start and its end one moment)
        // says neither: only that its trace had not finished, by the newest sample's time at that write.
        var made = Of("trace-not-attached", IncidentKind.Capture, open: false, peak: 100, level: 100);
        made.Capture = null;
        (made.ClosedUtc, made.ClosedMs) = (made.OnsetUtc, made.OnsetMs);
        writer.Write(h, made, []);
        string waits = $"File objects (charged to the opening process) (File, non-paged): its trace had not finished by {Fmt.Clock(h.Times[^1])}; this report is written again when it has.";
        Assert.Equal((waits, waits), PlainAnswer(made));
        // The live view's answer (TagViewBuilder) is of a window that runs to now: "is flat", and a fall as it is.
        var info = new TagKnowledge(null, null).Lookup(FileTag);
        Assert.Equal("File objects (charged to the opening process) (File, non-paged) is flat over the last hour.", OwnerRanking.Answer(info, FileTag, 0, 1, 0, "the last hour", []).Line);
        Assert.Equal("File objects (charged to the opening process) (File, non-paged) fell 40 MB in the last hour.", OwnerRanking.Answer(info, FileTag, 0, 1, -40 * MB, "the last hour", []).Line);
    }

    [Fact]
    public void AClosedIncidentsOwnerLineSaysWhatItsHandlesDidOverTheWindowNotWhatItHoldsNow()
    {
        // leaker.exe opens 2,000 File handles a sample for two minutes and File grows 96 MB with them: a growth with
        // handle evidence. Its incident closes after its quiet half minute and is found still held a minute after the
        // leaker went quiet. Under "In plain words" the owner's line says what its handles did over the window: "its
        // File handles rose by 24,000". Then leaker.exe exits and File falls to 10 MB; three hours on a note has the
        // report written again, with the owners as they were ranked at the close: the line is as it was, and true, beside
        // a Release section that says leaker.exe exited. (Before, it read "holds 24,000 more File handles", in the
        // present, three hours after the process had exited.) The same words while the incident is open, and in
        // the live view, which ranks the same way: there too a process whose handles rose can have exited since.
        Directory.CreateDirectory(_dir);
        var settings = new Settings { GrowthRules = [new(1, 3 * Settings.MB)], IncidentQuietMinutes = 0.5, RetentionMinutes = 1 };
        var w = new World { Interval = TimeSpan.FromSeconds(10) };
        var mgr = new IncidentManager(settings, new TagKnowledge(null, null), new CountingHost(_dir), w.Registry) { RewriteInterval = TimeSpan.Zero, Budget = new WriteBudget(1L << 30, 1L << 30) };
        var det = new Detector(settings);
        w.AddProcess(4, 0, "System");
        w.AddProcess(100, 4, "leaker.exe");
        long file = 50 * Settings.MB;
        bool exited = false;
        void Step(bool grow = false)
        {
            if (grow && !exited) { w.Io(100, other: 2000, handles: 2000); file += 8 * Settings.MB; }
            w.Sample(("File", file));
            w.ScanHandles();
            mgr.Process(w.History, det, det.Evaluate(w.History), At(w.Now - w.Interval));
        }
        for (int i = 0; i < 15; i++) Step();
        for (int i = 0; i < 12; i++) Step(grow: true);
        var open = Assert.Single(mgr.Open);
        Assert.StartsWith("its File handles rose by ", open.Owners![0].Plain[0]);
        Incident? inc = null;
        for (int i = 0; i < 90 && (inc = mgr.Closed.SingleOrDefault())?.Retention is null; i++) Step();
        Assert.NotNull(inc?.Retention);
        Assert.Same(open, inc);
        string grew = $"{FileSaid} grew 96 MB in 2 min. Most likely cause: leaker (strong evidence).";
        Assert.Equal((grew, grew), PlainAnswer(inc!));
        // A count and a share are written in the culture PoolWatch runs in ("24,000" and "100%" in English on Windows,
        // "100 %" in the invariant culture of a Linux runner), so the expected text formats them the same way.
        string rose = $"{24_000:N0}", all = $"{1.0:P0}";
        string line = $"- **leaker** (strong evidence): its File handles rose by {rose} ({all} of the File-handle growth, about 96 MB of the rise); " +
                      $"file and other I/O: 24 k operations ({all} of all); rose and fell with the tag: strongly. leaker: leaker.exe";
        Assert.Equal(line, Line(inc!, "- **leaker**"));

        // The live view ranks the owners of the last hour the same way (TagViewBuilder.Build): the same words.
        var window = CorrelationWindow.Recent(w.History, FileTag, TimeSpan.FromMinutes(60)).Window;
        var live = OwnerRanking.Rank(w.History, Correlator.Rank(w.History, w.Registry, FileTag, window, maxSuspects: 2000), FileTag, window, 96 * Settings.MB,
            s => PoolWatch.Core.Identity.ProcessIdentities.Of(w.Registry.Ancestry(s.Key)));
        Assert.Equal($"its File handles rose by {rose} ({all} of the File-handle growth, about 96 MB of the rise)", live[0].Plain[0]);

        w.Exit(100);
        exited = true;
        file = 10 * Settings.MB;
        for (int i = 0; i < 1080; i++) Step();
        mgr.Note(inc!, w.Now - w.Interval, "a note added three hours on");
        Step();
        string md = File.ReadAllText(Path.Combine(inc!.Dir, "report.md"));
        Assert.Contains("a note added three hours on", md); // written again
        Assert.Equal((grew, grew), PlainAnswer(inc)); // a growth with a release recorded on it later still says its growth
        Assert.Contains("leaker.exe (PID 100, started ", md); // its Release section: the process had exited
        Assert.Contains(", exited by ", md);
        Assert.Equal(line, Line(inc, "- **leaker**"));
        Assert.DoesNotContain(" more File handles", md);
    }

    [Fact]
    public void AnIncidentForTheWholeNonPagedPoolSaysAndRisingOnlyWhileItIsOpen()
    {
        // The non-paged pool stands at 5,100 MB, under its limit of 5.0 GB (the default: 5,120 MB), and then climbs
        // 20 MB a sample with File. The detector's own rule fires (no rule of a tag's own is set here) once the pool
        // is over the limit and has risen 100 MB in 10 minutes. Its alert's bytes are the fastest-growing tag's rise
        // in those 10 minutes, and it carries the limit it is about. While the incident is open its headline names
        // that limit, "Non-paged pool over 5.0 GB and rising", in its report, in the window's details (from memory)
        // and in Copy summary. It closes after its quiet half minute, and from then on the headline says what the
        // pool did, "rose over 5.0 GB": at the close, and three hours on, when the pool has fallen to 4,000 MB and a
        // note has the report written again. (Before, the figure was the alert's bytes, the tag's rise: "Non-paged
        // pool over 100 MB and rising" of a pool at 5.08 GB, and more as the rule fired with a bigger rise; and a
        // closed incident's headline went on saying "and rising".)
        Directory.CreateDirectory(_dir);
        var settings = new Settings { IncidentQuietMinutes = 0.5, GrowthRules = [] };
        var w = new World { Interval = TimeSpan.FromSeconds(10) };
        var mgr = new IncidentManager(settings, new TagKnowledge(null, null), new CountingHost(_dir), w.Registry) { RewriteInterval = TimeSpan.Zero, Budget = new WriteBudget(1L << 30, 1L << 30) };
        var det = new Detector(settings);
        w.AddProcess(4, 0, "System");
        w.AddProcess(100, 4, "node.exe");
        long level = 100 * Settings.MB, pool = 5_100 * Settings.MB;
        var fired = new List<Alert>();
        void Step(long rise = 0)
        {
            w.Io(100, other: 300);
            level += rise;
            pool += rise;
            w.Totals = new PoolTotals(400_000_000, (ulong)pool, 350_000_000, (ulong)(pool - 50 * Settings.MB));
            w.Sample(("File", level));
            var alerts = det.Evaluate(w.History);
            fired.AddRange(alerts);
            mgr.Process(w.History, det, alerts, At(w.Now - w.Interval));
        }
        static string Head(Incident of) => File.ReadAllLines(Path.Combine(of.Dir, "report.md"))[0];
        static string JsonHead(string json)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("headline").GetString()!;
        }
        for (int i = 0; i < 70; i++) Step();
        Assert.Empty(fired); // 5,100 MB is under the limit, and still
        for (int i = 0; i < 40 && mgr.Open.Count == 0; i++) Step(20 * Settings.MB);
        var inc = Assert.Single(mgr.Open);
        Assert.Equal(IncidentKind.NonPagedTotal, inc.Kind);
        // The detector's alert: its bytes are File's rise in the 10 minutes, and it carries the limit the pool is over.
        var first = Assert.Single(fired);
        Assert.Equal((AlertKind.NonPagedTotal, FileTag, "non-paged pool 5.08 GB > 5.0 GB and rising (+100 MB in 10 min)", 100 * Settings.MB, (long?)(5L << 30)),
            (first.Kind, first.Tag, first.Rule, first.Bytes, first.LimitBytes));
        Assert.Equal(5L << 30, settings.NonPagedTotalAlertBytes);
        const string tag = "fastest-growing tag `File` (nt File objects (charged to the opening process)) grew ";
        const string open = "Non-paged pool over 5.0 GB and rising; " + tag;
        Assert.StartsWith("# " + open, Head(inc));
        Assert.StartsWith(open, JsonHead(File.ReadAllText(Path.Combine(inc.Dir, "report.json"))));
        string live = mgr.SummaryJson(w.History, inc);
        Assert.StartsWith(open, JsonHead(live));
        Assert.StartsWith(open, PoolWatch.Core.Util.CopyText.IncidentSummary(live));
        for (int i = 0; i < 12; i++) Step(20 * Settings.MB);
        Assert.Equal(340 * Settings.MB, inc.Alerts[0].Bytes); // the rule fired with a bigger rise since, and the headline does not follow it
        Assert.StartsWith("# " + open, Head(inc));
        for (int i = 0; i < 60 && mgr.Open.Count > 0; i++) Step();
        Assert.Same(inc, Assert.Single(mgr.Closed));
        var top = inc.Suspects[0];
        string closed = $"Non-paged pool rose over 5.0 GB; {tag}340 MB in 2 min; top suspect node.exe PID 100, started {Fmt.Clock(top.StartUtc)} ({PoolWatch.Core.Util.PlainWords.Confidence(top.Confidence)})";
        Assert.Equal("# " + closed, Head(inc));
        Assert.Equal(closed, JsonHead(File.ReadAllText(Path.Combine(inc.Dir, "report.json"))));
        // The growth ended where the pool stopped rising, half a minute before the close: the line beside the headline says so, in the past too.
        Assert.Equal($"- Growth: 100 MB -> 440 MB (+340 MB) from {Fmt.Clock(inc.OnsetUtc)} to {Fmt.Clock(inc.ClosedUtc!.Value.AddSeconds(-30))}; 440 MB at {Fmt.Clock(inc.ClosedUtc.Value)}", Line(inc, "- Growth: "));

        pool = 4_000 * Settings.MB;
        for (int i = 0; i < 1080; i++) Step();
        mgr.Note(inc, w.Now - w.Interval, "a note added three hours on");
        Step();
        string md = File.ReadAllText(Path.Combine(inc.Dir, "report.md"));
        Assert.Contains("a note added three hours on", md); // written again
        Assert.Equal("# " + closed, Head(inc));
        Assert.Equal(closed, JsonHead(File.ReadAllText(Path.Combine(inc.Dir, "report.json"))));
        Assert.Same(inc, Assert.Single(mgr.Closed));
        // "and rising" is left only where the alert's own line and the timeline quote a rule's text as it fired, with their times.
        var rising = md.Split('\n').Where(l => l.Contains("and rising", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(rising);
        Assert.All(rising, l => Assert.Contains(fired, a => l.Contains(a.Rule, StringComparison.Ordinal)));
        Assert.All(fired, a => Assert.Equal((long?)(5L << 30), a.LimitBytes));

        // An alert that carries no limit (the detector makes none) is said without a figure, never with its bytes.
        var bare = new Incident { Id = "bare", Dir = Path.Combine(_dir, "bare"), Tag = FileTag, Kind = IncidentKind.NonPagedTotal };
        bare.Alerts.Add(first with { LimitBytes = null });
        Assert.Equal(("its limit", "5.0 GB"), (IncidentWriter.NonPagedLimit(bare), IncidentWriter.NonPagedLimit(inc)));
    }

    private const string ReportHeld = "The process cannot access the file 'report.md' because it is being used by another process.";
    private const string TriesAgain = " (while PoolWatch holds the incident it tries again, also as it stops; this is said once until the folder is written)";

    [Fact]
    public void AClosedIncidentsFolderThatCouldNotBeWrittenIsTriedAgainAMinuteOnAndSaidOnce()
    {
        // The owner's trace, and 150 late events of its window at once: 100 rows with the report at that sample. From
        // then on another program holds report.md, for five minutes. The write of the rows that waited, 30 s on, puts
        // the last 50 in events.csv and fails at the report: the incident's report.md is behind its events.csv, and
        // nothing of the incident changes after that (no event, no alert, no note). PoolWatch writes the whole folder
        // again a minute after the write failed, and a minute after each try that fails: four tries in those thirty
        // samples, not one at each. (The first try puts the file's rows in time order before it fails at the report,
        // so the write two minutes after the window's end finds nothing to do.) The log says once that the write
        // failed and what PoolWatch does about it.
        // When report.md is let go, the next try writes the folder, the report counts the file's rows, and the log
        // says once that the folder is written again; nothing more is written or said after that.
        // (Before, a closed incident's write that failed was not tried again until the incident's next change. With
        // none, its report.md went on saying "events.csv gets the rest when PoolWatch's write budget has room" of a
        // file that had every row, to the end of the run and after it.)
        var host = new HoldingHost(_dir);
        var (mgr, inc, inTrace, inFile, step, _) = OwnersTrace(new WriteBudget(1L << 30, 1L << 30), host);
        mgr.AddEvents(LateOf(inc, 150), At(inc.ClosedUtc!.Value.AddSeconds(26.3)));
        step(false);
        string rest = EventsLine(inc);
        Assert.StartsWith($"({inTrace + 150} other events in the window; events.csv gets the rest when PoolWatch's write budget has room", rest);
        Assert.Equal(TimeSpan.FromMinutes(1), mgr.FailedWriteRetry);

        host.Held = "report.md";
        var tried = new List<int>();
        for (int i = 0; i < 30; i++)
        {
            int before = host.Refused;
            step(false);
            if (host.Refused > before) tried.Add(i);
        }
        // 30 s after the folder's last write (the rows that waited), then a minute after each try that failed.
        Assert.Equal([2, 8, 14, 20, 26], tried);
        Assert.Equal(5, host.Refused);
        Assert.Equal(inTrace + 150, inFile().Count); // every row is in the file
        var times = Rows(inc).Where(r => r[1] == "Start").Select(r => Time(r[0])).ToList();
        Assert.Equal(times.Order(), times); // in time order: a try writes the whole folder, as far as it gets
        Assert.Equal(rest, EventsLine(inc)); // and the report beside it still says rows are to come
        Assert.True(inc.WriteFailed);
        Assert.Equal($"incident {inc.Id}: write failed: {ReportHeld}{TriesAgain}", Assert.Single(host.Logs, l => l.Contains("failed", StringComparison.Ordinal)));
        string again = $"incident {inc.Id}: its folder is written again";
        Assert.DoesNotContain(again, host.Logs);

        host.Held = null;
        int samples = 0;
        while (inc.WriteFailed && samples < 30) { step(false); samples++; }
        Assert.Equal(3, samples); // a minute after the last try that failed, 30 s before report.md was let go
        Assert.Equal($"({inTrace + 150} other events in events.csv)", EventsLine(inc));
        Assert.Single(host.Logs, l => l == again);
        long wrote = inc.LastWriteMs;
        int logged = host.Logs.Count;
        for (int i = 0; i < 30; i++) step(false);
        Assert.Equal((wrote, logged, 5), (inc.LastWriteMs, host.Logs.Count, host.Refused));
        Assert.Single(host.Logs, l => l.Contains("failed", StringComparison.Ordinal));
    }

    [Fact]
    public void AClosedIncidentsFolderThatCouldNotBeWrittenIsWrittenAtTheEndOfTheRun()
    {
        // The same, with report.md held until the first try at the whole folder has failed too: events.csv has every
        // row, in time order (nothing of it waits for a write any more), and report.md still says rows are to come.
        // report.md is let go and PoolWatch stops, before the next try is due: the end of the run writes the folder,
        // and the log says so. (Before, the end of a run wrote a closed incident only when something of it still
        // waited: this one, whose events.csv was settled, kept its stale report.md.)
        var host = new HoldingHost(_dir);
        var (mgr, inc, inTrace, inFile, step, finish) = OwnersTrace(new WriteBudget(1L << 30, 1L << 30), host);
        mgr.AddEvents(LateOf(inc, 150), At(inc.ClosedUtc!.Value.AddSeconds(26.3)));
        step(false);
        string rest = EventsLine(inc);
        host.Held = "report.md";
        for (int i = 0; i < 10; i++) step(false);
        Assert.Equal(2, host.Refused);
        var times = Rows(inc).Where(r => r[1] == "Start").Select(r => Time(r[0])).ToList();
        Assert.Equal(times.Order(), times);
        Assert.Equal(inTrace + 150, inFile().Count);
        Assert.Equal(rest, EventsLine(inc));
        Assert.True(inc.WriteFailed);

        host.Held = null;
        finish();
        Assert.Equal($"({inTrace + 150} other events in events.csv)", EventsLine(inc));
        Assert.False(inc.WriteFailed);
        Assert.Equal($"incident {inc.Id}: write failed: {ReportHeld}{TriesAgain}", Assert.Single(host.Logs, l => l.Contains("failed", StringComparison.Ordinal)));
        Assert.Single(host.Logs, l => l == $"incident {inc.Id}: its folder is written again");
    }

    [Fact]
    public void AFolderWriteThatFirstFailsAtTheEndOfARunSaysOnlyThatItFailed()
    {
        // A note is added to a closed incident as PoolWatch stops, while another program holds its report.md: the end
        // of the run writes its folder, and that write fails. Nothing tries it again (PoolWatch stops), so the log
        // says that the write failed and no more: not that PoolWatch tries again.
        var host = new HoldingHost(_dir);
        var (mgr, inc, _, _, step, finish) = OwnersTrace(new WriteBudget(1L << 30, 1L << 30), host);
        step(false);
        Assert.DoesNotContain(host.Logs, l => l.Contains("failed", StringComparison.Ordinal));
        mgr.Note(inc, inc.ClosedUtc!.Value.AddSeconds(40), "a note as PoolWatch stops");
        host.Held = "report.md";
        finish();
        Assert.Equal(1, host.Refused);
        Assert.Equal($"incident {inc.Id}: write failed: {ReportHeld}", Assert.Single(host.Logs, l => l.Contains("failed", StringComparison.Ordinal)));
        Assert.True(inc.WriteFailed);
    }

    [Fact]
    public void AFolderWriteThatFirstFailsAsItsIncidentLeavesMemorySaysOnlyThatItFailed()
    {
        // The owner's trace, and 50 late events of its window: their rows are added to its events.csv after rows of
        // later times, so the file is still to be written in time order (two minutes after the window's end, or as the
        // incident leaves memory). Before that, 21 more incidents close: more than the 20 PoolWatch holds, so the
        // trace's incident, the oldest, leaves memory at the next sample, and is written for the last time, while
        // another program holds its report.md. That write fails, and PoolWatch no longer holds the incident to try
        // again: the log says that the write failed and no more.
        var host = new HoldingHost(_dir);
        var (mgr, inc, inTrace, inFile, step, _, w) = OwnersTraceIn(new WriteBudget(1L << 30, 1L << 30), host);
        mgr.AddEvents(LateOf(inc, 50), At(inc.ClosedUtc!.Value.AddSeconds(26.3)));
        step(false);
        Assert.Equal(inTrace + 50, inFile().Count);
        var made = w.Now - w.Interval;
        for (int i = 0; i < 21; i++) mgr.CreateCapture(w.History, [PoolTag.Parse($"T{i:D3}")], made);
        Assert.Contains(inc, mgr.Closed);
        Assert.DoesNotContain(host.Logs, l => l.Contains("failed", StringComparison.Ordinal));
        (host.Held, host.In) = ("report.md", inc.Dir);
        step(false);
        Assert.DoesNotContain(inc, mgr.Closed);
        Assert.Equal(IncidentManager.MaxClosed, mgr.Closed.Count);
        Assert.Equal(1, host.Refused);
        Assert.Equal($"incident {inc.Id}: write failed: {ReportHeld}", Assert.Single(host.Logs, l => l.Contains("failed", StringComparison.Ordinal)));
    }

    [Fact]
    public void AClosedIncidentsWholeFolderIsStillWrittenAfterWritesOfItsReportAndRowsWereMadeMeanwhile()
    {
        // A trace's incident whose trace is attached while another program holds its timeline.csv: the attaching write,
        // a full one, cannot write that file, and writes the rest of the folder (the report has the trace). Two late
        // events of its window come, the earlier one last: each is written with the report as it comes. A minute
        // after the attaching write a try is refused timeline.csv again. It is for the chart files alone
        // (timeline.csv is all the attaching write could not write, and the late events are in the report and the
        // file), so it writes nothing else, and events.csv keeps its rows in the order they were added until the
        // write two minutes after the window's end, a write that must be made, puts them in time order. Neither
        // that write nor the two for the late events touches timeline.csv, and none is the whole folder: the
        // file that could not be written is still to be made, the log does not say the folder is written again,
        // and timeline.csv is as the incident's making left it. When timeline.csv is let go, the next try makes
        // it: timeline.csv holds the trace's samples, and the log says so, once.
        string dir = Path.Combine(_dir, "whole");
        var host = new HoldingHost(dir);
        var n = new NodeWorld(dir, host);
        n.Step(30);
        string asMade = "";
        var (inc, made) = n.Trace(rise: 0, node: false, (_, _) =>
        {
            asMade = File.ReadAllText(Path.Combine(n.Mgr.Closed.Single().Dir, "timeline.csv"));
            host.Held = "timeline.csv";
        });
        string Timeline() => File.ReadAllText(Path.Combine(inc.Dir, "timeline.csv"));
        List<int> InFile() => Rows(inc).Where(r => r[1] == "Exit").Select(r => int.Parse(r[2])).ToList();
        string again = $"incident {inc.Id}: its folder is written again";
        Assert.True(inc.WriteFailed);
        Assert.Equal(1, host.Refused);
        Assert.Equal($"incident {inc.Id}: write failed: The process cannot access the file 'timeline.csv' because it is being used by another process.{TriesAgain}",
            Assert.Single(host.Logs, l => l.Contains("failed", StringComparison.Ordinal)));
        Assert.Contains("## Confirmation trace", File.ReadAllText(Path.Combine(inc.Dir, "report.md"))); // the report was written, with the trace
        Assert.Equal(asMade, Timeline());

        LifecycleEvent Late(double beforeEnd, int pid) => new() { TimeUtc = inc.ClosedUtc!.Value.AddSeconds(-beforeEnd), Kind = LifecycleKind.Exit, Pid = pid, CreateTime = 1, Image = "late.exe" };
        n.Mgr.AddEvents([Late(5, 80_001)], At(n.Now.AddSeconds(10)));
        n.Step();
        n.Mgr.AddEvents([Late(20, 80_002)], At(n.Now.AddSeconds(10)));
        n.Step();
        Assert.Equal([80_001, 80_002], InFile()); // each with the report as it came: the earlier one after the later
        Assert.Equal([80_002, 80_001], ListedPids(inc));
        Assert.True(inc.WriteFailed);
        Assert.Equal(1, host.Refused); // neither write was of timeline.csv
        n.Step(4); // to the try a minute after the attaching write: refused timeline.csv, it wrote nothing else
        Assert.Equal(made.AddSeconds(150), n.Now);
        Assert.Equal(2, host.Refused);
        Assert.Equal([80_001, 80_002], InFile());
        n.Step(4); // to the time of the write two minutes after the window's end
        Assert.Equal(made.AddSeconds(190), n.Now);
        Assert.Equal([80_002, 80_001], InFile()); // in time order: that write put them so
        Assert.Equal(2, host.Refused);
        Assert.True(inc.WriteFailed);
        Assert.DoesNotContain(again, host.Logs);
        Assert.Equal(asMade, Timeline());

        host.Held = null;
        int samples = 0;
        while (inc.WriteFailed && samples < 30) { n.Step(); samples++; }
        Assert.Equal(2, samples); // a minute after the last try that failed
        Assert.NotEqual(asMade, Timeline());
        Assert.True(Timeline().Split('\n').Length > asMade.Split('\n').Length, "timeline.csv holds the trace's samples");
        Assert.Single(host.Logs, l => l == again);
        Assert.Single(host.Logs, l => l.Contains("failed", StringComparison.Ordinal));
        Assert.Equal(2, host.Refused);
    }

    [Fact]
    public void AClosedIncidentsWriteThatMustBeMadeIsStillMadeAfterAWriteThatCouldWaitWasMadeMeanwhile()
    {
        // The owner's trace, and 50 late events of its window: their rows are added to its events.csv after rows of
        // later times. Two minutes after the window's end the file is to be written whole, in time order, a write that
        // must be made. Another program holds events.csv then (rows can still be added to it; it cannot be replaced),
        // and the write fails. A note on the incident's timeline has its folder written at the next sample, the whole
        // folder, by a write that can wait: the report has the note, and events.csv is as it was, its late rows after
        // later ones (only a write that must be made replaces it). That write is not the one that failed either: the
        // log does not say the folder is written again. When events.csv is let go, the next try writes it in time
        // order, and the log says so, once.
        var host = new HoldingHost(_dir);
        var (mgr, inc, inTrace, inFile, step, _) = OwnersTrace(new WriteBudget(1L << 30, 1L << 30), host);
        mgr.AddEvents(LateOf(inc, 50), At(inc.ClosedUtc!.Value.AddSeconds(26.3)));
        step(false);
        Assert.Equal(inTrace + 50, inFile().Count);
        bool InTimeOrder()
        {
            var times = Rows(inc).Where(r => r[1] == "Start").Select(r => Time(r[0])).ToList();
            return times.SequenceEqual(times.Order());
        }
        Assert.False(InTimeOrder());
        string again = $"incident {inc.Id}: its folder is written again";

        host.Held = "events.csv";
        for (int i = 0; i < 10; i++) step(false); // to the write two minutes after the window's end
        Assert.Equal(1, host.Refused);
        Assert.True(inc.WriteFailed);
        Assert.Equal($"incident {inc.Id}: write failed: The process cannot access the file 'events.csv' because it is being used by another process.{TriesAgain}",
            Assert.Single(host.Logs, l => l.Contains("failed", StringComparison.Ordinal)));
        Assert.False(InTimeOrder());

        mgr.Note(inc, inc.ClosedUtc!.Value.AddSeconds(130), "a note after the write failed");
        step(false);
        Assert.Contains("a note after the write failed", File.ReadAllText(Path.Combine(inc.Dir, "report.json"))); // its folder was written: the note is on its timeline
        Assert.Equal(1, host.Refused);
        Assert.True(inc.WriteFailed);
        Assert.DoesNotContain(again, host.Logs);
        Assert.False(InTimeOrder());

        host.Held = null;
        int samples = 0;
        while (inc.WriteFailed && samples < 30) { step(false); samples++; }
        Assert.Equal(5, samples); // a minute after the try that failed
        Assert.True(InTimeOrder());
        Assert.Equal(inTrace + 50, inFile().Count);
        Assert.Single(host.Logs, l => l == again);
        Assert.Single(host.Logs, l => l.Contains("failed", StringComparison.Ordinal));
    }

    [Fact]
    public void AnOpenIncidentsWriteThatFailsIsLoggedAtEachTryAndMadeGoodByItsNextWriteAsBefore()
    {
        // A growth whose rule goes on firing, its report due at every sample (a rewrite interval of 10 s here), while
        // another program holds its report.md for three samples: each refresh fails, and the log says so each time,
        // with no word of trying again (an open incident's next change writes it: it has one at every alert). When
        // report.md is let go, the next refresh, a write of its report alone that can wait, makes it good: the
        // incident no longer counts as one whose write failed, and the log says nothing more. Only a closed incident's
        // failed write is said once, tried again a minute on, and made good only by a write of its whole folder that
        // must be made (or, when its timeline.csv or its processes.csv is all that is still to write, by a try for
        // the chart files alone).
        Directory.CreateDirectory(_dir);
        var host = new HoldingHost(_dir);
        var settings = new Settings { IncidentQuietMinutes = 600, IncidentMaxMinutes = 600 };
        var w = new World { Interval = TimeSpan.FromSeconds(10) };
        var mgr = new IncidentManager(settings, new TagKnowledge(null, null), host, w.Registry)
        {
            Budget = new WriteBudget(1L << 30, 1L << 30), RewriteInterval = TimeSpan.Zero, MaxRewriteInterval = TimeSpan.FromSeconds(10),
        };
        var det = new Detector(settings);
        w.AddProcess(4, 0, "System");
        long tag = 100 * Settings.MB;
        DateTime? onset = null;
        void Step(bool alert)
        {
            if (alert) tag += 4 * Settings.MB;
            w.Sample(("File", tag));
            var now = w.Now - w.Interval;
            onset ??= alert ? now.AddMinutes(-1) : null;
            Alert[] alerts = alert ? [new Alert(AlertKind.Growth, FileTag, now, "grew > 20 MB in 1 min", 24 * Settings.MB, TimeSpan.FromMinutes(1), onset!.Value) { Ms = At(now).Ms, OnsetMs = At(onset.Value).Ms }] : [];
            mgr.Process(w.History, det, alerts, At(now));
        }
        for (int i = 0; i < 24; i++) Step(alert: false);
        for (int i = 0; i < 3; i++) Step(alert: true);
        var inc = Assert.Single(mgr.Open);
        long wrote = inc.LastWriteMs;
        Assert.False(inc.WriteFailed);
        Assert.DoesNotContain(host.Logs, l => l.Contains("failed", StringComparison.Ordinal));

        host.Held = "report.md";
        for (int i = 0; i < 3; i++) Step(alert: true);
        Assert.Equal(3, host.Refused);
        Assert.Equal(Enumerable.Repeat($"incident {inc.Id}: write failed: {ReportHeld}", 3), host.Logs.Where(l => l.Contains("failed", StringComparison.Ordinal)));
        Assert.True(inc.WriteFailed);
        Assert.Equal(wrote, inc.LastWriteMs);
        Assert.Equal((1, 1), (mgr.BehindNow(w.History).Reports, mgr.BehindNow(w.History).Failed));

        host.Held = null;
        Step(alert: true);
        Assert.True(inc.IsOpen);
        Assert.True(inc.LastWriteMs > wrote, "its report is written");
        Assert.False(inc.WriteFailed);
        Assert.Equal(0, mgr.BehindNow(w.History).Failed);
        Assert.Equal(3, host.Logs.Count(l => l.Contains("failed", StringComparison.Ordinal)));
        Assert.DoesNotContain(host.Logs, l => l.Contains("written again", StringComparison.Ordinal));
    }

    /// <summary>
    /// A PC with the app's own history (70 minutes of 10 s samples) on which leaker.exe opens 2,000 File handles a
    /// sample while File grows 8 MB with them (<see cref="Step"/> with grow), a growth of the 1-minute rule; when it
    /// stops, the incident closes after its quiet half minute. The real detector raises the alerts, and the folders
    /// are written through a host a test holds files with (<see cref="HoldingHost"/>).
    /// </summary>
    private sealed class LeakWorld
    {
        public readonly World W = new(History.ForInterval(10)) { Interval = TimeSpan.FromSeconds(10) };
        public readonly HoldingHost Host;
        public readonly IncidentManager Mgr;
        private readonly Detector _det;
        private long _file = 50 * Settings.MB;

        public LeakWorld(string dir)
        {
            Directory.CreateDirectory(dir);
            Host = new HoldingHost(dir);
            var settings = new Settings { GrowthRules = [new(1, 3 * Settings.MB)], IncidentQuietMinutes = 0.5, RetentionMinutes = 600 };
            Mgr = new IncidentManager(settings, new TagKnowledge(null, null), Host, W.Registry) { RewriteInterval = TimeSpan.Zero, Budget = new WriteBudget(1L << 30, 1L << 30) };
            _det = new Detector(settings);
            W.AddProcess(4, 0, "System");
            W.AddProcess(100, 4, "leaker.exe");
        }

        /// <summary>The newest sample's time.</summary>
        public DateTime Now => W.Now - W.Interval;

        /// <summary><paramref name="samples"/> more samples, File growing at each with <paramref name="grow"/>.</summary>
        public void Step(int samples = 1, bool grow = false)
        {
            for (int i = 0; i < samples; i++)
            {
                if (grow) { W.Io(100, other: 2000, handles: 2000); _file += 8 * Settings.MB; }
                W.Sample(("File", _file));
                W.ScanHandles();
                Mgr.Process(W.History, _det, _det.Evaluate(W.History), At(Now));
            }
        }

        /// <summary>Two minutes of growth after two and a half still ones: its incident, open.</summary>
        public Incident Grow()
        {
            Step(15);
            Step(12, grow: true);
            return Assert.Single(Mgr.Open);
        }

        /// <summary>Samples without growth until the open incident has closed: that incident.</summary>
        public Incident Close()
        {
            for (int i = 0; i < 30 && Mgr.Open.Count > 0; i++) Step();
            Assert.Empty(Mgr.Open);
            return Mgr.Closed[^1];
        }
    }

    /// <summary>The times of the rows of one of an incident's CSV files (each row's first cell), in the file's order.</summary>
    private static List<DateTime> RowTimes(Incident inc, string name) =>
        File.ReadAllLines(Path.Combine(inc.Dir, name)).Skip(1).Select(l => Time(PoolWatch.Core.Util.Csv.Split(l)[0])).ToList();

    private static string FolderWritten(Incident inc) => $"incident {inc.Id}: its folder is written again";

    /// <summary>
    /// What the log says of the try that ends a closed incident's failed write when it left one or more of its chart,
    /// timeline.csv and processes.csv as they are, though they were never written for its close
    /// (<paramref name="left"/>: "timeline.csv", or "chart, timeline.csv and processes.csv").
    /// </summary>
    private static string FolderWrittenBut(Incident inc, string left)
    {
        bool one = !left.Contains(' ');
        return $"incident {inc.Id}: its folder is written again, but for its {left}, which {(one ? "stays as it is" : "stay as they are")} " +
               $"(PoolWatch no longer holds the samples from the start of its window to make {(one ? "it" : "them")} from)";
    }

    [Theory]
    [InlineData("processes.csv", 80, false)]
    [InlineData("timeline.csv", 80, false)]
    [InlineData("", 80, false)] // the folder itself cannot be written
    [InlineData("report.md", 80, true)]
    [InlineData("timeline.csv", 3, true)]
    public void ATryMadeAfterTheHistoryMovedPastAClosedIncidentLeavesItsChartAndItsTwoCsvsAsTheyAre(string held, int minutes, bool whole)
    {
        // A growth's incident closes while another program holds one of its files (or its folder cannot be written at
        // all), and the hold lasts 80 minutes: longer than the 70 minutes of samples PoolWatch keeps. The closing
        // write, a write of the whole folder, writes every file it can (with timeline.csv or processes.csv held, all
        // but that one, so the report says "closed" from the start; with report.md held, all but the report; with
        // the folder refused, none), and PoolWatch tries again every minute. A try made while the history still
        // reaches back to the start of the incident's window makes the chart, timeline.csv and processes.csv from
        // the window's samples, as the closing write would have (the last case: held 3 minutes). One made after
        // that would make a file from fewer of the window's samples than the file holds, or from samples of after
        // the incident alone: it leaves the file as it is, and the log names each file left so that was never
        // written for the close, once; nothing tries it again, and a write for a note later leaves it too. So with
        // processes.csv held, timeline.csv keeps the window's rows the closing write gave it and processes.csv the
        // rows of the incident's last refresh while it was open; with timeline.csv held, the other way round; with
        // the folder refused, both keep the refresh's rows, and the chart is the refresh's too. With report.md held
        // the three were written at the close: the folder is whole once report.md is let go, and the log says so.
        // (Before, with processes.csv held, the try 71 minutes after the close replaced timeline.csv's 23 rows of
        // the window with one row of a sample from after the incident, and again every 5 minutes; with timeline.csv
        // held 100 minutes, both CSVs ended as one such row and report.md lost its chart line; and the log said
        // "its folder is written again".)
        var l = new LeakWorld(Path.Combine(_dir, (held.Length > 0 ? held : "folder") + minutes));
        var open = l.Grow();
        string Read(string name) => File.ReadAllText(Path.Combine(open.Dir, name));
        var refresh = RowTimes(open, "timeline.csv"); // as its last refresh with the chart and the CSVs left them, while it was open
        Assert.NotEmpty(refresh);
        if (held.Length > 0) l.Host.Held = held; else l.Host.Folder = open.Dir;
        var inc = l.Close();
        Assert.Same(open, inc);
        Assert.True(inc.WriteFailed);
        Assert.Equal(1, l.Host.Refused);
        var (from, closed) = (inc.Window!.FromUtc, inc.ClosedUtc!.Value);
        var window = l.W.History.Times.Where(t => t >= from && t <= closed).ToList(); // the window's samples, to the close
        Assert.True(window.Count > refresh.Count && window[^1] == closed, "the window has samples after the last refresh");
        string timeline = Read("timeline.csv"), processes = Read("processes.csv");
        // What the closing write wrote: every file but the one it could not write (with the folder refused, none),
        // the report among them when it was timeline.csv or processes.csv that could not be written.
        Assert.Equal(held is "timeline.csv" or "" ? refresh : window, RowTimes(inc, "timeline.csv"));
        Assert.Equal(held is "processes.csv" or "" ? refresh : window, RowTimes(inc, "processes.csv").Distinct().ToList());
        Assert.Equal(held is "timeline.csv" or "processes.csv", Read("report.md").Contains(", closed ", StringComparison.Ordinal));
        string reason = held.Length > 0 ? $"The process cannot access the file '{held}' because it is being used by another process." : $"Access to the path '{Path.GetFileName(inc.Dir)}' is denied.";
        string failed = $"incident {inc.Id}: write failed: {reason}{TriesAgain}";
        // The file the log names as left: the one that was held, or, with the folder refused, all three.
        string but = FolderWrittenBut(inc, held.Length > 0 ? held : "chart, timeline.csv and processes.csv");

        l.Step(minutes * 6);
        bool reaches = l.W.History.Ms[0] <= inc.Window.FromMs;
        Assert.Equal(minutes < 70, reaches); // after 80 minutes the history no longer holds the window's first samples
        // While held: a try a minute. With timeline.csv or processes.csv held, the first try that held fewer of the
        // window's samples than the file (none, by the second batch of samples the history let go) left the file,
        // and the tries ended there; with the folder refused, or report.md held, every try failed.
        bool leftWhileHeld = !whole && held.Length > 0;
        Assert.Equal(!leftWhileHeld, inc.WriteFailed);
        Assert.Equal(leftWhileHeld ? 1 : 0, l.Host.Logs.Count(x => x == but));
        if (!leftWhileHeld) Assert.Equal(1 + minutes, l.Host.Refused); // the closing write, and a try a minute since
        else Assert.InRange(l.Host.Refused, 60, minutes); // a try a minute until the history moved on (it lets samples go some minutes at a time)
        int refusedWhileHeld = l.Host.Refused;

        (l.Host.Held, l.Host.Folder) = (null, null);
        l.Step(60);
        l.Mgr.Note(inc, l.Now, "a note added later");
        l.Step();
        string md = Read("report.md");
        Assert.Contains("a note added later", md); // written again, for the note
        Assert.Contains(", closed ", md);
        Assert.Contains("![timeline](timeline.png)", md);
        Assert.False(inc.WriteFailed);
        Assert.Equal(refusedWhileHeld, l.Host.Refused);
        Assert.Equal(failed, Assert.Single(l.Host.Logs, x => x.Contains("failed", StringComparison.Ordinal)));
        if (whole)
        {
            // The whole folder: the two CSVs hold the window's samples, and the log says the folder is written again.
            Assert.Equal(window, RowTimes(inc, "timeline.csv"));
            Assert.Equal(window, RowTimes(inc, "processes.csv").Distinct().ToList());
            Assert.Single(l.Host.Logs, x => x == FolderWritten(inc));
            Assert.DoesNotContain(l.Host.Logs, x => x.Contains("but for", StringComparison.Ordinal));
        }
        else
        {
            // The two CSVs as the closing write left them, no row of a sample from after the incident, and the log names the file that was left, once.
            Assert.Equal((timeline, processes), (Read("timeline.csv"), Read("processes.csv")));
            Assert.All(RowTimes(inc, "timeline.csv").Concat(RowTimes(inc, "processes.csv")), t => Assert.True(t <= closed, $"a row of {t:T}, after the close at {closed:T}"));
            Assert.Equal(but, Assert.Single(l.Host.Logs, x => x.Contains("but for", StringComparison.Ordinal)));
            Assert.DoesNotContain(FolderWritten(inc), l.Host.Logs);
        }
    }

    [Fact]
    public void AFileThatWasNeverWrittenIsNotMadeFromSamplesOfAfterTheIncident()
    {
        // Neither timeline.csv nor processes.csv can be written in the incident's folder from before the incident
        // opens until 80 minutes after it closes (a program that keeps them from being made, as a folder guard
        // does): the incident never has either file. Each refresh with the chart while it is open and its closing
        // write write the rest of the folder, so its report says "closed" from its close on; the tries after it are
        // for the two files alone, and are refused them.
        // By 80 minutes after the close the history holds no sample of the incident's window: a try made then would
        // give each file one row, of a sample from after the incident. It leaves them unmade, the tries end, and the
        // log names both. A write for a note later leaves them unmade too.
        var l = new LeakWorld(Path.Combine(_dir, "never"));
        (l.Host.Held, l.Host.AlsoHeld) = ("timeline.csv", "processes.csv");
        var open = l.Grow();
        bool Has(string name) => File.Exists(Path.Combine(open.Dir, name));
        Assert.False(Has("timeline.csv") || Has("processes.csv"));
        Assert.True(Has("timeline.png") && Has("report.md")); // the chart and the report were written all the same
        Assert.False(open.WriteFailed); // open: its report is not behind
        var inc = l.Close();
        Assert.Same(open, inc);
        Assert.True(inc.WriteFailed);
        Assert.Contains(", closed ", File.ReadAllText(Path.Combine(inc.Dir, "report.md")));
        string but = FolderWrittenBut(inc, "timeline.csv and processes.csv");

        l.Step(80 * 6);
        Assert.True(l.W.History.Times[0] > inc.ClosedUtc!.Value); // no sample of the incident's time is left
        Assert.False(inc.WriteFailed);
        Assert.Equal(but, Assert.Single(l.Host.Logs, x => x.Contains("written again", StringComparison.Ordinal)));
        Assert.False(Has("timeline.csv") || Has("processes.csv"));
        int refused = l.Host.Refused;

        (l.Host.Held, l.Host.AlsoHeld) = (null, null);
        l.Mgr.Note(inc, l.Now, "a note added later");
        l.Step();
        Assert.Contains("a note added later", File.ReadAllText(Path.Combine(inc.Dir, "report.md"))); // written again, for the note
        Assert.False(Has("timeline.csv") || Has("processes.csv")); // not made from samples of after the incident
        Assert.Equal(refused, l.Host.Refused);
        Assert.Equal(but, Assert.Single(l.Host.Logs, x => x.Contains("written again", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("timeline.csv", 3)] // held for 30 seconds
    [InlineData("timeline.csv", 40)] // and for nearly 7 minutes
    [InlineData("processes.csv", 3)]
    [InlineData("processes.csv", 40)]
    [InlineData("", 3)] // the folder itself cannot be written
    [InlineData("", 40)]
    public void AFileThatStopsBeforeALongIncidentsCloseIsReplacedByATryThatReachesTheClose(string held, int heldSamples)
    {
        // leaker.exe grows File for 80 minutes: longer than the 70 minutes of samples PoolWatch keeps, so the
        // incident's window, as it is measured at its close, starts at the oldest sample PoolWatch holds. Its
        // timeline.csv and processes.csv were last written with its chart 20 minutes before it closes (an open
        // incident's are written every half hour). It closes while another program holds one of the two, or while
        // its folder cannot be written at all, for 30 seconds or for nearly 7 minutes: the closing write writes
        // what it can of the folder (all but the held file; nothing of a folder that cannot be written), and the
        // tries follow a minute apart. PoolWatch lets its oldest samples go in batches of 32 (5 minutes 20
        // seconds), so by the try that can write the file the history may no longer hold the window's first
        // samples: after 30 seconds in one start of the four here (the four differ in how long PoolWatch ran before
        // the growth), after 7 minutes in all four. That try still holds far more of the window's samples than the
        // file does, and reaches the close: it replaces the file. The file's last row is the close, its rows are
        // every sample of the window from its first row on, never fewer of them than it held, and the log says the
        // folder is written again. A file written whole at the close stays as it is.
        // (Before, a try made once the history no longer held the window's first sample left the file for good: after
        // a hold of 30 seconds, timeline.csv and processes.csv stopped 20 minutes before the close.)
        string[] csvs = ["timeline.csv", "processes.csv"];
        bool NotWritten(string name) => held.Length == 0 || name == held; // the closing write could not write it
        int moved = 0;
        foreach (int phase in new[] { 0, 8, 16, 24 })
        {
            var l = new LeakWorld(Path.Combine(_dir, $"long-{(held.Length > 0 ? held : "folder")}-{heldSamples}-{phase}"));
            l.Step(15 + phase);
            l.Step(80 * 6, grow: true);
            var open = Assert.Single(l.Mgr.Open);
            var refresh = csvs.ToDictionary(name => name, name => RowTimes(open, name).Distinct().ToList()); // as the last refresh with the chart and the CSVs left them
            if (held.Length > 0) l.Host.Held = held; else l.Host.Folder = open.Dir;
            var inc = l.Close();
            Assert.Same(open, inc);
            Assert.True(inc.WriteFailed);
            var (from, closed) = (inc.Window!.FromUtc, inc.ClosedUtc!.Value);
            Assert.Equal((l.W.History.Times[0], l.W.History.Ms[0]), (from, inc.Window.FromMs!.Value)); // its window starts at the oldest sample PoolWatch holds
            var window = l.W.History.Times.Where(t => t >= from && t <= closed).ToList();
            var had = new Dictionary<string, int>(); // of the window's samples, how many each file that could not be written holds
            foreach (string name in csvs.Where(NotWritten))
            {
                Assert.Equal(refresh[name], RowTimes(inc, name).Distinct().ToList()); // it could not be written
                Assert.True(refresh[name][^1] <= closed.AddMinutes(-15), $"{name} stops at {refresh[name][^1]:T}, well before the close at {closed:T}");
                had[name] = refresh[name].Count(t => t >= from);
                Assert.InRange(had[name], 1, window.Count - 90);
            }
            Assert.Equal(held.Length > 0, File.ReadAllText(Path.Combine(inc.Dir, "report.md")).Contains(", closed ", StringComparison.Ordinal)); // the report is written beside a held CSV

            l.Step(heldSamples);
            Assert.True(inc.WriteFailed);
            foreach (string name in csvs.Where(NotWritten)) Assert.Equal(refresh[name], RowTimes(inc, name).Distinct().ToList());
            (l.Host.Held, l.Host.Folder) = (null, null);
            int samples = 0;
            while (inc.WriteFailed && samples < 12) { l.Step(); samples++; }
            Assert.False(inc.WriteFailed);
            bool movedOn = l.W.History.Ms[0] > inc.Window.FromMs.Value; // the history no longer holds the window's first samples at that try
            if (movedOn) moved++;
            foreach (string name in csvs)
            {
                var rows = RowTimes(inc, name).Distinct().ToList();
                Assert.Equal(closed, rows[^1]); // it reaches the close
                Assert.Equal(window.Where(t => t >= rows[0]).ToList(), rows); // every sample of the window from its first row on, and none from before or after it
                // A file that could not be written at the close starts where the history did at that try; one written at the close is whole.
                Assert.Equal(NotWritten(name) && movedOn ? l.W.History.Times[0] : from, rows[0]);
                if (NotWritten(name)) Assert.True(rows.Count >= had[name], $"{name} holds {rows.Count} of the window's samples, where it had {had[name]}");
            }
            Assert.Contains($", closed {Fmt.Stamp(closed)}", File.ReadAllText(Path.Combine(inc.Dir, "report.md")));
            Assert.Single(l.Host.Logs, x => x == FolderWritten(inc));
            Assert.DoesNotContain(l.Host.Logs, x => x.Contains("but for", StringComparison.Ordinal));
            string reason = held.Length > 0 ? $"The process cannot access the file '{held}' because it is being used by another process." : $"Access to the path '{Path.GetFileName(inc.Dir)}' is denied.";
            Assert.Equal($"incident {inc.Id}: write failed: {reason}{TriesAgain}",
                Assert.Single(l.Host.Logs, x => x.Contains("failed", StringComparison.Ordinal)));
            int logged = l.Host.Logs.Count;
            long wrote = inc.LastWriteMs;
            l.Step(12);
            Assert.Equal((logged, wrote), (l.Host.Logs.Count, inc.LastWriteMs)); // nothing more is written or said
        }
        Assert.Equal(heldSamples >= 40 ? 4 : 1, moved); // in how many of the four starts the history had moved on by that try
    }

    [Fact]
    public void AFileLeftAtOneCloseIsNotSaidLeftAtALaterCloseWhoseFolderIsWritten()
    {
        // The 80 minutes of growth again, its incident closing while another program holds its timeline.csv, this
        // time for half an hour. A try a minute fails at the file while a try would still write at least as many of
        // the window's samples as the file holds: the file lacks the window's last 20 minutes (120 of its 434
        // samples), and the history lets go of the window's first samples 32 at a time, the fourth time between 19
        // and 20 minutes after the close (128 by then). The try 20 minutes after the close would write 306 of the
        // window's samples where the file holds 314: it leaves the file, which ends the tries, and the log says
        // that the folder is written again but for its timeline.csv. processes.csv and the
        // report were written at the close. Then the file is let go, File grows again, and the incident re-opens
        // and closes a second time, with its report.md held now: that closing write writes the chart and both CSVs
        // for the second close and fails at the report. When report.md is let go, the next try writes the folder,
        // and the log says that the folder is written again: no file of it is left, and the log does not say again
        // that timeline.csv stays as it is. (What a write left is said of that write alone: kept from an earlier
        // one, the log said of a timeline.csv written for the second close that it stayed as it was.)
        // The line as the manager writes it for one file, two and three.
        Assert.Equal("incident x: its folder is written again, but for its timeline.csv, which stays as it is (PoolWatch no longer holds the samples from the start of " +
                     "its window to make it from)", IncidentManager.FolderWrittenBut("x", ["timeline.csv"]));
        Assert.Equal("incident x: its folder is written again, but for its chart and processes.csv, which stay as they are (PoolWatch no longer holds the samples from " +
                     "the start of its window to make them from)", IncidentManager.FolderWrittenBut("x", ["chart", "processes.csv"]));
        Assert.Equal("incident x: its folder is written again, but for its chart, timeline.csv and processes.csv, which stay as they are (PoolWatch no longer holds the " +
                     "samples from the start of its window to make them from)", IncidentManager.FolderWrittenBut("x", ["chart", "timeline.csv", "processes.csv"]));
        var l = new LeakWorld(Path.Combine(_dir, "left-then-written"));
        l.Step(15);
        l.Step(80 * 6, grow: true);
        var open = Assert.Single(l.Mgr.Open);
        string Read(string name) => File.ReadAllText(Path.Combine(open.Dir, name));
        string timeline = Read("timeline.csv");
        l.Host.Held = "timeline.csv";
        var inc = l.Close();
        Assert.Same(open, inc);
        var closed = inc.ClosedUtc!.Value;
        Assert.True(inc.WriteFailed);
        string but = FolderWrittenBut(inc, "timeline.csv");
        var (from, kept) = (inc.Window!.FromUtc, RowTimes(inc, "timeline.csv"));
        Assert.Equal((434, 314), (l.W.History.Times.Count(t => t >= from && t <= closed), kept.Count(t => t >= from))); // the window's samples, and those of them the file holds

        l.Step(19 * 6); // 19 minutes: a try a minute, each refused the file
        Assert.True(inc.WriteFailed);
        Assert.Equal(1 + 19, l.Host.Refused);
        Assert.DoesNotContain(l.Host.Logs, x => x.Contains("written again", StringComparison.Ordinal));
        Assert.Equal(434 - 96, l.W.History.Times.Count(t => t <= closed)); // what the history still holds of the window: more than the file
        l.Step(6); // the try 20 minutes after the close leaves the file
        Assert.Equal(434 - 128, l.W.History.Times.Count(t => t <= closed)); // fewer than the file now
        Assert.False(inc.WriteFailed); // the tries ended
        Assert.Equal(1 + 19, l.Host.Refused); // it did not try the file
        Assert.Equal(but, Assert.Single(l.Host.Logs, x => x.Contains("but for", StringComparison.Ordinal)));
        l.Step(60); // to half an hour after the close: nothing more is tried or said
        Assert.Equal(1 + 19, l.Host.Refused);
        Assert.Equal(but, Assert.Single(l.Host.Logs, x => x.Contains("written again", StringComparison.Ordinal)));
        Assert.DoesNotContain(FolderWritten(inc), l.Host.Logs);
        Assert.Equal(timeline, Read("timeline.csv")); // as it was: it stops 20 minutes before the close
        Assert.True(RowTimes(inc, "timeline.csv")[^1] <= closed.AddMinutes(-15));
        Assert.Equal(closed, RowTimes(inc, "processes.csv")[^1]); // written at the close
        Assert.Contains(", closed ", Read("report.md"));
        int refused = l.Host.Refused;

        l.Host.Held = null;
        for (int i = 0; i < 12 && !inc.IsOpen; i++) l.Step(grow: true);
        Assert.True(inc.IsOpen);
        Assert.Equal(1, inc.Reopened);
        l.Step(12, grow: true);
        l.Host.Held = "report.md";
        Assert.Same(inc, l.Close());
        var closedAgain = inc.ClosedUtc!.Value;
        Assert.True(closedAgain > closed.AddMinutes(30));
        Assert.True(inc.WriteFailed);
        Assert.Equal(refused + 1, l.Host.Refused);
        Assert.Equal((closedAgain, closedAgain), (RowTimes(inc, "timeline.csv")[^1], RowTimes(inc, "processes.csv")[^1])); // both written for the second close
        Assert.Contains(", still open)", Read("report.md")); // the report could not be

        l.Host.Held = null;
        int samples = 0;
        while (inc.WriteFailed && samples < 12) { l.Step(); samples++; }
        Assert.False(inc.WriteFailed);
        Assert.Contains($", closed {Fmt.Stamp(closedAgain)}", Read("report.md"));
        Assert.Single(l.Host.Logs, x => x == FolderWritten(inc));
        Assert.Equal(but, Assert.Single(l.Host.Logs, x => x.Contains("but for", StringComparison.Ordinal))); // the one line, of the first close
        Assert.Equal(2, l.Host.Logs.Count(x => x.Contains("write failed", StringComparison.Ordinal))); // one for each close
    }

    [Theory]
    [InlineData(8, true)] // the file lacks the window's last 128 samples: four batches on, the history holds as many of the window's samples as the file
    [InlineData(7, false)] // it lacks 127: four batches on, the history holds one fewer than the file
    public void ATryThatHoldsAsManyOfTheWindowsSamplesAsTheFileReplacesItAndOneThatHoldsOneFewerLeavesIt(int more, bool asMany)
    {
        // The edge of the rule: a try that would hold fewer of the window's samples than the file leaves the file,
        // and one that holds as many does not. leaker.exe grows File for a little over 81 minutes, so that the
        // incident's timeline.csv, last written with its chart while it was open, lacks the window's last 128
        // samples when it closes, or its last 127. Another program holds the file from the close on, and a try a
        // minute is refused it. The history lets go of the window's first samples 32 at a time: after the fourth
        // time it holds 128 fewer of the window's samples than the window has, each of the two a different stretch
        // of the window (the file its start, the history its end). Where that is as many as the file holds, a try
        // still writes the file: it is let go then, and the next try gives it the window's last samples, to the
        // close, as many as it had, and the log says that the folder is written again. Where it is one fewer, the
        // try leaves the file, still held, without trying it: the tries end, the file is as it was, and the log
        // says that the folder is written again but for it.
        var l = new LeakWorld(Path.Combine(_dir, $"edge-{more}"));
        l.Step(15);
        l.Step(80 * 6 + more, grow: true);
        var open = Assert.Single(l.Mgr.Open);
        string Timeline() => File.ReadAllText(Path.Combine(open.Dir, "timeline.csv"));
        l.Host.Held = "timeline.csv";
        var inc = l.Close();
        Assert.Same(open, inc);
        Assert.True(inc.WriteFailed);
        string kept = Timeline();
        var (from, closed) = (inc.Window!.FromUtc, inc.ClosedUtc!.Value);
        var window = l.W.History.Times.Where(t => t >= from && t <= closed).ToList();
        int had = RowTimes(inc, "timeline.csv").Count(t => t >= from); // the window's samples the file holds
        Assert.Equal(120 + more, window.Count - had); // those it lacks: the window's last

        // A try a minute, each refused the file, while the history holds more of the window's samples than the file.
        int History() => l.W.History.Times.Count(t => t <= closed); // the window's samples the history holds
        int minutes = 0;
        while (History() > had && minutes < 30)
        {
            Assert.True(inc.WriteFailed);
            Assert.Equal(1 + minutes, l.Host.Refused);
            l.Step(6);
            minutes++;
        }
        Assert.Equal(window.Count - 128, History()); // four batches on
        Assert.Equal(asMany ? had : had - 1, History());
        Assert.True(l.W.History.Ms[0] > inc.Window.FromMs);
        Assert.DoesNotContain(FolderWritten(inc), l.Host.Logs);

        if (asMany) l.Host.Held = null; // let go: a try that holds as many still writes it
        int samples = 0;
        while (inc.WriteFailed && samples < 12) { l.Step(); samples++; }
        Assert.False(inc.WriteFailed);
        Assert.Equal(window.Count - 128, History()); // the history let no more go before that try
        if (asMany)
        {
            var rows = RowTimes(inc, "timeline.csv");
            Assert.Equal(window.Skip(128).ToList(), rows); // the window's last samples, to the close
            Assert.Equal((had, closed), (rows.Count, rows[^1]));
            Assert.Equal(FolderWritten(inc), Assert.Single(l.Host.Logs, x => x.Contains("written again", StringComparison.Ordinal)));
        }
        else
        {
            Assert.Equal(kept, Timeline()); // as it was
            Assert.Equal(FolderWrittenBut(inc, "timeline.csv"), Assert.Single(l.Host.Logs, x => x.Contains("written again", StringComparison.Ordinal)));
        }
        int refused = l.Host.Refused;
        l.Step(12);
        Assert.Equal(refused, l.Host.Refused); // nothing more is tried
        Assert.Single(l.Host.Logs, x => x.Contains("written again", StringComparison.Ordinal));
    }

    [Fact]
    public void AFolderThatNeverHadAChartIsNotSaidWrittenAgainButForItsChart()
    {
        // A growth's incident on a PC where its chart cannot be drawn (the host draws none), which is no failed
        // write: its folder never has a timeline.png, and its report shows none. It closes while another program
        // holds its report.md, for 80 minutes: the closing write writes timeline.csv and processes.csv for the
        // close and fails at the report, and a try a minute fails there too. By the time report.md is let go the
        // history holds no sample of the incident's window. The next try writes the report: the folder has every
        // file it can have, and the log says that it is written again. It does not say "but for its chart, which
        // stays as it is": the folder has no chart to stay. (A chart that is left is named only when the folder has
        // one: IncidentWriter.Leaves. Named whenever it was not drawn for the close, the log said just that of this
        // folder.)
        var l = new LeakWorld(Path.Combine(_dir, "no-chart"));
        l.Host.NoChart = true;
        var open = l.Grow();
        bool Has(string name) => File.Exists(Path.Combine(open.Dir, name));
        Assert.Equal((true, true, false, true), (Has("timeline.csv"), Has("processes.csv"), Has("timeline.png"), Has("report.md")));
        l.Host.Held = "report.md";
        var inc = l.Close();
        Assert.Same(open, inc);
        Assert.True(inc.WriteFailed);
        var closed = inc.ClosedUtc!.Value;
        Assert.Equal((closed, closed), (RowTimes(inc, "timeline.csv")[^1], RowTimes(inc, "processes.csv")[^1])); // both written for the close
        Assert.False(Has("timeline.png"));
        Assert.Contains(", still open)", File.ReadAllText(Path.Combine(inc.Dir, "report.md"))); // the report could not be

        l.Step(80 * 6);
        Assert.True(inc.WriteFailed);
        Assert.Equal(1 + 80, l.Host.Refused); // the closing write, and a try a minute since
        Assert.True(l.W.History.Times[0] > closed, "the history holds no sample of the incident's window");
        Assert.DoesNotContain(l.Host.Logs, x => x.Contains("written again", StringComparison.Ordinal));

        l.Host.Held = null;
        int samples = 0;
        while (inc.WriteFailed && samples < 12) { l.Step(); samples++; }
        Assert.False(inc.WriteFailed);
        Assert.False(Has("timeline.png"));
        string md = File.ReadAllText(Path.Combine(inc.Dir, "report.md"));
        Assert.Contains($", closed {Fmt.Stamp(closed)}", md);
        Assert.DoesNotContain("![timeline]", md);
        Assert.Equal((closed, closed), (RowTimes(inc, "timeline.csv")[^1], RowTimes(inc, "processes.csv")[^1])); // as the closing write left them
        Assert.Equal(FolderWritten(inc), Assert.Single(l.Host.Logs, x => x.Contains("written again", StringComparison.Ordinal)));
        Assert.Equal($"incident {inc.Id}: write failed: The process cannot access the file 'report.md' because it is being used by another process.{TriesAgain}",
            Assert.Single(l.Host.Logs, x => x.Contains("failed", StringComparison.Ordinal)));
    }

    [Fact]
    public void ATraceAttachedAfterTheCloseHasProcessesCsvMadeAgainWithItsRanking()
    {
        // Two programs open File handles while File grows, busy.exe more of them than leaker.exe: the incident
        // closes with busy.exe ranked first, and its processes.csv, written for that close, ranks them so in every
        // row. Its confirmation trace finishes after the close and shows leaker.exe allocating the growth:
        // attached, it ranks leaker.exe first. The history still holds every sample of the incident's window, so
        // the attaching write makes the chart and the two CSVs again, as every write of the whole folder does
        // until the history has moved on: processes.csv has the same samples, now with leaker.exe ranked first, as
        // the report beside it says. (Only once the history no longer holds the window's first sample is a file
        // that was written for the close left as that write made it: IncidentWriter.Leaves. Applied from the close
        // on, that rule left processes.csv ranking busy.exe first beside a report whose first suspect is
        // leaker.exe, confirmed by a trace.)
        var l = new LeakWorld(Path.Combine(_dir, "ranked-again"));
        l.W.AddProcess(200, 4, "busy.exe");
        l.Step(15);
        for (int i = 0; i < 12; i++)
        {
            l.W.Io(200, other: 6000, handles: 6000);
            l.Step(grow: true);
        }
        var opened = Assert.Single(l.Mgr.Open).OpenedUtc;
        var inc = l.Close();
        Assert.False(inc.WriteFailed);
        long closed = inc.ClosedMs!.Value;
        // Each process's rank in processes.csv (one in every row of it), and the file's samples.
        Dictionary<int, int> Ranks() => File.ReadAllLines(Path.Combine(inc.Dir, "processes.csv")).Skip(1).Select(PoolWatch.Core.Util.Csv.Split)
            .GroupBy(r => int.Parse(r[2])).ToDictionary(g => g.Key, g => Assert.Single(g.Select(r => int.Parse(r[1])).Distinct()));
        Assert.Equal([200, 100], inc.Suspects.Take(2).Select(s => s.Key.Pid));
        Assert.Equal((1, 2), (Ranks()[200], Ranks()[100]));
        var samples = RowTimes(inc, "processes.csv").Distinct().ToList();
        Assert.Equal(inc.ClosedUtc, samples[^1]);

        l.Step(2); // the trace is read and attached 20 seconds after the close
        var start = opened.AddSeconds(3.7);
        var trace = new CaptureResult
        {
            PoolEventsParsed = true, Tags = [FileTag],
            Pool = [new CapturePoolUsage(100, "leaker.exe", FileTag, 90 * Settings.MB, 0, 1000, 0, [], ParentPid: 4, FirstAllocUtc: start.AddSeconds(5))],
        }.Recorded(At(start), At(start.AddMinutes(1)));
        l.Mgr.ApplyCapture(l.W.History, inc, trace);
        Assert.Equal(closed, inc.ClosedMs); // the same close
        Assert.True(l.W.History.Ms[0] <= inc.Window!.FromMs, "the history holds the window's first sample");
        Assert.Equal([100, 200], inc.Suspects.Take(2).Select(s => s.Key.Pid));
        Assert.Equal(Confidence.ConfirmedByTrace, inc.Suspects[0].Confidence);
        Assert.Equal((1, 2), (Ranks()[100], Ranks()[200])); // made again, with the ranking the report gives
        Assert.Equal(samples, RowTimes(inc, "processes.csv").Distinct().ToList()); // from the same samples
        Assert.Contains("| 1 | leaker.exe | 100 | confirmed by a trace |", File.ReadAllText(Path.Combine(inc.Dir, "report.md")));
        Assert.DoesNotContain(l.Host.Logs, x => x.Contains("failed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("timeline.csv", null)]
    [InlineData("processes.csv", null)]
    [InlineData("timeline.csv", "processes.csv")]
    public void AClosedIncidentsReportIsWrittenWhileAnotherProgramHoldsItsTimelineCsvOrItsProcessesCsv(string held, string? also)
    {
        // A growth's incident closes while another program holds its timeline.csv, its processes.csv, or both. The
        // closing write cannot write the held file, and writes everything else: the other of the two, with the
        // window's samples to the close; events.csv, with the event of the sample the incident closed at (which only
        // the closing write can have given it); and report.json and report.md, which say that the incident closed
        // and when. The write counts as failed all the same: the log says so once, naming the first file that could
        // not be written, with what PoolWatch does about it. The rule's alert at the next sample (it fires on for
        // half a minute after the growth stops) has the folder written again: that write is refused the same files,
        // and writes the report with the alert's line. A try follows a minute after that, for the chart files
        // alone: it is refused the same files too, and leaves the report as it is. When the file is
        // let go, the next try writes it from the window's samples, and the log says that the folder is written
        // again.
        // (Before, the closing write stopped at the first file it could not write: for as long as the file was
        // held, report.md went on saying "still open" of a closed incident, report.json had no close, and
        // events.csv lacked the incident's last events.)
        var l = new LeakWorld(Path.Combine(_dir, $"report-{held}-{also ?? "alone"}"));
        var open = l.Grow();
        string Read(string name) => File.ReadAllText(Path.Combine(open.Dir, name));
        var refresh = RowTimes(open, "timeline.csv");
        Assert.Contains(", still open)", Read("report.md"));
        (l.Host.Held, l.Host.AlsoHeld) = (held, also);
        int files = also is null ? 1 : 2, pid = 60_000;
        while (l.Mgr.Open.Count > 0 && pid < 60_030)
        {
            // An event a second before each sample: the one before the sample the incident closes at reaches PoolWatch with that sample.
            l.Mgr.AddEvents([new LifecycleEvent { TimeUtc = l.W.Now.AddSeconds(-1), Kind = LifecycleKind.Exit, Pid = ++pid, CreateTime = 1, Image = "x.exe" }], At(l.W.Now));
            l.Step();
        }
        var inc = Assert.Single(l.Mgr.Closed);
        Assert.Same(open, inc);
        var (from, closed) = (inc.Window!.FromUtc, inc.ClosedUtc!.Value);
        var window = l.W.History.Times.Where(t => t >= from && t <= closed).ToList();
        void Written(bool all)
        {
            foreach (string name in new[] { "timeline.csv", "processes.csv" })
                Assert.Equal(!all && (name == held || name == also) ? refresh : window, RowTimes(inc, name).Distinct().ToList());
            string md = Read("report.md");
            Assert.Contains($"- Incident `{inc.Id}` (Growth), opened {Fmt.Stamp(inc.OpenedUtc)}, closed {Fmt.Stamp(closed)}", md);
            Assert.DoesNotContain("still open", md);
            Assert.Contains("![timeline](timeline.png)", md);
            using var json = System.Text.Json.JsonDocument.Parse(Read("report.json"));
            Assert.Equal(closed, json.RootElement.GetProperty("closedUtc").GetDateTime().ToUniversalTime());
            Assert.Contains(pid, Rows(inc).Where(r => r[1] == "Exit").Select(r => int.Parse(r[2])));
        }
        Written(all: false);
        Assert.True(inc.WriteFailed);
        Assert.Equal(files, l.Host.Refused);
        string failed = $"incident {inc.Id}: write failed: The process cannot access the file '{held}' because it is being used by another process.{TriesAgain}";
        Assert.Equal(failed, Assert.Single(l.Host.Logs, x => x.Contains("failed", StringComparison.Ordinal)));
        Assert.DoesNotContain(l.Host.Logs, x => x.Contains("written again", StringComparison.Ordinal));

        l.Step(); // the rule's alert after the close: a line on the timeline, and a write for it, refused the same files
        Assert.Equal(2 * files, l.Host.Refused);
        Assert.Contains("alert after it closed: ", Read("report.md")); // the report has the line: it was written
        string report = Read("report.md");
        l.Step(6); // a minute on: the next try, refused the same files
        Assert.Equal(3 * files, l.Host.Refused);
        Assert.True(inc.WriteFailed);
        Assert.Equal(report, Read("report.md")); // the try was for the chart files alone
        Written(all: false);
        Assert.DoesNotContain(l.Host.Logs, x => x.Contains("written again", StringComparison.Ordinal));

        (l.Host.Held, l.Host.AlsoHeld) = (null, null);
        int samples = 0;
        while (inc.WriteFailed && samples < 12) { l.Step(); samples++; }
        Assert.Equal(6, samples); // a minute after the last try that failed
        Written(all: true);
        Assert.Single(l.Host.Logs, x => x == FolderWritten(inc));
        Assert.Equal(failed, Assert.Single(l.Host.Logs, x => x.Contains("failed", StringComparison.Ordinal)));
        Assert.Equal(3 * files, l.Host.Refused);
    }

    [Fact]
    public void AnOpenIncidentsReportIsWrittenWhileAnotherProgramHoldsItsTimelineCsv()
    {
        // An incident that goes on growing, its chart and its two other CSVs due with a refresh every minute here
        // (every half hour in the app), while another program holds its timeline.csv for two minutes. Each refresh
        // with the chart cannot write that file, and the log says so each time, as of any write of an open incident
        // that fails. The rest of the refresh is written: report.md gives the growth as PoolWatch last measured it
        // for a refresh, and processes.csv has the samples up to that refresh. So the incident does not count as one
        // whose report is behind because its last write failed: only its timeline.csv is as it was. When the file
        // is let go, the next refresh writes it.
        // (Before, a refresh with the chart stopped at timeline.csv: while the file was held, the report was not
        // written at all once the chart was due, and the incident counted as one whose last write failed.)
        var l = new LeakWorld(Path.Combine(_dir, "open-report"));
        l.Mgr.OpenChartInterval = TimeSpan.FromMinutes(1);
        var inc = l.Grow();
        string Read(string name) => File.ReadAllText(Path.Combine(inc.Dir, name));
        string timeline = Read("timeline.csv"), growth = Line(inc, "- Growth: ");
        var heldAt = l.Now;
        long wrote = inc.LastWriteMs;
        l.Host.Held = "timeline.csv";
        int refreshes = 0;
        for (int i = 0; i < 12; i++)
        {
            long before = inc.LastWriteMs;
            l.Step(grow: true);
            if (inc.LastWriteMs != before) refreshes++;
        }
        Assert.True(inc.IsOpen);
        Assert.InRange(refreshes, 2, 12); // a refresh is due as the growth is a quarter more, or every 5 minutes
        // Every one of them tried the file, which is still due (a refresh that could not write it does not count as one that wrote the chart files), and said so.
        Assert.Equal(refreshes, l.Host.Refused);
        Assert.Equal(l.Host.Refused, l.Host.Logs.Count(x => x == $"incident {inc.Id}: write failed: The process cannot access the file 'timeline.csv' because it is being used by another process."));
        Assert.Equal(timeline, Read("timeline.csv")); // held: as it was
        // The report and processes.csv are of the last refresh, which was made while the file was held.
        Assert.True(inc.LastWriteMs > wrote);
        Assert.NotEqual(growth, Line(inc, "- Growth: "));
        Assert.Contains($" (+{Fmt.Bytes(inc.WrittenGrowth)}) ", Line(inc, "- Growth: "));
        Assert.True(inc.WrittenGrowth >= 96 * Settings.MB + 6 * 8 * Settings.MB, "the report has the growth of a refresh in the last minute");
        Assert.Equal(inc.LastWriteUtc, RowTimes(inc, "processes.csv")[^1]);
        Assert.True(inc.LastWriteUtc > heldAt.AddMinutes(1));
        Assert.False(inc.WriteFailed);
        Assert.Equal((0, 0), (l.Mgr.BehindNow(l.W.History).Reports, l.Mgr.BehindNow(l.W.History).Failed));

        l.Host.Held = null;
        int refused = l.Host.Refused;
        l.Step(12, grow: true);
        Assert.Equal(refused, l.Host.Refused);
        Assert.Equal(inc.LastWriteUtc, RowTimes(inc, "timeline.csv")[^1]); // written with the last refresh
        Assert.True(inc.LastWriteUtc > heldAt.AddMinutes(2), "a refresh since the file was let go");
        Assert.DoesNotContain(l.Host.Logs, x => x.Contains("written again", StringComparison.Ordinal)); // said only of a closed incident's folder
    }

    [Theory]
    [InlineData(2, "closes")]
    [InlineData(20, "closes")]
    [InlineData(2, "held through its close")]
    [InlineData(2, "refresh with the chart")]
    public void AReopenedIncidentsFolderIsSaidWrittenAgainOnlyOnceItsChartAndCsvsAreWritten(int quietSamples, string then)
    {
        // A growth's incident closes while another program holds its timeline.csv: the closing write fails, and the
        // log says so once, with what PoolWatch does about it. Before the file is let go (20 s on, or after three
        // tries have failed, 200 s on) File grows again and the incident re-opens. The re-opening writes its report
        // alone, and that write is made: the incident's report is current, and it no longer counts as one whose
        // write failed. Its timeline.csv is still held, and still as it was before the close: the log does not say
        // that the folder is written again, at the re-opening or at the refreshes of the report that follow. It says
        // so once, at the incident's next write with the chart and the CSVs once the file is let go: its close; or,
        // when the file is still held at that close, the try a minute or more after it; or, with a chart due every
        // minute here, the first refresh after the file is let go, while the incident is still open (the refreshes
        // with the chart that fail before that are logged each as an open incident's are).
        // (Before, the re-opening's write of the report alone logged "its folder is written again" while
        // timeline.csv was still held, and nothing tried that file again until the incident's next write with it.)
        var l = new LeakWorld(Path.Combine(_dir, $"reopen-{quietSamples}-{then.Replace(' ', '-')}"));
        bool refresh = then == "refresh with the chart";
        if (refresh) l.Mgr.OpenChartInterval = TimeSpan.FromMinutes(1);
        var open = l.Grow();
        l.Host.Held = "timeline.csv";
        var inc = l.Close();
        Assert.Same(open, inc);
        string held = "The process cannot access the file 'timeline.csv' because it is being used by another process.";
        string failed = $"incident {inc.Id}: write failed: {held}{TriesAgain}", again = FolderWritten(inc);
        Assert.Equal(failed, Assert.Single(l.Host.Logs, x => x.Contains("failed", StringComparison.Ordinal)));
        string timeline = File.ReadAllText(Path.Combine(inc.Dir, "timeline.csv"));
        l.Step(quietSamples);
        Assert.True(inc.WriteFailed);
        int tries = l.Host.Refused;
        // The closing write; a write for the rule's alert after the close (it fires on for half a minute, a new line on
        // the timeline), which fails at the same file; and a try a minute after each try that failed.
        Assert.Contains(inc.Timeline, t => t.Text.StartsWith("alert after it closed: ", StringComparison.Ordinal));
        Assert.Equal(quietSamples == 2 ? 2 : 5, tries);

        // File grows again: the incident re-opens, and its report is written.
        for (int i = 0; i < 12 && !inc.IsOpen; i++) l.Step(grow: true);
        Assert.True(inc.IsOpen);
        Assert.Equal(1, inc.Reopened);
        Assert.Contains(", still open)", File.ReadAllText(Path.Combine(inc.Dir, "report.md")));
        Assert.False(inc.WriteFailed); // its report is current
        Assert.Equal(0, l.Mgr.BehindNow(l.W.History).Failed);
        Assert.DoesNotContain(again, l.Host.Logs);
        l.Step(6, grow: true);
        Assert.DoesNotContain(again, l.Host.Logs);
        Assert.Equal(timeline, File.ReadAllText(Path.Combine(inc.Dir, "timeline.csv"))); // still held, and as it was
        if (refresh)
        {
            // A chart was due at each of these refreshes: each tried timeline.csv and failed, logged as an open incident's
            // write is. Each wrote the report all the same, so the incident's report is not behind, and its next
            // refresh comes when one is due (the growth a quarter more, or 5 minutes on), not at every sample.
            Assert.True(l.Host.Refused > tries);
            Assert.Equal(l.Host.Refused - tries, l.Host.Logs.Count(x => x == $"incident {inc.Id}: write failed: {held}"));
            Assert.False(inc.WriteFailed);
            Assert.Equal(0, l.Mgr.BehindNow(l.W.History).Failed);
            Assert.DoesNotContain(again, l.Host.Logs);
            l.Host.Held = null;
            int refused = l.Host.Refused;
            long wrote = inc.LastWriteMs;
            for (int i = 0; i < 30 && inc.LastWriteMs == wrote; i++) l.Step(grow: true); // to the next refresh
            Assert.True(inc.LastWriteMs > wrote);
            Assert.True(inc.IsOpen);
            Assert.Equal(refused, l.Host.Refused);
            Assert.Single(l.Host.Logs, x => x == again);
        }
        else
        {
            Assert.Equal(tries, l.Host.Refused); // nothing tried timeline.csv: the writes since were of the report alone
            if (then == "closes") l.Host.Held = null;
            var closed = l.Close();
            Assert.Same(inc, closed);
            if (then != "closes")
            {
                // Its closing write failed too, at the same file: the log has said so (once, until the folder is written).
                Assert.Equal(tries + 1, l.Host.Refused);
                Assert.True(inc.WriteFailed);
                Assert.DoesNotContain(again, l.Host.Logs);
                l.Host.Held = null;
                l.Step(6);
            }
            Assert.Single(l.Host.Logs, x => x == again);
            Assert.Equal(failed, Assert.Single(l.Host.Logs, x => x.Contains("failed", StringComparison.Ordinal)));
        }
        Assert.False(inc.WriteFailed);
        Assert.NotEqual(timeline, File.ReadAllText(Path.Combine(inc.Dir, "timeline.csv")));
        Assert.True(RowTimes(inc, "timeline.csv")[^1] >= l.Now.AddMinutes(-1), "timeline.csv holds the samples up to that write");
        int logged = l.Host.Logs.Count(x => x == again);
        l.Step(12);
        Assert.Equal(logged, l.Host.Logs.Count(x => x == again));
    }

    /// <summary>
    /// <see cref="LeakWorld"/>'s PC with a second tag: leaker.exe grows File while worker.exe grows Even, each when
    /// a test says so (<see cref="Step"/>), so one incident can close while the other stays open; and the report
    /// folders' write budget a test gives it.
    /// </summary>
    private sealed class TwoTagWorld
    {
        public readonly World W = new(History.ForInterval(10)) { Interval = TimeSpan.FromSeconds(10) };
        public readonly HoldingHost Host;
        public readonly IncidentManager Mgr;
        private readonly Detector _det;
        private long _file = 50 * Settings.MB, _even = 50 * Settings.MB;

        public TwoTagWorld(string dir, WriteBudget budget)
        {
            Directory.CreateDirectory(dir);
            Host = new HoldingHost(dir);
            var settings = new Settings { GrowthRules = [new(1, 3 * Settings.MB)], IncidentQuietMinutes = 0.5, RetentionMinutes = 600, IncidentMaxMinutes = 600 };
            Mgr = new IncidentManager(settings, new TagKnowledge(null, null), Host, W.Registry) { RewriteInterval = TimeSpan.Zero, Budget = budget };
            _det = new Detector(settings);
            W.AddProcess(4, 0, "System");
            W.AddProcess(100, 4, "leaker.exe");
            W.AddProcess(200, 4, "worker.exe");
        }

        /// <summary>The newest sample's time.</summary>
        public DateTime Now => W.Now - W.Interval;

        /// <summary><paramref name="samples"/> more samples, File growing at each with <paramref name="file"/> and Even with <paramref name="even"/>.</summary>
        public void Step(int samples = 1, bool file = false, bool even = false)
        {
            for (int i = 0; i < samples; i++)
            {
                if (file) { W.Io(100, other: 2000, handles: 2000); _file += 8 * Settings.MB; }
                if (even) { W.Io(200, other: 1500); _even += 4 * Settings.MB; }
                W.Sample(("File", _file), ("Even", _even));
                W.ScanHandles();
                Mgr.Process(W.History, _det, _det.Evaluate(W.History), At(Now));
            }
        }
    }

    [Theory]
    [InlineData("timeline.csv")]
    [InlineData("processes.csv")]
    public void ATryForAHeldTimelineCsvOrProcessesCsvWritesNoReportAndAnOpenIncidentIsRefreshedAsWithoutTheHold(string held)
    {
        // File and Even grow together; File stops, and its incident closes while another program holds its
        // timeline.csv (or its processes.csv), for half an hour and more. Even goes on growing: its incident stays
        // open beside the closed one, with a refresh of its report due as it grows a quarter more, or every 5
        // minutes. The closing write and the write for the rule's alert after the close write File's reports, which
        // say that it closed. From then on nothing of File's incident changes but an alert counted on its timeline
        // (written half an hour after the report's last write, by a write that can wait, as for any closed
        // incident), and a try a minute is refused the held file. Each try is for the chart, timeline.csv and
        // processes.csv alone: it writes nothing (the chart and the other CSV are as they were), so the tries spend
        // none of the report folders' write budget, report.md stays as the write before left it, and Even's
        // incident is refreshed at the very samples it is refreshed at on a PC where no program holds the file (the
        // same world, run again without the hold). The budget here has room for those refreshes throughout (none
        // is held back) and refills 3 KB a minute, where a report is about 11 KB. When the file is let go, the
        // next try makes it from the window's samples, the only bytes it writes, and the log says once that the
        // folder is written again.
        // (Before, each try wrote report.json and report.md again, whole, as a write that must be made: they give
        // PoolWatch's own cost as it is at each write, as this host does, so they differed each time. In this
        // world thirty tries wrote thirty reports, 337,260 B; the budget ran out of room, and the open incident got
        // 5 of the 10 refreshes it gets without the hold, none after the seventh minute of the half hour.)
        (TwoTagWorld T, Incident File, List<int> Refreshes, List<int> Counted, long Must, long HeldBack, int Tries, List<DateTime> Window) HalfAnHour(bool hold)
        {
            var t = new TwoTagWorld(Path.Combine(_dir, $"alone-{held}-{(hold ? "held" : "free")}"), new WriteBudget(1L << 30, 1L << 30));
            t.Mgr.OpenChartInterval = TimeSpan.FromHours(10); // an open incident's chart and CSVs once: what is due for Even in the half hour is its report
            t.Step(15);
            t.Step(12, file: true, even: true);
            var file = Assert.Single(t.Mgr.Open, i => i.Tag == FileTag);
            var even = Assert.Single(t.Mgr.Open, i => i.Tag != FileTag);
            string Md() => File.ReadAllText(Path.Combine(file.Dir, "report.md"));
            if (hold) (t.Host.Held, t.Host.In) = (held, file.Dir);
            for (int i = 0; i < 30 && file.IsOpen; i++) t.Step(even: true);
            Assert.False(file.IsOpen);
            Assert.True(even.IsOpen);
            var (from, closed) = (file.Window!.FromUtc, file.ClosedUtc!.Value);
            var window = t.W.History.Times.Where(x => x >= from && x <= closed).ToList();
            // A minute on: past the rule's alerts after the close, a new line on the timeline at the first (written at
            // once) and a repeat counted on it at the second (written within half an hour).
            t.Step(6, even: true);
            Assert.Equal(hold, file.WriteFailed);
            Assert.Equal(hold ? 2 : 0, t.Host.Refused); // the closing write, and the write for the alert's line
            Assert.True(file.CountPending);
            string md = Md();
            Assert.Contains($", closed {Fmt.Stamp(closed)}", md);
            Assert.Contains("alert after it closed: ", md);
            Assert.DoesNotContain(" more time", md.Split("alert after it closed: ")[1]);
            // From here, a budget with room for the open incident's refreshes and for little more: 3 KB a minute.
            t.Mgr.Budget = new WriteBudget(180_000, 120_000);
            var (refreshes, counted) = (new List<int>(), new List<int>());
            var (evenWrote, fileWrote) = (even.LastWriteMs, file.LastWriteMs);
            for (int i = 0; i < 30 * 6; i++)
            {
                t.Step(even: true);
                if (even.LastWriteMs != evenWrote) { refreshes.Add(i); evenWrote = even.LastWriteMs; }
                if (file.LastWriteMs != fileWrote) { counted.Add(i); fileWrote = file.LastWriteMs; }
                if (counted.Count == 0) Assert.Equal(md, Md()); // until the counted alert is written, no write touched the report
            }
            Assert.True(even.IsOpen);
            Assert.Equal(hold, file.WriteFailed);
            Assert.Contains($", closed {Fmt.Stamp(closed)}", Md());
            Assert.Contains(" more time", Md().Split("alert after it closed: ")[1]); // the alert counted after the close reached the report
            return (t, file, refreshes, counted, t.Mgr.Budget.SpentMust, t.Mgr.Budget.Held, t.Host.Refused - (hold ? 2 : 0), window);
        }
        var free = HalfAnHour(hold: false);
        var with = HalfAnHour(hold: true);
        // Without the hold: the open incident is refreshed every 5 minutes or sooner, no write waits for the budget,
        // and the closed incident's counted alert is written once, half an hour after its last write.
        Assert.InRange(free.Refreshes.Count, 6, 30);
        Assert.Equal((0L, 0L, 0), (free.Must, free.HeldBack, free.Tries));
        int countedAt = Assert.Single(free.Counted);
        // With it: a try a minute, each refused the file, as is the write for the counted alert (a write of the
        // whole folder, as every write of a closed incident that can wait is); none of the tries wrote a byte.
        Assert.Equal(30 + 1, with.Tries);
        Assert.Equal(0, with.Must);
        Assert.Equal(0, with.HeldBack);
        Assert.Equal(free.Refreshes, with.Refreshes); // the open incident, refreshed at the same samples
        // The counted alert is written as without the hold, by a write that can wait: at the same sample, or at the
        // next one when a try is due at that sample (a closed incident gets one write a sample).
        Assert.InRange(Assert.Single(with.Counted), countedAt, countedAt + 1);
        Assert.DoesNotContain(with.T.Host.Logs, x => x.Contains("written again", StringComparison.Ordinal));

        // The file is let go: the next try makes it, and writes nothing else.
        var (l, inc) = (with.T, with.File);
        string before = File.ReadAllText(Path.Combine(inc.Dir, "report.md"));
        long wrote = inc.LastWriteMs;
        (l.Host.Held, l.Host.In) = (null, null);
        int samples = 0;
        while (inc.WriteFailed && samples < 12) { l.Step(even: true); samples++; }
        Assert.InRange(samples, 1, 6); // a minute after the last try that failed
        Assert.Equal(with.Window, RowTimes(inc, held).Distinct().ToList()); // the window's samples, to the close
        Assert.Equal(new FileInfo(Path.Combine(inc.Dir, held)).Length, l.Mgr.Budget.SpentMust); // the file, and no report
        Assert.Equal((before, wrote), (File.ReadAllText(Path.Combine(inc.Dir, "report.md")), inc.LastWriteMs));
        Assert.Single(l.Host.Logs, x => x == FolderWritten(inc));
        Assert.Equal($"incident {inc.Id}: write failed: The process cannot access the file '{held}' because it is being used by another process.{TriesAgain}",
            Assert.Single(l.Host.Logs, x => x.Contains("failed", StringComparison.Ordinal)));
        Assert.Equal(2 + 30 + 1, l.Host.Refused);
    }

    private const string AttachedAll = "**Attached evidence**: report.md, report.json, timeline.csv (tag and pool totals), processes.csv (suspects' counters), events.csv (process lifecycle), timeline.png.";
    private const string WatchWithTimeline = "3. Watch kernel pool tag `File` (poolmon, or the attached timeline.csv).";

    [Theory]
    [InlineData("chart")]
    [InlineData("timeline.csv")]
    [InlineData("processes.csv")]
    [InlineData("the two CSVs")]
    public void ATryThatMakesAFileTheReportDoesNotShowWritesTheReportAgain(string made)
    {
        // A growth's incident closes while another program holds its timeline.csv or its processes.csv, in a folder
        // that lacks a file its report could show. Either its chart could not be drawn so far (the host draws
        // none): the folder has no timeline.png, report.md has no chart line, and its draft bug report does not
        // list timeline.png as attached. Or timeline.csv, processes.csv or both could never be made (another
        // program has kept the file from being made since before the incident opened): the draft lists only the
        // one the folder holds, or neither, and its third step names "the attached timeline.csv" only where the
        // folder holds that file. The closing write and the write for the rule's alert after the close write the
        // report so. A minute on, a try for the chart files alone is refused the held file (or the two): it makes
        // nothing, and the report stays as it was, byte for byte. Then the files are let go (and the chart can be
        // drawn): the next try makes what the folder lacked, so the report no longer shows the folder as it is,
        // and the try writes the report again: it has the chart line, lists every file the folder holds, and says
        // that the incident closed. The log says once that the folder is written again.
        // (A try for the chart files alone that never wrote the report would leave a folder with a chart its
        // report does not show, and a draft that does not list the CSVs beside it.)
        bool chart = made == "chart";
        // What the folder holds until the files are let go.
        bool timeline = made is "chart" or "processes.csv", processes = made is "chart" or "timeline.csv";
        var l = new LeakWorld(Path.Combine(_dir, "shown-" + made.Replace(' ', '-')));
        if (chart) l.Host.NoChart = true;
        else (l.Host.Held, l.Host.AlsoHeld) = (timeline ? "processes.csv" : "timeline.csv", timeline || processes ? null : "processes.csv");
        int held = chart || timeline || processes ? 1 : 2;
        var open = l.Grow();
        if (chart) l.Host.Held = "timeline.csv";
        var inc = l.Close();
        Assert.Same(open, inc);
        Assert.True(inc.WriteFailed);
        l.Step(); // the rule's alert after the close: a line on the timeline, and a write for it
        string Md() => File.ReadAllText(Path.Combine(inc.Dir, "report.md"));
        bool Has(string name) => File.Exists(Path.Combine(inc.Dir, name));
        Assert.Equal((!chart, timeline, processes, true), (Has("timeline.png"), Has("timeline.csv"), Has("processes.csv"), Has("events.csv")));
        Assert.Equal(!chart, Md().Contains("![timeline](timeline.png)", StringComparison.Ordinal));
        Assert.Equal("**Attached evidence**: report.md, report.json" + (timeline ? ", timeline.csv (tag and pool totals)" : "") + (processes ? ", processes.csv (suspects' counters)" : "") +
                     ", events.csv (process lifecycle)" + (chart ? "" : ", timeline.png") + ".", Line(inc, "**Attached evidence**: "));
        Assert.Equal(timeline ? WatchWithTimeline : "3. Watch kernel pool tag `File` (poolmon).", Line(inc, "3. Watch kernel pool tag "));
        Assert.Contains(", closed ", Md());

        string md = Md();
        int refused = l.Host.Refused;
        l.Step(6); // a minute on: the try
        Assert.Equal(refused + held, l.Host.Refused);
        Assert.True(inc.WriteFailed);
        Assert.Equal(md, Md());

        (l.Host.Held, l.Host.AlsoHeld, l.Host.NoChart) = (null, null, false);
        int samples = 0;
        while (inc.WriteFailed && samples < 12) { l.Step(); samples++; }
        Assert.Equal(6, samples); // a minute after the try that failed
        Assert.True(Has("timeline.png") && Has("timeline.csv") && Has("processes.csv"));
        Assert.Contains("![timeline](timeline.png)", Md());
        Assert.Equal(AttachedAll, Line(inc, "**Attached evidence**: "));
        Assert.Equal(WatchWithTimeline, Line(inc, "3. Watch kernel pool tag "));
        Assert.Contains($", closed {Fmt.Stamp(inc.ClosedUtc!.Value)}", Md());
        Assert.Single(l.Host.Logs, x => x == FolderWritten(inc));
        Assert.DoesNotContain(l.Host.Logs, x => x.Contains("but for", StringComparison.Ordinal));
    }

    [Fact]
    public void ADraftBugReportListsAsAttachedTheFilesItsFolderHoldsWhenTheReportIsWritten()
    {
        // A growth's incident opens: its folder gets its report and its events.csv at once, and its chart,
        // timeline.csv and processes.csv only with its first refresh with them. The draft bug report at the end of
        // report.md lists as attached evidence the files the folder holds as that report.md is written: at the
        // opening, report.md, report.json and events.csv, and its third step, which watches the tag, names no
        // "attached timeline.csv"; after the first refresh with the chart, all six, and the step names the file.
        // Then the chart can no longer be drawn (the host draws none), and the incident closes: the closing write
        // has no chart of the window to the close, so report.md has no chart line, and the folder still holds the
        // chart of the last refresh, which the draft goes on listing: the list is of the files the folder holds.
        // (Before, the draft listed timeline.csv and processes.csv from the opening on, and whenever another program
        // kept them from being made: ATryThatMakesAFileTheReportDoesNotShowWritesTheReportAgain has such folders.)
        var l = new LeakWorld(Path.Combine(_dir, "attached"));
        l.Step(15);
        for (int i = 0; i < 12 && l.Mgr.Open.Count == 0; i++) l.Step(grow: true);
        var inc = Assert.Single(l.Mgr.Open);
        bool Has(string name) => File.Exists(Path.Combine(inc.Dir, name));
        string Md() => File.ReadAllText(Path.Combine(inc.Dir, "report.md"));
        Assert.Equal((true, true, true, false, false, false), (Has("report.md"), Has("report.json"), Has("events.csv"), Has("timeline.csv"), Has("processes.csv"), Has("timeline.png")));
        Assert.Equal("**Attached evidence**: report.md, report.json, events.csv (process lifecycle).", Line(inc, "**Attached evidence**: "));
        Assert.Equal("3. Watch kernel pool tag `File` (poolmon).", Line(inc, "3. Watch kernel pool tag "));
        Assert.DoesNotContain("![timeline]", Md());

        for (int i = 0; i < 12 && !Has("timeline.csv"); i++) l.Step(grow: true);
        Assert.True(inc.IsOpen);
        Assert.True(Has("timeline.csv") && Has("processes.csv") && Has("timeline.png"));
        Assert.Equal(AttachedAll, Line(inc, "**Attached evidence**: "));
        Assert.Equal(WatchWithTimeline, Line(inc, "3. Watch kernel pool tag "));
        Assert.Contains("![timeline](timeline.png)", Md());

        l.Host.NoChart = true;
        Assert.Same(inc, l.Close());
        Assert.False(inc.WriteFailed); // a chart that cannot be drawn is no failed write
        Assert.Contains($", closed {Fmt.Stamp(inc.ClosedUtc!.Value)}", Md());
        Assert.DoesNotContain("![timeline]", Md());
        Assert.True(Has("timeline.png"));
        Assert.Equal(AttachedAll, Line(inc, "**Attached evidence**: "));
    }

    [Fact]
    public void ADraftBugReportDoesNotListAnEventsCsvItsFolderDoesNotHold()
    {
        // The writer alone, over a closed incident with 600 events in its window: more than its events.csv holds of
        // one end, so the file is to hold both ends, and only a write that must be made writes such a file. The
        // first write, one that must be made, makes it, and the draft bug report lists it as attached. Then the
        // file is gone from the folder (deleted by hand, say). A write that can wait leaves a file that is to hold
        // both ends to the next write that must be made, so as its report.md is written the folder holds no
        // events.csv: the draft does not list it, and the line under the window's events does not count events in a
        // file the folder does not hold: it says that the folder holds none. The next write that must be made makes
        // the file again, and the draft and that line give it again.
        Directory.CreateDirectory(_dir);
        var (h, add, t0) = Minutes();
        for (int i = 1; i <= 30; i++) add(i);
        DateTime T(int minute) => t0.AddMinutes(minute);
        var inc = new Incident
        {
            Id = "no-events", Dir = Path.Combine(_dir, "no-events"), Tag = FileTag, Kind = IncidentKind.Growth, OpenedUtc = T(12), OpenedMs = At(T(12)).Ms,
            OnsetUtc = T(11), OnsetMs = At(T(11)).Ms, LastRiseUtc = T(20), LastRiseMs = At(T(20)).Ms, ClosedUtc = T(21), ClosedMs = At(T(21)).Ms,
            Window = new(T(9), T(11), null, T(21)) { FromMs = At(T(9)).Ms, OnsetMs = At(T(11)).Ms, ToMs = At(T(21)).Ms },
        };
        var events = Enumerable.Range(0, 600).Select(k => T(10).AddSeconds(k))
            .Select((t, k) => new LifecycleEvent { TimeUtc = t, Ms = At(t).Ms, Kind = LifecycleKind.Start, Pid = 1000 + k, CreateTime = 1, Image = "x.exe" }).ToList();
        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        string path = Path.Combine(inc.Dir, "events.csv");
        string Md() => File.ReadAllText(Path.Combine(inc.Dir, "report.md"));
        const string NoFile = "(events.csv is not in the folder as this report is written.)";
        writer.Write(h, inc, IncidentEventSet.Of(events));
        Assert.Equal(600, Rows(inc).Count(r => r[1] == "Start"));
        Assert.Equal(AttachedAll, Line(inc, "**Attached evidence**: "));
        Assert.Contains(" other events in events.csv", Md());
        Assert.DoesNotContain(NoFile, Md());

        File.Delete(path);
        writer.Write(h, inc, IncidentEventSet.Of(events), must: false);
        Assert.False(File.Exists(path));
        Assert.Equal(AttachedAll.Replace(", events.csv (process lifecycle)", ""), Line(inc, "**Attached evidence**: "));
        Assert.DoesNotContain("in events.csv", Md());
        Assert.Contains(NoFile, Md());

        writer.Write(h, inc, IncidentEventSet.Of(events));
        Assert.Equal(600, Rows(inc).Count(r => r[1] == "Start"));
        Assert.Equal(AttachedAll, Line(inc, "**Attached evidence**: "));
        Assert.Contains(" other events in events.csv", Md());
        Assert.DoesNotContain(NoFile, Md());
    }

    [Fact]
    public void ALateEventThatComesJustBeforeATryForTheChartFilesAloneIsStillWrittenWithItsReport()
    {
        // A trace's incident whose trace is attached while another program holds its timeline.csv: the attaching
        // write writes the rest of the folder, and a try for the chart files alone is due a minute on. Ten seconds
        // before that try a late event of the trace's window comes. The try is refused the file and writes nothing
        // else: the event is not in events.csv or the report yet, and it still waits to be. At the next sample it
        // is written with the report, as a late event is without a failed write.
        // (A try that counted as a write of the report would have had the event's write, and the write for the
        // window's late events after it, count as made: the event would have stayed out of the file and the report.)
        string dir = Path.Combine(_dir, "late-try");
        var host = new HoldingHost(dir);
        var n = new NodeWorld(dir, host);
        n.Step(30);
        var (inc, made) = n.Trace(rise: 0, node: false, (_, _) => host.Held = "timeline.csv");
        List<int> InFile() => Rows(inc).Where(r => r[1] == "Exit").Select(r => int.Parse(r[2])).ToList();
        Assert.True(inc.WriteFailed);
        Assert.Equal(1, host.Refused);
        n.Step(5);
        Assert.Equal(made.AddSeconds(140), n.Now);
        Assert.Equal(1, host.Refused);

        n.Mgr.AddEvents([new LifecycleEvent { TimeUtc = inc.ClosedUtc!.Value.AddSeconds(-5), Kind = LifecycleKind.Exit, Pid = 80_001, CreateTime = 1, Image = "late.exe" }], At(n.Now.AddSeconds(10)));
        n.Step(); // a minute after the attaching write: the try
        Assert.Equal(2, host.Refused);
        Assert.Empty(InFile());
        Assert.DoesNotContain(80_001, ListedPids(inc));

        n.Step(); // the write for the late event
        Assert.Equal([80_001], InFile());
        Assert.Contains(80_001, ListedPids(inc));
        Assert.Equal(2, host.Refused);
        Assert.True(inc.WriteFailed); // timeline.csv is still to write
    }

    [Fact]
    public void ATryStillWritesTheWholeFolderWhenAWriteThatCouldWaitWroteAllButTimelineCsvAfterTheWholeFolderHadFailed()
    {
        // A growth's incident with 720 events in its window: while it is open its events.csv holds the first 500,
        // and a Note row says so; its closing write is to replace the file with both ends of the window, every one
        // of the 720. That closing write is refused the folder itself: nothing is written, and the whole folder is
        // still to be written by a write that must be made. At the next sample the folder can be written again,
        // while another program holds timeline.csv: the rule's alert after the close has the folder written by a
        // write that can wait, which writes the report (it says "closed") and is refused timeline.csv; as it can
        // wait, it leaves events.csv as it was, with its Note row. So more than timeline.csv is still to write: the
        // try a minute on is of the whole folder, and replaces events.csv with every event of the window, though
        // it is refused timeline.csv again. Only from then on is timeline.csv all that is left, and when it is let
        // go, the next try, for the chart files alone, makes it; the log says that the folder is written again.
        // (A try for the chart files alone after any write that could not write timeline.csv would have left
        // events.csv with 500 of its 720 events and a Note row that speaks of an open incident, and the log would
        // have said that the folder is written again.)
        var l = new LeakWorld(Path.Combine(_dir, "whole-owed"));
        l.Step(15);
        int pid = 70_000;
        for (int i = 0; i < 12; i++)
        {
            // 60 starts in the 9 s before each sample.
            l.Mgr.AddEvents(Enumerable.Range(0, 60).Select(k => new LifecycleEvent { TimeUtc = l.W.Now.AddSeconds(-9).AddMilliseconds(k * 100), Kind = LifecycleKind.Start, Pid = ++pid, CreateTime = 1, Image = "x.exe" }).ToList(), At(l.W.Now));
            l.Step(grow: true);
        }
        var open = Assert.Single(l.Mgr.Open);
        string Md() => File.ReadAllText(Path.Combine(open.Dir, "report.md"));
        (int Starts, int Notes) InFile() => (Rows(open).Count(r => r[1] == "Start"), Rows(open).Count(r => r[1] == "Note"));
        Assert.Equal((500, 1), InFile());
        l.Host.Folder = open.Dir;
        var inc = l.Close();
        Assert.Same(open, inc);
        Assert.True(inc.WriteFailed);
        Assert.Equal(1, l.Host.Refused);
        string denied = $"incident {inc.Id}: write failed: Access to the path '{Path.GetFileName(inc.Dir)}' is denied.{TriesAgain}";
        Assert.Equal(denied, Assert.Single(l.Host.Logs, x => x.Contains("failed", StringComparison.Ordinal)));
        Assert.Contains(", still open)", Md());
        var (from, closed) = (inc.Window!.FromUtc, inc.ClosedUtc!.Value);
        var window = l.W.History.Times.Where(t => t >= from && t <= closed).ToList();

        (l.Host.Folder, l.Host.Held) = (null, "timeline.csv");
        l.Step(); // the rule's alert after the close: a write that can wait
        Assert.Equal(2, l.Host.Refused);
        Assert.Contains("alert after it closed: ", Md());
        Assert.Contains($", closed {Fmt.Stamp(closed)}", Md());
        Assert.Equal((500, 1), InFile()); // as it was: only a write that must be made replaces it
        Assert.True(inc.WriteFailed);

        l.Step(6); // a minute on: the try, of the whole folder
        Assert.Equal(3, l.Host.Refused);
        Assert.Equal((720, 0), InFile()); // both ends of the window: every event of it
        Assert.True(inc.WriteFailed); // timeline.csv is still to write
        Assert.NotEqual(window, RowTimes(inc, "timeline.csv"));
        Assert.DoesNotContain(l.Host.Logs, x => x.Contains("written again", StringComparison.Ordinal));

        l.Host.Held = null;
        string md = Md();
        int samples = 0;
        while (inc.WriteFailed && samples < 12) { l.Step(); samples++; }
        Assert.Equal(6, samples); // a minute after the try that failed
        Assert.Equal(window, RowTimes(inc, "timeline.csv"));
        Assert.Equal(md, Md()); // that try was for the chart files alone
        Assert.Equal((720, 0), InFile());
        Assert.Single(l.Host.Logs, x => x == FolderWritten(inc));
        Assert.Equal(denied, Assert.Single(l.Host.Logs, x => x.Contains("failed", StringComparison.Ordinal)));
    }

    [Fact]
    public void ATryAfterAWriteThatFailedAtTheReportWritesTheWholeFolderThoughAnEarlierWriteFailedAtTimelineCsvAlone()
    {
        // A growth's incident closes while another program holds its timeline.csv: the closing write and the write
        // for the rule's alert after the close write the rest of the folder, and timeline.csv is all that is still
        // to write. Then a note is added to the incident's timeline while its report.md is held too: the write for
        // the note is refused timeline.csv and then report.md, and fails. That write is not tried again at the next
        // sample (no write that fails is), and the report is now behind the incident: the try a minute on is of
        // the whole folder. Both files are let go by then: it writes timeline.csv from the window's samples and the
        // report with the note, and the log says that the folder is written again.
        // (A try for the chart files alone, because the first write that failed had failed at timeline.csv alone,
        // would have made timeline.csv and said the same, with a report.md that never got the note.)
        var l = new LeakWorld(Path.Combine(_dir, "report-owed"));
        var open = l.Grow();
        l.Host.Held = "timeline.csv";
        var inc = l.Close();
        Assert.Same(open, inc);
        string Md() => File.ReadAllText(Path.Combine(inc.Dir, "report.md"));
        var (from, closed) = (inc.Window!.FromUtc, inc.ClosedUtc!.Value);
        var window = l.W.History.Times.Where(t => t >= from && t <= closed).ToList();
        l.Step(); // the rule's alert after the close
        Assert.Equal(2, l.Host.Refused);
        Assert.Contains("alert after it closed: ", Md());

        const string Note = "a note while report.md is held too";
        l.Mgr.Note(inc, l.Now, Note);
        l.Host.AlsoHeld = "report.md";
        l.Step();
        Assert.Equal(4, l.Host.Refused); // timeline.csv, then report.md
        Assert.DoesNotContain(Note, Md());
        Assert.True(inc.WriteFailed);
        (l.Host.Held, l.Host.AlsoHeld) = (null, null);
        l.Step(5);
        Assert.True(inc.WriteFailed); // not tried again until a minute after it failed
        Assert.DoesNotContain(Note, Md());
        Assert.NotEqual(window, RowTimes(inc, "timeline.csv"));

        l.Step(); // the try
        Assert.False(inc.WriteFailed);
        Assert.Contains(Note, Md());
        Assert.Equal(window, RowTimes(inc, "timeline.csv"));
        Assert.Equal(4, l.Host.Refused);
        Assert.Single(l.Host.Logs, x => x == FolderWritten(inc));
        Assert.Equal($"incident {inc.Id}: write failed: The process cannot access the file 'timeline.csv' because it is being used by another process.{TriesAgain}",
            Assert.Single(l.Host.Logs, x => x.Contains("failed", StringComparison.Ordinal)));
    }

    [Fact]
    public void ATryForAnIncidentThatChangedSinceItsReportWasWrittenWritesTheWholeFolder()
    {
        // A growth's incident closes while another program holds its timeline.csv, and the report is written
        // beside it. Then the report folders' write budget has no room, and a note is added to the incident's
        // timeline: the write for the note can wait, and is held back sample after sample. A minute after the last
        // write that was refused timeline.csv the try is due. The incident has changed since its report was
        // written, so the try is of the whole folder, a write that must be made, whatever the budget: the report
        // has the note, and timeline.csv is refused again. The try a minute after that finds nothing changed: it
        // is for the chart files alone, and leaves the report as it is.
        // (A try for the chart files alone whatever had changed would have left the note out of the report for as
        // long as the budget had no room.)
        var l = new LeakWorld(Path.Combine(_dir, "changed"));
        var open = l.Grow();
        l.Host.Held = "timeline.csv";
        var inc = l.Close();
        Assert.Same(open, inc);
        string Md() => File.ReadAllText(Path.Combine(inc.Dir, "report.md"));
        l.Step(); // the rule's alert after the close, written at once: the budget has room
        Assert.Equal(2, l.Host.Refused);
        Assert.False(inc.Dirty);

        var none = new WriteBudget(1, 1);
        none.Spend(2, At(l.Now).Ms);
        l.Mgr.Budget = none;
        const string Note = "a note while the budget has no room";
        l.Mgr.Note(inc, l.Now, Note);
        l.Step(5);
        Assert.False(none.HasRoom(At(l.Now).Ms));
        Assert.True(inc.Dirty); // its write waits for the budget
        Assert.Equal(2, l.Host.Refused);
        Assert.DoesNotContain(Note, Md());

        l.Step(); // a minute after the last write that was refused the file: the try
        Assert.Equal(3, l.Host.Refused);
        Assert.Contains(Note, Md());
        Assert.False(inc.Dirty);
        Assert.True(inc.WriteFailed);

        string md = Md();
        l.Step(6); // and the next one
        Assert.Equal(4, l.Host.Refused);
        Assert.Equal(md, Md());
        Assert.True(inc.WriteFailed);
    }

    [Theory]
    [InlineData(11, false)] // the window starts at minute 11 again: the place kept is of another window's start
    [InlineData(31, true)] // it still starts at minute 31: the place kept is its own
    public void TheKeptPlaceOfTheWindowsFirstSampleCountsOnlyForTheWindowItIsOf(int closedFrom, bool replaced)
    {
        // The writer alone, over a history of samples a minute apart that keeps 70 minutes. An open incident's
        // window starts at minute 11, and a full write makes its timeline.csv from the samples of minutes 11 to 60:
        // 50 rows. Then the window is measured to start at minute 31, and a refresh of the report alone (no chart
        // files) keeps the place of that window's first sample. The incident closes at minute 70, and its folder
        // is written only at minute 105 (another program held it until then), when the history starts at minute
        // 33: it holds 38 of the samples from minute 11 to the close, and no longer the first one of either window.
        // Where the window at the close starts at minute 31 still, the place kept is its own first sample's: of
        // the file's 50 rows, 30 are the window's (minutes 31 to 60), the try holds 38 (minutes 33 to 70), and the
        // file is replaced by one that reaches the close. Where the window starts at minute 11 again (the onset
        // was measured earlier at the close), the place kept is of another window's start, and does not count:
        // every sample the file was written from counts as the window's, 50 of them, which they are, and a try
        // that holds 38 leaves the file as it is, named as left. (Counted from the place kept whatever window it
        // was of, the file would have counted for 30 samples and been replaced by one of 38: fewer of the window's
        // samples than it held.)
        Directory.CreateDirectory(_dir);
        var (h, add, t0) = Minutes();
        DateTime T(int minute) => t0.AddMinutes(minute);
        CorrelationWindow From(int minute, int to) => new(T(minute), T(minute + 2), null, T(to)) { FromMs = At(T(minute)).Ms, OnsetMs = At(T(minute + 2)).Ms, ToMs = At(T(to)).Ms };
        var writer = new IncidentWriter(new TagKnowledge(null, null), new CountingHost(_dir), new ProcessRegistry());
        for (int i = 1; i <= 60; i++) add(i);
        var inc = new Incident
        {
            Id = $"kept-{closedFrom}", Dir = Path.Combine(_dir, $"kept-{closedFrom}"), Tag = FileTag, Kind = IncidentKind.Growth, OpenedUtc = T(14), OpenedMs = At(T(14)).Ms,
            OnsetUtc = T(13), OnsetMs = At(T(13)).Ms, LastRiseUtc = T(60), LastRiseMs = At(T(60)).Ms, Window = From(11, 60),
        };
        List<DateTime> Span(int first, int last) => Enumerable.Range(first, last - first + 1).Select(T).ToList();
        writer.Write(h, inc, []);
        Assert.Equal(Span(11, 60), RowTimes(inc, "timeline.csv"));
        Assert.True(File.Exists(Path.Combine(inc.Dir, "timeline.png")));

        inc.Window = From(31, 60);
        writer.Write(h, inc, IncidentEventSet.Of([]), full: false);
        Assert.Equal(Span(11, 60), RowTimes(inc, "timeline.csv")); // a write without the chart files

        for (int i = 61; i <= 105; i++) add(i);
        Assert.Equal(T(33), h.Times[0]); // the history let its first 32 samples go
        (inc.ClosedUtc, inc.ClosedMs, inc.Window) = (T(70), At(T(70)).Ms, From(closedFrom, 70));
        writer.Write(h, inc, []);
        Assert.Contains($", closed {Fmt.Stamp(T(70))}", File.ReadAllText(Path.Combine(inc.Dir, "report.md")));
        if (replaced)
        {
            Assert.Equal(Span(33, 70), RowTimes(inc, "timeline.csv")); // 38 of the window's samples where it held 30, and it reaches the close
            Assert.Empty(writer.ChartFilesLeft(inc));
        }
        else
        {
            Assert.Equal(Span(11, 60), RowTimes(inc, "timeline.csv")); // as it was: 50 of the window's samples, where the try held 38
            Assert.Equal(["chart", "timeline.csv", "processes.csv"], writer.ChartFilesLeft(inc));
        }
    }

    [Theory]
    [InlineData("report.md", 0)]
    [InlineData("timeline.csv", 0)]
    [InlineData("report.md", 150)]
    [InlineData("timeline.csv", 150)]
    public void AnOpenIncidentWhoseClosingWriteFailsAtTheEndOfARunIsTriedOnceAndLoggedOnce(string held, int events)
    {
        // An incident is still open as PoolWatch stops, and another program holds one of its files: the end of the
        // run closes it, and its closing write fails. That is the one try, and the log says once that the write
        // failed, with no word of trying again (PoolWatch stops). So too when rows of its events.csv still waited
        // for the write budget as it closed (150 events in its window, 100 of them in the file), which the closing
        // write is to write. With timeline.csv held, that one try wrote the rest of the folder: events.csv has every
        // row, and report.md says that the incident closed. (Before, the end of a run wrote every closed incident
        // whose write had failed once more, and so tried this one twice in the same call, and logged its failure
        // twice.)
        Directory.CreateDirectory(_dir);
        var host = new HoldingHost(_dir);
        var settings = new Settings { IncidentQuietMinutes = 600, IncidentMaxMinutes = 600 };
        var w = new World { Interval = TimeSpan.FromSeconds(10) };
        var mgr = new IncidentManager(settings, new TagKnowledge(null, null), host, w.Registry) { Budget = new WriteBudget(1L << 30, 1L << 30), RewriteInterval = TimeSpan.Zero };
        var det = new Detector(settings);
        w.AddProcess(4, 0, "System");
        long tag = 100 * Settings.MB;
        DateTime? onset = null;
        void Step(bool alert)
        {
            if (alert) tag += 4 * Settings.MB;
            w.Sample(("File", tag));
            var now = w.Now - w.Interval;
            onset ??= alert ? now.AddMinutes(-1) : null;
            Alert[] alerts = alert ? [new Alert(AlertKind.Growth, FileTag, now, "grew > 20 MB in 1 min", 24 * Settings.MB, TimeSpan.FromMinutes(1), onset!.Value) { Ms = At(now).Ms, OnsetMs = At(onset.Value).Ms }] : [];
            mgr.Process(w.History, det, alerts, At(now));
        }
        for (int i = 0; i < 24; i++) Step(alert: false);
        // The events of the 10 s before the incident opens: in its window, and more than one write of rows adds.
        mgr.AddEvents(Enumerable.Range(0, events).Select(k => new LifecycleEvent { TimeUtc = w.Now.AddSeconds(-9).AddMilliseconds(k * 50), Kind = LifecycleKind.Start, Pid = 70_000 + k, CreateTime = 1, Image = "x.exe" }).ToList(), At(w.Now));
        Step(alert: true);
        var inc = Assert.Single(mgr.Open);
        Assert.Equal(Math.Min(events, IncidentManager.EventRowsPerWrite), Rows(inc).Count(r => r[1] == "Start")); // the rest wait for the next sample
        Assert.DoesNotContain(host.Logs, x => x.Contains("failed", StringComparison.Ordinal));

        host.Held = held;
        mgr.Finish(w.History, w.Now - w.Interval);
        Assert.False(inc.IsOpen);
        Assert.True(inc.WriteFailed);
        Assert.Equal(1, host.Refused);
        Assert.Equal($"incident {inc.Id}: write failed: The process cannot access the file '{held}' because it is being used by another process.",
            Assert.Single(host.Logs, x => x.Contains("failed", StringComparison.Ordinal)));
        Assert.DoesNotContain(host.Logs, x => x.Contains("written again", StringComparison.Ordinal));
        // What that one try wrote: with timeline.csv held, every row and the report; with report.md held, the rows and not the report.
        Assert.Equal(events, Rows(inc).Count(r => r[1] == "Start"));
        Assert.Equal(held == "timeline.csv", File.ReadAllText(Path.Combine(inc.Dir, "report.md")).Contains(", closed ", StringComparison.Ordinal));
    }

    [Fact]
    public void ATracesReportSaysThatItsTraceHadNotFinishedUntilTheTraceIsAttached()
    {
        // "Capture now" makes its incident, closed, its start and its end the moment it is made, and writes its folder
        // before the trace has recorded anything: the incident has no span yet for its plain words to say what the
        // tag did over. Over a span of no length no process stands out by what it did in it, and a report with no
        // suspect has no plain words: so it is while the newest handle scan is of the newest sample or before it.
        // With a handle scan after the newest sample that shows node.exe with 50 more File handles than the scan
        // before, node.exe is a suspect as the incident is made, and the report has plain words. They do not say
        // what the tag did over no time at all ("... was flat over 0 s."): they say that its trace had not finished,
        // by the time of the newest sample at that write, in report.md and in report.json's plainAnswer. That is true
        // as the incident is made, while the trace records, while it is read, and whenever that report is read. The
        // note that the trace started has the report written again at the next sample, with that sample's time. The
        // write that attaches the trace ranks again and says what the tag did over the trace, in the past.
        // The report's title and its Growth line say the same until the trace is attached, with or without a
        // suspect: that the trace had not finished by that time, and the level the tag held at the sample the
        // incident was made at; so do report.json's headline, Copy summary and the window's details, which are
        // read from it. None says "grew 0 B in 0 s", "no suspect stood out" or "100 MB -> 100 MB (+0 B)", which
        // read as what a trace found. After the attach they are as before: the growth over the trace and the top
        // suspect.
        var still = new NodeWorld(Path.Combine(_dir, "making-none"));
        still.Step(30);
        var none = still.Mgr.CreateCapture(still.W.History, [FileTag], still.Now);
        Assert.Empty(none.Suspects);
        Assert.DoesNotContain("## In plain words", File.ReadAllText(Path.Combine(none.Dir, "report.md")));
        Assert.Contains("\"plainAnswer\": null,", File.ReadAllText(Path.Combine(none.Dir, "report.json")));
        static string Title(DateTime by) => $"`File` (nt File objects (charged to the opening process)): its trace had not finished by {Fmt.Clock(by)}";
        static string Draft(DateTime by) => $"Kernel pool tag `File` (nt File objects (charged to the opening process)): its trace had not finished by {Fmt.Clock(by)}.";
        Assert.Equal((Title(still.Now), Title(still.Now)), Titles(none));
        Assert.Equal($"- Growth: its trace had not finished by {Fmt.Clock(still.Now)}; the tag held 100 MB at {Fmt.Clock(still.Now)}", Line(none, "- Growth: "));
        // The draft bug report under them says the same, in its summary and its "Actual" line.
        Assert.Equal((Draft(still.Now), $"**Actual**: {Title(still.Now)}."), (DraftSummary(none), Line(none, "**Actual**: ")));

        var n = new NodeWorld(Path.Combine(_dir, "making"));
        n.Step(30);
        var made = n.Now;
        n.W.ScanHandles();
        n.W.Io(100, handles: 50);
        n.W.ScanHandles(TimeSpan.FromSeconds(5));
        var inc = n.Mgr.CreateCapture(n.W.History, [FileTag], made);
        Assert.Equal((IncidentKind.Capture, false, (CaptureResult?)null), (inc.Kind, inc.IsOpen, inc.Capture));
        Assert.Equal("node.exe", Assert.Single(inc.Suspects).Image);
        string Json() => File.ReadAllText(Path.Combine(inc.Dir, "report.json"));
        string Files() => File.ReadAllText(Path.Combine(inc.Dir, "report.md")) + Json();
        static string NotFinished(DateTime by) => $"{FileSaid}: its trace had not finished by {Fmt.Clock(by)}; this report is written again when it has.";
        // What the title, the Growth line and the plain words said before: none is in the report or in what is copied from it.
        void SaysNothingOfATraceNotRecorded()
        {
            foreach (string text in new[] { Files(), PoolWatch.Core.Util.CopyText.IncidentSummary(Json()), PoolWatch.Core.Util.CopyText.IncidentDetails(Json()) })
            {
                Assert.DoesNotContain(" flat over ", text);
                Assert.DoesNotContain(" over 0 s.", text);
                Assert.DoesNotContain("grew 0 B", text);
                Assert.DoesNotContain(" in 0 s", text);
                Assert.DoesNotContain("(+0 B)", text);
                Assert.DoesNotContain("no suspect stood out", text);
            }
        }
        Assert.Equal((NotFinished(made), NotFinished(made)), PlainAnswer(inc));
        Assert.Equal((Title(made), Title(made)), Titles(inc));
        Assert.Equal((Draft(made), $"**Actual**: {Title(made)}."), (DraftSummary(inc), Line(inc, "**Actual**: ")));
        Assert.Equal($"- Growth: its trace had not finished by {Fmt.Clock(made)}; the tag held 100 MB at {Fmt.Clock(made)}", Line(inc, "- Growth: "));
        // Copy summary copies report.json's headline as it is, and the window's details show it without the tag's backticks.
        Assert.StartsWith(Title(made) + "\r\n" + $"Incident {inc.Id} (Capture), tag File\r\n", PoolWatch.Core.Util.CopyText.IncidentSummary(Json()));
        Assert.StartsWith(Title(made).Replace("`", "") + "\r\n", PoolWatch.Core.Util.CopyText.IncidentDetails(Json()));
        Assert.Contains("\r\n" + NotFinished(made) + "\r\n", PoolWatch.Core.Util.CopyText.IncidentSummary(Json()));
        SaysNothingOfATraceNotRecorded();
        n.Mgr.Note(inc, made, "capture started: pool events"); // as the pipeline notes it: the report is written again at the next sample
        n.Step();
        Assert.Equal(made.AddSeconds(10), n.Now);
        Assert.Equal((NotFinished(n.Now), NotFinished(n.Now)), PlainAnswer(inc));
        Assert.Equal((Title(n.Now), Title(n.Now)), Titles(inc)); // by the newest sample at that write
        Assert.Equal(Draft(n.Now), DraftSummary(inc));
        Assert.Equal($"- Growth: its trace had not finished by {Fmt.Clock(n.Now)}; the tag held 100 MB at {Fmt.Clock(made)}", Line(inc, "- Growth: ")); // the level is the making's
        SaysNothingOfATraceNotRecorded();

        n.Step(8);
        var start = made.AddSeconds(3.7);
        n.Mgr.ApplyCapture(n.W.History, inc, new CaptureResult { PoolEventsParsed = true, Tags = [FileTag] }.Recorded(At(start), At(start.AddMinutes(1))));
        string flat = $"{FileSaid} was flat over 1 min.";
        Assert.Equal((flat, flat), PlainAnswer(inc));
        Assert.DoesNotContain("had not finished", Files());
        // The trace attached: the title and the Growth line are the trace's minute, as before.
        string grew = $"`File` (nt File objects (charged to the opening process)) grew 0 B in 1 min; top suspect node.exe PID 100, started {Fmt.Clock(inc.Suspects[0].StartUtc)} (weak evidence)";
        Assert.Equal((grew, grew), Titles(inc));
        Assert.Equal($"- Growth: 100 MB -> 100 MB (+0 B) from {Fmt.Clock(start)}; 100 MB at {Fmt.Clock(made.AddSeconds(60))}", Line(inc, "- Growth: "));
    }

    /// <summary>An incident's title: report.md's first line without its "# ", and report.json's <c>headline</c>.</summary>
    private static (string Md, string? Json) Titles(Incident inc)
    {
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(inc.Dir, "report.json")));
        string first = File.ReadAllLines(Path.Combine(inc.Dir, "report.md"))[0];
        Assert.StartsWith("# ", first);
        return (first[2..], json.RootElement.GetProperty("headline").GetString());
    }

    /// <summary>The summary of report.md's draft bug report (the line under "**Summary**").</summary>
    private static string DraftSummary(Incident inc)
    {
        var md = File.ReadAllLines(Path.Combine(inc.Dir, "report.md"));
        return md[Array.IndexOf(md, "**Summary**") + 2];
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ATracesReportWhoseTraceRecordedNothingSaysThatNotWhatTheTagDidOverNoTime(bool suspect, bool stamped)
    {
        // "Capture now", and the trace cannot start (another recording owns the kernel logger: wpr -start fails).
        // The runner hands PoolWatch a result with an error and no span: no start and no end at all, or, for a CPU
        // recording that held nothing PoolWatch could read, one moment for both. Attached, it leaves the incident
        // where it was made: its start and its end one moment, for good. Its report then says that its trace
        // recorded nothing PoolWatch could use: in its title, in its Growth line (with the level the tag held at the
        // sample the incident was made at), in its draft bug report and, when it has a suspect and so plain words,
        // in those, in report.md and report.json. The trace's error is under "Confirmation trace". Three hours on a note has the report
        // written again, and it says the same. (Before, the title read "... grew 0 B in 0 s", the Growth line
        // "100 MB -> 100 MB (+0 B) from ...", and the plain words "... was flat over 0 s.", of a trace that never
        // recorded.) A trace that recorded, for comparison, gives the incident its span: its report says what the
        // tag did over it.
        var n = new NodeWorld(Path.Combine(_dir, $"nothing-{suspect}-{stamped}"));
        n.Step(30);
        var made = n.Now;
        if (suspect)
        {
            n.W.ScanHandles();
            n.W.Io(100, handles: 50);
            n.W.ScanHandles(TimeSpan.FromSeconds(5));
        }
        var inc = n.Mgr.CreateCapture(n.W.History, [FileTag], made);
        n.Mgr.Note(inc, made, "capture started: pool events");
        n.Step(3);
        const string error = "wpr -start failed (1): another recording is running";
        var result = new CaptureResult { Tags = [FileTag], Error = error };
        if (stamped) result.Recorded(At(made.AddSeconds(3.7)), At(made.AddSeconds(3.7)));
        n.Mgr.Note(inc, n.Now, "capture failed: " + error); // as the pipeline notes a trace's error on the timeline
        n.Mgr.ApplyCapture(n.W.History, inc, result);
        Assert.Equal((made, (DateTime?)made, suspect ? 1 : 0), (inc.OnsetUtc, inc.ClosedUtc, inc.Suspects.Count)); // where it was made
        const string nothing = "its trace recorded nothing PoolWatch could use";
        string title = $"`File` (nt File objects (charged to the opening process)): {nothing}";
        string growth = $"- Growth: {nothing}; the tag held 100 MB at {Fmt.Clock(made)}";
        string Md() => File.ReadAllText(Path.Combine(inc.Dir, "report.md"));
        void Says()
        {
            Assert.Equal((title, title), Titles(inc));
            Assert.Equal(growth, Line(inc, "- Growth: "));
            Assert.Equal(($"Kernel pool tag {title}.", $"**Actual**: {title}."), (DraftSummary(inc), Line(inc, "**Actual**: ")));
            if (suspect) Assert.Equal(($"{FileSaid}: {nothing}.", $"{FileSaid}: {nothing}."), PlainAnswer(inc));
            else Assert.DoesNotContain("## In plain words", Md());
            Assert.Contains($"The trace failed: {error}", Md());
            foreach (string text in new[] { Md(), File.ReadAllText(Path.Combine(inc.Dir, "report.json")) })
            {
                Assert.DoesNotContain(" flat over ", text);
                Assert.DoesNotContain("grew 0 B", text);
                Assert.DoesNotContain(" in 0 s", text);
                Assert.DoesNotContain("(+0 B)", text);
                Assert.DoesNotContain("had not finished", text);
            }
        }
        Says();
        n.Step(1080);
        n.Mgr.Note(inc, n.Now, "a note added three hours on");
        n.Step();
        Assert.Contains("a note added three hours on", Md()); // written again
        Says();

        // A trace that recorded 20 seconds: the incident starts and closes with it, and its report says what the tag did over them.
        var r = new NodeWorld(Path.Combine(_dir, $"recorded-{suspect}-{stamped}"));
        r.Step(30);
        var recorded = r.Mgr.CreateCapture(r.W.History, [FileTag], r.Now);
        var start = r.Now.AddSeconds(3.7);
        r.Step(3);
        r.Mgr.ApplyCapture(r.W.History, recorded, new CaptureResult { PoolEventsParsed = true, Tags = [FileTag] }.Recorded(At(start), At(start.AddSeconds(20))));
        Assert.StartsWith("`File` (nt File objects (charged to the opening process)) grew 0 B in 20 s; ", Titles(recorded).Md);
        Assert.StartsWith($"- Growth: 100 MB -> 100 MB (+0 B) from {Fmt.Clock(start)}; 100 MB at ", Line(recorded, "- Growth: "));
    }
}
