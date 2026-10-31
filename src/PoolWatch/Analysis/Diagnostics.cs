using Microsoft.Win32;
using PoolWatch.Collectors;
using PoolWatch.Core;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Model;
using PoolWatch.Core.Services;
using PoolWatch.Core.Spawn;
using PoolWatch.Engine;

namespace PoolWatch.Analysis;

/// <summary>
/// Diagnostics beyond pool-tag incidents (spawn storms, git roots, stray .git, ...). Each produces
/// <see cref="Finding"/>s: they are appended to findings.jsonl and events.csv, shown on the incident timelines and in
/// the Findings tab, and alert-level ones get a report folder under incidents\. Runs on the sampler thread.
/// </summary>
internal sealed class Diagnostics
{
    private readonly MonitorEngine _engine;
    private readonly AnalysisPipeline _analysis;
    private readonly FileProbe _probe = new();
    private DateTime _nextStrayCheck = DateTime.MinValue;

    public Diagnostics(MonitorEngine engine, AnalysisPipeline analysis)
    {
        _engine = engine;
        _analysis = analysis;
        Git = new GitRootResolver(_probe);
        Spawns = new SpawnTracker(engine.Settings, engine.Registry, Git, GitContext(), CwdOf);
        StrayGit = new StrayGitCheck(_probe, Git);
        Tokens = new Core.Tokens.TokenCorrelator(engine.Settings, engine.Registry);
        Lag = new Core.Lag.LagMonitor(engine.Settings);
        // Addendum 4 A: "PC is saturated" when the run queue stays above the core count OR the responsiveness probe (item
        // 9) trips, for over 60 s. The probe's state counts only while it is fresh (three probe intervals, at least 30 s).
        var probeFresh = TimeSpan.FromSeconds(Math.Max(30, 3 * engine.Settings.LagProbeSeconds));
        ProbeTripping = () => { lock (Lag) return Lag.Tripping(DateTime.UtcNow, probeFresh); };
        Accumulation = new Core.Spawn.ChildAccumulation(engine.Settings);
        AppSnapshots = new Core.Boot.AppSnapshots(engine.Settings.WatchedApps);
        if (engine.Settings.LagProbeSeconds > 0)
        {
            string probe = engine.Paths.Guard.CheckReal(Path.Combine(engine.Paths.EnsureDir(engine.Paths.CacheDir), "lag-probe.tmp"));
            _lagProber = new LagProber(TimeSpan.FromSeconds(engine.Settings.LagProbeSeconds), probe, () => engine.Latest?.Totals ?? default, SelfWorkPausesProbe,
                engine.Paths.Guard.CheckReal);
        }
        EventLog = new EventLogFeed(engine.Log.Info, Path.Combine(engine.Paths.EnsureDir(engine.Paths.CacheDir), "eventlog-last.txt"), engine.Paths.Guard.CheckReal);
        // C6: the event-log feed is a diagnostic extra; nothing in it may stop PoolWatch from starting.
        try { EventLog.Start(); }
        catch (Exception ex) { engine.Log.Info("event log feed not started: " + ex.Message); }
        analysis.Incidents.Opened += (_, _) => _instancesRequested = true;
        Cache = new CacheClearer(engine, Findings, () => AnyExperimentActive);
        Guard = new TokenLeakGuard(engine.Paths, engine.Log, f => _guardFindings.Enqueue(f));
        // Service changes are made on a worker thread (the page's Apply / Restore): their findings are queued and recorded on
        // the sampler's next tick, like every other finding raised off the sampler thread.
        Services = new ServicesController(engine.Paths, engine.Log, f => _fromOtherThreads.Enqueue(f), LiveCost, RecentCost);
        Findings.Added += OnFinding;
        // S3 (review round 3): a data folder moved aside or repaired at start is on the timeline (recorded on the first tick).
        foreach (var n in engine.Paths.StartupNotes)
            _fromOtherThreads.Enqueue(new Finding { TimeUtc = DateTime.UtcNow, Kind = FindingKind.Setup, Severity = FindingSeverity.Warning, Title = "Data folder: " + n });
        engine.Lifecycle += OnLifecycle;
        engine.Sampled += OnSample;
        _lagProber?.Start(); // after Cache: its pause reads the clear's state
    }

    /// <summary>
    /// C1: PoolWatch's own trace (recording or parsing) or cache clear is running, or ended under 30 s ago. The lag probe
    /// pauses then (probe thread), so lag PoolWatch causes itself is not recorded as a pool lag onset.
    /// C1 b (review round 2): at most 10 min for one piece of work. A capture thread that hangs (a stuck parse) used to keep
    /// the probe silent until PoolWatch restarted; past 10 min the probe runs again, and that is said once per stuck piece
    /// of work (log and a Lag finding).
    /// </summary>
    private bool SelfWorkPausesProbe()
    {
        bool captureBusy = _analysis.Captures.Busy, clearBusy = Cache.Busy;
        var since = Core.Lag.ProbePause.Earlier(captureBusy ? _analysis.Captures.BusySince : null, clearBusy ? Cache.BusySince : null);
        var now = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var state = Core.Lag.ProbePause.StateOf(captureBusy || clearBusy, since, Core.Lag.ProbePause.Later(_analysis.Captures.LastEnd, Cache.LastEnd), now);
        if (state == Core.Lag.ProbePause.State.Overdue && since is { } s && Interlocked.Exchange(ref _overdueNotedMs, (long)s.TotalMilliseconds) != (long)s.TotalMilliseconds)
        {
            string what = captureBusy ? "confirmation trace" : "cache clear";
            string msg = $"Lag probe resumed: PoolWatch's own {what} has been running for {Core.Detection.Fmt.Duration(now - s)} " +
                         $"(over {Core.Lag.ProbePause.MaxPause.TotalMinutes:0} min), so it may be stuck; lag is measured again and may include its load.";
            _engine.Log.Info(msg);
            _fromOtherThreads.Enqueue(new Finding { TimeUtc = DateTime.UtcNow, Kind = FindingKind.Lag, Severity = FindingSeverity.Warning, Title = msg });
        }
        return state == Core.Lag.ProbePause.State.Paused;
    }

