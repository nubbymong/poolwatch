using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PoolWatch.Engine;

/// <summary>
/// Whether someone who is not an administrator could change a program or the folders it is in: the judgement of an
/// install folder's access rules, read only. This one source file is compiled into PoolWatch's program
/// (InstallGuard.WhyNotFrom, InstallGuard.WhyNotStartFrom) and into its launcher (Program.WhyNotStart), so both judge a copy
/// by the same rules. Every account and group that is not an administrator counts (<see cref="IsAdmin"/>), whoever is
/// signed in, except the accounts every program of which runs as administrator on this PC (<see cref="ElevatedAccounts"/>:
/// PoolWatch's program reads them from the PC's UAC settings, AlwaysElevated; its launcher, which reads no such setting,
/// counts only the account it runs as, when every program of that account runs as administrator).
/// </summary>
internal static partial class InstallGuard
{
    /// <summary>
    /// What access rules may give someone who is not an administrator before PoolWatch counts a folder or a file as one
    /// they can change. On a folder: add a file or a folder, delete what is in it; on a file: write or append to it (the
    /// same bits). On either: delete or rename it, or change who has access (write the access rules, take ownership),
    /// directly or through a generic right.
    /// </summary>
    internal const int ChangeRights =
        0x0002 /* add a file; write */ | 0x0004 /* add a folder; append */ | 0x0040 /* delete what is in it */ | 0x0001_0000 /* delete */ |
        0x0004_0000 /* write the access rules */ | 0x0008_0000 /* take ownership */ | 0x1000_0000 /* all, generic */ | 0x4000_0000 /* write, generic */;

    /// <summary>
    /// Whether <paramref name="sid"/> is held only by administrators and Windows itself: SYSTEM, Administrators,
    /// TrustedInstaller, "Local account and member of Administrators group" (UAC keeps that one only to deny in a token
    /// without administrator rights), and the accounts and groups <paramref name="alwaysElevated"/> holds: those every
    /// program of whose members runs with administrator rights on this PC by its UAC settings (<see
    /// cref="AlwaysElevated"/>: with UAC off, the group's direct members (accounts or groups), the account PoolWatch runs
    /// as when it is an administrator, and an account or group Windows finds a member through a group when asked about
    /// its SID; with the built-in Administrator's Admin Approval Mode off and this PC's account domain read, that account,
    /// while the group lists it and when PoolWatch runs as it with Administrators in its token). Every other account and
    /// group counts as one that is not an administrator: any other user's own SID (the signed-in one's, another user's,
    /// an administrator's whose programs without administrator rights hold it), every other group (Users, a local or
    /// domain group, the service accounts, app containers), and the groups UAC treats as elevated, whose members without
    /// UAC hold them in every program.
    /// </summary>
    internal static bool IsAdmin(SecurityIdentifier sid, ElevatedAccounts? alwaysElevated = null) =>
        AdminSids.Contains(sid.Value) || alwaysElevated?.Holds(sid) == true;

    private static readonly HashSet<string> AdminSids = new(StringComparer.OrdinalIgnoreCase)
    {
        // SYSTEM, Administrators, TrustedInstaller (DataRootGuard.TrustedInstaller; written out here, as the launcher
        // compiles this file without DataRootGuard), "Local account and member of Administrators group".
        "S-1-5-18", "S-1-5-32-544", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", "S-1-5-114",
    };

    /// <summary>
    /// CREATOR OWNER (in a rule only for what is made later: whoever makes it gets the rule, and who may make it is judged
    /// by the other rules) and OWNER RIGHTS (what the owner may do: the owner is judged as such).
    /// </summary>
    private static bool StandsForTheOwner(SecurityIdentifier sid) => sid.Value is "S-1-3-0" or "S-1-3-4";

    /// <summary>
    /// CREATOR GROUP: no token holds it, so a rule for it gives nothing on the folder or file itself; in what is made
    /// later it becomes the maker's primary group ("None", "Domain Users": not administrators), so where it reaches the
    /// files made in the program's own folder it counts like any other group.
    /// </summary>
    private static bool IsCreatorGroup(SecurityIdentifier sid) => sid.Value is "S-1-3-1";

