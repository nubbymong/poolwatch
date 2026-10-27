using System.Text.Json;
using PoolWatch.Core.Cache;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Model;
using PoolWatch.Engine;
using PoolWatch.Native;

namespace PoolWatch.Analysis;

/// <summary>
/// "Clear cache" (addendum 2): a clear is PoolWatch's own in-process purge of the standby list (it runs elevated; S5,
/// review round 2: the SYSTEM helper task is gone); PoolWatch records the pool
/// totals, memory lists and top tags just before, measures again 2 minutes later, and writes one finding ("freed X GB").
/// The automatic rule (off by default) runs here too. Clears run on a worker thread; measuring happens on the sampler.
/// </summary>
internal sealed class CacheClearer
{
    public static readonly TimeSpan MeasureAfter = TimeSpan.FromMinutes(2);
    private readonly MonitorEngine _engine;
    private readonly FindingLog _findings;
    private readonly string _stateFile;
    private readonly List<ClearRecord> _history = new();
    private readonly object _lock = new();
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private int _running;
    private DateTime _nextLists = DateTime.MinValue, _nextStatus = DateTime.MinValue;

    private readonly Func<bool> _experimentActive;
    private long _lastEndMs;
    /// <summary>Review round 3 (item 7): the first status read of this start removes an older build's SYSTEM tasks.</summary>
    private readonly CacheHelper.OldTaskCleanup _status = new(CacheHelper.Query, CacheHelper.RemoveOldTasks);

    /// <param name="experimentActive">C1: an A/B experiment is running; no automatic clear then.</param>
    public CacheClearer(MonitorEngine engine, FindingLog findings, Func<bool>? experimentActive = null)
    {
        _engine = engine;
        _findings = findings;
        _experimentActive = experimentActive ?? (() => false);
        _stateFile = engine.Paths.Guard.CheckReal(Path.Combine(engine.Paths.EnsureDir(engine.Paths.CacheDir), "clears.json"));
        Load();
    }

    public MemoryLists Lists { get; private set; }
    public CacheHelper.Status? HelperStatus { get; private set; }
    public bool Busy => Volatile.Read(ref _running) != 0;
    /// <summary>When the last clear ended (Environment.TickCount64, monotonic); null before the first. The lag probe pauses
    /// for 30 s after it (C1).</summary>
    public TimeSpan? LastEnd => Volatile.Read(ref _lastEndMs) is var ms && ms > 0 ? TimeSpan.FromMilliseconds(ms) : null;
    /// <summary>When the clear running now started (monotonic); null when none runs (C1 b: caps the lag probe's pause).</summary>
    public TimeSpan? BusySince => Busy && Volatile.Read(ref _startedMs) is var ms && ms > 0 ? TimeSpan.FromMilliseconds(ms) : null;
    private long _startedMs;

    public List<ClearRecord> History() { lock (_lock) return _history.ToList(); }

    /// <summary>Re-reads the helper's status now (after the setup ran), off the calling thread.</summary>
    public void RefreshStatus() => Task.Run(() => HelperStatus = _status.Query());

    /// <summary>Starts a clear (any thread). False when one is running or no sample is in yet.</summary>
    public bool Request(ClearMode mode, ClearTrigger trigger)
    {
        if (_engine.Latest is not { } s) return false;
        if (Interlocked.Exchange(ref _running, 1) != 0) return false;
        Volatile.Write(ref _startedMs, Math.Max(1, Environment.TickCount64));
        var rec = new ClearRecord
        {
            TimeUtc = DateTime.UtcNow, Mode = mode, Trigger = trigger, Before = s.Totals, ListsBefore = SafeLists(),
            TagsBefore = s.Tags.OrderByDescending(t => t.TotalUsed).Take(30).Select(t => (t.Tag, (long)t.TotalUsed)).ToList(),
        };
        lock (_lock) { _history.Add(rec); if (_history.Count > 200) _history.RemoveAt(0); }
        _engine.Log.Info($"clear cache: {rec.ModeText} ({rec.TriggerText}) requested, pool {Core.Detection.Fmt.Bytes(rec.PoolBefore)}");
        Task.Run(() =>
        {
            try
            {
                rec.Error = CacheHelper.Run(mode);
                if (rec.Error is null) rec.ListsImmediate = SafeLists();
            }
            catch (Exception ex) { rec.Error = ex.Message; }
            finally
            {
                Volatile.Write(ref _lastEndMs, Math.Max(1, Environment.TickCount64)); // before "not busy" (the lag probe's pause)
                Volatile.Write(ref _running, 0);
            }
            if (rec.Error is not null) _engine.Log.Info("clear cache failed: " + rec.Error);
        });
        return true;
    }

