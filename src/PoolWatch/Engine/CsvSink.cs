using PoolWatch.Core;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;
using PoolWatch.Core.Util;

namespace PoolWatch.Engine;

/// <summary>
/// Writes the continuous CSV record: totals every sample; tags / processes at their (coarser) CSV interval and
/// filtered to significant rows. Under a PC doing agentic coding the day's files wrote about 4.7 MB an hour (the owner's
/// 1.0.0-beta.2), nearly the whole 5 MB budget; their share is now 2.5 MB an hour beside the report folders' 1 MB
/// (DESIGN.md section 3), and <c>DailyCsvBudgetTests</c> measures them under that load. So outside verbose mode each day's
/// files start with the whole picture (every process, every tag over the minimum, the whole services list, every handle
/// row) and then hold what changed: a tag's row when its paged or non-paged use moved <see cref="TagMoveBytes"/> since its
/// last row (and every <see cref="TagRefresh"/> while it stays over the minimum, once more as it falls under it); a
/// process once it was running at two process writes (a shorter one is in events.csv), then while it is among the busiest
/// by I/O (any at all), its handles moved by 100, or it is PoolWatch; a service when the process that hosts it changed
/// (0: stopped); a process's handles of a type when their count changed, and once more as it leaves the selection (the
/// per-type totals and the System process's every scan); an Exit or Orphaned event without the command line its Start
/// row already gave that day; and the event summary's minute rollups merged over <see cref="RollupMerge"/>. AGENTS.md
/// says, file by file, what a reader may carry forward from a row and what it may not. The handle scan's two files are
/// written on a thread of their own (<see cref="PostHandles"/>, <see cref="HandleCsvWriter"/>).
///
/// The day's files are written only with Detailed recording (<see cref="Settings.RecordMode"/>, read at each write, so a
/// change in Settings or setup applies without a restart). In Reports only, the default, nothing is written and no day
/// folder is made: each write returns at once, holding nothing. Turned off, the sink writes what it made while it was on
/// (the rows kept, the rollups being merged), lets go of every file and of the handle scan's writer, and forgets what the
/// files held; turned on again, the next write starts the day's files with the whole picture, as a start of PoolWatch does
/// (<see cref="TurnedOn"/> tells the engine, which writes the services and volumes it holds and asks for a handle scan).
/// </summary>
internal sealed class CsvSink : IDisposable
{
    /// <summary>A tag's row is written when its paged or non-paged use moved at least this much since its last row ...</summary>
    internal const long TagMoveBytes = 1L << 20;
    /// <summary>... and at least this often while it stays over the minimum, on the forward clock.</summary>
    internal static readonly TimeSpan TagRefresh = TimeSpan.FromMinutes(10);
    /// <summary>The event summary's minute rollups are merged over this long in the day's events.csv.</summary>
    internal static readonly TimeSpan RollupMerge = TimeSpan.FromMinutes(5);
    /// <summary>At most this many rollups are merged at once (the rest written as they come).</summary>
    internal const int MaxRollupsMerged = 2000;
    /// <summary>At most this many processes' Start rows are remembered for their Exit and Orphaned rows (past it, forgotten).</summary>
    internal const int MaxStartsRemembered = 10_000;
    /// <summary>At most this many per-process rows of one handle scan, the largest counts first.</summary>
    internal const int MaxHandleRowsPerScan = 2000;

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

    // What the day's files hold so far (the last row of each), so that only what changed is written: forgotten at the day's
    // switch, and for a file whose kept rows were dropped (Forget), whose next write then gives the whole picture again.
    /// <summary>Each tag's last row: its paged and non-paged use, and when (forward clock).</summary>
    private readonly Dictionary<PoolTag, (ulong Paged, ulong NonPaged, long Ms)> _tagRows = new();
    /// <summary>The processes with a row in the day's processes.csv (live ones), and whether the next write lists every process.</summary>
    private readonly HashSet<ProcessKey> _procRows = new();
    private bool _procAll = true;
    /// <summary>Each service's host and display name as the day's services.csv has them; null: the whole list is due.</summary>
    private Dictionary<string, (int Pid, string Display)>? _serviceRows;
    /// <summary>Each process instance's last handle count by type in the day's handles.csv.</summary>
    private readonly Dictionary<(ProcessKey Key, int Type), int> _handleRows = new();
    /// <summary>
    /// The processes whose Start row the day's events.csv has with a command line, still running, and that command line's
    /// mark (its length and hash): an Exit or Orphaned row leaves out only the same command line.
    /// </summary>
    private readonly Dictionary<ProcessKey, (int Length, int Hash)> _startRows = new();
    /// <summary>Whether the event summary's rollups come merged (<see cref="MergeRollupsFrom"/>), and those being merged.</summary>
    private bool _mergeRollups;
    private readonly Dictionary<(string? OwnerKey, string Parent, string Image), MergedRollup> _merged = new();
    private long? _mergeFromMs;

