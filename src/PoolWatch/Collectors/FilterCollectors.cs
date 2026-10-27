using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics.Eventing.Reader;
using System.Runtime.InteropServices;
using System.Text;
using PoolWatch.Core.Filters;

namespace PoolWatch.Collectors;

/// <summary>
/// FilterManager events (System log) and AppModel-Runtime events, read with the native event-log API (no WMI): the
/// ones since boot (or since the last run, whichever is later) once at start, most recent first and capped, then a push
/// subscription. Records land in a bounded queue the sampler drains.
/// </summary>
internal sealed class EventLogFeed : IDisposable
{
    private const int HistoryCap = 300, MaxQueued = 5000;
    private readonly ConcurrentQueue<LogEvent> _queue = new();
    private readonly List<EventLogWatcher> _watchers = new();
    private readonly Action<string> _log;
    private readonly string? _stateFile;
    private readonly Func<string, string> _checkWrite;
    private DateTime _newest = DateTime.MinValue, _saved = DateTime.MinValue, _lastSave = DateTime.MinValue;
    private long _dropped;

    /// <param name="stateFile">Where the newest record time seen is kept, so a restart does not replay the same records.</param>
    public EventLogFeed(Action<string> log, string? stateFile = null, Func<string, string>? checkWrite = null)
    {
        _log = log;
        _stateFile = stateFile;
        _checkWrite = checkWrite ?? (p => p);
    }

    public List<string> Status { get; } = new();
    public long Dropped => Interlocked.Read(ref _dropped);

    public void Start()
    {
        long sinceBootMs = Environment.TickCount64;
        // Since the last run's newest record when that is after boot (a restart must not re-report the same events).
        if (ReadState() is { } ticks && ticks > 0 && ticks < DateTime.MaxValue.Ticks)
        {
            _newest = _saved = new DateTime(ticks, DateTimeKind.Utc);
            long sinceLastMs = (long)(DateTime.UtcNow - _saved).TotalMilliseconds;
            if (sinceLastMs >= 0 && sinceLastMs < sinceBootMs) sinceBootMs = Math.Max(1, sinceLastMs - 1);
        }
        Subscribe("System", $"*[System[Provider[@Name='{FilterTimeline.FilterManagerProvider}'] and TimeCreated[timediff(@SystemTime) <= {sinceBootMs}]]]",
            $"*[System[Provider[@Name='{FilterTimeline.FilterManagerProvider}']]]");
        Subscribe(FilterTimeline.AppModelChannel, $"*[System[TimeCreated[timediff(@SystemTime) <= {sinceBootMs}]]]", "*");
    }

    /// <summary>
    /// The newest record time the last run saw, or null. C6: this runs in the Diagnostics constructor, so an unreadable state
    /// file (locked, access denied, garbage) must never stop PoolWatch from starting: logged, and the history since boot is
    /// read instead.
    /// </summary>
    private long? ReadState()
    {
        if (_stateFile is null) return null;
        try
        {
            if (!File.Exists(_stateFile)) return null;
            return long.TryParse(File.ReadAllText(_stateFile).Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long t) ? t : null;
        }
        catch (Exception ex)
        {
            _log($"event log state not read ({ex.Message}); reading the records since boot");
            return null;
        }
    }

    private void Subscribe(string channel, string historyXPath, string liveXPath)
    {
        try
        {
            // History since boot: newest first, capped, then replayed oldest first.
            var history = new List<LogEvent>();
            using (var reader = new EventLogReader(new EventLogQuery(channel, PathType.LogName, historyXPath) { ReverseDirection = true }))
            {
                for (int i = 0; i < HistoryCap && reader.ReadEvent() is { } rec; i++)
                    using (rec) if (Convert(channel, rec) is { } e) history.Add(e);
            }
            history.Reverse();
            foreach (var e in history) Enqueue(e);
            var watcher = new EventLogWatcher(new EventLogQuery(channel, PathType.LogName, liveXPath));
            watcher.EventRecordWritten += (_, a) =>
            {
                try { using (a.EventRecord) if (a.EventRecord is not null && Convert(channel, a.EventRecord) is { } e) Enqueue(e); }
                catch { }
            };
            watcher.Enabled = true;
            _watchers.Add(watcher);
            Status.Add($"{channel}: {history.Count} since boot, subscribed");
        }
        catch (Exception ex)
        {
            Status.Add($"{channel}: unavailable ({ex.Message})");
            _log($"event log {channel} unavailable: {ex.Message}");
        }
    }