    /// <summary>Sampler thread, once per sample: the 2-minute measurement, the automatic rule, the memory lists, the helper's status.</summary>
    public void Observe(Sample s)
    {
        var now = s.TimeUtc;
        if (now >= _nextLists) { _nextLists = now.AddSeconds(60); Lists = SafeLists(); }
        if (now >= _nextStatus)
        {
            _nextStatus = now.AddMinutes(60); // and right after a setup step (RefreshStatus)
            Task.Run(() => HelperStatus = _status.Query()); // COM: off the sampler thread
        }
        while (_status.Notes.TryDequeue(out var note))
        {
            _findings.Add(new Finding { TimeUtc = now, Kind = FindingKind.Setup, Severity = FindingSeverity.Info, Title = "Clear cache: " + note });
            _engine.Log.Info("clear cache: " + note);
        }
        List<ClearRecord> due;
        lock (_lock) due = _history.Where(r => r.After is null && (r.Error is not null ? !_reported.Contains(r) : now - r.TimeUtc >= MeasureAfter)).ToList();
        foreach (var r in due)
        {
            if (r.Error is null)
            {
                r.After = s.Totals;
                r.ListsAfter = SafeLists();
                var before = r.TagsBefore.Select(t => t.Tag).ToHashSet();
                r.TagsAfter = s.Tags.Where(t => before.Contains(t.Tag)).Select(t => (t.Tag, (long)t.TotalUsed)).ToList();
            }
            _reported.Add(r);
            _findings.Add(r.ToFinding());
            _engine.Log.Info(r.Summary());
            Save();
        }
        // Automatic clear (off by default; never while one runs or is being measured, never while a clear
        // task of your own is still there: the two must not both run; C1: never during an A/B experiment, whose phases a
        // clear would skew; the "lag before / after a clear" preset makes its own clear).
        DateTime? last;
        ClearRecord? lastPool;
        lock (_lock)
        {
            last = _history.LastOrDefault()?.TimeUtc;
            lastPool = _history.LastOrDefault(r => r.Trigger == ClearTrigger.PoolOverLimit && r.Done);
        }
        if (!Busy && !due.Any() && CacheHelper.AutoClearAllowed(HelperStatus)
            && ClearPolicy.Due(_engine.Settings, s.Totals, now, last, _startedUtc, lastPool, experimentRunning: _experimentActive()) is { } trigger)
        {
            ClearMode mode;
            bool experiment;
            lock (_lock) { mode = ClearPolicy.ModeFor(_history, _engine.Settings); experiment = ClearPolicy.IsExperiment(_history, _engine.Settings); }
            Request(mode, experiment ? ClearTrigger.Experiment : trigger);
        }
    }

    private readonly HashSet<ClearRecord> _reported = new(ReferenceEqualityComparer.Instance);

