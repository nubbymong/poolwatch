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
    public const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    /// <summary>A signed-in account (made up), for "the account itself".</summary>
    public const string SomeoneSid = "S-1-5-21-1-2-3-1001";
    public const string OtherSid = "S-1-5-21-1-2-3-1002";

    /// <summary>Program Files as Windows sets it: users may read and run what is in it, only administrators and Windows change it.</summary>
    public const string ProgramFiles = $"O:{TrustedInstallerSid}G:{TrustedInstallerSid}D:PAI(A;;FA;;;{TrustedInstallerSid})(A;CIIO;GA;;;{TrustedInstallerSid})" +
        "(A;;0x1301bf;;;SY)(A;OICIIO;GA;;;SY)(A;;0x1301bf;;;BA)(A;OICIIO;GA;;;BA)(A;;0x1200a9;;;BU)(A;OICIIO;GXGR;;;BU)(A;OICIIO;GA;;;CO)" +
        "(A;;0x1200a9;;;AC)(A;OICIIO;GXGR;;;AC)";

    /// <summary>A folder setup made in Program Files, with the rules it took from there.</summary>
    public const string Installed = $"O:BAG:SYD:AI(A;ID;FA;;;{TrustedInstallerSid})(A;CIIOID;GA;;;{TrustedInstallerSid})(A;ID;FA;;;SY)(A;OICIIOID;GA;;;SY)" +
        "(A;ID;FA;;;BA)(A;OICIIOID;GA;;;BA)(A;ID;0x1200a9;;;BU)(A;OICIIOID;GXGR;;;BU)(A;OICIIOID;GA;;;CO)(A;ID;0x1200a9;;;AC)(A;OICIIOID;GXGR;;;AC)";

    /// <summary>
    /// The same folder made by an account that owns what it makes (UAC off, or the built-in Administrator, with Windows'
    /// "default owner: object creator"): that account owns it and has full control through CREATOR OWNER.
    /// </summary>
    public const string InstalledByItsOwner = $"O:{SomeoneSid}G:SYD:AI(A;ID;FA;;;{TrustedInstallerSid})(A;ID;FA;;;SY)(A;ID;FA;;;BA)" +
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
/// Every account and group that is not an administrator counts, whoever is signed in.
/// </summary>
public class AdminFolderRulesTests
{
    private static bool Changeable(string sddl, bool filesInIt = false) =>
        InstallGuard.LetsNonAdminsChange(new RawSecurityDescriptor(sddl), filesInIt);

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
    [InlineData("(A;;FA;;;" + Sddl.SomeoneSid + ")", "an account itself, the signed-in one or not")]
    [InlineData("(A;;FA;;;" + Sddl.OtherSid + ")", "another user's own SID")]
    [InlineData("(A;;0x2;;;S-1-5-21-1-2-3-1105)", "a group only other users hold")]
    [InlineData("(A;;0x2;;;S-1-2-0)", "LOCAL, which every sign-in at the PC holds")]
    [InlineData("(A;;0x2;;;S-1-2-1)", "CONSOLE LOGON")]
    [InlineData("(A;;0x2;;;S-1-5-14)", "REMOTE INTERACTIVE LOGON, for a Remote Desktop sign-in")]
    [InlineData("(A;;0x2;;;S-1-5-15)", "This Organization")]
    [InlineData("(A;;0x2;;;S-1-5-113)", "Local account, which every local account holds")]
    [InlineData("(A;;0x2;;;S-1-5-3)", "BATCH, which a standard user's own scheduled task holds")]
    [InlineData("(A;;FA;;;LS)", "LOCAL SERVICE")]
    [InlineData("(A;;FA;;;NS)", "NETWORK SERVICE")]
    [InlineData("(A;;0x2;;;AC)", "ALL APPLICATION PACKAGES")]
    public void ARuleThatLetsSomeoneWhoIsNotAnAdministratorChangeItCounts(string ace, string why) => Assert.True(Changeable(Sddl.AdminOnlyAnd(ace)), why);

