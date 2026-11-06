using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace PoolWatch.Engine;

/// <summary>
/// PoolWatch will not use its data folder: it could not be moved aside, created or secured, it cannot be read or used
/// as it is now, or PoolWatch's data is in a folder of its own that it cannot read now. The message says why.
/// </summary>
internal sealed class DataRootRefusedException(string message, bool noFallback = false, IReadOnlyList<string>? notes = null)
    : Exception(message)
{
    /// <summary>
    /// No folder of its own is made in place of the usual one either (<see cref="DataRootGuard.PrepareFallback"/> is not
    /// run), so the data in a folder PoolWatch cannot read or use now is never set aside or split between two folders:
    /// PoolWatch's data is in a recorded folder of its own that it cannot read now, or the usual folder cannot be read now
    /// or used as it is; or this start does not hold the one-instance mutex (<see cref="DataRootGuard.UseAsItIs"/>); or
    /// which folder the data folder is could not be read once it was checked (AppPaths).
    /// </summary>
    public bool NoFallback { get; } = noFallback;

    /// <summary>
    /// What the same start did before it was refused (a folder moved aside, say): it is not lost with the refusal.
    /// </summary>
    public IReadOnlyList<string> Notes { get; } = notes ?? [];

    /// <summary>
    /// This refusal, with <paramref name="before"/> (the notes of earlier steps of the same start) ahead of its own.
    /// </summary>
    public DataRootRefusedException After(IReadOnlyList<string> before) =>
        before.Count == 0 ? this : new(Message, NoFallback, [.. before, .. Notes]);

    /// <summary>The message PoolWatch stops with: the refusal, then what the same start did before it.</summary>
    public string Shown => Notes.Count == 0 ? Message : $"{Message} Before that, at this start: {string.Join(" ", Notes)}";
}

/// <summary>
/// The data root itself. <see cref="Core.Util.PathGuard.CheckReal"/> walks only what is BELOW the
/// root, and %ProgramData% lets any user create a folder in it, so a standard user could create %ProgramData%\PoolWatch
/// before PoolWatch's first elevated run: as a junction (then the ACL, every write and the retention deletes would land
/// in its target) or as a folder they own (and can still change). On every elevated start the root is opened WITHOUT
/// following links and judged by <see cref="Decide"/>:
/// <list type="bullet">
/// <item>a link (junction, symbolic link, mount point), a file, a folder owned by anyone but Administrators / SYSTEM /
/// TrustedInstaller, or one where anyone else may write, delete or re-permission it (any allow entry that applies to
/// the folder itself) is moved aside, renamed through its own handle to <c>PoolWatch.untrusted-&lt;UTC&gt;</c> (for a
/// link: the link itself, never its target), and noted. When administrators may not open a folder as it is, the
/// backup privilege reads its owner and rules: a foreign owner or writer it finds moves it aside the same way, and the
/// note names them; so does a folder whose owner not even an open with that privilege can read (a folder whose delete
/// is pending, say). A link used to be refused, which let a standard user stop the elevated PoolWatch from
/// starting at every logon by planting a junction there before its first run;</item>
/// <item>a folder owned by Administrators is read as its owner may read it (<see cref="NoFollow.OpenToRead"/>), whatever
/// its rules say, and is judged and given PoolWatch's rules like any folder only administrators can change; a folder
/// that may hold PoolWatch's data and that it cannot read now (<see cref="CannotReadNow"/>: the look at it fails, after
/// <see cref="Looks"/> tries, which includes an open that is denied while the backup privilege cannot be turned on to
/// read it; or its rules do not let administrators open it while the backup privilege reads it as an administrator's
/// alone, as for a folder SYSTEM or TrustedInstaller owns, or one whose rules take the owner's own rights away with an
/// entry for OWNER RIGHTS) is never moved aside, and no other data folder is used in its place: the start stops, naming
/// it (<see cref="DataRootRefusedException.NoFallback"/>);</item>
/// <item>a missing root is created with its security descriptor applied AT creation (no moment with inherited
/// permissions);</item>
/// <item>an admin-owned root whose permissions differ from PoolWatch's gets them once, through a handle (no longer
/// re-applied across the whole tree on every start); when its owner cannot be set, its rules alone
/// are. When not even those can be applied, or they still differ after they were, it is used as it is, since only
/// administrators can change it, unless its rules would let someone else change what PoolWatch makes in it, or
/// administrators may not make files in it: then the start stops, naming it (NoFallback);</item>
/// <item>otherwise it is kept as it is.</item>
/// </list>
/// When the root cannot be made safe at all (a process holds a foreign folder there open, or makes it again after every
/// move), PoolWatch does not exit: it keeps its data in a folder of its own next to it (<see cref="PrepareFallback"/>)
/// and records that folder's name where only administrators can write (HKLM, <see cref="IRootOps.RecordFallback"/>), so
/// a later start judges that one folder and no other of its kind. While that folder is admin-only it holds PoolWatch's
/// data: a later start that finds the usual name free, or the usual folder safe to use where it stands, moves it back
/// to the usual name (a usual folder that is there is moved aside first, and kept), and a start that cannot uses it in
/// place. The record is deleted once the data is back, or once that folder is gone or no longer admin-only (or its open
/// is denied and who owns it cannot be read: an open with the backup privilege was denied too), or when the record
/// names no folder of PoolWatch's own name or is not one PoolWatch can trust; the start's notes name such a folder.
/// While that folder is there and admin-only but PoolWatch cannot read it now, or the look at it fails (at each of <see
/// cref="Looks"/> tries, as for the usual folder), the record is kept and PoolWatch uses no other data folder: it
/// refuses to start, naming that folder (<see cref="DataRootRefusedException.NoFallback"/>), so its data is never split
/// between two folders. A note about a folder moved aside, or one no longer used, says when files named like
/// PoolWatch's lists of changes stay in it (<see cref="ChangeListNames"/>). Whatever was created or repaired is
/// inspected again through a handle before it is used.
/// Only a start given the proof that its thread holds the one-instance mutex runs these steps (<see cref="Prepare"/>,
/// <see cref="PrepareFallback"/>; <see cref="ExitSignal.Hold"/>); any other PoolWatch process finds the folder in use
/// without moving or recording anything (<see cref="UseAsItIs"/>), and makes the usual folder only when nothing is
/// there and no PoolWatch may hold that mutex. So no PoolWatch moves the folder a running tray app or headless run
/// uses, or makes it again while one runs. The decision is pure and tested; the file-system and registry steps are
/// behind <see cref="IRootOps"/>, so no test writes an ACL or the registry.
/// </summary>
internal static class DataRootGuard
{
    public const string Administrators = "S-1-5-32-544", LocalSystem = "S-1-5-18",
        TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    /// <summary>Rights that let a principal add, change, delete or re-permission: write / append data (create files and
    /// folders), write EA and attributes, delete child, delete, WRITE_DAC, WRITE_OWNER, GENERIC_WRITE, GENERIC_ALL.</summary>
    public const int WriteMask = 0x2 | 0x4 | 0x10 | 0x40 | 0x100 | 0x10000 | 0x40000 | 0x80000 | 0x10000000 | 0x40000000;

    /// <summary>One access entry as read from the folder.</summary>
    public readonly record struct Ace(string Sid, bool Allow, int Mask, bool Inherited, bool InheritOnly, int Flags);

    /// <summary>
    /// What the folder is, read without following links. <see cref="Unreadable"/>: why its security could not be read as it
    /// is; when the backup privilege read it anyway (<see cref="NoFollow.OpenToRead"/>), <see cref="OwnerSid"/> and <see
    /// cref="Aces"/> are what it read, and are null when not even that could. <see cref="LookFailed"/>: the open failed
    /// for a reason other than being denied, or it was denied and the backup privilege could not be turned on for a
    /// second open, or the rules could not be read through an open handle, so nothing is known about who owns it or may
    /// change it.
    /// </summary>
    public sealed record Facts(bool Exists, bool IsReparsePoint = false, bool IsDirectory = true, string? OwnerSid = null,
        bool DaclProtected = false, IReadOnlyList<Ace>? Aces = null, string? Unreadable = null, bool LookFailed = false);

    public enum Verdict { Create, Keep, Repair, MoveAside }

    public sealed record Decision(Verdict Verdict, string Reason);

    public static bool Trusted(string sid) => sid is Administrators or LocalSystem or TrustedInstaller;

