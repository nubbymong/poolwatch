using System.Security.AccessControl;
using System.Security.Principal;
using PoolWatch.Engine;
using static PoolWatch.Engine.DataRootGuard;

namespace PoolWatch.AppTests;

/// <summary>
/// S3 (review round 3): the data root itself (a standard user can create %ProgramData%\PoolWatch before the first elevated
/// run). The decision is pure; Prepare runs against fake steps that record calls; the real steps are only used read-only
/// (inspect) or to rename a TEMP folder. No test writes an ACL.
/// </summary>
public sealed class DataRootGuardTests : IDisposable
{
    private static readonly SecurityIdentifier Owner = new("S-1-5-21-1111111111-2222222222-3333333333-1001");
    private const string User = "S-1-5-21-1111111111-2222222222-3333333333-1002";
    private const string Users = "S-1-5-32-545";
    private static readonly IReadOnlyList<Ace> Mine = Expected(Owner);
    private readonly string _base = Path.Combine(Path.GetTempPath(), "pw-root-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _links = new();

    public DataRootGuardTests() => Directory.CreateDirectory(_base);

    public void Dispose()
    {
        bool gone = true;
        foreach (var l in _links) gone &= Links.DeleteLinkOnly(l);
        if (gone) try { Directory.Delete(_base, recursive: true); } catch { }
    }

    private static Facts Folder(string owner, bool isProtected, params Ace[] aces) => new(true, false, true, owner, isProtected, aces);
    private static Ace Allow(string sid, int mask, bool inherited = false, bool inheritOnly = false) => new(sid, true, mask, inherited, inheritOnly, 3);
    private const int FullControl = 0x1F01FF, ReadExecute = 0x1200A9;

    // ------------------------------------------------------------------ the pure decision

    [Fact]
    public void ALinkIsMovedAsideNotRefused()
    {
        // M2: refusing a link let a standard user who planted a junction at %ProgramData%\PoolWatch stop the elevated
        // PoolWatch from starting at every logon. It is moved aside instead (the link itself, through its own handle).
        var d = Decide(new Facts(true, IsReparsePoint: true), Mine);
        Assert.Equal(Verdict.MoveAside, d.Verdict);
        Assert.Contains("link", d.Reason);
    }

    [Fact]
    public void AMissingRootIsCreated() => Assert.Equal(Verdict.Create, Decide(new Facts(false), Mine).Verdict);

    [Fact]
    public void AnAdminOwnedRootWithExactlyPoolWatchsPermissionsIsKept() =>
        Assert.Equal(Verdict.Keep, Decide(Folder(Administrators, true, [.. Mine]), Mine).Verdict);

    [Theory]
    [InlineData(User)]       // a standard user created it
    [InlineData(Users)]      // a group owns it
    public void AForeignOwnerMovesItAside(string owner)
    {
        var d = Decide(Folder(owner, true, [.. Mine]), Mine);
        Assert.Equal(Verdict.MoveAside, d.Verdict);
        Assert.Contains(owner, d.Reason);
    }

    [Theory]
    [InlineData(0x2, false)]       // create files
    [InlineData(0x4, false)]       // create folders
    [InlineData(0x40000, false)]   // WRITE_DAC only
    [InlineData(0x80000, false)]   // WRITE_OWNER only
    [InlineData(0x10000, false)]   // DELETE
    [InlineData(0x40000000, false)]// GENERIC_WRITE
    [InlineData(0x6, true)]        // inherited from %ProgramData% (Users may create files and folders there)
    public void AnyoneElseWhoMayChangeItMovesItAside(int mask, bool inherited)
    {
        var d = Decide(Folder(Administrators, !inherited, [.. Mine, Allow(Users, mask, inherited)]), Mine);
        Assert.Equal(Verdict.MoveAside, d.Verdict);
        Assert.Contains(Users, d.Reason);
    }

    [Fact]
    public void ReadDenyAndInheritOnlyEntriesDoNotCount()
    {
        var aces = new List<Ace>(Mine)
        {
            new(Users, Allow: false, 0x1F01FF, false, false, 3),                // a deny entry only restricts
            Allow("S-1-3-0", FullControl, inheritOnly: true),                   // CREATOR OWNER: children only
        };
        var d = Decide(Folder(Administrators, true, [.. aces]), Mine);
        Assert.NotEqual(Verdict.MoveAside, d.Verdict);
        Assert.Equal(Verdict.Repair, d.Verdict); // not exactly PoolWatch's: applied once
    }

    [Fact]
    public void TheRoundOneRootIsRepairedOnceNotMovedAside()
    {
        // Round 1: Administrators and SYSTEM full control, Users read (protected). Safe owner, no writer: re-permissioned.
        var roundOne = Folder(Administrators, true, Allow(Administrators, FullControl), Allow(LocalSystem, FullControl), Allow(Users, ReadExecute));
        Assert.Equal(Verdict.Repair, Decide(roundOne, Mine).Verdict);
        Assert.Equal(Verdict.Repair, Decide(Folder(Administrators, false, [.. Mine]), Mine).Verdict); // not protected
        Assert.Equal(Verdict.Repair, Decide(Folder(LocalSystem, true, [.. Mine]), Mine).Verdict);    // SYSTEM-owned: owner set once
    }

    [Fact]
    public void AFileOrUnreadablePermissionsMoveItAside()
    {
        Assert.Equal(Verdict.MoveAside, Decide(new Facts(true, IsDirectory: false), Mine).Verdict);
        Assert.Equal(Verdict.MoveAside, Decide(new Facts(true, Unreadable: "error 5"), Mine).Verdict);
    }

    // ------------------------------------------------------------------ Prepare, with recording fakes

    private sealed class FakeOps(params Facts[] script) : IRootOps
    {
        private int _next;
        public readonly List<string> Calls = new();
        public int CreateResult;
        public Facts Inspect(string root) { Calls.Add("inspect"); return script[Math.Min(_next++, script.Length - 1)]; }
        public int CreateSecure(string root, DirectorySecurity security) { Calls.Add("create"); return CreateResult; }
        public void ApplySecure(string root, DirectorySecurity security) => Calls.Add("apply");
        public string MoveAside(string root, DateTime utcNow) { Calls.Add("move"); return root + ".untrusted-x"; }
    }

    private static readonly Facts Exact = Folder(Administrators, true, [.. Mine]);

    [Fact]
    public void AnExactRootIsUsedAsItIsWithNoPermissionsReapplied()
    {
        // Review round 3, item 6: the ACL is no longer re-applied across the tree on every start.
        var ops = new FakeOps(Exact);
        Assert.Empty(Prepare(@"C:\ProgramData\PoolWatch", Owner, ops, () => DateTime.UtcNow));
        Assert.Equal(["inspect"], ops.Calls);
    }

    [Fact]
    public void APreCreatedJunctionIsMovedAsideAndPoolWatchStillStarts()
    {
        // M2: the junction is renamed away, a fresh admin-only root is created and checked; no exception, so PoolWatch starts.
        var ops = new FakeOps(new Facts(true, IsReparsePoint: true), new Facts(false), Exact);
        var notes = Prepare(@"C:\ProgramData\PoolWatch", Owner, ops, () => DateTime.UtcNow);
        Assert.Equal(["inspect", "move", "inspect", "create", "inspect"], ops.Calls);
        Assert.Contains(notes, n => n.Contains("link") && n.Contains("moved aside"));
    }

    [Fact]
    public void ARootThatCannotBeMovedAsideIsStillRefused()
    {
        // Fail closed: when the rename itself fails, nothing is written there (MoveAside throws the refusal).
        var ops = new ThrowingMove(new Facts(true, IsReparsePoint: true));
        Assert.Throws<DataRootRefusedException>(() => Prepare(@"C:\ProgramData\PoolWatch", Owner, ops, () => DateTime.UtcNow));
        Assert.Equal(["inspect", "move"], ops.Calls);
    }

    private sealed class ThrowingMove(Facts facts) : IRootOps
    {
        public readonly List<string> Calls = new();
        public Facts Inspect(string root) { Calls.Add("inspect"); return facts; }
        public int CreateSecure(string root, DirectorySecurity security) { Calls.Add("create"); return 0; }
        public void ApplySecure(string root, DirectorySecurity security) => Calls.Add("apply");
        public string MoveAside(string root, DateTime utcNow) { Calls.Add("move"); throw new DataRootRefusedException("could not move it aside"); }
    }

    [Fact]
    public void EveryPredictableAsideNameTakenStillLeavesARandomOne()
    {
        // M2: pre-creating PoolWatch.untrusted-<stamp> and -2 ... -9 must not make the move (and so the start) fail.
        var names = WindowsRootOps.AsideNames(@"C:\ProgramData", "PoolWatch", "20261026T224500Z").ToList();
        Assert.Equal(11, names.Count);
        Assert.Equal(@"C:\ProgramData\PoolWatch.untrusted-20261026T224500Z", names[0]);
        Assert.Equal(@"C:\ProgramData\PoolWatch.untrusted-20261026T224500Z-9", names[8]);
        Assert.Matches(@"PoolWatch\.untrusted-20261026T224500Z-[0-9a-f]{32}$", names[9]);
        Assert.NotEqual(names[9], names[10]);
    }

    [Fact]
    public void AForeignRootIsMovedAsideAndANewOneCreatedAndChecked()
    {
        var ops = new FakeOps(Folder(User, false, Allow(User, FullControl)), new Facts(false), Exact);
        var notes = Prepare(@"C:\ProgramData\PoolWatch", Owner, ops, () => DateTime.UtcNow);
        Assert.Equal(["inspect", "move", "inspect", "create", "inspect"], ops.Calls);
        Assert.Contains(notes, n => n.Contains("moved aside") && n.Contains(".untrusted-"));
    }

    [Fact]
    public void ADifferentButSafeRootIsRepairedOnceThenKept()
    {
        var roundOne = Folder(Administrators, true, Allow(Administrators, FullControl), Allow(LocalSystem, FullControl), Allow(Users, ReadExecute));
        var ops = new FakeOps(roundOne, Exact);
        Prepare(@"C:\ProgramData\PoolWatch", Owner, ops, () => DateTime.UtcNow);
        Assert.Equal(["inspect", "apply", "inspect"], ops.Calls);

        // Still different in detail after applying (never a reason to refuse to start): kept, with a note.
        var stubborn = new FakeOps(roundOne, roundOne);
        var notes = Prepare(@"C:\ProgramData\PoolWatch", Owner, stubborn, () => DateTime.UtcNow);
        Assert.Equal(["inspect", "apply", "inspect"], stubborn.Calls);
        Assert.Contains(notes, n => n.Contains("still differ"));
    }

    [Fact]
    public void AMissingRootIsCreatedWithItsPermissionsAndChecked()
    {
        var ops = new FakeOps(new Facts(false), Exact);
        Assert.Empty(Prepare(@"C:\ProgramData\PoolWatch", Owner, ops, () => DateTime.UtcNow));
        Assert.Equal(["inspect", "create", "inspect"], ops.Calls);

        // Someone created it between the check and the create: the create fails "exists", and what is there is judged.
        var raced = new FakeOps(new Facts(false), Folder(User, false, Allow(User, FullControl)), new Facts(false), Exact) { CreateResult = 183 };
        Prepare(@"C:\ProgramData\PoolWatch", Owner, raced, () => DateTime.UtcNow);
        Assert.Equal(["inspect", "create", "inspect", "move", "inspect", "create", "inspect"], raced.Calls);
    }

    // ------------------------------------------------------------------ the real steps, read-only or a rename in TEMP

    /// <summary>The real inspection, recording fakes for every step that would write.</summary>
    private sealed class InspectOnly : IRootOps
    {
        private readonly WindowsRootOps _real = new();
        public readonly List<string> Writes = new();
        public Facts Inspect(string root) => _real.Inspect(root);
        public int CreateSecure(string root, DirectorySecurity security) { Writes.Add("create"); return 0; }
        public void ApplySecure(string root, DirectorySecurity security) => Writes.Add("apply");
        public string MoveAside(string root, DateTime utcNow) { Writes.Add("move"); return root; }
    }

    [Fact]
    public void AJunctionPlantedAsTheDataRootIsSeenWithoutFollowingIt()
    {
        string target = Path.Combine(_base, "elsewhere");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "must survive");
        string root = Path.Combine(_base, "PoolWatch");
        Links.Junction(root, target);
        _links.Add(root);

        Assert.True(new WindowsRootOps().Inspect(root).IsReparsePoint);
        var ops = new InspectOnly();
        // The fake move never really moves it, so the root stays a link until Prepare gives up: nothing is ever created or
        // secured through the link, and its target is untouched.
        Assert.Throws<DataRootRefusedException>(() => new AppPaths(root, null, secure: true, rootOps: ops));
        Assert.DoesNotContain("create", ops.Writes);
        Assert.DoesNotContain("apply", ops.Writes);
        Assert.Contains("move", ops.Writes);
        Assert.Equal(["keep.txt"], Directory.GetFiles(target).Select(Path.GetFileName));
    }

