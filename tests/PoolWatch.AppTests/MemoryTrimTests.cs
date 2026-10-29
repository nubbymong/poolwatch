using PoolWatch.Engine;

namespace PoolWatch.AppTests;

/// <summary>
/// PoolWatch's own memory trim: a compacting collection only when the heap grew past what the last full collection left;
/// the working set emptied after start-up, when the window hides, and, between those, when it has reached three quarters
/// of the budget, not too soon after the last empty and never while it is mostly heap (that faults straight back in),
/// waiting longer after each empty that did not hold, judged 10 s after it whatever the sample interval. The sampler's
/// trim reads the working set its meter read for the sample. The trim acts on the test process itself, never on another
/// one; the working set tests give it their own readings, run its timer on their own clock and count the empties instead.
/// </summary>
public class MemoryTrimTests
{
    private const long MB = 1L << 20;

    /// <summary>
    /// The trim's clock and its timer, on the test's own time: an action due at a time runs when the clock passes it, at
    /// that time, before the sample there.
    /// </summary>
    private sealed class Clock
    {
        private readonly List<(TimeSpan Due, Action Run)> _timers = [];
        public TimeSpan Now { get; private set; }
        public void After(TimeSpan delay, Action run) => _timers.Add((Now + delay, run));

        public void To(TimeSpan t)
        {
            while (_timers.Count > 0 && _timers.MinBy(x => x.Due) is var first && first.Due <= t)
            {
                _timers.Remove(first);
                Now = first.Due;
                first.Run();
            }
            Now = t;
        }
    }

    [Fact]
    public void AtTheFiveMinuteCheckItCompactsOnlyWhenTheHeapGrewAndBothRunWhenReleasing()
    {
        Assert.Equal(MemoryTrim.TrimAction.None, MemoryTrim.Decide(release: false, heapBytes: 100 * MB, lastFullHeapBytes: 70 * MB));
        Assert.Equal(MemoryTrim.TrimAction.Compact, MemoryTrim.Decide(release: false, heapBytes: 102 * MB, lastFullHeapBytes: 70 * MB));
        Assert.Equal(MemoryTrim.TrimAction.CompactAndEmpty, MemoryTrim.Decide(release: true, heapBytes: 70 * MB, lastFullHeapBytes: 70 * MB));
    }

    [Fact]
    public void AWindowThatHidesIsTrimmedOnTheNextSampleAndLeavesAFloor()
    {
        var trim = new MemoryTrim();
        trim.MaybeRun(); // before the first start-up trim (30 s): nothing
        Assert.Equal(0, trim.Compactions);
        trim.Request();
        trim.MaybeRun();
        Assert.Equal(1, trim.Compactions);
        Assert.Equal(1, trim.WorkingSetEmptied);
        var floor = trim.Floors.Last();
        Assert.True(floor.HeapBytes > 0);
        Assert.True(floor.PrivateBytes > 0);
        trim.MaybeRun(); // the request is used up
        Assert.Equal(1, trim.Compactions);
    }

    [Fact]
    public void TheWorkingSetIsEmptiedWhenFullLongEnoughAfterTheLastEmptyAndNotMostlyHeap()
    {
        var gap = MemoryTrim.EmptyGap;
        Assert.Equal(75 * MB, MemoryTrim.EmptyAt); // three quarters of the 100 MB budget
        Assert.Equal(TimeSpan.FromMinutes(2), gap);
        // The CI UI soak with the window open: 78 to 101 MB of working set over a heap of about 15 MB.
        Assert.True(MemoryTrim.ShouldEmpty(100 * MB, gap, gap, () => 15 * MB));
        Assert.True(MemoryTrim.ShouldEmpty(75 * MB, gap, gap, () => 37 * MB));
        Assert.False(MemoryTrim.ShouldEmpty(75 * MB - 1, gap, gap, () => 15 * MB)); // under three quarters of the budget
        Assert.False(MemoryTrim.ShouldEmpty(100 * MB, gap - TimeSpan.FromSeconds(1), gap, () => 15 * MB)); // too soon after the last
        Assert.False(MemoryTrim.ShouldEmpty(100 * MB, TimeSpan.FromMinutes(7), TimeSpan.FromMinutes(8), () => 15 * MB)); // within a longer wait
        // Mostly heap in use: emptying it only made it fault straight back in (491 MB, then 64, then 477 within 15 s).
        Assert.False(MemoryTrim.ShouldEmpty(491 * MB, gap, gap, () => 400 * MB));
        Assert.False(MemoryTrim.ShouldEmpty(90 * MB, gap, gap, () => 46 * MB));
        // The heap is read only for a full working set long enough after the last empty: most samples read nothing new.
        int heapReads = 0;
        long Heap() { heapReads++; return 15 * MB; }
        Assert.False(MemoryTrim.ShouldEmpty(60 * MB, gap, gap, Heap));
        Assert.False(MemoryTrim.ShouldEmpty(100 * MB, TimeSpan.FromSeconds(30), gap, Heap));
        Assert.Equal(0, heapReads);
    }

