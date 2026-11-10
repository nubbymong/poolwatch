using System.Diagnostics;
using System.Globalization;
using PoolWatch.Core.Model;
using PoolWatch.Core.Util;

namespace PoolWatch.Engine;

/// <summary>
/// Writes each handle scan to handles.csv and system-handles.csv on a thread of its own, so the sampler's tick only hands
/// the scan over (<see cref="CsvSink.PostHandles"/>) and never waits for that write. In 1.0.0-beta.2 one scan's CSV stage
/// on the sampler thread took 41.9 s of wall time, 24 s of it the thread's CPU, while the same files written at every
/// sample never took more than 0.2 s: what took the time was not found (that build had no finer stages), so it is kept
/// off the tick whatever it was, and a slow write now says where its time went (<see cref="SlowWrite"/>).
///
/// It writes through a <see cref="CsvSink"/> of its own (the day's two files, their rows kept while a file cannot take
/// them, up to <see cref="CsvSink.HandleKeptChars"/>), used by its thread alone. A scan's census, type names and process
/// list are not changed after the scan, so it reads them as they are. At most one scan waits while one is written: a scan
/// handed over while one already waits takes its place, and the one it replaces is not written, counted
/// (<see cref="Replaced"/>, profile.json) and logged. The next scan it writes writes each count that changed since that
/// count's last row, so the file's last row of each count stays true; the replaced scan's totals row is what is missing.
///
/// Its thread lets go of the two files itself, as it ends (<see cref="Ended"/>), so a stop never has to touch a sink that
/// may still be in use: the end of a run waits for that, within <see cref="StopWait"/> (<see cref="Dispose"/>); Detailed
/// recording turned off does not wait at all (<see cref="LetGo"/>), as the sampler's tick never waits for a write.
/// </summary>
internal sealed class HandleCsvWriter : IDisposable
{
    /// <summary>One handle scan as the sampler took it.</summary>
    internal readonly record struct Scan(HandleCounts Counts, IReadOnlyDictionary<int, string> TypeNames, IReadOnlyList<ProcessSample> Processes, DateTime Utc);

    /// <summary>A write this long or longer is logged with its thread's CPU (kernel and user), the GCs and page faults during it, and its two parts.</summary>
    internal static readonly TimeSpan SlowWrite = TimeSpan.FromSeconds(5);

    /// <summary>How long <see cref="Dispose"/> waits for the write in progress and the scan waiting.</summary>
    internal static readonly TimeSpan StopWait = TimeSpan.FromSeconds(10);

    private readonly CsvSink _sink;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private Scan? _waiting;
    private bool _stopping;
    private Thread? _thread;
    private long _replaced;
    private volatile bool _ended;

    /// <param name="sink">The sink the scans are written through (its two handle files only); this writer's thread alone uses it, and disposes it.</param>
    public HandleCsvWriter(CsvSink sink, Action<string> log)
    {
        _sink = sink;
        _log = log;
    }

    /// <summary>Scans not written: each replaced by a later one while it waited.</summary>
    public long Replaced => Interlocked.Read(ref _replaced);

    /// <summary>Stopped, and its files let go of: its thread has ended (or never started), its last write with it.</summary>
    public bool Ended => _ended;

