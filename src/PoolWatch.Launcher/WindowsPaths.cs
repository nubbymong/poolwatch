using System.Security.Principal;
using Microsoft.Win32;

namespace PoolWatch.Launcher;

/// <summary>
/// Reads <see cref="SystemPaths"/> from Windows: the Windows and System32 folders from the kernel
/// (GetSystemWindowsDirectoryW, GetSystemDirectoryW); Program Files, its common files, ProgramData and the processor
/// facts from the machine's registry (HKLM, which only an administrator can change); the account's profile folder from
/// the machine's profile list (its ProfileImagePath, for the account the launcher's token is for); its AppData folders
/// from the account's own User Shell Folders settings, the settings Windows' known folders are read from; and its names
/// from Windows' account calls. Every value is read as written: %SystemDrive% and %SystemRoot% are filled in from the
/// Windows folder and %USERPROFILE% from that profile folder, and a value that names any other variable is left out.
/// Nothing is filled in from the environment the launcher was started with, and no shell call is made that would fill
/// one in from it (the shell's known-folder calls do: they fill in a setting's variables from the calling process's own
/// environment).
/// </summary>
internal static class WindowsPaths
{
    /// <summary>Where Windows keeps the machine's own environment variables, the processor facts among them.</summary>
    internal const string MachineEnvironmentKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    /// <summary>The machine's profile list: ProgramData and Public, and each account's profile folder under its SID.</summary>
    internal const string ProfileListKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    /// <summary>The account's own settings for its known folders (in HKCU), which its programs' AppData folders come from.</summary>
    internal const string UserShellFoldersKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";

    /// <summary>The AppData folders' settings there, and where Windows puts each when its setting is not there.</summary>
    internal const string RoamingSetting = "AppData", LocalSetting = "Local AppData";
    internal const string RoamingStandard = @"%USERPROFILE%\AppData\Roaming", LocalStandard = @"%USERPROFILE%\AppData\Local";

    /// <summary>Reads the folders and names (see the class). A value that cannot be read is empty.</summary>
    public static SystemPaths Read()
    {
        string windows = Native.SystemWindowsDirectory();
        string system32 = Environment.SystemDirectory;
        string drive = windows.Length >= 2 && windows[1] == ':' ? windows[..2] : "";
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var current = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion");
        using var profiles = hklm.OpenSubKey(ProfileListKey);
        using var machine = hklm.OpenSubKey(MachineEnvironmentKey);
        using var account = AccountSid() is { } sid ? profiles?.OpenSubKey(sid) : null;
        using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var shell = hkcu.OpenSubKey(UserShellFoldersKey);
        string Machine(RegistryKey? key, string name) => Expand(Raw(key, name), drive, windows);
        string Fact(string name) => Plain(Raw(machine, name));
        string profile = Profile(Raw(account, "ProfileImagePath"), drive, windows);
        string Name(Func<string> read)
        {
            try { return read(); }
            catch { return ""; }
        }
        return new SystemPaths(
            Windows: windows, System32: system32,
            ProgramFiles: Machine(current, "ProgramFilesDir"), ProgramFilesX86: Machine(current, "ProgramFilesDir (x86)"),
            CommonProgramFiles: Machine(current, "CommonFilesDir"), CommonProgramFilesX86: Machine(current, "CommonFilesDir (x86)"),
            ProgramData: Machine(profiles, "ProgramData"), Public: Machine(profiles, "Public"),
            UserProfile: profile,
            RoamingAppData: UserFolder(Raw(shell, RoamingSetting), RoamingStandard, profile, drive, windows),
            LocalAppData: UserFolder(Raw(shell, LocalSetting), LocalStandard, profile, drive, windows),
            UserName: Name(() => Environment.UserName), UserDomain: Name(() => Environment.UserDomainName), ComputerName: Name(() => Environment.MachineName),
            NumberOfProcessors: Fact("NUMBER_OF_PROCESSORS"), ProcessorArchitecture: Fact("PROCESSOR_ARCHITECTURE"),
            ProcessorIdentifier: Fact("PROCESSOR_IDENTIFIER"), ProcessorLevel: Fact("PROCESSOR_LEVEL"), ProcessorRevision: Fact("PROCESSOR_REVISION"));
    }

    /// <summary>A registry value as it is written (never with its variables filled in); null when it is not there or not text.</summary>
    private static string? Raw(RegistryKey? key, string name)
    {
        try { return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string; }
        catch { return null; }
    }

    /// <summary>The SID of the account the launcher's token is for (the same account elevated or not); null when it cannot be read.</summary>
    private static string? AccountSid()
    {
        try
        {
            using var me = WindowsIdentity.GetCurrent();
            return me.User?.Value;
        }
        catch { return null; }
    }

    /// <summary>
    /// Pure: the account's profile folder from its ProfileImagePath as written (<see cref="Expand"/>: only %SystemDrive%
    /// and %SystemRoot% filled in); empty when it is not a full local path.
    /// </summary>
    internal static string Profile(string? imagePath, string systemDrive, string windows)
    {
        string profile = Expand(imagePath, systemDrive, windows);
        return EnvironmentBlock.IsFullPath(profile) ? profile : "";
    }

    /// <summary>
    /// Pure: an AppData folder from its User Shell Folders <paramref name="setting"/> as written, or, when there is none,
    /// where Windows puts it (<paramref name="standard"/>). %USERPROFILE% is filled in with <paramref name="profile"/>, and
    /// %SystemDrive% and %SystemRoot% from the Windows folder (<see cref="Expand"/>). Empty when the profile folder is not
    /// known, when the setting names any other variable, and when the result is not a full local path: the folder is then
    /// left out, never guessed.
    /// </summary>
    internal static string UserFolder(string? setting, string standard, string profile, string systemDrive, string windows)
    {
        if (!EnvironmentBlock.IsFullPath(profile)) return "";
        string value = string.IsNullOrEmpty(setting) ? standard : setting;
        string folder = Expand(value.Replace("%USERPROFILE%", profile, StringComparison.OrdinalIgnoreCase), systemDrive, windows);
        return EnvironmentBlock.IsFullPath(folder) ? folder : "";
    }

    /// <summary>
    /// A machine value with %SystemDrive% and %SystemRoot% filled in from Windows' own folder; empty when it holds any
    /// other variable (nothing here is ever filled in from the environment).
    /// </summary>
    internal static string Expand(string? value, string systemDrive, string windows)
    {
        if (string.IsNullOrEmpty(value)) return "";
        string expanded = value.Replace("%SystemDrive%", systemDrive, StringComparison.OrdinalIgnoreCase)
                               .Replace("%SystemRoot%", windows, StringComparison.OrdinalIgnoreCase);
        return expanded.Contains('%') ? "" : expanded;
    }

    /// <summary>A machine value taken as it is, or empty when it refers to a variable (a processor fact never does).</summary>
    internal static string Plain(string? value) => string.IsNullOrEmpty(value) || value.Contains('%') ? "" : value;
}
