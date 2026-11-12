using System.Text.Json;
using PoolWatch.Capture;
using PoolWatch.Core;
using PoolWatch.Core.Aicc;
using PoolWatch.Core.Detection;
using PoolWatch.Core.History;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;
using PoolWatch.Core.Util;
using PoolWatch.Engine;

namespace PoolWatch.Analysis;

/// <summary>
/// Detection, correlation and incidents on top of the collectors. Everything runs on the sampler thread, inside
/// the history lock; readers (UI, since-boot report) take <see cref="History.SyncRoot"/>.
/// </summary>
internal sealed class AnalysisPipeline
{
    private readonly MonitorEngine _engine;
    private readonly CancellationTokenSource _stopCapture = new();
    /// <summary>The worker labels are set again every 60 s, on the clock that only goes forward.</summary>
    private readonly WorkerReads _workerReads = new();

    public AnalysisPipeline(MonitorEngine engine)
    {
        _engine = engine;
        var paths = engine.Paths;
        Knowledge = new TagKnowledge(engine.Settings.PoolTagTxtPath, paths.Guard.CheckReal(Path.Combine(paths.EnsureDir(paths.CacheDir), "tagscan.json")), paths.Guard.CheckReal);
        Host = new WindowsIncidentHost(engine, Knowledge);
        History = History.ForInterval(engine.Settings.SampleIntervalSeconds, engine.Settings.RawHistoryMinutes);
        // The since-boot report's names of an hour's busiest processes, taken as the minutes' processes are summed by the hour.
        History.NameOf = key => engine.Registry.Get(key)?.DisplayName;
        Detector = new Detector(engine.Settings);
        Growth = new Core.Growth.GrowthLedger(CacheClearer.MeasureAfter, TimeSpan.FromSeconds(engine.Settings.SampleIntervalSeconds));
        Incidents = new IncidentManager(engine.Settings, Knowledge, Host, engine.Registry);
        // Kernel functions in CPU traces are named from local, admin-only symbol folders only (SymbolPath, else
        // <data root>\symbols); never a symbol server, a share, a network drive or _NT_SYMBOL_PATH.
        Captures = new CaptureRunner(paths, engine.Log,
            () => Core.Util.SymbolPaths.Resolve(engine.Settings.SymbolPath, Path.Combine(paths.DataRoot, "symbols"), Engine.Interop.UsableSymbolFolder));
        // The governor runs on a monotonic clock, never the wall clock.
        Governor = new CaptureGovernor(engine.Settings, () => TimeSpan.FromMilliseconds(Environment.TickCount64));
        Incidents.CaptureRunning = () => Captures.Busy; // Start records stay while a trace runs
        Captures.CleanupStale(paths.IncidentsDir);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Captures.CancelIfRunning();
        Incidents.Opened += OnIncidentOpened;
        Host.SessionResolved += key => { lock (History.SyncRoot) Incidents.Touch(key); };
        engine.Notable += events => { lock (History.SyncRoot) using (Profiler.Stage("analysis.events")) Incidents.AddEvents(events, Core.Model.Moment.Now()); };
        engine.HandlesScanned += OnHandles;
        engine.Sampled += OnSample;
        Diagnostics = new Diagnostics(engine, this);
        Boots = new Core.Boot.BootTracker(paths.BootsDir, paths.Guard.CheckReal);
        engine.Log.Info($"tag knowledge: pooltag.txt {(Knowledge.PoolTagTxtLoaded ? "loaded" : "not found")} ({engine.Settings.PoolTagTxtPath})");
    }

    public TagKnowledge Knowledge { get; }
    public Diagnostics Diagnostics { get; }
    public Core.Boot.BootTracker Boots { get; }
    public WindowsIncidentHost Host { get; }
    public History History { get; }
    public Detector Detector { get; }
    /// <summary>What the pool and its tags grew, cleared and fell, by the minute for 25 hours (the growth and clear stats). History lock.</summary>
    public Core.Growth.GrowthLedger Growth { get; }
    public IncidentManager Incidents { get; }
    public long AlertsRaised { get; private set; }
    public CaptureRunner Captures { get; }
    public int CapturesStarted { get; private set; }

