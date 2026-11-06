using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using PoolWatch.Native;

namespace PoolWatch.Engine;

/// <summary>
/// Keeps PoolWatch's own footprint small and flat (DESIGN.md section 3: under 100 MB working set, private memory flat).
/// After start-up (30 s, 90 s and 3 min) a compacting full collection of PoolWatch's own heap returns the freed segments,
/// and EmptyWorkingSet sends the start-up pages it no longer touches to the standby list. Then, every 5 minutes, it compacts
/// only when the heap has grown <see cref="GrowthToCompact"/> past what the last full collection left; that compaction
/// empties nothing. Between those, a sample empties the working set alone once it has reached <see cref="EmptyAt"/>
/// (<see cref="ShouldEmpty"/>). With the window open it holds what the window's views and tabs touched since the last
/// empty: in the CI UI soak, with nothing emptied after start-up, 67 to 91 MB over the steady window with the window of
/// 1.0.0-preview.1, and 78 to 101 MB with 1.0.0's, while private memory and the heap stayed the same within 2 MB. The
/// likely reason, not measured by cause, is pages the larger window touches and that stay resident, not memory PoolWatch
/// keeps: the working set less private memory (steady medians) was 14.0 MB with preview.1's window and 22.9 MB with
/// 1.0.0's, and under 1 MB headless in both. It never empties a working set that is mostly heap, and it waits longer
/// after an empty that did not hold (<see cref="NextGap"/>, judged <see cref="JudgeAfter"/> after it, whatever the sample
/// interval): memory in use only faults straight back in (491 MB,
/// then 64 MB, then 477 MB within 15 s on a busy PC), at a cost in CPU each time. When the main window hides
/// (<see cref="Request"/>) the compaction and the empty both run once more: the window's pages and caches really are
/// unused then. The heap each full collection leaves (<see cref="Floors"/>, in profile.json) shows whether memory stays
/// flat. Only ever PoolWatch's own process.
/// </summary>
internal sealed class MemoryTrim
{
    private static readonly TimeSpan[] Early = [TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(90), TimeSpan.FromMinutes(3)];
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(5);
    /// <summary>Growth of the heap past the last full collection that is worth a compacting one.</summary>
    public const long GrowthToCompact = 32L << 20;
    /// <summary>
    /// The working set at which a sample empties it: three quarters of the budget, which leaves a quarter for what comes in
    /// before the next sample looks (a CI UI soak that emptied it every 5 minutes saw it rise by up to 19 MB between two
    /// 30-second readings once the views had all been shown, and faster before).
    /// </summary>
    public const long EmptyAt = SelfMeter.RamBudgetBytes / 4 * 3;
    /// <summary>The time between two empties while each one holds: at most 30 an hour.</summary>
    public static readonly TimeSpan EmptyGap = TimeSpan.FromMinutes(2);
    /// <summary>The longest wait between two empties, reached when they keep coming straight back (<see cref="NextGap"/>).</summary>
    public static readonly TimeSpan MaxEmptyGap = TimeSpan.FromHours(1);
    /// <summary>
    /// When an empty between the checks is judged: the working set is read this long after it, whatever the sample
    /// interval (1 to 300 s). Memory in use comes back fast (491 MB, then 64 MB, then 477 MB within 15 s on a busy PC; what
    /// was back at 10 s was not measured), and what comes back only after this counts as held (<see cref="NextGap"/>). The
    /// pages the window touches come back over minutes (in a CI UI soak, 12 to 45 MB in the minute after an empty).
    /// </summary>
    public static readonly TimeSpan JudgeAfter = TimeSpan.FromSeconds(10);
    /// <summary>Floors kept for profile.json: a day of 5-minute checks.</summary>
    private const int MaxFloors = 288;
    private readonly Func<TimeSpan> _clock;
    private readonly Func<long?> _workingSet;
    private readonly Func<long?> _workingSetNow;
    private readonly Func<long> _heap;
    private readonly Action _empty;
    private readonly Action<TimeSpan, Action>? _after;
    private readonly TimeProvider _time;
    private ITimer? _judgeTimer;
    private readonly Queue<Floor> _floors = new();
    private int _early;
    private TimeSpan _next = Early[0];
    private TimeSpan _emptiedAt;
    /// <summary>The wait before the next empty between the checks (<see cref="NextGap"/>).</summary>
    private TimeSpan _gap = EmptyGap;
    /// <summary>The working set read <see cref="JudgeAfter"/> after the last empty between the checks, until a sample takes it; -1: none.</summary>
    private long _judged = -1;
    private int _requested;
    private long _lastFullGc;

