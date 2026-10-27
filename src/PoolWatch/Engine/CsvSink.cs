using PoolWatch.Core;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;
using PoolWatch.Core.Util;

namespace PoolWatch.Engine;

/// <summary>
/// Writes the continuous CSV record: totals every sample; tags / processes at their (coarser) CSV interval and
/// filtered to significant rows, which keeps steady-state disk writes well under the 5 MB/hour budget.
/// </summary>
internal sealed class CsvSink : IDisposable
{
    private readonly AppPaths _paths;
    private readonly Settings _settings;
    private readonly bool _verbose;
    private string? _dir;
    private CsvFile? _totals, _tags, _processes, _events, _handles, _services, _volumes, _systemHandles, _lag, _load;
    private DateTime _lastTagWrite = DateTime.MinValue, _lastProcWrite = DateTime.MinValue;
    private readonly Dictionary<ProcessKey, ProcessSample> _lastProcRow = new();
    private string _lastServiceSignature = "";

    public CsvSink(AppPaths paths, Settings settings, bool verbose)
    {
        _paths = paths;
        _settings = settings;
        _verbose = verbose;
    }

    private DateTime _lastSwitchUtc = DateTime.MinValue;

    private void EnsureFiles(DateTime utcNow)
    {
        var dir = _paths.CsvDir(utcNow);
        if (dir == _dir) return;
        _lastSwitchUtc = utcNow;
        DisposeFiles();
        _paths.EnsureDir(dir);
        _dir = dir;
        _totals = new CsvFile(_paths.Guard.CheckReal(Path.Combine(dir, "totals.csv")),
            "time,committed_paged_bytes,committed_nonpaged_bytes,tagsum_paged_bytes,tagsum_nonpaged_bytes,tag_count,process_count," +
            "commit_bytes,commit_limit_bytes,available_bytes,compression_ws_bytes,system_handles");
        _tags = new CsvFile(_paths.Guard.CheckReal(Path.Combine(dir, "tags.csv")),
            "time,tag,tag_hex,paged_used,nonpaged_used,paged_allocs,paged_frees,nonpaged_allocs,nonpaged_frees");
        _processes = new CsvFile(_paths.Guard.CheckReal(Path.Combine(dir, "processes.csv")),
            "time,pid,create_time,image,parent_pid,handles,threads,quota_paged,quota_nonpaged,private_bytes,read_ops,write_ops,other_ops,read_bytes,write_bytes,d_read_ops,d_write_ops,d_other_ops,orphan");
        _events = new CsvFile(_paths.Guard.CheckReal(Path.Combine(dir, "events.csv")),
            "time,kind,pid,create_time,parent_pid,image,exit_code,user,command_line,detail");
        _handles = new CsvFile(_paths.Guard.CheckReal(Path.Combine(dir, "handles.csv")),
            "time,pid,image,type,count");
        _services = new CsvFile(_paths.Guard.CheckReal(Path.Combine(dir, "services.csv")),
            "time,pid,service,display_name");
        _volumes = new CsvFile(_paths.Guard.CheckReal(Path.Combine(dir, "volumes.csv")),
            "time,root,file_system,label,dev_drive,drive_type");
        _systemHandles = new CsvFile(_paths.Guard.CheckReal(Path.Combine(dir, "system-handles.csv")),
            "time,type,count");
        _lag = new CsvFile(_paths.Guard.CheckReal(Path.Combine(dir, "lag.csv")),
            "time,timer_ms,message_ms,file_ms,dwm_missed,committed_paged_bytes,committed_nonpaged_bytes");
        // Addendum 4 A: the load series (about 25 KB an hour at the 10 s interval).
        _load = new CsvFile(_paths.Guard.CheckReal(Path.Combine(dir, "load.csv")),
            "time,cpu_fraction,run_queue,cores,page_faults_per_sec,demand_zero_per_sec,transition_per_sec,pages_input_per_sec,probe_tripping");
        _lastServiceSignature = "";
    }

