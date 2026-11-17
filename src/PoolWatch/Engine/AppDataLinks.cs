using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using PoolWatch.Core.Changes;
using PoolWatch.Core.Links;
using PoolWatch.Engine.Changes;
using PoolWatch.Native;

namespace PoolWatch.Engine;

/// <summary>What is at a path, the entry itself, never followed: nothing, a plain file or folder, or a junction or symbolic link.</summary>
internal enum AtPath { NotThere, Plain, Link }

/// <summary>A look at one path: its <see cref="AtPath"/> and, for a link, the target it holds (one step, as written in it).</summary>
internal sealed record LinkLook(AtPath State, string? Target = null);

/// <summary>
/// Everything the AppData link finder reads and its fixes write, so the tests stand in for Windows: no test reads a real
/// PATH, looks at a real link or changes a variable. Each read is one explicit look; none follows a link into a write.
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
}

/// <summary>
/// The PC behind <see cref="ILinkSystem"/>: reads only, apart from the environment store's writes. Every open of a path
/// the account can change goes through <see cref="LinkNative"/>: attributes only, identification only, and a handle that
/// is not a file or folder on a disk is not used; a link's target is read through the handle that was checked.
/// </summary>
internal sealed class WindowsLinkSystem : ILinkSystem
{
    /// <summary>The key <see cref="Setup.WindowsEnvironmentReads"/> reads the Store packages from.</summary>
    private const string PackagesKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

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
        // The target as the link holds it, one step, read through this handle (never by the path again).
        return new(AtPath.Link, LinkNative.ReparseData(h) is { } data ? ReparseData.Target(data)?.Target : null);
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
}