    /// <summary>The start (monotonic ms) of the stuck work the probe already resumed for, so it is noted once.</summary>
    private long _overdueNotedMs;

    /// <summary>Whether the lag probe is paused for PoolWatch's own work right now (the Overview's lag line).</summary>
    public bool LagProbePaused => _lagProber?.Paused ?? false;
    /// <summary>Probes skipped for PoolWatch's own work since start (summary.json).</summary>
    public long LagProbesPaused => _lagProber?.PausedProbes ?? 0;

    public FindingLog Findings { get; } = new();
    private Core.Experiments.MarkInbox? _markInbox;
    public GitRootResolver Git { get; }
    public SpawnTracker Spawns { get; }
    public StrayGitCheck StrayGit { get; }
    public Core.Tokens.TokenCorrelator Tokens { get; }
    public Core.Filters.FilterTimeline Filters { get; } = new();
    public Core.Lag.LagMonitor Lag { get; }
    public Core.Spawn.ChildAccumulation Accumulation { get; }
    /// <summary>Item 13: each watched app's last tag snapshot before it started and first after it exited (lock it to read).</summary>
    public Core.Boot.AppSnapshots AppSnapshots { get; }
    private readonly LagProber? _lagProber;
    public bool DwmAvailable => _lagProber?.DwmAvailable ?? false;
    public EventLogFeed EventLog { get; }
    private DateTime _nextServices = DateTime.MinValue, _nextInstances = DateTime.MinValue;

    /// <summary>The running (or last) A/B experiment. Read under its own lock.</summary>
    public Core.Experiments.Experiment? Experiment { get; private set; }
    private readonly object _experimentLock = new();
    /// <summary>Held while reading <see cref="Experiment"/> from another thread.</summary>
    public object ExperimentLock => _experimentLock;
    /// <summary>Raised on the sampler thread at each experiment phase change (the tray shows the prompt).</summary>
    public event Action<Core.Experiments.ExperimentStep>? ExperimentStep;

    /// <summary>The "lag before vs after a clear" preset (addendum 2 C), when that was the last experiment asked for.</summary>
    public Core.Experiments.ClearLagExperiment? ClearExperiment { get; private set; }

    public bool AnyExperimentActive
    {
        get { lock (_experimentLock) return Experiment?.IsActive == true || ClearExperiment?.IsActive == true; }
    }

    /// <summary>Starts an experiment ("clear cache": the lag before / after a clear preset); false when one is already running.</summary>
    public bool StartExperiment(string app, TimeSpan phase)
    {
        if (string.IsNullOrWhiteSpace(app)) return false;
        lock (_experimentLock)
        {
            if (Experiment?.IsActive == true || ClearExperiment?.IsActive == true) return false;
            if (Core.Experiments.ClearLagExperiment.IsPreset(app))
            {
                ClearExperiment = new Core.Experiments.ClearLagExperiment(phase, DateTime.UtcNow);
                Experiment = null;
            }
            else
            {
                Experiment = new Core.Experiments.Experiment(Core.Experiments.Marks.Clean(app), phase, DateTime.UtcNow);
                ClearExperiment = null;
            }
        }
        _engine.Log.Info($"experiment with {app} requested ({phase.TotalMinutes:0.#} min per phase)");
        return true;
    }

    /// <summary>Any thread; the finding is recorded on the sampler's next tick.</summary>
    public void CancelExperiment()
    {
        Finding? f;
        lock (_experimentLock) f = Experiment?.Cancel(DateTime.UtcNow) ?? ClearExperiment?.Cancel(DateTime.UtcNow);
        if (f is not null) _fromOtherThreads.Enqueue(f);
    }

    /// <summary>
    /// Findings raised on the UI thread (tray / hotkey marks, a cancel). Recording one writes findings.jsonl and events.csv,
    /// which only the sampler thread may touch: they are queued here and recorded at the start of the next tick.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<Finding> _fromOtherThreads = new();

    private void DrainOtherThreads()
    {
        for (int i = 0; i < 1000 && _fromOtherThreads.TryDequeue(out var f); i++) Findings.Add(f);
    }

    /// <summary>The experiment line for the Overview and the tray ("" when none ran).</summary>
    public string ExperimentStatus()
    {
        lock (_experimentLock)
        {
            if (ClearExperiment is { } c) return c.Statement is { } st ? $"{c.Status(DateTime.UtcNow)}: {st}" : c.Status(DateTime.UtcNow);
            return Experiment is { } e ? (e.Result is { } r ? $"{e.Status(DateTime.UtcNow)}: {r.Statement}" : e.Status(DateTime.UtcNow)) : "";
        }
    }

    /// <summary>An owner's note on the timeline (tray Mark, hotkey, or a --mark request). Any thread: recorded on the next tick.</summary>
    public Finding? AddMark(string text, string source, DateTime? timeUtc = null)
    {
        var f = Core.Experiments.Marks.Finding(text, timeUtc ?? DateTime.UtcNow, source);
        if (f is not null) _fromOtherThreads.Enqueue(f);
        return f;
    }
    private volatile bool _instancesRequested;
    /// <summary>Clear cache (addendum 2): manual and automatic standby purges, measured.</summary>
    public CacheClearer Cache { get; }
    /// <summary>The token-leak guard (addendum 3). Its findings come from worker threads: queued, recorded on the sampler.</summary>
    public TokenLeakGuard Guard { get; }
    /// <summary>The background-services page (addendum 4): enumerate, apply/restore, self-repair watch.</summary>
    public ServicesController Services { get; }
    /// <summary>The "PC is saturated" detector (addendum 4 A).</summary>
    public SaturationDetector Saturation { get; } = new();
    /// <summary>Per-process CPU and page-fault rates in the last window (for the saturation answer and the page).</summary>
    public ProcessLoad.Rates? Load { get; private set; }
    /// <summary>The system-wide page-fault split in the last window (demand-zero / transition / hard); null until it has a reading.</summary>
    public FaultSplit? Faults { get; private set; }
    /// <summary>Minute points of the busy processes' CPU and page faults (before-costs, heavy background apps). Lock it to read.</summary>
    public ProcessCostHistory Costs { get; } = new();

