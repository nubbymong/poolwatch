namespace PoolWatch.Core.Spawn;

/// <summary>File-system lookups the git-root resolver needs (faked in tests). Every call is a single explicit path.</summary>
public interface IFileProbe
{
    bool DirectoryExists(string path);
    bool FileExists(string path);
    /// <summary>The first <paramref name="count"/> bytes of a file, or null if it cannot be read.</summary>
    byte[]? ReadHead(string path, int count);
    /// <summary>True for a path on a local fixed drive (never walk network or removable drives).</summary>
    bool IsLocalFixed(string path);
}

/// <summary>Where a user's well-known folders are, for classifying a git root.</summary>
/// <param name="ProfilesRoot">The folder holding all profiles (C:\Users): each direct child is a user profile.</param>
public sealed record GitRootContext(IReadOnlyList<string> UserProfiles, IReadOnlyList<string> OneDriveRoots, string? ProfilesRoot = null);

/// <summary>A resolved repository root and why it is suspicious (empty when it looks like a normal repo).</summary>
public sealed record GitRootInfo(string Root, IReadOnlyList<string> Flags, int? TrackedFiles)
{
    public bool Suspicious => Flags.Count > 0;
}

/// <summary>Windows path helpers that behave the same on Linux (unit tests) and Windows.</summary>
public static class WinPath
{
    public static bool IsRooted(string p) =>
        (p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] is '\\' or '/') || p.StartsWith(@"\\", StringComparison.Ordinal);

    public static bool IsUnc(string p) => p.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>Combines and normalises (separators, "." and ".." segments, trailing separator).</summary>
    public static string Combine(string? baseDir, string path)
    {
        if (IsRooted(path) || baseDir is null) return Normalize(path);
        return Normalize(baseDir.TrimEnd('\\', '/') + "\\" + path);
    }

    public static string Normalize(string p)
    {
        p = p.Replace('/', '\\');
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal) && p.Length > 6 && p[5] == ':') p = p[4..];
        string prefix;
        string rest;
        if (p.Length >= 2 && char.IsAsciiLetter(p[0]) && p[1] == ':') { prefix = char.ToUpperInvariant(p[0]) + ":\\"; rest = p.Length > 2 ? p[2..] : ""; }
        else if (p.StartsWith(@"\\", StringComparison.Ordinal)) { prefix = @"\\"; rest = p[2..]; }
        else { prefix = ""; rest = p; }
        var parts = new List<string>();
        foreach (var s in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (s == ".") continue;
            if (s == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(s);
        }
        return prefix + string.Join('\\', parts);
    }

    /// <summary>The parent directory, or null at a volume root.</summary>
    public static string? Parent(string p)
    {
        p = Normalize(p);
        if (IsVolumeRoot(p)) return null;
        int i = p.LastIndexOf('\\');
        if (i < 0) return null;
        return p.Length >= 3 && p[1] == ':' && i == 2 ? p[..3] : p[..i]; // C:\x -> C:\
    }

    public static bool IsVolumeRoot(string p)
    {
        p = Normalize(p);
        return (p.Length == 3 && p[1] == ':') || (IsUnc(p) && p.Count(c => c == '\\') <= 3);
    }

    public static bool SamePath(string a, string b) => string.Equals(Normalize(a).TrimEnd('\\'), Normalize(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="ancestor"/> is <paramref name="path"/> or one of its parents.</summary>
    public static bool IsSameOrUnder(string path, string ancestor)
    {
        string p = Normalize(path).TrimEnd('\\'), a = Normalize(ancestor).TrimEnd('\\');
        return p.Equals(a, StringComparison.OrdinalIgnoreCase) || p.StartsWith(a + "\\", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Finds the repository root a git command works in and flags roots that make git crawl far more than a project:
/// a user profile, a OneDrive folder, a volume root, an empty or invalid repository. Bounded: the walk stops at the
/// volume root (at most 64 levels), only local fixed drives are walked, and results are cached per directory.
/// </summary>
public sealed class GitRootResolver
{
    private const int MaxDepth = 64, MaxCache = 4096;
    private readonly IFileProbe _probe;
    private readonly TimeSpan _cacheFor;
    private readonly Dictionary<string, (string? root, DateTime until)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public GitRootResolver(IFileProbe probe, TimeSpan? cacheFor = null)
    {
        _probe = probe;
        _cacheFor = cacheFor ?? TimeSpan.FromMinutes(10);
    }

    public int ProbeCount { get; private set; }

    /// <summary>
    /// The directory a git invocation works in: <c>-C</c> options (relative ones apply to the previous), or
    /// <c>--work-tree</c>, else the process's working directory. Null when neither is known.
    /// </summary>
    public static string? WorkingDirectory(string? commandLine, string? cwd) => WorkingDirectory(commandLine, cwd, out _);

    /// <param name="fromOption">True when <c>-C</c> or <c>--work-tree</c> decided the directory.</param>
    public static string? WorkingDirectory(string? commandLine, string? cwd, out bool fromOption)
    {
        fromOption = false;
        var args = CommandLineTokens.Split(commandLine);
        string? cur = cwd is null ? null : WinPath.Normalize(cwd);
        string? workTree = null;
        for (int i = 1; i < args.Count; i++)
        {
            string a = args[i];
            if (!a.StartsWith('-')) break; // the subcommand: global options end here
            if (a == "-C" && i + 1 < args.Count) { string p = args[++i]; if (p.Length > 0) { cur = cur is null && !WinPath.IsRooted(p) ? null : WinPath.Combine(cur, p); fromOption = true; } }
            else if (a == "-c" && i + 1 < args.Count) i++;
            else if (a == "--work-tree" && i + 1 < args.Count) workTree = args[++i];
            else if (a.StartsWith("--work-tree=", StringComparison.Ordinal)) workTree = a["--work-tree=".Length..];
            else if (a is "--git-dir" or "--namespace" or "--exec-path" or "--config-env" && i + 1 < args.Count) i++;
        }
        if (workTree is { Length: > 0 }) fromOption = true;
        if (workTree is { Length: > 0 }) return WinPath.IsRooted(workTree) ? WinPath.Normalize(workTree) : cur is null ? null : WinPath.Combine(cur, workTree);
        return cur;
    }

    /// <summary>The nearest directory at or above <paramref name="dir"/> that holds a .git folder or file; null if none.</summary>
    public string? FindRoot(string dir, DateTime nowUtc)
    {
        dir = WinPath.Normalize(dir);
        if (!WinPath.IsRooted(dir) || WinPath.IsUnc(dir) || !_probe.IsLocalFixed(dir)) return null;
        var visited = new List<string>();
        string? cur = dir, found = null;
        for (int depth = 0; cur is not null && depth < MaxDepth; depth++)
        {
            if (_cache.TryGetValue(cur, out var c) && c.until > nowUtc) { found = c.root; break; }
            visited.Add(cur);
            ProbeCount++;
            string git = cur.TrimEnd('\\') + "\\.git";
            if (_probe.DirectoryExists(git) || _probe.FileExists(git)) { found = cur; break; }
            cur = WinPath.Parent(cur);
        }
        if (_cache.Count + visited.Count > MaxCache) _cache.Clear();
        var until = nowUtc + _cacheFor;
        foreach (var v in visited) _cache[v] = (found, until);
        return found;
    }

    /// <summary>Why a repository root is suspicious (empty list: a normal-looking repo).</summary>
    public GitRootInfo Classify(string root, GitRootContext ctx)
    {
        root = WinPath.Normalize(root);
        var flags = new List<string>();
        if (WinPath.IsVolumeRoot(root)) flags.Add("a volume root");
        if (ctx.ProfilesRoot is { } pr && WinPath.Parent(root) is { } parent && WinPath.SamePath(parent, pr)) flags.Add("a user profile");
        foreach (var profile in ctx.UserProfiles)
        {
            if (flags.Contains("a user profile")) break;
            if (WinPath.SamePath(root, profile)) { flags.Add("a user profile"); break; }
            if (WinPath.IsSameOrUnder(profile, root) && !WinPath.IsVolumeRoot(root)) { flags.Add("a folder that contains a user profile"); break; }
        }
        foreach (var od in ctx.OneDriveRoots)
        {
            if (WinPath.SamePath(root, od)) { flags.Add("a OneDrive root"); break; }
            string leaf = root[(root.LastIndexOf('\\') + 1)..];
            if (WinPath.Parent(root) is { } rp && WinPath.SamePath(rp, od) && new[] { "Desktop", "Documents", "Pictures" }.Contains(leaf, StringComparer.OrdinalIgnoreCase))
            { flags.Add($"a OneDrive known folder ({leaf})"); break; }
            if (WinPath.IsSameOrUnder(od, root) && !flags.Any(f => f.Contains("profile") || f.Contains("volume"))) { flags.Add("a folder that contains a OneDrive root"); break; }
        }
        int? tracked = null;
        string? gitDir = GitDir(root);
        if (gitDir is null || !_probe.FileExists(gitDir + "\\HEAD")) flags.Add("not a valid repository (no HEAD)");
        else
        {
            var head = _probe.ReadHead(gitDir + "\\index", 12);
            tracked = IndexEntryCount(head);
            if (head is null) flags.Add("no tracked files (no index)");
            else if (tracked == 0) flags.Add("no tracked files");
        }
        return new GitRootInfo(root, flags, tracked);
    }

    /// <summary>The git directory of a root: <c>root\.git</c>, or the target of a <c>gitdir:</c> file (worktrees, submodules).</summary>
    private string? GitDir(string root)
    {
        string git = root.TrimEnd('\\') + "\\.git";
        if (_probe.DirectoryExists(git)) return git;
        if (!_probe.FileExists(git) || _probe.ReadHead(git, 1024) is not { } bytes) return null;
        string text = System.Text.Encoding.UTF8.GetString(bytes).Trim();
        if (!text.StartsWith("gitdir:", StringComparison.Ordinal)) return null;
        string target = text["gitdir:".Length..].Split('\n')[0].Trim();
        return target.Length == 0 ? null : WinPath.Combine(root, target);
    }

    /// <summary>Entry count from a git index header (<c>DIRC</c>, version, count; big-endian), or null if not an index.</summary>
    public static int? IndexEntryCount(byte[]? head)
    {
        if (head is null || head.Length < 12 || head[0] != 'D' || head[1] != 'I' || head[2] != 'R' || head[3] != 'C') return null;
        return (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(8, 4));
    }
}
