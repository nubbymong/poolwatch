using System.Security.AccessControl;
using System.Security.Principal;

namespace PoolWatch.Engine;

/// <summary>
/// S4 (review): PoolWatch runs elevated (requireAdministrator) and starts at logon, so it must run from an admin-only
/// folder. If an elevated interactive run is launched from a user-writable location (the OneDrive Desktop, Downloads,
/// %LOCALAPPDATA%, %TEMP%, ...), an unelevated user could replace the executable. This refuses that run and says how to
/// install. Review round 2: every elevated mode is checked (tray, headless, self-test, the services / tasks / mark steps);
/// only the CI UI smoke, the token guard's own helper and an explicit <c>--allow-any-location</c> (CI, development) are
/// exempt (Program.Main). Since the release no longer self-extracts native DLLs into %TEMP% (PoolWatch.csproj), the DLLs
/// next to the exe are judged by their folder's rules, those for the files made in it included (their own rules are not
/// read). A program counts as admin-only by its place (Program Files) and by the access rules of its folders and of the
/// file itself, read only: one there that a standard user could change, whose rules cannot be read, that is not there
/// or that is itself a link is refused the same way (<see cref="UserWritable"/>). The same judgement decides when
/// putting back a clear task of your own needs your yes (ClearTaskReplacer).
/// </summary>
internal static class InstallGuard
{
    public static bool IsElevated
    {
        get { try { using var id = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); } catch { return false; } }
    }

    /// <summary>
    /// Whether this run is checked (S4, review round 2): every mode except the CI UI smoke, the token guard's own hidden
    /// helper (started by a PoolWatch that passed the check, from its own path) and an explicit --allow-any-location.
    /// </summary>
    public static bool Applies(Headless.CommandLineOptions opt) => opt.UiSmokeSeconds is null && opt.TokenLeakTest is null && !opt.AllowAnyLocation;

    /// <summary>
    /// The admin-only roots an elevated PoolWatch may run from: %ProgramFiles% and %ProgramFiles(x86)% only. Review round 3:
    /// not %WINDIR%, where some folders are writable by every user (Windows\Temp, Windows\Tasks, Windows\tracing, ...).
    /// </summary>
    internal static IEnumerable<string> AdminRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    }

    /// <summary>Whether this exe path is inside an admin-only install root (<see cref="AdminRoots"/>), by its location only.</summary>
    public static bool IsTrustedInstallPath(string exePath) => IsUnderAdminRoot(exePath, AdminRoots());

    /// <summary>True when this elevated run is from a user-writable folder; <paramref name="message"/> says what to do.</summary>
    public static bool RefuseElevatedFromUserWritable(out string message)
    {
        message = "";
        if (!OperatingSystem.IsWindows() || !IsElevated) return false;
        string? exe = Environment.ProcessPath;
        if (exe is null) return false;
        message = WhyNotFrom(exe, AdminPlaces.OfThisPc()) ?? "";
        return message.Length > 0;
    }

    /// <summary>
    /// Pure over <paramref name="places"/>: why an elevated PoolWatch must not run from <paramref name="exe"/> (a folder
    /// outside the admin-only roots, a folder or file whose access rules let a standard user change it or could not be
    /// read, or an exe that is a link), or null when it may.
    /// </summary>
    internal static string? WhyNotFrom(string exe, AdminPlaces places)
    {
        string full = Path.GetFullPath(exe);
        if (places.UserWritable(full) is not { } open) return null;
        string start = "PoolWatch runs as administrator and must not run from a folder you can write to without administrator " +
                       "rights (an attacker could replace it there).\n\n" +
                       $"It is at:\n{full}\n\n";
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string reinstall = $"Uninstall PoolWatch, delete that folder, and install it again to {programFiles}\\PoolWatch: setup gives a new " +
                           "folder and its files the permissions of Program Files.";
        return open.Because switch
        {
            OpenBecause.OutsideAdminRoots =>
                start + $"Install it to {programFiles}\\PoolWatch and start it from there (the logon task should point there too).",
            OpenBecause.ProgramRules =>
                start + $"The permissions of {full} let users without administrator rights change it (a file moved there keeps the " +
                $"permissions it had). The folder is {open.Folder}. " + reinstall,
            OpenBecause.Unreadable or OpenBecause.NotThere => // not there: the running exe, renamed or moved since it started
                start + $"PoolWatch could not read the permissions of {full}, {open.Folder} or a folder above it, so it cannot tell " +
                "whether users without administrator rights may change them. " + reinstall,
            OpenBecause.Link =>
                start + $"{full} is a link to a file elsewhere, so PoolWatch cannot check the folders that file is in. " + reinstall,
            _ => start + $"The permissions of {open.Folder} or a folder above it let users without administrator rights change what is " +
                 "in it. " + reinstall,
        };
    }

    /// <summary>
    /// What access rules may give a standard user before PoolWatch counts a folder or a file as one they can change. On a
    /// folder: add a file or a folder, delete what is in it; on a file: write or append to it (the same bits). On either:
    /// delete or rename it, or change who has access (write the access rules, take ownership), directly or through a
    /// generic right.
    /// </summary>
    internal const int ChangeRights =
        0x0002 /* add a file; write */ | 0x0004 /* add a folder; append */ | 0x0040 /* delete what is in it */ | 0x0001_0000 /* delete */ |
        0x0004_0000 /* write the access rules */ | 0x0008_0000 /* take ownership */ | 0x1000_0000 /* all, generic */ | 0x4000_0000 /* write, generic */;

    /// <summary>
    /// Pure: whether a folder's or a file's security descriptor lets one of <paramref name="standard"/> (<see
    /// cref="StandardUsers"/>) change it: an allowing rule that gives one of <see cref="ChangeRights"/> to one of them; an
    /// owner among them (an owner may always change who has access); no access rules at all (everyone may do anything);
    /// or a kind of rule PoolWatch does not know. A rule only for what is made in a folder later is judged where it
    /// lands, since each folder below holds its own copy, except one for files in the program's own folder
    /// (<paramref name="filesInIt"/>): it reaches the program and the files next to it. Read only: nothing is applied.
    /// </summary>
    internal static bool LetsStandardUsersChange(RawSecurityDescriptor sd, IReadOnlySet<SecurityIdentifier> standard, bool filesInIt = false)
    {
        if (sd.Owner is { } owner && standard.Contains(owner)) return true;
        if ((sd.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 || sd.DiscretionaryAcl is null) return true;
        foreach (GenericAce ace in sd.DiscretionaryAcl)
        {
            if ((ace.AceFlags & AceFlags.InheritOnly) != 0 && !(filesInIt && (ace.AceFlags & AceFlags.ObjectInherit) != 0)) continue;
            if (ace is not KnownAce known) return true; // a kind of rule PoolWatch cannot judge
            if (known is QualifiedAce { AceQualifier: not AceQualifier.AccessAllowed }) continue; // a deny (or an audit) gives nothing
            if (standard.Contains(known.SecurityIdentifier) && (known.AccessMask & ChangeRights) != 0) return true;
        }
        return false;
    }

    /// <summary>
    /// Who counts as a standard user: Everyone, Authenticated Users, Users, INTERACTIVE, BATCH, LOCAL, CONSOLE LOGON,
    /// REMOTE INTERACTIVE LOGON, This Organization and Local account (every sign-in of a standard user, at the PC or as a
    /// scheduled task of theirs, holds some of them), and the signed-in account's own SID and groups as <paramref
    /// name="account"/> gives them (null: none of them). What may never count does not, even as that account (<see
    /// cref="NeverStandard"/>).
    /// </summary>
    internal static IReadOnlySet<SecurityIdentifier> StandardUsers(UnelevatedAccount? account)
    {
        var set = new HashSet<SecurityIdentifier>(AnySignIn.Select(s => new SecurityIdentifier(s)));
        if (account is not null)
            foreach (var sid in account.User is { } user ? account.Groups.Prepend(user) : account.Groups)
                if (!NeverStandard(sid)) set.Add(sid);
        return set;
    }

    /// <summary>
    /// Everyone, Authenticated Users, Users, INTERACTIVE, BATCH, LOCAL, CONSOLE LOGON, REMOTE INTERACTIVE LOGON, This
    /// Organization, Local account.
    /// </summary>
    private static readonly string[] AnySignIn =
        ["S-1-1-0", "S-1-5-11", "S-1-5-32-545", "S-1-5-4", "S-1-5-3", "S-1-2-0", "S-1-2-1", "S-1-5-14", "S-1-5-15", "S-1-5-113"];

    /// <summary>
    /// Whether <paramref name="sid"/> is never a standard user, even as the account PoolWatch runs for: SYSTEM, LOCAL
    /// SERVICE, NETWORK SERVICE, TrustedInstaller, "Local account and member of Administrators group", and the groups UAC
    /// treats as elevated (a member gets a split token, whose half without administrator rights holds them only to deny):
    /// Administrators, Power Users, Account Operators, Server Operators, Print Operators, Backup Operators, RAS Servers,
    /// Network Configuration Operators, and a domain's Domain Admins, Domain Controllers, Cert Publishers, Schema Admins,
    /// Enterprise Admins and Group Policy Creator Owners. Four more of UAC's groups still count: Pre-Windows 2000
    /// Compatible Access (it often holds Authenticated Users or Everyone), Cryptographic Operators, and a domain's
    /// Read-Only Domain Controllers and Enterprise Read-Only Domain Controllers. A rule for one of them is refused, so
    /// counting them only fails closed.
    /// </summary>
    internal static bool NeverStandard(SecurityIdentifier sid)
    {
        if (NeverStandardSids.Contains(sid.Value)) return true;
        string[] parts = sid.Value.Split('-'); // a domain's group: S-1-5-21-<the domain's three numbers>-<the group's>
        return parts is [_, _, "5", "21", _, _, _, var group] && ElevatedDomainGroups.Contains(group);
    }

    private static readonly HashSet<string> NeverStandardSids = new(StringComparer.OrdinalIgnoreCase)
    {
        "S-1-5-18", "S-1-5-19", "S-1-5-20", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", "S-1-5-114",
        "S-1-5-32-544", "S-1-5-32-547", "S-1-5-32-548", "S-1-5-32-549", "S-1-5-32-550", "S-1-5-32-551", "S-1-5-32-553", "S-1-5-32-556",
    };

    /// <summary>Domain Admins, Domain Controllers, Cert Publishers, Schema Admins, Enterprise Admins, Group Policy Creator Owners.</summary>
    private static readonly string[] ElevatedDomainGroups = ["512", "516", "517", "518", "519", "520"];

    /// <summary>
    /// Pure over <paramref name="security"/>: why a standard user (<paramref name="standard"/>) could change
    /// <paramref name="file"/>, or null when only an administrator can. It must be under an admin-only root (<paramref
    /// name="roots"/>); no folder from its own up to that root may let a standard user change it (a folder above can be
    /// renamed and made again); and the file's own rules may not either, since a file moved there from a folder users
    /// may change keeps the rules it had. A folder that is not there is judged by the folder it would be made in. A file
    /// that is not there counts, since not every rule a file or folder made there later would take is judged (one its
    /// parent gives the folders made in it, say), and so does a file that is itself a link, since the folders of the file
    /// it points at are not judged. Rules that cannot be read count as rules a standard user may change.
    /// </summary>
    /// <param name="security">A folder's or file's security descriptor, read only; null when it is not there; throws
    /// <see cref="LinkedFileException"/> for a file that is a link, and any other exception when it cannot be read
    /// (<see cref="ReadSecurity(string)"/>).</param>
    internal static OpenPlace? UserWritable(string file, IReadOnlyList<string> roots, Func<string, RawSecurityDescriptor?> security,
        IReadOnlySet<SecurityIdentifier> standard)
    {
        string full = Path.GetFullPath(file);
        string folder = Path.GetDirectoryName(full) ?? full;
        if (!IsUnderAdminRoot(full, roots)) return new(folder, full, OpenBecause.OutsideAdminRoots);
        var under = roots.Where(r => !string.IsNullOrEmpty(r)).Select(r => Path.GetFullPath(r).TrimEnd('\\', '/')).ToList();
        bool filesInIt = true; // the nearest folder that is there holds the rules its files take
        for (string? f = folder; f is not null; f = Path.GetDirectoryName(f))
        {
            RawSecurityDescriptor? sd;
            try { sd = security(f); }
            catch (Exception) { return new(folder, full, OpenBecause.Unreadable); }
            if (sd is not null)
            {
                if (LetsStandardUsersChange(sd, standard, filesInIt)) return new(folder, full, OpenBecause.FolderRules);
                filesInIt = false;
            }
            if (under.Any(r => f.TrimEnd('\\', '/').Equals(r, StringComparison.OrdinalIgnoreCase))) break; // up to the root, never above it
        }
        RawSecurityDescriptor? its;
        try { its = security(full); }
        catch (LinkedFileException) { return new(folder, full, OpenBecause.Link); }
        catch (Exception) { return new(folder, full, OpenBecause.Unreadable); }
        if (its is null) return new(folder, full, OpenBecause.NotThere);
        return LetsStandardUsersChange(its, standard) ? new(folder, full, OpenBecause.ProgramRules) : null;
    }

    /// <summary>
    /// The access rules and owner of a folder or a file, read only (nothing is changed); null when it is not there.
    /// Throws when they cannot be read, its attributes included (one an administrator may not even look at counts), and
    /// <see cref="LinkedFileException"/> for a file that is a link (a symbolic link or the like), whose rules would be
    /// read from the file it points at. A folder that is a link (a junction) is read as it is.
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

/// <summary>Why a standard user could change a program (<see cref="InstallGuard.UserWritable"/>).</summary>
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
}