    /// <summary>
    /// Pure: why someone who is not an administrator (<see cref="IsAdmin"/>) could change a folder or a file with this
    /// security descriptor, or null when only administrators can. An owner who is not an administrator (an owner may
    /// always change who has access); rules that could not be read or no access rules at all (everyone may do anything); a
    /// kind of rule PoolWatch does not know; an allowing rule that gives one of <see cref="ChangeRights"/> to anyone who is
    /// not an administrator. A rule only for what is made in a folder later is judged where it lands, since each folder
    /// below holds its own copy, except one for files in the program's own folder (<paramref name="filesInIt"/>): it
    /// reaches the program and the files next to it. Read only: nothing is applied. <paramref name="alwaysElevated"/>: the
    /// accounts that count as administrators too on this PC (<see cref="IsAdmin"/>).
    /// </summary>
    internal static string? WhoMayChange(RawSecurityDescriptor? sd, bool filesInIt = false, ElevatedAccounts? alwaysElevated = null)
    {
        if (sd is null) return "its permissions could not be read";
        if (sd.Owner is not { } owner) return "it has no owner";
        if (!IsAdmin(owner, alwaysElevated)) return $"it is owned by {owner.Value}";
        if ((sd.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 || sd.DiscretionaryAcl is null) return "it has no access rules";
        foreach (GenericAce ace in sd.DiscretionaryAcl)
        {
            bool reachesFiles = filesInIt && (ace.AceFlags & AceFlags.ObjectInherit) != 0;
            if ((ace.AceFlags & AceFlags.InheritOnly) != 0 && !reachesFiles) continue;
            if (ace is not KnownAce known) return "it has a kind of access rule PoolWatch cannot judge";
            if (known is QualifiedAce { AceQualifier: not AceQualifier.AccessAllowed }) continue; // a deny (or an audit) gives nothing
            if (StandsForTheOwner(known.SecurityIdentifier)) continue;
            if (IsCreatorGroup(known.SecurityIdentifier) && !reachesFiles) continue;
            if ((known.AccessMask & ChangeRights) != 0 && !IsAdmin(known.SecurityIdentifier, alwaysElevated)) return $"{known.SecurityIdentifier.Value} may change it";
        }
        return null;
    }

    /// <summary><see cref="WhoMayChange"/> as a yes or no.</summary>
    internal static bool LetsNonAdminsChange(RawSecurityDescriptor sd, bool filesInIt = false, ElevatedAccounts? alwaysElevated = null) =>
        WhoMayChange(sd, filesInIt, alwaysElevated) is not null;

    /// <summary>
    /// Pure over <paramref name="security"/> and <paramref name="realPath"/>: why someone who is not an administrator could
    /// change <paramref name="file"/>, or null when only an administrator can. It must be under an admin-only root
    /// (<paramref name="roots"/>); no folder from its own up to that root may let them change it (a folder above can be
    /// renamed and made again); and the file's own rules may not either, since a file moved there from a folder users may
    /// change keeps the rules it had. A folder that is not there is judged by the folder it would be made in. A file that
    /// is not there counts, since not every rule a file or folder made there later would take is judged (one its parent
    /// gives the folders made in it, say), and so does a file that is itself a link, since the folders of the file it
    /// points at are not judged. When a folder on the way is a link (a junction), the folder the program really is in must
    /// be under an admin-only root too, and every folder from it up to that root is judged the same way. Rules that cannot
    /// be read count as rules a standard user may change.
    /// </summary>
    /// <param name="security">A folder's or file's security descriptor, read only; null when it is not there; throws
    /// <see cref="LinkedFileException"/> for a file that is a link, and any other exception when it cannot be read
    /// (<see cref="ReadSecurity(string)"/>).</param>
    /// <param name="realPath">Where a folder really is, every link on the way followed (<see cref="RealPath"/>); null or an
    /// exception when that cannot be told.</param>
    internal static OpenPlace? UserWritable(string file, IReadOnlyList<string> roots, Func<string, RawSecurityDescriptor?> security,
        Func<string, string?> realPath, ElevatedAccounts? alwaysElevated = null)
    {
        string full = Path.GetFullPath(file);
        string folder = Path.GetDirectoryName(full) ?? full;
        if (!IsUnderAdminRoot(full, roots)) return new(folder, full, OpenBecause.OutsideAdminRoots);
        if (FolderChain(folder, full, roots, security, alwaysElevated) is { } open) return open;
        RawSecurityDescriptor? its;
        try { its = security(full); }
        catch (LinkedFileException) { return new(folder, full, OpenBecause.Link); }
        catch (Exception) { return new(folder, full, OpenBecause.Unreadable); }
        if (its is null) return new(folder, full, OpenBecause.NotThere);
        if (LetsNonAdminsChange(its, alwaysElevated: alwaysElevated)) return new(folder, full, OpenBecause.ProgramRules);
        // A folder on the way that is a link: judge where the program really is, and every folder above that up to its root.
        string? real;
        try { real = realPath(folder); }
        catch (Exception) { real = null; }
        if (real is null) return new(folder, full, OpenBecause.Unreadable);
        if (SamePath(real, folder)) return null;
        if (!IsUnderAdminRoot(real, roots)) return new(real, full, OpenBecause.LinkedFolder);
        return FolderChain(Path.GetFullPath(real), full, roots, security, alwaysElevated);
    }

    /// <summary>Every folder from <paramref name="folder"/> up to its admin-only root (never above it): the first that is not admin-only.</summary>
    private static OpenPlace? FolderChain(string folder, string full, IReadOnlyList<string> roots, Func<string, RawSecurityDescriptor?> security,
        ElevatedAccounts? alwaysElevated)
    {
        var under = roots.Where(r => !string.IsNullOrEmpty(r)).Select(r => Path.GetFullPath(r).TrimEnd('\\', '/')).ToList();
        bool filesInIt = true; // the nearest folder that is there holds the rules its files take
        for (string? f = folder; f is not null; f = Path.GetDirectoryName(f))
        {
            RawSecurityDescriptor? sd;
            try { sd = security(f); }
            catch (Exception) { return new(folder, full, OpenBecause.Unreadable); }
            if (sd is not null)
            {
                if (LetsNonAdminsChange(sd, filesInIt, alwaysElevated)) return new(folder, full, OpenBecause.FolderRules);
                filesInIt = false;
            }
            if (under.Any(r => f.TrimEnd('\\', '/').Equals(r, StringComparison.OrdinalIgnoreCase))) break; // up to the root, never above it
        }
        return null;
    }

    /// <summary>More than this many files and folders in PoolWatch's own folder is not its install: refused rather than all read.</summary>
    internal const int MaxEntriesInItsFolder = 5000;

    /// <summary>
    /// How deep below its own folder PoolWatch looks: its install goes two levels down (<c>amd64\</c> and
    /// <c>licenses\Microsoft.NETCore.App\</c>); a folder deeper than this counts as not its install.
    /// </summary>
    internal const int MaxDepthInItsFolder = 8;

    /// <summary>
    /// Pure over <paramref name="places"/>: the first file or folder in the program's own folder (and below it) that is
    /// not admin-only by its own rules (a DLL next to the exe is loaded by it), that is a link, or whose rules cannot be
    /// read; null when every one is admin-only. A folder in it counts with the rules it gives the files made in it.
    /// </summary>
    internal static OpenPlace? ChangeableInItsFolder(string exe, AdminPlaces places)
    {
        string full = Path.GetFullPath(exe);
        string folder = Path.GetDirectoryName(full) ?? full;
        var todo = new Stack<(string Dir, int Depth)>();
        todo.Push((folder, 0));
        int seen = 0;
        while (todo.Count > 0)
        {
            var (dir, depth) = todo.Pop();
            IReadOnlyList<FolderEntry> entries;
            try { entries = places.Contents(dir); }
            catch (Exception) { return new(folder, dir, OpenBecause.Unreadable); }
            foreach (var e in entries)
            {
                if (++seen > MaxEntriesInItsFolder) return new(folder, e.Path, OpenBecause.TooMuchInItsFolder);
                if (e.Folder && depth + 1 > MaxDepthInItsFolder) return new(folder, e.Path, OpenBecause.TooDeepInItsFolder);
                if (SamePath(e.Path, full)) continue; // the program itself: judged by UserWritable
                if (e.Link) return new(folder, e.Path, OpenBecause.LinkInItsFolder);
                RawSecurityDescriptor? sd;
                try { sd = places.Security(e.Path); }
                catch (LinkedFileException) { return new(folder, e.Path, OpenBecause.LinkInItsFolder); }
                catch (Exception) { return new(folder, e.Path, OpenBecause.Unreadable); }
                if (sd is null) continue; // gone since it was listed
                if (LetsNonAdminsChange(sd, e.Folder, places.AlwaysElevated)) return new(folder, e.Path, OpenBecause.InItsFolder);
                if (e.Folder) todo.Push((e.Path, depth + 1));
            }
        }
        return null;
    }

    private static bool SamePath(string a, string b) =>
        Path.GetFullPath(a).TrimEnd('\\', '/').Equals(Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The access rules and owner of a folder or a file, read only (nothing is changed); null when it is not there.
    /// Throws when they cannot be read, its attributes included (one an administrator may not even look at counts), and
    /// <see cref="LinkedFileException"/> for a file that is a link (a symbolic link or the like), whose rules would be
    /// read from the file it points at. A folder that is a link (a junction) is read as it is; where it leads is judged
    /// through <see cref="RealPath"/>.
    /// </summary>
    internal static RawSecurityDescriptor? ReadSecurity(string path) =>
        ReadSecurity(path, File.GetAttributes, p => new FileInfo(p).LinkTarget, ReadRules);

    /// <summary>
    /// <see cref="ReadSecurity(string)"/> over its three reads, so tests can pin which errors mean "not there" without
    /// reading any access rules: only a path, file or folder not found does; every other error is thrown.
    /// </summary>
    /// <param name="attributes">The path's own attributes (File.GetAttributes, which does not follow a link).</param>
    /// <param name="linkTarget">Where a link points (FileSystemInfo.LinkTarget: null for a reparse point that is no link).</param>
    /// <param name="rules">The access rules of a folder (true) or a file (false).</param>
    internal static RawSecurityDescriptor? ReadSecurity(string path, Func<string, FileAttributes> attributes, Func<string, string?> linkTarget,
        Func<string, bool, RawSecurityDescriptor> rules)
    {
        try
        {
            FileAttributes a = attributes(path);
            bool isFolder = (a & FileAttributes.Directory) != 0;
            if (!isFolder && (a & FileAttributes.ReparsePoint) != 0 && linkTarget(path) is not null) throw new LinkedFileException(path);
            return rules(path, isFolder);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException) { return null; }
    }

    private static RawSecurityDescriptor ReadRules(string path, bool isFolder)
    {
        const AccessControlSections parts = AccessControlSections.Access | AccessControlSections.Owner;
        FileSystemSecurity rules = isFolder ? new DirectoryInfo(path).GetAccessControl(parts) : new FileInfo(path).GetAccessControl(parts);
        return new RawSecurityDescriptor(rules.GetSecurityDescriptorBinaryForm(), 0);
    }

    /// <summary>
    /// The files and folders right in <paramref name="dir"/> (read only, nothing followed): each with whether it is a
    /// folder and whether it is a link (a reparse point that points elsewhere: a symbolic link, a junction, a mount point).
    /// Throws when the folder cannot be listed. At most one more than <see cref="MaxEntriesInItsFolder"/> are returned.
    /// </summary>
    internal static IReadOnlyList<FolderEntry> FolderEntries(string dir)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = false, ReturnSpecialDirectories = false };
        return new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options).Take(MaxEntriesInItsFolder + 1)
            .Select(i => new FolderEntry(i.FullName, (i.Attributes & FileAttributes.Directory) != 0,
                (i.Attributes & FileAttributes.ReparsePoint) != 0 && i.LinkTarget is not null))
            .ToList();
    }

    /// <summary>Pure, testable: is <paramref name="exePath"/> inside one of the admin-only <paramref name="adminRoots"/>?</summary>
    public static bool IsUnderAdminRoot(string exePath, IEnumerable<string> adminRoots)
    {
        string full = Path.GetFullPath(exePath);
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var root in adminRoots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full.Equals(r, cmp) || full.StartsWith(r + Path.DirectorySeparatorChar, cmp)) return true;
        }
        return false;
    }
}