    /// <summary>Pure: what to do with the root as it is (<paramref name="expected"/>: PoolWatch's own entries, from <see cref="Expected"/>).</summary>
    public static Decision Decide(Facts f, IReadOnlyList<Ace> expected)
    {
        if (!f.Exists) return new(Verdict.Create, "it does not exist yet");
        // Moved aside (renamed through a handle opened on the link itself), never followed and never fatal.
        if (f.IsReparsePoint) return new(Verdict.MoveAside, "it is a link (a junction, symbolic link or mount point), not a folder");
        if (!f.IsDirectory) return new(Verdict.MoveAside, "it is a file, not a folder");
        // Never used as it is when its permissions cannot be read so; when the backup privilege read whose it is, and that
        // owner or a rule already makes it unsafe, the reason says so. (Prepare never acts on this verdict for a folder it
        // cannot read now, CannotReadNow: it stops instead.)
        if (f.Unreadable is { } why)
            return new(Verdict.MoveAside, f.OwnerSid is not null && Decide(f with { Unreadable = null }, expected) is { Verdict: Verdict.MoveAside } own
                ? own.Reason : $"its permissions cannot be read ({why})");
        if (f.OwnerSid is not { } owner || !Trusted(owner)) return new(Verdict.MoveAside, $"it is owned by {f.OwnerSid ?? "an unknown account"}, not Administrators or SYSTEM");
        foreach (var a in f.Aces ?? [])
            if (a.Allow && !a.InheritOnly && !Trusted(a.Sid) && (a.Mask & WriteMask) != 0)
                return new(Verdict.MoveAside, $"{a.Sid} may change it ({(a.Inherited ? "an inherited" : "its own")} permission 0x{a.Mask:X})");
        return IsExact(f, expected) ? new(Verdict.Keep, "") : new(Verdict.Repair, "its permissions are not PoolWatch's (Administrators and SYSTEM, read for your account)");
    }

    /// <summary>Owned by Administrators, protected (nothing inherited), and exactly PoolWatch's entries.</summary>
    public static bool IsExact(Facts f, IReadOnlyList<Ace> expected)
    {
        if (f.OwnerSid != Administrators || !f.DaclProtected || f.Aces is not { } aces || aces.Any(a => a.Inherited)) return false;
        var have = aces.Select(Key).Order().ToList();
        var want = expected.Select(Key).Order().ToList();
        return have.SequenceEqual(want);
        static string Key(Ace a) => $"{a.Sid}|{a.Allow}|{a.Mask:X}|{a.Flags:X}";
    }

    /// <summary>PoolWatch's own entries for the root (<see cref="Interop.DataRootSecurity"/>), as they read back.</summary>
    public static IReadOnlyList<Ace> Expected(SecurityIdentifier? reader) =>
        AcesOf(new RawSecurityDescriptor(Interop.DataRootSecurity(reader).GetSecurityDescriptorBinaryForm(), 0));

    /// <summary>
    /// The account an entry of a kind PoolWatch cannot read is counted for (<see cref="AcesOf"/>): never a trusted one.
    /// </summary>
    public const string UnknownKind = "an entry of a kind PoolWatch cannot read";

    /// <summary>
    /// Every entry of the rules, whatever its kind, so none can leave a folder judged as one only administrators can
    /// change: an object entry is read like any other (its account, allow or deny, its rights); a compound entry counts
    /// as an allow entry with its rights; a kind Windows does not define counts as an allow entry with every right for
    /// <see cref="UnknownKind"/>.
    /// </summary>
    public static IReadOnlyList<Ace> AcesOf(RawSecurityDescriptor sd) =>
        sd.DiscretionaryAcl is null ? [] : sd.DiscretionaryAcl.Cast<GenericAce>().Select(Of).ToList();

    private static Ace Of(GenericAce a)
    {
        bool inherited = a.IsInherited, inheritOnly = (a.AceFlags & AceFlags.InheritOnly) != 0;
        int flags = (int)(a.AceFlags & (AceFlags.ContainerInherit | AceFlags.ObjectInherit | AceFlags.NoPropagateInherit | AceFlags.InheritOnly));
        return a switch
        {
            QualifiedAce q => new(q.SecurityIdentifier.Value, q.AceQualifier == AceQualifier.AccessAllowed, q.AccessMask, inherited, inheritOnly, flags),
            KnownAce k => new(k.SecurityIdentifier.Value, true, k.AccessMask, inherited, inheritOnly, flags),
            _ => new(UnknownKind, true, -1, inherited, inheritOnly, flags),
        };
    }

    /// <summary>
    /// Makes the root safe to use (see the class summary) and returns what happened (for the log and the timeline). Run
    /// only by a start that holds the one-instance mutex (<see cref="AppPaths"/>). Throws <see
    /// cref="DataRootRefusedException"/> when it cannot be made safe (it could not be moved aside or created, or it kept
    /// changing while being checked) or when the data in a recorded folder of its own could not be brought back (the
    /// caller then uses that folder in place: <see cref="PrepareFallback"/>); and, with <see
    /// cref="DataRootRefusedException.NoFallback"/>, while that folder cannot be read, while the usual folder cannot be
    /// read now (<see cref="CannotReadNow"/>), and when the usual folder cannot be used as it is while its rules are not
    /// PoolWatch's (they could not be applied, or still differ once applied: <see cref="NotUsableAsItIs"/>). The refusal
    /// carries the notes of what this start did before it (<see cref="DataRootRefusedException.Notes"/>: a folder moved
    /// aside, say). <paramref name="pause"/>: the wait between two looks at a folder whose look failed (default:
    /// Thread.Sleep; tests pass their own).
    /// </summary>
    public static List<string> Prepare(string root, SecurityIdentifier? reader, IRootOps ops, Func<DateTime> utcNow, Action<TimeSpan>? pause = null)
    {
        var notes = new List<string>();
        try { return PrepareSteps(root, reader, ops, utcNow, pause ?? Thread.Sleep, notes); }
        catch (DataRootRefusedException e) { throw e.After(notes); }
    }

    /// <summary>
    /// How many times PoolWatch looks at a folder that may hold its data (the usual folder, or the recorded folder of its
    /// own) when the look fails, before it judges that folder by the last look (<see cref="LookPause"/> apart: <see
    /// cref="LookAt"/>).
    /// </summary>
    internal const int Looks = 3;

    internal static readonly TimeSpan LookPause = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// A folder that may hold PoolWatch's data (the usual folder, or the recorded folder of its own) as a look finds it. A
    /// look that fails (it throws, or <see cref="Facts.LookFailed"/>) is made again, <see cref="Looks"/> times in all, with
    /// <paramref name="pause"/> (<see cref="LookPause"/>) between two; when the last one fails too it is returned as such
    /// (a look that threw: as facts with LookFailed and why).
    /// </summary>
    private static Facts LookAt(string folder, IRootOps ops, Action<TimeSpan> pause)
    {
        for (int look = 1; ; look++)
        {
            Facts f;
            try { f = ops.Inspect(folder); }
            catch (Exception e) { f = new Facts(true, Unreadable: e.Message.TrimEnd('.'), LookFailed: true); }
            if (!f.LookFailed || look == Looks) return f;
            pause(LookPause);
        }
    }

    /// <summary>
    /// Why a folder that may hold PoolWatch's data cannot be read now, or null: the look at it failed (<see
    /// cref="Facts.LookFailed"/>, an open that was denied while the backup privilege could not be turned on included:
    /// nothing is known about who owns it or may change it), or its rules do not let
    /// administrators open it while the owner and rules the backup privilege read make it an administrator's alone. Such a
    /// folder is never moved aside, used, or given up for another data folder: a recorded one stops the start (<see
    /// cref="CannotRead"/>), and so does the usual one (<see cref="UsualCannotRead"/>). A link or a file is never such a
    /// folder.
    /// </summary>
    internal static string? CannotReadNow(Facts f, IReadOnlyList<Ace> expected)
    {
        if (f is not { Exists: true, IsReparsePoint: false, IsDirectory: true }) return null;
        if (f.LookFailed) return $"the look at it failed ({f.Unreadable})";
        if (f.Unreadable is { } why && f.OwnerSid is not null && Decide(f with { Unreadable = null }, expected).Verdict is Verdict.Keep or Verdict.Repair)
            return $"its permissions do not let administrators open it ({why})";
        return null;
    }

    /// <summary>The refusal while the usual folder cannot be read now (<see cref="CannotReadNow"/>).</summary>
    private static DataRootRefusedException UsualCannotRead(string root, string why) =>
        new($"PoolWatch cannot read its data folder {root} now ({why}). It neither moves that folder aside nor uses another data folder " +
            "meanwhile, so the data in it is neither set aside nor split: it starts as usual once that folder can be read again.",
            noFallback: true);