    private static LogEvent? Convert(string channel, EventRecord r)
    {
        string? message = null;
        try { message = r.FormatDescription(); } catch { }
        var props = new List<string>();
        try { foreach (var p in r.Properties.Take(8)) props.Add(p.Value?.ToString() ?? ""); } catch { }
        return new LogEvent((r.TimeCreated ?? DateTime.Now).ToUniversalTime(), channel, r.ProviderName ?? "", r.Id, message, props);
    }

    /// <summary>Drops records beyond <see cref="MaxQueued"/> (the sampler stalled): memory stays bounded.</summary>
    private void Enqueue(LogEvent e)
    {
        if (_queue.Count >= MaxQueued) { Interlocked.Increment(ref _dropped); return; }
        _queue.Enqueue(e);
    }

    /// <summary>Records received since the last call. Sampler thread.</summary>
    public List<LogEvent> Drain()
    {
        var list = new List<LogEvent>();
        while (list.Count < 1000 && _queue.TryDequeue(out var e))
        {
            list.Add(e);
            if (e.TimeUtc > _newest) _newest = e.TimeUtc;
        }
        if (_newest > _saved && DateTime.UtcNow - _lastSave >= TimeSpan.FromMinutes(10)) SaveState();
        return list;
    }

    private void SaveState()
    {
        _lastSave = DateTime.UtcNow;
        if (_stateFile is null || _newest <= _saved) return;
        try
        {
            File.WriteAllText(_checkWrite(_stateFile), _newest.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _saved = _newest;
        }
        catch (Exception ex) { _log("event log state not saved: " + ex.Message); }
    }

    public void Dispose()
    {
        SaveState();
        foreach (var w in _watchers) { try { w.Enabled = false; w.Dispose(); } catch { } }
        _watchers.Clear();
    }
}

/// <summary>Minifilter instances per volume through fltlib (FilterFindFirst / FilterInstanceFindFirst). Needs elevation.</summary>
internal static unsafe class FilterInstanceQuery
{
    private const int FilterFullInformation = 0, InstanceFullInformation = 2;
    private const int HrNoMoreItems = unchecked((int)0x80070103), HrInsufficientBuffer = unchecked((int)0x8007007A);

    [DllImport("fltlib.dll")] private static extern int FilterFindFirst(int infoClass, byte* buffer, int size, out int returned, out IntPtr find);
    [DllImport("fltlib.dll")] private static extern int FilterFindNext(IntPtr find, int infoClass, byte* buffer, int size, out int returned);
    [DllImport("fltlib.dll")] private static extern int FilterFindClose(IntPtr find);
    [DllImport("fltlib.dll", CharSet = CharSet.Unicode)] private static extern int FilterInstanceFindFirst(string filterName, int infoClass, byte* buffer, int size, out int returned, out IntPtr find);
    [DllImport("fltlib.dll")] private static extern int FilterInstanceFindNext(IntPtr find, int infoClass, byte* buffer, int size, out int returned);
    [DllImport("fltlib.dll")] private static extern int FilterInstanceFindClose(IntPtr find);

