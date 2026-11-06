using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PoolWatch.Engine;

/// <summary>
/// A process opened only to read what it runs (PROCESS_QUERY_LIMITED_INFORMATION). While this handle is open the process
/// object stays, so its ID cannot be given to another process: what is read and what is ended is one process.
/// </summary>
internal interface IOpenedProcess : IDisposable
{
    /// <summary>The full path of its exe, read through this handle; null when it cannot be read.</summary>
    string? ImagePath();

    /// <summary>Its command line, read through this handle; null when it cannot be read.</summary>
    string? CommandLine();

    /// <summary>Ends it through a handle with terminate rights, opened by its ID while this handle keeps that ID its own. Null when it was ended, else why not.</summary>
    string? End();
}

/// <summary>Windows' processes, as <see cref="LeftoverHelpers"/> reads them (the tests pass a fake).</summary>
internal interface IProcessTable
{
    IReadOnlyList<int> Ids();

    /// <summary>The process, opened to read; null when it cannot be opened (it ended, or it is protected).</summary>
    IOpenedProcess? Open(int id);

    /// <summary>
    /// The identity of a file: its volume and its file ID. Every spelling of one file gives the same identity: its long
    /// path, its short (8.3) path or a mix, and a hard link to it. Null when the file cannot be read.
    /// </summary>
    FileIdentity? IdentityOf(string path);
}

internal readonly record struct FileIdentity(ulong Volume, ulong IdLow, ulong IdHigh);

/// <summary>
/// The setup's and the uninstaller's last step before they wait for PoolWatch's files to be free (<c>--stop-running</c>
/// and <c>--uninstall-cleanup</c>, once the running PoolWatch has exited and its one-instance mutex is held): ends every
/// token-leak test helper (<c>PoolWatch.exe --token-leak-test</c>) that still runs from this same PoolWatch.exe. Such a
/// helper holds no mutex and can outlive the PoolWatch that started it: the logon check's helper runs on by itself, and,
/// when that PoolWatch is gone, it may show its "Turn on" notice and wait for a click for as long as nobody clicks. While
/// it runs, its exe can be neither replaced nor removed, so a silent setup or uninstall would wait for it and then stop.
/// Its result is not wanted any more. After a setup, what its notice offers is still done: the test's marker makes the
/// next PoolWatch start, the upgraded one, turn the workaround on. After an uninstall there is no next start: PoolWatch
/// is being removed, and its offer goes with it.
/// <para>
/// Only a process whose exe is this very file (by file identity, so a short (8.3) spelling of the path or a hard link
/// counts too) and whose first argument is <c>--token-leak-test</c> is ended, through a handle opened while the first
/// one keeps its ID from being reused; never this process. Every other process that runs from this exe is left to the
/// setup's and the uninstaller's own wait, which ends such a helper too when nothing else of PoolWatch runs (an earlier
/// PoolWatch's own steps, which an upgrade runs, did not end it).
/// </para>
/// </summary>
internal static class LeftoverHelpers
{
    /// <summary>The helper's option, its first argument (TokenLeakGuard.RunHelper starts it so).</summary>
    public const string HelperOption = "--token-leak-test";

    /// <summary>Ends the leftover helpers; each one is logged. Returns how many were ended. Never throws: what it cannot do is logged.</summary>
    public static int End(IProcessTable table, string? ownExe, int ownId, Action<string> log)
    {
        int ended = 0;
        try
        {
            if (ownExe is null || table.IdentityOf(ownExe) is not { } own)
            {
                log("leftover helpers: this PoolWatch.exe could not be read, so none is looked for");
                return 0;
            }
            foreach (int id in table.Ids())
            {
                if (id == ownId) continue;
                using var process = table.Open(id);
                // The command line first: it rules out nearly every process without opening its exe.
                if (process is null || !IsHelper(process.CommandLine())) continue;
                if (process.ImagePath() is not { } image || table.IdentityOf(image) != own) continue;
                if (process.End() is { } why) log($"leftover helpers: the token-leak test helper (process {id}) could not be ended: {why}");
                else
                {
                    ended++;
                    log($"leftover helpers: ended the token-leak test helper (process {id}) that still ran from {image}");
                }
            }
        }
        catch (Exception ex) { log("leftover helpers: " + ex.Message); }
        return ended;
    }

