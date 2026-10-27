using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;

namespace PoolWatch.Engine;

/// <summary>
/// File-system operations that never follow a link (review rounds 2 and 3): every open uses FILE_FLAG_OPEN_REPARSE_POINT
/// (and FILE_FLAG_BACKUP_SEMANTICS for folders), and everything after it (facts, security, rename, delete) goes through
/// that handle, so what is checked is what is acted on. Used by the one-time migration and the data root guard.
/// </summary>
internal static class NoFollow
{
    public readonly record struct FileId(ulong Volume, ulong Low, ulong High);
    public readonly record struct Facts(FileAttributes Attributes, uint Links, long Length, FileId Id, DateTime LastWriteUtc);

    public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, DELETE = 0x00010000, READ_CONTROL = 0x00020000,
        WRITE_DAC = 0x00040000, WRITE_OWNER = 0x00080000, SYNCHRONIZE = 0x00100000, FILE_READ_ATTRIBUTES = 0x80;
    public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4, ShareAll = 7;
    public const uint CREATE_NEW = 1, OPEN_EXISTING = 3;
    public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000, FILE_FLAG_BACKUP_SEMANTICS = 0x02000000, FILE_ATTRIBUTE_NORMAL = 0x80,
        FILE_FLAG_OVERLAPPED = 0x40000000;
    public const int ERROR_FILE_NOT_FOUND = 2, ERROR_PATH_NOT_FOUND = 3, ERROR_ACCESS_DENIED = 5, ERROR_ALREADY_EXISTS = 183;

    /// <summary>Opens the entry itself (never what a link points at). Check IsInvalid; the error is Marshal.GetLastWin32Error().</summary>
    public static SafeFileHandle Open(string path, bool dir, uint access, uint share) =>
        CreateFileW(path, access, share, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT | (dir ? FILE_FLAG_BACKUP_SEMANTICS : 0), IntPtr.Zero);

    /// <summary>
    /// B1: opens the entry to rename it aside, asking for DELETE and FILE_READ_ATTRIBUTES only. FILE_FLAG_OVERLAPPED stops
    /// the Win32 layer adding SYNCHRONIZE (it adds that for a synchronous handle, and a DACL that shuts Administrators out
    /// does not grant it); the two rights it does ask for can be granted by the parent folder (FILE_DELETE_CHILD and
    /// FILE_LIST_DIRECTORY on <c>%ProgramData%</c>), so an elevated PoolWatch can still rename a child whose own
    /// permissions deny it. A rename is metadata only, so the async handle needs no real I/O. The link itself is opened
    /// (FILE_FLAG_OPEN_REPARSE_POINT), never followed; FILE_FLAG_BACKUP_SEMANTICS is needed to open a directory handle and
    /// is what engages SeBackupPrivilege / SeRestorePrivilege while they are enabled. On ACCESS_DENIED it enables those two
    /// privileges (an elevated administrator token holds both, disabled by default) for one more open only, and puts the
    /// token back as it was right after it, so the rest of the process never runs with them on. Check IsInvalid;
    /// Marshal.GetLastWin32Error() is the open's own error (R3), never one from the privilege calls.
    /// </summary>
    public static SafeFileHandle OpenForMove(string path)
    {
        var (h, error) = WithPrivilegeFallback(() => OpenForMoveOnce(path), RaiseBackupRestore, first => first.Dispose());
        Marshal.SetLastPInvokeError(error);
        return h;
    }

    /// <summary>
    /// The fallback's order, kept apart from the Win32 calls so it can be tested. Runs <paramref name="open"/>; only when
    /// that fails with ACCESS_DENIED does it raise the privileges (<paramref name="raise"/> returns null when it cannot, and
    /// then leaves the token as it found it), discard the first result, open once more and drop the privileges again —
    /// on every path, a throwing open included, before it returns. The error returned belongs to the result returned.
    /// </summary>
    internal static (T Result, int Error) WithPrivilegeFallback<T>(Func<(T Result, int Error)> open, Func<IDisposable?> raise, Action<T> discard)
    {
        var first = open();
        if (first.Error != ERROR_ACCESS_DENIED) return first;
        using IDisposable? raised = raise();
        if (raised is null) return first;
        discard(first.Result);
        return open();
    }