    private void OnHandles(HandleSnapshot snap)
    {
        var byProcess = new Dictionary<ProcessKey, Dictionary<string, int>>();
        foreach (var pid in snap.Counts.Pids)
            if (snap.KeyOf(pid) is { } key) byProcess[key] = snap.ByTypeName(pid);
        lock (History.SyncRoot) using (Profiler.Stage("analysis.handles")) History.AddHandles(new TypedHandles(snap.TimeUtc, byProcess) { Ms = snap.Ms });
    }

    private void OnSample(Sample s)
    {
        // PoolWatch's own cache clears are its own impact, not the pool's: the detector measures across them. Read before
        // the history lock, as the other readers of both do. The clearer measures a clear at this same sample, after the
        // detector, from this sample's totals. Diagnostics is null only while the constructor runs: this handler is
        // subscribed before Diagnostics is made.
        var clears = Diagnostics is { } d ? d.Cache.History() : null;
        lock (History.SyncRoot)
        {
            using (Profiler.Stage("analysis.history")) History.Add(s);
            using (Profiler.Stage("analysis.growth"))
                Growth.Observe(Core.Model.Moment.Of(s), (long)(s.Totals.CommittedPaged + s.Totals.CommittedNonPaged), s.Tags, clears ?? (IReadOnlyList<Core.Cache.ClearRecord>)[]);
            // Every incident since PoolWatch started (Closed keeps only the last 6 hours). In memory only: the files are
            // written by Boots.Flush below, outside the history lock.
            try { using (Profiler.Stage("analysis.boots")) Boots.Observe(s, TimeSpan.FromMilliseconds(Environment.TickCount64), Incidents.CreatedCount); }
            catch (Exception ex) { _engine.Log.Info("boot summary failed: " + ex.Message); }
            if (_workerReads.At(s) is { } reads)
            {
                using var _ = Profiler.Stage("analysis.labels");
                ProcessLabels.Apply(_engine.Registry, reads); // reads in the last interval, not since start
            }
            List<Alert> alerts;
            using (Profiler.Stage("analysis.detector")) alerts = Detect(Detector, History, clears);
            AlertsRaised += alerts.Count;
            using (Profiler.Stage("analysis.incidents")) Incidents.Process(History, Detector, alerts, Core.Model.Moment.Of(s));
            try { RequestPoolCaptures(); }
            catch (Exception ex) { _engine.Log.Info("alert capture failed: " + ex.Message); }
            try { StartPendingCapture(); }
            catch (Exception ex) { _engine.Log.Info("held-back capture failed: " + ex.Message); }
        }
        // The boot files are written here, outside the history lock (a baseline is hundreds of KB of JSON); a failed
        // baseline write is retried after 5 minutes, not rebuilt on every sample. Logged once per distinct failure.
        string? bootError;
        using (Profiler.Stage("analysis.bootfiles")) bootError = Boots.Flush(s.TickMs);
        if (bootError != _lastBootError)
        {
            _lastBootError = bootError;
            if (bootError is not null) _engine.Log.Info(bootError);
        }
    }

    private string? _lastBootError;

    /// <summary>
    /// The detector on the newest sample, with PoolWatch's own cache clears as the clearer holds them (null: none yet),
    /// each measured <see cref="CacheClearer.MeasureAfter"/> after it ran, as the clearer measures it. The caller holds the
    /// history lock.
    /// </summary>
    internal static List<Alert> Detect(Detector detector, History history, IReadOnlyList<Core.Cache.ClearRecord>? clears) =>
        detector.Evaluate(history, clears is null ? OwnClears.None : new OwnClears(clears, CacheClearer.MeasureAfter));

