using System.Diagnostics;
using PoolWatch.Collectors;
using PoolWatch.Core;
using PoolWatch.Core.Model;
using PoolWatch.Core.Util;

namespace PoolWatch.Engine;

/// <summary>
/// The always-on collection loop. One background thread samples pool tags, totals and processes every
/// SampleIntervalSeconds; handles, services and volumes on their slower cadences. Subscribers get each sample
/// through <see cref="Sampled"/> on the sampler thread.
/// </summary>
internal sealed class MonitorEngine : IDisposable
{
    private readonly Settings _settings;
    private readonly AppPaths _paths;
    private readonly Log _log;
    private readonly SystemQueries _queries = new();
    private readonly CsvSink _sink;
    private readonly ProcessEtwCollector _etw;
    private readonly ManualResetEventSlim _stop = new();
    private readonly ManualResetEventSlim _handleScanRequest = new();
    private Thread? _thread;

    // Each tick's two times, the tick's sample stamped with them, and when each slower job is due, with its interval and
    // its first due time, on the clock that only goes forward (SamplerClock): the tick names each job and passes no time
    // or interval of its own. Made with the engine, so the first profile.json is due an hour after the engine is made.
    private readonly SamplerClock _clock = new();
    // How long retention has kept each thing it judges as it is, the files in a folder included (RetentionClock), counted
    // on the PC's working-time timer and never on the system clock, so a change of the system clock never makes retention
    // delete anything that was written within the retention period. It counts on from the state its last save kept.
    private readonly RetentionClock _retention;
    private bool _skipNextHandleScan;
    private bool _stopped;
    private bool _joined = true;

    public MonitorEngine(Settings settings, AppPaths paths, Log log, bool verboseCsv)
    {
        _settings = settings;
        _paths = paths;
        _log = log;
        _retention = paths.OpenRetentionClock(Native.WorkingTime.Ms, log.Info);
        _sink = new CsvSink(paths, settings, verboseCsv, log.Info);
        _sink.MergeRollupsFrom(_summarizer); // the day's events.csv merges the minute rollups over 5 minutes
        Registry = new ProcessRegistry { Enricher = ProcessDetails.Enrich, KeepShortLived = TimeSpan.FromMinutes(settings.ShortLivedKeepMinutes) };
        _etw = new ProcessEtwCollector(Registry, log.Info);
        Meter = new SelfMeter(TimeSpan.FromSeconds(Math.Max(30, settings.SampleIntervalSeconds * 3)));
        // The trim looks at the working set the meter has just read for this sample: nothing new on the sampling path.
        Trim = new MemoryTrim(clock: null, workingSet: () => Meter.WorkingSetNow, heap: null, empty: null);
    }

    public ProcessRegistry Registry { get; }
    public SelfMeter Meter { get; }
    public Settings Settings => _settings;
    /// <summary>
    /// Detailed recording is chosen now (<see cref="Core.Settings.RecordMode"/>): the day's files, findings.jsonl and the
    /// spawn-storm folders are written. In Reports only, the default, none of them is.
    /// </summary>
    public bool Recording => _sink.Recording;
    /// <summary>What the day's files record and since when, as the day's sink follows the choice (the incidents' reports say so).</summary>
    public CsvSink.RecordState Records => _sink.Records;
    public AppPaths Paths => _paths;
    public Log Log => _log;
    public bool EtwRunning => _etw.Running;
    public long EtwEvents => _etw.EventsSeen;
    /// <summary>Handle scans whose CSV rows were not written (<see cref="CsvSink.HandleScansNotWritten"/>).</summary>
    public long HandleScansNotWritten => _sink.HandleScansNotWritten;
    public int EtwEventsLost => _etw.EventsLost;
    public string EtwMode => _etw.Mode;

    public Sample? Latest => _pair?.Current;
    /// <summary>The last two samples, published together so a reader never pairs a sample with itself.</summary>
    public SamplePair? LatestPair => Volatile.Read(ref _pair);
    private SamplePair? _pair;
    public HandleSnapshot? LatestHandles { get; private set; }
    public IReadOnlyList<VolumeInfo> Volumes { get; private set; } = [];
    public IReadOnlyList<ServiceEntry> Services { get; private set; } = [];
    public long SampleCount { get; private set; }
    public TimeSpan LastSampleDuration { get; private set; }