    /// <summary>
    /// The account a rule of the folder lets change files and folders that are made in it, when that account is not one
    /// PoolWatch trusts, or null: an allow entry that applies only to what is made in it (inherit-only) and gives a right
    /// that changes them (<see cref="WriteMask"/>). Rules that apply to the folder itself are judged by <see
    /// cref="Decide"/>. CREATOR OWNER and OWNER RIGHTS stand for the owner of what is made, which for PoolWatch's own files
    /// is an administrator, so they do not count.
    /// </summary>
    internal static string? HandsDown(Facts f) =>
        f.Aces?.FirstOrDefault(a => a.Allow && a.InheritOnly && (a.Mask & WriteMask) != 0 && !Trusted(a.Sid) && a.Sid is not (CreatorOwner or OwnerRights))
            is { Sid: { } sid } ? sid : null;

    /// <summary>CREATOR OWNER and OWNER RIGHTS (<see cref="HandsDown"/>).</summary>
    public const string CreatorOwner = "S-1-3-0", OwnerRights = "S-1-3-4";

    /// <summary>
    /// Why a folder only administrators can change whose rules are not PoolWatch's (they could not be applied, or still
    /// differ once applied; or, for a process that changes nothing, they were not tried) cannot be used as it is, or
    /// null: its rules would let someone else change what PoolWatch makes in it (<see cref="HandsDown"/>), or
    /// administrators may not make files and folders in it (<see cref="IRootOps.MayWrite"/>).
    /// </summary>
    private static string? NotUsableAsItIs(string folder, Facts f, IRootOps ops) =>
        HandsDown(f) is { } who ? $"its permissions would let {who} change the files PoolWatch makes in it"
        : !ops.MayWrite(folder) ? "administrators may not make files in it" : null;

    /// <summary>
    /// The refusal for a folder that holds PoolWatch's data and that cannot be used as it is (<see
    /// cref="NotUsableAsItIs"/>): no other data folder is used, so that data is neither set aside nor split. <paramref
    /// name="before"/>: what the message starts with.
    /// </summary>
    private static DataRootRefusedException NotUsable(string before, string folder, string notApplied, string why) =>
        new($"{before}{folder}: {notApplied}, and it cannot be used as it is: {why}. PoolWatch neither moves that folder aside nor uses another " +
            "data folder meanwhile, so the data in it is neither set aside nor split: it starts as usual once that folder can be used " +
            "(once an administrator gives it PoolWatch's permissions, say: Administrators and SYSTEM full control, read for your account).",
            noFallback: true);

    /// <summary>
    /// For a process that does not hold the one-instance mutex, which uses a folder only as it stands (<see
    /// cref="UseAsItIs"/>): why a folder only administrators can change whose rules are not PoolWatch's (Repair) cannot be
    /// used so (<see cref="NotUsableAsItIs"/>), or null (and null for any other verdict, which <see cref="Decide"/>
    /// judges).
    /// </summary>
    private static string? NotAsItStands(string folder, Facts f, IReadOnlyList<Ace> expected, IRootOps ops) =>
        Decide(f, expected).Verdict == Verdict.Repair ? NotUsableAsItIs(folder, f, ops) : null;

    /// <summary>
    /// The refusal of a process that does not hold the one-instance mutex: it cannot use <paramref name="what"/> as it
    /// stands, and changes no data folder.
    /// </summary>
    private static DataRootRefusedException AsItStandsRefused(string what, string why) =>
        new($"PoolWatch cannot use {what} as it stands ({why}), and this PoolWatch run changes no data folder: only the tray app, a " +
            "headless run or the uninstaller's cleanup does that, at its start. Nothing was written. Exit PoolWatch if it runs and start " +
            "it again (its start makes the data folder safe, keeps the data in a folder of its own that this run then uses, or says why " +
            "it cannot), then try again.", noFallback: true);

    private static List<string> PrepareSteps(string root, SecurityIdentifier? reader, IRootOps ops, Func<DateTime> utcNow, Action<TimeSpan> pause,
        List<string> notes)
    {
        var expected = Expected(reader);
        var security = Interop.DataRootSecurity(reader);
        bool applied = false, triedHome = false;
        string? notApplied = null; // why PoolWatch's own rules could not be applied, at this start's one try
        for (int attempt = 0; attempt < 6; attempt++)
        {
            var f = LookAt(root, ops, pause);
            // It may hold PoolWatch's data (an earlier version's, say), and nothing is known about it, or only an
            // administrator could have made it so: never moved aside, so its data is never set aside or split.
            if (CannotReadNow(f, expected) is { } cannot) throw UsualCannotRead(root, cannot);
            var d = Decide(f, expected);
            switch (d.Verdict)
            {
                case Verdict.Keep:
                case Verdict.Repair when applied:
                {
                    // The usual folder can be used. A folder of its own that PoolWatch used in its place and that is still
                    // admin-only holds its data: that one comes back here (SettleRecord), and the usual folder is moved aside.
                    var (home, said) = SettleRecord(root, expected, ops, pause);
                    if (home is null)
                    {
                        // After one try at its rules: only owner and writers matter for safety, and those checked out (Repair,
                        // not MoveAside). Keep it rather than refuse to start, or leave its data for a folder of its own, over
                        // rules that differ in detail or that PoolWatch could not apply; unless what PoolWatch makes in it
                        // could then be changed by someone else or cannot be made at all: then the start stops, and moves
                        // that folder nowhere.
                        if (notApplied is not null)
                        {
                            if (NotUsableAsItIs(root, f, ops) is { } notUsable) throw NotUsable("", root, notApplied, notUsable);
                            notes.Add($"{root}: {notApplied}; it is used as it is (only administrators can change it)");
                        }
                        else if (d.Verdict == Verdict.Repair)
                        {
                            const string differs = "its permissions still differ in detail after PoolWatch applied its own";
                            if (NotUsableAsItIs(root, f, ops) is { } notUsable) throw NotUsable("", root, differs, notUsable);
                            notes.Add($"{root}: {differs}; kept (only administrators can change it)");
                        }
                        if (said is not null) notes.Add(said);
                        return notes;
                    }
                    string aside;
                    // When the usual folder cannot be moved aside (something holds it open), or the data cannot be moved
                    // back after it, the caller goes on using the folder the data is in (PrepareFallback), never the usual one.
                    try { aside = ops.MoveAside(root, utcNow()); }
                    catch (DataRootRefusedException e)
                    {
                        throw new DataRootRefusedException($"PoolWatch's data is in {home}, and the folder at {root} could not be moved aside to " +
                                                           $"make room for it ({e.Message.TrimEnd('.')}).");
                    }
                    if (!ops.MoveTo(home, root))
                        throw new DataRootRefusedException($"PoolWatch's data is in {home}, and it could not be moved back to {root}; the folder " +
                                                           $"that was at {root} is now {aside}, and anything in it stays there.");
                    TryClearRecord(ops); // if it cannot be cleared now, SettleRecord tries again: it names a folder no longer there, never used
                    notes.Add($"PoolWatch's data is back in {root} (it was kept in {home} while PoolWatch could not use {root}); the folder " +
                              $"that was at {root} was moved aside to {aside}, and anything in it stays there." + ListsLeft(aside, ops, adminOnly: true));
                    triedHome = true;
                    applied = false;
                    notApplied = null;
                    break; // inspect again: what came back is judged like any root before it is used
                }
                case Verdict.Create:
                    // A folder PoolWatch used while it could not use this one comes home first: renamed back to the
                    // usual name, judged again like any root before it is used, and its record cleared. When it cannot be
                    // moved (something holds it open), PoolWatch goes on using it rather than start empty. While it cannot be
                    // read, no usual folder is made from nothing (HomeFor throws the refusal).
                    if (!triedHome && HomeFor(root, expected, ops, pause) is { } away)
                    {
                        triedHome = true;
                        if (!ops.MoveTo(away, root))
                            throw new DataRootRefusedException($"PoolWatch's data is in {away}, and it could not be moved back to {root}.");
                        TryClearRecord(ops); // if it cannot be cleared now, SettleRecord tries again: it names a folder no longer there, never used
                        notes.Add($"PoolWatch's data is back in {root} (it was kept in {away} while PoolWatch could not use {root})");
                        break;
                    }
                    int err = ops.CreateSecure(root, security);
                    if (err != 0 && err != NoFollow.ERROR_ALREADY_EXISTS)
                        throw new DataRootRefusedException($"PoolWatch could not create its data folder {root} (error {err}).");
                    break; // inspect again: what was created must check out (or someone else's, created meanwhile, is judged)
                case Verdict.Repair:
                    // Owned by an administrator and changeable by no one else: when PoolWatch cannot apply its own rules (not
                    // even its rules alone: ApplySecure), the look below still decides, and a folder that checks out is used
                    // as it is (or the start stops: NotUsableAsItIs), never given up for a folder of its own while its data
                    // stays here.
                    try
                    {
                        ops.ApplySecure(root, security);
                        notes.Add($"{root}: {d.Reason}; PoolWatch applied its own (once)");
                    }
                    catch (Exception e) { notApplied = $"{d.Reason}; PoolWatch could not apply its own ({e.Message.TrimEnd('.')})"; }
                    applied = true;
                    break;
                case Verdict.MoveAside:
                    string moved = ops.MoveAside(root, utcNow());
                    notes.Add($"{root} was not safe to use ({d.Reason}): it was moved aside to {moved}. Anything PoolWatch had there is in {moved}." +
                              (f is { IsReparsePoint: false, IsDirectory: true } ? ListsLeft(moved, ops, adminOnly: false) : ""));
                    break;
            }
        }
        throw new DataRootRefusedException($"PoolWatch could not secure its data folder {root}: it kept changing while being checked.");
    }

