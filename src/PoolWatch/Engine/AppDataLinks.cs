using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using PoolWatch.Core.Changes;
using PoolWatch.Core.Links;
using PoolWatch.Engine.Changes;
using PoolWatch.Native;

namespace PoolWatch.Engine;

/// <summary>What is at a path, the entry itself, never followed: nothing, a plain file or folder, or a junction or symbolic link.</summary>
internal enum AtPath { NotThere, Plain, Link }

/// <summary>
/// A look at one path: its <see cref="AtPath"/> and, for a link, the target it holds (one step, as written in it) and the
/// length of its reparse data (<see cref="ReparseData.Length"/>), which the size of the block the leak keeps follows.
/// </summary>
internal sealed record LinkLook(AtPath State, string? Target = null, int? DataLength = null);

/// <summary>
/// Everything the AppData link finder reads and its fixes write, so the tests stand in for Windows: no test reads a real
/// PATH, looks at a real link, reads a real file of the account's, or changes a variable. Each read is one explicit look;
/// none follows a link into a write.
/// </summary>
internal interface ILinkSystem
{
    /// <summary>The account's %APPDATA% (Roaming), %LOCALAPPDATA% and profile folders, from Windows' known folders.</summary>
    string AppData { get; }
    string LocalFolder { get; }
    string Profile { get; }
    /// <summary>The account's Store package full names (the per-user AppModel package repository).</summary>
    IReadOnlyList<string> PackageFullNames();
    /// <summary>The account's environment as its programs get it (<see cref="UserEnvironment.Read"/>), for filling in %NAME%.</summary>
    IReadOnlyDictionary<string, string> Environment();
    IEnvironmentStore Store { get; }
    /// <summary>The entry at <paramref name="path"/> itself, never followed. Throws when it cannot be looked at.</summary>
    LinkLook Look(string path);
    /// <summary>Where <paramref name="path"/> really is, every link on the way followed; null when it leads nowhere.</summary>
    string? Resolved(string path);
    /// <summary>The folders directly in <paramref name="folder"/> (full paths), at most <paramref name="max"/>; none when it is not there.</summary>
    IReadOnlyList<string> Folders(string folder, int max);
    /// <summary>How many of the entries directly in <paramref name="folder"/> are links, counting at most <paramref name="max"/> entries.</summary>
    int LinksIn(string folder, int max);

    /// <summary>
    /// The text of a small file of the account's (a venv's pyvenv.cfg, Claude Code's settings.json): only when the entry at
    /// <paramref name="path"/> itself is a plain file on a disk, never through a link at that path, at most
    /// <paramref name="maxBytes"/> bytes (a longer file is not read). Null when it is not there or not read.
    /// </summary>
    string? ReadText(string path, int maxBytes) => null;

    /// <summary>
    /// Whether Fast Startup is on (null: not known), and the last start's boot type as Windows logged it (Kernel-Boot
    /// event 27: 0 a full start or Restart, 1 a start from a Shut down with Fast Startup on, 2 from hibernation; null: not
    /// known). Reads only.
    /// </summary>
    (bool? FastStartup, int? BootType) Boot() => (null, null);

    /// <summary>The programs of the Claude and Codex desktop apps running now (<see cref="LinkApps.AppOfProgram"/>). Reads only.</summary>
    IReadOnlyList<AppProcess> AppProcesses() => [];
}

/// <summary>
/// The PC behind <see cref="ILinkSystem"/>: reads only, apart from the environment store's writes. Every open of a path
/// the account can change goes through <see cref="LinkNative"/>, as identification only, and a handle that is not a file
/// or folder on a disk is not used: a look asks for attributes only, and a link's target is read through the handle that
/// was checked; a small file is read through a handle opened on the entry itself, used only when that entry is a plain
/// file.
/// </summary>
internal sealed class WindowsLinkSystem : ILinkSystem
{
    /// <summary>The key <see cref="Setup.WindowsEnvironmentReads"/> reads the Store packages from.</summary>
    private const string PackagesKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    /// <summary>Where Windows keeps whether Fast Startup is on (HiberbootEnabled, a DWORD), under the machine's key.</summary>
    private const string PowerKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";

    /// <summary>The newest Kernel-Boot event 27 ("The boot type was 0x1."), whose first value is the boot type.</summary>
    private const string BootQuery = "*[System[Provider[@Name='Microsoft-Windows-Kernel-Boot'] and EventID=27]]";

    public string AppData { get; } = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
    public string LocalFolder { get; } = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
    public string Profile { get; } = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
    public IEnvironmentStore Store => WindowsEnvironmentStore.Instance;

    public IReadOnlyList<string> PackageFullNames()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PackagesKey);
        return key?.GetSubKeyNames() ?? [];
    }

    public IReadOnlyDictionary<string, string> Environment() => UserEnvironment.Read();

    public LinkLook Look(string path)
    {
        using var h = LinkNative.Open(path, follow: false);
        if (h.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            if (error is NoFollow.ERROR_FILE_NOT_FOUND or NoFollow.ERROR_PATH_NOT_FOUND) return new(AtPath.NotThere);
            throw new IOException($"{path}: {new Win32Exception(error).Message}");
        }
        if (!LinkNative.OnDisk(h)) throw new IOException($"{path} is not a file or folder on a disk");
        if (NoFollow.ReparseTag(h) is not (ReparseData.MountPoint or ReparseData.SymbolicLink)) return new(AtPath.Plain);
        // The target as the link holds it, one step, and its length, read through this handle (never by the path again).
        var data = LinkNative.ReparseData(h);
        return new(AtPath.Link, data is null ? null : ReparseData.Target(data)?.Target, data is null ? null : ReparseData.Length(data));
    }

    public string? Resolved(string path)
    {
        using var h = LinkNative.Open(path, follow: true);
        return h.IsInvalid || !LinkNative.OnDisk(h) ? null : NoFollow.FinalPath(h);
    }

    public IReadOnlyList<string> Folders(string folder, int max)
    {
        // Listed only once it is a folder on a disk (the open checks so), as the entries' own names: no entry is opened.
        if (Resolved(folder) is null) return [];
        try { return Directory.EnumerateDirectories(folder).Take(max).ToList(); }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException) { return []; }
    }

    public int LinksIn(string folder, int max)
    {
        if (Resolved(folder) is null) return 0;
        try
        {
            return new DirectoryInfo(folder).EnumerateFileSystemInfos().Take(max).Count(e => (e.Attributes & FileAttributes.ReparsePoint) != 0);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException) { return 0; }
    }

    public string? ReadText(string path, int maxBytes)
    {
        try
        {
            using var h = LinkNative.OpenToRead(path);
            // A link at the path itself, a folder, a pipe or a device is never read.
            if (h.IsInvalid || !LinkNative.OnDisk(h) || NoFollow.ReparseTag(h) != 0) return null;
            var buffer = new byte[maxBytes + 1];
            int total = 0;
            while (total < buffer.Length)
            {
                int n = RandomAccess.Read(h, buffer.AsSpan(total), total);
                if (n <= 0) break;
                total += n;
            }
            if (total > maxBytes) return null; // longer than PoolWatch reads: not read at all
            // Bytes that are not UTF-8 are read as U+FFFD; a byte-order mark at the start is left out.
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetString(buffer, 0, total).TrimStart('\uFEFF');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or Win32Exception)
        {
            return null;
        }
    }

    public (bool? FastStartup, int? BootType) Boot()
    {
        bool? fast = null;
        int? type = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PowerKey);
            if (key?.GetValue("HiberbootEnabled") is int on) fast = on != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        try
        {
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, BootQuery) { ReverseDirection = true });
            using var record = reader.ReadEvent();
            if (record is not null && record.Properties.Count > 0 && record.Properties[0].Value is { } value)
                type = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidCastException or FormatException or OverflowException) { }
        return (fast, type);
    }

    public IReadOnlyList<AppProcess> AppProcesses()
    {
        var found = new List<AppProcess>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    // Query rights only: its path and when it started, nothing else of it.
                    using var h = SafeProcess.Open(p.Id, NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION);
                    if (h is null || !NativeMethods.GetProcessTimes(h.Handle, out long created, out _, out _, out _)) continue;
                    if (Collectors.ProcessDetails.GetImagePath(h.Handle) is not { } path || LinkApps.AppOfProgram(path, AppData, LocalFolder) is not { } app) continue;
                    found.Add(new AppProcess(Path.GetFileName(path), app, DateTime.FromFileTimeUtc(created)));
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { }
            }
        }
        return found;
    }
}

