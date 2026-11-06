using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PoolWatch.Engine;

/// <summary>Opens a file, used where a site lets a test stand in its own opener (the default is <see cref="NoWaitOpen.Open"/>).</summary>
internal delegate FileStream FileOpen(string path, FileMode mode, FileAccess access, FileShare share, bool appendOnly);

/// <summary>
/// Opens a file the way <see cref="FileStream"/> would, with one difference: an open that would have to wait for
/// another program before it could go ahead is given up at once, and is reported as refused — the
/// <see cref="IOException"/> a sharing refusal raises (<c>ERROR_SHARING_VIOLATION</c>), so a caller that already
/// handles a refused open handles this too, on the same path.
///
/// A real refusal (another handle's share mode or a byte-range lock), a missing file or folder, a denied access, a
/// folder where a file was asked for and a create-new open of a name that is there are reported with the exception
/// type and <see cref="Exception.HResult"/> that <see cref="FileStream"/> gives them. Any other failure is an
/// <see cref="IOException"/> whose HResult holds the Windows error code.
///
/// The one Windows feature this needs is named only in the interop declarations below.
/// </summary>
internal static class NoWaitOpen
{
    /// <summary>
    /// Opens <paramref name="path"/> like <c>new FileStream(path, mode, access, share)</c>, never waiting for another
    /// program: an open that would wait is given up and reported as a refusal (an <see cref="IOException"/> whose
    /// <see cref="Exception.HResult"/> is <c>ERROR_SHARING_VIOLATION</c>). With <paramref name="appendOnly"/> the handle
    /// is given append rights only (as PoolWatch's log line is), so Windows writes at the true end of the file whatever
    /// another writer did since the open and the handle can never overwrite earlier bytes.
    /// </summary>
    public static FileStream Open(string path, FileMode mode, FileAccess access, FileShare share, bool appendOnly = false) =>
        OpenStream(path, mode, access, share, appendOnly, bufferSize: 4096);

    /// <summary>
    /// <see cref="Open"/>, with no buffer in the stream: each write goes to Windows as it is made, so a write that fails
    /// leaves nothing behind that a later flush or close would write. The daily CSV files are opened so, as each of their
    /// writes is a whole batch of rows that is written again elsewhere when it fails.
    /// </summary>
    public static FileStream OpenUnbuffered(string path, FileMode mode, FileAccess access, FileShare share) =>
        OpenStream(path, mode, access, share, appendOnly: false, bufferSize: 0);

    private static FileStream OpenStream(string path, FileMode mode, FileAccess access, FileShare share, bool appendOnly, int bufferSize)
    {
        // FileMode.Append, like append-only, asks Windows for append rights so the write lands at the true end of the
        // file whatever another writer did since the open, and the handle can never overwrite earlier bytes.
        bool append = appendOnly || mode == FileMode.Append;
        uint desired = append
            ? FILE_APPEND_DATA | FILE_READ_ATTRIBUTES | SYNCHRONIZE
            : access switch
            {
                FileAccess.Read => FILE_GENERIC_READ,
                FileAccess.Write => FILE_GENERIC_WRITE,
                _ => FILE_GENERIC_READ | FILE_GENERIC_WRITE,
            };
        var handle = OpenHandle(path, Disposition(mode), desired, Share(share), FILE_NON_DIRECTORY_FILE);
        var fileAccess = append ? FileAccess.Write : access;
        try { return new FileStream(handle, fileAccess, bufferSize); }
        catch { handle.Dispose(); throw; }
    }

