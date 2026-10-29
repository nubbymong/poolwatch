using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace PoolWatch.Engine;

/// <summary>
/// S3 (review round 2): moves the old per-user data folder (%LOCALAPPDATA%\PoolWatch, writable by the unelevated user)
/// into the admin-only data root, once, on a background thread after start-up. The elevated process treats the old folder
/// as hostile: an unelevated program could have planted links in it to make an administrator copy, overwrite or delete
/// something else.
/// <list type="bullet">
/// <item>Refused outright (nothing copied, nothing deleted) when the old root, or any entry anywhere under it, is a
/// reparse point (junction, symlink, mount point) or a file with more than one name (a hard link), when the old root is
/// reached through a link above it, or when it holds more than <see cref="MaxEntries"/> entries. Nothing is followed:
/// every entry is opened with FILE_FLAG_OPEN_REPARSE_POINT and judged by what the handle says.</item>
/// <item>While it runs, every directory of the old tree is held open without FILE_SHARE_DELETE, so none can be renamed,
/// removed or swapped for a junction.</item>
/// <item>Only PoolWatch's own data is copied (<see cref="WhyNotMoved"/>): the logs, data\, incidents\,
/// reports\, exports\ and boots\ files of PoolWatch's own types. Never cache\ (rebuilt), symbols\ (an elevated PoolWatch
/// parses PDBs: never from a user-writable source), requests\ or service-changes.json (Restore acts on it as an
/// administrator: it is never taken from a folder an unelevated program could write). settings.json is taken only by
/// <see cref="MigrateSettings"/> at start, parsed and checked, never copied as it is. Files over
/// <see cref="MaxFileBytes"/> (traces) are left with a note.</item>
/// <item>Each file is read through a handle whose file ID matches the one scanned, copied to a temporary name under the
/// admin-only root, hashed again and only then moved into place. Existing files there are never overwritten.</item>
/// <item>After a verified copy the old file is deleted through a handle (delete-on-close), again only when its file ID
/// is the scanned one and it has one name. Emptied folders go the same way. A one-line README is left in the old folder
/// (created new, never through a link), and a marker in the new root stops a repeat.</item>
/// </list>
/// </summary>
internal sealed class LegacyMigration
{
    /// <summary>Files larger than this (WPR traces) are left in the old folder with a note.</summary>
    public const long MaxFileBytes = 50L * 1024 * 1024;
    /// <summary>A tree larger than this is not PoolWatch's (or is an attack on the scan): refused.</summary>
    public const int MaxEntries = 100_000;
    public const string MarkerName = ".migrated";
    public const string ReadmeName = "README - PoolWatch data moved.txt";
    /// <summary>Review round 3: runs that did not finish are retried at later starts, at most this many runs in all.</summary>
    public const int MaxAttempts = 5;
    public const string AttemptsName = ".migration-attempts";
    /// <summary>The old settings.json is read only up to this size (it is a few KB).</summary>
    public const int MaxSettingsBytes = 1024 * 1024;

    private readonly string _legacy, _dataRoot;
    private readonly Func<string, string> _checkWrite;
    private readonly Action<string> _log;
    private readonly Func<DateTime> _now;

