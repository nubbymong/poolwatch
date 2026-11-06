using PoolWatch.Core.Model;
using PoolWatch.Native;

namespace PoolWatch.Collectors;

/// <summary>Maps NT device paths (\Device\HarddiskVolume3\...) to DOS paths (C:\...).</summary>
internal static unsafe class DevicePaths
{
    /// <summary>How long the map is kept before it is read again (a drive letter can be added or moved meanwhile).</summary>
    internal static readonly TimeSpan MapAge = TimeSpan.FromMinutes(10);

    /// <summary>The device map, kept for <see cref="MapAge"/> on the clock that only goes forward.</summary>
    private static readonly Kept<Dictionary<string, string>> Map = NewMap(Moment.Now, Build);

    /// <summary>A map of <paramref name="build"/>, kept for <see cref="MapAge"/> timed on <paramref name="clock"/>'s forward clock (a test gives its own).</summary>
    internal static Kept<Dictionary<string, string>> NewMap(Func<Moment> clock, Func<Dictionary<string, string>> build) => new(MapAge, build, clock);

    public static string ToDosPath(string? ntPath)
    {
        lock (Map) return ToDosPath(ntPath, Map);
    }

    /// <summary>The DOS path of <paramref name="ntPath"/> by <paramref name="map"/>, read again first when it is <see cref="MapAge"/> old.</summary>
    internal static string ToDosPath(string? ntPath, Kept<Dictionary<string, string>> map)
    {
        if (string.IsNullOrEmpty(ntPath)) return ntPath ?? "";
        if (ntPath.StartsWith(@"\??\")) return ntPath[4..];
        if (ntPath.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), ntPath[12..]);
        if (!ntPath.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase)) return ntPath;
        foreach (var (device, letter) in map.Value)
        {
            if (ntPath.StartsWith(device + "\\", StringComparison.OrdinalIgnoreCase))
                return letter + ntPath[device.Length..];
        }
        return ntPath;
    }

    private static Dictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        char* buf = stackalloc char[1024];
        foreach (var drive in Environment.GetLogicalDrives())
        {
            var letter = drive[..2];
            int n = NativeMethods.QueryDosDeviceW(letter, buf, 1024);
            if (n > 0) map[new string(buf)] = letter;
        }
        return map;
    }
}