    /// <summary>
    /// Appends <paramref name="text"/> to the end of <paramref name="path"/> (made if it is not there) as UTF-8,
    /// sharing reading, writing and deleting, never waiting for another program (<see cref="Open"/>). A refused append
    /// throws as <see cref="File.AppendAllText(string, string?)"/> would.
    /// </summary>
    public static void AppendAllText(string path, string text)
    {
        using var stream = Open(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using var writer = new StreamWriter(stream, Utf8NoBom);
        writer.Write(text);
    }

    /// <summary>
    /// Reads the whole of a small file as UTF-8, never waiting for another program (<see cref="Open"/>). A refused or
    /// missing read throws as <see cref="File.ReadAllText(string)"/> would, so a caller's existing catch handles it.
    /// </summary>
    public static string ReadAllText(string path)
    {
        using var stream = Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Writes <paramref name="text"/> as UTF-8 (no byte-order mark) to <paramref name="path"/>, replacing what is there,
    /// never waiting for another program (<see cref="Open"/>). For a file that must survive a power cut mid-write, use
    /// <see cref="DurableFile.Replace"/> instead; this is for the records that are rewritten whole each time and read
    /// again after a restart.
    /// </summary>
    public static void WriteAllText(string path, string text)
    {
        using var stream = Open(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, Utf8NoBom);
        writer.Write(text);
    }

    private static readonly System.Text.Encoding Utf8NoBom = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Throws the refusal <see cref="Open"/> throws when an open of <paramref name="path"/> would have to wait for another
    /// program, and returns quietly otherwise. A path that is not there cannot be waited on, so it returns.
    /// Used right before a rename that replaces <paramref name="path"/>: a file whose open would have to wait at that
    /// time is not renamed over.
    /// </summary>
    public static void FailIfOpenWouldWait(string path)
    {
        var handle = TryOpenHandle(path, FILE_OPEN, DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE, ShareAll, FILE_NON_DIRECTORY_FILE, out int status);
        using (handle)
            if (status == StatusGivenUp) throw Refused(path);
        // Every other result — opened cleanly, nothing there, a real sharing or lock refusal, access denied, a folder —
        // is left for the real write or rename to carry out or report as it always did. Only an open that WOULD have to
        // wait for another program is given up here.
    }

    // --- the open itself -------------------------------------------------------------------------------------------

    private static SafeFileHandle OpenHandle(string path, uint disposition, uint access, uint share, uint extraOptions)
    {
        var handle = TryOpenHandle(path, disposition, access, share, extraOptions, out int status);
        if (status == STATUS_SUCCESS) return handle;
        handle.Dispose(); // an alternate-success open returns a handle we must not do any I/O on, so close it unused
        if (status == StatusGivenUp) throw Refused(path);
        throw ForStatus(status, path);
    }

    /// <summary>
    /// The one open. Asks Windows to complete at once rather than wait for another program; <paramref name="status"/>
    /// says whether the open was given up that way. The handle is open when the open succeeded or was given up (it is then
    /// closed unused, with no I/O on it), and invalid otherwise.
    /// </summary>
    private static SafeFileHandle TryOpenHandle(string path, uint disposition, uint access, uint share, uint extraOptions, out int status)
    {
        string full = Path.GetFullPath(path);
        string nt = full.StartsWith(@"\\?\", StringComparison.Ordinal) ? @"\??\" + full[4..]
            : full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\??\UNC\" + full[2..]
            : @"\??\" + full;
        if (nt.Length > MaxNtNameChars) { status = unchecked((int)0xC0000106); return new SafeFileHandle(IntPtr.Zero, ownsHandle: false); } // STATUS_NAME_TOO_LONG
        IntPtr name = Marshal.StringToHGlobalUni(nt);
        IntPtr unicode = Marshal.AllocHGlobal(Marshal.SizeOf<UNICODE_STRING>());
        try
        {
            Marshal.StructureToPtr(new UNICODE_STRING
            {
                Length = checked((ushort)(nt.Length * 2)),
                MaximumLength = checked((ushort)(nt.Length * 2 + 2)),
                Buffer = name,
            }, unicode, false);
            var attributes = new OBJECT_ATTRIBUTES { Length = Marshal.SizeOf<OBJECT_ATTRIBUTES>(), ObjectName = unicode, Attributes = OBJ_CASE_INSENSITIVE };
            uint options = FILE_SYNCHRONOUS_IO_NONALERT | GiveUpRatherThanWait | extraOptions;
            status = NtCreateFile(out IntPtr raw, access, ref attributes, out IO_STATUS_BLOCK _, IntPtr.Zero, FILE_ATTRIBUTE_NORMAL, share, disposition, options, IntPtr.Zero, 0);
            return new SafeFileHandle(raw, ownsHandle: true);
        }
        finally
        {
            Marshal.FreeHGlobal(unicode);
            Marshal.FreeHGlobal(name);
        }
    }

    private static uint Disposition(FileMode mode) => mode switch
    {
        FileMode.CreateNew => FILE_CREATE,
        FileMode.Create => FILE_OVERWRITE_IF,
        FileMode.Open => FILE_OPEN,
        FileMode.OpenOrCreate => FILE_OPEN_IF,
        FileMode.Truncate => FILE_OVERWRITE,
        FileMode.Append => FILE_OPEN_IF,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static uint Share(FileShare share)
    {
        uint s = 0;
        if (share.HasFlag(FileShare.Read)) s |= FILE_SHARE_READ;
        if (share.HasFlag(FileShare.Write)) s |= FILE_SHARE_WRITE;
        if (share.HasFlag(FileShare.Delete)) s |= FILE_SHARE_DELETE;
        return s;
    }

    private static IOException Refused(string path) =>
        new($"'{path}' is open in another program in a way that would make this open wait; it was given up.") { HResult = ERROR_SHARING_VIOLATION };

    private static Exception ForStatus(int status, string path)
    {
        // CreateFileW reports a create-new open of a name that is there as ERROR_FILE_EXISTS; the general mapping of that
        // status gives ERROR_ALREADY_EXISTS, so it is mapped here as CreateFileW does.
        int win32 = status == STATUS_OBJECT_NAME_COLLISION ? ErrorFileExists : RtlNtStatusToDosError(status);
        int hr = unchecked((int)0x80070000) | (win32 & 0xFFFF);
        return win32 switch
        {
            ErrorFileNotFound => new FileNotFoundException($"Could not find file '{path}'.", path),
            ErrorPathNotFound => new DirectoryNotFoundException($"Could not find a part of the path '{path}'."),
            ErrorAccessDenied => new UnauthorizedAccessException($"Access to the path '{path}' is denied."),
            _ => new IOException(new Win32Exception(win32).Message, hr),
        };
    }

    // --- interop ---------------------------------------------------------------------------------------------------

    private const int STATUS_SUCCESS = 0;
    /// <summary>The open completed at once instead of waiting for another program (<see cref="StatusGivenUp"/>).</summary>
    private const int STATUS_OPLOCK_BREAK_IN_PROGRESS = 0x00000108, StatusGivenUp = STATUS_OPLOCK_BREAK_IN_PROGRESS;
    private const int STATUS_OBJECT_NAME_COLLISION = unchecked((int)0xC0000035);

    private const uint FILE_READ_DATA = 0x1, FILE_WRITE_DATA = 0x2, FILE_APPEND_DATA = 0x4, FILE_READ_EA = 0x8, FILE_WRITE_EA = 0x10,
        FILE_READ_ATTRIBUTES = 0x80, FILE_WRITE_ATTRIBUTES = 0x100, DELETE = 0x10000, READ_CONTROL = 0x20000, SYNCHRONIZE = 0x100000;
    private const uint FILE_GENERIC_READ = FILE_READ_DATA | FILE_READ_ATTRIBUTES | FILE_READ_EA | READ_CONTROL | SYNCHRONIZE;
    private const uint FILE_GENERIC_WRITE = FILE_WRITE_DATA | FILE_APPEND_DATA | FILE_WRITE_ATTRIBUTES | FILE_WRITE_EA | READ_CONTROL | SYNCHRONIZE;

    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4, ShareAll = 7;

    // CreateDisposition values (NtCreateFile), the NT-layer equivalents of FileMode.
    private const uint FILE_OPEN = 1, FILE_CREATE = 2, FILE_OPEN_IF = 3, FILE_OVERWRITE = 4, FILE_OVERWRITE_IF = 5;

    // CreateOptions. FILE_COMPLETE_IF_OPLOCKED (GiveUpRatherThanWait in the code) is the one that makes the open give
    // up rather than wait.
    private const uint FILE_SYNCHRONOUS_IO_NONALERT = 0x20, FILE_NON_DIRECTORY_FILE = 0x40, FILE_COMPLETE_IF_OPLOCKED = 0x100,
        GiveUpRatherThanWait = FILE_COMPLETE_IF_OPLOCKED;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80, OBJ_CASE_INSENSITIVE = 0x40;

    private const int ErrorFileNotFound = 2, ErrorPathNotFound = 3, ErrorAccessDenied = 5, ErrorFileExists = 80;
    private const int ERROR_SHARING_VIOLATION = unchecked((int)0x80070020);

    private const int MaxNtNameChars = (ushort.MaxValue - 2) / 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING { public ushort Length, MaximumLength; public IntPtr Buffer; }

    [StructLayout(LayoutKind.Sequential)]
    private struct OBJECT_ATTRIBUTES { public int Length; public IntPtr RootDirectory, ObjectName; public uint Attributes; public IntPtr SecurityDescriptor, SecurityQualityOfService; }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_STATUS_BLOCK { public IntPtr Status, Information; }

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out IntPtr handle, uint access, ref OBJECT_ATTRIBUTES attributes, out IO_STATUS_BLOCK status,
        IntPtr allocationSize, uint fileAttributes, uint share, uint disposition, uint options, IntPtr eaBuffer, uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern int RtlNtStatusToDosError(int status);
}
