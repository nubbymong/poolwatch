using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PoolWatch.Engine;

/// <summary>PoolWatch will not use its data folder: it could not be moved aside, created or secured. The message says why.</summary>
internal sealed class DataRootRefusedException(string message) : Exception(message);

/// <summary>
/// S3 (review round 3): the data root itself. <see cref="Core.Util.PathGuard.CheckReal"/> walks only what is BELOW the
/// root, and %ProgramData% lets any user create a folder in it, so a standard user could create %ProgramData%\PoolWatch
/// before PoolWatch's first elevated run: as a junction (then the ACL, every write and the retention deletes would land in
/// its target) or as a folder they own (and can still change). On every elevated start the root is opened WITHOUT
/// following links and judged by <see cref="Decide"/>:
/// <list type="bullet">
/// <item>a link (junction, symbolic link, mount point), a file, a folder owned by anyone but Administrators / SYSTEM /
/// TrustedInstaller, one whose permissions cannot be read, or one where anyone else may write, delete or re-permission it
/// (any allow entry that applies to the folder itself) is moved aside, renamed through its own handle to
/// <c>PoolWatch.untrusted-&lt;UTC&gt;</c> (for a link: the link itself, never its target), and noted. M2: a link used to be
/// refused, which let a standard user stop the elevated PoolWatch from starting at every logon by planting a junction
/// there before its first run;</item>
/// <item>a missing root is created with its security descriptor applied AT creation (no moment with inherited permissions);</item>
/// <item>an admin-owned root whose permissions differ from PoolWatch's gets them once, through a handle (review round 3,
/// item 6: no longer re-applied across the whole tree on every start);</item>
/// <item>otherwise it is kept as it is.</item>
/// </list>
/// Whatever was created or repaired is inspected again through a handle before it is used. The decision is pure and tested;
/// the file-system steps are behind <see cref="IRootOps"/>, so no test writes an ACL.
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

    /// <summary>What the folder is, read without following links. <see cref="Unreadable"/>: why its security could not be read.</summary>
    public sealed record Facts(bool Exists, bool IsReparsePoint = false, bool IsDirectory = true, string? OwnerSid = null,
        bool DaclProtected = false, IReadOnlyList<Ace>? Aces = null, string? Unreadable = null);

    public enum Verdict { Create, Keep, Repair, MoveAside }

    public sealed record Decision(Verdict Verdict, string Reason);

    public static bool Trusted(string sid) => sid is Administrators or LocalSystem or TrustedInstaller;

    /// <summary>Pure: what to do with the root as it is (<paramref name="expected"/>: PoolWatch's own entries, from <see cref="Expected"/>).</summary>
    public static Decision Decide(Facts f, IReadOnlyList<Ace> expected)
    {
        if (!f.Exists) return new(Verdict.Create, "it does not exist yet");
        // M2: moved aside (renamed through a handle opened on the link itself), never followed and never fatal.
        if (f.IsReparsePoint) return new(Verdict.MoveAside, "it is a link (a junction, symbolic link or mount point), not a folder");
        if (!f.IsDirectory) return new(Verdict.MoveAside, "it is a file, not a folder");
        if (f.Unreadable is { } why) return new(Verdict.MoveAside, $"its permissions cannot be read ({why})");
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

    public static IReadOnlyList<Ace> AcesOf(RawSecurityDescriptor sd) =>
        sd.DiscretionaryAcl is null ? [] : sd.DiscretionaryAcl.OfType<CommonAce>().Select(a => new Ace(
            a.SecurityIdentifier.Value, a.AceQualifier == AceQualifier.AccessAllowed, a.AccessMask, a.IsInherited,
            (a.AceFlags & AceFlags.InheritOnly) != 0,
            (int)(a.AceFlags & (AceFlags.ContainerInherit | AceFlags.ObjectInherit | AceFlags.NoPropagateInherit | AceFlags.InheritOnly)))).ToList();

    /// <summary>
    /// Makes the root safe to use (see the class summary) and returns what happened (for the log and the timeline). Throws
    /// <see cref="DataRootRefusedException"/> only when it cannot be made safe (it could not be moved aside, created or
    /// secured, or it kept changing while being checked): nothing is written then.
    /// </summary>
    public static List<string> Prepare(string root, SecurityIdentifier? reader, IRootOps ops, Func<DateTime> utcNow)
    {
        var notes = new List<string>();
        var expected = Expected(reader);
        var security = Interop.DataRootSecurity(reader);
        bool applied = false;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            var d = Decide(ops.Inspect(root), expected);
            switch (d.Verdict)
            {
                case Verdict.Keep:
                    return notes;
                case Verdict.Create:
                    int err = ops.CreateSecure(root, security);
                    if (err != 0 && err != NoFollow.ERROR_ALREADY_EXISTS)
                        throw new DataRootRefusedException($"PoolWatch could not create its data folder {root} (error {err}).");
                    break; // inspect again: what was created must check out (or someone else's, created meanwhile, is judged)
                case Verdict.Repair when applied:
                    // Already re-applied in this run and still not exact: only owner and writers matter for safety, and those
                    // checked out (Repair, not MoveAside). Keep it rather than refuse to start over a difference in detail.
                    notes.Add($"{root}: its permissions still differ in detail after PoolWatch applied its own; kept (only administrators can change it)");
                    return notes;
                case Verdict.Repair:
                    ops.ApplySecure(root, security);
                    applied = true;
                    notes.Add($"{root}: {d.Reason}; PoolWatch applied its own (once)");
                    break;
                case Verdict.MoveAside:
                    string moved = ops.MoveAside(root, utcNow());
                    notes.Add($"{root} was not safe to use ({d.Reason}): it was moved aside to {moved} and a new data folder created. " +
                              $"Anything PoolWatch had there is in {moved}.");
                    break;
            }
        }
        throw new DataRootRefusedException($"PoolWatch could not secure its data folder {root}: it kept changing while being checked.");
    }
}

