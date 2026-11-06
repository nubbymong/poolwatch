using System.Runtime.InteropServices;
using System.Security.Principal;

namespace PoolWatch.Engine;

/// <summary>
/// The accounts and groups every program of whose members runs with administrator rights on this PC, by its User
/// Account Control settings, so that their own SID is no way in for the admin-only check (<see
/// cref="InstallGuard.IsAdmin"/>).
/// With UAC off ("Run all administrators in Admin Approval Mode" disabled: EnableLUA 0), these members of the
/// Administrators group: each of its direct members (an account or a group), the account PoolWatch runs as when its
/// token holds Administrators, and any other local, domain or Microsoft Entra ID account or group that Windows' Authz
/// API, asked about its SID alone, finds to be a member through a group (<see cref="IsAdministratorBySid"/>, <see
/// cref="ElevatedAccounts.IsAccount"/>). A SID the Authz API gives no answer for (a domain account while no domain
/// controller answers, say) is not counted: the check then fails closed. With "Admin Approval Mode for the Built-in
/// Administrator account" off (FilterAdministratorToken 0, Windows' default), this PC's built-in Administrator account
/// (its account domain's RID 500, so only when this PC's account domain can be read), while the group lists it as a
/// direct member and when PoolWatch runs as it with Administrators in its token. Without this, setup run by such an
/// account, which owns what it makes (Windows' "default owner: object creator" on client Windows), gives PoolWatch's
/// folder an owner the check would count, and PoolWatch could never start. Decided from the machine's settings and its
/// Administrators group, not from the token of the moment, with two exceptions, each through PoolWatch's own token:
/// with UAC off, the account PoolWatch runs as counts when its token holds Administrators; with the built-in
/// Administrator's Admin Approval Mode off, that account counts when PoolWatch runs as it with Administrators in its
/// token. So two kinds of account count when PoolWatch runs as them and not when another account runs it: with UAC
/// off, an administrator that the group's members, as read, do not list (they could not be read, or it is a member
/// only through a group) and that the Authz API does not find to be a member (it gives no answer, say); with UAC on and
/// the built-in Administrator's Admin Approval Mode off, that account when the group's members cannot be read or do not
/// list it (and this PC's account domain can be read: otherwise it counts neither way). Every other account still
/// counts. A program such an administrator deliberately starts with fewer rights (a "Basic User" SAFER level, say)
/// still holds the account's SID: that is the administrator's own choice, documented in SECURITY.md. Read only. With
/// UAC on, nothing here asks a domain controller: the group's members are read as SIDs (no member's name is looked up;
/// only the group's own name, which this PC holds), the account domain comes from this PC's own account database, and
/// the Authz API is asked about no SID.
/// </summary>
internal static class AlwaysElevated
{
    /// <summary>The account PoolWatch runs as: its SID and whether its token holds Administrators.</summary>
    internal sealed record Account(SecurityIdentifier Sid, bool Administrator);

    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>
    /// Pure: the accounts that count as an administrator's own on a PC with these settings. <paramref name="administrators"/>:
    /// the group's direct members (null when they could not be read); <paramref name="accountDomain"/>: this PC's account
    /// domain (null when it could not be read: the built-in Administrator's own switch then counts no account);
    /// <paramref name="me"/>: the account
    /// PoolWatch runs as; <paramref name="memberBySid"/>: whether Windows finds an account a member of Administrators from
    /// its SID alone (null: it could not tell), asked only with UAC off. UAC settings that could not be read (null) give
    /// none: the check then fails closed.
    /// </summary>
    internal static ElevatedAccounts Of(UacSwitches? uac, IReadOnlyCollection<SecurityIdentifier>? administrators, SecurityIdentifier? accountDomain,
        Account? me, Func<SecurityIdentifier, bool?>? memberBySid)
    {
        if (uac is null) return ElevatedAccounts.None;
        var known = new HashSet<SecurityIdentifier>();
        if (!uac.ForAll)
        {
            foreach (var m in administrators ?? []) known.Add(m);
            if (me is { Administrator: true }) known.Add(me.Sid);
        }
        if (!uac.ForBuiltInAdministrator && accountDomain is not null)
        {
            var builtIn = new SecurityIdentifier(WellKnownSidType.AccountAdministratorSid, accountDomain);
            if (administrators?.Contains(builtIn) == true || (me is { Administrator: true } && me.Sid == builtIn)) known.Add(builtIn);
        }
        return new ElevatedAccounts(known, uac.ForAll ? null : memberBySid);
    }

