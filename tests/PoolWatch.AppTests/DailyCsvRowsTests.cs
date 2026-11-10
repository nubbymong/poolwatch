using PoolWatch.Core;
using PoolWatch.Core.Model;
using PoolWatch.Core.Util;
using PoolWatch.Engine;

namespace PoolWatch.AppTests;

/// <summary>
/// What the day's CSV files hold outside verbose mode (CsvSink): each day starts with the whole picture, then what
/// changed; and a handle scan writes its own two files only.
/// </summary>
public sealed class DailyCsvRowsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pw-csvrows-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private AppPaths Paths() => new(Path.Combine(_root, "data"), null, secure: false);

    /// <summary>The data rows of a day file (CSV fields split), every day folder together, in the order written.</summary>
    private List<List<string>> Rows(string name) =>
        Directory.EnumerateFiles(Path.Combine(_root, "data"), name, SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .SelectMany(f => Lines(f).Skip(1)).Select(Csv.Split).ToList();

    /// <summary>A file's lines, read beside the sink, which holds it open.</summary>
    private static string[] Lines(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }

    private static Sample S(int minute, IReadOnlyList<PoolTagEntry> tags, IReadOnlyList<ProcessSample> procs) =>
        new(Now.AddMinutes(minute), default, tags, procs) { TickMs = minute * 60_000L };

    /// <summary>
    /// An Exit or Orphaned row leaves out the command line its Start row gave that day (they are matched by pid and
    /// create_time); the registry's Exit and Orphaned events carry no user (ProcessRegistry.MarkExited, PromoteOrphans).
    /// </summary>
    [Fact]
    public void AnExitOrOrphanedRowLeavesOutTheCommandLineItsStartRowGaveThatDay()
    {
        var start = new LifecycleEvent { TimeUtc = Now, Kind = LifecycleKind.Start, Pid = 10, CreateTime = 111, ParentPid = 4, Image = "node.exe", User = "PC\\me", CommandLine = "node server.js --port 3000" };
        var unseen = new LifecycleEvent { TimeUtc = Now, Kind = LifecycleKind.Exit, Pid = 11, CreateTime = 222, ParentPid = 4, Image = "git.exe", CommandLine = "git fetch", ExitCode = 1 };
        using (var sink = new CsvSink(Paths(), new Settings { RecordMode = RecordMode.Detailed }, verbose: false))
        {
            sink.WriteEvents([start], Now);
            sink.WriteEvents([start with { Kind = LifecycleKind.Orphaned, TimeUtc = Now.AddMinutes(1), User = null, Detail = "parent bash.exe (4) exited" }], Now.AddMinutes(1));
            sink.WriteEvents([start with { Kind = LifecycleKind.Exit, TimeUtc = Now.AddMinutes(2), ExitCode = 0, User = null }, unseen], Now.AddMinutes(2));
            // The next day's file has no Start row of a process still running: its exit is written whole there.
            var late = new LifecycleEvent { TimeUtc = Now, Kind = LifecycleKind.Start, Pid = 12, CreateTime = 333, Image = "sleep.exe", CommandLine = "sleep 99999" };
            sink.WriteEvents([late], Now);
            sink.WriteEvents([late with { Kind = LifecycleKind.Exit, TimeUtc = Now.AddDays(1) }], Now.AddDays(1));
            sink.Flush();
        }
        var rows = Rows("events.csv");
        Assert.Equal(["Start", "node server.js --port 3000", "PC\\me"], [rows[0][1], rows[0][8], rows[0][7]]);
        Assert.Equal(["Orphaned", "", "", "parent bash.exe (4) exited"], [rows[1][1], rows[1][8], rows[1][7], rows[1][9]]);
        Assert.Equal(["Exit", "10", "111", "node.exe", "0", "", ""], [rows[2][1], rows[2][2], rows[2][3], rows[2][5], rows[2][6], rows[2][7], rows[2][8]]);
        Assert.Equal(["Exit", "git fetch"], [rows[3][1], rows[3][8]]); // its start is not in the file: whole
        Assert.Equal(["Exit", "sleep 99999"], [rows[^1][1], rows[^1][8]]);
    }

    [Fact]
    public void ATagHasARowWhenItMovedAMegabyteEveryTenMinutesAndOnceAsItFallsUnderTheMinimum()
    {
        PoolTag steady = PoolTag.Parse("Stdy"), growing = PoolTag.Parse("Grow"), falling = PoolTag.Parse("Fall");
        using (var sink = new CsvSink(Paths(), new Settings { RecordMode = RecordMode.Detailed }, verbose: false))
            for (int m = 0; m <= 12; m++)
            {
                var tags = new List<PoolTagEntry>
                {
                    new(steady, (uint)m, 0, (8UL << 20) + (ulong)m * 50_000, 0, 0, 0), // 50 KB a minute
                    new(growing, 0, 0, 0, (uint)m, 0, (8UL << 20) + ((ulong)m << 21)), // 2 MB a minute
                    new(falling, 0, 0, m < 3 ? 2UL << 20 : 1UL << 19, 0, 0, 0), // under 1 MB from minute 3
                };
                sink.WriteSample(S(m, tags, []), new ProcessRegistry());
            }
        var rows = Rows("tags.csv");
        int Count(PoolTag t) => rows.Count(r => r[1] == t.ToString());
        Assert.Equal(2, Count(steady)); // the first, and 10 minutes on
        Assert.Equal(13, Count(growing)); // every minute
        Assert.Equal(2, Count(falling)); // the first, and the one under the minimum
        Assert.Equal(new string[] { "2026-11-09T10:00:00.000Z", "2026-11-09T10:10:00.000Z" }, rows.Where(r => r[1] == steady.ToString()).Select(r => r[0]));
        Assert.Equal((1UL << 19).ToString(), rows.Last(r => r[1] == falling.ToString())[3]);
    }

    [Fact]
    public void AProcessIsWrittenOnceItRanAtTwoWritesAndAgainWhileItIsBusy()
    {
        static ProcessSample P(int pid, long reads = 0, int handles = 50) => new() { Pid = pid, ParentPid = 4, CreateTime = pid, ImageName = $"p{pid}.exe", ReadOperationCount = reads, HandleCount = handles };
        using (var sink = new CsvSink(Paths(), new Settings { RecordMode = RecordMode.Detailed }, verbose: false))
        {
            sink.WriteSample(S(0, [], [P(1), P(2)]), new ProcessRegistry());               // the day's first: every one
            sink.WriteSample(S(1, [], [P(1), P(2, 100), P(3), P(4)]), new ProcessRegistry()); // 2 busy; 3 and 4 new
            sink.WriteSample(S(2, [], [P(1), P(2, 100), P(3), P(5, 0, 50)]), new ProcessRegistry()); // 3 ran at two writes; 4 gone
            sink.WriteSample(S(3, [], [P(1, 0, 400), P(2, 100), P(3), P(5)]), new ProcessRegistry()); // 1's handles moved; 5 at its second
        }
        var rows = Rows("processes.csv").Select(r => (Minute: r[0][14..16], Pid: r[1])).ToList();
        Assert.Equal([("00", "1"), ("00", "2"), ("01", "2"), ("02", "3"), ("03", "1"), ("03", "5")], rows);
    }

    [Fact]
    public void TheServicesListIsWrittenWholeThenEachServiceWhoseHostChanged()
    {
        using (var sink = new CsvSink(Paths(), new Settings { RecordMode = RecordMode.Detailed }, verbose: false))
        {
            sink.WriteServices([new("A", "Alpha", 100), new("B", "Beta", 100), new("C", "Gamma", 0)], Now);
            sink.WriteServices([new("A", "Alpha", 100), new("B", "Beta", 100), new("C", "Gamma", 0)], Now.AddMinutes(1)); // unchanged
            sink.WriteServices([new("A", "Alpha", 100), new("C", "Gamma", 300)], Now.AddMinutes(2)); // B left, C started
            sink.WriteServices([new("A", "Alpha", 0), new("C", "Gamma", 300)], Now.AddMinutes(3)); // A stopped
        }
        Assert.Equal(new[] { "00:100:A", "00:100:B", "02:0:B", "02:300:C", "03:0:A" },
            Rows("services.csv").Select(r => $"{r[0][14..16]}:{r[1]}:{r[2]}"));
    }

    [Fact]
    public void AHandleScanWritesTheTotalsEveryScanAndAProcessesCountsWhenTheyChange()
    {
        static HandleCounts Counts(int file)
        {
            var c = new HandleCounts();
            for (int i = 0; i < file; i++) c.Add(500, 1);
            for (int i = 0; i < 30; i++) c.Add(500, 2);
            return c;
        }
        var names = new Dictionary<int, string> { [1] = "File", [2] = "Event" };
        var procs = new[] { new ProcessSample { Pid = 500, CreateTime = 5, ImageName = "app.exe" } };
        using (var sink = new CsvSink(Paths(), new Settings { RecordMode = RecordMode.Detailed }, verbose: false))
        {
            sink.WriteHandles(Counts(40), names, procs, Now);
            sink.WriteHandles(Counts(40), names, procs, Now.AddMinutes(5));
            sink.WriteHandles(Counts(90), names, procs, Now.AddMinutes(10));
        }
        var rows = Rows("handles.csv").Select(r => $"{r[0][14..16]}:{r[1]}:{r[3]}:{r[4]}").ToList();
        Assert.Equal(["00:-1:File:40", "00:-1:Event:30", "00:500:File:40", "00:500:Event:30", "05:-1:File:40", "05:-1:Event:30",
            "10:-1:File:90", "10:-1:Event:30", "10:500:File:90"], rows);
    }

    private static HandleCounts Census(params (int Pid, int Type, int N)[] held)
    {
        var c = new HandleCounts();
        foreach (var (pid, type, n) in held)
            for (int i = 0; i < n; i++) c.Add(pid, type);
        return c;
    }

    private static ProcessSample P(int pid) => new() { Pid = pid, ParentPid = 4, CreateTime = pid * 10L, ImageName = $"p{pid}.exe" };

    /// <summary>
    /// A count with a row that a scan no longer picks gets one more row with its count then, so a reader who carries the last
    /// row forward (as AGENTS.md says) never reads a count that is no longer held: p500's 50,000 File handles released to 5
    /// (under the 20 a process among the 20 with the most needs), p501's 800 File handles gone (0), p500's Event handles
    /// fallen under 20 later. A process that exited (p600) gets none: its exit is in events.csv.
    /// </summary>
    [Fact]
    public void ACountThatLeavesTheSelectionGetsOneLastRowWithItsCountThen()
    {
        var names = new Dictionary<int, string> { [1] = "File", [2] = "Event" };
        using (var sink = new CsvSink(Paths(), new Settings { RecordMode = RecordMode.Detailed }, verbose: false))
        {
            sink.WriteHandles(Census((500, 1, 50_000), (500, 2, 30), (501, 1, 800), (501, 2, 25), (600, 1, 700)), names, [P(500), P(501), P(600)], Now);
            sink.WriteHandles(Census((500, 1, 5), (500, 2, 30), (501, 2, 25)), names, [P(500), P(501)], Now.AddMinutes(5));
            sink.WriteHandles(Census((500, 1, 5), (500, 2, 30), (501, 2, 25)), names, [P(500), P(501)], Now.AddMinutes(10)); // nothing changed
            sink.WriteHandles(Census((500, 2, 10), (501, 2, 25)), names, [P(500), P(501)], Now.AddMinutes(15));
        }
        var rows = Rows("handles.csv").Where(r => r[1] != "-1").Select(r => $"{r[0][14..16]}:{r[1]}:{r[3]}:{r[4]}").ToList();
        Assert.Equal(["00:500:File:50000", "00:500:Event:30", "00:501:File:800", "00:501:Event:25", "00:600:File:700",
            "05:500:File:5", "05:501:File:0", "15:500:Event:10"], rows);
    }

    /// <summary>
    /// A scan whose selection is over <see cref="CsvSink.MaxHandleRowsPerScan"/> rows writes the largest counts and says how
    /// many it left out (pid -2), as AGENTS.md tells a reader; the next scan, unchanged, writes none of them again but says
    /// so again; a scan without that many writes the counts left out before.
    /// </summary>
    [Fact]
    public void AScanWritesAtMostItsCapOfProcessRowsTheLargestFirstAndSaysHowManyItLeftOut()
    {
        int n = CsvSink.MaxHandleRowsPerScan + 100;
        var census = Census(Enumerable.Range(0, n).Select(i => (10_000 + i, 1, 500 + i)).ToArray());
        var procs = Enumerable.Range(0, n).Select(i => P(10_000 + i)).ToArray();
        int fits = CsvSink.MaxHandleRowsPerScan;
        var fewer = Census(Enumerable.Range(0, fits).Select(i => (10_000 + i, 1, 500 + i)).ToArray()); // the 100 largest exited
        var names = new Dictionary<int, string> { [1] = "File" };
        using (var sink = new CsvSink(Paths(), new Settings { RecordMode = RecordMode.Detailed }, verbose: false))
        {
            sink.WriteHandles(census, names, procs, Now);
            sink.WriteHandles(census, names, procs, Now.AddMinutes(5));
            sink.WriteHandles(fewer, names, procs[..fits], Now.AddMinutes(10));
        }
        static int Count(List<string> r) => int.Parse(r[4], System.Globalization.CultureInfo.InvariantCulture);
        var all = Rows("handles.csv").Where(r => r[1] != "-1").ToList();
        List<List<string>> At(int minute) => all.Where(r => r[0].StartsWith($"2026-11-09T10:{minute:00}:", StringComparison.Ordinal)).ToList();
        Assert.Equal(["-2:(left out):100"], At(0).Where(r => r[1] == "-2").Select(r => $"{r[1]}:{r[3]}:{r[4]}"));
        Assert.Equal(CsvSink.MaxHandleRowsPerScan, At(0).Count(r => r[1] != "-2"));
        Assert.Equal(600, At(0).Where(r => r[1] != "-2").Min(Count));
        Assert.Equal(["-2"], At(5).Select(r => r[1])); // unchanged, and still more than fit
        Assert.Equal(Enumerable.Range(500, 100), At(10).Select(Count).Order()); // recorded again once they fit; no pid -2
    }

    /// <summary>
    /// An Exit keeps a command line its Start row did not have (the process was read after its start: the registry's enrich
    /// retry), and one that differs from the Start row's.
    /// </summary>
    [Fact]
    public void AnExitKeepsACommandLineItsStartRowDidNotHave()
    {
        var unread = new LifecycleEvent { TimeUtc = Now, Kind = LifecycleKind.Start, Pid = 20, CreateTime = 444, ParentPid = 4, Image = "node.exe" };
        var other = unread with { Pid = 21, CreateTime = 555, CommandLine = "node a.js" };
        using (var sink = new CsvSink(Paths(), new Settings { RecordMode = RecordMode.Detailed }, verbose: false))
        {
            sink.WriteEvents([unread, other], Now);
            sink.WriteEvents([unread with { Kind = LifecycleKind.Exit, TimeUtc = Now.AddMinutes(1), CommandLine = "node server.js (enriched later)", ExitCode = 0 },
                other with { Kind = LifecycleKind.Exit, TimeUtc = Now.AddMinutes(1), CommandLine = "node b.js", ExitCode = 0 }], Now.AddMinutes(1));
        }
        Assert.Equal(["Start:20:", "Start:21:node a.js", "Exit:20:node server.js (enriched later)", "Exit:21:node b.js"],
            Rows("events.csv").Select(r => $"{r[1]}:{r[2]}:{r[8]}"));
    }

    /// <summary>
    /// Past <see cref="CsvSink.MaxStartsRemembered"/> running processes the Start rows remembered are forgotten (the memory
    /// stays bounded): their Exit rows are then written whole, never left without a command line no Start row is known to give.
    /// </summary>
    [Fact]
    public void PastTheStartsRememberedAnExitIsWrittenWhole()
    {
        int n = CsvSink.MaxStartsRemembered + 1;
        var starts = Enumerable.Range(0, n).Select(i => new LifecycleEvent { TimeUtc = Now, Kind = LifecycleKind.Start, Pid = 30_000 + i, CreateTime = 1 + i, ParentPid = 4, Image = "sleep.exe", CommandLine = $"sleep {i}" }).ToList();
        using (var sink = new CsvSink(Paths(), new Settings { RecordMode = RecordMode.Detailed }, verbose: false))
        {
            sink.WriteEvents(starts, Now);
            sink.WriteEvents([starts[0] with { Kind = LifecycleKind.Exit, TimeUtc = Now.AddMinutes(1) }, starts[^1] with { Kind = LifecycleKind.Exit, TimeUtc = Now.AddMinutes(1) }], Now.AddMinutes(1));
        }
        var exits = Rows("events.csv").Where(r => r[1] == "Exit").Select(r => $"{r[2]}:{r[8]}").ToList();
        Assert.Equal(["30000:sleep 0", $"{30_000 + n - 1}:"], exits); // the first was forgotten: whole; the last remembered
    }

    /// <summary>A day file's writes fail while <c>failing</c> holds (the disk is full, say): its rows are kept.</summary>
    private sealed class Failing(Stream inner, Func<bool> failing) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (failing()) throw new IOException("There is not enough space on the disk (test).");
            inner.Write(buffer);
        }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    /// <summary>
    /// A file whose kept rows were dropped (no file could take them, past the bound) may not hold what the sink thought, so
    /// its next write gives the whole picture again: the tags, the processes, the services and the handle counts.
    /// </summary>
    [Fact]
    public void AFileWhoseKeptRowsWereDroppedIsWrittenWholeAtItsNextWrite()
    {
        bool failing = false;
        var log = new List<string>();
        Stream Open(string p, FileMode m, FileAccess a, FileShare s) => m == FileMode.Append ? new Failing(new FileStream(p, m, a, s, bufferSize: 0), () => failing) : new FileStream(p, m, a, s, bufferSize: 0);
        var quiet = Enumerable.Range(0, 3).Select(i => new PoolTagEntry(PoolTag.Parse($"Qt{i}"), 0, 0, 4UL << 20, 0, 0, 0)).ToList();
        List<PoolTagEntry> Moving(int m) => Enumerable.Range(0, 30).Select(i => new PoolTagEntry(PoolTag.Parse($"Mv{i:00}"), 0, 0, (4UL + 2UL * (ulong)m) << 20, 0, 0, 0)).ToList();
        var calm = Enumerable.Range(0, 3).Select(i => P(70 + i)).ToList();
        List<ProcessSample> Busy(int m) => Enumerable.Range(0, 30).Select(i => P(80 + i) with { ReadOperationCount = 1000L * m }).ToList();
        List<ServiceEntry> Services(int m) => [.. Enumerable.Range(0, 3).Select(i => new ServiceEntry($"Calm{i}", $"Calm {i}", 900)),
            .. Enumerable.Range(0, 30).Select(i => new ServiceEntry($"Moving{i:00}", $"Moving {i}", 1000 + m))];
        var names = new Dictionary<int, string> { [1] = "File" };
        HandleCounts Handles(int m) => Census([(60, 1, 900), .. Enumerable.Range(0, 30).Select(i => (61 + i, 1, 500 + m * 10 + i))]);
        var handleProcs = Enumerable.Range(60, 31).Select(P).ToArray();
        using (var sink = new CsvSink(Paths(), new Settings { RecordMode = RecordMode.Detailed }, verbose: false, log.Add, Open, maxKeptChars: 4096))
        {
            // The day's whole picture, written.
            sink.WriteSample(S(0, [.. quiet, .. Moving(0)], [.. calm, .. Busy(0)]), new ProcessRegistry());
            sink.WriteServices(Services(0), Now);
            sink.WriteHandles(Handles(0), names, handleProcs, Now);
            // Then no file can be written: what moves is kept, past the bound, and the oldest of the file keeping the most dropped.
            failing = true;
            for (int m = 1; m <= 8; m++)
            {
                sink.WriteSample(S(m, Moving(m), Busy(m)), new ProcessRegistry());
                sink.WriteServices(Services(m), Now.AddMinutes(m));
                sink.WriteHandles(Handles(m), names, handleProcs, Now.AddMinutes(m));
            }
            failing = false;
            // Nothing quiet moved (and a tag's 10-minute refresh is not due): a sink that still thought each file held its rows
            // would write none of the quiet ones.
            sink.WriteSample(S(9, [.. quiet], [.. calm]), new ProcessRegistry());
            sink.WriteServices(Services(8), Now.AddMinutes(9));
            sink.WriteHandles(Handles(8), names, handleProcs, Now.AddMinutes(9));
        }
        foreach (var file in new[] { "tags.csv", "processes.csv", "services.csv", "handles.csv" })
            Assert.Contains(log, l => l.StartsWith(file + ": the rows that could not be written are kept up to 4 K characters", StringComparison.Ordinal));
        static bool At9(List<string> r) => r[0].StartsWith("2026-11-09T10:09:", StringComparison.Ordinal);
        Assert.Equal(3, Rows("tags.csv").Count(r => At9(r) && r[1].StartsWith("Qt", StringComparison.Ordinal)));
        Assert.Equal(3, Rows("processes.csv").Count(r => At9(r) && r[1] is "70" or "71" or "72"));
        Assert.Equal(3, Rows("services.csv").Count(r => At9(r) && r[2].StartsWith("Calm", StringComparison.Ordinal)));
        Assert.Single(Rows("handles.csv"), r => At9(r) && r[1] == "60");
    }

    /// <summary>
    /// A handle scan writes handles.csv and system-handles.csv only: the rows other files keep (an event drained this tick,
    /// a sample's) wait for the sample's own flush. (In the owner's beta.2 one scan's CSV stage, which also wrote every other
    /// file, took 41.9 s, 24 s of it the sampler thread's CPU; what took the time was not found. The scan is now written on
    /// its own thread: HandleCsvWriterTests.)
    /// </summary>
    [Fact]
    public void AHandleScanWritesOnlyItsOwnTwoFiles()
    {
        var writes = new List<string>();
        bool counting = false;
        Stream Open(string p, FileMode m, FileAccess a, FileShare s)
        {
            var fs = new FileStream(p, m, a, s, bufferSize: 0);
            string name = Path.GetFileName(p);
            return m == FileMode.Append ? new Recording(fs, () => { if (counting) writes.Add(name); }) : fs;
        }
        var c = new HandleCounts();
        for (int i = 0; i < 40; i++) { c.Add(4, 1); c.Add(500, 1); }
        using (var sink = new CsvSink(Paths(), new Settings { RecordMode = RecordMode.Detailed }, verbose: false, null, Open))
        {
            sink.WriteSample(S(0, [new(PoolTag.Parse("Leak"), 0, 0, 2UL << 20, 0, 0, 0)], [new ProcessSample { Pid = 500, CreateTime = 5, ImageName = "app.exe" }]), new ProcessRegistry());
            sink.WriteEvents([new LifecycleEvent { TimeUtc = Now, Kind = LifecycleKind.Finding, Image = "Alert", Detail = "kept until the sample's flush" }], Now);
            counting = true;
            sink.WriteHandles(c, new Dictionary<int, string> { [1] = "File" }, [new ProcessSample { Pid = 500, CreateTime = 5, ImageName = "app.exe" }], Now);
            counting = false;
            Assert.Equal(["handles.csv", "system-handles.csv"], writes.Order(StringComparer.Ordinal));
            Assert.Empty(Rows("events.csv"));
            sink.WriteSample(S(1, [], []), new ProcessRegistry());
            Assert.Single(Rows("events.csv"));
        }
    }

    private sealed class Recording(Stream inner, Action wrote) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); wrote(); }
        public override void Write(ReadOnlySpan<byte> buffer) { inner.Write(buffer); wrote(); }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    /// <summary>
    /// The event summary's minute rollups reach the day's events.csv merged over 5 minutes: 12 minutes of one bash's short
    /// commands (two images) are 6 rows (two each for minutes 0-4 and 5-9, then the rest as the sink lets go), with every
    /// start counted, where they were 24; the rollups handed on with the notable events are not written again.
    /// </summary>
    [Fact]
    public void TheMinuteRollupsAreMergedOverFiveMinutesInTheDaysEventsFile()
    {
        var summarizer = new EventSummarizer();
        int pid = 5000, starts = 0;
        using (var sink = new CsvSink(Paths(), new Settings { RecordMode = RecordMode.Detailed }, verbose: false))
        {
            sink.MergeRollupsFrom(summarizer);
            for (int s = 0; s <= 72; s++) // a sample every 10 s for 12 minutes
            {
                var at = Now.AddSeconds(10 * s);
                var events = new List<LifecycleEvent>();
                for (int k = 0; k < 3; k++, pid++, starts++)
                {
                    string image = k == 0 ? "git.exe" : "sed.exe";
                    events.Add(new LifecycleEvent { TimeUtc = at, Kind = LifecycleKind.Start, Pid = pid, CreateTime = at.ToFileTimeUtc() + k, ParentPid = 900, Image = image, CommandLine = $"{image} -n {k}" });
                    events.Add(new LifecycleEvent { TimeUtc = at.AddMilliseconds(50), Kind = LifecycleKind.Exit, Pid = pid, CreateTime = at.ToFileTimeUtc() + k, ParentPid = 900, Image = image, ExitCode = 0 });
                }
                var notable = summarizer.Process(events, new Moment(at.AddSeconds(1), 10_000L * s + 1000), _ => "bash.exe");
                Assert.All(notable, e => Assert.Equal(LifecycleKind.Rollup, e.Kind));
                sink.WriteEvents(notable, at);
                sink.WriteSample(S(0, [], []) with { TimeUtc = at, TickMs = 10_000L * s }, new ProcessRegistry());
            }
            sink.WriteEvents(summarizer.Flush(new Moment(Now.AddMinutes(12.1), 726_000)), Now.AddMinutes(12.1));
        }
        var rollups = Rows("events.csv").Where(r => r[1] == "Rollup").ToList();
        Assert.Equal(6, rollups.Count);
        int counted = rollups.Sum(r => int.Parse(System.Text.RegularExpressions.Regex.Match(r[9], @"started ([\d,]+)").Groups[1].Value.Replace(",", ""), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(starts, counted);
        Assert.All(rollups, r => Assert.StartsWith("bash.exe (900) started ", r[9]));
        // Each row is dated by its first start: minutes 0 to 5, then from 5:10 (the merge's 5 minutes are the samples' own).
        Assert.Equal(["2026-11-09T10:00:00.000Z", "2026-11-09T10:00:00.000Z", "2026-11-09T10:05:10.000Z", "2026-11-09T10:05:10.000Z"], rollups.Take(4).Select(r => r[0]));
    }

    /// <summary>In verbose mode the day's events.csv has every minute rollup the event summary hands on, none merged.</summary>
    [Fact]
    public void VerboseModeWritesEveryMinuteRollup()
    {
        var summarizer = new EventSummarizer();
        int pid = 5000, handedOn = 0;
        using (var sink = new CsvSink(Paths(), new Settings { RecordMode = RecordMode.Detailed }, verbose: true))
        {
            sink.MergeRollupsFrom(summarizer);
            for (int s = 0; s <= 72; s++)
            {
                var at = Now.AddSeconds(10 * s);
                var events = new List<LifecycleEvent>();
                for (int k = 0; k < 3; k++, pid++)
                {
                    string image = k == 0 ? "git.exe" : "sed.exe";
                    events.Add(new LifecycleEvent { TimeUtc = at, Kind = LifecycleKind.Start, Pid = pid, CreateTime = at.ToFileTimeUtc() + k, ParentPid = 900, Image = image, CommandLine = $"{image} -n {k}" });
                    events.Add(new LifecycleEvent { TimeUtc = at.AddMilliseconds(50), Kind = LifecycleKind.Exit, Pid = pid, CreateTime = at.ToFileTimeUtc() + k, ParentPid = 900, Image = image, ExitCode = 0 });
                }
                var notable = summarizer.Process(events, new Moment(at.AddSeconds(1), 10_000L * s + 1000), _ => "bash.exe");
                handedOn += notable.Count(e => e.Kind == LifecycleKind.Rollup);
                sink.WriteEvents(notable, at);
                sink.WriteSample(S(0, [], []) with { TimeUtc = at, TickMs = 10_000L * s }, new ProcessRegistry());
            }
        }
        Assert.True(handedOn >= 22, $"{handedOn} minute rollups");
        Assert.Equal(handedOn, Rows("events.csv").Count(r => r[1] == "Rollup"));
    }

    private static RollupParts Parts(string image, DateTime at, IReadOnlyCollection<int>? parents = null, IReadOnlyDictionary<string, int>? patterns = null) =>
        new("owner", "bash.exe", "bash.exe", image, 3, 3, 900, parents ?? [900], at, at.AddSeconds(30), patterns ?? new Dictionary<string, int> { [image + " -n"] = 3 });

    /// <summary>
    /// Past <see cref="CsvSink.MaxRollupsMerged"/> keys being merged at once, a rollup of a new key is written as it came
    /// (none is held without a bound, none lost); the others when their 5 minutes are over.
    /// </summary>
    [Fact]
    public void PastTheRollupsMergedARollupIsWrittenAsItCame()
    {
        using (var sink = new CsvSink(Paths(), new Settings { RecordMode = RecordMode.Detailed }, verbose: false))
        {
            sink.WriteEvents([], Now); // the day's folder
            for (int i = 0; i <= CsvSink.MaxRollupsMerged; i++) sink.AddRollup(Parts($"tool{i}.exe", Now));
            sink.Flush();
            var early = Rows("events.csv");
            Assert.Single(early);
            Assert.Equal($"tool{CsvSink.MaxRollupsMerged}.exe", early[0][5]);
        }
        Assert.Equal(CsvSink.MaxRollupsMerged + 1, Rows("events.csv").Count(r => r[1] == "Rollup"));
    }

    /// <summary>
    /// A rollup being merged does not keep the event summary's own collections (its parent PIDs and command patterns, valid
    /// during its call only, up to 1,000 and 20 for each of up to 2,000 keys for 5 minutes): the merge counts its own.
    /// </summary>
    [Fact]
    public void AMergedRollupKeepsNoneOfTheSummarysCollections()
    {
        using var sink = new CsvSink(Paths(), new Settings { RecordMode = RecordMode.Detailed }, verbose: false);
        sink.WriteEvents([], Now);
        var (parents, patterns) = Hand(sink);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(parents.IsAlive, "the summary's parent PIDs are still held");
        Assert.False(patterns.IsAlive, "the summary's command patterns are still held");
        sink.Dispose();
        var row = Assert.Single(Rows("events.csv"));
        Assert.Matches(@"^bash\.exe ×[\d,. ]+\+ started 3, 3 exited", row[9]); // the merge's own parents, counted to their cap
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (WeakReference Parents, WeakReference Patterns) Hand(CsvSink sink)
    {
        var parents = new HashSet<int>(Enumerable.Range(1, EventSummarizer.MaxRollupParents));
        var patterns = new Dictionary<string, int> { ["git status"] = 3 };
        sink.AddRollup(Parts("git.exe", Now, parents, patterns));
        return (new WeakReference(parents), new WeakReference(patterns));
    }
}