/// <summary>
/// Finds the links under AppData that leak inside the Claude and Codex desktop apps (anthropics/claude-code issue 96870),
/// only when one of the two is installed for this account (<see cref="LinkApps"/>, from its Store package names): each
/// PATH entry, the account's and the machine's, that is or passes through a junction or symbolic link located under
/// %APPDATA% or %LOCALAPPDATA%; winget's Links folder on PATH; uv's minor-version links under %APPDATA%\uv\python; pnpm's
/// global packages and the Codex command-line tool's bin link kept under AppData; Claude Code's temp folder as a junction
/// (issue 95489); the apps' own data folders as junctions; a venv on PATH whose Python is reached through such a link
/// (its pyvenv.cfg read, bounded); Claude Code hooks that start Python (its settings.json read, bounded); and the same
/// tools and folders in each app's own copy of AppData (%LOCALAPPDATA%\Packages\...\LocalCache), where a program the app
/// started puts them, out of sight of programs outside it. Reads only, and nothing is followed into a write: what it finds
/// is judged again right before a fix (<see cref="AppDataLinkFixes"/>).
/// </summary>
internal static class AppDataLinkFinder
{
    public const string PathName = "Path";

    /// <summary>The account's AppData folders, as full paths without a closing "\".</summary>
    internal sealed record Roots(string AppData, string LocalFolder, string Profile)
    {
        public static Roots Of(ILinkSystem s) => new(Full(s.AppData), Full(s.LocalFolder), Full(s.Profile));

        /// <summary>The AppData folder <paramref name="path"/> is below, or null.</summary>
        public string? Holding(string path) => LinkPaths.Under(path, AppData) ? AppData : LinkPaths.Under(path, LocalFolder) ? LocalFolder : null;

        /// <summary>The path as the window shows it: the AppData folder at its start as %APPDATA% or %LOCALAPPDATA%.</summary>
        public string Shown(string path) =>
            LinkPaths.Under(path, AppData) ? "%APPDATA%" + path[AppData.Length..]
            : LinkPaths.Under(path, LocalFolder) ? LinkWords.LocalVariable + path[LocalFolder.Length..]
            : path;

        /// <summary>A folder below the account's local AppData folder, by its path from there ("Temp", "Packages\...").</summary>
        public string Local(string below) => Path.Join(LocalFolder, below);

        /// <summary>The account's own AppData, as the finder looks at it (the apps' own copies are <see cref="AppDataView"/>s too).</summary>
        public AppDataView Own => new(null, AppData, LocalFolder);

        private static string Full(string path) => path.Length == 0 ? "" : Path.GetFullPath(path).TrimEnd('\\');
    }

    /// <summary>
    /// One view of AppData: the account's own (<see cref="App"/> null), or the copy the Claude or Codex app keeps in its
    /// package's LocalCache, which programs the app starts write into: its Roaming and its Local folders.
    /// </summary>
    internal sealed record AppDataView(string? App, string Roaming, string Local);

    public static LinkScan Scan(ILinkSystem s, Action<string>? log = null)
    {
        var now = DateTime.UtcNow;
        var packages = s.PackageFullNames();
        bool claude = packages.Any(LinkApps.IsClaude), codex = packages.Any(LinkApps.IsCodex);
        if (!claude && !codex) return new LinkScan(now, false, false, []);
        var roots = Roots.Of(s);
        if (roots.AppData.Length == 0 || roots.LocalFolder.Length == 0) return new LinkScan(now, claude, codex, []);
        var env = s.Environment();
        var found = new List<LinkFinding>();
        var paths = new List<(EnvScope Scope, EnvValue Value)>();
        foreach (var scope in new[] { EnvScope.User, EnvScope.Machine })
        {
            EnvValue? path;
            try { path = s.Store.Read(scope, PathName); }
            catch (Exception ex) { log?.Invoke($"appdata links: the {EnvScopes.Word(scope)} PATH was not read: {ex.Message}"); continue; }
            if (path is not { } value) continue;
            paths.Add((scope, value));
            var entries = PathList.Split(value.Text);
            for (int i = 0; i < entries.Length && i < PathList.MaxEntries; i++)
            {
                try { if (Judge(s, roots, env, scope, value, i) is { } f) found.Add(f); }
                catch (Exception ex) { log?.Invoke($"appdata links: a {EnvScopes.Word(scope)} PATH entry was not judged: {ex.Message}"); }
            }
        }
        LinkFinding? uv = null;
        foreach (var look in new Func<LinkFinding?>[] { () => uv = Uv(s, roots, env), () => Pnpm(s, roots, env), () => Codex(s, roots, env), () => ClaudeTemp(s, roots, env, claude) })
        {
            try { if (look() is { } f) found.Add(f); }
            catch (Exception ex) { log?.Invoke("appdata links: a tool's folder was not judged: " + ex.Message); }
        }
        found.AddRange(AppFolders(s, roots, roots.Own, claude, codex, log));
        found.AddRange(Venvs(s, roots, env, paths, log));
        try { if (Hooks(s, roots, env, uv) is { } hooks) found.Add(hooks); }
        catch (Exception ex) { log?.Invoke("appdata links: Claude Code's hooks were not judged: " + ex.Message); }
        found.AddRange(InApps(s, roots, packages, log));
        found.Add(new LinkFinding { Kind = LinkKind.Projects });
        return new LinkScan(now, claude, codex, found);
    }