    /// <summary>Raised on the sampler thread after every sample.</summary>
    public event Action<Sample>? Sampled;
    /// <summary>Raised on the sampler thread after every handle scan.</summary>
    public event Action<HandleSnapshot>? HandlesScanned;
    /// <summary>Raised on the sampler thread with every lifecycle event drained this tick (spawn diagnostics need them all).</summary>
    public event Action<IReadOnlyList<LifecycleEvent>>? Lifecycle;
    /// <summary>Raised on the sampler thread with the notable events and short-lived rollups (what events.csv records).</summary>
    public event Action<IReadOnlyList<LifecycleEvent>>? Notable;
    private readonly EventSummarizer _summarizer = new(alwaysNotable: Core.Incidents.IncidentManager.IsWorktreeDelete);
    public EventSummarizer Summarizer => _summarizer;
    /// <summary>The last <see cref="RawEventsKept"/> raw lifecycle events, for the UI's "Show all events" (fixed size).</summary>
    private readonly LifecycleEvent[] _raw = new LifecycleEvent[RawEventsKept];
    private int _rawNext, _rawCount;
    public const int RawEventsKept = 2000;

    public List<LifecycleEvent> RecentRawEvents()
    {
        lock (_raw)
        {
            var list = new List<LifecycleEvent>(_rawCount);
            for (int i = 0; i < _rawCount; i++) list.Add(_raw[(_rawNext - _rawCount + i + RawEventsKept) % RawEventsKept]);
            return list;
        }
    }