    private void OnIncidentOpened(Incident inc, Alert a)
    {
        // An automatic capture, through the governor (CaptureOnAlert; at most one per hour; not while saturated or typing).
        // Collected for the tick, so the incidents that open together share one trace.
        if (inc.Kind is IncidentKind.Growth or IncidentKind.NonPagedTotal) _openedThisTick.Add((inc, a));
        _engine.RecordEvents([new LifecycleEvent
        {
            TimeUtc = a.TimeUtc, Kind = a.Kind == AlertKind.Release ? LifecycleKind.Release : LifecycleKind.Alert,
            Image = inc.Tag.ToString(), Detail = $"{a.Rule} ({Fmt.SignedBytes(a.Kind == AlertKind.Release ? -a.Bytes : a.Bytes)}); incident {inc.Id}",
        }]);
    }

    /// <summary>
    /// The one gate for every trace: at most one automatic capture per <see cref="Settings.AutoCaptureMinMinutes"/>,
    /// never back to back, never while the PC is saturated or in use, pool events only unless DeeperCapture. History lock.
    /// </summary>
    public CaptureGovernor Governor { get; }
    /// <summary>Capture requests skipped so far (summary.json).</summary>
    public int CapturesSkipped { get { lock (History.SyncRoot) return Governor.SkippedCount; } }

    /// <summary>The governor's inputs now: readings of the last sampler tick plus the session's last input.</summary>
    private CaptureConditions Conditions() =>
        new(Captures.Busy, Environment.ProcessorCount, Diagnostics.RunQueue, Diagnostics.Load?.CpuFraction, Native.InputNative.SinceLastInput());

    /// <summary>
    /// "Capture now": a capture incident for the fastest-growing tags (up to 3 by 10-minute growth, else File), recording
    /// pool events + Kernel-File (+ CPU with <see cref="Settings.CpuTraceEnabled"/>). The owner asked for it: no hourly cap,
    /// no load or input check, but never while another capture runs. Returns null when a capture is already running.
    /// </summary>
    public Incident? CaptureNow()
    {
        if (Captures.Busy) return null;
        lock (History.SyncRoot)
        {
            var d = Governor.Decide(new CaptureRequest(CaptureSource.Manual, DateTime.UtcNow, Governor.Now, "Capture now"), new CaptureConditions(Captures.Busy, Environment.ProcessorCount));
            if (!d.Start) { Record(d); return null; }
            var tags = Detector.Status.Values.Where(s => s.Delta10m > 0 && s.Tag != EtwBuffers).OrderByDescending(s => s.Delta10m).Take(3).Select(s => s.Tag).ToList();
            if (tags.Count == 0) tags.Add(PoolTag.Parse("File"));
            var now = History.Count > 0 ? History.Times[^1] : DateTime.UtcNow;
            var inc = Incidents.CreateCapture(History, tags, now);
            Incidents.Note(inc, now, $"capture started: {d.Summary} for {string.Join(", ", tags.Select(t => t.ToDisplay()))}");
            if (StartCapture(d, [inc], tags)) return inc;
            Incidents.Discard(inc); // an alert-triggered capture started meanwhile: no empty incident folder
            return null;
        }
    }

    /// <summary>
    /// The lag probe tripped (sampler thread): a CPU-only trace (<see cref="Settings.CpuTraceSeconds"/>) into its own capture
    /// incident, through the governor. Returns the incident when one started, and the line for the Lag finding (where the
    /// trace went, or "capture skipped: ..."; null when CPU traces are off).
    /// </summary>
    public (Incident? inc, string? note) CaptureForLag(string why, DateTime timeUtc)
    {
        lock (History.SyncRoot)
        {
            var req = new CaptureRequest(CaptureSource.LagTrip, timeUtc, Governor.Now, why);
            var d = Governor.Decide(req, Conditions());
            if (d.Start)
            {
                if (StartLagCapture(d) is { } inc) return (inc, $"CPU trace ({d.Scope.Describe()}) in capture incident {inc.Id}");
                d = Governor.Decide(req, Conditions() with { CaptureRunning = true }); // lost the race to another trace: held back
            }
            Record(d);
            return (null, d.Note);
        }
    }

    private static readonly PoolTag EtwBuffers = PoolTag.Parse("EtwB");

