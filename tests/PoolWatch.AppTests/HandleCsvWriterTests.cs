using System.Diagnostics;
using PoolWatch.Core;
using PoolWatch.Core.Model;
using PoolWatch.Core.Util;
using PoolWatch.Engine;
using Xunit.Abstractions;

namespace PoolWatch.AppTests;

/// <summary>
/// The handle scan's CSV write is off the sampler's tick (<see cref="CsvSink.PostHandles"/>, <see cref="HandleCsvWriter"/>):
/// in 1.0.0-beta.2 one scan's CSV stage took 41.9 s of the sampler thread, 24 s of it CPU. The tick hands the scan over
/// and goes on while it is written; a scan handed over while another waits replaces it, counted and logged; the stop
/// writes what waits. And a write of an owner-sized census (about 600 processes, 2 million handles) is measured.
/// </summary>
public sealed class HandleCsvWriterTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pw-handlecsv-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private AppPaths Paths() => new(Path.Combine(_root, "data"), null, secure: false);

    private List<List<string>> Rows(string name) =>
        Directory.EnumerateFiles(Path.Combine(_root, "data"), name, SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .SelectMany(f =>
            {
                using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return new StreamReader(s).ReadToEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
            }).Select(Csv.Split).ToList();

    private static readonly Dictionary<int, string> Names = new() { [1] = "File", [2] = "Event" };

    private static HandleCounts Census(int file)
    {
        var c = new HandleCounts();
        for (int i = 0; i < file; i++) c.Add(500, 1);
        return c;
    }

    private static readonly ProcessSample[] Procs = [new() { Pid = 500, CreateTime = 5, ImageName = "app.exe" }];

    /// <summary>A stream whose writes wait at <paramref name="gate"/> (an open file another program holds up, a slow disk).</summary>
    private sealed class HeldUp(Stream inner, ManualResetEventSlim gate, ManualResetEventSlim entered) : Stream
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
            entered.Set();
            gate.Wait(TimeSpan.FromSeconds(60));
            inner.Write(buffer);
        }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    private Func<string, FileMode, FileAccess, FileShare, Stream> HoldingUpHandles(ManualResetEventSlim gate, ManualResetEventSlim entered) => (p, m, a, s) =>
    {
        var fs = new FileStream(p, m, a, s, bufferSize: 0);
        return m == FileMode.Append && Path.GetFileName(p) == "handles.csv" ? new HeldUp(fs, gate, entered) : fs;
    };

    /// <summary>
    /// While handles.csv's write is held up, handing the scan over returns at once and the samples' files go on being
    /// written; the scan's rows are written once the write can go on.
    /// </summary>
    [Fact]
    public void TheTickHandsTheScanOverAndGoesOnWhileItIsWritten()
    {
        using var gate = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        using var handedOver = new ManualResetEventSlim(false);
        var sink = new CsvSink(Paths(), new Settings(), verbose: false, null, HoldingUpHandles(gate, entered));
        try
        {
            // The sampler's tick, on a thread of its own here so that a hand-over that waited for the write fails the test
            // rather than holding it up.
            new Thread(() => { sink.PostHandles(Census(40), Names, Procs, Now); handedOver.Set(); }) { IsBackground = true }.Start();
            Assert.True(handedOver.Wait(TimeSpan.FromSeconds(5)), "handing the scan over waited for its write");
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the scan's write began on its own thread");
            sink.WriteSample(new Sample(Now, default, [], []) { TickMs = 0 }, new ProcessRegistry());
            Assert.Single(Rows("totals.csv"));
            Assert.Empty(Rows("handles.csv"));
        }
        finally { gate.Set(); }
        sink.Dispose();
        Assert.Equal(["-1:File:40", "500:File:40"], Rows("handles.csv").Select(r => $"{r[1]}:{r[3]}:{r[4]}"));
    }

    /// <summary>
    /// A scan handed over while one already waits behind a write in progress takes its place: the one it replaces is not
    /// written, and that is counted (profile.json's handleScansNotWritten) and logged, never silent.
    /// </summary>
    [Fact]
    public void AScanHandedOverWhileOneWaitsReplacesItCountedAndLogged()
    {
        using var gate = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        var log = new List<string>();
        var sink = new CsvSink(Paths(), new Settings(), verbose: false, l => { lock (log) log.Add(l); }, HoldingUpHandles(gate, entered));
        try
        {
            sink.PostHandles(Census(40), Names, Procs, Now);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            sink.PostHandles(Census(50), Names, Procs, Now.AddMinutes(5)); // waits
            sink.PostHandles(Census(60), Names, Procs, Now.AddMinutes(10)); // replaces it
            Assert.Equal(1, sink.HandleScansNotWritten);
            lock (log) Assert.Contains("handles.csv: the handle scan of 10:05:00 UTC was not written: the one before it was still being written (1 so far)", log);
        }
        finally { gate.Set(); }
        sink.Dispose();
        Assert.Equal(["00:-1:40", "00:500:40", "10:-1:60", "10:500:60"], Rows("handles.csv").Select(r => $"{r[0][14..16]}:{r[1]}:{r[4]}"));
    }

    /// <summary>
    /// The stop (the sink's Dispose, which lets go of the handle writer first) writes the scan that waits behind a write in
    /// progress, after it; a scan handed over after the stop is not written and nothing waits for it.
    /// </summary>
    [Fact]
    public void TheStopWritesTheScanThatWaits()
    {
        using var gate = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        using var stopped = new ManualResetEventSlim(false);
        var sink = new CsvSink(Paths(), new Settings(), verbose: false, null, HoldingUpHandles(gate, entered));
        try
        {
            sink.PostHandles(Census(40), Names, Procs, Now);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            sink.PostHandles(Census(50), Names, Procs, Now.AddMinutes(5)); // waits behind the first
            new Thread(() => { sink.Dispose(); stopped.Set(); }) { IsBackground = true }.Start();
            Assert.False(stopped.Wait(TimeSpan.FromMilliseconds(300)), "the stop waits for the write in progress");
        }
        finally { gate.Set(); }
        Assert.True(stopped.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(["00:-1:40", "00:500:40", "05:-1:50", "05:500:50"], Rows("handles.csv").Select(r => $"{r[0][14..16]}:{r[1]}:{r[4]}"));
        sink.PostHandles(Census(60), Names, Procs, Now.AddMinutes(10));
        Assert.Equal(4, Rows("handles.csv").Count);
    }

    /// <summary>
    /// An owner-sized census (the System process with 1.4 million handles, 600 other processes with about 1,000 to 2,000
    /// each, 2 million in all, 820 rows picked): the day's first scan (every row) and the next (a third of the processes'
    /// File counts moved) each cost under 250 ms of the writing thread's CPU and under 2 s of wall time; the tick's
    /// hand-over under 50 ms.
    /// </summary>
    [Fact]
    public void AnOwnerSizedCensusIsWrittenWithinItsBudget()
    {
        // The first 20 hold 1,000 Event handles and more each, so they (and the System process) are the 20 with the most in
        // both scans; of the rest, every third one's File count moves in the second.
        static bool Moves(int p) => p >= 20 && p % 3 == 0;
        HandleCounts Owner(int shift)
        {
            var c = new HandleCounts();
            for (int type = 1; type <= 10; type++)
                for (int i = 0; i < 140_000; i++) c.Add(4, type);
            for (int p = 0; p < 600; p++)
            {
                int pid = 4_000_000 + 4 * p;
                for (int i = 0; i < 600 + (Moves(p) ? shift : 0); i++) c.Add(pid, 1); // File: each holds 500 or more
                for (int i = 0; i < (p < 20 ? 1000 + p : 40); i++) c.Add(pid, 2);
                for (int type = 3; type <= 12; type++)
                    for (int i = 0; i < 40; i++) c.Add(pid, type);
            }
            return c;
        }
        var first = Owner(0);
        var second = Owner(7);
        Assert.InRange(first.TotalHandles, 2_000_000, 2_050_000);
        var procs = Enumerable.Range(0, 600).Select(p => new ProcessSample { Pid = 4_000_000 + 4 * p, CreateTime = 77 + p, ImageName = p % 2 == 0 ? "node.exe" : "bash.exe" })
            .Append(new ProcessSample { Pid = 4, ImageName = "System" }).ToArray();
        var names = Enumerable.Range(1, 40).ToDictionary(t => t, t => $"Type{t}");
        using var sink = new CsvSink(Paths(), new Settings(), verbose: false);
        foreach (var (census, at, what) in new[] { (first, Now, "first"), (second, Now.AddMinutes(5), "second") })
        {
            long cpu0 = PoolWatch.Native.NativeMethods.CurrentThreadCpuTicks();
            var wall = Stopwatch.StartNew();
            var (rows, commit) = sink.WriteHandles(census, names, procs, at);
            wall.Stop();
            double cpuMs = (PoolWatch.Native.NativeMethods.CurrentThreadCpuTicks() - cpu0) / 10_000.0;
            output.WriteLine($"{what} scan: {cpuMs:0.0} ms CPU, {wall.Elapsed.TotalMilliseconds:0.0} ms wall ({rows.TotalMilliseconds:0.0} making rows, {commit.TotalMilliseconds:0.0} writing)");
            Assert.True(cpuMs < 250, $"{what} scan: {cpuMs:0.0} ms of CPU");
            Assert.True(wall.Elapsed < TimeSpan.FromSeconds(2), $"{what} scan: {wall.Elapsed.TotalMilliseconds:0} ms");
        }
        var handOver = Stopwatch.StartNew();
        sink.PostHandles(first, names, procs, Now.AddMinutes(10));
        handOver.Stop();
        output.WriteLine($"hand-over: {handOver.Elapsed.TotalMilliseconds:0.00} ms");
        Assert.True(handOver.Elapsed < TimeSpan.FromMilliseconds(50), $"hand-over {handOver.Elapsed.TotalMilliseconds:0.0} ms");
        var written = Rows("handles.csv").Where(r => r[1] != "-1").ToList();
        // The System process's 10 types, the 19 others among the 20 with the most by all 12, the 20th by its two of 500 or
        // more, and the other 580 by File.
        Assert.Equal(10 + 19 * 12 + 2 + 580, written.Count(r => r[0].StartsWith("2026-11-09T10:00:", StringComparison.Ordinal)));
        Assert.Equal(Enumerable.Range(0, 600).Count(Moves), written.Count(r => r[0].StartsWith("2026-11-09T10:05:", StringComparison.Ordinal)));
    }
}
