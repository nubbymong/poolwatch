using System.Security.AccessControl;
using System.Security.Principal;
using PoolWatch.Engine;

namespace PoolWatch.AppTests;

/// <summary>
/// Folder and file access rules for the admin-only checks, as SDDL: each is made into a security descriptor in memory and
/// judged there, never read from or applied to a folder or file on the PC.
/// </summary>
internal static class Sddl
{
    private const string TrustedInstaller = TrustedInstallerSid;
    public const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    /// <summary>A signed-in account (made up), for "the account itself".</summary>
    public const string SomeoneSid = "S-1-5-21-1-2-3-1001";
    public const string OtherSid = "S-1-5-21-1-2-3-1002";

    /// <summary>Program Files as Windows sets it: users may read and run what is in it, only administrators and Windows change it.</summary>
    public const string ProgramFiles = $"O:{TrustedInstaller}G:{TrustedInstaller}D:PAI(A;;FA;;;{TrustedInstaller})(A;CIIO;GA;;;{TrustedInstaller})" +
        "(A;;0x1301bf;;;SY)(A;OICIIO;GA;;;SY)(A;;0x1301bf;;;BA)(A;OICIIO;GA;;;BA)(A;;0x1200a9;;;BU)(A;OICIIO;GXGR;;;BU)(A;OICIIO;GA;;;CO)" +
        "(A;;0x1200a9;;;AC)(A;OICIIO;GXGR;;;AC)";

    /// <summary>A folder setup made in Program Files, with the rules it took from there.</summary>
    public const string Installed = $"O:BAG:SYD:AI(A;ID;FA;;;{TrustedInstaller})(A;CIIOID;GA;;;{TrustedInstaller})(A;ID;FA;;;SY)(A;OICIIOID;GA;;;SY)" +
        "(A;ID;FA;;;BA)(A;OICIIOID;GA;;;BA)(A;ID;0x1200a9;;;BU)(A;OICIIOID;GXGR;;;BU)(A;OICIIOID;GA;;;CO)(A;ID;0x1200a9;;;AC)(A;OICIIOID;GXGR;;;AC)";

    /// <summary>
    /// The same folder made by an account that owns what it makes (UAC off, or the built-in Administrator, with Windows'
    /// "default owner: object creator"): that account owns it and has full control through CREATOR OWNER.
    /// </summary>
    public const string InstalledByItsOwner = $"O:{SomeoneSid}G:SYD:AI(A;ID;FA;;;{TrustedInstaller})(A;ID;FA;;;SY)(A;ID;FA;;;BA)" +
        $"(A;ID;0x1200a9;;;BU)(A;ID;FA;;;{SomeoneSid})(A;OICIIOID;GA;;;CO)";

    /// <summary>An admin-only folder's rules with one more rule (an ACE string) added.</summary>
    public static string AdminOnlyAnd(string ace) => "O:BAG:SYD:(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x1200a9;;;BU)" + ace;

    /// <summary>Not rules: the folder or file is not there.</summary>
    public const string NotThere = "(not there)";
    /// <summary>Not rules: they cannot be read.</summary>
    public const string Unreadable = "(unreadable)";
    /// <summary>Not rules: the file is a link (ReadSecurity reads no rules of it).</summary>
    public const string Link = "(a link)";

    /// <summary>A sentinel's read, as ReadSecurity gives it: null, an access error, a link, or the descriptor made from SDDL.</summary>
    public static RawSecurityDescriptor? Read(string path, string sddl) => sddl switch
    {
        NotThere => null,
        Unreadable => throw new UnauthorizedAccessException("Access is denied."),
        Link => throw new LinkedFileException(path),
        _ => new(sddl),
    };
}

/// <summary>
/// An admin-only program is judged by its place, by the access rules of its folders and by its own, read only. Every
/// descriptor here is made in memory from SDDL: nothing reads or changes the access rules of a folder or file on this PC.
/// </summary>
public class AdminFolderRulesTests
{
    private static readonly SecurityIdentifier Someone = new(Sddl.SomeoneSid);
    private static readonly UnelevatedAccount SomeoneUnelevated = new(Someone, []);