    /// <summary>A PID's cost in the latest window (the services page's Cost column); null before the first window.</summary>
    public (double cores, long faultsPerSec)? CostOfPid(int pid) => LiveCost(pid);

    /// <summary>
    /// A process's measured cost for the tasks / startup list: its average over the last 30 minutes while it has been busy
    /// (<see cref="Costs"/>), else the latest window (idle processes are not tracked: they cost about nothing).
    /// </summary>
    public (double cores, long faultsPerSec)? CostOfProcess(ProcessKey key)
    {
        lock (Costs)
            if (Costs.Average(key, TimeSpan.FromMinutes(30), DateTime.UtcNow) is { } avg) return (avg.cores, avg.faultsPerSec);
        if (Load is not { } load) return null;
        foreach (var r in load.Processes) if (r.Key == key) return (r.Cores, r.PageFaultsPerSec);
        return (0, 0);
    }

    /// <summary>Heavy background apps (addendum 4 C): over 0.5 core for the last 30 minutes with no window in front.</summary>
    public List<HeavyProcess> HeavyBackground()
    {
        lock (Costs) return Costs.Heavy(DateTime.UtcNow, TimeSpan.FromMinutes(30), 0.5);
    }

    /// <summary>A change the owner made on the Background services page (task / startup entry): a timeline finding. Any thread.</summary>
    /// <summary>A finding raised off the sampler thread (the logon-task check, review round 3): recorded on the next tick.</summary>
    public void RecordFromOtherThread(Finding f) => _fromOtherThreads.Enqueue(f);

    public void RecordOwnerChange(string title, string? image) => _fromOtherThreads.Enqueue(new Finding
    {
        TimeUtc = DateTime.UtcNow, Kind = FindingKind.ServiceChange, Severity = FindingSeverity.Info, Title = title, Image = image,
    });

    /// <summary>A PID's cost in the latest window: (0, 0) when it is alive but idle; null before the first window.</summary>
    private (double cores, long faultsPerSec)? LiveCost(int pid)
    {
        if (Load is not { } load) return null;
        foreach (var r in load.Processes) if (r.Key.Pid == pid) return (r.Cores, r.PageFaultsPerSec);
        return (0, 0);
    }

    /// <summary>A PID's average cost over the last 10 minutes when it has been busy (else its live cost): a change's "before".</summary>
    private (double cores, long faultsPerSec)? RecentCost(int pid)
    {
        if (_engine.Registry.GetLive(pid) is { } rec)
            lock (Costs)
                if (Costs.Average(rec.Key, TimeSpan.FromMinutes(10), DateTime.UtcNow) is { } avg) return (avg.cores, avg.faultsPerSec);
        return LiveCost(pid);
    }
    public double? RunQueue { get; private set; }
    private IReadOnlyList<ProcessSample>? _prevProcs;
    private DateTime _prevProcsUtc;
    /// <summary>Whether the lag probe (item 9) is tripping now: one of the two saturation conditions (wired in the constructor).</summary>
    public Func<bool>? ProbeTripping { get; set; }
    /// <summary>The tray UI's notices for the guard (null headless).</summary>
    public IGuardUi? GuardUi { get; set; }
    private readonly System.Collections.Concurrent.ConcurrentQueue<Finding> _guardFindings = new();
    private Task? _guardTask;
    private readonly object _guardStartLock = new();
    private readonly CancellationTokenSource _guardStop = new();

    private int _failsafeHooked;