    /// <summary>
    /// Rollups of one key being merged (<see cref="RollupMerge"/>), as the event summary counts them. Of the first one only
    /// its names and first parent are kept (<see cref="Head"/>): its collections are the summary's own, valid during its
    /// call only, and the merge counts its own.
    /// </summary>
    private sealed class MergedRollup(RollupParts head)
    {
        private static readonly IReadOnlyDictionary<string, int> NoPatterns = new Dictionary<string, int>();
        public readonly RollupParts Head = head with { ParentPids = [], Patterns = NoPatterns };
        public int Starts, Exits;
        public readonly HashSet<int> ParentPids = [];
        public readonly Dictionary<string, int> Patterns = new(StringComparer.Ordinal);
        public DateTime From = DateTime.MaxValue, To = DateTime.MinValue;

        public void Add(RollupParts p)
        {
            Starts += p.Starts;
            Exits += p.Exits;
            foreach (int pid in p.ParentPids) if (ParentPids.Count < Core.Model.EventSummarizer.MaxRollupParents) ParentPids.Add(pid);
            foreach (var (pattern, n) in p.Patterns)
                if (Patterns.ContainsKey(pattern) || Patterns.Count < Core.Model.EventSummarizer.MaxRollupPatterns) Patterns[pattern] = Patterns.GetValueOrDefault(pattern) + n;
            if (p.First < From) From = p.First;
            if (p.Last > To) To = p.Last;
        }

        public RollupParts Parts() => Head with { Starts = Starts, Exits = Exits, ParentPids = ParentPids, First = From, Last = To, Patterns = Patterns };
    }

    /// <summary>The most that the rows no file could take yet may hold in all the files, in characters, before the oldest
    /// rows of the file that keeps the most are dropped.</summary>
    internal const int MaxKeptChars = 4 * 1024 * 1024;

    /// <summary>
    /// The handle scan's own sink (<see cref="HandleCsvWriter"/>, its two files only) keeps this much of its own: rows of
    /// about six scans that no file could take yet (a scan writes at most <see cref="MaxHandleRowsPerScan"/> rows of
    /// about 40 characters, and its totals).
    /// </summary>
    internal const int HandleKeptChars = 512 * 1024;

    /// <summary>How many of a file's names one flush tries, each after the one before refused its rows.</summary>
    private const int NamesPerFlush = 3;

    private readonly int _maxKeptChars;

    /// <param name="log">Where a file that cannot be written is logged (once, and again once it ends). Null: nowhere.</param>
    /// <param name="open">How each CSV file is opened (null: <see cref="NoWaitOpen.OpenUnbuffered"/>). Tests stand in
    /// their own.</param>
    /// <param name="maxKeptChars">The bound on the rows kept (<see cref="MaxKeptChars"/>; the handle scan's sink
    /// <see cref="HandleKeptChars"/>).</param>
    public CsvSink(AppPaths paths, Settings settings, bool verbose, Action<string>? log = null,
        Func<string, FileMode, FileAccess, FileShare, Stream>? open = null, int maxKeptChars = MaxKeptChars)
    {
        _paths = paths;
        _settings = settings;
        _verbose = verbose;
        _log = log ?? (_ => { });
        _open = open ?? NoWaitOpen.OpenUnbuffered;
        _maxKeptChars = maxKeptChars;
        _files = [_totals, _tags, _processes, _events, _handles, _services, _volumes, _systemHandles, _lag, _load];
        _on = Recording; // the mode it starts in is no change: a start of PoolWatch begins the day's files as before
        _records = new(_on, DateTime.UtcNow);
    }

    /// <summary>Detailed recording is chosen now (<see cref="Settings.RecordMode"/>): the day's files are written.</summary>
    public bool Recording => _settings.RecordMode == RecordMode.Detailed;

    /// <summary>What the day's files record, as the sink follows the choice, and since when without a change.</summary>
    /// <param name="Detailed">Detailed recording: the day's files are written.</param>
    /// <param name="SinceUtc">When the sink started, or followed the last change of the choice (<see cref="On"/>).</param>
    internal sealed record RecordState(bool Detailed, DateTime SinceUtc);

    private volatile RecordState _records;

    /// <summary>
    /// What the day's files record and since when (any thread): an incident's report says the day's events.csv holds the
    /// events it left out only for a time Detailed recording was on throughout (<see cref="Core.Incidents.IIncidentHost.DayRecords"/>).
    /// </summary>
    public RecordState Records => _records;