    /// <summary>All instances, or null with the reason when they cannot be listed.</summary>
    public static (List<FilterInstance>? instances, string? note) Query()
    {
        if (!Environment.IsPrivilegedProcess) return (null, "not elevated");
        try
        {
            var list = new List<FilterInstance>();
            foreach (var filter in Filters())
                list.AddRange(Instances(filter));
            return (list, null);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    private static List<string> Filters()
    {
        var names = new List<string>();
        var buf = new byte[4096];
        fixed (byte* p = buf)
        {
            int hr = FilterFindFirst(FilterFullInformation, p, buf.Length, out _, out var find);
            if (hr == HrNoMoreItems) return names;
            if (hr != 0) throw new InvalidOperationException($"FilterFindFirst 0x{hr:x8}");
            try
            {
                do
                {
                    if (FilterName(buf) is { } name) names.Add(name);
                    hr = FilterFindNext(find, FilterFullInformation, p, buf.Length, out _);
                } while (hr == 0);
                if (hr != HrNoMoreItems && hr != HrInsufficientBuffer) throw new InvalidOperationException($"FilterFindNext 0x{hr:x8}");
            }
            finally { FilterFindClose(find); }
        }
        return names;
    }

    private static List<FilterInstance> Instances(string filter)
    {
        var list = new List<FilterInstance>();
        var buf = new byte[4096];
        fixed (byte* p = buf)
        {
            int hr = FilterInstanceFindFirst(filter, InstanceFullInformation, p, buf.Length, out _, out var find);
            if (hr != 0) return list; // no instances (or the filter went away meanwhile)
            try
            {
                for (int i = 0; hr == 0 && i < 256; i++)
                {
                    // INSTANCE_FULL_INFORMATION: NextEntryOffset, then (length, offset) USHORT pairs for instance name (4),
                    // altitude (8), volume name (12) and filter name (16); offsets are from the start of the structure.
                    if (Field(buf, 12) is { } volume && Field(buf, 8) is { } altitude && Field(buf, 4) is { } instance)
                    {
                        string dos = DevicePaths.ToDosPath(volume + "\\");
                        list.Add(new FilterInstance(filter, dos.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase) ? volume : dos, altitude, instance));
                    }
                    hr = FilterInstanceFindNext(find, InstanceFullInformation, p, buf.Length, out _);
                }
            }
            finally { FilterInstanceFindClose(find); }
        }
        return list;
    }

    /// <summary>
    /// The name in a FILTER_FULL_INFORMATION record (ULONG NextEntryOffset, FrameID, NumberOfInstances, then USHORT
    /// FilterNameLength in bytes and the name at offset 14). Null when the length runs past the record.
    /// </summary>
    internal static string? FilterName(ReadOnlySpan<byte> record)
    {
        if (record.Length < 14) return null;
        int len = BinaryPrimitives.ReadUInt16LittleEndian(record[12..]) & ~1;
        return 14 + len <= record.Length ? Encoding.Unicode.GetString(record.Slice(14, len)) : null;
    }

    /// <summary>
    /// A (USHORT length in bytes, USHORT offset from the record start) string field of an INSTANCE_FULL_INFORMATION
    /// record at <paramref name="at"/>. Null when the pair or the string runs past the record.
    /// </summary>
    internal static string? Field(ReadOnlySpan<byte> record, int at)
    {
        if (at < 0 || at + 4 > record.Length) return null;
        int len = BinaryPrimitives.ReadUInt16LittleEndian(record[at..]) & ~1, offset = BinaryPrimitives.ReadUInt16LittleEndian(record[(at + 2)..]);
        return offset + len <= record.Length ? Encoding.Unicode.GetString(record.Slice(offset, len)) : null;
    }
}

/// <summary>Running state of named services (Win32 services and drivers) via OpenService + QueryServiceStatusEx.</summary>
internal static unsafe class ServiceStates
{
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr OpenSCManagerW(string? machine, string? db, uint access);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatusEx(IntPtr service, int level, byte* buffer, int size, out int needed);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr h);

    public static Dictionary<string, bool> Query(IEnumerable<string> names)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var scm = OpenSCManagerW(null, null, 0x0001 /* CONNECT */);
        if (scm == IntPtr.Zero) return result;
        try
        {
            byte* buf = stackalloc byte[64];
            foreach (var name in names)
            {
                var svc = OpenServiceW(scm, name, 0x0004 /* QUERY_STATUS */);
                if (svc == IntPtr.Zero) { result[name] = false; continue; } // not installed
                try { result[name] = QueryServiceStatusEx(svc, 0, buf, 64, out _) && *(uint*)(buf + 4) == 4 /* SERVICE_RUNNING */; }
                finally { CloseServiceHandle(svc); }
            }
        }
        finally { CloseServiceHandle(scm); }
        return result;
    }
}