    /// <summary>
    /// Starts the trace, then the sampler thread, whose first tick runs at once: the trace's restart is first due a minute
    /// after that tick (<see cref="SamplerClock.Begin"/>).
    /// </summary>
    public void Start()
    {
        _etw.Start();
        _thread = new Thread(Loop) { IsBackground = true, Name = "PoolWatch sampler", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Asks for a handle scan on the next tick (used when an alert fires).</summary>
    public void RequestHandleScan() => _handleScanRequest.Set();

    private void Loop()
    {
        var interval = TimeSpan.FromSeconds(_settings.SampleIntervalSeconds);
        var sw = Stopwatch.StartNew();
        var next = sw.Elapsed;
        while (!_stop.IsSet)
        {
            try { Tick(); }
            catch (Exception ex) { _log.Info("sample failed: " + ex); }
            next += interval;
            var wait = next - sw.Elapsed;
            if (wait < TimeSpan.Zero) { next = sw.Elapsed; wait = TimeSpan.Zero; } // fell behind: do not burst
            _stop.Wait(wait);
        }
    }

    private void Tick()
    {
        var t0 = Stopwatch.GetTimestamp();
        using var tick = Profiler.Stage("tick (all)");
        // This tick, begun: its two times, read once, with which it stamps its sample, and through which each slower job
        // below asks whether it is due (a test holds that each job's work is under its own job's question).
        var at = _clock.Begin();
        var now = at.Time.Utc; // for the times shown and written; every interval is timed by the clock on its Ms
        // Detailed recording turned on since the last tick (Settings, setup): the day's files start again with the whole
        // picture in this tick, as at PoolWatch's start: the services and the volumes PoolWatch holds now, and a handle scan.
        if (_sink.TurnedOn())
        {
            _handleScanRequest.Set();
            _sink.WriteServices(Services, now);
            _sink.WriteVolumes(Volumes, now);
        }
        List<PoolTagEntry> tags;
        using (Profiler.Stage("tick.poolTags")) tags = _queries.QueryPoolTags();
        var (cPaged, cNonPaged, system) = SystemQueries.QueryPerformance();
        List<ProcessSample> procs;
        using (Profiler.Stage("tick.processes")) procs = _queries.QueryProcesses();
        var sample = at.NewSample(cPaged, cNonPaged, system, tags, procs);

        using (Profiler.Stage("tick.registry")) Registry.ObserveSnapshot(procs, now);

        if (!_etw.Running && at.Due(SamplerClock.Job.EtwRetry, _settings))
        {
            // The session can be stopped from outside (another tool taking the name, logman stop): restart it.
            if (SampleCount > 0) _log.Info("ETW process session is not running; restarting it");
            _etw.Start();
        }

        if (at.Due(SamplerClock.Job.Services, _settings))
        {
            using (Profiler.Stage("tick.services")) RefreshServices(now);
        }
        else if (Registry.HasPendingStart("svchost.exe"))
        {
            RefreshServices(now); // a new svchost: find out what it hosts
        }

        var events = Registry.DrainEvents();
        if (events.Count > 0)
        {
            lock (_raw)
                foreach (var e in events) { _raw[_rawNext] = e; _rawNext = (_rawNext + 1) % RawEventsKept; _rawCount = Math.Min(RawEventsKept, _rawCount + 1); }
            using (Profiler.Stage("tick.lifecycleHandlers")) Lifecycle?.Invoke(events);
        }
        List<LifecycleEvent> notable;
        using (Profiler.Stage("tick.summarize")) notable = _summarizer.Process(events, new Moment(at.Time.Utc, at.Time.Ms), ParentName, OwnerName);
        if (notable.Count > 0)
        {
            using (Profiler.Stage("tick.eventsCsv")) _sink.WriteEvents(notable, now);
            Notable?.Invoke(notable);
        }

        bool requested = _handleScanRequest.IsSet;
        if (at.Due(SamplerClock.Job.Handles, _settings, now: requested))
        {
            _handleScanRequest.Reset();
            if (_skipNextHandleScan && !requested) _skipNextHandleScan = false;
            else using (Profiler.Stage("tick.handleScan")) ScanHandles(now, at.Time.Ms, procs);
        }

        if (at.Due(SamplerClock.Job.Volumes, _settings))
        {
            try
            {
                Volumes = VolumeCollector.Query();
                _sink.WriteVolumes(Volumes, now);
            }
            catch (Exception ex) { _log.Info("volume query failed: " + ex.Message); }
        }

        if (at.Due(SamplerClock.Job.Retention, _settings))
            _paths.ApplyRetention(_settings.RetentionDays, _log.Info, _retention);

        using (Profiler.Stage("tick.csv")) _sink.WriteSample(sample, Registry);
        Volatile.Write(ref _pair, new SamplePair(_pair?.Current, sample));
        SampleCount++;
        LastSampleDuration = Stopwatch.GetElapsedTime(t0);
        Meter.Tick();
        using (Profiler.Stage("tick.sampleHandlers")) Sampled?.Invoke(sample);
        Trim.MaybeRun();
        if (Profile is { } profile && at.Due(SamplerClock.Job.Profile, _settings))
            WriteProfile(profile);
    }

    /// <summary>
    /// One tick's two times, read together at its start: <see cref="Utc"/> on the system clock, for the times shown and
    /// written, and <see cref="Ms"/> on Environment.TickCount64 (milliseconds since Windows started), a clock that only
    /// goes forward whatever the system clock does, which every interval of the sampler is timed on.
    /// </summary>
    internal readonly record struct TickTime(DateTime Utc, long Ms)
    {
        public static TickTime Now() => new(DateTime.UtcNow, Environment.TickCount64);
    }

    /// <summary>
    /// The sampler's schedule and its clock. It is made with the engine, on the thread that makes the engine (its
    /// constructor reads the clock once, for the first profile.json), and is then used on the sampler thread alone, which
    /// starts after that. Each tick begins with <see cref="Begin"/>, which gives it a <see cref="ThisTick"/>: no tick has
    /// one without beginning, since its one constructor reads the tick's two times from this clock (it is a class, so not
    /// left empty either, as a struct can be). The tick stamps its sample through it (<see cref="ThisTick.NewSample"/>) and
    /// asks it whether a slower job is due (<see cref="ThisTick.Due"/>), and a test holds that the engine's tick does both
    /// and puts each job's work under its own job's question. Every job's interval (<see cref="Every"/>), when it is first
    /// due and when it is next due are the clock's own, timed on <see cref="TickTime.Ms"/> alone: the tick names the job
    /// and hands no time or interval of its own, so its sample is stamped with the times it began at, no job runs on
    /// another's interval or time, and setting the system clock back never holds a job back until it catches up, nor does
    /// setting it forward run one early. The first profile.json is due an hour after the clock is made, and the trace's
    /// restart a minute after the first tick, which runs as soon as the engine has started the trace (<see cref="Start"/>).
    /// </summary>
    internal sealed class SamplerClock
    {
        /// <summary>The sampler's slower jobs, each with when it is next due.</summary>
        internal enum Job { EtwRetry, Services, Handles, Volumes, Retention, Profile }

        /// <summary>How often the trace is restarted while something has stopped it, retention runs and profile.json is written.</summary>
        internal static readonly TimeSpan EtwRetryEvery = TimeSpan.FromSeconds(60), RetentionEvery = TimeSpan.FromHours(6), ProfileEvery = TimeSpan.FromHours(1);

        private readonly Func<TickTime> _read;
        private readonly long[] _nextMs = Enum.GetValues<Job>().Select(_ => long.MinValue).ToArray();
        private bool _begun;

        /// <param name="read">Reads the two times (<see cref="TickTime.Now"/>; a test gives its own).</param>
        internal SamplerClock(Func<TickTime>? read = null)
        {
            _read = read ?? TickTime.Now;
            _nextMs[(int)Job.Profile] = _read().Ms + Ms(ProfileEvery); // the first profile.json an hour after the clock is made
        }

        /// <summary>The two times the current tick began at (both 0 before the first <see cref="Begin"/>).</summary>
        internal TickTime At { get; private set; }

        /// <summary>When <paramref name="job"/> is next due, in milliseconds on <see cref="TickTime.Ms"/> (long.MinValue: at the next tick).</summary>
        internal long NextMs(Job job) => _nextMs[(int)job];

        /// <summary>
        /// How often <paramref name="job"/> runs: the services, the handle scan and the volumes as <paramref name="settings"/>
        /// say (<see cref="Core.Settings.ServiceIntervalSeconds"/>, <see cref="Core.Settings.HandleIntervalSeconds"/>,
        /// <see cref="Core.Settings.VolumeIntervalMinutes"/>, read at each tick, so a change applies from the next one); the
        /// trace's restart, retention and profile.json on their fixed intervals.
        /// </summary>
        internal static TimeSpan Every(Job job, Settings settings) => job switch
        {
            Job.EtwRetry => EtwRetryEvery,
            Job.Services => TimeSpan.FromSeconds(settings.ServiceIntervalSeconds),
            Job.Handles => TimeSpan.FromSeconds(settings.HandleIntervalSeconds),
            Job.Volumes => TimeSpan.FromMinutes(settings.VolumeIntervalMinutes),
            Job.Retention => RetentionEvery,
            Job.Profile => ProfileEvery,
            _ => throw new ArgumentOutOfRangeException(nameof(job)),
        };

        /// <summary>A tick begins (<see cref="ThisTick(SamplerClock)"/>).</summary>
        internal ThisTick Begin() => new(this);

        /// <summary>
        /// Whether <paramref name="job"/> is due at the tick that began at <paramref name="at"/> (or <paramref name="now"/>,
        /// when it was asked for: a handle scan when an alert fires), and if so, when it is next due: its interval
        /// (<see cref="Every"/>) after that tick began, on <see cref="TickTime.Ms"/> alone.
        /// </summary>
        private bool Due(TickTime at, Job job, Settings settings, bool now)
        {
            ref long next = ref _nextMs[(int)job];
            if (!now && at.Ms < next) return false;
            next = at.Ms + Ms(Every(job, settings));
            return true;
        }

        private static long Ms(TimeSpan t) => (long)t.TotalMilliseconds;

        /// <summary>One tick of the sampler, as <see cref="Begin"/> began it: its two times, its sample and its due jobs.</summary>
        internal sealed class ThisTick
        {
            private readonly SamplerClock _clock;

            /// <summary>
            /// Begins a tick of <paramref name="clock"/>: its two times are read, together, and the clock keeps them until the
            /// next one begins (<see cref="At"/>). At the first tick, the trace's restart is first due
            /// <see cref="EtwRetryEvery"/> later: the engine has just started the trace, so a trace that did not start is
            /// tried again a minute on, not at once. The only constructor, so a tick is never made with times of its own.
            /// </summary>
            internal ThisTick(SamplerClock clock)
            {
                _clock = clock;
                Time = clock.At = clock._read();
                if (!clock._begun)
                {
                    clock._begun = true;
                    clock._nextMs[(int)Job.EtwRetry] = Time.Ms + Ms(EtwRetryEvery);
                }
            }

            /// <summary>The two times the tick began at.</summary>
            internal TickTime Time { get; }

            /// <summary>
            /// Whether <paramref name="job"/> is due at this tick (or <paramref name="now"/>, when it was asked for), on its
            /// own interval (<see cref="Every"/>) and its own next due time; if so, it is next due its interval after this
            /// tick began.
            /// </summary>
            internal bool Due(Job job, Settings settings, bool now = false) => _clock.Due(Time, job, settings, now);

            /// <summary>
            /// The tick's sample from what it read, stamped with the two times the tick began at (<see cref="Sample.TimeUtc"/>,
            /// <see cref="Sample.TickMs"/>: the load per process, the minute cost points and how long load has lasted are
            /// timed on the latter), with the pool tags' sums and the compression store's working set.
            /// </summary>
            internal Sample NewSample(ulong committedPaged, ulong committedNonPaged, SystemCounters system, List<PoolTagEntry> tags, List<ProcessSample> procs)
            {
                ulong sumPaged = 0, sumNonPaged = 0;
                foreach (var t in tags) { sumPaged += t.PagedUsed; sumNonPaged += t.NonPagedUsed; }
                ulong compression = 0;
                foreach (var p in procs) if (p.ImageName == "Memory Compression") { compression = p.WorkingSet; break; }
                return new Sample(Time.Utc, new PoolTotals(committedPaged, committedNonPaged, sumPaged, sumNonPaged), tags, procs)
                {
                    System = system with { CompressionWsBytes = compression }, TickMs = Time.Ms,
                };
            }
        }
    }

    public MemoryTrim Trim { get; }

    /// <summary>
    /// Any thread: the next sample compacts PoolWatch's heap and empties its working set (the main window hid and dropped its
    /// caches, or another of PoolWatch's windows closed: Ui.OtherWindows).
    /// </summary>
    public void ReleaseMemory() => Trim.Request();

    /// <summary>Builds profile.json (CostReport.Json); when set, the sampler writes it to cache\profile.json every hour.</summary>
    public Func<string>? Profile { get; set; }

    /// <summary>
    /// PoolWatch's own cost and what it holds, measured on the PC it runs on. Written to a temporary file and moved into
    /// place, so a vendor bundle built meanwhile reads the previous hour's file, never half of this one.
    /// </summary>
    private void WriteProfile(Func<string> profile)
    {
        try
        {
            string json = profile();
            string path = Path.Combine(_paths.EnsureDir(_paths.CacheDir), "profile.json");
            DurableFile.Replace(_paths.Guard, path, json);
            Profiler.Wrote(path, json.Length);
        }
        catch (Exception ex) { _log.Info("profile.json failed: " + ex.Message); }
    }

    private string? ParentName(int pid) => Registry.GetLive(pid)?.Image ?? Registry.RecentImage(pid);
    private Core.Identity.Owner? OwnerName(int pid) => Registry.GetLive(pid) is { } r ? Core.Identity.ProcessIdentities.OwnerOf(Registry.Ancestry(r.Key)) : null;

    /// <summary>Appends a responsiveness probe to lag.csv. Sampler thread only.</summary>
    public void WriteLag(Core.Lag.LagProbe p) => _sink.WriteLag(p);

    /// <summary>Appends the system-wide load (CPU, run queue, page-fault split) to load.csv. Sampler thread only.</summary>
    public void WriteLoad(Core.Services.LoadPoint p, long pageFaultsPerSec) => _sink.WriteLoad(p, pageFaultsPerSec);

    /// <summary>Appends app events (alerts, releases, captures) to events.csv. Sampler thread only.</summary>
    public void RecordEvents(IReadOnlyList<LifecycleEvent> events) => _sink.WriteEvents(events, DateTime.UtcNow);

    private void RefreshServices(DateTime now)
    {
        try
        {
            Services = ServiceCollector.Query();
            Registry.SetServices(Services.Where(s => s.ProcessId != 0).GroupBy(s => s.ProcessId).ToDictionary(g => g.Key, g => g.Select(s => s.Name).OrderBy(n => n).ToList()));
            _sink.WriteServices(Services, now);
        }
        catch (Exception ex) { _log.Info("service query failed: " + ex.Message); }
    }

    private void ScanHandles(DateTime now, long nowMs, IReadOnlyList<ProcessSample> procs)
    {
        try
        {
            var types = _queries.ObjectTypeNames();
            HandleCounts counts; TimeSpan elapsed;
            using (Profiler.Stage("tick.handleScan.query+parse")) (counts, elapsed) = _queries.QueryHandles();
            if (elapsed.TotalMilliseconds > _settings.HandleScanBudgetMs)
            {
                _skipNextHandleScan = true;
                _log.Info($"handle scan took {elapsed.TotalMilliseconds:0} ms (> {_settings.HandleScanBudgetMs} ms): skipping the next cycle");
            }
            var keys = procs.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First().Key);
            var snap = new HandleSnapshot(now, counts, types, elapsed, keys) { Ms = nowMs };
            LatestHandles = snap;
            // Handed to the handle CSV's own thread, which writes it (csv.handles): the tick does not wait for that write.
            using (Profiler.Stage("tick.handleScan.csv")) _sink.PostHandles(counts, types, procs, now);
            using (Profiler.Stage("tick.handleScan.handlers")) HandlesScanned?.Invoke(snap);
        }
        catch (Exception ex) { _log.Info("handle scan failed: " + ex.Message); }
    }

    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        _stop.Set();
        _joined = _thread?.Join(TimeSpan.FromSeconds(30)) ?? true;
        _etw.Dispose();
        if (_joined)
        {
            try
            {
                var stopped = TickTime.Now();
                var last = _summarizer.Flush(new Moment(stopped.Utc, stopped.Ms));
                _sink.WriteEvents(last, DateTime.UtcNow);
                Notable?.Invoke(last); // the final rollups reach the incident timelines too
            }
            catch { }
            _sink.Flush();
            _retention.Save(); // the next start counts on from here
        } // never touch the writers while a stuck sampler may still use them
        else _log.Info("sampler thread did not stop within 30 s: the stop neither writes nor counts the CSV rows not written yet");
    }

