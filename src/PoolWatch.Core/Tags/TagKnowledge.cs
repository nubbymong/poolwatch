using System.Collections.Concurrent;
using System.Text.Json;

namespace PoolWatch.Core.Tags;

/// <summary>
/// Resolves a tag's owner and description (DESIGN.md section 5): pooltag.txt (read at runtime, never shipped),
/// then the cached result of scanning driver binaries for the tag literal, then the built-in table.
/// </summary>
public sealed class TagKnowledge
{
    /// <summary>
    /// L4: the most of pooltag.txt that is read. Microsoft's file is well under 2 MB; a path setting that points at anything
    /// bigger is not a pooltag.txt, and the elevated app must not read or parse an unbounded file.
    /// </summary>
    public const long MaxPoolTagTxtBytes = 8L * 1024 * 1024;

    private readonly Dictionary<PoolTag, TagInfo> _poolTagTxt;
    private readonly ConcurrentDictionary<PoolTag, string[]> _driverHits = new();
    private readonly object _saveLock = new();
    private readonly string? _cacheFile;
    private readonly Func<string, string> _checkWrite;

    /// <param name="checkWrite">The write policy for the cache file (the app passes PathGuard.CheckReal: review round 2, S3).</param>
    /// <param name="maxPoolTagTxtBytes">L4: pooltag.txt is not used when it is larger than this.</param>
    public TagKnowledge(string? poolTagTxtPath, string? cacheFile, Func<string, string>? checkWrite = null, long maxPoolTagTxtBytes = MaxPoolTagTxtBytes)
    {
        try { _poolTagTxt = poolTagTxtPath is not null && File.Exists(poolTagTxtPath) ? PoolTagTxt.ReadBounded(poolTagTxtPath, maxPoolTagTxtBytes) : new(); }
        catch (Exception) { _poolTagTxt = new(); } // unreadable: fall back to the driver scan and the built-in table
        PoolTagTxtLoaded = _poolTagTxt.Count > 0;
        _cacheFile = cacheFile;
        _checkWrite = checkWrite ?? (p => p);
        LoadCache();
    }

    public bool PoolTagTxtLoaded { get; }

    /// <summary>Tags whose owner is not known from pooltag.txt and that have not been scanned yet.</summary>
    public bool NeedsDriverScan(PoolTag tag) => !_poolTagTxt.ContainsKey(tag) && !_driverHits.ContainsKey(tag);

    public TagInfo Lookup(PoolTag tag)
    {
        _poolTagTxt.TryGetValue(tag, out var txt);
        BuiltinTags.Table.TryGetValue(tag, out var builtin);
        _driverHits.TryGetValue(tag, out var drivers);
        if (txt is not null) return txt;
        if (drivers is { Length: > 0 })
            return new TagInfo(string.Join(", ", drivers), builtin?.Description ?? "(found in driver binary)", "driver scan");
        if (builtin is not null) return builtin;
        return new TagInfo("unknown", "", "none");
    }

    /// <summary>Records driver-scan results (from <see cref="DriverTagScanner"/>) and persists the cache.</summary>
    public void AddDriverHits(IReadOnlyDictionary<PoolTag, string[]> hits)
    {
        foreach (var (tag, drivers) in hits) _driverHits[tag] = drivers;
        lock (_saveLock) SaveCache();
    }

    /// <summary>True when the tag belongs to a file-system stack component (used for the "file-system tag" label).</summary>
    public bool IsFileSystemTag(PoolTag tag)
    {
        var owner = Lookup(tag).Owner.ToLowerInvariant();
        return owner.Contains("ntfs") || owner.Contains("refs") || owner.Contains("fltmgr") || owner.Contains("fslib") ||
               owner.Contains("nt!mm") || owner.Contains("nt!cc") || owner.Contains("fastfat") || owner.Contains("exfat") ||
               owner.Contains("wof") || owner.Contains("wcifs") || owner.Contains("bindflt") || owner.Contains("minifilter") ||
               tag == PoolTag.Parse("File") || tag == PoolTag.Parse("IoNm");
    }

    private void LoadCache()
    {
        if (_cacheFile is null || !File.Exists(_cacheFile)) return;
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(_cacheFile));
            foreach (var (hex, drivers) in raw ?? new())
                if (uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v)) _driverHits[new PoolTag(v)] = drivers;
        }
        catch { /* a corrupt cache is rebuilt */ }
    }

    private void SaveCache()
    {
        if (_cacheFile is null) return;
        try
        {
            Directory.CreateDirectory(_checkWrite(Path.GetDirectoryName(_cacheFile)!));
            var raw = _driverHits.ToDictionary(kv => kv.Key.Raw.ToString("x8"), kv => kv.Value);
            File.WriteAllText(_checkWrite(_cacheFile + ".tmp"), JsonSerializer.Serialize(raw));
            File.Move(_checkWrite(_cacheFile + ".tmp"), _checkWrite(_cacheFile), overwrite: true);
        }
        catch { }
    }
}