    [Fact]
    public void AfterStartUpASampleEmptiesAFullWorkingSetAloneAndWaitsTwoMinutesBeforeTheNext()
    {
        var clock = new Clock();
        long workingSet = 90 * MB;
        int emptied = 0;
        // An empty sends the pages not in use away: the next reading is a small working set, which then grows again.
        var trim = new MemoryTrim(() => clock.Now, () => workingSet, () => 15 * MB, () => { emptied++; workingSet = 20 * MB; }, clock.After, () => workingSet);
        void At(int seconds) { clock.To(TimeSpan.FromSeconds(seconds)); trim.MaybeRun(); }

        At(20); // start-up, before its first trim: a full working set waits for it (the gap counts from the start)
        Assert.Equal(0, emptied);
        foreach (int s in new[] { 30, 90, 180 }) At(s); // the start-up trims, each with its compaction and empty
        Assert.Equal(3, emptied);
        Assert.Equal(3, trim.Compactions);
        workingSet = 90 * MB;
        At(290); // full, under 2 minutes after the 180 s empty
        Assert.Equal(3, emptied);
        int floors = trim.Floors.Count;
        At(300); // full, 2 minutes on, between the 5-minute checks (the next is at 480 s): the empty alone
        Assert.Equal(4, emptied);
        Assert.Equal(4, trim.WorkingSetEmptied);
        Assert.Equal(3, trim.Compactions);
        Assert.Equal(floors, trim.Floors.Count); // an empty alone changes no heap, so it leaves no floor
        At(310); // read 10 s after it: the empty held, so the next may come 2 minutes after it
        Assert.Equal(MemoryTrim.EmptyGap, trim.Gap);
        Assert.Equal(0, trim.EmptiesNotHeld);
        workingSet = 90 * MB;
        At(410); // full again, too soon
        Assert.Equal(4, emptied);
        workingSet = 75 * MB - 1;
        At(430); // under three quarters of the budget
        Assert.Equal(4, emptied);
        workingSet = 75 * MB;
        At(440);
        Assert.Equal(5, emptied);
    }

    [Fact]
    public void AnEmptyThatComesStraightBackDoublesTheWaitUpToAnHourAndOneThatHoldsBringsBackTwoMinutes()
    {
        var two = MemoryTrim.EmptyGap;
        Assert.Equal(TimeSpan.FromHours(1), MemoryTrim.MaxEmptyGap);
        Assert.Equal(two, MemoryTrim.NextGap(two, 75 * MB - 1));                        // under three quarters of the budget: it held
        Assert.Equal(TimeSpan.FromMinutes(4), MemoryTrim.NextGap(two, 75 * MB));        // back at once: wait twice as long
        Assert.Equal(TimeSpan.FromHours(1), MemoryTrim.NextGap(TimeSpan.FromMinutes(32), 300 * MB)); // never more than an hour
        Assert.Equal(TimeSpan.FromHours(1), MemoryTrim.NextGap(TimeSpan.FromHours(1), 300 * MB));
        Assert.Equal(two, MemoryTrim.NextGap(TimeSpan.FromHours(1), 20 * MB));

        // A busy PC whose working set is memory in use and not mostly heap (300 MB over a 10 MB heap): it is back at once
        // after every empty, which stops it being emptied every 2 minutes for good.
        var clock = new Clock();
        long workingSet = 300 * MB;
        bool holds = false;
        var at = new List<double>();
        var trim = new MemoryTrim(() => clock.Now, () => workingSet, () => 10 * MB, () =>
        {
            at.Add(clock.Now.TotalMinutes);
            if (holds) workingSet = 20 * MB;
        }, clock.After, () => workingSet);
        void Run(int from, int to) { for (int s = from; s <= to; s += 10) { clock.To(TimeSpan.FromSeconds(s)); trim.MaybeRun(); } }
        Run(0, 3 * 3600);
        // Start-up's three (30 s, 90 s, 3 min) do not set the wait; then 2, 4, 8, 16, 32 and 60 minutes apart.
        Assert.Equal([0.5, 1.5, 3, 5, 9, 17, 33, 65, 125], at);
        Assert.Equal(TimeSpan.FromHours(1), trim.Gap);
        Assert.Equal(6, trim.EmptiesNotHeld); // every empty between the checks; start-up's are not judged

        // The PC calms down: the next empty holds, and the one after may come 2 minutes later again.
        holds = true;
        Run(3 * 3600 + 10, 185 * 60 + 10);
        Assert.Equal(185, at[^1]);
        Assert.Equal(two, trim.Gap);
        workingSet = 90 * MB;
        Run(185 * 60 + 20, 188 * 60);
        Assert.Equal(187, at[^1]);
        Assert.Equal(6, trim.EmptiesNotHeld);
    }