    public void Dispose()
    {
        Stop();
        if (!_joined) return; // a stuck sampler may still use the native buffers: leaking them at exit is the safe choice
        _sink.Dispose();
        _queries.Dispose();
    }
}

internal sealed record SamplePair(Sample? Previous, Sample Current);

/// <summary>
/// One handle scan: counts per process per type, with the type-name table. <see cref="Keys"/> maps each PID to
/// the (PID, create time) instance that owned it in the same tick's process snapshot, so PID reuse between
/// scans is never mistaken for the same process.
/// </summary>
internal sealed record HandleSnapshot(DateTime TimeUtc, HandleCounts Counts, IReadOnlyDictionary<int, string> TypeNames, TimeSpan Elapsed,
    IReadOnlyDictionary<int, ProcessKey> Keys)
{
    /// <summary>The scan's tick on the clock that only goes forward (<see cref="Sample.TickMs"/>).</summary>
    public required long Ms { get; init; }

    public ProcessKey? KeyOf(int pid) => Keys.TryGetValue(pid, out var k) ? k : null;

    public int Count(int pid, string typeName)
    {
        foreach (var (idx, name) in TypeNames)
            if (name == typeName) return Counts.Count(pid, idx);
        return 0;
    }

    public Dictionary<string, int> ByTypeName(int pid)
    {
        var d = new Dictionary<string, int>();
        foreach (var (idx, n) in Counts.ForProcess(pid)) d[TypeNames.GetValueOrDefault(idx, $"#{idx}")] = n;
        return d;
    }
}