    [Theory]
    [InlineData("(A;OICIIO;0x1301bf;;;BU)", "only for what is made in it later")]
    [InlineData("(A;;0x1200a9;;;WD)", "read and run only")]
    [InlineData("(A;;0x110;;;BU)", "attributes only, which change no file")]
    [InlineData("(D;;FA;;;BU)", "a deny")]
    [InlineData("(A;;FA;;;BA)", "administrators")]
    [InlineData("(A;;FA;;;SY)", "SYSTEM")]
    [InlineData("(A;;FA;;;" + Sddl.TrustedInstallerSid + ")", "TrustedInstaller")]
    [InlineData("(A;;FA;;;S-1-5-114)", "Local account and member of Administrators group")]
    [InlineData("(A;;FA;;;CO)", "CREATOR OWNER on the folder itself gives nobody anything")]
    [InlineData("(A;;FA;;;OW)", "OWNER RIGHTS: the owner, judged as the owner")]
    [InlineData("(A;;FA;;;CG)", "CREATOR GROUP on the folder itself gives nobody anything")]
    [InlineData("(A;OICIIO;FA;;;CG)", "CREATOR GROUP for what is made in it later, judged where it lands")]
    public void ARuleThatGivesNoOneButAnAdministratorAChangeDoesNotCount(string ace, string why) => Assert.False(Changeable(Sddl.AdminOnlyAnd(ace)), why);

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
        // CREATOR GROUP: a file made in it gives the maker's primary group ("None", "Domain Users") the right, so it counts.
        Assert.True(Changeable(Sddl.AdminOnlyAnd("(A;OIIO;FA;;;CG)"), filesInIt: true));
        Assert.True(Changeable(Sddl.AdminOnlyAnd("(A;OICI;0x2;;;CG)"), filesInIt: true));
        Assert.False(Changeable(Sddl.AdminOnlyAnd("(A;CIIO;FA;;;CG)"), filesInIt: true));
        Assert.False(Changeable(Sddl.AdminOnlyAnd("(A;OIIO;GXGR;;;BU)"), filesInIt: true));
    }

    [Fact]
    public void AnOwnerWhoIsNotAnAdministratorOrNoRulesAtAllCount()
    {
        Assert.True(Changeable("O:BUG:SYD:(A;;FA;;;SY)(A;;FA;;;BA)")); // owned by Users: an owner may always change who has access
        Assert.True(Changeable($"O:{Sddl.SomeoneSid}G:SYD:(A;;FA;;;SY)(A;;FA;;;BA)"));
        Assert.True(Changeable($"O:{Sddl.OtherSid}G:SYD:(A;;FA;;;SY)(A;;FA;;;BA)")); // whoever is signed in
        Assert.True(Changeable("O:LSG:SYD:(A;;FA;;;SY)(A;;FA;;;BA)"));
        Assert.True(Changeable("O:BAG:SYD:NO_ACCESS_CONTROL")); // a null list: everyone may do anything
        Assert.True(Changeable("O:BAG:SY"));                    // no list at all
        Assert.False(Changeable("O:BAG:SYD:"));                 // an empty list: nobody may
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544", Sddl.TrustedInstallerSid, "S-1-5-114" })
            Assert.False(Changeable($"O:{sid}G:SYD:(A;;FA;;;BA)(A;;FA;;;{sid})"), sid);
        Assert.Contains("S-1-5-21-1-2-3-1001", InstallGuard.WhoMayChange(new RawSecurityDescriptor($"O:{Sddl.SomeoneSid}G:SYD:(A;;FA;;;BA)")));
        Assert.Equal("its permissions could not be read", InstallGuard.WhoMayChange(null));
    }

    /// <summary>
    /// The groups UAC treats as elevated: their members without UAC (or without administrator rights) hold them in every
    /// program, so only Administrators and "Local account and member of Administrators group" are no way in.
    /// </summary>
    private static readonly string[] ElevatedGroupsThatAreNotAdministrators =
    [
        "S-1-5-32-547", "S-1-5-32-548", "S-1-5-32-549", "S-1-5-32-550", "S-1-5-32-551", "S-1-5-32-553", "S-1-5-32-556",
        "S-1-5-21-1-2-3-512", "S-1-5-21-1-2-3-516", "S-1-5-21-1-2-3-517", "S-1-5-21-1-2-3-518", "S-1-5-21-1-2-3-519", "S-1-5-21-1-2-3-520",
    ];

    [Fact]
    public void OnlyAdministratorsAndWindowsItselfAreNoWayIn()
    {
        foreach (var group in ElevatedGroupsThatAreNotAdministrators)
            Assert.True(Changeable(Sddl.AdminOnlyAnd($"(A;OICI;FA;;;{group})")), group);
        // An account that owns what it makes (UAC off, or the built-in Administrator, with Windows' "default owner:
        // object creator") counts too: its SID is an account's, which programs without administrator rights may hold.
        Assert.True(Changeable(Sddl.InstalledByItsOwner));
        // A Microsoft Entra ID account's SID is not from the PC or a domain (S-1-12-1-...): it still counts, as owner and in a rule.
        var entra = "S-1-12-1-1111111111-2222222222-3333333333-4444444444";
        Assert.True(Changeable($"O:{entra}G:SYD:(A;;FA;;;SY)(A;;FA;;;BA)"));
        Assert.True(Changeable(Sddl.AdminOnlyAnd($"(A;OICI;FA;;;{entra})")));
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544", Sddl.TrustedInstallerSid, "S-1-5-114" })
            Assert.True(InstallGuard.IsAdmin(new SecurityIdentifier(sid)), sid);
        foreach (var sid in new[] { "S-1-5-19", "S-1-5-20", "S-1-5-32-545", "S-1-5-21-1-2-3-500", "S-1-5-21-1-2-3-512", "S-1-5-80-0", Sddl.SomeoneSid })
            Assert.False(InstallGuard.IsAdmin(new SecurityIdentifier(sid)), sid);
    }

    [Fact]
    public void AConditionalRuleCountsAsAllowedAndAnUnknownKindOfRuleFailsClosed()
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        RawSecurityDescriptor With(GenericAce ace)
        {
            var acl = new RawAcl(GenericAcl.AclRevision, 1);
            acl.InsertAce(0, ace);
            return new(ControlFlags.DiscretionaryAclPresent, admins, admins, null, acl);
        }
        Assert.True(InstallGuard.LetsNonAdminsChange(With(new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, 0x2, users, isCallback: true, opaque: null))));
        Assert.True(InstallGuard.LetsNonAdminsChange(With(new CustomAce((AceType)0x20, AceFlags.None, new byte[4]))));
        Assert.False(InstallGuard.LetsNonAdminsChange(With(new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, 0x2, admins, isCallback: false, opaque: null))));
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

    /// <summary>Every folder is where it is named (no link on the way).</summary>
    private static string? Itself(string folder) => folder;

    private static OpenPlace? Open(string file, Dictionary<string, string> rules, Func<string, string?>? realPath = null) =>
        InstallGuard.UserWritable(file, Roots, Rules(rules).Read, realPath ?? Itself);

    private const string Tools = @"C:\Program Files\Vendor\Tools";
    private const string Ram = Tools + @"\RAMMap64.exe";
    private const string UsersModify = "(A;OICI;0x1301bf;;;BU)";

    [Fact]
    public void EveryFolderFromTheProgramsUpToTheRootIsJudgedAndNoneAboveThenTheProgramItself()
    {
        var (read, asked) = Rules([]);
        Assert.Null(InstallGuard.UserWritable(Ram, Roots, read, Itself));
        Assert.Equal([Tools, @"C:\Program Files\Vendor", @"C:\Program Files", Ram], asked); // never C:\, where users may add folders

        // A folder above can be renamed and made again, so one users may change counts for the folders below it too.
        var folder = new OpenPlace(Tools, Ram, OpenBecause.FolderRules);
        Assert.Equal(folder, Open(Ram, new() { [@"C:\Program Files\Vendor"] = Sddl.AdminOnlyAnd(UsersModify) }));
        Assert.Equal(folder, Open(Ram, new() { [Tools] = Sddl.AdminOnlyAnd(UsersModify) }));
        Assert.Equal(folder, Open(Ram, new() { [@"C:\Program Files"] = Sddl.AdminOnlyAnd(UsersModify) }));
        // A rule for the files made in a folder counts in the program's own, where it reaches the program.
        Assert.Equal(folder, Open(Ram, new() { [Tools] = Sddl.AdminOnlyAnd("(A;OIIO;FA;;;BU)") }));
        Assert.Null(Open(Ram, new() { [@"C:\Program Files\Vendor"] = Sddl.AdminOnlyAnd("(A;OIIO;FA;;;BU)") }));
        // Another user's SID counts, whoever runs the check.
        Assert.Equal(folder, Open(Ram, new() { [Tools] = Sddl.AdminOnlyAnd($"(A;OICI;FA;;;{Sddl.OtherSid})") }));
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
    public void AFolderOnTheWayThatIsALinkIsJudgedWhereItReallyIsUpToItsRoot()
    {
        const string real = @"C:\Program Files (x86)\Elsewhere\Tools";
        Func<string, string?> linked = f => f.Equals(Tools, StringComparison.OrdinalIgnoreCase) ? real : f;
        // It leads to an admin-only place: every folder there up to its root is judged, and passes.
        var (read, asked) = Rules([]);
        Assert.Null(InstallGuard.UserWritable(Ram, Roots, read, linked));
        Assert.Equal([Tools, @"C:\Program Files\Vendor", @"C:\Program Files", Ram, real, @"C:\Program Files (x86)\Elsewhere", @"C:\Program Files (x86)"], asked);
        // A parent of where it leads lets users change it: refused, naming that folder.
        Assert.Equal(new OpenPlace(real, Ram, OpenBecause.FolderRules),
            Open(Ram, new() { [@"C:\Program Files (x86)\Elsewhere"] = Sddl.AdminOnlyAnd(UsersModify) }, linked));
        // It leads outside Program Files: refused without reading the rules there.
        Assert.Equal(new OpenPlace(@"D:\Apps\Tools", Ram, OpenBecause.LinkedFolder),
            Open(Ram, [], f => f.Equals(Tools, StringComparison.OrdinalIgnoreCase) ? @"D:\Apps\Tools" : f));
        // Where it leads cannot be told.
        Assert.Equal(new OpenPlace(Tools, Ram, OpenBecause.Unreadable), Open(Ram, [], _ => null));
        Assert.Equal(new OpenPlace(Tools, Ram, OpenBecause.Unreadable), Open(Ram, [], _ => throw new IOException("no")));
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
        Assert.Equal(new OpenPlace(@"C:\Tools", @"C:\Tools\RAMMap64.exe", OpenBecause.OutsideAdminRoots), InstallGuard.UserWritable(@"C:\Tools\RAMMap64.exe", Roots, read, Itself));
        Assert.Equal(@"C:\Program Files Evil", InstallGuard.UserWritable(@"C:\Program Files Evil\RAMMap64.exe", Roots, read, Itself)?.Folder);
        Assert.Empty(asked);
    }

    private const string PwFolder = @"C:\Program Files\PoolWatch", PwExe = PwFolder + @"\PoolWatch.exe";

    /// <summary>PoolWatch's folder as listed (path, folder, link): the exe, its DLLs, the uninstaller, and a folder of its own.</summary>
    private static Dictionary<string, List<FolderEntry>> Install(params FolderEntry[] more)
    {
        var listing = new Dictionary<string, List<FolderEntry>>(StringComparer.OrdinalIgnoreCase)
        {
            [PwFolder] = [new(PwExe, false, false), new(PwFolder + @"\pdh-helper.dll", false, false), new(PwFolder + @"\unins000.exe", false, false),
                new(PwFolder + @"\knowledge", true, false)],
            [PwFolder + @"\knowledge"] = [new(PwFolder + @"\knowledge\pack.json", false, false)],
        };
        foreach (var e in more) listing[Path.GetDirectoryName(e.Path)!].Add(e);
        return listing;
    }

    private static AdminPlaces Places(Dictionary<string, string> rules, Dictionary<string, List<FolderEntry>>? listing = null,
        Func<string, string?>? realPath = null) =>
        new(Roots, Rules(rules).Read, realPath ?? Itself, d => (listing ?? Install()).TryGetValue(d, out var l) ? l : throw new DirectoryNotFoundException(d));

    [Fact]
    public void EveryFileAndFolderInPoolWatchsOwnFolderIsJudgedByItsOwnRules()
    {
        Assert.Null(InstallGuard.WhyNotFrom(PwExe, Places([])));
        string dll = PwFolder + @"\pdh-helper.dll", pack = PwFolder + @"\knowledge\pack.json", sub = PwFolder + @"\knowledge";
        // A DLL next to the exe that users may change (moved there with its own rules), or one another account owns.
        Assert.Equal(new OpenPlace(PwFolder, dll, OpenBecause.InItsFolder), Places(new() { [dll] = Sddl.AdminOnlyAnd(UsersModify) }).ChangeableInItsFolder(PwExe));
        Assert.Equal(new OpenPlace(PwFolder, pack, OpenBecause.InItsFolder), Places(new() { [pack] = $"O:{Sddl.OtherSid}G:SYD:(A;;FA;;;BA)" }).ChangeableInItsFolder(PwExe));
        // A folder in it that lets users add files, also only through the rule it gives the files made in it.
        Assert.Equal(new OpenPlace(PwFolder, sub, OpenBecause.InItsFolder), Places(new() { [sub] = Sddl.AdminOnlyAnd("(A;;0x2;;;BU)") }).ChangeableInItsFolder(PwExe));
        Assert.Equal(new OpenPlace(PwFolder, sub, OpenBecause.InItsFolder), Places(new() { [sub] = Sddl.AdminOnlyAnd("(A;OIIO;FA;;;BU)") }).ChangeableInItsFolder(PwExe));
        // A link in it, a file whose rules cannot be read, a folder that cannot be listed, a file that is a link by its read.
        Assert.Equal(new OpenPlace(PwFolder, PwFolder + @"\evil.dll", OpenBecause.LinkInItsFolder),
            Places([], Install(new FolderEntry(PwFolder + @"\evil.dll", false, true))).ChangeableInItsFolder(PwExe));
        Assert.Equal(new OpenPlace(PwFolder, dll, OpenBecause.Unreadable), Places(new() { [dll] = Sddl.Unreadable }).ChangeableInItsFolder(PwExe));
        Assert.Equal(new OpenPlace(PwFolder, dll, OpenBecause.LinkInItsFolder), Places(new() { [dll] = Sddl.Link }).ChangeableInItsFolder(PwExe));
        var unlisted = Install();
        unlisted.Remove(sub);
        Assert.Equal(new OpenPlace(PwFolder, sub, OpenBecause.Unreadable), Places([], unlisted).ChangeableInItsFolder(PwExe));
        // Gone since it was listed: nothing to judge.
        Assert.Null(Places(new() { [dll] = Sddl.NotThere }).ChangeableInItsFolder(PwExe));
        // The exe itself is judged by UserWritable only (its own rules), not twice.
        Assert.Null(Places(new() { [PwExe] = Sddl.Installed }).ChangeableInItsFolder(PwExe));

        // And the start refuses with a message that names the file.
        string? why = InstallGuard.WhyNotFrom(PwExe, Places(new() { [dll] = Sddl.AdminOnlyAnd(UsersModify) }));
        Assert.Contains(@"The permissions of C:\Program Files\PoolWatch\pdh-helper.dll, in PoolWatch's folder, let users without administrator rights change it", why);
        Assert.Contains("Uninstall PoolWatch, delete that folder, and install it again", why);
        Assert.Contains(@"C:\Program Files\PoolWatch\evil.dll, in PoolWatch's folder, is a link",
            InstallGuard.WhyNotFrom(PwExe, Places([], Install(new FolderEntry(PwFolder + @"\evil.dll", false, true)))));
    }

    [Fact]
    public void AFolderThatHoldsFarMoreThanPoolWatchsOwnIsNotReadToTheEnd()
    {
        var many = Install();
        for (int i = 0; i < InstallGuard.MaxEntriesInItsFolder; i++) many[PwFolder].Add(new(PwFolder + $@"\f{i}.dat", false, false));
        Assert.Equal(OpenBecause.TooMuchInItsFolder, Places([], many).ChangeableInItsFolder(PwExe)?.Because);
        var deep = Install();
        string d = PwFolder;
        for (int i = 0; i <= InstallGuard.MaxDepthInItsFolder; i++)
        {
            string next = d + $@"\d{i}";
            deep.TryAdd(d, []);
            deep[d].Add(new(next, true, false));
            d = next;
        }
        deep.TryAdd(d, []);
        Assert.Equal(OpenBecause.TooDeepInItsFolder, Places([], deep).ChangeableInItsFolder(PwExe)?.Because);
    }

    [Fact]
    public void AnElevatedPoolWatchRefusesToRunFromAFolderOrAFileUsersMayChangeAsFromOneOutsideProgramFiles()
    {
        const string folder = PwFolder, exe = PwExe;
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
        Assert.Contains(@"C:\Program Files\PoolWatch leads through a link to D:\PoolWatch, a place outside Program Files",
            InstallGuard.WhyNotFrom(exe, Places([], realPath: f => f.Equals(folder, StringComparison.OrdinalIgnoreCase) ? @"D:\PoolWatch" : f)));

        // In the installed layout the program is app\PoolWatch.exe, and the launcher, the install folder's PoolWatch.exe, is
        // judged the same way: the logon task starts it as administrator with no prompt. So is PoolWatch-selfcontained.exe,
        // the self-contained zip's name for it, when it is there; with no launcher at all, the run is refused.
        const string program = folder + @"\app\PoolWatch.exe", launcher = exe, other = folder + @"\PoolWatch-selfcontained.exe";
        // PoolWatch's own folder in that layout is app\, which holds the program (each file and folder in it is judged by
        // its own rules too: ChangeableInItsFolder).
        var appListing = Install();
        appListing[folder + @"\app"] = [new(program, false, false)];
        AdminPlaces At(Dictionary<string, string> rules) => Places(rules, appListing);
        Func<string, bool> onlyLauncher = p => p == launcher, both = p => p == launcher || p == other;
        Assert.Null(InstallGuard.WhyNotStartFrom(program, At([]), onlyLauncher, NoFiles));
        Assert.Contains($"The permissions of {launcher} let users without administrator rights change it",
            InstallGuard.WhyNotStartFrom(program, At(new() { [launcher] = Sddl.AdminOnlyAnd(UsersModify) }), onlyLauncher, NoFiles));
        Assert.Contains($"{launcher} is a link to a file elsewhere", InstallGuard.WhyNotStartFrom(program, At(new() { [launcher] = Sddl.Link }), onlyLauncher, NoFiles));
        Assert.Contains($"PoolWatch could not read the permissions of {launcher}", InstallGuard.WhyNotStartFrom(program, At(new() { [launcher] = Sddl.NotThere }), onlyLauncher, NoFiles));
        Assert.Contains($"The permissions of {other} let users", InstallGuard.WhyNotStartFrom(program, At(new() { [other] = Sddl.AdminOnlyAnd(UsersModify) }), both, NoFiles));
        Assert.Null(InstallGuard.WhyNotStartFrom(program, At(new() { [other] = Sddl.AdminOnlyAnd(UsersModify) }), onlyLauncher, NoFiles)); // not there: not started
        Assert.Contains($"PoolWatch could not read the permissions of {launcher}", InstallGuard.WhyNotStartFrom(program, At(new() { [launcher] = Sddl.NotThere }), _ => false, NoFiles));
        // The program's own answer comes first.
        Assert.Contains($"The permissions of {program} let users",
            InstallGuard.WhyNotStartFrom(program, At(new() { [program] = Sddl.AdminOnlyAnd(UsersModify), [launcher] = Sddl.Link }), onlyLauncher, NoFiles));

        // And every other file below the program's own folder, the native DLLs PoolWatch loads from app\amd64\ among them,
        // with the folders each is in: one a standard user could change, in a folder they could change, or that is a link,
        // refuses the run, and so does a folder whose files cannot be listed. The program itself is judged once.
        const string app = folder + @"\app", amd64 = app + @"\amd64", dll = amd64 + @"\KernelTraceControl.dll", dia = amd64 + @"\msdia140.dll";
        var listed = new List<string>();
        IEnumerable<string> Below(string f) { listed.Add(f); return [program, dll, dia]; }
        Assert.Null(InstallGuard.WhyNotStartFrom(program, At([]), onlyLauncher, Below));
        Assert.Equal([app], listed);
        Assert.Contains($"The permissions of {dia} let users without administrator rights change it",
            InstallGuard.WhyNotStartFrom(program, At(new() { [dia] = Sddl.AdminOnlyAnd(UsersModify) }), onlyLauncher, Below));
        Assert.Contains($"The permissions of {amd64} or a folder above it let users without administrator rights change",
            InstallGuard.WhyNotStartFrom(program, At(new() { [amd64] = Sddl.AdminOnlyAnd(UsersModify) }), onlyLauncher, Below));
        Assert.Contains($"{dll} is a link to a file elsewhere", InstallGuard.WhyNotStartFrom(program, At(new() { [dll] = Sddl.Link }), onlyLauncher, Below));
        Assert.Contains($"could not list the files in {app} to check", InstallGuard.WhyNotStartFrom(program, At([]), onlyLauncher, _ => throw new UnauthorizedAccessException("denied")));
        Assert.Contains($"could not list the files in {app} to check", InstallGuard.WhyNotStartFrom(program, At([]), onlyLauncher, _ => throw new DirectoryNotFoundException("gone")));
        // This PC's real call is the one that lists every file, and RefuseElevatedFromUserWritable uses it.
        Assert.Contains("message = WhyNotStartFrom(exe, AdminPlaces.OfThisPc(), File.Exists, FilesBelow) ?? \"\";",
            File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Engine", "InstallGuard.cs")));

        // Setup run by an account that owns what it makes: refused while that account's programs may run without
        // administrator rights, since its SID counts then.
        var owned = new Dictionary<string, string> { [folder] = Sddl.InstalledByItsOwner, [exe] = Sddl.InstalledByItsOwner };
        Assert.NotNull(InstallGuard.WhyNotFrom(exe, Places(owned)));
    }

    /// <summary>No other file below the program's folder (the tests of the program and its launchers).</summary>
    private static IEnumerable<string> NoFiles(string folder) => [];

    /// <summary>On disk, every file below a folder is listed, in every folder below it, hidden and system files too.</summary>
    [Fact]
    public void EveryFileBelowTheProgramsFolderIsListed()
    {
        string dir = Directory.CreateTempSubdirectory("pw-files-below-").FullName;
        try
        {
            string top = Path.Combine(dir, "PoolWatch.exe"), deep = Path.Combine(dir, "amd64", "x", "a.dll"), hidden = Path.Combine(dir, "amd64", "h.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(deep)!);
            foreach (var f in new[] { top, deep, hidden }) File.WriteAllText(f, "");
            File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);
            Assert.Equal(new[] { top, deep, hidden }.Order(StringComparer.OrdinalIgnoreCase), InstallGuard.FilesBelow(dir).Order(StringComparer.OrdinalIgnoreCase));
            File.SetAttributes(hidden, FileAttributes.Normal);
            Assert.Throws<DirectoryNotFoundException>(() => InstallGuard.FilesBelow(Path.Combine(dir, "not there")).ToList());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// On disk, a folder below the program's that cannot be opened is never skipped: the listing throws, and the run is
    /// refused (a listing that skipped it would leave what is in it unjudged). The folder is made so without any access
    /// rule: an empty folder marked for deletion while a handle to it stays open is still listed in its parent, and
    /// Windows answers every open of it with "access denied" (STATUS_DELETE_PENDING) until that handle closes. First shown:
    /// it is exactly the folder a listing that ignores what it cannot open (<see cref="EnumerationOptions.IgnoreInaccessible"/>)
    /// skips without a word.
    /// </summary>
    [Fact]
    public void AFolderBelowTheProgramsThatCannotBeOpenedRefusesTheRun()
    {
        string dir = Directory.CreateTempSubdirectory("pw-files-below-").FullName;
        try
        {
            string app = Path.Combine(dir, "app"), program = Path.Combine(app, "PoolWatch.exe"), dll = Path.Combine(app, "amd64", "a.dll"),
                   held = Path.Combine(app, "amd64", "held");
            Directory.CreateDirectory(held);
            foreach (var f in new[] { program, dll }) File.WriteAllText(f, "");
            using (PendingDelete.Mark(held))
            {
                Assert.Contains(held, Directory.EnumerateDirectories(Path.GetDirectoryName(held)!));
                var skipping = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true };
                Assert.Equal(new[] { program, dll }.Order(StringComparer.OrdinalIgnoreCase),
                    Directory.EnumerateFiles(app, "*", skipping).Order(StringComparer.OrdinalIgnoreCase));

                Assert.Throws<UnauthorizedAccessException>(() => InstallGuard.FilesBelow(app).ToList());
                // The whole check, with this real listing: every rule as Program Files' own, the launcher there.
                // Its own folder's files are listed as none here, so it is the listing of the files below that refuses the run;
                // with the real listing, the check of PoolWatch's own folder refuses it first, at the same folder.
                var places = new AdminPlaces([dir], Rules([]).Read, Itself, _ => []);
                Assert.Contains($"could not list the files in {app} to check", InstallGuard.WhyNotStartFrom(program, places, _ => true, InstallGuard.FilesBelow));
                Assert.Contains($"PoolWatch could not read the permissions of {held}",
                    InstallGuard.WhyNotStartFrom(program, places with { Contents = InstallGuard.FolderEntries }, _ => true, InstallGuard.FilesBelow));
            }
            Assert.False(Directory.Exists(held));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>An empty folder marked for deletion through an open handle: it goes when the handle is closed (no access rule changes).</summary>
    private static class PendingDelete
    {
        private const uint Delete = 0x00010000, ShareAll = 7, OpenExisting = 3, BackupSemantics = 0x02000000;
        private const int FileDispositionInfo = 4;

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, int infoClass, ref byte deleteFile, uint size);

        public static Microsoft.Win32.SafeHandles.SafeFileHandle Mark(string folder)
        {
            var handle = CreateFileW(folder, Delete, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception();
            byte deleteFile = 1; // FILE_DISPOSITION_INFO.DeleteFile
            if (SetFileInformationByHandle(handle, FileDispositionInfo, ref deleteFile, 1)) return handle;
            var error = new System.ComponentModel.Win32Exception();
            handle.Dispose();
            throw error;
        }
    }

    [Fact]
    public void WhereEveryProgramOfTheAccountRunsAsAdministratorItsOwnFoldersAreNoWayIn()
    {
        // UAC off, or the built-in Administrator with its Admin Approval Mode off: setup gives the folder and the exe that
        // account as owner and full control (Windows' "default owner: object creator"). PoolWatch must still start there.
        const string folder = PwFolder, exe = PwExe;
        var owned = new Dictionary<string, string> { [folder] = Sddl.InstalledByItsOwner, [exe] = Sddl.InstalledByItsOwner };
        var someone = ElevatedAccounts.Only(new SecurityIdentifier(Sddl.SomeoneSid));
        Assert.Null(InstallGuard.WhyNotFrom(exe, Places(owned) with { AlwaysElevated = someone }));
        Assert.Null(InstallGuard.WhoMayChange(new RawSecurityDescriptor(Sddl.InstalledByItsOwner), filesInIt: true, someone));
        Assert.True(InstallGuard.IsAdmin(new SecurityIdentifier(Sddl.SomeoneSid), someone));
        // Every other account still counts, that one's own rules aside.
        Assert.NotNull(InstallGuard.WhyNotFrom(exe, Places(owned) with { AlwaysElevated = ElevatedAccounts.Only(new SecurityIdentifier(Sddl.OtherSid)) }));
        Assert.NotNull(InstallGuard.WhyNotFrom(exe, Places(new() { [folder] = Sddl.AdminOnlyAnd($"(A;OICI;FA;;;{Sddl.OtherSid})") }) with { AlwaysElevated = someone }));
        Assert.NotNull(InstallGuard.WhyNotFrom(exe, Places(new() { [folder] = Sddl.AdminOnlyAnd(UsersModify) }) with { AlwaysElevated = someone }));
        Assert.False(InstallGuard.IsAdmin(new SecurityIdentifier(Sddl.OtherSid), someone));
        // Files next to the exe are judged with it too.
        string dll = PwFolder + @"\pdh-helper.dll";
        Assert.Equal(dll, Places(new() { [dll] = Sddl.InstalledByItsOwner }).ChangeableInItsFolder(exe)?.Program);
        Assert.Null((Places(new() { [dll] = Sddl.InstalledByItsOwner }) with { AlwaysElevated = someone }).ChangeableInItsFolder(exe));
    }

    /// <summary>
    /// None of the texts it reads says that UAC leaves a batch, service or network sign-in's token unsplit in every case:
    /// they say only that such a token can be unsplit even where UAC splits the account's sign-in in person, so that the
    /// token alone cannot tell, which is why PoolWatch then goes by UAC's settings. It reads README.md, SECURITY.md, the
    /// .md files directly in docs, the guide the app ships for AI assistants and every comment of the C# code under src
    /// (<see cref="RecommendationActionsTests.ScannedTexts"/>); the repository's AGENTS.md, CONTRIBUTING.md and
    /// CHANGELOG.md, the installer's script and every comment of the C# code under tests
    /// (<see cref="RecommendationActionsTests.MoreTexts"/>); no other file. A comment after code on its line, or between
    /// /* and */, is read too (<see cref="RecommendationActionsTests.CommentTexts"/>). A sentence (or a clause between semicolons) is
    /// listed when it has the word "never" just before a word that starts with "split", or before "be", "been", "get",
    /// "gets" or "got" and then such a word, and also the word UAC, batch, service or network, in either order, whether
    /// it says so actively or passively.
    /// </summary>
    [Fact]
    public void NoTextSaysUacNeverSplitsASignInThatIsNotInPerson()
    {
        var never = new System.Text.RegularExpressions.Regex(@"\bnever\s+((be|been|get|gets|got)\s+)?split", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var about = new System.Text.RegularExpressions.Regex(@"\bUAC\b|\b(batch|service|network)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        bool Claims(string sentence) => never.IsMatch(sentence) && about.IsMatch(sentence);
        var texts = RecommendationActionsTests.ScannedTexts().Concat(RecommendationActionsTests.MoreTexts()).ToList();
        var found = texts
            .SelectMany(t => System.Text.RegularExpressions.Regex.Split(t.Text, @"(?<=[.;])\s+").Where(Claims).Select(s => $"{t.File}: {s}"))
            .ToList();
        Assert.True(found.Count == 0, string.Join(Environment.NewLine, found));
        Assert.True(Claims("or, for a batch, service or network sign-in, which UAC never splits, UAC's settings say"));
        Assert.True(Claims("it never splits a batch, service or network sign-in's token (a task set to run whether the user is signed in or not, a service)"));
        Assert.True(Claims("A batch or service sign-in's token is never split."));
        Assert.True(Claims("UAC would never split such a token."));
        Assert.True(Claims("Such a token has never been split, a service's or a batch job's."));
        Assert.True(Claims("A network sign-in's token never gets split"));
        Assert.False(Claims("whose token can be unsplit even where UAC splits the account's sign-in in person"));
        Assert.False(Claims("A batch or service sign-in's token, not split: UAC on, or settings that cannot be read, count no one."));
        // The texts it reads include the tests' comments and the root AGENTS.md.
        Assert.Contains(texts, t => t.File == "AdminFolderRulesTests.cs" && t.Text.Contains("A batch or service sign-in's token, not split"));
        Assert.Contains(texts, t => t.File == "the repository's AGENTS.md" && t.Text.Contains("for a batch, service or network sign-in, UAC's settings say so"));
        // A claim in a comment after code on its line, or between /* and */, is found as well.
        Assert.Equal(["A service sign-in's token is never split.", "UAC would never split such a token."],
            RecommendationActionsTests.CommentTexts(["    _gate = hold; // A service sign-in's token is never split.",
                "    Run(/* UAC would never split such a token. */ x);"]).Where(Claims));
    }

    /// <summary>
    /// The launcher, which compiles these rules, counts as an administrator's own only the account it runs as, and only
    /// when every program of that account runs as administrator: its token holds Administrators and UAC did not split it,
    /// and it is a sign-in in person, whose token UAC would have split otherwise; or, for a batch, service or network
    /// sign-in, whose token can be unsplit even where UAC splits the account's sign-in in person, UAC's settings say UAC is
    /// off, or that the account is this PC's built-in Administrator with its Admin Approval Mode off. Setup's folder, which
    /// such an account owns, is then admin-only to it too. A split token, a token without Administrators, one whose
    /// elevation type could not be read, no account, and a batch sign-in with UAC on (or with settings that cannot be
    /// read), count no one; for a sign-in in person no setting is read.
    /// </summary>
    [Fact]
    public void TheLauncherCountsItsOwnAccountOnlyWhenEveryProgramOfItRunsAsAdministrator()
    {
        static T Never<T>() => throw new InvalidOperationException("not read");
        var someone = new SecurityIdentifier(Sddl.SomeoneSid);
        UacSwitches on = new(ForAll: true, ForBuiltInAdministrator: false), off = new(ForAll: false, ForBuiltInAdministrator: true);
        var owned = new Dictionary<string, string> { [PwFolder] = Sddl.InstalledByItsOwner, [PwExe] = Sddl.InstalledByItsOwner };
        // A batch or service sign-in's token with UAC off, and this PC's built-in Administrator's with its Admin Approval
        // Mode off: the account counts.
        var batch = ElevatedAccounts.OfToken(someone, UnelevatedAccount.ElevationDefault, administrator: true, interactive: false, () => off, Never<SecurityIdentifier?>);
        Assert.Equal([someone], batch.Known);
        Assert.Null((Places(owned) with { AlwaysElevated = batch }).UserWritable(PwExe));
        Assert.Equal([BuiltInAdministrator],
            ElevatedAccounts.OfToken(BuiltInAdministrator, UnelevatedAccount.ElevationDefault, administrator: true, interactive: false, () => on, () => ThisPc).Known);
        var unsplit = ElevatedAccounts.OfToken(someone, UnelevatedAccount.ElevationDefault, administrator: true, interactive: true, Never<UacSwitches?>, Never<SecurityIdentifier?>);
        Assert.Equal(someone, Assert.Single(unsplit.Known));
        Assert.Null((Places(owned) with { AlwaysElevated = unsplit }).UserWritable(PwExe));
        Assert.False(unsplit.Holds(new SecurityIdentifier(Sddl.OtherSid))); // no other account, and Windows is asked about none
        foreach (var none in new[]
                 {
                     ElevatedAccounts.OfToken(someone, UnelevatedAccount.ElevationFull, administrator: true, interactive: true, Never<UacSwitches?>, Never<SecurityIdentifier?>),
                     ElevatedAccounts.OfToken(someone, UnelevatedAccount.ElevationLimited, administrator: false, interactive: true, Never<UacSwitches?>, Never<SecurityIdentifier?>),
                     ElevatedAccounts.OfToken(someone, 0, administrator: true, interactive: true, Never<UacSwitches?>, Never<SecurityIdentifier?>),
                     ElevatedAccounts.OfToken(someone, UnelevatedAccount.ElevationDefault, administrator: false, interactive: true, Never<UacSwitches?>, Never<SecurityIdentifier?>),
                     ElevatedAccounts.OfToken(null, UnelevatedAccount.ElevationDefault, administrator: true, interactive: true, Never<UacSwitches?>, Never<SecurityIdentifier?>),
                     // A batch or service sign-in's token, not split: UAC on, or settings that cannot be read, count no one.
                     ElevatedAccounts.OfToken(someone, UnelevatedAccount.ElevationDefault, administrator: true, interactive: false, () => on, () => ThisPc),
                     ElevatedAccounts.OfToken(someone, UnelevatedAccount.ElevationDefault, administrator: true, interactive: false, () => null, () => ThisPc),
                     // The built-in Administrator only of this PC, and only with its Admin Approval Mode off.
                     ElevatedAccounts.OfToken(DomainAdministrator, UnelevatedAccount.ElevationDefault, administrator: true, interactive: false, () => on, () => ThisPc),
                     ElevatedAccounts.OfToken(BuiltInAdministrator, UnelevatedAccount.ElevationDefault, administrator: true, interactive: false, () => new(true, true), () => ThisPc),
                     ElevatedAccounts.OfToken(BuiltInAdministrator, UnelevatedAccount.ElevationDefault, administrator: true, interactive: false, () => on, () => null),
                 })
        {
            Assert.Empty(none.Known);
            Assert.NotNull((Places(owned) with { AlwaysElevated = none }).UserWritable(PwExe));
        }
        // This process's own token reads: at most its own account counts. INTERACTIVE is S-1-5-4.
        Assert.True(ElevatedAccounts.OfThisProcess().Known.Count <= 1);
        Assert.Equal("S-1-5-4", UnelevatedAccount.Interactive.Value);
        // The token's own answers go in: its elevation type, Administrators, INTERACTIVE, and UAC's settings and this PC's
        // account domain as read (only when they are asked for).
        string rules = UninstallerTests.OneLine(File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Engine", "AdminFolderRules.cs")));
        Assert.Contains("return OfToken(me.User, UnelevatedAccount.ElevationTypeOfThisProcess(), token.IsInRole(WindowsBuiltInRole.Administrator), " +
                        "token.IsInRole(UnelevatedAccount.Interactive), UacSwitches.Read, UnelevatedAccount.ReadAccountDomain);", rules);
        // The launcher's folder rules go by it, with each folder where it really is.
        string launcherProgram = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch.Launcher", "Program.cs"));
        Assert.Contains("AlwaysElevated = ElevatedAccounts.OfThisProcess(),", launcherProgram);
        Assert.Contains("InstallGuard.ReadSecurity, Native.FolderFinalPathOf, InstallGuard.FolderEntries)", launcherProgram);
    }

    private static readonly SecurityIdentifier ThisPc = new("S-1-5-21-1-2-3"), BuiltInAdministrator = new("S-1-5-21-1-2-3-500"),
        Alice = new(Sddl.SomeoneSid), Bob = new(Sddl.OtherSid), DomainAdministrator = new("S-1-5-21-9-9-9-500"), DomainAdmins = new("S-1-5-21-9-9-9-512"),
        Carol = new("S-1-5-21-9-9-9-1105"), Dave = new("S-1-5-21-9-9-9-1106"), Erin = new("S-1-12-1-1-2-3-4");

    /// <summary>
    /// Whether Windows finds an account a member of Administrators from its SID: Carol is, through Domain Admins; Dave is
    /// not; Erin's cannot be told (no domain controller answers, say). Each question is recorded.
    /// </summary>
    private static Func<SecurityIdentifier, bool?> MemberBySid(List<SecurityIdentifier> asked) => sid =>
    {
        asked.Add(sid);
        return sid == Carol ? true : sid == Erin ? null : false;
    };

    [Fact]
    public void WhoseEveryProgramRunsAsAdministratorIsDecidedFromThePcsUacSettings()
    {
        // The group's direct members: this PC's Administrator, Alice, a domain's Administrator (not this PC's own) and
        // Domain Admins, a group.
        var members = new List<SecurityIdentifier> { BuiltInAdministrator, Alice, DomainAdministrator, DomainAdmins };
        var me = new AlwaysElevated.Account(Bob, Administrator: true);
        var asked = new List<SecurityIdentifier>();

        // Windows' defaults (UAC on, the built-in Administrator not in Admin Approval Mode): only this PC's built-in
        // Administrator, and no account is asked about (that could wait for a domain controller).
        var defaults = AlwaysElevated.Of(new(ForAll: true, ForBuiltInAdministrator: false), members, ThisPc, me, MemberBySid(asked));
        Assert.Equal([BuiltInAdministrator], defaults.Known);
        Assert.False(defaults.Holds(Carol));
        Assert.False(defaults.Holds(DomainAdministrator));
        Assert.Empty(asked);

        // UAC off: every member. The direct ones (a group among them) and the account PoolWatch runs as up front; one that
        // is a member only through a group when Windows says so, asked once; one Windows cannot tell about is not counted.
        var off = AlwaysElevated.Of(new(ForAll: false, ForBuiltInAdministrator: false), members, ThisPc, me, MemberBySid(asked));
        Assert.Equal(new HashSet<SecurityIdentifier> { BuiltInAdministrator, Alice, DomainAdministrator, DomainAdmins, Bob }, off.Known);
        Assert.True(off.Holds(DomainAdmins));
        Assert.True(off.Holds(Carol));
        Assert.True(off.Holds(Carol));
        Assert.False(off.Holds(Dave));
        Assert.False(off.Holds(Erin));
        Assert.False(off.Holds(new SecurityIdentifier("S-1-5-32-545"))); // Users: a group, never asked about
        Assert.False(off.Holds(new SecurityIdentifier("S-1-5-11")));     // Authenticated Users, the same
        Assert.Equal([Carol, Dave, Erin], asked);
        Assert.True(InstallGuard.IsAdmin(Carol, off));
        Assert.False(InstallGuard.IsAdmin(Dave, off));

        // Admin Approval Mode for every administrator, the built-in one included: none. Settings that could not be read: none.
        Assert.Empty(AlwaysElevated.Of(new(ForAll: true, ForBuiltInAdministrator: true), members, ThisPc, me, MemberBySid(asked)).Known);
        Assert.Same(ElevatedAccounts.None, AlwaysElevated.Of(null, members, ThisPc, me, MemberBySid(asked)));
        Assert.False(ElevatedAccounts.None.Holds(Carol));

        // The members could not be read: the account PoolWatch runs as when it is an administrator, and Windows' answer.
        var unread = AlwaysElevated.Of(new(ForAll: false, ForBuiltInAdministrator: true), null, null, me, MemberBySid(asked));
        Assert.Equal([Bob], unread.Known);
        Assert.True(unread.Holds(Carol));
        Assert.Empty(AlwaysElevated.Of(new(ForAll: false, ForBuiltInAdministrator: true), null, null, me with { Administrator = false }, null).Known);
        // So with UAC off, an administrator that the members, as read, do not list (here they could not be read) and that
        // Windows cannot tell about counts when PoolWatch runs as it, and not when another account runs PoolWatch
        // (SECURITY.md says so).
        var asErin = AlwaysElevated.Of(new(ForAll: false, ForBuiltInAdministrator: true), null, null, new AlwaysElevated.Account(Erin, Administrator: true), MemberBySid(asked));
        Assert.True(asErin.Holds(Erin));
        Assert.False(unread.Holds(Erin));

        // The built-in Administrator counts only as this PC's own account, and only while it is an administrator.
        Assert.Empty(AlwaysElevated.Of(new(ForAll: true, ForBuiltInAdministrator: false), [Alice], ThisPc, me, null).Known); // taken out of the group
        Assert.Equal([BuiltInAdministrator], AlwaysElevated.Of(new(ForAll: true, ForBuiltInAdministrator: false), null, ThisPc,
            new AlwaysElevated.Account(BuiltInAdministrator, Administrator: true), null).Known);
        Assert.Empty(AlwaysElevated.Of(new(ForAll: true, ForBuiltInAdministrator: false), [DomainAdministrator], ThisPc,
            new AlwaysElevated.Account(DomainAdministrator, Administrator: true), null).Known);
        // With UAC on, that account counts through PoolWatch's own token when PoolWatch runs as it, and not when another
        // account runs PoolWatch, while the group's members cannot be read or do not list it (SECURITY.md says so).
        var runsAsIt = new AlwaysElevated.Account(BuiltInAdministrator, Administrator: true);
        Assert.Equal([BuiltInAdministrator], AlwaysElevated.Of(new(ForAll: true, ForBuiltInAdministrator: false), [Alice], ThisPc, runsAsIt, null).Known);
        var askedWithUacOn = new List<SecurityIdentifier>();
        var another = AlwaysElevated.Of(new(ForAll: true, ForBuiltInAdministrator: false), null, ThisPc, me, MemberBySid(askedWithUacOn));
        Assert.Empty(another.Known);
        Assert.False(another.Holds(BuiltInAdministrator));
        Assert.Empty(askedWithUacOn); // with UAC on no account is asked about
        Assert.Empty(AlwaysElevated.Of(new(ForAll: true, ForBuiltInAdministrator: false), [Alice], ThisPc, runsAsIt with { Administrator = false }, null).Known);
        Assert.Empty(AlwaysElevated.Of(new(ForAll: true, ForBuiltInAdministrator: false), members, null, me, null).Known); // no account domain read
    }

    [Theory]
    [InlineData(null, null, true, false)]  // neither value there: Windows' defaults (UAC on, the built-in Administrator not in Admin Approval Mode)
    [InlineData(0, null, false, false)]    // UAC off
    [InlineData(1, 1, true, true)]
    [InlineData(null, 0, true, false)]
    [InlineData(0, 1, false, true)]
    [InlineData("0", "0", true, true)]     // a value that is not a number counts as on: fewer accounts count
    [InlineData(0L, null, true, false)]    // a QWORD is not the DWORD Windows reads: on
    public void UacsSwitchesAreReadWithWindowsDefaults(object? enableLua, object? filterAdministratorToken, bool forAll, bool forBuiltIn) =>
        Assert.Equal(new UacSwitches(forAll, forBuiltIn), UacSwitches.Of(enableLua, filterAdministratorToken));

    [Fact]
    public void WithUacOnNoAccountIsLookedUpAndWithBothSwitchesOnNothingButTheSettingsIsRead()
    {
        // A read that must not be made throws; ElevatedAccounts.Holds would then count nothing, and OfThisPc gives none.
        static T Never<T>() => throw new InvalidOperationException("not read");
        var members = new List<SecurityIdentifier> { BuiltInAdministrator, Alice };
        var me = new AlwaysElevated.Account(Bob, true);

        var reads = new List<string>();
        var both = AlwaysElevated.OfThisPc(new(() => new(true, true), () => { reads.Add("members"); return members; },
            () => { reads.Add("domain"); return ThisPc; }, () => { reads.Add("me"); return me; }, _ => { reads.Add("asked"); return true; }));
        Assert.Same(ElevatedAccounts.None, both);
        Assert.False(both.Holds(Carol));
        Assert.Empty(reads); // both switches on: nothing but UAC's settings is read

        var asked = new List<SecurityIdentifier>();
        var defaults = AlwaysElevated.OfThisPc(new(() => new(true, false), () => members, () => ThisPc, () => me, MemberBySid(asked)));
        Assert.Equal([BuiltInAdministrator], defaults.Known);
        Assert.False(defaults.Holds(Carol));
        Assert.Empty(asked); // UAC on: no account is asked about

        var off = AlwaysElevated.OfThisPc(new(() => new(false, true), () => members, Never<SecurityIdentifier?>, () => me, MemberBySid(asked)));
        Assert.Equal(new HashSet<SecurityIdentifier> { BuiltInAdministrator, Alice, Bob }, off.Known); // no account domain read
        Assert.True(off.Holds(Carol));
        Assert.Equal([Carol], asked);

        Assert.Same(ElevatedAccounts.None, AlwaysElevated.OfThisPc(new(() => null, Never<IReadOnlyCollection<SecurityIdentifier>?>, Never<SecurityIdentifier?>,
            Never<AlwaysElevated.Account?>, _ => Never<bool?>())));
    }

    [Fact]
    public void TheRealReadsFindThisPcsUacSettingsAndItsBuiltInAdministrator()
    {
        // Read only: the policy key, the Administrators group's members as SIDs and this PC's account domain (any account
        // may read them).
        Assert.NotNull(UacSwitches.Read());
        var members = AlwaysElevated.ReadAdministrators();
        Assert.NotNull(members);
        var domain = UnelevatedAccount.ReadAccountDomain();
        Assert.NotNull(domain);
        Assert.StartsWith("S-1-5-21-", domain.Value);
        Assert.Contains(new SecurityIdentifier(WellKnownSidType.AccountAdministratorSid, domain), members);
        // What this PC's settings give is what the start check (AdminPlaces.OfThisPc) and the read of a stand-in folder's
        // record (WindowsRootOps.RecordedFallback, given the key's rules and value) judge with: a key such an account owns
        // is trusted by both, and one the Users group owns by neither.
        var always = AlwaysElevated.OfThisPc();
        Assert.Equal(always.Known, AdminPlaces.OfThisPc().AlwaysElevated.Known);
        foreach (var sid in always.Known)
        {
            var owned = new RawSecurityDescriptor($"O:{sid.Value}G:SYD:(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;{sid.Value})");
            Assert.NotNull(InstallGuard.WhoMayChange(owned, true));
            Assert.Null(InstallGuard.WhoMayChange(owned, true, always));
            Assert.Equal(@"C:\x", WindowsRootOps.RecordOf(owned, @"C:\x", always));
            Assert.Equal(@"C:\x", WindowsRootOps.RecordedFallback(() => (owned, @"C:\x")));
        }
        var users = new RawSecurityDescriptor("O:BUG:SYD:(A;;FA;;;SY)(A;;FA;;;BA)");
        Assert.NotNull(InstallGuard.WhoMayChange(users, true, always)); // the start check's judgement: not admin-only
        Assert.Null(WindowsRootOps.RecordOf(users, @"C:\x", always));
        Assert.Null(WindowsRootOps.RecordedFallback(() => (users, @"C:\x")));
        Assert.Null(WindowsRootOps.RecordedFallback(() => null)); // no key
        // A key only administrators and SYSTEM may change: its value is the record only when it is text, and a read that
        // throws gives none.
        var admins = new RawSecurityDescriptor("O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)");
        Assert.Equal(@"C:\x", WindowsRootOps.RecordedFallback(() => (admins, @"C:\x")));
        Assert.Null(WindowsRootOps.RecordedFallback(() => (admins, null))); // no value
        Assert.Null(WindowsRootOps.RecordedFallback(() => (admins, 42))); // not text
        Assert.Null(WindowsRootOps.RecordedFallback(() => throw new UnauthorizedAccessException("the key cannot be read")));
    }

    [Fact]
    public void WindowsWorksOutAnAccountsAdministratorsMembershipFromItsSid()
    {
        // Read only: the groups of the test's own account, worked out by Windows from its SID, against those in its own
        // token (where UAC keeps Administrators only to deny, it is still a membership).
        using var me = WindowsIdentity.GetCurrent();
        bool inToken = me.Claims.Any(c => c.Type is System.Security.Claims.ClaimTypes.GroupSid or System.Security.Claims.ClaimTypes.DenyOnlySid &&
                                          c.Value == "S-1-5-32-544");
        var groups = AlwaysElevated.GroupsOf(me.User!);
        bool? answer = AlwaysElevated.IsAdministratorBySid(me.User!);
        if (me.User!.AccountDomainSid is { } domain && domain == UnelevatedAccount.ReadAccountDomain())
        {
            // An account of this PC's own (CI's runner, a local account): Windows answers from this PC's account database.
            Assert.NotNull(groups);
            Assert.Contains(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), groups);
            Assert.Equal(inToken, answer);
        }
        else
        {
            // A domain or Microsoft Entra ID account (a developer's PC): Windows may need a domain controller or the cloud to
            // answer. No answer is not counted (the check then fails closed); an answer must match the token.
            Assert.True(answer is null || answer == inToken, $"Authz says {answer}, the token {inToken}");
        }
        // A SID no account has: Windows cannot tell, so it is not counted.
        Assert.Null(AlwaysElevated.IsAdministratorBySid(new SecurityIdentifier("S-1-5-21-1-2-3-4242")));
        // A group's SID has an account's form and is asked about the same way: this PC's "None" group, which is not a
        // member of Administrators, is not counted.
        var thisPc = UnelevatedAccount.ReadAccountDomain();
        Assert.NotNull(thisPc);
        var noneGroup = new SecurityIdentifier(thisPc.Value + "-513");
        Assert.True(ElevatedAccounts.IsAccount(noneGroup));
        Assert.NotEqual(true, AlwaysElevated.IsAdministratorBySid(noneGroup));
    }

    [Fact]
    public void AQuestionAboutAnAccountThatFailsDoesNotCountIt()
    {
        // The Authz question throwing (a native failure, say) counts as "not an administrator": the check fails closed.
        int asked = 0;
        var failing = new ElevatedAccounts([], sid => { asked++; throw new InvalidOperationException("the question failed"); });
        Assert.False(failing.Holds(Carol));
        Assert.False(failing.Holds(Carol)); // asked once: the answer is kept
        Assert.Equal(1, asked);
        Assert.False(InstallGuard.IsAdmin(Carol, failing));
        Assert.NotNull(InstallGuard.WhoMayChange(new RawSecurityDescriptor($"O:{Carol.Value}G:SYD:PAI(A;;FA;;;SY)(A;;FA;;;BA)"), alwaysElevated: failing));
    }

    [Fact]
    public void TheLogonTasksAccountIsCheckedAgainstTheAccountThisPoolWatchRunsAs()
    {
        using var me = WindowsIdentity.GetCurrent();
        string dir = Path.Combine(Path.GetTempPath(), "pw-tasks-" + Guid.NewGuid().ToString("N"));
        try
        {
            var tasks = new Ui.WindowsLogonTasks(new AppPaths(dir, null, secure: false)); // nothing is read from or written to Task Scheduler
            Assert.True(tasks.IsThisAccount(me.Name));
            Assert.True(tasks.IsThisAccount(me.User!.Value));
            Assert.False(tasks.IsThisAccount("S-1-5-21-1-2-3-1002"));
            Assert.False(tasks.IsThisAccount(null));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void ATooDeepFolderAndATooFullOneAreToldApart()
    {
        var many = Install();
        for (int i = 0; i < InstallGuard.MaxEntriesInItsFolder; i++) many[PwFolder].Add(new(PwFolder + $@"\f{i}.dat", false, false));
        string? full = InstallGuard.WhyNotFrom(PwExe, Places([], many));
        Assert.Contains("holds more than 5,000 files and folders", full);
        var deep = Install();
        string d = PwFolder;
        for (int i = 0; i <= InstallGuard.MaxDepthInItsFolder; i++)
        {
            string next = d + $@"\d{i}";
            deep.TryAdd(d, []);
            deep[d].Add(new(next, true, false));
            d = next;
        }
        deep.TryAdd(d, []);
        string? tooDeep = InstallGuard.WhyNotFrom(PwExe, Places([], deep));
        Assert.Contains($"holds folders more than {InstallGuard.MaxDepthInItsFolder} levels deep", tooDeep);
        Assert.DoesNotContain("5,000", tooDeep);
    }

    [Fact]
    public void SetupsInstalledFactUsesTheFullCheckNotThePlaceAlone()
    {
        Assert.True(Engine.Setup.WindowsEnvironmentReads.IsInstalledInProgramFiles(PwExe, Places([])));
        // In Program Files, but a DLL next to it that users may change: not installed as PoolWatch must be.
        Assert.False(Engine.Setup.WindowsEnvironmentReads.IsInstalledInProgramFiles(PwExe, Places(new() { [PwFolder + @"\pdh-helper.dll"] = Sddl.AdminOnlyAnd(UsersModify) })));
        Assert.False(Engine.Setup.WindowsEnvironmentReads.IsInstalledInProgramFiles(PwExe, Places(new() { [PwFolder] = Sddl.AdminOnlyAnd(UsersModify) })));
        Assert.False(Engine.Setup.WindowsEnvironmentReads.IsInstalledInProgramFiles(@"C:\Users\someone\Downloads\PoolWatch.exe", Places([])));
        Assert.False(Engine.Setup.WindowsEnvironmentReads.IsInstalledInProgramFiles(null, Places([])));
    }
}