    /// <summary>
    /// An empty between the checks is judged by the working set read 10 s after it, whatever the sample interval (the
    /// setting allows 1 to 300 s). Judged by the next sample instead, a 300 s interval read the window's pages already back
    /// and backed off for an empty that held, and a 1 s interval read memory in use before it came back and never backed
    /// off. Either way there are at most 30 an hour.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(300)]
    public void AnEmptyIsJudgedTenSecondsAfterItAtEverySampleInterval(int interval)
    {
        Assert.Equal(TimeSpan.FromSeconds(10), MemoryTrim.JudgeAfter);
        // The window open: an empty leaves 20 MB, and the pages the window touches come back at 19 MB per 30 s (the
        // fastest steady rise a CI UI soak saw), up to 100 MB. Each empty holds, so they come every 2 minutes, or at every
        // sample when samples are further apart.
        var (window, windowAt) = Run(interval, since => Math.Min(100 * MB, 20 * MB + 19 * MB * (long)since.TotalSeconds / 30));
        Assert.Equal(0, window.EmptiesNotHeld);
        Assert.Equal(MemoryTrim.EmptyGap, window.Gap);
        Assert.Equal(interval == 300 ? 12 : 30, InHour(windowAt, 2));
        // A busy PC: 300 MB in use (over a 10 MB heap), back 5 s after each empty. No empty holds: the wait doubles to an
        // hour, and the third hour has one empty.
        var (busy, busyAt) = Run(interval, since => since < TimeSpan.FromSeconds(5) ? 20 * MB : 300 * MB);
        Assert.Equal(busyAt.Count - 3, busy.EmptiesNotHeld); // all but start-up's three, which are not judged
        Assert.Equal(6, busy.EmptiesNotHeld);
        Assert.Equal(MemoryTrim.MaxEmptyGap, busy.Gap);
        Assert.Equal(1, InHour(busyAt, 2));
        foreach (var at in new[] { windowAt, busyAt })
            for (int hour = 0; hour < 3; hour++) Assert.InRange(InHour(at, hour), 0, 30);
    }

    /// <summary>
    /// Three hours of samples every <paramref name="interval"/> seconds, with a working set that follows the time since
    /// the last empty, over a 10 MB heap; the trim's timer runs on the same clock. As in the app, the sample's reading is
    /// the meter's, taken at each sample and held until the next, and the judge reads the working set at its own time:
    /// 10 s after an empty with no sample between, the meter still holds the reading that started the empty (75 MB or
    /// more), so a judge that read it would count every empty as not held. The times of the empties after start-up's.
    /// </summary>
    private static (MemoryTrim Trim, List<TimeSpan> Empties) Run(int interval, Func<TimeSpan, long> workingSetSince)
    {
        var clock = new Clock();
        var empties = new List<TimeSpan>();
        var last = TimeSpan.Zero;
        long WorkingSet() => workingSetSince(clock.Now - last);
        long meter = 0;
        var trim = new MemoryTrim(() => clock.Now, () => meter, () => 10 * MB, () => { empties.Add(clock.Now); last = clock.Now; }, clock.After, () => WorkingSet());
        for (int s = interval; s <= 3 * 3600; s += interval)
        {
            clock.To(TimeSpan.FromSeconds(s));
            meter = WorkingSet(); // the sample's own reading, before the trim looks
            trim.MaybeRun();
        }
        Assert.Equal(3, trim.Compactions); // start-up's, each with an empty
        return (trim, empties);
    }

