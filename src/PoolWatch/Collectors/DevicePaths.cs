using PoolWatch.Native;

namespace PoolWatch.Collectors;

/// <summary>Maps NT device paths (\Device\HarddiskVolume3\...) to DOS paths (C:\...).</summary>
internal static unsafe class DevicePaths
{
    private static Dictionary<string, string> _map = Build();
    private static DateTime _builtAt = DateTime.UtcNow;

    public static string ToDosPath(string? ntPath)
    {
        if (string.IsNullOrEmpty(ntPath)) return ntPath ?? "";
        if (ntPath.StartsWith(@"\??\")) return ntPath[4..];
        if (ntPath.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), ntPath[12..]);
        if (!ntPath.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase)) return ntPath;
        if (DateTime.UtcNow - _builtAt > TimeSpan.FromMinutes(10)) { _map = Build(); _builtAt = DateTime.UtcNow; }
        foreach (var (device, letter) in _map)
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
