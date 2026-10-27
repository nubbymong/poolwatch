using PoolWatch.Core.Incidents;
using PoolWatch.Core.Util;

namespace PoolWatch.Tests;

/// <summary>Review Part 3, addendum 2 C: kernel functions in the CPU trace, from local admin-only symbol folders only.</summary>
public class CpuPartThreeTests
{
    private static readonly HashSet<string> Usable = new(StringComparer.OrdinalIgnoreCase) { @"C:\ProgramData\PoolWatch\symbols", @"D:\sym cache", @"C:\pdbs" };
    private static bool IsUsable(string d) => Usable.Contains(d);

    private const string Admins = "S-1-5-32-544", System = "S-1-5-18", Users = "S-1-5-32-545", AuthUsers = "S-1-5-11", Owner = "S-1-3-0";
    private const int Read = 0x1 | 0x8 | 0x20 | 0x80 | 0x20000, Modify = 0x1301BF, Full = 0x1F01FF, CreateFiles = 0x2;

    [Fact]
    public void OnlyLocalFoldersAreUsedNeverAServerOrAShare()
    {
        Assert.Equal(@"C:\pdbs", SymbolPaths.LocalOnly(@"srv*C:\pdbs*https://msdl.microsoft.com/download/symbols", IsUsable));
        Assert.Equal(@"D:\sym cache", SymbolPaths.LocalOnly(@"symsrv*symsrv.dll*""D:\sym cache""*http://server/symbols", IsUsable));
        Assert.Null(SymbolPaths.LocalOnly(@"srv*https://msdl.microsoft.com/download/symbols", IsUsable)); // a server only: nothing
        Assert.Null(SymbolPaths.LocalOnly(@"\\fileserver\symbols;C:\symbols", IsUsable));                 // a share; a folder not usable
        Assert.Null(SymbolPaths.LocalOnly(null, IsUsable));
        Assert.Equal(@"C:\pdbs", SymbolPaths.LocalOnly(@"C:\pdbs;c:\PDBS", IsUsable));                      // no duplicates
    }

    [Fact]
    public void TheSettingElseTheDataRootsSymbolsFolder()
    {
        Assert.Equal(@"C:\pdbs", SymbolPaths.Resolve(@"C:\pdbs", @"C:\ProgramData\PoolWatch\symbols", IsUsable));
        Assert.Equal(@"C:\ProgramData\PoolWatch\symbols", SymbolPaths.Resolve("", @"C:\ProgramData\PoolWatch\symbols", IsUsable));
        Assert.Equal(@"C:\ProgramData\PoolWatch\symbols", SymbolPaths.Resolve(@"C:\symbols", @"C:\ProgramData\PoolWatch\symbols", IsUsable)); // not usable
        Assert.Null(SymbolPaths.Resolve("", @"C:\missing", IsUsable));
    }

    [Fact]
    public void ASymbolFolderMustBeAdminOnly()
    {
        // PoolWatch's own data root: Administrators and SYSTEM full control, Users read.
        Assert.True(SymbolPaths.AdminOnly(Admins, [new(Admins, true, Full), new(System, true, Full), new(Users, true, Read)]));
        // CREATOR OWNER (applies to the owner, here Administrators) and a deny rule for users are fine.
        Assert.True(SymbolPaths.AdminOnly(Admins, [new(Admins, true, Full), new(Owner, true, Full), new(Users, false, Full)]));
        // The usual C:\symbols under C:\: Authenticated Users may modify; or users may create files; or a user owns it.
        Assert.False(SymbolPaths.AdminOnly(Admins, [new(Admins, true, Full), new(AuthUsers, true, Modify)]));
        Assert.False(SymbolPaths.AdminOnly(Admins, [new(Admins, true, Full), new(Users, true, Read | CreateFiles)]));
        Assert.False(SymbolPaths.AdminOnly("S-1-5-21-1-2-3-1001", [new(Admins, true, Full)]));
        Assert.False(SymbolPaths.AdminOnly(Admins, [new(Users, true, 0x40000000)])); // GENERIC_WRITE on an inheritable rule
    }

    [Fact]
    public void FunctionsAreNamedOrModulePlusOffset()
    {
        Assert.Equal("refs.sys!RefsLookupFcb", CpuProfile.FrameName("refs.sys", "RefsLookupFcb", 0x1a2b0));
        Assert.Equal("refs.sys+0x1a2b0", CpuProfile.FrameName("refs.sys", null, 0x1a2b0));
        Assert.StartsWith("Kernel functions are module + offset: no admin-only symbol folder", CpuProfile.NoteFor(null, false));
        Assert.Contains("never reads symbols from a folder users can write to, a network drive or a symbol server", CpuProfile.NoteFor(null, false));
        Assert.StartsWith(@"Kernel function names from the local symbols in C:\pdbs", CpuProfile.NoteFor(@"C:\pdbs", true));
        Assert.StartsWith(@"Kernel functions are module + offset: no matching symbols in C:\pdbs", CpuProfile.NoteFor(@"C:\pdbs", false));
        Assert.Contains("could not be read (bad pdb)", CpuProfile.NoteFor(@"C:\pdbs", false, "bad pdb"));
    }

    [Fact]
    public void SummaryNamesOnlyRealFunctions()
    {
        var p = new CpuProfile
        {
            Seconds = 30, Processors = 24, Samples = 720_000,
            TopCpu = [new("refs.sys", 151_200, 0.21), new("FLTMGR.SYS", 64_800, 0.09), new("idle", 300_000, 0.42)],
            TopFunctions = [new("refs.sys!RefsLookupFcb", 90_000, 0.125, true), new("FLTMGR.SYS+0x2f10", 30_000, 0.042, false)],
        };
        Assert.Contains("top kernel functions refs.sys!RefsLookupFcb (13%)", p.Summary());
        Assert.DoesNotContain("FLTMGR.SYS+0x2f10", p.Summary()); // an address is not a function
        var unnamed = new CpuProfile { Seconds = 30, Processors = 24, TopFunctions = [new("FLTMGR.SYS+0x2f10", 30_000, 0.042, false)] };
        Assert.DoesNotContain("top kernel functions", unnamed.Summary());
    }
}