    private static (SafeFileHandle, int) OpenForMoveOnce(string path)
    {
        var h = CreateFileW(path, DELETE | FILE_READ_ATTRIBUTES, ShareAll, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        return (h, h.IsInvalid ? Marshal.GetLastWin32Error() : 0);
    }

    /// <summary>
    /// Enables SeBackupPrivilege and SeRestorePrivilege in the process token and returns a scope that, when disposed, puts
    /// back the previous state of each privilege this call changed (one that was already on is left on). Null when not both
    /// could be enabled (a standard token holds neither; AdjustTokenPrivileges then reports ERROR_NOT_ALL_ASSIGNED): any
    /// change made on the way is undone before it returns.
    /// </summary>
    private static IDisposable? RaiseBackupRestore()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr token)) return null;
        var changed = new List<TOKEN_PRIVILEGES>(2);
        bool raised = false;
        try
        {
            raised = Enable(token, "SeBackupPrivilege", changed) & Enable(token, "SeRestorePrivilege", changed);
            if (!raised) return null;
            var scope = new PrivilegeScope(token, changed);
            token = IntPtr.Zero; // the scope owns it now
            return scope;
        }
        finally
        {
            if (!raised) Restore(token, changed);
            if (token != IntPtr.Zero) CloseHandle(token);
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

    /// <summary>Creates a new file (fails if the name exists, a link included), never through a link.</summary>
    public static SafeFileHandle CreateNew(string path) =>
        CreateFileW(path, GENERIC_WRITE, 0, IntPtr.Zero, CREATE_NEW, FILE_FLAG_OPEN_REPARSE_POINT | FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);

    public static Facts Query(SafeFileHandle h)
    {
        if (!GetFileInformationByHandleEx(h, FileBasicInfo, out FILE_BASIC_INFO basic, Marshal.SizeOf<FILE_BASIC_INFO>()) ||
            !GetFileInformationByHandleEx(h, FileStandardInfo, out FILE_STANDARD_INFO standard, Marshal.SizeOf<FILE_STANDARD_INFO>()) ||
            !GetFileInformationByHandleEx(h, FileIdInfo, out FILE_ID_INFO id, Marshal.SizeOf<FILE_ID_INFO>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return new Facts((FileAttributes)basic.FileAttributes, standard.NumberOfLinks, standard.EndOfFile,
            new FileId(id.VolumeSerialNumber, id.IdLow, id.IdHigh), DateTime.FromFileTimeUtc(basic.LastWriteTime));
    }

    /// <summary>The handle's path now (normalized, DOS form, "\\?\" prefix removed); null when it cannot be read.</summary>
    public static string? FinalPath(SafeFileHandle h)
    {
        var buf = new char[1024];
        int n = GetFinalPathNameByHandleW(h, buf, buf.Length, 0 /* FILE_NAME_NORMALIZED | VOLUME_NAME_DOS */);
        if (n <= 0 || n >= buf.Length) return null;
        var p = new string(buf, 0, n);
        return p.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + p[8..] : p.StartsWith(@"\\?\", StringComparison.Ordinal) ? p[4..] : p;
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
        // FILE_RENAME_INFO (x64): ReplaceIfExists (4 with padding) | pad 4 | RootDirectory (8) | FileNameLength (4) | FileName.
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
    public static int SetOwnerAndProtectedDacl(SafeFileHandle h, ObjectSecurity security)
    {
        var raw = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        var owner = new byte[raw.Owner!.BinaryLength];
        raw.Owner.GetBinaryForm(owner, 0);
        var dacl = new byte[raw.DiscretionaryAcl!.BinaryLength];
        raw.DiscretionaryAcl.GetBinaryForm(dacl, 0);
        unsafe
        {
            fixed (byte* o = owner)
            fixed (byte* d = dacl)
                return SetSecurityInfo(h, SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION,
                    (IntPtr)o, IntPtr.Zero, (IntPtr)d, IntPtr.Zero);
        }
    }

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

    private const int FileBasicInfo = 0, FileStandardInfo = 1, FileRenameInfo = 3, FileDispositionInfo = 4, FileIdInfo = 18;
    private const int SE_FILE_OBJECT = 1;
    private const uint OWNER_SECURITY_INFORMATION = 1, DACL_SECURITY_INFORMATION = 4, PROTECTED_DACL_SECURITY_INFORMATION = 0x80000000;
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x20, TOKEN_QUERY = 0x8, SE_PRIVILEGE_ENABLED = 0x2;

    [StructLayout(LayoutKind.Sequential)] private struct FILE_BASIC_INFO { public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime; public uint FileAttributes; }
    [StructLayout(LayoutKind.Sequential)] private struct FILE_STANDARD_INFO { public long AllocationSize, EndOfFile; public uint NumberOfLinks; public byte DeletePending, Directory; }
    [StructLayout(LayoutKind.Sequential)] private struct FILE_ID_INFO { public ulong VolumeSerialNumber, IdLow, IdHigh; }
    [StructLayout(LayoutKind.Sequential)] private struct FILE_DISPOSITION_INFO { public byte DeleteFile; }
    [StructLayout(LayoutKind.Sequential)] private struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }
    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] private struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID Luid; public uint Attributes; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateDirectoryW(string path, ref SECURITY_ATTRIBUTES security);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int cls, out FILE_BASIC_INFO info, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int cls, out FILE_STANDARD_INFO info, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int cls, out FILE_ID_INFO info, int size);
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