/// <summary>Why someone who is not an administrator could change a program (<see cref="InstallGuard.UserWritable"/>).</summary>
internal enum OpenBecause
{
    /// <summary>It is not under an admin-only root (Program Files).</summary>
    OutsideAdminRoots,
    /// <summary>The rules of its folder, or of a folder above it up to the root, let a standard user change what is in it.</summary>
    FolderRules,
    /// <summary>The program file's own rules let a standard user change it.</summary>
    ProgramRules,
    /// <summary>The rules of the program, or of a folder on the way, could not be read.</summary>
    Unreadable,
    /// <summary>The program is not there, so not every rule a file made there would take is known.</summary>
    NotThere,
    /// <summary>The program is itself a link, and the folders of the file it points at are not judged.</summary>
    Link,
    /// <summary>A folder on the way is a link, and the folder it leads to is not under an admin-only root.</summary>
    LinkedFolder,
    /// <summary>A file or folder in the program's own folder (<see cref="OpenPlace.Program"/>) is not admin-only by its own rules.</summary>
    InItsFolder,
    /// <summary>A file or folder in the program's own folder (<see cref="OpenPlace.Program"/>) is a link.</summary>
    LinkInItsFolder,
    /// <summary>The program's own folder holds more than PoolWatch checks.</summary>
    TooMuchInItsFolder,
    /// <summary>The program's own folder holds folders deeper than PoolWatch checks.</summary>
    TooDeepInItsFolder,
}