    /// <summary>This PC's (<see cref="Of"/>), read now; none when not on Windows or when UAC's settings cannot be read.</summary>
    public static ElevatedAccounts OfThisPc() => OfThisPc(ThisPcsReads);

    /// <summary>
    /// <see cref="OfThisPc()"/> over its reads, so tests can pin which are made: with UAC on and the built-in
    /// Administrator's Admin Approval Mode on too, none but UAC's settings; the group's members only when either is off;
    /// the account domain only for the built-in Administrator; the per-account question only with UAC off.
    /// </summary>
    internal static ElevatedAccounts OfThisPc(Reads reads)
    {
        try
        {
            if (reads.Uac() is not { } uac) return ElevatedAccounts.None;
            if (uac.ForAll && uac.ForBuiltInAdministrator) return ElevatedAccounts.None;
            return Of(uac, reads.Administrators(), uac.ForBuiltInAdministrator ? null : reads.AccountDomain(), reads.Me(), reads.MemberBySid);
        }
        catch (Exception) { return ElevatedAccounts.None; }
    }

    /// <summary>The reads behind <see cref="OfThisPc()"/>.</summary>
    internal sealed record Reads(Func<UacSwitches?> Uac, Func<IReadOnlyCollection<SecurityIdentifier>?> Administrators, Func<SecurityIdentifier?> AccountDomain,
        Func<Account?> Me, Func<SecurityIdentifier, bool?> MemberBySid);

    private static readonly Reads ThisPcsReads = new(UacSwitches.Read, ReadAdministrators, UnelevatedAccount.ReadAccountDomain, ReadAccount, IsAdministratorBySid);