    /// <summary>
    /// The logon check, on a worker thread (the tray app at start-up; headless with --token-guard; the guard turned on later).
    /// While a check runs, the caller gets that one.
    /// </summary>
    public Task StartTokenGuard(IGuardUi? ui)
    {
        GuardUi = ui;
        // C4 (review): a crash during the leak test must still leave the logon protected. The clean shutdown path calls
        // OnExit via StopTokenGuard; these catch an unhandled exception or a bare process exit as well (OnExit is a
        // no-op unless the test is still running). Not a kill or a native crash (no handler runs): the next start's logon
        // check re-applies the workaround (residual risk, TokenLeakGuard.OnExit).
        if (Interlocked.Exchange(ref _failsafeHooked, 1) == 0)
        {
            AppDomain.CurrentDomain.UnhandledException += (_, _) => { try { Guard.OnExit(); } catch { } };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Guard.OnExit(); } catch { } };
        }
        // Once a check has finished, a new one starts: the guard turned off (Undo) and on again in the same run must check
        // this logon again, or it stays unprotected until the next start.
        lock (_guardStartLock)
        {
            if (_guardTask is not { IsCompleted: false }) _guardTask = Task.Run(() => Guard.RunAtLogon(ui, _guardStop.Token));
            return _guardTask;
        }
    }

    /// <summary>PoolWatch exits: if the check is still running, turn the workaround on before going.</summary>
    public void StopTokenGuard()
    {
        Guard.OnExit();
        _guardStop.Cancel();
    }

    private static readonly string OwnImage = Path.GetFileName(Environment.ProcessPath ?? "PoolWatch.exe");
    private DateTime _ownHelperSeen = DateTime.MinValue;

    /// <summary>A child of the guard's helper: a PoolWatch.exe started by this PoolWatch (via its hidden cmd.exe).</summary>
    private bool IsOwnHelper(int parentPid)
    {
        string? image = _engine.Registry.GetLive(parentPid)?.Image ?? _engine.Registry.RecentImage(parentPid);
        bool own = image is not null && (image.Equals(OwnImage, StringComparison.OrdinalIgnoreCase) || parentPid == Environment.ProcessId);
        if (own) _ownHelperSeen = DateTime.UtcNow.AddMinutes(2); // the last children are counted a little after the test ends
        return own;
    }

    private void ObserveLoad(Sample s)
    {
        int cores = Environment.ProcessorCount;
        if (_prevProcs is { } prev && s.TimeUtc > _prevProcsUtc)
            Load = ProcessLoad.Between(prev, s.Processes, s.TimeUtc - _prevProcsUtc, cores);
        _prevProcs = s.Processes;
        _prevProcsUtc = s.TimeUtc;
        lock (Costs) Costs.Observe(s.TimeUtc, s.Processes, Native.InputNative.ForegroundPid());
        var reading = Native.SystemLoadNative.Read();
        RunQueue = reading.RunQueue;
        Faults = reading.Faults;
        if (Load is not { } load) return;
        bool probe = ProbeTripping?.Invoke() ?? false;
        // C2 (review round 2): no PDH, no run queue: the rule is off (null), not fed the sum of process cores (it never tripped).
        var point = new LoadPoint(s.TimeUtc, load.CpuFraction, RunQueue, cores, probe, Faults);
        // The load series (load.csv): CPU, run queue, the fault split and whether the probe trips, every sample.
        _engine.WriteLoad(point, load.SystemPageFaultsPerSec);
        var costs = load.Processes.Take(8).Select(r =>
        {
            string identity = _engine.Registry.GetLive(r.Key.Pid) is { } rec ? Core.Identity.ProcessIdentities.Of(_engine.Registry.Ancestry(rec.Key)).Display : r.Image;
            string? svc = ServiceAt(r.Key.Pid);
            return new ProcessCost(r.Key.Pid, r.Image, svc is not null ? $"{identity} ({svc} service)" : identity, r.Cores, r.PageFaultsPerSec, svc);
        }).ToList();
        if (Saturation.Observe(point, costs) is { } answer)
        {
            var top = costs.FirstOrDefault(c => c.CatalogueService is not null && Services.Catalogue.Find(c.CatalogueService) is not null);
            Findings.Add(new Finding
            {
                TimeUtc = s.TimeUtc, Kind = FindingKind.Saturation, Severity = FindingSeverity.Alert, Title = answer,
                Data = new Dictionary<string, object?>
                {
                    ["cpuFraction"] = Math.Round(point.CpuFraction, 3), ["runQueue"] = point.RunQueue, ["cores"] = cores, ["probeTripping"] = probe,
                    ["demandZeroPerSec"] = Faults is { } f1 ? Math.Round(f1.DemandZeroPerSec) : null,
                    ["transitionPerSec"] = Faults is { } f2 ? Math.Round(f2.TransitionPerSec) : null,
                    ["pagesInputPerSec"] = Faults is { } f3 ? Math.Round(f3.PagesInputPerSec) : null,
                    ["topService"] = top?.CatalogueService, ["offerTurnOff"] = top is not null,
                },
            });
        }
    }

    /// <summary>The catalogue service hosted in a PID, if the host is a service process we know.</summary>
    private string? ServiceAt(int pid)
    {
        foreach (var e in _engine.Services)
            if (e.ProcessId == pid && Services.Catalogue.Find(e.Name) is not null) return e.Name;
        return null;
    }

    /// <summary>Toke outstanding allocations and bytes, from the latest sample (for the Overview).</summary>
    public (long? outstanding, long? bytes) TokeNow { get; private set; }

    /// <summary>Toke (token objects) outstanding allocations and bytes in a sample.</summary>
    public static (long? outstanding, long? bytes) Toke(Sample? s)
    {
        if (s is null) return (null, null);
        foreach (var t in s.Tags)
            if (t.Tag == TokeTag)
                return ((long)t.PagedAllocs + t.NonPagedAllocs - t.PagedFrees - t.NonPagedFrees, (long)t.TotalUsed);
        return (null, null);
    }
    private static readonly PoolTag TokeTag = PoolTag.Parse("Toke");

    /// <summary>Lifecycle events of the current tick (raised just before the sample, on the sampler thread).</summary>
    private readonly List<LifecycleEvent> _tickEvents = new();

    private void OnLifecycle(IReadOnlyList<LifecycleEvent> events)
    {
        // Drained every sample; bounded in case samples keep failing after the lifecycle step.
        if (_tickEvents.Count > 50_000) { _tickEvents.RemoveRange(0, _tickEvents.Count - 25_000); _engine.Log.Info("diagnostics: lifecycle backlog trimmed"); }
        _tickEvents.AddRange(events);
    }

    /// <summary>
    /// Once per tick. Diagnostics must never cost the pool sample: everything is caught and logged. The spawn tracker
    /// lock (the Findings tab reads its rates) is held only for the in-memory update; report and log writes happen after.
    /// </summary>
    private void OnSample(Sample s)
    {
        using var _ = Core.Util.Profiler.Stage("diagnostics");
        var events = _tickEvents.ToList();
        _tickEvents.Clear();
        while (_guardFindings.TryDequeue(out var gf)) Findings.Add(gf);
        TokeNow = Toke(s);
        try { ObserveLoad(s); }
        catch (Exception ex) { _engine.Log.Info("saturation / load failed: " + ex.Message); }
        try
        {
            Services.CheckSelfRepair(s.TimeUtc);
            Services.ObserveSavings(s.TimeUtc, _engine.Services);
        }
        catch (Exception ex) { _engine.Log.Info("service self-repair / savings check failed: " + ex.Message); }
        if (_guardTask is not null)
            try { Guard.Recheck(s.TimeUtc, TimeSpan.FromMinutes(_engine.Settings.TokenGuardRecheckMinutes), () => GuardUi); }
            catch (Exception ex) { _engine.Log.Info("token guard re-check failed: " + ex.Message); }
        try { Cache.Observe(s); }
        catch (Exception ex) { _engine.Log.Info("clear cache failed: " + ex.Message); }
        try
        {
            List<Finding> findings;
            List<SpawnStorm> toWrite;
            // The token-leak guard's own test (PoolWatch.exe starting 300 cmd.exe) is not a spawn storm to report.
            var spawnEvents = Guard.Checking || _ownHelperSeen > s.TimeUtc ? events.Where(e => !IsOwnHelper(e.ParentPid)).ToList() : events;
            lock (Spawns) (findings, toWrite) = Spawns.Observe(spawnEvents, s.TimeUtc);
            foreach (var storm in toWrite) WriteStorm(storm, s.TimeUtc); // sets ReportDir on the storm's latest finding
            foreach (var f in findings) Findings.Add(f);
        }
        catch (Exception ex) { _engine.Log.Info("spawn diagnostics failed: " + ex.Message); }
        try
        {
            Finding? token;
            lock (Tokens) token = Tokens.Observe(s, events);
            if (token is not null) Findings.Add(token);
        }
        catch (Exception ex) { _engine.Log.Info("token correlation failed: " + ex.Message); }
        try
        {
            if (_lagProber is not null)
                foreach (var probe in _lagProber.Drain())
                {
                    Core.Findings.Finding? lag;
                    lock (Lag) lag = Lag.Add(probe);
                    lock (_experimentLock) ClearExperiment?.AddProbe(probe);
                    _engine.WriteLag(probe);
                    if (lag is not null)
                    {
                        // Addendum 2 C: what the CPU is doing while it lags, next to the pool size (a CPU-only trace). C1: through
                        // the capture governor (CpuTraceEnabled + DeeperCapture, hourly cap, not while saturated or typing); the
                        // finding says what happened ("capture skipped: ...").
                        var (inc, note) = _analysis.CaptureForLag("lag probe tripped: " + lag.Title, lag.TimeUtc);
                        if (note is { Length: > 0 }) lag = lag with { Details = [.. lag.Details, char.ToUpperInvariant(note[0]) + note[1..] + "."] };
                        Findings.Add(lag);
                        if (inc is not null) _engine.Log.Info($"lag: CPU trace into {inc.Dir}");
                    }
                }
        }
        catch (Exception ex) { _engine.Log.Info("lag probe failed: " + ex.Message); }
        try
        {
            // C6: one inbox for the run: the folder is listed only when it changed, and undeletable files never block new marks.
            _markInbox ??= new Core.Experiments.MarkInbox(_engine.Paths.RequestsDir, _engine.Paths.Guard.CheckReal);
            foreach (var (t, text) in _markInbox.Take(s.TimeUtc)) AddMark(text, "command line", t);
            DrainOtherThreads();
        }
        catch (Exception ex) { _engine.Log.Info("mark requests failed: " + ex.Message); }
        if (s.TimeUtc >= _nextVersionCheck)
        {
            _nextVersionCheck = s.TimeUtc.AddMinutes(_engine.Settings.VersionCheckMinutes);
            try { CheckVersions(s.TimeUtc); }
            catch (Exception ex) { _engine.Log.Info("version markers failed: " + ex.Message); }
        }
        if (s.TimeUtc >= _nextCrashCheck)
        {
            _crashChecks++;
            _nextCrashCheck = s.TimeUtc + (_crashChecks == 1 ? TimeSpan.FromMinutes(5) : _crashChecks == 2 ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(_engine.Settings.VersionCheckMinutes));
            // C6: a log that filled its page has more records behind it: the next page in a minute, not at the next check.
            try { if (CheckCrashes(s.TimeUtc) && _nextCrashCheck > s.TimeUtc.AddMinutes(1)) _nextCrashCheck = s.TimeUtc.AddMinutes(1); }
            catch (Exception ex) { _engine.Log.Info("crash markers failed: " + ex.Message); }
        }
        if (Accumulation.Due(s.TimeUtc))
        try
        {
            List<Finding> acc;
            lock (Accumulation)
                acc = Accumulation.Observe(_engine.Registry.LiveRecords(), r => _engine.Registry.Ancestry(r.Key) is { Count: > 0 } chain ? Core.Identity.ProcessIdentities.Of(chain) : null, s.TimeUtc);
            foreach (var f in acc) Findings.Add(f);
        }
        catch (Exception ex) { _engine.Log.Info("child accumulation failed: " + ex.Message); }
        try { ObserveExperiment(s); }
        catch (Exception ex) { _engine.Log.Info("experiment failed: " + ex.Message); }
        try
        {
            // Item 13: before-launch / after-exit tag snapshots of the watched apps, outside experiments too.
            List<Core.Boot.AppSnapshot> taken;
            lock (AppSnapshots) taken = AppSnapshots.Observe(s.TimeUtc, s.Processes, s.Tags);
            foreach (var t in taken) _engine.Log.Info($"tag snapshot kept: {t.App} {t.Moment} ({t.Tags.Count} tags)");
        }
        catch (Exception ex) { _engine.Log.Info("app snapshots failed: " + ex.Message); }
        try { ObserveFilters(s.TimeUtc, events); }
        catch (Exception ex) { _engine.Log.Info("filter / container timeline failed: " + ex.Message); }
        if (s.TimeUtc >= _nextStrayCheck)
        {
            _nextStrayCheck = s.TimeUtc.AddMinutes(_engine.Settings.StrayGitCheckMinutes);
            try
            {
                foreach (var f in StrayGit.Run(StrayGitCandidates(), GitContext(), s.TimeUtc)) Findings.Add(f);
            }
            catch (Exception ex) { _engine.Log.Info("stray .git check failed: " + ex.Message); }
        }
    }

    private DateTime _nextVersionCheck = DateTime.MinValue;
    public IReadOnlyDictionary<string, string> Versions { get; private set; } = new Dictionary<string, string>();
    public List<Core.Versions.CrashMarkers.SystemCrash> SystemCrashes { get; } = new();
    public int AppCrashes { get; private set; }

    /// <summary>
    /// At start and every <see cref="Core.Settings.VersionCheckMinutes"/>: app versions against the last check (kept in
    /// cache\versions.txt).
    /// </summary>
    private void CheckVersions(DateTime now)
    {
        string cache = _engine.Paths.EnsureDir(_engine.Paths.CacheDir);
        string versionsFile = Path.Combine(cache, "versions.txt");
        string? aicc = _engine.Registry.LiveRecords().FirstOrDefault(r => r.Image.Equals(Core.Aicc.ProcessLabels.AiccExe, StringComparison.OrdinalIgnoreCase))?.ImagePath;
        var current = VersionCollector.Read(_engine.Settings, aicc, _engine.Log.Info);
        Dictionary<string, string>? previous = File.Exists(versionsFile) ? Core.Versions.VersionMarkers.Parse(File.ReadAllText(versionsFile)) : null;
        // AICC's version is only readable while it runs: keep the last one seen rather than report it gone.
        if (!current.ContainsKey("AI Code Conductor") && previous?.TryGetValue("AI Code Conductor", out var av) == true) current["AI Code Conductor"] = av;
        var changes = Core.Versions.VersionMarkers.Diff(previous, current, now);
        Versions = current;
        if (previous is null || changes.Count > 0) File.WriteAllText(_engine.Paths.Guard.CheckReal(versionsFile), Core.Versions.VersionMarkers.Serialize(current));
        foreach (var f in changes) Findings.Add(f);

    }

    /// <summary>The crash records read so far this run (system ones; bounded), and where reading stopped in each log.</summary>
    private readonly List<Core.Versions.CrashRecord> _systemRecords = new();
    private Core.Versions.CrashCursor? _crashCursor;
    private readonly Dictionary<Core.Versions.CrashMarkers.SystemCrash, string> _crashDirs = new(ReferenceEqualityComparer.Instance);
    private DateTime _nextCrashCheck = DateTime.MinValue;
    private int _crashChecks;

    /// <summary>
    /// Crash records since PoolWatch last looked (cache\last-seen.txt = where reading stopped in each log, or the lookback on
    /// a first run): at start, again 5 and 15 minutes later (WER writes the BugCheck record and the BlueScreen bucket a few
    /// minutes after logon), then with every version check. Records of one restart that arrive in different reads still make
    /// one crash: its report folder is rewritten with the later records. C6: each log is paged through by record ID (a
    /// bounded page per check, oldest first), so records beyond a page are read at the next check, never skipped. Returns
    /// true when a log filled its page (more to read).
    /// </summary>
    private bool CheckCrashes(DateTime now)
    {
        string seenFile = Path.Combine(_engine.Paths.EnsureDir(_engine.Paths.CacheDir), "last-seen.txt");
        if (_crashCursor is null)
        {
            string? text = null;
            try { if (File.Exists(seenFile)) text = File.ReadAllText(seenFile); }
            catch (Exception ex) { _engine.Log.Info("last-seen.txt not read (reading the lookback): " + ex.Message); }
            _crashCursor = Core.Versions.CrashCursor.Parse(text, DateTime.UtcNow);
        }
        var (events, more) = VersionCollector.CrashEvents(_crashCursor, DateTime.UtcNow, _engine.Settings.CrashLookbackDays, _engine.Log.Info);
        var records = events.Select(e => Core.Versions.CrashMarkers.Parse(e, _engine.Settings.WatchedApps)).OfType<Core.Versions.CrashRecord>().ToList();
        try { File.WriteAllText(_engine.Paths.Guard.CheckReal(seenFile), _crashCursor.Format()); }
        catch (Exception ex) { _engine.Log.Info("last-seen.txt not saved: " + ex.Message); } // read again after a restart: repeats, never gaps

        var newSystem = records.Where(r => r.Kind is Core.Versions.CrashKind.BugCheck or Core.Versions.CrashKind.KernelPower or Core.Versions.CrashKind.BlueScreenReport).ToList();
        if (newSystem.Count > 0)
        {
            _systemRecords.AddRange(newSystem);
            if (_systemRecords.Count > 500) _systemRecords.RemoveRange(0, _systemRecords.Count - 500);
            var before = SystemCrashes.ToList();
            var crashes = Core.Versions.CrashMarkers.SystemCrashes(_systemRecords);
            SystemCrashes.Clear();
            SystemCrashes.AddRange(crashes);
            foreach (var c in crashes)
            {
                // The same crash as one already reported (it shares its first record): rewrite that report with the new records.
                var old = before.FirstOrDefault(o => o.Records[0] == c.Records[0]);
                if (old is not null && old.Records.Count == c.Records.Count) { if (_crashDirs.Remove(old, out var d0)) _crashDirs[c] = d0; continue; }
                var f = Core.Versions.CrashMarkers.ToFinding(c);
                string? dir = old is not null && _crashDirs.Remove(old, out var d) ? d : null;
                try
                {
                    dir ??= FindingReport.NewDir(_engine.Paths.IncidentsDir, f, c.Code ?? "restart");
                    var md = new System.Text.StringBuilder("## Records\n\n");
                    foreach (var r in c.Records) md.AppendLine($"- {Core.Detection.Fmt.Stamp(r.TimeUtc)} {r.Kind}: {FindingReport.Md(r.Text)}");
                    md.AppendLine().AppendLine("Read from the System and Application event logs (records since PoolWatch last looked).");
                    FindingReport.Write(dir, f, md.ToString(), null, _engine.Paths.Guard.CheckReal);
                    f.ReportDir = dir;
                    _crashDirs[c] = dir;
                }
                catch (Exception ex) { _engine.Log.Info("crash report failed: " + ex.Message); }
                if (old is not null) f = f with { Title = f.Title + " (updated with later records)" };
                Findings.Add(f);
            }
        }
        // An app crash usually has both records (Application Error and the WER report a moment later): one finding.
        var apps = records.Where(r => r.Kind == Core.Versions.CrashKind.AppCrash).ToList();
        foreach (var rep in records.Where(r => r.Kind == Core.Versions.CrashKind.AppCrashReport))
        {
            int i = apps.FindIndex(x => string.Equals(x.App, rep.App, StringComparison.OrdinalIgnoreCase) && x.Bucket is null && (rep.TimeUtc - x.TimeUtc).Duration() <= TimeSpan.FromMinutes(2));
            if (i >= 0) apps[i] = apps[i] with { Bucket = rep.Bucket, Text = apps[i].Text + $", fault bucket {rep.Bucket}" };
            else apps.Add(rep);
        }
        // C6: the newest 50 one by one; any older ones in one summary finding (they were dropped before).
        foreach (var f in Core.Versions.CrashMarkers.AppFindings(apps)) Findings.Add(f);
        AppCrashes += apps.Count;
        return more;
    }

    private void ObserveExperiment(Sample s)
    {
        ObserveClearExperiment(s);
        Core.Experiments.ExperimentStep? step;
        Core.Experiments.Experiment? e;
        lock (_experimentLock)
        {
            e = Experiment;
            step = e?.Observe(s);
        }
        if (step is null || e is null) return;
        if (e.Result is { } r)
        {
            try
            {
                string dir = FindingReport.NewDir(_engine.Paths.IncidentsDir, step.Finding, e.App);
                FindingReport.Write(dir, step.Finding, r.Markdown(e.App, e.PhaseLength) + "\n" + _engine.Meter.OwnCostLine() + "\n",
                    new Dictionary<string, string> { ["experiment.csv"] = r.Csv() }, _engine.Paths.Guard.CheckReal);
                step.Finding.ReportDir = dir;
            }
            catch (Exception ex) { _engine.Log.Info("experiment report failed: " + ex.Message); }
        }
        Findings.Add(step.Finding);
        ExperimentStep?.Invoke(step);
    }

    /// <summary>The clear preset: at the end of phase 1 PoolWatch clears the standby cache itself (trigger Experiment).</summary>
    private void ObserveClearExperiment(Sample s)
    {
        (Core.Experiments.ExperimentStep step, bool clearNow)? r;
        Core.Experiments.ClearLagExperiment? c;
        lock (_experimentLock)
        {
            c = ClearExperiment;
            r = c?.Observe(s);
        }
        if (r is not { } x || c is null) return;
        if (x.clearNow)
        {
            string? error = !CacheHelper.IsElevated ? "Clear cache needs PoolWatch to run as administrator"
                : !Cache.Request(Core.Cache.ClearMode.Standby, Core.Cache.ClearTrigger.Experiment) ? "a clear was already running" : null;
            if (error is not null) lock (_experimentLock) c.ClearError = error;
        }
        if (c.Statement is { } st)
        {
            try
            {
                string dir = FindingReport.NewDir(_engine.Paths.IncidentsDir, x.step.Finding, "lag-vs-clear");
                string md = "## In plain words\n\n" + FindingReport.Md(st) + "\n\n" + string.Concat(c.Notes.Select(n => "- " + FindingReport.Md(n) + "\n")) +
                            "\nThe clear itself is its own CacheClear finding (pool and top tags before and 2 min after).\n\n" + _engine.Meter.OwnCostLine() + "\n";
                FindingReport.Write(dir, x.step.Finding, md, new Dictionary<string, string>(), _engine.Paths.Guard.CheckReal);
                x.step.Finding.ReportDir = dir;
            }
            catch (Exception ex) { _engine.Log.Info("experiment report failed: " + ex.Message); }
        }
        Findings.Add(x.step.Finding);
        ExperimentStep?.Invoke(x.step);
    }

    /// <summary>Filter / package events, container processes and services, and (hourly or on alert) the filter instances.</summary>
    private void ObserveFilters(DateTime now, IReadOnlyList<LifecycleEvent> events)
    {
        var found = new List<Finding>();
        lock (Filters)
        {
            foreach (var e in EventLog.Drain()) found.AddRange(Filters.OnLogEvent(e));
            found.AddRange(Filters.OnLifecycle(events, _engine.Registry.LiveRecords, now));
            if (now >= _nextServices)
            {
                _nextServices = now.AddSeconds(_engine.Settings.ServiceIntervalSeconds);
                try { found.AddRange(Filters.OnServices(ServiceStates.Query(Core.Filters.FilterTimeline.WatchedServices), now)); }
                catch (Exception ex) { _engine.Log.Info("service state query failed: " + ex.Message); }
            }
            if (now >= _nextInstances || _instancesRequested)
            {
                _instancesRequested = false;
                _nextInstances = now.AddMinutes(60);
                var (instances, note) = FilterInstanceQuery.Query();
                found.AddRange(Filters.OnInstances(instances, note, now));
            }
        }
        foreach (var f in found) Findings.Add(f);
    }

    private void WriteStorm(SpawnStorm storm, DateTime now)
    {
        try
        {
            var head = new Finding
            {
                TimeUtc = storm.OpenedUtc, Kind = FindingKind.SpawnStorm, Severity = FindingSeverity.Alert,
                Title = $"Spawn storm: {storm.ParentImage} (PID {storm.ParentPid}) started {storm.TotalStarts:N0} processes" +
                        $" (peak {storm.PeakShortLived} short-lived per {_engine.Settings.SpawnStormWindowSeconds:0} s){(storm.IsOpen ? ", still running" : "")}",
                Pid = storm.ParentPid, Image = storm.ParentImage, Data = Spawns.StormData(storm),
            };
            storm.ReportDir ??= FindingReport.NewDir(_engine.Paths.IncidentsDir, head, Path.GetFileNameWithoutExtension(storm.ParentImage.Split(' ')[0]));
            // spawns.csv (up to 2,000 rows) when it gained rows, and once more at close / exit (late exit codes).
            bool final = !storm.IsOpen || _finishing;
            var extra = storm.Trace.Count != storm.TraceWritten || final ? new Dictionary<string, string> { ["spawns.csv"] = SpawnTracker.TraceCsv(storm) } : null;
            FindingReport.Write(storm.ReportDir, head, Spawns.StormMarkdown(storm) + "\n" + _engine.Meter.OwnCostLine() + "\n", extra, _engine.Paths.Guard.CheckReal);
            storm.TraceWritten = storm.Trace.Count;
            storm.LastWriteUtc = now;
            if (storm.LastFinding is { } lf) lf.ReportDir = storm.ReportDir;
        }
        catch (Exception ex)
        {
            storm.LastWriteUtc = now; // back off to the normal refresh interval instead of retrying every tick
            _engine.Log.Info("spawn storm report failed: " + ex.Message);
        }
    }

    /// <summary>End of a run: rewrite the reports of storms that are still open with their final numbers.</summary>
    private bool _finishing;

    public void Finish()
    {
        _finishing = true;
        CancelExperiment();
        DrainOtherThreads(); // the sampler has stopped: record what is still queued
        _lagProber?.Dispose();
        EventLog.Dispose();
        lock (Spawns)
            foreach (var storm in Spawns.OpenStorms.ToList()) WriteStorm(storm, DateTime.UtcNow);
    }

    /// <summary>Every finding: log, findings.jsonl, events.csv and the incident timelines.</summary>
    private void OnFinding(Finding f)
    {
        _engine.Log.Info($"finding {f.Kind}: {f.Title}");
        try
        {
            // Today's folder, also for a finding dated earlier (a replayed event-log record): never re-create an old day.
            string dir = _engine.Paths.EnsureDir(_engine.Paths.CsvDir(DateTime.UtcNow));
            string line = FindingLog.ToJsonLine(f) + "\n";
            File.AppendAllText(_engine.Paths.Guard.CheckReal(Path.Combine(dir, "findings.jsonl")), line);
            Core.Util.Profiler.Wrote("findings.jsonl", line.Length);
        }
        catch (Exception ex) { _engine.Log.Info("findings.jsonl write failed: " + ex.Message); }
        var ev = new LifecycleEvent
        {
            TimeUtc = f.TimeUtc, Kind = LifecycleKind.Finding, Pid = f.Pid, Image = f.Kind.ToString(), Detail = f.Title,
        };
        try
        {
            _engine.RecordEvents([ev]);
            lock (_analysis.History.SyncRoot) _analysis.Incidents.AddEvents([ev]);
        }
        catch (Exception ex) { _engine.Log.Info("finding event write failed: " + ex.Message); }
    }

    /// <summary>Parents' working directories (PEB reads), kept for a minute: a parent can change directory.</summary>
    private readonly Dictionary<ProcessKey, (string? cwd, DateTime until)> _cwdCache = new();

    /// <summary>The parent's working directory for a git start without -C. Kept apart from the record's session context.</summary>
    private string? CwdOf(ProcessKey key)
    {
        var now = DateTime.UtcNow;
        if (_cwdCache.TryGetValue(key, out var c) && c.until > now) return c.cwd;
        var rec = _engine.Registry.Get(key);
        string? cwd = rec is { IsAlive: true } ? PebReader.Read(key.Pid, key.CreateTime, [])?.cwd : null;
        if (_cwdCache.Count > 512) _cwdCache.Clear();
        _cwdCache[key] = (cwd, now.AddMinutes(1));
        return cwd;
    }

    internal static GitRootContext GitContext()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var profiles = new List<string>();
        if (profile.Length > 0) profiles.Add(profile);
        return new GitRootContext(profiles, OneDriveRoots(), profile.Length > 0 ? Path.GetDirectoryName(profile) : null);
    }

    /// <summary>OneDrive roots from the environment and the per-account registry entries (HKCU, read-only).</summary>
    internal static List<string> OneDriveRoots()
    {
        var roots = new List<string>();
        foreach (var v in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } p) roots.Add(p);
        try
        {
            using var accounts = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
            foreach (var name in accounts?.GetSubKeyNames() ?? [])
            {
                using var k = accounts!.OpenSubKey(name);
                if (k?.GetValue("UserFolder") is string folder && folder.Length > 0) roots.Add(folder);
            }
        }
        catch { }
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private IEnumerable<(string, string)> StrayGitCandidates()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 0) yield return (profile, "the user profile");
        var onedrive = OneDriveRoots();
        foreach (var od in onedrive) yield return (od, "a OneDrive root");
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (desktop.Length > 0 && onedrive.Any(od => WinPath.IsSameOrUnder(desktop, od))) yield return (desktop, "the OneDrive Desktop");
        foreach (var v in _engine.Volumes.Where(v => v.DriveType == "Fixed")) yield return (v.Root, "a volume root");
    }

    /// <summary>Real file-system probes for the git-root resolver: single explicit paths, fixed drives only.</summary>
    private sealed class FileProbe : IFileProbe
    {
        private readonly Dictionary<char, (bool fixedDrive, DateTime until)> _drives = new();

        public bool DirectoryExists(string path) => Directory.Exists(path);
        public bool FileExists(string path) => File.Exists(path);

        public byte[]? ReadHead(string path, int count)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
                var buf = new byte[count];
                int n = fs.ReadAtLeast(buf, count, throwOnEndOfStream: false);
                return n == count ? buf : buf[..n];
            }
            catch { return null; }
        }

        public bool IsLocalFixed(string path)
        {
            if (path.Length < 2 || path[1] != ':' || !char.IsAsciiLetter(path[0])) return false;
            char d = char.ToUpperInvariant(path[0]);
            lock (_drives)
            {
                if (_drives.TryGetValue(d, out var c) && c.until > DateTime.UtcNow) return c.fixedDrive;
                bool f = Native.NativeMethods.GetDriveTypeW(d + ":\\") == 3;
                _drives[d] = (f, DateTime.UtcNow.AddMinutes(5));
                return f;
            }
        }
    }
}
