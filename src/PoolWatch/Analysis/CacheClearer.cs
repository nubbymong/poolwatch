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
    /// <summary>The memory lists read once a minute, kept for the graph's RAM overlay (25 hours, in memory only). Under <see cref="_lock"/>.</summary>
    private readonly Core.Growth.RamHistory _ram = new();
    /// <summary>The PC's RAM, as the incident reports say it.</summary>
    private readonly long _totalRam = SafeTotalRam();
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
        if (now >= _nextLists)
        {
            _nextLists = now.AddSeconds(60);
            Lists = SafeLists();
            var lists = Lists;
            if (lists.Known) lock (_lock) _ram.Add(now, _totalRam, lists.FreeBytes + lists.ZeroBytes, lists.StandbyBytes);
        }
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
                var lists = SafeLists();
                var before = r.TagsBefore.Select(t => t.Tag).ToHashSet();
                var tags = s.Tags.Where(t => before.Contains(t.Tag)).Select(t => (t.Tag, (long)t.TotalUsed)).ToList();
                // Under the lock, so Evidence and Next, which read under it, never see the clear measured without its tags.
                lock (_lock) { r.ListsAfter = lists; r.TagsAfter = tags; r.After = s.Totals; }
            }
            _reported.Add(r);
            _findings.Add(r.ToFinding());
            _engine.Log.Info(r.Summary());
            Save();
        }
        // Automatic clear (off by default; never while one runs or is being measured, never while a clear
        // task of your own is still there: the two must not both run; C1: never during an A/B experiment, whose phases a
        // clear would skew; the "lag before / after a clear" preset makes its own clear).
        if (Busy || due.Count > 0 || !CacheHelper.AutoClearAllowed(HelperStatus)) return;
        bool experimentRunning = _experimentActive();
        (ClearMode Mode, ClearTrigger Trigger)? start;
        lock (_lock) start = ClearPolicy.Automatic(_history, _engine.Settings, s.Totals, now, _startedUtc, experimentRunning);
        if (start is { } clear) Request(clear.Mode, clear.Trigger);
    }

    /// <summary>
    /// When the automatic clear runs next (any thread): <see cref="ClearPolicy.Next"/> with the inputs <see cref="Observe"/>
    /// decides with, so the "Next cache clear" line can never disagree with it.
    /// </summary>
    public NextClear Next(DateTime nowUtc)
    {
        var now = _engine.Latest?.Totals ?? default;
        var status = HelperStatus;
        bool experimentRunning = _experimentActive();
        lock (_lock)
            return ClearPolicy.Next(_engine.Settings, now, nowUtc, _history.LastOrDefault()?.TimeUtc, _startedUtc,
                ClearPolicy.LastPoolClear(_history), status?.CanPurge, status?.LegacyTaskPresent,
                experimentRunning, Busy || _history.Any(r => !r.Done), ClearPolicy.EscalateNow(_history, _engine.Settings, nowUtc));
    }

    /// <summary>Whether clearing frees kernel pool on this PC, from every measured clear so far (they survive restarts).</summary>
    public ClearEvidence Evidence() { lock (_lock) return ClearEvidence.From(_history); }

    private readonly HashSet<ClearRecord> _reported = new(ReferenceEqualityComparer.Instance);

    /// <summary>"Last clear freed 4.7 GB (11:20); clearable now about 1.2 GB." and the low-priority verdict, for the Clear cache button's tooltip.</summary>
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
            parts.Add($"an older PoolWatch's Clear cache tasks are still installed and no longer used ({string.Join(", ", HelperStatus.OldTasks)}; tray menu: Remove old clear-cache tasks)");
        if (HelperStatus?.LegacyTaskPresent == true)
            parts.Add(_engine.Settings.AutoClearEnabled
                ? $"the automatic clear is paused while your own clear task exists ({HelperStatus.LegacyTaskText}; tray menu: Remove my clear task)"
                : $"your own clear task clears the cache ({HelperStatus.LegacyTaskText}); turning on the automatic clear offers to remove it");
        return parts.Count == 0 ? "" : string.Join("; ", parts) + ".";
    }

    private static MemoryLists SafeLists()
    {
        try { return MemoryNative.Query(); } catch { return default; }
    }

    private static long SafeTotalRam()
    {
        try { return MemoryNative.TotalPhysical(); } catch { return 0; }
    }

    /// <summary>
    /// The graph's RAM columns for the <paramref name="span"/> up to <paramref name="nowUtc"/>, each <paramref name="width"/>
    /// wide (<see cref="Core.Growth.RamHistory.Columns"/>), and the PC's RAM they are a share of; any thread.
    /// </summary>
    public (List<Core.Growth.RamColumn> Columns, long TotalBytes) Ram(DateTime nowUtc, TimeSpan span, TimeSpan width)
    {
        lock (_lock) return (_ram.Columns(nowUtc, span, width), _ram.TotalBytes);
    }

    /// <summary>The RAM readings kept (the cost report).</summary>
    public int RamReadings { get { lock (_lock) return _ram.Count; } }

    // ---- the clears survive restarts (they feed the estimate, the low-priority verdict and whether clearing helps here)
    /// <summary>A clear as clears.json keeps it. <paramref name="Tags"/>: what it freed by tag; absent in older files and for a clear that did not run.</summary>
    internal sealed record Saved(DateTime TimeUtc, string Mode, string Trigger, long PoolBefore, long PoolAfter, long PagedBefore, long PagedAfter, long StandbyBefore, long StandbyAfter, string? Error,
        IReadOnlyList<SavedTag>? Tags = null);

    /// <summary>A tag the clear freed, before and after: the evidence of whose records it freed.</summary>
    internal sealed record SavedTag(string Tag, long Before, long After);

    /// <summary>The tags a clear freed at least 1 MB of, most first, at most 12 (enough to tell whose records they were).</summary>
    private static List<SavedTag> FreedTags(ClearRecord r)
    {
        var after = r.TagsAfter.ToDictionary(t => t.Tag, t => t.Bytes);
        return r.TagsBefore.Select(t => new SavedTag(t.Tag.ToString(), t.Bytes, after.GetValueOrDefault(t.Tag)))
            .Where(t => t.Before - t.After >= 1L << 20).OrderByDescending(t => t.Before - t.After).Take(12).ToList();
    }

    internal static Saved ToSaved(ClearRecord r) => new(r.TimeUtc, r.Mode.ToString(), r.Trigger.ToString(), r.PoolBefore, r.PoolAfter,
        (long)r.Before.CommittedPaged, (long)(r.After?.CommittedPaged ?? r.Before.CommittedPaged), r.ListsBefore.StandbyBytes, r.ListsAfter.StandbyBytes, r.Error,
        r.Error is null ? FreedTags(r) : null); // a clear that did not run freed nothing, whatever its tags before were

    /// <summary>A saved clear as a record again: sizes below 0 read as 0, and a tag that is not a pool tag is left out.</summary>
    internal static ClearRecord FromSaved(Saved r)
    {
        ulong paged = (ulong)Math.Max(0, r.PagedBefore), pagedAfter = (ulong)Math.Max(0, r.PagedAfter);
        var tags = new List<(Core.PoolTag Tag, SavedTag Saved)>();
        foreach (var saved in r.Tags ?? [])
            if (Core.PoolTag.TryParse(saved.Tag, out var tag)) tags.Add((tag, saved));
        var rec = new ClearRecord
        {
            TimeUtc = r.TimeUtc, Mode = Enum.TryParse<ClearMode>(r.Mode, out var m) ? m : ClearMode.Standby, Trigger = Enum.TryParse<ClearTrigger>(r.Trigger, out var t) ? t : ClearTrigger.Manual,
            Before = new PoolTotals(paged, (ulong)Math.Max(0, r.PoolBefore - r.PagedBefore), 0, 0),
            ListsBefore = new MemoryLists(0, 0, r.StandbyBefore, 0, 0),
            TagsBefore = tags.Select(x => (x.Tag, Math.Max(0, x.Saved.Before))).ToList(),
        };
        if (r.Error is null) { rec.After = new PoolTotals(pagedAfter, (ulong)Math.Max(0, r.PoolAfter - r.PagedAfter), 0, 0); rec.ListsAfter = new MemoryLists(0, 0, r.StandbyAfter, 0, 0); }
        else rec.Error = r.Error;
        rec.TagsAfter = tags.Select(x => (x.Tag, Math.Max(0, x.Saved.After))).ToList();
        return rec;
    }

    private void Save()
    {
        try
        {
            List<Saved> rows;
            lock (_lock) rows = _history.Where(r => r.Done).TakeLast(100).Select(ToSaved).ToList();
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
            foreach (var rec in (JsonSerializer.Deserialize<List<Saved>>(File.ReadAllText(_stateFile)) ?? []).Select(FromSaved))
            {
                _reported.Add(rec);
                _history.Add(rec);
            }
        }
        catch (Exception ex) { _engine.Log.Info("clears.json not read: " + ex.Message); }
    }
}