/// <summary>Parser for Microsoft's pooltag.txt ("Tag - driver - description", // comments).</summary>
public static class PoolTagTxt
{
    /// <summary>
    /// L4: reads at most <paramref name="maxBytes"/> of the file into one buffer and parses that. A file that is, or grows
    /// while it is read, larger than that is not used (empty result), and so is anything that is not a plain file: a link
    /// (a symbolic link could lead to a device or a pipe) or a stream that cannot seek.
    /// </summary>
    public static Dictionary<PoolTag, TagInfo> ReadBounded(string path, long maxBytes)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return new();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        if (!fs.CanSeek) return new();
        long length = fs.Length;
        if (length > maxBytes || length >= int.MaxValue) return new();
        var buf = new byte[length + 1]; // one byte more than the file: a file that grows while it is read is not used
        int total = 0, n;
        while (total < buf.Length && (n = fs.Read(buf, total, buf.Length - total)) > 0) total += n;
        if (total > length) return new();
        using var reader = new StreamReader(new MemoryStream(buf, 0, total, writable: false), detectEncodingFromByteOrderMarks: true);
        return Parse(Lines(reader));

        static IEnumerable<string> Lines(StreamReader r)
        {
            while (r.ReadLine() is { } line) yield return line;
        }
    }

    public static Dictionary<PoolTag, TagInfo> Parse(IEnumerable<string> lines)
    {
        var result = new Dictionary<PoolTag, TagInfo>();
        foreach (var raw in lines)
        {
            if (raw.Length < 5 || raw.StartsWith("//") || raw.StartsWith("rem", StringComparison.OrdinalIgnoreCase)) continue;
            int dash = raw.IndexOf(" - ", StringComparison.Ordinal);
            if (dash is < 1 or > 6) continue;
            var tagText = raw[..dash].TrimStart();
            if (tagText.Length > 4) tagText = tagText.TrimEnd();
            if (!PoolTag.TryParse(tagText, out var tag)) continue;
            var rest = raw[(dash + 3)..];
            int dash2 = rest.IndexOf(" - ", StringComparison.Ordinal);
            string owner = (dash2 >= 0 ? rest[..dash2] : rest).Trim();
            string desc = dash2 >= 0 ? rest[(dash2 + 3)..].Trim() : "";
            if (result.TryGetValue(tag, out var existing))
            {
                if (!existing.Owner.Split(", ").Contains(owner)) result[tag] = existing with { Owner = existing.Owner + ", " + owner };
            }
            else result[tag] = new TagInfo(owner, desc, "pooltag.txt");
        }
        return result;
    }
}

/// <summary>
/// The classic <c>findstr /m /l</c> method: find driver binaries that contain the 4 tag bytes. Bounded to an
/// explicit list of files (loaded drivers + %SystemRoot%\System32\drivers\*.sys) and to a batch of tags per pass,
/// each file read once.
/// </summary>
public static class DriverTagScanner
{
    public static Dictionary<PoolTag, string[]> Scan(IReadOnlyCollection<PoolTag> tags, IEnumerable<string> driverFiles, long maxFileBytes = 512L * 1024 * 1024, CancellationToken ct = default)
    {
        var hits = tags.ToDictionary(t => t, _ => new List<string>());
        var patterns = tags.Select(t => (tag: t, bytes: t.GetBytes())).ToList();
        // Streamed in 1 MB chunks with a 3-byte overlap, so large drivers never land whole on the heap.
        var buf = new byte[(1 << 20) + 3];
        foreach (var file in driverFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var found = new HashSet<PoolTag>();
            try
            {
                var fi = new FileInfo(file);
                if (!fi.Exists || fi.Length > maxFileBytes) continue;
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
                int carry = 0;
                while (found.Count < patterns.Count)
                {
                    int n = fs.Read(buf, carry, buf.Length - carry);
                    if (n <= 0) break;
                    var span = buf.AsSpan(0, carry + n);
                    foreach (var (tag, bytes) in patterns)
                        if (!found.Contains(tag) && span.IndexOf(bytes) >= 0) found.Add(tag);
                    carry = Math.Min(3, span.Length);
                    span[^carry..].CopyTo(buf);
                }
            }
            catch { continue; }
            foreach (var tag in found) hits[tag].Add(Path.GetFileName(file).ToLowerInvariant());
        }
        // Too many hits means the literal is common (e.g. "File"): not useful as attribution.
        return hits.ToDictionary(kv => kv.Key, kv => kv.Value.Count is > 0 and <= 5 ? kv.Value.ToArray() : []);
    }
}