/// <summary>A program a standard user could change: its folder, the program (or the file in its folder that is not admin-only), and why.</summary>
internal sealed record OpenPlace(string Folder, string Program, OpenBecause Because);

/// <summary>A file or folder in a folder, as <see cref="InstallGuard.FolderEntries"/> lists it.</summary>
internal sealed record FolderEntry(string Path, bool Folder, bool Link);

/// <summary>A file that is a link, from <see cref="InstallGuard.ReadSecurity(string)"/>: its own rules are not read.</summary>
internal sealed class LinkedFileException(string path) : IOException($"{path} is a link to another file");

/// <summary>
/// The account this process runs as, as UAC made its token: its TOKEN_ELEVATION_TYPE (<see cref="ElevationDefault"/>, <see
/// cref="ElevationFull"/>, <see cref="ElevationLimited"/>), and whether it is a sign-in in person (<see cref="Interactive"/>).
/// UAC splits the token of an administrator's sign-in in person, unless UAC is off or the account is the built-in
/// Administrator without Admin Approval Mode. A batch, service or network sign-in's token (a task set to run whether the
/// user is signed in or not, a service) can hold Administrators unsplit even for an account whose sign-in in person UAC
/// splits, so a token that is not split (<see cref="ElevationDefault"/>) tells that every program of the account runs as
/// administrator only for a sign-in in person (<see cref="ElevatedAccounts.OfThisProcess"/>). Also this PC's account
/// domain, whose RID 500 is the built-in Administrator. Read only.
/// </summary>
internal static class UnelevatedAccount
{
    /// <summary>TOKEN_ELEVATION_TYPE values (winnt.h); 0 here means it could not be read.</summary>
    internal const int ElevationDefault = 1, ElevationFull = 2, ElevationLimited = 3;