    /// <summary>
    /// The PATH entry at <paramref name="index"/> of <paramref name="value"/>, judged as it is now: a row when it goes
    /// through a link under AppData (with the folder to put in its place, or why there is none; whether that folder is on
    /// the PATH already), or is winget's Links folder with links in it; null for any other entry, and for one whose link
    /// PoolWatch cannot tell leads nowhere (<see cref="Gone"/>). An entry of the machine's PATH, which every account and
    /// Windows' own services read, is advice only. Used by the scan and again by the fix, right before it writes.
    /// </summary>
    internal static LinkFinding? Judge(ILinkSystem s, Roots roots, IReadOnlyDictionary<string, string> env, EnvScope scope, EnvValue value, int index)
    {
        var entries = PathList.Split(value.Text);
        if (index < 0 || index >= entries.Length) return null;
        string entry = entries[index];
        if (Expanded(entry, value.Expand, env) is not { } path) return null;
        var row = new LinkFinding { Kind = LinkKind.PathEntry, Scope = scope, Index = index, Entry = entry, Link = roots.Shown(path) };
        if (path.Equals(Path.Join(roots.LocalFolder, @"Microsoft\WinGet\Links"), StringComparison.OrdinalIgnoreCase))
        {
            int links = s.LinksIn(path, LinkFinder.MaxFolders);
            return links > 0 ? row with { Kind = LinkKind.WinGetLinks, Count = links } : null;
        }
        if (roots.Holding(path) is not { } root) return null;
        // The first link from the top: a link deeper down is reached through it, so it is where the link really is.
        string? link = null;
        LinkLook? found = null;
        foreach (string level in LinkPaths.Levels(path, root))
        {
            var look = s.Look(level);
            if (look.State == AtPath.NotThere) break;
            if (look.State == AtPath.Link) { (link, found) = (level, look); break; }
        }
        if (link is null || found is null) return null;
        row = row with { Link = roots.Shown(link), Block = Block(found) };
        // The folder the entry is in: the link's own target, one step, with the rest of the entry.
        string? target = Absolute(found.Target, Path.GetDirectoryName(link)!) is { } leads ? Joined(leads, path[link.Length..]) : null;
        if (s.Resolved(path) is not { } really) return target is not null && Gone(s, target) ? row with { Kind = LinkKind.PathEntryGone, Target = target } : null;
        if (scope != EnvScope.User) return row with { WhyNot = LinkWords.MachinePath };
        if (target is null) return row with { WhyNot = "its link leads to a place that is not a plain folder on a local drive." };
        if (!LinkPaths.Writable(target)) return row with { WhyNot = "the folder it leads to has a name PoolWatch does not write on a PATH (a ; or a %, say)." };
        if (roots.Holding(target) is { } again && LinkPaths.Levels(target, again).Any(level => s.Look(level).State == AtPath.Link))
            return row with { WhyNot = "the folder it leads to is reached through another link under AppData." };
        if (!NoFollow.SamePath(s.Resolved(target), really)) return row with { WhyNot = "PoolWatch could not confirm that the folder is where the link leads." };
        // Already on the PATH as another entry (codex update puts its link back in front after each fix): the fix removes this
        // entry rather than write the folder a second time.
        string wanted = target.TrimEnd('\\');
        bool already = entries.Where((other, i) => i != index && i < PathList.MaxEntries)
            .Any(other => Expanded(other, value.Expand, env) is { } there && there.TrimEnd('\\').Equals(wanted, StringComparison.OrdinalIgnoreCase));
        return row with { Target = target, OnPathAlready = already };
    }

    /// <summary>A folder and the rest of a path below it, as one full path ("D:\" for a drive's root).</summary>
    private static string Joined(string folder, string rest)
    {
        string full = (folder.TrimEnd('\\') + rest).TrimEnd('\\');
        return full.Length == 2 ? full + "\\" : full;
    }