    /// <param name="checkWrite">The write policy for the new root (PathGuard.CheckReal): returns the path or throws.</param>
    public LegacyMigration(string legacyRoot, string dataRoot, Func<string, string> checkWrite, Action<string> log, Func<DateTime>? now = null)
    {
        _legacy = Path.GetFullPath(legacyRoot).TrimEnd('\\');
        _dataRoot = Path.GetFullPath(dataRoot).TrimEnd('\\');
        _checkWrite = checkWrite;
        _log = log;
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>What happened. <see cref="Refused"/> set: nothing was copied or deleted.</summary>
    public sealed class Result
    {
        public string? Refused { get; set; }
        public bool NothingToDo { get; set; }
        public int Copied { get; set; }
        public int Deleted { get; set; }
        /// <summary>Files left in the old folder for good, each with why ("over 50 MB", "not PoolWatch's data", ...).</summary>
        public List<string> Left { get; } = new();
        /// <summary>What did not work this time (a file in use, changed meanwhile, ...): the run is retried at a later start.</summary>
        public List<string> Failed { get; } = new();
        /// <summary>Which run this was (1..<see cref="MaxAttempts"/>); 0 when nothing was attempted.</summary>
        public int Attempt { get; set; }
        /// <summary>The run finished: nothing failed (the marker is written).</summary>
        public bool Complete => Refused is null && Failed.Count == 0 && !NothingToDo;
        /// <summary>Not complete after the last allowed run: the marker is written anyway and the old folder is left as it is.</summary>
        public bool GaveUp { get; set; }

        public string Summary() => (Refused is { } why ? "old data folder not migrated: " + why
            : NothingToDo ? "no old data folder to migrate"
            : $"old data folder migrated{(Complete ? "" : " in part")}: {Copied} file(s) copied and checked, {Deleted} removed from the old folder" +
              (Left.Count > 0 ? $"; {Left.Count} left there ({string.Join("; ", Left.Take(20))}{(Left.Count > 20 ? "; ..." : "")})" : "") +
              (Failed.Count > 0 ? $"; {Failed.Count} failed ({string.Join("; ", Failed.Take(10))}{(Failed.Count > 10 ? "; ..." : "")})" : "")) +
            (Attempt == 0 || Complete ? "" : GaveUp ? $"; gave up after {Attempt} attempts (the rest stays in the old folder)" : $"; attempt {Attempt} of {MaxAttempts}, retried at the next start");
    }

    /// <summary>
    /// Pure: null when a file of the old folder (path relative to it, '\'-separated) is PoolWatch's own data and is moved;
    /// else why it stays.
    /// </summary>
    public static string? WhyNotMoved(string rel, long length)
    {
        var parts = rel.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "not PoolWatch's data";
        string first = parts[0].ToLowerInvariant();
        if (parts.Length == 1)
        {
            if (first == "service-changes.json") return "service records are never taken from a folder an unelevated program could write; restore those services in services.msc";
            // Only the checked settings migration at start takes settings.json (MigrateSettings: parsed, clamped, and without
            // service profiles, which PoolWatch applies as an administrator). A copy as it is would carry them over.
            if (first == "settings.json") return "settings are taken over only by the checked settings migration at start; set them again in Settings";
            if (first is not ("poolwatch.log" or "poolwatch.log.1" or "since-boot.md" or "since-boot.json" or "boots.md"))
                return "not PoolWatch's data";
        }
        else
        {
            if (first is "cache") return "a cache (rebuilt)";
            if (first is "symbols") return "symbols are never taken from a user-writable folder";
            if (first is not ("data" or "incidents" or "reports" or "exports" or "boots")) return "not PoolWatch's data";
            if (parts.Length > 3 || parts.Any(p => p.Equals("wpr-temp", StringComparison.OrdinalIgnoreCase))) return "not PoolWatch's data";
            string ext = Path.GetExtension(rel).ToLowerInvariant();
            if (ext is not (".csv" or ".json" or ".jsonl" or ".md" or ".txt" or ".png" or ".etl" or ".zip")) return "not PoolWatch's data";
        }
        if (length > MaxFileBytes) return $"over {MaxFileBytes >> 20} MB";
        return null;
    }

    private sealed record Entry(string Full, string Rel, bool IsDir, NoFollow.FileId Id, long Length, DateTime LastWriteUtc);

    /// <summary>Runs the migration (call on a background thread). Never throws; the result says what happened.</summary>
    public Result Run()
    {
        var r = new Result();
        if (!OperatingSystem.IsWindows()) { r.NothingToDo = true; return r; }
        var held = new Dictionary<string, SafeFileHandle>(StringComparer.OrdinalIgnoreCase);
        SafeFileHandle? root = null;
        try
        {
            if (File.Exists(Path.Combine(_dataRoot, MarkerName))) { r.NothingToDo = true; return r; }
            CleanTemporaryCopies(); // a run that was cut short (PoolWatch ended mid-copy) leaves *.migrating behind
            root = Open(_legacy, dir: true, FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE);
            if (root.IsInvalid)
            {
                int e = Marshal.GetLastWin32Error();
                if (e is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND) { r.NothingToDo = true; return r; }
                r.Attempt = NextAttempt();
                r.Refused = $"the old folder could not be opened (error {e})";
                return Finish(r, root);
            }
            r.Attempt = NextAttempt();
            var rootInfo = Query(root);
            if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0) { r.Refused = $"{_legacy} is a link"; return Finish(r, root); }
            if ((rootInfo.Attributes & FileAttributes.Directory) == 0) { r.Refused = $"{_legacy} is not a folder"; return Finish(r, root); }
            if (!SamePath(FinalPath(root), _legacy)) { r.Refused = $"{_legacy} is reached through a link ({FinalPath(root)})"; return Finish(r, root); }

            if (Scan(held, r) is not { } entries) return Finish(r, root);

            var copied = new List<Entry>();
            foreach (var e in entries.Where(x => !x.IsDir))
            {
                if (WhyNotMoved(e.Rel, e.Length) is { } why) { r.Left.Add($"{e.Rel} ({why})"); continue; }
                try
                {
                    // Review round 3: a file that could not be copied THIS time (in use, changed meanwhile, read back wrong)
                    // fails the run, which is retried; one that will never be copied (too large, another file already there) is left.
                    var (notCopied, retry) = CopyVerified(e);
                    if (notCopied is null) { copied.Add(e); r.Copied++; }
                    else if (retry) r.Failed.Add($"{e.Rel}: {notCopied}");
                    else r.Left.Add($"{e.Rel} ({notCopied})");
                }
                catch (Exception ex) { r.Failed.Add($"{e.Rel}: {ex.Message}"); }
            }
            // The new folders keep the old ones' times (retention ages daily folders and incidents by them).
            foreach (var d in entries.Where(x => x.IsDir))
                try { var dest = Path.Combine(_dataRoot, d.Rel); if (Directory.Exists(dest)) Directory.SetLastWriteTimeUtc(_checkWrite(dest), d.LastWriteUtc); } catch { }

            foreach (var e in copied)
                if (DeleteIfSame(e, dir: false) is { } err) r.Failed.Add($"{e.Rel}: copied, but not removed from the old folder ({err})");
                else r.Deleted++;
            // Emptied folders, deepest first; each one's own hold is released just before (a non-empty one stays).
            foreach (var d in entries.Where(x => x.IsDir).OrderByDescending(x => x.Rel.Count(c => c == '\\')))
            {
                if (held.Remove(d.Rel, out var h)) h.Dispose();
                DeleteIfSame(d, dir: true);
            }
            return Finish(r, root);
        }
        catch (Exception ex)
        {
            r.Failed.Add(ex.Message);
            try { return Finish(r, root); } catch { return r; }
        }
        finally
        {
            foreach (var h in held.Values) h.Dispose();
            root?.Dispose();
            try { CleanTemporaryCopies(); } catch { }
        }
    }

