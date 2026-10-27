namespace PoolWatch.Core.Util;

/// <summary>
/// Enforces the write policy: PoolWatch writes only under its data root (%ProgramData%\PoolWatch on Windows, admin-only;
/// %LOCALAPPDATA%\PoolWatch on the Linux build) or an explicitly requested headless output directory, never on a volume
/// the caller's volume rule refuses, and never next to the exe. The Windows app's rule refuses ReFS volumes (every Dev
/// Drive is one): ReFS keeps per-file and per-directory metadata in the kernel pool PoolWatch measures, so its own files
/// there would add to the growth it reports. Review round 2 (S3): every write and every delete goes through
/// <see cref="CheckReal"/>, which also refuses a path that runs through a link. There is no string-only check for a write
/// any more; <see cref="IsAllowed"/> is the policy test alone (for display and tests), never enough to write.
/// </summary>
public sealed class PathGuard
{
    private readonly List<string> _allowedRoots = new();
    private readonly string? _exeDir;
    private readonly Func<string, string?>? _refuseVolume;
    private readonly Func<string, bool>? _isRemote;

    /// <param name="refuseVolume">
    /// Optional volume rule: for a path, why PoolWatch must not write on the volume it finally lands on (the rule follows
    /// junctions, mount points and subst drives), or null when it may. No drive letter is refused by name.
    /// </param>
    /// <param name="isRemote">Optional check for mapped network drives (Windows GetDriveType == DRIVE_REMOTE).</param>
    public PathGuard(string dataRoot, string? exeDir, Func<string, string?>? refuseVolume = null, Func<string, bool>? isRemote = null)
    {
        _isRemote = isRemote;
        _allowedRoots.Add(Normalize(dataRoot));
        _exeDir = exeDir is null ? null : Normalize(exeDir);
        _refuseVolume = refuseVolume;
    }

    public IReadOnlyList<string> AllowedRoots => _allowedRoots;

    /// <summary>Adds an explicit output root (headless --out). Throws if it violates the drive / exe-dir rules.</summary>
    public void AllowRoot(string root)
    {
        CheckDrive(root);
        var n = Normalize(root);
        CheckDriveAndExe(n);
        _allowedRoots.Add(n);
    }

    public bool IsAllowed(string path, out string reason)
    {
        string n;
        try { CheckDrive(path); n = Normalize(path); CheckDriveAndExe(n); }
        catch (UnauthorizedAccessException ex) { reason = ex.Message; return false; }
        foreach (var root in _allowedRoots)
        {
            if (IsUnder(n, root)) { reason = ""; return true; }
        }
        reason = $"'{path}' is outside the allowed data roots";
        return false;
    }

    /// <summary>The string policy alone: throws unless the name is under an allowed root (and not on a refused volume / next to the exe).</summary>
    private void CheckPolicy(string path)
    {
        if (!IsAllowed(path, out var reason)) throw new UnauthorizedAccessException("PoolWatch write policy: " + reason);
    }

    /// <summary>
    /// S3 (review): returns the path when PoolWatch may write or delete there, else throws: the string policy
    /// (<see cref="IsAllowed"/>), and the path, or any directory between its allowed root and it, must not be a reparse
    /// point (a symlink, junction or mount point). An elevated PoolWatch must never delete or write <em>through</em> a
    /// link that an unelevated process could have planted inside the data root, because the link could redirect the
    /// operation to a file or folder outside the root. Review round 2: the ONLY check for a write or a delete (call it
    /// right before each one); the string-only check that some writes used is gone.
    /// </summary>
    public string CheckReal(string path)
    {
        CheckPolicy(path);
        var n = Normalize(path);
        string? root = _allowedRoots.FirstOrDefault(r => IsUnder(n, r));
        if (root is null) throw new UnauthorizedAccessException("PoolWatch write policy: '" + path + "' is outside the allowed data roots");
        // Walk each component from just below the (admin-owned) root down to the leaf; refuse any reparse point.
        // Sliced at the root's length (not +1): a drive-root output folder ("D:\") ends in the separator already.
        var rel = n.Length > root.Length ? n[root.Length..] : "";
        string cur = root;
        foreach (var part in rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part.Length == 0) continue;
            cur = Path.Combine(cur, part);
            if (IsReparsePoint(cur))
                throw new UnauthorizedAccessException($"PoolWatch write policy: '{cur}' is a link; PoolWatch never writes or deletes through links");
        }
        return path;
    }

    /// <summary>
    /// For a path OUTSIDE the data root that the elevated app must touch (an older build's helper files in Program Files):
    /// the first component from just below <paramref name="root"/> down to <paramref name="path"/> that is a reparse point,
    /// or null when there is none. The path must be under the root (else the path itself is returned: refuse).
    /// </summary>
    public static string? FirstLinkBelow(string root, string path)
    {
        var r = Normalize(root);
        var n = Normalize(path);
        if (!IsUnder(n, r) || n.Length == r.Length) return path;
        string cur = r;
        foreach (var part in n[r.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part.Length == 0) continue;
            cur = Path.Combine(cur, part);
            if (IsReparsePoint(cur)) return cur;
        }
        return null;
    }

    /// <summary>
    /// True if the path is a reparse point of any kind: a symlink, a directory junction (what an unelevated process can
    /// plant without any privilege), a mount point, or any other tag. The attributes are read from the entry itself
    /// (never followed), so a dangling link whose target is gone is still a link. A path that does not exist is not a
    /// link. Any other failure to read the attributes counts as a link (fail closed): the guard must not wave through a
    /// path it could not inspect. Cross-platform: on Unix a symlink carries <see cref="FileAttributes.ReparsePoint"/> too.
    /// </summary>
    internal static bool IsReparsePoint(string p)
    {
        FileAttributes a;
        try { a = File.GetAttributes(p); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (Exception) { return true; }
        return a.HasFlag(FileAttributes.ReparsePoint);
    }

    private void CheckDrive(string p)
    {
        p = p.Trim();
        // Network and raw volume paths could alias any drive: refused outright.
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) || p.StartsWith(@"\\?\Volume", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith(@"\\.\Volume", StringComparison.OrdinalIgnoreCase) || p.StartsWith(@"\\?\GLOBALROOT", StringComparison.OrdinalIgnoreCase) ||
            (p.StartsWith(@"\\") && !p.StartsWith(@"\\?\") && !p.StartsWith(@"\\.\")) || p.StartsWith("//"))
            throw new UnauthorizedAccessException($"'{p}' is a network or device path; PoolWatch writes only to local drive paths");
        if (_isRemote is not null && _isRemote(p))
            throw new UnauthorizedAccessException($"'{p}' is on a network drive; PoolWatch writes only to local drive paths");
        if (_refuseVolume is not null && _refuseVolume(p) is { } why)
            throw new UnauthorizedAccessException($"'{p}' is on {why}; PoolWatch never writes there");
    }

    private void CheckDriveAndExe(string n)
    {
        CheckDrive(n);
        if (_exeDir is not null && IsUnder(n, _exeDir) && !_allowedRoots.Any(r => IsUnder(n, r) && r.Length > _exeDir.Length))
            throw new UnauthorizedAccessException($"'{n}' is next to the executable; PoolWatch never writes there");
    }

    private static bool IsUnder(string path, string root)
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (path.Equals(root, cmp)) return true;
        var r = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(r, cmp);
    }

    private static string Normalize(string p)
    {
        var full = Path.GetFullPath(p);
        return full.Length > 3 ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }
}