    /// <summary>
    /// The files that hold PoolWatch's lists of changes, in its data folder: <c>applied-changes.json</c> and
    /// <c>service-changes.json</c>. Active changes and the uninstaller read them only there.
    /// </summary>
    public static readonly IReadOnlyList<string> ChangeListNames = [Changes.ChangeRecords.FileName, ServicesController.ChangeFileName];

    /// <summary>
    /// Said of a folder PoolWatch no longer uses that holds files named like its lists of changes (<see
    /// cref="ChangeListNames"/>), so they are not lost track of: PoolWatch does not read them there, so neither Undo nor
    /// the uninstaller puts back what only they list. <paramref name="adminOnly"/>: only administrators could change that
    /// folder, so those are PoolWatch's own lists; otherwise someone else could have written them, and they are named
    /// only as files named like them. Empty when it holds none, or when that cannot be told. Only called for a folder,
    /// never a link.
    /// </summary>
    private static string ListsLeft(string folder, IRootOps ops, bool adminOnly)
    {
        IReadOnlyList<string> lists;
        try { lists = ops.ChangeListsIn(folder); }
        catch (Exception) { return ""; }
        if (lists.Count == 0) return "";
        string which = string.Join(", ", lists);
        return (adminOnly ? $" Its lists of changes ({which}) stay there too" : $" Files in it named like PoolWatch's lists of changes ({which}) stay there too") +
               ": PoolWatch does not read them there, so neither Undo in Active changes nor the uninstaller puts back what only they list.";
    }

    /// <summary>
    /// The name a folder PoolWatch uses in place of its data root gets, next to it:
    /// <c>PoolWatch.safe-&lt;UTC&gt;-&lt;random&gt;</c>.
    /// </summary>
    public const string FallbackMark = ".safe-";

    /// <summary>
    /// What a start finds a record of a folder used in place of the data root names (<see cref="Look"/>).
    /// </summary>
    private enum Found
    {
        /// <summary>
        /// No record PoolWatch can trust, or one that names no folder of PoolWatch's own name next to the root.
        /// </summary>
        NoneOfItsOwn,
        /// <summary>The folder is not there.</summary>
        Gone,
        /// <summary>
        /// Owned by an administrator and changeable by no one else (Keep or Repair): it holds PoolWatch's data.
        /// </summary>
        Usable,
        /// <summary>
        /// A folder is there that PoolWatch cannot read now (<see cref="CannotReadNow"/>): owned by an administrator and
        /// changeable by no one else as the backup privilege reads it, but its rules do not let administrators open it; or
        /// the look at it failed at each of its <see cref="Looks"/> tries (it threw, or the open failed for a reason other
        /// than being denied, or was denied while the backup privilege could not be turned on, or its rules could not be
        /// read through the open handle: <see cref="Facts.LookFailed"/>). It may hold PoolWatch's data, so its record is
        /// kept and no other data folder is used (<see cref="CannotRead"/>).
        /// </summary>
        Unreadable,
        /// <summary>
        /// It is there, and someone who is not an administrator owns it or may change it, it is a link or a file, or its
        /// open is denied and who owns it is not known: an open with the backup privilege was denied too (one whose delete
        /// is pending, say).
        /// </summary>
        NotAdminOnly,
    }

    /// <summary>
    /// The record of the folder PoolWatch uses in place of <paramref name="root"/> (<see
    /// cref="IRootOps.RecordedFallback"/>: a record only administrators can write) and that folder, as this start finds
    /// them (<see cref="Found"/>), why, when that folder is there, it cannot be used, and whether it is a folder (not a
    /// link or a file). Only a name of PoolWatch's own pattern right next to the root (<see cref="IsFallbackName"/>) is
    /// looked at, and only that one folder is ever judged: none is looked for by its name, so folders someone else makes
    /// with names like it are never even looked at. A record that cannot be read reads as none (RecordedFallback returns
    /// null), so it is <see cref="Found.NoneOfItsOwn"/>, like one PoolWatch cannot trust. That folder is looked at as the
    /// usual folder is (<see cref="LookAt"/>: a look that fails is made again, <see cref="Looks"/> times in all, with
    /// <paramref name="pause"/> between two). <paramref name="saw"/> is given what the last look at that folder found
    /// (UseAsItIs judges a usable one further).
    /// </summary>
    private static (Found What, string? Folder, string? Why, bool IsFolder) Look(string root, IReadOnlyList<Ace> expected, IRootOps ops,
        Action<TimeSpan> pause, Action<Facts>? saw = null)
    {
        if (ops.RecordedFallback() is not { } recorded || !IsFallbackName(root, recorded)) return (Found.NoneOfItsOwn, null, null, false);
        var (what, why, isFolder) = Judge(() =>
        {
            var f = LookAt(recorded, ops, pause);
            saw?.Invoke(f);
            return f;
        }, expected);
        return (what, recorded, why, isFolder);
    }

    /// <summary>
    /// What a folder of PoolWatch's own name is, as a look at it finds it (<see cref="Found"/>; never <see
    /// cref="Found.NoneOfItsOwn"/>), why when it cannot be used, and whether it is a folder (not a link or a file). A
    /// folder PoolWatch cannot read now (<see cref="CannotReadNow"/>) is <see cref="Found.Unreadable"/>: one whose look
    /// failed (<see cref="Facts.LookFailed"/>: its open failed for a reason other than being denied, or was denied while the
    /// backup privilege could not be turned on for a second open, or its rules could not be read through the open handle;
    /// or the look threw), so that nothing is known about it; and one whose rules do not let administrators open it, that
    /// an open with the backup privilege read, while the owner and rules that open read make it an administrator's alone.
    /// One whose open is denied even with that privilege turned on (a delete pending, say) is <see
    /// cref="Found.NotAdminOnly"/>: who owns it cannot be read.
    /// </summary>
    private static (Found What, string? Why, bool IsFolder) Judge(Func<Facts> look, IReadOnlyList<Ace> expected)
    {
        Facts f;
        try { f = look(); }
        catch (Exception e) { return (Found.Unreadable, $"the look at it failed ({e.Message.TrimEnd('.')})", false); }
        bool isFolder = f is { Exists: true, IsReparsePoint: false, IsDirectory: true };
        if (CannotReadNow(f, expected) is { } cannot) return (Found.Unreadable, cannot, true);
        var d = Decide(f, expected);
        if (d.Verdict == Verdict.Create) return (Found.Gone, null, false);
        if (d.Verdict is Verdict.Keep or Verdict.Repair) return (Found.Usable, null, true);
        if (isFolder && f.Unreadable is { } why && f.OwnerSid is null) return (Found.NotAdminOnly, $"who owns it cannot be read ({why})", true);
        return (Found.NotAdminOnly, d.Reason, isFolder);
    }

    /// <summary>
    /// The refusal while PoolWatch's data is in a recorded folder of its own that it cannot read now (<see
    /// cref="Found.Unreadable"/>): no other data folder is used, neither the usual one made from nothing nor a new one of
    /// its own, so that data is never split between two folders. <paramref name="before"/>: what the message starts with.
    /// </summary>
    private static DataRootRefusedException CannotRead(string before, string folder, string root, string why) =>
        new($"{before}PoolWatch's data is in {folder}, a folder of its own it used in place of {root}, and PoolWatch cannot read " +
            $"that folder now ({why}). It uses no other data folder meanwhile, so that its data is not split between two folders: it " +
            "starts as usual once that folder can be read again, or once that folder is deleted.", noFallback: true);