    /// <summary>At most this many tags share one confirmation trace (its pool-tag filter).</summary>
    public const int MaxTagsPerTrace = 3;

    /// <summary>Growth incidents opened in this sampler tick (history lock), waiting for <see cref="RequestPoolCaptures"/>.</summary>
    private readonly List<(Incident inc, Alert alert)> _openedThisTick = new();

    private static IReadOnlyList<Incident> IncidentsOf(object? context) => CaptureSteps.IncidentsOf(context);

    private static List<PoolTag> TagsOf(IEnumerable<Incident> incs) => CaptureSteps.TagsOf(incs, MaxTagsPerTrace);

    /// <summary>Each capture note at most once per incident and reason (no "capture skipped" every tick).</summary>
    private readonly CaptureNotes _captureNotes = new();

    private void NoteOnce(Incident inc, DateTime timeUtc, string text)
    {
        if (_captureNotes.First(inc, text)) Incidents.Note(inc, timeUtc, text);
    }

    private void NoteStarted(Incident inc, string text)
    {
        _captureNotes.Reset(inc); // what held it back may be said again for a later episode
        Incidents.Note(inc, DateTime.UtcNow, text);
    }

    /// <summary>
    /// The incidents that opened in this tick share ONE trace, up to <see cref="MaxTagsPerTrace"/> tags
    /// (<see cref="CaptureSteps.Plan"/>; a held-back pool-alert trace whose incidents are still open joins it). The governor
    /// still decides; the incidents that did not fit are told only after that decision. History lock.
    /// </summary>
    private void RequestPoolCaptures()
    {
        var batch = CaptureSteps.Plan(_openedThisTick, Governor.Pending, DateTime.UtcNow, Governor.Now, MaxTagsPerTrace);
        _openedThisTick.Clear();
        if (batch is null) return;
        var d = RequestPoolCapture(batch.Chosen, batch.Request);
        if (CaptureSteps.LeftNote(d, MaxTagsPerTrace) is { } note)
            foreach (var inc in batch.Left) NoteOnce(inc, DateTime.UtcNow, note);
    }

    /// <summary>A pool alert's capture (one or more incidents): started now, or held back with "capture skipped: ..." on their timelines. History lock.</summary>
    private CaptureDecision RequestPoolCapture(IReadOnlyList<Incident> incs, CaptureRequest req)
    {
        var d = Governor.Decide(req, Conditions());
        if (d.Start)
        {
            var tags = TagsOf(incs);
            if (StartCapture(d, incs, tags))
            {
                string shared = incs.Count > 1 ? $" (one trace for {string.Join(", ", tags.Select(t => t.ToDisplay()))})" : "";
                foreach (var inc in incs) NoteStarted(inc, $"capture started: {d.Summary}{shared}");
                return d;
            }
            d = Governor.Decide(req, Conditions() with { CaptureRunning = true }); // lost the race to another trace: held back
        }
        Record(d);
        return d;
    }

    /// <summary>A lag trip's CPU trace in its own capture incident; null (no folder left) when the runner refused.</summary>
    private Incident? StartLagCapture(CaptureDecision d)
    {
        var now = History.Count > 0 ? History.Times[^1] : DateTime.UtcNow;
        var inc = Incidents.CreateCapture(History, [], now, CaptureSource.LagTrip);
        Incidents.Note(inc, d.Request.TimeUtc, d.Request.Why);
        Incidents.Note(inc, now, $"capture started: {d.Summary}");
        if (StartCapture(d, [inc], [])) return inc;
        Incidents.Discard(inc);
        return null;
    }

    /// <summary>A decision that started nothing: logged, and "capture skipped: ..." on the incidents it was for.</summary>
    private void Record(CaptureDecision d)
    {
        if (d.Note is not { } note) return;
        _engine.Log.Info($"{d.Request.Why}: {note}");
        var now = DateTime.UtcNow;
        var mine = IncidentsOf(d.Request.Context);
        foreach (var inc in mine) NoteOnce(inc, d.Request.TimeUtc, note);
        if (d.Replaced is { } replaced)
            foreach (var old in IncidentsOf(replaced.Context).Where(o => !mine.Contains(o)))
                NoteOnce(old, now, $"capture skipped: a newer request replaced this one ({d.Request.Why})");
    }

