using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PoolWatch.Engine;

/// <summary>Small Windows-only helpers kept apart from the cross-platform Core.</summary>
internal static class Interop
{
    /// <summary>
    /// S3 (review rounds 2 and 3): the data root's security descriptor, so an elevated PoolWatch's files are out of reach
    /// of an unelevated process. Administrators (owner) and SYSTEM get full control; nobody else may write. The data holds
    /// command lines, so it is not readable by all local Users: only the account PoolWatch runs as (<paramref name="reader"/>,
    /// the owner) keeps read + execute, because there is an unelevated reader: PoolWatch opens incident, report and export
    /// folders in the shell (Explorer and the apps it starts run unelevated). Protected: nothing inherited from %ProgramData%.
    /// Applied at creation, or once when a check finds it changed (<see cref="DataRootGuard"/>). Pure: built in memory
    /// (unit-tested). The reader is granted only when it is one account: a group (Users, Authenticated Users, Everyone),
    /// Administrators or SYSTEM is never added as a reader.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static DirectorySecurity DataRootSecurity(SecurityIdentifier? reader)
    {
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.SetOwner(admins);
        sec.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        if (reader is not null && IsOneAccount(reader))
            sec.AddAccessRule(new FileSystemAccessRule(reader, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        return sec;
    }

    /// <summary>
    /// A single account: a local, domain or Microsoft account (S-1-5-21-...) or an Entra ID account (S-1-12-1-...), and not
    /// one of the well-known domain groups (RIDs 512-520). Built-in principals (Users, Everyone, SYSTEM, ...) are not.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static bool IsOneAccount(SecurityIdentifier sid) =>
        (sid.Value.StartsWith("S-1-5-21-", StringComparison.Ordinal) && !IsDomainGroupRid(sid)) ||
        sid.Value.StartsWith("S-1-12-1-", StringComparison.Ordinal);

    /// <summary>The well-known domain group RIDs (Domain Users 513, Domain Guests 514, Domain Computers 515, Domain Admins 512, ...).</summary>
    private static bool IsDomainGroupRid(SecurityIdentifier sid)
    {
        string v = sid.Value;
        int dash = v.LastIndexOf('-');
        return dash > 0 && int.TryParse(v[(dash + 1)..], out int rid) && rid is >= 512 and <= 520;
    }

    /// <summary>The account the current process runs as (the owner, for the tray app); null when it cannot be read.</summary>
    [SupportedOSPlatform("windows")]
    public static SecurityIdentifier? CurrentUser()
    {
        try { using var id = WindowsIdentity.GetCurrent(); return id.User; }
        catch { return null; }
    }

    /// <summary>
    /// Addendum 2 C / Part 3 review: a symbol folder an elevated PoolWatch may read PDBs from: it exists, is on a local drive
    /// (not a mapped network drive) and is admin-only by its ACL (read only: nothing is changed). Any failure counts as no.
    /// </summary>
    public static bool UsableSymbolFolder(string dir)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            if (!Directory.Exists(dir) || Collectors.VolumeIdentity.IsRemoteDrive(dir)) return false;
            var sec = new DirectoryInfo(dir).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            string owner = sec.GetOwner(typeof(SecurityIdentifier))?.Value ?? "";
            var rules = sec.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
                .Select(r => new Core.Util.FolderRule(r.IdentityReference.Value, r.AccessControlType == AccessControlType.Allow, (int)r.FileSystemRights));
            return Core.Util.SymbolPaths.AdminOnly(owner, rules);
        }
        catch { return false; }
    }
}