    /// <summary>The mode the last write saw (<see cref="On"/>).</summary>
    private bool _on;
    /// <summary>Recording turned on since the engine last asked (<see cref="TurnedOn"/>).</summary>
    private bool _turnedOn;

    /// <summary>
    /// Whether this write is made: Detailed recording is chosen now. When the mode changed since the last write, the sink
    /// follows it first: turned off, it closes cleanly (<see cref="StopWriting"/>); turned on, its next rows start the day's
    /// files with the whole picture (<see cref="StartAgain"/>).
    /// </summary>
    private bool On()
    {
        bool on = Recording;
        if (on == _on) return on;
        _on = on;
        _records = new(on, DateTime.UtcNow);
        if (on)
        {
            StartAgain();
            _turnedOn = true;
            _log("Detailed recording turned on: the day's CSV files and findings.jsonl are written again, starting with the whole picture");
        }
        else
        {
            StopWriting();
            _log("Detailed recording turned off (Reports only): the day's CSV files were closed, and nothing more is written to them");
        }
        return on;
    }

    /// <summary>
    /// Sampler thread, at the start of each tick: whether Detailed recording was turned on since the last call. The engine
    /// then writes the services and the volumes it holds and asks for a handle scan, so that the day's files start again
    /// with the whole picture in this tick, as at PoolWatch's start.
    /// </summary>
    public bool TurnedOn()
    {
        On();
        bool turned = _turnedOn;
        _turnedOn = false;
        return turned;
    }

    /// <summary>
    /// Detailed recording was turned off: the rows it made while it was on (kept, or the rollups being merged) are written,
    /// then each file and the handle scan's writer are let go of, as at the end of a run, and what the files held is
    /// forgotten, along with the processes' last rows, so nothing of them is held while nothing is written. The handle
    /// scan's writer is not waited for (the tick never waits for its write, which took 41.9 s in 1.0.0-beta.2): the scan it
    /// holds is not written, and a write going on ends on its own thread, which then closes its two files
    /// (<see cref="HandleCsvWriter.LetGo"/>); no other writer starts on them until it has (<see cref="PostHandles"/>).
    /// </summary>
    private void StopWriting()
    {
        if (_handleWriter is { } writer)
        {
            _handleWriter = null; // first, so that HandleScansNotWritten never counts its scans twice
            Interlocked.Add(ref _handleScansNotWrittenBefore, writer.Replaced); // final: nothing is handed to it any more
            writer.LetGo();
            _stoppedHandleWriter = writer;
        }
        WriteMerged();
        foreach (var f in _files) Commit(f);
        foreach (var f in _files)
        {
            if (f.Rows.Count > 0 || f.Dropped > 0)
                _log($"{f.Name}: the rows kept for it could not be written before recording was turned off ({f.Rows.Count} kept" +
                     (f.Dropped > 0 ? $", {f.Dropped} dropped before" : "") + "): those rows are not in the CSV files");
            Close(f);
            f.Rows.Clear();
            f.Chars = 0;
            f.Dropped = 0;
            f.From = 1;
            f.Failing = null;
        }
        _keptChars = 0;
        _merged.Clear();
        _mergeFromMs = null;
        _dir = null;
        _dirMade = false;
        _lastTagWriteMs = _lastProcWriteMs = null;
        _lastProcRow.Clear();
        Forget(null);
    }