    /// <summary>
    /// At a start whose usual folder can be used (Keep, or Repair after one try), the record of a folder used in its place
    /// (<see cref="Look"/>):
    /// <list type="bullet">
    /// <item>names a folder that is still PoolWatch's own and admin-only: that folder is returned, and Prepare brings it
    /// back to the usual name (the record is deleted once it is there);</item>
    /// <item>names a folder that is there and admin-only but cannot be read now, or whose look failed at each of its <see
    /// cref="Looks"/> tries (<see cref="Found.Unreadable"/>): the record is kept, and the refusal is thrown (<see
    /// cref="CannotRead"/>);</item>
    /// <item>names a folder that is no longer admin-only, or is a link or a file, or whose owner not even an open with the
    /// backup privilege turned on can read (a delete pending, say): PoolWatch never uses it again, as it never uses the
    /// usual folder so. The note names it and says that anything PoolWatch kept there stays there, and names the files in
    /// it named like its lists of changes (<see cref="ListsLeft"/>); the record is deleted, and when that fails the note
    /// says so and a later start tries again;</item>
    /// <item>names a folder that is gone, names no folder of PoolWatch's own name next to the usual folder, or is one that
    /// someone who is not an administrator could have written (or that cannot be read): the record is deleted, so it is
    /// not left behind for good, and when that fails the note says so and a later start tries again. The note names a
    /// gone folder; a name that is not PoolWatch's own is never named.</item>
    /// </list>
    /// Returns that folder, or null with the note for this start (null: no record, nothing to say).
    /// </summary>
    private static (string? Home, string? Note) SettleRecord(string root, IReadOnlyList<Ace> expected, IRootOps ops, Action<TimeSpan> pause)
    {
        (Found What, string? Folder, string? Why, bool IsFolder) look;
        try
        {
            if (!ops.HasRecord()) return (null, null);
            look = Look(root, expected, ops, pause);
        }
        // A guard only: the steps read a record that cannot be read as none (HasRecord false, RecordedFallback null, and
        // such a record is deleted below as one PoolWatch cannot trust). One that throws all the same leaves the record as
        // it is, and the start goes on with the usual folder.
        catch (Exception) { return (null, null); }
        var (what, folder, why, isFolder) = look;
        return what switch
        {
            Found.Usable => (folder, null),
            Found.Unreadable => throw CannotRead("", folder!, root, why!),
            Found.NotAdminOnly => (null, $"{folder}, the folder PoolWatch's record names in place of {root}, is not one only administrators can " +
                                         $"change now ({why}), so PoolWatch does not use it any more; anything PoolWatch kept there stays there." +
                                         (isFolder ? ListsLeft(folder!, ops, adminOnly: false) : "") + " " + RecordDeleted(ops)),
            Found.Gone => (null, $"{folder}, the folder PoolWatch's record names in place of {root}, is not there any more. " + RecordDeleted(ops)),
            _ => (null, $"PoolWatch's record of a folder used in place of {root} names no folder of PoolWatch's own, or is not one PoolWatch " +
                        "can trust. " + RecordDeleted(ops)),
        };
    }

    /// <summary>
    /// Deletes the record, and says whether it was deleted (a step that throws counts as one that failed).
    /// </summary>
    private static string RecordDeleted(IRootOps ops) =>
        TryClearRecord(ops) ? "Its record was deleted." : "Its record could not be deleted now; a later start tries again.";