    /// <summary>Starts one trace for one or more incidents (their tags together); the result is attached to each. History lock.</summary>
    private bool StartCapture(CaptureDecision d, IReadOnlyList<Incident> incs, IReadOnlyCollection<PoolTag> tags)
    {
        var scope = d.Scope;
        int cpuSeconds = scope.Cpu ? _engine.Settings.CpuTraceSeconds : 0;
        bool started = Captures.TryStart(incs[0].Dir, tags, scope, _engine.Settings.CaptureSeconds, result =>
        {
            // The runner calls this whatever happened (also when the capture threw).
            result.Notes.Insert(0, "Recorded: " + d.Summary + "."); // what this trace holds, and why
            lock (History.SyncRoot)
            {
                // The governor first (it knows the trace ended, whatever happens next), then each incident (CaptureSteps.Complete).
                var now = DateTime.UtcNow;
                CaptureSteps.Complete(Governor, incs, result, (inc, text) => Incidents.Note(inc, now, text),
                    inc => Incidents.ApplyCapture(History, inc, result), _engine.Log.Info);
                Incidents.Suppress(EtwBuffers, Environment.TickCount64 + 120_000); // the buffers drain after the stop
            }
            // No queue. A held-back request starts from the sampler tick (StartPendingCapture) once the governor allows.
        }, _stopCapture.Token, cpuSeconds);
        if (!started) return false;
        // The callback above needs the history lock, which the caller holds: Finished cannot run before Started.
        if (Governor.Started(d.Request) is { } superseded)
        {
            _engine.Log.Info($"{superseded.Why}: held-back capture dropped (a newer capture started)");
            foreach (var old in IncidentsOf(superseded.Context).Where(o => !incs.Contains(o)))
                NoteOnce(old, DateTime.UtcNow, "held-back capture dropped: a newer capture started");
        }
        CapturesStarted++;
        _engine.Log.Info($"capture started: {d.Summary}{(tags.Count > 0 ? " for " + string.Join(", ", tags.Select(t => t.ToDisplay())) : "")}");
        Incidents.Suppress(EtwBuffers, Environment.TickCount64 + (long)(((scope.PoolEvents ? _engine.Settings.CaptureSeconds : 0) + cpuSeconds + 120) * 1000));
        _engine.RequestHandleScan();
        return true;
    }

    /// <summary>
    /// Each sampler tick (history lock): the held-back request (the newest one), when the governor allows it now. Dropped when
    /// its incident has closed or a lag trip is too old.
    /// </summary>
    private void StartPendingCapture()
    {
        if (Governor.Pending is null || _stopCapture.IsCancellationRequested) return;
        PendingCaptures.Tick(Governor, Conditions(),
            stillWanted: r => IncidentsOf(r.Context) is not { Count: > 0 } incs || incs.Any(i => i.IsOpen),
            start: d =>
            {
                var now = DateTime.UtcNow;
                if (IncidentsOf(d.Request.Context).Where(i => i.IsOpen).ToList() is { Count: > 0 } open)
                {
                    if (StartCapture(d, open, TagsOf(open)))
                        foreach (var inc in open) NoteStarted(inc, $"capture started: {d.Summary}");
                }
                else if (d.Request.Source == CaptureSource.LagTrip) StartLagCapture(d);
            },
            dropped: d =>
            {
                _engine.Log.Info($"{d.Request.Why}: held-back capture dropped ({d.Reason})");
                foreach (var gone in IncidentsOf(d.Request.Context)) NoteOnce(gone, DateTime.UtcNow, $"held-back capture dropped: {d.Reason}");
            });
    }

    /// <summary>How long <see cref="Finish"/> waits for a capture to save and parse, at most.</summary>
    internal static readonly TimeSpan FinishCaptureWait = TimeSpan.FromMinutes(6);

