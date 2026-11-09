using System.Text.Json;
using PoolWatch.Engine;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// PoolWatch's own memory trim: a compacting collection only when the heap grew past what the last full collection left;
/// the working set emptied after start-up, when the window hides or another of PoolWatch's windows closes, and, between
/// those, when it has reached three quarters of the budget, not too soon after the last empty and never while it is
/// mostly heap (that faults straight back in), waiting longer after each empty that did not hold, judged 10 s after it
/// whatever the sample interval. The sampler's trim reads the working set its meter read for the sample. The trim acts on
/// the test process itself, never on another one; the working set tests give it their own readings, run its timer on
/// their own clock and count the empties instead.
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

    /// <summary>The clock of the trim's own timer (the system's in the app): it shows how each timer it made is set, and fires one when told.</summary>
    private sealed class TimerClock : TimeProvider
    {
        public readonly List<Timer> Timers = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(() => callback(state), (dueTime, period));
            Timers.Add(timer);
            return timer;
        }

        public sealed class Timer(Action fire, (TimeSpan Due, TimeSpan Period) made) : ITimer
        {
            /// <summary>When it was set to fire and how often: as made, then at each change.</summary>
            public readonly List<(TimeSpan Due, TimeSpan Period)> Set = [made];
            public void Fire() => fire();
            public bool Change(TimeSpan dueTime, TimeSpan period) { Set.Add((dueTime, period)); return true; }
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// The app gives the trim no timer of its own, so the judge runs on the trim's timer: made once, idle, and set for each
    /// empty between the checks to fire once, 10 s after it, never at once and never again by itself. Nothing is read
    /// before it fires; when it fires the working set is read then, and the next sample judges the empty by that reading.
    /// </summary>
    [Fact]
    public void WithNoTimerGivenTheJudgeRunsOnceOnATimerSetForTenSecondsAfterEachEmpty()
    {
        var clock = new Clock();
        var time = new TimerClock();
        int reads = 0;
        var trim = new MemoryTrim(() => clock.Now, () => 80 * MB, () => 10 * MB, () => { }, after: null, workingSetNow: () => { reads++; return 90 * MB; }, time: time);
        foreach (int s in new[] { 30, 90, 180 }) // start-up's empties: not judged
        {
            clock.To(TimeSpan.FromSeconds(s));
            trim.MaybeRun();
        }
        Assert.Empty(time.Timers);
        clock.To(TimeSpan.FromSeconds(300)); // full, 2 minutes after the last empty: an empty between the checks
        trim.MaybeRun();
        var timer = Assert.Single(time.Timers);
        var never = Timeout.InfiniteTimeSpan;
        Assert.Equal([(never, never), (TimeSpan.FromSeconds(10), never)], timer.Set);
        Assert.Equal(0, reads);
        timer.Fire(); // 10 s later
        Assert.Equal(1, reads);
        clock.To(TimeSpan.FromSeconds(310));
        trim.MaybeRun(); // judged by the reading the timer took: back over EmptyAt, so the wait doubles
        Assert.Equal(TimeSpan.FromMinutes(4), trim.Gap);
        Assert.Equal(1, trim.EmptiesNotHeld);
        clock.To(TimeSpan.FromSeconds(300 + 4 * 60)); // the next empty: the same timer, set again
        trim.MaybeRun();
        Assert.Same(timer, Assert.Single(time.Timers));
        Assert.Equal([(never, never), (TimeSpan.FromSeconds(10), never), (TimeSpan.FromSeconds(10), never)], timer.Set);
        Assert.Equal(1, reads);
    }

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

    /// <summary>
    /// What About and the CI read of PoolWatch's own cost (<c>SelfMeter.Report</c>: About, overhead.json, the CI UI test) is
    /// this process's own, as each sample reads it: its CPU time and its working set and private bytes. It was read through
    /// Process.GetCurrentProcess(), whose working set and private bytes come from a snapshot of every process on the
    /// machine. The meter's code (comments out) names no Process at all.
    /// </summary>
    [Fact]
    public void TheMetersReportReadsThisProcessAloneWithNoSnapshotOfEveryProcess()
    {
        var report = new SelfMeter(TimeSpan.FromSeconds(30)).Report();
        long fresh = Environment.WorkingSet;
        Assert.InRange(report.PeakWorkingSetBytes, fresh / 2, fresh * 2); // this process's own, read a moment ago
        Assert.True(report.PeakPrivateBytes > MB, $"the report read {report.PeakPrivateBytes} private bytes");
        string source = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Engine", "SelfMeter.cs"));
        Assert.DoesNotMatch(@"\bProcess\b", ScriptCode.CSharp(source));
        Assert.Equal("public OverheadReport Report() { var (workingSet, privateBytes) = Memory(); return Report(_wall.Elapsed, Cpu(), workingSet, privateBytes, WriteBytes()); }",
            UninstallerTests.OneLine(ScriptCode.CSharp(ScriptCode.CSharpMember(source, "public OverheadReport Report()").Value)));
    }

    /// <summary>
    /// One of PoolWatch's windows other than the main one stops showing (it closed, or hid): the look at the next idle of the
    /// UI thread asks for a release, once. A window that opens, one that stays, and a first look with windows already open
    /// ask for none; two that close between two looks (Settings with its Advanced grid) ask once.
    /// </summary>
    [Fact]
    public void AnotherWindowThatStopsShowingAsksForARelease()
    {
        int shown = 0, released = 0;
        var windows = new OtherWindows(() => shown, () => released++);
        void At(int open) { shown = open; windows.Look(); }
        At(0);
        At(1); // About opens
        At(1); // and stays
        At(2); // Report an issue, opened from it
        Assert.Equal(0, released);
        At(1); // About closes
        Assert.Equal(1, released);
        At(1);
        At(0); // then Report an issue
        Assert.Equal(2, released);
        At(0);
        At(2);
        At(0); // two between two looks
        Assert.Equal(3, released);
        var later = new OtherWindows(() => 3, () => released++);
        later.Look(); // its first look: three already open
        Assert.Equal(3, released);
    }

    /// <summary>
    /// The release another window's closing asks for is the main window's hide's: the trim's request (the engine's
    /// ReleaseMemory, which the tray app gives the look). The next sample compacts the heap and empties the working set,
    /// with no guard of the trim's (here 10 s after start, before its first start-up trim, and with the working set far
    /// under the 75 MB at which a sample empties it alone), once.
    /// </summary>
    [Fact]
    public void AnotherWindowsClosingTrimsTheNextSampleAsTheHideDoes()
    {
        int shown = 0, emptied = 0;
        var trim = new MemoryTrim(() => TimeSpan.FromSeconds(10), () => 20 * MB, () => 10 * MB, () => emptied++);
        var windows = new OtherWindows(() => shown, trim.Request);
        shown = 1;
        windows.Look();
        trim.MaybeRun(); // About shows: nothing
        Assert.Equal((0, 0), (trim.Compactions, emptied));
        shown = 0;
        windows.Look();
        trim.MaybeRun(); // closed: both, at once
        Assert.Equal((1, 1), (trim.Compactions, emptied));
        trim.MaybeRun(); // the request is used up
        Assert.Equal((1, 1), (trim.Compactions, emptied));
    }

    /// <summary>
    /// The look counts a window that shows, never one that does not (hidden, or closed: Application.OpenForms holds a
    /// window from its first showing until it closes) nor the main one. No test may put a window on the desktop, so a
    /// window here "shows" by WinForms' own Visible flag alone, set without a window handle, as
    /// <see cref="WindowFitTests.OwnVisible"/> reads it; none is ever made.
    /// </summary>
    [Fact]
    public void TheLookCountsAWindowThatShowsButNeverTheMainOne()
    {
        using var main = new Form();
        using var other = new Form();
        Assert.False(OtherWindows.Counts(other, main)); // never shown
        Assert.False(OtherWindows.Counts(null, main));
        try
        {
            SetVisible(other, true);
            SetVisible(main, true);
            Assert.True(other.Visible && main.Visible && !other.IsHandleCreated && !main.IsHandleCreated);
            Assert.True(OtherWindows.Counts(other, main));
            Assert.True(OtherWindows.Counts(other, null)); // before the main window is made
            Assert.False(OtherWindows.Counts(main, main));
        }
        finally
        {
            SetVisible(other, false);
            SetVisible(main, false);
        }
        Assert.False(OtherWindows.Counts(other, main)); // hidden again
        Assert.Equal(0, OtherWindows.Shown(Application.OpenForms, null)); // no test shows a window
    }

    /// <summary>WinForms' own Control.SetState(States, bool): found by name, so a WinForms that renames it fails here, by name.</summary>
    private static readonly System.Reflection.MethodInfo SetState = typeof(Control)
        .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        .Single(m => m.Name == "SetState" && m.GetParameters() is [{ ParameterType.IsEnum: true }, { ParameterType: var flag }] && flag == typeof(bool));

    /// <summary>Sets a control's own Visible flag (its States member by name), with no window shown or made.</summary>
    private static void SetVisible(Control c, bool visible) =>
        SetState.Invoke(c, [Enum.Parse(SetState.GetParameters()[0].ParameterType, "Visible"), visible]);

    /// <summary>
    /// The tray app looks at every idle of its UI thread, with the engine's release, which is the trim's request, as the
    /// main window's hide asks for it; and it stops looking as it exits (the event is the thread's, and would hold the app).
    /// The tray app needs the whole app, so its source is read.
    /// </summary>
    [Fact]
    public void TheTrayAppLooksAtEveryIdleWithTheHidesReleaseAndStopsAsItExits()
    {
        string Code(string file, string header) => UninstallerTests.OneLine(ScriptCode.CSharp(ScriptCode.CSharpMember(
            File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", file)), header).Value));
        string tray = Code(Path.Combine("Ui", "TrayApp.cs"), "public TrayApp(");
        Assert.Contains("_otherWindows = new OtherWindows(() => OtherWindows.Shown(Application.OpenForms, _form), engine.ReleaseMemory); " +
                        "_onIdle = (_, _) => _otherWindows.Look(); Application.Idle += _onIdle;", tray);
        Assert.Contains("Application.Idle -= _onIdle;", Code(Path.Combine("Ui", "TrayApp.cs"), "protected override void ExitThreadCore()"));
        Assert.Contains("_engine.ReleaseMemory(); }", Code(Path.Combine("Ui", "MainForm.cs"), "private void ReleaseWhileHidden()"));
        string engine = UninstallerTests.OneLine(ScriptCode.CSharp(File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Engine", "MonitorEngine.cs"))));
        Assert.Contains("public void ReleaseMemory() => Trim.Request();", engine);
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

    /// <summary>
    /// The GC settings in the build's own runtimeconfig (the single-file publish carries the same one inside the exe):
    /// workstation GC without the background thread, the heap compacted harder, and gen0 at most 8 MB, so its size does
    /// not follow the CPU's cache.
    /// </summary>
    [Fact]
    public void TheGcKeepsGen0SmallOnAnyCpu()
    {
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PoolWatch.runtimeconfig.json")));
        var properties = config.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties");
        Assert.Equal(8L << 20, properties.GetProperty("System.GC.Gen0MaxBudget").GetInt64());
        Assert.Equal(7, properties.GetProperty("System.GC.ConserveMemory").GetInt32());
        Assert.Equal(JsonValueKind.False, properties.GetProperty("System.GC.Concurrent").ValueKind);
        Assert.Equal(JsonValueKind.False, properties.GetProperty("System.GC.Server").ValueKind);
    }

    /// <summary>
    /// The profile gives the gen0 cap the GC runs with, as the GC holds it, and the soak prints it, so a CI run shows the
    /// shipped build's cap on the runner itself.
    /// </summary>
    [Fact]
    public void TheProfileAndTheSoakGiveTheGen0CapTheGcRunsWith()
    {
        string source = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Engine", "CostReport.cs"));
        string gc = MainWindowLayoutTests.Block(source, "gc = new");
        Assert.Contains("gen0MaxBudgetMB = GC.GetConfigurationVariables().TryGetValue(\"GCGen0MaxBudget\", out var cap)", gc);
        Assert.True(GC.GetConfigurationVariables().ContainsKey("GCGen0MaxBudget"), "the GC no longer gives GCGen0MaxBudget");
        string soak = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "ci", "soak.ps1"));
        Assert.Contains("gen0 cap {7} MB", soak);
        Assert.Contains("$profile.gc.gen0MaxBudgetMB))", soak);
    }
}