    public void WriteSample(Sample s, ProcessRegistry registry)
    {
        EnsureFiles(s.TimeUtc);
        _totals!.Write(s.TimeUtc, s.Totals.CommittedPaged, s.Totals.CommittedNonPaged, s.Totals.TagSumPaged, s.Totals.TagSumNonPaged, s.Tags.Count, s.Processes.Count,
            s.System.CommitBytes, s.System.CommitLimitBytes, s.System.AvailableBytes, s.System.CompressionWsBytes, s.System.Handles);

        if (_verbose || s.TimeUtc - _lastTagWrite >= TimeSpan.FromSeconds(_settings.TagCsvIntervalSeconds - 0.5))
        {
            _lastTagWrite = s.TimeUtc;
            foreach (var t in s.Tags)
            {
                if (!_verbose && t.TotalUsed < (ulong)_settings.TagCsvMinBytes && !BuiltinTags.AlwaysTracked.Contains(t.Tag)) continue;
                _tags!.Write(s.TimeUtc, t.Tag.ToString(), t.Tag.Raw.ToString("x8"), t.PagedUsed, t.NonPagedUsed, t.PagedAllocs, t.PagedFrees, t.NonPagedAllocs, t.NonPagedFrees);
            }
        }

        if (_verbose || s.TimeUtc - _lastProcWrite >= TimeSpan.FromSeconds(_settings.ProcessCsvIntervalSeconds - 0.5))
        {
            _lastProcWrite = s.TimeUtc;
            WriteProcesses(s, registry);
        }
        Flush();
    }

    private void WriteProcesses(Sample s, ProcessRegistry registry)
    {
        int self = Environment.ProcessId;
        var rows = new List<(ProcessSample p, long dR, long dW, long dO, int dH)>();
        foreach (var p in s.Processes)
        {
            _lastProcRow.TryGetValue(p.Key, out var prev);
            long dR = prev is null ? 0 : p.ReadOperationCount - prev.ReadOperationCount;
            long dW = prev is null ? 0 : p.WriteOperationCount - prev.WriteOperationCount;
            long dO = prev is null ? 0 : p.OtherOperationCount - prev.OtherOperationCount;
            int dH = prev is null ? 0 : p.HandleCount - prev.HandleCount;
            rows.Add((p, dR, dW, dO, dH));
        }
        var chosen = new HashSet<ProcessKey>(rows.OrderByDescending(r => r.dR + r.dW + r.dO).Take(_settings.ProcessCsvTopN).Select(r => r.p.Key));
        foreach (var r in rows)
        {
            bool include = _verbose || chosen.Contains(r.p.Key) || Math.Abs(r.dH) >= 100 || r.p.Pid == self || !_lastProcRow.ContainsKey(r.p.Key);
            if (!include) continue;
            var p = r.p;
            bool orphan = registry.Get(p.Key)?.IsOrphan ?? false;
            _processes!.Write(s.TimeUtc, p.Pid, p.CreateTime, p.ImageName, p.ParentPid, p.HandleCount, p.ThreadCount, p.QuotaPagedPoolUsage,
                p.QuotaNonPagedPoolUsage, p.PrivateBytes, p.ReadOperationCount, p.WriteOperationCount, p.OtherOperationCount,
                p.ReadTransferCount, p.WriteTransferCount, r.dR, r.dW, r.dO, orphan ? 1 : 0);
        }
        _lastProcRow.Clear();
        foreach (var p in s.Processes) _lastProcRow[p.Key] = p;
    }

    public void WriteEvents(IEnumerable<LifecycleEvent> events, DateTime utcNow)
    {
        EnsureFiles(utcNow);
        foreach (var e in events)
            _events!.Write(e.TimeUtc, e.Kind.ToString(), e.Pid, e.CreateTime, e.ParentPid, e.Image, e.ExitCode,
                e.User is null ? null : Collectors.ProcessDetails.DisplayUser(e.User), e.CommandLine, e.Detail);
    }