/// <summary>The file-system steps behind <see cref="DataRootGuard.Prepare"/> (tests pass fakes: no test writes an ACL).</summary>
internal interface IRootOps
{
    DataRootGuard.Facts Inspect(string root);
    /// <summary>Creates the folder with this security at creation; 0, or the Win32 error (ERROR_ALREADY_EXISTS when it exists).</summary>
    int CreateSecure(string root, DirectorySecurity security);
    /// <summary>Applies the owner and the protected DACL through a handle opened without following links.</summary>
    void ApplySecure(string root, DirectorySecurity security);
    /// <summary>Renames the root through its own handle to PoolWatch.untrusted-&lt;UTC&gt; next to it; returns the new path.</summary>
    string MoveAside(string root, DateTime utcNow);
}

/// <summary>The real steps, every one through a handle opened with FILE_FLAG_OPEN_REPARSE_POINT.</summary>
internal sealed class WindowsRootOps : IRootOps
{
    public DataRootGuard.Facts Inspect(string root)
    {
        FileAttributes attrs;
        try { attrs = File.GetAttributes(root); } // read from the entry itself (a dangling link is still a link)
        catch (FileNotFoundException) { return new(false); }
        catch (DirectoryNotFoundException) { return new(false); }
        if ((attrs & FileAttributes.ReparsePoint) != 0) return new(true, IsReparsePoint: true);
        using var h = NoFollow.Open(root, dir: true, NoFollow.READ_CONTROL | NoFollow.FILE_READ_ATTRIBUTES, NoFollow.ShareAll);
        if (h.IsInvalid)
        {
            int e = Marshal.GetLastWin32Error();
            if (e is NoFollow.ERROR_FILE_NOT_FOUND or NoFollow.ERROR_PATH_NOT_FOUND) return new(false);
            return new(true, IsDirectory: (attrs & FileAttributes.Directory) != 0, Unreadable: $"error {e}");
        }
        var q = NoFollow.Query(h);
        if ((q.Attributes & FileAttributes.ReparsePoint) != 0) return new(true, IsReparsePoint: true);
        bool isDir = (q.Attributes & FileAttributes.Directory) != 0;
        if (NoFollow.Security(h) is not { } sd) return new(true, IsDirectory: isDir, Unreadable: $"error {Marshal.GetLastWin32Error()}");
        return new(true, false, isDir, sd.Owner?.Value, (sd.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0, DataRootGuard.AcesOf(sd));
    }

    public int CreateSecure(string root, DirectorySecurity security) => NoFollow.CreateDirectoryWithSecurity(root, security);

    public void ApplySecure(string root, DirectorySecurity security)
    {
        using var h = NoFollow.Open(root, dir: true, NoFollow.WRITE_DAC | NoFollow.WRITE_OWNER | NoFollow.READ_CONTROL | NoFollow.FILE_READ_ATTRIBUTES,
            NoFollow.FILE_SHARE_READ | NoFollow.FILE_SHARE_WRITE);
        if (h.IsInvalid) throw new DataRootRefusedException($"PoolWatch could not open its data folder {root} to secure it (error {Marshal.GetLastWin32Error()}).");
        if ((NoFollow.Query(h).Attributes & FileAttributes.ReparsePoint) != 0) throw new DataRootRefusedException($"{root} became a link while it was secured.");
        int err = NoFollow.SetOwnerAndProtectedDacl(h, security);
        if (err != 0) throw new DataRootRefusedException($"PoolWatch could not secure its data folder {root} (error {err}).");
    }

    public string MoveAside(string root, DateTime utcNow)
    {
        string parent = Path.GetDirectoryName(Path.GetFullPath(root).TrimEnd('\\'))!;
        string name = Path.GetFileName(Path.GetFullPath(root).TrimEnd('\\'));
        // B1: open asking for DELETE | FILE_READ_ATTRIBUTES and NOT SYNCHRONIZE, so even a DACL that denies the running
        // principal cannot block the open — those two rights come from the parent folder (FILE_DELETE_CHILD,
        // FILE_LIST_DIRECTORY), and OpenForMove falls back to the backup / restore privileges for that one open (then turns
        // them off again) if that is not enough. The old open asked for SYNCHRONIZE, which such a DACL does not grant and
        // the parent cannot supply.
        using var h = NoFollow.OpenForMove(root);
        if (h.IsInvalid) throw new DataRootRefusedException($"PoolWatch could not move {root} aside (error {Marshal.GetLastWin32Error()}).");
        string stamp = utcNow.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        foreach (string target in AsideNames(parent, name, stamp))
            if (NoFollow.Rename(h, target)) return target;
        throw new DataRootRefusedException($"PoolWatch could not move {root} aside (error {Marshal.GetLastWin32Error()}).");
    }

    /// <summary>
    /// The names a root is moved aside to, in order: <c>name.untrusted-stamp</c>, then <c>-2</c> to <c>-9</c>, then two
    /// random ones (M2: a standard user who pre-creates every predictable name cannot make the move fail, and with it the
    /// start).
    /// </summary>
    internal static IEnumerable<string> AsideNames(string parent, string name, string stamp)
    {
        for (int i = 1; i <= 9; i++) yield return Path.Combine(parent, $"{name}.untrusted-{stamp}{(i == 1 ? "" : "-" + i)}");
        for (int i = 0; i < 2; i++) yield return Path.Combine(parent, $"{name}.untrusted-{stamp}-{Guid.NewGuid():N}");
    }
}