    /// <summary>Hands <paramref name="scan"/> to the writer's thread (started at the first) and returns at once. Not after <see cref="Dispose"/>.</summary>
    public void Post(Scan scan)
    {
        Scan? replaced;
        lock (_gate)
        {
            if (_stopping) return;
            replaced = _waiting;
            _waiting = scan;
            if (_thread is null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "PoolWatch handle CSV", Priority = ThreadPriority.BelowNormal };
                _thread.Start();
            }
            Monitor.PulseAll(_gate);
        }
        if (replaced is { } r)
        {
            long n = Interlocked.Increment(ref _replaced);
            _log($"handles.csv: the handle scan of {r.Utc.ToString("HH:mm:ss", CultureInfo.InvariantCulture)} UTC was not written: the one before it was still being written ({n:N0} so far)");
        }
    }

    private void Run()
    {
        try
        {
            while (true)
            {
                Scan scan;
                lock (_gate)
                {
                    while (_waiting is null && !_stopping) Monitor.Wait(_gate);
                    if (_waiting is not { } next) return; // stopping, and nothing waits
                    scan = next;
                    _waiting = null;
                }
                try { Write(scan); }
                catch (Exception e) { _log($"handles.csv: the handle scan of {scan.Utc.ToString("HH:mm:ss", CultureInfo.InvariantCulture)} UTC could not be written: {e.Message}"); }
            }
        }
        finally { Release(); } // its thread alone uses the sink: it lets go of the files once its last write has ended
    }

    /// <summary>Lets go of the two files (<see cref="CsvSink.Dispose"/>), once: by the thread as it ends, or by the stop when there was none.</summary>
    private void Release()
    {
        try { _sink.Dispose(); }
        catch (Exception e) { _log("handles.csv: its files could not be let go of: " + e.Message); }
        _ended = true;
    }

    /// <summary>Stops taking scans (once): the thread to wait for, or null when there is none (its files are let go of here).</summary>
    private Thread? Stop(bool dropWaiting)
    {
        Thread? thread;
        lock (_gate)
        {
            if (_stopping) return _thread; // stopped already: the same thread, if any, still ends (its files are its own)
            _stopping = true;
            if (dropWaiting) _waiting = null;
            thread = _thread;
            Monitor.PulseAll(_gate);
        }
        if (thread is null) Release(); // never started: nothing else uses the sink
        return thread;
    }

    /// <summary>
    /// Detailed recording was turned off: stops at once, without waiting. The scan waiting is not written; a write going on
    /// ends on its own thread, which then lets go of the two files (<see cref="Ended"/>). Sampler thread.
    /// </summary>
    public void LetGo() => Stop(dropWaiting: true);

    private void Write(Scan s)
    {
        long began = Stopwatch.GetTimestamp();
        var (kernel0, user0) = Native.NativeMethods.CurrentThreadTimes();
        long faults0 = Native.NativeMethods.OwnPageFaults();
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        var paused0 = GC.GetTotalPauseDuration();
        (TimeSpan Rows, TimeSpan Commit) parts;
        using (Profiler.Stage("csv.handles")) parts = _sink.WriteHandles(s.Counts, s.TypeNames, s.Processes, s.Utc);
        var wall = Stopwatch.GetElapsedTime(began);
        if (wall < SlowWrite) return;
        var (kernel, user) = Native.NativeMethods.CurrentThreadTimes();
        _log(string.Create(CultureInfo.InvariantCulture,
            $"handles.csv: one handle scan's write took {wall.TotalSeconds:0.0} s ({parts.Rows.TotalSeconds:0.0} s making its rows, {parts.Commit.TotalSeconds:0.0} s writing its two files): " +
            $"its thread's CPU {(kernel - kernel0) / 1e7:0.0} s in the kernel and {(user - user0) / 1e7:0.0} s in PoolWatch; during it {GC.CollectionCount(0) - gen0} gen0, " +
            $"{GC.CollectionCount(1) - gen1} gen1 and {GC.CollectionCount(2) - gen2} gen2 GCs pausing {(GC.GetTotalPauseDuration() - paused0).TotalMilliseconds:0} ms, " +
            $"and {Native.NativeMethods.OwnPageFaults() - faults0:N0} page faults in all of PoolWatch"));
    }

    /// <summary>
    /// The end of a run: writes the scan waiting, if one does, after the one in progress, then lets go of the files
    /// (<see cref="CsvSink.Dispose"/>, by its thread as it ends), waiting at most <see cref="StopWait"/>; after
    /// <see cref="LetGo"/>, waits for the write it left going on the same way. A write still going on then is left to end on
    /// its own and the stop does not touch its sink (it may still be in use; its thread lets go of it as it ends): the stop
    /// neither writes nor counts what it had not written yet, and the log says so.
    /// </summary>
    public void Dispose()
    {
        if (Stop(dropWaiting: false) is { } thread && !thread.Join(StopWait))
            _log($"handles.csv: the handle scan's write did not end within {StopWait.TotalSeconds:0} s of the stop: the stop neither writes nor counts the rows it had not written yet");
    }
}
