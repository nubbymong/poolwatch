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
    /// <summary>The object type names, read again every hour on the clock that only goes forward (<see cref="TypeNamesAge"/>).</summary>
    private readonly Kept<Dictionary<int, string>> _typeNames;

    /// <summary>How long the object type names are kept before they are read again.</summary>
    internal static readonly TimeSpan TypeNamesAge = TimeSpan.FromHours(1);

    /// <param name="clock">Reads the two clocks (<see cref="Moment.Now"/>; a test gives its own).</param>
    public SystemQueries(Func<Moment>? clock = null) => _typeNames = new(TypeNamesAge, ReadTypeNames, clock);

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

    /// <summary>Object type index -> name (File, Event, Token...). Kept; types do not change at runtime, but read again
    /// hourly (<see cref="TypeNamesAge"/>) in case a driver registers a new type.</summary>
    public IReadOnlyDictionary<int, string> ObjectTypeNames() => _typeNames.Value;

    private static Dictionary<int, string> ReadTypeNames()
    {
        using var buf = new NativeBuffer(64 * 1024);
        int len = buf.Query((p, size) =>
        {
            int st = NativeMethods.NtQueryObject(IntPtr.Zero, NativeMethods.ObjectTypesInformation, (void*)p, size, out int ret);
            return (st, ret);
        });
        return ObjectTypesParser.Parse(buf.Span(len), buf.Address).ToDictionary(t => t.TypeIndex, t => t.Name);
    }

    public void Dispose()
    {
        _tagBuffer.Dispose();
        _procBuffer.Dispose();
    }
}
