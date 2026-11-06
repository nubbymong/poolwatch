using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;

namespace PoolWatch.Engine;

/// <summary>
/// File-system operations that never follow a link: every open uses FILE_FLAG_OPEN_REPARSE_POINT
/// (and FILE_FLAG_BACKUP_SEMANTICS for folders), and everything after it (facts, security, rename, delete) goes through
/// that handle, so what is checked is what is acted on. Used by the data root guard, the desktop shortcut and every
/// other place that opens a path without following a link.
/// </summary>
internal static class NoFollow
{
    public readonly record struct FileId(ulong Volume, ulong Low, ulong High);
    public readonly record struct Facts(FileAttributes Attributes, uint Links, long Length, FileId Id, DateTime LastWriteUtc);

    public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, DELETE = 0x00010000, READ_CONTROL = 0x00020000,
        WRITE_DAC = 0x00040000, WRITE_OWNER = 0x00080000, SYNCHRONIZE = 0x00100000, FILE_READ_ATTRIBUTES = 0x80,
        FILE_ADD_FILE = 0x2, FILE_ADD_SUBDIRECTORY = 0x4;
    public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4, ShareAll = 7;
    public const uint CREATE_NEW = 1, OPEN_EXISTING = 3;
    public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000, FILE_FLAG_BACKUP_SEMANTICS = 0x02000000, FILE_ATTRIBUTE_NORMAL = 0x80,
        FILE_FLAG_OVERLAPPED = 0x40000000;
    public const int ERROR_FILE_NOT_FOUND = 2, ERROR_PATH_NOT_FOUND = 3, ERROR_ACCESS_DENIED = 5, ERROR_FILE_EXISTS = 80, ERROR_ALREADY_EXISTS = 183;

    /// <summary>Opens the entry itself (never what a link points at). Check IsInvalid; the error is Marshal.GetLastWin32Error().</summary>
    public static SafeFileHandle Open(string path, bool dir, uint access, uint share) =>
        CreateFileW(path, access, share, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT | (dir ? FILE_FLAG_BACKUP_SEMANTICS : 0), IntPtr.Zero);

    /// <summary>
    /// Opens the entry to rename it aside, asking for DELETE and FILE_READ_ATTRIBUTES only, through <see
    /// cref="OpenAsOwner"/>, which adds no SYNCHRONIZE (CreateFileW adds it to every open, and a DACL that shuts
    /// Administrators out does not grant it); the two rights it does ask for can be granted by the parent folder
    /// (FILE_DELETE_CHILD and FILE_LIST_DIRECTORY on <c>%ProgramData%</c>), so an elevated PoolWatch can still rename a
    /// child whose own permissions deny it. A rename is metadata only, so the handle needs no real I/O. The link itself is
    /// opened, never followed; backup intent is what engages SeBackupPrivilege / SeRestorePrivilege while they are
    /// enabled. On ACCESS_DENIED it enables those two
    /// privileges (an elevated administrator token holds both, disabled by default) for one more open only, and puts the
    /// token back as it was right after it, so the rest of the process never runs with them on. Check IsInvalid;
    /// Marshal.GetLastWin32Error() is the open's own error, never one from the privilege calls.
    /// </summary>
    public static SafeFileHandle OpenForMove(string path)
    {
        var (h, error, _) = WithPrivilegeFallback(() => OpenForMoveOnce(path), RaiseBackupRestore, first => first.Dispose());
        Marshal.SetLastPInvokeError(error);
        return h;
    }

    /// <summary>
    /// Opens a folder entry itself (never what a link points at) to read its owner and rules: READ_CONTROL and
    /// FILE_READ_ATTRIBUTES, as its owner may (<see cref="OpenAsOwner"/>), so a folder owned by Administrators is read by
    /// an elevated PoolWatch whatever its rules say. When that is denied (a folder another account, SYSTEM or
    /// TrustedInstaller owns whose rules shut administrators out), it enables SeBackupPrivilege alone (an elevated
    /// administrator token holds it, disabled by default) for one more open only, and puts the token back as it was right
    /// after it, as <see cref="OpenForMove"/> does. With backup intent, that privilege grants read access to
    /// the entry's owner and rules whatever they say, so an administrator can read who owns such a folder.
    /// <c>Privileged</c>: the handle returned came from that second open (it may still be invalid: a folder whose delete
    /// is pending, say). The error is the returned open's own.
    /// </summary>
    public static (SafeFileHandle Handle, int Error, bool Privileged) OpenToRead(string path) => OpenToRead(path, OpenToReadOnce, RaisePrivileges);

