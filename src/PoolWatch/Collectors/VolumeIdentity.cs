using PoolWatch.Native;

namespace PoolWatch.Collectors;

/// <summary>
/// Resolves a path to the GUID of the volume it finally lands on, following junctions, mount points and subst
/// drives, so the write policy judges the real volume, not an alias of it. Uses the nearest existing ancestor of the path.
/// </summary>
internal static unsafe class VolumeIdentity
{
    /// <summary>The file system of each volume seen (by volume GUID path); a volume's file system does not change.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> FileSystems = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The volume rule of the write policy (<see cref="Core.Util.PathGuard"/>): why PoolWatch must not write where
    /// <paramref name="path"/> finally lands, or null when it may. ReFS volumes (every Dev Drive is one) are refused: ReFS
    /// keeps per-file and per-directory metadata in kernel pool (Reff, RefF, MS*), which is what PoolWatch measures, so its
    /// own files there would add to the growth it reports. A volume whose file system cannot be read is allowed: this rule
    /// keeps the measurement clean; it is not a security boundary.
    /// </summary>
    public static string? RefusalFor(string path)
    {
        if (Of(path) is not { } volume) return null;
        if (!FileSystems.TryGetValue(volume, out var fs))
        {
            if (FileSystemOf(volume + "\\") is not { } read) return null;
            fs = FileSystems.GetOrAdd(volume, read);
        }
        return FileSystemRefusal(fs);
    }

    /// <summary>Why a volume with this file system name ("ReFS", "NTFS") is refused, or null.</summary>
    public static string? FileSystemRefusal(string fileSystem) =>
        fileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase)
            ? "a ReFS volume (a Dev Drive or another ReFS volume, whose file metadata lives in the kernel pool PoolWatch measures)"
            : null;

    private static string? FileSystemOf(string volumeRoot)
    {
        char* fs = stackalloc char[261];
        return NativeMethods.GetVolumeInformationW(volumeRoot, null, 0, out _, out _, out _, fs, 261) ? new string(fs) : null;
    }

    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint OPEN_EXISTING = 3;
    private const int VOLUME_NAME_GUID = 1;

    public static string? Of(string path)
    {
        string? p;
        try { p = Path.GetFullPath(path); }
        catch { return null; }
        while (p is not null && !Directory.Exists(p) && !File.Exists(p)) p = Path.GetDirectoryName(p);
        if (p is null) return null;
        var h = NativeMethods.CreateFileW(p, 0, 7, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h == NativeMethods.InvalidHandle) return null;
        try
        {
            char* buf = stackalloc char[1024];
            int n = NativeMethods.GetFinalPathNameByHandleW(h, buf, 1024, VOLUME_NAME_GUID);
            if (n <= 0 || n >= 1024) return null;
            var final = new string(buf, 0, n); // \\?\Volume{guid}\rest
            int end = final.IndexOf('}');
            return end > 0 ? final[..(end + 1)] : null;
        }
        finally { NativeMethods.CloseHandle(h); }
    }

    /// <summary>True when the path is on a mapped network drive (DRIVE_REMOTE).</summary>
    public static bool IsRemoteDrive(string path)
    {
        string? root;
        string p = path.Trim();
        if (p.StartsWith(@"\\?\") || p.StartsWith(@"\\.\")) p = p[4..]; // \\?\Z:\x is still Z:
        try { root = Path.GetPathRoot(Path.GetFullPath(p)); }
        catch { return false; }
        if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':') return false;
        return NativeMethods.GetDriveTypeW(root[..2] + "\\") == 4; // DRIVE_REMOTE
    }
}