    [Fact]
    public void MovingAJunctionAsideRenamesTheLinkNotItsTarget()
    {
        // M2: the real rename, on a junction in TEMP (no ACL is read or written): the link moves, its target stays put.
        string target = Path.Combine(_base, "elsewhere");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "must survive");
        string root = Path.Combine(_base, "PoolWatch");
        Links.Junction(root, target);
        _links.Add(root);

        string moved = new WindowsRootOps().MoveAside(root, new DateTime(2026, 10, 26, 22, 45, 0, DateTimeKind.Utc));
        _links.Add(moved);
        Assert.False(Directory.Exists(root) || File.Exists(root));
        Assert.True(File.GetAttributes(moved).HasFlag(FileAttributes.ReparsePoint)); // the link itself was renamed
        Assert.Equal(["keep.txt"], Directory.GetFiles(target).Select(Path.GetFileName)); // its target was not moved
    }

    [Fact]
    public void MoveAsideKeepsTryingWhenAnEarlyNameIsTaken()
    {
        // The real rename must advance past a candidate name that already exists (no ACL op: a plain folder in TEMP).
        string root = Path.Combine(_base, "PoolWatch");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "settings.json"), "{}");
        var when = new DateTime(2026, 10, 26, 22, 45, 0, DateTimeKind.Utc);
        string firstName = WindowsRootOps.AsideNames(_base, "PoolWatch", "20261026T224500Z").First();
        Directory.CreateDirectory(firstName); // the first aside name is taken
        string moved = new WindowsRootOps().MoveAside(root, when);
        Assert.EndsWith("-2", moved);           // so it moved to the second
        Assert.False(Directory.Exists(root));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(moved, "settings.json")));
    }

    [Fact]
    public void ARealFolderIsReadThroughAHandle()
    {
        string root = Path.Combine(_base, "real");
        Directory.CreateDirectory(root);
        var f = new WindowsRootOps().Inspect(root);
        Assert.True(f.Exists);
        Assert.False(f.IsReparsePoint);
        Assert.True(f.IsDirectory);
        Assert.Null(f.Unreadable);
        Assert.NotNull(f.OwnerSid);
        Assert.NotEmpty(f.Aces!);
        Assert.False(new WindowsRootOps().Inspect(Path.Combine(_base, "missing")).Exists);
    }

    [Fact]
    public void MovingAsideRenamesTheFolderItself()
    {
        string root = Path.Combine(_base, "PoolWatch");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "settings.json"), "{}");
        string moved = new WindowsRootOps().MoveAside(root, new DateTime(2026, 10, 26, 22, 45, 0, DateTimeKind.Utc));
        Assert.Equal(Path.Combine(_base, "PoolWatch.untrusted-20261026T224500Z"), moved);
        Assert.False(Directory.Exists(root));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(moved, "settings.json")));
        // The same second again: a second name, never over the first.
        Directory.CreateDirectory(root);
        Assert.EndsWith("-2", new WindowsRootOps().MoveAside(root, new DateTime(2026, 10, 26, 22, 45, 0, DateTimeKind.Utc)));
    }
}