/// <summary>A program a standard user could change: its folder, the program, and why.</summary>
internal sealed record OpenPlace(string Folder, string Program, OpenBecause Because);

/// <summary>A file that is a link, from <see cref="InstallGuard.ReadSecurity(string)"/>: its own rules are not read.</summary>
internal sealed class LinkedFileException(string path) : IOException($"{path} is a link to another file");

/// <summary>
/// The signed-in account as a standard user's program may share it: its SID, null when none of its programs runs without
/// administrator rights, and the groups of its token that give access (a group there only to deny, such as
/// Administrators in a split token, gives nothing). <see cref="InstallGuard.StandardUsers"/> leaves out those that may
/// never count.
/// </summary>
internal sealed record UnelevatedAccount(SecurityIdentifier? User, IReadOnlyList<SecurityIdentifier> Groups)
{
    /// <summary>TOKEN_ELEVATION_TYPE values (winnt.h); 0 here means it could not be read.</summary>
    internal const int ElevationDefault = 1, ElevationFull = 2, ElevationLimited = 3;

    /// <summary>
    /// Pure: the account as a standard user's program may share it, from this process's token. An elevated token split
    /// by UAC (<see cref="ElevationFull"/>) has a linked token without administrator rights: that one; when it cannot be
    /// read, this token's own user and groups. A token that is not an administrator's is that one itself (<paramref
    /// name="self"/>). An administrator's token with no split (UAC off, or the built-in Administrator) has none: every
    /// program of that account runs as administrator, so its own SID is no way in, but its groups still are, since
    /// standard users may be in them too (Domain Users, say). An administrator's token whose elevation type could not be
    /// read counts as a split one. Either way the groups UAC treats as elevated never count (<see
    /// cref="InstallGuard.NeverStandard"/>).
    /// </summary>
    internal static UnelevatedAccount Pick(int elevationType, bool administrator, UnelevatedAccount self, Func<UnelevatedAccount?> linked) =>
        elevationType == ElevationFull || (elevationType == 0 && administrator) ? linked() ?? self
        : administrator ? self with { User = null }
        : self;

