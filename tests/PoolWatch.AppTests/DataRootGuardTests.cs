using System.Security.AccessControl;
using System.Security.Principal;
using PoolWatch.Engine;
using static PoolWatch.Engine.DataRootGuard;

namespace PoolWatch.AppTests;

/// <summary>
/// The data root itself (a standard user can create %ProgramData%\PoolWatch before the first elevated
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
        // Refusing a link let a standard user who planted a junction at %ProgramData%\PoolWatch stop the elevated
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
    public void TheRootAnEarlierBuildMadeIsRepairedOnceNotMovedAside()
    {
        // An earlier build's rules: Administrators and SYSTEM full control, Users read (protected). Safe owner, no writer: re-permissioned.
        var earlierRules = Folder(Administrators, true, Allow(Administrators, FullControl), Allow(LocalSystem, FullControl), Allow(Users, ReadExecute));
        Assert.Equal(Verdict.Repair, Decide(earlierRules, Mine).Verdict);
        Assert.Equal(Verdict.Repair, Decide(Folder(Administrators, false, [.. Mine]), Mine).Verdict); // not protected
        Assert.Equal(Verdict.Repair, Decide(Folder(LocalSystem, true, [.. Mine]), Mine).Verdict);    // SYSTEM-owned: owner set once
    }

    [Fact]
    public void AFileOrUnreadablePermissionsMoveItAside()
    {
        Assert.Equal(Verdict.MoveAside, Decide(new Facts(true, IsDirectory: false), Mine).Verdict);
        Assert.Equal(new Decision(Verdict.MoveAside, "its permissions cannot be read (error 5)"), Decide(new Facts(true, Unreadable: "error 5"), Mine));
        // Administrators may not open it as it is, and the backup privilege read whose it is: never used as it is, and the
        // reason names a foreign owner or writer when there is one.
        Assert.Equal(new Decision(Verdict.MoveAside, $"it is owned by {User}, not Administrators or SYSTEM"),
            Decide(Folder(User, false, Allow(User, FullControl)) with { Unreadable = "error 5" }, Mine));
        Assert.Equal(new Decision(Verdict.MoveAside, $"{Users} may change it (its own permission 0x2)"),
            Decide(Folder(Administrators, true, [.. Mine, Allow(Users, 0x2)]) with { Unreadable = "error 5" }, Mine));
        Assert.Equal(new Decision(Verdict.MoveAside, "its permissions cannot be read (error 5)"), Decide(Exact with { Unreadable = "error 5" }, Mine));
    }

    // ------------------------------------------------------------------ Prepare, with recording fakes

    private sealed class FakeOps(params Facts[] script) : IRootOps
    {
        private int _next;
        public readonly List<string> Calls = new();
        public int CreateResult;
        /// <summary>Folders next to the root, by full path, with what each is (stand-ins PoolWatch made earlier, or someone else's).</summary>
        public readonly Dictionary<string, Facts> Beside = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>What a folder next to the root is at every look after its first (null: the same as at the first).</summary>
        public Facts? BesideLater;
        /// <summary>
        /// What a folder next to the root is at its first looks, in turn (an entry that is null, and every look past the
        /// last entry: as <see cref="Beside"/> and <see cref="BesideLater"/> say).
        /// </summary>
        public readonly List<Facts?> BesideAt = new();
        public bool MoveResult = true;
        /// <summary>Moving the root aside fails, as WindowsRootOps.MoveAside throws when something holds it open.</summary>
        public bool MoveAsideFails;
        /// <summary>
        /// A folder next to the root whose look fails: inspecting it throws (an I/O error, say). WindowsRootOps.Inspect
        /// returns Facts with Unreadable instead when its open or its permissions fail; tests put that case in <see
        /// cref="Beside"/>.
        /// </summary>
        public string? Unreadable;
        public Facts Inspect(string root)
        {
            Calls.Add(Beside.ContainsKey(root) || root == Unreadable ? "inspect:" + Path.GetFileName(root) : "inspect");
            if (root == Unreadable) throw new IOException("the device is not ready");
            if (Beside.TryGetValue(root, out var f))
            {
                int look = Calls.Count(c => c == "inspect:" + Path.GetFileName(root));
                if (look <= BesideAt.Count && BesideAt[look - 1] is { } at) return at;
                return BesideLater is { } later && look > 1 ? later : f;
            }
            return script[Math.Min(_next++, script.Length - 1)];
        }
        /// <summary>
        /// The recorded stand-in (RecordedFallback), and every record written (RecordFallback: null clears it).
        /// <see cref="UntrustedRecord"/>: a record is there that someone who is not an administrator could have written, so
        /// RecordedFallback reads none. <see cref="RecordThrows"/>: reading the record throws (the real steps never do: they
        /// read a record they cannot read as none).
        /// </summary>
        public string? Recorded;
        public bool UntrustedRecord, RecordThrows;
        public readonly List<string?> Records = new();
        /// <summary>False: writing or deleting a record fails (it stays as it was).</summary>
        public bool RecordResult = true;
        public string? RecordedFallback() => RecordThrows ? throw new IOException("the record cannot be read") : Recorded;
        public bool HasRecord() => RecordThrows ? throw new IOException("the record cannot be read") : Recorded is not null || UntrustedRecord;
        public bool RecordFallback(string? path)
        {
            Records.Add(path);
            if (RecordResult) { Recorded = path; UntrustedRecord = false; }
            return RecordResult;
        }
        public bool MoveTo(string from, string to) { Calls.Add("moveto:" + Path.GetFileName(from)); return MoveResult; }
        public int CreateSecure(string root, DirectorySecurity security) { Calls.Add(Path.GetFileName(root).Contains(FallbackMark) ? "create:safe" : "create"); return CreateResult; }
        /// <summary>
        /// Applying PoolWatch's rules to the usual folder fails, as WindowsRootOps.ApplySecure throws when its open is denied.
        /// </summary>
        public bool ApplyFails;
        public void ApplySecure(string root, DirectorySecurity security)
        {
            Calls.Add("apply");
            if (ApplyFails && !Path.GetFileName(root).Contains(FallbackMark))
                throw new DataRootRefusedException($"PoolWatch could not open its data folder {root} to secure it (error 5).");
        }
        public string MoveAside(string root, DateTime utcNow)
        {
            Calls.Add("move");
            if (MoveAsideFails) throw new DataRootRefusedException($"PoolWatch could not move {root} aside (error 32).");
            return root + ".untrusted-x";
        }
        /// <summary>The lists of changes each folder holds, by full path (none unless named here).</summary>
        public readonly Dictionary<string, string[]> Lists = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<string> ChangeListsIn(string folder) => Lists.TryGetValue(folder, out var lists) ? lists : [];
        /// <summary>What MayWrite answers for every folder (false: administrators may not make files there).</summary>
        public bool MayWriteResult = true;
        public bool MayWrite(string folder) => MayWriteResult;
    }

    private static readonly Facts Exact = Folder(Administrators, true, [.. Mine]);

    [Fact]
    public void AnExactRootIsUsedAsItIsWithNoPermissionsReapplied()
    {
        // The ACL is no longer re-applied across the tree on every start.
        var ops = new FakeOps(Exact);
        Assert.Empty(Prepare(@"C:\ProgramData\PoolWatch", Owner, ops, () => DateTime.UtcNow));
        Assert.Equal(["inspect"], ops.Calls);
    }

    [Fact]
    public void APreCreatedJunctionIsMovedAsideAndPoolWatchStillStarts()
    {
        // The junction is renamed away, a fresh admin-only root is created and checked; no exception, so PoolWatch starts.
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
        public string? RecordedFallback() => null;
        public bool HasRecord() => false;
        public bool RecordFallback(string? path) => throw new InvalidOperationException("no stand-in here");
        public bool MoveTo(string from, string to) => throw new InvalidOperationException("no stand-in here");
    }

    [Fact]
    public void EveryPredictableAsideNameTakenStillLeavesARandomOne()
    {
        // Pre-creating PoolWatch.untrusted-<stamp> and -2 ... -9 must not make the move (and so the start) fail.
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
        var earlierRules = Folder(Administrators, true, Allow(Administrators, FullControl), Allow(LocalSystem, FullControl), Allow(Users, ReadExecute));
        var ops = new FakeOps(earlierRules, Exact);
        Prepare(@"C:\ProgramData\PoolWatch", Owner, ops, () => DateTime.UtcNow);
        Assert.Equal(["inspect", "apply", "inspect"], ops.Calls);

        // Still different in detail after applying (never a reason to refuse to start): kept, with a note.
        var stubborn = new FakeOps(earlierRules, earlierRules);
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

    // ------------------------------------------------------------------ a root that cannot be made safe: PoolWatch keeps running

    private const string Root = @"C:\ProgramData\PoolWatch";
    private static readonly DateTime Now = new(2026, 11, 1, 2, 30, 0, DateTimeKind.Utc);

    /// <summary>A foreign root someone holds open (the rename fails), or makes again after every move.</summary>
    private sealed class Unmovable(bool remadeEveryTime) : IRootOps
    {
        public readonly List<string> Calls = new();
        public readonly Dictionary<string, Facts> Beside = new(StringComparer.OrdinalIgnoreCase);
        public int CreateResult;
        public Facts? Created = Exact;
        /// <summary>How many looks at a folder next to the root succeed; after them inspecting it throws (null: never).</summary>
        public int? ReadableLooks;
        /// <summary>Applying PoolWatch's rules to a folder next to the root fails (as WindowsRootOps.ApplySecure throws).</summary>
        public bool ApplyFails;
        /// <summary>What a folder next to the root is at every look after the first (null: the same as at the first).</summary>
        public Facts? Later;
        private int _looks;
        public Facts Inspect(string root)
        {
            if (Beside.TryGetValue(root, out var f))
            {
                Calls.Add("inspect:" + Path.GetFileName(root));
                if (ReadableLooks is { } readable && ++_looks > readable) throw new IOException("the device is not ready");
                if (Later is { } later && Calls.Count(c => c == "inspect:" + Path.GetFileName(root)) > 1) return later;
                return f;
            }
            if (root.Equals(Root, StringComparison.OrdinalIgnoreCase)) { Calls.Add("inspect"); return Folder(User, false, Allow(User, FullControl)); }
            Calls.Add("inspect:new");
            return Created ?? new Facts(false);
        }
        public int CreateSecure(string root, DirectorySecurity security)
        {
            Calls.Add(root.Equals(Root, StringComparison.OrdinalIgnoreCase) ? "create" : "create:new");
            return CreateResult;
        }
        public void ApplySecure(string root, DirectorySecurity security)
        {
            Calls.Add("apply:" + Path.GetFileName(root));
            if (ApplyFails && Beside.ContainsKey(root)) throw new DataRootRefusedException($"PoolWatch could not secure its data folder {root} (error 32).");
        }
        public string MoveAside(string root, DateTime utcNow)
        {
            Calls.Add("move");
            if (!remadeEveryTime) throw new DataRootRefusedException($"PoolWatch could not move {root} aside (error 32).");
            return root + ".untrusted-x"; // moved, but the same name is back before the next look
        }
        /// <summary>The recorded stand-in, and every record written (null clears it); RecordResult false: writing a record fails.</summary>
        public string? Recorded;
        public readonly List<string?> Records = new();
        public bool RecordResult = true;
        public string? RecordedFallback() => Recorded;
        public bool HasRecord() => Recorded is not null;
        public bool RecordFallback(string? path) { Records.Add(path); if (RecordResult) Recorded = path; return RecordResult; }
        public bool MoveTo(string from, string to) { Calls.Add("moveto"); return false; }
    }

    [Theory]
    [InlineData(false)] // held open: the rename fails
    [InlineData(true)]  // made again after every move: Prepare gives up after six
    public void ARootThatCannotBeMadeSafeLeavesPoolWatchRunningOnAFolderOfItsOwn(bool remadeEveryTime)
    {
        var ops = new Unmovable(remadeEveryTime);
        var paths = new AppPaths(Root, null, secure: true, holdsInstance: true, rootOps: ops);
        Assert.Equal(Root, paths.UsualRootNotUsed);
        Assert.Matches(@"^C:\\ProgramData\\PoolWatch\.safe-\d{8}T\d{6}Z-[0-9a-f]{32}$", paths.DataRoot); // a name no one can guess
        Assert.Equal(1, ops.Calls.Count(c => c == "create:new"));            // made new with its permissions, then checked
        Assert.Equal("inspect:new", ops.Calls[^1]);
        Assert.DoesNotContain("create", ops.Calls);                           // nothing made or written at the unsafe name
        var note = Assert.Single(paths.StartupNotes, n => n.Contains("PoolWatch keeps running"));
        Assert.Contains(paths.DataRoot, note);
        Assert.Contains("only administrators can change", note);
        Assert.Equal([paths.DataRoot], ops.Records); // its name recorded, for the next start to find that one
        // It is what every write is checked against now; the unsafe folder is not.
        Assert.Equal(Path.Combine(paths.DataRoot, "settings.json"), paths.SettingsFile);
        Assert.Throws<UnauthorizedAccessException>(() => paths.Guard.CheckReal(Path.Combine(Root, "uninstall-list.json")));
    }

    [Fact]
    public void TheFolderOfItsOwnRecordedAtAnEarlierStartIsUsedAgainAndNoOtherIsLookedAt()
    {
        var ops = new Unmovable(remadeEveryTime: false);
        string mine = Root + FallbackMark + "20261030T100000Z-aaaa", newerButForeign = Root + FallbackMark + "20261031T100000Z-ffff";
        ops.Beside[mine] = Exact;
        ops.Beside[newerButForeign] = Folder(User, false, Allow(User, FullControl)); // a standard user's, named like one
        ops.Recorded = mine;
        var (used, notes) = PrepareFallback(Root, "why.", Owner, ops, () => Now);
        Assert.Equal(mine, used);
        Assert.DoesNotContain("create:new", ops.Calls);
        Assert.DoesNotContain("inspect:" + Path.GetFileName(newerButForeign), ops.Calls); // never looked at
        Assert.DoesNotContain(ops.Calls, c => c.StartsWith("apply", StringComparison.Ordinal) || c.StartsWith("move", StringComparison.Ordinal));
        Assert.Empty(ops.Records); // the record already names it
        Assert.Contains(mine, Assert.Single(notes));

        // A record that names a folder that is not PoolWatch's own (not admin-only now, say): a new one, recorded in its place.
        var changed = new Unmovable(remadeEveryTime: false) { Recorded = newerButForeign };
        changed.Beside[newerButForeign] = Folder(User, false, Allow(User, FullControl));
        string fresh = PrepareFallback(Root, "why.", Owner, changed, () => Now).Root;
        Assert.NotEqual(newerButForeign, fresh);
        Assert.Contains("create:new", changed.Calls);
        Assert.Equal([fresh], changed.Records);
    }

    [Theory]
    [InlineData(@"C:\ProgramData\Elsewhere")]                         // not named as PoolWatch names one
    [InlineData(@"C:\ProgramData\PoolWatch.untrusted-20261030T100000Z")]
    [InlineData(@"C:\Other\PoolWatch.safe-20261030T100000Z-aaaa")]      // that name, but not next to the usual folder
    [InlineData(@"C:\ProgramData\PoolWatch.safe-x\..\..\Windows")]
    [InlineData("PoolWatch.safe-relative")]
    public void ARecordThatIsNotAFolderOfItsOwnNameNextToTheUsualOneIsNeverUsed(string recorded)
    {
        var ops = new Unmovable(remadeEveryTime: false) { Recorded = recorded };
        ops.Beside[recorded] = Exact; // even one that reads as PoolWatch's
        var (used, notes) = PrepareFallback(Root, "why.", Owner, ops, () => Now);
        Assert.NotEqual(recorded, used);
        Assert.DoesNotContain("named before", Assert.Single(notes)); // and it is not named as a folder of PoolWatch's own
        Assert.DoesNotContain(ops.Calls, c => c == "inspect:" + Path.GetFileName(recorded));
        Assert.False(IsFallbackName(Root, recorded));
        Assert.True(IsFallbackName(Root, used));
    }

    [Fact]
    public void ANameThatCannotBeRecordedIsSaidAndPoolWatchStillRuns()
    {
        var ops = new Unmovable(remadeEveryTime: false) { RecordResult = false };
        var (used, notes) = PrepareFallback(Root, "why.", Owner, ops, () => Now);
        Assert.Matches(@"PoolWatch\.safe-", used);
        Assert.EndsWith(" PoolWatch could not record that folder's name, so a later start that still cannot use the usual folder makes another one, " +
                        $"and what PoolWatch keeps in {used} does not come back to the usual folder by itself: it stays there.", Assert.Single(notes));
    }

    [Fact]
    public void NoFolderOfItsOwnCanBeMadeEitherIsStillRefused()
    {
        var failing = new Unmovable(remadeEveryTime: false) { CreateResult = 5 };
        var ex = Assert.Throws<DataRootRefusedException>(() => new AppPaths(Root, null, secure: true, holdsInstance: true, rootOps: failing));
        Assert.Contains("could not move", ex.Message);
        Assert.Contains("could not make a folder of its own", ex.Message);
        Assert.Equal(3, failing.Calls.Count(c => c == "create:new"));
        // Made, but not as PoolWatch made it when looked at again: never used.
        var changed = new Unmovable(remadeEveryTime: false) { Created = Folder(User, false, Allow(User, FullControl)) };
        Assert.Throws<DataRootRefusedException>(() => PrepareFallback(Root, "why.", Owner, changed, () => Now));
    }

    [Fact]
    public void OnceTheUsualFolderIsFreeTheDataComesHome()
    {
        // The usual name is free again (the foreign folder was moved aside): the folder PoolWatch used meanwhile is renamed back.
        string away = Root + FallbackMark + "20261030T100000Z-aaaa";
        var ops = new FakeOps(Folder(User, false, Allow(User, FullControl)), new Facts(false), Exact) { Recorded = away };
        ops.Beside[away] = Exact;
        var notes = Prepare(Root, Owner, ops, () => Now);
        Assert.Equal(["inspect", "move", "inspect", "inspect:" + Path.GetFileName(away), "moveto:" + Path.GetFileName(away), "inspect"], ops.Calls);
        Assert.Contains(notes, n => n.Contains("back in " + Root) && n.Contains(away));
        Assert.Equal([null], ops.Records); // the record is cleared

        // It cannot be moved (something holds it open): PoolWatch goes on using it rather than start on an empty folder.
        var held = new FakeOps(new Facts(false)) { MoveResult = false, Recorded = away };
        held.Beside[away] = Exact;
        Assert.Throws<DataRootRefusedException>(() => Prepare(Root, Owner, held, () => Now));
        Assert.DoesNotContain("create", held.Calls);
        Assert.Empty(held.Records);
        Assert.Equal(away, PrepareFallback(Root, "why.", Owner, held, () => Now).Root);

        // A folder of that name that is someone else's never comes home, recorded or not.
        var foreign = new FakeOps(new Facts(false), Exact) { Recorded = away };
        foreign.Beside[away] = Folder(User, false, Allow(User, FullControl));
        Prepare(Root, Owner, foreign, () => Now);
        Assert.DoesNotContain(foreign.Calls, c => c.StartsWith("moveto", StringComparison.Ordinal));
        Assert.Contains("create", foreign.Calls);
        Assert.Equal([null], foreign.Records); // and its record, which names no folder of PoolWatch's own, is deleted

        // Nothing recorded: the usual folder is simply made, whatever is next to it.
        var none = new FakeOps(new Facts(false), Exact);
        none.Beside[away] = Exact;
        Prepare(Root, Owner, none, () => Now);
        Assert.Equal(["inspect", "create", "inspect"], none.Calls);
    }

    private const string Away = Root + FallbackMark + "20261030T100000Z-aaaa";
    private static readonly string AwayName = Path.GetFileName(Away);
    private const string Deleted = "Its record was deleted.", NotDeleted = "Its record could not be deleted now; a later start tries again.";
    private static string GoneNote(string record) => $"{Away}, the folder PoolWatch's record names in place of {Root}, is not there any more. {record}";
    private static string NotItsOwnNote(string record) =>
        $"PoolWatch's record of a folder used in place of {Root} names no folder of PoolWatch's own, or is not one PoolWatch can trust. {record}";
    private static string NotAdminOnlyNote(string why, string record) =>
        $"{Away}, the folder PoolWatch's record names in place of {Root}, is not one only administrators can change now ({why}), so " +
        $"PoolWatch does not use it any more; anything PoolWatch kept there stays there. {record}";
    private static readonly Facts EarlierRules = Folder(Administrators, true, Allow(Administrators, FullControl), Allow(LocalSystem, FullControl), Allow(Users, ReadExecute));

    [Fact]
    public void ARecordThatNamesNoFolderOfItsOwnIsDeletedAtAStartThatUsesTheUsualFolder()
    {
        // A record of a folder used in place of the usual one that names no folder of PoolWatch's own any more is deleted
        // once the usual folder is in use, so it is not left behind for good (after that folder was deleted by hand, say).
        var gone = new FakeOps(Exact) { Recorded = Away };
        gone.Beside[Away] = new Facts(false);
        Assert.Equal([GoneNote(Deleted)], Prepare(Root, Owner, gone, () => Now));
        Assert.Equal([null], gone.Records);

        var elsewhere = new FakeOps(Exact) { Recorded = @"C:\ProgramData\Elsewhere" }; // not a name of PoolWatch's own
        Assert.Equal([NotItsOwnNote(Deleted)], Prepare(Root, Owner, elsewhere, () => Now)); // and never named
        Assert.Equal([null], elsewhere.Records);
        Assert.Equal(["inspect"], elsewhere.Calls);                                         // nor looked at

        var untrusted = new FakeOps(Exact) { UntrustedRecord = true };                      // a key someone else could have written
        Assert.Equal([NotItsOwnNote(Deleted)], Prepare(Root, Owner, untrusted, () => Now));
        Assert.Equal([null], untrusted.Records);

        // A record that cannot be deleted now: the note says so, names a gone folder all the same, and the record stays.
        var goneStuck = new FakeOps(Exact) { Recorded = Away, RecordResult = false };
        goneStuck.Beside[Away] = new Facts(false);
        Assert.Equal([GoneNote(NotDeleted)], Prepare(Root, Owner, goneStuck, () => Now));
        Assert.Equal(Away, goneStuck.Recorded);
        var untrustedStuck = new FakeOps(Exact) { UntrustedRecord = true, RecordResult = false };
        Assert.Equal([NotItsOwnNote(NotDeleted)], Prepare(Root, Owner, untrustedStuck, () => Now));
        Assert.True(untrustedStuck.UntrustedRecord);

        // The usual folder kept after one try at its rules: the same, after its own note.
        var stubborn = new FakeOps(EarlierRules, EarlierRules) { Recorded = Away };
        stubborn.Beside[Away] = new Facts(false);
        var notes = Prepare(Root, Owner, stubborn, () => Now);
        Assert.Equal(3, notes.Count);
        Assert.Contains("still differ in detail", notes[1]);
        Assert.Equal(GoneNote(Deleted), notes[2]);
        Assert.Equal([null], stubborn.Records);

        // No record: only the usual folder is looked at, and nothing is written.
        var none = new FakeOps(Exact);
        Prepare(Root, Owner, none, () => Now);
        Assert.Equal(["inspect"], none.Calls);
        Assert.Empty(none.Records);
    }

    private static string BackOver(string aside) =>
        $"PoolWatch's data is back in {Root} (it was kept in {Away} while PoolWatch could not use {Root}); the folder that was at {Root} " +
        $"was moved aside to {aside}, and anything in it stays there.";

    [Fact]
    public void ARecordedFolderOfItsOwnComesBackOnceTheUsualFolderIsSafeWhereItStands()
    {
        // PoolWatch ran on a folder of its own while the usual one was someone else's. An administrator then made the usual
        // folder safe where it stands (as the note invites): its rules are applied once, and the folder that holds
        // PoolWatch's data comes back to the usual name. The usual folder, with what was in it, is moved aside and kept.
        var taken = new FakeOps(EarlierRules, Exact, Exact) { Recorded = Away };
        taken.Beside[Away] = Exact;
        var notes = Prepare(Root, Owner, taken, () => Now);
        Assert.Equal(["inspect", "apply", "inspect", "inspect:" + AwayName, "move", "moveto:" + AwayName, "inspect"], taken.Calls);
        Assert.Equal([null], taken.Records); // the record is deleted once the data is back
        Assert.Equal(2, notes.Count);
        Assert.Equal(BackOver(Root + ".untrusted-x"), notes[1]);
        // A later start: no record, nothing more to do.
        var later = new FakeOps(Exact);
        Assert.Empty(Prepare(Root, Owner, later, () => Now));
        Assert.Equal(["inspect"], later.Calls);

        // Already exact where it stands, or kept after one try at its rules: the same, and no "kept" note for a folder that
        // was moved aside.
        foreach (var usual in new[] { new FakeOps(Exact, Exact) { Recorded = Away }, new FakeOps(EarlierRules, EarlierRules, Exact) { Recorded = Away } })
        {
            usual.Beside[Away] = Exact;
            var said = Prepare(Root, Owner, usual, () => Now);
            Assert.Contains("moveto:" + AwayName, usual.Calls);
            Assert.Equal(BackOver(Root + ".untrusted-x"), said[^1]);
            Assert.DoesNotContain(said, n => n.Contains("still differ"));
            Assert.Equal([null], usual.Records);
        }
        // The folder that came back is judged like any root: its own rules are applied once too, whatever was applied to
        // the usual folder before it was moved aside.
        var differs = new FakeOps(EarlierRules, EarlierRules, EarlierRules, Exact) { Recorded = Away };
        differs.Beside[Away] = Exact;
        var differsNotes = Prepare(Root, Owner, differs, () => Now);
        Assert.Equal(["inspect", "apply", "inspect", "inspect:" + AwayName, "move", "moveto:" + AwayName, "inspect", "apply", "inspect"], differs.Calls);
        Assert.DoesNotContain(differsNotes, n => n.Contains("still differ"));

        // The usual folder cannot be moved aside (something holds it open): PoolWatch uses the folder its data is in, as
        // when the usual folder cannot be made safe, and keeps its record; the usual folder's own data is not used.
        var held = new FakeOps(Exact) { Recorded = Away, MoveAsideFails = true };
        held.Beside[Away] = Exact;
        var refused = Assert.Throws<DataRootRefusedException>(() => Prepare(Root, Owner, held, () => Now));
        Assert.False(refused.NoFallback);
        Assert.Equal($"PoolWatch's data is in {Away}, and the folder at {Root} could not be moved aside to make room for it " +
                     $"(PoolWatch could not move {Root} aside (error 32)).", refused.Message);
        Assert.DoesNotContain(held.Calls, c => c.StartsWith("moveto", StringComparison.Ordinal));
        Assert.Empty(held.Records);
        Assert.Equal(Away, PrepareFallback(Root, refused.Message, Owner, held, () => Now).Root);

        // Moved aside, but the data cannot be moved back: the same, and the message names where the usual folder went.
        var stuck = new FakeOps(Exact) { Recorded = Away, MoveResult = false };
        stuck.Beside[Away] = Exact;
        var notBack = Assert.Throws<DataRootRefusedException>(() => Prepare(Root, Owner, stuck, () => Now));
        Assert.False(notBack.NoFallback);
        Assert.Equal($"PoolWatch's data is in {Away}, and it could not be moved back to {Root}; the folder that was at {Root} is now " +
                     $"{Root}.untrusted-x, and anything in it stays there.", notBack.Message);
        Assert.Empty(stuck.Records);
        Assert.Equal(Away, PrepareFallback(Root, notBack.Message, Owner, stuck, () => Now).Root);
    }

    private static string Refused(string why, string before = "") =>
        $"{before}PoolWatch's data is in {Away}, a folder of its own it used in place of {Root}, and PoolWatch cannot read that folder " +
        $"now ({why}). It uses no other data folder meanwhile, so that its data is not split between two folders: it starts as usual " +
        "once that folder can be read again, or once that folder is deleted.";

    [Theory]
    [InlineData(true)]  // the look itself fails (it throws)
    [InlineData(false)] // an administrator owns it and no one else may change it, but administrators may not open it
    public void WhileARecordedFolderOfItsOwnCannotBeReadPoolWatchUsesNoOtherDataFolder(bool lookThrows)
    {
        // That folder may hold PoolWatch's data. A usual folder made from nothing, or a new folder of its own, would split
        // that data between two folders once it can be read again, so PoolWatch keeps the record and starts on neither.
        string why = lookThrows ? "the look at it failed (the device is not ready)" : "its permissions do not let administrators open it (error 5)";
        FakeOps With(params Facts[] script)
        {
            var ops = new FakeOps(script) { Recorded = Away, Unreadable = lookThrows ? Away : null };
            if (!lookThrows) ops.Beside[Away] = Exact with { Unreadable = "error 5" };
            return ops;
        }
        // The usual name is free, the usual folder is in use, or it is kept after one try at its rules: refused, with nothing
        // made or moved and the record kept.
        foreach (var ops in new[] { With(new Facts(false), Exact), With(Exact), With(EarlierRules, EarlierRules) })
        {
            var ex = Assert.Throws<DataRootRefusedException>(() => Prepare(Root, Owner, ops, () => Now));
            Assert.True(ex.NoFallback);
            Assert.Equal(Refused(why), ex.Message);
            Assert.DoesNotContain("create", ops.Calls);
            Assert.DoesNotContain(ops.Calls, c => c.StartsWith("move", StringComparison.Ordinal));
            Assert.Empty(ops.Records);
        }
        // So the start makes no folder of its own in its place either, and nothing is written.
        var paths = With(new Facts(false));
        Assert.True(Assert.Throws<DataRootRefusedException>(() => new AppPaths(Root, null, secure: true, holdsInstance: true, rootOps: paths)).NoFallback);
        Assert.DoesNotContain(paths.Calls, c => c.StartsWith("create", StringComparison.Ordinal));
        Assert.Empty(paths.Records);
        // Nor does PrepareFallback, when the usual folder cannot be made safe.
        var held = new Unmovable(remadeEveryTime: false) { Recorded = Away, ReadableLooks = lookThrows ? 0 : null };
        held.Beside[Away] = lookThrows ? Exact : Exact with { Unreadable = "error 5" };
        var fallback = Assert.Throws<DataRootRefusedException>(() => PrepareFallback(Root, "why.", Owner, held, () => Now));
        Assert.Equal(Refused(why, "why. "), fallback.Message);
        Assert.DoesNotContain("create:new", held.Calls);
        Assert.Empty(held.Records);

        // A later start that can read it again: it comes back to the usual name, and nothing was made from nothing meanwhile.
        var next = new FakeOps(new Facts(false), Exact) { Recorded = Away };
        next.Beside[Away] = Exact;
        Assert.Equal([$"PoolWatch's data is back in {Root} (it was kept in {Away} while PoolWatch could not use {Root})"], Prepare(Root, Owner, next, () => Now));
        Assert.Equal(["inspect", "inspect:" + AwayName, "moveto:" + AwayName, "inspect"], next.Calls);
    }

    [Fact]
    public void ARefusalWhileARecordedFolderCannotBeReadIsNeverTurnedIntoAFolderOfItsOwn()
    {
        // AppPaths does not hand that refusal on to PrepareFallback: there, a look a moment later that finds the folder
        // gone would make a new folder of its own, and PoolWatch would start on it.
        var ops = new FakeOps(new Facts(false)) { Recorded = Away, BesideLater = new Facts(false) };
        ops.Beside[Away] = Exact with { Unreadable = "error 5" };
        var ex = Assert.Throws<DataRootRefusedException>(() => new AppPaths(Root, null, secure: true, holdsInstance: true, rootOps: ops));
        Assert.True(ex.NoFallback);
        Assert.Equal(Refused("its permissions do not let administrators open it (error 5)"), ex.Message);
        Assert.Equal(["inspect", "inspect:" + AwayName], ops.Calls); // one look at that folder, and nothing made
        Assert.Empty(ops.Records);
    }

    /// <summary>
    /// Owned by Administrators and changeable by no one else, but Administrators may only modify it (no WRITE_OWNER).
    /// </summary>
    private static readonly Facts Modify = Folder(Administrators, true, Allow(Administrators, 0x1301BF), Allow(LocalSystem, FullControl));
    /// <summary>
    /// Exactly PoolWatch's rules for the account this test runs as, which is what AppPaths reads for (Interop.CurrentUser).
    /// </summary>
    private static readonly Facts ExactHere = Folder(Administrators, true, [.. Expected(Interop.CurrentUser())]);
    private static string NotApplied(string root) =>
        $"{root}: its permissions are not PoolWatch's (Administrators and SYSTEM, read for your account); PoolWatch could not apply its own " +
        $"(PoolWatch could not open its data folder {root} to secure it (error 5)); it is used as it is (only administrators can change it)";

    [Fact]
    public void AnAdminOnlyUsualFolderWhoseRulesCannotBeAppliedIsUsedAsItIsAndNoFolderOfItsOwnIsMade()
    {
        // PoolWatch's own rules cannot be applied to it, but only administrators can change it: it is used as it is. A
        // folder of its own in its place would leave the data in it unused, and set it aside once the rules were back.
        Assert.Equal(Verdict.Repair, Decide(Modify, Mine).Verdict);
        var first = new FakeOps(Modify, Modify) { ApplyFails = true };
        var paths = new AppPaths(Root, null, secure: true, holdsInstance: true, rootOps: first);
        Assert.Equal(Root, paths.DataRoot);
        Assert.Null(paths.UsualRootNotUsed);
        Assert.Equal(["inspect", "apply", "inspect"], first.Calls); // nothing made, moved or recorded
        Assert.Empty(first.Records);
        Assert.Equal([NotApplied(Root)], paths.StartupNotes);
        // Once its rules are back, the same folder: nothing is moved.
        var next = new FakeOps(Exact);
        Assert.Empty(Prepare(Root, Owner, next, () => Now));
        Assert.Equal(["inspect"], next.Calls);
    }

    [Fact]
    public void AFolderBroughtHomeWhoseRulesCannotBeAppliedIsUsedAsItIsSoItsDataIsNeverSplit()
    {
        // The recorded folder of its own comes home; at the usual name its rules differ and cannot be applied. It is used
        // there, so no new folder of its own takes the data's place while the data stays in it.
        var ops = new FakeOps(ExactHere, EarlierRules, EarlierRules) { Recorded = Away, ApplyFails = true };
        ops.Beside[Away] = ExactHere;
        var paths = new AppPaths(Root, null, secure: true, holdsInstance: true, rootOps: ops);
        Assert.Equal(Root, paths.DataRoot);
        Assert.Null(paths.UsualRootNotUsed);
        Assert.Equal(["inspect", "inspect:" + AwayName, "move", "moveto:" + AwayName, "inspect", "apply", "inspect"], ops.Calls);
        Assert.Equal([null], ops.Records); // the record is cleared once, and no folder of its own is recorded
        Assert.Equal([BackOver(Root + ".untrusted-x"), NotApplied(Root)], paths.StartupNotes);

        // The usual folder's rules could not be applied, and then the recorded folder comes home in its place: no note says
        // that the folder now there is used as it is, since that one checked out.
        var after = new FakeOps(Modify, Modify, ExactHere) { Recorded = Away, ApplyFails = true };
        after.Beside[Away] = ExactHere;
        var afterPaths = new AppPaths(Root, null, secure: true, holdsInstance: true, rootOps: after);
        Assert.Equal(["inspect", "apply", "inspect", "inspect:" + AwayName, "move", "moveto:" + AwayName, "inspect"], after.Calls);
        Assert.Equal([BackOver(Root + ".untrusted-x")], afterPaths.StartupNotes);
    }

    private static string UsualUnread(string why) =>
        $"PoolWatch cannot read its data folder {Root} now ({why}). It neither moves that folder aside nor uses another data folder meanwhile, " +
        "so the data in it is neither set aside nor split: it starts as usual once that folder can be read again.";

    private static Microsoft.Win32.SafeHandles.SafeFileHandle Invalid() => new(new IntPtr(-1), ownsHandle: false);

    [Fact]
    public void AUsualFolderWhoseLookFailsIsNeverMovedAsideOrGivenUpForAnother()
    {
        // An earlier version's data folder, admin-only, whose open fails for a reason other than being denied (error 21, or
        // 1450 when the system runs short of resources): nothing is known about it. It is looked at again, and when every
        // look fails the start stops. It is never moved aside (its data would be set aside), and no folder of its own is made
        // in its place (a later start would bring that one home over the data).
        var failed = new Facts(true, IsDirectory: true, Unreadable: "error 21", LookFailed: true);
        var pauses = new List<TimeSpan>();
        var ops = new FakeOps(failed); // every look fails
        var refused = Assert.Throws<DataRootRefusedException>(() => Prepare(Root, Owner, ops, () => Now, pauses.Add));
        Assert.True(refused.NoFallback);
        Assert.Equal(UsualUnread("the look at it failed (error 21)"), refused.Message);
        Assert.Equal(Enumerable.Repeat("inspect", Looks), ops.Calls);
        Assert.Equal(Enumerable.Repeat(LookPause, Looks - 1), pauses);
        // Three looks, a quarter of a second apart, as SECURITY.md says.
        Assert.Equal(3, Looks);
        Assert.Equal(TimeSpan.FromMilliseconds(250), LookPause);
        // Through the start, held open (the move would fail) or not: the same, and no folder of its own either.
        foreach (bool moveFails in new[] { false, true })
        {
            var held = new FakeOps(failed) { MoveAsideFails = moveFails };
            var start = Assert.Throws<DataRootRefusedException>(() => new AppPaths(Root, null, secure: true, holdsInstance: true, rootOps: held));
            Assert.True(start.NoFallback);
            Assert.Equal(Enumerable.Repeat("inspect", Looks), held.Calls);
            Assert.Empty(held.Records);
        }
        // So the next start, which can read it again, finds the data where it was: nothing to bring home, nothing moved.
        var next = new FakeOps(Exact);
        Assert.Empty(Prepare(Root, Owner, next, () => Now));
        Assert.Equal(["inspect"], next.Calls);
        // A look that fails once and then reads: used, with nothing moved.
        var once = new FakeOps(failed, Exact);
        Assert.Empty(Prepare(Root, Owner, once, () => Now, _ => { }));
        Assert.Equal(["inspect", "inspect"], once.Calls);
        // A look that throws every time is one that fails.
        var throws = new FakeOps(Exact) { Unreadable = Root };
        Assert.Equal(UsualUnread("the look at it failed (the device is not ready)"),
            Assert.Throws<DataRootRefusedException>(() => Prepare(Root, Owner, throws, () => Now, _ => { })).Message);
        // The real look, given an open that fails so, on a folder this test made: the same.
        string folder = Path.Combine(_base, "short-of-resources");
        Directory.CreateDirectory(folder);
        var real = WindowsRootOps.Inspect(folder, _ => (Invalid(), 1450, false));
        Assert.Equal("the look at it failed (error 1450)", CannotReadNow(real, Mine));
        var withReal = new FakeOps(real);
        Assert.True(Assert.Throws<DataRootRefusedException>(() => Prepare(Root, Owner, withReal, () => Now, _ => { })).NoFallback);
        Assert.DoesNotContain(withReal.Calls, c => c != "inspect");
    }

    [Fact]
    public void AnAdminOnlyUsualFolderThatShutsAdministratorsOutIsNeverMovedAsideOrGivenUpForAnother()
    {
        // Owned by Administrators and changeable by no one else as the backup privilege reads it, but its rules do not let
        // administrators open it: only an administrator could have made it so, and it may hold PoolWatch's data. The start
        // stops, naming it, as for a recorded folder in that state: it is not moved aside, and no folder of its own is made
        // in its place, whether the folder could be moved or not.
        var shut = Exact with { Unreadable = "error 5" };
        Assert.Equal("its permissions do not let administrators open it (error 5)", CannotReadNow(shut, Mine));
        foreach (bool moveFails in new[] { false, true })
        {
            var ops = new FakeOps(shut, new Facts(false), Exact) { MoveAsideFails = moveFails };
            var refused = Assert.Throws<DataRootRefusedException>(() => new AppPaths(Root, null, secure: true, holdsInstance: true, rootOps: ops));
            Assert.True(refused.NoFallback);
            Assert.Equal(UsualUnread("its permissions do not let administrators open it (error 5)"), refused.Message);
            Assert.Equal(["inspect"], ops.Calls);
            Assert.Empty(ops.Records);
        }
        // One the backup privilege reads as someone else's, or whose owner not even an open with that privilege can read (a
        // delete pending, say), is still moved aside.
        foreach (var notAdminOnly in new[] { Folder(User, false, Allow(User, FullControl)) with { Unreadable = "error 5" }, new Facts(true, Unreadable: "error 5") })
        {
            Assert.Null(CannotReadNow(notAdminOnly, Mine));
            var ops = new FakeOps(notAdminOnly, new Facts(false), Exact);
            Prepare(Root, Owner, ops, () => Now);
            Assert.Equal(["inspect", "move", "inspect", "create", "inspect"], ops.Calls);
        }
        // A link or a file is never such a folder, whatever its look found.
        Assert.Null(CannotReadNow(new Facts(true, IsReparsePoint: true), Mine));
        Assert.Null(CannotReadNow(new Facts(true, IsDirectory: false, Unreadable: "error 21", LookFailed: true), Mine));
        Assert.Null(CannotReadNow(Exact, Mine));
    }

    private const string NoPrivilege = "error 5, and the backup privilege could not be turned on to read it";

    [Fact]
    public void AFolderWhoseOpenIsDeniedWhileTheBackupPrivilegeCannotBeTurnedOnIsNeverMovedAsideOrGivenUpForAnother()
    {
        // The plain open is denied, and the backup privilege cannot be turned on for a second open (not held, or a step that
        // turns it on fails): the real OpenToRead hands back the first open's error, not privileged, and the real look says
        // that it failed. Nothing is known about whose folder it is, so it is never moved aside and no folder of its own is
        // made or recorded in its place: the start stops, naming it, and the next start that can read it finds the data there.
        string folder = Path.Combine(_base, "no-privilege");
        Directory.CreateDirectory(folder);
        var raised = new List<string[]>();
        (Microsoft.Win32.SafeHandles.SafeFileHandle, int, bool) Open(string path) =>
            NoFollow.OpenToRead(path, _ => (Invalid(), NoFollow.ERROR_ACCESS_DENIED), names => { raised.Add(names); return null; });
        var facts = WindowsRootOps.Inspect(folder, Open);
        Assert.Equal([NoFollow.ReadPrivileges], raised); // it was asked for, and could not be turned on
        Assert.Equal(new Facts(true, IsDirectory: true, Unreadable: NoPrivilege, LookFailed: true), facts);
        Assert.Equal($"the look at it failed ({NoPrivilege})", CannotReadNow(facts, Mine));
        foreach (bool moveFails in new[] { false, true })
        {
            var ops = new FakeOps(facts) { MoveAsideFails = moveFails }; // every look fails so
            var refused = Assert.Throws<DataRootRefusedException>(() => new AppPaths(Root, null, secure: true, holdsInstance: true, rootOps: ops));
            Assert.True(refused.NoFallback);
            Assert.Equal(UsualUnread($"the look at it failed ({NoPrivilege})"), refused.Message);
            Assert.Equal(Enumerable.Repeat("inspect", Looks), ops.Calls); // looked at again, and nothing moved or made
            Assert.Empty(ops.Records);
        }
        // A recorded folder of its own in that state keeps its record, and the start uses no other data folder.
        var recorded = new FakeOps(Exact) { Recorded = Away };
        recorded.Beside[Away] = facts;
        Assert.Equal(Refused($"the look at it failed ({NoPrivilege})"), Assert.Throws<DataRootRefusedException>(() => Prepare(Root, Owner, recorded, () => Now)).Message);
        Assert.Empty(recorded.Records);
        var held = new Unmovable(remadeEveryTime: false) { Recorded = Away };
        held.Beside[Away] = facts;
        Assert.True(Assert.Throws<DataRootRefusedException>(() => PrepareFallback(Root, "why.", Owner, held, () => Now)).NoFallback);
        Assert.DoesNotContain("create:new", held.Calls);
        Assert.Empty(held.Records);
        // With the privilege turned on and its open denied too (a delete pending, say), the look did not fail: whose it is
        // cannot be read, and such a folder is moved aside (above).
        Assert.Equal(new Facts(true, IsDirectory: true, Unreadable: "error 5"), WindowsRootOps.Inspect(folder, _ => (Invalid(), 5, true)));
    }

    [Fact]
    public void AFolderWhoseRulesCannotBeAppliedIsUsedAsItIsOnlyWhileWhatPoolWatchMakesThereIsItsOwn()
    {
        // Not even PoolWatch's rules alone could be applied. The folder is used as it is only while no rule of it hands a
        // right to change what PoolWatch makes there to someone else, and administrators may make files there. Otherwise
        // the start stops, naming it, and still moves nothing and makes no folder of its own.
        var handsDown = Folder(Administrators, true, Allow(Administrators, FullControl), Allow(LocalSystem, FullControl), Allow(Users, 0x6, inheritOnly: true));
        Assert.Equal(Verdict.Repair, Decide(handsDown, Mine).Verdict);
        Assert.Equal(Users, HandsDown(handsDown));
        string notApplied = $"{Root}: its permissions are not PoolWatch's (Administrators and SYSTEM, read for your account); PoolWatch could not " +
                            $"apply its own (PoolWatch could not open its data folder {Root} to secure it (error 5)), and it cannot be used as it is: ";
        const string After = ". PoolWatch neither moves that folder aside nor uses another data folder meanwhile, so the data in it is neither " +
                             "set aside nor split: it starts as usual once that folder can be used (once an administrator gives it PoolWatch's " +
                             "permissions, say: Administrators and SYSTEM full control, read for your account).";
        var down = new FakeOps(handsDown, handsDown) { ApplyFails = true };
        var refused = Assert.Throws<DataRootRefusedException>(() => new AppPaths(Root, null, secure: true, holdsInstance: true, rootOps: down));
        Assert.True(refused.NoFallback);
        Assert.Equal(notApplied + $"its permissions would let {Users} change the files PoolWatch makes in it" + After, refused.Message);
        Assert.Equal(["inspect", "apply", "inspect"], down.Calls);
        Assert.Empty(down.Records);
        var readOnly = new FakeOps(Modify, Modify) { ApplyFails = true, MayWriteResult = false };
        var noWrite = Assert.Throws<DataRootRefusedException>(() => new AppPaths(Root, null, secure: true, holdsInstance: true, rootOps: readOnly));
        Assert.True(noWrite.NoFallback);
        Assert.Equal(notApplied + "administrators may not make files in it" + After, noWrite.Message);
        Assert.Equal(["inspect", "apply", "inspect"], readOnly.Calls);
        // Rules that were applied leave nothing to judge: a folder administrators may not write in gets PoolWatch's rules.
        var applied = new FakeOps(Modify, Exact) { MayWriteResult = false };
        Prepare(Root, Owner, applied, () => Now);
        Assert.Equal(["inspect", "apply", "inspect"], applied.Calls);
        // Rules that were applied and still differ in detail are judged on the same terms.
        var stillDiffers = new FakeOps(EarlierRules, EarlierRules) { MayWriteResult = false };
        var differs = Assert.Throws<DataRootRefusedException>(() => Prepare(Root, Owner, stillDiffers, () => Now));
        Assert.True(differs.NoFallback);
        Assert.Equal($"{Root}: its permissions still differ in detail after PoolWatch applied its own, and it cannot be used as it is: " +
                     "administrators may not make files in it" + After, differs.Message);
        Assert.Equal(["inspect", "apply", "inspect"], stillDiffers.Calls);
        Assert.Empty(stillDiffers.Records);
        var stillHandsDown = new FakeOps(handsDown, handsDown);
        Assert.Contains($"after PoolWatch applied its own, and it cannot be used as it is: its permissions would let {Users} change",
            Assert.Throws<DataRootRefusedException>(() => Prepare(Root, Owner, stillHandsDown, () => Now)).Message);
        // The recorded folder of its own, on the same terms: no new one is made in its place.
        var held = new Unmovable(remadeEveryTime: false) { Recorded = Away, ApplyFails = true };
        held.Beside[Away] = handsDown;
        var fallback = Assert.Throws<DataRootRefusedException>(() => PrepareFallback(Root, "why.", Owner, held, () => Now));
        Assert.True(fallback.NoFallback);
        Assert.Equal($"why. PoolWatch's data is in a folder of its own, {Away}: PoolWatch could not apply its own permissions (PoolWatch could not " +
                     $"secure its data folder {Away} (error 32)), and it cannot be used as it is: its permissions would let {Users} change the files " +
                     "PoolWatch makes in it" + After, fallback.Message);
        Assert.DoesNotContain("create:new", held.Calls);
        Assert.Empty(held.Records);
        // Its rules applied, and still not PoolWatch's: the same.
        var heldDiffers = new Unmovable(remadeEveryTime: false) { Recorded = Away };
        heldDiffers.Beside[Away] = handsDown;
        Assert.Equal($"why. PoolWatch's data is in a folder of its own, {Away}: its permissions are not PoolWatch's (Administrators and SYSTEM, read " +
                     $"for your account), and it cannot be used as it is: its permissions would let {Users} change the files PoolWatch makes in it" + After,
            Assert.Throws<DataRootRefusedException>(() => PrepareFallback(Root, "why.", Owner, heldDiffers, () => Now)).Message);
        Assert.Contains("apply:" + AwayName, heldDiffers.Calls);
        Assert.DoesNotContain("create:new", heldDiffers.Calls);
        Assert.Empty(heldDiffers.Records);
        // What does not count: CREATOR OWNER and OWNER RIGHTS (the owner of what is made there: an administrator), a rule that
        // only reads, a deny rule, and a trusted account. A kind of rule PoolWatch cannot read counts.
        Assert.Null(HandsDown(Folder(Administrators, true, Allow(CreatorOwner, FullControl, inheritOnly: true), Allow(OwnerRights, FullControl, inheritOnly: true),
            Allow(Users, ReadExecute, inheritOnly: true), new Ace(Users, false, FullControl, false, true, 3), Allow(LocalSystem, FullControl, inheritOnly: true))));
        Assert.Equal(UnknownKind, HandsDown(Folder(Administrators, true, new Ace(UnknownKind, true, -1, false, true, 3))));
    }

    [Fact]
    public void WhenItsOwnerCannotBeSetPoolWatchsRulesAloneAreApplied()
    {
        // The real steps' order, with an open of the test's own and steps that only record (no rule is written): the owner and
        // the rules first; when that open or that step fails, the rules alone, through a handle that asks only for them (an
        // owner may always change the rules of what it owns), so the rules the folder hands down are PoolWatch's own.
        string folder = Path.Combine(_base, "rules");
        Directory.CreateDirectory(folder);
        var asked = new List<uint>();
        var steps = new List<string>();
        Func<string, uint, (Microsoft.Win32.SafeHandles.SafeFileHandle, int)> Open(uint? denied) => (path, access) =>
        {
            asked.Add(access);
            return access == denied ? (Invalid(), 5) : (NoFollow.Open(path, dir: true, NoFollow.READ_CONTROL | NoFollow.FILE_READ_ATTRIBUTES, NoFollow.ShareAll), 0);
        };
        Func<Microsoft.Win32.SafeHandles.SafeFileHandle, int> Step(string name, int result) => _ => { steps.Add(name); return result; };
        // The owner and the rules work: nothing more.
        WindowsRootOps.ApplySecure(folder, Open(null), Step("all", 0), Step("rules", 0));
        Assert.Equal(["all"], steps);
        Assert.Equal([WindowsRootOps.OwnerAndRules], asked);
        // That open is denied (administrators may not change its owner): the rules alone.
        asked.Clear(); steps.Clear();
        WindowsRootOps.ApplySecure(folder, Open(WindowsRootOps.OwnerAndRules), Step("all", 0), Step("rules", 0));
        Assert.Equal(["rules"], steps);
        Assert.Equal([WindowsRootOps.OwnerAndRules, WindowsRootOps.RulesAlone], asked);
        Assert.Equal(0u, WindowsRootOps.RulesAlone & NoFollow.WRITE_OWNER);
        // That step fails (the owner cannot be set): the rules alone.
        asked.Clear(); steps.Clear();
        WindowsRootOps.ApplySecure(folder, Open(null), Step("all", 1307), Step("rules", 0));
        Assert.Equal(["all", "rules"], steps);
        // Neither works: refused, with both errors.
        steps.Clear();
        var ex = Assert.Throws<DataRootRefusedException>(() => WindowsRootOps.ApplySecure(folder, Open(null), Step("all", 1307), Step("rules", 5)));
        Assert.Equal($"PoolWatch could not secure its data folder {folder} (error 1307; its rules alone: error 5).", ex.Message);
        Assert.Equal($"PoolWatch could not open its data folder {folder} to secure it (error 5; for its rules alone: error 5).",
            Assert.Throws<DataRootRefusedException>(() => WindowsRootOps.ApplySecure(folder, (_, _) => (Invalid(), 5), Step("all", 0), Step("rules", 0))).Message);
        // A link there is never changed.
        string target = Path.Combine(_base, "rules-target");
        Directory.CreateDirectory(target);
        string link = Path.Combine(_base, "rules-link");
        Links.Junction(link, target);
        _links.Add(link);
        steps.Clear();
        Assert.Throws<DataRootRefusedException>(() => WindowsRootOps.ApplySecure(link, Open(null), Step("all", 0), Step("rules", 0)));
        Assert.Empty(steps);
    }

    /// <summary>The steps of a data folder that is safe as it is, which hold it open when asked (unless told to refuse).</summary>
    private sealed class HoldingOps : IRootOps
    {
        public readonly List<(string Folder, string Identity)> Holds = new();
        public bool Refuse;
        public Facts Inspect(string root) => ExactHere;
        public int CreateSecure(string root, DirectorySecurity security) => throw new InvalidOperationException("nothing to make");
        public void ApplySecure(string root, DirectorySecurity security) => throw new InvalidOperationException("nothing to apply");
        public string MoveAside(string root, DateTime utcNow) => throw new InvalidOperationException("nothing to move");
        public string? RecordedFallback() => null;
        public bool HasRecord() => false;
        public bool RecordFallback(string? path) => true;
        public bool MoveTo(string from, string to) => false;
        public string? IdentityOf(string folder) => "id of " + folder;
        public IDisposable? Hold(string folder, string identity)
        {
            Holds.Add((folder, identity));
            return Refuse ? null : new MemoryStream();
        }
    }

    /// <summary>
    /// The data folder a start checked is held open from then on (<see cref="IRootOps.Hold"/>, the folder of the identity
    /// read for it), so that no one can rename or delete it while that process runs and a write right after its check is a
    /// write in it. When it cannot be held (another program's open keeps that out), each check of a path in it tries again,
    /// and once held it is not asked again.
    /// </summary>
    [Fact]
    public void TheDataFolderAStartCheckedIsHeldOpenAndTriedAgainUntilItIs()
    {
        string root = Path.GetFullPath(Path.Combine(_base, "held"));
        Directory.CreateDirectory(root);
        var ops = new HoldingOps();
        var paths = new AppPaths(root, null, secure: true, holdsInstance: true, rootOps: ops, refuseVolume: _ => null);
        Assert.True(paths.Guard.DataRootHeld);
        Assert.Equal([(root, "id of " + root)], ops.Holds);
        Assert.Equal(Path.Combine(root, "x.json"), paths.Guard.CheckReal(Path.Combine(root, "x.json")));
        Assert.Single(ops.Holds);

        var refused = new HoldingOps { Refuse = true };
        var unheld = new AppPaths(root, null, secure: true, holdsInstance: true, rootOps: refused, refuseVolume: _ => null);
        Assert.False(unheld.Guard.DataRootHeld);
        unheld.Guard.CheckReal(Path.Combine(root, "x.json"));
        Assert.Equal(2, refused.Holds.Count);
        refused.Refuse = false;
        unheld.Guard.CheckReal(Path.Combine(root, "x.json"));
        Assert.True(unheld.Guard.DataRootHeld);
        unheld.Guard.CheckReal(Path.Combine(root, "x.json"));
        Assert.Equal(3, refused.Holds.Count);
    }

    /// <summary>
    /// The real hold (<see cref="NoFollow.HoldFolder"/>), on a TEMP folder: while it is open no one can rename or delete the
    /// folder, and writing, making and deleting in it go on as before. It holds only the folder of the identity it is
    /// given, never a file (given that folder's identity or the file's own) or a missing one; and while another program
    /// has the folder open in a way that keeps the hold out (sharing no reading), that open keeps the folder from being
    /// renamed too.
    /// </summary>
    [Fact]
    public void HoldingAFolderKeepsAnyoneFromRenamingOrDeletingItButNotFromWritingInIt()
    {
        string folder = Path.Combine(_base, "hold-me"), moved = Path.Combine(_base, "hold-me-moved");
        Directory.CreateDirectory(folder);
        string id = NoFollow.IdentityOf(folder)!;
        Assert.Equal(id, IdentityAsIfAFolder(folder));
        using (var hold = NoFollow.HoldFolder(folder, id))
        {
            Assert.NotNull(hold);
            File.WriteAllText(Path.Combine(folder, "x.txt"), "x");
            Directory.CreateDirectory(Path.Combine(folder, "sub"));
            File.Delete(Path.Combine(folder, "x.txt"));
            Directory.Delete(Path.Combine(folder, "sub"));
            Assert.Throws<IOException>(() => Directory.Move(folder, moved));
            Assert.ThrowsAny<IOException>(() => Directory.Delete(folder));
            Assert.Equal(id, NoFollow.IdentityOf(folder));
            Assert.False(Directory.Exists(moved));
        }
        Assert.Null(NoFollow.HoldFolder(folder, "another folder"));
        string file = Path.Combine(_base, "hold-file");
        File.WriteAllText(file, "x");
        Assert.Null(NoFollow.HoldFolder(file, id));
        Assert.Null(NoFollow.HoldFolder(file, IdentityAsIfAFolder(file)));
        Assert.Null(NoFollow.HoldFolder(Path.Combine(_base, "not-there"), id));
        using (var blocker = NoFollow.Open(folder, dir: true, NoFollow.FILE_LIST_DIRECTORY, 0))
        {
            Assert.False(blocker.IsInvalid);
            Assert.Null(NoFollow.HoldFolder(folder, id));
            Assert.Throws<IOException>(() => Directory.Move(folder, moved));
        }
        Directory.Move(folder, moved); // once nothing holds it, it moves
        Assert.True(Directory.Exists(moved));
    }

    /// <summary>
    /// The real steps' hold (<see cref="WindowsRootOps.Hold"/>), as the data folder's guard asks for it, through
    /// <see cref="IRootOps"/> (AppPaths passes it to PathGuard), on a TEMP folder: while it is open no one can rename or
    /// delete the folder, and writing and deleting in it go on; once it is let go the folder moves. It holds only the
    /// folder of the identity it is given: not that folder given another folder's identity, nor a file, given that
    /// folder's identity or the file's own (its volume and file ID, in the identity's format).
    /// </summary>
    [Fact]
    public void TheRealStepsHoldTheVeryFolderTheyAreGiven()
    {
        string folder = Path.Combine(_base, "real-hold"), moved = Path.Combine(_base, "real-hold-moved"), other = Path.Combine(_base, "real-hold-other");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(other);
        string id = NoFollow.IdentityOf(folder)!;
        Assert.Equal(id, IdentityAsIfAFolder(folder));
        IRootOps real = new WindowsRootOps();
        using (var hold = real.Hold(folder, id))
        {
            Assert.NotNull(hold);
            File.WriteAllText(Path.Combine(folder, "x.txt"), "x");
            File.Delete(Path.Combine(folder, "x.txt"));
            Assert.Throws<IOException>(() => Directory.Move(folder, moved));
            Assert.ThrowsAny<IOException>(() => Directory.Delete(folder));
            Assert.Equal(id, NoFollow.IdentityOf(folder));
        }
        Assert.Null(real.Hold(folder, "another folder"));
        Assert.Null(real.Hold(folder, NoFollow.IdentityOf(other)!));
        string file = Path.Combine(_base, "real-hold-file");
        File.WriteAllText(file, "x");
        Assert.Null(real.Hold(file, id));
        Assert.Null(real.Hold(file, IdentityAsIfAFolder(file)));
        Directory.Move(folder, moved); // let go, it moves
        Assert.True(Directory.Exists(moved));
        // AppPaths gives the data folder's guard this very step.
        string paths = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Engine", "AppPaths.cs"));
        Assert.Contains("var ops = _secured ? rootOps ?? new WindowsRootOps() : null;", paths);
        Assert.Contains("holdDataRoot: ops is null ? null : ops.Hold);", paths);
    }

    /// <summary>
    /// The identity the entry at <paramref name="path"/> would have as a folder: its volume and file ID in the format of
    /// <see cref="NoFollow.IdentityOf"/>, read through a handle on the entry itself, as the hold opens it, a file too.
    /// </summary>
    private static string IdentityAsIfAFolder(string path)
    {
        using var h = NoFollow.Open(path, dir: true, NoFollow.FILE_READ_ATTRIBUTES, NoFollow.ShareAll);
        Assert.False(h.IsInvalid);
        var id = NoFollow.Query(h).Id;
        return $"{id.Volume:X16}-{id.High:X16}{id.Low:X16}";
    }

    [Fact]
    public void WhetherAdministratorsMayMakeFilesInAFolderIsAskedOfTheFolderItself()
    {
        string folder = Path.Combine(_base, "writable");
        Directory.CreateDirectory(folder);
        uint askedFor = 0;
        Assert.True(WindowsRootOps.MayWrite(folder, (path, access) => { askedFor = access; return (NoFollow.Open(path, dir: true, access, NoFollow.ShareAll), 0); }));
        Assert.Equal(NoFollow.FILE_ADD_FILE | NoFollow.FILE_ADD_SUBDIRECTORY, askedFor);
        Assert.True(new WindowsRootOps().MayWrite(folder)); // the test's own folder
        Assert.False(WindowsRootOps.MayWrite(folder, (_, _) => (Invalid(), NoFollow.ERROR_ACCESS_DENIED)));
        Assert.True(WindowsRootOps.MayWrite(folder, (_, _) => (Invalid(), 32))); // a sharing violation says nothing about the rules
        // The real open asks the folder itself for those rights: while another handle on it shares no writing, an open that
        // asks to add files and folders is refused for sharing, and one that asks only to read its attributes is not.
        using (var noWriters = NoFollow.Open(folder, dir: true, 0x1 /* FILE_LIST_DIRECTORY */, NoFollow.FILE_SHARE_READ))
        {
            Assert.False(noWriters.IsInvalid);
            var (add, addError) = WindowsRootOps.OpenOnItself(folder, NoFollow.FILE_ADD_FILE | NoFollow.FILE_ADD_SUBDIRECTORY);
            using (add) Assert.Equal(32, addError); // ERROR_SHARING_VIOLATION
            var (read, readError) = WindowsRootOps.OpenOnItself(folder, NoFollow.FILE_READ_ATTRIBUTES);
            using (read) Assert.Equal(0, readError);
            Assert.True(new WindowsRootOps().MayWrite(folder)); // a sharing violation says nothing about the rules
        }
        // And the real MayWrite asks the real open for a right to add files or folders. A read-only file stands in for a
        // folder administrators may not make files in (no test writes a rule): FILE_ADD_FILE and FILE_ADD_SUBDIRECTORY are
        // the bits a file calls FILE_WRITE_DATA and FILE_APPEND_DATA, which Windows refuses (ERROR_ACCESS_DENIED) for a
        // read-only file, while it lets that file's attributes be read. It refuses both alike, so it cannot tell which of the
        // two the open asks for: the real MayWrite's binding to the open that asks for both, above, is held by its line.
        string guardSource = File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Engine", "DataRootGuard.cs"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(guardSource, @"(?m)^    public bool MayWrite\(string folder\) => MayWrite\(folder, OpenOnItself\);\r?$"));
        Assert.Contains("var (handle, error) = open(folder, NoFollow.FILE_ADD_FILE | NoFollow.FILE_ADD_SUBDIRECTORY);",
            ScriptCode.CSharpMember(guardSource, "internal static bool MayWrite(string folder, Func<string, uint, (SafeFileHandle Handle, int Error)> open)").Value);
        string readOnly = Path.Combine(_base, "read-only");
        File.WriteAllText(readOnly, "x");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        try
        {
            var (attributes, attributesError) = WindowsRootOps.OpenOnItself(readOnly, NoFollow.FILE_READ_ATTRIBUTES);
            using (attributes) Assert.Equal(0, attributesError);
            var (write, writeError) = WindowsRootOps.OpenOnItself(readOnly, NoFollow.FILE_ADD_FILE | NoFollow.FILE_ADD_SUBDIRECTORY);
            using (write) Assert.Equal(NoFollow.ERROR_ACCESS_DENIED, writeError);
            Assert.False(new WindowsRootOps().MayWrite(readOnly));
        }
        finally { File.SetAttributes(readOnly, FileAttributes.Normal); }
        // Which folder a name names: read through a handle, and another folder made at the same name later is another one.
        string? first = NoFollow.IdentityOf(folder);
        Assert.NotNull(first);
        Assert.Equal(first, NoFollow.IdentityOf(folder));
        Assert.Equal(first, new WindowsRootOps().IdentityOf(folder)); // the real steps read it so too
        Directory.Delete(folder);
        Assert.Null(NoFollow.IdentityOf(folder));
        Assert.Null(new WindowsRootOps().IdentityOf(folder));
        Directory.CreateDirectory(folder);
        Assert.NotEqual(first, NoFollow.IdentityOf(folder));
        Assert.NotEqual(first, new WindowsRootOps().IdentityOf(folder));
        File.WriteAllText(Path.Combine(_base, "a-file"), "x");
        Assert.Null(NoFollow.IdentityOf(Path.Combine(_base, "a-file")));
        string target = Path.Combine(_base, "id-target");
        Directory.CreateDirectory(target);
        string link = Path.Combine(_base, "id-link");
        Links.Junction(link, target);
        _links.Add(link);
        Assert.Null(NoFollow.IdentityOf(link)); // a link is never taken for its target
    }

    [Fact]
    public void EveryKindOfEntryInTheRulesIsJudged()
    {
        // Rules as read from a folder: Administrators full control, then the entry under test.
        static RawSecurityDescriptor With(GenericAce entry)
        {
            var acl = new RawAcl(GenericAcl.AclRevision, 2);
            acl.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, FullControl, new SecurityIdentifier(Administrators), false, null));
            acl.InsertAce(1, entry);
            return new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected,
                new SecurityIdentifier(Administrators), null, null, acl);
        }
        static ObjectAce Object(AceQualifier qualifier) =>
            new(AceFlags.None, qualifier, 0x2, new SecurityIdentifier(Users), ObjectAceFlags.None, Guid.Empty, Guid.Empty, false, null);
        // An object entry is read like any other: this one lets Users create files, so the folder is not admin-only.
        Assert.Equal(new Decision(Verdict.MoveAside, $"{Users} may change it (its own permission 0x2)"),
            Decide(Folder(Administrators, true, [.. AcesOf(With(Object(AceQualifier.AccessAllowed)))]), Mine));
        // A deny object entry only restricts.
        Assert.Equal(Verdict.Repair, Decide(Folder(Administrators, true, [.. AcesOf(With(Object(AceQualifier.AccessDenied)))]), Mine).Verdict);
        // A compound entry counts as one that allows its rights.
        Assert.Equal(new Decision(Verdict.MoveAside, $"{Users} may change it (its own permission 0x2)"),
            Decide(Folder(Administrators, true, [.. AcesOf(With(new CompoundAce(AceFlags.None, 0x2, CompoundAceType.Impersonation, new SecurityIdentifier(Users))))]), Mine));
        // A kind Windows does not define: an entry that may change anything, for an account that is never trusted.
        var custom = With(new CustomAce((AceType)0x40, AceFlags.None, new byte[4]));
        Assert.Equal(new Ace(UnknownKind, true, -1, false, false, 0), Assert.Single(AcesOf(custom), a => a.Sid == UnknownKind));
        Assert.Equal(Verdict.MoveAside, Decide(Folder(Administrators, true, [.. AcesOf(custom)]), Mine).Verdict);
    }

    [Fact]
    public void ANoteAboutAMoveSaysOnlyWhatHappened()
    {
        // The usual folder is moved aside, and the folder PoolWatch kept its data in comes home in its place: no folder is
        // made, and no note says one was.
        var ops = new FakeOps(Folder(User, false, Allow(User, FullControl)), new Facts(false), Exact) { Recorded = Away };
        ops.Beside[Away] = Exact;
        var notes = Prepare(Root, Owner, ops, () => Now);
        Assert.DoesNotContain("create", ops.Calls);
        Assert.Equal([MovedAside, $"PoolWatch's data is back in {Root} (it was kept in {Away} while PoolWatch could not use {Root})"], notes);
    }

    private static readonly string MovedAside = $"{Root} was not safe to use (it is owned by {User}, not Administrators or SYSTEM): it was moved " +
                                                $"aside to {Root}.untrusted-x. Anything PoolWatch had there is in {Root}.untrusted-x.";
    /// <summary>Lists of changes left in a folder only administrators could change (NamedLike: any other).</summary>
    private static string ListsLeft(string lists) =>
        $" Its lists of changes ({lists}) stay there too: PoolWatch does not read them there, so neither Undo in Active changes nor the " +
        "uninstaller puts back what only they list.";
    private static string NamedLike(string lists) =>
        $" Files in it named like PoolWatch's lists of changes ({lists}) stay there too: PoolWatch does not read them there, so neither Undo in " +
        "Active changes nor the uninstaller puts back what only they list.";

    [Fact]
    public void AFolderPoolWatchNoLongerUsesIsNamedWithTheListsOfChangesLeftInIt()
    {
        string aside = Root + ".untrusted-x";
        string[] both = ["applied-changes.json", "service-changes.json"];
        // The usual folder set aside when the folder PoolWatch kept its data in comes home: what it listed is not put back
        // from there, and the note says so.
        var home = new FakeOps(Exact, Exact) { Recorded = Away };
        home.Beside[Away] = Exact;
        home.Lists[aside] = both;
        Assert.Equal(BackOver(aside) + ListsLeft("applied-changes.json, service-changes.json"), Prepare(Root, Owner, home, () => Now)[^1]);
        // An unsafe folder moved aside: the same, said of files named like them (someone else could have written them).
        var foreign = new FakeOps(Folder(User, false, Allow(User, FullControl)), new Facts(false), Exact);
        foreign.Lists[aside] = both;
        Assert.Equal(MovedAside + NamedLike("applied-changes.json, service-changes.json"), Prepare(Root, Owner, foreign, () => Now)[0]);
        // A link moved aside is never looked into.
        var link = new FakeOps(new Facts(true, IsReparsePoint: true), new Facts(false), Exact);
        link.Lists[aside] = both;
        Assert.DoesNotContain("lists of changes", Prepare(Root, Owner, link, () => Now)[0]);
        // A recorded folder no longer used (not admin-only now), at a start on the usual folder and at one that makes another
        // folder of its own: the same.
        var notAdminOnly = Folder(User, false, Allow(User, FullControl));
        var used = new FakeOps(Exact) { Recorded = Away };
        used.Beside[Away] = notAdminOnly;
        used.Lists[Away] = ["applied-changes.json"];
        Assert.Equal([NotAdminOnlyNote($"it is owned by {User}, not Administrators or SYSTEM", Deleted)
                .Replace("stays there. ", "stays there." + NamedLike("applied-changes.json") + " ")], Prepare(Root, Owner, used, () => Now));
        var replaced = new FakeOps(Exact) { Recorded = Away };
        replaced.Beside[Away] = notAdminOnly;
        replaced.Lists[Away] = ["service-changes.json"];
        Assert.EndsWith($"could not be used again (it is owned by {User}, not Administrators or SYSTEM); anything PoolWatch kept there stays there." +
                        NamedLike("service-changes.json"), Assert.Single(PrepareFallback(Root, "why.", Owner, replaced, () => Now).Notes));
        var recordedLink = new FakeOps(Exact) { Recorded = Away };
        recordedLink.Beside[Away] = new Facts(true, IsReparsePoint: true);
        recordedLink.Lists[Away] = both;
        Assert.DoesNotContain("lists of changes", Assert.Single(Prepare(Root, Owner, recordedLink, () => Now)));

        // The real step: which of the two files are in a folder (a TEMP folder this test made).
        string folder = Path.Combine(_base, "listed");
        Directory.CreateDirectory(folder);
        IRootOps real = new WindowsRootOps();
        Assert.Empty(real.ChangeListsIn(folder));
        File.WriteAllText(Path.Combine(folder, "service-changes.json"), "[]");
        Assert.Equal(["service-changes.json"], real.ChangeListsIn(folder));
        File.WriteAllText(Path.Combine(folder, "applied-changes.json"), "[]");
        Assert.Equal(both, real.ChangeListsIn(folder));
        Assert.Equal(both, ChangeListNames); // the files Active changes and the uninstaller read in the data folder
    }

    [Fact]
    public void ARefusalSaysWhatTheSameStartDidBeforeIt()
    {
        // The usual folder is moved aside, then the folder PoolWatch keeps its data in cannot be read: the refusal says
        // where the usual folder went.
        var ops = new FakeOps(Folder(User, false, Allow(User, FullControl)), new Facts(false)) { Recorded = Away };
        ops.Beside[Away] = Exact with { Unreadable = "error 5" };
        var refused = Assert.Throws<DataRootRefusedException>(() => new AppPaths(Root, null, secure: true, holdsInstance: true, rootOps: ops));
        Assert.True(refused.NoFallback);
        Assert.Equal([MovedAside], refused.Notes);
        Assert.Equal(Refused("its permissions do not let administrators open it (error 5)") + " Before that, at this start: " + MovedAside, refused.Shown);
        // The data cannot be moved back: the folder it is in is used in place, and the start's notes say first where the
        // usual folder went.
        var stuck = new FakeOps(Folder(User, false, Allow(User, FullControl)), new Facts(false)) { Recorded = Away, MoveResult = false };
        stuck.Beside[Away] = Exact;
        var paths = new AppPaths(Root, null, secure: true, holdsInstance: true, rootOps: stuck);
        Assert.Equal(Away, paths.DataRoot);
        Assert.Equal(2, paths.StartupNotes.Count);
        Assert.Equal(MovedAside, paths.StartupNotes[0]);
        // And that folder cannot be read at the next look: refused, with the same said.
        var lost = new FakeOps(Folder(User, false, Allow(User, FullControl)), new Facts(false))
            { Recorded = Away, MoveResult = false, BesideLater = Exact with { Unreadable = "error 5" } };
        lost.Beside[Away] = Exact;
        var lostRefused = Assert.Throws<DataRootRefusedException>(() => new AppPaths(Root, null, secure: true, holdsInstance: true, rootOps: lost));
        Assert.True(lostRefused.NoFallback);
        Assert.Equal([MovedAside], lostRefused.Notes);
        // A refusal with nothing done before it is shown as it is.
        Assert.Equal("x.", new DataRootRefusedException("x.").Shown);
    }

    [Fact]
    public void AnOpenThatFailsForAReasonOtherThanBeingDeniedKeepsTheRecordAndStopsTheStart()
    {
        // Nothing is known about a recorded folder whose open fails with an I/O error (the device is not ready, say), as
        // when the look throws: its record is kept, and no other data folder is used.
        string folder = Path.Combine(_base, "not-ready");
        Directory.CreateDirectory(folder);
        static Microsoft.Win32.SafeHandles.SafeFileHandle Invalid() => new(new IntPtr(-1), ownsHandle: false);
        var facts = WindowsRootOps.Inspect(folder, _ => (Invalid(), 21, false));
        Assert.Equal(new Facts(true, IsDirectory: true, Unreadable: "error 21", LookFailed: true), facts);
        var used = new FakeOps(Exact) { Recorded = Away };
        used.Beside[Away] = facts;
        var refused = Assert.Throws<DataRootRefusedException>(() => Prepare(Root, Owner, used, () => Now));
        Assert.True(refused.NoFallback);
        Assert.Equal(Refused("the look at it failed (error 21)"), refused.Message);
        Assert.Empty(used.Records);
        var held = new Unmovable(remadeEveryTime: false) { Recorded = Away };
        held.Beside[Away] = facts;
        Assert.True(Assert.Throws<DataRootRefusedException>(() => PrepareFallback(Root, "why.", Owner, held, () => Now)).NoFallback);
        Assert.DoesNotContain("create:new", held.Calls);
        Assert.Empty(held.Records);
        // Denied, and the backup privilege not turned on for a second open: the look failed too.
        Assert.Equal(new Facts(true, IsDirectory: true, Unreadable: NoPrivilege, LookFailed: true), WindowsRootOps.Inspect(folder, _ => (Invalid(), 5, false)));
        // A file there is never a folder of PoolWatch's own, whatever its open does.
        string file = Path.Combine(_base, "a-file");
        File.WriteAllText(file, "x");
        var atFile = new FakeOps(Exact) { Recorded = Away };
        atFile.Beside[Away] = WindowsRootOps.Inspect(file, _ => (Invalid(), 21, false));
        Assert.Equal([NotAdminOnlyNote("it is a file, not a folder", Deleted)], Prepare(Root, Owner, atFile, () => Now));
        Assert.Equal([null], atFile.Records);
    }

    [Fact]
    public void AProcessThatDoesNotHoldTheOneInstanceMutexMovesMakesAndRecordsNoFolderOfItsOwn()
    {
        // The token-leak guard's helper, --mark, --changes-dump and the other steps may run while the tray app uses its data
        // folder. They use the folder in use as it stands: never a start's moves (a folder brought home or moved aside),
        // never a folder of their own, and never a change to the record.
        static AppPaths Step(FakeOps ops, bool running = false) =>
            new(Root, null, secure: true, rootOps: ops, holdsInstance: false, instanceRunning: () => running);
        // The data is in the recorded folder of its own, and the usual folder is safe where it stands: the recorded folder
        // is used, and nothing is moved.
        var away = new FakeOps(Exact) { Recorded = Away };
        away.Beside[Away] = Exact;
        var paths = Step(away);
        Assert.Equal(Away, paths.DataRoot);
        Assert.Equal(Root, paths.UsualRootNotUsed);
        Assert.Equal(["inspect:" + AwayName], away.Calls);
        Assert.Empty(away.Records);
        Assert.Equal([$"This PoolWatch run uses {Away}, the folder of its own PoolWatch keeps its data in while it cannot use {Root}. It moves no " +
                      "data folder: only the tray app, a headless run or the uninstaller's cleanup does that, at its start."], paths.StartupNotes);
        // The usual name is free: the same.
        var free = new FakeOps(new Facts(false)) { Recorded = Away };
        free.Beside[Away] = Exact;
        Assert.Equal(Away, Step(free).DataRoot);
        Assert.Equal(["inspect:" + AwayName], free.Calls);
        // No record: the usual folder as it stands, its rules not applied even where they differ in detail.
        var differs = new FakeOps(EarlierRules);
        var usual = Step(differs);
        Assert.Equal(Root, usual.DataRoot);
        Assert.Null(usual.UsualRootNotUsed);
        Assert.Equal(["inspect"], differs.Calls);
        Assert.Empty(usual.StartupNotes);
        // A record of a folder that is gone is left for a start that holds the mutex: not deleted here.
        var gone = new FakeOps(Exact) { Recorded = Away };
        gone.Beside[Away] = new Facts(false);
        Assert.Equal(Root, Step(gone).DataRoot);
        Assert.Empty(gone.Records);
        // Nothing at the usual name while no PoolWatch may hold the mutex (a CI step on a fresh PC, say): made, with
        // PoolWatch's rules at creation, and checked (nothing is moved).
        var none = new FakeOps(new Facts(false), Exact);
        Assert.Equal(Root, Step(none).DataRoot);
        Assert.Equal(["inspect", "create", "inspect"], none.Calls);
        // A usual folder that cannot be used as it stands, a recorded folder that cannot be read, or nothing at the usual name
        // while a PoolWatch may hold the mutex (the folder it uses is gone): refused, with nothing moved, made or recorded,
        // and no folder of its own either.
        var foreign = new FakeOps(Folder(User, false, Allow(User, FullControl)));
        var stillMissing = new FakeOps(new Facts(false), new Facts(false));
        var unreadable = new FakeOps(Exact) { Recorded = Away };
        unreadable.Beside[Away] = Exact with { Unreadable = "error 5" };
        var goneWhileRunning = new FakeOps(new Facts(false), Exact);
        var handsDownFacts = Folder(Administrators, true, [.. Mine, Allow(Users, 0x6, inheritOnly: true)]);
        var handsDown = new FakeOps(handsDownFacts);
        // Rules that are not PoolWatch's are used as they stand only while they hand nobody else a right to change what is
        // made there and administrators may make files there, so no write fails later for want of rights: for the usual
        // folder, and for the recorded folder of its own on the same terms.
        var noWrite = new FakeOps(EarlierRules) { MayWriteResult = false };
        var recordedHandsDown = new FakeOps(Exact) { Recorded = Away };
        recordedHandsDown.Beside[Away] = handsDownFacts;
        var recordedNoWrite = new FakeOps(Exact) { Recorded = Away, MayWriteResult = false };
        recordedNoWrite.Beside[Away] = EarlierRules;
        foreach (var (ops, running) in new[] { (foreign, false), (stillMissing, false), (unreadable, false), (goneWhileRunning, true), (handsDown, false),
                     (noWrite, false), (recordedHandsDown, false), (recordedNoWrite, false) })
        {
            var refused = Assert.Throws<DataRootRefusedException>(() => Step(ops, running));
            Assert.True(refused.NoFallback);
            Assert.DoesNotContain(ops.Calls, c => c.StartsWith("move", StringComparison.Ordinal) || c == "apply" || c == "create:safe");
            Assert.Empty(ops.Records);
        }
        Assert.Equal(["inspect"], foreign.Calls);
        Assert.Equal(["inspect"], goneWhileRunning.Calls);
        Assert.Equal(["inspect:" + AwayName], recordedHandsDown.Calls); // the usual folder is not used in its place either
        Assert.Equal(["inspect:" + AwayName], recordedNoWrite.Calls);
        string RecordedRefused(string why) =>
            $"PoolWatch cannot use {Away}, the folder of its own PoolWatch keeps its data in while it cannot use {Root}, as it stands ({why}), and " +
            "this PoolWatch run changes no data folder: only the tray app, a headless run or the uninstaller's cleanup does that, at its " +
            "start. Nothing was written. Exit PoolWatch if it runs and start it again (its start makes the data folder safe, keeps " +
            "the data in a folder of its own that this run then uses, or says why it cannot), then try again.";
        var downAgain = new FakeOps(Exact) { Recorded = Away };
        downAgain.Beside[Away] = handsDownFacts;
        Assert.Equal(RecordedRefused($"its permissions would let {Users} change the files PoolWatch makes in it"),
            Assert.Throws<DataRootRefusedException>(() => Step(downAgain)).Message);
        var noWriteAgain = new FakeOps(Exact) { Recorded = Away, MayWriteResult = false };
        noWriteAgain.Beside[Away] = EarlierRules;
        Assert.Equal(RecordedRefused("administrators may not make files in it"), Assert.Throws<DataRootRefusedException>(() => Step(noWriteAgain)).Message);
        Assert.Contains("as it stands (administrators may not make files in it)",
            Assert.Throws<DataRootRefusedException>(() => Step(new FakeOps(EarlierRules) { MayWriteResult = false })).Message);
        // Rules that are exactly PoolWatch's need no such look.
        var exactHere = new FakeOps(ExactHere) { MayWriteResult = false };
        Assert.Equal(Root, Step(exactHere).DataRoot);
        Assert.Equal($"PoolWatch's data folder {Root} is not there, and a PoolWatch may be running (its one-instance lock exists): this " +
                     "PoolWatch run makes no data folder then. Nothing was written. Exit PoolWatch and start it again (its start makes the " +
                     "data folder), then try again.", Assert.Throws<DataRootRefusedException>(() => Step(new FakeOps(new Facts(false)), running: true)).Message);
        Assert.Equal($"PoolWatch cannot use its data folder {Root} as it stands (it is owned by {User}, not Administrators or SYSTEM), and this " +
                     "PoolWatch run changes no data folder: only the tray app, a headless run or the uninstaller's cleanup does that, at its " +
                     "start. Nothing was written. Exit PoolWatch if it runs and start it again (its start makes the data folder safe, keeps " +
                     "the data in a folder of its own that this run then uses, or says why it cannot), then try again.",
            Assert.Throws<DataRootRefusedException>(() => Step(new FakeOps(Folder(User, false, Allow(User, FullControl))))).Message);
        Assert.Contains($"as it stands (its permissions would let {Users} change the files PoolWatch makes in it)",
            Assert.Throws<DataRootRefusedException>(() => Step(new FakeOps(Folder(Administrators, true, [.. Mine, Allow(Users, 0x6, inheritOnly: true)])))).Message);
        // The public constructor, which every PoolWatch process uses, takes no word on whether it holds the mutex, only the
        // proof (none: it does not), and the internal one goes by that proof when it is not told otherwise (tests).
        var ctor = typeof(AppPaths).GetConstructors().Single();
        Assert.Equal([typeof(string), typeof(ExitSignal.Hold)], ctor.GetParameters().Select(p => p.ParameterType));
        Assert.Null(ctor.GetParameters()[1].DefaultValue);
        var inner = typeof(AppPaths).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Single();
        Assert.Null(inner.GetParameters().Single(p => p.Name == "holdsInstance").DefaultValue);
    }

    [Fact]
    public void OnlyAStartGivenAProofThatItsThreadHoldsTheOneInstanceMutexMayMoveDataFolders()
    {
        // Only the two steps that take the one-instance mutex make the proof (ExitSignal.Hold): AsTheOneInstance, for the tray
        // app and a headless run, and StopAndHold, for the uninstall cleanup. A start given it, on the thread that took the
        // mutex and while it holds, runs the start's checks (a foreign folder is moved aside). Without one, on another thread,
        // or once it is disposed, the folder is found as it stands (here: refused, nothing moved).
        string name = @"Local\PoolWatch-Test-Monitor-" + Guid.NewGuid().ToString("N");
        FakeOps Foreign() => new(Folder(User, false, Allow(User, FullControl)), new Facts(false), ExactHere);
        AppPaths Paths(FakeOps ops, ExitSignal.Hold? hold) => new(Root, null, secure: true, rootOps: ops, instanceRunning: () => false, hold: hold);
        var outside = Foreign();
        Assert.True(Assert.Throws<DataRootRefusedException>(() => Paths(outside, null)).NoFallback);
        Assert.Equal(["inspect"], outside.Calls);
        var inside = Foreign();
        var elsewhere = Foreign();
        ExitSignal.Hold? kept = null;
        bool heldInRun = false, heldElsewhere = true;
        Exception? elsewhereRefused = null;
        Assert.Equal(0, ExitSignal.AsTheOneInstance(name, () => 3, hold =>
        {
            kept = hold;
            heldInRun = hold.HeldHere;
            var other = new Thread(() =>
            {
                heldElsewhere = hold.HeldHere;
                try { Paths(elsewhere, hold); }
                catch (DataRootRefusedException e) { elsewhereRefused = e; }
            });
            other.Start();
            other.Join();
            Paths(inside, hold);
            return 0;
        }));
        Assert.True(heldInRun);
        Assert.False(heldElsewhere);
        Assert.NotNull(elsewhereRefused);
        Assert.Equal(["inspect"], elsewhere.Calls);
        Assert.Equal(["inspect", "move", "inspect", "create", "inspect"], inside.Calls);
        // Once the run is over, its proof holds no more.
        Assert.False(kept!.HeldHere);
        var after = Foreign();
        Assert.Throws<DataRootRefusedException>(() => Paths(after, kept));
        Assert.Equal(["inspect"], after.Calls);
        // Another PoolWatch holds the mutex: alreadyRunning runs, and no proof is made.
        using var holding = new ManualResetEventSlim();
        using var stop = new ManualResetEventSlim();
        var running = new Thread(() =>
        {
            using var mutex = new Mutex(true, name);
            holding.Set();
            stop.Wait(TimeSpan.FromSeconds(30));
            mutex.ReleaseMutex();
        }) { IsBackground = true };
        running.Start();
        Assert.True(holding.Wait(TimeSpan.FromSeconds(10)));
        bool ranWithProof = false;
        Assert.Equal(3, ExitSignal.AsTheOneInstance(name, () => 3, _ => { ranWithProof = true; return 0; }));
        Assert.False(ranWithProof);
        stop.Set();
        Assert.True(running.Join(TimeSpan.FromSeconds(10)));
        // The uninstall cleanup: StopAndHold's proof, until it is disposed.
        ExitSignal.Hold? cleanupHold;
        using (var held = ExitSignal.StopAndHold(@"Local\PoolWatch-Test-Exit-" + Guid.NewGuid().ToString("N"), name, TimeSpan.FromSeconds(10)))
        {
            Assert.NotNull(held);
            Assert.True(held.HeldHere);
            var cleanup = Foreign();
            Paths(cleanup, held);
            Assert.Contains("move", cleanup.Calls);
            cleanupHold = held;
        }
        Assert.False(cleanupHold.HeldHere);
        // No other code can make a proof: its one constructor is private.
        Assert.All(typeof(ExitSignal.Hold).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic), c => Assert.True(c.IsPrivate));
    }

    [Fact]
    public void TheTrayAppAHeadlessRunAndTheUninstallCleanupMakeTheirPathsWithTheirProof()
    {
        // The tray app and a headless run take the proof as a parameter, which Program.Main can only get from AsTheOneInstance;
        // the uninstall cleanup is handed the one StopAndHold returned (TheUninstallCleanupOnAFolderOfItsOwn... below). Each
        // makes its paths with it, and with nothing in its place. No test can run the tray app, a headless run or the real
        // StopAndHold (it would ask a PoolWatch running on this PC to exit), so their source is read: these are the lines that
        // make them the one PoolWatch that may move data folders, the mutex they take included. A proof of a hold on any other
        // mutex would let a second PoolWatch move the folder a running one uses.
        Assert.Equal([typeof(Headless.CommandLineOptions), typeof(ExitSignal.Hold)], typeof(Ui.UiRunner).GetMethod("Run")!.GetParameters().Select(p => p.ParameterType));
        Assert.Equal([typeof(Headless.CommandLineOptions), typeof(ExitSignal.Hold)], typeof(Headless.HeadlessRunner).GetMethod("Run")!.GetParameters().Select(p => p.ParameterType));
        string src = Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch");
        static string[] Made(string source) =>
            System.Text.RegularExpressions.Regex.Matches(source, @"new (?:Engine\.)?AppPaths\([^;]*?\)").Select(m => m.Value).ToArray();
        Assert.Equal(["new AppPaths(opt.OutDir, hold)"], Made(File.ReadAllText(Path.Combine(src, "Ui", "UiRunner.cs"))));
        Assert.Equal(["new AppPaths(opt.OutDir, hold)"], Made(File.ReadAllText(Path.Combine(src, "Headless", "HeadlessRunner.cs"))));
        string program = File.ReadAllText(Path.Combine(src, "Program.cs"));
        string flat = System.Text.RegularExpressions.Regex.Replace(program, @"\s+", " ");
        Assert.Contains("hold => opt.Headless ? HeadlessRunner.Run(opt, hold) : Ui.UiRunner.Run(opt, hold),", program);
        Assert.Contains("() => Engine.ExitSignal.StopAndHold(StopWait), held => new Engine.AppPaths(opt.OutDir, held),", program);
        // The one-instance mutex: the one Program.Main takes, at its only call (RunAsTheOnlyMonitor, given the name in
        // PoolWatch's private namespace), and the one the cleanup's StopAndHold takes, the same namespace's.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(program, @"AsTheOneInstance\("));
        Assert.Contains("return Engine.ExitSignal.AsTheOneInstance(mutexName(), () =>", program);
        Assert.Contains("return RunAsTheOnlyMonitor(MonitorMutexName, hold =>", flat);
        Assert.Contains("private static string MonitorMutexName() { return Engine.AdminNamespace.Shared.MonitorMutexName; }", flat);
        Assert.Contains("public static Hold? StopAndHold(TimeSpan wait) { var names = AdminNamespace.Shared; " +
                        "return StopAndHold(names.ExitEventName, names.MonitorMutexName, wait); }",
            System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(Path.Combine(src, "Engine", "ExitSignal.cs")), @"\s+", " "));
        // Every other step in Program makes its paths without a proof.
        Assert.Equal(["new Engine.AppPaths(opt.OutDir, held)"], Made(program).Where(m => m.Contains(',')));
        // The public constructor hands the proof on.
        Assert.Contains("public AppPaths(string? outDir, ExitSignal.Hold? hold = null) : this(DefaultDataRoot(), outDir, secure: OperatingSystem.IsWindows(), hold: hold) { }",
            File.ReadAllText(Path.Combine(src, "Engine", "AppPaths.cs")));
    }

    [Fact]
    public void WhetherAPoolWatchMayHoldTheOneInstanceMutexIsWhetherItExists()
    {
        // A mutex of the test's own, never the real one.
        string name = @"Local\PoolWatch-Test-Monitor-" + Guid.NewGuid().ToString("N");
        Assert.False(ExitSignal.InstanceRunning(name));
        using (new Mutex(false, name)) Assert.True(ExitSignal.InstanceRunning(name));
        Assert.False(ExitSignal.InstanceRunning(name));
        // One that exists but that this process may not open counts as existing: a mutex whose rules let no one open it
        // (protected, with no entries; its owner may only read and change its rules).
        var noOne = new MutexSecurity();
        noOne.SetSecurityDescriptorSddlForm("D:P");
        using (MutexAcl.Create(false, name, out bool created, noOne))
        {
            Assert.True(created);
            Assert.Throws<UnauthorizedAccessException>(() => Mutex.OpenExisting(name)); // the open is denied
            Assert.True(ExitSignal.InstanceRunning(name));
        }
        Assert.False(ExitSignal.InstanceRunning(name));
    }

    /// <summary>
    /// A data root that is a real TEMP folder (made when PoolWatch asks, without rules of its own), read as PoolWatch's.
    /// </summary>
    private sealed class KeptInTemp : IRootOps
    {
        public Facts Inspect(string path) => Directory.Exists(path) ? Exact : new Facts(false);
        public int CreateSecure(string path, DirectorySecurity security) { Directory.CreateDirectory(path); return 0; }
        public void ApplySecure(string path, DirectorySecurity security) { }
        public string MoveAside(string path, DateTime utcNow) => throw new InvalidOperationException("nothing is moved here");
        public string? RecordedFallback() => null;
        public bool HasRecord() => false;
        public bool RecordFallback(string? path) => throw new InvalidOperationException("nothing is recorded here");
        /// <summary>True: which folder it is cannot be read.</summary>
        public bool Unidentified;
        public string? IdentityOf(string path) => Unidentified ? null : NoFollow.IdentityOf(path); // the real one, read through a handle
        public bool MoveTo(string from, string to) => throw new InvalidOperationException("nothing is moved here");
    }

    [Fact]
    public void ADataRootThatIsGoneIsNeverMadeAgainByAWrite()
    {
        // Renamed or deleted while PoolWatch runs: no write makes it again with the permissions %ProgramData% hands down.
        string root = Path.Combine(_base, "PoolWatch");
        var paths = new AppPaths(root, null, secure: true, holdsInstance: true, rootOps: new KeptInTemp(), refuseVolume: NoVolumeRule);
        string deep = paths.EnsureDir(Path.Combine(root, "cache", "deep"));
        Assert.True(Directory.Exists(deep));
        Assert.Equal(root, paths.EnsureDir(root)); // the root itself is there: nothing to make
        Directory.Delete(root, recursive: true);
        Assert.Throws<UnauthorizedAccessException>(() => paths.EnsureDir(Path.Combine(root, "cache")));
        Assert.Throws<UnauthorizedAccessException>(() => paths.EnsureDir(root));
        Assert.Throws<UnauthorizedAccessException>(() => paths.Guard.CheckReal(Path.Combine(root, "applied-changes.json")));
        Assert.False(Directory.Exists(root));
        // A file in its place is not the data root either.
        File.WriteAllText(root, "x");
        Assert.Throws<UnauthorizedAccessException>(() => paths.Guard.CheckReal(Path.Combine(root, "applied-changes.json")));
        File.Delete(root);
        // Nor is a folder made at that name afterwards, by anyone: it is not the folder this PoolWatch's start checked.
        Directory.CreateDirectory(root);
        var notTheOne = Assert.Throws<UnauthorizedAccessException>(() => paths.Guard.CheckReal(Path.Combine(root, "applied-changes.json")));
        Assert.Contains("not the data folder this PoolWatch checked at its start", notTheOne.Message);
        Assert.Throws<UnauthorizedAccessException>(() => paths.EnsureDir(Path.Combine(root, "cache")));
        Assert.False(Directory.Exists(Path.Combine(root, "cache")));
        // A start made now pins that one, and writes there.
        var again = new AppPaths(root, null, secure: true, holdsInstance: true, rootOps: new KeptInTemp(), refuseVolume: NoVolumeRule);
        Assert.Equal(Path.Combine(root, "x.json"), again.Guard.CheckReal(Path.Combine(root, "x.json")));
        // Which folder it is cannot be read once it is checked: the start stops, and writes nothing.
        var unread = Assert.Throws<DataRootRefusedException>(() =>
            new AppPaths(root, null, secure: true, holdsInstance: true, rootOps: new KeptInTemp { Unidentified = true }, refuseVolume: NoVolumeRule));
        Assert.True(unread.NoFallback);
        Assert.Equal($"PoolWatch could not read which folder its data folder {root} is, after checking it. Nothing was written.", unread.Message);
        Directory.Delete(root, recursive: true);
        // EnsureDir's own step makes each folder inside the one above it, never the data root.
        string missing = Path.Combine(_base, "missing");
        Assert.Throws<IOException>(() => AppPaths.MakeBelow(missing, Path.Combine(missing, "cache", "deep")));
        Assert.False(Directory.Exists(missing));
        Assert.Equal(NoFollow.ERROR_PATH_NOT_FOUND, NoFollow.CreateDirectoryOnly(Path.Combine(missing, "cache")));
        Assert.False(Directory.Exists(missing));
        Directory.CreateDirectory(missing);
        AppPaths.MakeBelow(missing, Path.Combine(missing, "cache", "deep"));
        Assert.True(Directory.Exists(Path.Combine(missing, "cache", "deep")));
        Assert.Equal(NoFollow.ERROR_ALREADY_EXISTS, NoFollow.CreateDirectoryOnly(Path.Combine(missing, "cache")));
        // Not secured (tests, the Linux build, an unelevated run): made as before.
        var plain = new AppPaths(Path.Combine(_base, "plain"), null, secure: false);
        Directory.Delete(plain.DataRoot);
        Assert.True(Directory.Exists(plain.EnsureDir(Path.Combine(plain.DataRoot, "cache"))));
    }

    [Fact]
    public void AProcessThatDoesNotHoldTheMutexWritesOnlyInTheFolderItChecked()
    {
        // The token-leak check's helper, say, still running after the tray app exited: a later start moves the folder it
        // checked away, or deletes it, and a folder is made at that name again. That process writes nothing there.
        string root = Path.Combine(_base, "PoolWatch");
        Directory.CreateDirectory(root);
        var helper = new AppPaths(root, null, secure: true, holdsInstance: false, instanceRunning: () => false, rootOps: new KeptInTemp(), refuseVolume: NoVolumeRule);
        Assert.Equal(root, helper.DataRoot);
        Assert.Equal(Path.Combine(root, "x.json"), helper.Guard.CheckReal(Path.Combine(root, "x.json")));
        Directory.Delete(root);
        Directory.CreateDirectory(root); // a later start made a folder there (or moved another one there)
        Assert.Throws<UnauthorizedAccessException>(() => helper.Guard.CheckReal(Path.Combine(root, "x.json")));
        Assert.Throws<UnauthorizedAccessException>(() => helper.EnsureDir(Path.Combine(root, "requests")));
        Assert.False(Directory.Exists(Path.Combine(root, "requests")));
    }

    [Fact]
    public void ALookThatFailsAfterAFolderCameHomeNeverSetsItsDataAside()
    {
        // The recorded folder of its own came home (its record is cleared), and every look at the usual name after that fails:
        // the folder now there holds the data, so it is not moved aside and no folder of its own is made in its place.
        var failed = new Facts(true, IsDirectory: true, Unreadable: "error 21", LookFailed: true);
        var ops = new FakeOps(ExactHere, failed) { Recorded = Away };
        ops.Beside[Away] = ExactHere;
        var refused = Assert.Throws<DataRootRefusedException>(() => new AppPaths(Root, null, secure: true, holdsInstance: true, rootOps: ops));
        Assert.True(refused.NoFallback);
        Assert.Equal(["inspect", "inspect:" + AwayName, "move", "moveto:" + AwayName, "inspect", "inspect", "inspect"], ops.Calls);
        Assert.Equal([null], ops.Records); // cleared once the data was back, and no folder of its own recorded
        Assert.StartsWith(UsualUnread("the look at it failed (error 21)"), refused.Shown);
        Assert.Equal([BackOver(Root + ".untrusted-x")], refused.Notes); // what this start did before is said
    }

    [Fact]
    public void ARecordedFolderOfItsOwnWhoseLookFailsIsLookedAtAgainAsTheUsualFolderIs()
    {
        // A look at the recorded folder of its own that fails (an I/O error, say) is made again, as one at the usual folder:
        // Looks times in all, LookPause apart. One that reads at a later look is used; one whose every look fails stops the
        // start, and its record is kept. So a failure that passes before the last of those looks does not stop the start.
        var failed = new Facts(true, IsDirectory: true, Unreadable: "error 21", LookFailed: true);
        FakeOps With(Facts?[] looks, params Facts[] usual)
        {
            var ops = new FakeOps(usual) { Recorded = Away };
            ops.Beside[Away] = Exact;
            ops.BesideAt.AddRange(looks);
            return ops;
        }
        Facts?[] failsBeforeTheLast = [.. Enumerable.Repeat<Facts?>(failed, Looks - 1)];
        var tries = Enumerable.Repeat("inspect:" + AwayName, Looks).ToArray();
        var pauses = new List<TimeSpan>();
        // The usual folder can be used: the recorded folder comes home once a look reads it.
        var home = With(failsBeforeTheLast, Exact, Exact);
        Assert.Equal([BackOver(Root + ".untrusted-x")], Prepare(Root, Owner, home, () => Now, pauses.Add));
        Assert.Equal(["inspect", .. tries, "move", "moveto:" + AwayName, "inspect"], home.Calls);
        Assert.Equal(Enumerable.Repeat(LookPause, Looks - 1), pauses);
        Assert.Equal([null], home.Records);
        // The usual name is free: the same.
        var free = With(failsBeforeTheLast, new Facts(false), Exact);
        Assert.Equal([$"PoolWatch's data is back in {Root} (it was kept in {Away} while PoolWatch could not use {Root})"],
            Prepare(Root, Owner, free, () => Now, _ => { }));
        Assert.Equal(["inspect", .. tries, "moveto:" + AwayName, "inspect"], free.Calls);
        // Every look fails: refused after Looks of them, with nothing moved or made, and the record kept.
        foreach (var usual in new[] { Exact, new Facts(false) })
        {
            var never = With([.. Enumerable.Repeat<Facts?>(failed, Looks)], usual);
            pauses.Clear();
            var refused = Assert.Throws<DataRootRefusedException>(() => Prepare(Root, Owner, never, () => Now, pauses.Add));
            Assert.True(refused.NoFallback);
            Assert.Equal(Refused("the look at it failed (error 21)"), refused.Message);
            Assert.Equal(["inspect", .. tries], never.Calls);
            Assert.Equal(Enumerable.Repeat(LookPause, Looks - 1), pauses);
            Assert.Empty(never.Records);
        }
        // PrepareFallback looks at it so too: at its first look, at the look that decides whether its rules are tried, and
        // at the look after they were.
        pauses.Clear();
        var first = With(failsBeforeTheLast);
        Assert.Equal(Away, PrepareFallback(Root, "why.", Owner, first, () => Now, pauses.Add).Root);
        Assert.Equal(Enumerable.Repeat(LookPause, Looks - 1), pauses);
        Assert.Empty(first.Records);
        pauses.Clear();
        // Read at its first look and at the look that decides whether its rules are tried (they are PoolWatch's, so they are
        // not), then the look after that fails twice before it reads.
        var afterRules = With([null, null, .. failsBeforeTheLast]);
        Assert.Equal(Away, PrepareFallback(Root, "why.", Owner, afterRules, () => Now, pauses.Add).Root);
        Assert.Equal(Enumerable.Repeat("inspect:" + AwayName, 2 + Looks), afterRules.Calls);
        Assert.Equal(Enumerable.Repeat(LookPause, Looks - 1), pauses);
        var beforeRules = With([null, .. failsBeforeTheLast]); // read, then fails twice before its rules (not PoolWatch's) are tried
        beforeRules.Beside[Away] = EarlierRules;
        Assert.Equal(Away, PrepareFallback(Root, "why.", Owner, beforeRules, () => Now, _ => { }).Root);
        Assert.Equal([.. Enumerable.Repeat("inspect:" + AwayName, 1 + Looks), "apply", "inspect:" + AwayName], beforeRules.Calls);
        var lost = With([.. Enumerable.Repeat<Facts?>(failed, Looks)]);
        Assert.Equal(Refused("the look at it failed (error 21)", "why. "),
            Assert.Throws<DataRootRefusedException>(() => PrepareFallback(Root, "why.", Owner, lost, () => Now, _ => { })).Message);
        Assert.DoesNotContain("create:safe", lost.Calls);
        // A process that does not hold the one-instance mutex: the recorded folder, and the usual folder, so too.
        pauses.Clear();
        var step = With(failsBeforeTheLast, Exact);
        Assert.Equal(Away, UseAsItIs(Root, Owner, step, () => false, pauses.Add).Root);
        Assert.Equal(tries, step.Calls);
        Assert.Equal(Enumerable.Repeat(LookPause, Looks - 1), pauses);
        pauses.Clear();
        var usualStep = new FakeOps(failed, failed, Exact);
        var (usedRoot, isUsual, _) = UseAsItIs(Root, Owner, usualStep, () => false, pauses.Add);
        Assert.Equal(Root, usedRoot);
        Assert.True(isUsual);
        Assert.Equal(Enumerable.Repeat("inspect", Looks), usualStep.Calls);
        Assert.Equal(Enumerable.Repeat(LookPause, Looks - 1), pauses);
        var usualNever = new FakeOps(failed);
        Assert.True(Assert.Throws<DataRootRefusedException>(() => UseAsItIs(Root, Owner, usualNever, () => false, _ => { })).NoFallback);
        Assert.Equal(Enumerable.Repeat("inspect", Looks), usualNever.Calls);
    }

    [Fact]
    public void WithoutAPauseOfTheirOwnTheLooksAreAtLeastAQuarterOfASecondApart()
    {
        // AppPaths passes no pause: Prepare, PrepareFallback and UseAsItIs then wait LookPause between two looks at a folder
        // whose look fails. Measured from below only, so a busy machine cannot make this fail.
        var failed = new Facts(true, IsDirectory: true, Unreadable: "error 21", LookFailed: true);
        var least = (Looks - 1) * LookPause - TimeSpan.FromMilliseconds(50); // a timer may wake a little early
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<DataRootRefusedException>(() => Prepare(Root, Owner, new FakeOps(failed), () => Now));
        Assert.True(clock.Elapsed >= least, $"Prepare took {clock.Elapsed}");
        var recorded = new FakeOps { Recorded = Away };
        recorded.Beside[Away] = failed;
        clock.Restart();
        Assert.Throws<DataRootRefusedException>(() => PrepareFallback(Root, "why.", Owner, recorded, () => Now));
        Assert.True(clock.Elapsed >= least, $"PrepareFallback took {clock.Elapsed}");
        clock.Restart();
        Assert.Throws<DataRootRefusedException>(() => UseAsItIs(Root, Owner, new FakeOps(failed), () => false));
        Assert.True(clock.Elapsed >= least, $"UseAsItIs took {clock.Elapsed}");
    }

    [Fact]
    public void ARecordedFolderThatIsNoLongerAdminOnlyIsNamedAndItsRecordDeleted()
    {
        // PoolWatch never uses that folder again, as it never uses the usual folder so: its record is deleted, and the note
        // names it, so anything PoolWatch kept there is not lost track of.
        var cases = new (Facts Facts, string Why)[]
        {
            (Folder(User, false, Allow(User, FullControl)), $"it is owned by {User}, not Administrators or SYSTEM"),
            (Folder(Administrators, true, [.. Mine, Allow(Users, 0x2)]), $"{Users} may change it (its own permission 0x2)"),
            (new Facts(true, IsReparsePoint: true), "it is a link (a junction, symbolic link or mount point), not a folder"),
            (new Facts(true, IsDirectory: false, Unreadable: "error 5"), "it is a file, not a folder"), // a file is never read as a folder of its own
            // Administrators may not open it, and the backup privilege read whose it is: another account's.
            (Folder(User, false, Allow(User, FullControl)) with { Unreadable = "error 5" }, $"it is owned by {User}, not Administrators or SYSTEM"),
            (Folder(Administrators, true, [.. Mine, Allow(Users, 0x2)]) with { Unreadable = "error 5" }, $"{Users} may change it (its own permission 0x2)"),
            // Not even the backup privilege could open it (a delete pending, say): whose it is cannot be told.
            (new Facts(true, Unreadable: "error 5"), "who owns it cannot be read (error 5)"),
        };
        foreach (var (facts, why) in cases)
        {
            var used = new FakeOps(Exact) { Recorded = Away };
            used.Beside[Away] = facts;
            Assert.Equal([NotAdminOnlyNote(why, Deleted)], Prepare(Root, Owner, used, () => Now));
            Assert.Equal([null], used.Records);
            // The usual name is free: that folder is not brought home, the usual folder is made, and the same is said.
            var free = new FakeOps(new Facts(false), Exact) { Recorded = Away };
            free.Beside[Away] = facts;
            Assert.Equal([NotAdminOnlyNote(why, Deleted)], Prepare(Root, Owner, free, () => Now));
            Assert.DoesNotContain(free.Calls, c => c.StartsWith("moveto", StringComparison.Ordinal));
            Assert.Contains("create", free.Calls);
            Assert.Equal([null], free.Records);
        }
        // A record that cannot be deleted now: said, and tried again at a later start.
        var stuck = new FakeOps(Exact) { Recorded = Away, RecordResult = false };
        stuck.Beside[Away] = cases[0].Facts;
        Assert.Equal([NotAdminOnlyNote(cases[0].Why, NotDeleted)], Prepare(Root, Owner, stuck, () => Now));
        Assert.Equal(Away, stuck.Recorded);
    }

    [Fact]
    public void AFolderAdministratorsMayNotOpenIsJudgedByWhoseItIsAsTheBackupPrivilegeReadsIt()
    {
        // The real inspection, given what its open hands back when the plain open was denied and the one with the backup
        // privilege worked: here a plain handle on a TEMP folder this test made stands in for it (no privilege is enabled,
        // and no rule is written). The facts are what the handle reads, marked as not readable as they are.
        string folder = Path.Combine(_base, "shut");
        Directory.CreateDirectory(folder);
        static (Microsoft.Win32.SafeHandles.SafeFileHandle, int, bool) WithThePrivilege(string path) =>
            (NoFollow.Open(path, dir: true, NoFollow.READ_CONTROL | NoFollow.FILE_READ_ATTRIBUTES, NoFollow.ShareAll), 0, true);
        var read = WindowsRootOps.Inspect(folder, WithThePrivilege);
        var plain = new WindowsRootOps().Inspect(folder);
        Assert.Null(plain.Unreadable);
        Assert.Equal("error 5", read.Unreadable);
        Assert.Equal(plain.OwnerSid, read.OwnerSid); // whose it is, as read
        Assert.Equal(plain.Aces!, read.Aces!);
        Assert.Equal(Verdict.MoveAside, Decide(read, Mine).Verdict); // never used as it is

        // At the recorded name, a folder that is not an administrator's alone (this test's TEMP folder: its owner, or the
        // test's own account through its inherited rules) is not one of PoolWatch's own any more: its record is deleted,
        // and the note names it and why, as read with the privilege.
        var own = Decide(read with { Unreadable = null }, Mine);
        Assert.Equal(Verdict.MoveAside, own.Verdict);
        var at = new FakeOps(Exact) { Recorded = Away };
        at.Beside[Away] = read;
        Assert.Equal([NotAdminOnlyNote(own.Reason, Deleted)], Prepare(Root, Owner, at, () => Now));
        Assert.Equal([null], at.Records);

        // Not even with the privilege (a delete pending, say): who owns it is not known.
        static Microsoft.Win32.SafeHandles.SafeFileHandle Invalid() => new(new IntPtr(-1), ownsHandle: false);
        Assert.Equal(new Facts(true, IsDirectory: true, Unreadable: "error 5"), WindowsRootOps.Inspect(folder, _ => (Invalid(), 5, true)));
        // Gone between the two looks.
        Assert.False(WindowsRootOps.Inspect(folder, _ => (Invalid(), 2, false)).Exists);
    }

    [Fact]
    public void ARecordThatCannotBeReadAtAllNeverStopsAStart()
    {
        // The real steps read a record they cannot read as none, and such a record is deleted as one PoolWatch cannot trust
        // (above). A step that throws all the same is only guarded: the record is left as it is, and the start goes on.
        var used = new FakeOps(Exact) { RecordThrows = true };
        Assert.Empty(Prepare(Root, Owner, used, () => Now));
        Assert.Empty(used.Records);
        var free = new FakeOps(new Facts(false), Exact) { RecordThrows = true };
        Assert.Empty(Prepare(Root, Owner, free, () => Now));
        Assert.Equal(["inspect", "create", "inspect"], free.Calls);
        Assert.Empty(free.Records);
        var held = new Unmovable(remadeEveryTime: false);
        var (fresh, notes) = PrepareFallback(Root, "why.", Owner, new ThrowingRecord(held), () => Now);
        Assert.Matches(@"PoolWatch\.safe-", fresh);
        Assert.DoesNotContain("named before", Assert.Single(notes));
    }

    /// <summary><see cref="Unmovable"/>, whose record cannot be read: reading it throws.</summary>
    private sealed class ThrowingRecord(Unmovable inner) : IRootOps
    {
        public Facts Inspect(string root) => inner.Inspect(root);
        public int CreateSecure(string root, DirectorySecurity security) => inner.CreateSecure(root, security);
        public void ApplySecure(string root, DirectorySecurity security) => inner.ApplySecure(root, security);
        public string MoveAside(string root, DateTime utcNow) => inner.MoveAside(root, utcNow);
        public string? RecordedFallback() => throw new IOException("the record cannot be read");
        public bool HasRecord() => throw new IOException("the record cannot be read");
        public bool RecordFallback(string? path) => inner.RecordFallback(path);
        public bool MoveTo(string from, string to) => inner.MoveTo(from, to);
    }

    [Fact]
    public void AFolderOfItsOwnWhoseRulesCannotBeAppliedAgainIsStillUsedAndOneThatCannotBeReadAgainStopsTheStart()
    {
        string mine = Away;
        // Owned by an administrator and changeable by no one else, but not with exactly PoolWatch's rules: they are applied
        // once, and when that fails the folder is still used, as Prepare uses the usual one, rather than PoolWatch exiting.
        var repair = new Unmovable(remadeEveryTime: false) { Recorded = mine, ApplyFails = true };
        repair.Beside[mine] = Folder(Administrators, false, [.. Mine]);
        var (used, notes) = PrepareFallback(Root, "why.", Owner, repair, () => Now);
        Assert.Equal(mine, used);
        Assert.Contains("apply:" + Path.GetFileName(mine), repair.Calls);
        Assert.DoesNotContain("create:new", repair.Calls);
        Assert.Contains(mine, Assert.Single(notes));

        // It cannot be read again once found: it may still hold PoolWatch's data, so no new folder is made in its place,
        // its record is kept, and the start is refused.
        var lost = new Unmovable(remadeEveryTime: false) { Recorded = mine, ReadableLooks = 1 };
        lost.Beside[mine] = Exact;
        var ex = Assert.Throws<DataRootRefusedException>(() => PrepareFallback(Root, "why.", Owner, lost, () => Now));
        Assert.True(ex.NoFallback);
        Assert.Equal(Refused("the look at it failed (the device is not ready)", "why. "), ex.Message);
        Assert.DoesNotContain("create:new", lost.Calls);
        Assert.Empty(lost.Records);
    }

    [Fact]
    public void EveryRecordedFolderOfItsOwnThatIsNotUsedIsNamedInTheNoteOfTheNewOne()
    {
        // The record can name only the folder in use, so a new one takes its place there; the note names the one it
        // replaces, with why, so anything PoolWatch kept there is not lost track of. Each case fails at the first look.
        string mine = Away;
        string Kept(string why) => $" The folder its record named before, {mine}, could not be used again ({why}); anything PoolWatch kept there stays there.";
        var cases = new (Facts Facts, string Said)[]
        {
            (new Facts(true, Unreadable: "error 5"), Kept("who owns it cannot be read (error 5)")),
            (Folder(User, false, Allow(User, FullControl)) with { Unreadable = "error 5" }, Kept($"it is owned by {User}, not Administrators or SYSTEM")),
            (Folder(User, false, Allow(User, FullControl)), Kept($"it is owned by {User}, not Administrators or SYSTEM")),
            (new Facts(true, IsReparsePoint: true), Kept("it is a link (a junction, symbolic link or mount point), not a folder")),
            (new Facts(false), $" The folder its record named before, {mine}, is not there any more."),
        };
        foreach (var (facts, said) in cases)
        {
            var ops = new Unmovable(remadeEveryTime: false) { Recorded = mine };
            ops.Beside[mine] = facts;
            var (used, notes) = PrepareFallback(Root, "why.", Owner, ops, () => Now);
            Assert.NotEqual(mine, used);
            Assert.Equal([used], ops.Records);
            string note = Assert.Single(notes);
            Assert.StartsWith($"why. PoolWatch keeps running: it keeps its data in {used}, a folder of its own that only administrators can " +
                              $"change, until a later start finds {Root} free or safe to use and moves that folder back there (settings, " +
                              $"records and reports in {Root} are not used meanwhile).", note);
            Assert.EndsWith(said, note);
        }
        // Usable at the first look, and gone or changed by the next: named the same way.
        var later = new (Facts Later, string Said)[]
        {
            (new Facts(false), $" The folder its record named before, {mine}, is not there any more."),
            (Folder(User, false, Allow(User, FullControl)), Kept($"it is owned by {User}, not Administrators or SYSTEM")),
        };
        foreach (var (then, said) in later)
        {
            var ops = new Unmovable(remadeEveryTime: false) { Recorded = mine, Later = then };
            ops.Beside[mine] = Exact;
            var (used, notes) = PrepareFallback(Root, "why.", Owner, ops, () => Now);
            Assert.NotEqual(mine, used);
            Assert.True(ops.Calls.Count(c => c == "inspect:" + Path.GetFileName(mine)) > 1);
            Assert.EndsWith(said, Assert.Single(notes));
        }
        // Usable at the first look, and not readable by administrators at the next: refused, as above.
        var shut = new Unmovable(remadeEveryTime: false) { Recorded = mine, Later = Exact with { Unreadable = "error 5" } };
        shut.Beside[mine] = Exact;
        Assert.True(Assert.Throws<DataRootRefusedException>(() => PrepareFallback(Root, "why.", Owner, shut, () => Now)).NoFallback);
        Assert.DoesNotContain("create:new", shut.Calls);
        // No record, or one of no folder of PoolWatch's own name: nothing more is said.
        foreach (string? recorded in new[] { null, @"C:\ProgramData\Elsewhere" })
        {
            var ops = new Unmovable(remadeEveryTime: false) { Recorded = recorded };
            Assert.DoesNotContain("named before", Assert.Single(PrepareFallback(Root, "why.", Owner, ops, () => Now).Notes));
        }
    }

    [Theory]
    [InlineData("O:BAG:SYD:PAI(A;CI;0xF003F;;;BA)(A;CI;0xF003F;;;SY)(A;CI;0x20019;;;BU)", true)]     // as HKLM\SOFTWARE gives it: administrators and SYSTEM, users read
    [InlineData("O:SYG:SYD:PAI(A;CI;0xF003F;;;BA)(A;CI;0xF003F;;;SY)(A;CIIO;0xF003F;;;BU)", true)]   // a rule only for keys made in it later
    [InlineData("O:BAG:SYD:PAI(A;CI;0xF003F;;;BA)(A;;0x2;;;BU)", false)]                              // users may set a value
    [InlineData("O:BAG:SYD:PAI(A;CI;0xF003F;;;BA)(A;;0x40000;;;S-1-5-21-1-2-3-1001)", false)]        // someone may change its rules
    [InlineData("O:S-1-5-21-1-2-3-1001G:SYD:PAI(A;CI;0xF003F;;;BA)", false)]                         // a standard user owns it
    [InlineData("O:BAG:SYD:NO_ACCESS_CONTROL", false)]                                                // no rules: anyone may do anything
    public void TheRecordIsReadOnlyFromAKeyOnlyAnAdministratorCouldHaveWritten(string sddl, bool read)
    {
        const string folder = @"C:\ProgramData\PoolWatch.safe-20261030T100000Z-aaaa";
        Assert.Equal(read ? folder : null, WindowsRootOps.RecordOf(new RawSecurityDescriptor(sddl), folder, ElevatedAccounts.None));
    }

    [Fact]
    public void ARecordThatIsNotTextOrWhoseRulesCannotBeReadNamesNoFolder()
    {
        var adminOnly = new RawSecurityDescriptor("O:BAG:SYD:PAI(A;CI;0xF003F;;;BA)(A;CI;0xF003F;;;SY)");
        Assert.Equal(@"C:\x", WindowsRootOps.RecordOf(adminOnly, @"C:\x", ElevatedAccounts.None));
        Assert.Null(WindowsRootOps.RecordOf(adminOnly, 42, ElevatedAccounts.None));
        Assert.Null(WindowsRootOps.RecordOf(adminOnly, new byte[] { 1 }, ElevatedAccounts.None));
        Assert.Null(WindowsRootOps.RecordOf(null, @"C:\x", ElevatedAccounts.None));
        // An owner every program of whom runs as administrator on this PC (UAC off, say) counts as an administrator here too.
        var owned = new RawSecurityDescriptor("O:S-1-5-21-1-2-3-1001G:SYD:PAI(A;CI;0xF003F;;;BA)");
        Assert.Equal(@"C:\x", WindowsRootOps.RecordOf(owned, @"C:\x", ElevatedAccounts.Only(new SecurityIdentifier("S-1-5-21-1-2-3-1001"))));
    }

    [Fact]
    public void TheUninstallCleanupTellsTheUninstallerWhenItRanOnAFolderOfItsOwn()
    {
        Assert.Equal(Engine.ExitCodes.OtherDataFolder, Program.CleanupExit(0, otherDataFolder: true));
        Assert.Equal(Engine.ExitCodes.OtherDataFolder, Program.CleanupExit(Engine.ExitCodes.NotAllPutBack, otherDataFolder: true));
        Assert.Equal(Engine.ExitCodes.StillRunning, Program.CleanupExit(Engine.ExitCodes.StillRunning, otherDataFolder: true));
        Assert.Equal(0, Program.CleanupExit(0, otherDataFolder: false));
        Assert.Equal(Engine.ExitCodes.NotAllPutBack, Program.CleanupExit(Engine.ExitCodes.NotAllPutBack, otherDataFolder: false));
    }

    /// <summary>
    /// A usual root (in TEMP) someone else owns and holds open, and a stand-in that is really made in TEMP when PoolWatch
    /// asks for one (without any access rules of its own: none is written), then reads as PoolWatch made it.
    /// </summary>
    private sealed class StandInInTemp(string root) : IRootOps
    {
        public Facts Inspect(string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ? Folder(User, false, Allow(User, FullControl))
            : Directory.Exists(path) ? Exact : new Facts(false);
        public int CreateSecure(string path, DirectorySecurity security)
        {
            if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) return 5;
            Directory.CreateDirectory(path);
            return 0;
        }
        public void ApplySecure(string path, DirectorySecurity security) { }
        public string MoveAside(string path, DateTime utcNow) => throw new DataRootRefusedException($"PoolWatch could not move {path} aside (error 32).");
        public string? Recorded;
        public string? RecordedFallback() => Recorded;
        public bool HasRecord() => Recorded is not null;
        public bool RecordFallback(string? path) { Recorded = path; return true; }
        public string? IdentityOf(string path) => NoFollow.IdentityOf(path);
        public bool MoveTo(string from, string to) => false;
    }

    [Fact]
    public void TheUninstallCleanupOnAFolderOfItsOwnExitsTwelveAndLeavesTheUsualOneAlone()
    {
        string root = Path.Combine(_base, "PoolWatch");
        AppPaths? paths = null;
        // The cleanup itself (the change steps are fakes: nothing on this PC is put back), its stop holding a mutex of the
        // test's own. Its paths are made only once the stop has worked, with the proof the stop returned: their checks then
        // run as the one PoolWatch running (here: a folder of its own is made, which only such a start does).
        string mutex = @"Local\PoolWatch-Test-Monitor-" + Guid.NewGuid().ToString("N"), exit = @"Local\PoolWatch-Test-Exit-" + Guid.NewGuid().ToString("N");
        var steps = new List<string>();
        ExitSignal.Hold? stopped = null;
        bool heldWhenMade = false;
        int code = Program.UninstallCleanup(false, () => { steps.Add("stopped"); return stopped = ExitSignal.StopAndHold(exit, mutex, TimeSpan.FromSeconds(10)); },
            held =>
            {
                steps.Add("paths");
                heldWhenMade = ReferenceEquals(held, stopped) && held.HeldHere;
                return paths = new AppPaths(root, null, secure: true, rootOps: new StandInInTemp(root), refuseVolume: NoVolumeRule, hold: held);
            },
            (_, _) => [], (_, _, _) => []);
        Assert.Equal(["stopped", "paths"], steps);
        Assert.True(heldWhenMade);
        Assert.False(stopped!.HeldHere); // released once the cleanup is done
        Assert.Equal(root, paths!.UsualRootNotUsed);
        Assert.Equal(Engine.ExitCodes.OtherDataFolder, code);
        Assert.True(File.Exists(Path.Combine(paths.DataRoot, Program.UninstallSummaryName)));
        Assert.False(Directory.Exists(root)); // nothing made or written at the usual name

        // On the usual folder, the same cleanup exits 0, and PoolWatch still running is 7 either way, with no paths made
        // (so no data folder is looked at, let alone moved, while it runs).
        var usual = new AppPaths(Path.Combine(_base, "usual"), null, secure: false);
        Assert.Equal(0, Program.UninstallCleanup(false, () => ExitSignal.StopAndHold(exit, mutex, TimeSpan.FromSeconds(10)), _ => usual, (_, _) => [], (_, _, _) => []));
        bool made = false;
        Assert.Equal(Engine.ExitCodes.StillRunning, Program.UninstallCleanup(false, () => null, _ => { made = true; return paths; }, (_, _) => [], (_, _, _) => []));
        Assert.False(made);
    }

    [Fact]
    public void TheUninstallCleanupAddsToItsSummaryOnlyWhenTheUninstallerAsks()
    {
        // The uninstaller's second cleanup of one uninstall passes --append-summary: that run's summary follows the first
        // one's in uninstall-summary.txt and says it is a second run. Any other run replaces the file. The stop holds a
        // mutex of the test's own; the change steps are fakes.
        string mutex = @"Local\PoolWatch-Test-Monitor-" + Guid.NewGuid().ToString("N"), exit = @"Local\PoolWatch-Test-Exit-" + Guid.NewGuid().ToString("N");
        var usual = new AppPaths(Path.Combine(_base, "usual"), null, secure: false);
        string file = Path.Combine(usual.DataRoot, Program.UninstallSummaryName);
        int Cleanup(bool append) => Program.UninstallCleanup(false, () => ExitSignal.StopAndHold(exit, mutex, TimeSpan.FromSeconds(10)), _ => usual,
            (_, _) => [], (_, _, _) => [], append: append);
        Assert.Equal(0, Cleanup(append: false));
        string first = File.ReadAllText(file);
        Assert.DoesNotContain("cleanup run again", first);
        Assert.Equal(0, Cleanup(append: true));
        string both = File.ReadAllText(file);
        Assert.StartsWith(first, both);
        Assert.StartsWith("PoolWatch uninstall, cleanup run again, ", both[first.Length..]);
        Assert.Equal(0, Cleanup(append: false));
        Assert.DoesNotContain("cleanup run again", File.ReadAllText(file));
        // The command line's --append-summary is what the real cleanup passes on (its stop asks a running PoolWatch to
        // exit, so its source is read).
        string program = System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "src", "PoolWatch", "Program.cs")), @"\s+", " ");
        Assert.Contains("EndLeftoverHelpers, message => TryOpenLog(opt)(message), opt.AppendSummary);", program);
    }

    [Fact]
    public void ASecondStartLooksAtNoDataFolderWhileAnotherPoolWatchHoldsTheOneInstanceMutex()
    {
        // A mutex of the test's own in the session's local namespace, never the real one a running PoolWatch holds. While
        // another PoolWatch (here: a thread) holds it, the start that would check the data folder never runs.
        string name = @"Local\PoolWatch-Test-Monitor-" + Guid.NewGuid().ToString("N");
        using var holding = new ManualResetEventSlim();
        using var stop = new ManualResetEventSlim();
        var running = new Thread(() =>
        {
            using var mutex = new Mutex(true, name);
            holding.Set();
            stop.Wait(TimeSpan.FromSeconds(30));
            mutex.ReleaseMutex();
        }) { IsBackground = true };
        running.Start();
        Assert.True(holding.Wait(TimeSpan.FromSeconds(10)));
        var steps = new List<string>();
        Assert.Equal(3, ExitSignal.AsTheOneInstance(name, () => { steps.Add("already running"); return 3; }, _ => { steps.Add("start"); return 0; }));
        Assert.Equal(["already running"], steps);
        stop.Set();
        Assert.True(running.Join(TimeSpan.FromSeconds(10)));
        // Once no PoolWatch holds it, the start runs, holding the mutex meanwhile.
        steps.Clear();
        bool heldMeanwhile = false;
        Assert.Equal(0, ExitSignal.AsTheOneInstance(name, () => { steps.Add("already running"); return 3; }, _ =>
        {
            steps.Add("start");
            var other = new Thread(() =>
            {
                using var probe = new Mutex(false, name);
                heldMeanwhile = !probe.WaitOne(0);
                if (!heldMeanwhile) probe.ReleaseMutex();
            });
            other.Start();
            other.Join();
            return 0;
        }));
        Assert.Equal(["start"], steps);
        Assert.True(heldMeanwhile);
    }

    /// <summary>A volume rule that refuses none: the test's TEMP may be on a Dev Drive (ReFS), which the real rule refuses.</summary>
    private static string? NoVolumeRule(string path) => null;

    [Fact]
    public void TheVolumeRuleASecuredRootIsGivenIsTheOneItsWritesAreCheckedAgainst()
    {
        // The real rule refuses ReFS; a test's own rule stands in for it, so this holds wherever TEMP is.
        string root = Path.Combine(_base, "PoolWatch");
        var refusing = new AppPaths(root, null, secure: true, holdsInstance: true, rootOps: new StandInInTemp(root), refuseVolume: _ => "a volume this test refuses");
        var ex = Assert.Throws<UnauthorizedAccessException>(() => refusing.Guard.CheckReal(Path.Combine(refusing.DataRoot, "poolwatch.log")));
        Assert.Contains("a volume this test refuses", ex.Message);
        var open = new AppPaths(root, null, secure: true, holdsInstance: true, rootOps: new StandInInTemp(root), refuseVolume: NoVolumeRule);
        Assert.Equal(Path.Combine(open.DataRoot, "poolwatch.log"), open.Guard.CheckReal(Path.Combine(open.DataRoot, "poolwatch.log")));
        // Without security (tests, the Linux build) no volume rule applies, whatever is passed.
        var plain = new AppPaths(Path.Combine(_base, "plain"), null, secure: false, refuseVolume: _ => "a volume this test refuses");
        Assert.Equal(Path.Combine(plain.DataRoot, "x.txt"), plain.Guard.CheckReal(Path.Combine(plain.DataRoot, "x.txt")));
    }

    [Fact]
    public void FoldersNamedLikePoolWatchsOwnAreNeverLookedAtHoweverMany()
    {
        // A standard user may make any number of folders in %ProgramData% named like PoolWatch's stand-in, named to sort
        // before it: PoolWatch judges only the one it recorded, so none of them costs a look, and its own is still the one
        // used, and the one that comes home.
        string mine = Root + FallbackMark + "20261030T100000Z-" + new string('a', 32);
        void Decoys(Dictionary<string, Facts> beside)
        {
            for (int i = 0; i < 300; i++) beside[Root + FallbackMark + $"zzzz{i:D4}-{new string('f', 32)}"] = Folder(User, false, Allow(User, FullControl));
            beside[mine] = Exact;
        }
        var held = new Unmovable(remadeEveryTime: false) { Recorded = mine };
        Decoys(held.Beside);
        Assert.Equal(mine, PrepareFallback(Root, "why.", Owner, held, () => Now).Root);
        Assert.DoesNotContain("create:new", held.Calls);
        Assert.Equal(0, held.Calls.Count(c => c.StartsWith("inspect:PoolWatch.safe-zzzz", StringComparison.Ordinal)));

        var free = new FakeOps(new Facts(false), Exact) { Recorded = mine };
        Decoys(free.Beside);
        Prepare(Root, Owner, free, () => Now);
        Assert.Contains("moveto:" + Path.GetFileName(mine), free.Calls);
        Assert.DoesNotContain("create", free.Calls);
        Assert.Equal(0, free.Calls.Count(c => c.StartsWith("inspect:PoolWatch.safe-zzzz", StringComparison.Ordinal)));

        // Nothing recorded: a new one of its own, and still none of them looked at.
        var fresh = new Unmovable(remadeEveryTime: false);
        Decoys(fresh.Beside);
        string used = PrepareFallback(Root, "why.", Owner, fresh, () => Now).Root;
        Assert.DoesNotContain(used, fresh.Beside.Keys);
        Assert.DoesNotContain(fresh.Calls, c => c.StartsWith("inspect:PoolWatch.safe-", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ the real steps, read-only or a rename in TEMP

    /// <summary>The real inspection, recording fakes for every step that would write.</summary>
    private sealed class InspectOnly : IRootOps
    {
        private readonly WindowsRootOps _real = new();
        public readonly List<string> Writes = new();
        public Facts Inspect(string root) => _real.Inspect(root);
        public int CreateSecure(string root, DirectorySecurity security) { Writes.Add(Path.GetFileName(root).Contains(FallbackMark) ? "create:safe" : "create"); return 0; }
        public void ApplySecure(string root, DirectorySecurity security) => Writes.Add("apply");
        public string MoveAside(string root, DateTime utcNow) { Writes.Add("move"); return root; }
        public string? RecordedFallback() => null; // never the real record: a test reads no registry value of PoolWatch's
        public bool HasRecord() => false;
        public bool RecordFallback(string? path) { Writes.Add("record"); return true; }
        public bool MoveTo(string from, string to) { Writes.Add("moveto"); return false; }
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
        Assert.Throws<DataRootRefusedException>(() => new AppPaths(root, null, secure: true, holdsInstance: true, rootOps: ops));
        Assert.DoesNotContain("create", ops.Writes);
        Assert.DoesNotContain("apply", ops.Writes);
        Assert.Contains("move", ops.Writes);
        Assert.Equal(["keep.txt"], Directory.GetFiles(target).Select(Path.GetFileName));
    }

    [Fact]
    public void MovingAJunctionAsideRenamesTheLinkNotItsTarget()
    {
        // The real rename, on a junction in TEMP (no ACL is read or written): the link moves, its target stays put.
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
    public void NoRecordOfAFolderOfItsOwnReadsAsNone()
    {
        // Read only: a key no one has (never PoolWatch's own, and nothing is written).
        var none = new WindowsRootOps(@"SOFTWARE\PoolWatch.Tests-" + Guid.NewGuid().ToString("N"));
        Assert.Null(none.RecordedFallback());
        Assert.False(none.HasRecord());
        // Only the record's value is a record: a key every Windows has, without that value, holds none, and a value every
        // Windows has in its key reads as one (opened to read, nothing written).
        Assert.False(new WindowsRootOps(@"SOFTWARE\Microsoft").HasRecord());
        Assert.True(new WindowsRootOps(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName").HasRecord());
        // So deleting a record takes its value, and the key only when nothing else is left in it.
        Assert.True(WindowsRootOps.DeleteKeyAfter(valuesLeft: 0, keysLeft: 0));
        Assert.False(WindowsRootOps.DeleteKeyAfter(valuesLeft: 1, keysLeft: 0));
        Assert.False(WindowsRootOps.DeleteKeyAfter(valuesLeft: 0, keysLeft: 1));
    }

    [Fact]
    public void TheRulesTheRecordsKeyIsMadeWithLetOnlyAdministratorsChangeItWhoeverMakesIt()
    {
        // Owned by Administrators, with nothing inherited and no rule for anyone but Administrators and SYSTEM: so the record
        // reads as one only an administrator could have written whatever accounts count as administrators now (UAC on or off).
        var sd = new RawSecurityDescriptor(WindowsRootOps.RecordSecurity().GetSecurityDescriptorBinaryForm(), 0);
        Assert.Equal(Administrators, sd.Owner!.Value);
        Assert.True((sd.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0);
        Assert.Equal([LocalSystem, Administrators], AcesOf(sd).Select(a => a.Sid).Order(StringComparer.Ordinal));
        Assert.All(AcesOf(sd), a => Assert.True(a.Allow && !a.Inherited));
        const string folder = @"C:\ProgramData\PoolWatch.safe-20261030T100000Z-aaaa";
        Assert.Equal(folder, WindowsRootOps.RecordOf(sd, folder, ElevatedAccounts.None));
    }

    [Fact]
    public void TheRecordsKeyIsAskedForWithThoseRules()
    {
        // How the record is written, up to the registry call: the key is made with RecordSecurity. A stand-in for that call
        // sees what it is given and writes nothing (it throws, so the record reads as not written).
        string? asked = null, rules = null;
        var ops = new WindowsRootOps(@"SOFTWARE\PoolWatch.Tests-" + Guid.NewGuid().ToString("N"), "DataFolder", (_, key, security) =>
        {
            asked = key;
            rules = security.GetSecurityDescriptorSddlForm(AccessControlSections.All);
            throw new UnauthorizedAccessException("a test writes nothing");
        });
        Assert.False(ops.RecordFallback(@"C:\ProgramData\PoolWatch.safe-20261030T100000Z-aaaa"));
        Assert.StartsWith(@"SOFTWARE\PoolWatch.Tests-", asked);
        Assert.Equal(WindowsRootOps.RecordSecurity().GetSecurityDescriptorSddlForm(AccessControlSections.All), rules);
        Assert.Contains("O:BA", rules);
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

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    private static extern int NtQueryObject(System.Runtime.InteropServices.SafeHandle handle, int informationClass, byte[] information, int length, out int returned);

    /// <summary>The rights a handle was granted (NtQueryObject, ObjectBasicInformation: PUBLIC_OBJECT_BASIC_INFORMATION's GrantedAccess).</summary>
    private static uint GrantedAccess(System.Runtime.InteropServices.SafeHandle h)
    {
        var info = new byte[56];
        Assert.Equal(0, NtQueryObject(h, 0, info, info.Length, out _));
        return BitConverter.ToUInt32(info, 4);
    }

    /// <summary>
    /// The opens that read a data folder's owner and rules and that secure it ask Windows for those rights alone, as the
    /// folder's owner may: Windows grants an owner READ_CONTROL and WRITE_DAC whatever the rules say, and the parent's
    /// FILE_LIST_DIRECTORY grants FILE_READ_ATTRIBUTES, but nothing grants SYNCHRONIZE to rules that shut administrators
    /// out, which CreateFileW adds to every open, overlapped or not. So both open through NtOpenFile, and each handle is granted exactly
    /// what was asked, no SYNCHRONIZE, as is the move-aside open; CreateFileW's opens of the same rights are given it. Measured on a TEMP folder
    /// this test made (no rule is written), whose owner and rules the real look then reads through such a handle.
    /// </summary>
    [Fact]
    public void TheOpensThatReadAndSecureADataFolderAskForTheOwnersRightsAlone()
    {
        string folder = Path.Combine(_base, "as-owner");
        Directory.CreateDirectory(folder);
        var (read, error, privileged) = NoFollow.OpenToRead(folder);
        using (read)
        {
            Assert.Equal((0, false), (error, privileged));
            Assert.Equal(NoFollow.READ_CONTROL | NoFollow.FILE_READ_ATTRIBUTES, GrantedAccess(read));
            Assert.NotNull(NoFollow.Security(read));
            Assert.True((NoFollow.Query(read).Attributes & FileAttributes.Directory) != 0);
        }
        foreach (uint access in new[] { WindowsRootOps.OwnerAndRules, WindowsRootOps.RulesAlone })
        {
            var (secure, secureError) = WindowsRootOps.OpenToSecure(folder, access);
            using (secure)
            {
                Assert.Equal(0, secureError);
                Assert.Equal(access, GrantedAccess(secure));
            }
        }
        // The move-aside open, the same way: DELETE and FILE_READ_ATTRIBUTES alone.
        using (var move = NoFollow.OpenForMove(folder))
            Assert.Equal(NoFollow.DELETE | NoFollow.FILE_READ_ATTRIBUTES, GrantedAccess(move));
        // Through CreateFileW the same rights come with SYNCHRONIZE, overlapped or not, which such rules do not grant.
        using (var synchronous = NoFollow.Open(folder, dir: true, NoFollow.READ_CONTROL | NoFollow.FILE_READ_ATTRIBUTES, NoFollow.ShareAll))
            Assert.Equal(NoFollow.READ_CONTROL | NoFollow.FILE_READ_ATTRIBUTES | NoFollow.SYNCHRONIZE, GrantedAccess(synchronous));
        using (var overlapped = CreateFileW(folder, NoFollow.READ_CONTROL | NoFollow.FILE_READ_ATTRIBUTES, NoFollow.ShareAll, IntPtr.Zero, NoFollow.OPEN_EXISTING,
                   NoFollow.FILE_FLAG_OPEN_REPARSE_POINT | NoFollow.FILE_FLAG_BACKUP_SEMANTICS | NoFollow.FILE_FLAG_OVERLAPPED, IntPtr.Zero))
            Assert.Equal(NoFollow.READ_CONTROL | NoFollow.FILE_READ_ATTRIBUTES | NoFollow.SYNCHRONIZE, GrantedAccess(overlapped));
        var facts = new WindowsRootOps().Inspect(folder);
        Assert.Equal((null, false), (facts.Unreadable, facts.LookFailed));
        Assert.NotNull(facts.OwnerSid);
    }

    /// <summary>
    /// A path whose NT name is longer than a UNICODE_STRING can count is not opened: its length in bytes does not fit the
    /// string's 16 bits, and cast to them it would name a shorter path. Here the name is the folder's and 32,768 characters
    /// more, so its byte count, cut to 16 bits, is the folder's own: the open would open the folder.
    /// </summary>
    [Fact]
    public void APathTooLongForAnNtNameIsNotOpenedAsAShorterOne()
    {
        string folder = Path.Combine(_base, "long-name");
        Directory.CreateDirectory(folder);
        string ntFolder = @"\??\" + folder;
        string path = @"\\?\" + folder + @"\" + new string('a', 32767);
        Assert.Equal(ntFolder.Length + 32768, (@"\??\" + path[4..]).Length);
        Assert.Equal(2 * ntFolder.Length, (ushort)(2 * (ntFolder.Length + 32768))); // what the 16-bit count would say
        var (h, error) = NoFollow.OpenAsOwner(path, NoFollow.READ_CONTROL | NoFollow.FILE_READ_ATTRIBUTES, NoFollow.ShareAll);
        using (h)
        {
            Assert.Equal(NoFollow.ERROR_FILENAME_EXCED_RANGE, error);
            Assert.True(h.IsInvalid);
        }
        // A name that fits, the folder's own, is opened as asked.
        Assert.Equal(32766, NoFollow.MaxNtNameChars);
        var (ok, okError) = NoFollow.OpenAsOwner(@"\\?\" + folder, NoFollow.READ_CONTROL | NoFollow.FILE_READ_ATTRIBUTES, NoFollow.ShareAll);
        using (ok) Assert.Equal(0, okError);
    }

    /// <summary>
    /// A usual folder owned by Administrators whose rules shut administrators out, read as its owner (no backup privilege
    /// needed), is judged as any folder only administrators can change: its rules are made PoolWatch's again and it is
    /// used, with its data. It is never set aside, and the start does not stop.
    /// </summary>
    [Fact]
    public void AnAdministratorsFolderWhoseRulesShutThemOutIsReadAsItsOwnerAndGivenPoolWatchsRules()
    {
        var shut = Folder(Administrators, true, new Ace(Administrators, false, FullControl, false, false, 3), new Ace(LocalSystem, false, FullControl, false, false, 3));
        Assert.Null(CannotReadNow(shut, Mine));
        Assert.Equal(Verdict.Repair, Decide(shut, Mine).Verdict);
        var ops = new FakeOps(shut, Exact);
        Prepare(Root, Owner, ops, () => Now);
        Assert.Equal(["inspect", "apply", "inspect"], ops.Calls.Take(3));
        Assert.DoesNotContain(ops.Calls, c => c.StartsWith("move", StringComparison.Ordinal) || c.StartsWith("create", StringComparison.Ordinal));
        Assert.Empty(ops.Records);
        // SECURITY.md says so, and keeps the stop for the folders PoolWatch cannot read as their owner.
        string security = UninstallerTests.OneLine(File.ReadAllText(Path.Combine(TrayIconsTests.RepoRoot(), "SECURITY.md")));
        Assert.Contains("**Without the backup privilege, PoolWatch cannot tell whose a shut data folder is unless Administrators own it.**", security);
        // What the read open asks for beyond an owner's rights (FILE_READ_ATTRIBUTES, as the open test above measures) is
        // said, with what grants it.
        Assert.Equal(NoFollow.READ_CONTROL | NoFollow.FILE_READ_ATTRIBUTES, NoFollow.ReadAccess);
        Assert.Contains("and PoolWatch's open asks for nothing more but the right to read its attributes, which `%ProgramData%`'s own rules give " +
                        "administrators (a folder's right to list what is in it grants that right on each entry in it, whatever the entry's rules say).", security);
        Assert.Contains("So a folder owned by Administrators, as every folder PoolWatch and 1.0.0-beta.1 make is, is read, judged and given PoolWatch's " +
                        "rules again even when its rules shut administrators out.", security);
        Assert.Contains("reads a folder Administrators own as its owner may, whatever its rules say, and judges and secures it as above;", security);
    }
}
