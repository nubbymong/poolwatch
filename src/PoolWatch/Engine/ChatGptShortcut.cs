using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PoolWatch.Core.Changes;
using PoolWatch.Core.Util;
using PoolWatch.Engine.Changes;

namespace PoolWatch.Engine;

/// <summary>
/// The desktop shortcut "ChatGPT (no shader cache)": it starts the ChatGPT desktop app through the launcher script the
/// installer puts next to PoolWatch.exe, which runs as the user (a packaged app cannot be started from an elevated
/// process such as PoolWatch). PoolWatch is elevated and the desktop is a folder the user can change, so the shortcut is
/// built in the data folder, checked, and written only as a new file, never through a link; its SHA-256 goes into the
/// change records, so Undo and the uninstaller remove it only while it is unchanged.
/// </summary>
internal static class ChatGptShortcut
{
    public const string FileName = "ChatGPT (no shader cache).lnk", LauncherName = "chatgpt-no-shader-cache.ps1";
    private const string Description = "Starts ChatGPT without its GPU shader disk cache (made by PoolWatch)";
    private const string AlreadyThere = "A shortcut with that name is already on your desktop.";
    /// <summary>SW_SHOWMINNOACTIVE: PowerShell's console window starts minimised, so it barely shows before it hides.</summary>
    private const int ShowMinimised = 7;

    /// <summary>Where the shortcut goes, and the folder up to which every folder above it is checked for links.</summary>
    internal sealed record Folders(string Desktop, string Profile);

    /// <summary>The launcher next to PoolWatch.exe, or null (a zip install without it).</summary>
    public static string? Launcher()
    {
        string path = Path.Combine(AppContext.BaseDirectory, LauncherName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Writes the shortcut on the user's desktop and records it for Undo. Never replaces an existing file. The icon is
    /// <paramref name="iconPath"/> (ChatGPT.exe in its package) when it exists, else PowerShell's.
    /// </summary>
    public static (bool Ok, string Message) Create(AppPaths paths, ChangeRecords records, string? iconPath, string via) =>
        Launcher() is { } launcher
            ? Create(paths, launcher, iconPath, via, new Folders(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), records.Put)
            : (false, "The ChatGPT launcher is not installed. Install PoolWatch with its setup program to get it.");

    /// <summary>The same, with the launcher, the folders and where the record goes passed in (the tests: TEMP folders, a list).</summary>
    internal static (bool Ok, string Message) Create(AppPaths paths, string launcher, string? iconPath, string via, Folders folders, Action<ChangeRecord> record)
    {
        if (folders.Desktop.Length == 0 || !Directory.Exists(folders.Desktop)) return (false, "PoolWatch could not find your desktop folder.");
        string path = Path.Combine(Path.GetFullPath(folders.Desktop), FileName);
        if (File.Exists(path) || Directory.Exists(path)) return (false, AlreadyThere);

        string powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        string arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{launcher}\"";
        byte[] bytes;
        try { bytes = Build(paths, powershell, arguments, Path.GetDirectoryName(launcher)!, iconPath is not null && File.Exists(iconPath) ? iconPath : powershell); }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException)
        {
            return (false, "PoolWatch could not make the shortcut: " + ex.Message);
        }
        // What the shell wrote is read back with PoolWatch's own parser: it must start exactly what was asked for.
        if (ShellLink.Read(bytes) is not { } made || !made.Target.Equals(powershell, StringComparison.OrdinalIgnoreCase) || made.Arguments != arguments)
            return (false, "PoolWatch could not make the shortcut as intended, so it wrote nothing.");

        if (FirstLink(folders.Desktop, folders.Profile) is { } link)
            return (false, $"PoolWatch did not write the shortcut: {link} is a link to another place.");
        using var file = NoFollow.CreateNew(path, NoFollow.GENERIC_WRITE | NoFollow.DELETE);
        if (file.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            return (false, error is NoFollow.ERROR_FILE_EXISTS or NoFollow.ERROR_ALREADY_EXISTS ? AlreadyThere : $"PoolWatch could not write the shortcut (error {error}).");
        }
        try
        {
            // A folder swapped for a link after the check above would put the new file somewhere else: refuse it then.
            if (!NoFollow.SamePath(NoFollow.FinalPath(file), path)) throw new IOException("your desktop folder moved while it was written");
            RandomAccess.Write(file, bytes, 0);
            record(new ChangeRecord
            {
                Id = ChangeIds.Shortcut(path), Kind = RecordKind.Shortcut, ChangedUtc = DateTime.UtcNow, Via = via,
                Name = FileName, Path = path, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            });
        }
        catch (Exception ex)
        {
            string failed = "PoolWatch could not write the shortcut: " + ex.Message;
            // Never a shortcut that Undo does not know about: removed through its handle, or the user is told to remove it.
            return (false, NoFollow.DeleteOnClose(file) ? failed
                : $"{failed.TrimEnd('.')}. It is still on your desktop: delete \"ChatGPT (no shader cache)\" yourself.");
        }
        return (true, "Created the desktop shortcut \"ChatGPT (no shader cache)\". Quit ChatGPT fully, then start it from the new shortcut.");
    }

    /// <summary>
    /// The first of <paramref name="desktop"/> and the folders above it, up to <paramref name="profile"/> (or the drive's
    /// root when the desktop is elsewhere), that is a link of any kind except OneDrive's cloud-files folders; null when
    /// there is none. A folder that cannot be checked counts as a link.
    /// </summary>
    private static string? FirstLink(string desktop, string profile)
    {
        for (string? folder = Path.GetFullPath(desktop); folder is not null; folder = Path.GetDirectoryName(folder))
        {
            using var h = NoFollow.Open(folder, dir: true, NoFollow.FILE_READ_ATTRIBUTES, NoFollow.ShareAll);
            if (h.IsInvalid || !RealOrCloud(h)) return folder;
            if (NoFollow.SamePath(folder, profile)) return null;
        }
        return null;
    }

    /// <summary>Whether the open folder is not a link, or is a cloud-files folder; false when its tag cannot be read.</summary>
    private static bool RealOrCloud(SafeFileHandle h)
    {
        try
        {
            uint tag = NoFollow.ReparseTag(h);
            return tag == 0 || NoFollow.IsCloudFilesTag(tag);
        }
        catch (Win32Exception) { return false; }
    }

    /// <summary>The shortcut's bytes, made by the shell (IShellLink) in a temporary file in the data folder and read back.</summary>
    private static byte[] Build(AppPaths paths, string target, string arguments, string workingFolder, string icon)
    {
        string temp = Path.Combine(paths.DataRoot, $"shortcut-{Guid.NewGuid():N}.tmp");
        var link = (IShellLinkW)new ShellLinkObject();
        try
        {
            link.SetPath(target);
            link.SetArguments(arguments);
            link.SetWorkingDirectory(workingFolder);
            link.SetIconLocation(icon, 0);
            link.SetDescription(Description);
            link.SetShowCmd(ShowMinimised);
            ((IPersistFile)link).Save(paths.Guard.CheckReal(temp), true);
            return File.ReadAllBytes(paths.Guard.CheckReal(temp));
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
            if (File.Exists(temp)) File.Delete(paths.Guard.CheckReal(temp));
        }
    }

    /// <summary>CLSID_ShellLink.</summary>
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkObject { }

    /// <summary>IShellLinkW (shobjidl_core.h), every method in vtable order; only the setters are used.</summary>
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int size, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
