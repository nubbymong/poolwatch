namespace PoolWatch.Core.Model;

/// <summary>Handle counts per process per object type index, from SystemExtendedHandleInformation.</summary>
public sealed class HandleCounts
{
    private readonly Dictionary<int, Dictionary<int, int>> _byPid = new();

    public long TotalHandles { get; private set; }

    public void Add(int pid, int typeIndex)
    {
        if (!_byPid.TryGetValue(pid, out var types)) _byPid[pid] = types = new Dictionary<int, int>();
        types[typeIndex] = types.GetValueOrDefault(typeIndex) + 1;
        TotalHandles++;
    }

    public IReadOnlyDictionary<int, int> ForProcess(int pid) =>
        _byPid.TryGetValue(pid, out var t) ? t : EmptyTypes;

    public int Count(int pid, int typeIndex) => _byPid.TryGetValue(pid, out var t) ? t.GetValueOrDefault(typeIndex) : 0;

    public IEnumerable<int> Pids => _byPid.Keys;

    private static readonly Dictionary<int, int> EmptyTypes = new();
}
