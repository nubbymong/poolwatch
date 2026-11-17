using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PoolWatch.Native;

/// <summary>
/// The opens the AppData link finder makes of paths an account can change. PoolWatch runs as administrator, and a link
/// there can lead to something that is not a file at all (a named pipe, whose server could act as the program that opened
/// it), so each open asks for identification only (SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION: a pipe's server
/// learns who opened it and can do nothing as them), reads attributes only, and its handle is used only once
/// <see cref="OnDisk"/> says it is a file or folder on a disk. A link's target is read through the handle that was checked
/// (FSCTL_GET_REPARSE_POINT), never by its path again.
/// </summary>
internal static class LinkNative
{
    private const uint FILE_READ_ATTRIBUTES = 0x80, FILE_SHARE_ALL = 7, OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000, FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint SECURITY_SQOS_PRESENT = 0x00100000, SECURITY_IDENTIFICATION = 0x00010000;
    private const uint FILE_TYPE_DISK = 1;
    private const uint FSCTL_GET_REPARSE_POINT = 0x000900A8;

    /// <summary>HANDLE CreateFileW(LPCWSTR, DWORD dwDesiredAccess, DWORD dwShareMode, LPSECURITY_ATTRIBUTES, DWORD dwCreationDisposition, DWORD dwFlagsAndAttributes, HANDLE hTemplateFile).</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    /// <summary>DWORD GetFileType(HANDLE hFile).</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle file);

    /// <summary>BOOL DeviceIoControl(HANDLE, DWORD dwIoControlCode, LPVOID lpInBuffer, DWORD nInBufferSize, LPVOID lpOutBuffer, DWORD nOutBufferSize, LPDWORD lpBytesReturned, LPOVERLAPPED).</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr inBuffer, uint inSize, [Out] byte[] outBuffer, uint outSize, out uint returned, IntPtr overlapped);

    /// <summary>
    /// Opens the entry at <paramref name="path"/> itself (<paramref name="follow"/> false: never what a link there points
    /// at) or where it leads (true), for its attributes only, as identification only. Check IsInvalid; the error is
    /// Marshal.GetLastWin32Error().
    /// </summary>
    public static SafeFileHandle Open(string path, bool follow) =>
        CreateFileW(path, FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | (follow ? 0 : FILE_FLAG_OPEN_REPARSE_POINT) | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, IntPtr.Zero);

    /// <summary>Whether the open handle is a file or folder on a disk (not a pipe, a console or another device).</summary>
    public static bool OnDisk(SafeFileHandle h) => GetFileType(h) == FILE_TYPE_DISK;

    /// <summary>The reparse data of the open entry (at most <see cref="Core.Links.ReparseData.MaxBytes"/>), or null when it cannot be read.</summary>
    public static byte[]? ReparseData(SafeFileHandle h)
    {
        var buffer = new byte[Core.Links.ReparseData.MaxBytes];
        return DeviceIoControl(h, FSCTL_GET_REPARSE_POINT, IntPtr.Zero, 0, buffer, (uint)buffer.Length, out uint returned, IntPtr.Zero) && returned <= buffer.Length
            ? buffer[..(int)returned]
            : null;
    }
}