    private static bool Changeable(string sddl, SecurityIdentifier? user = null, bool filesInIt = false) =>
        InstallGuard.LetsStandardUsersChange(new RawSecurityDescriptor(sddl), InstallGuard.StandardUsers(new UnelevatedAccount(user ?? Someone, [])), filesInIt);

    private static bool ChangeableWithNoUnelevatedAccount(string sddl) =>
        InstallGuard.LetsStandardUsersChange(new RawSecurityDescriptor(sddl), InstallGuard.StandardUsers(null));

    [Fact]
    public void ProgramFilesAndAFolderSetupMadeThereAreAdminOnly()
    {
        foreach (bool filesInIt in new[] { false, true })
        {
            Assert.False(Changeable(Sddl.ProgramFiles, filesInIt: filesInIt));
            Assert.False(Changeable(Sddl.Installed, filesInIt: filesInIt));
            Assert.False(Changeable(Sddl.AdminOnlyAnd(""), filesInIt: filesInIt));
        }
    }

    [Theory]
    [InlineData("(A;OICI;0x1301bf;;;BU)", "Users may modify it")]
    [InlineData("(A;;0x2;;;AU)", "Authenticated Users may add a file")]
    [InlineData("(A;;0x4;;;WD)", "Everyone may add a folder")]
    [InlineData("(A;;0x40;;;IU)", "INTERACTIVE may delete what is in it")]
    [InlineData("(A;;SD;;;BU)", "Users may delete or rename it")]
    [InlineData("(A;;WD;;;BU)", "Users may change its rules")]
    [InlineData("(A;;WO;;;BU)", "Users may take ownership")]
    [InlineData("(A;;GW;;;BU)", "a generic write")]
    [InlineData("(A;;GA;;;WD)", "a generic all")]
    [InlineData("(A;;FA;;;" + Sddl.SomeoneSid + ")", "the signed-in account itself")]
    [InlineData("(A;;0x2;;;S-1-2-0)", "LOCAL, which every sign-in at the PC holds")]
    [InlineData("(A;;0x2;;;S-1-2-1)", "CONSOLE LOGON")]
    [InlineData("(A;;0x2;;;S-1-5-14)", "REMOTE INTERACTIVE LOGON, for a Remote Desktop sign-in")]
    [InlineData("(A;;0x2;;;S-1-5-15)", "This Organization")]
    [InlineData("(A;;0x2;;;S-1-5-113)", "Local account, which every local account holds")]
    [InlineData("(A;;0x2;;;S-1-5-3)", "BATCH, which a standard user's own scheduled task holds")]
    public void ARuleThatLetsAStandardUserChangeItCounts(string ace, string why) => Assert.True(Changeable(Sddl.AdminOnlyAnd(ace)), why);

    [Theory]
    [InlineData("(A;OICIIO;0x1301bf;;;BU)", "only for what is made in it later")]
    [InlineData("(A;;0x1200a9;;;WD)", "read and run only")]
    [InlineData("(A;;0x110;;;BU)", "attributes only, which change no file")]
    [InlineData("(D;;FA;;;BU)", "a deny")]
    [InlineData("(A;;FA;;;" + Sddl.OtherSid + ")", "another account")]
    [InlineData("(A;;FA;;;BA)", "administrators")]
    [InlineData("(A;;FA;;;SY)", "SYSTEM")]
    public void ARuleThatGivesAStandardUserNoChangeDoesNotCount(string ace, string why) => Assert.False(Changeable(Sddl.AdminOnlyAnd(ace)), why);

