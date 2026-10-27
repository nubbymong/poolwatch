using System.Diagnostics;
using PoolWatch.Core.Model;
using PoolWatch.Core.Parsing;
using PoolWatch.Native;

namespace PoolWatch.Collectors;

/// <summary>
/// The always-on native queries: pool tags, processes, handles and pool totals. Not thread-safe; owned by the
/// sampler thread. Buffers are reused between calls (except the handle table, which can be tens of MB).
/// </summary>
internal sealed unsafe class SystemQueries : IDisposable
{
    private readonly NativeBuffer _tagBuffer = new(256 * 1024);
    private readonly NativeBuffer _procBuffer = new(1024 * 1024);
    private Dictionary<int, string>? _typeNames;
    private DateTime _typeNamesAt;

    public List<PoolTagEntry> QueryPoolTags()
    {
        int len = _tagBuffer.Query((p, size) =>
        {
            int st = NativeMethods.NtQuerySystemInformation(SystemInfoClass.SystemPoolTagInformation, (void*)p, size, out int ret);
            return (st, ret);
        });
        return PoolTagInfoParser.Parse(_tagBuffer.Span(len));
    }

    public List<ProcessSample> QueryProcesses()
    {
        int len = _procBuffer.Query((p, size) =>
        {
            int st = NativeMethods.NtQuerySystemInformation(SystemInfoClass.SystemProcessInformation, (void*)p, size, out int ret);
            return (st, ret);
        });
        return ProcessInfoParser.Parse(_procBuffer.Span(len), _procBuffer.Address);
    }

    /// <summary>Committed paged / non-paged pool bytes, as Task Manager shows them.</summary>
    public static (ulong paged, ulong nonPaged) QueryCommittedPool()
    {
        var (p, np, _) = QueryPerformance();
        return (p, np);
    }

    /// <summary>Pool totals and the system counters from one GetPerformanceInfo call (compression working set left 0).</summary>
    public static (ulong paged, ulong nonPaged, SystemCounters system) QueryPerformance()
    {
        if (!NativeMethods.GetPerformanceInfo(out var pi, sizeof(NativeMethods.PERFORMANCE_INFORMATION)))
            return (0, 0, default);
        ulong page = pi.PageSize;
        return (pi.KernelPaged * page, pi.KernelNonpaged * page,
            new SystemCounters(pi.CommitTotal * page, pi.CommitLimit * page, pi.PhysicalAvailable * page, 0, pi.HandleCount, pi.ProcessCount));
    }

    /// <summary>
    /// Handle counts per process per object type. The table is allocated for the scan and freed afterwards.
    /// </summary>
    public (HandleCounts counts, TimeSpan elapsed) QueryHandles()
    {
        var sw = Stopwatch.StartNew();
        using var buf = new NativeBuffer(4 * 1024 * 1024);
        int len = buf.Query((p, size) =>
        {
            int st = NativeMethods.NtQuerySystemInformation(SystemInfoClass.SystemExtendedHandleInformation, (void*)p, size, out int ret);
            return (st, ret);
        });
        var counts = HandleInfoParser.Parse(buf.Span(len));
        return (counts, sw.Elapsed);
    }

    /// <summary>Object type index -> name (File, Event, Token...). Cached; types do not change at runtime, but
    /// refreshed hourly in case a driver registers a new type.</summary>
    public IReadOnlyDictionary<int, string> ObjectTypeNames()
    {
        if (_typeNames is not null && DateTime.UtcNow - _typeNamesAt < TimeSpan.FromHours(1)) return _typeNames;
        using var buf = new NativeBuffer(64 * 1024);
        int len = buf.Query((p, size) =>
        {
            int st = NativeMethods.NtQueryObject(IntPtr.Zero, NativeMethods.ObjectTypesInformation, (void*)p, size, out int ret);
            return (st, ret);
        });
        _typeNames = ObjectTypesParser.Parse(buf.Span(len), buf.Address).ToDictionary(t => t.TypeIndex, t => t.Name);
        _typeNamesAt = DateTime.UtcNow;
        return _typeNames;
    }

    public void Dispose()
    {
        _tagBuffer.Dispose();
        _procBuffer.Dispose();
    }
}
