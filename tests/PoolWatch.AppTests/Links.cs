namespace PoolWatch.AppTests;

/// <summary>Plants junctions and hard links in the test's TEMP folder with <c>cmd /c mklink</c>; removes a link entry only.</summary>
internal static class Links
{
    public static void Junction(string link, string target) => Mklink("/J", link, target);
    public static void HardLink(string link, string target) => Mklink("/H", link, target);

    private static void Mklink(string kind, string link, string target)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "/d", "/c", "mklink", kind, link, target }) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("cmd.exe did not start");
        var err = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(30_000)) { try { p.Kill(); } catch { } throw new TimeoutException("mklink did not finish"); }
        if (p.ExitCode != 0 || !(Directory.Exists(link) || File.Exists(link)))
            throw new InvalidOperationException($"mklink {kind} failed (exit {p.ExitCode}): {output} {err.Result}");
    }

    /// <summary>
    /// Makes a file of the test's own a reparse point, as a symbolic link to a file is, but with a tag no filter handles
    /// (FSCTL_SET_REPARSE_POINT, a third-party tag and GUID, no data): a symbolic link needs a privilege the test may not
    /// hold, this needs only write access to the file. Nothing is followed through it. Throws when Windows refuses.
    /// </summary>
    public static void MarkAsReparsePoint(string file)
    {
        using var h = File.OpenHandle(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var buffer = new byte[24]; // REPARSE_GUID_DATA_BUFFER: tag, data length 0, reserved, GUID
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), 0x00000777u);
        Guid.NewGuid().TryWriteBytes(buffer.AsSpan(8, 16));
        if (!DeviceIoControl(h, 0x000900A4 /* FSCTL_SET_REPARSE_POINT */, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint code, byte[] input, int inputSize, IntPtr output,
        int outputSize, out int returned, IntPtr overlapped);

    /// <summary>Removes the link entry itself (non-recursive), never what it points at. True when nothing is left there.</summary>
    public static bool DeleteLinkOnly(string link)
    {
        try
        {
            if (Directory.Exists(link) || File.Exists(link))
            {
                if (!File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint)) return false;
                Directory.Delete(link, recursive: false);
            }
        }
        catch { }
        return !Directory.Exists(link) && !File.Exists(link);
    }
}