    [Fact]
    public void InTheProgramsOwnFolderARuleForTheFilesMadeInItCounts()
    {
        // "Files only" in the Advanced Security dialog: the folder itself is closed, but RAMMap (or PoolWatch.exe and its
        // DLLs) made in it takes the rule, so a standard user could overwrite the program.
        Assert.True(Changeable(Sddl.AdminOnlyAnd("(A;OIIO;FA;;;BU)"), filesInIt: true));
        Assert.True(Changeable(Sddl.AdminOnlyAnd("(A;OICIIO;0x1301bf;;;BU)"), filesInIt: true));
        Assert.True(Changeable(Sddl.AdminOnlyAnd("(A;OIIONP;0x2;;;AU)"), filesInIt: true)); // the files right in it only, the program among them
        Assert.False(Changeable(Sddl.AdminOnlyAnd("(A;OIIO;FA;;;BU)"))); // a folder above: the folder below holds its own copy
        Assert.False(Changeable(Sddl.AdminOnlyAnd("(A;CIIO;FA;;;BU)"), filesInIt: true)); // for the folders in it only, each judged on its own
        Assert.False(Changeable(Sddl.AdminOnlyAnd("(A;OIIO;FA;;;CO)"), filesInIt: true)); // CREATOR OWNER: only for whoever makes a file, an administrator
        Assert.False(Changeable(Sddl.AdminOnlyAnd("(A;OIIO;GXGR;;;BU)"), filesInIt: true));
    }

    [Fact]
    public void AnOwnerAmongThemOrNoRulesAtAllCount()
    {
        Assert.True(Changeable("O:BUG:SYD:(A;;FA;;;SY)(A;;FA;;;BA)")); // owned by Users: an owner may always change who has access
        Assert.True(Changeable($"O:{Sddl.SomeoneSid}G:SYD:(A;;FA;;;SY)(A;;FA;;;BA)"));
        Assert.False(Changeable($"O:{Sddl.SomeoneSid}G:SYD:(A;;FA;;;SY)(A;;FA;;;BA)", new(Sddl.OtherSid)));
        Assert.True(Changeable("O:BAG:SYD:NO_ACCESS_CONTROL")); // a null list: everyone may do anything
        Assert.True(Changeable("O:BAG:SY"));                    // no list at all
        Assert.False(Changeable("O:BAG:SYD:"));                 // an empty list: nobody may
        // SYSTEM, the service accounts, Administrators and TrustedInstaller are no standard user, even as the account
        // PoolWatch runs for.
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-19", "S-1-5-20", "S-1-5-32-544", Sddl.TrustedInstallerSid })
        {
            Assert.False(Changeable(Sddl.AdminOnlyAnd($"(A;;FA;;;SY)(A;;FA;;;LS)(A;;FA;;;NS)(A;;FA;;;{Sddl.TrustedInstallerSid})"), new(sid)), sid);
            Assert.False(Changeable($"O:{sid}G:SYD:(A;;FA;;;BA)", new(sid)), sid);
        }
    }

    /// <summary>
    /// "Local account and member of Administrators group", and the groups UAC treats as elevated: Administrators, Power
    /// Users, Account Operators, Server Operators, Print Operators, Backup Operators, RAS Servers, Network Configuration
    /// Operators, and a domain's Domain Admins, Domain Controllers, Cert Publishers, Schema Admins, Enterprise Admins and
    /// Group Policy Creator Owners.
    /// </summary>
    private static readonly string[] ElevatedGroups =
    [
        "S-1-5-114", "S-1-5-32-544", "S-1-5-32-547", "S-1-5-32-548", "S-1-5-32-549", "S-1-5-32-550", "S-1-5-32-551", "S-1-5-32-553", "S-1-5-32-556",
        "S-1-5-21-1-2-3-512", "S-1-5-21-1-2-3-516", "S-1-5-21-1-2-3-517", "S-1-5-21-1-2-3-518", "S-1-5-21-1-2-3-519", "S-1-5-21-1-2-3-520",
    ];

    private const string DomainUsers = "S-1-5-21-1-2-3-513";