    /// <summary>The account PoolWatch runs as (its token: Administrators held); null when it cannot be read.</summary>
    private static Account? ReadAccount()
    {
        using var id = WindowsIdentity.GetCurrent();
        if (id.User is not { } user) return null;
        return new Account(user, new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator));
    }

    /// <summary>
    /// The direct members of the local Administrators group as SIDs (NetLocalGroupGetMembers, level 0: no name is looked
    /// up, so no domain controller is asked); null when they cannot be read.
    /// </summary>
    internal static IReadOnlyCollection<SecurityIdentifier>? ReadAdministrators()
    {
        if (!OperatingSystem.IsWindows()) return null;
        string group;
        try
        {
            // The group's name in this Windows' language ("BUILTIN\Administrators", "VORDEFINIERT\Administratoren", ...): a
            // well-known group, named by this PC itself.
            string full = Administrators.Translate(typeof(NTAccount)).Value;
            group = full[(full.IndexOf('\\') + 1)..];
        }
        catch (Exception) { return null; }
        int result = NetLocalGroupGetMembers(null, group, 0, out IntPtr buffer, MaxPreferredLength, out int read, out _, IntPtr.Zero);
        try
        {
            if (result != 0 || buffer == IntPtr.Zero) return null;
            var members = new List<SecurityIdentifier>(read);
            for (int i = 0; i < read; i++) // LOCALGROUP_MEMBERS_INFO_0: one PSID each
                if (Marshal.ReadIntPtr(buffer, i * IntPtr.Size) is var sid && sid != IntPtr.Zero) members.Add(new SecurityIdentifier(sid));
            return members;
        }
        finally { if (buffer != IntPtr.Zero) NetApiBufferFree(buffer); }
    }

    /// <summary>
    /// Whether Windows finds <paramref name="sid"/> a member of Administrators, directly or through groups, from the SID
    /// alone (the groups of an Authz client context made from it, as Windows' own Effective Access reads them); null when
    /// it cannot tell. For a domain account Windows asks a domain controller, so this is asked only with UAC off and only
    /// about a SID of an account's kind the check meets (<see cref="ElevatedAccounts.Holds"/>, <see cref="ElevatedAccounts.IsAccount"/>).
    /// </summary>
    internal static bool? IsAdministratorBySid(SecurityIdentifier sid) => GroupsOf(sid) is { } groups ? groups.Contains(Administrators) : null;

    /// <summary>The groups Windows works out for an account (or a group) from its SID alone (deny-only ones left out); null when it cannot.</summary>
    internal static IReadOnlyList<SecurityIdentifier>? GroupsOf(SecurityIdentifier sid)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (!AuthzInitializeResourceManager(AuthzRmFlagNoAudit, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, null, out IntPtr manager)) return null;
        try
        {
            var bytes = new byte[sid.BinaryLength];
            sid.GetBinaryForm(bytes, 0);
            if (!AuthzInitializeContextFromSid(0, bytes, manager, IntPtr.Zero, default, IntPtr.Zero, out IntPtr context)) return null;
            try
            {
                AuthzGetInformationFromContext(context, AuthzContextInfoGroupsSids, 0, out int size, IntPtr.Zero);
                if (size <= 0) return null;
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (!AuthzGetInformationFromContext(context, AuthzContextInfoGroupsSids, size, out _, buffer)) return null;
                    int count = Marshal.ReadInt32(buffer); // TOKEN_GROUPS: a DWORD count, then SID_AND_ATTRIBUTES aligned as a pointer
                    int entry = Marshal.SizeOf<SidAndAttributes>();
                    var groups = new List<SecurityIdentifier>(count);
                    for (int i = 0; i < count; i++)
                    {
                        var g = Marshal.PtrToStructure<SidAndAttributes>(buffer + IntPtr.Size + i * entry);
                        if (g.Sid != IntPtr.Zero && (g.Attributes & SeGroupUseForDenyOnly) == 0) groups.Add(new SecurityIdentifier(g.Sid));
                    }
                    return groups;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { AuthzFreeContext(context); }
        }
        catch (Exception) { return null; }
        finally { AuthzFreeResourceManager(manager); }
    }

    private const int MaxPreferredLength = -1, AuthzContextInfoGroupsSids = 2;
    private const uint AuthzRmFlagNoAudit = 0x1, SeGroupUseForDenyOnly = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int NetLocalGroupGetMembers(string? serverName, string localGroupName, int level, out IntPtr buffer, int preferredMaximumLength,
        out int entriesRead, out int totalEntries, IntPtr resumeHandle);

    [DllImport("netapi32.dll", ExactSpelling = true)]
    private static extern int NetApiBufferFree(IntPtr buffer);

    [DllImport("authz.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AuthzInitializeResourceManager(uint flags, IntPtr accessCheck, IntPtr computeDynamicGroups, IntPtr freeDynamicGroups,
        string? resourceManagerName, out IntPtr resourceManager);

    [DllImport("authz.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AuthzInitializeContextFromSid(uint flags, byte[] userSid, IntPtr resourceManager, IntPtr expirationTime, Luid identifier,
        IntPtr dynamicGroupArgs, out IntPtr clientContext);

    [DllImport("authz.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AuthzGetInformationFromContext(IntPtr clientContext, int infoClass, int bufferSize, out int sizeRequired, IntPtr buffer);

    [DllImport("authz.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AuthzFreeContext(IntPtr clientContext);

    [DllImport("authz.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AuthzFreeResourceManager(IntPtr resourceManager);
}