    /// <summary>
    /// INTERACTIVE (S-1-5-4), the group Windows puts in the token of every sign-in in person, at the PC or by Remote
    /// Desktop, and in no batch (S-1-5-3), service (S-1-5-6) or network (S-1-5-2) sign-in's (Microsoft Learn, "Well-known
    /// SIDs").
    /// </summary>
    internal static readonly SecurityIdentifier Interactive = new(WellKnownSidType.InteractiveSid, null);

    /// <summary>This process's TOKEN_ELEVATION_TYPE (<see cref="ElevationDefault"/>, <see cref="ElevationFull"/>, <see cref="ElevationLimited"/>); 0 when it cannot be read.</summary>
    public static unsafe int ElevationTypeOfThisProcess()
    {
        using var me = WindowsIdentity.GetCurrent();
        int type;
        return GetTokenInformation(me.Token, 18 /* TokenElevationType */, &type, sizeof(int), out _) ? type : 0;
    }

    /// <summary>
    /// This PC's account domain (NetUserModalsGet, level 2, of this PC's own account database: no domain controller is
    /// asked), whose RID 500 is the built-in Administrator; null when it cannot be read.
    /// </summary>
    internal static SecurityIdentifier? ReadAccountDomain()
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (NetUserModalsGet(null, 2, out IntPtr buffer) != 0 || buffer == IntPtr.Zero) return null;
        try
        {
            IntPtr sid = Marshal.ReadIntPtr(buffer, IntPtr.Size); // USER_MODALS_INFO_2: LPWSTR name, PSID domain id
            return sid == IntPtr.Zero ? null : new SecurityIdentifier(sid);
        }
        catch (Exception) { return null; }
        finally { NetApiBufferFree(buffer); }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool GetTokenInformation(IntPtr token, int infoClass, void* buffer, int length, out int returnLength);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int NetUserModalsGet(string? serverName, int level, out IntPtr buffer);