    [Fact]
    public void WithNoSplitTokenTheAccountsGroupsCountButNotItsOwnSidNorAGroupUacTreatsAsElevated()
    {
        // UAC off, or the built-in Administrator: no program of the account runs without administrator rights, but other
        // members of its groups (Domain Users, a local group) are standard users whose programs hold them.
        var self = new UnelevatedAccount(Someone, [Users, .. ElevatedGroups.Select(s => new SecurityIdentifier(s)), new(DomainUsers)]);
        var unsplit = UnelevatedAccount.Pick(UnelevatedAccount.ElevationDefault, administrator: true, self, () => throw new InvalidOperationException("no linked token"));
        var standard = InstallGuard.StandardUsers(unsplit);
        Assert.True(InstallGuard.LetsStandardUsersChange(new RawSecurityDescriptor(Sddl.AdminOnlyAnd($"(A;OICI;0x1301bf;;;{DomainUsers})")), standard));
        Assert.False(InstallGuard.LetsStandardUsersChange(new RawSecurityDescriptor(Sddl.InstalledByItsOwner), standard)); // its own SID: no way in
        foreach (var group in ElevatedGroups)
            Assert.False(InstallGuard.LetsStandardUsersChange(new RawSecurityDescriptor(Sddl.AdminOnlyAnd($"(A;OICI;FA;;;{group})")), standard), group);

        // A split token whose linked half cannot be read: this token's own user and groups, less those UAC treats as elevated.
        var unreadable = InstallGuard.StandardUsers(UnelevatedAccount.Pick(UnelevatedAccount.ElevationFull, administrator: true, self, () => null));
        Assert.True(InstallGuard.LetsStandardUsersChange(new RawSecurityDescriptor(Sddl.AdminOnlyAnd($"(A;OICI;0x1301bf;;;{DomainUsers})")), unreadable));
        Assert.True(InstallGuard.LetsStandardUsersChange(new RawSecurityDescriptor(Sddl.InstalledByItsOwner), unreadable));
        foreach (var group in ElevatedGroups)
            Assert.False(InstallGuard.LetsStandardUsersChange(new RawSecurityDescriptor(Sddl.AdminOnlyAnd($"(A;OICI;FA;;;{group})")), unreadable), group);

        // A domain group is known by its own number only: not by one that merely ends in it, nor in a SID of another shape.
        Assert.False(InstallGuard.NeverStandard(new SecurityIdentifier("S-1-5-21-1-2-3-1512")));
        Assert.False(InstallGuard.NeverStandard(new SecurityIdentifier("S-1-5-21-1-2-512")));
        Assert.False(InstallGuard.NeverStandard(new SecurityIdentifier(DomainUsers)));
    }

    [Fact]
    public void AnAccountOfAnyAuthorityCountsAsItself()
    {
        // A Microsoft Entra ID account's SID is not from the PC or a domain (S-1-12-1-...): it still counts, as owner and in a rule.
        var entra = new SecurityIdentifier("S-1-12-1-1111111111-2222222222-3333333333-4444444444");
        Assert.True(Changeable($"O:{entra.Value}G:SYD:(A;;FA;;;SY)(A;;FA;;;BA)", entra));
        Assert.True(Changeable(Sddl.AdminOnlyAnd($"(A;OICI;FA;;;{entra.Value})"), entra));
        Assert.False(Changeable(Sddl.AdminOnlyAnd($"(A;OICI;FA;;;{entra.Value})"))); // not that account: a stranger to it
    }

    [Fact]
    public void TheAccountsOwnGroupsCountAndNoGroupThereForDenyingDoes()
    {
        var domainUsers = new SecurityIdentifier("S-1-5-21-1-2-3-513");
        var rule = Sddl.AdminOnlyAnd($"(A;OICI;0x1301bf;;;{domainUsers.Value})");
        Assert.False(Changeable(rule)); // a group the account is not in
        Assert.True(InstallGuard.LetsStandardUsersChange(new RawSecurityDescriptor(rule), InstallGuard.StandardUsers(new(Someone, [domainUsers]))));
        // Administrators is never counted, should a token ever list it.
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        Assert.False(InstallGuard.LetsStandardUsersChange(new RawSecurityDescriptor(Sddl.ProgramFiles), InstallGuard.StandardUsers(new(Someone, [admins]))));
    }

    [Fact]
    public void WithNoProgramWithoutAdministratorRightsTheAccountsOwnFoldersAreNoWayIn()
    {
        // UAC off, or the built-in Administrator: every program of that account runs as administrator, so a folder it owns
        // is no way in. Every other standard user still counts.
        Assert.False(ChangeableWithNoUnelevatedAccount(Sddl.InstalledByItsOwner));
        Assert.True(Changeable(Sddl.InstalledByItsOwner));
        Assert.True(ChangeableWithNoUnelevatedAccount(Sddl.AdminOnlyAnd("(A;OICI;0x1301bf;;;BU)")));
    }