    /// <summary>This process's (<see cref="Pick"/>): reads its token and, for a split token, the linked one; nothing is changed.</summary>
    public static unsafe UnelevatedAccount? OfThisProcess()
    {
        using var me = WindowsIdentity.GetCurrent();
        if (Of(me) is not { } self) return null;
        int type;
        if (!Native.NativeMethods.GetTokenInformation(me.Token, 18 /* TokenElevationType */, &type, sizeof(int), out _)) type = 0;
        bool administrator = new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator);
        return Pick(type, administrator, self, () => Linked(me.Token));
    }

    /// <summary>The account of the token linked to <paramref name="token"/> (TokenLinkedToken), or null when it cannot be read.</summary>
    private static unsafe UnelevatedAccount? Linked(IntPtr token)
    {
        IntPtr linked;
        if (!Native.NativeMethods.GetTokenInformation(token, 19 /* TokenLinkedToken */, &linked, IntPtr.Size, out _)) return null;
        try { using var id = new WindowsIdentity(linked); return Of(id); }
        catch (Exception) { return null; }
        finally { Native.NativeMethods.CloseHandle(linked); }
    }

    /// <summary>A token's user and its enabled groups (WindowsIdentity.Groups leaves out disabled, deny-only and logon groups).</summary>
    private static UnelevatedAccount? Of(WindowsIdentity id) =>
        id.User is { } user ? new(user, id.Groups?.OfType<SecurityIdentifier>().ToList() ?? []) : null;
}

/// <summary>
/// Where an elevated PoolWatch may run a program from: the admin-only roots, how a folder's or file's access rules are
/// read (read only), and the signed-in account as its programs without administrator rights run (<see
/// cref="InstallGuard.UserWritable"/>). Tests pass descriptors made in memory, so no test reads or changes the access
/// rules of a folder or file on the PC.
/// </summary>
internal sealed record AdminPlaces(IReadOnlyList<string> Roots, Func<string, RawSecurityDescriptor?> Security, UnelevatedAccount? Account)
{
    /// <summary>This PC's: Program Files, the access rules as they are, the account PoolWatch runs for.</summary>
    public static AdminPlaces OfThisPc() => new(InstallGuard.AdminRoots().ToList(), InstallGuard.ReadSecurity, UnelevatedAccount.OfThisProcess());

    /// <summary>Why a standard user could change <paramref name="file"/>, or null when only an administrator can.</summary>
    public OpenPlace? UserWritable(string file) => InstallGuard.UserWritable(file, Roots, Security, InstallGuard.StandardUsers(Account));
}