    /// <summary>
    /// Whether the folder a link leads to is gone as far as PoolWatch can tell, as administrator: its drive is there, and from
    /// the drive's root down each folder is a plain one until one that is not there. False when it cannot tell: the drive is
    /// not there for PoolWatch (a drive letter mapped or subst'ed only in your own sign-in, a removable, locked or detached
    /// drive), a folder on the way is another link, or a look fails (access is denied, say).
    /// </summary>
    private static bool Gone(ILinkSystem s, string target)
    {
        string? root = Path.GetPathRoot(target);
        if (string.IsNullOrEmpty(root) || s.Resolved(root) is null) return false;
        try
        {
            foreach (string level in LinkPaths.Levels(target, root))
            {
                var look = s.Look(level);
                if (look.State == AtPath.NotThere) return true;
                if (look.State == AtPath.Link) return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception) { }
        return false;
    }

    /// <summary>
    /// The entry as a program reads it, as a full local path: unquoted, its %NAME% filled in from the account's environment
    /// when the value is REG_EXPAND_SZ, normalized. Null for an entry that is empty, still holds a %, or is not a plain
    /// path on a local drive.
    /// </summary>
    internal static string? Expanded(string entry, bool expand, IReadOnlyDictionary<string, string> env)
    {
        string text = LinkPaths.Unquote(expand ? UserEnvironment.Expand(entry, env) : entry);
        if (text.Length < 3 || text.Contains('%') || !char.IsAsciiLetter(text[0]) || text[1] != ':' || text[2] is not ('\\' or '/')) return null;
        try
        {
            string full = Path.GetFullPath(text).TrimEnd('\\');
            return Core.Settings.IsLocalDrivePath(full.Length == 2 ? full + "\\" : full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>
    /// A link's target as a full path: a drive path as it is (the NT prefix "\??\" or "\\?\" taken off), a relative one
    /// from the link's own folder. Null for any other form (a volume, a share, a path from the drive's root).
    /// </summary>
    internal static string? Absolute(string? target, string folder)
    {
        if (string.IsNullOrEmpty(target)) return null;
        // With the NT prefix the target is a full path: a drive's, or one PoolWatch does not write (a volume, a share);
        // it is never read as a path from the link's folder.
        bool full = target.StartsWith(@"\??\", StringComparison.Ordinal) || target.StartsWith(@"\\?\", StringComparison.Ordinal);
        string t = full ? target[4..] : target;
        try
        {
            if (t.Length >= 3 && char.IsAsciiLetter(t[0]) && t[1] == ':' && t[2] == '\\') return Path.GetFullPath(t);
            if (full || t.Contains(':') || t.StartsWith('\\') || t.StartsWith('/')) return null;
            return Path.GetFullPath(Path.Combine(folder, t));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>The size of the block each open through the link keeps, from its reparse data's length; null when that was not read.</summary>
    private static int? Block(LinkLook look) => look.DataLength is { } length ? LinkBlocks.Expected(length) : null;

    /// <summary>A path as the window shows it: %APPDATA% or %LOCALAPPDATA% at its start, else %USERPROFILE%, else as it is.</summary>
    private static string Shown(Roots roots, string path) => roots.Holding(path) is not null ? roots.Shown(path) : LinkPaths.InProfileWords(path, roots.Profile);

    /// <summary>The first link on the way from the AppData folder down to <paramref name="path"/>, when it is under AppData; else null.</summary>
    private static LinkLook? ThroughLink(ILinkSystem s, Roots roots, string path)
    {
        if (roots.Holding(path) is not { } root) return null;
        foreach (string level in LinkPaths.Levels(path, root))
        {
            var look = s.Look(level);
            if (look.State == AtPath.NotThere) return null;
            if (look.State == AtPath.Link) return look;
        }
        return null;
    }

    /// <summary>The value of <paramref name="name"/> the account's programs get, as a full path; null when it is not set or not a local path.</summary>
    private static string? Effective(IReadOnlyDictionary<string, string> env, string name) =>
        env.TryGetValue(name, out var v) ? Expanded(v, expand: false, env) : null;

    /// <summary>The account's own value as written (what a fix replaces and Put back restores); null when it has none.</summary>
    private static string? Seen(ILinkSystem s, string name) => s.Store.Read(EnvScope.User, name)?.Text;

    /// <summary>
    /// The account's programs get <paramref name="name"/> set to something PoolWatch leaves alone: a folder outside AppData,
    /// or a value that is not a plain path on a local drive (a share, or a %NAME% not filled in), which points away from
    /// AppData as far as PoolWatch can tell. Such a variable is offered no fix, and never told about as come back.
    /// </summary>
    private static bool LeftAlone(Roots roots, IReadOnlyDictionary<string, string> env, string name) =>
        env.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) && (Expanded(v, expand: false, env) is not { } path || roots.Holding(path) is null);

    /// <summary>The variables of <paramref name="names"/> that still point into AppData (or are not set), each with the folder it is set to.</summary>
    private static List<EnvSetting> ToSet(ILinkSystem s, Roots roots, IReadOnlyDictionary<string, string> env, params string[] names) =>
        names.Where(n => !LeftAlone(roots, env, n)).Select(n => new EnvSetting(n, LinkFinder.FolderFor(n, roots.Profile), Seen(s, n))).ToList();

    /// <summary>The links among the folders directly in <paramref name="folder"/> (uv's minor-version links), each with its look.</summary>
    private static List<(string Name, LinkLook Look)> LinkedFolders(ILinkSystem s, string folder) =>
        s.Folders(folder, LinkFinder.MaxFolders).Select(f => (Name: Path.GetFileName(f.TrimEnd('\\')), Look: s.Look(f))).Where(x => x.Look.State == AtPath.Link).ToList();

    /// <summary>uv's Pythons reached through its minor-version links under AppData (%APPDATA%\uv\python by default).</summary>
    private static LinkFinding? Uv(ILinkSystem s, Roots roots, IReadOnlyDictionary<string, string> env)
    {
        string? set = Effective(env, LinkFinder.UvPythons);
        bool moved = LeftAlone(roots, env, LinkFinder.UvPythons);
        // Moved: the links left are in the folder uv used before, its default.
        string folder = !moved && set is not null ? set : Path.Join(roots.AppData, @"uv\python");
        if (roots.Holding(folder) is null) return null;
        var links = LinkedFolders(s, folder);
        if (links.Count == 0) return null;
        return new LinkFinding
        {
            Kind = LinkKind.Uv, Link = roots.Shown(folder), Count = links.Count, Names = links.Select(l => l.Name).Take(LinkFinder.MaxNames).ToList(), Moved = moved,
            Block = links.Select(l => Block(l.Look)).FirstOrDefault(b => b is not null),
            EnvSettings = ToSet(s, roots, env, moved ? new[] { LinkFinder.UvCache } : new[] { LinkFinder.UvPythons, LinkFinder.UvCache }),
        };
    }

    /// <summary>pnpm's global packages kept under AppData (%LOCALAPPDATA%\pnpm by default), while PNPM_HOME does not point elsewhere.</summary>
    private static LinkFinding? Pnpm(ILinkSystem s, Roots roots, IReadOnlyDictionary<string, string> env)
    {
        if (LeftAlone(roots, env, LinkFinder.PnpmHome)) return null;
        string? home = Effective(env, LinkFinder.PnpmHome);
        string folder = home ?? Path.Join(roots.LocalFolder, "pnpm");
        if (s.Look(Path.Join(folder, "global")).State == AtPath.NotThere) return null;
        return new LinkFinding { Kind = LinkKind.Pnpm, Link = roots.Shown(folder), EnvSettings = ToSet(s, roots, env, LinkFinder.PnpmHome) };
    }

    /// <summary>
    /// The Codex command-line tool's bin link under AppData, made by its standalone installer, while CODEX_INSTALL_DIR does
    /// not point elsewhere. Only a link: a plain folder there is reached through none.
    /// </summary>
    private static LinkFinding? Codex(ILinkSystem s, Roots roots, IReadOnlyDictionary<string, string> env)
    {
        if (LeftAlone(roots, env, LinkFinder.CodexInstall)) return null;
        string? dir = Effective(env, LinkFinder.CodexInstall);
        string bin = dir ?? Path.Join(roots.LocalFolder, @"Programs\OpenAI\Codex\bin");
        var look = s.Look(bin);
        if (look.State != AtPath.Link) return null;
        return new LinkFinding { Kind = LinkKind.Codex, Link = roots.Shown(bin), Block = Block(look), EnvSettings = ToSet(s, roots, env, LinkFinder.CodexInstall) };
    }

    /// <summary>
    /// Claude Code's temp folder (TEMP\claude) as a junction or link under AppData, with the Claude app installed and
    /// CLAUDE_CODE_TMPDIR not set outside AppData: the Claude app's Claude Code then retries a failing open thousands of
    /// times a second (anthropics/claude-code issue 95489). Its fix sets CLAUDE_CODE_TMPDIR. A TEMP moved off AppData is
    /// left alone: that case was not measured.
    /// </summary>
    private static LinkFinding? ClaudeTemp(ILinkSystem s, Roots roots, IReadOnlyDictionary<string, string> env, bool claude)
    {
        if (!claude || LeftAlone(roots, env, LinkFinder.ClaudeTemp)) return null;
        string temp = Effective(env, "TEMP") ?? roots.Local("Temp");
        string folder = Path.Join(temp, "claude");
        if (roots.Holding(folder) is null) return null;
        var look = s.Look(folder);
        if (look.State != AtPath.Link) return null;
        return new LinkFinding
        {
            Kind = LinkKind.ClaudeTemp, Link = Shown(roots, folder), Target = Absolute(look.Target, temp), Block = Block(look),
            EnvSettings = ToSet(s, roots, env, LinkFinder.ClaudeTemp),
        };
    }

    /// <summary>
    /// The apps' own data folders as junctions or links, in one view of AppData: %APPDATA%\Claude for the Claude app;
    /// %APPDATA%\Codex, its web folder, %LOCALAPPDATA%\Codex and %LOCALAPPDATA%\OpenAI\Codex for the Codex app. Each is
    /// looked at alone (a look that fails leaves out that one); PoolWatch only gives steps for them.
    /// </summary>
    private static List<LinkFinding> AppFolders(ILinkSystem s, Roots roots, AppDataView view, bool claude, bool codex, Action<string>? log)
    {
        var places = new List<(string App, string Path)>();
        if (claude) places.Add((LinkApps.Claude, Path.Join(view.Roaming, "Claude")));
        if (codex)
        {
            places.Add((LinkApps.Codex, Path.Join(view.Roaming, "Codex")));
            places.Add((LinkApps.Codex, Path.Join(view.Roaming, @"Codex\web")));
            places.Add((LinkApps.Codex, Path.Join(view.Local, "Codex")));
            places.Add((LinkApps.Codex, Path.Join(view.Local, @"OpenAI\Codex")));
        }
        var found = new List<LinkFinding>();
        foreach (var (app, path) in places)
        {
            try
            {
                var look = s.Look(path);
                if (look.State != AtPath.Link) continue;
                found.Add(new LinkFinding
                {
                    Kind = LinkKind.AppFolder, Tool = app, InApp = view.App, Link = Shown(roots, path),
                    Target = Absolute(look.Target, Path.GetDirectoryName(path)!), Block = Block(look),
                });
            }
            catch (Exception ex) { log?.Invoke("appdata links: an app's data folder was not looked at: " + ex.Message); }
        }
        return found;
    }

    /// <summary>
    /// Each PATH entry that is a venv's Scripts folder whose Python (its home in the pyvenv.cfg beside it, read bounded) is
    /// reached through a link under AppData: PlatformIO's and Hermes Agent's venvs on uv's links, which put every python a
    /// program of the app runs through the link. The entry itself is a plain folder, so the PATH check does not see it.
    /// </summary>
    private static List<LinkFinding> Venvs(ILinkSystem s, Roots roots, IReadOnlyDictionary<string, string> env, List<(EnvScope Scope, EnvValue Value)> paths, Action<string>? log)
    {
        var found = new List<LinkFinding>();
        foreach (var (scope, value) in paths)
        {
            var entries = PathList.Split(value.Text);
            for (int i = 0; i < entries.Length && i < PathList.MaxEntries; i++)
            {
                try
                {
                    if (Expanded(entries[i], value.Expand, env) is not { } scripts || !Path.GetFileName(scripts).Equals("Scripts", StringComparison.OrdinalIgnoreCase) ||
                        Path.GetDirectoryName(scripts) is not { } venv) continue;
                    if (VenvConfig.Home(s.ReadText(Path.Join(venv, "pyvenv.cfg"), VenvConfig.MaxBytes)) is not { } home) continue;
                    if (Expanded(home, expand: false, env) is not { } python || ThroughLink(s, roots, python) is not { } link) continue;
                    found.Add(new LinkFinding
                    {
                        Kind = LinkKind.Venv, Scope = scope, Index = i, Entry = entries[i], Link = Shown(roots, venv), Home = roots.Shown(python),
                        Tool = LinkApps.ToolOf(venv), Block = Block(link),
                    });
                }
                catch (Exception ex) { log?.Invoke($"appdata links: a {EnvScopes.Word(scope)} PATH entry was not looked at as a venv: {ex.Message}"); }
            }
        }
        return found;
    }

    /// <summary>
    /// The hooks in the account's Claude Code settings (CLAUDE_CONFIG_DIR's settings.json, or the profile's .claude one;
    /// read bounded) that start Python likely through a link under AppData: by a full path through one, or by a name or
    /// from uv's tool folder while uv's Pythons are reached through its links under AppData. Null when there are none.
    /// </summary>
    private static LinkFinding? Hooks(ILinkSystem s, Roots roots, IReadOnlyDictionary<string, string> env, LinkFinding? uv)
    {
        string folder = Effective(env, "CLAUDE_CONFIG_DIR") ?? Path.Join(roots.Profile, ".claude");
        var commands = ClaudeHooks.Commands(s.ReadText(Path.Join(folder, "settings.json"), ClaudeHooks.MaxBytes))
            .Where(c => ClaudeHooks.StartsPython(ClaudeHooks.Program(c)) && Likely(s, roots, env, ClaudeHooks.Program(c), uv)).ToList();
        if (commands.Count == 0) return null;
        return new LinkFinding
        {
            Kind = LinkKind.Hook, Link = Shown(roots, folder), Count = commands.Count,
            Names = commands.Take(LinkFinder.MaxNames).Select(c => c.Length <= 120 ? c : c[..117] + "...").ToList(),
        };
    }

    /// <summary>Whether a hook's Python program is likely reached through a link under AppData.</summary>
    private static bool Likely(ILinkSystem s, Roots roots, IReadOnlyDictionary<string, string> env, string program, LinkFinding? uv)
    {
        bool uvLinked = uv is { Moved: false };
        if (Expanded(program, expand: true, env) is not { } full) return uvLinked; // a name, found on PATH
        // uv's own launchers (~\.local\bin\python3.12.exe) still go through its links (fsfxing2023-wq).
        return ThroughLink(s, roots, full) is not null || uvLinked && LinkPaths.Under(full, Path.Join(roots.Profile, @".local\bin"));
    }

    /// <summary>
    /// The same tools and data folders in each app's own copy of AppData (its package's LocalCache, Roaming and Local):
    /// what a program the app started made there, which programs outside the app do not see. Each app's package is named
    /// by its family name, read strictly (<see cref="LinkApps.Family"/>), so the folder is always one plain name.
    /// </summary>
    private static List<LinkFinding> InApps(ILinkSystem s, Roots roots, IReadOnlyList<string> packages, Action<string>? log)
    {
        var found = new List<LinkFinding>();
        var apps = packages.Select(p => (App: LinkApps.AppOf(p), Family: LinkApps.Family(p))).Where(p => p.App is not null && p.Family is not null).Distinct().Take(4).ToList();
        foreach (var (app, family) in apps)
        {
            try
            {
                string cache = roots.Local(Path.Join("Packages", family, "LocalCache"));
                if (s.Look(cache).State == AtPath.NotThere) continue;
                var view = new AppDataView(app, Path.Join(cache, "Roaming"), Path.Join(cache, "Local"));
                string uv = Path.Join(view.Roaming, @"uv\python");
                var links = LinkedFolders(s, uv);
                if (links.Count > 0)
                    found.Add(new LinkFinding
                    {
                        Kind = LinkKind.Uv, InApp = app, Link = roots.Shown(uv), Count = links.Count, Names = links.Select(l => l.Name).Take(LinkFinder.MaxNames).ToList(),
                        Block = links.Select(l => Block(l.Look)).FirstOrDefault(b => b is not null),
                    });
                string pnpm = Path.Join(view.Local, "pnpm");
                if (s.Look(Path.Join(pnpm, "global")).State != AtPath.NotThere) found.Add(new LinkFinding { Kind = LinkKind.Pnpm, InApp = app, Link = roots.Shown(pnpm) });
                string bin = Path.Join(view.Local, @"Programs\OpenAI\Codex\bin");
                var codex = s.Look(bin);
                if (codex.State == AtPath.Link) found.Add(new LinkFinding { Kind = LinkKind.Codex, InApp = app, Link = roots.Shown(bin), Block = Block(codex) });
                found.AddRange(AppFolders(s, roots, view, app == LinkApps.Claude, app == LinkApps.Codex, log));
            }
            catch (Exception ex) { log?.Invoke($"appdata links: the {app} app's own copy of AppData was not looked at: {ex.Message}"); }
        }
        return found;
    }

    /// <summary>
    /// What PoolWatch fixed before that is back, and can be fixed again: a PATH entry it replaced or removed that is on the
    /// PATH again (codex update puts its link back), or a variable it set that points into AppData again (the Hermes
    /// installer, a tool's own setup). What the tray tells about once, naming each.
    /// </summary>
    public static IReadOnlyList<LinkFinding> Back(LinkScan scan, IEnumerable<ChangeRecord> records)
    {
        var all = records.ToList();
        var fixedEntries = all.Where(r => r.Kind is RecordKind.PathEntry or RecordKind.RemovedPathEntry).Select(r => (r.Location, r.Before)).ToHashSet();
        var removed = all.Where(r => r.Kind == RecordKind.RemovedPathEntry).Select(r => (r.Location, r.Before)).ToHashSet();
        var set = all.Where(r => r.Kind == RecordKind.EnvironmentVariable && r.Name is not null).Select(r => r.Name!).ToHashSet(StringComparer.Ordinal);
        return scan.Findings.Where(f =>
            f is { Kind: LinkKind.PathEntry, CanFix: true } && fixedEntries.Contains((EnvScopes.Word(f.Scope), f.Entry))
            || f is { Kind: LinkKind.PathEntryGone, CanFix: true } && removed.Contains((EnvScopes.Word(f.Scope), f.Entry))
            || f.InApp is null && f.EnvSettings.Any(e => set.Contains(e.Name))).ToList();
    }
}

/// <summary>
/// The AppData links window's three changes, and Put back for each. Each change is recorded before it is made, nothing
/// is changed when the record cannot be saved, a failed change takes its record back, and each value is read again right
/// before it is written (through the key the write goes through), so a value that changed since the window showed it is
/// left as it is. A PATH entry is judged again from scratch first (<see cref="AppDataLinkFinder.Judge"/>): the entry, its
/// link and the folder it leads to must be as the row showed them. PoolWatch never reinstalls, moves or deletes a tool or
/// a file: it writes only the one PATH entry (replaced, or removed from the account's own PATH when its link leads
/// nowhere), or the few variables of <see cref="LinkFinder.Variables"/>.
/// </summary>
internal sealed class AppDataLinkFixes(ILinkSystem system, ChangeRecords records, Log log)
{
    /// <summary>What a fix says when what it would change is no longer as the window showed it.</summary>
    internal const string ChangedSince = "It is no longer as the window showed it, so PoolWatch changed nothing. Look again.";

    /// <summary>What the window reads besides the changes: the boot, the apps' programs.</summary>
    internal ILinkSystem System => system;

    /// <summary>
    /// Replaces the row's entry of the account's own PATH with the folder its link leads to, and nothing else; or, when that
    /// folder is on the PATH already, removes the entry rather than write the folder a second time (recorded as a removal,
    /// with the SHA-256 of the PATH it leaves, as <see cref="Remove"/> does). Never the machine's PATH, which every account and
    /// Windows' own services read.
    /// </summary>
    public (bool Ok, string Message) Replace(LinkFinding shown, string via)
    {
        if (shown is not { Kind: LinkKind.PathEntry, Scope: EnvScope.User, InApp: null, Target: { } target }) return (false, "PoolWatch does not replace this entry.");
        var store = system.Store;
        string scope = EnvScopes.User, where = ChangeWords.PathOf(scope);
        EnvValue value;
        LinkFinding? now;
        try
        {
            if (store.Read(EnvScope.User, AppDataLinkFinder.PathName) is not { } read) return (false, ChangedSince);
            value = read;
            now = AppDataLinkFinder.Judge(system, AppDataLinkFinder.Roots.Of(system), system.Environment(), EnvScope.User, value, shown.Index);
        }
        catch (Exception ex) { return (false, $"PoolWatch could not read {where}: {ex.Message}"); }
        if (now is not { Kind: LinkKind.PathEntry } || now.Entry != shown.Entry || now.Target != target || now.OnPathAlready != shown.OnPathAlready)
            return (false, now?.WhyNot is { } why && now.Entry == shown.Entry ? "PoolWatch did not replace the entry: " + why : ChangedSince);
        var changed = DateTime.UtcNow;
        if (now.OnPathAlready)
        {
            if (PathList.Remove(value.Text, shown.Index, shown.Entry) is not { } left) return (false, ChangedSince);
            var removal = new ChangeRecord
            {
                Id = ChangeIds.RemovedPathEntry(scope, shown.Entry, changed), Kind = RecordKind.RemovedPathEntry, ChangedUtc = changed, Via = via,
                Location = scope, Before = shown.Entry, After = target, Index = shown.Index, Sha256 = PathList.Hash(left),
            };
            if (Write(removal, EnvScope.User, value, left, where) is { } notRemoved) return (false, notRemoved);
            log.Info($"appdata links: {ChangeWords.RemovedPathEntry(scope, shown.Entry, target)} (from {via})");
            return (true, ChangeWords.RemovedPathEntry(scope, shown.Entry, target) + ". " + LinkWords.Restart);
        }
        if (PathList.Replace(value.Text, shown.Index, shown.Entry, target) is not { } next) return (false, ChangedSince);
        var record = new ChangeRecord
        {
            Id = ChangeIds.PathEntry(scope, shown.Entry, changed), Kind = RecordKind.PathEntry, ChangedUtc = changed, Via = via,
            Location = scope, Before = shown.Entry, After = target, Index = shown.Index,
        };
        if (Write(record, EnvScope.User, value, next, where) is { } failed) return (false, failed);
        log.Info($"appdata links: {ChangeWords.PathEntry(scope, shown.Entry, target)} (from {via})");
        return (true, ChangeWords.PathEntry(scope, shown.Entry, target) + ". " + LinkWords.Restart);
    }

    /// <summary>
    /// Removes the row's entry from the account's own PATH, only while its link still leads nowhere, and nothing else.
    /// The record keeps the SHA-256 of the PATH it leaves, so Put back acts only on that PATH, exactly.
    /// </summary>
    public (bool Ok, string Message) Remove(LinkFinding shown, string via)
    {
        if (shown is not { Kind: LinkKind.PathEntryGone, Scope: EnvScope.User, InApp: null }) return (false, "PoolWatch does not remove this entry.");
        var store = system.Store;
        string scope = EnvScopes.User, where = ChangeWords.PathOf(scope);
        EnvValue value;
        LinkFinding? now;
        try
        {
            if (store.Read(EnvScope.User, AppDataLinkFinder.PathName) is not { } read) return (false, ChangedSince);
            value = read;
            now = AppDataLinkFinder.Judge(system, AppDataLinkFinder.Roots.Of(system), system.Environment(), EnvScope.User, value, shown.Index);
        }
        catch (Exception ex) { return (false, $"PoolWatch could not read {where}: {ex.Message}"); }
        if (now is not { Kind: LinkKind.PathEntryGone } || now.Entry != shown.Entry) return (false, ChangedSince);
        if (PathList.Split(value.Text).Length < 2) return (false, "PoolWatch did not remove the entry: it is the only one on your PATH. Remove it yourself in Windows' Environment Variables window.");
        if (PathList.Remove(value.Text, shown.Index, shown.Entry) is not { } next) return (false, ChangedSince);
        var changed = DateTime.UtcNow;
        var record = new ChangeRecord
        {
            Id = ChangeIds.RemovedPathEntry(scope, shown.Entry, changed), Kind = RecordKind.RemovedPathEntry, ChangedUtc = changed, Via = via,
            Location = scope, Before = shown.Entry, Index = shown.Index, Sha256 = PathList.Hash(next),
        };
        if (Write(record, EnvScope.User, value, next, where) is { } failed) return (false, failed);
        log.Info($"appdata links: {ChangeWords.RemovedPathEntry(scope, shown.Entry)} (from {via})");
        return (true, ChangeWords.RemovedPathEntry(scope, shown.Entry) + ". " + LinkWords.Restart);
    }

    /// <summary>
    /// Records the change, then writes the PATH (its kind kept) only while it is still <paramref name="value"/>; a failed
    /// write takes the record back. Null when it was written, else what the window says.
    /// </summary>
    private string? Write(ChangeRecord record, EnvScope scope, EnvValue value, string next, string where)
    {
        var store = system.Store;
        try { records.Put(record); }
        catch (Exception ex) { return $"PoolWatch did not change {where}, because it could not record the change to put it back later: {ex.Message}"; }
        string? error;
        try { error = store.Write(scope, AppDataLinkFinder.PathName, value, new EnvValue(next, value.Expand)); }
        catch (Exception ex) { error = ex.Message; }
        if (error is not null)
        {
            records.Remove(record);
            return $"PoolWatch could not change {where}: {error}.";
        }
        Tell(store);
        return null;
    }

    /// <summary>
    /// Sets the row's variables for the account, each only when it is still as shown and its folder is PoolWatch's own
    /// choice for it under the profile, outside AppData, with no link on the way; returns the ones it set.
    /// </summary>
    public (bool Ok, string Message, IReadOnlyList<EnvSetting> Set) SetVariables(LinkFinding shown, string via)
    {
        if (shown.Kind is not (LinkKind.Uv or LinkKind.Pnpm or LinkKind.Codex or LinkKind.ClaudeTemp) || shown.InApp is not null || shown.EnvSettings.Count == 0)
            return (false, "There is nothing to set for this row.", []);
        var store = system.Store;
        var set = new List<EnvSetting>();
        var errors = new List<string>();
        foreach (var s in shown.EnvSettings)
        {
            if (WhyNotSet(s) is { } why) { errors.Add($"{s.Name} was not set: {why}"); continue; }
            EnvValue? now;
            try { now = store.Read(EnvScope.User, s.Name); }
            catch (Exception ex) { errors.Add($"{s.Name} was not set: PoolWatch could not read it ({ex.Message})"); continue; }
            if (now?.Text != s.Seen) { errors.Add($"{s.Name} was not set: {WindowsEnvironmentStore.ChangedSince}"); continue; }
            var changed = DateTime.UtcNow;
            var record = new ChangeRecord
            {
                Id = ChangeIds.EnvironmentVariable(s.Name), Kind = RecordKind.EnvironmentVariable, ChangedUtc = changed, Via = via, Location = EnvScopes.User,
                Name = s.Name, Before = now?.Text, ValueKind = now is { } v ? (v.Expand ? "ExpandString" : "String") : null, After = s.Value,
            };
            try { records.Put(record); }
            catch (Exception ex) { errors.Add($"{s.Name} was not set, because PoolWatch could not record it to put it back later: {ex.Message}"); continue; }
            string? error;
            try { error = store.Write(EnvScope.User, s.Name, now, new EnvValue(s.Value, Expand: false)); }
            catch (Exception ex) { error = ex.Message; }
            if (error is not null)
            {
                records.Remove(record);
                errors.Add($"{s.Name} was not set: {error}");
                continue;
            }
            log.Info($"appdata links: {ChangeWords.EnvironmentVariable(s.Name, s.Value)} (from {via})");
            set.Add(s);
        }
        if (set.Count > 0) Tell(store);
        string said = string.Join(" ", set.Select(s => ChangeWords.EnvironmentVariable(s.Name, s.Value) + ".").Concat(errors.Select(e => e + ".")));
        return (errors.Count == 0, said, set);
    }

    /// <summary>
    /// Why the variable is not set to its folder, or null: only one of <see cref="LinkFinder.Variables"/>, only to
    /// PoolWatch's own folder for it (<see cref="LinkFinder.FolderFor"/>), a plain local path below the profile and
    /// outside AppData, and with no folder from the profile down to it a link (a folder not made yet is fine: the tool
    /// makes it, or the steps say to).
    /// </summary>
    internal string? WhyNotSet(EnvSetting s)
    {
        var roots = AppDataLinkFinder.Roots.Of(system);
        if (!LinkFinder.Variables.Contains(s.Name, StringComparer.Ordinal)) return "PoolWatch does not set that variable";
        if (!LinkPaths.Writable(s.Value) || !s.Value.Equals(LinkFinder.FolderFor(s.Name, roots.Profile), StringComparison.OrdinalIgnoreCase))
            return "that is not the folder PoolWatch sets it to";
        if (!LinkPaths.Under(s.Value, roots.Profile) || roots.Holding(s.Value) is not null) return "the folder is not in your profile outside AppData";
        try
        {
            foreach (string level in LinkPaths.Levels(s.Value, roots.Profile))
            {
                var look = system.Look(level);
                if (look.State == AtPath.NotThere) break;
                if (look.State == AtPath.Link) return $"{level} is a link to another place";
            }
        }
        catch (Exception ex) { return "PoolWatch could not look at the folder: " + ex.Message; }
        return null;
    }

    /// <summary>When PoolWatch last changed a PATH entry or a variable for links under AppData; null when the change list holds none.</summary>
    public DateTime? LastChange()
    {
        var mine = records.All().Where(r => r.Kind is RecordKind.PathEntry or RecordKind.EnvironmentVariable or RecordKind.RemovedPathEntry).ToList();
        return mine.Count == 0 ? null : mine.Max(r => r.ChangedUtc);
    }

    /// <summary>The Claude and Codex apps' programs that started before <paramref name="since"/> and still run: they do not see a change made since. Reads only.</summary>
    public IReadOnlyList<AppProcess> StillOld(DateTime since)
    {
        try { return system.AppProcesses().Where(p => p.StartedUtc < since).ToList(); }
        catch (Exception ex)
        {
            log.Info("appdata links: the apps' programs were not read: " + ex.Message);
            return [];
        }
    }

    private void Tell(IEnvironmentStore store)
    {
        try { store.Broadcast(); }
        catch (Exception ex) { log.Info("appdata links: the change was not told to running programs: " + ex.Message); }
    }

    /// <summary>
    /// Put back (Active changes, Put all back, the uninstaller), checked against the registry as it is now, and never
    /// claiming more than it wrote: a result that wrote less than the whole change carries a <see cref="UndoResult.Caveat"/>,
    /// so the uninstaller's summary says "Partly put back", never a plain "Put back". A replaced entry goes back where the
    /// folder PoolWatch wrote still is on that PATH (<see cref="PathList.PutBack"/>); when the original entry is on the PATH
    /// again already, the folder PoolWatch wrote is taken out instead, so the entry is not there twice; when the folder is
    /// no longer on the PATH, nothing is written and the record goes; when it is there more than once, none of them where
    /// PoolWatch wrote it, nothing is written and the record stays. A removed entry goes back exactly while the account's
    /// PATH is still what PoolWatch left (<see cref="PathList.Insert"/>), else at its old place among the entries there now
    /// (<see cref="PathList.InsertAt"/>), or not at all when it is on the PATH again already. A variable goes back only while
    /// it is still what PoolWatch set it to. With no store (a test's ledger, or none given), nothing is put back.
    /// </summary>
    internal static UndoResult PutBack(ChangeRecord r, IEnvironmentStore? store, ChangeRecords changes)
    {
        if (store is null) return new(false, "PoolWatch cannot put this back from here.");
        if (r.Kind == RecordKind.PathEntry)
        {
            var scope = EnvScopes.Parse(r.Location)!.Value;
            string where = ChangeWords.PathOf(r.Location), before = r.Before!.Trim();
            EnvValue? now;
            try { now = store.Read(scope, AppDataLinkFinder.PathName); }
            catch (Exception ex) { return new(false, $"PoolWatch could not read {where}: {ex.Message}"); }
            int at = now is { } held ? PathList.Find(PathList.Split(held.Text), r.Index!.Value, r.After!) : -1;
            if (now is not { } value || at == -1)
            {
                changes.Remove(r);
                string nothing = $"{where} no longer holds {r.After}, the folder PoolWatch wrote, so PoolWatch wrote nothing" +
                                 (now is { } v && PathList.Holds(v.Text, r.Before!) ? $": {before} is on it again"
                                                                                      : $": you can add {before} again in Windows' Environment Variables window");
                return new(true, Capital(nothing) + ".") { Caveat = nothing };
            }
            if (at == PathList.Twice)
                return new(false, $"{Capital(where)} holds {r.After} more than once, none of them where PoolWatch wrote it, so PoolWatch cannot tell " +
                                  "which is its own. Remove the extra one in Windows' Environment Variables window, then put this back again.");
            // The original entry on the PATH again (codex update puts its link back): take out the folder PoolWatch wrote, so the
            // entry is there once.
            bool again = PathList.Holds(value.Text, r.Before!);
            string? next = again ? PathList.Remove(value.Text, at, r.After!) : PathList.PutBack(value.Text, at, r.After!, r.Before!);
            if (next is null) return new(false, $"PoolWatch could not put {before} back on {where}: the PATH would be longer than Windows allows.");
            if (store.Write(scope, AppDataLinkFinder.PathName, value, new EnvValue(next, value.Expand)) is { } error)
                return new(false, $"PoolWatch could not put the entry back on {where}: {error}.");
            try { store.Broadcast(); } catch (Exception) { } // put back either way; programs started later read the registry
            changes.Remove(r);
            if (!again) return new(true, $"Put {before} back on {where}. " + LinkWords.Restart);
            string once = $"{before} was on {where} again already, so PoolWatch took out {r.After}, the folder it had written, rather than add the " +
                          "entry a second time";
            return new(true, Capital(once) + ". " + LinkWords.Restart) { Caveat = once };
        }
        if (r.Kind == RecordKind.RemovedPathEntry)
        {
            string where = ChangeWords.PathOf(EnvScopes.User), entry = r.Before!.Trim();
            EnvValue? now;
            try { now = store.Read(EnvScope.User, AppDataLinkFinder.PathName); }
            catch (Exception ex) { return new(false, $"PoolWatch could not read {where}: {ex.Message}"); }
            if (now is not { } value)
                return new(false, $"{Capital(where)} is not there any more, so PoolWatch did not put {entry} back: add it in Windows' Environment Variables window.");
            string? caveat = null;
            if (PathList.Insert(value.Text, r.Index!.Value, r.Before!, r.Sha256!) is not { } next)
            {
                // The PATH changed since PoolWatch removed the entry: never lose it. Back at its old place among the entries
                // there now, unless it is on the PATH again already.
                if (PathList.Holds(value.Text, r.Before!))
                {
                    changes.Remove(r);
                    string there = $"{entry} was on {where} again already, so PoolWatch did not add it a second time";
                    return new(true, Capital(there) + ".") { Caveat = there };
                }
                if (PathList.InsertAt(value.Text, r.Index!.Value, r.Before!) is not { } placed)
                    return new(false, $"PoolWatch could not put {entry} back on {where}: the PATH would be longer than Windows allows.");
                next = placed;
                caveat = $"{where} changed after PoolWatch removed {entry}, so it went back at its old place among the entries there now, not " +
                         "exactly as it was";
            }
            if (store.Write(EnvScope.User, AppDataLinkFinder.PathName, value, new EnvValue(next, value.Expand)) is { } error)
                return new(false, $"PoolWatch could not put the entry back on {where}: {error}.");
            try { store.Broadcast(); } catch (Exception) { }
            changes.Remove(r);
            return new(true, (caveat is null ? $"Put {entry} back on {where}. " : Capital(caveat) + ". ") + LinkWords.Restart) { Caveat = caveat };
        }
        EnvValue? was;
        try { was = store.Read(EnvScope.User, r.Name!); }
        catch (Exception ex) { return new(false, $"PoolWatch could not read {r.Name}: {ex.Message}"); }
        if (was?.Text != r.After)
        {
            changes.Remove(r);
            return new(true, $"{r.Name} was changed after PoolWatch set it, so PoolWatch left it as it is.");
        }
        EnvValue? back = r.Before is { } before ? new EnvValue(before, r.ValueKind == "ExpandString") : null;
        if (store.Write(EnvScope.User, r.Name!, was, back) is { } failed) return new(false, $"PoolWatch could not put {r.Name} back: {failed}.");
        try { store.Broadcast(); } catch (Exception) { }
        changes.Remove(r);
        return new(true, r.Before is null ? $"Removed {r.Name}: it was not set before PoolWatch set it." : $"Set {r.Name} back to {r.Before}.");
    }

    private static string Capital(string s) => char.ToUpperInvariant(s[0]) + s[1..];
}

/// <summary>
/// The last look for links under AppData, kept in memory: looked again at most once an hour on its own (the Simple view's
/// refresh and the tray's slow beat ask, <see cref="ScanIfOld"/>), and at once when the window opens or a fix is made. One
/// look at a time, on a worker thread.
/// </summary>
internal sealed class AppDataLinkWatch
{
    public static readonly TimeSpan Every = TimeSpan.FromHours(1);

    private readonly Func<LinkScan> _scan;
    private readonly Log _log;
    private readonly Func<long> _nowMs;
    private readonly object _lock = new();
    private LinkScan? _last;
    /// <summary>When the last look ended, on the clock that only goes forward; null before the first.</summary>
    private long? _endedMs;
    private Task<LinkScan>? _running;

    /// <param name="nowMs">The tests: a clock they move on; the forward clock (<see cref="Core.Model.Moment.Now"/>) otherwise.</param>
    public AppDataLinkWatch(Func<LinkScan> scan, Log log, Func<long>? nowMs = null)
    {
        _scan = scan;
        _log = log;
        _nowMs = nowMs ?? (() => Core.Model.Moment.Now().Ms);
    }

    public LinkScan? Last { get { lock (_lock) return _last; } }

    /// <summary>Looks now (or joins the look that is running).</summary>
    public Task<LinkScan> ScanNow()
    {
        lock (_lock)
        {
            if (_running is { IsCompleted: false } running) return running;
            return _running = Task.Run(() =>
            {
                try
                {
                    var scan = _scan();
                    lock (_lock) (_last, _endedMs) = (scan, _nowMs());
                    return scan;
                }
                catch (Exception ex)
                {
                    lock (_lock) _endedMs = _nowMs();
                    _log.Info("appdata links: not looked at: " + ex.Message);
                    throw;
                }
            });
        }
    }

    /// <summary>Looks in the background when the last look is an hour old or there was none; null when it does not look now.</summary>
    public Task<LinkScan>? ScanIfOld()
    {
        lock (_lock)
            if (_running is { IsCompleted: false } || _endedMs is { } ended && _nowMs() - ended < (long)Every.TotalMilliseconds) return null;
        var scan = ScanNow();
        _ = scan.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); // logged by the look itself
        return scan;
    }
}
