using System.Text;

namespace PoolWatch.Launcher;

/// <summary>
/// What the environment PoolWatch gets is built from, read by <see cref="WindowsPaths.Read"/> from Windows itself: the
/// Windows folder from the kernel; Program Files, ProgramData and the processor facts from the machine's registry (HKLM,
/// which only an administrator can change); the account's profile folder from the machine's profile list; its AppData
/// folders from the account's own User Shell Folders settings; and its names from Windows' account calls. Each value is
/// read as written, with only Windows' own values filled in (the Windows folder, and the profile folder for
/// %USERPROFILE%), so no value comes from the environment the launcher was started with. A value that could not be read,
/// or that names any other variable, is empty, and its variables are then left out.
/// </summary>
internal sealed record SystemPaths(
    string Windows, string System32, string ProgramFiles, string ProgramFilesX86, string CommonProgramFiles, string CommonProgramFilesX86,
    string ProgramData, string Public, string UserProfile, string RoamingAppData, string LocalAppData, string UserName, string UserDomain,
    string ComputerName, string NumberOfProcessors = "", string ProcessorArchitecture = "", string ProcessorIdentifier = "",
    string ProcessorLevel = "", string ProcessorRevision = "");

/// <summary>
/// The environment PoolWatch's program is started with: an allowlist, each variable filled in from <see
/// cref="SystemPaths"/> (Windows' folders, the machine's processor facts and the profile's folders), and the folder the
/// launcher was started in (<see cref="StartedIn"/>, its current folder, so PoolWatch reads relative paths on its command
/// line as the caller meant them). No variable of the
/// environment the launcher was started with is passed on: not .NET's start-up variables (<see cref="KnobPrefixes"/>,
/// which <see cref="Add"/> refuses whatever the allowlist names), not a Path with folders a standard user can write to (it
/// is Windows' own folders only), not SystemRoot, windir, ProgramFiles or ComSpec as the user's environment may have set
/// them.
/// </summary>
internal static class EnvironmentBlock
{
    /// <summary>
    /// The prefixes of the variables that make a .NET runtime load code or change how it runs: DOTNET_ and COMPlus_ (the
    /// runtime's settings, among them start-up hooks, another GC or JIT, crash dumps to a chosen path, diagnostic ports,
    /// extra dependencies and DOTNET_ROOT), CORECLR_ (the .NET profiler) and COR_ (the .NET Framework profiler).
    /// Compared without regard to case, as Windows compares variable names.
    /// </summary>
    public static readonly IReadOnlyList<string> KnobPrefixes = ["DOTNET_", "COMPlus_", "CORECLR_", "COR_"];

    /// <summary>
    /// The variable the launcher adds, so PoolWatch can tell it was started here (it then never hands its start over
    /// again, so the two can never start each other in a loop). It is not a secret: anything that can set it could set
    /// the rest of the environment, and PoolWatch still checks that environment itself.
    /// </summary>
    public const string Marker = "POOLWATCH_LAUNCHER";

    /// <summary>
    /// The variable through which the launcher tells PoolWatch the folder the launcher was started in: its own current
    /// folder, never a variable of its environment. The launcher starts PoolWatch in PoolWatch's own app folder
    /// (Native.Run), and PoolWatch reads a file named on its command line by a relative path against this folder
    /// (LaunchGate.StartedIn), as the caller meant it.
    /// </summary>
    public const string StartedIn = "POOLWATCH_STARTED_IN";

    /// <summary>Puts <paramref name="folder"/>, the folder the launcher was started in, into <paramref name="env"/> as <see cref="StartedIn"/>, through <see cref="Add"/>; left out when it is not a full path.</summary>
    public static void AddStartedIn(IDictionary<string, string> env, string? folder)
    {
        if (folder is not null && Path.IsPathFullyQualified(folder)) Add(env, StartedIn, folder);
    }

    /// <summary>Windows' default PATHEXT.</summary>
    public const string PathExt = ".COM;.EXE;.BAT;.CMD;.VBS;.VBE;.JS;.JSE;.WSF;.WSH;.MSC";