    /// <summary>
    /// Review round 3: the marker (no more runs) only when every file succeeded, or after the last allowed run; otherwise
    /// the next start tries again. The README goes into the old folder only then (it says what stayed for good).
    /// </summary>
    private Result Finish(Result r, SafeFileHandle? root)
    {
        if (r.Attempt == 0) return r; // nothing was attempted (no old folder, or it could not be opened at all)
        if (!r.Complete && r.Attempt < MaxAttempts) return r;
        r.GaveUp = !r.Complete;
        if (root is { IsInvalid: false } && r.Refused is null) WriteReadme(root, r);
        File.WriteAllText(_checkWrite(Path.Combine(_dataRoot, MarkerName)), $"{_now():o}\n{r.Summary()}\n");
        try { string attempts = Path.Combine(_dataRoot, AttemptsName); if (File.Exists(attempts)) File.Delete(_checkWrite(attempts)); } catch { }
        return r;
    }

    /// <summary>Counts this run (kept in the admin-only data root) and returns its number.</summary>
    private int NextAttempt()
    {
        string file = _checkWrite(Path.Combine(_dataRoot, AttemptsName));
        int n = 0;
        try { if (File.Exists(file)) int.TryParse(File.ReadAllText(file).Trim(), out n); } catch { }
        n = Math.Max(0, n) + 1;
        File.WriteAllText(file, n.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return n;
    }

    /// <summary>Deletes the temporary copies (*.migrating) a run left in the data root (never through a link).</summary>
    private void CleanTemporaryCopies()
    {
        if (!Directory.Exists(_dataRoot)) return;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true, MaxRecursionDepth = 4 };
        foreach (var f in Directory.EnumerateFiles(_dataRoot, "*.migrating", options))
            try { File.Delete(_checkWrite(f)); } catch { }
    }