    [DllImport("netapi32.dll", ExactSpelling = true)]
    private static extern int NetApiBufferFree(IntPtr buffer);
}

/// <summary>
/// UAC's two switches (HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System): <paramref name="ForAll"/> is
/// EnableLUA (Admin Approval Mode for every administrator; Windows' default on), <paramref name="ForBuiltInAdministrator"/>
/// is FilterAdministratorToken (Admin Approval Mode for the built-in Administrator; Windows' default off). Read by
/// PoolWatch's program (AlwaysElevated) and its launcher (<see cref="ElevatedAccounts.OfThisProcess"/>) alike.
/// </summary>
internal sealed record UacSwitches(bool ForAll, bool ForBuiltInAdministrator)
{
    /// <summary>
    /// Pure: UAC's switches from the two registry values as read (null when a value is not there). A value that is not
    /// there has Windows' default (EnableLUA on, FilterAdministratorToken off); one that is not a number counts as on,
    /// which fails closed for both (fewer accounts count).
    /// </summary>
    internal static UacSwitches Of(object? enableLua, object? filterAdministratorToken) =>
        new(Dword(enableLua, 1) != 0, Dword(filterAdministratorToken, 0) != 0);

    /// <summary>A DWORD value as read, <paramref name="fallback"/> when it is not there, and on (1) when it is not a number.</summary>
    private static int Dword(object? value, int fallback) => value switch { null => fallback, int i => i, _ => 1 };

    /// <summary>UAC's two switches (<see cref="Of"/>); null when the key cannot be read.</summary>
    internal static UacSwitches? Read()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
            if (k is null) return null;
            return Of(k.GetValue("EnableLUA"), k.GetValue("FilterAdministratorToken"));
        }
        catch (Exception) { return null; }
    }
}

/// <summary>
/// The accounts and groups that count as administrators too on this PC (AlwaysElevated, in PoolWatch's program; <see
/// cref="OfThisProcess"/> in its launcher): the ones known up front (<see cref="Known"/>), and, with UAC off, any other
/// account or group Windows finds to be a member of Administrators from its SID alone, asked once per SID and only when
/// the check meets it (<see cref="Holds"/>).
/// </summary>
internal sealed class ElevatedAccounts
{
    /// <summary>No account: every account but the well-known administrator ones counts as one that is not an administrator.</summary>
    public static ElevatedAccounts None { get; } = new([], null);

    /// <summary>Exactly these accounts (tests).</summary>
    public static ElevatedAccounts Only(params SecurityIdentifier[] sids) => new(sids, null);

    /// <summary>
    /// The account this process runs as, when every program of that account runs as administrator (<see cref="OfToken"/>),
    /// so a folder setup made as that account, which it owns, still counts as admin-only to the launcher (PoolWatch's
    /// program counts more accounts: AlwaysElevated); and whether a logon task of that account needs highest privileges
    /// (Ui.LogonTask.HighestNeeded). None otherwise, and none when the token cannot be read: then the check fails closed.
    /// Read only.
    /// </summary>
    public static ElevatedAccounts OfThisProcess()
    {
        try
        {
            using var me = WindowsIdentity.GetCurrent();
            var token = new WindowsPrincipal(me);
            return OfToken(me.User, UnelevatedAccount.ElevationTypeOfThisProcess(), token.IsInRole(WindowsBuiltInRole.Administrator),
                token.IsInRole(UnelevatedAccount.Interactive), UacSwitches.Read, UnelevatedAccount.ReadAccountDomain);
        }
        catch (Exception) { return None; }
    }