    /// <summary>The privileges <see cref="OpenToRead"/> turns on for its second open: the backup privilege alone.</summary>
    internal static readonly string[] ReadPrivileges = ["SeBackupPrivilege"];

    /// <summary>
    /// <see cref="OpenToRead(string)"/> with its open and its privilege step passed in (tests pass fakes): which
    /// privileges it asks for (<see cref="ReadPrivileges"/>) and what it returns after each open.
    /// </summary>
    internal static (SafeFileHandle Handle, int Error, bool Privileged) OpenToRead(string path, Func<string, (SafeFileHandle, int)> open,
        Func<string[], IDisposable?> raise)
    {
        var r = WithPrivilegeFallback(() => open(path), () => raise(ReadPrivileges), first => first.Dispose());
        Marshal.SetLastPInvokeError(r.Error);
        return r;
    }

    /// <summary>
    /// What the read open asks for: the entry's owner and rules (READ_CONTROL, an owner's right) and its attributes
    /// (FILE_READ_ATTRIBUTES, which the parent's FILE_LIST_DIRECTORY grants).
    /// </summary>
    internal const uint ReadAccess = READ_CONTROL | FILE_READ_ATTRIBUTES;

    private static (SafeFileHandle, int) OpenToReadOnce(string path)
    {
        return OpenAsOwner(path, ReadAccess, ShareAll);
    }

    /// <summary>
    /// The open options of <see cref="OpenAsOwner"/>: the entry itself, never what a link points at
    /// (FILE_OPEN_REPARSE_POINT), file or folder alike, with backup intent (FILE_OPEN_FOR_BACKUP_INTENT, which
    /// FILE_FLAG_BACKUP_SEMANTICS stands for: it lets an enabled backup or restore privilege grant the open), and no
    /// FILE_SYNCHRONOUS_IO_* option, so no SYNCHRONIZE is needed.
    /// </summary>
    internal const uint OwnerOpenOptions = FILE_OPEN_REPARSE_POINT | FILE_OPEN_FOR_BACKUP_INTENT;

    private const uint FILE_OPEN_REPARSE_POINT = 0x00200000, FILE_OPEN_FOR_BACKUP_INTENT = 0x00004000, OBJ_CASE_INSENSITIVE = 0x40;

    /// <summary>
    /// The longest NT name <see cref="OpenAsOwner"/> opens, in characters: a UNICODE_STRING counts its bytes in 16 bits,
    /// and its MaximumLength holds the name and its terminating null (2 bytes), so at most (65535 - 2) / 2.
    /// </summary>
    internal const int MaxNtNameChars = (ushort.MaxValue - 2) / 2;

    /// <summary>ERROR_FILENAME_EXCED_RANGE: the file name or extension is too long.</summary>
    internal const int ERROR_FILENAME_EXCED_RANGE = 206;

