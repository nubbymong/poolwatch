using PoolWatch.Native;

namespace PoolWatch.Analysis;

/// <summary>The explicit, bounded list of driver binaries the tag scan reads: loaded drivers plus System32\drivers\*.sys.</summary>
internal static unsafe class DriverFiles
{
    public static List<string> List()
    {
        var result = new List<string>();
        string sysRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        try
        {
            NativeMethods.EnumDeviceDrivers(null, 0, out int needed);
            int count = needed / IntPtr.Size + 16;
            var bases = new IntPtr[count];
            fixed (IntPtr* p = bases)
            {
                if (NativeMethods.EnumDeviceDrivers(p, count * IntPtr.Size, out needed))
                {
                    char* buf = stackalloc char[1024];
                    for (int i = 0; i < Math.Min(count, needed / IntPtr.Size); i++)
                    {
                        int n = NativeMethods.GetDeviceDriverFileNameW(bases[i], buf, 1024);
                        if (n > 0 && Normalize(new string(buf, 0, n), sysRoot) is { } path) result.Add(path);
                    }
                }
            }
        }
        catch { /* fall back to the drivers folder */ }
        try { result.AddRange(Directory.EnumerateFiles(Path.Combine(sysRoot, "System32", "drivers"), "*.sys")); }
        catch { }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Kernel image names come as \SystemRoot\..., \??\C:\... or \Windows\...; return a Win32 path.</summary>
    private static string? Normalize(string name, string sysRoot)
    {
        if (name.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) return Path.Combine(sysRoot, name[12..]);
        if (name.StartsWith(@"\??\")) return name[4..];
        if (name.StartsWith(@"\Windows\", StringComparison.OrdinalIgnoreCase)) return Path.Combine(sysRoot, name[9..]);
        if (name.Length > 2 && name[1] == ':') return name;
        return null;
    }
}
