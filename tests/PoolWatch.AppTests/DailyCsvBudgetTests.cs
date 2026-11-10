using PoolWatch.Core;
using PoolWatch.Core.Lag;
using PoolWatch.Core.Model;
using PoolWatch.Core.Services;
using PoolWatch.Engine;
using Xunit.Abstractions;

namespace PoolWatch.AppTests;

/// <summary>
/// The day's CSV files under a PC doing agentic coding, the owner's shape (1.0.0-beta.2, data\20261109: 112 MB in the
/// day, about 4.7 MB an hour, events.csv 2.2, tags.csv 0.95, processes.csv 0.75, services.csv 0.31, handles.csv 0.19):
/// samples every 10 s of about 330 processes, 25 short commands caught in each snapshot, about 1,100 pool tags of which
/// 180 over 1 MB and 10 growing (ten growth incidents open), 30 cache tags moving; the events.csv rows the event summary
/// gives such a PC in an hour (4,000 rollups, 1,940 long-running starts and their exits, 540 orphans, 210 findings, at
/// the owner's lengths), a services list that changes 25 times an hour, handle scans every 5 minutes with a third of
/// their counts moving, and the lag and load series. The sink's bytes are counted at its opener, for 4 hours.
/// </summary>
public sealed class DailyCsvBudgetTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pw-csvbudget-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>The day's CSV files' share of the 5 MB an hour (DESIGN.md section 3), beside the report folders' 1 MB.</summary>
    private const double ShareMBPerHour = 2.5;

    /// <summary>A stream that counts what is written through it, by the file's name.</summary>
    private sealed class Counting(Stream inner, Action<long> wrote) : Stream
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
        public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); wrote(count); }
        public override void Write(ReadOnlySpan<byte> buffer) { inner.Write(buffer); wrote(buffer.Length); }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    /// <summary>The owner's shape, deterministic (one seed).</summary>
    internal sealed class AgenticWorld
    {
        private readonly Random _rnd = new(20261109);
        public readonly DateTime T0 = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
        public const int SamplesPerHour = 360;
        private readonly List<ProcessSample> _stable = new();
        private readonly List<(ProcessSample P, int UntilSample)> _medium = new();
        private readonly List<(PoolTag Tag, double Bytes, double Drift, uint Allocs)> _tags = new();
        private readonly List<ServiceEntry> _services = new();
        private readonly Queue<(LifecycleEvent Start, int ExitAt)> _running = new();
        private int _nextPid = 20_000, _sample;
        private double _owedStarts, _owedOrphans, _owedFindings;
        private static readonly string[] Short = ["bash.exe", "sleep.exe", "sed.exe", "git.exe", "grep.exe", "tr.exe", "cut.exe", "date.exe"];

        public AgenticWorld()
        {
            for (int i = 0; i < 300; i++)
                _stable.Add(new ProcessSample
                {
                    Pid = 1000 + i, ParentPid = i == 0 ? 4 : 1000 + _rnd.Next(i), CreateTime = T0.AddHours(-5).ToFileTimeUtc() + i,
                    ImageName = i < 40 ? "chrome.exe" : i < 80 ? "svchost.exe" : i < 120 ? "node.exe" : i < 150 ? "conhost.exe" : $"app{i}.exe",
                    HandleCount = 200 + _rnd.Next(800), ThreadCount = 10, PrivateBytes = (ulong)_rnd.Next(1 << 28),
                });
            _stable.Add(new ProcessSample { Pid = 4, ParentPid = 0, CreateTime = 1, ImageName = "System", HandleCount = 30_000 });
            _stable.Add(new ProcessSample { Pid = Environment.ProcessId, ParentPid = 1000, CreateTime = 2, ImageName = "PoolWatch.exe", HandleCount = 800 });
            for (int i = 0; i < 1100; i++)
            {
                double bytes = i < 180 ? (1 << 20) + _rnd.NextDouble() * (i < 10 ? 2e9 : 2e8) : _rnd.NextDouble() * (1 << 20);
                // Ten tags grow (the ten open growth incidents), 30 caches move both ways, the rest hold within 100 KB.
                double drift = i < 10 ? 2e6 + _rnd.NextDouble() * 8e6 : i < 40 ? 3e6 : 1e5;
                _tags.Add((new PoolTag((uint)(0x41414141 + i * 7919)), bytes, drift, (uint)_rnd.Next(1 << 20)));
            }
            for (int i = 0; i < 250; i++) _services.Add(new ServiceEntry($"Svc{i:D3}", $"Service number {i} of the PC", i < 200 ? 1040 + i % 40 : 0));
        }

        public DateTime Utc => T0.AddSeconds(10 * _sample);
        public long Ms => 10_000L * _sample;

        /// <summary>One sample's snapshot: the stable processes with their counters moving, the medium-lived ones, 25 short commands.</summary>
        public Sample NextSample()
        {
            _sample++;
            for (int i = 0; i < _stable.Count; i++)
            {
                var p = _stable[i];
                bool busy = i < 60 || _rnd.Next(30) == 0;
                int dh = i == 5 ? 20 : busy ? _rnd.Next(-5, 6) : 0; // PID 1005 leaks 20 handles a sample
                _stable[i] = p with
                {
                    ReadOperationCount = p.ReadOperationCount + (busy ? _rnd.Next(1, 500) : 0), WriteOperationCount = p.WriteOperationCount + (busy ? _rnd.Next(0, 200) : 0),
                    OtherOperationCount = p.OtherOperationCount + (busy ? _rnd.Next(0, 300) : 0), ReadTransferCount = p.ReadTransferCount + (busy ? _rnd.Next(1 << 16) : 0),
                    WriteTransferCount = p.WriteTransferCount + (busy ? _rnd.Next(1 << 15) : 0), HandleCount = Math.Max(10, p.HandleCount + dh),
                };
            }
            // About 3 a minute live for 1 to 10 minutes (sleep 60, a renderer, a test run).
            if (_rnd.Next(20) < 10)
                _medium.Add((new ProcessSample { Pid = _nextPid++, ParentPid = 1100 + _rnd.Next(20), CreateTime = Utc.ToFileTimeUtc(), ImageName = _rnd.Next(2) == 0 ? "sleep.exe" : "chrome.exe", HandleCount = 100 },
                    _sample + 6 + _rnd.Next(54)));
            _medium.RemoveAll(m => m.UntilSample <= _sample);
            var procs = new List<ProcessSample>(_stable);
            procs.AddRange(_medium.Select(m => m.P));
            for (int k = 0; k < 25; k++)
                procs.Add(new ProcessSample { Pid = _nextPid++, ParentPid = 1120 + _rnd.Next(30), CreateTime = Utc.ToFileTimeUtc() - k, ImageName = Short[_rnd.Next(Short.Length)], HandleCount = 60 });
            for (int i = 0; i < _tags.Count; i++)
            {
                var t = _tags[i];
                double step = i < 10 ? t.Drift / 6 : (_rnd.NextDouble() - 0.5) * t.Drift / 3;
                _tags[i] = (t.Tag, Math.Max(0, t.Bytes + step), t.Drift, t.Allocs + (uint)_rnd.Next(1000));
            }
            var tags = _tags.Select(t => new PoolTagEntry(t.Tag, t.Allocs, t.Allocs - 10, (ulong)(t.Bytes * 0.6), t.Allocs / 2, t.Allocs / 2 - 5, (ulong)(t.Bytes * 0.4))).ToList();
            return new Sample(Utc, new PoolTotals(20UL << 30, 6UL << 30, 19UL << 30, 5UL << 30), tags, procs)
            {
                TickMs = Ms, System = new SystemCounters(40UL << 30, 80UL << 30, 10UL << 30, 1UL << 30, 330_000, (uint)procs.Count),
            };
        }

        private string CommandLine(string image)
        {
            int r = _rnd.Next(10);
            int length = r < 7 ? 60 + _rnd.Next(120) : r < 9 ? 250 + _rnd.Next(200) : 700 + _rnd.Next(500);
            var s = new System.Text.StringBuilder($"\"C:\\Program Files\\Git\\usr\\bin\\{image}\" -c \"");
            while (s.Length < length) s.Append("cd /f/project && git status --porcelain; ");
            return s.Append('"').ToString();
        }

        /// <summary>
        /// The events.csv rows of one sample, as the event summary gives them: the minute's rollups, the long-running
        /// starts, the exits of those started 30 s to 10 minutes earlier, orphans and findings.
        /// </summary>
        public List<LifecycleEvent> NextEvents()
        {
            var events = new List<LifecycleEvent>();
            for (_owedStarts += 1940.0 / SamplesPerHour; _owedStarts >= 1; _owedStarts--)
            {
                string image = Short[_rnd.Next(Short.Length)];
                var start = new LifecycleEvent
                {
                    TimeUtc = Utc.AddSeconds(-31), Kind = LifecycleKind.Start, Pid = _nextPid++, CreateTime = Utc.AddSeconds(-31).ToFileTimeUtc(), ParentPid = 1120 + _rnd.Next(30),
                    Image = image, User = "PC\\owner", CommandLine = CommandLine(image),
                };
                events.Add(start);
                _running.Enqueue((start, _sample + 3 + _rnd.Next(60)));
            }
            while (_running.TryPeek(out var r) && r.ExitAt <= _sample)
            {
                _running.Dequeue();
                events.Add(r.Start with { TimeUtc = Utc, Kind = LifecycleKind.Exit, ExitCode = 0, User = null });
            }
            for (_owedOrphans += 540.0 / SamplesPerHour; _owedOrphans >= 1; _owedOrphans--)
            {
                // An orphan is a long-running process that outlived its shell: one of the starts above, still running.
                var o = _running.ElementAt(_rnd.Next(_running.Count)).Start;
                events.Add(o with { TimeUtc = Utc, Kind = LifecycleKind.Orphaned, Detail = $"parent bash.exe ({o.ParentPid}) exited at 23:59:36; still running 30 s later" });
            }
            for (_owedFindings += 210.0 / SamplesPerHour; _owedFindings >= 1; _owedFindings--)
                events.Add(new LifecycleEvent { TimeUtc = Utc, Kind = LifecycleKind.Finding, Image = "SpawnStorm", Detail = "Spawn storm: bash.exe (PID 4242) started 120 short-lived processes in 30 s: git.exe 80, sed.exe 40" });
            return events;
        }

        /// <summary>
        /// The event summary's rollups as each minute closes: 66 a minute (the owner's 94,972 a day), each of one owner,
        /// parent and child image, drawn from 125 of them, so that over 5 minutes they come to about 120 keys: 2.7 times
        /// fewer rows merged than a minute's, as the owner's day had (74,518 parent and child images by the minute, 27,255
        /// by 5 minutes, before the owners split them further).
        /// </summary>
        public List<RollupParts> NextRollups()
        {
            var parts = new List<RollupParts>();
            if (_sample % 6 != 0) return parts;
            var keys = new HashSet<int>();
            while (keys.Count < 66) keys.Add(_rnd.Next(RollupKeys));
            foreach (int k in keys)
            {
                int starts = 1 + _rnd.Next(300);
                var pids = Enumerable.Range(0, 1 + _rnd.Next(40)).Select(i => 1120 + (k + i) % 30).ToHashSet();
                var patterns = new Dictionary<string, int> { [$"bash <path> x{k:x4}-ldb-1 <path> <id> …"] = starts / 2 + 1, ["bash <path> status"] = starts / 3 + 1, [$"{Short[k % Short.Length][..^4]} <n>"] = starts / 6 + 1 };
                // Key k is its owner (k / 8), its parent (by the owner) and its child image (k % 8): one key each.
                parts.Add(new RollupParts($"claude:{4000 + k / 8}", $"Claude Code session {k / 8}", k / 8 % 3 == 0 ? "node.exe" : "bash.exe", Short[k % Short.Length], starts, starts - _rnd.Next(3),
                    pids.First(), pids, Utc.AddSeconds(-59), Utc.AddSeconds(-1), patterns));
            }
            return parts;
        }

        private const int RollupKeys = 125;

        /// <summary>The services, one of which starts, stops or moves to another host about every 2.4 minutes.</summary>
        public IReadOnlyList<ServiceEntry> Services()
        {
            if (_sample % 14 == 0)
            {
                int i = 200 + _rnd.Next(50);
                _services[i] = _services[i] with { ProcessId = _services[i].ProcessId == 0 ? _nextPid++ : 0 };
            }
            return _services.ToList();
        }

        /// <summary>A handle census: every process by type, a third of the counts moving since the last.</summary>
        public HandleCounts Handles(IReadOnlyList<ProcessSample> procs)
        {
            var c = new HandleCounts();
            foreach (var p in procs.Where(p => p.Pid < 20_000).Take(330))
            {
                int types = p.Pid is 4 or 1005 || p.Pid < 1020 ? 25 : 8;
                for (int t = 1; t <= types; t++)
                {
                    int n = (p.Pid * 31 + t * 17) % (p.Pid < 1020 ? 900 : 40) + (_rnd.Next(3) == 0 ? _rnd.Next(1, 30) : 0) + (p.Pid == 1005 ? _sample : 0);
                    for (int k = 0; k < n; k++) c.Add(p.Pid, t);
                }
            }
            return c;
        }

        public static readonly IReadOnlyDictionary<int, string> TypeNames = Enumerable.Range(1, 30).ToDictionary(t => t, t => $"Type{t}");
        public int Sample => _sample;
    }

    /// <summary>Thread CPU of the calling thread, 100 ns ticks.</summary>
    private static long Cpu() => PoolWatch.Native.NativeMethods.CurrentThreadCpuTicks();

    [Fact]
    public void TheDaysCsvFilesUnderTheOwnersAgenticLoadStayWithinTheirShareOfTheWriteBudget()
    {
        var paths = new AppPaths(Path.Combine(_root, "data"), null, secure: false);
        var byFile = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var perHour = new long[4];
        int hour = 0;
        Stream Open(string p, FileMode m, FileAccess a, FileShare s)
        {
            string name = Path.GetFileName(p);
            var fs = new FileStream(p, m, a, s, bufferSize: 0);
            return m == FileMode.Append ? new Counting(fs, n => { byFile[name] = byFile.GetValueOrDefault(name) + n; perHour[hour] += n; }) : fs;
        }
        var world = new AgenticWorld();
        var registry = new ProcessRegistry();
        long cpu = 0, alloc = 0;
        int minuteRollups = 0;
        using (var sink = new CsvSink(paths, new Settings { RecordMode = RecordMode.Detailed }, verbose: false, null, Open))
        {
            sink.MergeRollupsFrom(new EventSummarizer()); // as the engine's sink takes them
            sink.WriteVolumes([new VolumeInfo("C:\\", "NTFS", "Windows", false, "Fixed"), new VolumeInfo("F:\\", "ReFS", "Dev", true, "Fixed")], world.Utc);
            for (int s = 0; s < 4 * AgenticWorld.SamplesPerHour; s++)
            {
                hour = s / AgenticWorld.SamplesPerHour;
                var sample = world.NextSample();
                var rollups = world.NextRollups();
                var events = world.NextEvents();
                var services = world.Services();
                var handles = world.Sample % 30 == 0 ? world.Handles(sample.Processes) : null;
                minuteRollups += rollups.Count;
                long c0 = Cpu(), a0 = GC.GetAllocatedBytesForCurrentThread();
                foreach (var r in rollups) sink.AddRollup(r);
                if (world.Sample % 6 == 0) sink.WriteServices(services, sample.TimeUtc);
                sink.WriteEvents(events, sample.TimeUtc);
                if (handles is not null) sink.WriteHandles(handles, AgenticWorld.TypeNames, sample.Processes, sample.TimeUtc);
                sink.WriteSample(sample, registry);
                sink.WriteLag(new LagProbe(sample.TimeUtc, 1.2, 3.4, 0.8, 0, sample.Totals) { TickMs = sample.TickMs });
                sink.WriteLoad(new LoadPoint(sample.TimeUtc, sample.TickMs, 0.43, 2.5, 32, false), 1200);
                cpu += Cpu() - c0;
                alloc += GC.GetAllocatedBytesForCurrentThread() - a0;
            }
        }
        int rollupRows = Directory.EnumerateFiles(Path.Combine(_root, "data"), "events*.csv", SearchOption.AllDirectories)
            .Sum(f => File.ReadLines(f).Count(l => l.Contains(",Rollup,", StringComparison.Ordinal)));
        output.WriteLine($"rollups: {minuteRollups} minute rollups, {rollupRows} rows");
        Assert.InRange(rollupRows, minuteRollups / 4, minuteRollups / 2); // merged: 2.7 times fewer rows, as the owner's day would have
        foreach (var (file, bytes) in byFile.OrderByDescending(kv => kv.Value)) output.WriteLine($"{file}: {bytes / 4 / 1048576.0:0.000} MB/h");
        for (int h = 0; h < 4; h++) output.WriteLine($"hour {h + 1}: {perHour[h] / 1048576.0:0.000} MB");
        output.WriteLine($"sink CPU {cpu / 1e7:0.00} s in 4 h ({cpu / 1e7 / (4 * 3600) * 100:0.0000}% of one core), allocated {alloc / 1048576.0 / 4:0.0} MB/h");
        string files = string.Join(", ", byFile.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value / 4 / 1048576.0:0.00}"));
        for (int h = 0; h < 4; h++)
            Assert.True(perHour[h] <= ShareMBPerHour * 1048576, $"hour {h + 1} wrote {perHour[h] / 1048576.0:0.00} MB, over the day's CSV files' share of {ShareMBPerHour} MB/h ({files} MB/h)");
        // The sink's own work on the sampler thread: well inside the 0.5% budget (the 4 hours took it this long).
        Assert.True(cpu / 1e7 < 0.05 / 100 * 4 * 3600, $"the sink used {cpu / 1e7:0.00} s of CPU in 4 simulated hours");
    }
}