    /// <summary>
    /// Pure: <see cref="OfThisProcess"/> for a token of account <paramref name="user"/>, TOKEN_ELEVATION_TYPE <paramref
    /// name="elevationType"/> (0: not read), that holds Administrators or not (<paramref name="administrator"/>) and that
    /// is a sign-in in person or not (<paramref name="interactive"/>): that account only for an administrator's token UAC
    /// did not split, and then, for a sign-in in person, at once (UAC would have split it); for a batch, service or network
    /// sign-in, whose token can be unsplit even where UAC splits the account's sign-in in person, only when UAC's settings
    /// (<paramref name="uac"/>, read only then) say every program of the account runs as administrator: UAC is off, or the
    /// account is this PC's built-in Administrator (RID 500 of <paramref name="accountDomain"/>, read only then) and its
    /// Admin Approval Mode is off. Settings or a domain that cannot be read (null) count no one.
    /// </summary>
    internal static ElevatedAccounts OfToken(SecurityIdentifier? user, int elevationType, bool administrator, bool interactive,
        Func<UacSwitches?> uac, Func<SecurityIdentifier?> accountDomain)
    {
        if (user is null || elevationType != UnelevatedAccount.ElevationDefault || !administrator) return None;
        if (interactive) return Only(user);
        if (uac() is not { } switches) return None;
        if (!switches.ForAll) return Only(user);
        return !switches.ForBuiltInAdministrator && accountDomain() is { } domain && user == new SecurityIdentifier(WellKnownSidType.AccountAdministratorSid, domain)
            ? Only(user) : None;
    }

    private readonly Func<SecurityIdentifier, bool?>? _memberBySid;
    private readonly Dictionary<SecurityIdentifier, bool> _asked = new();

    internal ElevatedAccounts(IEnumerable<SecurityIdentifier> known, Func<SecurityIdentifier, bool?>? memberBySid)
    {
        Known = new HashSet<SecurityIdentifier>(known);
        _memberBySid = memberBySid;
    }

    /// <summary>The accounts and groups known up front: the group's direct members and the others decided from the settings alone.</summary>
    public IReadOnlySet<SecurityIdentifier> Known { get; }

    /// <summary>
    /// Whether <paramref name="sid"/> counts as an administrator's own: one of <see cref="Known"/>, or a SID of the kinds
    /// asked about (<see cref="IsAccount"/>) that Windows finds a member of Administrators when asked; one it cannot tell
    /// about, and a question that fails, do not count.
    /// </summary>
    public bool Holds(SecurityIdentifier sid)
    {
        if (Known.Contains(sid)) return true;
        if (_memberBySid is null || !IsAccount(sid)) return false;
        lock (_asked)
        {
            if (!_asked.TryGetValue(sid, out bool member))
            {
                try { member = _memberBySid(sid) == true; }
                catch (Exception) { member = false; }
                _asked[sid] = member;
            }
            return member;
        }
    }

    /// <summary>
    /// The SIDs Windows is asked about: those of the kinds an account has, a local or domain one (S-1-5-21-...) or a
    /// Microsoft Entra ID one (S-1-12-1-...). A local or domain group's SID has that form too: it is asked about the same
    /// way and counts only when Windows' answer says it is a member of Administrators (this PC's "None" group, which is
    /// not, is not counted). Well-known groups (S-1-5-32-..., S-1-5-11 and the like) are never asked about.
    /// </summary>
    internal static bool IsAccount(SecurityIdentifier sid) =>
        sid.Value.StartsWith("S-1-5-21-", StringComparison.Ordinal) || sid.Value.StartsWith("S-1-12-1-", StringComparison.Ordinal);
}

/// <summary>
/// Where an elevated PoolWatch may run a program from: the admin-only roots, how a folder's or file's access rules are
/// read (read only), where a folder really is (links followed, read only), and what a folder holds (read only, nothing
/// followed), and the accounts that count as administrators too on this PC (<see cref="AlwaysElevated"/>). Tests pass
/// descriptors made in memory, listings and accounts of their own, so no test reads or changes the access rules of a
/// folder or file on the PC.
/// </summary>
internal sealed partial record AdminPlaces(IReadOnlyList<string> Roots, Func<string, RawSecurityDescriptor?> Security, Func<string, string?> RealPath,
    Func<string, IReadOnlyList<FolderEntry>> Contents)
{
    /// <summary>The accounts that count as administrators too (<see cref="InstallGuard.IsAdmin"/>); none unless set.</summary>
    public ElevatedAccounts AlwaysElevated { get; init; } = ElevatedAccounts.None;

    /// <summary>Why someone who is not an administrator could change <paramref name="file"/>, or null when only an administrator can.</summary>
    public OpenPlace? UserWritable(string file) => InstallGuard.UserWritable(file, Roots, Security, RealPath, AlwaysElevated);

    /// <summary>The first file or folder in <paramref name="exe"/>'s folder that is not admin-only (<see cref="InstallGuard.ChangeableInItsFolder"/>).</summary>
    public OpenPlace? ChangeableInItsFolder(string exe) => InstallGuard.ChangeableInItsFolder(exe, this);
}