    public void WriteHandles(HandleCounts counts, IReadOnlyDictionary<int, string> typeNames, IReadOnlyList<ProcessSample> processes, DateTime utcNow)
    {
        EnsureFiles(utcNow);
        // Per-type totals (pid -1), then the top processes by handle count with their significant types.
        var totals = new Dictionary<int, long>();
        foreach (var pid in counts.Pids)
            foreach (var (type, n) in counts.ForProcess(pid)) totals[type] = totals.GetValueOrDefault(type) + n;
        foreach (var (type, n) in totals.OrderByDescending(kv => kv.Value))
            _handles!.Write(utcNow, -1, "*", typeNames.GetValueOrDefault(type, $"#{type}"), n);
        // The System process (PID 4) by type, every census (the reports saw it hold 1.75 M handles).
        foreach (var (type, n) in counts.ForProcess(4).OrderByDescending(kv => kv.Value))
            _systemHandles!.Write(utcNow, typeNames.GetValueOrDefault(type, $"#{type}"), n);
        var names = processes.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First().ImageName);
        var top = counts.Pids.Select(pid => (pid, total: counts.ForProcess(pid).Values.Sum())).OrderByDescending(x => x.total).Take(20).Select(x => x.pid).ToHashSet();
        int self = Environment.ProcessId;
        foreach (var pid in counts.Pids)
        {
            var types = counts.ForProcess(pid);
            bool isTop = top.Contains(pid) || pid == self;
            foreach (var (type, n) in types)
            {
                if (n >= 500 || (isTop && n >= 20))
                    _handles!.Write(utcNow, pid, names.GetValueOrDefault(pid, ""), typeNames.GetValueOrDefault(type, $"#{type}"), n);
            }
        }
        Flush();
    }

    public void WriteServices(IReadOnlyList<ServiceEntry> services, DateTime utcNow)
    {
        EnsureFiles(utcNow);
        var sig = string.Join("|", services.OrderBy(s => s.Name).Select(s => s.Name + ":" + s.ProcessId));
        if (sig == _lastServiceSignature) return;
        _lastServiceSignature = sig;
        foreach (var s in services.Where(s => s.ProcessId != 0).OrderBy(s => s.ProcessId))
            _services!.Write(utcNow, s.ProcessId, s.Name, s.DisplayName);
        Flush();
    }

    public void WriteLag(Core.Lag.LagProbe p)
    {
        // Probes are drained after the sample: one taken just before midnight goes to the day already open, rather than
        // switching every file back to yesterday's folder.
        if (_lag is null || p.TimeUtc > _lastSwitchUtc) EnsureFiles(p.TimeUtc);
        _lag!.Write(p.TimeUtc, Math.Round(p.TimerMs, 2), p.MessageMs is { } c ? Math.Round(c, 2) : null, Math.Round(p.FileMs, 2), p.DwmMissed,
            p.Pool.CommittedPaged, p.Pool.CommittedNonPaged);
    }

    public void WriteLoad(Core.Services.LoadPoint p, long pageFaultsPerSec)
    {
        if (_load is null || p.TimeUtc > _lastSwitchUtc) EnsureFiles(p.TimeUtc);
        _load!.Write(p.TimeUtc, Math.Round(p.CpuFraction, 3), p.RunQueue is { } q ? Math.Round(q, 1) : null, p.Cores, pageFaultsPerSec,
            p.Faults is { } f ? Math.Round(f.DemandZeroPerSec) : null, p.Faults is { } t ? Math.Round(t.TransitionPerSec) : null,
            p.Faults is { } i ? Math.Round(i.PagesInputPerSec) : null, p.ProbeTripping ? 1 : 0);
    }

    public void WriteVolumes(IReadOnlyList<VolumeInfo> volumes, DateTime utcNow)
    {
        EnsureFiles(utcNow);
        foreach (var v in volumes)
            _volumes!.Write(utcNow, v.Root, v.FileSystem, v.Label, v.IsDevDrive?.ToString() ?? "unknown", v.DriveType);
        Flush();
    }

    public void Flush()
    {
        _totals?.Flush(); _tags?.Flush(); _processes?.Flush(); _events?.Flush(); _handles?.Flush(); _services?.Flush(); _volumes?.Flush(); _systemHandles?.Flush(); _lag?.Flush(); _load?.Flush();
    }

    private void DisposeFiles()
    {
        _totals?.Dispose(); _tags?.Dispose(); _processes?.Dispose(); _events?.Dispose(); _handles?.Dispose(); _services?.Dispose(); _volumes?.Dispose(); _systemHandles?.Dispose(); _lag?.Dispose(); _load?.Dispose();
        _totals = _tags = _processes = _events = _handles = _services = _volumes = _systemHandles = _lag = _load = null;
    }

    public void Dispose() => DisposeFiles();
}