    /// <summary>"Last clear freed 4.7 GB (11:20); clearable now about 1.2 GB." and the low-priority verdict, for the Overview.</summary>
    public string StatusLine(PoolTotals now)
    {
        List<ClearRecord> h;
        lock (_lock) h = _history.ToList();
        var parts = new List<string>();
        if (h.LastOrDefault(r => r.After is not null && r.Error is null) is { } last)
            parts.Add($"Last clear freed {Core.Detection.Fmt.Bytes(Math.Max(0, last.Freed))} of pool ({Core.Detection.Fmt.Clock(last.TimeUtc)}, {last.ModeText})");
        if (ClearPolicy.ClearableNow(h, now) is { } c) parts.Add($"clearable now about {Core.Detection.Fmt.Bytes(c)}");
        if (h.LastOrDefault(r => r.Done == false) is { } run) parts.Add($"a clear is being measured ({Core.Detection.Fmt.Clock(run.TimeUtc)})");
        if (ClearPolicy.LowPriorityVerdict(h) is { } v) parts.Add(v);
        if (HelperStatus is { CanPurge: false }) parts.Add("Clear cache needs PoolWatch to run as administrator");
        if (HelperStatus?.OldTasksPresent == true)
            parts.Add($"an older PoolWatch's Clear cache tasks are still installed and no longer used ({string.Join(", ", HelperStatus.OldTasks)}; toolbar: Remove old clear-cache tasks)");
        if (HelperStatus?.LegacyTaskPresent == true)
            parts.Add(_engine.Settings.AutoClearEnabled
                ? $"the automatic clear is paused while your own clear task exists ({HelperStatus.LegacyTaskText}; toolbar: Remove my clear task)"
                : $"your own clear task clears the cache ({HelperStatus.LegacyTaskText}); turning on the automatic clear offers to remove it");
        return parts.Count == 0 ? "" : string.Join("; ", parts) + ".";
    }

    private static MemoryLists SafeLists()
    {
        try { return MemoryNative.Query(); } catch { return default; }
    }

    // ---- the clears survive restarts (they feed the estimate and the low-priority verdict)
    private sealed record Saved(DateTime TimeUtc, string Mode, string Trigger, long PoolBefore, long PoolAfter, long PagedBefore, long PagedAfter, long StandbyBefore, long StandbyAfter, string? Error);

    private void Save()
    {
        try
        {
            List<Saved> rows;
            lock (_lock) rows = _history.Where(r => r.Done).TakeLast(100).Select(r => new Saved(r.TimeUtc, r.Mode.ToString(), r.Trigger.ToString(), r.PoolBefore, r.PoolAfter,
                (long)r.Before.CommittedPaged, (long)(r.After?.CommittedPaged ?? r.Before.CommittedPaged), r.ListsBefore.StandbyBytes, r.ListsAfter.StandbyBytes, r.Error)).ToList();
            File.WriteAllText(_engine.Paths.Guard.CheckReal(_stateFile + ".tmp"), JsonSerializer.Serialize(rows));
            File.Move(_engine.Paths.Guard.CheckReal(_stateFile + ".tmp"), _engine.Paths.Guard.CheckReal(_stateFile), overwrite: true);
        }
        catch (Exception ex) { _engine.Log.Info("clears.json not saved: " + ex.Message); }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_stateFile)) return;
            foreach (var r in JsonSerializer.Deserialize<List<Saved>>(File.ReadAllText(_stateFile)) ?? [])
            {
                ulong paged = (ulong)Math.Max(0, r.PagedBefore), pagedAfter = (ulong)Math.Max(0, r.PagedAfter);
                var rec = new ClearRecord
                {
                    TimeUtc = r.TimeUtc, Mode = Enum.TryParse<ClearMode>(r.Mode, out var m) ? m : ClearMode.Standby, Trigger = Enum.TryParse<ClearTrigger>(r.Trigger, out var t) ? t : ClearTrigger.Manual,
                    Before = new PoolTotals(paged, (ulong)Math.Max(0, r.PoolBefore - r.PagedBefore), 0, 0),
                    ListsBefore = new MemoryLists(0, 0, r.StandbyBefore, 0, 0),
                };
                if (r.Error is null) { rec.After = new PoolTotals(pagedAfter, (ulong)Math.Max(0, r.PoolAfter - r.PagedAfter), 0, 0); rec.ListsAfter = new MemoryLists(0, 0, r.StandbyAfter, 0, 0); }
                else rec.Error = r.Error;
                _reported.Add(rec);
                _history.Add(rec);
            }
        }
        catch (Exception ex) { _engine.Log.Info("clears.json not read: " + ex.Message); }
    }
}
