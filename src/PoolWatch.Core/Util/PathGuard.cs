namespace PoolWatch.Core.Util;

/// <summary>
/// Enforces the write policy: PoolWatch writes only under its data root (%ProgramData%\PoolWatch on Windows, admin-only;
/// %LOCALAPPDATA%\PoolWatch on the Linux build) or an explicitly requested headless output directory, never on a volume
/// the caller's volume rule refuses, and never next to the exe. The Windows app's rule refuses ReFS volumes (every Dev
/// Drive is one): ReFS keeps per-file and per-directory metadata in the kernel pool PoolWatch measures, so its own files
/// there would add to the growth it reports. Every write and every delete goes through
/// <see cref="CheckReal"/>, which also refuses a path that runs through a link. There is no string-only check for a write
/// any more; <see cref="IsAllowed"/> is the policy test alone (for display and tests), never enough to write.
/// </summary>
public sealed class PathGuard
{
    private readonly List<string> _allowedRoots = new();
    private readonly string? _exeDir;
    private readonly Func<string, string?>? _refuseVolume;
    private readonly Func<string, bool>? _isRemote;
    private readonly bool _dataRootMustExist;
    private readonly Func<string, string?>? _identity;
    private readonly string? _pinned;
    private readonly Func<string, string, IDisposable?>? _holdDataRoot;
    private readonly object _holdLock = new();
    // The data root held open (see the constructor's holdDataRoot) from the first time that could be done; it stays open
    // for the life of the process.
    private IDisposable? _held;

    /// <param name="refuseVolume">
    /// Optional volume rule: for a path, why PoolWatch must not write on the volume it finally lands on (the rule follows
    /// junctions, mount points and subst drives), or null when it may. No drive letter is refused by name.
    /// </param>
    /// <param name="isRemote">Optional check for mapped network drives (Windows GetDriveType == DRIVE_REMOTE).</param>
    /// <param name="dataRootMustExist">
    /// The data root was made and checked by PoolWatch's start (the Windows app): a path in it is refused unless the data
    /// root is there as a folder, not a link, so no write makes it again (with the permissions its parent hands down) once
    /// it is gone.
    /// </param>
    /// <param name="dataRootIdentity">
    /// With <paramref name="dataRootMustExist"/>: which folder a path names (the Windows app: its volume and file ID), or
    /// null when that cannot be read. It is read once here, for the folder the start has just checked, and a path in the
    /// data root is refused whenever the folder at that name is not that one (another folder made there after it was moved
    /// or deleted, whoever made it), or when it could not be read here (<see cref="DataRootPinned"/>) or at that write;
    /// each refusal says which.
    /// </param>
    /// <param name="holdDataRoot">
    /// With <paramref name="dataRootIdentity"/>: holds the data root open so that no one can rename or delete it while the
    /// handle is open, and only when the folder held is the one checked (the identity given), or gives null when that
    /// cannot be done (the Windows app: <c>NoFollow.HoldFolder</c>). It is tried here and, until it is held, again at each
    /// check of a path in the data root, before the folder at its name is compared with the one checked: once held, that
    /// name names the folder checked for as long as this process runs, so a write right after its check is a write in it.
    /// </param>
    public PathGuard(string dataRoot, string? exeDir, Func<string, string?>? refuseVolume = null, Func<string, bool>? isRemote = null,
        bool dataRootMustExist = false, Func<string, string?>? dataRootIdentity = null, Func<string, string, IDisposable?>? holdDataRoot = null)
    {
        _isRemote = isRemote;
        _allowedRoots.Add(Normalize(dataRoot));
        _exeDir = exeDir is null ? null : Normalize(exeDir);
        _refuseVolume = refuseVolume;
        _dataRootMustExist = dataRootMustExist;
        _identity = dataRootMustExist ? dataRootIdentity : null;
        _pinned = _identity is null ? null : IdentityNow(_allowedRoots[0]);
        _holdDataRoot = _identity is null ? null : holdDataRoot;
        HoldDataRoot();
    }

    /// <summary>Whether the data root is held open against a rename or a delete (<c>holdDataRoot</c>).</summary>
    public bool DataRootHeld => Volatile.Read(ref _held) is not null;

    /// <summary>Holds the data root open when it is not yet held and it can be (<c>holdDataRoot</c>), the folder checked only.</summary>
    private void HoldDataRoot()
    {
        if (_holdDataRoot is null || _pinned is null || Volatile.Read(ref _held) is not null) return;
        lock (_holdLock)
        {
            if (_held is not null) return;
            try { Volatile.Write(ref _held, _holdDataRoot(_allowedRoots[0], _pinned)); }
            catch (Exception) { } // not held: tried again at the next check
        }
    }

    public IReadOnlyList<string> AllowedRoots => _allowedRoots;

    /// <summary>
    /// False only when the data root's identity was to be read (<c>dataRootIdentity</c>) and could not be: then no path in
    /// the data root is allowed.
    /// </summary>
    public bool DataRootPinned => _identity is null || _pinned is not null;

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
    /// Returns the path when PoolWatch may write or delete there, else throws: the string policy
    /// (<see cref="IsAllowed"/>), and the path, or any directory between its allowed root and it, must not be a reparse
    /// point (a symlink, junction or mount point). An elevated PoolWatch must never delete or write <em>through</em> a
    /// link that an unelevated process could have planted inside the data root, because the link could redirect the
    /// operation to a file or folder outside the root. The ONLY check for a write or a delete (call it
    /// right before each one); the string-only check that some writes used is gone. With dataRootMustExist, a path in the
    /// data root is refused while the data root is not there as a folder (it is gone, or a link or a file stands there),
    /// and, with dataRootIdentity, while the folder there is not the one this guard was made for.
    /// </summary>
    public string CheckReal(string path)
    {
        CheckPolicy(path);
        var n = Normalize(path);
        string? root = _allowedRoots.FirstOrDefault(r => IsUnder(n, r));
        if (root is null) throw new UnauthorizedAccessException("PoolWatch write policy: '" + path + "' is outside the allowed data roots");
        if (_dataRootMustExist && ReferenceEquals(root, _allowedRoots[0]) && !IsFolder(root))
            throw new UnauthorizedAccessException($"PoolWatch write policy: its data folder '{root}' is not there as a folder; only PoolWatch's start makes it");
        if (_identity is not null && ReferenceEquals(root, _allowedRoots[0]))
        {
            if (_pinned is null)
                throw new UnauthorizedAccessException($"PoolWatch write policy: which folder its data folder '{root}' is could not be read at its start");
            HoldDataRoot(); // before the look below: once held, the folder that look finds there stays there
            string? now = IdentityNow(root);
            if (now is null)
                throw new UnauthorizedAccessException($"PoolWatch write policy: which folder is at '{root}' could not be read now, so nothing is written there");
            if (now != _pinned)
                throw new UnauthorizedAccessException($"PoolWatch write policy: the folder at '{root}' is not the data folder this PoolWatch checked at its start");
        }
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

    /// <summary>
    /// True if the path is a folder itself (read from the entry, never followed): not a link, not a file, and there. A
    /// failure to read it counts as not a folder (fail closed).
    /// </summary>
    private static bool IsFolder(string p)
    {
        try
        {
            var a = File.GetAttributes(p);
            return a.HasFlag(FileAttributes.Directory) && !a.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Which folder the data root names now (<c>dataRootIdentity</c>); a failure to read it is null (fail closed).
    /// </summary>
    private string? IdentityNow(string root)
    {
        try { return _identity!(root); }
        catch (Exception) { return null; }
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