    /// <summary>Whether a command line is the helper's: after the exe (quoted or not), its first argument is <see cref="HelperOption"/>.</summary>
    internal static bool IsHelper(string? commandLine)
    {
        // The rules of the installer's IsHelperLine: the program's name runs from a quote to the next quote, or up to the
        // first space or tab; only a space and a tab separate (a control character, a no-break or an ideographic space is
        // part of a word); a NUL ends the line. A test holds the two readers to the same answer for each line it lists.
        // Neither is Windows' own split (CommandLineToArgvW ends an unquoted program name at any control character).
        if (commandLine is null) return false;
        int end = commandLine.IndexOf('\0');
        if (end >= 0) commandLine = commandLine[..end];
        string line = commandLine.TrimStart(' ', '\t');
        int afterExe;
        if (line.StartsWith('"'))
        {
            int close = line.IndexOf('"', 1);
            if (close < 0) return false;
            afterExe = close + 1;
        }
        else
        {
            afterExe = line.IndexOfAny([' ', '\t']);
            if (afterExe < 0) return false;
        }
        string rest = line[afterExe..];
        if (rest.Length == 0 || rest[0] is not (' ' or '\t')) return false;
        rest = rest.TrimStart(' ', '\t');
        return rest.StartsWith(HelperOption, StringComparison.Ordinal) &&
               (rest.Length == HelperOption.Length || rest[HelperOption.Length] is ' ' or '\t');
    }
}

/// <summary>The real table: the process IDs Windows lists, each opened with PROCESS_QUERY_LIMITED_INFORMATION.</summary>
internal sealed class WindowsProcessTable : IProcessTable
{
    private const uint PROCESS_TERMINATE = 0x0001, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint FILE_READ_ATTRIBUTES = 0x80, FILE_SHARE_ALL = 7, OPEN_EXISTING = 3;
    private const int FileIdInfo = 18;

    public IReadOnlyList<int> Ids()
    {
        var all = Process.GetProcesses();
        try { return all.Select(p => p.Id).ToList(); }
        finally { foreach (var p in all) p.Dispose(); }
    }

    public IOpenedProcess? Open(int id)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, id);
        if (!h.IsInvalid) return new Opened(h, id);
        h.Dispose();
        return null;
    }

    public FileIdentity? IdentityOf(string path)
    {
        using var h = CreateFileW(path, FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;
        return GetFileInformationByHandleEx(h, FileIdInfo, out FILE_ID_INFO info, Marshal.SizeOf<FILE_ID_INFO>())
            ? new FileIdentity(info.VolumeSerialNumber, info.IdLow, info.IdHigh)
            : null;
    }

    private sealed class Opened(SafeProcessHandle h, int id) : IOpenedProcess
    {
        public string? ImagePath()
        {
            var buffer = new char[1024];
            int size = buffer.Length;
            return QueryFullProcessImageNameW(h, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
        }

        public string? CommandLine() => Collectors.ProcessDetails.GetCommandLine(h.DangerousGetHandle());

        public string? End()
        {
            // By ID, while h keeps that ID this process's: the new handle is to the same process.
            using var terminate = OpenProcess(PROCESS_TERMINATE, false, id);
            if (terminate.IsInvalid) return $"error {Marshal.GetLastPInvokeError()}";
            return TerminateProcess(terminate, 1) ? null : $"error {Marshal.GetLastPInvokeError()}";
        }

        public void Dispose() => h.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)] private struct FILE_ID_INFO { public ulong VolumeSerialNumber, IdLow, IdHigh; }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(SafeProcessHandle h, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle h, int flags, [Out] char[] buffer, ref int size);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int cls, out FILE_ID_INFO info, int size);
}
