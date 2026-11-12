using PoolWatch.Core;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Identity;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Core.Spawn;
using PoolWatch.Core.Tags;
using Xunit.Abstractions;

using static PoolWatch.Tests.Clocks;

namespace PoolWatch.Tests;

/// <summary>
/// A day of the maintainer's agentic load (<see cref="AgentLoadWorld"/>: about 600 process starts a sample, Git Bash fork
/// copies among them, 600 long-running processes) through every per-sample part of PoolWatch's core, as the sampler runs
/// them: the registry's snapshot, the event summarizer, the history, the detector, the incidents (10 growing all the time,
/// beside them 10 more an hour that open and close, all day) and the diagnostics' spawn, token, load, cost, filter, census
/// and snapshot parts. On 9144b20's code the heap these parts hold rose from 45 MB at half an hour to 101 MB at 4 hours, and
/// they spent 2.6% of one core and allocated 40 MB a sample; beta.2 on the maintainer's PC held 157 MB of heap after
/// 4 hours. The budget is under 0.5% of one core and under 100 MB of working set (DESIGN.md section 3).
/// </summary>
[Collection(FootprintCollection.Name)]
public sealed class AgentLoadTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pw-agentload-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

    /// <summary>This thread's CPU time so far (100 ns ticks); 0 off Windows.</summary>
    private static long ThreadCpu() => OperatingSystem.IsWindows() && GetThreadTimes(GetCurrentThread(), out _, out _, out long k, out long u) ? k + u : 0;

    private sealed class Stage
    {
        public long Cpu, Alloc;
    }

    /// <summary>
    /// The heap these parts may hold under this load (after a compacting full collection, less what the test held before
    /// it): measured 35 to 36 MB at 4 hours and 36 to 40 MB from 6 hours to the day's end (Release, both ends of each
    /// incident's window 500 events; 37 to 43 MB with 1,000 at each end; each with BothEndsTests and WriteBudgetTests in
    /// the same run: this test alone in its process reads about 2 MB more at every hour, 37 MB at 4 hours and 38 to
    /// 42 MB after, for the same code), the raw history (15,000 processes'
    /// tracks), the 90 minutes of events, 32 incidents and the registry's records held. PoolWatch's own trim empties its
    /// working set only while the heap is at most half of the 75 MB it empties at (MemoryTrim), and the window and the runtime
    /// hold about 70 MB of working set beside a 15 MB heap in the CI soak: the target is about 37 MB in all, this load's
    /// parts and the app's own; this ceiling is what holds today, with room for the noise of the hours.
    /// </summary>
    private const long HeapCeiling = 48L << 20;
    /// <summary>The CPU these parts may take: measured 0.11% to 0.18% of one core here (Release, from run to run); about three times that, so a slower runner passes and the old 30 s re-ranking (1.3%) does not.</summary>
    private const double CpuCeiling = 0.0045;
    /// <summary>The allocations a sample: measured 3.9 to 4.0 MB here (9144b20: 40 MB).</summary>
    private const long AllocCeiling = 6L << 20;

#if DEBUG
    private const double DebugFactor = 3; // an unoptimized build: the same work, about three times the CPU
#else
    private const double DebugFactor = 1;