    [Fact]
    public void AConditionalRuleCountsAsAllowedAndAnUnknownKindOfRuleFailsClosed()
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var standard = InstallGuard.StandardUsers(SomeoneUnelevated);
        RawSecurityDescriptor With(GenericAce ace)
        {
            var acl = new RawAcl(GenericAcl.AclRevision, 1);
            acl.InsertAce(0, ace);
            return new(ControlFlags.DiscretionaryAclPresent, admins, admins, null, acl);
        }
        Assert.True(InstallGuard.LetsStandardUsersChange(With(new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, 0x2, users, isCallback: true, opaque: null)), standard));
        Assert.True(InstallGuard.LetsStandardUsersChange(With(new CustomAce((AceType)0x20, AceFlags.None, new byte[4])), standard));
        Assert.False(InstallGuard.LetsStandardUsersChange(With(new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, 0x2, admins, isCallback: false, opaque: null)), standard));
    }

    private static readonly string[] Roots = [@"C:\Program Files", @"C:\Program Files (x86)"];

    /// <summary>The rules of folders and files by path (not listed: those of Program Files), and the paths read, in order.</summary>
    private static (Func<string, RawSecurityDescriptor?> Read, List<string> Asked) Rules(Dictionary<string, string> rules)
    {
        var asked = new List<string>();
        return (f =>
        {
            asked.Add(f);
            return Sddl.Read(f, rules.GetValueOrDefault(f, Sddl.ProgramFiles));
        }, asked);
    }

    private static OpenPlace? Open(string file, Dictionary<string, string> rules) =>
        InstallGuard.UserWritable(file, Roots, Rules(rules).Read, InstallGuard.StandardUsers(SomeoneUnelevated));

    private const string Tools = @"C:\Program Files\Vendor\Tools";
    private const string Ram = Tools + @"\RAMMap64.exe";
    private const string UsersModify = "(A;OICI;0x1301bf;;;BU)";

    [Fact]
    public void EveryFolderFromTheProgramsUpToTheRootIsJudgedAndNoneAboveThenTheProgramItself()
    {
        var (read, asked) = Rules([]);
        Assert.Null(InstallGuard.UserWritable(Ram, Roots, read, InstallGuard.StandardUsers(SomeoneUnelevated)));
        Assert.Equal([Tools, @"C:\Program Files\Vendor", @"C:\Program Files", Ram], asked); // never C:\, where users may add folders

        // A folder above can be renamed and made again, so one users may change counts for the folders below it too.
        var folder = new OpenPlace(Tools, Ram, OpenBecause.FolderRules);
        Assert.Equal(folder, Open(Ram, new() { [@"C:\Program Files\Vendor"] = Sddl.AdminOnlyAnd(UsersModify) }));
        Assert.Equal(folder, Open(Ram, new() { [Tools] = Sddl.AdminOnlyAnd(UsersModify) }));
        Assert.Equal(folder, Open(Ram, new() { [@"C:\Program Files"] = Sddl.AdminOnlyAnd(UsersModify) }));
        // A rule for the files made in a folder counts in the program's own, where it reaches the program.
        Assert.Equal(folder, Open(Ram, new() { [Tools] = Sddl.AdminOnlyAnd("(A;OIIO;FA;;;BU)") }));
        Assert.Null(Open(Ram, new() { [@"C:\Program Files\Vendor"] = Sddl.AdminOnlyAnd("(A;OIIO;FA;;;BU)") }));
    }

    [Fact]
    public void TheProgramsOwnRulesAreJudgedSinceAFileMovedThereKeepsThem()
    {
        // Downloaded into the profile and moved, on the same drive, into an admin-only folder: it keeps its full control for the account.
        Assert.Equal(new OpenPlace(Tools, Ram, OpenBecause.ProgramRules), Open(Ram, new() { [Ram] = $"O:BAG:SYD:(A;ID;FA;;;SY)(A;ID;FA;;;BA)(A;ID;FA;;;{Sddl.SomeoneSid})" }));
        Assert.Equal(new OpenPlace(Tools, Ram, OpenBecause.ProgramRules), Open(Ram, new() { [Ram] = $"O:{Sddl.SomeoneSid}G:SYD:(A;;FA;;;SY)(A;;FA;;;BA)" }));
        Assert.Equal(new OpenPlace(Tools, Ram, OpenBecause.Unreadable), Open(Ram, new() { [Ram] = Sddl.Unreadable }));
        Assert.Null(Open(Ram, new() { [Ram] = Sddl.Installed })); // made there, it took the folder's rules
        // Not there: not every rule a file made there later would take is judged. A link: the folders of the file it
        // points at are not judged.
        Assert.Equal(new OpenPlace(Tools, Ram, OpenBecause.NotThere), Open(Ram, new() { [Ram] = Sddl.NotThere }));
        Assert.Equal(new OpenPlace(Tools, Ram, OpenBecause.Link), Open(Ram, new() { [Ram] = Sddl.Link }));
    }

    [Fact]
    public void AFolderThatIsNotThereIsJudgedByTheOneItWouldBeMadeInAndRulesThatCannotBeReadCount()
    {
        // Its program is not there either, which counts once every folder that is there passes.
        Assert.Equal(new OpenPlace(Tools, Ram, OpenBecause.NotThere), Open(Ram, new() { [Tools] = Sddl.NotThere, [Ram] = Sddl.NotThere }));
        Assert.Equal(new OpenPlace(Tools, Ram, OpenBecause.FolderRules),
            Open(Ram, new() { [Tools] = Sddl.NotThere, [Ram] = Sddl.NotThere, [@"C:\Program Files\Vendor"] = Sddl.AdminOnlyAnd("(A;;0x4;;;AU)") }));
        // The program would be made in the nearest folder that is there, so that folder's rule for the files made in it counts.
        Assert.Equal(new OpenPlace(Tools, Ram, OpenBecause.FolderRules),
            Open(Ram, new() { [Tools] = Sddl.NotThere, [Ram] = Sddl.NotThere, [@"C:\Program Files\Vendor"] = Sddl.AdminOnlyAnd("(A;OIIO;FA;;;BU)") }));
        Assert.Equal(new OpenPlace(Tools, Ram, OpenBecause.Unreadable), Open(Ram, new() { [Tools] = Sddl.Unreadable }));
        Assert.Equal(new OpenPlace(Tools, Ram, OpenBecause.Unreadable), Open(Ram, new() { [@"C:\Program Files"] = Sddl.Unreadable }));
    }

    [Fact]
    public void ReadSecurityIsNullOnlyForAPathThatIsNotThere()
    {
        // The real reader, on paths whose attributes cannot be read: it reads no access rules, since that read fails first.
        string missing = Path.Combine(Path.GetTempPath(), "pw-not-there-" + Guid.NewGuid().ToString("N"));
        Assert.Null(InstallGuard.ReadSecurity(missing + ".exe"));                         // a file not found
        Assert.Null(InstallGuard.ReadSecurity(Path.Combine(missing, "RAMMap64.exe")));    // its folder not found
        Assert.ThrowsAny<Exception>(() => InstallGuard.ReadSecurity(missing + "\0.exe")); // any other error: thrown, so it counts
    }

    [Fact]
    public void ReadSecurityPinsWhichErrorsMeanNotThereAndReadsNoRulesOfAFileThatIsALink()
    {
        // Over its three reads, in memory: no attributes, link or access rules of anything on the PC are read.
        var rules = new List<(string Path, bool Folder)>();
        RawSecurityDescriptor Rules(string p, bool folder) { rules.Add((p, folder)); return new(Sddl.Installed); }
        RawSecurityDescriptor? Read(FileAttributes attributes, string? target = null, Exception? error = null, Exception? rulesError = null) =>
            InstallGuard.ReadSecurity(Ram, _ => error is null ? attributes : throw error, _ => target,
                (p, folder) => rulesError is null ? Rules(p, folder) : throw rulesError);

        Assert.Null(Read(0, error: new FileNotFoundException()));
        Assert.Null(Read(0, error: new DirectoryNotFoundException()));
        Assert.Null(Read(FileAttributes.Normal, rulesError: new FileNotFoundException())); // gone between the two reads
        Assert.Throws<UnauthorizedAccessException>(() => Read(0, error: new UnauthorizedAccessException()));
        Assert.Throws<UnauthorizedAccessException>(() => Read(FileAttributes.Normal, rulesError: new UnauthorizedAccessException()));
        Assert.Throws<IOException>(() => Read(0, error: new IOException("The device is not ready.")));
        Assert.Empty(rules);

        Assert.Throws<LinkedFileException>(() => Read(FileAttributes.ReparsePoint, target: @"C:\Users\Public\RAMMap64.exe"));
        Assert.Empty(rules); // a link's rules are those of the file it points at: never read
        Assert.NotNull(Read(FileAttributes.Normal));
        Assert.NotNull(Read(FileAttributes.ReparsePoint));                                                 // a reparse point that is no link
        Assert.NotNull(Read(FileAttributes.Directory | FileAttributes.ReparsePoint, target: @"D:\Programs")); // a junction: read as it is
        Assert.Equal([(Ram, false), (Ram, false), (Ram, true)], rules);
    }

    [Fact]
    public void AFolderOutsideTheRootsIsUserWritableWithoutReadingItsRules()
    {
        var (read, asked) = Rules([]);
        var standard = InstallGuard.StandardUsers(SomeoneUnelevated);
        Assert.Equal(new OpenPlace(@"C:\Tools", @"C:\Tools\RAMMap64.exe", OpenBecause.OutsideAdminRoots), InstallGuard.UserWritable(@"C:\Tools\RAMMap64.exe", Roots, read, standard));
        Assert.Equal(@"C:\Program Files Evil", InstallGuard.UserWritable(@"C:\Program Files Evil\RAMMap64.exe", Roots, read, standard)?.Folder);
        Assert.Empty(asked);
    }

    [Fact]
    public void AnElevatedPoolWatchRefusesToRunFromAFolderOrAFileUsersMayChangeAsFromOneOutsideProgramFiles()
    {
        AdminPlaces Places(Dictionary<string, string> rules, UnelevatedAccount? account = null) => new(Roots, Rules(rules).Read, account ?? SomeoneUnelevated);
        const string folder = @"C:\Program Files\PoolWatch", exe = folder + @"\PoolWatch.exe";
        Assert.Null(InstallGuard.WhyNotFrom(exe, Places([])));
        string? open = InstallGuard.WhyNotFrom(exe, Places(new() { [folder] = Sddl.AdminOnlyAnd(UsersModify) }));
        Assert.StartsWith("PoolWatch runs as administrator and must not run from a folder you can write to", open);
        Assert.Contains(@"The permissions of C:\Program Files\PoolWatch or a folder above it let users without administrator rights change", open);
        Assert.Contains("Uninstall PoolWatch, delete that folder, and install it again", open);
        Assert.Contains("The permissions of C:\\Program Files\\PoolWatch\\PoolWatch.exe let users without administrator rights change it",
            InstallGuard.WhyNotFrom(exe, Places(new() { [exe] = Sddl.AdminOnlyAnd(UsersModify) })));
        Assert.Contains("Uninstall PoolWatch, delete that folder, and install it again", InstallGuard.WhyNotFrom(exe, Places(new() { [exe] = Sddl.AdminOnlyAnd(UsersModify) })));
        foreach (var unreadable in new[] { folder, exe })
            Assert.Contains(@"PoolWatch could not read the permissions of C:\Program Files\PoolWatch\PoolWatch.exe, C:\Program Files\PoolWatch or a folder above it",
                InstallGuard.WhyNotFrom(exe, Places(new() { [unreadable] = Sddl.Unreadable })));
        Assert.Contains("Install it to", InstallGuard.WhyNotFrom(@"C:\Users\someone\Downloads\PoolWatch.exe", Places([])));
        Assert.Contains(@"C:\Program Files\PoolWatch\PoolWatch.exe is a link to a file elsewhere, so PoolWatch cannot check the folders that file is in.",
            InstallGuard.WhyNotFrom(exe, Places(new() { [exe] = Sddl.Link })));
        Assert.Contains("Uninstall PoolWatch, delete that folder, and install it again", InstallGuard.WhyNotFrom(exe, Places(new() { [exe] = Sddl.Link })));
        Assert.Contains(@"PoolWatch could not read the permissions of C:\Program Files\PoolWatch\PoolWatch.exe",
            InstallGuard.WhyNotFrom(exe, Places(new() { [exe] = Sddl.NotThere }))); // the running exe, renamed since it started

        // Setup run by an account that owns what it makes: refused only while that account has programs without
        // administrator rights (a split token); with UAC off it starts.
        var owned = new Dictionary<string, string> { [folder] = Sddl.InstalledByItsOwner, [exe] = Sddl.InstalledByItsOwner };
        Assert.NotNull(InstallGuard.WhyNotFrom(exe, Places(owned)));
        Assert.Null(InstallGuard.WhyNotFrom(exe, new AdminPlaces(Roots, Rules(owned).Read, null)));
        var unsplit = UnelevatedAccount.Pick(UnelevatedAccount.ElevationDefault, administrator: true, new(Someone, [Users, Admins, new(DomainUsers)]), () => null);
        Assert.Null(InstallGuard.WhyNotFrom(exe, Places(owned, unsplit)));
        // But a group it shares with standard users still counts with UAC off.
        Assert.Contains(@"The permissions of C:\Program Files\PoolWatch or a folder above it",
            InstallGuard.WhyNotFrom(exe, Places(new() { [folder] = Sddl.AdminOnlyAnd($"(A;OICI;0x1301bf;;;{DomainUsers})") }, unsplit)));
    }

    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    [Fact]
    public void TheAccountCountedIsTheOneItsProgramsWithoutAdministratorRightsRunAs()
    {
        var self = new UnelevatedAccount(Someone, [Users, Admins]); // this process's token
        var linked = new UnelevatedAccount(Someone, [Users]);       // the split token's other half
        int asked = 0;
        UnelevatedAccount? Linked() { asked++; return linked; }

        // UAC's split token: the linked token's account and groups; this token's own when that cannot be read (the groups
        // UAC treats as elevated, Administrators among them, never count: StandardUsers leaves them out).
        Assert.Same(linked, UnelevatedAccount.Pick(UnelevatedAccount.ElevationFull, administrator: true, self, Linked));
        Assert.Same(self, UnelevatedAccount.Pick(UnelevatedAccount.ElevationFull, administrator: true, self, () => null));
        // An administrator's token with no split (UAC off, the built-in Administrator): its groups, not its own SID.
        asked = 0;
        var unsplit = UnelevatedAccount.Pick(UnelevatedAccount.ElevationDefault, administrator: true, self, Linked);
        Assert.Null(unsplit.User);
        Assert.Same(self.Groups, unsplit.Groups);
        Assert.Equal(0, asked);
        // A token without administrator rights is the one: this process's own.
        Assert.Same(self, UnelevatedAccount.Pick(UnelevatedAccount.ElevationDefault, administrator: false, self, Linked));
        Assert.Same(self, UnelevatedAccount.Pick(UnelevatedAccount.ElevationLimited, administrator: false, self, Linked));
        Assert.Equal(0, asked);
        // An elevation type that could not be read: an administrator's counts as split, else this process's own.
        Assert.Same(linked, UnelevatedAccount.Pick(0, administrator: true, self, Linked));
        Assert.Same(self, UnelevatedAccount.Pick(0, administrator: true, self, () => null));
        Assert.Same(self, UnelevatedAccount.Pick(0, administrator: false, self, Linked));
    }

    [Fact]
    public void ThisProcesssAccountIsReadFromItsTokenAndCountsNoGroupThatMayNeverCount()
    {
        // Read only: this test process's own token (and its linked one, when UAC split it); nothing is changed.
        using var me = WindowsIdentity.GetCurrent();
        bool administrator = new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator);
        var account = UnelevatedAccount.OfThisProcess();
        Assert.NotNull(account);
        if (account.User is null) Assert.True(administrator); // only an administrator with no split token, as on CI's runners
        else Assert.Equal(me.User, account.User);
        var standard = InstallGuard.StandardUsers(account);
        Assert.DoesNotContain(Admins, standard);
        Assert.DoesNotContain(new SecurityIdentifier("S-1-5-114"), standard);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), standard);
    }
}