    /// <summary>The empties after start-up's three in hour <paramref name="hour"/> (after its start, up to its end).</summary>
    private static int InHour(List<TimeSpan> empties, int hour) =>
        empties.Skip(3).Count(t => t > TimeSpan.FromHours(hour) && t <= TimeSpan.FromHours(hour + 1));

    [Fact]
    public void TheProfileCountsTheEmptiesThatDidNotHold()
    {
        string source = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Engine", "CostReport.cs"));
        string memory = MainWindowLayoutTests.Block(source, "memory = new");
        Assert.Contains("emptiesNotHeld = engine.Trim.EmptiesNotHeld,", memory);
    }

    /// <summary>
    /// The engine gives the trim the meter's reading for its checks and nothing for the judge, so the judge reads the
    /// working set itself 10 s after an empty (<see cref="AnEmptyIsJudgedTenSecondsAfterItAtEverySampleInterval"/> shows
    /// why the meter's reading will not do). The engine needs the whole app to sample, so its source is read.
    /// </summary>
    [Fact]
    public void TheEngineLeavesTheJudgeToReadTheWorkingSetItself()
    {
        string source = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Engine", "MonitorEngine.cs"));
        var wiring = source.Split('\n').Select(l => l.Trim()).Where(l => l.Contains("new MemoryTrim(", StringComparison.Ordinal)).ToList();
        Assert.Equal(["Trim = new MemoryTrim(clock: null, workingSet: () => Meter.WorkingSetNow, heap: null, empty: null);"], wiring);
    }

    /// <summary>
    /// A trim given the sample's reading and nothing for the judge, as the engine builds it, judges an empty by a reading of
    /// its own, taken when it judges: this process's working set, never the sample's. A default that fell back on the
    /// sample's reading would hand the judge the meter's again, the reading that started the empty.
    /// </summary>
    [Fact]
    public void ATrimGivenOnlyTheSamplesReadingJudgesByAFreshReadOfItsOwn()
    {
        var trim = new MemoryTrim(clock: null, workingSet: () => 1, heap: null, empty: null);
        Assert.Equal(1, trim.WorkingSetRead); // the sample's reading, as given
        long fresh = Environment.WorkingSet;
        Assert.True(trim.JudgeRead > MB, $"the judge read {trim.JudgeRead} bytes");
        Assert.InRange(trim.JudgeRead!.Value, fresh / 2, fresh * 2); // this process's own, read now
    }

    [Fact]
    public void TheMeterKeepsTheWorkingSetItReadForEachSample()
    {
        var meter = new SelfMeter(TimeSpan.FromSeconds(30));
        Assert.Equal(0, meter.WorkingSetNow); // nothing read before its first sample
        meter.Tick();
        long fresh = Environment.WorkingSet;
        Assert.True(meter.WorkingSetNow > MB, $"the meter kept {meter.WorkingSetNow} bytes");
        Assert.InRange(meter.WorkingSetNow, fresh / 2, fresh * 2); // this process's own, read a moment ago
    }

    [Fact]
    public void TheSamplersTrimReadsTheWorkingSetItsMeterReadForTheSample()
    {
        string root = Path.Combine(Path.GetTempPath(), "pw-trim-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(Path.Combine(root, "data"), null, secure: false);
            // Never started: no sampler thread and no trace, only the meter and the trim the engine wires together.
            using var engine = new MonitorEngine(new PoolWatch.Core.Settings(), paths, new Log(Path.Combine(paths.DataRoot, "test.log")), verboseCsv: false);
            Assert.Equal(0, engine.Trim.WorkingSetRead); // the meter's reading, none before its first sample: not one of its own
            Assert.True(engine.Trim.JudgeRead > MB, $"the judge read {engine.Trim.JudgeRead} bytes"); // its own, not the meter's
            engine.Meter.Tick();
            Assert.True(engine.Meter.WorkingSetNow > MB);
            Assert.Equal(engine.Meter.WorkingSetNow, engine.Trim.WorkingSetRead);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