    /// <summary>
    /// Review round 3: settings.json first, synchronously, BEFORE the settings are read, so the first session after the
    /// move is not on defaults. Link-safe like the rest (the old root and the file are judged through handles; a link, a
    /// hard link or a file over 1 MB is refused), and the content is never carried over as it is: it is parsed with the
    /// same migrations, validation and clamping as any settings load and written back in PoolWatch's own form
    /// (<paramref name="normalize"/>; unknown fields are dropped). Only when the new root has no settings.json yet; the old
    /// file is then removed (through its handle). Returns a line for the log, or null when there was nothing to do.
    /// </summary>
    public string? MigrateSettings(Func<string, string> normalize)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            string dest = Path.Combine(_dataRoot, "settings.json");
            if (File.Exists(Path.Combine(_dataRoot, MarkerName)) || File.Exists(dest)) return null;
            using (var root = Open(_legacy, dir: true, FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE))
            {
                if (root.IsInvalid) return null;
                var rq = Query(root);
                if ((rq.Attributes & FileAttributes.ReparsePoint) != 0 || (rq.Attributes & FileAttributes.Directory) == 0 || !SamePath(FinalPath(root), _legacy))
                    return "old settings.json not migrated: the old folder is a link";
                string src = Path.Combine(_legacy, "settings.json");
                Entry e;
                byte[] bytes;
                using (var h = Open(src, dir: false, GENERIC_READ | FILE_READ_ATTRIBUTES, FILE_SHARE_READ))
                {
                    if (h.IsInvalid) return null; // none there, or in use (the next start tries again until the move is done)
                    var q = Query(h);
                    if ((q.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || q.Links != 1)
                        return "old settings.json not migrated: it is a link or has more than one name";
                    if (q.Length > MaxSettingsBytes) return "old settings.json not migrated: over 1 MB";
                    using var fs = new FileStream(h, FileAccess.Read, 1);
                    bytes = new byte[MaxSettingsBytes + 1];
                    int n = fs.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
                    if (n > MaxSettingsBytes) return "old settings.json not migrated: over 1 MB";
                    Array.Resize(ref bytes, n);
                    e = new Entry(src, "settings.json", false, q.Id, q.Length, q.LastWriteUtc);
                }
                string json;
                try { json = normalize(System.Text.Encoding.UTF8.GetString(bytes)); }
                catch (Exception ex) { return "old settings.json not migrated: it is not valid (" + ex.Message + ")"; }
                string tmp = _checkWrite(dest + ".migrating");
                File.WriteAllText(tmp, json);
                File.Move(tmp, _checkWrite(dest), overwrite: false);
                string? notRemoved = DeleteIfSame(e, dir: false);
                return "old settings.json migrated (read as any settings load: checked and clamped)" +
                       (notRemoved is null ? "" : $"; it could not be removed from the old folder ({notRemoved})");
            }
        }
        catch (Exception ex) { return "old settings.json not migrated: " + ex.Message; }
    }

    /// <summary>The whole old tree, every entry judged without following anything; null (and Refused set) on a link, a hard link or a huge tree.</summary>
    private List<Entry>? Scan(Dictionary<string, SafeFileHandle> held, Result r)
    {
        var entries = new List<Entry>();
        var todo = new Stack<(string full, string rel)>();
        todo.Push((_legacy, ""));
        var options = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = false, ReturnSpecialDirectories = false };
        while (todo.Count > 0)
        {
            var (dirFull, dirRel) = todo.Pop();
            foreach (var fsi in new DirectoryInfo(dirFull).EnumerateFileSystemInfos("*", options))
            {
                string rel = dirRel.Length == 0 ? fsi.Name : dirRel + "\\" + fsi.Name;
                if (entries.Count >= MaxEntries) { r.Refused = $"more than {MaxEntries:N0} entries in the old folder"; return null; }
                if ((fsi.Attributes & FileAttributes.ReparsePoint) != 0) { r.Refused = $"{rel} is a link (junction, symlink or mount point)"; return null; }
                bool isDir = (fsi.Attributes & FileAttributes.Directory) != 0;
                // Directories are held without FILE_SHARE_DELETE for the rest of the run: none can be renamed or swapped.
                var h = Open(fsi.FullName, isDir, FILE_READ_ATTRIBUTES, isDir ? FILE_SHARE_READ | FILE_SHARE_WRITE : FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE);
                if (h.IsInvalid) { r.Refused = $"{rel} could not be opened to check it (error {Marshal.GetLastWin32Error()})"; return null; }
                var q = Query(h);
                if ((q.Attributes & FileAttributes.ReparsePoint) != 0) { h.Dispose(); r.Refused = $"{rel} is a link"; return null; }
                if (!isDir && q.Links > 1) { h.Dispose(); r.Refused = $"{rel} is a hard link ({q.Links} names)"; return null; }
                if (((q.Attributes & FileAttributes.Directory) != 0) != isDir) { h.Dispose(); r.Refused = $"{rel} changed while it was checked"; return null; }
                entries.Add(new Entry(fsi.FullName, rel, isDir, q.Id, q.Length, q.LastWriteUtc));
                if (isDir) { held[rel] = h; todo.Push((fsi.FullName, rel)); }
                else h.Dispose();
            }
        }
        return entries;
    }

    /// <summary>
    /// Copies one file through a handle that is the scanned file. Reason null: copied and checked. Otherwise why not, and
    /// whether a later run may succeed (<c>retry</c>: in use, changed meanwhile, read back wrong) or never will (too large,
    /// another file of that name already in the new folder).
    /// </summary>
    private (string? reason, bool retry) CopyVerified(Entry e)
    {
        string dest = Path.Combine(_dataRoot, e.Rel);
        byte[] srcHash;
        using (var src = Open(e.Full, dir: false, GENERIC_READ | FILE_READ_ATTRIBUTES, FILE_SHARE_READ))
        {
            if (src.IsInvalid) return ($"could not be opened (error {Marshal.GetLastWin32Error()})", true);
            var q = Query(src);
            if ((q.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || q.Links != 1 || q.Id != e.Id)
                return ("changed since it was checked", true);
            if (q.Length > MaxFileBytes) return ($"over {MaxFileBytes >> 20} MB", false);
            if (File.Exists(dest))
            {
                // Already there (an earlier, interrupted run): it counts as copied only when it is the same content.
                using var existing = new FileStream(src, FileAccess.Read, 1);
                return Same(HashOf(existing), dest) ? (null, false) : ("a different file of that name is already in the new folder", false);
            }
            Directory.CreateDirectory(_checkWrite(Path.GetDirectoryName(dest)!));
            string tmp = _checkWrite(dest + ".migrating");
            using var fs = new FileStream(src, FileAccess.Read, 1);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buf = new byte[81920];
                long total = 0;
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    total += n;
                    if (total > MaxFileBytes) { dst.Close(); File.Delete(tmp); return ($"over {MaxFileBytes >> 20} MB", false); }
                    hash.AppendData(buf, 0, n);
                    dst.Write(buf, 0, n);
                }
            }
            srcHash = hash.GetHashAndReset();
            if (!Same(srcHash, tmp)) { File.Delete(tmp); return ("the copy did not read back the same", true); }
            File.Move(tmp, _checkWrite(dest), overwrite: false);
        }
        try { File.SetLastWriteTimeUtc(dest, e.LastWriteUtc); } catch { }
        return (null, false);
    }

    private static bool Same(byte[] hash, string file)
    {
        using var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        return HashOf(f).AsSpan().SequenceEqual(hash);
    }

    private static byte[] HashOf(Stream s)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(s);
    }

    /// <summary>Deletes an old entry through a handle, only when it is still the scanned one (same file ID, one name, no link). Null when deleted.</summary>
    private static string? DeleteIfSame(Entry e, bool dir)
    {
        using var h = Open(e.Full, dir, DELETE | FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE);
        if (h.IsInvalid) return $"error {Marshal.GetLastWin32Error()}";
        var q = Query(h);
        if ((q.Attributes & FileAttributes.ReparsePoint) != 0 || q.Id != e.Id || (!dir && q.Links != 1)) return "it changed since it was copied";
        if (!NoFollow.DeleteOnClose(h)) return $"error {Marshal.GetLastWin32Error()}"; // a folder that is not empty stays
        return null;
    }

    /// <summary>The one-line README in the old folder: created new (never over an existing name, never through a link).</summary>
    private void WriteReadme(SafeFileHandle root, Result r)
    {
        // The old root is held open (it cannot be renamed); it must still be where it was checked.
        if (!SamePath(FinalPath(root), _legacy)) { r.Failed.Add("README not written: the old folder moved"); return; }
        using var h = NoFollow.CreateNew(Path.Combine(_legacy, ReadmeName));
        if (h.IsInvalid) return; // already there (an earlier run) or refused: nothing else to do
        string line = $"PoolWatch moved its data to {_dataRoot} (administrators only) on {_now().ToLocalTime():yyyy-MM-dd}; " +
                      $"anything still here was not moved (over {MaxFileBytes >> 20} MB, or not PoolWatch's own data).\r\n";
        using var fs = new FileStream(h, FileAccess.Write, 1);
        fs.Write(System.Text.Encoding.UTF8.GetBytes(line));
    }

    // ------------------------------------------------------------------ native (shared, never following a link: NoFollow)

    private static bool SamePath(string? finalPath, string expected) => NoFollow.SamePath(finalPath, expected);
    private static SafeFileHandle Open(string path, bool dir, uint access, uint share) => NoFollow.Open(path, dir, access, share);
    private static NoFollow.Facts Query(SafeFileHandle h) => NoFollow.Query(h);
    private static string? FinalPath(SafeFileHandle h) => NoFollow.FinalPath(h);

    private const uint GENERIC_READ = NoFollow.GENERIC_READ, DELETE = NoFollow.DELETE, FILE_READ_ATTRIBUTES = NoFollow.FILE_READ_ATTRIBUTES;
    private const uint FILE_SHARE_READ = NoFollow.FILE_SHARE_READ, FILE_SHARE_WRITE = NoFollow.FILE_SHARE_WRITE, FILE_SHARE_DELETE = NoFollow.FILE_SHARE_DELETE;
    private const int ERROR_FILE_NOT_FOUND = NoFollow.ERROR_FILE_NOT_FOUND, ERROR_PATH_NOT_FOUND = NoFollow.ERROR_PATH_NOT_FOUND;
}