    public MemoryTrim() : this(null, null, null, null) { }

    /// <summary>
    /// PoolWatch's own process, except what a test gives: the time since start, the working set (null when it cannot be
    /// read; the sampler passes the reading its meter took this sample), the heap the last collection left, the empty,
    /// what runs an action once after a delay (a test runs it on its own clock), the working set read at that moment (the
    /// judge of an empty, <see cref="JudgeAfter"/> after it), and, when nothing runs the action, the clock of the timer
    /// that does (the system's; a test gives one that shows how the timer is set).
    /// </summary>
    internal MemoryTrim(Func<TimeSpan>? clock, Func<long?>? workingSet, Func<long>? heap, Action? empty,
        Action<TimeSpan, Action>? after = null, Func<long?>? workingSetNow = null, TimeProvider? time = null)
    {
        var since = Stopwatch.StartNew();
        _clock = clock ?? (() => since.Elapsed);
        _workingSetNow = workingSetNow ?? (() => NativeMethods.GetOwnMemory(out long ws, out _) ? ws : null);
        _workingSet = workingSet ?? _workingSetNow;
        _heap = heap ?? (() => GC.GetGCMemoryInfo(GCKind.Any).HeapSizeBytes);
        _empty = empty ?? (() => EmptyWorkingSet(GetCurrentProcess())); // the pseudo-handle: nothing to close
        _after = after;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// The heap after a full collection: PoolWatch's own compaction, timed when it ran and with private bytes right after
    /// it, or the GC's own, timed when the next 5-minute check saw it (the last of them since the check before).
    /// </summary>
    internal readonly record struct Floor(double Seconds, long HeapBytes, long? PrivateBytes);

    /// <summary>What a sample does: nothing, a compaction, both, or (between the checks) only the empty.</summary>
    internal enum TrimAction { None, Compact, CompactAndEmpty, Empty }

    /// <summary>Compacting collections run (start-up, window hidden, heap grown).</summary>
    public int Compactions { get; private set; }
    /// <summary>Working-set empties run (start-up, window hidden, and each time it was full).</summary>
    public int WorkingSetEmptied { get; private set; }
    /// <summary>Empties between the checks that did not hold: the working set back at <see cref="EmptyAt"/> or more <see cref="JudgeAfter"/> after (profile.json).</summary>
    public int EmptiesNotHeld { get; private set; }

    /// <summary>The floors seen, oldest first (sampler thread; read after it stops or on it).</summary>
    public IReadOnlyCollection<Floor> Floors => _floors;

    /// <summary>Any thread: the next sample compacts the heap and empties the working set (the main window has hidden).</summary>
    public void Request() => Interlocked.Exchange(ref _requested, 1);

    /// <summary>
    /// After start-up and when asked, both; otherwise a compaction only when the heap the last collection left
    /// (<paramref name="heapBytes"/>) has grown <see cref="GrowthToCompact"/> past the last full one's.
    /// </summary>
    internal static TrimAction Decide(bool release, long heapBytes, long lastFullHeapBytes) =>
        release ? TrimAction.CompactAndEmpty : heapBytes - lastFullHeapBytes >= GrowthToCompact ? TrimAction.Compact : TrimAction.None;

    /// <summary>
    /// On a sample with nothing else to do: empty the working set when it has reached <see cref="EmptyAt"/>,
    /// the last empty was at least <paramref name="gap"/> ago (<see cref="NextGap"/>), and the heap the last collection
    /// left (<paramref name="heapBytes"/>, read only then) is at most half of it, so most of it is pages not touched since.
    /// An empty of a working set that is mostly heap in use would not hold.
    /// </summary>
    internal static bool ShouldEmpty(long workingSetBytes, TimeSpan sinceLastEmpty, TimeSpan gap, Func<long> heapBytes) =>
        workingSetBytes >= EmptyAt && sinceLastEmpty >= gap && heapBytes() <= workingSetBytes / 2;

    /// <summary>
    /// The wait before the next empty, from the working set read <see cref="JudgeAfter"/> after an empty
    /// (<paramref name="workingSetAfter"/>): back at <see cref="EmptyAt"/> or more by then, the empty did not hold (memory
    /// in use, not mostly heap, faulted straight back in), so the wait doubles, up to <see cref="MaxEmptyGap"/>; under it,
    /// the empty held and the wait is <see cref="EmptyGap"/> again. Memory in use that is back within
    /// <see cref="JudgeAfter"/> is emptied about once an hour after its first hour, not every 2 minutes for good. Memory in
    /// use that comes back more slowly (under <see cref="EmptyAt"/> at the judge, over it by the time the next empty is
    /// due) counts as held, so it is still emptied every 2 minutes, at most 30 times an hour, each time faulting back in.
    /// </summary>
    internal static TimeSpan NextGap(TimeSpan gap, long workingSetAfter) =>
        workingSetAfter < EmptyAt ? EmptyGap : gap * 2 < MaxEmptyGap ? gap * 2 : MaxEmptyGap;

    /// <summary>The wait before the next empty between the checks, as the last empties set it (tests).</summary>
    internal TimeSpan Gap => _gap;

    /// <summary>The working set the trim reads for a sample (tests: the sampler's trim reads its meter's reading).</summary>
    internal long? WorkingSetRead => _workingSet();

    /// <summary>The working set the judge of an empty reads, read now (tests: the process's own, never the sample's reading).</summary>
    internal long? JudgeRead => _workingSetNow();

    /// <summary>Sampler thread, after the sample's handlers.</summary>
    public void MaybeRun()
    {
        bool requested = Interlocked.Exchange(ref _requested, 0) == 1;
        var t = _clock();
        // The working set the sampler's meter read for this sample (nothing new on the sampling path).
        long? ws = _workingSet();
        // The last empty between the checks, judged by the reading taken a fixed time after it, not by this sample's: a
        // sample can come 1 s or 5 minutes after an empty (the sample interval).
        long judged = Interlocked.Exchange(ref _judged, -1);
        if (judged >= 0)
        {
            _gap = NextGap(_gap, judged);
            if (judged >= EmptyAt) EmptiesNotHeld++;
        }
        bool due = t >= _next;
        var action = TrimAction.None;
        if (due || requested)
        {
            bool startUp = due && _early < Early.Length;
            if (due) _next = ++_early < Early.Length ? Early[_early] : t + Every;
            var full = GC.GetGCMemoryInfo(GCKind.FullBlocking);
            if (full.Index > _lastFullGc) Record(t, full.HeapSizeBytes, null); // a full collection the GC ran on its own
            action = Decide(startUp || requested, _heap(), full.HeapSizeBytes);
        }
        // Any other sample: the heap is read only for a full working set long enough after the last empty. Start-up's
        // own empties (30 s, 90 s, 3 min) are each under the gap apart, so this waits for them to end.
        if (action == TrimAction.None && ws is { } w && ShouldEmpty(w, t - _emptiedAt, _gap, _heap))
            action = TrimAction.Empty;
        if (action == TrimAction.None) return;
        using var stage = Core.Util.Profiler.Stage("tick.trim");
        if (action != TrimAction.Empty)
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            Compactions++;
        }
        if (action != TrimAction.Compact)
        {
            _empty();
            WorkingSetEmptied++;
            _emptiedAt = t;
            if (action == TrimAction.Empty) JudgeLater(); // start-up's and a hide's empties do not set the wait
        }
        // Only a compaction leaves a floor: an empty alone changes no heap.
        if (action != TrimAction.Empty)
            Record(t, GC.GetGCMemoryInfo(GCKind.FullBlocking).HeapSizeBytes, NativeMethods.GetOwnMemory(out _, out long priv) ? priv : null);
    }

    /// <summary>
    /// Reads the working set <see cref="JudgeAfter"/> from now (a timer, off the sampler thread), for the next sample to
    /// judge the empty by: one timer, made once and set again for each empty, which fires once each time.
    /// </summary>
    private void JudgeLater()
    {
        if (_after is { } after) after(JudgeAfter, Judge);
        else (_judgeTimer ??= _time.CreateTimer(_ => Judge(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan)).Change(JudgeAfter, Timeout.InfiniteTimeSpan);
    }

    private void Judge()
    {
        try { if (_workingSetNow() is { } ws) Interlocked.Exchange(ref _judged, ws); }
        catch (Exception) { } // on a timer: a reading that fails judges nothing, and never ends the process
    }

    private void Record(TimeSpan t, long heapBytes, long? privateBytes)
    {
        _lastFullGc = GC.GetGCMemoryInfo(GCKind.FullBlocking).Index;
        _floors.Enqueue(new Floor(Math.Round(t.TotalSeconds), heapBytes, privateBytes));
        while (_floors.Count > MaxFloors) _floors.Dequeue();
    }

    [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr process);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
}