    /// <summary>Detailed recording was turned on: the next rows are the day's whole picture, from the next sample.</summary>
    private void StartAgain()
    {
        _dir = null; // the next write opens today's folder
        _dirMade = false;
        _lastTagWriteMs = _lastProcWriteMs = null;
        _lastProcRow.Clear();
        _mergeFromMs = null;
        Forget(null);
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

    // Switches to the day's folder when the day changes. What is kept for the day before is written there first (the
    // rollups being merged too); what no file of it could take is kept for the new day's files, so no row is lost at the
    // switch. Each new day's files start with the whole picture again (Forget).
    private void EnsureFiles(DateTime utcNow)
    {
        var dir = _paths.CsvDir(utcNow);
        if (dir == _dir) return;
        if (_dir is not null)
        {
            WriteMerged();
            Flush();
        }
        _lastSwitchUtc = utcNow;
        CloseFiles();
        _dir = dir;
        _dirMade = false;
        Forget(null);
    }

    /// <summary>
    /// Forgets what the day's <paramref name="file"/> (null: every file) holds, so its next write gives the whole picture:
    /// at the day's switch, and when rows kept for it were dropped (it may not hold what it was thought to).
    /// </summary>
    private void Forget(DayFile? file)
    {
        if (file is null || file == _tags) _tagRows.Clear();
        if (file is null || file == _processes) { _procRows.Clear(); _procAll = true; }
        if (file is null || file == _services) _serviceRows = null;
        if (file is null || file == _handles) _handleRows.Clear();
        if (file is null || file == _events) _startRows.Clear();
    }

    /// <summary>Keeps a row for <paramref name="file"/>, written at the next flush.</summary>
    private void Row(DayFile file, params object?[] values)
    {
        string line = Csv.Line(values);
        file.Rows.Add(line);
        file.Chars += line.Length;
        _keptChars += line.Length;
        Profiler.WroteName(file.Name, line.Length + 2);
        if (_keptChars <= _maxKeptChars) return;
        // No file has taken these rows for a long time: the oldest of the file that keeps the most go, so what is kept
        // stays bounded in all the files.
        var most = _files.MaxBy(f => f.Chars)!;
        int drop = 0;
        long chars = most.Chars;
        while (_keptChars > _maxKeptChars && drop < most.Rows.Count - 1)
        {
            int n = most.Rows[drop++].Length;
            chars -= n;
            _keptChars -= n;
        }
        most.Rows.RemoveRange(0, drop);
        most.Chars = chars;
        if (most.Dropped == 0 && drop > 0)
            _log($"{most.Name}: the rows that could not be written are kept up to {(_maxKeptChars >= 1 << 20 ? $"{_maxKeptChars >> 20} M" : $"{_maxKeptChars >> 10} K")} characters in all the files, and the oldest beyond that are dropped, until it can be written");
        most.Dropped += drop;
        if (drop > 0) Forget(most); // its next write gives the whole picture again (the services list, every tag, ...)
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
        if (!On()) return;
        EnsureFiles(s.TimeUtc);
        Row(_totals, s.TimeUtc, s.Totals.CommittedPaged, s.Totals.CommittedNonPaged, s.Totals.TagSumPaged, s.Totals.TagSumNonPaged, s.Tags.Count, s.Processes.Count,
            s.System.CommitBytes, s.System.CommitLimitBytes, s.System.AvailableBytes, s.System.CompressionWsBytes, s.System.Handles);

        if (_verbose || Due(_lastTagWriteMs, s.TickMs, _settings.TagCsvIntervalSeconds))
        {
            _lastTagWriteMs = s.TickMs;
            WriteTags(s);
        }

        if (_verbose || Due(_lastProcWriteMs, s.TickMs, _settings.ProcessCsvIntervalSeconds))
        {
            _lastProcWriteMs = s.TickMs;
            WriteProcesses(s, registry);
        }
        // The rollups merged over RollupMerge are written once it has passed, on the forward clock.
        _mergeFromMs ??= s.TickMs;
        if (s.TickMs - _mergeFromMs >= (long)RollupMerge.TotalMilliseconds)
        {
            WriteMerged();
            _mergeFromMs = s.TickMs;
        }
        Flush();
    }

    /// <summary>
    /// The tags over the minimum (and those always tracked): in verbose mode each one at every write; otherwise each one
    /// whose paged or non-paged use moved <see cref="TagMoveBytes"/> since its last row, or whose last row is
    /// <see cref="TagRefresh"/> old, and once more as it falls under the minimum (its row says so: under it).
    /// </summary>
    private void WriteTags(Sample s)
    {
        long refreshMs = (long)TagRefresh.TotalMilliseconds;
        foreach (var t in s.Tags)
        {
            bool over = t.TotalUsed >= (ulong)_settings.TagCsvMinBytes || BuiltinTags.AlwaysTracked.Contains(t.Tag);
            if (!_verbose)
            {
                if (_tagRows.TryGetValue(t.Tag, out var last))
                {
                    if (!over) _tagRows.Remove(t.Tag); // its last row, under the minimum: no more until it is over it again
                    else if (Moved(t.PagedUsed, last.Paged) < TagMoveBytes && Moved(t.NonPagedUsed, last.NonPaged) < TagMoveBytes && s.TickMs - last.Ms < refreshMs) continue;
                }
                else if (!over) continue;
                if (over) _tagRows[t.Tag] = (t.PagedUsed, t.NonPagedUsed, s.TickMs);
            }
            Row(_tags, s.TimeUtc, t.Tag.ToString(), t.Tag.Raw.ToString("x8"), t.PagedUsed, t.NonPagedUsed, t.PagedAllocs, t.PagedFrees, t.NonPagedAllocs, t.NonPagedFrees);
        }
    }

    private static ulong Moved(ulong now, ulong was) => now > was ? now - was : was - now;

    /// <summary>Rows written every <paramref name="seconds"/> (half a second early is on time) are due again, on the forward clock.</summary>
    private static bool Due(long? lastMs, long nowMs, double seconds) => lastMs is not { } last || nowMs - last >= (long)((seconds - 0.5) * 1000);

    /// <summary>
    /// The processes: in verbose mode, and at the day's first write, every one; otherwise the <see cref="Settings.ProcessCsvTopN"/>
    /// busiest by I/O since the last write (that did any), any whose handles moved by 100, PoolWatch itself, and, once, each
    /// that was running at the last write too (one that lived less than that is in events.csv, start and exit or rolled up).
    /// </summary>
    private void WriteProcesses(Sample s, ProcessRegistry registry)
    {
        int self = Environment.ProcessId;
        bool all = _verbose || _procAll;
        var rows = new List<(ProcessSample p, long dR, long dW, long dO, int dH, bool before)>(s.Processes.Count);
        foreach (var p in s.Processes)
        {
            bool before = _lastProcRow.TryGetValue(p.Key, out var prev);
            long dR = prev is null ? 0 : p.ReadOperationCount - prev.ReadOperationCount;
            long dW = prev is null ? 0 : p.WriteOperationCount - prev.WriteOperationCount;
            long dO = prev is null ? 0 : p.OtherOperationCount - prev.OtherOperationCount;
            int dH = prev is null ? 0 : p.HandleCount - prev.HandleCount;
            rows.Add((p, dR, dW, dO, dH, before));
        }
        var chosen = new HashSet<ProcessKey>(rows.Where(r => r.dR + r.dW + r.dO > 0 || _verbose).OrderByDescending(r => r.dR + r.dW + r.dO)
            .Take(_settings.ProcessCsvTopN).Select(r => r.p.Key));
        foreach (var r in rows)
        {
            var p = r.p;
            bool include = all || chosen.Contains(p.Key) || Math.Abs(r.dH) >= 100 || p.Pid == self || (r.before && !_procRows.Contains(p.Key));
            if (!include) continue;
            _procRows.Add(p.Key);
            bool orphan = registry.Get(p.Key)?.IsOrphan ?? false;
            Row(_processes, s.TimeUtc, p.Pid, p.CreateTime, p.ImageName, p.ParentPid, p.HandleCount, p.ThreadCount, p.QuotaPagedPoolUsage,
                p.QuotaNonPagedPoolUsage, p.PrivateBytes, p.ReadOperationCount, p.WriteOperationCount, p.OtherOperationCount,
                p.ReadTransferCount, p.WriteTransferCount, r.dR, r.dW, r.dO, orphan ? 1 : 0);
        }
        _procAll = false;
        _lastProcRow.Clear();
        foreach (var p in s.Processes) _lastProcRow[p.Key] = p;
        _procRows.RemoveWhere(k => !_lastProcRow.ContainsKey(k)); // the live ones only
    }

    /// <summary>
    /// The day's events.csv rows. An Exit or Orphaned row of a process whose Start row the day's file has with the same
    /// command line (this run, the latest <see cref="MaxStartsRemembered"/> running ones) leaves that command line out: a
    /// Start row without one (the process was not read yet) is not remembered, so a command line read later is written
    /// with its exit. (Exit and Orphaned events carry no user.) The event summary's rollups, when they come merged
    /// (<see cref="MergeRollupsFrom"/>), are written by <see cref="AddRollup"/> instead.
    /// </summary>
    public void WriteEvents(IEnumerable<LifecycleEvent> events, DateTime utcNow)
    {
        if (!On()) return;
        EnsureFiles(utcNow);
        foreach (var e in events)
        {
            if (_mergeRollups && e.Kind == LifecycleKind.Rollup) continue;
            bool started = !_verbose && e.Kind is LifecycleKind.Exit or LifecycleKind.Orphaned && e.CreateTime != 0 && e.CommandLine is { } given
                && _startRows.TryGetValue(e.Key, out var mark) && mark == Mark(given);
            Row(_events, e.TimeUtc, e.Kind.ToString(), e.Pid, e.CreateTime, e.ParentPid, e.Image, e.ExitCode,
                e.User is null ? null : Collectors.ProcessDetails.DisplayUser(e.User), started ? null : e.CommandLine, e.Detail);
            if (e.Kind == LifecycleKind.Start && e.CreateTime != 0 && !_verbose && e.CommandLine is { } command)
            {
                if (_startRows.Count >= MaxStartsRemembered) _startRows.Clear(); // their exits are written whole
                _startRows[e.Key] = Mark(command);
            }
            else if (e.Kind == LifecycleKind.Exit) _startRows.Remove(e.Key);
        }
    }

    /// <summary>A command line's mark: its length and hash (this run's), so the sink holds no copy of it.</summary>
    private static (int Length, int Hash) Mark(string commandLine) => (commandLine.Length, commandLine.GetHashCode(StringComparison.Ordinal));

    /// <summary>
    /// The day's events.csv takes the event summary's rollups from <paramref name="summarizer"/> as each closes, and merges
    /// those of one owner, parent and child image over <see cref="RollupMerge"/> (a row each, as the summary words it:
    /// their starts and exits added, their parents and commands counted together); the rollups it hands on with its notable
    /// events are then not written again. Not in verbose mode, which writes every rollup.
    /// </summary>
    public void MergeRollupsFrom(Core.Model.EventSummarizer summarizer)
    {
        if (_verbose) return;
        summarizer.RollupClosed += AddRollup;
        _mergeRollups = true;
    }

    /// <summary>
    /// A rollup closed (<see cref="MergeRollupsFrom"/>): merged with the others of its key until <see cref="RollupMerge"/> has
    /// passed. In Reports only it is neither merged nor kept.
    /// </summary>
    internal void AddRollup(RollupParts parts)
    {
        if (!On()) return;
        var key = (parts.OwnerKey, parts.Parent, parts.Image);
        if (!_merged.TryGetValue(key, out var m))
        {
            if (_merged.Count >= MaxRollupsMerged)
            {
                Rollup(parts); // too many at once: this one as it is
                return;
            }
            _merged[key] = m = new MergedRollup(parts);
        }
        m.Add(parts);
    }

    /// <summary>Writes the merged rollups, the earliest first, and starts merging anew.</summary>
    private void WriteMerged()
    {
        if (_merged.Count == 0) return;
        if (_dir is null) return; // nothing written yet: kept for the first day's file
        foreach (var m in _merged.Values.OrderBy(m => m.From)) Rollup(m.Parts());
        _merged.Clear();
    }

    private void Rollup(RollupParts parts)
    {
        var e = parts.Event();
        Row(_events, e.TimeUtc, e.Kind.ToString(), e.Pid, e.CreateTime, e.ParentPid, e.Image, e.ExitCode, null, null, e.Detail);
    }

    /// <summary>
    /// A handle scan: the per-type totals (pid -1) and the System process's handles by type (system-handles.csv) every scan;
    /// then the 20 processes with the most handles (and PoolWatch) by each type they hold 20 of, and any process by each type
    /// it holds 500 of, at most <see cref="MaxHandleRowsPerScan"/> rows (the largest first; a pid -2 row counts those left
    /// out): in verbose mode, and at the day's
    /// first scan, each; otherwise each whose count changed since that process's last row of the type, and once more each
    /// (process, type) with a row that this scan did not pick while the process still runs, with its count now (0: it holds
    /// none of that type): so the last row of each is what it holds until a newer row says otherwise, or the process exits
    /// (its exit is in events.csv). Only its own two files are written here (the others at the sample's flush). Called on
    /// the handle scan's own thread (<see cref="PostHandles"/>), and by tests. Returns how long its rows took to make and
    /// its two files to write (profiled too: csv.handles.rows, csv.handles.commit).
    /// </summary>
    public (TimeSpan Rows, TimeSpan Commit) WriteHandles(HandleCounts counts, IReadOnlyDictionary<int, string> typeNames, IReadOnlyList<ProcessSample> processes, DateTime utcNow)
    {
        if (!On()) return (TimeSpan.Zero, TimeSpan.Zero); // turned off since the scan was handed over: not written
        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        var rows = Profiler.Stage("csv.handles.rows");
        EnsureFiles(utcNow);
        string Type(int type) => typeNames.TryGetValue(type, out var name) ? name : "#" + type.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var totals = new Dictionary<int, long>();
        var byPid = new List<(int Pid, long Total)>();
        foreach (var pid in counts.Pids)
        {
            long total = 0;
            foreach (var (type, n) in counts.ForProcess(pid))
            {
                totals[type] = totals.GetValueOrDefault(type) + n;
                total += n;
            }
            byPid.Add((pid, total));
        }
        foreach (var (type, n) in totals.OrderByDescending(kv => kv.Value)) Row(_handles, utcNow, -1, "*", Type(type), n);
        // The System process (PID 4) by type, every census (the reports saw it hold 1.75 M handles).
        foreach (var (type, n) in counts.ForProcess(4).OrderByDescending(kv => kv.Value)) Row(_systemHandles, utcNow, Type(type), n);
        var keys = new Dictionary<int, ProcessSample>();
        foreach (var p in processes) keys.TryAdd(p.Pid, p);
        ProcessKey KeyOf(int pid) => keys.TryGetValue(pid, out var p) ? p.Key : new ProcessKey(pid, 0);
        byPid.Sort((a, b) => b.Total.CompareTo(a.Total));
        int self = Environment.ProcessId;
        var top = new HashSet<int>(byPid.Take(20).Select(x => x.Pid)) { self };
        var picked = new List<(int Pid, int Type, int N)>();
        foreach (var (pid, _) in byPid)
            foreach (var (type, n) in counts.ForProcess(pid))
                if (n >= 500 || (top.Contains(pid) && n >= 20)) picked.Add((pid, type, n));
        if (picked.Count > MaxHandleRowsPerScan)
        {
            // The scan's file says so: pid -2 counts the process counts left out (the smallest), which are not recorded
            // until a scan without one (AGENTS.md).
            Row(_handles, utcNow, -2, "*", "(left out)", picked.Count - MaxHandleRowsPerScan);
            picked = picked.OrderByDescending(x => x.N).Take(MaxHandleRowsPerScan).ToList();
        }
        var written = new HashSet<(ProcessKey Key, int Type)>();
        foreach (var (pid, type, n) in picked)
        {
            var key = (KeyOf(pid), type);
            if (!_verbose)
            {
                written.Add(key);
                if (_handleRows.TryGetValue(key, out int was) && was == n) continue;
                _handleRows[key] = n;
            }
            Row(_handles, utcNow, pid, keys.GetValueOrDefault(pid)?.ImageName ?? "", Type(type), n);
        }
        if (_handleRows.Count > written.Count)
        {
            // A count with a row that this scan did not pick (it fell under the selection, the process left the 20 with the
            // most handles, or the scan's rows were capped): one more row with its count now, then none until it is picked
            // again, so the last row a reader carries forward is the count when it stopped being recorded, not an older one
            // (AGENTS.md says a last row may be that). A process that exited (or whose PID now belongs to another) has its
            // exit in events.csv: no row.
            var running = new HashSet<ProcessKey>();
            foreach (var pid in counts.Pids) running.Add(KeyOf(pid));
            foreach (var k in _handleRows.Keys.Where(k => !written.Contains(k)).ToList())
            {
                _handleRows.Remove(k);
                if (running.Contains(k.Key))
                    Row(_handles, utcNow, k.Key.Pid, keys.GetValueOrDefault(k.Key.Pid)?.ImageName ?? "", Type(k.Type), counts.Count(k.Key.Pid, k.Type));
            }
        }
        rows.Dispose();
        long made = System.Diagnostics.Stopwatch.GetTimestamp();
        using (Profiler.Stage("csv.handles.commit"))
        {
            Commit(_handles);
            Commit(_systemHandles);
        }
        return (System.Diagnostics.Stopwatch.GetElapsedTime(began, made), System.Diagnostics.Stopwatch.GetElapsedTime(made));
    }

    /// <summary>The handle scan's writer (<see cref="PostHandles"/>), made at the first scan.</summary>
    private HandleCsvWriter? _handleWriter;
    /// <summary>The writer let go of when Detailed recording was last turned off, until its last write has ended (<see cref="StopWriting"/>).</summary>
    private HandleCsvWriter? _stoppedHandleWriter;

    /// <summary>
    /// Hands a handle scan to its own thread (<see cref="HandleCsvWriter"/>), which writes it to handles.csv and
    /// system-handles.csv through a sink of its own (<see cref="WriteHandles"/>), and returns at once: the sampler's tick
    /// never waits for that write. The census, type names and process list are not changed after the scan. One writer at a
    /// time: while the one let go of at a switch-off still ends its last write (the two files are its own until it has), a
    /// scan is not written, counted and logged, as one replaced while it waited is. Sampler thread.
    /// </summary>
    public void PostHandles(HandleCounts counts, IReadOnlyDictionary<int, string> typeNames, IReadOnlyList<ProcessSample> processes, DateTime utcNow)
    {
        if (!On()) return;
        if (_stoppedHandleWriter is { } stopped)
        {
            if (!stopped.Ended)
            {
                long n = Interlocked.Increment(ref _handleScansNotWrittenBefore);
                _log($"handles.csv: the handle scan of {utcNow.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)} UTC was not written: " +
                     $"the write going on when recording was turned off has not ended yet ({n:N0} not written so far)");
                return;
            }
            _stoppedHandleWriter = null;
        }
        (_handleWriter ??= new HandleCsvWriter(new CsvSink(_paths, _settings, _verbose, _log, _open, HandleKeptChars), _log))
            .Post(new HandleCsvWriter.Scan(counts, typeNames, processes, utcNow));
    }

    /// <summary>
    /// The scans not written by the handle scan's writers let go of when recording was turned off (<see cref="StopWriting"/>),
    /// and those handed over while such a writer still ended its last write (<see cref="PostHandles"/>).
    /// </summary>
    private long _handleScansNotWrittenBefore;

    /// <summary>
    /// Handle scans whose rows were not written: each was handed over while an earlier one still waited
    /// (<see cref="HandleCsvWriter"/>), or while the write going on when recording was turned off had not ended.
    /// </summary>
    public long HandleScansNotWritten => Interlocked.Read(ref _handleScansNotWrittenBefore) + (_handleWriter?.Replaced ?? 0);

    /// <summary>
    /// The services and the process that hosts each: at the day's first call the whole list (those running), then a row for
    /// each service whose host changed, with PID 0 for one that stopped or left the list. A change no file could take yet is
    /// kept, so an unchanged list writes nothing. Only services.csv is written here.
    /// </summary>
    public void WriteServices(IReadOnlyList<ServiceEntry> services, DateTime utcNow)
    {
        if (!On()) return;
        EnsureFiles(utcNow);
        var now = new Dictionary<string, (int Pid, string Display)>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in services) now[s.Name] = (s.ProcessId, s.DisplayName);
        var was = _serviceRows;
        _serviceRows = now;
        if (was is null)
        {
            foreach (var s in services.Where(s => s.ProcessId != 0).OrderBy(s => s.ProcessId)) Row(_services, utcNow, s.ProcessId, s.Name, s.DisplayName);
        }
        else
        {
            var changed = new List<(int Pid, string Name, string Display)>();
            foreach (var (name, (pid, display)) in now)
                if ((was.TryGetValue(name, out var before) ? before.Pid : 0) != pid) changed.Add((pid, name, display));
            foreach (var (name, (pid, display)) in was)
                if (pid != 0 && !now.ContainsKey(name)) changed.Add((0, name, display));
            if (changed.Count == 0) return;
            foreach (var (pid, name, display) in changed.OrderBy(c => c.Pid).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)) Row(_services, utcNow, pid, name, display);
        }
        Commit(_services);
    }

    public void WriteLag(Core.Lag.LagProbe p)
    {
        if (!On()) return;
        // Probes are drained after the sample: one taken just before midnight goes to the day already open, rather than
        // switching every file back to yesterday's folder.
        if (_dir is null || p.TimeUtc > _lastSwitchUtc) EnsureFiles(p.TimeUtc);
        Row(_lag, p.TimeUtc, Math.Round(p.TimerMs, 2), p.MessageMs is { } c ? Math.Round(c, 2) : null, Math.Round(p.FileMs, 2), p.DwmMissed,
            p.Pool.CommittedPaged, p.Pool.CommittedNonPaged);
    }

    public void WriteLoad(Core.Services.LoadPoint p, long pageFaultsPerSec)
    {
        if (!On()) return;
        if (_dir is null || p.TimeUtc > _lastSwitchUtc) EnsureFiles(p.TimeUtc);
        Row(_load, p.TimeUtc, Math.Round(p.CpuFraction, 3), p.RunQueue is { } q ? Math.Round(q, 1) : null, p.Cores, pageFaultsPerSec,
            p.Faults is { } f ? Math.Round(f.DemandZeroPerSec) : null, p.Faults is { } t ? Math.Round(t.TransitionPerSec) : null,
            p.Faults is { } i ? Math.Round(i.PagesInputPerSec) : null, p.ProbeTripping ? 1 : 0);
    }

    /// <summary>The volumes (at start and every hour). Only volumes.csv is written here.</summary>
    public void WriteVolumes(IReadOnlyList<VolumeInfo> volumes, DateTime utcNow)
    {
        if (!On()) return;
        EnsureFiles(utcNow);
        foreach (var v in volumes)
            Row(_volumes, utcNow, v.Root, v.FileSystem, v.Label, v.IsDevDrive?.ToString() ?? "unknown", v.DriveType);
        Commit(_volumes);
    }

    /// <summary>Writes the rows kept for each file (<see cref="Commit"/>); a file that cannot be written is not thrown for.</summary>
    public void Flush()
    {
        if (!On()) return;
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

    /// <summary>Writes what is kept (the rollups being merged too), as a flush does, then lets go of each file. The rows of a
    /// file that no file could take even then, and those dropped since its last write, are logged: they are not in the CSV
    /// files. The handle scan's writer is let go of first: it writes the scan it was handed, within its time
    /// (<see cref="HandleCsvWriter.Dispose"/>), and so is one let go of when recording was turned off, whose last write may
    /// still be going on. In Reports only every file is let go of already (as recording was turned off, which this looks at
    /// first), and nothing is written.</summary>
    public void Dispose()
    {
        bool on = On();
        _stoppedHandleWriter?.Dispose(); // waits for the write it left going on, within its time
        _stoppedHandleWriter = null;
        if (!on) return;
        _handleWriter?.Dispose();
        WriteMerged();
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
