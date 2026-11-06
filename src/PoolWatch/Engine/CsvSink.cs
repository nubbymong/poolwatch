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
    // Each CSV file of the day is opened on its own, through _open (by default the no-wait opener: an open that would
    // have to wait for another program is given up and refused), at the first flush that has rows for it. A sample's
    // rows are kept with their file until a flush writes them, each file's in one write, and a write that fails is never
    // let out of this class: the sample goes on with the other files and with what follows the CSV writes. A file whose
    // open or write is refused is passed over, and its rows, those of the refused write too, go on in the file's next
    // numbered file (as for a header change, CsvFile). Rows that no file can take yet (the day's folder cannot be made,
    // the path check refuses it, an open or a write fails for another reason) are kept and written at a later flush, up
    // to MaxKeptChars in all the files; beyond that the oldest rows of the file that keeps the most are dropped. Each
    // failure is logged once until it ends, and so is its end. What is still kept, or was dropped, when the sink is
    // disposed is logged then.
    private readonly Func<string, FileMode, FileAccess, FileShare, Stream> _open;
    private readonly Action<string> _log;
    private string? _dir;
    private bool _dirMade;
    private readonly DayFile _totals = new("totals.csv",
            "time,committed_paged_bytes,committed_nonpaged_bytes,tagsum_paged_bytes,tagsum_nonpaged_bytes,tag_count,process_count," +
            "commit_bytes,commit_limit_bytes,available_bytes,compression_ws_bytes,system_handles"),
        _tags = new("tags.csv", "time,tag,tag_hex,paged_used,nonpaged_used,paged_allocs,paged_frees,nonpaged_allocs,nonpaged_frees"),
        _processes = new("processes.csv",
            "time,pid,create_time,image,parent_pid,handles,threads,quota_paged,quota_nonpaged,private_bytes,read_ops,write_ops,other_ops,read_bytes,write_bytes,d_read_ops,d_write_ops,d_other_ops,orphan"),
        _events = new("events.csv", "time,kind,pid,create_time,parent_pid,image,exit_code,user,command_line,detail"),
        _handles = new("handles.csv", "time,pid,image,type,count"),
        _services = new("services.csv", "time,pid,service,display_name"),
        _volumes = new("volumes.csv", "time,root,file_system,label,dev_drive,drive_type"),
        _systemHandles = new("system-handles.csv", "time,type,count"),
        _lag = new("lag.csv", "time,timer_ms,message_ms,file_ms,dwm_missed,committed_paged_bytes,committed_nonpaged_bytes"),
        // The load series (about 25 KB an hour at the 10 s interval).
        _load = new("load.csv",
            "time,cpu_fraction,run_queue,cores,page_faults_per_sec,demand_zero_per_sec,transition_per_sec,pages_input_per_sec,probe_tripping");
    /// <summary>The day's files, in the order a flush writes them.</summary>
    private readonly DayFile[] _files;
    /// <summary>The characters of the rows kept for all the files together, not written yet (the sum of each file's
    /// <see cref="DayFile.Chars"/>, kept as rows are kept, written or dropped).</summary>
    private long _keptChars;
    /// <summary>When the tag and process rows were last written, on the clock that only goes forward (the samples' TickMs); null: not yet.</summary>
    private long? _lastTagWriteMs, _lastProcWriteMs;
    private readonly Dictionary<ProcessKey, ProcessSample> _lastProcRow = new();
    private string _lastServiceSignature = "";

    /// <summary>The most that the rows no file could take yet may hold in all the files, in characters, before the oldest
    /// rows of the file that keeps the most are dropped.</summary>
    internal const int MaxKeptChars = 4 * 1024 * 1024;

    /// <summary>How many of a file's names one flush tries, each after the one before refused its rows.</summary>
    private const int NamesPerFlush = 3;

    /// <param name="log">Where a file that cannot be written is logged (once, and again once it ends). Null: nowhere.</param>
    /// <param name="open">How each CSV file is opened (null: <see cref="NoWaitOpen.OpenUnbuffered"/>). Tests stand in
    /// their own.</param>
    public CsvSink(AppPaths paths, Settings settings, bool verbose, Action<string>? log = null,
        Func<string, FileMode, FileAccess, FileShare, Stream>? open = null)
    {
        _paths = paths;
        _settings = settings;
        _verbose = verbose;
        _log = log ?? (_ => { });
        _open = open ?? NoWaitOpen.OpenUnbuffered;
        _files = [_totals, _tags, _processes, _events, _handles, _services, _volumes, _systemHandles, _lag, _load];
    }

    /// <summary>
    /// One CSV file of the day, with its header: the rows not written yet, the file they go to once it is open, and the
    /// number of the first of its names they may go to (1: the name itself; n: its n-th numbered file).
    /// </summary>
    private sealed class DayFile(string name, string header)
    {
        public string Name { get; } = name;
        public string Header { get; } = header;
        public CsvFile? File { get; set; }
        public List<string> Rows { get; } = new();
        public long Chars { get; set; }
        public int From { get; set; } = 1;
        /// <summary>The failure logged for this file, until it ends; null: none.</summary>
        public string? Failing { get; set; }
        /// <summary>Rows dropped beyond <see cref="MaxKeptChars"/> since the last write.</summary>
        public long Dropped { get; set; }
    }

    private DateTime _lastSwitchUtc = DateTime.MinValue;

    // Switches to the day's folder when the day changes. What is kept for the day before is written there first; what no
    // file of it could take is kept for the new day's files, so no row is lost at the switch.
    private void EnsureFiles(DateTime utcNow)
    {
        var dir = _paths.CsvDir(utcNow);
        if (dir == _dir) return;
        if (_dir is not null) Flush();
        _lastSwitchUtc = utcNow;
        CloseFiles();
        _dir = dir;
        _dirMade = false;
        _lastServiceSignature = "";
    }

    /// <summary>Keeps a row for <paramref name="file"/>, written at the next flush.</summary>
    private void Row(DayFile file, params object?[] values)
    {
        string line = Csv.Line(values);
        file.Rows.Add(line);
        file.Chars += line.Length;
        _keptChars += line.Length;
        Profiler.WroteName(file.Name, line.Length + 2);
        if (_keptChars <= MaxKeptChars) return;
        // No file has taken these rows for a long time: the oldest of the file that keeps the most go, so what is kept
        // stays bounded in all the files.
        var most = _files.MaxBy(f => f.Chars)!;
        int drop = 0;
        long chars = most.Chars;
        while (_keptChars > MaxKeptChars && drop < most.Rows.Count - 1)
        {
            int n = most.Rows[drop++].Length;
            chars -= n;
            _keptChars -= n;
        }
        most.Rows.RemoveRange(0, drop);
        most.Chars = chars;
        if (most.Dropped == 0 && drop > 0)
            _log($"{most.Name}: the rows that could not be written are kept up to {MaxKeptChars / (1024 * 1024)} M characters in all the files, and the oldest beyond that are dropped, until it can be written");
        most.Dropped += drop;
        if (most == _services && drop > 0) _lastServiceSignature = ""; // the list is written again at the next call
    }

    /// <summary>
    /// The day's <paramref name="file"/>, opened now when it is not open yet (from its number <see cref="DayFile.From"/>
    /// on: a name whose open is refused is passed over); null when it cannot be opened (its folder cannot be made, the path
    /// check refuses it, or every name is refused or fails): its rows are then kept for a later flush.
    /// </summary>
    private CsvFile? Open(DayFile file)
    {
        if (file.File is { } open) return open;
        if (_dir is not { } dir) return null;
        try
        {
            if (!_dirMade)
            {
                _paths.EnsureDir(dir);
                _dirMade = true;
            }
            file.File = new CsvFile(_paths.Guard.CheckReal(Path.Combine(dir, file.Name)), file.Header, _open, file.From);
        }
        catch (Exception e)
        {
            Failed(file, $"{file.Name} could not be opened ({e.Message.Trim()}): its rows are kept, and written once it can be (tried at each flush)");
            return null;
        }
        file.From = file.File.Number;
        foreach (var (name, why) in file.File.Refused)
            _log($"{name} could not be written ({why}): its rows go on in {file.File.Name}");
        return file.File;
    }

    /// <summary>
    /// Writes the rows kept for <paramref name="file"/>. A write that is refused moves them on to the next numbered file,
    /// within this flush; any other failure keeps them for the next flush. A write that fails is not thrown from here.
    /// </summary>
    private void Commit(DayFile file)
    {
        for (int i = 0; i < NamesPerFlush && file.Rows.Count > 0; i++)
        {
            if (Open(file) is not { } csv) return;
            try
            {
                csv.Append(file.Rows);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ObjectDisposedException)
            {
                Close(file);
                if (CsvFile.IsRefused(e))
                {
                    file.From = Math.Min(csv.Number + 1, CsvFile.LastNumber);
                    _log($"{csv.Name} could not be written ({e.Message.Trim()}): its rows go on in the next numbered file");
                }
                else
                {
                    Failed(file, $"{csv.Name} could not be written ({e.Message.Trim()}): its rows are kept, and written once it can be (tried at each flush)");
                    return;
                }
                continue;
            }
            _keptChars -= file.Chars;
            file.Rows.Clear();
            file.Chars = 0;
            if (file.Dropped > 0) _log($"{csv.Name}: {file.Dropped} rows that could not be kept were dropped");
            file.Dropped = 0;
            if (file.Failing is not null) _log($"{csv.Name} written again: the rows kept for it are written");
            file.Failing = null;
            return;
        }
    }

    // Logged once until the failure ends.
    private void Failed(DayFile file, string line)
    {
        if (file.Failing is null) _log(line);
        file.Failing = line;
    }

    public void WriteSample(Sample s, ProcessRegistry registry)
    {
        EnsureFiles(s.TimeUtc);
        Row(_totals, s.TimeUtc, s.Totals.CommittedPaged, s.Totals.CommittedNonPaged, s.Totals.TagSumPaged, s.Totals.TagSumNonPaged, s.Tags.Count, s.Processes.Count,
            s.System.CommitBytes, s.System.CommitLimitBytes, s.System.AvailableBytes, s.System.CompressionWsBytes, s.System.Handles);

        if (_verbose || Due(_lastTagWriteMs, s.TickMs, _settings.TagCsvIntervalSeconds))
        {
            _lastTagWriteMs = s.TickMs;
            foreach (var t in s.Tags)
            {
                if (!_verbose && t.TotalUsed < (ulong)_settings.TagCsvMinBytes && !BuiltinTags.AlwaysTracked.Contains(t.Tag)) continue;
                Row(_tags, s.TimeUtc, t.Tag.ToString(), t.Tag.Raw.ToString("x8"), t.PagedUsed, t.NonPagedUsed, t.PagedAllocs, t.PagedFrees, t.NonPagedAllocs, t.NonPagedFrees);
            }
        }

        if (_verbose || Due(_lastProcWriteMs, s.TickMs, _settings.ProcessCsvIntervalSeconds))
        {
            _lastProcWriteMs = s.TickMs;
            WriteProcesses(s, registry);
        }
        Flush();
    }

    /// <summary>Rows written every <paramref name="seconds"/> (half a second early is on time) are due again, on the forward clock.</summary>
    private static bool Due(long? lastMs, long nowMs, double seconds) => lastMs is not { } last || nowMs - last >= (long)((seconds - 0.5) * 1000);

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
            Row(_processes, s.TimeUtc, p.Pid, p.CreateTime, p.ImageName, p.ParentPid, p.HandleCount, p.ThreadCount, p.QuotaPagedPoolUsage,
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
            Row(_events, e.TimeUtc, e.Kind.ToString(), e.Pid, e.CreateTime, e.ParentPid, e.Image, e.ExitCode,
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
            Row(_handles, utcNow, -1, "*", typeNames.GetValueOrDefault(type, $"#{type}"), n);
        // The System process (PID 4) by type, every census (the reports saw it hold 1.75 M handles).
        foreach (var (type, n) in counts.ForProcess(4).OrderByDescending(kv => kv.Value))
            Row(_systemHandles, utcNow, typeNames.GetValueOrDefault(type, $"#{type}"), n);
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
                    Row(_handles, utcNow, pid, names.GetValueOrDefault(pid, ""), typeNames.GetValueOrDefault(type, $"#{type}"), n);
            }
        }
        Flush();
    }

    public void WriteServices(IReadOnlyList<ServiceEntry> services, DateTime utcNow)
    {
        EnsureFiles(utcNow);
        var sig = string.Join("|", services.OrderBy(s => s.Name).Select(s => s.Name + ":" + s.ProcessId));
        if (sig == _lastServiceSignature) return;
        // Its rows are kept until a file takes them, so an unchanged list is not written again.
        _lastServiceSignature = sig;
        foreach (var s in services.Where(s => s.ProcessId != 0).OrderBy(s => s.ProcessId))
            Row(_services, utcNow, s.ProcessId, s.Name, s.DisplayName);
        Flush();
    }

    public void WriteLag(Core.Lag.LagProbe p)
    {
        // Probes are drained after the sample: one taken just before midnight goes to the day already open, rather than
        // switching every file back to yesterday's folder.
        if (_dir is null || p.TimeUtc > _lastSwitchUtc) EnsureFiles(p.TimeUtc);
        Row(_lag, p.TimeUtc, Math.Round(p.TimerMs, 2), p.MessageMs is { } c ? Math.Round(c, 2) : null, Math.Round(p.FileMs, 2), p.DwmMissed,
            p.Pool.CommittedPaged, p.Pool.CommittedNonPaged);
    }

    public void WriteLoad(Core.Services.LoadPoint p, long pageFaultsPerSec)
    {
        if (_dir is null || p.TimeUtc > _lastSwitchUtc) EnsureFiles(p.TimeUtc);
        Row(_load, p.TimeUtc, Math.Round(p.CpuFraction, 3), p.RunQueue is { } q ? Math.Round(q, 1) : null, p.Cores, pageFaultsPerSec,
            p.Faults is { } f ? Math.Round(f.DemandZeroPerSec) : null, p.Faults is { } t ? Math.Round(t.TransitionPerSec) : null,
            p.Faults is { } i ? Math.Round(i.PagesInputPerSec) : null, p.ProbeTripping ? 1 : 0);
    }

    public void WriteVolumes(IReadOnlyList<VolumeInfo> volumes, DateTime utcNow)
    {
        EnsureFiles(utcNow);
        foreach (var v in volumes)
            Row(_volumes, utcNow, v.Root, v.FileSystem, v.Label, v.IsDevDrive?.ToString() ?? "unknown", v.DriveType);
        Flush();
    }

    /// <summary>Writes the rows kept for each file (<see cref="Commit"/>); a file that cannot be written is not thrown for.</summary>
    public void Flush()
    {
        foreach (var f in _files) Commit(f);
    }

    private static void Close(DayFile f)
    {
        try { f.File?.Close(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* let go of all the same */ }
        f.File = null;
    }

    // A file that failed the day before is tried afresh in the new day's folder (and logged again if it fails there); the
    // rows kept for it stay kept.
    private void CloseFiles()
    {
        foreach (var f in _files)
        {
            Close(f);
            f.From = 1;
            f.Failing = null;
        }
    }

    /// <summary>Writes what is kept, as a flush does, then lets go of each file. The rows of a file that no file could take
    /// even then, and those dropped since its last write, are logged: they are not in the CSV files.</summary>
    public void Dispose()
    {
        Flush();
        foreach (var f in _files)
        {
            if (f.Rows.Count > 0 || f.Dropped > 0)
                _log($"{f.Name}: the rows kept for it could not be written before PoolWatch let go of its files ({f.Rows.Count} kept" +
                     (f.Dropped > 0 ? $", {f.Dropped} dropped before" : "") + "): those rows are not in the CSV files");
            Close(f);
        }
    }
}
