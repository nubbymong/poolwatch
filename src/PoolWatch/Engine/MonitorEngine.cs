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

    private DateTime _nextHandles = DateTime.MinValue, _nextServices = DateTime.MinValue, _nextVolumes = DateTime.MinValue, _nextRetention = DateTime.MinValue;
    private bool _skipNextHandleScan;
    private DateTime _nextEtwRetry = DateTime.MinValue;
    private bool _stopped;
    private bool _joined = true;

    public MonitorEngine(Settings settings, AppPaths paths, Log log, bool verboseCsv)
    {
        _settings = settings;
        _paths = paths;
        _log = log;
        _sink = new CsvSink(paths, settings, verboseCsv);
        Registry = new ProcessRegistry { Enricher = ProcessDetails.Enrich, KeepShortLived = TimeSpan.FromMinutes(settings.ShortLivedKeepMinutes) };
        _etw = new ProcessEtwCollector(Registry, log.Info);
        Meter = new SelfMeter(TimeSpan.FromSeconds(Math.Max(30, settings.SampleIntervalSeconds * 3)));
    }

    public ProcessRegistry Registry { get; }
    public SelfMeter Meter { get; }
    public Settings Settings => _settings;
    public AppPaths Paths => _paths;
    public Log Log => _log;
    public bool EtwRunning => _etw.Running;
    public long EtwEvents => _etw.EventsSeen;
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

    public void Start()
    {
        _etw.Start();
        _nextEtwRetry = DateTime.UtcNow.AddSeconds(60);
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
        var now = DateTime.UtcNow;
        List<PoolTagEntry> tags;
        using (Profiler.Stage("tick.poolTags")) tags = _queries.QueryPoolTags();
        var (cPaged, cNonPaged, system) = SystemQueries.QueryPerformance();
        List<ProcessSample> procs;
        using (Profiler.Stage("tick.processes")) procs = _queries.QueryProcesses();
        ulong sumPaged = 0, sumNonPaged = 0;
        foreach (var t in tags) { sumPaged += t.PagedUsed; sumNonPaged += t.NonPagedUsed; }
        ulong compression = 0;
        foreach (var p in procs) if (p.ImageName == "Memory Compression") { compression = p.WorkingSet; break; }
        var sample = new Sample(now, new PoolTotals(cPaged, cNonPaged, sumPaged, sumNonPaged), tags, procs) { System = system with { CompressionWsBytes = compression } };

        using (Profiler.Stage("tick.registry")) Registry.ObserveSnapshot(procs, now);

        if (!_etw.Running && now >= _nextEtwRetry)
        {
            // The session can be stopped from outside (another tool taking the name, logman stop): restart it.
            _nextEtwRetry = now.AddSeconds(60);
            if (SampleCount > 0) _log.Info("ETW process session is not running; restarting it");
            _etw.Start();
        }

        if (now >= _nextServices)
        {
            _nextServices = now.AddSeconds(_settings.ServiceIntervalSeconds);
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
        using (Profiler.Stage("tick.summarize")) notable = _summarizer.Process(events, now, ParentName, OwnerName);
        if (notable.Count > 0)
        {
            using (Profiler.Stage("tick.eventsCsv")) _sink.WriteEvents(notable, now);
            Notable?.Invoke(notable);
        }

        bool requested = _handleScanRequest.IsSet;
        if (requested || now >= _nextHandles)
        {
            _handleScanRequest.Reset();
            _nextHandles = now.AddSeconds(_settings.HandleIntervalSeconds);
            if (_skipNextHandleScan && !requested) _skipNextHandleScan = false;
            else using (Profiler.Stage("tick.handleScan")) ScanHandles(now, procs);
        }

        if (now >= _nextVolumes)
        {
            _nextVolumes = now.AddMinutes(_settings.VolumeIntervalMinutes);
            try
            {
                Volumes = VolumeCollector.Query();
                _sink.WriteVolumes(Volumes, now);
            }
            catch (Exception ex) { _log.Info("volume query failed: " + ex.Message); }
        }

        if (now >= _nextRetention)
        {
            _nextRetention = now.AddHours(6);
            _paths.ApplyRetention(_settings.RetentionDays, _log.Info);
        }

        using (Profiler.Stage("tick.csv")) _sink.WriteSample(sample, Registry);
        Volatile.Write(ref _pair, new SamplePair(_pair?.Current, sample));
        SampleCount++;
        LastSampleDuration = Stopwatch.GetElapsedTime(t0);
        Meter.Tick();
        using (Profiler.Stage("tick.sampleHandlers")) Sampled?.Invoke(sample);
        _trim.MaybeRun();
    }

    private readonly MemoryTrim _trim = new();

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

    private void ScanHandles(DateTime now, IReadOnlyList<ProcessSample> procs)
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
            var snap = new HandleSnapshot(now, counts, types, elapsed, keys);
            LatestHandles = snap;
            using (Profiler.Stage("tick.handleScan.csv")) _sink.WriteHandles(counts, types, procs, now);
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
                var last = _summarizer.Flush(DateTime.UtcNow);
                _sink.WriteEvents(last, DateTime.UtcNow);
                Notable?.Invoke(last); // the final rollups reach the incident timelines too
            }
            catch { }
            _sink.Flush();
        } // never touch the writers while a stuck sampler may still use them
        else _log.Info("sampler thread did not stop within 30 s");
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