/// <summary>
/// Finds the links under AppData that leak inside the Claude and Codex desktop apps (anthropics/claude-code issue 96870),
/// only when one of the two is installed for this account (<see cref="LinkApps"/>, from its Store package names): each
/// PATH entry, the account's and the machine's, that is or passes through a junction or symbolic link located under
/// %APPDATA% or %LOCALAPPDATA%; winget's Links folder on PATH; uv's minor-version links under %APPDATA%\uv\python; pnpm's
/// global packages and the Codex command-line tool's bin link kept under AppData. Reads only, and nothing is followed
/// into a write: what it finds is judged again right before a fix (<see cref="AppDataLinkFixes"/>).
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

        private static string Full(string path) => path.Length == 0 ? "" : Path.GetFullPath(path).TrimEnd('\\');
    }

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
        foreach (var scope in new[] { EnvScope.User, EnvScope.Machine })
        {
            EnvValue? path;
            try { path = s.Store.Read(scope, PathName); }
            catch (Exception ex) { log?.Invoke($"appdata links: the {EnvScopes.Word(scope)} PATH was not read: {ex.Message}"); continue; }
            if (path is not { } value) continue;
            var entries = PathList.Split(value.Text);
            for (int i = 0; i < entries.Length && i < PathList.MaxEntries; i++)
            {
                try { if (Judge(s, roots, env, scope, value, i) is { } f) found.Add(f); }
                catch (Exception ex) { log?.Invoke($"appdata links: a {EnvScopes.Word(scope)} PATH entry was not judged: {ex.Message}"); }
            }
        }
        foreach (var look in new Func<LinkFinding?>[] { () => Uv(s, roots, env), () => Pnpm(s, roots, env), () => Codex(s, roots, env) })
        {
            try { if (look() is { } f) found.Add(f); }
            catch (Exception ex) { log?.Invoke("appdata links: a tool's folder was not judged: " + ex.Message); }
        }
        found.Add(new LinkFinding { Kind = LinkKind.Projects });
        return new LinkScan(now, claude, codex, found);
    }

    /// <summary>
    /// The PATH entry at <paramref name="index"/> of <paramref name="value"/>, judged as it is now: a row when it goes
    /// through a link under AppData (with the folder to put in its place, or why there is none), or is winget's Links
    /// folder with links in it; null for any other entry. Used by the scan and again by the fix, right before it writes.
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
        row = row with { Link = roots.Shown(link) };
        if (s.Resolved(path) is not { } really) return row with { Kind = LinkKind.PathEntryGone };
        if (Absolute(found.Target, Path.GetDirectoryName(link)!) is not { } leads) return row with { WhyNot = "its link leads to a place that is not a plain folder on a local drive." };
        string target = (leads.TrimEnd('\\') + path[link.Length..]).TrimEnd('\\');
        if (target.Length == 2) target += "\\"; // a drive's root: "D:\"
        if (!LinkPaths.Writable(target)) return row with { WhyNot = "the folder it leads to has a name PoolWatch does not write on a PATH (a ; or a %, say)." };
        if (roots.Holding(target) is { } again && LinkPaths.Levels(target, again).Any(level => s.Look(level).State == AtPath.Link))
            return row with { WhyNot = "the folder it leads to is reached through another link under AppData." };
        if (!NoFollow.SamePath(s.Resolved(target), really)) return row with { WhyNot = "PoolWatch could not confirm that the folder is where the link leads." };
        return row with { Target = target };
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
        string t = target.StartsWith(@"\??\", StringComparison.Ordinal) ? target[4..]
            : target.StartsWith(@"\\?\", StringComparison.Ordinal) && !target.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? target[4..]
            : target;
        try
        {
            if (t.Length >= 3 && char.IsAsciiLetter(t[0]) && t[1] == ':' && t[2] == '\\') return Path.GetFullPath(t);
            if (t.Contains(':') || t.StartsWith('\\') || t.StartsWith('/')) return null;
            return Path.GetFullPath(Path.Combine(folder, t));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>The value of <paramref name="name"/> the account's programs get, as a full path; null when it is not set or not a local path.</summary>
    private static string? Effective(IReadOnlyDictionary<string, string> env, string name) =>
        env.TryGetValue(name, out var v) ? Expanded(v, expand: false, env) : null;

    /// <summary>The account's own value as written (what a fix replaces and Put back restores); null when it has none.</summary>
    private static string? Seen(ILinkSystem s, string name) => s.Store.Read(EnvScope.User, name)?.Text;

    private static bool Outside(Roots roots, string? path) => path is not null && roots.Holding(path) is null;

    /// <summary>The variables of <paramref name="names"/> that still point into AppData (or are not set), each with the folder it is set to.</summary>
    private static List<EnvSetting> ToSet(ILinkSystem s, Roots roots, IReadOnlyDictionary<string, string> env, params string[] names) =>
        names.Where(n => !Outside(roots, Effective(env, n))).Select(n => new EnvSetting(n, LinkFinder.FolderFor(n, roots.Profile), Seen(s, n))).ToList();

    /// <summary>uv's Pythons reached through its minor-version links under AppData (%APPDATA%\uv\python by default).</summary>
    private static LinkFinding? Uv(ILinkSystem s, Roots roots, IReadOnlyDictionary<string, string> env)
    {
        string? set = Effective(env, LinkFinder.UvPythons);
        bool moved = Outside(roots, set);
        // Moved: the links left are in the folder uv used before, its default.
        string folder = !moved && set is not null ? set : Path.Join(roots.AppData, @"uv\python");
        if (roots.Holding(folder) is null) return null;
        var links = s.Folders(folder, LinkFinder.MaxFolders).Where(f => s.Look(f).State == AtPath.Link).Select(f => Path.GetFileName(f.TrimEnd('\\'))).ToList();
        if (links.Count == 0) return null;
        return new LinkFinding
        {
            Kind = LinkKind.Uv, Link = roots.Shown(folder), Count = links.Count, Names = links.Take(LinkFinder.MaxNames).ToList(), Moved = moved,
            EnvSettings = ToSet(s, roots, env, moved ? new[] { LinkFinder.UvCache } : new[] { LinkFinder.UvPythons, LinkFinder.UvCache }),
        };
    }

    /// <summary>pnpm's global packages kept under AppData (%LOCALAPPDATA%\pnpm by default), while PNPM_HOME does not point elsewhere.</summary>
    private static LinkFinding? Pnpm(ILinkSystem s, Roots roots, IReadOnlyDictionary<string, string> env)
    {
        string? home = Effective(env, LinkFinder.PnpmHome);
        if (Outside(roots, home)) return null;
        string folder = home ?? Path.Join(roots.LocalFolder, "pnpm");
        if (s.Look(Path.Join(folder, "global")).State == AtPath.NotThere) return null;
        return new LinkFinding { Kind = LinkKind.Pnpm, Link = roots.Shown(folder), EnvSettings = ToSet(s, roots, env, LinkFinder.PnpmHome) };
    }

    /// <summary>The Codex command-line tool's bin link under AppData, made by its standalone installer, while CODEX_INSTALL_DIR does not point elsewhere.</summary>
    private static LinkFinding? Codex(ILinkSystem s, Roots roots, IReadOnlyDictionary<string, string> env)
    {
        string? dir = Effective(env, LinkFinder.CodexInstall);
        if (Outside(roots, dir)) return null;
        string bin = dir ?? Path.Join(roots.LocalFolder, @"Programs\OpenAI\Codex\bin");
        if (s.Look(bin).State == AtPath.NotThere) return null;
        return new LinkFinding { Kind = LinkKind.Codex, Link = roots.Shown(bin), EnvSettings = ToSet(s, roots, env, LinkFinder.CodexInstall) };
    }

    /// <summary>
    /// The PATH entries a fix replaced before that are back on the PATH and can be fixed again: a tool put its link back
    /// (codex update does). What the tray tells about once.
    /// </summary>
    public static IReadOnlyList<LinkFinding> Back(LinkScan scan, IEnumerable<ChangeRecord> records)
    {
        var replaced = records.Where(r => r.Kind == RecordKind.PathEntry).Select(r => (r.Location, r.Before)).ToHashSet();
        return scan.Findings.Where(f => f is { Kind: LinkKind.PathEntry, Target: not null } && replaced.Contains((EnvScopes.Word(f.Scope), f.Entry))).ToList();
    }

}

/// <summary>
/// The AppData links window's two changes, and Put back for both. Each change is recorded before it is made, nothing is
/// changed when the record cannot be saved, a failed change takes its record back, and each value is read again right
/// before it is written (through the key the write goes through), so a value that changed since the window showed it is
/// left as it is. A PATH entry is judged again from scratch first (<see cref="AppDataLinkFinder.Judge"/>): the entry, its
/// link and the folder it leads to must be as the row showed them. PoolWatch never reinstalls, moves or deletes a tool or
/// a file: it writes only the one PATH entry, or the few variables of <see cref="LinkFinder.Variables"/>.
/// </summary>
internal sealed class AppDataLinkFixes(ILinkSystem system, ChangeRecords records, Log log)
{
    /// <summary>What a fix says when what it would change is no longer as the window showed it.</summary>
    internal const string ChangedSince = "It is no longer as the window showed it, so PoolWatch changed nothing. Look again.";

    /// <summary>Replaces the row's PATH entry with the folder its link leads to, and nothing else.</summary>
    public (bool Ok, string Message) Replace(LinkFinding shown, string via)
    {
        if (shown is not { Kind: LinkKind.PathEntry, Target: { } target }) return (false, "PoolWatch does not replace this entry.");
        var store = system.Store;
        string scope = EnvScopes.Word(shown.Scope), where = ChangeWords.PathOf(scope);
        EnvValue value;
        LinkFinding? now;
        try
        {
            if (store.Read(shown.Scope, AppDataLinkFinder.PathName) is not { } read) return (false, ChangedSince);
            value = read;
            now = AppDataLinkFinder.Judge(system, AppDataLinkFinder.Roots.Of(system), system.Environment(), shown.Scope, value, shown.Index);
        }
        catch (Exception ex) { return (false, $"PoolWatch could not read {where}: {ex.Message}"); }
        if (now is not { Kind: LinkKind.PathEntry } || now.Entry != shown.Entry || now.Target != target)
            return (false, now?.WhyNot is { } why && now.Entry == shown.Entry ? "PoolWatch did not replace the entry: " + why : ChangedSince);
        if (PathList.Replace(value.Text, shown.Index, shown.Entry, target) is not { } next) return (false, ChangedSince);
        var changed = DateTime.UtcNow;
        var record = new ChangeRecord
        {
            Id = ChangeIds.PathEntry(scope, shown.Entry, changed), Kind = RecordKind.PathEntry, ChangedUtc = changed, Via = via,
            Location = scope, Before = shown.Entry, After = target, Index = shown.Index,
        };
        try { records.Put(record); }
        catch (Exception ex) { return (false, $"PoolWatch did not change {where}, because it could not record the change to put it back later: {ex.Message}"); }
        string? error;
        try { error = store.Write(shown.Scope, AppDataLinkFinder.PathName, value, new EnvValue(next, value.Expand)); }
        catch (Exception ex) { error = ex.Message; }
        if (error is not null)
        {
            records.Remove(record);
            return (false, $"PoolWatch could not change {where}: {error}.");
        }
        Tell(store);
        log.Info($"appdata links: {ChangeWords.PathEntry(scope, shown.Entry, target)} (from {via})");
        return (true, ChangeWords.PathEntry(scope, shown.Entry, target) + ". " + LinkWords.Restart);
    }

    /// <summary>
    /// Sets the row's variables for the account, each only when it is still as shown and its folder is PoolWatch's own
    /// choice for it under the profile, outside AppData, with no link on the way; returns the ones it set.
    /// </summary>
    public (bool Ok, string Message, IReadOnlyList<EnvSetting> Set) SetVariables(LinkFinding shown, string via)
    {
        if (shown.Kind is not (LinkKind.Uv or LinkKind.Pnpm or LinkKind.Codex) || shown.EnvSettings.Count == 0) return (false, "There is nothing to set for this row.", []);
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
    /// makes it).
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

    private void Tell(IEnvironmentStore store)
    {
        try { store.Broadcast(); }
        catch (Exception ex) { log.Info("appdata links: the change was not told to running programs: " + ex.Message); }
    }

    /// <summary>
    /// Put back (Active changes, Put all back, the uninstaller), checked against the registry as it is now: a PATH entry
    /// goes back only where the folder PoolWatch wrote is still on that PATH (<see cref="PathList.PutBack"/>), a variable
    /// only while it is still what PoolWatch set it to. Otherwise the record goes and the PC is left as it is. With no
    /// store (a test's ledger, or none given), nothing is put back.
    /// </summary>
    internal static UndoResult PutBack(ChangeRecord r, IEnvironmentStore? store, ChangeRecords changes)
    {
        if (store is null) return new(false, "PoolWatch cannot put this back from here.");
        if (r.Kind == RecordKind.PathEntry)
        {
            var scope = EnvScopes.Parse(r.Location)!.Value;
            string where = ChangeWords.PathOf(r.Location);
            EnvValue? now;
            try { now = store.Read(scope, AppDataLinkFinder.PathName); }
            catch (Exception ex) { return new(false, $"PoolWatch could not read {where}: {ex.Message}"); }
            if (now is not { } value || PathList.PutBack(value.Text, r.Index!.Value, r.After!, r.Before!) is not { } next)
            {
                changes.Remove(r);
                return new(true, $"{Capital(where)} no longer holds {r.After} as PoolWatch wrote it, so there is nothing to put back.");
            }
            if (store.Write(scope, AppDataLinkFinder.PathName, value, new EnvValue(next, value.Expand)) is { } error)
                return new(false, $"PoolWatch could not put the entry back on {where}: {error}.");
            try { store.Broadcast(); } catch (Exception) { } // put back either way; programs started later read the registry
            changes.Remove(r);
            return new(true, $"Put {r.Before!.Trim()} back on {where}. " + LinkWords.Restart);
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