    private static bool TryClearRecord(IRootOps ops)
    {
        try { return ops.RecordFallback(null); }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// At a start whose usual name is free: the recorded folder of its own to bring back there (<see cref="Look"/>: <see
    /// cref="Found.Usable"/>), or null. While that folder cannot be read, the refusal is thrown (<see cref="CannotRead"/>):
    /// the usual folder is not made from nothing while that one may hold PoolWatch's data. Any other record is settled once
    /// the usual folder is made (<see cref="SettleRecord"/>); one that cannot be read at all never stops a start (the same
    /// guard as there).
    /// </summary>
    private static string? HomeFor(string root, IReadOnlyList<Ace> expected, IRootOps ops, Action<TimeSpan> pause)
    {
        (Found What, string? Folder, string? Why, bool IsFolder) look;
        try { look = Look(root, expected, ops, pause); }
        catch (Exception) { return null; }
        if (look is (Found.Unreadable, { } folder, { } why, _)) throw CannotRead("", folder, root, why);
        return look.What == Found.Usable ? look.Folder : null;
    }

    /// <summary>
    /// Pure: whether <paramref name="candidate"/> is named as PoolWatch names a folder in place of <paramref
    /// name="root"/>, right next to it.
    /// </summary>
    internal static bool IsFallbackName(string root, string candidate)
    {
        try
        {
            string full = Path.GetFullPath(root).TrimEnd('\\'), path = Path.GetFullPath(candidate).TrimEnd('\\');
            return string.Equals(Path.GetDirectoryName(path), Path.GetDirectoryName(full), StringComparison.OrdinalIgnoreCase) &&
                   Path.GetFileName(path).StartsWith(Path.GetFileName(full) + FallbackMark, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// When the data root itself cannot be used (<see cref="Prepare"/> threw: a process holds a foreign folder there open,
    /// or makes it again after every move, or the data in a recorded folder of its own could not be brought back to it),
    /// PoolWatch keeps running on a folder of its own next to it instead of exiting: the one it recorded at an earlier
    /// start when it is still usable (<see cref="Look"/>), or a new one, created with PoolWatch's permissions at creation
    /// under a name no one can guess, checked through a handle before it is used, and recorded (<see
    /// cref="IRootOps.RecordFallback"/>) in place of the earlier one so a later start finds that one and no other. Its note
    /// says so, and names the recorded folder it replaces, with why. A later start that finds the usual name free, or the
    /// usual folder safe to use where it stands, moves the folder in use back there (<see cref="Prepare"/>). Throws <see
    /// cref="DataRootRefusedException"/> when no such folder can be made either; and the refusal with <see
    /// cref="DataRootRefusedException.NoFallback"/> while the recorded one is there and admin-only but cannot be read now
    /// (<see cref="CannotRead"/>), or cannot be used as it is while its rules are not PoolWatch's (<see
    /// cref="NotUsableAsItIs"/>): no new one is made in its place then. Each look at the recorded folder is made as at
    /// <see cref="Prepare"/> (<see cref="LookAt"/>); <paramref name="pause"/>: the wait between two looks at it when the
    /// look fails (default: Thread.Sleep; tests pass their own).
    /// </summary>
    public static (string Root, List<string> Notes) PrepareFallback(string root, string why, SecurityIdentifier? reader, IRootOps ops, Func<DateTime> utcNow,
        Action<TimeSpan>? pause = null)
    {
        var expected = Expected(reader);
        var security = Interop.DataRootSecurity(reader);
        var wait = pause ?? Thread.Sleep;
        string full = Path.GetFullPath(root).TrimEnd('\\');
        string Note(string used) =>
            $"{why} PoolWatch keeps running: it keeps its data in {used}, a folder of its own that only administrators can change, " +
            $"until a later start finds {full} free or safe to use and moves that folder back there (settings, records and reports in " +
            $"{full} are not used meanwhile).";
        // The recorded folder this start does not use, why (null: it is not there any more), and whether it is a folder.
        (string Folder, string? Why, bool IsFolder)? notUsed = null;
        (Found What, string? Folder, string? Why, bool IsFolder) look;
        try { look = Look(full, expected, ops, wait); }
        catch (Exception) { look = (Found.NoneOfItsOwn, null, null, false); } // a guard, as in SettleRecord: a new folder of its own, below
        switch (look)
        {
            case (Found.Usable, { } earlier, _, _):
            {
                // Owned by an administrator and changeable by no one else (Keep or Repair): PoolWatch's own rules are applied
                // once, and one whose rules still are not PoolWatch's (they could not be applied, or differ in detail) is
                // still used, as Prepare uses the usual folder after one try, and on the same terms (NotUsableAsItIs): when it
                // cannot be used as it is, no new one is made in its place.
                string? notApplied = null;
                try { if (Decide(LookAt(earlier, ops, wait), expected).Verdict == Verdict.Repair) ops.ApplySecure(earlier, security); }
                catch (Exception e) { notApplied = $"PoolWatch could not apply its own permissions ({e.Message.TrimEnd('.')})"; } // the look below decides
                Facts? seen = null;
                var (again, whyNot, isFolder) = Judge(() => (seen = LookAt(earlier, ops, wait)), expected);
                if (again == Found.Usable)
                {
                    if (seen is { } now && Decide(now, expected) is { Verdict: Verdict.Repair } differs && NotUsableAsItIs(earlier, now, ops) is { } notUsable)
                        throw NotUsable($"{why} PoolWatch's data is in a folder of its own, ", earlier, notApplied ?? differs.Reason, notUsable);
                    return (earlier, [Note(earlier)]);
                }
                if (again == Found.Unreadable) throw CannotRead(why + " ", earlier, full, whyNot!);
                notUsed = (earlier, again == Found.Gone ? null : whyNot, isFolder);
                break;
            }
            case (Found.Unreadable, { } unread, { } whyUnread, _):
                throw CannotRead(why + " ", unread, full, whyUnread);
            case (Found.Gone or Found.NotAdminOnly, { } other, var whyOther, var otherIsFolder):
                notUsed = (other, whyOther, otherIsFolder);
                break;
        }
        string stamp = utcNow().ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        for (int i = 0; i < 3; i++)
        {
            string fresh = $"{full}{FallbackMark}{stamp}-{Guid.NewGuid():N}";
            if (ops.CreateSecure(fresh, security) != 0) continue; // only a folder made here and now, never one that was there
            // Checked through a handle like any root: owned by an administrator and changeable by no one else (Keep), or
            // so but differing in a detail of PoolWatch's own entries (Repair), as Prepare keeps it too.
            if (Decide(ops.Inspect(fresh), expected).Verdict is not (Verdict.Keep or Verdict.Repair)) continue;
            string note = Note(fresh);
            if (notUsed is { } old)
                note += old.Why is null
                    ? $" The folder its record named before, {old.Folder}, is not there any more."
                    : $" The folder its record named before, {old.Folder}, could not be used again ({old.Why}); anything PoolWatch kept there stays there." +
                      (old.IsFolder ? ListsLeft(old.Folder, ops, adminOnly: false) : "");
            if (!ops.RecordFallback(fresh))
                note += " PoolWatch could not record that folder's name, so a later start that still cannot use the usual folder makes another one, " +
                        $"and what PoolWatch keeps in {fresh} does not come back to the usual folder by itself: it stays there.";
            return (fresh, [note]);
        }
        throw new DataRootRefusedException($"{why} PoolWatch could not make a folder of its own in its place either.");
    }

    /// <summary>
    /// The data folder for a PoolWatch process that does not hold the one-instance mutex (<see cref="AppPaths"/>: any but
    /// the tray app, a headless run, and the uninstall cleanup once it has stopped PoolWatch), found without moving,
    /// renaming, re-permissioning or recording anything, so it never moves a data folder. A recorded folder of its own
    /// that is admin-only holds PoolWatch's data (a start that holds the mutex brings it home, or uses it in place when it
    /// cannot), so that one is used; otherwise the usual folder, when it is safe to use as it stands, or when nothing is
    /// there, no PoolWatch may hold the mutex (<paramref name="instanceRunning"/>), and it is made, with PoolWatch's rules
    /// at creation, and then checks out. So it never makes the folder again under a running PoolWatch. Either folder is
    /// used as it stands only when its rules are PoolWatch's (Keep), or differ from them (Repair) while they hand nobody
    /// else a right to change what is made in it and let administrators make files in it (<see cref="NotUsableAsItIs"/>),
    /// so no write fails later for want of rights. Throws the refusal, with <see
    /// cref="DataRootRefusedException.NoFallback"/> (no folder of its own is made in its place), while the recorded folder
    /// cannot be read or cannot be used as it stands, when the usual folder cannot be used as it stands, when it is not
    /// there while a PoolWatch may hold the mutex, or when it cannot be made. Returns the folder, whether it is the usual
    /// one, and the note for this start. Each look at either folder is made as at <see cref="Prepare"/> (<see
    /// cref="LookAt"/>); <paramref name="pause"/>: the wait between two looks when one fails (default: Thread.Sleep; tests
    /// pass their own).
    /// </summary>
    public static (string Root, bool Usual, List<string> Notes) UseAsItIs(string root, SecurityIdentifier? reader, IRootOps ops, Func<bool> instanceRunning,
        Action<TimeSpan>? pause = null)
    {
        var expected = Expected(reader);
        var wait = pause ?? Thread.Sleep;
        string full = Path.GetFullPath(root).TrimEnd('\\');
        (Found What, string? Folder, string? Why, bool IsFolder) look;
        Facts? recorded = null;
        try { look = Look(full, expected, ops, wait, f => recorded = f); }
        catch (Exception) { look = (Found.NoneOfItsOwn, null, null, false); } // a guard, as in SettleRecord: the usual folder, below
        if (look is (Found.Usable, { } kept, _, _))
        {
            if (recorded is { } seen && NotAsItStands(kept, seen, expected, ops) is { } notUsable)
                throw AsItStandsRefused($"{kept}, the folder of its own PoolWatch keeps its data in while it cannot use {full},", notUsable);
            return (kept, false, [$"This PoolWatch run uses {kept}, the folder of its own PoolWatch keeps its data in while it cannot use {full}. " +
                                  "It moves no data folder: only the tray app, a headless run or the uninstaller's cleanup does that, at its start."]);
        }
        if (look is (Found.Unreadable, { } unread, { } whyUnread, _)) throw CannotRead("", unread, full, whyUnread);
        bool made = false;
        while (true)
        {
            var f = LookAt(root, ops, wait);
            var d = Decide(f, expected);
            string? notUsable = NotAsItStands(root, f, expected, ops);
            if (d.Verdict is Verdict.Keep or Verdict.Repair && notUsable is null) return (root, true, []);
            if (d.Verdict != Verdict.Create || made) throw AsItStandsRefused($"its data folder {full}", notUsable ?? d.Reason);
            // Nothing is there. While a PoolWatch may hold the mutex, the folder it uses is gone (renamed or deleted while it
            // runs): this run makes none in its place, so a running PoolWatch never finds a folder another process made.
            if (instanceRunning())
                throw new DataRootRefusedException($"PoolWatch's data folder {full} is not there, and a PoolWatch may be running (its " +
                                                   "one-instance lock exists): this PoolWatch run makes no data folder then. Nothing was written. " +
                                                   "Exit PoolWatch and start it again (its start makes the data folder), then try again.", noFallback: true);
            int err = ops.CreateSecure(root, Interop.DataRootSecurity(reader));
            if (err != 0 && err != NoFollow.ERROR_ALREADY_EXISTS)
                throw new DataRootRefusedException($"PoolWatch could not create its data folder {full} (error {err}).", noFallback: true);
            made = true; // look again: what was made, or what someone else made meanwhile, must check out
        }
    }
}

/// <summary>
/// The file-system and registry steps behind <see cref="DataRootGuard.Prepare"/> (tests pass fakes: no test writes an
/// ACL or the registry).
/// </summary>
internal interface IRootOps
{
    DataRootGuard.Facts Inspect(string root);
    /// <summary>Creates the folder with this security at creation; 0, or the Win32 error (ERROR_ALREADY_EXISTS when it exists).</summary>
    int CreateSecure(string root, DirectorySecurity security);
    /// <summary>
    /// Applies the owner and the protected DACL through a handle opened without following links; when that cannot be
    /// done, the protected DACL alone. Throws when neither could be applied.
    /// </summary>
    void ApplySecure(string root, DirectorySecurity security);
    /// <summary>Renames the root through its own handle to PoolWatch.untrusted-&lt;UTC&gt; next to it; returns the new path.</summary>
    string MoveAside(string root, DateTime utcNow);
    /// <summary>
    /// The folder PoolWatch recorded as the one it uses in place of the data root (<see cref="RecordFallback"/>); null when
    /// none is recorded, the record cannot be read, or it is kept where someone who is not an administrator could have
    /// written it.
    /// </summary>
    string? RecordedFallback();
    /// <summary>
    /// Whether a record of such a folder is there at all (its value, not just its key), whoever could have written it
    /// (false when that cannot be read).
    /// </summary>
    bool HasRecord();
    /// <summary>
    /// Records <paramref name="path"/> as that folder where only administrators can write, or clears the record (null);
    /// false when it could not be written or cleared.
    /// </summary>
    bool RecordFallback(string? path);
    /// <summary>
    /// Renames the folder <paramref name="from"/> to <paramref name="to"/> through its own handle (never a link); false
    /// when <paramref name="to"/> exists or it fails.
    /// </summary>
    bool MoveTo(string from, string to);
    /// <summary>
    /// Which of PoolWatch's lists of changes (<see cref="DataRootGuard.ChangeListNames"/>) are in <paramref name="folder"/>
    /// (a folder, never a link). Tests that name none get none.
    /// </summary>
    IReadOnlyList<string> ChangeListsIn(string folder) => [];
    /// <summary>
    /// Whether this process may make files and folders in <paramref name="folder"/> (false only when that is denied).
    /// Tests that say nothing get true.
    /// </summary>
    bool MayWrite(string folder) => true;
    /// <summary>
    /// Which folder <paramref name="folder"/> names now (<see cref="NoFollow.IdentityOf"/>), or null when that cannot be
    /// read: the data folder's writes go only to the very folder its start checked (PathGuard). Tests that say nothing get
    /// the name itself.
    /// </summary>
    string? IdentityOf(string folder) => folder;
    /// <summary>
    /// Holds <paramref name="folder"/> open so that no one can rename or delete it while the handle is open, when the folder
    /// there is <paramref name="identity"/> (<see cref="NoFollow.HoldFolder"/>); null when that cannot be done. Tests that
    /// say nothing hold nothing.
    /// </summary>
    IDisposable? Hold(string folder, string identity) => null;
}

/// <summary>The real steps, every one through a handle opened with FILE_FLAG_OPEN_REPARSE_POINT.</summary>
internal sealed class WindowsRootOps : IRootOps
{
    public DataRootGuard.Facts Inspect(string root) => Inspect(root, NoFollow.OpenToRead);

    /// <summary>
    /// <see cref="Inspect(string)"/> with its open passed in (tests pass one that hands back a handle of their own).
    /// The folder is opened to read its owner and rules (<see cref="NoFollow.OpenToRead"/>). When administrators may not
    /// open it as it is, the backup privilege reads them anyway: the facts then carry that owner and those rules with
    /// <see cref="DataRootGuard.Facts.Unreadable"/> set, so such a folder is never used as it is, and is judged by whose
    /// it is. When that open with the privilege is denied too (a folder whose delete is pending, say), who owns it is not
    /// known (no owner). An open that fails for a reason other than being denied, an open that is denied while the
    /// privilege could not be turned on for a second one (<c>Privileged</c> false), and rules that cannot be read through
    /// the open handle are a look that failed (<see cref="DataRootGuard.Facts.LookFailed"/>): nothing is known about it.
    /// </summary>
    internal static DataRootGuard.Facts Inspect(string root, Func<string, (SafeFileHandle Handle, int Error, bool Privileged)> open)
    {
        FileAttributes attrs;
        try { attrs = File.GetAttributes(root); } // read from the entry itself (a dangling link is still a link)
        catch (FileNotFoundException) { return new(false); }
        catch (DirectoryNotFoundException) { return new(false); }
        if ((attrs & FileAttributes.ReparsePoint) != 0) return new(true, IsReparsePoint: true);
        var (handle, e, privileged) = open(root);
        using var h = handle;
        if (h.IsInvalid)
        {
            if (e is NoFollow.ERROR_FILE_NOT_FOUND or NoFollow.ERROR_PATH_NOT_FOUND) return new(false);
            bool isDirectory = (attrs & FileAttributes.Directory) != 0;
            // Denied, and the backup privilege could not be turned on for a second open (not held, or a step that turns it
            // on failed): the folder was never read, so nothing is known about it.
            if (e == NoFollow.ERROR_ACCESS_DENIED && !privileged)
                return new(true, IsDirectory: isDirectory, Unreadable: $"error {e}, and the backup privilege could not be turned on to read it", LookFailed: true);
            return new(true, IsDirectory: isDirectory, Unreadable: $"error {e}", LookFailed: e != NoFollow.ERROR_ACCESS_DENIED);
        }
        var q = NoFollow.Query(h);
        if ((q.Attributes & FileAttributes.ReparsePoint) != 0) return new(true, IsReparsePoint: true);
        bool isDir = (q.Attributes & FileAttributes.Directory) != 0;
        if (NoFollow.Security(h) is not { } sd) return new(true, IsDirectory: isDir, Unreadable: $"error {Marshal.GetLastWin32Error()}", LookFailed: true);
        var facts = new DataRootGuard.Facts(true, false, isDir, sd.Owner?.Value, (sd.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0,
            DataRootGuard.AcesOf(sd));
        // Read only with the backup privilege: the plain open was denied (ACCESS_DENIED is the only error that tries it).
        return privileged ? facts with { Unreadable = $"error {NoFollow.ERROR_ACCESS_DENIED}" } : facts;
    }

    public int CreateSecure(string root, DirectorySecurity security) => NoFollow.CreateDirectoryWithSecurity(root, security);

    public IReadOnlyList<string> ChangeListsIn(string folder) =>
        DataRootGuard.ChangeListNames.Where(name => File.Exists(Path.Combine(folder, name))).ToList();

    public void ApplySecure(string root, DirectorySecurity security) =>
        ApplySecure(root, OpenToSecure, h => NoFollow.SetOwnerAndProtectedDacl(h, security), h => NoFollow.SetProtectedDacl(h, security));

    /// <summary>
    /// Opens the folder itself to change its owner or rules, asking for <paramref name="access"/> alone, as its owner may
    /// (<see cref="NoFollow.OpenAsOwner"/>): so a folder owned by Administrators whose rules shut administrators out gets
    /// PoolWatch's rules again through the rules-alone try, as an owner may always change its rules.
    /// </summary>
    internal static (SafeFileHandle, int) OpenToSecure(string root, uint access) => NoFollow.OpenAsOwner(root, access, NoFollow.FILE_SHARE_READ | NoFollow.FILE_SHARE_WRITE);

    /// <summary>The rights the first try asks for: the owner and the rules.</summary>
    internal const uint OwnerAndRules = NoFollow.WRITE_DAC | NoFollow.WRITE_OWNER | NoFollow.READ_CONTROL | NoFollow.FILE_READ_ATTRIBUTES;

    /// <summary>The rights the second try asks for: the rules alone, which an owner may always change.</summary>
    internal const uint RulesAlone = NoFollow.WRITE_DAC | NoFollow.READ_CONTROL | NoFollow.FILE_READ_ATTRIBUTES;

    /// <summary>
    /// <see cref="ApplySecure(string, DirectorySecurity)"/> with its open and its two steps passed in (tests pass fakes
    /// that write nothing). First the owner and the protected DACL (<paramref name="setAll"/>) through a handle with <see
    /// cref="OwnerAndRules"/>; when that open or step fails (its rules do not let administrators change its owner, say),
    /// the protected DACL alone (<paramref name="setRules"/>) through a handle with <see cref="RulesAlone"/>: an owner may
    /// always change the rules of what it owns, so PoolWatch's own rules then replace whatever the folder hands down to
    /// what is made in it. Each handle is opened without following links, and an entry that is a link is never changed.
    /// Throws when neither step worked.
    /// </summary>
    internal static void ApplySecure(string root, Func<string, uint, (SafeFileHandle Handle, int Error)> open, Func<SafeFileHandle, int> setAll,
        Func<SafeFileHandle, int> setRules)
    {
        var (allError, allOpened) = Through(root, OwnerAndRules, open, setAll);
        if (allError == 0) return;
        var (rulesError, rulesOpened) = Through(root, RulesAlone, open, setRules);
        if (rulesError == 0) return;
        throw new DataRootRefusedException(allOpened || rulesOpened
            ? $"PoolWatch could not secure its data folder {root} (error {allError}; its rules alone: error {rulesError})."
            : $"PoolWatch could not open its data folder {root} to secure it (error {allError}; for its rules alone: error {rulesError}).");
    }

    /// <summary>
    /// One try: the handle with <paramref name="access"/>, then <paramref name="set"/> through it. The error (0: done),
    /// and whether it opened.
    /// </summary>
    private static (int Error, bool Opened) Through(string root, uint access, Func<string, uint, (SafeFileHandle Handle, int Error)> open,
        Func<SafeFileHandle, int> set)
    {
        var (handle, error) = open(root, access);
        using var h = handle;
        if (h.IsInvalid) return (error == 0 ? -1 : error, false);
        if ((NoFollow.Query(h).Attributes & FileAttributes.ReparsePoint) != 0) throw new DataRootRefusedException($"{root} became a link while it was secured.");
        return (set(h), true);
    }

    public bool MayWrite(string folder) => MayWrite(folder, OpenOnItself);

    /// <summary>
    /// Opens the folder itself (never through a link) asking for <paramref name="access"/>, every kind of sharing allowed:
    /// the handle and the open's error (0 when it opened).
    /// </summary>
    internal static (SafeFileHandle Handle, int Error) OpenOnItself(string folder, uint access)
    {
        var h = NoFollow.Open(folder, dir: true, access, NoFollow.ShareAll);
        return (h, h.IsInvalid ? Marshal.GetLastWin32Error() : 0);
    }

    /// <summary>
    /// <see cref="MayWrite(string)"/> with its open passed in (the real one: <see cref="OpenOnItself"/>): a handle that
    /// asks to add files and folders (FILE_ADD_FILE, FILE_ADD_SUBDIRECTORY) is opened on the folder itself, never through
    /// a link, and closed at once. Only an open that is denied (ERROR_ACCESS_DENIED) says no: any other failure (a
    /// sharing violation, say) leaves the writes themselves to tell.
    /// </summary>
    internal static bool MayWrite(string folder, Func<string, uint, (SafeFileHandle Handle, int Error)> open)
    {
        var (handle, error) = open(folder, NoFollow.FILE_ADD_FILE | NoFollow.FILE_ADD_SUBDIRECTORY);
        using var h = handle;
        return !h.IsInvalid || error != NoFollow.ERROR_ACCESS_DENIED;
    }

    public string? IdentityOf(string folder) => NoFollow.IdentityOf(folder);

    public IDisposable? Hold(string folder, string identity) => NoFollow.HoldFolder(folder, identity);

    public string MoveAside(string root, DateTime utcNow)
    {
        string parent = Path.GetDirectoryName(Path.GetFullPath(root).TrimEnd('\\'))!;
        string name = Path.GetFileName(Path.GetFullPath(root).TrimEnd('\\'));
        // Open asking for DELETE | FILE_READ_ATTRIBUTES and NOT SYNCHRONIZE, so even a DACL that denies the running
        // principal cannot block the open — those two rights come from the parent folder (FILE_DELETE_CHILD,
        // FILE_LIST_DIRECTORY), and OpenForMove falls back to the backup / restore privileges for that one open (then turns
        // them off again) if that is not enough. An open through CreateFileW asks for SYNCHRONIZE too, overlapped or not,
        // which such a DACL does not grant and the parent cannot supply.
        using var h = NoFollow.OpenForMove(root);
        if (h.IsInvalid) throw new DataRootRefusedException($"PoolWatch could not move {root} aside (error {Marshal.GetLastWin32Error()}).");
        string stamp = utcNow.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        foreach (string target in AsideNames(parent, name, stamp))
            if (NoFollow.Rename(h, target)) return target;
        throw new DataRootRefusedException($"PoolWatch could not move {root} aside (error {Marshal.GetLastWin32Error()}).");
    }

    /// <summary>
    /// Where the folder PoolWatch uses in place of its data root is recorded: <c>HKLM\SOFTWARE\PoolWatch</c>, value
    /// <c>DataFolder</c> (the 64-bit view). HKLM\SOFTWARE lets only administrators make a key in it, and the record is
    /// read only while the key's owner and rules are an administrator's alone (<see cref="RecordOf"/>). PoolWatch makes
    /// the key only when it needs such a folder, with rules of its own (<see cref="RecordSecurity"/>), and deletes the
    /// value once the data is back in the usual folder, or at a start that uses the usual folder when the folder it names
    /// is gone or no longer admin-only, or the record names no folder of PoolWatch's own name or is not one PoolWatch can
    /// trust; the key itself goes with it when nothing else is left in it. Only that value is the record: a key of that
    /// name without it holds no record. Tests pass a key and a value of their own name, or ones every Windows has, which
    /// they only read, and <paramref name="createKey"/>, how the key is made (default: <see cref="CreateKey"/>), so a test
    /// sees the rules it is made with and nothing is written.
    /// </summary>
    internal WindowsRootOps(string recordKey = @"SOFTWARE\PoolWatch", string recordValue = "DataFolder",
        Func<RegistryKey, string, RegistrySecurity, RegistryKey>? createKey = null)
    {
        _recordKey = recordKey;
        _recordValue = recordValue;
        _createKey = createKey ?? CreateKey;
    }

    private readonly string _recordKey, _recordValue;
    private readonly Func<RegistryKey, string, RegistrySecurity, RegistryKey> _createKey;

    /// <summary>
    /// Makes (or opens) the key under <paramref name="machine"/>, with <paramref name="security"/> when it is made:
    /// RegCreateKeyEx applies the security attributes only when it creates the key.
    /// </summary>
    private static RegistryKey CreateKey(RegistryKey machine, string key, RegistrySecurity security) =>
        machine.CreateSubKey(key, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryOptions.None, security);

    public string? RecordedFallback() => RecordedFallback(ReadRecord);

    /// <summary>
    /// The record <paramref name="read"/> gives (its key's owner and rules, and its value; null when there is no key),
    /// judged by <see cref="RecordOf"/> with the accounts this PC's settings run with administrator rights (<see
    /// cref="AlwaysElevated.OfThisPc()"/>, as the start check counts them). Null when there is no key, its value is not
    /// there or is not text, the read throws, or the key could have been written by someone who is not an administrator
    /// (<see cref="RecordOf"/>). Tests pass the read.
    /// </summary>
    internal static string? RecordedFallback(Func<(RawSecurityDescriptor Rules, object? Value)?> read)
    {
        try { return read() is { } record ? RecordOf(record.Rules, record.Value, AlwaysElevated.OfThisPc()) : null; }
        catch (Exception) { return null; }
    }

    /// <summary>The record's key (its owner and rules) and its value, as read; null when there is no key.</summary>
    private (RawSecurityDescriptor Rules, object? Value)? ReadRecord()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(_recordKey);
        if (key is null) return null;
        return (new RawSecurityDescriptor(key.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access).GetSecurityDescriptorBinaryForm(), 0),
            key.GetValue(_recordValue));
    }

    /// <summary>
    /// The rules the record's key is made with: owned by Administrators, full control for Administrators and SYSTEM, and
    /// nothing inherited. So whether the record can be trusted (<see cref="RecordOf"/>) never depends on which account made
    /// it, or on UAC's settings then (by the default of client Windows, with UAC off a key's default owner is the account
    /// that makes it, which no longer counts as an administrator's once UAC is on). A key that is already there keeps its
    /// own rules.
    /// </summary>
    internal static RegistrySecurity RecordSecurity()
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new RegistrySecurity();
        security.SetOwner(admins);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { admins, system })
            security.AddAccessRule(new RegistryAccessRule(sid, RegistryRights.FullControl, InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// Pure: whether the record's key is deleted after its value: only when nothing else (no value, no key) is left in it.
    /// </summary>
    internal static bool DeleteKeyAfter(int valuesLeft, int keysLeft) => valuesLeft == 0 && keysLeft == 0;

    /// <summary>
    /// Pure: the folder a record names (<paramref name="value"/>, as read from the key), or null when it is not text or
    /// when the key (<paramref name="sd"/>: its owner and rules) could have been written by someone who is not an
    /// administrator (<see cref="InstallGuard.WhoMayChange"/>, with the accounts <paramref name="elevated"/> counts as
    /// administrators).
    /// A key's rights to set a value, make a key in it, delete it, change its rules or take it over are the same bits as
    /// a file's write, append, delete, write-rules and take-ownership rights, so the same judge applies.
    /// </summary>
    internal static string? RecordOf(RawSecurityDescriptor? sd, object? value, ElevatedAccounts elevated) =>
        InstallGuard.WhoMayChange(sd, alwaysElevated: elevated) is null ? value as string : null;

    public bool HasRecord()
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey(_recordKey);
            return key?.GetValue(_recordValue) is not null;
        }
        catch (Exception) { return false; }
    }

    public bool RecordFallback(string? path)
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            if (path is null)
            {
                using (var key = machine.OpenSubKey(_recordKey, writable: true))
                {
                    if (key is null) return true;
                    key.DeleteValue(_recordValue, throwOnMissingValue: false);
                    if (!DeleteKeyAfter(key.ValueCount, key.SubKeyCount)) return true; // what else is in it is not PoolWatch's to delete
                }
                machine.DeleteSubKey(_recordKey, throwOnMissingSubKey: false);
                return true;
            }
            using var made = _createKey(machine, _recordKey, RecordSecurity());
            made.SetValue(_recordValue, path, RegistryValueKind.String);
            return true;
        }
        catch (Exception) { return false; }
    }

    public bool MoveTo(string from, string to)
    {
        using var h = NoFollow.Open(from, dir: true, NoFollow.DELETE | NoFollow.FILE_READ_ATTRIBUTES, NoFollow.FILE_SHARE_READ | NoFollow.FILE_SHARE_WRITE);
        if (h.IsInvalid || (NoFollow.Query(h).Attributes & FileAttributes.ReparsePoint) != 0) return false;
        return NoFollow.Rename(h, to);
    }

    /// <summary>
    /// The names a root is moved aside to, in order: <c>name.untrusted-stamp</c>, then <c>-2</c> to <c>-9</c>, then two
    /// random ones (A standard user who pre-creates every predictable name cannot make the move fail, and with it the
    /// start).
    /// </summary>
    internal static IEnumerable<string> AsideNames(string parent, string name, string stamp)
    {
        for (int i = 1; i <= 9; i++) yield return Path.Combine(parent, $"{name}.untrusted-{stamp}{(i == 1 ? "" : "-" + i)}");
        for (int i = 0; i < 2; i++) yield return Path.Combine(parent, $"{name}.untrusted-{stamp}-{Guid.NewGuid():N}");
    }
}