#endif

    /// <summary>The heap after a compacting full collection: what is held, not what a collection left behind.</summary>
    private static long Heap()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        return GC.GetTotalMemory(forceFullCollection: false);
    }

    [Fact]
    public void ADayOfTheOwnersAgenticLoadHoldsAFlatHeapAndStaysInsideTheCpuBudget()
    {
        Directory.CreateDirectory(_dir);
        var w = new AgentLoadWorld();
        var settings = new Settings();
        var mgr = new IncidentManager(settings, new TagKnowledge(null, null), new FakeHost(_dir), w.Registry);
        var det = new Detector(settings);
        var summarizer = new EventSummarizer(alwaysNotable: IncidentManager.IsWorktreeDelete);
        var spawns = new SpawnTracker(settings, w.Registry, new GitRootResolver(new FakeProbe()), new([@"C:\Users\me"], [], @"C:\Users"), _ => null);
        var tokens = new Core.Tokens.TokenCorrelator(settings, w.Registry);
        var costs = new Core.Services.ProcessCostHistory();
        var filters = new Core.Filters.FilterTimeline();
        var census = new ChildAccumulation(settings);
        var apps = new Core.Boot.AppSnapshots(settings.WatchedApps);
        var stages = new Dictionary<string, Stage>();
        bool measuring = false;
        void Time(string name, Action f)
        {
            long c = ThreadCpu(), a = GC.GetAllocatedBytesForCurrentThread();
            f();
            if (!measuring) return;
            if (!stages.TryGetValue(name, out var st)) stages[name] = st = new Stage();
            st.Cpu += ThreadCpu() - c;
            st.Alloc += GC.GetAllocatedBytesForCurrentThread() - a;
        }
        string? ParentName(int pid) => w.Registry.GetLive(pid)?.Image ?? w.Registry.RecentImage(pid);
        Owner? OwnerName(int pid) => w.Registry.GetLive(pid) is { } r ? ProcessIdentities.OwnerOf(w.Registry.Ancestry(r.Key)) : null;
        ProcessIdentity? IdentityOf(ProcessRecord r) => w.Registry.Ancestry(r.Key) is { Count: > 0 } chain ? ProcessIdentities.Of(chain) : null;

        const int PerHour = 360, Hours = 26, Warmup = 30;
        long baseline = Heap();
        var heap = new List<long>(); // at the end of each hour
        var lines = new List<string>();
        int measured = 0;
        Sample? previous = null;
        for (int i = 0; i < Hours * PerHour; i++)
        {
            measuring = i >= Warmup;
            if (measuring) measured++;
            // 10 tags growing all the time from minute 5; and every 4 hours, from its minute 10, another tag every 6 minutes for
            // 6 minutes (40 incidents in the 4 hours, most re-opened the next time round).
            int tick = i, round = i % (4 * PerHour);
            var s = w.Step(t => t < 10 ? (tick >= 30 ? 4L << 20 : 0) : t < 50 && round >= 60 + (t - 10) * 36 && round < 60 + (t - 10) * 36 + 36 ? 8L << 20 : 0);
            var now = At(s.TimeUtc);
            Time("registry", () => w.Registry.ObserveSnapshot(s.Processes, s.TimeUtc));
            var events = w.Registry.DrainEvents();
            List<LifecycleEvent> notable = [];
            Time("summarize", () => notable = summarizer.Process(events, now, ParentName, OwnerName));
            Time("incidents.events", () =>
            {
                mgr.AddEvents(notable, now);
                // About one finding a sample (the owner's 339 an hour), each its own event.
                mgr.AddEvents([new LifecycleEvent { TimeUtc = s.TimeUtc, Kind = LifecycleKind.Finding, Image = "SpawnStorm", Detail = $"Spawn storm: node.exe (PID {tick}) started 44 short-lived children in 30 s" }], now);
            });
            Time("history", () => w.History.Add(s));
            if (i % 30 == 29) Time("history.handles", () => w.History.AddHandles(w.Handles(s)));
            List<Alert> alerts = [];
            Time("detector", () => alerts = det.Evaluate(w.History));
            Time("incidents", () => mgr.Process(w.History, det, alerts, now));
            Time("diagnostics.spawns", () => spawns.Observe(events, now));
            Time("diagnostics.tokens", () => tokens.Observe(s, events, now.Ms));
            Time("diagnostics.load", () =>
            {
                costs.Observe(TimeSpan.FromMilliseconds(s.TickMs), s.Processes, 0);
                if (previous is not null) Core.Services.ProcessLoad.Between(previous.Processes, s.Processes, TimeSpan.FromMilliseconds(s.TickMs - previous.TickMs), 32);
            });
            Time("diagnostics.filters", () => filters.OnLifecycle(events, w.Registry.LiveRecords, s.TimeUtc));
            Time("diagnostics.census", () => { if (census.Due(now)) census.Observe(w.Registry.LiveRecords(), IdentityOf, now); });
            Time("diagnostics.snapshots", () => apps.Observe(s.TimeUtc, s.Processes, s.Tags));
            previous = s;
            if ((i + 1) % PerHour == 0)
            {
                heap.Add(Heap() - baseline);
                var held = mgr.Held();
                lines.Add($"{(i + 1) / PerHour} h: heap {heap[^1] / 1048576.0:0.0} MB; registry {w.Registry.Count} (compact {w.Registry.CompactCount}, named {w.Registry.NamedCount}), " +
                          $"history {w.History.ProcessTrackCount} processes / {w.History.ProcessPointCount} points, {w.History.Minutes.Count} minutes / {w.History.MinuteProcessEntries} entries, " +
                          $"events {mgr.EventCount}, incidents {held.incidents} ({mgr.Open.Count} open), owners {held.owners} / {held.ownerMembers} members, kept events {held.events}");
            }
        }
        foreach (var line in lines) output.WriteLine(line);
        double seconds = measured * AgentLoadWorld.Interval.TotalSeconds;
        long cpu = stages.Values.Sum(st => st.Cpu), alloc = stages.Values.Sum(st => st.Alloc);
        foreach (var (name, st) in stages.OrderByDescending(kv => kv.Value.Cpu))
            output.WriteLine($"{name}: {st.Cpu / 1e7 / seconds * 100:0.0000}% of one core, {st.Alloc / 1024.0 / measured:0} KB a sample");
        output.WriteLine($"all: {cpu / 1e7 / seconds * 100:0.000}% of one core, {alloc / 1048576.0 / measured:0.00} MB a sample, {w.Starts:N0} process starts");

        string figures = string.Join("; ", lines);
        Assert.Equal(10 + 2, mgr.Open.Count); // the 10 that grow all the time, and two of the round's
        Assert.True(mgr.Closed.Count == IncidentManager.MaxClosed, figures);
        // Every window it holds is full by 6 hours (the raw history's 70 minutes, the events' 90, the 20 closed incidents, the
        // named records' 10,000); from then to the day's end the heap is flat but for the minutes' tags filling to 25 hours
        // and the hours' ups and downs (incidents opening and closing): its slope over hours 6 to 26 is under 0.25 MB an hour,
        // and the last 6 hours hold under 3 MB more than hours 6 to 12. 9144b20: 45 MB at half an hour, 101 MB at 4 hours.
        Assert.True(heap.Max() < HeapCeiling, figures);
        var steady = heap.Skip(5).ToList(); // hours 6 to 26
        double mean = (steady.Count - 1) / 2.0, slope = steady.Select((v, k) => (k - mean) * v).Sum() / steady.Select((_, k) => (k - mean) * (k - mean)).Sum();
        Assert.True(slope < 0.25 * 1048576, $"slope {slope / 1048576.0:0.00} MB an hour; {figures}");
        Assert.True(heap.TakeLast(6).Average() - heap.Skip(5).Take(7).Average() < 3 << 20, figures);
        // The CPU of these parts under this load, inside the 0.5% of one core the whole of PoolWatch has with room for a slower
        // machine (the kernel's process query, the handle scan, the CSV files and the window are the rest, DESIGN.md section
        // 3), and the allocations a sample.
        if (OperatingSystem.IsWindows()) Assert.True(cpu / 1e7 / seconds < CpuCeiling * DebugFactor, $"{cpu / 1e7 / seconds * 100:0.000}% of one core");
        Assert.True(alloc / measured < AllocCeiling, $"{alloc / 1048576.0 / measured:0.00} MB a sample");
    }
}