    public static bool IsKnob(string name)
    {
        foreach (var prefix in KnobPrefixes)
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Path: the Windows folders a standard user cannot write to, in Windows' own order.</summary>
    public static string SystemPath(SystemPaths sys) =>
        string.Join(';', sys.System32, sys.Windows, Path.Combine(sys.System32, "Wbem"), Path.Combine(sys.System32, @"WindowsPowerShell\v1.0") + @"\");

    /// <summary>
    /// Puts one variable of the allowlist into <paramref name="env"/>; refused, so left out: one of .NET's start-up
    /// variables (<see cref="IsKnob"/>), whatever the allowlist names; a name no environment block can hold (empty, or
    /// with an = or a NUL); and an empty value, or one with a NUL.
    /// </summary>
    internal static void Add(IDictionary<string, string> env, string name, string? value)
    {
        if (IsKnob(name) || name.Length == 0 || name.Contains('=') || name.Contains('\0')) return;
        if (string.IsNullOrEmpty(value) || value.Contains('\0')) return;
        env[name] = value;
    }

    /// <summary>
    /// The variables, sorted by name without regard to case (as Windows keeps an environment block), each put in through
    /// <see cref="Add"/>.
    /// </summary>
    public static SortedDictionary<string, string> Build(SystemPaths sys)
    {
        var env = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Set(string name, string? value) => Add(env, name, value);

        // Windows' own folders.
        if (IsFullPath(sys.Windows) && IsFullPath(sys.System32))
        {
            Set("SystemRoot", sys.Windows);
            Set("windir", sys.Windows);
            Set("SystemDrive", sys.Windows[..2]);
            Set("ComSpec", Path.Combine(sys.System32, "cmd.exe"));
            Set("Path", SystemPath(sys));
        }
        Set("PATHEXT", PathExt);
        Set("OS", "Windows_NT");
        Set("ProgramFiles", FullOrNull(sys.ProgramFiles));
        Set("ProgramW6432", FullOrNull(sys.ProgramFiles));
        Set("ProgramFiles(x86)", FullOrNull(sys.ProgramFilesX86));
        Set("CommonProgramFiles", FullOrNull(sys.CommonProgramFiles));
        Set("CommonProgramW6432", FullOrNull(sys.CommonProgramFiles));
        Set("CommonProgramFiles(x86)", FullOrNull(sys.CommonProgramFilesX86));
        Set("ProgramData", FullOrNull(sys.ProgramData));
        Set("ALLUSERSPROFILE", FullOrNull(sys.ProgramData));
        Set("PUBLIC", FullOrNull(sys.Public));
        Set("COMPUTERNAME", sys.ComputerName);

        // The machine's processor facts, which Windows tools expect (as Windows itself sets them, from the machine's registry).
        Set("NUMBER_OF_PROCESSORS", sys.NumberOfProcessors);
        Set("PROCESSOR_ARCHITECTURE", sys.ProcessorArchitecture);
        Set("PROCESSOR_IDENTIFIER", sys.ProcessorIdentifier);
        Set("PROCESSOR_LEVEL", sys.ProcessorLevel);
        Set("PROCESSOR_REVISION", sys.ProcessorRevision);

        // The account's profile.
        if (IsFullPath(sys.UserProfile))
        {
            Set("USERPROFILE", sys.UserProfile);
            Set("HOMEDRIVE", sys.UserProfile[..2]);
            Set("HOMEPATH", sys.UserProfile.Length > 2 ? sys.UserProfile[2..] : @"\");
        }
        Set("APPDATA", FullOrNull(sys.RoamingAppData));
        if (IsFullPath(sys.LocalAppData))
        {
            Set("LOCALAPPDATA", sys.LocalAppData);
            Set("TEMP", Path.Combine(sys.LocalAppData, "Temp"));
            Set("TMP", Path.Combine(sys.LocalAppData, "Temp"));
        }
        Set("USERNAME", sys.UserName);
        Set("USERDOMAIN", sys.UserDomain);

        Set(Marker, "1");
        return env;
    }

    /// <summary>
    /// The block CreateProcessW takes with CREATE_UNICODE_ENVIRONMENT: each NAME=value ended by a NUL, and one more NUL
    /// after the last.
    /// </summary>
    public static string Serialize(IEnumerable<KeyValuePair<string, string>> env)
    {
        var block = new StringBuilder();
        foreach (var (name, value) in env) block.Append(name).Append('=').Append(value).Append('\0');
        if (block.Length == 0) block.Append('\0');
        return block.Append('\0').ToString();
    }

    /// <summary>A local path with a drive letter (C:\...): the only form a Windows or profile folder may take here.</summary>
    internal static bool IsFullPath(string? path) =>
        path is { Length: >= 3 } && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';

    private static string? FullOrNull(string? path) => IsFullPath(path) ? path : null;
}