    /// <summary>
    /// Opens an entry itself (never what a link points at) asking Windows for <paramref name="access"/> and nothing more,
    /// through NtOpenFile (<see cref="OwnerOpenOptions"/>): the handle and the open's Win32 error (0 when it opened). The
    /// Win32 CreateFileW adds SYNCHRONIZE to every open, with FILE_FLAG_OVERLAPPED too (a test measures the rights each
    /// handle is given), and rules that shut administrators out do not grant it. What this open asks for, an owner of the
    /// entry is given whatever its rules say (READ_CONTROL and WRITE_DAC, unless the rules hold an entry for OWNER
    /// RIGHTS), and the parent folder can grant (FILE_READ_ATTRIBUTES through its FILE_LIST_DIRECTORY, DELETE through its
    /// FILE_DELETE_CHILD). The handle is used for the entry's metadata, security and name only, never for I/O. A path
    /// whose NT name is longer than a UNICODE_STRING can hold (<see cref="MaxNtNameChars"/>) is not opened at all
    /// (ERROR_FILENAME_EXCED_RANGE, and an invalid handle): its length would not fit the string's 16-bit count, which
    /// would then name a shorter path.
    /// </summary>
    internal static (SafeFileHandle Handle, int Error) OpenAsOwner(string path, uint access, uint share)
    {
        string full = Path.GetFullPath(path);
        string nt = full.StartsWith(@"\\?\", StringComparison.Ordinal) ? @"\??\" + full[4..]
            : full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\??\UNC\" + full[2..]
            : @"\??\" + full;
        if (nt.Length > MaxNtNameChars) return (new SafeFileHandle(IntPtr.Zero, ownsHandle: false), ERROR_FILENAME_EXCED_RANGE);
        var name = Marshal.StringToHGlobalUni(nt);
        var unicode = Marshal.AllocHGlobal(Marshal.SizeOf<UNICODE_STRING>());
        try
        {
            Marshal.StructureToPtr(new UNICODE_STRING { Length = checked((ushort)(nt.Length * 2)), MaximumLength = checked((ushort)(nt.Length * 2 + 2)), Buffer = name },
                unicode, false);
            var attributes = new OBJECT_ATTRIBUTES { Length = Marshal.SizeOf<OBJECT_ATTRIBUTES>(), ObjectName = unicode, Attributes = OBJ_CASE_INSENSITIVE };
            int status = NtOpenFile(out SafeFileHandle h, access, ref attributes, out _, share, OwnerOpenOptions);
            return status >= 0 ? (h, 0) : (h, RtlNtStatusToDosError(status));
        }
        finally
        {
            Marshal.FreeHGlobal(unicode);
            Marshal.FreeHGlobal(name);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length, MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OBJECT_ATTRIBUTES
    {
        public int Length;
        public IntPtr RootDirectory, ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor, SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_STATUS_BLOCK
    {
        public IntPtr Status, Information;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtOpenFile(out SafeFileHandle handle, uint access, ref OBJECT_ATTRIBUTES attributes, out IO_STATUS_BLOCK status, uint share, uint options);

    [DllImport("ntdll.dll")]
    private static extern int RtlNtStatusToDosError(int status);

    /// <summary>
    /// The fallback's order, kept apart from the Win32 calls so it can be tested. Runs <paramref name="open"/>; only when
    /// that fails with ACCESS_DENIED does it raise the privileges (<paramref name="raise"/> returns null when it cannot, and
    /// then leaves the token as it found it), discard the first result, open once more and drop the privileges again —
    /// on every path, a throwing open included, before it returns. The error returned belongs to the result returned;
    /// <c>Raised</c> says whether that result is the second open's, made with the privileges on.
    /// </summary>
    internal static (T Result, int Error, bool Raised) WithPrivilegeFallback<T>(Func<(T Result, int Error)> open, Func<IDisposable?> raise, Action<T> discard)
    {
        var first = open();
        if (first.Error != ERROR_ACCESS_DENIED) return (first.Result, first.Error, false);
        using IDisposable? raised = raise();
        if (raised is null) return (first.Result, first.Error, false);
        discard(first.Result);
        var second = open();
        return (second.Result, second.Error, true);
    }

    private static (SafeFileHandle, int) OpenForMoveOnce(string path) => OpenAsOwner(path, DELETE | FILE_READ_ATTRIBUTES, ShareAll);

    /// <summary>SeBackupPrivilege and SeRestorePrivilege (<see cref="RaisePrivileges"/>), for the move-aside open.</summary>
    private static IDisposable? RaiseBackupRestore() => RaisePrivileges("SeBackupPrivilege", "SeRestorePrivilege");

    /// <summary>
    /// Enables the privileges named in the process token and returns a scope that, when disposed, puts back the previous
    /// state of each privilege this call changed (one that was already on is left on). Null when not all could be enabled
    /// (a standard token holds none of these; AdjustTokenPrivileges then reports ERROR_NOT_ALL_ASSIGNED): any change made
    /// on the way is undone before it returns.
    /// </summary>
    private static IDisposable? RaisePrivileges(params string[] names)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr token)) return null;
        try
        {
            return RaiseAll<TOKEN_PRIVILEGES>(names, (name, changed) => Enable(token, name, changed), changed => Restore(token, changed),
                changed =>
                {
                    var scope = new PrivilegeScope(token, changed);
                    token = IntPtr.Zero; // the scope owns it now
                    return scope;
                });
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    /// <summary>
    /// The order <see cref="RaisePrivileges"/> keeps, kept apart from the token calls so it can be tested: each privilege
    /// is enabled in turn (<paramref name="enable"/> records each change it makes in the list it is given); when all are
    /// on, <paramref name="scope"/> takes the changes and is returned. Otherwise, a step that throws included, every
    /// change made on the way is put back (<paramref name="restore"/>) before it returns null or the exception goes on.
    /// </summary>
    internal static IDisposable? RaiseAll<T>(string[] names, Func<string, List<T>, bool> enable, Action<List<T>> restore,
        Func<List<T>, IDisposable> scope)
    {
        var changed = new List<T>(names.Length);
        bool handedOver = false;
        try
        {
            bool all = true;
            foreach (string name in names) all &= enable(name, changed); // each one is tried, as before
            if (!all) return null;
            var held = scope(changed);
            handedOver = true;
            return held;
        }
        finally
        {
            if (!handedOver) restore(changed);
        }
    }

    /// <summary>
    /// Enables one privilege. Whenever the token was actually changed, its previous state goes into <paramref name="changed"/>,
    /// even if the call then reports a failure, so it can always be put back. True only when the privilege is now on.
    /// </summary>
    private static bool Enable(IntPtr token, string name, List<TOKEN_PRIVILEGES> changed)
    {
        if (!LookupPrivilegeValue(null, name, out LUID luid)) return false;
        var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
        bool ok = AdjustTokenPrivileges(token, false, ref tp, Marshal.SizeOf<TOKEN_PRIVILEGES>(), out TOKEN_PRIVILEGES previous, out _);
        int error = Marshal.GetLastWin32Error();
        if (ok && MustRestore(previous.PrivilegeCount)) changed.Add(previous);
        return ok && error == 0;
    }

    /// <summary>
    /// Whether a privilege must be put back after the open: AdjustTokenPrivileges reports in PreviousState only the
    /// privileges it changed, so a count of 0 means it was already on (or not held) and is left alone.
    /// </summary>
    internal static bool MustRestore(uint previousCount) => previousCount > 0;

    private static void Restore(IntPtr token, List<TOKEN_PRIVILEGES> changed)
    {
        for (int i = changed.Count - 1; i >= 0; i--)
        {
            var previous = changed[i];
            AdjustTokenPrivileges(token, false, ref previous, 0, IntPtr.Zero, IntPtr.Zero);
        }
        changed.Clear();
    }

    private sealed class PrivilegeScope(IntPtr token, List<TOKEN_PRIVILEGES> changed) : IDisposable
    {
        private IntPtr _token = token;

        public void Dispose()
        {
            if (_token == IntPtr.Zero) return;
            Restore(_token, changed);
            CloseHandle(_token);
            _token = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Creates a new file (fails if the name exists, a link included; the error is then ERROR_FILE_EXISTS), never through
    /// a link. Add DELETE to <paramref name="access"/> to be able to remove it again through the handle.
    /// </summary>
    public static SafeFileHandle CreateNew(string path, uint access = GENERIC_WRITE) =>
        CreateFileW(path, access, 0, IntPtr.Zero, CREATE_NEW, FILE_FLAG_OPEN_REPARSE_POINT | FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);

    public static Facts Query(SafeFileHandle h)
    {
        if (!GetFileInformationByHandleEx(h, FileBasicInfo, out FILE_BASIC_INFO basic, Marshal.SizeOf<FILE_BASIC_INFO>()) ||
            !GetFileInformationByHandleEx(h, FileStandardInfo, out FILE_STANDARD_INFO standard, Marshal.SizeOf<FILE_STANDARD_INFO>()) ||
            !GetFileInformationByHandleEx(h, FileIdInfo, out FILE_ID_INFO id, Marshal.SizeOf<FILE_ID_INFO>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return new Facts((FileAttributes)basic.FileAttributes, standard.NumberOfLinks, standard.EndOfFile,
            new FileId(id.VolumeSerialNumber, id.IdLow, id.IdHigh), DateTime.FromFileTimeUtc(basic.LastWriteTime));
    }

    /// <summary>The reparse tag of the handle's entry (a link's kind), or 0 when it is not a reparse point.</summary>
    public static uint ReparseTag(SafeFileHandle h)
    {
        if (!GetFileInformationByHandleEx(h, FileAttributeTagInfo, out FILE_ATTRIBUTE_TAG_INFO info, Marshal.SizeOf<FILE_ATTRIBUTE_TAG_INFO>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return ((FileAttributes)info.FileAttributes & FileAttributes.ReparsePoint) != 0 ? info.ReparseTag : 0;
    }

    /// <summary>
    /// Whether a reparse tag is one of the cloud-files tags (IO_REPARSE_TAG_CLOUD to IO_REPARSE_TAG_CLOUD_F in winnt.h):
    /// what OneDrive puts on its folders, which hold their own files rather than point anywhere else.
    /// </summary>
    public static bool IsCloudFilesTag(uint tag) => (tag & ~IO_REPARSE_TAG_CLOUD_MASK) == IO_REPARSE_TAG_CLOUD;

    /// <summary>The handle's path now (normalized, DOS form, "\\?\" prefix removed); null when it cannot be read.</summary>
    public static string? FinalPath(SafeFileHandle h)
    {
        var buf = new char[1024];
        int n = GetFinalPathNameByHandleW(h, buf, buf.Length, 0 /* FILE_NAME_NORMALIZED | VOLUME_NAME_DOS */);
        if (n <= 0 || n >= buf.Length) return null;
        var p = new string(buf, 0, n);
        return p.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + p[8..] : p.StartsWith(@"\\?\", StringComparison.Ordinal) ? p[4..] : p;
    }

    /// <summary>
    /// Where a folder or file really is, every link on the way followed: the one open in this class that follows links,
    /// and it only reads the path it ends at (FILE_READ_ATTRIBUTES, nothing else). Null when it cannot be opened.
    /// </summary>
    public static string? ResolvedPath(string path, bool dir)
    {
        using var h = CreateFileW(path, FILE_READ_ATTRIBUTES, ShareAll, IntPtr.Zero, OPEN_EXISTING, dir ? FILE_FLAG_BACKUP_SEMANTICS : 0, IntPtr.Zero);
        return h.IsInvalid ? null : FinalPath(h);
    }

    public static bool SamePath(string? finalPath, string expected) =>
        finalPath is not null && finalPath.TrimEnd('\\').Equals(expected.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Marks the handle's entry for deletion when it closes (the handle needs DELETE). A non-empty folder fails.</summary>
    public static bool DeleteOnClose(SafeFileHandle h)
    {
        var disposition = new FILE_DISPOSITION_INFO { DeleteFile = 1 };
        return SetFileInformationByHandle(h, FileDispositionInfo, ref disposition, Marshal.SizeOf<FILE_DISPOSITION_INFO>());
    }

    /// <summary>Renames the handle's entry (the handle needs DELETE): a link is renamed itself, never its target. Fails if the new name exists.</summary>
    public static bool Rename(SafeFileHandle h, string newFullPath)
    {
        // FILE_RENAME_INFO (64-bit): ReplaceIfExists (4 with padding) | pad 4 | RootDirectory (8) | FileNameLength (4) | FileName.
        byte[] name = System.Text.Encoding.Unicode.GetBytes(newFullPath);
        int size = 20 + name.Length + 2;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            for (int i = 0; i < size; i++) Marshal.WriteByte(buf, i, 0);
            Marshal.WriteInt32(buf, 16, name.Length);
            Marshal.Copy(name, 0, buf + 20, name.Length);
            return SetFileInformationByHandle(h, FileRenameInfo, buf, size);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>The handle's owner and DACL (the handle needs READ_CONTROL); null when they cannot be read.</summary>
    public static RawSecurityDescriptor? Security(SafeFileHandle h)
    {
        int err = GetSecurityInfo(h, SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION, out _, out _, out _, out _, out IntPtr sd);
        if (err != 0) return null;
        try
        {
            int len = GetSecurityDescriptorLength(sd);
            var bytes = new byte[len];
            Marshal.Copy(sd, bytes, 0, len);
            return new RawSecurityDescriptor(bytes, 0);
        }
        finally { LocalFree(sd); }
    }

    /// <summary>Sets the owner and a protected DACL through the handle (WRITE_OWNER | WRITE_DAC). Windows propagates it to what is inside.</summary>
    public static int SetOwnerAndProtectedDacl(SafeFileHandle h, ObjectSecurity security) => SetSecurity(h, security, ownerToo: true);

    /// <summary>
    /// Sets the protected DACL alone through the handle (WRITE_DAC), the owner left as it is. Windows propagates it to what
    /// is inside.
    /// </summary>
    public static int SetProtectedDacl(SafeFileHandle h, ObjectSecurity security) => SetSecurity(h, security, ownerToo: false);

    private static int SetSecurity(SafeFileHandle h, ObjectSecurity security, bool ownerToo)
    {
        var raw = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        var owner = new byte[raw.Owner!.BinaryLength];
        raw.Owner.GetBinaryForm(owner, 0);
        var dacl = new byte[raw.DiscretionaryAcl!.BinaryLength];
        raw.DiscretionaryAcl.GetBinaryForm(dacl, 0);
        uint what = DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION | (ownerToo ? OWNER_SECURITY_INFORMATION : 0);
        unsafe
        {
            fixed (byte* o = owner)
            fixed (byte* d = dacl)
                return SetSecurityInfo(h, SE_FILE_OBJECT, what, ownerToo ? (IntPtr)o : IntPtr.Zero, IntPtr.Zero, (IntPtr)d, IntPtr.Zero);
        }
    }

    /// <summary>
    /// Which folder a path names now: its volume and file ID, read through a handle on the entry itself (never what a link
    /// points at). Null when it cannot be opened or read, or when it is a link or a file. Two folders made one after the
    /// other at the same name have different IDs.
    /// </summary>
    public static string? IdentityOf(string path)
    {
        using var h = Open(path, dir: true, FILE_READ_ATTRIBUTES, ShareAll);
        return h.IsInvalid ? null : FolderIdentity(h);
    }

    /// <summary>The identity <see cref="IdentityOf"/> gives, read through an open handle; null for a link, a file or a failed read.</summary>
    private static string? FolderIdentity(SafeFileHandle h)
    {
        try
        {
            var q = Query(h);
            if ((q.Attributes & FileAttributes.ReparsePoint) != 0 || (q.Attributes & FileAttributes.Directory) == 0) return null;
            return $"{q.Id.Volume:X16}-{q.Id.High:X16}{q.Id.Low:X16}";
        }
        catch (Win32Exception) { return null; }
    }

    /// <summary>FILE_LIST_DIRECTORY: an access that takes part in sharing, so the handle's share mode binds every later open.</summary>
    public const uint FILE_LIST_DIRECTORY = 0x1;

    /// <summary>
    /// Holds the folder at <paramref name="path"/> open (the entry itself, never what a link points at) without sharing
    /// delete: while the handle is open Windows lets no one rename or delete that folder, as either needs an open for
    /// delete access, which that share mode refuses (CreateFileW, "Creating and Opening Files"). Reading and writing in
    /// the folder, and opening it to read or write while sharing reading, stay as they were; while it is held, an open of
    /// the folder itself to read or write that shares no reading is refused, as the hold reads it (FILE_LIST_DIRECTORY).
    /// Returned only when the folder held is <paramref name="identity"/> (<see cref="IdentityOf"/>), read through that
    /// very handle; null when it cannot be opened (another program has it open in a way that refuses this one: one that
    /// keeps it from being read, or one open for delete), or when it is another folder, a link or a file.
    /// </summary>
    public static SafeFileHandle? HoldFolder(string path, string identity)
    {
        var h = Open(path, dir: true, FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE);
        if (!h.IsInvalid && FolderIdentity(h) == identity) return h;
        h.Dispose();
        return null;
    }

    /// <summary>
    /// Creates one folder inside one that is there (never a folder above it: ERROR_PATH_NOT_FOUND when the one above is
    /// missing), with the permissions it inherits there. 0, or the Win32 error (ERROR_ALREADY_EXISTS when it exists).
    /// </summary>
    public static int CreateDirectoryOnly(string path) => CreateDirectoryW(path, IntPtr.Zero) ? 0 : Marshal.GetLastWin32Error();

    /// <summary>Creates a folder with its security descriptor applied at creation (no window with inherited permissions). 0, or the Win32 error.</summary>
    public static int CreateDirectoryWithSecurity(string path, ObjectSecurity security)
    {
        byte[] sd = security.GetSecurityDescriptorBinaryForm();
        unsafe
        {
            fixed (byte* p = sd)
            {
                var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = (IntPtr)p, bInheritHandle = 0 };
                return CreateDirectoryW(path, ref sa) ? 0 : Marshal.GetLastWin32Error();
            }
        }
    }

    private const int FileBasicInfo = 0, FileStandardInfo = 1, FileRenameInfo = 3, FileDispositionInfo = 4, FileAttributeTagInfo = 9, FileIdInfo = 18;
    private const uint IO_REPARSE_TAG_CLOUD = 0x9000001A, IO_REPARSE_TAG_CLOUD_MASK = 0x0000F000;
    private const int SE_FILE_OBJECT = 1;
    private const uint OWNER_SECURITY_INFORMATION = 1, DACL_SECURITY_INFORMATION = 4, PROTECTED_DACL_SECURITY_INFORMATION = 0x80000000;
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x20, TOKEN_QUERY = 0x8, SE_PRIVILEGE_ENABLED = 0x2;

    [StructLayout(LayoutKind.Sequential)] private struct FILE_BASIC_INFO { public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime; public uint FileAttributes; }
    [StructLayout(LayoutKind.Sequential)] private struct FILE_STANDARD_INFO { public long AllocationSize, EndOfFile; public uint NumberOfLinks; public byte DeletePending, Directory; }
    [StructLayout(LayoutKind.Sequential)] private struct FILE_ID_INFO { public ulong VolumeSerialNumber, IdLow, IdHigh; }
    [StructLayout(LayoutKind.Sequential)] private struct FILE_ATTRIBUTE_TAG_INFO { public uint FileAttributes, ReparseTag; }
    [StructLayout(LayoutKind.Sequential)] private struct FILE_DISPOSITION_INFO { public byte DeleteFile; }
    [StructLayout(LayoutKind.Sequential)] private struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }
    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] private struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID Luid; public uint Attributes; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateDirectoryW(string path, ref SECURITY_ATTRIBUTES security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateDirectoryW(string path, IntPtr security);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int cls, out FILE_BASIC_INFO info, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int cls, out FILE_STANDARD_INFO info, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int cls, out FILE_ID_INFO info, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int cls, out FILE_ATTRIBUTE_TAG_INFO info, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle h, int cls, ref FILE_DISPOSITION_INFO info, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle h, int cls, IntPtr info, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetFinalPathNameByHandleW(SafeFileHandle h, [Out] char[] path, int size, int flags);
    [DllImport("advapi32.dll")]
    private static extern int GetSecurityInfo(SafeFileHandle h, int objectType, uint info, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr sd);
    [DllImport("advapi32.dll")]
    private static extern int SetSecurityInfo(SafeFileHandle h, int objectType, uint info, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
    [DllImport("advapi32.dll")] private static extern int GetSecurityDescriptorLength(IntPtr sd);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr p);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool LookupPrivilegeValue(string? system, string name, out LUID luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState, int bufferLength, IntPtr previous, IntPtr returnLength);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState, int bufferLength, out TOKEN_PRIVILEGES previous, out int returnLength);
}