    /// <summary>
    /// Waits while a capture is <paramref name="busy"/>, for at most <see cref="FinishCaptureWait"/> on the clock that only
    /// goes forward (<paramref name="clock"/>; a test gives its own, and its own <paramref name="sleep"/>).
    /// </summary>
    internal static bool WaitForCaptures(Func<bool> busy, Func<Core.Model.Moment>? clock = null, Action<TimeSpan>? sleep = null) =>
        Core.Model.Waits.While(busy, FinishCaptureWait, TimeSpan.FromMilliseconds(200), clock, sleep);

    /// <summary>Closes open incidents and writes the since-boot report. Call after the engine has stopped.</summary>
    public void Finish(string outRoot)
    {
        // End a running capture early and let it save + parse (outside the history lock: it takes the lock to attach).
        _stopCapture.Cancel();
        Host.WaitForSearches(TimeSpan.FromSeconds(10));
        Diagnostics.Finish();
        WaitForCaptures(() => Captures.Busy);
        lock (History.SyncRoot)
        {
            var now = History.Count > 0 ? History.Times[^1] : DateTime.UtcNow;
            Incidents.Finish(History, now);
            WriteSinceBoot(outRoot);
            try
            {
                Boots.Save(ended: true);
                WriteAtomic(Path.Combine(outRoot, "boots.md"), Core.Boot.BootTracker.Table(Boots.All()));
            }
            catch (Exception ex) { _engine.Log.Info("boot summary not saved: " + ex.Message); }
        }
    }

    /// <summary>since-boot.md / since-boot.json in <paramref name="dir"/> (the "Since boot" report).</summary>
    public string WriteSinceBoot(string dir)
    {
        lock (History.SyncRoot)
        {
            var tags = SinceBootReport.Build(History, _engine.Registry, Knowledge);
            var boot = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
            var first = History.Minutes.Count > 0 ? History.Minutes[0].TimeUtc : History.FirstTime;
            var now = History.Count > 0 ? History.Times[^1] : DateTime.UtcNow;
            string md = Path.Combine(dir, "since-boot.md");
            WriteAtomic(md, SinceBootReport.ToMarkdown(tags, boot, first, now) + "\n" + _engine.Meter.OwnCostLine() + "\n");
            var json = tags.Select(t => new
            {
                tag = t.Tag.ToString(), tagHex = t.Tag.Raw.ToString("x8"), t.Owner, t.Description, t.BytesNow, t.BytesAtFirstMinute, t.GrowthObserved,
                hours = t.Hours.Select(h => new { h.HourUtc, h.Growth, attribution = h.Attribution.Select(a => new { a.Process, a.Pid, a.Share, a.Bytes }) }),
            });
            WriteAtomic(Path.Combine(dir, "since-boot.json"),
                JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            return md;
        }
    }

    private void WriteAtomic(string path, string content)
    {
        DurableFile.Replace(_engine.Paths.Guard, path, content);
        Core.Util.Profiler.Wrote(path, content.Length);
    }
}

/// <summary>
/// What the worker labels are set from: each process's reads since the last time, every 60 s on the clock that only goes
/// forward (each sample's own moment), so setting the system clock back never holds the labels back, nor does setting it
/// forward bring them early. Sampler thread only.
/// </summary>
internal sealed class WorkerReads
{
    private readonly Core.Model.Every _every = new(TimeSpan.FromSeconds(60));
    private Dictionary<ProcessKey, long> _last = new();

    /// <summary>Each process's reads since the last time, when the labels are due at <paramref name="s"/>; null when they are not.</summary>
    public Dictionary<ProcessKey, long>? At(Sample s)
    {
        if (!_every.DueAt(s)) return null;
        // Reads in the last interval, not since start: a restarted worker must not lose.
        var reads = s.Processes.ToDictionary(p => p.Key, p => p.ReadOperationCount);
        var since = reads.ToDictionary(kv => kv.Key, kv => kv.Value - _last.GetValueOrDefault(kv.Key));
        _last = reads;
        return since;
    }
}
