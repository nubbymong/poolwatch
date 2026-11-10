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

    public Diagnostics(MonitorEngine engine, AnalysisPipeline analysis)
    {
        _engine = engine;
        _analysis = analysis;
        Git = new GitRootResolver(_probe);
        Spawns = new SpawnTracker(engine.Settings, engine.Registry, Git, GitContext(), CwdOf);
        StrayGit = new StrayGitCheck(_probe, Git);
        Tokens = new Core.Tokens.TokenCorrelator(engine.Settings, engine.Registry);
        Lag = new Core.Lag.LagMonitor(engine.Settings);
        // "PC is saturated" when the run queue stays above the core count OR the responsiveness probe
        // trips, for over 60 s. The probe's state counts only while it is fresh (three probe intervals, at least 30 s).
        var probeFresh = TimeSpan.FromSeconds(Math.Max(30, 3 * engine.Settings.LagProbeSeconds));
        ProbeTripping = () => { lock (Lag) return Lag.Tripping(Environment.TickCount64, probeFresh); }; // freshness on the clock that only goes forward
        Accumulation = new Core.Spawn.ChildAccumulation(engine.Settings);
        AppSnapshots = new Core.Boot.AppSnapshots(engine.Settings.WatchedApps);
        if (engine.Settings.LagProbeSeconds > 0)
        {
            string probe = engine.Paths.Guard.CheckReal(Path.Combine(engine.Paths.EnsureDir(engine.Paths.CacheDir), "lag-probe.tmp"));
            _lagProber = new LagProber(TimeSpan.FromSeconds(engine.Settings.LagProbeSeconds), probe, () => engine.Latest?.Totals ?? default, SelfWorkPausesProbe,
                engine.Paths.Guard.CheckReal);
        }
        EventLog = new EventLogFeed(engine.Log.Info, Path.Combine(engine.Paths.EnsureDir(engine.Paths.CacheDir), "eventlog-last.txt"), engine.Paths.Guard.CheckReal);
        // The event-log feed is a diagnostic extra; nothing in it may stop PoolWatch from starting.
        try { EventLog.Start(); }
        catch (Exception ex) { engine.Log.Info("event log feed not started: " + ex.Message); }
        analysis.Incidents.Opened += (_, _) => _instancesRequested = true;
        Cache = new CacheClearer(engine, Findings, () => AnyExperimentActive);
        // Made whatever the guard setting; it hooks its exit failsafe as it is made (TokenLeakGuard.HookProcessExit).
        Guard = new TokenLeakGuard(engine.Paths, engine.Log, f => _guardFindings.Enqueue(f));
        // Service changes are made on a worker thread (the page's Apply / Restore): their findings are queued and recorded on
        // the sampler's next tick, like every other finding raised off the sampler thread.
        Services = new ServicesController(engine.Paths, engine.Log, f => _fromOtherThreads.Enqueue(f), LiveCost, RecentCost);
        Findings.Added += OnFinding;
        // A data folder moved aside or repaired at start is on the timeline (recorded on the first tick).
        foreach (var n in engine.Paths.StartupNotes)
            _fromOtherThreads.Enqueue(new Finding { TimeUtc = DateTime.UtcNow, Kind = FindingKind.Setup, Severity = FindingSeverity.Warning, Title = "Data folder: " + n });
        engine.Lifecycle += OnLifecycle;
        engine.Sampled += OnSample;
        _lagProber?.Start(); // after Cache: its pause reads the clear's state
    }

    /// <summary>
    /// PoolWatch's own trace (recording or parsing) or cache clear is running, or ended under 30 s ago. The lag probe
    /// pauses then (probe thread), so lag PoolWatch causes itself is not recorded as a pool lag onset.
    /// At most 10 min for one piece of work. A capture thread that hangs (a stuck parse) used to keep
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
    /// <summary>Each watched app's last tag snapshot before it started and first after it exited (lock it to read).</summary>
    public Core.Boot.AppSnapshots AppSnapshots { get; }
    private readonly LagProber? _lagProber;
    public bool DwmAvailable => _lagProber?.DwmAvailable ?? false;
    public EventLogFeed EventLog { get; }
    /// <summary>When the version, crash, stray .git and filter checks run, on the clock that only goes forward.</summary>
    private readonly CheckSchedule _checks = new();

    /// <summary>The running (or last) A/B experiment. Read under its own lock.</summary>
    public Core.Experiments.Experiment? Experiment { get; private set; }
    private readonly object _experimentLock = new();
    /// <summary>Held while reading <see cref="Experiment"/> from another thread.</summary>
    public object ExperimentLock => _experimentLock;
    /// <summary>Raised on the sampler thread at each experiment phase change (the tray shows the prompt).</summary>
    public event Action<Core.Experiments.ExperimentStep>? ExperimentStep;

    /// <summary>The "lag before vs after a clear" preset, when that was the last experiment asked for.</summary>
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
            long nowMs = Core.Model.Moment.Now().Ms; // the minutes left are timed on the forward clock
            if (ClearExperiment is { } c) return c.Statement is { } st ? $"{c.Status(nowMs)}: {st}" : c.Status(nowMs);
            return Experiment is { } e ? (e.Result is { } r ? $"{e.Status(nowMs)}: {r.Statement}" : e.Status(nowMs)) : "";
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
    /// <summary>Clear cache: manual and automatic standby purges, measured.</summary>
    public CacheClearer Cache { get; }
    /// <summary>The token-leak guard. Its findings come from worker threads: queued, recorded on the sampler.</summary>
    public TokenLeakGuard Guard { get; }
    /// <summary>The background-services page: enumerate, apply/restore, self-repair watch.</summary>
    public ServicesController Services { get; }
    /// <summary>The "PC is saturated" detector.</summary>
    public SaturationDetector Saturation { get; } = new();
    /// <summary>Per-process CPU and page-fault rates in the last window (for the saturation answer and the page).</summary>
    public ProcessLoad.Rates? Load { get; private set; }
    /// <summary>The system-wide page-fault split in the last window (demand-zero / transition / hard); null until it has a reading.</summary>
    public FaultSplit? Faults { get; private set; }
    /// <summary>Minute points of the busy processes' CPU and page faults (before-costs, heavy background apps). Lock it to read.</summary>
    public ProcessCostHistory Costs { get; } = new();

    /// <summary>A PID's cost in the latest window (the services page's Cost column); null before the first window.</summary>
    public (double cores, long faultsPerSec)? CostOfPid(int pid) => LiveCost(pid);

    /// <summary>Heavy background apps: over 0.5 core for the last 30 minutes with no window in front.</summary>
    public List<HeavyProcess> HeavyBackground()
    {
        lock (Costs) return Costs.Heavy(ProcessCostHistory.ClockNow(), TimeSpan.FromMinutes(30), 0.5);
    }

    /// <summary>A finding raised off the sampler thread (the logon-task check): recorded on the next tick.</summary>
    public void RecordFromOtherThread(Finding f) => _fromOtherThreads.Enqueue(f);

    /// <summary>A change the owner made on the Background services page (task / startup entry): a timeline finding. Any thread.</summary>
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
                if (Costs.Average(rec.Key, TimeSpan.FromMinutes(10), ProcessCostHistory.ClockNow()) is { } avg) return (avg.cores, avg.faultsPerSec);
        return LiveCost(pid);
    }
    public double? RunQueue { get; private set; }
    /// <summary>The sample before this one, for the load per process between the two (<see cref="StepLoad"/>).</summary>
    private Sample? _prevSample;
    /// <summary>Whether the lag probe is tripping now: one of the two saturation conditions (wired in the constructor).</summary>
    public Func<bool>? ProbeTripping { get; set; }
    /// <summary>The tray UI's notices for the guard (null headless).</summary>
    public IGuardUi? GuardUi { get; set; }
    private readonly System.Collections.Concurrent.ConcurrentQueue<Finding> _guardFindings = new();
    private Task? _guardTask;
    private readonly object _guardStartLock = new();
    private readonly CancellationTokenSource _guardStop = new();

    /// <summary>
    /// The logon check, on a worker thread (the tray app at start-up; headless with --token-guard; the guard turned on later).
    /// While a check runs, the caller gets that one.
    /// </summary>
    public Task StartTokenGuard(IGuardUi? ui)
    {
        GuardUi = ui;
        // A crash during the leak test must still leave the logon protected. The clean shutdown path calls
        // OnExit via StopTokenGuard; an unhandled exception or a bare process exit call it too, through the failsafe the
        // guard hooked when it was made, whatever the guard setting (TokenLeakGuard.HookProcessExit). Not a kill or a native
        // crash (no handler runs): the logon check's helper then asks for the one click, and the next start's logon check
        // re-applies the workaround without testing (TokenLeakGuard.OnExit, GuardStep.TurnOnInterrupted).
        // Once a check has finished, a new one starts: the guard turned off (Undo) and on again in the same run must check
        // this logon again, or it stays unprotected until the next start.
        // Turned on: the guard may set the setting again, also in a check still under way since it was turned off.
        Guard.TurnedOn();
        lock (_guardStartLock)
        {
            if (_guardTask is not { IsCompleted: false }) _guardTask = Task.Run(() => Guard.RunAtLogon(ui, _guardStop.Token));
            return _guardTask;
        }
    }

    /// <summary>
    /// PoolWatch exits: if the logon check is still running, turn the workaround on before going; a check-only helper that
    /// runs in a job ends (<see cref="TokenLeakGuard.OnExit"/>).
    /// </summary>
    public void StopTokenGuard()
    {
        Guard.OnExit();
        _guardStop.Cancel();
    }

    private static readonly string OwnImage = Path.GetFileName(Environment.ProcessPath ?? "PoolWatch.exe");
    /// <summary>Until when (Environment.TickCount64, monotonic) the guard's helper's children are still left out of the spawn diagnostics.</summary>
    private long _ownHelperUntilMs;

    /// <summary>A child of the guard's helper: a PoolWatch.exe started by this PoolWatch (via its hidden cmd.exe).</summary>
    private bool IsOwnHelper(int parentPid)
    {
        string? image = _engine.Registry.GetLive(parentPid)?.Image ?? _engine.Registry.RecentImage(parentPid);
        bool own = image is not null && (image.Equals(OwnImage, StringComparison.OrdinalIgnoreCase) || parentPid == Environment.ProcessId);
        if (own) _ownHelperUntilMs = Environment.TickCount64 + 120_000; // the last children are counted a little after the test ends
        return own;
    }

    /// <summary>
    /// One sample's step of the processes' costs: a minute point in <paramref name="costs"/>, and the load per process
    /// since <paramref name="before"/> (the Services tab's Cost column, a change's before-cost when no average is held),
    /// null when there is no sample before or no time passed. Both are timed on the samples' <see cref="Sample.TickMs"/>, a
    /// clock that only goes forward, never on <see cref="Sample.TimeUtc"/>: setting the system clock back neither freezes
    /// them until it catches up nor gives a rate over a wrong time.
    /// </summary>
    internal static ProcessLoad.Rates? StepLoad(Sample s, Sample? before, ProcessCostHistory costs, int cores, int foregroundPid)
    {
        lock (costs) costs.Observe(TimeSpan.FromMilliseconds(s.TickMs), s.Processes, foregroundPid);
        return before is not null && s.TickMs > before.TickMs
            ? ProcessLoad.Between(before.Processes, s.Processes, TimeSpan.FromMilliseconds(s.TickMs - before.TickMs), cores)
            : null;
    }

    /// <summary>
    /// The system-wide load at sample <paramref name="s"/>, stamped with both of its times: the "PC is saturated" check
    /// times how long load has lasted on <see cref="LoadPoint.TickMs"/>, the sample's <see cref="Sample.TickMs"/>.
    /// </summary>
    internal static LoadPoint PointOf(Sample s, double cpuFraction, double? runQueue, int cores, bool probeTripping, FaultSplit? faults) =>
        new(s.TimeUtc, s.TickMs, cpuFraction, runQueue, cores, probeTripping, faults);

    private void ObserveLoad(Sample s)
    {
        int cores = Environment.ProcessorCount;
        if (StepLoad(s, _prevSample, Costs, cores, Native.InputNative.ForegroundPid()) is { } rates) Load = rates;
        _prevSample = s;
        var reading = Native.SystemLoadNative.Read();
        RunQueue = reading.RunQueue;
        Faults = reading.Faults;
        if (Load is not { } load) return;
        bool probe = ProbeTripping?.Invoke() ?? false;
        // No PDH, no run queue: the rule is off (null), not fed the sum of process cores (it never tripped).
        var point = PointOf(s, load.CpuFraction, RunQueue, cores, probe, Faults);
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
            Services.CheckSelfRepair();
            Services.ObserveSavings(_engine.Services);
        }
        catch (Exception ex) { _engine.Log.Info("service self-repair / savings check failed: " + ex.Message); }
        if (_guardTask is not null)
            try { Guard.Recheck(TimeSpan.FromMinutes(_engine.Settings.TokenGuardRecheckMinutes), () => GuardUi); }
            catch (Exception ex) { _engine.Log.Info("token guard re-check failed: " + ex.Message); }
        try { Cache.Observe(s); }
        catch (Exception ex) { _engine.Log.Info("clear cache failed: " + ex.Message); }
        try
        {
            List<Finding> findings;
            List<StormFolder> toWrite;
            // The token-leak guard's own test (PoolWatch.exe starting 300 cmd.exe) is not a spawn storm to report.
            var spawnEvents = Guard.Checking || Environment.TickCount64 < _ownHelperUntilMs ? events.Where(e => !IsOwnHelper(e.ParentPid)).ToList() : events;
            lock (Spawns) (findings, toWrite) = Spawns.Observe(spawnEvents, Core.Model.Moment.Of(s));
            foreach (var folder in toWrite) WriteStorm(folder, s.TickMs); // sets ReportDir on its storms' latest findings
            foreach (var f in findings) Findings.Add(f);
        }
        catch (Exception ex) { _engine.Log.Info("spawn diagnostics failed: " + ex.Message); }
        try
        {
            Finding? token;
            lock (Tokens) token = Tokens.Observe(s, events, Environment.TickCount64);
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
                        // What the CPU is doing while it lags, next to the pool size (a CPU-only trace). Through
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
            // One inbox for the run: the folder is listed only when it changed, and undeletable files never block new marks.
            _markInbox ??= new Core.Experiments.MarkInbox(_engine.Paths.RequestsDir, _engine.Paths.CheckRealNoWait);
            foreach (var (t, text) in _markInbox.Take(s.TickMs)) AddMark(text, "command line", t);
            DrainOtherThreads();
        }
        catch (Exception ex) { _engine.Log.Info("mark requests failed: " + ex.Message); }
        // The checks' schedules are timed on the clock that only goes forward (the sample's own moment). The filter manager's
        // services and instances are counted as done where they are read (ObserveFilters), so a failure before them leaves
        // them, and an incident's request for the instances, due at the next sample.
        var due = _checks.At(s, _engine.Settings, _instancesRequested);
        if (due.Version)
        {
            try { CheckVersions(s.TimeUtc); }
            catch (Exception ex) { _engine.Log.Info("version markers failed: " + ex.Message); }
        }
        if (due.Crash)
        {
            // A log that filled its page has more records behind it: the next page in a minute, not at the next check.
            try { if (CheckCrashes(s.TimeUtc)) _checks.CrashPageFull(s); }
            catch (Exception ex) { _engine.Log.Info("crash markers failed: " + ex.Message); }
        }
        if (Accumulation.Due(Core.Model.Moment.Of(s)))
        try
        {
            List<Finding> acc;
            lock (Accumulation)
                acc = Accumulation.Observe(_engine.Registry.LiveRecords(), r => _engine.Registry.Ancestry(r.Key) is { Count: > 0 } chain ? Core.Identity.ProcessIdentities.Of(chain) : null, Core.Model.Moment.Of(s));
            foreach (var f in acc) Findings.Add(f);
        }
        catch (Exception ex) { _engine.Log.Info("child accumulation failed: " + ex.Message); }
        try { ObserveExperiment(s); }
        catch (Exception ex) { _engine.Log.Info("experiment failed: " + ex.Message); }
        try
        {
            // Before-launch / after-exit tag snapshots of the watched apps, outside experiments too.
            List<Core.Boot.AppSnapshot> taken;
            lock (AppSnapshots) taken = AppSnapshots.Observe(s.TimeUtc, s.Processes, s.Tags);
            foreach (var t in taken) _engine.Log.Info($"tag snapshot kept: {t.App} {t.Moment} ({t.Tags.Count} tags)");
        }
        catch (Exception ex) { _engine.Log.Info("app snapshots failed: " + ex.Message); }
        try { ObserveFilters(s, due, events); }
        catch (Exception ex) { _engine.Log.Info("filter / container timeline failed: " + ex.Message); }
        if (due.StrayGit)
        {
            try
            {
                foreach (var f in StrayGit.Run(StrayGitCandidates(), GitContext(), s.TimeUtc)) Findings.Add(f);
            }
            catch (Exception ex) { _engine.Log.Info("stray .git check failed: " + ex.Message); }
        }
    }

    public IReadOnlyDictionary<string, string> Versions { get; private set; } = new Dictionary<string, string>();
    public List<Core.Versions.CrashMarkers.SystemCrash> SystemCrashes { get; } = new();
    public int AppCrashes { get; private set; }

    /// <summary>
    /// Every live process named like AICC's exe, from any folder, in the order seen: VersionCollector takes the version of
    /// the one that is AICC's own (<see cref="VersionCollector.AiccVersion"/>), so none is passed over by name alone.
    /// </summary>
    internal static List<string?> AiccCandidates(IEnumerable<ProcessRecord> live) =>
        live.Where(r => r.Image.Equals(Core.Aicc.ProcessLabels.AiccExe, StringComparison.OrdinalIgnoreCase)).Select(r => r.ImagePath).ToList();

    /// <summary>
    /// At start and every <see cref="Core.Settings.VersionCheckMinutes"/>: app versions against the last check (kept in
    /// cache\versions.txt).
    /// </summary>
    private void CheckVersions(DateTime now)
    {
        string cache = _engine.Paths.EnsureDir(_engine.Paths.CacheDir);
        string versionsFile = Path.Combine(cache, "versions.txt");
        var current = VersionCollector.Read(_engine.Settings, AiccCandidates(_engine.Registry.LiveRecords()), _engine.Log.Info);
        Dictionary<string, string>? previous = File.Exists(versionsFile) ? Core.Versions.VersionMarkers.Parse(PoolWatch.Engine.NoWaitOpen.ReadAllText(versionsFile)) : null;
        // AICC's version is only readable while it runs: keep the last one seen rather than report it gone.
        if (!current.ContainsKey("AI Code Conductor") && previous?.TryGetValue("AI Code Conductor", out var av) == true) current["AI Code Conductor"] = av;
        var changes = Core.Versions.VersionMarkers.Diff(previous, current, now);
        Versions = current;
        if (previous is null || changes.Count > 0) PoolWatch.Engine.NoWaitOpen.WriteAllText(_engine.Paths.Guard.CheckReal(versionsFile), Core.Versions.VersionMarkers.Serialize(current));
        foreach (var f in changes) Findings.Add(f);

    }

    /// <summary>The crash records read so far this run (system ones; bounded), and where reading stopped in each log.</summary>
    private readonly List<Core.Versions.CrashRecord> _systemRecords = new();
    private Core.Versions.CrashCursor? _crashCursor;
    private readonly Dictionary<Core.Versions.CrashMarkers.SystemCrash, string> _crashDirs = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Crash records since PoolWatch last looked (cache\last-seen.txt = where reading stopped in each log, or the lookback on
    /// a first run): at start, again 5 and 15 minutes later (WER writes the BugCheck record and the BlueScreen bucket a few
    /// minutes after logon), then with every version check. Records of one restart that arrive in different reads still make
    /// one crash: its report folder is rewritten with the later records. Each log is paged through by record ID (a
    /// bounded page per check, oldest first), so records beyond a page are read at the next check, never skipped. Returns
    /// true when a log filled its page (more to read).
    /// </summary>
    private bool CheckCrashes(DateTime now)
    {
        string seenFile = Path.Combine(_engine.Paths.EnsureDir(_engine.Paths.CacheDir), "last-seen.txt");
        if (_crashCursor is null)
        {
            string? text = null;
            try { if (File.Exists(seenFile)) text = PoolWatch.Engine.NoWaitOpen.ReadAllText(seenFile); }
            catch (Exception ex) { _engine.Log.Info("last-seen.txt not read (reading the lookback): " + ex.Message); }
            _crashCursor = Core.Versions.CrashCursor.Parse(text, DateTime.UtcNow);
        }
        var (events, more) = VersionCollector.CrashEvents(_crashCursor, DateTime.UtcNow, _engine.Settings.CrashLookbackDays, _engine.Log.Info);
        var records = events.Select(e => Core.Versions.CrashMarkers.Parse(e, _engine.Settings.WatchedApps)).OfType<Core.Versions.CrashRecord>().ToList();
        try { PoolWatch.Engine.NoWaitOpen.WriteAllText(_engine.Paths.Guard.CheckReal(seenFile), _crashCursor.Format()); }
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
                    FindingReport.Write(dir, f, md.ToString(), null, _engine.Paths.CheckRealNoWait);
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
        // The newest 50 one by one; any older ones in one summary finding (they were dropped before).
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
                    new Dictionary<string, string> { ["experiment.csv"] = r.Csv() }, _engine.Paths.CheckRealNoWait);
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
                string md = "## In plain words\n\n" + Core.Util.MdText.Line(st) + "\n\n" + string.Concat(c.Notes.Select(n => "- " + FindingReport.Md(n) + "\n")) +
                            "\nThe clear itself is its own CacheClear finding (pool and top tags before and 2 min after).\n\n" + _engine.Meter.OwnCostLine() + "\n";
                FindingReport.Write(dir, x.step.Finding, md, new Dictionary<string, string>(), _engine.Paths.CheckRealNoWait);
                x.step.Finding.ReportDir = dir;
            }
            catch (Exception ex) { _engine.Log.Info("experiment report failed: " + ex.Message); }
        }
        Findings.Add(x.step.Finding);
        ExperimentStep?.Invoke(x.step);
    }

    /// <summary>
    /// Filter / package events, container processes and services, and (hourly or on alert) the filter instances. The services
    /// and the instances are counted as done as each is read, after the events (<see cref="ReadFilters"/>): a failure before
    /// them leaves them due at the next sample, and a query that fails waits for its next time, as before.
    /// </summary>
    private void ObserveFilters(Sample s, CheckSchedule.Due due, IReadOnlyList<LifecycleEvent> events)
    {
        List<Finding> found;
        lock (Filters)
            found = ReadFilters(_checks, s, due, Filters, events, _engine.Registry.LiveRecords,
                new FilterSources(EventLog.Drain, () => ServiceStates.Query(Core.Filters.FilterTimeline.WatchedServices), FilterInstanceQuery.Query),
                instancesTaken: () => _instancesRequested = false, _engine.Log.Info);
        foreach (var f in found) Findings.Add(f);
    }

    /// <summary>
    /// What the filter part of a sample reads (<see cref="ReadFilters"/>): the event-log records drained since the last
    /// sample, the filter manager's watched services, and its instances with a note on the query.
    /// </summary>
    internal sealed record FilterSources(Func<IEnumerable<Core.Filters.LogEvent>> EventRecords, Func<IReadOnlyDictionary<string, bool>> Services,
        Func<(List<Core.Filters.FilterInstance>? Instances, string? Note)> Instances);

    /// <summary>
    /// The filter part of sample <paramref name="s"/> (<see cref="ObserveFilters"/>), read from <paramref name="sources"/>
    /// in <see cref="CheckSchedule.ReadFilters"/>'s order: the event-log records and the lifecycle events into the
    /// timeline first, then the services and the instances <paramref name="due"/> says are due, each counted as read just
    /// before it is read. A service query that throws is logged (<paramref name="log"/>) and the instances are still read;
    /// <paramref name="instancesTaken"/> runs before the instance query, so an incident's request for them is taken also
    /// when the query throws (they then wait for their hour). Anything else that throws goes on to the caller, and what was
    /// found is not kept.
    /// </summary>
    internal static List<Finding> ReadFilters(CheckSchedule checks, Sample s, CheckSchedule.Due due, Core.Filters.FilterTimeline filters,
        IReadOnlyList<LifecycleEvent> events, Func<IEnumerable<ProcessRecord>> live, FilterSources sources, Action instancesTaken, Action<string> log)
    {
        var now = s.TimeUtc;
        var found = new List<Finding>();
        checks.ReadFilters(s, due,
            timeline: () =>
            {
                foreach (var e in sources.EventRecords()) found.AddRange(filters.OnLogEvent(e, Core.Model.Moment.Of(s)));
                found.AddRange(filters.OnLifecycle(events, live, now));
            },
            services: () =>
            {
                try { found.AddRange(filters.OnServices(sources.Services(), now)); }
                catch (Exception ex) { log("service state query failed: " + ex.Message); }
            },
            instances: () =>
            {
                instancesTaken();
                var (instances, note) = sources.Instances();
                found.AddRange(filters.OnInstances(instances, note, now));
            });
        return found;
    }

    /// <summary>
    /// Writes a spawn-storm report folder (<see cref="StormReports.WriteWithin"/>). Its first write and its last (it leaves memory,
    /// or PoolWatch stops) are always made; an update in between waits while the report folders' write budget has no room
    /// (<see cref="Core.Incidents.WriteBudget"/>, shared with the incidents), and is made at a later sample.
    /// </summary>
    private void WriteStorm(StormFolder folder, long nowMs)
    {
        try
        {
            StormReports.WriteWithin(_analysis.Incidents.Budget, Spawns, folder, _engine.Paths.IncidentsDir, _engine.Meter.OwnCostLine(), _engine.Paths.CheckRealNoWait, nowMs);
        }
        catch (Exception ex)
        {
            folder.LastWriteMs = nowMs; // back off to the normal refresh interval instead of retrying every tick
            _engine.Log.Info("spawn storm report failed: " + ex.Message);
        }
    }

    /// <summary>End of a run: the report folders with anything not written yet get it, with their storms' final numbers.</summary>
    public void Finish()
    {
        CancelExperiment();
        DrainOtherThreads(); // the sampler has stopped: record what is still queued
        _lagProber?.Dispose();
        EventLog.Dispose();
        lock (Spawns)
            foreach (var folder in Spawns.FoldersToFinish()) WriteStorm(folder, Environment.TickCount64);
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
            PoolWatch.Engine.NoWaitOpen.AppendAllText(_engine.Paths.Guard.CheckReal(Path.Combine(dir, "findings.jsonl")), line);
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
            lock (_analysis.History.SyncRoot) _analysis.Incidents.AddEvents([ev], Core.Model.Moment.Now());
        }
        catch (Exception ex) { _engine.Log.Info("finding event write failed: " + ex.Message); }
    }

    /// <summary>Parents' working directories (PEB reads), each kept for a minute on the clock that only goes forward: a parent can change directory.</summary>
    private readonly Core.Model.KeptBy<ProcessKey, string?> _cwdCache = new(TimeSpan.FromMinutes(1), 512);

    /// <summary>The parent's working directory for a git start without -C. Kept apart from the record's session context.</summary>
    private string? CwdOf(ProcessKey key) => _cwdCache.Get(key, k =>
        _engine.Registry.Get(k) is { IsAlive: true } ? PebReader.Read(k.Pid, k.CreateTime, [])?.cwd : null);

    internal static GitRootContext GitContext()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var profiles = new List<string>();
        if (profile.Length > 0) profiles.Add(profile);
        return new GitRootContext(profiles, OneDriveRoots(), profile.Length > 0 ? Path.GetDirectoryName(profile) : null);
    }

    /// <summary>
    /// OneDrive roots from the account's environment (read as data: PoolWatch's own, which its launcher builds, has no
    /// OneDrive variables) and the per-account registry entries (HKCU, read-only).
    /// </summary>
    internal static List<string> OneDriveRoots() => OneDriveRoots(Engine.UserEnvironment.Read());

    internal static List<string> OneDriveRoots(IReadOnlyDictionary<string, string> account)
    {
        var roots = new List<string>();
        foreach (var v in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            if (account.GetValueOrDefault(v) is { Length: > 0 } p) roots.Add(p);
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
    /// <param name="clock">Reads the two clocks (<see cref="Core.Model.Moment.Now"/>; a test gives its own).</param>
    /// <param name="driveType">A drive's type (GetDriveTypeW of its root; a test gives its own).</param>
    internal sealed class FileProbe(Func<Core.Model.Moment>? clock = null, Func<char, uint>? driveType = null) : IFileProbe
    {
        /// <summary>How long a drive's answer is kept before Windows is asked again.</summary>
        internal static readonly TimeSpan DriveAge = TimeSpan.FromMinutes(5);
        /// <summary>Each drive's answer, kept for <see cref="DriveAge"/> on the clock that only goes forward.</summary>
        private readonly Dictionary<char, Core.Model.Kept<bool>> _drives = new();
        private readonly Func<Core.Model.Moment> _clock = clock ?? Core.Model.Moment.Now;
        private readonly Func<char, uint> _driveType = driveType ?? (d => Native.NativeMethods.GetDriveTypeW(d + ":\\"));

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
                if (!_drives.TryGetValue(d, out var kept)) _drives[d] = kept = new(DriveAge, () => _driveType(d) == 3, _clock);
                return kept.Value;
            }
        }
    }
}
